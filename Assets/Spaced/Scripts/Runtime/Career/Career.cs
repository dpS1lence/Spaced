using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Spaced
{
    public enum ContractKind
    {
        /// <summary>Launch a customer's satellites to a given orbit.</summary>
        Deploy,
        /// <summary>Launch a whole constellation: many satellites over several planes and launches.</summary>
        Constellation,
        /// <summary>Photograph a place with one of your imaging satellites.</summary>
        Imaging,
        LunarFlyby,
        LunarOrbit,
        LunarLanding,
    }

    public enum ContractStatus
    {
        Offered,
        Active,
        Completed,
        Failed,
        Expired,
    }

    [Flags]
    public enum CareerUnlock
    {
        None = 0,
        Pad39A = 1 << 0,
        Vandenberg = 1 << 1,
        /// <summary>Starship and its pad at Starbase.</summary>
        Starship = 1 << 2,
    }

    [Serializable]
    public sealed class Contract
    {
        public int Id;
        public ContractKind Kind;
        public ContractStatus Status;
        public string Client, Title, Description;
        public double Reward, Penalty;
        public int Reputation;
        /// <summary>Offer leaves the board at this date if nobody takes it.</summary>
        public double ExpiresJd;
        /// <summary>Days allowed once accepted.</summary>
        public double DurationDays;
        public double AcceptedJd, DeadlineJd, ClosedJd;

        // Deploy / constellation
        public int ClassIndex, Count, Planes;
        public double AltitudeKm, InclinationDeg;
        /// <summary>Whether a launch has been scheduled for it (deploy, constellation and lunar contracts).</summary>
        public bool Launched;
        /// <summary>Lunar contracts that must be flown with Starship.</summary>
        public bool Starship;

        // Imaging
        public string Place;
        public double LatitudeDeg, LongitudeDeg;

        public bool IsLunar => Kind == ContractKind.LunarFlyby || Kind == ContractKind.LunarOrbit || Kind == ContractKind.LunarLanding;
        public bool NeedsLaunch => Kind != ContractKind.Imaging;
        public LunarMissionKind LunarKind => Kind == ContractKind.LunarFlyby ? LunarMissionKind.Flyby : Kind == ContractKind.LunarOrbit ? LunarMissionKind.Orbit : LunarMissionKind.Landing;
        public SatelliteClass Class => SatelliteClass.Presets[math.clamp(ClassIndex, 0, SatelliteClass.Presets.Length - 1)];
    }

    [Serializable]
    public struct LedgerEntry
    {
        public double JulianDate;
        public double Amount;
        public string Text;
    }

    /// <summary>The whole career in a save file.</summary>
    [Serializable]
    public sealed class CareerSave
    {
        public bool Active;
        public string Company = "";
        public double Cash, Reputation;
        public double StartJd, NextOfferJd, NextPayrollJd, NextEventJd, ServiceDayJd;
        public double ServiceAccrued;
        public int NextContractId = 1;
        public int Unlocks;
        public List<Contract> Contracts = new List<Contract>();
        public List<string> Milestones = new List<string>();
        public List<LedgerEntry> Ledger = new List<LedgerEntry>();
        public double BrokeSinceJd = SaveGame.Unset;
        public bool Bankrupt;
        public double Earned, Spent;
        public int Launches, LunarSuccesses, ContractsDone, ContractsFailed;
        public double SolarStormUntilJd;
        public int Seed = 1;
    }

    /// <summary>Prices in millions of US dollars.</summary>
    public static class Prices
    {
        public const double StartingCash = 250;
        public const double MonthlyOverhead = 6;
        public const double PerSatellitePerMonth = 0.01;
        public const double PayrollDays = 30;
        public const double BankruptAfterDays = 45;

        public static double Launch(LaunchVehicle vehicle) =>
            vehicle.Kind == VehicleKind.Starship ? 90 : vehicle.Recovery == BoosterRecovery.Expended ? 97 : 67;

        /// <summary>Building one of your own satellites (customer satellites are supplied by the customer).</summary>
        public static double Satellite(SatelliteClass satClass)
        {
            switch (satClass.Model)
            {
                case ModelKind.CubeSat: return 0.2;
                case ModelKind.Smallsat: return 4;
                case ModelKind.Starlink: return 1.2;
                case ModelKind.Comsat: return 180;
                case ModelKind.Station: return 3000;
                default: return 5;
            }
        }

        public static double LunarCraft(LunarSpacecraft craft) =>
            craft.IsShip ? 250 : craft.Name.Contains("lander") ? 160 : craft.Name.Contains("orbiter") ? 70 : 20;

        // Income from your own satellites, per day.
        public const double CommsPerCoveragePercentDay = 0.12;
        public const double NavPerCoveragePercentDay = 0.05;
        public const double PerImageSold = 0.02;

        public static double Unlock(CareerUnlock unlock) =>
            unlock == CareerUnlock.Pad39A ? 40 : unlock == CareerUnlock.Vandenberg ? 60 : unlock == CareerUnlock.Starship ? 400 : 0;

        public static double UnlockReputation(CareerUnlock unlock) =>
            unlock == CareerUnlock.Pad39A ? 8 : unlock == CareerUnlock.Vandenberg ? 15 : unlock == CareerUnlock.Starship ? 40 : 0;

        public static string Money(double millions)
        {
            double a = math.abs(millions);
            string sign = millions < 0 ? "−" : "";
            if (a >= 1000) return $"{sign}${a / 1000:0.00}B";
            if (a >= 10) return $"{sign}${a:0}M";
            if (a >= 1) return $"{sign}${a:0.0}M";
            if (a < 0.0005) return "$0";
            return $"{sign}${a * 1000:0}k";
        }
    }

    /// <summary>Makes up the offers on the contract board, harder and better paid as your reputation grows.</summary>
    public static class ContractBoard
    {
        static readonly string[] Clients =
        {
            "Helios Comms", "NovaSat", "AgriWatch", "Polar Research Institute", "Meridian Navigation", "Bluewave Telecom",
            "Orbital Insight Labs", "Aurora University", "Terra Monitor", "Skyline Broadband", "Cobalt Defence Systems",
            "Lumen Media", "Northstar Weather", "Atlas Mapping", "Ministry of Science", "Selene Ventures", "Tranquility Mining",
        };

        static readonly (string name, double lat, double lon)[] Places =
        {
            ("the Pyramids of Giza", 29.9792, 31.1342), ("the Grand Canyon", 36.1069, -112.1129), ("Mount Everest", 27.9881, 86.9250),
            ("Tokyo Bay", 35.45, 139.85), ("Paris", 48.8566, 2.3522), ("the Sydney Opera House", -33.8568, 151.2153),
            ("the Palm Jumeirah", 25.1124, 55.1390), ("the Panama Canal", 9.08, -79.68), ("Venice", 45.4408, 12.3155),
            ("Kilimanjaro", -3.0674, 37.3556), ("the Amazon at Manaus", -3.119, -60.0217), ("the Golden Gate", 37.8199, -122.4783),
            ("Mont Saint-Michel", 48.6361, -1.5115), ("Uluru", -25.3444, 131.0369), ("the Nile delta", 31.0, 31.0),
            ("Mount Fuji", 35.3606, 138.7274), ("Iceland's Vatnajökull", 64.4, -16.8), ("the Strait of Gibraltar", 35.97, -5.5),
            ("Rio de Janeiro", -22.9068, -43.1729), ("Cape Town", -33.9249, 18.4241), ("the Richat Structure", 21.1240, -11.4017),
            ("Singapore harbour", 1.264, 103.84), ("the Matterhorn", 45.9763, 7.6586), ("New York", 40.7128, -74.0060),
            ("Lake Baikal", 53.5, 108.0), ("the Great Barrier Reef", -18.29, 147.7), ("Hong Kong", 22.3193, 114.1694),
        };

        static readonly (double alt, double inc)[] Orbits =
        {
            (500, 97.4), (550, 53), (600, 97.8), (400, 51.6), (800, 98.6), (1000, 99.5), (1200, 87.9), (700, 45),
        };

        public static int Tier(double reputation) => reputation < 15 ? 0 : reputation < 35 ? 1 : reputation < 60 ? 2 : 3;

        public static Contract Generate(System.Random rng, double jd, double reputation, CareerUnlock unlocks, int id)
        {
            int tier = Tier(reputation);
            for (int attempt = 0; attempt < 12; attempt++)
            {
                var c = TryGenerate(rng, tier, unlocks);
                if (c == null) continue;
                c.Id = id;
                c.Status = ContractStatus.Offered;
                c.Client = Clients[rng.Next(Clients.Length)];
                c.ExpiresJd = jd + 8 + rng.NextDouble() * 12;
                c.Penalty = math.round(c.Reward * 0.1 * 10) / 10;
                return c;
            }
            return null;
        }

        static Contract TryGenerate(System.Random rng, int tier, CareerUnlock unlocks)
        {
            double roll = rng.NextDouble();
            switch (tier)
            {
                case 0: return roll < 0.55 ? Deploy(rng, tier) : Imaging(rng, tier);
                case 1:
                    if (roll < 0.35) return Deploy(rng, tier);
                    if (roll < 0.55) return Imaging(rng, tier);
                    if (roll < 0.70) return Geo(rng);
                    if (roll < 0.88) return Constellation(rng, tier);
                    return Lunar(rng, ContractKind.LunarFlyby, false);
                case 2:
                    if (roll < 0.25) return Deploy(rng, tier);
                    if (roll < 0.40) return Imaging(rng, tier);
                    if (roll < 0.65) return Constellation(rng, tier);
                    if (roll < 0.75) return Geo(rng);
                    if (roll < 0.85) return Lunar(rng, ContractKind.LunarFlyby, false);
                    if (roll < 0.95) return Lunar(rng, ContractKind.LunarOrbit, false);
                    return Lunar(rng, ContractKind.LunarLanding, false);
                default:
                    if (roll < 0.15) return Deploy(rng, tier);
                    if (roll < 0.25) return Imaging(rng, tier);
                    if (roll < 0.50) return Constellation(rng, tier);
                    if (roll < 0.65) return Lunar(rng, ContractKind.LunarOrbit, false);
                    if (roll < 0.85) return Lunar(rng, ContractKind.LunarLanding, false);
                    return (unlocks & CareerUnlock.Starship) != 0 ? Lunar(rng, ContractKind.LunarLanding, true) : Geo(rng);
            }
        }

        /// <summary>Reward that covers the launches on Falcon 9 plus a margin, so taking the contract always pays if you fly it as planned.</summary>
        static double LaunchReward(System.Random rng, int launches, double margin) =>
            math.round(launches * Prices.Launch(LaunchVehicle.Falcon9) * (margin + rng.NextDouble() * 0.3));

        static Contract Deploy(System.Random rng, int tier)
        {
            var orbit = Orbits[rng.Next(Orbits.Length)];
            bool cube = rng.NextDouble() < (tier == 0 ? 0.5 : 0.25);
            int classIndex = cube ? 0 : 1;
            int count = cube ? 3 + rng.Next(10) : 1 + rng.Next(tier == 0 ? 4 : 10);
            var site = orbit.inc > 60 ? LaunchSite.All[2] : LaunchSite.All[0];
            var plan = LaunchManager.Plan(LaunchVehicle.Falcon9, SatelliteClass.Presets[classIndex], site, orbit.alt, orbit.inc, count);
            if (plan.TooHeavy) return null;
            string what = count == 1 ? $"a {SatelliteClass.Presets[classIndex].Name}" : $"{count} × {SatelliteClass.Presets[classIndex].Name}";
            return new Contract
            {
                Kind = ContractKind.Deploy,
                Title = $"Deploy {what}",
                Description = $"Put {what} into a {orbit.alt:0} km orbit inclined {orbit.inc:0.#}°.",
                ClassIndex = classIndex, Count = count, Planes = 1, AltitudeKm = orbit.alt, InclinationDeg = orbit.inc,
                Reward = LaunchReward(rng, plan.Launches, 1.25),
                Reputation = 2 + tier,
                DurationDays = 20 + rng.Next(20),
            };
        }

        static Contract Geo(System.Random rng)
        {
            const int classIndex = 3;
            double inc = 28.5;
            var plan = LaunchManager.Plan(LaunchVehicle.Falcon9, SatelliteClass.Presets[classIndex], LaunchSite.All[0], 35786, inc, 1);
            if (plan.TooHeavy) return null;
            return new Contract
            {
                Kind = ContractKind.Deploy,
                Title = "Launch a geostationary comsat",
                Description = "Deliver a 6-tonne communications satellite towards geostationary orbit (35,786 km).",
                ClassIndex = classIndex, Count = 1, Planes = 1, AltitudeKm = 35786, InclinationDeg = inc,
                Reward = LaunchReward(rng, plan.Launches, 1.5),
                Reputation = 5,
                DurationDays = 30 + rng.Next(20),
            };
        }

        static Contract Constellation(System.Random rng, int tier)
        {
            int count = tier <= 1 ? 20 + rng.Next(3) * 10 : tier == 2 ? 60 + rng.Next(5) * 20 : 200 + rng.Next(6) * 50;
            int planes = math.max(1, count / (tier <= 1 ? 20 : 20));
            double alt = 520 + rng.Next(5) * 20;
            double inc = rng.NextDouble() < 0.6 ? 53 : rng.NextDouble() < 0.5 ? 70 : 97.6;
            var site = inc > 60 ? LaunchSite.All[2] : LaunchSite.All[0];
            var plan = LaunchManager.Plan(LaunchVehicle.Falcon9, SatelliteClass.Presets[2], site, alt, inc, count);
            if (plan.TooHeavy) return null;
            // Each plane needs at least one launch of its own.
            int perPlane = (count + planes - 1) / planes;
            int launches = planes * (int)math.ceil(perPlane / (double)plan.PerLaunch);
            return new Contract
            {
                Kind = ContractKind.Constellation,
                Title = $"Build a {count}-satellite constellation",
                Description = $"Launch {count} Starlink-class satellites into {planes} plane(s) at {alt:0} km, {inc:0.#}°. " +
                              $"About {launches} Falcon 9 launches.",
                ClassIndex = 2, Count = count, Planes = planes, AltitudeKm = alt, InclinationDeg = inc,
                Reward = LaunchReward(rng, launches, 1.3),
                Reputation = 4 + tier * 2,
                DurationDays = math.round(launches * LaunchManager.PadTurnaroundDays * 1.6 + 25),
            };
        }

        static Contract Imaging(System.Random rng, int tier)
        {
            var place = Places[rng.Next(Places.Length)];
            return new Contract
            {
                Kind = ContractKind.Imaging,
                Title = $"Photograph {place.name}",
                Description = $"Take a daylight picture of {place.name} with one of your imaging satellites.",
                Place = place.name, LatitudeDeg = place.lat, LongitudeDeg = place.lon,
                Reward = math.round((3 + rng.NextDouble() * 6) * (1 + tier * 0.6) * 10) / 10,
                Reputation = 1 + tier,
                DurationDays = 10 + rng.Next(15),
            };
        }

        static Contract Lunar(System.Random rng, ContractKind kind, bool starship)
        {
            var vehicle = starship ? LaunchVehicle.Starship : LaunchVehicle.Falcon9;
            var craft = LunarSpacecraft.For(kind == ContractKind.LunarFlyby ? LunarMissionKind.Flyby : kind == ContractKind.LunarOrbit ? LunarMissionKind.Orbit : LunarMissionKind.Landing, vehicle.Kind);
            double cost = Prices.Launch(LaunchManager.LunarRocket(vehicle)) + Prices.LunarCraft(craft);
            string title = kind == ContractKind.LunarFlyby ? "Fly around the Moon" : kind == ContractKind.LunarOrbit ? "Put an orbiter around the Moon" : starship ? "Land a Starship on the Moon" : "Land on the Moon";
            string description = kind == ContractKind.LunarFlyby ? "Send a probe around the far side of the Moon and back to Earth."
                : kind == ContractKind.LunarOrbit ? "Brake a science orbiter into a low lunar orbit."
                : starship ? "Fly a Starship lander, refuelled in Earth orbit, to a soft landing on the Moon." : "Soft-land a robotic lander on the Moon.";
            return new Contract
            {
                Kind = kind,
                Title = title,
                Description = description + $" Your cost: about {Prices.Money(cost)}.",
                Starship = starship,
                Reward = math.round(cost * (starship ? 1.8 : 1.5 + rng.NextDouble() * 0.3)),
                Reputation = kind == ContractKind.LunarFlyby ? 8 : kind == ContractKind.LunarOrbit ? 12 : starship ? 25 : 18,
                DurationDays = 75,
            };
        }
    }
}
