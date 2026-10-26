using Unity.Mathematics;

namespace Spaced
{
    /// <summary>Launch geometry and performance: target planes, launch windows and payload capacity.</summary>
    public static class LaunchPlanner
    {
        /// <summary>Unit normal of an orbit plane (direction of angular momentum) in the inertial frame.</summary>
        public static double3 PlaneNormal(double inclinationRad, double raanRad) => new double3(
            math.sin(inclinationRad) * math.sin(raanRad),
            -math.sin(inclinationRad) * math.cos(raanRad),
            math.cos(inclinationRad));

        /// <summary>True when the site should launch southward for this inclination.</summary>
        public static bool LaunchesSouth(LaunchSite site, double inclinationDeg) => site.PrefersSouth || inclinationDeg > 70;

        /// <summary>
        /// Plane reached by launching right now: inertial azimuth from sin(β) = cos(i)/cos(φ).
        /// Inclinations below the site latitude are not reachable without a plane change, so they clamp to it.
        /// </summary>
        public static double3 PlaneForImmediateLaunch(LaunchSite site, double inclinationDeg, double jd, out double azimuthDeg)
        {
            double3 site0 = Astro.EcefToEci(Astro.GeoToEcef(site.LatitudeDeg, site.LongitudeDeg, 0), jd);
            Astro.EnuBasis(site0, out double3 east, out double3 north, out double3 up);
            double ratio = math.clamp(math.cos(inclinationDeg * MathConst.Deg2Rad) / math.cos(site.LatitudeDeg * MathConst.Deg2Rad), -1, 1);
            double beta = math.asin(ratio);
            if (LaunchesSouth(site, inclinationDeg)) beta = math.PI_DBL - beta;
            azimuthDeg = OrbitMath.WrapAngle(beta) * MathConst.Rad2Deg;
            double3 direction = math.sin(beta) * east + math.cos(beta) * north;
            return math.normalize(math.cross(up, direction));
        }

        /// <summary>
        /// Next time at or after <paramref name="earliestJd"/> when the pad passes through the target plane
        /// on the wanted branch (north-east or south-bound). The plane's node drifts with J2 meanwhile.
        /// </summary>
        public static double NextLaunchWindow(LaunchSite site, OrbitElements planeAtEpoch, double epochJd, double earliestJd)
        {
            OrbitMath.SecularRates(planeAtEpoch, out double raanDot, out _, out _);
            bool south = LaunchesSouth(site, planeAtEpoch.I * MathConst.Rad2Deg);

            double Crossing(double jd, out bool rightBranch)
            {
                double raan = planeAtEpoch.Raan + raanDot * (jd - epochJd) * MathConst.SecondsPerDay;
                double3 n = PlaneNormal(planeAtEpoch.I, raan);
                double3 r = math.normalize(Astro.EcefToEci(Astro.GeoToEcef(site.LatitudeDeg, site.LongitudeDeg, 0), jd));
                Astro.EnuBasis(r, out _, out double3 north, out _);
                double3 prograde = math.normalize(math.cross(n, r));
                rightBranch = south ? math.dot(prograde, north) < 0 : math.dot(prograde, north) >= 0;
                return math.dot(n, r);
            }

            const double stepDays = 120.0 / MathConst.SecondsPerDay;
            double previous = Crossing(earliestJd, out _);
            double bestJd = earliestJd, bestValue = double.MaxValue;
            for (double jd = earliestJd + stepDays; jd < earliestJd + 2.0; jd += stepDays)
            {
                double value = Crossing(jd, out bool branch);
                if (branch && math.abs(value) < bestValue) { bestValue = math.abs(value); bestJd = jd; }
                if (branch && math.sign(value) != math.sign(previous))
                {
                    double lo = jd - stepDays, hi = jd;
                    for (int k = 0; k < 40; k++)
                    {
                        double mid = 0.5 * (lo + hi);
                        if (math.sign(Crossing(mid, out _)) == math.sign(previous)) lo = mid; else hi = mid;
                    }
                    return 0.5 * (lo + hi);
                }
                previous = value;
            }
            return bestJd; // inclination below the site latitude: closest approach instead
        }

        /// <summary>
        /// Largest payload (kg) the vehicle can put into a circular orbit (with its booster recovered unless it is
        /// the expendable variant). Gravity, drag and steering losses are calibrated against <see cref="RocketFlight"/>.
        /// </summary>
        public static double MaxPayloadKg(LaunchVehicle vehicle, LaunchSite site, double altitudeKm, double inclinationDeg)
        {
            double required = RequiredDeltaV(vehicle, site, altitudeKm, inclinationDeg);
            double lo = 0, hi = vehicle.MaxPayloadSearchKg;
            for (int k = 0; k < 40; k++)
            {
                double mid = 0.5 * (lo + hi);
                if (AvailableDeltaV(vehicle, mid) >= required) lo = mid; else hi = mid;
            }
            return lo;
        }

        /// <summary>Speed the upper stage has left after reaching a 200 km parking orbit with this payload (m/s).</summary>
        public static double DeltaVLeftInParkingOrbit(LaunchVehicle vehicle, LaunchSite site, double inclinationDeg, double payloadKg) =>
            AvailableDeltaV(vehicle, payloadKg) - RequiredDeltaV(vehicle, site, 200, inclinationDeg);

        static double RequiredDeltaV(LaunchVehicle vehicle, LaunchSite site, double altitudeKm, double inclinationDeg)
        {
            double mu = EarthConstants.Mu * 1e9;
            double re = EarthConstants.RadiusKm * 1000.0;
            double insertion = math.min(altitudeKm, RocketFlight.DirectInsertionMaxKm) * 1000.0 + re;
            double target = altitudeKm * 1000.0 + re;

            double ratio = math.clamp(math.cos(inclinationDeg * MathConst.Deg2Rad) / math.cos(site.LatitudeDeg * MathConst.Deg2Rad), -1, 1);
            double rotationGain = EarthConstants.RotationRate * re * math.cos(site.LatitudeDeg * MathConst.Deg2Rad) * ratio;

            double dv = math.sqrt(mu / insertion) + vehicle.AscentLossesMs - rotationGain;
            if (target > insertion + 1)
            {
                double a = 0.5 * (insertion + target);
                dv += math.sqrt(mu * (2 / insertion - 1 / a)) - math.sqrt(mu / insertion);
                if (altitudeKm <= RocketFlight.PayloadSelfRaiseAboveKm)
                    dv += math.sqrt(mu / target) - math.sqrt(mu * (2 / target - 1 / a));
            }
            return dv;
        }

        static double AvailableDeltaV(LaunchVehicle v, double payloadKg)
        {
            double upper = v.SecondDryKg + v.SecondPropKg + payloadKg;
            double liftoff = upper + v.FairingKg + v.BoosterDryKg + v.BoosterPropKg;
            double meco = liftoff - (v.BoosterPropKg - v.LandingReserveKg);
            double first = v.FirstStageEffectiveIsp * LaunchVehicle.G0 * math.log(liftoff / meco);
            double second = v.SecondIsp * LaunchVehicle.G0 * math.log((upper + 0.5 * v.FairingKg) / (v.SecondDryKg + payloadKg));
            return first + second;
        }
    }
}
