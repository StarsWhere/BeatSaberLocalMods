using System;
using System.ComponentModel;
using System.Linq;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.ViewControllers;

namespace LocalModUI
{
    [ViewDefinition("LocalModUI.Center.bsml")]
    public sealed class CenterView : BSMLAutomaticViewController, INotifyPropertyChanged
    {
        internal CenterFlow Owner;
        public new event PropertyChangedEventHandler PropertyChanged;
        private UiEntry[] rows = new UiEntry[0];
        private string error = "";
        private void Notify(string name) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
        private UiEntry Row(int index) => index < rows.Length ? rows[index] : null;
        private string Title(int index) => Row(index)?.Title ?? "";
        private bool Available(int index) => UiRegistry.IsAvailable(Row(index));
        private string Secondary(int index) => Row(index)?.SecondaryTitle ?? "";

        public string Row0 => Title(0);
        public string Row1 => Title(1);
        public string Row2 => Title(2);
        public bool HasRow0 => Row(0) != null;
        public bool HasRow1 => Row(1) != null;
        public bool HasRow2 => Row(2) != null;
        public bool CanRow0 => Available(0);
        public bool CanRow1 => Available(1);
        public bool CanRow2 => Available(2);
        public string Secondary0 => Secondary(0);
        public string Secondary1 => Secondary(1);
        public string Secondary2 => Secondary(2);
        public bool HasSecondary0 => Row(0)?.SecondaryOpen != null;
        public bool HasSecondary1 => Row(1)?.SecondaryOpen != null;
        public bool HasSecondary2 => Row(2)?.SecondaryOpen != null;
        public bool TrainingSelected => MenuContext.LastCategory == UiCategory.Training;
        public bool LibrarySelected => MenuContext.LastCategory == UiCategory.Library;
        public bool DevicesSelected => MenuContext.LastCategory == UiCategory.Devices;
        public bool TrainingUnselected => !TrainingSelected;
        public bool LibraryUnselected => !LibrarySelected;
        public bool DevicesUnselected => !DevicesSelected;
        public bool Empty => rows.Length == 0;
        public bool HasError => !string.IsNullOrEmpty(error);
        public string Error => error;

        [UIAction("training")] private void Training() { Select(UiCategory.Training); }
        [UIAction("library")] private void Library() { Select(UiCategory.Library); }
        [UIAction("devices")] private void Devices() { Select(UiCategory.Devices); }
        [UIAction("open-0")] private void Open0() { Open(0, false); }
        [UIAction("open-1")] private void Open1() { Open(1, false); }
        [UIAction("open-2")] private void Open2() { Open(2, false); }
        [UIAction("secondary-0")] private void SecondaryOpen0() { Open(0, true); }
        [UIAction("secondary-1")] private void SecondaryOpen1() { Open(1, true); }
        [UIAction("secondary-2")] private void SecondaryOpen2() { Open(2, true); }
        [UIAction("#post-parse")] private void Parsed() { Refresh(); }
        private void Open(int index, bool secondary) { var entry = Row(index); if (entry != null) Owner.OpenEntry(entry.Id, secondary); }
        private void Select(UiCategory category) { MenuContext.LastCategory = category; SetError(null); Refresh(); }
        internal void SetError(string message) { error = message ?? ""; Notify(nameof(Error)); Notify(nameof(HasError)); }
        internal void Refresh()
        {
            rows = UiRegistry.Entries.Where(x => x.Category == MenuContext.LastCategory).ToArray();
            foreach (var property in GetType().GetProperties()) Notify(property.Name);
        }
        protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);
            Refresh();
        }
    }
}
