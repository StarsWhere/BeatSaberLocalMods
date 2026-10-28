using IPA;
using SiraUtil.Zenject;
using Zenject;
using HarmonyLib;

namespace LocalModUI
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public sealed class Plugin
    {
        internal static IPA.Logging.Logger Log;
        private Harmony harmony;
        [Init]
        public void Init(IPA.Logging.Logger logger, Zenjector zenjector)
        {
            Log = logger;
            harmony = new Harmony("localmodui.owned-pages");
            harmony.PatchAll(typeof(Plugin).Assembly);
            zenjector.Install<MenuInstaller>(Location.Menu);
        }
        [OnExit] public void Exit() { harmony?.UnpatchSelf(); }
    }

    public sealed class MenuInstaller : Installer
    {
        public override void InstallBindings()
        {
            Container.Bind<MenuContext>().FromNewComponentOnNewGameObject().WithGameObjectName("LocalModUI.Menu").AsSingle().NonLazy();
        }
    }
}
