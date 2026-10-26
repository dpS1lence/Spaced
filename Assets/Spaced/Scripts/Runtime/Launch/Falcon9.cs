using Unity.Mathematics;

namespace Spaced
{
    /// <summary>Falcon 9 Block 5 figures from SpaceX's published user guide and public flight data.</summary>
    public static class Falcon9
    {
        public const double G0 = 9.80665;

        // First stage: nine Merlin 1D.
        public const double BoosterDryKg = 25600;
        public const double BoosterPropKg = 395700;
        public const double BoosterThrustSeaLevel = 7607e3;
        public const double BoosterThrustVacuum = 8227e3;
        public const double BoosterIspVacuum = 311;
        public const int BoosterEngines = 9;
        public const double MerlinThrustSeaLevel = BoosterThrustSeaLevel / BoosterEngines;
        public const double MerlinThrustVacuum = BoosterThrustVacuum / BoosterEngines;
        public const double MerlinMinThrottle = 0.4;
        /// <summary>Propellant held back at MECO for the entry and landing burns on a droneship mission.</summary>
        public const double LandingReserveKg = 30000;

        // Second stage: one Merlin Vacuum.
        public const double SecondDryKg = 4000;
        public const double SecondPropKg = 92670;
        public const double SecondThrust = 981e3;
        public const double SecondIsp = 348;

        public const double FairingKg = 1900;
        public const double RadiusM = 1.83;
        public const double CrossSectionM2 = math.PI_DBL * RadiusM * RadiusM;

        /// <summary>Booster length, interstage included (metres). The full stack is ~70 m.</summary>
        public const float BoosterLengthM = 47.7f;
        public const float StackLengthM = 70f;

        /// <summary>Max payload to low Earth orbit with booster recovery on a droneship.</summary>
        public const double ReusableLeoPayloadKg = 17400;

        /// <summary>Thrust at the given ambient density: nozzle back-pressure losses scale with pressure.</summary>
        public static double BoosterThrust(double airDensity, int engines) =>
            (MerlinThrustVacuum - (MerlinThrustVacuum - MerlinThrustSeaLevel) * math.saturate(airDensity / 1.225)) * engines;

        public static double BoosterMassFlow(int engines) => MerlinThrustVacuum * engines / (BoosterIspVacuum * G0);
        public static double SecondMassFlow => SecondThrust / (SecondIsp * G0);

        /// <summary>Drag coefficient against Mach number for a slender rocket flying nose first.</summary>
        public static double AscentDragCoefficient(double mach)
        {
            if (mach < 0.8) return 0.30;
            if (mach < 1.2) return math.lerp(0.30, 0.55, (mach - 0.8) / 0.4);
            if (mach < 2.0) return math.lerp(0.55, 0.40, (mach - 1.2) / 0.8);
            return math.max(0.25, 0.40 - (mach - 2.0) * 0.03);
        }
    }
}
