using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NoteMotionControl
{
    /// <summary>
    /// Thread-safe global settings store for Note Training Control.
    /// </summary>
    public sealed class SettingsStore
    {
        internal const int SchemaVersion = 2;
        internal const string GameBuild = "1.45.2_28848";
        internal const string PluginFileName = "NoteMotionControl.json";

        private static readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings
        {
            MissingMemberHandling = MissingMemberHandling.Error,
            TypeNameHandling = TypeNameHandling.None,
            MaxDepth = 24,
            Formatting = Formatting.Indented,
        };

        private readonly object gate = new object();
        private readonly string path;
        private readonly string temporaryRoot;
        private readonly Action<string, Exception> report;
        private Options current;

        public SettingsStore(string gameRoot = null, Action<string, Exception> report = null)
        {
            string root = Path.GetFullPath(gameRoot ?? Environment.CurrentDirectory);
            path = Path.Combine(root, "UserData", PluginFileName);
            temporaryRoot = Path.Combine(root, ".tmp", "training-merge", "config");
            this.report = report;
            current = new Options();
            current.Validate();

            Exception migrationError = null;
            bool migrationSucceeded = OptionsMigration.Migrate(root, (category, error) =>
            {
                migrationError = error;
                Report(category, error);
            });
            if (!migrationSucceeded)
            {
                LastError = ReadError("Settings migration failed", migrationError);
                return;
            }

            Load();
        }

        public string LastError { get; private set; }

        public Options Global
        {
            get
            {
                lock (gate) return current.Copy();
            }
        }

        public void SaveGlobal(Options settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            Options candidate = Copy(settings);
            candidate.Validate();
            JObject document = CreateEnvelope(candidate);
            string temporary = null;
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    Directory.CreateDirectory(temporaryRoot);
                    temporary = Path.Combine(
                        temporaryRoot,
                        Path.GetFileNameWithoutExtension(path) + "-" + Guid.NewGuid().ToString("N") + ".json");
                    File.WriteAllText(temporary, document.ToString(Formatting.Indented), new UTF8Encoding(false));
                    ReplaceAtomically(temporary, path);
                    temporary = null;
                    current = candidate;
                    LastError = null;
                }
            }
            catch (Exception error)
            {
                LastError = ReadError("Settings could not be saved", error);
                Report("configuration-write", error);
                throw;
            }
            finally
            {
                if (!string.IsNullOrEmpty(temporary) && File.Exists(temporary))
                {
                    try { File.Delete(temporary); }
                    catch (Exception cleanupError) { Report("configuration-cleanup", cleanupError); }
                }
            }
        }

        public static Options Copy(Options value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            return value.Copy();
        }

        internal static JObject CreateEnvelope(Options value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            value.Validate();
            JObject settings = JObject.FromObject(value, JsonSerializer.Create(SerializerSettings));
            return new JObject
            {
                ["SchemaVersion"] = SchemaVersion,
                ["GameBuild"] = GameBuild,
                ["Settings"] = settings,
            };
        }

        internal static Options ParseEnvelope(JObject document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            RequireEnvelopeShape(document);
            JObject settings = (JObject)document["Settings"];
            Options loaded;
            try
            {
                loaded = JsonConvert.DeserializeObject<Options>(
                    settings.ToString(Formatting.None),
                    SerializerSettings);
            }
            catch (Exception error)
            {
                throw new InvalidDataException("Invalid settings values", error);
            }
            if (loaded == null) throw new InvalidDataException("Settings are missing");
            loaded.Validate();
            return loaded;
        }

        internal static void RequireEnvelopeShape(JObject document)
        {
            foreach (JProperty property in document.Properties())
            {
                if (!string.Equals(property.Name, "SchemaVersion", StringComparison.Ordinal) &&
                    !string.Equals(property.Name, "GameBuild", StringComparison.Ordinal) &&
                    !string.Equals(property.Name, "Settings", StringComparison.Ordinal))
                    throw new InvalidDataException("Unknown settings envelope field: " + property.Name);
            }

            if (document["SchemaVersion"] == null || document["SchemaVersion"].Type != JTokenType.Integer ||
                document["SchemaVersion"].Value<int>() != SchemaVersion)
                throw new InvalidDataException("Unsupported settings schema");
            if (document["GameBuild"] == null || document["GameBuild"].Type != JTokenType.String ||
                !string.Equals((string)document["GameBuild"], GameBuild, StringComparison.Ordinal))
                throw new InvalidDataException("Unsupported game build");
            if (!(document["Settings"] is JObject))
                throw new InvalidDataException("Settings must be an object");
        }

        internal static JObject ParseJson(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            return JObject.Parse(text, new JsonLoadSettings
            {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
            });
        }

        private void Load()
        {
            if (!File.Exists(path)) return;
            try
            {
                if (new FileInfo(path).Length > 4 * 1024 * 1024)
                    throw new InvalidDataException("Settings file is too large");
                Options loaded = ParseEnvelope(ParseJson(File.ReadAllText(path)));
                lock (gate) current = loaded;
                LastError = null;
            }
            catch (Exception error)
            {
                LastError = ReadError("Settings could not be loaded; defaults are active", error);
                Report("configuration-read", error);
            }
        }

        private void ReplaceAtomically(string temporary, string destination)
        {
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        }

        private void Report(string category, Exception error)
        {
            report?.Invoke(category, error);
        }

        private static string ReadError(string prefix, Exception error)
        {
            return prefix + ". " + (error == null ? "Unknown error." : error.Message);
        }
    }
}
