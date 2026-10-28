using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NoteMotionControl.Tests
{
    internal static class OptionsMigrationTests
    {
        internal static void RunAll()
        {
            DefaultsAndRanges();
            StrictJsonBoundary();
            LegacyGlobalMerge();
            DensityOnlyMerge();
            DormantMotionRemainsNative();
            MigrationFailurePreservesSource();
            SaveValidationIsAtomic();
        }

        private static void DefaultsAndRanges()
        {
            var options = new Options();
            Check(!options.Enabled && options.ApplicationPolicy == ApplicationPolicy.OnlyReduce,
                "new settings must be disabled and use OnlyReduce");
            Check(options.NjsMode == NjsMode.Map && options.OverrideNjs == 12f && options.SpeedMultiplier == 1f,
                "new NJS defaults must leave the map unchanged");
            Check(options.ReductionMode == ReductionMode.Off && options.MaxNps == 4f &&
                options.SameHandMinIntervalMs == 250f && options.WindowSeconds == 1f && options.RetainPercent == 100f,
                "new reduction defaults must be native");
            Check(options.ReactionTimeMode == ReactionTimeMode.Game && options.ReactionTimeMs == 800f,
                "new reaction defaults must use game timing");
            Check(!options.HidePreJump && options.VerticalJump && options.HorizontalSwap &&
                options.SpawnRotation && options.RotateTowardsPlayer,
                "new movement defaults must be native");

            Options copy = options.Copy();
            copy.Enabled = true;
            copy.VerticalJump = false;
            Check(!options.Enabled && options.VerticalJump, "Copy must be independent");

            options.MaxNps = 12.1f;
            ExpectThrows(() => options.Validate(), "out-of-range numeric values must fail validation");
            options = new Options { OverrideNjs = float.NaN };
            ExpectThrows(() => options.Validate(), "nonfinite numeric values must fail validation");
            options = new Options { ReductionMode = (ReductionMode)99 };
            ExpectThrows(() => options.Validate(), "invalid enum values must fail validation");
        }

        private static void StrictJsonBoundary()
        {
            Options original = new Options
            {
                Enabled = true,
                ApplicationPolicy = ApplicationPolicy.Strict,
                NjsMode = NjsMode.Multiplier,
                SpeedMultiplier = 0.75f,
                ReductionMode = ReductionMode.Percentage,
                RetainPercent = 50f,
                ReactionTimeMode = ReactionTimeMode.Specified,
                ReactionTimeMs = 1000f,
                HidePreJump = true,
                VerticalJump = false,
            };
            string json = JsonConvert.SerializeObject(original);
            Options roundTrip = JsonConvert.DeserializeObject<Options>(json);
            Check(roundTrip != null && roundTrip.ApplicationPolicy == ApplicationPolicy.Strict &&
                roundTrip.ReductionMode == ReductionMode.Percentage && roundTrip.SpeedMultiplier == 0.75f,
                "strict options must round-trip");

            foreach (string invalid in new[]
            {
                "{\"Enabled\":\"true\"}",
                "{\"ApplicationPolicy\":2}",
                "{\"NjsMode\":\"Multiplier\"}",
                "{\"OverrideNjs\":\"12\"}",
                "{\"Unknown\":true}",
                "{\"OverrideNjs\":NaN}",
                "{\"Enabled\":false,\"Enabled\":true}",
            })
            {
                ExpectThrows(() => JsonConvert.DeserializeObject<Options>(invalid),
                    "strict JSON must reject: " + invalid);
            }
        }

        private static void LegacyGlobalMerge()
        {
            string root = NewRoot();
            string notePath = Path.Combine(root, "UserData", "NoteMotionControl.json");
            string densityPath = Path.Combine(root, "UserData", "DensityReducer.json");
            try
            {
                File.WriteAllText(notePath, LegacyMotionDocument(true, 1, 12f, 0.75f).ToString());
                File.WriteAllText(densityPath, LegacyDensityDocument(false, 0, 3.5f, 250f, 90f, 0.95f).ToString());
                string oldNoteBytes = File.ReadAllText(notePath);
                string oldDensityBytes = File.ReadAllText(densityPath);
                Exception report = null;
                var store = new SettingsStore(root, (category, error) => report = error);
                Options merged = store.Global;
                Check(report == null, "valid legacy merge must not report an error");
                Check(merged.Enabled && merged.ApplicationPolicy == ApplicationPolicy.OnlyReduce,
                    "legacy migration must use the new OnlyReduce default");
                Check(merged.NjsMode == NjsMode.Override && merged.OverrideNjs == 12f && merged.SpeedMultiplier == 0.75f,
                    "legacy motion values must survive even when reducer is dormant");
                Check(merged.ReductionMode == ReductionMode.Off && merged.MaxNps == 3.5f &&
                    merged.SameHandMinIntervalMs == 250f && merged.RetainPercent == 90f && merged.WindowSeconds == 0.95f,
                    "dormant reducer values must survive while reduction remains off");
                JObject current = JObject.Parse(File.ReadAllText(notePath));
                Check((int)current["SchemaVersion"] == 2 && current["Settings"] is JObject &&
                    current["Global"] == null && current["ChartOverrides"] == null,
                    "migration must write the unified v2 envelope");
                Check(!File.Exists(densityPath), "density source must be removed only after successful merge");

                string migratedBytes = File.ReadAllText(notePath);
                OptionsMigration.Migrate(root, (category, error) => report = error);
                Check(File.ReadAllText(notePath) == migratedBytes, "migration must be idempotent");
                Check(oldNoteBytes != migratedBytes && oldDensityBytes.Length > 0,
                    "migration must replace the old note file and consume the density source");
            }
            finally { DeleteRoot(root); }
        }

        private static void DensityOnlyMerge()
        {
            string root = NewRoot();
            string densityPath = Path.Combine(root, "UserData", "DensityReducer.json");
            try
            {
                File.WriteAllText(densityPath, LegacyDensityDocument(true, 1, 3.5f, 250f, 90f, 0.95f).ToString());
                var store = new SettingsStore(root);
                Options merged = store.Global;
                Check(merged.Enabled && merged.ApplicationPolicy == ApplicationPolicy.OnlyReduce &&
                    merged.ReductionMode == ReductionMode.Percentage && merged.RetainPercent == 90f,
                    "density-only migration must enable percentage reduction without strict motion");
                Check(merged.NjsMode == NjsMode.Map && merged.OverrideNjs == 12f && merged.SpeedMultiplier == 1f,
                    "density-only migration must retain native motion defaults");
                Check(!File.Exists(densityPath), "density-only source must be removed after writing NMC");
            }
            finally { DeleteRoot(root); }
        }

        private static void DormantMotionRemainsNative()
        {
            string root = NewRoot();
            try
            {
                File.WriteAllText(Path.Combine(root, "UserData", "NoteMotionControl.json"),
                    LegacyMotionDocument(false, 1, 22f, 0.75f).ToString());
                File.WriteAllText(Path.Combine(root, "UserData", "DensityReducer.json"),
                    LegacyDensityDocument(true, 0, 3.5f, 250f, 90f, 0.95f).ToString());
                var store = new SettingsStore(root);
                Options merged = store.Global;
                Check(merged.Enabled && merged.ReductionMode == ReductionMode.Density && merged.NjsMode == NjsMode.Map,
                    "enabling the merged reducer must not activate a previously disabled speed override");
                Check(merged.OverrideNjs == 22f && merged.SpeedMultiplier == 0.75f,
                    "dormant speed values must remain available for later editing");
            }
            finally { DeleteRoot(root); }
        }

        private static void MigrationFailurePreservesSource()
        {
            string root = NewRoot();
            string notePath = Path.Combine(root, "UserData", "NoteMotionControl.json");
            string densityPath = Path.Combine(root, "UserData", "DensityReducer.json");
            try
            {
                const string invalid = "{ invalid JSON }";
                File.WriteAllText(notePath, invalid);
                File.WriteAllText(densityPath, LegacyDensityDocument(true, 0, 3.5f, 250f, 90f, 0.95f).ToString());
                Exception report = null;
                var store = new SettingsStore(root, (category, error) => report = error);
                Check(report != null && store.LastError != null, "corrupt migration source must report an error");
                Check(File.ReadAllText(notePath) == invalid && File.Exists(densityPath),
                    "failed migration must preserve both source files");
                Check(!Directory.GetFiles(Path.Combine(root, ".tmp", "training-merge", "config")).Any(),
                    "failed migration must clean temporary files");
            }
            finally { DeleteRoot(root); }
        }

        private static void SaveValidationIsAtomic()
        {
            string root = NewRoot();
            string notePath = Path.Combine(root, "UserData", "NoteMotionControl.json");
            try
            {
                var store = new SettingsStore(root);
                Options valid = store.Global;
                store.SaveGlobal(valid);
                string before = File.ReadAllText(notePath);
                Options invalid = valid.Copy();
                invalid.RetainPercent = 4f;
                ExpectThrows(() => store.SaveGlobal(invalid), "invalid saves must fail");
                Check(File.ReadAllText(notePath) == before && store.Global.RetainPercent == valid.RetainPercent,
                    "invalid saves must leave bytes and the in-memory value unchanged");
                Check(!Directory.GetFiles(Path.Combine(root, ".tmp", "training-merge", "config")).Any(),
                    "failed saves must clean temporary files");
            }
            finally { DeleteRoot(root); }
        }

        private static JObject LegacyMotionDocument(bool enabled, int njsMode, float overrideNjs, float multiplier)
        {
            return new JObject
            {
                ["SchemaVersion"] = 1,
                ["GameBuild"] = "1.45.2_28848",
                ["Global"] = new JObject
                {
                    ["Enabled"] = enabled,
                    ["NjsMode"] = njsMode,
                    ["OverrideNjs"] = overrideNjs,
                    ["SpeedMultiplier"] = multiplier,
                },
                ["ChartOverrides"] = new JObject
                {
                    ["discarded"] = new JObject { ["Unknown"] = true },
                },
            };
        }

        private static JObject LegacyDensityDocument(bool enabled, int mode, float maxNps, float interval, float retain, float window)
        {
            return new JObject
            {
                ["SchemaVersion"] = 1,
                ["GameBuild"] = "1.45.2_28848",
                ["Global"] = new JObject
                {
                    ["Enabled"] = enabled,
                    ["Mode"] = mode,
                    ["MaxNps"] = maxNps,
                    ["SameHandMinIntervalMs"] = interval,
                    ["RetainPercent"] = retain,
                    ["WindowSeconds"] = window,
                },
                ["ChartOverrides"] = new JObject { ["discarded"] = "ignore me" },
            };
        }

        private static string NewRoot()
        {
            string parent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".tmp", "training-merge", "config-tests"));
            string root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "UserData"));
            Directory.CreateDirectory(Path.Combine(root, ".tmp", "training-merge", "config"));
            return root;
        }

        private static void DeleteRoot(string root)
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            string parent = Path.GetDirectoryName(root);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent) &&
                !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
        }

        private static void ExpectThrows(Action action, string message)
        {
            bool rejected = false;
            try { action(); }
            catch (Exception) { rejected = true; }
            Check(rejected, message);
        }

        private static void Check(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException("Note Training Control config: " + message);
        }
    }
}
