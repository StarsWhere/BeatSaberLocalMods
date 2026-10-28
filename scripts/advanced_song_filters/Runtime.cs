using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using LocalModUI;
using UnityEngine;
using UnityEngine.UI;
using Zenject;

namespace AdvancedSongFilters
{
    public sealed class Runtime : MonoBehaviour
    {
        public static volatile Runtime Current;
        static readonly OwnedParser ParseQueue = new OwnedParser(delegate(Exception ex) {
            Plugin.Logger.Warn("AdvancedSongFilters [stale-parser-drain]: " + ex);
        });
        volatile FilterSettings applied;
        volatile Dictionary<string, SongMetrics> snapshot = new Dictionary<string, SongMetrics>(StringComparer.Ordinal);
        SynchronizationContext context;
        BeatmapDataLoader loader;
        Panel panel;
        IDisposable centerRegistration;
        Coroutine centerOpen;
        LevelCollectionNavigationController navigation;
        LevelCollectionTableView table;
        BeatmapLevel[] input = new BeatmapLevel[0];
        string[] inputIds = new string[0];
        string[] modes = new[] { "Standard" };
        CancellationTokenSource indexCancel;
        Task indexTask;
        int generation, observation, completed, total, failures;
        volatile bool busy, live, menuActive;
        bool catalogLoading, uiUpdateQueued;
        readonly Dictionary<string, Failure> errors = new Dictionary<string, Failure>(StringComparer.Ordinal);
        int errorSequence;
        PresetCollection presets = new PresetCollection();
        bool presetsAvailable;
        FilterPreview preview = new FilterPreview { State = FilterPreviewState.Loading };
        FilterSettings previewSettings;
        string previewKey;
        int previewRevision, previewGeneration, snapshotVersion, pipelineVersion;
        Coroutine previewRoutine;
        Coroutine restore;
        BeatmapLevel restoreSelection;
        string scrollAnchor;
        float scrollOffset;

        sealed class Failure
        {
            public string Category, Message;
            public int Sequence;
            public Func<bool> Retry;
        }

        public FilterSettings Applied { get { return applied; } }
        public IDictionary<string, SongMetrics> Snapshot { get { return snapshot; } }
        public string[] Modes { get { return (string[])modes.Clone(); } }
        public bool MenuActive { get { return menuActive && live; } }
        public bool IsBusy { get { return busy; } }
        public IPA.Logging.Logger Log { get { return Plugin.Logger; } }
        public FilterPreview Preview { get { return preview; } }
        public PresetCollection Presets { get { return presets.Clone(); } }
        public bool PresetsAvailable { get { return presetsAvailable; } }
        public string LastSavedPresetId { get; private set; }
        Failure LatestFailure { get { return errors.Values.OrderByDescending(x => x.Sequence).FirstOrDefault(); } }
        bool RelevantStatisticsFailure {
            get { return failures > 0 && RequiresStatistics(previewSettings ?? applied); }
        }
        public string ErrorCategory {
            get { var failure = LatestFailure; return failure != null ? failure.Category : RelevantStatisticsFailure ? "chart-statistics" : null; }
        }
        public bool CanRetry {
            get {
                var failure = LatestFailure;
                return failure != null ? failure.Retry != null && (!failure.Category.StartsWith("statistics-", StringComparison.Ordinal) || !busy)
                    : RelevantStatisticsFailure && !busy;
            }
        }
        public string Status {
            get {
                var failure = LatestFailure;
                if (failure != null) return failure.Message;
                if (RelevantStatisticsFailure && !busy) return failures + " charts could not be read";
                return String.Empty;
            }
        }
        public bool CentralTransitioning {
            get {
                if (navigation == null) return false;
                var screen = navigation.screen;
                return screen != null && screen.GetComponentsInChildren<HMUI.ViewController>(true)
                    .Any(x => x.gameObject.activeInHierarchy && x.isInTransition);
            }
        }

        [Inject]
        public void Construct(BeatmapDataLoader dataLoader)
        {
            loader = dataLoader;
            context = SynchronizationContext.Current;
            if (context == null) throw new InvalidOperationException("Unity menu synchronization context is unavailable");
            applied = Plugin.Settings.Clone();
            live = true;
            Current = this;
            panel = new Panel(this);
            centerRegistration = UiRegistry.Register(new UiEntry {
                Id = "advanced-filters", Title = "Advanced Filters", Category = UiCategory.Library,
                Order = 30, SavePolicy = SavePolicy.Explicit, Open = OpenFromCenter
            });
            if (!String.IsNullOrEmpty(Plugin.Store.Warning))
                SetFailure("configuration-read", Plugin.Store.Warning, null, ReloadSettings);
            ReloadPresets();
            SongCore.Loader.LoadingStartedEvent += SongsLoading;
            SongCore.Loader.SongsLoadedEvent += SongsLoaded;
            if (Adapter.PendingToolbar != null) BindToolbar(Adapter.PendingToolbar);
            var active = Resources.FindObjectsOfTypeAll<LevelCollectionNavigationController>()
                .FirstOrDefault(x => x.isActivated);
            if (active != null) Activate(active);
        }

        void OpenFromCenter(HMUI.FlowCoordinator parent)
        {
            UiRegistry.LeaveCenter(delegate {
                if (!live) { UiRegistry.ReportError("Could not open Advanced Filters"); return; }
                if (centerOpen == null) centerOpen = StartCoroutine(OpenSoloFilters());
            });
        }

        System.Collections.IEnumerator OpenSoloFilters()
        {
            float deadline = Time.realtimeSinceStartup + 10f;
            bool soloRequested = false;
            yield return null;
            while (live && Time.realtimeSinceStartup < deadline)
            {
                if (MenuActive && !CentralTransitioning && panel != null && panel.CanOpen)
                {
                    panel.Open();
                    centerOpen = null;
                    if (!panel.IsOpen) UiRegistry.ReportError("Could not open Advanced Filters");
                    yield break;
                }
                if (!soloRequested)
                {
                    var menu = Resources.FindObjectsOfTypeAll<MainMenuViewController>()
                        .FirstOrDefault(x => x.isActivated && !x.isInTransition && x.gameObject.activeInHierarchy);
                    if (menu != null)
                    {
                        Exception failure = null;
                        try
                        {
                            var field = AccessTools.Field(typeof(MainMenuViewController), "_soloButton");
                            var button = field == null ? null : field.GetValue(menu) as Button;
                            if (button == null || !button.interactable) throw new InvalidOperationException("Solo button unavailable");
                            button.onClick.Invoke();
                            soloRequested = true;
                        }
                        catch (Exception ex) { failure = ex; }
                        if (failure != null)
                        {
                            centerOpen = null;
                            UiRegistry.ReportError("Could not open Advanced Filters", failure);
                            yield break;
                        }
                    }
                }
                yield return null;
            }
            centerOpen = null;
            if (live) UiRegistry.ReportError("Timed out opening Advanced Filters");
        }
        public void BindToolbar(RectTransform toolbar)
        {
            if (!live || toolbar == null) return;
            try { panel.BindToolbar(toolbar); }
            catch (Exception ex) { Report("panel-attach", "Could not create filter panel", ex); }
        }
        public void Activate(LevelCollectionNavigationController controller)
        {
            if (!live) return;
            navigation = controller;
            menuActive = true;
            if (Adapter.PendingToolbar != null) BindToolbar(Adapter.PendingToolbar);
            var observed = Adapter.Input;
            if (observed != null) AcceptInput(observed.ToArray(), true);
            RequestLayout();
        }
        public void Deactivate()
        {
            Interlocked.Increment(ref observation);
            menuActive = false;
            ClosePanel();
            CancelIndex();
        }
        public void ClosePanel() { EndPreview(); if (panel != null) panel.Close(); }
        public void RequestLayout()
        {
            if (!live || panel == null) return;
            panel.InvalidateLayout();
        }
        public void ObserveInput(IReadOnlyList<BeatmapLevel> levels)
        {
            if (!live || catalogLoading) return;
            var observed = levels.ToArray();
            var stamp = Interlocked.Increment(ref observation);
            if (SynchronizationContext.Current == context) {
                if (menuActive) AcceptInput(observed, false);
                return;
            }
            Post(delegate {
                if (stamp == observation && menuActive && !catalogLoading) AcceptInput(observed, false);
            });
        }
        public void ObserveOutput(IReadOnlyList<BeatmapLevel> levels)
        {
            if (!live || levels == null) return;
            Post(delegate {
                pipelineVersion++;
                InvalidatePreview();
                panel.UpdateStatus();
            });
        }
        void QueueUiUpdate()
        {
            if (!live || uiUpdateQueued) return;
            uiUpdateQueued = true;
            Post(delegate { uiUpdateQueued = false; if (panel != null) panel.UpdateStatus(); });
        }
        void Post(Action action)
        {
            if (!live || context == null) return;
            context.Post(delegate(object ignored) {
                if (!live) return;
                try { action(); }
                catch (Exception ex) { Report("menu-callback", "Could not update filter UI", ex); }
            }, null);
        }
        void AcceptInput(BeatmapLevel[] levels, bool force)
        {
            var ids = levels.Select(x => x.levelID).ToArray();
            if (!force && ids.SequenceEqual(inputIds)) return;
            input = levels;
            inputIds = ids;
            BeginIndex();
        }
        sealed class Work
        {
            public string CacheKey;
            public BeatmapKey Key;
            public IBeatmapLevelData Data;
            public ChartMetrics Target;
            public string PreparationError;
        }
        static double? Positive(float value)
        { return value > 0 && !Single.IsInfinity(value) && !Single.IsNaN(value) ? (double?)value : null; }
        static string Key(BeatmapKey value)
        { return value.levelId + "|" + value.characteristic + "|" + (int)value.difficulty; }
        void BeginIndex()
        {
            CancelIndex();
            if (!menuActive || !live) return;
            ClearFailure("statistics-index");
            ClearFailure("statistics-cancel");
            var initial = new Dictionary<string, SongMetrics>(StringComparer.Ordinal);
            var finished = new Dictionary<string, SongMetrics>(StringComparer.Ordinal);
            var work = new List<Work>();
            var allModes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var level in input.GroupBy(x => x.levelID).Select(x => x.First())) {
                var charts = new List<ChartMetrics>();
                var doneCharts = new List<ChartMetrics>();
                IBeatmapLevelData data = null;
                string preparationError = null;
                foreach (var key in level.GetBeatmapKeys()) {
                    var basic = level.GetDifficultyBeatmapData(key.characteristic, key.difficulty);
                    if (basic == null) continue;
                    var mode = key.characteristic.ToString();
                    allModes.Add(mode);
                    int notes;
                    int? count = level.hasPrecalculatedData ? (int?)basic.notesCount :
                        Plugin.Cache.Counts.TryGetValue(Key(key), out notes) ? (int?)notes : null;
                    var njs = basic.noteJumpMovementSpeed;
                    if (njs <= 0) njs = BeatmapDifficultyMethods.DefaultNoteJumpMovementSpeed(key.difficulty);
                    var chart = new ChartMetrics { Mode = mode, Difficulty = (int)key.difficulty,
                        Njs = Positive(njs), Notes = count >= 0 ? count : null };
                    charts.Add(chart);
                    var target = new ChartMetrics { Mode = chart.Mode, Difficulty = chart.Difficulty,
                        Njs = chart.Njs, Notes = chart.Notes };
                    doneCharts.Add(target);
                    if (!chart.Notes.HasValue) {
                        if (data == null && preparationError == null) {
                            try {
                                if (!FilterEngine.IsCustomLevelId(level.levelID))
                                    throw new InvalidOperationException("Official level lacks precomputed note statistics");
                                data = SongCore.Loader.CustomLevelLoader.LoadBeatmapLevelData(level);
                                if (data == null) throw new InvalidOperationException("Local beatmap data is unavailable");
                            } catch (Exception ex) {
                                preparationError = ex.ToString();
                                Log.Warn("AdvancedSongFilters [chart-source] " + level.levelID + ": " + ex);
                            }
                        }
                        work.Add(new Work { CacheKey = Key(key), Key = key, Data = data,
                            Target = target, PreparationError = preparationError });
                    }
                }
                initial[level.levelID] = new SongMetrics { Id = level.levelID, Name = level.songName,
                    Duration = Positive(level.songDuration), Bpm = Positive(level.beatsPerMinute), Charts = charts.ToArray() };
                finished[level.levelID] = new SongMetrics { Id = level.levelID, Name = level.songName,
                    Duration = Positive(level.songDuration), Bpm = Positive(level.beatsPerMinute), Charts = doneCharts.ToArray() };
            }
            snapshot = initial;
            snapshotVersion++;
            modes = allModes.OrderBy(x => x == "Standard" ? 0 : 1).ThenBy(x => x, StringComparer.Ordinal).ToArray();
            if (modes.Length == 0) modes = new[] { "Standard" };
            total = work.Count;
            completed = failures = 0;
            busy = work.Count > 0;
            panel.UpdateStatus();
            var stamp = generation;
            indexCancel = new CancellationTokenSource();
            indexTask = RunIndex(work, finished, stamp, indexCancel.Token);
        }
        async Task RunIndex(List<Work> work, Dictionary<string, SongMetrics> finished, int stamp, CancellationToken token)
        {
            // A cache-only index also yields, so refreshing cannot recursively
            // enter BetterSongList while its SetData hook is still executing.
            await Task.Yield();
            var counts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            var next = -1;
            Func<Task> worker = async delegate {
                for (;;) {
                    token.ThrowIfCancellationRequested();
                    var index = Interlocked.Increment(ref next);
                    if (index >= work.Count) return;
                    var item = work[index];
                    try {
                        token.ThrowIfCancellationRequested();
                        if (item.PreparationError != null) throw new InvalidDataExceptionWithContext(item.PreparationError);
                        var result = await ParseQueue.Run(delegate {
                            return loader.LoadBasicBeatmapDataAsync(item.Data, item.Key);
                        }, TimeSpan.FromSeconds(30), token);
                        token.ThrowIfCancellationRequested();
                        if (result == null || result.cuttableNotesCount < 0) throw new InvalidOperationException("Invalid note statistics");
                        item.Target.Notes = result.cuttableNotesCount;
                        counts[item.CacheKey] = result.cuttableNotesCount;
                    } catch (OperationCanceledException) { throw; }
                    catch (Exception ex) {
                        if (stamp == generation && live) {
                            failures++;
                            Log.Warn("AdvancedSongFilters [chart-statistics] " + item.CacheKey + ": " + ex);
                        }
                    }
                    if (stamp == generation && live) { completed++; QueueUiUpdate(); }
                }
            };
            try {
                await Task.WhenAll(worker(), worker());
                token.ThrowIfCancellationRequested();
                if (stamp != generation || !live || !menuActive) return;
                snapshot = finished;
                snapshotVersion++;
                InvalidatePreview();
                foreach (var value in counts) Plugin.Cache.Counts[value.Key] = value.Value;
                busy = false;
                if (counts.Count > 0) {
                    SaveStatisticsCache();
                }
                RefreshPreservingSelection();
                panel.UpdateStatus();
            } catch (OperationCanceledException) {
                if (!token.IsCancellationRequested && stamp == generation && live)
                    SetFailure("statistics-cancel", "Chart statistics interrupted", new InvalidOperationException("Unexpected parser cancellation"), RetryStatistics);
            } catch (Exception ex) {
                if (stamp == generation && live) SetFailure("statistics-index", "Chart statistics failed", ex, RetryStatistics);
            } finally {
                if (stamp == generation && live) { busy = false; InvalidatePreview(); panel.UpdateStatus(); }
            }
        }
        sealed class InvalidDataExceptionWithContext : Exception
        { public InvalidDataExceptionWithContext(string message) : base(message) { } }
        void CancelIndex()
        {
            generation++;
            if (indexCancel != null) { indexCancel.Cancel(); indexCancel.Dispose(); indexCancel = null; }
            busy = false;
            InvalidatePreview();
        }
        void SongsLoading(SongCore.Loader ignored)
        {
            if (!live) return;
            Interlocked.Increment(ref observation);
            CancelIndex();
            catalogLoading = true;
            snapshot = new Dictionary<string, SongMetrics>(StringComparer.Ordinal);
            snapshotVersion++;
            panel.UpdateStatus();
        }
        void SongsLoaded(SongCore.Loader ignored, ConcurrentDictionary<string, BeatmapLevel> songs)
        {
            if (!live) return;
            Interlocked.Increment(ref observation);
            catalogLoading = false;
            inputIds = new string[0];
            if (menuActive) {
                var levels = Adapter.Input;
                if (levels != null) AcceptInput(levels.ToArray(), true);
            }
        }
        static bool RequiresStatistics(FilterSettings settings)
        {
            return settings != null && settings.Enabled && (settings.Nps.IsSet || settings.Notes.IsSet);
        }

        // This predicate is shared by preview and the installed BetterSongList
        // transformer. The native filter remains owned by BetterSongList.
        static MatchAssessment Assess(BeatmapLevel level, IDictionary<string, SongMetrics> data, FilterSettings settings)
        {
            if (!settings.Enabled) return MatchAssessment.Match;
            if (settings.ExcludeOfficial && !FilterEngine.IsCustomLevelId(level.levelID)) return MatchAssessment.NoMatch;
            if (!settings.RequiresSongMetrics) return MatchAssessment.Match;
            SongMetrics song;
            if (!data.TryGetValue(level.levelID, out song)) return MatchAssessment.Unknown;
            return FilterEngine.Assess(song, settings);
        }

        public IReadOnlyList<BeatmapLevel> FilterApplied(IReadOnlyList<BeatmapLevel> levels)
        {
            var settings = applied;
            if (!settings.Enabled) return levels;
            IReadOnlyList<BeatmapLevel> candidates = settings.ExcludeOfficial
                ? levels.Where(x => FilterEngine.IsCustomLevelId(x.levelID)).ToArray() : levels;
            if (catalogLoading) return candidates;
            var data = snapshot;
            // An observation arriving off the Unity thread is indexed on the
            // menu context. Do not turn that scheduling interval into no results.
            if (settings.RequiresSongMetrics && candidates.Any(x => !data.ContainsKey(x.levelID))) return candidates;
            var result = new List<BeatmapLevel>();
            var original = Adapter.OriginalFilter;
            foreach (var level in candidates) {
                MatchAssessment assessment = Assess(level, data, settings);
                if (assessment == MatchAssessment.Match) result.Add(level);
                else if (busy && RequiresStatistics(settings) && assessment == MatchAssessment.Unknown
                    && (original == null || !original.isReady || original.GetValueFor(level))) return candidates;
            }
            return result.ToArray();
        }

        static string SettingsKey(FilterSettings settings)
        {
            var key = new StringBuilder(settings.Mode);
            key.Append('|').Append(settings.Enabled).Append('|').Append(settings.ExcludeOfficial).Append('|');
            foreach (var difficulty in settings.Difficulties) key.Append(difficulty).Append(',');
            foreach (Metric metric in Enum.GetValues(typeof(Metric))) {
                var range = settings.Range(metric);
                key.Append('|').Append(range.Min.HasValue ? range.Min.Value.ToString("R", CultureInfo.InvariantCulture) : "-");
                key.Append(':').Append(range.Max.HasValue ? range.Max.Value.ToString("R", CultureInfo.InvariantCulture) : "-");
            }
            return key.ToString();
        }

        public void RequestPreview(FilterSettings settings, int revision)
        {
            if (!live || !menuActive) return;
            try {
                var effective = settings.Normalize();
                var original = Adapter.OriginalFilter;
                bool originalReady = original == null || original.isReady;
                string key = SettingsKey(effective) + "|" + snapshotVersion + "|" + pipelineVersion
                    + "|" + catalogLoading + "|" + busy + "|" + originalReady;
                previewSettings = effective;
                previewRevision = revision;
                if (previewKey == key) return;
                InvalidatePreview();
                previewKey = key;
                int stamp = previewGeneration;
                preview = new FilterPreview { State = FilterPreviewState.Loading, Total = input.Length };
                if (catalogLoading || !originalReady) return;
                previewRoutine = StartCoroutine(ComputePreview(effective, input, snapshot, original, stamp));
            } catch (Exception ex) {
                InvalidatePreview();
                preview = new FilterPreview { State = FilterPreviewState.Error, Total = input.Length, ErrorCategory = "filter-preview" };
                SetFailure("filter-preview", "Could not preview filters", ex, RetryPreview);
            }
        }

        IEnumerator ComputePreview(FilterSettings settings, BeatmapLevel[] levels,
            IDictionary<string, SongMetrics> data, BetterSongList.FilterModels.IFilter original, int stamp)
        {
            // Coalesce changes occurring in a single frame. Native filter calls
            // stay on Unity's context, with a bounded per-frame work budget.
            yield return null;
            var result = new FilterPreview { Total = levels.Length };
            var budget = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < levels.Length; i++) {
                if (!live || !menuActive || stamp != previewGeneration) yield break;
                try {
                    if (original == null || original.GetValueFor(levels[i])) {
                        MatchAssessment assessment = Assess(levels[i], data, settings);
                        if (assessment == MatchAssessment.Match) result.Matched++;
                        else if (assessment == MatchAssessment.Unknown) result.Unknown++;
                    }
                } catch (Exception ex) {
                    previewRoutine = null;
                    preview = new FilterPreview { State = FilterPreviewState.Error, Total = levels.Length, ErrorCategory = "filter-preview" };
                    SetFailure("filter-preview", "Could not preview filters", ex, RetryPreview);
                    QueueUiUpdate();
                    yield break;
                }
                if (budget.ElapsedMilliseconds >= 2) {
                    yield return null;
                    budget.Restart();
                }
            }
            if (!live || !menuActive || stamp != previewGeneration) yield break;
            result.State = result.Unknown > 0
                ? (busy && RequiresStatistics(settings) ? FilterPreviewState.Loading : FilterPreviewState.Partial)
                : FilterPreviewState.Ready;
            if (result.State == FilterPreviewState.Partial) result.ErrorCategory = "statistics-incomplete";
            previewRoutine = null;
            preview = result;
            ClearFailure("filter-preview");
            QueueUiUpdate();
        }

        void InvalidatePreview()
        {
            previewGeneration++;
            previewKey = null;
            if (previewRoutine != null) { StopCoroutine(previewRoutine); previewRoutine = null; }
        }

        public void EndPreview()
        {
            InvalidatePreview();
            previewSettings = null;
            preview = new FilterPreview { State = FilterPreviewState.Loading, Total = input.Length };
            // Closing the edit session discards its unapplied settings and
            // retry payload. Explicit preset operations have separate owners.
            ClearFailure("configuration-write");
            ClearFailure("configuration-validation");
            ClearFailure("filter-preview");
        }

        bool RetryPreview()
        {
            ClearFailure("filter-preview");
            var settings = previewSettings;
            InvalidatePreview();
            if (settings != null) RequestPreview(settings, previewRevision);
            return true;
        }

        public bool Commit(FilterSettings settings)
        {
            FilterSettings value;
            try { value = settings.Normalize(); }
            catch (Exception ex) { SetFailure("configuration-validation", "Invalid filters", ex, null); return false; }
            var original = Adapter.OriginalFilter;
            if (catalogLoading || (original != null && !original.isReady)) return false;
            if (busy && RequiresStatistics(value)
                && !(previewKey != null && previewSettings != null && SettingsKey(previewSettings) == SettingsKey(value) && preview.CanApply)) return false;
            try { Plugin.Store.SaveSettings(value); }
            catch (Exception ex) {
                SetFailure("configuration-write", "Save failed; filters have not been applied", ex, delegate { return Commit(value); });
                return false;
            }
            ClearFailure("configuration-validation");
            ClearFailure("configuration-write");
            ClearFailure("configuration-read");
            applied = value;
            Plugin.Settings = value.Clone();
            try { RefreshPreservingSelection(); }
            catch (Exception ex) {
                SetFailure("song-list-refresh", "Filters saved; song list could not be refreshed", ex, RetryListRefresh);
                return false;
            }
            ClearFailure("song-list-refresh");
            panel.UpdateStatus();
            return true;
        }

        bool ReloadSettings()
        {
            var value = Plugin.Store.LoadSettings();
            if (!String.IsNullOrEmpty(Plugin.Store.Warning)) {
                SetFailure("configuration-read", Plugin.Store.Warning, null, ReloadSettings);
                return false;
            }
            applied = value;
            Plugin.Settings = value.Clone();
            ClearFailure("configuration-read");
            return RetryListRefresh();
        }

        bool RetryListRefresh()
        {
            try { RefreshPreservingSelection(); ClearFailure("song-list-refresh"); QueueUiUpdate(); return true; }
            catch (Exception ex) { SetFailure("song-list-refresh", "Could not refresh song list", ex, RetryListRefresh); return false; }
        }

        bool SaveStatisticsCache()
        {
            try { Plugin.Store.SaveCache(Plugin.Cache); ClearFailure("statistics-cache-write"); return true; }
            catch (Exception ex) { SetFailure("statistics-cache-write", "Could not save statistics cache", ex, SaveStatisticsCache); return false; }
        }

        bool RetryStatistics()
        {
            if (!menuActive || busy) return false;
            BeginIndex();
            return true;
        }

        bool ReloadPresets()
        {
            try {
                presets = Plugin.Store.LoadPresets();
                presetsAvailable = true;
                ClearFailure("presets-read");
                return true;
            } catch (Exception ex) {
                presetsAvailable = false;
                SetFailure("presets-read", "Could not read presets; original file preserved", ex, ReloadPresets);
                return false;
            }
        }

        public FilterPreset SavePreset(string id, string name, FilterSettings settings)
        {
            if (!presetsAvailable) return null;
            name = (name ?? String.Empty).Trim();
            if (name.Length == 0 || name.Length > 32) {
                SetFailure("presets-validation", "Name must contain 1–32 characters", null, null);
                return null;
            }
            if (presets.Presets.Any(x => x.Id != id && String.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) {
                SetFailure("presets-validation", "Preset name already exists", null, null);
                return null;
            }
            FilterPreset saved;
            PresetCollection candidate;
            try {
                candidate = presets.Clone();
                if (id != null && !candidate.Presets.Any(x => x.Id == id)) throw new ArgumentException("Preset no longer exists");
                saved = new FilterPreset { Id = id ?? Guid.NewGuid().ToString("N"), Name = name, Settings = settings.Normalize() };
                int index = candidate.Presets.FindIndex(x => x.Id == id);
                if (index >= 0) candidate.Presets[index] = saved; else candidate.Presets.Add(saved);
                candidate.Validate();
            } catch (Exception ex) { SetFailure("presets-validation", "Invalid preset", ex, null); return null; }
            return PersistPreset(candidate, saved);
        }

        FilterPreset PersistPreset(PresetCollection candidate, FilterPreset saved)
        {
            try { Plugin.Store.SavePresets(candidate); }
            catch (Exception ex) {
                // All preset writes run synchronously on the menu context. A
                // later successful mutation clears this pending retry; until
                // then both the candidate and its generated id remain owned.
                SetFailure("presets-write", "Could not save preset", ex, delegate { return PersistPreset(candidate, saved) != null; });
                return null;
            }
            presets = candidate;
            LastSavedPresetId = saved.Id;
            ClearFailure("presets-validation");
            ClearFailure("presets-write");
            return saved.Clone();
        }

        public bool RenamePreset(string id, string name)
        {
            var preset = presets.Presets.FirstOrDefault(x => x.Id == id);
            return preset != null && SavePreset(id, name, preset.Settings) != null;
        }

        public bool DeletePreset(string id)
        {
            if (!presetsAvailable) return false;
            var candidate = presets.Clone();
            if (candidate.Presets.RemoveAll(x => x.Id == id) == 0) return true;
            try { Plugin.Store.SavePresets(candidate); }
            catch (Exception ex) { SetFailure("presets-write", "Could not delete preset", ex, delegate { return DeletePreset(id); }); return false; }
            presets = candidate;
            LastSavedPresetId = null;
            ClearFailure("presets-write");
            ClearFailure("presets-validation");
            return true;
        }

        public bool Retry()
        {
            var failure = LatestFailure;
            if (failure != null && failure.Retry != null) return failure.Retry();
            return failure == null && RelevantStatisticsFailure && RetryStatistics();
        }

        void ClearFailure(string category) { errors.Remove(category); }
        void SetFailure(string category, string visible, Exception ex, Func<bool> retry)
        {
            if (ex != null) Log.Error("AdvancedSongFilters [" + category + "]: " + ex);
            errors[category] = new Failure { Category = category, Message = visible, Retry = retry, Sequence = ++errorSequence };
        }
        void Report(string category, string visible, Exception ex) { SetFailure(category, visible, ex, null); }
        public void TableChanged(LevelCollectionTableView value)
        {
            table = value;
            RequestLayout();
        }
        void RefreshPreservingSelection()
        {
            if (!menuActive || Adapter.Input == null) return;
            if (table != null) {
                restoreSelection = AccessTools.Field(typeof(LevelCollectionTableView), "_selectedBeatmapLevel").GetValue(table) as BeatmapLevel;
                var native = AccessTools.Field(typeof(LevelCollectionTableView), "_tableView").GetValue(table) as HMUI.TableView;
                if (native != null) {
                    var oldOutput = Adapter.Output;
                    var top = Math.Max(0, (int)(native.scrollView.position / Math.Max(1, native.cellSize + native.spacing)));
                    var header = (bool)AccessTools.Field(typeof(LevelCollectionTableView), "_showLevelPackHeader").GetValue(table);
                    var index = top - (header ? 1 : 0);
                    scrollAnchor = oldOutput != null && index >= 0 && index < oldOutput.Count ? oldOutput[index].levelID : null;
                    scrollOffset = native.scrollView.position - CellPosition(native, top);
                }
            }
            Adapter.Refresh();
            if (restore != null) StopCoroutine(restore);
            restore = StartCoroutine(RestoreSelection());
        }
        IEnumerator RestoreSelection()
        {
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            restore = null;
            if (!menuActive || table == null) yield break;
            var output = Adapter.Output;
            if (restoreSelection != null) {
                var found = output == null ? null : output.FirstOrDefault(x => x.levelID == restoreSelection.levelID);
                if (found != null) {
                    if (navigation != null) navigation.SelectLevel(found);
                    else table.SelectLevel(found);
                }
                else ClearNativeSelection();
            } else ClearNativeSelection();
            var native = AccessTools.Field(typeof(LevelCollectionTableView), "_tableView").GetValue(table) as HMUI.TableView;
            if (native != null && output != null && scrollAnchor != null) {
                var index = output.ToList().FindIndex(x => x.levelID == scrollAnchor);
                if (index >= 0) {
                    var header = (bool)AccessTools.Field(typeof(LevelCollectionTableView), "_showLevelPackHeader").GetValue(table);
                    native.ScrollToPosition(CellPosition(native, index + (header ? 1 : 0)) + scrollOffset, false);
                }
            }
            restoreSelection = null;
            RequestLayout();
        }
        void ClearNativeSelection()
        {
            table.ClearSelection();
            if (navigation != null) navigation.ClearSelected();
            // Native ClearSelection resets the row highlight, but leaves this
            // backing reference unchanged. It must not outlive filtered data.
            AccessTools.Field(typeof(LevelCollectionTableView), "_selectedBeatmapLevel").SetValue(table, null);
        }
        static float CellPosition(HMUI.TableView value, int index)
        {
            return (float)AccessTools.Method(typeof(HMUI.TableView), "GetCellPosition", new[] { typeof(int) })
                .Invoke(value, new object[] { index });
        }
        public void Shutdown()
        {
            if (!live) return;
            live = false;
            if (centerOpen != null) { StopCoroutine(centerOpen); centerOpen = null; }
            if (centerRegistration != null) { centerRegistration.Dispose(); centerRegistration = null; }
            menuActive = false;
            CancelIndex();
            SongCore.Loader.LoadingStartedEvent -= SongsLoading;
            SongCore.Loader.SongsLoadedEvent -= SongsLoaded;
            if (restore != null) StopCoroutine(restore);
            if (panel != null) { panel.Dispose(); panel = null; }
            if (ReferenceEquals(Current, this)) Current = null;
        }
        void OnDestroy() { Shutdown(); }
    }
}
