using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace Spaced
{
    /// <summary>A group of satellites going up together: one or more Falcon 9 missions from one pad.</summary>
    public sealed class LaunchCampaign
    {
        public string Name;
        public SatelliteClass Class;
        public bool Maneuverable;
        public LaunchSite Site;
        public List<OrbitSlot> Slots;
        public double PlanEpochJd;
        /// <summary>Launch whenever the pad is free, into whatever plane that gives (single satellites).</summary>
        public bool AnyPlane;
        public int SatellitesPerLaunch;
        public bool WatchAll;
        /// <summary>Fleet group the satellites join once deployed.</summary>
        public int Group = -1;
        public readonly List<LaunchMission> Missions = new List<LaunchMission>();
        public int Deployed;

        public bool Complete => Missions.TrueForAll(m => m.Done);
        public int Remaining => Missions.FindAll(m => !m.Done).Count;
    }

    public sealed class LaunchMission
    {
        public LaunchCampaign Campaign;
        public int Number;
        public string Name;
        public List<int> SlotIndices = new List<int>();
        public double PayloadKg;
        public double AltitudeKm;
        public double LiftoffJd = double.NaN;
        public RocketFlight Flight;
        public RocketView View;
        public DroneshipView Droneship;
        public int EventCursor;
        public bool PayloadDeployed;
        public uint FirstSatelliteId;
        public bool Done;
        public double FinishedJd = double.NaN;

        public bool Scheduled => !double.IsNaN(LiftoffJd);
        public OrbitElements Plane => Campaign.Slots[SlotIndices[0]].Elements;
    }

    /// <summary>Publishes the cinematic lights after all rocket views have updated (late execution order).</summary>
    [DefaultExecutionOrder(50)]
    public sealed class CinematicLightPublisher : MonoBehaviour
    {
        public LaunchManager Manager;
        void LateUpdate() => Manager?.PublishLights();
    }

    public struct LaunchPlan
    {
        public double CapacityKg;
        public int PerLaunch;
        public int Launches;
        public bool TooHeavy => PerLaunch == 0;
    }

    /// <summary>
    /// Schedules and flies Falcon 9 missions: picks launch windows when the pad passes under each target plane,
    /// runs the flight physics, parks the droneship, deploys the satellites as trains that raise into their
    /// slots, and directs the camera through the interesting moments while time is slowed down.
    /// </summary>
    [DefaultExecutionOrder(-40)]
    public sealed class LaunchManager : MonoBehaviour
    {
        public const double PadTurnaroundDays = 2.5;
        public const double FirstLaunchLeadMinutes = 3;
        public const double PoweredFlightMaxWarp = 10;
        public const double CoastMaxWarp = 600;
        /// <summary>
        /// At or above this warp (5 sim-minutes per second) a launch nobody is watching is resolved in one step
        /// instead of being flown in 0.1 s physics steps; otherwise flights fall behind the clock and pile up.
        /// </summary>
        public const double FastResolveWarp = 300;

        /// <summary>Slow time down and frame each watched launch automatically.</summary>
        public bool Director = true;
        /// <summary>While watching a launch: hide satellites and panels, light the rocket like a broadcast.</summary>
        public bool CinematicMode = true;

        public readonly List<LaunchCampaign> Campaigns = new List<LaunchCampaign>();
        public LaunchMission Watched { get; private set; }

        SpaceSimulation _sim;
        CameraRig _rig;
        SatelliteRenderer _dots;
        MaterialLibrary _materials;
        Fx _fx;
        Shader _groundShader;
        readonly Dictionary<LaunchSite, LaunchSiteView> _sites = new Dictionary<LaunchSite, LaunchSiteView>();
        readonly Dictionary<LaunchSite, double> _padFreeJd = new Dictionary<LaunchSite, double>();
        Random _rng = new Random(0xF9F9F9u);
        int _directorStage = -1;
        int _campaignCounter;

        public void Init(SpaceSimulation sim, CameraRig rig, SatelliteRenderer dots, MaterialLibrary materials, Fx fx, Shader groundShader)
        {
            _sim = sim;
            _rig = rig;
            _dots = dots;
            _materials = materials;
            _fx = fx;
            _groundShader = groundShader;
            gameObject.AddComponent<CinematicLightPublisher>().Manager = this;
            foreach (var site in LaunchSite.All)
            {
                var view = new GameObject(site.ShortName).AddComponent<LaunchSiteView>();
                view.Init(site, rig, materials, groundShader, fx);
                _sites[site] = view;
            }
        }

        public LaunchSiteView SiteView(LaunchSite site) => _sites[site];

        // ───────────────────────────── Planning ─────────────────────────────

        public static LaunchPlan Plan(SatelliteClass satClass, LaunchSite site, double altitudeKm, double inclinationDeg, int count)
        {
            double capacity = LaunchPlanner.MaxPayloadKg(site, altitudeKm, inclinationDeg);
            int perLaunch = (int)math.floor(capacity / satClass.MassKg);
            return new LaunchPlan
            {
                CapacityKg = capacity,
                PerLaunch = perLaunch,
                Launches = perLaunch > 0 ? (int)math.ceil(count / (double)perLaunch) : 0,
            };
        }

        /// <summary>Creates the missions for a set of slots (grouped by plane) and schedules the first one.</summary>
        public LaunchCampaign StartCampaign(string name, SatelliteClass satClass, bool maneuverable, LaunchSite site,
            List<OrbitSlot> slots, bool anyPlane, bool watchAll, int group = -1)
        {
            var first = slots[0].Elements;
            var plan = Plan(satClass, site, first.A - EarthConstants.RadiusKm, first.I * MathConst.Rad2Deg, slots.Count);
            if (plan.TooHeavy) return null;

            _campaignCounter++;
            var campaign = new LaunchCampaign
            {
                Name = name,
                Class = satClass,
                Maneuverable = maneuverable,
                Site = site,
                Slots = slots,
                PlanEpochJd = _sim.Clock.JulianDate,
                AnyPlane = anyPlane,
                SatellitesPerLaunch = plan.PerLaunch,
                WatchAll = watchAll,
                Group = group,
            };

            // Each mission flies to one plane; big planes take several missions.
            var byPlane = new SortedDictionary<int, List<int>>();
            for (int i = 0; i < slots.Count; i++)
            {
                if (!byPlane.TryGetValue(slots[i].Plane, out var list)) byPlane[slots[i].Plane] = list = new List<int>();
                list.Add(i);
            }
            foreach (var plane in byPlane.Values)
            {
                for (int start = 0; start < plane.Count; start += plan.PerLaunch)
                {
                    var mission = new LaunchMission { Campaign = campaign, Number = campaign.Missions.Count + 1 };
                    mission.SlotIndices.AddRange(plane.GetRange(start, math.min(plan.PerLaunch, plane.Count - start)));
                    mission.PayloadKg = mission.SlotIndices.Count * satClass.MassKg;
                    mission.AltitudeKm = slots[mission.SlotIndices[0]].Elements.A - EarthConstants.RadiusKm;
                    mission.Name = $"{name} {mission.Number}";
                    campaign.Missions.Add(mission);
                }
            }

            Campaigns.Add(campaign);
            ScheduleNext(campaign);
            var firstMission = campaign.Missions[0];
            _sim.Log($"{name}: {slots.Count:N0} × {satClass.Name} on {campaign.Missions.Count} Falcon 9 launch(es) from {site.ShortName}; " +
                     $"first liftoff {Astro.ToDateTime(firstMission.LiftoffJd):yyyy-MM-dd HH:mm} UTC");
            return campaign;
        }

        void ScheduleNext(LaunchCampaign campaign)
        {
            var next = campaign.Missions.Find(m => !m.Scheduled);
            if (next == null) return;
            double now = _sim.Clock.JulianDate;
            _padFreeJd.TryGetValue(campaign.Site, out double padFree);
            double earliest = math.max(now + FirstLaunchLeadMinutes / 1440.0, padFree);
            next.LiftoffJd = campaign.AnyPlane
                ? earliest
                : LaunchPlanner.NextLaunchWindow(campaign.Site, next.Plane, campaign.PlanEpochJd, earliest);
            _padFreeJd[campaign.Site] = next.LiftoffJd + PadTurnaroundDays;
        }

        /// <summary>Next mission that has not lifted off yet, across all campaigns.</summary>
        public LaunchMission NextLaunch()
        {
            LaunchMission best = null;
            foreach (var c in Campaigns)
                foreach (var m in c.Missions)
                    if (!m.Done && m.Flight == null && m.Scheduled && (best == null || m.LiftoffJd < best.LiftoffJd)) best = m;
            return best;
        }

        public IEnumerable<LaunchMission> ActiveMissions()
        {
            foreach (var c in Campaigns)
                foreach (var m in c.Missions)
                    if (m.Flight != null && !m.Done) yield return m;
        }

        // ───────────────────────────── Cleanup ─────────────────────────────

        /// <summary>Cancels every launch that hasn't released its payload yet, including rockets in flight.</summary>
        public int CancelPendingLaunches()
        {
            int cancelled = 0;
            foreach (var c in Campaigns)
            {
                foreach (var m in c.Missions)
                {
                    if (m.Done || m.PayloadDeployed) continue;
                    Retire(m);
                    cancelled++;
                }
            }
            _padFreeJd.Clear();
            if (cancelled > 0) _sim.Log($"Cancelled {cancelled:N0} launch(es)");
            return cancelled;
        }

        public int DeorbitMine()
        {
            CancelPendingLaunches();
            int count = LaunchService.DeorbitUserSatellites(_sim.Catalog, ref _rng);
            if (count > 0) _sim.Log($"Deorbit burns commanded for {count:N0} of your objects — they reenter within ~2 days");
            return count;
        }

        public int RemoveMine()
        {
            CancelPendingLaunches();
            int count = LaunchService.Remove(_sim.Catalog, p => (p.Flags & ObjectFlags.UserOwned) != 0);
            if (count > 0) _sim.Log($"Removed {count:N0} of your objects");
            return count;
        }

        public int ClearGeneratedDebris()
        {
            int count = LaunchService.Remove(_sim.Catalog, p => p.Category == ObjectCategory.GeneratedDebris);
            if (count > 0) _sim.Log($"Active debris removal: cleared {count:N0} fragments");
            return count;
        }

        // ───────────────────────────── Camera ─────────────────────────────

        public void Watch(LaunchMission mission)
        {
            Watched = mission;
            _directorStage = -1;
            Director = true;
        }

        public void StopWatching()
        {
            Watched = null;
            _sim.Clock.MaxWarp = double.PositiveInfinity;
        }

        public void ViewPad(LaunchMission mission)
        {
            var site = _sites[mission.Campaign.Site];
            _rig.ViewFromGround(site.OffsetEcefKm(-2400, -1700, 4), () => mission.View != null ? mission.View.StackCenterKm : (double3?)null, $"{mission.Name} — pad camera");
        }

        public void ViewChase(LaunchMission mission) =>
            _rig.ViewVehicle(() => mission.View != null ? mission.View.StackCenterKm : (double3?)null, $"{mission.Name} — chase", 170);

        public void ViewBooster(LaunchMission mission) =>
            _rig.ViewVehicle(() => mission.View != null && mission.View.BoosterVisible ? mission.View.BoosterCenterKm : (double3?)null, $"{mission.Name} — booster", 110);

        public void ViewSecondStageOnboard(LaunchMission mission) =>
            _rig.ViewOnboard(() => mission.View != null ? mission.View.SecondStageOnboard : null, $"{mission.Name} — second stage camera");

        public void ViewBoosterOnboard(LaunchMission mission) =>
            _rig.ViewOnboard(() => mission.View != null ? mission.View.BoosterOnboard : null, $"{mission.Name} — booster camera");

        public void ViewDroneship(LaunchMission mission)
        {
            if (mission.Droneship == null) return;
            _rig.ViewFromGround(mission.Droneship.OffsetEcefKm(-380, -260, 12), () => mission.View != null && mission.View.BoosterVisible ? mission.View.BoosterCenterKm : (double3?)null,
                $"{mission.Name} — {mission.Droneship.ShipName}");
        }

        // ───────────────────────────── Frame ─────────────────────────────

        void Update()
        {
            if (_sim == null || _sim.Catalog == null) return;
            double jd = _sim.Clock.JulianDate;
            double dt = _sim.LastDeltaSeconds;

            bool fast = !_sim.Clock.Paused && math.abs(_sim.Clock.Warp) >= FastResolveWarp;
            foreach (var campaign in Campaigns)
            {
                foreach (var mission in campaign.Missions)
                {
                    if (mission.Done) continue;
                    if (mission.Flight == null)
                    {
                        if (mission.Scheduled && jd >= mission.LiftoffJd - RocketFlight.CountdownSeconds / MathConst.SecondsPerDay)
                        {
                            if (fast) ResolveInstantly(mission, jd);
                            else BeginFlight(mission, jd);
                        }
                        continue;
                    }
                    if (fast && Watched != mission)
                    {
                        // Time sped up while this flight was in progress and nobody is looking: finish it now.
                        if (!mission.PayloadDeployed) ResolveInstantly(mission, jd);
                        else Retire(mission);
                        continue;
                    }
                    Fly(mission, jd, dt);
                }
            }

            UpdateDirector(jd);
            UpdateShake();
            Cinematic.Active = CinematicMode && Watched != null && Watched.Flight != null;
            foreach (var mission in ActiveMissions())
            {
                if (mission.View == null) continue;
                mission.View.Rig = _rig;
                mission.View.CinematicLighting = Cinematic.Active && mission == Watched;
            }
        }

        /// <summary>Rumble on the ground cameras while nine Merlins are firing nearby.</summary>
        void UpdateShake()
        {
            float shake = 0f;
            var flight = Watched?.Flight;
            if (flight != null && _rig.Mode == ViewMode.GroundTracking && flight.BoosterAttached && flight.EnginesLit > 0)
            {
                // A small fraction of the frame, whatever the zoom; fades within the first ~15 s and with distance.
                double distanceKm = math.distance(_rig.CameraEciKm, flight.RenderPosition / 1000.0);
                float fade = Mathf.Clamp01(1f - (float)flight.MissionTime / 15f);
                shake = (float)(flight.Throttle * 0.006 * _rig.Far.fieldOfView * fade / (1.0 + distanceKm * distanceKm / 4.0));
            }
            else if (flight != null && _rig.Mode == ViewMode.Onboard && flight.EnginesLit > 0)
            {
                shake = 0.03f;
            }
            _rig.Shake = shake;
        }

        /// <summary>Called by <see cref="CinematicLightPublisher"/> once every rocket has moved this frame.</summary>
        public void PublishLights()
        {
            Cinematic.ClearLights();
            if (Cinematic.Active && Watched?.View != null) Watched.View.PublishLights();
        }

        /// <summary>Orbit plane the mission flies into: the target plane at liftoff, or whatever "launch now" gives.</summary>
        static double3 TargetPlaneNormal(LaunchMission mission)
        {
            var campaign = mission.Campaign;
            if (campaign.AnyPlane)
                return LaunchPlanner.PlaneForImmediateLaunch(campaign.Site, mission.Plane.I * MathConst.Rad2Deg, mission.LiftoffJd, out _);
            var plane = mission.Plane;
            OrbitMath.Advance(ref plane, 0, (mission.LiftoffJd - campaign.PlanEpochJd) * MathConst.SecondsPerDay);
            return LaunchPlanner.PlaneNormal(plane.I, plane.Raan);
        }

        void BeginFlight(LaunchMission mission, double jd)
        {
            var campaign = mission.Campaign;
            double3 normal = TargetPlaneNormal(mission);

            mission.Flight = new RocketFlight(campaign.Site, mission.LiftoffJd, mission.PayloadKg, mission.AltitudeKm, normal);
            // If time warp jumped past T-60 s, catch the flight up.
            double late = (jd - (mission.LiftoffJd - RocketFlight.CountdownSeconds / MathConst.SecondsPerDay)) * MathConst.SecondsPerDay;
            mission.Flight.Advance(late);

            mission.View = new GameObject(mission.Name).AddComponent<RocketView>();
            mission.View.Init(mission.Flight, _sim, _sites[campaign.Site], _materials, _fx);
            ScheduleNext(campaign);

            bool watch = Director && (mission.Number == 1 || campaign.WatchAll) && Watched == null;
            if (watch) Watch(mission);
            _sim.Log($"{mission.Name}: T-60 s, {mission.SlotIndices.Count} × {campaign.Class.Name} on the pad at {campaign.Site.ShortName}");
        }

        void Fly(LaunchMission mission, double jd, double dt)
        {
            var flight = mission.Flight;
            if (dt > 0) flight.Advance(dt);

            for (; mission.EventCursor < flight.Events.Count; mission.EventCursor++)
            {
                string text = flight.Events[mission.EventCursor].Text;
                if (text == "Liftoff" || text.StartsWith("Main engine") || text.StartsWith("Touchdown") || text.StartsWith("Booster lost")
                    || text.StartsWith("Payload") || text.StartsWith("Vehicle did not"))
                    _sim.Log($"{mission.Name}: {text}");
            }

            if (flight.HasDroneship && mission.Droneship == null)
            {
                mission.Droneship = new GameObject(mission.Campaign.Site.DroneshipName).AddComponent<DroneshipView>();
                mission.Droneship.Init(mission.Campaign.Site.DroneshipName, flight.DroneshipEcefKm, _rig, _materials, _groundShader, _fx);
                mission.View.AttachDroneship(mission.Droneship);
            }

            if (flight.PayloadReleased && !mission.PayloadDeployed)
            {
                double catchUp = (jd - flight.JulianDate) * MathConst.SecondsPerDay;
                DeployPayload(mission, jd, flight.Position / 1000.0, flight.Velocity / 1000.0, catchUp, flight.PayloadRaisesOrbit);
                if (Watched == mission && Director && mission.FirstSatelliteId != 0) _pendingSatelliteView = mission.FirstSatelliteId;
            }

            if (mission.View != null)
            {
                _dots.Markers.Add(mission.View.StackCenterKm);
                if (!flight.BoosterAttached && flight.Booster != null && !flight.Booster.Finished) _dots.Markers.Add(mission.View.BoosterCenterKm);
            }

            if (flight.Finished)
            {
                if (double.IsNaN(mission.FinishedJd)) mission.FinishedJd = jd;
                if ((jd - mission.FinishedJd) * MathConst.SecondsPerDay > 300 && Watched != mission) Retire(mission);
            }
        }

        /// <summary>
        /// Skips the flight simulation: the rocket is assumed to fly its usual profile (it does whenever the payload is
        /// within capacity), so the satellites appear in the orbit it would have delivered and start raising.
        /// </summary>
        void ResolveInstantly(LaunchMission mission, double jd)
        {
            bool wasFlying = mission.Flight != null;
            Retire(mission);
            if (!wasFlying) mission.LiftoffJd = math.min(mission.LiftoffJd, jd);

            var campaign = mission.Campaign;
            double3 normal = TargetPlaneNormal(mission);
            double inclination = math.acos(math.clamp(normal.z, -1, 1)) * MathConst.Rad2Deg;
            double raan = math.atan2(normal.x, -normal.y) * MathConst.Rad2Deg;
            bool raises = mission.AltitudeKm > RocketFlight.PayloadSelfRaiseAboveKm;
            double perigee = raises ? RocketFlight.DirectInsertionMaxKm : mission.AltitudeKm;

            var first = campaign.Slots[mission.SlotIndices[0]].Elements;
            if (!campaign.AnyPlane) OrbitMath.Advance(ref first, 0, (jd - campaign.PlanEpochJd) * MathConst.SecondsPerDay);
            double argLat = campaign.AnyPlane ? 0 : (first.ArgP + first.M) * MathConst.Rad2Deg;

            var stage = OrbitMath.FromAltitudes(perigee, mission.AltitudeKm, inclination, raan, 0, argLat);
            OrbitMath.ToStateVector(stage, out double3 positionKm, out double3 velocityKmS);
            DeployPayload(mission, jd, positionKm, velocityKmS, 0, raises);
            if (!wasFlying) ScheduleNext(campaign);
            _sim.Log($"{mission.Name}: launched from {campaign.Site.ShortName} and deployed {mission.SlotIndices.Count} satellites (fast-forwarded)");
        }

        void DeployPayload(LaunchMission mission, double jd, double3 positionKm, double3 velocityKmS, double catchUp, bool raisesOrbit)
        {
            mission.PayloadDeployed = true;
            var campaign = mission.Campaign;

            var targets = new List<OrbitElements>(mission.SlotIndices.Count);
            OrbitMath.FromStateVector(positionKm, velocityKmS, out var stageOrbit);
            OrbitMath.Advance(ref stageOrbit, 0, catchUp);
            foreach (int slot in mission.SlotIndices)
            {
                var target = campaign.Slots[slot].Elements;
                if (campaign.AnyPlane)
                {
                    // No plane was targeted: keep the plane and position the rocket delivered, at the wanted shape.
                    target.Raan = stageOrbit.Raan;
                    target.I = stageOrbit.I;
                    target.M = OrbitMath.WrapAngle(stageOrbit.ArgP + stageOrbit.M - target.ArgP);
                }
                else
                {
                    OrbitMath.Advance(ref target, 0, (jd - campaign.PlanEpochJd) * MathConst.SecondsPerDay);
                }
                targets.Add(target);
            }

            double transferDays = raisesOrbit ? 10 : campaign.AnyPlane ? 1 : 5;
            var added = LaunchService.Deploy(_sim.Catalog, campaign.Class, positionKm, velocityKmS, targets, campaign.Maneuverable,
                campaign.Name, campaign.Deployed + 1, transferDays, catchUp, ref _rng, campaign.Group);
            campaign.Deployed += added.Count;
            _sim.RecordLaunch(added.Count, $"{campaign.Class.Name} ({mission.Name})");

            if (raisesOrbit && OrbitMath.FromStateVector(positionKm, velocityKmS, out var stage))
            {
                // The upper stage stays in the transfer orbit as a new rocket body.
                OrbitMath.Advance(ref stage, 0, catchUp);
                _sim.Catalog.Add(stage, new ObjectPhysics
                {
                    MassKg = (float)Falcon9.SecondDryKg,
                    RadiusM = 6f,
                    BallisticCoeff = 0.01f,
                    Category = ObjectCategory.RocketBody,
                    Model = ModelKind.RocketBody,
                    Flags = ObjectFlags.Alive | ObjectFlags.UserOwned,
                }, $"FALCON 9 R/B ({mission.Name})");
            }
            else if (mission.Flight != null)
            {
                _sim.Log($"{mission.Name}: second stage deorbit burn");
            }

            if (added.Count > 0) mission.FirstSatelliteId = _sim.Catalog.Ids[added[0]];
        }

        uint _pendingSatelliteView;

        void Retire(LaunchMission mission)
        {
            mission.Done = true;
            if (Watched == mission) StopWatching();
            if (mission.View != null) Destroy(mission.View.gameObject);
            if (mission.Droneship != null) Destroy(mission.Droneship.gameObject);
            mission.View = null;
            mission.Droneship = null;
        }

        /// <summary>
        /// Picks the shot for each phase of the watched flight and limits time warp while something is happening.
        /// The user can take over the camera at any time; the director only acts when the phase changes.
        /// </summary>
        void UpdateDirector(double jd)
        {
            var clock = _sim.Clock;
            clock.MaxWarp = double.PositiveInfinity;

            // Ease time down towards the next launch so it isn't skipped at high warp.
            var next = NextLaunch();
            if (Director && next != null && Watched == null && (next.Number == 1 || next.Campaign.WatchAll))
            {
                double untilCountdown = (next.LiftoffJd - jd) * MathConst.SecondsPerDay - RocketFlight.CountdownSeconds;
                clock.MaxWarp = math.max(1.0, untilCountdown / 0.6);
            }

            var mission = Watched;
            if (mission == null) return;
            if (mission.Done || mission.Flight == null && !mission.Scheduled) { StopWatching(); return; }
            var flight = mission.Flight;
            if (flight == null) return;

            var booster = flight.Booster;
            bool boosterBusy = booster != null && !booster.Finished;
            bool powered = flight.Phase == FlightPhase.Countdown || flight.Phase == FlightPhase.FirstStage
                           || flight.Phase == FlightPhase.StageSeparation || flight.Phase == FlightPhase.SecondStage
                           || flight.Phase == FlightPhase.Circularization;
            if (Director) clock.MaxWarp = powered || boosterBusy ? PoweredFlightMaxWarp : CoastMaxWarp;

            if (!Director) return;
            int stage;
            if (flight.Phase == FlightPhase.Countdown || flight.MissionTime < 14) stage = 0;
            else if (flight.BoosterAttached) stage = 1;
            else if (boosterBusy && booster.Phase == BoosterPhase.Coasting && booster.TimeSinceSeparation < 40) stage = 2;
            else if (boosterBusy && booster.AltitudeM > 3500) stage = 3;
            else if (boosterBusy || booster != null && booster.Phase == BoosterPhase.Landed
                     && (flight.JulianDate - booster.JulianDate) * MathConst.SecondsPerDay < 12) stage = 4;
            else if (!flight.PayloadReleased) stage = 5;
            else stage = 6;

            if (stage == _directorStage) return;
            _directorStage = stage;
            switch (stage)
            {
                case 0: ViewPad(mission); break;
                case 1: ViewChase(mission); break;
                case 2: ViewSecondStageOnboard(mission); break; // the booster falling away below

                case 3: ViewBooster(mission); break;
                case 4: if (mission.Droneship != null) ViewDroneship(mission); else ViewBoosterOnboard(mission); break;
                case 5: ViewChase(mission); break;
                case 6:
                    if (_pendingSatelliteView != 0)
                    {
                        _sim.SelectedId = _pendingSatelliteView;
                        _rig.ViewSatellite(_pendingSatelliteView);
                        _pendingSatelliteView = 0;
                    }
                    StopWatching();
                    break;
            }
        }
    }
}
