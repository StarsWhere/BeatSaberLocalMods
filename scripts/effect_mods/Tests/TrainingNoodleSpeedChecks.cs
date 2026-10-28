using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EffectModsSmoke;

public sealed partial class Runner
{
    bool trainingNoodleSpeedChecked;

    sealed class NoodleOracleFrame
    {
        internal Vector3? PositionOffset;
        internal Quaternion? RotationOffset;
        internal Vector3? ScaleOffset;
        internal Quaternion? LocalRotationOffset;
        internal Vector3? DefinitePosition;
    }

    // Replay the installed Noodle/native code with motion control on and off.
    // Author animation is sampled only by Noodle's own AnimationHelper.
    void VerifyNoodleSpeedRegression(NoteJump live)
    {
        if (trainingNoodleSpeedChecked || live == null || !trainingMotionNoodleLoaded) return;
        if (!ReadBool(live, "_jumpStartedReported", false)) return;
        Type updateType = FindTrainingType("NoodleExtensions.HarmonyPatches.Objects.NoteUpdateNoodlifier");
        Type helperType = FindTrainingType("NoodleExtensions.Animation.AnimationHelper");
        object noteUpdate = ResolveTrainingNoodle(updateType);
        object helper = ResolveTrainingNoodle(helperType);
        object originalData = GetMember(noteUpdate, "NoodleData");
        if (originalData == null || helper == null) return;
        trainingNoodleSpeedChecked = true;
        Type runtime = FindTrainingType("NoteMotionControl.RuntimeState");
        FieldInfo active = AccessTools.Field(runtime, "<GameplayActive>k__BackingField");
        bool wasActive = active != null && (bool)active.GetValue(null);
        int samples = 0, eventViolations = 0, finiteViolations = 0, fixedTimeSamples = 0, definiteSamples = 0;
        float maxPosition = 0f, maxChild = 0f, maxRoot = 0f, maxWorld = 0f, maxScale = 0f;
        try
        {
            if (active == null) throw new MissingFieldException("RuntimeState.GameplayActive");
            MethodInfo getOffset = FindNoodleMethod(helper.GetType(), "GetObjectOffset", 10);
            MethodInfo getDefinite = FindNoodleMethod(helper.GetType(), "GetDefinitePositionOffset", 4);
            if (getOffset == null || getDefinite == null) throw new MissingMethodException("Noodle AnimationHelper methods");
            float half = ReadFloat(GetMember(live, "_variableMovementDataProvider"), "halfJumpDuration", 0.75f);
            MethodInfo clone = AccessTools.Method(typeof(object), "MemberwiseClone");
            foreach (int variant in new[] { 0, 1, 2 })
            {
                object data = variant == 0 ? originalData : clone.Invoke(originalData, null);
                if (variant > 0)
                {
                    SetTrainingMember(data, "InternalWorldRotation", Quaternion.Euler(0f, 47f, 0f));
                    SetTrainingMember(data, "InternalLocalRotation", Quaternion.Euler(12f, -9f, 23f));
                    FieldInfo disable = AccessTools.Field(data.GetType(), "<DisableLook>k__BackingField");
                    if (disable == null) throw new MissingFieldException("NoodleBaseNoteData.DisableLook");
                    disable.SetValue(data, variant == 2);
                }
                SetTrainingMember(noteUpdate, "NoodleData", data);
                using NativeMotionFixture changed = new(live, 0f, true);
                using NativeMotionFixture native = new(live, 0f, true);
                object animation = GetMember(data, "AnimationObject");
                object tracks = GetMember(data, "Track");
                object provider = GetMember(live, "_variableMovementDataProvider");
                Vector3 providerStart = ReadVector3(provider, "moveEndPosition");
                Vector3 providerEnd = ReadVector3(provider, "jumpEndPosition");
                Vector3 startOffset = ReadVector3(data, "InternalMidPos");
                Vector3 endOffset = ReadVector3(data, "InternalEndPos");
                bool fixedTime = TryReadNullableFloat(data, "GetTimeProperty", out float heldTime);
                foreach (float progress in BuildNativeMotionSamples(72))
                {
                    changed.Clock.Time = NativeMotionFixture.NoteTime - half + 2f * half * progress;
                    native.Clock.Time = NativeMotionFixture.NoteTime - half + 2f * half * progress;
                    float authorTime = fixedTime ? heldTime : progress;
                    NoodleOracleFrame frame = InvokeNoodleFrame(getOffset, getDefinite, helper, animation, tracks, data, authorTime);
                    ApplyNoodlePrefix(changed.Jump, frame, data, providerStart, providerEnd, startOffset, endOffset);
                    ApplyNoodlePrefix(native.Jump, frame, data, providerStart, providerEnd, startOffset, endOffset);
                    Vector3 head = new(0.15f * Mathf.Sin(progress * 7f), 1.6f, 0.08f * Mathf.Cos(progress * 9f));
                    changed.SetHead(head);
                    native.SetHead(head);
                    active.SetValue(null, false);
                    native.Jump.ManualUpdate();
                    active.SetValue(null, true);
                    changed.Jump.ManualUpdate();
                    Vector3 position = ReadVector3(changed.Jump, "_localPosition");
                    Vector3 expected = ReadVector3(native.Jump, "_localPosition");
                    maxPosition = Mathf.Max(maxPosition, Vector3.Distance(position, expected));
                    maxChild = Mathf.Max(maxChild, Quaternion.Angle(changed.Rotated.localRotation, native.Rotated.localRotation));
                    maxRoot = Mathf.Max(maxRoot, Quaternion.Angle(changed.Jump.transform.localRotation, native.Jump.transform.localRotation));
                    maxWorld = Mathf.Max(maxWorld, Quaternion.Angle(ReadQuaternion(changed.Jump, "_worldRotation"), ReadQuaternion(native.Jump, "_worldRotation")));
                    maxScale = Mathf.Max(maxScale, Vector3.Distance(changed.Jump.transform.localScale, native.Jump.transform.localScale));
                    if (!SameNativeMotionEvents(changed, native) || Mathf.Abs(changed.ReportedProgress - native.ReportedProgress) > 0.00001f) eventViolations++;
                    if (!IsFiniteTraining(position) || !IsFiniteTraining(changed.Rotated.localRotation) || !IsFiniteTraining(changed.Jump.transform.localRotation)) finiteViolations++;
                    if (fixedTime) fixedTimeSamples++;
                    if (frame.DefinitePosition.HasValue) definiteSamples++;
                    samples++;
                }
            }
            Check("training-noodle-authored-pose-preserved", samples > 200 && maxPosition < 0.002f && maxChild < 0.1f && maxRoot < 0.1f && maxWorld < 0.1f && maxScale < 0.002f,
                "samples=" + samples + "; position=" + maxPosition + "; child=" + maxChild + "; root=" + maxRoot + "; world=" + maxWorld + "; scale=" + maxScale);
            Check("training-noodle-native-time-and-events", eventViolations == 0 && finiteViolations == 0,
                "eventViolations=" + eventViolations + "; nonFinite=" + finiteViolations + "; fixedTimeSamples=" + fixedTimeSamples + "; definiteSamples=" + definiteSamples);
        }
        catch (Exception error)
        {
            Exception actual = error is TargetInvocationException invocation && invocation.InnerException != null ? invocation.InnerException : error;
            Check("training-noodle-authored-oracle", false, actual.ToString());
        }
        finally
        {
            SetTrainingMember(noteUpdate, "NoodleData", originalData);
            if (active != null) active.SetValue(null, wasActive);
        }
    }

    static object ResolveTrainingNoodle(Type type)
    {
        if (type == null) return null;
        foreach (Zenject.SceneContext context in Resources.FindObjectsOfTypeAll<Zenject.SceneContext>())
            if (context.Container != null && context.Container.HasBinding(type)) return context.Container.Resolve(type);
        return null;
    }

    static MethodInfo FindNoodleMethod(Type type, string name, int parameterCount)
    {
        if (type == null) return null;
        foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            if (method.Name == name && method.GetParameters().Length == parameterCount) return method;
        return null;
    }

    static bool TryReadNullableFloat(object value, string methodName, out float result)
    {
        result = 0f;
        if (value == null) return false;
        MethodInfo method = value.GetType().GetMethod(methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, Type.EmptyTypes, null);
        if (method == null) return false;
        object boxed = method.Invoke(value, null);
        if (boxed == null) return false;
        try
        {
            result = Convert.ToSingle(boxed);
            return IsFiniteTraining(result);
        }
        catch { return false; }
    }

    static NoodleOracleFrame InvokeNoodleFrame(
        MethodInfo getObjectOffset,
        MethodInfo getDefinitePosition,
        object animationHelper,
        object animation,
        object tracks,
        object data,
        float time)
    {
        object[] offsets = { animation, tracks, time, null, null, null, null, null, null, null };
        getObjectOffset.Invoke(animationHelper, offsets);
        NoodleOracleFrame frame = new()
        {
            PositionOffset = offsets[3] is Vector3 position ? position : (Vector3?)null,
            RotationOffset = offsets[4] is Quaternion rotation ? rotation : (Quaternion?)null,
            ScaleOffset = offsets[5] is Vector3 scale ? scale : (Vector3?)null,
            LocalRotationOffset = offsets[6] is Quaternion localRotation ? localRotation : (Quaternion?)null
        };

        object[] definite = { animation, tracks, time, null };
        getDefinitePosition.Invoke(animationHelper, definite);
        if (definite[3] is Vector3 definitePosition)
            frame.DefinitePosition = definitePosition + ReadVector3(data, "InternalNoteOffset");
        return frame;
    }

    static void ApplyNoodlePrefix(
        NoteJump jump,
        NoodleOracleFrame frame,
        object data,
        Vector3 providerStart,
        Vector3 providerEnd,
        Vector3 baseStartOffset,
        Vector3 baseEndOffset)
    {
        if (frame.PositionOffset.HasValue)
        {
            Vector3 startOffset = baseStartOffset + frame.PositionOffset.Value;
            Vector3 endOffset = baseEndOffset + frame.PositionOffset.Value;
            Vector3 start = providerStart + startOffset;
            Vector3 end = providerEnd + endOffset;
            // NoteUpdateNoodlifier writes these offset fields. The native
            // NoteJump body consumes them before its patched final position;
            // keeping _startPos/_endPos synchronized also covers the game's
            // post-miss continuous interpolation path.
            SetTrainingMember(jump, "_startOffset", startOffset);
            SetTrainingMember(jump, "_endOffset", endOffset);
            SetTrainingMember(jump, "_startPos", start);
            SetTrainingMember(jump, "_endPos", end);
        }

        Quaternion world = ReadQuaternion(data, "InternalWorldRotation");
        if (frame.RotationOffset.HasValue)
        {
            world *= frame.RotationOffset.Value;
            SetTrainingMember(jump, "_worldRotation", world);
            SetTrainingMember(jump, "_inverseWorldRotation", Quaternion.Inverse(world));
        }

        Quaternion root = world * ReadQuaternion(data, "InternalLocalRotation");
        if (frame.LocalRotationOffset.HasValue) root *= frame.LocalRotationOffset.Value;
        jump.transform.localRotation = root;
        if (frame.ScaleOffset.HasValue)
            jump.transform.localScale = Vector3.Scale(ReadVector3(data, "InternalScale"), frame.ScaleOffset.Value);
    }

}
