using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using HMUI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EffectModsSmoke;

public sealed class IndependentSettingsFixtureFlow : FlowCoordinator
{
    internal ViewController View;
    protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
    {
        SetTitle("Settings regression check");
        ProvideInitialViewControllers(View);
    }
}

public sealed partial class Runner
{
    static class IndependentSettingsRestartProbe
    {
        internal static int Calls;
        public static bool Prefix() { Calls++; return false; }
    }

    // Uses the real parsed production layouts and their buttons in an isolated
    // flow. Only their dismissal/network callbacks and the restart body are
    // intercepted; config writes, draft restoration and hint consumers run.
    IEnumerator VerifyIndependentSettings()
    {
        var context = SettingsNavigationMainContext();
        var container = context.Container;
        var main = Active<MainFlowCoordinator>();
        Type pmType = FindTrainingType("PlaylistManager.UI.SettingsViewController");
        Type filtersType = FindTrainingType("MorePlaylists.BeatSaver.BeatSaverFiltersViewController");
        Check("independent-settings-plugins", pmType != null && filtersType != null,
            "PlaylistManager and MorePlaylists production types are present.");
        var pm = (ViewController)container.Resolve(pmType);
        var filters = (ViewController)container.Resolve(filtersType);
        var fixture = container.InstantiateComponentOnNewGameObject<IndependentSettingsFixtureFlow>("IndependentSettingsFixture");
        object config = AccessTools.Property(FindTrainingType("PlaylistManager.Configuration.PluginConfig"), "Instance").GetValue(null, null);
        bool hintsBefore = (bool)GetMember(config, "PlaylistHoverHints");
        bool foldersBefore = (bool)GetMember(config, "FoldersDisabled");
        object model = GetMember(filters, "filterOptions");
        object oldSearch = GetMember(model, "NullableSearchFilter");
        object oldMode = GetMember(model, "FilterMode");
        object oldUser = GetMember(model, "UserName");
        string oldQuery = oldSearch == null ? null : (string)GetMember(oldSearch, "Query");
        bool oldEmpty = oldSearch != null && (bool)GetMember(oldSearch, "IncludeEmpty");
        bool oldCurated = oldSearch != null && (bool)GetMember(oldSearch, "IsCurated");
        FieldInfo pmDismiss = AccessTools.Field(pmType, "DismissFlowEvent");
        FieldInfo filterDismiss = AccessTools.Field(filtersType, "RequestDismiss");
        FieldInfo filtersSet = AccessTools.Field(filtersType, "FiltersSet");
        object oldPmDismiss = pmDismiss.GetValue(pm), oldFilterDismiss = filterDismiss.GetValue(filters), oldFiltersSet = filtersSet.GetValue(filters);
        Action close = () => main.DismissFlowCoordinator(fixture, immediately: true);
        pmDismiss.SetValue(pm, close);
        filterDismiss.SetValue(filters, close);
        filtersSet.SetValue(filters, null);
        var guard = new Harmony("localtraining.smoke.independent-settings");
        IndependentSettingsRestartProbe.Calls = 0;
        guard.Patch(AccessTools.Method(typeof(MenuTransitionsHelper), "RestartGame"),
            new HarmonyMethod(typeof(IndependentSettingsRestartProbe), nameof(IndependentSettingsRestartProbe.Prefix)));
        try
        {
            fixture.View = pm;
            main.PresentFlowCoordinator(fixture, immediately: true);
            yield return Wait(() => pm.isActivated, 10f, "independent-playlist-settings-open");
            VerifyIndependentHoverOwnership(config, GetMember(pm, "hoverHints"));
            SetTrainingMember(config, "PlaylistHoverHints", hintsBefore);
            SetTrainingMember(pm, "PlaylistHoverHints", !hintsBefore);
            SetTrainingMember(pm, "FoldersDisabled", foldersBefore);
            Check("independent-playlist-hints-no-restart-required", !(bool)GetMember(pm, "SoftRestart"),
                "Only the locally applied hover hint setting changed.");
            IndependentSettingsClick(pm, true);
            yield return Wait(() => !fixture.isActivated, 10f, "independent-playlist-settings-confirm");
            Check("independent-playlist-hints-confirm", IndependentSettingsRestartProbe.Calls == 0 &&
                (bool)GetMember(config, "PlaylistHoverHints") == !hintsBefore && SettingsNavigationMainContext() == context,
                "Real OK saved the setting and dismissed without rebuilding MainMenu.");

            main.PresentFlowCoordinator(fixture, immediately: true);
            yield return Wait(() => pm.isActivated, 10f, "independent-playlist-settings-reopen");
            SetTrainingMember(pm, "PlaylistHoverHints", hintsBefore);
            IndependentSettingsClick(pm, false);
            yield return Wait(() => !fixture.isActivated, 10f, "independent-playlist-settings-cancel");
            Check("independent-playlist-cancel-discards", (bool)GetMember(config, "PlaylistHoverHints") == !hintsBefore,
                "Real Cancel did not save the edited hint draft.");
            main.PresentFlowCoordinator(fixture, immediately: true);
            yield return Wait(() => pm.isActivated, 10f, "independent-playlist-settings-folder-open");
            Check("independent-playlist-cancel-reopen", (bool)GetMember(pm, "PlaylistHoverHints") == !hintsBefore,
                "Reopening restored the committed hint value.");
            SetTrainingMember(pm, "FoldersDisabled", !foldersBefore);
            Check("independent-playlist-folders-restart-required", (bool)GetMember(pm, "SoftRestart"),
                "Directory structure changes still require rebuilding the menu bindings.");
            IndependentSettingsClick(pm, true);
            Check("independent-playlist-folders-original-restart", IndependentSettingsRestartProbe.Calls == 1 &&
                (bool)GetMember(config, "FoldersDisabled") == !foldersBefore,
                "Real OK reached the native RestartGame entry; this test guard prevented its body.");
            SetTrainingMember(config, "FoldersDisabled", foldersBefore);
            IndependentSettingsClick(pm, false);
            yield return Wait(() => !fixture.isActivated, 10f, "independent-playlist-settings-folder-close");

            object search = GetMember(model, "SearchFilter");
            SetTrainingMember(search, "Query", "committed search");
            SetTrainingMember(search, "IncludeEmpty", false);
            SetTrainingMember(search, "IsCurated", true);
            IndependentSetMode(model, "Search");
            fixture.View = filters;
            main.PresentFlowCoordinator(fixture, immediately: true);
            yield return Wait(() => filters.isActivated, 10f, "independent-more-filters-open");
            var input = (InputFieldView)GetMember(filters, "inputFieldView");
            var tabs = (TextSegmentedControl)GetMember(filters, "filtersTabSelector");
            var searchPanel = ((RectTransform)GetMember(filters, "searchVerticalTransform")).gameObject;
            object animations = GetMember(filters, "animationGrabber");
            object dismissAnimation = GetMember(animations, "DismissPanelAnimation");
            float animationDeadline = Convert.ToSingle(AccessTools.Field(dismissAnimation.GetType(), "_duration").GetValue(dismissAnimation)) + 0.2f;
            Check("independent-more-search-restore", input.text == "committed search" && tabs.selectedCellNumber == 0 &&
                !(bool)GetMember(filters, "IncludeEmpty") && (bool)GetMember(filters, "CuratedOnly"),
                "Search controls match the committed model on activation.");
            input.SetText("discarded search");
            SetTrainingMember(filters, "IncludeEmpty", true);
            SetTrainingMember(filters, "CuratedOnly", false);
            IndependentSettingsSwitchTab(filters, tabs, 1);
            Check("independent-more-cancel-started-dismiss", searchPanel.GetComponent<PanelAnimation>() != null,
                "The real TabSwitched handler started the native dismiss animation before Cancel.");
            IndependentSettingsClick(filters, false);
            yield return Wait(() => !fixture.isActivated, 10f, "independent-more-search-cancel");
            Check("independent-more-search-cancel-model", (string)GetMember(search, "Query") == "committed search" &&
                GetMember(model, "FilterMode").ToString() == "Search" && !(bool)GetMember(search, "IncludeEmpty"),
                "Real Cancel did not mutate the active filter.");
            main.PresentFlowCoordinator(fixture, immediately: true);
            yield return Wait(() => filters.isActivated, 10f, "independent-more-search-reopen");
            yield return new WaitForSecondsRealtime(animationDeadline);
            Check("independent-more-search-cancel-reopen", input.text == "committed search" && tabs.selectedCellNumber == 0 &&
                !(bool)GetMember(filters, "IncludeEmpty") && (bool)GetMember(filters, "CuratedOnly") && IndependentSearchPanelRestored(searchPanel),
                "Canceled search text, tab and checkboxes did not survive reopening; the old animation deadline passed without hiding or fading the restored panel.");
            IndependentSettingsClick(filters, false);
            yield return Wait(() => !fixture.isActivated, 10f, "independent-more-search-close");

            SetTrainingMember(model, "UserName", "committed-user");
            IndependentSetMode(model, "User");
            main.PresentFlowCoordinator(fixture, immediately: true);
            yield return Wait(() => filters.isActivated, 10f, "independent-more-user-open");
            Check("independent-more-user-restore", tabs.selectedCellNumber == 1 && input.text == "committed-user" &&
                !((RectTransform)GetMember(filters, "searchVerticalTransform")).gameObject.activeSelf,
                "User mode restores its committed tab and username.");
            input.SetText("discarded-user");
            IndependentSettingsSwitchTab(filters, tabs, 0);
            IndependentSettingsClick(filters, false);
            yield return Wait(() => !fixture.isActivated, 10f, "independent-more-user-cancel");
            main.PresentFlowCoordinator(fixture, immediately: true);
            yield return Wait(() => filters.isActivated, 10f, "independent-more-user-reopen");
            yield return new WaitForSecondsRealtime(animationDeadline);
            Check("independent-more-user-cancel-reopen", tabs.selectedCellNumber == 1 && input.text == "committed-user" && !searchPanel.activeSelf,
                "Canceled user text and tab did not survive reopening; a canceled present animation cannot reveal search controls in user mode.");
            input.SetText("saved-user");
            IndependentSettingsClick(filters, true);
            yield return Wait(() => !fixture.isActivated, 10f, "independent-more-user-confirm");
            Check("independent-more-user-confirm", (string)GetMember(model, "UserName") == "saved-user" &&
                GetMember(model, "FilterMode").ToString() == "User", "Real OK commits the current user filter.");
            main.PresentFlowCoordinator(fixture, immediately: true);
            yield return Wait(() => filters.isActivated, 10f, "independent-more-saved-reopen");
            Check("independent-more-user-confirm-reopen", input.text == "saved-user" && tabs.selectedCellNumber == 1,
                "The confirmed value remains after reopening.");
            // Start and replace the actual native animations within one frame.
            // Programmatic SelectCellWithNumber alone does not emit TabSwitched.
            IndependentSettingsSwitchTab(filters, tabs, 0);
            IndependentSettingsSwitchTab(filters, tabs, 1);
            Check("independent-more-clear-started-dismiss", searchPanel.GetComponent<PanelAnimation>() != null,
                "The real tab handler started a native dismiss animation immediately before ClearFilters.");
            AccessTools.Method(filtersType, "ClearFilters").Invoke(filters, null);
            yield return new WaitForSecondsRealtime(animationDeadline);
            Check("independent-more-clear-controls", input.text == String.Empty && tabs.selectedCellNumber == 0 &&
                !(bool)GetMember(filters, "IncludeEmpty") && !(bool)GetMember(filters, "CuratedOnly") &&
                GetMember(model, "NullableSearchFilter") == null && IndependentSearchPanelRestored(searchPanel),
                "Clear resets the model and controls; after the old dismiss deadline, the search panel stays visible at full opacity and scale.");
            IndependentSettingsClick(filters, false);
            yield return Wait(() => !fixture.isActivated, 10f, "independent-more-clear-close");
            main.PresentFlowCoordinator(fixture, immediately: true);
            yield return Wait(() => filters.isActivated, 10f, "independent-more-clear-reopen");
            Check("independent-more-clear-reopen", input.text == String.Empty && tabs.selectedCellNumber == 0 &&
                !(bool)GetMember(filters, "IncludeEmpty") && !(bool)GetMember(filters, "CuratedOnly") &&
                GetMember(model, "NullableSearchFilter") == null && IndependentSearchPanelRestored(searchPanel),
                "Reopening after Clear retains empty search and default checkboxes without resurrecting a discarded draft.");
            IndependentSettingsClick(filters, false);
            yield return Wait(() => !fixture.isActivated, 10f, "independent-more-cleared-close");
            Check("independent-settings-main-menu-preserved", SettingsNavigationMainContext() == context &&
                IndependentSettingsRestartProbe.Calls == 1, "Only the folder option requested a restart.");
        }
        finally
        {
            if (fixture.isActivated) main.DismissFlowCoordinator(fixture, immediately: true);
            pmDismiss.SetValue(pm, oldPmDismiss);
            filterDismiss.SetValue(filters, oldFilterDismiss);
            filtersSet.SetValue(filters, oldFiltersSet);
            SetTrainingMember(config, "PlaylistHoverHints", hintsBefore);
            SetTrainingMember(config, "FoldersDisabled", foldersBefore);
            AccessTools.Method(GetMember(pm, "hoverHints").GetType(), "RefreshHoverHints").Invoke(GetMember(pm, "hoverHints"), null);
            AccessTools.Property(model.GetType(), "NullableSearchFilter").SetValue(model, oldSearch, null);
            SetTrainingMember(model, "FilterMode", oldMode);
            SetTrainingMember(model, "UserName", oldUser);
            if (oldSearch != null)
            {
                SetTrainingMember(oldSearch, "Query", oldQuery);
                SetTrainingMember(oldSearch, "IncludeEmpty", oldEmpty);
                SetTrainingMember(oldSearch, "IsCurated", oldCurated);
            }
            if (GetMember(filters, "inputFieldView") != null) AccessTools.Method(filtersType, "RestoreFilters").Invoke(filters, null);
            guard.UnpatchSelf();
            UnityEngine.Object.Destroy(fixture.gameObject);
        }
    }

    void VerifyIndependentHoverOwnership(object config, object owner)
    {
        var ownedObject = new GameObject("PlaylistOwnedHintFixture");
        var foreignObject = new GameObject("ForeignHintFixture");
        ownedObject.SetActive(false);
        foreignObject.SetActive(false);
        try
        {
            var ownedCell = ownedObject.AddComponent<AnnotatedBeatmapLevelCollectionCell>();
            var foreignCell = foreignObject.AddComponent<AnnotatedBeatmapLevelCollectionCell>();
            var foreign = foreignObject.AddComponent<HoverHint>();
            foreign.text = "foreign text";
            bool foreignEnabled = foreign.enabled;
            var pack = new BeatmapLevelPack("settings-check", "Owned hint fixture", "Fixture", null, null,
                default, Array.Empty<BeatmapLevel>(), default);
            AccessTools.Field(typeof(AnnotatedBeatmapLevelCollectionCell), "_beatmapLevelPack").SetValue(ownedCell, pack);
            AccessTools.Field(typeof(AnnotatedBeatmapLevelCollectionCell), "_beatmapLevelPack").SetValue(foreignCell, pack);
            MethodInfo update = AccessTools.Method(owner.GetType(), "UpdateHoverHint");
            SetTrainingMember(config, "PlaylistHoverHints", true);
            update.Invoke(owner, new object[] { ownedCell, pack });
            HoverHint owned = ownedObject.GetComponent<HoverHint>();
            Check("independent-playlist-hint-ownership", owned != null && owned.GetType().Name == "PlaylistNameHoverHint" &&
                owned.text == pack.packName && owned.enabled, "PlaylistManager creates an explicitly owned hint component.");
            SetTrainingMember(config, "PlaylistHoverHints", false);
            update.Invoke(owner, new object[] { ownedCell, pack });
            update.Invoke(owner, new object[] { foreignCell, pack });
            Check("independent-playlist-hints-disable-owned-only", !owned.enabled && foreign.enabled == foreignEnabled &&
                foreign.text == "foreign text", "Disabling hides owned hints and leaves another mod's hint untouched.");
            SetTrainingMember(config, "PlaylistHoverHints", true);
            update.Invoke(owner, new object[] { ownedCell, pack });
            update.Invoke(owner, new object[] { foreignCell, pack });
            Check("independent-playlist-hints-reuse-no-duplicates", owned.enabled &&
                ownedObject.GetComponents<HoverHint>().Length == 1 && foreignObject.GetComponents<HoverHint>().Length == 1 &&
                foreign.text == "foreign text", "Re-enabling reuses the owned component without claiming or duplicating foreign hints.");
            MethodInfo refresh = AccessTools.Method(owner.GetType(), "RefreshHoverHints");
            for (int index = 0; index < 4; index++)
            {
                bool enabled = index % 2 == 1;
                SetTrainingMember(config, "PlaylistHoverHints", enabled);
                refresh.Invoke(owner, null);
                Check("independent-playlist-existing-hint-toggle-" + index, owned.enabled == enabled &&
                    ownedObject.GetComponents<HoverHint>().Length == 1 && foreign.enabled == foreignEnabled &&
                    foreign.text == "foreign text" && foreignObject.GetComponents<HoverHint>().Length == 1,
                    "Refreshing already created cells changes only the owned component and never duplicates it.");
            }
        }
        finally
        {
            UnityEngine.Object.Destroy(ownedObject);
            UnityEngine.Object.Destroy(foreignObject);
        }
    }

    static void IndependentSetMode(object model, string mode)
    {
        var type = AccessTools.Property(model.GetType(), "FilterMode").PropertyType;
        SetTrainingMember(model, "FilterMode", Enum.Parse(type, mode));
    }

    static void IndependentSettingsSwitchTab(ViewController view, TextSegmentedControl tabs, int index)
    {
        tabs.SelectCellWithNumber(index);
        AccessTools.Method(view.GetType(), "TabSwitched").Invoke(view, new object[] { tabs, index });
    }

    static bool IndependentSearchPanelRestored(GameObject panel)
    {
        var group = panel.GetComponent<CanvasGroup>();
        return panel.activeSelf && group != null && Mathf.Abs(group.alpha - 1f) < 0.0001f &&
            Vector3.Distance(panel.transform.localScale, Vector3.one) < 0.0001f;
    }

    static void IndependentSettingsClick(ViewController view, bool confirm)
    {
        string[] captions = confirm ? new[] { "OK", "Ok", "确定" } : new[] { "Cancel", "取消" };
        var button = view.GetComponentsInChildren<Button>(true).Single(value => value.gameObject.activeInHierarchy &&
            value.GetComponentsInChildren<TMP_Text>(true).Any(text => captions.Contains(text.text)));
        if (!button.interactable) throw new InvalidOperationException("Settings button is not interactable.");
        button.onClick.Invoke();
    }
}
