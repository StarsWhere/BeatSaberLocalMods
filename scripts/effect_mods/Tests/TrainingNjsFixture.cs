using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EffectModsSmoke;

public sealed partial class Runner
{
    Harmony trainingNjsFixtureHarmony, trainingNjsEventHarmony;
    float trainingNjsMinimum = float.PositiveInfinity, trainingNjsMaximum = float.NegativeInfinity;
    float trainingNjsPreviousTime = float.NaN, trainingNjsPreviousSpeed;
    float trainingNjsOriginalInitial = float.NaN;
    int trainingNjsSpeedSamples, trainingNjsSpeedJumps, trainingNjsEventSamples, trainingNjsEventViolations;
    int trainingNjsCapSamples, trainingNjsCapViolations, trainingNjsEasierSamples, trainingNjsCappedSamples;
    VariableMovementDataProvider trainingNjsLiveProvider;
    readonly List<string> trainingNjsCapEvidence = new();
    readonly List<object> trainingNjsSampleDetails = new();
    object[] trainingNjsSourceEvents = Array.Empty<object>();

    void InstallTrainingNjsFixture()
    {
        TrainingNjsFixturePatch.MinimumRelative = trainingScenario == "CapVariable" ? -6f : -2f;
        trainingNjsFixtureHarmony = new Harmony("EffectModsSmoke.TrainingNjsFixture");
        trainingNjsFixtureHarmony.Patch(
            AccessTools.Method(typeof(BeatmapDataTransformHelper), "CreateTransformedBeatmapData", new[] {
                typeof(IReadonlyBeatmapData), typeof(BeatmapKey), typeof(BeatmapLevel),
                typeof(GameplayModifiers), typeof(PlayerSpecificSettings), typeof(EnvironmentInfoSO), typeof(bool)
            }), postfix: new HarmonyMethod(typeof(TrainingNjsFixturePatch), "Postfix"));
        if (trainingScenario == "CapVariable")
        {
            TrainingCapCurveProbe.Owner = this;
            // Install before SongCore creates its instance-capturing ILHook.
            // This probe lives until the isolated game process exits; changing
            // Harmony patches on this method while that ILHook is active would
            // re-transpile SongCore's replacement instead of the native body.
            new Harmony("EffectModsSmoke.CapCurveOracle").Patch(
                AccessTools.Method(typeof(VariableMovementDataProvider), "ManualUpdate"),
                postfix: new HarmonyMethod(typeof(TrainingCapCurveProbe), "Postfix") { priority = Priority.Last });
        }
    }

    static class TrainingNjsFixturePatch
    {
        internal static float MinimumRelative = -2f;
        static void Postfix(ref IReadonlyBeatmapData __result)
        {
            if (__result == null) return;
            BeatmapData copy = __result.GetCopy();
            copy.updateAllBeatmapDataOnInsert = true;
            copy.InsertBeatmapEventData(new NoteJumpSpeedEventData(0f, 0f, EaseType.None, false));
            copy.InsertBeatmapEventData(new NoteJumpSpeedEventData(4f, 6f, EaseType.None, false));
            copy.InsertBeatmapEventData(new NoteJumpSpeedEventData(4.1f, MinimumRelative, EaseType.None, false));
            copy.InsertBeatmapEventData(new NoteJumpSpeedEventData(10f, 0f, EaseType.Linear, false));
            copy.InsertBeatmapEventData(new NoteJumpSpeedEventData(14f, 3f, EaseType.InOutSine, false));
            __result = copy;
        }
    }

    static class TrainingSpeedEventProbe
    {
        internal static Runner Owner;
        static void Postfix(VariableMovementDataProvider __instance, NoteJumpSpeedEventData __0) => Owner?.CheckTrainingSpeedEvent(__instance, __0);
    }

    static class TrainingCapCurveProbe
    {
        internal static Runner Owner;
        static void Postfix(VariableMovementDataProvider __instance, float __0) => Owner?.CheckTrainingCapCurve(__instance, __0);
    }

    void CheckTrainingCapCurve(VariableMovementDataProvider provider, float time)
    {
        if (trainingScenario != "CapVariable" || trainingMotionAuthoredLoaded || !ReferenceEquals(provider, trainingNjsLiveProvider)) return;
        object interpolation = GetMember(provider, "_relativeNoteJumpSpeedInterpolation");
        float relative = (float)AccessTools.Method(interpolation.GetType(), "GetValue", new[] { typeof(float) })
            .Invoke(interpolation, new object[] { time });
        float raw = Mathf.Max(ReadFloat(provider, "_initNoteJumpMovementSpeed", float.NaN) + relative, 0.01f);
        float target = trainingSpeedProfileFixedNjs;
        float actual = provider.noteJumpSpeed;
        float expected = Mathf.Min(raw, target);
        trainingNjsCapSamples++;
        if (raw < target) trainingNjsEasierSamples++;
        if (raw > target) trainingNjsCappedSamples++;
        if (!IsFiniteTraining(actual) || !IsFiniteTraining(expected) || Mathf.Abs(actual - expected) > 0.0001f)
        {
            trainingNjsCapViolations++;
            if (trainingNjsCapEvidence.Count < 8)
                trainingNjsCapEvidence.Add("time=" + time + "; raw=" + raw + "; expected=" + expected + "; actual=" + actual);
        }
    }

    void CheckTrainingSpeedEvent(VariableMovementDataProvider provider, NoteJumpSpeedEventData source)
    {
        try
        {
            if (!trainingNativeOracleReady || source == null) return;
            VariableMovementDataProvider reference = new(null, null);
            NativeSpeedOracle.Event(reference, source);
            object expected = GetMember(reference, "_relativeNoteJumpSpeedInterpolation");
            object actual = GetMember(provider, "_relativeNoteJumpSpeedInterpolation");
            object[] policy = { provider, null };
            MethodInfo lookup = AccessTools.Method(FindTrainingType("NoteMotionControl.SpeedRuntime"), "TryGetPolicy");
            bool hasPolicy = lookup != null && (bool)lookup.Invoke(null, policy);
            bool valid = true;
            foreach (string field in new[] { "_fromValue", "_toValue" })
            {
                float raw = ReadFloat(expected, field, float.NaN);
                // The merged speed route evaluates the original event curve
                // in ManualUpdate; Init and interpolation event payloads are
                // intentionally left untouched.
                float transformed = raw;
                float value = ReadFloat(actual, field, float.NaN);
                valid &= IsFiniteTraining(value) && Mathf.Abs(value - transformed) < 0.00001f;
            }
            foreach (string field in new[] { "_fromTime", "_duration", "_easeType" })
                valid &= Equals(GetMember(actual, field), GetMember(expected, field));
            trainingNjsEventSamples++;
            if (!valid) trainingNjsEventViolations++;
        }
        catch (Exception error) { errors.Add("training-speed-event-oracle: " + error); }
    }

    void CaptureTrainingNjsFixture(IReadonlyBeatmapData beatmap)
    {
        if (!trainingNoteLoaded) return;
        trainingNjsSourceEvents = beatmap.allBeatmapDataItems.OfType<NoteJumpSpeedEventData>().Take(256)
            .Select(value => (object)new { value.time, value.relativeNoteJumpSpeed, easeType = value.easeType.ToString() }).ToArray();
        if (trainingNjsFixtureHarmony != null)
            Check("training-variable-njs-native-events", trainingNjsSourceEvents.Length >= 5, "events=" + trainingNjsSourceEvents.Length);
        TrainingSpeedEventProbe.Owner = this;
        trainingNjsLiveProvider = (GetStaticMember(FindTrainingType("NoteMotionControl.RuntimeState"), "activeContainer") as Zenject.DiContainer)
            ?.TryResolve<IVariableMovementDataProvider>() as VariableMovementDataProvider;
        trainingNjsEventHarmony = new Harmony("EffectModsSmoke.SpeedEventOracle");
        var postfix = new HarmonyMethod(typeof(TrainingSpeedEventProbe), "Postfix") { priority = Priority.Last, after = new[] { "localtraining.NoteMotionControl" } };
        trainingNjsEventHarmony.Patch(AccessTools.Method(typeof(VariableMovementDataProvider), "HandleNoteJumpMovementSpeedEvent"), postfix: postfix);
    }

    void ObserveTrainingNjsFixture(float songTime)
    {
        if (!trainingNoteLoaded) return;
        foreach (object controller in ActiveTrainingNoteControllers())
        {
            object jump = GetMember(GetMember(controller, "_noteMovement"), "_jump");
            object provider = GetMember(jump, "_variableMovementDataProvider");
            if (provider == null) continue;
            float speed = ReadFloat(provider, "noteJumpSpeed", float.NaN);
            if (!IsFiniteTraining(speed)) continue;
            if (!IsFiniteTraining(trainingNjsOriginalInitial))
                trainingNjsOriginalInitial = ReadFloat(provider, "_initNoteJumpMovementSpeed", float.NaN);
            trainingNjsMinimum = Mathf.Min(trainingNjsMinimum, speed);
            trainingNjsMaximum = Mathf.Max(trainingNjsMaximum, speed);
            float elapsed = songTime - trainingNjsPreviousTime;
            bool jumpObserved = !float.IsNaN(trainingNjsPreviousTime) && elapsed > 0.001f && elapsed < 0.03f && Mathf.Abs(speed-trainingNjsPreviousSpeed) > 2f;
            if (jumpObserved) trainingNjsSpeedJumps++;
            // Authored instantaneous speed changes are retained. A jump is
            // diagnostic information, never a continuity failure by itself.
            if (trainingNjsSampleDetails.Count < 128 && (trainingNjsSpeedSamples < 12 || jumpObserved))
                trainingNjsSampleDetails.Add(new { frame = Time.frameCount, songTime, speed, jumpObserved,
                    sourceInitial = ReadFloat(provider, "_initNoteJumpMovementSpeed", float.NaN),
                    halfJump = ReadFloat(provider, "halfJumpDuration", float.NaN) });
            trainingNjsPreviousTime = songTime;
            trainingNjsPreviousSpeed = speed;
            trainingNjsSpeedSamples++;
            break;
        }
    }

    void FinishTrainingNjsFixture()
    {
        trainingNjsFixtureHarmony?.UnpatchSelf(); trainingNjsFixtureHarmony = null;
        trainingNjsEventHarmony?.UnpatchSelf(); trainingNjsEventHarmony = null;
        TrainingSpeedEventProbe.Owner = null;
        TrainingCapCurveProbe.Owner = null;
        if (!trainingNoteLoaded) return;
        Check("training-speed-event-values-and-timing", trainingNjsEventViolations == 0,
            "events=" + trainingNjsEventSamples + "; violations=" + trainingNjsEventViolations + "; authoredJumps=" + trainingNjsSpeedJumps);
        if (trainingScenario == "Variable")
            Check("training-speed-variable-fixture-exercised", trainingNjsEventSamples > 0 && trainingNjsMaximum - trainingNjsMinimum > 1f,
                "events=" + trainingNjsEventSamples + "; speed=" + trainingNjsMinimum + ".." + trainingNjsMaximum);
        if (trainingScenario == "CapVariable" && !trainingMotionAuthoredLoaded)
        {
            float target = ReadFloat(GetStaticMember(FindTrainingType("NoteMotionControl.RuntimeState"), "ActiveOptions"), "OverrideNjs", float.NaN);
            float lowestRaw = trainingNjsOriginalInitial + trainingNjsSourceEvents.Min(value => ReadFloat(value, "relativeNoteJumpSpeed", 0f));
            Check("training-speed-cap-preserves-easier-sections", trainingNjsEventSamples > 0 &&
                IsFiniteTraining(lowestRaw) && lowestRaw < target &&
                trainingNjsCapSamples > 10 && trainingNjsEasierSamples > 0 && trainingNjsCappedSamples > 0 && trainingNjsCapViolations == 0 &&
                trainingNjsMinimum >= lowestRaw - 0.02f &&
                Mathf.Abs(trainingNjsMaximum - target) < 0.02f,
                "sourceMinimum=" + lowestRaw + "; target=" + target + "; observed=" + trainingNjsMinimum + ".." + trainingNjsMaximum +
                "; samples=" + trainingNjsCapSamples + "; easier=" + trainingNjsEasierSamples + "; capped=" + trainingNjsCappedSamples +
                "; violations=" + trainingNjsCapViolations + "; evidence=" + string.Join(" | ", trainingNjsCapEvidence));
        }
        File.WriteAllText(Path.Combine(directory, "training-njs-provider-samples.json"), Newtonsoft.Json.JsonConvert.SerializeObject(new {
            trainingScenario, trainingNjsSpeedSamples,
            minimum = IsFiniteTraining(trainingNjsMinimum) ? (float?)trainingNjsMinimum : null,
            maximum = IsFiniteTraining(trainingNjsMaximum) ? (float?)trainingNjsMaximum : null,
            trainingNjsSpeedJumps, trainingNjsEventSamples, trainingNjsEventViolations,
            sourceEvents = trainingNjsSourceEvents, samples = trainingNjsSampleDetails
        }, Newtonsoft.Json.Formatting.Indented));
    }
}
