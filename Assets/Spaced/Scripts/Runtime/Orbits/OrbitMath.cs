using Unity.Mathematics;

namespace Spaced
{
    /// <summary>
    /// Mean orbital elements in the inertial (TEME/ECI) frame.
    /// <see cref="N"/> is kept alongside <see cref="A"/> because SGP4's Brouwer mean motion is not
    /// exactly √(μ/a³); keeping it separate preserves along-track timing for real satellites.
    /// </summary>
    public struct OrbitElements
    {
        /// <summary>Semi-major axis, km.</summary>
        public double A;
        /// <summary>Mean motion, rad/s.</summary>
        public double N;
        public double E;
        /// <summary>Inclination, rad.</summary>
        public double I;
        /// <summary>Right ascension of the ascending node, rad.</summary>
        public double Raan;
        /// <summary>Argument of perigee, rad.</summary>
        public double ArgP;
        /// <summary>Mean anomaly, rad.</summary>
        public double M;

        public double PerigeeAltitudeKm => A * (1.0 - E) - EarthConstants.RadiusKm;
        public double ApogeeAltitudeKm => A * (1.0 + E) - EarthConstants.RadiusKm;
        public double PeriodSeconds => MathConst.TwoPi / N;
    }

    /// <summary>
    /// Orbit propagation: secular J2 + atmospheric drag on mean elements, plus element/state conversions.
    /// Everything here is static and allocation free so Burst jobs can call it.
    /// </summary>
    public static class OrbitMath
    {
        const double MinutesPerDay = 1440.0;

        /// <summary>
        /// Converts SGP4 (Kozai) mean elements, as published in TLE/OMM, into Brouwer mean elements.
        /// This is the same "un-Kozai" step SGP4 performs during initialisation.
        /// </summary>
        public static OrbitElements FromKozai(double meanMotionRevPerDay, double eccentricity,
            double inclinationDeg, double raanDeg, double argPerigeeDeg, double meanAnomalyDeg)
        {
            double xke = 60.0 / math.sqrt(EarthConstants.RadiusKm * EarthConstants.RadiusKm * EarthConstants.RadiusKm / EarthConstants.Mu);
            double k2 = 0.5 * EarthConstants.J2;

            double n0 = meanMotionRevPerDay * MathConst.TwoPi / MinutesPerDay; // rad/min
            double e = math.clamp(eccentricity, 0.0, 0.999);
            double inc = inclinationDeg * MathConst.Deg2Rad;
            double cosI = math.cos(inc);
            double x3thm1 = 3.0 * cosI * cosI - 1.0;
            double beta2 = 1.0 - e * e;
            double beta3 = beta2 * math.sqrt(beta2);

            double a1 = math.pow(xke / n0, 2.0 / 3.0);
            double del1 = 1.5 * k2 * x3thm1 / (a1 * a1 * beta3);
            double a0 = a1 * (1.0 - del1 * (1.0 / 3.0 + del1 * (1.0 + 134.0 / 81.0 * del1)));
            double del0 = 1.5 * k2 * x3thm1 / (a0 * a0 * beta3);
            double nBrouwer = n0 / (1.0 + del0);
            double aBrouwer = a0 / (1.0 - del0);

            return new OrbitElements
            {
                A = aBrouwer * EarthConstants.RadiusKm,
                N = nBrouwer / 60.0,
                E = e,
                I = inc,
                Raan = raanDeg * MathConst.Deg2Rad,
                ArgP = argPerigeeDeg * MathConst.Deg2Rad,
                M = meanAnomalyDeg * MathConst.Deg2Rad,
            };
        }

        /// <summary>Elements for an orbit given perigee/apogee altitudes (km) and angles in degrees.</summary>
        public static OrbitElements FromAltitudes(double perigeeAltKm, double apogeeAltKm, double inclinationDeg,
            double raanDeg, double argPerigeeDeg, double meanAnomalyDeg)
        {
            double rp = EarthConstants.RadiusKm + math.min(perigeeAltKm, apogeeAltKm);
            double ra = EarthConstants.RadiusKm + math.max(perigeeAltKm, apogeeAltKm);
            double a = 0.5 * (rp + ra);
            return new OrbitElements
            {
                A = a,
                N = KeplerMeanMotion(a),
                E = (ra - rp) / (ra + rp),
                I = inclinationDeg * MathConst.Deg2Rad,
                Raan = WrapAngle(raanDeg * MathConst.Deg2Rad),
                ArgP = WrapAngle(argPerigeeDeg * MathConst.Deg2Rad),
                M = WrapAngle(meanAnomalyDeg * MathConst.Deg2Rad),
            };
        }

        public static double KeplerMeanMotion(double aKm) => math.sqrt(EarthConstants.Mu / (aKm * aKm * aKm));

        public static double WrapAngle(double rad)
        {
            rad %= MathConst.TwoPi;
            return rad < 0 ? rad + MathConst.TwoPi : rad;
        }

        /// <summary>Secular J2 drift rates (rad/s) of the node, argument of perigee and mean anomaly.</summary>
        public static void SecularRates(in OrbitElements el, out double raanDot, out double argPDot, out double mDot)
        {
            double p = el.A * (1.0 - el.E * el.E);
            double ratio = EarthConstants.RadiusKm / p;
            double k = 0.75 * EarthConstants.J2 * ratio * ratio * el.N; // ¾·n·J2·(Re/p)²
            double cosI = math.cos(el.I);
            double cos2 = cosI * cosI;

            raanDot = -2.0 * k * cosI;
            argPDot = k * (5.0 * cos2 - 1.0);
            mDot = el.N + k * math.sqrt(1.0 - el.E * el.E) * (3.0 * cos2 - 1.0);
        }

        /// <summary>
        /// Semi-major axis decay rate (km/s) from drag. <paramref name="ballisticCoeff"/> is Cd·A/m in m²/kg.
        /// For eccentric orbits drag acts mostly near perigee, so the perigee density is scaled
        /// by the King-Hele factor √(H / 2πae).
        /// </summary>
        public static double DragDecayRate(in OrbitElements el, double ballisticCoeff)
        {
            if (ballisticCoeff <= 0) return 0;
            double perigeeAlt = el.PerigeeAltitudeKm;
            if (perigeeAlt > 2000.0) return 0;

            double ae = el.A * el.E;
            double rho = Atmosphere.Density(el.E < 0.002 ? el.A - EarthConstants.RadiusKm : perigeeAlt, out double h);
            if (ae > h) rho *= math.sqrt(h / (MathConst.TwoPi * ae));

            // sqrt(μa) [km²/s] · ρ [kg/m³] · B [m²/kg] = km²/(s·m); ×1000 m/km → km/s.
            return -math.sqrt(EarthConstants.Mu * el.A) * rho * ballisticCoeff * 1000.0;
        }

        /// <summary>
        /// Advances mean elements by <paramref name="dt"/> seconds (may be negative).
        /// Drag lowers the apogee first (perigee radius held), then circular orbits shrink as a whole.
        /// </summary>
        public static void Advance(ref OrbitElements el, double ballisticCoeff, double dt)
        {
            double nStart = el.N;

            double decay = DragDecayRate(el, ballisticCoeff);
            if (decay != 0)
            {
                int steps = (int)math.clamp(math.ceil(math.abs(decay * dt) / 2.0), 1, 16);
                double h = dt / steps;
                for (int s = 0; s < steps; s++)
                {
                    if (s > 0) decay = DragDecayRate(el, ballisticCoeff);
                    double rp = el.A * (1.0 - el.E);
                    double aNew = math.max(el.A + decay * h, EarthConstants.RadiusKm);
                    el.N *= math.pow(el.A / aNew, 1.5);
                    el.A = aNew;
                    el.E = math.clamp(1.0 - rp / aNew, 0.0, 0.999);
                    if (el.PerigeeAltitudeKm < EarthConstants.ReentryAltitudeKm) break;
                }
            }

            SecularRates(el, out double raanDot, out double argPDot, out double mDot);
            mDot -= 0.5 * (el.N - nStart); // use the mean motion averaged over the step

            el.Raan = WrapAngle(el.Raan + raanDot * dt);
            el.ArgP = WrapAngle(el.ArgP + argPDot * dt);
            el.M = WrapAngle(el.M + mDot * dt);
        }

        /// <summary>
        /// Moves elements a fraction <paramref name="k"/> of the way towards a target. Angles take the short way
        /// round; the along-track position is blended as argument of latitude, which stays defined for circular orbits.
        /// </summary>
        public static void BlendToward(ref OrbitElements el, in OrbitElements target, double k)
        {
            double argLat = el.ArgP + el.M;
            double targetArgLat = target.ArgP + target.M;

            el.A += (target.A - el.A) * k;
            el.N += (target.N - el.N) * k;
            el.E += (target.E - el.E) * k;
            el.I += (target.I - el.I) * k;
            el.Raan = WrapAngle(el.Raan + ShortestAngle(target.Raan - el.Raan) * k);
            el.ArgP = WrapAngle(el.ArgP + ShortestAngle(target.ArgP - el.ArgP) * k);
            argLat += ShortestAngle(targetArgLat - argLat) * k;
            el.M = WrapAngle(argLat - el.ArgP);
        }

        /// <summary>Angle difference wrapped to (-π, π].</summary>
        public static double ShortestAngle(double delta)
        {
            delta = WrapAngle(delta);
            return delta > math.PI_DBL ? delta - MathConst.TwoPi : delta;
        }

        /// <summary>Solves Kepler's equation M = E - e·sin(E) for the eccentric anomaly.</summary>
        public static double SolveKepler(double meanAnomaly, double e)
        {
            double ecc = e < 0.8 ? meanAnomaly : math.PI_DBL;
            for (int k = 0; k < 12; k++)
            {
                double f = ecc - e * math.sin(ecc) - meanAnomaly;
                double delta = f / (1.0 - e * math.cos(ecc));
                ecc -= delta;
                if (math.abs(delta) < 1e-12) break;
            }
            return ecc;
        }

        /// <summary>Position (km) and velocity (km/s) in the inertial frame.</summary>
        public static void ToStateVector(in OrbitElements el, out double3 position, out double3 velocity)
        {
            double ecc = SolveKepler(el.M, el.E);
            double sinE = math.sin(ecc), cosE = math.cos(ecc);
            double beta = math.sqrt(1.0 - el.E * el.E);
            double denom = 1.0 - el.E * cosE;

            double xp = el.A * (cosE - el.E);
            double yp = el.A * beta * sinE;
            double na = math.sqrt(EarthConstants.Mu / el.A);
            double vxp = -na * sinE / denom;
            double vyp = na * beta * cosE / denom;

            double cosO = math.cos(el.Raan), sinO = math.sin(el.Raan);
            double cosW = math.cos(el.ArgP), sinW = math.sin(el.ArgP);
            double cosI = math.cos(el.I), sinI = math.sin(el.I);

            var p = new double3(cosO * cosW - sinO * sinW * cosI, sinO * cosW + cosO * sinW * cosI, sinW * sinI);
            var q = new double3(-cosO * sinW - sinO * cosW * cosI, -sinO * sinW + cosO * cosW * cosI, cosW * sinI);

            position = xp * p + yp * q;
            velocity = vxp * p + vyp * q;
        }

        /// <summary>
        /// Converts an inertial state vector to elements. Returns false for escape (non-elliptic) orbits.
        /// Near-circular and near-equatorial orbits are handled by measuring from the node line.
        /// </summary>
        public static bool FromStateVector(double3 r, double3 v, out OrbitElements el)
        {
            el = default;
            double mu = EarthConstants.Mu;
            double rMag = math.length(r);
            double v2 = math.lengthsq(v);
            double energy = 0.5 * v2 - mu / rMag;
            if (energy >= 0) return false;

            double3 h = math.cross(r, v);
            double hMag = math.length(h);
            if (hMag < 1e-9) return false;

            double3 eVec = ((v2 - mu / rMag) * r - math.dot(r, v) * v) / mu;
            double e = math.length(eVec);
            if (e >= 0.999) return false;

            double3 w = h / hMag;
            double raan = math.atan2(h.x, -h.y);
            var node = new double3(math.cos(raan), math.sin(raan), 0);
            double3 inPlane = math.cross(w, node);

            double argLat = math.atan2(math.dot(r, inPlane), math.dot(r, node));
            double argP = e > 1e-9 ? math.atan2(math.dot(eVec, inPlane), math.dot(eVec, node)) : 0.0;
            double nu = argLat - argP;
            double eccAnomaly = math.atan2(math.sqrt(1.0 - e * e) * math.sin(nu), e + math.cos(nu));

            double a = -mu / (2.0 * energy);
            el = new OrbitElements
            {
                A = a,
                N = KeplerMeanMotion(a),
                E = e,
                I = math.atan2(math.sqrt(h.x * h.x + h.y * h.y), h.z),
                Raan = WrapAngle(raan),
                ArgP = WrapAngle(argP),
                M = WrapAngle(eccAnomaly - e * math.sin(eccAnomaly)),
            };
            return true;
        }
    }
}
