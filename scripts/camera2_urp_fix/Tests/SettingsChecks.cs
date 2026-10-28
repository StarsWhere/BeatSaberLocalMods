using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace Camera2RegressionRuntime
{
    public sealed partial class RegressionRunner
    {
        IEnumerator RunSettingsRegression()
        {
            var uiType = FindType("Camera2.UI.SettingsView");
            var controlsType = FindType("Camera2UrpFix.CameraSettingsControls");
            var atomicType = FindType("Camera2UrpFix.AtomicConfiguration");
            var scenesType = FindType("Camera2.Managers.ScenesManager");
            if (uiType == null || controlsType == null || atomicType == null || scenesType == null)
                throw new TypeLoadException("Settings regression types are unavailable");
            observerHarmony.Patch(cam2Type.GetMethod("UpdateRenderTextureAndView", BindingFlags.Instance | BindingFlags.NonPublic),
                transpiler: new HarmonyMethod(typeof(SettingsObserver), "RebuildTranspiler"));

            InvokeStatic(FindType("Camera2.UI.SpaghettiUI"), "ShowFlow");
            var deadline = Time.realtimeSinceStartup + 6f;
            Component view = null;
            Component controls = null;
            while (Time.realtimeSinceStartup < deadline && !Expired())
            {
                view = Resources.FindObjectsOfTypeAll(uiType).OfType<Component>().FirstOrDefault(value => value.gameObject.activeInHierarchy);
                if (view != null) controls = view.GetComponent(controlsType);
                if (controls != null && GetMember(controls, "root") != null) break;
                yield return null;
            }
            if (view == null || controls == null || GetMember(controls, "root") == null)
                throw new InvalidOperationException("Camera2 settings UI did not initialize");
            var first = FindCamByName("FirstPerson");
            var third = FindCamByName(StaticCameraName);
            if (first == null || third == null) throw new InvalidOperationException("Expected camera configurations are absent");
            var setCam = uiType.GetMethod("SetCam", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Invoke(setCam, view, first);
            yield return new WaitForSecondsRealtime(0.3f);

            var before = SettingsObserver.Rebuilds;
            Invoke(controlsType.GetMethod("Balanced"), controls);
            AddCheck("settings.preset-one-rebuild", SettingsObserver.Rebuilds == before + 1,
                "balanced preset rebuilt " + (SettingsObserver.Rebuilds - before) + " render targets");
            var settings = GetMember(first, "settings");
            AddCheck("settings.balanced-values", PresetName(controlsType, settings) == "Balanced" &&
                (float)GetMember(settings, "renderScale") == 1f && (int)GetMember(settings, "antiAliasing") == 2,
                "preset is derived from the native settings object");
            var display = GetMember(view, "viewRectTab");
            AddCheck("settings.display-tab-visible", (bool)GetMember(display, "IsVisible"), "Display is accessible immediately");
            var quickRoot = GetMember(controls, "root") as RectTransform;
            AddCheck("settings.quick-controls-layout", quickRoot != null && quickRoot.GetSiblingIndex() == 0 &&
                quickRoot.parent.GetComponent<VerticalLayoutGroup>() != null,
                "quick controls participate in the native layout rather than overlaying settings");
            yield return new WaitForSecondsRealtime(0.4f);
            var firstCamera = GetMember(first, "UCamera") as Camera;
            AddCheck("settings.balanced-fov", (float)GetMember(settings, "FOV") == 70f &&
                firstCamera != null && Mathf.Abs(firstCamera.fieldOfView - 70f) < 0.01f,
                "balanced preset uses a 70-degree FOV in the rendered camera");
            var summary = GetMember(controls, "summary");
            AddCheck("settings.localized-summary", Convert.ToString(GetMember(summary, "text")).Contains("平衡"),
                Convert.ToString(GetMember(summary, "text")));
            CaptureSettingsView(view, "settings-first-person.png");
            yield return new WaitForSecondsRealtime(0.3f);

            before = SettingsObserver.Rebuilds;
            yield return new WaitForSecondsRealtime(0.15f);
            AddCheck("settings.stable-window-no-updates", SettingsObserver.Rebuilds == before,
                "an unchanged window no longer updates every camera's render target each frame");
            var scale = uiType.GetProperty("renderScale", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            scale.SetValue(view, 1.1f, null);
            scale.SetValue(view, 1.2f, null);
            scale.SetValue(view, 1.3f, null);
            AddCheck("settings.drag-no-immediate-rebuild", SettingsObserver.Rebuilds == before,
                "three consecutive UI changes did not rebuild immediately");
            yield return new WaitForSecondsRealtime(0.12f);
            AddCheck("settings.drag-quiet-period", SettingsObserver.Rebuilds == before, "quiet period is longer than 120 ms");
            yield return new WaitForSecondsRealtime(0.2f);
            AddCheck("settings.drag-debounced-rebuild", SettingsObserver.Rebuilds == before + 1,
                "settled changes rebuilt " + (SettingsObserver.Rebuilds - before) + " render targets");
            AddCheck("settings.custom-mode", PresetName(controlsType, settings) == "Custom", "manual adjustment changed the derived mode");

            before = SettingsObserver.Rebuilds;
            uiType.GetProperty("antiAliasing", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(view, 4, null);
            uiType.GetProperty("viewRect_width", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(view, 0.9f, null);
            Invoke(setCam, view, third);
            AddCheck("settings.switch-flush", SettingsObserver.Rebuilds == before + 1,
                "switching cameras flushed pending anti-aliasing and rectangle changes once");
            yield return new WaitForSecondsRealtime(0.3f);
            var presets = GetMember(controls, "presets") as Component;
            AddCheck("settings.third-person-no-presets", presets != null && !presets.gameObject.activeSelf,
                "first-person preset buttons are hidden for third-person cameras");
            CaptureSettingsView(view, "settings-third-person.png");
            yield return new WaitForSecondsRealtime(0.3f);

            Invoke(setCam, view, first);
            Invoke(controlsType.GetMethod("Performance"), controls);
            AddCheck("settings.performance-values", PresetName(controlsType, settings) == "Performance" &&
                (float)GetMember(settings, "renderScale") == 0.8f && (int)GetMember(settings, "antiAliasing") == 1,
                "performance uses 0.8 scale and one sample (no MSAA)");
            AddCheck("settings.performance-fov", (float)GetMember(settings, "FOV") == 70f &&
                firstCamera != null && Mathf.Abs(firstCamera.fieldOfView - 70f) < 0.01f,
                "performance preset keeps the rendered camera at 70 degrees");
            before = SettingsObserver.Rebuilds;
            Invoke(cam2Type.GetMethod("UpdateRenderTextureAndView", BindingFlags.Instance | BindingFlags.NonPublic), first);
            AddCheck("settings.non-ui-rebuild-synchronous", SettingsObserver.Rebuilds == before + 1,
                "external render-target updates remain synchronous");
            RunAtomicSaveRegression(first, settings, atomicType);
            Invoke(setCam, view, new object[] { null });
            RunSceneBindingsRegression(first, third, scenesType);

            Invoke(setCam, view, first);
            Invoke(controlsType.GetMethod("Balanced"), controls);
            var coordinator = GetMember(FindType("Camera2.UI.Coordinator"), "instance");
            before = SettingsObserver.Rebuilds;
            scale.SetValue(view, 1.1f, null);
            Invoke(coordinator.GetType().GetMethod("BackButtonWasPressed", BindingFlags.Instance | BindingFlags.NonPublic), coordinator, view);
            AddCheck("settings.close-flush", SettingsObserver.Rebuilds == before + 1,
                "closing settings flushes pending changes synchronously");
            yield return new WaitForSecondsRealtime(0.5f);
            AddCheck("settings.pending-cleared", ((IDictionary)GetMember(controls, "pending")).Count == 0,
                "the settings owner has no pending render work after closing");
        }

        void RunAtomicSaveRegression(object cam, object settings, Type atomicType)
        {
            var path = Path.GetFullPath((string)GetMember(cam, "configPath"));
            InvokeStatic(atomicType, "Save", settings);
            var bytes = File.ReadAllBytes(path);
            var validation = FindType("Camera2UrpFix.SettingsContract");
            var json = File.ReadAllText(path);
            foreach (var invalid in new[] {
                json.Replace("\"FOV\": 70.0", "\"FOV\": -1.0"),
                json.Replace("\"type\": \"FirstPerson\"", "\"type\": \"UnknownCamera\"")
            })
            {
                var rejected = false;
                try { InvokeStatic(validation, "Validate", invalid, true); }
                catch (Exception error) { rejected = error.GetType().FullName.StartsWith("Newtonsoft.Json.", StringComparison.Ordinal); }
                AddCheck("settings.contract-rejects-invalid." + (invalid.Contains("UnknownCamera") ? "enum" : "range"), rejected &&
                    BytesEqual(bytes, File.ReadAllBytes(path)), "invalid input is rejected without changing the live camera or saved config");
            }
            var originalToken = GetMember(settings, "overrideToken");
            var tokenType = FindType("Camera2.SDK.OverrideToken");
            var token = Activator.CreateInstance(tokenType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { cam }, null);
            SetMember(token, "FOV", 120f);
            SetMember(settings, "overrideToken", token);
            try
            {
                bool saved;
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    saved = (bool)InvokeStatic(atomicType, "Save", settings);
                    AddCheck("settings.atomic-failure-original-intact", !saved && BytesEqual(bytes, File.ReadAllBytes(path)),
                        "a locked destination rejects replacement and retains its original bytes");
                    AddCheck("settings.atomic-failure-context", (bool)GetMember(atomicType, "HasFailures") &&
                        Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + ".pending.*").Length > 0,
                        "failed writes retain the native object and a recoverable candidate");
                }
                AddCheck("settings.override-restored-after-failure", ReferenceEquals(token, GetMember(settings, "overrideToken")),
                    "temporary SDK overrides remain attached after a failed save");
                saved = (bool)InvokeStatic(atomicType, "Retry");
                AddCheck("settings.atomic-retry", saved && !(bool)GetMember(atomicType, "HasFailures"),
                    "retry succeeds after unlocking and clears failure state");
                AddCheck("settings.override-not-persisted", ReferenceEquals(token, GetMember(settings, "overrideToken")) &&
                    File.ReadAllText(path).Contains("\"FOV\": 70.0"), "saved config contains the base FOV, not the 120-degree override");
                AddCheck("settings.atomic-candidate-cleanup", Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + ".pending.*").Length == 0,
                    "successful retry removes owned recovery candidates");
            }
            finally
            {
                SetMember(settings, "overrideToken", originalToken);
                Invoke(tokenType.GetMethod("Close", BindingFlags.Instance | BindingFlags.Public), token);
            }
        }

        void RunSceneBindingsRegression(object first, object third, Type scenesType)
        {
            var settings = GetMember(scenesType, "settings");
            var scenes = (IDictionary)GetMember(settings, "scenes");
            var sceneEnum = FindType("Camera2.Configuration.SceneTypes");
            foreach (var name in new[] { "Playing", "Playing360", "PlayingModmap", "PlayingMulti", "SpectatingMulti", "Replay", "FPFC" })
            {
                var cameras = (IList)scenes[Enum.Parse(sceneEnum, name)];
                AddCheck("settings.scene." + name, cameras.Count == 1 && (string)cameras[0] == "FirstPerson",
                    "the scene uses only FirstPerson");
            }
            var bindings = (IDictionary)GetMember(settings, "customSceneBindings");
            var custom = (IDictionary)GetMember(settings, "customScenes");
            var f2 = (string)bindings[KeyCode.F2];
            var f3 = (string)bindings[KeyCode.F3];
            AddCheck("settings.scene-key-bindings", custom.Contains(f2) && custom.Contains(f3) &&
                (string)((IList)custom[f2])[0] == "FirstPerson" && (string)((IList)custom[f3])[0] == StaticCameraName,
                "F2 and F3 refer to the existing cameras");
            InvokeStatic(scenesType, "SwitchToCustomScene", f3);
            AddCheck("settings.scene-third-person-switch", ((Component)third).gameObject.activeSelf && !((Component)first).gameObject.activeSelf,
                "custom third-person scene activates one camera");
            InvokeStatic(scenesType, "SwitchToCustomScene", f2);
            AddCheck("settings.scene-first-person-switch", ((Component)first).gameObject.activeSelf && !((Component)third).gameObject.activeSelf,
                "custom first-person scene activates one camera");
            InvokeStatic(scenesType, "LoadGameScene", null, false);
            AddCheck("settings.scene-default-restored", !(bool)GetMember(scenesType, "isOnCustomScene") &&
                (bool)GetMember(settings, "autoswitchFromCustom"), "default scene resumes after a scene transition or F1");
        }

        object FindCamByName(string name)
        {
            return Resources.FindObjectsOfTypeAll(cam2Type).FirstOrDefault(value => (string)GetMember(value, "name") == name);
        }

        void CaptureSettingsView(Component view, string filename)
        {
            var rect = view.GetComponent<RectTransform>();
            if (rect == null) throw new InvalidOperationException("Settings view has no rectangle");
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            var center = (corners[0] + corners[2]) * 0.5f;
            var width = Vector3.Distance(corners[1], corners[2]);
            var height = Vector3.Distance(corners[0], corners[1]);
            var owner = new GameObject("__Camera2Regression.SettingsCapture");
            var camera = owner.AddComponent<Camera>();
            var original = nativeMainCamera == null ? FindNativeMainCamera() : nativeMainCamera;
            if (original != null) camera.CopyFrom(original);
            camera.enabled = false;
            camera.fieldOfView = 50f;
            camera.aspect = 1920f / 1080f;
            camera.orthographic = false;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 100f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            var tangent = Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f);
            var distance = Mathf.Max(height / (2f * tangent), width / (2f * tangent * camera.aspect)) * 1.4f;
            camera.transform.position = center - rect.forward * distance;
            camera.transform.rotation = Quaternion.LookRotation(rect.forward, rect.up);
            var data = owner.AddComponent<UniversalAdditionalCameraData>();
            data.allowXRRendering = false;
            data.renderPostProcessing = false;
            var target = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);
            var image = new Texture2D(1920, 1080, TextureFormat.RGBA32, false);
            var previous = RenderTexture.active;
            try
            {
                if (!SubmitStandardRequest(camera, target)) throw new NotSupportedException("Settings UI capture render request is unavailable");
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0, false);
                image.Apply(false, false);
                File.WriteAllBytes(Path.Combine(runtimeDirectory, filename), image.EncodeToPNG());
                var visible = image.GetPixels32().Count(value => value.r + value.g + value.b > 90);
                AddCheck("settings.capture." + filename, visible > 1000, "UI capture contains " + visible + " visible pixels");
            }
            finally
            {
                RenderTexture.active = previous;
                target.Release();
                UnityEngine.Object.Destroy(target);
                UnityEngine.Object.Destroy(image);
                UnityEngine.Object.Destroy(owner);
            }
        }

        static string PresetName(Type controls, object settings)
        {
            return (string)InvokeStatic(controls, "PresetName", settings);
        }

        static void SetMember(object target, string name, object value)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var property = target.GetType().GetProperty(name, flags);
            if (property != null) property.SetValue(target, value, null);
            else target.GetType().GetField(name, flags).SetValue(target, value);
        }

        static class SettingsObserver
        {
            internal static int Rebuilds;
            public static void CountRebuild() { Rebuilds++; }
            public static IEnumerable<CodeInstruction> RebuildTranspiler(IEnumerable<CodeInstruction> instructions)
            {
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SettingsObserver), "CountRebuild"));
                foreach (var instruction in instructions) yield return instruction;
            }
        }
    }
}
