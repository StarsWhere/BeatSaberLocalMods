using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using AdvancedSongFilters;
using IPA;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace AdvancedSongFiltersSmoke
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public sealed class Plugin
    {
        static IPA.Logging.Logger logger;
        GameObject owner;

        [Init]
        public void Init(IPA.Logging.Logger value)
        {
            logger = value;
        }

        [OnStart]
        public void Start()
        {
            owner = new GameObject("AdvancedSongFiltersSmoke.Runtime");
            UnityEngine.Object.DontDestroyOnLoad(owner);
            GameSmokeRunner runner = owner.AddComponent<GameSmokeRunner>();
            runner.Initialize(logger);
        }

        [OnExit]
        public void Exit()
        {
            if (owner != null)
            {
                UnityEngine.Object.Destroy(owner);
                owner = null;
            }
        }
    }

    public sealed class GameSmokeRunner : MonoBehaviour
    {
        const float TotalSeconds = 240f;
        const float MenuWaitSeconds = 70f;
        const float RuntimeWaitSeconds = 90f;
        const float GeometryToleranceUnits = 0.1f;
        const int CaptureWidth = 1920;
        const int CaptureHeight = 1080;

        sealed class Check
        {
            public string Name;
            public bool Passed;
            public string Detail;
        }

        sealed class CapturedLog
        {
            public string Message;
            public string StackTrace;
            public string Type;
        }

        sealed class ParserAttempt
        {
            public Counts Actual;
            public Exception Error;
            public bool Completed;
        }

        sealed class CaptureAttempt
        {
            public bool Completed;
            public int Attempts;
            public bool VisualPassed;
            public string Detail;
        }

        struct CameraPose
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public Rect Rect;
            public float FieldOfView;
            public float Aspect;
            public float NearClipPlane;
            public float FarClipPlane;
            public bool Orthographic;
            public float OrthographicSize;
            public Matrix4x4 ProjectionMatrix;
        }

        sealed class NativeSelection
        {
            public string LevelId;
            public string Difficulty;
            public bool DifficultyAvailable;
            public bool DetailActive;
            public bool StartActive;
        }

        sealed class UserDataSnapshot
        {
            public string SettingsPath;
            public string CachePath;
            public string PresetsPath;
            public bool SettingsExists;
            public bool CacheExists;
            public bool PresetsExists;
            public byte[] SettingsBytes;
            public byte[] CacheBytes;
            public byte[] PresetsBytes;
        }

        sealed class NativeUiTarget
        {
            public Button Button;
            public RectTransform Rect;
            public Canvas Canvas;
            public Graphic Graphic;
            public Component VrRaycaster;
            public GraphicRaycaster GraphicRaycaster;
            public string RaycasterPath;
            public string GraphicDiagnostics;
        }

        sealed class ReadyAttempt
        {
            public bool Ready;
        }

        sealed class GameplayAttempt
        {
            public bool Invoked;
            public bool SetupInvoked;
            public bool EnteredGameplay;
            public bool GameplayReady;
            public bool PauseRequested;
            public bool PauseObserved;
            public bool ReturnInvoked;
            public bool ReturnedToSelection;
            public string EntryDetail;
            public string ReturnDetail;
            public string PauseReadyDetail;
            public string ReturnStateBefore;
            public string ReturnStateAfter;
            public string ReturnCaptureDetail;
            public bool PracticeSetupBackInvoked;
            public bool PracticeSetupReturned;
            public string PracticeSetupDetail;
        }

        sealed class PreviewContractAttempt
        {
            public bool Passed;
            public string Detail;
        }

        sealed class SelectionRestoreAttempt
        {
            public bool Invoked;
            public bool Restored;
        }

        sealed class ParserSample
        {
            public BeatmapLevel Level;
            public BeatmapKey Key;
            public IBeatmapLevelData Data;
        }

        struct Counts
        {
            public int NumberOfLines;
            public int CuttableNotesCount;
            public int CuttableScoringObjectsCount;
            public int ObstaclesCount;
            public int BombsCount;
        }

        struct ExpectedCounts
        {
            public int NumberOfLines;
            public int CuttableNotesCount;
            public int CuttableScoringObjectsCount;
            public int ObstaclesCount;
            public int BombsCount;
        }

        readonly List<Check> checks = new List<Check>();
        readonly List<string> exceptions = new List<string>();
        readonly List<CapturedLog> capturedLogs = new List<CapturedLog>();
        readonly object logLock = new object();
        Stopwatch clock;
        IPA.Logging.Logger logger;
        string gameRoot;
        string runtimeDirectory;
        bool finished;
        bool menuReady;
        bool soloClicked;
        bool runtimeReady;
        bool parserPassed;
        bool uiPassed;
        bool practicePassed;
        bool screenshotPassed;
        Exception fatalException;
        Camera captureCamera;
        bool captureCameraPrepared;
        CameraPose capturePose;
        bool capturePoseValid;
        string capturePoseDiagnostics;
        string visualDiagnostics;
        Rect visualPanelViewport;
        bool visualPanelViewportValid;
        Rect visualToolbarViewport;
        bool visualToolbarViewportValid;
        AdvancedSongFilters.Runtime runtime;
        AdvancedSongFilters.Panel panel;
        LevelCollectionTableView table;
        LevelCollectionNavigationController navigation;
        NativeSelection smokeOriginalSelection;

        public void Initialize(IPA.Logging.Logger value)
        {
            logger = value;
        }

        void Start()
        {
            Application.runInBackground = true;
            gameRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string configuredRuntimeDirectory = Environment.GetEnvironmentVariable("ADVANCED_SONG_FILTERS_SMOKE_DIR");
            runtimeDirectory = String.IsNullOrWhiteSpace(configuredRuntimeDirectory)
                ? Path.Combine(gameRoot, ".tmp", "advanced-song-filters-v020", "runtime-check")
                : Path.GetFullPath(configuredRuntimeDirectory);
            Directory.CreateDirectory(runtimeDirectory);
            clock = Stopwatch.StartNew();
            Application.logMessageReceived += OnLogMessage;
            StartCoroutine(Run());
        }

        void OnDestroy()
        {
            Application.logMessageReceived -= OnLogMessage;
            StopAllCoroutines();
        }

        void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            lock (logLock)
            {
                if (capturedLogs.Count >= 200) return;
                capturedLogs.Add(new CapturedLog
                {
                    Message = condition ?? String.Empty,
                    StackTrace = stackTrace ?? String.Empty,
                    Type = type.ToString()
                });
            }
        }

        IEnumerator Run()
        {
            bool failed = false;
            IEnumerator phase = WaitForMainMenuAndClick();
            object yielded;
            while (MovePhase(phase, out yielded)) yield return yielded;
            if (HasFatalException()) failed = true;

            if (!failed)
            {
                phase = WaitForRuntimeAndOpenPanel();
                while (MovePhase(phase, out yielded)) yield return yielded;
                if (HasFatalException()) failed = true;
            }

            if (!failed)
            {
                try
                {
                    uiPassed = ValidatePanel();
                }
                catch (Exception error)
                {
                    RecordException("panel-validation", error);
                    uiPassed = false;
                }
                if (!uiPassed) failed = true;
                // UI geometry is a reported acceptance failure, but a live
                // Panel still provides enough state for screenshots, runtime
                // interaction, parser, and renderer diagnostics. Abort only
                // when the panel itself is unavailable.
                if (!uiPassed && panel != null && panel.IsOpen) failed = false;
            }

            if (!failed)
            {
                phase = ValidateSelectionAndCapture();
                while (MovePhase(phase, out yielded)) yield return yielded;
                if (HasFatalException()) failed = true;
            }

            if (!failed && !Expired())
            {
                phase = RunParserTests(gameRoot);
                while (MovePhase(phase, out yielded)) yield return yielded;
                if (HasFatalException()) failed = true;
            }

            parserPassed = parserPassed && !Expired();
            if (Expired())
            {
                AddCheck("deadline", false, "runtime smoke exceeded the 240 second total limit");
                failed = true;
            }
            if (smokeOriginalSelection != null && table != null && navigation != null)
                RestoreNativeSelection(smokeOriginalSelection);
            Finish(!failed && menuReady && soloClicked && runtimeReady && uiPassed && practicePassed && screenshotPassed && parserPassed);
            yield break;
        }

        bool MovePhase(IEnumerator phase, out object yielded)
        {
            yielded = null;
            if (phase == null || Expired()) return false;
            try
            {
                if (!phase.MoveNext()) return false;
                yielded = phase.Current;
                return true;
            }
            catch (Exception error)
            {
                RecordException("phase", error);
                fatalException = error;
                return false;
            }
        }

        bool HasFatalException()
        {
            return fatalException != null;
        }

        bool Expired()
        {
            return clock == null || clock.Elapsed.TotalSeconds >= TotalSeconds;
        }

        float SecondsRemaining(float phaseLimit)
        {
            if (clock == null) return 0f;
            double remaining = TotalSeconds - clock.Elapsed.TotalSeconds;
            return (float)Math.Max(0d, Math.Min((double)phaseLimit, remaining));
        }

        IEnumerator WaitForMainMenuAndClick()
        {
            MainMenuViewController menu = null;
            float deadline = Time.realtimeSinceStartup + SecondsRemaining(MenuWaitSeconds);
            while (!Expired() && Time.realtimeSinceStartup < deadline)
            {
                MainMenuViewController[] menus = Resources.FindObjectsOfTypeAll<MainMenuViewController>();
                for (int i = 0; i < menus.Length; i++)
                {
                    if (menus[i] != null && menus[i].isActivated)
                    {
                        menu = menus[i];
                        break;
                    }
                }
                if (menu != null) break;
                yield return null;
            }

            if (menu == null)
            {
                AddCheck("main-menu.activated", false, "MainMenuViewController.isActivated was not observed within 60 seconds");
                yield break;
            }

            menuReady = true;
            AddCheck("main-menu.activated", true, menu.name);
            soloClicked = InvokeSoloButton(menu);
            AddCheck("main-menu.solo", soloClicked,
                soloClicked ? "entered SoloFreePlay through the native menu action" : "could not invoke _soloButton or MainFlowCoordinator fallback");
        }

        bool InvokeSoloButton(MainMenuViewController menu)
        {
            try
            {
                FieldInfo field = GetFieldIncludingBases(menu.GetType(), "_soloButton");
                Button button = field == null ? null : field.GetValue(menu) as Button;
                if (button != null)
                {
                    button.onClick.Invoke();
                    return true;
                }
            }
            catch (Exception error)
            {
                RecordException("main-menu.solo-button", error);
            }

            try
            {
                MainFlowCoordinator[] coordinators = Resources.FindObjectsOfTypeAll<MainFlowCoordinator>();
                for (int i = 0; i < coordinators.Length; i++)
                {
                    MainFlowCoordinator coordinator = coordinators[i];
                    if (coordinator == null || !coordinator.gameObject.activeInHierarchy) continue;
                    MethodInfo[] methods = coordinator.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    for (int m = 0; m < methods.Length; m++)
                    {
                        MethodInfo method = methods[m];
                        if (method.Name != "HandleMainMenuViewControllerDidFinish") continue;
                        ParameterInfo[] parameters = method.GetParameters();
                        if (parameters.Length != 2 || !parameters[0].ParameterType.IsInstanceOfType(menu)) continue;
                        object buttonValue = Enum.Parse(parameters[1].ParameterType, "SoloFreePlay");
                        method.Invoke(coordinator, new object[] { menu, buttonValue });
                        return true;
                    }
                }
            }
            catch (Exception error)
            {
                RecordException("main-menu.flow-coordinator", error);
            }
            return false;
        }

        IEnumerator WaitForRuntimeAndOpenPanel()
        {
            float deadline = Time.realtimeSinceStartup + SecondsRemaining(RuntimeWaitSeconds);
            while (!Expired() && Time.realtimeSinceStartup < deadline)
            {
                runtime = AdvancedSongFilters.Runtime.Current;
                if (runtime != null && runtime.MenuActive && !runtime.CentralTransitioning)
                {
                    panel = GetPanel(runtime);
                    if (panel != null && !runtime.IsBusy) break;
                }
                yield return null;
            }

            runtime = AdvancedSongFilters.Runtime.Current;
            panel = runtime == null ? null : GetPanel(runtime);
            runtimeReady = runtime != null && runtime.MenuActive && !runtime.CentralTransitioning;
            AddCheck("runtime.menu-active", runtimeReady,
                runtimeReady ? "Runtime.Current.MenuActive and CentralTransitioning=false" : "Runtime.Current was not ready after entering SoloFreePlay");
            if (!runtimeReady || panel == null)
            {
                AddCheck("panel.private-field", false, "Runtime private Panel field was not available");
                yield break;
            }
            AddCheck("panel.private-field", true, panel.GetType().FullName);

            Button toolbarEntry = GetFieldValue(panel, "toolbarEntry") as Button;
            Button bottomEntry = GetFieldValue(panel, "bottomEntry") as Button;
            Button actualEntry = toolbarEntry != null && toolbarEntry.gameObject.activeInHierarchy
                ? toolbarEntry : bottomEntry;
            bool entryAvailable = actualEntry != null && actualEntry.gameObject.activeInHierarchy;
            bool entryInteractable = entryAvailable && actualEntry.interactable;
            string entryDetail = DescribeToolbarEntry(actualEntry);
            AddCheck("panel.toolbar-entry-interactable", entryInteractable,
                entryDetail + "; active entry must be active and interactable before opening");

            bool entryInvoked = false;
            if (entryInteractable)
            {
                try
                {
                    actualEntry.onClick.Invoke();
                    entryInvoked = true;
                }
                catch (Exception error)
                {
                    RecordException("panel.toolbar-entry-click", error);
                }
            }
            float openDeadline = Time.realtimeSinceStartup + SecondsRemaining(20f);
            while (!Expired() && Time.realtimeSinceStartup < openDeadline)
            {
                bool interactive = GetFieldValue(panel, "interactive") is bool && (bool)GetFieldValue(panel, "interactive");
                if (panel.IsOpen && interactive && runtime != null && !runtime.CentralTransitioning) break;
                yield return null;
            }
            bool openedFromEntry = panel.IsOpen;
            if (!openedFromEntry)
            {
                // Keep a direct call only as a diagnostic fallback.  The
                // acceptance path above must exercise the real toolbar/bottom
                // entry listener first.
                panel.Open();
                float fallbackDeadline = Time.realtimeSinceStartup + SecondsRemaining(8f);
                while (!Expired() && Time.realtimeSinceStartup < fallbackDeadline)
                {
                    bool interactive = GetFieldValue(panel, "interactive") is bool && (bool)GetFieldValue(panel, "interactive");
                    if (panel.IsOpen && interactive && runtime != null && !runtime.CentralTransitioning) break;
                    yield return null;
                }
            }
            // Panel's coalesced measurement runs on the following frame.
            yield return null;
            yield return null;
            yield return StartCoroutine(WaitForStablePanelLayout(3f));
            bool panelInteractive = GetFieldValue(panel, "interactive") is bool && (bool)GetFieldValue(panel, "interactive");
            bool panelOpen = panel.IsOpen && panelInteractive;
            AddCheck("panel.toolbar-entry-click", entryInvoked && openedFromEntry && panelOpen,
                entryInvoked && openedFromEntry && panelOpen
                    ? "active toolbar/bottom entry onClick opened an interactive native modal"
                    : "entry click did not open the panel; direct Panel.Open fallback was " + (panelOpen ? "used" : "also unsuccessful"));
            AddCheck("panel.open", panelOpen, panelOpen ? "toolbar entry created an interactive native modal" : "Panel.Open did not become interactive");
        }

        string DescribeToolbarEntry(Button entry)
        {
            if (entry == null) return "entry=null";
            RectTransform rect = entry.GetComponent<RectTransform>();
            LayoutElement layout = entry.GetComponent<LayoutElement>();
            int persistent = 0;
            try { persistent = entry.onClick == null ? 0 : entry.onClick.GetPersistentEventCount(); } catch { }
            string rectDetail = rect == null ? "null" : DescribeRectDimensions(rect);
            string layoutDetail = layout == null ? "none" : "min="
                + layout.minWidth.ToString("0.###", CultureInfo.InvariantCulture) + "x"
                + layout.minHeight.ToString("0.###", CultureInfo.InvariantCulture)
                + ",preferred=" + layout.preferredWidth.ToString("0.###", CultureInfo.InvariantCulture)
                + "x" + layout.preferredHeight.ToString("0.###", CultureInfo.InvariantCulture)
                + ",flex=" + layout.flexibleWidth.ToString("0.###", CultureInfo.InvariantCulture)
                + "x" + layout.flexibleHeight.ToString("0.###", CultureInfo.InvariantCulture);
            return "entry=" + entry.gameObject.name + ",active=" + entry.gameObject.activeInHierarchy
                + ",interactable=" + entry.interactable + ",enabled=" + entry.enabled
                + ",persistentListeners=" + persistent.ToString(CultureInfo.InvariantCulture)
                + ",rect=" + rectDetail + ",layout={" + layoutDetail + "}";
        }

        AdvancedSongFilters.Panel GetPanel(AdvancedSongFilters.Runtime value)
        {
            FieldInfo field = GetFieldIncludingBases(value.GetType(), "panel");
            return field == null ? null : field.GetValue(value) as AdvancedSongFilters.Panel;
        }

        Component GetPanelSurface()
        {
            if (panel == null) return null;
            return (GetMemberValue(panel, "filterModal") as Component)
                ?? (GetMemberValue(panel, "boundFilterModal") as Component);
        }

        bool ValidatePanel()
        {
            if (panel == null || !panel.IsOpen) return false;
            Canvas.ForceUpdateCanvases();
            AddCheck("panel.open-state", true, "Panel.IsOpen=true");
            Component modal = GetPanelSurface();
            bool modalBound = modal != null && modal.GetComponent<RectTransform>() != null
                && modal.gameObject.activeInHierarchy;
            AddCheck("panel.native-modal-bound", modalBound,
                modalBound ? modal.GetType().FullName + " is active" : "Panel modal/filterModal field was not active");
            GameObject root = GetPanelRootObject();
            bool rootBound = root != null && root.activeInHierarchy;
            AddCheck("panel.native-root", rootBound,
                rootBound ? GetTransformPath(root.transform) : "panel root was not discoverable");

            FilterSettings draft = GetPanelDraft() as FilterSettings;
            object revision = GetPanelRevision();
            bool stateContract = draft != null && revision != null;
            AddCheck("panel.edit-session", stateContract,
                stateContract ? "Panel public Draft and Revision are available" : "Panel public Draft/Revision contract was not available");

            Button[] buttons = root == null ? new Button[0] : root.GetComponentsInChildren<Button>(true);
            int semanticMetrics = CountSemanticButtons(buttons, new[] { "nps", "njs", "duration", "bpm", "notes", "音符", "时长" });
            bool metrics = semanticMetrics >= 5 || HasMetricBindings();
            AddCheck("panel.common-metrics", metrics,
                "metric controls discovered=" + semanticMetrics.ToString(CultureInfo.InvariantCulture));

            bool selectors = HasSemanticButton(buttons, new[] { "all", "全部", "standard", "expert", "difficulty", "难度" });
            AddCheck("panel.direct-selectors", selectors,
                selectors ? "mode and difficulty selectors are present on the main panel" : "direct mode/difficulty selector controls were not found");

            string moreDetail;
            bool more = ValidateMoreControls(out moreDetail);
            AddCheck("panel.more-controls", more, moreDetail);

            string scopeStateDetail;
            bool scopeStates = ValidateScopeActiveStates(out scopeStateDetail);
            AddCheck("panel.scope-selector-active-state", scopeStates, scopeStateDetail);

            string translationDetail;
            bool translationBoundary = ValidateTranslationBoundary(root, out translationDetail);
            AddCheck("panel.translation-scope-boundary", translationBoundary, translationDetail);

            bool hierarchy = ValidateBindingHierarchy();
            AddCheck("panel.toolbar-binding", hierarchy, hierarchy ? "toolbar and native screen binding is available" : "toolbar/screen binding is incomplete");

            bool geometry = ValidateGeometry();
            AddCheck("panel.geometry", geometry, geometry ? "native modal is centered inside the original screen" : "native modal geometry failed centered/in-screen validation");
            string targetDetail;
            bool targetGeometry = ValidateTargetPanelGeometry(root, out targetDetail);
            AddCheck("panel.target-size-and-hitboxes", targetGeometry, targetDetail);

            bool exercisedNumeric = ExerciseNumericPage();
            AddCheck("panel.numeric-navigation", exercisedNumeric,
                exercisedNumeric ? "metric numeric editor opened and returned to the main panel" : "could not navigate a metric numeric editor");
            return modalBound && rootBound && stateContract && metrics && selectors && more
                && translationBoundary && scopeStates && hierarchy && geometry && targetGeometry && exercisedNumeric;
        }

        bool ClickScopeCellNative(int index)
        {
            string detail;
            return ClickSegmentedCellNative("boundScopeSelector", index, out detail);
        }

        bool ClickSegmentedCellNative(string selectorField, int index, out string detail)
        {
            detail = String.Empty;
            try
            {
                Component selector = GetMemberValue(panel, selectorField) as Component;
                if (selector == null)
                {
                    detail = selectorField + "=missing";
                    return false;
                }
                int selectedBefore = ReadIntMember(selector, "selectedCellNumber", -1);
                Component cell = FindActiveRegisteredSegmentedCell(selector, index, out detail);
                if (cell == null)
                {
                    return false;
                }
                MethodInfo click = FindMethod(cell.GetType(), "OnPointerClick", new[] { typeof(PointerEventData) });
                if (click == null)
                {
                    detail = selectorField + " cell OnPointerClick missing";
                    return false;
                }
                EventSystem eventSystem = EventSystem.current;
                if (eventSystem == null)
                {
                    EventSystem[] systems = Resources.FindObjectsOfTypeAll<EventSystem>();
                    eventSystem = systems.FirstOrDefault(x => x != null && x.gameObject.activeInHierarchy)
                        ?? systems.FirstOrDefault(x => x != null);
                }
                PointerEventData pointer = new PointerEventData(eventSystem);
                pointer.button = PointerEventData.InputButton.Left;
                pointer.clickCount = 1;
                pointer.eligibleForClick = true;
                RectTransform rect = cell.GetComponent<RectTransform>();
                if (rect != null)
                {
                    Camera camera = FindMenuCamera();
                    pointer.position = camera == null ? Vector2.zero
                        : RectTransformUtility.WorldToScreenPoint(camera, rect.position);
                }
                click.Invoke(cell, new object[] { pointer });
                object selected = GetMemberValue(selector, "selectedCellNumber");
                int selectedAfter = selected == null ? -1 : Convert.ToInt32(selected, CultureInfo.InvariantCulture);
                bool selectedExpected = selectedAfter == index;
                detail = selectorField + " native registered SelectableCell.OnPointerClick(" + index.ToString(CultureInfo.InvariantCulture)
                    + ") before=" + selectedBefore.ToString(CultureInfo.InvariantCulture)
                    + ",selected=" + selectedAfter.ToString(CultureInfo.InvariantCulture)
                    + ",interactive=" + ReadBoolMember(panel, "interactive", false)
                    + ",syncingSelectors=" + ReadBoolMember(panel, "syncingSelectors", false)
                    + ",menuActive=" + (runtime != null && runtime.MenuActive)
                    + ",matched=" + selectedExpected;
                return selectedExpected;
            }
            catch (Exception error)
            {
                detail = selectorField + " native cell click exception: " + DescribeException(error);
                RecordException("panel.native-segmented-cell", error);
                return false;
            }
        }

        Component FindActiveRegisteredSegmentedCell(Component selector, int index, out string detail)
        {
            detail = String.Empty;
            if (selector == null)
            {
                detail = "segmented selector missing";
                return null;
            }
            object value = GetMemberValue(selector, "cells");
            IEnumerable registered = value as IEnumerable;
            if (registered == null)
            {
                detail = selector.GetType().Name + ".cells unavailable";
                return null;
            }
            int total = 0;
            int active = 0;
            int matchingNumber = 0;
            Transform selectorTransform = selector.transform;
            foreach (object item in registered)
            {
                total++;
                Component cell = item as Component;
                if (cell == null || cell.gameObject == null) continue;
                if (!cell.gameObject.activeInHierarchy) continue;
                active++;
                if (selectorTransform == null || cell.transform == null || !cell.transform.IsChildOf(selectorTransform)) continue;
                object numberValue = GetMemberValue(cell, "cellNumber");
                int number;
                try { number = numberValue == null ? -1 : Convert.ToInt32(numberValue, CultureInfo.InvariantCulture); }
                catch { number = -1; }
                if (number != index) continue;
                matchingNumber++;
                MethodInfo click = FindMethod(cell.GetType(), "OnPointerClick", new[] { typeof(PointerEventData) });
                if (click == null) continue;
                detail = selector.GetType().Name + " registeredCells=" + total.ToString(CultureInfo.InvariantCulture)
                    + ",active=" + active.ToString(CultureInfo.InvariantCulture)
                    + ",matchingCell=" + matchingNumber.ToString(CultureInfo.InvariantCulture)
                    + ",cell=" + GetTransformPath(cell.transform);
                return cell;
            }
            detail = selector.GetType().Name + " registeredCells=" + total.ToString(CultureInfo.InvariantCulture)
                + ",active=" + active.ToString(CultureInfo.InvariantCulture)
                + ",matchingCell=" + matchingNumber.ToString(CultureInfo.InvariantCulture)
                + ",requested=" + index.ToString(CultureInfo.InvariantCulture)
                + "; no active registered cell with that cellNumber";
            return null;
        }

        bool ValidateMoreControls(out string detail)
        {
            detail = String.Empty;
            Component selector = GetMemberValue(panel, "boundScopeSelector") as Component;
            GameObject morePage = GetMemberValue(panel, "boundMorePage") as GameObject;
            bool segmentedMore = false;
            if (selector != null)
            {
                Component[] children = selector.GetComponentsInChildren<Component>(true);
                for (int i = 0; i < children.Length; i++)
                {
                    Component child = children[i];
                    if (child == null || child.GetType().Name.IndexOf("SelectableCell", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    TMP_Text[] labels = child.GetComponentsInChildren<TMP_Text>(true);
                    for (int t = 0; t < labels.Length; t++)
                    {
                        string text = labels[t] == null ? String.Empty : labels[t].text ?? String.Empty;
                        if (text.IndexOf("更多", StringComparison.Ordinal) >= 0
                            || text.IndexOf("More", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            segmentedMore = true;
                            break;
                        }
                    }
                    if (segmentedMore) break;
                }
            }
            string openDetail;
            bool openedMore = ClickSegmentedCellNative("boundScopeSelector", 1, out openDetail);
            bool pageActive = morePage != null && morePage.activeInHierarchy;
            bool bpm = false;
            bool notes = false;
            if (morePage != null)
            {
                TMP_Text[] labels = morePage.GetComponentsInChildren<TMP_Text>(true);
                for (int i = 0; i < labels.Length; i++)
                {
                    string text = labels[i] == null ? String.Empty : labels[i].text ?? String.Empty;
                    if (String.Equals(text.Trim(), "BPM", StringComparison.OrdinalIgnoreCase)) bpm = true;
                    if (String.Equals(text.Trim(), "音符", StringComparison.Ordinal)
                        || String.Equals(text.Trim(), "Notes", StringComparison.OrdinalIgnoreCase)) notes = true;
                }
            }
            string returnDetail;
            bool returnedCommon = ClickSegmentedCellNative("boundScopeSelector", 0, out returnDetail);
            detail = "segmentedMoreCell=" + segmentedMore + ",openedMore=" + openedMore
                + ",morePageActive=" + pageActive + ",BPM=" + bpm + ",Notes=" + notes
                + ",returnedCommon=" + returnedCommon + ",open=" + openDetail + ",return=" + returnDetail;
            return segmentedMore && openedMore && pageActive && bpm && notes && returnedCommon;
        }

        bool ValidateScopeActiveStates(out string detail)
        {
            detail = String.Empty;
            GameObject common = GetMemberValue(panel, "boundCommonPage") as GameObject;
            GameObject more = GetMemberValue(panel, "boundMorePage") as GameObject;
            bool initialCommon = common != null && common.activeInHierarchy;
            bool initialMore = more != null && more.activeInHierarchy;
            string openDetail;
            bool openedMore = ClickSegmentedCellNative("boundScopeSelector", 1, out openDetail);
            bool moreActive = more != null && more.activeInHierarchy;
            bool commonHidden = common == null || !common.activeInHierarchy;
            string returnDetail;
            bool returnedCommon = ClickSegmentedCellNative("boundScopeSelector", 0, out returnDetail);
            bool commonRestored = common != null && common.activeInHierarchy;
            bool moreHidden = more == null || !more.activeInHierarchy;
            detail = "initialCommon=" + initialCommon + ",initialMore=" + initialMore
                + ",openedMore=" + openedMore + ",moreActive=" + moreActive
                + ",commonHidden=" + commonHidden + ",returnedCommon=" + returnedCommon
                + ",commonRestored=" + commonRestored + ",moreHidden=" + moreHidden
                + ",open=" + openDetail + ",return=" + returnDetail;
            return initialCommon && !initialMore && openedMore && moreActive && commonHidden
                && returnedCommon && commonRestored && moreHidden;
        }

        bool ValidateTranslationBoundary(GameObject root, out string detail)
        {
            detail = String.Empty;
            if (root == null)
            {
                detail = "panel root unavailable for scoped label validation";
                return false;
            }
            TMP_Text[] texts = root.GetComponentsInChildren<TMP_Text>(true);
            bool njs = false;
            bool bpm = false;
            bool expandedNjs = false;
            bool expandedBpm = false;
            for (int i = 0; i < texts.Length; i++)
            {
                string value = texts[i] == null ? String.Empty : (texts[i].text ?? String.Empty).Trim();
                if (value == "NJS") njs = true;
                if (value == "BPM") bpm = true;
                if (value.IndexOf("音符跳跃速度", StringComparison.Ordinal) >= 0) expandedNjs = true;
                if (value.IndexOf("每分钟节拍数", StringComparison.Ordinal) >= 0) expandedBpm = true;
            }
            Type scopeType = FindLoadedType("ModUiChinese.UiScope");
            GameObject mainScreen = FindMainScreenObject();
            bool mainScreenClean = scopeType != null && mainScreen != null;
            int advancedOnMain = 0;
            int nativeScopeLeaks = 0;
            string firstNativeLeak = String.Empty;
            if (scopeType != null)
            {
                if (mainScreen != null)
                {
                    Component[] ownComponents = mainScreen.GetComponents<Component>();
                    for (int i = 0; i < ownComponents.Length; i++)
                    {
                        Component marker = ownComponents[i];
                        if (marker == null || !scopeType.IsInstanceOfType(marker)) continue;
                        string scope = Convert.ToString(GetMemberValue(marker, "Scope"), CultureInfo.InvariantCulture);
                        if (String.Equals(scope, "AdvancedSongFilters", StringComparison.Ordinal)) advancedOnMain++;
                    }
                    Component[] controls = Resources.FindObjectsOfTypeAll<Component>();
                    for (int i = 0; i < controls.Length; i++)
                    {
                        Component control = controls[i];
                        if (control == null || !control.gameObject.activeInHierarchy
                            || (!typeof(Button).IsAssignableFrom(control.GetType())
                                && !typeof(TMP_Text).IsAssignableFrom(control.GetType()))) continue;
                        if (!IsUnderTransform(control.transform, mainScreen.transform)
                            || IsUnderTransform(control.transform, root.transform)
                            || IsPluginOwnedUi(control.transform)) continue;
                        string effective = EffectiveUiScope(control.transform, scopeType);
                        if (String.Equals(effective, "AdvancedSongFilters", StringComparison.Ordinal))
                        {
                            nativeScopeLeaks++;
                            if (String.IsNullOrEmpty(firstNativeLeak)) firstNativeLeak = GetTransformPath(control.transform);
                        }
                    }
                }
                mainScreenClean = mainScreen != null && advancedOnMain == 0 && nativeScopeLeaks == 0;
            }
            detail = "NJS=" + njs + ",BPM=" + bpm + ",expandedNJS=" + expandedNjs
                + ",expandedBPM=" + expandedBpm + ",uiScopeType=" + (scopeType == null ? "missing" : scopeType.FullName)
                + ",mainScreen=" + (mainScreen == null ? "missing" : GetTransformPath(mainScreen.transform))
                + ",advancedOnMainScreenOwnComponents=" + advancedOnMain
                + ",nativeScopeLeaks=" + nativeScopeLeaks
                + (String.IsNullOrEmpty(firstNativeLeak) ? String.Empty : ",firstLeak=" + firstNativeLeak);
            return njs && bpm && !expandedNjs && !expandedBpm && mainScreenClean;
        }

        Type FindLoadedType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type value = assembly.GetType(fullName, false);
                if (value != null) return value;
            }
            return null;
        }

        GameObject FindMainScreenObject()
        {
            Transform[] transforms = Resources.FindObjectsOfTypeAll<Transform>();
            GameObject fallback = null;
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform value = transforms[i];
                if (value == null || !String.Equals(value.name, "MainScreen", StringComparison.Ordinal)) continue;
                if (value.gameObject.activeInHierarchy) return value.gameObject;
                if (fallback == null) fallback = value.gameObject;
            }
            return fallback;
        }

        bool IsUnderTransform(Transform value, Transform root)
        {
            return value != null && root != null && (value == root || value.IsChildOf(root));
        }

        bool IsPluginOwnedUi(Transform value)
        {
            for (Transform current = value; current != null; current = current.parent)
            {
                string name = current.name ?? String.Empty;
                if (name.IndexOf("AdvancedSongFilters", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        string EffectiveUiScope(Transform value, Type scopeType)
        {
            if (value == null || scopeType == null) return null;
            for (Transform current = value; current != null; current = current.parent)
            {
                Component marker = current.GetComponent(scopeType);
                if (marker == null) continue;
                return Convert.ToString(GetMemberValue(marker, "Scope"), CultureInfo.InvariantCulture);
            }
            return null;
        }

        GameObject GetPanelRootObject()
        {
            string[] names = { "panelRoot", "boundPanelRoot" };
            for (int i = 0; i < names.Length; i++)
            {
                object value = GetMemberValue(panel, names[i]);
                GameObject gameObject = value as GameObject;
                if (gameObject != null) return gameObject;
                Component component = value as Component;
                if (component != null) return component.gameObject;
            }
            return null;
        }

        object GetPanelDraft()
        {
            return GetMemberValue(panel, "Draft");
        }

        object GetPanelRevision()
        {
            return GetMemberValue(panel, "Revision");
        }

        bool HasMetricBindings()
        {
            string[] names = { "Nps", "Njs", "Duration", "Bpm", "Notes" };
            int found = 0;
            for (int i = 0; i < names.Length; i++)
            {
                if (FindMethod(panel.GetType(), "Open" + names[i] + "Min", Type.EmptyTypes) != null
                    || FindMethod(panel.GetType(), "Open" + names[i] + "Max", Type.EmptyTypes) != null) found++;
            }
            return found >= 3;
        }

        int CountSemanticButtons(Button[] buttons, string[] tokens)
        {
            int found = 0;
            for (int i = 0; i < buttons.Length; i++)
            {
                if (buttons[i] != null && buttons[i].gameObject.activeInHierarchy
                    && HasToken(ButtonSemanticText(buttons[i]), tokens)) found++;
            }
            return found;
        }

        bool HasSemanticButton(Button[] buttons, string[] tokens)
        {
            return CountSemanticButtons(buttons, tokens) > 0;
        }

        string ButtonSemanticText(Button button)
        {
            if (button == null) return String.Empty;
            return (GetTransformPath(button.transform) + "|" + ReadButtonText(button)).ToLowerInvariant();
        }

        bool HasToken(string value, string[] tokens)
        {
            if (String.IsNullOrEmpty(value)) return false;
            for (int i = 0; i < tokens.Length; i++)
                if (value.IndexOf(tokens[i].ToLowerInvariant(), StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        bool ValidateTargetPanelGeometry(GameObject root, out string detail)
        {
            detail = String.Empty;
            Component modal = GetPanelSurface();
            RectTransform rect = modal == null ? null : modal.GetComponent<RectTransform>();
            if (rect == null && modal != null) rect = modal.GetComponentInChildren<RectTransform>(true);
            if (rect == null) { detail = "modal RectTransform unavailable"; return false; }
            float width = Mathf.Abs(rect.rect.width);
            float height = Mathf.Abs(rect.rect.height);
            bool target = Mathf.Abs(width - 150f) <= 6f && Mathf.Abs(height - 76f) <= 6f;
            int checkedButtons = 0;
            int tooSmall = 0;
            if (root != null)
            {
                Button[] buttons = root.GetComponentsInChildren<Button>(true);
                for (int i = 0; i < buttons.Length; i++)
                {
                    Button button = buttons[i];
                    if (button == null || !button.gameObject.activeInHierarchy) continue;
                    RectTransform buttonRect = button.GetComponent<RectTransform>();
                    if (buttonRect == null) continue;
                    checkedButtons++;
                    if (buttonRect.rect.height < 7.5f) tooSmall++;
                }
            }
            bool hitboxes = checkedButtons > 0 && tooSmall == 0;
            detail = "modal=" + width.ToString("0.##", CultureInfo.InvariantCulture) + "x"
                + height.ToString("0.##", CultureInfo.InvariantCulture) + ",target=150x76"
                + ",buttons=" + checkedButtons.ToString(CultureInfo.InvariantCulture)
                + ",below8=" + tooSmall.ToString(CultureInfo.InvariantCulture);
            return target && hitboxes;
        }

        bool ValidateBindingHierarchy()
        {
            RectTransform toolbarRoot = GetFieldValue(panel, "toolbarRoot") as RectTransform;
            RectTransform toolbarSlot = GetFieldValue(panel, "toolbarSlot") as RectTransform;
            Component screen = GetFieldValue(panel, "screen") as Component;
            RectTransform screenRect = GetMemberValue(panel, "screenRect") as RectTransform;
            if (screenRect == null)
            {
                Component modal = GetPanelSurface();
                if (modal != null) screenRect = modal.transform.parent == null
                    ? null : modal.transform.parent.GetComponent<RectTransform>();
            }
            if (screen == null && screenRect != null) screen = screenRect.GetComponent<Component>();
            Button toolbarEntry = GetFieldValue(panel, "toolbarEntry") as Button;
            Button bottomEntry = GetFieldValue(panel, "bottomEntry") as Button;
            Button actualEntry = toolbarEntry != null && toolbarEntry.gameObject.activeInHierarchy ? toolbarEntry : bottomEntry;
            bool entryValid = actualEntry != null && actualEntry.gameObject.activeInHierarchy;
            if (entryValid)
            {
                RectTransform entryRect = actualEntry.GetComponent<RectTransform>();
                if (entryRect == null) entryValid = false;
                else
                {
                    float width = ProjectedSpan(entryRect, entryRect.right.normalized);
                    float height = ProjectedSpan(entryRect, entryRect.up.normalized);
                    LayoutElement layout = actualEntry.GetComponent<LayoutElement>();
                    entryValid = width > 1e-5f && height > 1e-5f
                        && (layout == null || (layout.minWidth > 0f && layout.minHeight >= 8f));
                }
            }
            // Some game versions expose only the native toolbar Button and
            // the panel owns no toolbarRoot/toolbarSlot fields. In that case
            // the active modal/root is the binding evidence.
            bool shellValid = screenRect != null && screenRect.rect.width > 1e-5f
                && screenRect.rect.height > 1e-5f;
            return shellValid && (entryValid || GetPanelRootObject() != null);
        }

        bool ValidateGeometry()
        {
            RectTransform screenRect = GetMemberValue(panel, "screenRect") as RectTransform;
            Component screen = GetMemberValue(panel, "screen") as Component;
            Component modal = GetPanelSurface();
            if (screenRect == null && modal != null && modal.transform.parent != null)
                screenRect = modal.transform.parent.GetComponent<RectTransform>();
            if (screen == null) screen = screenRect == null ? null : screenRect.GetComponent<Component>();
            if (screenRect == null || screen == null || modal == null) return false;
            RectTransform modalRect = modal.GetComponent<RectTransform>();
            if (modalRect == null) modalRect = modal.GetComponentInChildren<RectTransform>(true);
            if (modalRect == null) return false;
            Vector3[] centralCorners = new Vector3[4];
            Vector3[] modalCorners = new Vector3[4];
            screenRect.GetWorldCorners(centralCorners);
            modalRect.GetWorldCorners(modalCorners);
            Vector3 center = (centralCorners[0] + centralCorners[2]) * 0.5f;
            Vector3 right = screenRect.right.normalized;
            Vector3 up = screenRect.up.normalized;
            Vector3 normal = screenRect.forward.normalized;
            float centralWidth = ProjectedSpan(centralCorners, right);
            float centralHeight = ProjectedSpan(centralCorners, up);
            float modalWidth = ProjectedSpan(modalCorners, right);
            float modalHeight = ProjectedSpan(modalCorners, up);
            bool bounds = centralWidth > 1e-5f && centralHeight > 1e-5f
                && modalWidth > 1e-5f && modalHeight > 1e-5f
                && AllFinite(centralCorners) && AllFinite(modalCorners);
            if (!bounds) return false;

            Rect centralBounds;
            Rect modalBounds = new Rect();
            Vector3 centralOrigin = center;
            bool projected = ProjectBounds(centralCorners, centralOrigin, right, up, out centralBounds)
                && ProjectBounds(modalCorners, centralOrigin, right, up, out modalBounds);
            Vector3 modalCenter = (modalCorners[0] + modalCorners[2]) * 0.5f;
            Vector3 centerDelta = modalCenter - center;
            float centerToleranceX = Mathf.Max(GeometryToleranceUnits, centralWidth * 0.05f);
            float centerToleranceY = Mathf.Max(GeometryToleranceUnits, centralHeight * 0.05f);
            bool centered = Mathf.Abs(Vector3.Dot(centerDelta, right)) <= centerToleranceX
                && Mathf.Abs(Vector3.Dot(centerDelta, up)) <= centerToleranceY;
            bool inside = projected && RectContainsWithTolerance(centralBounds, modalBounds, GeometryToleranceUnits);
            bool coplanar = Mathf.Abs(Vector3.Dot(centerDelta, normal)) <= Mathf.Max(GeometryToleranceUnits, 0.02f);
            Transform modalParent = modal.transform.parent;
            Transform screenParent = screenRect.transform.parent;
            bool nativeParent = modalParent == screenRect.transform
                || modalParent == screenParent
                || (screenParent != null && modal.transform.IsChildOf(screenParent));
            return projected && centered && inside && coplanar && nativeParent;
        }

        bool ValidateModalGeometry(RectTransform centralRect, RectTransform modalRect)
        {
            if (centralRect == null || modalRect == null) return false;
            Vector3[] centralCorners = new Vector3[4];
            Vector3[] modalCorners = new Vector3[4];
            centralRect.GetWorldCorners(centralCorners);
            modalRect.GetWorldCorners(modalCorners);
            Vector3 center = (centralCorners[0] + centralCorners[2]) * 0.5f;
            Vector3 right = centralRect.right.normalized;
            Vector3 up = centralRect.up.normalized;
            Vector3 normal = centralRect.forward.normalized;
            Rect centralBounds;
            Rect modalBounds;
            if (!ProjectBounds(centralCorners, center, right, up, out centralBounds)
                || !ProjectBounds(modalCorners, center, right, up, out modalBounds)) return false;
            float width = centralBounds.width;
            float height = centralBounds.height;
            Vector3 modalCenter = (modalCorners[0] + modalCorners[2]) * 0.5f;
            Vector3 delta = modalCenter - center;
            bool centered = Mathf.Abs(Vector3.Dot(delta, right)) <= Mathf.Max(GeometryToleranceUnits, width * 0.05f)
                && Mathf.Abs(Vector3.Dot(delta, up)) <= Mathf.Max(GeometryToleranceUnits, height * 0.05f);
            bool inside = RectContainsWithTolerance(centralBounds, modalBounds, GeometryToleranceUnits);
            bool coplanar = Mathf.Abs(Vector3.Dot(delta, normal)) <= Mathf.Max(GeometryToleranceUnits, 0.02f);
            return modalRect.gameObject.activeInHierarchy && centered && inside && coplanar;
        }

        bool ValidateNumericKeypadViewport(out string detail)
        {
            detail = String.Empty;
            try
            {
                GameObject numericPage = FindNumericSurface();
                RectTransform viewport = numericPage == null ? null : numericPage.GetComponent<RectTransform>();
                if (viewport == null && numericPage != null) viewport = FindScrollViewport(numericPage);
                if (viewport == null) viewport = FindScrollViewport(GetPanelRootObject());
                if (numericPage == null || viewport == null || !numericPage.activeInHierarchy)
                {
                    detail = "range editor/viewport unavailable";
                    return false;
                }
                Vector3[] viewportCorners = new Vector3[4];
                viewport.GetWorldCorners(viewportCorners);
                Vector3 origin = (viewportCorners[0] + viewportCorners[2]) * 0.5f;
                Vector3 right = viewport.right.normalized;
                Vector3 up = viewport.up.normalized;
                Rect viewportBounds;
                if (!ProjectBounds(viewportCorners, origin, right, up, out viewportBounds))
                {
                    detail = "viewport projection failed";
                    return false;
                }
                Button minimumButton = GetMemberValue(panel, "boundRangeMinimum") as Button;
                Button maximumButton = GetMemberValue(panel, "boundRangeMaximum") as Button;
                Button backButton = null;
                Button commitButton = null;
                Button[] buttons = numericPage.GetComponentsInChildren<Button>(true);
                for (int i = 0; i < buttons.Length; i++)
                {
                    Button button = buttons[i];
                    if (button == null || !button.gameObject.activeInHierarchy) continue;
                    string text = ReadButtonText(button);
                    string semantic = ButtonSemanticText(button);
                    if (minimumButton == null && HasToken(semantic, new[] { "rangeminimum", "minimum", "下限" }))
                        minimumButton = button;
                    if (maximumButton == null && HasToken(semantic, new[] { "rangemaximum", "maximum", "上限" }))
                        maximumButton = button;
                    if (backButton == null && HasToken(semantic, new[] { "cancelrange", "back", "返回" }))
                        backButton = button;
                    if (commitButton == null && (text.IndexOf("确定", StringComparison.Ordinal) >= 0
                        || text.IndexOf("confirm", StringComparison.OrdinalIgnoreCase) >= 0
                        || text.IndexOf("apply", StringComparison.OrdinalIgnoreCase) >= 0
                        || text.IndexOf("ok", StringComparison.OrdinalIgnoreCase) >= 0))
                        commitButton = button;
                }
                bool minimumInside;
                bool maximumInside;
                bool backInside;
                bool commitInside;
                string minimumDetail = DescribePageButton("minimum", minimumButton, origin, right, up,
                    viewportBounds, out minimumInside);
                string maximumDetail = DescribePageButton("maximum", maximumButton, origin, right, up,
                    viewportBounds, out maximumInside);
                string backDetail = DescribeRawKeyButton("back", backButton, origin, right, up,
                    viewportBounds, out backInside);
                string commitDetail = DescribeRawKeyButton("commit", commitButton, origin, right, up,
                    viewportBounds, out commitInside);
                detail = "viewport=" + FormatRect(viewportBounds) + "; " + minimumDetail + "; "
                    + maximumDetail + "; " + backDetail + "; " + commitDetail;
                return minimumInside && maximumInside && backInside && commitInside;
            }
            catch (Exception error)
            {
                detail = "numeric keypad viewport exception: " + DescribeException(error);
                RecordException("panel.numeric-keypad-viewport", error);
                return false;
            }
        }

        string DescribeRawKeyButton(string label, Button button, Vector3 origin, Vector3 right,
            Vector3 up, Rect viewportBounds, out bool inside)
        {
            inside = false;
            if (button == null) return label + "=missing";
            RectTransform rect = button.GetComponent<RectTransform>();
            if (rect == null) return label + "=noRect";
            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            Rect rawBounds;
            bool projected = ProjectBounds(corners, origin, right, up, out rawBounds);
            inside = projected && rect.gameObject.activeInHierarchy && button.enabled
                && button.interactable && RectContainsWithTolerance(viewportBounds, rawBounds, 0.001f);
            return label + "={active=" + rect.gameObject.activeInHierarchy
                + ",enabled=" + button.enabled + ",interactable=" + button.interactable
                + ",raw=" + (projected ? FormatRect(rawBounds) : "projectionFailed")
                + ",insideViewport=" + inside + "}";
        }

        string DescribePageButton(string label, Button button, Vector3 origin, Vector3 right,
            Vector3 up, Rect viewportBounds, out bool inside)
        {
            inside = false;
            if (button == null) return label + "=missing";
            RectTransform rect = button.GetComponent<RectTransform>();
            if (rect == null) return label + "=noRect";
            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            Rect rawBounds;
            bool projected = ProjectBounds(corners, origin, right, up, out rawBounds);
            inside = projected && rect.gameObject.activeSelf && button.enabled
                && RectContainsWithTolerance(viewportBounds, rawBounds, 0.001f);
            return label + "={active=" + rect.gameObject.activeInHierarchy
                + ",activeSelf=" + rect.gameObject.activeSelf + ",enabled=" + button.enabled
                + ",interactable=" + button.interactable
                + ",raw=" + (projected ? FormatRect(rawBounds) : "projectionFailed")
                + ",insideViewport=" + inside + "}";
        }

        bool ValidateInteractiveGeometry(out string detail)
        {
            detail = String.Empty;
            try
            {
                GameObject panelRoot = GetPanelRootObject();
                Component floating = GetPanelSurface();
                RectTransform screenRect = GetMemberValue(panel, "screenRect") as RectTransform;
                if (screenRect == null && panelRoot != null) screenRect = panelRoot.GetComponentInParent<RectTransform>();
                RectTransform floatingRect = floating == null ? null : floating.GetComponent<RectTransform>();
                if (floatingRect == null && floating != null) floatingRect = floating.GetComponentInChildren<RectTransform>(true);
                if (panelRoot == null || floating == null || screenRect == null || floatingRect == null)
                {
                    detail = "form geometry unavailable: panelRoot/modal/screenRect missing";
                    return false;
                }
                Vector3[] floatingCorners = new Vector3[4];
                floatingRect.GetWorldCorners(floatingCorners);
                Vector3 floatingOrigin = (floatingCorners[0] + floatingCorners[2]) * 0.5f;
                Vector3 right = floatingRect.right.normalized;
                Vector3 up = floatingRect.up.normalized;
                Rect floatingBounds;
                if (!ProjectBounds(floatingCorners, floatingOrigin, right, up, out floatingBounds))
                {
                    detail = "form geometry projection failed";
                    return false;
                }
                RectTransform viewportRect = FindScrollViewport(panelRoot);
                Rect viewportBounds = new Rect();
                bool hasViewportBounds = false;
                if (viewportRect != null)
                {
                    Vector3[] viewportCorners = new Vector3[4];
                    viewportRect.GetWorldCorners(viewportCorners);
                    hasViewportBounds = ProjectBounds(viewportCorners, floatingOrigin, right, up, out viewportBounds);
                }
                RectTransform[] rects = panelRoot.GetComponentsInChildren<RectTransform>(true);
                int controls = 0;
                int outside = 0;
                string outsideNames = String.Empty;
                string outsideDetails = String.Empty;
                for (int i = 0; i < rects.Length; i++)
                {
                    RectTransform rect = rects[i];
                    if (rect == null || !rect.gameObject.activeInHierarchy || !IsInteractiveRect(rect)) continue;
                    if (IsScrollStructuralRect(rect)) continue;
                    Graphic directGraphic = GetEffectiveGraphic(rect);
                    if (directGraphic == null) continue;
                    if (!directGraphic.enabled || !directGraphic.gameObject.activeInHierarchy
                        || directGraphic.canvasRenderer.cull
                        || directGraphic.canvasRenderer.GetColor().a <= 0.001f) continue;
                    Vector3[] corners;
                    if (!TryGetEffectiveGraphicCorners(directGraphic, out corners)) continue;
                    Rect bounds;
                    Rect effectiveBounds;
                    bool visibleBounds = ProjectBounds(corners, floatingOrigin, right, up, out bounds);
                    if (visibleBounds && hasViewportBounds && (rect == viewportRect || rect.IsChildOf(viewportRect)))
                        visibleBounds = IntersectRects(bounds, viewportBounds, out effectiveBounds);
                    else effectiveBounds = bounds;
                    Rect ancestorClip;
                    if (visibleBounds && TryGetAncestorClipBounds(rect, floatingOrigin, right, up, out ancestorClip))
                        visibleBounds = IntersectRects(effectiveBounds, ancestorClip, out effectiveBounds);
                    if (!visibleBounds) continue; // Entirely masked scroll content has no visible/clickable region.
                    controls++;
                    if (!RectContainsWithTolerance(floatingBounds, effectiveBounds, 0.1f))
                    {
                        outside++;
                        if (outsideNames.Length < 256)
                        {
                            if (outsideNames.Length > 0) outsideNames += ",";
                            outsideNames += rect.name;
                        }
                        if (outsideDetails.Length < 4096)
                        {
                            if (outsideDetails.Length > 0) outsideDetails += ";";
                            outsideDetails += DescribeOutsideGraphic(rect, directGraphic, bounds,
                                visibleBounds, effectiveBounds, viewportRect);
                        }
                    }
                }

                // The native HMUI modal intentionally sits above the original
                // menu and blocks it while open.  Original detail/practice/
                // start controls are therefore allowed to occupy the same
                // screen projection; blocker/raycast checks below verify the
                // input boundary instead of treating this expected overlap as
                // a form geometry failure.
                bool valid = controls > 0 && outside == 0;
                detail = "activeControls=" + controls.ToString(CultureInfo.InvariantCulture)
                    + ",outsideModal=" + outside.ToString(CultureInfo.InvariantCulture)
                    + (outside == 0 ? String.Empty : "[" + outsideNames + "]")
                    + ",nativeModalAllowsOriginalOverlap=true"
                    + ",modalBounds=" + FormatRect(floatingBounds)
                    + (outside == 0 ? String.Empty : ",outsideDetails={" + outsideDetails + "}");
                return valid;
            }
            catch (Exception error)
            {
                detail = "form geometry exception: " + DescribeException(error);
                RecordException("panel.form-geometry", error);
                return false;
            }
        }

        bool IsInteractiveRect(RectTransform rect)
        {
            if (rect == null) return false;
            Graphic graphic = rect.GetComponent<Graphic>();
            if (graphic != null) return true;
            Selectable selectable = rect.GetComponent<Selectable>();
            if (selectable == null) return false;
            // A Selectable without its own target graphic is a structural
            // BSML node.  Its child target is visited separately by the
            // RectTransform walk, so counting the parent would overstate the
            // panel bounds and produce false out-of-viewport failures.
            Graphic target = selectable.targetGraphic;
            return target != null && target.transform == rect;
        }

        Graphic GetEffectiveGraphic(RectTransform rect)
        {
            if (rect == null) return null;
            Graphic graphic = rect.GetComponent<Graphic>();
            if (graphic != null) return graphic;
            Selectable selectable = rect.GetComponent<Selectable>();
            if (selectable == null || selectable.targetGraphic == null
                || selectable.targetGraphic.transform != rect) return null;
            return selectable.targetGraphic;
        }

        bool TryGetEffectiveGraphicCorners(Graphic graphic, out Vector3[] corners)
        {
            corners = null;
            if (graphic == null) return false;
            TMP_Text text = graphic as TMP_Text;
            if (text != null)
            {
                try
                {
                    TMP_TextInfo info = text.textInfo;
                    List<Vector3> vertices = new List<Vector3>();
                    if (info != null && info.characterInfo != null && info.meshInfo != null)
                    {
                        for (int i = 0; i < info.characterCount && i < info.characterInfo.Length; i++)
                        {
                            TMP_CharacterInfo character = info.characterInfo[i];
                            if (!character.isVisible) continue;
                            int materialIndex = character.materialReferenceIndex;
                            int vertexIndex = character.vertexIndex;
                            if (materialIndex < 0 || materialIndex >= info.meshInfo.Length) continue;
                            Vector3[] source = info.meshInfo[materialIndex].vertices;
                            if (source == null || vertexIndex < 0 || vertexIndex + 3 >= source.Length) continue;
                            for (int vertex = 0; vertex < 4; vertex++)
                                vertices.Add(text.transform.TransformPoint(source[vertexIndex + vertex]));
                        }
                    }
                    if (vertices.Count > 0)
                    {
                        corners = vertices.ToArray();
                        return true;
                    }
                    // Empty/whitespace TMP graphics do not contribute visible
                    // geometry.  Skipping them avoids treating a large BSML
                    // text container as an out-of-bounds control.
                    return false;
                }
                catch
                {
                    return false;
                }
            }
            corners = new Vector3[4];
            graphic.rectTransform.GetWorldCorners(corners);
            return true;
        }

        string DescribeOutsideGraphic(RectTransform rect, Graphic graphic, Rect rawBounds,
            bool visible, Rect effectiveBounds, RectTransform viewport)
        {
            string path = GetTransformPath(rect);
            string type = graphic == null ? "null" : graphic.GetType().Name;
            string viewportInfo = viewport == null ? "none" : GetTransformPath(viewport);
            return type + "@" + path + ":raw=" + FormatRect(rawBounds)
                + ",effective=" + FormatRect(effectiveBounds)
                + ",visibleAfterClip=" + visible + ",viewport=" + viewportInfo;
        }

        bool IsScrollStructuralRect(RectTransform rect)
        {
            string name = rect == null ? String.Empty : rect.name.ToLowerInvariant();
            return name == "content" || name == "viewport"
                || name.IndexOf("scrollablecontainer", StringComparison.Ordinal) >= 0
                || name.IndexOf("scrollrect", StringComparison.Ordinal) >= 0;
        }

        RectTransform FindScrollViewport(GameObject panelRoot)
        {
            if (panelRoot == null) return null;
            RectTransform[] rects = panelRoot.GetComponentsInChildren<RectTransform>(true);
            return rects.FirstOrDefault(x => x != null
                && x.name.IndexOf("viewport", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        bool TryGetAncestorClipBounds(RectTransform rect, Vector3 origin, Vector3 right, Vector3 up, out Rect clipBounds)
        {
            clipBounds = new Rect();
            bool found = false;
            Mask[] masks = rect.GetComponentsInParent<Mask>(true);
            for (int i = 0; i < masks.Length; i++)
            {
                if (masks[i] == null || !masks[i].isActiveAndEnabled) continue;
                RectTransform maskRect = masks[i] == null ? null : masks[i].GetComponent<RectTransform>();
                Rect maskBounds;
                if (maskRect != null && ProjectBoundsFromRect(maskRect, origin, right, up, out maskBounds))
                {
                    clipBounds = found ? IntersectOrEmpty(clipBounds, maskBounds) : maskBounds;
                    found = true;
                }
            }
            RectMask2D[] rectMasks = rect.GetComponentsInParent<RectMask2D>(true);
            for (int i = 0; i < rectMasks.Length; i++)
            {
                if (rectMasks[i] == null || !rectMasks[i].isActiveAndEnabled) continue;
                RectTransform maskRect = rectMasks[i] == null ? null : rectMasks[i].GetComponent<RectTransform>();
                Rect maskBounds;
                if (maskRect != null && ProjectBoundsFromRect(maskRect, origin, right, up, out maskBounds))
                {
                    clipBounds = found ? IntersectOrEmpty(clipBounds, maskBounds) : maskBounds;
                    found = true;
                }
            }
            return found;
        }

        bool ProjectBoundsFromRect(RectTransform rect, Vector3 origin, Vector3 right, Vector3 up, out Rect bounds)
        {
            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return ProjectBounds(corners, origin, right, up, out bounds);
        }

        Rect IntersectOrEmpty(Rect left, Rect right)
        {
            Rect intersection;
            IntersectRects(left, right, out intersection);
            return intersection;
        }

        bool ProjectBounds(Vector3[] corners, Vector3 origin, Vector3 right, Vector3 up, out Rect bounds)
        {
            bounds = new Rect();
            if (corners == null || corners.Length == 0) return false;
            float minX = float.MaxValue;
            float maxX = float.MinValue;
            float minY = float.MaxValue;
            float maxY = float.MinValue;
            for (int i = 0; i < corners.Length; i++)
            {
                float x = Vector3.Dot(corners[i] - origin, right);
                float y = Vector3.Dot(corners[i] - origin, up);
                if (float.IsNaN(x) || float.IsNaN(y) || float.IsInfinity(x) || float.IsInfinity(y)) return false;
                minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x);
                minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y);
            }
            bounds = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return bounds.width > 0.00001f && bounds.height > 0.00001f;
        }

        bool RectContainsWithTolerance(Rect outer, Rect inner, float tolerance)
        {
            return inner.xMin >= outer.xMin - tolerance && inner.xMax <= outer.xMax + tolerance
                && inner.yMin >= outer.yMin - tolerance && inner.yMax <= outer.yMax + tolerance;
        }

        bool IntersectRects(Rect left, Rect right, out Rect intersection)
        {
            float minX = Mathf.Max(left.xMin, right.xMin);
            float maxX = Mathf.Min(left.xMax, right.xMax);
            float minY = Mathf.Max(left.yMin, right.yMin);
            float maxY = Mathf.Min(left.yMax, right.yMax);
            intersection = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return maxX > minX && maxY > minY;
        }

        string FormatRect(Rect value)
        {
            return "[" + value.xMin.ToString("0.###", CultureInfo.InvariantCulture)
                + "," + value.yMin.ToString("0.###", CultureInfo.InvariantCulture)
                + "," + value.width.ToString("0.###", CultureInfo.InvariantCulture)
                + "x" + value.height.ToString("0.###", CultureInfo.InvariantCulture) + "]";
        }

        IEnumerator ValidateNativeModalBlockerFlow()
        {
            Component modal = GetPanelSurface();
            if (modal == null)
            {
                AddCheck("panel.native-modal-blocker", false, "native modal field was unavailable");
                AddCheck("panel.native-modal-close-restores-ui", false, "native modal field was unavailable");
                yield break;
            }

            // First close the modal and prove that the candidate really is a
            // native clickable control. This avoids selecting a stale button
            // under a BSML modal or a hidden child raycaster.
            panel.Close();
            yield return StartCoroutine(WaitForNativeModalClosed(modal));
            string detail;
            NativeUiTarget target = FindNativeUiTarget(out detail);
            if (target == null || modal == null)
            {
                AddCheck("panel.native-modal-blocker", false,
                    "native modal blocker target unavailable: " + detail);
                AddCheck("panel.native-modal-close-restores-ui", false,
                    "cannot validate native UI restoration without a modal and underlying button");
                if (modal != null)
                {
                    panel.Open();
                    ReadyAttempt restored = new ReadyAttempt();
                    yield return StartCoroutine(WaitForPanelReady(restored));
                }
                yield break;
            }

            List<RaycastResult> baselineHits;
            bool baselineRaycast = TryRaycast(target, null, out baselineHits);
            bool baselineTarget = baselineRaycast && IsTargetTopHit(baselineHits, target);
            AddCheck("panel.native-target-baseline", baselineTarget,
                "target=" + target.Button.gameObject.name + "," + detail
                + "; hits=" + DescribeRaycastHits(baselineHits));

            panel.Open();
            ReadyAttempt openedForBlocker = new ReadyAttempt();
            yield return StartCoroutine(WaitForPanelReady(openedForBlocker));
            if (!openedForBlocker.Ready)
            {
                AddCheck("panel.native-modal-blocker", false, "native modal did not reopen for blocker validation");
                AddCheck("panel.native-modal-close-restores-ui", false, "native modal did not reopen after baseline raycast");
                yield break;
            }

            List<RaycastResult> blockedHits;
            bool blockedRaycast = TryRaycast(target, modal, out blockedHits);
            GameObject blockedTop = blockedRaycast && blockedHits.Count > 0 ? blockedHits[0].gameObject : null;
            bool hitModal = blockedTop != null && IsModalHit(blockedTop, modal);
            bool hitUnderlying = blockedTop != null && blockedTop != modal.gameObject
                && blockedTop.transform != modal.transform && !blockedTop.transform.IsChildOf(modal.transform)
                && blockedTop != null
                && (blockedTop == target.Button.gameObject
                    || blockedTop.transform == target.Rect.transform
                    || blockedTop.transform.IsChildOf(target.Rect.transform));
            bool blockerPassed = hitModal && !hitUnderlying;
            AddCheck("panel.native-modal-blocker", blockerPassed,
                "target=" + target.Button.gameObject.name + "," + detail
                + "; hits=" + DescribeRaycastHits(blockedHits)
                + "; modalHit=" + hitModal + "; underlyingHit=" + hitUnderlying);

            panel.Close();
            yield return StartCoroutine(WaitForNativeModalClosed(modal));
            List<RaycastResult> restoredHits;
            bool restoredRaycast = false;
            restoredHits = new List<RaycastResult>();
            float raycastDeadline = Time.realtimeSinceStartup + SecondsRemaining(3f);
            while (!Expired() && Time.realtimeSinceStartup < raycastDeadline)
            {
                restoredRaycast = TryRaycast(target, null, out restoredHits);
                if (restoredRaycast && IsTargetTopHit(restoredHits, target)) break;
                yield return null;
            }
            bool modalClosed = !modal.gameObject.activeInHierarchy && !ModalIsShown(modal);
            GameObject restoredTop = restoredRaycast && restoredHits.Count > 0 ? restoredHits[0].gameObject : null;
            bool underlyingRestored = target.Button.gameObject.activeInHierarchy && target.Button.interactable
                && IsTargetTopHit(restoredHits, target);
            AddCheck("panel.native-modal-close-restores-ui", underlyingRestored && modalClosed,
                "modalClosed=" + modalClosed + ",buttonInteractable=" + target.Button.interactable
                + "; hits=" + DescribeRaycastHits(restoredHits));

            panel.Open();
            ReadyAttempt reopened = new ReadyAttempt();
            yield return StartCoroutine(WaitForPanelReady(reopened));
            AddCheck("panel.native-modal-reopen", reopened.Ready,
                reopened.Ready ? "native modal reopened after blocker/close validation" : "native modal did not reopen after blocker validation");
        }

        IEnumerator WaitForNativeModalClosed(Component modal)
        {
            float closeDeadline = Time.realtimeSinceStartup + SecondsRemaining(4f);
            while (!Expired() && Time.realtimeSinceStartup < closeDeadline
                && modal != null && (modal.gameObject.activeInHierarchy || ModalIsShown(modal)))
                yield return null;
            // Let ModalView's blocker/canvas group and the native screen
            // raycaster finish their close-frame state before probing input.
            for (int frame = 0; frame < 3 && !Expired(); frame++)
            {
                Canvas.ForceUpdateCanvases();
                yield return null;
            }
        }

        NativeUiTarget FindNativeUiTarget(out string detail)
        {
            detail = String.Empty;
            RectTransform central = GetMemberValue(panel, "screenRect") as RectTransform;
            if (central == null)
            {
                GameObject root = GetPanelRootObject();
                if (root != null) central = root.GetComponentInParent<RectTransform>();
            }
            Component modal = GetPanelSurface();
            if (central == null || modal == null)
            {
                detail = "screenRect or modal field unavailable";
                return null;
            }
            RectTransform modalRect = modal.GetComponent<RectTransform>();
            if (modalRect == null) modalRect = modal.GetComponentInChildren<RectTransform>(true);
            if (modalRect == null)
            {
                detail = "modal RectTransform unavailable";
                return null;
            }
            Vector3[] centralCorners = new Vector3[4];
            Vector3[] modalCorners = new Vector3[4];
            central.GetWorldCorners(centralCorners);
            modalRect.GetWorldCorners(modalCorners);
            Vector3 origin = (centralCorners[0] + centralCorners[2]) * 0.5f;
            Rect modalBounds;
            if (!ProjectBounds(modalCorners, origin, central.right.normalized, central.up.normalized, out modalBounds))
            {
                detail = "modal projection unavailable";
                return null;
            }
            Button[] buttons = central.GetComponentsInChildren<Button>(true);
            Button toolbar = GetFieldValue(panel, "toolbarEntry") as Button;
            Button bottom = GetFieldValue(panel, "bottomEntry") as Button;
            string candidateDiagnostics = String.Empty;
            for (int i = 0; i < buttons.Length; i++)
            {
                Button button = buttons[i];
                if (button == null || !button.gameObject.activeInHierarchy || !button.interactable) continue;
                if (button == toolbar || button == bottom) continue;
                if (button.transform == modal.transform || button.transform.IsChildOf(modal.transform)) continue;
                RectTransform rect = button.GetComponent<RectTransform>();
                if (rect == null) continue;
                Vector3 point = rect.TransformPoint(rect.rect.center);
                Vector3 delta = point - origin;
                Rect pointBounds = new Rect(Vector3.Dot(delta, central.right.normalized),
                    Vector3.Dot(delta, central.up.normalized), 0f, 0f);
                if (modalBounds.Contains(pointBounds.center)) continue;
                Graphic graphicTarget = button.targetGraphic ?? button.GetComponent<Graphic>();
                if (graphicTarget == null || !graphicTarget.enabled || !graphicTarget.raycastTarget
                    || graphicTarget.canvasRenderer.cull || graphicTarget.canvasRenderer.GetColor().a <= 0.001f) continue;
                Canvas canvas = graphicTarget.GetComponentInParent<Canvas>();
                GraphicRaycaster graphic;
                Component vr;
                string raycasterPath;
                FindNearestCanvasRaycaster(canvas, out graphic, out vr, out raycasterPath);
                if (graphic == null && vr == null) continue;
                NativeUiTarget candidate = new NativeUiTarget {
                    Button = button,
                    Rect = rect,
                    Canvas = canvas,
                    Graphic = graphicTarget,
                    GraphicRaycaster = graphic,
                    VrRaycaster = vr,
                    RaycasterPath = raycasterPath,
                    GraphicDiagnostics = DescribeNativeGraphic(graphicTarget, canvas)
                };
                List<RaycastResult> candidateHits;
                bool candidateRaycast = TryRaycast(candidate, null, out candidateHits);
                if (candidateRaycast && IsTargetTopHit(candidateHits, candidate))
                {
                    detail = "buttonPath=" + GetTransformPath(button.transform)
                        + ",modalBounds=" + FormatRect(modalBounds)
                        + "," + candidate.GraphicDiagnostics
                        + ",raycasterPath=" + candidate.RaycasterPath
                        + ",graphicRegistry=" + DescribeGraphicRegistry(canvas)
                        + ",baselineHits=" + DescribeRaycastHits(candidateHits);
                    return candidate;
                }
                if (candidateDiagnostics.Length < 2048)
                {
                    if (candidateDiagnostics.Length > 0) candidateDiagnostics += ";";
                    candidateDiagnostics += button.gameObject.name + "{" + candidate.GraphicDiagnostics
                        + ",raycasterPath=" + candidate.RaycasterPath
                        + ",hits=" + DescribeRaycastHits(candidateHits) + "}";
                }
            }
            detail = "no active underlying Button with a native raycast hit outside modal bounds"
                + "; candidates=" + candidateDiagnostics;
            return null;
        }

        void FindNearestCanvasRaycaster(Canvas canvas, out GraphicRaycaster graphic,
            out Component vr, out string path)
        {
            graphic = null;
            vr = null;
            path = "none";
            if (canvas == null) return;
            Transform current = canvas.transform;
            while (current != null)
            {
                Component[] components = current.GetComponents<Component>();
                for (int i = 0; i < components.Length; i++)
                {
                    Component component = components[i];
                    if (component == null) continue;
                    Behaviour behaviour = component as Behaviour;
                    if (behaviour != null && !behaviour.isActiveAndEnabled) continue;
                    if (graphic == null) graphic = component as GraphicRaycaster;
                    if (vr == null && component.GetType().FullName == "VRUIControls.VRGraphicRaycaster") vr = component;
                }
                if (graphic != null || vr != null)
                {
                    path = GetTransformPath(current);
                    return;
                }
                current = current.parent;
            }
        }

        string DescribeNativeGraphic(Graphic graphic, Canvas canvas)
        {
            if (graphic == null) return "graphic=null";
            return "graphic=" + graphic.GetType().Name + "@" + GetTransformPath(graphic.transform)
                + ",enabled=" + graphic.enabled + ",raycastTarget=" + graphic.raycastTarget
                + ",cull=" + graphic.canvasRenderer.cull
                + ",alpha=" + graphic.canvasRenderer.GetColor().a.ToString("0.###", CultureInfo.InvariantCulture)
                + ",canvas=" + (canvas == null ? "null" : GetTransformPath(canvas.transform));
        }

        string DescribeGraphicRegistry(Canvas canvas)
        {
            if (canvas == null) return "unavailable";
            try
            {
                Type registry = typeof(Graphic).Assembly.GetType("UnityEngine.UI.GraphicRegistry", false);
                MethodInfo method = registry == null ? null : registry.GetMethod("GetGraphicsForCanvas",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new Type[] { typeof(Canvas) }, null);
                object value = method == null ? null : method.Invoke(null, new object[] { canvas });
                ICollection collection = value as ICollection;
                return collection == null ? "unavailable" : "count=" + collection.Count.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception error)
            {
                return "error=" + DescribeException(error);
            }
        }

        bool TryRaycast(NativeUiTarget target, Component raycastRoot, out List<RaycastResult> hits)
        {
            hits = new List<RaycastResult>();
            if (target == null || target.Rect == null || captureCamera == null) return false;
            EventSystem eventSystem = EventSystem.current;
            if (eventSystem == null)
            {
                EventSystem[] systems = Resources.FindObjectsOfTypeAll<EventSystem>();
                eventSystem = systems.FirstOrDefault(x => x != null && x.gameObject.activeInHierarchy)
                    ?? systems.FirstOrDefault(x => x != null);
            }
            if (eventSystem == null) return false;
            Canvas canvas = raycastRoot == null ? target.Canvas : raycastRoot.GetComponentInParent<Canvas>();
            GraphicRaycaster graphic = raycastRoot == null ? target.GraphicRaycaster : raycastRoot.GetComponentInChildren<GraphicRaycaster>(true);
            Component vr = raycastRoot == null ? target.VrRaycaster : raycastRoot.GetComponentsInChildren<Component>(true)
                .FirstOrDefault(x => x != null && x.GetType().FullName == "VRUIControls.VRGraphicRaycaster");
            if (graphic == null && vr == null && canvas != null)
            {
                graphic = canvas.GetComponent<GraphicRaycaster>();
                if (vr == null) vr = canvas.GetComponentsInChildren<Component>(true)
                    .FirstOrDefault(x => x != null && x.GetType().FullName == "VRUIControls.VRGraphicRaycaster");
            }
            Vector3 world = target.Rect.TransformPoint(target.Rect.rect.center);
            Vector3 origin = captureCamera.transform.position;
            Vector3 direction = (world - origin).normalized;
            Vector2 screen = RectTransformUtility.WorldToScreenPoint(captureCamera, world);
            PointerEventData pointer = new PointerEventData(eventSystem) {
                position = screen,
                worldPosition = origin
            };
            RaycastResult seed = new RaycastResult {
                origin = origin,
                worldPosition = origin,
                worldNormal = direction,
                screenPosition = screen
            };
            pointer.pointerCurrentRaycast = seed;
            try
            {
                if (raycastRoot == null)
                {
                    // After close, prefer the raycaster that owns the
                    // underlying native button. EventSystem.RaycastAll also
                    // returns physics GroundCollider hits, which can mask a
                    // valid UI result when it is used as the first probe.
                    if (vr != null)
                    {
                        MethodInfo direct = vr.GetType().GetMethod("Raycast", BindingFlags.Instance | BindingFlags.Public,
                            null, new Type[] { typeof(PointerEventData), typeof(List<RaycastResult>) }, null);
                        if (direct != null) direct.Invoke(vr, new object[] { pointer, hits });
                    }
                    else if (graphic != null) graphic.Raycast(pointer, hits);
                    if (hits.Count > 0) return true;
                }
                // ModalView's blocker is a sibling on the parent canvas, so
                // call the real EventSystem aggregation instead of selecting
                // only raycasters below the modal root.
                eventSystem.RaycastAll(pointer, hits);
                if (hits.Count == 0 && vr != null)
                {
                    MethodInfo method = vr.GetType().GetMethod("Raycast", BindingFlags.Instance | BindingFlags.Public,
                        null, new Type[] { typeof(PointerEventData), typeof(List<RaycastResult>) }, null);
                    if (method == null) return false;
                    method.Invoke(vr, new object[] { pointer, hits });
                }
                else if (hits.Count == 0 && graphic != null) graphic.Raycast(pointer, hits);
                return true;
            }
            catch (Exception error)
            {
                RecordException("panel.native-modal-raycast", error);
                return false;
            }
        }

        bool IsModalHit(GameObject hit, Component modal)
        {
            if (hit == null) return false;
            if (modal != null && (hit.transform == modal.transform || hit.transform.IsChildOf(modal.transform))) return true;
            string path = GetTransformPath(hit.transform).ToLowerInvariant();
            return path.IndexOf("blocker", StringComparison.Ordinal) >= 0
                || path.IndexOf("modal", StringComparison.Ordinal) >= 0;
        }

        bool ModalIsShown(Component modal)
        {
            if (modal == null) return false;
            object value = GetMemberValue(modal, "isShown");
            return value is bool && (bool)value;
        }

        string DescribeRaycastHits(List<RaycastResult> hits)
        {
            if (hits == null || hits.Count == 0) return "[]";
            StringBuilder result = new StringBuilder("[");
            int limit = Math.Min(8, hits.Count);
            for (int i = 0; i < limit; i++)
            {
                if (i > 0) result.Append(";");
                result.Append(hits[i].gameObject == null ? "null" : GetTransformPath(hits[i].gameObject.transform));
            }
            if (hits.Count > limit) result.Append(";...");
            return result.Append("]").ToString();
        }

        bool IsTargetTopHit(List<RaycastResult> hits, NativeUiTarget target)
        {
            if (hits == null || hits.Count == 0 || target == null || target.Rect == null) return false;
            GameObject hit = hits[0].gameObject;
            return hit != null && (hit == target.Button.gameObject
                || hit.transform == target.Rect.transform
                || hit.transform.IsChildOf(target.Rect.transform));
        }

        bool ValidateRaycastScenarios(out string detail)
        {
            detail = String.Empty;
            try
            {
                Component floating = GetPanelSurface();
                Canvas canvas = floating == null ? null : floating.GetComponentsInChildren<Canvas>(true).FirstOrDefault();
                GraphicRaycaster raycaster = canvas == null ? null : canvas.GetComponent<GraphicRaycaster>();
                Component vrRaycaster = floating == null ? null : floating.GetComponentsInChildren<Component>(true)
                    .FirstOrDefault(x => x != null && x.GetType().FullName == "VRUIControls.VRGraphicRaycaster");
                EventSystem eventSystem = EventSystem.current;
                if (eventSystem == null)
                {
                    EventSystem[] systems = Resources.FindObjectsOfTypeAll<EventSystem>();
                    eventSystem = systems.FirstOrDefault(x => x != null && x.gameObject.activeInHierarchy)
                        ?? systems.FirstOrDefault(x => x != null);
                }
                if ((raycaster == null && vrRaycaster == null) || eventSystem == null)
                {
                    detail = "actual raycast validation unavailable: GraphicRaycaster/VRGraphicRaycaster or EventSystem was not found; physical VR hardware was not exercised";
                    return false;
                }
                RectTransform floatingRect = floating == null ? null : floating.GetComponent<RectTransform>();
                if (floatingRect == null) floatingRect = floating == null ? null : floating.GetComponentInChildren<RectTransform>(true);
                if (captureCamera == null || floatingRect == null)
                {
                    detail = "actual raycast validation unavailable: capture camera or modal rect was not found; physical VR hardware was not exercised";
                    return false;
                }
                string[] names = { "seated", "standing", "angle" };
                float[] heights = { -0.12f, 0.12f, 0f };
                float[] angles = { 0f, 0f, 10f };
                bool allHit = true;
                StringBuilder outcomes = new StringBuilder();
                for (int i = 0; i < names.Length; i++)
                {
                    // Keep the real menu camera untouched.  The three cases
                    // vary only the virtual ray origin/direction supplied to
                    // the game's actual raycaster.
                    Vector3 origin = captureCamera.transform.position + Vector3.up * heights[i];
                    Vector3 direction = (floatingRect.position - origin).normalized;
                    if (Mathf.Abs(angles[i]) > 0.001f)
                        direction = Quaternion.AngleAxis(angles[i], Vector3.up) * direction;
                    Vector2 screenPoint = RectTransformUtility.WorldToScreenPoint(captureCamera, floatingRect.position);
                    PointerEventData pointer = new PointerEventData(eventSystem) { position = screenPoint, worldPosition = origin };
                    RaycastResult seed = new RaycastResult();
                    seed.origin = origin;
                    seed.worldPosition = origin;
                    seed.worldNormal = direction;
                    seed.screenPosition = screenPoint;
                    pointer.pointerCurrentRaycast = seed;
                    List<RaycastResult> hits = new List<RaycastResult>();
                    if (vrRaycaster != null)
                    {
                        MethodInfo method = vrRaycaster.GetType().GetMethod("Raycast", BindingFlags.Instance | BindingFlags.Public,
                            null, new Type[] { typeof(PointerEventData), typeof(List<RaycastResult>) }, null);
                        if (method != null) method.Invoke(vrRaycaster, new object[] { pointer, hits });
                    }
                    else if (raycaster != null) raycaster.Raycast(pointer, hits);
                    bool hitPanel = hits.Any(x => x.gameObject != null
                        && (x.gameObject.transform == floating.transform || x.gameObject.transform.IsChildOf(floating.transform)));
                    allHit = allHit && hitPanel;
                    if (outcomes.Length > 0) outcomes.Append(",");
                    outcomes.Append(names[i]).Append("=").Append(hitPanel)
                        .Append("@origin").Append(FormatVector(origin))
                        .Append("/direction").Append(FormatVector(direction));
                }
                detail = "physicalHMD=not_exercised; actual "
                    + (vrRaycaster == null ? "GraphicRaycaster" : "VRGraphicRaycaster")
                    + " invoked for seated/standing/angle rays: " + outcomes;
                return allHit;
            }
            catch (Exception error)
            {
                detail = "raycast simulation exception: " + DescribeException(error);
                RecordException("panel.raycast-scenarios", error);
                return false;
            }
        }

        bool ExerciseNumericPage()
        {
            try
            {
                object before = GetPanelDraft();
                bool opened = OpenNumericForMetricSmoke("Nps", true);
                GameObject numericPage = FindNumericSurface();
                bool visible = opened && numericPage != null && numericPage.activeInHierarchy;
                bool rangeSession = GetMemberValue(panel, "rangeSession") != null;
                ReturnToMainPage();
                GameObject afterPage = FindNumericSurface();
                bool returned = afterPage == null || !afterPage.activeInHierarchy;
                return visible && returned && (rangeSession || before is FilterSettings);
            }
            catch (Exception error)
            {
                RecordException("panel.numeric-navigation", error);
                return false;
            }
        }

        bool OpenNumericPageForCapture()
        {
            try
            {
                return OpenNumericForMetricSmoke("Nps", true)
                    && FindNumericSurface() != null && FindNumericSurface().activeInHierarchy;
            }
            catch (Exception error)
            {
                RecordException("panel.numeric-capture", error);
                return false;
            }
        }

        void ReturnToMainPage()
        {
            try
            {
                if (GetMemberValue(panel, "rangeSession") != null)
                    InvokePanelMethod("CancelRange", new object[0]);
                else
                    InvokePanelMethod("CloseManagePresets", new object[0]);
            }
            catch (Exception error)
            {
                RecordException("panel.numeric-capture-back", error);
            }
        }

        bool OpenNumericForMetricSmoke(string metricName, bool minimum)
        {
            string direct = "Open" + metricName + (minimum ? "Min" : "Max");
            return InvokePanelMethod(direct, new object[0]);
        }

        GameObject FindNumericSurface()
        {
            object value = GetMemberValue(panel, "boundRangePage");
            GameObject page = value as GameObject;
            Component component = value as Component;
            if (page == null && component != null) page = component.gameObject;
            return page;
        }

        bool OpenMorePageForCapture()
        {
            return ClickScopeCellNative(1);
        }

        bool OpenPresetsPageForCapture()
        {
            return InvokePanelMethod("OpenManagePresets", new object[0]);
        }

        IEnumerator WaitForPanelReady(ReadyAttempt result)
        {
            result.Ready = false;
            float deadline = Time.realtimeSinceStartup + SecondsRemaining(20f);
            while (!Expired() && Time.realtimeSinceStartup < deadline)
            {
                object interactiveValue = GetFieldValue(panel, "interactive");
                bool interactive = interactiveValue is bool && (bool)interactiveValue;
                if (panel != null && panel.IsOpen && interactive && runtime != null
                    && !runtime.CentralTransitioning && !runtime.IsBusy)
                {
                    yield return null;
                    yield return null;
                    result.Ready = true;
                    yield break;
                }
                yield return null;
            }
        }

        IEnumerator WaitForStablePanelLayout(float seconds)
        {
            float deadline = Time.realtimeSinceStartup + SecondsRemaining(seconds);
            Vector3 previousCenter = Vector3.zero;
            Vector2 previousSize = Vector2.zero;
            bool havePrevious = false;
            int stableFrames = 0;
            while (!Expired() && Time.realtimeSinceStartup < deadline)
            {
                Canvas.ForceUpdateCanvases();
                Component floating = panel == null ? null : GetPanelSurface();
                RectTransform rect = floating == null ? null : floating.GetComponent<RectTransform>();
                if (rect == null && floating != null) rect = floating.GetComponentInChildren<RectTransform>(true);
                if (rect != null && rect.gameObject.activeInHierarchy)
                {
                    Vector3[] corners = new Vector3[4];
                    rect.GetWorldCorners(corners);
                    Vector3 center = (corners[0] + corners[2]) * 0.5f;
                    Vector2 size = new Vector2(ProjectedSpan(corners, rect.right.normalized),
                        ProjectedSpan(corners, rect.up.normalized));
                    bool same = havePrevious && Vector3.Distance(center, previousCenter) <= 0.0005f
                        && Vector2.Distance(size, previousSize) <= 0.0005f;
                    stableFrames = same ? stableFrames + 1 : 0;
                    havePrevious = true;
                    previousCenter = center;
                    previousSize = size;
                    if (stableFrames >= 3) yield break;
                }
                yield return null;
            }
        }

        IEnumerator ValidateSelectionAndCapture()
        {
            table = FindActiveTable();
            navigation = GetNavigation();
            bool listValid = table != null && navigation != null && GetAdapterLevels("Input").Count > 0;
            AddCheck("song-list.valid", listValid, listValid ? "active LevelCollectionTableView and original input levels are available" : "active song list or original level collection is missing");
            if (!listValid)
            {
                screenshotPassed = false;
                yield break;
            }

            NativeSelection selectionBeforeSmoke = GetNativeSelectionSignature(table, navigation);
            smokeOriginalSelection = selectionBeforeSmoke;
            if (!HasSelectedBeatmap(table, navigation))
            {
                BeatmapLevel first = GetAdapterLevels("Output").FirstOrDefault() ?? GetAdapterLevels("Input").FirstOrDefault();
                if (first != null) SelectLevel(table, navigation, first);
                for (int i = 0; i < 5; i++) yield return null;
            }
            bool detail = HasSelectedBeatmap(table, navigation) && HasActiveDetail();
            AddCheck("song-detail.selected", detail, detail ? "selected BeatmapLevel and active detail controller are valid" : "selected list detail is missing");

            yield return StartCoroutine(ValidateLiveInteractions());

            yield return StartCoroutine(WaitForStablePanelLayout(3f));
            string stableFormGeometryDetail;
            bool stableFormGeometry = ValidateInteractiveGeometry(out stableFormGeometryDetail);
            AddCheck("panel.form-keyboard-geometry", stableFormGeometry, stableFormGeometryDetail);

            captureCameraPrepared = PrepareCaptureCamera();
            AddCheck("capture.camera", captureCameraPrepared,
                captureCameraPrepared ? "FirstPersonFlyingController.Inject preserved the real menu camera pose" : "could not prepare a live menu camera through FirstPersonFlyingController public API");
            yield return StartCoroutine(ValidateNativeModalBlockerFlow());

            string raycastDetail;
            bool raycastGeometry = ValidateRaycastScenarios(out raycastDetail);
            AddCheck("panel.raycast-scenarios", raycastGeometry, raycastDetail);
            bool visualUi = BuildVisualDiagnostics(out visualDiagnostics);
            AddCheck("capture.ui-viewport", visualUi, visualDiagnostics);

            string baselinePath = Path.Combine(runtimeDirectory, "scene-baseline.png");
            panel.Close();
            for (int i = 0; i < 2; i++) yield return null;
            CaptureAttempt baselineCapture = new CaptureAttempt();
            yield return StartCoroutine(CaptureAndWait(baselinePath, baselineCapture));
            bool baselineShot = baselineCapture.Completed;
            AddCheck("screenshot.scene-baseline", baselineShot,
                baselineShot ? baselinePath + " (URP visual pixels passed); " + baselineCapture.Detail : baselineCapture.Detail ?? "scene baseline URP capture failed");

            panel.Open();
            ReadyAttempt reopened = new ReadyAttempt();
            yield return StartCoroutine(WaitForPanelReady(reopened));
            bool reopenedPanel = reopened.Ready;
            bool reopenedPose = reopenedPanel && ApplyCapturePose(captureCamera);
            AddCheck("panel.reopen", reopenedPanel, reopenedPanel ? "panel reopened on the same capture camera" : "panel did not reopen after baseline capture");
            AddCheck("capture.camera.reopen-pose", reopenedPose,
                reopenedPose ? "saved capture camera world pose and projection were restored after panel reopen" : "saved capture camera pose could not be restored after panel reopen");
            if (reopenedPose) visualUi = BuildVisualDiagnostics(out visualDiagnostics);
            else visualUi = false;
            AddCheck("capture.ui-viewport.reopened", visualUi, visualDiagnostics);

            string selectedPath = Path.Combine(runtimeDirectory, "selected-panel.png");
            CaptureAttempt selectedCapture = new CaptureAttempt();
            yield return StartCoroutine(CaptureAndWait(selectedPath, selectedCapture));
            bool selectedShot = selectedCapture.Completed;
            AddCheck("screenshot.selected", selectedShot,
                selectedShot ? selectedPath + " (visual pixels passed); " + selectedCapture.Detail : selectedCapture.Detail ?? "selected-panel.png was missing or visually black");

            string panelDiffDetail = String.Empty;
            bool panelDiff = baselineShot && selectedShot && CompareScreenshotRegion(baselinePath, selectedPath, out panelDiffDetail);

            string landmarkDetail = String.Empty;
            bool uiLandmarks = baselineShot && selectedShot
                && ValidateScreenshotLandmarks(baselinePath, selectedPath, out landmarkDetail);
            AddCheck("screenshot.ui-landmarks", uiLandmarks, landmarkDetail);

            AddCheck("screenshot.panel-diff", panelDiff,
                panelDiffDetail);

            bool moreOpened = reopenedPanel && OpenMorePageForCapture();
            AddCheck("screenshot.more-page.ready", moreOpened,
                moreOpened ? "More controls are active for URP visual capture" : "More controls could not be opened for visual capture");
            bool moreShot = false;
            if (moreOpened)
            {
                for (int i = 0; i < 2; i++) yield return null;
                yield return StartCoroutine(WaitForStablePanelLayout(2f));
                string morePath = Path.Combine(runtimeDirectory, "more-panel.png");
                CaptureAttempt moreCapture = new CaptureAttempt();
                yield return StartCoroutine(CaptureAndWait(morePath, moreCapture));
                moreShot = moreCapture.Completed;
                AddCheck("screenshot.more-page", moreShot,
                    moreShot ? morePath + " (URP visual pixels passed); " + moreCapture.Detail : moreCapture.Detail ?? "more-panel.png URP capture failed");
                ReturnToMainPage();
                ClickScopeCellNative(0);
                for (int i = 0; i < 2; i++) yield return null;
            }
            else AddCheck("screenshot.more-page", false, "More page was unavailable");

            bool numericOpened = reopenedPanel && OpenNumericPageForCapture();
            AddCheck("screenshot.numeric-page.ready", numericOpened, numericOpened ? "numeric page is active for URP visual capture" : "numeric page could not be opened for visual capture");
            bool numericShot = false;
            bool numericDiff = false;
            bool numericKeypadViewport = false;
            string numericDiffDetail = String.Empty;
            if (numericOpened)
            {
                for (int i = 0; i < 2; i++) yield return null;
                yield return StartCoroutine(WaitForStablePanelLayout(3f));
                string numericKeypadDetail;
                numericKeypadViewport = ValidateNumericKeypadViewport(out numericKeypadDetail);
                AddCheck("panel.numeric-keypad-viewport", numericKeypadViewport, numericKeypadDetail);
                string numericGeometryDetail;
                bool numericGeometry = ValidateInteractiveGeometry(out numericGeometryDetail);
                AddCheck("panel.numeric-form-geometry", numericGeometry, numericGeometryDetail);
                string numericPath = Path.Combine(runtimeDirectory, "numeric-panel.png");
                CaptureAttempt numericCapture = new CaptureAttempt();
                yield return StartCoroutine(CaptureAndWait(numericPath, numericCapture));
                numericShot = numericCapture.Completed;
                AddCheck("screenshot.numeric-page", numericShot,
                    numericShot ? numericPath + " (URP visual pixels passed); " + numericCapture.Detail : numericCapture.Detail ?? "numeric-panel.png URP capture failed");
                numericDiff = selectedShot && numericShot
                    && CompareScreenshotRegion(selectedPath, numericPath, out numericDiffDetail);
                AddCheck("screenshot.numeric-diff", numericDiff, numericDiffDetail);
                ReturnToMainPage();
                for (int i = 0; i < 2; i++) yield return null;
            }
            else
            {
                AddCheck("panel.numeric-keypad-viewport", false, "numeric page was not available for raw keypad viewport validation");
                AddCheck("screenshot.numeric-diff", false, "numeric screenshot was not available for same-view comparison");
            }

            bool presetsOpened = reopenedPanel && OpenPresetsPageForCapture();
            AddCheck("screenshot.presets-page.ready", presetsOpened,
                presetsOpened ? "preset controls are active for URP visual capture" : "preset controls could not be opened for visual capture");
            bool presetsShot = false;
            if (presetsOpened)
            {
                for (int i = 0; i < 2; i++) yield return null;
                yield return StartCoroutine(WaitForStablePanelLayout(2f));
                string presetsPath = Path.Combine(runtimeDirectory, "presets-panel.png");
                CaptureAttempt presetsCapture = new CaptureAttempt();
                yield return StartCoroutine(CaptureAndWait(presetsPath, presetsCapture));
                presetsShot = presetsCapture.Completed;
                AddCheck("screenshot.presets-page", presetsShot,
                    presetsShot ? presetsPath + " (URP visual pixels passed); " + presetsCapture.Detail : presetsCapture.Detail ?? "presets-panel.png URP capture failed");
                ReturnToMainPage();
                for (int i = 0; i < 2; i++) yield return null;
            }
            else AddCheck("screenshot.presets-page", false, "preset page was unavailable");

            bool cleared = ClearSelection(table, navigation);
            for (int i = 0; i < 5; i++) yield return null;
            AddCheck("song-list.clear-selection", cleared, cleared ? "native table/navigation selection was cleared" : "could not clear selection through the native list APIs");
            string unselectedPath = Path.Combine(runtimeDirectory, "unselected-panel.png");
            CaptureAttempt unselectedCapture = new CaptureAttempt();
            yield return StartCoroutine(CaptureAndWait(unselectedPath, unselectedCapture));
            bool unselectedShot = unselectedCapture.Completed;
            AddCheck("screenshot.unselected", unselectedShot,
                unselectedShot ? unselectedPath + " (visual pixels passed); " + unselectedCapture.Detail : unselectedCapture.Detail ?? "unselected-panel.png was missing or visually black");
            if (panel != null && panel.IsOpen)
            {
                panel.Close();
                yield return StartCoroutine(WaitForPanelClosed(4f));
            }
            SelectionRestoreAttempt finalSelectionAttempt = new SelectionRestoreAttempt();
            yield return StartCoroutine(RestoreNativeSelectionAndWait(selectionBeforeSmoke, 15f, finalSelectionAttempt));
            bool finalSelectionRestored = finalSelectionAttempt.Restored;
            AddCheck("song-list.restore-original-selection", finalSelectionRestored,
                "original selection restored after selected/unselected screenshots; invoked="
                + finalSelectionAttempt.Invoked + ",awaitedDetail=" + finalSelectionRestored);
            screenshotPassed = captureCameraPrepared && visualUi && baselineShot && reopenedPanel
                && reopenedPose && selectedShot && panelDiff && uiLandmarks && numericShot
                && numericKeypadViewport
                && numericDiff && moreShot && presetsShot && unselectedShot && cleared
                && finalSelectionRestored;

            // Keep the expensive gameplay transition after all panel and
            // screenshot assertions.  A failed native pause/menu round trip
            // must not erase evidence that the VR panel itself rendered and
            // interacted correctly.
            if (!Expired()) yield return StartCoroutine(ValidatePracticeRoundTrip());
        }

        LevelCollectionTableView FindActiveTable()
        {
            if (runtime != null)
            {
                LevelCollectionTableView bound = GetFieldValue(runtime, "table") as LevelCollectionTableView;
                if (bound != null) return bound;
            }
            LevelCollectionTableView[] values = Resources.FindObjectsOfTypeAll<LevelCollectionTableView>();
            for (int i = 0; i < values.Length; i++)
                if (values[i] != null && values[i].gameObject.activeInHierarchy) return values[i];
            return values.FirstOrDefault(x => x != null);
        }

        LevelCollectionNavigationController GetNavigation()
        {
            if (runtime != null)
            {
                object value = GetFieldValue(runtime, "navigation");
                LevelCollectionNavigationController typed = value as LevelCollectionNavigationController;
                if (typed != null) return typed;
            }
            LevelCollectionNavigationController[] values = Resources.FindObjectsOfTypeAll<LevelCollectionNavigationController>();
            return values.FirstOrDefault(x => x != null && x.gameObject.activeInHierarchy) ?? values.FirstOrDefault(x => x != null);
        }

        List<BeatmapLevel> GetAdapterLevels(string propertyName)
        {
            List<BeatmapLevel> result = new List<BeatmapLevel>();
            Type adapter = typeof(AdvancedSongFilters.Runtime).Assembly.GetType("AdvancedSongFilters.Adapter", false);
            if (adapter == null) return result;
            PropertyInfo property = adapter.GetProperty(propertyName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            object value = property == null ? null : property.GetValue(null, null);
            IEnumerable enumerable = value as IEnumerable;
            if (enumerable == null) return result;
            foreach (object item in enumerable)
            {
                BeatmapLevel level = item as BeatmapLevel;
                if (level != null) result.Add(level);
            }
            return result;
        }

        List<string> GetAdapterIds(string propertyName)
        {
            return GetAdapterLevels(propertyName).Where(x => x != null && !String.IsNullOrEmpty(x.levelID))
                .Select(x => x.levelID).ToList();
        }

        UserDataSnapshot CaptureUserDataSnapshot()
        {
            UserDataSnapshot snapshot = new UserDataSnapshot
            {
                SettingsPath = Path.Combine(gameRoot, "UserData", "AdvancedSongFilters.json"),
                CachePath = Path.Combine(gameRoot, "UserData", "AdvancedSongFilters.notes.json"),
                PresetsPath = Path.Combine(gameRoot, "UserData", "AdvancedSongFilters.presets.json")
            };
            try
            {
                snapshot.SettingsExists = File.Exists(snapshot.SettingsPath);
                snapshot.CacheExists = File.Exists(snapshot.CachePath);
                snapshot.PresetsExists = File.Exists(snapshot.PresetsPath);
                if (snapshot.SettingsExists) snapshot.SettingsBytes = File.ReadAllBytes(snapshot.SettingsPath);
                if (snapshot.CacheExists) snapshot.CacheBytes = File.ReadAllBytes(snapshot.CachePath);
                if (snapshot.PresetsExists) snapshot.PresetsBytes = File.ReadAllBytes(snapshot.PresetsPath);
            }
            catch (Exception error)
            {
                RecordException("interaction.snapshot-userdata", error);
            }
            return snapshot;
        }

        bool RestoreUserDataSnapshot(UserDataSnapshot snapshot)
        {
            if (snapshot == null) return false;
            try
            {
                RestoreFile(snapshot.SettingsPath, snapshot.SettingsExists, snapshot.SettingsBytes);
                RestoreFile(snapshot.CachePath, snapshot.CacheExists, snapshot.CacheBytes);
                RestoreFile(snapshot.PresetsPath, snapshot.PresetsExists, snapshot.PresetsBytes);
                ReloadPresetsAfterRestore();
                return FilesEqual(snapshot.SettingsPath, snapshot.SettingsExists, snapshot.SettingsBytes)
                    && FilesEqual(snapshot.CachePath, snapshot.CacheExists, snapshot.CacheBytes)
                    && FilesEqual(snapshot.PresetsPath, snapshot.PresetsExists, snapshot.PresetsBytes);
            }
            catch (Exception error)
            {
                RecordException("interaction.restore-userdata", error);
                return false;
            }
        }

        void RestoreFile(string path, bool existed, byte[] bytes)
        {
            if (existed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, bytes ?? new byte[0]);
            }
            else if (File.Exists(path)) File.Delete(path);
        }

        bool FilesEqual(string path, bool existed, byte[] expected)
        {
            if (!existed) return !File.Exists(path);
            if (!File.Exists(path)) return false;
            byte[] actual = File.ReadAllBytes(path);
            if (expected == null || actual.Length != expected.Length) return false;
            for (int i = 0; i < actual.Length; i++) if (actual[i] != expected[i]) return false;
            return true;
        }

        NotesCache CapturePluginCache()
        {
            try
            {
                Type plugin = typeof(AdvancedSongFilters.Runtime).Assembly.GetType("AdvancedSongFilters.Plugin", false);
                FieldInfo field = plugin == null ? null : GetFieldIncludingBases(plugin, "Cache");
                NotesCache cache = field == null ? null : field.GetValue(null) as NotesCache;
                return CloneCache(cache);
            }
            catch (Exception error)
            {
                RecordException("interaction.snapshot-cache", error);
                return null;
            }
        }

        NotesCache CloneCache(NotesCache source)
        {
            if (source == null) return null;
            NotesCache result = new NotesCache
            {
                SchemaVersion = source.SchemaVersion,
                GameBuild = source.GameBuild,
                ParserVersion = source.ParserVersion,
                Counts = new Dictionary<string, int>(StringComparer.Ordinal)
            };
            if (source.Counts != null)
                foreach (KeyValuePair<string, int> entry in source.Counts) result.Counts[entry.Key] = entry.Value;
            return result;
        }

        void RestorePluginCache(NotesCache cache)
        {
            if (cache == null) return;
            try
            {
                Type plugin = typeof(AdvancedSongFilters.Runtime).Assembly.GetType("AdvancedSongFilters.Plugin", false);
                FieldInfo field = plugin == null ? null : GetFieldIncludingBases(plugin, "Cache");
                if (field != null) field.SetValue(null, CloneCache(cache));
            }
            catch (Exception error)
            {
                RecordException("interaction.restore-cache", error);
            }
        }

        void ReloadPresetsAfterRestore()
        {
            try
            {
                if (runtime != null)
                {
                    MethodInfo method = runtime.GetType().GetMethod("ReloadPresets",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (method != null && method.GetParameters().Length == 0)
                    {
                        method.Invoke(runtime, null);
                        return;
                    }
                }
            }
            catch (Exception error)
            {
                RecordException("interaction.restore-presets", error);
            }
        }

        void RestoreRuntimeFields(FilterSettings original)
        {
            if (runtime == null || original == null) return;
            FieldInfo applied = GetFieldIncludingBases(runtime.GetType(), "applied");
            if (applied != null) applied.SetValue(runtime, original.Clone());
            Type plugin = typeof(AdvancedSongFilters.Runtime).Assembly.GetType("AdvancedSongFilters.Plugin", false);
            FieldInfo settings = plugin == null ? null : GetFieldIncludingBases(plugin, "Settings");
            if (settings != null) settings.SetValue(null, original.Clone());
            MethodInfo refresh = runtime.GetType().GetMethod("RefreshPreservingSelection",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (refresh != null) refresh.Invoke(runtime, null);
        }

        bool RestoreNativeSelection(NativeSelection original)
        {
            if (original == null || String.IsNullOrEmpty(original.LevelId))
            {
                ClearSelection(table, navigation);
                return true;
            }
            BeatmapLevel level = GetAdapterLevels("Output").FirstOrDefault(x => x != null && x.levelID == original.LevelId)
                ?? GetAdapterLevels("Input").FirstOrDefault(x => x != null && x.levelID == original.LevelId);
            if (level == null) return false;
            SelectLevel(table, navigation, level);
            return true;
        }

        IEnumerator RestoreNativeSelectionAndWait(NativeSelection original, float seconds, SelectionRestoreAttempt result)
        {
            if (result == null) yield break;
            result.Invoked = RestoreNativeSelection(original);
            result.Restored = false;
            float deadline = Time.realtimeSinceStartup + SecondsRemaining(seconds);
            while (!Expired() && Time.realtimeSinceStartup < deadline)
            {
                NativeSelection current = GetNativeSelectionSignature(table, navigation);
                bool detailReady = original == null || String.IsNullOrEmpty(original.LevelId)
                    ? !HasSelectedBeatmap(table, navigation)
                    : HasSelectedBeatmap(table, navigation) && FindActiveDetailController() != null;
                bool equivalent = original == null || String.IsNullOrEmpty(original.LevelId)
                    ? current == null || String.IsNullOrEmpty(current.LevelId)
                    : NativeSelectionEquivalent(original, current, false);
                if (result.Invoked && detailReady && equivalent)
                {
                    result.Restored = true;
                    yield break;
                }
                yield return null;
            }
        }

        bool SettingsEquivalent(FilterSettings left, FilterSettings right)
        {
            if (left == null || right == null) return left == right;
            return left.SchemaVersion == right.SchemaVersion && left.Enabled == right.Enabled
                && left.ExcludeOfficial == right.ExcludeOfficial
                && String.Equals(left.Mode, right.Mode, StringComparison.Ordinal)
                && IntArrayEqual(left.Difficulties, right.Difficulties)
                && RangeEqual(left.Nps, right.Nps) && RangeEqual(left.Njs, right.Njs)
                && RangeEqual(left.Duration, right.Duration) && RangeEqual(left.Bpm, right.Bpm)
                && RangeEqual(left.Notes, right.Notes);
        }

        bool IntArrayEqual(int[] left, int[] right)
        {
            if (left == null || right == null) return left == right;
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        bool RangeEqual(NumericRange left, NumericRange right)
        {
            if (left == null || right == null) return left == right;
            return left.Min == right.Min && left.Max == right.Max;
        }

        void SelectLevel(LevelCollectionTableView value, LevelCollectionNavigationController controller, BeatmapLevel level)
        {
            try
            {
                // Navigation also restores the detail visibility after an
                // empty collection; selecting the table alone only changes rows.
                if (controller != null)
                {
                    controller.SelectLevel(level);
                    return;
                }
                MethodInfo method = FindMethod(value.GetType(), "SelectLevel", new[] { typeof(BeatmapLevel) });
                if (method != null)
                {
                    method.Invoke(value, new object[] { level });
                    return;
                }
                method = FindMethod(controller.GetType(), "SelectLevel", new[] { typeof(BeatmapLevel) });
                if (method != null) method.Invoke(controller, new object[] { level });
            }
            catch (Exception error)
            {
                RecordException("song-list.select", error);
            }
        }

        bool ClearSelection(LevelCollectionTableView value, LevelCollectionNavigationController controller)
        {
            bool cleared = false;
            try
            {
                MethodInfo method = FindMethod(value.GetType(), "ClearSelection", Type.EmptyTypes);
                if (method != null)
                {
                    method.Invoke(value, null);
                    cleared = true;
                }
                method = FindMethod(controller.GetType(), "ClearSelected", Type.EmptyTypes);
                if (method != null)
                {
                    method.Invoke(controller, null);
                    cleared = true;
                }
            }
            catch (Exception error)
            {
                RecordException("song-list.clear-selection", error);
                return false;
            }
            return cleared;
        }

        bool HasSelectedBeatmap(LevelCollectionTableView value, LevelCollectionNavigationController controller)
        {
            return FindBeatmapReference(value) != null || FindBeatmapReference(controller) != null;
        }

        bool HasActiveDetail()
        {
            StandardLevelDetailViewController[] details = Resources.FindObjectsOfTypeAll<StandardLevelDetailViewController>();
            for (int i = 0; i < details.Length; i++)
            {
                if (details[i] != null && details[i].gameObject.activeInHierarchy && FindBeatmapReference(details[i]) != null)
                    return true;
            }
            return false;
        }

        NativeSelection GetNativeSelectionSignature(LevelCollectionTableView value, LevelCollectionNavigationController controller)
        {
            NativeSelection result = new NativeSelection();
            BeatmapLevel level = FindBeatmapReference(value) ?? FindBeatmapReference(controller);
            StandardLevelDetailViewController detail = FindActiveDetailController();
            if (level == null && detail != null) level = FindBeatmapReference(detail);
            result.LevelId = level == null ? null : level.levelID;
            result.DetailActive = detail != null && FindBeatmapReference(detail) != null;
            result.StartActive = result.DetailActive && HasActiveStartControl(detail);
            result.Difficulty = FindDifficultyToken(detail) ?? FindDifficultyToken(controller);
            result.DifficultyAvailable = !String.IsNullOrEmpty(result.Difficulty);
            return result;
        }

        StandardLevelDetailViewController FindActiveDetailController()
        {
            StandardLevelDetailViewController[] details = Resources.FindObjectsOfTypeAll<StandardLevelDetailViewController>();
            for (int i = 0; i < details.Length; i++)
                if (details[i] != null && details[i].gameObject.activeInHierarchy
                    && FindBeatmapReference(details[i]) != null) return details[i];
            return null;
        }

        bool HasActiveStartControl(StandardLevelDetailViewController detail)
        {
            if (detail == null) return false;
            Button[] buttons = detail.GetComponentsInChildren<Button>(true);
            for (int i = 0; i < buttons.Length; i++)
            {
                Button button = buttons[i];
                if (button == null || !button.gameObject.activeInHierarchy) continue;
                string name = GetTransformPath(button.transform).ToLowerInvariant();
                if (name.IndexOf("practice", StringComparison.Ordinal) >= 0
                    || name.IndexOf("action", StringComparison.Ordinal) >= 0
                    || name.IndexOf("start", StringComparison.Ordinal) >= 0
                    || name.IndexOf("play", StringComparison.Ordinal) >= 0)
                    return button.interactable;
            }
            return false;
        }

        IEnumerator ValidatePracticeRoundTrip()
        {
            practicePassed = false;
            AddCheck("game.physical-hmd-limitation", true,
                "physical HMD/controller input was not exercised; native onClick and pause-menu actions were driven in the game process");

            NativeSelection original = GetNativeSelectionSignature(table, navigation);
            if (FindActiveDetailController() == null)
            {
                AddCheck("game.normal-start-gameplay", false, "active selected-song detail was unavailable");
                AddCheck("game.normal-pause-return", false, "normal gameplay could not be attempted");
                AddCheck("game.practice-start-gameplay", false, "active selected-song detail was unavailable");
                AddCheck("game.practice-pause-return", false, "practice gameplay could not be attempted");
                yield break;
            }

            panel.Close();
            yield return StartCoroutine(WaitForPanelClosed(4f));

            GameplayAttempt normal = new GameplayAttempt();
            Button normalAction = FindDetailStartAction(FindActiveDetailController(), false);
            if (normalAction == null)
            {
                AddCheck("game.normal-start-gameplay", false,
                    "native Start/Play action was not present on the selected-song detail");
                AddCheck("game.normal-pause-return", false,
                    "normal gameplay action was unavailable; physical HMD limitation applies");
            }
            else
            {
                yield return StartCoroutine(RunGameplayAttempt(normalAction, false, original, normal));
                AddCheck("game.normal-start-gameplay", normal.Invoked && normal.EnteredGameplay,
                    normal.EntryDetail ?? "normal start did not reach an active GameCore/GameplayCore");
                AddCheck("game.normal-pause-return", normal.EnteredGameplay && normal.PauseObserved
                    && normal.ReturnInvoked && normal.ReturnedToSelection,
                    normal.ReturnDetail ?? "native pause menu did not return to the selected-song detail");
            }

            // Do not invoke a second pause/menu action on the same gameplay
            // instance after a rejected return event.  The first attempt's
            // diagnostics identify the native state; a second synthetic menu
            // event would obscure that state and can race the transition.
            yield return StartCoroutine(WaitForSelectionReady(original, 12f));

            NativeSelection beforePractice = GetNativeSelectionSignature(table, navigation);
            Button practice = null;
            float practiceActionDeadline = Time.realtimeSinceStartup + SecondsRemaining(12f);
            while (!Expired() && Time.realtimeSinceStartup < practiceActionDeadline)
            {
                practice = FindDetailStartAction(FindActiveDetailController(), true);
                if (practice != null) break;
                yield return null;
            }
            GameplayAttempt practiceAttempt = new GameplayAttempt();
            if (practice == null)
            {
                AddCheck("game.practice-start-gameplay", false,
                    "native Practice action was not present on the selected-song detail");
                AddCheck("game.practice-pause-return", false,
                    "practice gameplay action was unavailable; physical HMD limitation applies");
            }
            else
            {
                yield return StartCoroutine(RunGameplayAttempt(practice, true, beforePractice, practiceAttempt));
                if (practiceAttempt.EnteredGameplay && practiceAttempt.ReturnInvoked
                    && !practiceAttempt.ReturnedToSelection)
                    yield return StartCoroutine(ReturnFromPracticeSetup(beforePractice, practiceAttempt));
                AddCheck("game.practice-start-gameplay", practiceAttempt.Invoked
                    && practiceAttempt.EnteredGameplay,
                    practiceAttempt.EntryDetail ?? "practice start did not reach an active GameCore/GameplayCore");
                AddCheck("game.practice-pause-return", practiceAttempt.EnteredGameplay
                    && practiceAttempt.PauseObserved && practiceAttempt.ReturnInvoked
                    && practiceAttempt.ReturnedToSelection,
                    practiceAttempt.ReturnDetail ?? "native pause menu did not return to the selected-song detail");
            }

            NativeSelection after = GetNativeSelectionSignature(table, navigation);
            practicePassed = normal.Invoked && normal.EnteredGameplay && normal.PauseObserved
                && normal.ReturnInvoked && normal.ReturnedToSelection
                && practiceAttempt.Invoked && practiceAttempt.EnteredGameplay
                && practiceAttempt.PauseObserved && practiceAttempt.ReturnInvoked
                && practiceAttempt.ReturnedToSelection
                && NativeSelectionEquivalent(original, after, false);
            AddCheck("game.start-practice-return-selection", practicePassed,
                "normal=" + DescribeGameplayAttempt(normal) + ",practice="
                + DescribeGameplayAttempt(practiceAttempt) + ",before="
                + DescribeSelection(original) + ",after=" + DescribeSelection(after));

            if (runtime != null && runtime.MenuActive)
            {
                panel.Open();
                ReadyAttempt ready = new ReadyAttempt();
                yield return StartCoroutine(WaitForPanelReady(ready));
            }
        }

        Button FindDetailStartAction(StandardLevelDetailViewController detail, bool practice)
        {
            if (detail == null) return null;
            Button[] buttons = detail.GetComponentsInChildren<Button>(true);
            string[] tokens = practice
                ? new[] { "practice", "练习" }
                : new[] { "start", "play", "action", "开始", "播放" };
            for (int i = 0; i < buttons.Length; i++)
            {
                Button button = buttons[i];
                if (button == null || !button.gameObject.activeInHierarchy || !button.interactable) continue;
                string semantic = ButtonSemanticText(button);
                if (!practice && HasToken(semantic, new[] { "practice", "练习" })) continue;
                if (HasToken(semantic, tokens)) return button;
            }
            return null;
        }

        IEnumerator RunGameplayAttempt(Button action, bool practice, NativeSelection before, GameplayAttempt result)
        {
            if (result == null) yield break;
            if (action != null)
            {
                try
                {
                    action.onClick.Invoke();
                    result.Invoked = true;
                }
                catch (Exception error)
                {
                    RecordException(practice ? "game.practice-entry" : "game.normal-entry", error);
                }
            }

            float entryDeadline = Time.realtimeSinceStartup + SecondsRemaining(practice ? 35f : 30f);
            int pauseReadyFrames = 0;
            string latestReadinessDetail = "not observed";
            while (!Expired() && Time.realtimeSinceStartup < entryDeadline)
            {
                string gameplayDetail;
                if (FindActiveGameplay(out gameplayDetail))
                {
                    result.EnteredGameplay = true;
                    string readinessDetail;
                    if (IsGameplayReadyForPause(out readinessDetail))
                    {
                        latestReadinessDetail = readinessDetail;
                        pauseReadyFrames++;
                        if (pauseReadyFrames >= 2)
                        {
                            result.GameplayReady = true;
                            result.EntryDetail = "invoked=" + result.Invoked + ",GameCore/GameplayCore active: "
                                + gameplayDetail + ",sceneReadyFrames=" + pauseReadyFrames + ",sceneReady=" + readinessDetail;
                            break;
                        }
                    }
                    else
                    {
                        pauseReadyFrames = 0;
                        latestReadinessDetail = readinessDetail;
                    }
                    result.EntryDetail = "invoked=" + result.Invoked + ",GameCore/GameplayCore active, waiting for native scene transition/pause readiness: frames="
                        + pauseReadyFrames + "," + latestReadinessDetail;
                }

                // Practice opens a native practice setup in some builds.  Only
                // invoke its own Start/Play control, never a background detail
                // button, and continue waiting for real gameplay objects.
                if (practice && !result.EnteredGameplay && !result.SetupInvoked)
                {
                    Button setupStart = FindPracticeSetupStartAction();
                    if (setupStart != null)
                    {
                        try
                        {
                            setupStart.onClick.Invoke();
                            result.SetupInvoked = true;
                        }
                        catch (Exception error)
                        {
                            RecordException("game.practice-setup", error);
                        }
                    }
                }
                yield return null;
            }

            if (!result.EnteredGameplay || !result.GameplayReady)
            {
                result.EntryDetail = "invoked=" + result.Invoked + ",setupInvoked="
                    + result.SetupInvoked + ",gameplay=" + result.EnteredGameplay + ",sceneReady="
                    + result.GameplayReady + ",GameCore/GameplayCore scene transition did not become pause-ready within the bounded wait; menu entry alone is not counted";
                yield break;
            }

            string pauseDetail = String.Empty;
            Component pauseController = null;
            float pauseRequestDeadline = Time.realtimeSinceStartup + SecondsRemaining(12f);
            while (!Expired() && Time.realtimeSinceStartup < pauseRequestDeadline)
            {
                pauseController = FindPauseController();
                if (PauseControllerCanPauseOrUnknown(pauseController))
                {
                    result.PauseRequested = InvokeNativePause(pauseController, out pauseDetail);
                    if (result.PauseRequested) break;
                }
                yield return null;
            }
            if (!result.PauseRequested)
            {
                result.ReturnDetail = "GameCore/GameplayCore entered, but PauseController.Pause was not invoked: "
                    + pauseDetail;
                yield break;
            }

            Component pauseOwner = null;
            float pauseDeadline = Time.realtimeSinceStartup + SecondsRemaining(20f);
            while (!Expired() && Time.realtimeSinceStartup < pauseDeadline)
            {
                if (FindActivePauseMenu(out pauseOwner, out pauseDetail)) break;
                yield return null;
            }
            result.PauseObserved = pauseOwner != null;
            if (!result.PauseObserved)
            {
                result.ReturnDetail = "gameplay active but native pause menu was not observed within the bounded wait; detail=" + pauseDetail;
                yield break;
            }

            int returnReadyFrames = 0;
            bool returnReady = false;
            float returnReadyDeadline = Time.realtimeSinceStartup + SecondsRemaining(10f);
            string returnReadyDetail = String.Empty;
            while (!Expired() && Time.realtimeSinceStartup < returnReadyDeadline)
            {
                Component visibleOwner;
                string visibleDetail;
                bool visible = FindActivePauseMenu(out visibleOwner, out visibleDetail);
                if (visibleOwner != null) pauseOwner = visibleOwner;
                Component currentPauseController = FindPauseController();
                bool canChange = PauseControllerCanChangeOrUnknown(currentPauseController);
                bool managerReady = PauseMenuManagerInteractionReady(pauseOwner, out returnReadyDetail);
                returnReadyDetail = "visible=" + visible + ",canChangePauseState=" + canChange
                    + ",managerInputReady=" + managerReady + ",frames=" + returnReadyFrames
                    + ",visibleDetail=" + visibleDetail + ",manager=" + returnReadyDetail;
                if (visible && canChange && managerReady)
                {
                    returnReadyFrames++;
                    if (returnReadyFrames >= 2)
                    {
                        returnReady = true;
                        break;
                    }
                }
                else returnReadyFrames = 0;
                yield return null;
            }
            result.PauseReadyDetail = returnReadyDetail + ",stableFrames=" + returnReadyFrames;
            if (!returnReady)
            {
                result.ReturnDetail = "native pause menu was visible but did not become return-action-ready: "
                    + result.PauseReadyDetail + "; state=" + DescribeGameplayState(pauseOwner);
                yield break;
            }

            result.ReturnStateBefore = DescribeGameplayState(pauseOwner);
            result.ReturnInvoked = InvokeNativePauseReturn(pauseOwner, out pauseDetail);
            CaptureAttempt returnCapture = new CaptureAttempt();
            string returnCapturePath = Path.Combine(runtimeDirectory,
                practice ? "gameplay-pause-return-practice.png" : "gameplay-pause-return-normal.png");
            if (result.ReturnInvoked && captureCameraPrepared && captureCamera != null)
            {
                yield return StartCoroutine(CaptureAndWait(returnCapturePath, returnCapture));
                result.ReturnCaptureDetail = (returnCapture.Completed ? "captured=" : "capture-failed=")
                    + returnCapturePath + "," + (returnCapture.Detail ?? String.Empty);
            }
            else result.ReturnCaptureDetail = result.ReturnInvoked
                ? "capture camera was not prepared before gameplay"
                : "return action was not invoked; no post-invoke capture";
            float returnDeadline = Time.realtimeSinceStartup + SecondsRemaining(35f);
            while (!Expired() && Time.realtimeSinceStartup < returnDeadline)
            {
                runtime = AdvancedSongFilters.Runtime.Current ?? runtime;
                table = FindActiveTable();
                navigation = GetNavigation();
                string gameplayDetail;
                NativeSelection current = GetNativeSelectionSignature(table, navigation);
                bool returned = !FindActiveGameplay(out gameplayDetail)
                    && runtime != null && runtime.MenuActive
                    && FindActiveDetailController() != null
                    && NativeSelectionEquivalent(before, current, false);
                if (returned)
                {
                    result.ReturnedToSelection = true;
                    break;
                }
                yield return null;
            }
            NativeSelection after = GetNativeSelectionSignature(table, navigation);
            result.ReturnStateAfter = DescribeGameplayState(pauseOwner);
            result.ReturnDetail = "pauseObserved=" + result.PauseObserved + ",returnInvoked="
                + result.ReturnInvoked + ",returned=" + result.ReturnedToSelection
                + ",pause=" + (pauseDetail ?? "") + ",before=" + DescribeSelection(before)
                + ",after=" + DescribeSelection(after) + ",pauseReady=" + (result.PauseReadyDetail ?? "")
                + ",beforeReturnState=" + (result.ReturnStateBefore ?? "")
                + ",afterReturnState=" + (result.ReturnStateAfter ?? "")
                + ",capture=" + (result.ReturnCaptureDetail ?? "");
        }

        IEnumerator WaitForSelectionReady(NativeSelection expected, float seconds)
        {
            float deadline = Time.realtimeSinceStartup + SecondsRemaining(seconds);
            while (!Expired() && Time.realtimeSinceStartup < deadline)
            {
                runtime = AdvancedSongFilters.Runtime.Current ?? runtime;
                table = FindActiveTable();
                navigation = GetNavigation();
                NativeSelection current = GetNativeSelectionSignature(table, navigation);
                if (runtime != null && runtime.MenuActive && FindActiveDetailController() != null
                    && NativeSelectionEquivalent(expected, current, false)) yield break;
                yield return null;
            }
        }

        string DescribeGameplayAttempt(GameplayAttempt value)
        {
            if (value == null) return "null";
            return "invoked=" + value.Invoked + ",setup=" + value.SetupInvoked
                + ",gameplay=" + value.EnteredGameplay + ",sceneReady=" + value.GameplayReady + ",pauseRequested=" + value.PauseRequested
                + ",pause=" + value.PauseObserved
                + ",returnInvoked=" + value.ReturnInvoked + ",returned="
                + value.ReturnedToSelection + ",pauseReady=" + (value.PauseReadyDetail ?? "")
                + ",capture=" + (value.ReturnCaptureDetail ?? "")
                + ",practiceSetupBack=" + value.PracticeSetupBackInvoked
                + ",practiceSetupReturned=" + value.PracticeSetupReturned;
        }

        IEnumerator ReturnFromPracticeSetup(NativeSelection expected, GameplayAttempt result)
        {
            if (result == null) yield break;
            float findDeadline = Time.realtimeSinceStartup + SecondsRemaining(12f);
            PracticeViewControllerState practiceState = new PracticeViewControllerState();
            Button back = null;
            string backDetail = String.Empty;
            while (!Expired() && Time.realtimeSinceStartup < findDeadline)
            {
                back = FindNativePracticeBackButton(out backDetail);
                CapturePracticeViewState(practiceState);
                if (practiceState.Active && practiceState.FlowInPractice && !practiceState.Transitioning
                    && back != null && back.gameObject.activeInHierarchy && back.interactable) break;
                yield return null;
            }
            string before = DescribePracticeViewState(practiceState);
            if (!practiceState.Active || !practiceState.FlowInPractice || practiceState.Transitioning
                || back == null || !back.gameObject.activeInHierarchy || !back.interactable)
            {
                result.PracticeSetupDetail = "practice setup remained unavailable for native Back: active="
                    + practiceState.Active + ",back=" + (back == null ? "null" : GetTransformPath(back.transform))
                    + ",backDetail=" + backDetail + ",state=" + before;
                result.ReturnDetail = (result.ReturnDetail ?? String.Empty) + ",practiceSetup=" + result.PracticeSetupDetail;
                yield break;
            }
            try
            {
                back.onClick.Invoke();
                result.PracticeSetupBackInvoked = true;
            }
            catch (Exception error)
            {
                result.PracticeSetupDetail = "native practice Back threw: " + DescribeException(error)
                    + ",button=" + GetTransformPath(back.transform) + ",state=" + before;
                RecordException("game.practice-setup-back", error);
                result.ReturnDetail = (result.ReturnDetail ?? String.Empty) + ",practiceSetup=" + result.PracticeSetupDetail;
                yield break;
            }

            float returnDeadline = Time.realtimeSinceStartup + SecondsRemaining(20f);
            while (!Expired() && Time.realtimeSinceStartup < returnDeadline)
            {
                runtime = AdvancedSongFilters.Runtime.Current ?? runtime;
                table = FindActiveTable();
                navigation = GetNavigation();
                NativeSelection current = GetNativeSelectionSignature(table, navigation);
                bool returned = runtime != null && runtime.MenuActive
                    && FindActiveDetailController() != null
                    && NativeSelectionEquivalent(expected, current, false)
                    && FindActivePracticeViewController() == null;
                if (returned)
                {
                    result.PracticeSetupReturned = true;
                    result.ReturnedToSelection = true;
                    break;
                }
                yield return null;
            }
            PracticeViewControllerState afterState = new PracticeViewControllerState();
            CapturePracticeViewState(afterState);
            NativeSelection after = GetNativeSelectionSignature(table, navigation);
            result.PracticeSetupDetail = "backInvoked=" + result.PracticeSetupBackInvoked
                + ",returned=" + result.PracticeSetupReturned + ",button=" + GetTransformPath(back.transform)
                + ",before=" + before + ",after=" + DescribePracticeViewState(afterState)
                + ",selection=" + DescribeSelection(after);
            result.ReturnDetail = (result.ReturnDetail ?? String.Empty) + ",practiceSetup=" + result.PracticeSetupDetail;
        }

        sealed class PracticeViewControllerState
        {
            public bool Active;
            public bool FlowInPractice;
            public bool Transitioning;
            public string Detail;
        }

        void CapturePracticeViewState(PracticeViewControllerState state)
        {
            if (state == null) return;
            Component practice = FindActivePracticeViewController();
            Component flow = FindActiveSinglePlayerFlowCoordinator();
            state.Active = practice != null;
            state.FlowInPractice = flow != null && ReadBoolMember(flow, "isInPracticeView", false);
            state.Transitioning = flow != null && ReadBoolMember(flow, "isInTransition", false);
            StringBuilder detail = new StringBuilder("practice=")
                .Append(practice == null ? "null" : GetTransformPath(practice.transform));
            if (flow == null) detail.Append(",flow=null");
            else detail.Append(",flow=").Append(GetTransformPath(flow.transform))
                .Append(",isInPracticeView=").Append(state.FlowInPractice)
                .Append(",isInTransition=").Append(state.Transitioning);
            state.Detail = detail.ToString();
        }

        string DescribePracticeViewState(PracticeViewControllerState state)
        {
            return state == null ? "null" : state.Detail ?? "";
        }

        Component FindActivePracticeViewController()
        {
            Component[] components = Resources.FindObjectsOfTypeAll<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component != null && component.gameObject.activeInHierarchy
                    && String.Equals(component.GetType().Name, "PracticeViewController", StringComparison.Ordinal))
                    return component;
            }
            return null;
        }

        Component FindActiveSinglePlayerFlowCoordinator()
        {
            Component[] components = Resources.FindObjectsOfTypeAll<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component != null && component.gameObject.activeInHierarchy
                    && component is SinglePlayerLevelSelectionFlowCoordinator)
                    return component;
            }
            return null;
        }

        Button FindNativePracticeBackButton(out string detail)
        {
            detail = String.Empty;
            Component[] components = Resources.FindObjectsOfTypeAll<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null || !component.gameObject.activeInHierarchy
                    || !String.Equals(component.GetType().Name, "ScreenSystem", StringComparison.Ordinal)) continue;
                Button back = GetFieldValue(component, "_backButton") as Button;
                detail = "ScreenSystem=" + GetTransformPath(component.transform) + ",button="
                    + (back == null ? "null" : GetTransformPath(back.transform)) + ",active="
                    + (back != null && back.gameObject.activeInHierarchy) + ",interactable="
                    + (back != null && back.interactable);
                if (back != null && back.gameObject.activeInHierarchy && back.interactable) return back;
            }
            detail = String.IsNullOrEmpty(detail) ? "active ScreenSystem with _backButton was unavailable" : detail;
            return null;
        }

        Button FindPracticeSetupStartAction()
        {
            Button[] buttons = Resources.FindObjectsOfTypeAll<Button>();
            for (int i = 0; i < buttons.Length; i++)
            {
                Button button = buttons[i];
                if (button == null || !button.gameObject.activeInHierarchy || !button.interactable
                    || !HasPracticeSetupAncestor(button)) continue;
                if (HasToken(ButtonSemanticText(button), new[] { "start", "play", "begin", "开始", "播放" }))
                    return button;
            }
            return null;
        }

        bool HasPracticeSetupAncestor(Button button)
        {
            if (button == null) return false;
            for (Transform current = button.transform; current != null; current = current.parent)
            {
                string name = (current.name ?? String.Empty).ToLowerInvariant();
                if (name.IndexOf("practice", StringComparison.Ordinal) >= 0
                    || name.IndexOf("gameplaysetup", StringComparison.Ordinal) >= 0)
                    return true;
                Component[] components = current.GetComponents<Component>();
                for (int i = 0; i < components.Length; i++)
                {
                    if (components[i] == null) continue;
                    string type = components[i].GetType().Name.ToLowerInvariant();
                    if (type.IndexOf("practice", StringComparison.Ordinal) >= 0
                        || type.IndexOf("gameplaysetup", StringComparison.Ordinal) >= 0)
                        return true;
                }
            }
            return false;
        }

        bool FindActiveGameplay(out string detail)
        {
            detail = String.Empty;
            Component[] components = Resources.FindObjectsOfTypeAll<Component>();
            List<string> matches = new List<string>();
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null || !component.gameObject.activeInHierarchy) continue;
                string type = component.GetType().FullName == null
                    ? component.GetType().Name.ToLowerInvariant()
                    : component.GetType().FullName.ToLowerInvariant();
                bool strong = type.IndexOf("gamecore", StringComparison.Ordinal) >= 0
                    || type.IndexOf("gameplaycore", StringComparison.Ordinal) >= 0;
                if (!strong) continue;
                string path = GetTransformPath(component.transform);
                matches.Add(type + "@" + path);
                if (matches.Count >= 4) break;
            }
            detail = matches.Count == 0 ? "none" : String.Join(";", matches.ToArray());
            return matches.Count > 0;
        }

        Component FindGameScenesManager()
        {
            Component[] components = Resources.FindObjectsOfTypeAll<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null || !component.gameObject.activeInHierarchy) continue;
                if (String.Equals(component.GetType().Name, "GameScenesManager", StringComparison.Ordinal)) return component;
            }
            return null;
        }

        bool IsGameplayReadyForPause(out string detail)
        {
            detail = String.Empty;
            Component scenes = FindGameScenesManager();
            Component pause = FindPauseController();
            bool inTransition = true;
            bool sceneNamesAvailable = false;
            bool standardGameplay = false;
            bool gameCore = false;
            bool defaultEnvironment = false;
            if (scenes != null)
            {
                object value = GetMemberValue(scenes, "isInTransition");
                inTransition = value is bool ? (bool)value : true;
                MethodInfo loadedMethod = scenes.GetType().GetMethod("GetCurrentlyLoadedSceneNames",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (loadedMethod != null && loadedMethod.GetParameters().Length == 0)
                {
                    object loaded = loadedMethod.Invoke(scenes, null);
                    IEnumerable names = loaded as IEnumerable;
                    if (names != null)
                    {
                        sceneNamesAvailable = true;
                        foreach (object nameValue in names)
                        {
                            string name = Convert.ToString(nameValue, CultureInfo.InvariantCulture);
                            if (String.Equals(name, "StandardGameplay", StringComparison.Ordinal)) standardGameplay = true;
                            if (String.Equals(name, "GameCore", StringComparison.Ordinal)) gameCore = true;
                            if (String.Equals(name, "DefaultEnvironment", StringComparison.Ordinal)) defaultEnvironment = true;
                        }
                    }
                }
            }
            bool canPause = PauseControllerCanPauseOrUnknown(pause);
            detail = "manager=" + (scenes != null) + ",inTransition=" + inTransition
                + ",loadedNames=" + sceneNamesAvailable + ",StandardGameplay=" + standardGameplay
                + ",GameCore=" + gameCore + ",DefaultEnvironment=" + defaultEnvironment
                + ",canPause=" + canPause;
            return scenes != null && !inTransition && sceneNamesAvailable && standardGameplay
                && gameCore && pause != null && canPause;
        }

        Component FindPauseController()
        {
            Component[] components = Resources.FindObjectsOfTypeAll<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null || !component.gameObject.activeInHierarchy) continue;
                if (String.Equals(component.GetType().Name, "PauseController", StringComparison.Ordinal))
                    return component;
            }
            return null;
        }

        bool InvokeNativePause(Component pauseController, out string detail)
        {
            detail = String.Empty;
            if (pauseController == null)
            {
                detail = "no active PauseController component";
                return false;
            }
            MethodInfo pause = pauseController.GetType().GetMethod("Pause",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pause == null || pause.GetParameters().Length != 0)
            {
                detail = pauseController.GetType().FullName + ".Pause() was unavailable";
                return false;
            }
            try
            {
                pause.Invoke(pauseController, null);
                detail = pauseController.GetType().FullName + ".Pause() invoked";
                return true;
            }
            catch (Exception error)
            {
                detail = "PauseController.Pause threw: " + DescribeException(error);
                RecordException("game.pause-request", error);
                return false;
            }
        }

        bool PauseControllerCanPauseOrUnknown(Component pauseController)
        {
            if (pauseController == null) return false;
            object value = GetMemberValue(pauseController, "canPause");
            if (value is bool && !(bool)value) return false;
            object stateValue = GetMemberValue(pauseController, "canChangePauseState");
            return stateValue == null || !(stateValue is bool) || (bool)stateValue;
        }

        bool PauseControllerCanChangeOrUnknown(Component pauseController)
        {
            if (pauseController == null) return false;
            object value = GetMemberValue(pauseController, "canChangePauseState");
            return value == null || !(value is bool) || (bool)value;
        }

        bool PauseMenuManagerInteractionReady(Component owner, out string detail)
        {
            detail = String.Empty;
            if (owner == null)
            {
                detail = "owner=null";
                return false;
            }
            if (!String.Equals(owner.GetType().Name, "PauseMenuManager", StringComparison.Ordinal))
            {
                detail = "owner=" + owner.GetType().Name + ",managerTimer=unknown";
                return true;
            }
            object remainingValue = GetFieldValue(owner, "_disabledInteractionRemainingTime");
            float remaining = 0f;
            bool timerKnown = remainingValue != null;
            if (timerKnown)
            {
                try { remaining = Convert.ToSingle(remainingValue, CultureInfo.InvariantCulture); }
                catch { timerKnown = false; }
            }
            Component animation = GetFieldValue(owner, "_pauseAnimationController") as Component;
            Component animator = animation == null ? null : GetFieldValue(animation, "_animator") as Component;
            bool animationTransition = false;
            if (animator != null && animator.gameObject.activeInHierarchy)
            {
                MethodInfo transitionMethod = animator.GetType().GetMethod("IsInTransition",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                    new[] { typeof(int) }, null);
                if (transitionMethod != null)
                {
                    try { animationTransition = Convert.ToBoolean(transitionMethod.Invoke(animator, new object[] { 0 }), CultureInfo.InvariantCulture); }
                    catch { animationTransition = false; }
                }
            }
            detail = "owner=PauseMenuManager,disabledInteractionRemaining="
                + remaining.ToString("0.###", CultureInfo.InvariantCulture)
                + ",timerKnown=" + timerKnown + ",animationTransition=" + animationTransition;
            return (!timerKnown || remaining <= 0f) && !animationTransition;
        }

        string DescribeGameplayState(Component pauseOwner)
        {
            StringBuilder result = new StringBuilder();
            Component scenes = FindGameScenesManager();
            if (scenes == null) result.Append("scenes=null");
            else
            {
                bool inTransition = ReadBoolMember(scenes, "isInTransition", true);
                object transition = GetMemberValue(scenes, "currentSceneTransitionType");
                result.Append("scenes={inTransition=").Append(inTransition)
                    .Append(",transition=").Append(transition == null ? "?" : transition.ToString())
                    .Append(",loaded=").Append(DescribeLoadedSceneNames(scenes))
                    .Append(",stack=").Append(DescribeSceneStack(scenes)).Append("}");
            }
            string gameplayDetail;
            bool gameplay = FindActiveGameplay(out gameplayDetail);
            Component activePauseOwner;
            string pauseDetail;
            bool pauseVisible = FindActivePauseMenu(out activePauseOwner, out pauseDetail);
            Component pauseController = FindPauseController();
            result.Append(",activeGameplay=").Append(gameplay).Append("[").Append(gameplayDetail).Append("]")
                .Append(",pauseOwner=").Append(pauseOwner == null ? "null" : pauseOwner.GetType().FullName)
                .Append(",pauseVisible=").Append(pauseVisible).Append("[").Append(pauseDetail).Append("]")
                .Append(",pauseController=").Append(pauseController == null ? "null" : pauseController.GetType().FullName)
                .Append(",canChange=").Append(PauseControllerCanChangeOrUnknown(pauseController));
            if (pauseOwner != null)
            {
                string managerInputDetail;
                PauseMenuManagerInteractionReady(pauseOwner, out managerInputDetail);
                result.Append(",pauseManagerState=").Append(managerInputDetail)
                    .Append(",menuEvent=").Append(DescribeDelegate(pauseOwner, "didPressMenuButtonEvent"));
            }
            if (pauseController != null)
            {
                result.Append(",returnEvent=").Append(DescribeDelegate(pauseController, "didReturnToMenuEvent"))
                    .Append(",returnController=").Append(DescribeObjectType(GetFieldValue(pauseController, "_returnToMenuController")));
            }
            result.Append(",visibleOverlays=").Append(DescribeVisibleOverlays());
            return result.ToString();
        }

        string DescribeLoadedSceneNames(Component scenes)
        {
            if (scenes == null) return "null";
            MethodInfo loadedMethod = scenes.GetType().GetMethod("GetCurrentlyLoadedSceneNames",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (loadedMethod == null || loadedMethod.GetParameters().Length != 0) return "method-missing";
            try
            {
                IEnumerable names = loadedMethod.Invoke(scenes, null) as IEnumerable;
                if (names == null) return "null";
                List<string> values = new List<string>();
                foreach (object name in names)
                {
                    if (values.Count >= 16) { values.Add("..."); break; }
                    values.Add(Convert.ToString(name, CultureInfo.InvariantCulture));
                }
                return "[" + String.Join("|", values.ToArray()) + "]";
            }
            catch (Exception error) { return "error=" + DescribeException(error); }
        }

        string DescribeSceneStack(Component scenes)
        {
            object value = GetFieldValue(scenes, "_scenesStack");
            IEnumerable stack = value as IEnumerable;
            if (stack == null) return "null";
            List<string> layers = new List<string>();
            foreach (object item in stack)
            {
                if (layers.Count >= 8) { layers.Add("..."); break; }
                IEnumerable names = GetMemberValue(item, "sceneNames") as IEnumerable;
                if (names == null) { layers.Add("?"); continue; }
                List<string> sceneNames = new List<string>();
                foreach (object name in names)
                {
                    if (sceneNames.Count >= 16) { sceneNames.Add("..."); break; }
                    sceneNames.Add(Convert.ToString(name, CultureInfo.InvariantCulture));
                }
                layers.Add("[" + String.Join("|", sceneNames.ToArray()) + "]");
            }
            return "[" + String.Join(";", layers.ToArray()) + "]";
        }

        string DescribeObjectType(object value)
        {
            return value == null ? "null" : value.GetType().FullName;
        }

        string DescribeDelegate(object target, string fieldName)
        {
            if (target == null) return "target=null";
            object value = GetFieldValue(target, fieldName);
            Delegate callback = value as Delegate;
            if (callback == null) return value == null ? "null" : value.GetType().FullName;
            Delegate[] handlers = callback.GetInvocationList();
            List<string> names = new List<string>();
            for (int i = 0; i < handlers.Length && i < 8; i++)
            {
                MethodInfo method = handlers[i].Method;
                names.Add(method == null ? "?" : method.DeclaringType.FullName + "." + method.Name);
            }
            return "count=" + handlers.Length.ToString(CultureInfo.InvariantCulture)
                + ",handlers=" + String.Join("|", names.ToArray());
        }

        string DescribeVisibleOverlays()
        {
            Component[] components = Resources.FindObjectsOfTypeAll<Component>();
            List<string> overlays = new List<string>();
            for (int i = 0; i < components.Length && overlays.Count < 12; i++)
            {
                Component component = components[i];
                if (component == null || !component.gameObject.activeInHierarchy) continue;
                string type = component.GetType().Name ?? String.Empty;
                string lowerType = type.ToLowerInvariant();
                if (lowerType.IndexOf("modal", StringComparison.Ordinal) < 0
                    && lowerType.IndexOf("dialog", StringComparison.Ordinal) < 0
                    && lowerType.IndexOf("confirm", StringComparison.Ordinal) < 0
                    && lowerType.IndexOf("popup", StringComparison.Ordinal) < 0
                    && lowerType.IndexOf("prompt", StringComparison.Ordinal) < 0) continue;
                object shownValue = GetMemberValue(component, "isShown");
                if (shownValue is bool && !(bool)shownValue) continue;
                Button[] buttons = component.GetComponentsInChildren<Button>(true);
                int activeButtons = 0;
                for (int b = 0; b < buttons.Length; b++)
                    if (buttons[b] != null && buttons[b].gameObject.activeInHierarchy) activeButtons++;
                TMP_Text[] labels = component.GetComponentsInChildren<TMP_Text>(true);
                List<string> text = new List<string>();
                for (int t = 0; t < labels.Length && text.Count < 4; t++)
                {
                    if (labels[t] == null || !labels[t].gameObject.activeInHierarchy) continue;
                    string value = (labels[t].text ?? String.Empty).Trim();
                    if (!String.IsNullOrEmpty(value)) text.Add(value.Length > 60 ? value.Substring(0, 60) : value);
                }
                overlays.Add(type + "@" + GetTransformPath(component.transform) + ",buttons="
                    + activeButtons.ToString(CultureInfo.InvariantCulture) + ",text=" + String.Join("|", text.ToArray()));
            }
            return overlays.Count == 0 ? "none" : String.Join(";", overlays.ToArray());
        }

        bool FindActivePauseMenu(out Component owner, out string detail)
        {
            owner = null;
            detail = String.Empty;
            Component[] components = Resources.FindObjectsOfTypeAll<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null || !component.gameObject.activeInHierarchy) continue;
                string type = component.GetType().FullName == null
                    ? component.GetType().Name.ToLowerInvariant()
                    : component.GetType().FullName.ToLowerInvariant();
                string path = GetTransformPath(component.transform).ToLowerInvariant();
                if ((String.Equals(component.GetType().Name, "PauseMenuManager", StringComparison.Ordinal)
                    || type.IndexOf("pausemenu", StringComparison.Ordinal) >= 0
                    || path.IndexOf("pausemenu", StringComparison.Ordinal) >= 0)
                    && IsPauseMenuVisible(component, out detail))
                {
                    owner = component;
                    return true;
                }
            }
            Button[] buttons = Resources.FindObjectsOfTypeAll<Button>();
            for (int i = 0; i < buttons.Length; i++)
            {
                Button button = buttons[i];
                if (button == null || !button.gameObject.activeInHierarchy) continue;
                string path = GetTransformPath(button.transform).ToLowerInvariant();
                if (path.IndexOf("pausemenu", StringComparison.Ordinal) >= 0
                    && button.interactable)
                {
                    owner = button;
                    detail = "button=" + path;
                    return true;
                }
            }
            detail = "no active PauseMenu/PauseMenuManager component or pause-menu button";
            return false;
        }

        bool IsPauseMenuVisible(Component owner, out string detail)
        {
            detail = String.Empty;
            if (owner == null) return false;
            Transform container = GetFieldValue(owner, "_pauseContainerTransform") as Transform;
            GameObject controllers = GetFieldValue(owner, "_menuControllersParent") as GameObject;
            Button continueButton = GetFieldValue(owner, "_continueButton") as Button;
            Button restartButton = GetFieldValue(owner, "_restartButton") as Button;
            Button backButton = GetFieldValue(owner, "_backButton") as Button;
            bool containerActive = container != null && container.gameObject.activeInHierarchy;
            bool controllersActive = controllers != null && controllers.activeInHierarchy;
            bool actionable = IsActiveInteractable(backButton) || IsActiveInteractable(restartButton)
                || IsActiveInteractable(continueButton);
            detail = owner.GetType().FullName + "@" + GetTransformPath(owner.transform)
                + ",container=" + containerActive + ",controllers=" + controllersActive
                + ",continue=" + IsActiveInteractable(continueButton)
                + ",restart=" + IsActiveInteractable(restartButton)
                + ",back=" + IsActiveInteractable(backButton);
            return (containerActive || controllersActive) && actionable;
        }

        bool IsActiveInteractable(Button button)
        {
            return button != null && button.gameObject.activeInHierarchy && button.interactable;
        }

        bool InvokeNativePauseReturn(Component owner, out string detail)
        {
            detail = String.Empty;
            if (owner != null && String.Equals(owner.GetType().Name, "PauseMenuManager", StringComparison.Ordinal))
            {
                MethodInfo menu = owner.GetType().GetMethod("MenuButtonPressed",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (menu != null && menu.GetParameters().Length == 0)
                {
                    try
                    {
                        menu.Invoke(owner, null);
                        detail = "PauseMenuManager.MenuButtonPressed() invoked; event="
                            + DescribeDelegate(owner, "didPressMenuButtonEvent");
                        return true;
                    }
                    catch (Exception error)
                    {
                        RecordException("game.pause-return", error);
                        detail = "PauseMenuManager.MenuButtonPressed threw: " + DescribeException(error);
                    }
                }
                Button backButton = GetFieldValue(owner, "_backButton") as Button;
                if (IsActiveInteractable(backButton))
                {
                    try
                    {
                        backButton.onClick.Invoke();
                        detail = "PauseMenuManager._backButton invoked after MenuButtonPressed was unavailable";
                        return true;
                    }
                    catch (Exception error)
                    {
                        RecordException("game.pause-return", error);
                        detail = "PauseMenuManager._backButton threw: " + DescribeException(error);
                    }
                }
            }
            Button target = FindPauseReturnAction(owner);
            if (target != null)
            {
                try
                {
                    target.onClick.Invoke();
                    detail = "native button=" + GetTransformPath(target.transform);
                    return true;
                }
                catch (Exception error)
                {
                    RecordException("game.pause-return", error);
                    detail = "native pause button threw: " + DescribeException(error);
                }
            }
            detail = String.IsNullOrEmpty(detail)
                ? "native pause menu had no Back/Quit/Return-to-menu action" : detail;
            return false;
        }

        Button FindPauseReturnAction(Component owner)
        {
            if (owner != null && String.Equals(owner.GetType().Name, "PauseMenuManager", StringComparison.Ordinal))
            {
                Button backButton = GetFieldValue(owner, "_backButton") as Button;
                if (IsActiveInteractable(backButton)) return backButton;
            }
            Button[] buttons = owner == null ? new Button[0] : owner.GetComponentsInChildren<Button>(true);
            Button candidate = FindPauseReturnButton(buttons);
            if (candidate != null) return candidate;
            buttons = Resources.FindObjectsOfTypeAll<Button>();
            List<Button> candidates = new List<Button>();
            for (int i = 0; i < buttons.Length; i++)
            {
                Button button = buttons[i];
                if (button == null || !button.gameObject.activeInHierarchy || !button.interactable) continue;
                string path = GetTransformPath(button.transform).ToLowerInvariant();
                if (path.IndexOf("pausemenu", StringComparison.Ordinal) >= 0) candidates.Add(button);
            }
            return FindPauseReturnButton(candidates.ToArray());
        }

        Button FindPauseReturnButton(Button[] buttons)
        {
            for (int i = 0; i < buttons.Length; i++)
            {
                Button button = buttons[i];
                if (button == null || !button.gameObject.activeInHierarchy || !button.interactable) continue;
                string semantic = ButtonSemanticText(button);
                if (HasToken(semantic, new[] { "resume", "continue", "继续" })) continue;
                if (HasToken(semantic, new[] { "quit", "exit", "menu", "song", "back", "返回", "选歌", "菜单" }))
                    return button;
            }
            return null;
        }

        Button FindDetailAction(StandardLevelDetailViewController detail, string[] tokens)
        {
            if (detail == null) return null;
            Button[] buttons = detail.GetComponentsInChildren<Button>(true);
            for (int i = 0; i < buttons.Length; i++)
            {
                Button button = buttons[i];
                if (button == null || !button.gameObject.activeInHierarchy || !button.interactable) continue;
                if (HasToken(ButtonSemanticText(button), tokens)) return button;
            }
            return null;
        }

        string FindDifficultyToken(object target)
        {
            if (target == null) return null;
            string keyToken = FindBeatmapKeyDifficulty(target);
            if (!String.IsNullOrEmpty(keyToken)) return keyToken;
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo field = fields[i];
                    object value;
                    try { value = field.GetValue(target); } catch { continue; }
                    string token = DifficultyValueToken(field.Name, field.FieldType, value);
                    if (!String.IsNullOrEmpty(token)) return token;
                }
                PropertyInfo[] properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < properties.Length; i++)
                {
                    PropertyInfo property = properties[i];
                    if (property.GetIndexParameters().Length != 0) continue;
                    object value;
                    try { value = property.GetValue(target, null); } catch { continue; }
                    string token = DifficultyValueToken(property.Name, property.PropertyType, value);
                    if (!String.IsNullOrEmpty(token)) return token;
                }
            }
            return null;
        }

        string DifficultyValueToken(string fieldName, Type fieldType, object value)
        {
            if (value == null) return null;
            if (fieldType.IsEnum && fieldType.Name.IndexOf("DifficultyMask", StringComparison.OrdinalIgnoreCase) >= 0)
                return null;
            if (fieldType.IsEnum && fieldType.Name.IndexOf("Difficulty", StringComparison.OrdinalIgnoreCase) >= 0)
                return value.ToString();
            if (fieldName.IndexOf("difficulty", StringComparison.OrdinalIgnoreCase) < 0
                && value.GetType().Name.IndexOf("BeatmapKey", StringComparison.OrdinalIgnoreCase) < 0) return null;
            for (Type type = value.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(x => x.FieldType.IsEnum && x.FieldType.Name.IndexOf("Difficulty", StringComparison.OrdinalIgnoreCase) >= 0);
                if (field != null)
                {
                    try { return field.GetValue(value).ToString(); } catch { return null; }
                }
            }
            if (value.GetType().Name.IndexOf("BeatmapKey", StringComparison.OrdinalIgnoreCase) >= 0)
                return FindDifficultyEnum(value);
            return null;
        }

        string FindBeatmapKeyDifficulty(object target)
        {
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                PropertyInfo keyProperty = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(x => x.GetIndexParameters().Length == 0
                        && (String.Equals(x.Name, "beatmapKey", StringComparison.OrdinalIgnoreCase)
                            || x.PropertyType.Name.IndexOf("BeatmapKey", StringComparison.OrdinalIgnoreCase) >= 0));
                if (keyProperty != null)
                {
                    try
                    {
                        object key = keyProperty.GetValue(target, null);
                        string token = FindDifficultyEnum(key);
                        if (!String.IsNullOrEmpty(token)) return token;
                    }
                    catch { }
                }
                FieldInfo keyField = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(x => String.Equals(x.Name, "_beatmapKey", StringComparison.OrdinalIgnoreCase)
                        || x.FieldType.Name.IndexOf("BeatmapKey", StringComparison.OrdinalIgnoreCase) >= 0);
                if (keyField != null)
                {
                    try
                    {
                        string token = FindDifficultyEnum(keyField.GetValue(target));
                        if (!String.IsNullOrEmpty(token)) return token;
                    }
                    catch { }
                }
            }
            return null;
        }

        string FindDifficultyEnum(object value)
        {
            if (value == null) return null;
            for (Type type = value.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(x => x.FieldType.IsEnum && x.FieldType.Name.IndexOf("Difficulty", StringComparison.OrdinalIgnoreCase) >= 0);
                if (field != null)
                {
                    try { return field.GetValue(value).ToString(); } catch { return null; }
                }
            }
            return null;
        }

        bool NativeSelectionEquivalent(NativeSelection before, NativeSelection after, bool requireActive)
        {
            if (before == null || after == null) return false;
            bool level = String.Equals(before.LevelId, after.LevelId, StringComparison.Ordinal);
            bool difficulty = !before.DifficultyAvailable
                || (after.DifficultyAvailable && String.Equals(before.Difficulty, after.Difficulty, StringComparison.Ordinal));
            bool active = !requireActive
                || (before.DetailActive == after.DetailActive && before.StartActive == after.StartActive);
            return level && difficulty && active;
        }

        string DescribeSelection(NativeSelection value)
        {
            if (value == null) return "null";
            return "{level=" + (value.LevelId ?? "null") + ",difficulty=" + (value.Difficulty ?? "unavailable")
                + ",detail=" + value.DetailActive + ",start=" + value.StartActive + "}";
        }

        BeatmapLevel FindBeatmapReference(object target)
        {
            if (target == null) return null;
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo field = fields[i];
                    if (!typeof(BeatmapLevel).IsAssignableFrom(field.FieldType)) continue;
                    try
                    {
                        BeatmapLevel value = field.GetValue(target) as BeatmapLevel;
                        if (value != null) return value;
                    }
                    catch { }
                }
            }
            return null;
        }

        IEnumerator ValidateLiveInteractions()
        {
            FilterSettings originalApplied = runtime == null || runtime.Applied == null
                ? new FilterSettings() : runtime.Applied.Clone();
            List<string> originalInputIds = GetAdapterIds("Input");
            List<string> originalOutputIds = GetAdapterIds("Output");
            NativeSelection originalSelection = GetNativeSelectionSignature(table, navigation);
            UserDataSnapshot userData = CaptureUserDataSnapshot();
            NotesCache originalCache = CapturePluginCache();
            bool interactionPhaseCompleted = false;
            try
            {
                ReadyAttempt ready = new ReadyAttempt();
                yield return StartCoroutine(EnsurePanelReady(ready));
                if (!ready.Ready)
                {
                    AddCheck("interaction.completed", false, "panel was not interactive");
                    yield break;
                }

                bool resetInvoked = InvokePanelMethod("Reset", new object[0]);
                yield return null;
                FilterSettings resetDraft = GetDraftSettings();
                bool resetOpen = panel.IsOpen;
                bool resetEmpty = IsUnconstrained(resetDraft);
                AddCheck("interaction.reset-draft-only", resetInvoked && resetOpen && resetEmpty,
                    "resetInvoked=" + resetInvoked + ",open=" + resetOpen + ",emptyDraft=" + resetEmpty);

                panel.CancelDraft();
                yield return StartCoroutine(WaitForPanelClosed(4f));
                bool resetCancelPreserved = SettingsEquivalent(runtime.Applied, originalApplied)
                    && NativeSelectionEquivalent(originalSelection, GetNativeSelectionSignature(table, navigation), false);
                AddCheck("interaction.reset-then-cancel-preserves-original", resetCancelPreserved,
                    "reset changed draft only; cancel preserved applied settings and selection=" + resetCancelPreserved);

                ready = new ReadyAttempt();
                yield return StartCoroutine(EnsurePanelReady(ready));
                FilterSettings beforeClose = runtime.Applied == null ? new FilterSettings() : runtime.Applied.Clone();
                bool closeChanged = MutateDraftForV020();
                panel.Close();
                yield return StartCoroutine(WaitForPanelClosed(4f));
                bool closePreserved = closeChanged && SettingsEquivalent(runtime.Applied, beforeClose);
                AddCheck("interaction.close-discards-draft", closePreserved,
                    "closeChanged=" + closeChanged + ",appliedPreserved=" + closePreserved);

                ready = new ReadyAttempt();
                yield return StartCoroutine(EnsurePanelReady(ready));
                long revisionBeforeDifficulty = GetRevisionNumber();
                bool difficultyChanged = ToggleDifficultyForSmoke();
                long revisionAfterDifficulty = GetRevisionNumber();
                object difficultyPreview = RequestPreviewForSmoke(GetDraftSettings(), revisionAfterDifficulty);
                yield return StartCoroutine(WaitForPreviewCanApply(difficultyPreview, 12f));
                difficultyPreview = GetRuntimePreview(difficultyPreview);
                bool difficultyPreviewUpdated = difficultyChanged
                    && (revisionAfterDifficulty != revisionBeforeDifficulty || difficultyPreview != null)
                    && !PreviewIsError(difficultyPreview);
                AddCheck("interaction.difficulty-refreshes-preview", difficultyPreviewUpdated,
                    "changed=" + difficultyChanged + ",revision=" + revisionBeforeDifficulty + "->" + revisionAfterDifficulty
                    + ",preview=" + DescribePreview(difficultyPreview));

                string modeDetail;
                bool modeNative = ValidateNativeModeSelection(out modeDetail);
                AddCheck("interaction.mode-selector-native-action", modeNative, modeDetail);

                string numericDetail;
                bool numericBack = ValidateNumericRangeBackAndConfirm(out numericDetail);
                AddCheck("interaction.numeric-back-rolls-back-range", numericBack, numericDetail);

                ready = new ReadyAttempt();
                yield return StartCoroutine(EnsurePanelReady(ready));
                FilterSettings draftToApply = GetDraftSettings();
                long applyRevision = GetRevisionNumber();
                object previewBeforeApply = RequestPreviewForSmoke(draftToApply, applyRevision);
                yield return StartCoroutine(WaitForPreviewCanApply(previewBeforeApply, 15f));
                previewBeforeApply = GetRuntimePreview(previewBeforeApply);
                NativeSelection beforeApply = GetNativeSelectionSignature(table, navigation);
                panel.ApplyDraft();
                yield return StartCoroutine(WaitForRuntimeSettled(15f));
                NativeSelection afterApply = GetNativeSelectionSignature(table, navigation);
                int appliedCount = GetAdapterIds("Output").Count;
                int previewMatched = PreviewInt(previewBeforeApply, "Matched", -1);
                bool appliedChanged = runtime.Applied != null && draftToApply != null
                    && SettingsEquivalent(runtime.Applied, draftToApply);
                bool previewAgrees = previewMatched < 0 || previewMatched == appliedCount;
                bool applySelection = NativeSelectionEquivalent(beforeApply, afterApply, true);
                int previewUnknown = PreviewInt(previewBeforeApply, "Unknown", -1);
                object previewStateValue = GetMemberValue(previewBeforeApply, "State");
                string previewState = previewStateValue == null ? String.Empty
                    : Convert.ToString(previewStateValue, CultureInfo.InvariantCulture);
                bool unknownCountMeaningful = previewUnknown >= 0
                    && (!String.Equals(previewState, "Partial", StringComparison.OrdinalIgnoreCase)
                        || previewUnknown > 0);
                AddCheck("interaction.apply-preview-agrees", appliedChanged && previewAgrees,
                    "previewMatched=" + previewMatched.ToString(CultureInfo.InvariantCulture)
                    + ",appliedCount=" + appliedCount.ToString(CultureInfo.InvariantCulture)
                    + ",preview=" + DescribePreview(previewBeforeApply));
                AddCheck("interaction.preview-unknown-count", unknownCountMeaningful,
                    "unknown=" + previewUnknown.ToString(CultureInfo.InvariantCulture)
                    + ",state=" + previewState + "; Partial previews must retain a non-zero Unknown count");
                AddCheck("interaction.apply-preserves-selection", applySelection,
                    "before=" + DescribeSelection(beforeApply) + ",after=" + DescribeSelection(afterApply));

                ready = new ReadyAttempt();
                yield return StartCoroutine(EnsurePanelReady(ready));
                bool emptyReset = InvokePanelMethod("ClearDraft", new object[0]);
                FilterSettings emptyDraft = GetDraftSettings();
                long emptyRevision = GetRevisionNumber();
                object emptyPreview = RequestPreviewForSmoke(emptyDraft, emptyRevision);
                yield return StartCoroutine(WaitForPreviewCanApply(emptyPreview, 15f));
                emptyPreview = GetRuntimePreview(emptyPreview);
                bool emptyPreviewReady = PreviewCanApply(emptyPreview);
                if (emptyPreviewReady) panel.ApplyDraft();
                yield return StartCoroutine(WaitForRuntimeSettled(15f));
                int emptyCount = GetAdapterIds("Output").Count;
                bool emptyApplied = runtime.Applied != null && IsUnconstrained(runtime.Applied);
                bool emptyRestoresList = emptyCount == originalInputIds.Count;
                AddCheck("interaction.empty-apply-restores-original-count", emptyReset && emptyPreviewReady
                    && emptyApplied && emptyRestoresList,
                    "emptyApplied=" + emptyApplied + ",before=" + originalOutputIds.Count.ToString(CultureInfo.InvariantCulture)
                    + ",after=" + emptyCount.ToString(CultureInfo.InvariantCulture)
                    + ",input=" + originalInputIds.Count.ToString(CultureInfo.InvariantCulture)
                    + ",previewReady=" + emptyPreviewReady + ",preview=" + DescribePreview(emptyPreview));

                yield return StartCoroutine(ValidateOfficialExclusion());

                ready = new ReadyAttempt();
                yield return StartCoroutine(EnsurePanelReady(ready));
                string presetsDetail;
                bool presetsPassed = ValidatePresetCrud(originalApplied, out presetsDetail);
                AddCheck("interaction.presets-crud", presetsPassed, presetsDetail);

                ready = new ReadyAttempt();
                yield return StartCoroutine(EnsurePanelReady(ready));
                string presetNameDetail;
                bool presetNamesPassed = ValidatePresetNameProtection(out presetNameDetail);
                AddCheck("interaction.preset-name-protection", presetNamesPassed, presetNameDetail);

                ready = new ReadyAttempt();
                yield return StartCoroutine(EnsurePanelReady(ready));
                string saveFailureDetail;
                bool saveFailurePassed = ValidateLiveSaveFailureRecovery(out saveFailureDetail);
                AddCheck("interaction.commit-save-failure-retry", saveFailurePassed, saveFailureDetail);

                PreviewContractAttempt previewContract = new PreviewContractAttempt();
                yield return StartCoroutine(ValidatePreviewContract(previewContract));
                AddCheck("interaction.preview-contract", previewContract.Passed, previewContract.Detail);
                interactionPhaseCompleted = true;
            }
            finally
            {
                try
                {
                    MethodInfo endPreview = runtime == null ? null : runtime.GetType().GetMethod("EndPreview",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (endPreview != null && endPreview.GetParameters().Length == 0) endPreview.Invoke(runtime, null);
                    RestoreRuntimeFields(originalApplied);
                    RestoreUserDataSnapshot(userData);
                    RestorePluginCache(originalCache);
                }
                catch (Exception error)
                {
                    RecordException("interaction.emergency-restore", error);
                }
            }

            yield return StartCoroutine(WaitForRuntimeSettled(15f));
            SelectionRestoreAttempt selectedRestoreAttempt = new SelectionRestoreAttempt();
            yield return StartCoroutine(RestoreNativeSelectionAndWait(originalSelection, 15f, selectedRestoreAttempt));
            bool selectedRestored = selectedRestoreAttempt.Restored;
            bool userDataRestored = RestoreUserDataSnapshot(userData);
            RestorePluginCache(originalCache);
            ReadyAttempt restoredReady = new ReadyAttempt();
            yield return StartCoroutine(EnsurePanelReady(restoredReady));
            bool restored = selectedRestored && userDataRestored && restoredReady.Ready
                && SettingsEquivalent(runtime.Applied, originalApplied);
            AddCheck("interaction.restore-userdata-presets-and-selection", restored,
                "settings/cache/presets files, Runtime.Applied, native selection and open panel restored=" + restored
                + ",selectInvoked=" + selectedRestoreAttempt.Invoked);
            AddCheck("interaction.completed", interactionPhaseCompleted, "live Panel/Runtime interaction phase completed");
        }

        FilterSettings GetDraftSettings()
        {
            FilterSettings value = GetPanelDraft() as FilterSettings;
            return value == null ? null : value.Clone();
        }

        long GetRevisionNumber()
        {
            object value = GetPanelRevision();
            if (value == null) return -1L;
            try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
            catch { return -1L; }
        }

        FilterSettings BuildUnconstrainedSettings()
        {
            FilterSettings value = new FilterSettings
            {
                Enabled = false,
                Mode = "All",
                Difficulties = new int[0],
                Nps = new NumericRange(),
                Njs = new NumericRange(),
                Duration = new NumericRange(),
                Bpm = new NumericRange(),
                Notes = new NumericRange()
            };
            return value;
        }

        IEnumerator ValidateOfficialExclusion()
        {
            ReadyAttempt ready = new ReadyAttempt();
            yield return StartCoroutine(EnsurePanelReady(ready));
            panel.Reset();
            bool moreOpened = ClickScopeCellNative(1);
            yield return null;
            object setting = GetMemberValue(panel, "boundExcludeOfficial");
            Toggle toggle = GetMemberValue(setting, "Toggle") as Toggle;
            bool controlReady = ready.Ready && moreOpened && toggle != null
                && toggle.gameObject.activeInHierarchy && toggle.interactable;
            if (!controlReady)
            {
                AddCheck("interaction.exclude-official", false, "native source switch was unavailable");
                yield break;
            }
            FilterSettings before = runtime.Applied.Clone();
            long revision = panel.Revision;
            toggle.OnPointerClick(new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left });
            bool draftOnly = panel.Draft.ExcludeOfficial && panel.Draft.ActiveCount == 1
                && panel.Revision > revision && SettingsEquivalent(before, runtime.Applied);
            RectTransform rect = toggle.GetComponent<RectTransform>();
            bool hitbox = rect != null && rect.rect.width >= 8f && rect.rect.height >= 8f;
            object preview = RequestPreviewForSmoke(panel.Draft, panel.Revision);
            yield return StartCoroutine(WaitForPreviewCanApply(preview, 15f));
            preview = GetRuntimePreview(preview);
            int matched = PreviewInt(preview, "Matched", -1);
            bool complete = PreviewCanApply(preview) && PreviewInt(preview, "Unknown", -1) == 0;
            if (complete) panel.ApplyDraft();
            yield return StartCoroutine(WaitForRuntimeSettled(15f));
            List<BeatmapLevel> output = GetAdapterLevels("Output");
            bool applied = runtime.Applied.ExcludeOfficial && runtime.Applied.Enabled
                && output.All(x => FilterEngine.IsCustomLevelId(x.levelID)) && matched == output.Count;
            BeatmapLevel official = GetAdapterLevels("Input").FirstOrDefault(x => !FilterEngine.IsCustomLevelId(x.levelID));
            BeatmapLevel custom = SongCore.Loader.CustomLevels.Values.FirstOrDefault(x => x != null);
            bool mixedPassed = false;
            if (official != null && custom != null)
            {
                var mixed = runtime.FilterApplied(new[] { official, custom });
                mixedPassed = mixed.Count == 1 && mixed[0].levelID == custom.levelID;
            }
            bool zeroCleared = output.Count != 0 || FindActiveDetailController() == null;
            AddCheck("interaction.exclude-official", draftOnly && hitbox && complete && applied
                && mixedPassed && zeroCleared,
                "draftOnly=" + draftOnly + ",hitbox=" + hitbox + ",preview=" + DescribePreview(preview)
                + ",applied=" + applied + ",mixedLibrary=" + mixedPassed + ",zeroDetailCleared=" + zeroCleared);

            ready = new ReadyAttempt();
            yield return StartCoroutine(EnsurePanelReady(ready));
            panel.Reset();
            bool cleared = !panel.Draft.ExcludeOfficial;
            preview = RequestPreviewForSmoke(panel.Draft, panel.Revision);
            yield return StartCoroutine(WaitForPreviewCanApply(preview, 15f));
            if (PreviewCanApply(GetRuntimePreview(preview))) panel.ApplyDraft();
            yield return StartCoroutine(WaitForRuntimeSettled(15f));
            AddCheck("interaction.exclude-official-reset", cleared && !runtime.Applied.ExcludeOfficial
                && GetAdapterIds("Output").Count == GetAdapterIds("Input").Count,
                "reset clears the draft source condition and applying restores the current pack");
        }

        bool IsUnconstrained(FilterSettings value)
        {
            if (value == null) return false;
            return !value.Enabled && !value.ExcludeOfficial && (String.IsNullOrEmpty(value.Mode)
                || String.Equals(value.Mode, "All", StringComparison.OrdinalIgnoreCase))
                && (value.Difficulties == null || value.Difficulties.Length == 0)
                && (value.Nps == null || !value.Nps.IsSet)
                && (value.Njs == null || !value.Njs.IsSet)
                && (value.Duration == null || !value.Duration.IsSet)
                && (value.Bpm == null || !value.Bpm.IsSet)
                && (value.Notes == null || !value.Notes.IsSet);
        }

        bool MutateDraftForV020()
        {
            FilterSettings before = GetDraftSettings();
            if (before == null) return false;
            bool invoked = InvokePanelMethod("AdjustBound", new object[] { Metric.Nps, true, 1 });
            FilterSettings after = GetDraftSettings();
            if (invoked && !SettingsEquivalent(before, after)) return true;
            if (OpenNumericForMetricSmoke("Nps", true))
            {
                invoked = InvokePanelMethod("AdjustBound", new object[] { Metric.Nps, true, 1 });
                ReturnToMainPage();
                after = GetDraftSettings();
                if (invoked && !SettingsEquivalent(before, after)) return true;
            }
            string[] modes = runtime == null ? new string[0] : runtime.Modes;
            for (int i = 0; i < modes.Length; i++)
            {
                if (String.Equals(modes[i], before.Mode, StringComparison.OrdinalIgnoreCase)) continue;
                if (InvokePanelMethod("SetMode", new object[] { modes[i] }))
                {
                    after = GetDraftSettings();
                    if (!SettingsEquivalent(before, after)) return true;
                }
            }
            return false;
        }

        bool ToggleDifficultyForSmoke()
        {
            if (InvokePanelMethod("ToggleDifficulty", new object[] { 2 })) return true;
            if (InvokePanelMethod("ToggleDifficulty", new object[] { 0 })) return true;
            return false;
        }

        bool ValidateNativeModeSelection(out string detail)
        {
            detail = String.Empty;
            bool reset = InvokePanelMethod("Reset", new object[0]);
            FilterSettings before = GetDraftSettings();
            long beforeRevision = GetRevisionNumber();
            Component selector = GetMemberValue(panel, "boundModeSelector") as Component;
            object cells = selector == null ? null : GetMemberValue(selector, "cells");
            int cellCount = 0;
            IEnumerable enumerable = cells as IEnumerable;
            if (enumerable != null)
                foreach (object ignored in enumerable) cellCount++;
            string clickDetail = String.Empty;
            bool clicked = cellCount > 0 && ClickSegmentedCellNative("boundModeSelector", 0, out clickDetail);
            FilterSettings selected = GetDraftSettings();
            bool selectedMode = selected != null && !String.Equals(selected.Mode, "All", StringComparison.OrdinalIgnoreCase);
            long selectedRevision = GetRevisionNumber();
            Button allModes = GetMemberValue(panel, "boundAllModesButton") as Button;
            bool allClicked = false;
            if (allModes != null && allModes.gameObject.activeInHierarchy && allModes.interactable)
            {
                allModes.onClick.Invoke();
                allClicked = true;
            }
            FilterSettings restored = GetDraftSettings();
            bool allMode = restored != null && (String.IsNullOrEmpty(restored.Mode)
                || String.Equals(restored.Mode, "All", StringComparison.OrdinalIgnoreCase));
            detail = "reset=" + reset + ",cells=" + cellCount + ",clicked=" + clicked
                + ",selectedMode=" + (selected == null ? "null" : selected.Mode)
                + ",revision=" + beforeRevision + "->" + selectedRevision
                + ",allClicked=" + allClicked + ",allMode=" + allMode
                + ",native=" + (clickDetail ?? String.Empty);
            return reset && clicked && selectedMode && selectedRevision > beforeRevision
                && allClicked && allMode && SettingsEquivalent(before, restored);
        }

        bool ValidateNumericRangeBackAndConfirm(out string detail)
        {
            detail = String.Empty;
            bool resetForBack = InvokePanelMethod("Reset", new object[0]);
            bool plusMinimum = InvokePanelMethod("AdjustBound", new object[] { Metric.Nps, true, 1 });
            bool plusMaximum = InvokePanelMethod("AdjustBound", new object[] { Metric.Nps, false, 1 });
            bool plusMaximumAgain = InvokePanelMethod("AdjustBound", new object[] { Metric.Nps, false, 1 });
            FilterSettings steppedDraft = GetDraftSettings();
            NumericRange stepped = GetDraftRange(Metric.Nps);
            bool openedBack = OpenNumericForMetricSmoke("Nps", true);
            bool enteredBackMinimum = EnterNumericBufferForSmoke("2");
            bool switchedBackMaximum = InvokePanelMethod("RangeMaximum", new object[0]);
            bool enteredBackMaximum = EnterNumericBufferForSmoke("4");
            bool scratchPresentBeforeBack = GetMemberValue(panel, "rangeSession") != null
                && FindNumericSurface() != null && FindNumericSurface().activeInHierarchy;
            ReturnToMainPage();
            NumericRange afterBack = GetDraftRange(Metric.Nps);
            bool backRolledBack = resetForBack && plusMinimum && plusMaximum && plusMaximumAgain
                && openedBack && enteredBackMinimum && switchedBackMaximum && enteredBackMaximum
                && scratchPresentBeforeBack && RangeEqual(stepped, afterBack);

            bool resetForConfirm = InvokePanelMethod("Reset", new object[0]);
            long revisionBeforeConfirm = GetRevisionNumber();
            bool openedConfirm = OpenNumericForMetricSmoke("Nps", true);
            bool enteredMinimum = EnterNumericBufferForSmoke("1.5");
            bool switchedMaximum = InvokePanelMethod("RangeMaximum", new object[0]);
            bool enteredMaximum = EnterNumericBufferForSmoke("3.5");
            object scratch = GetMemberValue(panel, "rangeSession");
            string scratchMinimum = Convert.ToString(GetMemberValue(scratch, "MinimumText"), CultureInfo.InvariantCulture);
            string scratchMaximum = Convert.ToString(GetMemberValue(scratch, "MaximumText"), CultureInfo.InvariantCulture);
            bool scratchRetainedBoth = String.Equals(scratchMinimum, "1.5", StringComparison.Ordinal)
                && String.Equals(scratchMaximum, "3.5", StringComparison.Ordinal);
            bool confirmed = InvokePanelMethod("ConfirmRange", new object[0]);
            NumericRange afterConfirm = GetDraftRange(Metric.Nps);
            long revisionAfterConfirm = GetRevisionNumber();
            bool confirmCommittedBoth = confirmed && afterConfirm != null && afterConfirm.Min.HasValue
                && afterConfirm.Max.HasValue && afterConfirm.Min.Value == 1.5d
                && afterConfirm.Max.Value == 3.5d && revisionAfterConfirm > revisionBeforeConfirm;

            FilterSettings beforeInvalidSettings = GetDraftSettings();
            long revisionBeforeInvalid = GetRevisionNumber();
            bool openedInvalid = OpenNumericForMetricSmoke("Nps", true);
            bool invalidCleared = InvokePanelMethod("KeyClear", new object[0]);
            bool invalidDecimal = InvokePanelMethod("KeyDecimal", new object[0]);
            bool invalidSessionBeforeConfirm = GetMemberValue(panel, "rangeSession") != null;
            bool invalidConfirmed = InvokePanelMethod("ConfirmRange", new object[0]);
            FilterSettings afterInvalidSettings = GetDraftSettings();
            bool invalidStayedOpen = panel.IsOpen && FindNumericSurface() != null
                && FindNumericSurface().activeInHierarchy;
            bool invalidSessionAfterConfirm = GetMemberValue(panel, "rangeSession") != null;
            bool invalidDraftPreserved = SettingsEquivalent(beforeInvalidSettings, afterInvalidSettings)
                && GetRevisionNumber() == revisionBeforeInvalid;
            InvokePanelMethod("CancelRange", new object[0]);
            bool invalidCancelPreserved = SettingsEquivalent(beforeInvalidSettings, GetDraftSettings());

            detail = "backOpened=" + openedBack + ",backInput=" + enteredBackMinimum + "/"
                + enteredBackMaximum + ",backScratch=" + scratchPresentBeforeBack + ",back=" + backRolledBack
                + ",confirmOpened=" + openedConfirm + ",confirmInput=" + enteredMinimum + "/"
                + enteredMaximum + ",scratch=" + scratchMinimum + "/" + scratchMaximum
                + ",scratchRetained=" + scratchRetainedBoth + ",confirmed=" + confirmed
                + ",range=" + DescribeRange(afterConfirm) + ",revision=" + revisionBeforeConfirm
                + "->" + revisionAfterConfirm + ",invalidOpened=" + openedInvalid
                + ",invalidInput=" + invalidCleared + "/" + invalidDecimal
                + ",invalidConfirmed=" + invalidConfirmed + ",invalidStayedOpen=" + invalidStayedOpen
                + ",invalidSession=" + invalidSessionBeforeConfirm + "/" + invalidSessionAfterConfirm
                + ",invalidDraftPreserved=" + invalidDraftPreserved
                + ",invalidCancelPreserved=" + invalidCancelPreserved;
            if (panel.IsOpen) panel.CancelDraft();
            return backRolledBack && scratchRetainedBoth && confirmCommittedBoth
                && openedInvalid && invalidCleared && invalidDecimal && invalidConfirmed
                && invalidStayedOpen && invalidSessionBeforeConfirm && invalidSessionAfterConfirm
                && invalidDraftPreserved && invalidCancelPreserved;
        }

        string DescribeRange(NumericRange value)
        {
            if (value == null) return "null";
            return "[" + (value.Min.HasValue ? value.Min.Value.ToString(CultureInfo.InvariantCulture) : "-")
                + "," + (value.Max.HasValue ? value.Max.Value.ToString(CultureInfo.InvariantCulture) : "-") + "]";
        }

        NumericRange GetDraftRange(Metric metric)
        {
            FilterSettings value = GetDraftSettings();
            if (value == null) return null;
            NumericRange range = value.Range(metric);
            return range == null ? null : range.Clone();
        }

        bool EnterNumericBufferForSmoke(string value)
        {
            if (value == null) return false;
            bool invoked = InvokePanelMethod("KeyClear", new object[0]);
            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                if (character >= '0' && character <= '9')
                    invoked = InvokePanelMethod("Key" + character, new object[0]) && invoked;
                else if (character == '.' || character == ':')
                    invoked = InvokePanelMethod("KeyDecimal", new object[0]) && invoked;
                else return false;
            }
            return invoked;
        }

        bool ConfirmNumericEdit()
        {
            return InvokePanelMethod("ConfirmRange", new object[0]);
        }

        object RequestPreviewForSmoke(FilterSettings settings, long revision)
        {
            if (runtime == null || settings == null) return null;
            MethodInfo method = FindMethod(runtime.GetType(), "RequestPreview",
                new[] { typeof(FilterSettings), typeof(int) });
            if (method == null) return null;
            try
            {
                method.Invoke(runtime, new object[] { settings, Convert.ToInt32(revision, CultureInfo.InvariantCulture) });
                return null;
            }
            catch (Exception error)
            {
                RecordException("preview.request", error);
                return null;
            }
        }

        object GetRuntimePreview(object fallback)
        {
            object value = GetMemberValue(runtime, "Preview");
            return value ?? fallback;
        }

        IEnumerator WaitForPreviewCanApply(object initial, float seconds)
        {
            float deadline = Time.realtimeSinceStartup + SecondsRemaining(seconds);
            object current = initial;
            while (!Expired() && Time.realtimeSinceStartup < deadline)
            {
                current = GetRuntimePreview(current);
                if (PreviewCanApply(current) || PreviewIsError(current)) yield break;
                yield return null;
            }
        }

        bool PreviewCanApply(object preview)
        {
            object value = GetMemberValue(preview, "CanApply");
            return value is bool && (bool)value;
        }

        bool PreviewIsError(object preview)
        {
            object state = GetMemberValue(preview, "State");
            return state != null && String.Equals(Convert.ToString(state, CultureInfo.InvariantCulture), "Error", StringComparison.OrdinalIgnoreCase);
        }

        Task ObserveInputAsyncForSmoke(List<BeatmapLevel> levels)
        {
            if (runtime == null || levels == null) return null;
            MethodInfo method = FindMethod(runtime.GetType(), "ObserveInput",
                new[] { typeof(IReadOnlyList<BeatmapLevel>) });
            if (method == null) return null;
            try
            {
                return Task.Factory.StartNew(delegate
                {
                    method.Invoke(runtime, new object[] { levels });
                });
            }
            catch (Exception error)
            {
                RecordException("preview.observe-input", error);
                return null;
            }
        }

        int PreviewInt(object preview, string name, int fallback)
        {
            object value = GetMemberValue(preview, name);
            if (value == null) return fallback;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        string DescribePreview(object preview)
        {
            if (preview == null) return "null";
            object state = GetMemberValue(preview, "State");
            object matched = GetMemberValue(preview, "Matched");
            object total = GetMemberValue(preview, "Total");
            object unknown = GetMemberValue(preview, "Unknown");
            object category = GetMemberValue(preview, "ErrorCategory");
            return "state=" + (state == null ? "?" : Convert.ToString(state, CultureInfo.InvariantCulture))
                + ",matched=" + (matched == null ? "?" : Convert.ToString(matched, CultureInfo.InvariantCulture))
                + ",total=" + (total == null ? "?" : Convert.ToString(total, CultureInfo.InvariantCulture))
                + ",unknown=" + (unknown == null ? "?" : Convert.ToString(unknown, CultureInfo.InvariantCulture))
                + ",error=" + (category == null ? "?" : Convert.ToString(category, CultureInfo.InvariantCulture));
        }

        IEnumerator ValidatePreviewContract(PreviewContractAttempt result)
        {
            if (result == null) yield break;
            result.Passed = false;
            result.Detail = String.Empty;
            FilterSettings settings = GetDraftSettings() ?? BuildUnconstrainedSettings();
            FilterSettings firstSettings = settings.Clone();
            firstSettings.Enabled = true;
            firstSettings.Nps = new NumericRange { Min = 0.5d, Max = 99999d };
            FilterSettings supersedingSettings = BuildUnconstrainedSettings();
            long revision = GetRevisionNumber();
            long supersedingRevision = revision < 0 ? 1L : revision + 1L;
            List<BeatmapLevel> originalInput = GetAdapterLevels("Input");
            List<BeatmapLevel> changedInput = new List<BeatmapLevel>(originalInput);
            changedInput.Reverse();
            object preview = RequestPreviewForSmoke(firstSettings, revision);
            preview = GetRuntimePreview(preview);
            bool firstLoading = preview != null && !PreviewCanApply(preview);
            Task observeTask = null;
            bool observeScheduled = changedInput.Count > 1;
            if (observeScheduled)
            {
                observeTask = ObserveInputAsyncForSmoke(changedInput);
                observeScheduled = observeTask != null;
                while (observeTask != null && !observeTask.IsCompleted && !Expired()) yield return null;
                if (observeTask != null && observeTask.IsFaulted)
                {
                    RecordException("preview.observe-input", observeTask.Exception);
                    observeScheduled = false;
                }
                // Let the posted ObserveInput boundary reach Runtime before
                // requesting the superseding preview. This exercises the
                // background-input stamp and an actually pending preview.
                List<string> changedInputIds = changedInput.Select(x => x.levelID).ToList();
                float boundaryDeadline = Time.realtimeSinceStartup + SecondsRemaining(20f);
                while (!Expired() && Time.realtimeSinceStartup < boundaryDeadline
                    && (!GetAdapterIds("Input").SequenceEqual(changedInputIds)
                        || (runtime != null && runtime.IsBusy)))
                    yield return null;
            }
            object supersedingPreview = RequestPreviewForSmoke(supersedingSettings, supersedingRevision);
            yield return StartCoroutine(WaitForPreviewCanApply(supersedingPreview, 15f));
            supersedingPreview = GetRuntimePreview(supersedingPreview);
            bool shape = supersedingPreview != null && HasReadableMember(supersedingPreview, "State")
                && HasReadableMember(supersedingPreview, "Matched") && HasReadableMember(supersedingPreview, "Total")
                && HasReadableMember(supersedingPreview, "Unknown") && HasReadableMember(supersedingPreview, "ErrorCategory")
                && HasReadableMember(supersedingPreview, "CanApply");
            bool retry = FindMethod(runtime.GetType(), "Retry", Type.EmptyTypes) != null;
            bool healthyRetryHidden = true;
            GameObject root = GetPanelRootObject();
            if (root != null && !PreviewIsError(supersedingPreview))
                healthyRetryHidden = CountSemanticButtons(root.GetComponentsInChildren<Button>(true),
                    new[] { "retry", "重试" }) == 0;
            object finalRevision = GetMemberValue(runtime, "previewRevision");
            bool revisionAdvanced = false;
            if (finalRevision != null)
            {
                try { revisionAdvanced = Convert.ToInt64(finalRevision, CultureInfo.InvariantCulture) >= supersedingRevision; }
                catch { revisionAdvanced = false; }
            }
            FilterSettings finalSettings = GetMemberValue(runtime, "previewSettings") as FilterSettings;
            bool supersedingSettingsWon = finalSettings != null
                && SettingsEquivalent(finalSettings, supersedingSettings);
            bool staleCancelled = observeScheduled && firstLoading && revisionAdvanced && supersedingSettingsWon;

            bool restoredInput = true;
            if (observeScheduled)
            {
                Task restoreTask = ObserveInputAsyncForSmoke(originalInput);
                restoredInput = restoreTask != null;
                while (restoreTask != null && !restoreTask.IsCompleted && !Expired()) yield return null;
                if (restoreTask != null && restoreTask.IsFaulted)
                {
                    RecordException("preview.observe-input-restore", restoreTask.Exception);
                    restoredInput = false;
                }
                if (restoredInput)
                {
                    float inputRestoreDeadline = Time.realtimeSinceStartup + SecondsRemaining(12f);
                    while (!Expired() && Time.realtimeSinceStartup < inputRestoreDeadline
                        && !GetAdapterIds("Input").SequenceEqual(originalInput.Select(x => x.levelID)))
                        yield return null;
                    restoredInput = GetAdapterIds("Input").SequenceEqual(originalInput.Select(x => x.levelID));
                }
            }

            result.Detail = DescribePreview(preview) + ",latest=" + DescribePreview(supersedingPreview)
                + ",observeInput=" + observeScheduled + ",firstLoading=" + firstLoading
                + ",revisionAdvanced=" + revisionAdvanced + ",supersedingSettingsWon=" + supersedingSettingsWon
                + ",inputRestored=" + restoredInput
                + ",retryMethod=" + retry + ",healthyRetryHidden=" + healthyRetryHidden
                + ",staleCancelled=" + staleCancelled;
            result.Passed = shape && retry && healthyRetryHidden && staleCancelled && restoredInput;
        }

        bool ValidatePresetCrud(FilterSettings originalApplied, out string detail)
        {
            detail = String.Empty;
            try
            {
                object available = GetMemberValue(runtime, "PresetsAvailable");
                if (available is bool && !(bool)available)
                {
                    detail = "preset store is unavailable; runtime refused to overwrite its source file";
                    return false;
                }
                MethodInfo save = FindMethodByName(runtime.GetType(), "SavePreset", 3);
                MethodInfo rename = FindMethodByName(runtime.GetType(), "RenamePreset", 2);
                MethodInfo delete = FindMethodByName(runtime.GetType(), "DeletePreset", 1);
                if (save == null || rename == null || delete == null)
                {
                    detail = "preset CRUD methods were not exposed";
                    return false;
                }
                FilterSettings settings = GetDraftSettings() ?? originalApplied.Clone();
                string name = "ASF smoke " + DateTime.UtcNow.ToString("MMddHHmmssfff", CultureInfo.InvariantCulture);
                object saved = InvokeRuntimeMethod(save, new object[] { null, name, settings });
                string id = Convert.ToString(GetMemberValue(saved, "Id"), CultureInfo.InvariantCulture);
                bool created = saved != null && !String.IsNullOrEmpty(id) && HasPreset(id);
                string rowDetail = String.Empty;
                bool rowActions = created && ValidatePresetRowActions(id, GetDraftSettings(), out rowDetail);
                List<string> outputBeforeLoad = GetAdapterIds("Output");
                bool beforeLoadSame = true;
                bool loadedDraft = created && InvokePanelMethod("Load", new object[] { settings });
                bool afterLoadSame = beforeLoadSame && outputBeforeLoad.SequenceEqual(GetAdapterIds("Output"));
                if (panel.IsOpen) panel.CancelDraft();
                bool savedSurvivesCancel = created && HasPreset(id);
                bool renamed = created && Convert.ToBoolean(InvokeRuntimeMethod(rename, new object[] { id, name + " renamed" }), CultureInfo.InvariantCulture);
                bool updated = created && InvokeRuntimeMethod(save, new object[] { id, name + " updated", settings }) != null;
                bool deleted = created && Convert.ToBoolean(InvokeRuntimeMethod(delete, new object[] { id }), CultureInfo.InvariantCulture)
                    && !HasPreset(id);
                detail = "created=" + created + ",loadedDraft=" + loadedDraft + ",listUnchanged=" + afterLoadSame
                    + ",savedSurvivesCancel=" + savedSurvivesCancel
                    + ",rowActions=" + rowActions + "[" + (rowDetail ?? String.Empty) + "]"
                    + ",renamed=" + renamed + ",updated=" + updated + ",deleted=" + deleted;
                return created && rowActions && loadedDraft && afterLoadSame && savedSurvivesCancel
                    && renamed && updated && deleted;
            }
            catch (Exception error)
            {
                detail = "preset CRUD exception: " + DescribeException(error);
                RecordException("preset-crud", error);
                return false;
            }
        }

        bool ValidatePresetRowActions(string id, FilterSettings beforeDraft, out string detail)
        {
            detail = String.Empty;
            try
            {
                bool opened = InvokePanelMethod("OpenManagePresets", new object[0]);
                GameObject options = GetMemberValue(panel, "boundPresetOptions") as GameObject;
                Transform row = options == null ? null : options.transform.Find("PresetRow_" + id);
                Button rename = FindPresetRowButton(row, "重命名", 1);
                Button delete = FindPresetRowButton(row, "删除", 2);
                if (rename == null || delete == null)
                {
                    detail = "opened=" + opened + ",row=" + (row == null ? "missing" : row.name)
                        + ",rename=" + (rename != null) + ",delete=" + (delete != null);
                    InvokePanelMethod("CloseManagePresets", new object[0]);
                    return false;
                }

                rename.onClick.Invoke();
                string editingId = Convert.ToString(GetMemberValue(panel, "editingPresetId"), CultureInfo.InvariantCulture);
                GameObject renamePage = GetMemberValue(panel, "boundPresetRenamePage") as GameObject;
                bool renamePageActive = renamePage != null && renamePage.activeInHierarchy;
                bool renameDidNotLoad = SettingsEquivalent(beforeDraft, GetDraftSettings());
                bool renameIdCorrect = String.Equals(editingId, id, StringComparison.Ordinal);
                bool renameCancelled = InvokePanelMethod("CancelPresetName", new object[0]);
                bool manageAfterRename = IsPresetPageActive("boundPresetManagePage");

                options = GetMemberValue(panel, "boundPresetOptions") as GameObject;
                row = options == null ? null : options.transform.Find("PresetRow_" + id);
                delete = FindPresetRowButton(row, "删除", 2);
                if (delete == null)
                {
                    detail = "renamePage=" + renamePageActive + ",renameId=" + editingId
                        + ",renameCancelled=" + renameCancelled + ",manageAfterRename=" + manageAfterRename
                        + ",deleteButton=missing";
                    InvokePanelMethod("CloseManagePresets", new object[0]);
                    return false;
                }
                delete.onClick.Invoke();
                string deleteId = Convert.ToString(GetMemberValue(panel, "deletePresetId"), CultureInfo.InvariantCulture);
                GameObject deletePage = GetMemberValue(panel, "boundPresetDeletePage") as GameObject;
                bool deletePageActive = deletePage != null && deletePage.activeInHierarchy;
                bool deleteDidNotLoad = SettingsEquivalent(beforeDraft, GetDraftSettings());
                bool deleteIdCorrect = String.Equals(deleteId, id, StringComparison.Ordinal);
                bool deleteCancelled = InvokePanelMethod("CancelDeletePreset", new object[0]);
                bool manageAfterDelete = IsPresetPageActive("boundPresetManagePage");
                bool closed = InvokePanelMethod("CloseManagePresets", new object[0]);
                string pageDetail;
                bool pagesInactive = ValidatePresetPagesInactive(true, out pageDetail);
                detail = "opened=" + opened + ",renamePage=" + renamePageActive + ",renameId="
                    + editingId + ",renameDraftPreserved=" + renameDidNotLoad + ",renameCancelled="
                    + renameCancelled + ",manageAfterRename=" + manageAfterRename + ",deletePage="
                    + deletePageActive + ",deleteId=" + deleteId + ",deleteDraftPreserved="
                    + deleteDidNotLoad + ",deleteCancelled=" + deleteCancelled + ",manageAfterDelete="
                    + manageAfterDelete + ",closed=" + closed + ",pages=" + pageDetail;
                return opened && renamePageActive && renameIdCorrect && renameDidNotLoad
                    && renameCancelled && manageAfterRename && deletePageActive && deleteIdCorrect
                    && deleteDidNotLoad && deleteCancelled && manageAfterDelete && closed && pagesInactive;
            }
            catch (Exception error)
            {
                detail = "preset row action exception: " + DescribeException(error);
                RecordException("preset-row-actions", error);
                try { InvokePanelMethod("CloseManagePresets", new object[0]); } catch { }
                return false;
            }
        }

        Button FindPresetRowButton(Transform row, string token, int fallbackIndex)
        {
            if (row == null) return null;
            Button[] buttons = row.GetComponentsInChildren<Button>(true);
            for (int i = 0; i < buttons.Length; i++)
            {
                Button button = buttons[i];
                if (button != null && HasToken(ButtonSemanticText(button), new[] { token })) return button;
            }
            return fallbackIndex >= 0 && fallbackIndex < buttons.Length ? buttons[fallbackIndex] : null;
        }

        bool IsPresetPageActive(string fieldName)
        {
            GameObject page = GetMemberValue(panel, fieldName) as GameObject;
            return page != null && page.activeInHierarchy;
        }

        bool ValidatePresetPagesInactive(bool mainExpected, out string detail)
        {
            string[] names = { "boundPresetSelectorPage", "boundPresetManagePage", "boundPresetSavePage", "boundPresetRenamePage", "boundPresetDeletePage" };
            int active = 0;
            string activeNames = String.Empty;
            for (int i = 0; i < names.Length; i++)
            {
                if (!IsPresetPageActive(names[i])) continue;
                active++;
                if (activeNames.Length > 0) activeNames += ",";
                activeNames += names[i];
            }
            bool main = !mainExpected || IsPresetPageActive("boundMainPage");
            detail = "activePresetPages=" + active + (activeNames.Length == 0 ? String.Empty : "[" + activeNames + "]")
                + ",mainActive=" + IsPresetPageActive("boundMainPage");
            return active == 0 && main;
        }

        bool ValidatePresetNameProtection(out string detail)
        {
            detail = String.Empty;
            List<string> createdIds = new List<string>();
            try
            {
                if (runtime == null || panel == null || !panel.IsOpen)
                {
                    detail = "runtime or panel was unavailable for preset-name rendering";
                    return false;
                }
                object available = GetMemberValue(runtime, "PresetsAvailable");
                if (available is bool && !(bool)available)
                {
                    detail = "preset store is unavailable; reserved-name rendering could not be exercised";
                    return false;
                }
                MethodInfo save = FindMethodByName(runtime.GetType(), "SavePreset", 3);
                MethodInfo delete = FindMethodByName(runtime.GetType(), "DeletePreset", 1);
                if (save == null || delete == null)
                {
                    detail = "preset SavePreset/DeletePreset methods were not exposed";
                    return false;
                }
                FilterSettings settings = GetDraftSettings() ?? new FilterSettings();
                string[] requiredNames = { "Default", "Back" };
                for (int i = 0; i < requiredNames.Length; i++)
                {
                    if (FindPresetByName(requiredNames[i]) != null) continue;
                    object saved = InvokeRuntimeMethod(save, new object[] { null, requiredNames[i], settings });
                    string id = Convert.ToString(GetMemberValue(saved, "Id"), CultureInfo.InvariantCulture);
                    if (saved == null || String.IsNullOrEmpty(id))
                    {
                        detail = "could not create reserved-name fixture " + requiredNames[i];
                        return false;
                    }
                    createdIds.Add(id);
                }

                InvokePanelMethod("UpdateStatus", new object[0]);
                bool opened = InvokePanelMethod("OpenManagePresets", new object[0]);
                GameObject root = GetPanelRootObject();
                TMP_Text[] texts = root == null ? new TMP_Text[0] : root.GetComponentsInChildren<TMP_Text>(true);
                bool defaultVisible = HasExactText(texts, "Default");
                bool backVisible = HasExactText(texts, "Back");
                bool closed = InvokePanelMethod("CloseManagePresets", new object[0]);

                bool cleanup = true;
                for (int i = 0; i < createdIds.Count; i++)
                {
                    bool removed = Convert.ToBoolean(InvokeRuntimeMethod(delete,
                        new object[] { createdIds[i] }), CultureInfo.InvariantCulture);
                    cleanup = cleanup && removed && !HasPreset(createdIds[i]);
                }
                InvokePanelMethod("UpdateStatus", new object[0]);
                detail = "opened=" + opened + ",Default=" + defaultVisible + ",Back="
                    + backVisible + ",closed=" + closed + ",fixtureCleanup=" + cleanup;
                return opened && defaultVisible && backVisible && closed && cleanup;
            }
            catch (Exception error)
            {
                detail = "preset-name rendering exception: " + DescribeException(error);
                RecordException("preset-name-protection", error);
                return false;
            }
            finally
            {
                // If the panel or the runtime threw midway through the visual
                // assertion, do a best-effort cleanup of only fixtures owned by
                // this check.  The outer UserData snapshot remains authoritative.
                try
                {
                    MethodInfo delete = runtime == null ? null : FindMethodByName(runtime.GetType(), "DeletePreset", 1);
                    for (int i = 0; i < createdIds.Count; i++)
                        if (HasPreset(createdIds[i]) && delete != null)
                            InvokeRuntimeMethod(delete, new object[] { createdIds[i] });
                    InvokePanelMethod("CloseManagePresets", new object[0]);
                    InvokePanelMethod("UpdateStatus", new object[0]);
                }
                catch (Exception error)
                {
                    RecordException("preset-name-cleanup", error);
                }
            }
        }

        object FindPresetByName(string name)
        {
            foreach (object preset in GetPresetItems())
            {
                string value = Convert.ToString(GetMemberValue(preset, "Name"), CultureInfo.InvariantCulture);
                if (String.Equals(value, name, StringComparison.Ordinal)) return preset;
            }
            return null;
        }

        bool HasExactText(TMP_Text[] texts, string expected)
        {
            for (int i = 0; i < texts.Length; i++)
            {
                TMP_Text text = texts[i];
                if (text != null && String.Equals((text.text ?? String.Empty).Trim(), expected, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        bool ValidateLiveSaveFailureRecovery(out string detail)
        {
            detail = String.Empty;
            string failureRoot = Path.Combine(runtimeDirectory, "save-failure");
            string blockedDestination = Path.Combine(failureRoot, "UserData", "AdvancedSongFilters.json");
            FieldInfo storeField = null;
            object originalStore = null;
            FilterSettings beforeApplied = runtime == null || runtime.Applied == null
                ? new FilterSettings() : runtime.Applied.Clone();
            try
            {
                if (runtime == null || panel == null || !panel.IsOpen)
                {
                    detail = "runtime or panel unavailable for save-failure recovery";
                    return false;
                }
                FilterSettings failedDraft = GetDraftSettings();
                if (!MutateDraftForV020())
                {
                    detail = "could not create a distinct draft before forcing the save failure";
                    return false;
                }
                failedDraft = GetDraftSettings();
                if (failedDraft == null || SettingsEquivalent(failedDraft, beforeApplied))
                {
                    detail = "draft did not differ from applied settings before forcing the save failure";
                    return false;
                }
                Type plugin = typeof(AdvancedSongFilters.Runtime).Assembly.GetType("AdvancedSongFilters.Plugin", false);
                storeField = plugin == null ? null : GetFieldIncludingBases(plugin, "Store");
                if (storeField == null)
                {
                    detail = "Plugin.Store field was unavailable";
                    return false;
                }
                originalStore = storeField.GetValue(null);
                Directory.CreateDirectory(Path.Combine(failureRoot, "UserData"));
                Directory.CreateDirectory(blockedDestination);
                Persistence failureStore = new Persistence(failureRoot, delegate(string failureCategory, Exception error) { });
                storeField.SetValue(null, failureStore);

                MethodInfo commit = FindMethodByName(runtime.GetType(), "Commit", 1);
                object commitResult = InvokeRuntimeMethod(commit, new object[] { failedDraft });
                bool commitFailed = commitResult is bool && !(bool)commitResult;
                bool appliedPreserved = SettingsEquivalent(runtime.Applied, beforeApplied);
                bool draftPreserved = SettingsEquivalent(GetDraftSettings(), failedDraft) && panel.IsOpen;
                string category = Convert.ToString(GetMemberValue(runtime, "ErrorCategory"), CultureInfo.InvariantCulture);

                Directory.Delete(blockedDestination, true);
                MethodInfo retry = FindMethodByName(runtime.GetType(), "Retry", 0);
                object retryResult = InvokeRuntimeMethod(retry, new object[0]);
                bool retrySucceeded = retryResult is bool && (bool)retryResult;
                bool appliedAfterRetry = SettingsEquivalent(runtime.Applied, failedDraft);
                detail = "commitFailed=" + commitFailed + ",appliedPreserved=" + appliedPreserved
                    + ",draftPreserved=" + draftPreserved + ",category=" + (category ?? "null")
                    + ",retry=" + retrySucceeded + ",appliedAfterRetry=" + appliedAfterRetry;
                return commitFailed && appliedPreserved && draftPreserved
                    && String.Equals(category, "configuration-write", StringComparison.Ordinal)
                    && retrySucceeded && appliedAfterRetry;
            }
            catch (Exception error)
            {
                detail = "save-failure recovery exception: " + DescribeException(error);
                RecordException("interaction.save-failure", error);
                return false;
            }
            finally
            {
                try
                {
                    if (storeField != null && originalStore != null) storeField.SetValue(null, originalStore);
                    if (Directory.Exists(failureRoot)) Directory.Delete(failureRoot, true);
                }
                catch (Exception error)
                {
                    RecordException("interaction.save-failure-cleanup", error);
                }
            }
        }

        bool HasPreset(string id)
        {
            foreach (object preset in GetPresetItems())
            {
                string value = Convert.ToString(GetMemberValue(preset, "Id"), CultureInfo.InvariantCulture);
                if (String.Equals(value, id, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        List<object> GetPresetItems()
        {
            List<object> result = new List<object>();
            object collection = GetMemberValue(runtime, "Presets");
            IEnumerable enumerable = GetMemberValue(collection, "Presets") as IEnumerable;
            if (enumerable == null) return result;
            foreach (object value in enumerable) if (value != null) result.Add(value);
            return result;
        }

        MethodInfo FindMethodByName(Type type, string name, int parameterCount)
        {
            if (type == null) return null;
            MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
                if (methods[i].Name == name && methods[i].GetParameters().Length == parameterCount) return methods[i];
            return null;
        }

        object InvokeRuntimeMethod(MethodInfo method, object[] values)
        {
            if (method == null) return null;
            ParameterInfo[] parameters = method.GetParameters();
            object[] arguments = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++) arguments[i] = CoerceArgument(values[i], parameters[i].ParameterType);
            return method.Invoke(runtime, arguments);
        }

        IEnumerator WaitForPanelClosed(float seconds)
        {
            float deadline = Time.realtimeSinceStartup + SecondsRemaining(seconds);
            Component modal = GetPanelSurface();
            while (!Expired() && Time.realtimeSinceStartup < deadline
                && panel != null && (panel.IsOpen || (modal != null && (modal.gameObject.activeInHierarchy || ModalIsShown(modal)))))
                yield return null;
        }

        string ReadButtonText(Button button)
        {
            if (button == null) return String.Empty;
            TMP_Text[] texts = button.GetComponentsInChildren<TMP_Text>(true);
            StringBuilder result = new StringBuilder();
            for (int i = 0; i < texts.Length; i++)
            {
                if (texts[i] == null || !texts[i].gameObject.activeInHierarchy) continue;
                if (result.Length > 0) result.Append("|");
                result.Append(texts[i].text ?? String.Empty);
            }
            return result.ToString();
        }

        IEnumerator EnsurePanelReady(ReadyAttempt result)
        {
            if (panel == null || runtime == null || !runtime.MenuActive)
            {
                result.Ready = false;
                yield break;
            }
            if (!panel.IsOpen) panel.Open();
            yield return StartCoroutine(WaitForPanelReady(result));
        }

        IEnumerator WaitForRuntimeSettled(float seconds)
        {
            float deadline = Time.realtimeSinceStartup + SecondsRemaining(seconds);
            while (!Expired() && Time.realtimeSinceStartup < deadline)
            {
                if (runtime != null && !runtime.IsBusy && !runtime.CentralTransitioning)
                {
                    yield return null;
                    yield return null;
                    yield break;
                }
                yield return null;
            }
        }

        bool InvokePanelMethod(string name, object[] values)
        {
            return panel != null && InvokeNamedMethod(panel, name, values);
        }

        bool InvokeNamedMethod(object target, string name, object[] values)
        {
            if (target == null || String.IsNullOrEmpty(name)) return false;
            MethodInfo[] methods = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name != name) continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != values.Length) continue;
                object[] arguments = new object[parameters.Length];
                try
                {
                    for (int p = 0; p < parameters.Length; p++)
                        arguments[p] = CoerceArgument(values[p], parameters[p].ParameterType);
                    method.Invoke(target, arguments);
                    return true;
                }
                catch (Exception error)
                {
                    RecordException("interaction.action." + name, error);
                }
            }
            return false;
        }

        object CoerceArgument(object value, Type targetType)
        {
            if (targetType == null) return value;
            if (value == null)
            {
                if (!targetType.IsValueType || Nullable.GetUnderlyingType(targetType) != null) return null;
                return Activator.CreateInstance(targetType);
            }
            Type nullable = Nullable.GetUnderlyingType(targetType);
            Type effective = nullable ?? targetType;
            if (effective.IsInstanceOfType(value)) return value;
            if (effective.IsEnum)
            {
                if (value is string) return Enum.Parse(effective, (string)value, true);
                return Enum.ToObject(effective, value);
            }
            if (effective == typeof(string)) return Convert.ToString(value, CultureInfo.InvariantCulture);
            if (effective == typeof(bool)) return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            if (effective == typeof(double)) return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (effective == typeof(float)) return Convert.ToSingle(value, CultureInfo.InvariantCulture);
            if (effective == typeof(decimal)) return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            if (effective == typeof(int)) return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (effective == typeof(long)) return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            return Convert.ChangeType(value, effective, CultureInfo.InvariantCulture);
        }

        bool PrepareCaptureCamera()
        {
            capturePoseValid = false;
            capturePoseDiagnostics = String.Empty;
            try
            {
                FirstPersonFlyingController controller = null;
                FirstPersonFlyingController[] controllers = Resources.FindObjectsOfTypeAll<FirstPersonFlyingController>();
                for (int i = 0; i < controllers.Length; i++)
                {
                    if (controllers[i] != null && controllers[i].gameObject.activeInHierarchy)
                    {
                        controller = controllers[i];
                        break;
                    }
                }
                if (controller == null) controller = controllers.FirstOrDefault(x => x != null);
                if (controller == null)
                {
                    RecordException("capture.camera", new MissingMethodException("FirstPersonFlyingController component was not found"));
                    return false;
                }

                // Resolve the concrete menu camera once and reuse this exact
                // instance for FPFC injection, viewport diagnostics, and the
                // URP render request.  The smoke test deliberately preserves
                // the game's real menu pose; it must never move the camera to
                // a designer-computed panel viewpoint.
                Camera camera = FindMenuCamera();
                if (camera == null) camera = GetFieldValue(controller, "_camera") as Camera;
                if (camera == null)
                {
                    RecordException("capture.camera", new MissingMethodException("menu Camera was not found"));
                    return false;
                }

                MethodInfo inject = controller.GetType().GetMethod("Inject", BindingFlags.Instance | BindingFlags.Public);
                ParameterInfo[] parameters = inject == null ? new ParameterInfo[0] : inject.GetParameters();
                bool injected = false;
                if (inject != null && parameters.Length == 6)
                {
                    object centerAdjust = GetFieldValue(controller, "_centerAdjust");
                    object controller0 = GetFieldValue(controller, "_controller0");
                    object controller1 = GetFieldValue(controller, "_controller1");
                    object trackedPoseDriver = GetFieldValue(controller, "_trackedPoseDriver");
                    if (centerAdjust == null)
                    {
                        VRCenterAdjust[] centers = Resources.FindObjectsOfTypeAll<VRCenterAdjust>();
                        centerAdjust = centers.FirstOrDefault(x => x != null && x.gameObject.activeInHierarchy)
                            ?? centers.FirstOrDefault(x => x != null);
                    }
                    if (controller0 == null || controller1 == null)
                    {
                        UnityEngine.Object[] vrObjects = Resources.FindObjectsOfTypeAll(parameters[2].ParameterType);
                        List<UnityEngine.Object> activeControllers = vrObjects.Where(x => x != null && ((Component)x).gameObject.activeInHierarchy).ToList();
                        if (controller0 == null && activeControllers.Count > 0) controller0 = activeControllers[0];
                        if (controller1 == null)
                        {
                            for (int i = 0; i < activeControllers.Count; i++)
                                if (!System.Object.ReferenceEquals(activeControllers[i], controller0)) { controller1 = activeControllers[i]; break; }
                        }
                    }
                    if (centerAdjust != null && controller0 != null && controller1 != null)
                    {
                        // Use the game's public injection API so the smoke
                        // checks observe the real menu camera and pose.
                        inject.Invoke(controller, new object[] {
                            camera, centerAdjust, controller0, controller1, trackedPoseDriver, true
                        });
                        if (!controller.enabled) controller.enabled = true;
                        injected = true;
                    }
                }
                if (!injected)
                {
                    RecordException("capture.camera", new InvalidOperationException("FirstPersonFlyingController.Inject dependencies were incomplete"));
                    return false;
                }
                captureCamera = camera;
                capturePose = CapturePose(camera);
                capturePoseValid = true;
                capturePoseDiagnostics = DescribePose("source", capturePose);
                return true;
            }
            catch (Exception error)
            {
                RecordException("capture.camera", error);
                return false;
            }
        }

        CameraPose CapturePose(Camera camera)
        {
            CameraPose pose = new CameraPose();
            if (camera == null) return pose;
            pose.Position = camera.transform.position;
            pose.Rotation = camera.transform.rotation;
            pose.Rect = camera.rect;
            pose.FieldOfView = camera.fieldOfView;
            pose.Aspect = camera.aspect;
            pose.NearClipPlane = camera.nearClipPlane;
            pose.FarClipPlane = camera.farClipPlane;
            pose.Orthographic = camera.orthographic;
            pose.OrthographicSize = camera.orthographicSize;
            pose.ProjectionMatrix = camera.projectionMatrix;
            return pose;
        }

        bool ApplyPose(Camera camera, CameraPose pose)
        {
            if (camera == null) return false;
            camera.transform.SetPositionAndRotation(pose.Position, pose.Rotation);
            camera.rect = pose.Rect;
            camera.fieldOfView = pose.FieldOfView;
            camera.aspect = pose.Aspect;
            camera.nearClipPlane = pose.NearClipPlane;
            camera.farClipPlane = pose.FarClipPlane;
            camera.orthographic = pose.Orthographic;
            camera.orthographicSize = pose.OrthographicSize;
            camera.projectionMatrix = pose.ProjectionMatrix;
            return true;
        }

        bool ApplyCapturePose(Camera camera)
        {
            return camera != null && capturePoseValid && ApplyPose(camera, capturePose);
        }

        string DescribePose(string label, CameraPose pose)
        {
            return label + "Pose={position=" + FormatVector(pose.Position)
                + ",rotation=" + FormatQuaternion(pose.Rotation)
                + ",rect=" + pose.Rect.ToString()
                + ",fov=" + pose.FieldOfView.ToString("0.####", CultureInfo.InvariantCulture)
                + ",aspect=" + pose.Aspect.ToString("0.####", CultureInfo.InvariantCulture)
                + ",near=" + pose.NearClipPlane.ToString("0.####", CultureInfo.InvariantCulture)
                + ",far=" + pose.FarClipPlane.ToString("0.####", CultureInfo.InvariantCulture)
                + ",orthographic=" + pose.Orthographic
                + ",orthographicSize=" + pose.OrthographicSize.ToString("0.####", CultureInfo.InvariantCulture) + "}";
        }

        string FormatQuaternion(Quaternion value)
        {
            return "(" + value.x.ToString("0.####", CultureInfo.InvariantCulture) + ","
                + value.y.ToString("0.####", CultureInfo.InvariantCulture) + ","
                + value.z.ToString("0.####", CultureInfo.InvariantCulture) + ","
                + value.w.ToString("0.####", CultureInfo.InvariantCulture) + ")";
        }

        Camera FindMenuCamera()
        {
            Camera[] cameras = Resources.FindObjectsOfTypeAll<Camera>();
            Camera best = Camera.main;
            int bestScore = best == null ? -1 : 10;
            for (int i = 0; i < cameras.Length; i++)
            {
                Camera candidate = cameras[i];
                if (candidate == null || !candidate.gameObject.activeInHierarchy) continue;
                string name = candidate.name ?? String.Empty;
                int score = name.IndexOf("MenuMainCamera", StringComparison.OrdinalIgnoreCase) >= 0 ? 100
                    : name.IndexOf("MainCamera", StringComparison.OrdinalIgnoreCase) >= 0 ? 50 : 1;
                if (score > bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }
            return best;
        }

        bool BuildVisualDiagnostics(out string detail)
        {
            detail = String.Empty;
            try
            {
                RectTransform centralRect = GetMemberValue(panel, "screenRect") as RectTransform;
                GameObject panelRoot = GetPanelRootObject();
                if (centralRect == null && panelRoot != null) centralRect = panelRoot.GetComponentInParent<RectTransform>();
                Component modal = GetPanelSurface();
                RectTransform modalRect = modal == null ? null : modal.GetComponent<RectTransform>();
                if (modalRect == null && modal != null) modalRect = modal.GetComponentInChildren<RectTransform>(true);
                Canvas originalCanvas = centralRect == null ? null : centralRect.GetComponentsInParent<Canvas>(true).FirstOrDefault();
                Canvas modalCanvas = modal == null ? null : modal.GetComponentsInChildren<Canvas>(true).FirstOrDefault();
                Button toolbarEntry = GetFieldValue(panel, "toolbarEntry") as Button;
                Button bottomEntry = GetFieldValue(panel, "bottomEntry") as Button;
                Button actualEntry = toolbarEntry != null && toolbarEntry.gameObject.activeInHierarchy ? toolbarEntry : bottomEntry;
                RectTransform toolbarRect = actualEntry == null ? null : actualEntry.GetComponent<RectTransform>();
                bool centralVisible;
                bool modalVisible;
                bool toolbarVisible;
                string centralDetail = DescribeViewportRect("central", centralRect, captureCamera, out centralVisible);
                string modalDetail = DescribeViewportRect("modal", modalRect, captureCamera, out modalVisible);
                string toolbarDetail = DescribeViewportRect("toolbar", toolbarRect, captureCamera, out toolbarVisible);
                visualPanelViewportValid = TryGetViewportBounds(modalRect, captureCamera, out visualPanelViewport);
                visualToolbarViewportValid = TryGetViewportBounds(toolbarRect, captureCamera, out visualToolbarViewport);
                string originalCanvasDetail = DescribeCanvasChain("original", centralRect, originalCanvas);
                string modalCanvasDetail = DescribeCanvas("modal", modalCanvas);
                string panelUiDetail = DescribePanelUi(panelRoot, modal, actualEntry);
                bool modalGeometry = ValidateModalGeometry(centralRect, modalRect);
                bool valid = captureCamera != null && panelRoot != null && panelRoot.activeInHierarchy
                    && modal != null && modal.gameObject.activeInHierarchy && modalRect != null
                    && modalGeometry && centralVisible && modalVisible
                    && visualPanelViewportValid;
                detail = originalCanvasDetail + "; " + modalCanvasDetail + "; "
                    + centralDetail + "; " + modalDetail + "; " + toolbarDetail
                    + "; " + panelUiDetail
                    + "; " + DescribeCamera(captureCamera)
                    + "; modalGeometry=" + modalGeometry
                    + "; toolbarVisible=" + toolbarVisible
                    + "; panelRootActive=" + (panelRoot != null && panelRoot.activeInHierarchy);
                return valid;
            }
            catch (Exception error)
            {
                detail = "visual diagnostics exception: " + DescribeException(error);
                RecordException("capture.ui-viewport", error);
                return false;
            }
        }

        string DescribePanelUi(GameObject panelRoot, Component floating, Button toolbarEntry)
        {
            if (panelRoot == null || floating == null) return "panelUi={rootOrModal=null}";
            RectTransform rootRect = panelRoot.GetComponent<RectTransform>();
            RectTransform columnRect = panelRoot.GetComponentsInChildren<RectTransform>(true)
                .FirstOrDefault(x => x != null && x.name.IndexOf("panelColumn", StringComparison.OrdinalIgnoreCase) >= 0);
            if (columnRect == null) columnRect = rootRect;
            Graphic[] graphics = floating.GetComponentsInChildren<Graphic>(true);
            int activeGraphics = 0;
            int culledGraphics = 0;
            float minimumAlpha = 1f;
            float maximumAlpha = 0f;
            for (int i = 0; i < graphics.Length; i++)
            {
                if (graphics[i] == null) continue;
                if (graphics[i].gameObject.activeInHierarchy) activeGraphics++;
                if (graphics[i].canvasRenderer.cull) culledGraphics++;
                float alpha = graphics[i].canvasRenderer.GetColor().a;
                minimumAlpha = Mathf.Min(minimumAlpha, alpha);
                maximumAlpha = Mathf.Max(maximumAlpha, alpha);
            }
            CanvasGroup rootGroup = floating.GetComponentsInChildren<CanvasGroup>(true).FirstOrDefault();
            Button childButton = toolbarEntry == null ? null : toolbarEntry.GetComponentsInChildren<Button>(true).FirstOrDefault(x => x != toolbarEntry);
            RectTransform childRect = childButton == null ? null : childButton.GetComponent<RectTransform>();
            string groups = rootGroup == null ? "none" : rootGroup.alpha.ToString("0.###", CultureInfo.InvariantCulture);
            return "panelUi={panelRoot=" + DescribeRectDimensions(rootRect)
                + ",panelColumn=" + DescribeRectDimensions(columnRect)
                + ",modalGraphics=" + graphics.Length.ToString(CultureInfo.InvariantCulture)
                + ",activeGraphics=" + activeGraphics.ToString(CultureInfo.InvariantCulture)
                + ",culledGraphics=" + culledGraphics.ToString(CultureInfo.InvariantCulture)
                + ",canvasRendererAlpha=" + minimumAlpha.ToString("0.###", CultureInfo.InvariantCulture)
                + ".." + maximumAlpha.ToString("0.###", CultureInfo.InvariantCulture)
                + ",rootCanvasGroupAlpha=" + groups
                + ",toolbarEntryButton=" + DescribeRectDimensions(childRect)
                + ",layoutRects=" + DescribeNamedPanelRects(panelRoot) + "}";
        }

        string DescribeNamedPanelRects(GameObject panelRoot)
        {
            if (panelRoot == null) return "null";
            string[] labels = {
                "panelColumn", "header", "scope", "status", "scrollContainer", "viewport",
                "contentWrapper", "body", "mainPage", "npsRow", "footer"
            };
            string[][] tokens = {
                new string[] { "panelcolumn" },
                new string[] { "header" },
                new string[] { "scope" },
                new string[] { "status" },
                new string[] { "scrollcontainer", "scrollablecontainer" },
                new string[] { "viewport" },
                new string[] { "contentwrapper" },
                new string[] { "body" },
                new string[] { "mainpage", "main" },
                new string[] { "npsrow", "nps" },
                new string[] { "footer" }
            };
            StringBuilder result = new StringBuilder("{");
            for (int i = 0; i < labels.Length; i++)
            {
                if (i > 0) result.Append(";");
                RectTransform bound = GetBoundPanelRect(i);
                RectTransform fallback = bound == null ? FindPanelRect(panelRoot, tokens[i]) : null;
                result.Append(labels[i]).Append("=")
                    .Append(DescribeLayoutRect(bound ?? fallback));
            }
            return result.Append("}").ToString();
        }

        RectTransform GetBoundPanelRect(int index)
        {
            // Keep this mapping aligned with Panel's actual BSML [UIObject]
            // fields.  Name matching is only a fallback because BSML may
            // leave generic names such as "BSMLText" on instantiated nodes.
            string fieldName = null;
            switch (index)
            {
                case 0: fieldName = "boundPanelColumn"; break;
                case 1: fieldName = "boundHeader"; break;
                case 2: fieldName = "boundSelectionSection"; break;
                case 3: fieldName = "boundStatusText"; break;
                case 4: fieldName = "boundScrollContainer"; break;
                case 6:
                    var scrollRoot = GetFieldValue(panel, "boundScrollContainer") as GameObject;
                    if (scrollRoot == null) return null;
                    foreach (var item in scrollRoot.GetComponents<Component>())
                        if (item != null && item.GetType().Name == "BSMLScrollableContainer")
                            return GetMemberValue(item, "ContentRect") as RectTransform;
                    return null;
                case 7: fieldName = "boundBody"; break;
                case 8: fieldName = "boundMainPage"; break;
                case 9: fieldName = "boundNpsRow"; break;
                case 10: fieldName = "boundFooter"; break;
            }
            if (String.IsNullOrEmpty(fieldName)) return null;
            object value = GetFieldValue(panel, fieldName);
            GameObject gameObject = value as GameObject;
            if (gameObject != null) return gameObject.GetComponent<RectTransform>();
            Component component = value as Component;
            if (component != null)
            {
                if (component is TMP_Text) return component.gameObject.GetComponent<RectTransform>();
                return component.GetComponent<RectTransform>();
            }
            return null;
        }

        RectTransform FindPanelRect(GameObject panelRoot, string[] tokens)
        {
            if (panelRoot == null || tokens == null || tokens.Length == 0) return null;
            RectTransform[] rects = panelRoot.GetComponentsInChildren<RectTransform>(true);
            // Prefer an exact object name so a nested label such as
            // "StatusText" does not hide the actual status row/container.
            for (int pass = 0; pass < 3; pass++)
            {
                for (int i = 0; i < rects.Length; i++)
                {
                    RectTransform rect = rects[i];
                    if (rect == null) continue;
                    string name = rect.name.ToLowerInvariant();
                    string path = GetTransformPath(rect).ToLowerInvariant();
                    bool match = false;
                    for (int tokenIndex = 0; tokenIndex < tokens.Length; tokenIndex++)
                    {
                        string token = tokens[tokenIndex];
                        if (pass == 0) match = match || name == token;
                        else if (pass == 1) match = match || name.IndexOf(token, StringComparison.Ordinal) >= 0;
                        else match = match || path.IndexOf(token, StringComparison.Ordinal) >= 0;
                    }
                    if (match) return rect;
                }
            }
            return null;
        }

        string DescribeLayoutRect(RectTransform rect)
        {
            if (rect == null) return "null";
            try
            {
                float worldWidth = ProjectedSpan(rect, rect.right.normalized);
                float worldHeight = ProjectedSpan(rect, rect.up.normalized);
                LayoutElement layout = rect.GetComponent<LayoutElement>();
                ContentSizeFitter fitter = rect.GetComponent<ContentSizeFitter>();
                CanvasGroup[] groups = rect.GetComponentsInParent<CanvasGroup>(true);
                float alpha = 1f;
                bool hasGroup = false;
                for (int i = 0; i < groups.Length; i++)
                {
                    if (groups[i] == null) continue;
                    hasGroup = true;
                    alpha *= groups[i].alpha;
                }
                string layoutDetail = layout == null ? "none" : "min="
                    + layout.minWidth.ToString("0.###", CultureInfo.InvariantCulture) + "x"
                    + layout.minHeight.ToString("0.###", CultureInfo.InvariantCulture)
                    + ",preferred=" + layout.preferredWidth.ToString("0.###", CultureInfo.InvariantCulture)
                    + "x" + layout.preferredHeight.ToString("0.###", CultureInfo.InvariantCulture)
                    + ",flex=" + layout.flexibleWidth.ToString("0.###", CultureInfo.InvariantCulture)
                    + "x" + layout.flexibleHeight.ToString("0.###", CultureInfo.InvariantCulture);
                string fitterDetail = fitter == null ? "none" : fitter.horizontalFit + "/" + fitter.verticalFit;
                Rect local = rect.rect;
                return "{name=" + rect.name + ",active=" + rect.gameObject.activeInHierarchy
                    + ",local=" + FormatRect(local)
                    + ",world=" + worldWidth.ToString("0.####", CultureInfo.InvariantCulture) + "x"
                    + worldHeight.ToString("0.####", CultureInfo.InvariantCulture)
                    + ",layout={" + layoutDetail + "},fitter=" + fitterDetail
                    + ",canvasGroupAlpha=" + (hasGroup ? alpha.ToString("0.###", CultureInfo.InvariantCulture) : "none") + "}";
            }
            catch (Exception error)
            {
                return "{exception=" + DescribeException(error) + "}";
            }
        }

        string DescribeRectDimensions(RectTransform rect)
        {
            if (rect == null) return "null";
            float width = ProjectedSpan(rect, rect.right.normalized);
            float height = ProjectedSpan(rect, rect.up.normalized);
            return "{" + width.ToString("0.####", CultureInfo.InvariantCulture) + "x"
                + height.ToString("0.####", CultureInfo.InvariantCulture)
                + ",active=" + rect.gameObject.activeInHierarchy + "}";
        }

        string DescribeCanvas(string label, Canvas canvas)
        {
            if (canvas == null) return label + "Canvas=null";
            string cameraName = canvas.worldCamera == null ? "null" : canvas.worldCamera.name;
            return label + "Canvas={renderMode=" + canvas.renderMode
                + ",layer=" + canvas.gameObject.layer.ToString(CultureInfo.InvariantCulture)
                + ",worldCamera=" + cameraName + ",active=" + canvas.gameObject.activeInHierarchy + "}";
        }

        string DescribeCanvasChain(string label, RectTransform rect, Canvas nearest)
        {
            if (rect == null) return label + "CanvasChain=null";
            Canvas[] canvases = rect.GetComponentsInParent<Canvas>(true);
            StringBuilder result = new StringBuilder(DescribeCanvas(label, nearest)).Append(",chain=[");
            for (int i = 0; i < canvases.Length; i++)
            {
                if (i > 0) result.Append(";");
                Canvas canvas = canvases[i];
                result.Append(canvas == null ? "null" : canvas.gameObject.name)
                    .Append("/path=").Append(canvas == null ? "" : GetTransformPath(canvas.transform))
                    .Append("/mode=").Append(canvas == null ? "" : canvas.renderMode.ToString())
                    .Append("/layer=").Append(canvas == null ? "" : canvas.gameObject.layer.ToString(CultureInfo.InvariantCulture))
                    .Append("/camera=").Append(canvas == null || canvas.worldCamera == null ? "null" : canvas.worldCamera.name);
            }
            return result.Append("]").ToString();
        }

        string GetTransformPath(Transform value)
        {
            if (value == null) return "";
            List<string> names = new List<string>();
            Transform current = value;
            while (current != null)
            {
                names.Add(current.name);
                current = current.parent;
            }
            names.Reverse();
            return String.Join("/", names.ToArray());
        }

        string DescribeCamera(Camera camera)
        {
            if (camera == null) return "camera=null";
            return "camera={name=" + camera.name + ",position=" + FormatVector(camera.transform.position)
                + ",forward=" + FormatVector(camera.transform.forward)
                + ",aspect=" + camera.aspect.ToString("0.####", CultureInfo.InvariantCulture)
                + ",fov=" + camera.fieldOfView.ToString("0.####", CultureInfo.InvariantCulture)
                + ",targetTexture=" + (camera.targetTexture == null ? "null" : camera.targetTexture.name) + "}";
        }

        bool TryGetViewportBounds(RectTransform rect, Camera camera, out Rect bounds)
        {
            bounds = new Rect();
            if (rect == null || camera == null) return false;
            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            float minX = float.MaxValue;
            float maxX = float.MinValue;
            float minY = float.MaxValue;
            float maxY = float.MinValue;
            for (int i = 0; i < corners.Length; i++)
            {
                Vector3 point = camera.WorldToViewportPoint(corners[i]);
                if (point.z <= 0f) return false;
                minX = Mathf.Min(minX, point.x);
                maxX = Mathf.Max(maxX, point.x);
                minY = Mathf.Min(minY, point.y);
                maxY = Mathf.Max(maxY, point.y);
            }
            bounds = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return true;
        }

        string DescribeViewportRect(string label, RectTransform rect, Camera camera, out bool visible)
        {
            visible = false;
            if (rect == null) return label + "Rect=null";
            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            float width = ProjectedSpan(corners, rect.right.normalized);
            float height = ProjectedSpan(corners, rect.up.normalized);
            StringBuilder result = new StringBuilder();
            result.Append(label).Append("Rect={world=")
                .Append(width.ToString("0.####", CultureInfo.InvariantCulture)).Append("x")
                .Append(height.ToString("0.####", CultureInfo.InvariantCulture)).Append(",corners=");
            int inFront = 0;
            float minX = float.MaxValue;
            float maxX = float.MinValue;
            float minY = float.MaxValue;
            float maxY = float.MinValue;
            bool allInside = true;
            result.Append("[");
            for (int i = 0; i < corners.Length; i++)
            {
                if (i > 0) result.Append(",");
                result.Append(FormatVector(corners[i]));
                if (camera != null)
                {
                    Vector3 viewport = camera.WorldToViewportPoint(corners[i]);
                    if (viewport.z > 0f) inFront++;
                    if (viewport.z <= 0f || viewport.x < 0f || viewport.x > 1f || viewport.y < 0f || viewport.y > 1f)
                        allInside = false;
                    minX = Mathf.Min(minX, viewport.x);
                    maxX = Mathf.Max(maxX, viewport.x);
                    minY = Mathf.Min(minY, viewport.y);
                    maxY = Mathf.Max(maxY, viewport.y);
                }
            }
            result.Append("],viewport=")
                .Append("[").Append(minX.ToString("0.###", CultureInfo.InvariantCulture)).Append(",")
                .Append(maxX.ToString("0.###", CultureInfo.InvariantCulture)).Append(",")
                .Append(minY.ToString("0.###", CultureInfo.InvariantCulture)).Append(",")
                .Append(maxY.ToString("0.###", CultureInfo.InvariantCulture)).Append("]");
            visible = camera != null && inFront == corners.Length && allInside;
            result.Append(",visible=" + visible + "}");
            return result.ToString();
        }

        string FormatVector(Vector3 value)
        {
            return "(" + value.x.ToString("0.####", CultureInfo.InvariantCulture) + ","
                + value.y.ToString("0.####", CultureInfo.InvariantCulture) + ","
                + value.z.ToString("0.####", CultureInfo.InvariantCulture) + ")";
        }

        IEnumerator CaptureAndWait(string path, CaptureAttempt result)
        {
            result.Completed = false;
            result.Attempts = 0;
            string offscreenDetail = String.Empty;
            for (int attempt = 1; attempt <= 3 && !Expired(); attempt++)
            {
                result.Attempts = attempt;
                bool posed = ApplyCapturePose(captureCamera);
                if (!posed)
                {
                    result.Detail = "saved capture camera pose was unavailable before URP attempt="
                        + attempt.ToString(CultureInfo.InvariantCulture);
                    continue;
                }
                yield return new WaitForEndOfFrame();
                bool offscreen = TryCaptureUrpOffscreen(path, out offscreenDetail);
                result.Detail = "URP main attempt=" + attempt.ToString(CultureInfo.InvariantCulture)
                    + ", " + offscreenDetail + "; " + capturePoseDiagnostics;
                result.VisualPassed = result.VisualPassed || offscreen;
                if (offscreen)
                {
                    result.Completed = true;
                    yield break;
                }
            }
        }

        bool TryCaptureUrpOffscreen(string path, out string detail)
        {
            detail = String.Empty;
            Camera camera = captureCamera;
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture previousTarget = camera == null ? null : camera.targetTexture;
            bool previousEnabled = camera != null && camera.enabled;
            RenderTexture target = null;
            Texture2D texture = null;
            try
            {
                if (camera == null)
                {
                    detail = "camera unavailable";
                    return false;
                }
                target = new RenderTexture(CaptureWidth, CaptureHeight, 24, RenderTextureFormat.ARGB32);
                target.name = "AdvancedSongFiltersSmoke.OffscreenTarget";
                target.Create();
                camera.targetTexture = target;
                camera.enabled = true;
                ApplyCapturePose(camera);
                // Render the real menu camera at its current pose.  This is
                // the same native canvas path used by the player view.
                RenderPipeline.StandardRequest request = new RenderPipeline.StandardRequest();
                request.destination = target;
                request.mipLevel = 0;
                request.face = CubemapFace.Unknown;
                request.slice = 0;
                if (!RenderPipeline.SupportsRenderRequest<RenderPipeline.StandardRequest>(camera, request))
                {
                    detail = "URP SupportsRenderRequest=false";
                    RecordException("screenshot.offscreen.unsupported", new NotSupportedException(detail));
                    return false;
                }
                RenderPipeline.SubmitRenderRequest<RenderPipeline.StandardRequest>(camera, request);
                RenderTexture.active = target;
                texture = new Texture2D(CaptureWidth, CaptureHeight, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0f, 0f, CaptureWidth, CaptureHeight), 0, 0, false);
                texture.Apply(false, false);
                Color32[] pixels = texture.GetPixels32();
                for (int i = 0; i < pixels.Length; i++) pixels[i].a = 255;
                texture.SetPixels32(pixels);
                texture.Apply(false, false);
                string pixelDetail;
                bool visual = ValidatePixels(pixels, out pixelDetail);
                byte[] png = texture.EncodeToPNG();
                File.WriteAllBytes(path, png);
                detail = "URP submitted " + CaptureWidth.ToString(CultureInfo.InvariantCulture) + "x"
                    + CaptureHeight.ToString(CultureInfo.InvariantCulture) + "x24, " + pixelDetail
                    + "; " + capturePoseDiagnostics;
                return visual;
            }
            catch (Exception error)
            {
                detail = "URP exception: " + DescribeException(error);
                RecordException("screenshot.offscreen", error);
                return false;
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (camera != null)
                {
                    camera.targetTexture = previousTarget;
                    camera.enabled = previousEnabled;
                }
                if (texture != null) UnityEngine.Object.Destroy(texture);
                if (target != null)
                {
                    target.Release();
                    UnityEngine.Object.Destroy(target);
                }
            }
        }

        bool ValidatePixels(Color32[] pixels, out string detail)
        {
            detail = String.Empty;
            try
            {
                int stride = Math.Max(1, pixels.Length / 4096);
                int sampled = 0;
                int nonBlack = 0;
                double sum = 0d;
                double sumSquares = 0d;
                for (int i = 0; i < pixels.Length; i += stride)
                {
                    float luminance = (pixels[i].r + pixels[i].g + pixels[i].b) / 3f;
                    sampled++;
                    sum += luminance;
                    sumSquares += luminance * luminance;
                    // Alpha may be zero in URP's offscreen color target even
                    // when the RGB image is visibly rendered.  Visibility is
                    // therefore determined from RGB only.
                    if (luminance > 8f) nonBlack++;
                }
                double mean = sampled == 0 ? 0d : sum / sampled;
                double variance = sampled == 0 ? 0d : Math.Max(0d, sumSquares / sampled - mean * mean);
                detail = "samples=" + sampled.ToString(CultureInfo.InvariantCulture)
                    + ", nonBlack=" + nonBlack.ToString(CultureInfo.InvariantCulture)
                    + ", mean=" + mean.ToString("0.0", CultureInfo.InvariantCulture)
                    + ", variance=" + variance.ToString("0.0", CultureInfo.InvariantCulture);
                return sampled > 0 && nonBlack >= Math.Max(8, sampled / 100)
                    && (mean >= 8d || variance >= 16d);
            }
            catch (Exception error)
            {
                detail = "visual pixel exception: " + DescribeException(error);
                return false;
            }
        }

        bool CompareScreenshotRegion(string beforePath, string afterPath, out string detail)
        {
            return CompareScreenshotRegionWithThreshold(beforePath, afterPath,
                visualPanelViewport, visualPanelViewportValid, 0.01, 4d, out detail);
        }

        bool ValidateScreenshotLandmarks(string beforePath, string afterPath, out string detail)
        {
            string panelDetail;
            bool panel = CompareScreenshotRegionWithThreshold(beforePath, afterPath,
                visualPanelViewport, visualPanelViewportValid, 0.05, 6d, out panelDetail);
            detail = "native-modal=" + panel + " (" + panelDetail + ")"
                + "; toolbarViewport=" + (visualToolbarViewportValid ? visualToolbarViewport.ToString() : "unavailable");
            return panel;
        }

        bool CompareScreenshotRegionWithThreshold(string beforePath, string afterPath,
            Rect region, bool regionValid, double minimumChangedFraction,
            double minimumMeanDifference, out string detail)
        {
            detail = String.Empty;
            Texture2D before = null;
            Texture2D after = null;
            try
            {
                if (!regionValid)
                {
                    detail = "viewport landmark is invalid";
                    return false;
                }
                before = LoadScreenshotTexture(beforePath);
                after = LoadScreenshotTexture(afterPath);
                if (before == null || after == null || before.width != after.width || before.height != after.height)
                {
                    detail = "landmark decode or dimensions failed";
                    return false;
                }
                int left = Mathf.Clamp(Mathf.FloorToInt(region.xMin * before.width), 0, before.width - 1);
                int right = Mathf.Clamp(Mathf.CeilToInt(region.xMax * before.width), left + 1, before.width);
                int bottom = Mathf.Clamp(Mathf.FloorToInt(region.yMin * before.height), 0, before.height - 1);
                int top = Mathf.Clamp(Mathf.CeilToInt(region.yMax * before.height), bottom + 1, before.height);
                Color32[] beforePixels = before.GetPixels32();
                Color32[] afterPixels = after.GetPixels32();
                int samples = 0;
                int changed = 0;
                double totalDifference = 0d;
                int stride = Math.Max(1, Math.Min(before.width, before.height) / 360);
                for (int y = bottom; y < top; y += stride)
                {
                    for (int x = left; x < right; x += stride)
                    {
                        int index = y * before.width + x;
                        int difference = Math.Abs(beforePixels[index].r - afterPixels[index].r)
                            + Math.Abs(beforePixels[index].g - afterPixels[index].g)
                            + Math.Abs(beforePixels[index].b - afterPixels[index].b);
                        samples++;
                        totalDifference += difference;
                        if (difference >= 30) changed++;
                    }
                }
                double meanDifference = samples == 0 ? 0d : totalDifference / samples;
                double changedFraction = samples == 0 ? 0d : (double)changed / samples;
                detail = "region=[" + region.xMin.ToString("0.###", CultureInfo.InvariantCulture)
                    + "," + region.yMin.ToString("0.###", CultureInfo.InvariantCulture)
                    + "," + region.width.ToString("0.###", CultureInfo.InvariantCulture)
                    + "," + region.height.ToString("0.###", CultureInfo.InvariantCulture)
                    + "],samples=" + samples.ToString(CultureInfo.InvariantCulture)
                    + ",changed=" + changed.ToString(CultureInfo.InvariantCulture)
                    + ",changedFraction=" + changedFraction.ToString("0.####", CultureInfo.InvariantCulture)
                    + ",meanDifference=" + meanDifference.ToString("0.0", CultureInfo.InvariantCulture);
                return samples > 0 && changedFraction >= minimumChangedFraction
                    && meanDifference >= minimumMeanDifference;
            }
            catch (Exception error)
            {
                detail = "landmark diff exception: " + DescribeException(error);
                RecordException("screenshot.panel-diff", error);
                return false;
            }
            finally
            {
                if (before != null) UnityEngine.Object.Destroy(before);
                if (after != null) UnityEngine.Object.Destroy(after);
            }
        }

        Texture2D LoadScreenshotTexture(string path)
        {
            if (!File.Exists(path)) return null;
            byte[] bytes = File.ReadAllBytes(path);
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            MethodInfo loadImage = typeof(ImageConversion).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(x => x.Name == "LoadImage" && x.GetParameters().Length == 3
                    && x.GetParameters()[1].ParameterType == typeof(byte[]));
            if (loadImage == null)
            {
                UnityEngine.Object.Destroy(texture);
                throw new MissingMethodException("UnityEngine.ImageConversion.LoadImage(byte[])");
            }
            bool loaded = (bool)loadImage.Invoke(null, new object[] { texture, bytes, false });
            if (!loaded)
            {
                UnityEngine.Object.Destroy(texture);
                return null;
            }
            return texture;
        }

        IEnumerator RunParserTests(string root)
        {
            string[] labels = { "v2", "v3", "v4" };
            string[] fixtures = { Version2Fixture(), Version3Fixture(), Version4Fixture() };
            int passed = 0;
            int failed = 0;
            for (int i = 0; i < fixtures.Length; i++)
            {
                ParserAttempt attempt = new ParserAttempt();
                yield return StartCoroutine(LoadAndCheckParser(labels[i] + " fixture", fixtures[i], attempt));
                if (attempt.Completed && attempt.Error == null)
                {
                    passed++;
                    AddCheck("parser." + labels[i], true, DescribeCounts(attempt.Actual));
                }
                else
                {
                    failed++;
                    if (attempt.Error != null) RecordException("parser." + labels[i], attempt.Error);
                    AddCheck("parser." + labels[i], false, attempt.Error == null ? "parser task did not complete" : DescribeException(attempt.Error));
                }
            }

            List<ParserSample> samples = BuildParserSamples();
            if (samples.Count == 0)
            {
                failed++;
                AddCheck("parser.samples", false, "no loaded custom BeatmapLevel with a Standard beatmap and IBeatmapLevelData was available");
            }
            else
            {
                bool sampleSetValid = samples.Count == 2;
                if (!sampleSetValid)
                {
                    failed++;
                    AddCheck("parser.samples", false, "expected two different loaded custom songs, got " + samples.Count.ToString(CultureInfo.InvariantCulture));
                }
                else AddCheck("parser.samples", true, "two different loaded custom songs with Standard beatmaps");
                for (int i = 0; i < samples.Count; i++)
                {
                    ParserAttempt attempt = new ParserAttempt();
                    string label = "sample " + samples[i].Level.levelID;
                    yield return StartCoroutine(LoadAndCheckParserData(label, samples[i], attempt));
                    if (attempt.Completed && attempt.Error == null)
                    {
                        passed++;
                        AddCheck("parser.sample." + i.ToString(CultureInfo.InvariantCulture), true, label + ": " + DescribeCounts(attempt.Actual));
                    }
                    else
                    {
                        failed++;
                        if (attempt.Error != null) RecordException("parser.sample." + i.ToString(CultureInfo.InvariantCulture), attempt.Error);
                        AddCheck("parser.sample." + i.ToString(CultureInfo.InvariantCulture), false, attempt.Error == null ? "parser task did not complete" : DescribeException(attempt.Error));
                    }
                }
            }
            parserPassed = failed == 0 && passed == fixtures.Length + samples.Count;
            AddCheck("parser.summary", parserPassed, "pass=" + passed + ", fail=" + failed + ", sampleLimit=" + samples.Count);
        }

        IEnumerator LoadAndCheckParser(string label, string json, ParserAttempt result)
        {
            result.Actual = new Counts();
            result.Error = null;
            result.Completed = false;
            Task<Counts> task = null;
            Exception startError = null;
            try
            {
                // BeatmapDataLoader's Task can capture Unity's synchronization
                // context.  The parser fixtures are pure JSON, so run the same
                // synchronous .Result call used by IntegrationTests off the
                // Unity main thread and only poll its completion from here.
                task = Task.Run(delegate { return LoadBasicInfo(json); });
            }
            catch (Exception caughtStartError)
            {
                startError = caughtStartError;
            }
            if (startError != null)
            {
                result.Error = startError;
                yield break;
            }
            while (!Expired() && task != null && !task.IsCompleted) yield return null;
            if (task == null || !task.IsCompleted)
            {
                result.Error = new TimeoutException(label + " parser task exceeded the total runtime limit");
                yield break;
            }
            try
            {
                if (task.IsFaulted)
                {
                    Exception taskError = task.Exception == null
                        ? (Exception)new InvalidOperationException("parser task faulted")
                        : task.Exception;
                    throw taskError;
                }
                result.Actual = task.Result;
                AssertCounts(result.Actual, ExpectedFromJson(json), label);
                result.Completed = true;
            }
            catch (Exception parseError)
            {
                result.Error = parseError;
            }
        }

        List<ParserSample> BuildParserSamples()
        {
            List<ParserSample> result = new List<ParserSample>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List<BeatmapLevel> levels = GetAdapterLevels("Input");
            if (SongCore.Loader.CustomLevels != null)
            {
                foreach (BeatmapLevel custom in SongCore.Loader.CustomLevels.Values)
                    if (custom != null && !levels.Any(x => x != null && x.levelID == custom.levelID)) levels.Add(custom);
            }
            for (int i = 0; i < levels.Count && result.Count < 2; i++)
            {
                BeatmapLevel level = levels[i];
                if (level == null || String.IsNullOrEmpty(level.levelID)
                    || !level.levelID.StartsWith("custom_level_", StringComparison.Ordinal)
                    || !seen.Add(level.levelID)) continue;
                BeatmapKey selectedKey = default(BeatmapKey);
                bool foundStandard = false;
                foreach (BeatmapKey key in level.GetBeatmapKeys())
                {
                    if (String.Equals(key.characteristic.ToString(), "Standard", StringComparison.OrdinalIgnoreCase))
                    {
                        selectedKey = key;
                        foundStandard = true;
                        break;
                    }
                }
                if (!foundStandard) continue;
                try
                {
                    IBeatmapLevelData data = SongCore.Loader.CustomLevelLoader.LoadBeatmapLevelData(level);
                    if (data != null) result.Add(new ParserSample { Level = level, Key = selectedKey, Data = data });
                }
                catch (Exception error)
                {
                    RecordException("parser.sample-data." + level.levelID, error);
                }
            }
            return result;
        }

        IEnumerator LoadAndCheckParserData(string label, ParserSample sample, ParserAttempt result)
        {
            result.Actual = new Counts();
            result.Error = null;
            result.Completed = false;
            Task<ParserAttempt> parserTask = null;
            Exception startError = null;
            try
            {
                // Start on Unity's menu thread so both data-model async calls
                // capture the Unity synchronization context.  The coroutine
                // only polls this task; it never blocks the main thread.
                parserTask = LoadDataParserAsync(label, sample);
            }
            catch (Exception caughtStartError)
            {
                startError = caughtStartError;
            }
            if (startError != null)
            {
                result.Error = startError;
                yield break;
            }
            while (!Expired() && parserTask != null && !parserTask.IsCompleted) yield return null;
            if (parserTask == null || !parserTask.IsCompleted)
            {
                result.Error = new TimeoutException(label + " data parser task exceeded the total runtime limit");
                yield break;
            }
            try
            {
                if (parserTask.IsFaulted)
                {
                    Exception taskError = parserTask.Exception == null
                        ? (Exception)new InvalidOperationException("data parser task faulted")
                        : parserTask.Exception;
                    throw taskError;
                }
                ParserAttempt completed = parserTask.Result;
                result.Actual = completed.Actual;
                result.Error = completed.Error;
                result.Completed = completed.Completed;
            }
            catch (Exception parseError)
            {
                result.Error = parseError;
            }
        }

        async Task<ParserAttempt> LoadDataParserAsync(string label, ParserSample sample)
        {
            ParserAttempt result = new ParserAttempt();
            try
            {
                if (sample == null || sample.Data == null) throw new ArgumentNullException("sample.Data");
                BeatmapKey key = sample.Key;
                // The game interface metadata is a by-ref BeatmapKey method.
                // The Unity 1.45 Mono compiler reports CS0570 for that
                // metadata when called directly, so invoke that exact method
                // reflectively, then await its Task on Unity's context.
                string rawJson = await GetBeatmapStringTask(sample.Data, key);
                if (String.IsNullOrEmpty(rawJson)) throw new InvalidDataException("raw beatmap string is empty");
                BeatmapDataLoader loader = new BeatmapDataLoader(null, null);
                BeatmapDataBasicInfo info = await loader.LoadBasicBeatmapDataAsync(sample.Data, key);
                result.Actual = new Counts
                {
                    NumberOfLines = info.numberOfLines,
                    CuttableNotesCount = info.cuttableNotesCount,
                    CuttableScoringObjectsCount = info.cuttableScoringObjectsCount,
                    ObstaclesCount = info.obstaclesCount,
                    BombsCount = info.bombsCount
                };
                AssertCounts(result.Actual, ExpectedFromJson(rawJson), label);
                result.Completed = true;
            }
            catch (Exception error)
            {
                result.Error = error;
            }
            return result;
        }

        Task<string> GetBeatmapStringTask(IBeatmapLevelData data, BeatmapKey key)
        {
            if (data == null) throw new ArgumentNullException("data");
            MethodInfo method = null;
            MethodInfo[] methods = data.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo candidate = methods[i];
                if ((candidate.Name == "GetBeatmapStringAsync" || candidate.Name.EndsWith(".GetBeatmapStringAsync", StringComparison.Ordinal))
                    && candidate.GetParameters().Length == 1)
                {
                    method = candidate;
                    break;
                }
            }
            if (method == null)
            {
                Type[] interfaces = data.GetType().GetInterfaces();
                for (int i = 0; i < interfaces.Length && method == null; i++)
                {
                    MethodInfo candidate = interfaces[i].GetMethod("GetBeatmapStringAsync", BindingFlags.Instance | BindingFlags.Public);
                    if (candidate != null && candidate.GetParameters().Length == 1) method = candidate;
                }
            }
            if (method == null) throw new MissingMethodException(data.GetType().FullName, "GetBeatmapStringAsync");
            object value = method.Invoke(data, new object[] { key });
            Task<string> task = value as Task<string>;
            if (task == null) throw new InvalidOperationException("IBeatmapLevelData.GetBeatmapStringAsync did not return Task<string>");
            return task;
        }

        Counts LoadBasicInfo(string json)
        {
            BeatmapDataLoader loader = new BeatmapDataLoader(null, null);
            BeatmapDataBasicInfo info = loader.LoadBasicBeatmapDataAsync(json).Result;
            return new Counts
            {
                NumberOfLines = info.numberOfLines,
                CuttableNotesCount = info.cuttableNotesCount,
                CuttableScoringObjectsCount = info.cuttableScoringObjectsCount,
                ObstaclesCount = info.obstaclesCount,
                BombsCount = info.bombsCount
            };
        }

        ExpectedCounts ExpectedFromJson(string json)
        {
            JObject root = JObject.Parse(json);
            JToken versionToken = root["_version"] ?? root["version"];
            if (versionToken == null) throw new InvalidDataException("Beatmap fixture has no version field");
            Version version = new Version(versionToken.Value<string>());
            ExpectedCounts result = new ExpectedCounts { NumberOfLines = 4 };
            if (version.Major < 3)
            {
                JArray notes = JsonArray(root, "_notes");
                for (int i = 0; i < notes.Count; i++)
                {
                    if (Integer(notes[i], "_type") == 3) result.BombsCount++;
                    else { result.CuttableNotesCount++; result.CuttableScoringObjectsCount++; }
                }
                result.ObstaclesCount = JsonArray(root, "_obstacles").Count;
                return result;
            }
            if (version.Major < 4)
            {
                JArray colorNotes = JsonArray(root, "colorNotes");
                result.CuttableNotesCount = colorNotes.Count;
                result.CuttableScoringObjectsCount = colorNotes.Count;
                JArray burstSliders = JsonArray(root, "burstSliders");
                for (int i = 0; i < burstSliders.Count; i++) result.CuttableScoringObjectsCount += Integer(burstSliders[i], "sc") - 1;
                result.ObstaclesCount = JsonArray(root, "obstacles").Count;
                result.BombsCount = JsonArray(root, "bombNotes").Count;
                return result;
            }
            JArray v4ColorNotes = JsonArray(root, "colorNotes");
            result.CuttableNotesCount = v4ColorNotes.Count;
            result.CuttableScoringObjectsCount = v4ColorNotes.Count;
            JArray chains = JsonArray(root, "chains");
            JArray chainsData = JsonArray(root, "chainsData");
            for (int i = 0; i < chains.Count; i++)
            {
                int index = Integer(chains[i], "ci");
                if (index < 0 || index >= chainsData.Count) throw new InvalidDataException("v4 chain data index is out of range");
                result.CuttableScoringObjectsCount += Integer(chainsData[index], "c") - 1;
            }
            result.ObstaclesCount = JsonArray(root, "obstacles").Count;
            result.BombsCount = JsonArray(root, "bombNotes").Count;
            return result;
        }

        JArray JsonArray(JObject root, string name)
        {
            JToken value = root[name];
            return value == null || value.Type == JTokenType.Null ? new JArray() : (JArray)value;
        }

        int Integer(JToken item, string name)
        {
            JToken value = item[name];
            return value == null || value.Type == JTokenType.Null ? 0 : value.Value<int>();
        }

        void AssertCounts(Counts actual, ExpectedCounts expected, string label)
        {
            if (actual.NumberOfLines != expected.NumberOfLines
                || actual.CuttableNotesCount != expected.CuttableNotesCount
                || actual.CuttableScoringObjectsCount != expected.CuttableScoringObjectsCount
                || actual.ObstaclesCount != expected.ObstaclesCount
                || actual.BombsCount != expected.BombsCount)
                throw new InvalidOperationException(label + " counts differ from independent JSON expectations");
        }

        string DescribeCounts(Counts value)
        {
            return "lines=" + value.NumberOfLines + ", notes=" + value.CuttableNotesCount
                + ", scoring=" + value.CuttableScoringObjectsCount + ", obstacles=" + value.ObstaclesCount
                + ", bombs=" + value.BombsCount;
        }

        string Version2Fixture()
        {
            return "{\"_version\":\"2.6.0\",\"_events\":[],\"_notes\":["
                + "{\"_time\":1,\"_lineIndex\":0,\"_lineLayer\":0,\"_type\":0,\"_cutDirection\":1},"
                + "{\"_time\":1,\"_lineIndex\":1,\"_lineLayer\":0,\"_type\":3,\"_cutDirection\":0}],"
                + "\"_obstacles\":[]}";
        }

        string Version3Fixture()
        {
            return "{\"version\":\"3.0.0\",\"bpmEvents\":[],\"rotationEvents\":[],"
                + "\"colorNotes\":[{\"b\":1,\"x\":0,\"y\":0,\"c\":0,\"d\":1}],"
                + "\"bombNotes\":[{\"b\":1,\"x\":1,\"y\":0}],\"obstacles\":[],"
                + "\"sliders\":[],\"burstSliders\":[{\"b\":1,\"c\":0,\"x\":0,\"y\":0,"
                + "\"d\":1,\"tb\":2,\"tx\":1,\"ty\":1,\"sc\":3,\"s\":1}],\"waypoints\":[],"
                + "\"basicBeatmapEvents\":[],\"colorBoostBeatmapEvents\":[],"
                + "\"lightColorEventBoxGroups\":[],\"lightRotationEventBoxGroups\":[],"
                + "\"lightTranslationEventBoxGroups\":[],\"vfxEventBoxGroups\":[],"
                + "\"basicEventTypesWithKeywords\":{},\"useNormalEventsAsCompatibleEvents\":false}";
        }

        string Version4Fixture()
        {
            return "{\"version\":\"4.0.0\",\"colorNotes\":[{\"b\":1,\"i\":0}],"
                + "\"bombNotes\":[{\"b\":1,\"i\":0}],\"obstacles\":[],"
                + "\"chains\":[{\"hb\":1,\"hr\":0,\"tb\":2,\"tr\":0,\"i\":0,\"ci\":0}],"
                + "\"arcs\":[],\"njsEvents\":[],\"colorNotesData\":[{\"x\":0,\"y\":0,\"c\":0,\"d\":1}],"
                + "\"bombNotesData\":[{\"x\":1,\"y\":0}],\"obstaclesData\":[],"
                + "\"chainsData\":[{\"tx\":1,\"ty\":1,\"c\":3,\"s\":1}],\"arcsData\":[],\"njsEventData\":[]}";
        }

        MethodInfo FindMethod(Type type, string name, Type[] parameters)
        {
            MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name != name) continue;
                ParameterInfo[] actual = method.GetParameters();
                if (actual.Length != parameters.Length) continue;
                bool same = true;
                for (int p = 0; p < actual.Length; p++) if (actual[p].ParameterType != parameters[p]) same = false;
                if (same) return method;
            }
            return null;
        }

        FieldInfo GetFieldIncludingBases(Type type, string name)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                FieldInfo field = current.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            return null;
        }

        object GetFieldValue(object target, string name)
        {
            if (target == null) return null;
            FieldInfo field = GetFieldIncludingBases(target.GetType(), name);
            return field == null ? null : field.GetValue(target);
        }

        object GetMemberValue(object target, string name)
        {
            if (target == null || String.IsNullOrEmpty(name)) return null;
            for (Type current = target.GetType(); current != null; current = current.BaseType)
            {
                FieldInfo field = current.GetField(name,
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    try { return field.GetValue(target); } catch { return null; }
                }
                PropertyInfo property = current.GetProperty(name,
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    try { return property.GetValue(target, null); } catch { return null; }
                }
            }
            return null;
        }

        int ReadIntMember(object target, string name, int fallback)
        {
            object value = GetMemberValue(target, name);
            if (value == null) return fallback;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        bool ReadBoolMember(object target, string name, bool fallback)
        {
            object value = GetMemberValue(target, name);
            if (value == null) return fallback;
            try { return Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        bool HasReadableMember(object target, string name)
        {
            if (target == null || String.IsNullOrEmpty(name)) return false;
            for (Type current = target.GetType(); current != null; current = current.BaseType)
            {
                FieldInfo field = current.GetField(name,
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null) return true;
                PropertyInfo property = current.GetProperty(name,
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (property != null && property.GetIndexParameters().Length == 0 && property.CanRead) return true;
            }
            return false;
        }

        bool SetMemberValue(object target, string name, object value)
        {
            if (target == null || String.IsNullOrEmpty(name)) return false;
            for (Type current = target.GetType(); current != null; current = current.BaseType)
            {
                FieldInfo field = current.GetField(name,
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    try { field.SetValue(target, CoerceArgument(value, field.FieldType)); return true; }
                    catch { return false; }
                }
                PropertyInfo property = current.GetProperty(name,
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (property != null && property.GetIndexParameters().Length == 0 && property.CanWrite)
                {
                    try { property.SetValue(target, CoerceArgument(value, property.PropertyType), null); return true; }
                    catch { return false; }
                }
            }
            return false;
        }

        int GetChildCount(object value)
        {
            GameObject gameObject = value as GameObject;
            return gameObject == null ? 0 : gameObject.transform.childCount;
        }

        float ProjectedSpan(RectTransform rect, Vector3 axis)
        {
            if (rect == null) return 0f;
            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            return ProjectedSpan(corners, axis);
        }

        float ProjectedSpan(Vector3[] corners, Vector3 axis)
        {
            float minimum = float.MaxValue;
            float maximum = float.MinValue;
            for (int i = 0; i < corners.Length; i++)
            {
                float value = Vector3.Dot(corners[i], axis);
                minimum = Mathf.Min(minimum, value);
                maximum = Mathf.Max(maximum, value);
            }
            return Mathf.Abs(maximum - minimum);
        }

        bool AllFinite(Vector3[] values)
        {
            for (int i = 0; i < values.Length; i++)
                if (float.IsNaN(values[i].x) || float.IsNaN(values[i].y) || float.IsNaN(values[i].z)
                    || float.IsInfinity(values[i].x) || float.IsInfinity(values[i].y) || float.IsInfinity(values[i].z)) return false;
            return true;
        }

        void AddCheck(string name, bool passed, string detail)
        {
            string value = detail ?? String.Empty;
            checks.Add(new Check { Name = name, Passed = passed, Detail = value });
            try
            {
                if (logger != null)
                {
                    string concise = value.Replace('\r', ' ').Replace('\n', ' ');
                    if (concise.Length > 280) concise = concise.Substring(0, 280) + "...";
                    logger.Info("[AdvancedSongFiltersSmoke] " + name + "="
                        + (passed ? "PASS" : "FAIL") + (concise.Length == 0 ? String.Empty : " " + concise));
                }
            }
            catch { }
        }

        void RecordException(string stage, Exception error)
        {
            string value = stage + ": " + DescribeException(error);
            exceptions.Add(value);
            try { if (logger != null) logger.Error("AdvancedSongFiltersSmoke [" + stage + "]: " + error); } catch { }
        }

        string DescribeException(Exception error)
        {
            if (error == null) return String.Empty;
            StringBuilder builder = new StringBuilder();
            Exception current = error;
            while (current != null)
            {
                if (builder.Length > 0) builder.Append(" -> ");
                builder.Append(current.GetType().FullName).Append(": ").Append(current.Message);
                current = current.InnerException;
            }
            return builder.ToString();
        }

        void Finish(bool passed)
        {
            if (finished) return;
            finished = true;
            Application.logMessageReceived -= OnLogMessage;
            try
            {
                bool checksPassed = checks.All(x => x.Passed);
                WriteReport(passed && checksPassed);
            }
            catch (Exception error)
            {
                RecordException("report", error);
                try { WriteReport(false); } catch { }
            }
            StopAllCoroutines();
            Application.Quit();
        }

        void WriteReport(bool passed)
        {
            StringBuilder json = new StringBuilder();
            json.Append("{\"passed\":").Append(passed ? "true" : "false");
            json.Append(",\"startedUtc\":").Append(JsonString(DateTime.UtcNow.Subtract(clock.Elapsed).ToString("o", CultureInfo.InvariantCulture)));
            json.Append(",\"finishedUtc\":").Append(JsonString(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));
            json.Append(",\"elapsedSeconds\":").Append(clock.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture));
            json.Append(",\"visualDiagnostics\":").Append(JsonString(visualDiagnostics));
            json.Append(",\"capturePoseDiagnostics\":").Append(JsonString(capturePoseDiagnostics));
            json.Append(",\"checks\":[");
            for (int i = 0; i < checks.Count; i++)
            {
                if (i > 0) json.Append(",");
                json.Append("{\"name\":").Append(JsonString(checks[i].Name));
                json.Append(",\"passed\":").Append(checks[i].Passed ? "true" : "false");
                json.Append(",\"detail\":").Append(JsonString(checks[i].Detail)).Append("}");
            }
            json.Append("],\"exceptions\":[");
            for (int i = 0; i < exceptions.Count; i++)
            {
                if (i > 0) json.Append(",");
                json.Append(JsonString(exceptions[i]));
            }
            json.Append("],\"capturedLogs\":[");
            lock (logLock)
            {
                for (int i = 0; i < capturedLogs.Count; i++)
                {
                    if (i > 0) json.Append(",");
                    json.Append("{\"type\":").Append(JsonString(capturedLogs[i].Type));
                    json.Append(",\"message\":").Append(JsonString(capturedLogs[i].Message));
                    json.Append(",\"stackTrace\":").Append(JsonString(capturedLogs[i].StackTrace)).Append("}");
                }
            }
            json.Append("]}");
            File.WriteAllText(Path.Combine(runtimeDirectory, "report.json"), json.ToString(), new UTF8Encoding(false));
        }

        string JsonString(string value)
        {
            if (value == null) return "null";
            StringBuilder result = new StringBuilder();
            result.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '\\': result.Append("\\\\"); break;
                    case '"': result.Append("\\\""); break;
                    case '\r': result.Append("\\r"); break;
                    case '\n': result.Append("\\n"); break;
                    case '\t': result.Append("\\t"); break;
                    default:
                        if (c < 32) result.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else result.Append(c);
                        break;
                }
            }
            return result.Append('"').ToString();
        }
    }
}
