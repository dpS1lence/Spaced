using System;
using Unity.Mathematics;

namespace Spaced
{
    /// <summary>What a trial trajectory does at the Moon (and, for a free return, back at the Earth).</summary>
    public struct LunarEncounter
    {
        /// <summary>Closest approach to the Moon's centre (km) and when.</summary>
        public double PeriluneRadiusKm, PeriluneJd;
        /// <summary>Aim point in the B-plane (km): where the approach asymptote pierces the plane through the Moon.</summary>
        public double BT, BR;
        public bool ReachedSoi;
        /// <summary>Hyperbolic excess speed relative to the Moon (km/s).</summary>
        public double VInfinityKmS;
        /// <summary>Closest approach to the Earth's centre after the flyby (km); +∞ if it never comes back.</summary>
        public double ReturnPerigeeKm, ReturnPerigeeJd;
        public double PeriluneAltitudeKm => PeriluneRadiusKm - MoonConstants.RadiusKm;
        public double ReturnPerigeeAltitudeKm => ReturnPerigeeKm - EarthConstants.RadiusKm;
    }

    /// <summary>
    /// Planning for Moon missions: the launch plane (it must contain the Moon at arrival), the trans-lunar injection
    /// from the parking orbit (a patched-conic first guess), and mid-course corrections that hit a chosen aim point
    /// at the Moon, found by Newton iteration on trial trajectories flown with the full Earth–Moon gravity.
    /// </summary>
    public static class LunarTargeting
    {
        public const double ParkingAltitudeKm = 250;
        /// <summary>Transfer apogee as a multiple of the Moon's distance: a bit beyond it makes a ~3-day trip.</summary>
        const double ApogeeFactor = 1.12;

        /// <summary>
        /// Launch time and orbit plane whose parking orbit contains the Moon's position at arrival, so the transfer
        /// stays in one plane. The inclination is the pad's latitude unless the Moon is further north or south.
        /// </summary>
        public static double LaunchWindow(LaunchSite site, double earliestJd, out double inclinationDeg, out double raanDeg)
        {
            double liftoff = earliestJd;
            inclinationDeg = math.max(site.LatitudeDeg + 0.3, 5);
            raanDeg = 0;
            for (int iteration = 0; iteration < 5; iteration++)
            {
                double arrival = liftoff + 0.08 + 3.1;
                double3 moon = math.normalize(Astro.MoonPosition(arrival));
                double declination = math.asin(moon.z), rightAscension = math.atan2(moon.y, moon.x);
                inclinationDeg = math.max(site.LatitudeDeg + 0.3, math.abs(declination * MathConst.Rad2Deg) + 1.5);
                double inc = inclinationDeg * MathConst.Deg2Rad;
                double ratio = math.clamp(math.tan(declination) / math.tan(inc), -1, 1);
                // Two planes contain the direction (ascending or descending through it); take the earlier window.
                double best = double.MaxValue, bestRaan = 0;
                foreach (double raan in new[] { rightAscension - math.asin(ratio), rightAscension - math.PI_DBL + math.asin(ratio) })
                {
                    var plane = OrbitMath.FromAltitudes(ParkingAltitudeKm, ParkingAltitudeKm, inclinationDeg, raan * MathConst.Rad2Deg, 0, 0);
                    double window = LaunchPlanner.NextLaunchWindow(site, plane, liftoff, earliestJd);
                    if (window < best) { best = window; bestRaan = raan; }
                }
                liftoff = best;
                raanDeg = OrbitMath.WrapAngle(bestRaan) * MathConst.Rad2Deg;
            }
            return liftoff;
        }

        /// <summary>
        /// The circular parking orbit a rocket launched at <paramref name="liftoffJd"/> into the given plane reaches
        /// about ten minutes later, some 2000 km downrange of the pad (for launches that aren't flown).
        /// </summary>
        public static SpaceState ParkingStateAfterAscent(LaunchSite site, double liftoffJd, double inclinationDeg, double raanDeg)
        {
            double jd = liftoffJd + 600.0 / MathConst.SecondsPerDay;
            var plane = OrbitMath.FromAltitudes(ParkingAltitudeKm, ParkingAltitudeKm, inclinationDeg, raanDeg, 0, 0);
            double3 n = LaunchPlanner.PlaneNormal(plane.I, plane.Raan);
            double3 pad = math.normalize(Astro.EcefToEci(Astro.GeoToEcef(site.LatitudeDeg, site.LongitudeDeg, 0), liftoffJd));
            double3 inPlane = math.normalize(pad - math.dot(pad, n) * n);
            double3 prograde = math.normalize(math.cross(n, inPlane));
            double r = EarthConstants.RadiusKm + ParkingAltitudeKm;
            double downrange = 2000.0 / r;
            double3 p = math.normalize(inPlane * math.cos(downrange) + prograde * math.sin(downrange));
            return new SpaceState { R = p * r, V = math.normalize(math.cross(n, p)) * math.sqrt(EarthConstants.Mu / r), Jd = jd };
        }

        /// <summary>
        /// First guess for trans-lunar injection from the parking orbit (which needn't be quite circular): when to
        /// burn (Julian date) and how much to speed up (km/s, prograde), so the transfer ellipse meets the Moon.
        /// The parking orbit is propagated on its real two-body path to find the burn point.
        /// </summary>
        public static double PlanInjection(SpaceState parking, out double deltaVKmS)
        {
            double3 h = math.normalize(math.cross(parking.R, parking.V));
            double burnJd = parking.Jd + 900.0 / MathConst.SecondsPerDay;
            deltaVKmS = 3.1;
            for (int iteration = 0; iteration < 12; iteration++)
            {
                var atBurn = parking;
                Cislunar.CoastAroundEarth(ref atBurn, (burnJd - parking.Jd) * MathConst.SecondsPerDay);
                double r0 = math.length(atBurn.R);

                // Transfer ellipse from the burn point to beyond the Moon; time to reach the Moon's distance.
                double distance = math.length(Astro.MoonPosition(burnJd + 3.0));
                double apogee = distance * ApogeeFactor;
                double a = 0.5 * (r0 + apogee), e = (apogee - r0) / (apogee + r0);
                double cosNu = math.clamp((a * (1 - e * e) / distance - 1) / e, -1, 1);
                double nu = math.acos(cosNu);
                double eccentricAnomaly = 2 * math.atan(math.sqrt((1 - e) / (1 + e)) * math.tan(nu / 2));
                double tof = (eccentricAnomaly - e * math.sin(eccentricAnomaly)) / math.sqrt(EarthConstants.Mu / (a * a * a));
                deltaVKmS = math.sqrt(EarthConstants.Mu * (2 / r0 - 1 / a)) - math.length(atBurn.V);

                // The Moon at arrival, projected into the orbit plane: the burn point lies nu before it.
                double3 moon = Astro.MoonPosition(burnJd + tof / MathConst.SecondsPerDay);
                double3 moonInPlane = math.normalize(moon - math.dot(moon, h) * h);
                double3 departure = RotateAbout(moonInPlane, h, -nu);
                double3 here = math.normalize(atBurn.R);
                double angle = math.atan2(math.dot(math.cross(here, departure), h), math.dot(here, departure));
                double rate = math.length(math.cross(atBurn.R, atBurn.V)) / (r0 * r0);
                burnJd += angle / rate / MathConst.SecondsPerDay;
                // Not in the past, and leave time to prepare: wait for the next time round instead.
                double period = MathConst.TwoPi / rate;
                if (burnJd < parking.Jd + 600.0 / MathConst.SecondsPerDay) burnJd += period / MathConst.SecondsPerDay;
            }
            return burnJd;
        }

        static double3 RotateAbout(double3 v, double3 axis, double angle)
        {
            double c = math.cos(angle), s = math.sin(angle);
            return v * c + math.cross(axis, v) * s + axis * math.dot(axis, v) * (1 - c);
        }

        /// <summary>
        /// Flies a trial trajectory: up to the Moon (closest approach, B-plane at the sphere of influence) and, when
        /// <paramref name="followReturn"/>, back to its next closest approach to the Earth.
        /// </summary>
        public static LunarEncounter Simulate(SpaceState s, double maxDays, bool followReturn)
        {
            var result = new LunarEncounter { PeriluneRadiusKm = double.MaxValue, ReturnPerigeeKm = double.PositiveInfinity };
            double endJd = s.Jd + maxDays;
            bool pastPerilune = false;
            double previousMoon = double.MaxValue, previousEarth = double.MaxValue;
            int guard = 0;
            while (s.Jd < endJd && guard++ < 100000)
            {
                double3 moon = Astro.MoonPosition(s.Jd);
                double rm = math.length(s.R - moon);
                double re = math.length(s.R);
                if (!result.ReachedSoi && rm < MoonConstants.SoiKm)
                {
                    result.ReachedSoi = true;
                    Cislunar.Moon(s.Jd, out _, out double3 moonVelocity);
                    BPlane(s.R - moon, s.V - moonVelocity, moon, moonVelocity, out result.BT, out result.BR);
                    double vRel = math.length(s.V - moonVelocity);
                    result.VInfinityKmS = math.sqrt(math.max(vRel * vRel - 2 * MoonConstants.Mu / rm, 0.01));
                }
                if (!pastPerilune)
                {
                    if (rm < result.PeriluneRadiusKm) { result.PeriluneRadiusKm = rm; result.PeriluneJd = s.Jd; }
                    if (rm < MoonConstants.RadiusKm) break; // impact
                    if (rm > previousMoon && result.ReachedSoi) pastPerilune = true;
                    else if (rm > previousMoon && rm > 450000) break; // went past without getting close
                    previousMoon = rm;
                }
                else
                {
                    if (!followReturn) break;
                    if (re < result.ReturnPerigeeKm) { result.ReturnPerigeeKm = re; result.ReturnPerigeeJd = s.Jd; }
                    if (re < EarthConstants.RadiusKm + 50 || re > previousEarth && re < 200000) break;
                    previousEarth = re;
                }
                double h = Cislunar.StepFor(s.R, moon, 1800);
                Cislunar.Step(ref s, h, moon);
            }
            if (!pastPerilune && result.PeriluneRadiusKm == double.MaxValue) result.PeriluneRadiusKm = 1e9;
            return result;
        }

        /// <summary>B-plane components (km) of a Moon-relative approach state.</summary>
        static void BPlane(double3 r, double3 v, double3 moon, double3 moonVelocity, out double bt, out double br)
        {
            double3 s = math.normalize(v);
            double3 b = r - math.dot(r, s) * s;
            double3 moonNormal = math.normalize(math.cross(moon, moonVelocity));
            double3 t = math.normalize(math.cross(s, moonNormal));
            double3 rr = math.cross(s, t);
            bt = math.dot(b, t);
            br = math.dot(b, rr);
        }

        /// <summary>B-plane distance that gives a closest approach of <paramref name="periluneRadiusKm"/> at this approach speed.</summary>
        static double BForPerilune(double periluneRadiusKm, double vInfinityKmS) =>
            periluneRadiusKm * math.sqrt(1 + 2 * MoonConstants.Mu / (periluneRadiusKm * vInfinityKmS * vInfinityKmS));

        /// <summary>
        /// Velocity change (km/s, inertial) that makes the trajectory from <paramref name="s"/> pass the Moon at
        /// <paramref name="altitudeKm"/>, on the side given by <paramref name="direction"/> in the B-plane (null: the
        /// side it's already heading for). Newton iteration on the B-plane aim point with a numerically differentiated
        /// Jacobian (smallest-norm solution), in an outer loop that corrects the aim from the altitude actually flown.
        /// </summary>
        public static bool SolvePerilune(SpaceState s, double altitudeKm, double2? direction, out double3 deltaV, out LunarEncounter achieved,
            bool followReturn = false)
        {
            deltaV = double3.zero;
            double days = followReturn ? 14 : 6;
            achieved = Trial(s, deltaV, days, followReturn);
            if (!achieved.ReachedSoi) return false;
            double2 aimDirection = direction ?? math.normalizesafe(new double2(achieved.BT, achieved.BR), new double2(1, 0));
            double targetRadius = MoonConstants.RadiusKm + altitudeKm;
            double scale = 1;
            for (int outer = 0; outer < 6; outer++)
            {
                double b = BForPerilune(targetRadius, achieved.VInfinityKmS) * scale;
                if (!NewtonB(s, aimDirection * b, days, followReturn, ref deltaV, ref achieved)) return false;
                double error = achieved.PeriluneRadiusKm - targetRadius;
                if (math.abs(error) < 8) return true;
                // Gravity focusing makes the flown perilune grow roughly with the square of the aim distance.
                scale *= math.sqrt(math.clamp(targetRadius / math.max(achieved.PeriluneRadiusKm, 1), 0.25, 4));
            }
            return math.abs(achieved.PeriluneRadiusKm - targetRadius) < 50;
        }

        static bool NewtonB(SpaceState s, double2 want, double days, bool followReturn, ref double3 deltaV, ref LunarEncounter achieved)
        {
            const double perturbation = 0.001; // 1 m/s
            for (int iteration = 0; iteration < 10; iteration++)
            {
                var residual = new[] { want.x - achieved.BT, want.y - achieved.BR };
                if (math.abs(residual[0]) < 5 && math.abs(residual[1]) < 5) return true;
                var columns = new double[3][];
                for (int axis = 0; axis < 3; axis++)
                {
                    double3 step = double3.zero;
                    step[axis] = perturbation;
                    var perturbed = Trial(s, deltaV + step, days, followReturn);
                    if (!perturbed.ReachedSoi) return false;
                    columns[axis] = new[] { (perturbed.BT - achieved.BT) / perturbation, (perturbed.BR - achieved.BR) / perturbation };
                }
                double3 correction = LeastNorm(columns, residual);
                // Damp big jumps: the problem is nonlinear close to the Moon.
                double magnitude = math.length(correction);
                if (magnitude > 0.05) correction *= 0.05 / magnitude;
                deltaV += correction;
                achieved = Trial(s, deltaV, days, followReturn);
                if (!achieved.ReachedSoi) return false;
            }
            return math.abs(want.x - achieved.BT) < 25 && math.abs(want.y - achieved.BR) < 25;
        }

        /// <summary>
        /// Free return: the flyby that swings around the Moon and comes back to <paramref name="returnAltitudeKm"/>
        /// above the Earth (re-entry) with no further burns. Scans flyby altitudes on four sides of the Moon, then
        /// refines (secant on the flyby altitude) where the return perigee crosses the target.
        /// </summary>
        public static bool SolveFreeReturn(SpaceState s, double returnAltitudeKm, out double3 deltaV, out LunarEncounter achieved)
        {
            deltaV = double3.zero;
            achieved = Trial(s, deltaV, 14, true);
            if (!achieved.ReachedSoi) return false;
            double2 current = math.normalizesafe(new double2(achieved.BT, achieved.BR), new double2(1, 0));
            double target = EarthConstants.RadiusKm + returnAltitudeKm;
            double bestScore = double.MaxValue;
            double3 bestDv = double3.zero;
            var bestEncounter = achieved;

            double Perigee(LunarEncounter e) => double.IsInfinity(e.ReturnPerigeeKm) ? 1e7 : e.ReturnPerigeeKm;
            void Consider(double3 dv, LunarEncounter e)
            {
                double score = math.abs(Perigee(e) - target);
                if (score < bestScore) { bestScore = score; bestDv = dv; bestEncounter = e; }
            }

            for (int d = 0; d < 8; d++)
            {
                double angle = d * math.PI_DBL / 4;
                double2 direction = new double2(current.x * math.cos(angle) - current.y * math.sin(angle), current.x * math.sin(angle) + current.y * math.cos(angle));
                double previousAltitude = 0, previousPerigee = 0;
                foreach (double altitude in new[] { 150.0, 400, 1000, 2500, 5000, 9000, 15000 })
                {
                    if (!SolvePerilune(s, altitude, direction, out double3 dv, out var e, true)) continue;
                    Consider(dv, e);
                    double perigee = Perigee(e);
                    if (previousPerigee != 0 && math.sign(perigee - target) != math.sign(previousPerigee - target))
                    {
                        double lo = previousAltitude, hi = altitude, fLo = previousPerigee - target, fHi = perigee - target;
                        for (int k = 0; k < 10 && bestScore >= 15; k++)
                        {
                            double mid = lo + (hi - lo) * math.clamp(fLo / (fLo - fHi), 0.1, 0.9);
                            if (!SolvePerilune(s, mid, direction, out double3 dvMid, out var eMid, true)) break;
                            Consider(dvMid, eMid);
                            double fMid = Perigee(eMid) - target;
                            if (math.sign(fMid) == math.sign(fLo)) { lo = mid; fLo = fMid; } else { hi = mid; fHi = fMid; }
                        }
                    }
                    previousAltitude = altitude;
                    previousPerigee = perigee;
                    if (bestScore < 15) break;
                }
                if (bestScore < 15) break;
            }
            deltaV = bestDv;
            achieved = bestEncounter;
            return bestScore < 40;
        }

        static LunarEncounter Trial(SpaceState s, double3 deltaV, double days, bool followReturn)
        {
            s.V += deltaV;
            return Simulate(s, days, followReturn);
        }

        /// <summary>Δv = Jᵀ(J Jᵀ)⁻¹ r for a 2×3 Jacobian given as three columns.</summary>
        static double3 LeastNorm(double[][] columns, double[] residual)
        {
            // J is 2×3: rows k, columns axis. A = J Jᵀ (2×2).
            double a00 = 0, a01 = 0, a11 = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                a00 += columns[axis][0] * columns[axis][0];
                a01 += columns[axis][0] * columns[axis][1];
                a11 += columns[axis][1] * columns[axis][1];
            }
            double det = a00 * a11 - a01 * a01;
            if (math.abs(det) < 1e-12) return double3.zero;
            double y0 = (a11 * residual[0] - a01 * residual[1]) / det;
            double y1 = (-a01 * residual[0] + a00 * residual[1]) / det;
            var result = double3.zero;
            for (int axis = 0; axis < 3; axis++) result[axis] = columns[axis][0] * y0 + columns[axis][1] * y1;
            return result;
        }
    }
}
