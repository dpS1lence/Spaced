using System.Collections.Generic;
using UnityEngine;

namespace Spaced
{
    /// <summary>
    /// A satellite model in metres. Body axes: +Y away from Earth (zenith), +Z along the velocity, +X towards
    /// the orbit normal. <see cref="Arrays"/> holds the solar arrays, which turn about the X axis to face the Sun.
    /// </summary>
    public sealed class SpacecraftModel
    {
        public SurfaceMesh Body;
        public SurfaceMesh Arrays;
        /// <summary>Debris tumbles and is scaled by its radius instead of being Earth-pointing.</summary>
        public bool Tumbles;
    }

    /// <summary>Procedural models for every <see cref="ModelKind"/>, at real-world scale.</summary>
    public static class SpacecraftModels
    {
        static readonly Dictionary<ModelKind, SpacecraftModel> Cache = new Dictionary<ModelKind, SpacecraftModel>();

        public static SpacecraftModel Get(ModelKind kind)
        {
            if (!Cache.TryGetValue(kind, out var model)) Cache[kind] = model = Build(kind);
            return model;
        }

        static SpacecraftModel Build(ModelKind kind)
        {
            switch (kind)
            {
                case ModelKind.Starlink: return Starlink();
                case ModelKind.OneWeb: return OneWeb();
                case ModelKind.Comsat: return Comsat();
                case ModelKind.Navigation: return Navigation();
                case ModelKind.Station: return Station();
                case ModelKind.CubeSat: return CubeSat();
                case ModelKind.RocketBody: return RocketBody();
                case ModelKind.Debris: return Debris();
                default: return Smallsat();
            }
        }

        /// <summary>Starlink V2 Mini: a flat, dark phased-array chassis and two ~13 m arrays on one mast.</summary>
        static SpacecraftModel Starlink()
        {
            var body = new MeshBuilder();
            body.Box(new Vector3(0, 0, 0), new Vector3(2.7f, 0.32f, 4.1f), Surface.DarkPanel, 1f);
            for (int i = 0; i < 4; i++) // phased-array antennas on the Earth-facing side
                body.Box(new Vector3(-0.65f + (i % 2) * 1.3f, -0.18f, -1.0f + (i / 2) * 2.0f), new Vector3(1.1f, 0.04f, 1.7f), Surface.BlackComposite);
            body.Box(new Vector3(0, 0.19f, 0), new Vector3(2.6f, 0.04f, 4.0f), Surface.SilverFoil, 0.5f);
            for (int i = 0; i < 3; i++) // optical inter-satellite links
                body.Cylinder(new Vector3(-0.8f + i * 0.8f, 0.2f, 1.6f), new Vector3(-0.8f + i * 0.8f, 0.45f, 1.6f), 0.12f, 0.1f, 12, Surface.Aluminium);
            body.Cylinder(new Vector3(-0.8f, 0.2f, -1.6f), new Vector3(-0.8f, 0.5f, -1.6f), 0.15f, 0.15f, 12, Surface.Aluminium);

            var arrays = new MeshBuilder();
            arrays.Cylinder(new Vector3(1.35f, 0, 0), new Vector3(3.2f, 0, 0), 0.06f, 0.06f, 8, Surface.Aluminium);
            arrays.Panel(new Vector3(3.2f + 6.45f, 0, 0), new Vector2(12.8f, 4.1f), 0.04f, Quaternion.identity, Surface.SolarCells, Surface.Radiator, 0.8f);
            arrays.Panel(new Vector3(3.2f + 12.9f + 0.2f + 6.45f, 0, 0), new Vector2(12.8f, 4.1f), 0.04f, Quaternion.identity, Surface.SolarCells, Surface.Radiator, 0.8f);
            return new SpacecraftModel { Body = body.Build("Starlink"), Arrays = arrays.Build("Starlink Arrays") };
        }

        static SpacecraftModel OneWeb()
        {
            var body = new MeshBuilder();
            body.Box(Vector3.zero, new Vector3(1.0f, 1.3f, 1.0f), Surface.SilverFoil, 0.4f);
            body.Box(new Vector3(0, -0.68f, 0), new Vector3(0.7f, 0.06f, 0.7f), Surface.Radiator);
            body.Cylinder(new Vector3(0, -0.7f, 0.3f), new Vector3(0, -0.95f, 0.3f), 0.08f, 0.05f, 10, Surface.Aluminium);
            return new SpacecraftModel { Body = body.Build("OneWeb"), Arrays = Wings(0.5f, 2, new Vector2(1.25f, 1.0f), "OneWeb Arrays") };
        }

        static SpacecraftModel Smallsat()
        {
            var body = new MeshBuilder();
            body.Box(Vector3.zero, new Vector3(1.0f, 1.0f, 1.2f), Surface.GoldFoil, 0.4f);
            body.Box(new Vector3(0, 0.52f, 0), new Vector3(0.9f, 0.04f, 1.1f), Surface.Radiator);
            body.Dish(new Vector3(0, -0.5f, 0.2f), Vector3.down, 0.35f, 0.12f, 20, Surface.Radiator);
            body.Cylinder(new Vector3(0.3f, 0.5f, -0.4f), new Vector3(0.3f, 0.75f, -0.4f), 0.06f, 0.06f, 8, Surface.Aluminium);
            return new SpacecraftModel { Body = body.Build("Smallsat"), Arrays = Wings(0.5f, 2, new Vector2(1.0f, 0.8f), "Smallsat Arrays") };
        }

        /// <summary>Geostationary communications satellite: big box bus, north/south radiators, deployed reflectors.</summary>
        static SpacecraftModel Comsat()
        {
            var body = new MeshBuilder();
            body.Box(Vector3.zero, new Vector3(2.4f, 2.4f, 3.4f), Surface.GoldFoil, 0.6f);
            body.Box(new Vector3(1.22f, 0, 0), new Vector3(0.04f, 2.3f, 3.3f), Surface.Radiator);
            body.Box(new Vector3(-1.22f, 0, 0), new Vector3(0.04f, 2.3f, 3.3f), Surface.Radiator);
            body.Box(new Vector3(0, -1.25f, 0), new Vector3(2.2f, 0.1f, 3.2f), Surface.SilverFoil, 0.6f);
            foreach (float side in new[] { 1f, -1f })
            {
                // Deployed reflector on an arm, aimed down at the Earth.
                body.Cylinder(new Vector3(0, 0.6f, side * 1.7f), new Vector3(0, 0.2f, side * 2.9f), 0.05f, 0.05f, 6, Surface.Aluminium);
                body.Dish(new Vector3(0, 0.2f, side * 2.9f), new Vector3(0, -0.6f, side * 0.8f).normalized, 1.25f, 0.35f, 28, Surface.Radiator);
            }
            body.Dish(new Vector3(0.5f, -1.3f, 0.8f), Vector3.down, 0.5f, 0.15f, 20, Surface.SilverFoil);
            body.Cylinder(new Vector3(0, -1.3f, -0.8f), new Vector3(0, -1.9f, -0.8f), 0.25f, 0.4f, 16, Surface.WhitePaint);
            return new SpacecraftModel { Body = body.Build("Comsat"), Arrays = Wings(1.2f, 4, new Vector2(2.6f, 2.3f), "Comsat Arrays") };
        }

        /// <summary>GPS-style navigation satellite with an Earth-facing helix antenna array.</summary>
        static SpacecraftModel Navigation()
        {
            var body = new MeshBuilder();
            body.Box(Vector3.zero, new Vector3(2.0f, 2.0f, 2.5f), Surface.GoldFoil, 0.6f);
            body.Box(new Vector3(0, -1.03f, 0), new Vector3(1.8f, 0.06f, 2.2f), Surface.Radiator);
            for (int i = 0; i < 12; i++)
            {
                float angle = i / 12f * Mathf.PI * 2f;
                var p = new Vector3(Mathf.Cos(angle) * 0.6f, -1.05f, Mathf.Sin(angle) * 0.6f);
                body.Cylinder(p, p + Vector3.down * 0.45f, 0.06f, 0.06f, 6, Surface.WhitePaint);
            }
            body.Cylinder(new Vector3(0, -1.05f, 0), new Vector3(0, -1.6f, 0), 0.12f, 0.12f, 8, Surface.WhitePaint);
            return new SpacecraftModel { Body = body.Build("Navigation"), Arrays = Wings(1.0f, 2, new Vector2(2.4f, 2.4f), "Navigation Arrays") };
        }

        /// <summary>ISS-like station: pressurised modules along the velocity, a 100 m truss, eight big array wings.</summary>
        static SpacecraftModel Station()
        {
            var body = new MeshBuilder();
            // Truss (along X) and the pressurised stack (along Z).
            body.Box(Vector3.zero, new Vector3(100f, 3f, 3f), Surface.Aluminium, 2f);
            for (int i = -10; i <= 10; i++)
                body.Box(new Vector3(i * 4.8f, 0, 0), new Vector3(0.3f, 3.4f, 3.4f), Surface.Steel);
            void Module(float zFrom, float zTo, float radius, Surface surface) =>
                body.Cylinder(new Vector3(0, -3.5f, zFrom), new Vector3(0, -3.5f, zTo), radius, radius, 24, surface);
            Module(-34f, -21f, 2.1f, Surface.WhitePaint);   // Zvezda
            Module(-21f, -8f, 2.1f, Surface.WhitePaint);    // Zarya
            Module(-8f, -2f, 2.3f, Surface.SilverFoil);     // Unity
            Module(-2f, 8.5f, 2.2f, Surface.WhitePaint);    // Destiny
            Module(8.5f, 15f, 2.3f, Surface.SilverFoil);    // Harmony
            body.Cylinder(new Vector3(2.3f, -3.5f, 12f), new Vector3(9f, -3.5f, 12f), 2.2f, 2.2f, 24, Surface.WhitePaint);   // Columbus
            body.Cylinder(new Vector3(-2.3f, -3.5f, 12f), new Vector3(-13f, -3.5f, 12f), 2.2f, 2.2f, 24, Surface.WhitePaint); // Kibo
            body.Cylinder(new Vector3(0, -1.5f, 0), new Vector3(0, -3.5f, 0), 1.0f, 1.0f, 16, Surface.Aluminium);
            // Radiators hanging below the truss.
            foreach (float x in new[] { -17f, 17f })
                for (int k = 0; k < 3; k++)
                    body.Box(new Vector3(x, -9f, -4f + k * 4f), new Vector3(13f, 12f, 0.15f), Surface.Radiator);
            // Small Russian-segment arrays on Zvezda.
            body.Panel(new Vector3(8f, -3.5f, -30f), new Vector2(11f, 3f), 0.1f, Quaternion.identity, Surface.SolarCells, Surface.Radiator, 1f);
            body.Panel(new Vector3(-8f, -3.5f, -30f), new Vector2(11f, 3f), 0.1f, Quaternion.identity, Surface.SolarCells, Surface.Radiator, 1f);

            var arrays = new MeshBuilder();
            foreach (float x in new[] { -46f, -34f, 34f, 46f })
            {
                foreach (float z in new[] { 1f, -1f })
                {
                    arrays.Panel(new Vector3(x - 2.9f, 0, z * 19.5f), new Vector2(4.6f, 34f), 0.08f, Quaternion.identity, Surface.SolarCells, Surface.GoldFoil, 1.2f);
                    arrays.Panel(new Vector3(x + 2.9f, 0, z * 19.5f), new Vector2(4.6f, 34f), 0.08f, Quaternion.identity, Surface.SolarCells, Surface.GoldFoil, 1.2f);
                    arrays.Box(new Vector3(x, 0, z * 19.5f), new Vector3(0.3f, 0.3f, 35f), Surface.Aluminium);
                }
            }
            return new SpacecraftModel { Body = body.Build("Station"), Arrays = arrays.Build("Station Arrays") };
        }

        static SpacecraftModel CubeSat()
        {
            var body = new MeshBuilder();
            body.Box(Vector3.zero, new Vector3(0.1f, 0.1f, 0.34f), Surface.BlackComposite);
            body.Box(new Vector3(0, 0.0505f, 0), new Vector3(0.085f, 0.002f, 0.32f), Surface.SolarCells, 0.1f);
            body.Box(new Vector3(0, -0.0505f, 0), new Vector3(0.085f, 0.002f, 0.32f), Surface.SolarCells, 0.1f);
            body.Cylinder(new Vector3(0, 0.05f, 0.1f), new Vector3(0, 0.25f, 0.1f), 0.002f, 0.002f, 4, Surface.Aluminium, false);
            return new SpacecraftModel { Body = body.Build("CubeSat"), Arrays = Wings(0.05f, 1, new Vector2(0.1f, 0.32f), "CubeSat Arrays") };
        }

        /// <summary>A spent Falcon 9 second stage left in orbit, nozzle first.</summary>
        static SpacecraftModel RocketBody()
        {
            var body = new MeshBuilder();
            body.Cylinder(new Vector3(0, 0, -4.5f), new Vector3(0, 0, 4.5f), 1.83f, 1.83f, 32, Surface.WhitePaint, true, 2f);
            body.Lathe(new Vector3(0, 0, -4.5f), Vector3.back, Nozzle(3.2f, 1.6f, 0.45f), 32, Surface.EngineBell);
            return new SpacecraftModel { Body = body.Build("Rocket Body") };
        }

        /// <summary>A jagged, unit-sized fragment; instances are scaled to each piece's size and tumble.</summary>
        static SpacecraftModel Debris()
        {
            var body = new MeshBuilder();
            var random = new System.Random(3);
            for (int k = 0; k < 5; k++)
            {
                var center = new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f) * 0.4f;
                var size = new Vector3(0.2f + (float)random.NextDouble() * 0.6f, 0.03f + (float)random.NextDouble() * 0.15f, 0.2f + (float)random.NextDouble() * 0.6f);
                var rotation = Quaternion.Euler((float)random.NextDouble() * 360f, (float)random.NextDouble() * 360f, (float)random.NextDouble() * 360f);
                body.Box(center, size, rotation, k % 3 == 0 ? Surface.GoldFoil : k % 3 == 1 ? Surface.Aluminium : Surface.SolarCells, 0.3f);
            }
            return new SpacecraftModel { Body = body.Build("Debris"), Tumbles = true };
        }

        /// <summary>Two wings of <paramref name="panels"/> panels each, extending along ±X from the body edge.</summary>
        static SurfaceMesh Wings(float bodyHalfWidth, int panels, Vector2 panelSize, string name)
        {
            var arrays = new MeshBuilder();
            float yoke = Mathf.Max(0.15f, panelSize.x * 0.3f);
            foreach (float side in new[] { 1f, -1f })
            {
                arrays.Cylinder(new Vector3(side * bodyHalfWidth, 0, 0), new Vector3(side * (bodyHalfWidth + yoke), 0, 0), 0.02f + panelSize.y * 0.01f, 0.02f + panelSize.y * 0.01f, 8, Surface.Aluminium);
                for (int p = 0; p < panels; p++)
                {
                    float x = side * (bodyHalfWidth + yoke + panelSize.x * (p + 0.5f) + p * 0.05f);
                    arrays.Panel(new Vector3(x, 0, 0), panelSize, Mathf.Max(0.004f, panelSize.y * 0.015f), Quaternion.identity, Surface.SolarCells, Surface.Radiator, panelSize.y * 0.5f);
                }
            }
            return arrays.Build(name);
        }

        /// <summary>Bell-nozzle profile: (distance from throat plane, radius) pairs.</summary>
        public static Vector2[] Nozzle(float length, float exitRadius, float throatRadius)
        {
            var profile = new Vector2[10];
            for (int i = 0; i < profile.Length; i++)
            {
                float t = i / (profile.Length - 1f);
                profile[i] = new Vector2(t * length, Mathf.Lerp(throatRadius, exitRadius, Mathf.Sqrt(t)));
            }
            return profile;
        }
    }
}
