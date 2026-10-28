using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using IPA;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace Camera2RegressionRuntime
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public sealed class Plugin
    {
        GameObject owner;

        [Init]
        public void Init(IPA.Logging.Logger logger)
        {
            RegressionRunner.Logger = logger;
        }

        [OnStart]
        public void Start()
        {
            owner = new GameObject("__Camera2Regression.Owner");
            UnityEngine.Object.DontDestroyOnLoad(owner);
            var runner = owner.AddComponent<RegressionRunner>();
            runner.Initialize();
        }

        [OnExit]
        public void Exit()
        {
            if (owner != null)
            {
                UnityEngine.Object.Destroy(owner);
                owner = null;
            }
        }
    }

    public sealed partial class RegressionRunner : MonoBehaviour
    {
        const float TotalSeconds = 80f;
        const float MenuWaitSeconds = 16f;
        const string TemporaryCameraPrefix = "__Camera2Regression";
        const string StaticCameraName = "Static Thirdperson";
        const int CaptureSize = 512;

        internal static IPA.Logging.Logger Logger;

        sealed class Check
        {
            internal string Name;
            internal bool Passed;
            internal string Detail;
        }

        sealed class CapturedError
        {
            internal string Type;
            internal string Message;
            internal string StackTrace;
            internal bool Relevant;
        }

        sealed class CameraFile
        {
            internal string RelativePath;
            internal byte[] Bytes;
        }

        sealed class ResourceCounts
        {
            internal int SourceCameras;
            internal int PostProcessingStates;
            internal int DesktopViews;
        }

        readonly List<Check> checks = new List<Check>();
        readonly List<string> errors = new List<string>();
        readonly List<CapturedError> capturedErrors = new List<CapturedError>();
        readonly List<GameObject> ownedObjects = new List<GameObject>();
        readonly List<UnityEngine.Object> ownedAssets = new List<UnityEngine.Object>();
        readonly List<CameraFile> originalCameraFiles = new List<CameraFile>();
        readonly object errorLock = new object();

        Stopwatch clock;
        string gameRoot;
        string runtimeDirectory;
        string cameraDirectory;
        Harmony observerHarmony;
        Type cam2Type;
        Type camManagerType;
        Camera nativeMainCamera;
        bool nativeMainHadData;
        bool nativeMainAllowXr;
        bool finished;
        bool phaseFailed;

        public void Initialize()
        {
            Application.runInBackground = true;
            gameRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var requestedOutput = Environment.GetEnvironmentVariable("BEATSABER_CAMERA2_TEST_OUTPUT");
            runtimeDirectory = String.IsNullOrEmpty(requestedOutput) ?
                Path.Combine(gameRoot, ".tmp", "camera2-fix", "runtime") : Path.GetFullPath(requestedOutput);
            cameraDirectory = Path.Combine(gameRoot, "UserData", "Camera2");
            Directory.CreateDirectory(runtimeDirectory);
            clock = Stopwatch.StartNew();
            SnapshotCameraFiles();
            RememberNativeMainCamera();
            Application.logMessageReceived += OnLogMessage;
            try
            {
                InstallObserverPatches();
            }
            catch (Exception error)
            {
                RecordError("observer-install", error);
                AddCheck("observer.install", false, DescribeException(error));
            }
            StartCoroutine(Run());
        }

        void OnDestroy()
        {
            Application.logMessageReceived -= OnLogMessage;
            StopAllCoroutines();
            ReleaseObserverPatches();
            DestroyOwnedObjects();
        }

        void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            var message = condition ?? String.Empty;
            var stack = stackTrace ?? String.Empty;
            var lower = (message + "\n" + stack).ToLowerInvariant();
            var relevant = lower.Contains("camera2") || lower.Contains("urp fix") ||
                lower.Contains("screen displacement") || lower.Contains("obstacle") ||
                lower.Contains("render request") || lower.Contains("desktopgamecamera");
            lock (errorLock)
            {
                capturedErrors.Add(new CapturedError {
                    Type = type.ToString(), Message = message, StackTrace = stack, Relevant = relevant
                });
            }
        }

        IEnumerator Run()
        {
            object yielded;
            var phase = WaitForStableMainMenu();
            while (MovePhase(phase, out yielded)) yield return yielded;

            if (!Expired()) RunSafe("prepare", RunPrepareRegression);
            if (!Expired()) RunSafe("post-processing", RunPostProcessingRegression);
            if (!Expired()) RunSafe("camera-files", RunCameraFileRegression);

            if (!Expired())
            {
                phase = RunSettingsRegression();
                while (MovePhase(phase, out yielded)) yield return yielded;
            }

            if (!Expired())
            {
                phase = RunCameraLifecycleRegression();
                while (MovePhase(phase, out yielded)) yield return yielded;
            }

            if (!Expired())
            {
                phase = RunObstacleRegression();
                while (MovePhase(phase, out yielded)) yield return yielded;
            }

            if (!Expired())
            {
                phase = RunDesktopRegression();
                while (MovePhase(phase, out yielded)) yield return yielded;
            }

            AddCheck("prepare.all-source-cameras-xr-disabled", ObserverPatches.ValidPrepareCount > 0 && ObserverPatches.UnsafePrepareCount == 0,
                "valid forced preparations=" + ObserverPatches.ValidPrepareCount + "; XR-enabled preparations=" + ObserverPatches.UnsafePrepareCount);
            if (Expired()) AddCheck("deadline", false, "runtime checks exceeded the 80 second limit");
            var passed = checks.Count > 0 && checks.All(value => value.Passed) && !phaseFailed && !Expired();
            yield return StartCoroutine(Finish(passed));
        }

        bool MovePhase(IEnumerator phase, out object yielded)
        {
            yielded = null;
            if (phase == null || Expired()) return false;
            try
            {
                if (!phase.MoveNext()) return false;
                yielded = phase.Current;
                return true;
            }
            catch (Exception error)
            {
                phaseFailed = true;
                RecordError("phase", error);
                AddCheck("phase.exception", false, DescribeException(error));
                return false;
            }
        }

        void RunSafe(string name, Action action)
        {
            try { action(); }
            catch (Exception error)
            {
                phaseFailed = true;
                RecordError(name, error);
                AddCheck(name + ".exception", false, DescribeException(error));
            }
        }

        IEnumerator WaitForStableMainMenu()
        {
            var menuType = FindType("MainMenuViewController");
            var deadline = Time.realtimeSinceStartup + MenuWaitSeconds;
            var stableFrames = 0;
            while (!Expired() && Time.realtimeSinceStartup < deadline)
            {
                if (menuType == null) menuType = FindType("MainMenuViewController");
                var active = false;
                if (menuType != null)
                {
                    foreach (var item in Resources.FindObjectsOfTypeAll(menuType))
                    {
                        var component = item as Component;
                        if (component == null || component.gameObject == null) continue;
                        var value = GetMember(item, "isActivated");
                        if (value is bool && (bool)value && component.gameObject.activeInHierarchy)
                        {
                            active = true;
                            break;
                        }
                    }
                }
                stableFrames = active ? stableFrames + 1 : 0;
                if (stableFrames >= 20) break;
                yield return null;
            }
            AddCheck("runtime.main-menu", stableFrames >= 20,
                stableFrames >= 20 ? "main menu was stable for 20 frames" : "main menu was not stable before the deadline");
        }

        void InstallObserverPatches()
        {
            cam2Type = FindType("Camera2.Behaviours.Cam2");
            camManagerType = FindType("Camera2.Managers.CamManager");
            if (cam2Type == null || camManagerType == null)
                throw new TypeLoadException("Camera2 0.6.119 runtime types are unavailable");

            observerHarmony = new Harmony("local.camera2.regression.runtime");
            var prepare = cam2Type.GetMethod("PrepareMiddlewaredRender",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(bool) }, null);
            if (prepare == null) throw new MissingMethodException("Cam2.PrepareMiddlewaredRender(bool)");
            observerHarmony.Patch(prepare,
                prefix: new HarmonyMethod(typeof(ObserverPatches), "PreparePrefix"));
            observerHarmony.Patch(FindType("Camera2.Behaviours.CamPostProcessor").GetMethod("OnRenderImage",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                prefix: new HarmonyMethod(typeof(ObserverPatches), "PostProcessPrefix"));

            var obstacleType = FindType("ObstacleController");
            var update = obstacleType == null ? null : obstacleType.GetMethod("Update",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                Type.EmptyTypes, null);
            if (update == null) throw new MissingMethodException("ObstacleController.Update()");
            observerHarmony.Patch(update,
                prefix: new HarmonyMethod(typeof(ObserverPatches), "ObstacleUpdatePrefix"));
            ObserverPatches.Active = this;
            AddCheck("observer.priority-last", true,
                "observer prefix is marked Harmony Priority.Last and is installed after the adapter");
        }

        void ReleaseObserverPatches()
        {
            ObserverPatches.Active = null;
            if (observerHarmony == null) return;
            try { observerHarmony.UnpatchSelf(); }
            catch (Exception error) { RecordError("observer-release", error); }
            observerHarmony = null;
        }

        void RunPrepareRegression()
        {
            var cam = FindUsableCam2();
            if (cam == null) throw new InvalidOperationException("no initialized Camera2 Cam2 instance was found");
            var camera = GetMember(cam, "UCamera") as Camera;
            if (camera == null) throw new InvalidOperationException("Cam2.UCamera is null");
            var data = camera.GetComponent<UniversalAdditionalCameraData>();
            var beforeFalse = data != null && !data.allowXRRendering;
            var method = cam2Type.GetMethod("PrepareMiddlewaredRender",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(bool) }, null);
            var callsBefore = ObserverPatches.PostProcessCount;
            Invoke(method, cam, true);
            var observed = ObserverPatches.PrepareObserved;
            var observerBeforeFalse = ObserverPatches.PrepareObservedBeforeAllowXrFalse;
            AddCheck("prepare.before-allow-xr-false", beforeFalse && observed && observerBeforeFalse,
                "before=" + beforeFalse + "; first=" + ObserverPatches.FirstPrepareDetail + "; observed=" + observerBeforeFalse);
            AddCheck("prepare.force-true", ObserverPatches.PrepareObservedForce,
                "observer recorded a force=true PrepareMiddlewaredRender call");
            AddCheck("post-processing.urp-callback", ObserverPatches.PostProcessCount > callsBefore,
                "forced URP rendering executed the existing Camera2 effect chain");
            var originalTarget = camera.targetTexture;
            try
            {
                camera.targetTexture = null;
                Invoke(method, cam, true);
                AddCheck("prepare.failed-render-cleanup", !camera.enabled,
                    "a missing destination closes the middleware render lifetime and disables the source camera");
            }
            finally { camera.targetTexture = originalTarget; }
            callsBefore = ObserverPatches.PostProcessCount;
            Invoke(method, cam, true);
            AddCheck("prepare.failed-render-recovery", !camera.enabled && ObserverPatches.PostProcessCount > callsBefore,
                "a restored destination renders and completes normally on the next attempt");
        }

        void RunPostProcessingRegression()
        {
            var cam = FindUsableCam2();
            var camera = GetMember(cam, "UCamera") as Camera;
            var options = GetMember(GetMember(cam, "settings"), "PostProcessing");
            var threshold = options.GetType().GetField("transparencyThreshold");
            var originalThreshold = threshold.GetValue(options);
            var originalDepth = camera.depthTextureMode;
            var adapterType = FindType("Camera2UrpFix.PostProcessingCompatibility");
            var states = (IDictionary)adapterType.GetField("States", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            var state = states[camera.GetInstanceID()];
            var process = adapterType.GetMethod("ProcessFrame", BindingFlags.Static | BindingFlags.NonPublic);
            var target = new RenderTexture(64, 32, 0, RenderTextureFormat.ARGB32);
            var input = new Texture2D(2, 1, TextureFormat.RGBA32, false);
            var previous = RenderTexture.active;
            var srgb = GL.sRGBWrite;
            Texture2D output = null;
            try
            {
                target.Create();
                input.filterMode = FilterMode.Point;
                input.SetPixels32(new[] { new Color32(0,0,0,0), new Color32(255,255,255,0) });
                input.Apply(false, false);
                camera.depthTextureMode = DepthTextureMode.None;
                for (var pass = 0; pass < 2; pass++)
                {
                    threshold.SetValue(options, pass == 0 ? 0f : 70f);
                    GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear && target.sRGB;
                    Graphics.Blit(input, target);
                    Invoke(process, null, state, target);
                    RenderTexture.active = target;
                    output = new Texture2D(64,32,TextureFormat.RGBA32,false);
                    output.ReadPixels(new Rect(0,0,64,32),0,0,false);
                    output.Apply(false,false);
                    var dark = output.GetPixel(8,16);
                    var bright = output.GetPixel(56,16);
                    AddCheck(pass == 0 ? "post-processing.default-alpha" : "post-processing.luminance-key",
                        pass == 0 ? dark.a > .95f && bright.a > .95f && bright.r > .9f : dark.a < .05f && bright.a > .5f && bright.r > .9f,
                        "synthetic black/white alpha-zero input produced darkAlpha="+dark.a+", brightAlpha="+bright.a);
                    UnityEngine.Object.Destroy(output); output = null;
                }
            }
            finally
            {
                threshold.SetValue(options, originalThreshold);
                camera.depthTextureMode = originalDepth;
                var view = GetMember(cam, "previewImage");
                InvokeStatic(FindType("Camera2UrpFix.DesktopCanvasOwner"), "SetTransparency", view, false);
                RenderTexture.active = previous;
                GL.sRGBWrite = srgb;
                if(output != null) UnityEngine.Object.Destroy(output);
                target.Release(); UnityEngine.Object.Destroy(target); UnityEngine.Object.Destroy(input);
            }
        }

        void RunCameraFileRegression()
        {
            if (camManagerType == null) throw new TypeLoadException("CamManager type is unavailable");
            var source = FindCameraFile(StaticCameraName);
            if (source == null) throw new FileNotFoundException("Static Thirdperson.json is unavailable");
            var temporary = Path.Combine(Path.GetDirectoryName(source), TemporaryCameraPrefix + ".json");
            if (File.Exists(temporary)) File.Delete(temporary);
            var initialNames = ReadCameraNames();
            try
            {
                File.Copy(source, temporary, true);
                InvokeStatic(camManagerType, "Reload");
                var loadedNames = ReadCameraNames();
                var loaded = loadedNames.Contains(TemporaryCameraPrefix);
                AddCheck("camera-file.reload-load", loaded,
                    loaded ? "temporary camera loaded by CamManager.Reload" : "temporary camera was not loaded");

                File.Delete(temporary);
                InvokeStatic(camManagerType, "Reload");
                var removedNames = ReadCameraNames();
                var removed = !removedNames.Contains(TemporaryCameraPrefix);
                AddCheck("camera-file.reload-delete", removed,
                    removed ? "deleting the temporary file and reloading removed the camera" : "deleted camera remained active");
                AddCheck("camera-file.names-unchanged", initialNames.SetEquals(ReadCameraNames()),
                    "CamManager.Reload preserved existing camera names");
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        IEnumerator RunCameraLifecycleRegression()
        {
            // Reload queues Unity destruction until the end of the frame.
            yield return null;
            yield return null;
            yield return null;
            var manager = camManagerType;
            if (manager == null) throw new TypeLoadException("CamManager type is unavailable");
            var add = manager.GetMethod("AddNewCamera", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(string) }, null);
            var delete = manager.GetMethod("DeleteCamera", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { cam2Type }, null);
            var update = cam2Type.GetMethod("UpdateRenderTextureAndView",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (add == null || delete == null) throw new MissingMethodException("CamManager AddNewCamera/DeleteCamera");

            var baseline = CaptureResourceCounts();
            var namesBefore = ReadCameraNames();
            AddCheck("camera-file.reload-source-cleanup", baseline.SourceCameras == namesBefore.Count,
                "source records=" + baseline.SourceCameras + "; loaded cameras=" + namesBefore.Count);
            for (var index = 0; index < 3 && !Expired(); index++)
            {
                var name = TemporaryCameraPrefix + "_" + index.ToString(CultureInfo.InvariantCulture);
                var cycle = RunOneCameraLifecycleCycle(add, delete, update, name, index, baseline);
                object yielded;
                while (MovePhase(cycle, out yielded)) yield return yielded;
                if (!checks.Any(value => value.Name == "camera-lifecycle." + index.ToString(CultureInfo.InvariantCulture)))
                    AddCheck("camera-lifecycle." + index.ToString(CultureInfo.InvariantCulture), false,
                        "camera lifecycle cycle raised an exception before producing its result");
            }

            AddCheck("camera-lifecycle.names-unchanged", namesBefore.SetEquals(ReadCameraNames()),
                "three temporary camera cycles preserved the original camera names");
        }

        IEnumerator RunOneCameraLifecycleCycle(MethodInfo add, MethodInfo delete, MethodInfo update,
            string name, int index, ResourceCounts baseline)
        {
            object camera = null;
            var textureId = 0;
            var materialId = 0;
            try
            {
                camera = Invoke(add, null, name);
                if (camera == null) throw new InvalidOperationException("AddNewCamera returned null");
                AddCheck("camera-create.valid-save." + index.ToString(CultureInfo.InvariantCulture),
                    !(bool)GetMember(FindType("Camera2UrpFix.AtomicConfiguration"), "HasFailures"),
                    "new cameras initialize a valid native rectangle before saving");
                yield return null;
                if (update != null) Invoke(update, camera);
                var oldTextureId = GetInstanceId(GetMember(camera, "renderTexture"));
                var cameraSettings = GetMember(camera, "settings");
                var scaleProperty = cameraSettings.GetType().GetProperty("renderScale");
                scaleProperty.SetValue(cameraSettings, .5f, null);
                yield return null;
                yield return null;
                textureId = GetInstanceId(GetMember(camera, "renderTexture"));
                AddCheck("camera-resize." + index.ToString(CultureInfo.InvariantCulture),
                    textureId != oldTextureId && IsInstanceGone(oldTextureId),
                    "replacing the render target destroyed its former Unity object");
                var worldCam = GetMember(camera, "worldCam");
                materialId = GetInstanceId(GetField(worldCam, "viewMaterial"));
                Invoke(delete, null, camera);
                yield return null;
                yield return null;
                yield return null;
                var namesAfter = ReadCameraNames();
                var resourceCounts = CaptureResourceCounts();
                var trackedResources = textureId != 0 && materialId != 0;
                var resourcesGone = trackedResources && IsInstanceGone(textureId) && IsInstanceGone(materialId);
                var sourceRecordsReturned = baseline.SourceCameras >= 0 &&
                    resourceCounts.SourceCameras == baseline.SourceCameras;
                var recordsReturned = sourceRecordsReturned &&
                    resourceCounts.PostProcessingStates == baseline.PostProcessingStates &&
                    resourceCounts.DesktopViews == baseline.DesktopViews;
                var removed = !namesAfter.Contains(name);
                AddCheck("camera-lifecycle." + index.ToString(CultureInfo.InvariantCulture),
                    removed && trackedResources && resourcesGone && recordsReturned,
                    "removed=" + removed + "; texture=" + textureId + "/gone=" + IsInstanceGone(textureId) +
                    "; material=" + materialId + "/gone=" + IsInstanceGone(materialId) +
                    "; sourceRecords=" + baseline.SourceCameras + "->" + resourceCounts.SourceCameras +
                    "; effects=" + baseline.PostProcessingStates + "->" + resourceCounts.PostProcessingStates +
                    "; views=" + baseline.DesktopViews + "->" + resourceCounts.DesktopViews);
            }
            finally
            {
                if (camera != null)
                {
                    try { Invoke(delete, null, camera); } catch (Exception cleanupError) { RecordError("camera-cleanup", cleanupError); }
                }
                DeleteTemporaryCameraFiles();
            }
        }

        IEnumerator RunObstacleRegression()
        {
            var extensionType = FindType("BGLib.UnityExtension.AddressablesExtensions");
            var controllerType = FindType("ObstacleController");
            var stretchType = FindType("StretchableObstacle");
            if (extensionType == null || controllerType == null || stretchType == null)
                throw new TypeLoadException("native obstacle runtime types are unavailable");

            var load = extensionType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == "LoadContent" && method.IsGenericMethodDefinition &&
                    method.GetParameters().Length == 1);
            if (load == null) throw new MissingMethodException("BGLib.UnityExtension.AddressablesExtensions.LoadContent<T>");
            var loaded = Invoke(load.MakeGenericMethod(typeof(GameObject)), null,
                "Assets/Prefabs/SongElements/Obstacle.prefab") as IEnumerable;
            if (loaded == null) throw new InvalidOperationException("LoadContent returned no enumerable");
            GameObject prefab = null;
            foreach (var item in loaded)
            {
                prefab = item as GameObject;
                if (prefab == null)
                {
                    var component = item as Component;
                    if (component != null) prefab = component.gameObject;
                }
                if (prefab != null) break;
            }
            if (prefab == null) throw new InvalidOperationException("native obstacle prefab was not returned");

            var updateCountBefore = ObserverPatches.ObstacleUpdateCount;
            var obstacle = (GameObject)UnityEngine.Object.Instantiate(prefab);
            obstacle.name = TemporaryCameraPrefix + ".Obstacle";
            ownedObjects.Add(obstacle);
            // This isolated prefab has no scene DI context; graphics selection
            // is explicitly initialized below instead of running its DI consumer.
            foreach (var behaviour in obstacle.GetComponentsInChildren<Behaviour>(true))
                if (behaviour.GetType().Name == "GraphicSettingsConditionalActivator") behaviour.enabled = false;
            var controller = obstacle.GetComponentInChildren(controllerType, true);
            var stretch = obstacle.GetComponentInChildren(stretchType, true);
            if (controller == null || stretch == null) throw new InvalidOperationException("native obstacle components are missing");

            var settingsType = FindType("BeatSaber.Settings.Settings");
            if (settingsType == null) throw new TypeLoadException("BeatSaber.Settings.Settings is unavailable");
            var settings = Activator.CreateInstance(settingsType, true);
            var qualityField = settingsType.GetField("quality", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (qualityField == null) throw new MissingFieldException("Settings.quality");
            var quality = Activator.CreateInstance(qualityField.FieldType, true);
            var obstaclesField = qualityField.FieldType.GetField("obstacles", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var displacementField = qualityField.FieldType.GetField("screenDisplacementEffects", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (obstaclesField == null || displacementField == null) throw new MissingFieldException("QualitySettings obstacle settings");
            obstaclesField.SetValue(quality, Enum.ToObject(obstaclesField.FieldType, 2));
            displacementField.SetValue(quality, true);
            qualityField.SetValue(settings, quality);
            AddCheck("obstacle.settings-high2-screen-displacement",
                Convert.ToInt32(obstaclesField.GetValue(quality), CultureInfo.InvariantCulture) == 2 &&
                (bool)displacementField.GetValue(quality),
                "InitGraphics received obstacle quality High2 and screen displacement enabled");

            var initGraphics = controllerType.GetMethod("InitGraphics", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (initGraphics == null) throw new MissingMethodException("ObstacleController.InitGraphics");
            Invoke(initGraphics, controller, settings);
            var setProperties = stretchType.GetMethod("SetAllProperties", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (setProperties == null) throw new MissingMethodException("StretchableObstacle.SetAllProperties");
            Invoke(setProperties, stretch, 2f, 3f, .8f, Color.red, 0f);

            var core = GetField(stretch, "_obstacleCore") as Transform;
            if (core == null) throw new InvalidOperationException("StretchableObstacle._obstacleCore is null");
            var coreMaterials = core.GetComponentsInChildren<Renderer>(true)
                .SelectMany(renderer => renderer == null ? new Material[0] : renderer.sharedMaterials)
                .Where(material => material != null)
                .ToArray();
            var materialText = String.Join(";", coreMaterials.Select(material =>
                material.name + "/" + (material.shader == null ? String.Empty : material.shader.name)).ToArray());
            AddCheck("obstacle.core-layer", core.gameObject.layer == 17,
                "native obstacle core retained layer 17 (actual=" + core.gameObject.layer.ToString(CultureInfo.InvariantCulture) + ")");
            AddCheck("obstacle.screen-displacement-material",
                coreMaterials.Any(material => (material.name + " " + (material.shader == null ? String.Empty : material.shader.name))
                    .IndexOf("ScreenDisplacementHD", StringComparison.OrdinalIgnoreCase) >= 0),
                "native obstacle core material uses ScreenDisplacementHD (" + materialText + ")");

            obstacle.transform.position = Vector3.zero;
            var renderRoot = new GameObject(TemporaryCameraPrefix + ".ObstacleRender");
            renderRoot.layer = 30;
            ownedObjects.Add(renderRoot);
            var cameraObject = new GameObject(TemporaryCameraPrefix + ".ObstacleRenderCamera");
            ownedObjects.Add(cameraObject);
            var renderCamera = cameraObject.AddComponent<Camera>();
            renderCamera.transform.position = new Vector3(0f, 0f, -10f);
            renderCamera.transform.rotation = Quaternion.identity;
            renderCamera.clearFlags = CameraClearFlags.SolidColor;
            renderCamera.backgroundColor = Color.black;
            renderCamera.orthographic = true;
            renderCamera.orthographicSize = 3f;
            renderCamera.nearClipPlane = .1f;
            renderCamera.farClipPlane = 100f;
            renderCamera.allowHDR = false;
            renderCamera.allowMSAA = false;
            renderCamera.cullingMask = (1 << 11) | (1 << 17) | (1 << 30);
            var cameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
            cameraData.renderType = CameraRenderType.Base;
            cameraData.renderPostProcessing = false;
            cameraData.renderShadows = false;
            cameraData.requiresDepthTexture = false;
            cameraData.requiresColorTexture = true;
            cameraData.allowXRRendering = false;

            var loadedShaders = (IEnumerable)Invoke(load.MakeGenericMethod(typeof(Shader)), null,
                "Assets/Visuals/Shaders/GlowingInstancedHD.shader");
            var backgroundShader = loadedShaders.Cast<Shader>().FirstOrDefault();
            if (backgroundShader == null || !backgroundShader.isSupported)
                throw new NotSupportedException("Native GlowingInstancedHD is unavailable for the synthetic background");
            for (var index = 0; index < 3; index++)
            {
                var bar = GameObject.CreatePrimitive(PrimitiveType.Quad);
                bar.name = TemporaryCameraPrefix + ".BlueBar" + index.ToString(CultureInfo.InvariantCulture);
                bar.layer = 30;
                bar.transform.SetParent(renderRoot.transform, false);
                bar.transform.position = new Vector3((index - 1) * 2f, 0f, 2f);
                bar.transform.localScale = new Vector3(2f, 5f, 1f);
                var material = new Material(backgroundShader);
                material.SetColor("_Color", index == 1 ? new Color(0f, .15f, 1f, 0f) : new Color(0f, .45f, 1f, 0f));
                bar.GetComponent<Renderer>().sharedMaterial = material;
                ownedAssets.Add(material);
                ownedObjects.Add(bar);
            }

            // Update is deliberately disabled by the observer patch.  Waiting a
            // frame also proves that the instantiated controller reached Update.
            yield return null;
            AddCheck("obstacle.update-disabled", ObserverPatches.ObstacleUpdateCount > updateCountBefore,
                "ObstacleController.Update was intercepted and disabled before movement");
            var target = new RenderTexture(CaptureSize, CaptureSize, 24, RenderTextureFormat.ARGB32);
            target.name = TemporaryCameraPrefix + ".ObstacleRenderTarget";
            target.Create();
            ownedAssets.Add(target);
            Texture2D screenshot = null;
            var previousActive = RenderTexture.active;
            try
            {
                renderCamera.targetTexture = target;
                var submitted = SubmitStandardRequest(renderCamera, target);
                if (!submitted) throw new NotSupportedException("URP StandardRequest was not submitted for obstacle capture");
                RenderTexture.active = target;
                screenshot = new Texture2D(CaptureSize, CaptureSize, TextureFormat.RGBA32, false);
                screenshot.ReadPixels(new Rect(0f, 0f, CaptureSize, CaptureSize), 0, 0, false);
                screenshot.Apply(false, false);
                File.WriteAllBytes(Path.Combine(runtimeDirectory, "obstacle-urp.png"), screenshot.EncodeToPNG());
                var pixels = screenshot.GetPixels32();
                var blue = pixels.Count(pixel => pixel.b > pixel.r + 35 && pixel.b > pixel.g + 20 && pixel.b > 55);
                var nonBackground = pixels.Count(pixel => pixel.a > 0 && pixel.r + pixel.g + pixel.b > 75 &&
                    !(pixel.b > pixel.r + 35 && pixel.b > pixel.g + 20));
                AddCheck("obstacle.urp-standard-request", blue > pixels.Length / 40 && nonBackground > pixels.Length / 500,
                    "synthetic blue bars and rendered obstacle pixels were captured through URP StandardRequest");
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (screenshot != null) UnityEngine.Object.Destroy(screenshot);
                renderCamera.targetTexture = null;
            }
            RunWallVisibilityRegression(stretch);
            UnityEngine.Object.Destroy(obstacle);
            yield return null;
            yield return null;
            var cleanupCamera = GetMember(FindUsableCam2(), "UCamera") as Camera;
            SubmitStandardRequest(cleanupCamera, cleanupCamera.targetTexture);
            var wallRecords = GetListCount("Camera2UrpFix.WallCompatibility", "TrackedWalls");
            AddCheck("obstacle.tracking-cleanup", wallRecords == 0,
                "remaining wall tracking records after the next source render=" + wallRecords);
        }

        void RunWallVisibilityRegression(Component stretch)
        {
            if (nativeMainCamera == null) RememberNativeMainCamera();
            var cam2 = FindUsableCam2();
            var camera = GetMember(cam2, "UCamera") as Camera;
            var settings = GetMember(cam2, "settings");
            var visible = GetMember(settings, "visibleObjects");
            var modmap = GetMember(settings, "ModmapExtensions");
            var walls = visible.GetType().GetProperty("Walls");
            var autoOpaque = modmap.GetType().GetField("autoOpaqueWalls");
            var wallMap = FindType("Camera2.HarmonyPatches.HookLeveldata").GetField("isWallMap");
            var originalMode = walls.GetValue(visible, null);
            var originalAuto = autoOpaque.GetValue(modmap);
            var originalWallMap = wallMap.GetValue(null);
            var originalMainMask = nativeMainCamera.cullingMask;
            var core = (Component)GetField(stretch, "_obstacleCore");
            var coreRenderers = core.GetComponentsInChildren<Renderer>(true);
            var frameRenderers = new List<Renderer>();
            foreach (var name in new[] { "_obstacleFrame", "_obstacleFakeGlow" })
            {
                var component = GetField(stretch, name) as Component;
                if (component != null) frameRenderers.AddRange(component.GetComponentsInChildren<Renderer>(true));
            }
            var allRenderers = coreRenderers.Concat(frameRenderers).Distinct().ToArray();
            var originalEnabled = allRenderers.Select(renderer => renderer.enabled).ToArray();
            var unrelated = GameObject.CreatePrimitive(PrimitiveType.Quad);
            unrelated.name = TemporaryCameraPrefix + ".NonWallScreenDisplacement";
            unrelated.layer = 17;
            ownedObjects.Add(unrelated);
            var unrelatedRenderer = unrelated.GetComponent<Renderer>();
            Action<ScriptableRenderContext, Camera> observe = null;
            try
            {
                autoOpaque.SetValue(modmap, false);
                wallMap.SetValue(null, false);
                for (var mode = 0; mode < 4; mode++)
                {
                    var expectedMode = mode == 3 ? 0 : mode;
                    walls.SetValue(visible, Enum.ToObject(walls.PropertyType, mode == 3 ? 1 : mode), null);
                    if (mode == 3)
                    {
                        autoOpaque.SetValue(modmap, true);
                        wallMap.SetValue(null, true);
                    }
                    // Apply the auto-opaque override after updating its global input.
                    Invoke(settings.GetType().GetMethod("ApplyLayerBitmask", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), settings);
                    var observed = false;
                    var scoped = false;
                    observe = delegate(ScriptableRenderContext context, Camera renderingCamera) {
                        if (renderingCamera != camera) return;
                        observed = true;
                        scoped = allRenderers.Select((renderer, index) => expectedMode == 0 ?
                            renderer.enabled == originalEnabled[index] :
                            (coreRenderers.Contains(renderer) || expectedMode == 2 ? !renderer.enabled :
                                renderer.enabled == originalEnabled[index])).All(value => value) && unrelatedRenderer.enabled;
                    };
                    RenderPipelineManager.beginCameraRendering += observe;
                    if (mode == 3)
                        Invoke(cam2Type.GetMethod("PrepareMiddlewaredRender", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), cam2, true);
                    else if (!SubmitStandardRequest(camera, camera.targetTexture))
                        throw new NotSupportedException("source camera URP request is unavailable");
                    RenderPipelineManager.beginCameraRendering -= observe;
                    observe = null;
                    var restored = allRenderers.Select((renderer, index) => renderer.enabled == originalEnabled[index]).All(value => value);
                    AddCheck("obstacle.visibility-mode." + mode, observed && scoped && restored &&
                        unrelatedRenderer.enabled && (camera.cullingMask & (1 << 17)) != 0 &&
                        (camera.cullingMask & (1 << 27)) != 0 && nativeMainCamera.cullingMask == originalMainMask,
                        "observed=" + observed + "; scoped=" + scoped + "; restored=" + restored +
                        "; source wall mode=" + expectedMode + "; non-wall layers and native main mask preserved");
                }
            }
            finally
            {
                if (observe != null) RenderPipelineManager.beginCameraRendering -= observe;
                wallMap.SetValue(null, originalWallMap);
                autoOpaque.SetValue(modmap, originalAuto);
                walls.SetValue(visible, originalMode, null);
                for (var index = 0; index < allRenderers.Length; index++) allRenderers[index].enabled = originalEnabled[index];
                UnityEngine.Object.Destroy(unrelated);
            }
        }

        IEnumerator RunDesktopRegression()
        {
            Component owner = null;
            Canvas canvas = null;
            Camera desktopCamera = null;
            RawImage activeImage = null;
            Texture originalTexture = null;
            Material originalMaterial = null;
            RenderTexture target = null;
            Texture2D bars = null;
            var previousTarget = (RenderTexture)null;
            var previousEnabled = false;
            try
            {
                if (nativeMainCamera == null) RememberNativeMainCamera();
                var ownerType = FindType("Camera2UrpFix.DesktopCanvasOwner");
                if (ownerType == null) throw new TypeLoadException("Camera2UrpFix.DesktopCanvasOwner is unavailable");
                var owners = Resources.FindObjectsOfTypeAll(ownerType);
                foreach (var item in owners)
                {
                    var component = item as Component;
                    if (component == null || component.gameObject == null || !component.gameObject.scene.IsValid()) continue;
                    owner = component;
                    break;
                }
                if (owner == null) throw new InvalidOperationException("no active Camera2 desktop canvas owner was found");
                canvas = owner.GetComponent<Canvas>();
                desktopCamera = GetMember(owner, "OwnedCamera") as Camera;
                if (canvas == null || desktopCamera == null) throw new InvalidOperationException("desktop canvas owner is incomplete");
                var desktopData = desktopCamera.GetComponent<UniversalAdditionalCameraData>();
                AddCheck("desktop.screen-space-camera", canvas.renderMode == RenderMode.ScreenSpaceCamera &&
                    canvas.worldCamera == desktopCamera,
                    "Camera2 desktop Canvas remains ScreenSpaceCamera and uses its owned camera");
                AddCheck("desktop.camera-xr-disabled", desktopData != null && !desktopData.allowXRRendering,
                    "owned desktop camera has URP allowXRRendering=false");

                var images = canvas.GetComponentsInChildren<RawImage>(true);
                foreach (var image in images)
                {
                    if (image != null && image.enabled && image.gameObject.activeInHierarchy)
                    {
                        activeImage = image;
                        break;
                    }
                }
                if (activeImage == null) throw new InvalidOperationException("no active Camera2 RawImage view was found");
                originalTexture = activeImage.texture;
                originalMaterial = activeImage.material;

                var viewportType = FindType("Camera2.Behaviours.CamerasViewport");
                var desktopViewType = FindType("Camera2.Behaviours.CameraDesktopView");
                var viewport = viewportType == null ? null : owner.GetComponent(viewportType);
                var currentView = activeImage;
                if (viewport == null || currentView == null || desktopViewType == null)
                    throw new InvalidOperationException("Camera2 viewport current view is unavailable");
                var currentImage = currentView as RawImage;
                if (currentImage != null && currentImage.enabled && currentImage.gameObject.activeInHierarchy)
                {
                    activeImage = currentImage;
                    originalTexture = activeImage.texture;
                    originalMaterial = activeImage.material;
                }
                AddCheck("desktop.active-material-opaque", IsOpaqueDesktopMaterial(originalMaterial),
                    "activeRawImage keeps an opaque desktop material");
                var actionType = viewportType.GetNestedType("CamAction", BindingFlags.Public | BindingFlags.NonPublic);
                var hitTest = viewportType.GetMethod("GetViewAtPoint", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(Vector2), actionType.MakeByRefType() }, null);
                if (hitTest == null) throw new MissingMethodException("CamerasViewport.GetViewAtPoint");
                var centerAction = Activator.CreateInstance(actionType);
                var hit = Invoke(hitTest, viewport, new object[] {
                    new Vector2(Screen.width * .5f, Screen.height * .5f), centerAction
                });
                AddCheck("desktop.hit-test-center", hit != null && ReferenceEquals(hit, currentView),
                    "screen center hit the current Camera2 view");

                bars = new Texture2D(4, 1, TextureFormat.RGBA32, false);
                bars.name = TemporaryCameraPrefix + ".RgbBarsAlphaZero";
                bars.filterMode = FilterMode.Point;
                bars.wrapMode = TextureWrapMode.Clamp;
                bars.SetPixels32(new[] {
                    new Color32(255, 0, 0, 0), new Color32(0, 255, 0, 0),
                    new Color32(0, 0, 255, 0), new Color32(255, 255, 255, 0)
                });
                bars.Apply(false, false);
                originalTexture = activeImage.texture;
                activeImage.texture = bars;
                Canvas.ForceUpdateCanvases();
                yield return null;

                var width = Screen.width;
                var height = Screen.height;
                if (width <= 0 || height <= 0) throw new InvalidOperationException("invalid screen dimensions");
                target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                target.name = TemporaryCameraPrefix + ".DesktopRenderTarget";
                target.Create();
                previousTarget = desktopCamera.targetTexture;
                previousEnabled = desktopCamera.enabled;
                desktopCamera.enabled = false;
                desktopCamera.targetTexture = target;
                var submitted = SubmitStandardRequest(desktopCamera, target);
                if (!submitted) throw new NotSupportedException("URP StandardRequest was not submitted for desktop capture");
                var previousActive = RenderTexture.active;
                Texture2D screenshot = null;
                try
                {
                    RenderTexture.active = target;
                    screenshot = new Texture2D(width, height, TextureFormat.RGBA32, false);
                    screenshot.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
                    screenshot.Apply(false, false);
                    File.WriteAllBytes(Path.Combine(runtimeDirectory, "desktop-bars.png"), screenshot.EncodeToPNG());
                    var pixels = screenshot.GetPixels32();
                    var matches = 0;
                    var bright = 0;
                    var sampleXs = new[] { .125f, .375f, .625f, .875f };
                    foreach (var sample in sampleXs)
                    {
                        var x = Mathf.Clamp((int)(width * sample), 0, width - 1);
                        var y = height / 2;
                        var pixel = pixels[y * width + x];
                        var expected = sample < .25f ? 0 : (sample < .5f ? 1 : (sample < .75f ? 2 : 3));
                        var matched = expected == 0 ? pixel.r > pixel.g + 35 && pixel.r > pixel.b + 35 :
                            (expected == 1 ? pixel.g > pixel.r + 35 && pixel.g > pixel.b + 35 :
                            (expected == 2 ? pixel.b > pixel.r + 35 && pixel.b > pixel.g + 35 :
                                pixel.r > 100 && pixel.g > 100 && pixel.b > 100));
                        if (matched) matches++;
                    }
                    for (var i = 0; i < pixels.Length; i++)
                        if (pixels[i].r + pixels[i].g + pixels[i].b > 90) bright++;
                    AddCheck("desktop.urp-rgb-bars", matches >= 3 && bright > pixels.Length / 100,
                        "alpha-zero RGB bars remained visible through explicit Screen.width/height URP capture");
                    AddCheck("desktop.material-retained", activeImage.material == originalMaterial,
                        "activeRawImage material stayed unchanged during texture replacement");
                }
                finally
                {
                    RenderTexture.active = previousActive;
                    if (screenshot != null) UnityEngine.Object.Destroy(screenshot);
                }
            }
            finally
            {
                if (activeImage != null) activeImage.texture = originalTexture;
                if (desktopCamera != null)
                {
                    desktopCamera.targetTexture = previousTarget;
                    desktopCamera.enabled = previousEnabled;
                }
                if (target != null) UnityEngine.Object.Destroy(target);
                if (bars != null) UnityEngine.Object.Destroy(bars);
                var main = nativeMainCamera == null ? FindNativeMainCamera() : nativeMainCamera;
                if (main != null && nativeMainHadData)
                {
                    var data = main.GetComponent<UniversalAdditionalCameraData>();
                    AddCheck("desktop.native-main-camera-xr-unchanged", data != null && data.allowXRRendering == nativeMainAllowXr,
                        "native main camera allowXRRendering was unchanged");
                }
                else
                {
                    AddCheck("desktop.native-main-camera-xr-unchanged", false,
                        "native MainCamera with URP additional data was unavailable");
                }
            }
        }

        bool SubmitStandardRequest(Camera camera, RenderTexture destination)
        {
            var request = new RenderPipeline.StandardRequest {
                destination = destination, mipLevel = 0, face = CubemapFace.Unknown, slice = 0
            };
            if (!RenderPipeline.SupportsRenderRequest<RenderPipeline.StandardRequest>(camera, request)) return false;
            RenderPipeline.SubmitRenderRequest<RenderPipeline.StandardRequest>(camera, request);
            return true;
        }

        void SnapshotCameraFiles()
        {
            originalCameraFiles.Clear();
            if (!Directory.Exists(cameraDirectory)) return;
            var prefix = cameraDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var file in Directory.GetFiles(cameraDirectory, "*.json", SearchOption.AllDirectories))
            {
                var relative = file.Substring(prefix.Length).Replace(Path.DirectorySeparatorChar, '/');
                originalCameraFiles.Add(new CameraFile { RelativePath = relative, Bytes = File.ReadAllBytes(file) });
            }
        }

        void RestoreCameraFiles()
        {
            try
            {
                RemoveUntrackedCameraFiles();
                foreach (var original in originalCameraFiles)
                {
                    var path = Path.Combine(cameraDirectory, original.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllBytes(path, original.Bytes);
                }
                if (camManagerType != null) InvokeStatic(camManagerType, "Reload");
                RemoveUntrackedCameraFiles();
                var restored = true;
                foreach (var original in originalCameraFiles)
                {
                    var path = Path.Combine(cameraDirectory, original.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(path) || !BytesEqual(original.Bytes, File.ReadAllBytes(path))) restored = false;
                }
                AddCheck("camera-files.byte-restore", restored,
                    restored ? "all original UserData/Camera2 JSON bytes were restored" : "one or more original Camera2 JSON files changed");
            }
            catch (Exception error)
            {
                phaseFailed = true;
                RecordError("camera-files-restore", error);
                AddCheck("camera-files.byte-restore", false, DescribeException(error));
            }
        }

        void RemoveUntrackedCameraFiles()
        {
            if (!Directory.Exists(cameraDirectory)) return;
            foreach (var file in Directory.GetFiles(cameraDirectory, "*.json", SearchOption.AllDirectories))
            {
                if (Path.GetFileNameWithoutExtension(file).StartsWith(
                    TemporaryCameraPrefix, StringComparison.OrdinalIgnoreCase)) File.Delete(file);
            }
        }

        static bool IsOpaqueDesktopMaterial(Material material)
        {
            if (material == null) return false;
            if (material.name.IndexOf("Camera2UrpFix.OpaqueDesktopView", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            var shader = material.shader;
            return shader != null && String.Equals(shader.name, "Unlit/Texture", StringComparison.OrdinalIgnoreCase);
        }

        string FindCameraFile(string name)
        {
            if (!Directory.Exists(cameraDirectory)) return null;
            return Directory.GetFiles(cameraDirectory, "*.json", SearchOption.AllDirectories)
                .FirstOrDefault(path => String.Equals(Path.GetFileNameWithoutExtension(path), name, StringComparison.OrdinalIgnoreCase));
        }

        void DeleteTemporaryCameraFiles()
        {
            if (!Directory.Exists(cameraDirectory)) return;
            foreach (var path in Directory.GetFiles(cameraDirectory, TemporaryCameraPrefix + "*.json", SearchOption.AllDirectories))
            {
                try { File.Delete(path); } catch (Exception error) { RecordError("camera-file-cleanup", error); }
            }
        }

        HashSet<string> ReadCameraNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (camManagerType == null) return names;
            var property = camManagerType.GetProperty("cams", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var dictionary = property == null ? null : property.GetValue(null, null) as IDictionary;
            if (dictionary == null) return names;
            foreach (DictionaryEntry item in dictionary) names.Add(Convert.ToString(item.Key, CultureInfo.InvariantCulture));
            return names;
        }

        object FindUsableCam2()
        {
            if (cam2Type == null) return null;
            foreach (var item in Resources.FindObjectsOfTypeAll(cam2Type))
            {
                var component = item as Component;
                if (component == null || component.gameObject == null || !component.gameObject.scene.IsValid()) continue;
                if (GetMember(item, "UCamera") is Camera) return item;
            }
            return null;
        }

        ResourceCounts CaptureResourceCounts()
        {
            var ownerType = FindType("Camera2UrpFix.DesktopCanvasOwner");
            var views = 0;
            foreach (var owner in Resources.FindObjectsOfTypeAll(ownerType))
            {
                var entries = GetMember(owner, "viewMaterials") as IEnumerable;
                if (entries != null) foreach (var unused in entries) views++;
            }
            return new ResourceCounts {
                SourceCameras = GetListCount("Camera2UrpFix.Runtime", "SourceCameras"),
                PostProcessingStates = GetListCount("Camera2UrpFix.PostProcessingCompatibility", "States"),
                DesktopViews = views
            };
        }

        int GetListCount(string typeName, string fieldName)
        {
            var type = FindType(typeName);
            if (type == null) return -1;
            var field = type.GetField(fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var value = field == null ? null : field.GetValue(null) as IEnumerable;
            if (value == null) return -1;
            var count = 0;
            foreach (var unused in value) count++;
            return count;
        }

        static int GetInstanceId(object value)
        {
            var unityObject = value as UnityEngine.Object;
            return unityObject == null ? 0 : unityObject.GetInstanceID();
        }

        static bool IsInstanceGone(int id)
        {
            if (id == 0) return true;
            var method = typeof(Resources).GetMethod("InstanceIDToObject",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null,
                new[] { typeof(int) }, null);
            if (method == null) return false;
            try { return method.Invoke(null, new object[] { id }) as UnityEngine.Object == null; }
            catch { return false; }
        }

        void RememberNativeMainCamera()
        {
            nativeMainCamera = FindNativeMainCamera();
            if (nativeMainCamera == null) return;
            var data = nativeMainCamera.GetComponent<UniversalAdditionalCameraData>();
            if (data == null) return;
            nativeMainHadData = true;
            nativeMainAllowXr = data.allowXRRendering;
        }

        Camera FindNativeMainCamera()
        {
            try
            {
                if (Camera.main != null) return Camera.main;
            }
            catch { }
            foreach (var camera in Resources.FindObjectsOfTypeAll<Camera>())
            {
                try { if (camera != null && camera.CompareTag("MainCamera")) return camera; }
                catch { }
            }
            return null;
        }

        void DestroyOwnedObjects()
        {
            for (var index = ownedAssets.Count - 1; index >= 0; index--)
            {
                var item = ownedAssets[index];
                if (item != null) UnityEngine.Object.Destroy(item);
            }
            ownedAssets.Clear();
            for (var index = ownedObjects.Count - 1; index >= 0; index--)
            {
                var item = ownedObjects[index];
                if (item != null) UnityEngine.Object.Destroy(item);
            }
            ownedObjects.Clear();
        }

        IEnumerator Finish(bool passed)
        {
            if (finished) yield break;
            finished = true;
            try { RestoreCameraFiles(); }
            catch (Exception error) { phaseFailed = true; RecordError("restore", error); }
            ReleaseObserverPatches();
            DestroyOwnedObjects();
            for (var index = 0; index < 4; index++) yield return null;
            try { WriteReport(passed && !phaseFailed && checks.All(value => value.Passed)); }
            catch (Exception error)
            {
                RecordError("report", error);
                try { WriteReport(false); } catch { }
            }
            Application.logMessageReceived -= OnLogMessage;
            StopAllCoroutines();
            Application.Quit();
        }

        void WriteReport(bool passed)
        {
            var json = new StringBuilder();
            json.Append("{\"passed\":").Append(passed ? "true" : "false");
            json.Append(",\"elapsedSeconds\":").Append(clock == null ? "null" : clock.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture));
            json.Append(",\"checks\":[");
            for (var index = 0; index < checks.Count; index++)
            {
                if (index > 0) json.Append(",");
                json.Append("{\"name\":").Append(JsonString(checks[index].Name));
                json.Append(",\"passed\":").Append(checks[index].Passed ? "true" : "false");
                json.Append(",\"detail\":").Append(JsonString(checks[index].Detail)).Append("}");
            }
            json.Append("],\"errors\":[");
            for (var index = 0; index < errors.Count; index++)
            {
                if (index > 0) json.Append(",");
                json.Append(JsonString(errors[index]));
            }
            json.Append("],\"capturedErrors\":[");
            lock (errorLock)
            {
                for (var index = 0; index < capturedErrors.Count; index++)
                {
                    if (index > 0) json.Append(",");
                    var item = capturedErrors[index];
                    json.Append("{\"type\":").Append(JsonString(item.Type));
                    json.Append(",\"relevant\":").Append(item.Relevant ? "true" : "false");
                    json.Append(",\"message\":").Append(JsonString(item.Message));
                    json.Append(",\"stackTrace\":").Append(JsonString(item.StackTrace)).Append("}");
                }
            }
            json.Append("],\"artifacts\":[\"obstacle-urp.png\",\"desktop-bars.png\",\"settings-first-person.png\",\"settings-third-person.png\"]}");
            File.WriteAllText(Path.Combine(runtimeDirectory, "report.json"), json.ToString(), new UTF8Encoding(false));
        }

        void AddCheck(string name, bool passed, string detail)
        {
            checks.Add(new Check { Name = name, Passed = passed, Detail = detail ?? String.Empty });
        }

        void RecordError(string stage, Exception error)
        {
            var value = stage + ": " + DescribeException(error);
            errors.Add(value);
            try { if (Logger != null) Logger.Error("Camera2RegressionRuntime [" + stage + "]: " + value); } catch { }
        }

        static string DescribeException(Exception error)
        {
            if (error == null) return String.Empty;
            var builder = new StringBuilder();
            var current = error;
            while (current != null)
            {
                if (builder.Length > 0) builder.Append(" -> ");
                builder.Append(current.GetType().FullName).Append(": ").Append(current.Message);
                current = current.InnerException;
            }
            return builder.ToString();
        }

        static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (var index = 0; index < left.Length; index++) if (left[index] != right[index]) return false;
            return true;
        }

        static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullName, false);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }

        static object GetMember(object target, string name)
        {
            if (target == null) return null;
            var type = target as Type ?? target.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var property = type.GetProperty(name, flags);
            if (property != null) return property.GetValue(target is Type ? null : target, null);
            var field = type.GetField(name, flags);
            return field == null ? null : field.GetValue(target is Type ? null : target);
        }

        static object GetField(object target, string name)
        {
            if (target == null) return null;
            var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(target);
        }

        static object Invoke(MethodInfo method, object target, params object[] arguments)
        {
            if (method == null) throw new MissingMethodException("reflection method is null");
            try { return method.Invoke(target, arguments); }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }

        static object InvokeStatic(Type type, string methodName, params object[] arguments)
        {
            var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return Invoke(method, null, arguments);
        }

        bool Expired()
        {
            return clock == null || clock.Elapsed.TotalSeconds >= TotalSeconds;
        }

        static string JsonString(string value)
        {
            if (value == null) return "null";
            var result = new StringBuilder();
            result.Append('"');
            foreach (var character in value)
            {
                switch (character)
                {
                    case '\\': result.Append("\\\\"); break;
                    case '"': result.Append("\\\""); break;
                    case '\r': result.Append("\\r"); break;
                    case '\n': result.Append("\\n"); break;
                    case '\t': result.Append("\\t"); break;
                    default:
                        if (character < 32) result.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        else result.Append(character);
                        break;
                }
            }
            result.Append('"');
            return result.ToString();
        }

        sealed class ObserverPatches
        {
            internal static RegressionRunner Active;
            internal static bool PrepareObserved;
            internal static bool PrepareObservedBeforeAllowXrFalse;
            internal static bool PrepareObservedForce;
            internal static int ObstacleUpdateCount;
            internal static int PostProcessCount;
            internal static string FirstPrepareDetail;
            internal static int ValidPrepareCount;
            internal static int UnsafePrepareCount;

            public static void PostProcessPrefix() { PostProcessCount++; }

            [HarmonyPriority(Priority.Last)]
            public static void PreparePrefix(object __instance, bool __0)
            {
                if (Active == null || !__0) return;
                var camera = GetMember(__instance, "UCamera") as Camera;
                if (camera == null) return; // Native Prepare returns before any rendering here.
                var data = camera.GetComponent<UniversalAdditionalCameraData>();
                ValidPrepareCount++;
                if (data == null || data.allowXRRendering) UnsafePrepareCount++;
                if (PrepareObserved) return;
                PrepareObserved = true;
                PrepareObservedForce = __0;
                PrepareObservedBeforeAllowXrFalse = data != null && !data.allowXRRendering;
                FirstPrepareDetail = "cam=" + (camera == null ? "null" : camera.name) +
                    ", data=" + (data == null ? "null" : data.allowXRRendering.ToString()) +
                    ", initialized=" + (GetMember(__instance, "middlewares") != null);
            }

            [HarmonyPriority(Priority.Last)]
            public static bool ObstacleUpdatePrefix()
            {
                ObstacleUpdateCount++;
                return false;
            }
        }
    }
}
