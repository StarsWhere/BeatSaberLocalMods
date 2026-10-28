using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace Camera2UrpFix
{
    // Camera2 owns its shader chain and settings. URP supplies the completed
    // mono target here because it does not dispatch OnRenderImage itself.
    internal static class PostProcessingCompatibility
    {
        sealed class State
        {
            internal object Cam2;
            internal Camera Camera;
            internal Behaviour Processor;
            internal UniversalAdditionalCameraData Data;
            internal bool OriginalDepth;
            internal bool Processing;
            internal object FailedSettings;
            internal Array FailedShaders;
            internal float FailedThreshold;
        }

        static readonly Dictionary<int, State> States = new Dictionary<int, State>();
        static Type processorType;
        static PropertyInfo cameraProperty;
        static PropertyInfo settingsProperty;
        static PropertyInfo viewProperty;
        static PropertyInfo postProcessingProperty;
        static FieldInfo thresholdField;
        static FieldInfo shadersField;
        static MethodInfo process;
        static MethodInfo completed;
        static bool installed;

        internal static void Install(Harmony harmony)
        {
            var camType = AccessTools.TypeByName("Camera2.Behaviours.Cam2");
            processorType = AccessTools.TypeByName("Camera2.Behaviours.CamPostProcessor");
            cameraProperty = AccessTools.Property(camType, "UCamera");
            settingsProperty = AccessTools.Property(camType, "settings");
            viewProperty = AccessTools.Property(camType, "previewImage");
            postProcessingProperty = AccessTools.Property(settingsProperty.PropertyType, "PostProcessing");
            thresholdField = AccessTools.Field(postProcessingProperty.PropertyType, "transparencyThreshold");
            shadersField = AccessTools.Field(postProcessingProperty.PropertyType, "shaders");
            process = AccessTools.Method(processorType, "OnRenderImage", new[] { typeof(RenderTexture), typeof(RenderTexture) });
            completed = AccessTools.Method(camType, "PostprocessCompleted");
            if (cameraProperty == null || viewProperty == null || thresholdField == null || shadersField == null || process == null || completed == null)
                throw new MissingMemberException("Camera2 0.6.119 post-processing contract changed.");
            harmony.Patch(AccessTools.Method(camType, "Init"), postfix: new HarmonyMethod(typeof(PostProcessingCompatibility), "Configure"));
            harmony.Patch(AccessTools.Method(camType, "UpdateDepthTextureActive"), postfix: new HarmonyMethod(typeof(PostProcessingCompatibility), "Configure"));
            RenderPipelineManager.endCameraRendering += EndCameraRendering;
            installed = true;
        }

        public static void Configure(object __instance)
        {
            var camera = cameraProperty.GetValue(__instance, null) as Camera;
            if (camera == null) return;
            Runtime.DisableCameraXr(camera);
            var processor = camera.GetComponent(processorType) as Behaviour;
            if (processor == null) return;
            State state;
            if (!States.TryGetValue(camera.GetInstanceID(), out state))
            {
                var data = camera.GetComponent<UniversalAdditionalCameraData>();
                state = new State { Cam2 = __instance, Camera = camera, Processor = processor, Data = data, OriginalDepth = data.requiresDepthTexture };
                States.Add(camera.GetInstanceID(), state);
            }
            state.Data.requiresDepthTexture = (camera.depthTextureMode & (DepthTextureMode.Depth | DepthTextureMode.DepthNormals)) != 0;
            state.FailedSettings = null;
        }

        static void EndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (camera == null || GraphicsSettings.currentRenderPipeline == null) return;
            State state;
            if (!States.TryGetValue(camera.GetInstanceID(), out state)) return;
            var target = camera.targetTexture;
            if (target == null || state.Processor == null || state.Processing) return;
            if (target.dimension != TextureDimension.Tex2D || target.volumeDepth != 1 || state.Data.allowXRRendering)
            {
                Runtime.LogError("Camera2 post-processing rejected a non-mono render target", new InvalidOperationException(camera.name));
                return;
            }
            ProcessFrame(state, target);
        }

        static void ProcessFrame(State state, RenderTexture target)
        {
            var settings = settingsProperty.GetValue(state.Cam2, null);
            var options = postProcessingProperty.GetValue(settings, null);
            var threshold = (float)thresholdField.GetValue(options);
            var shaders = shadersField.GetValue(options) as Array;
            var view = viewProperty.GetValue(state.Cam2, null) as RawImage;
            if (ReferenceEquals(state.FailedSettings, options) && ReferenceEquals(state.FailedShaders, shaders) && state.FailedThreshold == threshold)
            {
                // A changed/reloaded configuration retries. Leave the scene RGB
                // visible while the previous configuration's diagnostic remains.
                DesktopCanvasOwner.SetTransparency(view, false);
                completed.Invoke(state.Cam2, null);
                return;
            }

            RenderTexture output = null;
            var previousTarget = RenderTexture.active;
            var previousSrgb = GL.sRGBWrite;
            state.Processing = true;
            try
            {
                var descriptor = target.descriptor;
                descriptor.depthBufferBits = 0;
                descriptor.depthStencilFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.None;
                descriptor.msaaSamples = 1;
                descriptor.bindMS = false;
                descriptor.useMipMap = false;
                descriptor.autoGenerateMips = false;
                output = RenderTexture.GetTemporary(descriptor);
                GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear && target.sRGB;
                process.Invoke(state.Processor, new object[] { target, output });
                Graphics.Blit(output, target);
                DesktopCanvasOwner.SetTransparency(view, state.Processor.enabled && threshold > 0f);
                state.FailedSettings = null;
            }
            catch (Exception error)
            {
                state.FailedSettings = options;
                state.FailedShaders = shaders;
                state.FailedThreshold = threshold;
                DesktopCanvasOwner.SetTransparency(view, false);
                var cause = error is TargetInvocationException && error.InnerException != null ? error.InnerException : error;
                Runtime.LogError("Camera2 post-processing failed for " + state.Camera.name + "; reload or change its effect settings to retry", cause);
                // The original routine normally releases its middleware here.
                // Preserve that lifetime boundary even when a filter rejects input.
                try { completed.Invoke(state.Cam2, null); }
                catch (Exception cleanupError) { Runtime.LogError("Camera2 post-processing middleware cleanup failed", cleanupError); }
            }
            finally
            {
                RenderTexture.active = previousTarget;
                GL.sRGBWrite = previousSrgb;
                if (output != null) RenderTexture.ReleaseTemporary(output);
                state.Processing = false;
            }
        }

        internal static void Forget(Camera camera)
        {
            if (ReferenceEquals(camera, null)) return;
            States.Remove(camera.GetInstanceID());
        }

        internal static void Release()
        {
            if (installed) RenderPipelineManager.endCameraRendering -= EndCameraRendering;
            installed = false;
            foreach (var state in States.Values)
                if (state.Data != null) state.Data.requiresDepthTexture = state.OriginalDepth;
            States.Clear();
        }
    }
}
