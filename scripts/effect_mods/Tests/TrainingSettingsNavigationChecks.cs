using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EffectModsSmoke;

public sealed partial class Runner
{
    // Compatibility context used by AdditionalSettingsNavigationChecks. Its
    // old per-chart profile assertions were removed; these helpers only
    // exercise the native BSML flow and parser-event isolation.
    sealed class SettingsNavigationHost { }
    sealed class SettingsNavigationContext
    {
        internal object Settings, Flow, About, Probe;
        internal object[] OriginalEntries;
        internal Component ProbeView;
        internal int Apply, Cancel;
    }

    // The merged NoteMotionControl page is a single global settings entry.
    // Detailed value, persistence, preset, conditional, and localization
    // checks live in UnifiedTrainingChecks.
    IEnumerator VerifyTrainingSettingsNavigation()
    {
        EnsureTrainingPluginState();
        if (!trainingNoteLoaded) yield break;
        yield return VerifyUnifiedSettingsNavigation();
    }

    void VerifySettingsNavigationPatchOwnership() { }

    static Component SettingsNavigationCreateView(string name)
    {
        Type ui = FindTrainingType("BeatSaberMarkupLanguage.BeatSaberUI");
        MethodInfo create = ui.GetMethods(BindingFlags.Static | BindingFlags.Public)
            .Single(method => method.Name == "CreateViewController" && method.IsGenericMethodDefinition && method.GetParameters().Length == 0);
        var view = (Component)create.MakeGenericMethod(typeof(HMUI.ViewController)).Invoke(null, null);
        view.gameObject.name = name;
        AccessTools.Method(FindTrainingType("BeatSaberMarkupLanguage.Settings.SettingsMenu"), "SetupViewControllerTransform")
            .Invoke(null, new object[] { view });
        return view;
    }

    static void SettingsNavigationReplaceEntries(object settings, object[] entries)
    {
        object collection = GetMember(settings, "SettingsMenus");
        Type contract = collection.GetType().GetInterfaces()
            .First(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ICollection<>));
        contract.GetMethod("Clear").Invoke(collection, null);
        foreach (object entry in entries) contract.GetMethod("Add").Invoke(collection, new[] { entry });
    }

    static void DisposeSettingsNavigationContext(SettingsNavigationContext navigation)
    {
        if (navigation == null) return;
        if (navigation.Settings != null && navigation.OriginalEntries != null)
            SettingsNavigationReplaceEntries(navigation.Settings, navigation.OriginalEntries);
        if (navigation.ProbeView != null) UnityEngine.Object.Destroy(navigation.ProbeView.gameObject);
    }

    IEnumerator SettingsNavigationPresent(SettingsNavigationContext navigation)
    {
        AccessTools.Method(navigation.Settings.GetType(), "PresentSettings", Type.EmptyTypes).Invoke(navigation.Settings, null);
        yield return SettingsNavigationWait(navigation, () => IsFlowActive(navigation.Flow) &&
            !ReadBool(navigation.Flow, "IsAnimating", true) && !ReadBool(navigation.Flow, "isPresenting", true) &&
            !ReadBool(navigation.Flow, "isInTransition", true), 15f, "settings-navigation-present");
    }

    IEnumerator SettingsNavigationOpen(SettingsNavigationContext navigation, object entry)
    {
        yield return SettingsNavigationWait(navigation, () => !ReadBool(navigation.Flow, "IsAnimating", true) &&
            !ReadBool(navigation.Flow, "isPresenting", true), 15f, "settings-navigation-idle");
        AccessTools.Method(navigation.Flow.GetType(), "OpenMenu", new[] { entry.GetType() }).Invoke(navigation.Flow, new[] { entry });
        yield return SettingsNavigationReady(navigation, GetMember(entry, "ViewController") as Component, "settings-navigation-open");
    }

    IEnumerator SettingsNavigationReady(SettingsNavigationContext navigation, Component view, string label)
    {
        yield return SettingsNavigationWait(navigation, () => view != null &&
            ReferenceEquals(GetMember(navigation.Flow, "ActiveController"), view) &&
            IsTrainingSettingsViewActive(view), 15f, label);
    }

    IEnumerator SettingsNavigationWait(SettingsNavigationContext navigation, Func<bool> predicate, float seconds, string label)
    {
        yield return Wait(predicate, seconds, label);
    }

    static bool SettingsNavigationMainReady(SettingsNavigationContext navigation)
    {
        object main = GetStaticMember(FindTrainingType("BeatSaberMarkupLanguage.BeatSaberUI"), "MainFlowCoordinator");
        return main != null && ModUiAuditFlowActive(main) && !ReadBool(main, "isInTransition", true) &&
            Active<MainMenuViewController>() != null && Active<MainMenuViewController>().isActivated && !IsFlowActive(navigation.Flow);
    }

    static Zenject.SceneContext SettingsNavigationMainContext()
    {
        return Resources.FindObjectsOfTypeAll<Zenject.SceneContext>()
            .FirstOrDefault(context => context != null && context.gameObject.scene.name == "MainMenu");
    }

    Button SettingsNavigationClick(SettingsNavigationContext navigation, bool confirm, string name)
    {
        Transform root = GetMember(navigation.Flow, "bottomButtons") as Transform;
        Button button = root != null && root.childCount == 2 ? root.GetChild(confirm ? 1 : 0).GetComponent<Button>() : null;
        Check("settings-navigation-native-button-" + name, button != null && button.isActiveAndEnabled && button.interactable,
            "Native " + (confirm ? "OK" : "Back") + " button");
        button?.onClick.Invoke();
        return button;
    }
}
