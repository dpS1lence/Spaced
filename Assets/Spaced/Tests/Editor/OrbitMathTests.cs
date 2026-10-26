using System;
using NUnit.Framework;
using Unity.Mathematics;

namespace Spaced.Tests
{
    public class OrbitMathTests
    {
        [Test]
        public void Gmst_MatchesVallado()
        {
            // Vallado, example 3-5: 1992-08-20 12:14 UT1 → 152.578787810°.
            double jd = Astro.JulianDate(new DateTime(1992, 8, 20, 12, 14, 0, DateTimeKind.Utc));
            Assert.AreEqual(152.578787810, Astro.Gmst(jd) * MathConst.Rad2Deg, 1e-5);
        }

        [Test]
        public void SunDirection_MatchesVallado()
        {
            // Vallado, example 5-1: 2006-04-02 00:00 UTC.
            double3 sun = Astro.SunDirection(Astro.JulianDate(new DateTime(2006, 4, 2, 0, 0, 0, DateTimeKind.Utc)));
            double3 expected = math.normalize(new double3(146186178, 28788972, 12481021));
            Assert.Less(math.degrees(math.acos(math.clamp(math.dot(sun, expected), -1, 1))), 0.01);
        }

        [Test]
        public void Propagation_StaysCloseToSgp4Reference()
        {
            // SGP4 verification object 00005 (Vallado tcppver.out). Mean-element J2 propagation ignores
            // short-period terms, so a few km of difference is expected.
            var el = OrbitMath.FromKozai(10.82419157, 0.1859667, 34.2682, 348.7242, 331.7664, 19.3264);
            OrbitMath.ToStateVector(el, out double3 r0, out _);
            Assert.Less(math.distance(r0, new double3(7022.46529266, -1400.08296755, 0.03995155)), 20.0);

            OrbitMath.Advance(ref el, 0, 360 * 60);
            OrbitMath.ToStateVector(el, out double3 r360, out _);
            Assert.Less(math.distance(r360, new double3(-7154.03120202, -3783.17682504, -3536.19412294)), 20.0);
        }

        [Test]
        public void StateVector_RoundTrips()
        {
            var random = new System.Random(1);
            for (int k = 0; k < 2000; k++)
            {
                var el = OrbitMath.FromAltitudes(200 + random.NextDouble() * 2000, 200 + random.NextDouble() * 40000,
                    random.NextDouble() * 180, random.NextDouble() * 360, random.NextDouble() * 360, random.NextDouble() * 360);
                if (k % 4 == 0) el.E = 0;
                if (k % 7 == 0) el.I = 0;

                OrbitMath.ToStateVector(el, out double3 r, out double3 v);
                Assert.IsTrue(OrbitMath.FromStateVector(r, v, out var back));
                OrbitMath.ToStateVector(back, out double3 r2, out double3 v2);
                Assert.Less(math.distance(r, r2), 1e-3);
                Assert.Less(math.distance(v, v2), 1e-6);
            }
        }

        [Test]
        public void SunSynchronousOrbit_PrecessesOneDegreePerDay()
        {
            var el = OrbitMath.FromAltitudes(800, 800, 98.6, 0, 0, 0);
            OrbitMath.SecularRates(el, out double raanDot, out _, out _);
            Assert.AreEqual(0.9856, raanDot * MathConst.Rad2Deg * MathConst.SecondsPerDay, 0.01);
        }

        [Test]
        public void Drag_BringsLowCubeSatDownWithinAYear()
        {
            var el = OrbitMath.FromAltitudes(400, 400, 51.6, 0, 0, 0);
            double t = 0;
            while (el.PerigeeAltitudeKm > EarthConstants.ReentryAltitudeKm && t < 5 * MathConst.SecondsPerYear)
            {
                OrbitMath.Advance(ref el, SatelliteClass.Presets[0].BallisticCoeff, MathConst.SecondsPerDay);
                t += MathConst.SecondsPerDay;
            }
            Assert.Less(t, MathConst.SecondsPerYear);
            Assert.Greater(t, 30 * MathConst.SecondsPerDay);
        }
    }
}
