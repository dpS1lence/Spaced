using Unity.Mathematics;

namespace Spaced
{
    public enum BoosterPhase
    {
        Coasting,
        EntryBurn,
        Descent,
        LandingBurn,
        Landed,
        Lost,
    }

    /// <summary>
    /// First-stage return to a droneship: coast, three-engine entry burn, aerodynamic descent and a
    /// single-engine "hoverslam" landing burn (a Merlin at minimum throttle still out-lifts the empty
    /// booster, so it has to reach zero velocity exactly at the deck).
    /// Deterministic, so a copy can be flown ahead of time to predict where to park the droneship.
    /// </summary>
    public sealed class BoosterDescent
    {
        const double EntryBurnStartAltitudeM = 62000;
        const double EntryBurnTargetSpeed = 1050;
        const double LandingDragCoefficient = 1.1;
        const double LegsDeployAltitudeM = 2200;
        const double LandingBurnCeilingM = 6000;
        /// <summary>Kept for the landing burn, whatever the entry burn achieved.</summary>
        const double LandingPropellantKg = 11000;

        public BoosterPhase Phase { get; private set; } = BoosterPhase.Coasting;
        public double3 Position;
        public double3 Velocity;
        public double PropellantKg;
        public double JulianDate;
        public double Throttle;
        public int EnginesLit;
        public double3 ThrustDirection;
        public double TimeSinceSeparation;
        public bool GridFinsDeployed => TimeSinceSeparation > 12;
        public bool LegsDeployed;
        public double TouchdownSpeed;
        /// <summary>Where it came down, fixed to the rotating Earth (km).</summary>
        public double3 TouchdownEcefKm;

        public double Mass => Falcon9.BoosterDryKg + PropellantKg;
        public double AltitudeM => math.length(Position) - EarthConstants.RadiusKm * 1000.0;
        public double3 AirVelocity => Velocity - Astro.EarthRotationVelocity(Position);
        public bool Finished => Phase == BoosterPhase.Landed || Phase == BoosterPhase.Lost;

        /// <summary>Events raised during the last <see cref="Step"/> (null when nothing happened).</summary>
        public string LastEvent { get; private set; }

        public BoosterDescent Clone() => (BoosterDescent)MemberwiseClone();

        double HorizontalSpeed(double3 up) { double3 air = AirVelocity; return math.length(air - math.dot(air, up) * up); }

        double DragArea => Falcon9.CrossSectionM2 + (GridFinsDeployed ? 4 * 1.5 : 0);

        /// <summary>
        /// Altitude at which a retrograde burn at the given throttle, started now, brings the booster to rest
        /// (2-D: vertical and horizontal speed, with drag and propellant use).
        /// </summary>
        double PredictStopAltitude(double altitude, double verticalSpeed, double horizontalSpeed, double throttle)
        {
            double h = altitude, vr = verticalSpeed, vh = horizontalSpeed, mass = Mass;
            const double dt = 0.1;
            for (int i = 0; i < 1500 && vr < 0; i++)
            {
                double speed = math.sqrt(vr * vr + vh * vh);
                double density = Atmosphere.Density(h / 1000.0);
                double decel = Falcon9.BoosterThrust(density, 1) * throttle / mass
                               + 0.5 * density * speed * speed * LandingDragCoefficient * DragArea / mass;
                vr += (-decel * vr / speed - 9.81) * dt;
                vh += -decel * vh / speed * dt;
                h += vr * dt;
                mass -= Falcon9.BoosterMassFlow(1) * throttle * dt;
            }
            return h;
        }

        public void Step(double dt)
        {
            LastEvent = null;
            if (Finished) return;
            TimeSinceSeparation += dt;
            JulianDate += dt / MathConst.SecondsPerDay;

            double altitude = AltitudeM;
            double3 up = math.normalize(Position);
            double3 air = AirVelocity;
            double airSpeed = math.length(air);
            double verticalSpeed = math.dot(Velocity, up);
            double density = Atmosphere.Density(altitude / 1000.0);
            double3 retrograde = airSpeed > 1e-3 ? -air / airSpeed : up;

            EnginesLit = 0;
            Throttle = 0;
            switch (Phase)
            {
                case BoosterPhase.Coasting:
                    if (verticalSpeed < 0 && altitude < EntryBurnStartAltitudeM)
                    {
                        Phase = BoosterPhase.EntryBurn;
                        LastEvent = "Entry burn startup";
                    }
                    break;

                case BoosterPhase.EntryBurn:
                    EnginesLit = 3;
                    Throttle = 1;
                    if (airSpeed < EntryBurnTargetSpeed || PropellantKg < LandingPropellantKg)
                    {
                        Phase = BoosterPhase.Descent;
                        LastEvent = "Entry burn shutdown";
                    }
                    break;

                case BoosterPhase.Descent:
                    if (!LegsDeployed && altitude < LegsDeployAltitudeM) LegsDeployed = true;
                    // Hoverslam: light the engine when a burn at 80% thrust would stop just above the deck.
                    if (verticalSpeed < 0 && altitude < LandingBurnCeilingM && PredictStopAltitude(altitude, verticalSpeed, HorizontalSpeed(up), 0.85) <= 15)
                    {
                        Phase = BoosterPhase.LandingBurn;
                        LastEvent = "Landing burn startup";
                    }
                    break;

                case BoosterPhase.LandingBurn:
                    LegsDeployed = true;
                    EnginesLit = 1;
                    // Pick the throttle that stops the booster two metres above the deck.
                    double lo = Falcon9.MerlinMinThrottle, hi = 1.0, horizontal = HorizontalSpeed(up);
                    for (int k = 0; k < 14; k++)
                    {
                        double mid = 0.5 * (lo + hi);
                        if (PredictStopAltitude(altitude, verticalSpeed, horizontal, mid) > 2.0) hi = mid; else lo = mid;
                    }
                    Throttle = hi;
                    break;
            }

            if (PropellantKg <= 0)
            {
                EnginesLit = 0;
                Throttle = 0;
            }

            ThrustDirection = retrograde;
            double3 acceleration = -EarthConstants.Mu * 1e9 * Position / math.pow(math.lengthsq(Position), 1.5);
            if (EnginesLit > 0)
            {
                double thrust = Falcon9.BoosterThrust(density, EnginesLit) * Throttle;
                acceleration += retrograde * thrust / Mass;
                PropellantKg = math.max(0, PropellantKg - Falcon9.BoosterMassFlow(EnginesLit) * Throttle * dt);
            }

            // Falling engines-first, with grid fins out.
            acceleration += -0.5 * density * airSpeed * LandingDragCoefficient * DragArea / Mass * air;

            Velocity += acceleration * dt;
            Position += Velocity * dt;

            if (AltitudeM <= 0)
            {
                TouchdownSpeed = math.length(AirVelocity);
                Phase = TouchdownSpeed < 8 ? BoosterPhase.Landed : BoosterPhase.Lost;
                LastEvent = Phase == BoosterPhase.Landed ? "Touchdown — booster landed" : "Booster lost on landing";
                Position = math.normalize(Position) * EarthConstants.RadiusKm * 1000.0;
                Velocity = Astro.EarthRotationVelocity(Position);
                TouchdownEcefKm = Astro.EciToEcef(Position / 1000.0, JulianDate);
            }
        }
    }
}
