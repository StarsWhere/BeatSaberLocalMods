#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.IO;
using System.Security.Cryptography;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Settings;
using BGLib.Polyglot;
using HarmonyLib;
using HMUI;
using TMPro;
using UnityEngine;

namespace LocalModUI
{
    // One process-wide owner is loaded by LocalModUI. Native third-party page
    // policies remain scoped to the explicitly reviewed assemblies below.
    public static class InstantSettingsFlow
    {
        private const string HarmonyId = "localmodui.instant-settings";
        private static Coordinator? owner;

        public static void Register(object host, Action<Exception> report)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            try
            {
                if (owner == null)
                {
                    var created = new Coordinator(report);
                    try { created.Install(); }
                    catch { created.Dispose(); throw; }
                    owner = created;
                }
                owner.ChangeRegistration(host, true);
            }
            catch (Exception error) { report(error); }
        }

        public static void Unregister(object host) { owner?.ChangeRegistration(host, false); }

        private static void Begin(object __instance) { owner?.Begin(__instance); }
        private static void Opened(object __instance, ViewController __0) { owner?.Opened(__instance, __0); }
        private static bool Confirm(object __instance) { return owner == null || owner.Exit(__instance, true); }
        private static bool Return(object __instance) { return owner == null || owner.Exit(__instance, false); }

        private enum PagePolicy { Native, Instant, ApplyOnConfirm }

        // These third-party consumers were checked in the installed assemblies.
        // An updated DLL must be reviewed again rather than inheriting a rule
        // merely because it kept its class name or version declaration.
        private sealed class AuditedPage
        {
            internal readonly string Host, Assembly, Resource, Sha256;
            internal readonly PagePolicy Policy;
            internal AuditedPage(string host, string assembly, string resource, string sha256, PagePolicy policy)
            { Host = host; Assembly = assembly; Resource = resource; Sha256 = sha256; Policy = policy; }
        }
        private static readonly AuditedPage[] AuditedPages = {
            new AuditedPage("Vivify.Settings.SettingsMenu", "Vivify", "Vivify.Resources.Settings.bsml",
                "16466c3abcef940e3d8b0684b5e1d3985efce36d58627a154a4a04ea993e0837", PagePolicy.Instant),
            new AuditedPage("SongChartVisualizer.UI.ViewControllers.SettingsController", "SongChartVisualizer", "SongChartVisualizer.UI.Views.settings.bsml",
                "a9cf88a4ee85772adeec16f5b13826ee34928f45befb77188bef3bf8ea273331", PagePolicy.ApplyOnConfirm)
        };

        private sealed class Session
        {
            internal bool ApprovedVisited;
            internal bool ForeignVisited;
            internal bool Closing;
            internal readonly HashSet<object> DeferredMenus = new HashSet<object>();
            internal bool CanClose => ApprovedVisited && !ForeignVisited;
        }

        private sealed class Coordinator : IDisposable
        {
            private readonly Harmony harmony = new Harmony(HarmonyId);
            private readonly HashSet<object> hosts = new HashSet<object>();
            private readonly Dictionary<Assembly, string> assemblyHashes = new Dictionary<Assembly, string>();
            private readonly HashSet<LocalizedTextMeshProUGUI> detachedCaptions = new HashSet<LocalizedTextMeshProUGUI>();
            private readonly Action<Exception> report;
            private ConditionalWeakTable<object, Session> sessions = new ConditionalWeakTable<object, Session>();
            private readonly Type flowType = typeof(BSMLSettings).Assembly.GetType("BeatSaberMarkupLanguage.Settings.ModSettingsFlowCoordinator", true);
            private readonly FieldInfo flowField = AccessTools.Field(typeof(BSMLSettings), "modSettingsFlowCoordinator");
            private readonly FieldInfo aboutField = AccessTools.Field(typeof(BSMLSettings), "settingsMenu");
            private readonly MethodInfo getSettings = AccessTools.PropertyGetter(typeof(BSMLSettings), "Instance");
            private readonly PropertyInfo activeController;
            private readonly PropertyInfo animating;
            private readonly FieldInfo presenting;
            private readonly FieldInfo buttons;

            internal Coordinator(Action<Exception> report)
            {
                this.report = report;
                activeController = AccessTools.Property(flowType, "ActiveController");
                animating = AccessTools.Property(flowType, "IsAnimating");
                presenting = AccessTools.Field(flowType, "isPresenting");
                buttons = AccessTools.Field(flowType, "bottomButtons");
            }

            internal void Install()
            {
                var present = AccessTools.Method(typeof(BSMLSettings), "PresentSettings", Type.EmptyTypes);
                var open = AccessTools.Method(flowType, "OpenMenu", new[] { typeof(ViewController), typeof(bool), typeof(bool) });
                var ok = AccessTools.Method(flowType, "Ok", Type.EmptyTypes);
                var cancel = AccessTools.Method(flowType, "Cancel", Type.EmptyTypes);
                if (present == null || open == null || ok == null || cancel == null || flowField == null ||
                    aboutField == null || getSettings == null || activeController == null || animating == null ||
                    presenting == null || buttons == null)
                    throw new MissingMemberException("The installed BSML settings navigation interface is unsupported.");
                Patch(present, nameof(Begin), true);
                Patch(open, nameof(Opened), false);
                Patch(ok, nameof(Confirm), true);
                Patch(cancel, nameof(Return), true);
            }

            private void Patch(MethodInfo method, string patchName, bool prefix)
            {
                var patch = new HarmonyMethod(typeof(InstantSettingsFlow), patchName);
                harmony.Patch(method, prefix ? patch : null, prefix ? null : patch);
            }

            internal void ChangeRegistration(object host, bool add)
            {
                if (add) { hosts.Add(host); return; }
                if (!hosts.Remove(host)) return;
                // Menu hosts belong to a scene container. Never carry a
                // partially disposed container's navigation into a new scene.
                sessions = new ConditionalWeakTable<object, Session>();
                if (hosts.Count != 0) return;
                Dispose();
                owner = null;
            }

            internal void Begin(object settings)
            {
                object flow = flowField.GetValue(settings);
                if (((FlowCoordinator)flow).isActivated) return;
                sessions.Remove(flow);
                var session = new Session();
                sessions.Add(flow, session);
                try
                {
                    // BSML keeps ActiveController across ordinary closes.
                    // Account for a retained external page before any click.
                    object? view = activeController.GetValue(flow, null);
                    if (view != null) Visit(settings, flow, view, session);
                    Caption(flow, session.CanClose && session.DeferredMenus.Count == 0);
                }
                catch (Exception error) { session.ForeignVisited = true; Caption(flow, false); report(error); }
            }

            internal void Opened(object flow, ViewController view)
            {
                if (!sessions.TryGetValue(flow, out Session session) || session.Closing ||
                    !ReferenceEquals(activeController.GetValue(flow, null), view)) return;
                try { Visit(getSettings.Invoke(null, null), flow, view, session); }
                catch (Exception error) { session.ForeignVisited = true; Caption(flow, false); report(error); }
            }

            private void Visit(object settings, object flow, object view, Session session)
            {
                var menus = ((IEnumerable)AccessTools.Property(settings.GetType(), "SettingsMenus").GetValue(settings, null)).Cast<object>();
                object? menu = menus.FirstOrDefault(entry => ReferenceEquals(
                    AccessTools.Property(entry.GetType(), "ViewController").GetValue(entry, null), view));
                if (menu == null) session.ForeignVisited = true;
                else if (!ReferenceEquals(menu, aboutField.GetValue(settings)))
                {
                    object host = AccessTools.Property(menu.GetType(), "Host").GetValue(menu, null);
                    PagePolicy policy = hosts.Contains(host) ? PagePolicy.Instant : AuditedPolicy(menu, host);
                    if (policy == PagePolicy.Native) session.ForeignVisited = true;
                    else
                    {
                        session.ApprovedVisited = true;
                        if (policy == PagePolicy.ApplyOnConfirm) session.DeferredMenus.Add(menu);
                    }
                }
                Caption(flow, session.CanClose && session.DeferredMenus.Count == 0);
            }

            private PagePolicy AuditedPolicy(object menu, object host)
            {
                Type type = host.GetType();
                if (!ReferenceEquals(AccessTools.Property(menu.GetType(), "Assembly").GetValue(menu, null), type.Assembly))
                    return PagePolicy.Native;
                string resource = (string)AccessTools.Property(menu.GetType(), "Resource").GetValue(menu, null);
                foreach (var rule in AuditedPages)
                {
                    if (type.FullName != rule.Host || type.Assembly.GetName().Name != rule.Assembly || resource != rule.Resource) continue;
                    string hash;
                    if (!assemblyHashes.TryGetValue(type.Assembly, out hash))
                    {
                        using (var algorithm = SHA256.Create())
                        using (var stream = File.OpenRead(type.Assembly.Location))
                            hash = BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                        assemblyHashes.Add(type.Assembly, hash);
                    }
                    return hash == rule.Sha256 ? rule.Policy : PagePolicy.Native;
                }
                return PagePolicy.Native;
            }

            // true preserves BSML's original handler, including all events and
            // RestartGame for sessions that touched any other settings page.
            internal bool Exit(object flow, bool confirm)
            {
                if (!sessions.TryGetValue(flow, out Session session)) return true;
                if (session.Closing) return false;
                if (!session.CanClose) return true;
                if ((bool)animating.GetValue(flow, null) || (bool)presenting.GetValue(flow)) return false;
                try
                {
                    session.Closing = true;
                    // Preserve a reviewed page's own draft contract without
                    // broadcasting events to unvisited or unrelated plugins.
                    foreach (object menu in session.DeferredMenus)
                    {
                        var parser = (BeatSaberMarkupLanguage.Parser.BSMLParserParams)
                            AccessTools.Property(menu.GetType(), "ParserParams").GetValue(menu, null);
                        parser.EmitEvent(confirm ? "apply" : "cancel");
                    }
                    BeatSaberUI.DismissFlowCoordinator(BeatSaberUI.MainFlowCoordinator, (FlowCoordinator)flow,
                        null, ViewController.AnimationDirection.Vertical, false);
                    return false;
                }
                catch (Exception error)
                {
                    session.Closing = false;
                    report(error);
                    return false;
                }
            }

            private void Caption(object flow, bool instant)
            {
                var root = buttons.GetValue(flow) as Transform;
                if (root == null || root.childCount < 1) return;
                var label = root.GetChild(0).GetComponentInChildren<TMP_Text>(true);
                if (label == null) return;
                // This game has no BUTTON_BACK key. Let the mod's ordinary
                // Back caption translation own this label while instant-saving;
                // restore the native CANCEL binding for all other sessions.
                var binding = label.GetComponent<LocalizedTextMeshProUGUI>();
                if (binding != null)
                {
                    // Polyglot subscribes in Start and unsubscribes only in
                    // OnDestroy; disabling its Behaviour does not detach it.
                    if (instant && detachedCaptions.Add(binding)) Localization.Instance.RemoveOnLocalizeEvent(binding);
                    else if (!instant && detachedCaptions.Remove(binding)) Localization.Instance.AddOnLocalizeEvent(binding);
                }
                label.text = instant ? "Back" : Localization.Get("BUTTON_CANCEL");
            }

            public void Dispose()
            {
                harmony.UnpatchSelf();
                foreach (var binding in detachedCaptions)
                    if (binding != null) Localization.Instance.AddOnLocalizeEvent(binding);
                detachedCaptions.Clear();
                assemblyHashes.Clear();
                hosts.Clear();
                sessions = new ConditionalWeakTable<object, Session>();
            }
        }
    }
}
