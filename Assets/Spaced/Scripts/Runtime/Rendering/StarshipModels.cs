using UnityEngine;

namespace Spaced
{
    /// <summary>
    /// Procedural Starship (Block 2) in metres: Super Heavy and Ship, 9 m stainless-steel stages. +Y points up the
    /// vehicle; y = 0 is each stage's engine exit plane. The Ship sits on the booster's hot-staging ring.
    /// </summary>
    public static class StarshipModels
    {
        public const float Radius = 4.5f;
        public const float BoosterBody = 69.2f;
        public const float BoosterLength = 71f;      // with the vented hot-staging ring
        public const float ShipBase = 71.2f;         // Ship origin in stack coordinates
        public const float ShipLength = 52f;
        public const float GridFinHeight = 65.5f;
        /// <summary>Booster hardpoints the tower's arms close under (height above the booster's engines).</summary>
        public const float CatchPointHeight = 64f;

        static SurfaceMesh _booster, _ship;

        /// <summary>Super Heavy: 33 Raptors (3 centre, 10 inner ring, 20 outer), steel tank, vented ring, four grid fins.</summary>
        public static SurfaceMesh SuperHeavy()
        {
            if (_booster != null) return _booster;
            var b = new MeshBuilder();
            var raptor = SpacecraftModels.Nozzle(1.9f, 0.65f, 0.25f);
            void Ring(int count, float radius, float phase)
            {
                for (int i = 0; i < count; i++)
                {
                    float a = (i / (float)count + phase) * Mathf.PI * 2f;
                    b.Lathe(new Vector3(Mathf.Cos(a) * radius, 1.9f, Mathf.Sin(a) * radius), Vector3.down, raptor, 14, Surface.EngineBell);
                }
            }
            Ring(3, 0.95f, 0f);
            Ring(10, 2.35f, 0.05f);
            Ring(20, 3.75f, 0f);
            b.Cylinder(new Vector3(0, 1.8f, 0), new Vector3(0, 2.1f, 0), Radius - 0.05f, Radius - 0.05f, 48, Surface.BlackComposite); // aft heat shield
            b.Lathe(Vector3.zero, Vector3.up, new[] { new Vector2(0.2f, Radius + 0.15f), new Vector2(2.6f, Radius), new Vector2(BoosterBody, Radius) }, 64,
                Surface.Stainless, false, false, 2f);
            // Vented hot-staging ring: dark slots in a steel band, then a domed cap.
            b.Lathe(new Vector3(0, BoosterBody, 0), Vector3.up, new[] { new Vector2(0, Radius), new Vector2(BoosterLength - BoosterBody, Radius) }, 64, Surface.Stainless);
            for (int i = 0; i < 24; i++)
            {
                float a = i / 24f * Mathf.PI * 2f;
                var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                b.Box(dir * (Radius + 0.02f) + Vector3.up * (BoosterBody + 0.9f), new Vector3(0.12f, 1.2f, 0.8f), Quaternion.LookRotation(Vector3.Cross(dir, Vector3.up)), Surface.BlackComposite);
            }
            b.Cylinder(new Vector3(0, BoosterLength, 0), new Vector3(0, BoosterLength + 0.15f, 0), Radius, Radius - 0.6f, 48, Surface.Steel);
            // Raceways, chines and the catch hardpoints.
            foreach (float side in new[] { 1f, -1f })
            {
                b.Box(new Vector3(side * (Radius + 0.15f), 36f, 0), new Vector3(0.3f, 62f, 0.6f), Surface.Stainless);
                b.Box(new Vector3(0, 30f, side * (Radius + 0.2f)), new Vector3(0.5f, 40f, 0.25f), Surface.Stainless);
                b.Box(new Vector3(side * (Radius + 0.45f), CatchPointHeight, 1.6f), new Vector3(0.9f, 0.6f, 1.2f), Surface.Steel);
                b.Box(new Vector3(side * (Radius + 0.45f), CatchPointHeight, -1.6f), new Vector3(0.9f, 0.6f, 1.2f), Surface.Steel);
            }
            // Four big grid fins, fixed open.
            for (int i = 0; i < 4; i++)
            {
                float a = (i * 90f + 45f) * Mathf.Deg2Rad;
                var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                AppendGridFin(b, dir * Radius + Vector3.up * GridFinHeight, dir);
            }
            return _booster = b.Build("Super Heavy");
        }

        static void AppendGridFin(MeshBuilder b, Vector3 root, Vector3 outward)
        {
            var rotation = Quaternion.LookRotation(outward, Vector3.up);
            const float width = 3.2f, length = 3.4f, depth = 0.6f, wall = 0.09f;
            Vector3 P(float x, float y, float z) => root + rotation * new Vector3(x, y, z);
            b.Box(P(0, 0, 0.35f), new Vector3(0.5f, 0.6f, 0.7f), rotation, Surface.Steel);
            b.Box(P(-width * 0.5f, 0, 0.7f + length * 0.5f), new Vector3(wall * 2f, depth, length), rotation, Surface.Steel);
            b.Box(P(width * 0.5f, 0, 0.7f + length * 0.5f), new Vector3(wall * 2f, depth, length), rotation, Surface.Steel);
            b.Box(P(0, 0, 0.7f), new Vector3(width, depth, wall * 2f), rotation, Surface.Steel);
            b.Box(P(0, 0, 0.7f + length), new Vector3(width, depth, wall * 2f), rotation, Surface.Steel);
            for (int i = 1; i < 6; i++)
                b.Box(P(-width * 0.5f + width * i / 6f, 0, 0.7f + length * 0.5f), new Vector3(wall * 0.5f, depth, length), rotation, Surface.Steel);
            for (int j = 1; j < 6; j++)
                b.Box(P(0, 0, 0.7f + length * j / 6f), new Vector3(width, depth, wall * 0.5f), rotation, Surface.Steel);
        }

        /// <summary>
        /// Ship: steel body with black hexagonal heat-shield tiles on the windward side (−Z), ogive nose, two
        /// forward and two aft flaps, three vacuum and three sea-level Raptors, and the payload door.
        /// </summary>
        public static SurfaceMesh Ship()
        {
            if (_ship != null) return _ship;
            var b = new MeshBuilder();
            var vacuum = SpacecraftModels.Nozzle(3.1f, 1.15f, 0.3f);
            var sea = SpacecraftModels.Nozzle(1.9f, 0.65f, 0.25f);
            for (int i = 0; i < 3; i++)
            {
                float a = (i / 3f) * Mathf.PI * 2f + Mathf.PI / 2f;
                b.Lathe(new Vector3(Mathf.Cos(a) * 2.9f, 3.0f, Mathf.Sin(a) * 2.9f), Vector3.down, vacuum, 20, Surface.EngineBell);
                float c = a + Mathf.PI / 3f;
                b.Lathe(new Vector3(Mathf.Cos(c) * 1.0f, 3.0f, Mathf.Sin(c) * 1.0f), Vector3.down, sea, 14, Surface.EngineBell);
            }
            b.Cylinder(new Vector3(0, 2.9f, 0), new Vector3(0, 3.2f, 0), Radius - 0.1f, Radius - 0.1f, 48, Surface.BlackComposite);
            const float bodyTop = 41f;
            b.Lathe(Vector3.zero, Vector3.up, new[] { new Vector2(2.4f, Radius), new Vector2(bodyTop, Radius) }, 64, Surface.Stainless, false, false, 2f);
            var nose = new System.Collections.Generic.List<Vector2>();
            for (int i = 0; i <= 12; i++)
            {
                float t = i / 12f;
                nose.Add(new Vector2(bodyTop + (ShipLength - bodyTop) * t, Mathf.Max(0.35f, Radius * Mathf.Sqrt(1f - t * t * 0.97f))));
            }
            b.Lathe(Vector3.zero, Vector3.up, nose, 64, Surface.Stainless, false, true, 1f);
            // Heat shield: a slightly proud shell of tiles over the windward half, body and nose.
            var shield = new System.Collections.Generic.List<Vector2> { new Vector2(2.6f, Radius + 0.04f), new Vector2(bodyTop, Radius + 0.04f) };
            foreach (var p in nose) shield.Add(new Vector2(p.x, p.y + 0.04f));
            b.Lathe(Vector3.zero, Vector3.up, shield, 40, Surface.HeatTile, false, false, 6f, 180f, 180f);
            // Payload door ("pez dispenser") on the leeward side.
            b.Box(new Vector3(0, 30f, Radius + 0.03f), new Vector3(5.4f, 2.2f, 0.08f), Surface.BlackComposite);
            // Flaps (static here; the aft pair hinges at the tile line).
            foreach (float side in new[] { 1f, -1f })
            {
                b.Box(new Vector3(side * (Radius + 1.6f), 8.5f, -0.6f), new Vector3(3.2f, 10.5f, 0.5f), Surface.HeatTile, 3f);
                b.Box(new Vector3(side * (Radius + 1.6f), 8.5f, -0.25f), new Vector3(3.2f, 10.5f, 0.2f), Surface.Stainless);
                b.Box(new Vector3(side * (Radius + 1.0f), 45.5f, -1.2f), new Vector3(2.0f, 6.5f, 0.4f), Quaternion.Euler(0, 0, side * -8f), Surface.HeatTile, 3f);
                b.Box(new Vector3(side * (Radius + 1.0f), 45.5f, -0.9f), new Vector3(2.0f, 6.5f, 0.15f), Quaternion.Euler(0, 0, side * -8f), Surface.Stainless);
            }
            return _ship = b.Build("Ship");
        }
    }
}
