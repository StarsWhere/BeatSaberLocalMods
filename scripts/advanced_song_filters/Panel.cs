using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using BeatSaberMarkupLanguage.Components.Settings;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AdvancedSongFilters
{
    /// <summary>
    /// Native VR editor for the song-filter draft. Runtime owns previews,
    /// persistence and the applied filter; this class owns controls and
    /// draft transactions.
    /// </summary>
    public sealed class Panel : IDisposable
    {
        const float EntryWidth = 32f;
        const float EntryHeight = 8f;
        const float TargetWidth = 150f;
        const float TargetHeight = 76f;
        const float MinimumControl = 8f;
        const int TransitionFrameLimit = 240;

        readonly Runtime runtime;
        readonly Dictionary<Button, ColorBlock> nativeColors = new Dictionary<Button, ColorBlock>();
        readonly Dictionary<Button, Color> nativeTextColors = new Dictionary<Button, Color>();
        readonly Dictionary<string, Button> presetButtons = new Dictionary<string, Button>(StringComparer.Ordinal);
        readonly List<Button> allButtons = new List<Button>();

        RectTransform toolbarRoot;
        RectTransform toolbarSlot;
        Component screen;
        RectTransform screenRect;
        GameObject ownedUiRoot;
        GameObject toolbarEntryHost;
        GameObject bottomEntryHost;
        Button toolbarEntry;
        Button bottomEntry;
        HMUI.ModalView filterModal;
        GameObject panelRoot;

        FilterEditSession editSession;
        RangeEditSession rangeSession;
        Metric rangeMetric;
        bool rangeEditingMinimum;
        string rangeError;
        string localStatus;
        string selectedPresetId;
        // Preset editing is deliberately separate from the selected preset.
        // Renaming or deleting a row must never change the draft conditions.
        string editingPresetId;
        string presetNameDraft;
        bool renamingPreset;
        int selectedPresetRevision = -1;
        string deletePresetId;
        string modeKey;
        string scopeLabelKey;
        readonly List<string> modeValues = new List<string>();
        HMUI.IconSegmentedControl.DataItem[] modeIconItems = new HMUI.IconSegmentedControl.DataItem[0];
        int scopeIndex;
        int requestedRevision = -1;
        bool disposed;
        bool isOpen;
        bool interactive;
        bool presetSubedit;
        bool rangeSubedit;
        bool keyboardSubedit;
        bool syncingSelectors;
        Coroutine measureCoroutine;
        Coroutine transitionCoroutine;
        LayoutWatcher layoutWatcher;

        sealed class LayoutWatcher : MonoBehaviour
        {
            public Panel Owner;
            void OnRectTransformDimensionsChange()
            {
                if (Owner != null) Owner.InvalidateLayout();
            }
        }

        // Stable bindings retained for smoke tools and older integrations.
        [UIComponent("filterModal")] HMUI.ModalView boundFilterModal;
        [UIObject("panelRoot")] GameObject boundPanelRoot;
        [UIObject("mainPage")] GameObject boundMainPage;
        [UIObject("rangePage")] GameObject boundRangePage;
        [UIObject("presetSelectorPage")] GameObject boundPresetSelectorPage;
        [UIObject("presetManagePage")] GameObject boundPresetManagePage;
        [UIObject("presetSavePage")] GameObject boundPresetSavePage;
        [UIObject("presetRenamePage")] GameObject boundPresetRenamePage;
        [UIObject("presetDeletePage")] GameObject boundPresetDeletePage;
        [UIObject("header")] GameObject boundHeader;
        [UIObject("footer")] GameObject boundFooter;
        [UIObject("headerSpacer")] GameObject boundHeaderSpacer;
        [UIObject("footerSpacer")] GameObject boundFooterSpacer;
        [UIObject("metricHeader")] GameObject boundMetricHeader;
        [UIObject("moreMetricHeader")] GameObject boundMoreMetricHeader;
        [UIComponent("title")] TMP_Text boundTitle;
        [UIComponent("presetSelectorButton")] Button boundPresetSelectorButton;
        [UIObject("presetSelectorOptions")] GameObject boundPresetSelectorOptions;
        [UIComponent("saveNewButton")] Button boundSaveNewButton;
        [UIComponent("updateCurrentButton")] Button boundUpdateCurrentButton;
        [UIComponent("presetSaveName")] TMP_Text boundPresetSaveName;
        [UIComponent("presetSaveTitle")] TMP_Text boundPresetSaveTitle;
        [UIComponent("presetSaveStatus")] TMP_Text boundPresetSaveStatus;
        [UIComponent("presetRenameName")] TMP_Text boundPresetRenameName;
        [UIComponent("presetRenameTitle")] TMP_Text boundPresetRenameTitle;
        [UIComponent("presetRenameStatus")] TMP_Text boundPresetRenameStatus;
        [UIComponent("presetSelectorStatus")] TMP_Text boundPresetSelectorStatus;
        [UIComponent("presetManageStatus")] TMP_Text boundPresetManageStatus;
        [UIComponent("presetDeleteStatus")] TMP_Text boundPresetDeleteStatus;
        [UIComponent("presetDeleteConfirmText")] TMP_Text boundPresetDeleteConfirmText;
        [UIComponent("scopeSelector")] HMUI.TextSegmentedControl boundScopeSelector;
        [UIObject("selectionSection")] GameObject boundSelectionSection;
        [UIObject("scopeRow")] GameObject boundScopeRow;
        [UIComponent("modeSelector")] HMUI.IconSegmentedControl boundModeSelector;
        [UIComponent("allModesButton")] Button boundAllModesButton;
        [UIComponent("difficultyAll")] Button boundDifficultyAll;
        [UIComponent("difficulty0")] Button boundDifficulty0;
        [UIComponent("difficulty1")] Button boundDifficulty1;
        [UIComponent("difficulty2")] Button boundDifficulty2;
        [UIComponent("difficulty3")] Button boundDifficulty3;
        [UIComponent("difficulty4")] Button boundDifficulty4;
        [UIObject("scrollContainer")] GameObject boundScrollContainer;
        [UIObject("formContent")] GameObject boundFormContent;
        [UIObject("commonPage")] GameObject boundCommonPage;
        [UIObject("morePage")] GameObject boundMorePage;
        [UIComponent("excludeOfficial")] ToggleSetting boundExcludeOfficial;
        [UIObject("npsRow")] GameObject boundNpsRow;
        [UIObject("njsRow")] GameObject boundNjsRow;
        [UIObject("durationRow")] GameObject boundDurationRow;
        [UIObject("bpmRow")] GameObject boundBpmRow;
        [UIObject("notesRow")] GameObject boundNotesRow;
        [UIComponent("npsMinDec")] Button boundNpsMinDec;
        [UIComponent("npsMinValue")] Button boundNpsMinValue;
        [UIComponent("npsMinInc")] Button boundNpsMinInc;
        [UIComponent("npsMaxDec")] Button boundNpsMaxDec;
        [UIComponent("npsMaxValue")] Button boundNpsMaxValue;
        [UIComponent("npsMaxInc")] Button boundNpsMaxInc;
        [UIComponent("njsMinDec")] Button boundNjsMinDec;
        [UIComponent("njsMinValue")] Button boundNjsMinValue;
        [UIComponent("njsMinInc")] Button boundNjsMinInc;
        [UIComponent("njsMaxDec")] Button boundNjsMaxDec;
        [UIComponent("njsMaxValue")] Button boundNjsMaxValue;
        [UIComponent("njsMaxInc")] Button boundNjsMaxInc;
        [UIComponent("durationMinDec")] Button boundDurationMinDec;
        [UIComponent("durationMinValue")] Button boundDurationMinValue;
        [UIComponent("durationMinInc")] Button boundDurationMinInc;
        [UIComponent("durationMaxDec")] Button boundDurationMaxDec;
        [UIComponent("durationMaxValue")] Button boundDurationMaxValue;
        [UIComponent("durationMaxInc")] Button boundDurationMaxInc;
        [UIComponent("bpmMinDec")] Button boundBpmMinDec;
        [UIComponent("bpmMinValue")] Button boundBpmMinValue;
        [UIComponent("bpmMinInc")] Button boundBpmMinInc;
        [UIComponent("bpmMaxDec")] Button boundBpmMaxDec;
        [UIComponent("bpmMaxValue")] Button boundBpmMaxValue;
        [UIComponent("bpmMaxInc")] Button boundBpmMaxInc;
        [UIComponent("notesMinDec")] Button boundNotesMinDec;
        [UIComponent("notesMinValue")] Button boundNotesMinValue;
        [UIComponent("notesMinInc")] Button boundNotesMinInc;
        [UIComponent("notesMaxDec")] Button boundNotesMaxDec;
        [UIComponent("notesMaxValue")] Button boundNotesMaxValue;
        [UIComponent("notesMaxInc")] Button boundNotesMaxInc;
        [UIComponent("statusText")] TMP_Text boundStatusText;
        [UIComponent("rangeTitle")] TMP_Text boundRangeTitle;
        [UIComponent("rangeMinimum")] Button boundRangeMinimum;
        [UIComponent("rangeMaximum")] Button boundRangeMaximum;
        [UIComponent("rangeMinimumError")] TMP_Text boundRangeMinimumError;
        [UIComponent("rangeMaximumError")] TMP_Text boundRangeMaximumError;
        [UIComponent("rangeError")] TMP_Text boundRangeError;
        [UIObject("rangeActions")] GameObject boundRangeActions;
        [UIObject("numericKeypad")] GameObject boundNumericKeypad;
        [UIComponent("keypadDecimal")] Button boundKeypadDecimal;
        [UIComponent("presetNameKeyboard")] ModalKeyboard boundPresetNameKeyboard;
        [UIComponent("presetName")] TMP_Text boundPresetName;
        [UIObject("presetOptions")] GameObject boundPresetOptions;
        [UIComponent("matchCount")] TMP_Text boundMatchCount;
        [UIComponent("retryButton")] Button boundRetryButton;
        [UIComponent("resetButton")] Button boundResetButton;
        [UIComponent("cancelButton")] Button boundCancelButton;
        [UIComponent("applyButton")] Button boundApplyButton;

        public Panel(Runtime runtime)
        {
            if (runtime == null) throw new ArgumentNullException("runtime");
            this.runtime = runtime;
        }

        public bool IsOpen { get { return isOpen; } }
        public bool CanOpen { get { Vector2 size; return !disposed && screen != null && screenRect != null && TryGetPanelSize(out size); } }

        public FilterSettings Draft { get { return editSession == null ? null : editSession.Draft; } }
        public int Revision { get { return editSession == null ? -1 : editSession.Revision; } }

        public void Reset() { ResetDraft(); }

        public void Load(FilterSettings settings)
        {
            if (!CanEditDraft() || settings == null) return;
            try { editSession.Load(settings); selectedPresetId = null; selectedPresetRevision = -1; UpdateUi(); } catch (Exception ex) { Report("Could not load preset", ex); }
        }

        public void LoadPreset(string id) { LoadPresetById(id); }

        public void SetMode(string mode)
        {
            if (!CanEditDraft()) return;
            try { editSession.SetMode(mode); UpdateUi(); } catch (Exception ex) { Report("Could not select mode", ex); }
        }

        public void ToggleDifficulty(int difficulty)
        {
            if (!CanEditDraft()) return;
            try { editSession.ToggleDifficulty(difficulty); UpdateUi(); } catch (Exception ex) { Report("Could not select difficulty", ex); }
        }

        public bool AdjustBound(Metric metric, bool isMinimum, int direction)
        {
            if (!CanEditDraft()) return false;
            try
            {
                if (rangeSubedit && rangeSession != null && rangeSession.Metric == metric)
                {
                    bool changedScratch = AdjustRangeScratch(isMinimum, direction);
                    UpdateRangeUi();
                    return changedScratch;
                }
                bool changed = editSession.AdjustBound(metric, isMinimum, direction);
                UpdateUi();
                return changed;
            }
            catch (Exception ex) { Report("Could not adjust range", ex); return false; }
        }

        public void SetRange(Metric metric, NumericRange range)
        {
            if (!CanEditDraft() || range == null) return;
            try
            {
                if (rangeSubedit && rangeSession != null && rangeSession.Metric == metric)
                {
                    rangeSession.MinimumText = FilterEditing.FormatInput(metric, range.Min);
                    rangeSession.MaximumText = FilterEditing.FormatInput(metric, range.Max);
                    UpdateRangeUi();
                }
                else { editSession.SetRange(metric, range); UpdateUi(); }
            }
            catch (Exception ex) { Report("Could not set range", ex); }
        }

        public void SetRange(Metric metric, double minimum, double maximum) { SetRange(metric, new NumericRange { Min = minimum, Max = maximum }); }

        bool AdjustRangeScratch(bool isMinimum, int direction)
        {
            if (direction != -1 && direction != 1) return false;
            double? current;
            if (!FilterEditing.TryParse(rangeMetric, isMinimum ? rangeSession.MinimumText : rangeSession.MaximumText, out current)) return false;
            if (!current.HasValue && direction < 0) return false;
            double next = (current.HasValue ? current.Value : 0d) + FilterEditing.Step(rangeMetric) * direction;
            if (next < 0 || !NumericRange.IsFinite(next)) return false;
            double? other;
            if (isMinimum)
            {
                FilterEditing.TryParse(rangeMetric, rangeSession.MaximumText, out other);
                if (other.HasValue && next > other.Value) next = other.Value;
                if (current.HasValue && next == current.Value) return false;
                rangeSession.MinimumText = FilterEditing.FormatInput(rangeMetric, next);
            }
            else
            {
                FilterEditing.TryParse(rangeMetric, rangeSession.MinimumText, out other);
                if (other.HasValue && next < other.Value) next = other.Value;
                if (current.HasValue && next == current.Value) return false;
                rangeSession.MaximumText = FilterEditing.FormatInput(rangeMetric, next);
            }
            return true;
        }

        [UIValue("ScopeLabels")]
        public string[] ScopeLabels
        {
            get
            {
                int active = 0;
                FilterSettings value = editSession == null ? null : editSession.Draft;
                if (value != null)
                {
                    if (value.ExcludeOfficial) active++;
                    if (value.Bpm != null && value.Bpm.IsSet) active++;
                    if (value.Notes != null && value.Notes.IsSet) active++;
                }
                return new[] { "Common", active == 0 ? "More" : "More (" + active.ToString(CultureInfo.InvariantCulture) + ")" };
            }
        }

        [UIValue("ExcludeOfficial")]
        public bool ExcludeOfficial
        {
            get { return editSession != null && editSession.Draft.ExcludeOfficial; }
            set
            {
                if (!CanEditDraft() || rangeSubedit || presetSubedit || keyboardSubedit
                    || editSession.Draft.ExcludeOfficial == value) return;
                editSession.SetExcludeOfficial(value);
                UpdateUi();
            }
        }

        [UIValue("DifficultyLabels")]
        public string[] DifficultyLabels { get { return new[] { "Easy", "Normal", "Hard", "Expert", "Expert+" }; } }

        [UIValue("ModeIconItems")]
        public HMUI.IconSegmentedControl.DataItem[] ModeIconItems { get { return modeIconItems; } }

        [UIValue("PresetLabels")]
        public string[] PresetLabels
        {
            get
            {
                List<string> labels = new List<string> { "Current" };
                PresetCollection collection = runtime.Presets;
                if (collection != null && collection.Presets != null)
                {
                    labels.AddRange(collection.Presets.Where(x => x != null).Select(x => x.Name).ToArray());
                }
                return labels.ToArray();
            }
        }

        public void BindToolbar(RectTransform toolbar)
        {
            try
            {
                if (disposed || toolbar == null) return;
                toolbarRoot = toolbar;
                toolbarSlot = ResolveToolbarSlot(toolbar);
                screen = FindStableScreen(toolbar);
                screenRect = screen == null ? null : screen.GetComponent<RectTransform>();
                EnsureEntry();
                PlaceEntry();
                UpdateEntryText();
                InvalidateLayout();
            }
            catch (Exception ex) { Report("Could not create filter entry", ex); }
        }

        public void InvalidateLayout()
        {
            try
            {
                if (disposed || !isOpen || measureCoroutine != null) return;
                if (runtime.CentralTransitioning)
                {
                    SetInteractive(false);
                    if (transitionCoroutine == null) transitionCoroutine = runtime.StartCoroutine(WaitForCentralTransition());
                }
                measureCoroutine = runtime.StartCoroutine(MeasureNextFrame());
            }
            catch (Exception ex) { Report("Could not update filter panel layout", ex); }
        }

        public void UpdateStatus()
        {
            try
            {
                UpdateEntryText();
                if (isOpen) UpdateUi();
            }
            catch (Exception ex) { Report("Could not update filter panel state", ex); }
        }

        public void Close()
        {
            if (disposed) return;
            StopCoroutine(ref measureCoroutine);
            StopCoroutine(ref transitionCoroutine);
            try { runtime.EndPreview(); } catch (Exception ex) { Report("Could not finish filter preview", ex); }
            HideRangeEditor();
            HidePresetEditors();
            isOpen = false;
            interactive = false;
            editSession = null;
            selectedPresetId = null;
            selectedPresetRevision = -1;
            localStatus = null;
            if (filterModal != null)
            {
                try
                {
                    if (filterModal.isShown) filterModal.Hide(false, null);
                    else filterModal.gameObject.SetActive(false);
                }
                catch (Exception ex) { Report("Could not close filter panel", ex); }
            }
            if (toolbarEntry != null) toolbarEntry.interactable = true;
            if (bottomEntry != null) bottomEntry.interactable = true;
            UpdateEntryText();
        }

        public void Dispose()
        {
            if (disposed) return;
            Close();
            disposed = true;
            if (filterModal != null)
            {
                try { filterModal.blockerClickedEvent -= CancelDraft; } catch (Exception ex) { Report("Could not remove filter panel events", ex); }
            }
            if (layoutWatcher != null) layoutWatcher.Owner = null;
            DestroyObject(ownedUiRoot);
            DestroyObject(toolbarEntryHost);
            if (bottomEntryHost != toolbarEntryHost) DestroyObject(bottomEntryHost);
            ownedUiRoot = null;
            layoutWatcher = null;
            filterModal = null;
            toolbarEntry = null;
            bottomEntry = null;
        }

        [UIAction("Open")]
        public void Open()
        {
            try
            {
                if (disposed || isOpen || !runtime.MenuActive) return;
                EnsureScreenAndView();
                editSession = new FilterEditSession(runtime.Applied);
                requestedRevision = -1;
                selectedPresetId = null;
                selectedPresetRevision = -1;
                scopeIndex = 0;
                isOpen = true;
                interactive = false;
                filterModal.gameObject.SetActive(true);
                filterModal.Show(true, true, null);
                if (toolbarEntry != null) toolbarEntry.interactable = false;
                if (bottomEntry != null) bottomEntry.interactable = false;
                if (transitionCoroutine != null) runtime.StopCoroutine(transitionCoroutine);
                transitionCoroutine = runtime.StartCoroutine(WaitForCentralTransition());
                InvalidateLayout();
                UpdateUi();
            }
            catch (Exception ex)
            {
                Report("Could not open filter panel", ex);
                Close();
            }
        }

        [UIAction("CancelDraft")]
        public void CancelDraft() { if (isOpen) Close(); }

        [UIAction("ApplyDraft")]
        public void ApplyDraft()
        {
            if (!CanEditDraft() || rangeSubedit || presetSubedit || keyboardSubedit) return;
            try
            {
                FilterSettings value = editSession.Draft.Normalize();
                FilterPreview preview = runtime.Preview;
                if (preview == null || !preview.CanApply || runtime.IsBusy && !PreviewReadyFor(value))
                {
                    UpdateUi();
                    return;
                }
                if (runtime.Commit(value)) Close();
                else UpdateUi();
            }
            catch (Exception ex) { Report("Could not apply filters", ex); localStatus = "Apply failed"; UpdateUi(); }
        }

        [UIAction("ResetDraft")]
        public void ResetDraft()
        {
            if (!CanEditDraft()) return;
            try { editSession.Reset(); selectedPresetId = null; selectedPresetRevision = -1; localStatus = null; ClearRangeError(); UpdateUi(); }
            catch (Exception ex) { Report("Could not reset filter draft", ex); }
        }

        [UIAction("ClearDraft")]
        public void ClearDraft() { ResetDraft(); }

        [UIAction("RetryStatus")]
        public void RetryStatus()
        {
            // Runtime.CanRetry already carries the category-specific busy
            // rule. A statistics retry may be offered while a new preview is
            // being requested, while persistence retries must remain usable.
            if (!runtime.CanRetry) return;
            try
            {
                string category = runtime.ErrorCategory;
                if (String.Equals(category, "configuration-write", StringComparison.Ordinal)) ApplyDraft();
                else
                {
                    bool retried = runtime.Retry();
                    if (retried) localStatus = null;
                    if (retried && String.Equals(category, "presets-write", StringComparison.Ordinal)
                        && !String.IsNullOrEmpty(runtime.LastSavedPresetId)
                        && PresetMatchesDraft(runtime.LastSavedPresetId))
                    {
                        selectedPresetId = runtime.LastSavedPresetId;
                        selectedPresetRevision = editSession == null ? -1 : editSession.Revision;
                    }
                }
                UpdateUi();
            }
            catch (Exception ex) { Report("Retry failed", ex); }
        }

        [UIAction("ScopeSelected")]
        public void ScopeSelected(HMUI.SegmentedControl control, int index)
        {
            if (syncingSelectors || !CanEditDraft()) return;
            scopeIndex = Mathf.Clamp(index, 0, 1);
            UpdateUi();
        }

        [UIAction("ModeSelected")]
        public void ModeSelected(HMUI.SegmentedControl control, int index)
        {
            if (syncingSelectors || !CanEditDraft() || index < 0 || index >= modeValues.Count) return;
            try { editSession.SetMode(ModeModeAt(index)); ClearRangeError(); UpdateUi(); }
            catch (Exception ex) { Report("Could not select mode", ex); }
        }

        [UIAction("SelectAllModes")]
        public void SelectAllModes()
        {
            if (!CanEditDraft()) return;
            try { editSession.SetMode("All"); UpdateUi(); }
            catch (Exception ex) { Report("Could not select mode", ex); }
        }

        [UIAction("DifficultyAll")]
        public void DifficultyAll()
        {
            if (!CanEditDraft()) return;
            try { editSession.SelectAllDifficulties(); UpdateUi(); }
            catch (Exception ex) { Report("Could not select difficulty", ex); }
        }

        [UIAction("Difficulty0")] public void Difficulty0() { ToggleDifficultyFromUi(0); }
        [UIAction("Difficulty1")] public void Difficulty1() { ToggleDifficultyFromUi(1); }
        [UIAction("Difficulty2")] public void Difficulty2() { ToggleDifficultyFromUi(2); }
        [UIAction("Difficulty3")] public void Difficulty3() { ToggleDifficultyFromUi(3); }
        [UIAction("Difficulty4")] public void Difficulty4() { ToggleDifficultyFromUi(4); }

        void ToggleDifficultyFromUi(int difficulty)
        {
            if (!CanEditDraft()) return;
            try { editSession.ToggleDifficulty(difficulty); UpdateUi(); }
            catch (Exception ex) { Report("Could not select difficulty", ex); }
        }

        [UIAction("OpenNpsMin")] public void OpenNpsMin() { OpenRangeEditorThenKeyboard(Metric.Nps, true); }
        [UIAction("OpenNpsMax")] public void OpenNpsMax() { OpenRangeEditorThenKeyboard(Metric.Nps, false); }
        [UIAction("OpenNjsMin")] public void OpenNjsMin() { OpenRangeEditorThenKeyboard(Metric.Njs, true); }
        [UIAction("OpenNjsMax")] public void OpenNjsMax() { OpenRangeEditorThenKeyboard(Metric.Njs, false); }
        [UIAction("OpenDurationMin")] public void OpenDurationMin() { OpenRangeEditorThenKeyboard(Metric.Duration, true); }
        [UIAction("OpenDurationMax")] public void OpenDurationMax() { OpenRangeEditorThenKeyboard(Metric.Duration, false); }
        [UIAction("OpenBpmMin")] public void OpenBpmMin() { OpenRangeEditorThenKeyboard(Metric.Bpm, true); }
        [UIAction("OpenBpmMax")] public void OpenBpmMax() { OpenRangeEditorThenKeyboard(Metric.Bpm, false); }
        [UIAction("OpenNotesMin")] public void OpenNotesMin() { OpenRangeEditorThenKeyboard(Metric.Notes, true); }
        [UIAction("OpenNotesMax")] public void OpenNotesMax() { OpenRangeEditorThenKeyboard(Metric.Notes, false); }

        [UIAction("RangeMinimum")]
        public void RangeMinimum() { OpenRangeKeyboard(true); UpdateRangeUi(); }

        [UIAction("RangeMaximum")]
        public void RangeMaximum() { OpenRangeKeyboard(false); UpdateRangeUi(); }

        [UIAction("Key0")] public void Key0() { KeyInput("0"); }
        [UIAction("Key1")] public void Key1() { KeyInput("1"); }
        [UIAction("Key2")] public void Key2() { KeyInput("2"); }
        [UIAction("Key3")] public void Key3() { KeyInput("3"); }
        [UIAction("Key4")] public void Key4() { KeyInput("4"); }
        [UIAction("Key5")] public void Key5() { KeyInput("5"); }
        [UIAction("Key6")] public void Key6() { KeyInput("6"); }
        [UIAction("Key7")] public void Key7() { KeyInput("7"); }
        [UIAction("Key8")] public void Key8() { KeyInput("8"); }
        [UIAction("Key9")] public void Key9() { KeyInput("9"); }

        [UIAction("KeyDecimal")]
        public void KeyDecimal()
        {
            if (!keyboardSubedit || rangeSession == null || rangeMetric == Metric.Notes) return;
            string separator = rangeMetric == Metric.Duration ? ":" : ".";
            string current = FocusedRangeText();
            if (current.IndexOf(separator, StringComparison.Ordinal) < 0) KeyInput(separator);
        }

        [UIAction("KeyBackspace")]
        public void KeyBackspace()
        {
            if (!keyboardSubedit || rangeSession == null) return;
            string current = FocusedRangeText();
            if (String.IsNullOrEmpty(current)) return;
            SetFocusedRangeText(current.Substring(0, current.Length - 1));
            rangeError = null;
            UpdateKeypadUi();
            UpdateRangeUi();
        }

        [UIAction("KeyClear")]
        public void KeyClear()
        {
            if (!keyboardSubedit || rangeSession == null) return;
            SetFocusedRangeText(String.Empty);
            rangeError = null;
            UpdateKeypadUi();
            UpdateRangeUi();
        }

        [UIAction("KeyUnlimited")]
        public void KeyUnlimited()
        {
            if (!keyboardSubedit || rangeSession == null) return;
            SetFocusedRangeText(String.Empty);
            rangeError = null;
            UpdateKeypadUi();
            UpdateRangeUi();
        }

        void KeyInput(string value)
        {
            if (!keyboardSubedit || rangeSession == null) return;
            SetFocusedRangeText(FocusedRangeText() + (value ?? String.Empty));
            rangeError = null;
            UpdateKeypadUi();
            UpdateRangeUi();
        }

        string FocusedRangeText()
        {
            if (rangeSession == null) return String.Empty;
            return rangeEditingMinimum ? (rangeSession.MinimumText ?? String.Empty) : (rangeSession.MaximumText ?? String.Empty);
        }

        void SetFocusedRangeText(string value)
        {
            if (rangeSession == null) return;
            if (rangeEditingMinimum) rangeSession.MinimumText = value ?? String.Empty;
            else rangeSession.MaximumText = value ?? String.Empty;
        }

        void UpdateKeypadUi()
        {
            if (boundKeypadDecimal != null)
            {
                SetActive(boundKeypadDecimal.gameObject, rangeMetric != Metric.Notes);
                boundKeypadDecimal.interactable = rangeMetric != Metric.Notes;
                SetButtonText(boundKeypadDecimal, rangeMetric == Metric.Duration ? ":" : ".");
            }
        }

        void HideKeypad()
        {
            keyboardSubedit = false;
            if (isOpen && editSession != null) UpdateUiInteractivity(editSession.Draft);
        }

        [UIAction("CancelRange")]
        public void CancelRange() { HideRangeEditor(); if (isOpen) UpdateUi(); }

        [UIAction("ConfirmRange")]
        public void ConfirmRange()
        {
            if (!CanEditDraft() || rangeSession == null) return;
            string error = rangeSession.Validate();
            if (error != null) { rangeError = LocalizeRangeError(error); UpdateRangeUi(); return; }
            try
            {
                editSession.SetRange(rangeMetric, rangeSession.GetRange());
                HideRangeEditor();
                UpdateUi();
            }
            catch (Exception ex) { rangeError = LocalizeRangeError(ex.Message); UpdateRangeUi(); }
        }

        [UIAction("PresetSelected")]
        public void PresetSelected(int index)
        {
            if (!CanEditDraft()) return;
            if (index == 0)
            {
                try { editSession.Load(runtime.Applied); selectedPresetId = null; selectedPresetRevision = -1; ClosePresetSelector(); UpdateUi(); } catch (Exception ex) { Report("Could not load current filters", ex); }
                return;
            }
            if (index < 0) return;
            PresetCollection collection = runtime.Presets;
            if (collection == null || collection.Presets == null) return;
            int presetIndex = index - 1;
            if (presetIndex < 0 || presetIndex >= collection.Presets.Count || collection.Presets[presetIndex] == null) return;
            try
            {
                FilterPreset preset = collection.Presets[presetIndex];
                editSession.Load(preset.Settings);
                selectedPresetId = preset.Id;
                selectedPresetRevision = editSession.Revision;
                ClosePresetSelector();
                UpdateUi();
            }
            catch (Exception ex) { Report("Could not load preset", ex); }
        }

        [UIAction("OpenSavePreset")]
        public void OpenSavePreset()
        {
            if (!CanEditDraft() || !runtime.PresetsAvailable) return;
            renamingPreset = false;
            // Saving a new item always starts with a unique default name;
            // updating the selected item uses its persisted name separately.
            presetNameDraft = runtime.Presets.NextName();
            editingPresetId = null;
            presetSubedit = true;
            ShowPresetPage(boundPresetSavePage);
            UpdateUi();
        }

        [UIAction("SaveAsNewPreset")]
        public void SaveAsNewPreset()
        {
            if (!CanEditDraft() || !runtime.PresetsAvailable) return;
            string name = String.IsNullOrWhiteSpace(presetNameDraft) ? runtime.Presets.NextName() : presetNameDraft.Trim();
            if (name.Length == 0) { localStatus = "Enter a preset name"; UpdateUi(); return; }
            try
            {
                FilterPreset saved = runtime.SavePreset(null, name, editSession.Draft);
                if (saved == null) { UpdateUi(); return; }
                selectedPresetId = saved.Id;
                selectedPresetRevision = editSession.Revision;
                presetNameDraft = saved.Name;
                localStatus = null;
                HidePresetEditors();
                UpdateUi();
            }
            catch (Exception ex) { Report("Could not save preset", ex); UpdateUi(); }
        }

        [UIAction("UpdateCurrentPreset")]
        public void UpdateCurrentPreset()
        {
            if (!CanEditDraft() || !runtime.PresetsAvailable || String.IsNullOrEmpty(selectedPresetId)) return;
            FilterPreset selected = FindPreset(selectedPresetId);
            if (selected == null) return;
            try
            {
                FilterPreset saved = runtime.SavePreset(selected.Id, selected.Name, editSession.Draft);
                if (saved == null) { UpdateUi(); return; }
                selectedPresetId = saved.Id;
                selectedPresetRevision = editSession.Revision;
                presetNameDraft = saved.Name;
                localStatus = null;
                HidePresetEditors();
                UpdateUi();
            }
            catch (Exception ex) { Report("Could not update preset", ex); UpdateUi(); }
        }

        [UIAction("EditPresetName")]
        public void EditPresetName()
        {
            if (!CanEditDraft() || !presetSubedit) return;
            OpenPresetNameKeyboard();
        }

        [UIAction("OpenPresetSelector")]
        public void OpenPresetSelector()
        {
            if (!CanEditDraft() || boundPresetSelectorPage == null) return;
            BuildPresetSelectorOptions();
            presetSubedit = true;
            ShowPresetPage(boundPresetSelectorPage);
            UpdateUi();
        }

        [UIAction("ClosePresetSelector")]
        public void ClosePresetSelector()
        {
            HidePresetEditors();
            if (isOpen) UpdateUi();
        }

        [UIAction("OpenManagePresets")]
        public void OpenManagePresets()
        {
            if (!CanEditDraft() || !runtime.PresetsAvailable || boundPresetManagePage == null) return;
            BuildPresetOptions();
            presetSubedit = true;
            ShowPresetPage(boundPresetManagePage);
            UpdateUi();
        }

        [UIAction("CloseManagePresets")]
        public void CloseManagePresets()
        {
            HidePresetEditors();
            if (isOpen) UpdateUi();
        }

        [UIAction("PresetNameEntered")]
        public void PresetNameEntered(string value)
        {
            presetNameDraft = (value ?? String.Empty).Trim();
            if (boundPresetSaveName != null) boundPresetSaveName.text = presetNameDraft;
            if (boundPresetRenameName != null) boundPresetRenameName.text = presetNameDraft;
            if (boundPresetNameKeyboard != null && boundPresetNameKeyboard.ModalView != null)
            {
                try { boundPresetNameKeyboard.ModalView.blockerClickedEvent -= CancelPresetName; boundPresetNameKeyboard.ModalView.Hide(false, null); } catch (Exception ex) { Report("Could not close preset keyboard", ex); }
            }
            keyboardSubedit = false;
            if (renamingPreset) UpdateUi();
            else { localStatus = null; UpdateUi(); }
        }

        [UIAction("CancelPresetName")]
        public void CancelPresetName()
        {
            keyboardSubedit = false;
            if (boundPresetNameKeyboard != null && boundPresetNameKeyboard.ModalView != null)
            {
                try { boundPresetNameKeyboard.ModalView.blockerClickedEvent -= CancelPresetName; boundPresetNameKeyboard.ModalView.Hide(false, null); } catch (Exception ex) { Report("Could not close preset keyboard", ex); }
            }
            if (renamingPreset)
            {
                renamingPreset = false;
                editingPresetId = null;
                ShowPresetPage(boundPresetManagePage);
                BuildPresetOptions();
                UpdateUi();
            }
        }

        [UIAction("ConfirmDeletePreset")]
        public void ConfirmDeletePreset()
        {
            if (String.IsNullOrEmpty(deletePresetId)) return;
            bool deleted = false;
            try { deleted = runtime.DeletePreset(deletePresetId); }
            catch (Exception ex) { Report("Could not delete preset", ex); }
            if (deleted)
            {
                localStatus = null;
                if (String.Equals(deletePresetId, selectedPresetId, StringComparison.Ordinal)) { selectedPresetId = null; selectedPresetRevision = -1; }
            }
            deletePresetId = null;
            SetActive(boundPresetDeletePage, false);
            BuildPresetOptions();
            ShowPresetPage(boundPresetManagePage);
            UpdateUi();
        }

        [UIAction("CancelDeletePreset")]
        public void CancelDeletePreset()
        {
            deletePresetId = null;
            SetActive(boundPresetDeletePage, false);
            ShowPresetPage(boundPresetManagePage);
            UpdateUi();
        }

        void EnsureScreenAndView()
        {
            if (filterModal != null && panelRoot != null) return;
            if (screen == null && toolbarRoot != null) screen = FindStableScreen(toolbarRoot);
            screenRect = screen == null ? null : screen.GetComponent<RectTransform>();
            Vector2 size;
            if (screen == null || screenRect == null || !TryGetPanelSize(out size))
                throw new InvalidOperationException("AdvancedSongFilters requires a valid HMUI screen");

            ResetBindings();
            ownedUiRoot = new GameObject("AdvancedSongFilters.UI", typeof(RectTransform));
            ownedUiRoot.transform.SetParent(screen.transform, false);
            RectTransform ownedRect = ownedUiRoot.GetComponent<RectTransform>();
            ownedRect.anchorMin = Vector2.zero; ownedRect.anchorMax = Vector2.one; ownedRect.offsetMin = Vector2.zero; ownedRect.offsetMax = Vector2.zero;
            BSMLParser.Instance.Parse(LoadMarkup(), ownedUiRoot, this);
            // Protect user supplied preset names before the first status pass
            // writes a selected name into any TMP label.
            MarkPresetName(boundPresetSelectorButton == null ? null : boundPresetSelectorButton.gameObject);
            MarkPresetName(boundPresetSaveName == null ? null : boundPresetSaveName.gameObject);
            MarkPresetName(boundPresetRenameName == null ? null : boundPresetRenameName.gameObject);
            MarkPresetName(boundPresetDeleteConfirmText == null ? null : boundPresetDeleteConfirmText.gameObject);
            layoutWatcher = ownedUiRoot.GetComponent<LayoutWatcher>();
            if (layoutWatcher == null) layoutWatcher = ownedUiRoot.AddComponent<LayoutWatcher>();
            layoutWatcher.Owner = this;
            filterModal = boundFilterModal;
            panelRoot = boundPanelRoot;
            var scrollView = boundScrollContainer == null ? null :
                boundScrollContainer.GetComponentInParent<BSMLScrollableContainer>(true);
            if (scrollView == null) throw new InvalidOperationException("Filter scroll container is unavailable");
            boundScrollContainer = scrollView.gameObject;
            if (filterModal == null || panelRoot == null || boundScrollContainer == null || boundFormContent == null
                || boundModeSelector == null || boundScopeSelector == null || boundPresetSelectorButton == null
                || boundMainPage == null || boundRangePage == null)
                throw new InvalidOperationException("Panel.bsml did not bind the native modal controls");
            filterModal.blockerClickedEvent -= CancelDraft;
            filterModal.blockerClickedEvent += CancelDraft;
            ConfigurePanelLayout(size);
            ConfigureNativeControls();
            BuildModeItems();
            HideRangeEditor();
            HidePresetEditors();
            filterModal.gameObject.SetActive(false);
            UpdateUi();
        }

        void ConfigurePanelLayout(Vector2 size)
        {
            RectTransform modalRect = filterModal.GetComponent<RectTransform>();
            modalRect.anchorMin = new Vector2(0.5f, 0.5f); modalRect.anchorMax = new Vector2(0.5f, 0.5f); modalRect.pivot = new Vector2(0.5f, 0.5f);
            modalRect.localScale = Vector3.one; modalRect.sizeDelta = size;
            RectTransform root = EnsureRect(panelRoot);
            root.anchorMin = Vector2.zero; root.anchorMax = Vector2.one; root.offsetMin = Vector2.zero; root.offsetMax = Vector2.zero;
            LayoutElement rootLayout = EnsureLayout(panelRoot);
            rootLayout.minWidth = size.x; rootLayout.preferredWidth = size.x; rootLayout.minHeight = size.y; rootLayout.preferredHeight = size.y;
            rootLayout.flexibleWidth = 0f; rootLayout.flexibleHeight = 0f;
            SetLayout(panelRoot, false, 1f, TextAnchor.UpperCenter);
            SetFixedHeight(boundMainPage, size.y);
            SetFixedHeight(boundRangePage, size.y);
            SetFixedHeight(boundPresetSelectorPage, size.y);
            SetFixedHeight(boundPresetManagePage, size.y);
            SetFixedHeight(boundPresetSavePage, size.y);
            SetFixedHeight(boundPresetRenamePage, size.y);
            SetFixedHeight(boundPresetDeletePage, size.y);
            SetLayout(boundMainPage, false, 1f, TextAnchor.UpperCenter);
            SetFixedHeight(boundHeader, MinimumControl);
            SetFixedWidth(boundHeader, size.x);
            SetLayout(boundHeader, true, 1f, TextAnchor.MiddleLeft);
            SetFixedWidth(boundTitle.gameObject, 24f);
            SetFlexibleSpace(boundHeaderSpacer);
            SetFixedWidth(boundFooter, size.x);
            SetLayout(boundFooter, true, 1f, TextAnchor.MiddleLeft);
            SetFixedWidth(boundMatchCount.gameObject, 62f);
            SetFlexibleSpace(boundFooterSpacer);
            SetFixedWidth(boundScopeRow, size.x);
            SetLayout(boundScopeRow, true, 0f, TextAnchor.MiddleCenter);
            SetFixedWidth(boundScopeSelector.gameObject, 64f);
            SetLayout(boundRangePage, false, 0f, TextAnchor.UpperCenter);
            SetFixedWidth(boundRangeTitle.gameObject, size.x - 4f);
            SetLayout(boundNumericKeypad, false, 0f, TextAnchor.UpperCenter);
            SetFixedWidth(boundNumericKeypad, 128f);
            for (int i = 0; i < boundNumericKeypad.transform.childCount; i++)
            {
                GameObject row = boundNumericKeypad.transform.GetChild(i).gameObject;
                SetFixedWidth(row, 128f);
                SetLayout(row, true, 1f, TextAnchor.MiddleCenter);
                for (int c = 0; c < row.transform.childCount; c++)
                    SetFixedWidth(row.transform.GetChild(c).gameObject, 42f);
            }
            GameObject[] presetPages = { boundPresetSelectorPage, boundPresetManagePage,
                boundPresetSavePage, boundPresetRenamePage, boundPresetDeletePage };
            foreach (GameObject page in presetPages)
            {
                if (page == null) continue;
                EnsureLayout(page).ignoreLayout = true;
                RectTransform pageRect = EnsureRect(page);
                pageRect.anchorMin = Vector2.zero;
                pageRect.anchorMax = Vector2.one;
                pageRect.offsetMin = Vector2.zero;
                pageRect.offsetMax = Vector2.zero;
                SetLayout(page, false, 1f, TextAnchor.UpperCenter);
                if (page.transform.childCount == 0) continue;
                GameObject header = page.transform.GetChild(0).gameObject;
                SetFixedWidth(header, size.x - 4f);
                SetFixedHeight(header, MinimumControl);
                SetLayout(header, true, 1f, TextAnchor.MiddleLeft);
                if (header.transform.childCount > 1)
                    SetFixedWidth(header.transform.GetChild(1).gameObject, size.x - 24f);
            }
            ConfigurePresetScroll(boundPresetSelectorOptions, boundPresetSelectorStatus, size);
            ConfigurePresetScroll(boundPresetOptions, boundPresetManageStatus, size);
            SetFixedHeight(boundSelectionSection, 19f);
            SetFixedHeight(boundScopeRow, MinimumControl);
            SetFixedHeight(boundStatusText == null ? null : boundStatusText.gameObject, MinimumControl);
            LayoutElement scroll = EnsureLayout(boundScrollContainer);
            scroll.minWidth = size.x; scroll.preferredWidth = size.x; scroll.flexibleHeight = 1f;
            float fixedHeight = MinimumControl * 3f + 18f + 5f;
            if (boundStatusText != null && boundStatusText.gameObject.activeSelf) fixedHeight += MinimumControl + 1f;
            scroll.minHeight = Mathf.Max(MinimumControl, size.y - fixedHeight);
            scroll.preferredHeight = scroll.minHeight;
            var wrapper = boundScrollContainer.GetComponent<BSMLScrollableContainer>().ContentRect;
            wrapper.anchorMin = new Vector2(0f, 1f); wrapper.anchorMax = new Vector2(1f, 1f);
            wrapper.pivot = new Vector2(0.5f, 1f); wrapper.sizeDelta = new Vector2(0f, wrapper.sizeDelta.y);
            SetLayout(wrapper.gameObject, false, 1f, TextAnchor.UpperCenter);
            var bodyLayout = EnsureLayout(boundFormContent);
            bodyLayout.minWidth = size.x; bodyLayout.preferredWidth = size.x;
            bodyLayout.minHeight = 0f; bodyLayout.preferredHeight = -1f; bodyLayout.flexibleHeight = 0f;
            var bodyFitter = boundFormContent.GetComponent<ContentSizeFitter>();
            if (bodyFitter == null) bodyFitter = boundFormContent.AddComponent<ContentSizeFitter>();
            bodyFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            bodyFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            SetLayout(boundFormContent, false, 0f, TextAnchor.UpperCenter);
            SetFixedHeight(boundFooter, MinimumControl);
            ConfigureRows();
            if (boundExcludeOfficial != null)
            {
                SetFixedHeight(boundExcludeOfficial.gameObject, MinimumControl);
                SetFixedWidth(boundExcludeOfficial.gameObject, 140f);
                RectTransform toggleRect = boundExcludeOfficial.Toggle.GetComponent<RectTransform>();
                toggleRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, MinimumControl);
            }
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(root);
        }

        void ConfigureRows()
        {
            ConfigureMetricHeader(boundMetricHeader);
            ConfigureMetricHeader(boundMoreMetricHeader);
            GameObject[] rows = { boundNpsRow, boundNjsRow, boundDurationRow, boundBpmRow, boundNotesRow };
            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i] == null) continue;
                SetFixedHeight(rows[i], MinimumControl);
                SetFixedWidth(rows[i], 140f);
                SetLayout(rows[i], true, 1f, TextAnchor.MiddleCenter);
                if (rows[i].transform.childCount >= 3)
                {
                    SetFixedWidth(rows[i].transform.GetChild(0).gameObject, 20f);
                    SetFixedWidth(rows[i].transform.GetChild(1).gameObject, 58f);
                    SetFixedWidth(rows[i].transform.GetChild(2).gameObject, 58f);
                }
            }
            if (boundCommonPage != null) SetLayout(boundCommonPage, false, 0f, TextAnchor.UpperCenter);
            if (boundMorePage != null) SetLayout(boundMorePage, false, 0f, TextAnchor.UpperCenter);
        }

        void ConfigureMetricHeader(GameObject header)
        {
            if (header == null) return;
            SetFixedWidth(header, 140f);
            SetLayout(header, true, 1f, TextAnchor.MiddleCenter);
            for (int i = 0; i < header.transform.childCount; i++)
                SetFixedWidth(header.transform.GetChild(i).gameObject, i == 0 ? 20f : 58f);
        }

        static void SetFixedWidth(GameObject target, float width)
        {
            if (target == null) return;
            LayoutElement layout = EnsureLayout(target);
            layout.minWidth = width;
            layout.preferredWidth = width;
            layout.flexibleWidth = 0f;
        }

        static void SetFlexibleSpace(GameObject target)
        {
            if (target == null) return;
            LayoutElement layout = EnsureLayout(target);
            layout.minWidth = 0f;
            layout.preferredWidth = 0f;
            layout.flexibleWidth = 1f;
        }

        void ConfigurePresetScroll(GameObject content, TMP_Text status, Vector2 size)
        {
            if (content == null) return;
            var container = content.GetComponentInParent<BSMLScrollableContainer>(true);
            if (container == null) return;
            float height = size.y - MinimumControl - 3f;
            if (status != null && status.gameObject.activeSelf) height -= 6f;
            SetFixedHeight(container.gameObject, Mathf.Max(MinimumControl, height));
            SetFixedWidth(container.gameObject, size.x - 4f);
            RectTransform wrapper = container.ContentRect;
            wrapper.anchorMin = new Vector2(0f, 1f);
            wrapper.anchorMax = new Vector2(1f, 1f);
            wrapper.pivot = new Vector2(0.5f, 1f);
            wrapper.sizeDelta = new Vector2(0f, wrapper.sizeDelta.y);
            SetLayout(wrapper.gameObject, false, 0f, TextAnchor.UpperCenter);
            SetFixedWidth(content, size.x - 6f);
            SetLayout(content, false, 1f, TextAnchor.UpperCenter);
            ContentSizeFitter fitter = content.GetComponent<ContentSizeFitter>();
            if (fitter == null) fitter = content.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        void ConfigureNativeControls()
        {
            BindBoundButtons(Metric.Nps, true, boundNpsMinDec, boundNpsMinValue, boundNpsMinInc);
            BindBoundButtons(Metric.Nps, false, boundNpsMaxDec, boundNpsMaxValue, boundNpsMaxInc);
            BindBoundButtons(Metric.Njs, true, boundNjsMinDec, boundNjsMinValue, boundNjsMinInc);
            BindBoundButtons(Metric.Njs, false, boundNjsMaxDec, boundNjsMaxValue, boundNjsMaxInc);
            BindBoundButtons(Metric.Duration, true, boundDurationMinDec, boundDurationMinValue, boundDurationMinInc);
            BindBoundButtons(Metric.Duration, false, boundDurationMaxDec, boundDurationMaxValue, boundDurationMaxInc);
            BindBoundButtons(Metric.Bpm, true, boundBpmMinDec, boundBpmMinValue, boundBpmMinInc);
            BindBoundButtons(Metric.Bpm, false, boundBpmMaxDec, boundBpmMaxValue, boundBpmMaxInc);
            BindBoundButtons(Metric.Notes, true, boundNotesMinDec, boundNotesMinValue, boundNotesMinInc);
            BindBoundButtons(Metric.Notes, false, boundNotesMaxDec, boundNotesMaxValue, boundNotesMaxInc);
            RememberNativeColor(boundDifficultyAll); RememberNativeColor(boundDifficulty0); RememberNativeColor(boundDifficulty1); RememberNativeColor(boundDifficulty2); RememberNativeColor(boundDifficulty3); RememberNativeColor(boundDifficulty4); RememberNativeColor(boundAllModesButton);
            if (boundScopeSelector != null) boundScopeSelector.SelectCellWithNumber(0);
            SetButtonSize(allButtons.ToArray());
            SetButtonSize(new[] { boundNpsMinDec, boundNpsMinValue, boundNpsMinInc, boundNpsMaxDec, boundNpsMaxValue, boundNpsMaxInc,
                boundNjsMinDec, boundNjsMinValue, boundNjsMinInc, boundNjsMaxDec, boundNjsMaxValue, boundNjsMaxInc,
                boundDurationMinDec, boundDurationMinValue, boundDurationMinInc, boundDurationMaxDec, boundDurationMaxValue, boundDurationMaxInc,
                boundBpmMinDec, boundBpmMinValue, boundBpmMinInc, boundBpmMaxDec, boundBpmMaxValue, boundBpmMaxInc,
                boundNotesMinDec, boundNotesMinValue, boundNotesMinInc, boundNotesMaxDec, boundNotesMaxValue, boundNotesMaxInc });
        }

        void BindBoundButtons(Metric metric, bool isMinimum, Button decrement, Button value, Button increment)
        {
            if (decrement != null)
            {
                decrement.onClick.RemoveAllListeners(); decrement.onClick.AddListener(delegate { AdjustBoundFromUi(metric, isMinimum, -1); }); RememberNativeColor(decrement);
            }
            if (increment != null)
            {
                increment.onClick.RemoveAllListeners(); increment.onClick.AddListener(delegate { AdjustBoundFromUi(metric, isMinimum, 1); }); RememberNativeColor(increment);
            }
            if (value != null)
            {
                value.onClick.RemoveAllListeners(); value.onClick.AddListener(delegate { OpenRangeEditorThenKeyboard(metric, isMinimum); }); RememberNativeColor(value);
            }
        }

        void AdjustBoundFromUi(Metric metric, bool isMinimum, int direction)
        {
            if (!CanEditDraft() || rangeSubedit || presetSubedit || keyboardSubedit) return;
            try { editSession.AdjustBound(metric, isMinimum, direction); UpdateUi(); }
            catch (Exception ex) { Report("Could not adjust range", ex); }
        }

        void OpenRangeEditor(Metric metric, bool focusMinimum)
        {
            if (!CanEditDraft() || boundRangePage == null) return;
            try
            {
                rangeMetric = metric; rangeEditingMinimum = focusMinimum; rangeSession = new RangeEditSession(editSession.Draft, metric); rangeError = null; rangeSubedit = true; keyboardSubedit = false;
                SetActive(boundMainPage, false);
                SetActive(boundRangePage, true);
                OpenRangeKeyboard(focusMinimum);
                UpdateRangeUi(); UpdateUiInteractivity(editSession.Draft);
            }
            catch (Exception ex) { Report("Could not open range editor", ex); rangeSubedit = false; }
        }

        void OpenRangeEditorThenKeyboard(Metric metric, bool focusMinimum)
        {
            OpenRangeEditor(metric, focusMinimum);
            if (rangeSubedit) OpenRangeKeyboard(focusMinimum);
        }

        void OpenRangeKeyboard(bool minimum)
        {
            if (!rangeSubedit || rangeSession == null) return;
            rangeEditingMinimum = minimum; keyboardSubedit = true;
            rangeError = null;
            UpdateKeypadUi();
            UpdateRangeUi();
            if (isOpen && editSession != null) UpdateUiInteractivity(editSession.Draft);
        }

        void UpdateRangeUi()
        {
            if (!rangeSubedit || rangeSession == null) return;
            if (boundRangeTitle != null) boundRangeTitle.text = MetricLabel(rangeMetric);
            SetButtonText(boundRangeMinimum, RangeFieldLabel(rangeSession.MinimumText, rangeEditingMinimum));
            SetButtonText(boundRangeMaximum, RangeFieldLabel(rangeSession.MaximumText, !rangeEditingMinimum));
            SetSelected(boundRangeMinimum, rangeEditingMinimum);
            SetSelected(boundRangeMaximum, !rangeEditingMinimum);
            double? minimum;
            double? maximum;
            bool minimumValid = FilterEditing.TryParse(rangeMetric, rangeSession.MinimumText, out minimum);
            bool maximumValid = FilterEditing.TryParse(rangeMetric, rangeSession.MaximumText, out maximum);
            SetInlineRangeError(boundRangeMinimumError, minimumValid ? String.Empty : "Invalid value");
            SetInlineRangeError(boundRangeMaximumError, maximumValid ? String.Empty : "Invalid value");
            string orderError = minimumValid && maximumValid && minimum.HasValue && maximum.HasValue && maximum.Value < minimum.Value
                ? "Maximum must not be below minimum" : String.Empty;
            string visibleError = String.IsNullOrEmpty(rangeError) ? orderError : rangeError;
            if (boundRangeError != null) { boundRangeError.text = visibleError ?? String.Empty; boundRangeError.gameObject.SetActive(!String.IsNullOrEmpty(visibleError)); }
        }

        void SetInlineRangeError(TMP_Text label, string value)
        {
            if (label == null) return;
            bool show = !String.IsNullOrEmpty(value);
            label.text = show ? value : String.Empty;
            label.gameObject.SetActive(show);
        }

        static string RangeFieldLabel(string value, bool focused)
        {
            string label = String.IsNullOrEmpty(value) ? "Any" : value;
            return focused ? "<color=#6dd9ff>› " + label + "</color>" : label;
        }

        void HideRangeEditor()
        {
            HideKeypad();
            rangeSubedit = false; rangeSession = null; rangeError = null;
            SetActive(boundRangePage, false);
            SetActive(boundMainPage, true);
        }

        void ClearRangeError() { rangeError = null; if (boundRangeError != null) boundRangeError.gameObject.SetActive(false); }

        void OpenPresetNameKeyboard()
        {
            if (boundPresetNameKeyboard == null) return;
            keyboardSubedit = true; presetSubedit = true;
            if (boundPresetName != null) boundPresetName.text = presetNameDraft ?? String.Empty;
            try
            {
                boundPresetNameKeyboard.SetText(presetNameDraft ?? String.Empty);
                ProtectPresetKeyboardName();
                if (boundPresetNameKeyboard.ModalView != null)
                {
                    boundPresetNameKeyboard.ModalView.blockerClickedEvent -= CancelPresetName;
                    boundPresetNameKeyboard.ModalView.blockerClickedEvent += CancelPresetName;
                    boundPresetNameKeyboard.ModalView.Show(true, true, null);
                }
                ProtectPresetKeyboardName();
            }
            catch (Exception ex) { keyboardSubedit = false; Report("Could not open preset keyboard", ex); }
        }

        [UIAction("SaveOrRenamePreset")]
        void SaveOrRenamePreset()
        {
            if (editSession == null) return;
            if (String.IsNullOrWhiteSpace(presetNameDraft)) { localStatus = "Enter a preset name"; UpdateUi(); return; }
            try
            {
                if (renamingPreset)
                {
                    bool renamed = !String.IsNullOrEmpty(editingPresetId) && runtime.RenamePreset(editingPresetId, presetNameDraft.Trim());
                    if (renamed)
                    {
                        renamingPreset = false;
                        editingPresetId = null;
                        localStatus = null;
                        BuildPresetOptions();
                        ShowPresetPage(boundPresetManagePage);
                        UpdateUi();
                    }
                    else UpdateUi();
                    return;
                }
                SaveAsNewPreset();
            }
            catch (Exception ex) { Report("Could not save preset", ex); UpdateUi(); }
        }

        void ShowPresetPage(GameObject page)
        {
            SetActive(boundMainPage, page == null);
            SetActive(boundRangePage, false);
            SetActive(boundPresetSelectorPage, page == boundPresetSelectorPage);
            SetActive(boundPresetManagePage, page == boundPresetManagePage);
            SetActive(boundPresetSavePage, page == boundPresetSavePage);
            SetActive(boundPresetRenamePage, page == boundPresetRenamePage);
            SetActive(boundPresetDeletePage, page == boundPresetDeletePage);
            if (page != null) SetActive(page, true);
            InvalidateLayout();
        }

        void UpdatePresetEditorUi()
        {
            if (boundPresetSaveName != null) boundPresetSaveName.text = presetNameDraft ?? String.Empty;
            if (boundPresetRenameName != null) boundPresetRenameName.text = presetNameDraft ?? String.Empty;
            FilterPreset selected = FindPreset(selectedPresetId);
            if (boundUpdateCurrentButton != null) boundUpdateCurrentButton.interactable = selected != null && !keyboardSubedit;
            if (boundSaveNewButton != null) boundSaveNewButton.interactable = !keyboardSubedit;
            if (boundPresetSaveTitle != null) boundPresetSaveTitle.text = "Save Preset";
            if (boundPresetRenameTitle != null) boundPresetRenameTitle.text = "Rename Preset";
        }

        void BuildPresetSelectorOptions()
        {
            if (boundPresetSelectorOptions == null) return;
            List<GameObject> old = new List<GameObject>();
            for (int i = 0; i < boundPresetSelectorOptions.transform.childCount; i++) old.Add(boundPresetSelectorOptions.transform.GetChild(i).gameObject);
            for (int i = 0; i < old.Count; i++) DestroyObject(old[i]);
            GameObject currentRow = new GameObject("PresetSelectorCurrent", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            currentRow.transform.SetParent(boundPresetSelectorOptions.transform, false);
            SetLayout(currentRow, true, 1f, TextAnchor.MiddleCenter); SetFixedHeight(currentRow, MinimumControl);
            Button current = CreateButtonInHost(currentRow, "Current");
            if (current != null) current.onClick.AddListener(delegate { PresetSelected(0); });
            PresetCollection collection = runtime.Presets;
            if (collection == null || collection.Presets == null) return;
            for (int i = 0; i < collection.Presets.Count; i++)
            {
                FilterPreset preset = collection.Presets[i];
                if (preset == null) continue;
                int index = i + 1;
                GameObject row = new GameObject("PresetSelectorRow_" + preset.Id, typeof(RectTransform), typeof(HorizontalLayoutGroup));
                row.transform.SetParent(boundPresetSelectorOptions.transform, false);
                SetLayout(row, true, 1f, TextAnchor.MiddleCenter); SetFixedHeight(row, MinimumControl);
                Button choice = CreateButtonInHost(row, "PresetName");
                if (choice == null) continue;
                MarkPresetName(choice.gameObject);
                SetButtonText(choice, preset.Name);
                SetFlexibleButtonWidth(choice, 1f, 24f, 70f);
                choice.onClick.AddListener(delegate { PresetSelected(index); });
            }
            SetButtonSize(boundPresetSelectorOptions.GetComponentsInChildren<Button>(true));
        }

        void BuildPresetOptions()
        {
            if (boundPresetOptions == null) return;
            presetButtons.Clear();
            List<GameObject> old = new List<GameObject>();
            for (int i = 0; i < boundPresetOptions.transform.childCount; i++) old.Add(boundPresetOptions.transform.GetChild(i).gameObject);
            for (int i = 0; i < old.Count; i++) DestroyObject(old[i]);
            PresetCollection collection = runtime.Presets;
            if (collection == null || collection.Presets == null) return;
            for (int i = 0; i < collection.Presets.Count; i++)
            {
                FilterPreset preset = collection.Presets[i];
                if (preset == null) continue;
                GameObject row = new GameObject("PresetRow_" + preset.Id, typeof(RectTransform), typeof(HorizontalLayoutGroup)); row.transform.SetParent(boundPresetOptions.transform, false); SetLayout(row, true, 1f, TextAnchor.MiddleCenter); SetFixedHeight(row, MinimumControl);
                Button load = CreateButtonInHost(row, "PresetName");
                if (load != null)
                {
                    MarkPresetName(load.gameObject); SetButtonText(load, preset.Name); SetFlexibleButtonWidth(load, 1f, 24f, 70f); string id = preset.Id; load.onClick.AddListener(delegate { LoadPresetById(id); }); presetButtons[id] = load; RememberNativeColor(load);
                }
                Button rename = CreateButtonInHost(row, "Rename");
                if (rename != null) { SetFlexibleButtonWidth(rename, 0f, 18f, 18f); string id = preset.Id; rename.onClick.AddListener(delegate { BeginRenamePreset(id); }); }
                Button delete = CreateButtonInHost(row, "Delete");
                if (delete != null) { SetFlexibleButtonWidth(delete, 0f, 14f, 14f); string id = preset.Id; delete.onClick.AddListener(delegate { BeginDeletePreset(id); }); }
            }
            SetButtonSize(boundPresetOptions.GetComponentsInChildren<Button>(true));
        }

        void LoadPresetById(string id)
        {
            if (!CanEditDraft()) return;
            FilterPreset preset = FindPreset(id); if (preset == null) return;
            try { editSession.Load(preset.Settings); selectedPresetId = id; selectedPresetRevision = editSession.Revision; CloseManagePresets(); UpdateUi(); } catch (Exception ex) { Report("Could not load preset", ex); }
        }

        void BeginRenamePreset(string id)
        {
            FilterPreset preset = FindPreset(id); if (preset == null) return;
            editingPresetId = id;
            renamingPreset = true;
            presetNameDraft = preset.Name;
            presetSubedit = true;
            ShowPresetPage(boundPresetRenamePage);
            UpdateUi();
        }

        void BeginDeletePreset(string id)
        {
            FilterPreset preset = FindPreset(id); if (preset == null || boundPresetDeletePage == null) return;
            deletePresetId = id;
            if (boundPresetDeleteConfirmText != null) boundPresetDeleteConfirmText.text = preset.Name;
            presetSubedit = true;
            ShowPresetPage(boundPresetDeletePage);
            UpdateUi();
        }

        void HidePresetEditors()
        {
            presetSubedit = false; keyboardSubedit = false; renamingPreset = false; editingPresetId = null; deletePresetId = null;
            if (boundPresetNameKeyboard != null && boundPresetNameKeyboard.ModalView != null) { try { boundPresetNameKeyboard.ModalView.blockerClickedEvent -= CancelPresetName; boundPresetNameKeyboard.ModalView.Hide(false, null); } catch (Exception ex) { Report("Could not close preset keyboard", ex); } }
            SetActive(boundPresetSelectorPage, false);
            SetActive(boundPresetManagePage, false);
            SetActive(boundPresetSavePage, false);
            SetActive(boundPresetRenamePage, false);
            SetActive(boundPresetDeletePage, false);
            SetActive(boundRangePage, false);
            SetActive(boundMainPage, true);
        }

        FilterPreset FindPreset(string id)
        {
            if (String.IsNullOrEmpty(id)) return null;
            PresetCollection collection = runtime.Presets;
            return collection == null || collection.Presets == null ? null : collection.Presets.FirstOrDefault(x => x != null && x.Id == id);
        }

        bool PresetMatchesDraft(string id)
        {
            FilterPreset preset = FindPreset(id);
            if (preset == null || editSession == null || preset.Settings == null) return false;
            try { return EquivalentSettings(preset.Settings.Normalize(), editSession.Draft.Normalize()); }
            catch (Exception ex) { Report("Could not compare preset state", ex); return false; }
        }

        static bool EquivalentSettings(FilterSettings left, FilterSettings right)
        {
            if (left == null || right == null) return left == right;
            if (left.SchemaVersion != right.SchemaVersion || left.Enabled != right.Enabled
                || left.ExcludeOfficial != right.ExcludeOfficial
                || !String.Equals(left.Mode, right.Mode, StringComparison.Ordinal)
                || !EquivalentDifficulties(left.Difficulties, right.Difficulties)) return false;
            return EquivalentRange(left.Nps, right.Nps) && EquivalentRange(left.Njs, right.Njs)
                && EquivalentRange(left.Duration, right.Duration) && EquivalentRange(left.Bpm, right.Bpm)
                && EquivalentRange(left.Notes, right.Notes);
        }

        static bool EquivalentDifficulties(int[] left, int[] right)
        {
            if (left == null || right == null) return left == right;
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        static bool EquivalentRange(NumericRange left, NumericRange right)
        {
            if (left == null || right == null) return left == right;
            return left.Min == right.Min && left.Max == right.Max;
        }

        void UpdateUi()
        {
            if (!isOpen || editSession == null || panelRoot == null) return;
            FilterSettings draft = editSession.Draft;
            syncingSelectors = true;
            try
            {
                string currentModeKey = String.Join("|", runtime.Modes ?? new string[0]);
                if (!String.Equals(modeKey, currentModeKey, StringComparison.Ordinal)) BuildModeItems();
                UpdateModeUi(draft);
                UpdateScopeUi();
            }
            finally { syncingSelectors = false; }
            try { runtime.RequestPreview(draft, editSession.Revision); requestedRevision = editSession.Revision; } catch (Exception ex) { Report("Could not request filter preview", ex); }
            if (boundTitle != null) boundTitle.text = "Advanced Filters";
            UpdateDifficultyUi(draft); UpdateIncrementUi(draft); UpdatePresetUi(); UpdatePresetEditorUi(); UpdatePreviewUi(draft); UpdateStatusUi(); UpdateUiInteractivity(draft); UpdateEntryText();
            if (boundExcludeOfficial != null) boundExcludeOfficial.Value = draft.ExcludeOfficial;
        }

        void UpdateModeUi(FilterSettings draft)
        {
            if (boundModeSelector != null && modeValues.Count > 0)
            {
                int selected = -1; for (int i = 0; i < modeValues.Count; i++) if (String.Equals(ModeModeAt(i), draft.Mode, StringComparison.OrdinalIgnoreCase)) { selected = i; break; }
                boundModeSelector.SelectCellWithNumber(selected);
            }
            SetSelected(boundAllModesButton, FilterSettings.IsAllMode(draft.Mode));
        }

        void UpdateDifficultyUi(FilterSettings draft)
        {
            bool all = draft.Difficulties == null || draft.Difficulties.Length == 0; SetButtonText(boundDifficultyAll, "All"); SetSelected(boundDifficultyAll, all);
            Button[] buttons = { boundDifficulty0, boundDifficulty1, boundDifficulty2, boundDifficulty3, boundDifficulty4 };
            for (int i = 0; i < buttons.Length; i++) { bool selected = draft.Difficulties != null && draft.Difficulties.Contains(i); SetButtonText(buttons[i], DifficultyLabels[i]); SetSelected(buttons[i], selected); }
        }

        void UpdateIncrementUi(FilterSettings draft)
        {
            UpdateBoundButtons(draft.Nps, Metric.Nps, true, boundNpsMinDec, boundNpsMinValue, boundNpsMinInc);
            UpdateBoundButtons(draft.Nps, Metric.Nps, false, boundNpsMaxDec, boundNpsMaxValue, boundNpsMaxInc);
            UpdateBoundButtons(draft.Njs, Metric.Njs, true, boundNjsMinDec, boundNjsMinValue, boundNjsMinInc);
            UpdateBoundButtons(draft.Njs, Metric.Njs, false, boundNjsMaxDec, boundNjsMaxValue, boundNjsMaxInc);
            UpdateBoundButtons(draft.Duration, Metric.Duration, true, boundDurationMinDec, boundDurationMinValue, boundDurationMinInc);
            UpdateBoundButtons(draft.Duration, Metric.Duration, false, boundDurationMaxDec, boundDurationMaxValue, boundDurationMaxInc);
            UpdateBoundButtons(draft.Bpm, Metric.Bpm, true, boundBpmMinDec, boundBpmMinValue, boundBpmMinInc);
            UpdateBoundButtons(draft.Bpm, Metric.Bpm, false, boundBpmMaxDec, boundBpmMaxValue, boundBpmMaxInc);
            UpdateBoundButtons(draft.Notes, Metric.Notes, true, boundNotesMinDec, boundNotesMinValue, boundNotesMinInc);
            UpdateBoundButtons(draft.Notes, Metric.Notes, false, boundNotesMaxDec, boundNotesMaxValue, boundNotesMaxInc);
        }

        void UpdateBoundButtons(NumericRange range, Metric metric, bool minimum, Button decrement, Button valueButton, Button increment)
        {
            if (range == null) return;
            double? current = minimum ? range.Min : range.Max;
            SetButtonText(valueButton, FilterEditing.FormatBound(metric, current));
            bool allow = interactive && !rangeSubedit && !presetSubedit && !keyboardSubedit;
            if (editSession != null)
            {
                if (decrement != null) decrement.interactable = allow && editSession.CanAdjustBound(metric, minimum, -1);
                if (increment != null) increment.interactable = allow && editSession.CanAdjustBound(metric, minimum, 1);
            }
            if (valueButton != null) valueButton.interactable = allow;
        }

        void UpdateScopeUi()
        {
            if (boundScopeSelector != null) {
                var labels = ScopeLabels;
                string key = String.Join("|", labels.ToArray());
                if (scopeLabelKey != key) {
                    boundScopeSelector.SetTexts(labels, new HashSet<int>());
                    scopeLabelKey = key;
                }
                boundScopeSelector.SelectCellWithNumber(scopeIndex);
            }
            SetActive(boundCommonPage, scopeIndex == 0);
            SetActive(boundMorePage, scopeIndex == 1);
        }

        void UpdatePresetUi()
        {
            if (boundPresetSelectorButton == null) return;
            List<string> labels = PresetLabels.ToList(); int selected = 0;
            if (!String.IsNullOrEmpty(selectedPresetId)) { PresetCollection collection = runtime.Presets; int index = collection == null || collection.Presets == null ? -1 : collection.Presets.FindIndex(x => x != null && x.Id == selectedPresetId); selected = index < 0 ? 0 : index + 1; }
            string label = selected > 0 && selected < labels.Count ? labels[selected] : "Current";
            if (selected > 0 && selectedPresetRevision != editSession.Revision) label += " *";
            SetButtonText(boundPresetSelectorButton, label);
        }

        void UpdatePreviewUi(FilterSettings draft)
        {
            FilterPreview preview = runtime.Preview; if (boundMatchCount == null) return;
            if (preview == null || preview.State == FilterPreviewState.Loading) { boundMatchCount.text = "Calculating"; return; }
            if (preview.State == FilterPreviewState.Error) { boundMatchCount.text = String.Empty; return; }
            if (preview.Matched == 0 && preview.Unknown == 0) boundMatchCount.text = "No matching songs";
            else if (preview.Unknown > 0) boundMatchCount.text = "Matches: " + preview.Matched.ToString(CultureInfo.InvariantCulture) + " / " + preview.Total.ToString(CultureInfo.InvariantCulture) + " · " + preview.Unknown.ToString(CultureInfo.InvariantCulture) + " songs have incomplete data";
            else boundMatchCount.text = "Matches: " + preview.Matched.ToString(CultureInfo.InvariantCulture) + " / " + preview.Total.ToString(CultureInfo.InvariantCulture);
        }

        void UpdateStatusUi()
        {
            string status = runtime.Status; if (!String.IsNullOrEmpty(localStatus)) status = String.IsNullOrEmpty(status) ? localStatus : status + " " + localStatus;
            if (boundStatusText != null) {
                bool show = !String.IsNullOrEmpty(status);
                bool changed = boundStatusText.gameObject.activeSelf != show;
                boundStatusText.text = status ?? String.Empty;
                boundStatusText.gameObject.SetActive(show);
                if (changed) InvalidateLayout();
            }
            SetPageStatus(boundPresetSelectorStatus, status, boundPresetSelectorPage);
            SetPageStatus(boundPresetManageStatus, status, boundPresetManagePage);
            SetPageStatus(boundPresetSaveStatus, status, boundPresetSavePage);
            SetPageStatus(boundPresetRenameStatus, status, boundPresetRenamePage);
            SetPageStatus(boundPresetDeleteStatus, status, boundPresetDeletePage);
            if (boundRetryButton != null) { boundRetryButton.gameObject.SetActive(runtime.CanRetry); boundRetryButton.interactable = interactive && runtime.CanRetry; }
        }

        void SetPageStatus(TMP_Text label, string status, GameObject page)
        {
            if (label == null) return;
            bool show = page != null && page.activeSelf && !String.IsNullOrEmpty(status);
            label.text = show ? status : String.Empty;
            label.gameObject.SetActive(show);
        }

        void UpdateUiInteractivity(FilterSettings draft)
        {
            bool allow = CanEditDraft() && !rangeSubedit && !presetSubedit && !keyboardSubedit;
            if (boundExcludeOfficial != null) boundExcludeOfficial.Interactable = allow;
            SetInteractable(boundAllModesButton, allow); SetInteractable(boundDifficultyAll, allow); SetInteractable(boundDifficulty0, allow); SetInteractable(boundDifficulty1, allow); SetInteractable(boundDifficulty2, allow); SetInteractable(boundDifficulty3, allow); SetInteractable(boundDifficulty4, allow);
            if (boundModeSelector != null) { Selectable value = boundModeSelector.GetComponent<Selectable>(); if (value != null) value.interactable = allow; }
            if (boundScopeSelector != null) { Selectable value = boundScopeSelector.GetComponent<Selectable>(); if (value != null) value.interactable = allow; }
            SetInteractable(boundPresetSelectorButton, allow);
            if (boundResetButton != null) boundResetButton.interactable = CanEditDraft() && !rangeSubedit && !keyboardSubedit;
            if (boundCancelButton != null) boundCancelButton.interactable = isOpen;
            FilterPreview preview = runtime.Preview; bool canApply = allow && preview != null && preview.CanApply && PreviewReadyFor(draft); if (boundApplyButton != null) boundApplyButton.interactable = canApply;
            bool rangeAllow = CanEditDraft() && rangeSubedit && !presetSubedit;
            SetInteractable(boundRangeMinimum, rangeAllow);
            SetInteractable(boundRangeMaximum, rangeAllow);
            if (boundNumericKeypad != null)
            {
                Button[] keys = boundNumericKeypad.GetComponentsInChildren<Button>(true);
                for (int i = 0; i < keys.Length; i++) keys[i].interactable = CanEditDraft() && rangeSubedit && !presetSubedit;
            }
            if (boundRangeActions != null)
            {
                Button[] actions = boundRangeActions.GetComponentsInChildren<Button>(true);
                for (int i = 0; i < actions.Length; i++) actions[i].interactable = rangeAllow || (CanEditDraft() && rangeSubedit);
            }
        }

        bool PreviewReadyFor(FilterSettings value) { return runtime.Preview != null && runtime.Preview.CanApply && editSession != null && requestedRevision == editSession.Revision; }
        bool CanEditDraft() { return isOpen && interactive && editSession != null && runtime.MenuActive; }

        void BuildModeItems()
        {
            modeValues.Clear(); string[] modes = runtime.Modes ?? new string[0];
            for (int i = 0; i < modes.Length; i++) { string mode = FilterSettings.NormalizeMode(modes[i]); if (!String.IsNullOrEmpty(mode) && !String.Equals(mode, "All", StringComparison.OrdinalIgnoreCase) && !modeValues.Contains(mode, StringComparer.OrdinalIgnoreCase)) modeValues.Add(mode); }
            List<HMUI.IconSegmentedControl.DataItem> items = new List<HMUI.IconSegmentedControl.DataItem>();
            for (int i = 0; i < modeValues.Count; i++) items.Add(new HMUI.IconSegmentedControl.DataItem(FindModeSprite(modeValues[i]), DisplayMode(modeValues[i]), true));
            modeIconItems = items.ToArray();
            if (boundModeSelector != null) boundModeSelector.SetData(modeIconItems);
            modeKey = String.Join("|", runtime.Modes ?? new string[0]);
        }

        string ModeModeAt(int index)
        {
            string[] modes = runtime.Modes ?? new string[0]; List<string> values = new List<string>();
            for (int i = 0; i < modes.Length; i++) { string mode = FilterSettings.NormalizeMode(modes[i]); if (!String.IsNullOrEmpty(mode) && !String.Equals(mode, "All", StringComparison.OrdinalIgnoreCase) && !values.Contains(mode, StringComparer.OrdinalIgnoreCase)) values.Add(mode); }
            return index >= 0 && index < values.Count ? values[index] : "All";
        }

        Sprite FindModeSprite(string mode)
        {
            string normalized = FilterSettings.NormalizeMode(mode); string[] needles = normalized == "All" ? new[] { "standard", "default", "all" } : normalized == "Degree90" ? new[] { "90degree", "degree90", "90" } : normalized == "Degree360" ? new[] { "360degree", "degree360", "360" } : new[] { normalized.ToLowerInvariant() };
            Sprite[] sprites = Resources.FindObjectsOfTypeAll<Sprite>();
            for (int n = 0; n < needles.Length; n++) for (int i = 0; i < sprites.Length; i++) { string name = sprites[i] == null ? String.Empty : sprites[i].name.ToLowerInvariant(); if (name.Contains(needles[n]) && (name.Contains("icon") || name.Contains("characteristic") || normalized == "All")) return sprites[i]; }
            for (int i = 0; i < sprites.Length; i++) if (sprites[i] != null && sprites[i].name.ToLowerInvariant().Contains("standard")) return sprites[i];
            HMUI.IconSegmentedControl[] controls = Resources.FindObjectsOfTypeAll<HMUI.IconSegmentedControl>();
            for (int c = 0; c < controls.Length; c++)
            {
                if (controls[c] == null || controls[c].cells == null) continue;
                for (int i = 0; i < controls[c].cells.Count; i++)
                {
                    Image image = controls[c].cells[i] == null ? null : controls[c].cells[i].GetComponentInChildren<Image>(true);
                    if (image != null && image.sprite != null) return image.sprite;
                }
            }
            return sprites.Length == 0 ? null : sprites[0];
        }

        IEnumerator WaitForCentralTransition()
        {
            int frames = 0; while (runtime.CentralTransitioning && frames++ < TransitionFrameLimit) yield return null;
            transitionCoroutine = null; SetInteractive(!runtime.CentralTransitioning); InvalidateLayout(); UpdateUi();
        }

        IEnumerator MeasureNextFrame()
        {
            yield return null; measureCoroutine = null; Vector2 size;
            if (TryGetPanelSize(out size) && filterModal != null) ConfigurePanelLayout(size);
            if (!runtime.CentralTransitioning) SetInteractive(true);
        }

        void SetInteractive(bool value) { interactive = value && isOpen && !runtime.CentralTransitioning; UpdateUiInteractivity(editSession == null ? null : editSession.Draft); }

        string LoadMarkup()
        {
            Assembly assembly = typeof(Panel).Assembly;
            foreach (string resource in assembly.GetManifestResourceNames()) if (resource.EndsWith("Panel.bsml", StringComparison.OrdinalIgnoreCase)) using (Stream stream = assembly.GetManifestResourceStream(resource)) using (StreamReader reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            string directory = null; try { directory = Path.GetDirectoryName(assembly.Location); } catch (Exception ex) { Report("Could not read filter panel resource path", ex); }
            string path = String.IsNullOrEmpty(directory) ? null : Path.Combine(directory, "Panel.bsml"); if (!String.IsNullOrEmpty(path) && File.Exists(path)) return File.ReadAllText(path, Encoding.UTF8);
            throw new FileNotFoundException("AdvancedSongFilters.Panel.bsml is unavailable");
        }

        void EnsureEntry()
        {
            if (toolbarEntry != null || bottomEntry != null) return; RectTransform parent = toolbarSlot == null ? toolbarRoot : toolbarSlot; if (parent == null) return;
            toolbarEntryHost = CreateEntryHost(parent, "AdvancedSongFilters.ToolbarEntryHost", out toolbarEntry); if (toolbarEntry != null) toolbarEntry.onClick.AddListener(Open); PlaceEntry();
        }

        GameObject CreateEntryHost(RectTransform parent, string name, out Button button)
        {
            GameObject host = new GameObject(name, typeof(RectTransform), typeof(HorizontalLayoutGroup)); host.transform.SetParent(parent, false); LayoutElement layout = host.AddComponent<LayoutElement>(); layout.minWidth = EntryWidth; layout.preferredWidth = EntryWidth; layout.minHeight = EntryHeight; layout.preferredHeight = EntryHeight;
            button = CreateButtonInHost(host, "Advanced Filters"); SetButtonSize(button == null ? new Button[0] : new[] { button }); return host;
        }

        Button CreateButtonInHost(GameObject host, string text)
        {
            if (host == null) return null;
            try
            {
                Button[] before = host.GetComponentsInChildren<Button>(true);
                BSMLParser.Instance.Parse("<button text=\"" + XmlEscape(text) + "\" />", host, this);
                Button[] after = host.GetComponentsInChildren<Button>(true);
                for (int i = 0; i < after.Length; i++)
                {
                    bool existed = false;
                    for (int j = 0; j < before.Length; j++) if (after[i] == before[j]) { existed = true; break; }
                    if (!existed) { RememberNativeColor(after[i]); return after[i]; }
                }
                Button fallback = after.Length == 0 ? null : after[after.Length - 1];
                if (fallback != null) RememberNativeColor(fallback);
                return fallback;
            }
            catch (Exception ex) { Report("Could not create native button", ex); return null; }
        }

        void PlaceEntry()
        {
            if (toolbarEntry == null) return; toolbarEntry.interactable = runtime.MenuActive;
            if (toolbarSlot != null && HasToolbarSpace(toolbarSlot)) { if (bottomEntryHost != null) bottomEntryHost.SetActive(false); toolbarEntryHost.SetActive(true); toolbarEntryHost.transform.SetParent(toolbarSlot, false); return; }
            EnsureBottomEntry(); toolbarEntryHost.SetActive(false); if (bottomEntryHost != null) bottomEntryHost.SetActive(true);
        }

        void EnsureBottomEntry()
        {
            if (bottomEntryHost != null || screenRect == null) return; bottomEntryHost = new GameObject("AdvancedSongFilters.BottomEntry", typeof(RectTransform), typeof(HorizontalLayoutGroup)); bottomEntryHost.transform.SetParent(screenRect, false); RectTransform rect = bottomEntryHost.GetComponent<RectTransform>(); rect.anchorMin = new Vector2(0.5f, 0f); rect.anchorMax = new Vector2(0.5f, 0f); rect.pivot = new Vector2(0.5f, 1f); rect.anchoredPosition = new Vector2(0f, -8f); rect.sizeDelta = new Vector2(EntryWidth, EntryHeight); bottomEntry = CreateButtonInHost(bottomEntryHost, "Advanced Filters"); if (bottomEntry != null) { bottomEntry.onClick.AddListener(Open); bottomEntry.interactable = runtime.MenuActive; }
        }

        bool HasToolbarSpace(RectTransform rect)
        {
            if (rect == null) return false; HorizontalLayoutGroup group = rect.GetComponent<HorizontalLayoutGroup>(); if (group == null) return false; float occupied = group.padding.left + group.padding.right;
            for (int i = 0; i < rect.childCount; i++) { RectTransform child = rect.GetChild(i) as RectTransform; if (child == null || !child.gameObject.activeSelf || child == toolbarEntryHost.transform) continue; occupied += Mathf.Max(1f, LayoutUtility.GetPreferredWidth(child)); }
            return rect.rect.width <= 0f || rect.rect.width - occupied >= EntryWidth + 8f;
        }

        RectTransform ResolveToolbarSlot(RectTransform root)
        {
            if (root == null) return null; for (int i = 0; i < root.childCount; i++) { RectTransform child = root.GetChild(i) as RectTransform; if (child != null && child.GetComponent<HorizontalLayoutGroup>() != null && child.GetComponentsInChildren<HMUI.DropdownWithTableView>(true).Length == 0) return child; }
            HorizontalOrVerticalLayoutGroup[] groups = root.GetComponentsInChildren<HorizontalOrVerticalLayoutGroup>(true); return groups.Length == 0 ? root : groups[0].GetComponent<RectTransform>();
        }

        void UpdateEntryText()
        {
            try { int active = runtime.Applied == null ? 0 : runtime.Applied.ActiveCount; string value = "Advanced Filters" + (active > 0 ? " · " + active.ToString(CultureInfo.InvariantCulture) : String.Empty); SetButtonText(toolbarEntry, value); SetButtonText(bottomEntry, value); SetEntrySelected(toolbarEntry, active > 0); SetEntrySelected(bottomEntry, active > 0); }
            catch (Exception ex) { Report("Could not update filter entry", ex); }
        }

        void SetEntrySelected(Button button, bool selected)
        {
            SetSelected(button, selected);
        }

        void RememberNativeColor(Button button) { if (button != null && !nativeColors.ContainsKey(button)) nativeColors.Add(button, button.colors); if (button != null && !allButtons.Contains(button)) allButtons.Add(button); }

        void SetSelected(Button button, bool selected)
        {
            if (button == null) return;
            RememberNativeColor(button);
            ColorBlock colors = nativeColors[button];
            if (selected) { colors.normalColor = new Color(0.15f, 0.45f, 0.9f, 1f); colors.highlightedColor = new Color(0.25f, 0.58f, 1f, 1f); colors.selectedColor = colors.highlightedColor; button.colors = colors; }
            else button.colors = nativeColors[button];
            TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
            if (label != null)
            {
                if (!nativeTextColors.ContainsKey(button)) nativeTextColors.Add(button, label.color);
                label.color = selected ? new Color(0.55f, 0.85f, 1f, 1f) : nativeTextColors[button];
            }
        }

        void SetButtonSize(IEnumerable<Button> buttons)
        {
            foreach (Button button in buttons)
            {
                if (button == null) continue;
                RememberNativeColor(button);
                LayoutElement layout = button.GetComponent<LayoutElement>(); if (layout == null) layout = button.gameObject.AddComponent<LayoutElement>();
                layout.minHeight = Mathf.Max(layout.minHeight, MinimumControl); layout.preferredHeight = Mathf.Max(layout.preferredHeight, MinimumControl);
                layout.minWidth = Mathf.Max(layout.minWidth, MinimumControl); layout.preferredWidth = Mathf.Max(layout.preferredWidth, MinimumControl);
                RectTransform rect = button.transform as RectTransform;
                if (rect != null)
                {
                    if (rect.rect.height < MinimumControl) rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, MinimumControl);
                    if (rect.rect.width < MinimumControl) rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, MinimumControl);
                }
                button.interactable = true;
            }
        }

        void SetFlexibleButtonWidth(Button button, float flexible, float minimum, float preferred)
        {
            if (button == null) return;
            LayoutElement layout = button.GetComponent<LayoutElement>();
            if (layout == null) layout = button.gameObject.AddComponent<LayoutElement>();
            layout.minWidth = Mathf.Max(MinimumControl, minimum);
            layout.preferredWidth = Mathf.Max(layout.minWidth, preferred);
            layout.flexibleWidth = flexible;
        }

        void SetInteractable(Button button, bool value) { if (button != null) button.interactable = value; }

        void AddClickHandler(GameObject target, Action action)
        {
            if (target == null || action == null) return; EventTrigger trigger = target.GetComponent<EventTrigger>(); if (trigger == null) trigger = target.AddComponent<EventTrigger>(); EventTrigger.Entry entry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick }; entry.callback.AddListener(delegate { action(); }); trigger.triggers.Add(entry);
        }

        void MarkPresetName(GameObject root)
        {
            if (root == null) return; root.name = "PresetName"; TMP_Text[] texts = root.GetComponentsInChildren<TMP_Text>(true); for (int i = 0; i < texts.Length; i++) texts[i].gameObject.name = i == 0 ? "PresetName" : "preset-name";
        }

        void ProtectPresetKeyboardName()
        {
            if (boundPresetNameKeyboard == null || boundPresetNameKeyboard.Keyboard == null) return;
            TMP_Text input = boundPresetNameKeyboard.Keyboard.KeyboardText;
            if (input != null) input.gameObject.name = "PresetName";
        }

        void ResetBindings()
        {
            boundFooter = null;
            boundHeaderSpacer = null; boundFooterSpacer = null;
            boundMetricHeader = null; boundMoreMetricHeader = null;
            boundExcludeOfficial = null;
            scopeLabelKey = null;
            boundFilterModal = null; boundPanelRoot = null; boundMainPage = null; boundRangePage = null; boundPresetSelectorPage = null; boundPresetManagePage = null; boundPresetSavePage = null; boundPresetRenamePage = null; boundPresetDeletePage = null;
            boundHeader = null; boundTitle = null; boundPresetSelectorButton = null; boundPresetSelectorOptions = null; boundSaveNewButton = null; boundUpdateCurrentButton = null; boundPresetSaveName = null; boundPresetSaveTitle = null; boundPresetSaveStatus = null; boundPresetRenameName = null; boundPresetRenameTitle = null; boundPresetRenameStatus = null; boundPresetSelectorStatus = null; boundPresetManageStatus = null; boundPresetDeleteStatus = null; boundPresetDeleteConfirmText = null;
            boundScopeSelector = null; boundSelectionSection = null; boundScopeRow = null; boundModeSelector = null; boundAllModesButton = null; boundDifficultyAll = null; boundDifficulty0 = null; boundDifficulty1 = null; boundDifficulty2 = null; boundDifficulty3 = null; boundDifficulty4 = null;
            boundScrollContainer = null; boundFormContent = null; boundCommonPage = null; boundMorePage = null; boundNpsRow = null; boundNjsRow = null; boundDurationRow = null; boundBpmRow = null; boundNotesRow = null;
            boundNpsMinDec = null; boundNpsMinValue = null; boundNpsMinInc = null; boundNpsMaxDec = null; boundNpsMaxValue = null; boundNpsMaxInc = null;
            boundNjsMinDec = null; boundNjsMinValue = null; boundNjsMinInc = null; boundNjsMaxDec = null; boundNjsMaxValue = null; boundNjsMaxInc = null;
            boundDurationMinDec = null; boundDurationMinValue = null; boundDurationMinInc = null; boundDurationMaxDec = null; boundDurationMaxValue = null; boundDurationMaxInc = null;
            boundBpmMinDec = null; boundBpmMinValue = null; boundBpmMinInc = null; boundBpmMaxDec = null; boundBpmMaxValue = null; boundBpmMaxInc = null;
            boundNotesMinDec = null; boundNotesMinValue = null; boundNotesMinInc = null; boundNotesMaxDec = null; boundNotesMaxValue = null; boundNotesMaxInc = null;
            boundStatusText = null; boundRangeTitle = null; boundRangeMinimum = null; boundRangeMaximum = null; boundRangeMinimumError = null; boundRangeMaximumError = null; boundRangeError = null; boundRangeActions = null; boundNumericKeypad = null; boundKeypadDecimal = null; boundPresetNameKeyboard = null; boundPresetName = null; boundPresetOptions = null; boundPresetDeleteConfirmText = null; boundMatchCount = null; boundRetryButton = null; boundResetButton = null; boundCancelButton = null; boundApplyButton = null;
        }

        static RectTransform EnsureRect(GameObject target) { if (target == null) return null; RectTransform rect = target.GetComponent<RectTransform>(); return rect == null ? target.AddComponent<RectTransform>() : rect; }
        static LayoutElement EnsureLayout(GameObject target) { if (target == null) return null; LayoutElement value = target.GetComponent<LayoutElement>(); return value ?? target.AddComponent<LayoutElement>(); }
        static void SetLayout(GameObject target, bool horizontal, float spacing, TextAnchor anchor) { if (target == null) return; HorizontalOrVerticalLayoutGroup group = target.GetComponent<HorizontalOrVerticalLayoutGroup>(); if (group == null) group = horizontal ? (HorizontalOrVerticalLayoutGroup)target.AddComponent<HorizontalLayoutGroup>() : (HorizontalOrVerticalLayoutGroup)target.AddComponent<VerticalLayoutGroup>(); group.spacing = spacing; group.childAlignment = anchor; group.childControlWidth = true; group.childControlHeight = true; group.childForceExpandWidth = false; group.childForceExpandHeight = false; }
        static void SetFixedHeight(GameObject target, float height) { if (target == null) return; LayoutElement value = EnsureLayout(target); if (value == null) return; value.minHeight = height; value.preferredHeight = height; value.flexibleHeight = 0f; }
        bool TryGetPanelSize(out Vector2 size) { size = Vector2.zero; if (screenRect == null) return false; Vector2 bounds = screenRect.rect.size; if (bounds.x <= 0f || bounds.y <= 0f) return false; size = new Vector2(Mathf.Min(TargetWidth, bounds.x), Mathf.Min(TargetHeight, bounds.y)); return size.x > 0f && size.y > 0f; }
        Component FindStableScreen(Transform source) { if (source == null) return null; Type type = AccessTools.TypeByName("HMUI.Screen"); Component found = type == null ? null : source.GetComponentInParent(type); if (found != null) return found; type = AccessTools.TypeByName("HMUI.ScreenBase"); return type == null ? null : source.GetComponentInParent(type); }
        string MetricLabel(Metric metric) { switch (metric) { case Metric.Nps: return "Average NPS"; case Metric.Njs: return "NJS"; case Metric.Duration: return "Duration"; case Metric.Bpm: return "BPM"; case Metric.Notes: return "Notes"; default: return metric.ToString(); } }
        string DisplayMode(string mode) { if (String.Equals(mode, "All", StringComparison.OrdinalIgnoreCase)) return "All"; if (String.Equals(mode, "Standard", StringComparison.OrdinalIgnoreCase)) return "Standard"; if (String.Equals(mode, "OneSaber", StringComparison.OrdinalIgnoreCase)) return "One Saber"; if (String.Equals(mode, "NoArrows", StringComparison.OrdinalIgnoreCase)) return "No Arrows"; if (String.Equals(mode, "Degree90", StringComparison.OrdinalIgnoreCase)) return "90 Degree"; if (String.Equals(mode, "Degree360", StringComparison.OrdinalIgnoreCase)) return "360 Degree"; return mode ?? String.Empty; }
        string LocalizeRangeError(string error) { if (String.IsNullOrEmpty(error)) return "Invalid value"; if (error.IndexOf("greater than or equal", StringComparison.OrdinalIgnoreCase) >= 0) return MetricLabel(rangeMetric) + ": maximum must not be below minimum"; if (error.IndexOf("invalid", StringComparison.OrdinalIgnoreCase) >= 0) return MetricLabel(rangeMetric) + ": enter a valid value"; return MetricLabel(rangeMetric) + "：" + error; }
        void SetButtonText(Button button, string value) { if (button == null) return; TMP_Text text = button.GetComponentInChildren<TMP_Text>(true); if (text != null) text.text = value ?? String.Empty; }

        static string XmlEscape(string value) { return (value ?? String.Empty).Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;"); }
        static void SetActive(GameObject target, bool value) { if (target != null && target.activeSelf != value) target.SetActive(value); }
        void StopCoroutine(ref Coroutine coroutine) { if (coroutine == null) return; try { runtime.StopCoroutine(coroutine); } catch (Exception ex) { Report("Could not stop filter panel coroutine", ex); } coroutine = null; }
        void DestroyObject(UnityEngine.Object target) { if (target == null) return; try { UnityEngine.Object.Destroy(target); } catch (Exception ex) { Report("Could not destroy filter panel", ex); } }
        void Report(string message, Exception ex) { try { if (runtime.Log != null) runtime.Log.Error(ex == null ? message : message + ": " + ex); } catch { } }
    }
}
