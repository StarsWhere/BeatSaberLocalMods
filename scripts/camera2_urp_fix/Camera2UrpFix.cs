using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using IPA;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace Camera2UrpFix
{
    // Remove when Camera2 natively supports this game's URP cameras, effects,
    // obstacle layers, desktop composition and resource lifetimes.
    [Plugin(RuntimeOptions.SingleStartInit)]
    public sealed class Plugin
    {
        static Harmony harmony;

        [Init]
        public void Init(IPA.Logging.Logger logger)
        {
            Runtime.Log = logger;
        }

        [OnStart]
        public void Start()
        {
            harmony = new Harmony("local.camera2.urp-fix");
            try
            {
                var viewportType = AccessTools.TypeByName("Camera2.Behaviours.CamerasViewport");
                var cam2Type = AccessTools.TypeByName("Camera2.Behaviours.Cam2");
                if (viewportType == null || cam2Type == null)
                    throw new TypeLoadException("Camera2 0.6.119 behaviour types are unavailable");
                var awake = AccessTools.Method(viewportType, "Awake");
                var addNewView = AccessTools.Method(viewportType, "AddNewView");
                var init = AccessTools.Method(cam2Type, "Init");
                var hitTest = viewportType.GetMethod(
                    "GetViewAtPoint", BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new[] { typeof(Vector2), typeof(int).MakeByRefType() }, null);
                if (awake == null || addNewView == null || init == null || hitTest == null)
                    throw new MissingMethodException("Camera2 0.6.119 viewport methods are unavailable");

                harmony.Patch(
                    awake,
                    postfix: new HarmonyMethod(typeof(Patches), "ViewportAwakePostfix"));
                harmony.Patch(
                    addNewView,
                    postfix: new HarmonyMethod(typeof(Patches), "AddNewViewPostfix"));
                harmony.Patch(
                    init,
                    postfix: new HarmonyMethod(typeof(Patches), "Cam2InitPostfix"));
                harmony.Patch(
                    hitTest,
                    transpiler: new HarmonyMethod(typeof(Patches), "GetViewAtPointTranspiler"));

                WallCompatibility.Install(harmony);
                LifecycleCompatibility.Install(harmony);
                PostProcessingCompatibility.Install(harmony);
                AtomicConfiguration.Install(harmony);
                SettingsCompatibility.Install(harmony);
                Runtime.AttachExistingViewports();
                Runtime.ConfigureExistingCameras();
                Runtime.LogInfo("Camera2 URP Fix 0.3.1 enabled");
            }
            catch
            {
                try { ReleaseCompatibility(); }
                catch (Exception exception) { Runtime.LogError("Camera2 URP startup rollback failed", exception); }
                harmony.UnpatchSelf();
                harmony = null;
                throw;
            }
        }

        [OnExit]
        public void Exit()
        {
            ReleaseCompatibility();
            if (harmony != null)
            {
                harmony.UnpatchSelf();
                harmony = null;
            }
        }

        static void ReleaseCompatibility()
        {
            SettingsCompatibility.Release();
            AtomicConfiguration.Release();
            try { PostProcessingCompatibility.Release(); }
            finally
            {
                try { LifecycleCompatibility.Release(); }
                finally
                {
                    try { WallCompatibility.Release(); }
                    finally { Runtime.ReleaseAll(); }
                }
            }
        }
    }

    internal static class Patches
    {
        public static void ViewportAwakePostfix(object __instance)
        {
            Runtime.AttachViewport(__instance);
        }

        public static void AddNewViewPostfix(object __instance, object __result)
        {
            var viewport = __instance as Component;
            if (viewport == null || viewport.GetComponent<DesktopCanvasOwner>() == null) return;
            Runtime.NormalizeView(__result);
            viewport.GetComponent<DesktopCanvasOwner>().ConfigureView(__result as RawImage);
        }

        public static void Cam2InitPostfix(object __instance)
        {
            Runtime.DisableCameraXr(__instance);
        }

        public static IEnumerable<CodeInstruction> GetViewAtPointTranspiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var positionGetterCount = 0;
            var matchingCount = 0;
            var replacement = AccessTools.Method(
                typeof(Runtime), "ScreenPosition", new[] { typeof(Transform) });
            if (replacement == null) throw new MissingMethodException("ScreenPosition");

            for (var index = 0; index < list.Count; index++)
            {
                var method = list[index].operand as MethodInfo;
                if (method == null || method.Name != "get_position" ||
                    method.DeclaringType != typeof(Transform)) continue;
                positionGetterCount++;
                var previous = index == 0 ? null : list[index - 1].operand as MethodInfo;
                var next = index + 1 >= list.Count ? null : list[index + 1].operand as MethodInfo;
                var isViewportRect = previous != null && previous.Name == "get_rekt" &&
                    previous.DeclaringType != null &&
                    previous.DeclaringType.FullName == "Camera2.Behaviours.CameraDesktopView";
                var isVector2Conversion = next != null && next.Name == "op_Implicit" &&
                    next.DeclaringType == typeof(Vector2) && next.ReturnType == typeof(Vector2) &&
                    next.GetParameters().Length == 1 &&
                    next.GetParameters()[0].ParameterType == typeof(Vector3);
                if (!isViewportRect || !isVector2Conversion) continue;
                matchingCount++;
                list[index] = new CodeInstruction(OpCodes.Call, replacement);
            }

            if (positionGetterCount != 1 || matchingCount != 1)
                throw new InvalidOperationException(
                    "Camera2 GetViewAtPoint position hook shape changed; expected one matching Transform.get_position");
            return list;
        }
    }

    internal static class Runtime
    {
        internal static IPA.Logging.Logger Log;
        static readonly List<SourceCameraState> SourceCameras = new List<SourceCameraState>();

        sealed class SourceCameraState
        {
            internal Camera Camera;
            internal UniversalAdditionalCameraData Data;
            internal bool AllowXr;
            internal bool AddedData;
        }

        internal static void LogInfo(string message)
        {
            if (Log != null) Log.Info(message);
        }

        internal static void LogWarning(string message)
        {
            if (Log != null) Log.Warn(message);
        }

        internal static void LogError(string message, Exception exception)
        {
            if (Log != null) Log.Error(message + ": " + exception);
        }

        internal static void AttachViewport(object viewport)
        {
            var component = viewport as Component;
            if (component == null || component.gameObject == null) return;
            if (!component.gameObject.scene.IsValid()) return;
            try
            {
                var canvas = component.GetComponent<Canvas>();
                if (canvas == null) return;
                var owner = component.GetComponent<DesktopCanvasOwner>();
                if (owner == null) owner = component.gameObject.AddComponent<DesktopCanvasOwner>();
                owner.Initialize(canvas);
            }
            catch (Exception exception)
            {
                LogError("Camera2 viewport URP setup failed", exception);
            }
        }

        internal static void AttachExistingViewports()
        {
            var type = AccessTools.TypeByName("Camera2.Behaviours.CamerasViewport");
            if (type == null) return;
            foreach (var viewport in Resources.FindObjectsOfTypeAll(type))
                AttachViewport(viewport);
        }

        internal static void ConfigureExistingCameras()
        {
            var type = AccessTools.TypeByName("Camera2.Behaviours.Cam2");
            if (type == null) return;
            foreach (var cam2 in Resources.FindObjectsOfTypeAll(type))
            {
                var component = cam2 as Component;
                if (component != null && component.gameObject != null && component.gameObject.scene.IsValid())
                    DisableCameraXr(cam2);
            }
        }

        internal static void DisableCameraXr(object cam2)
        {
            if (cam2 == null) return;
            try
            {
                var property = cam2.GetType().GetProperty(
                    "UCamera", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var camera = property == null ? null : property.GetValue(cam2, null) as Camera;
                if (camera == null) return;
                DisableCameraXr(camera);
            }
            catch (Exception exception)
            {
                LogError("Camera2 camera URP setup failed", exception);
            }
        }

        internal static void DisableCameraXr(Camera camera)
        {
            if (camera == null) return;
            SourceCameras.RemoveAll(value => value.Camera == null || value.Data == null);
            var data = camera.GetComponent<UniversalAdditionalCameraData>();
            if (!SourceCameras.Any(value => value.Camera == camera))
            {
                var addedData = data == null;
                if (addedData) data = camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
                SourceCameras.Add(new SourceCameraState {
                    Camera = camera, Data = data, AllowXr = data.allowXRRendering, AddedData = addedData
                });
            }
            data.allowXRRendering = false;
        }

        internal static void ForgetSourceCamera(Camera camera)
        {
            SourceCameras.RemoveAll(value => value.Camera == null || ReferenceEquals(value.Camera, camera));
            PostProcessingCompatibility.Forget(camera);
        }

        internal static void NormalizeView(object view)
        {
            var component = view as Component;
            if (component == null) return;
            var rect = component.transform as RectTransform;
            if (rect == null) return;

            // Camera2 uses worldPositionStays=true when parenting. That is valid
            // for its original Overlay canvas, but preserves the wrong world
            // geometry after the root becomes a ScreenSpaceCamera canvas.
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
            var localPosition = rect.localPosition;
            localPosition.z = 0f;
            rect.localPosition = localPosition;
        }

        internal static Vector3 ScreenPosition(Transform transform)
        {
            if (transform == null) return Vector3.zero;
            var original = transform.position;
            var canvas = transform.GetComponentInParent<Canvas>();
            if (canvas == null) return original;
            var owner = canvas.GetComponent<DesktopCanvasOwner>();
            if (owner == null || !owner.OwnsCanvas(canvas)) return original;
            var camera = owner.OwnedCamera;
            if (camera == null || canvas.worldCamera != camera) return original;

            // Camera2's current native Canvas has scaleFactor=1, so its local
            // RectTransform size is already in screen pixels. If a future
            // Camera2 build changes that invariant, its get_rect/size path
            // must be adapted together with this position hook.
            var screen = RectTransformUtility.WorldToScreenPoint(camera, original);
            return new Vector3(screen.x, screen.y, original.z);
        }

        internal static void ReleaseAll()
        {
            DesktopCanvasOwner.ReleaseAll();
            foreach (var state in SourceCameras)
            {
                if (state.Data == null) continue;
                state.Data.allowXRRendering = state.AllowXr;
                if (state.AddedData) UnityEngine.Object.Destroy(state.Data);
            }
            SourceCameras.Clear();
        }
    }

    internal sealed class DesktopCanvasOwner : MonoBehaviour
    {
        static readonly List<DesktopCanvasOwner> Owners = new List<DesktopCanvasOwner>();

        Canvas canvas;
        Camera desktopCamera;
        RectTransform canvasRect;
        Coroutine deferredRefresh;
        bool captured;
        bool released;
        RenderMode originalRenderMode;
        Camera originalWorldCamera;
        float originalPlaneDistance;
        float originalScaleFactor;
        float originalReferencePixelsPerUnit;
        Vector3 originalCanvasLocalPosition;
        Vector3 originalCanvasLocalScale;
        Quaternion originalCanvasLocalRotation;
        Vector2 originalCanvasAnchorMin;
        Vector2 originalCanvasAnchorMax;
        Vector2 originalCanvasPivot;
        Vector2 originalCanvasAnchoredPosition;
        Vector2 originalCanvasSizeDelta;
        bool capturedCanvasTransform;
        bool warnedScaleFactor;
        Material desktopMaterial;
        readonly List<ViewMaterialState> viewMaterials = new List<ViewMaterialState>();

        sealed class ViewMaterialState
        {
            internal RawImage Image;
            internal Material Material;
            internal bool UseAlpha;
        }

        void Awake()
        {
            if (!Owners.Contains(this)) Owners.Add(this);
        }

        public void Initialize(Canvas value)
        {
            if (released || value == null) return;
            if (!captured || canvas != value)
            {
                canvas = value;
                originalRenderMode = canvas.renderMode;
                originalWorldCamera = canvas.worldCamera;
                originalPlaneDistance = canvas.planeDistance;
                originalScaleFactor = canvas.scaleFactor;
                originalReferencePixelsPerUnit = canvas.referencePixelsPerUnit;
                canvasRect = canvas.transform as RectTransform;
                if (canvasRect != null)
                {
                    originalCanvasLocalPosition = canvasRect.localPosition;
                    originalCanvasLocalScale = canvasRect.localScale;
                    originalCanvasLocalRotation = canvasRect.localRotation;
                    originalCanvasAnchorMin = canvasRect.anchorMin;
                    originalCanvasAnchorMax = canvasRect.anchorMax;
                    originalCanvasPivot = canvasRect.pivot;
                    originalCanvasAnchoredPosition = canvasRect.anchoredPosition;
                    originalCanvasSizeDelta = canvasRect.sizeDelta;
                    capturedCanvasTransform = true;
                }
                captured = true;
            }
            Apply();
        }

        internal Camera OwnedCamera { get { return desktopCamera; } }

        internal bool OwnsCanvas(Canvas value)
        {
            return !released && captured && canvas == value;
        }

        void OnEnable()
        {
            Apply();
        }

        void OnDisable()
        {
            StopDeferredRefresh();
            if (desktopCamera != null) desktopCamera.enabled = false;
        }

        void OnDestroy()
        {
            Release();
        }

        void Apply()
        {
            if (released || !captured || canvas == null) return;
            try
            {
                EnsureCamera();
                if (desktopCamera == null) return;

                ConfigureCamera();

                // Bind only after the camera has its final projection and
                // transform. Unity caches ScreenSpaceCamera canvas geometry at
                // this boundary.
                canvas.scaleFactor = originalScaleFactor;
                canvas.referencePixelsPerUnit = originalReferencePixelsPerUnit;
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = desktopCamera;
                canvas.planeDistance = 0.5f;
                if (!warnedScaleFactor && Mathf.Abs(canvas.scaleFactor - 1f) > 0.001f)
                {
                    warnedScaleFactor = true;
                    Runtime.LogWarning("Camera2 viewport Canvas scaleFactor is not 1; native hit-test Rect.size may need a future adapter");
                }

                NormalizeChildViews();
                RefreshCanvasLayout();
                desktopCamera.enabled = isActiveAndEnabled;
                ScheduleDeferredRefresh();
            }
            catch (Exception exception)
            {
                Runtime.LogError("Camera2 desktop canvas URP setup failed", exception);
            }
        }

        void ConfigureCamera()
        {
            desktopCamera.enabled = false;
            desktopCamera.transform.position = new Vector3(10000f, 10000f, -10000f);
            desktopCamera.transform.rotation = Quaternion.identity;
            // This Base camera owns the complete desktop frame. A render-graph
            // attachment with no color clear has undefined background pixels.
            desktopCamera.clearFlags = CameraClearFlags.SolidColor;
            desktopCamera.backgroundColor = Color.black;
            desktopCamera.nearClipPlane = 0.1f;
            desktopCamera.farClipPlane = 1f;
            desktopCamera.depth = 1000f;
            desktopCamera.orthographic = true;
            desktopCamera.orthographicSize = 5f;
            desktopCamera.useOcclusionCulling = false;
            desktopCamera.allowHDR = false;
            desktopCamera.allowMSAA = false;
            desktopCamera.depthTextureMode = DepthTextureMode.None;

            var layer = canvas.gameObject.layer;
            desktopCamera.cullingMask = layer >= 0 && layer < 32 ? 1 << layer : 0;

            var data = desktopCamera.GetComponent<UniversalAdditionalCameraData>();
            if (data == null) data = desktopCamera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            data.renderType = CameraRenderType.Base;
            if (data.cameraStack != null) data.cameraStack.Clear();
            data.renderPostProcessing = false;
            data.renderShadows = false;
            data.requiresDepthTexture = false;
            data.requiresColorTexture = false;
            data.allowXRRendering = false;
        }

        void NormalizeChildViews()
        {
            var type = AccessTools.TypeByName("Camera2.Behaviours.CameraDesktopView");
            if (type == null || canvas == null) return;
            var components = canvas.GetComponentsInChildren(type, true);
            foreach (var component in components)
            {
                Runtime.NormalizeView(component);
                ConfigureView(component as RawImage);
            }
        }

        internal void ConfigureView(RawImage image)
        {
            if (released || image == null) return;
            viewMaterials.RemoveAll(value => value.Image == null);
            if (desktopMaterial == null)
            {
                var shader = Shader.Find("Unlit/Texture");
                if (shader == null || !shader.isSupported)
                    throw new NotSupportedException("Opaque desktop shader Unlit/Texture is unavailable");
                desktopMaterial = new Material(shader);
                desktopMaterial.name = "Camera2UrpFix.OpaqueDesktopView";
                desktopMaterial.renderQueue = 3000;
                Runtime.LogInfo("Camera2 opaque desktop composition enabled");
            }
            var state = viewMaterials.FirstOrDefault(value => value.Image == image);
            if (state == null)
            {
                state = new ViewMaterialState { Image = image, Material = image.material };
                viewMaterials.Add(state);
                var lifetime = image.GetComponent<DesktopViewLifetime>();
                if (lifetime == null) lifetime = image.gameObject.AddComponent<DesktopViewLifetime>();
                lifetime.Initialize(this, image);
            }
            // The game's URP scene targets contain valid RGB with zero alpha.
            // Camera2's default UI alpha blending discards that image. Its
            // desktop viewport needs an opaque texture copy, preserving RGB.
            image.material = state.UseAlpha ? state.Material : desktopMaterial;
        }

        internal void ForgetView(RawImage image)
        {
            viewMaterials.RemoveAll(value => value.Image == null || ReferenceEquals(value.Image, image));
        }

        internal static void SetTransparency(RawImage image, bool enabled)
        {
            if (image == null) return;
            foreach (var owner in Owners)
            {
                if (owner == null || owner.released) continue;
                var state = owner.viewMaterials.FirstOrDefault(value => value.Image == image);
                if (state == null) continue;
                state.UseAlpha = enabled;
                image.material = enabled ? state.Material : owner.desktopMaterial;
                return;
            }
        }

        void RefreshCanvasLayout()
        {
            if (canvas == null) return;
            Canvas.ForceUpdateCanvases();
            if (canvasRect != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(canvasRect);
            Canvas.ForceUpdateCanvases();
        }

        void ScheduleDeferredRefresh()
        {
            if (!isActiveAndEnabled || deferredRefresh != null) return;
            deferredRefresh = StartCoroutine(DeferredRefresh());
        }

        IEnumerator DeferredRefresh()
        {
            yield return null;
            deferredRefresh = null;
            if (released || !captured || canvas == null || desktopCamera == null || !isActiveAndEnabled)
                yield break;
            ConfigureCamera();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = desktopCamera;
            canvas.planeDistance = 0.5f;
            canvas.scaleFactor = originalScaleFactor;
            canvas.referencePixelsPerUnit = originalReferencePixelsPerUnit;
            NormalizeChildViews();
            RefreshCanvasLayout();
            desktopCamera.enabled = true;
        }

        void StopDeferredRefresh()
        {
            if (deferredRefresh == null) return;
            StopCoroutine(deferredRefresh);
            deferredRefresh = null;
        }

        void EnsureCamera()
        {
            if (desktopCamera != null) return;
            var gameObject = new GameObject("Camera2UrpFix.DesktopGameCamera");
            UnityEngine.Object.DontDestroyOnLoad(gameObject);
            desktopCamera = gameObject.AddComponent<Camera>();
        }

        public void Release()
        {
            if (released) return;
            released = true;
            StopDeferredRefresh();

            foreach (var state in viewMaterials)
                if (state.Image != null) state.Image.material = state.Material;
            viewMaterials.Clear();
            if (desktopMaterial != null)
            {
                UnityEngine.Object.Destroy(desktopMaterial);
                desktopMaterial = null;
            }

            if (canvas != null && captured)
            {
                canvas.renderMode = originalRenderMode;
                canvas.worldCamera = originalWorldCamera;
                canvas.planeDistance = originalPlaneDistance;
                canvas.scaleFactor = originalScaleFactor;
                canvas.referencePixelsPerUnit = originalReferencePixelsPerUnit;
            }

            if (canvasRect != null && capturedCanvasTransform)
            {
                canvasRect.localPosition = originalCanvasLocalPosition;
                canvasRect.localScale = originalCanvasLocalScale;
                canvasRect.localRotation = originalCanvasLocalRotation;
                canvasRect.anchorMin = originalCanvasAnchorMin;
                canvasRect.anchorMax = originalCanvasAnchorMax;
                canvasRect.pivot = originalCanvasPivot;
                canvasRect.anchoredPosition = originalCanvasAnchoredPosition;
                canvasRect.sizeDelta = originalCanvasSizeDelta;
            }

            if (desktopCamera != null)
            {
                desktopCamera.enabled = false;
                var gameObject = desktopCamera.gameObject;
                desktopCamera = null;
                if (gameObject != null) UnityEngine.Object.Destroy(gameObject);
            }

            Owners.Remove(this);
        }

        public static void ReleaseAll()
        {
            var copy = Owners.ToArray();
            foreach (var owner in copy)
                if (owner != null) owner.Release();
            Owners.Clear();
        }
    }

    internal sealed class DesktopViewLifetime : MonoBehaviour
    {
        DesktopCanvasOwner owner;
        RawImage image;

        internal void Initialize(DesktopCanvasOwner value, RawImage view)
        {
            owner = value;
            image = view;
        }

        void OnDestroy()
        {
            if (owner != null) owner.ForgetView(image);
            owner = null;
            image = null;
        }
    }
}
