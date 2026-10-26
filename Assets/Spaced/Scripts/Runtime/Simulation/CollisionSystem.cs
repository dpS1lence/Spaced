using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace Spaced
{
    public struct CollisionCandidate
    {
        public int A;
        public int B;
        public float RelativeSpeedKmS;
    }

    /// <summary>
    /// Statistical collision model ("cube" method, Liou 2006, used by NASA's LEGEND debris model).
    /// Space is cut into cubes; for every pair of objects that share a cube at the sampled instant,
    /// the probability that they collide during the sample interval Δt is P = v_rel · σ · Δt / U,
    /// with σ = π(r₁ + r₂)² and U the cube volume. This works at any time warp, unlike distance checks,
    /// which cannot catch two objects passing at 10 km/s.
    /// </summary>
    public sealed class CollisionSystem : IDisposable
    {
        public float CellSizeKm = 10f;
        /// <summary>Fraction of collisions avoided when at least one object can manoeuvre.</summary>
        public float AvoidanceEfficiency = 0.95f;
        /// <summary>Multiplies every probability. 1 is physical; raise it to see cascades sooner.</summary>
        public float ProbabilityScale = 1f;
        public int MaxPairsPerCell = 256;

        /// <summary>Expected collisions per year from the most recent sample.</summary>
        public double ExpectedPerYear { get; private set; }

        NativeArray<CellEntry> _entries;
        NativeList<CollisionCandidate> _hits = new NativeList<CollisionCandidate>(64, Allocator.Persistent);
        NativeArray<double> _expected = new NativeArray<double>(1, Allocator.Persistent);
        uint _seed = 0x9E3779B9u;

        /// <summary>Samples the current positions as representing <paramref name="intervalSeconds"/> of exposure.</summary>
        public NativeList<CollisionCandidate> Sample(ObjectCatalog catalog, double intervalSeconds)
        {
            _hits.Clear();
            int count = catalog.Count;
            if (count < 2 || intervalSeconds <= 0) return _hits;

            if (!_entries.IsCreated || _entries.Length < count)
            {
                if (_entries.IsCreated) _entries.Dispose();
                _entries = new NativeArray<CellEntry>(math.ceilpow2(count), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            var entries = _entries.GetSubArray(0, count);

            _expected[0] = 0;
            _seed = _seed * 1664525u + 1013904223u;

            var keys = new CellKeyJob
            {
                CellSizeKm = CellSizeKm,
                Positions = catalog.PositionsKm.AsArray(),
                Physics = catalog.Physics.AsArray(),
                Entries = entries,
            }.Schedule(count, 1024);

            var sort = new SortJob { Entries = entries }.Schedule(keys);

            new CubeJob
            {
                Entries = entries,
                Velocities = catalog.VelocitiesKmS.AsArray(),
                Physics = catalog.Physics.AsArray(),
                IntervalSeconds = intervalSeconds,
                CellVolumeKm3 = (double)CellSizeKm * CellSizeKm * CellSizeKm,
                AvoidanceEfficiency = AvoidanceEfficiency,
                ProbabilityScale = ProbabilityScale,
                MaxPairsPerCell = MaxPairsPerCell,
                Seed = _seed | 1u,
                Hits = _hits,
                Expected = _expected,
            }.Schedule(sort).Complete();

            ExpectedPerYear = _expected[0] / intervalSeconds * MathConst.SecondsPerYear;
            return _hits;
        }

        public void Dispose()
        {
            if (_entries.IsCreated) _entries.Dispose();
            if (_hits.IsCreated) _hits.Dispose();
            if (_expected.IsCreated) _expected.Dispose();
        }

        struct CellEntry : IComparable<CellEntry>
        {
            public long Key;
            public int Index;

            public int CompareTo(CellEntry other)
            {
                int c = Key.CompareTo(other.Key);
                return c != 0 ? c : Index.CompareTo(other.Index);
            }
        }

        [BurstCompile]
        struct CellKeyJob : IJobParallelFor
        {
            public float CellSizeKm;
            [ReadOnly] public NativeArray<double3> Positions;
            [ReadOnly] public NativeArray<ObjectPhysics> Physics;
            [WriteOnly] public NativeArray<CellEntry> Entries;

            public void Execute(int index)
            {
                long key = long.MaxValue;
                if (Physics[index].IsAlive)
                {
                    // 16 bits per axis: ±32k cells, i.e. ±327,000 km with 10 km cells.
                    int3 cell = math.clamp((int3)math.floor(Positions[index] / CellSizeKm), -32767, 32767) + 32768;
                    key = ((long)cell.x << 32) | ((long)cell.y << 16) | (long)cell.z;
                }
                Entries[index] = new CellEntry { Key = key, Index = index };
            }
        }

        [BurstCompile]
        struct SortJob : IJob
        {
            public NativeArray<CellEntry> Entries;
            public void Execute() => Entries.Sort();
        }

        [BurstCompile]
        struct CubeJob : IJob
        {
            [ReadOnly] public NativeArray<CellEntry> Entries;
            [ReadOnly] public NativeArray<float3> Velocities;
            [ReadOnly] public NativeArray<ObjectPhysics> Physics;
            public double IntervalSeconds;
            public double CellVolumeKm3;
            public float AvoidanceEfficiency;
            public float ProbabilityScale;
            public int MaxPairsPerCell;
            public uint Seed;

            public NativeList<CollisionCandidate> Hits;
            public NativeArray<double> Expected;

            public void Execute()
            {
                var rng = new Random(Seed);
                double expected = 0;
                int n = Entries.Length;
                int start = 0;

                while (start < n)
                {
                    long key = Entries[start].Key;
                    if (key == long.MaxValue) break; // dead objects sort last

                    int end = start + 1;
                    while (end < n && Entries[end].Key == key) end++;
                    int occupants = end - start;

                    if (occupants > 1)
                    {
                        long pairs = (long)occupants * (occupants - 1) / 2;
                        if (pairs <= MaxPairsPerCell)
                        {
                            for (int a = start; a < end; a++)
                                for (int b = a + 1; b < end; b++)
                                    Evaluate(Entries[a].Index, Entries[b].Index, 1.0, ref rng, ref expected);
                        }
                        else
                        {
                            // Crowded cell (fresh debris cloud, launch stack): sample pairs and reweight.
                            double weight = (double)pairs / MaxPairsPerCell;
                            for (int k = 0; k < MaxPairsPerCell; k++)
                            {
                                int a = rng.NextInt(start, end);
                                int b = rng.NextInt(start, end - 1);
                                if (b >= a) b++;
                                Evaluate(Entries[a].Index, Entries[b].Index, weight, ref rng, ref expected);
                            }
                        }
                    }

                    start = end;
                }

                Expected[0] = expected;
            }

            void Evaluate(int i, int j, double weight, ref Random rng, ref double expected)
            {
                float relativeSpeed = math.distance(Velocities[i], Velocities[j]);
                if (relativeSpeed < 1e-3f) return; // co-orbiting (docked modules, same launch)

                var pi = Physics[i];
                var pj = Physics[j];
                double radiusKm = (pi.RadiusM + pj.RadiusM) * 0.001;
                double crossSection = math.PI_DBL * radiusKm * radiusKm;
                double p = relativeSpeed * crossSection * IntervalSeconds / CellVolumeKm3 * weight * ProbabilityScale;

                if (((pi.Flags | pj.Flags) & ObjectFlags.Maneuverable) != 0)
                    p *= 1.0 - AvoidanceEfficiency;

                expected += p;
                if (rng.NextDouble() < p)
                    Hits.Add(new CollisionCandidate { A = i, B = j, RelativeSpeedKmS = relativeSpeed });
            }
        }
    }
}
