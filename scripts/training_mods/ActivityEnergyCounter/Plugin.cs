using HarmonyLib;
using IPA;
using LocalTraining;
using SiraUtil.Zenject;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Zenject;

namespace ActivityEnergyCounter
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public sealed class Plugin
    {
        public const string PluginId = "ActivityEnergyCounter";
        public const string CounterName = "Activity Energy";
        internal static IPA.Logging.Logger Log { get; private set; }
        internal static ProfileStore<EnergySettings> Store { get; private set; }
        internal static EnergyHistoryStore History { get; private set; }
        internal static EnergySettings DisplaySettings { get; private set; } = new EnergySettings();
        internal static EnergySettings Settings { get { return Store == null ? new EnergySettings() : Store.Global; } }
        internal static event Action StatisticsChanged;
        private Harmony harmony;
        private static bool stopped;

        [Init]
        public void Init(IPA.Logging.Logger logger, Zenjector zenjector)
        {
            Log = logger; stopped = false;
            Store = new ProfileStore<EnergySettings>(PluginId, ReportProfileError);
            DisplaySettings = Settings;
            History = new EnergyHistoryStore(HistoryRoot(), ReportProfileError);
            zenjector.Install<AppInstaller>(Location.App);
            zenjector.Install<MenuInstaller>(Location.Menu);
            zenjector.Install<GameInstaller>(Location.StandardPlayer | Location.CampaignPlayer);
        }

        private static string HistoryRoot()
        {
            string root = Path.GetFullPath(Environment.CurrentDirectory);
            if (!Environment.GetCommandLineArgs().Contains("--effect-mods-smoke")) return root;
            string run = Environment.GetEnvironmentVariable("EFFECT_SMOKE_DIR");
            if (string.IsNullOrEmpty(run)) throw new InvalidOperationException("Runtime tests need an isolated energy history directory.");
            string full = Path.GetFullPath(run);
            string temporaryRoot = Path.Combine(root, ".tmp") + Path.DirectorySeparatorChar;
            if (!full.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Runtime energy history must be inside the workspace temporary directory.");
            return Path.Combine(full, "activity-energy-data");
        }

        [OnStart]
        public void OnApplicationStart()
        {
            harmony = new Harmony("local.beatsaber.activity-energy-counter");
            try { harmony.PatchAll(Assembly.GetExecutingAssembly()); }
            catch { harmony.UnpatchSelf(); harmony = null; throw; }
            Log?.Info("Activity Energy Counter 0.2.1 enabled; three-point energy is an estimate.");
        }
        internal static void NotifySettingsChanged() { DisplaySettings = Settings; RaiseStatisticsChanged(); }
        internal static void StartNewWorkout()
        {
            if (EnergyRecorder.Current != null && !EnergyRecorder.Current.Finished)
                throw new InvalidOperationException("Finish the current song before starting a new workout.");
            History?.StartNewSession(); History?.RequestSave(); RaiseStatisticsChanged();
        }
        internal static void RaiseStatisticsChanged()
        {
            Action handlers = StatisticsChanged;
            if (handlers == null) return;
            foreach (Action handler in handlers.GetInvocationList())
                try { handler(); } catch (Exception error) { Log?.Error("energy-display-refresh: " + error); }
        }
        [OnExit]
        public void OnApplicationQuit()
        {
            Shutdown();
            EnergySnapshotStore.Clear();
            if (harmony != null) { harmony.UnpatchSelf(); harmony = null; }
        }
        internal static void Shutdown()
        {
            if (stopped) return;
            stopped = true;
            EnergyRecorder.Current?.Finish("quit");
            if (History != null)
            {
                if (!History.Flush(TimeSpan.FromSeconds(5))) Log?.Error("energy-history-flush: " + History.LastError);
                History.Dispose();
            }
        }
        private static void ReportProfileError(string category, Exception error) { Log?.Error(category + ": " + error); }
    }

    internal sealed class AppInstaller : Installer
    {
        public override void InstallBindings()
        {
            Container.Bind<EnergyAppLifetime>().FromNewComponentOnNewGameObject()
                .WithGameObjectName("ActivityEnergyCounter.History").AsSingle().NonLazy();
        }
    }
    public sealed class EnergyAppLifetime : MonoBehaviour
    {
        private double nextCheckpoint, nextRefresh;
        private void Update()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (now >= nextCheckpoint) { Plugin.History?.RequestSave(); nextCheckpoint = now + 5; }
            if (now >= nextRefresh) { Plugin.RaiseStatisticsChanged(); nextRefresh = now + 1; }
        }
        private void OnApplicationQuit() { Plugin.Shutdown(); }
    }
}

