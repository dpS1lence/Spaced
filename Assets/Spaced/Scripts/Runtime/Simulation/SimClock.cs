using System;

namespace Spaced
{
    /// <summary>Simulation time as a Julian date (UTC), with pause and time warp.</summary>
    public sealed class SimClock
    {
        /// <summary>Warp steps in sim-seconds per real second, from real time up to a year per second.</summary>
        public static readonly double[] WarpSteps =
        {
            1, 10, 60, 300, 1800, 3600, 6 * 3600, MathConst.SecondsPerDay, 7 * MathConst.SecondsPerDay,
            30 * MathConst.SecondsPerDay, 0.25 * MathConst.SecondsPerYear, MathConst.SecondsPerYear,
        };

        public double JulianDate { get; private set; }
        public bool Paused { get; set; }
        public int WarpIndex { get; private set; }
        public bool Reverse { get; set; }

        /// <summary>Upper bound on |warp| imposed by the game (e.g. while a launch is being watched).</summary>
        public double MaxWarp { get; set; } = double.PositiveInfinity;
        public bool WarpLimited => WarpSteps[WarpIndex] > MaxWarp;

        public double Warp => (Reverse ? -1 : 1) * System.Math.Min(WarpSteps[WarpIndex], MaxWarp);
        public DateTime Utc => Astro.ToDateTime(JulianDate);

        double _pendingJumpSeconds;

        public SimClock() => JulianDate = Astro.JulianDate(DateTime.UtcNow);

        public void Faster() => WarpIndex = Math.Min(WarpIndex + 1, WarpSteps.Length - 1);
        public void Slower() => WarpIndex = Math.Max(WarpIndex - 1, 0);
        /// <summary>Sets the time directly (loading a save); nothing is propagated across the change.</summary>
        public void SetTime(double julianDate)
        {
            JulianDate = julianDate;
            _pendingJumpSeconds = 0;
        }

        public void SetWarpIndex(int index) => WarpIndex = Math.Max(0, Math.Min(index, WarpSteps.Length - 1));

        /// <summary>Jumps to the given time on the next <see cref="Tick"/>; objects are propagated across the gap.</summary>
        public void JumpTo(DateTime utc) =>
            _pendingJumpSeconds = (Astro.JulianDate(utc) - JulianDate) * MathConst.SecondsPerDay;

        /// <summary>Advances the clock and returns how many sim seconds elapsed.</summary>
        public double Tick(double realSeconds)
        {
            double dt = _pendingJumpSeconds + (Paused ? 0 : realSeconds * Warp);
            _pendingJumpSeconds = 0;
            JulianDate += dt / MathConst.SecondsPerDay;
            return dt;
        }

        public static string FormatWarp(double secondsPerSecond)
        {
            double s = Math.Abs(secondsPerSecond);
            string sign = secondsPerSecond < 0 ? "−" : "";
            if (s < 60) return $"{sign}{s:0}×";
            if (s < 3600) return $"{sign}{s / 60:0} min/s";
            if (s < MathConst.SecondsPerDay) return $"{sign}{s / 3600:0} h/s";
            if (s < 7 * MathConst.SecondsPerDay) return $"{sign}{s / MathConst.SecondsPerDay:0} day/s";
            if (s < 30 * MathConst.SecondsPerDay) return $"{sign}{s / (7 * MathConst.SecondsPerDay):0} wk/s";
            if (s < MathConst.SecondsPerYear) return $"{sign}{s / (MathConst.SecondsPerYear / 12):0} mo/s";
            return $"{sign}{s / MathConst.SecondsPerYear:0.#} yr/s";
        }
    }
}
