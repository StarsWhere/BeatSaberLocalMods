using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NoteMotionControl
{
    /// <summary>
    /// Applies the small, native note-motion choices exposed by the training
    /// profile.  The native movement evaluators remain responsible for their
    /// clock, transforms, callbacks and marker events; this class only edits
    /// the spawn inputs before those evaluators consume them.
    /// </summary>
    internal static class MotionRuntime
    {
        private sealed class NoteHideState
        {
            internal bool Owned;
            internal bool External;
            internal bool Changing;
        }

        private sealed class SliderHideState
        {
            internal bool Owned;
            internal bool External;
            internal bool Changing;
        }

        private static readonly Dictionary<NoteController, NoteHideState> noteHides =
            new Dictionary<NoteController, NoteHideState>();
        private static readonly Dictionary<SliderController, SliderHideState> sliderHides =
            new Dictionary<SliderController, SliderHideState>();

        private static readonly FieldInfo NoteMovementProvider =
            AccessTools.Field(typeof(NoteMovement), "_variableMovementDataProvider");
        private static readonly FieldInfo SliderMovementProvider =
            AccessTools.Field(typeof(SliderMovement), "_variableMovementDataProvider");
        private static readonly FieldInfo NoteControllerMovement =
            AccessTools.Field(typeof(NoteController), "_noteMovement");
        private static readonly FieldInfo NoteJumpProvider =
            AccessTools.Field(typeof(NoteJump), "_variableMovementDataProvider");
        private static readonly FieldInfo NoteJumpStartRotation =
            AccessTools.Field(typeof(NoteJump), "_startRotation");
        private static readonly FieldInfo NoteJumpMiddleRotation =
            AccessTools.Field(typeof(NoteJump), "_middleRotation");
        private static readonly FieldInfo NoteJumpEndRotation =
            AccessTools.Field(typeof(NoteJump), "_endRotation");
        private static readonly FieldInfo NoteJumpRotatedObject =
            AccessTools.Field(typeof(NoteJump), "_rotatedObject");
        private static readonly FieldInfo SliderMovementAudio =
            AccessTools.Field(typeof(SliderMovement), "_audioTimeSyncController");
        private static BeatmapObjectSpawnMovementData spawnMovement;
        private static bool interfaceReady = true;

        private sealed class HiddenSliderMotionHost : MonoBehaviour
        {
            private void Update()
            {
                TickHiddenSliders();
            }
        }

        internal static bool ValidateInterface(out string error)
        {
            bool valid =
                NoteMovementProvider != null &&
                SliderMovementProvider != null &&
                NoteControllerMovement != null &&
                NoteJumpProvider != null &&
                SliderMovementAudio != null &&
                NoteJumpStartRotation?.FieldType == typeof(Quaternion) &&
                NoteJumpMiddleRotation?.FieldType == typeof(Quaternion) &&
                NoteJumpEndRotation?.FieldType == typeof(Quaternion) &&
                NoteJumpRotatedObject?.FieldType == typeof(Transform) &&
                AccessTools.Method(
                    typeof(NoteMovement),
                    "Init",
                    new[]
                    {
                        typeof(float),
                        typeof(float),
                        typeof(NoteSpawnData).MakeByRefType(),
                        typeof(float),
                        typeof(float),
                        typeof(bool),
                        typeof(bool),
                    }) != null &&
                AccessTools.Method(
                    typeof(SliderMovement),
                    "Init",
                    new[] { typeof(SliderData), typeof(SliderSpawnData).MakeByRefType() }) != null &&
                AccessTools.Method(
                    typeof(NoteJump),
                    "Init",
                    new[]
                    {
                        typeof(float),
                        typeof(float),
                        typeof(Vector3),
                        typeof(Vector3),
                        typeof(float),
                        typeof(float),
                        typeof(float),
                        typeof(bool),
                        typeof(bool),
                    }) != null &&
                AccessTools.Method(
                    typeof(NoteController),
                    "Init",
                    new[]
                    {
                        typeof(NoteData),
                        typeof(NoteSpawnData).MakeByRefType(),
                        typeof(float),
                        typeof(float),
                        typeof(bool),
                        typeof(bool),
                    }) != null &&
                AccessTools.Method(typeof(NoteController), "Hide", new[] { typeof(bool) }) != null &&
                AccessTools.Method(typeof(NoteController), "HandleNoteDidStartJump") != null &&
                AccessTools.Method(
                    typeof(SliderController),
                    "Init",
                    new[]
                    {
                        typeof(SliderController.LengthType),
                        typeof(SliderData),
                        typeof(SliderSpawnData).MakeByRefType(),
                        typeof(float),
                        typeof(float),
                    }) != null &&
                AccessTools.Method(typeof(SliderController), "ManualUpdate") != null &&
                AccessTools.Method(typeof(SliderController), "Hide", new[] { typeof(bool) }) != null &&
                AccessTools.Method(typeof(BeatmapObjectSpawnMovementData), "Init") != null;

            interfaceReady = valid;
            error = valid
                ? string.Empty
                : "The native note-motion interface is unavailable; original note motion was retained.";
            return valid;
        }

        internal static void Clear()
        {
            noteHides.Clear();
            sliderHides.Clear();
            spawnMovement = null;
            interfaceReady = true;
        }

        internal static void CaptureSpawnMovement(BeatmapObjectSpawnMovementData value)
        {
            spawnMovement = value;
        }

        private static bool EnsureSliderMotionHost(SliderMovement movement)
        {
            if (movement == null || SliderMovementAudio == null) return false;
            try
            {
                Component audioSource = SliderMovementAudio.GetValue(movement) as Component;
                if (audioSource == null)
                {
                    RuntimeState.WarnOnce(
                        "slider-motion-host",
                        "Native slider motion could not be kept updating while pre-jump visuals were hidden.");
                    return false;
                }
                if (audioSource.GetComponent<HiddenSliderMotionHost>() == null)
                    audioSource.gameObject.AddComponent<HiddenSliderMotionHost>();
                return true;
            }
            catch (Exception error)
            {
                RuntimeState.WarnOnce(
                    "slider-motion-host",
                    "Native slider motion could not be kept updating while pre-jump visuals were hidden.");
                RuntimeState.ErrorOnce("slider-motion-host-detail", "Native slider motion host setup failed.", error);
                return false;
            }
        }

        private static void TickHiddenSliders()
        {
            if (!RuntimeState.GameplayActive || sliderHides.Count == 0) return;
            var controllers = new List<SliderController>(sliderHides.Keys);
            foreach (SliderController controller in controllers)
            {
                if (controller == null)
                {
                    sliderHides.Remove(controller);
                    continue;
                }
                if (!sliderHides.TryGetValue(controller, out SliderHideState state) ||
                    !state.Owned || controller.gameObject.activeInHierarchy)
                    continue;
                try
                {
                    // SliderController.Hide disables its visual root. Invoke
                    // the unchanged native ManualUpdate from an active audio
                    // host so movement, callbacks and markers continue to run.
                    controller.ManualUpdate();
                }
                catch (Exception error)
                {
                    RuntimeState.ErrorOnce(
                        "slider-hidden-update",
                        "Native slider motion stopped updating while pre-jump visuals were hidden.",
                        error);
                    RevealSlider(controller);
                }
            }
        }

        private static bool Eligible(IVariableMovementDataProvider provider)
        {
            VariableMovementDataProvider local = provider as VariableMovementDataProvider;
            Options options = RuntimeState.ActiveOptions;
            return interfaceReady &&
                RuntimeState.GameplayActive &&
                RuntimeState.MovementInterfaceReady &&
                !RuntimeState.PreserveAuthoredMotion &&
                options != null &&
                options.Enabled &&
                local != null &&
                local.GetType() == typeof(VariableMovementDataProvider) &&
                RuntimeState.OwnsProvider(local);
        }

        private static bool Eligible(NoteMovement movement, out VariableMovementDataProvider provider)
        {
            provider = movement == null || NoteMovementProvider == null
                ? null
                : NoteMovementProvider.GetValue(movement) as VariableMovementDataProvider;
            return Eligible(provider);
        }

        private static bool Eligible(SliderMovement movement, out VariableMovementDataProvider provider)
        {
            provider = movement == null || SliderMovementProvider == null
                ? null
                : SliderMovementProvider.GetValue(movement) as VariableMovementDataProvider;
            return Eligible(provider);
        }

        private static bool Eligible(NoteJump jump, out VariableMovementDataProvider provider)
        {
            provider = jump == null || NoteJumpProvider == null
                ? null
                : NoteJumpProvider.GetValue(jump) as VariableMovementDataProvider;
            return Eligible(provider);
        }

        private static bool TryTicket(string reason)
        {
            return RuntimeState.EnsureMotionTicket(reason);
        }

        private static bool Approximately(float left, float right)
        {
            return Mathf.Approximately(left, right);
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool Finite(Vector3 value)
        {
            return Finite(value.x) && Finite(value.y) && Finite(value.z);
        }

        private static bool ChangeNoteSpawn(
            ref NoteSpawnData spawnData,
            ref float flipYSide,
            Options options)
        {
            Vector3 moveStart = spawnData.moveStartOffset;
            Vector3 moveEnd = spawnData.moveEndOffset;
            Vector3 jumpEnd = spawnData.jumpEndOffset;
            float gravity = spawnData.gravityBase;
            float nextFlipYSide = flipYSide;
            if (!Finite(moveStart) || !Finite(moveEnd) || !Finite(jumpEnd) || !Finite(gravity))
                return false;

            bool changed = false;

            // NoteJump's actual hit height is moveEndOffset.y + gravityBase.
            // Move that height into the start offset before removing gravity;
            // NoteJump.beatPos is only a midpoint and is not the hit pose.
            if (!options.VerticalJump)
            {
                float hitHeight = moveEnd.y + gravity;
                if (!Finite(hitHeight)) return false;
                if (!Approximately(moveStart.y, hitHeight) ||
                    !Approximately(moveEnd.y, hitHeight) ||
                    !Approximately(jumpEnd.y, hitHeight))
                {
                    changed = true;
                }
                moveStart.y = hitHeight;
                moveEnd.y = hitHeight;
                jumpEnd.y = hitHeight;
                if (!Approximately(gravity, 0f))
                {
                    gravity = 0f;
                    changed = true;
                }

                // yAvoidance is derived from flipYSide in NoteJump.Init.
                if (!Approximately(nextFlipYSide, 0f))
                {
                    nextFlipYSide = 0f;
                    changed = true;
                }
            }

            if (!options.HorizontalSwap &&
                (!Approximately(moveStart.x, jumpEnd.x) || !Approximately(moveEnd.x, jumpEnd.x)))
            {
                moveStart.x = jumpEnd.x;
                moveEnd.x = jumpEnd.x;
                changed = true;
            }

            if (!changed) return false;
            if (!TryTicket("Note training settings changed movement or timing.")) return false;

            spawnData = new NoteSpawnData(moveStart, moveEnd, jumpEnd, gravity);
            flipYSide = nextFlipYSide;
            return true;
        }

        private static bool ChangeSliderSpawn(
            ref SliderSpawnData spawnData,
            SliderData sliderData,
            Options options)
        {
            Vector3 head = spawnData.headNoteOffset;
            Vector3 tail = spawnData.tailNoteOffset;
            float headGravity = spawnData.headGravityBase;
            float tailGravity = spawnData.tailGravityBase;
            if (!Finite(head) || !Finite(tail) || !Finite(headGravity) || !Finite(tailGravity))
                return false;

            bool changed = false;

            if (!options.VerticalJump)
            {
                if (!Approximately(headGravity, 0f))
                {
                    head.y += headGravity;
                    headGravity = 0f;
                    changed = true;
                }
                if (!Approximately(tailGravity, 0f))
                {
                    tail.y += tailGravity;
                    tailGravity = 0f;
                    changed = true;
                }
            }

            if (!options.HorizontalSwap && sliderData != null && spawnMovement != null)
            {
                try
                {
                    Vector3 finalHead = spawnMovement.GetNoteOffset(
                        sliderData.headLineIndex,
                        sliderData.headLineLayer);
                    Vector3 finalTail = spawnMovement.GetNoteOffset(
                        sliderData.tailLineIndex,
                        sliderData.tailLineLayer);
                    if (Finite(finalHead) && Finite(finalTail))
                    {
                        if (!Approximately(head.x, finalHead.x))
                        {
                            head.x = finalHead.x;
                            changed = true;
                        }
                        if (!Approximately(tail.x, finalTail.x))
                        {
                            tail.x = finalTail.x;
                            changed = true;
                        }
                    }
                }
                catch (Exception error)
                {
                    RuntimeState.WarnOnce(
                        "slider-offsets",
                        "The native slider lane offsets could not be resolved; original slider lanes were retained.");
                    RuntimeState.ErrorOnce("slider-offsets-detail", "The native slider lane offsets could not be resolved.", error);
                }
            }

            if (!changed) return false;
            if (!TryTicket("Note training settings changed movement or timing.")) return false;

            spawnData = new SliderSpawnData(head, headGravity, tail, tailGravity);
            return true;
        }

        private static bool TryModifyNoteJumpInit(
            NoteJump jump,
            ref bool rotateTowardsPlayer)
        {
            if (!Eligible(jump, out _)) return false;
            Options options = RuntimeState.ActiveOptions;
            if (options.RotateTowardsPlayer || !rotateTowardsPlayer) return false;
            if (!TryTicket("Note training settings changed movement or timing.")) return false;
            rotateTowardsPlayer = false;
            return true;
        }

        private static void ApplySpawnRotation(NoteJump jump)
        {
            if (!Eligible(jump, out _)) return;
            Options options = RuntimeState.ActiveOptions;
            if (options.SpawnRotation) return;
            if (NoteJumpStartRotation == null || NoteJumpMiddleRotation == null) return;
            if (!TryTicket("Note training settings changed movement or timing.")) return;

            try
            {
                // NoteJump.Init leaves _endRotation as the authored cut angle;
                // the transform still has identity here. Read the private end
                // value through reflection so mapping-extension angles remain
                // intact, including nonzero world yaw.
                Quaternion finalRotation = (Quaternion)NoteJumpEndRotation.GetValue(jump);
                NoteJumpStartRotation.SetValue(jump, finalRotation);
                NoteJumpMiddleRotation.SetValue(jump, finalRotation);
                Transform rotatedObject = NoteJumpRotatedObject.GetValue(jump) as Transform;
                if (rotatedObject != null) rotatedObject.localRotation = finalRotation;
            }
            catch (Exception error)
            {
                RuntimeState.ErrorOnce(
                    "spawn-rotation",
                    "The native spawn rotation could not be adjusted; authored rotation was retained.",
                    error);
            }
        }

        private static bool BeginNoteHide(NoteController controller)
        {
            if (controller == null || !RuntimeState.GameplayActive ||
                !RuntimeState.MovementInterfaceReady || RuntimeState.PreserveAuthoredMotion)
                return false;
            Options options = RuntimeState.ActiveOptions;
            if (options == null || !options.Enabled || !options.HidePreJump) return false;

            NoteMovement movement = NoteControllerMovement?.GetValue(controller) as NoteMovement;
            if (!Eligible(movement, out _)) return false;
            if (!TryTicket("Note training settings changed movement or timing.")) return false;

            NoteHideState state = new NoteHideState
            {
                External = controller.hidden,
            };
            noteHides[controller] = state;
            SetNoteOwnedHidden(controller, state, true);
            return true;
        }

        private static void SetNoteOwnedHidden(NoteController controller, NoteHideState state, bool value)
        {
            if (controller == null || state == null || state.Owned == value) return;
            state.Owned = value;
            state.Changing = true;
            try
            {
                controller.Hide(value || state.External);
            }
            finally
            {
                state.Changing = false;
            }
        }

        private static void RevealNote(NoteController controller)
        {
            if (controller == null || !noteHides.TryGetValue(controller, out NoteHideState state)) return;
            state.Owned = false;
            if (!state.External)
            {
                state.Changing = true;
                try { controller.Hide(false); }
                finally { state.Changing = false; }
            }
            // Keep the layer until the controller is reused or destroyed. The
            // spawn manager calls Hide(spawnHidden) immediately after its first
            // ManualUpdate; retaining the state lets that native hide update
            // the external layer without undoing our reveal.
        }

        private static bool BeginSliderHide(SliderController controller)
        {
            if (controller == null || !RuntimeState.GameplayActive ||
                !RuntimeState.MovementInterfaceReady || RuntimeState.PreserveAuthoredMotion)
                return false;
            Options options = RuntimeState.ActiveOptions;
            if (options == null || !options.Enabled || !options.HidePreJump) return false;
            SliderMovement movement = controller.sliderMovement;
            if (!Eligible(movement, out _)) return false;
            if (!EnsureSliderMotionHost(movement)) return false;
            if (!TryTicket("Note training settings changed movement or timing.")) return false;

            SliderHideState state = new SliderHideState();
            sliderHides[controller] = state;
            SetSliderOwnedHidden(controller, state, true);
            return true;
        }

        private static void SetSliderOwnedHidden(SliderController controller, SliderHideState state, bool value)
        {
            if (controller == null || state == null || state.Owned == value) return;
            state.Owned = value;
            state.Changing = true;
            try { controller.Hide(value || state.External); }
            finally { state.Changing = false; }
        }

        private static void RevealSlider(SliderController controller)
        {
            if (controller == null || !sliderHides.TryGetValue(controller, out SliderHideState state)) return;
            state.Owned = false;
            if (!state.External)
            {
                state.Changing = true;
                try { controller.Hide(false); }
                finally { state.Changing = false; }
            }
            // Keep the layer until the controller is reused or destroyed. The
            // spawn manager calls Hide(spawnHidden) immediately after its first
            // ManualUpdate; retaining the state lets that native hide update
            // the external layer without undoing our reveal.
        }

        [HarmonyPatch(typeof(BeatmapObjectSpawnMovementData), "Init")]
        private static class SpawnMovementInitPatch
        {
            private static void Postfix(BeatmapObjectSpawnMovementData __instance)
            {
                CaptureSpawnMovement(__instance);
            }
        }

        [HarmonyPatch(typeof(NoteMovement), "Init")]
        private static class NoteMovementInitPatch
        {
            private static void Prefix(
                NoteMovement __instance,
                ref NoteSpawnData noteSpawnData,
                ref float flipYSide)
            {
                if (!Eligible(__instance, out _)) return;
                ChangeNoteSpawn(ref noteSpawnData, ref flipYSide, RuntimeState.ActiveOptions);
            }
        }

        [HarmonyPatch(typeof(SliderMovement), "Init")]
        private static class SliderMovementInitPatch
        {
            private static void Prefix(
                SliderMovement __instance,
                SliderData sliderData,
                ref SliderSpawnData sliderSpawnData)
            {
                if (!Eligible(__instance, out _)) return;
                ChangeSliderSpawn(ref sliderSpawnData, sliderData, RuntimeState.ActiveOptions);
            }
        }

        [HarmonyPatch(typeof(NoteJump), "Init")]
        private static class NoteJumpInitPatch
        {
            private static void Prefix(NoteJump __instance, ref bool rotateTowardsPlayer)
            {
                TryModifyNoteJumpInit(__instance, ref rotateTowardsPlayer);
            }

            private static void Postfix(NoteJump __instance)
            {
                ApplySpawnRotation(__instance);
            }
        }

        [HarmonyPatch(typeof(NoteController), "Init")]
        private static class NoteControllerInitPatch
        {
            private static void Postfix(NoteController __instance)
            {
                noteHides.Remove(__instance);
                BeginNoteHide(__instance);
            }
        }

        [HarmonyPatch(typeof(NoteController), "Hide")]
        private static class NoteControllerHidePatch
        {
            private static void Prefix(NoteController __instance, ref bool hide)
            {
                if (!noteHides.TryGetValue(__instance, out NoteHideState state) || state.Changing)
                    return;
                state.External = hide;
                if (state.Owned) hide = true;
            }
        }

        [HarmonyPatch(typeof(NoteController), "HandleNoteDidStartJump")]
        private static class NoteControllerJumpStartPatch
        {
            private static void Postfix(NoteController __instance)
            {
                // The native event is raised after NoteJump has committed its
                // pose and before NoteMovement.ManualUpdate returns, so this
                // reveal cannot expose a stale first-frame transform.
                RevealNote(__instance);
            }
        }

        [HarmonyPatch(typeof(NoteController), "OnDestroy")]
        private static class NoteControllerDestroyPatch
        {
            private static void Postfix(NoteController __instance)
            {
                noteHides.Remove(__instance);
            }
        }

        [HarmonyPatch(typeof(SliderController), "Init")]
        private static class SliderControllerInitPatch
        {
            private static void Postfix(SliderController __instance)
            {
                sliderHides.Remove(__instance);
                BeginSliderHide(__instance);
            }
        }

        [HarmonyPatch(typeof(SliderController), "Hide")]
        private static class SliderControllerHidePatch
        {
            private static void Prefix(SliderController __instance, ref bool hide)
            {
                if (!sliderHides.TryGetValue(__instance, out SliderHideState state) || state.Changing)
                    return;
                state.External = hide;
                if (state.Owned) hide = true;
            }
        }

        [HarmonyPatch(typeof(SliderController), "ManualUpdate")]
        private static class SliderControllerJumpStartPatch
        {
            private static void Postfix(SliderController __instance)
            {
                if (__instance == null || !sliderHides.ContainsKey(__instance)) return;
                SliderMovement movement = __instance.sliderMovement;
                if (movement != null && movement.timeSinceHeadNoteJump >= 0f)
                    RevealSlider(__instance);
            }
        }

        [HarmonyPatch(typeof(SliderController), "OnDestroy")]
        private static class SliderControllerDestroyPatch
        {
            private static void Postfix(SliderController __instance)
            {
                sliderHides.Remove(__instance);
            }
        }
    }
}
