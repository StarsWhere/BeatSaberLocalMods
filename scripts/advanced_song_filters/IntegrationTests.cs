using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace AdvancedSongFilters
{
    public static class IntegrationTests
    {
        private const string CurrentGameBuild = "1.45.2_28848";

        public static int Main(string[] args)
        {
            string gameRoot = args != null && args.Length > 0
                ? Path.GetFullPath(args[0])
                : Directory.GetCurrentDirectory();

            string testRoot = args != null && args.Length > 1
                ? Path.GetFullPath(args[1])
                : Path.Combine(gameRoot, ".tmp", "advanced-song-filters-v020", "integration-data");
            EnsureOwnedTestRoot(testRoot, gameRoot);
            DeleteTreeIfPresent(testRoot);
            Directory.CreateDirectory(testRoot);

            try
            {
                RunPersistenceTests(testRoot);
                RunPresetTests(testRoot);
                Console.WriteLine("INTEGRATION TESTS PASSED.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("INTEGRATION TESTS FAILED: " + error);
                return 1;
            }
            finally
            {
                DeleteTreeIfPresent(testRoot);
            }
        }

        private static void RunPersistenceTests(string testRoot)
        {
            List<string> reports = new List<string>();
            Persistence store = NewPersistence(testRoot, reports);

            FilterSettings settings = new FilterSettings
            {
                Enabled = true,
                ExcludeOfficial = true,
                Mode = "OneSaber",
                Difficulties = new[] { 1, 4 }
            };
            settings.Nps.Min = 2;
            settings.Njs.Max = 18.5;
            settings.Notes.Min = 120;
            store.SaveSettings(settings);

            NotesCache cache = new NotesCache();
            cache.Counts["song-id|OneSaber|1"] = 121;
            cache.Counts["song-id|OneSaber|4"] = 245;
            store.SaveCache(cache);

            Assert(File.Exists(Path.Combine(testRoot, "UserData", "AdvancedSongFilters.json")),
                "settings file is written below the isolated test root");
            Assert(File.Exists(Path.Combine(testRoot, "UserData", "AdvancedSongFilters.notes.json")),
                "cache file is written below the isolated test root");

            FilterSettings loadedSettings = NewPersistence(testRoot, reports).LoadSettings();
            Assert(loadedSettings.Validate() == null, "settings roundtrip validates");
            Assert(loadedSettings.Enabled && loadedSettings.ExcludeOfficial && loadedSettings.Mode == "OneSaber",
                "settings roundtrip keeps scalar fields");
            Assert(loadedSettings.Difficulties.Length == 2 && loadedSettings.Difficulties[1] == 4,
                "settings roundtrip keeps selected difficulties");
            Assert(loadedSettings.Nps.Min == 2 && loadedSettings.Njs.Max == 18.5
                && loadedSettings.Notes.Min == 120,
                "settings roundtrip keeps numeric ranges");

            NotesCache loadedCache = NewPersistence(testRoot, reports).LoadCache();
            Assert(loadedCache.GameBuild == CurrentGameBuild && loadedCache.ParserVersion == 1,
                "cache roundtrip keeps schema metadata");
            Assert(loadedCache.Counts.Count == 2 && loadedCache.Counts["song-id|OneSaber|4"] == 245,
                "cache roundtrip keeps chart counts");
            Assert(reports.Count == 0, "valid persistence roundtrips do not report warnings");

            string settingsPath = Path.Combine(testRoot, "UserData", "AdvancedSongFilters.json");
            string cachePath = Path.Combine(testRoot, "UserData", "AdvancedSongFilters.notes.json");

            File.WriteAllText(settingsPath, "{\"Enabled\":true}");
            reports.Clear();
            FilterSettings missingVersionSettings = NewPersistence(testRoot, reports).LoadSettings();
            Assert(missingVersionSettings.Validate() == null && !missingVersionSettings.Enabled,
                "missing settings schema version restores defaults");
            Assert(HasReport(reports, "configuration-read"),
                "missing settings schema version reports configuration-read");

            FilterSettings invalidRange = new FilterSettings();
            invalidRange.Nps.Min = -1;
            File.WriteAllText(settingsPath, JsonConvert.SerializeObject(invalidRange));
            reports.Clear();
            FilterSettings recoveredSettings = NewPersistence(testRoot, reports).LoadSettings();
            Assert(recoveredSettings.Validate() == null && !recoveredSettings.Enabled,
                "invalid settings range restores defaults");
            Assert(HasReport(reports, "configuration-read"),
                "invalid settings range reports configuration-read");

            FilterSettings invalidVersion = new FilterSettings { SchemaVersion = 2 };
            File.WriteAllText(settingsPath, JsonConvert.SerializeObject(invalidVersion));
            reports.Clear();
            Persistence warningStore = NewPersistence(testRoot, reports);
            FilterSettings versionRecoveredSettings = warningStore.LoadSettings();
            Assert(versionRecoveredSettings.Validate() == null && !versionRecoveredSettings.Enabled,
                "unsupported settings schema restores defaults");
            Assert(HasReport(reports, "configuration-read"),
                "unsupported settings schema reports configuration-read");
            Assert(!String.IsNullOrEmpty(warningStore.Warning),
                "unsupported settings schema sets a visible warning");
            warningStore.SaveSettings(settings);
            Assert(String.IsNullOrEmpty(warningStore.Warning),
                "successful SaveSettings clears a previous warning");

            NotesCache invalidCacheVersion = new NotesCache { SchemaVersion = 2 };
            File.WriteAllText(cachePath, JsonConvert.SerializeObject(invalidCacheVersion));
            reports.Clear();
            NotesCache versionRecoveredCache = NewPersistence(testRoot, reports).LoadCache();
            AssertValidEmptyCache(versionRecoveredCache, "unsupported cache schema restores an empty cache");
            Assert(HasReport(reports, "statistics-cache-read"),
                "unsupported cache schema reports statistics-cache-read");

            NotesCache invalidBuild = new NotesCache { GameBuild = "other-build" };
            File.WriteAllText(cachePath, JsonConvert.SerializeObject(invalidBuild));
            reports.Clear();
            NotesCache buildRecoveredCache = NewPersistence(testRoot, reports).LoadCache();
            AssertValidEmptyCache(buildRecoveredCache, "cache from another game build is rejected");
            Assert(HasReport(reports, "statistics-cache-read"),
                "cache from another game build reports statistics-cache-read");

            NotesCache negativeCache = new NotesCache();
            negativeCache.Counts["song-id|Standard|1"] = -1;
            bool saveRejected = false;
            try
            {
                store.SaveCache(negativeCache);
            }
            catch (InvalidDataException)
            {
                saveRejected = true;
            }
            Assert(saveRejected, "negative cache count is rejected before save");

            File.WriteAllText(cachePath, JsonConvert.SerializeObject(negativeCache));
            reports.Clear();
            NotesCache negativeRecoveredCache = NewPersistence(testRoot, reports).LoadCache();
            AssertValidEmptyCache(negativeRecoveredCache, "negative cache count restores an empty cache");
            Assert(HasReport(reports, "statistics-cache-read"),
                "negative cache count reports statistics-cache-read");

            NotesCache oldKey = new NotesCache();
            oldKey.Counts["same-song|Standard|1"] = 17;
            store.SaveCache(oldKey);
            NotesCache oldKeyLoaded = NewPersistence(testRoot, reports).LoadCache();
            Assert(oldKeyLoaded.Counts.ContainsKey("same-song|Standard|1"),
                "original cache key is available after save");

            NotesCache changedKey = new NotesCache();
            changedKey.Counts["same-song|Standard|2"] = 29;
            store.SaveCache(changedKey);
            NotesCache changedKeyLoaded = NewPersistence(testRoot, reports).LoadCache();
            Assert(!changedKeyLoaded.Counts.ContainsKey("same-song|Standard|1")
                && changedKeyLoaded.Counts.ContainsKey("same-song|Standard|2")
                && changedKeyLoaded.Counts["same-song|Standard|2"] == 29,
                "changing a chart cache key cannot reuse the old count");

            Console.WriteLine("PERSISTENCE PASS: isolated settings/cache roundtrip, schema/build/range/count rejection, and cache-key replacement.");
        }

        private static void RunPresetTests(string testRoot)
        {
            List<string> reports = new List<string>();
            Persistence store = NewPersistence(testRoot, reports);
            string path = Path.Combine(testRoot, "UserData", "AdvancedSongFilters.presets.json");

            PresetCollection collection = new PresetCollection();
            collection.Presets.Add(new FilterPreset
            {
                Id = Guid.NewGuid().ToString("D"),
                Name = "方案 1",
                Settings = new FilterSettings()
            });
            collection.Presets.Add(new FilterPreset
            {
                Id = Guid.NewGuid().ToString("D"),
                Name = "Fast",
                Settings = new FilterSettings { Mode = "Degree90", Enabled = true, ExcludeOfficial = true }
            });
            store.SavePresets(collection);
            byte[] original = File.ReadAllBytes(path);

            PresetCollection loaded = NewPersistence(testRoot, reports).LoadPresets();
            loaded.Validate();
            Assert(loaded.Presets.Count == 2 && loaded.Presets[1].Name == "Fast"
                && loaded.Presets[1].Settings.ExcludeOfficial,
                "preset collection roundtrip preserves entries");
            Assert(loaded.NextName() == "Preset 1" && loaded.Presets[0].Name == "方案 1", "English defaults preserve existing Chinese preset names");

            File.WriteAllText(path, "{broken json");
            bool corruptRejected = false;
            try { NewPersistence(testRoot, reports).LoadPresets(); }
            catch (Exception) { corruptRejected = true; }
            Assert(corruptRejected && File.ReadAllText(path) == "{broken json",
                "corrupt preset data is rejected without overwrite");

            File.WriteAllText(path, "{\"SchemaVersion\":2,\"Presets\":[]}");
            bool versionRejected = false;
            try { NewPersistence(testRoot, reports).LoadPresets(); }
            catch (InvalidDataException) { versionRejected = true; }
            Assert(versionRejected, "unsupported preset schema is rejected");

            File.WriteAllBytes(path, original);
            PresetCollection duplicate = NewPersistence(testRoot, reports).LoadPresets();
            duplicate.Presets[1].Name = "方案 1";
            bool saveRejected = false;
            try { store.SavePresets(duplicate); }
            catch (InvalidDataException) { saveRejected = true; }
            byte[] afterRejectedSave = File.ReadAllBytes(path);
            Assert(saveRejected && ByteEqual(original, afterRejectedSave),
                "invalid duplicate presets are rejected before overwriting");

            FilterSettings invalid = new FilterSettings { SchemaVersion = 2 };
            File.WriteAllText(Path.Combine(testRoot, "UserData", "AdvancedSongFilters.json"),
                JsonConvert.SerializeObject(invalid));
            Persistence warningStore = NewPersistence(testRoot, reports);
            warningStore.LoadSettings();
            Assert(!String.IsNullOrEmpty(warningStore.Warning), "invalid settings set a warning");
            FilterSettings valid = new FilterSettings();
            File.WriteAllText(Path.Combine(testRoot, "UserData", "AdvancedSongFilters.json"),
                JsonConvert.SerializeObject(valid));
            FilterSettings reloaded = warningStore.LoadSettings();
            Assert(reloaded.Validate() == null && String.IsNullOrEmpty(warningStore.Warning),
                "successful settings load clears a stale warning");

            Console.WriteLine("PRESET PASS: roundtrip, naming, corruption/version rejection, duplicate validation, and warning recovery.");
        }

        private static Persistence NewPersistence(string testRoot, List<string> reports)
        {
            return new Persistence(testRoot, delegate(string category, Exception error)
            {
                reports.Add(category + ":" + (error == null ? String.Empty : error.GetType().Name));
            });
        }

        private static bool HasReport(List<string> reports, string category)
        {
            for (int i = 0; i < reports.Count; i++)
            {
                if (reports[i].StartsWith(category + ":", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static void EnsureOwnedTestRoot(string testRoot, string gameRoot)
        {
            string expectedParent = Path.GetFullPath(Path.Combine(gameRoot, ".tmp"));
            string candidate = Path.GetFullPath(testRoot);
            string prefix = expectedParent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            Assert(!String.Equals(expectedParent, candidate, StringComparison.OrdinalIgnoreCase)
                && candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase),
                "integration test root is outside the designated temporary directory");
        }

        private static void DeleteTreeIfPresent(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }

        private static void AssertValidEmptyCache(NotesCache cache, string message)
        {
            cache.Validate();
            Assert(cache.Counts.Count == 0, message);
        }

        private static bool ByteEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return left == right;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Assertion failed: " + message);
            }
        }
    }
}
