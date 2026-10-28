using System;
using System.Linq;
using BeatSaberMarkupLanguage;
using HMUI;
using IPA;
using LocalModUI;
using SiraUtil.Zenject;
using UnityEngine;
using Zenject;

namespace SongDuplicateCleaner
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public sealed class Plugin
    {
        internal static IPA.Logging.Logger Log;
        [Init] public void Init(IPA.Logging.Logger logger, Zenjector zenjector)
        {
            Log = logger;
            zenjector.Install<CleanerInstaller>(Location.Menu);
        }
    }

    public sealed class CleanerInstaller : Installer
    {
        public override void InstallBindings()
        {
            Container.Bind<CleanerMenu>().FromNewComponentOnNewGameObject()
                .WithGameObjectName("SongDuplicateCleaner.Menu").AsSingle().NonLazy();
        }
    }

    public sealed class CleanerMenu : MonoBehaviour
    {
        IDisposable registration;
        CleanerFlow flow;
        FlowCoordinator parent;
        bool closing;
        void Start()
        {
            registration = UiRegistry.Register(new UiEntry
            {
                Id = "song-cleaner", Title = "Duplicate songs", Category = UiCategory.Library,
                Order = 20, SavePolicy = SavePolicy.Action, Open = Open
            });
        }
        public void Open(FlowCoordinator owner)
        {
            try
            {
                if (closing || owner == null || !owner.isActivated || owner.isInTransition) return;
                if (flow == null)
                {
                    flow = BeatSaberUI.CreateFlowCoordinator<CleanerFlow>();
                    flow.Owner = this;
                }
                if (flow.isActivated || flow.isInTransition) return;
                parent = owner;
                parent.PresentFlowCoordinator(flow, animationDirection: ViewController.AnimationDirection.Horizontal);
            }
            catch (Exception error) { Plugin.Log.Error("[open] " + error); }
        }
        public void Close()
        {
            if (flow == null || parent == null || closing || flow.isInTransition) return;
            flow.Cancel();
            closing = true;
            try
            {
                parent.DismissFlowCoordinator(flow, delegate { closing = false; parent = null; },
                    ViewController.AnimationDirection.Horizontal);
            }
            catch (Exception error) { closing = false; Plugin.Log.Error("[close] " + error); }
        }
        void OnApplicationQuit() { if (flow != null) flow.Cancel(); }
        void OnDestroy()
        {
            if (flow != null)
            {
                flow.Cancel();
                UnityEngine.Object.Destroy(flow.gameObject);
                flow = null;
            }
            try { if (registration != null) registration.Dispose(); }
            catch (Exception error) { Plugin.Log.Error("[unregister] " + error); }
            registration = null;
        }
    }

    public sealed class CleanerFlow : FlowCoordinator
    {
        internal CleanerMenu Owner;
        public CleanerView View { get; private set; }
        protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            if (!firstActivation) return;
            showBackButton = true;
            SetTitle("重复谱面清理");
            View = BeatSaberUI.CreateViewController<CleanerView>();
            ProvideInitialViewControllers(View);
        }
        protected override void BackButtonWasPressed(ViewController topViewController)
        {
            if (View != null && View.ConfirmVisible) View.CancelConfirmation();
            else if (View == null || !View.Mutating) Owner.Close();
        }
        internal void Cancel() { if (View != null) View.CancelWork(); }
        void OnDestroy() { if (View != null) { View.CancelWork(); UnityEngine.Object.Destroy(View.gameObject); } }
    }
}
