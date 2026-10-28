using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using HarmonyLib;
using UnityEngine;

namespace EffectModsSmoke;

public sealed partial class Runner
{
    bool trainingMotionExpected, trainingMotionAuthoredLoaded, trainingMotionNoodleLoaded, trainingMotionModificationObserved;
    bool trainingSpeedRegressionChecked, trainingNativeOracleReady, trainingMotionHeadSeen, trainingMotionPolicyObserved;
    bool trainingSpeedProfileEnabled, trainingSpeedInterfaceReady, trainingSpeedNativeProviderSeen, trainingSpeedExpectedNativePolicy, trainingSpeedExpectedPolicyResolved;
    int trainingSpeedProfileMode, trainingSpeedProfileApplicationPolicy, trainingSpeedProfileReactionMode, trainingSpeedPolicySamples, trainingSpeedPolicyViolations;
    float trainingSpeedProfileFixedNjs, trainingSpeedProfileMultiplier, trainingSpeedProfileReactionMs;
    int trainingMotionCutEvents, trainingMotionSamples, trainingMotionFiniteViolations;
    int trainingMotionRouteSamples, trainingMotionRouteViolations, trainingFixedSpeedSamples, trainingFixedSpeedViolations;
    float trainingMotionHeadRange, trainingMotionMaxPositionError, trainingMotionMaxRotationError;
    Vector3 trainingMotionFirstHead;
    Harmony trainingMotionRouteHarmony;
    readonly Dictionary<string, SpeedElement> trainingMotionElements = new();
    readonly HashSet<SliderController> trainingMotionSliders = new();
    readonly HashSet<object> trainingSpeedObservedProviders = new();
    readonly List<object> trainingSpeedProviderEvidence = new();

    sealed class SpeedElement
    {
        internal int ChartObjects, SpawnedObjects, ActiveFrames, FiniteViolations;
        internal int RenderableFrames, NativeOracleSamples, NativeOracleViolations;
        internal float MaxNativePositionError, MaxNativeRotationError;
        internal float FirstTime = float.PositiveInfinity;
    }

    sealed class NativeMotionClock : IAudioTimeSource
    {
        public float Time, Delta = 1f / 90f, Rate = 1f;
        public float songTime => Time;
        public float lastFrameDeltaSongTime => Delta;
        public float songEndTime => 1000f;
        public float songLength => 1000f;
        public float timeScale => Rate;
        public bool isReady => true;
        public IAudioTimeSource.State state => default;
    }

    sealed class NativeMotionFixture : IDisposable
    {
        internal const float NoteTime = 10f;
        readonly GameObject owner;
        internal readonly NoteJump Jump;
        internal readonly Transform Rotated;
        internal readonly NativeMotionClock Clock = new();
        internal readonly PlayerTransforms Player;
        internal readonly List<string> Events = new();
        internal float ReportedProgress;

        internal NativeMotionFixture(NoteJump original, float angle, bool preservePose = false)
        {
            owner = new GameObject("Native speed regression fixture");
            owner.SetActive(false);
            Transform parent = original.transform.parent;
            if (parent != null)
            {
                owner.transform.position = parent.position;
                owner.transform.rotation = parent.rotation;
                owner.transform.localScale = parent.lossyScale;
            }
            GameObject root = new("Jump");
            root.transform.SetParent(owner.transform, false);
            root.transform.localPosition = original.transform.localPosition;
            root.transform.localRotation = original.transform.localRotation;
            root.transform.localScale = original.transform.localScale;
            Jump = root.AddComponent<NoteJump>();
            CopyFields(original, Jump);
            GameObject child = new("Rotated");
            child.transform.SetParent(root.transform, false);
            Rotated = child.transform;
            SetTrainingMember(Jump, "_rotatedObject", Rotated);
            SetTrainingMember(Jump, "_audioTimeSyncController", Clock);
            SetTrainingMember(Jump, "_noteTime", NoteTime);
            PlayerTransforms player = GetMember(original, "_playerTransforms") as PlayerTransforms;
            if (player != null)
            {
                GameObject head = new("Player");
                head.transform.SetParent(owner.transform, false);
                Player = head.AddComponent<PlayerTransforms>();
                CopyFields(player, Player);
                SetTrainingMember(Jump, "_playerTransforms", Player);
            }
            if (preservePose)
            {
                Transform rotated = GetMember(original, "_rotatedObject") as Transform;
                if (rotated != null)
                {
                    Rotated.localPosition = rotated.localPosition;
                    Rotated.localRotation = rotated.localRotation;
                    Rotated.localScale = rotated.localScale;
                }
            }
            else
            {
                Quaternion roll = Quaternion.Euler(0f, 0f, angle);
                SetTrainingMember(Jump, "_startRotation", Quaternion.identity);
                SetTrainingMember(Jump, "_middleRotation", roll * Quaternion.Euler(12f, -9f, 7f));
                SetTrainingMember(Jump, "_endRotation", roll);
                SetTrainingMember(Jump, "_rotateTowardsPlayer", true);
                ResetMarkers();
            }
            Jump.noteJumpDidStartEvent += () => Events.Add("start@" + Clock.Time.ToString("R"));
            Jump.noteJumpDidPassHalfEvent += () => Events.Add("half@" + Clock.Time.ToString("R"));
            Jump.noteJumpDidPassThreeQuartersEvent += value => Events.Add("threequarters@" + Clock.Time.ToString("R"));
            Jump.noteJumpDidPassMissedMarkerEvent += () => Events.Add("miss@" + Clock.Time.ToString("R"));
            Jump.noteJumpDidFinishEvent += () => Events.Add("finish@" + Clock.Time.ToString("R"));
            Jump.noteJumpDidUpdateProgressEvent += value => ReportedProgress = value;
        }

        internal void ResetMarkers()
        {
            foreach (string flag in new[] { "_jumpStartedReported", "_missedMarkReported", "_threeQuartersMarkReported", "_halfJumpMarkReported" })
                SetTrainingMember(Jump, flag, false);
            Events.Clear();
            ReportedProgress = float.NaN;
        }

        internal void SetHead(Vector3 value)
        {
            if (Player == null) throw new InvalidOperationException("Native head input is unavailable.");
            SetTrainingMember(Player, "_headPseudoLocalPos", value);
            SetTrainingMember(Player, "_headPseudoLocalZOnlyPos", new Vector3(0f, 0f, value.z));
        }

        static void CopyFields(object source, object destination)
        {
            foreach (FieldInfo field in source.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (!field.IsStatic && !typeof(Delegate).IsAssignableFrom(field.FieldType)) field.SetValue(destination, field.GetValue(source));
        }
        public void Dispose() => UnityEngine.Object.DestroyImmediate(owner);
    }

    static class NativeSpeedOracle
    {
        // Original reverse patches read the game's on-disk IL, excluding both
        // Harmony transpilers and SongCore's instance-capturing ILHook.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Vector3 Jump(NoteJump instance) => throw new NotSupportedException("Oracle is not installed.");
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Update(VariableMovementDataProvider instance, float time) => throw new NotSupportedException("Oracle is not installed.");
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ManualUpdate(VariableMovementDataProvider instance, float time) => throw new NotSupportedException("Oracle is not installed.");
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Init(VariableMovementDataProvider instance, float halfBeats, float maxDistance, float initial, float minimum, float bpm,
            BeatmapObjectSpawnMovementData.NoteJumpValueType type, float value, Vector3 center, Vector3 forward) => throw new NotSupportedException("Oracle is not installed.");
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Event(VariableMovementDataProvider instance, NoteJumpSpeedEventData data) => throw new NotSupportedException("Oracle is not installed.");
    }

    void EnsureNativeSpeedOracle()
    {
        if (trainingNativeOracleReady) return;
        Harmony oracle = new("EffectModsSmoke.OriginalSpeedOracle");
        foreach (var pair in new[] {
            new { Type = typeof(NoteJump), Original = "ManualUpdate", Standin = "Jump" },
            new { Type = typeof(VariableMovementDataProvider), Original = "ManualUpdate", Standin = "Update" },
            new { Type = typeof(VariableMovementDataProvider), Original = "ManualUpdate", Standin = "ManualUpdate" },
            new { Type = typeof(VariableMovementDataProvider), Original = "Init", Standin = "Init" },
            new { Type = typeof(VariableMovementDataProvider), Original = "HandleNoteJumpMovementSpeedEvent", Standin = "Event" } })
            oracle.CreateReversePatcher(AccessTools.Method(pair.Type, pair.Original),
                new HarmonyMethod(AccessTools.Method(typeof(NativeSpeedOracle), pair.Standin))).Patch(
                    pair.Standin == "ManualUpdate" ? HarmonyReversePatchType.Snapshot : HarmonyReversePatchType.Original);
        trainingNativeOracleReady = true;
    }

    static VariableMovementDataProvider MakeNativeProvider(float initial, float minimum = 0f, PlayerTransforms player = null)
    {
        VariableMovementDataProvider provider = new(player, null);
        NativeSpeedOracle.Init(provider, 4f, 18f, initial, minimum, 120f,
            BeatmapObjectSpawnMovementData.NoteJumpValueType.BeatOffset, 0f, Vector3.zero, Vector3.forward);
        // Init calls the globally detoured ManualUpdate. Recompute all derived
        // geometry from this instance through the pristine body. Spawn horizon
        // itself is calculated by Init from its original input parameters.
        SetTrainingMember(provider, "_prevNoteJumpMovementSpeed", float.NaN);
        NativeSpeedOracle.Update(provider, 0f);
        return provider;
    }

    void VerifyNativeSpeedRegression(NoteJump live)
    {
        if (trainingSpeedRegressionChecked || live == null || trainingMotionAuthoredLoaded) return;
        if (!(GetMember(live, "_variableMovementDataProvider") is VariableMovementDataProvider)) return;

        Type runtime = FindTrainingType("NoteMotionControl.RuntimeState");
        Type speed = FindTrainingType("NoteMotionControl.SpeedRuntime");
        FieldInfo activeField = AccessTools.Field(runtime, "<GameplayActive>k__BackingField");
        FieldInfo optionsField = AccessTools.Field(runtime, "<ActiveOptions>k__BackingField");
        FieldInfo containerField = AccessTools.Field(runtime, "activeContainer");
        FieldInfo ticketField = AccessTools.Field(runtime, "activeTicket");
        FieldInfo submissionField = AccessTools.Field(runtime, "activeSubmission");
        FieldInfo preserveField = AccessTools.Field(runtime, "<PreserveAuthoredMotion>k__BackingField");
        FieldInfo loggerField = AccessTools.Field(runtime, "logger");
        FieldInfo noticeField = AccessTools.Field(runtime, "<LastNotice>k__BackingField");
        FieldInfo policiesField = AccessTools.Field(speed, "policies");
        MethodInfo initialize = AccessTools.Method(speed, "Initialize");
        MethodInfo afterInitialization = AccessTools.Method(speed, "AfterInitialization");
        MethodInfo applySpeed = AccessTools.Method(speed, "ApplySpeed");
        MethodInfo manualUpdate = AccessTools.Method(
            typeof(NativeSpeedOracle), nameof(NativeSpeedOracle.ManualUpdate),
            new[] { typeof(VariableMovementDataProvider), typeof(float) });
        IDictionary policies = policiesField?.GetValue(null) as IDictionary;
        FieldInfo warningsField = AccessTools.Field(runtime, "warnings");
        var warnings = warningsField?.GetValue(null) as HashSet<string>;

        FieldInfo[] state =
        {
            activeField, optionsField, containerField, ticketField, submissionField,
            preserveField, loggerField, noticeField
        };
        object[] previous = state.Select(field => field?.GetValue(null)).ToArray();
        var savedWarnings = warnings?.ToArray();
        var savedPolicies = new List<DictionaryEntry>();
        if (policies != null)
            foreach (DictionaryEntry entry in policies) savedPolicies.Add(entry);

        try
        {
            EnsureNativeSpeedOracle();
            if (state.Any(field => field == null) || initialize == null || afterInitialization == null ||
                applySpeed == null || manualUpdate == null || policies == null)
                throw new MissingMemberException("The merged native speed fixture interface is incomplete.");

            VerifyMergedNativeSpeedInputs(
                initialize,
                afterInitialization,
                applySpeed,
                activeField,
                optionsField,
                containerField,
                ticketField,
                submissionField,
                preserveField,
                loggerField,
                policies,
                manualUpdate);
            trainingSpeedRegressionChecked = true;
        }
        catch (Exception error)
        {
            Exception actual = error is TargetInvocationException invocation && invocation.InnerException != null
                ? invocation.InnerException
                : error;
            Check("training-speed-regression-fixture", false, actual.ToString());
        }
        finally
        {
            if (policies != null)
            {
                policies.Clear();
                foreach (DictionaryEntry entry in savedPolicies) policies.Add(entry.Key, entry.Value);
            }

            for (int index = 0; index < state.Length; index++)
                state[index]?.SetValue(null, previous[index]);

            if (warnings != null && savedWarnings != null)
            {
                warnings.Clear();
                foreach (string warning in savedWarnings) warnings.Add(warning);
            }
        }
    }

    void VerifyMergedNativeSpeedInputs(
        MethodInfo initialize,
        MethodInfo afterInitialization,
        MethodInfo applySpeed,
        FieldInfo activeField,
        FieldInfo optionsField,
        FieldInfo containerField,
        FieldInfo ticketField,
        FieldInfo submissionField,
        FieldInfo preserveField,
        FieldInfo loggerField,
        IDictionary policies,
        MethodInfo manualUpdate)
    {
        Type optionsType = optionsField.GetValue(null)?.GetType();
        if (optionsType == null) throw new MissingMemberException("RuntimeState.ActiveOptions");

        // A non-null fixture ticket makes the successful policy cases exercise
        // the same EnsureMotionTicket gate used by gameplay. The failure case
        // below clears both ticket and submission and verifies no Init input is
        // changed.
        object fixtureTicket = FormatterServices.GetUninitializedObject(ticketField.FieldType);
        ticketField.SetValue(null, fixtureTicket);
        submissionField.SetValue(null, null);
        loggerField.SetValue(null, null);
        preserveField.SetValue(null, false);
        activeField.SetValue(null, true);

        int initViolations = 0;
        int eventViolations = 0;
        int curveViolations = 0;
        int poolViolations = 0;
        int cases = 0;
        var curveEvidence = new List<string>();
        var eventEvidence = new List<string>();
        BeatmapData eventSource = new(0) { updateAllBeatmapDataOnInsert = true };
        foreach (NoteJumpSpeedEventData value in new[]
        {
            new NoteJumpSpeedEventData(0f, 0f, EaseType.None, false),
            new NoteJumpSpeedEventData(4f, 6f, EaseType.None, false),
            new NoteJumpSpeedEventData(4.1f, -2f, EaseType.None, false),
            new NoteJumpSpeedEventData(10f, 0f, EaseType.Linear, false),
            new NoteJumpSpeedEventData(14f, 3f, EaseType.InOutSine, false)
        }) eventSource.InsertBeatmapEventData(value);
        NoteJumpSpeedEventData[] events = eventSource.allBeatmapDataItems
            .OfType<NoteJumpSpeedEventData>()
            .OrderBy(value => value.time)
            .ToArray();

        foreach (string mode in new[] { "Disabled", "Map", "Fixed", "MultiplierLow", "MultiplierHigh", "Identity", "Reaction" })
        {
            object options = Activator.CreateInstance(optionsType, true);
            bool enabled = mode != "Disabled";
            int njsMode = mode == "Fixed" ? 1 :
                mode == "MultiplierLow" || mode == "MultiplierHigh" || mode == "Identity" ? 2 : 0;
            float multiplier = mode == "MultiplierLow" ? 0.65f :
                mode == "MultiplierHigh" ? 1.5f : 1f;
            SetTrainingMember(options, "Enabled", enabled);
            SetTrainingEnum(options, "NjsMode", njsMode,
                njsMode == 1 ? "Override" : njsMode == 2 ? "Multiplier" : "Map");
            SetTrainingEnum(options, "ApplicationPolicy", mode == "Fixed" ? 1 : 0,
                mode == "Fixed" ? "Strict" : "OnlyReduce");
            SetTrainingMember(options, "OverrideNjs", 16f);
            SetTrainingMember(options, "SpeedMultiplier", multiplier);
            if (mode == "Reaction")
            {
                SetTrainingEnum(options, "ReactionTimeMode", 1, "Specified");
                // Keep this above the fixture's native one-second baseline so
                // OnlyReduce has a real timing change to attach and ticket.
                SetTrainingMember(options, "ReactionTimeMs", 1200f);
            }
            optionsField.SetValue(null, options);

            VariableMovementDataProvider changed = new(null, null);
            Zenject.DiContainer container = new();
            container.Bind<IVariableMovementDataProvider>().FromInstance(changed);
            containerField.SetValue(null, container);

            object[] init = NativeInitArguments(changed);
            initialize.Invoke(null, init);
            // OnlyReduce must be a true no-op when a multiplier raises NJS;
            // the runtime should therefore avoid both the policy map and the
            // ticket for MultiplierHigh. Strict fixed mode and reductions still
            // exercise the changed native route.
            bool policyExpected = enabled &&
                (njsMode == 1 || njsMode == 2 && multiplier < 1f || mode == "Reaction");
            bool initChanged = (BeatmapObjectSpawnMovementData.NoteJumpValueType)init[6] !=
                    BeatmapObjectSpawnMovementData.NoteJumpValueType.BeatOffset ||
                Math.Abs((float)init[7]) > 0.00001f;
            if (Math.Abs((float)init[3] - 12f) > 0.00001f ||
                Math.Abs((float)init[4] + 2f) > 0.00001f ||
                (policyExpected && (!initChanged || !IsFiniteTraining((float)init[7]))) ||
                (!policyExpected && initChanged))
                initViolations++;

            NativeSpeedOracle.Init(
                changed,
                4f,
                18f,
                (float)init[3],
                (float)init[4],
                120f,
                (BeatmapObjectSpawnMovementData.NoteJumpValueType)init[6],
                (float)init[7],
                Vector3.zero,
                Vector3.forward);
            SetTrainingMember(changed, "_prevNoteJumpMovementSpeed", float.NaN);
            manualUpdate.Invoke(null, new object[] { changed, 0f });
            afterInitialization.Invoke(null, new object[] { changed });

            bool policyAttached = policies.Contains(changed);
            if (policyAttached != policyExpected) initViolations++;

            VariableMovementDataProvider native = new(null, null);
            NativeSpeedOracle.Init(
                native, 4f, 18f, 12f, -2f, 120f,
                BeatmapObjectSpawnMovementData.NoteJumpValueType.BeatOffset, 0f,
                Vector3.zero, Vector3.forward);
            SetTrainingMember(native, "_prevNoteJumpMovementSpeed", float.NaN);

            int next = 0;
            foreach (float time in new[] { 0f, 1f, 2f, 3.5f, 4f, 4.1f, 5f, 8f, 10f, 12f, 14f, 16f, 20f })
            {
                while (next < events.Length && events[next].time <= time)
                {
                    NativeSpeedOracle.Event(changed, events[next]);
                    NativeSpeedOracle.Event(native, events[next]);
                    next++;
                }

                NativeSpeedOracle.Update(native, time);
                manualUpdate.Invoke(null, new object[] { changed, time });

                float raw = native.noteJumpSpeed;
                float expected = ExpectedSpeed(raw, njsMode, options);
                bool speedMismatch = !IsFiniteTraining(changed.noteJumpSpeed) ||
                    Math.Abs(changed.noteJumpSpeed - expected) > 0.0001f;
                bool timingMismatch = !IsFiniteTraining(changed.halfJumpDuration) ||
                    !IsFiniteTraining(changed.spawnAheadTime) ||
                    changed.halfJumpDuration <= 0f ||
                    changed.spawnAheadTime <= 0f;
                if (speedMismatch)
                    curveViolations++;
                if (timingMismatch)
                    curveViolations++;
                bool reactionMismatch = mode == "Reaction" && changed.halfJumpDuration < 1.1999f;
                if (reactionMismatch)
                    curveViolations++;
                if ((speedMismatch || timingMismatch || reactionMismatch) && curveEvidence.Count < 32)
                    curveEvidence.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}@{1:0.###}: raw={2:R}; expected={3:R}; actual={4:R}; half={5:R}; spawn={6:R}; policy={7}",
                        mode,
                        time,
                        raw,
                        expected,
                        changed.noteJumpSpeed,
                        changed.halfJumpDuration,
                        changed.spawnAheadTime,
                        policyAttached));

                object actualInterpolation = GetMember(changed, "_relativeNoteJumpSpeedInterpolation");
                object nativeInterpolation = GetMember(native, "_relativeNoteJumpSpeedInterpolation");
                foreach (string field in new[] { "_fromValue", "_toValue", "_fromTime", "_duration", "_easeType" })
                    if (!Equals(GetMember(actualInterpolation, field), GetMember(nativeInterpolation, field)))
                    {
                        eventViolations++;
                        if (eventEvidence.Count < 32)
                            eventEvidence.Add(string.Format(
                                CultureInfo.InvariantCulture,
                                "{0}@{1:0.###} field={2}; actual={3}; native={4}",
                                mode,
                                time,
                                field,
                                GetMember(actualInterpolation, field),
                                GetMember(nativeInterpolation, field)));
                    }
            }

            // Reinitializing the same provider must clear its old policy and
            // force the native body to recompute from the fresh source speed.
            // A no-op OnlyReduce policy is allowed to leave the native pooled
            // previous-speed field alone; changed policies must reset it
            // before the original Init body runs.
            object[] pooledInit = NativeInitArguments(changed);
            initialize.Invoke(null, pooledInit);
            bool pooledPolicyAttached = policies.Contains(changed);
            if (Math.Abs((float)pooledInit[3] - 12f) > 0.00001f ||
                Math.Abs((float)pooledInit[4] + 2f) > 0.00001f ||
                pooledPolicyAttached != policyExpected ||
                policyExpected && !float.IsNaN(ReadFloat(changed, "_prevNoteJumpMovementSpeed", float.NaN)))
                poolViolations++;
            NativeSpeedOracle.Init(
                changed, 4f, 18f, (float)pooledInit[3], (float)pooledInit[4], (float)pooledInit[5],
                (BeatmapObjectSpawnMovementData.NoteJumpValueType)pooledInit[6], (float)pooledInit[7],
                Vector3.zero, Vector3.forward);
            SetTrainingMember(changed, "_prevNoteJumpMovementSpeed", float.NaN);
            manualUpdate.Invoke(null, new object[] { changed, 0f });
            afterInitialization.Invoke(null, new object[] { changed });

            VariableMovementDataProvider fresh = new(null, null);
            Zenject.DiContainer freshContainer = new();
            freshContainer.Bind<IVariableMovementDataProvider>().FromInstance(fresh);
            containerField.SetValue(null, freshContainer);
            object[] freshInit = NativeInitArguments(fresh);
            initialize.Invoke(null, freshInit);
            bool freshPolicyAttached = policies.Contains(fresh);
            if (freshPolicyAttached != policyExpected ||
                (BeatmapObjectSpawnMovementData.NoteJumpValueType)freshInit[6] !=
                    (BeatmapObjectSpawnMovementData.NoteJumpValueType)pooledInit[6] ||
                Math.Abs((float)freshInit[7] - (float)pooledInit[7]) > 0.00001f)
                poolViolations++;
            NativeSpeedOracle.Init(
                fresh, 4f, 18f, (float)freshInit[3], (float)freshInit[4], (float)freshInit[5],
                (BeatmapObjectSpawnMovementData.NoteJumpValueType)freshInit[6], (float)freshInit[7],
                Vector3.zero, Vector3.forward);
            SetTrainingMember(fresh, "_prevNoteJumpMovementSpeed", float.NaN);
            manualUpdate.Invoke(null, new object[] { fresh, 0f });
            afterInitialization.Invoke(null, new object[] { fresh });
            if (Math.Abs(changed.noteJumpSpeed - fresh.noteJumpSpeed) > 0.0001f ||
                Math.Abs(changed.halfJumpDuration - fresh.halfJumpDuration) > 0.0001f ||
                Math.Abs(changed.spawnAheadTime - fresh.spawnAheadTime) > 0.0001f ||
                Math.Abs(ReadFloat(changed, "_waitingDuration", float.NaN) -
                    ReadFloat(fresh, "_waitingDuration", float.NaN)) > 0.0001f)
                poolViolations++;
            policies.Remove(changed);
            policies.Remove(fresh);
            cases++;
        }

        // A failed ticket acquisition must leave the exact native Init
        // arguments intact and must not install a policy.
        ticketField.SetValue(null, null);
        submissionField.SetValue(null, null);
        object failureOptions = Activator.CreateInstance(optionsType, true);
        SetTrainingMember(failureOptions, "Enabled", true);
        SetTrainingEnum(failureOptions, "NjsMode", 1, "Override");
        SetTrainingEnum(failureOptions, "ApplicationPolicy", 1, "Strict");
        SetTrainingMember(failureOptions, "OverrideNjs", 16f);
        optionsField.SetValue(null, failureOptions);
        VariableMovementDataProvider refused = new(null, null);
        Zenject.DiContainer refusedContainer = new();
        refusedContainer.Bind<IVariableMovementDataProvider>().FromInstance(refused);
        containerField.SetValue(null, refusedContainer);
        object[] refusedInit = NativeInitArguments(refused);
        initialize.Invoke(null, refusedInit);
        bool ticketFailureChanged = (BeatmapObjectSpawnMovementData.NoteJumpValueType)refusedInit[6] !=
                BeatmapObjectSpawnMovementData.NoteJumpValueType.BeatOffset ||
            Math.Abs((float)refusedInit[7]) > 0.00001f;
        bool ticketFailureAttached = policies.Contains(refused);

        RecordSpeedFixtureCheck("training-speed-native-init-preserves-inputs", cases == 7 && initViolations == 0,
            "cases=" + cases + "; violations=" + initViolations);
        RecordSpeedFixtureCheck("training-speed-native-easing-is-untouched", eventViolations == 0,
            "event interpolation violations=" + eventViolations + "; evidence=" + string.Join(" | ", eventEvidence));
        RecordSpeedFixtureCheck("training-speed-native-curve-and-timing", curveViolations == 0,
            "curve/timing violations=" + curveViolations + "; evidence=" + string.Join(" | ", curveEvidence));
        RecordSpeedFixtureCheck("training-speed-provider-pool-reset", poolViolations == 0,
            "pool violations=" + poolViolations);
        RecordSpeedFixtureCheck("training-speed-ticket-failure-no-mutation", !ticketFailureChanged && !ticketFailureAttached,
            "changed=" + ticketFailureChanged + "; policy=" + ticketFailureAttached);
    }

    void RecordSpeedFixtureCheck(string name, bool passed, string detail)
    {
        checks.Add(new { name, passed, detail });
        if (!passed) errors.Add(name + ": " + detail);
    }

    static object[] NativeInitArguments(VariableMovementDataProvider provider)
    {
        return new object[]
        {
            provider, 4f, 18f, 12f, -2f, 120f,
            BeatmapObjectSpawnMovementData.NoteJumpValueType.BeatOffset, 0f
        };
    }

    static float ExpectedSpeed(float raw, int njsMode, object options)
    {
        float target = njsMode == 1
            ? ReadFloat(options, "OverrideNjs", raw)
            : njsMode == 2
                ? raw * ReadFloat(options, "SpeedMultiplier", 1f)
                : raw;
        bool onlyReduce = ReadInt(options, "ApplicationPolicy", 0) == 0;
        return onlyReduce ? Mathf.Min(raw, target) : target;
    }

    // Shared by the authored-motion oracle in TrainingNoodleSpeedChecks. Keep
    // these independent from the merged speed-policy fixture above: they only
    // compare native pose/event observations.
    static bool SameNativeMotionEvents(NativeMotionFixture first, NativeMotionFixture second) => first.Events.SequenceEqual(second.Events);
    static float[] BuildNativeMotionSamples(int frames)
    {
        var samples = Enumerable.Range(0, frames + 1).Select(index => index / (float)frames).ToList();
        foreach (float value in new[] { 0f, 0.12499f, 0.125f, 0.12501f, 0.24999f, 0.25f, 0.25001f, 0.49999f, 0.5f, 0.50001f, 0.74999f, 0.75f, 0.75001f, 1.01f }) samples.Add(value);
        samples.Add(0.3f); samples.Add(0.3f);
        return samples.OrderBy(value => value).ToArray();
    }

    static class TrainingMotionRouteProbe
    {
        internal static Runner Owner;
        static void Prefix(NoteJump __instance, out NativeMotionFixture __state)
        {
            __state = null;
            Runner owner = Owner;
            if (owner == null || owner.trainingMotionAuthoredLoaded || !owner.trainingNativeOracleReady || Time.frameCount % 24 != 0) return;
            if (__instance == null || !__instance.gameObject.activeInHierarchy) return;
            try
            {
                __state = new NativeMotionFixture(__instance, 0f, true);
                float time = ReadFloat(GetMember(__instance, "_audioTimeSyncController"), "songTime", float.NaN);
                SetTrainingMember(__state.Jump, "_noteTime", GetMember(__instance, "_noteTime"));
                __state.Clock.Time = time;
                NativeSpeedOracle.Jump(__state.Jump);
            }
            catch (Exception error) { __state?.Dispose(); __state = null; owner.errors.Add("training-speed-route-prefix: " + error); }
        }
        static void Postfix(NoteJump __instance, NativeMotionFixture __state)
        {
            if (__state == null) return;
            try
            {
                Runner owner = Owner;
                float position = Vector3.Distance(ReadVector3(__instance, "_localPosition"), ReadVector3(__state.Jump, "_localPosition"));
                Transform rotated = GetMember(__instance, "_rotatedObject") as Transform;
                float rotation = rotated == null ? float.PositiveInfinity : Quaternion.Angle(rotated.localRotation, __state.Rotated.localRotation);
                owner.trainingMotionRouteSamples++;
                owner.trainingMotionMaxPositionError = Mathf.Max(owner.trainingMotionMaxPositionError, position);
                owner.trainingMotionMaxRotationError = Mathf.Max(owner.trainingMotionMaxRotationError, rotation);
                bool invalid = position > 0.002f || rotation > 0.1f || !IsFiniteTraining(position) || !IsFiniteTraining(rotation);
                if (invalid) owner.trainingMotionRouteViolations++;
                NoteController controller = __instance.GetComponent<NoteController>() ?? __instance.GetComponentInParent<NoteController>();
                if (controller?.noteData != null)
                    foreach (string category in TrainingMotionCategories(controller.noteData))
                    {
                        SpeedElement element = owner.trainingMotionElements[category];
                        element.NativeOracleSamples++;
                        if (invalid) element.NativeOracleViolations++;
                        element.MaxNativePositionError = Mathf.Max(element.MaxNativePositionError, position);
                        element.MaxNativeRotationError = Mathf.Max(element.MaxNativeRotationError, rotation);
                    }
            }
            finally { __state.Dispose(); }
        }
    }

    void CaptureNoteMotionGameplay(IReadonlyBeatmapData beatmap)
    {
        Type runtime = FindTrainingType("NoteMotionControl.RuntimeState");
        object options = GetStaticMember(runtime, "ActiveOptions");
        trainingMotionExpected = options != null && GetStaticMember(runtime, "ActiveSetup") != null;
        trainingSpeedProfileEnabled = ReadBool(options, "Enabled", false);
        trainingSpeedProfileMode = ReadInt(options, "NjsMode", -1);
        trainingSpeedProfileApplicationPolicy = ReadInt(options, "ApplicationPolicy", 0);
        trainingSpeedProfileReactionMode = ReadInt(options, "ReactionTimeMode", 0);
        trainingSpeedProfileReactionMs = ReadFloat(options, "ReactionTimeMs", 800f);
        trainingSpeedProfileFixedNjs = ReadFloat(options, "OverrideNjs", float.NaN);
        trainingSpeedProfileMultiplier = ReadFloat(options, "SpeedMultiplier", float.NaN);
        trainingSpeedInterfaceReady = ReadBool(GetStaticMember(runtime, "MovementInterfaceReady"), string.Empty, false);
        trainingSpeedNativeProviderSeen = false;
        trainingSpeedExpectedNativePolicy = false;
        trainingSpeedExpectedPolicyResolved = false;
        trainingMotionPolicyObserved = false;
        trainingMotionModificationObserved = false;
        trainingSpeedPolicySamples = trainingSpeedPolicyViolations = 0;
        trainingSpeedObservedProviders.Clear();
        trainingSpeedProviderEvidence.Clear();
        Check("training-note-speed-gameplay-state", trainingMotionExpected, "Current player profile/session is available");
        trainingMotionAuthoredLoaded = ReadBool(GetStaticMember(runtime, "PreserveAuthoredMotion"), string.Empty, false);
        var setup = GetStaticMember(runtime, "ActiveSetup") as GameplayCoreSceneSetupData;
        trainingMotionNoodleLoaded = setup != null && CustomLevel &&
            SongCore.Collections.GetCustomLevelSongDifficultyData(setup.beatmapKey)?.additionalDifficultyData?._requirements?.Contains("Noodle Extensions") == true;
        var methods = Harmony.GetAllPatchedMethods().Where(method => Harmony.GetPatchInfo(method)?.Owners.Contains("localtraining.NoteMotionControl") == true).ToArray();
        bool IsSpeedInitPatch(MethodBase method) => method.DeclaringType == typeof(VariableMovementDataProvider) && method.Name == "Init";
        bool IsSpeedCurvePatch(MethodBase method) => method.DeclaringType == typeof(VariableMovementDataProvider) && method.Name == "ManualUpdate";
        bool IsMenuPatch(MethodBase method) => method.DeclaringType?.FullName == "BeatSaberMarkupLanguage.Settings.SettingsMenu" && method.Name == "Setup";
        bool IsLoadPatch(MethodBase method)
        {
            string key = method.DeclaringType?.FullName + "." + method.Name;
            return key == "MultiplayerLevelScenesTransitionSetupData.Init" ||
                key == "StandardLevelScenesTransitionSetupData.Init" ||
                key == "MissionLevelScenesTransitionSetupData.Init" ||
                key == "BeatmapEditor3D.BeatmapEditorStandardLevelScenesTransitionSetupData.Init" ||
                key == "GameplayCoreSceneSetupData.LoadTransformedBeatmapDataAsync" ||
                key == "BeatmapDataLoader.CreateOrGetTransformedBeatmapDataAsync" ||
                key == "BeatmapDataTransformHelper.CreateTransformedBeatmapData";
        }
        bool IsLifecyclePatch(MethodBase method)
        {
            string type = method.DeclaringType?.FullName;
            if (type == "BeatmapObjectSpawnMovementData") return method.Name == "Init";
            if (type == "NoteMovement" || type == "SliderMovement") return method.Name == "Init";
            if (type == "NoteJump") return method.Name == "Init";
            if (type == "NoteController")
                return method.Name == "Init" || method.Name == "Hide" ||
                    method.Name == "HandleNoteDidStartJump" || method.Name == "OnDestroy";
            if (type == "SliderController")
                return method.Name == "Init" || method.Name == "Hide" ||
                    method.Name == "ManualUpdate" || method.Name == "OnDestroy";
            return false;
        }
        bool IsAllowedPatch(MethodBase method) => IsSpeedInitPatch(method) || IsSpeedCurvePatch(method) ||
            IsMenuPatch(method) || IsLoadPatch(method) || IsLifecyclePatch(method);
        Check("training-note-speed-patch-boundary", methods.Any(IsSpeedInitPatch) && methods.Any(IsSpeedCurvePatch) &&
            methods.Any(IsMenuPatch) && methods.All(IsAllowedPatch),
            string.Join("; ", methods.Select(method => method.DeclaringType.FullName + "." + method.Name)));
        EnsureNativeSpeedOracle();
        trainingMotionElements.Clear();
        foreach (string name in new[] { "Arrow", "Dot", "Bomb", "ArcHead", "ArcTail", "ChainHead", "ChainElement", "Arc", "Obstacle" })
            trainingMotionElements.Add(name, new SpeedElement());
        int generatedSlices = 0;
        float firstSliceTime = float.PositiveInfinity;
        foreach (BeatmapDataItem item in beatmap.allBeatmapDataItems)
        {
            IEnumerable<string> names = item is NoteData note ? TrainingMotionCategories(note) :
                item is SliderData slider && slider.sliderType == SliderData.Type.Normal ? new[] { "Arc" } :
                item is ObstacleData ? new[] { "Obstacle" } : Array.Empty<string>();
            foreach (string name in names)
            {
                trainingMotionElements[name].ChartObjects++;
                trainingMotionElements[name].FirstTime = Mathf.Min(trainingMotionElements[name].FirstTime, item.time);
            }
            // In 1.45.2 the Burst branch calls ProcessNoteData once per slice.
            // Only normal arcs allocate a SliderController/mesh of their own.
            if (item is SliderData burst && burst.sliderType == SliderData.Type.Burst && burst.sliceCount > 1)
            {
                generatedSlices += burst.sliceCount - 1;
                firstSliceTime = Mathf.Min(firstSliceTime, Mathf.LerpUnclamped(burst.time, burst.tailTime, 1f / (burst.sliceCount - 1)));
            }
        }
        SpeedElement chain = trainingMotionElements["ChainElement"];
        chain.ChartObjects = Math.Max(chain.ChartObjects, generatedSlices);
        chain.FirstTime = Mathf.Min(chain.FirstTime, firstSliceTime);
        if (manager != null)
        {
            manager.noteWasCutEvent += OnTrainingMotionCut;
            manager.noteWasSpawnedEvent += OnTrainingMotionNoteSpawned;
            manager.sliderWasSpawnedEvent += OnTrainingMotionSliderSpawned;
            manager.sliderWasDespawnedEvent += OnTrainingMotionSliderDespawned;
            manager.obstacleWasSpawnedEvent += OnTrainingMotionObstacleSpawned;
            foreach (object item in ActiveTrainingNoteControllers()) if (item is NoteController note) OnTrainingMotionNoteSpawned(note);
        }
        if (!trainingMotionAuthoredLoaded)
        {
            TrainingMotionRouteProbe.Owner = this;
            trainingMotionRouteHarmony = new Harmony("EffectModsSmoke.NativeSpeedRoute");
            trainingMotionRouteHarmony.Patch(AccessTools.Method(typeof(NoteJump), "ManualUpdate"),
                prefix: new HarmonyMethod(typeof(TrainingMotionRouteProbe), "Prefix"), postfix: new HarmonyMethod(typeof(TrainingMotionRouteProbe), "Postfix"));
        }
    }

    static IEnumerable<string> TrainingMotionCategories(NoteData note)
    {
        if (note.gameplayType == NoteData.GameplayType.Bomb) yield return "Bomb";
        else if (note.gameplayType == NoteData.GameplayType.BurstSliderHead) yield return "ChainHead";
        else if (note.gameplayType == NoteData.GameplayType.BurstSliderElement) yield return "ChainElement";
        else yield return note.cutDirection == NoteCutDirection.Any ? "Dot" : "Arrow";
        if (note.isArcHead) yield return "ArcHead";
        if (note.isArcTail) yield return "ArcTail";
    }
    void OnTrainingMotionCut(NoteController controller, in NoteCutInfo info) => trainingMotionCutEvents++;
    void OnTrainingMotionNoteSpawned(NoteController controller)
    {
        foreach (string name in TrainingMotionCategories(controller.noteData)) trainingMotionElements[name].SpawnedObjects++;
    }
    void OnTrainingMotionObstacleSpawned(ObstacleController controller) => trainingMotionElements["Obstacle"].SpawnedObjects++;
    void OnTrainingMotionSliderSpawned(SliderController controller)
    {
        if (controller.sliderData.sliderType != SliderData.Type.Normal)
        {
            Check("training-native-slider-controller-type", false, "An unexpected burst payload reached a native arc controller.");
            return;
        }
        trainingMotionSliders.Add(controller);
        trainingMotionElements["Arc"].SpawnedObjects++;
    }
    void OnTrainingMotionSliderDespawned(SliderController controller) => trainingMotionSliders.Remove(controller);

    bool ExpectedLiveNativeSpeedPolicy(VariableMovementDataProvider provider, bool policyObserved)
    {
        if (policyObserved) return true;
        if (!trainingSpeedProfileEnabled || trainingMotionAuthoredLoaded || provider == null)
            return false;

        float initial = ReadFloat(provider, "_initNoteJumpMovementSpeed", float.NaN);
        if (!IsFiniteTraining(initial) || initial <= 0f) return false;

        bool changed = LiveNativeSpeedDiffers(initial);
        Type runtime = FindTrainingType("NoteMotionControl.RuntimeState");
        object setup = GetStaticMember(runtime, "ActiveSetup");
        if (setup != null && GetMember(setup, "transformedBeatmapData") is IReadonlyBeatmapData data)
            foreach (NoteJumpSpeedEventData speed in data.allBeatmapDataItems.OfType<NoteJumpSpeedEventData>())
            {
                float raw = Mathf.Max(0.01f, initial + speed.relativeNoteJumpSpeed);
                if (IsFiniteTraining(raw) && LiveNativeSpeedDiffers(raw))
                {
                    changed = true;
                    break;
                }
            }

        if (trainingSpeedProfileReactionMode == 1)
        {
            float requestedHalf = trainingSpeedProfileReactionMs / 1000f;
            float baseHalfBeats = ReadFloat(provider, "_halfJumpDurationInBeats", float.NaN);
            float beatDuration = ReadFloat(provider, "_initOneBeatDuration", float.NaN);
            float nativeHalf = baseHalfBeats * beatDuration;
            if (IsFiniteTraining(requestedHalf) && IsFiniteTraining(nativeHalf))
                changed |= trainingSpeedProfileApplicationPolicy == 1
                    ? Mathf.Abs(requestedHalf - nativeHalf) > 0.00001f
                    : requestedHalf > nativeHalf + 0.00001f;
        }

        return changed;
    }

    bool LiveNativeSpeedDiffers(float raw)
    {
        if (!IsFiniteTraining(raw) || raw <= 0f) return false;
        float target = trainingSpeedProfileMode == 1
            ? trainingSpeedProfileFixedNjs
            : trainingSpeedProfileMode == 2
                ? raw * trainingSpeedProfileMultiplier
                : raw;
        if (!IsFiniteTraining(target)) return false;
        float output = trainingSpeedProfileApplicationPolicy == 0 ? Mathf.Min(raw, target) : target;
        return IsFiniteTraining(output) && Mathf.Abs(output - raw) > 0.00001f;
    }

    void ObserveNoteMotion(float songTime)
    {
        foreach (object value in ActiveTrainingNoteControllers())
        {
            NoteController controller = value as NoteController;
            NoteJump jump = GetMember(GetMember(value, "_noteMovement"), "_jump") as NoteJump;
            if (controller == null || jump == null) continue;
            VerifyNativeSpeedRegression(jump);
            VerifyNoodleSpeedRegression(jump);
            Vector3 position = ReadVector3(jump, "_localPosition");
            Transform rotated = GetMember(jump, "_rotatedObject") as Transform;
            bool finite = IsFiniteTraining(position) && rotated != null && IsFiniteTraining(rotated.localRotation);
            bool renderable = controller.GetComponentsInChildren<Renderer>(true).Any(renderer =>
                renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy && renderer.sharedMaterial != null &&
                IsFiniteTraining(renderer.bounds.center) && IsFiniteTraining(renderer.bounds.extents) && renderer.bounds.extents.sqrMagnitude > 0f);
            trainingMotionSamples++;
            if (!finite) trainingMotionFiniteViolations++;
            foreach (string category in TrainingMotionCategories(controller.noteData))
            {
                trainingMotionElements[category].ActiveFrames++;
                if (!finite) trainingMotionElements[category].FiniteViolations++;
                if (renderable) trainingMotionElements[category].RenderableFrames++;
            }
            object provider = GetMember(jump, "_variableMovementDataProvider");
            object[] policy = { provider, null };
            MethodInfo tryPolicy = AccessTools.Method(FindTrainingType("NoteMotionControl.SpeedRuntime"), "TryGetPolicy");
            bool hasPolicy = false;
            if (tryPolicy != null && provider is VariableMovementDataProvider)
                hasPolicy = (bool)tryPolicy.Invoke(null, policy);
            if (provider is VariableMovementDataProvider nativeProvider)
            {
                if (hasPolicy) trainingSpeedExpectedNativePolicy = true;
                else if (!trainingSpeedExpectedPolicyResolved)
                {
                    trainingSpeedExpectedNativePolicy = ExpectedLiveNativeSpeedPolicy(nativeProvider, false);
                    trainingSpeedExpectedPolicyResolved = true;
                }
            }
            trainingMotionPolicyObserved |= hasPolicy;
            float actualInitial = ReadFloat(provider, "_initNoteJumpMovementSpeed", float.NaN);
            if (provider?.GetType() == typeof(VariableMovementDataProvider) && IsFiniteTraining(actualInitial) && actualInitial > 0f)
                trainingSpeedNativeProviderSeen = true;
            int actualMode = hasPolicy ? ReadInt(policy[1], "Mode", -1) : -1;
            float actualFixed = hasPolicy ? ReadFloat(policy[1], "FixedNjs", float.NaN) : float.NaN;
            float actualMultiplier = hasPolicy ? ReadFloat(policy[1], "Multiplier", float.NaN) : float.NaN;
            if (hasPolicy)
            {
                trainingSpeedPolicySamples++;
                if (actualMode != trainingSpeedProfileMode || !IsFiniteTraining(actualFixed) || !IsFiniteTraining(actualMultiplier) ||
                    Mathf.Abs(actualFixed - trainingSpeedProfileFixedNjs) > 0.00001f ||
                    Mathf.Abs(actualMultiplier - trainingSpeedProfileMultiplier) > 0.00001f) trainingSpeedPolicyViolations++;
            }
            if (provider != null && trainingSpeedProviderEvidence.Count < 32 && trainingSpeedObservedProviders.Add(provider))
                trainingSpeedProviderEvidence.Add(new {
                    songTime, providerId = RuntimeHelpers.GetHashCode(provider), providerType = provider.GetType().FullName,
                    actualInitial = IsFiniteTraining(actualInitial) ? (float?)actualInitial : null,
                    actualSpeed = ReadFloat(provider, "noteJumpSpeed", float.NaN), hasPolicy,
                    policyMode = hasPolicy ? (int?)actualMode : null,
                    policyFixedNjs = hasPolicy ? (float?)actualFixed : null,
                    policyMultiplier = hasPolicy ? (float?)actualMultiplier : null
                });
            if (trainingMotionAuthoredLoaded && GetStaticMember(FindTrainingType("NoteMotionControl.SpeedRuntime"), "policies") is IDictionary policies)
                trainingMotionPolicyObserved |= policies.Count > 0;
            trainingMotionModificationObserved |= GetStaticMember(FindTrainingType("NoteMotionControl.RuntimeState"), "activeTicket") != null;
            if (trainingSpeedProfileEnabled && trainingSpeedProfileMode == 1 && !trainingMotionAuthoredLoaded && trainingSpeedProfileApplicationPolicy == 1)
            {
                trainingFixedSpeedSamples++;
                if (Mathf.Abs(ReadFloat(provider, "noteJumpSpeed", float.NaN) - trainingSpeedProfileFixedNjs) > 0.0001f) trainingFixedSpeedViolations++;
            }
            Vector3 head = ReadVector3(GetMember(jump, "_playerTransforms"), "_headPseudoLocalPos");
            if (IsFiniteTraining(head))
            {
                if (!trainingMotionHeadSeen) trainingMotionFirstHead = head;
                trainingMotionHeadSeen = true;
                trainingMotionHeadRange = Mathf.Max(trainingMotionHeadRange, Vector3.Distance(trainingMotionFirstHead, head));
            }
        }
        foreach (SliderController slider in trainingMotionSliders)
        {
            if (slider == null || !slider.gameObject.activeInHierarchy) continue;
            SpeedElement element = trainingMotionElements["Arc"];
            element.ActiveFrames++;
            if (!IsFiniteTraining(slider.transform.localPosition) || !IsFiniteTraining(slider.transform.localRotation)) element.FiniteViolations++;
        }
        foreach (ObstacleController obstacle in Resources.FindObjectsOfTypeAll<ObstacleController>())
        {
            if (obstacle == null || !obstacle.gameObject.activeInHierarchy) continue;
            trainingMotionElements["Obstacle"].ActiveFrames++;
            if (!IsFiniteTraining(obstacle.transform.localPosition) || !IsFiniteTraining(obstacle.transform.localRotation)) trainingMotionElements["Obstacle"].FiniteViolations++;
        }
    }

    void FinishNoteMotionGameplay()
    {
        TrainingMotionRouteProbe.Owner = null;
        trainingMotionRouteHarmony?.UnpatchSelf();
        trainingMotionRouteHarmony = null;
        if (manager != null)
        {
            manager.noteWasCutEvent -= OnTrainingMotionCut;
            manager.noteWasSpawnedEvent -= OnTrainingMotionNoteSpawned;
            manager.sliderWasSpawnedEvent -= OnTrainingMotionSliderSpawned;
            manager.sliderWasDespawnedEvent -= OnTrainingMotionSliderDespawned;
            manager.obstacleWasSpawnedEvent -= OnTrainingMotionObstacleSpawned;
        }
        Check("training-note-speed-finite-motion", trainingMotionSamples > 10 && trainingMotionFiniteViolations == 0,
            "samples=" + trainingMotionSamples + "; invalid=" + trainingMotionFiniteViolations);
        // OnlyReduce is intentionally allowed to be a native no-op: raising a
        // multiplier or requesting an override above the source NJS must not
        // create a ticket or attach a policy. Strict policies and specified
        // reaction time still change the native route.
        bool expectsChange = trainingSpeedExpectedNativePolicy;
        bool nativeSafe = trainingSpeedNativeProviderSeen && !trainingMotionAuthoredLoaded;
        bool realTicket = trainingNoteTicketObserved || HasTrainingSubmissionTicket("NoteMotionControl", out _);
        if (nativeSafe)
            Check("training-note-speed-native-interface-ready", trainingSpeedInterfaceReady,
                "The current native provider's supported speed interface must remain available.");
        if (nativeSafe && expectsChange)
            Check("training-note-speed-configured-policy-applied", trainingMotionPolicyObserved &&
                trainingSpeedPolicySamples > 0 && trainingSpeedPolicyViolations == 0,
                "policySamples=" + trainingSpeedPolicySamples + "; mismatch=" + trainingSpeedPolicyViolations +
                "; expected=" + expectsChange + "; mode=" + trainingSpeedProfileMode +
                "; fixed=" + trainingSpeedProfileFixedNjs + "; multiplier=" + trainingSpeedProfileMultiplier);
        if (nativeSafe && !expectsChange)
            Check("training-note-speed-native-mode-no-policy", !trainingMotionPolicyObserved,
                "policy=" + trainingMotionPolicyObserved + "; expected=" + expectsChange + "; mode=" + trainingSpeedProfileMode);
        if (!trainingMotionAuthoredLoaded)
        {
            Check("training-note-speed-independent-oracle-exercised", trainingSpeedRegressionChecked, "Native mode matrix ran=" + trainingSpeedRegressionChecked);
            Check("training-note-speed-live-native-route", trainingMotionRouteSamples > 0 && trainingMotionRouteViolations == 0,
                "samples=" + trainingMotionRouteSamples + "; violations=" + trainingMotionRouteViolations + "; position=" + trainingMotionMaxPositionError + "; rotation=" + trainingMotionMaxRotationError);
        }
        else
        {
            if (trainingMotionNoodleLoaded)
                Check("training-noodle-authored-motion-sampled", trainingNoodleSpeedChecked, "Real AnimationHelper oracle ran=" + trainingNoodleSpeedChecked);
            Check("training-note-speed-author-policy-bypassed", !trainingMotionPolicyObserved,
                "No provider speed policy was attached to the authored route");
        }
        if (trainingSpeedProfileEnabled && trainingSpeedProfileMode == 1 && !trainingMotionAuthoredLoaded && trainingSpeedProfileApplicationPolicy == 1)
            Check("training-note-speed-fixed-provider", trainingFixedSpeedSamples > 10 && trainingFixedSpeedViolations == 0,
                "NJS=" + trainingSpeedProfileFixedNjs + "; samples=" + trainingFixedSpeedSamples + "; invalid=" + trainingFixedSpeedViolations);
        foreach (var pair in trainingMotionElements)
        {
            bool required = pair.Value.ChartObjects > 0 && pair.Value.FirstTime < maximumTime - 0.1f;
            bool linked = pair.Key == "ChainHead" || pair.Key == "ChainElement" || pair.Key == "ArcHead" || pair.Key == "ArcTail";
            bool requireNative = required && linked && !trainingMotionAuthoredLoaded;
            bool requireRender = required && (pair.Key == "ChainHead" || pair.Key == "ChainElement");
            if (required || pair.Value.ActiveFrames > 0)
                Check("training-note-speed-element-" + pair.Key.ToLowerInvariant(), pair.Value.FiniteViolations == 0 && pair.Value.NativeOracleViolations == 0 &&
                    (!required || pair.Value.SpawnedObjects > 0 && pair.Value.ActiveFrames > 0) &&
                    (!requireNative || pair.Value.NativeOracleSamples > 0) && (!requireRender || pair.Value.RenderableFrames > 0),
                    "chart=" + pair.Value.ChartObjects + "; spawned=" + pair.Value.SpawnedObjects + "; frames=" + pair.Value.ActiveFrames +
                    "; rendered=" + pair.Value.RenderableFrames + "; invalid=" + pair.Value.FiniteViolations + "; nativeSamples=" + pair.Value.NativeOracleSamples +
                    "; nativeViolations=" + pair.Value.NativeOracleViolations + "; position=" + pair.Value.MaxNativePositionError + "; rotation=" + pair.Value.MaxNativeRotationError);
        }
        File.WriteAllText(Path.Combine(directory, "training-speed-motion.json"), Newtonsoft.Json.JsonConvert.SerializeObject(new {
            trainingScenario, trainingMotionSamples, trainingMotionRouteSamples, trainingMotionRouteViolations,
            profile = new { enabled = trainingSpeedProfileEnabled, mode = trainingSpeedProfileMode,
                applicationPolicy = trainingSpeedProfileApplicationPolicy, reactionMode = trainingSpeedProfileReactionMode,
                fixedNjs = trainingSpeedProfileFixedNjs, multiplier = trainingSpeedProfileMultiplier },
            nativeSafe, expectsChange, realScoreTicket = realTicket,
            trainingSpeedPolicySamples, trainingSpeedPolicyViolations,
            providerSamples = trainingSpeedProviderEvidence,
            maxPositionError = trainingMotionMaxPositionError, maxRotationError = trainingMotionMaxRotationError,
            elements = trainingMotionElements.ToDictionary(pair => pair.Key, pair => new {
                pair.Value.ChartObjects, pair.Value.SpawnedObjects, pair.Value.ActiveFrames, pair.Value.FiniteViolations,
                pair.Value.RenderableFrames, pair.Value.NativeOracleSamples, pair.Value.NativeOracleViolations,
                pair.Value.MaxNativePositionError, pair.Value.MaxNativeRotationError }),
            requestedVr = useVr, xrActive = UnityEngine.XR.XRSettings.isDeviceActive,
            headDisplacementMeters = trainingMotionHeadRange, realNoteCutEvents = trainingMotionCutEvents,
            physicalInteractionObserved = useVr && trainingMotionHeadRange > 0.01f && trainingMotionCutEvents > 0
        }, Newtonsoft.Json.Formatting.Indented));
    }

    IEnumerable<object> ActiveTrainingNoteControllers()
    {
        if (GetMember(manager, "activeNoteControllers") is IEnumerable values)
        {
            foreach (object value in values) if (value != null) yield return value;
            yield break;
        }
        foreach (NoteController value in Resources.FindObjectsOfTypeAll<NoteController>())
            if (value != null && value.gameObject.activeInHierarchy) yield return value;
    }
}
