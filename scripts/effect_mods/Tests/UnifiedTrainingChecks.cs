using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EffectModsSmoke;

public sealed partial class Runner
{
    void CaptureUnifiedTrainingGameplay(Zenject.SceneContext context, IReadonlyBeatmapData beatmap)
    {
        trainingUnifiedCaptured = true;
        trainingUnifiedBeatmap = beatmap;
        trainingUnifiedBeatmapType = beatmap?.GetType();
        Type loadType = FindTrainingType("NoteMotionControl.TrainingLoad");
        trainingUnifiedOutcome = GetStaticMember(loadType, "LastOutcome");
        if (trainingUnifiedOutcome == null)
        {
            Check("training-unified-runtime-outcome", false,
                "NoteMotionControl.TrainingLoad.LastOutcome is unavailable after chart loading.");
            return;
        }

        bool failClosed = ReadBool(trainingUnifiedOutcome, "FailClosed", false);
        bool applied = ReadBool(trainingUnifiedOutcome, "Applied", false);
        bool effective = ReadBool(trainingUnifiedOutcome, "EffectiveReduction", false);
        string chartKey = Convert.ToString(GetMember(trainingUnifiedOutcome, "ChartKey"), CultureInfo.InvariantCulture) ?? string.Empty;
        trainingUnifiedChartKey = chartKey;
        Check("training-unified-runtime-result", !failClosed && !string.IsNullOrEmpty(chartKey),
            "chart=" + chartKey + "; applied=" + applied + "; effective=" + effective +
            "; reason=" + GetMember(trainingUnifiedOutcome, "Reason"));
        long generation = ReadLong(GetMember(trainingUnifiedOutcome, "Generation"), 0);
        Check("training-unified-generation", generation > 0,
            "generation=" + generation);

        Type runtimeType = FindTrainingType("NoteMotionControl.RuntimeState");
        object setup = GetStaticMember(runtimeType, "ActiveSetup");
        if (setup != null)
        {
            object beatmapKey = GetMember(setup, "beatmapKey");
            MethodInfo serializedName = beatmapKey?.GetType().GetMethod("SerializedName", Type.EmptyTypes);
            string expectedKey = serializedName == null
                ? Convert.ToString(beatmapKey, CultureInfo.InvariantCulture) ?? string.Empty
                : Convert.ToString(serializedName.Invoke(beatmapKey, null), CultureInfo.InvariantCulture) ?? string.Empty;
            Check("training-unified-chart-scope", string.IsNullOrEmpty(expectedKey) || expectedKey == chartKey,
                "setup=" + expectedKey + "; outcome=" + chartKey);

            Type load = FindTrainingType("NoteMotionControl.TrainingLoad");
            MethodInfo getOptions = load?.GetMethod("GetOptions", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            object capturedOptions = getOptions?.Invoke(null, new[] { setup });
            object activeOptions = GetStaticMember(runtimeType, "ActiveOptions");
            Check("training-unified-options-snapshot-shared", SameTrainingOptions(activeOptions, capturedOptions),
                "RuntimeState.ActiveOptions and TrainingLoad.GetOptions(setup) differ.");
        }

        if (!applied)
        {
            Check("training-unified-disabled-summary-empty", string.IsNullOrEmpty(ReadTrainingSummary()),
                ReadTrainingSummary());
            return;
        }

        int original = ReadInt(trainingUnifiedOutcome, "OriginalScoringObjects", -1);
        int filtered = ReadInt(trainingUnifiedOutcome, "FilteredScoringObjects", -1);
        trainingUnifiedReportedFiltered = filtered;
        trainingUnifiedScoringCount = CountTrainingScoringObjects(beatmap);
        Check("training-unified-scoring-count", trainingUnifiedScoringCount >= 0 && filtered == trainingUnifiedScoringCount,
            "outcome=" + filtered + "; beatmap=" + trainingUnifiedScoringCount + "; original=" + original);
        Check("training-unified-scoring-order", original >= filtered && original >= 0,
            "original=" + original + "; filtered=" + filtered);

        int reportedMaxScore = ReadInt(trainingUnifiedOutcome, "FilteredMaxScore", -1);
        bool computed = TryComputeTrainingMaxScore(beatmap, out int computedMaxScore, out string scoreDetail);
        trainingUnifiedMaxScore = computedMaxScore;
        Check("training-unified-maxscore-consistent", computed && computedMaxScore == reportedMaxScore,
            "reported=" + reportedMaxScore + "; computed=" + computedMaxScore + "; " + scoreDetail);

        double originalPeak = ReadDouble(trainingUnifiedOutcome, "OriginalPeakNps", -1);
        double filteredPeak = ReadDouble(trainingUnifiedOutcome, "FilteredPeakNps", -1);
        Check("training-unified-peak-density", IsFiniteTraining(originalPeak) && IsFiniteTraining(filteredPeak) &&
            originalPeak >= 0 && filteredPeak >= 0,
            "original=" + originalPeak + "; filtered=" + filteredPeak);
        bool summaryExpected = original > 0 && (ReadInt(trainingUnifiedOutcome, "OriginalMaxScore", 0) > 0 ||
            ReadInt(trainingUnifiedOutcome, "FilteredMaxScore", 0) > 0);
        Check("training-unified-summary", summaryExpected == !string.IsNullOrEmpty(ReadTrainingSummary()),
            "expected=" + summaryExpected + "; summary=" + ReadTrainingSummary());
        Check("training-unified-beatmap-type", CustomBeatmapExpected == (trainingUnifiedBeatmapType?.Name == "CustomBeatmapData"),
            "expected=" + (CustomBeatmapExpected ? "CustomBeatmapData" : "BeatmapData") +
            "; actual=" + trainingUnifiedBeatmapType?.FullName);
        VerifyUnifiedBeatmapStructure(beatmap);
    }

    void FinishUnifiedTrainingGameplay()
    {
        if (!trainingUnifiedCaptured) return;
        Type load = FindTrainingType("NoteMotionControl.TrainingLoad");
        object current = GetStaticMember(load, "LastOutcome");
        if (current == null)
        {
            Check("training-unified-result-stable", !SceneManager.GetSceneByName("GameCore").isLoaded,
                "LastOutcome was cleared before gameplay finished.");
            return;
        }
        string chartKey = Convert.ToString(GetMember(current, "ChartKey"), CultureInfo.InvariantCulture) ?? string.Empty;
        bool sameChart = string.Equals(chartKey, trainingUnifiedChartKey, StringComparison.Ordinal);
        bool sameCount = trainingUnifiedReportedFiltered < 0 ||
            ReadInt(current, "FilteredScoringObjects", -1) == trainingUnifiedReportedFiltered;
        Check("training-unified-result-stable", sameChart && sameCount,
            "captured=" + trainingUnifiedChartKey + "; current=" + chartKey + "; filtered=" +
            ReadInt(current, "FilteredScoringObjects", -1));
    }

    void VerifyUnifiedBeatmapStructure(IReadonlyBeatmapData beatmap)
    {
        if (beatmap == null) return;
        int notes = 0, arcs = 0, chains = 0, bombs = 0, obstacles = 0;
        int invalid = 0;
        foreach (BeatmapDataItem item in beatmap.allBeatmapDataItems)
        {
            if (item is NoteData note)
            {
                notes++;
                if (!IsFiniteTraining(note.time)) invalid++;
                if (note.gameplayType == NoteData.GameplayType.Bomb) bombs++;
                if (note.gameplayType == NoteData.GameplayType.BurstSliderHead ||
                    note.gameplayType == NoteData.GameplayType.BurstSliderElement) chains++;
                if (note.isArcHead || note.isArcTail) arcs++;
            }
            else if (item is SliderData slider)
            {
                if (!IsFiniteTraining(slider.time) || !IsFiniteTraining(slider.tailTime) ||
                    slider.tailTime < slider.time) invalid++;
                if (slider.sliderType == SliderData.Type.Burst && slider.sliceCount < 2) invalid++;
                if (slider.sliderType == SliderData.Type.Normal) arcs++;
            }
            else if (item is ObstacleData obstacle)
            {
                obstacles++;
                if (!IsFiniteTraining(obstacle.time) || !IsFiniteTraining(obstacle.duration)) invalid++;
            }
        }
        Check("training-unified-native-structure-finite", invalid == 0,
            "notes=" + notes + "; arcs=" + arcs + "; chains=" + chains + "; bombs=" + bombs +
            "; obstacles=" + obstacles + "; invalid=" + invalid);
        if (trainingScenario == "Percentage" || trainingScenario == "Half")
            Check("training-unified-reduction-structure-present", notes + obstacles > 0,
                "notes=" + notes + "; obstacles=" + obstacles);
    }

    IEnumerator VerifyUnifiedSettingsNavigation()
    {
        Type settingsType = FindTrainingType("BeatSaberMarkupLanguage.Settings.BSMLSettings");
        object settings = FindBsmlSettingsInstance(settingsType);
        object entriesObject = GetMember(settings, "SettingsMenus");
        object[] entries = entriesObject is IEnumerable values ? values.Cast<object>().ToArray() : Array.Empty<object>();
        object[] noteEntries = entries.Where(entry => GetMember(entry, "Host")?.GetType().FullName == "NoteMotionControl.SettingsMenu").ToArray();
        Check("training-global-settings-single-entry", noteEntries.Length == 1,
            "NoteMotionControl settings entries=" + noteEntries.Length);
        if (noteEntries.Length != 1) yield break;

        object host = GetMember(noteEntries[0], "Host");
        EnsureSettingsMenuSetup(noteEntries[0]);
        Check("training-global-settings-no-scope", host.GetType().GetProperty("Scope") == null &&
            host.GetType().GetProperty("SelectedScope") == null &&
            host.GetType().GetMethod("RestoreGlobal", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) == null,
            "The merged settings host must expose one global profile.");

        string[] presets = ReadChoiceStrings(host, "Presets");
        Check("training-global-settings-presets", presets.SequenceEqual(new[] { "Original", "Entry", "Easy", "Normal", "Hard", "Half", "Stable", "Early", "Custom" }),
            string.Join(", ", presets));
        Check("training-global-settings-choice-contract",
            ReadChoiceStrings(host, "NjsModes").SequenceEqual(new[] { "Map", "Override", "Multiplier" }) &&
            ReadChoiceStrings(host, "ReductionModes").SequenceEqual(new[] { "Off", "Density", "Percentage" }) &&
            ReadChoiceStrings(host, "ApplicationPolicies").SequenceEqual(new[] { "Only reduce", "Strict" }) &&
            ReadChoiceStrings(host, "ReactionModes").SequenceEqual(new[] { "Game", "Specified" }),
            "NJS/reduction/policy/reaction choices follow the merged contract.");

        object store = GetStaticMember(FindTrainingType("NoteMotionControl.RuntimeState"), "Store");
        object original = GetMember(store, "Global");
        string path = Path.Combine(Path.GetDirectoryName(Application.dataPath), "UserData", "NoteMotionControl.json");
        try
        {
            SetTrainingMember(host, "ReductionMode", "Percentage");
            SetTrainingMember(host, "RetainPercent", 50f);
            object saved = GetMember(store, "Global");
            JObject document = JObject.Parse(File.ReadAllText(path));
            Check("training-global-settings-envelope", (int?)document["SchemaVersion"] == 2 &&
                document["Settings"] is JObject && document["Global"] == null && document["ChartOverrides"] == null,
                document.ToString(Newtonsoft.Json.Formatting.None));
            Check("training-global-settings-value-save", ReadInt(saved, "ReductionMode", -1) == 2 &&
                Math.Abs(ReadFloat(saved, "RetainPercent", -1f) - 50f) < 0.001f &&
                (int?)document["Settings"]?["ReductionMode"] == 2,
                document.ToString(Newtonsoft.Json.Formatting.None));
            SetTrainingMember(host, "Preset", "Original");
            SetTrainingMember(host, "ReductionMode", "Off");
            SetTrainingMember(host, "RetainPercent", 100f);
            AccessTools.Method(host.GetType(), "Refresh")?.Invoke(host, null);
            yield return null;
        }
        finally
        {
            if (store != null && original != null) SaveTrainingProfile(store, original);
            AccessTools.Method(host.GetType(), "Refresh")?.Invoke(host, null);
        }
    }

    static string[] ReadChoiceStrings(object host, string property)
    {
        object value = GetMember(host, property);
        return value is IEnumerable values
            ? values.Cast<object>().Select(item => Convert.ToString(item, CultureInfo.InvariantCulture)).ToArray()
            : Array.Empty<string>();
    }

    static bool SameTrainingOptions(object left, object right)
    {
        if (left == null || right == null) return left == right;
        string[] names = {
            "Enabled", "ApplicationPolicy", "NjsMode", "OverrideNjs", "SpeedMultiplier", "ReductionMode",
            "MaxNps", "SameHandMinIntervalMs", "WindowSeconds", "RetainPercent", "ReactionTimeMode",
            "ReactionTimeMs", "HidePreJump", "VerticalJump", "HorizontalSwap", "SpawnRotation", "RotateTowardsPlayer"
        };
        foreach (string name in names)
        {
            object a = GetMember(left, name), b = GetMember(right, name);
            if (a is float af && b is float bf)
            {
                if (Math.Abs(af - bf) > 0.00001f) return false;
            }
            else if (!Equals(a, b)) return false;
        }
        return true;
    }

    string ReadTrainingSummary()
    {
        return Convert.ToString(GetStaticMember(FindTrainingType("NoteMotionControl.TrainingLoad"), "LastSummary"),
            CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
