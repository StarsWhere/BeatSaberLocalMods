using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Settings;
using LocalModUI;
using Zenject;
using HarmonyLib;

namespace NoteMotionControl
{
    internal sealed class SettingsMenu : IInitializable, IDisposable, INotifyPropertyChanged
    {
        private static readonly List<object> PresetChoices = MakeChoices(NoteMotionControl.Presets.Names);
        private static readonly List<object> ApplicationPolicyChoices = new List<object>
        {
            "Only reduce",
            "Strict",
        };
        private static readonly List<object> NjsChoices = new List<object>
        {
            "Map",
            "Override",
            "Multiplier",
        };
        private static readonly List<object> ReductionChoices = new List<object>
        {
            "Off",
            "Density",
            "Percentage",
        };
        private static readonly List<object> ReactionChoices = new List<object>
        {
            "Game",
            "Specified",
        };

        private readonly BSMLSettings bsmlSettings;
        private IDisposable uiEntry;
        private Options draft;
        private string localError = string.Empty;
        private HMUI.ViewController attachedView;

        internal SettingsMenu(BSMLSettings settings)
        {
            bsmlSettings = settings;
            ReloadDraft();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        [UIValue("presets")]
        public List<object> Presets => PresetChoices;

        [UIValue("application-policies")]
        public List<object> ApplicationPolicies => ApplicationPolicyChoices;

        [UIValue("njs-modes")]
        public List<object> NjsModes => NjsChoices;

        [UIValue("reduction-modes")]
        public List<object> ReductionModes => ReductionChoices;

        [UIValue("reaction-modes")]
        public List<object> ReactionModes => ReactionChoices;

        [UIValue("preset")]
        public string Preset
        {
            get { return NoteMotionControl.Presets.GetName(draft); }
            set
            {
                if (string.Equals(value, NoteMotionControl.Presets.Custom, StringComparison.OrdinalIgnoreCase)) return;
                Options selected;
                if (!NoteMotionControl.Presets.TryCreate(value, out selected)) return;
                draft = selected;
                Commit();
            }
        }

        [UIValue("enabled")]
        public bool Enabled
        {
            get { return draft.Enabled; }
            set { draft.Enabled = value; Commit(); }
        }

        [UIValue("application-policy")]
        public string ApplicationPolicy
        {
            get { return draft.ApplicationPolicy == NoteMotionControl.ApplicationPolicy.Strict ? "Strict" : "Only reduce"; }
            set
            {
                draft.ApplicationPolicy = string.Equals(value, "Strict", StringComparison.Ordinal)
                    ? NoteMotionControl.ApplicationPolicy.Strict
                    : NoteMotionControl.ApplicationPolicy.OnlyReduce;
                Commit();
            }
        }

        [UIValue("njs-mode")]
        public string NjsMode
        {
            get
            {
                switch (draft.NjsMode)
                {
                    case NoteMotionControl.NjsMode.Override: return "Override";
                    case NoteMotionControl.NjsMode.Multiplier: return "Multiplier";
                    default: return "Map";
                }
            }
            set
            {
                draft.NjsMode = string.Equals(value, "Override", StringComparison.Ordinal)
                    ? NoteMotionControl.NjsMode.Override
                    : string.Equals(value, "Multiplier", StringComparison.Ordinal)
                        ? NoteMotionControl.NjsMode.Multiplier
                        : NoteMotionControl.NjsMode.Map;
                Commit();
            }
        }

        [UIValue("override-njs")]
        public float OverrideNjs
        {
            get { return draft.OverrideNjs; }
            set { draft.OverrideNjs = value; Commit(); }
        }

        [UIValue("speed-multiplier")]
        public float SpeedMultiplier
        {
            get { return draft.SpeedMultiplier; }
            set { draft.SpeedMultiplier = value; Commit(); }
        }

        [UIValue("reduction-mode")]
        public string ReductionMode
        {
            get
            {
                switch (draft.ReductionMode)
                {
                    case NoteMotionControl.ReductionMode.Density: return "Density";
                    case NoteMotionControl.ReductionMode.Percentage: return "Percentage";
                    default: return "Off";
                }
            }
            set
            {
                draft.ReductionMode = string.Equals(value, "Density", StringComparison.Ordinal)
                    ? NoteMotionControl.ReductionMode.Density
                    : string.Equals(value, "Percentage", StringComparison.Ordinal)
                        ? NoteMotionControl.ReductionMode.Percentage
                        : NoteMotionControl.ReductionMode.Off;
                Commit();
            }
        }

        [UIValue("max-nps")]
        public float MaxNps
        {
            get { return draft.MaxNps; }
            set { draft.MaxNps = value; Commit(); }
        }

        [UIValue("same-hand-interval")]
        public float SameHandInterval
        {
            get { return draft.SameHandMinIntervalMs; }
            set { draft.SameHandMinIntervalMs = value; Commit(); }
        }

        [UIValue("window-seconds")]
        public float WindowSeconds
        {
            get { return draft.WindowSeconds; }
            set { draft.WindowSeconds = value; Commit(); }
        }

        [UIValue("retain-percent")]
        public float RetainPercent
        {
            get { return draft.RetainPercent; }
            set { draft.RetainPercent = value; Commit(); }
        }

        [UIValue("reaction-time-mode")]
        public string ReactionTimeMode
        {
            get { return draft.ReactionTimeMode == NoteMotionControl.ReactionTimeMode.Specified ? "Specified" : "Game"; }
            set
            {
                draft.ReactionTimeMode = string.Equals(value, "Specified", StringComparison.Ordinal)
                    ? NoteMotionControl.ReactionTimeMode.Specified
                    : NoteMotionControl.ReactionTimeMode.Game;
                Commit();
            }
        }

        [UIValue("reaction-time-ms")]
        public float ReactionTimeMs
        {
            get { return draft.ReactionTimeMs; }
            set { draft.ReactionTimeMs = value; Commit(); }
        }

        [UIValue("hide-pre-jump")]
        public bool HidePreJump
        {
            get { return draft.HidePreJump; }
            set { draft.HidePreJump = value; Commit(); }
        }

        [UIValue("vertical-jump")]
        public bool VerticalJump
        {
            get { return draft.VerticalJump; }
            set { draft.VerticalJump = value; Commit(); }
        }

        [UIValue("horizontal-swap")]
        public bool HorizontalSwap
        {
            get { return draft.HorizontalSwap; }
            set { draft.HorizontalSwap = value; Commit(); }
        }

        [UIValue("spawn-rotation")]
        public bool SpawnRotation
        {
            get { return draft.SpawnRotation; }
            set { draft.SpawnRotation = value; Commit(); }
        }

        [UIValue("rotate-towards-player")]
        public bool RotateTowardsPlayer
        {
            get { return draft.RotateTowardsPlayer; }
            set { draft.RotateTowardsPlayer = value; Commit(); }
        }

        [UIValue("show-override-njs")]
        public bool ShowOverrideNjs => draft.NjsMode == NoteMotionControl.NjsMode.Override;

        [UIValue("show-speed-multiplier")]
        public bool ShowSpeedMultiplier => draft.NjsMode == NoteMotionControl.NjsMode.Multiplier;

        [UIValue("show-reduction")]
        public bool ShowReduction => draft.ReductionMode != NoteMotionControl.ReductionMode.Off;

        [UIValue("show-density")]
        public bool ShowDensity => draft.ReductionMode == NoteMotionControl.ReductionMode.Density;

        [UIValue("show-percentage")]
        public bool ShowPercentage => draft.ReductionMode == NoteMotionControl.ReductionMode.Percentage;

        [UIValue("show-specified-reaction")]
        public bool ShowSpecifiedReaction => draft.ReactionTimeMode == NoteMotionControl.ReactionTimeMode.Specified;

        [UIValue("status")]
        public string Status
        {
            get
            {
                if (!string.IsNullOrEmpty(localError)) return localError;
                string persistent = RuntimeState.Store == null ? string.Empty : RuntimeState.Store.LastError;
                if (!string.IsNullOrEmpty(persistent)) return persistent;
                return RuntimeState.LastNotice ?? string.Empty;
            }
        }

        [UIValue("has-status")]
        public bool HasStatus => !string.IsNullOrEmpty(Status);

        [UIValue("latest-summary")]
        public string LatestSummary => ReadLatestSummary();

        [UIValue("has-latest-summary")]
        public bool HasLatestSummary => !string.IsNullOrEmpty(LatestSummary);

        public string FormatNjs(float value)
        {
            return value.ToString("0.0", CultureInfo.InvariantCulture);
        }

        public string FormatMultiplier(float value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture) + "×";
        }

        public string FormatNps(float value)
        {
            return value.ToString("0.0", CultureInfo.InvariantCulture) + " NPS";
        }

        public string FormatMilliseconds(float value)
        {
            return value.ToString("0", CultureInfo.InvariantCulture) + " ms";
        }

        public string FormatSeconds(float value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture) + " s";
        }

        public string FormatPercent(float value)
        {
            return value.ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        public void Initialize()
        {
            bool nativeAttempted = false, flowAttempted = false;
            try
            {
                uiEntry = UiRegistry.Register(new UiEntry
                {
                    Id = "training", Title = "Training settings", Category = UiCategory.Training, Order = 10,
                    ShortcutTitle = "Training settings",
                    Page = new SettingsPage
                    {
                        Host = this, Assembly = typeof(SettingsMenu).Assembly,
                        Resource = "NoteMotionControl.Settings.bsml", Refresh = Refresh,
                    },
                });
                nativeAttempted = true;
                bsmlSettings.AddSettingsMenu("Note Training Control", "NoteMotionControl.Settings.bsml", this);
                flowAttempted = true;
                InstantSettingsFlow.Register(this, error => Plugin.Log?.Warn(
                    "Settings navigation optimization is unavailable; native navigation is retained: " + error));
            }
            catch
            {
                try { if (flowAttempted) InstantSettingsFlow.Unregister(this); }
                catch (Exception error) { Plugin.Log?.Warn("Settings navigation cleanup failed: " + error); }
                try { if (nativeAttempted) bsmlSettings.RemoveSettingsMenu(this); }
                catch (Exception error) { Plugin.Log?.Warn("Settings menu cleanup failed: " + error); }
                try { uiEntry?.Dispose(); uiEntry = null; }
                catch (Exception error) { Plugin.Log?.Warn("Settings registration cleanup failed: " + error); }
                if (attachedView != null) attachedView.didActivateEvent -= OnActivated;
                attachedView = null;
                throw;
            }
        }

        public void Dispose()
        {
            uiEntry?.Dispose();
            uiEntry = null;
            InstantSettingsFlow.Unregister(this);
            if (attachedView != null) attachedView.didActivateEvent -= OnActivated;
            attachedView = null;
            bsmlSettings.RemoveSettingsMenu(this);
        }

        internal void AttachView(HMUI.ViewController view)
        {
            if (attachedView == view) return;
            if (attachedView != null) attachedView.didActivateEvent -= OnActivated;
            attachedView = view;
            if (attachedView != null) attachedView.didActivateEvent += OnActivated;
        }

        private void OnActivated(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling) => Refresh();

        [UIAction("refresh")]
        public void Refresh()
        {
            ReloadDraft();
            NotifyAll();
        }

        private void ReloadDraft()
        {
            draft = RuntimeState.Store == null ? new Options() : RuntimeState.Store.Global;
            draft.Validate();
        }

        private void Commit()
        {
            if (RuntimeState.Store == null) return;
            try
            {
                draft.Validate();
                RuntimeState.Store.SaveGlobal(draft);
                localError = string.Empty;
                NotifyAll();
            }
            catch (Exception error)
            {
                localError = "Settings could not be saved. " + error.Message;
                ReloadDraft();
                NotifyStatus();
            }
        }

        private void NotifyAll()
        {
            foreach (string name in new[]
            {
                "preset", "enabled", "application-policy", "njs-mode", "override-njs",
                "speed-multiplier", "reduction-mode", "max-nps", "same-hand-interval",
                "window-seconds", "retain-percent", "reaction-time-mode", "reaction-time-ms",
                "hide-pre-jump", "vertical-jump", "horizontal-swap", "spawn-rotation",
                "rotate-towards-player", "show-override-njs", "show-speed-multiplier",
                "show-reduction", "show-density", "show-percentage", "show-specified-reaction",
                "latest-summary", "has-latest-summary",
            }) Notify(name);
            NotifyStatus();
        }

        private void NotifyStatus()
        {
            Notify("status");
            Notify("has-status");
        }

        private void Notify(string id)
        {
            string propertyName = id;
            foreach (PropertyInfo property in typeof(SettingsMenu).GetProperties(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                UIValue binding = Attribute.GetCustomAttribute(property, typeof(UIValue)) as UIValue;
                if (binding != null && binding.Id == id)
                {
                    propertyName = property.Name;
                    break;
                }
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private static List<object> MakeChoices(IReadOnlyList<string> values)
        {
            var result = new List<object>(values.Count);
            foreach (string value in values) result.Add(value);
            return result;
        }

        private static string ReadLatestSummary()
        {
            return TrainingLoad.LastSummary;
        }
    }

    [HarmonyPatch]
    internal static class SettingsViewBindingPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(
            AccessTools.TypeByName("BeatSaberMarkupLanguage.Settings.SettingsMenu"), "Setup");

        private static void Postfix(object __instance)
        {
            if (AccessTools.Property(__instance.GetType(), "Host").GetValue(__instance, null) is SettingsMenu host)
                host.AttachView(AccessTools.Property(__instance.GetType(), "ViewController").GetValue(__instance, null) as HMUI.ViewController);
        }
    }
}
