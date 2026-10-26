using UnityEngine;

namespace Spaced
{
    /// <summary>
    /// Procedural lunar spacecraft in metres (+Y up the thrust axis, the main engine's exit at y = 0): a small flyby
    /// probe, an orbiter with a big solar array and dish, and a four-legged lander.
    /// </summary>
    public static class LunarModels
    {
        static SurfaceMesh _probe, _orbiter, _lander;

        public static SurfaceMesh For(LunarSpacecraft craft) =>
            craft.Name.Contains("lander") ? Lander() : craft.Name.Contains("orbiter") ? Orbiter() : Probe();

        public static SurfaceMesh Probe()
        {
            if (_probe != null) return _probe;
            var b = new MeshBuilder();
            b.Lathe(new Vector3(0, 0.35f, 0), Vector3.down, SpacecraftModels.Nozzle(0.35f, 0.12f, 0.05f), 12, Surface.EngineBell);
            b.Box(new Vector3(0, 0.95f, 0), new Vector3(1.1f, 1.1f, 1.1f), Surface.GoldFoil, 1f);
            b.Dish(new Vector3(0, 1.55f, 0), Vector3.up, 0.6f, 0.15f, 20, Surface.WhitePaint);
            b.Panel(new Vector3(1.9f, 0.95f, 0), new Vector2(2.6f, 0.9f), 0.04f, Quaternion.identity, Surface.SolarCells, Surface.WhitePaint, 0.9f);
            b.Panel(new Vector3(-1.9f, 0.95f, 0), new Vector2(2.6f, 0.9f), 0.04f, Quaternion.identity, Surface.SolarCells, Surface.WhitePaint, 0.9f);
            return _probe = b.Build("Lunar Probe");
        }

        public static SurfaceMesh Orbiter()
        {
            if (_orbiter != null) return _orbiter;
            var b = new MeshBuilder();
            b.Lathe(new Vector3(0, 0.6f, 0), Vector3.down, SpacecraftModels.Nozzle(0.6f, 0.25f, 0.08f), 16, Surface.EngineBell);
            b.Cylinder(new Vector3(0, 0.6f, 0), new Vector3(0, 2.6f, 0), 0.95f, 0.95f, 16, Surface.GoldFoil, true, 2f);
            b.Box(new Vector3(0, 3.0f, 0), new Vector3(1.6f, 0.8f, 1.6f), Surface.SilverFoil, 1f);
            b.Dish(new Vector3(0.9f, 3.0f, 0), Vector3.right, 0.9f, 0.25f, 24, Surface.WhitePaint);
            b.Panel(new Vector3(0, 1.8f, 3.6f), new Vector2(1.8f, 5.6f), 0.05f, Quaternion.identity, Surface.SolarCells, Surface.WhitePaint, 0.9f);
            b.Box(new Vector3(0, 1.8f, 0.85f), new Vector3(0.12f, 0.12f, 0.9f), Surface.Aluminium);
            b.Box(new Vector3(-0.7f, 3.5f, -0.4f), new Vector3(0.3f, 0.6f, 0.3f), Surface.BlackComposite); // camera
            return _orbiter = b.Build("Lunar Orbiter");
        }

        /// <summary>Descent-stage lander: gold-foil octagonal body, tanks, four splayed legs with footpads.</summary>
        public static SurfaceMesh Lander()
        {
            if (_lander != null) return _lander;
            var b = new MeshBuilder();
            b.Lathe(new Vector3(0, 1.3f, 0), Vector3.down, SpacecraftModels.Nozzle(0.9f, 0.45f, 0.12f), 16, Surface.EngineBell);
            b.Cylinder(new Vector3(0, 1.2f, 0), new Vector3(0, 2.9f, 0), 1.7f, 1.7f, 8, Surface.GoldFoil, true, 2f);
            foreach (var corner in new[] { new Vector2(1, 1), new Vector2(1, -1), new Vector2(-1, 1), new Vector2(-1, -1) })
            {
                var c = new Vector3(corner.x * 0.75f, 3.4f, corner.y * 0.75f);
                b.Lathe(c - Vector3.up * 0.55f, Vector3.up, new[] { new Vector2(0, 0.01f), new Vector2(0.15f, 0.5f), new Vector2(0.95f, 0.5f), new Vector2(1.1f, 0.01f) }, 16, Surface.SilverFoil);
                var top = new Vector3(corner.x * 1.4f, 2.2f, corner.y * 1.4f);
                var foot = new Vector3(corner.x * 2.6f, 0.05f, corner.y * 2.6f);
                b.Cylinder(top, foot, 0.08f, 0.08f, 8, Surface.Aluminium);
                b.Cylinder(new Vector3(corner.x * 1.5f, 1.2f, corner.y * 1.5f), foot + Vector3.up * 0.4f, 0.05f, 0.05f, 6, Surface.Aluminium);
                b.Cylinder(foot, foot + Vector3.up * 0.1f, 0.38f, 0.32f, 12, Surface.GoldFoil);
            }
            b.Dish(new Vector3(0, 4.1f, 0), Vector3.up, 0.5f, 0.12f, 16, Surface.WhitePaint);
            b.Panel(new Vector3(0, 3.0f, -2.1f), new Vector2(2.4f, 1.0f), 0.04f, Quaternion.Euler(90f, 0, 0), Surface.SolarCells, Surface.WhitePaint, 0.9f);
            return _lander = b.Build("Lunar Lander");
        }

        /// <summary>Boulders strewn over a disc of the given radius (metres), for the lunar landing site.</summary>
        public static SurfaceMesh Boulders(int count, float radius, int seed)
        {
            var random = new System.Random(seed);
            var b = new MeshBuilder();
            for (int i = 0; i < count; i++)
            {
                float r = radius * Mathf.Sqrt((float)random.NextDouble());
                float a = (float)random.NextDouble() * Mathf.PI * 2f;
                float size = 0.3f + 2.2f * Mathf.Pow((float)random.NextDouble(), 3f);
                var center = new Vector3(Mathf.Cos(a) * r, size * 0.25f, Mathf.Sin(a) * r);
                b.Box(center, new Vector3(size, size * 0.6f, size * 0.8f), Quaternion.Euler(0, (float)random.NextDouble() * 360f, (float)random.NextDouble() * 25f), Surface.Regolith, 2f);
            }
            return b.Build("Boulders");
        }
    }
}
