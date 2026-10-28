using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using IPA;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Rendering;
using CustomJSONData.CustomBeatmap;

namespace EffectModsSmoke;

[Plugin(RuntimeOptions.SingleStartInit)]
public sealed class Plugin
{
    GameObject owner;
    [OnStart] public void Start()
    {
        if (!Environment.GetCommandLineArgs().Contains("--effect-mods-smoke")) return;
        owner = new GameObject("EffectModsSmoke");
        UnityEngine.Object.DontDestroyOnLoad(owner); owner.AddComponent<Runner>();
    }
    [OnExit] public void Exit() { if (owner != null) UnityEngine.Object.Destroy(owner); }
}

public sealed partial class Runner : MonoBehaviour
{
    readonly List<object> checks = new();
    readonly List<string> errors = new();
    readonly System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
    readonly string targetId = Environment.GetEnvironmentVariable("EFFECT_SMOKE_LEVEL");
    bool CustomLevel => targetId.StartsWith("custom_level_", StringComparison.Ordinal);
    readonly string directory = Environment.GetEnvironmentVariable("EFFECT_SMOKE_DIR");
    readonly float duration = float.Parse(Environment.GetEnvironmentVariable("EFFECT_SMOKE_SECONDS") ?? "30", System.Globalization.CultureInfo.InvariantCulture);
    readonly bool practice = Environment.GetEnvironmentVariable("EFFECT_SMOKE_PRACTICE") == "1";
    readonly float practiceSpeed = float.Parse(Environment.GetEnvironmentVariable("EFFECT_SMOKE_PRACTICE_SPEED") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
    readonly string characteristic = Environment.GetEnvironmentVariable("EFFECT_SMOKE_CHARACTERISTIC");
    readonly string difficulty = Environment.GetEnvironmentVariable("EFFECT_SMOKE_DIFFICULTY");
    readonly bool assetAuditOnly = Environment.GetEnvironmentVariable("EFFECT_SMOKE_ASSETS") == "1";
    readonly bool reload = Environment.GetEnvironmentVariable("EFFECT_SMOKE_RELOAD") == "1";
    readonly bool useVr = Environment.GetEnvironmentVariable("EFFECT_SMOKE_VR") == "1";
    readonly bool hideAvatar = Environment.GetEnvironmentVariable("EFFECT_SMOKE_HIDE_AVATAR") == "1";
    readonly bool verifyAudio = Environment.GetEnvironmentVariable("EFFECT_SMOKE_VERIFY_AUDIO") == "1";
    readonly float[] audioSamples = new float[1024];
    float songAudioPeak;
    string songAudioClip;
    string phase;
    bool done;
    BeatmapObjectManager manager;
    int notes, obstacles;
    object modifiers;
    System.Reflection.FieldInfo noFail;
    bool originalNoFail;
    float maximumTime;
    JObject expectedData;
    JObject expectedLightshowData;
    bool CustomBeatmapExpected => CustomLevel && !((string)expectedData?["version"] ?? string.Empty).StartsWith("4.", StringComparison.Ordinal);
    string expectedPath;
    byte[] originalChartHash;
    RenderTexture offscreen;
    bool captured;
    float audioLinkPeak;
    bool audioLinkFinite = true;
    long maximumPasses, maximumBlits;
    float grabPeak;
    long grabCaptures;
    long maximumDepthRenders;
    float secondaryDepthRange;
    int nextCapture = 12;
    void Start() { InitializeShutdownTrace(); InitializeLocalCompatibilityChecks(); Application.logMessageReceived += OnLog; StartCoroutine(Drive(Run())); }
    void Update() { if (!done && timer.Elapsed.TotalSeconds > (Environment.GetEnvironmentVariable("EFFECT_SMOKE_FULL_MOD_UI_AUDIT") == "1" ? 1180 : (duration > 0 ? duration / practiceSpeed + 150 : 740 / practiceSpeed) + (Environment.GetEnvironmentVariable("EFFECT_SMOKE_SETTINGS_NAVIGATION") == "1" ? 120 : 0) + (Environment.GetEnvironmentVariable("EFFECT_SMOKE_ADDITIONAL_SETTINGS_NAVIGATION") == "1" ? 180 : 0))) Finish("Finite deadline exceeded: " + phase); }
    void OnLog(string message, string stack, LogType kind)
    {
        if (message.Contains("BoxCollider does not support negative scale or size")) negativeColliderWarnings++;
        if (kind != LogType.Exception && kind != LogType.Error && kind != LogType.Assert) return;
        string text = message + "\n" + stack;
        if (new[] {"CustomJSONData", "NoodleExtensions", "Heck.", "Chroma.", "Vivify.", "AudioLink", "NoteMotionControl", "ActivityEnergy", "ModUiChinese", "RoomCalibration", "PlaylistManager", "MorePlaylists", "SongChartVisualizer", "UnityShutdownFix", "RenderPass:", "EndRenderPass:", "GrabPass", "RenderGraph", "Releasing render texture that is set as Camera.targetTexture"}.Any(text.Contains) && !errors.Contains(text)) errors.Add(text);
    }
    static T Active<T>() where T : Component => Resources.FindObjectsOfTypeAll<T>().FirstOrDefault(x => x != null && x.gameObject.scene.IsValid() && x.gameObject.activeInHierarchy);
    static IEnumerable<string> Parents(Transform transform) { while (transform != null) { yield return transform.name; transform = transform.parent; } }
    void Check(string name, bool passed, string detail)
    {
        checks.Add(new {name, passed, detail}); if (!passed) throw new InvalidOperationException(name + ": " + detail);
    }
    IEnumerator Drive(IEnumerator routine)
    {
        var stack = new Stack<IEnumerator>(); stack.Push(routine);
        while (!done && stack.Count > 0)
        {
            bool next = false; object current = null;
            try { next = stack.Peek().MoveNext(); if (next) current = stack.Peek().Current; }
            catch (Exception error) { Finish(error.ToString()); yield break; }
            if (!next) { stack.Pop(); continue; }
            if (current is IEnumerator child) { stack.Push(child); continue; }
            yield return current;
        }
        if (!done) Finish(null);
    }
    IEnumerator Wait(Func<bool> predicate, float seconds, string label)
    {
        phase = label; double deadline = timer.Elapsed.TotalSeconds + seconds;
        File.WriteAllText(Path.Combine(directory, "progress.json"), JsonConvert.SerializeObject(new {phase, elapsed=timer.Elapsed.TotalSeconds}));
        while (timer.Elapsed.TotalSeconds < deadline) { if (predicate()) yield break; yield return null; }
        throw new InvalidOperationException("Timed out: " + label);
    }
    IEnumerator Run()
    {
        if(useVr)yield return Wait(()=>UnityEngine.XR.XRSettings.isDeviceActive,15,"active-VR-headset");
        yield return Wait(() => Active<MainMenuViewController>() != null && Active<MainMenuViewController>().isActivated, 85, "main-menu");
        Check("native-main-menu", true, SceneManager.GetActiveScene().name);
        if(useVr)Check("active-VR-headset",UnityEngine.XR.XRSettings.isDeviceActive && Resources.FindObjectsOfTypeAll<Camera>().Any(camera=>camera.enabled&&camera.stereoEnabled),"isDeviceActive="+UnityEngine.XR.XRSettings.isDeviceActive+"; device="+UnityEngine.XR.XRSettings.loadedDeviceName);
        int patches = Harmony.GetAllPatchedMethods().Count(m => Harmony.GetPatchInfo(m).Owners.Contains("aeroluna.CustomJSONData"));
        Check("custom-json-patches", patches > 10, patches + " patched methods");
        Check("Noodle-capability", SongCore.Collections.capabilities.Contains("Noodle Extensions"), String.Join(",", SongCore.Collections.capabilities));
        Check("Chroma-capability", SongCore.Collections.capabilities.Contains("Chroma"), String.Join(",", SongCore.Collections.capabilities));
        VerifyV4LightshowConversion();
        VerifyUnityShutdownGuard();
        PrepareTrainingScenario();
        if (Environment.GetEnvironmentVariable("EFFECT_SMOKE_ADDITIONAL_SETTINGS_NAVIGATION") == "1")
        {
            yield return VerifyAdditionalSettingsNavigation();
            yield return VerifyIndependentSettings();
            if (trainingNoteLoaded && trainingScenario != "Default")
                Check("additional-navigation-restored-speed-scenario", ScenarioNoteMatches(
                    GetMember(GetStaticMember(FindTrainingType("NoteMotionControl.RuntimeState"), "Store"), "Global"), trainingScenario),
                    "The committed speed scenario survived all settings edits and the native menu reload.");
        }
        if (Environment.GetEnvironmentVariable("EFFECT_SMOKE_SETTINGS_NAVIGATION") == "1")
            yield return VerifyTrainingSettingsNavigation();
        if(Environment.GetEnvironmentVariable("EFFECT_SMOKE_MENU_ONLY")=="1") {
            VerifyTrainingMenu();
            if (Environment.GetEnvironmentVariable("EFFECT_SMOKE_TRAINING_UI") == "1") yield return VerifyTrainingUi();
            Check("no-training-menu-errors", errors.Count==0, String.Join("\n",errors)); yield break;
        }
        BeatmapLevel level = null;
        BeatmapLevelsModel levels = null;
        yield return Wait(() => {
            if (!SongCore.Loader.AreSongsLoaded || SongCore.Loader.AreSongsLoading) return false;
            levels = Resources.FindObjectsOfTypeAll<Zenject.SceneContext>().First(c=>c.gameObject.scene.name=="MainMenu").Container.Resolve<BeatmapLevelsModel>();
            level = CustomLevel ? SongCore.Loader.GetLevelById(targetId) : levels.GetBeatmapLevel(targetId,false);
            return level != null;
        },45,"target-level-available");
        var initialPack = CustomLevel ? SongCore.Loader.CustomLevelsPack : levels.GetLevelPackForLevelId(targetId);
        Check("target-native-pack-available", initialPack != null, "target="+targetId+"; pack="+initialPack?.packID);
        var initialKey = level.GetBeatmapKeys()
            .Where(k => String.IsNullOrEmpty(characteristic) || SongCore.Collections.GetCurrentCharacteristicSerializedName(k.characteristic) == characteristic)
            .Where(k => String.IsNullOrEmpty(difficulty) || k.difficulty.ToString() == difficulty)
            .OrderBy(k => SongCore.Collections.GetCurrentCharacteristicSerializedName(k.characteristic) == "Standard" ? 0 : 1)
            .ThenBy(k => k.difficulty).First();
        var solo = Resources.FindObjectsOfTypeAll<SoloFreePlayFlowCoordinator>().First();
        solo.Setup(new LevelSelectionFlowCoordinator.State(SelectLevelCategoryViewController.LevelCategory.All, initialPack, in initialKey, level));
        ((Button)AccessTools.Field(typeof(MainMenuViewController), "_soloButton").GetValue(Active<MainMenuViewController>())).onClick.Invoke();
        yield return Wait(() =>
        {
            if(!SongCore.Loader.AreSongsLoaded || SongCore.Loader.AreSongsLoading || Active<LevelCollectionTableView>()==null)return false;
            level=CustomLevel?SongCore.Loader.GetLevelById(targetId):Resources.FindObjectsOfTypeAll<Zenject.SceneContext>().First(c=>c.gameObject.scene.name=="MainMenu").Container.Resolve<BeatmapLevelsModel>().GetBeatmapLevel(targetId,false);
            return level!=null;
        }, 45, "level-loaded");
        yield return new WaitForSecondsRealtime(2f);
        AuditLibrary();
        if(assetAuditOnly){yield return AuditBundles();yield break;}
        if(CustomLevel)yield return CacheIsolation(level);
        yield return Wait(() => Active<StandardLevelDetailView>() != null && Active<StandardLevelDetailView>().beatmapKey.levelId == targetId && Active<StandardLevelDetailView>().actionButton.interactable, 30, "play-enabled");
        var view = Active<StandardLevelDetailView>();
        if (!String.IsNullOrEmpty(characteristic) && SongCore.Collections.GetCurrentCharacteristicSerializedName(view.beatmapKey.characteristic) != characteristic)
        {
            var controller=(BeatmapCharacteristicSegmentedControlController)AccessTools.Field(typeof(StandardLevelDetailView),"_beatmapCharacteristicSegmentedControlController").GetValue(view);
            var available=(List<BeatmapCharacteristic>)AccessTools.Field(typeof(BeatmapCharacteristicSegmentedControlController),"_currentlyAvailableBeatmapCharacteristics").GetValue(controller);
            int index=available.FindIndex(c=>SongCore.Collections.GetCurrentCharacteristicSerializedName(c)==characteristic);
            Check("requested-characteristic-available",index>=0,String.Join(",",available.Select(SongCore.Collections.GetCurrentCharacteristicSerializedName)));
            AccessTools.Method(typeof(BeatmapCharacteristicSegmentedControlController),"HandleBeatmapCharacteristicSegmentedControlDidSelectCell").Invoke(controller,new object[]{AccessTools.Field(typeof(BeatmapCharacteristicSegmentedControlController),"_segmentedControl").GetValue(controller),index});
            yield return Wait(()=>SongCore.Collections.GetCurrentCharacteristicSerializedName(view.beatmapKey.characteristic)==characteristic,15,"requested-characteristic-selected");
        }
        var preferred = level.GetBeatmapKeys()
            .Where(k => string.IsNullOrEmpty(difficulty) || k.difficulty.ToString() == difficulty)
            .Where(k => k.characteristic == view.beatmapKey.characteristic)
            .Where(k => !CustomLevel || SongCore.Collections.GetCustomLevelSongDifficultyData(k)?.additionalDifficultyData._requirements.All(SongCore.Collections.capabilities.Contains) == true)
            .OrderByDescending(k => SongCore.Collections.GetCustomLevelSongDifficultyData(k)?.additionalDifficultyData._requirements.Length ?? 0)
            .ThenByDescending(k => CustomLevel ? (int)k.difficulty : k.difficulty==BeatmapDifficulty.Hard?100:(int)k.difficulty).First();
        var difficultyController = (BeatmapDifficultySegmentedControlController)AccessTools.Field(typeof(StandardLevelDetailView), "_beatmapDifficultySegmentedControlController").GetValue(view);
        var segmentedControl = AccessTools.Field(typeof(BeatmapDifficultySegmentedControlController), "_difficultySegmentedControl").GetValue(difficultyController);
        var difficulties = (List<BeatmapDifficulty>)AccessTools.Field(typeof(BeatmapDifficultySegmentedControlController), "_difficulties").GetValue(difficultyController);
        if (!view.beatmapKey.Equals(preferred))
            AccessTools.Method(typeof(BeatmapDifficultySegmentedControlController), "HandleDifficultySegmentedControlDidSelectCell").Invoke(difficultyController, new object[] {segmentedControl, difficulties.IndexOf(preferred.difficulty)});
        yield return Wait(() => view.beatmapKey.Equals(preferred) && view.actionButton.interactable, 30, "required-difficulty-selected");
        yield return new WaitForSecondsRealtime(0.5f);
        var key = view.beatmapKey;
        if(CustomLevel)
        {
        var saved = ((Dictionary<string, CustomLevelLoader.LoadedSaveData>)AccessTools.Field(typeof(CustomLevelLoader), "_loadedBeatmapSaveData").GetValue(SongCore.Loader.CustomLevelLoader))[key.levelId];
        var info = JObject.Parse(saved.customLevelFolderInfo.levelInfoJsonString);
        string sourceCharacteristic = SongCore.Collections.GetCurrentCharacteristicSerializedName(key.characteristic);
        string sourceFile;
        string sourceLightshowFile = null;
        if (info["difficultyBeatmaps"] is JArray modernCharts)
        {
            var sourceChart = modernCharts.First(chart => (string)chart["characteristic"] == sourceCharacteristic &&
                (string)chart["difficulty"] == key.difficulty.ToString());
            sourceFile = (string)sourceChart["beatmapDataFilename"];
            sourceLightshowFile = (string)sourceChart["lightshowDataFilename"];
        }
        else
            sourceFile = (string)info["_difficultyBeatmapSets"].First(set => (string)set["_beatmapCharacteristicName"] == sourceCharacteristic)
                ["_difficultyBeatmaps"].First(diff => (string)diff["_difficulty"] == key.difficulty.ToString())["_beatmapFilename"];
        expectedPath = Path.Combine(saved.customLevelFolderInfo.folderPath, sourceFile);
        // An independent oracle must read the original file, never the cache under test.
        var ioType = AccessTools.TypeByName("SongCore.Hooks.BeatmapLevelCache.IOBlacklistHook") ?? AccessTools.TypeByName("SongCore.Hooks.BeatmapLevelCache.IOBlacklistHooks");
        var io = (System.Threading.AsyncLocal<bool>)AccessTools.Property(ioType, "AllowIO").GetValue(null);
        bool previousIO = io.Value;
        try {
            io.Value = true;
            byte[] original = File.ReadAllBytes(expectedPath);
            using (var sha = System.Security.Cryptography.SHA256.Create()) originalChartHash = sha.ComputeHash(original);
            expectedData = JObject.Parse(System.Text.Encoding.UTF8.GetString(original).TrimStart('\uFEFF'));
            if (!string.IsNullOrEmpty(sourceLightshowFile))
                expectedLightshowData = JObject.Parse(File.ReadAllText(Path.Combine(saved.customLevelFolderInfo.folderPath, sourceLightshowFile)).TrimStart('\uFEFF'));
        }
        finally { io.Value = previousIO; }
        }
        else { expectedData=new JObject();expectedPath="builtin:"+key; }
        Check("selected-difficulty-exists", level.GetBeatmapKeys().Contains(key), key.ToString());
        var data = SongCore.Collections.GetCustomLevelSongDifficultyData(key);
        string[] requirements=data?.additionalDifficultyData._requirements??Array.Empty<string>();
        Check("selected-difficulty-requirements", (!CustomLevel || data != null) && requirements.All(SongCore.Collections.capabilities.Contains), String.Join(",",requirements));
        Check("native-play-button", view.actionButton.interactable, key.ToString());
        Check("native-practice-button", ((Button)AccessTools.Field(typeof(StandardLevelDetailView), "_practiceButton").GetValue(view)).interactable, key.ToString());
        var panel = Active<GameplayModifiersPanelController>();
        modifiers = AccessTools.Field(typeof(GameplayModifiersPanelController), "_gameplayModifiers").GetValue(panel);
        noFail = AccessTools.Field(modifiers.GetType(), "_noFailOn0Energy"); originalNoFail = (bool)noFail.GetValue(modifiers); noFail.SetValue(modifiers, true);
        if (practice)
        {
            ((Button)AccessTools.Field(typeof(StandardLevelDetailView), "_practiceButton").GetValue(view)).onClick.Invoke();
            yield return Wait(() => Active<PracticeViewController>() != null && ((Button)AccessTools.Field(typeof(PracticeViewController), "_playButton").GetValue(Active<PracticeViewController>())).interactable, 30, "practice-menu");
            var practiceView = Active<PracticeViewController>();
            var practiceSettings = (PracticeSettings)AccessTools.Field(typeof(PracticeViewController), "_practiceSettings").GetValue(practiceView);
            practiceSettings.startSongTime = 0; practiceSettings.songSpeedMul = practiceSpeed;
            ((HMUI.PercentSlider)AccessTools.Field(typeof(PracticeViewController), "_speedSlider").GetValue(practiceView)).value = practiceSpeed;
            ((HMUI.TimeSlider)AccessTools.Field(typeof(PracticeViewController), "_songStartSlider").GetValue(practiceView)).value = 0;
            ((Button)AccessTools.Field(typeof(PracticeViewController), "_playButton").GetValue(practiceView)).onClick.Invoke();
        }
        else view.actionButton.onClick.Invoke();
        yield return Wait(() =>
        {
            if (errors.Count > 0) throw new InvalidOperationException("Gameplay initialization failed: " + errors[0]);
            var settingsType = AccessTools.TypeByName("Heck.SettingsSetter.SettingsSetterViewController");
            var settings = Resources.FindObjectsOfTypeAll(settingsType).OfType<HMUI.ViewController>().FirstOrDefault(v => v.isActivated && v.gameObject.activeInHierarchy);
            if (settings != null) AccessTools.Method(settingsType, "OnAcceptClick").Invoke(settings, null);
            return Active<AudioTimeSyncController>() != null && SceneManager.GetSceneByName("GameCore").isLoaded;
        }, 60, "gameplay-entry");
        var context = Resources.FindObjectsOfTypeAll<Zenject.SceneContext>().First(x => x.gameObject.scene.name.Contains("GameCore") && x.gameObject.activeInHierarchy);
        if (practice)
        {
            var setup = context.Container.Resolve<GameplayCoreSceneSetupData>();
            Check("practice-effective-speed", setup.practiceSettings != null && Math.Abs(setup.practiceSettings.songSpeedMul - practiceSpeed) < 0.001f,
                "requested=" + practiceSpeed + "; actual=" + setup.practiceSettings?.songSpeedMul);
        }
        if (trainingScenario == "Percentage" || trainingScenario == "Fixed") yield return VerifyTrainingRestart(context, value => context = value);
        CaptureShutdownOwner(context);
        CaptureLocalCompatibility(context);
        yield return VerifyLocalCompatibility(context);
        var beatmap = context.Container.Resolve<IReadonlyBeatmapData>();
        Check("beatmap-data-type", CustomBeatmapExpected ? beatmap is CustomBeatmapData : beatmap is BeatmapData, beatmap.GetType().FullName);
        VerifyV4LoadedEvents(beatmap);
        if (beatmap is CustomBeatmapData custom)
        {
            var expectedEvents = (JArray)(expectedData["customData"]?["customEvents"] ?? expectedData["_customData"]?["_customEvents"] ?? new JArray());
            var types = expectedEvents.Select(e => (string)(e["t"] ?? e["_type"])).OrderBy(t => t).ToArray();
            var actualTypes = custom.customEventDatas.Select(e => e.eventType).OrderBy(t => t).ToArray();
            Check("selected-chart-events-preserved", types.SequenceEqual(actualTypes), expectedPath + ": expected " + types.Length + "; loaded " + actualTypes.Length);
            File.WriteAllText(Path.Combine(directory, "loaded-custom-data.json"), JsonConvert.SerializeObject(custom.customData, Formatting.Indented));
        }
        else if (CustomLevel)
        {
            var sourceEvents = (JArray)(expectedData["customData"]?["customEvents"] ?? expectedData["_customData"]?["_customEvents"] ?? new JArray());
            Check("selected-chart-native-data-has-no-custom-events", sourceEvents.Count == 0,
                "Native v4 data is allowed only when no authored custom events would be lost.");
        }
        manager = context.Container.Resolve<BeatmapObjectManager>();
        CaptureTrainingGameplay(context, beatmap);
        if (trainingScenario == "Fixed") yield return VerifyTrainingPause(context);
        if (beatmap is CustomBeatmapData wallBeatmap) SetupWallRegression(context, wallBeatmap);
        if(reload){yield return new WaitForSecondsRealtime(2f);yield return ReloadCheck(context,beatmap);}
        manager.noteWasSpawnedEvent += OnNote; manager.obstacleWasSpawnedEvent += OnObstacle;
        var audio = Active<AudioTimeSyncController>();
        File.WriteAllText(Path.Combine(directory, "render-state.json"), JsonConvert.SerializeObject(new {
            featurePatches=Harmony.GetPatchInfo(AccessTools.Method(typeof(MainEffectRendererFeature), "AddRenderPasses"))?.Postfixes.Select(p=>p.PatchMethod.DeclaringType.FullName + "." + p.PatchMethod.Name),
            cameras=Resources.FindObjectsOfTypeAll<Camera>().Where(c=>c.gameObject.scene.IsValid()).Select(c=>new {c.name,c.enabled,c.stereoEnabled,c.cullingMask,active=c.gameObject.activeInHierarchy,scene=c.gameObject.scene.name,path=String.Join("/",Parents(c.transform).Reverse()),target=c.targetTexture==null?null:new{c.targetTexture.name,c.targetTexture.graphicsFormat,c.targetTexture.depthStencilFormat},components=c.GetComponents<Component>().Where(x=>x!=null).Select(x=>x.GetType().FullName)}),
            skyComponents=Resources.FindObjectsOfTypeAll<Transform>().Where(t=>t.gameObject.scene.IsValid() && (t.name=="StarSky" || t.name=="Aurora")).Select(t=>new {t.name,components=t.GetComponentsInChildren<Component>(true).Where(x=>x!=null).Select(x=>x.GetType().FullName)}),
            vivifyActive=AccessTools.Property(AccessTools.TypeByName("Vivify.HarmonyPatches.CameraEffectApplier"),"Active").GetValue(null)?.GetType().FullName
        },Formatting.Indented));
        // Exercise camera fixtures while the gameplay context still owns them.
        // A natural results transition can destroy these cameras before reporting.
        if(requirements.Contains("Vivify"))
        {
            yield return null;
            var fixtureType=AccessTools.TypeByName("Vivify.PostProcessing.PostProcessingController");
            var fixtureProcessors=Resources.FindObjectsOfTypeAll(fixtureType).Where(x=>x!=null).ToArray();
            DepthCameraGpuCheck();
            OrderedGrabGpuCheck(context);
            if(fixtureProcessors.Length>0){StereoArrayCopy(fixtureType,fixtureProcessors.First(),1);StereoArrayCopy(fixtureType,fixtureProcessors.First(),4);}
        }
        float expected = duration > 0 ? Math.Min(duration, level.songDuration - 0.5f) : level.songDuration - 0.5f;
        double deadline = timer.Elapsed.TotalSeconds + (duration > 0 ? duration : level.songDuration) / practiceSpeed + 25;
        phase = "song-playing";
        File.WriteAllText(Path.Combine(directory, "progress.json"), JsonConvert.SerializeObject(new { phase, elapsed=timer.Elapsed.TotalSeconds }));
        while ((audio = Active<AudioTimeSyncController>()) != null && timer.Elapsed.TotalSeconds < deadline)
        {
            maximumTime = Math.Max(maximumTime, audio.songTime);
            if (verifyAudio && Time.frameCount % 15 == 0)
            {
                var source = (AudioSource)AccessTools.Field(typeof(AudioTimeSyncController), "_audioSource").GetValue(audio);
                if (source != null && source.isPlaying && source.clip != null)
                {
                    songAudioClip = source.clip.name;
                    source.GetOutputData(audioSamples, 0);
                    songAudioPeak = Math.Max(songAudioPeak, audioSamples.Max(sample => Math.Abs(sample)));
                }
            }
            ObserveTrainingGameplay(audio.songTime);
            CaptureTrainingHudIfReady(audio.songTime);
            ObserveWallRegression(audio.songTime);
            if (Time.frameCount % 3 == 0)
            {
                var camera = Resources.FindObjectsOfTypeAll<Camera>().First(c => c.name == "MainCamera" && c.gameObject.activeInHierarchy && c.gameObject.scene.name != "MainMenu");
                if (offscreen == null) { offscreen = new RenderTexture(640, 360, 24, RenderTextureFormat.ARGBHalf); offscreen.Create(); }
                RenderForCapture(camera);
                var processorType=AccessTools.TypeByName("Vivify.PostProcessing.PostProcessingController");
                var processorsNow=Resources.FindObjectsOfTypeAll(processorType).Where(p=>p!=null).ToArray();
                maximumPasses=Math.Max(maximumPasses,processorsNow.Sum(p=>(long)AccessTools.Property(processorType,"RenderPassCount").GetValue(p)));
                maximumBlits=Math.Max(maximumBlits,processorsNow.Sum(p=>(long)AccessTools.Property(processorType,"MaterialBlitCount").GetValue(p)));
                var depthType=AccessTools.TypeByName("Vivify.PostProcessing.DepthOnlyCameraController");
                maximumDepthRenders=Math.Max(maximumDepthRenders,Resources.FindObjectsOfTypeAll(depthType).Where(p=>p!=null).Sum(p=>(long)AccessTools.Property(depthType,"RenderCount").GetValue(p)));
                if(Time.frameCount%60==0 && secondaryDepthRange<0.001f)
                {
                    var secondaryType=AccessTools.TypeByName("Vivify.PostProcessing.SecondaryCameraController");
                    foreach(var controller in Resources.FindObjectsOfTypeAll(secondaryType).Where(p=>p!=null))
                        foreach(RenderTexture depth in ((IDictionary)AccessTools.Property(secondaryType,"RenderTexturesDepth").GetValue(controller)).Values)
                            secondaryDepthRange=Math.Max(secondaryDepthRange,SampleDepthRange(depth));
                }
                if (maximumTime > 3f)
                    foreach(var processor in processorsNow)
                    {
                        var grabType=AccessTools.TypeByName("Vivify.PostProcessing.LegacyGrabRenderer");
                        foreach(var grabber in Resources.FindObjectsOfTypeAll(grabType).Where(g=>g!=null))
                        {
                            grabCaptures=Math.Max(grabCaptures,(long)AccessTools.Property(grabType,"Captures").GetValue(grabber));
                            if(grabPeak==0&&Time.frameCount%60==0)
                                foreach(RenderTexture grab in ((IDictionary)AccessTools.Field(grabType,"Textures").GetValue(grabber)).Values)grabPeak=Math.Max(grabPeak,SampleTexturePeak(grab));
                        }
                    }
                if (requirements.Contains("AudioLink") && maximumTime > 3f && Time.frameCount % 30 == 0) SampleAudioLink();
                if (maximumTime >= Math.Min(nextCapture, expected) && (!captured || maximumTime>=nextCapture))
                {
                    captured = true;
                    var previous = RenderTexture.active; RenderTexture.active = offscreen;
                    var pixels = new Texture2D(offscreen.width, offscreen.height, TextureFormat.RGB24, false);
                    pixels.ReadPixels(new Rect(0,0,pixels.width,pixels.height),0,0); pixels.Apply();
                    SaveOpaqueScreenshot(pixels, Path.Combine(directory,nextCapture==12?"frame.png":"frame-"+nextCapture+".png"));
                    nextCapture=nextCapture==12?40:nextCapture==40?90:nextCapture==90?140:Int32.MaxValue;
                    UnityEngine.Object.Destroy(pixels); RenderTexture.active = previous;
                }
            }
            if (duration > 0 && maximumTime >= expected) break;
            yield return null;
        }
        FinishTrainingGameplay();
        if (CustomLevel && !reload)
        {
            var ioType = AccessTools.TypeByName("SongCore.Hooks.BeatmapLevelCache.IOBlacklistHook") ?? AccessTools.TypeByName("SongCore.Hooks.BeatmapLevelCache.IOBlacklistHooks");
            var io = (System.Threading.AsyncLocal<bool>)AccessTools.Property(ioType, "AllowIO").GetValue(null);
            bool previous = io.Value;
            byte[] current;
            try { io.Value = true; using(var sha=System.Security.Cryptography.SHA256.Create()) current=sha.ComputeHash(File.ReadAllBytes(expectedPath)); }
            finally { io.Value = previous; }
            Check("source-chart-file-unchanged", originalChartHash != null && originalChartHash.SequenceEqual(current), expectedPath);
        }
        Check("song-time-advances", maximumTime >= expected, maximumTime + " / " + expected + " seconds");
        Check("native-notes-spawn", notes > 0, notes + " notes; " + obstacles + " walls");
        if (verifyAudio) Check("native-song-audio-output", songAudioPeak > 0.0001f, "clip=" + songAudioClip + "; peak=" + songAudioPeak);
        if (requirements.Contains("AudioLink"))
        {
            Check("AudioLink-live-GPU-spectrum", audioLinkPeak>0.0001f && audioLinkFinite, "128x4 bands sampled during song; peak="+audioLinkPeak);
        }
        if (requirements.Contains("Vivify"))
        {
            var type = AccessTools.TypeByName("Vivify.PostProcessing.PostProcessingController");
            var processors = Resources.FindObjectsOfTypeAll(type).Where(x => x != null).ToArray();
            long passes = processors.Sum(x => (long)AccessTools.Property(type, "RenderPassCount").GetValue(x));
            long blits = processors.Sum(x => (long)AccessTools.Property(type, "MaterialBlitCount").GetValue(x));
            passes=Math.Max(passes,maximumPasses);blits=Math.Max(blits,maximumBlits);
            long calls = (long)AccessTools.Property(AccessTools.TypeByName("Vivify.HarmonyPatches.AddComponentsToCamera"), "Calls").GetValue(null);
            var sourceEvents = (JArray)(expectedData["customData"]?["customEvents"] ?? new JArray());
            bool needsBlit = sourceEvents.Any(e => (string)e["t"] == "Blit" && (float)e["b"] * 60f / level.beatsPerMinute <= maximumTime);
            Check("URP-material-passes", !needsBlit || (passes > 0 && blits > 0), calls+ " native feature calls; " + processors.Length + " processors; " + passes + " render passes; " + blits + " material blits");
            var grabNames=(string[])context.Container.Resolve(AccessTools.TypeByName("Vivify.Managers.AssetBundleManager")).GetType().GetProperty("GrabTextures",AccessTools.all).GetValue(context.Container.Resolve(AccessTools.TypeByName("Vivify.Managers.AssetBundleManager")));
            // A bundle can contain grab shaders that its opening scene never uses.
            if(grabNames.Length>0 && (duration==0 || grabCaptures>0))Check("URP-authored-grab-captures",grabCaptures>0,String.Join(",",grabNames)+"; captures="+grabCaptures+"; peak="+grabPeak);
            if(maximumDepthRenders>0)Check("prefab-depth-camera-rendering",true,maximumDepthRenders+" authored camera frames");
            bool needsSecondaryDepth=sourceEvents.Any(e=>(string)e["t"]=="CreateCamera" && e["d"]?["depthTexture"]!=null && (float)e["b"]*60f/level.beatsPerMinute<maximumTime);
            if(needsSecondaryDepth)Check("secondary-camera-depth-GPU-content",secondaryDepthRange>0.001f,"sampled depth range="+secondaryDepthRange);
        }
        if(duration==0)
        {
            var resultsType=AccessTools.TypeByName("ResultsViewController");
            yield return Wait(()=>Resources.FindObjectsOfTypeAll(resultsType).OfType<HMUI.ViewController>().Any(view=>view.isActivated),15,"native-song-results");
            Check("native-full-song-results",true,"Song completed and native results screen activated.");
            VerifyTrainingResults();
            yield return new WaitForSecondsRealtime(2f);
            yield return new WaitForEndOfFrame();
            string resultsCapture = Path.Combine(directory, "results.png");
            CaptureResultsImage(resultsCapture);
            yield return VerifyEnergyResultsVariants();
        }
        if (Active<PauseController>() != null && !quitFromGameplay)
        {
            Active<PauseController>().Pause(); yield return new WaitForSecondsRealtime(1f);
            var pause = Active<PauseMenuManager>();
            ((Button)AccessTools.Field(typeof(PauseMenuManager), "_backButton").GetValue(pause)).onClick.Invoke();
            yield return Wait(() => !SceneManager.GetSceneByName("GameCore").isLoaded && (Active<StandardLevelDetailView>() != null || Active<PracticeViewController>() != null), 30, "return-to-song-menu");
            Check("scene-disposal-and-menu-return", true, practice ? "practice-menu" : "song-menu");
            yield return new WaitForSecondsRealtime(2f);
            CheckShutdownOwnerDisposed();
        }
        FinishWallRegression();
        CaptureShutdownTargets("before-quit");
        Check("no-effect-mod-exceptions", errors.Count == 0, String.Join("\n", errors));
    }
    void OnNote(NoteController _) { notes++; }
    void CaptureResultsImage(string path)
    {
        var camera = Resources.FindObjectsOfTypeAll<Camera>().First(value => value != null && value.name == "MenuMainCamera" && value.gameObject.activeInHierarchy);
        var previousTexture = offscreen;
        var previousActive = RenderTexture.active;
        float previousFov = camera.fieldOfView;
        var target = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);
        var pixels = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
        try
        {
            target.Create();
            offscreen = target;
            camera.fieldOfView = 60;
            RenderForCapture(camera);
            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0);
            pixels.Apply();
            Check("results-camera-image", pixels.GetPixels32().Any(pixel => pixel.r > 20 || pixel.g > 20 || pixel.b > 20), "Results camera produced visible pixels");
            SaveOpaqueScreenshot(pixels, path);
            CaptureLocalizationSnapshot(path);
            VerifyEnergyResultsPixels(pixels, camera, Path.GetFileNameWithoutExtension(path));
        }
        finally
        {
            camera.fieldOfView = previousFov;
            RenderTexture.active = previousActive;
            offscreen = previousTexture;
            target.Release();
            UnityEngine.Object.Destroy(target);
            UnityEngine.Object.Destroy(pixels);
        }
    }
    void SaveOpaqueScreenshot(Texture2D pixels, string path)
    {
        // URP's final color target can have zero alpha even on visible UI.
        // Desktop screenshots must omit that compositor-specific channel.
        byte[] png = pixels.EncodeToPNG();
        Check("screenshot-opaque-" + Path.GetFileNameWithoutExtension(path),
            png.Length > 25 && png[25] == 2,
            "PNG color type=" + (png.Length > 25 ? png[25].ToString() : "missing") + "; expected RGB without alpha");
        File.WriteAllBytes(path, png);
    }
    void RenderForCapture(Camera camera)
    {
        var avatarType=AccessTools.TypeByName("CustomAvatar.Avatar.SpawnedAvatar");
        var renderers=hideAvatar&&avatarType!=null
            ? Resources.FindObjectsOfTypeAll(avatarType).OfType<Component>().SelectMany(avatar=>avatar.GetComponentsInChildren<Renderer>(true)).Distinct().ToArray()
            : Array.Empty<Renderer>();
        var previous=renderers.Select(renderer=>renderer.forceRenderingOff).ToArray();
        try
        {
            foreach(var renderer in renderers)renderer.forceRenderingOff=true;
            RenderPipeline.SubmitRenderRequest(camera,new RenderPipeline.StandardRequest{destination=offscreen});
        }
        finally{for(int index=0;index<renderers.Length;index++)if(renderers[index]!=null)renderers[index].forceRenderingOff=previous[index];}
    }
    IEnumerator ReloadCheck(Zenject.SceneContext context,IReadonlyBeatmapData beatmap)
    {
        var type=AccessTools.TypeByName("Heck.ReLoad.ReLoader");var reloader=context.Container.Resolve(type);
        byte[] original=null;ChartIO(()=>original=File.ReadAllBytes(expectedPath));
        DateTime originalTime=File.GetLastWriteTimeUtc(expectedPath);
        File.WriteAllText(Path.Combine(directory,"reload-chart-time.txt"),originalTime.ToString("O"));
        File.WriteAllBytes(Path.Combine(directory,"reload-chart-original.bin"),original);
        File.WriteAllText(Path.Combine(directory,"reload-chart-path.txt"),Path.GetFullPath(expectedPath));
        var changed=JObject.Parse(System.Text.Encoding.UTF8.GetString(original));
        string key=changed["_version"]!=null?"_customData":"customData";
        if(changed[key]==null)changed[key]=new JObject();changed[key]["effectReloadCheck"]="fresh-from-disk";
        try
        {
            ChartIO(()=>File.WriteAllText(expectedPath,changed.ToString(Formatting.None)));
            var gc=UnityEngine.Scripting.GarbageCollector.GCMode;
            var task=(Task)AccessTools.Method(type,"Reload").Invoke(reloader,new object[]{false});
            yield return Wait(()=>task.IsCompleted,35,"hot-reload");task.GetAwaiter().GetResult();
            Check("hot-reload-fresh-file",((CustomBeatmapData)beatmap).customData.Get<string>("effectReloadCheck")=="fresh-from-disk","Edited chart was read through native loader and SongCore cache.");
            var pause=context.Container.Resolve<IGamePause>();Check("hot-reload-restores-running-state",!pause.isPaused,"Previously running gameplay resumed.");
            Check("hot-reload-GC-mode-restored",UnityEngine.Scripting.GarbageCollector.GCMode==gc,gc.ToString());
            pause.Pause();
            task=(Task)AccessTools.Method(type,"Reload").Invoke(reloader,new object[]{false});
            yield return Wait(()=>task.IsCompleted,35,"hot-reload-paused");task.GetAwaiter().GetResult();
            Check("hot-reload-preserves-paused-state",pause.isPaused,"Paused reload stayed paused.");pause.Resume();
            AccessTools.Field(type,"_songStartTime").SetValue(reloader,5f);
            task=(Task)AccessTools.Method(type,"Reload").Invoke(reloader,new object[]{true});
            yield return Wait(()=>task.IsCompleted,35,"hot-reload-saved-time");task.GetAwaiter().GetResult();
            float songTime=Active<AudioTimeSyncController>().songTime;
            Check("hot-reload-jumps-to-saved-time",songTime>=4.9f&&songTime<6.5f,"songTime="+songTime);
            var controllerType=AccessTools.TypeByName("Heck.Animation.Transform.TransformController");
            var controllers=Resources.FindObjectsOfTypeAll(controllerType).OfType<Component>().Where(controller=>controller.gameObject.scene.IsValid()&&controller.name.StartsWith("NoodlePlayerTransform")).ToArray();
            Check("hot-reload-reuses-player-controllers",controllers.All(controller=>controller.GetComponents(controllerType).Length==1),controllers.Length+" player transform controllers; no duplicates");
        }
        finally{ChartIO(()=>File.WriteAllBytes(expectedPath,original));File.SetLastWriteTimeUtc(expectedPath,originalTime);}
    }
    static void ChartIO(Action operation)
    {
        var type=AccessTools.TypeByName("SongCore.Hooks.BeatmapLevelCache.IOBlacklistHook");
        var gate=AccessTools.Property(type,"AllowIO").GetValue(null);var value=gate.GetType().GetProperty("Value");
        bool previous=(bool)value.GetValue(gate);value.SetValue(gate,true);
        try{operation();}finally{value.SetValue(gate,previous);}
    }
    void OrderedGrabGpuCheck(Zenject.SceneContext context)
    {
        var bundle=AssetBundle.LoadFromFile(Path.Combine(Path.GetDirectoryName(Application.dataPath),"scripts/effect_mods/Tests/Resources/LegacyGrabChecks2022"));
        var shaders=bundle.LoadAllAssets<Shader>();
        var solid=new Material(shaders.Single(s=>s.name=="Vivify/Checks/Solid"));
        var glass=new Material(shaders.Single(s=>s.name=="Vivify/Checks/Refract"));
        var applierType=AccessTools.TypeByName("Vivify.HarmonyPatches.CameraEffectApplier");
        var applier=AccessTools.Property(applierType,"Active").GetValue(null);
        var plansField=AccessTools.Field(applierType,"<GrabShaders>k__BackingField");
        var original=(Array)plansField.GetValue(applier);var planType=original.GetType().GetElementType();
        var plan=JsonConvert.DeserializeObject("{\"shader\":\"Vivify/Checks/Refract\",\"steps\":[{\"passIndex\":0},{\"passIndex\":-1,\"texture\":\"_EffectSmokeGrab\",\"perObject\":false},{\"passIndex\":1}]}",planType);
        AccessTools.Field(planType,"Asset").SetValue(plan,glass.shader);
        var otherPlan=JsonConvert.DeserializeObject(JsonConvert.SerializeObject(plan),planType);
        var otherMaterial=new Material(shaders.Single(shader=>shader.name=="Vivify/Checks/RefractOther"));
        AccessTools.Field(planType,"Asset").SetValue(otherPlan,otherMaterial.shader);
        var otherSteps=(Array)AccessTools.Field(planType,"steps").GetValue(otherPlan);
        AccessTools.Field(otherSteps.GetValue(1).GetType(),"perObject").SetValue(otherSteps.GetValue(1),true);
        var plans=Array.CreateInstance(planType,original.Length+2);Array.Copy(original,plans,original.Length);plans.SetValue(plan,original.Length);plans.SetValue(otherPlan,original.Length+1);plansField.SetValue(applier,plans);
        var root=new GameObject("EffectSmokeGrabCamera");var objects=new List<GameObject>();
        var target=new RenderTexture(32,32,24,RenderTextureFormat.ARGBHalf);target.Create();
        var materials=new List<Material>();
        try
        {
            var camera=root.AddComponent<Camera>();camera.enabled=false;camera.transform.position=new Vector3(9999,9999,9999);camera.orthographic=true;camera.orthographicSize=1;camera.nearClipPlane=1;camera.farClipPlane=10;camera.cullingMask=1<<30;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.blue;
            void Quad(float z,Material material){var obj=GameObject.CreatePrimitive(PrimitiveType.Quad);obj.layer=30;obj.transform.position=root.transform.position+Vector3.forward*z;obj.transform.localScale=new Vector3(2,2,2);obj.GetComponent<Renderer>().sharedMaterial=material;objects.Add(obj);}
            var red=new Material(solid);red.SetColor("_Color",new Color(1,0,0,0.5f));materials.Add(red);
            var green=new Material(solid);green.SetColor("_Color",new Color(0,1,0,0.5f));materials.Add(green);
            Quad(6,red);Quad(5,glass);Quad(4,green);Quad(3,glass);
            Color Read(){RenderPipeline.SubmitRenderRequest(camera,new RenderPipeline.StandardRequest{destination=target});var previous=RenderTexture.active;var image=new Texture2D(32,32,TextureFormat.RGBAFloat,false);try{RenderTexture.active=target;image.ReadPixels(new Rect(0,0,32,32),0,0);image.Apply();return image.GetPixel(16,16);}finally{RenderTexture.active=previous;UnityEngine.Object.Destroy(image);}}
            var named=Read();var grabber=camera.GetComponent(AccessTools.TypeByName("Vivify.PostProcessing.LegacyGrabRenderer"));
            long captures=(long)AccessTools.Property(grabber.GetType(),"Captures").GetValue(grabber);
            Check("named-grab-first-object-GPU",Math.Abs(named.r-0.5f)<0.06f && named.g<0.04f && Math.Abs(named.b-0.5f)<0.06f && captures==1,named+"; captures="+captures);
            var steps=(Array)AccessTools.Field(planType,"steps").GetValue(plan);AccessTools.Field(steps.GetValue(1).GetType(),"perObject").SetValue(steps.GetValue(1),true);
            var perObject=Read();long after=(long)AccessTools.Property(grabber.GetType(),"Captures").GetValue(grabber);
            Check("unnamed-grab-overlapping-transparency-GPU",Math.Abs(perObject.r-0.25f)<0.06f && Math.Abs(perObject.g-0.5f)<0.06f && Math.Abs(perObject.b-0.25f)<0.06f && after-captures==2,perObject+"; captures="+(after-captures));
            glass.SetColor("_Prefix",Color.white);var prefix=Read();
            Check("shader-pass-before-grab-GPU",prefix.r>0.95f&&prefix.g>0.95f&&prefix.b>0.95f,prefix.ToString());
            glass.SetColor("_Prefix",Color.clear);AccessTools.Field(steps.GetValue(1).GetType(),"perObject").SetValue(steps.GetValue(1),false);
            Quad(3.5f,otherMaterial);var mixed=Read();
            Check("named-grab-rebind-after-unnamed-GPU",Math.Abs(mixed.r-0.5f)<0.06f&&mixed.g<0.04f&&Math.Abs(mixed.b-0.5f)<0.06f,mixed.ToString());
        }
        finally
        {
            plansField.SetValue(applier,original);foreach(var obj in objects){obj.SetActive(false);UnityEngine.Object.Destroy(obj);}foreach(var material in materials)UnityEngine.Object.Destroy(material);
            UnityEngine.Object.Destroy(root);UnityEngine.Object.Destroy(solid);UnityEngine.Object.Destroy(glass);UnityEngine.Object.Destroy(otherMaterial);target.Release();UnityEngine.Object.Destroy(target);bundle.Unload(true);
        }
    }
    float SampleTexturePeak(RenderTexture target)
    {
        var previous=RenderTexture.active;var pixels=new Texture2D(target.width,target.height,TextureFormat.RGBAFloat,false);
        try{RenderTexture.active=target;pixels.ReadPixels(new Rect(0,0,target.width,target.height),0,0);pixels.Apply();return pixels.GetPixels().Max(c=>Math.Max(c.r,Math.Max(c.g,c.b)));}
        finally{RenderTexture.active=previous;UnityEngine.Object.Destroy(pixels);}
    }
    float SampleDepthRange(RenderTexture target)
    {
        var previous=RenderTexture.active;var pixels=new Texture2D(target.width,target.height,TextureFormat.RGBAFloat,false);
        try{RenderTexture.active=target;pixels.ReadPixels(new Rect(0,0,target.width,target.height),0,0);pixels.Apply();var values=pixels.GetPixels();return values.Max(c=>c.r)-values.Min(c=>c.r);}
        finally{RenderTexture.active=previous;UnityEngine.Object.Destroy(pixels);}
    }
    void DepthCameraGpuCheck()
    {
        var bundle=AssetBundle.LoadFromFile(Path.Combine(Path.GetDirectoryName(Application.dataPath),"scripts/effect_mods/Vivify/Vivify/Resources/LegacyShadowCaster2022"));
        var shader=bundle.LoadAllAssets<Shader>().Single();var material=new Material(shader);
        var root=new GameObject("EffectSmokeDepthCamera");var cube=GameObject.CreatePrimitive(PrimitiveType.Cube);
        var depth=new RenderTexture(32,32,24,RenderTextureFormat.Depth);depth.Create();
        var color=new RenderTexture(32,32,0,RenderTextureFormat.RFloat);color.Create();
        var reader=new Material(Blitter.GetBlitMaterial(TextureDimension.Tex2D));reader.SetTexture("_BlitTexture",depth,RenderTextureSubElement.Depth);reader.SetVector("_BlitScaleBias",new Vector4(1,1,0,0));
        var previous=Shader.GetGlobalTexture("_CameraDepthTexture");
        try
        {
            root.transform.position=new Vector3(9999,9999,9999);var camera=root.AddComponent<Camera>();camera.enabled=true;camera.orthographic=true;camera.orthographicSize=2;camera.nearClipPlane=1;camera.farClipPlane=10;camera.cullingMask=1<<30;camera.targetTexture=depth;
            cube.layer=30;cube.transform.position=root.transform.position+Vector3.forward*3;cube.GetComponent<Renderer>().sharedMaterial=material;
            var type=AccessTools.TypeByName("Vivify.PostProcessing.DepthOnlyCameraController");var controller=root.AddComponent(type);
            float ReadDepth(){var command=new CommandBuffer();try{command.SetRenderTarget(color);command.SetViewport(new Rect(0,0,32,32));command.DrawProcedural(Matrix4x4.identity,reader,0,MeshTopology.Triangles,3);Graphics.ExecuteCommandBuffer(command);}finally{command.Release();}var active=RenderTexture.active;var pixels=new Texture2D(32,32,TextureFormat.RGBAFloat,false);try{RenderTexture.active=color;pixels.ReadPixels(new Rect(0,0,32,32),0,0);pixels.Apply();return pixels.GetPixel(16,16).r;}finally{RenderTexture.active=active;UnityEngine.Object.Destroy(pixels);}}
            AccessTools.Method(type,"RenderDepth").Invoke(controller,null);float occupied=ReadDepth();int draws=(int)AccessTools.Property(type,"LastDrawCount").GetValue(controller);
            cube.SetActive(false);AccessTools.Method(type,"RenderDepth").Invoke(controller,null);float empty=ReadDepth();
            Check("depth-only-camera-GPU-occluder",!Single.IsNaN(occupied)&&!Single.IsNaN(empty)&&Math.Abs(occupied-empty)>0.05f,"cube="+occupied+"; clear="+empty+"; draws="+draws);
            var emitter=new GameObject("EffectSmokeParticle");
            try
            {
                emitter.transform.position=cube.transform.position;var particles=emitter.AddComponent<ParticleSystem>();particles.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);
                var main=particles.main;main.startSpeed=0;main.startLifetime=10;main.startSize=1;
                particles.GetComponent<ParticleSystemRenderer>().sharedMaterial=material;particles.Emit(1);particles.Simulate(0.01f,false,false);
                var command=new CommandBuffer();try{command.SetRenderTarget(color);command.ClearRenderTarget(false,true,Color.black);command.SetViewProjectionMatrices(camera.worldToCameraMatrix,GL.GetGPUProjectionMatrix(camera.projectionMatrix,true));command.DrawRenderer(particles.GetComponent<ParticleSystemRenderer>(),material,0,0);Graphics.ExecuteCommandBuffer(command);}finally{command.Release();}
                Check("particle-command-draw-GPU",SampleTexturePeak(color)>0.5f,"ParticleSystemRenderer through CommandBuffer.DrawRenderer");
            }
            finally { UnityEngine.Object.Destroy(emitter); }
        }
        finally{Shader.SetGlobalTexture("_CameraDepthTexture",previous);root.GetComponent<Camera>().targetTexture=null;UnityEngine.Object.Destroy(root);UnityEngine.Object.Destroy(cube);UnityEngine.Object.Destroy(material);UnityEngine.Object.Destroy(reader);depth.Release();color.Release();UnityEngine.Object.Destroy(depth);UnityEngine.Object.Destroy(color);bundle.Unload(true);}
    }
    void OnObstacle(ObstacleController _) { obstacles++; }
    IEnumerator CacheIsolation(BeatmapLevel level)
    {
        var type = AccessTools.TypeByName("SongCore.Hooks.BeatmapLevelCache.BeatmapLevelCache");
        var cache = Activator.CreateInstance(type, true);
        var levelData = SongCore.Loader.CustomLevelLoader.LoadBeatmapLevelData(level);
        var keys = level.GetBeatmapKeys().Take(2).ToArray();
        if (keys.Length < 2) yield break;
        AccessTools.Property(type, "BeatmapLevelData").SetValue(cache, levelData);
        var keyProperty = AccessTools.Property(type, "BeatmapKey"); keyProperty.SetValue(cache, keys[1]);
        var kind = Enum.Parse(AccessTools.TypeByName("SongCore.Hooks.BeatmapLevelCache.BeatmapDataType"), "Beatmap");
        var read = AccessTools.Method(type, "GetJsonDataAsync");
        var oldCompletion = new TaskCompletionSource<string>(); var currentCompletion = new TaskCompletionSource<string>();
        var current = (Task<string>)read.Invoke(cache, new object[] {levelData, keys[1], kind, (Func<Task<string>>)(() => currentCompletion.Task)});
        var old = (Task<string>)read.Invoke(cache, new object[] {levelData, keys[0], kind, (Func<Task<string>>)(() => oldCompletion.Task)});
        oldCompletion.SetResult("old-chart");
        yield return Wait(() => old.IsCompleted, 5, "cache-overlapping-difficulties");
        Check("cache-pending-task-isolation", old.Result == "old-chart" && !current.IsCompleted, "Overlapping different difficulties must keep independent tasks.");
        currentCompletion.SetResult("selected-chart");
        yield return Wait(() => current.IsCompleted, 5, "cache-selected-completion");
        var cached = (Task<string>)read.Invoke(cache, new object[] {levelData, keys[1], kind, (Func<Task<string>>)(() => throw new InvalidOperationException("Cache was lost"))});
        Check("cache-result-isolation", cached.Result == "selected-chart", cached.Result);
        AccessTools.Method(type, "InvalidateDifficulty").Invoke(cache, null); keyProperty.SetValue(cache, keys[0]);
        var lateCompletion = new TaskCompletionSource<string>();
        var late = (Task<string>)read.Invoke(cache, new object[] {levelData, keys[0], kind, (Func<Task<string>>)(() => lateCompletion.Task)});
        AccessTools.Method(type, "InvalidateDifficulty").Invoke(cache, null); keyProperty.SetValue(cache, keys[1]);
        var replacement = (Task<string>)read.Invoke(cache, new object[] {levelData, keys[1], kind, (Func<Task<string>>)(() => Task.FromResult("new-selection"))});
        lateCompletion.SetResult("late-old-chart"); yield return Wait(() => late.IsCompleted, 5, "cache-stale-completion");
        cached = (Task<string>)read.Invoke(cache, new object[] {levelData, keys[1], kind, (Func<Task<string>>)(() => throw new InvalidOperationException("Cache was lost"))});
        Check("cache-stale-write-rejected", cached.Result == "new-selection" && replacement.Result == "new-selection", "Late old difficulty completion did not overwrite selection.");
    }
    void StereoArrayCopy(Type type, UnityEngine.Object processor, int samples)
    {
        var effectsProperty=AccessTools.Property(type,"Effects");var original=effectsProperty.GetValue(processor);
        var empty=(IDictionary)Activator.CreateInstance(original.GetType());
        foreach (DictionaryEntry entry in (IDictionary)original) empty.Add(entry.Key,Activator.CreateInstance(entry.Value.GetType()));
        var descriptor=new RenderTextureDescriptor(32,32,RenderTextureFormat.ARGBHalf,0){dimension=TextureDimension.Tex2DArray,volumeDepth=2,msaaSamples=samples,vrUsage=VRTextureUsage.TwoEyes};
        var array=new RenderTexture(descriptor);array.Create();
        var resolvedDescriptor=descriptor;resolvedDescriptor.msaaSamples=1;
        var resolved=new RenderTexture(resolvedDescriptor);resolved.Create();
        var mono=new RenderTexture(32,32,0,RenderTextureFormat.ARGBHalf);mono.Create();
        var previous=RenderTexture.active;
        bool keyword=Shader.IsKeywordEnabled("STEREO_MULTIVIEW_ON");
        try
        {
            Graphics.SetRenderTarget(array,0,CubemapFace.Unknown,0);GL.Clear(false,true,Color.red);
            Graphics.SetRenderTarget(array,0,CubemapFace.Unknown,1);GL.Clear(false,true,Color.blue);
            effectsProperty.SetValue(processor,empty);Shader.EnableKeyword("STEREO_MULTIVIEW_ON");
            using(var command=new CommandBuffer())
            {
                AccessTools.Method(type,"RenderUrp").Invoke(processor,new object[]{command,descriptor,new RenderTargetIdentifier(array),Enum.ToObject(AccessTools.TypeByName("Vivify.PostProcessingOrder"),0),array});
                Graphics.ExecuteCommandBuffer(command);
            }
            for(int eye=0;eye<2;eye++)
            {
                if(samples>1)array.ResolveAntiAliasedSurface(resolved);
                Graphics.CopyTexture(samples>1?resolved:array,eye,0,mono,0,0);RenderTexture.active=mono;
                var pixel=new Texture2D(1,1,TextureFormat.RGBAFloat,false);pixel.ReadPixels(new Rect(0,0,1,1),0,0);pixel.Apply();Color value=pixel.GetPixel(0,0);UnityEngine.Object.Destroy(pixel);
                Check("stereo-array-MSAA"+samples+"-eye-"+eye,eye==0 ? value.r>0.95f && value.b<0.05f : value.b>0.95f && value.r<0.05f,value.ToString());
            }
            Check("stereo-keyword-restored",Shader.IsKeywordEnabled("STEREO_MULTIVIEW_ON"),"Pre-existing multiview keyword preserved.");
            var fixture=AssetBundle.LoadFromFile(Path.Combine(Path.GetDirectoryName(Application.dataPath),"scripts/effect_mods/Tests/Resources/LegacyGrabChecks2022"));
            var eyeMaterial=new Material(fixture.LoadAllAssets<Shader>().Single(shader=>shader.name=="Vivify/Checks/Eye"));
            try
            {
                var order=Enum.ToObject(AccessTools.TypeByName("Vivify.PostProcessingOrder"),0);
                var dataType=AccessTools.TypeByName("Vivify.PostProcessing.MaterialData");
                var data=Activator.CreateInstance(dataType,AccessTools.all,null,new object[]{eyeMaterial,0,null,null,(int?)0,null},null);
                ((IList)empty[order]).Add(data);
                using(var command=new CommandBuffer())
                {
                    AccessTools.Method(type,"RenderUrp").Invoke(processor,new object[]{command,descriptor,new RenderTargetIdentifier(array),order,array});Graphics.ExecuteCommandBuffer(command);
                }
                for(int eye=0;eye<2;eye++)
                {
                    if(samples>1)array.ResolveAntiAliasedSurface(resolved);
                    Graphics.CopyTexture(samples>1?resolved:array,eye,0,mono,0,0);RenderTexture.active=mono;
                    var pixel=new Texture2D(1,1,TextureFormat.RGBAFloat,false);pixel.ReadPixels(new Rect(0,0,1,1),0,0);pixel.Apply();Color value=pixel.GetPixel(0,0);UnityEngine.Object.Destroy(pixel);
                    Check("stereo-shader-MSAA"+samples+"-eye-"+eye,eye==0?value.r>0.95f&&value.b<0.05f:value.b>0.95f&&value.r<0.05f,value.ToString());
                }
            }
            finally{UnityEngine.Object.Destroy(eyeMaterial);fixture.Unload(true);}
        }
        finally { effectsProperty.SetValue(processor,original);if(!keyword)Shader.DisableKeyword("STEREO_MULTIVIEW_ON");RenderTexture.active=previous;array.Release();mono.Release();resolved.Release();UnityEngine.Object.Destroy(array);UnityEngine.Object.Destroy(mono);UnityEngine.Object.Destroy(resolved); }
    }
    void SampleAudioLink()
    {
        var type=AccessTools.TypeByName("AudioLink.AudioLink");var audioLink=Resources.FindObjectsOfTypeAll(type).First();
        var texture=(CustomRenderTexture)AccessTools.Field(type,"audioRenderTexture").GetValue(audioLink);
        var previous=RenderTexture.active;RenderTexture.active=texture;
        var pixels=new Texture2D(texture.width,4,TextureFormat.RGBAFloat,false);
        try { pixels.ReadPixels(new Rect(0,0,texture.width,4),0,0);pixels.Apply();var values=pixels.GetPixels().Select(p=>p.r).ToArray();audioLinkPeak=Math.Max(audioLinkPeak,values.Max());audioLinkFinite&=values.All(x=>!float.IsNaN(x)&&!float.IsInfinity(x)); }
        finally { RenderTexture.active=previous;UnityEngine.Object.Destroy(pixels); }
    }
    void AuditLibrary()
    {
        var loaded=(IEnumerable<KeyValuePair<string,BeatmapLevel>>)AccessTools.Field(typeof(SongCore.Loader),"CustomLevels").GetValue(null);
        var counts=new Dictionary<string,(HashSet<string> songs,int difficulties)>();var missing=new List<string>();var assetRows=new List<object>();
        var saved=(Dictionary<string,CustomLevelLoader.LoadedSaveData>)AccessTools.Field(typeof(CustomLevelLoader),"_loadedBeatmapSaveData").GetValue(SongCore.Loader.CustomLevelLoader);
        int total=0,difficulties=0;
        foreach(var entry in loaded)
        {
            total++;
            foreach(var key in entry.Value.GetBeatmapKeys())
            {
                difficulties++;var data=SongCore.Collections.GetCustomLevelSongDifficultyData(key);
                if(data==null){missing.Add("Missing metadata: "+key);continue;}
                foreach(var required in data.additionalDifficultyData._requirements)
                {
                    if(!SongCore.Collections.capabilities.Contains(required))missing.Add(key+": "+required);
                    if(!counts.TryGetValue(required,out var value))value=(new HashSet<string>(),0);
                    value.songs.Add(entry.Value.levelID);value.difficulties++;counts[required]=value;
                }
            }
            if(saved.TryGetValue(entry.Value.levelID,out var file))
            {
                var info=JObject.Parse(file.customLevelFolderInfo.levelInfoJsonString);
                bool vivify=SongCore.Collections.GetCustomLevelSongData(entry.Value.levelID)?._difficulties.Any(d=>d.additionalDifficultyData._requirements.Contains("Vivify"))==true;
                if(vivify){string path=Path.Combine(file.customLevelFolderInfo.folderPath,"bundleWindows2021.vivify");assetRows.Add(new{id=entry.Value.levelID,path,checksum=(uint?)info["_customData"]?["_assetBundle"]?["_windows2021"],exists=File.Exists(path)});}
                foreach(var set in (JArray)(info["_difficultyBeatmapSets"]??new JArray()))
                    foreach(var chart in set["_difficultyBeatmaps"])
                        if(chart["_customData"]?["_requirements"] is JArray req && req.Count>0 && !entry.Value.GetBeatmapKeys().Any(k=>SongCore.Collections.GetCurrentCharacteristicSerializedName(k.characteristic)==(string)set["_beatmapCharacteristicName"] && k.difficulty.ToString()==(string)chart["_difficulty"]))missing.Add("Required chart not exposed: "+entry.Key+"/"+(string)set["_beatmapCharacteristicName"]+"/"+(string)chart["_difficulty"]);
            }
        }
        File.WriteAllText(Path.Combine(directory,"library-audit.json"),JsonConvert.SerializeObject(new{songs=total,difficulties,capabilities=SongCore.Collections.capabilities,required=counts.ToDictionary(e=>e.Key,e=>new{songs=e.Value.songs.Count,difficulties=e.Value.difficulties}),assets=assetRows,missing},Formatting.Indented));
        Check("library-requirements-and-chart-availability",missing.Count==0,total+" songs; "+difficulties+" difficulties; "+String.Join(",",missing));
    }
    IEnumerator AuditBundles()
    {
        var audit=JObject.Parse(File.ReadAllText(Path.Combine(directory,"library-audit.json")));
        var rows=(JArray)audit["assets"];Check("all-Vivify-assets-present",rows.Count==45 && rows.All(r=>(bool)r["exists"] && r["checksum"].Type!=JTokenType.Null),rows.Count+" Vivify maps");
        var results=new List<object>();
        foreach(var row in rows)
        {
            var bundle=(AssetBundle)AccessTools.Method(AccessTools.TypeByName("Vivify.Managers.AssetBundleCompatibility"),"Load").Invoke(null,new object[]{(string)row["path"],(uint)row["checksum"]});
            Check("bundle-CRC-"+(string)row["id"],bundle!=null,(string)row["path"]);
            try
            {
                var materials=new HashSet<Material>(bundle.LoadAllAssets<Material>());
                foreach(var prefab in bundle.LoadAllAssets<GameObject>())foreach(var renderer in prefab.GetComponentsInChildren<Renderer>(true))foreach(var material in renderer.sharedMaterials)if(material!=null)materials.Add(material);
                var bad=materials.Where(m=>m.shader==null||!m.shader.isSupported||m.passCount==0).Select(m=>m.name+": "+(m.shader==null?"missing shader":m.shader.name)).ToArray();
                if(bad.Length>0)File.WriteAllText(Path.Combine(directory,"unsupported-shaders.json"),JsonConvert.SerializeObject(new{
                    device=SystemInfo.graphicsDeviceType,shaderLevel=SystemInfo.graphicsShaderLevel,
                    legacy=Shader.Find("Legacy Shaders/VertexLit")?.name,
                    shaderDetails=materials.Where(m=>m.shader==null||!m.shader.isSupported||m.passCount==0).Select(m=>new{m.name,shader=m.shader?.name,supported=m.shader?.isSupported,passes=m.passCount,shaderPasses=m.shader?.passCount,renderPipeline=m.GetTag("RenderPipeline",false,""),keywords=m.shaderKeywords})
                },Formatting.Indented));
                Check("bundle-shaders-"+(string)row["id"],bad.Length==0,materials.Count+" materials; "+String.Join(",",bad));
                results.Add(new{id=(string)row["id"],path=(string)row["path"],materials=materials.Count,shaders=materials.Select(m=>m.shader.name).Distinct().OrderBy(n=>n).ToArray()});
            }
            finally{AccessTools.Method(AccessTools.TypeByName("Vivify.Managers.AssetBundleCompatibility"),"Unload").Invoke(null,new object[]{bundle});}
            yield return null;
        }
        File.WriteAllText(Path.Combine(directory,"bundle-audit.json"),JsonConvert.SerializeObject(results,Formatting.Indented));
    }
    void Finish(string error)
    {
        if (done) return; done = true;
        if (error != null) errors.Add(error);
        if (modUiAudit != null) ModUiAuditWrite(modUiAudit);
        if (error != null)
        {
            try
            {
                File.WriteAllText(Path.Combine(directory, "failure-ui.json"), JsonConvert.SerializeObject(new {
                    views=Resources.FindObjectsOfTypeAll<StandardLevelDetailView>().Select(v=>new{key=v.beatmapKey.ToString(),active=v.gameObject.activeInHierarchy,button=v.actionButton.interactable}),
                    tables=Resources.FindObjectsOfTypeAll<LevelCollectionTableView>().Select(v=>new{path=String.Join("/",Parents(v.transform).Reverse()),active=v.gameObject.activeInHierarchy,ownerActive=v.GetComponentInParent<HMUI.ViewController>()?.isActivated,listeners=(AccessTools.Field(v.GetType(),"didSelectLevelEvent").GetValue(v) as Delegate)?.GetInvocationList().Length}),
                    navigation=Resources.FindObjectsOfTypeAll<LevelCollectionNavigationController>().Select(v=>new{path=String.Join("/",Parents(v.transform).Reverse()),active=v.gameObject.activeInHierarchy,v.isActivated}),
                    labels=Resources.FindObjectsOfTypeAll<TMPro.TMP_Text>().Where(t=>t!=null&&t.gameObject.activeInHierarchy).Select(t=>new{path=String.Join("/",Parents(t.transform).Reverse()),t.text})
                },Formatting.Indented));
            }
            catch (Exception diagnostic) { errors.Add("failure-ui: "+diagnostic.Message); }
        }
        try { if (manager != null) { manager.noteWasSpawnedEvent -= OnNote; manager.obstacleWasSpawnedEvent -= OnObstacle; } if (offscreen != null) { offscreen.Release(); UnityEngine.Object.Destroy(offscreen); } if (noFail != null) noFail.SetValue(modifiers, originalNoFail); }
        catch (Exception restore) { errors.Add(restore.ToString()); }
        Application.logMessageReceived -= OnLog;
        Application.logMessageReceived -= CaptureLocalMessage;
        File.WriteAllText(Path.Combine(directory, "report.json"), JsonConvert.SerializeObject(new {passed=errors.Count==0, phase, targetId, maximumTime, notes, obstacles, grabCaptures, grabPeak, negativeColliderWarnings, elapsed=timer.Elapsed.TotalSeconds, xrActive=UnityEngine.XR.XRSettings.isDeviceActive, checks, errors}, Formatting.Indented));
        Application.Quit();
    }
}
