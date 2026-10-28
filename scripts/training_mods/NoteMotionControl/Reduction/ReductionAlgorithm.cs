#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Linq;
using NoteMotionControl;

namespace NoteMotionControl.Reduction
{
    public enum ActionHand
    {
        Any = 0,
        Left = 1,
        Right = 2,
    }

    /// <summary>
    /// A scoring action in playback seconds.  The reducer deliberately receives
    /// playback time rather than beat time so Practice speed is applied exactly
    /// once when a chart snapshot is created.
    /// </summary>
    public readonly struct ActionPoint
    {
        public ActionPoint(double time, ActionHand hand)
        {
            Time = time;
            Hand = hand;
        }

        public double Time { get; }

        public ActionHand Hand { get; }
    }

    /// <summary>
    /// The smallest unit the selector may retain or remove.  A Burst slider
    /// group contains its head and every chain link, while a normal note or arc
    /// endpoint is one group.  Weight is the number of scoring objects used by
    /// the percentage allocator and by the rolling NPS check.
    /// </summary>
    public sealed class CandidateGroup
    {
        public CandidateGroup(
            int id,
            int weight,
            IEnumerable<ActionPoint> actions,
            bool forcedKeep = false)
        {
            if (weight < 0) throw new ArgumentOutOfRangeException(nameof(weight));
            Id = id;
            Weight = weight;
            Actions = (actions ?? throw new ArgumentNullException(nameof(actions)))
                .OrderBy(a => a.Time)
                .ThenBy(a => a.Hand)
                .ToArray();
            ForcedKeep = forcedKeep;
        }

        public int Id { get; }

        public int Weight { get; }

        public IReadOnlyList<ActionPoint> Actions { get; }

        public bool ForcedKeep { get; }

        public double FirstTime => Actions.Count == 0 ? double.PositiveInfinity : Actions[0].Time;
    }

    public sealed class SelectionResult
    {
        internal SelectionResult(HashSet<int> kept, int fallbackGroupId, int consideredWeight)
        {
            KeptGroupIds = kept;
            FallbackGroupId = fallbackGroupId;
            ConsideredWeight = consideredWeight;
        }

        public IReadOnlyCollection<int> KeptGroupIds { get; }

        public int FallbackGroupId { get; }

        public int ConsideredWeight { get; }

        public bool UsedFallback => FallbackGroupId >= 0;
    }

    /// <summary>
    /// Deterministic selection rules shared by runtime code and the regression
    /// tests.  No random source, clock, hash code, or object identity is used.
    /// </summary>
    public static class SelectionAlgorithm
    {
        private const double Epsilon = 1e-9;

        public static SelectionResult SelectPercentage(
            IEnumerable<CandidateGroup> input,
            double retainFraction)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var groups = Sort(input).Where(g => !g.ForcedKeep && g.Weight > 0).ToArray();
            var kept = new HashSet<int>();
            var forced = Sort(input).Where(g => g.ForcedKeep).ToArray();
            foreach (CandidateGroup group in forced) kept.Add(group.Id);

            retainFraction = Clamp(retainFraction, 0, 1);
            int totalWeight = groups.Sum(g => g.Weight);
            int processedWeight = 0;
            int keptWeight = 0;

            foreach (CandidateGroup group in groups)
            {
                int nextProcessed = processedWeight + group.Weight;
                double target = nextProcessed * retainFraction;
                double keepError = Math.Abs((keptWeight + group.Weight) - target);
                double dropError = Math.Abs(keptWeight - target);

                // A tie is resolved toward keeping the group.  This is the
                // weighted error diffusion rule: with four weight-2 groups at
                // 50%, it keeps groups 1 and 3, rather than every group.
                bool keep = keepError < dropError - Epsilon ||
                            Math.Abs(keepError - dropError) <= Epsilon;
                if (keep)
                {
                    kept.Add(group.Id);
                    keptWeight += group.Weight;
                }

                processedWeight = nextProcessed;
            }

            int fallback = EnsureMinimumGroup(groups, kept);
            if (fallback >= 0) kept.Add(fallback);
            return new SelectionResult(kept, fallback, totalWeight);
        }

        public static SelectionResult SelectDensity(
            IEnumerable<CandidateGroup> input,
            double maxNps,
            double minimumSameHandIntervalSeconds,
            double windowSeconds = 1.0)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var sorted = Sort(input).ToArray();
            var groups = sorted.Where(g => !g.ForcedKeep && g.Weight > 0).ToArray();
            var kept = new HashSet<int>(sorted.Where(g => g.ForcedKeep).Select(g => g.Id));

            maxNps = Math.Max(0, maxNps);
            minimumSameHandIntervalSeconds = Math.Max(0, minimumSameHandIntervalSeconds);
            windowSeconds = Math.Max(0.001, windowSeconds);

            // Forced groups are fake/non-interactable or otherwise marked
            // preservation-only by the planner.  They intentionally do not
            // consume the gameplay density budget or the same-hand interval.
            var selectedActions = new List<ActionPoint>();

            foreach (CandidateGroup group in groups)
            {
                if (!CanAdd(group.Actions, selectedActions, maxNps,
                        minimumSameHandIntervalSeconds, windowSeconds))
                {
                    continue;
                }

                kept.Add(group.Id);
                selectedActions.AddRange(group.Actions);
                selectedActions.Sort((a, b) =>
                {
                    int time = a.Time.CompareTo(b.Time);
                    return time != 0 ? time : a.Hand.CompareTo(b.Hand);
                });
            }

            int fallback = EnsureMinimumGroup(groups, kept);
            if (fallback >= 0) kept.Add(fallback);
            return new SelectionResult(kept, fallback, groups.Sum(g => g.Weight));
        }

        private static bool CanAdd(
            IReadOnlyList<ActionPoint> candidate,
            IReadOnlyList<ActionPoint> selected,
            double maxNps,
            double minimumSameHandIntervalSeconds,
            double windowSeconds)
        {
            if (candidate.Count == 0) return true;

            var combined = selected.Concat(candidate)
                .OrderBy(a => a.Time)
                .ThenBy(a => a.Hand)
                .ToArray();

            // Check each hand independently.  A left and right note in the
            // same row do not block one another.
            var previousByHand = new Dictionary<ActionHand, double>();
            foreach (ActionPoint action in combined)
            {
                if (action.Hand == ActionHand.Any) continue;
                if (previousByHand.TryGetValue(action.Hand, out double previous) &&
                    action.Time - previous + Epsilon < minimumSameHandIntervalSeconds)
                {
                    return false;
                }

                previousByHand[action.Hand] = action.Time;
            }

            // Every window whose right edge is not an action contains no more
            // actions than the window ending at the next action, so checking
            // action-ending windows is sufficient for a one-second rolling NPS
            // cap.  Chain links are individual actions even though their group
            // decision remains atomic.
            if (maxNps <= 0) return false;
            double limit = maxNps * windowSeconds + Epsilon;
            for (int i = 0; i < combined.Length; i++)
            {
                double start = combined[i].Time - windowSeconds;
                int count = 0;
                for (int j = i; j >= 0 && combined[j].Time + Epsilon >= start; j--)
                {
                    count++;
                }

                if (count > limit)
                {
                    return false;
                }
            }

            return true;
        }

        private static IEnumerable<CandidateGroup> Sort(IEnumerable<CandidateGroup> groups)
        {
            return groups.OrderBy(g => g.FirstTime).ThenBy(g => g.Id);
        }

        private static int EnsureMinimumGroup(IEnumerable<CandidateGroup> groups, HashSet<int> kept)
        {
            if (groups.Any(g => kept.Contains(g.Id))) return -1;
            CandidateGroup? fallback = groups
                .Where(g => g.Weight > 0)
                .OrderBy(g => g.Weight)
                .ThenBy(g => g.FirstTime)
                .ThenBy(g => g.Id)
                .FirstOrDefault();
            return fallback?.Id ?? -1;
        }

        private static double Clamp(double value, double min, double max)
        {
            return value < min ? min : value > max ? max : value;
        }
    }
}


