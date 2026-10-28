using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using BeatSaberMarkupLanguage.ViewControllers;
using HMUI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SongDuplicateCleaner
{
    [ViewDefinition("SongDuplicateCleaner.CleanerView.bsml")]
    public sealed class CleanerView : BSMLAutomaticViewController
    {
        // BSML assigns component fields while parsing the embedded view.
#pragma warning disable 0649
        [UIComponent("summary")] TMP_Text summary;
        [UIComponent("status")] TMP_Text status;
        [UIComponent("selected-title")] TMP_Text title;
        [UIComponent("detail")] TMP_Text detail;
        [UIComponent("groups")] CustomListTableData list;
        [UIComponent("scan")] Button scan;
        [UIComponent("delete-group")] Button deleteGroup;
        [UIComponent("delete-all")] Button deleteAll;
        [UIComponent("confirm")] ModalView confirmation;
        [UIComponent("confirmation")] TMP_Text confirmationText;
        [UIComponent("copy-prev")] Button copyPrevious;
        [UIComponent("copy-next")] Button copyNext;
        [UIComponent("copy-page")] TMP_Text copyPageText;
#pragma warning restore 0649
        HoverHint summaryHint, statusHint, titleHint, detailHint;
        readonly string root = Path.GetFullPath(CustomLevelPathHelper.customLevelsDirectoryPath);
        ScanResult result;
        DuplicateGroup[] selectedDeletion;
        Task work;
        CancellationTokenSource cancellation;
        int selected = -1, revision, copyPage;
        bool parsed, subscribed, waitingForLibrary, refreshing, rescanOnActivation = true;
        public bool Busy { get { return work != null; } }
        public bool Mutating { get; private set; }
        public bool ConfirmVisible { get; private set; }
        public ScanResult Result { get { return result; } }
        public DeleteResult LastDeletion { get; private set; }
        public string Summary { get { return summary == null ? "" : summary.text; } }
        public string Status { get { return status == null ? "" : status.text; } }

        [UIAction("#post-parse")]
        void Parsed()
        {
            if (title != null) title.gameObject.name = "SongName";
            parsed = true;
            summaryHint = HintFor(summary);
            statusHint = HintFor(status);
            titleHint = HintFor(title);
            detailHint = HintFor(detail);
            SongCore.Loader.SongsLoadedEvent += LibraryChanged;
            SongCore.Loader.LoadingStartedEvent += LibraryLoading;
            subscribed = true;
            Controls();
        }
        protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);
            if (parsed && rescanOnActivation && !Busy) { rescanOnActivation = false; Scan(); }
        }
        protected override void DidDeactivate(bool removedFromHierarchy, bool screenSystemDisabling)
        {
            CancelConfirmation();
            if (!Mutating)
            {
                rescanOnActivation = Busy || result == null;
                CancelWork();
            }
            base.DidDeactivate(removedFromHierarchy, screenSystemDisabling);
        }
        void LibraryChanged(SongCore.Loader loader, ConcurrentDictionary<string, BeatmapLevel> levels)
        {
            revision++;
            if (Mutating) return;
            if (cancellation != null) cancellation.Cancel();
            result = null;
            selected = -1;
            rescanOnActivation = true;
            CancelConfirmation();
            Render();
            if (waitingForLibrary && isActivated && gameObject.activeInHierarchy)
            {
                waitingForLibrary = false;
                Scan();
                return;
            }
            status.text = "曲库已更新，请重新扫描";
        }
        void LibraryLoading(SongCore.Loader loader)
        {
            if (Mutating)
            {
                // An external refresh must not race further file removals. The
                // current directory finishes atomically; the remaining plan cancels.
                if (!refreshing && cancellation != null) cancellation.Cancel();
                return;
            }
            CancelWork();
            CancelConfirmation();
            result = null;
            selected = -1;
            rescanOnActivation = true;
            waitingForLibrary = true;
            Render();
            status.text = "正在等待曲库加载";
        }
        void Controls()
        {
            if (!parsed) return;
            bool ready = !Busy && SongCore.Loader.Instance != null && SongCore.Loader.AreSongsLoaded && !SongCore.Loader.AreSongsLoading;
            scan.interactable = ready;
            deleteAll.interactable = ready && result != null && result.Groups.Count > 0;
            deleteGroup.interactable = deleteAll.interactable && selected >= 0 && selected < result.Groups.Count;
            copyPrevious.interactable = deleteGroup.interactable && copyPage > 0;
            copyNext.interactable = deleteGroup.interactable && copyPage < result.Groups[selected].Remove.Count;
        }
        void Update() { if (parsed) { Controls(); RefreshHints(); } }

        static HoverHint HintFor(TMP_Text label)
        {
            if (label == null) return null;
            label.raycastTarget = true;
            var hint = label.GetComponent<HoverHint>();
            return hint != null ? hint : label.GetComponentInParent<HoverHint>();
        }
        static void SetHint(HoverHint hint, string value)
        {
            if (hint != null && hint.text != (value ?? "")) hint.text = value ?? "";
        }
        void RefreshHints()
        {
            SetHint(summaryHint, summary == null ? "" : summary.text);
            SetHint(statusHint, status == null ? "" : status.text);
            SetHint(titleHint, title == null ? "" : title.text);
            string fullPath = "";
            if (result != null && selected >= 0 && selected < result.Groups.Count)
            {
                var group = result.Groups[selected];
                if (copyPage == 0) fullPath = group.Keep.Path;
                else if (copyPage <= group.Remove.Count) fullPath = group.Remove[copyPage - 1].Path;
            }
            SetHint(detailHint, String.IsNullOrEmpty(fullPath) && detail != null ? detail.text : fullPath);
        }
        SongEntry[] Snapshot()
        {
            if (!SongCore.Loader.AreSongsLoaded || SongCore.Loader.AreSongsLoading) throw new InvalidOperationException("曲库正在刷新，请稍后重试");
            return SongCore.Loader.CustomLevels.Select(pair => new SongEntry(pair.Key, pair.Value.levelID,
                SongCore.Collections.GetCustomLevelHash(pair.Value.levelID), pair.Value.songName)).ToArray();
        }
        [UIAction("scan")]
        public void Scan()
        {
            if (Busy || !parsed) return;
            CancelConfirmation();
            if (!SongCore.Loader.AreSongsLoaded || SongCore.Loader.AreSongsLoading)
            {
                waitingForLibrary = true;
                status.text = "正在等待曲库加载";
                summary.text = "正在读取曲库";
                return;
            }
            waitingForLibrary = false;
            SongEntry[] entries;
            try { entries = Snapshot(); }
            catch (Exception error) { status.text = error.Message; return; }
            result = null;
            Render();
            status.text = "正在核对重复谱面的文件内容";
            summary.text = "正在扫描";
            cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var token = cancellation.Token;
            var task = Task.Run(() => DuplicateLibrary.Scan(root, entries, token), token);
            BeginWork(task);
            StartCoroutine(ScanFinished(task, revision));
        }
        void BeginWork(Task task)
        {
            work = task;
            // The owner also observes failures when Unity removes a view and stops its coroutine.
            task.ContinueWith(failed => Plugin.Log.Error("[operation] " + failed.Exception),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            Controls();
        }
        IEnumerator ScanFinished(Task<ScanResult> task, int startedRevision)
        {
            float scanStarted = Time.realtimeSinceStartup;
            int lastSecond = -1;
            while (!task.IsCompleted)
            {
                if (!System.Object.ReferenceEquals(work, task)) yield break;
                int seconds = (int)(Time.realtimeSinceStartup - scanStarted);
                if (seconds != lastSecond)
                {
                    lastSecond = seconds;
                    status.text = "正在核对重复谱面的文件内容（" + seconds + " 秒）";
                }
                yield return null;
            }
            if (!System.Object.ReferenceEquals(work, task)) yield break;
            EndWork(task);
            if (task.IsCanceled || startedRevision != revision)
            {
                status.text = "扫描已取消，请重新扫描";
                summary.text = "尚未完成扫描";
                yield break;
            }
            if (task.IsFaulted)
            {
                status.text = "扫描失败：" + Friendly(task.Exception.GetBaseException());
                summary.text = "扫描失败";
                yield break;
            }
            result = task.Result;
            selected = result.Groups.Count > 0 ? 0 : -1;
            copyPage = 0;
            Render();
            var warnings = new List<string>();
            if (result.DifferentContentGroups > 0) warnings.Add(result.DifferentContentGroups + " 组包含不同文件，已保留");
            if (result.Errors.Count > 0) warnings.Add(result.Errors.Count + " 个目录无法核对");
            status.text = warnings.Count == 0 ? "扫描完成" : String.Join("；", warnings.ToArray());
            if (result.Errors.Count > 0) Plugin.Log.Warn("[scan] " + String.Join("\n", result.Errors.ToArray()));
        }
        static string Friendly(Exception error)
        {
            if (error is UnauthorizedAccessException) return "无法访问谱面文件";
            if (error is IOException) return "文件被占用或发生变化，请重新扫描";
            return "操作未完成，请重新扫描后重试";
        }
        void EndWork(Task completed)
        {
            if (!System.Object.ReferenceEquals(work, completed)) return;
            work = null;
            if (cancellation != null) { cancellation.Dispose(); cancellation = null; }
            Controls();
        }
        void Render()
        {
            if (!parsed) return;
            list.Data.Clear();
            if (result != null)
                for (int index = 0; index < result.Groups.Count; index++)
                {
                    var group = result.Groups[index];
                    var label = Escape(group.Title);
                    if (index == selected) label = "<color=#69D8FF>› " + label + "</color>";
                    list.Data.Add(new CustomListTableData.CustomCellInfo(label,
                        "可清理 " + group.Remove.Count + " 份  " + Bytes(group.ReclaimBytes)));
                }
            list.TableView.ReloadData();
            summary.text = result == null ? "尚未完成扫描" : result.Groups.Count == 0 ?
                (result.Errors.Count > 0 ? "未能完成曲库核对" : "未发现可清理的重复谱面") :
                result.Groups.Count + " 组重复，可清理 " + result.Groups.Sum(group => group.Remove.Count) + " 份  " + Bytes(result.Groups.Sum(group => group.ReclaimBytes));
            ShowSelection();
            if (selected >= 0 && result != null && selected < result.Groups.Count) list.TableView.SelectCellWithIdx(selected, false);
            Controls();
        }
        [UIAction("select-group")]
        void SelectGroup(TableView table, int index)
        {
            if (Busy || result == null || index < 0 || index >= result.Groups.Count) return;
            selected = index;
            copyPage = 0;
            Render();
        }
        void ShowSelection()
        {
            if (result == null || selected < 0 || selected >= result.Groups.Count)
            {
                title.text = "";
                detail.text = result != null && result.Errors.Count > 0 ? "部分目录未能读取，请关闭占用谱面文件的程序后重新扫描。" : "";
                copyPageText.text = "";
                copyPrevious.interactable = copyNext.interactable = false;
                RefreshHints();
                return;
            }
            var group = result.Groups[selected];
            copyPage = Math.Max(0, Math.Min(copyPage, group.Remove.Count));
            title.text = group.Title;
            detail.text = (copyPage == 0 ? "保留\n\n" : "删除\n\n") +
                Path.GetFileName(copyPage == 0 ? group.Keep.Path : group.Remove[copyPage - 1].Path);
            copyPageText.text = (copyPage + 1) + " / " + (group.Remove.Count + 1);
            copyPrevious.interactable = copyPage > 0;
            copyNext.interactable = copyPage < group.Remove.Count;
            RefreshHints();
        }
        [UIAction("copy-prev")] void PreviousCopy() { if (!Busy) { copyPage--; ShowSelection(); } }
        [UIAction("copy-next")] void NextCopy() { if (!Busy) { copyPage++; ShowSelection(); } }
        static string Escape(string value) { return (value ?? "").Replace("<", "＜").Replace(">", "＞"); }
        static string Bytes(long size) { return size >= 1024L * 1024 * 1024 ? (size / (1024.0 * 1024 * 1024)).ToString("0.00") + " GB" : (size / (1024.0 * 1024)).ToString("0.0") + " MB"; }
        [UIAction("delete-group")]
        public void RequestSelected()
        {
            if (!Busy && result != null && selected >= 0 && selected < result.Groups.Count)
                Request(new[] { result.Groups[selected] });
        }
        [UIAction("delete-all")]
        public void RequestAll() { if (!Busy && result != null && result.Groups.Count > 0) Request(result.Groups.ToArray()); }
        void Request(DuplicateGroup[] groups)
        {
            if (SongCore.Loader.AreSongsLoading) { status.text = "曲库正在刷新，请稍后重试"; return; }
            selectedDeletion = groups;
            confirmationText.text = "删除 " + groups.Sum(group => group.Remove.Count) + " 份，保留 " + groups.Length + " 份。\n释放 " + Bytes(groups.Sum(group => group.ReclaimBytes)) + "。此操作无法撤销。";
            confirmation.Show(true);
            ConfirmVisible = true;
        }
        [UIAction("cancel-confirm")]
        public void CancelConfirmation()
        {
            selectedDeletion = null;
            if (ConfirmVisible && confirmation != null) confirmation.Hide(true);
            ConfirmVisible = false;
        }
        [UIAction("confirm-delete")]
        public void ConfirmDelete()
        {
            if (Busy || !ConfirmVisible || selectedDeletion == null) return;
            var groups = selectedDeletion;
            CancelConfirmation();
            if (SongCore.Loader.AreSongsLoading) { status.text = "曲库已更新，请重新扫描"; return; }
            Mutating = true;
            status.text = "正在清理重复谱面";
            cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var token = cancellation.Token;
            var task = Task.Run(() => DuplicateLibrary.Delete(root, groups, token), token);
            BeginWork(task);
            StartCoroutine(DeleteFinished(task));
        }
        IEnumerator DeleteFinished(Task<DeleteResult> task)
        {
            while (!task.IsCompleted) yield return null;
            if (!System.Object.ReferenceEquals(work, task)) yield break;
            string outcome;
            if (task.IsFaulted) outcome = "清理失败：" + Friendly(task.Exception.GetBaseException());
            else if (task.IsCanceled) outcome = "清理已取消";
            else
            {
                LastDeletion = task.Result;
                outcome = "已删除 " + LastDeletion.DeletedPaths.Count + " 份重复谱面";
                if (LastDeletion.Cancelled) outcome += "，清理已取消";
                if (LastDeletion.Errors.Count > 0)
                {
                    outcome += "，" + LastDeletion.Errors.Count + " 项失败，请重新扫描后重试";
                    Plugin.Log.Error("[delete] " + String.Join("\n", LastDeletion.Errors.ToArray()));
                }
            }
            status.text = outcome + "；正在刷新曲库";
            float readyDeadline = Time.realtimeSinceStartup + 60f;
            while (SongCore.Loader.AreSongsLoading && Time.realtimeSinceStartup < readyDeadline) yield return null;
            int beforeRefresh = revision;
            Exception refreshError = null;
            refreshing = true;
            try
            {
                if (SongCore.Loader.AreSongsLoading) throw new TimeoutException("An existing SongCore refresh did not finish");
                SongCore.Loader.Instance.RefreshSongs(true);
            }
            catch (Exception error) { refreshError = error; Plugin.Log.Error("[refresh] " + error); }
            float deadline = Time.realtimeSinceStartup + 60f;
            while (refreshError == null && revision == beforeRefresh && Time.realtimeSinceStartup < deadline) yield return null;
            bool refreshed = refreshError == null && revision != beforeRefresh && !SongCore.Loader.AreSongsLoading;
            EndWork(task);
            refreshing = false;
            Mutating = false;
            result = null;
            selected = -1;
            Render();
            summary.text = "清理结束";
            status.text = outcome + (refreshed ? "，曲库已刷新" : "；曲库刷新未完成，请重新扫描或重启游戏");
            if (!refreshed) Plugin.Log.Error("[refresh] SongCore refresh did not complete within 60 seconds");
            Controls();
        }
        public void CancelWork()
        {
            if (cancellation == null) return;
            var source = cancellation;
            var ownedTask = work;
            work = null;
            cancellation = null;
            source.Cancel();
            if (ownedTask != null && !ownedTask.IsCompleted)
                ownedTask.ContinueWith(completed => source.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            else source.Dispose();
        }
        protected override void OnDestroy()
        {
            CancelWork();
            if (subscribed)
            {
                SongCore.Loader.SongsLoadedEvent -= LibraryChanged;
                SongCore.Loader.LoadingStartedEvent -= LibraryLoading;
                subscribed = false;
            }
            base.OnDestroy();
        }
    }
}
