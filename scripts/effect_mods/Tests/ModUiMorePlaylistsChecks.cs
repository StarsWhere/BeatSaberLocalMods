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
    static object ModUiMorePlaylistsRead(object value, params string[] names)
    {
        return value == null ? null : ReadMemberAny(value, names);
    }

    static List<object> ModUiMorePlaylistsValues(object value)
    {
        if (value is not IEnumerable values || value is string) return new List<object>();
        try { return values.Cast<object>().Where(item => item != null).ToList(); }
        catch { return new List<object>(); }
    }

    static Component ModUiMorePlaylistsView(object value)
    {
        if (value is Component component) return component;
        return ModUiMorePlaylistsRead(value, "ViewController", "viewController") as Component;
    }

    static Component ModUiMorePlaylistsFindActive(string typeName)
    {
        Type type = FindTrainingType(typeName);
        if (type == null) return null;
        try
        {
            return Resources.FindObjectsOfTypeAll(type).OfType<Component>()
                .Where(value => value != null && value.gameObject.scene.IsValid())
                .OrderByDescending(value => value.gameObject.activeInHierarchy)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    static string ModUiMorePlaylistsType(object value)
    {
        return value?.GetType().FullName ?? string.Empty;
    }

    static Dictionary<string, object> ModUiMorePlaylistsPage(ModUiAuditRun run,
        string title, string host, string resource, string status = "blocked")
    {
        Dictionary<string, object> page = new()
        {
            ["kind"] = "MorePlaylistsPage",
            ["title"] = title,
            ["scope"] = "MorePlaylists",
            ["host"] = host,
            ["resource"] = resource,
            ["status"] = status
        };
        run.Pages.Add(page);
        return page;
    }

    static void ModUiMorePlaylistsBlock(ModUiAuditRun run, Dictionary<string, object> page, string reason)
    {
        page["status"] = "blocked";
        page["reason"] = reason;
        run.Block("MorePlaylists", ModUiAuditString(page["title"]), reason);
        run.BlockedPages++;
    }

    static bool ModUiMorePlaylistsInvokeNoArg(object target, string methodName, out string detail)
    {
        detail = string.Empty;
        if (target == null)
        {
            detail = "target is unavailable";
            return false;
        }

        MethodInfo method = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(value => value.Name == methodName && value.GetParameters().Length == 0);
        if (method == null)
        {
            detail = target.GetType().FullName + "." + methodName + "() is unavailable";
            return false;
        }

        try
        {
            method.Invoke(target, null);
            detail = target.GetType().FullName + "." + methodName + "() invoked";
            return true;
        }
        catch (Exception error)
        {
            detail = ModUiAuditException(error);
            return false;
        }
    }

    static bool ModUiMorePlaylistsInvokeButton(object owner, string fieldName, string fallbackMethod,
        out string detail)
    {
        detail = string.Empty;
        object value = ModUiMorePlaylistsRead(owner, fieldName);
        Button button = value as Button;
        if (button == null && value is Component component)
        {
            try { button = component.GetComponentInChildren<Button>(true); }
            catch { }
        }

        if (button != null)
        {
            if (!button.gameObject.activeInHierarchy)
            {
                detail = "Button is inactive: " + ModUiAuditPath(button.transform);
                return false;
            }
            if (!button.interactable)
            {
                detail = "Button is not interactable: " + ModUiAuditPath(button.transform);
                return false;
            }
            try
            {
                button.onClick.Invoke();
                detail = "UnityEngine.UI.Button.onClick invoked; path=" + ModUiAuditPath(button.transform);
                return true;
            }
            catch (Exception error)
            {
                detail = ModUiAuditException(error);
                return false;
            }
        }

        return ModUiMorePlaylistsInvokeNoArg(owner, fallbackMethod, out detail);
    }

    static bool ModUiMorePlaylistsInvokeParameterized(object target, string methodName,
        object primary, bool animateIn, out string detail)
    {
        detail = string.Empty;
        if (target == null || primary == null)
        {
            detail = "target or primary argument is unavailable";
            return false;
        }

        MethodInfo method = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(value => value.Name == methodName && !value.IsGenericMethod)
            .OrderBy(value => value.GetParameters().Length)
            .FirstOrDefault(value => value.GetParameters().Length >= 1 &&
                value.GetParameters().Any(parameter => parameter.ParameterType.IsInstanceOfType(primary)));
        if (method == null)
        {
            detail = target.GetType().FullName + "." + methodName + "(…) is unavailable for " + primary.GetType().FullName;
            return false;
        }

        ParameterInfo[] parameters = method.GetParameters();
        object[] arguments = new object[parameters.Length];
        bool assignedPrimary = false;
        for (int index = 0; index < parameters.Length; index++)
        {
            ParameterInfo parameter = parameters[index];
            Type type = parameter.ParameterType;
            if (!assignedPrimary && type.IsInstanceOfType(primary))
            {
                arguments[index] = primary;
                assignedPrimary = true;
            }
            else if (parameter.HasDefaultValue)
            {
                arguments[index] = parameter.DefaultValue;
            }
            else if (type.IsEnum)
            {
                string name = animateIn ? "In" : "Out";
                try { arguments[index] = Enum.Parse(type, name, true); }
                catch { arguments[index] = Enum.GetValues(type).GetValue(0); }
            }
            else if (type == typeof(bool))
            {
                arguments[index] = parameter.Name != null &&
                    parameter.Name.IndexOf("immediately", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            else if (!type.IsValueType || typeof(Delegate).IsAssignableFrom(type))
            {
                arguments[index] = null;
            }
            else
            {
                try { arguments[index] = Activator.CreateInstance(type); }
                catch { arguments[index] = null; }
            }
        }

        if (!assignedPrimary)
        {
            detail = "No parameter accepted " + primary.GetType().FullName;
            return false;
        }

        try
        {
            method.Invoke(target, arguments);
            detail = target.GetType().FullName + "." + methodName + " invoked: " + method;
            return true;
        }
        catch (Exception error)
        {
            detail = ModUiAuditException(error);
            return false;
        }
    }

    static int ModUiMorePlaylistsCount(object value)
    {
        return ModUiMorePlaylistsValues(value).Count;
    }

    static string ModUiMorePlaylistsCacheMembers(object entry, out bool detailCached)
    {
        List<string> cached = new();
        foreach (string name in new[] { "playlistDetail", "beatmapsPage", "RemotePlaylist", "songs" })
        {
            object member = ModUiMorePlaylistsRead(entry, name);
            bool present = member is IEnumerable enumerable && member is not string
                ? enumerable.Cast<object>().Any()
                : member != null;
            if (present) cached.Add(name);
        }

        detailCached = cached.Any(name => name == "playlistDetail" || name == "RemotePlaylist");
        return cached.Count == 0 ? "none" : string.Join(",", cached.ToArray());
    }

    static string ModUiMorePlaylistsEntryType(object entry)
    {
        return entry == null ? string.Empty : entry.GetType().FullName;
    }

    static List<object> ModUiMorePlaylistsRows(object controller)
    {
        object table = ModUiMorePlaylistsRead(controller, "customListTableData");
        return ModUiMorePlaylistsValues(ModUiMorePlaylistsRead(table, "Data", "data"));
    }

    static bool ModUiMorePlaylistsModalActive(Component modal)
    {
        try { return modal != null && modal.gameObject.activeInHierarchy; }
        catch { return false; }
    }

    IEnumerator ModUiMorePlaylistsCapturePage(ModUiAuditRun run, Dictionary<string, object> page,
        Component view, Camera camera, string slug, bool tabs, bool modals)
    {
        if (view == null)
        {
            ModUiMorePlaylistsBlock(run, page, "ViewController component is unavailable.");
            yield break;
        }

        yield return ModUiAuditWaitLayout(view, 4, 4f);
        camera ??= FindTrainingMenuCamera();
        page["camera"] = camera?.name;
        page["active"] = ModUiAuditViewActive(view);
        if (!ModUiAuditViewActive(view))
        {
            ModUiMorePlaylistsBlock(run, page, "ViewController did not become active after the native navigation action.");
            yield break;
        }

        page["status"] = "verified";
        run.StablePages++;
        try
        {
            ModUiAuditCheckVisibility(run, page, view, camera);
            ModUiAuditCapturePage(run, page, view, camera, slug + "-top");
            ModUiAuditCaptureHints(run, page, view, "MorePlaylists");
            ModUiAuditControls(run, page, view);
        }
        catch (Exception error)
        {
            page["captureReason"] = ModUiAuditException(error);
            run.Block("MorePlaylists", ModUiAuditString(page["title"]), "Initial page capture failed: " + ModUiAuditException(error));
        }

        if (tabs) yield return ModUiAuditTabs(run, page, view, camera, slug);
        yield return ModUiAuditScroll(run, page, view, camera, slug);
        yield return ModUiAuditRealHoverHints(run, page, view, camera, "MorePlaylists");
        if (modals) yield return ModUiAuditModals(run, page, view, camera, slug, "MorePlaylists");
    }

    IEnumerator ModUiMorePlaylistsCaptureShownModal(ModUiAuditRun run,
        Dictionary<string, object> page, Component root, Camera camera, Component modal,
        string slug, string actionDetail)
    {
        Dictionary<string, object> record = new()
        {
            ["kind"] = "MorePlaylistsModal",
            ["page"] = ModUiAuditString(page["title"]),
            ["scope"] = "MorePlaylists",
            ["host"] = modal?.GetType().FullName,
            ["path"] = ModUiAuditPath(modal?.transform),
            ["showAction"] = actionDetail,
            ["status"] = ModUiMorePlaylistsModalActive(modal) ? "verified" : "blocked"
        };
        run.Modals.Add(record);
        if (modal == null)
        {
            record["reason"] = "HMUI.ModalView component was not injected by the real controller.";
            run.Block("MorePlaylists", ModUiAuditString(page["title"]), (string)record["reason"]);
            yield break;
        }

        yield return ModUiAuditWait(run, () => ModUiMorePlaylistsModalActive(modal), 5, 5f);
        if (!ModUiMorePlaylistsModalActive(modal))
        {
            record["reason"] = "Native modal Show/event did not activate the modal.";
            run.Block("MorePlaylists", ModUiAuditString(page["title"]), (string)record["reason"]);
            yield break;
        }

        page["modalAction"] = actionDetail;
        ModUiAuditCapturePage(run, page, root, camera, slug + "-shown");
        ModUiAuditCaptureHints(run, page, root, "MorePlaylists");
        yield return ModUiAuditRealHoverHints(run, page, root, camera, "MorePlaylists");
        run.ShownModals.Add(modal.GetInstanceID());

        bool hidden = ModUiAuditInvokeModal(modal, "Hide", out string hideSignature, out string hideError);
        record["hideSignature"] = hideSignature;
        record["hideReason"] = hideError;
        record["restored"] = hidden;
        record["status"] = hidden ? "verified" : "partial";
        if (!hidden)
            run.Block("MorePlaylists", ModUiAuditString(page["title"]), "Native modal Hide failed: " + hideError);
        yield return ModUiAuditWait(run, () => !ModUiMorePlaylistsModalActive(modal), 5, 5f);
    }

    static Component ModUiMorePlaylistsControllerModal(object controller, string memberName)
    {
        Component modal = ModUiMorePlaylistsRead(controller, memberName) as Component;
        if (modal != null) return modal;
        Type modalType = FindTrainingType("HMUI.ModalView");
        if (modalType == null) return null;
        try
        {
            return Resources.FindObjectsOfTypeAll(modalType).OfType<Component>()
                .Where(value => value != null && value.gameObject.scene.IsValid() &&
                    ModUiAuditPath(value.transform).IndexOf(memberName.Replace("View", ""), StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(value => value.gameObject.activeInHierarchy)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    IEnumerator ModUiMorePlaylistsSourceModal(ModUiAuditRun run, object flow,
        object listController, object sourceModalController, Camera camera)
    {
        Dictionary<string, object> page = ModUiMorePlaylistsPage(run, "More Playlists Sources",
            ModUiMorePlaylistsType(sourceModalController), "MorePlaylists.UI.Views.SourceModal.bsml");
        if (listController == null || sourceModalController == null)
        {
            ModUiMorePlaylistsBlock(run, page, "ListViewController or SourceModalController is unavailable.");
            yield break;
        }

        bool invoked = ModUiMorePlaylistsInvokeNoArg(flow, "SourceButtonClicked", out string detail);
        if (!invoked)
            invoked = ModUiMorePlaylistsInvokeButton(listController, "sourceButton", "DisplaySources", out detail);
        page["action"] = detail;
        if (!invoked)
        {
            ModUiMorePlaylistsBlock(run, page, "Native SourceButtonClicked/source-button action failed: " + detail);
            yield break;
        }

        Component modal = null;
        yield return ModUiAuditWait(run, () =>
        {
            modal = ModUiMorePlaylistsControllerModal(sourceModalController, "sourceModalView");
            return ModUiMorePlaylistsModalActive(modal);
        }, 8, 8f);
        if (!ModUiMorePlaylistsModalActive(modal))
        {
            ModUiMorePlaylistsBlock(run, page, "SourceModalController.ShowModal emitted the event but its HMUI.ModalView did not activate.");
            yield break;
        }

        List<object> rows = ModUiMorePlaylistsRows(sourceModalController);
        page["sourceRows"] = rows.Select(row => new
        {
            text = ModUiAuditString(ModUiMorePlaylistsRead(row, "text", "Text", "name", "Name")),
            subtext = ModUiAuditString(ModUiMorePlaylistsRead(row, "subtext", "Subtext"))
        }).ToArray();
        page["sourceRowCount"] = rows.Count;
        page["sourceDataStatus"] = rows.Count > 0 ? "verified-read-only" : "blocked-no-source-rows-after-native-parse";
        if (rows.Count == 0)
            run.Block("MorePlaylists", ModUiAuditString(page["title"]), "SourceModal parsed but its native source table has no rows.");
        else
            page["status"] = "verified";

        Component root = ModUiMorePlaylistsView(listController) ?? listController as Component;
        yield return ModUiMorePlaylistsCaptureShownModal(run, page, root, camera, modal,
            "more-playlists-source-modal", detail);
    }

    IEnumerator ModUiMorePlaylistsFilterView(ModUiAuditRun run, object listController, Camera camera)
    {
        Dictionary<string, object> page = ModUiMorePlaylistsPage(run, "BeatSaver Filters",
            "MorePlaylists.BeatSaver.BeatSaverFiltersViewController", "MorePlaylists.BeatSaver.BeatSaverFiltersView.bsml");
        if (listController == null || ModUiMorePlaylistsType(listController).IndexOf("BeatSaver", StringComparison.OrdinalIgnoreCase) < 0)
        {
            ModUiMorePlaylistsBlock(run, page, "The selected MorePlaylists source is not BeatSaver; no safe FilterViewRequested entry is active.");
            yield break;
        }

        bool invoked = ModUiMorePlaylistsInvokeButton(listController, "filtersButton", "FilterClicked", out string detail);
        page["action"] = detail;
        if (!invoked)
        {
            ModUiMorePlaylistsBlock(run, page, "Native filters button/FilterClicked action failed: " + detail);
            yield break;
        }

        Component filter = null;
        yield return ModUiAuditWait(run, () =>
        {
            filter = ModUiMorePlaylistsFindActive("MorePlaylists.BeatSaver.BeatSaverFiltersViewController");
            return ModUiAuditViewActive(filter);
        }, 8, 10f);
        if (!ModUiAuditViewActive(filter))
        {
            ModUiMorePlaylistsBlock(run, page, "FilterViewRequested fired but BeatSaverFiltersViewController did not become active.");
            yield break;
        }

        yield return ModUiMorePlaylistsCapturePage(run, page, filter, camera,
            "more-playlists-filters", true, false);

        bool dismissed = ModUiMorePlaylistsInvokeNoArg(filter, "CancelClicked", out string dismissDetail);
        page["dismissAction"] = dismissDetail;
        yield return ModUiAuditWait(run, () => !ModUiAuditViewActive(filter), 8, 8f);
        if (ModUiAuditViewActive(filter))
        {
            // The concrete controller owns RequestDismiss; use its actual event
            // as a fallback without invoking Ok or changing filter values.
            Delegate requestDismiss = ModUiMorePlaylistsRead(filter, "RequestDismiss") as Delegate;
            if (requestDismiss != null)
            {
                try { requestDismiss.DynamicInvoke(null); }
                catch (Exception error) { dismissDetail = ModUiAuditException(error); }
            }
            yield return ModUiAuditWait(run, () => !ModUiAuditViewActive(filter), 8, 8f);
        }
        if (ModUiAuditViewActive(filter))
        {
            page["dismissStatus"] = "blocked";
            run.Block("MorePlaylists", ModUiAuditString(page["title"]), "Filter view remained active after native Cancel/RequestDismiss.");
        }
        else page["dismissStatus"] = "verified";
    }

    IEnumerator ModUiMorePlaylistsPopupModal(ModUiAuditRun run, object popupController,
        Component root, Camera camera)
    {
        Dictionary<string, object> page = ModUiMorePlaylistsPage(run, "More Playlists Popup Modal",
            ModUiMorePlaylistsType(popupController), "MorePlaylists.UI.Views.PopupModals.bsml");
        if (popupController == null || root == null)
        {
            ModUiMorePlaylistsBlock(run, page, "PopupModalsController or list root is unavailable.");
            yield break;
        }

        MethodInfo show = popupController.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(value => value.Name == "ShowOkModal" && value.GetParameters().Length == 5)
            .FirstOrDefault();
        if (show == null)
        {
            ModUiMorePlaylistsBlock(run, page, "PopupModalsController.ShowOkModal(Transform,string,Action,string,bool) is unavailable.");
            yield break;
        }

        const string auditMessage = "Error";
        try
        {
            show.Invoke(popupController, new object[] { root.transform, auditMessage, null, "Ok", true });
            page["action"] = "ShowOkModal invoked with synthetic read-only audit text";
            page["syntheticMessage"] = true;
            page["messageSource"] = auditMessage;
        }
        catch (Exception error)
        {
            ModUiMorePlaylistsBlock(run, page, "ShowOkModal failed: " + ModUiAuditException(error));
            yield break;
        }

        Component modal = null;
        yield return ModUiAuditWait(run, () =>
        {
            modal = ModUiMorePlaylistsControllerModal(popupController, "okModalView");
            return ModUiMorePlaylistsModalActive(modal);
        }, 8, 8f);
        if (!ModUiMorePlaylistsModalActive(modal))
        {
            ModUiMorePlaylistsBlock(run, page, "PopupModalsController emitted open-ok but its HMUI.ModalView did not activate.");
            yield break;
        }

        page["status"] = "verified";
        yield return ModUiMorePlaylistsCaptureShownModal(run, page, root, camera, modal,
            "more-playlists-popup-modal", "PopupModalsController.ShowOkModal");
    }

    static bool ModUiMorePlaylistsInvokeShowDetail(object detailController, object entry, out string detail)
    {
        detail = string.Empty;
        if (detailController == null || entry == null)
        {
            detail = "detail controller or cached entry is unavailable";
            return false;
        }
        MethodInfo method = detailController.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(value => value.Name == "ShowDetail" && value.GetParameters().Length == 1)
            .FirstOrDefault(value => value.GetParameters()[0].ParameterType.IsInstanceOfType(entry));
        if (method == null)
        {
            detail = detailController.GetType().FullName + ".ShowDetail(entry) is unavailable for " + entry.GetType().FullName;
            return false;
        }
        try
        {
            method.Invoke(detailController, new[] { entry });
            detail = detailController.GetType().FullName + ".ShowDetail(cachedEntry) invoked";
            return true;
        }
        catch (Exception error)
        {
            detail = ModUiAuditException(error);
            return false;
        }
    }

    static bool ModUiMorePlaylistsInvokeNativeEntrySelect(object listController, object table,
        int row, out string detail)
    {
        detail = string.Empty;
        if (listController == null || table == null)
        {
            detail = "list controller or native table is unavailable";
            return false;
        }

        // SelectCellWithIdx(int,bool) only changes the highlight unless its
        // second argument is true.  DidSelectCellWithIdx(int) is HMUI's
        // production callback entry and is the same event path BSML wires to
        // the controller's Select(TableView,int) action.
        MethodInfo tableCallback = table.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(value => value.Name == "DidSelectCellWithIdx" && value.GetParameters().Length == 1 &&
                value.GetParameters()[0].ParameterType == typeof(int));
        if (tableCallback != null)
        {
            try
            {
                tableCallback.Invoke(table, new object[] { row });
                detail = table.GetType().FullName + ".DidSelectCellWithIdx(" + row + ") invoked; native select-cell callback requested";
                return true;
            }
            catch (Exception error)
            {
                detail = "TableView.DidSelectCellWithIdx failed: " + ModUiMorePlaylistsExceptionDetails(error);
            }
        }

        MethodInfo tableSelect = table.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(value => value.Name == "SelectCellWithIdx" && value.GetParameters().Length == 2 &&
                value.GetParameters()[0].ParameterType == typeof(int) && value.GetParameters()[1].ParameterType == typeof(bool));
        if (tableSelect != null)
        {
            try
            {
                tableSelect.Invoke(table, new object[] { row, true });
                detail = table.GetType().FullName + ".SelectCellWithIdx(" + row + ",true) invoked; native callback requested";
                return true;
            }
            catch (Exception error)
            {
                detail = "TableView.SelectCellWithIdx(int,bool) failed: " + ModUiMorePlaylistsExceptionDetails(error);
            }
        }

        MethodInfo callback = listController.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(value => value.Name == "Select" && value.GetParameters().Length == 2)
            .FirstOrDefault(value =>
            {
                ParameterInfo[] parameters = value.GetParameters();
                return parameters[0].ParameterType.IsInstanceOfType(table) && parameters[1].ParameterType == typeof(int);
            });
        if (callback == null)
        {
            detail = string.IsNullOrEmpty(detail)
                ? listController.GetType().FullName + ".Select(TableView,int) was unavailable"
                : detail + "; controller callback unavailable";
            return false;
        }
        try
        {
            callback.Invoke(listController, new object[] { table, row });
            detail = listController.GetType().FullName + ".Select(TableView," + row + ") invoked as the native BSML select-cell callback";
            return true;
        }
        catch (Exception error)
        {
            detail = "Controller Select callback failed: " + ModUiMorePlaylistsExceptionDetails(error);
            return false;
        }
    }

    static string ModUiMorePlaylistsExceptionDetails(Exception error)
    {
        Exception actual = error is TargetInvocationException invocation && invocation.InnerException != null
            ? invocation.InnerException : error;
        return actual == null ? "<unknown exception>" : actual.ToString();
    }

    static object ModUiMorePlaylistsFirstMember(object value, params string[] names)
    {
        foreach (string name in names)
        {
            object member = ModUiMorePlaylistsRead(value, name);
            if (member != null) return member;
        }
        return null;
    }

    static object ModUiMorePlaylistsFirstRow(object listController)
    {
        foreach (string name in new[] { "allPlaylists", "currentPlaylists" })
        {
            List<object> values = ModUiMorePlaylistsValues(ModUiMorePlaylistsRead(listController, name));
            if (values.Count > 0) return values[0];
        }
        return null;
    }

    static object ModUiMorePlaylistsSelectionDiagnostics(object selectedSource, object listController,
        object table, List<object> rows)
    {
        object all = ModUiMorePlaylistsFirstMember(listController, "allPlaylists");
        object current = ModUiMorePlaylistsFirstMember(listController, "currentPlaylists");
        return new
        {
            selectedSourceType = ModUiMorePlaylistsType(selectedSource),
            selectedSourceListType = ModUiMorePlaylistsType(ModUiMorePlaylistsRead(selectedSource, "ListViewController", "listViewController")),
            selectedSourceDetailType = ModUiMorePlaylistsType(ModUiMorePlaylistsRead(selectedSource, "DetailViewController", "detailViewController")),
            listControllerType = ModUiMorePlaylistsType(listController),
            tableType = ModUiMorePlaylistsType(table),
            tableRows = rows?.Count ?? 0,
            allPlaylistsCount = ModUiMorePlaylistsCount(all),
            currentPlaylistsCount = ModUiMorePlaylistsCount(current),
            rowEntryType = ModUiMorePlaylistsType(ModUiMorePlaylistsFirstRow(listController)),
            rowEntryAvailable = ModUiMorePlaylistsFirstRow(listController) != null,
            selectionCallback = table == null ? "<no-table>" :
                string.Join("|", table.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(method => method.Name == "DidSelectCellWithIdx" || method.Name == "SelectCellWithIdx")
                    .Select(method => method.ToString()).ToArray())
        };
    }

    static string ModUiMorePlaylistsDetailResource(object detailController)
    {
        return ModUiMorePlaylistsType(detailController).IndexOf("BeatSaver", StringComparison.OrdinalIgnoreCase) >= 0
            ? "MorePlaylists.BeatSaver.BeatSaverDetailView.bsml"
            : "MorePlaylists.UI.Views.BasicDetailView.bsml";
    }

    IEnumerator ModUiMorePlaylistsNativeEntrySelection(ModUiAuditRun run, object flow,
        object selectedSource, object listController, object detailController, object songController, Camera camera)
    {
        Dictionary<string, object> detailPage = ModUiMorePlaylistsPage(run, "More Playlists Playlist Detail",
            ModUiMorePlaylistsType(detailController), ModUiMorePlaylistsDetailResource(detailController));
        Dictionary<string, object> songPage = ModUiMorePlaylistsPage(run, "More Playlists Song List",
            ModUiMorePlaylistsType(songController), "MorePlaylists.UI.Views.MorePlaylistsSongListView.bsml");
        Component listView = ModUiMorePlaylistsView(listController) ?? listController as Component;
        Component detailView = ModUiMorePlaylistsView(detailController) ?? detailController as Component;
        Component songView = ModUiMorePlaylistsView(songController) ?? songController as Component;
        object tableData = ModUiMorePlaylistsRead(listController, "customListTableData");
        object table = ModUiMorePlaylistsRead(tableData, "TableView", "tableView");
        List<object> rows = ModUiMorePlaylistsRows(listController);
        yield return ModUiAuditWait(run, () => ReadBool(listController, "Loaded", false) ||
            ModUiMorePlaylistsRows(listController).Count > 0, 8, 20f);
        tableData = ModUiMorePlaylistsRead(listController, "customListTableData");
        table = ModUiMorePlaylistsRead(tableData, "TableView", "tableView");
        rows = ModUiMorePlaylistsRows(listController);
        detailPage["candidateCount"] = rows.Count;
        songPage["candidateCount"] = rows.Count;
        run.Entrances.Add(new
        {
            kind = "MorePlaylists.NativePlaylistSelectionDiagnostic",
            status = "observed",
            data = ModUiMorePlaylistsSelectionDiagnostics(selectedSource, listController, table, rows)
        });
        if (listController == null || table == null || rows.Count == 0)
        {
            string reason = rows.Count == 0
                ? "The live playlist list has no existing entry; no synthetic playlist was created."
                : "The live playlist list did not expose its native TableView.";
            ModUiMorePlaylistsBlock(run, detailPage, reason);
            ModUiMorePlaylistsBlock(run, songPage, reason);
            yield break;
        }

        bool selected;
        string selectionDetail;
        try
        {
            selected = ModUiMorePlaylistsInvokeNativeEntrySelect(listController, table, 0, out selectionDetail);
        }
        catch (Exception error)
        {
            selected = false;
            selectionDetail = "Native selection adapter threw: " + ModUiMorePlaylistsExceptionDetails(error);
        }
        run.Entrances.Add(new
        {
            kind = "MorePlaylists.NativePlaylistSelection",
            host = ModUiMorePlaylistsType(listController),
            row = 0,
            status = selected ? "invoked" : "blocked",
            detail = selectionDetail,
            networkPolicy = "read-only GET allowed; download/save/delete/upload callbacks are not invoked"
        });
        detailPage["action"] = selectionDetail;
        songPage["action"] = selectionDetail;
        if (!selected)
        {
            ModUiMorePlaylistsBlock(run, detailPage, "Existing playlist native selection failed: " + selectionDetail);
            ModUiMorePlaylistsBlock(run, songPage, "Existing playlist native selection failed: " + selectionDetail);
            yield break;
        }

        yield return ModUiAuditWait(run, () => ModUiAuditViewActive(detailView), 8, 15f);
        if (ModUiAuditViewActive(detailView))
        {
            yield return ModUiMorePlaylistsCapturePage(run, detailPage, detailView, camera,
                "more-playlists-native-detail", false, true);
        }
        else
        {
            ModUiMorePlaylistsBlock(run, detailPage,
                "Native playlist selection did not activate the detail ViewController.");
        }

        yield return ModUiAuditWait(run, () =>
        {
            bool loaded = ReadBool(songController, "Loaded", false);
            bool errored = ReadBool(songController, "HasError", false);
            return ModUiAuditViewActive(songView) && (loaded || errored || ModUiMorePlaylistsRows(songController).Count > 0);
        }, 8, 35f);
        int songRows = ModUiMorePlaylistsRows(songController).Count;
        bool songLoaded = ReadBool(songController, "Loaded", false);
        bool songError = ReadBool(songController, "HasError", false);
        songPage["loaded"] = songLoaded;
        songPage["rowCount"] = songRows;
        songPage["hasError"] = songError;
        if (ModUiAuditViewActive(songView))
        {
            yield return ModUiMorePlaylistsCapturePage(run, songPage, songView, camera,
                "more-playlists-native-song-list", false, true);
            if (songRows > 0)
            {
                songPage["dataStatus"] = "verified-read-only-get";
            }
            else
            {
                string errorText = ModUiAuditString(ModUiMorePlaylistsRead(songController, "ErrorText", "errorText"),
                    "IEntry.GetSongs returned no rows");
                songPage["status"] = "blocked";
                songPage["reason"] = "Read-only playlist song GET produced no rows: " + errorText;
                run.Block("MorePlaylists", ModUiAuditString(songPage["title"]), (string)songPage["reason"]);
            }
        }
        else
        {
            string reason = songError
                ? "Song-list GET reported an error before a live view became active."
                : "Native playlist selection did not activate MorePlaylistsSongListViewController; network/cache state was not claimed.";
            ModUiMorePlaylistsBlock(run, songPage, reason);
        }

        if (!ModUiMorePlaylistsInvokeNoArg(flow, "DismissDetailView", out string dismissDetail))
            run.Block("MorePlaylists", "More Playlists Playlist Detail", "Native DismissDetailView failed: " + dismissDetail);
        detailPage["dismissAction"] = dismissDetail;
        songPage["dismissAction"] = dismissDetail;
        yield return ModUiAuditWait(run, () => !ModUiAuditViewActive(detailView) && !ModUiAuditViewActive(songView), 8, 15f);
        if (ModUiAuditViewActive(detailView) || ModUiAuditViewActive(songView))
        {
            run.Block("MorePlaylists", "More Playlists Playlist Detail", "Native DismissDetailView left a child page active.");
        }
    }

    static bool ModUiMorePlaylistsInvokeSetRight(object flow, Component view, bool show, out string detail)
    {
        detail = string.Empty;
        if (flow == null)
        {
            detail = "flow is unavailable";
            return false;
        }
        MethodInfo method = flow.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(value => value.Name == "SetRightScreenViewController" && value.GetParameters().Length >= 1)
            .OrderBy(value => value.GetParameters().Length)
            .FirstOrDefault();
        if (method == null)
        {
            detail = "FlowCoordinator.SetRightScreenViewController(view,animation) was unavailable";
            return false;
        }
        ParameterInfo[] parameters = method.GetParameters();
        object[] arguments = new object[parameters.Length];
        bool assignedView = false;
        for (int index = 0; index < parameters.Length; index++)
        {
            ParameterInfo parameter = parameters[index];
            Type type = parameter.ParameterType;
            if (!assignedView && index == 0 && (!type.IsValueType || (view != null && type.IsInstanceOfType(view))))
            {
                arguments[index] = view;
                assignedView = true;
            }
            else if (parameter.HasDefaultValue)
            {
                arguments[index] = parameter.DefaultValue;
            }
            else if (type.IsEnum)
            {
                string name = show ? "In" : "Out";
                try { arguments[index] = Enum.Parse(type, name, true); }
                catch { arguments[index] = Enum.GetValues(type).GetValue(0); }
            }
            else if (!type.IsValueType || typeof(Delegate).IsAssignableFrom(type))
            {
                arguments[index] = null;
            }
            else
            {
                try { arguments[index] = Activator.CreateInstance(type); }
                catch { arguments[index] = null; }
            }
        }
        if (!assignedView)
        {
            detail = "SetRightScreenViewController first parameter was not a nullable ViewController slot";
            return false;
        }
        try
        {
            method.Invoke(flow, arguments);
            detail = flow.GetType().FullName + ".SetRightScreenViewController(" + (show ? "cachedSongList" : "null") + ") invoked";
            return true;
        }
        catch (Exception error)
        {
            detail = ModUiAuditException(error);
            return false;
        }
    }

    IEnumerator ModUiMorePlaylistsCachedSongList(ModUiAuditRun run, object flow,
        object songController, Camera camera)
    {
        Dictionary<string, object> page = ModUiMorePlaylistsPage(run, "More Playlists Cached Song List",
            ModUiMorePlaylistsType(songController), "MorePlaylists.UI.Views.MorePlaylistsSongListView.bsml");
        Component view = ModUiMorePlaylistsView(songController) ?? songController as Component;
        List<object> rows = ModUiMorePlaylistsRows(songController);
        object currentEntry = ModUiMorePlaylistsRead(songController, "currentEntry");
        page["cachedRows"] = rows.Count;
        page["cachedEntryType"] = ModUiMorePlaylistsEntryType(currentEntry);
        page["cacheStatus"] = rows.Count > 0 && currentEntry != null ? "existing-rendered-data" : "no-existing-rendered-data";
        if (view == null)
        {
            ModUiMorePlaylistsBlock(run, page, "MorePlaylistsSongListViewController is unavailable.");
            yield break;
        }
        if (rows.Count == 0 || currentEntry == null)
        {
            ModUiMorePlaylistsBlock(run, page,
                "No existing rendered song cache was available; SetCurrentPlaylist would call IEntry.GetSongs and was withheld.");
            yield break;
        }

        if (!ModUiMorePlaylistsInvokeSetRight(flow, view, true, out string showDetail))
        {
            ModUiMorePlaylistsBlock(run, page, "SetRightScreenViewController(songList,In) was unavailable: " + showDetail);
            yield break;
        }
        page["action"] = showDetail;
        yield return ModUiMorePlaylistsCapturePage(run, page, view, camera,
            "more-playlists-cached-song-list", false, true);
        ModUiMorePlaylistsInvokeSetRight(flow, null, false, out string hideDetail);
        page["hideAction"] = hideDetail;
    }

    static bool ModUiMorePlaylistsInvokePush(object flow, object navigation, Component view, out string detail)
    {
        detail = string.Empty;
        if (flow == null || navigation == null || view == null)
        {
            detail = "flow, navigation controller, or detail view is unavailable";
            return false;
        }
        MethodInfo method = flow.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(value => value.Name == "PushViewControllerToNavigationController" && value.GetParameters().Length >= 2)
            .OrderBy(value => value.GetParameters().Length)
            .FirstOrDefault(value =>
            {
                ParameterInfo[] parameters = value.GetParameters();
                return parameters.Any(parameter => parameter.ParameterType.IsInstanceOfType(navigation)) &&
                    parameters.Any(parameter => parameter.ParameterType.IsInstanceOfType(view));
            });
        if (method == null)
        {
            detail = "FlowCoordinator.PushViewControllerToNavigationController(nav,view,...) was unavailable";
            return false;
        }

        ParameterInfo[] parametersInfo = method.GetParameters();
        object[] arguments = new object[parametersInfo.Length];
        bool navAssigned = false;
        bool viewAssigned = false;
        for (int index = 0; index < parametersInfo.Length; index++)
        {
            ParameterInfo parameter = parametersInfo[index];
            if (!navAssigned && parameter.ParameterType.IsInstanceOfType(navigation))
            {
                arguments[index] = navigation;
                navAssigned = true;
            }
            else if (!viewAssigned && parameter.ParameterType.IsInstanceOfType(view))
            {
                arguments[index] = view;
                viewAssigned = true;
            }
            else if (parameter.HasDefaultValue) arguments[index] = parameter.DefaultValue;
            else if (parameter.ParameterType.IsEnum)
            {
                try { arguments[index] = Enum.GetValues(parameter.ParameterType).GetValue(0); }
                catch { arguments[index] = null; }
            }
            else if (!parameter.ParameterType.IsValueType || typeof(Delegate).IsAssignableFrom(parameter.ParameterType)) arguments[index] = null;
            else
            {
                try { arguments[index] = Activator.CreateInstance(parameter.ParameterType); }
                catch { arguments[index] = null; }
            }
        }
        if (!navAssigned || !viewAssigned)
        {
            detail = "PushViewControllerToNavigationController parameters were not assignable";
            return false;
        }
        try
        {
            method.Invoke(flow, arguments);
            detail = flow.GetType().FullName + ".PushViewControllerToNavigationController(cachedDetail) invoked";
            return true;
        }
        catch (Exception error)
        {
            detail = ModUiAuditException(error);
            return false;
        }
    }

    IEnumerator ModUiMorePlaylistsCachedDetail(ModUiAuditRun run, object flow,
        object listController, object detailController, object navigation, Camera camera)
    {
        Dictionary<string, object> page = ModUiMorePlaylistsPage(run, "More Playlists Cached Playlist Detail",
            ModUiMorePlaylistsType(detailController), "MorePlaylists.UI.Views.BasicDetailView.bsml");
        Component view = ModUiMorePlaylistsView(detailController) ?? detailController as Component;
        List<object> entries = ModUiMorePlaylistsValues(ModUiMorePlaylistsRead(listController, "currentPlaylists", "allPlaylists"));
        object entry = entries.FirstOrDefault();
        page["candidateCount"] = entries.Count;
        if (entry == null || detailController == null || navigation == null || view == null)
        {
            ModUiMorePlaylistsBlock(run, page, "No live playlist entry/detail/navigation controller was available.");
            yield break;
        }

        bool detailCached;
        string cacheMembers = ModUiMorePlaylistsCacheMembers(entry, out detailCached);
        page["cacheMembers"] = cacheMembers;
        if (!detailCached)
        {
            ModUiMorePlaylistsBlock(run, page,
                "No existing playlist detail cache was found on the live entry (" + cacheMembers + "); ShowDetail was withheld.");
            yield break;
        }

        if (!ModUiMorePlaylistsInvokeShowDetail(detailController, entry, out string showDetail))
        {
            ModUiMorePlaylistsBlock(run, page, "Cached detail ShowDetail failed: " + showDetail);
            yield break;
        }
        page["action"] = showDetail;
        if (!ModUiMorePlaylistsInvokePush(flow, navigation, view, out string pushDetail))
        {
            ModUiMorePlaylistsBlock(run, page, "Cached detail push failed: " + pushDetail);
            yield break;
        }
        page["pushAction"] = pushDetail;
        yield return ModUiMorePlaylistsCapturePage(run, page, view, camera,
            "more-playlists-cached-detail", false, true);

        if (!ModUiMorePlaylistsInvokeNoArg(flow, "DismissDetailView", out string dismissDetail))
            run.Block("MorePlaylists", ModUiAuditString(page["title"]), "DismissDetailView failed: " + dismissDetail);
        page["dismissAction"] = dismissDetail;
        yield return ModUiAuditWait(run, () => !ModUiAuditViewActive(view), 8, 8f);
        if (ModUiAuditViewActive(view))
            run.Block("MorePlaylists", ModUiAuditString(page["title"]), "Cached detail remained active after native DismissDetailView.");
    }

    IEnumerator ModUiMorePlaylistsRegisterControllers(ModUiAuditRun run)
    {
        var controllers = new[]
        {
            new { Type = "MorePlaylists.BeatSaver.BeatSaverListViewController", Resource = "MorePlaylists.UI.Views.BasicListView.bsml" },
            new { Type = "MorePlaylists.BeatSaver.BeatSaverFiltersViewController", Resource = "MorePlaylists.BeatSaver.BeatSaverFiltersView.bsml" },
            new { Type = "MorePlaylists.BeatSaver.BeatSaverDetailViewController", Resource = "MorePlaylists.BeatSaver.BeatSaverDetailView.bsml" },
            new { Type = "MorePlaylists.UI.BasicListViewController", Resource = "MorePlaylists.UI.Views.BasicListView.bsml" },
            new { Type = "MorePlaylists.UI.BasicDetailViewController", Resource = "MorePlaylists.UI.Views.BasicDetailView.bsml" },
            new { Type = "MorePlaylists.UI.MorePlaylistsSongListViewController", Resource = "MorePlaylists.UI.Views.MorePlaylistsSongListView.bsml" },
            new { Type = "MorePlaylists.UI.PopupModalsController", Resource = "MorePlaylists.UI.Views.PopupModals.bsml" },
            new { Type = "MorePlaylists.UI.SourceModalController", Resource = "MorePlaylists.UI.Views.SourceModal.bsml" }
        };
        foreach (var controller in controllers)
        {
            Type type = FindTrainingType(controller.Type);
            Component active = ModUiMorePlaylistsFindActive(controller.Type);
            run.Entrances.Add(new
            {
                kind = "MorePlaylists.lazy-ui-controller",
                type = controller.Type,
                resource = controller.Resource,
                loaded = type != null,
                active = active != null && active.gameObject.activeInHierarchy,
                status = active != null && active.gameObject.activeInHierarchy ? "active" : "blocked-lazy-or-inactive"
            });
        }
        yield return null;
    }

    IEnumerator ModUiAuditMorePlaylists(ModUiAuditRun run, object flow, Camera camera)
    {
        if (run == null) yield break;
        if (flow == null || ModUiMorePlaylistsType(flow).IndexOf("MorePlaylistsFlowCoordinator", StringComparison.OrdinalIgnoreCase) < 0)
        {
            run.Block("MorePlaylists", "adapter", "MorePlaylistsFlowCoordinator was unavailable for the plugin-specific adapter.");
            yield break;
        }

        yield return ModUiMorePlaylistsRegisterControllers(run);
        camera ??= FindTrainingMenuCamera();

        object sourceModal = ModUiMorePlaylistsRead(flow, "sourceModalController", "_sourceModalController");
        object selectedSource = ModUiMorePlaylistsRead(sourceModal, "SelectedSource", "selectedSource");
        object listController = ModUiMorePlaylistsRead(selectedSource, "ListViewController", "listViewController") ??
            ModUiMorePlaylistsFindActive("MorePlaylists.BeatSaver.BeatSaverListViewController");
        object detailController = ModUiMorePlaylistsRead(selectedSource, "DetailViewController", "detailViewController") ??
            ModUiMorePlaylistsFindActive("MorePlaylists.BeatSaver.BeatSaverDetailViewController");
        object popup = ModUiMorePlaylistsRead(flow, "popupModalsController", "_popupModalsController");
        object songList = ModUiMorePlaylistsRead(flow, "morePlaylistsSongListViewController", "_morePlaylistsSongListViewController") ??
            ModUiMorePlaylistsFindActive("MorePlaylists.UI.MorePlaylistsSongListViewController");

        Component listView = ModUiMorePlaylistsView(listController) ?? listController as Component;
        if (listView == null)
        {
            Dictionary<string, object> page = ModUiMorePlaylistsPage(run, "More Playlists List", ModUiMorePlaylistsType(listController), "MorePlaylists.UI.Views.BasicListView.bsml");
            ModUiMorePlaylistsBlock(run, page, "The selected source did not expose an active list ViewController.");
            yield break;
        }

        yield return ModUiAuditWait(run, () => ModUiAuditViewActive(listView), 8, 12f);
        Dictionary<string, object> listPage = ModUiMorePlaylistsPage(run, "More Playlists List",
            ModUiMorePlaylistsType(listController), "MorePlaylists.UI.Views.BasicListView.bsml");
        listPage["selectedSourceType"] = ModUiMorePlaylistsType(selectedSource);
        listPage["loaded"] = ReadBool(listController, "Loaded", false);
        listPage["rowCount"] = ModUiMorePlaylistsRows(listController).Count;
        yield return ModUiMorePlaylistsCapturePage(run, listPage, listView, camera,
            "more-playlists-list", false, true);

        yield return ModUiMorePlaylistsSourceModal(run, flow, listController, sourceModal, camera);
        yield return ModUiMorePlaylistsFilterView(run, listController, camera);
        yield return ModUiMorePlaylistsPopupModal(run, popup, listView, camera);
        yield return ModUiMorePlaylistsNativeEntrySelection(run, flow, selectedSource, listController, detailController, songList, camera);
    }
}
