using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Unity.Mathematics;
using UnityEngine;

namespace Spaced
{
    public enum GameMode
    {
        Sandbox,
        Career,
    }

    /// <summary>Everything but the object catalog, as JSON (the catalog is stored as compact binary after it).</summary>
    [Serializable]
    public sealed class SaveData
    {
        public int Version = SaveGame.Version;
        public GameMode Mode;
        public string SavedAtUtc;
        public double JulianDate;
        public int WarpIndex;
        public SimulationStats Stats;
        public List<SimEvent> Log = new List<SimEvent>();
        public int LaunchCounter;
        public List<GroupSave> Groups = new List<GroupSave>();
        public List<TargetSave> Targets = new List<TargetSave>();
        public List<ImageSave> Gallery = new List<ImageSave>();
        public int CampaignCounter;
        public List<CampaignSave> Campaigns = new List<CampaignSave>();
        public List<LunarMissionSave> Lunar = new List<LunarMissionSave>();
        public CareerSave Career = new CareerSave();
    }

    [Serializable]
    public sealed class GroupSave
    {
        public string Name;
        public SatelliteClass Class;
        public PayloadRole Role;
        public double CreatedJd, LastCaptureJd;
        public bool AutoCapture, StationKeeping, AvoidsCollisions;
    }

    [Serializable]
    public sealed class TargetSave
    {
        public string Name;
        public double LatitudeDeg, LongitudeDeg, RequestedJd;
        public bool Captured;
        public int ImageIndex = -1;
    }

    [Serializable]
    public sealed class ImageSave
    {
        public string Satellite, SavedPath;
        public double JulianDate, LatitudeDeg, LongitudeDeg, ResolutionM, SwathKm, OffNadirDeg, AltitudeKm;
        public bool Night;
        public int TargetIndex = -1;
        /// <summary>Index of the PNG in the save file's image block.</summary>
        public int Png = -1;
    }

    [Serializable]
    public sealed class CampaignSave
    {
        public string Name;
        public VehicleKind Vehicle;
        public bool Expendable;
        public SatelliteClass Class;
        public bool Maneuverable, AnyPlane, WatchAll;
        public int Site, Group, SatellitesPerLaunch, Deployed;
        public double PlanEpochJd;
        public List<OrbitSlot> Slots = new List<OrbitSlot>();
        public int LunarKind = -1;
        public int Contract = -1;
        public List<MissionSave> Missions = new List<MissionSave>();
    }

    [Serializable]
    public sealed class MissionSave
    {
        public int Number;
        public string Name;
        public List<int> SlotIndices = new List<int>();
        public double PayloadKg, AltitudeKm, LiftoffJd;
        public bool Done;
    }

    /// <summary>
    /// Save files: <c>persistentDataPath/saves/&lt;slot&gt;.spaced</c>, a gzip stream holding a header, the game state
    /// as JSON, the whole object catalog in binary (so collisions, debris and your satellites come back exactly as
    /// they were) and the gallery pictures as PNG. Written to a temporary file first so a crash can't corrupt a save.
    /// </summary>
    public static class SaveGame
    {
        public const int Version = 1;
        const uint Magic = 0x44435053; // "SPCD"

        public static string Folder => Path.Combine(Application.persistentDataPath, "saves");
        public static string PathFor(GameMode mode) => Path.Combine(Folder, (mode == GameMode.Career ? "career" : "sandbox") + ".spaced");
        public static bool Exists(GameMode mode) => File.Exists(PathFor(mode));

        public static void Write(string path, SaveData data, ObjectCatalog catalog, List<byte[]> images)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            using (var file = File.Create(temp))
            using (var gzip = new GZipStream(file, System.IO.Compression.CompressionLevel.Fastest))
            using (var w = new BinaryWriter(gzip))
            {
                w.Write(Magic);
                w.Write(Version);
                w.Write(JsonUtility.ToJson(data));
                WriteCatalog(w, catalog);
                w.Write(images.Count);
                foreach (var png in images)
                {
                    w.Write(png.Length);
                    w.Write(png);
                }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        /// <summary>Reads only the JSON part (for the main menu), or null if the file is missing or unreadable.</summary>
        public static SaveData Peek(string path)
        {
            try
            {
                using (var file = File.OpenRead(path))
                using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                using (var r = new BinaryReader(gzip))
                {
                    if (r.ReadUInt32() != Magic || r.ReadInt32() > Version) return null;
                    return JsonUtility.FromJson<SaveData>(r.ReadString());
                }
            }
            catch (Exception) { return null; }
        }

        /// <summary>Reads a save, replacing the catalog's contents. Returns null if the file can't be read.</summary>
        public static SaveData Read(string path, ObjectCatalog catalog, List<byte[]> images)
        {
            using (var file = File.OpenRead(path))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var r = new BinaryReader(gzip))
            {
                if (r.ReadUInt32() != Magic) return null;
                if (r.ReadInt32() > Version) return null;
                var data = JsonUtility.FromJson<SaveData>(r.ReadString());
                ReadCatalog(r, catalog);
                int count = r.ReadInt32();
                for (int i = 0; i < count; i++) images.Add(r.ReadBytes(r.ReadInt32()));
                return data;
            }
        }

        static void WriteCatalog(BinaryWriter w, ObjectCatalog catalog)
        {
            int alive = 0;
            for (int i = 0; i < catalog.Count; i++)
                if (catalog.Physics[i].IsAlive) alive++;
            w.Write(alive);
            for (int i = 0; i < catalog.Count; i++)
            {
                var physics = catalog.Physics[i];
                if (!physics.IsAlive) continue;
                WriteElements(w, catalog.Elements[i]);
                w.Write(physics.MassKg);
                w.Write(physics.RadiusM);
                w.Write(physics.BallisticCoeff);
                w.Write((byte)physics.Category);
                w.Write((byte)physics.Flags);
                w.Write((byte)physics.Model);
                var maneuver = catalog.Maneuvers[i];
                w.Write(maneuver.Active);
                if (maneuver.Active != 0)
                {
                    WriteElements(w, maneuver.Target);
                    w.Write(maneuver.RemainingSeconds);
                }
                w.Write(catalog.Names[i] ?? "");
                w.Write(catalog.NoradIds[i]);
                w.Write(catalog.Groups[i]);
                w.Write(catalog.Ids[i]);
            }
            w.Write(catalog.NextId);
        }

        static void ReadCatalog(BinaryReader r, ObjectCatalog catalog)
        {
            catalog.Clear();
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var elements = ReadElements(r);
                var physics = new ObjectPhysics
                {
                    MassKg = r.ReadSingle(),
                    RadiusM = r.ReadSingle(),
                    BallisticCoeff = r.ReadSingle(),
                    Category = (ObjectCategory)r.ReadByte(),
                    Flags = (ObjectFlags)r.ReadByte(),
                    Model = (ModelKind)r.ReadByte(),
                };
                var maneuver = new OrbitManeuver { Active = r.ReadByte() };
                if (maneuver.Active != 0)
                {
                    maneuver.Target = ReadElements(r);
                    maneuver.RemainingSeconds = r.ReadDouble();
                }
                string name = r.ReadString();
                int norad = r.ReadInt32();
                int group = r.ReadInt32();
                uint id = r.ReadUInt32();
                catalog.AddRestored(elements, physics, maneuver, name, norad, group, id);
            }
            catalog.SetNextId(r.ReadUInt32());
        }

        static void WriteElements(BinaryWriter w, in OrbitElements e)
        {
            w.Write(e.A); w.Write(e.N); w.Write(e.E); w.Write(e.I); w.Write(e.Raan); w.Write(e.ArgP); w.Write(e.M);
        }

        static OrbitElements ReadElements(BinaryReader r) => new OrbitElements
        {
            A = r.ReadDouble(), N = r.ReadDouble(), E = r.ReadDouble(), I = r.ReadDouble(),
            Raan = r.ReadDouble(), ArgP = r.ReadDouble(), M = r.ReadDouble(),
        };

        /// <summary>JSON has no NaN: "not set" times are stored as this instead.</summary>
        public const double Unset = -1e300;
        public static double Pack(double value) => double.IsNaN(value) ? Unset : value;
        public static double Unpack(double value) => value <= Unset * 0.5 ? double.NaN : value;
    }
}
