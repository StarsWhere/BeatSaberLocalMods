using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HarmonyLib;
using IPA;
using TMPro;
using UnityEngine;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Components;
using BeatSaberMarkupLanguage.Parser;

[assembly: AssemblyVersion("1.1.7.0")]

namespace ModUiChinese
{
    [Plugin(RuntimeOptions.DynamicInit)]
    public sealed class Plugin
    {
        Harmony harmony;
        GameObject owner;
        [Init] public void Init(IPA.Logging.Logger logger, SiraUtil.Zenject.Zenjector zenjector)
        {
            UiTranslator.Log = logger;
            LocalizationState.Initialize(logger);
            zenjector.Install<LocalizationMenuInstaller>(SiraUtil.Zenject.Location.Menu);
        }
        [OnEnable] public void Enable()
        {
            UiTranslator.ValidateFontApi();
            // The game's TMP shaders are not loaded during the initial IPA enable.
            BeatSaberMarkupLanguage.Util.MainMenuAwaiter.MainMenuInitializing += SymbolFontFallback.Enable;
            if (BeatSaberUI.MainTextFont != null) SymbolFontFallback.Enable();
            UiTranslator.Engine = LocalizationState.CreateEngine();
            UiTranslator.ConfigureCaptionScopes();
            harmony = new Harmony("local.beatsaber.mod-ui-chinese");
            var cameraScenes = AccessTools.TypeByName("Camera2.Configuration.ScenesSettings");
            if (cameraScenes != null)
                foreach (var nested in cameraScenes.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                    foreach (var callback in nested.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (callback.Name.StartsWith("<Load>b__", StringComparison.Ordinal) && callback.ReturnType == typeof(int))
                            harmony.Patch(callback, transpiler: new HarmonyMethod(typeof(UiTranslator), "CameraPopupTranspiler"));
            harmony.Patch(AccessTools.PropertySetter(typeof(TMP_Text), "text"), prefix: new HarmonyMethod(typeof(UiTranslator), "TextPrefix"));
            harmony.Patch(AccessTools.Method(typeof(TextMeshProUGUI), "OnEnable"),
                postfix: new HarmonyMethod(typeof(UiTranslator), "FormattedTextPostfix"));
            foreach (var method in typeof(TMP_Text).GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(x => x.Name == "SetText" && x.GetParameters().Length > 0 && x.GetParameters()[0].ParameterType == typeof(string)))
            {
                var parameters = method.GetParameters();
                if (parameters.Length == 1 || parameters[1].ParameterType == typeof(bool))
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(UiTranslator), "TextPrefix"));
                // The shorter numeric overloads delegate here. Translate the rendered
                // value, so restoration never replaces numbers with format placeholders.
                else if (parameters.Length == 9 && parameters.Skip(1).All(x => x.ParameterType == typeof(float)))
                    harmony.Patch(method, postfix: new HarmonyMethod(typeof(UiTranslator), "FormattedTextPostfix"));
            }
            // AddSettingsMenu uses GetCallingAssembly to locate resources. Hook
            // the constructors instead, preserving that original caller frame.
            harmony.Patch(typeof(BeatSaberMarkupLanguage.Settings.BSMLSettings).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single(),
                postfix: new HarmonyMethod(typeof(UiTranslator), "SettingsRegistryPostfix"));
            harmony.Patch(AccessTools.Constructor(AccessTools.TypeByName("BeatSaberMarkupLanguage.Settings.SettingsMenu"), new[] { typeof(string), typeof(string), typeof(object), typeof(Assembly) }),
                postfix: new HarmonyMethod(typeof(UiTranslator), "SettingsMenuPostfix"));
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName("BeatSaberMarkupLanguage.MenuButtons.MenuButtons"), "RegisterButton"),
                postfix: new HarmonyMethod(typeof(UiTranslator), "MenuButtonPostfix"));
            var progressBar = AccessTools.TypeByName("SongCore.UI.ProgressBar");
            if (progressBar != null)
                harmony.Patch(AccessTools.Method(progressBar, "AppendText"),
                    prefix: new HarmonyMethod(typeof(UiTranslator), "ProgressAppendPrefix"),
                    finalizer: new HarmonyMethod(typeof(UiTranslator), "ProgressAppendFinalizer"));
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName("MissedNoteEffectSpawner"), "HandleNoteWasMissed"),
                prefix: new HarmonyMethod(typeof(GameplayFeedback), "MissPrefix"),
                finalizer: new HarmonyMethod(typeof(GameplayFeedback), "MissFinalizer"));
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName("MissedNoteEffectSpawner"), "Start"),
                postfix: new HarmonyMethod(typeof(GameplayFeedback), "Warmup"));
            var chartView = AccessTools.TypeByName("SongChartVisualizer.UI.ViewControllers.ChartViewController");
            if (chartView != null)
                harmony.Patch(AccessTools.Method(chartView, "PrepareWarningText"),
                    postfix: new HarmonyMethod(typeof(UiTranslator), "ChartWarningPostfix"));
            var parses = typeof(BSMLParser).GetMethods().Where(x => x.Name == "Parse" && x.GetParameters().Length == 3 && x.GetParameters()[1].ParameterType == typeof(GameObject) && x.GetParameters()[2].ParameterType == typeof(object)).ToArray();
            if (parses.Length == 0) throw new MissingMethodException("BSMLParser.Parse");
            foreach (var parse in parses)
                harmony.Patch(parse, prefix: new HarmonyMethod(typeof(UiTranslator), "ParsePrefix"), postfix: new HarmonyMethod(typeof(UiTranslator), "ParsePostfix"), finalizer: new HarmonyMethod(typeof(UiTranslator), "ParseFinalizer"));
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName("BeatSaberMarkupLanguage.Tags.Settings.SubmenuTag"), "CreateObject"),
                postfix: new HarmonyMethod(typeof(UiTranslator), "SubmenuPostfix"));
            harmony.Patch(AccessTools.Method(typeof(CustomListTableData), "CellForIdx"),
                prefix: new HarmonyMethod(typeof(UiTranslator), "PlaylistCellPrefix"),
                finalizer: new HarmonyMethod(typeof(UiTranslator), "PlaylistCellFinalizer"));
            harmony.Patch(AccessTools.Method(typeof(HMUI.HoverHintController), "SetupAndShowHintPanel"),
                prefix: new HarmonyMethod(typeof(UiTranslator), "HoverHintPrefix"));
            var extraParams = AccessTools.TypeByName("BetterSongList.HarmonyPatches.UI.ExtraLevelParams");
            if (extraParams != null)
                foreach (var method in AccessTools.GetDeclaredMethods(extraParams).Where(value => value.Name.Contains("g__ModifyValue")))
                    harmony.Patch(method, postfix: new HarmonyMethod(typeof(UiTranslator), "SongListHintPostfix"));
            var folderCell = AccessTools.TypeByName("PlaylistManager.Types.FolderCell");
            if (folderCell != null)
                harmony.Patch(AccessTools.Method(folderCell, "PopulateCell"),
                    prefix: new HarmonyMethod(typeof(UiTranslator), "FolderCellPrefix"),
                    postfix: new HarmonyMethod(typeof(UiTranslator), "FolderCellPostfix"),
                    finalizer: new HarmonyMethod(typeof(UiTranslator), "FolderCellFinalizer"));
            var filters = AccessTools.TypeByName("MorePlaylists.BeatSaver.BeatSaverListViewController");
            if (filters != null)
                harmony.Patch(AccessTools.Method(filters, "SetActiveFilter"),
                    prefix: new HarmonyMethod(typeof(UiTranslator), "FolderCellPrefix"),
                    postfix: new HarmonyMethod(typeof(UiTranslator), "FilterCaptionPostfix"),
                    finalizer: new HarmonyMethod(typeof(UiTranslator), "FolderCellFinalizer"));
            var saberCell = AccessTools.TypeByName("CustomSabersLite.Menu.Components.SaberListCell");
            if (saberCell != null)
                harmony.Patch(AccessTools.Method(saberCell, "UpdateInfo"), postfix: new HarmonyMethod(typeof(UiTranslator), "SaberCellPostfix"));
            var avatarCell = AccessTools.TypeByName("CustomAvatar.UI.AvatarListTableCell");
            if (avatarCell != null)
                harmony.Patch(AccessTools.Method(avatarCell, "UpdateData"), prefix: new HarmonyMethod(typeof(UiTranslator), "AvatarCellPrefix"));
            owner = new GameObject("ModUiChinese.Runtime");
            UnityEngine.Object.DontDestroyOnLoad(owner);
            owner.AddComponent<UiTranslator>();
            UiTranslator.NotifyPluginLanguages();
            UiTranslator.Log.Info("Chinese mod UI localization enabled");
        }
        [OnDisable] public void Disable()
        {
            if (harmony != null) harmony.UnpatchSelf();
            UiTranslator.Restore();
            BeatSaberMarkupLanguage.Util.MainMenuAwaiter.MainMenuInitializing -= SymbolFontFallback.Enable;
            SymbolFontFallback.Disable();
            if (owner != null) UnityEngine.Object.Destroy(owner);
            UiTranslator.Engine = null;
            UiTranslator.NotifyPluginLanguages();
        }
    }
    public sealed class UiScope : MonoBehaviour
    {
        public string Scope;
    }
    public sealed class UiDataHint : MonoBehaviour
    {
        public object Host;
        public string Binding;
    }
    public sealed class UiDataBinding : MonoBehaviour
    {
        public string Binding;
        public object Host;
        public bool IsData
        {
            get
            {
                if (Binding == "name" && Host != null && Host.GetType().FullName == "Camera2.UI.CustomSceneUIEntry")
                {
                    var name = AccessTools.Property(Host.GetType(), "_name");
                    return name != null && !String.IsNullOrEmpty(name.GetValue(Host, null) as string);
                }
                if (Binding != "playlist-description" || Host == null || Host.GetType().Assembly.GetName().Name != "MorePlaylists") return true;
                var field = AccessTools.Field(Host.GetType(), "selectedPlaylistEntry");
                var entry = field == null ? null : field.GetValue(Host);
                var description = entry == null ? null : AccessTools.Property(entry.GetType(), "Description");
                // The empty-description fallback is a caption; supplied descriptions are data.
                return description != null && !String.IsNullOrWhiteSpace(description.GetValue(entry, null) as string);
            }
        }
    }
    public sealed class UiTranslator : MonoBehaviour
    {
        sealed class Tracked
        {
            public TMP_Text Text;
            public string Source, Displayed, Scope;
        }
        sealed class FontFallbackChange
        {
            public TMP_FontAsset Font;
            public List<TMP_FontAsset> Original, Applied;
        }
        public sealed class ParseState
        {
            public string PreviousScope, Scope;
            public GameObject Parent;
            public object Host;
            public string Content;
            public string[] DataBindings;
            public string[] DataHints;
            public int[] ExistingModals;
            public HashSet<int> ExistingTransforms;
        }
        public sealed class PlaylistCellState
        {
            public CustomListTableData.CustomCellInfo Item;
            public string Text, Subtext;
        }
        sealed class PlaylistList
        {
            public CustomListTableData List;
            public bool ImagePicker;
        }
        public static TranslationEngine Engine;
        public static IPA.Logging.Logger Log;
        [ThreadStatic] static string ambient;
        [ThreadStatic] static int dataWriteDepth;
        static readonly Dictionary<int, PlaylistList> playlistLists = new Dictionary<int, PlaylistList>();
        const string DataTag = "mod-ui-chinese-data-";
        static readonly HashSet<string> PlaylistDataBindings = new HashSet<string>(new[] {
            "playlist-name", "playlist-author", "playlist-description", "folder-text"
        }, StringComparer.Ordinal);
        static readonly Dictionary<int, Tracked> tracked = new Dictionary<int, Tracked>();
        static readonly HashSet<int> protectedTexts = new HashSet<int>();
        static readonly HashSet<int> builtInTexts = new HashSet<int>();
        static readonly List<Behaviour> disabledInheritedLocalization = new List<Behaviour>();
        static readonly Dictionary<int, FontFallbackChange> changedFallbacks = new Dictionary<int, FontFallbackChange>();
        const string ChineseFontName = "SourceHanSansCN-Bold-SDF-Common-1(2k)";
        static readonly MethodInfo ClearGlyphCache = AccessTools.Method(
            typeof(TMP_FontAsset).Assembly.GetType("TMPro.TMP_ResourceManager"), "ClearFontAssetGlyphCache");
        static readonly Regex DataName = new Regex("song.?name|song.?author|level.?author|author.?name|mapper.?name|user.?name|player.?name|saber.?name|camera.?name|preset.?name|file.?name|file.?path", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex ExplicitKey = new Regex("(?:text-key|tab-name-key)\\s*=\\s*['\"]([^'\"]+)['\"]", RegexOptions.CultureInvariant);
        float next;
        static TMP_FontAsset cjkFont;
        static int fontLookupFrame = -1;
        static bool fontCacheDirty;
        static bool refreshing;
        static bool suspended;
        static readonly Dictionary<string, string> captionScopes = new Dictionary<string, string>(StringComparer.Ordinal);
        static object settingsRegistry;
        static object menuButtonRegistry;
        static readonly Regex RichTags = new Regex("<[^>]+>", RegexOptions.CultureInvariant);
        internal static void ConfigureCaptionScopes()
        {
            captionScopes.Clear();
            foreach (var plugin in IPA.Loader.PluginManager.EnabledPlugins) {
                string scope = plugin.Id;
                if (!KnownScope(scope)) continue;
                captionScopes[scope] = scope;
                if (!String.IsNullOrEmpty(plugin.Name)) captionScopes[plugin.Name] = scope;
            }
            captionScopes["Activity Energy"] = "ActivityEnergyCounter";
            if (settingsRegistry != null)
            {
                var menus = AccessTools.Property(settingsRegistry.GetType(), "SettingsMenus");
                var entries = menus == null ? null : menus.GetValue(settingsRegistry, null) as IEnumerable;
                if (entries != null)
                    foreach (var entry in entries)
                    {
                        var host = AccessTools.Property(entry.GetType(), "Host");
                        var text = AccessTools.Property(entry.GetType(), "Text");
                        if (host != null && text != null) RegisterCaption(text.GetValue(entry, null) as string, host.GetValue(entry, null));
                    }
            }
            if (menuButtonRegistry != null)
            {
                var buttons = AccessTools.Property(menuButtonRegistry.GetType(), "Buttons");
                var entries = buttons == null ? null : buttons.GetValue(menuButtonRegistry, null) as IEnumerable;
                if (entries != null) foreach (var entry in entries) RegisterMenuButton(entry);
            }
        }
        static void RegisterCaption(string caption, object host)
        {
            if (host == null || String.IsNullOrEmpty(caption)) return;
            string scope = host.GetType().Assembly.GetName().Name;
            if (KnownScope(scope)) captionScopes[RichTags.Replace(caption, "")] = scope;
        }
        public static void SettingsRegistryPostfix(object __instance)
        {
            settingsRegistry = __instance;
        }
        public static void SettingsMenuPostfix(string __0, object __2)
        {
            RegisterCaption(__0, __2);
        }
        public static void MenuButtonPostfix(object __instance, object __0)
        {
            menuButtonRegistry = __instance;
            RegisterMenuButton(__0);
        }
        public static IEnumerable<CodeInstruction> CameraPopupTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                var method = instruction.operand as MethodInfo;
                if (method != null && method.Name == "MessageBox" && method.DeclaringType.FullName == "Camera2.Utils.WinAPI")
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(UiTranslator), "CameraNativeMessageBox");
                }
                yield return instruction;
            }
        }
        [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern int NativeMessageBox(IntPtr window, string text, string caption, int type);
        public static void TranslateNativeDialog(string scope, ref string text, ref string caption)
        {
            if (!Enabled()) return;
            text = Engine.Translate(text, scope);
            caption = Engine.Translate(caption, scope);
        }
        public static int CameraNativeMessageBox(IntPtr window, string text, string caption, int type)
        {
            TranslateNativeDialog("Camera2", ref text, ref caption);
            return NativeMessageBox(window, text, caption, type);
        }
        static void RegisterMenuButton(object button)
        {
            if (button == null) return;
            var callbackField = AccessTools.Field(button.GetType(), "OnClick");
            var callback = callbackField == null ? null : callbackField.GetValue(button) as Delegate;
            if (callback == null) return;
            foreach (var handler in callback.GetInvocationList())
            {
                string scope = handler.Method.DeclaringType.Assembly.GetName().Name;
                if (!KnownScope(scope)) continue;
                foreach (string property in new[] { "Text", "HoverHint" })
                {
                    var member = AccessTools.Property(button.GetType(), property);
                    string caption = member == null ? null : member.GetValue(button, null) as string;
                    if (!String.IsNullOrEmpty(caption)) captionScopes[RichTags.Replace(caption, "")] = scope;
                }
            }
        }
        static bool KnownScope(string scope) { return Engine != null ? Engine.HasScope(scope) : GeneratedCatalog.HasScope(scope); }
        static bool Enabled()
        {
            return Engine != null && !suspended && LocalizationState.Enabled;
        }
        internal static void Reload()
        {
            var replacement = LocalizationState.CreateEngine();
            suspended = true;
            try { Restore(false); Engine = replacement; ConfigureCaptionScopes(); GameplayFeedback.Refresh(); }
            finally { suspended = false; }
            NotifyPluginLanguages();
            nextRefresh = true;
        }
        internal static void NotifyPluginLanguages()
        {
            NotifyPluginLanguage("BetterSongSearch", "BetterSongSearch.Util.BssText");
            NotifyPluginLanguage("BeatSage", "BeatSage.UI.LocalizationBridge");
        }
        static void NotifyPluginLanguage(string pluginId, string typeName)
        {
            if (!IPA.Loader.PluginManager.EnabledPlugins.Any(plugin => plugin.Id == pluginId)) return;
            try
            {
                var type = AccessTools.TypeByName(typeName);
                var bridge = type == null ? null : AccessTools.Method(type, "OnLocalizationReloaded");
                if (bridge != null) bridge.Invoke(null, null);
            }
            catch (Exception error) { Log.Error(pluginId + " language refresh failed: " + error); }
        }
        static bool nextRefresh;
        public static void ValidateFontApi()
        {
            if (ClearGlyphCache == null) throw new MissingMethodException("TMP_ResourceManager.ClearFontAssetGlyphCache");
        }
        static string ScopeOf(TMP_Text text)
        {
            return ScopeOf(text.transform);
        }
        static string ScopeOf(Transform transform)
        {
            if (ambient != null) return ambient;
            for (var current = transform; current != null; current = current.parent)
            {
                var marker = current.GetComponent<UiScope>();
                if (marker != null && marker.Scope != null) return marker.Scope;
                foreach (var component in current.GetComponents<MonoBehaviour>())
                {
                    if (component == null) continue;
                    if (component.GetType().Assembly == typeof(UiTranslator).Assembly) continue;
                    string name = component.GetType().Assembly.GetName().Name;
                    if (KnownScope(name)) return name;
                }
                if (current.name.StartsWith("Counters+", StringComparison.Ordinal)) return "Counters+";
                if (current.name.StartsWith("Cam2_", StringComparison.Ordinal)) return "Camera2";
            }
            return "Common";
        }
        static bool Protected(TMP_Text text)
        {
            if (builtInTexts.Contains(text.GetInstanceID())) return false;
            for (var current = text.transform; current != null; current = current.parent)
            {
                var binding = current.GetComponent<UiDataBinding>();
                if (binding != null) return binding.IsData;
                var setting = current.GetComponent<BeatSaberMarkupLanguage.Components.Settings.StringSetting>();
                if (setting != null && setting.TextMesh == text) return true;
            }
            if (ScopeOf(text) == "Counters+")
            {
                if (text.name == "SongAuthor") return false; // Reused song cells contain counter descriptions.
                if (text.name == "SongName" && text.text == "Main")
                    for (var parent = text.transform.parent; parent != null; parent = parent.parent)
                        foreach (var component in parent.GetComponents<MonoBehaviour>())
                        {
                            if (component == null || !component.GetType().Name.Contains("ListTableCell")) continue;
                            var index = AccessTools.Property(component.GetType(), "idx");
                            if (index != null && Convert.ToInt32(index.GetValue(component, null)) == 0) return false;
                        }
            }
            if (protectedTexts.Contains(text.GetInstanceID())) return true;
            if (DataName.IsMatch(text.name)) return true;
            for (var current = text.transform; current != null; current = current.parent)
            {
                foreach (var input in current.GetComponents<TMP_InputField>())
                    if (input.textComponent == text) return true;
                foreach (var component in current.GetComponents<MonoBehaviour>())
                {
                    if (component == null) continue;
                    string typeName = component.GetType().Name;
                    if (typeName.IndexOf("InputField", StringComparison.OrdinalIgnoreCase) >= 0 && text.name.IndexOf("Placeholder", StringComparison.OrdinalIgnoreCase) < 0) return true;
                    if (typeName.IndexOf("ListTableCell", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        ProtectHostData(component);
                        if (protectedTexts.Contains(text.GetInstanceID())) return true;
                    }
                }
            }
            return false;
        }
        public static void TextPrefix(TMP_Text __instance, ref string __0)
        {
            // Data labels and inactive prefabs can measure their text before
            // LateUpdate. Supply the fallback before TMP's first glyph lookup.
            if (__instance != null && NeedsCjk(__0)) EnsureFont(__instance);
            if (!Enabled() || __instance == null || String.IsNullOrEmpty(__0) || dataWriteDepth > 0) return;
            if (Protected(__instance)) { tracked.Remove(__instance.GetInstanceID()); return; }
            try
            {
                int id = __instance.GetInstanceID();
                if (__0 != "设置" && __0 != "Settings" && __instance.transform.parent != null && __instance.transform.parent.name.Contains("TextSegmentedControlCell"))
                    foreach (var component in __instance.GetComponents<MonoBehaviour>())
                        if (component != null && component.enabled && component.GetType().FullName.Contains("LocalizedTextMeshPro") && NativeKey(component) == "BUTTON_SETTINGS")
                        { component.enabled = false; disabledInheritedLocalization.Add(component); }
                Tracked old;
                if (refreshing && tracked.TryGetValue(id, out old) && __0 == old.Displayed) return;
                string scope = ScopeOf(__instance);
                string captionScope;
                if (!LocalModUI.UiRegistry.OwnsPageScope(scope) && captionScopes.TryGetValue(RichTags.Replace(__0, ""), out captionScope)) scope = captionScope;
                if (__0.StartsWith("Camera2 | ", StringComparison.Ordinal)) scope = "Camera2";
                string translated = Engine.Translate(__0, scope);
                if (NeedsCjk(translated)) EnsureFont(__instance);
                // The base patch's generic placeholder is not a usable play label.
                if (__0 == "!未定义" || __0 == "!未定义!")
                    for (var parent = __instance.transform; parent != null; parent = parent.parent)
                        if (parent.name == "ActionButton") { translated = "开始"; break; }
                if (translated == __0) return;
                tracked[id] = new Tracked { Text = __instance, Source = __0, Displayed = translated, Scope = scope };
                __0 = translated;
            }
            catch (Exception exception) { Log.Error("UI translation failed: " + exception); }
        }
        public static void FormattedTextPostfix(TMP_Text __instance)
        {
            if (Enabled() && dataWriteDepth == 0) Apply(__instance);
        }
        public static void ProgressAppendPrefix(object __instance, out TMP_Text __state)
        {
            __state = null;
            if (!Enabled()) return;
            var field = AccessTools.Field(__instance.GetType(), "_headerText");
            var text = field == null ? null : field.GetValue(__instance) as TMP_Text;
            if (text == null) return;
            dataWriteDepth++;
            __state = text;
            Tracked old;
            if (tracked.TryGetValue(text.GetInstanceID(), out old) && text.text == old.Displayed)
                text.text = old.Source;
            tracked.Remove(text.GetInstanceID());
        }
        public static Exception ProgressAppendFinalizer(Exception __exception, TMP_Text __state)
        {
            if (__state != null)
            {
                dataWriteDepth--;
                if (Enabled()) Apply(__state);
            }
            return __exception;
        }
        public static void ChartWarningPostfix(object __instance)
        {
            var field = AccessTools.Field(__instance.GetType(), "_peakWarningGo");
            var canvas = field == null ? null : field.GetValue(__instance) as GameObject;
            if (canvas == null) return;
            var marker = canvas.GetComponent<UiScope>();
            if (marker == null) marker = canvas.AddComponent<UiScope>();
            marker.Scope = "SongChartVisualizer";
            foreach (var text in canvas.GetComponentsInChildren<TMP_Text>(true)) Apply(text);
        }
        public static void AvatarCellPrefix(object __instance)
        {
            var property = AccessTools.Property(__instance.GetType(), "listItem");
            var item = property == null ? null : property.GetValue(__instance, null);
            if (item == null) return;
            var file = AccessTools.Property(item.GetType(), "fileName");
            var error = AccessTools.Property(item.GetType(), "loadException");
            var nameField = AccessTools.Field(__instance.GetType(), "_nameText");
            var authorField = AccessTools.Field(__instance.GetType(), "_authorText");
            MarkAvatarCaption(nameField == null ? null : nameField.GetValue(__instance) as TMP_Text,
                file != null && String.IsNullOrEmpty(file.GetValue(item, null) as string));
            MarkAvatarCaption(authorField == null ? null : authorField.GetValue(__instance) as TMP_Text,
                error != null && error.GetValue(item, null) != null);
        }
        static void MarkAvatarCaption(TMP_Text text, bool caption)
        {
            if (text == null) return;
            int id = text.GetInstanceID();
            if (caption) builtInTexts.Add(id);
            else { builtInTexts.Remove(id); protectedTexts.Add(id); }
        }
        public static void SaberCellPostfix(object __instance, object __0)
        {
            var field = AccessTools.Field(__instance.GetType(), "mainText");
            var text = field == null ? null : field.GetValue(__instance) as TMP_Text;
            if (text == null) return;
            int id = text.GetInstanceID();
            builtInTexts.Remove(id);
            string label = null;
            if (__0 != null && __0.GetType().FullName == "CustomSabersLite.Models.ListFavouritesCellInfo") label = "Favourites";
            if (__0 != null && label == null)
            {
                var property = AccessTools.Property(__0.GetType(), "Value");
                object value = property == null ? null : property.GetValue(__0, null);
                if (value != null && value.GetType().Name == "DefaultSaberValue") label = "Default";
            }
            if (label == null) { protectedTexts.Add(id); RestoreDataText(text); return; }
            builtInTexts.Add(id);
            text.text = label;
            Apply(text);
        }
        public static void ParsePrefix(object __0, GameObject __1, object __2, out ParseState __state)
        {
            string scope = __2 == null ? "BSML" : __2.GetType().Assembly.GetName().Name;
            __state = new ParseState { PreviousScope = ambient, Scope = scope, Parent = __1, Host = __2, Content = __0 == null ? null : __0.ToString() };
            __state.ExistingModals = __1 == null ? new int[0] : __1.GetComponentsInChildren<HMUI.ModalView>(true).Select(value => value.GetInstanceID()).ToArray();
            __state.ExistingTransforms = new HashSet<int>(__1 == null ? new int[0] : __1.GetComponentsInChildren<Transform>(true).Select(value => value.GetInstanceID()));
            var xml = __0 as XContainer;
            if (xml != null && (scope == "PlaylistManager" || scope == "MorePlaylists" || scope == "BeatSage" || scope == "Camera2" || scope == "HitScoreVisualizer" || scope == "BetterSongSearch"))
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                var hints = new HashSet<string>(StringComparer.Ordinal);
                var root = xml as XElement;
                var elements = root == null ? xml.Descendants() : root.DescendantsAndSelf();
                foreach (var element in elements)
                {
                    string hint = (string)element.Attribute("hover-hint");
                    if (hint == "~name-hint" || hint == "~author-hint")
                    {
                        AddTag(element, DataTag + hint.Substring(1));
                        hints.Add(hint.Substring(1));
                    }
                    string value = (string)element.Attribute("text");
                    if (value == null || !value.StartsWith("~", StringComparison.Ordinal)) continue;
                    string binding = value.Substring(1);
                    bool cameraData = scope == "Camera2" && (binding == "name" || binding == "camNames");
                    bool configData = scope == "HitScoreVisualizer" && binding == "ConfigName";
                    bool searchPresetData = scope == "BetterSongSearch" && binding == "name" && __2.GetType().FullName == "BetterSongSearch.UI.SplitViews.Presets+FilterPresetRow";
                    bool beatSageData = scope == "BeatSage" && (binding == "browser-title" || binding == "current-path" || binding == "selected-song-title" || binding == "selected-song-artist" || binding == "job-title" || binding == "job-artist" || binding == "selected-preset-label" || binding == "favorites-summary" || binding == "retry-title");
                    if (!beatSageData && !PlaylistDataBindings.Contains(binding) && !(binding == "name" && __2.GetType().FullName == "PlaylistManager.Types.Contributor") && !cameraData && !configData && !searchPresetData) continue;
                    string tag = DataTag + binding;
                    AddTag(element, tag);
                    names.Add(binding);
                }
                __state.DataBindings = names.ToArray();
                __state.DataHints = hints.ToArray();
            }
            ambient = scope;
        }
        static void AddTag(XElement element, string tag)
        {
            var tags = ((string)element.Attribute("tags") ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (!tags.Contains(tag)) tags.Add(tag);
            element.SetAttributeValue("tags", String.Join(" ", tags.ToArray()));
        }
        public static void SubmenuPostfix(GameObject __result)
        {
            // BSML creates submenu ViewControllers outside the parsed parent's
            // hierarchy and later reparents them into the navigation container.
            if (__result == null || !KnownScope(ambient)) return;
            var marker = __result.GetComponent<UiScope>();
            if (marker == null) marker = __result.AddComponent<UiScope>();
            marker.Scope = ambient;
        }
        static void ProtectHostData(object host)
        {
            if (host == null) return;
            for (var type = host.GetType(); type != null; type = type.BaseType)
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (!DataName.IsMatch(field.Name)) continue;
                    var component = field.GetValue(host) as Component;
                    if (component == null) continue;
                    foreach (var text in component.GetComponentsInChildren<TMP_Text>(true))
                    {
                        int id = text.GetInstanceID();
                        protectedTexts.Add(id);
                        Tracked old;
                        if (tracked.TryGetValue(id, out old)) { tracked.Remove(id); text.text = old.Source; }
                    }
                }
        }
        public static void ParsePostfix(BSMLParserParams __result, ParseState __state)
        {
            try
            {
                // A custom counter parses into Counters+' existing editor. Only
                // the newly created subtree belongs to that embedded plugin.
                if (__state.Parent != null && KnownScope(__state.Scope))
                    foreach (var item in __state.Parent.GetComponentsInChildren<Transform>(true))
                    {
                        if (__state.ExistingTransforms.Contains(item.GetInstanceID())) continue;
                        if (item.parent != null && !__state.ExistingTransforms.Contains(item.parent.GetInstanceID())) continue;
                        var marker = item.GetComponent<UiScope>();
                        if (marker == null) marker = item.gameObject.AddComponent<UiScope>();
                        marker.Scope = __state.Scope;
                    }
                if (__state.DataHints != null)
                    foreach (var binding in __state.DataHints)
                        foreach (var item in __result.GetObjectsWithTag(DataTag + binding))
                        {
                            var hint = item.GetComponent<UiDataHint>();
                            if (hint == null) hint = item.AddComponent<UiDataHint>();
                            hint.Host = __state.Host;
                            hint.Binding = binding;
                        }
                if (__state.DataBindings != null)
                    foreach (var binding in __state.DataBindings)
                        foreach (var item in __result.GetObjectsWithTag(DataTag + binding))
                        {
                            var marker = item.GetComponent<UiDataBinding>();
                            if (marker == null) marker = item.AddComponent<UiDataBinding>();
                            marker.Binding = binding;
                            marker.Host = __state.Host;
                            if (marker.IsData)
                                foreach (var text in item.GetComponentsInChildren<TMP_Text>(true)) RestoreDataText(text);
                        }
                RegisterPlaylistLists(__state.Host, __state.Scope);
                ProtectHostData(__state.Host);
                if (__state.Parent == null) return;
                // HMUI reparents a shown modal into its presentation container.
                // Carry this plugin's scope with the modal, never its screen.
                if (KnownScope(__state.Scope))
                    foreach (var modal in __state.Parent.GetComponentsInChildren<HMUI.ModalView>(true))
                    {
                        if (__state.ExistingModals != null && __state.ExistingModals.Contains(modal.GetInstanceID())) continue;
                        var marker = modal.GetComponent<UiScope>();
                        if (marker == null) marker = modal.gameObject.AddComponent<UiScope>();
                        marker.Scope = __state.Scope;
                    }
                var allowedKeys = new HashSet<string>(ExplicitKey.Matches(__state.Content ?? "").Cast<Match>().Select(x => x.Groups[1].Value), StringComparer.Ordinal);
                if (__state.Scope == "BSML" || KnownScope(__state.Scope))
                    foreach (var component in __state.Parent.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (component == null || __state.ExistingTransforms.Contains(component.transform.GetInstanceID()) || !component.GetType().FullName.Contains("LocalizedTextMeshPro")) continue;
                        string key = NativeKey(component);
                        if (key == null || allowedKeys.Contains(key) || !component.enabled) continue;
                        if (__state.Scope == "BSML" && key != "BUTTON_SETTINGS") continue;
                        component.enabled = false;
                        disabledInheritedLocalization.Add(component);
                    }
                foreach (var text in __state.Parent.GetComponentsInChildren<TMP_Text>(true))
                    if (!__state.ExistingTransforms.Contains(text.transform.GetInstanceID())) Apply(text);
            }
            finally { ambient = __state.PreviousScope; }
        }
        public static void HoverHintPrefix(HMUI.HoverHintController __instance, HMUI.HoverHint __0)
        {
            var panel = AccessTools.Field(typeof(HMUI.HoverHintController), "_hoverHintPanel").GetValue(__instance) as Component;
            if (panel == null || __0 == null) return;
            var binding = __0.GetComponent<UiDataHint>();
            if (binding != null && binding.Host != null)
            {
                // BSML evaluates hover-hint bindings only when parsing. Read the
                // current production value after the user edits a playlist.
                var property = AccessTools.Property(binding.Host.GetType(), binding.Binding == "name-hint" ? "NameHint" : "AuthorHint");
                if (property != null) __0.text = property.GetValue(binding.Host, null) as string ?? "";
            }
            var sourceData = __0.GetComponent<UiDataBinding>() ?? __0.GetComponentInParent<UiDataBinding>();
            var sourceText = __0.GetComponent<TMP_Text>();
            bool isData = binding != null || (sourceData != null && sourceData.IsData) ||
                (sourceText != null && Protected(sourceText)) || __0.GetComponents<MonoBehaviour>()
                .Any(component => component != null && component.GetType().Name == "AnnotatedBeatmapLevelCollectionCell");
            var scope = panel.GetComponent<UiScope>();
            if (scope == null) scope = panel.gameObject.AddComponent<UiScope>();
            scope.Scope = ScopeOf(__0.transform);
            string hintScope;
            if (!LocalModUI.UiRegistry.OwnsPageScope(scope.Scope) && captionScopes.TryGetValue(RichTags.Replace(__0.text ?? "", ""), out hintScope)) scope.Scope = hintScope;
            foreach (var text in panel.GetComponentsInChildren<TMP_Text>(true))
            {
                int id = text.GetInstanceID();
                tracked.Remove(id);
                if (isData) protectedTexts.Add(id);
                else protectedTexts.Remove(id);
            }
        }
        public static void SongListHintPostfix(TMP_Text __0)
        {
            if (__0 == null) return;
            var hint = __0.GetComponentInParent<HMUI.HoverHint>();
            if (hint == null) return;
            var marker = hint.GetComponent<UiScope>();
            if (marker == null) marker = hint.gameObject.AddComponent<UiScope>();
            marker.Scope = "BetterSongList";
        }
        static void RestoreDataText(TMP_Text text)
        {
            Tracked old;
            if (!tracked.TryGetValue(text.GetInstanceID(), out old)) return;
            tracked.Remove(text.GetInstanceID());
            if (text.text != old.Displayed) return;
            dataWriteDepth++;
            try { text.text = old.Source; }
            finally { dataWriteDepth--; }
        }
        static void RegisterPlaylistLists(object host, string scope)
        {
            if (host == null || scope != "PlaylistManager") return;
            string name = host.GetType().Name;
            if (name != "AddPlaylistModalController" && name != "ImageSelectionModalController") return;
            foreach (var field in AccessTools.GetDeclaredFields(host.GetType()))
            {
                var list = field.GetValue(host) as CustomListTableData;
                if (list == null || (name == "AddPlaylistModalController" && field.Name != "playlistTableData")) continue;
                playlistLists[list.GetInstanceID()] = new PlaylistList { List = list, ImagePicker = name == "ImageSelectionModalController" };
            }
        }
        public static void PlaylistCellPrefix(CustomListTableData __instance, int __1, out PlaylistCellState __state)
        {
            __state = null;
            PlaylistList rule;
            if (Engine == null || !playlistLists.TryGetValue(__instance.GetInstanceID(), out rule)) return;
            if (__1 < 0 || __1 >= __instance.Data.Count || (rule.ImagePicker && __1 >= 2)) return;
            var item = __instance.Data[__1];
            __state = new PlaylistCellState { Item = item, Text = item.Text, Subtext = item.Subtext };
            // Native song cells protect their author field as data. Translate only the
            // known folder/count captions before those cells render, then restore the model.
            if (rule.ImagePicker) item.Text = Engine.Translate(item.Text, "PlaylistManager");
            item.Subtext = Engine.Translate(item.Subtext, "PlaylistManager");
        }
        public static Exception PlaylistCellFinalizer(Exception __exception, PlaylistCellState __state)
        {
            if (__state != null) { __state.Item.Text = __state.Text; __state.Item.Subtext = __state.Subtext; }
            return __exception;
        }
        public static void FolderCellPrefix() { dataWriteDepth++; }
        public static void FolderCellPostfix(Component __instance)
        {
            var text = AccessTools.Field(__instance.GetType(), "text").GetValue(__instance) as TMP_Text;
            if (text != null) { protectedTexts.Add(text.GetInstanceID()); RestoreDataText(text); }
        }
        public static Exception FolderCellFinalizer(Exception __exception) { dataWriteDepth--; return __exception; }
        static object ValueOf(object instance, string name)
        {
            if (instance == null) return null;
            var property = AccessTools.Property(instance.GetType(), name);
            return property == null ? null : property.GetValue(instance, null);
        }
        public static void FilterCaptionPostfix(object __instance, object __0)
        {
            var text = AccessTools.Field(__instance.GetType(), "filterText").GetValue(__instance) as TMP_Text;
            if (text == null || Engine == null) return;
            string source = AccessTools.Field(__instance.GetType(), "filterString").GetValue(__instance) as string ?? "";
            string display;
            if (Convert.ToString(ValueOf(__0, "FilterMode")) == "User")
                display = Engine.Translate("user:" + (ValueOf(__0, "UserName") as string ?? ""), "MorePlaylists");
            else
            {
                var filter = ValueOf(__0, "NullableSearchFilter");
                var parts = new List<string>();
                string query = ValueOf(filter, "Query") as string;
                if (!String.IsNullOrWhiteSpace(query)) parts.Add(query);
                if (Convert.ToBoolean(ValueOf(filter, "IncludeEmpty") ?? false)) parts.Add(Engine.Translate("IncludeEmpty", "MorePlaylists"));
                if (Convert.ToBoolean(ValueOf(filter, "IsCurated") ?? false)) parts.Add(Engine.Translate("CuratedOnly", "MorePlaylists"));
                display = String.Join("，", parts.ToArray());
            }
            protectedTexts.Add(text.GetInstanceID());
            text.text = display;
            tracked[text.GetInstanceID()] = new Tracked { Text = text, Source = source, Displayed = display, Scope = "MorePlaylists" };
        }
        public static Exception ParseFinalizer(Exception __exception, ParseState __state)
        {
            if (__state != null) ambient = __state.PreviousScope;
            return __exception;
        }
        static string NativeKey(Component component)
        {
            for (var type = component.GetType(); type != null; type = type.BaseType)
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                    if (field.FieldType == typeof(string) && field.Name.IndexOf("key", StringComparison.OrdinalIgnoreCase) >= 0)
                        return (string)field.GetValue(component);
            return null;
        }
        static void Apply(TMP_Text text)
        {
            if (text == null) return;
            if (Protected(text))
            {
                // Data labels keep their content, but use the same Chinese
                // glyph family as captions when they contain Han characters.
                if (NeedsCjk(text.text)) EnsureFont(text);
                return;
            }
            Tracked old;
            string source = tracked.TryGetValue(text.GetInstanceID(), out old) && text.text == old.Displayed ? old.Source : text.text;
            string translated = source;
            TextPrefix(text, ref translated);
            if (text.text != translated) text.text = translated;
            if (NeedsCjk(translated)) EnsureFont(text);
        }
        static bool NeedsCjk(string value)
        {
            return value != null && value.Any(code => (code >= 0x3000 && code <= 0x9fff) || (code >= 0xf900 && code <= 0xfaff));
        }
        static void FindChineseFont()
        {
            if (cjkFont != null || fontLookupFrame == Time.frameCount) return;
            fontLookupFrame = Time.frameCount;
            // Common-1 links Common-2 and Uncommon of the same CN Bold face.
            cjkFont = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(
                value => value != null && value.name == ChineseFontName);
        }
        static void EnsureFont(TMP_Text text)
        {
            FindChineseFont();
            if (text.font == null || cjkFont == null || text.font == cjkFont) return;
            var font = text.font;
            var original = font.fallbackFontAssetTable;
            if (original != null && original.Count > 0 && original[0] == cjkFont) return;
            var ordered = new List<TMP_FontAsset> { cjkFont };
            if (original != null) ordered.AddRange(original.Where(value => value != cjkFont));
            FontFallbackChange change;
            if (!changedFallbacks.TryGetValue(font.GetInstanceID(), out change))
            {
                change = new FontFallbackChange { Font = font, Original = original };
                changedFallbacks.Add(font.GetInstanceID(), change);
            }
            change.Applied = ordered;
            font.fallbackFontAssetTable = ordered;
            fontCacheDirty = true;
        }
        static void RefreshFontCache(TMP_Text[] texts)
        {
            if (!fontCacheDirty) return;
            // TMP caches fallback glyphs in the primary font. Reordering only
            // the fallback list would leave already-rendered JP glyphs cached.
            ClearGlyphCache.Invoke(null, null);
            fontCacheDirty = false;
            foreach (var text in texts)
                if (text != null && text.gameObject.activeInHierarchy) text.havePropertiesChanged = true;
        }
        void LateUpdate()
        {
            if (!nextRefresh && Time.unscaledTime < next) return;
            nextRefresh = false;
            next = Time.unscaledTime + 0.5f;
            try
            {
                FindChineseFont();
                refreshing = true;
                var texts = Resources.FindObjectsOfTypeAll<TMP_Text>();
                foreach (var text in texts)
                    if (text != null && text.gameObject.activeInHierarchy)
                    {
                        if (Enabled()) Apply(text);
                        else if (NeedsCjk(text.text)) EnsureFont(text);
                    }
                RefreshFontCache(texts);
                foreach (var id in tracked.Where(x => x.Value.Text == null).Select(x => x.Key).ToArray()) { tracked.Remove(id); protectedTexts.Remove(id); }
                var alive = new HashSet<int>(texts.Where(x => x != null).Select(x => x.GetInstanceID()));
                protectedTexts.RemoveWhere(x => !alive.Contains(x));
                builtInTexts.RemoveWhere(x => !alive.Contains(x));
                disabledInheritedLocalization.RemoveAll(x => x == null);
                foreach (var id in changedFallbacks.Where(value => value.Value.Font == null).Select(value => value.Key).ToArray())
                    changedFallbacks.Remove(id);
                foreach (var id in playlistLists.Where(value => value.Value.List == null).Select(value => value.Key).ToArray()) playlistLists.Remove(id);
            }
            catch (Exception exception) { Log.Error("UI localization refresh failed: " + exception); }
            finally { refreshing = false; }
        }
        public static object[] Capture()
        {
            return Resources.FindObjectsOfTypeAll<TMP_Text>().Where(x => x != null && x.gameObject.activeInHierarchy).Select(x =>
            {
                Tracked entry;
                tracked.TryGetValue(x.GetInstanceID(), out entry);
                if (entry != null && x.text != entry.Displayed) entry = null;
                string scope = entry == null ? ScopeOf(x) : entry.Scope;
                string captionScope;
                if (entry == null && !LocalModUI.UiRegistry.OwnsPageScope(scope) && captionScopes.TryGetValue(RichTags.Replace(x.text ?? "", ""), out captionScope)) scope = captionScope;
                string source = entry == null ? x.text : entry.Source;
                if (source != null && source.StartsWith("Camera2 | ", StringComparison.Ordinal)) scope = "Camera2";
                string[] dataSegments = scope == "Camera2" && source != null && source.StartsWith("Camera2 | ", StringComparison.Ordinal)
                    ? new[] { source.Substring("Camera2 | ".Length) } : new string[0];
                const string loadedConfigPrefix = "Currently Loaded Config<size=90%> : ";
                if (scope == "HitScoreVisualizer" && source != null && source.StartsWith(loadedConfigPrefix, StringComparison.Ordinal))
                {
                    string configFileName = source.Substring(loadedConfigPrefix.Length);
                    if (configFileName != "None") dataSegments = new[] { configFileName };
                }
                if (scope == "BetterSongList" && source != null && source.StartsWith("BetterSongList v", StringComparison.Ordinal))
                {
                    int author = source.LastIndexOf(" by ", StringComparison.Ordinal);
                    if (author >= 0) dataSegments = new[] { source.Substring(author + 4) };
                }
                return (object)new { path = PathOf(x.transform), text = x.text, source = source, scope = scope, dataSegments = dataSegments,
                    localizationEnabled = Enabled(), scopeEnabled = Engine != null && Engine.IsScopeEnabled(scope),
                    protectedData = Protected(x), font = x.font == null ? null : x.font.name };
            }).Concat(GameplayFeedback.Capture()).ToArray();
        }
        static string PathOf(Transform transform)
        {
            var parts = new List<string>();
            for (; transform != null; transform = transform.parent) parts.Add(transform.name);
            parts.Reverse();
            return String.Join("/", parts.ToArray());
        }
        public static void Restore(bool removeMarkers = true)
        {
            GameplayFeedback.Restore(removeMarkers);
            foreach (var entry in tracked.Values) if (entry.Text != null && entry.Text.text == entry.Displayed) entry.Text.text = entry.Source;
            // A reload keeps the parser's correction of inherited native keys.
            // Re-enabling those clones would turn arbitrary tab titles into Settings.
            if (removeMarkers)
                foreach (var component in disabledInheritedLocalization) if (component != null) component.enabled = true;
            // Glyph coverage also belongs to untranslated song/author data.
            // A language reload keeps it; actual plugin disable restores it.
            if (removeMarkers)
            {
                foreach (var change in changedFallbacks.Values)
                    if (change.Font != null && System.Object.ReferenceEquals(change.Font.fallbackFontAssetTable, change.Applied))
                    {
                        // BSML can append system fonts after our first text
                        // assignment. Keep those additions when removing our
                        // preferred CN face instead of restoring a stale list.
                        var restored = change.Applied.Where(value => value != cjkFont).ToList();
                        if (change.Original != null && change.Original.Contains(cjkFont))
                            restored.Insert(Math.Min(change.Original.IndexOf(cjkFont), restored.Count), cjkFont);
                        change.Font.fallbackFontAssetTable = change.Original == null && restored.Count == 0 ? null : restored;
                        fontCacheDirty = true;
                    }
                RefreshFontCache(Resources.FindObjectsOfTypeAll<TMP_Text>());
                changedFallbacks.Clear(); cjkFont = null; fontLookupFrame = -1;
            }
            tracked.Clear();
            if (removeMarkers)
            {
                protectedTexts.Clear(); builtInTexts.Clear();
                disabledInheritedLocalization.Clear();
                playlistLists.Clear();
            }
            else disabledInheritedLocalization.RemoveAll(component => component == null);
            if (removeMarkers) {
                foreach (var scope in Resources.FindObjectsOfTypeAll<UiScope>()) if (scope != null) UnityEngine.Object.Destroy(scope);
                foreach (var binding in Resources.FindObjectsOfTypeAll<UiDataBinding>()) if (binding != null) UnityEngine.Object.Destroy(binding);
                foreach (var hint in Resources.FindObjectsOfTypeAll<UiDataHint>()) if (hint != null) UnityEngine.Object.Destroy(hint);
            }
        }
    }
}
