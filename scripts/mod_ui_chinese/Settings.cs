using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Settings;
using LocalModUI;
using LocalTraining;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SiraUtil.Zenject;
using UnityEngine;
using Zenject;

namespace ModUiChinese
{
    internal sealed class LocalizationOptions : IValidatedSettings
    {
        public bool Enabled = true;
        public Dictionary<string, bool> Mods = new Dictionary<string, bool>(StringComparer.Ordinal);
        public void Validate()
        {
            if (Mods == null || Mods.Count > 1024) throw new InvalidDataException("Invalid mod language settings");
            foreach (var entry in Mods)
                if (String.IsNullOrWhiteSpace(entry.Key) || entry.Key.Length > 256) throw new InvalidDataException("Invalid mod identity");
        }
    }
    internal static class LocalizationState
    {
        internal static ProfileStore<LocalizationOptions> Store;
        internal static LocalizationOptions ActiveOptions;
        internal static Dictionary<string, Dictionary<string, string>> Custom = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        internal static string Error;
        internal static bool Enabled { get { return ActiveOptions == null || ActiveOptions.Enabled; } }
        internal static List<KeyValuePair<string, string>> InstalledPlugins()
        {
            return IPA.Loader.PluginManager.EnabledPlugins
                .Select(plugin => new KeyValuePair<string, string>(plugin.Id, plugin.Assembly == null ? null : plugin.Assembly.GetName().Name))
                .ToList();
        }
        internal static string CustomPath { get { return Path.Combine(Environment.CurrentDirectory, "UserData", "ModUiChinese", "overrides.json"); } }
        internal static void Initialize(IPA.Logging.Logger logger)
        {
            Store = new ProfileStore<LocalizationOptions>("ModUiChinese", (category, error) => logger.Error(category + ": " + error));
            ActiveOptions = Store.Global;
            Error = Store.LastError;
            Reload(false);
        }
        internal static TranslationEngine CreateEngine()
        {
            var catalog = GeneratedCatalog.Create();
            foreach (var scope in Custom) {
                Dictionary<string, string> builtin;
                var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (catalog.TryGetValue(scope.Key, out builtin))
                    foreach (var entry in builtin.OrderBy(x => x.Key, StringComparer.Ordinal))
                        if (!table.ContainsKey(entry.Key)) table.Add(entry.Key, entry.Value);
                foreach (var entry in scope.Value) table[entry.Key] = entry.Value;
                catalog[scope.Key] = table;
            }
            return new TranslationEngine(catalog, ActiveOptions.Mods.Where(x => !x.Value).Select(x => x.Key), ActiveOptions.Enabled);
        }
        internal static Dictionary<string, Dictionary<string, string>> ParseCustom(string content)
        {
            JObject root;
            try { root = JObject.Parse(content, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }); }
            catch (JsonException error) { throw new InvalidDataException("Invalid custom translation JSON: " + error.Message, error); }
            if (root.Properties().Any(x => x.Name != "SchemaVersion" && x.Name != "Translations")
                || root["SchemaVersion"] == null || root["SchemaVersion"].Type != JTokenType.Integer
                || (int?)root["SchemaVersion"] != 1 || !(root["Translations"] is JObject))
                throw new InvalidDataException("Unsupported custom translation schema");
            var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var scope in ((JObject)root["Translations"]).Properties()) {
                if (String.IsNullOrWhiteSpace(scope.Name) || scope.Name.Length > 256 || !(scope.Value is JObject))
                    throw new InvalidDataException("Invalid translation scope: " + scope.Name);
                var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in ((JObject)scope.Value).Properties()) {
                    if (entry.Value.Type != JTokenType.String) throw new InvalidDataException(scope.Name + ": " + entry.Name + " must have a string translation");
                    string target = (string)entry.Value;
                    ValidateTranslation(entry.Name, target, scope.Name);
                    if (table.ContainsKey(entry.Name)) throw new InvalidDataException("Duplicate translation: " + scope.Name + ": " + entry.Name);
                    table.Add(entry.Name, target);
                }
                result.Add(scope.Name, table);
            }
            return result;
        }
        static void ValidateTranslation(string source, string target, string scope)
        {
            if (String.IsNullOrEmpty(source) || String.IsNullOrWhiteSpace(target) || source.Length > 16384 || target.Length > 16384)
                throw new InvalidDataException("Empty or oversized translation: " + scope + ": " + source);
            Func<string, string, string[]> tokens = (text, pattern) => Regex.Matches(text, pattern).Cast<Match>().Select(x => x.Value).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (!tokens(source, @"\{[^{}]+\}").SequenceEqual(tokens(target, @"\{[^{}]+\}")))
                throw new InvalidDataException("Changed placeholders: " + scope + ": " + source);
            if (!Regex.Matches(source, "<[^>]+>").Cast<Match>().Select(x => x.Value).SequenceEqual(Regex.Matches(target, "<[^>]+>").Cast<Match>().Select(x => x.Value)))
                throw new InvalidDataException("Changed rich text tags: " + scope + ": " + source);
            try { TranslationEngine.ValidateTemplate(source, target); }
            catch (FormatException error) { throw new InvalidDataException("Invalid translation format: " + scope + ": " + source, error); }
        }
        internal static bool Reload(bool refresh)
        {
            try {
                Dictionary<string, Dictionary<string, string>> candidate;
                if (!File.Exists(CustomPath)) candidate = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                else {
                    if (new FileInfo(CustomPath).Length > 2 * 1024 * 1024) throw new InvalidDataException("Custom translations exceed 2 MiB");
                    candidate = ParseCustom(File.ReadAllText(CustomPath));
                }
                // Construct first: an invalid catalog cannot replace the last usable one.
                var previous = Custom;
                Custom = candidate;
                try { CreateEngine(); } catch { Custom = previous; throw; }
                Error = Store.LastError;
                if (refresh) UiTranslator.Reload();
                return true;
            } catch (Exception error) {
                Error = "Custom translations could not be loaded: " + error.Message;
                UiTranslator.Log.Error(Error + " [" + CustomPath + "]");
                return false;
            }
        }
    }
    internal sealed class LocalizationPanel : INotifyPropertyChanged
    {
        string selected = "Common";
        [UIComponent("language-settings-content")] Transform content;
        public event PropertyChangedEventHandler PropertyChanged;
        void Notify(string name) { var handler = PropertyChanged; if (handler != null) handler(this, new PropertyChangedEventArgs(name)); }
        [UIAction("#post-parse")]
        void Parsed()
        {
            if (content != null) content.gameObject.AddComponent<LocalizationSettingsView>().Initialize(this);
            Refresh();
        }
        public void Refresh()
        {
            if (!ScopeChoices.Contains(selected)) selected = "Common";
            foreach (string name in new[] { "Enabled", "ScopeChoices", "SelectedScope", "ScopeEnabled", "Status", "HasStatus", "CustomFile" }) Notify(name);
        }
        public bool Enabled { get { return LocalizationState.Enabled; } set { Change(x => x.Enabled = value); } }
        public List<object> ScopeChoices { get { return TranslationEngine.InstalledScopes(LocalizationState.InstalledPlugins(),
            GeneratedCatalog.Create().Keys.Concat(LocalizationState.Custom.Keys)).Select(scope => (object)scope).ToList(); } }
        public string SelectedScope { get { return selected; } set { selected = value; Notify("ScopeEnabled"); } }
        public bool ScopeEnabled { get { bool enabled; return !LocalizationState.Store.Global.Mods.TryGetValue(selected, out enabled) || enabled; }
            set { Change(x => x.Mods[selected] = value); } }
        public string Status { get { return LocalizationState.Error ?? ""; } }
        public bool HasStatus { get { return !String.IsNullOrEmpty(Status); } }
        public string CustomFile { get { return "UserData/ModUiChinese/overrides.json"; } }
        public string FormatScope(object value)
        {
            string id = value as string ?? "";
            switch (id) {
                case "NoteMotionControl": return "Note Training Control";
                case "ActivityEnergyCounter": return "Activity Energy Counter";
                case "ModUiChinese": return "Mod UI Language";
                case "RoomCalibration": return "Room Calibration";
                case "AdvancedSongFilters": return "Advanced Song Filters";
                case "PlaylistManager": return "Playlist Manager";
                case "CustomSabersLite": return "Custom Sabers Lite";
                case "BetterSongSearch": return "Better Song Search";
                case "BeatSage": return "Beat Sage";
                case "GameFeedback": return "Gameplay feedback";
                default: return id;
            }
        }
        void Change(Action<LocalizationOptions> update)
        {
            try {
                var options = LocalizationState.Store.Global;
                update(options);
                LocalizationState.Store.SaveGlobal(options);
                LocalizationState.ActiveOptions = options;
                LocalizationState.Error = null;
                UiTranslator.Reload();
            } catch (Exception error) { LocalizationState.Error = "Language settings could not be saved: " + error.Message; }
            Notify("Enabled"); Notify("ScopeEnabled"); Notify("Status"); Notify("HasStatus");
        }
        public void ReloadCustom() { LocalizationState.Reload(true); Refresh(); }
    }
    public sealed class LocalizationSettingsView : MonoBehaviour
    {
        LocalizationPanel panel;
        internal void Initialize(LocalizationPanel value) { panel = value; if (isActiveAndEnabled) panel.Refresh(); }
        void OnEnable() { panel?.Refresh(); }
        void OnDestroy() { panel = null; }
    }
    public sealed class LocalizationMenuInstaller : Installer
    {
        public override void InstallBindings() { Container.BindInterfacesTo<LocalizationMenu>().AsSingle(); }
    }
    internal sealed class LocalizationMenu : IInitializable, IDisposable
    {
        readonly BSMLSettings settings;
        readonly LocalizationPanel panel = new LocalizationPanel();
        IDisposable uiEntry;
        public LocalizationMenu(BSMLSettings settings) { this.settings = settings; }
        public void Initialize()
        {
            bool nativeAttempted = false, flowAttempted = false;
            try {
                uiEntry = UiRegistry.Register(new UiEntry {
                    Id = "language", Title = "Mod UI Language", Category = UiCategory.Devices, Order = 30,
                    Page = new SettingsPage {
                        Host = panel, Assembly = typeof(LocalizationMenu).Assembly,
                        Resource = "ModUiChinese.Settings.bsml", Refresh = panel.Refresh,
                    },
                });
                nativeAttempted = true;
                settings.AddSettingsMenu("Mod UI Language", "ModUiChinese.Settings.bsml", panel);
                flowAttempted = true;
                InstantSettingsFlow.Register(panel, error => UiTranslator.Log.Error("Language settings navigation failed: " + error));
            }
            catch {
                try { if (flowAttempted) InstantSettingsFlow.Unregister(panel); }
                catch (Exception error) { UiTranslator.Log.Error("Language navigation cleanup failed: " + error); }
                try { if (nativeAttempted) settings.RemoveSettingsMenu(panel); }
                catch (Exception error) { UiTranslator.Log.Error("Language menu cleanup failed: " + error); }
                try { uiEntry?.Dispose(); uiEntry = null; }
                catch (Exception error) { UiTranslator.Log.Error("Language registration cleanup failed: " + error); }
                throw;
            }
        }
        public void Dispose()
        {
            uiEntry?.Dispose(); uiEntry = null;
            InstantSettingsFlow.Unregister(panel);
            settings.RemoveSettingsMenu(panel);
        }
    }
}
