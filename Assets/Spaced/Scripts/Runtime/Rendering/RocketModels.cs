using System.Collections.Generic;
using UnityEngine;

namespace Spaced
{
    /// <summary>
    /// Procedural Falcon 9 Block 5 in metres. +Y points up the vehicle; y = 0 is the booster's engine exit plane.
    /// Parts that move independently (legs, grid fins, fairing halves) are separate meshes with their own pivots.
    /// </summary>
    public static class RocketModels
    {
        public const float BoosterTankTop = 41.2f;
        public const float InterstageTop = Falcon9.BoosterLengthM;
        /// <summary>Second stage origin (MVac exit plane) in stack coordinates: inside the interstage.</summary>
        public const float SecondStageBase = 42.0f;
        public const float FairingBase = 13.5f;   // relative to the second stage origin
        public const float FairingLength = 12.6f;
        public const float LegHingeHeight = 2.2f;
        public const float LegLength = 9.6f;
        public const float GridFinHeight = 46.4f;

        static SurfaceMesh _booster, _secondStage, _nozzle, _leg, _gridFin, _payload, _plume;
        static SurfaceMesh[] _fairingHalves;

        /// <summary>First stage: nine Merlins in the octaweb, sooty tank, raceway, black interstage.</summary>
        public static SurfaceMesh Booster()
        {
            if (_booster != null) return _booster;
            var b = new MeshBuilder();
            var merlin = SpacecraftModels.Nozzle(1.3f, 0.46f, 0.17f);
            b.Lathe(new Vector3(0, 1.3f, 0), Vector3.down, merlin, 16, Surface.EngineBell);
            for (int i = 0; i < 8; i++)
            {
                float angle = i / 8f * Mathf.PI * 2f;
                b.Lathe(new Vector3(Mathf.Cos(angle) * 1.25f, 1.3f, Mathf.Sin(angle) * 1.25f), Vector3.down, merlin, 16, Surface.EngineBell);
            }
            b.Cylinder(new Vector3(0, 1.25f, 0), new Vector3(0, 1.45f, 0), 1.75f, 1.75f, 32, Surface.BlackComposite); // heat shield
            b.Lathe(Vector3.zero, Vector3.up, new[] { new Vector2(1.4f, 1.92f), new Vector2(2.6f, 1.86f), new Vector2(2.8f, 1.83f) }, 40, Surface.EngineBell); // octaweb skirt
            b.Lathe(Vector3.zero, Vector3.up, new[] { new Vector2(2.8f, 1.83f), new Vector2(BoosterTankTop, 1.83f) }, 48, Surface.BoosterPaint, false, false, 1f);
            b.Lathe(Vector3.zero, Vector3.up, new[] { new Vector2(BoosterTankTop, 1.83f), new Vector2(InterstageTop, 1.83f) }, 48, Surface.BlackComposite, false, true);
            b.Box(new Vector3(1.88f, 22f, 0), new Vector3(0.18f, 38f, 0.35f), Surface.WhitePaint); // raceway
            for (int i = 0; i < 4; i++)
            {
                float angle = (i * 90f + 45f) * Mathf.Deg2Rad;
                var dir = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
                b.Box(dir * 1.86f + Vector3.up * 44.5f, new Vector3(0.3f, 0.4f, 0.3f), Quaternion.LookRotation(dir), Surface.Aluminium); // cold-gas thrusters
                b.Box(dir * 1.9f + Vector3.up * 1.9f, new Vector3(0.5f, 0.8f, 0.5f), Quaternion.LookRotation(dir), Surface.BlackComposite); // leg hinges
            }
            return _booster = b.Build("Falcon 9 Booster");
        }

        /// <summary>A landing leg in its own frame: hinge at the origin, extending up +Y (stowed against the tank).</summary>
        public static SurfaceMesh Leg()
        {
            if (_leg != null) return _leg;
            var b = new MeshBuilder();
            b.Box(new Vector3(0, LegLength * 0.5f, 0.12f), new Vector3(0.75f, LegLength, 0.25f), Surface.BlackComposite, 1f);
            b.Box(new Vector3(0, LegLength * 0.35f, 0.32f), new Vector3(0.25f, LegLength * 0.6f, 0.2f), Surface.Aluminium);
            b.Box(new Vector3(0, -0.15f, 0.25f), new Vector3(0.9f, 0.3f, 0.7f), Surface.Aluminium); // foot
            return _leg = b.Build("Landing Leg");
        }

        /// <summary>
        /// Titanium grid fin: a waffle lattice whose cells line up with the airflow along the rocket axis.
        /// Hinge at the origin; deployed it sticks straight out along +Z, horizontal.
        /// </summary>
        public static SurfaceMesh GridFin()
        {
            if (_gridFin != null) return _gridFin;
            var b = new MeshBuilder();
            const float width = 1.2f, length = 1.5f, depth = 0.33f, wall = 0.05f, root = 0.4f;
            b.Box(new Vector3(0, 0, root * 0.5f), new Vector3(0.22f, 0.26f, root), Surface.Steel);
            b.Box(new Vector3(-width * 0.5f, 0, root + length * 0.5f), new Vector3(wall * 2f, depth, length), Surface.Titanium);
            b.Box(new Vector3(width * 0.5f, 0, root + length * 0.5f), new Vector3(wall * 2f, depth, length), Surface.Titanium);
            b.Box(new Vector3(0, 0, root), new Vector3(width, depth, wall * 2f), Surface.Titanium);
            b.Box(new Vector3(0, 0, root + length), new Vector3(width, depth, wall * 2f), Surface.Titanium);
            for (int i = 1; i < 8; i++)
                b.Box(new Vector3(-width * 0.5f + width * i / 8f, 0, root + length * 0.5f), new Vector3(wall * 0.4f, depth, length), Surface.Titanium);
            for (int j = 1; j < 10; j++)
                b.Box(new Vector3(0, 0, root + length * j / 10f), new Vector3(width, depth, wall * 0.4f), Surface.Titanium);
            return _gridFin = b.Build("Grid Fin");
        }

        /// <summary>Second stage without its nozzle (which glows and is drawn separately).</summary>
        public static SurfaceMesh SecondStage()
        {
            if (_secondStage != null) return _secondStage;
            var b = new MeshBuilder();
            b.Cylinder(new Vector3(0, 2.6f, 0), new Vector3(0, 3.1f, 0), 0.9f, 1.83f, 32, Surface.EngineBell, false); // thrust structure
            b.Lathe(Vector3.zero, Vector3.up, new[] { new Vector2(3.1f, 1.83f), new Vector2(FairingBase - 0.6f, 1.83f) }, 48, Surface.WhitePaint, false, false, 1f);
            b.Lathe(Vector3.zero, Vector3.up, new[] { new Vector2(FairingBase - 0.6f, 1.83f), new Vector2(FairingBase, 1.9f) }, 48, Surface.BlackComposite, false, true);
            return _secondStage = b.Build("Falcon 9 Second Stage");
        }

        /// <summary>Merlin Vacuum nozzle extension, exit at the origin, throat 3 m up.</summary>
        public static SurfaceMesh VacuumNozzle()
        {
            if (_nozzle != null) return _nozzle;
            var b = new MeshBuilder();
            b.Lathe(new Vector3(0, 2.9f, 0), Vector3.down, SpacecraftModels.Nozzle(2.9f, 1.6f, 0.3f), 40, Surface.EngineBell);
            return _nozzle = b.Build("MVac Nozzle");
        }

        /// <summary>A stack of flat-packed Starlink-style satellites on the payload adapter.</summary>
        public static SurfaceMesh PayloadStack()
        {
            if (_payload != null) return _payload;
            var b = new MeshBuilder();
            b.Cylinder(new Vector3(0, FairingBase, 0), new Vector3(0, FairingBase + 0.8f, 0), 1.6f, 1.2f, 32, Surface.BlackComposite);
            for (int i = 0; i < 12; i++)
            {
                float y = FairingBase + 0.8f + i * 0.62f;
                b.Box(new Vector3(0, y + 0.29f, 0), new Vector3(2.7f, 0.56f, 2.7f), i % 2 == 0 ? Surface.DarkPanel : Surface.SilverFoil, 0.5f);
            }
            return _payload = b.Build("Payload Stack");
        }

        /// <summary>
        /// Fairing half (side +1 or -1), centred on its own middle so the flight sim's fairing position can be used
        /// directly. Cylinder section, then an ogive to the nose.
        /// </summary>
        public static SurfaceMesh FairingHalf(int side)
        {
            _fairingHalves ??= new SurfaceMesh[2];
            int slot = side > 0 ? 0 : 1;
            if (_fairingHalves[slot] != null) return _fairingHalves[slot];

            var profile = new List<Vector2> { new Vector2(0, 1.9f), new Vector2(0.9f, 2.6f), new Vector2(6.4f, 2.6f) };
            const int ogiveSteps = 10;
            for (int i = 1; i <= ogiveSteps; i++)
            {
                float t = i / (float)ogiveSteps;
                float radius = Mathf.Max(0.25f, 2.6f * Mathf.Sqrt(1f - t * t));
                profile.Add(new Vector2(6.4f + (FairingLength - 6.4f) * t, radius));
            }
            var b = new MeshBuilder();
            b.Lathe(new Vector3(0, -FairingLength * 0.5f, 0), Vector3.up, profile, 24, Surface.WhitePaint, false, true, 1f, 180f, side > 0 ? 0f : 180f);
            return _fairingHalves[slot] = b.Build(side > 0 ? "Fairing Half A" : "Fairing Half B");
        }

        /// <summary>Unit exhaust plume: radius 1 at the nozzle (y = 0) running to y = -1. Shaped in the plume shader.</summary>
        public static SurfaceMesh Plume()
        {
            if (_plume != null) return _plume;
            var profile = new List<Vector2>();
            for (int i = 0; i <= 16; i++) profile.Add(new Vector2(i / 16f, 1f));
            var b = new MeshBuilder();
            b.Lathe(Vector3.zero, Vector3.down, profile, 32, Surface.WhitePaint);
            _plume = b.Build("Plume");
            // The shader stretches it (Super Heavy's up to ~350 m long and ~160 m wide in thin air); keep it from being culled.
            _plume.Mesh.bounds = new Bounds(new Vector3(0, -200f, 0), new Vector3(400f, 440f, 400f));
            return _plume;
        }
    }
}
