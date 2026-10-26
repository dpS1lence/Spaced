namespace Spaced
{
    /// <summary>
    /// Earth constants. WGS-72 is used on purpose: CelesTrak's elements are fitted with SGP4,
    /// which uses WGS-72, so the Kozai → Brouwer conversion has to use the same values.
    /// </summary>
    public static class EarthConstants
    {
        /// <summary>Gravitational parameter, km³/s².</summary>
        public const double Mu = 398600.8;

        /// <summary>Equatorial radius, km.</summary>
        public const double RadiusKm = 6378.135;

        /// <summary>Second zonal harmonic.</summary>
        public const double J2 = 0.001082616;

        /// <summary>Sidereal rotation rate, rad/s.</summary>
        public const double RotationRate = 7.292115146706979e-5;

        /// <summary>Objects whose perigee drops below this altitude are considered reentered.</summary>
        public const double ReentryAltitudeKm = 100.0;
    }

    /// <summary>World-space scale used for everything rendered in the scene.</summary>
    public static class WorldScale
    {
        /// <summary>Kilometres per Unity unit. Earth is ~6.4 units across, GEO sits at ~42.</summary>
        public const double KmPerUnit = 1000.0;
        public const float InvKmPerUnit = (float)(1.0 / KmPerUnit);
    }

    public static class MathConst
    {
        public const double TwoPi = 2.0 * System.Math.PI;
        public const double Deg2Rad = System.Math.PI / 180.0;
        public const double Rad2Deg = 180.0 / System.Math.PI;
        public const double SecondsPerDay = 86400.0;
        public const double SecondsPerYear = 365.25 * SecondsPerDay;
    }
}
