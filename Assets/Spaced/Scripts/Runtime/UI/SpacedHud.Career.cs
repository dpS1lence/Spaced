using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Spaced
{
    /// <summary>Main menu, career top bar, contract board, company page and notification banners.</summary>
    public sealed partial class SpacedHud
    {
        public GameSession Session;
        public CareerManager Career;
        public WeatherService Weather;

        public int LaunchCounter
        {
            get => _launchCounter;
            set => _launchCounter = value;
        }

        bool CareerActive => Career != null && Career.Active;

        struct Toast
        {
            public string Title, Text;
            public float Time;
        }

        readonly List<Toast> _toasts = new List<Toast>();
        readonly Dictionary<int, int> _contractVehicle = new Dictionary<int, int>();
        readonly Dictionary<int, int> _contractSite = new Dictionary<int, int>();
        string _companyName = "Orbital Dynamics";
        string _contractMessage = "";
        bool _confirmNewCareer;
        Vector2 _contractScroll, _companyScroll;
        SaveData _careerSave, _sandboxSave;
        float _menuPeekTime = -100f;
        GUIStyle _menuTitle;

        public void AttachCareer(CareerManager career, GameSession session)
        {
            Career = career;
            Session = session;
            career.Notification += AddToast;
        }

        void AddToast(string title, string text)
        {
            _toasts.Add(new Toast { Title = title, Text = text, Time = Time.unscaledTime });
            if (_toasts.Count > 3) _toasts.RemoveAt(0);
        }

        /// <summary>The pad forecast for a launch at <paramref name="jd"/>, with the go/no-go call.</summary>
        void DrawPadWeather(LaunchSite site, LaunchVehicle vehicle, double jd)
        {
            if (Weather == null) return;
            var w = Weather.At(site, jd, vehicle.Recovery == BoosterRecovery.Droneship);
            if (!w.Known) { GUILayout.Label($"Weather at {site.ShortName}: no forecast for that date.", _small); return; }
            GUILayout.Label($"Weather at {site.ShortName}: {w.Summary} — " + (w.Go ? "<color=#9fe39f>GO</color>" : $"<color=#ff6a5a>NO-GO ({w.Reason})</color>"), _small);
        }

        void QuitToMenu()
        {
            Session.QuitToMenu();
            // A fresh scene is the simplest clean slate for whatever is picked next.
            var scene = SceneManager.GetActiveScene();
            if (scene.buildIndex >= 0) SceneManager.LoadScene(scene.buildIndex);
#if UNITY_EDITOR
            // Playing a scene that isn't in the build settings.
            else UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(scene.path, new LoadSceneParameters(LoadSceneMode.Single));
#endif
        }

        double OwnLaunchCost(LaunchVehicle vehicle, SatelliteClass satClass, int count, int perLaunch)
        {
            if (perLaunch <= 0) return double.PositiveInfinity;
            int planes = _launchMode == 1 ? math.max(1, _planes) : 1;
            int perPlane = (count + planes - 1) / planes;
            int launches = planes * ((perPlane + perLaunch - 1) / perLaunch);
            return launches * Prices.Launch(vehicle) + count * Prices.Satellite(satClass);
        }

        double OwnLaunchCost(LaunchVehicle vehicle, SatelliteClass satClass, List<OrbitSlot> slots, int perLaunch) =>
            perLaunch <= 0 ? double.PositiveInfinity : LaunchManager.CountLaunches(slots, perLaunch) * Prices.Launch(vehicle) + slots.Count * Prices.Satellite(satClass);

        // ───────────────────────────── Free roam ─────────────────────────────

        void ToggleFreeRoam()
        {
            if (_rig.Mode == ViewMode.Free)
            {
                _rig.ViewEarth();
                return;
            }
            // Nobody else steers the camera while you're flying it.
            _launches.StopWatching();
            _lunar.StopWatching();
            _rig.ViewFree();
        }

        void DrawFreeBar(Rect rect)
        {
            double jd = _sim.Clock.JulianDate;
            double3 camera = _rig.CameraEciKm;
            double earthKm = math.length(camera) - EarthConstants.RadiusKm;
            double moonKm = math.distance(camera, Astro.MoonPosition(jd)) - MoonConstants.RadiusKm;
            GUILayout.BeginArea(rect, _window);
            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>FREE ROAM</b>", _header, GUILayout.Width(96));
            if (GUILayout.Button("Earth", _button, GUILayout.Width(60))) _rig.FreeGoTo(FreeAnchor.Earth);
            if (GUILayout.Button("Moon", _button, GUILayout.Width(60))) _rig.FreeGoTo(FreeAnchor.Moon);
            if (GUILayout.Button("Earth + Moon", _button, GUILayout.Width(96))) _rig.FreeGoTo(FreeAnchor.Earth, true);
            foreach (var m in _lunar.Active())
            {
                var view = _lunar.View(m);
                if (view != null && GUILayout.Button(m.Name, _button, GUILayout.Width(90))) _lunar.ViewCraft(m);
                break;
            }
            GUILayout.FlexibleSpace();
            GUILayout.Label($"Earth {FormatKm(earthKm)} · Moon {FormatKm(moonKm)}" + (_rig.FreeSpeedKmS > 0 ? $" · {FormatSpeed(_rig.FreeSpeedKmS)}" : ""), _small);
            if (GUILayout.Button("Back to globe", _button, GUILayout.Width(96))) _rig.ViewEarth();
            GUILayout.EndHorizontal();
            GUILayout.Label("WASD / arrows fly · Q/E down/up · hold Shift for speed · drag to look around · scroll to zoom · " +
                            $"moving with the {(_rig.Anchor == FreeAnchor.Moon ? "Moon" : "Earth")} · G or Esc to leave", _small);
            GUILayout.EndArea();
        }

        /// <summary>Names on the Earth and the Moon while they're small in the frame, so you can find your way.</summary>
        void DrawBodyLabels()
        {
            double jd = _sim.Clock.JulianDate;
            Label(double3.zero, EarthConstants.RadiusKm, "Earth");
            Label(Astro.MoonPosition(jd), MoonConstants.RadiusKm, "Moon");

            void Label(double3 centerKm, double radiusKm, string name)
            {
                double distance = math.distance(_rig.CameraEciKm, centerKm);
                if (distance < radiusKm * 25) return;
                Vector3 screen = _rig.Far.WorldToScreenPoint(WorldFrame.ToFar(centerKm));
                if (screen.z <= 0) return;
                var rect = new Rect(screen.x / _scale + 10, (Screen.height - screen.y) / _scale - 9, 200, 18);
                GUI.Label(rect, $"<b>{name}</b>  <color=#9fb3c8>{FormatKm(distance - radiusKm)}</color>", _label);
            }
        }

        // ───────────────────────────── Superhero ─────────────────────────────

        public HeroController Hero;

        /// <summary>Superhero mode is a sandbox toy.</summary>
        bool HeroAvailable => Hero != null && !CareerActive && _sim.IsLoaded;

        void DrawHeroBar(Rect rect)
        {
            GUILayout.BeginArea(rect, _window);
            GUILayout.BeginHorizontal();
            GUILayout.Label("<b>SUPERHERO</b>", _header, GUILayout.Width(100));
            double kmh = Hero.SpeedMs * 3.6;
            string speed = kmh < 10000 ? $"{kmh:N0} km/h" : FormatSpeed(Hero.SpeedMs / 1000.0);
            string where = Hero.NearMoon ? "near the Moon" : $"over {math.abs(Hero.LatDeg):0.00}°{(Hero.LatDeg >= 0 ? "N" : "S")} {math.abs(Hero.LonDeg):0.00}°{(Hero.LonDeg >= 0 ? "E" : "W")}";
            string state = Hero.Grounded ? "standing" : Hero.Boosting ? "super speed" : Hero.SpeedMs > 343 && !Hero.NearMoon && Hero.AltitudeM < 40000 ? "supersonic" : "flying";
            GUILayout.Label($"{speed} · {FormatKm(Hero.AltitudeM / 1000.0)} up · {where} · {state}", _label);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button($"Face {Hero.FaceTargetName} (M)", _button, GUILayout.Width(160))) Hero.FaceTarget();
            if (GUILayout.Button("Leave (P)", _button, GUILayout.Width(90))) Hero.Leave();
            GUILayout.EndHorizontal();
            GUILayout.Label(Hero.MouseCaptured
                ? "Mouse look · W/A/S/D fly where you look · Space / Ctrl up / down · Shift super speed · M face the Moon · Scroll camera · Esc free the mouse"
                : "<b>Click the view to take control</b> · P to leave · speed grows with height: fly up to go faster, all the way to the Moon", _small);
            GUILayout.EndArea();
        }

        static string FormatKm(double km) => km < 1 ? $"{km * 1000:N0} m" : km < 10000 ? $"{km:N0} km" : $"{km / 1000:N0} thousand km";

        static string FormatSpeed(double kmS) => kmS < 1 ? $"{kmS * 1000:N0} m/s" : kmS < 1000 ? $"{kmS:N1} km/s" : $"{kmS:N0} km/s";

        // ───────────────────────────── Tabs ─────────────────────────────

        void DrawTabs()
        {
            if (!CareerActive)
            {
                if (_leftTab > 2) _leftTab = 0;
                _leftTab = GUILayout.Toolbar(_leftTab, new[] { "LAUNCH", "MY SATELLITES", $"GALLERY ({_fleet.Gallery.Count})" }, _button, GUILayout.Height(22));
                return;
            }
            // Career order: contracts first.
            int[] order = { 3, 0, 1, 2, 4 };
            int active = 0;
            foreach (var c in Career.Contracts) if (c.Status == ContractStatus.Active) active++;
            string[] names = { $"CONTRACTS ({active})", "LAUNCH", "FLEET", $"GALLERY ({_fleet.Gallery.Count})", "COMPANY" };
            int current = System.Array.IndexOf(order, _leftTab);
            int picked = GUILayout.Toolbar(math.max(current, 0), names, _button, GUILayout.Height(22));
            _leftTab = order[picked];
        }

        void DrawCareerTopBar()
        {
            if (!CareerActive) return;
            GUILayout.Space(12);
            string cash = Prices.Money(Career.Cash);
            GUILayout.Label($"<b>{Career.Company}</b>", _header, GUILayout.ExpandWidth(false));
            GUILayout.Label(Career.Cash < 0 ? $"<color=#ff6a5a><b>{cash}</b></color>" : $"<color=#9fe39f><b>{cash}</b></color>", _header, GUILayout.Width(80));
            GUILayout.Label($"★ {Career.Reputation:0}", _accent, GUILayout.Width(46));
            if (Career.SolarStorm) GUILayout.Label("<color=#ffb347>SOLAR STORM</color>", _small, GUILayout.Width(90));
        }

        // ───────────────────────────── Contracts ─────────────────────────────

        static readonly string[] TierNames = { "Newcomer", "Established", "Major player", "Industry leader" };

        void DrawContractsTab()
        {
            double jd = _sim.Clock.JulianDate;
            GUILayout.Label($"Reputation {Career.Reputation:0} — {TierNames[ContractBoard.Tier(Career.Reputation)]}. Better reputation brings bigger contracts.", _small);
            if (_contractMessage.Length > 0) GUILayout.Label(_contractMessage, _accent);

            _contractScroll = GUILayout.BeginScrollView(_contractScroll, GUILayout.Height(Mathf.Min(620, Screen.height / _scale - 200)));
            bool any = false;
            GUILayout.Label("ACTIVE", _header);
            foreach (var c in Career.Contracts)
                if (c.Status == ContractStatus.Active) { DrawContract(c, jd); any = true; }
            if (!any) GUILayout.Label("None — accept one from the board below.", _small);

            GUILayout.Space(6);
            GUILayout.Label("CONTRACT BOARD", _header);
            Contract accept = null, decline = null;
            foreach (var c in Career.Contracts)
            {
                if (c.Status != ContractStatus.Offered) continue;
                if (DrawOffer(c, jd, out bool declined)) accept = c;
                if (declined) decline = c;
            }
            if (accept != null) Career.Accept(accept);
            if (decline != null) Career.Decline(decline);

            GUILayout.Space(6);
            GUILayout.Label("RECENT", _header);
            int shown = 0;
            for (int i = Career.Contracts.Count - 1; i >= 0 && shown < 6; i--)
            {
                var c = Career.Contracts[i];
                if (c.Status != ContractStatus.Completed && c.Status != ContractStatus.Failed) continue;
                string mark = c.Status == ContractStatus.Completed ? "<color=#9fe39f>✓</color>" : "<color=#ff6a5a>✗</color>";
                GUILayout.Label($"{mark} {c.Title} — {c.Client} ({(c.Status == ContractStatus.Completed ? "+" + Prices.Money(c.Reward) : "−" + Prices.Money(c.Penalty))})", _small);
                shown++;
            }
            GUILayout.EndScrollView();
        }

        bool DrawOffer(Contract c, double jd, out bool declined)
        {
            declined = false;
            bool accepted = false;
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>{c.Title}</b>", _label);
            GUILayout.FlexibleSpace();
            GUILayout.Label($"<color=#9fe39f><b>{Prices.Money(c.Reward)}</b></color>", _label);
            GUILayout.EndHorizontal();
            GUILayout.Label($"{c.Client} · ★+{c.Reputation} · {c.DurationDays:0} days to deliver · offer ends in {FormatDuration((c.ExpiresJd - jd) * MathConst.SecondsPerDay)}", _small);
            GUILayout.Label(c.Description, _small);
            if (c.Starship && !Career.IsUnlocked(CareerUnlock.Starship)) GUILayout.Label("Needs the Starship programme.", _warning);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Accept", _button)) accepted = true;
            if (GUILayout.Button("Decline", _button, GUILayout.Width(70))) declined = true;
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            return accepted;
        }

        void DrawContract(Contract c, double jd)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>{c.Title}</b>", _label);
            GUILayout.FlexibleSpace();
            GUILayout.Label($"<color=#9fe39f><b>{Prices.Money(c.Reward)}</b></color>", _label);
            GUILayout.EndHorizontal();
            double left = (c.DeadlineJd - jd) * MathConst.SecondsPerDay;
            GUILayout.Label($"{c.Client} · due in {FormatDuration(left)}{(left < 3 * MathConst.SecondsPerDay ? " <color=#ff6a5a>— hurry!</color>" : "")}", _small);
            GUILayout.Label(c.Description, _small);

            if (c.Kind == ContractKind.Imaging)
            {
                GUILayout.Label("Your imaging satellites photograph it automatically on the first daylight pass within 30° of straight down, " +
                                "if the sky there is under 50% cloud.", _small);
                var target = _fleet.Targets.Find(t => t.Name == c.Place && !t.Captured);
                if (target != null) GUILayout.Label("Status: " + PassStatus(target), _accent);
                if (GUILayout.Button("Show on the globe", _button)) { _rig.FlyTo(c.LatitudeDeg, c.LongitudeDeg, 1.5e6); }
            }
            else if (!c.Launched)
            {
                DrawContractLaunch(c);
            }
            else if (c.IsLunar)
            {
                var mission = _lunar.Missions.Find(m => m.Contract == c.Id);
                var campaign = _launches.Campaigns.Find(k => k.Contract == c.Id);
                string status = mission != null ? LunarPhaseName(mission.Phase) : campaign != null && campaign.Missions.Count > 0 && campaign.Missions[0].Flight != null ? "climbing to orbit"
                    : campaign != null ? $"liftoff in {FormatDuration((campaign.Missions[0].LiftoffJd - jd) * MathConst.SecondsPerDay)}" : "";
                GUILayout.BeginHorizontal();
                GUILayout.Label("Status: " + status, _accent);
                if (mission != null && !mission.Finished && GUILayout.Button("Follow", _button, GUILayout.Width(60))) _lunar.Watch(mission);
                GUILayout.EndHorizontal();
            }
            else
            {
                var campaign = _launches.Campaigns.Find(k => k.Contract == c.Id);
                if (campaign != null)
                {
                    var next = campaign.Missions.Find(m => !m.Done && m.Flight == null && m.Scheduled);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"Deployed {campaign.Deployed}/{c.Count}" + (next != null ? $" · next liftoff in {FormatDuration((next.LiftoffJd - jd) * MathConst.SecondsPerDay)}" : ""), _accent);
                    if (next != null && GUILayout.Button("Go to T-1 min", _button, GUILayout.Width(90)))
                        _sim.Clock.JumpTo(Astro.ToDateTime(next.LiftoffJd - 65.0 / MathConst.SecondsPerDay));
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.EndVertical();
        }

        void DrawContractLaunch(Contract c)
        {
            bool starshipOk = Career.IsUnlocked(CareerUnlock.Starship);
            if (!_contractVehicle.TryGetValue(c.Id, out int vehicleIndex)) vehicleIndex = c.Starship ? 1 : 0;
            if (starshipOk)
            {
                vehicleIndex = GUILayout.Toolbar(vehicleIndex, new[] { "Falcon 9", "Starship" }, _button);
                _contractVehicle[c.Id] = vehicleIndex;
            }
            else vehicleIndex = 0;
            var vehicle = vehicleIndex == 1 ? LaunchVehicle.Starship : LaunchVehicle.Falcon9;
            if (c.IsLunar) vehicle = vehicleIndex == 1 ? LaunchVehicle.Starship : LaunchVehicle.Falcon9;

            // Pads you own that can fly it; polar orbits go best from Vandenberg.
            if (!_contractSite.TryGetValue(c.Id, out int siteIndex) || !LaunchSite.All[siteIndex].Supports(vehicle) || !Career.SiteAvailable(LaunchSite.All[siteIndex]))
                siteIndex = System.Array.FindIndex(LaunchSite.All, s => s.Supports(vehicle) && Career.SiteAvailable(s));
            if (siteIndex < 0)
            {
                GUILayout.Label("No pad for this rocket yet.", _warning);
                return;
            }
            GUILayout.BeginHorizontal();
            for (int i = 0; i < LaunchSite.All.Length; i++)
            {
                var site = LaunchSite.All[i];
                if (!site.Supports(vehicle) || !Career.SiteAvailable(site)) continue;
                if (GUILayout.Toggle(siteIndex == i, site.ShortName, _button)) siteIndex = i;
            }
            GUILayout.EndHorizontal();
            _contractSite[c.Id] = siteIndex;

            var pad = LaunchSite.All[siteIndex];
            if (!CareerManager.PlanContract(c, vehicle, pad, out int launches, out double cost))
            {
                GUILayout.Label($"Too heavy for {vehicle.Name}.", _warning);
                return;
            }
            double profit = c.Reward - cost;
            GUILayout.Label($"{launches} × {(c.IsLunar ? LaunchManager.LunarRocket(vehicle).Name : vehicle.Name)} · cost {Prices.Money(cost)} · profit " +
                            (profit >= 0 ? $"<color=#9fe39f>{Prices.Money(profit)}</color>" : $"<color=#ff6a5a>{Prices.Money(profit)}</color>"), _small);
            if (!c.IsLunar && c.InclinationDeg < pad.LatitudeDeg - 0.5)
                GUILayout.Label($"Below {pad.ShortName}'s latitude: the orbit will be a bit more inclined than asked.", _warning);
            if (!c.IsLunar) DrawContractPreview(c, pad);
            if (c.Starship && vehicle.Kind != VehicleKind.Starship) GUILayout.Label("This contract needs Starship.", _warning);
            GUI.enabled = Career.CanAfford(cost) && _sim.IsLoaded && (!c.Starship || vehicle.Kind == VehicleKind.Starship);
            if (GUILayout.Button(Career.CanAfford(cost) ? "Book the launch" : $"Need {Prices.Money(cost)}", _button, GUILayout.Height(26)))
            {
                Career.Fly(c, vehicle, pad, out _contractMessage);
            }
            GUI.enabled = true;
        }

        // ───────────────────────────── Company ─────────────────────────────

        void DrawCompanyTab()
        {
            double jd = _sim.Clock.JulianDate;
            var s = Career.State;
            _companyScroll = GUILayout.BeginScrollView(_companyScroll, GUILayout.Height(Mathf.Min(640, Screen.height / _scale - 180)));
            GUILayout.Label($"<b>{Career.Company}</b> — founded {Astro.ToDateTime(s.StartJd):d MMM yyyy}", _header);
            Row("Cash", Prices.Money(Career.Cash));
            Row("Reputation", $"{Career.Reputation:0} / 100 ({TierNames[ContractBoard.Tier(Career.Reputation)]})");
            Row("Satellite income", $"{Prices.Money(Career.ServiceIncomePerDay)} per day");
            Row("Next monthly bill", $"{Prices.Money(Prices.MonthlyOverhead + Career.OwnSatellites * Prices.PerSatellitePerMonth)} in {FormatDuration((Career.NextPayrollJd - jd) * MathConst.SecondsPerDay)}");
            Row("Your satellites", $"{Career.OwnSatellites:N0}");
            Row("Launches / Moon successes", $"{s.Launches} / {s.LunarSuccesses}");
            Row("Contracts delivered / failed", $"{s.ContractsDone} / {s.ContractsFailed}");
            Row("Earned / spent", $"{Prices.Money(s.Earned)} / {Prices.Money(s.Spent)}");
            GUILayout.Label("Your own broadband (Comms role) and navigation satellites earn money for the ground they cover. " +
                            "Imaging satellites sell their pictures.", _small);

            GUILayout.Space(6);
            GUILayout.Label("UPGRADES", _header);
            foreach (var unlock in new[] { CareerUnlock.Pad39A, CareerUnlock.Vandenberg, CareerUnlock.Starship })
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(CareerManager.UnlockName(unlock), _small);
                GUILayout.FlexibleSpace();
                if (Career.IsUnlocked(unlock)) GUILayout.Label("<color=#9fe39f>owned</color>", _small, GUILayout.Width(90));
                else
                {
                    GUI.enabled = Career.CanBuy(unlock);
                    string label = Career.Reputation < Prices.UnlockReputation(unlock) ? $"needs ★{Prices.UnlockReputation(unlock):0}" : Prices.Money(Prices.Unlock(unlock));
                    if (GUILayout.Button(label, _button, GUILayout.Width(90))) Career.Buy(unlock);
                    GUI.enabled = true;
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(6);
            GUILayout.Label("MILESTONES", _header);
            foreach (var (id, title) in CareerManager.AllMilestones())
                GUILayout.Label((Career.HasMilestone(id) ? "<color=#9fe39f>✓</color> " : "<color=#7f8fa6>○</color> ") + title, _small);

            GUILayout.Space(6);
            GUILayout.Label("ACCOUNTS", _header);
            int shown = 0;
            foreach (var e in Career.Ledger)
            {
                if (shown++ >= 14) break;
                string amount = e.Amount >= 0 ? $"<color=#9fe39f>+{Prices.Money(e.Amount)}</color>" : $"<color=#ff6a5a>{Prices.Money(e.Amount)}</color>";
                GUILayout.Label($"<color=#7f8fa6>{Astro.ToDateTime(e.JulianDate):MMM d}</color>  {amount}  {e.Text}", _small);
            }
            GUILayout.EndScrollView();
        }

        // ───────────────────────────── Overlays ─────────────────────────────

        void DrawCareerOverlays(float width, float height)
        {
            float now = Time.unscaledTime;
            _toasts.RemoveAll(t => now - t.Time > 8f);
            float y = 44;
            foreach (var t in _toasts)
            {
                float alpha = Mathf.Clamp01((8f - (now - t.Time)) / 1f) * Mathf.Clamp01((now - t.Time) / 0.25f);
                var rect = new Rect(width * 0.5f - 230, y, 460, 64);
                var old = GUI.color;
                GUI.color = new Color(1, 1, 1, alpha);
                GUILayout.BeginArea(rect, _window);
                GUILayout.Label($"<b>{t.Title}</b>", _title);
                GUILayout.Label(t.Text, _small);
                GUILayout.EndArea();
                GUI.color = old;
                y += 70;
            }
            if (Session != null && now - Session.StatusTime < 3f)
                GUI.Label(new Rect(width - 330, 38, 320, 20), Session.Status, _small);

            if (CareerActive && Career.Bankrupt)
            {
                var rect = new Rect(width * 0.5f - 220, height * 0.5f - 110, 440, 0);
                rect = GUILayout.Window(11, rect, _ =>
                {
                    GUILayout.Label("BANKRUPT", _menuTitle ?? _title);
                    GUILayout.Label($"{Career.Company} ran out of money. You flew {Career.State.Launches} launches, delivered {Career.State.ContractsDone} contracts " +
                                    $"and reached reputation {Career.Reputation:0}.", _small);
                    if (GUILayout.Button("Back to the menu", _button, GUILayout.Height(30))) QuitToMenu();
                }, "", _window);
                _blockingRects.Add(rect);
            }
        }

        // ───────────────────────────── Main menu ─────────────────────────────

        void DrawMainMenu(float width, float height)
        {
            if (_menuTitle == null) _menuTitle = new GUIStyle(_title) { fontSize = 40, alignment = TextAnchor.MiddleCenter };
            if (Time.unscaledTime - _menuPeekTime > 2f)
            {
                _menuPeekTime = Time.unscaledTime;
                _careerSave = GameSession.Peek(GameMode.Career);
                _sandboxSave = GameSession.Peek(GameMode.Sandbox);
            }
            var rect = new Rect(width * 0.5f - 240, height * 0.5f - 230, 480, 0);
            rect = GUILayout.Window(10, rect, _ =>
            {
                GUILayout.Label("SPACED", _menuTitle, GUILayout.Height(56));
                GUILayout.Label("Run a launch company under today's real sky — every satellite, every piece of debris.", _small);
                GUILayout.Space(10);

                GUILayout.Label("CAREER", _header);
                var career = _careerSave?.Career;
                if (career != null && career.Active)
                {
                    string state = career.Bankrupt ? "<color=#ff6a5a>bankrupt</color>" : $"{Prices.Money(career.Cash)} · ★ {career.Reputation:0}";
                    GUILayout.Label($"{career.Company} — {state} — {Astro.ToDateTime(_careerSave.JulianDate):d MMM yyyy}", _small);
                    GUI.enabled = !career.Bankrupt;
                    if (GUILayout.Button("Continue career", _button, GUILayout.Height(32))) Session.Continue(GameMode.Career);
                    GUI.enabled = true;
                }
                GUILayout.BeginHorizontal();
                GUILayout.Label("Company", _small, GUILayout.Width(60));
                _companyName = GUILayout.TextField(_companyName, 28);
                GUILayout.EndHorizontal();
                bool overwrite = career != null && career.Active && !career.Bankrupt;
                if (overwrite) _confirmNewCareer = GUILayout.Toggle(_confirmNewCareer, " Replace my saved career");
                GUI.enabled = !overwrite || _confirmNewCareer;
                if (GUILayout.Button("New career", _button, GUILayout.Height(32))) Session.StartNew(GameMode.Career, _companyName);
                GUI.enabled = true;
                GUILayout.Label($"Start with {Prices.Money(Prices.StartingCash)}, one pad and Falcon 9. Fly contracts, build constellations, reach the Moon.", _small);

                GUILayout.Space(10);
                GUILayout.Label("SANDBOX", _header);
                GUILayout.Label("Everything unlocked and free: launch anything, anywhere, and watch what happens.", _small);
                GUILayout.BeginHorizontal();
                GUI.enabled = _sandboxSave != null;
                if (GUILayout.Button("Continue sandbox", _button, GUILayout.Height(30))) Session.Continue(GameMode.Sandbox);
                GUI.enabled = true;
                if (GUILayout.Button("New sandbox", _button, GUILayout.Height(30))) Session.StartNew(GameMode.Sandbox);
                GUILayout.EndHorizontal();

                GUILayout.Space(8);
                if (Time.unscaledTime - Session.StatusTime < 5f) GUILayout.Label(Session.Status, _warning);
                GUILayout.Label($"Saved automatically every {GameSession.AutosaveMinutes:0} minutes and on exit, in {SaveGame.Folder}", _small);
            }, "", _window);
            _blockingRects.Add(rect);
        }
    }
}
