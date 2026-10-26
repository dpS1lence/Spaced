using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Spaced
{
    /// <summary>
    /// Advances every object by <see cref="DeltaSeconds"/>, applies station-keeping and transfer
    /// manoeuvres, flags reentries, and writes inertial positions and velocities.
    /// </summary>
    [BurstCompile]
    public struct PropagationJob : IJobParallelFor
    {
        public double DeltaSeconds;

        public NativeArray<OrbitElements> Elements;
        public NativeArray<ObjectPhysics> Physics;
        public NativeArray<OrbitManeuver> Maneuvers;
        [WriteOnly] public NativeArray<double3> PositionsKm;
        [WriteOnly] public NativeArray<float3> VelocitiesKmS;
        /// <summary>Indices of objects that reentered during this step.</summary>
        public NativeQueue<int>.ParallelWriter Reentered;

        public void Execute(int index)
        {
            var physics = Physics[index];
            if (!physics.IsAlive) return;

            var el = Elements[index];
            var maneuver = Maneuvers[index];
            bool thrusting = maneuver.Active != 0 || (physics.Flags & ObjectFlags.StationKeeping) != 0;

            if (DeltaSeconds != 0)
            {
                OrbitMath.Advance(ref el, thrusting ? 0 : physics.BallisticCoeff, DeltaSeconds);

                if (maneuver.Active != 0)
                {
                    OrbitMath.Advance(ref maneuver.Target, 0, DeltaSeconds);
                    if (DeltaSeconds > 0)
                    {
                        OrbitMath.BlendToward(ref el, maneuver.Target, math.min(1.0, DeltaSeconds / maneuver.RemainingSeconds));
                        maneuver.RemainingSeconds -= DeltaSeconds;
                        if (maneuver.RemainingSeconds <= 0)
                        {
                            el = maneuver.Target;
                            maneuver.Active = 0;
                        }
                    }
                    Maneuvers[index] = maneuver;
                }
            }
            Elements[index] = el;

            if (el.PerigeeAltitudeKm < EarthConstants.ReentryAltitudeKm)
            {
                physics.Flags = (physics.Flags & ~ObjectFlags.Alive) | ObjectFlags.Reentered;
                Physics[index] = physics;
                Reentered.Enqueue(index);
                return;
            }

            OrbitMath.ToStateVector(el, out double3 r, out double3 v);
            PositionsKm[index] = r;
            VelocitiesKmS[index] = (float3)v;
        }
    }

    /// <summary>Counts live objects per category.</summary>
    [BurstCompile]
    public struct CountCategoriesJob : IJob
    {
        [ReadOnly] public NativeArray<ObjectPhysics> Physics;
        public NativeArray<int> Counts;

        public void Execute()
        {
            for (int c = 0; c < Counts.Length; c++) Counts[c] = 0;
            for (int i = 0; i < Physics.Length; i++)
            {
                var p = Physics[i];
                if (p.IsAlive) Counts[(int)p.Category]++;
            }
        }
    }

    /// <summary>
    /// Converts inertial positions into GPU render data relative to the floating origin:
    /// xyz in far-scene units (1000 km), w = category + radius(m)/1000 (see Satellites.shader).
    /// </summary>
    [BurstCompile]
    public struct RenderDataJob : IJobParallelFor
    {
        public double3 OriginKm;
        [ReadOnly] public NativeArray<double3> PositionsKm;
        [ReadOnly] public NativeArray<ObjectPhysics> Physics;
        [WriteOnly] public NativeArray<float4> RenderData;

        public void Execute(int index)
        {
            var physics = Physics[index];
            if (!physics.IsAlive)
            {
                RenderData[index] = new float4(0, 0, 0, -1);
                return;
            }

            // Inertial → Unity axes: swap Y and Z (see Astro.EciToUnity).
            double3 rel = (PositionsKm[index] - OriginKm) / WorldScale.KmPerUnit;
            float packed = (float)physics.Category + math.clamp(physics.RadiusM, 0f, 999f) / 1000f;
            RenderData[index] = new float4((float)rel.x, (float)rel.z, (float)rel.y, packed);
        }
    }
}
