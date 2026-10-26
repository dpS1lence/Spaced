using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Spaced
{
    /// <summary>
    /// Career mode: you run a launch company. Take contracts from the board and fly them, earn from your own
    /// satellites (broadband and navigation coverage, imagery), pay the monthly bills, buy access to more pads and to
    /// Starship, and work your way up to the Moon. Reputation unlocks bigger contracts; running out of money for too
    /// long ends the career.
    /// </summary>
    [DefaultExecutionOrder(20)]
    public sealed class CareerManager : MonoBehaviour
    {
        const int MaxOffers = 5;

        static readonly (string id, string title, double cash, int reputation)[] MilestoneList =
        {
            ("first-launch", "First launch", 5, 2),
            ("first-landing", "First booster landing", 5, 2),
            ("first-catch", "Caught by the tower", 10, 4),
            ("first-contract", "First contract delivered", 0, 2),
            ("sats-10", "10 satellites in orbit", 0, 2),
            ("sats-100", "100 satellites in orbit", 20, 5),
            ("sats-1000", "1,000 satellites in orbit", 100, 8),
            ("starship", "First Starship flight", 0, 4),
            ("moon-flyby", "First lunar flyby", 25, 6),
            ("moon-orbit", "First spacecraft in lunar orbit", 40, 8),
            ("moon-landing", "First Moon landing", 80, 12),
            ("billion", "A billion in the bank", 0, 5),
        };

        CareerSave _s = new CareerSave();
        SpaceSimulation _sim;
        LaunchManager _launches;
        LunarMissionManager _lunar;
        FleetManager _fleet;
        CameraRig _rig;
        System.Random _rng = new System.Random(1);
        float _checkTimer;

        public bool Active => _s.Active;
        public bool Bankrupt => _s.Bankrupt;
        public string Company => _s.Company;
        public double Cash => _s.Cash;
        public double Reputation => _s.Reputation;
        public CareerUnlock Unlocks => (CareerUnlock)_s.Unlocks;
        public IReadOnlyList<Contract> Contracts => _s.Contracts;
        public IReadOnlyList<LedgerEntry> Ledger => _s.Ledger;
        public CareerSave State => _s;
        public double NextPayrollJd => _s.NextPayrollJd;
        public bool SolarStorm => _sim != null && _sim.Clock.JulianDate < _s.SolarStormUntilJd;
        /// <summary>Current income from your own satellites, $M per day.</summary>
        public double ServiceIncomePerDay { get; private set; }
        public int OwnSatellites { get; private set; }

        /// <summary>Raised for things worth a banner: (headline, detail).</summary>
        public event Action<string, string> Notification;

        public void Init(SpaceSimulation sim, LaunchManager launches, LunarMissionManager lunar, FleetManager fleet, CameraRig rig)
        {
            _sim = sim;
            _launches = launches;
            _lunar = lunar;
            _fleet = fleet;
            _rig = rig;
            _launches.FlightEvent += OnFlightEvent;
            _lunar.MissionFinished += OnLunarFinished;
            _fleet.ImageCaptured += OnImage;
        }

        public void StartNew(string company)
        {
            double jd = _sim.Clock.JulianDate;
            _s = new CareerSave
            {
                Active = true,
                Company = string.IsNullOrWhiteSpace(company) ? "Spaced Launch Co." : company.Trim(),
                Cash = Prices.StartingCash,
                StartJd = jd,
                NextOfferJd = jd + 2,
                NextPayrollJd = jd + Prices.PayrollDays,
                NextEventJd = jd + 12 + _rng.NextDouble() * 10,
                ServiceDayJd = jd + 1,
                Seed = Environment.TickCount,
            };
            _rng = new System.Random(_s.Seed);
            AddLedger(Prices.StartingCash, "Seed funding");
            for (int i = 0; i < 4; i++) Offer(jd);
            Notify($"Welcome, {_s.Company}", $"You have {Prices.Money(Prices.StartingCash)}. Take a contract from the board to get started.");
        }

        public void Deactivate() => _s = new CareerSave();

        // ───────────────────────────── Money ─────────────────────────────

        public bool CanAfford(double millions) => !_s.Active || _s.Cash >= millions;

        /// <summary>Pays for something; false (and nothing charged) if there isn't enough cash. Always true outside a career.</summary>
        public bool TryPay(double millions, string text)
        {
            if (!_s.Active) return true;
            if (_s.Bankrupt || _s.Cash < millions) return false;
            _s.Cash -= millions;
            _s.Spent += millions;
            AddLedger(-millions, text);
            return true;
        }

        void Earn(double millions, string text)
        {
            _s.Cash += millions;
            _s.Earned += millions;
            AddLedger(millions, text);
        }

        void AddLedger(double amount, string text)
        {
            _s.Ledger.Insert(0, new LedgerEntry { JulianDate = _sim.Clock.JulianDate, Amount = amount, Text = text });
            if (_s.Ledger.Count > 60) _s.Ledger.RemoveAt(_s.Ledger.Count - 1);
        }

        void AddReputation(double amount) => _s.Reputation = math.clamp(_s.Reputation + amount, 0, 100);

        void Notify(string title, string text)
        {
            _sim.Log($"{title} — {text}");
            Notification?.Invoke(title, text);
        }

        // ───────────────────────────── Unlocks ─────────────────────────────

        public bool IsUnlocked(CareerUnlock unlock) => !_s.Active || (_s.Unlocks & (int)unlock) != 0;

        public bool SiteAvailable(LaunchSite site)
        {
            if (!_s.Active) return true;
            int index = Array.IndexOf(LaunchSite.All, site);
            return index == 0 || index == 1 && IsUnlocked(CareerUnlock.Pad39A) || index == 2 && IsUnlocked(CareerUnlock.Vandenberg)
                   || index == 3 && IsUnlocked(CareerUnlock.Starship);
        }

        public bool VehicleAvailable(LaunchVehicle vehicle) => vehicle.Kind != VehicleKind.Starship || IsUnlocked(CareerUnlock.Starship);

        public bool CanBuy(CareerUnlock unlock) => _s.Active && !IsUnlocked(unlock) && _s.Reputation >= Prices.UnlockReputation(unlock) && _s.Cash >= Prices.Unlock(unlock);

        public void Buy(CareerUnlock unlock)
        {
            if (!CanBuy(unlock)) return;
            string name = UnlockName(unlock);
            TryPay(Prices.Unlock(unlock), name);
            _s.Unlocks |= (int)unlock;
            Notify("Unlocked: " + name, unlock == CareerUnlock.Starship ? "Starship and the Starbase pad are yours to fly." : "A new launch pad is available.");
        }

        public static string UnlockName(CareerUnlock unlock) =>
            unlock == CareerUnlock.Pad39A ? "Pad LC-39A (Kennedy)" : unlock == CareerUnlock.Vandenberg ? "Pad SLC-4E (Vandenberg, polar orbits)" : "Starship programme";

        // ───────────────────────────── Contracts ─────────────────────────────

        void Offer(double jd)
        {
            int offers = 0;
            foreach (var c in _s.Contracts) if (c.Status == ContractStatus.Offered) offers++;
            if (offers >= MaxOffers) return;
            var contract = ContractBoard.Generate(_rng, jd, _s.Reputation, Unlocks, _s.NextContractId);
            if (contract == null) return;
            _s.NextContractId++;
            _s.Contracts.Add(contract);
        }

        public void Accept(Contract c)
        {
            if (c.Status != ContractStatus.Offered) return;
            double jd = _sim.Clock.JulianDate;
            c.Status = ContractStatus.Active;
            c.AcceptedJd = jd;
            c.DeadlineJd = jd + c.DurationDays;
            if (c.Kind == ContractKind.Imaging) _fleet.AddTarget(c.LatitudeDeg, c.LongitudeDeg, c.Place);
            _sim.Log($"Contract accepted: {c.Title} for {c.Client} — {Prices.Money(c.Reward)}, due in {c.DurationDays:0} days");
        }

        public void Decline(Contract c)
        {
            if (c.Status == ContractStatus.Offered) _s.Contracts.Remove(c);
        }

        /// <summary>Cost (M$) and number of launches to fly a deploy / constellation / lunar contract with this rocket.</summary>
        public static bool PlanContract(Contract c, LaunchVehicle vehicle, LaunchSite site, out int launches, out double cost)
        {
            launches = 0;
            cost = 0;
            if (c.IsLunar)
            {
                var craft = LunarSpacecraft.For(c.LunarKind, vehicle.Kind);
                launches = 1;
                cost = Prices.Launch(LaunchManager.LunarRocket(vehicle)) + Prices.LunarCraft(craft);
                return true;
            }
            var plan = LaunchManager.Plan(vehicle, c.Class, site, c.AltitudeKm, c.InclinationDeg, c.Count);
            if (plan.TooHeavy) return false;
            launches = LaunchManager.CountLaunches(ContractSlots(c), plan.PerLaunch);
            cost = launches * Prices.Launch(vehicle);
            return true;
        }

        public static List<OrbitSlot> ContractSlots(Contract c) =>
            LaunchService.PlanWalker(c.AltitudeKm, c.InclinationDeg, c.Count, math.max(1, c.Planes), c.Planes > 1 ? 1 : 0, 0);

        /// <summary>Schedules the launch(es) for an accepted contract and pays for them.</summary>
        public bool Fly(Contract c, LaunchVehicle vehicle, LaunchSite site, out string message)
        {
            message = "";
            if (c.Status != ContractStatus.Active || c.Launched || !c.NeedsLaunch) return false;
            if (c.Starship && vehicle.Kind != VehicleKind.Starship) { message = "This contract needs Starship."; return false; }
            if (!PlanContract(c, vehicle, site, out int launches, out double cost)) { message = $"Too heavy for {vehicle.Name}."; return false; }
            if (!CanAfford(cost)) { message = $"You need {Prices.Money(cost)} to fly this."; return false; }

            string name = $"{c.Client} {c.Id}";
            LaunchCampaign campaign;
            if (c.IsLunar)
            {
                campaign = _launches.StartLunarCampaign(name, vehicle, site, c.LunarKind);
            }
            else
            {
                var group = _fleet.CreateGroup(name, c.Class, PayloadRole.None, true);
                campaign = _launches.StartCampaign(name, vehicle, c.Class, true, site, ContractSlots(c), c.Planes <= 1, false, group.Id);
            }
            if (campaign == null) { message = $"Too heavy for {vehicle.Name}."; return false; }
            campaign.Contract = c.Id;
            TryPay(cost, $"{launches} × {vehicle.Name} for “{c.Title}”");
            c.Launched = true;
            double minutes = (campaign.Missions[0].LiftoffJd - _sim.Clock.JulianDate) * 1440.0;
            message = $"{launches} launch(es) booked for {Prices.Money(cost)}. First liftoff in {(minutes < 120 ? $"{minutes:0} min" : $"{minutes / 60:0.0} h")}.";
            return true;
        }

        void Complete(Contract c)
        {
            c.Status = ContractStatus.Completed;
            c.ClosedJd = _sim.Clock.JulianDate;
            _s.ContractsDone++;
            Earn(c.Reward, $"Contract: {c.Title} ({c.Client})");
            AddReputation(c.Reputation);
            Notify("Contract complete!", $"{c.Client} paid {Prices.Money(c.Reward)} for “{c.Title}”. Reputation +{c.Reputation}.");
            Milestone("first-contract");
        }

        void Fail(Contract c, string why)
        {
            c.Status = ContractStatus.Failed;
            c.ClosedJd = _sim.Clock.JulianDate;
            _s.ContractsFailed++;
            if (c.Penalty > 0) { _s.Cash -= c.Penalty; _s.Spent += c.Penalty; AddLedger(-c.Penalty, $"Penalty: {c.Title}"); }
            AddReputation(-math.max(3, c.Reputation * 0.5));
            Notify("Contract failed", $"“{c.Title}” for {c.Client}: {why}. Penalty {Prices.Money(c.Penalty)}.");
        }

        /// <summary>Settles contracts whose launches have finished.</summary>
        void CheckContracts(double jd)
        {
            foreach (var c in _s.Contracts)
            {
                if (c.Status == ContractStatus.Offered && jd > c.ExpiresJd) c.Status = ContractStatus.Expired;
                if (c.Status != ContractStatus.Active) continue;

                if (c.Kind == ContractKind.Deploy || c.Kind == ContractKind.Constellation)
                {
                    var campaign = _launches.Campaigns.Find(k => k.Contract == c.Id);
                    if (campaign != null && campaign.Deployed >= c.Count) { Complete(c); continue; }
                    if (campaign != null && campaign.Complete) { Fail(c, $"only {campaign.Deployed} of {c.Count} satellites reached orbit"); continue; }
                    if (c.Launched && campaign == null) { Fail(c, "the launches were cancelled"); continue; }
                }
                else if (c.IsLunar && c.Launched)
                {
                    var campaign = _launches.Campaigns.Find(k => k.Contract == c.Id);
                    var mission = _lunar.Missions.Find(m => m.Contract == c.Id);
                    if (mission == null && (campaign == null || campaign.Complete)) { Fail(c, "the rocket never reached orbit"); continue; }
                }
                if (jd > c.DeadlineJd) Fail(c, "the deadline passed");
            }
            // Keep the history short.
            _s.Contracts.RemoveAll(c => (c.Status == ContractStatus.Expired || c.Status == ContractStatus.Completed || c.Status == ContractStatus.Failed) && jd - math.max(c.ClosedJd, c.ExpiresJd) > 60);
        }

        void OnImage(CapturedImage image)
        {
            if (!_s.Active || image.Night) return;
            var c = image.Target != null ? _s.Contracts.Find(k => k.Status == ContractStatus.Active && k.Kind == ContractKind.Imaging && k.Place == image.Target.Name) : null;
            if (c != null) Complete(c);
            else Earn(Prices.PerImageSold, $"Image sold ({image.Satellite})");
        }

        void OnLunarFinished(LunarMission mission)
        {
            if (!_s.Active) return;
            if (mission.Succeeded)
            {
                _s.LunarSuccesses++;
                Milestone(mission.Kind == LunarMissionKind.Flyby ? "moon-flyby" : mission.Kind == LunarMissionKind.Orbit ? "moon-orbit" : "moon-landing");
                if (mission.Phase == LunarPhase.Landed) Milestone("moon-orbit");
            }
            var c = mission.Contract >= 0 ? _s.Contracts.Find(k => k.Id == mission.Contract && k.Status == ContractStatus.Active) : null;
            if (c == null)
            {
                // Your own programme: the glory is the reward.
                AddReputation(mission.Succeeded ? (mission.Kind == LunarMissionKind.Landing ? 8 : 4) : -3);
                return;
            }
            bool done = mission.Succeeded && (int)mission.Kind >= (int)c.LunarKind;
            if (done) Complete(c);
            else Fail(c, mission.Phase == LunarPhase.Lost ? "the spacecraft was lost" : "the mission fell short");
        }

        void OnFlightEvent(LaunchMission mission, string text)
        {
            if (!_s.Active) return;
            if (text == "Liftoff")
            {
                _s.Launches++;
                Milestone("first-launch");
                if (mission.Campaign.Vehicle.Kind == VehicleKind.Starship) Milestone("starship");
            }
            else if (text.StartsWith("Touchdown")) { Milestone("first-landing"); AddReputation(0.5); }
            else if (text.StartsWith("Booster caught")) { Milestone("first-catch"); AddReputation(1); }
            else if (text.StartsWith("Booster lost")) AddReputation(-1);
            else if (text.StartsWith("Vehicle did not")) AddReputation(-6);
        }

        void Milestone(string id)
        {
            if (_s.Milestones.Contains(id)) return;
            foreach (var m in MilestoneList)
            {
                if (m.id != id) continue;
                _s.Milestones.Add(id);
                if (m.cash > 0) Earn(m.cash, $"Milestone bonus: {m.title}");
                AddReputation(m.reputation);
                Notify("Milestone: " + m.title, m.cash > 0 ? $"Investors are thrilled: +{Prices.Money(m.cash)}, reputation +{m.reputation}." : $"Reputation +{m.reputation}.");
                return;
            }
        }

        public static IEnumerable<(string id, string title)> AllMilestones()
        {
            foreach (var m in MilestoneList) yield return (m.id, m.title);
        }

        public bool HasMilestone(string id) => _s.Milestones.Contains(id);

        // ───────────────────────────── Frame ─────────────────────────────

        void Update()
        {
            if (!_s.Active || _sim == null || !_sim.IsLoaded || _s.Bankrupt) return;
            var clock = _sim.Clock;
            clock.Reverse = false; // a business only runs forwards
            double jd = clock.JulianDate;
            double dtDays = math.max(_sim.LastDeltaSeconds, 0) / MathConst.SecondsPerDay;

            // Income from your own satellites, credited as it accrues and booked once a day.
            double comms = _fleet.CommsCoveragePercent, nav = _fleet.NavCoveragePercent;
            ServiceIncomePerDay = comms * Prices.CommsPerCoveragePercentDay * (SolarStorm ? 0.6 : 1) + nav * Prices.NavPerCoveragePercentDay;
            double income = ServiceIncomePerDay * dtDays;
            _s.Cash += income;
            _s.Earned += income;
            _s.ServiceAccrued += income;
            if (jd >= _s.ServiceDayJd)
            {
                if (_s.ServiceAccrued > 0.0005) AddLedger(_s.ServiceAccrued, $"Satellite services (comms {comms:0.0}%, nav {nav:0.0}% coverage)");
                _s.ServiceAccrued = 0;
                _s.ServiceDayJd = jd + 1;
            }

            _checkTimer -= Time.unscaledDeltaTime;
            if (_checkTimer > 0) return;
            _checkTimer = 0.25f;

            OwnSatellites = 0;
            var catalog = _sim.Catalog;
            for (int i = 0; i < catalog.Count; i++)
            {
                var p = catalog.Physics[i];
                if (p.IsAlive && (p.Flags & ObjectFlags.UserOwned) != 0 && p.Category == ObjectCategory.UserSatellite) OwnSatellites++;
            }
            if (OwnSatellites >= 10) Milestone("sats-10");
            if (OwnSatellites >= 100) Milestone("sats-100");
            if (OwnSatellites >= 1000) Milestone("sats-1000");
            if (_s.Cash >= 1000) Milestone("billion");

            CheckContracts(jd);
            if (jd >= _s.NextOfferJd)
            {
                Offer(jd);
                _s.NextOfferJd = jd + 2.5 + _rng.NextDouble() * 3.5;
            }
            if (jd >= _s.NextPayrollJd)
            {
                double bill = Prices.MonthlyOverhead + OwnSatellites * Prices.PerSatellitePerMonth;
                _s.Cash -= bill;
                _s.Spent += bill;
                AddLedger(-bill, $"Monthly costs: staff, facilities, operating {OwnSatellites:N0} satellites");
                _s.NextPayrollJd += Prices.PayrollDays;
            }
            if (jd >= _s.NextEventJd) RandomEvent(jd);

            if (_rig != null) _rig.AuroraActivityLevel = SolarStorm ? 3f : 1f;

            // Running on empty: the bank gives you a while to turn things around.
            if (_s.Cash < 0)
            {
                double since = SaveGame.Unpack(_s.BrokeSinceJd);
                if (!double.IsNaN(since))
                {
                    if (jd - since > Prices.BankruptAfterDays)
                    {
                        _s.Bankrupt = true;
                        Notify("Bankrupt", $"{_s.Company} ran out of money. The board has called it a day.");
                    }
                }
                else
                {
                    _s.BrokeSinceJd = jd;
                    Notify("Out of money", $"Get back above zero within {Prices.BankruptAfterDays:0} days or the company goes under.");
                }
            }
            else _s.BrokeSinceJd = SaveGame.Unset;
        }

        void RandomEvent(double jd)
        {
            _s.NextEventJd = jd + 15 + _rng.NextDouble() * 20;
            double roll = _rng.NextDouble();
            if (roll < 0.3)
            {
                _s.SolarStormUntilJd = jd + 3;
                Notify("Solar storm!", "A coronal mass ejection hit the Earth: brilliant aurorae for three days, and your broadband income dips while it lasts.");
            }
            else if (roll < 0.55)
            {
                double grant = math.round(10 + _rng.NextDouble() * 25 + _s.Reputation * 0.5);
                Earn(grant, "Government research grant");
                Notify("Research grant", $"A space agency backs your work with {Prices.Money(grant)}.");
            }
            else if (roll < 0.7 && _s.Reputation >= 30)
            {
                double round = math.round(50 + _rng.NextDouble() * 100);
                Earn(round, "Investment round");
                Notify("Investors on board", $"A funding round raised {Prices.Money(round)}.");
            }
            else if (roll < 0.88)
            {
                var c = ContractBoard.Generate(_rng, jd, math.min(_s.Reputation + 20, 100), Unlocks, _s.NextContractId);
                if (c == null) return;
                _s.NextContractId++;
                c.Reward = math.round(c.Reward * 1.5);
                c.ExpiresJd = jd + 5;
                c.Title = "URGENT: " + c.Title;
                _s.Contracts.Add(c);
                Notify("Urgent contract", $"{c.Client} needs “{c.Title.Substring(8)}” and pays a premium: {Prices.Money(c.Reward)}. Offer ends in 5 days.");
            }
            else
            {
                double cost = math.round(4 + _rng.NextDouble() * 8);
                _s.Cash -= cost;
                _s.Spent += cost;
                AddLedger(-cost, "Insurance premiums");
                Notify("Insurance premiums up", $"Your insurer charged an extra {Prices.Money(cost)} this month.");
            }
        }

        // ───────────────────────────── Saving ─────────────────────────────

        public void WriteSave(SaveData data)
        {
            if (_s.Active && _sim.IsLoaded) CheckContracts(_sim.Clock.JulianDate);
            data.Career = _s;
        }

        public void RestoreSave(SaveData data)
        {
            _s = data.Career ?? new CareerSave();
            _rng = new System.Random(_s.Seed + _s.NextContractId * 7919);
        }
    }
}
