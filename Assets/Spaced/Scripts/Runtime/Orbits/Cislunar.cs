using Unity.Mathematics;

namespace Spaced
{
    public static class MoonConstants
    {
        /// <summary>Gravitational parameter, km³/s².</summary>
        public const double Mu = 4902.800;
        /// <summary>Mean radius, km.</summary>
        public const double RadiusKm = 1737.4;
        /// <summary>Sphere of influence radius (Laplace), km.</summary>
        public const double SoiKm = 66100;
    }

    /// <summary>A spacecraft state in the Earth-centred inertial frame: km, km/s and the Julian date.</summary>
    public struct SpaceState
    {
        public double3 R;
        public double3 V;
        public double Jd;
    }

    /// <summary>
    /// Earth–Moon space flight: gravity of both (the Moon from the ephemeris, plus the indirect term because the
    /// Earth itself falls towards the Moon), a fixed-step RK4 integrator whose step follows the nearer body, and
    /// universal-variable Kepler propagation for coasting "on rails" around either body.
    /// </summary>
    public static class Cislunar
    {
        /// <summary>Moon position (km) and velocity (km/s) in the Earth-centred inertial frame.</summary>
        public static void Moon(double jd, out double3 position, out double3 velocity)
        {
            position = Astro.MoonPosition(jd);
            const double h = 60.0;
            velocity = (Astro.MoonPosition(jd + h / MathConst.SecondsPerDay) - Astro.MoonPosition(jd - h / MathConst.SecondsPerDay)) / (2.0 * h);
        }

        public static double3 Acceleration(double3 r, double3 moon)
        {
            double re = math.length(r);
            double3 toMoon = r - moon;
            double rm = math.length(toMoon);
            double dm = math.length(moon);
            return -EarthConstants.Mu * r / (re * re * re)
                   - MoonConstants.Mu * toMoon / (rm * rm * rm)
                   - MoonConstants.Mu * moon / (dm * dm * dm);
        }

        /// <summary>Integration step (s): ~1/3000 of the orbital period around the nearer body.</summary>
        public static double StepFor(double3 r, double3 moon, double maxStep)
        {
            double re = math.length(r), rm = math.length(r - moon);
            double te = math.sqrt(re * re * re / EarthConstants.Mu);
            double tm = math.sqrt(rm * rm * rm / MoonConstants.Mu);
            return math.clamp(0.002 * math.min(te, tm), 0.5, maxStep);
        }

        /// <summary>Advances the state by <paramref name="dt"/> seconds (either sign) under Earth and Moon gravity.</summary>
        public static void Propagate(ref SpaceState s, double dt, double maxStep = 900)
        {
            double remaining = dt;
            double sign = dt >= 0 ? 1 : -1;
            int guard = 0;
            while (math.abs(remaining) > 1e-6 && guard++ < 200000)
            {
                double3 moon0 = Astro.MoonPosition(s.Jd);
                double h = math.min(StepFor(s.R, moon0, maxStep), math.abs(remaining)) * sign;
                Step(ref s, h, moon0);
                remaining -= h;
            }
        }

        /// <summary>One RK4 step of <paramref name="h"/> seconds; <paramref name="moon0"/> is the Moon at the start.</summary>
        public static void Step(ref SpaceState s, double h, double3 moon0) => Step(ref s, h, moon0, double3.zero);

        /// <summary>One RK4 step with a constant extra acceleration (thrust, km/s²).</summary>
        public static void Step(ref SpaceState s, double h, double3 moon0, double3 extra)
        {
            double3 moonMid = Astro.MoonPosition(s.Jd + 0.5 * h / MathConst.SecondsPerDay);
            double3 moon1 = Astro.MoonPosition(s.Jd + h / MathConst.SecondsPerDay);
            double3 r = s.R, v = s.V;
            double3 k1v = Acceleration(r, moon0) + extra, k1r = v;
            double3 k2v = Acceleration(r + 0.5 * h * k1r, moonMid) + extra, k2r = v + 0.5 * h * k1v;
            double3 k3v = Acceleration(r + 0.5 * h * k2r, moonMid) + extra, k3r = v + 0.5 * h * k2v;
            double3 k4v = Acceleration(r + h * k3r, moon1) + extra, k4r = v + h * k3v;
            s.R = r + h / 6.0 * (k1r + 2 * k2r + 2 * k3r + k4r);
            s.V = v + h / 6.0 * (k1v + 2 * k2v + 2 * k3v + k4v);
            s.Jd += h / MathConst.SecondsPerDay;
        }

        /// <summary>
        /// Two-body propagation (universal variables, any conic) of position <paramref name="r"/> and velocity
        /// <paramref name="v"/> relative to a body of gravitational parameter <paramref name="mu"/>.
        /// </summary>
        public static void Kepler(ref double3 r, ref double3 v, double mu, double dt)
        {
            if (dt == 0) return;
            double sqrtMu = math.sqrt(mu);
            double r0 = math.length(r);
            double vr0 = math.dot(r, v) / r0;
            double alpha = 2.0 / r0 - math.lengthsq(v) / mu; // 1/a
            double chi = math.abs(alpha) > 1e-9 ? sqrtMu * math.abs(alpha) * dt : sqrtMu * dt / r0;
            if (alpha > 1e-9) chi = sqrtMu * alpha * dt;
            double c = 0.5, s = 1.0 / 6.0;
            for (int i = 0; i < 60; i++)
            {
                double z = alpha * chi * chi;
                Stumpff(z, out c, out s);
                double f = r0 * vr0 / sqrtMu * chi * chi * c + (1 - alpha * r0) * chi * chi * chi * s + r0 * chi - sqrtMu * dt;
                double df = r0 * vr0 / sqrtMu * chi * (1 - alpha * chi * chi * s) + (1 - alpha * r0) * chi * chi * c + r0;
                double delta = f / df;
                chi -= delta;
                if (math.abs(delta) < 1e-10) break;
            }
            {
                double z = alpha * chi * chi;
                Stumpff(z, out c, out s);
            }
            double fl = 1 - chi * chi / r0 * c;
            double g = dt - chi * chi * chi / sqrtMu * s;
            double3 r1 = fl * r + g * v;
            double r1m = math.length(r1);
            double fdot = sqrtMu / (r1m * r0) * (alpha * chi * chi * chi * s - chi);
            double gdot = 1 - chi * chi / r1m * c;
            v = fdot * r + gdot * v;
            r = r1;
        }

        static void Stumpff(double z, out double c, out double s)
        {
            if (z > 1e-8)
            {
                double sz = math.sqrt(z);
                c = (1 - math.cos(sz)) / z;
                s = (sz - math.sin(sz)) / (sz * z);
            }
            else if (z < -1e-8)
            {
                double sz = math.sqrt(-z);
                c = (math.cosh(sz) - 1) / -z;
                s = (math.sinh(sz) - sz) / (sz * -z);
            }
            else
            {
                c = 0.5 - z / 24.0;
                s = 1.0 / 6.0 - z / 120.0;
            }
        }

        /// <summary>Coasts a state around the Moon on a two-body orbit (fast; ignores the Earth's pull).</summary>
        public static void CoastAroundMoon(ref SpaceState s, double dt)
        {
            Moon(s.Jd, out double3 moon0, out double3 moonV0);
            double3 r = s.R - moon0, v = s.V - moonV0;
            Kepler(ref r, ref v, MoonConstants.Mu, dt);
            s.Jd += dt / MathConst.SecondsPerDay;
            Moon(s.Jd, out double3 moon1, out double3 moonV1);
            s.R = moon1 + r;
            s.V = moonV1 + v;
        }

        /// <summary>Coasts a state around the Earth on a two-body orbit.</summary>
        public static void CoastAroundEarth(ref SpaceState s, double dt)
        {
            Kepler(ref s.R, ref s.V, EarthConstants.Mu, dt);
            s.Jd += dt / MathConst.SecondsPerDay;
        }

        /// <summary>
        /// Orientation of the Moon (tidally locked: the near side's centre, map longitude 0, faces the Earth; celestial
        /// north up), as Moon-fixed → inertial axes. Matches <see cref="MoonRenderer"/>.
        /// </summary>
        public static void MoonAxes(double jd, out double3 toEarth, out double3 north, out double3 east)
        {
            double3 moon = Astro.MoonPosition(jd);
            toEarth = math.normalize(-moon);
            north = math.normalize(new double3(0, 0, 1) - toEarth * toEarth.z);
            east = math.cross(north, toEarth);
        }

        /// <summary>Inertial position (km) of a point on the Moon's surface given in Moon-fixed latitude/longitude.</summary>
        public static double3 MoonSurfacePoint(double latDeg, double lonDeg, double heightKm, double jd)
        {
            MoonAxes(jd, out double3 x, out double3 z, out double3 y);
            double lat = latDeg * MathConst.Deg2Rad, lon = lonDeg * MathConst.Deg2Rad;
            double3 local = math.cos(lat) * math.cos(lon) * x + math.cos(lat) * math.sin(lon) * y + math.sin(lat) * z;
            return Astro.MoonPosition(jd) + local * (MoonConstants.RadiusKm + heightKm);
        }

        /// <summary>Moon-fixed latitude/longitude (degrees) of an inertial point near the Moon.</summary>
        public static void MoonLatLon(double3 positionKm, double jd, out double latDeg, out double lonDeg)
        {
            MoonAxes(jd, out double3 x, out double3 z, out double3 y);
            double3 d = math.normalize(positionKm - Astro.MoonPosition(jd));
            latDeg = math.asin(math.clamp(math.dot(d, z), -1, 1)) * MathConst.Rad2Deg;
            lonDeg = math.atan2(math.dot(d, y), math.dot(d, x)) * MathConst.Rad2Deg;
        }
    }
}
