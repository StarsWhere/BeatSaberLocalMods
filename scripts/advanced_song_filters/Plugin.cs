using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BetterSongList.Interfaces;
using BetterSongList.SortModels;
using HarmonyLib;
using IPA;
using SiraUtil.Zenject;
using UnityEngine;
using Zenject;

namespace AdvancedSongFilters
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public sealed class Plugin
    {
        internal static IPA.Logging.Logger Logger;
        internal static Persistence Store;
        internal static FilterSettings Settings;
        internal static NotesCache Cache;
        Harmony harmony;

        [Init]
        public void Init(IPA.Logging.Logger logger, Zenjector zenjector)
        {
            Logger = logger;
            Store = new Persistence(Environment.CurrentDirectory, delegate(string category, Exception ex) {
                logger.Error("AdvancedSongFilters [" + category + "]: " + ex);
            });
            Settings = Store.LoadSettings();
            Cache = Store.LoadCache();
            zenjector.Install<FilterInstaller>(Location.Menu);
        }
        [OnStart]
        public void Start()
        {
            Adapter.Resolve();
            harmony = new Harmony("local.beatsaber.advanced-song-filters");
            Adapter.Install(harmony);
            foreach (var metric in new[] { Metric.Nps, Metric.Njs, Metric.Notes }) {
                if (!BetterSongList.SortMethods.RegisterCustomSorter(new MetricSorter(metric)))
                    throw new InvalidOperationException("BetterSongList plugin sorts are disabled");
            }
            Logger.Info("AdvancedSongFilters 0.2.0 initialized for Beat Saber 1.45.2");
        }
        [OnExit]
        public void Exit()
        {
            if (Runtime.Current != null) Runtime.Current.Shutdown();
            if (harmony != null) harmony.UnpatchSelf();
        }
    }

    public sealed class FilterInstaller : Installer
    {
        public override void InstallBindings()
        {
            Container.Bind<Runtime>().FromNewComponentOnNewGameObject()
                .WithGameObjectName("AdvancedSongFilters.Runtime").AsSingle().NonLazy();
        }
    }

    public sealed class MetricSorter : ITransformerPlugin, ISorterCustom
    {
        readonly Metric metric;
        public MetricSorter(Metric value) { metric = value; }
        public string name { get { return metric == Metric.Nps ? "Average NPS" : metric == Metric.Njs ? "NJS" : "Note Count"; } }
        public bool visible { get { return true; } }
        public bool isReady { get { return true; } }
        public Task Prepare(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(true); }
        public void ContextSwitch(SelectLevelCategoryViewController.LevelCategory category, BeatmapLevelPack pack) { }
        public void DoSort(ref IEnumerable<BeatmapLevel> levels, bool ascending)
        {
            var current = Runtime.Current;
            if (current == null) return;
            var source = levels.ToArray();
            var byId = source.GroupBy(x => x.levelID).ToDictionary(x => x.Key, x => new Queue<BeatmapLevel>(x));
            var snapshot = current.Snapshot;
            var models = source.Select(x => {
                SongMetrics data;
                return snapshot.TryGetValue(x.levelID, out data) ? data : new SongMetrics {
                    Id = x.levelID, Name = x.songName, Charts = new ChartMetrics[0]
                };
            });
            var sorted = FilterEngine.Sort(models, current.Applied, metric, ascending);
            levels = sorted.Select(x => byId[x.Id].Dequeue()).ToArray();
        }
    }

    // All accesses to BetterSongList's internal UI/pipeline are isolated here.
    internal static class Adapter
    {
        static Type pipeline, filterUi;
        static MethodInfo refresh;
        static PropertyInfo lastIn, lastOut;
        static FieldInfo uiSingleton, uiRoot, originalFilter;
        internal static RectTransform PendingToolbar;
        internal static void Resolve()
        {
            pipeline = AccessTools.TypeByName("BetterSongList.HarmonyPatches.HookLevelCollectionTableSet");
            filterUi = AccessTools.TypeByName("BetterSongList.UI.FilterUI");
            if (pipeline == null || filterUi == null) throw new TypeLoadException("BetterSongList 0.4.3 integration types missing");
            refresh = AccessTools.Method(pipeline, "Refresh", new[] { typeof(bool), typeof(bool) });
            lastIn = AccessTools.Property(pipeline, "lastInMapList");
            lastOut = AccessTools.Property(pipeline, "lastOutMapList");
            uiSingleton = AccessTools.Field(filterUi, "persistentNuts");
            uiRoot = AccessTools.Field(filterUi, "rootTransform");
            originalFilter = AccessTools.Field(pipeline, "filter");
            if (refresh == null || lastIn == null || lastOut == null || uiSingleton == null || uiRoot == null || originalFilter == null)
                throw new MissingMemberException("BetterSongList 0.4.3 integration shape changed");
        }
        static void Patch(Harmony harmony, Type type, string target, string prefix, string postfix)
        {
            var method = AccessTools.Method(type, target);
            if (method == null) throw new MissingMethodException(type.FullName, target);
            harmony.Patch(method, prefix: prefix == null ? null : new HarmonyMethod(typeof(Adapter), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(Adapter), postfix));
        }
        internal static void Install(Harmony harmony)
        {
            Patch(harmony, pipeline, "FilterWrapper", "FilterPrefix", "FilterPostfix");
            Patch(harmony, filterUi, "AttachTo", null, "ToolbarPostfix");
            Patch(harmony, typeof(LevelCollectionNavigationController), "DidActivate", null, "ActivatePostfix");
            Patch(harmony, typeof(LevelCollectionNavigationController), "DidDeactivate", "DeactivatePrefix", null);
            Patch(harmony, typeof(LevelCollectionNavigationController), "SelectLevel", null, "LayoutPostfix");
            Patch(harmony, typeof(LevelCollectionNavigationController), "HandleLevelDetailViewControllerDidChangeDifficultyBeatmap", null, "LayoutPostfix");
            Patch(harmony, typeof(StandardLevelDetailViewController), "ShowContent", null, "LayoutPostfix");
            Patch(harmony, typeof(LevelCollectionNavigationController), "HandleLevelDetailViewControllerDidPressActionButton", "ClosePrefix", null);
            Patch(harmony, typeof(LevelCollectionNavigationController), "HandleLevelDetailViewControllerDidPressPracticeButton", "ClosePrefix", null);
            Patch(harmony, typeof(LevelCollectionTableView), "SetData", null, "TablePostfix");
        }
        internal static IReadOnlyList<BeatmapLevel> Input
        { get { return lastIn == null ? null : lastIn.GetValue(null, null) as IReadOnlyList<BeatmapLevel>; } }
        internal static IReadOnlyList<BeatmapLevel> Output
        { get { return lastOut == null ? null : lastOut.GetValue(null, null) as IReadOnlyList<BeatmapLevel>; } }
        internal static BetterSongList.FilterModels.IFilter OriginalFilter
        { get { return originalFilter.GetValue(null) as BetterSongList.FilterModels.IFilter; } }
        internal static void Refresh()
        {
            if (refresh != null && Input != null) refresh.Invoke(null, new object[] { false, true });
        }
        static void FilterPrefix(ref IReadOnlyList<BeatmapLevel> __0)
        {
            var current = Runtime.Current;
            if (current == null || __0 == null) return;
            current.ObserveInput(__0);
            __0 = current.FilterApplied(__0);
        }
        static void FilterPostfix(IReadOnlyList<BeatmapLevel> __0)
        {
            var current = Runtime.Current;
            if (current != null) current.ObserveOutput(__0);
        }
        static void ToolbarPostfix()
        {
            PendingToolbar = uiRoot.GetValue(uiSingleton.GetValue(null)) as RectTransform;
            if (Runtime.Current != null) Runtime.Current.BindToolbar(PendingToolbar);
        }
        static void ActivatePostfix(LevelCollectionNavigationController __instance)
        { if (Runtime.Current != null) Runtime.Current.Activate(__instance); }
        static void DeactivatePrefix()
        { if (Runtime.Current != null) Runtime.Current.Deactivate(); }
        static void LayoutPostfix()
        { if (Runtime.Current != null) Runtime.Current.RequestLayout(); }
        static void ClosePrefix()
        { if (Runtime.Current != null) Runtime.Current.ClosePanel(); }
        static void TablePostfix(LevelCollectionTableView __instance)
        { if (Runtime.Current != null) Runtime.Current.TableChanged(__instance); }
    }
}
