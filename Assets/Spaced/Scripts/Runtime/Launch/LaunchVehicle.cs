using Unity.Mathematics;

namespace Spaced
{
    public enum VehicleKind
    {
        Falcon9,
        Starship,
    }

    /// <summary>What happens to the first stage after it separates.</summary>
    public enum BoosterRecovery
    {
        /// <summary>Lands on a droneship parked where it will come down (Falcon 9).</summary>
        Droneship,
        /// <summary>Flies back to the launch site and is caught by the tower's arms (Super Heavy).</summary>
        TowerCatch,
        /// <summary>No propellant kept for landing: more performance, and the booster is lost.</summary>
        Expended,
    }

    /// <summary>
    /// A two-stage launcher as numbers the flight physics, the payload planner and the models share.
    /// Falcon 9 figures come from SpaceX's user guide and flight data; Starship (Block 2, Super Heavy + Ship)
    /// from published and estimated values.
    /// </summary>
    public sealed class LaunchVehicle
    {
        public const double G0 = 9.80665;

        public string Name;
        public VehicleKind Kind;
        public BoosterRecovery Recovery;

        // First stage.
        public double BoosterDryKg, BoosterPropKg;
        public double BoosterThrustSeaLevel, BoosterThrustVacuum, BoosterIspVacuum;
        public int BoosterEngines;
        public double MinThrottle;
        /// <summary>Propellant held back at MECO for the return (boostback / entry / landing burns).</summary>
        public double LandingReserveKg;
        public int BoostbackEngines, EntryBurnEngines, LandingEngines;
        /// <summary>Kept for the landing burn whatever happened before.</summary>
        public double LandingPropellantKg;
        /// <summary>The landing burn may start below this altitude (higher for a heavy, fast-falling booster).</summary>
        public double LandingBurnCeilingM = 6000;
        public double MaxAscentG;

        // Second stage.
        public double SecondDryKg, SecondPropKg, SecondThrust, SecondIsp;
        public int SecondEngines;
        public double MaxUpperStageG = 100;
        public double FairingKg;

        // Geometry (metres).
        public double RadiusM;
        public float BoosterLengthM, StackLengthM;
        /// <summary>Booster base height above the pad when it hangs in the tower's arms.</summary>
        public double CatchHeightM;

        // Guidance tuning (found offline across many missions; see the flight test harness).
        public double PitchKickStart, PitchKickDuration, KickLightDeg, KickHeavyDeg, HeavyPayloadKg;
        public double MaxQLimitPa;
        public double StageCoastSeconds;
        public double FairingSeparationAltitudeM;

        // Planner calibration.
        public double AscentLossesMs;
        public double FirstStageEffectiveIsp;
        public double MaxPayloadSearchKg;

        public double CrossSectionM2 => math.PI_DBL * RadiusM * RadiusM;
        public bool HasFairing => FairingKg > 0;
        public bool ReturnsToSite => Recovery == BoosterRecovery.TowerCatch;

        public double EngineThrustSeaLevel => BoosterThrustSeaLevel / BoosterEngines;
        public double EngineThrustVacuum => BoosterThrustVacuum / BoosterEngines;

        /// <summary>Thrust at the given ambient density: nozzle back-pressure losses scale with pressure.</summary>
        public double BoosterThrust(double airDensity, int engines) =>
            (EngineThrustVacuum - (EngineThrustVacuum - EngineThrustSeaLevel) * math.saturate(airDensity / 1.225)) * engines;

        public double BoosterMassFlow(int engines) => EngineThrustVacuum * engines / (BoosterIspVacuum * G0);
        public double SecondMassFlow => SecondThrust / (SecondIsp * G0);

        /// <summary>Drag coefficient against Mach number for a slender rocket flying nose first.</summary>
        public static double AscentDragCoefficient(double mach)
        {
            if (mach < 0.8) return 0.30;
            if (mach < 1.2) return math.lerp(0.30, 0.55, (mach - 0.8) / 0.4);
            if (mach < 2.0) return math.lerp(0.55, 0.40, (mach - 1.2) / 0.8);
            return math.max(0.25, 0.40 - (mach - 2.0) * 0.03);
        }

        /// <summary>The same vehicle with its booster expended: all the propellant goes into the ascent.</summary>
        public LaunchVehicle Expendable()
        {
            var v = (LaunchVehicle)MemberwiseClone();
            v.Recovery = BoosterRecovery.Expended;
            v.LandingReserveKg = 0;
            v.Name = Name + " (expendable)";
            return v;
        }

        public static readonly LaunchVehicle Falcon9 = new LaunchVehicle
        {
            Name = "Falcon 9",
            Kind = VehicleKind.Falcon9,
            Recovery = BoosterRecovery.Droneship,
            BoosterDryKg = Spaced.Falcon9.BoosterDryKg,
            BoosterPropKg = Spaced.Falcon9.BoosterPropKg,
            BoosterThrustSeaLevel = Spaced.Falcon9.BoosterThrustSeaLevel,
            BoosterThrustVacuum = Spaced.Falcon9.BoosterThrustVacuum,
            BoosterIspVacuum = Spaced.Falcon9.BoosterIspVacuum,
            BoosterEngines = Spaced.Falcon9.BoosterEngines,
            MinThrottle = Spaced.Falcon9.MerlinMinThrottle,
            LandingReserveKg = Spaced.Falcon9.LandingReserveKg,
            BoostbackEngines = 3,
            EntryBurnEngines = 3,
            LandingEngines = 1,
            LandingPropellantKg = 11000,
            MaxAscentG = 5.0,
            SecondDryKg = Spaced.Falcon9.SecondDryKg,
            SecondPropKg = Spaced.Falcon9.SecondPropKg,
            SecondThrust = Spaced.Falcon9.SecondThrust,
            SecondIsp = Spaced.Falcon9.SecondIsp,
            SecondEngines = 1,
            FairingKg = Spaced.Falcon9.FairingKg,
            RadiusM = Spaced.Falcon9.RadiusM,
            BoosterLengthM = Spaced.Falcon9.BoosterLengthM,
            StackLengthM = Spaced.Falcon9.StackLengthM,
            PitchKickStart = 7, PitchKickDuration = 10, KickLightDeg = 6.5, KickHeavyDeg = 4.8, HeavyPayloadKg = 16000,
            MaxQLimitPa = 32000,
            StageCoastSeconds = 7,
            FairingSeparationAltitudeM = 112000,
            AscentLossesMs = 1580,
            FirstStageEffectiveIsp = 300,
            MaxPayloadSearchKg = 30000,
        };

        /// <summary>
        /// Super Heavy (33 Raptors, caught by the tower) and Ship (3 sea-level + 3 vacuum Raptors). Hot staging:
        /// the Ship lights while still attached, so there is barely a coast between stages.
        /// </summary>
        public static readonly LaunchVehicle Starship = new LaunchVehicle
        {
            Name = "Starship",
            Kind = VehicleKind.Starship,
            Recovery = BoosterRecovery.TowerCatch,
            BoosterDryKg = 200000,
            BoosterPropKg = 3400000,
            BoosterThrustSeaLevel = 33 * 2.30e6,
            BoosterThrustVacuum = 33 * 2.53e6,
            BoosterIspVacuum = 350,
            BoosterEngines = 33,
            MinThrottle = 0.4,
            LandingReserveKg = 345000,
            BoostbackEngines = 13,
            EntryBurnEngines = 0,
            LandingEngines = 3,
            LandingPropellantKg = 40000,
            LandingBurnCeilingM = 20000,
            MaxAscentG = 3.2,
            SecondDryKg = 130000,
            SecondPropKg = 1500000,
            SecondThrust = 3 * 2.53e6 + 3 * 2.75e6,
            SecondIsp = 368,
            SecondEngines = 6,
            MaxUpperStageG = 3.5,
            FairingKg = 0,
            RadiusM = 4.5,
            BoosterLengthM = 71f,
            StackLengthM = 123f,
            CatchHeightM = 32,
            PitchKickStart = 10, PitchKickDuration = 14, KickLightDeg = 9.5, KickHeavyDeg = 8.5, HeavyPayloadKg = 100000,
            MaxQLimitPa = 36000,
            StageCoastSeconds = 2,
            FairingSeparationAltitudeM = 0,
            AscentLossesMs = 1580,
            FirstStageEffectiveIsp = 300,
            MaxPayloadSearchKg = 250000,
        };
    }
}
