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
    const string ModUiConditionalAssetBundleResource = "AssetBundleLoadingTools.UI.Modals.bsml";
    const string ModUiConditionalHeckResource = "Heck.SettingsSetter.SettingsSetter.bsml";
    const string ModUiConditionalVivifyResource = "Vivify.Resources.AssetBundleDownloading.bsml";
    const string ModUiConditionalMirrorResource = "CustomAvatar.UI.Views.Mirror.bsml";
    const string ModUiConditionalCounterErrorResource = "CountersPlus.UI.BSML.Config.Error.bsml";

    static Dictionary<string, object> ModUiConditionalEntry(ModUiAuditRun run,
        string scope, string title, string host, string resource)
    {
        Dictionary<string, object> entry = new()
        {
            ["kind"] = "conditional-dialog",
            ["scope"] = scope,
            ["title"] = title,
            ["host"] = host,
            ["resource"] = resource,
            ["status"] = "blocked"
        };
        run.Entrances.Add(entry);
        return entry;
    }

    static void ModUiConditionalNotApplicable(Dictionary<string, object> entry, string reason)
    {
        entry["status"] = "not-applicable";
        entry["detail"] = reason;
    }

    static void ModUiConditionalBlocked(ModUiAuditRun run, Dictionary<string, object> entry,
        string scope, string title, string reason)
    {
        entry["status"] = "blocked";
        entry["detail"] = reason;
        run.Block(scope, title, reason);
    }

    static Component ModUiConditionalFindScene(string typeName, bool activeOnly)
    {
        Type type = FindTrainingType(typeName);
        if (type == null) return null;
        try
        {
            return Resources.FindObjectsOfTypeAll(type).OfType<Component>()
                .Where(value => value != null && value.gameObject.scene.IsValid() &&
                    (!activeOnly || value.gameObject.activeInHierarchy))
                .OrderByDescending(value => value.gameObject.activeInHierarchy)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    static Component ModUiConditionalFindActive(string typeName)
    {
        return ModUiConditionalFindScene(typeName, true);
    }

    static UnityEngine.UI.Button ModUiConditionalFindCancelButton(Component view)
    {
        if (view == null) return null;
        try
        {
            return view.GetComponentsInChildren<UnityEngine.UI.Button>(true)
                .FirstOrDefault(button => button != null && button.gameObject.activeInHierarchy && button.interactable);
        }
        catch { return null; }
    }

    static bool ModUiConditionalInvokeEnum(Component target, string methodName, out string detail)
    {
        detail = string.Empty;
        if (target == null)
        {
            detail = "real controller is unavailable";
            return false;
        }
        MethodInfo method = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(value => value.Name == methodName && value.GetParameters().Length == 1 &&
                value.GetParameters()[0].ParameterType.IsEnum);
        if (method == null)
        {
            detail = target.GetType().FullName + "." + methodName + "(enum) is unavailable";
            return false;
        }
        Type enumType = method.GetParameters()[0].ParameterType;
        object argument = null;
        try
        {
            argument = Enum.GetValues(enumType).Cast<object>()
                .FirstOrDefault(value => !string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), "None", StringComparison.OrdinalIgnoreCase));
            if (argument == null)
            {
                detail = enumType.FullName + " has no non-None calibration mode";
                return false;
            }
            method.Invoke(target, new[] { argument });
            detail = method + "(" + Convert.ToString(argument, CultureInfo.InvariantCulture) + ")";
            return true;
        }
        catch (Exception error)
        {
            detail = ModUiAuditException(error);
            return false;
        }
    }

    static Component ModUiConditionalFindActiveResource(string resource)
    {
        if (string.IsNullOrEmpty(resource)) return null;
        try
        {
            return Resources.FindObjectsOfTypeAll<HMUI.ViewController>()
                .Where(value => value != null && value.gameObject.scene.IsValid() && value.gameObject.activeInHierarchy)
                .Where(value => string.Equals(
                    ModUiAuditString(ReadMemberAny(value, "ResourceName", "resourceName", "Resource", "resource")),
                    resource, StringComparison.Ordinal))
                .Cast<Component>()
                .FirstOrDefault();
        }
        catch { return null; }
    }

    static bool ModUiConditionalInvoke(Component target, string methodName, out string detail)
    {
        detail = string.Empty;
        if (target == null)
        {
            detail = "real controller is unavailable";
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
            detail = method.ToString();
            return true;
        }
        catch (Exception error)
        {
            detail = ModUiAuditException(error);
            return false;
        }
    }

    static bool ModUiConditionalReadBool(object value, out bool result)
    {
        result = false;
        if (value == null) return false;
        try
        {
            result = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch { return false; }
    }

    static object ModUiConditionalAssetBundleConfig()
    {
        foreach (string typeName in new[]
        {
            "AssetBundleLoadingTools.Plugin",
            "AssetBundleLoadingTools.Config",
            "AssetBundleLoadingTools.Configuration.Config"
        })
        {
            Type type = FindTrainingType(typeName);
            object value = GetStaticMember(type, "Config") ?? GetStaticMember(type, "config") ??
                GetStaticMember(type, "Instance") ?? GetStaticMember(type, "instance");
            if (value != null) return value;
        }
        return null;
    }

    static Component ModUiConditionalAssetBundleModal(object controller)
    {
        return ReadMemberAny(controller, "multiPassModal", "MultiPassModal", "_multiPassModal") as Component;
    }

    IEnumerator ModUiConditionalCapturePage(ModUiAuditRun run, string scope, string title,
        string resource, Component view, Camera camera, string slug)
    {
        Dictionary<string, object> page = new()
        {
            ["kind"] = "ConditionalDialogPage",
            ["title"] = title,
            ["scope"] = scope,
            ["host"] = view?.GetType().FullName,
            ["resource"] = resource,
            ["status"] = "blocked"
        };
        run.Pages.Add(page);
        if (view == null)
        {
            page["reason"] = "The real conditional-dialog component was unavailable.";
            run.Block(scope, title, (string)page["reason"]);
            run.BlockedPages++;
            yield break;
        }

        yield return ModUiAuditWaitLayout(view, 4, 4f);
        camera ??= FindTrainingMenuCamera();
        page["camera"] = camera?.name;
        bool active = view.gameObject.activeInHierarchy;
        if (view is HMUI.ViewController) active = active && ModUiAuditViewActive(view);
        page["active"] = active;
        if (!active)
        {
            page["reason"] = "The real conditional-dialog component did not become active.";
            run.Block(scope, title, (string)page["reason"]);
            run.BlockedPages++;
            yield break;
        }

        page["status"] = "verified";
        run.StablePages++;
        ModUiAuditCheckVisibility(run, page, view, camera);
        ModUiAuditCapturePage(run, page, view, camera, slug + "-top");
        ModUiAuditControls(run, page, view);
        yield return ModUiAuditScroll(run, page, view, camera, slug);
        yield return ModUiAuditRealHoverHints(run, page, view, camera, scope);
    }

    IEnumerator ModUiConditionalAssetBundleDialog(ModUiAuditRun run, Camera camera)
    {
        const string scope = "AssetBundleLoadingTools";
        const string title = "Asset bundle multi-pass compatibility dialog";
        Type controllerType = FindTrainingType("AssetBundleLoadingTools.UI.ModalsController");
        Dictionary<string, object> entry = ModUiConditionalEntry(run, scope, title,
            controllerType?.FullName, ModUiConditionalAssetBundleResource);
        if (controllerType == null)
        {
            ModUiConditionalBlocked(run, entry, scope, title,
                "AssetBundleLoadingTools.UI.ModalsController is not loaded; the installed dialog cannot be inspected.");
            yield break;
        }

        object controller = GetStaticMember(controllerType, "Instance") ??
            GetStaticMember(controllerType, "instance") ?? ModUiConditionalFindActive(controllerType.FullName);
        Component modal = ModUiConditionalAssetBundleModal(controller);
        entry["controller"] = controller?.GetType().FullName;
        entry["modal"] = modal?.GetType().FullName;
        if (controller == null || modal == null)
        {
            ModUiConditionalNotApplicable(entry,
                controller == null
                    ? "The real ModalsController singleton is not initialized in the settings scene; no model fault was manufactured."
                    : "The real ModalsController has no multiPassModal instance in this scene.");
            yield break;
        }

        bool wasActive = modal.gameObject.activeInHierarchy;
        object config = ModUiConditionalAssetBundleConfig();
        object showFlagValue = ReadMemberAny(config, "ShowMultiPassModal", "showMultiPassModal");
        bool showFlag;
        bool hasShowFlag = ModUiConditionalReadBool(showFlagValue, out showFlag);
        entry["showMultiPassModal"] = hasShowFlag ? showFlag : (object)null;
        if (!wasActive && (!hasShowFlag || !showFlag))
        {
            ModUiConditionalNotApplicable(entry,
                hasShowFlag
                    ? "ShowMultiPassModal=false; ShowMultiPassModalAsync would intentionally return, and no model fault was manufactured."
                    : "The live config did not expose ShowMultiPassModal; the native show guard was not bypassed.");
            yield break;
        }

        MethodInfo show = controllerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(value => value.Name == "ShowMultiPassModalAsync" && value.GetParameters().Length == 0);
        if (!wasActive && show == null)
        {
            ModUiConditionalBlocked(run, entry, scope, title,
                "The real ModalsController.ShowMultiPassModalAsync() signature is unavailable.");
            yield break;
        }

        entry["trigger"] = wasActive ? "observed-active-modal" : "synthetic-template";
        entry["syntheticTemplate"] = !wasActive;
        entry["faultObserved"] = false;
        if (!wasActive)
        {
            try { show.Invoke(controller, null); }
            catch (Exception error)
            {
                ModUiConditionalBlocked(run, entry, scope, title,
                    "Native ShowMultiPassModalAsync template probe failed: " + ModUiAuditException(error));
                yield break;
            }
        }

        yield return ModUiAuditWait(run, () => modal.gameObject.activeInHierarchy, 6, 8f);
        if (!modal.gameObject.activeInHierarchy)
        {
            ModUiConditionalBlocked(run, entry, scope, title,
                "The real ShowMultiPassModalAsync call did not activate multiPassModal; no fault was asserted.");
            yield break;
        }

        Dictionary<string, object> modalRecord = new()
        {
            ["kind"] = "conditional-dialog-modal",
            ["scope"] = scope,
            ["page"] = title,
            ["resource"] = ModUiConditionalAssetBundleResource,
            ["host"] = controllerType.FullName,
            ["path"] = ModUiAuditPath(modal.transform),
            ["status"] = "verified",
            ["syntheticTemplate"] = !wasActive,
            ["faultObserved"] = false
        };
        run.Modals.Add(modalRecord);
        yield return ModUiConditionalCapturePage(run, scope, title, ModUiConditionalAssetBundleResource,
            modal, camera, "conditional-asset-bundle");
        run.ShownModals.Add(modal.GetInstanceID());

        if (!ModUiConditionalInvoke(controller as Component, "NoButtonClicked", out string cancelDetail))
        {
            if (!ModUiAuditInvokeModal(modal, "Hide", out string hideSignature, out string hideError))
            {
                modalRecord["status"] = "blocked";
                modalRecord["cancelReason"] = cancelDetail + "; Hide=" + hideError;
                ModUiConditionalBlocked(run, entry, scope, title,
                    "Real NoButtonClicked cancellation failed: " + cancelDetail + "; HMUI.ModalView.Hide failed: " + hideError);
                yield break;
            }
            modalRecord["cancel"] = "HMUI.ModalView.Hide fallback: " + hideSignature;
        }
        else modalRecord["cancel"] = "NoButtonClicked: " + cancelDetail;
        yield return ModUiAuditWait(run, () => !modal.gameObject.activeInHierarchy, 6, 8f);
        bool hidden = !modal.gameObject.activeInHierarchy;
        modalRecord["restored"] = hidden;
        entry["cancel"] = cancelDetail;
        entry["restored"] = hidden;
        if (!hidden)
            ModUiConditionalBlocked(run, entry, scope, title,
                "The real NoButtonClicked/Hide cancellation left multiPassModal active.");
        else entry["status"] = "verified";
    }

    IEnumerator ModUiConditionalControllerDialog(ModUiAuditRun run, Camera camera,
        string scope, string title, string resource, string typeName, string cancelMethod,
        string unavailableReason)
    {
        Type type = FindTrainingType(typeName);
        Dictionary<string, object> entry = ModUiConditionalEntry(run, scope, title, typeName, resource);
        if (typeName.IndexOf("Heck.SettingsSetter", StringComparison.Ordinal) >= 0 ||
            typeName.IndexOf("Vivify.Controllers.AssetBundleDownload", StringComparison.Ordinal) >= 0)
        {
            entry["nativeEntry"] = "Heck.PlayView.PlayViewManager.Init(StartStandardLevelParameters) -> PresentViewController";
            entry["nativeEntrySafety"] = "withheld: Init requires real play parameters; Vivify may start network download and Heck may alter gameplay parameters";
        }
        if (type == null)
        {
            ModUiConditionalBlocked(run, entry, scope, title,
                typeName + " is not loaded; the installed controller cannot be inspected.");
            yield break;
        }

        Component view = ModUiConditionalFindActive(typeName);
        if (view == null)
        {
            Component inactive = ModUiConditionalFindScene(typeName, false);
            entry["instance"] = inactive?.GetType().FullName;
            entry["instanceActive"] = inactive?.gameObject.activeInHierarchy;
            ModUiConditionalNotApplicable(entry, inactive == null
                ? unavailableReason
                : unavailableReason + " A real controller instance exists but is inactive; its only native presentation path is play-flow Init, which was withheld.");
            yield break;
        }
        entry["instance"] = view.GetType().FullName;
        yield return ModUiConditionalCapturePage(run, scope, title, resource, view, camera,
            "conditional-" + ModUiAuditSafeSlug(scope) + "-" + ModUiAuditSafeSlug(title));

        if (!ModUiConditionalInvoke(view, cancelMethod, out string cancelDetail))
        {
            ModUiConditionalBlocked(run, entry, scope, title,
                "The real cancellation action " + cancelMethod + "() failed: " + cancelDetail);
            yield break;
        }
        entry["cancel"] = cancelMethod + ": " + cancelDetail;
        entry["status"] = "verified";
        yield return null;
    }

    IEnumerator ModUiConditionalMirrorDialog(ModUiAuditRun run, Camera camera, bool allowNativeCalibrationStart = false)
    {
        const string scope = "CustomAvatar";
        const string title = "Custom Avatar mirror calibration";
        const string typeName = "CustomAvatar.UI.MirrorViewController";
        Dictionary<string, object> entry = ModUiConditionalEntry(run, scope, title, typeName,
            ModUiConditionalMirrorResource);
        Type type = FindTrainingType(typeName);
        if (type == null)
        {
            ModUiConditionalBlocked(run, entry, scope, title,
                typeName + " is not loaded; the installed Mirror view cannot be inspected.");
            yield break;
        }
        Component view = ModUiConditionalFindActive(typeName);
        if (view == null)
        {
            ModUiConditionalNotApplicable(entry,
                "The real Mirror view requires an active CustomAvatar avatar flow; no flow was opened just to manufacture calibration UI.");
            yield break;
        }
        entry["instance"] = view.GetType().FullName;
        UnityEngine.UI.Button cancelButton = ModUiConditionalFindCancelButton(view);
        bool hasCancelButton = cancelButton != null;
        if (!hasCancelButton && allowNativeCalibrationStart)
        {
            Component trackingRig = ReadMemberAny(view, "_trackingRig", "trackingRig") as Component ??
                ModUiConditionalFindActive("CustomAvatar.Tracking.TrackingRig");
            entry["calibrationStartHost"] = trackingRig?.GetType().FullName;
            string startDetail = string.Empty;
            bool started = trackingRig != null && ModUiConditionalInvokeEnum(trackingRig, "BeginCalibration", out startDetail);
            if (!started)
            {
                ModUiConditionalNotApplicable(entry,
                    "The real Mirror view has no Cancel button and TrackingRig.BeginCalibration could not be safely reached: " +
                    (trackingRig == null ? "TrackingRig instance is unavailable." : startDetail));
                yield break;
            }
            entry["trigger"] = "synthetic-template";
            entry["syntheticTemplate"] = true;
            entry["faultObserved"] = false;
            entry["runtimeOnly"] = true;
            entry["configWritten"] = false;
            entry["calibrationStart"] = "TrackingRig.BeginCalibration: " + startDetail;
            yield return ModUiAuditWait(run, () => ModUiConditionalFindCancelButton(view) != null, 6, 5f);
            cancelButton = ModUiConditionalFindCancelButton(view);
            hasCancelButton = cancelButton != null;
            if (!hasCancelButton)
            {
                // BeginCalibration only changes transient TrackingRig state.  Always
                // restore it when the view failed to expose its native Cancel button.
                ModUiConditionalInvoke(view, "OnCancelButtonClicked", out string cleanupDetail);
                ModUiConditionalBlocked(run, entry, scope, title,
                    "TrackingRig.BeginCalibration returned but the real Mirror Cancel button did not appear; cleanup=" + cleanupDetail);
                yield break;
            }
        }
        entry["cancelButtonText"] = cancelButton == null
            ? string.Empty
            : string.Join(" ", cancelButton.GetComponentsInChildren<TMPro.TMP_Text>(true)
                .Select(text => text?.text ?? string.Empty));
        if (!hasCancelButton)
        {
            ModUiConditionalNotApplicable(entry,
                "MirrorViewController is active but its real Cancel button is not visible; no native calibration start was requested in this focus.");
            yield break;
        }
        yield return ModUiConditionalCapturePage(run, scope, title, ModUiConditionalMirrorResource,
            view, camera, "conditional-custom-avatar-mirror");
        if (!ModUiConditionalInvoke(view, "OnCancelButtonClicked", out string cancelDetail))
        {
            ModUiConditionalBlocked(run, entry, scope, title,
                "The real Mirror Cancel action failed: " + cancelDetail);
            yield break;
        }
        entry["cancel"] = "OnCancelButtonClicked: " + cancelDetail;
        entry["status"] = "verified";
        entry["cancelButtonAfter"] = ModUiConditionalFindCancelButton(view) == null ? "hidden" : "still-visible";
        yield return null;
    }

    IEnumerator ModUiConditionalCountersError(ModUiAuditRun run, Camera camera)
    {
        const string scope = "Counters+";
        const string title = "Counters+ Config.Error";
        const string typeName = "CountersPlus.UI.ViewControllers.CountersPlusSettingSectionSelectionViewController";
        Dictionary<string, object> entry = ModUiConditionalEntry(run, scope, title, typeName,
            ModUiConditionalCounterErrorResource);
        Type sectionType = FindTrainingType(typeName);
        if (sectionType == null)
        {
            ModUiConditionalBlocked(run, entry, scope, title,
                typeName + " is not loaded; the native Config.Error owner cannot be inspected.");
            yield break;
        }

        Component errorView = ModUiConditionalFindActiveResource(ModUiConditionalCounterErrorResource);
        if (errorView != null)
        {
            entry["trigger"] = "observed-active-error-page";
            yield return ModUiConditionalCapturePage(run, scope, title, ModUiConditionalCounterErrorResource,
                errorView, camera, "conditional-counters-config-error");
            object counterFlow = ModUiCounterFindFlow(null);
            if (counterFlow != null && ModUiCounterPop(counterFlow, out string popDetail))
            {
                entry["cancel"] = "CountersPlusSettingsFlowCoordinator.PopFromMainScreen: " + popDetail;
                entry["status"] = "verified";
            }
            else
            {
                string detail = "Config.Error was captured, but the real Counters+ flow could not pop the error page safely.";
                ModUiConditionalBlocked(run, entry, scope, title, detail);
            }
            yield break;
        }

        object section = ModUiCounterFindSection();
        List<object> groups = new();
        if (section != null)
        {
            object values = ModUiCounterMember(section, "loadedSettingsGroups", "LoadedSettingsGroups");
            if (values is IEnumerable enumerable) groups = enumerable.Cast<object>().Where(value => value != null).ToList();
        }
        string groupDiagnostic = groups.Count == 0
            ? "loadedSettingsGroups was unavailable or empty"
            : string.Join(";", groups.Select(value => value.GetType().FullName));
        entry["groups"] = groupDiagnostic;
        ModUiConditionalNotApplicable(entry,
            "Config.Error is a failure page instantiated by a real counter SettingsGroup.OnCellSelect; no active error page exists (" +
            groupDiagnostic + "). Invalid counter data was not manufactured to force it.");
        yield break;
    }

    IEnumerator ModUiAuditConditionalAvatarFlow(ModUiAuditRun run, object flow, Camera camera)
    {
        if (run == null) yield break;
        string flowType = flow?.GetType().FullName ?? string.Empty;
        if (!string.Equals(flowType, "CustomAvatar.UI.AvatarMenuFlowCoordinator", StringComparison.Ordinal))
        {
            yield break;
        }

        // AvatarMenuFlowCoordinator provides MirrorViewController as its initial
        // center view.  BeginCalibration is a real transient TrackingRig action;
        // its paired native Cancel action restores the rig without saving data.
        yield return ModUiConditionalMirrorDialog(run, camera, true);
    }

    IEnumerator ModUiAuditConditionalDialogs(ModUiAuditRun run)
    {
        if (run == null) yield break;
        Camera camera = FindTrainingMenuCamera();
        yield return ModUiConditionalAssetBundleDialog(run, camera);
        yield return ModUiConditionalControllerDialog(run, camera, "Heck", "Heck SettingsSetter",
            ModUiConditionalHeckResource, "Heck.SettingsSetter.SettingsSetterViewController", "OnDeclineClick",
            "The real Heck SettingsSetter appears only for a beatmap carrying an _settings payload; Init was withheld to avoid fabricating gameplay parameters.");
        yield return ModUiConditionalControllerDialog(run, camera, "Vivify", "Vivify asset bundle download dialog",
            ModUiConditionalVivifyResource, "Vivify.Controllers.AssetBundleDownloadViewController", "OnEarlyDismiss",
            "The real Vivify download view appears only after a Vivify map enters play; Init was withheld so no download or bad file could be created.");
        yield return ModUiConditionalMirrorDialog(run, camera);
        yield return ModUiConditionalCountersError(run, camera);
    }
}
