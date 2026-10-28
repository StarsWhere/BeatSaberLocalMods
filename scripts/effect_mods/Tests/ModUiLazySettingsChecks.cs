using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace EffectModsSmoke;

public sealed partial class Runner
{
    // BSML's settings-submenu is deliberately handled here instead of by
    // opening a second view or by toggling GameObjects.  SubmenuTag creates a
    // real ClickableText whose callback calls
    // ModSettingsFlowCoordinator.OpenMenu(HMUI.ViewController, bool, bool).
    // Invoking that callback is the same production navigation path used by a
    // user click, while keeping the audit read-only.
    sealed class ModUiLazySubmenuEntry
    {
        internal Component Trigger;
        internal Component Controller;
        internal string Label;
        internal string Path;
        internal string Key;
    }

    IEnumerator ModUiAuditSettingsSubmenus(ModUiAuditRun run,
        Dictionary<string, object> page, Component view, Camera camera,
        string slug, string scope)
    {
        if (run == null || page == null || view == null)
        {
            if (run != null) run.Block(scope, slug, "Settings submenu traversal requires an active BSML settings view.");
            yield break;
        }

        List<object> records = new();
        page["settingsSubmenus"] = records;
        HashSet<string> visited = new(StringComparer.Ordinal);
        HashSet<int> visitedControllers = new();
        yield return ModUiAuditSettingsSubmenusWalk(run, page, view, camera, slug, scope,
            records, visited, visitedControllers, 0);
        page["settingsSubmenuCount"] = records.Count;
        if (scope == "ParticleOverdrive" && records.Count < 4)
            run.Block(scope, "Particle submenus", "Expected four installed ParticleOverdrive submenus; captured " + records.Count);
        page["settingsSubmenuStatus"] = records.Count == 0 ? "none-detected" :
            records.Any(value => ModUiAuditString(GetMember(value, "status")) == "blocked") ? "partial" : "verified";
    }

    IEnumerator ModUiAuditSettingsSubmenusWalk(ModUiAuditRun run,
        Dictionary<string, object> parentPage, Component parentView, Camera camera,
        string parentSlug, string scope, List<object> records,
        HashSet<string> visited, HashSet<int> visitedControllers, int depth)
    {
        if (depth > 8)
        {
            run.Block(scope, ModUiAuditString(parentPage["title"]),
                "Settings submenu recursion exceeded the safe depth limit.");
            yield break;
        }

        List<ModUiLazySubmenuEntry> entries = ModUiLazyFindSubmenus(parentView);
        if (entries.Count == 0) yield break;
        foreach (ModUiLazySubmenuEntry entry in entries)
        {
            if (entry == null || entry.Trigger == null) continue;
            if (!visited.Add(entry.Key)) continue;

            Dictionary<string, object> record = new()
            {
                ["kind"] = "BSML.SettingsSubmenu",
                ["scope"] = scope,
                ["parent"] = ModUiAuditString(parentPage["title"]),
                ["label"] = entry.Label,
                ["path"] = entry.Path,
                ["triggerType"] = entry.Trigger.GetType().FullName,
                ["controllerType"] = entry.Controller?.GetType().FullName,
                ["depth"] = depth,
                ["status"] = "blocked"
            };
            records.Add(record);

            HashSet<int> beforeControllers = ModUiLazyActiveViewIds(parentView);
            if (!ModUiLazyInvokeClickable(entry.Trigger, out string openDetail))
            {
                record["reason"] = openDetail;
                run.Block(scope, entry.Label, "settings-submenu open failed: " + openDetail);
                continue;
            }
            record["open"] = openDetail;

            Component submenuView = entry.Controller;
            yield return ModUiAuditWait(run, () =>
                submenuView != null && ModUiAuditViewActive(submenuView), 5, 8f);
            if (submenuView == null || !ModUiAuditViewActive(submenuView))
            {
                submenuView = ModUiLazyFindNewActiveView(parentView, beforeControllers);
            }
            if (submenuView == null || !ModUiAuditViewActive(submenuView))
            {
                record["reason"] = "The production submenu callback returned without an active HMUI.ViewController.";
                run.Block(scope, entry.Label, (string)record["reason"]);
                continue;
            }

            int controllerId = submenuView.GetInstanceID();
            if (!visitedControllers.Add(controllerId))
            {
                record["status"] = "skipped-cycle";
                record["reason"] = "The same submenu ViewController was reached again; traversal stopped to prevent a loop.";
                yield return ModUiAuditSettingsSubmenusBack(run, parentView, submenuView, scope, entry.Label, record);
                continue;
            }

            yield return ModUiAuditWaitLayout(submenuView, 4, 4f);
            yield return new WaitForSecondsRealtime(0.35f);
            Canvas.ForceUpdateCanvases();
            Dictionary<string, object> childPage = new()
            {
                ["kind"] = "BSMLSettingsSubmenuPage",
                ["title"] = entry.Label,
                ["parent"] = ModUiAuditString(parentPage["title"]),
                ["scope"] = scope,
                ["host"] = submenuView.GetType().FullName,
                ["resource"] = ModUiAuditString(parentPage.TryGetValue("resource", out object resource) ? resource : null),
                ["path"] = ModUiAuditPath(submenuView.transform),
                ["depth"] = depth,
                ["status"] = "verified",
                ["active"] = true,
                ["camera"] = camera?.name
            };
            run.Pages.Add(childPage);
            run.StablePages++;
            record["status"] = "verified";
            record["view"] = ModUiAuditPath(submenuView.transform);

            ModUiAuditCheckVisibility(run, childPage, submenuView, camera);
            ModUiAuditCapturePage(run, childPage, submenuView, camera,
                parentSlug + "-submenu-" + ModUiAuditSafeSlug(entry.Label));
            ModUiAuditCaptureHints(run, childPage, submenuView, scope);
            ModUiAuditControls(run, childPage, submenuView);
            yield return ModUiAuditTabs(run, childPage, submenuView, camera,
                parentSlug + "-submenu-" + ModUiAuditSafeSlug(entry.Label));
            yield return ModUiAuditScroll(run, childPage, submenuView, camera,
                parentSlug + "-submenu-" + ModUiAuditSafeSlug(entry.Label));
            yield return ModUiAuditRealHoverHints(run, childPage, submenuView, camera, scope);
            yield return ModUiAuditModals(run, childPage, submenuView, camera,
                parentSlug + "-submenu-" + ModUiAuditSafeSlug(entry.Label), scope);

            // A submenu can itself contain another settings-submenu.  Only
            // active callbacks with a real controller are considered, so the
            // inactive parent template cannot be mistaken for a nested page.
            yield return ModUiAuditSettingsSubmenusWalk(run, childPage, submenuView, camera,
                parentSlug + "-submenu-" + ModUiAuditSafeSlug(entry.Label), scope,
                records, visited, visitedControllers, depth + 1);

            yield return ModUiAuditSettingsSubmenusBack(run, parentView, submenuView,
                scope, entry.Label, record);
        }
    }

    static List<ModUiLazySubmenuEntry> ModUiLazyFindSubmenus(Component view)
    {
        List<ModUiLazySubmenuEntry> result = new();
        if (view == null) return result;
        Type clickableType = FindTrainingType("BeatSaberMarkupLanguage.Components.ClickableText");
        if (clickableType == null) return result;
        try
        {
            foreach (Component trigger in view.GetComponentsInChildren(clickableType, true).OfType<Component>())
            {
                if (trigger == null || !trigger.gameObject.activeInHierarchy) continue;
                Component controller = ModUiLazySubmenuController(trigger);
                // The callback closure is the authoritative marker.  The
                // GameObject name is only a diagnostic fallback and is never
                // enough to open a page by itself.
                if (controller == null) continue;
                string label = trigger is TMP_Text text ? text.text : string.Empty;
                if (string.IsNullOrWhiteSpace(label)) continue;
                string path = ModUiAuditPath(trigger.transform);
                result.Add(new ModUiLazySubmenuEntry
                {
                    Trigger = trigger,
                    Controller = controller,
                    Label = label.Trim(),
                    Path = path,
                    Key = path + "|" + label.Trim() + "|" + trigger.GetInstanceID().ToString(CultureInfo.InvariantCulture)
                });
            }
        }
        catch { }
        return result
            .GroupBy(value => value.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    static Component ModUiLazySubmenuController(Component trigger)
    {
        if (trigger == null) return null;
        try
        {
            Delegate callback = ReadMemberAny(trigger, "OnClickEvent") as Delegate;
            if (callback == null) return null;
            foreach (Delegate invocation in callback.GetInvocationList())
            {
                object target = invocation.Target;
                if (target == null) continue;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
                foreach (FieldInfo field in target.GetType().GetFields(flags)
                    .Where(value => value.Name.IndexOf("submenuController", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    if (field.GetValue(target) is Component component && component != null)
                        return component;
                }
                foreach (PropertyInfo property in target.GetType().GetProperties(flags)
                    .Where(value => value.Name.IndexOf("submenuController", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        value.GetIndexParameters().Length == 0))
                {
                    if (property.GetValue(target, null) is Component component && component != null)
                        return component;
                }
            }
        }
        catch { }
        return null;
    }

    static HashSet<int> ModUiLazyActiveViewIds(Component except)
    {
        HashSet<int> result = new();
        Type viewType = FindTrainingType("HMUI.ViewController");
        if (viewType == null) return result;
        try
        {
            foreach (Component view in Resources.FindObjectsOfTypeAll(viewType).OfType<Component>())
            {
                if (view == null || ReferenceEquals(view, except) || !view.gameObject.scene.IsValid() ||
                    !view.gameObject.activeInHierarchy) continue;
                result.Add(view.GetInstanceID());
            }
        }
        catch { }
        return result;
    }

    static Component ModUiLazyFindNewActiveView(Component except, HashSet<int> before)
    {
        Type viewType = FindTrainingType("HMUI.ViewController");
        if (viewType == null) return null;
        try
        {
            return Resources.FindObjectsOfTypeAll(viewType).OfType<Component>()
                .Where(value => value != null && !ReferenceEquals(value, except) &&
                    value.gameObject.scene.IsValid() && value.gameObject.activeInHierarchy &&
                    (before == null || !before.Contains(value.GetInstanceID())))
                .OrderByDescending(value => value.GetInstanceID())
                .FirstOrDefault();
        }
        catch { return null; }
    }

    static bool ModUiLazyInvokeClickable(Component clickable, out string detail)
    {
        detail = string.Empty;
        if (clickable == null) { detail = "ClickableText is unavailable"; return false; }
        try
        {
            Delegate callback = ReadMemberAny(clickable, "OnClickEvent") as Delegate;
            if (callback == null)
            {
                detail = "ClickableText.OnClickEvent has no production callback";
                return false;
            }
            List<string> callbacks = new();
            foreach (Delegate invocation in callback.GetInvocationList())
            {
                MethodInfo method = invocation.Method;
                callbacks.Add((method.DeclaringType?.FullName ?? string.Empty) + "." + method.Name);
                ParameterInfo[] parameters = method.GetParameters();
                object[] arguments = parameters.Length == 0 ? Array.Empty<object>() : new object[parameters.Length];
                invocation.DynamicInvoke(arguments);
            }
            detail = "ClickableText.OnClickEvent invoked; callbacks=" + string.Join(",", callbacks.ToArray());
            return true;
        }
        catch (Exception error)
        {
            detail = ModUiAuditException(error);
            return false;
        }
    }

    IEnumerator ModUiAuditSettingsSubmenusBack(ModUiAuditRun run, Component parentView,
        Component submenuView, string scope, string label, Dictionary<string, object> record)
    {
        Component back = ModUiLazyFindBackClickable(submenuView);
        if (back == null)
        {
            record["backStatus"] = "blocked";
            record["backReason"] = "No active BSML ClickableText with a Back caption was found.";
            run.Block(scope, label, (string)record["backReason"]);
            yield break;
        }
        if (!ModUiLazyInvokeClickable(back, out string detail))
        {
            record["backStatus"] = "blocked";
            record["backReason"] = detail;
            run.Block(scope, label, "settings-submenu back failed: " + detail);
            yield break;
        }
        record["backAction"] = detail;
        yield return ModUiAuditWait(run, () =>
            parentView != null && ModUiAuditViewActive(parentView) &&
            (submenuView == null || !submenuView.gameObject.activeInHierarchy), 5, 8f);
        bool restored = parentView != null && ModUiAuditViewActive(parentView);
        record["backStatus"] = restored ? "verified" : "partial";
        record["restored"] = restored;
        if (!restored)
            run.Block(scope, label, "Back callback ran but the parent settings view was not restored.");
    }

    static Component ModUiLazyFindBackClickable(Component view)
    {
        if (view == null) return null;
        Type clickableType = FindTrainingType("BeatSaberMarkupLanguage.Components.ClickableText");
        if (clickableType == null) return null;
        try
        {
            return view.GetComponentsInChildren(clickableType, true).OfType<Component>()
                .Where(value => value != null && value.gameObject.activeInHierarchy &&
                    ModUiLazyIsBackCaption(value is TMP_Text text ? text.text : string.Empty))
                .OrderBy(value => ModUiAuditPath(value.transform).Length)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    static bool ModUiLazyIsBackCaption(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string compact = new string(text.Trim().Where(char.IsLetterOrDigit).ToArray());
        return string.Equals(compact, "back", StringComparison.OrdinalIgnoreCase) ||
            compact.IndexOf("back", StringComparison.OrdinalIgnoreCase) >= 0 ||
            compact == "返回" || compact == "后退" || compact == "返回上一级";
    }

    IEnumerator ModUiAuditBssQueue(ModUiAuditRun run, object flow, Camera camera)
    {
        const string scope = "BetterSongSearch";
        object detailController = ReadMemberAny(flow, "detailView", "DetailView") ??
            GetStaticMember(FindTrainingType("BetterSongSearch.UI.BSSFlowCoordinator"), "detailView");
        if (detailController == null)
            detailController = ModUiAuditFindActiveController("BetterSongSearch.UI.DetailController");

        Dictionary<string, object> entrance = new()
        {
            ["kind"] = "BetterSongSearch.DetailController",
            ["scope"] = scope,
            ["action"] = "ShowQueue",
            ["status"] = "blocked",
            ["readOnly"] = true,
            ["resource"] = "BetterSongSearch.UI.Views.DownloadHistory.bsml"
        };
        run.Entrances.Add(entrance);
        if (detailController == null)
        {
            entrance["reason"] = "BetterSongSearch.DetailController is not active in the native Solo song-selection flow.";
            run.Block(scope, "ShowQueue", (string)entrance["reason"]);
            yield break;
        }

        Component detailRoot = detailController as Component;
        bool wasQueueVisible = ReadBool(detailController, "QueueVisible", false);
        if (!ModUiAuditInvokeSafeLazyAction(detailController, "ShowQueue", out string showDetail))
        {
            entrance["reason"] = showDetail;
            run.Block(scope, "ShowQueue", "DetailController.ShowQueue failed: " + showDetail);
            yield break;
        }
        entrance["status"] = "invoked";
        entrance["detail"] = showDetail;

        Component queueRoot = ReadMemberAny(detailController, "queueArea", "QueueArea") as Component;
        queueRoot ??= detailRoot;
        yield return ModUiAuditWait(run, () => ReadBool(detailController, "QueueVisible", false) &&
            queueRoot != null && queueRoot.gameObject.activeInHierarchy, 5, 8f);
        if (queueRoot == null || !queueRoot.gameObject.activeInHierarchy)
        {
            entrance["status"] = "blocked";
            entrance["reason"] = "ShowQueue returned without an active queueArea.";
            run.Block(scope, "DownloadHistory", (string)entrance["reason"]);
            yield break;
        }

        // DownloadHistory.bsml is mounted below queueArea, but the native
        // DetailController owns the always-visible Details/Downloads header
        // and queue summary.  An empty queue intentionally has no row text or
        // row controls, so visibility and frames must include that outer root.
        Component frameRoot = detailRoot ?? queueRoot;
        yield return ModUiAuditWaitLayout(frameRoot, 4, 4f);
        yield return new WaitForSecondsRealtime(0.35f);
        Canvas.ForceUpdateCanvases();
        Dictionary<string, object> page = new()
        {
            ["kind"] = "BSSDownloadHistory",
            ["title"] = "Download history",
            ["scope"] = scope,
            ["host"] = detailController.GetType().FullName,
            ["resource"] = "BetterSongSearch.UI.Views.DownloadHistory.bsml",
            ["action"] = "ShowQueue",
            ["status"] = "verified",
            ["active"] = true,
            ["readOnly"] = true,
            ["camera"] = camera?.name,
            ["originalQueueVisible"] = wasQueueVisible,
            ["framePath"] = ModUiAuditPath(frameRoot?.transform),
            ["queuePath"] = ModUiAuditPath(queueRoot.transform)
        };
        run.Pages.Add(page);
        run.StablePages++;
        entrance["status"] = "verified";
        entrance["path"] = ModUiAuditPath(queueRoot.transform);

        ModUiAuditCheckVisibility(run, page, frameRoot, camera);
        ModUiAuditCapturePage(run, page, frameRoot, camera, "bss-download-history-top");
        ModUiAuditCaptureHints(run, page, frameRoot, scope);
        ModUiAuditControls(run, page, frameRoot);
        yield return ModUiAuditTabs(run, page, frameRoot, camera, "bss-download-history");
        yield return ModUiAuditScroll(run, page, frameRoot, camera, "bss-download-history");
        yield return ModUiAuditBssQueueTableScroll(run, page, queueRoot, frameRoot, camera);
        yield return ModUiAuditRealHoverHints(run, page, frameRoot, camera, scope);
        yield return ModUiAuditModals(run, page, frameRoot, camera, "bss-download-history", scope);

        string restoreAction = wasQueueVisible ? "ShowQueue" : "ShowDetails";
        bool restoredAction = ModUiAuditInvokeSafeLazyAction(detailController, restoreAction, out string restoreDetail);
        entrance["restoreAction"] = restoreAction;
        entrance["restoreDetail"] = restoreDetail;
        if (!restoredAction)
        {
            entrance["restoreStatus"] = "blocked";
            run.Block(scope, "DownloadHistory", "Returning to the original DetailController tab failed: " + restoreDetail);
            yield break;
        }
        yield return ModUiAuditWait(run, () => ReadBool(detailController, "QueueVisible", false) == wasQueueVisible, 5, 6f);
        bool restored = ReadBool(detailController, "QueueVisible", false) == wasQueueVisible;
        entrance["restoreStatus"] = restored ? "verified" : "partial";
        if (!restored)
            run.Block(scope, "DownloadHistory", "DetailController restore action ran but QueueVisible did not return to its original value.");
    }

    IEnumerator ModUiAuditBssQueueTableScroll(ModUiAuditRun run,
        Dictionary<string, object> page, Component tableRoot, Component frameRoot, Camera camera)
    {
        Type tableType = FindTrainingType("HMUI.TableView");
        if (tableType == null || tableRoot == null) yield break;
        Component[] tables;
        try { tables = tableRoot.GetComponentsInChildren(tableType, true).OfType<Component>().ToArray(); }
        catch { yield break; }
        List<object> records = new();
        page["tableScroll"] = records;
        int rowCount = 0;
        bool rowCountKnown = true;
        foreach (Component table in tables)
        {
            MethodInfo scroll = AccessTools.Method(table.GetType(), "ScrollToPosition",
                new[] { typeof(float), typeof(bool) });
            if (scroll == null)
            {
                records.Add(new { path = ModUiAuditPath(table.transform), status = "blocked-api-missing" });
                run.Block(ModUiAuditString(page["scope"]), ModUiAuditString(page["title"]),
                    "DownloadHistory TableView has no safe ScrollToPosition(float,bool) method.");
                continue;
            }
            object countValue = ReadMemberAny(table, "numberOfCells", "NumberOfCells");
            int count = ReadInt(countValue, string.Empty, 0);
            Dictionary<string, object> record = new()
            {
                ["path"] = ModUiAuditPath(table.transform),
                ["type"] = table.GetType().FullName,
                ["cells"] = count,
                ["rowCountKnown"] = countValue != null,
                ["emptyTable"] = countValue != null && count == 0,
                ["status"] = "blocked"
            };
            records.Add(record);
            if (countValue == null) rowCountKnown = false;
            else rowCount += Math.Max(0, count);
            if (!ModUiLazyInvokeTableScroll(scroll, table, 0f, out string error))
            {
                record["reason"] = error;
                run.Block(ModUiAuditString(page["scope"]), ModUiAuditString(page["title"]),
                    "DownloadHistory table scroll failed: " + record["reason"]);
                continue;
            }
            yield return ModUiAuditWaitLayout(tableRoot, 3, 1.25f);
            ModUiAuditCapturePage(run, page, frameRoot ?? tableRoot, camera, "bss-download-history-table-top");
            if (!ModUiLazyInvokeTableScroll(scroll, table, 1f, out error))
            {
                record["reason"] = error;
                run.Block(ModUiAuditString(page["scope"]), ModUiAuditString(page["title"]),
                    "DownloadHistory table end scroll failed: " + record["reason"]);
                continue;
            }
            yield return ModUiAuditWaitLayout(tableRoot, 3, 1.25f);
            ModUiAuditCheckVisibility(run, page, frameRoot ?? tableRoot, camera);
            ModUiAuditCapturePage(run, page, frameRoot ?? tableRoot, camera, "bss-download-history-table-bottom");
            if (!ModUiLazyInvokeTableScroll(scroll, table, 0f, out error))
            {
                record["reason"] = error;
                run.Block(ModUiAuditString(page["scope"]), ModUiAuditString(page["title"]),
                    "DownloadHistory table scroll reset failed: " + record["reason"]);
                continue;
            }
            record["status"] = "verified";
            record["restored"] = true;
        }
        page["rowCount"] = rowCountKnown ? rowCount : null;
        page["rowCountKnown"] = rowCountKnown;
        page["queueState"] = rowCountKnown && rowCount == 0 ? "empty-table" :
            rowCountKnown ? "has-rows" : "row-count-unavailable";
        // Empty DownloadHistory is a valid read-only state.  The frame root
        // already supplied the native header and summary, so absent rows do
        // not create a blocked visibility result.
    }

    static bool ModUiLazyInvokeTableScroll(MethodInfo method, Component table,
        float position, out string error)
    {
        error = string.Empty;
        try
        {
            method.Invoke(table, new object[] { position, false });
            return true;
        }
        catch (Exception exception)
        {
            error = ModUiAuditException(exception);
            return false;
        }
    }
}
