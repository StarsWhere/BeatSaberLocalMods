using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BeatSaberMarkupLanguage.MenuButtons;
using IPA;
using Newtonsoft.Json;
using SongDuplicateCleaner;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace SongDuplicateCleanerSmoke
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public sealed class Plugin
    {
        GameObject owner;
        [OnStart] public void Start()
        {
            if (String.IsNullOrEmpty(Environment.GetEnvironmentVariable("DUPLICATE_CLEANER_SMOKE_DIR"))) return;
            owner = new GameObject("SongDuplicateCleanerSmoke");
            UnityEngine.Object.DontDestroyOnLoad(owner);
            owner.AddComponent<Runner>();
        }
        [OnExit] public void Exit() { if (owner != null) UnityEngine.Object.Destroy(owner); }
    }
    public sealed class Runner : MonoBehaviour
    {
        readonly List<string> checks = new List<string>();
        readonly List<string> errors = new List<string>();
        string directory;
        float started;
        bool finished;
        float nextProgress;
        void Start()
        {
            directory = Environment.GetEnvironmentVariable("DUPLICATE_CLEANER_SMOKE_DIR");
            started = Time.realtimeSinceStartup;
            Application.logMessageReceived += Log;
            StartCoroutine(Drive());
        }
        void Update()
        {
            if (finished || Time.realtimeSinceStartup < nextProgress) return;
            nextProgress = Time.realtimeSinceStartup + 1;
            var view = Resources.FindObjectsOfTypeAll<CleanerView>().FirstOrDefault();
            var task = view == null ? null : Field<System.Threading.Tasks.Task>(view, "work");
            File.WriteAllText(Path.Combine(directory, "progress.json"), JsonConvert.SerializeObject(new {
                elapsed = Time.realtimeSinceStartup - started, lastCheck = checks.LastOrDefault(),
                viewActive = view != null && view.gameObject.activeInHierarchy,
                busy = view != null && view.Busy, mutating = view != null && view.Mutating,
                status = view == null ? "" : view.Status, summary = view == null ? "" : view.Summary,
                groups = view == null || view.Result == null ? -1 : view.Result.Groups.Count,
                workStatus = task == null ? "none" : task.Status.ToString()
            }, Formatting.Indented));
        }
        void Log(string message, string stack, LogType type)
        {
            if (type == LogType.Exception && (message.Contains("NullReferenceException") || stack.Contains("SongDuplicateCleaner")))
                errors.Add(message + "\n" + stack);
        }
        IEnumerator Drive()
        {
            var phases = new Stack<IEnumerator>();
            phases.Push(Run());
            while (phases.Count > 0)
            {
                bool next = false;
                object current = null;
                try { next = phases.Peek().MoveNext(); if (next) current = phases.Peek().Current; }
                catch (Exception error) { errors.Add(error.ToString()); break; }
                if (!next) { phases.Pop(); continue; }
                var child = current as IEnumerator;
                if (child != null) { phases.Push(child); continue; }
                yield return current;
            }
            Finish();
        }
        IEnumerator Wait(Func<bool> predicate, float seconds, string label)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < deadline && Time.realtimeSinceStartup - started < 240)
            {
                if (predicate()) { checks.Add(label); yield break; }
                yield return null;
            }
            throw new TimeoutException(label);
        }
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks.Add(label); }
        static T Active<T>() where T : Component { return Resources.FindObjectsOfTypeAll<T>().FirstOrDefault(item => item != null && item.gameObject.activeInHierarchy); }
        static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner); }
        MenuButton MenuButton()
        {
            var registry = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType("LocalModUI.UiRegistry", false)).FirstOrDefault(x => x != null);
            if (registry == null) return null;
            if (!(bool)registry.GetProperty("IsReady").GetValue(null, null)) return null;
            var entries = registry.GetProperty("Entries").GetValue(null, null) as IEnumerable;
            if (entries == null || !entries.Cast<object>().Any(x => (string)x.GetType().GetProperty("Id").GetValue(x, null) == "song-cleaner")) return null;
            return new MenuButton("Duplicate songs", delegate { registry.GetMethod("Open").Invoke(null, new object[] { "song-cleaner" }); });
        }
        static bool CenterReady()
        {
            var registry = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType("LocalModUI.UiRegistry", false)).First(x => x != null);
            if (!(bool)registry.GetProperty("IsReady").GetValue(null, null)) return false;
            return Resources.FindObjectsOfTypeAll<HMUI.FlowCoordinator>().Any(x => x != null && x.GetType().FullName == "LocalModUI.CenterFlow" && x.isActivated && !x.isInTransition);
        }
        static void LeaveCenter()
        {
            var registry = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType("LocalModUI.UiRegistry", false)).First(x => x != null);
            registry.GetMethod("LeaveCenter").Invoke(null, new object[] { (Action)delegate { } });
        }
        static Button ButtonNamed(CleanerView view, string label)
        {
            return view.GetComponentsInChildren<Button>(true).First(button => button.GetComponentsInChildren<TMP_Text>(true).Any(text => text.text == label));
        }
        IEnumerator Run()
        {
            yield return Wait(() => Active<MainMenuViewController>() != null && MenuButton() != null,
                70, "menu entry ready");
            var button = MenuButton();
            Check(button != null, "cleaner center entry registered");
            yield return new WaitForSeconds(0.7f);
            Capture("menu.png");
            var fixtureA = Environment.GetEnvironmentVariable("DUPLICATE_CLEANER_SMOKE_A");
            var fixtureB = Environment.GetEnvironmentVariable("DUPLICATE_CLEANER_SMOKE_B");
            button.ButtonClicked();
            CleanerView view = null;
            yield return Wait(() => (view = Active<CleanerView>()) != null, 10, "cleaner view opened");
            if (!SongCore.Loader.AreSongsLoaded) Check(view.Result == null, "early opening does not return an empty-library result");
            yield return Wait(() => SongCore.Loader.AreSongsLoaded && !view.Busy && view.Result != null, 120, "duplicate preview scan finished");
            ButtonNamed(view, "重新扫描").onClick.Invoke();
            SongCore.Loader.Instance.RefreshSongs(true);
            yield return Wait(() => SongCore.Loader.AreSongsLoaded && !view.Busy && view.Result != null, 120,
                "library refresh cancels stale scan and automatically produces a new preview");
            var originals = SongCore.Loader.CustomLevels.Keys.Where(path => !String.Equals(path, fixtureA, StringComparison.OrdinalIgnoreCase)
                && !String.Equals(path, fixtureB, StringComparison.OrdinalIgnoreCase)).ToArray();
            var initial = view.Result;
            File.WriteAllText(Path.Combine(directory, "preview.json"), JsonConvert.SerializeObject(new {
                groups = initial.Groups.Count, copies = initial.Groups.Sum(group => group.Remove.Count),
                reclaimBytes = initial.Groups.Sum(group => group.ReclaimBytes), differingGroups = initial.DifferentContentGroups,
                errors = initial.Errors
            }, Formatting.Indented));
            var fixtureGroup = initial.Groups.Single(group => new[] { group.Keep.Path }.Concat(group.Remove.Select(entry => entry.Path))
                .All(path => String.Equals(path, fixtureA, StringComparison.OrdinalIgnoreCase) || String.Equals(path, fixtureB, StringComparison.OrdinalIgnoreCase)));
            Check(fixtureGroup.Remove.Count == 1 && fixtureGroup.Keep.LevelId == "custom_level_" + fixtureGroup.Hash,
                "fixture copies detected and canonical level retained");
            Check(initial.Errors.Count == 0, "library directories readable");
            yield return new WaitForSeconds(3f);
            var directoryDetails = Field<TMP_Text>(view, "detail");
            Check(directoryDetails.preferredHeight > 2 && directoryDetails.rectTransform.rect.height >= directoryDetails.preferredHeight - 0.5f,
                "kept and removed directory details have enough layout height");
            Check(directoryDetails.text.StartsWith("保留") && directoryDetails.text.Contains(Path.GetFileName(initial.Groups[0].Keep.Path)),
                "retained directory preview is populated");
            Check(directoryDetails.raycastTarget && Field<HMUI.HoverHint>(view, "detailHint") != null &&
                Field<HMUI.HoverHint>(view, "detailHint").text.Contains(initial.Groups[0].Keep.Path),
                "retained directory has a raycastable full-path tooltip");
            ButtonNamed(view, "下一份").onClick.Invoke();
            yield return null;
            Check(directoryDetails.text.StartsWith("删除") && directoryDetails.text.Contains(Path.GetFileName(initial.Groups[0].Remove[0].Path)),
                "removed directory preview can be selected");
            Check(Field<HMUI.HoverHint>(view, "detailHint").text.Contains(initial.Groups[0].Remove[0].Path),
                "directory tooltip follows the selected copy");
            ButtonNamed(view, "上一份").onClick.Invoke();
            Capture("preview.png");
            ButtonNamed(view, "清理全部").onClick.Invoke();
            yield return Wait(() => view.ConfirmVisible, 3, "bulk cleanup confirmation shown");
            Check(Field<TMP_Text>(view, "confirmationText").text.Contains(initial.Groups.Sum(group => group.Remove.Count).ToString()),
                "bulk confirmation displays actual removal count");
            Check(Field<TMP_Text>(view, "confirmationText").text.Contains("保留 " + initial.Groups.Count),
                "bulk confirmation displays actual retention count");
            ButtonNamed(view, "取消").onClick.Invoke();
            Check(originals.All(Directory.Exists) && Directory.Exists(fixtureA) && Directory.Exists(fixtureB), "bulk cancellation preserves the full library");
            var select = typeof(CleanerView).GetMethod("SelectGroup", BindingFlags.Instance | BindingFlags.NonPublic);
            select.Invoke(view, new object[] { Field<BeatSaberMarkupLanguage.Components.CustomListTableData>(view, "list").TableView,
                initial.Groups.ToList().IndexOf(fixtureGroup) });
            ButtonNamed(view, "清理本组").onClick.Invoke();
            yield return Wait(() => view.ConfirmVisible, 3, "deletion confirmation shown");
            yield return new WaitForSeconds(0.5f);
            Capture("confirmation.png");
            ButtonNamed(view, "取消").onClick.Invoke();
            Check(!view.ConfirmVisible && Directory.Exists(fixtureA) && Directory.Exists(fixtureB), "cancel leaves both copies intact");
            ButtonNamed(view, "清理本组").onClick.Invoke();
            yield return Wait(() => view.ConfirmVisible, 3, "confirmation can reopen");
            ButtonNamed(view, "确认删除").onClick.Invoke();
            yield return Wait(() => !view.Busy && !view.Mutating && view.LastDeletion != null, 90, "confirmed cleanup and SongCore refresh finish");
            Check(view.LastDeletion.Errors.Count == 0 && !view.LastDeletion.Cancelled && view.LastDeletion.DeletedPaths.Count == 1,
                "fixture-only deletion completed without failures");
            Check(Directory.Exists(fixtureGroup.Keep.Path) && !Directory.Exists(fixtureGroup.Remove[0].Path), "exactly one fixture folder retained");
            Check(SongCore.Loader.CustomLevels.ContainsKey(fixtureGroup.Keep.Path) && !SongCore.Loader.CustomLevels.ContainsKey(fixtureGroup.Remove[0].Path),
                "SongCore removes deleted copy and keeps survivor");
            Check(originals.All(Directory.Exists), "all existing library folders preserved");
            ButtonNamed(view, "重新扫描").onClick.Invoke();
            yield return Wait(() => !view.Busy && view.Result != null, 120, "rescan after deletion finishes");
            Check(!view.Result.Groups.Any(group => group.Keep.Path == fixtureA || group.Keep.Path == fixtureB), "deleted fixture no longer appears as duplicate");
            Check(view.Result.Groups.Count == initial.Groups.Count - 1, "remaining duplicate group count is correct");
            var capabilities = new HashSet<string>(SongCore.Collections.capabilities, StringComparer.Ordinal);
            int auditedSongs = 0, auditedDifficulties = 0;
            foreach (var entry in SongCore.Loader.CustomLevels)
            {
                auditedSongs++;
                foreach (var key in entry.Value.GetBeatmapKeys())
                {
                    auditedDifficulties++;
                    var data = SongCore.Collections.GetCustomLevelSongDifficultyData(key);
                    if (data == null || data.additionalDifficultyData._requirements.Any(requirement => !capabilities.Contains(requirement)))
                        throw new InvalidOperationException("Missing SongCore difficulty data or capability: " + key);
                }
            }
            checks.Add("all library requirements covered: " + auditedSongs + " songs / " + auditedDifficulties + " difficulties");
            var flow = Active<CleanerFlow>();
            typeof(CleanerFlow).GetMethod("BackButtonWasPressed", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(flow, new object[] { view });
            yield return Wait(() => !flow.isActivated && !flow.isInTransition && CenterReady() &&
                !Field<bool>(Active<CleanerMenu>(), "closing"), 10, "back animation returns to center");
            button.ButtonClicked();
            yield return Wait(() => flow.isActivated && !flow.isInTransition && !view.Busy && view.Result != null,
                15, "menu can reopen after its animation finishes");
            var retainedRealSong = view.Result.Groups.First(group => group.Keep.Path != fixtureA && group.Keep.Path != fixtureB).Keep.Path;
            typeof(CleanerFlow).GetMethod("BackButtonWasPressed", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(flow, new object[] { view });
            yield return Wait(() => !flow.isActivated && !flow.isInTransition && CenterReady() &&
                !Field<bool>(Active<CleanerMenu>(), "closing"), 10, "cleaner returns to center");
            LeaveCenter();
            yield return Wait(() => Active<MainMenuViewController>() != null && Active<MainMenuViewController>().isActivated,
                10, "center dismisses before solo selection");
            Check(fixtureGroup.Keep.Path == fixtureA || fixtureGroup.Keep.Path == fixtureB, "fixture cleanup is restricted to owned folder");
            SongCore.Loader.Instance.DeleteSong(fixtureGroup.Keep.Path);
            Check(!Directory.Exists(fixtureGroup.Keep.Path), "temporary survivor removed after verification");
            SongCore.Loader.Instance.RefreshSongs(true);
            yield return Wait(() => SongCore.Loader.AreSongsLoaded && !SongCore.Loader.AreSongsLoading &&
                !SongCore.Loader.CustomLevels.ContainsKey(fixtureA) && !SongCore.Loader.CustomLevels.ContainsKey(fixtureB),
                45, "SongCore refresh removes all temporary fixture records");
            yield return new WaitForSeconds(0.7f);
            var level = SongCore.Loader.CustomLevels[retainedRealSong];
            var keyToPlay = level.GetBeatmapKeys().OrderBy(key =>
                SongCore.Collections.GetCurrentCharacteristicSerializedName(key.characteristic) == "Standard" ? 0 : 1)
                .ThenBy(key => key.difficulty).First();
            var solo = Resources.FindObjectsOfTypeAll<SoloFreePlayFlowCoordinator>().First();
            var stateConstructor = typeof(LevelSelectionFlowCoordinator.State).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(constructor => constructor.GetParameters().Length == 4);
            var state = stateConstructor.Invoke(new object[] { SelectLevelCategoryViewController.LevelCategory.All,
                SongCore.Loader.CustomLevelsPack, keyToPlay, level });
            typeof(SoloFreePlayFlowCoordinator).GetMethod("Setup", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Invoke(solo, new[] { state });
            Field<Button>(Active<MainMenuViewController>(), "_soloButton").onClick.Invoke();
            yield return Wait(() => Active<LevelCollectionTableView>() != null && Active<LevelCollectionNavigationController>() != null &&
                Active<LevelCollectionNavigationController>().isActivated,
                30, "native song selection ready");
            yield return new WaitForSeconds(0.7f);
            StandardLevelDetailView nativeDetail = null;
            yield return Wait(() => (nativeDetail = Active<StandardLevelDetailView>()) != null &&
                nativeDetail.beatmapKey.levelId == level.levelID && nativeDetail.actionButton.interactable,
                30, "retained song native play button enabled");
            Check(Field<Button>(nativeDetail, "_practiceButton").interactable, "retained song native practice button enabled");
            nativeDetail.actionButton.onClick.Invoke();
            yield return Wait(() => Active<AudioTimeSyncController>() != null && Active<AudioTimeSyncController>().songTime > 2,
                35, "retained song enters real gameplay and audio advances");
            Check(errors.Count == 0, "no startup or cleaner UI exceptions");
        }
        void Capture(string name)
        {
            var main = Active<MainCamera>();
            if (main == null) throw new InvalidOperationException("MainCamera missing");
            var camera = main.camera;
            var oldPosition = camera.transform.localPosition;
            var oldRotation = camera.transform.localRotation;
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            var oldRect = camera.rect;
            var oldAspect = camera.aspect;
            var oldFov = camera.fieldOfView;
            var oldEnabled = camera.enabled;
            var data = camera.GetComponent<UniversalAdditionalCameraData>();
            bool xr = data != null && data.allowXRRendering;
            var manager = Resources.FindObjectsOfTypeAll<MonoBehaviour>().FirstOrDefault(item => item != null &&
                item.GetType().FullName == "CustomAvatar.Player.PlayerAvatarManager");
            var avatar = manager == null ? null : manager.GetType().GetProperty("currentlySpawnedAvatar").GetValue(manager, null) as Component;
            var avatarRenderers = avatar == null ? new Renderer[0] : avatar.GetComponentsInChildren<Renderer>(true).Where(item => item.enabled).ToArray();
            var target = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32);
            var texture = new Texture2D(1600, 900, TextureFormat.RGBA32, false);
            try
            {
                foreach (var renderer in avatarRenderers) renderer.enabled = false;
                target.Create();
                camera.transform.localPosition = new Vector3(0, 1.6f, 0);
                camera.transform.localRotation = Quaternion.identity;
                camera.targetTexture = target;
                camera.enabled = true;
                camera.rect = new Rect(0, 0, 1, 1);
                camera.aspect = 1600f / 900;
                camera.fieldOfView = 45f;
                if (data != null) data.allowXRRendering = false;
                var request = new RenderPipeline.StandardRequest { destination = target, face = CubemapFace.Unknown };
                RenderPipeline.SubmitRenderRequest(camera, request);
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0, false);
                texture.Apply();
                var pixels = texture.GetPixels32();
                for (int index = 0; index < pixels.Length; index++) pixels[index].a = 255;
                texture.SetPixels32(pixels);
                texture.Apply();
                File.WriteAllBytes(Path.Combine(directory, name), texture.EncodeToPNG());
            }
            finally
            {
                camera.transform.localPosition = oldPosition; camera.transform.localRotation = oldRotation;
                camera.targetTexture = previousTarget; camera.rect = oldRect; camera.aspect = oldAspect;
                camera.fieldOfView = oldFov; camera.enabled = oldEnabled;
                RenderTexture.active = previousActive;
                if (data != null) data.allowXRRendering = xr;
                foreach (var renderer in avatarRenderers) if (renderer != null) renderer.enabled = true;
                target.Release(); UnityEngine.Object.Destroy(target); UnityEngine.Object.Destroy(texture);
            }
        }
        void Finish()
        {
            if (finished) return;
            finished = true;
            Application.logMessageReceived -= Log;
            File.WriteAllText(Path.Combine(directory, "report.json"), JsonConvert.SerializeObject(new {
                passed = errors.Count == 0, elapsed = Time.realtimeSinceStartup - started, checks, errors
            }, Formatting.Indented));
            Application.Quit(errors.Count == 0 ? 0 : 1);
        }
    }
}
