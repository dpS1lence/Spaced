using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Spaced
{
    public struct SimulationStats
    {
        public int CatalogObjects;
        public int Launched;
        public int Collisions;
        public int CatastrophicCollisions;
        public int FragmentsCreated;
        public int Reentries;
        public int Destroyed;
    }

    public struct SimEvent
    {
        public double JulianDate;
        public string Text;
    }

    /// <summary>
    /// Owns the object catalog and runs the simulation each frame:
    /// clock → propagation job → reentries → collision sampling → breakups.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class SpaceSimulation : MonoBehaviour
    {
        public static readonly string[] DefaultGroups =
        {
            "active", "cosmos-1408-debris", "fengyun-1c-debris", "iridium-33-debris", "cosmos-2251-debris",
        };

        [Tooltip("CelesTrak GP groups to load (see celestrak.org/NORAD/elements).")]
        public string[] CelestrakGroups = DefaultGroups;
        public bool CollisionsEnabled = true;
        [Tooltip("Real seconds between collision samples. Each sample covers all sim time since the previous one.")]
        public float CollisionSampleInterval = 0.1f;
        public int MaxEvents = 60;
        [Tooltip("Collisions resolved per sample; the rest are dropped so a runaway cascade can't stall a frame.")]
        public int MaxCollisionsPerSample = 150;
        [Tooltip("Debris fragments created per sample at most.")]
        public int MaxFragmentsPerSample = 20000;

        public SimClock Clock { get; } = new SimClock();
        public ObjectCatalog Catalog { get; private set; }
        public CollisionSystem Collisions { get; private set; }
        public BreakupModel Breakups { get; } = new BreakupModel();
        public SimulationStats Stats;
        public readonly List<SimEvent> Events = new List<SimEvent>();
        public readonly int[] CategoryCounts = new int[System.Enum.GetValues(typeof(ObjectCategory)).Length];
        public int AliveCount { get; private set; }

        public bool IsLoaded { get; private set; }

        /// <summary>Sim seconds advanced this frame (negative when running backwards).</summary>
        public double LastDeltaSeconds { get; private set; }
        public string DataStatus => _client.Status;

        /// <summary>Stable id of the selected object, or 0.</summary>
        public uint SelectedId { get; set; }

        public int SelectedIndex
        {
            get
            {
                if (SelectedId == 0 || Catalog == null) return -1;
                int index = Catalog.IndexOf(SelectedId);
                return index >= 0 && Catalog.Physics[index].IsAlive ? index : -1;
            }
        }

        readonly CelestrakClient _client = new CelestrakClient();
        readonly List<int> _reentered = new List<int>();
        readonly HashSet<int> _collidedThisSample = new HashSet<int>();
        NativeQueue<int> _reentryQueue;
        NativeArray<int> _categoryCounts;
        double _collisionExposureSeconds;
        float _collisionTimer;
        float _countTimer;

        void Awake()
        {
            Catalog = new ObjectCatalog();
            Collisions = new CollisionSystem();
            _reentryQueue = new NativeQueue<int>(Allocator.Persistent);
            _categoryCounts = new NativeArray<int>(CategoryCounts.Length, Allocator.Persistent);
        }

        IEnumerator Start()
        {
            yield return _client.FetchGroups(CelestrakGroups, AddCatalogRecords);
            IsLoaded = true;
        }

        void OnDestroy()
        {
            Catalog?.Dispose();
            Collisions?.Dispose();
            if (_reentryQueue.IsCreated) _reentryQueue.Dispose();
            if (_categoryCounts.IsCreated) _categoryCounts.Dispose();
        }

        void AddCatalogRecords(List<OmmRecord> records)
        {
            foreach (var record in records)
            {
                var elements = OrbitMath.FromKozai(record.MEAN_MOTION, record.ECCENTRICITY, record.INCLINATION,
                    record.RA_OF_ASC_NODE, record.ARG_OF_PERICENTER, record.MEAN_ANOMALY);
                var category = ObjectClassifier.Classify(record.OBJECT_NAME ?? "");
                var physics = ObjectClassifier.PhysicsFor(category, record.BSTAR, elements.A);

                double epochJd;
                try { epochJd = record.EpochJulianDate; }
                catch (System.FormatException) { continue; }

                // Bring each element set from its own epoch to the current sim time.
                OrbitMath.Advance(ref elements, physics.BallisticCoeff, (Clock.JulianDate - epochJd) * MathConst.SecondsPerDay);
                if (elements.PerigeeAltitudeKm < EarthConstants.ReentryAltitudeKm) continue;

                Catalog.Add(elements, physics, record.OBJECT_NAME, record.NORAD_CAT_ID);
            }
            Stats.CatalogObjects = Catalog.Count;
            Log($"Loaded {Catalog.Count:N0} tracked objects");
        }

        void Update()
        {
            // After a script reload in Play mode runtime state is gone; stay quiet instead of throwing every frame.
            if (Catalog == null || !Catalog.Elements.IsCreated) return;
            float realDt = Time.unscaledDeltaTime;
            double dt = Clock.Tick(realDt);
            LastDeltaSeconds = dt;

            Propagate(dt);

            int reentries = Catalog.CollectReentries(_reentryQueue, _reentered);
            if (reentries > 0)
            {
                Stats.Reentries += reentries;
                if (reentries <= 3)
                {
                    foreach (int index in _reentered)
                        if (Catalog.Physics[index].Category != ObjectCategory.GeneratedDebris)
                            Log($"{Catalog.Names[index]} reentered");
                }
                else
                {
                    Log($"{reentries:N0} objects reentered");
                }
            }

            if (dt > 0) _collisionExposureSeconds += dt;
            _collisionTimer += realDt;
            if (CollisionsEnabled && _collisionTimer >= CollisionSampleInterval && _collisionExposureSeconds > 0)
            {
                SampleCollisions(_collisionExposureSeconds);
                _collisionExposureSeconds = 0;
                _collisionTimer = 0;
            }

            if (Catalog.DeadCount > Mathf.Max(2000, Catalog.Count / 20))
                Catalog.Compact();

            _countTimer -= realDt;
            if (_countTimer <= 0)
            {
                _countTimer = 0.25f;
                CountCategories();
            }

        }

        void Propagate(double dt)
        {
            if (Catalog.Count == 0) return;
            new PropagationJob
            {
                DeltaSeconds = dt,
                Elements = Catalog.Elements.AsArray(),
                Physics = Catalog.Physics.AsArray(),
                Maneuvers = Catalog.Maneuvers.AsArray(),
                PositionsKm = Catalog.PositionsKm.AsArray(),
                VelocitiesKmS = Catalog.VelocitiesKmS.AsArray(),
                Reentered = _reentryQueue.AsParallelWriter(),
            }.Schedule(Catalog.Count, 256).Complete();
        }

        void SampleCollisions(double exposureSeconds)
        {
            var hits = Collisions.Sample(Catalog, exposureSeconds);
            if (hits.Length == 0) return;

            _collidedThisSample.Clear();
            Breakups.FragmentBudget = MaxFragmentsPerSample;
            int resolved = 0, fragments = 0;
            foreach (var hit in hits)
            {
                if (resolved >= MaxCollisionsPerSample) break;
                if (_collidedThisSample.Contains(hit.A) || _collidedThisSample.Contains(hit.B)) continue;
                if (!Catalog.Physics[hit.A].IsAlive || !Catalog.Physics[hit.B].IsAlive) continue;
                _collidedThisSample.Add(hit.A);
                _collidedThisSample.Add(hit.B);

                string nameA = Catalog.Names[hit.A];
                string nameB = Catalog.Names[hit.B];
                var result = Breakups.Collide(Catalog, hit.A, hit.B, hit.RelativeSpeedKmS);
                Stats.Collisions++;
                if (result.Catastrophic) Stats.CatastrophicCollisions++;
                Stats.FragmentsCreated += result.FragmentsCreated;
                fragments += result.FragmentsCreated;
                if (++resolved <= 3)
                    Log($"{(result.Catastrophic ? "Catastrophic collision" : "Collision")}: {nameA} × {nameB} " +
                        $"at {hit.RelativeSpeedKmS:0.0} km/s → {result.FragmentsCreated} fragments");
            }
            if (resolved > 3) Log($"{resolved} collisions this step → {fragments:N0} fragments in total");
            Breakups.FragmentBudget = int.MaxValue;
        }

        /// <summary>Breaks up an object as if hit by an anti-satellite weapon.</summary>
        public void DestroyObject(int index)
        {
            if (index < 0 || !Catalog.Physics[index].IsAlive) return;
            string name = Catalog.Names[index];
            var result = Breakups.Destroy(Catalog, index);
            Stats.Destroyed++;
            Stats.FragmentsCreated += result.FragmentsCreated;
            Log($"ASAT strike on {name} → {result.FragmentsCreated} fragments");
        }

        public void RecordLaunch(int count, string description)
        {
            Stats.Launched += count;
            Log($"Launched {count:N0} × {description}");
        }

        public void Log(string text)
        {
            Events.Insert(0, new SimEvent { JulianDate = Clock.JulianDate, Text = text });
            if (Events.Count > MaxEvents) Events.RemoveAt(Events.Count - 1);
        }

        void CountCategories()
        {
            new CountCategoriesJob { Physics = Catalog.Physics.AsArray(), Counts = _categoryCounts }.Run();
            int alive = 0;
            for (int c = 0; c < CategoryCounts.Length; c++)
            {
                CategoryCounts[c] = _categoryCounts[c];
                alive += CategoryCounts[c];
            }
            AliveCount = alive;
        }
    }

    public static class ShaderIds
    {
        public static readonly int SunDirection = Shader.PropertyToID("_SpacedSunDir");
        public static readonly int Positions = Shader.PropertyToID("_Positions");
        public static readonly int Palette = Shader.PropertyToID("_Palette");
        public static readonly int SelectedIndex = Shader.PropertyToID("_SelectedIndex");
        public static readonly int PointSize = Shader.PropertyToID("_PointSize");
        public static readonly int DayTex = Shader.PropertyToID("_DayTex");
        public static readonly int NightTex = Shader.PropertyToID("_NightTex");
        public static readonly int MainTex = Shader.PropertyToID("_MainTex");
        public static readonly int Color = Shader.PropertyToID("_Color");
    }
}
