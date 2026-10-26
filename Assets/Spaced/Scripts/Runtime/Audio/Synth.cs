using System;

namespace Spaced
{
    /// <summary>
    /// Offline synthesis of every sound in the game, so the project needs no audio assets and has no licensing
    /// strings attached. All functions return interleaved float PCM; they are pure and safe to run on a worker thread.
    /// </summary>
    public static class Synth
    {
        public const int SampleRate = 44100;
        const double TwoPi = Math.PI * 2.0;

        static double Note(int midi) => 440.0 * Math.Pow(2.0, (midi - 69) / 12.0);

        // ───────────────────────────── Music ─────────────────────────────

        /// <summary>
        /// "Soft cosmic" ambient loop: four slowly crossfading extended chords (D major / lydian colours), a sub drone,
        /// sparse pentatonic bell shimmers, all through a small Schroeder reverb. Rendered twice and the second pass
        /// kept, so the reverb tail wraps and the loop is seamless. Stereo, <paramref name="seconds"/> long.
        /// </summary>
        public static float[] AmbientMusic(int seconds = 48, int seed = 11)
        {
            int frames = seconds * SampleRate;
            int[][] chords =
            {
                new[] { 38, 45, 54, 61, 64 },  // Dmaj9
                new[] { 35, 42, 50, 57, 64 },  // Bm11
                new[] { 31, 38, 47, 54, 61 },  // Gmaj7#11
                new[] { 33, 40, 49, 52, 59 },  // Aadd9
            };
            double chordLength = seconds / (double)chords.Length;
            int[] bellNotes = { 74, 76, 78, 81, 83, 86, 88 };

            // Bell schedule, identical in both passes.
            var random = new Random(seed);
            var bellTimes = new System.Collections.Generic.List<(double time, double freq, double pan)>();
            for (double t = 1.0; t < seconds; t += 1.6 + random.NextDouble() * 3.2)
                bellTimes.Add((t, Note(bellNotes[random.Next(bellNotes.Length)]), random.NextDouble() * 1.6 - 0.8));

            // Every oscillator completes a whole number of cycles per loop, so the seam is click-free.
            double Loop(double f) => Math.Max(1.0, Math.Round(f * seconds)) / seconds;

            var output = new float[frames * 2];
            var reverbL = new Reverb(0);
            var reverbR = new Reverb(23);
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < frames; i++)
                {
                    double t = i / (double)SampleRate;
                    double left = 0, right = 0;

                    // Pad: each chord fades in and out over a raised-cosine window that overlaps its neighbours.
                    for (int c = 0; c < chords.Length; c++)
                    {
                        double centre = (c + 0.5) * chordLength;
                        double distance = Wrap(t - centre, seconds);
                        double window = Math.Abs(distance) < chordLength ? 0.5 + 0.5 * Math.Cos(Math.PI * distance / chordLength) : 0;
                        if (window <= 0) continue;
                        for (int n = 0; n < chords[c].Length; n++)
                        {
                            double f = Note(chords[c][n]);
                            double level = window * (n == 0 ? 0.055 : 0.035) / (1.0 + n * 0.15);
                            // Two detuned partials per note, spread left/right, plus a soft octave.
                            double a = Math.Sin(TwoPi * Loop(f * 0.9985) * t + n);
                            double b = Math.Sin(TwoPi * Loop(f * 1.0015) * t + n * 2.1);
                            double o = Math.Sin(TwoPi * Loop(f * 2.0) * t + n * 0.7) * 0.18;
                            left += level * (a + o);
                            right += level * (b + o);
                        }
                    }

                    // Slow breathing and a sub-bass drone on D.
                    double breathe = 0.82 + 0.18 * Math.Sin(TwoPi * t / 16.0);
                    left *= breathe;
                    right *= breathe;
                    double drone = Math.Sin(TwoPi * Loop(Note(26)) * t) * 0.03 * (0.8 + 0.2 * Math.Sin(TwoPi * t / 24.0));
                    left += drone;
                    right += drone;

                    // Bells: sine + inharmonic partial with an exponential decay.
                    foreach (var bell in bellTimes)
                    {
                        double dt = t - bell.time;
                        if (dt < 0) dt += seconds; // a bell struck near the end rings on into the start
                        if (dt > 6) continue;
                        double envelope = Math.Exp(-dt * 0.9) * Math.Min(1.0, dt * 200.0);
                        double tone = Math.Sin(TwoPi * bell.freq * dt) + 0.3 * Math.Sin(TwoPi * bell.freq * 2.76 * dt) * Math.Exp(-dt * 2.0);
                        double v = tone * envelope * 0.035;
                        left += v * (1.0 - bell.pan) * 0.5;
                        right += v * (1.0 + bell.pan) * 0.5;
                    }

                    double wetL = reverbL.Process(left), wetR = reverbR.Process(right);
                    if (pass == 1)
                    {
                        output[i * 2] = (float)(left * 0.65 + wetL * 0.6);
                        output[i * 2 + 1] = (float)(right * 0.65 + wetR * 0.6);
                    }
                }
            }
            Normalize(output, 0.55f);
            return output;
        }

        static double Wrap(double x, double period)
        {
            x %= period;
            if (x > period / 2) x -= period;
            if (x < -period / 2) x += period;
            return x;
        }

        /// <summary>Freeverb-style: four damped combs in parallel, two all-passes in series.</summary>
        sealed class Reverb
        {
            readonly float[][] _combs;
            readonly int[] _combIndex = new int[4];
            readonly float[] _combStore = new float[4];
            readonly float[][] _allpasses;
            readonly int[] _allpassIndex = new int[2];

            public Reverb(int spread)
            {
                int[] combLengths = { 1557, 1617, 1491, 1422 };
                int[] allpassLengths = { 556, 441 };
                _combs = new float[4][];
                for (int i = 0; i < 4; i++) _combs[i] = new float[combLengths[i] + spread];
                _allpasses = new float[2][];
                for (int i = 0; i < 2; i++) _allpasses[i] = new float[allpassLengths[i] + spread];
            }

            public double Process(double input)
            {
                double sum = 0;
                for (int c = 0; c < 4; c++)
                {
                    var buffer = _combs[c];
                    float y = buffer[_combIndex[c]];
                    _combStore[c] = y * 0.7f + _combStore[c] * 0.3f;
                    buffer[_combIndex[c]] = (float)(input * 0.25 + _combStore[c] * 0.86f);
                    _combIndex[c] = (_combIndex[c] + 1) % buffer.Length;
                    sum += y;
                }
                for (int a = 0; a < 2; a++)
                {
                    var buffer = _allpasses[a];
                    float delayed = buffer[_allpassIndex[a]];
                    float x = (float)sum;
                    buffer[_allpassIndex[a]] = x + delayed * 0.5f;
                    sum = delayed - x;
                    _allpassIndex[a] = (_allpassIndex[a] + 1) % buffer.Length;
                }
                return sum;
            }
        }

        // ───────────────────────────── Effects (mono) ─────────────────────────────

        /// <summary>Seamless rocket roar loop: brown noise with crackle (shock-wave pops), crossfaded at the seam.</summary>
        public static float[] Rumble(double seconds = 4.0, int seed = 3)
        {
            int n = (int)(seconds * SampleRate), fade = SampleRate / 4;
            var random = new Random(seed);
            var raw = new float[n + fade];
            double brown = 0, low = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                double white = random.NextDouble() * 2 - 1;
                brown = (brown + white * 0.02) * 0.998;
                low += (white - low) * 0.08;
                double crackle = random.NextDouble() < 0.004 ? (random.NextDouble() * 2 - 1) * 0.8 : 0;
                raw[i] = (float)(brown * 3.5 + low * 0.5 + crackle);
            }
            var loop = new float[n];
            for (int i = 0; i < n; i++)
            {
                if (i < fade)
                {
                    float w = i / (float)fade;
                    loop[i] = raw[i] * w + raw[n + i] * (1 - w);
                }
                else loop[i] = raw[i];
            }
            Normalize(loop, 0.9f);
            return loop;
        }

        /// <summary>Supersonic booster coming home: the double "ba-boom" heard before a landing.</summary>
        public static float[] SonicBoom(int seed = 5)
        {
            int n = SampleRate * 3;
            var output = new float[n];
            var random = new Random(seed);
            foreach (double start in new[] { 0.0, 0.32 })
            {
                double low = 0;
                for (int i = 0; i < n; i++)
                {
                    double t = i / (double)SampleRate - start;
                    if (t < 0) continue;
                    double envelope = Math.Exp(-t * 3.2) * Math.Min(1.0, t * 400.0);
                    low += ((random.NextDouble() * 2 - 1) - low) * 0.05;
                    output[i] += (float)((Math.Sin(TwoPi * 45 * t) * 0.8 + low * 2.5) * envelope);
                }
            }
            Normalize(output, 0.95f);
            return output;
        }

        /// <summary>Heavy structural thump (stage separation, fairing release, touchdown).</summary>
        public static float[] Thunk(double pitch = 1.0, int seed = 7)
        {
            int n = (int)(SampleRate * 0.8);
            var output = new float[n];
            var random = new Random(seed);
            double low = 0;
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)SampleRate;
                low += ((random.NextDouble() * 2 - 1) - low) * 0.12;
                double body = Math.Sin(TwoPi * 70 * pitch * t * (1 - t * 0.4)) * Math.Exp(-t * 9);
                double ring = Math.Sin(TwoPi * 410 * pitch * t) * Math.Exp(-t * 14) * 0.25;
                output[i] = (float)(body + ring + low * Math.Exp(-t * 20) * 1.2);
            }
            Normalize(output, 0.9f);
            return output;
        }

        /// <summary>Cold-gas thruster puff: band-limited hiss with a quick attack and tail.</summary>
        public static float[] Hiss(double seconds = 0.7, int seed = 9)
        {
            int n = (int)(SampleRate * seconds);
            var output = new float[n];
            var random = new Random(seed);
            double low = 0;
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)SampleRate;
                double white = random.NextDouble() * 2 - 1;
                low += (white - low) * 0.35;
                double envelope = Math.Min(1.0, t * 60.0) * Math.Exp(-t * 4.5);
                output[i] = (float)((white - low) * envelope);
            }
            Normalize(output, 0.7f);
            return output;
        }

        public static float[] Beep(double frequency, double seconds, double decay)
        {
            int n = (int)(SampleRate * seconds);
            var output = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)SampleRate;
                output[i] = (float)(Math.Sin(TwoPi * frequency * t) * Math.Min(1.0, t * 800.0) * Math.Exp(-t * decay));
            }
            Normalize(output, 0.6f);
            return output;
        }

        /// <summary>Deployment chime: a soft major triad arpeggio.</summary>
        public static float[] Chime()
        {
            int n = SampleRate * 3;
            var output = new float[n];
            int[] notes = { 74, 78, 81, 86 };
            for (int k = 0; k < notes.Length; k++)
            {
                double f = Note(notes[k]), start = k * 0.12;
                for (int i = 0; i < n; i++)
                {
                    double t = i / (double)SampleRate - start;
                    if (t < 0) continue;
                    double envelope = Math.Exp(-t * 1.6) * Math.Min(1.0, t * 300.0);
                    output[i] += (float)((Math.Sin(TwoPi * f * t) + 0.25 * Math.Sin(TwoPi * f * 3 * t) * Math.Exp(-t * 4)) * envelope * 0.3);
                }
            }
            Normalize(output, 0.6f);
            return output;
        }

        /// <summary>Camera shutter: two short mechanical clicks.</summary>
        public static float[] Shutter(int seed = 13)
        {
            int n = (int)(SampleRate * 0.25);
            var output = new float[n];
            var random = new Random(seed);
            foreach (double start in new[] { 0.0, 0.09 })
            {
                for (int i = 0; i < n; i++)
                {
                    double t = i / (double)SampleRate - start;
                    if (t < 0 || t > 0.03) continue;
                    output[i] += (float)((random.NextDouble() * 2 - 1) * Math.Exp(-t * 180) + Math.Sin(TwoPi * 2400 * t) * Math.Exp(-t * 250) * 0.5);
                }
            }
            Normalize(output, 0.6f);
            return output;
        }

        static void Normalize(float[] samples, float peak)
        {
            float max = 1e-6f;
            foreach (float s in samples) max = Math.Max(max, Math.Abs(s));
            float scale = peak / max;
            for (int i = 0; i < samples.Length; i++) samples[i] *= scale;
        }
    }
}
