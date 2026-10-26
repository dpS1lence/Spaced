using Unity.Mathematics;

namespace Spaced
{
    public struct BreakupResult
    {
        public bool Catastrophic;
        public int FragmentsCreated;
    }

    /// <summary>
    /// Simplified NASA Standard Breakup Model (Johnson et al. 2001) for fragments ≥ <see cref="MinFragmentSizeM"/>.
    /// <list type="bullet">
    /// <item>Catastrophic when the projectile's kinetic energy exceeds 40 J per gram of target.</item>
    /// <item>Fragment count N(≥Lc) = 0.1 · M^0.75 · Lc^-1.71.</item>
    /// <item>Area-to-mass ratio is log-normal and shrinks for larger fragments.</item>
    /// <item>Δv is log-normal with mean 0.9·log₁₀(A/m) + 2.9 (m/s), σ = 0.4.</item>
    /// </list>
    /// </summary>
    public sealed class BreakupModel
    {
        public const float MinFragmentSizeM = 0.1f;
        const double CatastrophicThresholdJPerG = 40.0;

        /// <summary>Upper bound on fragments per event, to keep huge collisions affordable.</summary>
        public int MaxFragmentsPerEvent = 2500;
        /// <summary>Total object budget; no fragments are added beyond it.</summary>
        public int MaxObjects = 500_000;

        /// <summary>Fragments still allowed in the current collision sample (set by the simulation each sample).</summary>
        public int FragmentBudget = int.MaxValue;

        Random _rng = new Random(0xC0FFEEu);

        /// <summary>Collides two catalog objects and spawns their debris.</summary>
        public BreakupResult Collide(ObjectCatalog catalog, int a, int b, double relativeSpeedKmS)
        {
            var pa = catalog.Physics[a];
            var pb = catalog.Physics[b];
            int target = pa.MassKg >= pb.MassKg ? a : b;
            int projectile = target == a ? b : a;
            float targetMass = catalog.Physics[target].MassKg;
            float projectileMass = catalog.Physics[projectile].MassKg;

            double speedMs = relativeSpeedKmS * 1000.0;
            double energyPerGram = 0.5 * projectileMass * speedMs * speedMs / (targetMass * 1000.0);
            bool catastrophic = energyPerGram > CatastrophicThresholdJPerG;

            // Mass that takes part in the breakup; for non-catastrophic impacts NASA uses m_p · v² (km/s).
            double ejectaMass = catastrophic
                ? targetMass + projectileMass
                : projectileMass * relativeSpeedKmS * relativeSpeedKmS;

            int count = FragmentCount(ejectaMass);
            int fromTarget = catastrophic ? (int)math.round(count * targetMass / (targetMass + projectileMass)) : count;
            int created = SpawnFragments(catalog, target, fromTarget, catastrophic ? targetMass : ejectaMass);
            created += SpawnFragments(catalog, projectile, count - fromTarget, projectileMass);

            catalog.Kill(projectile);
            if (catastrophic)
            {
                catalog.Kill(target);
            }
            else
            {
                var physics = catalog.Physics[target];
                physics.Flags = (physics.Flags | ObjectFlags.Damaged) & ~(ObjectFlags.Maneuverable | ObjectFlags.StationKeeping);
                catalog.Physics[target] = physics;
            }

            return new BreakupResult { Catastrophic = catastrophic, FragmentsCreated = created };
        }

        /// <summary>Destroys one object as if hit by a kinetic kill vehicle (an anti-satellite test).</summary>
        public BreakupResult Destroy(ObjectCatalog catalog, int index, float interceptorMassKg = 20f)
        {
            float mass = catalog.Physics[index].MassKg;
            int created = SpawnFragments(catalog, index, FragmentCount(mass + interceptorMassKg), mass);
            catalog.Kill(index);
            return new BreakupResult { Catastrophic = true, FragmentsCreated = created };
        }

        int FragmentCount(double massKg)
        {
            double n = 0.1 * math.pow(massKg, 0.75) * math.pow(MinFragmentSizeM, -1.71);
            return (int)math.min(n, MaxFragmentsPerEvent);
        }

        int SpawnFragments(ObjectCatalog catalog, int parent, int count, double parentMassKg)
        {
            if (count <= 0) return 0;
            count = math.min(count, math.min(MaxObjects - catalog.Count, FragmentBudget));
            if (count <= 0) return 0;

            var parentPhysics = catalog.Physics[parent];
            double maxSize = math.max(MinFragmentSizeM * 1.5, math.min(parentPhysics.RadiusM * 2.0, 10.0));
            string name = catalog.Names[parent] + " DEB";
            double3 position = catalog.PositionsKm[parent];
            double3 velocity = catalog.VelocitiesKmS[parent];

            int created = 0;
            for (int k = 0; k < count; k++)
            {
                double size = SamplePowerLawSize(MinFragmentSizeM, maxSize);
                double logSize = math.log10(size);
                double logAreaToMass = Normal(-0.6 - 0.3 * logSize, 0.4);
                double areaToMass = math.pow(10.0, logAreaToMass);
                double area = 0.540424 * size * size;
                double mass = math.min(area / areaToMass, parentMassKg);

                double deltaV = math.min(math.pow(10.0, Normal(0.9 * logAreaToMass + 2.9, 0.4)), 2000.0) / 1000.0;
                double3 fragmentVelocity = velocity + RandomDirection() * deltaV;

                if (!OrbitMath.FromStateVector(position, fragmentVelocity, out var elements)) continue;
                if (elements.PerigeeAltitudeKm < EarthConstants.ReentryAltitudeKm) continue;

                catalog.Add(elements, new ObjectPhysics
                {
                    MassKg = (float)mass,
                    RadiusM = (float)(size * 0.5),
                    BallisticCoeff = (float)math.min(2.2 * areaToMass, 5.0),
                    Category = ObjectCategory.GeneratedDebris,
                    // Debris from your satellites is yours too, so cleanup removes it with them.
                    Flags = ObjectFlags.Alive | (parentPhysics.Flags & ObjectFlags.UserOwned),
                    Model = ModelKind.Debris,
                }, name);
                created++;
            }
            FragmentBudget -= created;
            return created;
        }

        /// <summary>Samples L from the cumulative power law N(&gt;L) ∝ L^-1.71 between min and max.</summary>
        double SamplePowerLawSize(double min, double max)
        {
            const double k = 1.71;
            double u = _rng.NextDouble();
            double tail = math.pow(min / max, k);
            return min * math.pow(1.0 - u * (1.0 - tail), -1.0 / k);
        }

        double Normal(double mean, double sigma)
        {
            double u1 = math.max(_rng.NextDouble(), 1e-12);
            double u2 = _rng.NextDouble();
            return mean + sigma * math.sqrt(-2.0 * math.log(u1)) * math.cos(MathConst.TwoPi * u2);
        }

        double3 RandomDirection()
        {
            double z = _rng.NextDouble(-1.0, 1.0);
            double phi = _rng.NextDouble(0.0, MathConst.TwoPi);
            double s = math.sqrt(1.0 - z * z);
            return new double3(s * math.cos(phi), s * math.sin(phi), z);
        }
    }
}
