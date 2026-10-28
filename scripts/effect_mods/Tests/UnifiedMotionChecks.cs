using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EffectModsSmoke;

public sealed partial class Runner
{
    bool unifiedMotionCaptured;
    bool unifiedMotionAuthored;
    bool unifiedMotionStable;
    bool unifiedMotionHidePreJumpExpected;
    bool unifiedMotionInterfaceReady;
    int unifiedMotionFirstVisibleSamples;
    int unifiedMotionFirstVisibleViolations;
    int unifiedMotionStableSamples;
    int unifiedMotionStableViolations;
    int unifiedMotionNoteHiddenSamples;
    int unifiedMotionNoteHiddenViolations;
    int unifiedMotionSliderHiddenSamples;
    int unifiedMotionSliderHiddenViolations;
    int unifiedMotionSliderRevealedSamples;
    int unifiedMotionSliderLifecycleViolations;
    bool unifiedMotionSliderSeen;
    readonly Dictionary<SliderController, float> unifiedMotionSliderTimes = new();
    readonly Dictionary<SliderController, bool> unifiedMotionSliderOwnership = new();
    Harmony unifiedMotionHarmony;

    /// <summary>
    /// Call immediately after CaptureNoteMotionGameplay, while the GameCore
    /// container and RuntimeState still describe the active play.
    /// </summary>
    void CaptureUnifiedMotionChecks()
    {
        if (unifiedMotionCaptured) return;
        unifiedMotionCaptured = true;

        Type runtime = FindTrainingType("NoteMotionControl.RuntimeState");
        Type motion = FindTrainingType("NoteMotionControl.MotionRuntime");
        if (runtime == null || motion == null) return;

        object options = GetStaticMember(runtime, "ActiveOptions");
        unifiedMotionAuthored = ReadBool(GetStaticMember(runtime, "PreserveAuthoredMotion"), string.Empty, false);
        unifiedMotionInterfaceReady = ReadBool(GetStaticMember(runtime, "MovementInterfaceReady"), string.Empty, false);

        bool enabled = ReadBool(options, "Enabled", false);
        bool hidePreJump = ReadBool(options, "HidePreJump", false);
        bool verticalJump = ReadBool(options, "VerticalJump", true);
        bool horizontalSwap = ReadBool(options, "HorizontalSwap", true);
        bool spawnRotation = ReadBool(options, "SpawnRotation", true);
        bool rotateTowardsPlayer = ReadBool(options, "RotateTowardsPlayer", true);
        unifiedMotionStable = enabled && hidePreJump && !verticalJump && !horizontalSwap &&
            !spawnRotation && !rotateTowardsPlayer && !unifiedMotionAuthored && unifiedMotionInterfaceReady;
        unifiedMotionHidePreJumpExpected = enabled && hidePreJump && !unifiedMotionAuthored && unifiedMotionInterfaceReady;
        MethodInfo validate = AccessTools.Method(motion, "ValidateInterface");
        MethodInfo clear = AccessTools.Method(motion, "Clear");
        bool validated = false;
        string validationError = string.Empty;
        try
        {
            if (validate != null)
            {
                object[] arguments = { null };
                validated = Convert.ToBoolean(validate.Invoke(null, arguments));
                validationError = Convert.ToString(arguments[0]) ?? string.Empty;
            }
        }
        catch (Exception error)
        {
            validationError = error.GetType().Name + ": " + error.Message;
        }

        Check("training-unified-motion-interface", validate != null && clear != null && validated,
            "validate=" + validated + "; clear=" + (clear != null) + "; error=" + validationError);

        if (unifiedMotionAuthored)
        {
            Check("training-unified-authored-motion-start-clean", MotionStateCount("noteHides") == 0 &&
                MotionStateCount("sliderHides") == 0,
                "Authored-motion charts must not receive native hide ownership before their first object is observed.");
            return;
        }

        try
        {
            InstallUnifiedMotionHooks();
        }
        catch (Exception error)
        {
            Check("training-unified-motion-hooks", false, error.ToString());
        }
    }

    /// <summary>
    /// Call once per gameplay frame after ObserveNoteMotion(songTime).
    /// </summary>
    void ObserveUnifiedMotionChecks(float songTime)
    {
        if (!unifiedMotionCaptured) return;

        if (unifiedMotionAuthored)
        {
            if (MotionStateCount("noteHides") != 0 || MotionStateCount("sliderHides") != 0)
                unifiedMotionFirstVisibleViolations++;
            return;
        }

        if (unifiedMotionStable)
        {
            foreach (object value in ActiveTrainingNoteControllers())
            {
                NoteController controller = value as NoteController;
                if (controller == null) continue;
                ObserveStableNote(controller, songTime);
            }
        }

    }

    /// <summary>
    /// Call after FinishNoteMotionGameplay and before the final gameplay
    /// report is written.
    /// </summary>
    void FinishUnifiedMotionChecks()
    {
        if (!unifiedMotionCaptured) return;

        if (unifiedMotionAuthored)
        {
            Check("training-unified-authored-motion-preserved", MotionStateCount("noteHides") == 0 &&
                MotionStateCount("sliderHides") == 0 && unifiedMotionFirstVisibleViolations == 0,
                "Authored chart motion must bypass native note and slider ownership.");
        }
        else if (unifiedMotionStable)
        {
            Check("training-unified-stable-first-visible", unifiedMotionFirstVisibleSamples > 0 &&
                unifiedMotionFirstVisibleViolations == 0,
                "samples=" + unifiedMotionFirstVisibleSamples + "; violations=" + unifiedMotionFirstVisibleViolations);
            Check("training-unified-stable-linear-pose", unifiedMotionStableSamples > 10 &&
                unifiedMotionStableViolations == 0,
                "samples=" + unifiedMotionStableSamples + "; violations=" + unifiedMotionStableViolations);
            Check("training-unified-note-hide-lifecycle", unifiedMotionNoteHiddenSamples > 0 &&
                unifiedMotionNoteHiddenViolations == 0,
                "hiddenSamples=" + unifiedMotionNoteHiddenSamples + "; violations=" + unifiedMotionNoteHiddenViolations);
            if (unifiedMotionSliderSeen)
                Check("training-unified-slider-hide-lifecycle", unifiedMotionSliderHiddenSamples > 0 &&
                    unifiedMotionSliderRevealedSamples > 0 && unifiedMotionSliderHiddenViolations == 0 && unifiedMotionSliderLifecycleViolations == 0,
                    "hiddenSamples=" + unifiedMotionSliderHiddenSamples + "; revealedSamples=" +
                    unifiedMotionSliderRevealedSamples + "; violations=" + unifiedMotionSliderLifecycleViolations);
        }

        if (!unifiedMotionStable && unifiedMotionHidePreJumpExpected)
        {
            Check("training-unified-note-hide-lifecycle", unifiedMotionNoteHiddenSamples > 0 &&
                unifiedMotionNoteHiddenViolations == 0,
                "hiddenSamples=" + unifiedMotionNoteHiddenSamples + "; violations=" + unifiedMotionNoteHiddenViolations);
            if (unifiedMotionSliderSeen)
                Check("training-unified-slider-hide-lifecycle", unifiedMotionSliderHiddenSamples > 0 &&
                    unifiedMotionSliderRevealedSamples > 0 && unifiedMotionSliderHiddenViolations == 0 && unifiedMotionSliderLifecycleViolations == 0,
                    "hiddenSamples=" + unifiedMotionSliderHiddenSamples + "; revealedSamples=" +
                    unifiedMotionSliderRevealedSamples + "; violations=" + unifiedMotionSliderLifecycleViolations);
        }

        unifiedMotionSliderTimes.Clear();
        unifiedMotionSliderOwnership.Clear();
        UnifiedMotionJumpProbe.Owner = null;
        UnifiedMotionNoteHideProbe.Owner = null;
        UnifiedMotionSliderProbe.Owner = null;
        UnifiedMotionSliderInitProbe.Owner = null;
        UnifiedMotionSliderHideProbe.Owner = null;
        unifiedMotionHarmony?.UnpatchSelf();
        unifiedMotionHarmony = null;
    }

    void InstallUnifiedMotionHooks()
    {
        unifiedMotionHarmony = new Harmony("EffectModsSmoke.UnifiedMotionChecks");
        var postfix = new HarmonyMethod(typeof(UnifiedMotionJumpProbe), "Postfix")
        {
            priority = Priority.Last,
            after = new[] { "localtraining.NoteMotionControl" },
        };
        unifiedMotionHarmony.Patch(AccessTools.Method(typeof(NoteController), "HandleNoteDidStartJump"), postfix: postfix);

        var noteHide = new HarmonyMethod(typeof(UnifiedMotionNoteHideProbe), "Postfix")
        {
            priority = Priority.Last,
            after = new[] { "localtraining.NoteMotionControl" },
        };
        unifiedMotionHarmony.Patch(AccessTools.Method(typeof(NoteController), "Hide", new[] { typeof(bool) }), postfix: noteHide);

        var sliderPostfix = new HarmonyMethod(typeof(UnifiedMotionSliderProbe), "Postfix")
        {
            priority = Priority.Last,
            after = new[] { "localtraining.NoteMotionControl" },
        };
        unifiedMotionHarmony.Patch(AccessTools.Method(typeof(SliderController), "ManualUpdate"), postfix: sliderPostfix);

        var sliderInit = new HarmonyMethod(typeof(UnifiedMotionSliderInitProbe), "Postfix")
        {
            priority = Priority.Last,
            after = new[] { "localtraining.NoteMotionControl" },
        };
        unifiedMotionHarmony.Patch(AccessTools.Method(typeof(SliderController), "Init"), postfix: sliderInit);

        var sliderHide = new HarmonyMethod(typeof(UnifiedMotionSliderHideProbe), "Postfix")
        {
            priority = Priority.Last,
            after = new[] { "localtraining.NoteMotionControl" },
        };
        unifiedMotionHarmony.Patch(AccessTools.Method(typeof(SliderController), "Hide", new[] { typeof(bool) }), postfix: sliderHide);

        UnifiedMotionJumpProbe.Owner = this;
        UnifiedMotionNoteHideProbe.Owner = this;
        UnifiedMotionSliderProbe.Owner = this;
        UnifiedMotionSliderInitProbe.Owner = this;
        UnifiedMotionSliderHideProbe.Owner = this;
    }

    void ObserveStableNote(NoteController controller, float songTime)
    {
        object movement = GetMember(controller, "_noteMovement");
        NoteJump jump = GetMember(movement, "_jump") as NoteJump;
        if (jump == null) return;

        float noteTime = ReadFloat(jump, "_noteTime", float.NaN);
        object provider = GetMember(jump, "_variableMovementDataProvider");
        float half = ReadFloat(provider, "halfJumpDuration", float.NaN);
        float duration = ReadFloat(provider, "jumpDuration", float.NaN);
        if (!IsFiniteTraining(noteTime) || !IsFiniteTraining(half) || !IsFiniteTraining(duration) || duration <= 0f)
            return;
        float progress = (songTime - noteTime + half) / duration;
        bool owned = MotionStateOwned("noteHides", controller);
        if (progress < 0f && progress < 1f && owned)
        {
            if (NativeMotionVisualActive(controller) || !controller.hidden) unifiedMotionNoteHiddenViolations++;
        }

        if (progress < 0f || progress > 1.05f) return;
        Vector3 position = ReadVector3(jump, "_localPosition");
        Vector3 start = ReadVector3(jump, "_startPos");
        Vector3 end = ReadVector3(jump, "_endPos");
        Transform rotated = GetMember(jump, "_rotatedObject") as Transform;
        Quaternion actualRotation = rotated == null ? Quaternion.identity : rotated.localRotation;
        Quaternion finalRotation = ReadQuaternion(jump, "_endRotation");
        bool finite = IsFiniteTraining(position) && IsFiniteTraining(start) && IsFiniteTraining(end) &&
            IsFiniteTraining(actualRotation) && IsFiniteTraining(finalRotation);
        if (!finite)
        {
            unifiedMotionStableViolations++;
            return;
        }

        unifiedMotionStableSamples++;
        if (Mathf.Abs(start.x - end.x) > 0.001f || Mathf.Abs(start.y - end.y) > 0.001f ||
            Mathf.Abs(position.x - start.x) > 0.004f || Mathf.Abs(position.y - start.y) > 0.004f ||
            Quaternion.Angle(actualRotation, finalRotation) > 0.15f)
            unifiedMotionStableViolations++;
    }

    void OnUnifiedMotionJumpStart(NoteController controller)
    {
        if (!unifiedMotionStable || controller == null) return;
        object movement = GetMember(controller, "_noteMovement");
        NoteJump jump = GetMember(movement, "_jump") as NoteJump;
        if (jump == null) return;
        unifiedMotionFirstVisibleSamples++;
        Vector3 position = ReadVector3(jump, "_localPosition");
        Vector3 start = ReadVector3(jump, "_startPos");
        Vector3 expectedStart = ReadNativeStartPosition(jump);
        Transform rotated = GetMember(jump, "_rotatedObject") as Transform;
        Quaternion actualRotation = rotated == null ? Quaternion.identity : rotated.localRotation;
        Quaternion finalRotation = ReadQuaternion(jump, "_endRotation");
        if (controller.hidden || !NativeMotionVisualActive(controller) || !IsFiniteTraining(position) ||
            !IsFiniteTraining(start) || !IsFiniteTraining(expectedStart) ||
            !IsFiniteTraining(actualRotation) || !IsFiniteTraining(finalRotation) ||
            Mathf.Abs(position.x - expectedStart.x) > 0.004f || Mathf.Abs(position.y - expectedStart.y) > 0.004f ||
            Quaternion.Angle(actualRotation, finalRotation) > 0.15f)
            unifiedMotionFirstVisibleViolations++;
    }

    static Vector3 ReadNativeStartPosition(NoteJump jump)
    {
        if (jump == null) return new Vector3(float.NaN, float.NaN, float.NaN);
        Vector3 offset = ReadVector3(jump, "_startOffset");
        object provider = GetMember(jump, "_variableMovementDataProvider");
        Vector3 moveEnd = ReadVector3(provider, "moveEndPosition");
        return moveEnd + offset;
    }

    void OnUnifiedMotionNoteHidden(NoteController controller)
    {
        if (!unifiedMotionHidePreJumpExpected || controller == null || !MotionStateOwned("noteHides", controller)) return;
        if (controller.hidden && !NativeMotionVisualActive(controller)) unifiedMotionNoteHiddenSamples++;
        if (!controller.hidden || NativeMotionVisualActive(controller)) unifiedMotionNoteHiddenViolations++;
    }

    void OnUnifiedMotionSliderUpdate(SliderController controller)
    {
        if (!unifiedMotionHidePreJumpExpected || controller == null) return;
        SliderMovement movement = controller.sliderMovement;
        if (movement == null) return;
        float time = movement.timeSinceHeadNoteJump;
        if (!IsFiniteTraining(time))
        {
            unifiedMotionSliderLifecycleViolations++;
            return;
        }
        if (time < 0f) unifiedMotionSliderSeen = true;
        bool owned = MotionStateOwned("sliderHides", controller);
        bool wasOwned = unifiedMotionSliderOwnership.TryGetValue(controller, out bool oldOwned) && oldOwned;
        unifiedMotionSliderOwnership[controller] = owned;
        if (!owned && !wasOwned) return;
        unifiedMotionSliderSeen = true;
        if (unifiedMotionSliderTimes.TryGetValue(controller, out float previous) && time + 0.0001f < previous)
            unifiedMotionSliderLifecycleViolations++;
        unifiedMotionSliderTimes[controller] = time;
        if (owned && time < 0f)
        {
            unifiedMotionSliderHiddenSamples++;
            if (NativeMotionVisualActive(controller)) unifiedMotionSliderHiddenViolations++;
        }
        if (wasOwned && !owned && time >= 0f)
        {
            unifiedMotionSliderRevealedSamples++;
            if (!NativeMotionVisualActive(controller)) unifiedMotionSliderLifecycleViolations++;
        }
    }

    void OnUnifiedMotionSliderHidden(SliderController controller)
    {
        if (!unifiedMotionHidePreJumpExpected || controller == null || !MotionStateOwned("sliderHides", controller)) return;
        unifiedMotionSliderSeen = true;
        if (!NativeMotionVisualActive(controller)) unifiedMotionSliderHiddenSamples++;
        else unifiedMotionSliderHiddenViolations++;
    }

    void OnUnifiedMotionSliderInit(SliderController controller)
    {
        if (controller == null) return;
        unifiedMotionSliderTimes.Remove(controller);
        unifiedMotionSliderOwnership.Remove(controller);
    }

    static bool NativeMotionVisualActive(Component controller)
    {
        if (controller == null) return false;
        bool foundWrapper = false;
        foreach (MonoBehaviour child in controller.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (!(child is NoteController))
                continue;
            GameObject wrapper = GetMember(child, "_wrapperGO") as GameObject;
            if (wrapper == null) continue;
            foundWrapper = true;
            if (wrapper.activeInHierarchy) return true;
        }
        return foundWrapper ? false : controller.gameObject.activeInHierarchy;
    }

    bool MotionStateOwned(string fieldName, object owner)
    {
        object value = GetStaticMember(FindTrainingType("NoteMotionControl.MotionRuntime"), fieldName);
        if (!(value is IDictionary dictionary)) return false;
        foreach (DictionaryEntry entry in dictionary)
            if (ReferenceEquals(entry.Key, owner)) return ReadBool(entry.Value, "Owned", false);
        return false;
    }

    int MotionStateCount(string fieldName)
    {
        object value = GetStaticMember(FindTrainingType("NoteMotionControl.MotionRuntime"), fieldName);
        return value is IDictionary dictionary ? dictionary.Count : -1;
    }

    static class UnifiedMotionJumpProbe
    {
        internal static Runner Owner;
        static void Postfix(NoteController __instance) => Owner?.OnUnifiedMotionJumpStart(__instance);
    }

    static class UnifiedMotionNoteHideProbe
    {
        internal static Runner Owner;
        static void Postfix(NoteController __instance) => Owner?.OnUnifiedMotionNoteHidden(__instance);
    }

    static class UnifiedMotionSliderProbe
    {
        internal static Runner Owner;
        static void Postfix(SliderController __instance) => Owner?.OnUnifiedMotionSliderUpdate(__instance);
    }

    static class UnifiedMotionSliderInitProbe
    {
        internal static Runner Owner;
        static void Postfix(SliderController __instance) => Owner?.OnUnifiedMotionSliderInit(__instance);
    }

    static class UnifiedMotionSliderHideProbe
    {
        internal static Runner Owner;
        static void Postfix(SliderController __instance) => Owner?.OnUnifiedMotionSliderHidden(__instance);
    }
}
