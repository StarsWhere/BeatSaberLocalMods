using System;
using System.Collections;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EffectModsSmoke;

public sealed partial class Runner
{
    IEnumerator VerifyTrainingMenuShortcuts()
    {
        EnsureTrainingPluginState();
        if (!trainingAnyExpected) yield break;
        object main = GetStaticMember(FindTrainingType("BeatSaberMarkupLanguage.BeatSaberUI"), "MainFlowCoordinator");
        object registry = GetStaticMember(FindTrainingType("BeatSaberMarkupLanguage.MenuButtons.MenuButtons"), "Instance");
        yield return Wait(() => (LocalUiRegistryReady() && ModUiAuditFlowActive(main)) && !ReadBool(main, "isInTransition", true), 15f, "shortcut-main-ready");
        yield return new WaitForSecondsRealtime(0.5f);
        Component menu = LocalUiMenuButtonsView();
        Check("shortcut-menu-visible", menu != null, "Native Mods button panel is active");
        object[] buttons = (GetMember(registry, "Buttons") as IEnumerable)?.Cast<object>().ToArray() ?? Array.Empty<object>();
        Check("shortcut-energy-center-only", !buttons.Any(x =>
            new[] { "Configure Activity Energy", "Activity Energy" }.Contains(GetMember(x, "Text") as string)),
            "Activity Energy has no independent main-menu shortcut");
        Camera camera = FindTrainingMenuCamera();
        Check("shortcut-camera", camera != null, "Menu camera available");

        if (trainingNoteLoaded)
        {
            Button centerButton = LocalUiButton(menu, "Custom mods", "自定义模组", LocalUiOptionalCaption("Custom mods", "LocalModUI"));
            Check("shortcut-center-before-training", centerButton != null, "The visible mod center button is interactable");
            centerButton.onClick.Invoke();
            yield return Wait(() => LocalUiReady(LocalUiCenterFlow(main)) &&
                ModUiAuditViewActive(GetMember(LocalUiCenterFlow(main), "menu") as Component), 15f, "shortcut-center-before-training");
            object initialFlow = LocalUiCenterFlow(main);
            Component initialCenter = GetMember(initialFlow, "menu") as Component;
            Button devices = LocalUiButton(initialCenter, "Devices and interface", "设备与界面", LocalUiOptionalCaption("Devices and interface", "LocalModUI"));
            Check("shortcut-devices-category", devices != null, "Devices category is visible");
            devices.onClick.Invoke();
            yield return Wait(() => ReadBool(initialCenter, "DevicesSelected", false), 5f, "shortcut-devices-selected");
            Check("shortcut-devices-back-main", ModUiAuditDismissFlow(main, initialFlow, out string initialDetail), initialDetail);
            yield return Wait(() => !ModUiAuditFlowActive(initialFlow) && (LocalUiRegistryReady() && ModUiAuditFlowActive(main)) &&
                !ReadBool(main, "isInTransition", true), 15f, "shortcut-devices-main");
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Button training = LocalUiButton(menu, "Training settings", "训练设置", LocalUiOptionalCaption("Training settings", "LocalModUI"));
                Check("shortcut-training-button-" + attempt, training != null,
                    "The visible training shortcut is interactable");
                training.onClick.Invoke();
                yield return Wait(() => LocalUiReady(LocalUiCenterFlow(main)) &&
                    LocalUiPageId(LocalUiCenterFlow(main)) == "training" &&
                    ModUiAuditViewActive(GetMember(LocalUiCenterFlow(main), "settings") as Component), 15f, "shortcut-training-open-" + attempt);
                object flow = LocalUiCenterFlow(main);
                Component page = GetMember(flow, "settings") as Component;
                object entry = GetMember(page, "Entry");
                Check("shortcut-training-route-" + attempt, ModUiAuditViewActive(page) &&
                    GetMember(GetMember(entry, "Page"), "Host")?.GetType().FullName == "NoteMotionControl.SettingsMenu",
                    "Shortcut opened the registered training settings host in the center");
                yield return ModUiAuditWaitLayout(page, 4, 3f);
                CaptureTrainingUiPage("shortcut-training-" + attempt, camera, page);
                Check("shortcut-training-back-page-" + attempt, ModUiAuditDismissFlow(main, flow, out string detail), detail);
                yield return Wait(() => GetMember(flow, "settings") == null && LocalUiReady(flow) && ModUiAuditViewActive(GetMember(flow, "menu") as Component),
                    15f, "shortcut-training-center-" + attempt);
                Check("shortcut-training-category-" + attempt, ReadBool(GetMember(flow, "menu"), "TrainingSelected", false),
                    "Returning from training keeps its category");
                Check("shortcut-training-back-main-" + attempt, ModUiAuditDismissFlow(main, flow, out detail), detail);
                yield return Wait(() => !ModUiAuditFlowActive(flow) && (LocalUiRegistryReady() && ModUiAuditFlowActive(main)) &&
                    Active<MainMenuViewController>() != null && Active<MainMenuViewController>().isActivated && !ReadBool(main, "isInTransition", true),
                    15f, "shortcut-training-main-" + attempt);
            }
        }

        if (trainingEnergyLoaded)
        {
            Button centerButton = LocalUiButton(menu, "Custom mods", "自定义模组", LocalUiOptionalCaption("Custom mods", "LocalModUI"));
            Check("shortcut-center-button", centerButton != null, "The visible mod center button is interactable");
            centerButton.onClick.Invoke();
            yield return Wait(() => LocalUiReady(LocalUiCenterFlow(main)) && ModUiAuditViewActive(GetMember(LocalUiCenterFlow(main), "menu") as Component),
                15f, "shortcut-center-open");
            object flow = LocalUiCenterFlow(main);
            Component center = GetMember(flow, "menu") as Component;
            if (!ReadBool(center, "TrainingSelected", false))
            {
                Button category = LocalUiButton(center, "Training and activity", "训练与运动", LocalUiOptionalCaption("Training and activity", "LocalModUI"));
                Check("shortcut-training-category-button", category != null, "Training category is visible");
                category.onClick.Invoke();
                yield return null;
            }
            Button energy = LocalUiButton(center, "Activity Energy", "运动统计", "活动耗能", LocalUiOptionalCaption("Activity Energy", "LocalModUI"));
            Check("shortcut-energy-row", energy != null, "Activity Energy is available inside the center");
            energy.onClick.Invoke();
            yield return Wait(() => LocalUiPageId(flow) == "energy" && LocalUiReady(flow) && ModUiAuditViewActive(GetMember(flow, "settings") as Component),
                15f, "shortcut-energy-open");
            Check("shortcut-energy-back-page", ModUiAuditDismissFlow(main, flow, out string detail), detail);
            yield return Wait(() => GetMember(flow, "settings") == null && LocalUiReady(flow) && ModUiAuditViewActive(center), 15f, "shortcut-energy-center");
            Check("shortcut-energy-back-main", ModUiAuditDismissFlow(main, flow, out detail), detail);
            yield return Wait(() => !ModUiAuditFlowActive(flow) && (LocalUiRegistryReady() && ModUiAuditFlowActive(main)), 15f, "shortcut-energy-main");
        }
    }
}
