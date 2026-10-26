using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Mathematics;

namespace Spaced
{
    public enum LunarMissionKind
    {
        /// <summary>Swing around the Moon on a free-return trajectory and come home.</summary>
        Flyby,
        /// <summary>Brake into a low circular orbit around the Moon.</summary>
        Orbit,
        /// <summary>Brake into orbit, then descend and land.</summary>
        Landing,
    }

    public enum LunarPhase
    {
        ParkingOrbit,
        TransLunarInjection,
        Coast,
        CorrectionBurn,
        OrbitInsertion,
        LunarOrbit,
        Deorbit,
        DescentCoast,
        PoweredDescent,
        Landed,
        Returning,
        Splashdown,
        Lost,
    }

    /// <summary>A lunar mission in a save file: its public state plus the flight plan it is part-way through.</summary>
    [System.Serializable]
    public sealed class LunarMissionSave
    {
        public string Name;
        public LunarMissionKind Kind;
        public VehicleKind Vehicle;
        public LunarSpacecraft Craft;
        public LunarPhase Phase;
        public double3 R, V;
        public double Jd, LaunchJd;
        public bool StageAttached, Succeeded, FlybyAnnounced;
        public double StagePropellantKg, LandedLatDeg, LandedLonDeg, TouchdownSpeed, DeltaVUsedMs;
        public List<double> EventTimes = new List<double>();
        public List<string> EventTexts = new List<string>();
        public double NextEventJd;
        public string NextEventName;
        public double TliJd, TliDeltaV, BurnDeltaV, BurnDone;
        public double3 BurnDirection;
        public int Correction;
        public double CorrectionJd, ArrivalJd, ReturnJd, OrbitEntryJd, PeriodSeconds;
        public int Contract = -1;
    }

    /// <summary>The spacecraft that flies to the Moon (on Starship, the Ship itself).</summary>
    [System.Serializable]
    public sealed class LunarSpacecraft
    {
        public string Name;
        public double DryKg, PropellantKg;
        public double ThrustN, LandingThrustN;
        public double Isp;
        public double MinThrottle = 0.1;
        public bool IsShip;

        public double WetKg => DryKg + PropellantKg;
        public double DeltaVMs => Isp * LaunchVehicle.G0 * math.log(WetKg / DryKg);

        public LunarSpacecraft Clone() => (LunarSpacecraft)MemberwiseClone();

        public static LunarSpacecraft For(LunarMissionKind kind, VehicleKind vehicle)
        {
            if (vehicle == VehicleKind.Starship)
                return new LunarSpacecraft
                {
                    Name = "Starship HLS", DryKg = 130000 + 60000, PropellantKg = 1500000,
                    ThrustN = 3 * 2.75e6, LandingThrustN = 2.4e6, Isp = 372, MinThrottle = 0.4, IsShip = true,
                };
            switch (kind)
            {
                case LunarMissionKind.Flyby:
                    return new LunarSpacecraft { Name = "Lunar flyby probe", DryKg = 450, PropellantKg = 150, ThrustN = 450, LandingThrustN = 450, Isp = 300 };
                case LunarMissionKind.Orbit:
                    return new LunarSpacecraft { Name = "Lunar orbiter", DryKg = 1300, PropellantKg = 1100, ThrustN = 3000, LandingThrustN = 3000, Isp = 320 };
                default:
                    return new LunarSpacecraft { Name = "Lunar lander", DryKg = 1500, PropellantKg = 2600, ThrustN = 12000, LandingThrustN = 12000, Isp = 320 };
            }
        }
    }

    /// <summary>
    /// One flight to the Moon after the rocket has reached its parking orbit: trans-lunar injection (by the upper
    /// stage, or the Ship), mid-course corrections planned on a background thread, then a free-return flyby and
    /// re-entry, lunar orbit insertion, or insertion + deorbit + powered descent to a landing. Flown with the full
    /// Earth–Moon gravity (<see cref="Cislunar"/>); quiet stretches in a parking or lunar orbit ride on two-body
    /// Kepler orbits so any time warp is cheap.
    /// </summary>
    public sealed class LunarMission
    {
        public const double LunarOrbitAltitudeKm = 100;
        const double ReturnAltitudeKm = 60;
        const double FreeReturnAltitudeKm = 60;
        const double BurnStep = 0.5;

        public string Name;
        public LunarMissionKind Kind;
        public LaunchVehicle Vehicle;
        public LunarSpacecraft Craft;
        public LunarPhase Phase { get; private set; } = LunarPhase.ParkingOrbit;
        public SpaceState State;
        /// <summary>Upper stage still attached (Falcon 9: it performs trans-lunar injection).</summary>
        public bool StageAttached;
        public double StagePropellantKg;
        public double LandedLatDeg, LandedLonDeg;
        public double TouchdownSpeed;
        public bool Succeeded;
        public double DeltaVUsedMs;
        public readonly List<FlightEvent> Events = new List<FlightEvent>();
        /// <summary>Upcoming trajectory (inertial km), refreshed after every burn, for drawing.</summary>
        public readonly List<double3> PredictedPath = new List<double3>();
        /// <summary>Set while burning: thrust direction (inertial) and throttle.</summary>
        public double3 ThrustDirection;
        public double Throttle;
        public double NextEventJd = double.NaN;
        public string NextEventName = "";
        public double LaunchJd;
        /// <summary>Career contract this flight fulfils, or -1.</summary>
        public int Contract = -1;

        double _tliJd, _tliDeltaV;
        double _burnDeltaV, _burnDone;
        double3 _burnDirection;
        int _correction;
        double _correctionJd = double.NaN;
        double _arrivalJd;
        Task<(double3 dv, LunarEncounter encounter, bool ok)> _solve;
        SpaceState _solveFrom;
        double _orbitEntryJd;
        double _periodSeconds;
        bool _pathDirty = true;

        public double MassKg => Craft.DryKg + Craft.PropellantKg + (StageAttached ? Vehicle.SecondDryKg + StagePropellantKg : 0);
        public double MissionDays => State.Jd - LaunchJd;
        public bool Finished => Phase == LunarPhase.Landed || Phase == LunarPhase.Splashdown || Phase == LunarPhase.Lost
                                || Phase == LunarPhase.LunarOrbit && Kind == LunarMissionKind.Orbit && Succeeded;
        public bool Burning => Phase == LunarPhase.TransLunarInjection || Phase == LunarPhase.CorrectionBurn || Phase == LunarPhase.OrbitInsertion
                               || Phase == LunarPhase.Deorbit || Phase == LunarPhase.PoweredDescent;
        public double MoonAltitudeKm => math.length(State.R - Astro.MoonPosition(State.Jd)) - MoonConstants.RadiusKm;
        public double EarthAltitudeKm => math.length(State.R) - EarthConstants.RadiusKm;
        public double SpeedKmS => math.length(State.V);

        public LunarMissionSave ToSave()
        {
            var save = new LunarMissionSave
            {
                Name = Name, Kind = Kind, Vehicle = Vehicle.Kind, Craft = Craft.Clone(), Phase = Phase, R = State.R, V = State.V, Jd = State.Jd,
                LaunchJd = LaunchJd, StageAttached = StageAttached, Succeeded = Succeeded, FlybyAnnounced = _flybyAnnounced,
                StagePropellantKg = StagePropellantKg, LandedLatDeg = LandedLatDeg, LandedLonDeg = LandedLonDeg, TouchdownSpeed = TouchdownSpeed,
                DeltaVUsedMs = DeltaVUsedMs, NextEventJd = SaveGame.Pack(NextEventJd), NextEventName = NextEventName,
                TliJd = _tliJd, TliDeltaV = _tliDeltaV, BurnDeltaV = _burnDeltaV, BurnDone = _burnDone, BurnDirection = _burnDirection,
                Correction = _correction, CorrectionJd = SaveGame.Pack(_correctionJd), ArrivalJd = SaveGame.Pack(_arrivalJd),
                ReturnJd = SaveGame.Pack(_returnJd), OrbitEntryJd = _orbitEntryJd, PeriodSeconds = _periodSeconds, Contract = Contract,
            };
            foreach (var e in Events) { save.EventTimes.Add(e.MissionTime); save.EventTexts.Add(e.Text); }
            return save;
        }

        public static LunarMission FromSave(LunarMissionSave save)
        {
            var vehicle = LaunchManager.LunarRocket(save.Vehicle == VehicleKind.Starship ? LaunchVehicle.Starship : LaunchVehicle.Falcon9);
            var m = new LunarMission
            {
                Name = save.Name, Kind = save.Kind, Vehicle = vehicle, Craft = save.Craft.Clone(), LaunchJd = save.LaunchJd,
                State = new SpaceState { R = save.R, V = save.V, Jd = save.Jd },
                StageAttached = save.StageAttached, Succeeded = save.Succeeded, StagePropellantKg = save.StagePropellantKg,
                LandedLatDeg = save.LandedLatDeg, LandedLonDeg = save.LandedLonDeg, TouchdownSpeed = save.TouchdownSpeed,
                DeltaVUsedMs = save.DeltaVUsedMs, NextEventJd = SaveGame.Unpack(save.NextEventJd), NextEventName = save.NextEventName ?? "",
                Contract = save.Contract,
            };
            m.Phase = save.Phase;
            m._flybyAnnounced = save.FlybyAnnounced;
            m._tliJd = save.TliJd;
            m._tliDeltaV = save.TliDeltaV;
            m._burnDeltaV = save.BurnDeltaV;
            m._burnDone = save.BurnDone;
            m._burnDirection = save.BurnDirection;
            m._correction = save.Correction;
            m._arrivalJd = SaveGame.Unpack(save.ArrivalJd);
            m._returnJd = SaveGame.Unpack(save.ReturnJd);
            m._orbitEntryJd = save.OrbitEntryJd;
            m._periodSeconds = save.PeriodSeconds;
            for (int i = 0; i < save.EventTexts.Count && i < save.EventTimes.Count; i++)
                m.Events.Add(new FlightEvent { MissionTime = save.EventTimes[i], Text = save.EventTexts[i] });
            // A correction that was being planned is planned again from the saved state.
            double correctionJd = SaveGame.Unpack(save.CorrectionJd);
            if (!double.IsNaN(correctionJd))
            {
                m._correction--;
                m.ScheduleCorrection(math.max(correctionJd, m.State.Jd));
            }
            m._pathDirty = true;
            return m;
        }

        /// <summary>Starts the mission from the parking orbit the rocket delivered.</summary>
        public void Begin(SpaceState parking, double stagePropellantKg)
        {
            State = parking;
            StageAttached = !Craft.IsShip;
            StagePropellantKg = stagePropellantKg;
            if (Craft.IsShip) Raise("Propellant transfer complete: tanks refilled by tanker flights in orbit");
            _tliJd = LunarTargeting.PlanInjection(parking, out _tliDeltaV);
            NextEvent(_tliJd, "Trans-lunar injection");
            Raise($"Parking orbit; trans-lunar injection planned in {(_tliJd - parking.Jd) * 1440:0} min ({_tliDeltaV * 1000:0} m/s)");
        }

        void Raise(string text) => Events.Add(new FlightEvent { MissionTime = (State.Jd - LaunchJd) * MathConst.SecondsPerDay, Text = text });

        void NextEvent(double jd, string name)
        {
            NextEventJd = jd;
            NextEventName = name;
        }

        double Thrust => StageAttached ? Vehicle.SecondThrust : Phase == LunarPhase.PoweredDescent ? Craft.LandingThrustN : Craft.ThrustN;
        double Isp => StageAttached ? Vehicle.SecondIsp : Craft.Isp;

        // ───────────────────────────── Flight ─────────────────────────────

        /// <summary>
        /// Flies the mission up to the simulation clock. While a correction is still being planned (a second or so of
        /// real time) the spacecraft waits at the correction point and catches up afterwards.
        /// </summary>
        public void AdvanceTo(double jd)
        {
            int guard = 0;
            var startPhase = Phase;
            while (State.Jd < jd - 1e-9 && !FinishedForGood && guard++ < 100000)
            {
                double used = StepPhase((jd - State.Jd) * MathConst.SecondsPerDay);
                if (used <= 0) break;
                // A burn just started: stop here so time warp can be lowered before it is flown (catches up next frame).
                if (Phase != startPhase && Burning) break;
            }
            if (_pathDirty && !Burning) RefreshPath();
        }

        bool FinishedForGood => Phase == LunarPhase.Lost || Phase == LunarPhase.Splashdown;

        /// <summary>Advances the current phase by at most <paramref name="dt"/> seconds; returns the time used.</summary>
        double StepPhase(double dt)
        {
            switch (Phase)
            {
                case LunarPhase.ParkingOrbit:
                {
                    double untilBurn = (_tliJd - State.Jd) * MathConst.SecondsPerDay - BurnSeconds(_tliDeltaV) * 0.5;
                    double step = math.min(dt, math.max(untilBurn, 0));
                    Cislunar.CoastAroundEarth(ref State, step);
                    if (untilBurn - step <= 1e-6) StartBurn(LunarPhase.TransLunarInjection, _tliDeltaV, double3.zero, "Trans-lunar injection burn");
                    return math.max(step, 1e-6);
                }
                case LunarPhase.TransLunarInjection:
                case LunarPhase.CorrectionBurn:
                case LunarPhase.Deorbit:
                case LunarPhase.OrbitInsertion:
                    return Burn(math.min(dt, BurnStep));
                case LunarPhase.Coast:
                case LunarPhase.Returning:
                    return Coast(dt);
                case LunarPhase.LunarOrbit:
                case LunarPhase.DescentCoast:
                    return OrbitMoon(dt);
                case LunarPhase.PoweredDescent:
                    return Descend(math.min(dt, MoonAltitudeKm < 0.3 ? 0.05 : 0.2));
                case LunarPhase.Landed:
                    State.Jd += dt / MathConst.SecondsPerDay;
                    State.R = Cislunar.MoonSurfacePoint(LandedLatDeg, LandedLonDeg, 0, State.Jd);
                    Cislunar.Moon(State.Jd, out _, out double3 moonVelocity);
                    State.V = moonVelocity;
                    return dt;
                default:
                    State.Jd += dt / MathConst.SecondsPerDay;
                    return dt;
            }
        }

        double BurnSeconds(double deltaVKmS)
        {
            double mass = MassKg;
            double final = mass * math.exp(-deltaVKmS * 1000 / (Isp * LaunchVehicle.G0));
            return (mass - final) / (Thrust / (Isp * LaunchVehicle.G0));
        }

        void StartBurn(LunarPhase phase, double deltaVKmS, double3 direction, string text)
        {
            Phase = phase;
            _burnDeltaV = deltaVKmS;
            _burnDone = 0;
            _burnDirection = direction;
            Raise($"{text} ({deltaVKmS * 1000:0} m/s)");
            NextEvent(State.Jd + BurnSeconds(deltaVKmS) / MathConst.SecondsPerDay, text + " cutoff");
        }

        double3 MoonRelativeVelocity(out double3 relativePosition)
        {
            Cislunar.Moon(State.Jd, out double3 moon, out double3 moonVelocity);
            relativePosition = State.R - moon;
            return State.V - moonVelocity;
        }

        /// <summary>A finite burn: prograde (injection), fixed direction (corrections) or retrograde to the Moon (insertion, deorbit).</summary>
        double Burn(double h)
        {
            double3 direction;
            if (Phase == LunarPhase.TransLunarInjection) direction = math.normalize(State.V);
            else if (Phase == LunarPhase.CorrectionBurn) direction = _burnDirection;
            else direction = -math.normalize(MoonRelativeVelocity(out _));

            double mass = MassKg;
            double accelerationKmS2 = Thrust / mass / 1000.0;
            double remaining = _burnDeltaV - _burnDone;
            if (accelerationKmS2 * h > remaining) h = math.max(remaining / accelerationKmS2, 1e-3);

            double flow = Thrust / (Isp * LaunchVehicle.G0);
            double used = flow * h;
            if (StageAttached) StagePropellantKg = math.max(0, StagePropellantKg - used);
            else Craft.PropellantKg = math.max(0, Craft.PropellantKg - used);
            bool empty = StageAttached ? StagePropellantKg <= 0 : Craft.PropellantKg <= 0;

            ThrustDirection = direction;
            Throttle = 1;
            Cislunar.Step(ref State, h, Astro.MoonPosition(State.Jd), direction * accelerationKmS2);
            _burnDone += accelerationKmS2 * h;
            DeltaVUsedMs += accelerationKmS2 * h * 1000;

            bool done = _burnDone >= _burnDeltaV - 1e-7;
            if (Phase == LunarPhase.OrbitInsertion && CapturedLowEnough()) done = true;
            if (done || empty) EndBurn(empty && !done);
            return h;
        }

        bool CapturedLowEnough()
        {
            double3 v = MoonRelativeVelocity(out double3 r);
            double energy = 0.5 * math.lengthsq(v) - MoonConstants.Mu / math.length(r);
            if (energy >= 0) return false;
            double a = -MoonConstants.Mu / (2 * energy);
            double h = math.length(math.cross(r, v));
            double e = math.sqrt(math.max(0, 1 - h * h / (MoonConstants.Mu * a)));
            // Low enough on the far side, or braking any longer would drag the low point towards the ground.
            return a * (1 + e) <= MoonConstants.RadiusKm + LunarOrbitAltitudeKm * 1.15
                   || a * (1 - e) <= MoonConstants.RadiusKm + LunarOrbitAltitudeKm * 0.5;
        }

        void EndBurn(bool outOfPropellant)
        {
            Throttle = 0;
            _pathDirty = true;
            switch (Phase)
            {
                case LunarPhase.TransLunarInjection:
                    Raise(outOfPropellant ? "Injection cut short: upper stage out of propellant" : "Trans-lunar injection complete");
                    if (StageAttached)
                    {
                        StageAttached = false;
                        Raise("Spacecraft separation; the spent upper stage heads into solar orbit");
                    }
                    Phase = LunarPhase.Coast;
                    ScheduleCorrection(State.Jd + 6.0 / 24.0);
                    break;
                case LunarPhase.CorrectionBurn:
                    Raise($"Mid-course correction {_correction} complete");
                    Phase = LunarPhase.Coast;
                    if (_correction == 1 && !double.IsNaN(_arrivalJd) && _arrivalJd - State.Jd > 1.0) ScheduleCorrection(_arrivalJd - 14.0 / 24.0);
                    else NextArrivalEvent();
                    break;
                case LunarPhase.OrbitInsertion:
                    Phase = LunarPhase.LunarOrbit;
                    MoonRelativeVelocity(out double3 r);
                    _orbitEntryJd = State.Jd;
                    _periodSeconds = MathConst.TwoPi * math.sqrt(math.pow(math.length(r), 3) / MoonConstants.Mu);
                    if (outOfPropellant) { Raise("Out of propellant during orbit insertion"); }
                    Raise($"Lunar orbit insertion complete: {MoonAltitudeKm:0} km above the Moon");
                    if (Kind == LunarMissionKind.Orbit) { Succeeded = true; NextEvent(double.NaN, ""); }
                    else NextEvent(_orbitEntryJd + _periodSeconds / MathConst.SecondsPerDay, "Deorbit burn");
                    break;
                case LunarPhase.Deorbit:
                    Phase = LunarPhase.DescentCoast;
                    Raise("Deorbit burn complete: falling towards a 15 km perilune");
                    NextEvent(State.Jd + _periodSeconds * 0.5 / MathConst.SecondsPerDay, "Powered descent");
                    break;
            }
        }

        // ── Corrections, planned ahead on a background thread from the coasting state at the correction time.

        void ScheduleCorrection(double jd)
        {
            _correction++;
            _correctionJd = jd;
            var future = State;
            Cislunar.Propagate(ref future, (jd - State.Jd) * MathConst.SecondsPerDay);
            _solveFrom = future;
            var kind = Kind;
            _solve = Task.Run(() =>
            {
                bool ok;
                double3 dv;
                LunarEncounter encounter;
                if (kind == LunarMissionKind.Flyby) ok = LunarTargeting.SolveFreeReturn(future, FreeReturnAltitudeKm, out dv, out encounter);
                else ok = LunarTargeting.SolvePerilune(future, LunarOrbitAltitudeKm, null, out dv, out encounter);
                return (dv, encounter, ok);
            });
            NextEvent(jd, $"Mid-course correction {_correction}");
        }

        void NextArrivalEvent()
        {
            if (Kind == LunarMissionKind.Flyby) NextEvent(_arrivalJd, "Lunar flyby");
            else NextEvent(_arrivalJd, "Lunar orbit insertion");
        }

        double Coast(double dt)
        {
            // Stop exactly at a planned correction.
            double step = dt;
            if (!double.IsNaN(_correctionJd))
            {
                double until = (_correctionJd - State.Jd) * MathConst.SecondsPerDay;
                if (until <= 1e-6)
                {
                    if (_solve != null && !_solve.IsCompleted) return 0; // wait for the planner (rarely more than a second or two)
                    ApplyCorrection();
                    return 1e-6;
                }
                step = math.min(step, until);
            }

            double3 moon = Astro.MoonPosition(State.Jd);
            double h = math.min(Cislunar.StepFor(State.R, moon, 900), step);
            Cislunar.Step(ref State, h, moon);

            double moonDistance = math.length(State.R - Astro.MoonPosition(State.Jd));
            if (moonDistance < MoonConstants.RadiusKm) { Crash("Impact on the Moon"); return h; }
            if (Phase == LunarPhase.Coast && Kind != LunarMissionKind.Flyby && moonDistance < 12000 && TimeToPeriluneSeconds() <= BurnSeconds(InsertionDeltaV()) * 0.5)
            {
                StartBurn(LunarPhase.OrbitInsertion, InsertionDeltaV(), double3.zero, "Lunar orbit insertion burn");
                return h;
            }
            if (Phase == LunarPhase.Coast && Kind == LunarMissionKind.Flyby && moonDistance < MoonConstants.SoiKm && !_flybyAnnounced && TimeToPeriluneSeconds() <= 0)
            {
                _flybyAnnounced = true;
                Raise($"Closest approach: {moonDistance - MoonConstants.RadiusKm:N0} km above the far side of the Moon");
                Phase = LunarPhase.Returning;
                NextEvent(_returnJd, "Re-entry");
                _pathDirty = true;
            }
            if (Phase == LunarPhase.Returning && EarthAltitudeKm < 120)
            {
                Phase = LunarPhase.Splashdown;
                Succeeded = true;
                Raise($"Re-entry at {SpeedKmS:0.0} km/s — splashdown! Welcome home");
                NextEvent(double.NaN, "");
            }
            return h;
        }

        bool _flybyAnnounced;
        double _returnJd = double.NaN;

        void ApplyCorrection()
        {
            _correctionJd = double.NaN;
            var result = _solve.Result;
            _solve = null;
            // The plan was made for the coasting state at this moment; apply it to where we really are.
            double3 dv = result.dv;
            _arrivalJd = result.encounter.PeriluneJd;
            _returnJd = result.encounter.ReturnPerigeeJd;
            if (!result.ok) Raise("Trajectory planner: best effort (target not fully reached)");
            double magnitude = math.length(dv);
            if (magnitude < 0.0002)
            {
                Raise($"Mid-course correction {_correction} not needed");
                Phase = LunarPhase.Coast;
                if (_correction == 1 && _arrivalJd - State.Jd > 1.0) ScheduleCorrection(_arrivalJd - 14.0 / 24.0);
                else NextArrivalEvent();
                return;
            }
            StartBurn(LunarPhase.CorrectionBurn, magnitude, dv / magnitude, $"Mid-course correction {_correction}");
        }

        /// <summary>Speed to lose at perilune for a circular orbit there (km/s).</summary>
        double InsertionDeltaV()
        {
            double3 v = MoonRelativeVelocity(out double3 r);
            double rp = MoonConstants.RadiusKm + LunarOrbitAltitudeKm;
            double energy = 0.5 * math.lengthsq(v) - MoonConstants.Mu / math.length(r);
            double vPerilune = math.sqrt(2 * (energy + MoonConstants.Mu / rp));
            return math.max(vPerilune - math.sqrt(MoonConstants.Mu / rp), 0.05);
        }

        /// <summary>Two-body time to the next closest approach to the Moon (s); ≤ 0 once past it.</summary>
        double TimeToPeriluneSeconds()
        {
            double3 v = MoonRelativeVelocity(out double3 r);
            double radial = math.dot(r, v) / math.length(r);
            if (radial >= 0) return 0;
            // Hyperbolic/elliptic: from the eccentric (or hyperbolic) anomaly.
            double rm = math.length(r);
            double energy = 0.5 * math.lengthsq(v) - MoonConstants.Mu / rm;
            double a = -MoonConstants.Mu / (2 * energy);
            double3 eVec = ((math.lengthsq(v) - MoonConstants.Mu / rm) * r - math.dot(r, v) * v) / MoonConstants.Mu;
            double e = math.length(eVec);
            if (e > 1)
            {
                double coshF = (1 - rm / a) / e;
                double f = math.log(coshF + math.sqrt(math.max(coshF * coshF - 1, 0)));
                double meanMotion = math.sqrt(MoonConstants.Mu / -(a * a * a));
                return (e * math.sinh(f) - f) / meanMotion;
            }
            double cosE = math.clamp((1 - rm / a) / e, -1, 1);
            double ecc = math.acos(cosE);
            double n = math.sqrt(MoonConstants.Mu / (a * a * a));
            return (ecc - e * math.sin(ecc)) / n;
        }

        double OrbitMoon(double dt)
        {
            double step = dt;
            if (!double.IsNaN(NextEventJd)) step = math.min(step, math.max((NextEventJd - State.Jd) * MathConst.SecondsPerDay, 0));
            if (Phase == LunarPhase.DescentCoast) step = math.min(step, 5);
            Cislunar.CoastAroundMoon(ref State, step);
            if (MoonAltitudeKm < 0) { Crash("Impact on the Moon"); return math.max(step, 1e-6); }

            if (Phase == LunarPhase.LunarOrbit && Kind == LunarMissionKind.Landing && State.Jd >= NextEventJd - 1e-9)
            {
                double3 v = MoonRelativeVelocity(out double3 r);
                double r1 = math.length(r), rp = MoonConstants.RadiusKm + 15;
                double vApo = math.sqrt(MoonConstants.Mu * (2 / r1 - 2 / (r1 + rp)));
                StartBurn(LunarPhase.Deorbit, math.max(math.length(v) - vApo, 0.005), double3.zero, "Deorbit burn");
            }
            else if (Phase == LunarPhase.DescentCoast && MoonAltitudeKm < 40)
            {
                MoonRelativeVelocity(out double3 r);
                double3 v = MoonRelativeVelocity(out _);
                double vertical = math.dot(v, math.normalize(r));
                double horizontal = math.length(v - vertical * math.normalize(r));
                if (PredictStopAltitudeKm(MoonAltitudeKm * 1000, vertical * 1000, horizontal * 1000, 0.9) <= 400)
                {
                    Phase = LunarPhase.PoweredDescent;
                    Raise("Powered descent initiation");
                    NextEvent(double.NaN, "Touchdown");
                }
            }
            return math.max(step, 1e-6);
        }

        /// <summary>Altitude (m) at which a retrograde burn at this throttle, started now, stops the lander (no air on the Moon).</summary>
        double PredictStopAltitudeKm(double altitudeM, double verticalSpeed, double horizontalSpeed, double throttle)
        {
            double h = altitudeM, vr = verticalSpeed, vh = horizontalSpeed, mass = MassKg;
            double thrust = Craft.LandingThrustN * throttle;
            double flow = thrust / (Craft.Isp * LaunchVehicle.G0);
            double dt = altitudeM < 400 ? 0.05 : 0.5; // fine steps for the last metres
            for (int i = 0; i < 6000 && vr < 0; i++)
            {
                double speed = math.sqrt(vr * vr + vh * vh);
                double g = MoonConstants.Mu * 1e9 / math.pow(MoonConstants.RadiusKm * 1000 + h, 2);
                double decel = thrust / mass;
                vr += (-decel * vr / speed - g) * dt;
                vh += -decel * vh / speed * dt;
                h += vr * dt;
                mass = math.max(mass - flow * dt, Craft.DryKg);
            }
            return h;
        }

        /// <summary>Gravity-turn descent: retrograde thrust, throttled to come to rest just above the surface.</summary>
        double Descend(double h)
        {
            double3 v = MoonRelativeVelocity(out double3 r);
            double3 up = math.normalize(r);
            double altitudeM = MoonAltitudeKm * 1000;
            double vertical = math.dot(v, up) * 1000;
            double horizontal = math.length(v - math.dot(v, up) * up) * 1000;
            double speed = math.length(v) * 1000;

            double lo = Craft.MinThrottle, hi = 1.0;
            for (int k = 0; k < 14; k++)
            {
                double mid = 0.5 * (lo + hi);
                if (PredictStopAltitudeKm(altitudeM, vertical, horizontal, mid) > 1.3) hi = mid; else lo = mid;
            }
            Throttle = hi;
            // Hoverslam: if even the lowest throttle would stop well above the ground, coast for now (burning early
            // at minimum throttle just hovers and wastes propellant); and never climb back up near the surface.
            if (hi <= Craft.MinThrottle + 1e-6 && PredictStopAltitudeKm(altitudeM, vertical, horizontal, Craft.MinThrottle) > 60) Throttle = 0;
            if (vertical > -0.3 && altitudeM < 40) Throttle = 0;
            // Retrograde (a gravity turn) while there is sideways speed to kill; then straight up, upright.
            double3 direction = horizontal > 0.8 && speed > 0.5 ? -math.normalize(v) : up;
            ThrustDirection = direction;

            double thrust = Craft.LandingThrustN * Throttle;
            double flow = thrust / (Craft.Isp * LaunchVehicle.G0);
            Craft.PropellantKg = math.max(0, Craft.PropellantKg - flow * h);
            if (Craft.PropellantKg <= 0) Throttle = 0;
            double accelerationKmS2 = Throttle > 0 ? thrust / MassKg / 1000.0 : 0;
            DeltaVUsedMs += accelerationKmS2 * h * 1000;
            Cislunar.Step(ref State, h, Astro.MoonPosition(State.Jd), direction * accelerationKmS2);

            // Landing legs touch about a metre before the body would.
            if (MoonAltitudeKm * 1000 <= 1.0)
            {
                TouchdownSpeed = math.length(MoonRelativeVelocity(out _)) * 1000;
                Cislunar.MoonLatLon(State.R, State.Jd, out LandedLatDeg, out LandedLonDeg);
                if (TouchdownSpeed < 4)
                {
                    Phase = LunarPhase.Landed;
                    Succeeded = true;
                    Throttle = 0;
                    Raise($"Touchdown at {math.abs(LandedLatDeg):0.00}°{(LandedLatDeg >= 0 ? "N" : "S")} {math.abs(LandedLonDeg):0.00}°{(LandedLonDeg >= 0 ? "E" : "W")} " +
                          $"({TouchdownSpeed:0.0} m/s) — the {Craft.Name} has landed!");
                    NextEvent(double.NaN, "");
                }
                else Crash($"Crashed into the Moon at {TouchdownSpeed:0} m/s");
            }
            return h;
        }

        void Crash(string text)
        {
            Phase = LunarPhase.Lost;
            Throttle = 0;
            Raise(text);
            NextEvent(double.NaN, "");
        }

        // ───────────────────────────── Path ─────────────────────────────

        /// <summary>Samples the coming trajectory (no more burns) for drawing: a day or so ahead, or one lunar orbit.</summary>
        void RefreshPath()
        {
            _pathDirty = false;
            PredictedPath.Clear();
            var s = State;
            if (Phase == LunarPhase.LunarOrbit || Phase == LunarPhase.DescentCoast || Phase == LunarPhase.Landed) return; // drawn Moon-relative
            double horizonDays = Phase == LunarPhase.ParkingOrbit ? 0.07 : Phase == LunarPhase.Returning ? 5 : 6;
            double end = s.Jd + horizonDays;
            PredictedPath.Add(s.R);
            int guard = 0;
            while (s.Jd < end && guard++ < 600)
            {
                if (Phase == LunarPhase.ParkingOrbit) Cislunar.CoastAroundEarth(ref s, 60);
                else Cislunar.Propagate(ref s, 1200);
                PredictedPath.Add(s.R);
                if (math.length(s.R) < EarthConstants.RadiusKm + 100) break;
                if (math.length(s.R - Astro.MoonPosition(s.Jd)) < MoonConstants.RadiusKm) break;
            }
        }
    }
}
