using System;
using System.Collections.Generic;
using UnityEngine;

namespace Spaced
{
    /// <summary>
    /// One play session: chooses between a new career, a sandbox and continuing a save (the main menu drives this),
    /// and saves the game — every few minutes, when you press Save, and when you quit.
    /// </summary>
    [DefaultExecutionOrder(-45)]
    public sealed class GameSession : MonoBehaviour
    {
        public const float AutosaveMinutes = 3f;

        public GameMode Mode { get; private set; }
        /// <summary>False while the main menu is up.</summary>
        public bool Started { get; private set; }
        public string Status { get; private set; } = "";
        public float StatusTime { get; private set; } = -100f;

        SpaceSimulation _sim;
        LaunchManager _launches;
        LunarMissionManager _lunar;
        FleetManager _fleet;
        CareerManager _career;
        SpacedHud _hud;
        CameraRig _rig;
        float _autosaveTimer;

        public void Init(SpaceSimulation sim, CameraRig rig, LaunchManager launches, LunarMissionManager lunar, FleetManager fleet, CareerManager career, SpacedHud hud)
        {
            _sim = sim;
            _rig = rig;
            _launches = launches;
            _lunar = lunar;
            _fleet = fleet;
            _career = career;
            _hud = hud;
        }

        public static SaveData Peek(GameMode mode) => SaveGame.Exists(mode) ? SaveGame.Peek(SaveGame.PathFor(mode)) : null;

        /// <summary>Starts from today's real sky: a fresh catalog download and, in career mode, a new company.</summary>
        public void StartNew(GameMode mode, string company = null)
        {
            Mode = mode;
            Started = true;
            _sim.Clock.JumpTo(DateTime.UtcNow);
            StartCoroutine(StartWhenLoaded(mode, company));
        }

        System.Collections.IEnumerator StartWhenLoaded(GameMode mode, string company)
        {
            yield return _sim.DownloadCatalog();
            if (mode == GameMode.Career) _career.StartNew(company);
            else _career.Deactivate();
            _autosaveTimer = AutosaveMinutes * 60f;
            Save();
        }

        /// <summary>Loads the save for a mode. Returns false (and stays in the menu) if it can't be read.</summary>
        public bool Continue(GameMode mode)
        {
            var images = new List<byte[]>();
            SaveData data;
            try
            {
                data = SaveGame.Read(SaveGame.PathFor(mode), _sim.Catalog, images);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Spaced] Could not load the {mode} save: {e.Message}");
                SetStatus("That save could not be read.");
                return false;
            }
            if (data == null)
            {
                SetStatus("That save is from a newer version of the game.");
                return false;
            }
            Mode = mode;
            _sim.RestoreSave(data);
            _fleet.RestoreSave(data, images);
            _launches.RestoreSave(data);
            _lunar.RestoreSave(data);
            _career.RestoreSave(data);
            if (mode != GameMode.Career) _career.Deactivate();
            _hud.LaunchCounter = data.LaunchCounter;
            _rig.ViewEarth();
            Started = true;
            _autosaveTimer = AutosaveMinutes * 60f;
            SetStatus($"Welcome back — {Astro.ToDateTime(data.JulianDate):d MMM yyyy HH:mm} UTC");
            return true;
        }

        public bool Save()
        {
            if (!Started || !_sim.IsLoaded) return false;
            try
            {
                var data = new SaveData { Mode = Mode, SavedAtUtc = DateTime.UtcNow.ToString("u"), LaunchCounter = _hud.LaunchCounter };
                var images = new List<byte[]>();
                _sim.WriteSave(data);
                _fleet.WriteSave(data, images);
                _launches.WriteSave(data);
                _lunar.WriteSave(data);
                _career.WriteSave(data);
                SaveGame.Write(SaveGame.PathFor(Mode), data, _sim.Catalog, images);
                SetStatus("Game saved");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Spaced] Saving failed: {e}");
                SetStatus("Saving failed: " + e.Message);
                return false;
            }
        }

        /// <summary>Back to the menu: the current game is saved first.</summary>
        public void QuitToMenu()
        {
            Save();
            Started = false;
        }

        void SetStatus(string text)
        {
            Status = text;
            StatusTime = Time.unscaledTime;
        }

        void Update()
        {
            if (!Started) return;
            _autosaveTimer -= Time.unscaledDeltaTime;
            if (_autosaveTimer > 0) return;
            _autosaveTimer = AutosaveMinutes * 60f;
            Save();
        }

        void OnApplicationQuit() => Save();
    }
}
