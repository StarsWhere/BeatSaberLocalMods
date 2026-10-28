using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using NoteMotionControl.Reduction;

namespace NoteMotionControl
{
    /// <summary>
    /// Owns the reducer's chart-load scope.  A load captures the global
    /// profile, chart identity, practice speed, and generation once.  Every
    /// later transform callback uses that immutable capture, so changing the
    /// settings while a native async load is in flight cannot change the
    /// result or relabel an older callback as the current generation.
    /// </summary>
    internal static class TrainingLoad
    {
        private sealed class AllowedSetupMarker { }

        private sealed class LoadContext
        {
            internal LoadContext(
                GameplayCoreSceneSetupData setup,
                string chartKey,
                long generation,
                float playbackSpeed,
                Options options,
                OptionsSnapshot snapshot)
            {
                Setup = setup;
                ChartKey = chartKey;
                Session = new TransformSession(chartKey, generation, playbackSpeed);
                PlaybackSpeed = playbackSpeed;
                Options = options;
                Snapshot = snapshot;
            }

            internal GameplayCoreSceneSetupData Setup { get; }
            internal string ChartKey { get; }
            internal TransformSession Session { get; }
            internal float PlaybackSpeed { get; }
            internal Options Options { get; }
            internal OptionsSnapshot Snapshot { get; }
            internal ReductionOutcome Outcome { get; set; }
        }

        private static readonly object Gate = new object();
        private static AsyncLocal<LoadContext> FlowContext = new AsyncLocal<LoadContext>();
        private static ConditionalWeakTable<GameplayCoreSceneSetupData, AllowedSetupMarker> AllowedSetups =
            new ConditionalWeakTable<GameplayCoreSceneSetupData, AllowedSetupMarker>();
        private static LoadContext current;
        private static long nextGeneration;
        private static string lastSummary = string.Empty;

        /// <summary>
        /// Summary of the last completed transform that matched its load
        /// generation.  Failed and stale callbacks never replace it.
        /// </summary>
        internal static string LastSummary
        {
            get { lock (Gate) return lastSummary; }
        }

        /// <summary>
        /// Copy of the last outcome for the merged runtime smoke harness and
        /// settings status.  The copy prevents diagnostics from mutating the
        /// live load context.
        /// </summary>
        internal static ReductionOutcome LastOutcome
        {
            get
            {
                lock (Gate) return current?.Outcome?.Copy();
            }
        }

        internal static Options GetOptions(GameplayCoreSceneSetupData setup)
        {
            lock (Gate)
            {
                if (current != null && ReferenceEquals(current.Setup, setup))
                    return current.Options.Copy();
            }

            return ReadGlobalOptions();
        }

        internal static bool ReductionApplied(GameplayCoreSceneSetupData setup)
        {
            lock (Gate)
            {
                return current != null &&
                       ReferenceEquals(current.Setup, setup) &&
                       current.Outcome != null &&
                       current.Outcome.EffectiveReduction;
            }
        }

        internal static void End(GameplayCoreSceneSetupData setup)
        {
            if (setup == null) return;
            LoadContext ended = null;
            lock (Gate)
            {
                if (current != null && ReferenceEquals(current.Setup, setup))
                {
                    ended = current;
                    current = null;
                }
            }

            if (ended != null && ReferenceEquals(FlowContext.Value, ended))
                FlowContext.Value = null;
        }

        internal static void Shutdown()
        {
            lock (Gate)
            {
                current = null;
                lastSummary = string.Empty;
                AllowedSetups = new ConditionalWeakTable<GameplayCoreSceneSetupData, AllowedSetupMarker>();
                Interlocked.Increment(ref nextGeneration);
            }

            FlowContext.Value = null;
        }

        internal static void RegisterAllowedSetup(GameplayCoreSceneSetupData setup)
        {
            if (setup == null) return;
            lock (Gate)
            {
                AllowedSetups.Remove(setup);
                AllowedSetups.Add(setup, new AllowedSetupMarker());
            }
        }

        internal static void RegisterRejectedSetup(GameplayCoreSceneSetupData setup)
        {
            if (setup == null) return;
            LoadContext rejected = null;
            lock (Gate)
            {
                AllowedSetups.Remove(setup);
                if (current != null && ReferenceEquals(current.Setup, setup))
                {
                    rejected = current;
                    current = null;
                }
            }

            if (rejected != null && ReferenceEquals(FlowContext.Value, rejected))
                FlowContext.Value = null;
        }

        internal static bool IsAllowedSetup(GameplayCoreSceneSetupData setup)
        {
            if (setup == null) return false;
            lock (Gate) return AllowedSetups.TryGetValue(setup, out _);
        }

        internal static void Capture(GameplayCoreSceneSetupData setup)
        {
            if (setup == null) return;
            if (!IsAllowedSetup(setup))
            {
                // Multiplayer and editor loads must not inherit a previous
                // single-player context, including for the same chart key.
                ClearCurrent();
                return;
            }

            string chartKey;
            try
            {
                chartKey = setup.beatmapKey.SerializedName();
            }
            catch (Exception error)
            {
                ClearCurrent();
                throw new ReductionTransformException(
                    "density.configuration_invalid",
                    "The active chart key could not be serialized; chart loading was stopped.",
                    error);
            }
            float playbackSpeed = setup.practiceSettings != null
                ? setup.practiceSettings.songSpeedMul
                : setup.gameplayModifiers.songSpeedMul;
            if (float.IsNaN(playbackSpeed) || float.IsInfinity(playbackSpeed) || playbackSpeed <= 0)
                playbackSpeed = 1f;

            Options options;
            OptionsSnapshot snapshot;
            try
            {
                options = ReadGlobalOptions();
                snapshot = new OptionsSnapshot(options);
            }
            catch (Exception error)
            {
                ClearCurrent();
                throw new ReductionTransformException(
                    "density.configuration_invalid",
                    "NoteMotionControl reduction settings are invalid; chart loading was stopped.",
                    error);
            }
            var captured = new LoadContext(
                setup,
                chartKey,
                Interlocked.Increment(ref nextGeneration),
                playbackSpeed,
                options,
                snapshot);

            lock (Gate)
            {
                current = captured;
                // Keep the last completed summary available while the new
                // chart is loading.  It is replaced only by this generation's
                // completed transform.
            }

            FlowContext.Value = captured;
        }

        internal static bool ShouldBypassCache(BeatmapKey key)
        {
            if (!TryResolve(key, out LoadContext context)) return false;
            return context.Snapshot.Enabled;
        }

        internal static bool TryGetContext(BeatmapKey key, out TransformSession session)
        {
            if (TryResolve(key, out LoadContext context))
            {
                session = context.Session;
                return true;
            }

            session = default(TransformSession);
            return false;
        }

        internal static bool TryGetContext(
            BeatmapKey key,
            out GameplayCoreSceneSetupData setup,
            out OptionsSnapshot options,
            out TransformSession session)
        {
            if (TryResolve(key, out LoadContext context))
            {
                setup = context.Setup;
                options = context.Snapshot;
                session = context.Session;
                return true;
            }

            setup = null;
            options = null;
            session = default(TransformSession);
            return false;
        }

        internal static bool IsCurrent(TransformSession session)
        {
            lock (Gate)
            {
                return current != null && TransformPolicy.IsCurrent(session, current.Session);
            }
        }

        internal static void Record(TransformSession session, ReductionOutcome outcome)
        {
            if (outcome == null) throw new ArgumentNullException(nameof(outcome));
            lock (Gate)
            {
                if (current == null || !TransformPolicy.IsCurrent(session, current.Session)) return;
                outcome.ChartKey = current.ChartKey;
                outcome.Generation = current.Session.Generation;
                outcome.PlaybackSpeed = current.PlaybackSpeed;
                current.Outcome = outcome;
                lastSummary = FormatSummary(outcome);
            }
        }

        internal static void RecordFailure(TransformSession session, string category, string reason)
        {
            lock (Gate)
            {
                if (current == null || !TransformPolicy.IsCurrent(session, current.Session)) return;
                current.Outcome = new ReductionOutcome
                {
                    ChartKey = current.ChartKey,
                    Generation = current.Session.Generation,
                    PlaybackSpeed = current.PlaybackSpeed,
                    Applied = current.Snapshot.Enabled,
                    FailClosed = true,
                    FailureCategory = category,
                    Reason = reason,
                    ReductionMode = current.Snapshot.ReductionMode,
                };
            }
        }

        /// <summary>
        /// Completes a native, untransformed load when the loader returned a
        /// cached result and therefore never invoked the transform callback.
        /// This is only valid for an effectively disabled snapshot.  The
        /// wrapper awaits the original task so diagnostics are committed before
        /// the caller resumes, while faults and cancellation retain the native
        /// task's failure semantics.  Enabled reducer loads keep the original
        /// task identity so a missing transform remains a real diagnostic.
        /// </summary>
        internal static Task<IReadonlyBeatmapData> ObserveNativeLoad(
            BeatmapKey key,
            Task<IReadonlyBeatmapData> result)
        {
            if (result == null ||
                !TryGetContext(key, out _, out OptionsSnapshot options, out TransformSession session) ||
                options.Enabled)
                return result;

            return ObserveNativeLoadAsync(session, result);
        }

        private static async Task<IReadonlyBeatmapData> ObserveNativeLoadAsync(
            TransformSession session,
            Task<IReadonlyBeatmapData> result)
        {
            IReadonlyBeatmapData data = await result.ConfigureAwait(false);
            RecordNativeNoReduction(session);
            return data;
        }

        private static void RecordNativeNoReduction(TransformSession session)
        {
            lock (Gate)
            {
                if (current == null || !TransformPolicy.IsCurrent(session, current.Session) ||
                    current.Outcome != null)
                    return;

                ReductionOutcome outcome = ReductionOutcome.Disabled(current.ChartKey, current.Options);
                outcome.Generation = current.Session.Generation;
                outcome.PlaybackSpeed = current.PlaybackSpeed;
                current.Outcome = outcome;
                lastSummary = FormatSummary(outcome);
            }
        }

        internal static bool TryGetOutcome(TransformSession session, out ReductionOutcome outcome)
        {
            lock (Gate)
            {
                if (current != null && TransformPolicy.IsCurrent(session, current.Session) && current.Outcome != null)
                {
                    outcome = current.Outcome.Copy();
                    return true;
                }
            }

            outcome = null;
            return false;
        }

        internal static void Clear(TransformSession session)
        {
            LoadContext removed = null;
            lock (Gate)
            {
                if (current != null && TransformPolicy.IsCurrent(session, current.Session))
                {
                    removed = current;
                    current = null;
                }
            }

            if (removed != null && ReferenceEquals(FlowContext.Value, removed))
                FlowContext.Value = null;
        }

        private static bool TryResolve(BeatmapKey key, out LoadContext context)
        {
            string chartKey;
            try { chartKey = key.SerializedName(); }
            catch
            {
                context = null;
                return false;
            }

            LoadContext flowed = FlowContext.Value;
            LoadContext candidate;
            lock (Gate) candidate = current;

            if (flowed != null)
            {
                // A stale flowed callback is rejected even when its chart key
                // equals the current generation.  Falling back here would
                // apply an older transform to a newer same-song load.
                if (candidate != null && ReferenceEquals(flowed, candidate) &&
                    string.Equals(flowed.ChartKey, chartKey, StringComparison.Ordinal))
                {
                    context = flowed;
                    return true;
                }

                context = null;
                return false;
            }

            if (candidate != null && string.Equals(candidate.ChartKey, chartKey, StringComparison.Ordinal))
            {
                context = candidate;
                return true;
            }

            context = null;
            return false;
        }

        private static Options ReadGlobalOptions()
        {
            Options options = RuntimeState.Store == null
                ? new Options()
                : RuntimeState.Store.Global;
            options.Validate();
            return options.Copy();
        }

        private static string FormatSummary(ReductionOutcome outcome)
        {
            if (outcome == null) return string.Empty;
            if (outcome.FailClosed || !outcome.Applied || outcome.OriginalScoringObjects <= 0 ||
                (outcome.OriginalMaxScore <= 0 && outcome.FilteredMaxScore <= 0))
                return string.Empty;
            double retainedPercent = outcome.OriginalScoringObjects <= 0
                ? 0
                : outcome.FilteredScoringObjects * 100.0 / outcome.OriginalScoringObjects;
            string summary = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "Retained {0}/{1} · {2:0}%\nPeak density {3:0.0} → {4:0.0} NPS\nMax score {5} → {6}",
                outcome.FilteredScoringObjects,
                outcome.OriginalScoringObjects,
                retainedPercent,
                outcome.OriginalPeakNps,
                outcome.FilteredPeakNps,
                outcome.OriginalMaxScore,
                outcome.FilteredMaxScore);
            if (outcome.UsedFallback) summary += "\nMinimum valid group retained.";
            return summary;
        }

        private static void ClearCurrent()
        {
            LoadContext removed;
            lock (Gate)
            {
                removed = current;
                current = null;
            }

            if (removed != null && ReferenceEquals(FlowContext.Value, removed))
                FlowContext.Value = null;
        }
    }

    [HarmonyPatch]
    internal static class ReductionGameplaySetupScopePatch
    {
        private static readonly PropertyInfo GameplaySetupProperty =
            typeof(LevelScenesTransitionSetupData).GetProperty(
                "gameplayCoreSceneSetupData",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
            throw new MissingMemberException(
                typeof(LevelScenesTransitionSetupData).FullName,
                "gameplayCoreSceneSetupData");

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var types = new List<Type>
            {
                typeof(StandardLevelScenesTransitionSetupData),
                typeof(MissionLevelScenesTransitionSetupData),
                typeof(MultiplayerLevelScenesTransitionSetupData),
            };
            Type editorType = AccessTools.TypeByName(
                "BeatmapEditor3D.BeatmapEditorStandardLevelScenesTransitionSetupData");
            if (editorType != null) types.Add(editorType);

            foreach (Type type in types)
            {
                bool found = false;
                foreach (MethodInfo method in type.GetMethods(
                             BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                             BindingFlags.DeclaredOnly))
                {
                    if (method.Name != "Init") continue;
                    found = true;
                    yield return method;
                }

                if (!found) throw new MissingMethodException(type.FullName, "Init");
            }
        }

        private static void Postfix(LevelScenesTransitionSetupData __instance)
        {
            GameplayCoreSceneSetupData setup =
                GameplaySetupProperty.GetValue(__instance, null) as GameplayCoreSceneSetupData;
            if (setup == null)
            {
                Plugin.Log?.Warn("NoteMotionControl could not identify the gameplay setup scope; chart reduction is disabled for this load.");
                return;
            }

            Type transitionType = __instance.GetType();
            bool allowed = transitionType == typeof(StandardLevelScenesTransitionSetupData) ||
                           transitionType == typeof(MissionLevelScenesTransitionSetupData);
            if (allowed) TrainingLoad.RegisterAllowedSetup(setup);
            else TrainingLoad.RegisterRejectedSetup(setup);
        }
    }

    [HarmonyPatch]
    internal static class ReductionCaptureGameplaySpeedPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                       typeof(GameplayCoreSceneSetupData),
                       nameof(GameplayCoreSceneSetupData.LoadTransformedBeatmapDataAsync)) ??
                   throw new MissingMethodException(
                       typeof(GameplayCoreSceneSetupData).FullName,
                       nameof(GameplayCoreSceneSetupData.LoadTransformedBeatmapDataAsync));
        }

        private static void Prefix(GameplayCoreSceneSetupData __instance)
        {
            TrainingLoad.Capture(__instance);
        }
    }

    [HarmonyPatch]
    internal static class ReductionTransformedCachePatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                       typeof(BeatmapDataLoader),
                       nameof(BeatmapDataLoader.CreateOrGetTransformedBeatmapDataAsync),
                       new[]
                       {
                           typeof(BeatmapKey),
                           typeof(BeatmapLevel),
                           typeof(GameplayModifiers),
                           typeof(PlayerSpecificSettings),
                           typeof(EnvironmentInfoSO),
                           typeof(EnvironmentInfoSO),
                           typeof(bool),
                           typeof(bool),
                           typeof(IBeatmapLevelData),
                           typeof(BeatmapLevelDataVersion?),
                       }) ??
                   throw new MissingMethodException(
                       typeof(BeatmapDataLoader).FullName,
                       nameof(BeatmapDataLoader.CreateOrGetTransformedBeatmapDataAsync));
        }

        private static void Prefix(BeatmapKey beatmapKey, ref bool useCache)
        {
            if (TrainingLoad.ShouldBypassCache(beatmapKey)) useCache = false;
        }

        private static void Postfix(BeatmapKey beatmapKey, ref Task<IReadonlyBeatmapData> __result)
        {
            __result = TrainingLoad.ObserveNativeLoad(beatmapKey, __result);
        }
    }

    [HarmonyPatch]
    internal static class ReductionTransformPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                       typeof(BeatmapDataTransformHelper),
                       nameof(BeatmapDataTransformHelper.CreateTransformedBeatmapData),
                       new[]
                       {
                           typeof(IReadonlyBeatmapData),
                           typeof(BeatmapKey),
                           typeof(BeatmapLevel),
                           typeof(GameplayModifiers),
                           typeof(PlayerSpecificSettings),
                           typeof(EnvironmentInfoSO),
                           typeof(bool),
                       }) ??
                   throw new MissingMethodException(
                       typeof(BeatmapDataTransformHelper).FullName,
                       nameof(BeatmapDataTransformHelper.CreateTransformedBeatmapData));
        }

        private static void Postfix(ref IReadonlyBeatmapData __result, BeatmapKey __1)
        {
            if (__result == null) return;
            if (!TrainingLoad.TryGetContext(
                    __1,
                    out _,
                    out OptionsSnapshot options,
                    out TransformSession session) ||
                !TrainingLoad.IsCurrent(session))
                return;

            string chartKey = session.ChartKey;
            try
            {
                ReductionApplication application = BeatmapReducer.Apply(
                    __result,
                    options,
                    chartKey,
                    session.PlaybackSpeed);
                __result = application.Data;
                TrainingLoad.Record(session, application.Outcome);
                if (application.Outcome.EffectiveReduction)
                {
                    Plugin.Log?.Info(
                        "Reduced " + chartKey + ": " +
                        application.Outcome.FilteredScoringObjects + "/" +
                        application.Outcome.OriginalScoringObjects +
                        " scoring objects; peak density " +
                        application.Outcome.FilteredPeakNps.ToString(
                            "0.##", System.Globalization.CultureInfo.InvariantCulture) + " NPS.");
                }
            }
            catch (ReductionTransformException error)
            {
                TrainingLoad.RecordFailure(session, error.Category, error.Message);
                TrainingLoad.Clear(session);
                Plugin.Log?.Error("NoteMotionControl reduction failed for " + chartKey + ": " + error);
                throw;
            }
            catch (Exception error)
            {
                const string category = "density.configuration_invalid";
                TrainingLoad.RecordFailure(session, category, "Configuration or transformation failure; play was stopped.");
                TrainingLoad.Clear(session);
                Plugin.Log?.Error("NoteMotionControl reduction failed for " + chartKey + ": " + error);
                throw new ReductionTransformException(
                    category,
                    "NoteMotionControl configuration or transformation failed; play was stopped.",
                    error);
            }
        }
    }
}
