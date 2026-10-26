using System;
using NUnit.Framework;
using Unity.Mathematics;

namespace Spaced.Tests
{
    public class LaunchTests
    {
        static readonly double Jd = Astro.JulianDate(new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc));

        static RocketFlight Fly(LaunchSite site, double altitudeKm, double inclinationDeg, double payloadKg)
        {
            var normal = LaunchPlanner.PlaneForImmediateLaunch(site, inclinationDeg, Jd, out _);
            var flight = new RocketFlight(LaunchVehicle.Falcon9, site, Jd, payloadKg, altitudeKm, normal);
            for (int i = 0; i < 6 * 3600 && !flight.Finished; i++) flight.Advance(1.0);
            return flight;
        }

        [TestCase(0, 550, 53)]
        [TestCase(0, 300, 43)]
        [TestCase(2, 500, 97.4)]
        [TestCase(1, 400, 51.6)]
        public void Falcon9_ReachesOrbitAtCapacity_AndLandsBooster(int siteIndex, double altitudeKm, double inclinationDeg)
        {
            var site = LaunchSite.All[siteIndex];
            double payload = LaunchPlanner.MaxPayloadKg(LaunchVehicle.Falcon9, site, altitudeKm, inclinationDeg);
            var flight = Fly(site, altitudeKm, inclinationDeg, payload);

            Assert.AreEqual(FlightPhase.Complete, flight.Phase);
            flight.OrbitShape(out double perigee, out double apogee);
            Assert.AreEqual(altitudeKm, perigee, 25.0);
            Assert.AreEqual(altitudeKm, apogee, 25.0);

            Assert.AreEqual(BoosterPhase.Landed, flight.Booster.Phase);
            Assert.Less(flight.Booster.TouchdownSpeed, 6.0);
            // The droneship was parked exactly where the booster came down.
            Assert.Less(math.distance(flight.Booster.TouchdownEcefKm, flight.DroneshipEcefKm) * 1000.0, 1.0);
        }

        [Test]
        public void Falcon9_FirstStageCutoff_LooksLikeARealFlight()
        {
            var site = LaunchSite.All[0];
            var normal = LaunchPlanner.PlaneForImmediateLaunch(site, 53, Jd, out _);
            var flight = new RocketFlight(LaunchVehicle.Falcon9, site, Jd, 15000, 550, normal);
            while (flight.BoosterAttached) flight.Advance(0.1);

            // Real Starlink missions: MECO around T+2:20–2:40 at ~65–80 km and ~8,000 km/h.
            Assert.That(flight.MissionTime, Is.InRange(130.0, 170.0));
            Assert.That(flight.AltitudeKm, Is.InRange(55.0, 90.0));
            Assert.That(math.length(flight.AirVelocity), Is.InRange(2000.0, 2700.0));
        }

        [Test]
        public void LaunchWindow_PutsThePadInTheTargetPlane()
        {
            var site = LaunchSite.All[0];
            var plane = OrbitMath.FromAltitudes(550, 550, 53, 123, 0, 0);
            double window = LaunchPlanner.NextLaunchWindow(site, plane, Jd, Jd);
            Assert.That(window, Is.InRange(Jd, Jd + 1.0));

            OrbitMath.Advance(ref plane, 0, (window - Jd) * MathConst.SecondsPerDay);
            double3 normal = LaunchPlanner.PlaneNormal(plane.I, plane.Raan);
            double3 pad = math.normalize(Astro.EcefToEci(Astro.GeoToEcef(site.LatitudeDeg, site.LongitudeDeg, 0), window));
            Assert.Less(math.abs(math.dot(normal, pad)), 1e-4);
        }

        [Test]
        public void Capacity_IsInTheRightBallpark()
        {
            var cape = LaunchSite.All[0];
            // SpaceX quotes ~17.4 t to LEO with recovery and ~5.5 t to GTO.
            Assert.That(LaunchPlanner.MaxPayloadKg(LaunchVehicle.Falcon9, cape, 550, 53), Is.InRange(14000.0, 19000.0));
            Assert.That(LaunchPlanner.MaxPayloadKg(LaunchVehicle.Falcon9, cape, 35786, 28.5), Is.InRange(4500.0, 7500.0));
        }

        [Test]
        public void Deployment_PhasesSatellitesIntoTheirSlots()
        {
            var catalog = new ObjectCatalog(16);
            var reentered = new Unity.Collections.NativeQueue<int>(Unity.Collections.Allocator.Persistent);
            try
            {
                var target = OrbitMath.FromAltitudes(550, 550, 53, 40, 0, 90);
                var start = OrbitMath.FromAltitudes(300, 550, 53, 40, 0, 0);
                int index = catalog.Add(start, LaunchService.PhysicsFor(SatelliteClass.Presets[2], true), "test");
                catalog.SetManeuver(index, target, 2 * MathConst.SecondsPerDay);

                var job = new PropagationJob
                {
                    DeltaSeconds = 600,
                    Elements = catalog.Elements.AsArray(),
                    Physics = catalog.Physics.AsArray(),
                    Maneuvers = catalog.Maneuvers.AsArray(),
                    PositionsKm = catalog.PositionsKm.AsArray(),
                    VelocitiesKmS = catalog.VelocitiesKmS.AsArray(),
                    Reentered = reentered.AsParallelWriter(),
                };
                for (int step = 0; step < 3 * 144; step++) job.Execute(index);

                var el = catalog.Elements[index];
                OrbitMath.Advance(ref target, 0, 3 * MathConst.SecondsPerDay);
                Assert.AreEqual(0, catalog.Maneuvers[index].Active);
                Assert.AreEqual(target.A, el.A, 0.5);
                Assert.Less(math.abs(OrbitMath.ShortestAngle(el.ArgP + el.M - target.ArgP - target.M)), 0.01);
            }
            finally
            {
                catalog.Dispose();
                reentered.Dispose();
            }
        }
    }
}
