using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace Camera2UrpFix
{
    // Camera2 0.6.119's TransparentWalls patch changes shared obstacle
    // objects to FixMRAlpha and adds that layer to Camera.main.  The native
    // obstacle renderer already provides the correct layers, while layers 17
    // and 27 are also used by non-wall objects.  Wall visibility therefore
    // uses a short per-Camera2 render scope for wall renderers.
    internal static class WallCompatibility
    {
        const int VisibleWalls = 0;
        const int TransparentWalls = 1;
        const int HiddenWalls = 2;

        static readonly List<Hook> Hooks = new List<Hook>();

        static Harmony harmony;
        static MethodInfo settingsCamGetter;
        static MethodInfo settingsVisibleObjectsGetter;
        static MethodInfo visibleObjectsWallsGetter;
        static MethodInfo settingsModmapGetter;
        static MethodInfo cam2CameraGetter;
        static MethodInfo camManagerCamsGetter;
        static PropertyInfo cam2SettingsProperty;
        static Type cam2Type;
        static Type stretchableObstacleType;
        static FieldInfo obstacleCoreField;
        static FieldInfo obstacleFrameField;
        static FieldInfo obstacleFakeGlowField;
        static FieldInfo autoOpaqueWallsField;
        static FieldInfo isWallMapField;

        static readonly List<Component> TrackedWalls = new List<Component>();
        static readonly List<object> TrackedCam2 = new List<object>();

        static int obstacleLayer;
        static int screenDisplacementLayer;
        static int standardObstacleLayer;
        static int fixMrAlphaLayer;
        static int obstacleFrameBit;
        static int nativeCoreBits;
        static int fixMrAlphaBit;
        static bool installed;
        static bool maskFailureLogged;
        static bool renderEventsInstalled;
        static Camera activeRenderCamera;
        static WallRenderScope activeRenderScope;

        sealed class Hook
        {
            internal MethodBase Target;
            internal HarmonyPatchType Type;
        }

        internal static void Install(Harmony owner)
        {
            if (installed) return;
            if (owner == null) throw new ArgumentNullException("owner");

            var localHooks = new List<Hook>();
            try
            {
                ResolveLayers();
                ResolveCamera2Accessors();

                var transparentWallsType = AccessTools.TypeByName(
                    "Camera2.HarmonyPatches.TransparentWalls");
                if (transparentWallsType == null)
                    throw new TypeLoadException("Camera2 TransparentWalls type is unavailable");

                var transparentWallsPostfix = AccessTools.Method(
                    transparentWallsType, "Postfix");
                var makeWallsOpaqueForMainCam = AccessTools.Method(
                    transparentWallsType, "MakeWallsOpaqueForMainCam");
                if (transparentWallsPostfix == null || makeWallsOpaqueForMainCam == null)
                    throw new MissingMethodException(
                        "Camera2 0.6.119 wall patch methods are unavailable");

                // Patch the Camera2 patch methods themselves.  This leaves
                // Camera2's other Harmony patches intact and makes the old
                // wall mutation a single, explicitly suppressed path.
                var blockSharedWalls = new HarmonyMethod(
                    typeof(WallHooks), "BlockSharedWallMutationPrefix");
                blockSharedWalls.priority = Priority.First;
                owner.Patch(transparentWallsPostfix, prefix: blockSharedWalls);
                localHooks.Add(new Hook {
                    Target = transparentWallsPostfix,
                    Type = HarmonyPatchType.Prefix
                });

                var blockMainCamera = new HarmonyMethod(
                    typeof(WallHooks), "BlockMainCameraWallMutationPrefix");
                blockMainCamera.priority = Priority.First;
                owner.Patch(makeWallsOpaqueForMainCam, prefix: blockMainCamera);
                localHooks.Add(new Hook {
                    Target = makeWallsOpaqueForMainCam,
                    Type = HarmonyPatchType.Prefix
                });

                var cam2Init = AccessTools.Method(cam2Type, "Init");
                if (cam2Init == null)
                    throw new MissingMethodException("Camera2 Cam2.Init is unavailable");
                owner.Patch(
                    cam2Init,
                    postfix: new HarmonyMethod(typeof(WallHooks), "TrackCam2Postfix"));
                localHooks.Add(new Hook {
                    Target = cam2Init,
                    Type = HarmonyPatchType.Postfix
                });

                var prepareMiddlewaredRender = AccessTools.Method(
                    AccessTools.TypeByName("Camera2.Behaviours.Cam2"),
                    "PrepareMiddlewaredRender",
                    new[] { typeof(bool) });
                if (prepareMiddlewaredRender == null)
                    throw new MissingMethodException(
                        "Camera2 Cam2.PrepareMiddlewaredRender is unavailable");

                var wallRenderPrefix = new HarmonyMethod(
                    typeof(WallHooks), "PrepareWallRenderPrefix");
                wallRenderPrefix.priority = Priority.First;
                var wallRenderPostfix = new HarmonyMethod(
                    typeof(WallHooks), "PrepareWallRenderPostfix");
                var wallRenderFinalizer = new HarmonyMethod(
                    typeof(WallHooks), "PrepareWallRenderFinalizer");
                owner.Patch(
                    prepareMiddlewaredRender,
                    prefix: wallRenderPrefix,
                    postfix: wallRenderPostfix,
                    finalizer: wallRenderFinalizer);
                localHooks.Add(new Hook {
                    Target = prepareMiddlewaredRender,
                    Type = HarmonyPatchType.All
                });

                var setAllProperties = AccessTools.Method(
                    stretchableObstacleType, "SetAllProperties");
                if (setAllProperties == null)
                    throw new MissingMethodException(
                        "StretchableObstacle.SetAllProperties is unavailable");
                owner.Patch(
                    setAllProperties,
                    postfix: new HarmonyMethod(typeof(WallHooks), "TrackWallPostfix"));
                localHooks.Add(new Hook {
                    Target = setAllProperties,
                    Type = HarmonyPatchType.Postfix
                });

                var applyLayerBitmask = AccessTools.Method(
                    AccessTools.TypeByName("Camera2.Configuration.CameraSettings"),
                    "ApplyLayerBitmask", Type.EmptyTypes);
                if (applyLayerBitmask == null)
                    throw new MissingMethodException(
                        "Camera2 CameraSettings.ApplyLayerBitmask is unavailable");

                var applyLayerPostfix = new HarmonyMethod(
                    typeof(WallHooks), "ApplyLayerBitmaskPostfix");
                applyLayerPostfix.priority = Priority.Last;
                owner.Patch(applyLayerBitmask, postfix: applyLayerPostfix);
                localHooks.Add(new Hook {
                    Target = applyLayerBitmask,
                    Type = HarmonyPatchType.Postfix
                });

                var applyCameraValues = AccessTools.Method(
                    AccessTools.TypeByName("Camera2.Managers.CamManager"),
                    "ApplyCameraValues",
                    new[] { typeof(bool), typeof(bool), typeof(bool), typeof(bool) });
                if (applyCameraValues == null)
                    throw new MissingMethodException(
                        "Camera2 CamManager.ApplyCameraValues is unavailable");

                var applyCameraValuesPostfix = new HarmonyMethod(
                    typeof(WallHooks), "ApplyCameraValuesPostfix");
                applyCameraValuesPostfix.priority = Priority.Last;
                owner.Patch(applyCameraValues, postfix: applyCameraValuesPostfix);
                localHooks.Add(new Hook {
                    Target = applyCameraValues,
                    Type = HarmonyPatchType.Postfix
                });

                harmony = owner;
                Hooks.AddRange(localHooks);
                installed = true;
                SubscribeRenderEvents();
                RememberExistingWalls();
                RememberExistingCameras();
                RefreshExistingCameras();
            }
            catch
            {
                UnsubscribeRenderEvents();
                Unpatch(owner, localHooks);
                ClearState();
                throw;
            }
        }

        internal static void Release()
        {
            RestoreActiveRenderScope();
            UnsubscribeRenderEvents();
            if (harmony != null)
                Unpatch(harmony, Hooks);
            Hooks.Clear();
            ClearState();
        }

        static void Unpatch(Harmony owner, IEnumerable<Hook> hooks)
        {
            foreach (var hook in hooks)
            {
                if (hook == null || hook.Target == null) continue;
                try
                {
                    owner.Unpatch(hook.Target, hook.Type, owner.Id);
                }
                catch (Exception exception)
                {
                    Runtime.LogError("Camera2 wall compatibility unpatch failed", exception);
                }
            }
        }

        static void ClearState()
        {
            harmony = null;
            settingsCamGetter = null;
            settingsVisibleObjectsGetter = null;
            visibleObjectsWallsGetter = null;
            settingsModmapGetter = null;
            cam2CameraGetter = null;
            camManagerCamsGetter = null;
            cam2SettingsProperty = null;
            cam2Type = null;
            stretchableObstacleType = null;
            obstacleCoreField = null;
            obstacleFrameField = null;
            obstacleFakeGlowField = null;
            autoOpaqueWallsField = null;
            isWallMapField = null;
            TrackedWalls.Clear();
            TrackedCam2.Clear();
            installed = false;
            maskFailureLogged = false;
            renderEventsInstalled = false;
            activeRenderCamera = null;
            activeRenderScope = null;
            obstacleLayer = -1;
            screenDisplacementLayer = -1;
            standardObstacleLayer = -1;
            fixMrAlphaLayer = -1;
            obstacleFrameBit = 0;
            nativeCoreBits = 0;
            fixMrAlphaBit = 0;
        }

        static void ResolveLayers()
        {
            // These names are the runtime source of truth for this game
            // build.  Do not silently fall back to layer numbers: a changed
            // TagManager would make the wall adapter affect unrelated objects.
            obstacleLayer = ResolveLayer("Obstacle");
            screenDisplacementLayer = ResolveLayer("ScreenDisplacement");
            standardObstacleLayer = ResolveLayer("DontShowInExternalMRCamera");
            fixMrAlphaLayer = ResolveLayer("FixMRAlpha");

            if (obstacleLayer == screenDisplacementLayer ||
                obstacleLayer == standardObstacleLayer ||
                obstacleLayer == fixMrAlphaLayer ||
                screenDisplacementLayer == standardObstacleLayer ||
                screenDisplacementLayer == fixMrAlphaLayer ||
                standardObstacleLayer == fixMrAlphaLayer)
                throw new InvalidOperationException("Beat Saber wall layers are not distinct");

            obstacleFrameBit = LayerBit(obstacleLayer);
            nativeCoreBits = LayerBit(screenDisplacementLayer) |
                LayerBit(standardObstacleLayer);
            fixMrAlphaBit = LayerBit(fixMrAlphaLayer);
        }

        static int ResolveLayer(string name)
        {
            var layer = LayerMask.NameToLayer(name);
            if (layer < 0 || layer >= 32)
                throw new InvalidOperationException(
                    "Required Beat Saber layer is unavailable: " + name);
            return layer;
        }

        static int LayerBit(int layer)
        {
            return 1 << layer;
        }

        static void ResolveCamera2Accessors()
        {
            var settingsType = AccessTools.TypeByName("Camera2.Configuration.CameraSettings");
            var gameObjectsType = AccessTools.TypeByName("Camera2.Configuration.GameObjects");
            cam2Type = AccessTools.TypeByName("Camera2.Behaviours.Cam2");
            var managerType = AccessTools.TypeByName("Camera2.Managers.CamManager");
            stretchableObstacleType = AccessTools.TypeByName("StretchableObstacle");
            var modmapType = AccessTools.TypeByName(
                "Camera2.Configuration.Settings_ModmapExtensions");
            var hookLeveldataType = AccessTools.TypeByName(
                "Camera2.HarmonyPatches.HookLeveldata");
            if (settingsType == null || gameObjectsType == null ||
                cam2Type == null || managerType == null ||
                stretchableObstacleType == null || modmapType == null ||
                hookLeveldataType == null)
                throw new TypeLoadException("Camera2 wall compatibility types are unavailable");

            settingsCamGetter = AccessTools.PropertyGetter(settingsType, "cam");
            settingsVisibleObjectsGetter = AccessTools.PropertyGetter(
                settingsType, "visibleObjects");
            visibleObjectsWallsGetter = AccessTools.PropertyGetter(
                gameObjectsType, "Walls");
            settingsModmapGetter = AccessTools.PropertyGetter(
                settingsType, "ModmapExtensions");
            cam2CameraGetter = AccessTools.PropertyGetter(cam2Type, "UCamera");
            camManagerCamsGetter = AccessTools.PropertyGetter(managerType, "cams");
            cam2SettingsProperty = AccessTools.Property(cam2Type, "settings");
            obstacleCoreField = AccessTools.Field(stretchableObstacleType, "_obstacleCore");
            obstacleFrameField = AccessTools.Field(stretchableObstacleType, "_obstacleFrame");
            obstacleFakeGlowField = AccessTools.Field(stretchableObstacleType, "_obstacleFakeGlow");
            autoOpaqueWallsField = AccessTools.Field(modmapType, "autoOpaqueWalls");
            isWallMapField = AccessTools.Field(hookLeveldataType, "isWallMap");

            if (settingsCamGetter == null || settingsVisibleObjectsGetter == null ||
                visibleObjectsWallsGetter == null || settingsModmapGetter == null ||
                cam2CameraGetter == null || camManagerCamsGetter == null ||
                cam2SettingsProperty == null || !cam2SettingsProperty.CanRead ||
                obstacleCoreField == null || obstacleFrameField == null ||
                obstacleFakeGlowField == null || autoOpaqueWallsField == null ||
                isWallMapField == null)
                throw new MissingMethodException(
                    "Camera2 wall compatibility accessors are unavailable");
        }

        static void RefreshExistingCameras()
        {
            if (camManagerCamsGetter == null) return;
            try
            {
                var cams = camManagerCamsGetter.Invoke(null, null);
                if (cams == null) return;
                var valuesProperty = cams.GetType().GetProperty("Values",
                    BindingFlags.Instance | BindingFlags.Public);
                var values = valuesProperty == null ? null :
                    valuesProperty.GetValue(cams, null) as IEnumerable;
                if (values == null) return;

                foreach (var cam in values)
                {
                    if (cam == null) continue;
                    var settings = cam2SettingsProperty.GetValue(cam, null);
                    if (settings != null) ApplySettingsMaskSafely(settings);
                }
            }
            catch (Exception exception)
            {
                Runtime.LogError("Camera2 existing wall masks could not be refreshed", exception);
            }
        }

        static void ApplySettingsMask(object settings)
        {
            if (settings == null) return;

            var cam2 = settingsCamGetter.Invoke(settings, null);
            if (cam2 == null) return;
            var camera = cam2CameraGetter.Invoke(cam2, null) as Camera;
            if (camera == null) return;

            var visibleObjects = settingsVisibleObjectsGetter.Invoke(settings, null);
            if (visibleObjects == null) return;
            var wallMode = GetEffectiveWallMode(settings, visibleObjects);

            // ScreenDisplacement (17) and DontShowInExternalMRCamera (27)
            // are shared by native non-wall objects (for example Shockwave
            // and environment/MR objects).  Only remove the obsolete Camera2
            // FixMRAlpha bit here; wall core/frame renderers are handled in
            // the per-Camera2 render scope below.
            var mask = camera.cullingMask & ~fixMrAlphaBit;
            switch (wallMode)
            {
                case VisibleWalls:
                    mask |= nativeCoreBits;
                    mask |= obstacleFrameBit;
                    break;
                case TransparentWalls:
                    mask |= obstacleFrameBit;
                    break;
                case HiddenWalls:
                    mask &= ~obstacleFrameBit;
                    break;
                default:
                    mask &= ~obstacleFrameBit;
                    break;
            }

            if (camera.cullingMask != mask)
                camera.cullingMask = mask;
        }

        static int GetEffectiveWallMode(object settings, object visibleObjects)
        {
            var wallModeValue = visibleObjectsWallsGetter.Invoke(visibleObjects, null);
            var wallMode = Convert.ToInt32(wallModeValue);
            if (wallMode == VisibleWalls) return VisibleWalls;

            // This is the same override used by Camera2's original
            // ApplyLayerBitmask: on wall maps, autoOpaqueWalls makes both
            // Transparent and Hidden configurations render opaque walls.
            var modmap = settingsModmapGetter.Invoke(settings, null);
            var autoOpaque = modmap != null &&
                Convert.ToBoolean(autoOpaqueWallsField.GetValue(modmap));
            var isWallMap = Convert.ToBoolean(isWallMapField.GetValue(null));
            return autoOpaque && isWallMap ? VisibleWalls : wallMode;
        }

        static void ApplySettingsMaskSafely(object settings)
        {
            try
            {
                ApplySettingsMask(settings);
            }
            catch (Exception exception)
            {
                if (maskFailureLogged) return;
                maskFailureLogged = true;
                Runtime.LogError("Camera2 wall mask adaptation failed", exception);
            }
        }

        static void SubscribeRenderEvents()
        {
            if (renderEventsInstalled) return;
            RenderPipelineManager.beginCameraRendering += BeginCameraRendering;
            RenderPipelineManager.endCameraRendering += EndCameraRendering;
            renderEventsInstalled = true;
        }

        static void UnsubscribeRenderEvents()
        {
            if (!renderEventsInstalled) return;
            RenderPipelineManager.beginCameraRendering -= BeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= EndCameraRendering;
            renderEventsInstalled = false;
        }

        static void RememberExistingCameras()
        {
            if (cam2Type == null) return;
            foreach (var value in Resources.FindObjectsOfTypeAll(cam2Type))
                TrackCamera2(value);
        }

        static void TrackCamera2(object value)
        {
            var component = value as Component;
            if (component == null || component.gameObject == null) return;
            try
            {
                if (!component.gameObject.scene.IsValid()) return;
            }
            catch
            {
                return;
            }

            for (var index = 0; index < TrackedCam2.Count; index++)
                if (ReferenceEquals(TrackedCam2[index], value)) return;
            TrackedCam2.Add(value);
        }

        static object FindCamera2Owner(Camera camera)
        {
            for (var index = TrackedCam2.Count - 1; index >= 0; index--)
            {
                var cam2 = TrackedCam2[index];
                var component = cam2 as Component;
                if (component == null || component.gameObject == null)
                {
                    TrackedCam2.RemoveAt(index);
                    continue;
                }

                try
                {
                    var ownerCamera = cam2CameraGetter.Invoke(cam2, null) as Camera;
                    if (ownerCamera == camera) return cam2;
                }
                catch (Exception exception)
                {
                    if (!maskFailureLogged)
                    {
                        maskFailureLogged = true;
                        Runtime.LogError("Camera2 UCamera lookup failed", exception);
                    }
                }
            }
            return null;
        }

        static void BeginCameraRendering(
            ScriptableRenderContext context, Camera camera)
        {
            var owner = FindCamera2Owner(camera);
            if (owner != null && activeRenderScope != null && activeRenderCamera == camera)
                return;
            RestoreActiveRenderScope();
            if (owner == null) return;
            try
            {
                activeRenderCamera = camera;
                activeRenderScope = BeginWallRender(owner);
                if (activeRenderScope == null) activeRenderCamera = null;
            }
            catch (Exception exception)
            {
                activeRenderCamera = null;
                activeRenderScope = null;
                if (!maskFailureLogged)
                {
                    maskFailureLogged = true;
                    Runtime.LogError("Camera2 wall render scope failed", exception);
                }
            }
        }

        static void EndCameraRendering(
            ScriptableRenderContext context, Camera camera)
        {
            if (activeRenderScope == null) return;
            if (activeRenderCamera != null && activeRenderCamera != camera) return;
            RestoreActiveRenderScope();
        }

        static WallRenderScope BeginForceRenderScope(object cam2)
        {
            var camera = cam2 == null ? null : cam2CameraGetter.Invoke(cam2, null) as Camera;
            if (camera == null) return null;

            if (activeRenderScope != null && activeRenderCamera == camera)
                return activeRenderScope;
            RestoreActiveRenderScope();
            activeRenderCamera = camera;
            activeRenderScope = BeginWallRender(cam2);
            if (activeRenderScope == null) activeRenderCamera = null;
            return activeRenderScope;
        }

        static void RestoreActiveRenderScope()
        {
            var scope = activeRenderScope;
            activeRenderScope = null;
            activeRenderCamera = null;
            if (scope == null) return;
            try { scope.Restore(); }
            catch (Exception exception)
            {
                Runtime.LogError("Camera2 wall render scope restore failed", exception);
            }
        }

        static void RestoreForceRenderScope(WallRenderScope scope)
        {
            if (scope == null) return;
            if (ReferenceEquals(activeRenderScope, scope))
            {
                activeRenderScope = null;
                activeRenderCamera = null;
            }
            try { scope.Restore(); }
            catch (Exception exception)
            {
                Runtime.LogError("Camera2 wall render scope restore failed", exception);
            }
        }

        static void RememberExistingWalls()
        {
            if (stretchableObstacleType == null) return;
            foreach (var value in Resources.FindObjectsOfTypeAll(stretchableObstacleType))
                TrackWall(value);
        }

        static void TrackWall(object value)
        {
            TrackedWalls.RemoveAll(wall => wall == null);
            var component = value as Component;
            if (component == null || component.gameObject == null) return;
            try
            {
                if (!component.gameObject.scene.IsValid()) return;
            }
            catch
            {
                return;
            }

            for (var index = 0; index < TrackedWalls.Count; index++)
                if (TrackedWalls[index] == component) return;
            TrackedWalls.Add(component);
        }

        static WallRenderScope BeginWallRender(object cam2)
        {
            TrackedWalls.RemoveAll(wall => wall == null);
            if (cam2 == null) return null;
            var settings = cam2SettingsProperty.GetValue(cam2, null);
            if (settings == null) return null;
            var visibleObjects = settingsVisibleObjectsGetter.Invoke(settings, null);
            if (visibleObjects == null) return null;
            var mode = GetEffectiveWallMode(settings, visibleObjects);
            if (mode == VisibleWalls) return null;
            var hideFrame = mode != TransparentWalls;

            var scope = new WallRenderScope();
            for (var index = TrackedWalls.Count - 1; index >= 0; index--)
            {
                var wall = TrackedWalls[index];
                if (wall == null || wall.gameObject == null)
                {
                    TrackedWalls.RemoveAt(index);
                    continue;
                }

                HideRenderers(obstacleCoreField.GetValue(wall), scope);
                if (hideFrame)
                {
                    HideRenderers(obstacleFrameField.GetValue(wall), scope);
                    HideRenderers(obstacleFakeGlowField.GetValue(wall), scope);
                }
            }
            return scope;
        }

        static void HideRenderers(object owner, WallRenderScope scope)
        {
            var component = owner as Component;
            if (component == null) return;
            var renderers = component.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
                scope.Disable(renderer);
        }

        internal sealed class WallRenderScope
        {
            sealed class RendererState
            {
                internal Renderer Renderer;
                internal bool Enabled;
            }

            readonly List<RendererState> states = new List<RendererState>();
            bool restored;

            internal void Disable(Renderer renderer)
            {
                if (renderer == null) return;
                for (var index = 0; index < states.Count; index++)
                    if (states[index].Renderer == renderer) return;
                states.Add(new RendererState {
                    Renderer = renderer,
                    Enabled = renderer.enabled
                });
                renderer.enabled = false;
            }

            internal void Restore()
            {
                if (restored) return;
                restored = true;
                for (var index = states.Count - 1; index >= 0; index--)
                {
                    var state = states[index];
                    if (state.Renderer != null)
                        state.Renderer.enabled = state.Enabled;
                }
                states.Clear();
            }
        }

        internal static class WallHooks
        {
            internal static bool BlockSharedWallMutationPrefix()
            {
                // The original Camera2 method is a Harmony postfix.  Its
                // prefix is patched here so shared StretchableObstacle
                // children keep the native layers used by the URP renderer.
                return false;
            }

            internal static bool BlockMainCameraWallMutationPrefix()
            {
                // The old method unconditionally adds FixMRAlpha to the
                // native main camera after StartSong.  Skip it without
                // changing the native camera's own mask here.
                return false;
            }

            internal static void TrackWallPostfix(object __instance)
            {
                TrackWall(__instance);
            }

            internal static void TrackCam2Postfix(object __instance)
            {
                TrackCamera2(__instance);
            }

            internal static void PrepareWallRenderPrefix(
                object __instance, bool forceRender, out WallRenderScope __state)
            {
                __state = null;
                if (!forceRender) return;
                try
                {
                    __state = BeginForceRenderScope(__instance);
                }
                catch (Exception exception)
                {
                    if (!maskFailureLogged)
                    {
                        maskFailureLogged = true;
                        Runtime.LogError("Camera2 wall render scope failed", exception);
                    }
                }
            }

            internal static void PrepareWallRenderPostfix(WallRenderScope __state)
            {
                RestoreForceRenderScope(__state);
            }

            internal static Exception PrepareWallRenderFinalizer(
                Exception __exception, WallRenderScope __state)
            {
                RestoreForceRenderScope(__state);
                return __exception;
            }

            internal static void ApplyLayerBitmaskPostfix(object __instance)
            {
                ApplySettingsMaskSafely(__instance);
            }

            internal static void ApplyCameraValuesPostfix()
            {
                RefreshExistingCameras();
            }
        }
    }
}
