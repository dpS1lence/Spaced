using Unity.Mathematics;

namespace Spaced
{
    public enum BoosterPhase
    {
        Coasting,
        Boostback,
        EntryBurn,
        Descent,
        LandingBurn,
        Landed,
        Lost,
    }

    /// <summary>
    /// First-stage return. Falcon 9: coast, three-engine entry burn, aerodynamic descent and a single-engine
    /// "hoverslam" onto a droneship (a Merlin at minimum throttle still out-lifts the empty booster, so it has to
    /// reach zero velocity exactly at the deck). Super Heavy: a boostback burn that turns it around towards the
    /// launch site, grid-fin steering on the way down and a three-engine landing burn that stops it in the tower's
    /// arms. Expended boosters just fall.
    /// Deterministic, so a copy can be flown ahead of time to predict where it comes down.
    /// </summary>
    public sealed class BoosterDescent
    {
        const double EntryBurnStartAltitudeM = 62000;
        const double EntryBurnTargetSpeed = 1050;
        const double LandingDragCoefficient = 1.1;
        const double LegsDeployAltitudeM = 2200;
        const double FlipSeconds = 8;
        /// <summary>Within this of the arms at this speed or less, the tower catches it.</summary>
        const double CatchRadiusM = 30, CatchSpeed = 9;

        public LaunchVehicle Vehicle;
        public BoosterPhase Phase { get; private set; } = BoosterPhase.Coasting;
        public double3 Position;
        public double3 Velocity;
        public double PropellantKg;
        public double JulianDate;
        public double Throttle;
        public int EnginesLit;
        public double3 ThrustDirection;
        public double TimeSinceSeparation;
        public bool GridFinsDeployed => TimeSinceSeparation > 12 || Vehicle.Kind == VehicleKind.Starship;
        public bool LegsDeployed;
        public double TouchdownSpeed;
        /// <summary>Where it came down, fixed to the rotating Earth (km).</summary>
        public double3 TouchdownEcefKm;
        /// <summary>Return-to-site target (the tower's arms), Earth-fixed km; only for <see cref="BoosterRecovery.TowerCatch"/>.</summary>
        public double3 TargetEcefKm;
        public bool Caught;
        /// <summary>Horizontal miss distance at touchdown (m), for tower catches.</summary>
        public double MissDistanceM;

        public double Mass => Vehicle.BoosterDryKg + PropellantKg;
        public double AltitudeM => math.length(Position) - EarthConstants.RadiusKm * 1000.0;
        public double3 AirVelocity => Velocity - Astro.EarthRotationVelocity(Position);
        public bool Finished => Phase == BoosterPhase.Landed || Phase == BoosterPhase.Lost;
        bool Returning => Vehicle.Recovery == BoosterRecovery.TowerCatch;

        /// <summary>Height above the ground at which it is "down": the deck, or the arms for a catch.</summary>
        double GroundHeightM => Returning ? Vehicle.CatchHeightM : 0;

        /// <summary>Events raised during the last <see cref="Step"/> (null when nothing happened).</summary>
        public string LastEvent { get; private set; }

        double _predictionTimer;
        double _alongTrackMissM = double.MaxValue;
        double3 _returnDirection;

        public BoosterDescent Clone() => (BoosterDescent)MemberwiseClone();

        double HorizontalSpeed(double3 up) { double3 air = AirVelocity; return math.length(air - math.dot(air, up) * up); }

        double DragArea => Vehicle.CrossSectionM2 + (GridFinsDeployed ? 4 * (Vehicle.Kind == VehicleKind.Starship ? 12.0 : 1.5) : 0);

        /// <summary>
        /// Altitude at which a retrograde burn at the given throttle, started now, brings the booster to rest
        /// (2-D: vertical and horizontal speed, with drag and propellant use).
        /// </summary>
        double PredictStopAltitude(double altitude, double verticalSpeed, double horizontalSpeed, double throttle)
        {
            double h = altitude, vr = verticalSpeed, vh = horizontalSpeed, mass = Mass;
            int engines = Vehicle.LandingEngines;
            const double dt = 0.1;
            for (int i = 0; i < 1500 && vr < 0; i++)
            {
                double speed = math.sqrt(vr * vr + vh * vh);
                double density = Atmosphere.Density(h / 1000.0);
                double decel = Vehicle.BoosterThrust(density, engines) * throttle / mass
                               + 0.5 * density * speed * speed * LandingDragCoefficient * DragArea / mass;
                vr += (-decel * vr / speed - 9.81) * dt;
                vh += -decel * vh / speed * dt;
                h += vr * dt;
                mass -= Vehicle.BoosterMassFlow(engines) * throttle * dt;
            }
            return h;
        }

        /// <summary>
        /// Where the booster will come down (Earth-fixed km) if it stops burning now and then flies its landing burn:
        /// drag all the way, and a vertical hoverslam that throttles to stop exactly at the arms. The
        /// landing burn keeps it aloft for a while, during which it keeps drifting sideways; that matters.
        /// </summary>
        double3 PredictImpactEcefKm()
        {
            double3 p = Position, v = Velocity;
            double jd = JulianDate, mass = Mass;
            bool burning = false;
            const double dt = 0.5;
            double thrust = Vehicle.BoosterThrust(1.0, Vehicle.LandingEngines);
            for (int i = 0; i < 2000; i++)
            {
                double r = math.length(p);
                double altitude = r - EarthConstants.RadiusKm * 1000.0;
                double3 up = p / r;
                double verticalSpeed = math.dot(v, up);
                if (i > 0 && (altitude <= GroundHeightM || burning && verticalSpeed >= 0)) break;
                double3 air = v - Astro.EarthRotationVelocity(p);
                double density = Atmosphere.Density(altitude / 1000.0);
                double3 a = -EarthConstants.Mu * 1e9 * p / (r * r * r)
                            - 0.5 * density * math.length(air) * LandingDragCoefficient * DragArea / mass * air;
                // Hoverslam: once stopping at the arms needs 85% of full thrust, throttle to exactly that.
                double needed = verticalSpeed < 0 ? verticalSpeed * verticalSpeed / (2.0 * math.max(altitude - GroundHeightM, 1.0)) + 9.81 : 0;
                if (!burning && needed * mass >= thrust * 0.85 && altitude < Vehicle.LandingBurnCeilingM + GroundHeightM) burning = true;
                if (burning) a += up * math.clamp(needed * mass, thrust * Vehicle.MinThrottle, thrust) / mass;
                v += a * dt;
                p += v * dt;
                jd += dt / MathConst.SecondsPerDay;
            }
            return Astro.EciToEcef(p / 1000.0, jd);
        }

        /// <summary>Horizontal offset (m, inertial) from the booster to the target, in the local horizontal plane.</summary>
        double3 HorizontalToTarget(double3 up)
        {
            double3 target = Astro.EcefToEci(TargetEcefKm, JulianDate) * 1000.0;
            double3 delta = target - Position;
            return delta - math.dot(delta, up) * up;
        }

        public void Step(double dt)
        {
            LastEvent = null;
            if (Finished) return;
            TimeSinceSeparation += dt;

            double altitude = AltitudeM;
            double3 up = math.normalize(Position);
            double3 air = AirVelocity;
            double airSpeed = math.length(air);
            double verticalSpeed = math.dot(Velocity, up);
            double density = Atmosphere.Density(altitude / 1000.0);
            double3 retrograde = airSpeed > 1e-3 ? -air / airSpeed : up;
            double3 thrustDirection = retrograde;
            double3 steering = double3.zero; // extra acceleration from grid fins / engine gimbal (tower catch)

            EnginesLit = 0;
            Throttle = 0;
            switch (Phase)
            {
                case BoosterPhase.Coasting:
                    if (Vehicle.Recovery == BoosterRecovery.Expended) break;
                    if (Returning && TimeSinceSeparation > FlipSeconds)
                    {
                        Phase = BoosterPhase.Boostback;
                        LastEvent = "Boostback burn startup";
                    }
                    else if (!Returning && verticalSpeed < 0 && altitude < EntryBurnStartAltitudeM)
                    {
                        Phase = BoosterPhase.EntryBurn;
                        LastEvent = "Entry burn startup";
                    }
                    break;

                case BoosterPhase.Boostback:
                {
                    // Burn back towards the launch site until the predicted impact point reaches the tower; the
                    // last few kilometres on three engines, re-predicting every step for precision.
                    _predictionTimer -= dt;
                    if (_predictionTimer <= 0)
                    {
                        double3 impact = Astro.EcefToEci(PredictImpactEcefKm(), JulianDate) * 1000.0;
                        double3 target = Astro.EcefToEci(TargetEcefKm, JulianDate) * 1000.0;
                        double3 error = target - impact;
                        error -= math.dot(error, up) * up;
                        if (math.lengthsq(_returnDirection) < 0.5) _returnDirection = math.normalizesafe(error, up);
                        _alongTrackMissM = math.dot(error, _returnDirection);
                        _boostbackDirection = math.normalize(math.normalizesafe(error, _returnDirection) + up * 0.12);
                        _predictionTimer = _alongTrackMissM > 40000 ? 0.5 : 0;
                    }
                    if (_alongTrackMissM < 150 || PropellantKg < Vehicle.LandingPropellantKg + 8000)
                    {
                        Phase = BoosterPhase.Descent;
                        LastEvent = "Boostback burn shutdown";
                        break;
                    }
                    EnginesLit = _alongTrackMissM > 15000 ? Vehicle.BoostbackEngines : Vehicle.LandingEngines;
                    Throttle = 1;
                    thrustDirection = _boostbackDirection;
                    break;
                }

                case BoosterPhase.EntryBurn:
                    EnginesLit = Vehicle.EntryBurnEngines;
                    Throttle = 1;
                    if (airSpeed < EntryBurnTargetSpeed || PropellantKg < Vehicle.LandingPropellantKg)
                    {
                        Phase = BoosterPhase.Descent;
                        LastEvent = "Entry burn shutdown";
                    }
                    break;

                case BoosterPhase.Descent:
                    if (!Returning && !LegsDeployed && altitude < LegsDeployAltitudeM) LegsDeployed = true;
                    if (Returning) steering = CatchSteering(up, altitude, verticalSpeed, density, airSpeed, false);
                    // Hoverslam: light the engines when a burn at 85% thrust would stop just above the deck / arms.
                    if (verticalSpeed < 0 && altitude < Vehicle.LandingBurnCeilingM + GroundHeightM
                        && PredictStopAltitude(altitude, verticalSpeed, Returning ? 0 : HorizontalSpeed(up), 0.85) <= GroundHeightM + 15)
                    {
                        Phase = BoosterPhase.LandingBurn;
                        LastEvent = "Landing burn startup";
                    }
                    break;

                case BoosterPhase.LandingBurn:
                    if (!Returning) LegsDeployed = true;
                    EnginesLit = Vehicle.LandingEngines;
                    // Pick the throttle that stops the booster two metres above the deck / arms. A tower catch burns
                    // straight up and leaves the sideways moves to the steering (retrograde thrust would fight it).
                    double lo = Vehicle.MinThrottle, hi = 1.0, horizontal = Returning ? 0 : HorizontalSpeed(up);
                    for (int k = 0; k < 14; k++)
                    {
                        double mid = 0.5 * (lo + hi);
                        if (PredictStopAltitude(altitude, verticalSpeed, horizontal, mid) > GroundHeightM + 2.0) hi = mid; else lo = mid;
                    }
                    Throttle = hi;
                    if (Returning)
                    {
                        // Tilt the thrust to walk sideways onto the arms (up to ~27° of gimbal and body tilt).
                        double thrustAcceleration = Vehicle.BoosterThrust(density, EnginesLit) * Throttle / Mass;
                        double3 sideways = CatchSteering(up, altitude, verticalSpeed, density, airSpeed, true);
                        double limit = thrustAcceleration * 0.5;
                        if (math.length(sideways) > limit) sideways *= limit / math.length(sideways);
                        thrustDirection = math.normalize(up * thrustAcceleration + sideways);
                        // Even at minimum throttle the engines out-lift the empty booster: never climb.
                        if (verticalSpeed > 0) EnginesLit = 0;
                    }
                    break;
            }

            if (PropellantKg <= 0)
            {
                EnginesLit = 0;
                Throttle = 0;
            }

            ThrustDirection = thrustDirection;
            double3 acceleration = -EarthConstants.Mu * 1e9 * Position / math.pow(math.lengthsq(Position), 1.5);
            if (EnginesLit > 0)
            {
                double thrust = Vehicle.BoosterThrust(density, EnginesLit) * Throttle;
                acceleration += thrustDirection * thrust / Mass;
                PropellantKg = math.max(0, PropellantKg - Vehicle.BoosterMassFlow(EnginesLit) * Throttle * dt);
            }

            // Falling engines-first, with grid fins out.
            acceleration += -0.5 * density * airSpeed * LandingDragCoefficient * DragArea / Mass * air + steering;

            Velocity += acceleration * dt;
            Position += Velocity * dt;
            // The clock moves with the position (the target is Earth-fixed: a stale time puts it ~46 m off).
            JulianDate += dt / MathConst.SecondsPerDay;

            // The arms close around the booster as it settles between them.
            bool inArms = Returning && Phase == BoosterPhase.LandingBurn && AltitudeM <= GroundHeightM + 8 && math.length(AirVelocity) < 2.5;
            if (AltitudeM <= GroundHeightM || inArms)
            {
                TouchdownSpeed = math.length(AirVelocity);
                if (Returning)
                {
                    MissDistanceM = math.length(HorizontalToTarget(math.normalize(Position)));
                    Caught = TouchdownSpeed < CatchSpeed && MissDistanceM < CatchRadiusM;
                    Phase = Caught ? BoosterPhase.Landed : BoosterPhase.Lost;
                    LastEvent = Caught ? "Booster caught by the tower" : "Booster lost: missed the tower";
                    if (Caught) Position = Astro.EcefToEci(TargetEcefKm, JulianDate) * 1000.0;
                }
                else
                {
                    Phase = Vehicle.Recovery != BoosterRecovery.Expended && TouchdownSpeed < 8 ? BoosterPhase.Landed : BoosterPhase.Lost;
                    LastEvent = Phase == BoosterPhase.Landed ? "Touchdown — booster landed"
                        : Vehicle.Recovery == BoosterRecovery.Expended ? "Booster impact (expended)" : "Booster lost on landing";
                }
                if (!Caught) Position = math.normalize(Position) * (EarthConstants.RadiusKm * 1000.0 + GroundHeightM);
                Velocity = Astro.EarthRotationVelocity(Position);
                TouchdownEcefKm = Astro.EciToEcef(Position / 1000.0, JulianDate);
            }
        }

        double3 _boostbackDirection;

        /// <summary>
        /// Sideways acceleration that walks the booster onto the tower: grid-fin lift in thick air, thrust vectoring
        /// during the landing burn (the caller limits that one). A critically damped spring towards the arms whose
        /// time constant follows the time left, so it settles before touchdown without overshooting.
        /// </summary>
        double3 CatchSteering(double3 up, double altitude, double verticalSpeed, double density, double airSpeed, bool engines)
        {
            double3 offset = HorizontalToTarget(up);
            double3 air = AirVelocity;
            double3 horizontalVelocity = air - math.dot(air, up) * up;
            // Time to go: constant speed while falling, constant deceleration once the engines are lit.
            double height = math.max(altitude - GroundHeightM, 0.0);
            double timeToGo = math.clamp((engines ? 2.0 : 1.0) * height / math.max(-verticalSpeed, 1.0), 1.0, 300.0);
            double tau = math.max(timeToGo / (engines ? 4.0 : 2.5), engines ? 1.0 : 4.0);
            double3 command = offset / (tau * tau) - 2.0 * horizontalVelocity / tau;
            if (engines) return command;
            double dynamicPressure = 0.5 * density * airSpeed * airSpeed;
            double limit = math.min(dynamicPressure * DragArea * 0.35 / Mass, 6.0);
            double magnitude = math.length(command);
            return magnitude > limit && magnitude > 0 ? command * (limit / magnitude) : command;
        }
    }
}
