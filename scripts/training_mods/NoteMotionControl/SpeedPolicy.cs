using System;

namespace NoteMotionControl
{
    // Compare the unmodified native curve, not clipped event endpoints: a
    // curve crossing a speed cap must retain the original easing below it.
    internal sealed class SpeedPolicy
    {
        internal readonly NjsMode Mode;
        internal readonly float FixedNjs, Multiplier, OriginalInitial, OutputInitial;
        internal readonly float OriginalBaseHalf, OutputBaseHalf, MinimumOriginal;
        internal readonly bool NativeDurationFixed, OnlyReduce;
        internal readonly ReactionTimeMode TimeMode;
        internal readonly float RequestedHalf;

        private SpeedPolicy(Options options, float initial, float minimum, float originalHalf, float outputHalf, bool fixedDuration)
        {
            Mode = options.NjsMode;
            FixedNjs = options.OverrideNjs;
            Multiplier = options.SpeedMultiplier;
            OnlyReduce = options.ApplicationPolicy == ApplicationPolicy.OnlyReduce;
            TimeMode = options.ReactionTimeMode;
            RequestedHalf = options.ReactionTimeMs / 1000f;
            OriginalInitial = initial;
            OriginalBaseHalf = originalHalf;
            OutputBaseHalf = outputHalf;
            NativeDurationFixed = fixedDuration;
            MinimumOriginal = Math.Max(0.01f, Math.Min(initial, initial + minimum));
            OutputInitial = Speed(initial);
        }

        internal static bool IsRequested(Options options) => options != null && options.Enabled &&
            (options.NjsMode == NjsMode.Override ||
             options.NjsMode == NjsMode.Multiplier && options.SpeedMultiplier != 1f ||
             options.ReactionTimeMode == ReactionTimeMode.Specified);

        internal static bool TryCreate(Options options, float initial, float minimum,
            float originalHalf, float outputHalf, bool fixedDuration, out SpeedPolicy policy)
        {
            policy = null;
            if (!IsRequested(options) || !Finite(initial) || initial <= 0f || !Finite(minimum) ||
                !Finite(initial + minimum) || !Finite(originalHalf) || originalHalf <= 0f ||
                !Finite(outputHalf) || outputHalf <= 0f) return false;
            var candidate = new SpeedPolicy(options, initial, minimum, originalHalf, outputHalf, fixedDuration);
            if (!Finite(candidate.SpawnAhead) || candidate.SpawnAhead <= 0.5f) return false;
            policy = candidate;
            return true;
        }

        internal float Speed(float original)
        {
            float target = Mode == NjsMode.Override ? FixedNjs : Mode == NjsMode.Multiplier ? original * Multiplier : original;
            return OnlyReduce ? Math.Min(original, target) : target;
        }

        internal float OriginalHalf(float original) => NativeDurationFixed ? OriginalBaseHalf :
            OriginalBaseHalf / Math.Min(original / OriginalInitial, 1f);

        internal float Half(float original, float output)
        {
            float baseline = OriginalHalf(original);
            float adjusted = NativeDurationFixed ? OutputBaseHalf : OutputBaseHalf / Math.Min(output / OutputInitial, 1f);
            if (TimeMode == ReactionTimeMode.Specified)
                return OnlyReduce ? Math.Max(Math.Max(baseline, adjusted), RequestedHalf) : RequestedHalf;
            return OnlyReduce ? Math.Max(baseline, adjusted) : adjusted;
        }

        internal bool ChangesAt(float original) => Math.Abs(Speed(original) - original) > 0.00001f ||
            Math.Abs(Half(original, Speed(original)) - OriginalHalf(original)) > 0.00001f;

        internal float SpawnAhead => 0.5f + Half(MinimumOriginal, Speed(MinimumOriginal));
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
