using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace EffectModsSmoke;

// Runtime checks for the energy recorder are kept in their own partial so the
// effect smoke helper remains optional: every interaction with the energy
// plugin is resolved by reflection and the helper still loads without it.
public sealed partial class Runner
{
    bool energyRuntimeCaptured;
    bool energyRuntimeRecorderPresent;
    bool energyRuntimeSnapshotSeen;
    bool energyRuntimeSnapshotFinite;
    bool energyRuntimeSnapshotMonotonic = true;
    bool energyRuntimeSnapshotAdvanced;
    bool energyRuntimeSimulated;
    bool energyRuntimePlayStable = true;
    bool energyRuntimeHistoryStable = true;
    bool energyRuntimeDisplayChecked;
    bool energyRuntimeResultsChecked;
    string energyRuntimePlayId = string.Empty;
    string energyRuntimeBeatmapKey = string.Empty;
    string energyRuntimeTrackingMode = string.Empty;
    double energyRuntimeLastEquivalent = -1;
    double energyRuntimeLastPlaying = -1;
    double energyRuntimeSessionActive = double.NaN;
    double energyRuntimeSessionGross = double.NaN;
    double energyRuntimeTodayActive = double.NaN;
    double energyRuntimeTodayGross = double.NaN;
    object energyRuntimeRecorder;
    object energyRuntimeHistory;

    void VerifyEnergyRuntimeMenu()
    {
        Type settingsType = FindTrainingType("ActivityEnergyCounter.EnergySettings");
        Type recorderType = FindTrainingType("ActivityEnergyCounter.EnergyRecorder");
        Type historyType = FindTrainingType("ActivityEnergyCounter.EnergyHistoryStore");
        Type displayType = FindTrainingType("ActivityEnergyCounter.EnergyDisplay");
        Type pluginType = FindTrainingType("ActivityEnergyCounter.Plugin");
        Check("training-energy-new-api-types", settingsType != null && recorderType != null && historyType != null && displayType != null,
            "settings=" + (settingsType != null) + "; recorder=" + (recorderType != null) +
            "; history=" + (historyType != null) + "; display=" + (displayType != null));

        string[] settings = {
            "Enabled", "UseHeadTracking", "HudShowSong", "HudShowSession", "HudShowToday",
            "ResultsShowSong", "ResultsShowSession", "ResultsShowToday", "HudShowDuration",
            "ResultsShowDuration", "ShowLabels", "ShowEstimate", "DecimalPlaces", "HudFontScale", "ResultsFontScale",
            "HudPrimaryScope"
        };
        string[] missingSettings = settings.Where(name => GetReadableMember(settingsType, name) == null).ToArray();
        Check("training-energy-settings-schema", missingSettings.Length == 0,
            "missing=" + String.Join(",", missingSettings));

        bool current = GetStaticMember(recorderType, "Current") != null;
        bool currentSnapshot = HasReadableRecorderMember(recorderType, "CurrentSnapshot");
        bool playId = HasReadableRecorderMember(recorderType, "CurrentPlayId");
        bool simulated = HasReadableRecorderMember(recorderType, "IsSimulated");
        bool tracking = HasReadableRecorderMember(recorderType, "TrackingMode");
        Check("training-energy-recorder-contract", currentSnapshot && playId && simulated && tracking,
            "Current=" + current + "; CurrentSnapshot=" + currentSnapshot + "; CurrentPlayId=" + playId +
            "; IsSimulated=" + simulated + "; TrackingMode=" + tracking);

        string[] history = { "SessionTotals", "TodayTotals", "HasUnsavedChanges", "LastError", "SessionId" };
        string[] missingHistory = history.Where(name => GetReadableMember(historyType, name) == null).ToArray();
        Check("training-energy-history-contract", missingHistory.Length == 0,
            "missing=" + String.Join(",", missingHistory));

        MethodInfo hud = FindEnergyDisplayMethod(displayType, "FormatHud");
        MethodInfo results = FindEnergyDisplayMethod(displayType, "FormatResults");
        Check("training-energy-display-contract", hud != null && results != null,
            "FormatHud=" + (hud != null) + "; FormatResults=" + (results != null));
        MethodInfo reset = pluginType?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == "StartNewWorkout" && method.GetParameters().Length == 0);
        Check("training-energy-workout-reset-contract", reset != null,
            "Plugin.StartNewWorkout=" + (reset != null));
        Check("training-energy-display-settings-contract",
            HasReadableStaticMember(pluginType, "DisplaySettings") && HasReadableStaticMember(pluginType, "Settings"),
            "DisplaySettings=" + HasReadableStaticMember(pluginType, "DisplaySettings") +
            "; Settings=" + HasReadableStaticMember(pluginType, "Settings"));
        VerifyEnergyPersistencePipeline();
    }

    void VerifyEnergyPersistencePipeline()
    {
        string probeRoot = string.Empty;
        object store = null;
        object restored = null;
        bool passed = false;
        string detail = string.Empty;
        try
        {
            probeRoot = Path.GetFullPath(Path.Combine(directory, "energy-pipeline-probe"));
            Directory.CreateDirectory(probeRoot);
            Type pointType = FindTrainingType("ActivityEnergyCounter.MotionPoint");
            Type frameType = FindTrainingType("ActivityEnergyCounter.MotionFrame");
            Type featuresType = FindTrainingType("ActivityEnergyCounter.MotionFeatures");
            Type integratorType = FindTrainingType("ActivityEnergyCounter.MotionIntegrator");
            Type settingsType = FindTrainingType("ActivityEnergyCounter.EnergySettings");
            Type accumulatorType = FindTrainingType("ActivityEnergyCounter.EnergyAccumulator");
            Type totalsType = FindTrainingType("ActivityEnergyCounter.EnergyTotals");
            Type historyType = FindTrainingType("ActivityEnergyCounter.EnergyHistoryStore");
            if (pointType == null || frameType == null || featuresType == null || integratorType == null ||
                settingsType == null || accumulatorType == null || totalsType == null || historyType == null)
                throw new InvalidOperationException("One or more energy pipeline types are unavailable.");

            object point0Head = Activator.CreateInstance(pointType, new object[] { 0d, 1.7d, 0d });
            object point0Left = Activator.CreateInstance(pointType, new object[] { -0.3d, 1d, 0d });
            object point0Right = Activator.CreateInstance(pointType, new object[] { 0.3d, 1d, 0d });
            object point1Head = Activator.CreateInstance(pointType, new object[] { 0.1d, 1.7d, 0d });
            object point1Left = Activator.CreateInstance(pointType, new object[] { -0.2d, 1d, 0d });
            object point1Right = Activator.CreateInstance(pointType, new object[] { 0.4d, 1d, 0d });
            object frame0 = Activator.CreateInstance(frameType);
            SetMember(frame0, "Head", point0Head); SetMember(frame0, "Left", point0Left); SetMember(frame0, "Right", point0Right);
            SetMember(frame0, "HeadTracked", true); SetMember(frame0, "LeftTracked", true); SetMember(frame0, "RightTracked", true);
            SetMember(frame0, "Timestamp", 0d);
            object frame1 = Activator.CreateInstance(frameType);
            SetMember(frame1, "Head", point1Head); SetMember(frame1, "Left", point1Left); SetMember(frame1, "Right", point1Right);
            SetMember(frame1, "HeadTracked", true); SetMember(frame1, "LeftTracked", true); SetMember(frame1, "RightTracked", true);
            SetMember(frame1, "Timestamp", 0.2d);

            object integrator = Activator.CreateInstance(integratorType);
            MethodInfo advance = integratorType.GetMethod("TryAdvance", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (advance == null) throw new MissingMethodException(integratorType.FullName, "TryAdvance");
            object[] firstArgs = { frame0, false, null };
            advance.Invoke(integrator, firstArgs);
            object[] secondArgs = { frame1, false, null };
            bool advanced = Convert.ToBoolean(advance.Invoke(integrator, secondArgs), CultureInfo.InvariantCulture);
            object features = secondArgs[2];
            object settings = Activator.CreateInstance(settingsType);
            object accumulator = Activator.CreateInstance(accumulatorType, new[] { settings });
            MethodInfo addSample = accumulatorType.GetMethod("AddSample", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (addSample == null) throw new MissingMethodException(accumulatorType.FullName, "AddSample");
            object[] sampleArgs = { features, null };
            bool added = Convert.ToBoolean(addSample.Invoke(accumulator, sampleArgs), CultureInfo.InvariantCulture);
            object delta = sampleArgs[1];
            double armSpeed = ReadDouble(features, "ArmSpeedMps", double.NaN);
            double active = ReadDouble(delta, "ActiveKiloJoules", double.NaN);
            bool modelPassed = advanced && added && delta != null && IsFiniteTraining(active) && active > 0 &&
                IsFiniteTraining(armSpeed) && Math.Abs(armSpeed) < 0.01;
            if (!modelPassed)
                throw new InvalidOperationException("model: advanced=" + advanced + "; added=" + added +
                    "; active=" + active + "; armSpeed=" + armSpeed);

            DateTimeOffset now = new DateTimeOffset(2026, 10, 16, 23, 59, 59, 900, TimeSpan.Zero);
            Func<DateTimeOffset> clock = () => now;
            ConstructorInfo historyConstructor = historyType.GetConstructor(new[] {
                typeof(string), typeof(Action<string, Exception>), typeof(Func<DateTimeOffset>), typeof(TimeZoneInfo)
            });
            if (historyConstructor == null) throw new MissingMethodException(historyType.FullName, ".ctor(string,Action,Func,TimeZoneInfo)");
            store = historyConstructor.Invoke(new object[] { probeRoot, null, clock, TimeZoneInfo.Utc });
            MethodInfo beginPlay = historyType.GetMethod("BeginPlay", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo addInterval = historyType.GetMethod("AddInterval", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo endPlay = historyType.GetMethod("EndPlay", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo flush = historyType.GetMethod("Flush", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (beginPlay == null || addInterval == null || endPlay == null || flush == null)
                throw new MissingMethodException(historyType.FullName, "history pipeline method");
            string playId = (string)beginPlay.Invoke(store, new object[] { "energy-pipeline", "three-point-1", 70d, 4d, 1d });
            DateTimeOffset start = now;
            DateTimeOffset end = now.AddSeconds(0.2);
            bool accepted = Convert.ToBoolean(addInterval.Invoke(store, new object[] { playId, 1L, start, end, delta }), CultureInfo.InvariantCulture);
            now = end;
            object session = GetMember(store, "SessionTotals");
            object today = GetMember(store, "TodayTotals");
            bool sessionMatches = TotalsEqual(session, delta, 1e-9);
            bool todayHalf = TotalsEqualScaled(today, delta, 0.5, 1e-9);
            bool duplicate = Convert.ToBoolean(addInterval.Invoke(store, new object[] { playId, 1L, start, end, delta }), CultureInfo.InvariantCulture);
            object afterDuplicate = GetMember(store, "SessionTotals");
            bool duplicateUnchanged = TotalsEqual(afterDuplicate, session, 1e-9);
            now = end;
            endPlay.Invoke(store, new object[] { playId, "completed" });
            bool flushed = Convert.ToBoolean(flush.Invoke(store, new object[] { TimeSpan.FromSeconds(3) }), CultureInfo.InvariantCulture);
            bool clean = !Convert.ToBoolean(GetMember(store, "HasUnsavedChanges"), CultureInfo.InvariantCulture);
            (store as IDisposable)?.Dispose(); store = null;

            restored = historyConstructor.Invoke(new object[] { probeRoot, null, clock, TimeZoneInfo.Utc });
            object restoredSession = GetMember(restored, "SessionTotals");
            object restoredToday = GetMember(restored, "TodayTotals");
            bool restoredSessionEmpty = TotalsEqualZero(restoredSession, 1e-9);
            bool restoredTodayHalf = TotalsEqualScaled(restoredToday, delta, 0.5, 1e-9);
            passed = accepted && sessionMatches && todayHalf && !duplicate && duplicateUnchanged && flushed && clean &&
                restoredSessionEmpty && restoredTodayHalf;
            detail = "root=" + probeRoot + "; active=" + active + "; armSpeed=" + armSpeed +
                "; accepted=" + accepted + "; session=" + sessionMatches + "; todayHalf=" + todayHalf +
                "; duplicateRejected=" + !duplicate + "; duplicateUnchanged=" + duplicateUnchanged +
                "; flushed=" + flushed + "; clean=" + clean + "; restoredSessionEmpty=" + restoredSessionEmpty +
                "; restoredTodayHalf=" + restoredTodayHalf;
        }
        catch (Exception error)
        {
            Exception actual = error is TargetInvocationException invocation && invocation.InnerException != null
                ? invocation.InnerException : error;
            detail = "root=" + probeRoot + "; " + actual.GetType().Name + ": " + actual.Message;
        }
        finally
        {
            try { (store as IDisposable)?.Dispose(); } catch (Exception error) { passed = false; detail += "; dispose=" + error.Message; }
            try { (restored as IDisposable)?.Dispose(); } catch (Exception error) { passed = false; detail += "; restoreDispose=" + error.Message; }
        }
        Check("training-energy-persistence-pipeline", passed, detail);
    }

    IEnumerator VerifyEnergyHudToggleIsolation()
    {
        Type pluginType = FindTrainingType("ActivityEnergyCounter.Plugin");
        Type settingsType = FindTrainingType("ActivityEnergyCounter.EnergySettings");
        Type recorderType = FindTrainingType("ActivityEnergyCounter.EnergyRecorder");
        object originalSettings = GetStaticMember(pluginType, "DisplaySettings");
        object recorder = GetStaticMember(recorderType, "Current");
        TMP_Text label = FindEnergyHudLabel();
        if (pluginType == null || settingsType == null || originalSettings == null || recorder == null || label == null)
        {
            Check("training-energy-hud-toggle-isolation", false,
                "DisplaySettings=" + (originalSettings != null) + "; recorder=" + (recorder != null) +
                "; label=" + (label != null));
            yield break;
        }

        object before = ReadRecorderMember(recorderType, recorder, "CurrentSnapshot");
        double beforePlaying = ReadDouble(before, "ValidPlayingSeconds", double.NaN);
        object hiddenSettings = CloneEnergySettings(settingsType, originalSettings);
        object visibleSettings = CloneEnergySettings(settingsType, originalSettings);
        bool hiddenConfigured = hiddenSettings != null &&
            SetMember(hiddenSettings, "Enabled", true) &&
            SetMember(hiddenSettings, "HudShowSong", false) &&
            SetMember(hiddenSettings, "HudShowSession", false) &&
            SetMember(hiddenSettings, "HudShowToday", false);
        bool visibleConfigured = visibleSettings != null &&
            SetMember(visibleSettings, "Enabled", true) &&
            SetMember(visibleSettings, "HudShowSong", true) &&
            SetMember(visibleSettings, "HudShowSession", true) &&
            SetMember(visibleSettings, "HudShowToday", true);
        Check("training-energy-hud-toggle-setup", hiddenConfigured && visibleConfigured,
            "hidden=" + hiddenConfigured + "; visible=" + visibleConfigured);

        try
        {
            Check("training-energy-hud-toggle-apply", SetStaticMember(pluginType, "DisplaySettings", hiddenSettings),
                "Plugin.DisplaySettings setter is unavailable.");
            yield return new WaitForSecondsRealtime(0.35f);
            object hiddenRecorder = GetStaticMember(recorderType, "Current");
            object hiddenSnapshot = ReadRecorderMember(recorderType, hiddenRecorder, "CurrentSnapshot");
            double hiddenPlaying = ReadDouble(hiddenSnapshot, "ValidPlayingSeconds", double.NaN);
            bool playingContinues = IsFiniteTraining(beforePlaying) && IsFiniteTraining(hiddenPlaying) &&
                hiddenPlaying > beforePlaying + 0.05;
            Check("training-energy-recorder-continues-with-hidden-hud",
                ReferenceEquals(recorder, hiddenRecorder) && label != null && !label.enabled && playingContinues,
                "sameRecorder=" + ReferenceEquals(recorder, hiddenRecorder) + "; labelEnabled=" +
                (label == null ? "missing" : label.enabled.ToString()) + "; playing=" + beforePlaying + "->" + hiddenPlaying);

            Check("training-energy-hud-toggle-restore-apply", SetStaticMember(pluginType, "DisplaySettings", visibleSettings),
                "Plugin.DisplaySettings setter is unavailable during restore.");
            yield return new WaitForSecondsRealtime(0.35f);
            Check("training-energy-hud-restores-after-hidden", label != null && label.enabled,
                "labelEnabled=" + (label == null ? "missing" : label.enabled.ToString()));
        }
        finally
        {
            SetStaticMember(pluginType, "DisplaySettings", originalSettings);
        }
    }

    static TMP_Text FindEnergyHudLabel()
    {
        Type viewType = FindTrainingType("ActivityEnergyCounter.EnergyHudView");
        if (viewType == null || !typeof(UnityEngine.Object).IsAssignableFrom(viewType)) return null;
        try
        {
            foreach (UnityEngine.Object value in Resources.FindObjectsOfTypeAll(viewType))
            {
                Component view = value as Component;
                if (view == null || !view.gameObject.activeInHierarchy) continue;
                TMP_Text label = GetMember(view, "label") as TMP_Text;
                if (label != null) return label;
            }
        }
        catch { }
        return null;
    }

    static object CloneEnergySettings(Type settingsType, object source)
    {
        if (settingsType == null || source == null) return null;
        try
        {
            object clone = Activator.CreateInstance(settingsType, true);
            CopyReadableSettings(source, clone, new[] {
                "Mode", "Unit", "BodyMassKg", "ReferenceHandSpeedMps", "CalibrationFactor",
                "Enabled", "UseHeadTracking", "HudShowSong", "HudShowSession", "HudShowToday",
                "ResultsShowSong", "ResultsShowSession", "ResultsShowToday", "HudShowDuration",
                "ResultsShowDuration", "ShowLabels", "ShowEstimate", "DecimalPlaces", "HudFontScale", "ResultsFontScale",
                "HudPrimaryScope"
            });
            return clone;
        }
        catch { return null; }
    }

    void CaptureEnergyRuntime()
    {
        Type recorderType = FindTrainingType("ActivityEnergyCounter.EnergyRecorder");
        Type historyType = FindTrainingType("ActivityEnergyCounter.EnergyHistoryStore");
        energyRuntimeRecorder = GetStaticMember(recorderType, "Current");
        energyRuntimeHistory = GetStaticMember(FindTrainingType("ActivityEnergyCounter.Plugin"), "History");
        energyRuntimeRecorderPresent = energyRuntimeRecorder is Component &&
            energyRuntimeRecorder is MonoBehaviour &&
            energyRuntimeRecorder.GetType().FullName != "ActivityEnergyCounter.ActivityEnergyCounter";
        energyRuntimeCaptured = true;
        energyRuntimeSnapshotSeen = false;
        energyRuntimeSnapshotFinite = false;
        energyRuntimeSnapshotMonotonic = true;
        energyRuntimeSnapshotAdvanced = false;
        energyRuntimePlayStable = true;
        energyRuntimeHistoryStable = true;
        energyRuntimeLastEquivalent = -1;
        energyRuntimeLastPlaying = -1;
        energyRuntimeBeatmapKey = string.Empty;
        energyRuntimeTrackingMode = string.Empty;
        energyRuntimePlayId = Convert.ToString(ReadRecorderMember(recorderType, energyRuntimeRecorder, "CurrentPlayId"), CultureInfo.InvariantCulture) ?? string.Empty;
        energyRuntimeSimulated = ReadBool(ReadRecorderMember(recorderType, energyRuntimeRecorder, "IsSimulated"), string.Empty, false);
        energyRuntimeSessionActive = ReadTotalsValue(energyRuntimeHistory, "SessionTotals", "ActiveKiloJoules");
        energyRuntimeSessionGross = ReadTotalsValue(energyRuntimeHistory, "SessionTotals", "GrossKiloJoules");
        energyRuntimeTodayActive = ReadTotalsValue(energyRuntimeHistory, "TodayTotals", "ActiveKiloJoules");
        energyRuntimeTodayGross = ReadTotalsValue(energyRuntimeHistory, "TodayTotals", "GrossKiloJoules");
        energyRuntimeDisplayChecked = false;
        energyRuntimeResultsChecked = false;

        Check("training-energy-recorder-independent", energyRuntimeRecorderPresent,
            "Current=" + (energyRuntimeRecorder == null ? "null" : energyRuntimeRecorder.GetType().FullName));
        Check("training-energy-history-isolated", historyType != null && energyRuntimeHistory != null,
            "History=" + (energyRuntimeHistory == null ? "null" : energyRuntimeHistory.GetType().FullName));
    }

    void ObserveEnergyRuntime(float songTime)
    {
        if (!energyRuntimeCaptured) return;
        Type recorderType = FindTrainingType("ActivityEnergyCounter.EnergyRecorder");
        object recorder = GetStaticMember(recorderType, "Current");
        Type storeType = FindTrainingType("ActivityEnergyCounter.EnergySnapshotStore");
        object snapshot = ReadRecorderMember(recorderType, recorder, "CurrentSnapshot") ??
            GetStaticMember(storeType, "Latest");
        if (recorder == null || snapshot == null) return;

        bool firstSnapshot = !energyRuntimeSnapshotSeen;
        energyRuntimeSnapshotSeen = true;
        string key = Convert.ToString(GetMember(snapshot, "BeatmapKey"), CultureInfo.InvariantCulture) ?? string.Empty;
        if (string.IsNullOrEmpty(energyRuntimeBeatmapKey)) energyRuntimeBeatmapKey = key;
        string playId = Convert.ToString(ReadRecorderMember(recorderType, recorder, "CurrentPlayId"), CultureInfo.InvariantCulture) ?? string.Empty;
        energyRuntimePlayStable &= string.IsNullOrEmpty(energyRuntimePlayId) || string.Equals(playId, energyRuntimePlayId, StringComparison.Ordinal);
        string tracking = Convert.ToString(ReadRecorderMember(recorderType, recorder, "TrackingMode"), CultureInfo.InvariantCulture) ?? string.Empty;
        if (string.IsNullOrEmpty(energyRuntimeTrackingMode)) energyRuntimeTrackingMode = tracking;
        bool simulated = ReadBool(ReadRecorderMember(recorderType, recorder, "IsSimulated"), string.Empty, false) ||
            ReadBool(GetMember(snapshot, "IsSimulated"), string.Empty, false);
        energyRuntimeSimulated |= simulated;

        double equivalent = ReadDouble(snapshot, "EquivalentSeconds", double.NaN);
        double playing = ReadDouble(snapshot, "ValidPlayingSeconds", double.NaN);
        double active = ReadDouble(snapshot, "ActiveKiloJoules", double.NaN);
        double gross = ReadDouble(snapshot, "GrossKiloJoules", double.NaN);
        bool finite = IsFiniteTraining(equivalent) && IsFiniteTraining(playing) &&
            IsFiniteTraining(active) && IsFiniteTraining(gross) && equivalent >= 0 && playing >= 0 && active >= 0 && gross >= 0;
        energyRuntimeSnapshotFinite = firstSnapshot ? finite : energyRuntimeSnapshotFinite && finite;
        if (energyRuntimeLastEquivalent >= 0 && equivalent + 0.0001 < energyRuntimeLastEquivalent)
            energyRuntimeSnapshotMonotonic = false;
        if (energyRuntimeLastPlaying >= 0 && playing + 0.0001 < energyRuntimeLastPlaying)
            energyRuntimeSnapshotMonotonic = false;
        if (energyRuntimeLastEquivalent >= 0 && equivalent > energyRuntimeLastEquivalent + 0.0001)
            energyRuntimeSnapshotAdvanced = true;
        if (energyRuntimeLastPlaying >= 0 && playing > energyRuntimeLastPlaying + 0.0001)
            energyRuntimeSnapshotAdvanced = true;
        energyRuntimeLastEquivalent = equivalent;
        energyRuntimeLastPlaying = playing;
        energyRuntimeHistoryStable &= HistoryTotalsEqualBaseline();
    }

    void FinishEnergyRuntime()
    {
        if (!energyRuntimeCaptured) return;
        Type recorderType = FindTrainingType("ActivityEnergyCounter.EnergyRecorder");
        object recorder = GetStaticMember(recorderType, "Current");
        Type storeType = FindTrainingType("ActivityEnergyCounter.EnergySnapshotStore");
        object snapshot = ReadRecorderMember(recorderType, recorder, "CurrentSnapshot") ??
            GetStaticMember(storeType, "Latest");
        string currentPlayId = Convert.ToString(ReadRecorderMember(recorderType, recorder, "CurrentPlayId"), CultureInfo.InvariantCulture) ?? string.Empty;
        Check("training-energy-recorder-current-instance", energyRuntimeRecorderPresent &&
            recorder != null,
            "Current=" + (recorder == null ? "null" : recorder.GetType().FullName));
        Check("training-energy-snapshot-seen", energyRuntimeSnapshotSeen && snapshot != null,
            "CurrentSnapshot was not published during gameplay.");
        long currentSession = ReadLong(GetStaticMember(FindTrainingType("ActivityEnergyCounter.EnergySnapshotStore"), "CurrentSession"), long.MinValue);
        Check("training-energy-new-session-snapshot-isolated", snapshot != null && currentSession == trainingEnergySession,
            "CurrentSnapshot/session=" + (snapshot == null ? "missing" : "present") + "; " + currentSession + "/" + trainingEnergySession);
        Check("training-energy-snapshot-finite", energyRuntimeSnapshotFinite,
            "equivalent=" + energyRuntimeLastEquivalent + "; playing=" + energyRuntimeLastPlaying);
        Check("training-energy-snapshot-monotonic", energyRuntimeSnapshotMonotonic,
            "equivalent=" + energyRuntimeLastEquivalent + "; playing=" + energyRuntimeLastPlaying);
        Check("training-energy-snapshot-advanced", energyRuntimeSnapshotAdvanced,
            "observations=" + trainingEnergyObservations + "; playing=" + energyRuntimeLastPlaying);
        Check("training-energy-play-id-stable", energyRuntimePlayStable &&
            (string.IsNullOrEmpty(energyRuntimePlayId) || currentPlayId == energyRuntimePlayId),
            "captured=" + energyRuntimePlayId + "; current=" + currentPlayId);
        Check("training-energy-tracking-mode", !string.IsNullOrEmpty(energyRuntimeTrackingMode),
            "TrackingMode=" + energyRuntimeTrackingMode);
        Check("training-energy-fpfc-simulation-marked", useVr || energyRuntimeSimulated,
            "useVr=" + useVr + "; simulated=" + energyRuntimeSimulated);
        Check("training-energy-history-not-contaminated-by-simulation", !energyRuntimeSimulated ||
            (energyRuntimeHistoryStable && HistoryTotalsEqualBaseline()),
            "simulated=" + energyRuntimeSimulated + "; historyStable=" + energyRuntimeHistoryStable);
        Check("training-energy-history-no-error", string.IsNullOrEmpty(ReadMemberString(energyRuntimeHistory, "LastError")),
            "LastError=" + ReadMemberString(energyRuntimeHistory, "LastError"));
        VerifyEnergyDisplayCombinations(snapshot);
    }

    void VerifyEnergyRuntimeResults(object view)
    {
        if (!energyRuntimeCaptured || energyRuntimeResultsChecked) return;
        energyRuntimeResultsChecked = true;
        Type recorderType = FindTrainingType("ActivityEnergyCounter.EnergyRecorder");
        object recorder = GetStaticMember(recorderType, "Current");
        Type storeType = FindTrainingType("ActivityEnergyCounter.EnergySnapshotStore");
        object snapshot = ReadRecorderMember(recorderType, recorder, "CurrentSnapshot") ??
            GetStaticMember(storeType, "Latest");
        object history = GetStaticMember(FindTrainingType("ActivityEnergyCounter.Plugin"), "History");
        object session = GetMember(history, "SessionTotals");
        object today = GetMember(history, "TodayTotals");
        object settings = GetStaticMember(FindTrainingType("ActivityEnergyCounter.Plugin"), "DisplaySettings") ??
            GetStaticMember(FindTrainingType("ActivityEnergyCounter.Plugin"), "Settings");
        if (snapshot == null || session == null || today == null || settings == null) return;
        MethodInfo format = FindEnergyDisplayMethod(FindTrainingType("ActivityEnergyCounter.EnergyDisplay"), "FormatResults");
        if (format == null) return;
        string first = InvokeDisplay(format, snapshot, session, today, settings);
        string second = InvokeDisplay(format, snapshot, session, today, settings);
        Check("training-energy-results-format-stable", first != null && first == second,
            "first=" + first + "; second=" + second);
        Check("training-energy-results-snapshot-session", ReadLong(GetStaticMember(storeType, "CurrentSession"), long.MinValue) == trainingEnergySession,
            "captured=" + trainingEnergySession + "; current=" + GetStaticMember(FindTrainingType("ActivityEnergyCounter.EnergySnapshotStore"), "CurrentSession"));
        VerifyEnergyResultsSetDataStable(view, history);
    }

    void VerifyEnergyResultsSetDataStable(object view, object history)
    {
        if (view == null || history == null) return;
        MethodInfo setData = AccessTools.Method(view.GetType(), "SetDataToUI");
        if (setData == null || setData.GetParameters().Length != 0) return;
        TMP_Text label = FindEnergyResultsLabel(view);
        if (label == null) return;
        Transform root = GetMember(view, "transform") as Transform;
        RectTransform panel = root?.Find("Container/BottomPanel") as RectTransform;
        Vector2 beforeButtons = panel == null ? Vector2.zero : panel.anchoredPosition;
        string beforeText = label.text;
        double beforeSessionActive = ReadTotalsValue(history, "SessionTotals", "ActiveKiloJoules");
        double beforeSessionGross = ReadTotalsValue(history, "SessionTotals", "GrossKiloJoules");
        double beforeTodayActive = ReadTotalsValue(history, "TodayTotals", "ActiveKiloJoules");
        double beforeTodayGross = ReadTotalsValue(history, "TodayTotals", "GrossKiloJoules");
        try
        {
            setData.Invoke(view, null);
            setData.Invoke(view, null);
            TMP_Text after = FindEnergyResultsLabel(view);
            bool labelStable = after != null && after.gameObject.activeInHierarchy && after.enabled &&
                string.Equals(beforeText, after.text, StringComparison.Ordinal);
            bool historyStable = EqualEnergy(beforeSessionActive, ReadTotalsValue(history, "SessionTotals", "ActiveKiloJoules")) &&
                EqualEnergy(beforeSessionGross, ReadTotalsValue(history, "SessionTotals", "GrossKiloJoules")) &&
                EqualEnergy(beforeTodayActive, ReadTotalsValue(history, "TodayTotals", "ActiveKiloJoules")) &&
                EqualEnergy(beforeTodayGross, ReadTotalsValue(history, "TodayTotals", "GrossKiloJoules"));
            Check("training-energy-results-setdata-stable", labelStable && historyStable,
                "labelStable=" + labelStable + "; historyStable=" + historyStable +
                "; before=" + beforeText + "; after=" + (after == null ? "missing" : after.text));
            if (after != null && panel != null)
            {
                after.ForceMeshUpdate();
                Vector3 labelBottom = root.InverseTransformPoint(after.transform.TransformPoint(
                    new Vector3(0, after.textBounds.min.y, 0)));
                Vector3 panelTop = root.InverseTransformPoint(panel.TransformPoint(
                    new Vector3(0, panel.rect.yMax, 0)));
                Check("training-energy-results-above-buttons", labelBottom.y >= panelTop.y + 0.4f,
                    "energy bottom=" + labelBottom.y + "; button panel top=" + panelTop.y);
                RectTransform levelBar = root.Find("Container/LevelBarSimple") as RectTransform;
                Check("training-energy-results-song-bar-found", levelBar != null, "Native song information bounds are available.");
                float songTop = root.InverseTransformPoint(levelBar.TransformPoint(new Vector3(0, levelBar.rect.yMax, 0))).y;
                Check("training-energy-results-above-song", labelBottom.y >= songTop + 0.4f,
                    "energy bottom=" + labelBottom.y + "; song top=" + songTop);
                Check("training-energy-results-buttons-stable", Vector2.Distance(beforeButtons, panel.anchoredPosition) < 0.001f,
                    "before=" + beforeButtons + "; after=" + panel.anchoredPosition);
                RectTransform viewRect = root as RectTransform;
                float buttonBottom = root.InverseTransformPoint(panel.TransformPoint(new Vector3(0, panel.rect.yMin, 0))).y;
                Check("training-energy-results-buttons-in-view", viewRect != null && buttonBottom >= viewRect.rect.yMin + 0.4f,
                    "button bottom=" + buttonBottom + "; view bottom=" + (viewRect == null ? "missing" : viewRect.rect.yMin.ToString()));
                object display = GetStaticMember(FindTrainingType("ActivityEnergyCounter.Plugin"), "DisplaySettings");
                double scale = ReadDouble(display, "ResultsFontScale", 1);
                double minimum = Math.Min(4, 4.5 * scale);
                Check("training-energy-results-font-readable", after.fontSize >= minimum - 0.05,
                    "font size=" + after.fontSize + "; scale=" + scale);
            }
        }
        catch (Exception error)
        {
            Check("training-energy-results-setdata-stable", false,
                error.GetType().Name + ": " + error.Message);
        }
    }

    static TMP_Text FindEnergyResultsLabel(object view)
    {
        Transform root = GetMember(view, "transform") as Transform;
        Transform result = root?.Find("ActivityEnergyCounter.ResultEnergy");
        return result == null ? null : result.GetComponentInChildren<TMP_Text>(true);
    }

    IEnumerator VerifyEnergyResultsVariants()
    {
        if (!trainingEnergyLoaded) yield break;
        Type viewType = FindTrainingType("ResultsViewController");
        object view = Resources.FindObjectsOfTypeAll(viewType).OfType<HMUI.ViewController>()
            .First(value => value.isActivated && value.gameObject.activeInHierarchy);
        Type pluginType = FindTrainingType("ActivityEnergyCounter.Plugin");
        object original = GetStaticMember(pluginType, "DisplaySettings");
        object probe = CloneEnergySettings(original.GetType(), original);
        MethodInfo setData = AccessTools.Method(view.GetType(), "SetDataToUI");
        Transform root = GetMember(view, "transform") as Transform;
        RectTransform panel = root.Find("Container/BottomPanel") as RectTransform;
        Vector2 visibleButtons = panel.anchoredPosition;
        string originalText = FindEnergyResultsLabel(view).text;
        try
        {
            SetMember(probe, "ResultsShowSong", true); SetMember(probe, "ResultsShowSession", true); SetMember(probe, "ResultsShowToday", true);
            SetMember(probe, "ResultsFontScale", 1.5);
            Check("training-energy-results-variants-apply", SetStaticMember(pluginType, "DisplaySettings", probe), "DisplaySettings is writable.");
            setData.Invoke(view, null);
            yield return null;
            yield return new WaitForEndOfFrame();
            VerifyEnergyResultsSetDataStable(view, GetStaticMember(pluginType, "History"));
            CaptureResultsImage(Path.Combine(directory, "results-all-scopes-large.png"));

            SetMember(probe, "ResultsShowSong", false); SetMember(probe, "ResultsShowSession", false); SetMember(probe, "ResultsShowToday", false);
            setData.Invoke(view, null);
            yield return null;
            TMP_Text hidden = FindEnergyResultsLabel(view);
            Check("training-energy-results-hidden-keeps-buttons", hidden != null && !hidden.gameObject.activeSelf &&
                Vector2.Distance(panel.anchoredPosition, visibleButtons) < 0.001f, "hidden=" + (hidden != null && !hidden.gameObject.activeSelf) + "; buttons=" + panel.anchoredPosition);

            Type overlay = FindTrainingType("ActivityEnergyCounter.EnergyResultsOverlay");
            AccessTools.Method(overlay, "Clear").Invoke(null, new[] { view });
            object snapshot = GetStaticMember(FindTrainingType("ActivityEnergyCounter.EnergySnapshotStore"), "Latest");
            AccessTools.Method(overlay, "Remember").Invoke(null, new[] { view, snapshot });
            SetStaticMember(pluginType, "DisplaySettings", original);
            setData.Invoke(view, null);
            yield return null;
            Check("training-energy-results-reopen-keeps-native-layout", Vector2.Distance(panel.anchoredPosition, visibleButtons) < 0.001f &&
                FindEnergyResultsLabel(view).text == originalText, "buttons=" + panel.anchoredPosition + "; expected=" + visibleButtons);
        }
        finally
        {
            SetStaticMember(pluginType, "DisplaySettings", original);
            setData.Invoke(view, null);
        }
    }

    void VerifyEnergyResultsPixels(Texture2D pixels, Camera camera, string captureName)
    {
        if (!trainingEnergyLoaded) return;
        TMP_Text label = Resources.FindObjectsOfTypeAll<TMP_Text>().FirstOrDefault(value =>
            value != null && value.name == "ActivityEnergyCounter.ResultEnergy" && value.gameObject.activeInHierarchy);
        if (label == null) return;
        TMP_TextInfo info = label.textInfo;
        const string separator = "   |   ";
        string[] parts = label.text.Split(new[] { separator }, StringSplitOptions.None);
        var items = new List<object>();
        var visiblePixels = new List<int>();
        int nextCharacter = 0;
        for (int item = 0; item < parts.Length; item++)
        {
            int start = nextCharacter, end = start + parts[item].Length;
            nextCharacter = end + separator.Length;
            var glyphs = info.characterInfo.Take(info.characterCount)
                .Where(character => character.isVisible && character.index >= start && character.index < end).ToArray();
            if (glyphs.Length == 0) continue;
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
            foreach (TMP_CharacterInfo glyph in glyphs)
                foreach (Vector3 corner in new[] { glyph.bottomLeft, glyph.topRight })
                {
                    Vector3 point = camera.WorldToViewportPoint(label.transform.TransformPoint(corner));
                    minX = Mathf.Min(minX, point.x * pixels.width); maxX = Mathf.Max(maxX, point.x * pixels.width);
                    minY = Mathf.Min(minY, point.y * pixels.height); maxY = Mathf.Max(maxY, point.y * pixels.height);
                }
            int left = Mathf.Clamp(Mathf.FloorToInt(minX), 0, pixels.width - 1);
            int right = Mathf.Clamp(Mathf.CeilToInt(maxX), 0, pixels.width - 1);
            int bottom = Mathf.Clamp(Mathf.FloorToInt(minY), 0, pixels.height - 1);
            int top = Mathf.Clamp(Mathf.CeilToInt(maxY), 0, pixels.height - 1);
            int bright = 0;
            for (int y = bottom; y <= top; y++)
                for (int x = left; x <= right; x++)
                {
                    Color color = pixels.GetPixel(x, y);
                    if (color.r > 0.6f && color.g > 0.6f && color.b > 0.6f) bright++;
                }
            items.Add(new { item, text = parts[item], left, right, bottom, top, brightPixels = bright });
            visiblePixels.Add(bright);
        }
        File.WriteAllText(Path.Combine(directory, "energy-" + captureName + "-pixels.json"),
            Newtonsoft.Json.JsonConvert.SerializeObject(new { label.text, label.fontSize, items }, Newtonsoft.Json.Formatting.Indented));
        Check("training-energy-" + captureName + "-rendered-items", visiblePixels.Count == parts.Length,
            "rendered items=" + visiblePixels.Count + "; text items=" + parts.Length);
        for (int item = 0; item < visiblePixels.Count; item++)
            Check("training-energy-" + captureName + "-visible-item-" + item, visiblePixels[item] >= 30,
                "bright text pixels=" + visiblePixels[item]);
    }

    void VerifyEnergyDisplayCombinations(object snapshot)
    {
        if (energyRuntimeDisplayChecked) return;
        energyRuntimeDisplayChecked = true;
        Type displayType = FindTrainingType("ActivityEnergyCounter.EnergyDisplay");
        Type settingsType = FindTrainingType("ActivityEnergyCounter.EnergySettings");
        object history = GetStaticMember(FindTrainingType("ActivityEnergyCounter.Plugin"), "History");
        object session = GetMember(history, "SessionTotals");
        object today = GetMember(history, "TodayTotals");
        object settings = GetStaticMember(FindTrainingType("ActivityEnergyCounter.Plugin"), "DisplaySettings") ??
            GetStaticMember(FindTrainingType("ActivityEnergyCounter.Plugin"), "Settings");
        MethodInfo hud = FindEnergyDisplayMethod(displayType, "FormatHud");
        MethodInfo results = FindEnergyDisplayMethod(displayType, "FormatResults");
        if (snapshot == null || session == null || today == null || settings == null || hud == null || results == null || settingsType == null) return;

        object probe = null;
        try { probe = Activator.CreateInstance(settingsType, true); }
        catch { }
        if (probe == null) return;
        CopyReadableSettings(settings, probe, new[] {
            "Enabled", "UseHeadTracking", "HudShowSong", "HudShowSession", "HudShowToday",
            "ResultsShowSong", "ResultsShowSession", "ResultsShowToday", "HudShowDuration",
            "ResultsShowDuration", "ShowLabels", "ShowEstimate", "DecimalPlaces", "HudFontScale", "ResultsFontScale",
            "HudPrimaryScope"
        });
        SetMember(probe, "Enabled", true);
        SetMember(probe, "HudShowSong", true);
        SetMember(probe, "HudShowSession", true);
        SetMember(probe, "HudShowToday", true);
        SetMember(probe, "ResultsShowSong", true);
        SetMember(probe, "ResultsShowSession", true);
        SetMember(probe, "ResultsShowToday", true);
        SetMember(probe, "HudShowDuration", true);
        SetMember(probe, "ResultsShowDuration", true);
        SetMember(probe, "ShowLabels", true);
        SetMember(probe, "ShowEstimate", true);
        SetMember(probe, "DecimalPlaces", 2);
        string hudText = InvokeDisplay(hud, snapshot, session, today, probe);
        string resultsText = InvokeDisplay(results, snapshot, session, today, probe);
        Check("training-energy-display-custom-scopes", !string.IsNullOrEmpty(hudText) && !string.IsNullOrEmpty(resultsText),
            "hud=" + hudText + "; results=" + resultsText);

        SetMember(probe, "HudShowSong", false);
        SetMember(probe, "HudShowSession", false);
        SetMember(probe, "HudShowToday", false);
        SetMember(probe, "HudShowDuration", false);
        string hiddenScopes = InvokeDisplay(hud, snapshot, session, today, probe);
        Check("training-energy-display-scope-isolation", hiddenScopes != hudText,
            "all HUD scopes toggled without changing the source snapshot.");
    }

    bool HistoryTotalsEqualBaseline()
    {
        if (energyRuntimeHistory == null) return false;
        return EqualEnergy(energyRuntimeSessionActive, ReadTotalsValue(energyRuntimeHistory, "SessionTotals", "ActiveKiloJoules")) &&
            EqualEnergy(energyRuntimeSessionGross, ReadTotalsValue(energyRuntimeHistory, "SessionTotals", "GrossKiloJoules")) &&
            EqualEnergy(energyRuntimeTodayActive, ReadTotalsValue(energyRuntimeHistory, "TodayTotals", "ActiveKiloJoules")) &&
            EqualEnergy(energyRuntimeTodayGross, ReadTotalsValue(energyRuntimeHistory, "TodayTotals", "GrossKiloJoules"));
    }

    static bool EqualEnergy(double left, double right)
    {
        if (double.IsNaN(left) || double.IsNaN(right)) return false;
        return Math.Abs(left - right) <= 0.000001;
    }

    static bool TotalsEqual(object first, object second, double tolerance)
    {
        return first != null && second != null &&
            Close(ReadDouble(first, "ActiveKiloJoules", double.NaN), ReadDouble(second, "ActiveKiloJoules", double.NaN), tolerance) &&
            Close(ReadDouble(first, "GrossKiloJoules", double.NaN), ReadDouble(second, "GrossKiloJoules", double.NaN), tolerance) &&
            Close(ReadDouble(first, "EquivalentSeconds", double.NaN), ReadDouble(second, "EquivalentSeconds", double.NaN), tolerance) &&
            Close(ReadDouble(first, "ValidPlayingSeconds", double.NaN), ReadDouble(second, "ValidPlayingSeconds", double.NaN), tolerance);
    }

    static bool TotalsEqualScaled(object actual, object expected, double scale, double tolerance)
    {
        return actual != null && expected != null &&
            Close(ReadDouble(actual, "ActiveKiloJoules", double.NaN), ReadDouble(expected, "ActiveKiloJoules", double.NaN) * scale, tolerance) &&
            Close(ReadDouble(actual, "GrossKiloJoules", double.NaN), ReadDouble(expected, "GrossKiloJoules", double.NaN) * scale, tolerance) &&
            Close(ReadDouble(actual, "EquivalentSeconds", double.NaN), ReadDouble(expected, "EquivalentSeconds", double.NaN) * scale, tolerance) &&
            Close(ReadDouble(actual, "ValidPlayingSeconds", double.NaN), ReadDouble(expected, "ValidPlayingSeconds", double.NaN) * scale, tolerance);
    }

    static bool TotalsEqualZero(object totals, double tolerance)
    {
        return totals != null && Close(ReadDouble(totals, "ActiveKiloJoules", double.NaN), 0, tolerance) &&
            Close(ReadDouble(totals, "GrossKiloJoules", double.NaN), 0, tolerance) &&
            Close(ReadDouble(totals, "EquivalentSeconds", double.NaN), 0, tolerance) &&
            Close(ReadDouble(totals, "ValidPlayingSeconds", double.NaN), 0, tolerance);
    }

    static bool Close(double actual, double expected, double tolerance)
    {
        return IsFiniteTraining(actual) && IsFiniteTraining(expected) && Math.Abs(actual - expected) <= tolerance;
    }

    static MethodInfo FindEnergyDisplayMethod(Type type, string name)
    {
        if (type == null) return null;
        return type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == name && method.GetParameters().Length == 4 && method.ReturnType == typeof(string));
    }

    static PropertyInfo GetReadableMember(Type type, string name)
    {
        return type?.GetProperty(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    }

    static bool HasReadableStaticMember(Type type, string name)
    {
        if (type == null) return false;
        PropertyInfo property = type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        FieldInfo field = type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        return (property != null && property.GetIndexParameters().Length == 0 && property.CanRead) || field != null;
    }

    static bool HasReadableRecorderMember(Type type, string name)
    {
        if (type == null) return false;
        PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        return (property != null && property.GetIndexParameters().Length == 0 && property.CanRead) || field != null;
    }

    static object ReadRecorderMember(Type type, object recorder, string name)
    {
        object instance = GetMember(recorder, name);
        return instance ?? GetStaticMember(type, name);
    }

    static double ReadTotalsValue(object history, string totalsName, string memberName)
    {
        return ReadDouble(GetMember(history, totalsName), memberName, double.NaN);
    }

    static string ReadMemberString(object value, string name)
    {
        object member = GetMember(value, name);
        return member == null ? null : Convert.ToString(member, CultureInfo.InvariantCulture);
    }

    static string InvokeDisplay(MethodInfo method, object snapshot, object session, object today, object settings)
    {
        try { return Convert.ToString(method.Invoke(null, new[] { snapshot, session, today, settings }), CultureInfo.InvariantCulture); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }

    static void CopyReadableSettings(object source, object target, IEnumerable<string> names)
    {
        foreach (string name in names)
        {
            object value = GetMember(source, name);
            if (value != null) SetMember(target, name, value);
        }
    }

    static bool SetMember(object target, string name, object value)
    {
        if (target == null) return false;
        Type type = target.GetType();
        PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        try
        {
            if (property != null && property.CanWrite)
            {
                object converted = ConvertEnergyValue(value, property.PropertyType);
                property.SetValue(target, converted, null);
                return true;
            }
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && !field.IsInitOnly)
            {
                field.SetValue(target, ConvertEnergyValue(value, field.FieldType));
                return true;
            }
        }
        catch { }
        return false;
    }

    static bool SetStaticMember(Type type, string name, object value)
    {
        if (type == null) return false;
        try
        {
            PropertyInfo property = type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite)
            {
                property.SetValue(null, ConvertEnergyValue(value, property.PropertyType), null);
                return true;
            }
            FieldInfo field = type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && !field.IsInitOnly)
            {
                field.SetValue(null, ConvertEnergyValue(value, field.FieldType));
                return true;
            }
        }
        catch { }
        return false;
    }

    static object ConvertEnergyValue(object value, Type type)
    {
        if (value == null) return null;
        if (type.IsInstanceOfType(value)) return value;
        if (type.IsEnum)
        {
            if (value is string text && Enum.TryParse(type, text, true, out object parsed)) return parsed;
            return Enum.ToObject(type, Convert.ToInt32(value, CultureInfo.InvariantCulture));
        }
        return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
    }
}
