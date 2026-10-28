using System;
using System.Collections.Generic;

namespace NoteMotionControl.Tests
{
    internal static class PresetsTests
    {
        internal static void RunAll()
        {
            CheckOriginal();
            CheckEntryTable();
            CheckVariants();
            CheckCustomStatus();
        }

        private static void CheckOriginal()
        {
            Options value = Presets.Original;
            Check(!value.Enabled && value.NjsMode == NjsMode.Map && value.ReductionMode == ReductionMode.Off,
                "Original must be the disabled native profile");
            Check(Presets.GetName(value) == "Original", "Original must identify by name");
        }

        private static void CheckEntryTable()
        {
            CheckStable(Presets.Entry, 10f, 2f, 500f, 1200f);
            CheckStable(Presets.Easy, 12f, 3f, 300f, 1000f);
            CheckStable(Presets.Normal, 16f, 5f, 200f, 800f);
            CheckStable(Presets.Hard, 20f, 8f, 125f, 600f);
        }

        private static void CheckVariants()
        {
            Options half = Presets.Half;
            Check(half.Enabled && half.NjsMode == NjsMode.Map && half.ReductionMode == ReductionMode.Percentage &&
                half.RetainPercent == 50f && half.ReactionTimeMode == ReactionTimeMode.Specified &&
                half.ReactionTimeMs == 800f, "Half numerical combination must match the table");
            CheckStableMovement(half);
            Check(half.MaxNps == 4f && half.SameHandMinIntervalMs == 250f && half.WindowSeconds == 1f,
                "Half must reset unused density parameters");

            Options stable = Presets.Stable;
            Check(stable.Enabled && stable.NjsMode == NjsMode.Map && stable.ReductionMode == ReductionMode.Off &&
                stable.ReactionTimeMode == ReactionTimeMode.Specified && stable.ReactionTimeMs == 800f,
                "Stable combination must match the table");
            CheckStableMovement(stable);

            Options early = Presets.Early;
            Check(early.Enabled && early.NjsMode == NjsMode.Map && early.ReductionMode == ReductionMode.Off &&
                early.ReactionTimeMode == ReactionTimeMode.Specified && early.ReactionTimeMs == 1000f,
                "Early combination must match the table");
            Check(!early.HidePreJump && early.VerticalJump && early.HorizontalSwap &&
                early.SpawnRotation && early.RotateTowardsPlayer,
                "Early must retain native movement controls");
        }

        private static void CheckCustomStatus()
        {
            Options custom = Presets.Normal;
            custom.MaxNps = 5.5f;
            Check(Presets.GetName(custom) == Presets.Custom, "manual edits must derive Custom status");
            Check(Presets.TryCreate("easy", out Options easy) && easy.OverrideNjs == 12f,
                "preset lookup must be case insensitive");

            IReadOnlyList<PresetDefinition> all = Presets.All;
            Check(all.Count == 8, "all built-in presets must be present");
            foreach (PresetDefinition definition in all)
            {
                Options value = definition.CreateOptions();
                value.Validate();
            }
        }

        private static void CheckStable(Options value, float njs, float maxNps, float interval, float reaction)
        {
            Check(value.Enabled && value.ApplicationPolicy == ApplicationPolicy.OnlyReduce &&
                value.NjsMode == NjsMode.Override && value.OverrideNjs == njs &&
                value.ReductionMode == ReductionMode.Density && value.MaxNps == maxNps &&
                value.SameHandMinIntervalMs == interval && value.WindowSeconds == 1f &&
                value.ReactionTimeMode == ReactionTimeMode.Specified && value.ReactionTimeMs == reaction,
                "entry preset numerical table mismatch");
            CheckStableMovement(value);
            Check(value.RetainPercent == 100f && value.SpeedMultiplier == 1f,
                "entry preset unused values must be defaults");
        }

        private static void CheckStableMovement(Options value)
        {
            Check(value.HidePreJump && !value.VerticalJump && !value.HorizontalSwap &&
                !value.SpawnRotation && !value.RotateTowardsPlayer,
                "stable preset movement values mismatch");
        }

        private static void Check(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException("Note Training Control presets: " + message);
        }
    }
}
