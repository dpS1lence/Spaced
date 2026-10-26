using Unity.Mathematics;
using UnityEngine;

namespace Spaced
{
    /// <summary>
    /// Floating origin and the two render scales.
    /// <para>The <b>far scene</b> (Earth, stars, satellite dots, orbit lines) uses 1 unit = 1000 km and is drawn by
    /// the base camera. The <b>local scene</b> (rockets, pads, droneships, satellite models, particles) uses
    /// 1 unit = 1 m on <see cref="LocalLayer"/> and is drawn by an overlay camera with its own depth range.</para>
    /// <para>Both are positioned relative to <see cref="OriginKm"/>, an inertial point that follows whatever the
    /// camera looks at, so everything near the camera keeps full float precision.</para>
    /// </summary>
    public static class WorldFrame
    {
        public const int LocalLayer = 30;
        public const double MetersPerFarUnit = WorldScale.KmPerUnit * 1000.0;

        /// <summary>Inertial position (km) that sits at Unity's origin this frame.</summary>
        public static double3 OriginKm;

        /// <summary>Julian date the origin and every frame-dependent transform were computed for.</summary>
        public static double JulianDate;

        public static Vector3 ToFar(double3 eciKm) => Astro.EciToUnity((eciKm - OriginKm) / WorldScale.KmPerUnit);
        public static Vector3 ToLocal(double3 eciKm) => Astro.EciToUnity((eciKm - OriginKm) * 1000.0);
        public static Vector3 ToLocalMeters(double3 eciMeters) => Astro.EciToUnity(eciMeters - OriginKm * 1000.0);

        /// <summary>Rotation from model space (+Y up, +Z forward) to Unity world for an inertial up/forward pair.</summary>
        public static Quaternion LookRotation(double3 forwardEci, double3 upEci) =>
            Quaternion.LookRotation(Astro.EciToUnity(forwardEci), Astro.EciToUnity(upEci));

        /// <summary>
        /// Rotation of the local east-north-up frame at an Earth-fixed point: model +X = east, +Y = up, +Z = north.
        /// </summary>
        public static Quaternion SurfaceRotation(double3 ecefKm, double jd)
        {
            Astro.EnuBasis(Astro.EcefToEci(ecefKm, jd), out _, out double3 north, out double3 up);
            return LookRotation(north, up);
        }
    }
}
