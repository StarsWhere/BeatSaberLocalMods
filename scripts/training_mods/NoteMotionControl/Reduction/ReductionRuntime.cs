using System;
using NoteMotionControl;

namespace NoteMotionControl.Reduction
{
    /// <summary>
    /// The completed result of one immutable chart transformation snapshot.
    /// A result is published only while its load generation is still current.
    /// </summary>
    internal sealed class ReductionOutcome
    {
        public static ReductionOutcome Disabled(string chartKey, Options options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            return new ReductionOutcome
            {
                ChartKey = chartKey,
                Applied = false,
                EffectiveReduction = false,
                ReductionMode = options.ReductionMode,
                Reason = !options.Enabled || options.ReductionMode == ReductionMode.Off
                    ? "Disabled."
                    : "No reduction was necessary.",
            };
        }

        public string ChartKey { get; internal set; } = string.Empty;
        public bool Applied { get; internal set; }
        public bool EffectiveReduction { get; internal set; }
        public bool UsedFallback { get; internal set; }
        public bool FailClosed { get; internal set; }
        public string FailureCategory { get; internal set; } = string.Empty;
        public string Reason { get; internal set; } = string.Empty;
        public int OriginalScoringObjects { get; internal set; }
        public int FilteredScoringObjects { get; internal set; }
        public int RemovedItems { get; internal set; }
        public int OriginalMaxScore { get; internal set; }
        public int FilteredMaxScore { get; internal set; }
        public double OriginalPeakNps { get; internal set; }
        public double FilteredPeakNps { get; internal set; }
        public float WindowSeconds { get; internal set; } = 1f;
        public float PlaybackSpeed { get; internal set; } = 1f;
        public ReductionMode ReductionMode { get; internal set; }
        public long Generation { get; internal set; }

        internal ReductionOutcome Copy()
        {
            return new ReductionOutcome
            {
                ChartKey = ChartKey,
                Applied = Applied,
                EffectiveReduction = EffectiveReduction,
                UsedFallback = UsedFallback,
                FailClosed = FailClosed,
                FailureCategory = FailureCategory,
                Reason = Reason,
                OriginalScoringObjects = OriginalScoringObjects,
                FilteredScoringObjects = FilteredScoringObjects,
                RemovedItems = RemovedItems,
                OriginalMaxScore = OriginalMaxScore,
                FilteredMaxScore = FilteredMaxScore,
                OriginalPeakNps = OriginalPeakNps,
                FilteredPeakNps = FilteredPeakNps,
                WindowSeconds = WindowSeconds,
                PlaybackSpeed = PlaybackSpeed,
                ReductionMode = ReductionMode,
                Generation = Generation,
            };
        }
    }

    internal sealed class ReductionApplication
    {
        public ReductionApplication(IReadonlyBeatmapData data, ReductionOutcome outcome)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            Outcome = outcome ?? throw new ArgumentNullException(nameof(outcome));
        }

        public IReadonlyBeatmapData Data { get; }
        public ReductionOutcome Outcome { get; }
    }

    internal sealed class EndpointValidationException : InvalidOperationException
    {
        public EndpointValidationException(string message) : base(message) { }
    }

    internal sealed class ReductionTransformException : InvalidOperationException
    {
        public ReductionTransformException(string category, string message, Exception inner)
            : base("[" + category + "] " + message, inner)
        {
            Category = category;
        }

        public string Category { get; }
    }
}
