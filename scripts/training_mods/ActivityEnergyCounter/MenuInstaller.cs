using System;
using BeatSaberMarkupLanguage.Settings;
using LocalModUI;
using Zenject;

namespace ActivityEnergyCounter
{
    internal sealed class MenuInstaller : Installer
    {
        public override void InstallBindings()
        {
            Container.BindInterfacesTo<EnergyMenu>().AsSingle();
        }
    }

    internal sealed class EnergyMenu : IInitializable, IDisposable
    {
        private readonly BSMLSettings settings;
        private readonly EnergySettingsHost host = new EnergySettingsHost();
        private IDisposable uiEntry;

        internal EnergyMenu(BSMLSettings settings)
        {
            this.settings = settings;
        }

        public void Initialize()
        {
            bool nativeAttempted = false, flowAttempted = false;
            try
            {
                uiEntry = UiRegistry.Register(new UiEntry
                {
                    Id = "energy", Title = "Activity Energy", Category = UiCategory.Training, Order = 20,
                    Page = new SettingsPage
                    {
                        Host = host, Assembly = typeof(EnergyMenu).Assembly,
                        Resource = "ActivityEnergyCounter.UI.Settings.bsml",
                        Refresh = () => { host.AttachStatistics(); host.Refresh(); }, Closed = host.DetachStatistics,
                    },
                });
                nativeAttempted = true;
                settings.AddSettingsMenu(Plugin.CounterName, "ActivityEnergyCounter.UI.Settings.bsml", host);
                flowAttempted = true;
                InstantSettingsFlow.Register(host, error => Plugin.Log.Error("Energy settings navigation failed: " + error));
            }
            catch
            {
                try { if (flowAttempted) InstantSettingsFlow.Unregister(host); }
                catch (Exception error) { Plugin.Log.Error("Energy settings navigation cleanup failed: " + error); }
                try { if (nativeAttempted) settings.RemoveSettingsMenu(host); }
                catch (Exception error) { Plugin.Log.Error("Energy settings menu cleanup failed: " + error); }
                try { uiEntry?.Dispose(); uiEntry = null; }
                catch (Exception error) { Plugin.Log.Error("Energy settings registration cleanup failed: " + error); }
                try { host.Dispose(); }
                catch (Exception error) { Plugin.Log.Error("Energy settings host cleanup failed: " + error); }
                throw;
            }
        }

        public void Dispose()
        {
            uiEntry?.Dispose();
            uiEntry = null;
            host.Dispose();
            InstantSettingsFlow.Unregister(host);
            settings.RemoveSettingsMenu(host);
        }
    }
}
