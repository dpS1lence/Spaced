using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;

namespace Spaced
{
    public enum ObjectCategory : byte
    {
        Payload,
        Starlink,
        OneWeb,
        Station,
        Navigation,
        RocketBody,
        Debris,
        UserSatellite,
        GeneratedDebris,
    }

    /// <summary>Which 3D model represents an object up close.</summary>
    public enum ModelKind : byte
    {
        Smallsat,
        CubeSat,
        Starlink,
        OneWeb,
        Comsat,
        Navigation,
        Station,
        RocketBody,
        Debris,
    }

    [Flags]
    public enum ObjectFlags : byte
    {
        None = 0,
        Alive = 1 << 0,
        UserOwned = 1 << 1,
        /// <summary>Can perform collision-avoidance manoeuvres.</summary>
        Maneuverable = 1 << 2,
        Damaged = 1 << 3,
        /// <summary>Set by the propagation job; consumed by the simulation to count reentries.</summary>
        Reentered = 1 << 4,
        /// <summary>Thrusters cancel drag (operational satellites).</summary>
        StationKeeping = 1 << 5,
    }

    public struct ObjectPhysics
    {
        public float MassKg;
        /// <summary>Radius used for collision cross-sections, metres.</summary>
        public float RadiusM;
        /// <summary>Cd·A/m in m²/kg.</summary>
        public float BallisticCoeff;
        public ObjectCategory Category;
        public ObjectFlags Flags;
        public ModelKind Model;

        public bool IsAlive => (Flags & ObjectFlags.Alive) != 0;
    }

    /// <summary>
    /// A slow transfer towards target elements: orbit raising and phasing after deployment, or a
    /// deorbit burn sequence. The target itself keeps drifting with J2 while the object closes in.
    /// </summary>
    public struct OrbitManeuver
    {
        public OrbitElements Target;
        public double RemainingSeconds;
        public byte Active;
    }

    /// <summary>
    /// Every simulated object, stored as parallel arrays so Burst jobs can stream through them.
    /// Indices change on <see cref="Compact"/>; use <see cref="Ids"/> to hold on to an object.
    /// </summary>
    public sealed class ObjectCatalog : IDisposable
    {
        public NativeList<OrbitElements> Elements;
        public NativeList<ObjectPhysics> Physics;
        public NativeList<OrbitManeuver> Maneuvers;
        /// <summary>Inertial positions, km. Double precision so close-up views don't jitter.</summary>
        public NativeList<double3> PositionsKm;
        public NativeList<float3> VelocitiesKmS;
        /// <summary>Position relative to the floating origin and packed category/radius. Filled by the renderer.</summary>
        public NativeList<float4> RenderData;

        public readonly List<string> Names = new List<string>();
        public readonly List<int> NoradIds = new List<int>();
        /// <summary>Fleet group (see <see cref="FleetManager"/>) for your satellites, -1 for everything else.</summary>
        public readonly List<int> Groups = new List<int>();
        public readonly List<uint> Ids = new List<uint>();

        readonly Dictionary<uint, int> _indexById = new Dictionary<uint, int>();
        uint _nextId = 1;

        public int Count => Elements.Length;
        public int DeadCount { get; private set; }

        public ObjectCatalog(int initialCapacity = 32768)
        {
            Elements = new NativeList<OrbitElements>(initialCapacity, Allocator.Persistent);
            Physics = new NativeList<ObjectPhysics>(initialCapacity, Allocator.Persistent);
            Maneuvers = new NativeList<OrbitManeuver>(initialCapacity, Allocator.Persistent);
            PositionsKm = new NativeList<double3>(initialCapacity, Allocator.Persistent);
            VelocitiesKmS = new NativeList<float3>(initialCapacity, Allocator.Persistent);
            RenderData = new NativeList<float4>(initialCapacity, Allocator.Persistent);
        }

        public int Add(in OrbitElements elements, ObjectPhysics physics, string name, int noradId = -1, int group = -1)
        {
            physics.Flags |= ObjectFlags.Alive;
            int index = Elements.Length;
            Elements.Add(elements);
            Physics.Add(physics);
            Maneuvers.Add(default);

            OrbitMath.ToStateVector(elements, out double3 r, out double3 v);
            PositionsKm.Add(r);
            VelocitiesKmS.Add((float3)v);
            // Not drawn until the renderer fills it in.
            RenderData.Add(new float4(0, 0, 0, -1));

            uint id = _nextId++;
            Names.Add(name);
            Groups.Add(group);
            NoradIds.Add(noradId);
            Ids.Add(id);
            _indexById[id] = index;
            return index;
        }

        public int IndexOf(uint id) => _indexById.TryGetValue(id, out int index) ? index : -1;

        public void SetManeuver(int index, in OrbitElements target, double durationSeconds)
        {
            Maneuvers[index] = new OrbitManeuver { Target = target, RemainingSeconds = math.max(durationSeconds, 1), Active = 1 };
        }

        public void Kill(int index)
        {
            var physics = Physics[index];
            if (!physics.IsAlive) return;
            physics.Flags &= ~ObjectFlags.Alive;
            Physics[index] = physics;
            RenderData[index] = new float4(0, 0, 0, -1);
            DeadCount++;
        }

        /// <summary>Records objects the propagation job reported as reentered. Returns how many there were.</summary>
        public int CollectReentries(NativeQueue<int> queue, List<int> reenteredIndices)
        {
            reenteredIndices.Clear();
            while (queue.TryDequeue(out int i))
            {
                var physics = Physics[i];
                physics.Flags &= ~ObjectFlags.Reentered;
                Physics[i] = physics;
                reenteredIndices.Add(i);
                DeadCount++;
            }
            return reenteredIndices.Count;
        }

        /// <summary>Removes dead objects, keeping the order of the rest.</summary>
        public void Compact()
        {
            if (DeadCount == 0) return;

            int write = 0;
            _indexById.Clear();
            for (int read = 0; read < Elements.Length; read++)
            {
                if (!Physics[read].IsAlive) continue;
                if (write != read)
                {
                    Elements[write] = Elements[read];
                    Physics[write] = Physics[read];
                    Maneuvers[write] = Maneuvers[read];
                    PositionsKm[write] = PositionsKm[read];
                    VelocitiesKmS[write] = VelocitiesKmS[read];
                    RenderData[write] = RenderData[read];
                    Names[write] = Names[read];
                    NoradIds[write] = NoradIds[read];
                    Groups[write] = Groups[read];
                    Ids[write] = Ids[read];
                }
                _indexById[Ids[write]] = write;
                write++;
            }

            int removed = Elements.Length - write;
            Elements.ResizeUninitialized(write);
            Physics.ResizeUninitialized(write);
            Maneuvers.ResizeUninitialized(write);
            PositionsKm.ResizeUninitialized(write);
            VelocitiesKmS.ResizeUninitialized(write);
            RenderData.ResizeUninitialized(write);
            Names.RemoveRange(write, removed);
            NoradIds.RemoveRange(write, removed);
            Groups.RemoveRange(write, removed);
            Ids.RemoveRange(write, removed);
            DeadCount = 0;
        }

        public void Dispose()
        {
            if (Elements.IsCreated) Elements.Dispose();
            if (Physics.IsCreated) Physics.Dispose();
            if (Maneuvers.IsCreated) Maneuvers.Dispose();
            if (PositionsKm.IsCreated) PositionsKm.Dispose();
            if (VelocitiesKmS.IsCreated) VelocitiesKmS.Dispose();
            if (RenderData.IsCreated) RenderData.Dispose();
        }
    }
}
