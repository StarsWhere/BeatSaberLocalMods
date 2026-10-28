using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System.Xml.Linq;

namespace Camera2UrpFix
{
    // Camera2's own objects and converters remain the persistence contract.
    internal static class SettingsContract
    {
        static readonly Dictionary<string, MemberInfo> Members = new Dictionary<string, MemberInfo>();
        internal static Type CameraType;
        internal static Type SceneType;
        internal static JsonSerializerSettings DeserializeSettings;
        static readonly Dictionary<string, float[]> SliderRanges = new Dictionary<string, float[]>();

        internal static void Resolve()
        {
            CameraType = AccessTools.TypeByName("Camera2.Configuration.CameraSettings");
            SceneType = AccessTools.TypeByName("Camera2.Configuration.ScenesSettings");
            if (CameraType == null || SceneType == null)
                throw new TypeLoadException("Camera2 settings contract is unavailable");
            DeserializeSettings = new JsonSerializerSettings {
                ContractResolver = new DefaultContractResolver(), NullValueHandling = NullValueHandling.Ignore,
                MissingMemberHandling = MissingMemberHandling.Error
            };
            SliderRanges.Clear();
            using (var stream = CameraType.Assembly.GetManifestResourceStream("Camera2.UI.Views.camSettings.bsml"))
            {
                if (stream == null) throw new MissingMemberException("Camera2 settings markup is unavailable");
                foreach (var slider in XDocument.Load(stream).Descendants("slider-setting"))
                    SliderRanges[(string)slider.Attribute("value")] = new[] {
                        Single.Parse((string)slider.Attribute("min"), CultureInfo.InvariantCulture),
                        Single.Parse((string)slider.Attribute("max"), CultureInfo.InvariantCulture)
                    };
            }
        }

        internal static object Get(object target, string name)
        {
            if (target == null) return null;
            var type = target as Type ?? target.GetType();
            var member = Find(type, name);
            var instance = target is Type ? null : target;
            var property = member as PropertyInfo;
            return property == null ? ((FieldInfo)member).GetValue(instance) : property.GetValue(instance, null);
        }

        internal static void Set(object target, string name, object value)
        {
            var type = target as Type ?? target.GetType();
            var member = Find(type, name);
            var instance = target is Type ? null : target;
            var property = member as PropertyInfo;
            var valueType = property == null ? ((FieldInfo)member).FieldType : property.PropertyType;
            if (value is string && valueType.IsEnum) value = Enum.Parse(valueType, (string)value, false);
            if (property == null) ((FieldInfo)member).SetValue(instance, value);
            else property.SetValue(instance, value, null);
        }

        static MemberInfo Find(Type type, string name)
        {
            var key = type.FullName + ":" + name;
            MemberInfo member;
            if (Members.TryGetValue(key, out member)) return member;
            var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            member = (MemberInfo)type.GetProperty(name, flags) ?? type.GetField(name, flags);
            if (member == null) throw new MissingMemberException(type.FullName, name);
            Members.Add(key, member);
            return member;
        }

        internal static object Invoke(object target, string name, params object[] args)
        {
            var type = target as Type ?? target.GetType();
            var method = AccessTools.Method(type, name);
            if (method == null) throw new MissingMethodException(type.FullName, name);
            try { return method.Invoke(target is Type ? null : target, args); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }

        internal static void Validate(string json, bool camera)
        {
            var document = JObject.Parse(json);
            // Walk Camera2's serialization contract instead of invoking its
            // camera-mutating setters. Leaf values use its native converters.
            var serializer = JsonSerializer.Create(DeserializeSettings);
            ValidateNode(document, camera ? CameraType : SceneType, null, serializer);
            if (camera)
            {
                foreach (var name in new[] { "FOV", "renderScale" })
                {
                    var value = document[name];
                    if (value == null || (value.Type != JTokenType.Float && value.Type != JTokenType.Integer))
                        throw new JsonSerializationException("Missing numeric camera setting: " + name);
                    var raw = value.Value<float>();
                    var range = SliderRanges[name];
                    if (Single.IsNaN(raw) || Single.IsInfinity(raw) || raw < range[0] || raw > range[1])
                        throw new JsonSerializationException("Camera setting outside its native range: " + name);
                }
                var samples = document["antiAliasing"].Value<int>();
                if (samples != 1 && samples != 2 && samples != 4 && samples != 8)
                    throw new JsonSerializationException("Unsupported anti-aliasing sample count");
                var rect = document["viewRect"] as JObject;
                if (rect == null) throw new JsonSerializationException("Missing camera view rectangle");
                foreach (var name in new[] { "x", "y", "width", "height" })
                {
                    var value = rect[name].Value<float>();
                    if (Single.IsNaN(value) || Single.IsInfinity(value) || value < 0f || value > 1f ||
                        ((name == "width" || name == "height") && value <= 0f))
                        throw new JsonSerializationException("Invalid camera view rectangle: " + name);
                }
            }
            else
            {
                foreach (var scene in ((JObject)document["scenes"]).Properties())
                {
                    ValidateCameraNames(scene.Value);
                }
                var custom = (JObject)document["customScenes"];
                foreach (var scene in custom.Properties()) ValidateCameraNames(scene.Value);
                foreach (var binding in ((JObject)document["customSceneBindings"]).Properties())
                {
                    if (custom.Property(binding.Value.Value<string>()) == null) throw new JsonSerializationException("Custom scene binding has no scene");
                }
            }
        }

        static void ValidateNode(JToken token, Type type, JsonConverter converter, JsonSerializer serializer)
        {
            if (token == null || token.Type == JTokenType.Null) throw new JsonSerializationException("Null Camera2 setting: " + (token == null ? "root" : token.Path));
            var contract = serializer.ContractResolver.ResolveContract(type);
            converter = converter ?? contract.Converter;
            if (converter != null && converter.CanRead)
            {
                using (var reader = token.CreateReader())
                {
                    reader.Read();
                    ValidateLeaf(converter.ReadJson(reader, type, null, serializer));
                }
                return;
            }
            var dictionary = contract as JsonDictionaryContract;
            if (dictionary != null)
            {
                var values = token as JObject;
                if (values == null) throw new JsonSerializationException("Expected settings dictionary");
                foreach (var entry in values.Properties())
                {
                    if (dictionary.DictionaryKeyType.IsEnum) RequireEnum(Enum.Parse(dictionary.DictionaryKeyType, entry.Name, false));
                    ValidateNode(entry.Value, dictionary.DictionaryValueType, null, serializer);
                }
                return;
            }
            var array = contract as JsonArrayContract;
            if (array != null)
            {
                if (!(token is JArray)) throw new JsonSerializationException("Expected settings array");
                foreach (var value in (JArray)token) ValidateNode(value, array.CollectionItemType, null, serializer);
                return;
            }
            var obj = contract as JsonObjectContract;
            if (obj != null)
            {
                var values = token as JObject;
                if (values == null) throw new JsonSerializationException("Expected settings object");
                foreach (var entry in values.Properties())
                {
                    var property = obj.Properties.GetClosestMatchProperty(entry.Name);
                    if (property == null || property.Ignored) throw new JsonSerializationException("Unknown Camera2 setting: " + entry.Name);
                    ValidateNode(entry.Value, property.PropertyType, property.Converter, serializer);
                }
                return;
            }
            ValidateLeaf(token.ToObject(type, serializer));
        }

        static void ValidateLeaf(object value)
        {
            if (value == null) throw new JsonSerializationException("Null converted setting");
            if (value.GetType().IsEnum) RequireEnum(value);
            if (value is float && (Single.IsNaN((float)value) || Single.IsInfinity((float)value)))
                throw new JsonSerializationException("Non-finite camera setting");
        }

        static void RequireEnum(object value)
        {
            if (value == null || !Enum.IsDefined(value.GetType(), value))
                throw new JsonSerializationException("Unknown Camera2 enum value");
        }

        static void ValidateCameraNames(object value)
        {
            if (value == null) throw new JsonSerializationException("Scene camera list is null");
            foreach (var name in (JArray)value)
                if (String.IsNullOrWhiteSpace(name.Value<string>())) throw new JsonSerializationException("Empty scene camera name");
        }
    }

    internal static class AtomicConfiguration
    {
        internal static string SerializeCamera(object model)
        {
            string json = null;
            SettingsContract.Invoke(model, "Unoverriden", new Action(delegate {
                json = JsonConvert.SerializeObject(model, Formatting.Indented);
            }));
            return json;
        }

        sealed class Failure
        {
            internal object Model;
            internal string PendingFile;
        }

        static readonly Dictionary<string, Failure> Failures = new Dictionary<string, Failure>(StringComparer.OrdinalIgnoreCase);
        internal static string LastStatus = String.Empty;
        internal static float StatusUntil;
        internal static bool HasFailures { get { return Failures.Count != 0; } }

        internal static void Install(Harmony harmony)
        {
            SettingsContract.Resolve();
            harmony.Patch(AccessTools.Method(SettingsContract.CameraType, "Save"),
                prefix: new HarmonyMethod(typeof(AtomicConfiguration), "SavePrefix"));
            harmony.Patch(AccessTools.Method(SettingsContract.SceneType, "Save"),
                prefix: new HarmonyMethod(typeof(AtomicConfiguration), "SavePrefix"));
        }

        public static bool SavePrefix(object __instance)
        {
            Save(__instance);
            return false;
        }

        internal static bool Save(object model)
        {
            var camera = model != null && model.GetType() == SettingsContract.CameraType;
            if (model == null) return true;
            var cam = camera ? SettingsContract.Get(model, "cam") : null;
            if (camera && cam == null) return true;
            if (!camera && !(bool)SettingsContract.Get(model, "wasLoaded")) return true;
            var path = camera ? (string)SettingsContract.Get(cam, "configPath") :
                (string)SettingsContract.Get(AccessTools.TypeByName("Camera2.Utils.ConfigUtil"), "ScenesCfg");
            var pending = String.Empty;
            try
            {
                path = Path.GetFullPath(path);
                string json;
                if (camera)
                {
                    // Load saves new cameras before its own default rectangle
                    // initialization. Initialize that same native rectangle
                    // before validating/persisting the first config.
                    if (SettingsContract.Get(model, "viewRect") == null)
                    {
                        var rectangle = AccessTools.Property(SettingsContract.CameraType, "viewRect").PropertyType;
                        SettingsContract.Set(model, "viewRect", Activator.CreateInstance(rectangle,
                            new object[] { 0f, 0f, 1f, 1f, false }));
                    }
                    json = SerializeCamera(model);
                }
                else json = JsonConvert.SerializeObject(model, Formatting.Indented);
                SettingsContract.Validate(json, camera);
                var root = Path.GetFullPath((string)SettingsContract.Get(
                    AccessTools.TypeByName("Camera2.Utils.ConfigUtil"), "ConfigDir")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Camera config is outside Camera2's config directory");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                pending = path + ".pending." + Guid.NewGuid().ToString("N");
                using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(json);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                SettingsContract.Validate(File.ReadAllText(pending), camera);
                if (File.Exists(path)) File.Replace(pending, path, null);
                else File.Move(pending, path);
                ClearFailure(path);
                if (!HasFailures)
                {
                    LastStatus = "Saved";
                    StatusUntil = UnityEngine.Time.realtimeSinceStartup + 2.5f;
                }
                return true;
            }
            catch (Exception error)
            {
                Failure previous;
                if (Failures.TryGetValue(path, out previous) && !String.IsNullOrEmpty(pending) && File.Exists(pending))
                    DeletePending(previous.PendingFile);
                Failures[path] = new Failure { Model = model, PendingFile = File.Exists(pending) ? pending :
                    previous == null ? null : previous.PendingFile };
                LastStatus = "Could not save settings";
                StatusUntil = Single.PositiveInfinity;
                var category = error is JsonSerializationException || error is JsonReaderException ? "config_invalid" : "config_write_failed";
                Runtime.LogError("Camera2 " + category + ": " + path +
                    (File.Exists(pending) ? "; recoverable candidate=" + pending : String.Empty), error);
                return false;
            }
        }

        internal static bool Retry()
        {
            var success = true;
            foreach (var failure in Failures.Values.ToArray()) success = Save(failure.Model) && success;
            return success;
        }

        internal static void ForgetCamera(object cam)
        {
            foreach (var item in Failures.Where(value => value.Value.Model.GetType() == SettingsContract.CameraType &&
                ReferenceEquals(SettingsContract.Get(value.Value.Model, "cam"), cam)).ToArray()) ClearFailure(item.Key);
        }

        static void ClearFailure(string path)
        {
            Failure value;
            if (!Failures.TryGetValue(path, out value)) return;
            DeletePending(value.PendingFile);
            Failures.Remove(path);
        }

        static void DeletePending(string path)
        {
            if (String.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try { File.Delete(path); }
            catch (Exception error) { Runtime.LogError("Camera2 pending config cleanup failed: " + path, error); }
        }

        internal static void Release()
        {
            // Failed atomic replacements remain alongside their target as
            // recoverable candidates. Never delete them during process exit.
            Failures.Clear();
        }
    }
}
