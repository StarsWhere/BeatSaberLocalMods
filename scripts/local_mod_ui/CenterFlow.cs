using System;
using System.IO;
using System.Threading.Tasks;
using BeatSaberMarkupLanguage;
using HMUI;
using UnityEngine;

namespace LocalModUI
{
    public sealed class CenterFlow : FlowCoordinator
    {
        internal MenuContext Owner;
        private CenterView menu;
        private CenterSettingsView settings;
        private string error;
        private bool navigating;
        private float navigationDeadline;
        private int navigationGeneration;
        private bool removedPage;
        internal bool CanShowError => settings == null && childFlowCoordinator == null;
        internal bool CanNavigate => !navigating && !isInTransition && childFlowCoordinator == null;

        protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            if (firstActivation)
            {
                showBackButton = true;
                menu = BeatSaberUI.CreateViewController<CenterView>();
                menu.Owner = this;
                ProvideInitialViewControllers(menu);
            }
            SetTitle(settings == null ? "Custom mods" : settings.Entry.Title);
            menu.SetError(error);
        }

        internal void OpenEntry(string id, bool secondary = false)
        {
            if (navigating || isInTransition || childFlowCoordinator != null) return;
            UiEntry entry = UiRegistry.Find(id);
            if (!UiRegistry.IsAvailable(entry)) { SetError("This tool is not ready. Try again."); return; }
            MenuContext.LastCategory = entry.Category;
            menu?.Refresh();
            SetError(null);
            try
            {
                if (secondary)
                {
                    if (entry.SecondaryOpen == null) return;
                    entry.SecondaryOpen(this);
                }
                else if (entry.Page != null)
                {
                    if (settings != null) return;
                    var view = BeatSaberUI.CreateViewController<CenterSettingsView>();
                    settings = view;
                    view.Initialize(entry);
                    SetTitle(entry.Title);
                    navigating = true;
                    navigationDeadline = Time.realtimeSinceStartup + 10;
                    int generation = ++navigationGeneration;
                    PresentViewController(view, () => { if (generation == navigationGeneration) navigating = false; }, ViewController.AnimationDirection.Horizontal);
                }
                else entry.Open(this);
            }
            catch (Exception exception)
            {
                navigating = false;
                if (settings != null && !settings.isActivated) { Destroy(settings.gameObject); settings = null; SetTitle("Custom mods"); }
                UiRegistry.ReportError("Could not open the selected tool. Try again.", exception);
            }
        }

        internal void EntriesChanged()
        {
            menu?.Refresh();
            if (settings != null && !ReferenceEquals(UiRegistry.Find(settings.Entry.Id), settings.Entry)) removedPage = true;
        }

        internal void SetError(string message) { error = message ?? ""; menu?.SetError(error); }

        protected override void BackButtonWasPressed(ViewController topViewController)
        {
            if (navigating || isInTransition) return;
            if (settings != null) ReturnFromPage();
            else Owner.Close();
        }

        private async void ReturnFromPage()
        {
            if (settings == null || navigating) return;
            var previous = settings;
            int generation = ++navigationGeneration;
            navigating = true;
            navigationDeadline = Time.realtimeSinceStartup + 10;
            try
            {
                await DismissViewController(previous, ViewController.AnimationDirection.Horizontal);
                if (generation != navigationGeneration) return;
                if (settings == previous) settings = null;
                Destroy(previous.gameObject);
                SetTitle("Custom mods");
            }
            catch (Exception exception) { if (generation == navigationGeneration) UiRegistry.ReportError("Could not close this page. Try again.", exception); }
            finally { if (generation == navigationGeneration) navigating = false; }
        }

        internal void ClosePage() { settings?.Release(); }

        internal async Task ResetPage()
        {
            int generation = ++navigationGeneration;
            navigating = true;
            navigationDeadline = Time.realtimeSinceStartup + 12;
            try
            {
                var previous = settings;
                if (previous != null)
                {
                    await DismissViewController(previous, ViewController.AnimationDirection.Horizontal, null, true);
                    if (generation != navigationGeneration) return;
                    settings = null;
                    Destroy(previous.gameObject);
                }
                SetTitle("Custom mods");
            }
            finally { if (generation == navigationGeneration) navigating = false; }
        }

        private void Update()
        {
            if (navigating && Time.realtimeSinceStartup >= navigationDeadline)
            {
                navigating = false;
                ++navigationGeneration;
                Owner.Recover("Navigation timed out. Return and try again.");
            }
            if (removedPage && !navigating && !isInTransition) { removedPage = false; ReturnFromPage(); }
        }

        private void OnDestroy()
        {
            ++navigationGeneration;
            if (settings != null) { settings.Release(); Destroy(settings.gameObject); }
            if (menu != null) Destroy(menu.gameObject);
        }
    }

    public sealed class CenterSettingsView : ViewController
    {
        internal UiEntry Entry { get; private set; }
        private bool owned;
        internal void Initialize(UiEntry entry)
        {
            Entry = entry;
            string content;
            using (var stream = entry.Page.Assembly.GetManifestResourceStream(entry.Page.Resource))
            using (var reader = new StreamReader(stream)) content = reader.ReadToEnd();
            BSMLParser.Instance.Parse(content, gameObject, entry.Page.Host);
            entry.Page.Attach?.Invoke(this);
        }

        protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            owned = true;
            try { Entry.Page.Refresh?.Invoke(); }
            catch (Exception exception) { UiRegistry.ReportError("Could not refresh this page. Try again.", exception); }
        }

        protected override void DidDeactivate(bool removedFromHierarchy, bool screenSystemDisabling) { Release(); }
        internal void Release()
        {
            if (!owned) return;
            owned = false;
            try { Entry.Page.Closed?.Invoke(); }
            catch (Exception exception) { Plugin.Log?.Error("[page-close] " + Entry.Id + ": " + exception); }
        }
        protected override void OnDestroy() { Release(); base.OnDestroy(); }
    }
}
