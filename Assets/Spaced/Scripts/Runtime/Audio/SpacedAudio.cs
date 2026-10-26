using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Spaced
{
    /// <summary>
    /// Music and sound effects, all synthesised at startup (<see cref="Synth"/>).
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

        SpaceSimulation _sim;
        CameraRig _rig;
        LaunchManager _launches;
        System.Func<Vector2, bool> _isPointerOverUi;

        AudioSource _music, _roar, _effects;
        AudioLowPassFilter _roarFilter;
        AudioClip _boom, _thunk, _thunkLow, _hiss, _click, _tick, _chime, _shutter;
        Task<float[]> _musicTask;
        bool _musicStarted;

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

            // The music takes a second or two to render; do it off the main thread.
            _musicTask = Task.Run(() => Synth.AmbientMusic());
            fleet.ImageCaptured += _ => Play(_shutter, 0.7f);
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
            StartMusicWhenReady();
            _music.volume = Muted ? 0 : MusicVolume * (Cinematic.Active ? 0.6f : 1f);

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

        void StartMusicWhenReady()
        {
            if (_musicStarted || _musicTask == null || !_musicTask.IsCompleted) return;
            _musicStarted = true;
            if (_musicTask.IsFaulted) return;
            _music.clip = Clip("Ambient", _musicTask.Result, 2);
            _music.Play();
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
