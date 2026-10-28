using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LocalTraining
{
    internal interface IValidatedSettings { void Validate(); }

    internal static class ChartKey
    {
        internal static string For(BeatmapKey key) { return key.SerializedName(); }
    }

    // Compiled into each independent plugin from this one canonical source.
    internal sealed class ProfileStore<T> where T : class, IValidatedSettings, new()
    {
        sealed class Document
        {
            public int SchemaVersion = 1;
            public string GameBuild = "1.45.2_28848";
            public T Global = new T();
            public Dictionary<string, T> ChartOverrides = new Dictionary<string, T>(StringComparer.Ordinal);
        }
        static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings {
            MissingMemberHandling = MissingMemberHandling.Error, TypeNameHandling = TypeNameHandling.None,
            MaxDepth = 24, Formatting = Formatting.Indented
        };
        readonly object gate = new object();
        readonly string path, temporaryRoot;
        readonly Action<string, Exception> report;
        Document current = new Document();
        public string LastError { get; private set; }
        public T Global { get { lock (gate) return Copy(current.Global); } }
        public string[] OverrideKeys { get { lock (gate) { var keys = new string[current.ChartOverrides.Count]; current.ChartOverrides.Keys.CopyTo(keys, 0); Array.Sort(keys, StringComparer.Ordinal); return keys; } } }

        public ProfileStore(string pluginId, Action<string, Exception> report, string gameRoot = null)
        {
            if (String.IsNullOrEmpty(pluginId) || pluginId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || pluginId.Contains("..")) throw new ArgumentException("Invalid plugin identity");
            string root = Path.GetFullPath(gameRoot ?? Environment.CurrentDirectory);
            path = Path.Combine(root, "UserData", pluginId + ".json");
            temporaryRoot = Path.Combine(root, ".tmp", "training-mods", "config");
            this.report = report;
            Validate(current);
            if (!File.Exists(path)) return;
            try {
                if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Settings file is too large");
                var json = JObject.Parse(File.ReadAllText(path), new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (json.Property("SchemaVersion") == null || json.Property("GameBuild") == null
                    || json["SchemaVersion"].Type != JTokenType.Integer || json["GameBuild"].Type != JTokenType.String
                    || !(json["Global"] is JObject) || !(json["ChartOverrides"] is JObject))
                    throw new InvalidDataException("Missing settings schema fields");
                var loaded = JsonConvert.DeserializeObject<Document>(json.ToString(), JsonSettings);
                Validate(loaded);
                current = loaded;
            } catch (Exception error) {
                LastError = "Settings could not be loaded; defaults are active. " + error.Message;
                if (report != null) report("configuration-read", error);
            }
        }
        static void Validate(Document document)
        {
            if (document == null || document.SchemaVersion != 1 || document.GameBuild != "1.45.2_28848"
                || document.Global == null || document.ChartOverrides == null)
                throw new InvalidDataException("Unsupported settings schema or game build");
            document.Global.Validate();
            foreach (var entry in document.ChartOverrides) {
                if (String.IsNullOrWhiteSpace(entry.Key) || entry.Key.Length > 1024 || entry.Value == null)
                    throw new InvalidDataException("Invalid chart override");
                entry.Value.Validate();
            }
        }
        public static T Copy(T value)
        {
            if (value == null) throw new ArgumentNullException("value");
            return JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value, JsonSettings), JsonSettings);
        }
        public bool HasOverride(string chartKey) { lock (gate) return chartKey != null && current.ChartOverrides.ContainsKey(chartKey); }
        public T Resolve(string chartKey)
        {
            lock (gate) { T value; return Copy(chartKey != null && current.ChartOverrides.TryGetValue(chartKey, out value) ? value : current.Global); }
        }
        Document Candidate()
        {
            return JsonConvert.DeserializeObject<Document>(JsonConvert.SerializeObject(current, JsonSettings), JsonSettings);
        }
        public void SaveGlobal(T settings)
        {
            lock (gate) { var candidate = Candidate(); candidate.Global = Copy(settings); Commit(candidate); }
        }
        public void SaveOverride(string chartKey, T settings)
        {
            if (String.IsNullOrWhiteSpace(chartKey)) throw new ArgumentException("Select a chart first");
            lock (gate) { var candidate = Candidate(); candidate.ChartOverrides[chartKey] = Copy(settings); Commit(candidate); }
        }
        public void RemoveOverride(string chartKey)
        {
            if (String.IsNullOrWhiteSpace(chartKey)) throw new ArgumentException("Select a chart first");
            lock (gate) { var candidate = Candidate(); candidate.ChartOverrides.Remove(chartKey); Commit(candidate); }
        }
        void Commit(Document candidate)
        {
            Validate(candidate);
            string temporary = null;
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                Directory.CreateDirectory(temporaryRoot);
                temporary = Path.Combine(temporaryRoot, Path.GetFileNameWithoutExtension(path) + "-" + Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(temporary, JsonConvert.SerializeObject(candidate, JsonSettings), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                current = candidate;
                LastError = null;
            } catch (Exception error) {
                LastError = "Settings could not be saved. " + error.Message;
                if (report != null) report("configuration-write", error);
                throw;
            } finally { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
