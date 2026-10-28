using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace EffectModsSmoke;

public sealed partial class Runner
{
    sealed class ModUiGameplaySelectorState
    {
        internal Component Selector;
        internal Component Control;
        internal List<object> VisibleTabs = new();
        internal int PageSize;
        internal int OriginalPage;
        internal int OriginalLocal;
        internal int OriginalGlobal;
        internal object OriginalTab;
    }

    static List<object> ModUiGameplayEnumerable(object value)
    {
        if (!(value is IEnumerable values)) return new List<object>();
        try { return values.Cast<object>().Where(item => item != null).ToList(); }
        catch { return new List<object>(); }
    }

    static string ModUiGameplayMenuName(object menu)
    {
        return ModUiAuditString(ReadMemberAny(menu, "Name", "name"), "Gameplay tab");
    }

    static string ModUiGameplayMenuResource(object menu)
    {
        return ModUiAuditString(ReadMemberAny(menu, "Resource", "resource"));
    }

    static string ModUiGameplayMenuHost(object menu)
    {
        object host = ReadMemberAny(menu, "Host", "host");
        if (host is Type type) return type.FullName ?? type.Name;
        return host?.GetType().FullName ?? ModUiAuditTypeName(ReadMemberAny(menu, "HostType", "hostType"));
    }

    static string ModUiGameplayMenuAssembly(object menu)
    {
        object assembly = ReadMemberAny(menu, "Assembly", "assembly");
        if (assembly is Assembly value) return value.GetName().Name;
        object host = ReadMemberAny(menu, "Host", "host");
        if (host is Type type) return type.Assembly.GetName().Name;
        return host?.GetType().Assembly.GetName().Name ?? string.Empty;
    }

    static string ModUiGameplayTargetScope(object menu)
    {
        string text = (ModUiGameplayMenuName(menu) + " " + ModUiGameplayMenuHost(menu) + " " +
            ModUiGameplayMenuAssembly(menu) + " " + ModUiGameplayMenuResource(menu)).ToLowerInvariant();
        if (text.IndexOf("camera2", StringComparison.Ordinal) >= 0) return "Camera2";
        if (text.IndexOf("chroma", StringComparison.Ordinal) >= 0) return "Chroma";
        if (text.IndexOf("customsabers", StringComparison.Ordinal) >= 0 ||
            text.IndexOf("customsaber", StringComparison.Ordinal) >= 0) return "CustomSabersLite";
        return string.Empty;
    }

    static Component ModUiGameplayFindSelectorControl(Component selector)
    {
        if (selector == null) return null;
        Component direct = ReadMemberAny(selector, "TextSegmentedControl", "textSegmentedControl") as Component;
        if (direct != null) return direct;
        Type segmentedType = FindTrainingType("HMUI.SegmentedControl");
        if (segmentedType == null) return null;
        try
        {
            return selector.GetComponentsInChildren(segmentedType, true)
                .OfType<Component>()
                .FirstOrDefault(value => value != null &&
                    ModUiGameplaySelectMethod(value.GetType()) != null);
        }
        catch { return null; }
    }

    static MethodInfo ModUiGameplaySelectMethod(Type type)
    {
        if (type == null) return null;
        try
        {
            return type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.Name == "SelectCellWithNumber")
                .FirstOrDefault(method =>
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    return parameters.Length == 1 && parameters[0].ParameterType == typeof(int);
                });
        }
        catch { return null; }
    }

    static MethodInfo ModUiGameplayPageMethod(Type type, string name)
    {
        if (type == null) return null;
        try
        {
            return type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == name && method.GetParameters().Length == 0);
        }
        catch { return null; }
    }

    static int ModUiGameplayReadInt(object value, params string[] names)
    {
        foreach (string name in names)
        {
            object member = ReadMemberAny(value, name);
            if (member == null) continue;
            try { return Convert.ToInt32(member, CultureInfo.InvariantCulture); }
            catch { }
        }
        return -1;
    }

    static bool ModUiGameplayTabVisible(object tab)
    {
        object visible = ReadMemberAny(tab, "IsVisible", "isVisible", "Visible", "visible");
        return visible == null || ModUiAuditReadBool(visible, string.Empty, true);
    }

    static bool ModUiGameplayTabActive(object tab)
    {
        Component component = tab as Component;
        return component != null && component.gameObject.activeInHierarchy;
    }

    static Component ModUiGameplayMenuTab(object menu)
    {
        return ReadMemberAny(menu, "tab", "Tab") as Component;
    }

    static Component ModUiGameplayMenuTabObject(object menu)
    {
        object value = ReadMemberAny(menu, "tabObject", "TabObject");
        if (value is Component component) return component;
        if (value is GameObject gameObject) return gameObject.transform;
        return null;
    }

    static Component ModUiGameplayFindSelectorForTab(Component root, Component tab, out List<object> selectorTabs)
    {
        selectorTabs = new List<object>();
        Type selectorType = FindTrainingType("BeatSaberMarkupLanguage.Components.TabSelector");
        if (selectorType == null || root == null) return null;
        try
        {
            List<Component> candidates = root.GetComponentsInChildren(selectorType, true).OfType<Component>().ToList();
            if (candidates.Count == 0)
            {
                candidates = Resources.FindObjectsOfTypeAll(selectorType).OfType<Component>()
                    .Where(value => value != null && value.gameObject.scene.IsValid() && value.gameObject.activeInHierarchy)
                    .ToList();
            }
            foreach (Component selector in candidates)
            {
                List<object> tabs = ModUiGameplayEnumerable(ReadMemberAny(selector, "tabs", "Tabs"));
                if (tabs.Count == 0) continue;
                if (tab != null && tabs.Any(value => ReferenceEquals(value, tab) ||
                    value is Component component && component.GetInstanceID() == tab.GetInstanceID()))
                {
                    selectorTabs = tabs;
                    return selector;
                }
            }
        }
        catch { }
        return null;
    }

    static ModUiGameplaySelectorState ModUiGameplayReadSelectorState(Component selector, List<object> selectorTabs)
    {
        ModUiGameplaySelectorState state = new() { Selector = selector, VisibleTabs = selectorTabs.Where(ModUiGameplayTabVisible).ToList() };
        state.Control = ModUiGameplayFindSelectorControl(selector);
        state.PageSize = ModUiGameplayReadInt(selector, "PageCount", "pageCount");
        if (state.PageSize <= 0) state.PageSize = ModUiAuditTabCount(state.Control);
        if (state.PageSize <= 0) state.PageSize = state.VisibleTabs.Count;
        state.OriginalPage = ModUiGameplayReadInt(selector, "currentPage", "CurrentPage");
        if (state.OriginalPage < 0) state.OriginalPage = 0;
        state.OriginalLocal = ModUiAuditSelectedIndex(state.Control);
        state.OriginalGlobal = state.OriginalLocal < 0 || state.PageSize <= 0
            ? -1 : state.OriginalPage * state.PageSize + state.OriginalLocal;
        if (state.OriginalGlobal >= 0 && state.OriginalGlobal < state.VisibleTabs.Count)
            state.OriginalTab = state.VisibleTabs[state.OriginalGlobal];
        else
        {
            state.OriginalTab = state.VisibleTabs.FirstOrDefault(ModUiGameplayTabActive);
            if (state.OriginalTab != null) state.OriginalGlobal = state.VisibleTabs.IndexOf(state.OriginalTab);
        }
        return state;
    }

    static bool ModUiGameplayMoveSelectorPage(ModUiGameplaySelectorState state, int targetPage, out string detail)
    {
        detail = string.Empty;
        if (state == null || state.Selector == null)
        {
            detail = "Native TabSelector is unavailable.";
            return false;
        }
        MethodInfo left = ModUiGameplayPageMethod(state.Selector.GetType(), "PageLeft");
        MethodInfo right = ModUiGameplayPageMethod(state.Selector.GetType(), "PageRight");
        if (left == null || right == null)
        {
            detail = "TabSelector.PageLeft/PageRight native navigation signatures are unavailable.";
            return false;
        }
        int current = ModUiGameplayReadInt(state.Selector, "currentPage", "CurrentPage");
        if (current < 0) current = 0;
        MethodInfo method = targetPage < current ? left : right;
        int count = Math.Abs(targetPage - current);
        try
        {
            for (int index = 0; index < count; index++) method.Invoke(state.Selector, null);
            detail = method.ToString();
            return true;
        }
        catch (Exception error)
        {
            detail = ModUiAuditException(error);
            return false;
        }
    }

    static bool ModUiGameplaySelectGlobal(ModUiGameplaySelectorState state, int globalIndex, out string detail)
    {
        detail = string.Empty;
        if (state == null || state.Control == null || state.PageSize <= 0 ||
            globalIndex < 0 || globalIndex >= state.VisibleTabs.Count)
        {
            detail = "The target tab is outside the live visible TabSelector range.";
            return false;
        }
        int page = globalIndex / state.PageSize;
        int local = globalIndex % state.PageSize;
        if (!ModUiGameplayMoveSelectorPage(state, page, out detail)) return false;
        MethodInfo select = ModUiGameplaySelectMethod(state.Control.GetType());
        if (select == null)
        {
            detail = "HMUI.SegmentedControl.SelectCellWithNumber(int) is unavailable.";
            return false;
        }
        try
        {
            select.Invoke(state.Control, new object[] { local });
            // Programmatic selection only changes the segmented cell highlight.
            // Dispatch the same production callback used by a user click.
            MethodInfo handler = state.Selector.GetType().GetMethod("TabSelected", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            handler?.Invoke(state.Selector, new object[] { state.Control, local });
            detail = select.ToString();
            return true;
        }
        catch (Exception error)
        {
            detail = ModUiAuditException(error);
            return false;
        }
    }

    static string ModUiGameplaySelectorPath(Component selector)
    {
        return selector == null ? string.Empty : ModUiAuditPath(selector.transform);
    }

    IEnumerator ModUiGameplayCaptureNativeTab(ModUiAuditRun run, Component root, Component tab,
        Component selector, Component control, object menu, object flow, Camera camera,
        string scope, int globalIndex, int pageIndex, int localIndex, string selectDetail)
    {
        string name = ModUiGameplayMenuName(menu);
        Dictionary<string, object> page = new()
        {
            ["kind"] = "GameplaySetupNativeTab",
            ["title"] = name,
            ["scope"] = scope,
            ["host"] = ModUiGameplayMenuHost(menu),
            ["assembly"] = ModUiGameplayMenuAssembly(menu),
            ["resource"] = ModUiGameplayMenuResource(menu),
            ["tabObject"] = ReadMemberAny(menu, "tabObject", "TabObject")?.GetType().FullName,
            ["tabPath"] = tab == null ? ModUiAuditPath(ModUiGameplayMenuTabObject(menu)?.transform) : ModUiAuditPath(tab.transform),
            ["selector"] = ModUiGameplaySelectorPath(selector),
            ["selectorType"] = selector?.GetType().FullName,
            ["control"] = control?.GetType().FullName,
            ["globalIndex"] = globalIndex,
            ["pageIndex"] = pageIndex,
            ["localIndex"] = localIndex,
            ["selectMethod"] = selectDetail,
            ["flow"] = ModUiAuditFlowType(flow),
            ["status"] = "blocked"
        };
        run.Pages.Add(page);
        if (root == null || tab == null)
        {
            page["reason"] = "GameplaySetup root or registered menu Tab component was unavailable after native selection.";
            run.Block(scope, name, ModUiAuditString(page["reason"]));
            yield break;
        }

        yield return ModUiAuditWaitLayout(root, 4, 4f);
        bool active = ModUiGameplayTabActive(tab);
        page["active"] = active;
        page["selectedLocal"] = ModUiAuditSelectedIndex(control);
        page["selectedPage"] = ModUiGameplayReadInt(selector, "currentPage", "CurrentPage");
        if (!active)
        {
            page["reason"] = "Native TabSelector selection completed but the registered tab content did not become active.";
            run.Block(scope, name, ModUiAuditString(page["reason"]));
            yield break;
        }

        page["status"] = "verified";
        run.StablePages++;
        ModUiAuditCheckVisibility(run, page, root, camera);
        ModUiAuditCapturePage(run, page, root, camera, "gameplay-native-tab-" + ModUiAuditSafeSlug(scope) + "-" + ModUiAuditSafeSlug(name));
        ModUiAuditControls(run, page, root);
        yield return ModUiAuditScroll(run, page, root, camera, "gameplay-native-tab-" + ModUiAuditSafeSlug(scope) + "-" + ModUiAuditSafeSlug(name));
        yield return ModUiAuditRealHoverHints(run, page, root, camera, scope);
        yield return ModUiAuditModals(run, page, root, camera,
            "gameplay-native-tab-" + ModUiAuditSafeSlug(scope) + "-" + ModUiAuditSafeSlug(name), scope);
    }

    IEnumerator ModUiAuditNativeGameplayTabs(ModUiAuditRun run, object setup, Component root,
        object flow, Camera camera)
    {
        if (run == null) yield break;
        Dictionary<string, object> api = new()
        {
            ["kind"] = "BSML.GameplaySetup.NativeTabSelector",
            ["status"] = "blocked",
            ["setup"] = setup?.GetType().FullName,
            ["root"] = root?.GetType().FullName,
            ["flow"] = ModUiAuditFlowType(flow),
            ["selection"] = "HMUI.SegmentedControl.SelectCellWithNumber(int) through TabSelector.TabSelected; no SetActive calls"
        };
        run.Entrances.Add(api);
        if (setup == null)
        {
            run.Block("BSML.GameplaySetup", "NativeTabs", "GameplaySetup instance was unavailable after SoloFreePlay entry.");
            yield break;
        }
        if (root == null)
        {
            run.Block("BSML.GameplaySetup", "NativeTabs", "GameplaySetupViewController root was unavailable; native selector traversal cannot be verified.");
            yield break;
        }

        Type selectorType = FindTrainingType("BeatSaberMarkupLanguage.Components.TabSelector");
        if (selectorType == null)
        {
            run.Block("BSML.GameplaySetup", "NativeTabs", "BSML TabSelector type is unavailable in the loaded assembly.");
            yield break;
        }
        api["selectorType"] = selectorType.FullName;
        api["selectorAssembly"] = selectorType.Assembly.GetName().Name;
        api["selectMethod"] = ModUiGameplaySelectMethod(FindTrainingType("HMUI.TextSegmentedControl"))?.ToString();
        api["menuType"] = FindTrainingType("BeatSaberMarkupLanguage.GameplaySetup.GameplaySetupMenu")?.FullName;

        List<object> menus = ModUiGameplayEnumerable(ReadMemberAny(setup, "menus", "_menus", "Menus"));
        if (menus.Count == 0)
        {
            run.Block("BSML.GameplaySetup", "NativeTabs", "GameplaySetup.menus was empty or not enumerable after SoloFreePlay entry.");
            yield break;
        }

        Dictionary<int, ModUiGameplaySelectorState> states = new();
        Dictionary<string, int> targetCounts = new(StringComparer.Ordinal)
        {
            ["Camera2"] = 0,
            ["Chroma"] = 0,
            ["CustomSabersLite"] = 0
        };
        int inspected = 0;
        foreach (object menu in menus)
        {
            string scope = ModUiGameplayTargetScope(menu);
            if (string.IsNullOrEmpty(scope)) continue;
            targetCounts[scope]++;
            string name = ModUiGameplayMenuName(menu);
            Component tab = ModUiGameplayMenuTab(menu);
            if (tab == null)
            {
                MethodInfo setupMethod = menu.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(method => method.Name == "Setup" && method.GetParameters().Length == 0);
                if (setupMethod == null)
                {
                    run.Block(scope, name, "GameplaySetupMenu.tab was missing and its native Setup() method was unavailable.");
                    continue;
                }
                try
                {
                    setupMethod.Invoke(menu, null);
                    tab = ModUiGameplayMenuTab(menu);
                }
                catch (Exception error)
                {
                    run.Block(scope, name, "GameplaySetupMenu.Setup() failed: " + ModUiAuditException(error));
                    continue;
                }
            }

            Type tabType = FindTrainingType("BeatSaberMarkupLanguage.Components.Tab");
            foreach (Component parentTab in tab.GetComponentsInParent(tabType, true).OfType<Component>().Where(value => value != tab).Reverse())
            {
                Component parentSelector = ModUiGameplayFindSelectorForTab(root, parentTab, out List<object> parentTabs);
                if (parentSelector == null) continue;
                if (!states.TryGetValue(parentSelector.GetInstanceID(), out ModUiGameplaySelectorState parentState))
                    states[parentSelector.GetInstanceID()] = parentState = ModUiGameplayReadSelectorState(parentSelector, parentTabs);
                int parentIndex = parentState.VisibleTabs.FindIndex(value => ReferenceEquals(value, parentTab));
                if (!ModUiGameplaySelectGlobal(parentState, parentIndex, out string parentDetail)) run.Block(scope, name, parentDetail);
                yield return ModUiAuditWaitLayout(root, 3, 1.5f);
            }
            List<object> selectorTabs;
            Component selector = ModUiGameplayFindSelectorForTab(root, tab, out selectorTabs);
            if (selector == null)
            {
                run.Block(scope, name, "Registered GameplaySetupMenu.tab was not attached to a live native TabSelector.");
                continue;
            }
            int selectorId = selector.GetInstanceID();
            if (!states.TryGetValue(selectorId, out ModUiGameplaySelectorState state))
            {
                state = ModUiGameplayReadSelectorState(selector, selectorTabs);
                states[selectorId] = state;
                if (state.Control == null || ModUiGameplaySelectMethod(state.Control.GetType()) == null)
                {
                    run.Block(scope, name, "Live TabSelector did not expose HMUI.SegmentedControl.SelectCellWithNumber(int).");
                    continue;
                }
            }
            int globalIndex = state.VisibleTabs.FindIndex(value => ReferenceEquals(value, tab) ||
                value is Component component && component.GetInstanceID() == tab.GetInstanceID());
            Dictionary<string, object> entrance = new()
            {
                ["kind"] = "BSML.GameplaySetup.NativeMenuTab",
                ["name"] = name,
                ["scope"] = scope,
                ["host"] = ModUiGameplayMenuHost(menu),
                ["assembly"] = ModUiGameplayMenuAssembly(menu),
                ["resource"] = ModUiGameplayMenuResource(menu),
                ["menuType"] = ModUiAuditString(ReadMemberAny(menu, "MenuType", "menuType")),
                ["tabObject"] = ReadMemberAny(menu, "tabObject", "TabObject")?.GetType().FullName,
                ["tabPath"] = ModUiAuditPath(tab.transform),
                ["selector"] = ModUiGameplaySelectorPath(selector),
                ["selectorType"] = selector.GetType().FullName,
                ["control"] = state.Control.GetType().FullName,
                ["globalIndex"] = globalIndex,
                ["registered"] = true,
                ["status"] = "blocked"
            };
            run.Entrances.Add(entrance);
            if (globalIndex < 0)
            {
                entrance["reason"] = "The menu tab is registered but is not visible in the active GameplaySetup MenuType; it was not forced visible.";
                run.Block(scope, name, ModUiAuditString(entrance["reason"]));
                continue;
            }

            int pageSize = state.PageSize;
            int targetPage = pageSize <= 0 ? 0 : globalIndex / pageSize;
            int localIndex = pageSize <= 0 ? globalIndex : globalIndex % pageSize;
            if (!ModUiGameplaySelectGlobal(state, globalIndex, out string selectDetail))
            {
                entrance["reason"] = selectDetail;
                run.Block(scope, name, "Native GameplaySetup tab selection failed: " + selectDetail);
                continue;
            }
            entrance["status"] = "selected-native";
            entrance["selection"] = selectDetail;
            entrance["pageIndex"] = targetPage;
            entrance["localIndex"] = localIndex;
            yield return null;
            int pageStart = run.Pages.Count;
            yield return ModUiGameplayCaptureNativeTab(run, root, tab, selector, state.Control, menu, flow, camera,
                scope, globalIndex, targetPage, localIndex, selectDetail);
            bool pageVerified = run.Pages.Skip(pageStart)
                .OfType<Dictionary<string, object>>()
                .Any(page => string.Equals(ModUiAuditString(page.TryGetValue("status", out object status) ? status : null), "verified", StringComparison.Ordinal));
            entrance["status"] = pageVerified ? "verified" : "partial";
            if (pageVerified) inspected++;
        }

        foreach (KeyValuePair<int, ModUiGameplaySelectorState> pair in states.Reverse())
        {
            ModUiGameplaySelectorState state = pair.Value;
            if (state.OriginalGlobal < 0)
            {
                run.Block("BSML.GameplaySetup", "tab-restore", "The original native GameplaySetup tab selection could not be read from the live segmented control.");
                continue;
            }
            if (!ModUiGameplaySelectGlobal(state, state.OriginalGlobal, out string restoreDetail))
            {
                run.Block("BSML.GameplaySetup", "tab-restore", "Native GameplaySetup tab restore failed: " + restoreDetail);
                continue;
            }
            yield return ModUiAuditWaitLayout(root, 3, 2f);
            bool restored = state.OriginalTab == null || ModUiGameplayTabActive(state.OriginalTab);
            Dictionary<string, object> restore = new()
            {
                ["kind"] = "BSML.GameplaySetup.NativeRestore",
                ["selector"] = ModUiGameplaySelectorPath(state.Selector),
                ["globalIndex"] = state.OriginalGlobal,
                ["pageIndex"] = state.OriginalPage,
                ["localIndex"] = state.OriginalLocal,
                ["method"] = restoreDetail,
                ["active"] = restored,
                ["status"] = restored ? "verified" : "blocked"
            };
            run.Entrances.Add(restore);
            if (!restored)
                run.Block("BSML.GameplaySetup", "tab-restore", "Native selector restore returned but the original tab content was not active.");
        }

        foreach (KeyValuePair<string, int> target in targetCounts)
            if (target.Value == 0)
                run.Block(target.Key, "GameplaySetupTab", "No registered GameplaySetupMenu matched the requested target scope.");
        api["inspected"] = inspected;
        api["selectorCount"] = states.Count;
        api["status"] = inspected == 3 ? "verified" : "partial";
        if (inspected != 3)
            run.Block("BSML.GameplaySetup", "NativeTabs", "Expected three registered native tabs (Camera2, Chroma, CustomSabers); inspected " + inspected.ToString(CultureInfo.InvariantCulture) + ".");
    }
}
