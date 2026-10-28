using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Xml.Linq;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Newtonsoft.Json.Linq;

namespace Camera2UrpFix
{
    internal static class SettingsCompatibility
    {
        internal static readonly List<CameraSettingsControls> Owners = new List<CameraSettingsControls>();
        internal static Type ViewType;
        internal static Type SceneManagerType;
        internal static MethodInfo Rebuild;
        internal static bool Releasing;
        [ThreadStatic] internal static CameraSettingsControls EditOwner;
        [ThreadStatic] internal static bool ExecutingRebuild;

        public sealed class EditState
        {
            internal CameraSettingsControls Previous;
            internal CameraSettingsControls Owner;
        }

        internal static void Install(Harmony harmony)
        {
            Releasing = false;
            ViewType = AccessTools.TypeByName("Camera2.UI.SettingsView");
            SceneManagerType = AccessTools.TypeByName("Camera2.Managers.ScenesManager");
            var camType = AccessTools.TypeByName("Camera2.Behaviours.Cam2");
            Rebuild = AccessTools.Method(camType, "UpdateRenderTextureAndView");
            if (ViewType == null || SceneManagerType == null || Rebuild == null)
                throw new MissingMemberException("Camera2 settings UI contract is unavailable");

            Patch(harmony, AccessTools.Method(ViewType, "Parsed"), null, "ParsedPostfix", null);
            Patch(harmony, AccessTools.Method(ViewType, "SetCam"), "SetCamPrefix", "SetCamPostfix", null);
            Patch(harmony, AccessTools.Method(ViewType, "SaveSettings"), "SaveSettingsPrefix", null, null);
            Patch(harmony, Rebuild, "RebuildPrefix", null, null);
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Camera2.Behaviours.CamerasViewport"), "Update"),
                transpiler: new HarmonyMethod(typeof(SettingsCompatibility), "ViewportUpdateTranspiler"));
            Patch(harmony, AccessTools.Method(camType, "OnDestroy"), "DestroyPrefix", null, null);
            foreach (var setter in ViewType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(value => value.DeclaringType == ViewType && value.Name.StartsWith("set_", StringComparison.Ordinal)))
                Patch(harmony, setter, "EditPrefix", null, "EditFinalizer");
            Patch(harmony, typeof(BSMLParser).GetMethod("Parse", new[] { typeof(string), typeof(GameObject), typeof(object) }),
                "ParsePrefix", null, null);
        }

        static void Patch(Harmony harmony, MethodBase method, string prefix, string postfix, string finalizer)
        {
            if (method == null) throw new MissingMethodException("Camera2 settings patch target is missing");
            harmony.Patch(method,
                prefix == null ? null : new HarmonyMethod(typeof(SettingsCompatibility), prefix),
                postfix == null ? null : new HarmonyMethod(typeof(SettingsCompatibility), postfix),
                null, finalizer == null ? null : new HarmonyMethod(typeof(SettingsCompatibility), finalizer), null);
        }

        public static void ParsePrefix(ref string __0, object __2)
        {
            if (__2 == null || __2.GetType().FullName != "Camera2.UI.CamList") return;
            var document = XDocument.Parse(__0, LoadOptions.PreserveWhitespace);
            foreach (var element in document.Descendants().Where(value =>
                (string)value.Attribute("click-event") == "showViewrectModal" ||
                (string)value.Attribute("show-event") == "showViewrectModal").ToArray()) element.Remove();
            __0 = document.ToString(SaveOptions.DisableFormatting);
        }

        internal static CameraSettingsControls Owner(object view)
        {
            var component = view as Component;
            if (component == null) return null;
            var owner = component.GetComponent<CameraSettingsControls>();
            if (owner == null) owner = component.gameObject.AddComponent<CameraSettingsControls>();
            owner.View = view;
            return owner;
        }

        public static void ParsedPostfix(object __instance)
        {
            var owner = Owner(__instance);
            var display = SettingsContract.Get(__instance, "viewRectTab") as Tab;
            if (display == null) throw new MissingMemberException("Camera2 display tab is unavailable");
            display.IsVisible = true;
            owner.Attach();
        }

        public static void SetCamPrefix(object __instance)
        {
            var owner = Owner(__instance);
            if (owner != null) owner.Flush();
        }

        public static void SetCamPostfix(object __instance)
        {
            var owner = Owner(__instance);
            if (owner != null) owner.Refresh();
        }

        public static bool SaveSettingsPrefix(object __instance)
        {
            var owner = Owner(__instance);
            if (owner != null) { owner.Flush(); owner.SaveCurrent(); }
            return false;
        }

        public static void EditPrefix(object __instance, MethodBase __originalMethod, out EditState __state)
        {
            var owner = Owner(__instance);
            __state = new EditState { Previous = EditOwner, Owner = owner };
            var name = __originalMethod.Name;
            var expensive = name == "set_renderScale" || name == "set_antiAliasing" ||
                name == "set_viewRect_x" || name == "set_viewRect_y" ||
                name == "set_viewRect_width" || name == "set_viewRect_height";
            if (!expensive && owner != null && name == "set_type") owner.Flush();
            EditOwner = expensive ? owner : null;
        }

        public static Exception EditFinalizer(Exception __exception, EditState __state)
        {
            if (__state != null)
            {
                EditOwner = __state.Previous;
                if (__state.Owner != null && __exception == null)
                {
                    __state.Owner.Dirty = true;
                    __state.Owner.Refresh();
                }
            }
            return __exception;
        }

        public static bool RebuildPrefix(object __instance)
        {
            if (ExecutingRebuild || EditOwner == null) return true;
            EditOwner.Queue(__instance);
            return false;
        }

        public static IEnumerable<CodeInstruction> ViewportUpdateTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var matches = 0;
            for (var index = 1; index + 1 < list.Count; index++)
            {
                var method = list[index].operand as MethodInfo;
                var field = list[index - 1].operand as FieldInfo;
                var compare = list[index + 1].operand as MethodInfo;
                if (method == null || method.DeclaringType != typeof(Vector2) || method.Name != "get_zero" ||
                    field == null || field.Name != "lastScreenRes" || compare == null || compare.Name != "op_Inequality") continue;
                list[index].opcode = OpCodes.Call;
                list[index].operand = AccessTools.Method(typeof(SettingsCompatibility), "CurrentScreenSize");
                matches++;
            }
            if (matches != 1) throw new InvalidOperationException("Camera2 viewport resize guard changed; expected one lastScreenRes != zero guard");
            return list;
        }

        public static Vector2 CurrentScreenSize() { return new Vector2(Screen.width, Screen.height); }

        public static void DestroyPrefix(object __instance)
        {
            foreach (var owner in Owners.ToArray()) if (owner != null) owner.Cancel(__instance);
            AtomicConfiguration.ForgetCamera(__instance);
        }

        internal static object CurrentCamera { get { return SettingsContract.Get(ViewType, "cam"); } }
        internal static object Scenes { get { return SettingsContract.Get(SceneManagerType, "settings"); } }

        internal static bool Alive(object cam)
        {
            var component = cam as Component;
            return component != null && !(bool)SettingsContract.Get(cam, "destroying");
        }

        internal static void Release()
        {
            foreach (var owner in Owners.ToArray())
                if (owner != null)
                {
                    owner.Flush();
                    if (owner.Dirty) owner.SaveCurrent();
                    UnityEngine.Object.Destroy(owner);
                }
            Releasing = true;
            Owners.Clear();
            EditOwner = null;
        }
    }

    public sealed class CameraSettingsControls : MonoBehaviour
    {
        const string Markup = @"<vertical id='enhancements' preferred-width='90' horizontal-fit='Unconstrained' vertical-fit='PreferredSize' spacing='0.5'>
          <horizontal preferred-height='4' child-expand-width='true'>
            <text id='render-summary' text='' font-size='2.7' preferred-width='50' align='MidlineLeft'/>
            <text id='save-status' text='' font-size='2.5' preferred-width='22' align='MidlineRight'/>
            <button id='retry-save' text='Retry' on-click='retry' preferred-width='14' preferred-height='4'/>
          </horizontal>
          <horizontal id='preset-buttons' preferred-height='5' spacing='3' child-align='MiddleLeft'>
            <button text='Balanced' on-click='balanced' preferred-width='20' preferred-height='5'/>
            <button text='Performance' on-click='performance' preferred-width='20' preferred-height='5'/>
          </horizontal>
        </vertical>";

        readonly Dictionary<object, float> pending = new Dictionary<object, float>();
        [UIComponent("enhancements")] RectTransform root = null;
        [UIComponent("render-summary")] TMP_Text summary = null;
        [UIComponent("save-status")] TMP_Text status = null;
        [UIComponent("retry-save")] Button retry = null;
        [UIComponent("preset-buttons")] RectTransform presets = null;
        internal object View;
        internal bool Dirty;
        bool renderFailed;
        bool? failedPreset;
        object failedPresetCamera;
        float nextRefresh;

        void Awake() { SettingsCompatibility.Owners.Add(this); }

        internal void Attach()
        {
            if (root != null) return;
            var general = GetComponentsInChildren<Tab>(true).FirstOrDefault(value => value.TabName == "General" || value.TabName == "常规");
            if (general == null) throw new MissingMemberException("Camera2 general tab is unavailable");
            var layout = general.GetComponentsInChildren<VerticalLayoutGroup>(true).FirstOrDefault();
            if (layout == null) throw new MissingMemberException("Camera2 general settings layout is unavailable");
            BSMLParser.Instance.Parse(Markup, layout.gameObject, this);
            if (root == null || summary == null || status == null || retry == null || presets == null)
                throw new MissingMemberException("Camera2 quick settings UI was not bound");
            root.SetAsFirstSibling();
            Refresh();
        }

        internal void Queue(object cam)
        {
            if (SettingsCompatibility.Alive(cam)) pending[cam] = Time.realtimeSinceStartup + 0.25f;
        }

        internal void Cancel(object cam)
        {
            pending.Remove(cam);
            if (pending.Count == 0) renderFailed = false;
            if (ReferenceEquals(failedPresetCamera, cam)) { failedPresetCamera = null; failedPreset = null; }
        }

        internal bool Flush()
        {
            var success = true;
            foreach (var cam in pending.Keys.ToArray()) success = FlushOne(cam) && success;
            return success;
        }

        bool FlushOne(object cam)
        {
            if (!SettingsCompatibility.Alive(cam)) { pending.Remove(cam); return true; }
            var previous = SettingsCompatibility.ExecutingRebuild;
            try
            {
                SettingsCompatibility.ExecutingRebuild = true;
                SettingsContract.Invoke(cam, "UpdateRenderTextureAndView");
                pending.Remove(cam);
                renderFailed = false;
                return true;
            }
            catch (Exception error)
            {
                pending[cam] = Single.PositiveInfinity;
                renderFailed = true;
                Runtime.LogError("Camera2 render_target_failed: " + SettingsContract.Get(cam, "name"), error);
                return false;
            }
            finally { SettingsCompatibility.ExecutingRebuild = previous; }
        }

        internal bool SaveCurrent()
        {
            var cam = SettingsCompatibility.CurrentCamera;
            var success = cam == null || AtomicConfiguration.Save(SettingsContract.Get(cam, "settings"));
            success = AtomicConfiguration.Save(SettingsCompatibility.Scenes) && success;
            if (success) Dirty = false;
            Refresh();
            return success;
        }

        [UIAction("balanced")] public void Balanced() { ApplyPreset(false); }
        [UIAction("performance")] public void Performance() { ApplyPreset(true); }
        [UIAction("retry")] public void Retry()
        {
            if (failedPreset.HasValue) ApplyPresetTo(failedPresetCamera, failedPreset.Value);
            Flush();
            AtomicConfiguration.Retry();
            SaveCurrent();
        }

        internal void ApplyPreset(bool performance)
        {
            ApplyPresetTo(SettingsCompatibility.CurrentCamera, performance);
        }

        void ApplyPresetTo(object cam, bool performance)
        {
            if (!SettingsCompatibility.Alive(cam)) return;
            var settings = SettingsContract.Get(cam, "settings");
            if (SettingsContract.Get(settings, "type").ToString() != "FirstPerson") return;
            var previous = SettingsCompatibility.EditOwner;
            var values = PresetValues(performance);
            var originals = new Dictionary<string, object>();
            var applying = false;
            try
            {
                var candidate = JObject.Parse(AtomicConfiguration.SerializeCamera(settings));
                SettingsContract.Invoke(settings, "Unoverriden", new Action(delegate {
                    foreach (var entry in values) originals[entry.Key] = GetPath(settings, entry.Key);
                }));
                foreach (var entry in values)
                {
                    var parts = entry.Key.Split('.');
                    var parent = candidate;
                    for (var index = 0; index + 1 < parts.Length; index++) parent = (JObject)parent[parts[index]];
                    var vector = entry.Value is Vector3 ? (Vector3)entry.Value : Vector3.zero;
                    parent[parts[parts.Length - 1]] = entry.Value is Vector3 ?
                        new JObject(new JProperty("x", vector.x), new JProperty("y", vector.y), new JProperty("z", vector.z)) : JToken.FromObject(entry.Value);
                }
                SettingsContract.Validate(candidate.ToString(), true);
                SettingsCompatibility.EditOwner = this;
                applying = true;
                foreach (var entry in values) SetPath(settings, entry.Key, entry.Value);
                Dirty = true;
                failedPreset = null;
                failedPresetCamera = null;
            }
            catch (Exception error)
            {
                if (applying)
                    foreach (var entry in originals)
                        try { SetPath(settings, entry.Key, entry.Value); }
                        catch (Exception restoreError) { Runtime.LogError("Camera2 preset rollback failed: " + entry.Key, restoreError); }
                Dirty = true;
                failedPreset = performance;
                failedPresetCamera = cam;
                Runtime.LogError("Camera2 preset_apply_failed", error);
                Refresh();
                return;
            }
            finally { SettingsCompatibility.EditOwner = previous; }
            Flush();
            if (ReferenceEquals(cam, SettingsCompatibility.CurrentCamera))
            {
                SaveCurrent();
                foreach (var name in (string[])SettingsContract.Get(SettingsCompatibility.ViewType, "props"))
                    SettingsContract.Invoke(View, "NotifyPropertyChanged", name);
            }
            else AtomicConfiguration.Save(settings);
            Refresh();
        }

        internal static string PresetName(object settings)
        {
            if (SettingsContract.Get(settings, "type").ToString() != "FirstPerson") return String.Empty;
            if (Matches(settings, PresetValues(false))) return "Balanced";
            if (Matches(settings, PresetValues(true))) return "Performance";
            return "Custom";
        }

        static readonly Dictionary<string, object> BalancedValues = BuildPreset(false);
        static readonly Dictionary<string, object> PerformanceValues = BuildPreset(true);
        static Dictionary<string, object> PresetValues(bool performance) { return performance ? PerformanceValues : BalancedValues; }

        static Dictionary<string, object> BuildPreset(bool performance)
        {
            return new Dictionary<string, object> {
                { "FOV", 70f }, { "targetPos", new Vector3(0f, 0f, -0.2f) }, { "targetRot", Vector3.zero },
                { "orthographic", false }, { "renderScale", performance ? 0.8f : 1f }, { "antiAliasing", performance ? 1 : 2 },
                { "Smoothfollow.position", 8f }, { "Smoothfollow.rotation", 4f }, { "Smoothfollow.followReplayPosition", true },
                { "Smoothfollow.pivotingOffset", true }, { "Smoothfollow.limits.rot_z", "0:0" }, { "FPSLimiter.fpsLimit", 0 },
                { "visibleObjects.Avatar", "Hidden" }, { "visibleObjects.Debris", false }, { "visibleObjects.Walls", "Visible" },
                { "visibleObjects.Notes", "Visible" }, { "visibleObjects.UI", true }, { "visibleObjects.Sabers", true },
                { "visibleObjects.CutParticles", true }, { "visibleObjects.Floor", true }, { "visibleObjects.EverythingElse", true }
            };
        }

        static object GetPath(object root, string path)
        {
            foreach (var part in path.Split('.')) root = SettingsContract.Get(root, part);
            return root;
        }

        static void SetPath(object root, string path, object value)
        {
            var parts = path.Split('.');
            for (var index = 0; index + 1 < parts.Length; index++) root = SettingsContract.Get(root, parts[index]);
            SettingsContract.Set(root, parts[parts.Length - 1], value);
        }

        static bool Matches(object settings, Dictionary<string, object> expected)
        {
            foreach (var entry in expected)
            {
                var actual = GetPath(settings, entry.Key);
                if (entry.Value is float) { if (!Near(actual, (float)entry.Value)) return false; }
                else if (entry.Value is string) { if (actual == null || actual.ToString() != (string)entry.Value) return false; }
                else if (!Equals(actual, entry.Value)) return false;
            }
            return true;
        }

        static bool Near(object value, float target)
        {
            return Math.Abs(Convert.ToSingle(value, CultureInfo.InvariantCulture) - target) < 0.001f;
        }

        internal void Refresh()
        {
            if (root == null) return;
            var cam = SettingsCompatibility.CurrentCamera;
            root.gameObject.SetActive(SettingsCompatibility.Alive(cam));
            if (!SettingsCompatibility.Alive(cam)) return;
            var settings = SettingsContract.Get(cam, "settings");
            var preset = PresetName(settings);
            presets.gameObject.SetActive(preset.Length != 0);
            var texture = SettingsContract.Get(cam, "renderTexture") as RenderTexture;
            var dimensions = texture == null ? "" : texture.width + " × " + texture.height;
            var text = preset.Length == 0 ? dimensions : preset + " · " + dimensions;
            // Avoid overwriting translated text every Update. The source is
            // cached separately from TMP's displayed Chinese string.
            SetText(summary, text, ref summarySource);
            var failure = failedPreset.HasValue || renderFailed || AtomicConfiguration.HasFailures;
            retry.gameObject.SetActive(failure);
            var message = failedPreset.HasValue ? "Could not apply preset" : renderFailed ? "Could not update camera" : AtomicConfiguration.HasFailures ? "Could not save settings" :
                Dirty ? "Unsaved changes" : Time.realtimeSinceStartup < AtomicConfiguration.StatusUntil ? AtomicConfiguration.LastStatus : String.Empty;
            SetText(status, message, ref statusSource);
        }

        string summarySource;
        string statusSource;
        static void SetText(TMP_Text target, string value, ref string source)
        {
            if (source == value) return;
            source = value;
            target.text = value;
        }

        void Update()
        {
            if (pending.Count != 0)
                foreach (var item in pending.Where(value => value.Value <= Time.realtimeSinceStartup).ToArray()) FlushOne(item.Key);
            if (Time.realtimeSinceStartup >= nextRefresh)
            {
                nextRefresh = Time.realtimeSinceStartup + 0.15f;
                Refresh();
            }
        }

        void OnDisable()
        {
            if (SettingsCompatibility.Releasing) return;
            Flush();
            if (Dirty) SaveCurrent();
        }

        void OnDestroy()
        {
            pending.Clear();
            SettingsCompatibility.Owners.Remove(this);
        }
    }
}
