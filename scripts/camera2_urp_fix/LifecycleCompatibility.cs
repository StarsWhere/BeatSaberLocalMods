using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Camera2UrpFix
{
    // Camera2 0.6.119 creates these objects per Cam2/PositionableCam instance.
    // Keep this adapter tied to those ownership boundaries so game-owned
    // cameras and shared Camera2 materials are never destroyed here.
    internal static class LifecycleCompatibility
    {
        const string Cam2Name = "Camera2.Behaviours.Cam2";
        const string CamManagerName = "Camera2.Managers.CamManager";
        const string PositionableCamName = "Camera2.Behaviours.PositionableCam";

        static readonly List<MethodBase> PatchedMethods = new List<MethodBase>();
        static Harmony harmony;
        static bool installed;

        static Type cam2Type;
        static Type camManagerType;
        static Type positionableCamType;
        static PropertyInfo cameraProperty;
        static PropertyInfo renderTextureProperty;
        static PropertyInfo worldCamProperty;
        static FieldInfo viewMaterialField;
        static FieldInfo normalMaterialField;
        static FieldInfo hoverMaterialField;
        static MethodInfo postprocessCompleted;

        internal static void Install(Harmony owner)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            if (installed)
            {
                if (ReferenceEquals(harmony, owner)) return;
                Release();
            }

            ResetMembers();
            PatchedMethods.Clear();
            harmony = owner;
            try
            {
                ResolveMembers();
                var prepare = AccessTools.Method(cam2Type, "PrepareMiddlewaredRender", new[] { typeof(bool) });
                var update = AccessTools.Method(cam2Type, "UpdateRenderTextureAndView", Type.EmptyTypes);
                var destroy = AccessTools.Method(cam2Type, "OnDestroy", Type.EmptyTypes);
                var load = AccessTools.Method(camManagerType, "LoadCameras", new[] { typeof(bool) });
                Require(prepare, "Cam2.PrepareMiddlewaredRender");
                Require(update, "Cam2.UpdateRenderTextureAndView");
                Require(destroy, "Cam2.OnDestroy");
                Require(load, "CamManager.LoadCameras");

                PatchMethod(
                    owner,
                    prepare,
                    new HarmonyMethod(typeof(LifecycleCompatibility), "PreparePrefix"),
                    null,
                    new HarmonyMethod(typeof(LifecycleCompatibility), "PrepareTranspiler"));
                PatchMethod(
                    owner,
                    update,
                    new HarmonyMethod(typeof(LifecycleCompatibility), "UpdatePrefix"),
                    new HarmonyMethod(typeof(LifecycleCompatibility), "UpdatePostfix"),
                    null);
                PatchMethod(
                    owner,
                    destroy,
                    new HarmonyMethod(typeof(LifecycleCompatibility), "DestroyPrefix"),
                    null,
                    null);
                PatchMethod(
                    owner,
                    load,
                    null,
                    null,
                    new HarmonyMethod(typeof(LifecycleCompatibility), "LoadCamerasTranspiler"));

                installed = true;
            }
            catch
            {
                Rollback(owner);
                harmony = null;
                installed = false;
                ResetMembers();
                throw;
            }
        }

        internal static void Release()
        {
            if (harmony != null)
            {
                for (var index = PatchedMethods.Count - 1; index >= 0; index--)
                {
                    try
                    {
                        harmony.Unpatch(PatchedMethods[index], HarmonyPatchType.All, harmony.Id);
                    }
                    catch (Exception exception)
                    {
                        Runtime.LogError("Camera2 lifecycle patch release failed", exception);
                    }
                }
            }
            PatchedMethods.Clear();
            harmony = null;
            installed = false;
            ResetMembers();
        }

        static void ResolveMembers()
        {
            cam2Type = AccessTools.TypeByName(Cam2Name);
            camManagerType = AccessTools.TypeByName(CamManagerName);
            positionableCamType = AccessTools.TypeByName(PositionableCamName);
            if (cam2Type == null || camManagerType == null || positionableCamType == null)
                throw new TypeLoadException("Camera2 0.6.119 lifecycle types are unavailable");

            cameraProperty = AccessTools.Property(cam2Type, "UCamera");
            renderTextureProperty = AccessTools.Property(cam2Type, "renderTexture");
            worldCamProperty = AccessTools.Property(cam2Type, "worldCam");
            viewMaterialField = AccessTools.Field(positionableCamType, "viewMaterial");
            normalMaterialField = AccessTools.Field(positionableCamType, "normalMaterial");
            hoverMaterialField = AccessTools.Field(positionableCamType, "hoverMaterial");
            postprocessCompleted = AccessTools.Method(cam2Type, "PostprocessCompleted", Type.EmptyTypes);
            Require(postprocessCompleted, "Cam2.PostprocessCompleted");

            if (cameraProperty == null || !cameraProperty.CanRead || cameraProperty.PropertyType != typeof(Camera))
                throw new MissingMemberException(Cam2Name, "UCamera");
            if (renderTextureProperty == null || !renderTextureProperty.CanRead ||
                renderTextureProperty.PropertyType != typeof(RenderTexture))
                throw new MissingMemberException(Cam2Name, "renderTexture");
            if (worldCamProperty == null || !worldCamProperty.CanRead ||
                worldCamProperty.PropertyType != positionableCamType)
                throw new MissingMemberException(Cam2Name, "worldCam");
            if (viewMaterialField == null || viewMaterialField.IsStatic ||
                viewMaterialField.FieldType != typeof(Material))
                throw new MissingMemberException(PositionableCamName, "viewMaterial");
            if (normalMaterialField == null || !normalMaterialField.IsStatic ||
                normalMaterialField.FieldType != typeof(Material))
                throw new MissingMemberException(PositionableCamName, "normalMaterial");
            if (hoverMaterialField == null || !hoverMaterialField.IsStatic ||
                hoverMaterialField.FieldType != typeof(Material))
                throw new MissingMemberException(PositionableCamName, "hoverMaterial");
        }

        static void ResetMembers()
        {
            cam2Type = null;
            camManagerType = null;
            positionableCamType = null;
            cameraProperty = null;
            renderTextureProperty = null;
            worldCamProperty = null;
            viewMaterialField = null;
            normalMaterialField = null;
            hoverMaterialField = null;
            postprocessCompleted = null;
        }

        static void Require(MethodBase method, string name)
        {
            if (method == null) throw new MissingMethodException(name);
        }

        static void PatchMethod(
            Harmony owner,
            MethodBase original,
            HarmonyMethod prefix,
            HarmonyMethod postfix,
            HarmonyMethod transpiler)
        {
            PatchedMethods.Add(original);
            // The six-argument overload is the non-obsolete Harmony 2.16 API.
            owner.Patch(original, prefix, postfix, transpiler, null, null);
        }

        static void Rollback(Harmony owner)
        {
            for (var index = PatchedMethods.Count - 1; index >= 0; index--)
            {
                try { owner.Unpatch(PatchedMethods[index], HarmonyPatchType.All, owner.Id); }
                catch (Exception exception) { Runtime.LogError("Camera2 lifecycle patch rollback failed", exception); }
            }
            PatchedMethods.Clear();
        }

        static void PreparePrefix(object __instance)
        {
            // Init's postfix runs after the first render setup.  This prefix
            // disables XR at the actual middleware render boundary.
            Runtime.DisableCameraXr(__instance);
        }

        static IEnumerable<CodeInstruction> PrepareTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var replacement = AccessTools.Method(
                typeof(LifecycleCompatibility), "RenderCamera", new[] { typeof(Camera) });
            if (replacement == null) throw new MissingMethodException("LifecycleCompatibility.RenderCamera");

            var cameraRenderIndex = -1;
            var cameraRenderCount = 0;
            for (var index = 0; index < list.Count; index++)
            {
                var method = list[index].operand as MethodInfo;
                if (method == null || list[index].opcode != OpCodes.Callvirt || method.Name != "Render" ||
                    method.DeclaringType != typeof(Camera) || method.ReturnType != typeof(void) ||
                    method.GetParameters().Length != 0)
                    continue;
                cameraRenderCount++;
                var previous = index == 0 ? null : list[index - 1].operand as MethodInfo;
                if (previous != null && previous.Name == "get_UCamera" && previous.DeclaringType == cam2Type &&
                    previous.ReturnType == typeof(Camera) && previous.GetParameters().Length == 0)
                    cameraRenderIndex = index;
            }

            if (cameraRenderCount != 1 || cameraRenderIndex < 0)
                throw new InvalidOperationException(
                    "Camera2 PrepareMiddlewaredRender render hook shape changed; expected one Cam2 UCamera.Render call");
            list[cameraRenderIndex].opcode = OpCodes.Call;
            list[cameraRenderIndex].operand = replacement;
            return list;
        }

        internal static void RenderCamera(Camera camera)
        {
            if (camera == null) return;
            var target = camera.targetTexture;
            if (target == null)
            {
                Runtime.LogWarning("Camera2 render request skipped because UCamera.targetTexture is null");
                CompleteFailedRender(camera);
                return;
            }
            try
            {
                var data = camera.GetComponent<UniversalAdditionalCameraData>();
                if (data != null) data.allowXRRendering = false;
                var request = new RenderPipeline.StandardRequest();
                request.destination = target;
                request.mipLevel = 0;
                request.face = CubemapFace.Unknown;
                request.slice = 0;
                if (!RenderPipeline.SupportsRenderRequest<RenderPipeline.StandardRequest>(camera, request))
                {
                    Runtime.LogWarning("Camera2 URP render request is unsupported; legacy Camera.Render was not used");
                    CompleteFailedRender(camera);
                    return;
                }
                RenderPipeline.SubmitRenderRequest<RenderPipeline.StandardRequest>(camera, request);
            }
            catch (Exception exception)
            {
                Runtime.LogError("Camera2 URP render request failed", exception);
                CompleteFailedRender(camera);
            }
        }

        static void CompleteFailedRender(Camera camera)
        {
            // Successful post-processing already disables the source camera.
            // A request rejected before endCameraRendering must close the same
            // middleware lifetime boundary before a later frame can retry.
            if (camera == null || !camera.enabled) return;
            try
            {
                var owner = camera.GetComponentInParent(cam2Type, true);
                if (owner != null) postprocessCompleted.Invoke(owner, null);
                else camera.enabled = false;
            }
            catch (Exception exception)
            {
                camera.enabled = false;
                Runtime.LogError("Camera2 failed render middleware cleanup failed", exception);
            }
        }

        static void UpdatePrefix(object __instance, out RenderTexture __state)
        {
            __state = GetRenderTexture(__instance);
        }

        static void UpdatePostfix(object __instance, RenderTexture __state)
        {
            var current = GetRenderTexture(__instance);
            if (__state != null && !SameObject(__state, current))
                ReleaseAndDestroy(__state);
        }

        static void DestroyPrefix(object __instance)
        {
            var camera = GetCamera(__instance);
            if (camera != null) Runtime.ForgetSourceCamera(camera);

            var texture = GetRenderTexture(__instance);
            if (texture != null) ReleaseAndDestroy(texture);

            DestroyViewMaterial(GetWorldCam(__instance));
        }

        static RenderTexture GetRenderTexture(object cam2)
        {
            if (cam2 == null || renderTextureProperty == null) return null;
            return renderTextureProperty.GetValue(cam2, null) as RenderTexture;
        }

        static Camera GetCamera(object cam2)
        {
            if (cam2 == null || cameraProperty == null) return null;
            return cameraProperty.GetValue(cam2, null) as Camera;
        }

        static object GetWorldCam(object cam2)
        {
            if (cam2 == null || worldCamProperty == null) return null;
            return worldCamProperty.GetValue(cam2, null);
        }

        static Material GetViewMaterial(object positionableCam)
        {
            if (positionableCam == null || viewMaterialField == null) return null;
            return viewMaterialField.GetValue(positionableCam) as Material;
        }

        static void ReleaseAndDestroy(RenderTexture texture)
        {
            if (texture == null) return;
            try { texture.Release(); }
            catch (Exception exception) { Runtime.LogError("Camera2 RenderTexture.Release failed", exception); }
            try { UnityEngine.Object.Destroy(texture); }
            catch (Exception exception) { Runtime.LogError("Camera2 RenderTexture.Destroy failed", exception); }
        }

        static void DestroyViewMaterial(object positionableCam)
        {
            var material = GetViewMaterial(positionableCam);
            if (material == null || IsSharedMaterial(material)) return;
            try { UnityEngine.Object.Destroy(material); }
            catch (Exception exception) { Runtime.LogError("Camera2 PositionableCam viewMaterial.Destroy failed", exception); }
        }

        static bool IsSharedMaterial(Material material)
        {
            return SameObject(GetStaticMaterial(normalMaterialField), material) ||
                SameObject(GetStaticMaterial(hoverMaterialField), material);
        }

        static Material GetStaticMaterial(FieldInfo field)
        {
            return field == null ? null : field.GetValue(null) as Material;
        }

        static IEnumerable<CodeInstruction> LoadCamerasTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            MethodInfo whereCall = null;
            Type elementType = null;
            var whereCount = 0;
            var enumeratorPairCount = 0;
            var enumeratorIndex = -1;

            for (var index = 0; index < list.Count; index++)
            {
                var method = list[index].operand as MethodInfo;
                if (!IsWhereCall(method)) continue;
                whereCount++;
                var currentElementType = method.GetGenericArguments()[0];
                if (elementType == null) elementType = currentElementType;
                if (elementType != currentElementType)
                    throw new InvalidOperationException("Camera2 LoadCameras Where element type changed");
                whereCall = method;

                var next = index + 1 < list.Count ? list[index + 1].operand as MethodInfo : null;
                if (IsEnumeratorCall(next, currentElementType))
                {
                    enumeratorPairCount++;
                    enumeratorIndex = index + 1;
                }
            }

            // The installed Camera2 binary has one deferred Where source.
            // Snapshot it before the loop removes entries from its dictionary.
            if (whereCount != 1 || enumeratorPairCount != 1 ||
                whereCall == null || enumeratorIndex < 0)
                throw new InvalidOperationException(
                    "Camera2 CamManager.LoadCameras enumeration hook shape changed; expected one Where/GetEnumerator pair");

            var toArray = FindToArray(elementType);
            if (toArray == null) throw new MissingMethodException("Enumerable.ToArray");
            var snapshot = new CodeInstruction(OpCodes.Call, toArray);
            snapshot.labels.AddRange(list[enumeratorIndex].labels);
            list[enumeratorIndex].labels.Clear();
            snapshot.blocks.AddRange(list[enumeratorIndex].blocks);
            list[enumeratorIndex].blocks.Clear();
            list.Insert(enumeratorIndex, snapshot);
            return list;
        }

        static bool IsWhereCall(MethodInfo method)
        {
            if (method == null || method.Name != "Where" || method.DeclaringType != typeof(Enumerable) ||
                !method.IsGenericMethod || method.GetGenericArguments().Length != 1 ||
                method.GetParameters().Length != 2)
                return false;
            var parameters = method.GetParameters();
            return parameters[0].ParameterType.IsGenericType &&
                parameters[0].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>) &&
                parameters[1].ParameterType.IsGenericType &&
                parameters[1].ParameterType.GetGenericTypeDefinition() == typeof(Func<,>) &&
                parameters[1].ParameterType.GetGenericArguments()[1] == typeof(bool);
        }

        static bool IsEnumeratorCall(MethodInfo method, Type elementType)
        {
            return method != null && method.Name == "GetEnumerator" && method.DeclaringType != null &&
                method.DeclaringType.IsGenericType &&
                method.DeclaringType.GetGenericTypeDefinition() == typeof(IEnumerable<>) &&
                method.GetParameters().Length == 0 && method.ReturnType.IsGenericType &&
                method.ReturnType.GetGenericTypeDefinition() == typeof(IEnumerator<>) &&
                method.ReturnType.GetGenericArguments()[0] == elementType;
        }

        static MethodInfo FindToArray(Type elementType)
        {
            foreach (var method in typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != "ToArray" || !method.IsGenericMethodDefinition ||
                    method.GetGenericArguments().Length != 1)
                    continue;
                var parameters = method.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType.IsGenericType &&
                    parameters[0].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    return method.MakeGenericMethod(elementType);
            }
            return null;
        }

        static bool SameObject(UnityEngine.Object left, UnityEngine.Object right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            try { return left.GetInstanceID() == right.GetInstanceID(); }
            catch { return false; }
        }
    }
}
