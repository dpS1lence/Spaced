using System;
using Unity.Mathematics;

namespace Spaced
{
    /// <summary>A Web-Mercator tile: zoom level and column/row (row 0 at the north edge).</summary>
    public readonly struct TileId : IEquatable<TileId>
    {
        public readonly int Z, X, Y;

        public TileId(int z, int x, int y)
        {
            Z = z;
            X = x;
            Y = y;
        }

        public TileId Parent => new TileId(Z - 1, X >> 1, Y >> 1);
        public TileId Child(int i) => new TileId(Z + 1, X * 2 + (i & 1), Y * 2 + (i >> 1));

        /// <summary>The ancestor at a coarser zoom level.</summary>
        public TileId AncestorAt(int z) => z >= Z ? this : new TileId(z, X >> (Z - z), Y >> (Z - z));

        public bool Equals(TileId other) => Z == other.Z && X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is TileId other && Equals(other);
        public override int GetHashCode() => (Z * 73856093) ^ (X * 19349663) ^ (Y * 83492791);
        public override string ToString() => $"{Z}/{X}/{Y}";
    }

    /// <summary>Pure tile geometry (no Unity objects), shared by the streamer, terrain queries and tests.</summary>
    public static class TileMath
    {
        public const int TileSize = 256;
        /// <summary>Web Mercator stops here; the base globe covers the poles.</summary>
        public const double MaxLatitude = 85.0511287798;

        public static double Lon(double tileX, int z) => tileX / (1 << z) * 360.0 - 180.0;

        public static double Lat(double tileY, int z)
        {
            double n = math.PI_DBL * (1.0 - 2.0 * tileY / (1 << z));
            return math.atan(math.sinh(n)) * MathConst.Rad2Deg;
        }

        public static double TileX(double lonDeg, int z) => (lonDeg + 180.0) / 360.0 * (1 << z);

        public static double TileY(double latDeg, int z)
        {
            double lat = math.clamp(latDeg, -MaxLatitude, MaxLatitude) * MathConst.Deg2Rad;
            return (1.0 - math.log(math.tan(lat) + 1.0 / math.cos(lat)) / math.PI_DBL) / 2.0 * (1 << z);
        }

        public static TileId TileAt(double latDeg, double lonDeg, int z)
        {
            int n = 1 << z;
            int x = (int)math.floor(TileX(lonDeg, z));
            int y = (int)math.floor(TileY(latDeg, z));
            return new TileId(z, ((x % n) + n) % n, math.clamp(y, 0, n - 1));
        }

        /// <summary>Latitude/longitude of the tile centre.</summary>
        public static void Center(TileId t, out double latDeg, out double lonDeg)
        {
            latDeg = Lat(t.Y + 0.5, t.Z);
            lonDeg = Lon(t.X + 0.5, t.Z);
        }

        /// <summary>Ground width of the tile at its centre latitude, metres.</summary>
        public static double WidthMeters(TileId t)
        {
            Center(t, out double lat, out _);
            return 2.0 * math.PI_DBL * EarthConstants.RadiusKm * 1000.0 * math.cos(lat * MathConst.Deg2Rad) / (1 << t.Z);
        }

        /// <summary>Angular radius (radians) of a circle around the tile centre that contains the whole tile.</summary>
        public static double AngularRadius(TileId t)
        {
            Center(t, out double lat, out double lon);
            double3 c = Unit(lat, lon);
            double best = 0;
            for (int i = 0; i < 4; i++)
            {
                double3 corner = Unit(Lat(t.Y + (i >> 1), t.Z), Lon(t.X + (i & 1), t.Z));
                best = math.max(best, math.acos(math.clamp(math.dot(c, corner), -1, 1)));
            }
            return best;
        }

        static double3 Unit(double latDeg, double lonDeg) => math.normalize(Astro.GeoToEcef(latDeg, lonDeg, 0));

        /// <summary>
        /// Where a tile's texture region sits inside an ancestor's texture: uv' = uv · scale + offset
        /// (v = 0 at the south edge, as Unity loads images).
        /// </summary>
        public static void AncestorUv(TileId tile, TileId ancestor, out double scale, out double offsetU, out double offsetV)
        {
            int dz = tile.Z - ancestor.Z;
            double n = 1 << dz;
            double relX = tile.X - ancestor.X * n;
            double relY = tile.Y - ancestor.Y * n;
            scale = 1.0 / n;
            offsetU = relX / n;
            offsetV = 1.0 - (relY + 1.0) / n;
        }

        /// <summary>The point of the tile nearest to a location: its latitude/longitude clamped into the tile's bounds.</summary>
        public static void ClosestPoint(TileId t, double latDeg, double lonDeg, out double closestLat, out double closestLon)
        {
            double north = Lat(t.Y, t.Z), south = Lat(t.Y + 1, t.Z);
            double west = Lon(t.X, t.Z), east = Lon(t.X + 1, t.Z);
            closestLat = math.clamp(latDeg, south, north);
            double centerLon = (west + east) * 0.5, halfWidth = (east - west) * 0.5;
            double dLon = lonDeg - centerLon;
            dLon -= 360.0 * math.floor((dLon + 180.0) / 360.0); // wrap to [-180, 180)
            closestLon = centerLon + math.clamp(dLon, -halfWidth, halfWidth);
        }

        /// <summary>
        /// Decodes a Terrarium elevation pixel (AWS / Mapzen terrain tiles): metres = R·256 + G + B/256 − 32768.
        /// </summary>
        public static float TerrariumHeight(byte r, byte g, byte b) => r * 256f + g + b / 256f - 32768f;

        /// <summary>
        /// Should this tile be replaced by its four children? Yes while one imagery texel at the tile is bigger than
        /// <paramref name="pixelsPerTexel"/> screen pixels' worth of ground.
        /// </summary>
        public static bool ShouldSplit(TileId t, double distanceToTileM, double metersPerPixelPerMeterDistance, double pixelsPerTexel) =>
            TexelError(t, distanceToTileM, metersPerPixelPerMeterDistance) > pixelsPerTexel;

        /// <summary>How many screen pixels one imagery texel of the tile covers at that distance (above 1 = blurry).</summary>
        public static double TexelError(TileId t, double distanceToTileM, double metersPerPixelPerMeterDistance)
        {
            double texel = WidthMeters(t) / TileSize;
            return texel / (math.max(distanceToTileM, 1.0) * metersPerPixelPerMeterDistance);
        }
    }
}
