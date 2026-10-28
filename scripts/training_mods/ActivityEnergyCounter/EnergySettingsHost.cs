using BeatSaberMarkupLanguage.Attributes;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using UnityEngine;

namespace ActivityEnergyCounter
{
    /// <summary>
    /// BSML host for the Counters+ settings entry and the local settings pages.
    /// It keeps only a short-lived optional statistics subscription;
    /// callers can attach it while the page is visible and detach it when the
    /// page is closed.
    /// </summary>
    public sealed class EnergySettingsHost : INotifyPropertyChanged, IDisposable
    {
        private static readonly List<object> ModeChoices = new List<object> { "Net active", "Gross total" };
        private static readonly List<object> UnitChoices = new List<object> { "kJ", "kcal" };
        private static readonly List<object> ScopeChoices = new List<object> { "Song", "Session", "Today" };

        private string status = string.Empty;
        private bool statisticsAttached;
        [UIComponent("energy-settings-content")] private Transform content;

        public event PropertyChangedEventHandler PropertyChanged;

        [UIAction("#post-parse")]
        private void Parsed()
        {
            if (content != null) content.gameObject.AddComponent<EnergySettingsView>().Initialize(this);
            Refresh();
        }

        [UIValue("Modes")]
        public List<object> Modes { get { return ModeChoices; } }

        [UIValue("Units")]
        public List<object> Units { get { return UnitChoices; } }

        [UIValue("Scopes")]
        public List<object> Scopes { get { return ScopeChoices; } }

        [UIValue("Mode")]
        public string Mode
        {
            get { return ReadSettings().Mode == EnergyMode.GrossTotal ? "Gross total" : "Net active"; }
            set
            {
                SaveChanged(settings => settings.Mode = string.Equals(value, "Gross total", StringComparison.Ordinal)
                    ? EnergyMode.GrossTotal : EnergyMode.NetActive);
            }
        }

        [UIValue("Unit")]
        public string Unit
        {
            get { return ReadSettings().Unit == EnergyUnit.Kilocalories ? "kcal" : "kJ"; }
            set
            {
                SaveChanged(settings => settings.Unit = string.Equals(value, "kcal", StringComparison.Ordinal)
                    ? EnergyUnit.Kilocalories : EnergyUnit.KiloJoules);
            }
        }

        [UIValue("HudPrimaryScope")]
        public string HudPrimaryScope
        {
            get { return ScopeLabel(ReadSettings().HudPrimaryScope); }
            set
            {
                EnergyScope scope;
                if (!TryParseScope(value, out scope)) scope = EnergyScope.Today;
                SaveChanged(settings => settings.HudPrimaryScope = scope);
            }
        }

        [UIValue("Enabled")]
        public bool Enabled { get { return ReadSettings().Enabled; } set { SaveChanged(settings => settings.Enabled = value); } }

        [UIValue("UseHeadTracking")]
        public bool UseHeadTracking { get { return ReadSettings().UseHeadTracking; } set { SaveChanged(settings => settings.UseHeadTracking = value); } }

        [UIValue("HudShowSong")]
        public bool HudShowSong { get { return ReadSettings().HudShowSong; } set { SaveChanged(settings => settings.HudShowSong = value); } }

        [UIValue("HudShowSession")]
        public bool HudShowSession { get { return ReadSettings().HudShowSession; } set { SaveChanged(settings => settings.HudShowSession = value); } }

        [UIValue("HudShowToday")]
        public bool HudShowToday { get { return ReadSettings().HudShowToday; } set { SaveChanged(settings => settings.HudShowToday = value); } }

        [UIValue("ResultsShowSong")]
        public bool ResultsShowSong { get { return ReadSettings().ResultsShowSong; } set { SaveChanged(settings => settings.ResultsShowSong = value); } }

        [UIValue("ResultsShowSession")]
        public bool ResultsShowSession { get { return ReadSettings().ResultsShowSession; } set { SaveChanged(settings => settings.ResultsShowSession = value); } }

        [UIValue("ResultsShowToday")]
        public bool ResultsShowToday { get { return ReadSettings().ResultsShowToday; } set { SaveChanged(settings => settings.ResultsShowToday = value); } }

        [UIValue("HudShowDuration")]
        public bool HudShowDuration { get { return ReadSettings().HudShowDuration; } set { SaveChanged(settings => settings.HudShowDuration = value); } }

        [UIValue("ResultsShowDuration")]
        public bool ResultsShowDuration { get { return ReadSettings().ResultsShowDuration; } set { SaveChanged(settings => settings.ResultsShowDuration = value); } }

        [UIValue("ShowLabels")]
        public bool ShowLabels { get { return ReadSettings().ShowLabels; } set { SaveChanged(settings => settings.ShowLabels = value); } }

        [UIValue("ShowEstimate")]
        public bool ShowEstimate { get { return ReadSettings().ShowEstimate; } set { SaveChanged(settings => settings.ShowEstimate = value); } }

        [UIValue("DecimalPlaces")]
        public float DecimalPlaces
        {
            get { return ReadSettings().DecimalPlaces; }
            set { SaveChanged(settings => settings.DecimalPlaces = (int)Math.Round(value)); }
        }

        [UIValue("HudFontScale")]
        public float HudFontScale
        {
            get { return (float)ReadSettings().HudFontScale; }
            set { SaveChanged(settings => settings.HudFontScale = value); }
        }

        [UIValue("ResultsFontScale")]
        public float ResultsFontScale
        {
            get { return (float)ReadSettings().ResultsFontScale; }
            set { SaveChanged(settings => settings.ResultsFontScale = value); }
        }

        [UIValue("BodyMassKg")]
        public float BodyMassKg
        {
            get { return (float)ReadSettings().BodyMassKg; }
            set { SaveChanged(settings => settings.BodyMassKg = value); }
        }

        [UIValue("ReferenceHandSpeedMps")]
        public float ReferenceHandSpeedMps
        {
            get { return (float)ReadSettings().ReferenceHandSpeedMps; }
            set { SaveChanged(settings => settings.ReferenceHandSpeedMps = value); }
        }

        [UIValue("CalibrationFactor")]
        public float CalibrationFactor
        {
            get { return (float)ReadSettings().CalibrationFactor; }
            set { SaveChanged(settings => settings.CalibrationFactor = value); }
        }

        [UIValue("Status")]
        public string Status
        {
            get
            {
                var errors = new List<string>();
                AddError(errors, status);
                if (Plugin.Store != null) AddError(errors, Plugin.Store.LastError);
                if (Plugin.History != null) AddError(errors, Plugin.History.LastError);
                return string.Join("\n", errors.ToArray());
            }
        }

        [UIValue("HasStatus")]
        public bool HasStatus { get { return !string.IsNullOrEmpty(Status); } }

        [UIValue("HasSessionSummary")]
        public bool HasSessionSummary { get { return SessionTotals != null; } }

        [UIValue("HasTodaySummary")]
        public bool HasTodaySummary { get { return TodayTotals != null; } }

        [UIValue("HasLatestSong")]
        public bool HasLatestSong { get { return EnergySnapshotStore.Latest != null; } }

        [UIValue("CanStartNewWorkout")]
        public bool CanStartNewWorkout
        {
            get { return Plugin.History != null && Plugin.History.IsAvailable && (EnergyRecorder.Current == null || EnergyRecorder.Current.Finished); }
        }

        [UIValue("SessionSummary")]
        public string SessionSummary { get { return FormatSummary(SessionTotals); } }

        [UIValue("TodaySummary")]
        public string TodaySummary { get { return FormatSummary(TodayTotals); } }

        [UIValue("LatestSongSummary")]
        public string LatestSongSummary { get { return FormatSummary(EnergySnapshotStore.Latest); } }

        private EnergyTotals SessionTotals
        {
            get { return Plugin.History == null ? null : Plugin.History.SessionTotals; }
        }

        private EnergyTotals TodayTotals
        {
            get { return Plugin.History == null ? null : Plugin.History.TodayTotals; }
        }

        public string FormatMass(float value) { return value.ToString("0.0", CultureInfo.InvariantCulture) + " kg"; }
        public string FormatSpeed(float value) { return value.ToString("0.0", CultureInfo.InvariantCulture) + " m/s"; }
        public string FormatFactor(float value) { return value.ToString("0.00", CultureInfo.InvariantCulture); }
        public string FormatDecimals(float value) { return ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture); }
        public string FormatScale(float value) { return value.ToString("0.0", CultureInfo.InvariantCulture) + "×"; }
        public string FormatResultsScale(float value) { return value.ToString("0.00", CultureInfo.InvariantCulture) + "×"; }

        /// <summary>Refreshes all bindings when a BSML page is activated.</summary>
        [UIAction("refresh")]
        public void Refresh()
        {
            foreach (string name in new[]
            {
                "Mode", "Unit", "HudPrimaryScope", "Enabled", "UseHeadTracking",
                "HudShowSong", "HudShowSession", "HudShowToday", "ResultsShowSong",
                "ResultsShowSession", "ResultsShowToday", "HudShowDuration",
                "ResultsShowDuration", "ShowLabels", "ShowEstimate", "DecimalPlaces",
                "HudFontScale", "ResultsFontScale", "BodyMassKg", "ReferenceHandSpeedMps", "CalibrationFactor",
                "Status", "HasStatus", "HasSessionSummary", "HasTodaySummary",
                "HasLatestSong", "CanStartNewWorkout", "SessionSummary", "TodaySummary", "LatestSongSummary"
            }) Notify(name);
        }

        /// <summary>Opt in to live updates only while the settings page is visible.</summary>
        internal void AttachStatistics()
        {
            if (statisticsAttached) return;
            Plugin.StatisticsChanged += HandleStatisticsChanged;
            statisticsAttached = true;
            Refresh();
        }

        internal void DetachStatistics()
        {
            if (!statisticsAttached) return;
            Plugin.StatisticsChanged -= HandleStatisticsChanged;
            statisticsAttached = false;
        }

        [UIAction("reset-workout")]
        public void ResetWorkout()
        {
            try
            {
                Plugin.StartNewWorkout();
                status = string.Empty;
            }
            catch (Exception error)
            {
                status = "Energy statistics reset failed: " + error.Message;
                if (Plugin.Log != null) Plugin.Log.Error(status);
            }
            Refresh();
        }

        public void Dispose()
        {
            DetachStatistics();
        }

        private void HandleStatisticsChanged()
        {
            Refresh();
        }

        private EnergySettings ReadSettings()
        {
            EnergySettings settings = Plugin.Settings;
            if (settings == null) settings = new EnergySettings();
            settings.Validate();
            return settings;
        }

        private void SaveChanged(Action<EnergySettings> change)
        {
            EnergySettings settings = ReadSettings();
            change(settings);
            Save(settings);
        }

        private void Save(EnergySettings settings)
        {
            try
            {
                settings.Validate();
                if (Plugin.Store == null) throw new InvalidOperationException("Energy settings store is unavailable.");
                Plugin.Store.SaveGlobal(settings);
                Plugin.NotifySettingsChanged();
                status = string.Empty;
            }
            catch (Exception error)
            {
                status = "Energy settings could not be saved: " + error.Message;
                if (Plugin.Log != null) Plugin.Log.Error(status);
            }
            Refresh();
        }

        private string FormatSummary(EnergyTotals totals)
        {
            return totals == null ? string.Empty : EnergyDisplay.FormatSummary(totals, ReadSettings());
        }

        private static bool TryParseScope(string value, out EnergyScope scope)
        {
            if (string.Equals(value, "Song", StringComparison.Ordinal)) { scope = EnergyScope.Song; return true; }
            if (string.Equals(value, "Today", StringComparison.Ordinal)) { scope = EnergyScope.Today; return true; }
            if (string.Equals(value, "Session", StringComparison.Ordinal)) { scope = EnergyScope.Session; return true; }
            scope = EnergyScope.Today;
            return false;
        }

        private static string ScopeLabel(EnergyScope scope)
        {
            switch (scope)
            {
                case EnergyScope.Song: return "Song";
                case EnergyScope.Today: return "Today";
                default: return "Session";
            }
        }

        private static void AddError(List<string> errors, string value)
        {
            if (!string.IsNullOrEmpty(value) && !errors.Contains(value)) errors.Add(value);
        }

        private void Notify(string name)
        {
            PropertyChangedEventHandler changed = PropertyChanged;
            if (changed != null) changed(this, new PropertyChangedEventArgs(name));
        }
    }

    // The parsed page owns its subscription, including Counters+'s separate
    // settings host. Hidden or destroyed pages stop receiving live updates.
    public sealed class EnergySettingsView : MonoBehaviour
    {
        private EnergySettingsHost host;
        internal void Initialize(EnergySettingsHost value)
        {
            host = value;
            if (isActiveAndEnabled) host.AttachStatistics();
        }
        private void OnEnable() { host?.AttachStatistics(); }
        private void OnDisable() { host?.DetachStatistics(); }
        private void OnDestroy() { host?.Dispose(); host = null; }
    }
}
