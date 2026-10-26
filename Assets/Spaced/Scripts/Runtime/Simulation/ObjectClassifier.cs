namespace Spaced
{
    /// <summary>Guesses an object's type and physical properties from its catalog name.</summary>
    public static class ObjectClassifier
    {
        /// <summary>B = 2·B*/(ρ0·Re) with ρ0·Re = 0.15696615 kg/m² (SGP4 reference density × Earth radius).</summary>
        const double BstarToBallistic = 2.0 / 0.15696615;

        public static ObjectCategory Classify(string name)
        {
            string n = name.ToUpperInvariant();
            if (n.Contains(" DEB") || n.EndsWith("DEB")) return ObjectCategory.Debris;
            if (n.Contains("R/B")) return ObjectCategory.RocketBody;
            if (n.StartsWith("STARLINK")) return ObjectCategory.Starlink;
            if (n.StartsWith("ONEWEB")) return ObjectCategory.OneWeb;
            if (n.StartsWith("ISS ") || n.Contains("ZARYA") || n.Contains("TIANHE") || n.Contains("WENTIAN")
                || n.Contains("MENGTIAN") || n.StartsWith("CSS "))
                return ObjectCategory.Station;
            if (n.StartsWith("NAVSTAR") || n.StartsWith("GPS ") || n.StartsWith("GLONASS") || n.StartsWith("GSAT0")
                || n.StartsWith("GALILEO") || n.StartsWith("BEIDOU") || n.StartsWith("IRNSS") || n.StartsWith("QZS"))
                return ObjectCategory.Navigation;
            return ObjectCategory.Payload;
        }

        /// <summary>Typical mass, size and drag for a category. Real values vary a lot; these are sensible defaults.</summary>
        public static ObjectPhysics DefaultPhysics(ObjectCategory category)
        {
            const ObjectFlags operational = ObjectFlags.Maneuverable | ObjectFlags.StationKeeping;
            switch (category)
            {
                case ObjectCategory.Starlink: return Make(800f, 4f, 0.033f, category, operational, ModelKind.Starlink);
                case ObjectCategory.OneWeb: return Make(150f, 1.5f, 0.02f, category, operational, ModelKind.OneWeb);
                case ObjectCategory.Station: return Make(420000f, 50f, 0.005f, category, operational, ModelKind.Station);
                case ObjectCategory.Navigation: return Make(1500f, 3f, 0.01f, category, ObjectFlags.None, ModelKind.Navigation);
                case ObjectCategory.RocketBody: return Make(1500f, 2f, 0.01f, category, ObjectFlags.None, ModelKind.RocketBody);
                case ObjectCategory.Debris: return Make(1f, 0.1f, 0.1f, category, ObjectFlags.None, ModelKind.Debris);
                case ObjectCategory.GeneratedDebris: return Make(1f, 0.1f, 0.1f, category, ObjectFlags.None, ModelKind.Debris);
                default: return Make(500f, 1.5f, 0.02f, category, ObjectFlags.None, ModelKind.Smallsat);
            }
        }

        /// <summary>Physics for a catalog object, using its BSTAR drag term when it is usable.</summary>
        public static ObjectPhysics PhysicsFor(ObjectCategory category, double bstar, double semiMajorAxisKm)
        {
            var physics = DefaultPhysics(category);
            // Generic payloads far out are mostly big communications satellites.
            if (category == ObjectCategory.Payload && semiMajorAxisKm > 20000)
            {
                physics.Model = ModelKind.Comsat;
                physics.MassKg = 3500f;
                physics.RadiusM = 12f;
            }
            if (bstar > 1e-7)
                physics.BallisticCoeff = (float)System.Math.Min(bstar * BstarToBallistic, 1.0);
            return physics;
        }

        static ObjectPhysics Make(float mass, float radius, float ballistic, ObjectCategory category, ObjectFlags flags, ModelKind model) =>
            new ObjectPhysics
            {
                MassKg = mass,
                RadiusM = radius,
                BallisticCoeff = ballistic,
                Category = category,
                Flags = flags | ObjectFlags.Alive,
                Model = model,
            };
    }
}
