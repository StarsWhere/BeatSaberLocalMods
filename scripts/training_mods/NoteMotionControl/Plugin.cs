using System;
using System.Reflection;
using HarmonyLib;
using IPA;
using IPA.Logging;
using SiraUtil.Zenject;

namespace NoteMotionControl
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public sealed class Plugin
    {
        internal const string HarmonyId = "localtraining.NoteMotionControl";
        internal static Logger Log { get; private set; }

        private Harmony harmony;

        [Init]
        public Plugin(Logger logger, Zenjector zenjector)
        {
            Log = logger;
            RuntimeState.Initialize(logger);

            zenjector.Install<PlayerInstaller>(Location.StandardPlayer | Location.CampaignPlayer);
            zenjector.Install<MenuInstaller>(Location.Menu);
        }

        [OnStart]
        public void OnApplicationStart()
        {
            harmony = new Harmony(HarmonyId);
            try
            {
                harmony.PatchAll(Assembly.GetExecutingAssembly());
            }
            catch
            {
                harmony.UnpatchSelf();
                harmony = null;
                throw;
            }

            Log?.Info("Note Training Control 0.4.0 loaded for Beat Saber 1.45.2.");
        }

        [OnExit]
        public void OnApplicationQuit()
        {
            RuntimeState.End();
            TrainingLoad.Shutdown();
            EarlySession.Clear();
            if (harmony == null) return;
            harmony.UnpatchSelf();
            harmony = null;
        }
    }
}
