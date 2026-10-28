using System;

namespace NoteMotionControl.Tests
{
    internal static class SpeedPolicyTests
    {
        internal static void RunAll()
        {
            foreach (Options option in new[] { new Options(), new Options { Enabled = true },
                new Options { Enabled = true, NjsMode = NjsMode.Multiplier, SpeedMultiplier = 1f } })
                Check(!SpeedPolicy.TryCreate(option, 20f, -8f, .5f, .5f, false, out _), "Identity routes must remain native.");

            var settings = new Options { Enabled = true, NjsMode = NjsMode.Override, OverrideNjs = 12f };
            var cap = Make(settings, 20f, -12f);
            Near(cap.Speed(8f), 8f, "An easier original must stay slow.");
            Near(cap.Speed(12f), 12f, "An equal original is unchanged.");
            Near(cap.Speed(20f), 12f, "A harder original is capped.");
            // Samples through the interior, rather than endpoint-only checks,
            // catch replacing a native ease with interpolation of capped ends.
            foreach (float raw in new[] { 8f, 9.2f, 11.8f, 12.1f, 16f, 20f })
                Near(cap.Speed(raw), Math.Min(raw, 12f), "Cap the evaluated curve.");
            settings.OverrideNjs = 25f;
            Near(cap.Speed(20f), 12f, "Running songs must use an immutable snapshot.");
            settings.ApplicationPolicy = ApplicationPolicy.Strict;
            settings.OverrideNjs = 12f;
            var strict = Make(settings);
            foreach (float raw in new[] { 8f, 12f, 20f }) Near(strict.Speed(raw), 12f, "Strict mode must be constant.");

            foreach (ApplicationPolicy application in new[] { ApplicationPolicy.OnlyReduce, ApplicationPolicy.Strict })
            foreach (float factor in new[] { .1f, .65f, 1.5f, 5f })
            {
                var scaled = Make(new Options { Enabled = true, NjsMode = NjsMode.Multiplier,
                    SpeedMultiplier = factor, ApplicationPolicy = application });
                Near(scaled.Speed(8f), 8f * (application == ApplicationPolicy.OnlyReduce ? Math.Min(1f, factor) : factor), "Multiplier policy.");
            }

            var reading = new Options { Enabled = true, ReactionTimeMode = ReactionTimeMode.Specified, ReactionTimeMs = 800f };
            var dynamic = Make(reading, 20f, -12f, .5f, .5f, false);
            Near(dynamic.Half(20f, 20f), .8f, "Extend a shorter native reaction time.");
            Near(dynamic.Half(8f, 8f), 1.25f, "Retain a longer native reaction time.");
            Near(dynamic.SpawnAhead, 1.75f, "Spawn horizon must include future slow events.");
            reading.ApplicationPolicy = ApplicationPolicy.Strict;
            var fixedReading = Make(reading, 20f, -12f, .5f, .5f, false);
            Near(fixedReading.Half(8f, 8f), .8f, "Strict reaction time overrides a longer native value.");
            Near(fixedReading.SpawnAhead, 1.3f, "Strict horizon uses the requested duration.");
            foreach (float playbackRate in new[] { .5f, 1f, 1.5f })
                Near(fixedReading.Half(20f, 20f) / playbackRate, .8f / playbackRate, "Reaction time follows chart seconds.");

            reading.ApplicationPolicy = ApplicationPolicy.OnlyReduce;
            var nativeFixed = Make(reading, 20f, -12f, 1f, 1f, true);
            Near(nativeFixed.Half(8f, 8f), 1f, "Native fixed duration does not stretch with NJS.");
            var extendedBySpeed = Make(new Options { Enabled = true, NjsMode = NjsMode.Override, OverrideNjs = 12f,
                ReactionTimeMode = ReactionTimeMode.Specified, ReactionTimeMs = 800f }, 20f, 0f, .5f, 1f, false);
            Near(extendedBySpeed.Half(20f, 12f), 1f, "Only-reduce also retains extra reading time from slower native movement.");
            var variableCap = Make(new Options { Enabled = true, NjsMode = NjsMode.Override, OverrideNjs = 12f },
                14.5f, -6f, 1f, 1f, false);
            Near(variableCap.Half(8.5f, 8.5f), 14.5f / 8.5f,
                "An uncapped slow section must retain its original reaction time in game timing mode.");
            var easier = Make(new Options { Enabled = true, NjsMode = NjsMode.Override, OverrideNjs = 25f });
            Check(!easier.ChangesAt(20f) && !easier.ChangesAt(12f), "Identity caps must not acquire a gate or force a native route.");
            foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, 0f, -1f })
                Check(!SpeedPolicy.TryCreate(settings, invalid, 0f, .5f, .5f, false, out _), "Reject unsupported speed sources.");
            Check(!SpeedPolicy.TryCreate(settings, 20f, float.NaN, .5f, .5f, false, out _), "Reject invalid horizon sources.");
            Console.WriteLine("Speed cap, strict speed, easing, timing, practice scale and horizon tests: PASS");
        }

        private static SpeedPolicy Make(Options options, float initial = 20f, float minimum = -8f,
            float originalHalf = .5f, float outputHalf = .5f, bool fixedDuration = false)
        {
            if (!SpeedPolicy.TryCreate(options, initial, minimum, originalHalf, outputHalf, fixedDuration, out SpeedPolicy policy))
                throw new InvalidOperationException("A valid policy was not created.");
            return policy;
        }
        private static void Near(float actual, float expected, string message) => Check(Math.Abs(actual - expected) < .00001f, message);
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
