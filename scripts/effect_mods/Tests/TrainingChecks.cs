using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EffectModsSmoke;

public sealed partial class Runner
{
    // This file intentionally talks to the optional training assemblies only
    // through reflection.  The effect smoke plug-in must remain loadable when
    // one or more of those assemblies is not installed.
    bool trainingPluginStateRead;
    bool trainingScenarioConfigured;
    string trainingScenario = "Default";
    bool trainingNoteExpected;
    bool trainingNoteLoaded;
    bool trainingEnergyExpected;
    bool trainingEnergyLoaded;
    bool trainingAnyExpected;
    bool trainingCaptured;
    bool trainingUnifiedCaptured;
    object trainingUnifiedOutcome;
    string trainingUnifiedChartKey = string.Empty;
    int trainingUnifiedReportedFiltered = -1;
    IReadonlyBeatmapData trainingUnifiedBeatmap;
    Type trainingUnifiedBeatmapType;
    int trainingUnifiedScoringCount = -1;
    int trainingUnifiedMaxScore = -1;
    bool trainingEnergyHudSeen;
    bool trainingEnergyHudFinite;
    string trainingEnergyHudText = string.Empty;
    bool trainingEnergySnapshotSeen;
    bool trainingEnergySnapshotFinite;
    bool trainingEnergyTimeMonotonic = true;
    bool trainingEnergyTimeAdvanced;
    double trainingEnergyLastEquivalent = -1;
    double trainingEnergyLastPlaying = -1;
    long trainingEnergySession;
    bool trainingEnergyInitialSnapshotPresent;
    string trainingEnergyBeatmapKey = string.Empty;
    bool trainingEnergyResultExpected;
    bool trainingEnergyResultCheckScheduled;
    bool trainingEnergyResultObserved;
    Harmony trainingResultHarmony;
    int trainingEnergyObservations;
    object trainingSubmission;
    bool trainingNoteTicketObserved;
    static class TrainingResultsProbe
    {
        internal static Runner Owner;

        public static void Postfix(object __instance)
        {
            Runner owner = Owner;
            if (owner != null) owner.QueueTrainingResultsCheck(__instance);
        }
    }

    public void PrepareTrainingScenario()
    {
        EnsureTrainingPluginState();
        ConfigureTrainingScenario();
        if (!trainingAnyExpected) return;

        VerifyTrainingPlugin("NoteMotionControl", trainingNoteExpected, trainingNoteLoaded,
            "NoteMotionControl.Plugin", "localtraining.NoteMotionControl");
        VerifyTrainingPlugin("ActivityEnergyCounter", trainingEnergyExpected, trainingEnergyLoaded,
            "ActivityEnergyCounter.Plugin", "local.beatsaber.activity-energy-counter");
    }

    public void VerifyTrainingMenu()
    {
        PrepareTrainingScenario();
        if (!trainingAnyExpected) return;

        if (trainingNoteLoaded)
        {
            string detail;
            bool registered = FindBsmlHost("NoteMotionControl.SettingsMenu", out detail);
            Check("training-note-motion-bsml-menu", registered, detail);
            string gameRoot = Path.GetDirectoryName(Application.dataPath);
            Check("training-legacy-density-removed", FindTrainingType("DensityReducer.Plugin") == null &&
                !File.Exists(Path.Combine(gameRoot, "Plugins", "DensityReducer.dll")) &&
                !File.Exists(Path.Combine(gameRoot, "UserData", "DensityReducer.json")),
                "Legacy plugin and global/per-chart settings are absent.");
        }

        if (trainingEnergyLoaded)
        {
            Type counterType = FindTrainingType("ActivityEnergyCounter.ActivityEnergyCounter");
            Type hostType = FindTrainingType("ActivityEnergyCounter.EnergySettingsHost");
            Assembly assembly = counterType?.Assembly;
            bool resource = false;
            if (assembly != null)
            {
                using (Stream stream = assembly.GetManifestResourceStream("ActivityEnergyCounter.UI.Settings.bsml"))
                    resource = stream != null;
            }
            string registrationDetail;
            bool registered = FindCountersCustomCounter(out registrationDetail);
            Check("training-energy-counters-custom-counter", registered,
                registrationDetail + "; type=" + (counterType == null ? "missing" : counterType.FullName));
            Check("training-energy-bsml-schema", hostType != null && resource,
                "host=" + (hostType == null ? "missing" : hostType.FullName) + "; resource=" + resource);
            VerifyEnergyBsmlPage(counterType?.Assembly, hostType);
            VerifyEnergyRuntimeMenu();
        }

        VerifyTrainingLocalization();
        VerifyModUiChinesePage();
    }

    public void CaptureTrainingGameplay(Zenject.SceneContext context, IReadonlyBeatmapData beatmap)
    {
        EnsureTrainingPluginState();
        if (!trainingAnyExpected) return;
        trainingCaptured = true;
        CaptureTrainingSubmission(context);
        trainingNoteTicketObserved = false;
        ObserveTrainingSubmissionGate();

        CaptureUnifiedTrainingGameplay(context, beatmap);
        if (trainingNoteLoaded)
        {
            CaptureNoteMotionGameplay(beatmap);
            CaptureUnifiedMotionChecks();
        }
        if (trainingEnergyLoaded)
            CaptureEnergyGameplay();
        if (trainingEnergyLoaded)
            CaptureEnergyRuntime();
        CaptureTrainingNjsFixture(beatmap);
    }

    public void ObserveTrainingGameplay(float songTime)
    {
        EnsureTrainingPluginState();
        if (!trainingAnyExpected || !trainingCaptured) return;

        if (trainingMotionExpected)
        {
            ObserveNoteMotion(songTime);
            ObserveUnifiedMotionChecks(songTime);
        }
        if (trainingEnergyLoaded)
            ObserveEnergy(songTime);
        if (trainingEnergyLoaded)
            ObserveEnergyRuntime(songTime);
        ObserveTrainingNjsFixture(songTime);
        ObserveTrainingSubmissionGate();
    }

    public void FinishTrainingGameplay()
    {
        EnsureTrainingPluginState();
        if (!trainingAnyExpected || !trainingCaptured) return;

        if (trainingUnifiedCaptured)
            FinishUnifiedTrainingGameplay();
        if (trainingMotionExpected)
        {
            FinishNoteMotionGameplay();
            FinishUnifiedMotionChecks();
        }
        if (trainingEnergyLoaded)
            FinishEnergyGameplay();
        if (trainingEnergyLoaded)
            FinishEnergyRuntime();
        FinishTrainingNjsFixture();
        FinishTrainingSubmissionGate();
    }

    public void VerifyTrainingResults()
    {
        if (!trainingEnergyResultExpected || !trainingEnergyLoaded || trainingEnergyResultObserved) return;
        Type resultsType = FindTrainingType("ResultsViewController");
        object active = null;
        if (resultsType != null)
        {
            try
            {
                active = Resources.FindObjectsOfTypeAll(resultsType)
                    .OfType<HMUI.ViewController>()
                    .FirstOrDefault(view => view != null && view.isActivated && view.gameObject.activeInHierarchy);
            }
            catch { }
        }
        if (active == null)
        {
            Check("training-energy-results-view", false, "No active ResultsViewController was available.");
            return;
        }
        try
        {
            InspectTrainingResults(active);
            VerifyEnergyRuntimeResults(active);
        }
        finally
        {
            trainingEnergyResultCheckScheduled = false;
            UninstallTrainingResultsProbe();
        }
    }

    void EnsureTrainingPluginState()
    {
        if (trainingPluginStateRead) return;
        trainingPluginStateRead = true;
        if (Environment.GetEnvironmentVariable("EFFECT_SMOKE_SKIP_TRAINING") == "1")
        {
            var owners = new[] { "localtraining.NoteMotionControl", "local.beatsaber.activity-energy-counter" };
            Check("native-training-patches-disabled", !owners.Any(HasHarmonyOwner), "No training Harmony patches active during native song verification");
            return;
        }

        bool energyOnly = Environment.GetEnvironmentVariable("EFFECT_SMOKE_ENERGY_ONLY") == "1";
        bool noteTrainingOnly = Environment.GetEnvironmentVariable("EFFECT_SMOKE_NOTE_TRAINING_ONLY") == "1";

        Type noteType = null;
        trainingNoteExpected = !energyOnly && TrainingPluginExpected("NoteMotionControl", "NoteMotionControl.Plugin", out noteType);
        trainingNoteLoaded = noteType != null;
        Type energyType = null;
        trainingEnergyExpected = !noteTrainingOnly && TrainingPluginExpected("ActivityEnergyCounter", "ActivityEnergyCounter.Plugin", out energyType);
        trainingEnergyLoaded = !noteTrainingOnly && energyType != null;
        trainingAnyExpected = trainingNoteExpected || trainingEnergyExpected;
    }

    void ConfigureTrainingScenario()
    {
        if (trainingScenarioConfigured) return;
        trainingScenarioConfigured = true;
        trainingScenario = (Environment.GetEnvironmentVariable("EFFECT_SMOKE_TRAINING_SCENARIO") ?? "Default").Trim();
        if (string.IsNullOrEmpty(trainingScenario)) trainingScenario = "Default";
        if (string.Equals(trainingScenario, "Default", StringComparison.OrdinalIgnoreCase)) return;

        string canonical = new[] {
            "Disabled", "Map", "Fixed", "MultiplierLow", "MultiplierHigh", "Identity", "Effects", "Variable", "Percentage",
            "Entry", "Easy", "Normal", "Hard", "Half", "Stable", "Early", "Cap", "CapVariable", "TimingOnly", "StableStrict"
        }
            .FirstOrDefault(value => string.Equals(value, trainingScenario, StringComparison.OrdinalIgnoreCase));
        if (canonical == null)
        {
            Check("training-scenario-name", false, trainingScenario);
            return;
        }
        trainingScenario = canonical;
        if (canonical == "Variable" || canonical == "CapVariable") InstallTrainingNjsFixture();

        try
        {
            if (trainingNoteLoaded)
            {
                Type stateType = FindTrainingType("NoteMotionControl.RuntimeState");
                object store = GetStaticMember(stateType, "Store");
                object options = NewTrainingProfile(store);
                if (options == null) throw new InvalidOperationException("Note Motion profile store is unavailable.");
                ApplyTrainingScenario(options, canonical);
                SaveTrainingProfile(store, options);
                Check("training-scenario-note-applied", ScenarioNoteMatches(options, canonical), canonical);
            }
        }
        catch (Exception error)
        {
            Exception actual = error is TargetInvocationException invocation && invocation.InnerException != null
                ? invocation.InnerException : error;
            Check("training-scenario-apply", false, actual.GetType().Name + ": " + actual.Message);
        }
    }

    static void ApplyTrainingScenario(object options, string scenario)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));

        // Preset scenarios are resolved by the same public factory used by
        // SettingsMenu, so this smoke path exercises the real global profile
        // contract instead of a second copy of preset values.
        if (new[] { "Entry", "Easy", "Normal", "Hard", "Half", "Stable", "Early" }
            .Contains(scenario, StringComparer.Ordinal))
        {
            Type presets = FindTrainingType("NoteMotionControl.Presets");
            MethodInfo create = presets?.GetMethod("Create", BindingFlags.Static | BindingFlags.Public,
                null, new[] { typeof(string) }, null);
            object preset = create?.Invoke(null, new object[] { scenario });
            if (preset == null) throw new MissingMethodException("NoteMotionControl.Presets.Create(string)");
            foreach (PropertyInfo property in preset.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
                if (property.CanRead) SetTrainingMember(options, property.Name, property.GetValue(preset, null));
            return;
        }

        SetTrainingMember(options, "Enabled", scenario != "Disabled");
        SetTrainingEnum(options, "ApplicationPolicy",
            scenario == "TimingOnly" || scenario == "StableStrict" ? 1 : 0,
            scenario == "TimingOnly" || scenario == "StableStrict" ? "Strict" : "OnlyReduce");
        SetTrainingEnum(options, "ReductionMode",
            scenario == "Percentage" ? 2 : 0,
            scenario == "Percentage" ? "Percentage" : "Off");
        SetTrainingMember(options, "RetainPercent", 50f);
        SetTrainingEnum(options, "ReactionTimeMode",
            scenario == "TimingOnly" || scenario == "StableStrict" ? 1 : 0,
            scenario == "TimingOnly" || scenario == "StableStrict" ? "Specified" : "Game");
        if (scenario == "TimingOnly" || scenario == "StableStrict")
            SetTrainingMember(options, "ReactionTimeMs", 1000f);
        if (scenario == "StableStrict")
        {
            SetTrainingEnum(options, "NjsMode", 1, "Override");
            SetTrainingMember(options, "OverrideNjs", 12f);
            SetTrainingMember(options, "ReactionTimeMs", 800f);
            SetTrainingMember(options, "HidePreJump", true);
            SetTrainingMember(options, "VerticalJump", false);
            SetTrainingMember(options, "HorizontalSwap", false);
            SetTrainingMember(options, "SpawnRotation", false);
            SetTrainingMember(options, "RotateTowardsPlayer", false);
        }
        else if (scenario == "Fixed" || scenario == "Effects")
                {
                    SetTrainingEnum(options, "NjsMode", 1, "Override");
                    SetTrainingMember(options, "OverrideNjs", 16f);
                }
        else if (scenario == "Cap" || scenario == "CapVariable")
        {
            SetTrainingEnum(options, "NjsMode", 1, "Override");
            SetTrainingMember(options, "OverrideNjs", 12f);
        }
        else if (scenario == "MultiplierLow" || scenario == "MultiplierHigh" || scenario == "Identity" || scenario == "Variable")
                {
                    SetTrainingEnum(options, "NjsMode", 2, "Multiplier");
            SetTrainingMember(options, "SpeedMultiplier", scenario == "MultiplierLow" ? 0.65f : scenario == "Identity" ? 1f : 1.5f);
                }
    }

    static object NewTrainingProfile(object store)
    {
        Type storeType = store?.GetType();
        Type profileType = storeType?.GetProperty("Global", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.PropertyType;
        return profileType == null ? null : Activator.CreateInstance(profileType, true);
    }

    static void SaveTrainingProfile(object store, object options)
    {
        if (store == null || options == null) throw new InvalidOperationException("Profile store is unavailable.");
        MethodInfo save = AccessTools.Method(store.GetType(), "SaveGlobal");
        if (save == null) throw new MissingMethodException(store.GetType().FullName, "SaveGlobal");
        save.Invoke(store, new[] { options });
    }

    static void SetTrainingMember(object target, string name, object value)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        PropertyInfo property = target.GetType().GetProperty(name, flags);
        if (property != null && property.CanWrite)
        {
            property.SetValue(target, ConvertTrainingValue(value, property.PropertyType), null);
            return;
        }
        FieldInfo field = target.GetType().GetField(name, flags);
        if (field != null)
        {
            field.SetValue(target, ConvertTrainingValue(value, field.FieldType));
            return;
        }
        throw new MissingMemberException(target.GetType().FullName, name);
    }

    static void SetTrainingEnum(object target, string name, int numeric, string expectedName)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        PropertyInfo property = target.GetType().GetProperty(name, flags);
        Type enumType = property?.PropertyType;
        if (enumType == null || !enumType.IsEnum || !Enum.GetNames(enumType).Contains(expectedName, StringComparer.Ordinal))
            throw new InvalidDataException(name + " does not expose enum " + expectedName);
        property.SetValue(target, Enum.ToObject(enumType, numeric), null);
    }

    static object ConvertTrainingValue(object value, Type targetType)
    {
        if (value == null || targetType.IsInstanceOfType(value)) return value;
        if (targetType.IsEnum) return Enum.ToObject(targetType, Convert.ToInt32(value, CultureInfo.InvariantCulture));
        return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
    }

    static bool ScenarioNoteMatches(object options, string scenario)
    {
        if (scenario == "Disabled") return !ReadBool(options, "Enabled", true);
        if (new[] { "Entry", "Easy", "Normal", "Hard", "Half", "Stable", "Early" }
            .Contains(scenario, StringComparer.Ordinal))
        {
            Type presets = FindTrainingType("NoteMotionControl.Presets");
            // Reflection keeps this helper independent of the plugin's
            // concrete Options type while still using the canonical preset
            // matcher.
            MethodInfo identify = presets?.GetMethods(BindingFlags.Static | BindingFlags.Public)
                .FirstOrDefault(method => method.Name == "GetName" && method.GetParameters().Length == 1);
            return identify != null && string.Equals(Convert.ToString(identify.Invoke(null, new[] { options }), CultureInfo.InvariantCulture), scenario, StringComparison.Ordinal);
        }
        if (!ReadBool(options, "Enabled", false)) return false;
        int mode = ReadInt(options, "NjsMode", -1);
        if (scenario == "Fixed" || scenario == "Effects")
            return mode == 1 && Mathf.Abs(ReadFloat(options, "OverrideNjs", -1f) - 16f) < 0.001f;
        if (scenario == "MultiplierLow" || scenario == "MultiplierHigh" || scenario == "Identity" || scenario == "Variable")
            return mode == 2 && Mathf.Abs(ReadFloat(options, "SpeedMultiplier", -1f) -
                (scenario == "MultiplierLow" ? 0.65f : scenario == "Identity" ? 1f : 1.5f)) < 0.001f;
        if (scenario == "Percentage" || scenario == "Half")
            return ReadInt(options, "ReductionMode", -1) == 2 &&
                   Mathf.Abs(ReadFloat(options, "RetainPercent", -1f) - 50f) < 0.001f;
        if (scenario == "Cap" || scenario == "CapVariable")
            return mode == 1 && Mathf.Abs(ReadFloat(options, "OverrideNjs", -1f) - 12f) < 0.001f;
        if (scenario == "TimingOnly")
            return mode == 0 && ReadInt(options, "ApplicationPolicy", -1) == 1 &&
                   ReadInt(options, "ReactionTimeMode", -1) == 1 &&
                   Mathf.Abs(ReadFloat(options, "ReactionTimeMs", -1f) - 1000f) < 0.001f;
        if (scenario == "StableStrict")
            return mode == 1 && ReadInt(options, "ApplicationPolicy", -1) == 1 &&
                   Mathf.Abs(ReadFloat(options, "OverrideNjs", -1f) - 12f) < 0.001f &&
                   Mathf.Abs(ReadFloat(options, "ReactionTimeMs", -1f) - 800f) < 0.001f;
        return mode == 0;
    }

    static bool TrainingPluginExpected(string id, string pluginTypeName, out Type pluginType)
    {
        pluginType = null;
        Type manager = FindTrainingType("IPA.Loader.PluginManager");
        if (!(GetStaticMember(manager, "EnabledPlugins") is IEnumerable enabled)) return false;
        foreach (object metadata in enabled)
        {
            if (!string.Equals(Convert.ToString(GetMember(metadata, "Id"), CultureInfo.InvariantCulture), id, StringComparison.Ordinal)) continue;
            pluginType = FindTrainingType(pluginTypeName);
            return true;
        }
        return false;
    }

    void VerifyTrainingPlugin(string id, bool expected, bool loaded, string pluginTypeName, string harmonyId)
    {
        if (!expected) return;
        Type type = FindTrainingType(pluginTypeName);
        Check("training-plugin-loaded-" + id, loaded,
            "pluginType=" + (type == null ? "missing" : type.AssemblyQualifiedName));
        Check("training-harmony-hook-" + id, HasHarmonyOwner(harmonyId),
            "owner=" + harmonyId);
    }

    static bool HasHarmonyOwner(string owner)
    {
        try
        {
            foreach (MethodBase method in Harmony.GetAllPatchedMethods())
            {
                var patches = Harmony.GetPatchInfo(method);
                if (patches != null && patches.Owners != null && patches.Owners.Contains(owner)) return true;
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    static object FindPluginMetadata(string id)
    {
        try
        {
            Type manager = FindTrainingType("IPA.Loader.PluginManager");
            if (manager == null) return null;
            MethodInfo method = manager.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(value => value.Name == "GetPluginFromId" &&
                    value.GetParameters().Length == 1 && value.GetParameters()[0].ParameterType == typeof(string));
            return method?.Invoke(null, new object[] { id });
        }
        catch
        {
            return null;
        }
    }

    static Type FindTrainingType(string fullName)
    {
        try
        {
            Type direct = AccessTools.TypeByName(fullName);
            if (direct != null) return direct;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type value = assembly.GetType(fullName, false);
                if (value != null) return value;
            }
        }
        catch
        {
            // An optional assembly can disappear while a scene is unloading.
        }
        return null;
    }

    static bool FindBsmlHost(string hostTypeName, out string detail)
    {
        detail = "BSMLSettings unavailable";
        try
        {
            Type settingsType = FindTrainingType("BeatSaberMarkupLanguage.Settings.BSMLSettings");
            object settings = FindBsmlSettingsInstance(settingsType);
            if (settings == null) return false;
            object entries = GetMember(settings, "SettingsMenus");
            if (!(entries is IEnumerable values))
            {
                detail = "SettingsMenus unavailable";
                return false;
            }

            int count = 0;
            foreach (object entry in values)
            {
                count++;
                object host = GetMember(entry, "Host");
                if (host != null && string.Equals(host.GetType().FullName, hostTypeName, StringComparison.Ordinal))
                {
                    if (!ReadBool(entry, "DidSetup", false)) AccessTools.Method(entry.GetType(), "Setup")?.Invoke(entry, null);
                    var view = GetMember(entry, "ViewController") as Component;
                    int labels = view == null ? 0 : view.GetComponentsInChildren<TMP_Text>(true).Length;
                    string fieldName = hostTypeName.StartsWith("NoteMotionControl.", StringComparison.Ordinal) ? "localError" : "_localError";
                    var statusField = AccessTools.Field(host.GetType(), fieldName);
                    object previous = statusField.GetValue(host);
                    bool reactive = false;
                    string bindingDiagnostic = "";
                    Action notify = () => {
                        var method = AccessTools.Method(host.GetType(), "NotifyStatus");
                        if (method != null) method.Invoke(host, null);
                        else AccessTools.Method(host.GetType(), "Notify").Invoke(host, new object[] { "status" });
                    };
                    try {
                        statusField.SetValue(host, "Training settings binding probe");
                        notify();
                        reactive = view != null && view.GetComponentsInChildren<TMP_Text>(true).Any(t=>t.text=="Training settings binding probe");
                        if (!reactive && view != null) {
                            var updaters = view.GetComponentsInChildren(FindTrainingType("BeatSaberMarkupLanguage.Components.NotifyUpdater"),true);
                            bindingDiagnostic = "; Status=" + GetMember(host, "Status") + "; notifyUpdaters=" + updaters.Length + "; boundKeys=" +
                                String.Join("|", updaters.Select(u=>GetMember(u,"actionDict") is IDictionary dict ? String.Join(",",dict.Keys.Cast<object>()) : "missing")) +
                                "; textTail=" + String.Join("|",view.GetComponentsInChildren<TMP_Text>(true).Select(t=>t.text).Reverse().Take(4).Reverse());
                        }
                    }
                    finally { statusField.SetValue(host, previous); notify(); }
                    detail = "host=" + hostTypeName + "; entries=" + count + "; parsedLabels=" + labels + "; liveStatusBinding=" + reactive + bindingDiagnostic;
                    return labels > 0 && reactive;
                }
            }
            detail = "host=" + hostTypeName + "; entries=" + count;
        }
        catch (Exception error)
        {
            detail = error.GetType().Name + ": " + error.Message;
        }
        return false;
    }

    void VerifyEnergyBsmlPage(Assembly assembly, Type hostType)
    {
        if (assembly == null || hostType == null) return;
        object host = null;
        try { host = Activator.CreateInstance(hostType, true); }
        catch (Exception error)
        {
            Check("training-energy-bsml-page", false, error.GetType().Name + ": " + error.Message);
            return;
        }
        string detail;
        bool parsed = TryParseTrainingPage(
            assembly,
            "ActivityEnergyCounter.UI.Settings.bsml",
            host,
            new[] {
                "Enabled", "UseHeadTracking", "HudShowSong", "HudShowSession", "HudShowToday",
                "ResultsShowSong", "ResultsShowSession", "ResultsShowToday", "HudShowDuration",
                "ResultsShowDuration", "ShowLabels", "ShowEstimate", "DecimalPlaces",
                "HudFontScale", "ResultsFontScale", "HudPrimaryScope"
            },
            out detail);
        Check("training-energy-bsml-page", parsed, detail);
    }

    void VerifyModUiChinesePage()
    {
        Type panelType = FindTrainingType("ModUiChinese.LocalizationPanel");
        if (panelType == null) return;
        object host;
        Assembly assembly;
        string resource;
        string detail;
        if (!TryFindBsmlHostEntry(panelType.FullName, out host, out assembly, out resource, out detail))
        {
            Check("training-localization-bsml-page", false, detail);
            return;
        }

        bool parsed = TryParseTrainingPage(assembly, resource, host,
            new[] { "Enabled", "SelectedScope", "ScopeEnabled", "CustomFile", "Status", "HasStatus" }, out detail);
        Check("training-localization-bsml-page", parsed, detail);

        PropertyInfo enabled = panelType.GetProperty("Enabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (enabled == null || !enabled.CanRead || !enabled.CanWrite)
        {
            Check("training-localization-toggle", false, "LocalizationPanel.Enabled is not writable.");
            return;
        }
        bool original = Convert.ToBoolean(enabled.GetValue(host, null), CultureInfo.InvariantCulture);
        bool changed = false;
        try
        {
            enabled.SetValue(host, !original, null);
            changed = Convert.ToBoolean(enabled.GetValue(host, null), CultureInfo.InvariantCulture) == !original;
        }
        finally
        {
            try { enabled.SetValue(host, original, null); }
            catch (Exception error) { errors.Add("training-localization-toggle-restore: " + error); }
        }
        Check("training-localization-toggle", changed, "original=" + original + "; changed=" + changed);
        VerifyTranslationTextRestoration(host, panelType);
    }

    void VerifyTranslationTextRestoration(object panel, Type panelType)
    {
        var selected = panelType.GetProperty("SelectedScope");
        var perScope = panelType.GetProperty("ScopeEnabled");
        var enabled = panelType.GetProperty("Enabled");
        object originalSelected = selected.GetValue(panel, null);
        bool originalEnabled = (bool)enabled.GetValue(panel, null);
        bool originalScope = true;
        GameObject probe = null;
        try
        {
            selected.SetValue(panel, "NoteMotionControl", null);
            originalScope = (bool)perScope.GetValue(panel, null);
            enabled.SetValue(panel, true, null);
            perScope.SetValue(panel, true, null);
            probe = new GameObject("TrainingLanguageProbe", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI));
            var marker = probe.AddComponent(FindTrainingType("ModUiChinese.UiScope"));
            AccessTools.Field(marker.GetType(), "Scope").SetValue(marker, "NoteMotionControl");
            var text = probe.GetComponent<TMP_Text>();
            text.text = "Enabled";
            Check("training-localization-visible-chinese", text.text == "启用", text.text);
            enabled.SetValue(panel, false, null);
            Check("training-localization-restore-english", text.text == "Enabled", text.text);
            enabled.SetValue(panel, true, null);
            perScope.SetValue(panel, false, null);
            text.text = "Enabled";
            Check("training-localization-disabled-scope-common-fallback", text.text == "Enabled", text.text);
            perScope.SetValue(panel, originalScope, null);
            var scopeField = AccessTools.Field(marker.GetType(), "Scope");
            foreach (var entry in new[] {
                new[] { "AssetBundleLoadingTools", "Asset Bundles", "资源包" },
                new[] { "ParticleOverdrive", "Particle Overdrive", "粒子增强" },
                new[] { "SongChartVisualizer", "<size=75%>SongChartVisualizer</size>", "<size=75%>歌曲难度曲线</size>" }
            })
            {
                selected.SetValue(panel, entry[0], null);
                bool previous = (bool)perScope.GetValue(panel, null);
                try
                {
                    perScope.SetValue(panel, true, null);
                    scopeField.SetValue(marker, "Common");
                    text.text = entry[1];
                    Check("training-localization-menu-owner-" + entry[0], text.text == entry[2], text.text);
                    perScope.SetValue(panel, false, null);
                    Check("training-localization-menu-owner-restore-" + entry[0], text.text == entry[1], text.text);
                }
                finally { perScope.SetValue(panel, previous, null); }
            }
            selected.SetValue(panel, "SongCore", null);
            bool originalSongCore = (bool)perScope.GetValue(panel, null);
            try
            {
                perScope.SetValue(panel, true, null);
                scopeField.SetValue(marker, "SongCore");
                text.SetText("{0} songs loaded", 1608f);
                Check("training-localization-formatted-numeric", text.text == "已加载 1608 首歌曲", text.text);
                enabled.SetValue(panel, false, null);
                Check("training-localization-formatted-restore", text.text == "1608 songs loaded", text.text);
                enabled.SetValue(panel, true, null);
                text.text = "1608 songs loaded";
                Type progressType = FindTrainingType("SongCore.UI.ProgressBar");
                var progress = Resources.FindObjectsOfTypeAll(progressType).FirstOrDefault();
                var header = AccessTools.Field(progressType, "_headerText");
                object originalHeader = header.GetValue(progress);
                try
                {
                    header.SetValue(progress, text);
                    AccessTools.Method(progressType, "AppendText").Invoke(progress, new object[] { "\n11 playlists loaded" });
                    Check("training-localization-append-composite", text.text == "已加载 1608 首歌曲\n已加载 11 个歌单", text.text);
                    enabled.SetValue(panel, false, null);
                    Check("training-localization-append-restore", text.text == "1608 songs loaded\n11 playlists loaded", text.text);
                    enabled.SetValue(panel, true, null);
                }
                finally { header.SetValue(progress, originalHeader); }
            }
            finally { perScope.SetValue(panel, originalSongCore, null); }
        }
        finally
        {
            selected.SetValue(panel, "NoteMotionControl", null);
            perScope.SetValue(panel, originalScope, null);
            selected.SetValue(panel, originalSelected, null);
            enabled.SetValue(panel, originalEnabled, null);
            if (probe != null) UnityEngine.Object.Destroy(probe);
        }
    }

    static bool TryFindBsmlHostEntry(string hostTypeName, out object host, out Assembly assembly,
        out string resource, out string detail)
    {
        host = null;
        assembly = null;
        resource = string.Empty;
        detail = "BSMLSettings unavailable";
        try
        {
            Type settingsType = FindTrainingType("BeatSaberMarkupLanguage.Settings.BSMLSettings");
            object settings = FindBsmlSettingsInstance(settingsType);
            object entries = GetMember(settings, "SettingsMenus");
            if (!(entries is IEnumerable values))
            {
                detail = "SettingsMenus unavailable";
                return false;
            }
            int count = 0;
            foreach (object entry in values)
            {
                count++;
                object candidate = GetMember(entry, "Host");
                if (candidate == null || !string.Equals(candidate.GetType().FullName, hostTypeName, StringComparison.Ordinal)) continue;
                host = candidate;
                resource = Convert.ToString(GetMember(entry, "Resource"), CultureInfo.InvariantCulture) ?? string.Empty;
                assembly = GetMember(entry, "Assembly") as Assembly ?? candidate.GetType().Assembly;
                detail = "host=" + hostTypeName + "; resource=" + resource + "; entries=" + count;
                return assembly != null && !string.IsNullOrEmpty(resource);
            }
            detail = "host=" + hostTypeName + "; entries=" + count;
        }
        catch (Exception error)
        {
            detail = error.GetType().Name + ": " + error.Message;
        }
        return false;
    }

    static bool TryParseTrainingPage(Assembly assembly, string resource, object host,
        string[] expectedValues, out string detail)
    {
        detail = "BSML parser unavailable";
        GameObject root = null;
        try
        {
            Type parserType = FindTrainingType("BeatSaberMarkupLanguage.BSMLParser");
            object parser = FindBsmlSettingsInstance(parserType);
            MethodInfo parse = parserType?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(value => value.Name == "Parse" &&
                    value.GetParameters().Length == 3 &&
                    value.GetParameters()[0].ParameterType == typeof(string) &&
                    value.GetParameters()[1].ParameterType == typeof(GameObject));
            if (assembly == null || string.IsNullOrEmpty(resource) || parser == null || parse == null)
                return false;
            string markup;
            using (Stream stream = assembly.GetManifestResourceStream(resource))
            using (StreamReader reader = stream == null ? null : new StreamReader(stream))
                markup = reader?.ReadToEnd();
            if (string.IsNullOrEmpty(markup))
            {
                detail = "resource=" + resource + " is empty or missing";
                return false;
            }
            root = new GameObject("EffectSmoke.TrainingBsmlProbe");
            object parameters = parse.Invoke(parser, new object[] { markup, root, host });
            object parsedValues = GetMember(parameters, "Values");
            if (!(parsedValues is IDictionary values))
            {
                detail = "BSML parser returned no Values dictionary";
                return false;
            }
            string[] missing = (expectedValues ?? Array.Empty<string>())
                .Where(value => !values.Contains(value)).ToArray();
            detail = "resource=" + resource + "; values=" + values.Count + "; missing=" + String.Join(",", missing);
            return missing.Length == 0;
        }
        catch (Exception error)
        {
            Exception actual = error is TargetInvocationException invocation && invocation.InnerException != null
                ? invocation.InnerException : error;
            detail = actual.GetType().Name + ": " + actual.Message;
            return false;
        }
        finally
        {
            if (root != null) UnityEngine.Object.Destroy(root);
        }
    }

    static object FindBsmlSettingsInstance(Type settingsType)
    {
        if (settingsType == null) return null;
        object settings = GetStaticMember(settingsType, "instance") ?? GetStaticMember(settingsType, "Instance");
        if (settings != null) return settings;
        try
        {
            foreach (Zenject.SceneContext context in Resources.FindObjectsOfTypeAll<Zenject.SceneContext>())
            {
                try
                {
                    if (context == null || !context.gameObject.scene.IsValid() || !context.gameObject.activeInHierarchy) continue;
                    object container = context.Container;
                    MethodInfo tryResolve = container?.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .FirstOrDefault(value => value.Name == "TryResolve" && value.GetParameters().Length == 1 &&
                            value.GetParameters()[0].ParameterType == typeof(Type));
                    settings = tryResolve?.Invoke(container, new object[] { settingsType });
                    if (settings != null) return settings;
                }
                catch
                {
                    // A player container can exist alongside the menu
                    // container and legitimately have no BSMLSettings binding.
                }
            }
        }
        catch
        {
            // Menu scene transitions can dispose a container during this scan.
        }
        return null;
    }

    static bool FindCountersCustomCounter(out string detail)
    {
        detail = "Counters+ custom-counter registry unavailable";
        try
        {
            Type plugin = FindTrainingType("CountersPlus.Plugin");
            object loaded = GetStaticMember(plugin, "LoadedCustomCounters");
            if (!(loaded is IEnumerable values)) return false;
            int count = 0;
            foreach (object entry in values)
            {
                count++;
                if (ContainsCounterRegistration(entry, 3))
                {
                    detail = "LoadedCustomCounters=" + count;
                    return true;
                }
            }
            detail = "LoadedCustomCounters=" + count;
        }
        catch (Exception error)
        {
            detail = error.GetType().Name + ": " + error.Message;
        }
        return false;
    }

    static bool ContainsCounterRegistration(object value, int depth)
    {
        if (value == null) return false;
        if (value is string text)
            return text.IndexOf("ActivityEnergyCounter.ActivityEnergyCounter", StringComparison.Ordinal) >= 0 ||
                   text.IndexOf("Activity Energy", StringComparison.Ordinal) >= 0;
        if (depth < 0) return false;

        string typeName = value.GetType().FullName ?? value.GetType().Name;
        if (typeName.IndexOf("ActivityEnergyCounter", StringComparison.Ordinal) >= 0) return true;
        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
                if (ContainsCounterRegistration(entry.Key, depth - 1) || ContainsCounterRegistration(entry.Value, depth - 1)) return true;
            return false;
        }
        if (value is IEnumerable enumerable)
        {
            int count = 0;
            foreach (object entry in enumerable)
            {
                if (ContainsCounterRegistration(entry, depth - 1)) return true;
                if (++count >= 256) break;
            }
            return false;
        }

        foreach (string name in new[] { "Name", "CounterName", "CounterType", "CounterLocation", "Resource", "Host", "BSML" })
        {
            object member = GetMember(value, name);
            if (member is string memberText &&
                (memberText.IndexOf("ActivityEnergyCounter", StringComparison.Ordinal) >= 0 ||
                 memberText.IndexOf("Activity Energy", StringComparison.Ordinal) >= 0)) return true;
            if (member != null && !(member is string) && ContainsCounterRegistration(member, depth - 1)) return true;
        }
        return false;
    }

    void VerifyTrainingLocalization()
    {
        Type engineType = FindTrainingType("ModUiChinese.TranslationEngine");
        Type stateType = FindTrainingType("ModUiChinese.LocalizationState");
        if (engineType == null || stateType == null) return;

        try
        {
            var catalog = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
            {
                ["Common"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["Default"] = "默认" },
                ["TrainingChecks"] = new Dictionary<string, string>(StringComparer.Ordinal) {
                    ["Default"] = "训练默认", ["Value {0}"] = "值 {0}"
                }
            };
            object disabled = Activator.CreateInstance(engineType, new object[] { catalog, null, false });
            object disabledText = engineType.GetMethod("Translate", BindingFlags.Instance | BindingFlags.Public)
                .Invoke(disabled, new object[] { "Default", "Common" });
            Check("training-localization-disabled", Equals(disabledText, "Default"), Convert.ToString(disabledText, CultureInfo.InvariantCulture));

            object scoped = Activator.CreateInstance(engineType, new object[] { catalog, new[] { "TrainingChecks" }, true });
            MethodInfo translate = engineType.GetMethod("Translate", BindingFlags.Instance | BindingFlags.Public);
            object scopedText = translate.Invoke(scoped, new object[] { "Default", "TrainingChecks" });
            object commonText = translate.Invoke(scoped, new object[] { "Default", "Common" });
            Check("training-localization-scope-disable", Equals(scopedText, "Default") && Equals(commonText, "默认"),
                "disabled=" + scopedText + "; common=" + commonText);

            MethodInfo parse = stateType.GetMethod("ParseCustom", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (parse == null) throw new MissingMethodException(stateType.FullName, "ParseCustom");
            object parsed = parse.Invoke(null, new object[] {
                "{\"SchemaVersion\":1,\"Translations\":{\"TrainingChecks\":{\"Value {0}\":\"值 {0}\"}}}"
            });
            bool parsedSafe = parsed is IDictionary parsedDictionary && parsedDictionary.Contains("TrainingChecks");
            Check("training-localization-custom-schema", parsedSafe, parsed?.GetType().FullName ?? "null");

            var customField = stateType.GetField("Custom", BindingFlags.Static | BindingFlags.NonPublic);
            object previousCustom = customField.GetValue(null);
            try
            {
                customField.SetValue(null, parse.Invoke(null, new object[] {
                    "{\"SchemaVersion\":1,\"Translations\":{\"NoteMotionControl\":{\"enabled\":\"自定义启用\"}}}"
                }));
                object customEngine = AccessTools.Method(stateType, "CreateEngine").Invoke(null, null);
                object customText = translate.Invoke(customEngine, new object[] { "Enabled", "NoteMotionControl" });
                Check("training-localization-custom-priority", Equals(customText, "自定义启用"), Convert.ToString(customText));
            }
            finally { customField.SetValue(null, previousCustom); }

            bool rejectedVersion = RejectCustomSchema(parse,
                "{\"SchemaVersion\":2,\"Translations\":{\"TrainingChecks\":{\"Value\":\"值\"}}}");
            bool rejectedPlaceholder = RejectCustomSchema(parse,
                "{\"SchemaVersion\":1,\"Translations\":{\"TrainingChecks\":{\"Value {0}\":\"值\"}}}");
            bool rejectedTags = RejectCustomSchema(parse,
                "{\"SchemaVersion\":1,\"Translations\":{\"TrainingChecks\":{\"<b>Value</b>\":\"<i>值</i>\"}}}");
            bool rejectedFormat = RejectCustomSchema(parse,
                "{\"SchemaVersion\":1,\"Translations\":{\"TrainingChecks\":{\"Value {0}\":\"值 {0} }\"}}}");
            Check("training-localization-custom-validation", rejectedVersion && rejectedPlaceholder && rejectedTags && rejectedFormat,
                "schemaVersion=" + rejectedVersion + "; placeholders=" + rejectedPlaceholder + "; tags=" + rejectedTags + "; format=" + rejectedFormat);
            Type bssText = FindTrainingType("BetterSongSearch.Util.BssText");
            if (bssText != null)
            {
                MethodInfo bssTranslate = AccessTools.Method(bssText, "T");
                MethodInfo refresh = AccessTools.Method(bssText, "OnLocalizationReloaded");
                FieldInfo engine = AccessTools.Field(FindTrainingType("ModUiChinese.UiTranslator"), "Engine");
                object originalEngine = engine.GetValue(null);
                try
                {
                    catalog["BetterSongSearch"] = new Dictionary<string, string> { ["Default"] = "搜歌默认" };
                    engine.SetValue(null, Activator.CreateInstance(engineType, new object[] { catalog, null, true }));
                    string translated = (string)bssTranslate.Invoke(null, new object[] { "Default" });
                    engine.SetValue(null, Activator.CreateInstance(engineType, new object[] { catalog, null, false }));
                    string disabledValue = (string)bssTranslate.Invoke(null, new object[] { "Default" });
                    engine.SetValue(null, Activator.CreateInstance(engineType, new object[] { catalog, new[] { "BetterSongSearch" }, true }));
                    string scopeDisabledValue = (string)bssTranslate.Invoke(null, new object[] { "Default" });
                    Check("training-localization-bss-cache-invalidation", translated == "搜歌默认" && disabledValue == "Default" && scopeDisabledValue == "Default" && refresh != null,
                        "enabled=" + translated + "; globalOff=" + disabledValue + "; scopeOff=" + scopeDisabledValue + "; refreshBridge=" + (refresh != null));
                }
                finally { engine.SetValue(null, originalEngine); refresh?.Invoke(null, null); }
            }
        }
        catch (Exception error)
        {
            Exception actual = error is TargetInvocationException invocation && invocation.InnerException != null
                ? invocation.InnerException : error;
            Check("training-localization-reflection", false, actual.GetType().Name + ": " + actual.Message);
        }
    }

    static bool RejectCustomSchema(MethodInfo parse, string content)
    {
        try
        {
            parse.Invoke(null, new object[] { content });
            return false;
        }
        catch (TargetInvocationException error)
        {
            return error.InnerException is InvalidDataException;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    void CaptureTrainingSubmission(Zenject.SceneContext context)
    {
        trainingSubmission = null;
        Type submissionType = FindTrainingType("SiraUtil.Submissions.Submission");
        if (submissionType == null || context == null) return;
        try
        {
            object container = context.Container;
            MethodInfo tryResolve = container?.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(value => value.Name == "TryResolve" && value.GetParameters().Length == 1 &&
                    value.GetParameters()[0].ParameterType == typeof(Type));
            trainingSubmission = tryResolve?.Invoke(container, new object[] { submissionType });
        }
        catch
        {
            trainingSubmission = null;
        }
    }

    void FinishTrainingSubmissionGate()
    {
        ObserveTrainingSubmissionGate();
        bool noteOwnsTicket = HasTrainingSubmissionTicket("NoteMotionControl", out string noteDetail);
        bool requiresReduction = ReadBool(trainingUnifiedOutcome, "EffectiveReduction", false);
        bool requiresNote = trainingMotionModificationObserved;
        bool noteTicketObserved = noteOwnsTicket || trainingNoteTicketObserved;
        bool unchanged = string.Equals(trainingScenario, "Disabled", StringComparison.Ordinal) ||
            string.Equals(trainingScenario, "Map", StringComparison.Ordinal) ||
            string.Equals(trainingScenario, "Identity", StringComparison.Ordinal) ||
            trainingMotionAuthoredLoaded;
        unchanged &= !requiresReduction && !requiresNote;

        if (requiresReduction || requiresNote || unchanged)
        {
            Check("training-score-submission-state-readable", trainingSubmission != null,
                "SiraUtil.Submissions.Submission could not be resolved from GameCore.");
        }
        if (requiresReduction || requiresNote)
            Check("training-unified-score-submission-ticket", noteTicketObserved,
                noteDetail + "; observed=" + trainingNoteTicketObserved +
                "; reduction=" + requiresReduction + "; motion=" + requiresNote);
        if (unchanged)
            Check("training-unchanged-no-unified-score-ticket", !noteTicketObserved,
                noteDetail + "; observed=" + trainingNoteTicketObserved);
    }

    void ObserveTrainingSubmissionGate()
    {
        if (trainingSubmission == null) return;

        // Read the live SiraUtil submission object while gameplay is active.
        // A scene teardown can remove tickets before FinishTrainingGameplay;
        // retaining only an observed source keeps the final assertion tied to
        // a real ticket returned by Submission.Tickets(), not guessed plugin
        // state or a private field name.
        string ignored;
        if (HasTrainingSubmissionTicket("NoteMotionControl", out ignored))
            trainingNoteTicketObserved = true;
    }

    bool HasTrainingSubmissionTicket(string source, out string detail)
    {
        detail = "submission-unresolved; source=" + source;
        if (trainingSubmission == null) return false;
        try
        {
            MethodInfo ticketsMethod = AccessTools.Method(trainingSubmission.GetType(), "Tickets");
            if (ticketsMethod == null)
            {
                detail = "Submission.Tickets() unavailable; source=" + source;
                return false;
            }
            object tickets = ticketsMethod.Invoke(trainingSubmission, null);
            if (!(tickets is IEnumerable values))
            {
                detail = "Submission.Tickets() returned no enumerable; source=" + source;
                return false;
            }

            List<string> actual = new List<string>();
            foreach (object ticket in values)
            {
                string ticketSource = Convert.ToString(GetMember(ticket, "Source"), CultureInfo.InvariantCulture) ?? string.Empty;
                Assembly ticketAssembly = GetMember(ticket, "Assembly") as Assembly;
                string identity = ticketSource + (ticketAssembly == null ? string.Empty : "@" + ticketAssembly.GetName().Name);
                actual.Add(identity);
                if (string.Equals(ticketSource, source, StringComparison.Ordinal))
                {
                    detail = "source=" + source + "; tickets=" + String.Join(",", actual);
                    return true;
                }
            }
            detail = "source=" + source + "; tickets=" + String.Join(",", actual);
        }
        catch (Exception error)
        {
            detail = "source=" + source + "; " + error.GetType().Name + ": " + error.Message;
        }
        return false;
    }

    static int CountTrainingScoringObjects(object beatmap)
    {
        try
        {
            // Includes Burst slices before their NoteData are spawned during play.
            int nativeCount = ReadInt(beatmap, "cuttableScoringObjectsCount", -1);
            if (nativeCount >= 0) return nativeCount;
            object items = GetMember(beatmap, "allBeatmapDataItems");
            if (!(items is IEnumerable values)) return -1;
            int count = 0;
            foreach (object item in values)
            {
                if (!(item is NoteData)) continue;
                object scoring = GetMember(item, "scoringType");
                if (scoring == null) return -1;
                string name = scoring.ToString();
                if (!string.Equals(name, "Ignore", StringComparison.Ordinal) &&
                    !string.Equals(name, "NoScore", StringComparison.Ordinal)) count++;
            }
            return count;
        }
        catch
        {
            return -1;
        }
    }

    static bool TryComputeTrainingMaxScore(object beatmap, out int score, out string detail)
    {
        score = -1;
        detail = "ScoreModel unavailable";
        try
        {
            Type scoreType = FindTrainingType("ScoreModel");
            if (scoreType == null) return false;
            MethodInfo method = scoreType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(value => value.Name == "ComputeMaxMultipliedScoreForBeatmap" &&
                    value.GetParameters().Length == 1 &&
                    value.GetParameters()[0].ParameterType.IsInstanceOfType(beatmap));
            if (method == null)
            {
                detail = "ComputeMaxMultipliedScoreForBeatmap overload unavailable";
                return false;
            }
            score = Convert.ToInt32(method.Invoke(null, new[] { beatmap }), CultureInfo.InvariantCulture);
            detail = "method=" + method.DeclaringType.FullName;
            return score >= 0;
        }
        catch (Exception error)
        {
            detail = error.GetType().Name + ": " + error.Message;
            return false;
        }
    }

    void CaptureEnergyGameplay()
    {
        Type storeType = FindTrainingType("ActivityEnergyCounter.EnergySnapshotStore");
        trainingEnergySession = ReadLong(GetStaticMember(storeType, "CurrentSession"), -1);
        trainingEnergyInitialSnapshotPresent = GetStaticMember(storeType, "Latest") != null;
        trainingEnergyBeatmapKey = ReadActiveEnergyBeatmapKey();
        trainingEnergyResultExpected = duration <= 0f;
        trainingEnergyResultCheckScheduled = false;
        trainingEnergyResultObserved = false;
        trainingEnergyHudSeen = false;
        trainingEnergyHudFinite = false;
        trainingEnergySnapshotSeen = false;
        trainingEnergySnapshotFinite = false;
        trainingEnergyTimeMonotonic = true;
        trainingEnergyTimeAdvanced = false;
        trainingEnergyLastEquivalent = -1;
        trainingEnergyLastPlaying = -1;
        trainingEnergyObservations = 0;
        if (trainingEnergyResultExpected) InstallTrainingResultsProbe();
    }

    IEnumerator VerifyTrainingPause(Zenject.SceneContext context)
    {
        if (!trainingEnergyLoaded) yield break;
        Type recorder = FindTrainingType("ActivityEnergyCounter.EnergyRecorder");
        object recorderInstance = GetStaticMember(recorder, "Current");
        yield return new WaitForSecondsRealtime(1f);
        yield return VerifyEnergyHudToggleIsolation();
        var pause = context.Container.Resolve<IGamePause>();
        pause.Pause();
        yield return null;
        object before = ReadRecorderMember(recorder, recorderInstance, "CurrentSnapshot");
        double playing = ReadDouble(before, "ValidPlayingSeconds", double.NaN);
        double active = ReadDouble(before, "ActiveKiloJoules", double.NaN);
        yield return new WaitForSecondsRealtime(2f);
        object after = ReadRecorderMember(recorder, recorderInstance, "CurrentSnapshot");
        Type translator = FindTrainingType("ModUiChinese.UiTranslator");
        object engine = GetStaticMember(translator, "Engine");
        if (engine != null && FindTrainingType("RoomCalibration.GameContext") != null)
        {
            string expected = (string)AccessTools.Method(engine.GetType(), "Translate").Invoke(engine, new object[] { "Calibrate to Current Position", "RoomCalibration" });
            bool found = Resources.FindObjectsOfTypeAll<TMP_Text>().Any(text => text != null && text.text == expected && text.transform.parent != null && string.Join("/", Parents(text.transform)).Contains("RoomCalibration.PauseButtons"));
            Check("training-pause-calibration-localized", found, "expected=" + expected);
        }
        Check("training-energy-pause-excludes-time", pause.isPaused && playing > 0 && ReadDouble(after, "ValidPlayingSeconds", -1) == playing,
            "playingSeconds=" + playing + "; after=" + ReadDouble(after, "ValidPlayingSeconds", -1) + "; paused=" + pause.isPaused + "; pausedFor=2s");
        Check("training-energy-pause-excludes-energy", IsFiniteTraining(active) && ReadDouble(after, "ActiveKiloJoules", -1) == active,
            "activeKJ=" + active);
        pause.Resume();
        yield return Wait(() => !pause.isPaused && ReadDouble(ReadRecorderMember(recorder, recorderInstance, "CurrentSnapshot"), "ValidPlayingSeconds", -1) > playing + 0.1, 10, "training-energy-resumed");
        Check("training-energy-resume-continues-time", true, "Valid time resumed without adding the paused interval");
    }

    IEnumerator VerifyTrainingRestart(Zenject.SceneContext previous, Action<Zenject.SceneContext> accept)
    {
        if (!trainingNoteLoaded) { accept(previous); yield break; }
        Type store = trainingEnergyLoaded ? FindTrainingType("ActivityEnergyCounter.EnergySnapshotStore") : null;
        Type runtime = FindTrainingType("NoteMotionControl.TrainingLoad");
        object outcome = GetStaticMember(runtime, "LastOutcome");
        int original = ReadInt(outcome, "OriginalScoringObjects", -1);
        int filtered = ReadInt(outcome, "FilteredScoringObjects", -1);
        long previousSession = store == null ? -1 : ReadLong(GetStaticMember(store, "CurrentSession"), -1);
        object previousProvider = previous.Container.TryResolve<IVariableMovementDataProvider>();
        yield return new WaitForSecondsRealtime(1f);
        Active<PauseController>().Pause();
        yield return Wait(() => Active<PauseMenuManager>() != null, 5, "training-restart-menu");
        yield return new WaitForSecondsRealtime(1f);
        var restart = (UnityEngine.UI.Button)AccessTools.Field(typeof(PauseMenuManager), "_restartButton").GetValue(Active<PauseMenuManager>());
        restart.onClick.Invoke();
        Zenject.SceneContext current = null;
        yield return Wait(() => {
            current = Resources.FindObjectsOfTypeAll<Zenject.SceneContext>().FirstOrDefault(value => value != null && value != previous && value.gameObject.scene.name == "GameCore" && value.gameObject.activeInHierarchy);
            return current != null && Active<AudioTimeSyncController>() != null &&
                (store == null || ReadLong(GetStaticMember(store, "CurrentSession"), -1) > previousSession);
        }, 45, "training-native-restart");
        // SceneContext and the session ticket can exist before the spawn
        // controller has completed Init. Its native callbacks must be ready
        // before any post-restart capture or gameplay assertion runs.
        yield return Wait(() => {
            object provider = current.Container.TryResolve<IVariableMovementDataProvider>();
            return provider != null && Resources.FindObjectsOfTypeAll<BeatmapObjectSpawnController>().Any(spawn =>
                spawn != null && spawn.gameObject.activeInHierarchy &&
                ReferenceEquals(GetMember(spawn, "_variableMovementDataProvider"), provider) &&
                GetMember(spawn, "_noteDataCallbackWrapper") != null &&
                GetMember(spawn, "_sliderDataCallbackWrapper") != null &&
                GetMember(spawn, "_obstacleDataCallbackWrapper") != null &&
                ReadFloat(provider, "spawnAheadTime", 0f) > 0f);
        },
            15, "training-native-restart-spawn-initialized");
        object repeated = GetStaticMember(runtime, "LastOutcome");
        if (trainingScenario == "Percentage")
            Check("training-reduction-restart-does-not-reduce-twice", original > 0 && filtered > 0 && ReadInt(repeated, "OriginalScoringObjects", -1) == original && ReadInt(repeated, "FilteredScoringObjects", -1) == filtered,
                "original=" + original + "; filtered=" + filtered + "; repeated=" + ReadInt(repeated, "FilteredScoringObjects", -1));
        if (store != null)
            Check("training-energy-restart-new-session", ReadLong(GetStaticMember(store, "CurrentSession"), -1) > previousSession,
                "previous=" + previousSession + "; current=" + GetStaticMember(store, "CurrentSession"));
        if (trainingNoteLoaded)
        {
            Type motion = FindTrainingType("NoteMotionControl.RuntimeState");
            object setup = current.Container.Resolve<GameplayCoreSceneSetupData>();
            yield return Wait(() => ReferenceEquals(GetStaticMember(motion, "ActiveSetup"), setup) &&
                ReadBool(GetStaticMember(motion, "GameplayActive"), string.Empty, false),
                10, "training-note-motion-restart-session-ready");
            Check("training-note-motion-restart-new-owner", ReferenceEquals(GetStaticMember(motion, "ActiveSetup"), setup) &&
                ReadBool(GetStaticMember(motion, "GameplayActive"), string.Empty, false),
                "The restarted GameCore owns the active note-motion session.");
            object restartedProvider = current.Container.TryResolve<IVariableMovementDataProvider>();
            var policies = GetStaticMember(FindTrainingType("NoteMotionControl.SpeedRuntime"), "policies") as IDictionary;
            Check("training-note-speed-restart-clears-old-provider", restartedProvider != null &&
                !ReferenceEquals(previousProvider, restartedProvider) && policies != null &&
                (previousProvider == null || !policies.Contains(previousProvider)),
                "A fresh player provider owns the restarted song; no previous provider policy remains.");
        }
        accept(current);
    }

    void ObserveEnergy(float songTime)
    {
        trainingEnergyObservations++;
        TMP_Text hud;
        double hudValue;
        string hudText;
        if (TryFindEnergyHud(out hud, out hudValue, out hudText))
        {
            trainingEnergyHudSeen = true;
            trainingEnergyHudText = hudText;
            trainingEnergyHudFinite = !double.IsNaN(hudValue) && !double.IsInfinity(hudValue) && hudValue >= 0;
        }

        Type recorderType = FindTrainingType("ActivityEnergyCounter.EnergyRecorder");
        object snapshot = ReadRecorderMember(recorderType, GetStaticMember(recorderType, "Current"), "CurrentSnapshot");
        if (snapshot == null) return;
        if (string.IsNullOrEmpty(trainingEnergyBeatmapKey))
            trainingEnergyBeatmapKey = Convert.ToString(GetMember(snapshot, "BeatmapKey"), CultureInfo.InvariantCulture) ?? string.Empty;
        bool firstSnapshot = !trainingEnergySnapshotSeen;
        trainingEnergySnapshotSeen = true;
        double equivalent = ReadDouble(snapshot, "EquivalentSeconds", double.NaN);
        double playing = ReadDouble(snapshot, "ValidPlayingSeconds", double.NaN);
        double active = ReadDouble(snapshot, "ActiveKiloJoules", double.NaN);
        double gross = ReadDouble(snapshot, "GrossKiloJoules", double.NaN);
        bool finite = IsFiniteTraining(equivalent) && IsFiniteTraining(playing) &&
                      IsFiniteTraining(active) && IsFiniteTraining(gross) &&
                      equivalent >= 0 && playing >= 0 && active >= 0 && gross >= 0;
        trainingEnergySnapshotFinite = firstSnapshot ? finite : trainingEnergySnapshotFinite && finite;
        if (trainingEnergyLastEquivalent >= 0 && equivalent + 0.0001 < trainingEnergyLastEquivalent)
            trainingEnergyTimeMonotonic = false;
        if (trainingEnergyLastPlaying >= 0 && playing + 0.0001 < trainingEnergyLastPlaying)
            trainingEnergyTimeMonotonic = false;
        if (trainingEnergyLastEquivalent >= 0 && equivalent > trainingEnergyLastEquivalent + 0.0001)
            trainingEnergyTimeAdvanced = true;
        if (trainingEnergyLastPlaying >= 0 && playing > trainingEnergyLastPlaying + 0.0001)
            trainingEnergyTimeAdvanced = true;
        trainingEnergyLastEquivalent = equivalent;
        trainingEnergyLastPlaying = playing;
    }

    void FinishEnergyGameplay()
    {
        // The net activity value may legitimately remain zero in FPFC.  The
        // playing-time accumulator must still advance, which is the stable
        // runtime signal that no fake VR pose was injected by this check.
        Check("training-energy-hud-numeric", trainingEnergyHudSeen && trainingEnergyHudFinite,
            "seen=" + trainingEnergyHudSeen + "; finite=" + trainingEnergyHudFinite + "; text=" + trainingEnergyHudText);
        Check("training-energy-latest-finite", trainingEnergySnapshotSeen && trainingEnergySnapshotFinite,
            "seen=" + trainingEnergySnapshotSeen + "; finite=" + trainingEnergySnapshotFinite);
        Check("training-energy-latest-time-monotonic", trainingEnergyTimeMonotonic,
            "equivalentSeconds=" + trainingEnergyLastEquivalent + "; validPlayingSeconds=" + trainingEnergyLastPlaying);
        Check("training-energy-latest-time-advanced", trainingEnergyTimeAdvanced,
            "observations=" + trainingEnergyObservations + "; validPlayingSeconds=" + trainingEnergyLastPlaying);
        long currentSession = ReadLong(GetStaticMember(FindTrainingType("ActivityEnergyCounter.EnergySnapshotStore"), "CurrentSession"), -1);
        Check("training-energy-new-session-isolated", currentSession == trainingEnergySession,
            "CurrentSession=" + currentSession + "; captured=" + trainingEnergySession +
            "; initialLatest=" + trainingEnergyInitialSnapshotPresent);
        Check("training-energy-session-stable", currentSession == trainingEnergySession,
            "captured=" + trainingEnergySession + "; current=" + currentSession);
    }

    void InstallTrainingResultsProbe()
    {
        Type resultsType = FindTrainingType("ResultsViewController");
        MethodInfo setData = resultsType == null ? null : AccessTools.Method(resultsType, "SetDataToUI");
        if (setData == null)
        {
            Check("training-energy-results-hook", false, "ResultsViewController.SetDataToUI is unavailable.");
            return;
        }

        try
        {
            TrainingResultsProbe.Owner = this;
            trainingResultHarmony = new Harmony("EffectModsSmoke.TrainingResults");
            HarmonyMethod postfix = new HarmonyMethod(
                typeof(TrainingResultsProbe), nameof(TrainingResultsProbe.Postfix));
            postfix.priority = Priority.Last;
            postfix.after = new[] { "local.beatsaber.activity-energy-counter" };
            trainingResultHarmony.Patch(setData, postfix: postfix);
        }
        catch (Exception error)
        {
            TrainingResultsProbe.Owner = null;
            trainingResultHarmony = null;
            Check("training-energy-results-hook", false, error.GetType().Name + ": " + error.Message);
        }
    }

    void QueueTrainingResultsCheck(object view)
    {
        if (!trainingEnergyResultExpected || trainingEnergyResultObserved || trainingEnergyResultCheckScheduled) return;
        trainingEnergyResultCheckScheduled = true;
        StartCoroutine(VerifyTrainingResultsAfterFrame(view));
    }

    IEnumerator VerifyTrainingResultsAfterFrame(object view)
    {
        yield return null;
        try
        {
            InspectTrainingResults(view);
        }
        catch (Exception error)
        {
            errors.Add("training-results: " + error);
        }
        finally
        {
            trainingEnergyResultCheckScheduled = false;
            UninstallTrainingResultsProbe();
        }
    }

    void InspectTrainingResults(object view)
    {
        if (trainingEnergyResultObserved) return;
        trainingEnergyResultObserved = true;
        Transform root = GetMember(view, "transform") as Transform;
        Transform result = root?.Find("ActivityEnergyCounter.ResultEnergy");
        TMP_Text label = result == null ? null : result.GetComponentInChildren<TMP_Text>(true);
        double value;
        bool numeric = label != null && TryParseEnergyHud(label.text, out value) &&
                       !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;
        Check("training-energy-results-label", numeric,
            "label=" + (label == null ? "missing" : label.text));

        bool scoped = result != null;
        Check("training-energy-results-localization-scope", scoped,
            "ResultEnergy=" + (result == null ? "missing" : result.name));

        Type storeType = FindTrainingType("ActivityEnergyCounter.EnergySnapshotStore");
        Type recorderType = FindTrainingType("ActivityEnergyCounter.EnergyRecorder");
        object recorderSnapshot = ReadRecorderMember(recorderType, GetStaticMember(recorderType, "Current"), "CurrentSnapshot") ??
            GetStaticMember(storeType, "Latest");
        long currentSession = ReadLong(GetStaticMember(storeType, "CurrentSession"), -1);
        string snapshotKey = Convert.ToString(GetMember(recorderSnapshot, "BeatmapKey"), CultureInfo.InvariantCulture) ?? string.Empty;
        bool keyMatches = !string.IsNullOrEmpty(snapshotKey) &&
                          (string.IsNullOrEmpty(trainingEnergyBeatmapKey) ||
                           string.Equals(snapshotKey, trainingEnergyBeatmapKey, StringComparison.Ordinal));
        bool sessionMatches = currentSession == trainingEnergySession;
        Check("training-energy-results-snapshot-isolated", recorderSnapshot != null && keyMatches && sessionMatches,
            "snapshot=" + snapshotKey + "; expected=" + trainingEnergyBeatmapKey +
            "; session=" + currentSession + "/" + trainingEnergySession);
        if (trainingNoteTicketObserved)
        {
            object completion = GetMember(view, "_levelCompletionResults");
            object allowed = GetMember(completion, "ShouldSubmitScores");
            Check("training-modified-results-score-submission-disabled", allowed is bool && !(bool)allowed,
                "resultType=" + completion?.GetType().FullName + "; ShouldSubmitScores=" + allowed);
        }
    }

    void UninstallTrainingResultsProbe()
    {
        if (trainingResultHarmony != null)
        {
            try { trainingResultHarmony.UnpatchSelf(); }
            catch (Exception error) { errors.Add("training-results-unpatch: " + error); }
        }
        trainingResultHarmony = null;
        if (ReferenceEquals(TrainingResultsProbe.Owner, this)) TrainingResultsProbe.Owner = null;
    }

    string ReadActiveEnergyBeatmapKey()
    {
        Type recorderType = FindTrainingType("ActivityEnergyCounter.EnergyRecorder");
        if (recorderType == null) return string.Empty;
        try
        {
            object snapshot = ReadRecorderMember(recorderType, GetStaticMember(recorderType, "Current"), "CurrentSnapshot");
            string key = Convert.ToString(GetMember(snapshot, "BeatmapKey"), CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(key)) return key;
        }
        catch { }
        return string.Empty;
    }

    static bool TryFindEnergyHud(out TMP_Text hud, out double value, out string text)
    {
        hud = null;
        value = double.NaN;
        text = string.Empty;
        try
        {
            foreach (TMP_Text candidate in Resources.FindObjectsOfTypeAll<TMP_Text>())
            {
                if (candidate == null || !candidate.gameObject.activeInHierarchy) continue;
                if (TryParseEnergyHud(candidate.text, out value)) { hud = candidate; text = candidate.text; return true; }
            }
        }
        catch
        {
            // The scene may destroy a counter between FindObjectsOfTypeAll and
            // reading its label.  The next gameplay observation retries it.
        }
        return false;
    }

    static bool TryParseEnergyHud(string input, out double value)
    {
        value = double.NaN;
        if (string.IsNullOrEmpty(input) ||
            !ContainsAnyEnergyLabel(input)) return false;
        Match match = Regex.Match(input, @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)\s*(?:kJ|kcal|千焦|千卡)\b?", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        string numeric = Regex.Match(match.Value, @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)", RegexOptions.CultureInvariant).Value;
        return double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    static bool ContainsAnyEnergyLabel(string input)
    {
        return new[] { "Energy", "estimated", "Song", "Session", "Today", "total", "估算", "耗能", "本曲", "本次", "本日", "今日", "歌曲" }
            .Any(label => input.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    static object GetStaticMember(Type type, string name)
    {
        if (type == null || string.IsNullOrEmpty(name)) return null;
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
            PropertyInfo property = type.GetProperty(name, flags);
            if (property != null && property.GetIndexParameters().Length == 0) return property.GetValue(null, null);
            FieldInfo field = type.GetField(name, flags);
            return field?.GetValue(null);
        }
        catch
        {
            return null;
        }
    }

    static object GetMember(object value, string name)
    {
        if (value == null || string.IsNullOrEmpty(name)) return null;
        if (value is IDictionary dictionary && dictionary.Contains(name)) return dictionary[name];
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type type = value.GetType();
            PropertyInfo property = type.GetProperty(name, flags);
            if (property != null && property.GetIndexParameters().Length == 0) return property.GetValue(value, null);
            FieldInfo field = type.GetField(name, flags);
            return field?.GetValue(value);
        }
        catch
        {
            return null;
        }
    }

    static bool ReadBool(object value, string name, bool fallback)
    {
        if (string.IsNullOrEmpty(name))
        {
            if (value is bool direct) return direct;
            return fallback;
        }
        object member = GetMember(value, name);
        return member is bool result ? result : fallback;
    }

    static int ReadInt(object value, string name, int fallback)
    {
        if (string.IsNullOrEmpty(name))
        {
            try { return value == null ? fallback : Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }
        object member = GetMember(value, name);
        try { return member == null ? fallback : Convert.ToInt32(member, CultureInfo.InvariantCulture); }
        catch { return fallback; }
    }

    static float ReadFloat(object value, string name, float fallback)
    {
        if (string.IsNullOrEmpty(name))
        {
            if (value is float direct) return direct;
            try { return value == null ? fallback : Convert.ToSingle(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }
        object member = GetMember(value, name);
        try { return member == null ? fallback : Convert.ToSingle(member, CultureInfo.InvariantCulture); }
        catch { return fallback; }
    }

    static long ReadLong(object value, long fallback)
    {
        try { return value == null ? fallback : Convert.ToInt64(value, CultureInfo.InvariantCulture); }
        catch { return fallback; }
    }

    static double ReadDouble(object value, string name, double fallback)
    {
        object member = GetMember(value, name);
        try { return member == null ? fallback : Convert.ToDouble(member, CultureInfo.InvariantCulture); }
        catch { return fallback; }
    }

    static Vector3 ReadVector3(object value, string name)
    {
        object member = GetMember(value, name);
        return member is Vector3 result ? result : new Vector3(float.NaN, float.NaN, float.NaN);
    }

    static Quaternion ReadQuaternion(object value, string name)
    {
        object member = GetMember(value, name);
        return member is Quaternion result ? result : new Quaternion(float.NaN, float.NaN, float.NaN, float.NaN);
    }

    static bool IsFiniteTraining(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    static bool IsFiniteTraining(Vector3 value)
    {
        return IsFiniteTraining(value.x) && IsFiniteTraining(value.y) && IsFiniteTraining(value.z);
    }

    static bool IsFiniteTraining(Quaternion value)
    {
        return IsFiniteTraining(value.x) && IsFiniteTraining(value.y) && IsFiniteTraining(value.z) && IsFiniteTraining(value.w);
    }
}
