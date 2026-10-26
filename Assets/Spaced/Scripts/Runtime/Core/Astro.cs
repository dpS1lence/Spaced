using System;
using Unity.Mathematics;
using UnityEngine;

namespace Spaced
{
    /// <summary>Time scales, Earth orientation and Sun position (Vallado, "Fundamentals of Astrodynamics").</summary>
    public static class Astro
    {
        public const double JdJ2000 = 2451545.0;
        const double JdUnixEpoch = 2440587.5;
        static readonly DateTime UnixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static double JulianDate(DateTime utc)
        {
            if (utc.Kind == DateTimeKind.Local) utc = utc.ToUniversalTime();
            return JdUnixEpoch + (utc - UnixEpoch).TotalDays;
        }

        public static DateTime ToDateTime(double jd)
        {
            double days = math.clamp(jd - JdUnixEpoch, -700000.0, 2900000.0);
            return UnixEpoch.AddTicks((long)(days * TimeSpan.TicksPerDay));
        }

        /// <summary>Greenwich mean sidereal time in radians (IAU-82). UT1 is approximated by UTC.</summary>
        public static double Gmst(double jdUt1)
        {
            double t = (jdUt1 - JdJ2000) / 36525.0;
            double seconds = 67310.54841 + (876600.0 * 3600.0 + 8640184.812866) * t
                             + 0.093104 * t * t - 6.2e-6 * t * t * t;
            double rad = (seconds % MathConst.SecondsPerDay) / MathConst.SecondsPerDay * MathConst.TwoPi;
            return rad < 0 ? rad + MathConst.TwoPi : rad;
        }

        /// <summary>Unit vector from Earth to the Sun in the inertial frame (low-precision almanac, ~0.01°).</summary>
        public static double3 SunDirection(double jd)
        {
            double t = (jd - JdJ2000) / 36525.0;
            double meanLongitude = 280.460 + 36000.771 * t;
            double meanAnomaly = (357.5291092 + 35999.05034 * t) * MathConst.Deg2Rad;
            double eclipticLongitude = (meanLongitude
                                        + 1.914666471 * math.sin(meanAnomaly)
                                        + 0.019994643 * math.sin(2.0 * meanAnomaly)) * MathConst.Deg2Rad;
            double obliquity = (23.439291 - 0.0130042 * t) * MathConst.Deg2Rad;
            return new double3(
                math.cos(eclipticLongitude),
                math.cos(obliquity) * math.sin(eclipticLongitude),
                math.sin(obliquity) * math.sin(eclipticLongitude));
        }

        /// <summary>
        /// Geocentric position of the Moon (km, inertial frame of date) from the main terms of the lunar theory
        /// (Meeus, "Astronomical Algorithms", ch. 47, truncated): ~0.3° and ~1,000 km.
        /// </summary>
        public static double3 MoonPosition(double jd)
        {
            double t = (jd - JdJ2000) / 36525.0;
            double meanLongitude = 218.3165 + 481267.8813 * t;
            double elongation = (297.8502 + 445267.1115 * t) * MathConst.Deg2Rad;     // D
            double sunAnomaly = (357.5291 + 35999.0503 * t) * MathConst.Deg2Rad;      // M
            double moonAnomaly = (134.9634 + 477198.8676 * t) * MathConst.Deg2Rad;    // M'
            double latitudeArgument = (93.2721 + 483202.0175 * t) * MathConst.Deg2Rad; // F

            double longitude = meanLongitude
                               + 6.2888 * math.sin(moonAnomaly)
                               + 1.2740 * math.sin(2 * elongation - moonAnomaly)
                               + 0.6583 * math.sin(2 * elongation)
                               + 0.2136 * math.sin(2 * moonAnomaly)
                               - 0.1856 * math.sin(sunAnomaly)
                               - 0.1143 * math.sin(2 * latitudeArgument)
                               + 0.0588 * math.sin(2 * elongation - 2 * moonAnomaly)
                               + 0.0572 * math.sin(2 * elongation - sunAnomaly - moonAnomaly)
                               + 0.0533 * math.sin(2 * elongation + moonAnomaly);
            double latitude = 5.1282 * math.sin(latitudeArgument)
                              + 0.2806 * math.sin(moonAnomaly + latitudeArgument)
                              + 0.2777 * math.sin(moonAnomaly - latitudeArgument)
                              + 0.1732 * math.sin(2 * elongation - latitudeArgument);
            double distanceKm = 385000.56
                                - 20905.36 * math.cos(moonAnomaly)
                                - 3699.11 * math.cos(2 * elongation - moonAnomaly)
                                - 2955.97 * math.cos(2 * elongation)
                                - 569.93 * math.cos(2 * moonAnomaly);

            double lon = longitude * MathConst.Deg2Rad, lat = latitude * MathConst.Deg2Rad;
            double3 ecliptic = new double3(math.cos(lat) * math.cos(lon), math.cos(lat) * math.sin(lon), math.sin(lat));
            double obliquity = (23.439291 - 0.0130042 * t) * MathConst.Deg2Rad;
            double c = math.cos(obliquity), s = math.sin(obliquity);
            return distanceKm * new double3(ecliptic.x, c * ecliptic.y - s * ecliptic.z, s * ecliptic.y + c * ecliptic.z);
        }

        /// <summary>Fraction of the Moon's disc lit by the Sun as seen from Earth (0 = new, 1 = full).</summary>
        public static double MoonIllumination(double jd)
        {
            double cosElongation = math.dot(math.normalize(MoonPosition(jd)), SunDirection(jd));
            return (1.0 - cosElongation) * 0.5;
        }

        /// <summary>
        /// Inertial axes (right-handed, Z = north) → Unity axes (left-handed, Y up). No scaling.
        /// Swapping Y and Z flips handedness, so no axis needs negating.
        /// </summary>
        public static Vector3 EciToUnity(double3 v) => new Vector3((float)v.x, (float)v.z, (float)v.y);

        public static double3 UnityToEci(Vector3 v) => new double3(v.x, v.z, v.y);

        /// <summary>Earth's orientation in world space at the given time (rotation about world Y by GMST).</summary>
        public static Quaternion EarthRotation(double jd) =>
            Quaternion.AngleAxis(-(float)(Gmst(jd) * MathConst.Rad2Deg), Vector3.up);

        /// <summary>Earth-fixed position (km) on a spherical Earth.</summary>
        public static double3 GeoToEcef(double latDeg, double lonDeg, double altitudeKm)
        {
            double lat = latDeg * MathConst.Deg2Rad, lon = lonDeg * MathConst.Deg2Rad;
            double r = EarthConstants.RadiusKm + altitudeKm;
            return new double3(r * math.cos(lat) * math.cos(lon), r * math.cos(lat) * math.sin(lon), r * math.sin(lat));
        }

        public static void EcefToGeo(double3 ecefKm, out double latDeg, out double lonDeg, out double altitudeKm)
        {
            double r = math.length(ecefKm);
            latDeg = math.asin(ecefKm.z / r) * MathConst.Rad2Deg;
            lonDeg = math.atan2(ecefKm.y, ecefKm.x) * MathConst.Rad2Deg;
            altitudeKm = r - EarthConstants.RadiusKm;
        }

        public static double3 EcefToEci(double3 ecef, double jd) => RotateZ(ecef, Gmst(jd));
        public static double3 EciToEcef(double3 eci, double jd) => RotateZ(eci, -Gmst(jd));

        static double3 RotateZ(double3 v, double angle)
        {
            double c = math.cos(angle), s = math.sin(angle);
            return new double3(c * v.x - s * v.y, s * v.x + c * v.y, v.z);
        }

        /// <summary>Velocity (same units per second as <paramref name="eci"/>) of a point fixed to the rotating Earth.</summary>
        public static double3 EarthRotationVelocity(double3 eci) =>
            math.cross(new double3(0, 0, EarthConstants.RotationRate), eci);

        /// <summary>Local east/north/up unit vectors at an inertial position.</summary>
        public static void EnuBasis(double3 eci, out double3 east, out double3 north, out double3 up)
        {
            up = math.normalize(eci);
            east = math.cross(new double3(0, 0, 1), up);
            east = math.lengthsq(east) < 1e-12 ? new double3(0, 1, 0) : math.normalize(east);
            north = math.cross(up, east);
        }
    }
}
