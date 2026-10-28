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
    sealed class LocalUiTarget
    {
        internal readonly string Id, Category, Title, Host, Scope;
        internal readonly string[] Tabs, English;
        internal LocalUiTarget(string id, string category, string title, string host, string scope,
            string[] tabs, string[] english)
        { Id = id; Category = category; Title = title; Host = host; Scope = scope; Tabs = tabs; English = english; }
    }

    static readonly LocalUiTarget[] LocalUiPages = {
        new("training", "Training", "Training settings", "NoteMotionControl.SettingsMenu", "NoteMotionControl",
            new[] { "Common", "Density and reaction", "Motion effects" },
            new[] { "Preset", "Reduction mode", "Hide pre-jump" }),
        new("energy", "Training", "Activity Energy", "ActivityEnergyCounter.EnergySettingsHost", "ActivityEnergyCounter",
            new[] { "Overview", "General", "HUD", "Results", "Estimate" },
            new[] { "Reset workout", "Enabled", "Primary HUD scope", "Results font scale", "Body mass" }),
        new("voice-input", "Devices", "Voice Input", "VoiceInput.VoiceSettingsHost", "VoiceInput",
            Array.Empty<string>(), new[] { "Microphone" }),
        new("calibration", "Devices", "Room Calibration", "RoomCalibration.SettingsPanel", "RoomCalibration",
            Array.Empty<string>(), new[] { "Calibrate to Current Position" }),
        new("language", "Devices", "Mod UI Language", "ModUiChinese.LocalizationPanel", "ModUiChinese",
            new[] { "General", "Advanced" },
            new[] { "Chinese Mod UI", "Custom translations" }),
    };

    static Component LocalUiMenuButtonsView()
    {
        Type type = FindTrainingType("BeatSaberMarkupLanguage.MenuButtons.MenuButtonsViewController");
        return type == null ? null : Resources.FindObjectsOfTypeAll(type).OfType<Component>()
            .FirstOrDefault(value => value != null && value.gameObject.scene.IsValid() && ModUiAuditViewActive(value));
    }

    static Button LocalUiButton(Component root, params string[] captions)
    {
        return root == null ? null : root.GetComponentsInChildren<Button>(true).FirstOrDefault(button =>
            button != null && button.gameObject.activeInHierarchy && button.interactable &&
            button.GetComponentsInChildren<TMP_Text>(true).Any(text => captions.Contains(text.text)));
    }

    static object LocalUiCenterFlow(object main)
    {
        object flow = ModUiAuditYoungestFlow(main);
        return flow != null && flow.GetType().FullName == "LocalModUI.CenterFlow" && ModUiAuditFlowActive(flow) ? flow : null;
    }

    static string LocalUiPageId(object flow) => GetMember(GetMember(GetMember(flow, "settings"), "Entry"), "Id") as string;
    static bool LocalUiRegistryReady() => Equals(GetStaticMember(FindTrainingType("LocalModUI.UiRegistry"), "IsReady"), true);
    static bool LocalUiReady(object flow) => LocalUiRegistryReady() && ModUiAuditFlowActive(flow) &&
        !ReadBool(flow, "isInTransition", true) && !ReadBool(flow, "navigating", true);

    IEnumerator LocalUiSafeAudit(ModUiAuditRun run, IEnumerator routine)
    {
        var stack = new Stack<IEnumerator>();
        stack.Push(routine);
        try
        {
            while (stack.Count > 0)
            {
                bool next = false;
                object value = null;
                Exception failure = null;
                try { next = stack.Peek().MoveNext(); if (next) value = stack.Peek().Current; }
                catch (Exception error) { failure = error; }
                if (failure != null)
                {
                    string detail = ModUiAuditException(failure);
                    foreach (var page in run.Pages.OfType<Dictionary<string, object>>().Where(x => ModUiAuditString(x["status"]) == "blocked"))
                    { page["reason"] = detail; run.BlockedPages++; }
                    run.Block("LocalModUI", "runtime", detail);
                    yield break;
                }
                if (!next)
                {
                    try { (stack.Pop() as IDisposable)?.Dispose(); }
                    catch (Exception error) { run.Block("LocalModUI", "cleanup", ModUiAuditException(error)); yield break; }
                    continue;
                }
                if (value is IEnumerator child) stack.Push(child); else yield return value;
            }
        }
        finally
        {
            while (stack.Count > 0)
                try { (stack.Pop() as IDisposable)?.Dispose(); }
                catch (Exception error) { run.Block("LocalModUI", "cleanup", ModUiAuditException(error)); }
        }
    }

    static void LocalUiSetLanguage(object host, bool enabled)
    {
        PropertyInfo property = host?.GetType().GetProperty("Enabled");
        if (property == null) throw new MissingMemberException("ModUiChinese.LocalizationPanel.Enabled");
        property.SetValue(host, enabled, null);
        if ((bool)property.GetValue(host, null) != enabled) throw new InvalidOperationException("Language setting did not change");
    }

    static bool LocalUiText(Component root, string caption)
    {
        return root != null && root.GetComponentsInChildren<TMP_Text>(true)
            .Any(text => text != null && text.gameObject.activeInHierarchy && text.text == caption);
    }

    static string LocalUiTranslated(string caption, string scope)
    {
        object engine = GetStaticMember(FindTrainingType("ModUiChinese.UiTranslator"), "Engine");
        MethodInfo translate = AccessTools.Method(engine?.GetType(), "Translate", new[] { typeof(string), typeof(string) });
        if (translate == null) throw new MissingMethodException("ModUiChinese.TranslationEngine.Translate");
        return (string)translate.Invoke(engine, new object[] { caption, scope });
    }

    static string LocalUiOptionalCaption(string caption, string scope)
    {
        try { return LocalUiTranslated(caption, scope); }
        catch { return caption; }
    }

    void LocalUiCheckCenterControls(Component center, string category)
    {
        Button[] buttons = center.GetComponentsInChildren<Button>(true)
            .Where(x => x != null && x.gameObject.activeInHierarchy).ToArray();
        string[] undersized = buttons.Where(button =>
        {
            RectTransform rect = button.transform as RectTransform;
            TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
            return rect == null || rect.rect.height < 7.95f || label == null || label.fontSize < 3.15f;
        }).Select(x => ModUiAuditTextFor(x)).ToArray();
        Check("local-ui-control-size-" + category, buttons.Length > 0 && undersized.Length == 0,
            "Center buttons need height 8 and font size 3.2; undersized: " + string.Join(", ", undersized));
    }

    void LocalUiCheckCenterLabels(Component center, IEnumerable<object> entries, string category, bool chinese)
    {
        foreach (string group in new[] { "Training", "Library", "Devices" })
        {
            string source = LocalUiCategoryLabels(group)[0];
            string expected = chinese ? LocalUiTranslated(source, "LocalModUI") : source;
            Check("local-ui-center-category-label-" + category + "-" + group + "-" + chinese,
                LocalUiText(center, expected), "Expected visible center category label: " + expected);
        }
        foreach (object entry in entries.Where(x => Convert.ToString(GetMember(x, "Category")) == category))
        {
            string id = GetMember(entry, "Id") as string;
            foreach (string member in new[] { "Title", "SecondaryTitle" })
            {
                string source = GetMember(entry, member) as string;
                if (string.IsNullOrEmpty(source)) continue;
                string expected = chinese ? LocalUiTranslated(source, "LocalModUI") : source;
                Check("local-ui-center-entry-label-" + id + "-" + member + "-" + chinese,
                    LocalUiText(center, expected), "Center entry " + id + " expected visible " + member + ": " + expected);
            }
        }
    }

    void LocalUiCheckTabLabels(ModUiGameplaySelectorState state, LocalUiTarget target, bool chinese)
    {
        int page = ModUiGameplayReadInt(state.Selector, "currentPage", "CurrentPage");
        int start = page * state.PageSize;
        int count = Math.Min(state.PageSize, state.VisibleTabs.Count - start);
        Component[] cells = (GetMember(state.Control, "cells") as IEnumerable)?.Cast<object>()
            .OfType<Component>().Where(x => x != null && x.gameObject.activeInHierarchy).ToArray() ?? Array.Empty<Component>();
        Check("local-ui-tab-rendered-cell-count-" + target.Id + "-" + page + "-" + chinese,
            page >= 0 && count > 0 && cells.Length == count,
            "Page " + page + " expects " + count + " active selector cells; found " + cells.Length);
        for (int index = 0; index < count; index++)
        {
            int localIndex = index;
            Component[] matches = cells.Where(cell => GetMember(cell, "cellNumber") is int number && number == localIndex).ToArray();
            Check("local-ui-tab-rendered-cell-" + target.Id + "-" + (start + index) + "-" + chinese,
                matches.Length == 1, "Each visible tab has one corresponding selector cell");
            Component cell = matches[0];
            TMP_Text text = GetMember(cell, "_text") as TMP_Text ?? cell.GetComponentInChildren<TMP_Text>(true);
            string source = GetMember(state.VisibleTabs[start + index], "TabName") as string;
            string expected = chinese ? LocalUiTranslated(source, target.Scope) : source;
            bool rendered = text != null && text.gameObject.activeInHierarchy && text.enabled &&
                text.canvasRenderer != null && !text.canvasRenderer.cull &&
                text.rectTransform.rect.width > 0 && text.rectTransform.rect.height > 0;
            Check("local-ui-tab-rendered-label-" + target.Id + "-" + (start + index) + "-" + chinese,
                rendered && text.text == expected,
                "Expected rendered selector label '" + expected + "' from tab '" + source +
                "', actual='" + (text == null ? "<missing>" : text.text) + "', rendered=" + rendered);
        }
    }

    IEnumerator VerifyLocalModUi(ModUiAuditRun run)
    {
        object main = GetStaticMember(FindTrainingType("BeatSaberMarkupLanguage.BeatSaberUI"), "MainFlowCoordinator");
        yield return Wait(() => LocalUiRegistryReady() && ModUiAuditFlowActive(main) && !ReadBool(main, "isInTransition", true), 15f, "local-ui-main-ready");
        yield return new WaitForSecondsRealtime(1f);
        Type registryType = FindTrainingType("LocalModUI.UiRegistry");
        object[] entries = (GetStaticMember(registryType, "Entries") as IEnumerable)?.Cast<object>().ToArray() ?? Array.Empty<object>();
        string[] ids = entries.Select(x => GetMember(x, "Id") as string).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        string[] expected = { "advanced-filters", "beatsage", "calibration", "energy", "language", "song-cleaner", "training", "voice-input" };
        Check("local-ui-registry", ids.SequenceEqual(expected), "Registered IDs: " + string.Join(", ", ids));
        var byId = entries.ToDictionary(x => (string)GetMember(x, "Id"), StringComparer.Ordinal);
        LocalUiRegistryChecks(registryType, entries[0]);

        object settings = FindBsmlSettingsInstance(FindTrainingType("BeatSaberMarkupLanguage.Settings.BSMLSettings"));
        // Re-register a production page after BSML initialization. AddSettingsMenu
        // can parse synchronously, so ownership must exist before that call.
        object trainingHost = GetMember(GetMember(byId["training"], "Page"), "Host");
        Check("local-ui-native-initialized", ReadBool(settings, "isInitialized", false), "BSML registry is initialized before late registration");
        AccessTools.Method(trainingHost.GetType(), "Dispose").Invoke(trainingHost, null);
        AccessTools.Method(trainingHost.GetType(), "Initialize").Invoke(trainingHost, null);
        entries = ((IEnumerable)GetStaticMember(registryType, "Entries")).Cast<object>().ToArray();
        byId = entries.ToDictionary(x => (string)GetMember(x, "Id"), StringComparer.Ordinal);
        object[] native = (GetMember(settings, "SettingsMenus") as IEnumerable)?.Cast<object>().ToArray() ?? Array.Empty<object>();
        foreach (LocalUiTarget target in LocalUiPages)
        {
            object page = GetMember(byId[target.Id], "Page");
            object host = GetMember(page, "Host");
            object[] aliases = native.Where(x => ReferenceEquals(GetMember(x, "Host"), host)).ToArray();
            Check("local-ui-native-alias-" + target.Id, host?.GetType().FullName == target.Host && aliases.Length == 1 &&
                Equals(GetMember(aliases[0], "Resource"), GetMember(page, "Resource")) &&
                ReferenceEquals(GetMember(aliases[0], "Assembly"), GetMember(page, "Assembly")),
                "Native settings and center share one host, assembly, and resource");
            EnsureSettingsMenuSetup(aliases[0]);
            Check("local-ui-native-parse-" + target.Id, GetMember(aliases[0], "ViewController") is Component &&
                GetMember(aliases[0], "ParserParams") != null, "The native alias parses the shared resource");
            var nativeView = GetMember(aliases[0], "ViewController") as Component;
            Check("local-ui-native-owned-" + target.Id, nativeView.GetComponent(FindTrainingType("LocalModUI.OwnedPage")) != null,
                "Native aliases are marked before tab controls are created, including synchronous late registration");
            run.Menus.Add(new { kind = "BSMLSettings", title = target.Title, scope = target.Scope,
                host = target.Host, resource = GetMember(page, "Resource"), registered = true, source = "BSMLSettings.SettingsMenus" });
        }

        object buttons = GetStaticMember(FindTrainingType("BeatSaberMarkupLanguage.MenuButtons.MenuButtons"), "Instance");
        object[] registered = (GetMember(buttons, "Buttons") as IEnumerable)?.Cast<object>().ToArray() ?? Array.Empty<object>();
        string[] owned = registered.Where(x => ModUiAuditNavigationActionOwner(x) == "LocalModUI")
            .Select(x => GetMember(x, "Text") as string).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check("local-ui-main-buttons", owned.SequenceEqual(new[] { "Calibrate to Current Position", "Custom mods", "Training settings" }),
            "Main menu entries owned by LocalModUI: " + string.Join(", ", owned));
        Action calibration = GetMember(byId["calibration"], "ShortcutAction") as Action;
        Check("local-ui-calibration-shortcut", calibration?.Method.Name == "RequestCalibration" &&
            calibration.Target?.GetType().FullName == "RoomCalibration.CalibrationService",
            "Calibration shortcut calls the same calibration service without invoking it during audit");
        Check("local-ui-obsolete-shortcuts", !registered.Any(x => new[] {
            "Configure Note Training Control", "Configure Activity Energy", "Activity Energy", "Beat Sage",
            "Clear Session Calibration", "Duplicate songs" }.Contains(GetMember(x, "Text") as string)),
            "Old independent shortcuts have been removed");

        Type localizationState = FindTrainingType("ModUiChinese.LocalizationState");
        object store = GetStaticMember(localizationState, "Store");
        object originalOptions = GetMember(store, "Global");
        object options = GetMember(store, "Global");
        object languageHost = GetMember(GetMember(byId["language"], "Page"), "Host");
        FieldInfo activeOptions = AccessTools.Field(localizationState, "ActiveOptions");
        FieldInfo languageError = AccessTools.Field(localizationState, "Error");
        object originalError = languageError?.GetValue(null);
        MethodInfo save = AccessTools.Method(store?.GetType(), "SaveGlobal");
        MethodInfo reload = AccessTools.Method(FindTrainingType("ModUiChinese.UiTranslator"), "Reload");
        Check("local-ui-language-api", originalOptions != null && options != null && activeOptions != null && save != null && reload != null,
            "Translation settings and refresh API are available");
        try
        {
            IDictionary scopes = GetMember(options, "Mods") as IDictionary;
            Check("local-ui-language-scopes", scopes != null, "Per-mod translation scopes are available");
            foreach (string scope in LocalUiPages.Select(x => x.Scope).Concat(new[] { "LocalModUI" })) scopes[scope] = true;
            save.Invoke(store, new[] { options }); activeOptions.SetValue(null, options); reload.Invoke(null, null);
            var choices = ((IEnumerable)GetMember(languageHost, "ScopeChoices")).Cast<object>().Select(Convert.ToString).ToArray();
            Check("local-ui-language-assembly-scope", choices.Contains("BS_Utils") && !choices.Contains("BS Utils"),
                "Language settings use installed assembly scopes rather than differing plugin IDs");
            var selectedScope = languageHost.GetType().GetProperty("SelectedScope");
            var scopeEnabled = languageHost.GetType().GetProperty("ScopeEnabled");
            selectedScope.SetValue(languageHost, "BS_Utils", null);
            LocalUiSetLanguage(languageHost, true);
            scopeEnabled.SetValue(languageHost, false, null);
            Check("local-ui-language-disable-assembly-scope", LocalUiTranslated("Enabled", "BS_Utils") == "Enabled", "Disabling BS_Utils uses the original caption");
            scopeEnabled.SetValue(languageHost, true, null);
            Check("local-ui-language-enable-assembly-scope", LocalUiTranslated("Enabled", "BS_Utils") == "已启用", "Enabling BS_Utils translates the actual assembly scope");
            selectedScope.SetValue(languageHost, "Common", null);
            LocalUiSetLanguage(languageHost, false);
            yield return new WaitForSecondsRealtime(0.7f);
            Component nativeMenu = LocalUiMenuButtonsView();
            Button centerButton = LocalUiButton(nativeMenu, "Custom mods");
            Check("local-ui-center-button", centerButton != null, "Visible native Custom mods button");
            centerButton.onClick.Invoke();
            yield return Wait(() => LocalUiReady(LocalUiCenterFlow(main)) &&
                ModUiAuditViewActive(GetMember(LocalUiCenterFlow(main), "menu") as Component), 15f, "local-ui-center-open");
            object flow = LocalUiCenterFlow(main);
            Component center = GetMember(flow, "menu") as Component;
            Camera camera = FindTrainingMenuCamera();
            Check("local-ui-camera", camera != null, "Menu camera is available");
            yield return LocalUiTimeoutRecovery(run, main, flow, center, byId["training"]);
            flow = LocalUiCenterFlow(main);
            center = GetMember(flow, "menu") as Component;
            Check("local-ui-recovery-center-ready", LocalUiReady(flow) && ModUiAuditViewActive(center),
                "Recovered center remains available for the remaining audit");
            foreach (string category in new[] { "Training", "Library", "Devices" })
            {
                yield return LocalUiSelectCategory(center, category);
                LocalUiCheckCenterControls(center, category);
                var page = LocalUiRecord(run, "LocalModUICategory", category, "LocalModUI", center.GetType().FullName);
                string[] labels = LocalUiCategoryLabels(category);
                foreach (bool chinese in new[] { false, true })
                {
                    LocalUiSetLanguage(languageHost, chinese);
                    yield return new WaitForSecondsRealtime(0.7f);
                    yield return ModUiAuditWaitLayout(center, 3, 2f);
                    string caption = chinese ? LocalUiTranslated(labels[0], "LocalModUI") : labels[0];
                    Check("local-ui-category-caption-" + category + "-" + chinese, LocalUiText(center, caption),
                        "Category caption reflects selected language");
                    LocalUiCheckCenterLabels(center, entries, category, chinese);
                    ModUiAuditCheckVisibility(run, page, center, camera);
                    ModUiAuditCapturePage(run, page, center, camera, "local-category-" + category + (chinese ? "-zh" : "-en"));
                }
                page["status"] = "verified"; run.StablePages++;
                LocalUiSetLanguage(languageHost, false);
                yield return new WaitForSecondsRealtime(0.4f);
            }

            foreach (LocalUiTarget target in LocalUiPages)
                yield return LocalUiHostedPage(run, main, flow, center, byId[target.Id], target, languageHost, camera);
            yield return LocalUiTools(run, main, flow, center, languageHost, camera);
            yield return LocalUiAdvancedFilters(run, main, flow, center, languageHost, camera);
        }
        finally
        {
            try
            {
                save.Invoke(store, new[] { originalOptions }); activeOptions.SetValue(null, originalOptions);
                reload.Invoke(null, null); if (languageError != null) languageError.SetValue(null, originalError);
            }
            catch (Exception error) { run.Block("ModUiChinese", "restore", ModUiAuditException(error)); }
        }
    }

    void LocalUiRegistryChecks(Type registry, object existing)
    {
        MethodInfo register = AccessTools.Method(registry, "Register");
        Check("local-ui-register-api", register != null, "Registry exposes Register");
        bool rejected = false;
        IDisposable duplicate = null;
        try { duplicate = register.Invoke(null, new[] { existing }) as IDisposable; }
        catch (TargetInvocationException error) { rejected = error.InnerException is InvalidOperationException; }
        finally { duplicate?.Dispose(); }
        Check("local-ui-duplicate-rejected", rejected, "Duplicate IDs are rejected");
        Type type = FindTrainingType("LocalModUI.UiEntry");
        object probe = Activator.CreateInstance(type);
        type.GetProperty("Id").SetValue(probe, "audit-disposable", null);
        type.GetProperty("Title").SetValue(probe, "Runtime audit", null);
        type.GetProperty("Open").SetValue(probe, new Action<HMUI.FlowCoordinator>(_ => { }), null);
        IDisposable registration = (IDisposable)register.Invoke(null, new[] { probe });
        try
        {
            Check("local-ui-probe-registered", ((IEnumerable)GetStaticMember(registry, "Entries")).Cast<object>()
                .Any(x => ReferenceEquals(x, probe)), "Temporary entry appears in registry");
        }
        finally { registration.Dispose(); }
        Check("local-ui-probe-disposed", !((IEnumerable)GetStaticMember(registry, "Entries")).Cast<object>()
            .Any(x => ReferenceEquals(x, probe)), "Disposed entry is absent");
    }

    static string[] LocalUiCategoryLabels(string category)
    {
        switch (category) {
            case "Training": return new[] { "Training and activity", "训练与运动" };
            case "Library": return new[] { "Song library", "曲库工具" };
            default: return new[] { "Devices and interface", "设备与界面" };
        }
    }

    IEnumerator LocalUiSelectCategory(Component center, string category)
    {
        Button button = LocalUiButton(center, LocalUiCategoryLabels(category));
        Check("local-ui-category-button-" + category, button != null, "Visible category button");
        button.onClick.Invoke();
        yield return Wait(() => ReadBool(center, category + "Selected", false), 5f, "local-ui-category-" + category);
        yield return ModUiAuditWaitLayout(center, 3, 2f);
    }

    static Dictionary<string, object> LocalUiRecord(ModUiAuditRun run, string kind, string title, string scope, string host)
    {
        var page = new Dictionary<string, object> { ["kind"] = kind, ["title"] = title, ["scope"] = scope,
            ["host"] = host, ["status"] = "blocked" };
        run.Pages.Add(page);
        return page;
    }

    IEnumerator LocalUiTimeoutRecovery(ModUiAuditRun run, object main, object flow, Component center, object entry)
    {
        yield return LocalUiSelectCategory(center, "Training");
        Button row = LocalUiButton(center, "Training settings");
        Check("local-ui-timeout-training-row", row != null, "Training settings row is visible");
        row.onClick.Invoke();
        yield return Wait(() => LocalUiPageId(flow) == "training" && LocalUiReady(flow) &&
            ModUiAuditViewActive(GetMember(flow, "settings") as Component), 15f, "local-ui-timeout-training-open");
        Component oldFlow = flow as Component;
        Component oldPage = GetMember(flow, "settings") as Component;
        Check("local-ui-timeout-training-source", oldPage != null && ReferenceEquals(GetMember(oldPage, "Entry"), entry),
            "The training page belongs to the registered entry before the simulated timeout");
        FieldInfo navigating = AccessTools.Field(flow.GetType(), "navigating");
        FieldInfo deadline = AccessTools.Field(flow.GetType(), "navigationDeadline");
        object context = GetStaticMember(FindTrainingType("LocalModUI.MenuContext"), "Current");
        Check("local-ui-timeout-api", navigating != null && deadline != null && context != null,
            "Center navigation state and owning menu context are available");
        deadline.SetValue(flow, Time.realtimeSinceStartup - 1f);
        navigating.SetValue(flow, true);
        yield return Wait(() => oldFlow == null && oldPage == null && GetMember(context, "flow") == null &&
            LocalUiRegistryReady() && !ReadBool(context, "busy", true) && ModUiAuditFlowActive(main) && !ReadBool(main, "isInTransition", true),
            15f, "local-ui-timeout-recovered-main");
        Check("local-ui-timeout-destroyed", oldFlow == null && oldPage == null && GetMember(context, "flow") == null,
            "Timed-out flow and its retained settings page were destroyed and cleared");
        yield return Wait(() => GetMember(context, "overlay") is Component feedback && feedback != null,
            12f, "local-ui-timeout-feedback-created");
        Component overlay = GetMember(context, "overlay") as Component;
        Component overlayModal = GetMember(overlay, "modal") as Component;
        Check("local-ui-timeout-overlay-created", overlay != null && overlayModal != null,
            "Feedback owner=" + (overlay == null ? "missing" : overlay.gameObject.name) + "; modal=" + (overlayModal == null ? "missing" : overlayModal.gameObject.name));
        try
        {
            yield return Wait(() => overlay != null &&
                LocalUiButton(overlayModal, "Close", "关闭") != null, 10f, "local-ui-timeout-overlay");
            const string message = "Navigation timed out. Return and try again.";
            Check("local-ui-timeout-feedback", Equals(GetMember(overlay, "Message"), message) && LocalUiText(overlayModal, message),
                "The user receives visible timeout feedback");
            Button close = LocalUiButton(overlayModal, "Close", "关闭");
            close.onClick.Invoke();
            yield return Wait(() => overlay == null, 10f, "local-ui-timeout-overlay-closed");
        }
        finally
        {
            if (overlay != null)
            {
                try
                {
                    MethodInfo close = AccessTools.Method(overlay.GetType(), "Close");
                    if (close == null) run.Block("LocalModUI", "timeout-overlay-cleanup", "Overlay close handler is unavailable.");
                    else close.Invoke(overlay, null);
                }
                catch (Exception error) { run.Block("LocalModUI", "timeout-overlay-cleanup", ModUiAuditException(error)); }
            }
        }

        Component nativeMenu = LocalUiMenuButtonsView();
        Button shortcut = LocalUiButton(nativeMenu, "Training settings");
        Check("local-ui-timeout-reopen-button", shortcut != null, "Native training shortcut is visible after recovery");
        shortcut.onClick.Invoke();
        yield return Wait(() => {
            object fresh = LocalUiCenterFlow(main);
            return LocalUiReady(fresh) && !ReferenceEquals(fresh, flow) && LocalUiPageId(fresh) == "training" &&
                ModUiAuditViewActive(GetMember(fresh, "settings") as Component);
        }, 15f, "local-ui-timeout-training-reopen");
        object newFlow = LocalUiCenterFlow(main);
        Component newPage = GetMember(newFlow, "settings") as Component;
        Check("local-ui-timeout-new-page", newPage != null && !ReferenceEquals(newPage, oldPage) &&
            ReferenceEquals(GetMember(newPage, "Entry"), entry),
            "The real shortcut opens a fresh page for the original training entry");
        Check("local-ui-timeout-back", ModUiAuditDismissFlow(main, newFlow, out string detail), detail);
        yield return Wait(() => GetMember(newFlow, "settings") == null && LocalUiReady(newFlow) &&
            ModUiAuditViewActive(GetMember(newFlow, "menu") as Component), 15f, "local-ui-timeout-return-center");
        Check("local-ui-timeout-category", ReadBool(GetMember(newFlow, "menu"), "TrainingSelected", false),
            "The recovered page returns to the training category");
        run.Entrances.Add(new { kind = "LocalModUITimeoutRecovery", status = "verified",
            source = "Training settings", detail = "Timed-out flow and page destroyed; native shortcut opened a new flow." });
    }

    IEnumerator LocalUiHostedPage(ModUiAuditRun run, object main, object flow, Component center, object entry,
        LocalUiTarget target, object languageHost, Camera camera)
    {
        yield return LocalUiSelectCategory(center, target.Category);
        Button row = LocalUiButton(center, target.Title);
        Check("local-ui-row-" + target.Id, row != null, "Visible center row for " + target.Title);
        row.onClick.Invoke();
        yield return Wait(() => LocalUiPageId(flow) == target.Id && LocalUiReady(flow) && ModUiAuditViewActive(GetMember(flow, "settings") as Component),
            15f, "local-ui-page-" + target.Id);
        Component view = GetMember(flow, "settings") as Component;
        Check("local-ui-source-" + target.Id, ReferenceEquals(GetMember(view, "Entry"), entry), "Page uses the registered entry");
        yield return ModUiAuditWaitLayout(view, 4, 3f);
        var record = LocalUiRecord(run, "LocalModUISettings", target.Title, target.Scope, target.Host);
        record["resource"] = GetMember(GetMember(entry, "Page"), "Resource");
        Type selectorType = FindTrainingType("BeatSaberMarkupLanguage.Components.TabSelector");
        Component[] selectors = selectorType == null ? Array.Empty<Component>() :
            view.GetComponentsInChildren(selectorType, true).OfType<Component>().Where(x => x.gameObject.activeInHierarchy).ToArray();
        Check("local-ui-tabs-" + target.Id, selectors.Length == (target.Tabs.Length == 0 ? 0 : 1),
            "Expected native BSML selector count; actual=" + selectors.Length);
        ModUiGameplaySelectorState state = null;
        if (selectors.Length > 0)
        {
            state = ModUiGameplayReadSelectorState(selectors[0], ModUiGameplayEnumerable(GetMember(selectors[0], "tabs")));
            Check("local-ui-tab-count-" + target.Id, state.VisibleTabs.Count == target.Tabs.Length,
                "Expected " + target.Tabs.Length + " tabs; actual=" + state.VisibleTabs.Count);
        }
        var tabRecords = new List<object>(); record["tabs"] = tabRecords;
        for (int index = 0; index < Math.Max(1, target.Tabs.Length); index++)
        {
            if (state != null)
            {
                Check("local-ui-tab-select-" + target.Id + "-" + index, ModUiGameplaySelectGlobal(state, index, out string detail), detail);
                yield return ModUiAuditWaitLayout(view, 3, 2f);
                Component tab = state.VisibleTabs[index] as Component;
                Check("local-ui-tab-active-" + target.Id + "-" + index, tab != null && tab.gameObject.activeInHierarchy,
                    "Native selector activates the requested tab");
                string actual = GetMember(tab, "TabName") as string;
                Check("local-ui-tab-name-" + target.Id + "-" + index, actual == target.Tabs[index],
                    "Expected " + target.Tabs[index] + "; actual=" + actual);
                tabRecords.Add(new { index, name = actual, status = "verified" });
            }
            foreach (bool chinese in new[] { false, true })
            {
                LocalUiSetLanguage(languageHost, chinese);
                yield return new WaitForSecondsRealtime(0.7f);
                yield return ModUiAuditWaitLayout(view, 3, 2f);
                if (state != null)
                {
                    Canvas.ForceUpdateCanvases();
                    yield return new WaitForEndOfFrame();
                    LocalUiCheckTabLabels(state, target, chinese);
                }
                string caption = chinese ? LocalUiTranslated(target.English[index], target.Scope) : target.English[index];
                Check("local-ui-caption-" + target.Id + "-" + index + "-" + chinese, LocalUiText(view, caption),
                    "Visible page contains " + caption);
                ModUiAuditCheckVisibility(run, record, view, camera);
                ModUiAuditCapturePage(run, record, view, camera, "local-" + target.Id + "-tab-" + index + (chinese ? "-zh" : "-en"));
            }
            LocalUiSetLanguage(languageHost, false);
            yield return new WaitForSecondsRealtime(0.4f);
        }
        if (state != null && state.OriginalGlobal >= 0)
            Check("local-ui-tab-restore-" + target.Id, ModUiGameplaySelectGlobal(state, state.OriginalGlobal, out string restore), restore);
        record["status"] = "verified"; run.StablePages++;
        Check("local-ui-back-" + target.Id, ModUiAuditDismissFlow(main, flow, out string back), back);
        yield return Wait(() => GetMember(flow, "settings") == null && LocalUiReady(flow) && ModUiAuditViewActive(center), 15f, "local-ui-return-" + target.Id);
        Check("local-ui-category-restored-" + target.Id, ReadBool(center, target.Category + "Selected", false),
            "Returning preserves source category");
        row = LocalUiButton(center, target.Title);
        Check("local-ui-reopen-row-" + target.Id, row != null, "The original row remains available");
        row.onClick.Invoke();
        yield return Wait(() => LocalUiPageId(flow) == target.Id && LocalUiReady(flow) && ModUiAuditViewActive(GetMember(flow, "settings") as Component),
            15f, "local-ui-reopen-" + target.Id);
        Check("local-ui-reopen-source-" + target.Id, ReferenceEquals(GetMember(GetMember(flow, "settings"), "Entry"), entry),
            "Reopening resolves the same entry and host");
        Check("local-ui-reopen-back-" + target.Id, ModUiAuditDismissFlow(main, flow, out back), back);
        yield return Wait(() => GetMember(flow, "settings") == null && LocalUiReady(flow) && ModUiAuditViewActive(center), 15f, "local-ui-reopen-return-" + target.Id);
    }

    IEnumerator LocalUiTools(ModUiAuditRun run, object main, object flow, Component center, object languageHost, Camera camera)
    {
        yield return LocalUiSelectCategory(center, "Library");
        foreach (string[] tool in new[] {
            new[] { "beatsage", "Beat Sage", "BeatSage.UI.BeatSageFlowCoordinator", "BeatSage" },
            new[] { "song-cleaner", "Duplicate songs", "SongDuplicateCleaner.CleanerFlow", "SongDuplicateCleaner" },
            new[] { "beatsage-settings", "Generation settings", "BeatSage.UI.BeatSageFlowCoordinator", "BeatSage" } })
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Button row = LocalUiButton(center, tool[1]);
                Check("local-ui-tool-row-" + tool[0] + "-" + attempt, row != null, "Visible tool row");
                row.onClick.Invoke();
                yield return Wait(() => {
                    object child = ModUiAuditYoungestFlow(flow);
                    return child != null && child.GetType().FullName == tool[2] && ModUiAuditFlowActive(child) &&
                        !ReadBool(child, "isInTransition", true);
                }, 20f, "local-ui-tool-" + tool[0]);
                object childFlow = ModUiAuditYoungestFlow(flow);
                Component root = Resources.FindObjectsOfTypeAll<HMUI.ViewController>().FirstOrDefault(x =>
                    x != null && x.gameObject.activeInHierarchy && x.GetType().Assembly.GetName().Name == tool[3]);
                Check("local-ui-tool-view-" + tool[0] + "-" + attempt, root != null, "Tool presents its own view");
                yield return ModUiAuditWaitLayout(root, 4, 3f);
                if (attempt == 0)
                {
                    var record = LocalUiRecord(run, "LocalModUITool", tool[1], tool[3], tool[2]);
                    foreach (bool chinese in new[] { false, true })
                    {
                        LocalUiSetLanguage(languageHost, chinese);
                        yield return new WaitForSecondsRealtime(0.7f);
                        ModUiAuditCapturePage(run, record, root, camera, "local-tool-" + tool[0] + (chinese ? "-zh" : "-en"));
                    }
                    record["status"] = "verified"; run.StablePages++;
                    LocalUiSetLanguage(languageHost, false);
                    yield return new WaitForSecondsRealtime(0.4f);
                }
                Check("local-ui-tool-back-" + tool[0] + "-" + attempt, ModUiAuditDismissFlow(flow, childFlow, out string detail), detail);
                yield return Wait(() => !ModUiAuditFlowActive(childFlow) && LocalUiReady(flow) && ModUiAuditViewActive(center) &&
                    ReferenceEquals(ModUiAuditYoungestFlow(main), flow), 20f, "local-ui-tool-return-" + tool[0]);
                Check("local-ui-tool-category-" + tool[0] + "-" + attempt, ReadBool(center, "LibrarySelected", false),
                    "Tool returns to the library category");
            }
        }
    }

    IEnumerator LocalUiAdvancedFilters(ModUiAuditRun run, object main, object flow, Component center, object languageHost, Camera camera)
    {
        yield return LocalUiSelectCategory(center, "Library");
        Button row = LocalUiButton(center, "Advanced Filters");
        Check("local-ui-filter-row", row != null, "Visible Advanced Filters row");
        row.onClick.Invoke();
        Type runtimeType = FindTrainingType("AdvancedSongFilters.Runtime");
        object runtime = GetStaticMember(runtimeType, "Current");
        object panel = GetMember(runtime, "panel");
        yield return Wait(() => ReadBool(panel, "IsOpen", false) &&
            ModUiAuditYoungestFlow(main)?.GetType().FullName == "SoloFreePlayFlowCoordinator", 30f, "local-ui-filter-solo");
        object solo = ModUiAuditYoungestFlow(main);
        Component modal = GetMember(panel, "filterModal") as Component;
        Check("local-ui-filter-modal", modal != null && modal.gameObject.activeInHierarchy && !ModUiAuditFlowActive(flow),
            "Advanced Filters opens over native Solo after leaving the center");
        yield return ModUiAuditWaitLayout(modal, 4, 3f);
        var record = LocalUiRecord(run, "LocalModUIFilter", "Advanced Filters", "AdvancedSongFilters", panel.GetType().FullName);
        foreach (bool chinese in new[] { false, true })
        {
            LocalUiSetLanguage(languageHost, chinese);
            yield return new WaitForSecondsRealtime(0.7f);
            ModUiAuditCapturePage(run, record, modal, camera, "local-filters" + (chinese ? "-zh" : "-en"));
        }
        record["status"] = "verified"; run.StablePages++;
        Button cancel = GetMember(panel, "boundCancelButton") as Button;
        Check("local-ui-filter-cancel", cancel != null && cancel.gameObject.activeInHierarchy && cancel.interactable,
            "Visible filter Cancel button is interactable");
        cancel.onClick.Invoke();
        yield return Wait(() => !ReadBool(panel, "IsOpen", true) && ModUiAuditFlowActive(solo) &&
            ReferenceEquals(ModUiAuditYoungestFlow(main), solo), 15f, "local-ui-filter-return-solo");
        Check("local-ui-filter-solo-preserved", ModUiAuditFlowActive(solo), "Closing the filter returns to Solo");
        Check("local-ui-solo-back", ModUiAuditDismissFlow(main, solo, out string detail), detail);
        yield return Wait(() => ModUiAuditFlowActive(main) && !ModUiAuditFlowActive(solo), 20f, "local-ui-solo-return");
    }

    IEnumerator LocalUiCleanup(ModUiAuditRun run)
    {
        object main = GetStaticMember(FindTrainingType("BeatSaberMarkupLanguage.BeatSaberUI"), "MainFlowCoordinator");
        object runtime = GetStaticMember(FindTrainingType("AdvancedSongFilters.Runtime"), "Current");
        object panel = GetMember(runtime, "panel");
        if (ReadBool(panel, "IsOpen", false))
        {
            Button cancel = GetMember(panel, "boundCancelButton") as Button;
            if (cancel == null || !cancel.interactable) run.Block("LocalModUI", "cleanup", "Could not cancel the open filter panel.");
            else { cancel.onClick.Invoke(); yield return new WaitForSecondsRealtime(0.5f); }
        }
        object child = ModUiAuditYoungestFlow(main);
        for (int step = 0; child != null && !ReferenceEquals(child, main) && step < 4; step++)
        {
            if (!ModUiAuditDismissFlow(main, child, out string detail)) { run.Block("LocalModUI", "cleanup", detail); yield break; }
            yield return new WaitForSecondsRealtime(1f);
            child = ModUiAuditYoungestFlow(main);
        }
        if (child != null && !ReferenceEquals(child, main)) run.Block("LocalModUI", "cleanup", "Navigation did not return to the main menu.");
    }
}
