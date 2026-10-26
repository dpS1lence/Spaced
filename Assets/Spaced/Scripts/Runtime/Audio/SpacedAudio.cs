using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;

namespace Spaced
{
    /// <summary>Background music: one of the synthesised loops, or the player's own files from <see cref="SpacedAudio.MusicFolder"/>.</summary>
    public enum MusicTrack
    {
        Cosmic,
        Organ,
        YourMusic,
    }

    /// <summary>
    /// Music and sound effects, all synthesised at startup (<see cref="Synth"/>), or music files of your own.
    /// Volumes and the music choice are remembered between runs.
    /// <para>Rocket roar travels at the speed of sound: from the pad camera ~3 km away you hear liftoff about nine
    /// seconds after you see it. There is no sound in vacuum, except what the onboard cameras pick up through the
    /// structure.</para>
    /// </summary>
    [DefaultExecutionOrder(60)]
    public sealed class SpacedAudio : MonoBehaviour
    {
        const double SpeedOfSound = 343.0;

        public float MusicVolume = 0.35f;
        public float EffectsVolume = 0.8f;
        public bool Muted;
        public MusicTrack Track = MusicTrack.Cosmic;

        /// <summary>Drop .mp3, .ogg or .wav files here to hear them with "Your music".</summary>
        public static string MusicFolder => Path.Combine(Application.persistentDataPath, "Music");
        /// <summary>Your music: the file playing now (empty if none), and how many there are.</summary>
        public string NowPlaying { get; private set; } = "";
        public int YourMusicCount { get; private set; }

        const string PrefMusic = "Spaced.MusicVolume", PrefEffects = "Spaced.EffectsVolume", PrefMuted = "Spaced.Muted", PrefTrack = "Spaced.MusicTrack";
        float _savedMusic, _savedEffects;
        bool _savedMuted;
        MusicTrack _savedTrack;
        float _saveAt = -1;

        SpaceSimulation _sim;
        CameraRig _rig;
        LaunchManager _launches;
        System.Func<Vector2, bool> _isPointerOverUi;

        AudioSource _music, _roar, _effects;
        AudioLowPassFilter _roarFilter;
        AudioClip _boom, _thunk, _thunkLow, _hiss, _click, _tick, _chime, _shutter;
        readonly Dictionary<MusicTrack, Task<float[]>> _renders = new Dictionary<MusicTrack, Task<float[]>>();
        readonly Dictionary<MusicTrack, AudioClip> _loops = new Dictionary<MusicTrack, AudioClip>();
        MusicTrack _playing = (MusicTrack)(-1);
        float _fade = 1f;
        // Your music: a shuffled playlist, loaded one file at a time.
        readonly List<string> _playlist = new List<string>();
        int _playlistIndex = -1;
        bool _loadingFile;
        float _nextScanAt;
        AudioClip _fileClip;

        // Thrust history of the watched flight, keyed by mission time, for the sound delay.
        readonly List<(double time, float level)> _thrustHistory = new List<(double, float)>();
        LaunchMission _mission;
        int _eventCursor;
        readonly List<(float playAt, AudioClip clip, float volume)> _scheduled = new List<(float, AudioClip, float)>();
        int _lastCountdownSecond = int.MinValue;

        public void Init(SpaceSimulation sim, CameraRig rig, LaunchManager launches, FleetManager fleet, System.Func<Vector2, bool> isPointerOverUi)
        {
            _sim = sim;
            _rig = rig;
            _launches = launches;
            _isPointerOverUi = isPointerOverUi;

            if (rig.Far.GetComponent<AudioListener>() == null) rig.Far.gameObject.AddComponent<AudioListener>();

            _music = NewSource("Music", true);
            _effects = NewSource("Effects", false);
            _roar = NewSource("Rocket Roar", true);
            _roarFilter = _roar.gameObject.AddComponent<AudioLowPassFilter>();

            _roar.clip = Clip("Roar", Synth.Rumble(), 1);
            _roar.volume = 0;
            _roar.Play();
            _boom = Clip("Sonic Boom", Synth.SonicBoom(), 1);
            _thunk = Clip("Thunk", Synth.Thunk(), 1);
            _thunkLow = Clip("Touchdown", Synth.Thunk(0.6), 1);
            _hiss = Clip("Hiss", Synth.Hiss(), 1);
            _click = Clip("Click", Synth.Beep(1800, 0.04, 90), 1);
            _tick = Clip("Tick", Synth.Beep(1000, 0.12, 30), 1);
            _chime = Clip("Chime", Synth.Chime(), 1);
            _shutter = Clip("Shutter", Synth.Shutter(), 1);

            LoadSettings();
            // The loops take a second or two to render; do it off the main thread, the chosen one first.
            Render(Track == MusicTrack.YourMusic ? MusicTrack.Cosmic : Track);
            fleet.ImageCaptured += _ => Play(_shutter, 0.7f);
        }

        // ───────────────────────────── Settings ─────────────────────────────

        void LoadSettings()
        {
            MusicVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(PrefMusic, MusicVolume));
            EffectsVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(PrefEffects, EffectsVolume));
            Muted = PlayerPrefs.GetInt(PrefMuted, Muted ? 1 : 0) == 1;
            Track = (MusicTrack)Mathf.Clamp(PlayerPrefs.GetInt(PrefTrack, (int)Track), 0, (int)MusicTrack.YourMusic);
            _savedMusic = MusicVolume;
            _savedEffects = EffectsVolume;
            _savedMuted = Muted;
            _savedTrack = Track;
        }

        /// <summary>Remembers changed settings, written to disk a moment after the last change (sliders change every frame).</summary>
        void SaveSettingsIfChanged()
        {
            if (MusicVolume != _savedMusic || EffectsVolume != _savedEffects || Muted != _savedMuted || Track != _savedTrack)
            {
                _savedMusic = MusicVolume;
                _savedEffects = EffectsVolume;
                _savedMuted = Muted;
                _savedTrack = Track;
                PlayerPrefs.SetFloat(PrefMusic, MusicVolume);
                PlayerPrefs.SetFloat(PrefEffects, EffectsVolume);
                PlayerPrefs.SetInt(PrefMuted, Muted ? 1 : 0);
                PlayerPrefs.SetInt(PrefTrack, (int)Track);
                _saveAt = Time.unscaledTime + 1f;
            }
            if (_saveAt > 0 && Time.unscaledTime >= _saveAt)
            {
                _saveAt = -1;
                PlayerPrefs.Save();
            }
        }

        void OnApplicationQuit()
        {
            SaveSettingsIfChanged();
            PlayerPrefs.Save();
        }

        // ───────────────────────────── Music ─────────────────────────────

        void Render(MusicTrack track)
        {
            if (track == MusicTrack.YourMusic || _renders.ContainsKey(track)) return;
            _renders[track] = Task.Run(() => track == MusicTrack.Organ ? Synth.OrganMusic() : Synth.AmbientMusic());
        }

        /// <summary>Plays the next file of your music (rescanning the folder), or lists none.</summary>
        public void NextTrack()
        {
            if (Track != MusicTrack.YourMusic) return;
            ScanMusicFolder(false);
            if (_playlist.Count == 0) return;
            _playlistIndex = (_playlistIndex + 1) % _playlist.Count;
            StartCoroutine(LoadFile(_playlist[_playlistIndex]));
        }

        /// <summary>Opens the music folder in Explorer (creating it first).</summary>
        public void OpenMusicFolder()
        {
            Directory.CreateDirectory(MusicFolder);
            Application.OpenURL(new System.Uri(MusicFolder).AbsoluteUri);
        }

        void ScanMusicFolder(bool shuffle)
        {
            var files = new List<string>();
            try
            {
                Directory.CreateDirectory(MusicFolder);
                foreach (var file in Directory.GetFiles(MusicFolder))
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext == ".mp3" || ext == ".ogg" || ext == ".wav") files.Add(file);
                }
            }
            catch (IOException) { }
            catch (System.UnauthorizedAccessException) { }
            files.Sort();
            YourMusicCount = files.Count;
            if (files.Count == _playlist.Count && files.TrueForAll(_playlist.Contains)) return;
            if (shuffle)
                for (int i = files.Count - 1; i > 0; i--)
                {
                    int j = UnityEngine.Random.Range(0, i + 1);
                    (files[i], files[j]) = (files[j], files[i]);
                }
            _playlist.Clear();
            _playlist.AddRange(files);
            _playlistIndex = -1;
        }

        IEnumerator LoadFile(string path)
        {
            _loadingFile = true;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            var type = ext == ".mp3" ? AudioType.MPEG : ext == ".ogg" ? AudioType.OGGVORBIS : AudioType.WAV;
            using (var request = UnityWebRequestMultimedia.GetAudioClip(new System.Uri(path).AbsoluteUri, type))
            {
                ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio = true;
                yield return request.SendWebRequest();
                _loadingFile = false;
                if (Track != MusicTrack.YourMusic) yield break;
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"Music: couldn't play {Path.GetFileName(path)}: {request.error}");
                    NowPlaying = $"couldn't play {Path.GetFileName(path)}";
                    yield break;
                }
                var clip = DownloadHandlerAudioClip.GetContent(request);
                if (_fileClip != null && _fileClip != clip) Destroy(_fileClip);
                _fileClip = clip;
                _music.Stop();
                _music.loop = false;
                _music.clip = clip;
                _music.Play();
                NowPlaying = Path.GetFileNameWithoutExtension(path);
            }
        }

        /// <summary>Switches tracks with a short fade, starts loops once rendered, and moves through your music.</summary>
        void UpdateMusic()
        {
            if (Track != _playing)
            {
                // Fade out what's playing, then switch.
                _fade = Mathf.MoveTowards(_fade, 0f, Time.unscaledDeltaTime / 0.8f);
                if (_fade > 0f && _music.isPlaying) return;
                _music.Stop();
                _playing = Track;
                NowPlaying = "";
                if (Track == MusicTrack.YourMusic)
                {
                    ScanMusicFolder(true);
                    if (_playlist.Count > 0) NextTrack();
                }
            }

            if (_playing == MusicTrack.YourMusic)
            {
                // Next file when one ends (not while the window is in the background, when audio may be paused).
                if (!_loadingFile && !_music.isPlaying && Application.isFocused && Time.unscaledTime >= _nextScanAt)
                {
                    _nextScanAt = Time.unscaledTime + 3f; // an empty folder is looked at again every few seconds
                    NextTrack();
                }
            }
            else if (!_music.isPlaying || _music.clip == null || !_loops.ContainsValue(_music.clip))
            {
                Render(_playing);
                if (_loops.TryGetValue(_playing, out var loop) || TryFinishRender(_playing, out loop))
                {
                    _music.loop = true;
                    _music.clip = loop;
                    _music.Play();
                }
            }
            _fade = Mathf.MoveTowards(_fade, 1f, Time.unscaledDeltaTime / 0.8f);
        }

        bool TryFinishRender(MusicTrack track, out AudioClip clip)
        {
            clip = null;
            if (!_renders.TryGetValue(track, out var task) || !task.IsCompleted || task.IsFaulted) return false;
            clip = Clip(track.ToString(), task.Result, 2);
            _loops[track] = clip;
            return true;
        }

        AudioSource NewSource(string sourceName, bool loop)
        {
            var source = new GameObject(sourceName).AddComponent<AudioSource>();
            source.transform.SetParent(transform, false);
            source.loop = loop;
            source.playOnAwake = false;
            source.spatialBlend = 0;
            return source;
        }

        static AudioClip Clip(string clipName, float[] samples, int channels)
        {
            var clip = AudioClip.Create(clipName, samples.Length / channels, channels, Synth.SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        void Play(AudioClip clip, float volume)
        {
            if (!Muted) _effects.PlayOneShot(clip, volume * EffectsVolume);
        }

        void Update()
        {
            if (_sim == null) return;
            UpdateMusic();
            SaveSettingsIfChanged();
            _music.volume = Muted ? 0 : MusicVolume * _fade * (Cinematic.Active ? 0.6f : 1f);

            // UI clicks.
            var mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame && _isPointerOverUi != null && _isPointerOverUi(mouse.position.ReadValue()))
                Play(_click, 0.35f);

            UpdateLaunchAudio();

            for (int i = _scheduled.Count - 1; i >= 0; i--)
            {
                if (Time.unscaledTime < _scheduled[i].playAt) continue;
                Play(_scheduled[i].clip, _scheduled[i].volume);
                _scheduled.RemoveAt(i);
            }
        }

        void UpdateLaunchAudio()
        {
            var mission = _launches.Watched;
            var flight = mission?.Flight;
            if (flight == null)
            {
                _roar.volume = Mathf.MoveTowards(_roar.volume, 0, Time.unscaledDeltaTime);
                _mission = null;
                return;
            }
            if (_mission != mission)
            {
                _mission = mission;
                _eventCursor = flight.Events.Count;
                _thrustHistory.Clear();
            }

            double t = flight.MissionTime + flight.RenderLead;
            double warp = math.max(1.0, math.abs(_sim.Clock.Warp));

            // Loudest source: the stack before staging, the booster during its burns afterwards.
            var booster = flight.Booster;
            double3 sourceKm = flight.RenderPosition / 1000.0;
            float level = flight.BoosterAttached ? (float)(flight.EnginesLit > 0 ? flight.Throttle : 0) : 0f;
            if (booster != null && booster.EnginesLit > 0)
            {
                level = (float)booster.Throttle * (booster.EnginesLit >= 3 ? 0.8f : 0.55f);
                sourceKm = flight.Extrapolate(booster.Position, booster.Velocity) / 1000.0;
            }
            _thrustHistory.Add((t, level));
            if (_thrustHistory.Count > 4000) _thrustHistory.RemoveRange(0, 1000);

            double distanceM = math.distance(_rig.CameraEciKm, sourceKm) * 1000.0;
            double cameraAltitudeKm = math.length(_rig.CameraEciKm) - EarthConstants.RadiusKm;
            bool onboard = _rig.Mode == ViewMode.Onboard;
            float heard;
            if (onboard)
            {
                // Structure-borne: immediate, muffled, works in vacuum too.
                heard = LevelAt(t) * 0.45f;
                _roarFilter.cutoffFrequency = 900f;
            }
            else
            {
                // Air-borne: delayed by distance, quieter and duller far away, nothing above the atmosphere.
                float air = (float)math.saturate(1.0 - (cameraAltitudeKm - 30.0) / 30.0);
                float delayed = LevelAt(t - distanceM / SpeedOfSound);
                float falloff = (float)(1.0 / (1.0 + distanceM * distanceM / (1500.0 * 1500.0)));
                heard = delayed * falloff * air;
                _roarFilter.cutoffFrequency = Mathf.Lerp(400f, 6000f, falloff);
            }
            // At high warp the pitch of a recorded roar would be meaningless; just keep it steady and quieter.
            float target = Muted ? 0 : heard * EffectsVolume * (warp > 30 ? 0.3f : 1f);
            _roar.volume = Mathf.MoveTowards(_roar.volume, target, Time.unscaledDeltaTime * 2f);
            _roar.pitch = 0.9f + 0.2f * Mathf.Clamp01(heard);

            // Countdown ticks over the last ten seconds.
            if (t < 0 && t > -10.5)
            {
                int second = (int)math.floor(t);
                if (second != _lastCountdownSecond)
                {
                    _lastCountdownSecond = second;
                    Play(_tick, 0.5f);
                }
            }

            for (; _eventCursor < flight.Events.Count; _eventCursor++)
                OnFlightEvent(flight.Events[_eventCursor].Text, flight, distanceM, onboard, warp);
        }

        float LevelAt(double time)
        {
            if (_thrustHistory.Count == 0) return 0;
            if (time <= _thrustHistory[0].time) return time < 0 ? 0 : _thrustHistory[0].level;
            for (int i = _thrustHistory.Count - 1; i >= 0; i--)
                if (_thrustHistory[i].time <= time) return _thrustHistory[i].level;
            return 0;
        }

        void OnFlightEvent(string text, RocketFlight flight, double distanceM, bool onboard, double warp)
        {
            bool near = onboard || distanceM < 2000;
            if (text.StartsWith("Stage separation") && near) { Play(_thunk, 0.9f); Play(_hiss, 0.6f); }
            else if (text.StartsWith("Fairing") && near) Play(_thunk, 0.6f);
            else if (text.StartsWith("Entry burn startup") && near) Play(_hiss, 0.4f);
            else if (text.StartsWith("Landing burn startup"))
            {
                // The returning booster's sonic boom reaches a camera near the landing zone first.
                var booster = flight.Booster;
                double3 boosterKm = booster != null ? booster.Position / 1000.0 : flight.Position / 1000.0;
                double d = math.distance(_rig.CameraEciKm, boosterKm) * 1000.0;
                if (d < 40000 && !onboard) _scheduled.Add((Time.unscaledTime + (float)(d / SpeedOfSound / warp), _boom, 0.9f));
            }
            else if (text.StartsWith("Touchdown") && near) Play(_thunkLow, 1f);
            else if (text.StartsWith("Payload")) Play(_chime, 0.7f);
        }
    }
}
