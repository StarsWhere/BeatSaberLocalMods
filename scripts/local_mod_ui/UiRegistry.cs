using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LocalModUI
{
    public static class UiRegistry
    {
        private static readonly Dictionary<string, UiEntry> entries = new Dictionary<string, UiEntry>(StringComparer.Ordinal);
        public static event Action Changed;
        public static bool IsReady => MenuContext.Current != null && MenuContext.Current.Ready;
        public static IReadOnlyList<UiEntry> Entries => entries.Values.OrderBy(x => x.Category).ThenBy(x => x.Order).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();

        public static IDisposable Register(UiEntry entry)
        {
            Validate(entry);
            if (entries.ContainsKey(entry.Id)) throw new InvalidOperationException("Duplicate local UI entry: " + entry.Id);
            entries.Add(entry.Id, entry);
            NotifyChanged();
            return new Registration(entry);
        }

        internal static UiEntry Find(string id) { entries.TryGetValue(id, out var entry); return entry; }
        public static bool OwnsPageScope(string scope)
        {
            if (scope == "LocalModUI") return true;
            foreach (var entry in entries.Values)
                if (entry.Page != null && entry.Page.Assembly.GetName().Name == scope) return true;
            return false;
        }
        internal static bool IsAvailable(UiEntry entry)
        {
            try { return entry != null && ReferenceEquals(Find(entry.Id), entry) && (entry.Available == null || entry.Available()); }
            catch (Exception error) { Plugin.Log?.Error("[entry-availability] " + entry.Id + ": " + error); return false; }
        }

        public static void Open(string id)
        {
            if (MenuContext.Current == null) { ReportError("Mod menu is not ready. Try again."); return; }
            MenuContext.Current.Open(id);
        }

        public static void LeaveCenter(Action continuation)
        {
            if (continuation == null) throw new ArgumentNullException(nameof(continuation));
            Action guarded = () => { try { continuation(); } catch (Exception error) { ReportError("Could not open the selected tool. Try again.", error); } };
            if (MenuContext.Current != null) MenuContext.Current.LeaveWhenReady(guarded);
            else guarded();
        }

        public static void ReportError(string message, Exception error = null)
        {
            Plugin.Log?.Error("[navigation] " + message + (error == null ? "" : " " + error));
            if (MenuContext.Current != null) MenuContext.Current.ShowError(message);
        }

        private static void Validate(UiEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (string.IsNullOrWhiteSpace(entry.Id) || entry.Id.Any(c => !(char.IsLower(c) || char.IsDigit(c) || c == '-')))
                throw new ArgumentException("Entry IDs must use lowercase letters, digits and hyphens.");
            if (string.IsNullOrWhiteSpace(entry.Title)) throw new ArgumentException("Entry title is required.");
            if (!Enum.IsDefined(typeof(UiCategory), entry.Category) || !Enum.IsDefined(typeof(SavePolicy), entry.SavePolicy)) throw new ArgumentException("Unknown UI category or save policy.");
            if ((entry.Page == null) == (entry.Open == null)) throw new ArgumentException("An entry must have one settings page or one open action.");
            if (entry.Page != null)
            {
                if (entry.Page.Host == null || entry.Page.Assembly == null || string.IsNullOrWhiteSpace(entry.Page.Resource)) throw new ArgumentException("Incomplete settings page.");
                if (!entry.Page.Assembly.GetManifestResourceNames().Contains(entry.Page.Resource)) throw new ArgumentException("Settings resource is missing: " + entry.Page.Resource);
                if (entry.SavePolicy == SavePolicy.Action) throw new ArgumentException("Hosted settings pages must declare a settings save policy.");
            }
            if ((entry.SecondaryOpen == null) != string.IsNullOrWhiteSpace(entry.SecondaryTitle)) throw new ArgumentException("Secondary action requires a title and handler.");
            if (entry.ShortcutAction != null && string.IsNullOrWhiteSpace(entry.ShortcutTitle)) throw new ArgumentException("Shortcut action requires a title.");
        }

        private static void NotifyChanged()
        {
            var callbacks = Changed;
            if (callbacks == null) return;
            foreach (Action callback in callbacks.GetInvocationList())
                try { callback(); } catch (Exception error) { Plugin.Log?.Error("[registry-change] " + error); }
        }

        private sealed class Registration : IDisposable
        {
            private UiEntry entry;
            internal Registration(UiEntry value) { entry = value; }
            public void Dispose()
            {
                if (entry == null) return;
                var previous = entry;
                entry = null;
                if (!ReferenceEquals(Find(previous.Id), previous)) return;
                entries.Remove(previous.Id);
                NotifyChanged();
            }
        }
    }
}
