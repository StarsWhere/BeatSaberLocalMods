using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AdvancedSongFilters
{
    public sealed class NotesCache
    {
        public int SchemaVersion = 1;
        public string GameBuild = "1.45.2_28848";
        public int ParserVersion = 1;
        public Dictionary<string, int> Counts = new Dictionary<string, int>(StringComparer.Ordinal);

        public void Validate()
        {
            if (SchemaVersion != 1 || GameBuild != "1.45.2_28848" || ParserVersion != 1 || Counts == null)
                throw new InvalidDataException("Unsupported statistics cache schema or game build");
            foreach (var entry in Counts)
                if (String.IsNullOrEmpty(entry.Key) || entry.Key.IndexOf('|') < 0 || entry.Value < 0)
                    throw new InvalidDataException("Invalid chart identity or note count in cache");
        }
    }

    public sealed class Persistence
    {
        readonly string root;
        readonly Action<string, Exception> report;
        static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings {
            MissingMemberHandling = MissingMemberHandling.Error,
            TypeNameHandling = TypeNameHandling.None,
            MaxDepth = 16
        };
        public string Warning { get; private set; }
        public Persistence(string gameRoot, Action<string, Exception> reportError)
        {
            root = Path.GetFullPath(gameRoot);
            report = reportError;
        }
        public FilterSettings LoadSettings()
        {
            try {
                var path = Path.Combine(root, "UserData", "AdvancedSongFilters.json");
                if (!File.Exists(path)) {
                    Warning = null;
                    return new FilterSettings();
                }
                var value = Read<FilterSettings>(path, 65536);
                value = value.Normalize();
                Warning = null;
                return value;
            } catch (Exception ex) {
                Warning = "Could not read filters; defaults are active";
                report("configuration-read", ex);
                return new FilterSettings();
            }
        }
        public NotesCache LoadCache()
        {
            try {
                var path = Path.Combine(root, "UserData", "AdvancedSongFilters.notes.json");
                if (!File.Exists(path)) return new NotesCache();
                var value = Read<NotesCache>(path, 64 * 1024 * 1024);
                value.Validate();
                return value;
            } catch (Exception ex) {
                report("statistics-cache-read", ex);
                return new NotesCache();
            }
        }
        static T Read<T>(string path, long maxBytes) where T : class
        {
            if (new FileInfo(path).Length > maxBytes) throw new InvalidDataException("File exceeds schema size limit");
            var text = File.ReadAllText(path);
            var json = JObject.Parse(text);
            if (json["SchemaVersion"] == null) throw new InvalidDataException("Missing schema version");
            var value = JsonConvert.DeserializeObject<T>(text, JsonSettings);
            if (value == null) throw new InvalidDataException("Missing document");
            return value;
        }
        public void SaveSettings(FilterSettings value)
        {
            var error = value.Validate();
            if (error != null) throw new InvalidDataException(error);
            Write("AdvancedSongFilters.json", value);
            Warning = null;
        }
        public void SaveCache(NotesCache value)
        {
            value.Validate();
            Write("AdvancedSongFilters.notes.json", value);
        }
        public PresetCollection LoadPresets()
        {
            var path = Path.Combine(root, "UserData", "AdvancedSongFilters.presets.json");
            if (!File.Exists(path)) return new PresetCollection();
            var value = Read<PresetCollection>(path, 4 * 1024 * 1024);
            value.Validate();
            return value;
        }
        public void SavePresets(PresetCollection value)
        {
            if (value == null) throw new ArgumentNullException("value");
            value.Validate();
            Write("AdvancedSongFilters.presets.json", value);
        }
        void Write(string name, object value)
        {
            var directory = Path.Combine(root, "UserData");
            Directory.CreateDirectory(directory);
            var staging = Path.Combine(root, ".tmp", "advanced-song-filters-v020", "persistence");
            Directory.CreateDirectory(staging);
            var temporary = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".json");
            var destination = Path.Combine(directory, name);
            try {
                File.WriteAllText(temporary, JsonConvert.SerializeObject(value, Formatting.Indented, JsonSettings));
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            } finally {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
