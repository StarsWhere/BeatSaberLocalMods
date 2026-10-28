using System;
using System.Collections.Generic;
using System.Linq;

namespace NoteMotionControl.Reduction
{
    /// <summary>
    /// Statistics used by the selector and by the settings result line.  The
    /// input timestamps are already playback seconds; callers are responsible
    /// for applying the immutable song-speed snapshot exactly once.
    /// </summary>
    public static class DensityStatistics
    {
        private const double Epsilon = 1e-9;

        public static double PeakNps(IEnumerable<ActionPoint> actions, double windowSeconds)
        {
            if (actions == null) throw new ArgumentNullException(nameof(actions));
            windowSeconds = Math.Max(0.001, windowSeconds);
            double[] times = actions
                .Select(action => action.Time)
                .Where(time => !double.IsNaN(time) && !double.IsInfinity(time))
                .OrderBy(time => time)
                .ToArray();
            if (times.Length == 0) return 0;

            int left = 0;
            int peakCount = 0;
            for (int right = 0; right < times.Length; right++)
            {
                // The selector's rolling window includes both endpoints.  The
                // epsilon keeps the boundary behavior identical to CanAdd.
                while (left <= right && times[right] - times[left] > windowSeconds + Epsilon)
                    left++;
                peakCount = Math.Max(peakCount, right - left + 1);
            }

            return peakCount / windowSeconds;
        }
    }
}


