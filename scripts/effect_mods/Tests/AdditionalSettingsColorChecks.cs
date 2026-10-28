using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace EffectModsSmoke;

public sealed partial class Runner
{
    // Called with the same live registrations and restart probe as the other
    // additional-settings cases. Drive ColorSetting.DonePressed, the real
    // color picker's accepted-color handler, rather than the config directly.
    IEnumerator VerifyAdditionalSettingsColors(AdditionalNavigationContext context)
    {
        string[] properties = { "LineColor", "PointerColor" };
        Color[] originals = properties.Select(name => (Color)GetMember(context.ChartConfig, name)).ToArray();
        try
        {
            for (int index = 0; index < properties.Length; index++)
            {
                yield return AdditionalSettingsColorCase(context, properties[index], index, true);
                yield return AdditionalSettingsColorCase(context, properties[index], index, false);
            }
        }
        finally
        {
            for (int index = 0; index < properties.Length; index++)
                SetTrainingMember(context.ChartHost, properties[index], originals[index]);
            RestoreAdditionalChart(context);
            RefreshAdditionalNavigationControls(context.Entries[AdditionalSongChartHost]);
        }
        string path = Path.Combine(Path.GetDirectoryName(Application.dataPath), "UserData", "SongChartVisualizer.json");
        for (int index = 0; index < properties.Length; index++)
        {
            string property = properties[index];
            Color expected = originals[index];
            yield return Wait(() => AdditionalSettingsColorSaved(path, property, expected), 10f,
                "additional-navigation-color-restore-" + property);
        }
    }

    IEnumerator AdditionalSettingsColorCase(AdditionalNavigationContext context, string property, int index, bool confirm)
    {
        string name = property + (confirm ? "-confirm" : "-cancel");
        string hostType = AdditionalSongChartHost;
        object entry = context.Entries[hostType];
        string path = Path.Combine(Path.GetDirectoryName(Application.dataPath), "UserData", "SongChartVisualizer.json");
        yield return SettingsNavigationPresent(context.Navigation);
        yield return SettingsNavigationOpen(context.Navigation, entry);

        Vector3[] before = AdditionalChartVectors.Select(vector => (Vector3)GetMember(context.ChartConfig, vector)).ToArray();
        object vectorControl = ((GameObject)GetMember(context.ChartHost, AdditionalChartFields[0]))
            .GetComponent(FindTrainingType("BeatSaberMarkupLanguage.Components.Settings.IncrementSetting"));
        float oldX = Convert.ToSingle(GetMember(vectorControl, "Value"), CultureInfo.InvariantCulture);
        AccessTools.Method(vectorControl.GetType(), "IncButtonPressed").Invoke(vectorControl, null);
        float pendingX = Convert.ToSingle(GetMember(vectorControl, "Value"), CultureInfo.InvariantCulture);
        Check("additional-navigation-color-vector-draft-" + name,
            Math.Abs(pendingX - oldX) > 0.001f &&
            ((Vector3)GetMember(context.ChartConfig, AdditionalChartVectors[0]) - before[0]).sqrMagnitude < 0.0000001f,
            "The position edit remains a draft while the accepted color is saved immediately.");

        Type colorType = FindTrainingType("BeatSaberMarkupLanguage.Components.Settings.ColorSetting");
        Component view = GetMember(entry, "ViewController") as Component;
        object colorControl = view.GetComponentsInChildren(colorType, true).Cast<object>().Single(control =>
            String.Equals(Convert.ToString(GetMember(GetMember(control, "AssociatedValue"), "MemberName"), CultureInfo.InvariantCulture),
                property, StringComparison.Ordinal));
        Color expected = (Color)new Color32((byte)(index == 0 ? 51 : 153), (byte)(confirm ? 102 : 204), (byte)(index == 0 ? 153 : 51), 255);
        Check("additional-navigation-color-native-control-" + name,
            (bool)GetMember(colorControl, "UpdateOnChange"),
            "The real color setting uses its existing apply-on-change binding.");
        AccessTools.Method(colorType, "DonePressed").Invoke(colorControl, new object[] { expected });
        Check("additional-navigation-color-memory-" + name,
            AdditionalSettingsColorEqual((Color)GetMember(context.ChartHost, property), expected) &&
            AdditionalSettingsColorEqual((Color)GetMember(context.ChartConfig, property), expected),
            "The color picker's native Done handler updated the visible Host and actual config.");
        // Waiting before either bottom button proves persistence does not rely
        // on OnApply's position transaction or a later unrelated config edit.
        yield return Wait(() => AdditionalSettingsColorSaved(path, property, expected), 10f,
            "additional-navigation-color-saved-before-close-" + name);

        var main = SettingsNavigationMainContext();
        int calls = AdditionalNavigationRestartProbe.Calls;
        int apply = context.Navigation.Apply, cancel = context.Navigation.Cancel;
        var events = context.Events.ToDictionary(tap => tap.Name, tap => new[] { tap.Apply, tap.Cancel });
        SettingsNavigationClick(context.Navigation, confirm, "additional-color-" + name);
        yield return SettingsNavigationWait(context.Navigation, () => SettingsNavigationMainReady(context.Navigation), 15f,
            "additional-navigation-color-dismiss-" + name);
        Check("additional-navigation-color-no-restart-" + name,
            main != null && SettingsNavigationMainContext() == main && AdditionalNavigationRestartProbe.Calls == calls,
            "The actual bottom button preserved the current MainMenu scene.");
        VerifyAdditionalNavigationEvents(context, events, hostType, confirm, "color-" + name);
        Check("additional-navigation-color-no-global-events-" + name,
            context.Navigation.Apply == apply && context.Navigation.Cancel == cancel,
            "Only the reviewed page received the appropriate Apply or Cancel event.");
        Check("additional-navigation-color-retained-" + name,
            AdditionalSettingsColorEqual((Color)GetMember(context.ChartConfig, property), expected) &&
            AdditionalSettingsColorSaved(path, property, expected),
            "The already accepted color survives both Confirm and Cancel and remains in JSON.");
        for (int vector = 0; vector < before.Length; vector++)
        {
            Vector3 target = before[vector];
            if (confirm && vector == 0) target.x = pendingX;
            Check("additional-navigation-color-vector-contract-" + name + "-" + vector,
                ((Vector3)GetMember(context.ChartConfig, AdditionalChartVectors[vector]) - target).sqrMagnitude < 0.0000001f,
                "Confirm applies the pending position; Cancel preserves the committed position.");
        }
        if (!confirm)
            Check("additional-navigation-color-cancel-restored-widget-" + name,
                Math.Abs(Convert.ToSingle(GetMember(vectorControl, "Value"), CultureInfo.InvariantCulture) - oldX) < 0.0001f,
                "The canceled position widget was restored from its unchanged config.");
        additionalNavigationCases.Add(new { name = "color-" + name, button = confirm ? "OK" : "Cancel",
            input = "ColorSetting.DonePressed", savedBeforeClose = true, savedAfterClose = true,
            color = "#" + ColorUtility.ToHtmlStringRGB(expected), positionApplied = confirm });
        RestoreAdditionalChart(context);
    }

    static bool AdditionalSettingsColorEqual(Color actual, Color expected)
    {
        const float tolerance = 0.00001f;
        return Math.Abs(actual.r - expected.r) < tolerance && Math.Abs(actual.g - expected.g) < tolerance &&
            Math.Abs(actual.b - expected.b) < tolerance && Math.Abs(actual.a - expected.a) < tolerance;
    }

    static bool AdditionalSettingsColorSaved(string path, string property, Color expected)
    {
        try
        {
            string text = (string)JObject.Parse(File.ReadAllText(path))[property];
            return ColorUtility.TryParseHtmlString(text, out Color actual) && AdditionalSettingsColorEqual(actual, expected);
        }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
    }
}
