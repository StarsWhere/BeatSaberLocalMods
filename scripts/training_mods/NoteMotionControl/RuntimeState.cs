using System;
using System.Collections.Generic;
using SiraUtil.Submissions;
using Zenject;
using GameLogger = IPA.Logging.Logger;

namespace NoteMotionControl
{
    internal static class RuntimeState
    {
        internal const string PluginId = "NoteMotionControl";

        private static GameLogger logger;
        private static Submission activeSubmission;
        private static Ticket activeTicket;
        private static object activeOwner;
        private static DiContainer activeContainer;
        private static readonly HashSet<string> warnings = new HashSet<string>(StringComparer.Ordinal);

        internal static SettingsStore Store { get; private set; }
        internal static Options ActiveOptions { get; private set; } = NewDefaults();
        internal static GameplayCoreSceneSetupData ActiveSetup { get; private set; }
        internal static string ActiveChartKey { get; private set; } = string.Empty;
        internal static bool GameplayActive { get; private set; }
        internal static bool MovementInterfaceReady { get; private set; }
        internal static bool PreserveAuthoredMotion { get; private set; }
        internal static string LastNotice { get; private set; } = string.Empty;

        internal static void Initialize(GameLogger pluginLogger, string gameRoot = null)
        {
            logger = pluginLogger;
            Store = new SettingsStore(gameRoot, ReportProfileError);
            ActiveOptions = Store.Global;
            ActiveOptions.Validate();
        }

        internal static bool Begin(
            GameplayCoreSceneSetupData setup,
            Submission submission,
            object owner,
            DiContainer container)
        {
            End();
            if (setup == null || Store == null || owner == null || container == null) return false;

            string key;
            try
            {
                key = setup.beatmapKey.SerializedName();
            }
            catch (Exception error)
            {
                key = string.Empty;
                logger?.Warn("Note Training Control could not serialize the active chart key: " + error.Message);
            }

            Options resolved = TrainingLoad.GetOptions(setup);
            resolved.Validate();
            ActiveOptions = resolved;
            ActiveSetup = setup;
            activeOwner = owner;
            activeContainer = container;
            ActiveChartKey = key ?? string.Empty;
            activeSubmission = submission;
            GameplayActive = resolved.Enabled;
            MovementInterfaceReady = true;
            warnings.Clear();
            LastNotice = string.Empty;
            PreserveAuthoredMotion = HasAuthoredMotion(setup);
            if (GameplayActive && PreserveAuthoredMotion)
            {
                if (string.IsNullOrEmpty(LastNotice))
                    LastNotice = "Author-controlled note motion was preserved; movement controls were not applied to this chart.";
                logger?.Warn(LastNotice);
            }
            if (!SpeedRuntime.ValidateInterface(out string movementError))
            {
                MovementInterfaceReady = false;
                SetNotice(movementError);
                WarnOnce("native-interface", movementError);
            }

            if (!MotionRuntime.ValidateInterface(out string animationError))
            {
                MovementInterfaceReady = false;
                SetNotice(animationError);
                WarnOnce("animation-interface", animationError);
            }
            if (TrainingLoad.ReductionApplied(setup) &&
                !EnsureMotionTicket("A reduced chart was played; score submission is disabled for this play."))
                throw new InvalidOperationException("The reduced chart cannot start without the score submission gate.");

            return true;
        }

        internal static void End()
        {
            // Submission belongs to the gameplay container. Its Dispose reads
            // the tickets for the results screen, so leave this play's ticket
            // there until SiraUtil has captured it. The next play resolves a
            // fresh Submission instance.
            activeTicket = null;
            activeSubmission = null;
            activeOwner = null;
            activeContainer = null;
            SpeedRuntime.Clear();
            MotionRuntime.Clear();
            GameplayActive = false;
            ActiveSetup = null;
            ActiveChartKey = string.Empty;
            ActiveOptions = Store == null ? NewDefaults() : Store.Global;
            MovementInterfaceReady = false;
            warnings.Clear();
            PreserveAuthoredMotion = false;
        }

        internal static void End(
            object owner,
            GameplayCoreSceneSetupData setup,
            DiContainer container)
        {
            if (owner == null || setup == null || container == null) return;
            if (!ReferenceEquals(activeOwner, owner) ||
                !ReferenceEquals(ActiveSetup, setup) ||
                !ReferenceEquals(activeContainer, container))
                return;

            End();
        }

        internal static bool EnsureMotionTicket(string reason)
        {
            if (activeTicket != null) return true;
            if (activeSubmission == null)
            {
                ErrorOnce(
                    "submission-missing",
                    "Note Training Control detected a gameplay change but SiraUtil score submission is unavailable; the change is skipped.");
                return false;
            }

            try
            {
                Ticket ticket = activeSubmission.DisableScoreSubmission(PluginId, reason);
                if (ticket == null)
                {
                    ErrorOnce(
                        "submission-ticket",
                        "Note Training Control detected a gameplay change but SiraUtil did not return a score ticket; the change is skipped.");
                    return false;
                }

                activeTicket = ticket;
                return true;
            }
            catch (Exception error)
            {
                ErrorOnce(
                    "submission-failure",
                    "Note Training Control could not disable score submission; the detected gameplay change is skipped.",
                    error);
                return false;
            }
        }

        internal static bool OwnsProvider(VariableMovementDataProvider provider)
        {
            if (activeContainer == null) return false;
            try { return ReferenceEquals(activeContainer.TryResolve<IVariableMovementDataProvider>(), provider); }
            catch (Exception error)
            {
                const string message = "The local movement provider could not be identified; original speed was retained.";
                WarnOnce("speed-owner", message + " " + error.Message);
                SetNotice(message);
                return false;
            }
        }

        internal static bool HasSafeSpeedEvents(float multiplier)
        {
            if (ActiveSetup?.transformedBeatmapData == null) return true;
            foreach (BeatmapDataItem item in ActiveSetup.transformedBeatmapData.allBeatmapDataItems)
                if (item is NoteJumpSpeedEventData speed &&
                    (!SpeedPolicy.Finite(speed.relativeNoteJumpSpeed) || !SpeedPolicy.Finite(speed.relativeNoteJumpSpeed * multiplier)))
                {
                    const string message = "The native speed route could not be safely adjusted; original speed was retained.";
                    WarnOnce("speed-route", message);
                    SetNotice(message);
                    return false;
                }
            return true;
        }

        internal static void Warn(string message)
        {
            logger?.Warn(message);
        }

        internal static void SetNotice(string message)
        {
            LastNotice = message ?? string.Empty;
        }

        internal static void WarnOnce(string key, string message)
        {
            if (string.IsNullOrEmpty(key))
            {
                Warn(message);
                return;
            }

            if (warnings.Add(key)) Warn(message);
        }

        internal static void Error(string message, Exception error = null)
        {
            if (error == null) logger?.Error(message);
            else logger?.Error(message + " " + error.Message);
        }

        internal static void ErrorOnce(string key, string message, Exception error = null)
        {
            if (string.IsNullOrEmpty(key) || warnings.Add(key)) Error(message, error);
        }

        private static bool HasAuthoredMotion(GameplayCoreSceneSetupData setup)
        {
            if (setup.beatmapLevel == null || !setup.beatmapLevel.levelID.StartsWith("custom_level_", StringComparison.Ordinal)) return false;
            try
            {
                Type collections = Type.GetType("SongCore.Collections, SongCore", false);
                System.Reflection.MethodInfo method = collections?.GetMethod("GetCustomLevelSongDifficultyData", new[] { typeof(BeatmapKey) });
                if (method == null) throw new MissingMethodException("SongCore difficulty metadata is unavailable.");
                object difficulty = method?.Invoke(null, new object[] { setup.beatmapKey });
                if (difficulty == null) throw new InvalidOperationException("SongCore has no difficulty metadata for the active custom chart.");
                object additional = ReadMember(difficulty, "additionalDifficultyData");
                if (additional == null) throw new MissingMemberException("SongCore difficulty motion metadata is unavailable.");
                foreach (string key in new[] { "_requirements", "_suggestions" })
                {
                    if (!(ReadMember(additional, key) is string[] requirements))
                        throw new MissingMemberException("SongCore difficulty requirement metadata is unavailable.");
                    foreach (string requirement in requirements)
                        if (string.Equals(requirement, "Noodle Extensions", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(requirement, "Vivify", StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }
            catch (Exception error)
            {
                SetNotice("Chart motion metadata could not be inspected; original speed was retained.");
                logger?.Warn(LastNotice + " " + error.Message);
                return true;
            }
        }

        private static object ReadMember(object instance, string name)
        {
            if (instance == null) return null;
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            Type type = instance.GetType();
            return type.GetField(name, flags)?.GetValue(instance) ?? type.GetProperty(name, flags)?.GetValue(instance, null);
        }

        private static Options NewDefaults()
        {
            Options options = new Options();
            options.Validate();
            return options;
        }

        private static void ReportProfileError(string message, Exception error)
        {
            Error(message, error);
        }
    }
}
