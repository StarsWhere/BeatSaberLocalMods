using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NoteMotionControl
{
    /// <summary>
    /// Merges the two pre-unification stores into the single v2 global file.
    /// Only the old Global objects are read; chart overrides are deliberately
    /// discarded without being inspected.
    /// </summary>
    public static class OptionsMigration
    {
        private const int MaximumSettingsFileBytes = 4 * 1024 * 1024;

        public static bool Migrate(string gameRoot = null, Action<string, Exception> report = null)
        {
            string root = Path.GetFullPath(gameRoot ?? Environment.CurrentDirectory);
            string notePath = Path.Combine(root, "UserData", SettingsStore.PluginFileName);
            string densityPath = Path.Combine(root, "UserData", "DensityReducer.json");
            bool hasNote = File.Exists(notePath);
            bool hasDensity = File.Exists(densityPath);
            if (!hasNote && !hasDensity) return true;

            string temporary = null;
            try
            {
                JObject note = hasNote ? ReadDocument(notePath) : null;
                JObject density = hasDensity ? ReadDocument(densityPath) : null;

                Options merged;
                if (note != null && IsCurrent(note))
                {
                    // A previous run already committed the unified file. A
                    // leftover density file is still validated before removal
                    // so corrupt source bytes remain recoverable.
                    SettingsStore.ParseEnvelope(note);
                    if (density != null) ReadLegacyDensity(density);
                    merged = null;
                }
                else
                {
                    LegacyMotion motion = note == null ? LegacyMotion.Default : ReadLegacyMotion(note);
                    LegacyDensity reducer = density == null ? LegacyDensity.Default : ReadLegacyDensity(density);
                    merged = Merge(motion, reducer);

                    string temporaryRoot = Path.Combine(root, ".tmp", "training-merge", "config");
                    Directory.CreateDirectory(Path.GetDirectoryName(notePath));
                    Directory.CreateDirectory(temporaryRoot);
                    temporary = Path.Combine(
                        temporaryRoot,
                        Path.GetFileNameWithoutExtension(notePath) + "-migrate-" + Guid.NewGuid().ToString("N") + ".json");
                    File.WriteAllText(
                        temporary,
                        SettingsStore.CreateEnvelope(merged).ToString(Formatting.Indented),
                        new UTF8Encoding(false));
                    ReplaceAtomically(temporary, notePath);
                    temporary = null;
                }

                // This is intentionally after the successful write (or after
                // validation of an already-current file).
                if (density != null) File.Delete(densityPath);
                return true;
            }
            catch (Exception error)
            {
                report?.Invoke("configuration-migration", error);
                return false;
            }
            finally
            {
                if (!string.IsNullOrEmpty(temporary) && File.Exists(temporary))
                {
                    try { File.Delete(temporary); }
                    catch (Exception cleanupError) { report?.Invoke("configuration-cleanup", cleanupError); }
                }
            }
        }

        private static Options Merge(LegacyMotion motion, LegacyDensity reducer)
        {
            Options result = motion.Options.Copy();
            result.Enabled = motion.Enabled || reducer.Enabled;
            if (!motion.Enabled) result.NjsMode = NjsMode.Map;
            // ApplicationPolicy did not exist in either legacy store. Keep
            // the new safe default while preserving all legacy values that
            // have a direct representation in the unified profile.
            result.ApplicationPolicy = ApplicationPolicy.OnlyReduce;
            result.ReductionMode = !reducer.Enabled
                ? ReductionMode.Off
                : reducer.Mode == 0 ? ReductionMode.Density : ReductionMode.Percentage;
            result.MaxNps = reducer.MaxNps;
            result.SameHandMinIntervalMs = reducer.SameHandMinIntervalMs;
            result.WindowSeconds = reducer.WindowSeconds;
            result.RetainPercent = reducer.RetainPercent;
            result.Validate();
            return result;
        }

        private static LegacyMotion ReadLegacyMotion(JObject document)
        {
            JObject global = ReadLegacyGlobal(document, "NoteMotionControl");
            var result = new LegacyMotion
            {
                Options = new Options(),
            };
            foreach (JProperty property in global.Properties())
            {
                switch (property.Name)
                {
                    case "Enabled":
                        RequireType(property, JTokenType.Boolean);
                        result.Enabled = property.Value.Value<bool>();
                        result.Options.Enabled = result.Enabled;
                        break;
                    case "NjsMode":
                        result.Options.NjsMode = (NjsMode)ReadEnum(property, typeof(NjsMode));
                        break;
                    case "OverrideNjs":
                        result.Options.OverrideNjs = ReadRange(property, 0.1f, 100f);
                        break;
                    case "SpeedMultiplier":
                        result.Options.SpeedMultiplier = ReadRange(property, 0.1f, 5f);
                        break;
                    // These fields belonged to an earlier, already retired
                    // motion profile. Validate their types, then discard them.
                    case "StableNative":
                    case "StableEffects":
                    case "PositionSnap":
                    case "AllowSpeedJumps":
                    case "SmoothMapSpeed":
                        RequireType(property, JTokenType.Boolean);
                        break;
                    case "DistanceMode":
                        ReadEnumRange(property, 0, 1);
                        break;
                    case "ReadingTimeMs":
                    case "ManualDistanceMeters":
                    case "SmoothingMs":
                    case "PositionSmoothingMs":
                        RequireNumber(property);
                        break;
                    default:
                        throw new InvalidDataException("Unknown NoteMotionControl setting: " + property.Name);
                }
            }
            result.Options.Validate();
            return result;
        }

        private static LegacyDensity ReadLegacyDensity(JObject document)
        {
            JObject global = ReadLegacyGlobal(document, "DensityReducer");
            var result = new LegacyDensity
            {
                MaxNps = 4f,
                SameHandMinIntervalMs = 250f,
                WindowSeconds = 1f,
                RetainPercent = 100f,
            };
            foreach (JProperty property in global.Properties())
            {
                switch (property.Name)
                {
                    case "Enabled":
                        RequireType(property, JTokenType.Boolean);
                        result.Enabled = property.Value.Value<bool>();
                        break;
                    case "Mode":
                        result.Mode = ReadEnumRange(property, 0, 1);
                        break;
                    case "MaxNps":
                        result.MaxNps = ReadRange(property, 0.5f, 12f);
                        break;
                    case "SameHandMinIntervalMs":
                        result.SameHandMinIntervalMs = ReadRange(property, 0f, 1000f);
                        break;
                    case "WindowSeconds":
                        result.WindowSeconds = ReadRange(property, 0.05f, 3f);
                        break;
                    case "RetainPercent":
                        result.RetainPercent = ReadRange(property, 5f, 100f);
                        break;
                    default:
                        throw new InvalidDataException("Unknown DensityReducer setting: " + property.Name);
                }
            }
            return result;
        }

        private static JObject ReadLegacyGlobal(JObject document, string name)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            foreach (JProperty property in document.Properties())
            {
                if (!string.Equals(property.Name, "SchemaVersion", StringComparison.Ordinal) &&
                    !string.Equals(property.Name, "GameBuild", StringComparison.Ordinal) &&
                    !string.Equals(property.Name, "Global", StringComparison.Ordinal) &&
                    !string.Equals(property.Name, "ChartOverrides", StringComparison.Ordinal))
                    throw new InvalidDataException("Unknown " + name + " envelope field: " + property.Name);
            }
            if (document["SchemaVersion"] == null || document["SchemaVersion"].Type != JTokenType.Integer ||
                document["SchemaVersion"].Value<int>() != 1)
                throw new InvalidDataException("Unsupported " + name + " schema");
            if (document["GameBuild"] == null || document["GameBuild"].Type != JTokenType.String ||
                !string.Equals((string)document["GameBuild"], SettingsStore.GameBuild, StringComparison.Ordinal))
                throw new InvalidDataException("Unsupported " + name + " game build");
            if (!(document["Global"] is JObject))
                throw new InvalidDataException("Missing " + name + " global settings");
            if (document["ChartOverrides"] != null && !(document["ChartOverrides"] is JObject))
                throw new InvalidDataException("Invalid " + name + " chart overrides");
            return (JObject)document["Global"];
        }

        private static JObject ReadDocument(string path)
        {
            if (new FileInfo(path).Length > MaximumSettingsFileBytes)
                throw new InvalidDataException("Settings file is too large");
            return SettingsStore.ParseJson(File.ReadAllText(path));
        }

        private static bool IsCurrent(JObject document)
        {
            return document["SchemaVersion"] != null &&
                document["SchemaVersion"].Type == JTokenType.Integer &&
                document["SchemaVersion"].Value<int>() == SettingsStore.SchemaVersion;
        }

        private static void RequireType(JProperty property, JTokenType type)
        {
            if (property == null || property.Value == null || property.Value.Type != type)
                throw new InvalidDataException("Invalid setting type: " + (property == null ? string.Empty : property.Name));
        }

        private static void RequireNumber(JProperty property)
        {
            if (property == null || property.Value == null ||
                (property.Value.Type != JTokenType.Integer && property.Value.Type != JTokenType.Float))
                throw new InvalidDataException("Invalid setting type: " + (property == null ? string.Empty : property.Name));
            float value;
            try { value = property.Value.Value<float>(); }
            catch (Exception error) { throw new InvalidDataException("Invalid setting number: " + property.Name, error); }
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new InvalidDataException("Invalid setting number: " + property.Name);
        }

        private static int ReadEnum(JProperty property, Type enumType)
        {
            RequireType(property, JTokenType.Integer);
            int value;
            try { value = property.Value.Value<int>(); }
            catch (Exception error) { throw new InvalidDataException("Invalid setting enum: " + property.Name, error); }
            if (!Enum.IsDefined(enumType, value))
                throw new InvalidDataException("Invalid setting enum: " + property.Name);
            return value;
        }

        private static int ReadEnumRange(JProperty property, int minimum, int maximum)
        {
            RequireType(property, JTokenType.Integer);
            int value;
            try { value = property.Value.Value<int>(); }
            catch (Exception error) { throw new InvalidDataException("Invalid setting enum: " + property.Name, error); }
            if (value < minimum || value > maximum)
                throw new InvalidDataException("Invalid setting enum: " + property.Name);
            return value;
        }

        private static float ReadRange(JProperty property, float minimum, float maximum)
        {
            RequireNumber(property);
            float value = property.Value.Value<float>();
            if (value < minimum || value > maximum)
                throw new InvalidDataException("Invalid setting range: " + property.Name);
            return value;
        }

        private static void ReplaceAtomically(string temporary, string destination)
        {
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        }

        private sealed class LegacyMotion
        {
            internal static readonly LegacyMotion Default = new LegacyMotion
            {
                Options = new Options(),
                Enabled = false,
            };

            internal Options Options;
            internal bool Enabled;
        }

        private sealed class LegacyDensity
        {
            internal static readonly LegacyDensity Default = new LegacyDensity
            {
                Enabled = false,
                Mode = 0,
                MaxNps = 4f,
                SameHandMinIntervalMs = 250f,
                WindowSeconds = 1f,
                RetainPercent = 100f,
            };

            internal bool Enabled;
            internal int Mode;
            internal float MaxNps;
            internal float SameHandMinIntervalMs;
            internal float WindowSeconds;
            internal float RetainPercent;
        }
    }
}
