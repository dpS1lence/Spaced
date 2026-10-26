using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;

namespace Spaced
{
    /// <summary>Launch planning: when to lift off, and a preview of the orbit and ground track before you commit.</summary>
    public sealed partial class SpacedHud
    {
        public LaunchPreviewRenderer Preview;

        float _liftoffDelayHours;
        int _previewOrbits = 3;
        bool _showPreview = true;
        int _previewContract = -1;
        readonly List<OrbitElements> _previewList = new List<OrbitElements>();

        // Imaging forecast for the previewed orbit, worked out off the main thread.
        Task _forecastTask;
        float _forecastAt = -1e6f;
        volatile string _forecastText = "";
        // What the last layout pass saw: background results only show up from the next layout pass on, so IMGUI's
        // layout and repaint passes of a frame always draw the same controls.
        string _forecastShown = "";
        LaunchPath _tabPath, _contractPath;

        static bool LayoutPass => Event.current == null || Event.current.type == EventType.Layout;

        /// <summary>Liftoff delay for pad launches; returns the liftoff time it gives.</summary>
        double DrawLiftoffTime(LaunchSite site)
        {
            _liftoffDelayHours = Slider("Liftoff delay", _liftoffDelayHours, 0, 24, "0.0' h'");
            double liftoff = _launches.EarliestLiftoffJd(site, _liftoffDelayHours / 24.0);
            string when = $"{Astro.ToDateTime(liftoff):MMM d HH:mm} UTC, in {FormatDuration((liftoff - _sim.Clock.JulianDate) * MathConst.SecondsPerDay)}";
            GUILayout.Label(_launchMode == 0
                ? $"Liftoff {when}. From the same pad the track over the ground is the same whenever you go (the pad turns with the Earth), " +
                  "but a later liftoff changes the time of day under the orbit: which passes are in daylight."
                : $"Not before {when}; each plane then waits for the pad to pass under it.", _small);
            return liftoff;
        }

        /// <summary>Launch tab: shows the planned orbit(s) and ground track on the globe while you adjust the sliders.</summary>
        void DrawOrbitPreview()
        {
            if (Preview == null || !_sim.IsLoaded) return;
            _showPreview = GUILayout.Toggle(_showPreview, " Preview the orbit and ground track on the globe");
            if (!_showPreview) return;
            _previewOrbits = Mathf.RoundToInt(Slider("Track length", _previewOrbits, 1, LaunchPreviewRenderer.MaxTrackOrbits, "0' orbits'"));
            Preview.HideOthers = GUILayout.Toggle(Preview.HideOthers, " Hide other satellites while previewing");

            _previewList.Clear();
            double epoch = _sim.Clock.JulianDate;
            LaunchPath path = null;
            if (_launchMode == 0 && _deployMode == 0)
            {
                var site = LaunchSite.All[_siteIndex];
                double liftoff = _launches.EarliestLiftoffJd(site, _liftoffDelayHours / 24.0);
                if (LayoutPass)
                    _tabPath = LaunchPreview.FlyAsync(SelectedVehicle, site, SatelliteClass.Presets[_classIndex].MassKg, _perigee, Mathf.Max(_apogee, _perigee),
                        _inclination, liftoff);
                path = _tabPath;
                if (path == null)
                {
                    GUILayout.Label("Working out the flight…", _small);
                    return;
                }
                _previewList.Add(path.Final);
                epoch = path.ReleaseJd;
            }
            else if (_launchMode == 0)
                _previewList.Add(OrbitMath.FromAltitudes(_perigee, _apogee, _inclination, _raan, 0, 0));
            else
                AddWalkerPlanes(_previewList, _altitude, _inclination, _total, _planes, _phasing, _spread);

            Preview.Show(_previewList, epoch, _previewOrbits, path?.AscentEcef);
            GUILayout.Label(_launchMode == 1
                ? $"Green: the orbit planes{(_planes > LaunchPreviewRenderer.MaxPlanes ? $" ({LaunchPreviewRenderer.MaxPlanes} of {_planes} shown)" : "")} and the first satellite's ground track once in place."
                : path != null
                    ? $"Green: the rocket's climb and coast from the pad (payload released {FormatDuration((path.ReleaseJd - path.LiftoffJd) * MathConst.SecondsPerDay)} after liftoff), " +
                      "then the satellite's ground track over the turning Earth" + (path.RaisesOrbit ? " (approximate: it raises its own orbit first)." : ".")
                    : "Green: the orbit and its ground track over the turning Earth from now.", _small);
            if ((PayloadRole)_roleIndex == PayloadRole.Imaging) DrawPreviewForecast(_previewList[0], epoch);
        }

        /// <summary>One orbit per plane of a Walker constellation (the first satellite of each), like <see cref="LaunchService.PlanWalker"/>.</summary>
        static void AddWalkerPlanes(List<OrbitElements> list, double altitudeKm, double inclinationDeg, int total, int planes, int phasing, double spreadKm)
        {
            planes = math.clamp(planes, 1, math.max(total, 1));
            int shown = math.min(planes, LaunchPreviewRenderer.MaxPlanes);
            for (int k = 0; k < shown; k++)
            {
                int p = (int)((long)k * planes / shown);
                double altitude = altitudeKm + (planes > 1 ? (p / (planes - 1.0) - 0.5) * spreadKm : 0);
                double anomaly = 360.0 * phasing * p / math.max(total, 1);
                list.Add(OrbitMath.FromAltitudes(altitude, altitude, inclinationDeg, 360.0 * p / planes, 0, anomaly));
            }
        }

        /// <summary>When the previewed orbit would first bring your open imaging requests into camera reach.</summary>
        void DrawPreviewForecast(OrbitElements orbit, double epochJd)
        {
            int open = 0;
            foreach (var t in _fleet.Targets) if (!t.Captured) open++;
            if (open == 0) return;
            if ((_forecastTask == null || _forecastTask.IsCompleted) && Time.unscaledTime - _forecastAt > 1f)
            {
                _forecastAt = Time.unscaledTime;
                var names = new List<string>();
                var places = new List<double2>();
                foreach (var t in _fleet.Targets)
                {
                    if (t.Captured || names.Count >= 4) continue;
                    names.Add(t.Name);
                    places.Add(new double2(t.LatitudeDeg, t.LongitudeDeg));
                }
                var sats = new[] { orbit };
                double maxOffNadir = _fleet.MaxOffNadirDeg;
                _forecastTask = Task.Run(() =>
                {
                    var text = new StringBuilder();
                    for (int k = 0; k < names.Count; k++)
                    {
                        double pass = FleetManager.FindPass(sats, epochJd, places[k].x, places[k].y, maxOffNadir, 14, out _);
                        if (k > 0) text.Append('\n');
                        text.Append(double.IsNaN(pass)
                            ? $"• {names[k]}: never in reach for 2 weeks"
                            : $"• {names[k]}: first pass {FormatDuration((pass - epochJd) * MathConst.SecondsPerDay)} after reaching orbit");
                    }
                    _forecastText = text.ToString();
                });
            }
            if (LayoutPass) _forecastShown = _forecastText;
            if (_forecastShown.Length > 0)
                GUILayout.Label("From this orbit (daylight, within 30° of straight down, clouds permitting):\n" + _forecastShown, _accent);
        }

        /// <summary>Contract card: a button to see the orbit the booked launch will fly to.</summary>
        void DrawContractPreview(Contract c, LaunchSite pad)
        {
            if (Preview == null || !_sim.IsLoaded) return;
            bool on = _previewContract == c.Id;
            if (GUILayout.Toggle(on, " Preview the orbit on the globe") != on) on = !on;
            _previewContract = on ? c.Id : _previewContract == c.Id ? -1 : _previewContract;
            if (!on) return;

            _previewList.Clear();
            double epoch = _sim.Clock.JulianDate;
            LaunchPath path = null;
            if (c.Planes <= 1)
            {
                var vehicle = _contractVehicle.TryGetValue(c.Id, out int v) && v == 1 ? LaunchVehicle.Starship : LaunchVehicle.Falcon9;
                var plan = LaunchManager.Plan(vehicle, c.Class, pad, c.AltitudeKm, c.InclinationDeg, c.Count);
                double payload = math.max(1, math.min(plan.PerLaunch, c.Count)) * c.Class.MassKg;
                if (LayoutPass) _contractPath = LaunchPreview.FlyAsync(vehicle, pad, payload, c.AltitudeKm, c.AltitudeKm, c.InclinationDeg, _launches.EarliestLiftoffJd(pad));
                path = _contractPath;
                if (path == null) return;
                _previewList.Add(path.Final);
                epoch = path.ReleaseJd;
            }
            else AddWalkerPlanes(_previewList, c.AltitudeKm, c.InclinationDeg, c.Count, c.Planes, 1, 0);
            Preview.Show(_previewList, epoch, _previewOrbits, path?.AscentEcef);
        }
    }
}
