using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Spaced
{
    /// <summary>
    /// Flies every <see cref="LunarMission"/> after its rocket reaches the parking orbit: advances them with the
    /// clock, logs their events, shows them (<see cref="CislunarView"/>) and, for the one being followed, slows time
    /// for each burn and picks the camera for each phase.
    /// </summary>
    [DefaultExecutionOrder(10)] // after LaunchManager, which resets the warp limit every frame
    public sealed class LunarMissionManager : MonoBehaviour
    {
        /// <summary>Real seconds of warning before each event of the followed mission.</summary>
        const double EventLeadSeconds = 0.6;
        const double BurnMaxWarp = 10;
        const double DescentMaxWarp = 5;

        public readonly List<LunarMission> Missions = new List<LunarMission>();
        public LunarMission Watched { get; private set; }
        public bool Director = true;
        /// <summary>Hide the Earth-orbit satellites (and lighten their simulation) while a Moon mission is followed.</summary>
        public bool HideSatellites = true;
        /// <summary>Raised once when a mission ends (landed, in orbit, home, or lost).</summary>
        public event System.Action<LunarMission> MissionFinished;

        SpaceSimulation _sim;
        CameraRig _rig;
        SatelliteRenderer _dots;
        MaterialLibrary _materials;
        Fx _fx;
        Shader _lineShader;
        readonly Dictionary<LunarMission, CislunarView> _views = new Dictionary<LunarMission, CislunarView>();
        readonly Dictionary<LunarMission, int> _eventCursor = new Dictionary<LunarMission, int>();
        readonly HashSet<LunarMission> _reported = new HashSet<LunarMission>();
        int _directorStage = -1;

        public void Init(SpaceSimulation sim, CameraRig rig, SatelliteRenderer dots, MaterialLibrary materials, Fx fx, Shader lineShader)
        {
            _sim = sim;
            _rig = rig;
            _dots = dots;
            _materials = materials;
            _fx = fx;
            _lineShader = lineShader;
        }

        /// <summary>Takes over from the rocket in its parking orbit.</summary>
        public LunarMission Begin(string name, LunarMissionKind kind, LaunchVehicle vehicle, LunarSpacecraft craft, SpaceState parking,
            double stagePropellantKg, double launchJd, bool watch)
        {
            var mission = new LunarMission { Name = name, Kind = kind, Vehicle = vehicle, Craft = craft.Clone(), LaunchJd = launchJd };
            mission.Begin(parking, stagePropellantKg);
            Add(mission);
            if (watch) Watch(mission);
            return mission;
        }

        /// <summary>Adds a mission already in flight (a restored save).</summary>
        public void Add(LunarMission mission)
        {
            Missions.Add(mission);
            _eventCursor[mission] = 0;
            var view = new GameObject(mission.Name).AddComponent<CislunarView>();
            view.Init(mission, _sim, _materials, _fx, _lineShader, _rig.Far);
            _views[mission] = view;
            if (mission.Finished) _reported.Add(mission);
        }

        public CislunarView View(LunarMission mission) => _views.TryGetValue(mission, out var view) ? view : null;

        public IEnumerable<LunarMission> Active()
        {
            foreach (var m in Missions)
                if (!m.Finished) yield return m;
        }

        public void Watch(LunarMission mission)
        {
            Watched = mission;
            Director = true;
            _directorStage = -1;
        }

        public void StopWatching() => Watched = null;

        /// <summary>Removes a finished mission's craft from the scene.</summary>
        public void Dismiss(LunarMission mission)
        {
            if (Watched == mission) StopWatching();
            if (_views.TryGetValue(mission, out var view)) Destroy(view.gameObject);
            _views.Remove(mission);
            _eventCursor.Remove(mission);
            Missions.Remove(mission);
        }

        public void Clear()
        {
            foreach (var m in Missions.ToArray()) Dismiss(m);
            _reported.Clear();
        }

        public void WriteSave(SaveData data)
        {
            foreach (var m in Missions) data.Lunar.Add(m.ToSave());
        }

        public void RestoreSave(SaveData data)
        {
            Clear();
            foreach (var save in data.Lunar)
            {
                var mission = LunarMission.FromSave(save);
                Add(mission);
                _eventCursor[mission] = mission.Events.Count; // already in the log
            }
        }

        // ───────────────────────────── Camera ─────────────────────────────

        public void ViewCraft(LunarMission mission)
        {
            var view = View(mission);
            if (view == null) return;
            double distance = mission.Craft.IsShip ? 160 : mission.Kind == LunarMissionKind.Landing ? 22 : 14;
            if (mission.StageAttached) distance *= 2.2;
            float pitch = mission.Phase == LunarPhase.Landed || mission.Phase == LunarPhase.PoweredDescent ? 14f : 8f;
            _rig.ViewVehicle(() => view != null ? view.CenterKm : (double3?)null, $"{mission.Name} — {mission.Craft.Name}", distance, pitch);
        }

        /// <summary>
        /// The whole Earth–Moon system from the celestial south, looking along the axis of the Moon's orbit, so the
        /// transfer is seen from above its plane with the Earth and the Moon both in frame.
        /// </summary>
        public void ViewTrajectory(LunarMission mission)
        {
            if (View(mission) == null) return;
            _rig.ViewVehicle(() => 0.5 * Astro.MoonPosition(_sim.Clock.JulianDate), $"{mission.Name} — trajectory", 6.5e8, 0f, 0f);
        }

        /// <summary>The craft with the Moon behind it.</summary>
        public void ViewOverMoon(LunarMission mission)
        {
            var view = View(mission);
            if (view == null) return;
            double distance = math.clamp(mission.MoonAltitudeKm * 1000.0 * 2.5, 3.0e5, 1.2e7);
            _rig.ViewVehicle(() => view != null ? view.CenterKm : (double3?)null, $"{mission.Name} — over the Moon", distance);
        }

        // ───────────────────────────── Frame ─────────────────────────────

        void Update()
        {
            if (_sim == null) return;
            double jd = _sim.Clock.JulianDate;
            foreach (var mission in Missions)
            {
                // Finished missions keep going too: a lander rides along with the Moon, an orbiter keeps orbiting.
                mission.AdvanceTo(jd);
                LogEvents(mission);
                if (mission.Finished && _reported.Add(mission)) MissionFinished?.Invoke(mission);
                if (!mission.Finished && mission.MoonAltitudeKm > 50000 && mission.EarthAltitudeKm > 2000) _dots.Markers.Add(mission.State.R);
            }
            // From far away the Moon is a few pixels across; mark it while one of our missions is out there.
            if ((Watched != null || _rig.Mode == ViewMode.Free) && math.length(_rig.CameraEciKm - Astro.MoonPosition(jd)) > 150000) _dots.Markers.Add(Astro.MoonPosition(jd));
            UpdateDirector(jd);
            // Out by the Moon the ~20,000 satellites are a smudge around the Earth at best: skip them.
            bool farOut = math.length(_rig.CameraEciKm) > 150000;
            Cinematic.SatellitesHidden = HideSatellites && (Watched != null || farOut);
        }

        void LogEvents(LunarMission mission)
        {
            int cursor = _eventCursor.TryGetValue(mission, out int c) ? c : 0;
            for (; cursor < mission.Events.Count; cursor++) _sim.Log($"{mission.Name}: {mission.Events[cursor].Text}");
            _eventCursor[mission] = cursor;
        }

        void UpdateDirector(double jd)
        {
            var mission = Watched;
            if (mission == null) return;
            if (!Missions.Contains(mission)) { Watched = null; return; }
            var clock = _sim.Clock;

            if (Director && !mission.Finished)
            {
                double limit = double.PositiveInfinity;
                if (mission.Phase == LunarPhase.PoweredDescent) limit = DescentMaxWarp;
                else if (mission.Burning) limit = BurnMaxWarp;
                if (!double.IsNaN(mission.NextEventJd))
                {
                    double until = (mission.NextEventJd - jd) * MathConst.SecondsPerDay;
                    if (until > 0) limit = math.min(limit, math.max(1.0, until / EventLeadSeconds));
                }
                clock.MaxWarp = math.min(clock.MaxWarp, limit);
            }

            if (!Director) return;
            int stage;
            switch (mission.Phase)
            {
                case LunarPhase.ParkingOrbit:
                case LunarPhase.TransLunarInjection:
                case LunarPhase.CorrectionBurn:
                case LunarPhase.OrbitInsertion:
                case LunarPhase.Deorbit:
                    stage = 0; break;
                case LunarPhase.LunarOrbit:
                case LunarPhase.DescentCoast:
                    stage = 2; break;
                case LunarPhase.PoweredDescent:
                case LunarPhase.Landed:
                    stage = 3; break;
                default:
                    // Coasting: the trajectory from afar, but close up for the last hours before the Moon.
                    stage = mission.MoonAltitudeKm < 20000 && mission.Phase == LunarPhase.Coast ? 4 : 1; break;
            }
            if (mission.Phase == LunarPhase.Splashdown || mission.Phase == LunarPhase.Lost) stage = 5;
            if (stage == _directorStage) return;
            _directorStage = stage;
            switch (stage)
            {
                case 0: ViewCraft(mission); break;
                case 1: ViewTrajectory(mission); break;
                case 2: ViewOverMoon(mission); break;
                case 3: ViewCraft(mission); break;
                case 4: ViewOverMoon(mission); break;
                case 5: _rig.ViewEarth(); StopWatching(); break;
            }
        }
    }
}
