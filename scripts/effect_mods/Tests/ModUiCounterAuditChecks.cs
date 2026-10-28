using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;

namespace EffectModsSmoke;

public sealed partial class Runner
{
    sealed class ModUiCounterCecilResult
    {
        internal bool Passed;
        internal string Detail = string.Empty;
        internal readonly List<string> GroupOrder = new();
        internal bool HudSemanticsValid;
    }

    static bool ModUiCounterIsCountersAssembly(Type type)
    {
        if (type == null) return false;
        string name = type.Assembly.GetName().Name ?? string.Empty;
        return name.Equals("Counters+", StringComparison.OrdinalIgnoreCase) ||
            name.IndexOf("CountersPlus", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("Counters+", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static object ModUiCounterFindActiveObject(Type type, bool requireActive = true)
    {
        if (type == null) return null;
        try
        {
            foreach (object value in Resources.FindObjectsOfTypeAll(type))
            {
                Component component = value as Component;
                if (component == null || !component.gameObject.scene.IsValid()) continue;
                if (!requireActive || component.gameObject.activeInHierarchy) return value;
            }
        }
        catch { }
        return null;
    }

    static object ModUiCounterFindFlow(object supplied)
    {
        if (supplied != null && ModUiCounterIsCountersAssembly(supplied.GetType())) return supplied;
        Type flowType = FindTrainingType("CountersPlus.UI.FlowCoordinators.CountersPlusSettingsFlowCoordinator");
        return ModUiCounterFindActiveObject(flowType) ?? supplied;
    }

    static object ModUiCounterFindSection()
    {
        Type type = FindTrainingType("CountersPlus.UI.ViewControllers.CountersPlusSettingSectionSelectionViewController");
        return ModUiCounterFindActiveObject(type);
    }

    static bool ModUiCounterReadActive(object value)
    {
        if (value == null) return false;
        try
        {
            Component component = value as Component;
            if (component == null || !component.gameObject.activeInHierarchy) return false;
            object activated = ReadMemberAny(value, "isActivated", "IsActivated");
            return activated == null || Convert.ToBoolean(activated, CultureInfo.InvariantCulture);
        }
        catch { return false; }
    }

    static bool ModUiCounterIsOwnedEditor(Component view)
    {
        if (view == null || !ModUiCounterIsCountersAssembly(view.GetType())) return false;
        string name = view.GetType().Name;
        return name.IndexOf("CountersPlusSettingSectionSelection", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("CountersPlusCounterEdit", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("CountersPlusHUDEdit", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("CountersPlusMainSettingsEdit", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("CountersPlusHUDList", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static Component[] ModUiCounterActiveOwnedEditors()
    {
        try
        {
            return Resources.FindObjectsOfTypeAll<HMUI.ViewController>()
                .Where(value => value != null && value.gameObject.scene.IsValid() &&
                    value.gameObject.activeInHierarchy && ModUiCounterIsOwnedEditor(value))
                .Cast<Component>()
                .Distinct()
                .ToArray();
        }
        catch { return Array.Empty<Component>(); }
    }

    static object ModUiCounterMember(object value, params string[] names)
    {
        foreach (string name in names)
        {
            object member = ReadMemberAny(value, name);
            if (member != null) return member;
        }
        return null;
    }

    static object ModUiCounterFindSegmentedControl(object section)
    {
        Type segmentedType = FindTrainingType("HMUI.SegmentedControl");
        if (segmentedType == null) return null;
        foreach (string name in new[] { "segmentedControl", "_segmentedControl", "sectionSelector", "_sectionSelector", "tabs", "_tabs" })
        {
            object value = ModUiCounterMember(section, name);
            if (value != null && segmentedType.IsInstanceOfType(value)) return value;
        }
        Component sectionComponent = section as Component;
        if (sectionComponent != null)
        {
            try
            {
                Component child = sectionComponent.GetComponentInChildren(segmentedType, true);
                if (child != null) return child;
            }
            catch { }
        }
        try
        {
            return Resources.FindObjectsOfTypeAll(segmentedType).OfType<Component>()
                .FirstOrDefault(value => value != null && value.gameObject.activeInHierarchy &&
                    (ModUiCounterIsCountersAssembly(value.GetType()) ||
                        value.transform.GetComponentsInParent<HMUI.ViewController>(true).Any(ModUiCounterIsOwnedEditor)));
        }
        catch { return null; }
    }

    static MethodInfo ModUiCounterMethod(object value, string name, params Type[] parameterTypes)
    {
        if (value == null) return null;
        try
        {
            return value.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.Name == name)
                .FirstOrDefault(method =>
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != parameterTypes.Length) return false;
                    for (int index = 0; index < parameters.Length; index++)
                        if (parameterTypes[index] != null && !parameters[index].ParameterType.IsAssignableFrom(parameterTypes[index])) return false;
                    return true;
                });
        }
        catch { return null; }
    }

    static bool ModUiCounterInvokeSection(object section, object segment, int index, out string detail)
    {
        detail = string.Empty;
        MethodInfo method = ModUiCounterMethod(section, "HandleCellSelectedEvent", segment?.GetType(), typeof(int));
        if (method == null)
        {
            detail = "HandleCellSelectedEvent(HMUI.SegmentedControl,int) is unavailable";
            return false;
        }
        try
        {
            method.Invoke(section, new object[] { segment, index });
            detail = method.ToString();
            return true;
        }
        catch (Exception error) { detail = ModUiAuditException(error); return false; }
    }

    static object ModUiCounterTableData(object tableList)
    {
        return ModUiCounterMember(tableList, "Data", "data");
    }

    static object ModUiCounterTableView(object tableList)
    {
        return ModUiCounterMember(tableList, "TableView", "tableView", "_tableView");
    }

    static int ModUiCounterDataCount(object data)
    {
        if (data is ICollection collection) return collection.Count;
        if (data is IEnumerable values)
        {
            int count = 0;
            foreach (object ignored in values) count++;
            return count;
        }
        return 0;
    }

    static object ModUiCounterDataAt(object data, int index)
    {
        if (data is IList list && index >= 0 && index < list.Count) return list[index];
        if (data is IEnumerable values) return values.Cast<object>().Skip(index).FirstOrDefault();
        return null;
    }

    static string ModUiCounterModelName(object model, int index)
    {
        return ModUiAuditString(
            GetMember(model, "DisplayName"), GetMember(model, "displayName"),
            GetMember(model, "Text"), GetMember(model, "text"),
            GetMember(model, "Subtext"), GetMember(model, "Name"),
            "model-" + index);
    }

    static bool ModUiCounterScrollTo(object tableView, int index, out string detail)
    {
        detail = string.Empty;
        if (tableView == null) { detail = "TableView is unavailable"; return false; }
        MethodInfo method = tableView.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(value => value.Name == "ScrollToCellWithIdx")
            .FirstOrDefault(value => value.GetParameters().Length == 3);
        if (method == null) { detail = "TableView.ScrollToCellWithIdx signature is unavailable"; return false; }
        ParameterInfo[] parameters = method.GetParameters();
        object position = parameters[1].ParameterType.IsEnum ? Enum.GetValues(parameters[1].ParameterType).GetValue(0) : Activator.CreateInstance(parameters[1].ParameterType);
        try
        {
            method.Invoke(tableView, new object[] { index, position, false });
            detail = method.ToString();
            return true;
        }
        catch (Exception error) { detail = ModUiAuditException(error); return false; }
    }

    static object ModUiCounterFindDataCell(object tableList, int modelIndex)
    {
        Type cellType = FindTrainingType("CountersPlus.UI.CountersPlusListTableCell");
        object cell = ModUiCounterDataAt(ModUiCounterTableData(tableList), modelIndex);
        if (cell == null || (cellType != null && !cellType.IsInstanceOfType(cell))) return null;
        object value = ModUiCounterMember(cell, "CellIdx", "cellIdx", "_cellIdx");
        try { if (value != null && Convert.ToInt32(value, CultureInfo.InvariantCulture) == modelIndex) return cell; }
        catch { }
        return null;
    }

    static bool ModUiCounterInvokeOption(object section, object tableView, object cell, out string detail)
    {
        detail = string.Empty;
        MethodInfo method = ModUiCounterMethod(section, "OptionSelected", tableView?.GetType(), cell?.GetType());
        if (method == null)
        {
            detail = "OptionSelected(HMUI.TableView,CountersPlusListTableCell) is unavailable";
            return false;
        }
        try
        {
            method.Invoke(section, new[] { tableView, cell });
            detail = method.ToString();
            return true;
        }
        catch (Exception error) { detail = ModUiAuditException(error); return false; }
    }

    static bool ModUiCounterInvokeCellSelected(object hudList, object tableView, int index, out string detail)
    {
        detail = string.Empty;
        MethodInfo method = ModUiCounterMethod(hudList, "CellSelected", tableView?.GetType(), typeof(int));
        if (method == null)
        {
            detail = "CountersPlusHUDListViewController.CellSelected is unavailable";
            return false;
        }
        try
        {
            method.Invoke(hudList, new object[] { tableView, index });
            detail = method.ToString();
            return true;
        }
        catch (Exception error) { detail = ModUiAuditException(error); return false; }
    }

    static bool ModUiCounterPop(object flow, out string detail)
    {
        detail = string.Empty;
        MethodInfo method = ModUiCounterMethod(flow, "PopFromMainScreen");
        if (method == null) { detail = "CountersPlusSettingsFlowCoordinator.PopFromMainScreen() is unavailable"; return false; }
        try { method.Invoke(flow, null); detail = method.ToString(); return true; }
        catch (Exception error) { detail = ModUiAuditException(error); return false; }
    }

    static bool ModUiCounterSetRightView(object flow, object view, out string detail)
    {
        detail = string.Empty;
        MethodInfo method = ModUiCounterMethod(flow, "SetRightViewController", view?.GetType());
        if (method == null) { detail = "CountersPlusSettingsFlowCoordinator.SetRightViewController(ViewController) is unavailable"; return false; }
        try { method.Invoke(flow, new[] { view }); detail = method.ToString(); return true; }
        catch (Exception error) { detail = ModUiAuditException(error); return false; }
    }

    static ModUiCounterCecilResult ModUiCounterCheckCecil()
    {
        ModUiCounterCecilResult result = new();
        Type sectionType = FindTrainingType("CountersPlus.UI.ViewControllers.CountersPlusSettingSectionSelectionViewController");
        if (sectionType == null) { result.Detail = "CountersPlus section-selection type is unavailable"; return result; }
        string assemblyPath = string.Empty;
        try { assemblyPath = sectionType.Assembly.Location; } catch { }
        if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath))
        {
            result.Detail = "CountersPlus assembly path is unavailable for Cecil: " + assemblyPath;
            return result;
        }
        Assembly cecilAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(value => string.Equals(value.GetName().Name, "Mono.Cecil", StringComparison.OrdinalIgnoreCase));
        try
        {
            if (cecilAssembly == null)
            {
                string root = Path.GetDirectoryName(Path.GetDirectoryName(Application.dataPath));
                string cecilPath = Path.Combine(root ?? string.Empty, "Libs", "Mono.Cecil.dll");
                if (File.Exists(cecilPath)) cecilAssembly = Assembly.LoadFrom(cecilPath);
            }
            Type assemblyDefinition = cecilAssembly?.GetType("Mono.Cecil.AssemblyDefinition");
            MethodInfo readAssembly = assemblyDefinition?.GetMethods(BindingFlags.Static | BindingFlags.Public)
                .Where(value => value.Name == "ReadAssembly")
                .FirstOrDefault(value => value.GetParameters().Length == 1 && value.GetParameters()[0].ParameterType == typeof(string));
            if (readAssembly == null) { result.Detail = "Mono.Cecil AssemblyDefinition.ReadAssembly(string) is unavailable"; return result; }
            object definition = readAssembly.Invoke(null, new object[] { assemblyPath });
            try
            {
                object module = GetMember(definition, "MainModule");
                IEnumerable types = GetMember(module, "Types") as IEnumerable;
                object section = types?.Cast<object>().FirstOrDefault(value =>
                    string.Equals(ModUiAuditString(GetMember(value, "FullName")), sectionType.FullName, StringComparison.Ordinal));
                if (section == null) { result.Detail = "Cecil could not find the section-selection type"; return result; }
                IEnumerable methods = GetMember(section, "Methods") as IEnumerable;
                object option = methods?.Cast<object>().FirstOrDefault(value => ModUiAuditString(GetMember(value, "Name")) == "OptionSelected");
                object handle = methods?.Cast<object>().FirstOrDefault(value => ModUiAuditString(GetMember(value, "Name")) == "HandleCellSelectedEvent");
                object installer = types?.Cast<object>().FirstOrDefault(value =>
                    string.Equals(ModUiAuditString(GetMember(value, "FullName")),
                        "CountersPlus.Installers.MenuUIInstaller", StringComparison.Ordinal));
                object installBindings = (GetMember(installer, "Methods") as IEnumerable)?.Cast<object>()
                    .FirstOrDefault(value => ModUiAuditString(GetMember(value, "Name")) == "InstallBindings");
                object hudListType = types?.Cast<object>().FirstOrDefault(value =>
                    string.Equals(ModUiAuditString(GetMember(value, "FullName")),
                        "CountersPlus.UI.ViewControllers.CountersPlusHUDListViewController", StringComparison.Ordinal));
                object hudRefresh = (GetMember(hudListType, "Methods") as IEnumerable)?.Cast<object>()
                    .FirstOrDefault(value => ModUiAuditString(GetMember(value, "Name")) == "RefreshData");
                object hudCellSelected = (GetMember(hudListType, "Methods") as IEnumerable)?.Cast<object>()
                    .FirstOrDefault(value => ModUiAuditString(GetMember(value, "Name")) == "CellSelected");
                string optionIl = string.Join("\n", ModUiCounterInstructions(option));
                string handleIl = string.Join("\n", ModUiCounterInstructions(handle));
                string hudRefreshIl = string.Join("\n", ModUiCounterInstructions(hudRefresh));
                string hudCellIl = string.Join("\n", ModUiCounterInstructions(hudCellSelected));
                bool optionValid = option != null && optionIl.IndexOf("CountersPlusListTableCell::get_CellIdx", StringComparison.Ordinal) >= 0 &&
                    optionIl.IndexOf("SettingsGroup::OnCellSelect", StringComparison.Ordinal) >= 0;
                bool handleValid = handle != null && handleIl.IndexOf("loadedSettingsGroups", StringComparison.Ordinal) >= 0 &&
                    handleIl.IndexOf("tableList", StringComparison.Ordinal) >= 0 &&
                    handleIl.IndexOf("SettingsGroup::OnDisable", StringComparison.Ordinal) >= 0 &&
                    handleIl.IndexOf("SettingsGroup::OnEnable", StringComparison.Ordinal) >= 0 &&
                    handleIl.IndexOf("selectedGroup", StringComparison.Ordinal) >= 0 &&
                    handleIl.IndexOf("ReloadData", StringComparison.Ordinal) >= 0 &&
                    handleIl.IndexOf("SelectCellWithIdx", StringComparison.Ordinal) >= 0;
                result.GroupOrder.AddRange(ModUiCounterCecilGroupOrder(installBindings));
                bool groupOrderValid = result.GroupOrder.Count >= 3 &&
                    result.GroupOrder[0].EndsWith("MainSettingsGroup", StringComparison.Ordinal) &&
                    result.GroupOrder[1].EndsWith("CountersSettingsGroup", StringComparison.Ordinal) &&
                    result.GroupOrder[2].EndsWith("HUDsSettingsGroup", StringComparison.Ordinal);
                bool hudRefreshValid = hudRefresh != null &&
                    hudRefreshIl.IndexOf("GetCanvasSettingsFromID", StringComparison.Ordinal) >= 0 &&
                    hudRefreshIl.IndexOf("get_OtherCanvasSettings", StringComparison.Ordinal) >= 0 &&
                    hudRefreshIl.IndexOf("ldc.i4.m1", StringComparison.Ordinal) >= 0 &&
                    hudRefreshIl.IndexOf("CustomCellInfo", StringComparison.Ordinal) >= 0 &&
                    hudRefreshIl.IndexOf("ReloadData", StringComparison.Ordinal) >= 0;
                bool hudCellValid = hudCellSelected != null &&
                    hudCellIl.IndexOf("set_SelectedCanvas", StringComparison.Ordinal) >= 0 &&
                    hudCellIl.IndexOf("ldc.i4.1", StringComparison.Ordinal) >= 0 &&
                    hudCellIl.IndexOf("sub", StringComparison.Ordinal) >= 0 &&
                    hudCellIl.IndexOf("ApplyCanvasForEditing", StringComparison.Ordinal) >= 0;
                result.HudSemanticsValid = hudRefreshValid && hudCellValid;
                result.Passed = optionValid && handleValid && groupOrderValid && result.HudSemanticsValid;
                result.Detail = "Cecil=" + cecilAssembly.GetName().Version + "; OptionSelected=" + optionValid +
                    "; HandleCellSelectedEvent=" + handleValid + "; groupOrder=" +
                    (result.GroupOrder.Count == 0 ? "<unresolved>" : string.Join(",", result.GroupOrder)) +
                    "; HUD.RefreshData=" + hudRefreshValid + "; HUD.CellSelected=" + hudCellValid;
            }
            finally
            {
                if (definition is IDisposable disposable) disposable.Dispose();
            }
        }
        catch (Exception error) { result.Detail = ModUiAuditException(error); }
        return result;
    }

    static IEnumerable<string> ModUiCounterCecilGroupOrder(object method)
    {
        foreach (string instruction in ModUiCounterInstructions(method))
        {
            const string marker = "BindSettingsGroup<";
            int start = instruction.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) continue;
            start += marker.Length;
            int end = instruction.IndexOf('>', start);
            if (end <= start) continue;
            string group = instruction.Substring(start, end - start).Trim();
            if (group.Length > 0) yield return group;
        }
    }

    static IEnumerable<string> ModUiCounterInstructions(object method)
    {
        object body = GetMember(method, "Body");
        if (body == null) yield break;
        IEnumerable instructions = GetMember(body, "Instructions") as IEnumerable;
        if (instructions == null) yield break;
        foreach (object instruction in instructions) yield return Convert.ToString(instruction, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    static Dictionary<string, object> ModUiCounterPage(string title, string kind, Component view, Camera camera)
    {
        return new Dictionary<string, object>
        {
            ["kind"] = kind,
            ["title"] = title,
            ["scope"] = "Counters+",
            ["host"] = view?.GetType().FullName,
            ["resource"] = ModUiAuditString(GetMember(view, "ResourceName")),
            ["status"] = "verified",
            ["camera"] = camera?.name
        };
    }

    IEnumerator ModUiCounterCaptureEditors(ModUiAuditRun run, string title, Camera camera, HashSet<int> seen)
    {
        Component[] views = ModUiCounterActiveOwnedEditors();
        if (views.Length == 0)
        {
            run.Block("Counters+", title, "No active Counters+ editor/list ViewController was found after native navigation.");
            yield break;
        }
        int captured = 0;
        foreach (Component view in views)
        {
            if (view == null || !seen.Add(view.GetInstanceID())) continue;
            captured++;
            Dictionary<string, object> page = ModUiCounterPage(title + "/" + view.GetType().Name, "CountersPlusEditor", view, camera);
            run.Pages.Add(page);
            ModUiAuditCheckVisibility(run, page, view, camera);
            ModUiAuditCapturePage(run, page, view, camera, "counters-" + ModUiAuditSafeSlug(title) + "-" + ModUiAuditSafeSlug(view.GetType().Name));
            ModUiAuditControls(run, page, view);
            yield return ModUiAuditScroll(run, page, view, camera, "counters-" + ModUiAuditSafeSlug(title));
            yield return ModUiAuditRealHoverHints(run, page, view, camera, "Counters+");
            yield return ModUiAuditModals(run, page, view, camera, "counters-" + ModUiAuditSafeSlug(title), "Counters+");
        }
        if (captured == 0)
            run.Block("Counters+", title, "Counters+ editor candidates were present but all were already seen; no page was captured for this action.");
    }

    IEnumerator ModUiCounterCaptureHudCanvases(ModUiAuditRun run, object flow, object hudList,
        Camera camera, HashSet<int> seen)
    {
        Component hudView = hudList as Component;
        if (hudView == null)
        {
            run.Block("Counters+", "HUDs", "CountersPlusHUDListViewController is not a Unity Component.");
            yield break;
        }
        Dictionary<string, object> listPage = ModUiCounterPage("HUD canvas list", "CountersPlusHUDList", hudView, camera);
        run.Pages.Add(listPage);
        ModUiAuditCapturePage(run, listPage, hudView, camera, "counters-hud-list");
        yield return ModUiAuditRealHoverHints(run, listPage, hudView, camera, "Counters+");
        yield return ModUiAuditModals(run, listPage, hudView, camera, "counters-hud-list", "Counters+");

        object isDeleting = ModUiCounterMember(hudList, "IsDeleting", "isDeleting");
        if (isDeleting is bool deleting && deleting)
        {
            run.Block("Counters+", "HUDs", "HUD list is in delete mode; editor navigation was withheld.");
            yield break;
        }
        object data = ModUiCounterMember(ModUiCounterMember(hudList, "data", "Data"), "Data", "data");
        object tableView = ModUiCounterTableView(ModUiCounterMember(hudList, "data", "Data"));
        int count = ModUiCounterDataCount(data);
        run.Entrances.Add(new
        {
            kind = "CountersPlus.ExistingHUDCanvases",
            count,
            status = count > 0 ? "observed" : "blocked",
            detail = "HUDList.RefreshData emits row 0 for CanvasUtility.GetCanvasSettingsFromID(-1) (Main), followed by OtherCanvasSettings rows."
        });
        if (count <= 0)
        {
            run.Block("Counters+", "HUDs", "HUDList.RefreshData exposed no existing canvas rows, including the required Main canvas row.");
            yield break;
        }
        int capturedCanvases = 0;
        for (int index = 0; index < count; index++)
        {
            string name = ModUiCounterModelName(ModUiCounterDataAt(data, index), index);
            if (!ModUiCounterScrollTo(tableView, index, out string scrollDetail))
            {
                run.Block("Counters+", name, "HUD list scroll failed: " + scrollDetail);
                continue;
            }
            yield return new WaitForSecondsRealtime(0.35f);
            if (!ModUiCounterInvokeCellSelected(hudList, tableView, index, out string selectDetail))
            {
                run.Block("Counters+", name, "HUD editor navigation failed: " + selectDetail);
                continue;
            }
            yield return new WaitForSecondsRealtime(0.6f);
            seen.Clear();
            int pagesBefore = run.Pages.Count;
            string canvasLabel = index == 0 ? "HUD canvas Main/" + name : "HUD canvas Other/" + name;
            yield return ModUiCounterCaptureEditors(run, canvasLabel, camera, seen);
            if (run.Pages.Count > pagesBefore) capturedCanvases++;
            yield return VerifyModUiReloadChecks();
            ModUiAuditCaptureTranslator(run, "after-language-reload-HUD-" + index);
            if (!ModUiCounterSetRightView(flow, hudList, out string restoreDetail))
                run.Block("Counters+", name, "HUD list restore failed: " + restoreDetail);
            yield return null;
        }
        if (capturedCanvases == 0)
            run.Block("Counters+", "HUDs", "Existing HUD rows were enumerated but no HUD edit page was captured.");
    }

    static string ModUiCounterGroupType(object group)
    {
        return group?.GetType().FullName ?? string.Empty;
    }

    static bool ModUiCounterGroupIs(object group, string suffix)
    {
        return ModUiCounterGroupType(group).EndsWith(suffix, StringComparison.Ordinal);
    }

    void ModUiCounterRecordUnvisitedModels(ModUiAuditRun run, object data, int start,
        string section, string reason)
    {
        int count = ModUiCounterDataCount(data);
        for (int index = Math.Max(0, start); index < count; index++)
        {
            run.Entrances.Add(new
            {
                kind = "CountersPlus.CounterModel",
                section,
                index,
                name = ModUiCounterModelName(ModUiCounterDataAt(data, index), index),
                status = "blocked",
                detail = reason
            });
        }
    }

    IEnumerator ModUiCounterAuditCounterGroup(ModUiAuditRun run, object flow, object section,
        Camera camera, HashSet<int> seenEditors)
    {
        object tableList = ModUiCounterMember(section, "tableList", "TableList");
        object data = ModUiCounterTableData(tableList);
        object tableView = ModUiCounterTableView(tableList);
        int modelCount = ModUiCounterDataCount(data);
        run.Entrances.Add(new
        {
            kind = "CountersPlus.CounterModels",
            group = ModUiCounterGroupType(ModUiCounterMember(section, "selectedGroup", "SelectedGroup")),
            count = modelCount,
            status = modelCount > 0 ? "observed" : "blocked"
        });
        if (modelCount <= 0)
        {
            run.Block("Counters+", "CounterModels", "CountersSettingsGroup exposed no ConfigModel rows; the counter loop was not treated as passed.");
            yield break;
        }
        int capturedModels = 0;
        for (int modelIndex = 0; modelIndex < modelCount; modelIndex++)
        {
            string modelName = ModUiCounterModelName(ModUiCounterDataAt(data, modelIndex), modelIndex);
            if (!ModUiCounterScrollTo(tableView, modelIndex, out string scrollDetail))
            {
                run.Block("Counters+", modelName, "Counter model scroll failed: " + scrollDetail);
                run.Entrances.Add(new { kind = "CountersPlus.CounterModel", section = "Counters", index = modelIndex, name = modelName, status = "blocked", detail = scrollDetail });
                continue;
            }
            yield return new WaitForSecondsRealtime(0.35f);
            object cell = ModUiCounterFindDataCell(tableList, modelIndex);
            if (cell == null)
            {
                string reason = "tableList.Data did not expose the expected CountersPlusListTableCell at this ConfigModel row after scrolling.";
                run.Block("Counters+", modelName, reason);
                run.Entrances.Add(new { kind = "CountersPlus.CounterModel", section = "Counters", index = modelIndex, name = modelName, status = "blocked", detail = reason });
                continue;
            }
            if (!ModUiCounterInvokeOption(section, tableView, cell, out string optionDetail))
            {
                run.Block("Counters+", modelName, optionDetail);
                run.Entrances.Add(new { kind = "CountersPlus.CounterModel", section = "Counters", index = modelIndex, name = modelName, status = "blocked", detail = optionDetail });
                continue;
            }
            run.Entrances.Add(new { kind = "CountersPlus.CounterModel", section = "Counters", index = modelIndex, name = modelName, status = "selected", detail = optionDetail });
            yield return new WaitForSecondsRealtime(0.6f);
            int pagesBefore = run.Pages.Count;
            seenEditors.Clear();
            yield return ModUiCounterCaptureEditors(run, modelName, camera, seenEditors);
            if (run.Pages.Count > pagesBefore) capturedModels++;
            if (!ModUiCounterPop(flow, out string popDetail))
            {
                run.Block("Counters+", modelName, popDetail);
                ModUiCounterRecordUnvisitedModels(run, data, modelIndex + 1, "Counters", "Counters+ editor could not return to the section list after this model.");
                yield break;
            }
            yield return new WaitForSecondsRealtime(0.35f);
        }
        if (capturedModels == 0)
            run.Block("Counters+", "CounterModels", "All ConfigModel rows were enumerated but no counter editor page was captured.");
    }

    IEnumerator ModUiCounterAuditHudGroup(ModUiAuditRun run, object flow, object section,
        Camera camera, HashSet<int> seenEditors)
    {
        object tableList = ModUiCounterMember(section, "tableList", "TableList");
        object data = ModUiCounterTableData(tableList);
        object tableView = ModUiCounterTableView(tableList);
        int count = ModUiCounterDataCount(data);
        run.Entrances.Add(new
        {
            kind = "CountersPlus.HUDModels",
            group = ModUiCounterGroupType(ModUiCounterMember(section, "selectedGroup", "SelectedGroup")),
            count,
            status = count >= 2 ? "observed" : "blocked"
        });
        for (int index = 0; index < count; index++)
        {
            string name = ModUiCounterModelName(ModUiCounterDataAt(data, index), index);
            string status = index == 1 ? "safe-action-candidate" : "skipped-protected-action";
            run.Entrances.Add(new { kind = "CountersPlus.HUDModel", index, name, status, detail = index == 1 ? "Edit existing Canvases is read-only to open; Add/remove rows are protected." : "Opening this row could create or remove user content." });
        }
        if (count < 2)
        {
            run.Block("Counters+", "HUDs", "HUDsSettingsGroup did not expose the protected Add-new and safe Edit-existing rows.");
            yield break;
        }
        object cell = ModUiCounterFindDataCell(tableList, 1);
        string optionDetail = string.Empty;
        bool optionSucceeded = cell != null && ModUiCounterInvokeOption(section, tableView, cell, out optionDetail);
        if (!optionSucceeded)
        {
            string reason = cell == null ? "tableList.Data did not expose CountersPlusListTableCell row 1 (Edit existing Canvases)." : optionDetail;
            run.Block("Counters+", "HUDs.EditExisting", reason);
            yield break;
        }
        run.Entrances.Add(new { kind = "CountersPlus.HUDs.EditExisting", model = ModUiCounterModelName(ModUiCounterDataAt(data, 1), 1), status = "selected", detail = optionDetail });
        yield return new WaitForSecondsRealtime(0.6f);
        object hudList = ModUiCounterFindActiveObject(FindTrainingType("CountersPlus.UI.ViewControllers.CountersPlusHUDListViewController"));
        if (hudList == null)
        {
            run.Block("Counters+", "HUDs", "Edit-existing row selected but CountersPlusHUDListViewController did not become active.");
        }
        else
        {
            int pagesBefore = run.Pages.Count;
            yield return ModUiCounterCaptureHudCanvases(run, flow, hudList, camera, seenEditors);
            if (run.Pages.Count <= pagesBefore + 1)
                run.Block("Counters+", "HUDs", "Existing HUD canvas traversal returned only the list page; no HUD edit page was captured.");
        }
        if (!ModUiCounterPop(flow, out string popDetail))
            run.Block("Counters+", "HUDs", popDetail);
        yield return new WaitForSecondsRealtime(0.35f);
    }

    IEnumerator ModUiCounterAuditMainGroup(ModUiAuditRun run, object flow, object section,
        Camera camera, HashSet<int> seenEditors)
    {
        int pagesBefore = run.Pages.Count;
        seenEditors.Clear();
        yield return ModUiCounterCaptureEditors(run, "Main settings", camera, seenEditors);
        if (run.Pages.Count == pagesBefore)
            run.Block("Counters+", "MainSettings", "MainSettingsGroup was selected but no MainSettingsEdit page was captured.");
        if (!ModUiCounterPop(flow, out string popDetail))
            run.Block("Counters+", "MainSettings", popDetail);
        yield return new WaitForSecondsRealtime(0.35f);
    }

    IEnumerator ModUiAuditBuiltInCounters(ModUiAuditRun run, object flow, Camera camera)
    {
        ModUiCounterCecilResult cecil = ModUiCounterCheckCecil();
        run.Entrances.Add(new
        {
            kind = "CountersPlus.Cecil",
            status = cecil.Passed ? "verified" : "blocked",
            detail = cecil.Detail,
            groupOrder = cecil.GroupOrder
        });
        if (!cecil.Passed)
        {
            run.Block("Counters+", "Cecil", cecil.Detail);
            yield break;
        }

        object counterFlow = ModUiCounterFindFlow(flow);
        object section = ModUiCounterFindSection();
        if (counterFlow == null || section == null)
        {
            run.Block("Counters+", "BuiltInSections", "CountersPlusSettingsFlowCoordinator or section-selection view is unavailable.");
            yield break;
        }
        yield return ModUiAuditWait(run, () => ModUiCounterReadActive(section), 6, 10f);
        Component sectionView = section as Component;
        Camera actualCamera = camera ?? FindTrainingMenuCamera();
        HashSet<int> seenEditors = new();
        if (sectionView != null)
        {
            Dictionary<string, object> page = ModUiCounterPage("Built-in counter sections", "CountersPlusSections", sectionView, actualCamera);
            run.Pages.Add(page);
            ModUiAuditCheckVisibility(run, page, sectionView, actualCamera);
            ModUiAuditCapturePage(run, page, sectionView, actualCamera, "counters-sections");
            yield return ModUiAuditRealHoverHints(run, page, sectionView, actualCamera, "Counters+");
            yield return ModUiAuditModals(run, page, sectionView, actualCamera, "counters-sections", "Counters+");
        }

        object groups = ModUiCounterMember(section, "loadedSettingsGroups", "LoadedSettingsGroups");
        List<object> groupValues = groups is IEnumerable values ? values.Cast<object>().Where(value => value != null).ToList() : new List<object>();
        int groupCount = groupValues.Count;
        object segment = ModUiCounterFindSegmentedControl(section);
        MethodInfo optionSelected = ModUiCounterMethod(section, "OptionSelected", FindTrainingType("HMUI.TableView"), FindTrainingType("CountersPlus.UI.CountersPlusListTableCell"));
        if (segment == null) run.Block("Counters+", "sections", "Native HMUI.SegmentedControl for sections was not found.");
        if (optionSelected == null) run.Block("Counters+", "OptionSelected", "Native OptionSelected signature was not found.");
        if (groupCount == 0)
        {
            run.Block("Counters+", "sections", "loadedSettingsGroups was empty; no section loop was treated as passed.");
            yield break;
        }

        for (int sectionIndex = 0; sectionIndex < groupCount; sectionIndex++)
        {
            if (segment == null) continue;
            if (!ModUiCounterInvokeSection(section, segment, sectionIndex, out string sectionDetail))
            {
                run.Block("Counters+", "section-" + sectionIndex, sectionDetail);
                continue;
            }
            yield return new WaitForSecondsRealtime(0.6f);
            section = ModUiCounterFindSection() ?? section;
            object selectedGroup = ModUiCounterMember(section, "selectedGroup", "SelectedGroup");
            string groupName = ModUiCounterGroupType(selectedGroup);
            string expectedGroup = sectionIndex < cecil.GroupOrder.Count ? cecil.GroupOrder[sectionIndex] : string.Empty;
            bool groupMatches = selectedGroup != null &&
                (string.IsNullOrEmpty(expectedGroup) || string.Equals(groupName, expectedGroup, StringComparison.Ordinal));
            run.Entrances.Add(new
            {
                kind = "CountersPlus.Section",
                index = sectionIndex,
                expectedGroup,
                group = groupName,
                status = groupMatches ? "selected" : "blocked",
                detail = sectionDetail
            });
            if (!groupMatches)
            {
                run.Block("Counters+", "section-" + sectionIndex,
                    "HandleCellSelectedEvent did not expose the Cecil-expected selectedGroup; expected=" + expectedGroup + "; actual=" + groupName);
                continue;
            }
            if (ModUiCounterGroupIs(selectedGroup, "CountersSettingsGroup"))
            {
                yield return ModUiCounterAuditCounterGroup(run, counterFlow, section, actualCamera, seenEditors);
            }
            else if (ModUiCounterGroupIs(selectedGroup, "HUDsSettingsGroup"))
            {
                yield return ModUiCounterAuditHudGroup(run, counterFlow, section, actualCamera, seenEditors);
            }
            else if (ModUiCounterGroupIs(selectedGroup, "MainSettingsGroup"))
            {
                yield return ModUiCounterAuditMainGroup(run, counterFlow, section, actualCamera, seenEditors);
            }
            else
            {
                run.Block("Counters+", "section-" + sectionIndex,
                    "Cecil-resolved group has no safe adapter: " + groupName);
            }
        }
    }
}
