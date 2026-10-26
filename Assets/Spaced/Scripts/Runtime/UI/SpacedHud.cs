using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Spaced
{
    /// <summary>
    /// Prototype interface (IMGUI): time bar, launch panel, mission control, selected object, simulation stats
    /// and cleanup.
    /// </summary>
    public sealed class SpacedHud : MonoBehaviour
    {
        struct ConstellationPreset
        {
            public string Name;
            public int ClassIndex, Total, Planes, Phasing;
            public float Altitude, Inclination, Spread;
            public bool Maneuverable;
        }

        static readonly ConstellationPreset[] Presets =
        {
            new ConstellationPreset { Name = "One Starlink launch (20 sats, 1 plane)", ClassIndex = 2, Total = 20, Planes = 1, Phasing = 0, Altitude = 550, Inclination = 53, Maneuverable = true },
            new ConstellationPreset { Name = "Starlink shell (1,584 @ 550 km)", ClassIndex = 2, Total = 1584, Planes = 72, Phasing = 1, Altitude = 550, Inclination = 53, Maneuverable = true },
            new ConstellationPreset { Name = "Kuiper shell (1,156 @ 630 km)", ClassIndex = 2, Total = 1156, Planes = 34, Phasing = 1, Altitude = 630, Inclination = 51.9f, Maneuverable = true },
            new ConstellationPreset { Name = "Mega-constellation (30,000 @ 550 km)", ClassIndex = 2, Total = 30000, Planes = 300, Phasing = 1, Altitude = 550, Inclination = 53, Spread = 20, Maneuverable = true },
            new ConstellationPreset { Name = "CubeSat swarm (500, sun-synchronous)", ClassIndex = 0, Total = 500, Planes = 10, Phasing = 0, Altitude = 500, Inclination = 97.4f, Maneuverable = false },
            new ConstellationPreset { Name = "GEO belt (24 comsats)", ClassIndex = 3, Total = 24, Planes = 1, Phasing = 0, Altitude = 35786, Inclination = 28.5f, Maneuverable = true },
            new ConstellationPreset { Name = "Chaos: 100,000 dumb CubeSats", ClassIndex = 0, Total = 100000, Planes = 500, Phasing = 3, Altitude = 600, Inclination = 70, Spread = 100, Maneuverable = false },
        };

        SpaceSimulation _sim;
        CameraRig _rig;
        LaunchManager _launches;
        SatelliteRenderer _satellites;
        FleetManager _fleet;
        public SpacedAudio Audio;
        bool _soundOpen;

        // Left panel tabs and fleet state.
        int _leftTab;
        int _selectedGroup = -1;
        int _memberCursor;
        float _moveAltitude = 550;
        int _roleIndex = 2;
        int _lastClassIndex = -1;
        CapturedImage _preview;
        CapturedImage _viewer;
        GUIStyle _placeLabel, _portLabel, _targetLabel;
        Vector2 _groupScroll, _galleryScroll;
        static readonly string[] RoleNames = { "None", "Imaging", "Comms", "Navigation" };

        bool _visible = true, _showHelp, _launchOpen = true, _statsOpen = true, _missionOpen = true;
        float _scale = 1f;
        Rect _launchRect, _infoRect, _statsRect, _missionRect;
        readonly List<Rect> _blockingRects = new List<Rect>();

        // Launch parameters
        int _launchMode = 1;
        int _deployMode;          // 0 = Falcon 9 from a pad, 1 = instant
        int _siteIndex;
        int _classIndex = 2;
        float _perigee = 550, _apogee = 550, _altitude = 550, _inclination = 53, _raan, _spread;
        int _total = 20, _planes = 1, _phasing;
        bool _maneuverable = true, _watchAll;
        string _constellationName = "Spaced";
        int _launchCounter;
        string _lifetimeText = "";
        Vector4 _lifetimeKey;
        string _launchMessage = "";

        uint _infoId;
        string _infoLifetime = "";

        GUIStyle _window, _label, _small, _header, _title, _button, _accent, _warning;
        GUIStyle _callout, _clock, _gaugeValue, _gaugeLabel;

        // Webcast overlay state.
        LaunchMission _calloutMission;
        int _calloutCursor;
        string _calloutText = "";
        float _calloutShownAt = -10f;
        Texture2D _panelTexture;

        public void Init(SpaceSimulation sim, CameraRig rig, LaunchManager launches, SatelliteRenderer satellites, FleetManager fleet)
        {
            _sim = sim;
            _rig = rig;
            _launches = launches;
            _satellites = satellites;
            _fleet = fleet;
            _fleet.ImageCaptured += image => { _preview = image; };
        }

        /// <summary>True when the screen position (pixels, bottom-left origin) is over a HUD panel.</summary>
        public bool IsPointerOverUI(Vector2 screenPosition)
        {
            if (!_visible) return false;
            var gui = new Vector2(screenPosition.x, Screen.height - screenPosition.y) / _scale;
            foreach (var rect in _blockingRects)
                if (rect.Contains(gui)) return true;
            return false;
        }

        void Update()
        {
            if (_sim == null || _sim.Catalog == null || _rig == null) return;
            // Clicking into the 3D view takes focus away from text fields, so hotkeys work again.
            var mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame && !IsPointerOverUI(mouse.position.ReadValue()))
                GUIUtility.keyboardControl = 0;

            var keyboard = Keyboard.current;
            if (mouse != null && keyboard != null && mouse.leftButton.wasPressedThisFrame && keyboard.shiftKey.isPressed
                && _rig.Mode == ViewMode.Earth && _rig.CursorOnGlobe && !IsPointerOverUI(mouse.position.ReadValue()))
            {
                _fleet.AddTarget(_rig.CursorLatDeg, _rig.CursorLonDeg);
                _leftTab = 2;
            }
            if (keyboard == null || GUIUtility.keyboardControl != 0) return;
            var clock = _sim.Clock;

            if (keyboard.spaceKey.wasPressedThisFrame) clock.Paused = !clock.Paused;
            if (keyboard.periodKey.wasPressedThisFrame || keyboard.equalsKey.wasPressedThisFrame) clock.Faster();
            if (keyboard.commaKey.wasPressedThisFrame || keyboard.minusKey.wasPressedThisFrame) clock.Slower();
            if (keyboard.rKey.wasPressedThisFrame) clock.Reverse = !clock.Reverse;
            if (keyboard.nKey.wasPressedThisFrame) clock.JumpTo(System.DateTime.UtcNow);
            if (keyboard.fKey.wasPressedThisFrame && _sim.SelectedId != 0) _rig.ViewSatellite(_sim.SelectedId);
            if (keyboard.lKey.wasPressedThisFrame) _rig.LockToEarth = !_rig.LockToEarth;
            if (keyboard.escapeKey.wasPressedThisFrame) BackToEarth();
            if (keyboard.hKey.wasPressedThisFrame || keyboard.f1Key.wasPressedThisFrame) _visible = !_visible;
            if (keyboard.cKey.wasPressedThisFrame) _launches.CinematicMode = !_launches.CinematicMode;
            if (keyboard.mKey.wasPressedThisFrame) _leftTab = _leftTab == 1 ? 0 : 1;
        }

        void BackToEarth()
        {
            _launches.StopWatching();
            _sim.SelectedId = 0;
            _rig.ViewEarth();
        }

        void OnGUI()
        {
            if (_sim == null || _sim.Catalog == null || _launches == null) return;
            if (!_visible)
            {
                _blockingRects.Clear();
                return;
            }
            EnsureStyles();

            _scale = Mathf.Max(1f, Screen.height / 1080f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(_scale, _scale, 1f));
            float width = Screen.width / _scale, height = Screen.height / _scale;

            _blockingRects.Clear();
            var topBar = new Rect(0, 0, width, 34);
            DrawTopBar(topBar);
            _blockingRects.Add(topBar);

            if (Cinematic.Active)
            {
                // Broadcast mode: only the webcast overlay and a compact camera panel.
                DrawWebcast(width, height);
                _missionRect = GUILayout.Window(5, new Rect(10, 44, 330, 0), DrawMissionWindow, "", _window);
                _blockingRects.Add(_missionRect);
            }
            else
            {
                _launchRect = GUILayout.Window(1, new Rect(10, 44, 360, 0), DrawLeftPanel, "", _window);
                _blockingRects.Add(_launchRect);

                if (HasMissionContent())
                {
                    _missionRect = GUILayout.Window(5, new Rect(width * 0.5f - 210, 44, 420, 0), DrawMissionWindow, "", _window);
                    _blockingRects.Add(_missionRect);
                }

                if (_sim.SelectedIndex >= 0)
                {
                    _infoRect = GUILayout.Window(2, new Rect(width - 320, 44, 310, 0), DrawInfoWindow, "", _window);
                    _blockingRects.Add(_infoRect);
                }

                _statsRect = GUILayout.Window(3, new Rect(width - 320, height - _statsRect.height - 10, 310, 0), DrawStatsWindow, "", _window);
                _blockingRects.Add(_statsRect);

                if (_rig.Mode == ViewMode.Earth)
                {
                    DrawMapOverlays(width, height);
                    var bar = new Rect(width * 0.5f - 360, height - 50, 720, 44);
                    DrawMapBar(bar);
                    _blockingRects.Add(bar);
                }

                if (_viewer != null)
                {
                    var viewer = GUILayout.Window(8, new Rect(width * 0.5f - 420, 60, 840, 0), DrawViewerWindow, "", _window);
                    _blockingRects.Add(viewer);
                }
            }

            if (_soundOpen && Audio != null)
            {
                var sound = GUILayout.Window(7, new Rect(width - 270, 40, 260, 0), DrawSoundWindow, "", _window);
                _blockingRects.Add(sound);
            }

            if (_showHelp)
            {
                var help = GUILayout.Window(4, new Rect(width * 0.5f - 210, height * 0.5f - 170, 420, 0), DrawHelpWindow, "", _window);
                _blockingRects.Add(help);
            }
        }

        // ───────────────────────────── Top bar ─────────────────────────────

        void DrawTopBar(Rect rect)
        {
            var clock = _sim.Clock;
            GUILayout.BeginArea(rect, _window);
            GUILayout.BeginHorizontal();
            GUILayout.Label("SPACED", _title, GUILayout.Width(80));
            GUILayout.Label(clock.Utc.ToString("yyyy-MM-dd  HH:mm:ss") + " UTC", _header, GUILayout.Width(190));

            clock.Reverse = GUILayout.Toggle(clock.Reverse, "Rev", _button, GUILayout.Width(40));
            if (GUILayout.Button("−", _button, GUILayout.Width(26))) clock.Slower();
            if (GUILayout.Button(clock.Paused ? "Play" : "Pause", _button, GUILayout.Width(56))) clock.Paused = !clock.Paused;
            if (GUILayout.Button("+", _button, GUILayout.Width(26))) clock.Faster();
            string warp = clock.Paused ? "paused" : SimClock.FormatWarp(clock.Warp) + (clock.WarpLimited ? " (held)" : "");
            GUILayout.Label(warp, clock.WarpLimited ? _accent : _header, GUILayout.Width(110));
            if (GUILayout.Button("Now", _button, GUILayout.Width(44))) clock.JumpTo(System.DateTime.UtcNow);
            if (GUILayout.Button("Earth view", _button, GUILayout.Width(80))) BackToEarth();
            _rig.LockToEarth = GUILayout.Toggle(_rig.LockToEarth, "Lock to Earth", _button, GUILayout.Width(100));

            GUILayout.FlexibleSpace();
            GUILayout.Label(_rig.Mode == ViewMode.Earth ? "" : "Viewing: " + _rig.TargetLabel, _small);
            GUILayout.Space(10);
            GUILayout.Label(_sim.IsLoaded ? $"{_sim.AliveCount:N0} objects in orbit" : _sim.DataStatus, _header);
            GUILayout.Space(12);
            if (GUILayout.Button(Audio != null && Audio.Muted ? "Sound off" : "Sound", _button, GUILayout.Width(70))) _soundOpen = !_soundOpen;
            if (GUILayout.Button("?", _button, GUILayout.Width(26))) _showHelp = !_showHelp;
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        // ───────────────────────────── Launch ─────────────────────────────

        void DrawLeftPanel(int id)
        {
            GUILayout.BeginHorizontal();
            _leftTab = GUILayout.Toolbar(_leftTab, new[] { "LAUNCH", $"MY SATELLITES", $"GALLERY ({_fleet.Gallery.Count})" }, _button, GUILayout.Height(22));
            if (GUILayout.Button(_launchOpen ? "–" : "+", _button, GUILayout.Width(24))) _launchOpen = !_launchOpen;
            GUILayout.EndHorizontal();
            if (!_launchOpen) return;
            GUILayout.Space(4);
            if (_leftTab == 0) DrawLaunchTab();
            else if (_leftTab == 1) DrawFleetTab();
            else DrawGalleryTab();
        }

        void DrawLaunchTab()
        {

            _deployMode = GUILayout.Toolbar(_deployMode, new[] { "Falcon 9 from pad", "Instant (no rocket)" }, _button);
            if (_deployMode == 0)
            {
                var names = new string[LaunchSite.All.Length];
                for (int i = 0; i < names.Length; i++) names[i] = LaunchSite.All[i].ShortName;
                _siteIndex = GUILayout.Toolbar(_siteIndex, names, _button);
                GUILayout.Label(LaunchSite.All[_siteIndex].Name, _small);
            }
            GUILayout.Space(4);
            _launchMode = GUILayout.Toolbar(_launchMode, new[] { "Single", "Constellation" }, _button);
            GUILayout.Space(4);

            GUILayout.Label("Satellite class", _small);
            var classNames = new string[SatelliteClass.Presets.Length];
            for (int i = 0; i < classNames.Length; i++) classNames[i] = SatelliteClass.Presets[i].Name;
            _classIndex = GUILayout.SelectionGrid(_classIndex, classNames, 2, _button);
            var satClass = SatelliteClass.Presets[_classIndex];
            GUILayout.Label($"{satClass.MassKg:N0} kg · {satClass.SizeM:0.##} m span · drag {satClass.BallisticCoeff:0.0000} m²/kg", _small);
            if (_classIndex != _lastClassIndex)
            {
                _lastClassIndex = _classIndex;
                _roleIndex = (int)FleetManager.DefaultRole(satClass);
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("Payload", _small, GUILayout.Width(50));
            _roleIndex = GUILayout.Toolbar(_roleIndex, RoleNames, _button);
            GUILayout.EndHorizontal();
            GUILayout.Label(RoleDescription((PayloadRole)_roleIndex), _small);
            GUILayout.Space(4);

            if (_launchMode == 0)
            {
                _perigee = AltitudeSlider("Perigee", _perigee);
                _apogee = AltitudeSlider("Apogee", Mathf.Max(_apogee, _perigee));
                _inclination = Slider("Inclination", _inclination, 0, 180, "0.0°");
                if (_deployMode == 1) _raan = Slider("RAAN", _raan, 0, 360, "0°");
                else GUILayout.Label("RAAN is set by the moment of liftoff.", _small);
            }
            else
            {
                _altitude = AltitudeSlider("Altitude", _altitude);
                _inclination = Slider("Inclination", _inclination, 0, 180, "0.0°");
                _total = Mathf.RoundToInt(LogSlider("Satellites", _total, 1, 100000, "N0"));
                _planes = Mathf.Clamp(Mathf.RoundToInt(LogSlider("Planes", _planes, 1, Mathf.Max(1, Mathf.Min(_total, 1000)), "N0")), 1, Mathf.Max(1, _total));
                _phasing = Mathf.RoundToInt(Slider("Phasing (F)", _phasing, 0, Mathf.Max(0, _planes - 1), "0"));
                _spread = Slider("Altitude spread", _spread, 0, 200, "0 km");
            }

            _maneuverable = GUILayout.Toggle(_maneuverable, " Active (station-keeping, avoids collisions)");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", _small, GUILayout.Width(40));
            _constellationName = GUILayout.TextField(_constellationName, 24);
            GUILayout.EndHorizontal();

            UpdateLifetimeEstimate(satClass);
            GUILayout.Label(_lifetimeText, _small);

            int count = _launchMode == 0 ? 1 : _total;
            if (_deployMode == 0)
            {
                var site = LaunchSite.All[_siteIndex];
                float altitude = _launchMode == 0 ? Mathf.Max(_perigee, _apogee) : _altitude;
                var plan = LaunchManager.Plan(satClass, site, altitude, _inclination, count);
                if (plan.TooHeavy)
                    GUILayout.Label($"Too heavy: Falcon 9 lifts {plan.CapacityKg / 1000:0.0} t to this orbit. Use Instant.", _warning);
                else
                    GUILayout.Label($"Falcon 9: {plan.CapacityKg / 1000:0.0} t to this orbit → {Mathf.Min(plan.PerLaunch, count)} per launch, " +
                                    $"{plan.Launches:N0} launch(es), one every ≥{LaunchManager.PadTurnaroundDays} days", _small);
                if (_inclination < site.LatitudeDeg - 0.5f)
                    GUILayout.Label($"Below {site.ShortName}'s latitude ({site.LatitudeDeg:0.0}°): reached at the nearest inclination.", _warning);
                if (_launchMode == 1) _watchAll = GUILayout.Toggle(_watchAll, " Watch every launch (otherwise just the first)");
                GUI.enabled = !plan.TooHeavy && _sim.IsLoaded;
            }

            string action = _deployMode == 0 ? (_launchMode == 0 ? "Schedule launch" : $"Schedule {count:N0} satellites") : (_launchMode == 0 ? "Place satellite" : $"Place {count:N0} satellites");
            if (GUILayout.Button(action, _button, GUILayout.Height(28))) Launch(satClass);
            GUI.enabled = true;
            if (_launchMessage.Length > 0) GUILayout.Label(_launchMessage, _small);

            if (_launchMode == 1)
            {
                GUILayout.Space(4);
                GUILayout.Label("Presets", _small);
                foreach (var preset in Presets)
                    if (GUILayout.Button(preset.Name, _button)) ApplyPreset(preset);
            }

            GUILayout.Space(8);
            GUILayout.Label("CLEANUP", _header);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Deorbit my satellites", _button)) _launchMessage = $"Deorbiting {_launches.DeorbitMine():N0} objects.";
            if (GUILayout.Button("Remove mine now", _button)) _launchMessage = $"Removed {_launches.RemoveMine():N0} objects.";
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Cancel launches", _button)) _launchMessage = $"Cancelled {_launches.CancelPendingLaunches()} launch(es).";
            if (GUILayout.Button("Clear new debris", _button)) _launchMessage = $"Cleared {_launches.ClearGeneratedDebris():N0} fragments.";
            GUILayout.EndHorizontal();
        }

        void Launch(SatelliteClass satClass)
        {
            var catalog = _sim.Catalog;
            string name = string.IsNullOrWhiteSpace(_constellationName) ? "Spaced" : _constellationName.Trim();
            _launchCounter++;
            name = $"{name}-{_launchCounter}";

            List<OrbitSlot> slots;
            if (_launchMode == 0)
                slots = new List<OrbitSlot> { new OrbitSlot { Elements = OrbitMath.FromAltitudes(_perigee, _apogee, _inclination, _raan, 0, 0) } };
            else
                slots = LaunchService.PlanWalker(_altitude, _inclination, _total, _planes, _phasing, _spread);

            var group = _fleet.CreateGroup(name, satClass, (PayloadRole)_roleIndex, _maneuverable);
            if (_deployMode == 1)
            {
                int firstIndex = catalog.Count;
                int placed = LaunchService.PlaceInstantly(catalog, satClass, slots, _maneuverable, name, _sim.Breakups.MaxObjects, group.Id);
                _sim.RecordLaunch(placed, $"{satClass.Name} placed directly ({name})");
                if (placed > 0) _sim.SelectedId = catalog.Ids[firstIndex];
                _launchMessage = $"Placed {placed:N0} satellites.";
                return;
            }

            var campaign = _launches.StartCampaign(name, satClass, _maneuverable, LaunchSite.All[_siteIndex], slots, _launchMode == 0, _watchAll, group.Id);
            if (campaign == null)
            {
                _launchMessage = "Too heavy for Falcon 9.";
                return;
            }
            var first = campaign.Missions[0];
            double minutes = (first.LiftoffJd - _sim.Clock.JulianDate) * 1440.0;
            _launchMessage = $"{campaign.Missions.Count} launch(es) scheduled. First liftoff in {FormatDuration(minutes * 60)}" +
                             (_launches.Director ? " — time will slow down so you can watch." : ".");
        }

        void ApplyPreset(ConstellationPreset preset)
        {
            _classIndex = preset.ClassIndex;
            _total = preset.Total;
            _planes = preset.Planes;
            _phasing = preset.Phasing;
            _altitude = preset.Altitude;
            _inclination = preset.Inclination;
            _spread = preset.Spread;
            _maneuverable = preset.Maneuverable;
        }

        void UpdateLifetimeEstimate(SatelliteClass satClass)
        {
            float perigee = _launchMode == 0 ? _perigee : _altitude;
            float apogee = _launchMode == 0 ? _apogee : _altitude;
            var key = new Vector4(perigee, apogee, _inclination, satClass.BallisticCoeff);
            if (key == _lifetimeKey && _lifetimeText.Length > 0) return;
            _lifetimeKey = key;

            var elements = OrbitMath.FromAltitudes(perigee, apogee, _inclination, 0, 0, 0);
            _lifetimeText = "Lifetime once dead (no station-keeping): " + FormatLifetime(EstimateLifetimeSeconds(elements, satClass.BallisticCoeff));
        }

        static string RoleDescription(PayloadRole role)
        {
            switch (role)
            {
                case PayloadRole.Imaging: return "Camera: takes pictures of the ground below (Gallery tab).";
                case PayloadRole.Communications: return "Broadband relay: lights up its coverage on the globe.";
                case PayloadRole.Navigation: return "GNSS: a position fix needs 4 satellites in view.";
                default: return "No payload tools.";
            }
        }

        // ───────────────────────────── My satellites ─────────────────────────────

        void DrawFleetTab()
        {
            var groups = _fleet.Groups;
            GUILayout.BeginHorizontal();
            _fleet.ShowCoverage = GUILayout.Toggle(_fleet.ShowCoverage, " Show coverage on the globe");
            GUILayout.EndHorizontal();
            if (_fleet.CommsCoveragePercent > 0 || _fleet.NavCoveragePercent > 0)
                GUILayout.Label($"Comms coverage: <b>{_fleet.CommsCoveragePercent:0.0}%</b> of Earth · navigation fix: <b>{_fleet.NavCoveragePercent:0.0}%</b>", _small);

            if (groups.Count == 0)
            {
                GUILayout.Label("No satellites yet — launch some from the Launch tab.", _small);
                DrawMissionsList();
                return;
            }

            _groupScroll = GUILayout.BeginScrollView(_groupScroll, GUILayout.Height(Mathf.Min(150, 26 * groups.Count + 6)));
            foreach (var g in groups)
            {
                string status = g.Alive == 0 ? "none in orbit" : $"{g.Alive:N0} sats · {g.MeanAltitudeKm:N0} km" + (g.Maneuvering > 0 ? $" · {g.Maneuvering} manoeuvring" : "");
                bool selected = _selectedGroup == g.Id;
                if (GUILayout.Toggle(selected, $"{g.Name}  ·  {RoleNames[(int)g.Role]}  ·  {status}", _button) && !selected)
                {
                    _selectedGroup = g.Id;
                    _memberCursor = 0;
                    _moveAltitude = Mathf.Max(160f, (float)g.MeanAltitudeKm);
                }
            }
            GUILayout.EndScrollView();

            var group = _fleet.Group(_selectedGroup);
            if (group != null) DrawGroup(group);
            DrawMissionsList();
        }

        void DrawGroup(SatelliteGroup group)
        {
            GUILayout.Space(4);
            GUILayout.Label($"{group.Name} — {group.Class.Name}, {RoleNames[(int)group.Role]}", _header);
            if (group.Alive == 0)
            {
                GUILayout.Label("No satellites of this group are in orbit (still launching, deorbited or destroyed).", _small);
                return;
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("◀ Prev", _button)) StepMember(group, -1);
            if (GUILayout.Button("View satellite", _button)) StepMember(group, 0);
            if (GUILayout.Button("Next ▶", _button)) StepMember(group, 1);
            GUILayout.EndHorizontal();

            _moveAltitude = AltitudeSlider("New altitude", _moveAltitude);
            if (GUILayout.Button($"Move all {group.Alive:N0} to {_moveAltitude:N0} km (electric propulsion)", _button)) _fleet.ChangeAltitude(group, _moveAltitude);

            bool keep = GUILayout.Toggle(group.StationKeeping, " Station-keeping (cancel drag)");
            if (keep != group.StationKeeping) _fleet.SetStationKeeping(group, keep);
            bool avoid = GUILayout.Toggle(group.AvoidsCollisions, " Collision avoidance manoeuvres");
            if (avoid != group.AvoidsCollisions) _fleet.SetCollisionAvoidance(group, avoid);

            switch (group.Role)
            {
                case PayloadRole.Imaging:
                    GUILayout.Label($"Camera ≈ {group.CameraResolutionAt500Km * group.MeanAltitudeKm / 500:0.0} m/pixel at this altitude (imagery source: 10 m)", _small);
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Take picture now", _button))
                    {
                        var members = _fleet.Members(group, 4096);
                        int index = members[Mathf.Clamp(_memberCursor, 0, members.Count - 1)];
                        if (_fleet.Capture(index)) _leftTab = 2;
                    }
                    group.AutoCapture = GUILayout.Toggle(group.AutoCapture, " Auto (every 10 min, daylight)", _button);
                    GUILayout.EndHorizontal();
                    break;
                case PayloadRole.Communications:
                    GUILayout.Label($"Your comms fleets cover {_fleet.CommsCoveragePercent:0.0}% of Earth (25° minimum elevation).", _small);
                    break;
                case PayloadRole.Navigation:
                    GUILayout.Label($"Position fixes (≥4 satellites above 10°) available over {_fleet.NavCoveragePercent:0.0}% of Earth.", _small);
                    break;
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Deorbit group", _button)) _fleet.Deorbit(group);
            if (GUILayout.Button("Remove now", _button)) _fleet.Remove(group);
            GUILayout.EndHorizontal();
        }

        void StepMember(SatelliteGroup group, int step)
        {
            var members = _fleet.Members(group, 4096);
            if (members.Count == 0) return;
            _memberCursor = ((_memberCursor + step) % members.Count + members.Count) % members.Count;
            uint id = _sim.Catalog.Ids[members[_memberCursor]];
            _sim.SelectedId = id;
            _rig.ViewSatellite(id);
        }

        void DrawMissionsList()
        {
            if (_launches.Campaigns.Count == 0) return;
            GUILayout.Space(6);
            GUILayout.Label("MY MISSIONS", _header);
            double jd = _sim.Clock.JulianDate;
            foreach (var c in _launches.Campaigns)
            {
                int done = c.Missions.FindAll(m => m.Done).Count;
                var next = c.Missions.Find(m => !m.Done && m.Flight == null && m.Scheduled);
                string when = next == null ? "" : $" · next in {FormatDuration((next.LiftoffJd - jd) * MathConst.SecondsPerDay)}";
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{c.Name}: {c.Deployed:N0}/{c.Slots.Count:N0} deployed · launch {done}/{c.Missions.Count}{when}", _small);
                if (next != null && GUILayout.Button("Go to T-1 min", _button, GUILayout.Width(90)))
                    _sim.Clock.JumpTo(Astro.ToDateTime(next.LiftoffJd - 65.0 / MathConst.SecondsPerDay));
                GUILayout.EndHorizontal();
            }
        }

        // ───────────────────────────── Gallery ─────────────────────────────

        void DrawGalleryTab()
        {
            var gallery = _fleet.Gallery;
            if (gallery.Count == 0)
            {
                GUILayout.Label("No pictures yet. Launch satellites with the Imaging payload, then use My Satellites → Take picture.", _small);
                return;
            }

            var image = _preview ?? gallery[0];
            if (image.Texture != null)
            {
                var rect = GUILayoutUtility.GetRect(336, 336, GUILayout.ExpandWidth(false));
                GUI.DrawTexture(rect, image.Texture, ScaleMode.ScaleAndCrop);
                if (GUI.Button(rect, GUIContent.none, GUIStyle.none)) _viewer = image; // click to enlarge
                GUILayout.Label($"{image.Satellite} · {Astro.ToDateTime(image.JulianDate):yyyy-MM-dd HH:mm} UTC\n" +
                                $"{image.LatitudeDeg:0.000}°, {image.LongitudeDeg:0.000}° · {image.ResolutionM:0.0} m/px{(image.Night ? " · night pass" : "")}", _small);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Enlarge", _button)) _viewer = image;
                if (GUILayout.Button("Show on globe", _button)) ShowOnGlobe(image);
                if (GUILayout.Button("Save PNG", _button)) Save(image);
                GUILayout.EndHorizontal();
                if (image.SavedPath != null) GUILayout.Label("Saved to " + image.SavedPath, _small);
            }

            _galleryScroll = GUILayout.BeginScrollView(_galleryScroll, GUILayout.Height(120));
            GUILayout.BeginHorizontal();
            foreach (var item in gallery)
            {
                if (item.Texture == null) continue;
                if (GUILayout.Button(item.Texture, _button, GUILayout.Width(100), GUILayout.Height(100))) _preview = item;
            }
            GUILayout.EndHorizontal();
            GUILayout.EndScrollView();
            GUILayout.Label(ImageryMosaic.Attribution, _small);
            DrawTargetList();
        }

        void DrawTargetList()
        {
            GUILayout.Space(6);
            GUILayout.Label("IMAGING REQUESTS", _header);
            GUILayout.Label("Shift+click the globe to ask for a picture of that spot. The first imaging satellite to pass within 30° off-nadir in daylight takes it.", _small);
            for (int i = 0; i < _fleet.Targets.Count; i++)
            {
                var t = _fleet.Targets[i];
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{t.Name} · {t.LatitudeDeg:0.00}°, {t.LongitudeDeg:0.00}° · {(t.Captured ? "imaged" : "waiting for a pass")}", _small);
                if (t.Captured && GUILayout.Button("View", _button, GUILayout.Width(44))) _viewer = t.Image;
                if (GUILayout.Button("Go", _button, GUILayout.Width(30))) _rig.FlyTo(t.LatitudeDeg, t.LongitudeDeg, 60000);
                if (GUILayout.Button("×", _button, GUILayout.Width(22))) { _fleet.Targets.RemoveAt(i); GUILayout.EndHorizontal(); break; }
                GUILayout.EndHorizontal();
            }
        }

        void ShowOnGlobe(CapturedImage image)
        {
            _launches.StopWatching();
            _viewer = null;
            _rig.FlyTo(image.LatitudeDeg, image.LongitudeDeg, math.max(image.SwathKm * 1800.0, 8000.0));
        }

        void DrawViewerWindow(int id)
        {
            var image = _viewer;
            if (image?.Texture == null) { _viewer = null; return; }
            GUILayout.BeginHorizontal();
            GUILayout.Label(image.Target != null ? $"{image.Target.Name} — {image.Satellite}" : image.Satellite, _title);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("×", _button, GUILayout.Width(24))) _viewer = null;
            GUILayout.EndHorizontal();

            var rect = GUILayoutUtility.GetRect(820, 600, GUILayout.ExpandWidth(false));
            GUI.DrawTexture(rect, image.Texture, ScaleMode.ScaleToFit);
            // Scale bar in the corner: the frame spans three tiles ≈ the swath.
            float imageSide = Mathf.Min(rect.width, rect.height);
            double kmPerPixel = image.SwathKm / imageSide;
            double barKm = NiceLength(kmPerPixel * 150);
            float barPx = (float)(barKm / kmPerPixel);
            var barRect = new Rect(rect.center.x - imageSide * 0.5f + 16, rect.yMax - 26, barPx, 4);
            var previous = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTexture(barRect, Texture2D.whiteTexture);
            GUI.color = previous;
            GUI.Label(new Rect(barRect.x, barRect.y - 18, 160, 18), FormatLength(barKm), _label);

            GUILayout.Label($"{Astro.ToDateTime(image.JulianDate):yyyy-MM-dd HH:mm:ss} UTC · {image.LatitudeDeg:0.0000}°, {image.LongitudeDeg:0.0000}° · " +
                            $"altitude {image.AltitudeKm:N0} km · {image.OffNadirDeg:0.0}° off-nadir · swath {image.SwathKm:0.0} km · ~{image.ResolutionM:0} m/px" +
                            (image.Night ? " · night pass" : ""), _small);
            GUILayout.BeginHorizontal();
            int index = _fleet.Gallery.IndexOf(image);
            GUI.enabled = index > 0;
            if (GUILayout.Button("◀ Newer", _button)) _viewer = _fleet.Gallery[index - 1];
            GUI.enabled = index >= 0 && index < _fleet.Gallery.Count - 1;
            if (GUILayout.Button("Older ▶", _button)) _viewer = _fleet.Gallery[index + 1];
            GUI.enabled = true;
            if (GUILayout.Button("Show on globe", _button)) ShowOnGlobe(image);
            if (GUILayout.Button("Save PNG", _button)) Save(image);
            GUILayout.EndHorizontal();
            if (image.SavedPath != null) GUILayout.Label("Saved to " + image.SavedPath, _small);
            GUILayout.Label(ImageryMosaic.Attribution, _small);
        }

        // ───────────────────────────── Map ─────────────────────────────

        /// <summary>Bottom bar in globe view: cursor position, camera, heading, scale bar, quick actions.</summary>
        void DrawMapBar(Rect rect)
        {
            GUILayout.BeginArea(rect, _window);
            GUILayout.BeginHorizontal();
            string cursor = _rig.CursorOnGlobe ? FormatLatLon(_rig.CursorLatDeg, _rig.CursorLonDeg) : "—";
            if (_rig.CursorOnGlobe && GlobeTiles.Instance != null && _rig.CameraAltitudeKm < 2000)
                cursor += $"  {GlobeTiles.Instance.HeightAt(_rig.CursorLatDeg, _rig.CursorLonDeg):0} m";
            GUILayout.Label("Cursor  " + cursor, _label, GUILayout.Width(220));
            GUILayout.Label($"Camera {FormatLength(_rig.CameraAltitudeKm)}", _label, GUILayout.Width(120));
            GUILayout.Label($"Heading {((_rig.HeadingDeg % 360) + 360) % 360:000}° · tilt {_rig.TiltDeg:0}°", _label, GUILayout.Width(150));

            // Scale bar for the ground at the centre of the view.
            double kmPerPixel = _rig.MetersPerPixel / 1000.0 * _scale;
            double barKm = NiceLength(kmPerPixel * 90);
            float barPx = (float)(barKm / math.max(kmPerPixel, 1e-9));
            var bar = GUILayoutUtility.GetRect(100, 20, GUILayout.Width(100));
            var previous = GUI.color;
            GUI.color = new Color(1, 1, 1, 0.85f);
            GUI.DrawTexture(new Rect(bar.x, bar.y + 12, Mathf.Min(barPx, 100), 3), Texture2D.whiteTexture);
            GUI.color = previous;
            GUI.Label(new Rect(bar.x, bar.y - 4, 100, 16), FormatLength(barKm), _small);

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("N", _button, GUILayout.Width(26))) _rig.ResetNorth();
            if (GUILayout.Button("Target here", _button, GUILayout.Width(90))) { _fleet.AddTarget(_rig.FocusLatDeg, _rig.FocusLonDeg); _leftTab = 2; }
            GUILayout.EndHorizontal();
            if (_rig.CameraAltitudeKm < 20000)
                GUILayout.Label(ImageryMosaic.Attribution + " · " + TileDownloads.TerrainAttribution + " · " + TileDownloads.NightAttribution, _small);
            GUILayout.EndArea();
        }

        /// <summary>City and spaceport labels, and imaging requests, pinned to the globe.</summary>
        void DrawMapOverlays(float width, float height)
        {
            double jd = _sim.Clock.JulianDate;
            double3 camera = _rig.CameraEciKm;
            double cameraAltitude = _rig.CameraAltitudeKm;
            foreach (var place in Places.All)
            {
                double limit = place.Spaceport ? 30000 : 9000;
                if (cameraAltitude > limit) continue;
                DrawPin(place.LatitudeDeg, place.LongitudeDeg, place.Name, place.Spaceport ? _portLabel : _placeLabel,
                    place.Spaceport ? new Color(1f, 0.7f, 0.3f) : new Color(1f, 1f, 1f, 0.8f), jd, camera, width, height);
            }
            foreach (var target in _fleet.Targets)
                DrawPin(target.LatitudeDeg, target.LongitudeDeg, target.Name + (target.Captured ? " ✓" : " …"), _targetLabel,
                    new Color(1f, 0.35f, 0.75f), jd, camera, width, height);
        }

        void DrawPin(double lat, double lon, string text, GUIStyle style, Color color, double jd, double3 camera, float width, float height)
        {
            double3 eci = Astro.EcefToEci(Astro.GeoToEcef(lat, lon, 0), jd);
            // Behind the planet? The point faces away from the camera if (camera - p)·p < 0.
            if (math.dot(camera - eci, math.normalize(eci)) < 0) return;
            Vector3 screen = _rig.Far.WorldToScreenPoint(WorldFrame.ToFar(eci));
            if (screen.z <= 0) return;
            var gui = new Vector2(screen.x, Screen.height - screen.y) / _scale;
            if (gui.x < 0 || gui.y < 0 || gui.x > width || gui.y > height) return;
            var previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(gui.x - 2.5f, gui.y - 2.5f, 5, 5), Texture2D.whiteTexture);
            GUI.color = previous;
            GUI.Label(new Rect(gui.x + 6, gui.y - 9, 220, 18), text, style);
        }

        static double NiceLength(double km)
        {
            double magnitude = math.pow(10, math.floor(math.log10(math.max(km, 1e-6))));
            double n = km / magnitude;
            return (n < 2 ? 1 : n < 5 ? 2 : 5) * magnitude;
        }

        static string FormatLength(double km) =>
            km >= 1 ? $"{km:#,0.#} km" : $"{km * 1000:0} m";

        static string FormatLatLon(double lat, double lon) =>
            $"{math.abs(lat):0.0000}°{(lat >= 0 ? "N" : "S")}  {math.abs(lon):0.0000}°{(lon >= 0 ? "E" : "W")}";

        void Save(CapturedImage image)
        {
            string path = _fleet.Save(image);
            _launchMessage = "Saved " + path;
        }

        // ───────────────────────────── Mission control ─────────────────────────────

        bool HasMissionContent() => _launches.NextLaunch() != null || _launches.Watched != null || HasActive();

        bool HasActive()
        {
            foreach (var _ in _launches.ActiveMissions()) return true;
            return false;
        }

        void DrawMissionWindow(int id)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("MISSION CONTROL", _title);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(_missionOpen ? "–" : "+", _button, GUILayout.Width(24))) _missionOpen = !_missionOpen;
            GUILayout.EndHorizontal();
            if (!_missionOpen) return;

            var mission = _launches.Watched;
            if (mission == null)
                foreach (var active in _launches.ActiveMissions()) { mission = active; break; }

            if (mission?.Flight != null)
            {
                if (Cinematic.Active) DrawCameraButtons(mission);
                else DrawTelemetry(mission);
            }

            var next = _launches.NextLaunch();
            if (next != null)
            {
                double seconds = (next.LiftoffJd - _sim.Clock.JulianDate) * MathConst.SecondsPerDay;
                GUILayout.BeginHorizontal();
                GUILayout.Label($"Next: {next.Name} from {next.Campaign.Site.ShortName} in {FormatDuration(seconds)} " +
                                $"({next.SlotIndices.Count} × {next.Campaign.Class.Name})", _small);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Skip to T-1 min", _button, GUILayout.Width(110)))
                    _sim.Clock.JumpTo(Astro.ToDateTime(next.LiftoffJd - 65.0 / MathConst.SecondsPerDay));
                GUILayout.EndHorizontal();
            }

            _launches.Director = GUILayout.Toggle(_launches.Director, " Director: slow time and pick camera shots during launches");
            _launches.CinematicMode = GUILayout.Toggle(_launches.CinematicMode, " Cinematic (C): hide satellites and panels, light the rocket");
            if (_launches.Watched != null && GUILayout.Button("Stop watching", _button)) BackToEarth();
        }

        void DrawTelemetry(LaunchMission mission)
        {
            var flight = mission.Flight;
            var booster = flight.Booster;
            double t = flight.MissionTime;
            string clock = t < 0 ? $"T−{-t:0}s" : $"T+{(int)(t / 60):00}:{t % 60:00}";
            GUILayout.Label($"{mission.Name}   <b>{clock}</b>   {PhaseName(flight.Phase)}", _header);

            double speed = math.length(flight.AirVelocity);
            Row("Altitude", $"{flight.AltitudeKm:0.0} km");
            Row("Speed (surface / inertial)", $"{speed * 3.6:N0} km/h  /  {math.length(flight.Velocity) / 1000:0.00} km/s");
            Row("Downrange", $"{flight.DownrangeKm:N0} km");
            if (flight.Phase == FlightPhase.FirstStage) Row("Dynamic pressure", $"{flight.DynamicPressure / 1000:0.0} kPa");
            if (!flight.BoosterAttached || flight.Phase == FlightPhase.Complete)
            {
                flight.OrbitShape(out double peri, out double apo);
                Row("Orbit", peri < 0 ? "suborbital" : $"{peri:N0} × {apo:N0} km");
            }
            Bar("Stage 1 propellant", flight.Booster?.PropellantKg ?? flight.BoosterPropellantKg, Falcon9.BoosterPropKg);
            Bar("Stage 2 propellant", flight.SecondPropellantKg, Falcon9.SecondPropKg);
            if (booster != null)
                Row("Booster", $"{BoosterStatus(booster.Phase)} · {booster.AltitudeM / 1000:0.0} km · {math.length(booster.AirVelocity) * 3.6:N0} km/h");

            int from = Mathf.Max(0, flight.Events.Count - 4);
            for (int i = from; i < flight.Events.Count; i++)
            {
                var e = flight.Events[i];
                GUILayout.Label($"<color=#7f8fa6>T+{(int)(e.MissionTime / 60):00}:{e.MissionTime % 60:00}</color>  {e.Text}", _small);
            }

            DrawCameraButtons(mission);
        }

        void DrawCameraButtons(LaunchMission mission)
        {
            var flight = mission.Flight;
            var booster = flight.Booster;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Pad cam", _button)) { _launches.Watch(mission); _launches.Director = false; _launches.ViewPad(mission); }
            if (GUILayout.Button("Chase", _button)) { _launches.Watch(mission); _launches.Director = false; _launches.ViewChase(mission); }
            GUI.enabled = booster != null || flight.BoosterAttached;
            if (GUILayout.Button("Booster", _button)) { _launches.Watch(mission); _launches.Director = false; _launches.ViewBooster(mission); }
            GUI.enabled = mission.Droneship != null;
            if (GUILayout.Button("Droneship", _button)) { _launches.Watch(mission); _launches.Director = false; _launches.ViewDroneship(mission); }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Stage 2 onboard", _button)) { _launches.Watch(mission); _launches.Director = false; _launches.ViewSecondStageOnboard(mission); }
            GUI.enabled = booster != null || flight.BoosterAttached;
            if (GUILayout.Button("Booster onboard", _button)) { _launches.Watch(mission); _launches.Director = false; _launches.ViewBoosterOnboard(mission); }
            GUI.enabled = true;
            if (_launches.Watched != mission && GUILayout.Button("Watch", _button)) _launches.Watch(mission);
            GUILayout.EndHorizontal();
        }

        // ───────────────────────────── Webcast overlay ─────────────────────────────

        /// <summary>SpaceX-broadcast style overlay: stage gauges, mission clock, event timeline and callouts.</summary>
        void DrawWebcast(float width, float height)
        {
            var mission = _launches.Watched;
            var flight = mission?.Flight;
            if (flight == null) return;
            var booster = flight.Booster;
            UpdateCallout(mission);

            // Bottom band.
            var band = new Rect(0, height - 118, width, 118);
            var previous = GUI.color;
            GUI.color = new Color(0, 0, 0, 0.45f);
            GUI.DrawTexture(band, Texture2D.whiteTexture);
            GUI.color = previous;

            double t = flight.MissionTime;
            string sign = t < 0 ? "T−" : "T+";
            double a = math.abs(t);
            GUI.Label(new Rect(width * 0.5f - 160, height - 112, 320, 44), $"{sign}{(int)(a / 3600):00}:{(int)(a / 60) % 60:00}:{(int)a % 60:00}", _clock);
            GUI.Label(new Rect(width * 0.5f - 200, height - 70, 400, 20), mission.Name.ToUpperInvariant() + "  ·  " + mission.Campaign.Site.ShortName, _gaugeLabel);

            double3 s1Velocity = booster != null ? booster.AirVelocity : flight.AirVelocity;
            double s1Altitude = booster != null ? booster.AltitudeM / 1000.0 : flight.AltitudeKm;
            Gauge(new Rect(40, height - 112, 260, 100), "STAGE 1", math.length(s1Velocity) * 3.6, s1Altitude,
                booster == null ? "" : booster.Phase == BoosterPhase.Landed ? "LANDED" : booster.Phase == BoosterPhase.Lost ? "LOST" : BoosterStatus(booster.Phase).ToUpperInvariant());
            Gauge(new Rect(width - 300, height - 112, 260, 100), "STAGE 2", math.length(flight.AirVelocity) * 3.6, flight.AltitudeKm,
                flight.BoosterAttached ? "" : PhaseName(flight.Phase).ToUpperInvariant());

            DrawTimeline(new Rect(width * 0.5f - 330, height - 40, 660, 30), flight);

            // Countdown and callouts in the middle of the screen.
            float age = Time.unscaledTime - _calloutShownAt;
            string big = t < 0 && t > -10.5 ? $"{math.ceil(-t):0}" : age < 3.2f ? _calloutText : "";
            if (big.Length > 0)
            {
                float alpha = t < 0 && t > -10.5 ? 1f : Mathf.Clamp01(3.2f - age) * Mathf.Clamp01(age * 4f);
                GUI.color = new Color(1, 1, 1, alpha);
                GUI.Label(new Rect(0, height * 0.24f, width, 70), big, _callout);
                GUI.color = previous;
            }
        }

        void Gauge(Rect rect, string title, double speedKmh, double altitudeKm, string status)
        {
            GUI.Label(new Rect(rect.x, rect.y, rect.width, 18), title, _gaugeLabel);
            GUI.Label(new Rect(rect.x, rect.y + 18, rect.width * 0.5f, 34), $"{speedKmh:N0}", _gaugeValue);
            GUI.Label(new Rect(rect.x + rect.width * 0.5f, rect.y + 18, rect.width * 0.5f, 34), $"{altitudeKm:0.0}", _gaugeValue);
            GUI.Label(new Rect(rect.x, rect.y + 52, rect.width * 0.5f, 16), "SPEED KM/H", _gaugeLabel);
            GUI.Label(new Rect(rect.x + rect.width * 0.5f, rect.y + 52, rect.width * 0.5f, 16), "ALTITUDE KM", _gaugeLabel);
            if (status.Length > 0) GUI.Label(new Rect(rect.x, rect.y + 72, rect.width, 18), status, _accent);
        }

        /// <summary>Events so far on a line from T-60 s to a little past now, with a "now" marker.</summary>
        void DrawTimeline(Rect rect, RocketFlight flight)
        {
            double start = -60, end = math.max(600, flight.MissionTime + 120);
            var previous = GUI.color;
            GUI.color = new Color(1, 1, 1, 0.35f);
            GUI.DrawTexture(new Rect(rect.x, rect.y + 10, rect.width, 2), Texture2D.whiteTexture);
            float now = rect.x + rect.width * (float)((flight.MissionTime - start) / (end - start));
            GUI.color = new Color(0.55f, 0.85f, 1f, 0.9f);
            GUI.DrawTexture(new Rect(rect.x, rect.y + 10, Mathf.Max(0, now - rect.x), 2), Texture2D.whiteTexture);
            foreach (var e in flight.Events)
            {
                float x = rect.x + rect.width * (float)((e.MissionTime - start) / (end - start));
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(x - 2, rect.y + 7, 4, 8), Texture2D.whiteTexture);
            }
            GUI.color = new Color(1f, 0.8f, 0.35f);
            GUI.DrawTexture(new Rect(now - 1, rect.y + 2, 2, 18), Texture2D.whiteTexture);
            GUI.color = previous;
        }

        void UpdateCallout(LaunchMission mission)
        {
            if (_calloutMission != mission)
            {
                _calloutMission = mission;
                _calloutCursor = mission.Flight.Events.Count;
            }
            var events = mission.Flight.Events;
            for (; _calloutCursor < events.Count; _calloutCursor++)
            {
                string text = Callout(events[_calloutCursor].Text);
                if (text == null) continue;
                _calloutText = text;
                _calloutShownAt = Time.unscaledTime;
            }
        }

        static string Callout(string e)
        {
            if (e.StartsWith("Engine ignition")) return "IGNITION";
            if (e == "Liftoff") return "LIFTOFF";
            if (e.StartsWith("Max-Q")) return "MAX-Q";
            if (e.StartsWith("Main engine")) return "MECO";
            if (e.StartsWith("Stage separation")) return "STAGE SEPARATION";
            if (e.Contains("SES-1")) return "SECOND STAGE IGNITION";
            if (e.StartsWith("Fairing")) return "FAIRING SEPARATION";
            if (e.StartsWith("Entry burn startup")) return "ENTRY BURN";
            if (e.StartsWith("Landing burn")) return "LANDING BURN";
            if (e.StartsWith("Touchdown")) return "THE FALCON HAS LANDED";
            if (e.StartsWith("Booster lost")) return "BOOSTER LOST";
            if (e.Contains("SECO")) return "SECOND STAGE CUTOFF";
            if (e.Contains("SES-2")) return "SECOND STAGE RESTART";
            if (e.StartsWith("Payload")) return "DEPLOYMENT CONFIRMED";
            if (e.StartsWith("Vehicle did not")) return "MISSION FAILURE";
            return null;
        }

        static string PhaseName(FlightPhase phase)
        {
            switch (phase)
            {
                case FlightPhase.Countdown: return "Terminal count";
                case FlightPhase.FirstStage: return "First stage ascent";
                case FlightPhase.StageSeparation: return "Stage separation";
                case FlightPhase.SecondStage: return "Second stage burn";
                case FlightPhase.Coast: return "Coast to apoapsis";
                case FlightPhase.Circularization: return "Circularisation burn";
                case FlightPhase.Complete: return "Payload deployed";
                default: return "Mission failure";
            }
        }

        static string BoosterStatus(BoosterPhase phase)
        {
            switch (phase)
            {
                case BoosterPhase.Coasting: return "flip & coast";
                case BoosterPhase.EntryBurn: return "entry burn";
                case BoosterPhase.Descent: return "aerodynamic descent";
                case BoosterPhase.LandingBurn: return "landing burn";
                case BoosterPhase.Landed: return "landed";
                default: return "lost";
            }
        }

        void Bar(string label, double value, double max)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _small, GUILayout.Width(140));
            var rect = GUILayoutUtility.GetRect(100, 10, GUILayout.ExpandWidth(true), GUILayout.Height(10));
            rect.y += 4;
            var previous = GUI.color;
            GUI.color = new Color(1, 1, 1, 0.15f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = new Color(0.55f, 0.85f, 1f);
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width * Mathf.Clamp01((float)(value / max)), rect.height), Texture2D.whiteTexture);
            GUI.color = previous;
            GUILayout.Label($"{value / 1000:0.0} t", _label, GUILayout.Width(50));
            GUILayout.EndHorizontal();
        }

        // ───────────────────────────── Selected object ─────────────────────────────

        void DrawInfoWindow(int id)
        {
            int index = _sim.SelectedIndex;
            if (index < 0) return;
            var catalog = _sim.Catalog;
            var el = catalog.Elements[index];
            var physics = catalog.Physics[index];
            double3 r = catalog.PositionsKm[index];
            float3 v = catalog.VelocitiesKmS[index];
            var maneuver = catalog.Maneuvers[index];

            if (_infoId != catalog.Ids[index])
            {
                _infoId = catalog.Ids[index];
                _infoLifetime = FormatLifetime(EstimateLifetimeSeconds(el, physics.BallisticCoeff));
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label(catalog.Names[index], _title);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("×", _button, GUILayout.Width(24))) BackToEarth();
            GUILayout.EndHorizontal();

            string norad = catalog.NoradIds[index] > 0 ? $"NORAD {catalog.NoradIds[index]} · " : "";
            GUILayout.Label(norad + SatelliteRenderer.CategoryNames[(int)physics.Category], _small);
            GUILayout.Space(4);

            Row("Altitude", $"{math.length(r) - EarthConstants.RadiusKm:N0} km");
            Row("Speed", $"{math.length(v):0.00} km/s");
            Row("Perigee × apogee", $"{el.PerigeeAltitudeKm:N0} × {el.ApogeeAltitudeKm:N0} km");
            Row("Inclination", $"{el.I * MathConst.Rad2Deg:0.00}°");
            Row("Period", FormatPeriod(el.PeriodSeconds));
            Row("Mass (est.)", $"{physics.MassKg:N1} kg");
            Row("Size (est.)", $"{physics.RadiusM * 2:0.##} m");
            if ((physics.Flags & ObjectFlags.StationKeeping) != 0) Row("Drag", "cancelled by station-keeping");
            else Row("Decay rate", $"{-OrbitMath.DragDecayRate(el, physics.BallisticCoeff) * MathConst.SecondsPerDay * 1000:0.#} m/day");
            Row("Lifetime if dead", _infoLifetime);
            if (maneuver.Active != 0)
                Row("Manoeuvring", $"to {maneuver.Target.PerigeeAltitudeKm:N0} × {maneuver.Target.ApogeeAltitudeKm:N0} km, {FormatDuration(maneuver.RemainingSeconds)} left");

            var flags = new List<string>();
            if ((physics.Flags & ObjectFlags.UserOwned) != 0) flags.Add("yours");
            if ((physics.Flags & ObjectFlags.Maneuverable) != 0) flags.Add("avoids collisions");
            if ((physics.Flags & ObjectFlags.Damaged) != 0) flags.Add("damaged");
            if (flags.Count > 0) GUILayout.Label(string.Join(" · ", flags), _small);

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("View up close (F)", _button)) _rig.ViewSatellite(_sim.SelectedId);
            if (GUILayout.Button("Destroy (ASAT)", _button)) _sim.DestroyObject(index);
            GUILayout.EndHorizontal();
        }

        // ───────────────────────────── Stats ─────────────────────────────

        void DrawStatsWindow(int id)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("SIMULATION", _title);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(_statsOpen ? "–" : "+", _button, GUILayout.Width(24))) _statsOpen = !_statsOpen;
            GUILayout.EndHorizontal();
            if (!_statsOpen) return;

            for (int i = 0; i < SatelliteRenderer.CategoryNames.Length; i++)
            {
                GUILayout.BeginHorizontal();
                var swatch = GUILayoutUtility.GetRect(10, 10, GUILayout.Width(10), GUILayout.Height(10));
                swatch.y += 4;
                var previous = GUI.color;
                GUI.color = SatelliteRenderer.CategoryColors[i];
                GUI.DrawTexture(swatch, Texture2D.whiteTexture);
                GUI.color = previous;
                _satellites.CategoryVisible[i] = GUILayout.Toggle(_satellites.CategoryVisible[i], " " + SatelliteRenderer.CategoryNames[i]);
                GUILayout.FlexibleSpace();
                GUILayout.Label(_sim.CategoryCounts[i].ToString("N0"), _label);
                GUILayout.EndHorizontal();
            }

            var stats = _sim.Stats;
            GUILayout.Space(6);
            Row("Launched", stats.Launched.ToString("N0"));
            Row("Collisions", $"{stats.Collisions:N0} ({stats.CatastrophicCollisions:N0} catastrophic)");
            Row("Fragments created", stats.FragmentsCreated.ToString("N0"));
            Row("Reentries", stats.Reentries.ToString("N0"));
            Row("Collision rate", $"{_sim.Collisions.ExpectedPerYear:0.###} / year");

            GUILayout.Space(6);
            var collisions = _sim.Collisions;
            _sim.CollisionsEnabled = GUILayout.Toggle(_sim.CollisionsEnabled, " Simulate collisions");
            collisions.AvoidanceEfficiency = Slider("Avoidance success", collisions.AvoidanceEfficiency * 100f, 0, 100, "0'%'") / 100f;
            collisions.ProbabilityScale = LogSlider("Risk multiplier", collisions.ProbabilityScale, 1, 10000, "0.#×");

            GUILayout.Space(6);
            GUILayout.Label("EVENTS", _header);
            int shown = 0;
            foreach (var e in _sim.Events)
            {
                GUILayout.Label($"<color=#7f8fa6>{Astro.ToDateTime(e.JulianDate):MM-dd HH:mm}</color>  {e.Text}", _small);
                if (++shown >= 7) break;
            }
        }

        void DrawSoundWindow(int id)
        {
            GUILayout.Label("SOUND", _title);
            Audio.Muted = GUILayout.Toggle(Audio.Muted, " Mute everything");
            Audio.MusicVolume = Slider("Music", Audio.MusicVolume * 100f, 0, 100, "0'%'") / 100f;
            Audio.EffectsVolume = Slider("Effects", Audio.EffectsVolume * 100f, 0, 100, "0'%'") / 100f;
            GUILayout.Label("Rocket sound travels at 343 m/s, so a distant camera hears liftoff late. No sound in vacuum, except through the onboard cameras.", _small);
        }

        void DrawHelpWindow(int id)
        {
            GUILayout.Label("CONTROLS", _title);
            GUILayout.Label(
                "GLOBE: drag — move the map · right-drag (or Ctrl+drag) — rotate & tilt\n" +
                "Scroll — zoom to the cursor · double-click — fly there · Home / N button — north up\n" +
                "WASD / arrows — pan · Q/E — rotate · Z/X — tilt · PgUp/PgDn — zoom\n" +
                "Shift+click the globe — request a satellite image of that spot\n" +
                "Close-ups: drag — orbit · scroll — zoom (ground cameras: lens zoom)\n" +
                "Click an object — select it · F — view it up close\n" +
                "Esc — back to the Earth view\n" +
                "Space — pause   ·   + / − — time warp   ·   R — reverse\n" +
                "N — jump to now   ·   L — lock camera to Earth\n" +
                "M — My satellites   ·   C — cinematic launches\n" +
                "H — hide interface", _label);
            GUILayout.Space(6);
            GUILayout.Label(
                "Real objects come from CelesTrak (cached for 2 hours). Launches are flown physically: Falcon 9 gravity " +
                "turn, closed-loop second stage guidance, and a hoverslam landing on a droneship parked where the " +
                "booster will come down. Launch windows open when the pad passes under each target orbit plane.", _small);
            if (GUILayout.Button("Close", _button)) _showHelp = false;
        }

        // ───────────────────────────── Helpers ─────────────────────────────

        void Row(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _small);
            GUILayout.FlexibleSpace();
            GUILayout.Label(value, _label);
            GUILayout.EndHorizontal();
        }

        float Slider(string label, float value, float min, float max, string format)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _small, GUILayout.Width(110));
            value = GUILayout.HorizontalSlider(value, min, max, GUILayout.ExpandWidth(true));
            GUILayout.Label(value.ToString(format), _label, GUILayout.Width(70));
            GUILayout.EndHorizontal();
            return value;
        }

        float LogSlider(string label, float value, float min, float max, string format)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _small, GUILayout.Width(110));
            float t = Mathf.Log(Mathf.Clamp(value, min, max) / min) / Mathf.Max(Mathf.Log(max / min), 1e-6f);
            t = GUILayout.HorizontalSlider(t, 0f, 1f, GUILayout.ExpandWidth(true));
            value = min * Mathf.Exp(t * Mathf.Log(max / min));
            GUILayout.Label(value.ToString(format), _label, GUILayout.Width(70));
            GUILayout.EndHorizontal();
            return value;
        }

        float AltitudeSlider(string label, float valueKm) => Mathf.Round(LogSlider(label, valueKm, 160, 40000, "#,0' km'"));

        /// <summary>Integrates drag decay with adaptive steps until reentry (or 500 years).</summary>
        static double EstimateLifetimeSeconds(OrbitElements el, double ballisticCoeff)
        {
            const double limit = 500 * MathConst.SecondsPerYear;
            double t = 0;
            for (int i = 0; i < 4000 && t < limit; i++)
            {
                if (el.PerigeeAltitudeKm < EarthConstants.ReentryAltitudeKm) return t;
                double rate = math.abs(OrbitMath.DragDecayRate(el, ballisticCoeff));
                if (rate < 1e-14) return double.PositiveInfinity;
                double step = math.clamp(2.0 / rate, 3600.0, 30 * MathConst.SecondsPerDay);
                OrbitMath.Advance(ref el, ballisticCoeff, step);
                t += step;
            }
            return el.PerigeeAltitudeKm < EarthConstants.ReentryAltitudeKm ? t : double.PositiveInfinity;
        }

        static string FormatLifetime(double seconds)
        {
            if (double.IsInfinity(seconds)) return "centuries+";
            if (seconds < MathConst.SecondsPerDay * 60) return $"~{seconds / MathConst.SecondsPerDay:0} days";
            if (seconds < MathConst.SecondsPerYear * 2) return $"~{seconds / (MathConst.SecondsPerYear / 12):0} months";
            return $"~{seconds / MathConst.SecondsPerYear:0} years";
        }

        static string FormatDuration(double seconds)
        {
            seconds = math.max(0, seconds);
            if (seconds < 120) return $"{seconds:0} s";
            if (seconds < 7200) return $"{seconds / 60:0} min";
            if (seconds < 2 * MathConst.SecondsPerDay) return $"{seconds / 3600:0.0} h";
            return $"{seconds / MathConst.SecondsPerDay:0.0} days";
        }

        static string FormatPeriod(double seconds) =>
            seconds < 2 * 3600 ? $"{seconds / 60:0.0} min" : $"{seconds / 3600:0.00} h";

        void EnsureStyles()
        {
            if (_window != null) return;

            _panelTexture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            _panelTexture.SetPixel(0, 0, new Color(0.04f, 0.06f, 0.09f, 0.86f));
            _panelTexture.Apply();

            _window = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(10, 10, 6, 8),
                margin = new RectOffset(0, 0, 0, 0),
            };
            _window.normal.background = _panelTexture;
            _window.onNormal.background = _panelTexture;

            _label = new GUIStyle(GUI.skin.label) { fontSize = 12, richText = true };
            _label.normal.textColor = new Color(0.9f, 0.93f, 0.97f);
            _small = new GUIStyle(_label) { fontSize = 11, wordWrap = true };
            _small.normal.textColor = new Color(0.65f, 0.72f, 0.82f);
            _header = new GUIStyle(_label) { fontSize = 13, alignment = TextAnchor.MiddleLeft };
            _title = new GUIStyle(_label) { fontSize = 14, fontStyle = FontStyle.Bold };
            _title.normal.textColor = new Color(0.55f, 0.85f, 1f);
            _accent = new GUIStyle(_header);
            _accent.normal.textColor = new Color(1f, 0.8f, 0.35f);
            _warning = new GUIStyle(_small);
            _warning.normal.textColor = new Color(1f, 0.6f, 0.4f);
            _button = new GUIStyle(GUI.skin.button) { fontSize = 11 };

            _callout = new GUIStyle(_label) { fontSize = 46, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _callout.normal.textColor = Color.white;
            _clock = new GUIStyle(_label) { fontSize = 34, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _clock.normal.textColor = Color.white;
            _gaugeValue = new GUIStyle(_label) { fontSize = 26, fontStyle = FontStyle.Bold };
            _gaugeValue.normal.textColor = Color.white;
            _placeLabel = new GUIStyle(_label) { fontSize = 11 };
            _placeLabel.normal.textColor = new Color(1f, 1f, 1f, 0.85f);
            _portLabel = new GUIStyle(_label) { fontSize = 11, fontStyle = FontStyle.Bold };
            _portLabel.normal.textColor = new Color(1f, 0.75f, 0.4f);
            _targetLabel = new GUIStyle(_label) { fontSize = 12, fontStyle = FontStyle.Bold };
            _targetLabel.normal.textColor = new Color(1f, 0.45f, 0.8f);
            _gaugeLabel = new GUIStyle(_label) { fontSize = 11, alignment = TextAnchor.UpperLeft };
            _gaugeLabel.normal.textColor = new Color(0.75f, 0.8f, 0.88f);
        }

        void OnDestroy()
        {
            if (_panelTexture != null) Destroy(_panelTexture);
        }
    }
}
