using System;
using System.Collections.Generic;
using LocalTraining;
using SiraUtil.Tools.FPFC;
using UnityEngine;
using UnityEngine.XR;
using Zenject;

namespace ActivityEnergyCounter
{
    internal sealed class GameInstaller : Installer
    {
        public override void InstallBindings()
        {
            Container.Bind<EnergyRecorder>().FromNewComponentOnNewGameObject()
                .WithGameObjectName("ActivityEnergyCounter.Recorder").AsSingle().NonLazy();
        }
    }

    // Owns gameplay sampling and completion. Counters+ owns only the optional HUD.
    public sealed class EnergyRecorder : MonoBehaviour
    {
        public static EnergyRecorder Current { get; private set; }
        public EnergySnapshot CurrentSnapshot { get; private set; }
        public string CurrentPlayId { get; private set; }
        public bool IsSimulated { get; private set; }
        public string TrackingMode { get; private set; } = "Waiting for tracking";
        public bool Finished { get; private set; }

        private IGamePause pause;
        private GameplayCoreSceneSetupData setup;
        private AudioTimeSyncController audio;
        private SettingsApplicatorSO applicator;
        private StandardLevelGameplayManager gameplay;
        private IVRPlatformHelper platform;
        private IXRSystemState xrState;
        private IFPFCSettings fpfc;
        private SaberManager sabers;
        private readonly MotionIntegrator motion = new MotionIntegrator();
        private readonly List<XRInputSubsystem> inputSubsystems = new List<XRInputSubsystem>();
        private readonly List<XRInputSubsystem> discoveredSubsystems = new List<XRInputSubsystem>();
        private EnergyAccumulator accumulator;
        private EnergySettings calculationSettings;
        private string beatmapKey = "";
        private long ownerSession, sequence;
        private double nextPublish, nextSubsystemRefresh;
        private double clockAnchorMonotonic;
        private DateTimeOffset clockAnchorUtc, previousPoseUtc;
        private bool started, inputErrorReported, historyErrorReported;

        [Inject]
        private void Construct(GameplayCoreSceneSetupData data,
            [InjectOptional] IGamePause gamePause, [InjectOptional] AudioTimeSyncController timeSource,
            [InjectOptional] SettingsApplicatorSO roomSettings, [InjectOptional] StandardLevelGameplayManager manager,
            [InjectOptional] IVRPlatformHelper helper, [InjectOptional] IXRSystemState state,
            [InjectOptional] IFPFCSettings simulation, [InjectOptional] SaberManager saberManager)
        {
            setup = data; pause = gamePause; audio = timeSource; applicator = roomSettings;
            gameplay = manager; platform = helper; xrState = state; fpfc = simulation; sabers = saberManager;
            if (Current != null && Current != this) Current.Finish("quit");
            Current = this;
        }

        private void Start()
        {
            if (Finished) return;
            calculationSettings = Plugin.Settings;
            accumulator = new EnergyAccumulator(calculationSettings);
            beatmapKey = setup != null && setup.beatmapKey.IsValid() ? ChartKey.For(setup.beatmapKey) : "";
            ownerSession = EnergySnapshotStore.CurrentSession;
            IsSimulated = (fpfc != null && fpfc.Enabled) || !XRSettings.isDeviceActive;
            clockAnchorMonotonic = Time.realtimeSinceStartupAsDouble;
            clockAnchorUtc = previousPoseUtc = DateTimeOffset.UtcNow;
            if (!IsSimulated && Plugin.History != null)
            {
                try
                {
                    CurrentPlayId = Plugin.History.BeginPlay(beatmapKey, EnergyAccumulator.ModelVersion,
                        calculationSettings.BodyMassKg, calculationSettings.ReferenceHandSpeedMps, calculationSettings.CalibrationFactor);
                }
                catch (Exception error) { Plugin.Log?.Error("energy-history-begin: " + error); historyErrorReported = true; }
            }
            if (pause != null)
            {
                pause.didPauseEvent += ResetTracking; pause.willResumeEvent += ResetTracking; pause.didResumeEvent += ResetTracking;
            }
            if (applicator != null) applicator.roomTransformOffsetDidUpdateEvent += ResetTracking;
            if (gameplay != null) { gameplay.levelFinishedEvent += Completed; gameplay.levelFailedEvent += Failed; }
            if (xrState != null) xrState.AddListener(FocusChanged, XRSystemEventType.InputFocusLost,
                XRSystemEventType.VRFocusLost, XRSystemEventType.ControllersDidDisconnect);
            InputDevices.deviceConnected += DeviceChanged; InputDevices.deviceDisconnected += DeviceChanged;
            RefreshSubsystems();
            started = true;
            Publish();
            Plugin.Log?.Info("Energy recorder started; model=" + EnergyAccumulator.ModelVersion + "; simulated=" + IsSimulated);
        }

        private void LateUpdate()
        {
            if (!started || Finished || Current != this) return;
            try
            {
                if (Time.realtimeSinceStartupAsDouble >= nextSubsystemRefresh)
                {
                    RefreshSubsystems(); nextSubsystemRefresh = Time.realtimeSinceStartupAsDouble + 1;
                }
                bool paused = !Plugin.DisplaySettings.Enabled || (pause != null && pause.isPaused)
                    || (audio != null && audio.state != IAudioTimeSource.State.Playing)
                    || (!IsSimulated && xrState != null && (!xrState.hasInputFocus || !xrState.hasVrFocus || xrState.IsAppFocusCurrentlyLost()));
                MotionFrame frame = new MotionFrame { Timestamp = Time.realtimeSinceStartupAsDouble };
                DateTimeOffset poseUtc = clockAnchorUtc.AddTicks((long)Math.Round(
                    (frame.Timestamp - clockAnchorMonotonic) * TimeSpan.TicksPerSecond));
                if (!paused)
                {
                    if (IsSimulated) ReadSimulation(ref frame);
                    else
                    {
                        frame.HeadTracked = calculationSettings.UseHeadTracking && ReadNode(XRNode.Head, out frame.Head);
                        frame.LeftTracked = ReadNode(XRNode.LeftHand, out frame.Left);
                        frame.RightTracked = ReadNode(XRNode.RightHand, out frame.Right);
                    }
                }
                MotionFeatures features;
                EnergyTotals delta;
                if (motion.TryAdvance(frame, paused, out features) && accumulator.AddSample(features, out delta))
                {
                    TrackingMode = features.TrackingMode;
                    if (!IsSimulated && !string.IsNullOrEmpty(CurrentPlayId))
                    {
                        bool recorded = Plugin.History.AddInterval(CurrentPlayId, ++sequence, previousPoseUtc, poseUtc, delta);
                        if (!recorded && !historyErrorReported)
                        {
                            Plugin.Log?.Error("energy-history-interval: " + Plugin.History.LastError);
                        }
                        historyErrorReported = !recorded;
                    }
                }
                else if (!paused && !frame.HeadTracked && !frame.LeftTracked && !frame.RightTracked)
                    TrackingMode = "Waiting for tracking";
                previousPoseUtc = poseUtc;
                if (Time.realtimeSinceStartupAsDouble >= nextPublish)
                {
                    Publish(); nextPublish = Time.realtimeSinceStartupAsDouble + 0.1;
                }
                inputErrorReported = false;
            }
            catch (Exception error)
            {
                motion.Reset();
                if (!inputErrorReported) { Plugin.Log?.Error("energy-sampling: " + error); inputErrorReported = true; }
                TrackingMode = "Tracking unavailable";
            }
        }

        private bool ReadNode(XRNode node, out MotionPoint result)
        {
            result = default(MotionPoint);
            InputDevice device = InputDevices.GetDeviceAtXRNode(node);
            bool tracked;
            InputTrackingState tracking;
            if (!device.isValid || !device.TryGetFeatureValue(CommonUsages.isTracked, out tracked) || !tracked
                || !device.TryGetFeatureValue(CommonUsages.trackingState, out tracking) || (tracking & InputTrackingState.Position) == 0) return false;
            Vector3 position;
            Quaternion rotation;
            if (platform == null || !platform.GetNodePose(node, 0, out position, out rotation)) return false;
            result = new MotionPoint(position.x, position.y, position.z);
            return result.IsFinite;
        }

        private void ReadSimulation(ref MotionFrame frame)
        {
            // Simulation is visible for lifecycle checks, never added to history.
            if (sabers == null || sabers.leftSaber == null || sabers.rightSaber == null) return;
            Vector3 left = sabers.leftSaber.handlePos, right = sabers.rightSaber.handlePos;
            frame.Left = new MotionPoint(left.x, left.y, left.z); frame.LeftTracked = true;
            frame.Right = new MotionPoint(right.x, right.y, right.z); frame.RightTracked = true;
        }
        private void ResetTracking() { motion.Reset(); Publish(); Plugin.History?.RequestSave(); }
        private void FocusChanged(XRSystemEventType _) { ResetTracking(); }
        private void DeviceChanged(InputDevice _) { RefreshSubsystems(); ResetTracking(); }
        private void OriginChanged(XRInputSubsystem _) { ResetTracking(); }
        private void RefreshSubsystems()
        {
            discoveredSubsystems.Clear(); SubsystemManager.GetInstances(discoveredSubsystems);
            for (int i = inputSubsystems.Count - 1; i >= 0; i--)
                if (!discoveredSubsystems.Contains(inputSubsystems[i]))
                {
                    inputSubsystems[i].trackingOriginUpdated -= OriginChanged;
                    inputSubsystems.RemoveAt(i); motion.Reset();
                }
            foreach (XRInputSubsystem subsystem in discoveredSubsystems)
                if (!inputSubsystems.Contains(subsystem))
                {
                    subsystem.trackingOriginUpdated += OriginChanged;
                    inputSubsystems.Add(subsystem); motion.Reset();
                }
        }
        private void Completed() { Finish("completed"); }
        private void Failed() { Finish("failed"); }
        private void Publish()
        {
            if (accumulator == null) return;
            CurrentSnapshot = accumulator.CreateSnapshot(beatmapKey, TrackingMode, IsSimulated);
            EnergySnapshotStore.Publish(CurrentSnapshot, ownerSession);
        }
        public void Finish(string outcome)
        {
            if (Finished) return;
            Finished = true; motion.Reset(); Publish();
            if (!string.IsNullOrEmpty(CurrentPlayId)) { Plugin.History?.EndPlay(CurrentPlayId, outcome); Plugin.History?.RequestSave(); }
            Plugin.RaiseStatisticsChanged();
        }
        private void OnApplicationQuit() { Finish("quit"); }
        private void OnDestroy()
        {
            Finish("quit");
            if (pause != null) { pause.didPauseEvent -= ResetTracking; pause.willResumeEvent -= ResetTracking; pause.didResumeEvent -= ResetTracking; }
            if (applicator != null) applicator.roomTransformOffsetDidUpdateEvent -= ResetTracking;
            if (gameplay != null) { gameplay.levelFinishedEvent -= Completed; gameplay.levelFailedEvent -= Failed; }
            if (xrState != null) xrState.RemoveListener(FocusChanged);
            InputDevices.deviceConnected -= DeviceChanged; InputDevices.deviceDisconnected -= DeviceChanged;
            foreach (XRInputSubsystem subsystem in inputSubsystems) subsystem.trackingOriginUpdated -= OriginChanged;
            inputSubsystems.Clear();
            if (Current == this) Current = null;
        }
    }
}
