namespace Spaced
{
    public enum PadStyle
    {
        /// <summary>SLC-40: crew access tower beside the pad, four lightning masts.</summary>
        Slc40,
        /// <summary>LC-39A: the old Shuttle fixed service structure.</summary>
        Lc39A,
        /// <summary>SLC-4E: open pad with only the transporter-erector.</summary>
        Slc4E,
    }

    /// <summary>A SpaceX Falcon launch complex.</summary>
    public sealed class LaunchSite
    {
        public string Name;
        public string ShortName;
        public double LatitudeDeg;
        public double LongitudeDeg;
        /// <summary>Launch southward (polar corridor / Vandenberg) rather than north-east.</summary>
        public bool PrefersSouth;
        public string DroneshipName;
        public PadStyle Style;
        /// <summary>Direction (degrees from north) in which the flame trench vents exhaust.</summary>
        public float FlameTrenchHeadingDeg;
        /// <summary>Distance east to the shoreline, metres (negative = ocean to the west).</summary>
        public float ShorelineOffsetM;

        public static readonly LaunchSite[] All =
        {
            new LaunchSite
            {
                Name = "SLC-40, Cape Canaveral Space Force Station", ShortName = "SLC-40",
                LatitudeDeg = 28.56194, LongitudeDeg = -80.57722,
                DroneshipName = "A Shortfall of Gravitas", Style = PadStyle.Slc40,
                FlameTrenchHeadingDeg = 0f, ShorelineOffsetM = 900f,
            },
            new LaunchSite
            {
                Name = "LC-39A, Kennedy Space Center", ShortName = "LC-39A",
                LatitudeDeg = 28.60822, LongitudeDeg = -80.60407,
                DroneshipName = "Just Read the Instructions", Style = PadStyle.Lc39A,
                FlameTrenchHeadingDeg = 0f, ShorelineOffsetM = 1500f,
            },
            new LaunchSite
            {
                Name = "SLC-4E, Vandenberg Space Force Base", ShortName = "SLC-4E",
                LatitudeDeg = 34.63209, LongitudeDeg = -120.61058, PrefersSouth = true,
                DroneshipName = "Of Course I Still Love You", Style = PadStyle.Slc4E,
                FlameTrenchHeadingDeg = 200f, ShorelineOffsetM = -2200f,
            },
        };
    }
}
