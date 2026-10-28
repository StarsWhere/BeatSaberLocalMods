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
    static Component ModUiCameraFindActiveComponent(Type type)
    {
        if (type == null) return null;
        try
        {
            Component fallback = null;
            foreach (object value in Resources.FindObjectsOfTypeAll(type))
            {
                Component component = value as Component;
                if (component == null || !component.gameObject.scene.IsValid()) continue;
                if (component.gameObject.activeInHierarchy && ModUiAuditViewActive(component)) return component;
                if (fallback == null && component.gameObject.activeInHierarchy) fallback = component;
            }
            return fallback;
        }
        catch { return null; }
    }

    static Component ModUiCameraFindFieldOrActive(object owner, string member, string typeName)
    {
        Component component = GetMember(owner, member) as Component;
        return component != null ? component : ModUiCameraFindActiveComponent(FindTrainingType(typeName));
    }

    static MethodInfo ModUiCameraMethod(Type type, string name, params Type[] parameterTypes)
    {
        if (type == null) return null;
        try
        {
            return type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.Name == name)
                .FirstOrDefault(method =>
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != parameterTypes.Length) return false;
                    for (int index = 0; index < parameters.Length; index++)
                        if (parameterTypes[index] != null && parameters[index].ParameterType != parameterTypes[index]) return false;
                    return true;
                });
        }
        catch { return null; }
    }

    static string ModUiCameraMethodSignature(Type type, string name, params Type[] parameterTypes)
    {
        MethodInfo method = ModUiCameraMethod(type, name, parameterTypes);
        return method == null ? string.Empty : method.ToString();
    }

    static string ModUiCameraComponentResource(Component component)
    {
        return ModUiAuditString(
            GetMember(component, "ResourceName"),
            GetMember(component, "resourceName"),
            GetMember(component, "Resource"),
            component?.GetType().FullName);
    }

    static string ModUiCameraConfigFileName(object configInfo)
    {
        object file = GetMember(configInfo, "File");
        string name = ModUiAuditString(GetMember(file, "Name"), file);
        if (string.IsNullOrEmpty(name)) return string.Empty;
        try { return Path.GetFileName(name); }
        catch { return name; }
    }

    static MethodInfo ModUiCameraHsvMethod(Type type, string name, params Type[] runtimeArgumentTypes)
    {
        if (type == null) return null;
        try
        {
            return type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.Name == name)
                .FirstOrDefault(method =>
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != runtimeArgumentTypes.Length) return false;
                    for (int index = 0; index < parameters.Length; index++)
                    {
                        Type runtimeType = runtimeArgumentTypes[index];
                        if (runtimeType == null) continue;
                        Type declaredType = parameters[index].ParameterType;
                        if (!declaredType.IsAssignableFrom(runtimeType) && !runtimeType.IsAssignableFrom(declaredType)) return false;
                    }
                    return true;
                });
        }
        catch { return null; }
    }

    static string ModUiCameraHsvLoadedConfigText(object selector)
    {
        return ModUiAuditString(GetMember(selector, "LoadedConfigText"));
    }

    static bool ModUiCameraHsvLoadedConfigMatches(object selector, string configName, string fileName)
    {
        string loaded = ModUiCameraHsvLoadedConfigText(selector);
        if (string.IsNullOrEmpty(loaded) || string.Equals(loaded, "None", StringComparison.OrdinalIgnoreCase)) return false;
        string fileStem = string.Empty;
        try { fileStem = Path.GetFileNameWithoutExtension(fileName ?? string.Empty); }
        catch { }
        return (!string.IsNullOrEmpty(configName) && loaded.IndexOf(configName, StringComparison.OrdinalIgnoreCase) >= 0) ||
            (!string.IsNullOrEmpty(fileName) && loaded.IndexOf(fileName, StringComparison.OrdinalIgnoreCase) >= 0) ||
            (!string.IsNullOrEmpty(fileStem) && loaded.IndexOf(fileStem, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    static bool ModUiCameraConfigIsCompatible(object configInfo)
    {
        if (configInfo == null || GetMember(configInfo, "Config") == null) return false;
        object state = GetMember(configInfo, "State");
        if (state == null) return false;
        if (string.Equals(Convert.ToString(state, CultureInfo.InvariantCulture), "Compatible", StringComparison.OrdinalIgnoreCase))
            return true;
        try { return Convert.ToInt32(state, CultureInfo.InvariantCulture) == 4; }
        catch { return false; }
    }

    static List<object> ModUiCameraEnumerable(object value)
    {
        if (!(value is IEnumerable values)) return new List<object>();
        try { return values.Cast<object>().Where(item => item != null).ToList(); }
        catch { return new List<object>(); }
    }

    static Dictionary<string, object> ModUiCameraPage(string scope, string title, Component view)
    {
        return new Dictionary<string, object>
        {
            ["kind"] = "native-mod-page",
            ["scope"] = scope,
            ["title"] = title,
            ["host"] = view?.GetType().FullName,
            ["resource"] = ModUiCameraComponentResource(view),
            ["path"] = view == null ? string.Empty : ModUiAuditPath(view.transform),
            ["status"] = "blocked"
        };
    }

    IEnumerator ModUiCameraAuditPage(ModUiAuditRun run, Dictionary<string, object> page,
        Component view, Camera camera, string slug)
    {
        string scope = ModUiAuditString(page["scope"]);
        string title = ModUiAuditString(page["title"]);
        if (view == null)
        {
            page["reason"] = "The live ViewController/resource view was unavailable.";
            run.Block(scope, title, ModUiAuditString(page["reason"]));
            run.BlockedPages++;
            yield break;
        }

        yield return ModUiAuditWait(run, () => ModUiAuditViewActive(view), 6, 10f);
        if (!ModUiAuditViewActive(view))
        {
            page["reason"] = "The live ViewController did not become active after the native entry was opened.";
            run.Block(scope, title, ModUiAuditString(page["reason"]));
            run.BlockedPages++;
            yield break;
        }

        yield return ModUiAuditWaitLayout(view, 4, 4f);
        yield return new WaitForSecondsRealtime(0.6f);
        Canvas.ForceUpdateCanvases();
        page["active"] = true;
        page["status"] = "verified";
        run.StablePages++;
        if (view.GetType().FullName != "Camera2.UI.PreviewView") ModUiAuditCheckVisibility(run, page, view, camera);
        else page["content"] = "native-camera-preview-image";
        ModUiAuditCapturePage(run, page, view, camera, slug + "-top");
        ModUiAuditControls(run, page, view);
        yield return ModUiAuditTabs(run, page, view, camera, slug);
        yield return ModUiAuditScroll(run, page, view, camera, slug);
        yield return ModUiAuditRealHoverHints(run, page, view, camera, scope);
        yield return ModUiAuditModals(run, page, view, camera, slug, scope);
    }

    static Dictionary<string, object> ModUiCameraViewApi(Type type, string name, params Type[] parameterTypes)
    {
        return new Dictionary<string, object>
        {
            ["type"] = type?.FullName,
            ["assembly"] = type?.Assembly.GetName().Name,
            ["assemblyPath"] = type?.Assembly.Location,
            ["method"] = name,
            ["signature"] = ModUiCameraMethodSignature(type, name, parameterTypes)
        };
    }

    static int ModUiCameraVerifiedTabCount(Dictionary<string, object> page)
    {
        if (!page.TryGetValue("tabs", out object value) || !(value is IEnumerable tabs)) return 0;
        int count = 0;
        foreach (object tab in tabs)
        {
            object status = tab is IDictionary dictionary && dictionary.Contains("status")
                ? dictionary["status"] : GetMember(tab, "status");
            if (!string.Equals(ModUiAuditString(status), "verified", StringComparison.Ordinal)) continue;
            object cells = tab is IDictionary item && item.Contains("cells") ? item["cells"] : GetMember(tab, "cells");
            if (cells is IEnumerable values)
                count += values.Cast<object>().Count(value => String.Equals(ModUiAuditString(GetMember(value, "status")), "verified", StringComparison.Ordinal));
        }
        return count;
    }

    IEnumerator ModUiCameraAuditCamera2(ModUiAuditRun run, Dictionary<string, object> entry, Camera camera)
    {
        entry["settingsStatus"] = "pending";
        Type coordinatorType = FindTrainingType("Camera2.UI.Coordinator");
        Type settingsType = FindTrainingType("Camera2.UI.SettingsView");
        Type camListType = FindTrainingType("Camera2.UI.CamList");
        Type previewType = FindTrainingType("Camera2.UI.PreviewView");
        Type cam2Type = FindTrainingType("Camera2.Behaviours.Cam2");
        entry["coordinator"] = ModUiCameraViewApi(coordinatorType, "ShowSettingsForCam", cam2Type, typeof(bool));
        entry["settings"] = ModUiCameraViewApi(settingsType, "SetCam", cam2Type);
        entry["camList"] = ModUiCameraViewApi(camListType, "UnlockCamPosTab");
        entry["preview"] = ModUiCameraViewApi(previewType, "OnEnable");
        if (coordinatorType == null || settingsType == null || camListType == null)
        {
            string reason = "Camera2.UI.Coordinator, SettingsView, or CamList type is unavailable in the loaded assembly.";
            entry["reason"] = reason;
            run.Block("Camera2", "CamSettings", reason);
            yield break;
        }

        Component coordinator = GetStaticMember(coordinatorType, "instance") as Component;
        coordinator ??= ModUiCameraFindActiveComponent(coordinatorType);
        if (coordinator == null)
        {
            string reason = "Camera2.UI.Coordinator is not active in the current native flow.";
            entry["reason"] = reason;
            run.Block("Camera2", "CamSettings", reason);
            yield break;
        }

        entry["flow"] = coordinator.GetType().FullName;
        entry["flowPath"] = ModUiAuditPath(coordinator.transform);
        Camera actualCamera = camera ?? FindTrainingMenuCamera();
        HashSet<int> captured = new();
        Component camList = ModUiCameraFindFieldOrActive(coordinator, "camList", "Camera2.UI.CamList");
        Component settings = ModUiCameraFindFieldOrActive(coordinator, "settingsView", "Camera2.UI.SettingsView");
        Component preview = previewType == null ? null : ModUiCameraFindFieldOrActive(coordinator, "previewView", "Camera2.UI.PreviewView");

        if (camList != null && captured.Add(camList.GetInstanceID()))
        {
            Dictionary<string, object> page = ModUiCameraPage("Camera2", "Camera list", camList);
            run.Pages.Add(page);
            yield return ModUiCameraAuditPage(run, page, camList, actualCamera, "camera2-cam-list");
        }
        else if (camList == null)
        {
            run.Block("Camera2", "CamList", "The active Camera2 CamList resource view was not found.");
        }

        Type managerType = FindTrainingType("Camera2.Managers.CamManager");
        List<object> cameras = ModUiCameraEnumerable(GetMember(GetStaticMember(managerType, "cams"), "Values"));
        if (cameras.Count == 0)
        {
            cameras = cam2Type == null ? new List<object>() : Resources.FindObjectsOfTypeAll(cam2Type).Cast<object>().ToList();
        }
        object existingCamera = cameras.FirstOrDefault(value =>
            !string.IsNullOrEmpty(ModUiAuditString(GetMember(value, "configPath"), GetMember(value, "ConfigPath"))));
        entry["existingCameraCount"] = cameras.Count;
        entry["existingCameraNames"] = cameras.Select(value => ModUiAuditString(GetMember(value, "name"), GetMember(value, "Name"))).ToArray();
        entry["existingCamera"] = existingCamera == null ? null : new
        {
            name = ModUiAuditString(GetMember(existingCamera, "name"), GetMember(existingCamera, "Name")),
            configPath = ModUiAuditString(GetMember(existingCamera, "configPath"), GetMember(existingCamera, "ConfigPath"))
        };

        object currentCamera = GetStaticMember(settingsType, "cam");
        if (currentCamera == null)
        {
            if (existingCamera == null)
            {
                string reason = "No live Camera2.Behaviours.Cam2 with an existing configPath/settings was available; no camera was created or selected.";
                entry["settingsStatus"] = "blocked";
                run.Block("Camera2", "CamSettings", reason);
            }
            else
            {
                MethodInfo show = ModUiCameraMethod(coordinatorType, "ShowSettingsForCam", existingCamera.GetType(), typeof(bool));
                if (show == null)
                {
                    string reason = "Coordinator.ShowSettingsForCam(Cam2,bool) is unavailable; existing camera was left untouched.";
                    entry["settingsStatus"] = "blocked";
                    run.Block("Camera2", "CamSettings", reason);
                }
                else
                {
                    try
                    {
                        show.Invoke(coordinator, new object[] { existingCamera, false });
                        entry["selection"] = new
                        {
                            method = show.ToString(),
                            camera = ModUiAuditString(GetMember(existingCamera, "name"), GetMember(existingCamera, "Name")),
                            reselect = false,
                            saveAvoidedBecause = "SettingsView.cam was null before the first native selection"
                        };
                    }
                    catch (Exception error)
                    {
                        string reason = "ShowSettingsForCam(existing camera,false) failed: " + ModUiAuditException(error);
                        entry["settingsStatus"] = "blocked";
                        run.Block("Camera2", "CamSettings", reason);
                    }
                    yield return ModUiAuditWait(run, () => ModUiAuditViewActive(settings), 6, 8f);
                    yield return ModUiAuditWaitLayout(settings, 4, 3f);
                }
            }
        }
        else
        {
            entry["selection"] = new
            {
                method = "Coordinator.DidActivate/ShowSettingsForCam",
                camera = ModUiAuditString(GetMember(currentCamera, "name"), GetMember(currentCamera, "Name")),
                existing = true,
                saveAvoidedBecause = "The already-present native settings page was captured; no second camera selection was invoked"
            };
            if (string.IsNullOrEmpty(ModUiAuditString(GetMember(currentCamera, "configPath"), GetMember(currentCamera, "ConfigPath"))))
                run.Block("Camera2", "CamSettings", "The active Camera2 camera has no readable configPath; the page was captured but an existing persisted camera config could not be proven.");
        }

        settings = ModUiCameraFindFieldOrActive(coordinator, "settingsView", "Camera2.UI.SettingsView");
        if (settings != null)
            foreach (string tabField in new[] { "smoothfollowTab", "follow360Tab", "viewRectTab" })
            {
                object tab = GetMember(settings, tabField);
                tab?.GetType().GetProperty("IsVisible")?.SetValue(tab, true, null);
            }
        entry["inspectionTabs"] = "Temporarily expose conditional native tab UI; camera configuration values are unchanged";
        if (camList != null)
        {
            MethodInfo unlock = ModUiCameraMethod(camList.GetType(), "UnlockCamPosTab");
            if (unlock == null)
            {
                entry["displayTab"] = "blocked-method-missing";
                run.Block("Camera2", "CamSettings.Display", "CamList.UnlockCamPosTab() is unavailable; the ninth Display tab cannot be exposed safely.");
            }
            else
            {
                try
                {
                    unlock.Invoke(camList, null);
                    entry["displayTab"] = new { status = "exposed", method = unlock.ToString(), action = "navigation-only" };
                }
                catch (Exception error)
                {
                    entry["displayTab"] = "blocked-invoke";
                    run.Block("Camera2", "CamSettings.Display", "UnlockCamPosTab() failed: " + ModUiAuditException(error));
                }
            }
        }

        if (settings != null && captured.Add(settings.GetInstanceID()))
        {
            Dictionary<string, object> page = ModUiCameraPage("Camera2", "Camera settings (existing camera)", settings);
            page["expectedTabs"] = 9;
            page["cameraName"] = ModUiAuditString(GetMember(GetStaticMember(settingsType, "cam"), "name"));
            page["configPath"] = ModUiAuditString(GetMember(GetStaticMember(settingsType, "cam"), "configPath"));
            run.Pages.Add(page);
            yield return ModUiCameraAuditPage(run, page, settings, actualCamera, "camera2-cam-settings");
            page["verifiedTabCount"] = ModUiCameraVerifiedTabCount(page);
            if (ModUiCameraVerifiedTabCount(page) < 9)
                run.Block("Camera2", "CamSettings", "CamSettings did not expose and capture all nine native tabs after UnlockCamPosTab().");
            else entry["settingsStatus"] = "verified";
        }
        else
        {
            entry["settingsStatus"] = "blocked";
            run.Block("Camera2", "CamSettings", "The active Camera2 SettingsView was unavailable after native camera selection.");
        }

        preview = previewType == null ? null : ModUiCameraFindFieldOrActive(coordinator, "previewView", "Camera2.UI.PreviewView");
        if (preview != null && captured.Add(preview.GetInstanceID()))
        {
            Dictionary<string, object> page = ModUiCameraPage("Camera2", "Camera preview", preview);
            run.Pages.Add(page);
            yield return ModUiCameraAuditPage(run, page, preview, actualCamera, "camera2-cam-preview");
        }
        bool hasExistingConfig = existingCamera != null ||
            !string.IsNullOrEmpty(ModUiAuditString(GetMember(currentCamera, "configPath"), GetMember(currentCamera, "ConfigPath")));
        entry["status"] = ModUiAuditString(entry["settingsStatus"]) == "verified" && hasExistingConfig ? "verified" : "partial";
    }

    IEnumerator ModUiCameraAuditHitScoreVisualizer(ModUiAuditRun run, Dictionary<string, object> entry, Camera camera)
    {
        Type flowType = FindTrainingType("HitScoreVisualizer.UI.HsvMainFlowCoordinator");
        Type selectorType = FindTrainingType("HitScoreVisualizer.UI.ConfigSelectorViewController");
        Type settingsType = FindTrainingType("HitScoreVisualizer.UI.PluginSettingsViewController");
        Type previewType = FindTrainingType("HitScoreVisualizer.UI.ConfigPreviewViewController");
        Type tableViewType = FindTrainingType("HMUI.TableView");
        entry["flow"] = ModUiCameraViewApi(flowType, "DidActivate");
        entry["selector"] = ModUiCameraViewApi(selectorType, "ConfigSelected", tableViewType, typeof(object));
        entry["selection"] = ModUiCameraViewApi(selectorType, "PickConfig");
        entry["settings"] = ModUiCameraViewApi(settingsType, "DidActivate");
        entry["preview"] = ModUiCameraViewApi(previewType, "PreviewTabChanged", typeof(object), typeof(int));
        if (flowType == null || selectorType == null || settingsType == null || previewType == null)
        {
            string reason = "HitScoreVisualizer HsvMainFlowCoordinator or one of its three live view-controller types is unavailable.";
            entry["reason"] = reason;
            run.Block("HitScoreVisualizer", "ConfigFlow", reason);
            yield break;
        }

        Component flow = ModUiCameraFindActiveComponent(flowType);
        if (flow == null)
        {
            string reason = "HitScoreVisualizer.UI.HsvMainFlowCoordinator is not active in the current native flow.";
            entry["reason"] = reason;
            run.Block("HitScoreVisualizer", "ConfigFlow", reason);
            yield break;
        }

        Camera actualCamera = camera ?? FindTrainingMenuCamera();
        HashSet<int> captured = new();
        Component selector = ModUiCameraFindFieldOrActive(flow, "configSelectorViewController", "HitScoreVisualizer.UI.ConfigSelectorViewController");
        Component settings = ModUiCameraFindFieldOrActive(flow, "pluginSettingsViewController", "HitScoreVisualizer.UI.PluginSettingsViewController");
        Component preview = ModUiCameraFindFieldOrActive(flow, "configPreviewViewController", "HitScoreVisualizer.UI.ConfigPreviewViewController");
        if (selector == null)
        {
            run.Block("HitScoreVisualizer", "ConfigSelector", "The live ConfigSelectorViewController was not found in Resources or HsvMainFlowCoordinator.");
        }
        else if (captured.Add(selector.GetInstanceID()))
        {
            Dictionary<string, object> page = ModUiCameraPage("HitScoreVisualizer", "HSV config selector", selector);
            run.Pages.Add(page);
            yield return ModUiCameraAuditPage(run, page, selector, actualCamera, "hsv-config-selector");
        }

        object configsList = GetMember(selector, "configsList");
        if (selector != null)
        {
            yield return new WaitForSecondsRealtime(0.4f);
            yield return ModUiAuditWait(run, () => !ReadBool(selector, "LoadingConfigs", false) &&
                GetMember(GetMember(selector, "configsList"), "Data") != null, 4, 8f);
            configsList = GetMember(selector, "configsList");
        }
        List<object> configs = ModUiCameraEnumerable(GetMember(configsList, "Data"));
        object compatible = configs.FirstOrDefault(ModUiCameraConfigIsCompatible);
        entry["configCount"] = configs.Count;
        entry["configs"] = configs.Select(config => new
        {
            name = ModUiAuditString(GetMember(config, "ConfigName"), GetMember(config, "Name")),
            file = ModUiCameraConfigFileName(config),
            state = ModUiAuditString(GetMember(config, "State")),
            description = ModUiAuditString(GetMember(config, "Description"))
        }).ToArray();
        if (compatible == null)
        {
            string reason = configs.Count == 0
                ? "The live ConfigSelector list contained no existing config records."
                : "The live ConfigSelector list contained no compatible existing config; migration/invalid configs were not selected because selection could save or reset data.";
            entry["configSelection"] = "blocked";
            run.Block("HitScoreVisualizer", "ExistingConfig", reason);
        }
        else if (selector != null)
        {
            object tableView = GetMember(configsList, "TableView");
            MethodInfo selectedMethod = ModUiCameraHsvMethod(selector.GetType(), "ConfigSelected", tableView?.GetType(), typeof(object));
            if (tableView == null)
            {
                entry["configSelection"] = "blocked-list-view-missing";
                run.Block("HitScoreVisualizer", "ExistingConfig", "The live configs-list TableView was unavailable; no static ConfigInfo probe was treated as a selection.");
            }
            else if (selectedMethod == null)
            {
                entry["configSelection"] = "blocked-method-missing";
                run.Block("HitScoreVisualizer", "ExistingConfig", "ConfigSelected(HMUI.TableView,System.Object) was not available for the live list; runtime parameter assignability was checked.");
            }
            else
            {
                bool pickedExisting = false;
                string selectedConfigName = ModUiAuditString(GetMember(compatible, "ConfigName"), GetMember(compatible, "Name"));
                string selectedFileName = ModUiCameraConfigFileName(compatible);
                try
                {
                    selectedMethod.Invoke(selector, new[] { tableView, compatible });
                    entry["configSelection"] = new
                    {
                        status = "selected",
                        method = selectedMethod.ToString(),
                        declaredParameters = selectedMethod.GetParameters().Select(parameter => parameter.ParameterType.FullName).ToArray(),
                        configName = selectedConfigName,
                        fileName = selectedFileName,
                        state = ModUiAuditString(GetMember(compatible, "State")),
                        sideEffects = "ConfigSelected only sets the in-memory SelectedConfig; no create/delete/save/network method was called"
                    };
                    MethodInfo pick = ModUiCameraMethod(selector.GetType(), "PickConfig");
                    bool pickable = ReadBool(selector, "ConfigPickable", false);
                    if (pick == null || !pickable)
                    {
                        entry["configSelection"] = new
                        {
                            status = "selected-only",
                            method = selectedMethod.ToString(),
                            configName = selectedConfigName,
                            fileName = selectedFileName,
                            reason = pick == null ? "PickConfig() was unavailable." : "ConfigPickable was false after selecting the existing record."
                        };
                        run.Block("HitScoreVisualizer", "ExistingConfig.Load", "An existing config row was selected, but the native PickConfig path was not safely available.");
                    }
                    else
                    {
                        pick.Invoke(selector, null);
                        entry["configSelection"] = new
                        {
                            status = "loaded",
                            method = pick.ToString(),
                            configName = selectedConfigName,
                            fileName = selectedFileName,
                            state = "Compatible",
                            sideEffects = "PickConfig was invoked only for ConfigState.Compatible; the migration/save path was excluded"
                        };
                        pickedExisting = true;
                    }
                }
                catch (Exception error)
                {
                    string reason = "Selecting the existing compatible config failed: " + ModUiAuditException(error);
                    entry["configSelection"] = "blocked";
                    run.Block("HitScoreVisualizer", "ExistingConfig", reason);
                }
                if (pickedExisting)
                {
                    yield return new WaitForSecondsRealtime(0.35f);
                    bool loaded = false;
                    yield return ModUiAuditWait(run, () =>
                    {
                        loaded = ModUiCameraHsvLoadedConfigMatches(selector, selectedConfigName, selectedFileName);
                        return loaded && !ReadBool(selector, "LoadingConfigs", false);
                    }, 3, 8f);
                    yield return ModUiAuditWaitLayout(selector, 3, 2f);
                    if (!loaded)
                    {
                        entry["configSelection"] = new
                        {
                            status = "selected-only",
                            method = selectedMethod.ToString(),
                            configName = selectedConfigName,
                            fileName = selectedFileName,
                            reason = "PickConfig returned, but LoadedConfigText never confirmed the selected existing filename."
                        };
                        run.Block("HitScoreVisualizer", "ExistingConfig.Load", "The live selector did not confirm the compatible config as loaded; preview/settings were retained as partial coverage.");
                    }
                }
            }
        }

        settings = ModUiCameraFindFieldOrActive(flow, "pluginSettingsViewController", "HitScoreVisualizer.UI.PluginSettingsViewController");
        if (settings != null && captured.Add(settings.GetInstanceID()))
        {
            Dictionary<string, object> page = ModUiCameraPage("HitScoreVisualizer", "HSV plugin settings", settings);
            run.Pages.Add(page);
            yield return ModUiCameraAuditPage(run, page, settings, actualCamera, "hsv-plugin-settings");
        }
        else
        {
            run.Block("HitScoreVisualizer", "PluginSettings", "The live PluginSettingsViewController was unavailable after config selection.");
        }

        preview = ModUiCameraFindFieldOrActive(flow, "configPreviewViewController", "HitScoreVisualizer.UI.ConfigPreviewViewController");
        if (preview != null && captured.Add(preview.GetInstanceID()))
        {
            Dictionary<string, object> page = ModUiCameraPage("HitScoreVisualizer", "HSV config preview", preview);
            page["loadedConfigText"] = ModUiCameraHsvLoadedConfigText(selector);
            run.Pages.Add(page);
            yield return ModUiCameraAuditPage(run, page, preview, actualCamera, "hsv-config-preview");
        }
        else
        {
            run.Block("HitScoreVisualizer", "ConfigPreview", "The live ConfigPreviewViewController was unavailable after config selection.");
        }
        entry["status"] = string.Equals(ModUiAuditString(GetMember(entry["configSelection"], "status")), "loaded", StringComparison.Ordinal)
            ? "verified" : "partial";
    }

    IEnumerator ModUiAuditCameraAndScoreSettings(ModUiAuditRun run, string scope, Camera camera)
    {
        if (run == null || (scope != "Camera2" && scope != "HitScoreVisualizer")) yield break;

        Dictionary<string, object> entry = new()
        {
            ["kind"] = "deep-native-mod-ui",
            ["scope"] = scope,
            ["status"] = "blocked",
            ["navigation"] = "existing objects only; protected create/delete/save/download/upload/external actions are not invoked"
        };
        run.Entrances.Add(entry);
        if (scope == "Camera2")
            yield return ModUiCameraAuditCamera2(run, entry, camera);
        else
            yield return ModUiCameraAuditHitScoreVisualizer(run, entry, camera);
    }
}
