using System.Collections.Generic;
using Unity.Mathematics;

namespace Spaced
{
    public enum FlightPhase
    {
        Countdown,
        FirstStage,
        StageSeparation,
        SecondStage,
        Coast,
        Circularization,
        Complete,
        Failed,
    }

    public struct FlightEvent
    {
        public double MissionTime;
        public string Text;
    }

    /// <summary>A jettisoned fairing half falling back to the sea.</summary>
    public sealed class FairingHalf
    {
        public double3 Position;
        public double3 Velocity;
        public double3 SpinAxis;
        public double Age;
        public bool Splashed;
    }

    /// <summary>
    /// Physically integrated two-stage flight (Falcon 9 or Starship, see <see cref="LaunchVehicle"/>) in the
    /// inertial frame (metres, seconds), launched from a pad that rotates with the Earth. Includes gravity, thrust
    /// that varies with ambient pressure, Mach-dependent drag and a throttle bucket at max-Q.
    /// <para>First stage: vertical rise, pitch kick, then a zero-angle-of-attack gravity turn.</para>
    /// <para>Second stage: closed-loop altitude/vertical-speed guidance that builds horizontal speed at the
    /// insertion altitude and cuts off when apoapsis reaches the target. Higher targets coast to apoapsis
    /// and circularise; very high ones (GTO-like) release the payload in the transfer orbit.</para>
    /// </summary>
    public sealed class RocketFlight
    {
        public const double StepSeconds = 0.1;
        public const double CountdownSeconds = 60;
        /// <summary>Above this target altitude the satellites raise their own orbit from a transfer orbit.</summary>
        public const double PayloadSelfRaiseAboveKm = 2000;
        public const double DirectInsertionMaxKm = 220;
        /// <summary>At most 1,200 simulated seconds per <see cref="Advance"/> call.</summary>
        public const int MaxStepsPerAdvance = 12000;

        const double HoldOmega = 0.012, MaxSinPitch = 0.7;

        public readonly LaunchVehicle Vehicle;
        public readonly LaunchSite Site;
        public readonly double LiftoffJd;
        public readonly double PayloadKg;
        public readonly double TargetAltitudeKm;
        public readonly double InsertionAltitudeKm;
        public readonly double3 PlaneNormal;
        public readonly bool PayloadRaisesOrbit;

        /// <summary>
        /// Pitch-kick angle. Lighter payloads give a higher thrust-to-weight ratio and need a harder turn,
        /// otherwise the trajectory lofts and wastes second-stage propellant.
        /// </summary>
        public double PitchKickDeg => math.lerp(Vehicle.KickLightDeg, Vehicle.KickHeavyDeg, math.saturate(PayloadKg / Vehicle.HeavyPayloadKg));

        public FlightPhase Phase { get; private set; } = FlightPhase.Countdown;
        public double MissionTime { get; private set; } = -CountdownSeconds;
        public double JulianDate => LiftoffJd + MissionTime / MathConst.SecondsPerDay;

        /// <summary>Upper stack (whole vehicle before staging) position and velocity, m and m/s.</summary>
        public double3 Position;
        public double3 Velocity;
        public double BoosterPropellantKg;
        public double SecondPropellantKg;
        public bool BoosterAttached = true;
        public bool FairingAttached;
        public bool PayloadAttached = true;
        public double Throttle;
        public int EnginesLit;
        public double3 ThrustDirection;
        public double DynamicPressure;
        public double MaxDynamicPressure;

        public BoosterDescent Booster;
        public bool HasDroneship;
        public double3 DroneshipEcefKm;
        public readonly List<FairingHalf> Fairings = new List<FairingHalf>();
        public readonly List<FlightEvent> Events = new List<FlightEvent>();

        /// <summary>True once the payload has been released (consumed by the launch manager).</summary>
        public bool PayloadReleased { get; private set; }

        public double AltitudeKm => (math.length(Position) - EarthConstants.RadiusKm * 1000.0) / 1000.0;
        public double3 AirVelocity => Velocity - Astro.EarthRotationVelocity(Position);
        public double DownrangeKm => math.length(Astro.EciToEcef(Position / 1000.0, JulianDate) - PadEcefKm(0)) ;
        public bool InFlight => Phase != FlightPhase.Countdown && Phase != FlightPhase.Complete && Phase != FlightPhase.Failed;

        public bool Finished =>
            (Phase == FlightPhase.Complete || Phase == FlightPhase.Failed)
            && (Booster == null || Booster.Finished)
            && Fairings.TrueForAll(f => f.Splashed);

        double _accumulator;
        double _separationTime;
        bool _maxQPassed;
        double _previousQ;
        double _circularizationStartTime = double.MaxValue;

        public RocketFlight(LaunchVehicle vehicle, LaunchSite site, double liftoffJd, double payloadKg, double targetAltitudeKm, double3 planeNormal)
        {
            Vehicle = vehicle;
            BoosterPropellantKg = vehicle.BoosterPropKg;
            SecondPropellantKg = vehicle.SecondPropKg;
            FairingAttached = vehicle.HasFairing;
            Site = site;
            LiftoffJd = liftoffJd;
            PayloadKg = payloadKg;
            TargetAltitudeKm = targetAltitudeKm;
            // Very low targets are injected directly; the rest go via a 220 km perigee, coast and circularise.
            InsertionAltitudeKm = math.min(targetAltitudeKm, DirectInsertionMaxKm);
            PlaneNormal = math.normalize(planeNormal);
            PayloadRaisesOrbit = targetAltitudeKm > PayloadSelfRaiseAboveKm;
            PinToPad();
            ThrustDirection = math.normalize(Position);
        }

        public double3 PadEcefKm(double heightM) => Astro.GeoToEcef(Site.LatitudeDeg, Site.LongitudeDeg, heightM / 1000.0);

        public double UpperMass => Vehicle.SecondDryKg + SecondPropellantKg + (PayloadAttached ? PayloadKg : 0) + (FairingAttached ? Vehicle.FairingKg : 0);
        double TotalMass => UpperMass + (BoosterAttached ? Vehicle.BoosterDryKg + BoosterPropellantKg : 0);

        /// <summary>Sim time accumulated but not yet stepped (0 … 0.1 s). Renderers extrapolate by it.</summary>
        public double RenderLead => _accumulator;

        /// <summary>Upper stack position extrapolated to the current instant, so motion is smooth between steps.</summary>
        public double3 RenderPosition => Position + Velocity * _accumulator;

        public double3 Extrapolate(double3 position, double3 velocity) => position + velocity * _accumulator;

        public void Advance(double dt)
        {
            if (dt <= 0 || Finished) return;
            _accumulator += dt;
            int steps = 0;
            while (_accumulator >= StepSeconds)
            {
                StepOnce(StepSeconds);
                _accumulator -= StepSeconds;
                if (Finished) { _accumulator = 0; break; }
                // Bound the work per frame. Past this the flight simply runs behind the clock (deployment
                // catches the satellites up), instead of building a backlog that grows every frame.
                if (++steps >= MaxStepsPerAdvance) { _accumulator = 0; break; }
            }
        }

        void Raise(string text) => Events.Add(new FlightEvent { MissionTime = MissionTime, Text = text });

        void PinToPad()
        {
            Position = Astro.EcefToEci(PadEcefKm(Site.MountHeightM), JulianDate) * 1000.0;
            Velocity = Astro.EarthRotationVelocity(Position);
        }

        void StepOnce(double h)
        {
            MissionTime += h;
            StepDebris(h);

            switch (Phase)
            {
                case FlightPhase.Countdown:
                    PinToPad();
                    // Engines light at T-3 s while the hold-down clamps keep the vehicle on the pad.
                    if (MissionTime >= -3)
                    {
                        if (EnginesLit == 0) Raise("Engine ignition");
                        EnginesLit = Vehicle.BoosterEngines;
                        Throttle = math.saturate((MissionTime + 3) / 2.0);
                        ThrustDirection = math.normalize(Position);
                    }
                    if (MissionTime >= 0)
                    {
                        Phase = FlightPhase.FirstStage;
                        Raise("Liftoff");
                    }
                    break;
                case FlightPhase.FirstStage: FlyFirstStage(h); break;
                case FlightPhase.StageSeparation: FlyStageSeparation(h); break;
                case FlightPhase.SecondStage: FlySecondStage(h); break;
                case FlightPhase.Coast: FlyCoast(h); break;
                case FlightPhase.Circularization: FlyCircularization(h); break;
                default: Integrate(double3.zero, 0, h); break;
            }
        }

        /// <summary>Horizontal direction to steer in: towards the target plane, corrected for Earth's rotation.</summary>
        double3 AirRelativeHeading(double3 up)
        {
            double3 prograde = math.normalize(math.cross(PlaneNormal, up));
            double3 airTarget = prograde * 7800.0 - Astro.EarthRotationVelocity(Position);
            double3 horizontal = airTarget - math.dot(airTarget, up) * up;
            return math.normalize(horizontal);
        }

        void FlyFirstStage(double h)
        {
            double3 up = math.normalize(Position);
            double3 air = AirVelocity;
            double airSpeed = math.length(air);
            double density = Atmosphere.Density(AltitudeKm);
            DynamicPressure = 0.5 * density * airSpeed * airSpeed;
            TrackMaxQ();

            double pitchFromVertical;
            if (MissionTime < Vehicle.PitchKickStart) pitchFromVertical = 0;
            else if (MissionTime < Vehicle.PitchKickStart + Vehicle.PitchKickDuration)
                pitchFromVertical = PitchKickDeg * MathConst.Deg2Rad * (MissionTime - Vehicle.PitchKickStart) / Vehicle.PitchKickDuration;
            else
            {
                // Gravity turn: point along the air-relative velocity so the angle of attack stays zero.
                double flightAngle = math.acos(math.clamp(math.dot(air / math.max(airSpeed, 1e-3), up), -1, 1));
                pitchFromVertical = math.max(flightAngle, PitchKickDeg * MathConst.Deg2Rad);
            }

            double3 heading = AirRelativeHeading(up);
            ThrustDirection = math.normalize(math.cos(pitchFromVertical) * up + math.sin(pitchFromVertical) * heading);

            // Throttle bucket through max-Q, then cap acceleration near burnout.
            Throttle = DynamicPressure > Vehicle.MaxQLimitPa ? math.clamp(Vehicle.MaxQLimitPa / DynamicPressure, 0.6, 1.0) : 1.0;
            int engines = Vehicle.BoosterEngines;
            double thrust = Vehicle.BoosterThrust(density, engines) * Throttle;
            double maxAcceleration = Vehicle.MaxAscentG * 9.81;
            if (thrust / TotalMass > maxAcceleration) Throttle *= maxAcceleration * TotalMass / thrust;
            thrust = Vehicle.BoosterThrust(density, engines) * Throttle;
            EnginesLit = engines;

            BoosterPropellantKg -= Vehicle.BoosterMassFlow(engines) * Throttle * h;
            Integrate(ThrustDirection * thrust, LaunchVehicle.AscentDragCoefficient(airSpeed / 320.0), h);

            if (BoosterPropellantKg <= Vehicle.LandingReserveKg) Separate();
        }

        void TrackMaxQ()
        {
            MaxDynamicPressure = math.max(MaxDynamicPressure, DynamicPressure);
            if (!_maxQPassed && DynamicPressure < _previousQ && MaxDynamicPressure > 10000)
            {
                _maxQPassed = true;
                Raise($"Max-Q ({MaxDynamicPressure / 1000:0.0} kPa)");
            }
            _previousQ = DynamicPressure;
        }

        void Separate()
        {
            Raise("Main engine cutoff (MECO)");
            BoosterAttached = false;
            EnginesLit = 0;
            Throttle = 0;
            _separationTime = MissionTime;
            Phase = FlightPhase.StageSeparation;

            Booster = new BoosterDescent
            {
                Vehicle = Vehicle,
                Position = Position,
                Velocity = Velocity - ThrustDirection * 1.0,
                PropellantKg = BoosterPropellantKg,
                JulianDate = JulianDate,
                ThrustDirection = ThrustDirection,
                TargetEcefKm = Astro.GeoToEcef(Site.LatitudeDeg, Site.LongitudeDeg, Vehicle.CatchHeightM / 1000.0),
            };
            Velocity += ThrustDirection * 1.0;

            if (Vehicle.Recovery == BoosterRecovery.Droneship)
            {
                // Fly a copy of the booster ahead of time to find where it comes down; park the droneship there.
                var prediction = Booster.Clone();
                for (int i = 0; i < 30000 && !prediction.Finished; i++) prediction.Step(StepSeconds);
                HasDroneship = prediction.Phase == BoosterPhase.Landed;
                DroneshipEcefKm = prediction.TouchdownEcefKm;
            }
            Raise(Vehicle.Kind == VehicleKind.Starship ? "Hot staging" : "Stage separation");
        }

        void FlyStageSeparation(double h)
        {
            Integrate(double3.zero, 0.3, h);
            if (MissionTime - _separationTime >= Vehicle.StageCoastSeconds)
            {
                Phase = FlightPhase.SecondStage;
                Raise(Vehicle.Kind == VehicleKind.Starship ? "Ship engine start (SES-1)" : "Second stage engine start (SES-1)");
            }
        }

        void FlySecondStage(double h)
        {
            double3 up = math.normalize(Position);
            double r = math.length(Position);
            double verticalSpeed = math.dot(Velocity, up);
            double3 horizontalVelocity = Velocity - verticalSpeed * up;
            double horizontalSpeed = math.length(horizontalVelocity);
            double gravity = EarthConstants.Mu * 1e9 / (r * r);
            double mass = UpperMass;
            Throttle = UpperStageThrottle(mass);
            double thrustAccel = Vehicle.SecondThrust * Throttle / mass;

            // Second-order altitude hold at the insertion altitude, limited so the stage lofts gently.
            double targetRadius = (EarthConstants.RadiusKm + InsertionAltitudeKm) * 1000.0;
            double omega = HoldOmega;
            double commanded = math.clamp(omega * omega * (targetRadius - r) - 2.0 * omega * verticalSpeed, -6.0, 6.0);
            double sinPitch = math.clamp((commanded + gravity - horizontalSpeed * horizontalSpeed / r) / thrustAccel, -0.35, MaxSinPitch);

            ThrustDirection = math.normalize(sinPitch * up + math.sqrt(1 - sinPitch * sinPitch) * InPlaneHeading(up));
            EnginesLit = Vehicle.SecondEngines;
            SecondPropellantKg -= Vehicle.SecondMassFlow * Throttle * h;

            if (FairingAttached && AltitudeKm * 1000.0 > Vehicle.FairingSeparationAltitudeM) JettisonFairing(up);

            Integrate(ThrustDirection * Vehicle.SecondThrust * Throttle, 0.3, h);

            OrbitShape(out double periapsisKm, out double apoapsisKm);
            bool direct = TargetAltitudeKm - InsertionAltitudeKm < 10;
            if (apoapsisKm >= TargetAltitudeKm && periapsisKm >= (direct ? TargetAltitudeKm - 25 : math.min(InsertionAltitudeKm - 40, 150)))
            {
                Cutoff("Second stage engine cutoff (SECO-1)");
                if (PayloadRaisesOrbit || direct) Complete();
                else Phase = FlightPhase.Coast;
            }
            else if (SecondPropellantKg <= 0)
            {
                Cutoff("Second stage propellant depleted");
                if (periapsisKm > 140) Complete();
                else Fail("Vehicle did not reach orbit");
            }
        }

        /// <summary>Throttles the upper stage back so it doesn't exceed its acceleration limit as the tanks empty.</summary>
        double UpperStageThrottle(double mass) =>
            math.clamp(Vehicle.MaxUpperStageG * 9.81 * mass / Vehicle.SecondThrust, Vehicle.MinThrottle, 1.0);

        double3 InPlaneHeading(double3 up)
        {
            double3 prograde = math.normalize(math.cross(PlaneNormal, up));
            // Yaw against any velocity out of the target plane.
            double outOfPlane = math.dot(Velocity, PlaneNormal);
            return math.normalize(prograde - math.clamp(outOfPlane / 300.0, -0.25, 0.25) * PlaneNormal);
        }

        void FlyCoast(double h)
        {
            Integrate(double3.zero, 0.3, h);
            if (_circularizationStartTime == double.MaxValue) PlanCircularization();
            if (MissionTime >= _circularizationStartTime)
            {
                Phase = FlightPhase.Circularization;
                Raise("Second stage engine start (SES-2)");
            }
        }

        void PlanCircularization()
        {
            if (!OrbitMath.FromStateVector(Position / 1000.0, Velocity / 1000.0, out var el)) return;
            double meanToApo = OrbitMath.WrapAngle(math.PI_DBL - el.M);
            double timeToApo = meanToApo / el.N;
            double apoRadius = el.A * (1 + el.E) * 1000.0;
            double apoSpeed = math.sqrt(EarthConstants.Mu * 1e9 * (2 / apoRadius - 1 / (el.A * 1000.0)));
            double deltaV = math.sqrt(EarthConstants.Mu * 1e9 / apoRadius) - apoSpeed;
            double finalMass = UpperMass * math.exp(-deltaV / (Vehicle.SecondIsp * LaunchVehicle.G0));
            double burnTime = (UpperMass - finalMass) / (Vehicle.SecondMassFlow * UpperStageThrottle(UpperMass));
            _circularizationStartTime = MissionTime + timeToApo - burnTime / 2;
        }

        void FlyCircularization(double h)
        {
            double3 up = math.normalize(Position);
            double verticalSpeed = math.dot(Velocity, up);
            double sinPitch = math.clamp(-verticalSpeed / 150.0, -0.3, 0.3);
            ThrustDirection = math.normalize(sinPitch * up + math.sqrt(1 - sinPitch * sinPitch) * InPlaneHeading(up));
            Throttle = UpperStageThrottle(UpperMass);
            EnginesLit = Vehicle.SecondEngines;
            SecondPropellantKg -= Vehicle.SecondMassFlow * Throttle * h;
            Integrate(ThrustDirection * Vehicle.SecondThrust * Throttle, 0, h);

            OrbitShape(out double periapsisKm, out double apoapsisKm);
            if (periapsisKm >= TargetAltitudeKm - 3 || apoapsisKm > TargetAltitudeKm + 40 || SecondPropellantKg <= 0)
            {
                Cutoff("Second stage engine cutoff (SECO-2)");
                Complete();
            }
        }

        void Cutoff(string text)
        {
            Throttle = 0;
            EnginesLit = 0;
            SecondPropellantKg = math.max(SecondPropellantKg, 0);
            Raise(text);
        }

        void Complete()
        {
            Phase = FlightPhase.Complete;
            if (FairingAttached) JettisonFairing(math.normalize(Position));
            PayloadAttached = false;
            PayloadReleased = true;
            Raise("Payload deployment");
        }

        void Fail(string reason)
        {
            Phase = FlightPhase.Failed;
            Raise(reason);
        }

        void JettisonFairing(double3 up)
        {
            FairingAttached = false;
            double3 side = math.normalize(math.cross(up, ThrustDirection + new double3(1e-3, 0, 0)));
            foreach (double sign in new[] { 1.0, -1.0 })
            {
                Fairings.Add(new FairingHalf
                {
                    Position = Position + ThrustDirection * 62 + side * sign * 2.6,
                    Velocity = Velocity + side * sign * 4.0,
                    SpinAxis = math.normalize(math.cross(ThrustDirection, side) * sign + side * 0.3),
                });
            }
            Raise("Fairing separation");
        }

        /// <summary>Semi-implicit Euler step under gravity, thrust (N) and drag.</summary>
        void Integrate(double3 thrust, double dragCoefficient, double h)
        {
            double mass = TotalMass;
            double3 acceleration = -EarthConstants.Mu * 1e9 * Position / math.pow(math.lengthsq(Position), 1.5) + thrust / mass;
            if (dragCoefficient > 0 && AltitudeKm < 200)
            {
                double3 air = AirVelocity;
                double density = Atmosphere.Density(AltitudeKm);
                acceleration -= 0.5 * density * math.length(air) * dragCoefficient * Vehicle.CrossSectionM2 / mass * air;
            }
            Velocity += acceleration * h;
            Position += Velocity * h;
        }

        void StepDebris(double h)
        {
            if (Booster != null && !Booster.Finished)
            {
                Booster.Step(h);
                if (Booster.LastEvent != null) Raise(Booster.LastEvent);
            }

            foreach (var half in Fairings)
            {
                if (half.Splashed) continue;
                half.Age += h;
                double r = math.length(half.Position);
                double altitudeKm = r / 1000.0 - EarthConstants.RadiusKm;
                double3 air = half.Velocity - Astro.EarthRotationVelocity(half.Position);
                double density = Atmosphere.Density(altitudeKm);
                double3 acceleration = -EarthConstants.Mu * 1e9 * half.Position / (r * r * r)
                                       - 0.5 * density * math.length(air) * 1.4 * 28.0 / 950.0 * air;
                half.Velocity += acceleration * h;
                half.Position += half.Velocity * h;
                if (altitudeKm <= 0 || half.Age > 3600) half.Splashed = true;
            }
        }

        /// <summary>Two-body periapsis and apoapsis altitudes (km) of the current upper-stage state.</summary>
        public void OrbitShape(out double periapsisKm, out double apoapsisKm)
        {
            double mu = EarthConstants.Mu * 1e9;
            double r = math.length(Position);
            double energy = 0.5 * math.lengthsq(Velocity) - mu / r;
            double hMag = math.length(math.cross(Position, Velocity));
            if (energy >= 0)
            {
                periapsisKm = -1;
                apoapsisKm = double.PositiveInfinity;
                return;
            }
            double a = -mu / (2 * energy);
            double e = math.sqrt(math.max(0, 1 - hMag * hMag / (mu * a)));
            periapsisKm = (a * (1 - e)) / 1000.0 - EarthConstants.RadiusKm;
            apoapsisKm = (a * (1 + e)) / 1000.0 - EarthConstants.RadiusKm;
        }
    }
}
