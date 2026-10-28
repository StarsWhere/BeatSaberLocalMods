using System;
using System.Collections;
using System.Collections.Generic;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.MenuButtons;
using HMUI;
using UnityEngine;
using Zenject;
using HarmonyLib;
using System.Linq;

namespace LocalModUI
{
    public sealed class MenuContext : MonoBehaviour
    {
        internal static MenuContext Current { get; private set; }
        private MenuButtons buttons;
        private MainFlowCoordinator main;
        private CenterFlow flow;
        private FlowCoordinator parent;
        private MenuButton centerButton;
        private readonly Dictionary<string, MenuButton> shortcuts = new Dictionary<string, MenuButton>();
        private Coroutine pending;
        private Coroutine feedbackPending;
        private Coroutine leavePending;
        private bool leaveRequested;
        private ErrorOverlay overlay;
        private bool busy, quitting;
        private int operation;
        internal static UiCategory LastCategory = UiCategory.Training;
        internal bool Ready => !busy && !leaveRequested &&
            (flow != null && flow.isActivated ? flow.CanNavigate : main != null && main.isActivated && !main.isInTransition);

        private void SetBusy(bool value)
        {
            busy = value;
            if (centerButton != null) centerButton.Interactable = !value && !leaveRequested;
            foreach (var pair in shortcuts)
                pair.Value.Interactable = !value && !leaveRequested && UiRegistry.IsAvailable(UiRegistry.Find(pair.Key));
        }


        [Inject] public void Construct(MenuButtons menuButtons, MainFlowCoordinator mainFlow) { buttons = menuButtons; main = mainFlow; }
        private void Start()
        {
            Current = this;
            centerButton = new MenuButton("Custom mods", () => Open(null));
            buttons.RegisterButton(centerButton);
            UiRegistry.Changed += SyncEntries;
            SyncEntries();
        }

        private void SyncEntries()
        {
            foreach (var pair in shortcuts) buttons.UnregisterButton(pair.Value);
            shortcuts.Clear();
            foreach (var entry in UiRegistry.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.ShortcutTitle)) continue;
                var current = entry;
                var button = new MenuButton(current.ShortcutTitle, () =>
                {
                    if (!UiRegistry.IsAvailable(current)) { ShowError("This tool is not ready. Try again."); return; }
                    try { if (current.ShortcutAction != null) current.ShortcutAction(); else Open(current.Id); }
                    catch (Exception error) { UiRegistry.ReportError("Could not open the selected tool. Try again.", error); }
                });
                shortcuts.Add(entry.Id, button);
                buttons.RegisterButton(button);
            }
            SetBusy(busy);
            flow?.EntriesChanged();
        }

        internal void Open(string id)
        {
            if (!Ready) return;
            if (id != null && !UiRegistry.IsAvailable(UiRegistry.Find(id))) { ShowError("This tool is not ready. Try again."); return; }
            try
            {
                if (flow == null) { flow = BeatSaberUI.CreateFlowCoordinator<CenterFlow>(); flow.Owner = this; }
                if (flow.isActivated)
                {
                    if (!flow.isInTransition && id != null) flow.OpenEntry(id);
                    return;
                }
                parent = main.YoungestChildFlowCoordinatorOrSelf();
                SetBusy(true);
                int current = ++operation;
                parent.PresentFlowCoordinator(flow, animationDirection: ViewController.AnimationDirection.Horizontal);
                pending = StartCoroutine(WaitOpened(id, current));
            }
            catch (Exception error) { SetBusy(false); UiRegistry.ReportError("Could not open the mod menu. Try again.", error); }
        }

        private IEnumerator WaitOpened(string id, int current)
        {
            float deadline = Time.realtimeSinceStartup + 10;
            while (flow != null && (!flow.isActivated || flow.isInTransition))
            {
                if (current != operation) yield break;
                if (Time.realtimeSinceStartup >= deadline)
                {
                    SetBusy(false); pending = null;
                    Recover("Mod menu did not become ready. Try again.");
                    yield break;
                }
                yield return null;
            }
            SetBusy(false); pending = null;
            if (flow != null && id != null) flow.OpenEntry(id);
        }

        internal void Close(Action completed = null, bool immediately = false)
        {
            if (busy) return;
            if (flow == null || parent == null || (!flow.isActivated && !flow.isInTransition))
            { parent = null; completed?.Invoke(); return; }
            if (flow.childFlowCoordinator != null) return;
            SetBusy(true);
            int current = ++operation;
            pending = StartCoroutine(CloseRoutine(current, flow, parent, completed, immediately));
        }

        internal void LeaveWhenReady(Action completed)
        {
            if (leaveRequested) return;
            leaveRequested = true;
            SetBusy(busy);
            leavePending = StartCoroutine(WaitToLeave(flow, completed));
        }

        private IEnumerator WaitToLeave(CenterFlow expected, Action completed)
        {
            // A child tool may request this from inside its dismissal callback.
            // Wait until HMUI has detached that child before closing the center.
            yield return null;
            float deadline = Time.realtimeSinceStartup + 10;
            while (ReferenceEquals(flow, expected) && (busy || (flow != null && (flow.childFlowCoordinator != null || flow.isInTransition))))
            {
                if (Time.realtimeSinceStartup >= deadline)
                {
                    leaveRequested = false; leavePending = null; SetBusy(busy);
                    UiRegistry.ReportError("Could not close the mod menu. Try again.");
                    yield break;
                }
                yield return null;
            }
            leaveRequested = false; leavePending = null; SetBusy(busy);
            if (ReferenceEquals(flow, expected)) Close(completed);
        }

        internal void Recover(string message)
        {
            var failed = flow;
            Close(() =>
            {
                if (flow == failed)
                {
                    flow = null;
                    if (failed != null) Destroy(failed.gameObject);
                }
                ShowError(message);
            }, true);
        }

        private IEnumerator CloseRoutine(int current, CenterFlow closingFlow, FlowCoordinator closingParent, Action completed, bool immediately)
        {
            Exception failure = null;
            // HMUI must unwind the pushed settings view before dismissing its
            // owning flow; otherwise the restored main screen can stay empty.
            var reset = closingFlow.ResetPage();
            float resetDeadline = Time.realtimeSinceStartup + 2;
            while (!reset.IsCompleted && current == operation && Time.realtimeSinceStartup < resetDeadline) yield return null;
            if (current != operation) yield break;
            if (!reset.IsCompleted)
            {
                ++operation; SetBusy(false); pending = null;
                ShowError("Could not close this page. Try again.");
                yield break;
            }
            try { reset.GetAwaiter().GetResult(); }
            catch (Exception error) { failure = error; }
            if (failure != null)
            {
                ++operation; SetBusy(false); pending = null;
                UiRegistry.ReportError("Could not close this page. Try again.", failure);
                yield break;
            }
            try { BeatSaberUI.DismissFlowCoordinator(closingParent, closingFlow, null, ViewController.AnimationDirection.Horizontal, immediately); }
            catch (Exception error) { failure = error; }
            if (failure != null)
            {
                if (current == operation) { ++operation; SetBusy(false); pending = null; UiRegistry.ReportError("Could not close the mod menu. Try again.", failure); }
                yield break;
            }
            // HMUI's completion callback may run before it restores the parent
            // hierarchy. Continue only after the previous child is detached.
            yield return null;
            float deadline = Time.realtimeSinceStartup + 10;
            bool forced = false;
            while (current == operation && closingFlow != null &&
                (closingFlow.isActivated || closingFlow.isInTransition || closingParent.childFlowCoordinator == closingFlow))
            {
                if (Time.realtimeSinceStartup >= deadline)
                {
                    if (forced)
                    {
                        ++operation; SetBusy(false); pending = null;
                        ShowError("Could not close the mod menu. Try again.");
                        yield break;
                    }
                    forced = true;
                    deadline = Time.realtimeSinceStartup + 2;
                    try { BeatSaberUI.DismissFlowCoordinator(closingParent, closingFlow, null, ViewController.AnimationDirection.Horizontal, true); }
                    catch (Exception error) { failure = error; }
                    if (failure != null)
                    {
                        ++operation; SetBusy(false); pending = null;
                        UiRegistry.ReportError("Could not close the mod menu. Try again.", failure);
                        yield break;
                    }
                }
                yield return null;
            }
            if (current != operation) yield break;
            ++operation; SetBusy(false); pending = null; parent = null;
            try { completed?.Invoke(); }
            catch (Exception error) { UiRegistry.ReportError("Could not open the selected tool. Try again.", error); }
        }

        internal void ShowError(string message)
        {
            if (feedbackPending != null) { StopCoroutine(feedbackPending); feedbackPending = null; }
            if (!TryShowError(message)) feedbackPending = StartCoroutine(ShowErrorWhenReady(message));
        }

        private IEnumerator ShowErrorWhenReady(string message)
        {
            float deadline = Time.realtimeSinceStartup + 10;
            while (Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                if (TryShowError(message)) { feedbackPending = null; yield break; }
            }
            feedbackPending = null;
            Plugin.Log?.Warn("[navigation-feedback] No active menu view after timeout: " + message);
        }

        private bool TryShowError(string message)
        {
            if (flow != null && flow.isActivated && flow.CanShowError) { flow.SetError(message); return true; }
            try
            {
                var activeFlow = main?.YoungestChildFlowCoordinatorOrSelf();
                var view = activeFlow?.topViewController;
                if (view == null && activeFlow != null)
                    view = AccessTools.Field(typeof(FlowCoordinator), "_providedMainViewController").GetValue(activeFlow) as ViewController;
                if (view == null || !view.gameObject.activeInHierarchy)
                    view = Resources.FindObjectsOfTypeAll<MainMenuViewController>().FirstOrDefault(value => value.isActivated && value.gameObject.activeInHierarchy);
                if (view == null)
                    view = Resources.FindObjectsOfTypeAll<ViewController>().FirstOrDefault(value => value.GetType().Name == "MenuButtonsViewController" && value.isActivated && value.gameObject.activeInHierarchy);
                if (view == null) return false;
                Plugin.Log?.Debug("[navigation-feedback] " + view.GetType().Name + ": " + message);
                if (overlay != null) Destroy(overlay.gameObject);
                var root = new GameObject("LocalModUI.Error", typeof(RectTransform));
                root.transform.SetParent(view.transform, false);
                overlay = root.AddComponent<ErrorOverlay>();
                overlay.Show(message);
                return true;
            }
            catch (Exception exception) { Plugin.Log?.Error("[error-overlay] " + exception); return true; }
        }

        private void OnApplicationQuit() { quitting = true; }
        private void OnDestroy()
        {
            UiRegistry.Changed -= SyncEntries;
            ++operation;
            if (pending != null) StopCoroutine(pending);
            if (feedbackPending != null) StopCoroutine(feedbackPending);
            if (leavePending != null) StopCoroutine(leavePending);
            pending = null; busy = false;
            if (!quitting && buttons != null)
            {
                if (centerButton != null) buttons.UnregisterButton(centerButton);
                foreach (var button in shortcuts.Values) buttons.UnregisterButton(button);
            }
            shortcuts.Clear();
            if (overlay != null) Destroy(overlay.gameObject);
            if (flow != null) { flow.ClosePage(); Destroy(flow.gameObject); }
            if (Current == this) Current = null;
        }
    }
}
