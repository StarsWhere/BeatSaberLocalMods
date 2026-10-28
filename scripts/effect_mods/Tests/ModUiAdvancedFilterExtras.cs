using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace EffectModsSmoke;

public sealed partial class Runner
{
    static GameObject ModUiAdvancedFilterPage(object panel, string member)
    {
        return ReadMemberAny(panel, member) as GameObject;
    }

    static Component ModUiAdvancedFilterModalKeyboard(object panel)
    {
        object keyboard = ReadMemberAny(panel, "boundPresetNameKeyboard", "presetNameKeyboard");
        return ReadMemberAny(keyboard, "ModalView", "modalView") as Component;
    }

    static bool ModUiAdvancedFilterPageActive(object panel, string member)
    {
        GameObject page = ModUiAdvancedFilterPage(panel, member);
        try { return page != null && page.activeInHierarchy; }
        catch { return false; }
    }

    static int ModUiAdvancedFilterPresetCount(object runtime)
    {
        object collection = ReadMemberAny(runtime, "Presets");
        object values = ReadMemberAny(collection, "Presets", "presets");
        if (values is not IEnumerable enumerable || values is string) return 0;
        try { return enumerable.Cast<object>().Count(value => value != null); }
        catch { return 0; }
    }

    static Button ModUiAdvancedFilterPresetButton(object panel, string action)
    {
        GameObject options = ReadMemberAny(panel, "boundPresetOptions", "presetOptions") as GameObject;
        if (options == null) return null;
        try
        {
            return options.GetComponentsInChildren<Button>(true).FirstOrDefault(button =>
            {
                if (button == null || !button.gameObject.activeInHierarchy || !button.interactable) return false;
                string name = button.gameObject.name ?? string.Empty;
                string text = ModUiAuditTextFor(button);
                return string.Equals(name, action, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, action, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch { return null; }
    }

    static bool ModUiAdvancedFilterInvokeButton(Button button, out string detail)
    {
        detail = string.Empty;
        if (button == null)
        {
            detail = "native preset action button is unavailable";
            return false;
        }
        try
        {
            button.onClick.Invoke();
            detail = "UnityEngine.UI.Button.onClick invoked; action=" + button.gameObject.name;
            return true;
        }
        catch (Exception error)
        {
            detail = ModUiAuditException(error);
            return false;
        }
    }

    static void ModUiAdvancedFilterRecord(ModUiAuditRun run, string target, string status,
        string detail, bool notApplicable = false)
    {
        run.Entrances.Add(new
        {
            kind = "song-selection-extra",
            scope = "AdvancedSongFilters",
            target,
            status,
            detail,
            notApplicable
        });
        if (status == "blocked") run.Block("AdvancedSongFilters", target, detail);
    }

    IEnumerator ModUiAdvancedFilterCapturePage(ModUiAuditRun run, object panel,
        Camera camera, string target, string title, string slug, string pageMember)
    {
        GameObject page = ModUiAdvancedFilterPage(panel, pageMember);
        if (page == null || !page.activeInHierarchy)
        {
            ModUiAdvancedFilterRecord(run, target, "blocked",
                "The native Panel page was not active after its safe navigation action.");
            yield break;
        }

        ModUiAdvancedFilterRecord(run, target, "verified",
            "Native Panel page is active; only read-only capture and cancellation are used.");
        yield return ModUiAuditCaptureSongSelectionPage(run, page.transform, camera,
            "AdvancedSongFilters", title, slug, "AdvancedSongFilters.Panel", target);
    }

    IEnumerator ModUiAdvancedFilterCaptureRoot(ModUiAuditRun run, Component root,
        Camera camera, string target, string title, string slug)
    {
        if (root == null || !root.gameObject.activeInHierarchy)
        {
            ModUiAdvancedFilterRecord(run, target, "blocked", "Panel root is unavailable or inactive.");
            yield break;
        }
        ModUiAdvancedFilterRecord(run, target, "verified",
            "Native Panel keyboard/modal is active; no text entry or confirm action is invoked.");
        yield return ModUiAuditCaptureSongSelectionPage(run, root, camera,
            "AdvancedSongFilters", title, slug, "AdvancedSongFilters.Panel", target);
    }

    IEnumerator ModUiAdvancedFilterRangePages(ModUiAuditRun run, object panel, Camera camera)
    {
        foreach (string openAction in new[] { "OpenNpsMin", "OpenNjsMin", "OpenDurationMin", "OpenBpmMin", "OpenNotesMin" })
        {
            if (!ModUiAuditInvokeSafeLazyAction(panel, openAction, out string openDetail))
            {
                ModUiAdvancedFilterRecord(run, openAction, "blocked", openDetail);
                continue;
            }

            yield return ModUiAuditWait(run, () => ModUiAdvancedFilterPageActive(panel, "boundRangePage"), 6, 8f);
            if (!ModUiAdvancedFilterPageActive(panel, "boundRangePage"))
            {
                ModUiAdvancedFilterRecord(run, openAction, "blocked",
                    "Panel range action returned without an active boundRangePage.");
                continue;
            }

            foreach (string focusAction in new[] { "RangeMinimum", "RangeMaximum" })
            {
                if (!ModUiAuditInvokeSafeLazyAction(panel, focusAction, out string focusDetail))
                {
                    ModUiAdvancedFilterRecord(run, openAction + "/" + focusAction, "blocked", focusDetail);
                    continue;
                }

                yield return ModUiAdvancedFilterCapturePage(run, panel, camera,
                    openAction + "/" + focusAction,
                    "AdvancedSongFilters " + openAction + " " + focusAction,
                    "advanced-filters-" + ModUiAuditSafeSlug(openAction + "-" + focusAction),
                    "boundRangePage");
            }

            if (!ModUiAuditInvokeSafeLazyAction(panel, "CancelRange", out string cancelDetail))
            {
                ModUiAdvancedFilterRecord(run, openAction + "/CancelRange", "blocked", cancelDetail);
            }
            else
            {
                yield return ModUiAuditWait(run, () => !ModUiAdvancedFilterPageActive(panel, "boundRangePage"), 6, 8f);
                ModUiAdvancedFilterRecord(run, openAction + "/CancelRange",
                    ModUiAdvancedFilterPageActive(panel, "boundRangePage") ? "blocked" : "verified",
                    ModUiAdvancedFilterPageActive(panel, "boundRangePage")
                        ? "CancelRange left the range page active."
                        : "Range draft was cancelled and the main page was restored.");
            }
        }
    }

    IEnumerator ModUiAdvancedFilterSavePage(ModUiAuditRun run, object panel, Camera camera)
    {
        if (!ModUiAuditInvokeSafeLazyAction(panel, "OpenSavePreset", out string openDetail))
        {
            ModUiAdvancedFilterRecord(run, "OpenSavePreset", "blocked", openDetail);
            yield break;
        }

        yield return ModUiAdvancedFilterCapturePage(run, panel, camera,
            "OpenSavePreset", "AdvancedSongFilters Save Preset", "advanced-filters-save-preset",
            "boundPresetSavePage");

        if (!ModUiAuditInvokeSafeLazyAction(panel, "EditPresetName", out string keyboardDetail))
        {
            ModUiAdvancedFilterRecord(run, "OpenSavePreset/EditPresetName", "blocked", keyboardDetail);
        }
        else
        {
            Component keyboard = ModUiAdvancedFilterModalKeyboard(panel);
            yield return ModUiAuditWait(run, () => ModUiAuditSongSelectionActive(keyboard), 6, 8f);
            if (ModUiAuditSongSelectionActive(keyboard))
            {
                yield return ModUiAdvancedFilterCaptureRoot(run, panel as Component ??
                    ModUiAuditSongSelectionComponent(panel, "panelRoot", "filterModal"), camera,
                    "OpenSavePreset/EditPresetName", "AdvancedSongFilters Preset Name Keyboard",
                    "advanced-filters-save-preset-keyboard");
            }
            else
            {
                ModUiAdvancedFilterRecord(run, "OpenSavePreset/EditPresetName", "blocked",
                    "EditPresetName did not expose the native preset-name ModalView.");
            }

            if (!ModUiAuditInvokeSafeLazyAction(panel, "CancelPresetName", out string cancelDetail))
                ModUiAdvancedFilterRecord(run, "OpenSavePreset/CancelPresetName", "blocked", cancelDetail);
            else
            {
                yield return ModUiAuditWait(run, () => !ModUiAuditSongSelectionActive(ModUiAdvancedFilterModalKeyboard(panel)), 6, 8f);
                ModUiAdvancedFilterRecord(run, "OpenSavePreset/CancelPresetName",
                    ModUiAuditSongSelectionActive(ModUiAdvancedFilterModalKeyboard(panel)) ? "blocked" : "verified",
                    ModUiAuditSongSelectionActive(ModUiAdvancedFilterModalKeyboard(panel))
                        ? "CancelPresetName left the keyboard active."
                        : "Preset-name keyboard was cancelled without saving.");
            }
        }

        if (!ModUiAuditInvokeSafeLazyAction(panel, "CloseManagePresets", out string closeDetail))
            ModUiAdvancedFilterRecord(run, "OpenSavePreset/CloseManagePresets", "blocked", closeDetail);
    }

    IEnumerator ModUiAdvancedFilterExistingPresetAction(ModUiAuditRun run, object panel,
        object runtime, Camera camera, string action)
    {
        int presetCount = ModUiAdvancedFilterPresetCount(runtime);
        if (presetCount == 0)
        {
            ModUiAdvancedFilterRecord(run, action, "skipped-not-applicable",
                "No persisted filter preset exists in the active runtime; no fake preset was created.", true);
            yield break;
        }

        if (!ModUiAuditInvokeSafeLazyAction(panel, "OpenManagePresets", out string manageDetail))
        {
            ModUiAdvancedFilterRecord(run, action + "/OpenManagePresets", "blocked", manageDetail);
            yield break;
        }
        yield return ModUiAuditWait(run, () => ModUiAdvancedFilterPageActive(panel, "boundPresetManagePage"), 6, 8f);
        if (!ModUiAdvancedFilterPageActive(panel, "boundPresetManagePage"))
        {
            ModUiAdvancedFilterRecord(run, action + "/OpenManagePresets", "blocked",
                "OpenManagePresets did not expose an active manage page.");
            yield break;
        }

        Button button = ModUiAdvancedFilterPresetButton(panel, action);
        if (!ModUiAdvancedFilterInvokeButton(button, out string actionDetail))
        {
            ModUiAdvancedFilterRecord(run, action, "blocked", actionDetail);
            ModUiAuditInvokeSafeLazyAction(panel, "CloseManagePresets", out _);
            yield break;
        }
        ModUiAdvancedFilterRecord(run, action, "invoked", actionDetail);

        string pageMember = action == "Rename" ? "boundPresetRenamePage" : "boundPresetDeletePage";
        string pageTitle = action == "Rename" ? "AdvancedSongFilters Rename Preset" : "AdvancedSongFilters Delete Preset Confirmation";
        yield return ModUiAdvancedFilterCapturePage(run, panel, camera, action, pageTitle,
            "advanced-filters-" + ModUiAuditSafeSlug(action), pageMember);

        if (action == "Rename")
        {
            if (ModUiAuditInvokeSafeLazyAction(panel, "EditPresetName", out string editDetail))
            {
                Component keyboard = ModUiAdvancedFilterModalKeyboard(panel);
                yield return ModUiAuditWait(run, () => ModUiAuditSongSelectionActive(keyboard), 6, 8f);
                if (ModUiAuditSongSelectionActive(keyboard))
                    yield return ModUiAdvancedFilterCaptureRoot(run, panel as Component ?? ModUiAuditSongSelectionComponent(panel, "panelRoot", "filterModal"), camera,
                        "Rename/EditPresetName", "AdvancedSongFilters Rename Keyboard", "advanced-filters-rename-keyboard");
                else ModUiAdvancedFilterRecord(run, "Rename/EditPresetName", "blocked", "Rename keyboard did not become active.");
            }
            else ModUiAdvancedFilterRecord(run, "Rename/EditPresetName", "blocked", editDetail);

            if (!ModUiAuditInvokeSafeLazyAction(panel, "CancelPresetName", out string cancelDetail))
                ModUiAdvancedFilterRecord(run, "Rename/CancelPresetName", "blocked", cancelDetail);
            else
            {
                yield return ModUiAuditWait(run, () => !ModUiAuditSongSelectionActive(ModUiAdvancedFilterModalKeyboard(panel)), 6, 8f);
                ModUiAdvancedFilterRecord(run, "Rename/CancelPresetName",
                    ModUiAdvancedFilterSongKeyboardClosed(panel) ? "verified" : "blocked",
                    ModUiAdvancedFilterSongKeyboardClosed(panel)
                        ? "Rename keyboard was cancelled without changing the preset name."
                        : "CancelPresetName left the rename keyboard active.");
            }
        }
        else
        {
            if (!ModUiAuditInvokeSafeLazyAction(panel, "CancelDeletePreset", out string cancelDetail))
                ModUiAdvancedFilterRecord(run, "Delete/CancelDeletePreset", "blocked", cancelDetail);
            else
            {
                yield return ModUiAuditWait(run, () => !ModUiAdvancedFilterPageActive(panel, "boundPresetDeletePage"), 6, 8f);
                ModUiAdvancedFilterRecord(run, "Delete/CancelDeletePreset",
                    ModUiAdvancedFilterPageActive(panel, "boundPresetDeletePage") ? "blocked" : "verified",
                    ModUiAdvancedFilterPageActive(panel, "boundPresetDeletePage")
                        ? "CancelDeletePreset left the confirmation page active."
                        : "Delete confirmation was cancelled without deleting the preset.");
            }
        }

        if (!ModUiAuditInvokeSafeLazyAction(panel, "CloseManagePresets", out string closeDetail))
            ModUiAdvancedFilterRecord(run, action + "/CloseManagePresets", "blocked", closeDetail);
    }

    static bool ModUiAdvancedFilterSongKeyboardClosed(object panel)
    {
        return !ModUiAuditSongSelectionActive(ModUiAdvancedFilterModalKeyboard(panel));
    }

    IEnumerator ModUiAuditAdvancedFilterSubpages(ModUiAuditRun run, Camera camera)
    {
        Type runtimeType = FindTrainingType("AdvancedSongFilters.Runtime");
        object runtime = GetStaticMember(runtimeType, "Current");
        object panel = GetMember(runtime, "panel");
        if (runtime == null || panel == null)
        {
            ModUiAdvancedFilterRecord(run, "AdvancedFilterSubpages", "blocked",
                "AdvancedSongFilters.Runtime.Current or its existing Panel instance is unavailable.");
            yield break;
        }

        bool menuActive = ModUiAuditReadBool(GetMember(runtime, "MenuActive"), string.Empty, false);
        if (!menuActive)
        {
            ModUiAdvancedFilterRecord(run, "AdvancedFilterSubpages", "blocked",
                "AdvancedSongFilters reports MenuActive=false; subpage navigation was withheld.");
            yield break;
        }

        bool wasOpen = ModUiAuditReadBool(GetMember(panel, "IsOpen"), string.Empty, false);
        int revisionBefore = ReadInt(GetMember(panel, "Revision"), string.Empty, -1);
        bool openedHere = false;
        try
        {
            if (!wasOpen)
            {
                if (!ModUiAuditInvokeSafeLazyAction(panel, "Open", out string openDetail))
                {
                    ModUiAdvancedFilterRecord(run, "Panel.Open", "blocked", openDetail);
                    yield break;
                }
                openedHere = true;
                yield return ModUiAuditWait(run, () => ModUiAuditSongSelectionActive(ModUiAuditSongSelectionComponent(panel, "filterModal", "panelRoot", "ownedUiRoot")), 6, 10f);
            }

            Component root = ModUiAuditAdvancedPanelRoot(panel) ?? ModUiAuditSongSelectionComponent(panel, "filterModal", "panelRoot", "ownedUiRoot");
            if (root == null || !root.gameObject.activeInHierarchy)
            {
                ModUiAdvancedFilterRecord(run, "AdvancedFilterSubpages", "blocked", "Existing Panel did not expose an active root.");
                yield break;
            }

            yield return ModUiAdvancedFilterRangePages(run, panel, camera);
            yield return ModUiAdvancedFilterSavePage(run, panel, camera);
            yield return ModUiAdvancedFilterExistingPresetAction(run, panel, runtime, camera, "Rename");
            yield return ModUiAdvancedFilterExistingPresetAction(run, panel, runtime, camera, "Delete");
        }
        finally
        {
            try { ModUiAuditInvokeSafeLazyAction(panel, "CancelRange", out _); } catch { }
            try { ModUiAuditInvokeSafeLazyAction(panel, "CancelPresetName", out _); } catch { }
            try { ModUiAuditInvokeSafeLazyAction(panel, "CancelDeletePreset", out _); } catch { }
            try { ModUiAuditInvokeSafeLazyAction(panel, "CloseManagePresets", out _); } catch { }
            if (openedHere)
            {
                try { ModUiAuditInvokeSafeLazyAction(panel, "CancelDraft", out _); } catch { }
            }
            int revisionAfter = ReadInt(GetMember(panel, "Revision"), string.Empty, -1);
            ModUiAdvancedFilterRecord(run, "AdvancedFilterSubpages/restore",
                openedHere ? (revisionAfter == -1 ? "verified" : "blocked") :
                    (revisionBefore == revisionAfter ? "verified" : "blocked"),
                "Safe subpage cleanup invoked CancelRange/CancelPresetName/CancelDeletePreset/CloseManagePresets; revision " +
                revisionBefore + " -> " + revisionAfter + ".");
        }
    }
}
