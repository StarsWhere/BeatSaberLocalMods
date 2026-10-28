using System;
using System.Collections.Generic;

namespace NoteMotionControl
{
    public enum PresetKind
    {
        Original = 0,
        Entry = 1,
        Easy = 2,
        Normal = 3,
        Hard = 4,
        Half = 5,
        Stable = 6,
        Early = 7,
    }

    public sealed class PresetDefinition
    {
        internal PresetDefinition(PresetKind kind, string name, Options options)
        {
            Kind = kind;
            Name = name;
            this.options = options;
        }

        public PresetKind Kind { get; }
        public string Name { get; }
        public Options Options => options.Copy();

        private readonly Options options;

        public Options CreateOptions()
        {
            return Options.Copy();
        }
    }

    /// <summary>
    /// Built-in profiles. There is no persisted preset id: selecting one
    /// writes its complete Options value, and later edits become Custom.
    /// </summary>
    public static class Presets
    {
        public const string Custom = "Custom";
        private static readonly PresetDefinition[] Definitions = BuildDefinitions();

        public static IReadOnlyList<PresetDefinition> All => Definitions;

        public static IReadOnlyList<string> Names
        {
            get
            {
                var names = new List<string>(Definitions.Length + 1);
                foreach (PresetDefinition definition in Definitions) names.Add(definition.Name);
                names.Add(Custom);
                return names;
            }
        }

        public static Options Original => Create(PresetKind.Original);
        public static Options Entry => Create(PresetKind.Entry);
        public static Options Easy => Create(PresetKind.Easy);
        public static Options Normal => Create(PresetKind.Normal);
        public static Options Hard => Create(PresetKind.Hard);
        public static Options Half => Create(PresetKind.Half);
        public static Options Stable => Create(PresetKind.Stable);
        public static Options Early => Create(PresetKind.Early);

        public static Options Create(PresetKind kind)
        {
            foreach (PresetDefinition definition in Definitions)
                if (definition.Kind == kind) return definition.CreateOptions();
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        public static bool TryCreate(string name, out Options options)
        {
            PresetDefinition definition = Find(name);
            if (definition == null)
            {
                options = null;
                return false;
            }
            options = definition.CreateOptions();
            return true;
        }

        public static Options Create(string name)
        {
            if (!TryCreate(name, out Options options))
                throw new ArgumentException("Unknown preset: " + name, nameof(name));
            return options;
        }

        public static string GetName(Options options)
        {
            if (options == null) return Custom;
            foreach (PresetDefinition definition in Definitions)
                if (Equal(options, definition.Options)) return definition.Name;
            return Custom;
        }

        internal static bool Equal(Options left, Options right)
        {
            if (left == null || right == null) return ReferenceEquals(left, right);
            return left.Enabled == right.Enabled &&
                left.ApplicationPolicy == right.ApplicationPolicy &&
                left.NjsMode == right.NjsMode &&
                left.OverrideNjs == right.OverrideNjs &&
                left.SpeedMultiplier == right.SpeedMultiplier &&
                left.ReductionMode == right.ReductionMode &&
                left.MaxNps == right.MaxNps &&
                left.SameHandMinIntervalMs == right.SameHandMinIntervalMs &&
                left.WindowSeconds == right.WindowSeconds &&
                left.RetainPercent == right.RetainPercent &&
                left.ReactionTimeMode == right.ReactionTimeMode &&
                left.ReactionTimeMs == right.ReactionTimeMs &&
                left.HidePreJump == right.HidePreJump &&
                left.VerticalJump == right.VerticalJump &&
                left.HorizontalSwap == right.HorizontalSwap &&
                left.SpawnRotation == right.SpawnRotation &&
                left.RotateTowardsPlayer == right.RotateTowardsPlayer;
        }

        private static PresetDefinition Find(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            foreach (PresetDefinition definition in Definitions)
                if (string.Equals(definition.Name, name, StringComparison.OrdinalIgnoreCase))
                    return definition;
            return null;
        }

        private static PresetDefinition[] BuildDefinitions()
        {
            var original = new Options();
            var entry = StableOptions(10f, 2f, 500f, 1200f);
            var easy = StableOptions(12f, 3f, 300f, 1000f);
            var normal = StableOptions(16f, 5f, 200f, 800f);
            var hard = StableOptions(20f, 8f, 125f, 600f);
            var half = StableBase();
            half.Enabled = true;
            half.NjsMode = NjsMode.Map;
            half.ReductionMode = ReductionMode.Percentage;
            half.RetainPercent = 50f;
            half.ReactionTimeMode = ReactionTimeMode.Specified;
            half.ReactionTimeMs = 800f;

            var stable = StableBase();
            stable.Enabled = true;
            stable.NjsMode = NjsMode.Map;
            stable.ReductionMode = ReductionMode.Off;
            stable.ReactionTimeMode = ReactionTimeMode.Specified;
            stable.ReactionTimeMs = 800f;

            var early = new Options();
            early.Enabled = true;
            early.NjsMode = NjsMode.Map;
            early.ReductionMode = ReductionMode.Off;
            early.ReactionTimeMode = ReactionTimeMode.Specified;
            early.ReactionTimeMs = 1000f;

            return new[]
            {
                new PresetDefinition(PresetKind.Original, "Original", original),
                new PresetDefinition(PresetKind.Entry, "Entry", entry),
                new PresetDefinition(PresetKind.Easy, "Easy", easy),
                new PresetDefinition(PresetKind.Normal, "Normal", normal),
                new PresetDefinition(PresetKind.Hard, "Hard", hard),
                new PresetDefinition(PresetKind.Half, "Half", half),
                new PresetDefinition(PresetKind.Stable, "Stable", stable),
                new PresetDefinition(PresetKind.Early, "Early", early),
            };
        }

        private static Options StableOptions(float overrideNjs, float maxNps, float interval, float reaction)
        {
            var options = StableBase();
            options.Enabled = true;
            options.ApplicationPolicy = ApplicationPolicy.OnlyReduce;
            options.NjsMode = NjsMode.Override;
            options.OverrideNjs = overrideNjs;
            options.ReductionMode = ReductionMode.Density;
            options.MaxNps = maxNps;
            options.SameHandMinIntervalMs = interval;
            options.WindowSeconds = 1f;
            options.ReactionTimeMode = ReactionTimeMode.Specified;
            options.ReactionTimeMs = reaction;
            return options;
        }

        private static Options StableBase()
        {
            var options = new Options();
            options.HidePreJump = true;
            options.VerticalJump = false;
            options.HorizontalSwap = false;
            options.SpawnRotation = false;
            options.RotateTowardsPlayer = false;
            return options;
        }
    }
}
