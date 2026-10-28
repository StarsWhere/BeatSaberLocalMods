#nullable enable annotations
using System;

namespace NoteMotionControl.Reduction
{
    /// <summary>
    /// Identifies one chart-load attempt.  Practice speed is part of the
    /// snapshot because the native transformed-data cache does not include it.
    /// </summary>
    public readonly struct TransformSession : IEquatable<TransformSession>
    {
        public TransformSession(string chartKey, long generation, float playbackSpeed)
        {
            ChartKey = chartKey ?? string.Empty;
            Generation = generation;
            PlaybackSpeed = playbackSpeed;
        }

        public string ChartKey { get; }

        public long Generation { get; }

        public float PlaybackSpeed { get; }

        public bool IsValid => !string.IsNullOrEmpty(ChartKey) && Generation > 0;

        public bool Equals(TransformSession other)
        {
            return Generation == other.Generation &&
                   string.Equals(ChartKey, other.ChartKey, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is TransformSession other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (ChartKey.GetHashCode() * 397) ^ Generation.GetHashCode();
            }
        }

        public static bool operator ==(TransformSession left, TransformSession right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(TransformSession left, TransformSession right)
        {
            return !left.Equals(right);
        }
    }

    public static class TransformPolicy
    {
        public static bool ShouldBypassTransformedCache(bool reducerEnabled)
        {
            return reducerEnabled;
        }

        public static bool IsCurrent(TransformSession expected, TransformSession current)
        {
            return expected.IsValid && current.IsValid && expected == current;
        }

        /// <summary>
        /// Resolves the session for one transform callback. A flowed session
        /// identifies the async load that produced the callback. Once it is
        /// stale, falling back to a same-chart global session would relabel an
        /// old result as the new generation, so stale flowed state is rejected.
        /// The global fallback is only for callbacks with no flowed session.
        /// </summary>
        public static bool TryResolveSession(
            TransformSession? flowed,
            TransformSession? current,
            string chartKey,
            out TransformSession session)
        {
            if (flowed.HasValue)
            {
                if (current.HasValue &&
                    string.Equals(flowed.Value.ChartKey, chartKey, StringComparison.Ordinal) &&
                    IsCurrent(flowed.Value, current.Value))
                {
                    session = flowed.Value;
                    return true;
                }

                session = default(TransformSession);
                return false;
            }

            if (current.HasValue &&
                string.Equals(current.Value.ChartKey, chartKey, StringComparison.Ordinal) &&
                current.Value.IsValid)
            {
                session = current.Value;
                return true;
            }

            session = default(TransformSession);
            return false;
        }
    }
}


