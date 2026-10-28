using System;
using System.Reflection;
using HMUI;

namespace LocalModUI
{
    public enum UiCategory { Training, Library, Devices }
    public enum SavePolicy { Instant, Explicit, Action }

    public sealed class SettingsPage
    {
        public object Host { get; set; }
        public Assembly Assembly { get; set; }
        public string Resource { get; set; }
        public Action Refresh { get; set; }
        public Action Closed { get; set; }
        public Action<ViewController> Attach { get; set; }
    }

    public sealed class UiEntry
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public UiCategory Category { get; set; }
        public int Order { get; set; }
        public SavePolicy SavePolicy { get; set; } = SavePolicy.Instant;
        public SettingsPage Page { get; set; }
        public Action<FlowCoordinator> Open { get; set; }
        public string SecondaryTitle { get; set; }
        public Action<FlowCoordinator> SecondaryOpen { get; set; }
        public Func<bool> Available { get; set; }
        public string ShortcutTitle { get; set; }
        public Action ShortcutAction { get; set; }
    }
}
