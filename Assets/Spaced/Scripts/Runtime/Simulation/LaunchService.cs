using System;
using System.Collections.Generic;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace Spaced
{
    [Serializable]
    public struct SatelliteClass
    {
        public string Name;
        public float MassKg;
        /// <summary>Overall span, metres (sets the collision cross-section).</summary>
        public float SizeM;
        /// <summary>Average area facing the flow, m² (sets drag).</summary>
        public float DragAreaM2;
        public ModelKind Model;

        public float BallisticCoeff => 2.2f * DragAreaM2 / MassKg;

        public static readonly SatelliteClass[] Presets =
        {
            new SatelliteClass { Name = "CubeSat (3U)", MassKg = 4f, SizeM = 0.34f, DragAreaM2 = 0.03f, Model = ModelKind.CubeSat },
            new SatelliteClass { Name = "Smallsat", MassKg = 150f, SizeM = 2f, DragAreaM2 = 2f, Model = ModelKind.Smallsat },
            new SatelliteClass { Name = "Starlink-class", MassKg = 800f, SizeM = 8f, DragAreaM2 = 12f, Model = ModelKind.Starlink },
            new SatelliteClass { Name = "Large comsat", MassKg = 6000f, SizeM = 30f, DragAreaM2 = 40f, Model = ModelKind.Comsat },
            new SatelliteClass { Name = "Space station", MassKg = 420000f, SizeM = 100f, DragAreaM2 = 1600f, Model = ModelKind.Station },
        };
    }

    /// <summary>One slot of a planned constellation: the orbit a satellite should end up in.</summary>
    [System.Serializable]
    public struct OrbitSlot
    {
        public OrbitElements Elements;
        public int Plane;
    }

    /// <summary>Creates satellites: planned slots, instant placement, rocket deployment and cleanup.</summary>
    public static class LaunchService
    {
        /// <summary>
        /// Walker-delta constellation i:T/P/F at <paramref name="epochJd"/>. Planes are spread evenly in RAAN,
        /// satellites evenly within each plane, adjacent planes offset by F·360°/T. <paramref name="shellSpreadKm"/>
        /// stacks planes at slightly different altitudes, as real constellations do to avoid crossing points.
        /// </summary>
        public static List<OrbitSlot> PlanWalker(double altitudeKm, double inclinationDeg, int total, int planes,
            int phasing, double shellSpreadKm)
        {
            planes = math.clamp(planes, 1, math.max(total, 1));
            int perPlane = (int)math.ceil(total / (double)planes);
            var slots = new List<OrbitSlot>(total);
            for (int p = 0; p < planes && slots.Count < total; p++)
            {
                double raan = 360.0 * p / planes;
                double altitude = altitudeKm + (planes > 1 ? (p / (planes - 1.0) - 0.5) * shellSpreadKm : 0);
                for (int s = 0; s < perPlane && slots.Count < total; s++)
                {
                    double anomaly = 360.0 * s / perPlane + 360.0 * phasing * p / math.max(total, 1);
                    slots.Add(new OrbitSlot
                    {
                        Elements = OrbitMath.FromAltitudes(altitude, altitude, inclinationDeg, raan, 0, anomaly),
                        Plane = p,
                    });
                }
            }
            return slots;
        }

        /// <summary>Puts satellites straight into their slots (no rocket).</summary>
        public static int PlaceInstantly(ObjectCatalog catalog, SatelliteClass satClass, IReadOnlyList<OrbitSlot> slots,
            bool maneuverable, string namePrefix, int maxObjects, int group = -1)
        {
            var physics = PhysicsFor(satClass, maneuverable);
            int count = math.min(slots.Count, maxObjects - catalog.Count);
            for (int i = 0; i < count; i++)
                catalog.Add(slots[i].Elements, physics, $"{namePrefix}-{i + 1}", -1, group);
            return count;
        }

        /// <summary>
        /// Releases satellites from a rocket's upper stage. They leave as a "train" with small separation
        /// velocities, then spend <paramref name="transferDays"/> raising and phasing into their slots,
        /// like Starlink's krypton-thruster orbit raising after deployment.
        /// </summary>
        public static List<int> Deploy(ObjectCatalog catalog, SatelliteClass satClass, double3 stagePositionKm,
            double3 stageVelocityKmS, IReadOnlyList<OrbitElements> targets, bool maneuverable, string namePrefix,
            int firstNumber, double transferDays, double catchUpSeconds, ref Random rng, int group = -1)
        {
            var physics = PhysicsFor(satClass, maneuverable);
            double3 along = math.normalize(stageVelocityKmS);
            var added = new List<int>(targets.Count);
            for (int i = 0; i < targets.Count; i++)
            {
                // Spring separation: ~0.3 m/s, spread mostly along the track so the train strings out.
                double3 kick = (along * rng.NextDouble(-1.0, 1.0) + rng.NextDouble3Direction() * 0.3) * 0.0003;
                if (!OrbitMath.FromStateVector(stagePositionKm, stageVelocityKmS + kick, out var elements)) continue;
                // The rocket can lag the clock at extreme warp; bring the new orbit up to the current time.
                OrbitMath.Advance(ref elements, 0, catchUpSeconds);
                int index = catalog.Add(elements, physics, $"{namePrefix}-{firstNumber + i}", -1, group);
                catalog.SetManeuver(index, targets[i], transferDays * MathConst.SecondsPerDay * rng.NextDouble(0.8, 1.2));
                added.Add(index);
            }
            return added;
        }

        /// <summary>
        /// Controlled deorbit of every user satellite: perigee is lowered into the atmosphere over a day or two
        /// and they burn up. Returns how many were commanded.
        /// </summary>
        public static int DeorbitUserSatellites(ObjectCatalog catalog, ref Random rng)
        {
            int count = 0;
            for (int i = 0; i < catalog.Count; i++)
            {
                var physics = catalog.Physics[i];
                if (!physics.IsAlive || (physics.Flags & ObjectFlags.UserOwned) == 0) continue;

                var target = catalog.Elements[i];
                double apogeeRadius = target.A * (1 + target.E);
                double perigeeRadius = EarthConstants.RadiusKm + 60.0;
                target.A = 0.5 * (apogeeRadius + perigeeRadius);
                target.E = (apogeeRadius - perigeeRadius) / (apogeeRadius + perigeeRadius);
                target.N = OrbitMath.KeplerMeanMotion(target.A);

                physics.Flags &= ~ObjectFlags.StationKeeping;
                catalog.Physics[i] = physics;
                catalog.SetManeuver(i, target, MathConst.SecondsPerDay * rng.NextDouble(0.5, 2.0));
                count++;
            }
            return count;
        }

        /// <summary>Removes every object matching the predicate immediately. Returns how many were removed.</summary>
        public static int Remove(ObjectCatalog catalog, Func<ObjectPhysics, bool> predicate)
        {
            int count = 0;
            for (int i = 0; i < catalog.Count; i++)
            {
                var physics = catalog.Physics[i];
                if (!physics.IsAlive || !predicate(physics)) continue;
                catalog.Kill(i);
                count++;
            }
            return count;
        }

        public static ObjectPhysics PhysicsFor(SatelliteClass satClass, bool maneuverable) => new ObjectPhysics
        {
            MassKg = satClass.MassKg,
            RadiusM = satClass.SizeM * 0.5f,
            BallisticCoeff = satClass.BallisticCoeff,
            Category = ObjectCategory.UserSatellite,
            Model = satClass.Model,
            Flags = ObjectFlags.Alive | ObjectFlags.UserOwned
                    | (maneuverable ? ObjectFlags.Maneuverable | ObjectFlags.StationKeeping : ObjectFlags.None),
        };

        static double3 NextDouble3Direction(this ref Random rng)
        {
            double z = rng.NextDouble(-1.0, 1.0);
            double phi = rng.NextDouble(0.0, MathConst.TwoPi);
            double s = math.sqrt(1.0 - z * z);
            return new double3(s * math.cos(phi), s * math.sin(phi), z);
        }
    }
}
