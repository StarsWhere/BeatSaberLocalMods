using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EffectModsSmoke;

/// <summary>
/// A read-only, reflection based audit of the UI that is actually registered by
/// the loaded mods.  This is deliberately separate from the targeted training
/// checks: an optional mod may be absent or may fail to build a page, and that
/// must produce a coverage record while leaving the remaining pages auditable.
/// </summary>
public sealed partial class Runner
{
    sealed class ModUiAuditRun
    {
        internal readonly List<object> Plugins = new();
        internal readonly List<object> Menus = new();
        internal readonly List<object> Pages = new();
        internal readonly List<object> Modals = new();
        internal readonly List<object> Hints = new();
        internal readonly List<object> Entrances = new();
        internal readonly List<string> Blocked = new();
        internal readonly List<Dictionary<string, object>> Snapshots = new();
        internal readonly HashSet<string> SnapshotKeys = new(StringComparer.Ordinal);
        internal readonly HashSet<string> MenuKeys = new(StringComparer.Ordinal);
        internal readonly HashSet<int> CapturedLabels = new();
        internal readonly HashSet<string> ShownHints = new(StringComparer.Ordinal);
        internal readonly HashSet<int> ShownModals = new();
        internal readonly HashSet<string> CapturedHintContracts = new(StringComparer.Ordinal);
        internal string OutputDirectory;
        internal bool TranslatorAvailable;
        internal int ScreenshotCount;
        internal int StablePages;
        internal int BlockedPages;
        internal int ProtectedControls;

        internal void Block(string scope, string entry, string reason)
        {
            Blocked.Add(scope + ":" + entry + ": " + reason);
        }
    }

    ModUiAuditRun modUiAudit;

    /// <summary>
    /// Walks every live BSML settings registration and every safe-to-expand
    /// native page reachable from it.  The caller enables this explicitly with
    /// EFFECT_SMOKE_FULL_MOD_UI_AUDIT=1.
    /// </summary>
    public IEnumerator VerifyAllModUi()
    {
        if (Environment.GetEnvironmentVariable("EFFECT_SMOKE_FULL_MOD_UI_AUDIT") != "1")
            yield break;

        ModUiAuditRun run = new();
        modUiAudit = run;
        run.OutputDirectory = directory;
        ModUiAuditPrepareOutput(run);
        string focus = Environment.GetEnvironmentVariable("EFFECT_SMOKE_UI_FOCUS") ?? "";
        if (focus == "LocalModUI")
        {
            yield return LocalUiSafeAudit(run, VerifyTrainingMenuShortcuts());
            yield return LocalUiSafeAudit(run, LocalUiCleanup(run));
            yield return LocalUiSafeAudit(run, VerifyLocalModUi(run));
            yield return LocalUiSafeAudit(run, LocalUiCleanup(run));
            try { ModUiAuditDiscoverEntrances(run); }
            catch (Exception error) { run.Block("main-menu", "entrance-scan", ModUiAuditException(error)); }
            ModUiAuditWrite(run);
            modUiAudit = null;
            yield break;
        }
        if (focus == "Gameplay")
        {
            yield return ModUiAuditGameplaySetup(run);
            ModUiAuditDiscoverEntrances(run);
            ModUiAuditWrite(run);
            modUiAudit = null;
            yield break;
        }
        if (focus != "Settings") yield return ModUiAuditMainMenuNavigation(run);
        if (focus.StartsWith("Main:", StringComparison.Ordinal))
        {
            ModUiAuditDiscoverEntrances(run);
            ModUiAuditWrite(run);
            modUiAudit = null;
            yield break;
        }

        Type settingsType = FindTrainingType("BeatSaberMarkupLanguage.Settings.BSMLSettings");
        object settings = FindBsmlSettingsInstance(settingsType);
        if (settings == null)
        {
            run.Block("BSML", "BSMLSettings", "BSMLSettings instance is unavailable in the active menu scene.");
            try { ModUiAuditDiscoverEntrances(run); }
            catch (Exception error) { run.Block("main-menu", "entrance-scan", ModUiAuditException(error)); }
            yield return ModUiAuditGameplaySetup(run);
            ModUiAuditWrite(run);
            modUiAudit = null;
            yield break;
        }

        object flow = GetMember(settings, "modSettingsFlowCoordinator") ??
            GetMember(settings, "ModSettingsFlowCoordinator");
        MethodInfo openMenu = ModUiAuditFindOpenMenu(flow);
        MethodInfo presentSettings = settingsType?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == "PresentSettings" && method.GetParameters().Length == 0);
        if (flow == null || presentSettings == null)
        {
            run.Block("BSML", "PresentSettings", "BSMLSettings.modSettingsFlowCoordinator or private PresentSettings() is unavailable.");
        }
        else
        {
            try { presentSettings.Invoke(settings, null); }
            catch (Exception error) { run.Block("BSML", "PresentSettings", "PresentSettings failed: " + ModUiAuditException(error)); }
            yield return ModUiAuditWait(run, () => ModUiAuditFlowActive(flow), 8, 15f);
            yield return ModUiAuditWait(run, () => !ReadBool(flow, "IsAnimating", true) &&
                !ReadBool(flow, "isPresenting", false), 8, 10f);
        }
        IEnumerable registered = GetMember(settings, "SettingsMenus") as IEnumerable;
        if (registered == null)
        {
            run.Block("BSML", "SettingsMenus", "BSMLSettings.SettingsMenus is not enumerable.");
        }
        else
        {
            int index = 0;
            foreach (object entry in registered)
            {
                if (entry == null)
                {
                    run.Block("BSML", "SettingsMenus[" + index + "]", "Registered menu entry is null.");
                    index++;
                    continue;
                }

                string scope = ModUiAuditScopeForMenu(entry);
                string title = ModUiAuditString(GetMember(entry, "Text"), GetMember(entry, "Name"),
                    "SettingsMenu[" + index + "]");
                string resource = ModUiAuditString(GetMember(entry, "Resource"), GetMember(entry, "ResourcePath"));
                string hostType = (GetMember(entry, "Host")?.GetType().FullName) ??
                    ModUiAuditTypeName(GetMember(entry, "HostType"));
                string key = hostType + "|" + resource + "|" + title;
                run.MenuKeys.Add(key);
                run.Menus.Add(new
                {
                    kind = "BSMLSettings",
                    index,
                    title,
                    scope,
                    host = hostType,
                    resource,
                    registered = true,
                    source = "BSMLSettings.SettingsMenus"
                });

                yield return ModUiAuditSettingsMenu(run, flow, openMenu, entry, index, title, scope,
                    resource, hostType);
                index++;
            }

            if (index == 0)
                run.Block("BSML", "SettingsMenus", "SettingsMenus is registered but empty.");
        }

        yield return ModUiAuditCounters(run, flow, openMenu);
        if (focus == "Settings") yield return ModUiAuditConditionalDialogs(run);
        if (focus != "Settings") yield return ModUiAuditGameplaySetup(run);
        try { ModUiAuditDiscoverEntrances(run); }
        catch (Exception error) { run.Block("main-menu", "entrance-scan", ModUiAuditException(error)); }
        ModUiAuditWrite(run);
        modUiAudit = null;
    }

    static string ModUiAuditTypeName(object value)
    {
        if (value is Type type) return type.FullName ?? type.Name;
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    static string ModUiAuditString(params object[] values)
    {
        foreach (object value in values)
        {
            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(text)) return text;
        }
        return string.Empty;
    }

    static string ModUiAuditSafeSlug(string text)
    {
        if (string.IsNullOrEmpty(text)) return "page";
        char[] chars = text.Select(value => char.IsLetterOrDigit(value) || value == '-' || value == '_' ? value : '_').ToArray();
        string result = new string(chars).Trim('_');
        return string.IsNullOrEmpty(result) ? "page" : result.Substring(0, Math.Min(result.Length, 90));
    }

    static void ModUiAuditPrepareOutput(ModUiAuditRun run)
    {
        if (string.IsNullOrEmpty(run.OutputDirectory))
        {
            run.Block("output", "EFFECT_SMOKE_DIR", "EFFECT_SMOKE_DIR is empty; no audit files can be written.");
            return;
        }
        try { Directory.CreateDirectory(run.OutputDirectory); }
        catch (Exception error) { run.Block("output", run.OutputDirectory, error.GetType().Name + ": " + error.Message); }
    }

    static MethodInfo ModUiAuditFindOpenMenu(object flow)
    {
        if (flow == null) return null;
        try
        {
            Type menuType = FindTrainingType("BeatSaberMarkupLanguage.Settings.SettingsMenu");
            return flow.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.Name == "OpenMenu" && method.GetParameters().Length == 1)
                .FirstOrDefault(method =>
                {
                    Type parameter = method.GetParameters()[0].ParameterType;
                    return parameter.FullName == "BeatSaberMarkupLanguage.Settings.SettingsMenu" ||
                        (menuType != null && parameter.IsAssignableFrom(menuType));
                });
        }
        catch { return null; }
    }

    static string ModUiAuditScopeForMenu(object entry)
    {
        object host = GetMember(entry, "Host");
        if (host != null)
        {
            string assembly = host.GetType().Assembly.GetName().Name;
            if (!string.IsNullOrEmpty(assembly)) return assembly;
        }
        object assemblyValue = GetMember(entry, "Assembly");
        if (assemblyValue is Assembly value) return value.GetName().Name;
        return "BSML";
    }

    IEnumerator ModUiAuditSettingsMenu(ModUiAuditRun run, object flow, MethodInfo openMenu,
        object entry, int index, string title, string scope, string resource, string hostType)
    {
        string slug = ModUiAuditSafeSlug(string.IsNullOrEmpty(title) ? hostType : title) + "-" + index;
        Dictionary<string, object> page = new()
        {
            ["kind"] = "BSMLSettings",
            ["index"] = index,
            ["title"] = title,
            ["scope"] = scope,
            ["host"] = hostType,
            ["resource"] = resource,
            ["status"] = "blocked"
        };
        run.Pages.Add(page);

        try { EnsureSettingsMenuSetup(entry); }
        catch (Exception error)
        {
            string detail = ModUiAuditException(error);
            page["reason"] = "SettingsMenu.Setup failed: " + detail;
            run.Block(scope, title, (string)page["reason"]);
            run.BlockedPages++;
            yield break;
        }

        Component view = GetMember(entry, "ViewController") as Component;
        if (view == null)
        {
            page["reason"] = "SettingsMenu.ViewController is unavailable after Setup.";
            run.Block(scope, title, (string)page["reason"]);
            run.BlockedPages++;
            yield break;
        }
        if (flow == null || openMenu == null)
        {
            page["reason"] = "Native settings flow or OpenMenu(SettingsMenu) reflection entry is unavailable.";
            run.Block(scope, title, (string)page["reason"]);
            run.BlockedPages++;
            yield break;
        }

        if (ReadBool(flow, "isPresenting", false))
            yield return ModUiAuditWait(run, () => !ReadBool(flow, "isPresenting", false), 120, 10f);
        try { openMenu.Invoke(flow, new[] { entry }); }
        catch (Exception error)
        {
            page["reason"] = "OpenMenu failed: " + ModUiAuditException(error);
            run.Block(scope, title, (string)page["reason"]);
            run.BlockedPages++;
            yield break;
        }

        yield return ModUiAuditWait(run, () => ModUiAuditViewActive(view), 8, 15f);
        yield return ModUiAuditWaitLayout(view, 4, 4f);
        yield return new WaitForSecondsRealtime(0.6f);
        yield return null;
        Canvas.ForceUpdateCanvases();
        yield return new WaitForEndOfFrame();
        Canvas.ForceUpdateCanvases();

        bool active = ModUiAuditViewActive(view);
        page["active"] = active;
        if (!active)
        {
            page["reason"] = "ViewController did not become active after OpenMenu and layout wait.";
            run.Block(scope, title, (string)page["reason"]);
            run.BlockedPages++;
            yield break;
        }

        Camera camera = FindTrainingMenuCamera();
        page["camera"] = camera == null ? null : camera.name;
        page["status"] = "verified";
        run.StablePages++;
        ModUiAuditCheckVisibility(run, page, view, camera);
        ModUiAuditCapturePage(run, page, view, camera, slug + "-top");
        ModUiAuditCaptureHints(run, page, view, scope);
        ModUiAuditControls(run, page, view);

        yield return ModUiAuditTabs(run, page, view, camera, slug);
        yield return ModUiAuditScroll(run, page, view, camera, slug);
        yield return ModUiAuditRealHoverHints(run, page, view, camera, scope);
        yield return ModUiAuditModals(run, page, view, camera, slug, scope);
        yield return ModUiAuditSettingsSubmenus(run, page, view, camera, slug, scope);
    }

    static bool ModUiAuditViewActive(Component view)
    {
        try
        {
            return view != null && view.gameObject.activeInHierarchy &&
                (ReadBool(view, "isActivated", false) || ReadBool(view, "IsActivated", false) ||
                    view is HMUI.ViewController && ((HMUI.ViewController)view).isActivated);
        }
        catch { return view != null && view.gameObject.activeInHierarchy; }
    }

    IEnumerator ModUiAuditWait(ModUiAuditRun run, Func<bool> predicate, int stableFrames, float seconds)
    {
        float deadline = Time.unscaledTime + Mathf.Max(0.25f, seconds);
        int stable = 0;
        while (Time.unscaledTime < deadline)
        {
            bool ready = false;
            try { ready = predicate(); } catch { }
            stable = ready ? stable + 1 : 0;
            if (stable >= Math.Max(1, stableFrames)) yield break;
            yield return null;
        }
    }

    static string ModUiAuditLayoutSignature(Component view)
    {
        if (view == null) return string.Empty;
        try
        {
            return string.Join("|", view.GetComponentsInChildren<RectTransform>(true)
                .Where(value => value != null && value.gameObject.activeInHierarchy)
                .Take(160)
                .Select(value => value.GetInstanceID().ToString(CultureInfo.InvariantCulture) + ":" +
                    value.rect.x.ToString("R", CultureInfo.InvariantCulture) + "," +
                    value.rect.y.ToString("R", CultureInfo.InvariantCulture) + "," +
                    value.rect.width.ToString("R", CultureInfo.InvariantCulture) + "," +
                    value.rect.height.ToString("R", CultureInfo.InvariantCulture)));
        }
        catch { return string.Empty; }
    }

    IEnumerator ModUiAuditWaitLayout(Component view, int stableFrames, float seconds)
    {
        float started = Time.unscaledTime;
        float deadline = started + Mathf.Max(0.7f, seconds);
        string previous = null;
        int stable = 0;
        while (Time.unscaledTime < deadline)
        {
            Canvas.ForceUpdateCanvases();
            string current = ModUiAuditLayoutSignature(view);
            stable = !string.IsNullOrEmpty(current) && current == previous ? stable + 1 : 0;
            previous = current;
            if (stable >= Math.Max(1, stableFrames) && Time.unscaledTime - started >= 0.65f) yield break;
            yield return null;
        }
    }

    static string ModUiAuditPath(Transform transform)
    {
        if (transform == null) return string.Empty;
        List<string> names = new();
        for (Transform current = transform; current != null; current = current.parent) names.Add(current.name);
        names.Reverse();
        return string.Join("/", names.ToArray());
    }

    static string ModUiAuditScope(Transform transform, string fallback = "Common")
    {
        Type scopeType = FindTrainingType("ModUiChinese.UiScope");
        try
        {
            for (Transform current = transform; current != null; current = current.parent)
            {
                Component marker = scopeType == null ? null : current.GetComponent(scopeType);
                string marked = ModUiAuditString(GetMember(marker, "Scope"));
                if (!string.IsNullOrEmpty(marked)) return marked;
                if (current.name.StartsWith("Counters+", StringComparison.Ordinal)) return "Counters+";
                if (current.name.StartsWith("Cam2_", StringComparison.Ordinal)) return "Camera2";
                foreach (MonoBehaviour component in current.GetComponents<MonoBehaviour>())
                {
                    if (component == null) continue;
                    string assembly = component.GetType().Assembly.GetName().Name;
                    if (assembly == "ModUiChinese") continue;
                    if (!string.IsNullOrEmpty(assembly) && assembly != "Assembly-CSharp" && assembly != "UnityEngine")
                    {
                        Type translator = FindTrainingType("ModUiChinese.UiTranslator");
                        object engine = GetStaticMember(translator, "Engine");
                        MethodInfo hasScope = engine?.GetType().GetMethod("HasScope", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (hasScope != null && Convert.ToBoolean(hasScope.Invoke(engine, new object[] { assembly }), CultureInfo.InvariantCulture))
                            return assembly;
                    }
                }
            }
        }
        catch { }
        return fallback;
    }

    static bool ModUiAuditReadBool(object value, string name, bool fallback)
    {
        if (string.IsNullOrEmpty(name) && value is bool direct) return direct;
        object member = ReadMemberAny(value, name);
        if (member == null) return fallback;
        try { return Convert.ToBoolean(member, CultureInfo.InvariantCulture); }
        catch { return fallback; }
    }

    void ModUiAuditCheckVisibility(ModUiAuditRun run, Dictionary<string, object> page, Component view, Camera camera)
    {
        try
        {
            TMP_Text[] texts = view.GetComponentsInChildren<TMP_Text>(true)
                .Where(value => value != null && value.gameObject.activeInHierarchy && value.enabled).ToArray();
            Selectable[] controls = view.GetComponentsInChildren<Selectable>(true)
                .Where(value => value != null && value.gameObject.activeInHierarchy && value.enabled).ToArray();
            int visibleText = texts.Length;
            int visibleControls = controls.Length;
            if (camera != null)
            {
                visibleText = texts.Count(value => ModUiAuditElementVisible(value.rectTransform, camera));
                visibleControls = controls.Count(value => value.transform is RectTransform rect &&
                    ModUiAuditElementVisible(rect, camera));
                page["viewport"] = "per-element active clipping masks";
            }
            page["activeText"] = texts.Length;
            page["activeControls"] = controls.Length;
            page["visibleText"] = visibleText;
            page["visibleControls"] = visibleControls;
            page["visibility"] = visibleText > 0 ? "visible" : "blocked-no-visible-content";
            if (visibleText == 0)
                run.Block(ModUiAuditString(page["scope"]), ModUiAuditString(page["title"]),
                    "Active page has no visible text/control pair; text=" + texts.Length + "; controls=" + controls.Length);
        }
        catch (Exception error)
        {
            page["visibility"] = "blocked-check-failed";
            page["visibilityReason"] = ModUiAuditException(error);
            run.Block(ModUiAuditString(page["scope"]), ModUiAuditString(page["title"]),
                "Visibility check failed: " + ModUiAuditException(error));
        }
    }

    static bool ModUiAuditElementVisible(RectTransform element, Camera camera)
    {
        Rect viewport = new Rect(0, 0, Mathf.Max(1, camera.pixelWidth), Mathf.Max(1, camera.pixelHeight));
        foreach (RectMask2D mask in element.GetComponentsInParent<RectMask2D>(true))
        {
            if (!mask.isActiveAndEnabled) continue;
            Rect bounds = TrainingScreenRect(mask.rectTransform, camera);
            viewport = Rect.MinMaxRect(Mathf.Max(viewport.xMin, bounds.xMin), Mathf.Max(viewport.yMin, bounds.yMin),
                Mathf.Min(viewport.xMax, bounds.xMax), Mathf.Min(viewport.yMax, bounds.yMax));
            if (viewport.width <= 0 || viewport.height <= 0) return false;
        }
        return TrainingElementIntersects(element, camera, true, viewport);
    }

    static string ModUiAuditTextFor(Selectable selectable)
    {
        if (selectable == null) return string.Empty;
        try
        {
            TMP_Text text = selectable.GetComponentInChildren<TMP_Text>(true);
            return text == null ? string.Empty : text.text ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    void ModUiAuditControls(ModUiAuditRun run, Dictionary<string, object> page, Component view)
    {
        List<object> protectedControls = new();
        string[] forbidden = { "download", "delete", "remove", "upload", "external", "http", "quit", "exit", "save", "preset", "refresh", "clear", "reset", "calibrate", "apply", "submit", "play", "practice" };
        try
        {
            foreach (Button button in view.GetComponentsInChildren<Button>(true))
            {
                if (button == null) continue;
                string text = ModUiAuditTextFor(button);
                string label = (button.name + " " + text).ToLowerInvariant();
                bool protectedAction = forbidden.Any(label.Contains);
                if (protectedAction) run.ProtectedControls++;
                protectedControls.Add(new
                {
                    path = ModUiAuditPath(button.transform),
                    name = button.name,
                    text,
                    interactable = button.interactable,
                    active = button.gameObject.activeInHierarchy,
                    action = protectedAction ? "skipped-protected-action" : "skipped-untrusted-button"
                });
            }
        }
        catch (Exception error) { page["controlScanError"] = ModUiAuditException(error); }
        page["protectedControls"] = protectedControls;
    }

    IEnumerator ModUiAuditScroll(ModUiAuditRun run, Dictionary<string, object> page,
        Component view, Camera camera, string slug)
    {
        Component scroll = null;
        try { scroll = FindTrainingScrollContainer(view); }
        catch { }
        if (scroll == null)
        {
            page["scroll"] = "none";
            yield break;
        }

        page["scroll"] = scroll.GetType().FullName;
        MethodInfo toPosition = AccessTools.Method(scroll.GetType(), "ScrollTo", new[] { typeof(float), typeof(bool) });
        MethodInfo toEnd = AccessTools.Method(scroll.GetType(), "ScrollToEnd", new[] { typeof(bool) }) ??
            AccessTools.Method(typeof(HMUI.ScrollView), "ScrollToEnd", new[] { typeof(bool) });
        if (toPosition == null || toEnd == null)
        {
            page["scrollStatus"] = "blocked-api-missing";
            run.Block(ModUiAuditString(page["scope"]), ModUiAuditString(page["title"]),
                "ScrollView is present but ScrollTo/ScrollToEnd reflection signatures are unavailable.");
            yield break;
        }

        string scrollError = string.Empty;
        try { toPosition.Invoke(scroll, new object[] { 0f, false }); }
        catch (Exception error) { scrollError = ModUiAuditException(error); }
        if (!string.IsNullOrEmpty(scrollError))
        {
            page["scrollStatus"] = "blocked-invoke";
            page["scrollReason"] = scrollError;
            run.Block(ModUiAuditString(page["scope"]), ModUiAuditString(page["title"]),
                "Scroll traversal failed: " + scrollError);
            yield break;
        }
        yield return ModUiAuditWaitLayout(view, 3, 1.25f);
        ModUiAuditCapturePage(run, page, view, camera, slug + "-top-again");
        try { toEnd.Invoke(scroll, new object[] { false }); }
        catch (Exception error) { scrollError = ModUiAuditException(error); }
        if (!string.IsNullOrEmpty(scrollError))
        {
            page["scrollStatus"] = "blocked-invoke";
            page["scrollReason"] = scrollError;
            run.Block(ModUiAuditString(page["scope"]), ModUiAuditString(page["title"]),
                "Scroll-to-end failed: " + scrollError);
            yield break;
        }
        yield return ModUiAuditWaitLayout(view, 4, 1.5f);
        ModUiAuditCheckVisibility(run, page, view, camera);
        ModUiAuditCapturePage(run, page, view, camera, slug + "-bottom");
        try { toPosition.Invoke(scroll, new object[] { 0f, false }); }
        catch (Exception error) { scrollError = ModUiAuditException(error); }
        page["scrollStatus"] = string.IsNullOrEmpty(scrollError) ? "verified-top-and-bottom" : "partial-reset-failed";
        if (!string.IsNullOrEmpty(scrollError))
            run.Block(ModUiAuditString(page["scope"]), ModUiAuditString(page["title"]), "Scroll reset failed: " + scrollError);
    }

    static bool ModUiAuditLooksLikeTab(Component component)
    {
        if (component == null) return false;
        string type = component.GetType().FullName ?? component.GetType().Name;
        string path = ModUiAuditPath(component.transform);
        string value = (type + " " + path).ToLowerInvariant();
        bool explicitTab = value.IndexOf("tab", StringComparison.Ordinal) >= 0 ||
            value.IndexOf("category", StringComparison.Ordinal) >= 0 ||
            value.IndexOf("page", StringComparison.Ordinal) >= 0 ||
            value.IndexOf("scope", StringComparison.Ordinal) >= 0;
        if (!explicitTab && type.IndexOf("TabControl", StringComparison.Ordinal) < 0) return false;
        if (type.IndexOf("SegmentedControl", StringComparison.Ordinal) < 0 &&
            type.IndexOf("TabControl", StringComparison.Ordinal) < 0) return false;
        return AccessTools.Method(component.GetType(), "SelectCellWithNumber", new[] { typeof(int) }) != null;
    }

    static int ModUiAuditTabCount(object component)
    {
        foreach (string member in new[] { "numberOfCells", "NumberOfCells", "cellCount", "CellCount" })
        {
            object value = ReadMemberAny(component, member);
            if (value != null && int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out int count)) return count;
        }
        foreach (string member in new[] { "cells", "_cells", "Cells", "dataItems", "_dataItems", "data" })
        {
            object value = ReadMemberAny(component, member);
            if (value is ICollection collection) return collection.Count;
            if (value is IEnumerable values)
            {
                int count = 0;
                foreach (object ignored in values) if (++count > 64) break;
                return count;
            }
        }
        return 0;
    }

    static int ModUiAuditSelectedIndex(object component)
    {
        foreach (string member in new[] { "selectedCellNumber", "_selectedCellNumber", "selectedCellIndex", "_selectedCellIndex", "selectedIndex" })
        {
            object value = ReadMemberAny(component, member);
            if (value != null && int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out int index)) return index;
        }
        return -1;
    }

    IEnumerator ModUiAuditTabs(ModUiAuditRun run, Dictionary<string, object> page,
        Component view, Camera camera, string slug)
    {
        Type selectorType = FindTrainingType("BeatSaberMarkupLanguage.Components.TabSelector");
        Component[] selectors = selectorType == null ? Array.Empty<Component>() : view.GetComponentsInChildren(selectorType, true).OfType<Component>()
            .Where(value => value != null && value.gameObject.activeInHierarchy).ToArray();
        if (selectors.Length > 0)
        {
            foreach (Component selector in selectors) yield return ModUiAuditBsmlSelector(run, page, view, selector, camera, slug);
            yield break;
        }
        MonoBehaviour[] candidates;
        try { candidates = view.GetComponentsInChildren<MonoBehaviour>(true); }
        catch { yield break; }
        int controlNumber = 0;
        foreach (MonoBehaviour candidate in candidates.Where(ModUiAuditLooksLikeTab).Distinct())
        {
            MethodInfo select = AccessTools.Method(candidate.GetType(), "SelectCellWithNumber", new[] { typeof(int) });
            int count = ModUiAuditTabCount(candidate);
            Dictionary<string, object> result = new()
            {
                ["path"] = ModUiAuditPath(candidate.transform),
                ["type"] = candidate.GetType().FullName,
                ["count"] = count,
                ["status"] = count > 1 && select != null ? "pending" : "blocked"
            };
            List<object> tabResults = page.TryGetValue("tabs", out object existing) && existing is List<object> list
                ? list : new List<object>();
            if (!page.ContainsKey("tabs")) page["tabs"] = tabResults;
            tabResults.Add(result);
            if (count <= 1 || select == null)
            {
                result["reason"] = "No safe multi-cell tab signature was available.";
                run.Block(ModUiAuditString(page["scope"]), ModUiAuditString(page["title"]),
                    "Tab candidate " + result["path"] + " has no traversable cells.");
                controlNumber++;
                continue;
            }

            int original = ModUiAuditSelectedIndex(candidate);
            List<object> cells = new();
            for (int cell = 0; cell < Math.Min(count, 64); cell++)
            {
                Exception cellError = null;
                try
                {
                    select.Invoke(candidate, new object[] { cell });
                }
                catch (Exception error) { cellError = error; }
                if (cellError != null)
                {
                    cells.Add(new { index = cell, status = "blocked", reason = ModUiAuditException(cellError) });
                    run.Block(ModUiAuditString(page["scope"]), ModUiAuditString(page["title"]),
                        "Tab " + result["path"] + " cell " + cell + " failed: " + ModUiAuditException(cellError));
                    continue;
                }
                yield return ModUiAuditWaitLayout(view, 3, 1.25f);
                yield return new WaitForSecondsRealtime(0.6f);
                Canvas.ForceUpdateCanvases();
                string cellSlug = slug + "-tab-" + controlNumber + "-" + cell;
                ModUiAuditCapturePage(run, page, view, camera, cellSlug);
                ModUiAuditCaptureHints(run, page, view, ModUiAuditString(page["scope"]));
                yield return ModUiAuditRealHoverHints(run, page, view, camera, ModUiAuditString(page["scope"]));
                cells.Add(new { index = cell, status = "verified" });
            }
            if (original >= 0 && original < count)
            {
                try { select.Invoke(candidate, new object[] { original }); }
                catch (Exception error) { result["restoreReason"] = ModUiAuditException(error); }
            }
            result["status"] = cells.OfType<object>().Any() && cells.All(value =>
                value.ToString().IndexOf("blocked", StringComparison.Ordinal) < 0) ? "verified" : "partial";
            result["cells"] = cells;
            result["original"] = original;
            result["restored"] = original >= 0 && ModUiAuditSelectedIndex(candidate) == original;
            if (original < 0)
            {
                result["restoreStatus"] = "unknown-original-selection";
                run.Block(ModUiAuditString(page["scope"]), ModUiAuditString(page["title"]),
                    "Tab " + result["path"] + " has no readable selected-cell index; selection restoration cannot be proven.");
            }
            controlNumber++;
        }
        if (controlNumber == 0) page["tabs"] = "none-detected-by-safe-whitelist";
    }

    static MethodInfo ModUiAuditModalMethod(Component modal, string name)
    {
        if (modal == null) return null;
        return modal.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.Name == name && !method.IsGenericMethod)
            .OrderBy(method => method.GetParameters().Length)
            .FirstOrDefault();
    }

    static bool ModUiAuditArgument(object value, ParameterInfo parameter, out object argument)
    {
        Type type = parameter.ParameterType;
        if (parameter.HasDefaultValue)
        {
            argument = parameter.DefaultValue;
            return true;
        }
        if (!type.IsValueType || typeof(Delegate).IsAssignableFrom(type))
        {
            argument = null;
            return true;
        }
        if (type == typeof(bool)) { argument = false; return true; }
        if (type.IsEnum) { argument = Enum.GetValues(type).GetValue(0); return true; }
        if (type == typeof(int) || type == typeof(float) || type == typeof(double))
        {
            argument = Activator.CreateInstance(type);
            return true;
        }
        argument = null;
        return false;
    }

    static bool ModUiAuditInvokeModal(Component modal, string name, out string signature, out string error)
    {
        signature = string.Empty;
        error = string.Empty;
        MethodInfo method = ModUiAuditModalMethod(modal, name);
        if (method == null) { error = "HMUI.ModalView." + name + " has no reflectable overload."; return false; }
        signature = method.ToString();
        ParameterInfo[] parameters = method.GetParameters();
        object[] arguments = new object[parameters.Length];
        for (int index = 0; index < parameters.Length; index++)
            if (!ModUiAuditArgument(modal, parameters[index], out arguments[index]))
            {
                error = "Unsupported required parameter " + parameters[index].ParameterType.FullName;
                return false;
            }
        try
        {
            method.Invoke(modal, arguments);
            return true;
        }
        catch (Exception exception) { error = ModUiAuditException(exception); return false; }
    }

    IEnumerator ModUiAuditModals(ModUiAuditRun run, Dictionary<string, object> page,
        Component view, Camera camera, string slug, string fallbackScope)
    {
        Type modalType = FindTrainingType("HMUI.ModalView");
        if (modalType == null)
        {
            page["modals"] = "blocked-type-missing";
            run.Block(fallbackScope, ModUiAuditString(page["title"]), "HMUI.ModalView type is unavailable.");
            yield break;
        }
        Component[] modals;
        try { modals = view.GetComponentsInChildren(modalType, true).OfType<Component>().Distinct().ToArray(); }
        catch (Exception error)
        {
            page["modals"] = "blocked-scan";
            run.Block(fallbackScope, ModUiAuditString(page["title"]), "Modal scan failed: " + ModUiAuditException(error));
            yield break;
        }
        page["modalCount"] = modals.Length;
        for (int index = 0; index < modals.Length; index++)
        {
            Component modal = modals[index];
            if (modal != null && !run.ShownModals.Add(modal.GetInstanceID())) continue;
            bool wasActive = modal != null && modal.gameObject.activeInHierarchy;
            string scope = ModUiAuditScope(modal?.transform, fallbackScope);
            Dictionary<string, object> record = new()
            {
                ["page"] = ModUiAuditString(page["title"]),
                ["index"] = index,
                ["path"] = ModUiAuditPath(modal?.transform),
                ["type"] = modal?.GetType().FullName,
                ["scope"] = scope,
                ["initialActive"] = wasActive,
                ["status"] = "blocked"
            };
            run.Modals.Add(record);
            if (modal == null)
            {
                record["reason"] = "Modal component became null during scan.";
                run.Block(scope, ModUiAuditString(page["title"]), (string)record["reason"]);
                continue;
            }

            bool shown = ModUiAuditInvokeModal(modal, "Show", out string showSignature, out string showError);
            record["showSignature"] = showSignature;
            if (!shown)
            {
                record["reason"] = showError;
                run.Block(scope, ModUiAuditString(page["title"]), "Modal Show blocked: " + showError);
                continue;
            }
            yield return ModUiAuditWait(run, () => modal.gameObject.activeInHierarchy, 5, 5f);
            yield return ModUiAuditWaitLayout(view, 3, 1.25f);
            yield return new WaitForSecondsRealtime(0.6f);
            yield return null;
            Canvas.ForceUpdateCanvases();
            string modalSlug = slug + "-modal-" + index;
            ModUiAuditCapturePage(run, page, view, camera, modalSlug);
            ModUiAuditCaptureHints(run, page, view, scope);
            yield return ModUiAuditRealHoverHints(run, page, view, camera, scope);
            bool hidden = true;
            string hideSignature = string.Empty;
            string hideError = string.Empty;
            if (!wasActive)
                hidden = ModUiAuditInvokeModal(modal, "Hide", out hideSignature, out hideError);
            record["hideSignature"] = hideSignature;
            record["hideError"] = hideError;
            record["restored"] = wasActive || hidden;
            record["status"] = hidden ? "verified" : "partial";
            if (!hidden)
                run.Block(scope, ModUiAuditString(page["title"]), "Modal Hide failed: " + hideError);
            run.CapturedLabels.Add(modal.GetInstanceID());
        }
    }

    void ModUiAuditCapturePage(ModUiAuditRun run, Dictionary<string, object> page,
        Component view, Camera camera, string label)
    {
        string safe = ModUiAuditSafeSlug(label);
        if (camera != null && !string.IsNullOrEmpty(run.OutputDirectory))
        {
            try
            {
                CaptureTrainingUiPage("mod-ui-audit-" + safe, camera, view);
                run.ScreenshotCount++;
            }
            catch (Exception error)
            {
                run.Block(ModUiAuditString(page["scope"]), ModUiAuditString(page["title"]),
                    "Screenshot capture " + safe + " failed: " + ModUiAuditException(error));
            }
        }
        else if (camera == null)
        {
            run.Block(ModUiAuditString(page["scope"]), ModUiAuditString(page["title"]),
                "No active MenuMainCamera was available for screenshot capture.");
        }
        ModUiAuditCaptureTranslator(run, label);
        ModUiAuditCaptureHints(run, page, view, ModUiAuditString(page["scope"]));
        phase = "mod-ui:" + label;
        File.WriteAllText(Path.Combine(directory, "progress.json"), JsonConvert.SerializeObject(new { phase, elapsed = timer.Elapsed.TotalSeconds }));
        ModUiAuditWrite(run, false);
    }

    void ModUiAuditCaptureTranslator(ModUiAuditRun run, string label)
    {
        Type translator = FindTrainingType("ModUiChinese.UiTranslator");
        MethodInfo capture = translator?.GetMethod("Capture", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (capture == null)
        {
            run.Block("ModUiChinese", "UiTranslator.Capture", "The runtime translator capture API is unavailable.");
            ModUiAuditCaptureFallbackTexts(run, label);
            return;
        }
        run.TranslatorAvailable = true;
        try
        {
            object values = capture.Invoke(null, null);
            if (values is IEnumerable enumerable)
                foreach (object value in enumerable) ModUiAuditAddCapturedObject(run, value, label);
            else run.Block("ModUiChinese", "UiTranslator.Capture", "Capture returned a non-enumerable value.");
        }
        catch (Exception error)
        {
            run.Block("ModUiChinese", "UiTranslator.Capture", ModUiAuditException(error));
            ModUiAuditCaptureFallbackTexts(run, label);
        }
    }

    static void ModUiAuditAddCapturedObject(ModUiAuditRun run, object value, string label)
    {
        if (value == null) return;
        string path = ModUiAuditString(GetMember(value, "path"), GetMember(value, "Path"));
        string text = ModUiAuditString(GetMember(value, "text"), GetMember(value, "Text"));
        string source = ModUiAuditString(GetMember(value, "source"), GetMember(value, "Source"), text);
        string scope = ModUiAuditString(GetMember(value, "scope"), GetMember(value, "Scope"), "Common");
        bool protectedData = ModUiAuditReadBool(value, "protectedData", false);
        bool localizationEnabled = ModUiAuditReadBool(value, "localizationEnabled", true);
        bool scopeEnabled = ModUiAuditReadBool(value, "scopeEnabled", true);
        string key = path + "\u001f" + text + "\u001f" + source + "\u001f" + scope + "\u001f" + label;
        if (!run.SnapshotKeys.Add(key)) return;
        run.Snapshots.Add(new Dictionary<string, object>
        {
            ["path"] = path,
            ["text"] = text,
            ["source"] = source,
            ["scope"] = scope,
            ["protectedData"] = protectedData,
            ["dataSegments"] = GetMember(value, "dataSegments") ?? Array.Empty<string>(),
            ["localizationEnabled"] = localizationEnabled,
            ["scopeEnabled"] = scopeEnabled,
            ["capture"] = label
        });
    }

    void ModUiAuditCaptureFallbackTexts(ModUiAuditRun run, string label)
    {
        try
        {
            foreach (TMP_Text text in Resources.FindObjectsOfTypeAll<TMP_Text>().Where(value => value != null && value.gameObject.activeInHierarchy))
            {
                string display = text.text ?? string.Empty;
                string path = ModUiAuditPath(text.transform);
                string scope = ModUiAuditScope(text.transform);
                string key = path + "\u001f" + display + "\u001f" + display + "\u001f" + scope + "\u001f" + label;
                if (!run.SnapshotKeys.Add(key)) continue;
                run.Snapshots.Add(new Dictionary<string, object>
                {
                    ["path"] = path,
                    ["text"] = display,
                    ["source"] = display,
                    ["scope"] = scope,
                    ["protectedData"] = true,
                    ["localizationEnabled"] = false,
                    ["scopeEnabled"] = false,
                    ["capture"] = label,
                    ["captureWarning"] = "UiTranslator.Capture unavailable; protected fallback prevents false localization findings."
                });
            }
        }
        catch (Exception error) { run.Block("fallback", "TMP_Text", ModUiAuditException(error)); }
    }

    static string ModUiAuditTrackedSource(Type translator, TMP_Text text, out string trackedScope)
    {
        trackedScope = string.Empty;
        if (translator == null || text == null) return string.Empty;
        try
        {
            object value = AccessTools.Field(translator, "tracked")?.GetValue(null);
            if (!(value is IDictionary dictionary)) return string.Empty;
            foreach (DictionaryEntry item in dictionary)
            {
                object tracked = item.Value;
                TMP_Text candidate = GetMember(tracked, "Text") as TMP_Text;
                if (!ReferenceEquals(candidate, text)) continue;
                trackedScope = ModUiAuditString(GetMember(tracked, "Scope"));
                return ModUiAuditString(GetMember(tracked, "Source"));
            }
        }
        catch { }
        return string.Empty;
    }

    string ModUiAuditProbeTooltip(string source, string scope, out bool translated, out string detail)
    {
        translated = false;
        detail = string.Empty;
        Type translator = FindTrainingType("ModUiChinese.UiTranslator");
        MethodInfo prefix = translator?.GetMethod("TextPrefix", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (prefix == null) { detail = "UiTranslator.TextPrefix is unavailable."; return source; }
        GameObject probe = null;
        try
        {
            probe = new GameObject("EffectSmoke.ModUiAuditTooltipProbe", typeof(RectTransform), typeof(TextMeshProUGUI));
            Type scopeType = FindTrainingType("ModUiChinese.UiScope");
            if (scopeType != null)
            {
                Component marker = probe.AddComponent(scopeType);
                PropertyInfo scopeProperty = scopeType.GetProperty("Scope", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (scopeProperty != null && scopeProperty.CanWrite) scopeProperty.SetValue(marker, scope, null);
                else AccessTools.Field(scopeType, "Scope")?.SetValue(marker, scope);
            }
            object[] arguments = { probe.GetComponent<TMP_Text>(), source };
            prefix.Invoke(null, arguments);
            string result = Convert.ToString(arguments[1], CultureInfo.InvariantCulture) ?? source;
            translated = !string.Equals(result, source, StringComparison.Ordinal);
            detail = "TextPrefix probe scope=" + scope;
            return result;
        }
        catch (Exception error)
        {
            detail = ModUiAuditException(error);
            return source;
        }
        finally { if (probe != null) UnityEngine.Object.Destroy(probe); }
    }

    void ModUiAuditCaptureHints(ModUiAuditRun run, Dictionary<string, object> page, Component view, string fallbackScope)
    {
        if (view == null) return;
        try
        {
            foreach (HMUI.HoverHint hint in view.GetComponentsInChildren<HMUI.HoverHint>(true))
            {
                if (hint == null) continue;
                string actual = ModUiAuditString(ReadMemberAny(hint, "text", "Text"));
                if (string.IsNullOrEmpty(actual)) continue;
                TMP_Text child = hint.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault();
                Type translator = FindTrainingType("ModUiChinese.UiTranslator");
                string trackedScope;
                string source = ModUiAuditTrackedSource(translator, child, out trackedScope);
                string scope = string.IsNullOrEmpty(trackedScope) ? ModUiAuditScope(hint.transform, fallbackScope) : trackedScope;
                if (string.IsNullOrEmpty(source)) source = actual;
                string path = ModUiAuditPath(hint.transform) + "/HoverHint";
                if (!run.CapturedHintContracts.Add(scope + "|" + path + "|" + source + "|" + actual)) continue;
                bool translated;
                string probeDetail;
                string displayed = ModUiAuditProbeTooltip(source, scope, out translated, out probeDetail);
                bool localizationEnabled = ModUiAuditLocalizationEnabled();
                bool scopeEnabled = ModUiAuditScopeEnabled(scope);
                bool protectedData = hint.GetComponentInParent<MonoBehaviour>() != null &&
                    hint.GetComponentsInParent<MonoBehaviour>(true).Any(component => component != null &&
                        component.GetType().Name.IndexOf("UiDataHint", StringComparison.Ordinal) >= 0);
                string key = path + "\u001f" + displayed + "\u001f" + source + "\u001f" + scope;
                if (run.SnapshotKeys.Add(key))
                    run.Snapshots.Add(new Dictionary<string, object>
                    {
                        ["path"] = path,
                        ["text"] = displayed,
                        ["source"] = source,
                        ["scope"] = scope,
                        ["protectedData"] = protectedData,
                        ["localizationEnabled"] = localizationEnabled,
                        ["scopeEnabled"] = scopeEnabled,
                        ["hoverHint"] = true,
                        ["actualHintText"] = actual,
                        ["translationProbe"] = translated,
                        ["translationProbeDetail"] = probeDetail,
                        ["capture"] = ModUiAuditString(page["title"])
                    });
                run.Hints.Add(new
                {
                    kind = "caption-contract",
                    page = ModUiAuditString(page["title"]),
                    path,
                    scope,
                    source,
                    text = actual,
                    probeText = displayed,
                    probeApplied = translated,
                    protectedData,
                    status = string.IsNullOrEmpty(probeDetail) ? "blocked" : "verified",
                    detail = probeDetail
                });
            }
        }
        catch (Exception error)
        {
            run.Block(fallbackScope, ModUiAuditString(page["title"]), "HoverHint scan failed: " + ModUiAuditException(error));
        }
    }

    IEnumerator ModUiAuditRealHoverHints(ModUiAuditRun run, Dictionary<string, object> page,
        Component view, Camera camera, string fallbackScope)
    {
        Type controllerType = FindTrainingType("HMUI.HoverHintController");
        if (controllerType == null)
        {
            run.Block(fallbackScope, ModUiAuditString(page["title"]), "HMUI.HoverHintController type is unavailable; literal probe was still attempted.");
            yield break;
        }
        Component controller = null;
        try
        {
            controller = Resources.FindObjectsOfTypeAll(controllerType).OfType<Component>()
                .FirstOrDefault(value => value != null && value.gameObject.scene.IsValid() && value.gameObject.activeInHierarchy);
        }
        catch { }
        if (controller == null)
        {
            run.Block(fallbackScope, ModUiAuditString(page["title"]), "No active HoverHintController was available; literal probe was still attempted.");
            yield break;
        }
        MethodInfo show = controllerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.Name == "SetupAndShowHintPanel" && method.GetParameters().Length == 1)
            .FirstOrDefault();
        MethodInfo hide = controllerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.Name == "HideHintInstant" && method.GetParameters().Length == 1)
            .FirstOrDefault();
        if (show == null || hide == null)
        {
            run.Block(fallbackScope, ModUiAuditString(page["title"]),
                "HoverHintController.SetupAndShowHintPanel/HideHintPanel signature is unavailable; literal probe was still attempted.");
            yield break;
        }

        HMUI.HoverHint[] hints;
        try { hints = view.GetComponentsInChildren<HMUI.HoverHint>(true); }
        catch { yield break; }
        int index = 0;
        foreach (HMUI.HoverHint hint in hints)
        {
            if (hint == null || !hint.gameObject.activeInHierarchy || String.IsNullOrWhiteSpace(hint.text) || hint.text.Trim().Length <= 1) continue;
            string hintKey = fallbackScope + "|" + ModUiAuditPath(hint.transform) + "|" + hint.text;
            if (!run.ShownHints.Add(hintKey)) continue;
            Dictionary<string, object> result = new()
            {
                ["kind"] = "native-hover",
                ["scope"] = fallbackScope,
                ["page"] = ModUiAuditString(page["title"]),
                ["path"] = ModUiAuditPath(hint.transform),
                ["controller"] = controller.GetType().FullName,
                ["index"] = index,
                ["status"] = "blocked"
            };
            index++;
            Exception showError = null;
            try { show.Invoke(controller, new object[] { hint }); }
            catch (Exception error) { showError = error; }
            if (showError != null)
            {
                result["reason"] = ModUiAuditException(showError);
                run.Block(fallbackScope, ModUiAuditString(page["title"]), "Real hover display failed: " + ModUiAuditException(showError));
            }
            else
            {
                yield return new WaitForSecondsRealtime(0.6f);
                try
                {
                    Canvas.ForceUpdateCanvases();
                    ModUiAuditCaptureTranslator(run, "hover-real-" + ModUiAuditSafeSlug(ModUiAuditString(page["title"])) + "-" + index);
                    ModUiAuditCaptureHints(run, page, view, fallbackScope);
                    result["status"] = "verified";
                }
                catch (Exception error)
                {
                    result["reason"] = ModUiAuditException(error);
                    run.Block(fallbackScope, ModUiAuditString(page["title"]), "Real hover capture failed: " + ModUiAuditException(error));
                }
            }
            try { hide.Invoke(controller, new object[] { hint }); }
            catch (Exception error) { result["hideReason"] = ModUiAuditException(error); }
            run.Hints.Add(result);
            yield return null;
        }
    }

    static bool ModUiAuditLocalizationEnabled()
    {
        Type state = FindTrainingType("ModUiChinese.LocalizationState");
        object value = GetStaticMember(state, "Enabled");
        return value == null || ModUiAuditReadBool(value, string.Empty, true);
    }

    static bool ModUiAuditScopeEnabled(string scope)
    {
        Type translator = FindTrainingType("ModUiChinese.UiTranslator");
        object engine = GetStaticMember(translator, "Engine");
        if (engine == null) return false;
        try
        {
            MethodInfo method = engine.GetType().GetMethod("IsScopeEnabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return method == null || Convert.ToBoolean(method.Invoke(engine, new object[] { scope }), CultureInfo.InvariantCulture);
        }
        catch { return false; }
    }

    IEnumerator ModUiAuditCounters(ModUiAuditRun run, object flow, MethodInfo openMenu)
    {
        Type plugin = FindTrainingType("CountersPlus.Plugin");
        object loaded = GetStaticMember(plugin, "LoadedCustomCounters");
        if (plugin == null)
        {
            run.Block("Counters+", "LoadedCustomCounters", "CountersPlus.Plugin is not loaded; no custom-counter registry is available.");
            yield break;
        }
        if (!(loaded is IEnumerable counters))
        {
            run.Block("Counters+", "LoadedCustomCounters", "LoadedCustomCounters is unavailable or not enumerable.");
            yield break;
        }
        int index = 0;
        foreach (object counter in counters)
        {
            if (counter == null) { index++; continue; }
            string name = ModUiAuditString(GetMember(counter, "Name"), GetMember(counter, "CounterName"), "Custom counter " + index);
            object bsml = GetMember(counter, "BSML");
            string resource = ModUiAuditString(GetMember(bsml, "Resource"), GetMember(bsml, "ResourceName"), GetMember(counter, "Resource"));
            Type hostType = GetMember(bsml, "HostType") as Type ?? GetMember(counter, "HostType") as Type;
            if (hostType == null) hostType = GetMember(counter, "SettingsHostType") as Type;
            Assembly assembly = hostType?.Assembly ?? (GetMember(counter, "CounterType") as Type)?.Assembly;
            string hostName = hostType?.FullName ?? ModUiAuditTypeName(GetMember(bsml, "HostType"));
            Dictionary<string, object> registration = new()
            {
                ["kind"] = "Counters+",
                ["index"] = index,
                ["name"] = name,
                ["host"] = hostName,
                ["resource"] = resource,
                ["assembly"] = assembly?.GetName().Name,
                ["status"] = "blocked"
            };
            run.Menus.Add(registration);

            object menu = GetMember(counter, "SettingsMenu") as object;
            if (menu == null && hostType != null && assembly != null && !string.IsNullOrEmpty(resource))
            {
                try
                {
                    object host = Activator.CreateInstance(hostType, true);
                    Type menuType = FindTrainingType("BeatSaberMarkupLanguage.Settings.SettingsMenu");
                    if (menuType != null)
                        menu = Activator.CreateInstance(menuType, new object[] { name, resource, host, assembly });
                }
                catch (Exception error) { registration["reason"] = ModUiAuditException(error); }
            }
            if (menu == null)
            {
                registration["reason"] = registration.ContainsKey("reason") ? registration["reason"] :
                    "Counter did not expose a SettingsMenu and its BSML descriptor is incomplete.";
                run.Block("Counters+", name, ModUiAuditString(registration["reason"]));
            }
            else
            {
                string key = hostName + "|" + resource + "|" + name;
                if (run.MenuKeys.Add(key))
                {
                    registration["status"] = "registered";
                    yield return ModUiAuditSettingsMenu(run, flow, openMenu, menu, index,
                        name, string.IsNullOrEmpty(hostName) ? "Counters+" : hostName.Split('.')[0], resource, hostName);
                }
                else
                {
                    registration["status"] = "duplicate-of-BSMLSettings";
                }
            }
            index++;
        }
    }

    static string ModUiAuditReadResource(Component component)
    {
        if (component == null) return string.Empty;
        foreach (string name in new[] { "Resource", "ResourcePath", "ResourceName", "BSMLResource", "_resource", "_resourcePath" })
        {
            object value = ReadMemberAny(component, name);
            if (value != null && !string.IsNullOrEmpty(Convert.ToString(value, CultureInfo.InvariantCulture)))
                return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        return string.Empty;
    }

    static bool ModUiAuditIsUiType(Type type)
    {
        if (type == null || type.IsNestedPrivate ||
            type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false)) return false;
        string name = type.FullName ?? type.Name;
        return name.IndexOf("ViewController", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("FlowCoordinator", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Settings", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Panel", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Modal", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static object ModUiAuditFindActiveMainFlow()
    {
        Type type = FindTrainingType("MainFlowCoordinator");
        if (type == null) return null;
        try
        {
            return Resources.FindObjectsOfTypeAll(type).OfType<Component>()
                .FirstOrDefault(value => value != null && value.gameObject.scene.IsValid() &&
                    value.gameObject.activeInHierarchy && ModUiAuditFlowActive(value));
        }
        catch { return null; }
    }

    static bool ModUiAuditFlowActive(object flow)
    {
        if (flow == null) return false;
        try
        {
            Component component = flow as Component;
            if (component == null || !component.gameObject.activeInHierarchy) return false;
            object activated = ReadMemberAny(flow, "isActivated", "IsActivated");
            return activated == null || Convert.ToBoolean(activated, CultureInfo.InvariantCulture);
        }
        catch { return false; }
    }

    static object ModUiAuditYoungestFlow(object flow)
    {
        if (flow == null) return null;
        try
        {
            MethodInfo method = flow.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(value => (value.Name == "YoungestChildFlowCoordinatorOrSelf" ||
                    value.Name == "youngestChildFlowCoordinatorOrSelf") && value.GetParameters().Length == 0);
            object result = method?.Invoke(flow, null);
            if (result != null) return result;
        }
        catch { }
        object child = ReadMemberAny(flow, "youngestChildFlowCoordinatorOrSelf", "YoungestChildFlowCoordinatorOrSelf");
        if (child != null) return child;
        object current = flow;
        HashSet<int> seen = new();
        while (current != null && seen.Add((current as UnityEngine.Object)?.GetInstanceID() ?? current.GetHashCode()))
        {
            object next = ReadMemberAny(current, "childFlowCoordinator", "ChildFlowCoordinator");
            if (next == null) break;
            current = next;
        }
        return current;
    }

    static object ModUiAuditParentFlow(object flow, object fallback)
    {
        object parent = ReadMemberAny(flow, "parentFlowCoordinator", "ParentFlowCoordinator");
        return parent ?? fallback;
    }

    static string ModUiAuditFlowType(object flow)
    {
        return flow == null ? string.Empty : flow.GetType().FullName ?? flow.GetType().Name;
    }

    static List<object> ModUiAuditFlowChain(object main)
    {
        List<object> result = new();
        if (main == null) return result;
        object current = main;
        HashSet<int> seen = new();
        while (current != null && seen.Add((current as UnityEngine.Object)?.GetInstanceID() ?? current.GetHashCode()))
        {
            Component component = current as Component;
            result.Add(new
            {
                type = ModUiAuditFlowType(current),
                active = ModUiAuditFlowActive(current),
                path = ModUiAuditPath(component?.transform)
            });
            object child = ReadMemberAny(current, "childFlowCoordinator", "ChildFlowCoordinator");
            if (child == null) break;
            current = child;
        }
        object youngest = ModUiAuditYoungestFlow(main);
        if (youngest != null && !result.Any(value => ModUiAuditString(GetMember(value, "type")) == ModUiAuditFlowType(youngest)))
        {
            Component component = youngest as Component;
            result.Add(new
            {
                type = ModUiAuditFlowType(youngest),
                active = ModUiAuditFlowActive(youngest),
                path = ModUiAuditPath(component?.transform)
            });
        }
        return result;
    }

    static string ModUiAuditNavigationActionOwner(object button)
    {
        foreach (string member in new[] { "OnClick", "Action", "Clicked", "ClickAction" })
        {
            Delegate action = ReadMemberAny(button, member) as Delegate;
            if (action?.Method?.DeclaringType?.Assembly != null)
                return action.Method.DeclaringType.Assembly.GetName().Name;
        }
        return string.Empty;
    }

    static string ModUiAuditNavigationKey(string text, string hint, string actionOwner)
    {
        // These registered actions open navigation flows. Their descriptions may
        // legitimately mention playlists or downloads, without starting one.
        string normalized = (text ?? "").Trim().ToLowerInvariant();
        if (new[] { "beat sage", "better song search", "camera2", "counters+", "avatars", "custom sabers", "hit score visualizer", "more playlists", "configure note motion control", "configure density reducer", "configure activity energy" }.Contains(normalized))
            return normalized;
        string value = (text + " " + hint + " " + actionOwner).ToLowerInvariant();
        string[] forbidden = { "refresh", "download", "delete", "remove", "upload", "clear", "calibrate", "save", "preset", "quit", "exit", "external", "http", "play", "practice", "submit" };
        if (forbidden.Any(value.Contains)) return string.Empty;
        var allowed = new[]
        {
            new[] { "beat sage", "beatsage" },
            new[] { "better song search", "bettersongsearch" },
            new[] { "camera2", "camera 2" },
            new[] { "counters+", "countersplus", "counter" },
            new[] { "avatar", "custom avatar" },
            new[] { "custom saber", "customsaber" },
            new[] { "hit score", "hitscorevisualizer" },
            new[] { "more playlist", "moreplaylists" },
            new[] { "playlist manager", "playlistmanager" }
        };
        foreach (string[] aliases in allowed)
            if (aliases.Any(value.Contains)) return aliases[0];
        return string.Empty;
    }

    static bool ModUiAuditButtonTextMatches(string requested, string actual)
    {
        if (string.IsNullOrWhiteSpace(requested) || string.IsNullOrWhiteSpace(actual)) return false;
        if (string.Equals(requested.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        string[] words = requested.Split(new[] { ' ', '\t', '\r', '\n', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(value => value.Length >= 4).ToArray();
        return words.Length > 0 && words.All(value => actual.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    static bool ModUiAuditTryInvokeLiveNavigationButton(string label, out string detail)
    {
        detail = string.Empty;
        Type viewType = FindTrainingType("BeatSaberMarkupLanguage.MenuButtons.MenuButtonsViewController");
        if (viewType == null) { detail = "MenuButtonsViewController type is unavailable"; return false; }
        try
        {
            foreach (Component view in Resources.FindObjectsOfTypeAll(viewType).OfType<Component>())
            {
                if (view == null || !view.gameObject.activeInHierarchy) continue;
                foreach (Button button in view.GetComponentsInChildren<Button>(true))
                {
                    if (button == null || !button.gameObject.activeInHierarchy || !button.interactable) continue;
                    string actual = ModUiAuditTextFor(button);
                    if (!ModUiAuditButtonTextMatches(label, actual)) continue;
                    button.onClick.Invoke();
                    detail = "UnityEngine.UI.Button.onClick invoked; path=" + ModUiAuditPath(button.transform);
                    return true;
                }
            }
        }
        catch (Exception error) { detail = ModUiAuditException(error); return false; }
        detail = "No active MenuButtonsViewController Button matched the registered label";
        return false;
    }

    static bool ModUiAuditInvokeMenuButton(object button, string label, out string detail)
    {
        detail = string.Empty;
        if (ModUiAuditTryInvokeLiveNavigationButton(label, out detail)) return true;
        try
        {
            object interactable = ReadMemberAny(button, "Interactable", "interactable");
            if (interactable is bool enabled && !enabled)
            {
                detail = "registered MenuButton is not interactable";
                return false;
            }
            MethodInfo clicked = button?.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == "ButtonClicked" && method.GetParameters().Length == 0);
            if (clicked != null)
            {
                clicked.Invoke(button, null);
                detail = "MenuButton.ButtonClicked invoked after native Button lookup";
                return true;
            }
            Delegate action = ReadMemberAny(button, "OnClick", "Action", "Clicked", "ClickAction") as Delegate;
            if (action != null)
            {
                action.DynamicInvoke(null);
                detail = "registered MenuButton delegate invoked after native Button lookup";
                return true;
            }
        }
        catch (Exception error) { detail = ModUiAuditException(error); }
        return false;
    }

    static MethodInfo ModUiAuditFindDismiss(object parent, object child)
    {
        if (parent == null || child == null) return null;
        return parent.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.Name == "DismissFlowCoordinator")
            .FirstOrDefault(method =>
            {
                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length > 0 && parameters[0].ParameterType.IsInstanceOfType(child);
            });
    }

    static object ModUiAuditDismissArgument(ParameterInfo parameter)
    {
        if (parameter.HasDefaultValue) return parameter.DefaultValue;
        Type type = parameter.ParameterType;
        if (!type.IsValueType || typeof(Delegate).IsAssignableFrom(type)) return null;
        if (type == typeof(bool)) return parameter.Name != null && parameter.Name.IndexOf("immediately", StringComparison.OrdinalIgnoreCase) >= 0;
        if (type.IsEnum) return Enum.GetValues(type).GetValue(0);
        return Activator.CreateInstance(type);
    }

    static bool ModUiAuditDismissFlow(object main, object child, out string detail)
    {
        detail = string.Empty;
        if (child == null || ReferenceEquals(child, main))
        {
            detail = "main flow has no child to dismiss";
            return true;
        }
        if (child.GetType().FullName == "BeatSaberMarkupLanguage.Settings.ModSettingsFlowCoordinator")
        {
            try
            {
                // BSML's Cancel restores MainMenuViewController through its own
                // flow extension; raw HMUI dismissal leaves that view inactive.
                AccessTools.Method(child.GetType(), "Cancel").Invoke(child, null);
                detail = "ModSettingsFlowCoordinator.Cancel (native settings navigation)";
                return true;
            }
            catch (Exception error) { detail = "Native settings cancellation failed: " + ModUiAuditException(error); return false; }
        }
        MethodInfo back = child.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(value => value.Name == "BackButtonWasPressed" && value.DeclaringType != typeof(HMUI.FlowCoordinator) && value.GetParameters().Length == 1);
        if (back != null)
        {
            try
            {
                back.Invoke(child, new[] { ReadMemberAny(child, "topViewController", "providedMainViewController", "_providedMainViewController", "_mainViewController") });
                detail = child.GetType().FullName + ".BackButtonWasPressed (native cleanup)";
                return true;
            }
            catch (Exception error) { detail = "Native back navigation failed: " + ModUiAuditException(error); return false; }
        }
        object parent = ModUiAuditParentFlow(child, main);
        MethodInfo method = ModUiAuditFindDismiss(parent, child) ?? ModUiAuditFindDismiss(main, child);
        if (method == null)
        {
            detail = "FlowCoordinator.DismissFlowCoordinator(child, completion, direction, immediately) was not found";
            return false;
        }
        ParameterInfo[] parameters = method.GetParameters();
        object[] arguments = new object[parameters.Length];
        arguments[0] = child;
        for (int index = 1; index < parameters.Length; index++) arguments[index] = ModUiAuditDismissArgument(parameters[index]);
        try
        {
            method.Invoke(parent, arguments);
            detail = parent.GetType().FullName + "." + method;
            return true;
        }
        catch (Exception error) { detail = "DismissFlowCoordinator threw: " + ModUiAuditException(error); return false; }
    }

    IEnumerator ModUiAuditFlowPage(ModUiAuditRun run, string label, string scope, object main, object flow)
    {
        Dictionary<string, object> page = new()
        {
            ["kind"] = "MainMenuFlow",
            ["title"] = label,
            ["scope"] = scope,
            ["host"] = ModUiAuditFlowType(flow),
            ["status"] = "blocked",
            ["flowChain"] = ModUiAuditFlowChain(main)
        };
        run.Pages.Add(page);
        var ownedViews = Resources.FindObjectsOfTypeAll<HMUI.ViewController>()
            .Where(value => value != null && value.gameObject.scene.IsValid() && value.gameObject.activeInHierarchy &&
                value.GetType().Assembly.GetName().Name == scope).ToArray();
        Component root = (scope == "Camera2" ? ownedViews.FirstOrDefault(value => value.GetType().Name == "CamList") : null) ??
            ownedViews.OrderByDescending(value => value.GetComponentsInChildren<TMP_Text>(false).Length).FirstOrDefault() ?? flow as Component;
        if (root == null)
        {
            page["reason"] = "Active flow is not a Unity Component.";
            run.Block(scope, label, (string)page["reason"]);
            yield break;
        }
        yield return ModUiAuditWaitLayout(root, 4, 4f);
        Camera camera = FindTrainingMenuCamera();
        page["camera"] = camera?.name;
        page["active"] = ModUiAuditFlowActive(flow);
        if (!ModUiAuditFlowActive(flow))
        {
            page["reason"] = "Flow was not active after native MenuButton navigation.";
            run.Block(scope, label, (string)page["reason"]);
            yield break;
        }
        page["status"] = "verified";
        run.StablePages++;
        ModUiAuditCheckVisibility(run, page, root, camera);
        ModUiAuditCapturePage(run, page, root, camera, "main-" + ModUiAuditSafeSlug(label));
        ModUiAuditControls(run, page, root);
        if (scope == "Camera2") yield break; // The deep adapter first selects an existing camera before editing its pages.
        if (scope == "BeatSage" || scope == "BetterSongSearch") ModUiAuditKeyboardValues(root, scope);
        yield return ModUiAuditTabs(run, page, root, camera, "main-" + ModUiAuditSafeSlug(label));
        yield return ModUiAuditScroll(run, page, root, camera, "main-" + ModUiAuditSafeSlug(label));
        yield return ModUiAuditRealHoverHints(run, page, root, camera, scope);
        yield return ModUiAuditModals(run, page, root, camera, "main-" + ModUiAuditSafeSlug(label), scope);
        foreach (var owned in ownedViews.Where(value => value != root))
        {
            ModUiAuditCapturePage(run, page, owned, camera, "main-" + ModUiAuditSafeSlug(label) + "-" + ModUiAuditSafeSlug(owned.name));
            yield return ModUiAuditTabs(run, page, owned, camera, "main-" + ModUiAuditSafeSlug(label) + "-" + ModUiAuditSafeSlug(owned.name));
            yield return ModUiAuditScroll(run, page, owned, camera, "main-" + ModUiAuditSafeSlug(label) + "-" + ModUiAuditSafeSlug(owned.name));
            yield return ModUiAuditRealHoverHints(run, page, owned, camera, scope);
            yield return ModUiAuditModals(run, page, owned, camera, "main-" + ModUiAuditSafeSlug(label) + "-" + ModUiAuditSafeSlug(owned.name), scope);
        }
        yield return ModUiAuditSafeLazyActions(run, root, camera, scope, label);
        if (scope == "BeatSage")
        {
            object controller = ModUiAuditFindActiveController("BeatSage.UI.BeatSageViewController");
            foreach (string action in new[] { "ShowLocalTab", "ShowQueueTab", "ShowSettingsTab", "ShowGenerationSettings", "ShowApplicationSettings", "ShowPresetSettings", "OpenModelChoices", "OpenEnvironmentChoices", "OpenPresetChoices", "ShowSongEditor", "ShowRetry" })
            {
                if (!ModUiAuditInvokeSafeLazyAction(controller, action, out string detail))
                { run.Block(scope, action, detail); continue; }
                yield return ModUiAuditWaitLayout(root, 3, 2f);
                Dictionary<string, object> tab = new()
                { ["kind"] = "NativeNavigation", ["title"] = action, ["scope"] = scope, ["status"] = "verified" };
                run.Pages.Add(tab);
                ModUiAuditCapturePage(run, tab, root, camera, "beat-sage-" + action);
                yield return ModUiAuditScroll(run, tab, root, camera, "beat-sage-" + action);
                yield return ModUiAuditRealHoverHints(run, tab, root, camera, scope);
                yield return ModUiAuditModals(run, tab, root, camera, "beat-sage-" + action, scope);
                yield return ModUiAuditCaptureActiveModalsAfterAction(run, root, camera, scope, action);
            }
        }
    }

    void ModUiAuditKeyboardValues(Component root, string scope)
    {
        Type modalType = FindTrainingType("BeatSaberMarkupLanguage.Components.ModalKeyboard");
        if (modalType == null || root == null) return;
        foreach (Component modal in root.GetComponentsInChildren(modalType, true))
        {
            object keyboard = GetMember(modal, "Keyboard");
            if (!(GetMember(keyboard, "Keys") is IEnumerable keys)) continue;
            int checkedKeys = 0;
            foreach (object key in keys)
            {
                string name = GetMember(key, "Name") as string;
                if (name != "CAPS" && name != "SHIFT" && name != "TAB") continue;
                object value = GetMember(key, "Value"), shifted = GetMember(key, "Shifted"), action = GetMember(key, "KeyAction");
                key.GetType().GetMethod("UpdateText")?.Invoke(key, null);
                checkedKeys++;
                Check("all-mod-ui-keyboard-value-" + scope + "-" + name,
                    Equals(value, GetMember(key, "Value")) && Equals(shifted, GetMember(key, "Shifted")) && ReferenceEquals(action, GetMember(key, "KeyAction")),
                    "Localized key labels preserve native characters, shifted values and actions");
                TMP_Text label = GetMember(key, "buttonText") as TMP_Text;
                string expected = name == "CAPS" ? "大写" : name == "SHIFT" ? "上档" : "制表";
                Check("all-mod-ui-keyboard-caption-" + scope + "-" + name, label != null && label.text == expected, "Native keyboard key caption");
            }
            Check("all-mod-ui-keyboard-keys-" + scope, checkedKeys == 3, "CAPS, SHIFT and TAB key models were inspected");
            return;
        }
    }

    static object ModUiAuditFindActiveController(string typeName)
    {
        Type type = FindTrainingType(typeName);
        if (type == null) return null;
        try
        {
            return Resources.FindObjectsOfTypeAll(type).FirstOrDefault(value =>
            {
                Component component = value as Component;
                return component != null && component.gameObject.scene.IsValid() && component.gameObject.activeInHierarchy;
            });
        }
        catch { return null; }
    }

    static bool ModUiAuditInvokeSafeLazyAction(object controller, string action, out string detail)
    {
        detail = string.Empty;
        if (controller == null) { detail = "controller is unavailable"; return false; }
        MethodInfo method = controller.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(value => value.Name == action && value.GetParameters().Length == 0);
        if (method == null) { detail = controller.GetType().FullName + "." + action + "() is unavailable"; return false; }
        try
        {
            method.Invoke(controller, null);
            detail = controller.GetType().FullName + "." + action + "() invoked";
            return true;
        }
        catch (Exception error) { detail = ModUiAuditException(error); return false; }
    }

    IEnumerator ModUiAuditCaptureActiveModalsAfterAction(ModUiAuditRun run, Component root,
        Camera camera, string scope, string label)
    {
        Type modalType = FindTrainingType("HMUI.ModalView");
        if (modalType == null) yield break;
        Component[] active;
        try
        {
            active = Resources.FindObjectsOfTypeAll(modalType).OfType<Component>()
                .Where(value => value != null && value.gameObject.scene.IsValid() && value.gameObject.activeInHierarchy)
                .Distinct().ToArray();
        }
        catch { yield break; }
        int index = 0;
        foreach (Component modal in active)
        {
            Dictionary<string, object> record = new()
            {
                ["kind"] = "LazyModalAction",
                ["action"] = label,
                ["path"] = ModUiAuditPath(modal.transform),
                ["scope"] = scope,
                ["status"] = "verified"
            };
            run.Modals.Add(record);
            Dictionary<string, object> page = new()
            {
                ["kind"] = "LazyModalPage",
                ["title"] = label,
                ["scope"] = scope,
                ["host"] = modal.GetType().FullName,
                ["status"] = "verified"
            };
            run.Pages.Add(page);
            if (root != null)
            {
                ModUiAuditCapturePage(run, page, root, camera,
                    "lazy-modal-" + ModUiAuditSafeSlug(label) + "-" + index);
                ModUiAuditCaptureHints(run, page, root, scope);
            }
            else ModUiAuditCaptureTranslator(run, "lazy-modal-" + ModUiAuditSafeSlug(label) + "-" + index);
            if (!ModUiAuditInvokeModal(modal, "Hide", out string hideSignature, out string hideError))
            {
                record["status"] = "partial";
                record["hideSignature"] = hideSignature;
                record["hideReason"] = hideError;
                run.Block(scope, label, "Lazy modal Hide failed: " + hideError);
            }
            else record["hideSignature"] = hideSignature;
            index++;
            yield return null;
        }
    }

    IEnumerator ModUiAuditSafeLazyActions(ModUiAuditRun run, Component root, Camera camera,
        string scope, string flowLabel)
    {
        string flowType = root?.GetType().Assembly.GetName().Name ?? scope;
        if (flowType.IndexOf("BetterSongSearch", StringComparison.OrdinalIgnoreCase) < 0 &&
            scope.IndexOf("BetterSongSearch", StringComparison.OrdinalIgnoreCase) < 0) yield break;
        var actions = new[]
        {
            new { Type = "BetterSongSearch.UI.SongListController", Action = "ShowSettings" },
            new { Type = "BetterSongSearch.UI.SongListController", Action = "ShowMultiDlModal" },
            new { Type = "BetterSongSearch.UI.SongListController", Action = "ShowPlaylistCreation" },
            new { Type = "BetterSongSearch.UI.FilterView", Action = "ShowPresets" },
            new { Type = "BetterSongSearch.UI.FilterView", Action = "ShowGenrePicker" }
        };
        foreach (var candidate in actions)
        {
            object controller = ModUiAuditFindActiveController(candidate.Type);
            if (controller == null) continue;
            bool invoked = ModUiAuditInvokeSafeLazyAction(controller, candidate.Action, out string detail);
            Dictionary<string, object> entry = new()
            {
                ["kind"] = "BSML.SafeLazyAction",
                ["flow"] = flowLabel,
                ["host"] = candidate.Type,
                ["action"] = candidate.Action,
                ["status"] = invoked ? "invoked" : "blocked",
                ["detail"] = detail
            };
            run.Entrances.Add(entry);
            if (!invoked)
            {
                run.Block(scope, candidate.Action, detail);
                continue;
            }
            yield return ModUiAuditWaitLayout(root, 3, 2f);
            yield return ModUiAuditCaptureActiveModalsAfterAction(run, root, camera, scope, candidate.Action);
        }
        object filters = ModUiAuditFindActiveController("BetterSongSearch.UI.FilterView");
        foreach (string action in new[] { "ToggleStars", "ToggleMapping", "ToggleRating", "ToggleUploaders", "ToggleGenre", "ToggleMods" })
        {
            if (!ModUiAuditInvokeSafeLazyAction(filters, action, out string detail))
            { run.Block(scope, action, detail); continue; }
            Component view = filters as Component;
            yield return ModUiAuditWaitLayout(view, 3, 1.5f);
            Dictionary<string, object> group = new()
            { ["kind"] = "NativeExpansion", ["title"] = action, ["scope"] = scope, ["status"] = "verified" };
            run.Pages.Add(group);
            ModUiAuditCapturePage(run, group, view, camera, "search-" + action);
            yield return ModUiAuditScroll(run, group, view, camera, "search-" + action);
            yield return ModUiAuditRealHoverHints(run, group, view, camera, scope);
            yield return ModUiAuditModals(run, group, view, camera, "search-" + action, scope);
            if (!ModUiAuditInvokeSafeLazyAction(filters, action, out detail)) run.Block(scope, action, detail);
        }
    }

    IEnumerator ModUiAuditMainMenuNavigation(ModUiAuditRun run)
    {
        object nativeMain = GetStaticMember(FindTrainingType("BeatSaberMarkupLanguage.BeatSaberUI"), "MainFlowCoordinator");
        yield return ModUiAuditWait(run, () => nativeMain != null && ReadBool(nativeMain, "isActivated", false) && !ReadBool(nativeMain, "isInTransition", true), 8, 15f);
        // Some mods register from their first active-menu Update tick.
        yield return new WaitForSecondsRealtime(1f);
        Type menuButtonsType = FindTrainingType("BeatSaberMarkupLanguage.MenuButtons.MenuButtons");
        object menuButtons = GetStaticMember(menuButtonsType, "Instance");
        if (menuButtons == null && menuButtonsType != null)
        {
            try { menuButtons = Resources.FindObjectsOfTypeAll(menuButtonsType).FirstOrDefault(); }
            catch { }
        }
        object values = ReadMemberAny(menuButtons, "Buttons");
        if (!(values is IEnumerable buttons))
        {
            run.Block("BSML.MenuButtons", "Buttons", "MenuButtons registry is unavailable before main-menu navigation.");
            yield break;
        }
        List<object> candidates = new();
        try
        {
            foreach (object button in buttons)
            {
                string label = ModUiAuditString(GetMember(button, "Text"), GetMember(button, "TextKey"), GetMember(button, "Name"));
                string hint = ModUiAuditString(GetMember(button, "HintText"), GetMember(button, "Description"));
                string owner = ModUiAuditNavigationActionOwner(button);
                string key = ModUiAuditNavigationKey(label, hint, owner);
                if (string.IsNullOrEmpty(key)) continue;
                string focus = Environment.GetEnvironmentVariable("EFFECT_SMOKE_UI_FOCUS") ?? "";
                if (focus.StartsWith("Main:", StringComparison.Ordinal) && !String.Equals(focus.Substring(5), owner, StringComparison.Ordinal)) continue;
                candidates.Add(new { Button = button, Label = label, Hint = hint, Owner = owner, Key = key });
            }
        }
        catch (Exception error)
        {
            run.Block("BSML.MenuButtons", "Buttons", "Navigation candidate scan failed: " + ModUiAuditException(error));
            yield break;
        }

        if (candidates.Count == 0)
        {
            run.Block("BSML.MenuButtons", Environment.GetEnvironmentVariable("EFFECT_SMOKE_UI_FOCUS") ?? "main-menu", "No matching installed navigation entry was registered after the native menu became active.");
            yield break;
        }
        foreach (object candidate in candidates)
        {
            object button = GetMember(candidate, "Button");
            string label = ModUiAuditString(GetMember(candidate, "Label"), "main-menu-entry");
            string key = ModUiAuditString(GetMember(candidate, "Key"));
            object main = ModUiAuditFindActiveMainFlow();
            if (main == null)
            {
                run.Block(key, label, "MainFlowCoordinator is not active before the whitelisted navigation click.");
                continue;
            }
            object childBefore = ModUiAuditYoungestFlow(main);
            if (childBefore != null && !ReferenceEquals(childBefore, main))
            {
                if (!ModUiAuditDismissFlow(main, childBefore, out string dismissDetail))
                {
                    run.Block(key, label, "Could not return to MainFlowCoordinator: " + dismissDetail);
                    continue;
                }
                yield return ModUiAuditWait(run, () => ReferenceEquals(ModUiAuditYoungestFlow(main), main) || !ModUiAuditFlowActive(childBefore), 8, 15f);
            }

            yield return ModUiAuditWait(run, () => ReadBool(main, "isActivated", false) && !ReadBool(main, "isInTransition", true), 8, 10f);
            bool invoked = ModUiAuditInvokeMenuButton(button, label, out string invokeDetail);
            Dictionary<string, object> entrance = new()
            {
                ["kind"] = "BSML.MenuButtons.Navigation",
                ["label"] = label,
                ["scope"] = key,
                ["host"] = GetMember(candidate, "Owner"),
                ["status"] = invoked ? "clicked" : "blocked",
                ["detail"] = invokeDetail
            };
            run.Entrances.Add(entrance);
            if (!invoked)
            {
                run.Block(key, label, "Whitelisted native navigation click failed: " + invokeDetail);
                continue;
            }
            yield return ModUiAuditWait(run, () =>
            {
                object current = ModUiAuditYoungestFlow(main);
                return current != null && !ReferenceEquals(current, main) && ModUiAuditFlowActive(current);
            }, 8, 20f);
            object flow = ModUiAuditYoungestFlow(main);
            if (flow == null || ReferenceEquals(flow, main))
            {
                entrance["status"] = "blocked-no-child-flow";
                run.Block(key, label, "MenuButton click completed without an active child FlowCoordinator.");
                continue;
            }
            string scope = GetMember(candidate, "Owner") as string;
            if (string.IsNullOrEmpty(scope)) scope = flow.GetType().Assembly.GetName().Name;
            yield return ModUiAuditWait(run, () => !ReadBool(flow, "isInTransition", false), 4, 10f);
            yield return new WaitForSecondsRealtime(1f);
            yield return ModUiAuditFlowPage(run, label, scope, main, flow);
            if (scope == "CustomAvatar") yield return ModUiAuditConditionalAvatarFlow(run, flow, FindTrainingMenuCamera());
            if (scope == "BetterSongSearch") yield return ModUiAuditBssQueue(run, flow, FindTrainingMenuCamera());
            if (scope == "Counters+") yield return ModUiAuditBuiltInCounters(run, flow, FindTrainingMenuCamera());
            if (scope == "MorePlaylists") yield return ModUiAuditMorePlaylists(run, flow, FindTrainingMenuCamera());
            if (scope == "Camera2" || scope == "HitScoreVisualizer")
                yield return ModUiAuditCameraAndScoreSettings(run, scope, FindTrainingMenuCamera());
            if (scope == "CustomSabersLite") yield return VerifyModUiImportedSaberNames();
            if (scope == "CustomSabersLite" || Environment.GetEnvironmentVariable("EFFECT_SMOKE_UI_VERIFY_RELOAD") == "1")
            {
                yield return VerifyModUiReloadChecks();
                yield return ModUiAuditWaitLayout(flow as Component, 4, 3f);
                ModUiAuditCaptureTranslator(run, "after-language-reload-" + scope);
            }
            if ((Environment.GetEnvironmentVariable("EFFECT_SMOKE_UI_FOCUS") ?? "").StartsWith("Main:", StringComparison.Ordinal))
            {
                entrance["status"] = "verified";
                continue;
            }
            if (!ModUiAuditDismissFlow(main, flow, out string detail))
            {
                entrance["dismiss"] = "blocked";
                entrance["dismissDetail"] = detail;
                run.Block(key, label, "Child flow could not be dismissed: " + detail);
                continue;
            }
            entrance["dismiss"] = "requested";
            entrance["dismissDetail"] = detail;
            yield return ModUiAuditWait(run, () => !ModUiAuditFlowActive(flow) || ReferenceEquals(ModUiAuditYoungestFlow(main), main), 8, 20f);
            if (ModUiAuditFlowActive(flow))
                run.Block(key, label, "DismissFlowCoordinator returned but child flow remained active.");
        }
    }

    static object ModUiAuditFindGameplaySetup()
    {
        Type type = FindTrainingType("BeatSaberMarkupLanguage.GameplaySetup.GameplaySetup");
        if (type == null) return null;
        object instance = GetStaticMember(type, "instance") ?? GetStaticMember(type, "Instance");
        if (instance != null) return instance;
        try
        {
            return Resources.FindObjectsOfTypeAll(type).FirstOrDefault();
        }
        catch { return null; }
    }

    static Component ModUiAuditFindGameplayRoot(object flow)
    {
        Type viewType = FindTrainingType("GameplaySetupViewController");
        if (viewType != null)
        {
            try
            {
                Component active = Resources.FindObjectsOfTypeAll(viewType).OfType<Component>()
                    .FirstOrDefault(value => value != null && value.gameObject.activeInHierarchy);
                if (active != null) return active;
            }
            catch { }
        }
        return flow as Component;
    }

    IEnumerator ModUiAuditGameplaySetup(ModUiAuditRun run)
    {
        object main = ModUiAuditFindActiveMainFlow();
        if (main == null)
        {
            run.Block("BSML.GameplaySetup", "SoloFreePlay", "MainFlowCoordinator is unavailable before SoloFreePlay navigation.");
            yield break;
        }
        object existing = ModUiAuditYoungestFlow(main);
        if (existing != null && !ReferenceEquals(existing, main))
        {
            if (!ModUiAuditDismissFlow(main, existing, out string dismissDetail))
            {
                run.Block("BSML.GameplaySetup", "SoloFreePlay", "Could not dismiss the preceding flow: " + dismissDetail);
                yield break;
            }
            yield return ModUiAuditWait(run, () => !ModUiAuditFlowActive(existing) || ReferenceEquals(ModUiAuditYoungestFlow(main), main), 8, 20f);
        }

        MainMenuViewController menu = Resources.FindObjectsOfTypeAll<MainMenuViewController>()
            .FirstOrDefault(value => value != null && value.gameObject.activeInHierarchy && value.isActivated);
        if (menu == null)
        {
            run.Block("BSML.GameplaySetup", "SoloFreePlay", "MainMenuViewController was not active for native SoloFreePlay entry.");
            yield break;
        }
        SoloFreePlayFlowCoordinator solo = Resources.FindObjectsOfTypeAll<SoloFreePlayFlowCoordinator>().FirstOrDefault();
        Button soloButton = null;
        try { soloButton = AccessTools.Field(typeof(MainMenuViewController), "_soloButton").GetValue(menu) as Button; }
        catch { }
        string requested = targetId;
        bool configured = false;
        if (!string.IsNullOrEmpty(requested) && solo != null)
        {
            try
            {
                BeatmapLevel level;
                bool custom = requested.StartsWith("custom_level_", StringComparison.Ordinal);
                BeatmapLevelsModel levels = Resources.FindObjectsOfTypeAll<Zenject.SceneContext>()
                    .FirstOrDefault(value => value != null && value.gameObject.scene.name == "MainMenu")?.Container.Resolve<BeatmapLevelsModel>();
                level = custom ? SongCore.Loader.GetLevelById(requested) : levels?.GetBeatmapLevel(requested, false);
                var levelPack = custom ? SongCore.Loader.CustomLevelsPack : levels?.GetLevelPackForLevelId(requested);
                if (level != null && levelPack != null)
                {
                    BeatmapKey key = level.GetBeatmapKeys()
                        .Where(value => string.IsNullOrEmpty(characteristic) || SongCore.Collections.GetCurrentCharacteristicSerializedName(value.characteristic) == characteristic)
                        .Where(value => string.IsNullOrEmpty(difficulty) || value.difficulty.ToString() == difficulty)
                        .OrderBy(value => SongCore.Collections.GetCurrentCharacteristicSerializedName(value.characteristic) == "Standard" ? 0 : 1)
                        .ThenBy(value => value.difficulty)
                        .First();
                    solo.Setup(new LevelSelectionFlowCoordinator.State(SelectLevelCategoryViewController.LevelCategory.All, levelPack, in key, level));
                    configured = true;
                }
            }
            catch (Exception error) { run.Block("BSML.GameplaySetup", "SoloFreePlay.Setup", ModUiAuditException(error)); }
        }
        if (soloButton == null || !soloButton.interactable)
        {
            run.Block("BSML.GameplaySetup", "SoloFreePlay", "Native _soloButton was unavailable or not interactable.");
            yield break;
        }
        try { soloButton.onClick.Invoke(); }
        catch (Exception error)
        {
            run.Block("BSML.GameplaySetup", "SoloFreePlay", "Native _soloButton.onClick failed: " + ModUiAuditException(error));
            yield break;
        }
        yield return ModUiAuditWait(run, () =>
        {
            object flow = ModUiAuditYoungestFlow(main);
            return flow != null && !ReferenceEquals(flow, main) && ModUiAuditFlowActive(flow) &&
                Resources.FindObjectsOfTypeAll<LevelCollectionTableView>().Any(value => value != null && value.gameObject.activeInHierarchy);
        }, 8, 30f);
        object activeFlow = ModUiAuditYoungestFlow(main);
        Component root = ModUiAuditFindGameplayRoot(activeFlow) ?? activeFlow as Component;
        Camera camera = FindTrainingMenuCamera();
        if (root == null)
        {
            run.Block("BSML.GameplaySetup", "SoloFreePlay", "SoloFreePlay did not expose an active flow/view root after native entry.");
        }
        else
        {
            Dictionary<string, object> page = new()
            {
                ["kind"] = "SoloFreePlaySelection",
                ["title"] = "SoloFreePlay",
                ["scope"] = "BeatSaber",
                ["host"] = ModUiAuditFlowType(activeFlow),
                ["configured"] = configured,
                ["status"] = "verified"
            };
            run.Pages.Add(page);
            ModUiAuditCheckVisibility(run, page, root, camera);
            ModUiAuditCapturePage(run, page, root, camera, "solo-freeplay-selection");
            ModUiAuditControls(run, page, root);
            yield return ModUiAuditScroll(run, page, root, camera, "solo-freeplay-selection");
            yield return ModUiAuditRealHoverHints(run, page, root, camera, "BeatSaber");
            yield return ModUiAuditModals(run, page, root, camera, "solo-freeplay-selection", "BeatSaber");
            object setup = ModUiAuditFindGameplaySetup();
            if (setup == null)
                run.Block("BSML.GameplaySetup", "GameplaySetup", "GameplaySetup instance was not available on the active SoloFreePlay selection page.");
            else yield return ModUiAuditNativeGameplayTabs(run, setup, root, activeFlow, camera);
            yield return ModUiAuditSongSelectionExtras(run, camera);
        }
        activeFlow = ModUiAuditYoungestFlow(main);
        if (activeFlow != null && !ReferenceEquals(activeFlow, main))
        {
            if (!ModUiAuditDismissFlow(main, activeFlow, out string dismissDetail))
                run.Block("BSML.GameplaySetup", "SoloFreePlay", "Could not dismiss SoloFreePlay after tab audit: " + dismissDetail);
            else yield return ModUiAuditWait(run, () => !ModUiAuditFlowActive(activeFlow) || ReferenceEquals(ModUiAuditYoungestFlow(main), main), 8, 20f);
        }
    }

    void ModUiAuditDiscoverEntrances(ModUiAuditRun run)
    {
        Type pluginManager = FindTrainingType("IPA.Loader.PluginManager");
        object enabledPlugins = GetStaticMember(pluginManager, "EnabledPlugins");
        if (enabledPlugins is IEnumerable pluginValues)
        {
            foreach (object plugin in pluginValues)
            {
                object metadata = GetMember(plugin, "Metadata") ?? plugin;
                object pluginType = GetMember(plugin, "PluginType") ?? GetMember(metadata, "PluginType");
                Assembly assembly = pluginType is Type loadedType ? loadedType.Assembly : null;
                run.Plugins.Add(new
                {
                    id = ModUiAuditString(GetMember(metadata, "Id"), GetMember(plugin, "Id")),
                    name = ModUiAuditString(GetMember(metadata, "Name"), GetMember(plugin, "Name")),
                    version = ModUiAuditString(GetMember(metadata, "Version"), GetMember(plugin, "Version")),
                    assembly = assembly?.GetName().Name,
                    loaded = true
                });
            }
        }
        else
        {
            run.Block("IPA", "EnabledPlugins", "IPA.Loader.PluginManager.EnabledPlugins is unavailable; loaded assemblies are still inventoried below.");
        }
        string[] known = { "Camera2", "CustomAvatar", "CustomSabersLite", "BetterSongSearch", "PlaylistManager", "MorePlaylists", "BeatSage", "SongChartVisualizer", "AdvancedSongFilters", "Counters+", "ModUiChinese", "ParticleOverdrive", "RoomCalibration", "ActivityEnergyCounter", "DensityReducer", "NoteMotionControl", "AssetBundleLoadingTools", "SongCore", "Chroma", "Heck", "Vivify", "BetterSongList" };
        Type menuButtonsType = FindTrainingType("BeatSaberMarkupLanguage.MenuButtons.MenuButtons");
        object menuButtons = GetStaticMember(menuButtonsType, "Instance");
        if (menuButtons == null && menuButtonsType != null)
        {
            try { menuButtons = Resources.FindObjectsOfTypeAll(menuButtonsType).FirstOrDefault(); }
            catch { }
        }
        if (menuButtons != null)
        {
            object buttonsValue = GetMember(menuButtons, "Buttons");
            if (buttonsValue is IEnumerable buttons)
            {
                int index = 0;
                foreach (object button in buttons)
                {
                    string text = ModUiAuditString(GetMember(button, "Text"), GetMember(button, "TextKey"), GetMember(button, "Name"));
                    string hint = ModUiAuditString(GetMember(button, "HintText"), GetMember(button, "Description"));
                    string owner = ModUiAuditNavigationActionOwner(button);
                    bool safe = !String.IsNullOrEmpty(ModUiAuditNavigationKey(text, hint, owner));
                    run.Entrances.Add(new
                    {
                        kind = "BSML.MenuButtons.Buttons",
                        index,
                        text,
                        hint,
                        type = button?.GetType().FullName,
                        host = owner,
                        resource = string.Empty,
                        status = safe ? "navigation-registered" : "skipped-action",
                        reason = safe ? "Native registration; actual navigation is recorded separately by the focused batch." : "Action callback is not required to inspect captions."
                    });
                    index++;
                }
            }
            else run.Block("BSML.MenuButtons", "Buttons", "MenuButtons instance exists but Buttons is not enumerable.");
        }
        else run.Block("BSML.MenuButtons", "MenuButtons", "No live MenuButtons registry was available.");

        ModUiAuditDiscoverGameplaySetup(run);
        foreach (string assemblyName in known)
        {
            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(value => string.Equals(value.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
            if (assembly == null)
            {
                run.Entrances.Add(new { kind = "main-menu-registered-ui", plugin = assemblyName, status = "blocked-plugin-not-loaded", type = (string)null, host = (string)null, resource = (string)null, entry = (string)null, reason = "Assembly is not loaded in this runtime." });
                continue;
            }
            List<Component> instances = new();
            try
            {
                instances = Resources.FindObjectsOfTypeAll<Component>()
                    .Where(component => component != null && component.gameObject.scene.IsValid() && component.gameObject.activeInHierarchy &&
                        string.Equals(component.GetType().Assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase) &&
                        ModUiAuditIsUiType(component.GetType()))
                    .GroupBy(component => component.GetType().FullName + "|" + ModUiAuditPath(component.transform))
                    .Select(group => group.First()).Take(96).ToList();
            }
            catch { }
            if (instances.Count == 0)
            {
                try
                {
                    foreach (Type type in assembly.GetTypes().Where(ModUiAuditIsUiType).Take(96))
                        run.Entrances.Add(new { kind = "main-menu-registered-ui", plugin = assemblyName, status = "blocked-generic-open", type = type.FullName, host = (string)null, resource = (string)null, entry = (string)null, reason = "UI type is present but no active instance can be safely opened generically." });
                }
                catch (Exception error) { run.Block(assemblyName, "assembly-ui-types", ModUiAuditException(error)); }
                continue;
            }
            foreach (Component component in instances)
            {
                string type = component.GetType().FullName;
                string resource = ModUiAuditReadResource(component);
                run.Entrances.Add(new
                {
                    kind = "main-menu-registered-ui",
                    plugin = assemblyName,
                    status = "blocked-generic-open",
                    type,
                    host = component.GetType().FullName,
                    resource,
                    entry = ModUiAuditPath(component.transform),
                    reason = "Active native component discovered; generic opening is withheld until a plugin-specific safe adapter is selected."
                });
            }
        }
    }

    static bool ModUiAuditSafeMenuButton(string text, string hint)
    {
        string value = (text + " " + hint).ToLowerInvariant();
        string[] unsafeTerms = { "refresh", "download", "delete", "remove", "upload", "clear", "calibrate", "save", "preset", "quit", "exit", "external", "http", "play", "practice" };
        return !unsafeTerms.Any(value.Contains);
    }

    void ModUiAuditDiscoverGameplaySetup(ModUiAuditRun run)
    {
        Type setupType = FindTrainingType("BeatSaberMarkupLanguage.GameplaySetup.GameplaySetup");
        object setup = GetStaticMember(setupType, "instance") ?? GetStaticMember(setupType, "Instance");
        if (setup == null)
        {
            try { if (setupType != null) setup = Resources.FindObjectsOfTypeAll(setupType).FirstOrDefault(); }
            catch { }
        }
        if (setup == null)
        {
            run.Block("BSML.GameplaySetup", "GameplaySetup", "GameplaySetup instance is unavailable before SoloFreePlay entry.");
            return;
        }
        object menus = ReadMemberAny(setup, "menus", "_menus", "Menus");
        if (!(menus is IEnumerable values))
        {
            run.Block("BSML.GameplaySetup", "menus", "GameplaySetup private menus field is not enumerable.");
            return;
        }
        int index = 0;
        foreach (object menu in values)
        {
            string name = ModUiAuditString(GetMember(menu, "Name"), GetMember(menu, "name"));
            string resource = ModUiAuditString(GetMember(menu, "Resource"), GetMember(menu, "resource"));
            object hostObject = GetMember(menu, "Host");
            string host = hostObject is Type ? ModUiAuditTypeName(hostObject) : hostObject?.GetType().FullName;
            if (string.IsNullOrEmpty(host)) host = ModUiAuditTypeName(GetMember(menu, "HostType"));
            object tabObject = ReadMemberAny(menu, "tabObject", "TabObject");
            run.Entrances.Add(new
            {
                kind = "BSML.GameplaySetup",
                index,
                name,
                type = menu?.GetType().FullName,
                host,
                resource,
                tabObject = tabObject?.GetType().FullName,
                entry = tabObject is Component component ? ModUiAuditPath(component.transform) : string.Empty,
                status = "observed-safe-tab-not-opened",
                reason = "Gameplay setup tab is registered; generic audit records it and defers selection to the native SoloFreePlay flow."
            });
            index++;
        }
        if (index == 0) run.Block("BSML.GameplaySetup", "menus", "GameplaySetup menus field is empty.");
    }

    static string ModUiAuditException(Exception error)
    {
        Exception actual = error is TargetInvocationException invocation && invocation.InnerException != null ? invocation.InnerException : error;
        return actual.GetType().Name + ": " + actual.Message;
    }

    void ModUiAuditWrite(ModUiAuditRun run, bool final = true)
    {
        if (string.IsNullOrEmpty(run.OutputDirectory)) return;
        string snapshotPath = Path.Combine(run.OutputDirectory, "mod-ui-snapshot-all.json");
        string coveragePath = Path.Combine(run.OutputDirectory, "mod-ui-coverage.json");
        try
        {
            File.WriteAllText(snapshotPath, JsonConvert.SerializeObject(run.Snapshots, Formatting.Indented));
            var summary = new
            {
                settingsMenus = run.Menus.Count(value => ModUiAuditString(GetMember(value, "kind")) == "BSMLSettings"),
                pages = run.Pages.Count,
                stablePages = run.StablePages,
                blockedPages = run.BlockedPages,
                modals = run.Modals.Count,
                hints = run.Hints.Count(value => ModUiAuditString(GetMember(value, "kind")) == "native-hover"),
                hintContracts = run.Hints.Count(value => ModUiAuditString(GetMember(value, "kind")) == "caption-contract"),
                snapshots = run.Snapshots.Count,
                screenshots = run.ScreenshotCount,
                protectedControls = run.ProtectedControls,
                blocked = run.Blocked.Count
            };
            var coverage = new
            {
                schemaVersion = 1,
                kind = "full-mod-ui-audit",
                gameBuild = "1.45.2_28848",
                runtimeVerified = true,
                translatorCapture = run.TranslatorAvailable,
                settingsMenus = run.Menus,
                pages = run.Pages,
                modals = run.Modals,
                hoverHints = run.Hints,
                mainMenuEntrances = run.Entrances,
                plugins = run.Plugins,
                snapshots = new { path = snapshotPath, schema = "audit_mod_ui_localization.py", count = run.Snapshots.Count },
                summary,
                blocked = run.Blocked
            };
            File.WriteAllText(coveragePath, JsonConvert.SerializeObject(coverage, Formatting.Indented));
            if (final)
            {
                checks.Add(new { name = "full-mod-ui-audit", passed = run.Blocked.Count == 0, detail = coveragePath + "; blocked=" + run.Blocked.Count + "; pages=" + run.Pages.Count });
                if (run.Blocked.Count > 0 && !errors.Any(value => value.StartsWith("UI coverage incomplete:", StringComparison.Ordinal)))
                    errors.Add("UI coverage incomplete: " + run.Blocked.Count + " blocked checks; " + coveragePath);
            }
        }
        catch (Exception error)
        {
            run.Block("output", "mod-ui-coverage.json", ModUiAuditException(error));
        }
    }
}
