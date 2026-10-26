namespace Spaced
{
    /// <summary>Named places drawn on the globe: major cities and the world's spaceports.</summary>
    public static class Places
    {
        public struct Place
        {
            public string Name;
            public double LatitudeDeg;
            public double LongitudeDeg;
            public bool Spaceport;
        }

        static Place City(string name, double lat, double lon) => new Place { Name = name, LatitudeDeg = lat, LongitudeDeg = lon };
        static Place Port(string name, double lat, double lon) => new Place { Name = name, LatitudeDeg = lat, LongitudeDeg = lon, Spaceport = true };

        public static readonly Place[] All =
        {
            City("Tokyo", 35.68, 139.69), City("Delhi", 28.61, 77.21), City("Shanghai", 31.23, 121.47),
            City("São Paulo", -23.55, -46.63), City("Mexico City", 19.43, -99.13), City("Cairo", 30.04, 31.24),
            City("Mumbai", 19.08, 72.88), City("Beijing", 39.90, 116.41), City("Dhaka", 23.81, 90.41),
            City("New York", 40.71, -74.01), City("Karachi", 24.86, 67.01), City("Buenos Aires", -34.60, -58.38),
            City("Istanbul", 41.01, 28.98), City("Kolkata", 22.57, 88.36), City("Manila", 14.60, 120.98),
            City("Lagos", 6.52, 3.38), City("Rio de Janeiro", -22.91, -43.17), City("Moscow", 55.76, 37.62),
            City("Los Angeles", 34.05, -118.24), City("London", 51.51, -0.13), City("Paris", 48.86, 2.35),
            City("Jakarta", -6.21, 106.85), City("Seoul", 37.57, 126.98), City("Lima", -12.05, -77.04),
            City("Bangkok", 13.76, 100.50), City("Sydney", -33.87, 151.21), City("Johannesburg", -26.20, 28.05),
            City("Nairobi", -1.29, 36.82), City("Singapore", 1.35, 103.82), City("Toronto", 43.65, -79.38),
            City("Madrid", 40.42, -3.70), City("Berlin", 52.52, 13.40), City("Rome", 41.90, 12.50),
            City("Sofia", 42.70, 23.32), City("Athens", 37.98, 23.73), City("Warsaw", 52.23, 21.01),
            City("Houston", 29.76, -95.37), City("Miami", 25.76, -80.19), City("Orlando", 28.54, -81.38),
            City("Washington", 38.91, -77.04), City("Chicago", 41.88, -87.63), City("San Francisco", 37.77, -122.42),
            City("Seattle", 47.61, -122.33), City("Vancouver", 49.28, -123.12), City("Anchorage", 61.22, -149.90),
            City("Reykjavík", 64.15, -21.94), City("Cape Town", -33.92, 18.42), City("Honolulu", 21.31, -157.86),
            City("Auckland", -36.85, 174.76), City("Dubai", 25.20, 55.27), City("Riyadh", 24.71, 46.68),
            City("Tehran", 35.69, 51.39), City("Hong Kong", 22.32, 114.17), City("Santiago", -33.45, -70.67),
            City("Bogotá", 4.71, -74.07), City("Kinshasa", -4.44, 15.27), City("Stockholm", 59.33, 18.07),

            Port("Cape Canaveral / KSC", 28.57, -80.60), Port("Vandenberg", 34.63, -120.61),
            Port("Starbase", 26.00, -97.16), Port("Wallops", 37.94, -75.47),
            Port("Baikonur", 45.92, 63.34), Port("Plesetsk", 62.93, 40.58), Port("Vostochny", 51.88, 128.33),
            Port("Kourou (Guiana Space Centre)", 5.24, -52.77), Port("Jiuquan", 40.96, 100.29),
            Port("Wenchang", 19.61, 110.95), Port("Tanegashima", 30.40, 130.97), Port("Sriharikota", 13.72, 80.23),
            Port("Mahia (Rocket Lab)", -39.26, 177.86),
        };
    }
}
