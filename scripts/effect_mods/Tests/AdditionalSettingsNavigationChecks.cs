using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace EffectModsSmoke;

public sealed partial class Runner
{
    const string AdditionalNoteTrainingHost = "NoteMotionControl.SettingsMenu";
    const string AdditionalSongChartHost = "SongChartVisualizer.UI.ViewControllers.SettingsController";
    readonly List<object> additionalNavigationCases = new();
    static readonly string[] AdditionalInstantHosts = {
        AdditionalNoteTrainingHost,
        "ModUiChinese.LocalizationPanel", "RoomCalibration.SettingsPanel",
        "ActivityEnergyCounter.EnergySettingsHost", "Vivify.Settings.SettingsMenu",
        AdditionalSongChartHost
    };
    static readonly string[] AdditionalNativeHosts = {
        "ParticleOverdrive.UI.POUI", "SongCore.UI.SettingsController", "AssetBundleLoadingTools.UI.SettingsHost"
    };
    static readonly string[] AdditionalChartVectors = {
        "ChartStandardLevelPosition", "ChartStandardLevelRotation", "Chart360LevelPosition", "Chart360LevelRotation"
    };
    static readonly string[] AdditionalChartDrafts = { "_stdPos", "_stdRot", "_noStdPos", "_noStdRot" };
    static readonly string[] AdditionalChartFields = { "StdPosXField", "StdRotXField", "NoStdPosXField", "NoStdRotXField" };

    sealed class AdditionalNavigationContext
    {
        internal SettingsNavigationContext Navigation;
        internal readonly Dictionary<string, object> Entries = new();
        internal readonly List<AdditionalNavigationEvents> Events = new();
        internal readonly List<AdditionalNavigationValue> Values = new();
        internal object ChartHost, ChartConfig;
        internal Vector3[] ChartOriginal;
    }
    sealed class AdditionalNavigationEvents
    {
        internal string Name;
        internal IDictionary Dictionary;
        internal bool HadApply, HadCancel;
        internal object OriginalApply, OriginalCancel;
        internal int Apply, Cancel;
    }
    sealed class AdditionalNavigationValue
    {
        internal string HostType, Property, Path, JsonPath;
        internal object Host, Original;
        internal Func<object> ReadState;
    }
    static class AdditionalNavigationRestartProbe
    {
        internal static bool Allow;
        internal static int Calls, Executed;
        internal static bool Prefix()
        {
            Calls++;
            if (Allow) Executed++;
            return Allow;
        }
    }

    IEnumerator VerifyAdditionalSettingsNavigation()
    {
        var harmony = new Harmony("localtraining.smoke.additional-settings-navigation");
        var restart = AccessTools.Method(typeof(MenuTransitionsHelper), "RestartGame");
        Check("additional-navigation-restart-interface", restart != null, "Original MenuTransitionsHelper.RestartGame is available.");
        AdditionalNavigationRestartProbe.Allow = false;
        AdditionalNavigationRestartProbe.Calls = AdditionalNavigationRestartProbe.Executed = 0;
        harmony.Patch(restart, new HarmonyMethod(typeof(AdditionalNavigationRestartProbe), nameof(AdditionalNavigationRestartProbe.Prefix)));
        AdditionalNavigationContext context = null;
        try
        {
            context = CreateAdditionalNavigationContext();
            VerifySettingsNavigationPatchOwnership();
            yield return AdditionalNavigationTrainingContract(context);
            foreach (var value in context.Values)
            {
                yield return AdditionalNavigationImmediateCase(context, value, true, "confirm");
                yield return AdditionalNavigationImmediateCase(context, value, false, "return");
            }
            yield return AdditionalNavigationChartCase(context, true);
            yield return AdditionalNavigationChartCase(context, false);
            yield return VerifyAdditionalSettingsColors(context);

            // Each of these real hosts has initialization or cached-resource
            // dependencies. Merely returning to an instant page cannot erase
            // the session's obligation to run native global Apply + restart.
            foreach (string hostType in AdditionalNativeHosts)
            {
                yield return SettingsNavigationPresent(context.Navigation);
                yield return SettingsNavigationOpen(context.Navigation, context.Entries[hostType]);
                yield return SettingsNavigationOpen(context.Navigation, context.Entries[AdditionalNoteTrainingHost]);
                yield return AdditionalNavigationNativeFallback(context, hostType);
            }

            RestoreAdditionalNavigationValues(context);
            yield return AdditionalNavigationSavedAll(context, "before-real-restart");
            var oldHosts = context.Entries.Where(pair => AdditionalInstantHosts.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => GetMember(pair.Value, "Host"));
            var oldMain = SettingsNavigationMainContext();
            int process = System.Diagnostics.Process.GetCurrentProcess().Id;
            int calls = AdditionalNavigationRestartProbe.Calls;
            int apply = context.Navigation.Apply;
            yield return SettingsNavigationPresent(context.Navigation);
            yield return SettingsNavigationOpen(context.Navigation, context.Navigation.Probe);
            yield return SettingsNavigationOpen(context.Navigation, context.Entries[AdditionalNoteTrainingHost]);
            AdditionalNavigationRestartProbe.Allow = true;
            SettingsNavigationClick(context.Navigation, true, "additional-external-real-restart");
            Check("additional-navigation-real-restart-global-apply", context.Navigation.Apply == apply + 1,
                "The actual external-page session used native global Apply.");
            yield return Wait(() => SettingsNavigationMainContext() != null && SettingsNavigationMainContext() != oldMain &&
                Active<MainMenuViewController>() != null && Active<MainMenuViewController>().isActivated,
                85f, "additional-navigation-real-soft-restart");
            Check("additional-navigation-real-restart", AdditionalNavigationRestartProbe.Executed == 1 &&
                AdditionalNavigationRestartProbe.Calls == calls + 1 && System.Diagnostics.Process.GetCurrentProcess().Id == process,
                "One original restart rebuilt MainMenu in the same process.");
            additionalNavigationCases.Add(new { name = "external-real-restart", processId = process,
                nativeApply = context.Navigation.Apply - apply, restartExecuted = AdditionalNavigationRestartProbe.Executed });
            AdditionalNavigationRestartProbe.Allow = false;
            DisposeAdditionalNavigationContext(context);
            context = null;

            // Resolve native menu registrations, parser instances and hosts
            // after teardown; stale scene registrations must not be reusable.
            yield return Wait(() => FindBsmlSettingsInstance(FindTrainingType("BeatSaberMarkupLanguage.Settings.BSMLSettings")) != null,
                20f, "additional-navigation-reloaded-settings");
            context = CreateAdditionalNavigationContext();
            VerifySettingsNavigationPatchOwnership();
            foreach (string hostType in AdditionalInstantHosts)
                Check("additional-navigation-fresh-host-" + hostType,
                    !ReferenceEquals(oldHosts[hostType], GetMember(context.Entries[hostType], "Host")),
                    "The host was resolved from the new MainMenu registration.");
            foreach (var value in context.Values.Where(item => item.HostType != AdditionalNoteTrainingHost))
                yield return AdditionalNavigationImmediateCase(context, value, true, "after-native-restart");
            yield return AdditionalNavigationChartCase(context, true);
            RestoreAdditionalNavigationValues(context);
            yield return AdditionalNavigationSavedAll(context, "final-restore");
        }
        finally
        {
            AdditionalNavigationRestartProbe.Allow = false;
            harmony.UnpatchSelf();
            if (context != null) RestoreAdditionalNavigationValues(context);
            DisposeAdditionalNavigationContext(context);
            File.WriteAllText(Path.Combine(directory, "additional-settings-navigation.json"), JsonConvert.SerializeObject(new {
                processId = System.Diagnostics.Process.GetCurrentProcess().Id, realNativeRestarts = AdditionalNavigationRestartProbe.Executed,
                restartCalls = AdditionalNavigationRestartProbe.Calls, cases = additionalNavigationCases
            }, Formatting.Indented));
        }
    }

    AdditionalNavigationContext CreateAdditionalNavigationContext()
    {
        var result = new AdditionalNavigationContext();
        object settings = FindBsmlSettingsInstance(FindTrainingType("BeatSaberMarkupLanguage.Settings.BSMLSettings"));
        result.Navigation = new SettingsNavigationContext { Settings = settings, Flow = GetMember(settings, "modSettingsFlowCoordinator"),
            About = GetMember(settings, "settingsMenu"), OriginalEntries = ((IEnumerable)GetMember(settings, "SettingsMenus")).Cast<object>().ToArray() };
        try
        {
            foreach (string hostType in AdditionalInstantHosts.Concat(AdditionalNativeHosts))
            {
                object entry = result.Navigation.OriginalEntries.SingleOrDefault(item => GetMember(item, "Host")?.GetType().FullName == hostType);
                Check("additional-navigation-entry-" + hostType, entry != null, "Native SettingsMenus contains " + hostType);
                EnsureSettingsMenuSetup(entry);
                result.Entries.Add(hostType, entry);
                AttachAdditionalNavigationEvents(result, hostType, GetMember(entry, "ParserParams"));
            }
            EnsureSettingsMenuSetup(result.Navigation.About);
            var probeHost = new SettingsNavigationHost();
            result.Navigation.Probe = Activator.CreateInstance(FindTrainingType("BeatSaberMarkupLanguage.Settings.SettingsMenu"),
                new object[] { "Additional navigation external probe", "unused-additional-smoke-resource", probeHost, typeof(Runner).Assembly });
            object parser = AccessTools.Constructor(FindTrainingType("BeatSaberMarkupLanguage.Parser.BSMLParserParams"), new[] { typeof(object) }).Invoke(new object[] { probeHost });
            result.Navigation.ProbeView = SettingsNavigationCreateView("Additional external navigation probe");
            AccessTools.Method(parser.GetType(), "AddEvent").Invoke(parser, new object[] { "apply", (Action)(() => result.Navigation.Apply++) });
            AccessTools.Method(parser.GetType(), "AddEvent").Invoke(parser, new object[] { "cancel", (Action)(() => result.Navigation.Cancel++) });
            AccessTools.Property(result.Navigation.Probe.GetType(), "ViewController").SetValue(result.Navigation.Probe, result.Navigation.ProbeView, null);
            AccessTools.Property(result.Navigation.Probe.GetType(), "ParserParams").SetValue(result.Navigation.Probe, parser, null);
            SettingsNavigationReplaceEntries(settings, new[] { result.Navigation.About }.Concat(result.Entries.Values).Concat(new[] { result.Navigation.Probe }).ToArray());
            PopulateAdditionalNavigationValues(result);
            result.ChartHost = GetMember(result.Entries[AdditionalSongChartHost], "Host");
            result.ChartConfig = GetMember(result.ChartHost, "_configuration");
            result.ChartOriginal = AdditionalChartVectors.Select(name => (Vector3)GetMember(result.ChartConfig, name)).ToArray();
            return result;
        }
        catch { DisposeAdditionalNavigationContext(result); throw; }
    }

    static void AttachAdditionalNavigationEvents(AdditionalNavigationContext context, string name, object parser)
    {
        var item = new AdditionalNavigationEvents { Name = name, Dictionary = GetMember(parser, "events") as IDictionary };
        if (item.Dictionary == null) throw new MissingMemberException("BSMLParserParams.events");
        item.HadApply = item.Dictionary.Contains("apply"); item.HadCancel = item.Dictionary.Contains("cancel");
        item.OriginalApply = item.HadApply ? item.Dictionary["apply"] : null;
        item.OriginalCancel = item.HadCancel ? item.Dictionary["cancel"] : null;
        AccessTools.Method(parser.GetType(), "AddEvent").Invoke(parser, new object[] { "apply", (Action)(() => item.Apply++) });
        AccessTools.Method(parser.GetType(), "AddEvent").Invoke(parser, new object[] { "cancel", (Action)(() => item.Cancel++) });
        context.Events.Add(item);
    }

    static void DisposeAdditionalNavigationContext(AdditionalNavigationContext context)
    {
        if (context == null) return;
        foreach (var item in context.Events)
        {
            if (item.HadApply) item.Dictionary["apply"] = item.OriginalApply; else item.Dictionary.Remove("apply");
            if (item.HadCancel) item.Dictionary["cancel"] = item.OriginalCancel; else item.Dictionary.Remove("cancel");
        }
        DisposeSettingsNavigationContext(context.Navigation);
    }

    void PopulateAdditionalNavigationValues(AdditionalNavigationContext context)
    {
        string root = Path.Combine(Path.GetDirectoryName(Application.dataPath), "UserData");
        AddAdditionalNavigationValue(context, AdditionalNoteTrainingHost, "OverrideNjs", Path.Combine(root, "NoteMotionControl.json"), "Settings.OverrideNjs",
            () => GetMember(GetMember(GetStaticMember(FindTrainingType("NoteMotionControl.RuntimeState"), "Store"), "Global"), "OverrideNjs"));
        AddAdditionalNavigationValue(context, AdditionalInstantHosts[1], "Enabled", Path.Combine(root, "ModUiChinese.json"), "Global.Enabled",
            () => GetMember(GetStaticMember(FindTrainingType("ModUiChinese.LocalizationState"), "ActiveOptions"), "Enabled"));
        object roomHost = GetMember(context.Entries[AdditionalInstantHosts[2]], "Host");
        AddAdditionalNavigationValue(context, AdditionalInstantHosts[2], "PreparationSeconds", Path.Combine(root, "当前位置校准.json"), "PreparationSeconds",
            () => GetMember(GetMember(roomHost, "config"), "PreparationSeconds"));
        AddAdditionalNavigationValue(context, AdditionalInstantHosts[3], "BodyMassKg", Path.Combine(root, "ActivityEnergyCounter.json"), "Global.BodyMassKg",
            () => GetMember(GetStaticMember(FindTrainingType("ActivityEnergyCounter.Plugin"), "Settings"), "BodyMassKg"));
        object vivifyHost = GetMember(context.Entries[AdditionalInstantHosts[4]], "Host");
        AddAdditionalNavigationValue(context, AdditionalInstantHosts[4], "MaxCamera2Cams", Path.Combine(root, "Vivify.json"), "MaxCamera2Cams",
            () => GetMember(GetMember(vivifyHost, "_config"), "MaxCamera2Cams"));
    }

    IEnumerator AdditionalNavigationTrainingContract(AdditionalNavigationContext context)
    {
        object entry = context.Entries[AdditionalNoteTrainingHost];
        object host = GetMember(entry, "Host");
        object store = GetStaticMember(FindTrainingType("NoteMotionControl.RuntimeState"), "Store");
        object original = GetMember(store, "Global");
        string path = Path.Combine(Path.GetDirectoryName(Application.dataPath), "UserData", "NoteMotionControl.json");
        string[] expectedPresets = { "Original", "Entry", "Easy", "Normal", "Hard", "Half", "Stable", "Early", "Custom" };
        try
        {
            object choices = GetMember(host, "Presets");
            string[] actual = choices is IEnumerable values
                ? values.Cast<object>().Select(value => Convert.ToString(value, CultureInfo.InvariantCulture)).ToArray()
                : Array.Empty<string>();
            Check("additional-navigation-training-presets", actual.SequenceEqual(expectedPresets),
                "Actual presets: " + string.Join(", ", actual));
            Check("additional-navigation-training-global-only",
                host.GetType().GetProperty("Scope") == null && host.GetType().GetProperty("SelectedScope") == null &&
                host.GetType().GetMethod("RestoreGlobal", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) == null,
                "The merged page must expose one global profile.");

            foreach (string preset in expectedPresets.Where(value => value != "Custom"))
            {
                SetTrainingMember(host, "Preset", preset);
                RefreshAdditionalNavigationControls(entry);
                yield return null;
                object saved = GetMember(store, "Global");
                Check("additional-navigation-training-preset-" + preset,
                    string.Equals(Convert.ToString(GetMember(host, "Preset"), CultureInfo.InvariantCulture), preset, StringComparison.Ordinal),
                    "Selected preset=" + GetMember(host, "Preset"));
                JObject document = JObject.Parse(File.ReadAllText(path));
                Check("additional-navigation-training-envelope-" + preset,
                    document["Settings"] is JObject && document["Global"] == null && document["ChartOverrides"] == null &&
                    saved != null,
                    document.ToString(Formatting.None));
            }

            SetTrainingMember(host, "ApplicationPolicy", "Only reduce");
            yield return null;
            CheckAdditionalTrainingPolicy(store, path, 0, "only-reduce");
            SetTrainingMember(host, "ApplicationPolicy", "Strict");
            yield return null;
            CheckAdditionalTrainingPolicy(store, path, 1, "strict");

            SetTrainingMember(host, "NjsMode", "Override");
            SetTrainingMember(host, "OverrideNjs", 11f);
            RefreshAdditionalNavigationControls(entry);
            yield return null;
            Check("additional-navigation-training-custom", string.Equals(
                Convert.ToString(GetMember(host, "Preset"), CultureInfo.InvariantCulture), "Custom", StringComparison.Ordinal),
                "Manual edit did not derive Custom.");
        }
        finally
        {
            if (store != null && original != null) SaveTrainingProfile(store, original);
            AccessTools.Method(host.GetType(), "Refresh")?.Invoke(host, null);
        }
    }

    void CheckAdditionalTrainingPolicy(object store, string path, int expected, string name)
    {
        object saved = GetMember(store, "Global");
        JObject document = JObject.Parse(File.ReadAllText(path));
        Check("additional-navigation-training-policy-" + name,
            ReadInt(saved, "ApplicationPolicy", -1) == expected &&
            (int?)document["Settings"]?["ApplicationPolicy"] == expected,
            document.ToString(Formatting.None));
    }

    static void AddAdditionalNavigationValue(AdditionalNavigationContext context, string hostType, string property, string path, string jsonPath, Func<object> readState)
    {
        object host = GetMember(context.Entries[hostType], "Host");
        if (hostType == AdditionalNoteTrainingHost)
        {
            // A scenario can replace the profile after this menu host was
            // constructed. Capture the committed value, not its unopened draft.
            (AccessTools.Method(host.GetType(), "Refresh") ?? AccessTools.Method(host.GetType(), "LoadDraft"))?.Invoke(host, null);
            RefreshAdditionalNavigationControls(context.Entries[hostType]);
        }
        context.Values.Add(new AdditionalNavigationValue { HostType = hostType, Host = host, Property = property, Path = path,
            JsonPath = jsonPath, Original = GetMember(host, property), ReadState = readState });
    }

    IEnumerator AdditionalNavigationImmediateCase(AdditionalNavigationContext context, AdditionalNavigationValue item, bool confirm, string suffix)
    {
        string name = item.HostType + "-" + suffix;
        yield return SettingsNavigationPresent(context.Navigation);
        yield return SettingsNavigationOpen(context.Navigation, context.Entries[item.HostType]);
        object current = GetMember(item.Host, item.Property);
        object expected = AdditionalNavigationNextValue(item.Property, current);
        SetTrainingMember(item.Host, item.Property, expected);
        RefreshAdditionalNavigationControls(context.Entries[item.HostType]);
        yield return AdditionalNavigationSaved(item, expected, name + "-before-close");
        var main = SettingsNavigationMainContext();
        int calls = AdditionalNavigationRestartProbe.Calls;
        int apply = context.Navigation.Apply, cancel = context.Navigation.Cancel;
        var events = context.Events.ToDictionary(tap => tap.Name, tap => new[] { tap.Apply, tap.Cancel });
        SettingsNavigationClick(context.Navigation, confirm, "additional-" + name);
        yield return SettingsNavigationWait(context.Navigation, () => SettingsNavigationMainReady(context.Navigation), 15f, "additional-navigation-dismiss-" + name);
        yield return new WaitForSecondsRealtime(0.2f);
        Check("additional-navigation-no-restart-" + name, main != null && SettingsNavigationMainContext() == main && AdditionalNavigationRestartProbe.Calls == calls,
            "The real bottom button dismissed settings without rebuilding MainMenu or calling RestartGame.");
        VerifyAdditionalNavigationEvents(context, events, null, false, "immediate-" + name);
        Check("additional-navigation-no-probe-events-" + name, context.Navigation.Apply == apply && context.Navigation.Cancel == cancel,
            "An unrelated registered page received no global Apply or Cancel.");
        yield return AdditionalNavigationSaved(item, expected, name + "-after-close");
        additionalNavigationCases.Add(new { name, button = confirm ? "OK" : "Back", context = main.GetInstanceID(),
            host = item.HostType, property = item.Property, saved = expected, restartCalls = AdditionalNavigationRestartProbe.Calls - calls });
        SetTrainingMember(item.Host, item.Property, item.Original);
        RefreshAdditionalNavigationControls(context.Entries[item.HostType]);
        yield return AdditionalNavigationSaved(item, item.Original, name + "-restore");
    }

    IEnumerator AdditionalNavigationSaved(AdditionalNavigationValue item, object expected, string name)
    {
        object host = GetMember(item.Host, item.Property), runtime = item.ReadState();
        Check("additional-navigation-memory-" + name, AdditionalNavigationEqual(host, expected) && AdditionalNavigationEqual(runtime, expected),
            "host=" + host + "; runtime=" + runtime + "; expected=" + expected + "; property=" + item.Property);
        double deadline = timer.Elapsed.TotalSeconds + 10f;
        yield return Wait(() => {
            bool saved = AdditionalNavigationFileValue(item.Path, item.JsonPath, expected);
            if (!saved && timer.Elapsed.TotalSeconds >= deadline - 0.1f)
                File.WriteAllText(Path.Combine(directory, "additional-settings-persistence-failure.json"), JsonConvert.SerializeObject(new {
                    name, path = item.Path, jsonPath = item.JsonPath, expected,
                    host = GetMember(item.Host, item.Property), runtime = item.ReadState(),
                    content = File.Exists(item.Path) ? File.ReadAllText(item.Path) : "<missing>"
                }, Formatting.Indented));
            return saved;
        }, 10f, "additional-navigation-save-" + name);
        Check("additional-navigation-saved-" + name, AdditionalNavigationEqual(GetMember(item.Host, item.Property), expected) &&
            AdditionalNavigationEqual(item.ReadState(), expected) && AdditionalNavigationFileValue(item.Path, item.JsonPath, expected),
            "The visible host, actual runtime configuration and JSON file agree: " + item.JsonPath + "=" + expected);
    }

    static bool AdditionalNavigationEqual(object actual, object expected)
    {
        if (actual == null || expected == null) return Equals(actual, expected);
        if (actual is bool || expected is bool) return actual is bool a && expected is bool b && a == b;
        return Math.Abs(Convert.ToDouble(actual, CultureInfo.InvariantCulture) - Convert.ToDouble(expected, CultureInfo.InvariantCulture)) < 0.0001;
    }

    static object AdditionalNavigationNextValue(string property, object current)
    {
        if (current is bool flag) return !flag;
        if (current is int integer) return integer >= 20 ? integer - 1 : integer + 1;
        float value = Convert.ToSingle(current, CultureInfo.InvariantCulture);
        float increment = property == "PreparationSeconds" ? 0.25f : property == "RetainPercent" ? 5f : 0.5f;
        float maximum = property == "PreparationSeconds" ? 5f : property == "BodyMassKg" ? 200f : property == "MaxNps" ? 12f : 100f;
        return value + (value + increment > maximum ? -increment : increment);
    }

    static bool AdditionalNavigationFileValue(string path, string property, object expected)
    {
        try
        {
            JToken value = JObject.Parse(File.ReadAllText(path)).SelectToken(property);
            return value is JValue scalar && AdditionalNavigationEqual(scalar.Value, expected);
        }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
    }

    static void RefreshAdditionalNavigationControls(object entry)
    {
        var view = GetMember(entry, "ViewController") as Component;
        Type generic = FindTrainingType("BeatSaberMarkupLanguage.Components.Settings.GenericSetting");
        if (view == null || generic == null) throw new MissingMemberException("Native BSML GenericSetting view");
        foreach (Component control in view.GetComponentsInChildren(generic, true))
            AccessTools.Method(control.GetType(), "ReceiveValue").Invoke(control, null);
    }

    IEnumerator AdditionalNavigationChartCase(AdditionalNavigationContext context, bool confirm)
    {
        const string hostType = AdditionalSongChartHost;
        string name = confirm ? "chart-local-apply-confirm" : "chart-local-cancel-return";
        yield return SettingsNavigationPresent(context.Navigation);
        yield return SettingsNavigationOpen(context.Navigation, context.Entries[hostType]);
        Vector3[] before = AdditionalChartVectors.Select(property => (Vector3)GetMember(context.ChartConfig, property)).ToArray();
        object[] controls = AdditionalChartFields.Select(field => ((GameObject)GetMember(context.ChartHost, field))
            .GetComponent(FindTrainingType("BeatSaberMarkupLanguage.Components.Settings.IncrementSetting"))).ToArray();
        float[] pending = new float[controls.Length];
        for (int index = 0; index < controls.Length; index++)
        {
            object control = controls[index];
            float oldValue = Convert.ToSingle(GetMember(control, "Value"), CultureInfo.InvariantCulture);
            AccessTools.Method(control.GetType(), oldValue >= 340f ? "DecButtonPressed" : "IncButtonPressed").Invoke(control, null);
            pending[index] = Convert.ToSingle(GetMember(control, "Value"), CultureInfo.InvariantCulture);
            Check("additional-navigation-chart-draft-" + name + "-" + index, Math.Abs(pending[index] - oldValue) > 0.001f &&
                ((Vector3)GetMember(context.ChartConfig, AdditionalChartVectors[index]) - before[index]).sqrMagnitude < 0.0000001f,
                "The real increment control holds an unapplied edit; committed configuration has not changed.");
        }
        // Confirm while another approved page is active to test tracking all
        // visited deferred pages rather than only the last visible controller.
        if (confirm) yield return SettingsNavigationOpen(context.Navigation, context.Entries[AdditionalNoteTrainingHost]);
        var main = SettingsNavigationMainContext();
        int calls = AdditionalNavigationRestartProbe.Calls;
        int apply = context.Navigation.Apply, cancel = context.Navigation.Cancel;
        var events = context.Events.ToDictionary(tap => tap.Name, tap => new[] { tap.Apply, tap.Cancel });
        SettingsNavigationClick(context.Navigation, confirm, "additional-" + name);
        yield return SettingsNavigationWait(context.Navigation, () => SettingsNavigationMainReady(context.Navigation), 15f, "additional-navigation-dismiss-" + name);
        Check("additional-navigation-chart-no-restart-" + name, main != null && SettingsNavigationMainContext() == main && AdditionalNavigationRestartProbe.Calls == calls,
            "The actual bottom button dismissed without a soft restart.");
        VerifyAdditionalNavigationEvents(context, events, hostType, confirm, name);
        Check("additional-navigation-chart-no-global-events-" + name, context.Navigation.Apply == apply && context.Navigation.Cancel == cancel,
            "Only this page's parser event ran; the unrelated global probe received neither event.");
        for (int index = 0; index < before.Length; index++)
        {
            Vector3 expected = before[index];
            if (confirm) expected.x = pending[index];
            var actual = (Vector3)GetMember(context.ChartConfig, AdditionalChartVectors[index]);
            Check("additional-navigation-chart-vector-" + name + "-" + index, (actual - expected).sqrMagnitude < 0.0000001f,
                AdditionalChartVectors[index] + ": expected=" + expected + "; actual=" + actual);
            string path = Path.Combine(Path.GetDirectoryName(Application.dataPath), "UserData", "SongChartVisualizer.json");
            string vectorPath = AdditionalChartVectors[index] + ".x";
            yield return Wait(() => AdditionalNavigationFileValue(path, vectorPath, expected.x), 10f, "additional-navigation-chart-save-" + name + "-" + index);
            if (!confirm)
                Check("additional-navigation-chart-control-cancel-" + index,
                    Math.Abs(Convert.ToSingle(GetMember(controls[index], "Value"), CultureInfo.InvariantCulture) - before[index].x) < 0.0001f,
                    "Native local Cancel restored the increment widget from the host draft.");
        }
        additionalNavigationCases.Add(new { name, button = confirm ? "OK" : "Back", context = main.GetInstanceID(),
            localApply = confirm ? 1 : 0, localCancel = confirm ? 0 : 1, globalApply = context.Navigation.Apply - apply,
            globalCancel = context.Navigation.Cancel - cancel, savedVectors = before.Length });
        RestoreAdditionalChart(context);
    }

    void VerifyAdditionalNavigationEvents(AdditionalNavigationContext context, Dictionary<string, int[]> before, string localHost, bool confirm, string name)
    {
        foreach (var tap in context.Events)
        {
            int expectedApply = tap.Name == localHost && confirm ? 1 : 0;
            int expectedCancel = tap.Name == localHost && !confirm ? 1 : 0;
            Check("additional-navigation-parser-isolation-" + name + "-" + tap.Name,
                tap.Apply == before[tap.Name][0] + expectedApply && tap.Cancel == before[tap.Name][1] + expectedCancel,
                "Apply delta=" + (tap.Apply - before[tap.Name][0]) + "; Cancel delta=" + (tap.Cancel - before[tap.Name][1]) +
                "; expected=" + expectedApply + "/" + expectedCancel);
        }
    }

    IEnumerator AdditionalNavigationNativeFallback(AdditionalNavigationContext context, string hostType)
    {
        int calls = AdditionalNavigationRestartProbe.Calls, apply = context.Navigation.Apply, cancel = context.Navigation.Cancel;
        SettingsNavigationClick(context.Navigation, true, "additional-native-" + hostType);
        yield return null;
        Check("additional-navigation-native-confirm-" + hostType, AdditionalNavigationRestartProbe.Calls == calls + 1 && context.Navigation.Apply == apply + 1,
            "This real settings host retained native global Apply and RestartGame; the test blocked only the restart body.");
        SettingsNavigationClick(context.Navigation, false, "additional-native-cleanup-" + hostType);
        yield return SettingsNavigationWait(context.Navigation, () => SettingsNavigationMainReady(context.Navigation), 15f, "additional-navigation-native-cleanup-" + hostType);
        Check("additional-navigation-native-return-" + hostType, context.Navigation.Cancel == cancel + 1 && AdditionalNavigationRestartProbe.Calls == calls + 1,
            "The same mixed session retained native global Cancel for cleanup.");
        additionalNavigationCases.Add(new { name = "native-preserved-" + hostType, nativeApply = context.Navigation.Apply - apply,
            nativeCancel = context.Navigation.Cancel - cancel, restartCalls = AdditionalNavigationRestartProbe.Calls - calls });
    }

    static void RestoreAdditionalChart(AdditionalNavigationContext context)
    {
        if (context.ChartConfig == null || context.ChartOriginal == null) return;
        using (AccessTools.Method(context.ChartConfig.GetType(), "ChangeTransaction")?.Invoke(context.ChartConfig, null) as IDisposable)
        {
            for (int index = 0; index < AdditionalChartVectors.Length; index++)
            {
                SetTrainingMember(context.ChartConfig, AdditionalChartVectors[index], context.ChartOriginal[index]);
                SetTrainingMember(context.ChartHost, AdditionalChartDrafts[index], context.ChartOriginal[index]);
            }
        }
        RefreshAdditionalNavigationControls(context.Entries[AdditionalSongChartHost]);
    }

    static void RestoreAdditionalNavigationValues(AdditionalNavigationContext context)
    {
        foreach (var item in context.Values)
        {
            SetTrainingMember(item.Host, item.Property, item.Original);
            RefreshAdditionalNavigationControls(context.Entries[item.HostType]);
        }
        RestoreAdditionalChart(context);
    }

    IEnumerator AdditionalNavigationSavedAll(AdditionalNavigationContext context, string name)
    {
        foreach (var item in context.Values)
        {
            yield return AdditionalNavigationSaved(item, item.Original, name + "-" + item.HostType);
        }
    }
}
