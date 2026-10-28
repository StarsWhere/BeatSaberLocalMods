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
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace EffectModsSmoke;

public sealed partial class Runner
{
    object trainingUiLanguageHost;
    bool trainingUiHudCaptured;
    float trainingFeedbackVerifiedAt = -1f;

    void CaptureTrainingHudIfReady(float songTime)
    {
        if (trainingUiHudCaptured || !trainingEnergyHudSeen || songTime < 10f) return;
        Camera camera = Resources.FindObjectsOfTypeAll<Camera>()
            .FirstOrDefault(value => value != null && value.name == "MainCamera" &&
                value.gameObject.activeInHierarchy && value.gameObject.scene.name != "MainMenu");
        if (camera == null) return;
        if (trainingFeedbackVerifiedAt < 0f)
        {
            VerifyNativeFeedbackLocalization();
            trainingFeedbackVerifiedAt = Time.unscaledTime;
            return;
        }
        if (Time.unscaledTime - trainingFeedbackVerifiedAt < 0.6f) return;
        CaptureTrainingUiPage("gameplay-energy", camera);
        trainingUiHudCaptured = true;
    }
    void VerifyNativeFeedbackLocalization()
    {
        Type adapter = FindTrainingType("ModUiChinese.GameplayFeedback");
        if (adapter == null) return;
        Type spawnerType = FindTrainingType("MissedNoteEffectSpawner");
        object controller = Resources.FindObjectsOfTypeAll(spawnerType).FirstOrDefault();
        object spawner = AccessTools.Field(spawnerType, "_missedNoteFlyingSpriteSpawner").GetValue(controller);
        var spriteField = AccessTools.Field(spawner.GetType(), "_sprite");
        var original = spriteField.GetValue(spawner) as Sprite;
        Type panelType = FindTrainingType("ModUiChinese.LocalizationPanel");
        object panel = Activator.CreateInstance(panelType, true);
        var enabled = panelType.GetProperty("Enabled");
        var selected = panelType.GetProperty("SelectedScope");
        var scopeEnabled = panelType.GetProperty("ScopeEnabled");
        selected.SetValue(panel, "GameFeedback", null);
        bool oldEnabled = (bool)enabled.GetValue(panel, null);
        bool oldScopeEnabled = (bool)scopeEnabled.GetValue(panel, null);
        object state = null;
        var prefix = adapter.GetMethod("MissPrefix");
        var finalizer = adapter.GetMethod("MissFinalizer");
        try
        {
            enabled.SetValue(panel, true, null);
            scopeEnabled.SetValue(panel, true, null);
            var spriteProbe = new GameObject("TrainingMissSpriteProbe", typeof(SpriteRenderer));
            try
            {
                spriteProbe.GetComponent<SpriteRenderer>().sprite = original;
                var captured = (object[])adapter.GetMethod("Capture").Invoke(null, null);
                Check("training-localization-native-miss-detection", captured.Any(value =>
                    Convert.ToString(GetMember(value, "path"), CultureInfo.InvariantCulture) == "GameplayFeedback/TrainingMissSpriteProbe" &&
                    Convert.ToString(GetMember(value, "text"), CultureInfo.InvariantCulture) == "Miss"), "Untranslated native feedback is included in automatic audit snapshots");
            }
            finally { UnityEngine.Object.Destroy(spriteProbe); }
            var args = new object[] { controller, null };
            prefix.Invoke(null, args);
            state = args[1];
            var localized = spriteField.GetValue(spawner) as Sprite;
            Check("training-localization-native-miss-image", state != null && localized != null && localized != original && localized.name.EndsWith("漏击", StringComparison.Ordinal), localized?.name);
            Check("training-localization-native-miss-size", localized != null && original != null && localized.rect.size == original.rect.size && localized.pivot == original.pivot && localized.pixelsPerUnit == original.pixelsPerUnit, "Sprite dimensions, pivot and scale");
            finalizer.Invoke(null, new object[] { null, state });
            state = null;
            Check("training-localization-native-miss-spawner-restore", (spriteField.GetValue(spawner) as Sprite) == original, "Native sprite restored after spawn");
            enabled.SetValue(panel, false, null);
            args[1] = null;
            prefix.Invoke(null, args);
            state = args[1];
            Check("training-localization-native-miss-global-off", state == null && (spriteField.GetValue(spawner) as Sprite) == original, "Global language off uses original image");
            enabled.SetValue(panel, true, null);
            scopeEnabled.SetValue(panel, false, null);
            args[1] = null;
            prefix.Invoke(null, args);
            state = args[1];
            Check("training-localization-native-miss-scope-off", state == null && (spriteField.GetValue(spawner) as Sprite) == original, "Feedback language off uses original image");
            scopeEnabled.SetValue(panel, true, null);
            Type translator = FindTrainingType("ModUiChinese.UiTranslator");
            var engineField = translator.GetField("Engine");
            object previousEngine = engineField.GetValue(null);
            Type catalogType = FindTrainingType("ModUiChinese.GeneratedCatalog");
            var cache = AccessTools.Field(adapter, "images").GetValue(null) as ICollection;
            try
            {
                foreach (string caption in new[] { "漏", "未击" })
                {
                    var catalog = (IDictionary<string, Dictionary<string, string>>)catalogType.GetMethod("Create").Invoke(null, null);
                    catalog["GameFeedback"]["Miss"] = caption;
                    engineField.SetValue(null, Activator.CreateInstance(previousEngine.GetType(), new object[] { catalog }));
                    AccessTools.Method(adapter, "Restore").Invoke(null, new object[] { false });
                    AccessTools.Method(adapter, "Refresh").Invoke(null, null);
                    args[1] = null;
                    prefix.Invoke(null, args);
                    state = args[1];
                    localized = spriteField.GetValue(spawner) as Sprite;
                    Check("training-localization-native-miss-custom-" + caption, state != null && localized != null && localized.name.EndsWith(caption, StringComparison.Ordinal), localized?.name);
                    finalizer.Invoke(null, new object[] { null, state });
                    state = null;
                    Check("training-localization-native-miss-cache-" + caption, cache != null && cache.Count == 1, "Current sprite cache=" + cache?.Count);
                }
            }
            finally
            {
                if (state != null) { finalizer.Invoke(null, new object[] { null, state }); state = null; }
                AccessTools.Method(adapter, "Restore").Invoke(null, new object[] { false });
                engineField.SetValue(null, previousEngine);
                AccessTools.Method(adapter, "Refresh").Invoke(null, null);
            }
        }
        finally
        {
            if (state != null) finalizer.Invoke(null, new object[] { null, state });
            scopeEnabled.SetValue(panel, oldScopeEnabled, null);
            enabled.SetValue(panel, oldEnabled, null);
        }
    }
    /// <summary>
    /// Opens the real BSML settings flow and checks the rendered pages.  This
    /// is intentionally a partial Runner so it can use the existing native
    /// camera/render helper without changing the normal smoke path.
    /// </summary>
    public IEnumerator VerifyTrainingUi()
    {
        if (Environment.GetEnvironmentVariable("EFFECT_SMOKE_TRAINING_UI") != "1")
            yield break;

        if (Environment.GetEnvironmentVariable("EFFECT_SMOKE_FULL_MOD_UI_AUDIT") == "1")
        {
            if (String.IsNullOrEmpty(Environment.GetEnvironmentVariable("EFFECT_SMOKE_UI_FOCUS")) || Environment.GetEnvironmentVariable("EFFECT_SMOKE_UI_FOCUS") == "Settings")
            {
                yield return VerifyTrainingMenuShortcuts();
                yield return VerifyModUiCaptionContracts();
            }
            yield return VerifyAllModUi();
            yield break;
        }

        EnsureTrainingPluginState();
        yield return VerifyTrainingMenuShortcuts();
        ModUiOwnershipPresetNameProbe();

        Type settingsType = FindTrainingType("BeatSaberMarkupLanguage.Settings.BSMLSettings");
        object settings = FindBsmlSettingsInstance(settingsType);
        Check("training-ui-bsml-settings", settings != null,
            settings == null ? "BSMLSettings instance is unavailable." : settings.GetType().AssemblyQualifiedName);

        object flow = GetMember(settings, "modSettingsFlowCoordinator");
        MethodInfo present = settingsType == null ? null : settingsType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == "PresentSettings" && method.GetParameters().Length == 0);
        Check("training-ui-bsml-present-api", flow != null && present != null,
            "flow=" + (flow == null ? "missing" : flow.GetType().FullName) +
            "; PresentSettings=" + (present == null ? "missing" : "private zero-argument"));

        try
        {
            present.Invoke(settings, null);
        }
        catch (TargetInvocationException error)
        {
            throw new InvalidOperationException("BSMLSettings.PresentSettings failed: " +
                (error.InnerException ?? error).Message, error.InnerException ?? error);
        }
        yield return Wait(() => IsFlowActive(flow), 15f, "training-settings-flow");
        yield return Wait(() => !ReadBool(flow, "IsAnimating", true) &&
            !ReadBool(flow, "isPresenting", false), 10f, "training-settings-flow-ready");

        MethodInfo openMenu = flow.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.Name == "OpenMenu" && method.GetParameters().Length == 1)
            .FirstOrDefault(method =>
            {
                Type parameter = method.GetParameters()[0].ParameterType;
                return parameter.FullName == "BeatSaberMarkupLanguage.Settings.SettingsMenu";
            });
        Check("training-ui-bsml-open-api", openMenu != null,
            "ModSettingsFlowCoordinator.OpenMenu(SettingsMenu)=" + (openMenu == null ? "missing" : "available"));

        var targets = new[]
        {
            new TrainingUiTarget("note-training", "NoteMotionControl.SettingsMenu"),
            new TrainingUiTarget("mod-ui-chinese", "ModUiChinese.LocalizationPanel"),
        };

        object entriesObject = GetMember(settings, "SettingsMenus");
        var entries = entriesObject is IEnumerable values ? values.Cast<object>().ToArray() : Array.Empty<object>();
        trainingUiLanguageHost = entries.Select(value => GetMember(value, "Host"))
            .FirstOrDefault(value => value?.GetType().FullName == "ModUiChinese.LocalizationPanel");
        foreach (TrainingUiTarget target in targets)
        {
            if (!ShouldCheckTrainingTarget(target.HostTypeName)) continue;
            object entry = entries.FirstOrDefault(value =>
                {
                    object host = GetMember(value, "Host");
                    return host != null && string.Equals(host.GetType().FullName, target.HostTypeName,
                        StringComparison.Ordinal);
                });
            Check("training-ui-entry-" + target.Slug, entry != null,
                "SettingsMenus entry for " + target.HostTypeName + " was not found.");
            if (entry == null) continue;

            EnsureSettingsMenuSetup(entry);
            yield return OpenAndCheckTrainingPage(flow, openMenu, entry, target.Slug,
                target.HostTypeName);
        }

        if (trainingEnergyLoaded)
        {
            object counter = FindActivityEnergyCounterRegistration();
            Check("training-ui-entry-energy", counter != null,
                "Counters+ did not expose the Activity Energy custom-counter registration.");
            if (counter != null)
            {
                object energyEntry = CreateEnergySettingsMenu(counter);
                Check("training-ui-energy-menu", energyEntry != null,
                    "Could not create the native BSML SettingsMenu from Counters+ registration.");
                if (energyEntry != null)
                {
                    EnsureSettingsMenuSetup(energyEntry);
                    yield return OpenAndCheckTrainingPage(flow, openMenu, energyEntry, "energy",
                        "ActivityEnergyCounter.EnergySettingsHost");
                }
            }
        }
    }

    sealed class TrainingUiTarget
    {
        public readonly string Slug;
        public readonly string HostTypeName;

        public TrainingUiTarget(string slug, string hostTypeName)
        {
            Slug = slug;
            HostTypeName = hostTypeName;
        }
    }

    bool ShouldCheckTrainingTarget(string hostTypeName)
    {
        if (hostTypeName == "NoteMotionControl.SettingsMenu") return trainingNoteLoaded;
        if (hostTypeName == "ModUiChinese.LocalizationPanel")
            return FindTrainingType(hostTypeName) != null;
        return true;
    }

    static bool IsFlowActive(object flow)
    {
        try
        {
            Component component = flow as Component;
            return component != null && component.gameObject.activeInHierarchy &&
                (ReadBool(flow, "isActivated", false) || ReadBool(flow, "IsActivated", false));
        }
        catch
        {
            return false;
        }
    }

    static void EnsureSettingsMenuSetup(object entry)
    {
        if (entry == null) return;
        if (GetMember(entry, "ViewController") == null || GetMember(entry, "ParserParams") == null)
        {
            MethodInfo setup = AccessTools.Method(entry.GetType(), "Setup");
            if (setup == null) throw new MissingMethodException("BSML SettingsMenu.Setup");
            setup.Invoke(entry, null);
        }
    }

    IEnumerator OpenAndCheckTrainingPage(object flow, MethodInfo openMenu, object entry,
        string slug, string hostTypeName)
    {
        yield return Wait(() => !ReadBool(flow, "isPresenting", false), 10f, "training-page-navigation-ready");
        object viewObject = GetMember(entry, "ViewController");
        Component view = viewObject as Component;
        Check("training-ui-view-" + slug, view != null,
            "SettingsMenu.ViewController is " + (viewObject == null ? "null" : viewObject.GetType().FullName));
        if (view == null) yield break;

        try
        {
            openMenu.Invoke(flow, new[] { entry });
        }
        catch (TargetInvocationException error)
        {
            throw new InvalidOperationException("OpenMenu failed for " + slug + ": " +
                (error.InnerException ?? error).Message, error.InnerException ?? error);
        }
        yield return Wait(() => IsTrainingSettingsViewActive(view), 15f, "training-settings-" + slug);
        // BSML builds the native settings container and its child layout over
        // several canvas passes.  Checking immediately after activation can
        // observe every TMP renderer before RectMask2D has applied its final
        // clip state.  Wait for a stable layout while keeping the cull check
        // below as an actual rendering assertion.
        yield return ModUiAuditWaitLayout(view, 4, 4f);
        yield return null;
        Canvas.ForceUpdateCanvases();
        yield return new WaitForEndOfFrame();

        Camera camera = FindTrainingMenuCamera();
        Check("training-ui-camera-" + slug, camera != null,
            "An active menu camera was not available for the actual settings page.");
        if (camera == null) yield break;

        VerifyTrainingUiRoots(hostTypeName, view, entry);
        VerifyTrainingUiLayout(slug, view, camera);
        yield return VerifyTrainingUiModes(slug, hostTypeName, view, entry, GetMember(entry, "Host"));
        PropertyInfo language = trainingUiLanguageHost?.GetType().GetProperty("Enabled");
        bool originalLanguage = language != null && (bool)language.GetValue(trainingUiLanguageHost, null);
        try
        {
            foreach (bool chinese in new[] { true, false })
            {
                language?.SetValue(trainingUiLanguageHost, chinese, null);
                yield return new WaitForSecondsRealtime(0.6f);
                Canvas.ForceUpdateCanvases();
                Component scroll = FindTrainingScrollContainer(view);
                if (scroll != null) AccessTools.Method(scroll.GetType(), "ScrollTo", new[] { typeof(float), typeof(bool) })
                    .Invoke(scroll, new object[] { 0f, false });
                yield return null;
                string suffix = chinese ? "-zh" : "-en";
                CaptureTrainingUiPage(slug + suffix, camera);
                if (scroll != null)
                {
                    AccessTools.Method(typeof(HMUI.ScrollView), "ScrollToEnd").Invoke(scroll, new object[] { false });
                    yield return null;
                    CaptureTrainingUiPage(slug + suffix + "-bottom", camera);
                    AccessTools.Method(scroll.GetType(), "ScrollTo", new[] { typeof(float), typeof(bool) })
                        .Invoke(scroll, new object[] { 0f, false });
                }
            }
        }
        finally { language?.SetValue(trainingUiLanguageHost, originalLanguage, null); }
    }

    void VerifyTrainingUiRoots(string hostTypeName, Component view, object entry)
    {
        string rootId = hostTypeName == "NoteMotionControl.SettingsMenu" ? "training-settings-root" :
            hostTypeName == "ModUiChinese.LocalizationPanel" ? "language-settings-root" : null;
        if (string.IsNullOrEmpty(rootId)) return;
        object declared = ResolveBsmlTag(entry, rootId);
        Component scroll = FindTrainingScrollContainer(view);
        bool valid = declared != null && scroll != null;
        Check("training-ui-root-" + rootId, valid,
            "declared=" + (declared == null ? "unbound" : declared.GetType().FullName) +
            "; nativeScrollableRoot=" + (scroll == null ? "missing" : scroll.gameObject.name));
    }

    static bool IsTrainingSettingsViewActive(Component view)
    {
        try
        {
            return view != null && view.gameObject.activeInHierarchy &&
                (ReadBool(view, "isActivated", false) || ReadBool(view, "IsActivated", false));
        }
        catch
        {
            return false;
        }
    }

    void VerifyTrainingUiLayout(string slug, Component view, Camera camera)
    {
        TMP_Text[] texts = view.GetComponentsInChildren<TMP_Text>(true)
            .Where(value => value != null && value.gameObject.activeInHierarchy && value.enabled).ToArray();
        Selectable[] controls = view.GetComponentsInChildren<Selectable>(true)
            .Where(value => value != null && value.gameObject.activeInHierarchy && value.enabled).ToArray();
        bool hasViewport;
        Rect viewport = TrainingViewportRect(view, camera, out hasViewport);
        float cameraWidth = Mathf.Max(1f, camera.pixelWidth);
        float cameraHeight = Mathf.Max(1f, camera.pixelHeight);
        if (hasViewport)
            Check("training-ui-viewport-bounds-" + slug,
                viewport.xMin >= -12f && viewport.xMax <= cameraWidth + 12f &&
                viewport.yMin >= -12f && viewport.yMax <= cameraHeight + 12f,
                "viewport=" + viewport + "; camera=" + cameraWidth + "x" + cameraHeight);

        int viewportVisibleText = texts.Count(value =>
            TrainingElementIntersects(value.rectTransform, camera, hasViewport, viewport));
        int geometryVisibleText = texts.Count(value =>
            TrainingUiElementVisible(value.rectTransform, camera, hasViewport, viewport));
        int visibleText = texts.Count(value => value.canvasRenderer != null && !value.canvasRenderer.cull &&
            TrainingUiElementVisible(value.rectTransform, camera, hasViewport, viewport));
        int culledViewportText = texts.Count(value => value.canvasRenderer != null && value.canvasRenderer.cull &&
            TrainingElementIntersects(value.rectTransform, camera, hasViewport, viewport));
        int visibleControlsByViewport = controls.Count(value =>
            value.transform is RectTransform rect && TrainingElementIntersects(rect, camera, hasViewport, viewport));
        int visibleControls = controls.Count(value =>
            value.transform is RectTransform rect && TrainingUiElementVisible(rect, camera, hasViewport, viewport));
        string visibleTextSamples = String.Join(" | ", texts
            .Where(value => TrainingElementIntersects(value.rectTransform, camera, hasViewport, viewport))
            .Take(8)
            .Select(value => ModUiAuditPath(value.transform) + ":cull=" +
                (value.canvasRenderer != null && value.canvasRenderer.cull) + ":text=" +
                (value.text ?? String.Empty).Replace("|", "/")));
        Check("training-ui-content-" + slug, geometryVisibleText > 0 && visibleControls > 0,
            "activeText=" + texts.Length + "; activeControls=" + controls.Length +
            "; viewportVisibleText=" + viewportVisibleText + "; geometryVisibleText=" + geometryVisibleText +
            "; visibleText=" + visibleText + "; culledViewportText=" + culledViewportText +
            "; visibleControlsByViewport=" + visibleControlsByViewport + "; visibleControls=" + visibleControls +
            "; viewport=" + (hasViewport ? viewport.ToString() : "screen"));
        Check("training-ui-visible-text-cull-" + slug,
            viewportVisibleText == 0 || culledViewportText < viewportVisibleText,
            "Visible text remains renderer-culled after layout stabilization; samples=" + visibleTextSamples +
            "; viewportVisibleText=" + viewportVisibleText + "; geometryVisibleText=" + geometryVisibleText +
            "; visibleText=" + visibleText + "; culledViewportText=" + culledViewportText +
            "; scrollViews=" + view.GetComponentsInChildren<HMUI.ScrollView>(true).Length +
            "; rectMasks=" + view.GetComponentsInChildren<RectMask2D>(true).Length);

        var invalidControls = controls.Where(value =>
        {
            RectTransform rect = value.transform as RectTransform;
            return rect == null || rect.rect.width < 4f || rect.rect.height < 4f;
        }).Select(value => value.name + ":" + (value.transform as RectTransform)?.rect.ToString()).ToArray();
        Check("training-ui-control-size-" + slug, invalidControls.Length == 0,
            "invalid=" + string.Join(" | ", invalidControls));

        var invalidControlBounds = controls.Where(value =>
        {
            RectTransform rect = value.transform as RectTransform;
            if (rect == null) return false;
            Rect screen = TrainingScreenRect(rect, camera);
            bool intersects = TrainingElementIntersects(rect, camera, hasViewport, viewport);
            if (hasViewport) return false; // The viewport mask intentionally clips its content.
            return intersects && (screen.xMin < -12f || screen.xMax > cameraWidth + 12f ||
                screen.yMin < -12f || screen.yMax > cameraHeight + 12f);
        }).Select(value => value.name + ":" + TrainingScreenRect(value.transform as RectTransform, camera)).ToArray();
        Check("training-ui-control-bounds-" + slug, invalidControlBounds.Length == 0,
            "overflow=" + string.Join(" | ", invalidControlBounds));

        var invalidTextBounds = texts.Where(value =>
        {
            Rect screen = TrainingScreenRect(value.rectTransform, camera);
            bool intersects = TrainingElementIntersects(value.rectTransform, camera, hasViewport, viewport);
            if (hasViewport) return false; // The viewport mask intentionally clips its content.
            return intersects && (screen.xMin < -12f || screen.xMax > cameraWidth + 12f ||
                screen.yMin < -12f || screen.yMax > cameraHeight + 12f);
        }).Select(value => value.name + ":" + TrainingScreenRect(value.rectTransform, camera)).ToArray();
        Check("training-ui-text-bounds-" + slug, invalidTextBounds.Length == 0,
            "overflow=" + string.Join(" | ", invalidTextBounds));
    }

    static Rect TrainingViewportRect(Component view, Camera camera, out bool found)
    {
        Component scroll = FindTrainingScrollContainer(view);
        RectTransform viewport = scroll == null ? null : AccessTools.Field(typeof(HMUI.ScrollView), "_viewport").GetValue(scroll) as RectTransform;
        found = viewport != null;
        return found ? TrainingScreenRect(viewport, camera) :
            Rect.MinMaxRect(0f, 0f, Mathf.Max(1f, camera.pixelWidth), Mathf.Max(1f, camera.pixelHeight));
    }

    static bool TrainingElementIntersects(RectTransform element, Camera camera, bool hasViewport, Rect viewport)
    {
        if (element == null) return false;
        Rect screen = TrainingScreenRect(element, camera);
        Rect visibleArea = hasViewport ? viewport :
            Rect.MinMaxRect(0f, 0f, Mathf.Max(1f, camera.pixelWidth), Mathf.Max(1f, camera.pixelHeight));
        return screen.xMax > visibleArea.xMin && screen.xMin < visibleArea.xMax &&
            screen.yMax > visibleArea.yMin && screen.yMin < visibleArea.yMax;
    }

    static bool TrainingUiElementVisible(RectTransform element, Camera camera, bool hasViewport, Rect viewport)
    {
        if (element == null || camera == null || !ModUiAuditElementVisible(element, camera)) return false;
        return TrainingElementIntersects(element, camera, hasViewport, viewport);
    }

    static Rect TrainingScreenRect(RectTransform rect, Camera camera)
    {
        Vector3[] corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        Vector3 a = camera.WorldToScreenPoint(corners[0]);
        Vector3 b = camera.WorldToScreenPoint(corners[1]);
        Vector3 c = camera.WorldToScreenPoint(corners[2]);
        Vector3 d = camera.WorldToScreenPoint(corners[3]);
        float minX = Mathf.Min(a.x, b.x, c.x, d.x);
        float maxX = Mathf.Max(a.x, b.x, c.x, d.x);
        float minY = Mathf.Min(a.y, b.y, c.y, d.y);
        float maxY = Mathf.Max(a.y, b.y, c.y, d.y);
        return Rect.MinMaxRect(minX, minY, maxX, maxY);
    }

    IEnumerator VerifyTrainingUiModes(string slug, string hostTypeName, Component view, object entry, object host)
    {
        if (host == null) yield break;
        Canvas.ForceUpdateCanvases();
        VerifyTrainingUiConditionals(hostTypeName, view, entry, host);

        foreach (ModeProbe probe in GetModeProbes(hostTypeName))
        {
            PropertyInfo property = host.GetType().GetProperty(probe.Property,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object choicesObject = ReadMemberAny(host, probe.Choices);
            var choices = choicesObject is IEnumerable values ? values.Cast<object>().ToArray() : Array.Empty<object>();
            Check("training-ui-mode-options-" + slug + "-" + probe.Property,
                property != null && property.CanRead && property.CanWrite && choices.Length >= 2,
                "property=" + (property == null ? "missing" : property.PropertyType.FullName) +
                "; choices=" + choices.Length);
            if (property == null || !property.CanRead || !property.CanWrite || choices.Length < 2) continue;

            if (hostTypeName == "NoteMotionControl.SettingsMenu" && probe.Property == "NjsMode")
                Check("training-ui-motion-three-speed-modes", choices.Select(choice => Convert.ToString(choice, CultureInfo.InvariantCulture))
                    .SequenceEqual(new[] { "Map", "Override", "Multiplier" }),
                    "Actual BSML choices: " + string.Join(", ", choices));
            if (hostTypeName == "NoteMotionControl.SettingsMenu" && probe.Property == "ReductionMode")
                Check("training-ui-reduction-three-modes", choices.Select(choice => Convert.ToString(choice, CultureInfo.InvariantCulture))
                    .SequenceEqual(new[] { "Off", "Density", "Percentage" }),
                    "Actual BSML choices: " + string.Join(", ", choices));
            if (hostTypeName == "NoteMotionControl.SettingsMenu" && probe.Property == "ApplicationPolicy")
                Check("training-ui-application-policy-two-modes", choices.Select(choice => Convert.ToString(choice, CultureInfo.InvariantCulture))
                    .SequenceEqual(new[] { "Only reduce", "Strict" }),
                    "Actual BSML choices: " + string.Join(", ", choices));
            if (hostTypeName == "NoteMotionControl.SettingsMenu" && probe.Property == "ReactionTimeMode")
                Check("training-ui-reaction-time-two-modes", choices.Select(choice => Convert.ToString(choice, CultureInfo.InvariantCulture))
                    .SequenceEqual(new[] { "Game", "Specified" }),
                    "Actual BSML choices: " + string.Join(", ", choices));
            object original = property.GetValue(host, null);
            var alternates = choices.Where(choice =>
                !string.Equals(Convert.ToString(choice, CultureInfo.InvariantCulture),
                    Convert.ToString(original, CultureInfo.InvariantCulture), StringComparison.Ordinal)).ToArray();
            try
            {
                for (int index = 0; index < alternates.Length; index++)
                {
                    object alternate = alternates[index];
                    property.SetValue(host, ConvertModeValue(alternate, property.PropertyType), null);
                    yield return null;
                    Canvas.ForceUpdateCanvases();
                    VerifyTrainingUiConditionals(hostTypeName, view, entry, host);
                    int activeControls = view.GetComponentsInChildren<Selectable>(true)
                        .Count(value => value != null && value.gameObject.activeInHierarchy && value.enabled);
                    Check("training-ui-mode-visible-" + slug + "-" + probe.Property + "-" + index,
                        activeControls > 0 && string.Equals(Convert.ToString(property.GetValue(host, null), CultureInfo.InvariantCulture),
                            Convert.ToString(alternate, CultureInfo.InvariantCulture), StringComparison.Ordinal),
                        "alternate=" + alternate + "; activeControls=" + activeControls);
                }
            }
            finally
            {
                property.SetValue(host, ConvertModeValue(original, property.PropertyType), null);
            }
            yield return null;
            Canvas.ForceUpdateCanvases();
            VerifyTrainingUiConditionals(hostTypeName, view, entry, host);
        }
        if (hostTypeName == "NoteMotionControl.SettingsMenu")
        {
            string[] removed = { "StableNative", "StableEffects", "PositionSnap", "AllowSpeedJumps", "DistanceMode", "ReadingTimeMs", "ManualDistanceMeters", "PositionSmoothingMs", "SmoothMapSpeed", "SmoothingMs" };
            Check("training-ui-motion-controls-current", removed.All(name => host.GetType().GetProperty(name) == null) &&
                host.GetType().GetProperty("SpeedMultiplier") != null &&
                host.GetType().GetProperty("SelectedScope") == null &&
                host.GetType().GetProperty("Scope") == null &&
                host.GetType().GetMethod("RestoreGlobal", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) == null,
                "Merged global settings expose motion, reduction, reaction and preset controls without chart scope controls");

            object presets = ReadMemberAny(host, "Presets");
            string[] expectedPresets = { "Original", "Entry", "Easy", "Normal", "Hard", "Half", "Stable", "Early", "Custom" };
            Check("training-ui-eight-presets", presets is IEnumerable values &&
                values.Cast<object>().Select(value => Convert.ToString(value, CultureInfo.InvariantCulture)).SequenceEqual(expectedPresets),
                "Actual presets: " + (presets is IEnumerable actual ? string.Join(", ", actual.Cast<object>()) : "missing"));
        }
    }

    void VerifyTrainingUiConditionals(string hostTypeName, Component view, object entry, object host)
    {
        if (hostTypeName == "NoteMotionControl.SettingsMenu")
        {
            TMP_Text[] labels = view.GetComponentsInChildren<TMP_Text>(true);
            Check("training-ui-reaction-time-mode-label", labels.Any(value => value != null &&
                (value.text == "Reaction time mode" || value.text == "反应时间模式")),
                "Reaction time mode label was not rendered.");
            string njs = Convert.ToString(ReadMemberAny(host, "NjsMode"), CultureInfo.InvariantCulture) ?? string.Empty;
            string reduction = Convert.ToString(ReadMemberAny(host, "ReductionMode"), CultureInfo.InvariantCulture) ?? string.Empty;
            Check("training-ui-conditional-motion-fixed-njs", ReadBool(host, "ShowOverrideNjs", false) == (njs == "Override"), njs);
            Check("training-ui-conditional-motion-speed-multiplier", ReadBool(host, "ShowSpeedMultiplier", false) == (njs == "Multiplier"), njs);
            Check("training-ui-conditional-density", ReadBool(host, "ShowDensity", false) == (reduction == "Density"), reduction);
            Check("training-ui-conditional-percentage", ReadBool(host, "ShowPercentage", false) == (reduction == "Percentage"), reduction);
            Check("training-ui-conditional-reduction", ReadBool(host, "ShowReduction", false) == (reduction != "Off"), reduction);
            string reaction = Convert.ToString(ReadMemberAny(host, "ReactionTimeMode"), CultureInfo.InvariantCulture) ?? string.Empty;
            Check("training-ui-conditional-reaction", ReadBool(host, "ShowSpecifiedReaction", false) == (reaction == "Specified"), reaction);

            TMP_Text window = labels.FirstOrDefault(value => value != null &&
                (value.text == "Density window" || value.text == "密度窗口"));
            TMP_Text targetReaction = labels.FirstOrDefault(value => value != null &&
                (value.text == "Target reaction time" || value.text == "目标反应时间"));
            Check("training-ui-density-window-only-density", window != null &&
                window.gameObject.activeInHierarchy == (reduction == "Density"),
                "reduction=" + reduction + "; active=" + (window != null && window.gameObject.activeInHierarchy));
            Check("training-ui-target-reaction-only-specified", targetReaction != null &&
                targetReaction.gameObject.activeInHierarchy == (reaction == "Specified"),
                "reaction=" + reaction + "; active=" + (targetReaction != null && targetReaction.gameObject.activeInHierarchy));
        }
    }

    void CheckTrainingUiConditional(Component view, object entry, string hostTypeName, string id, bool expectedActive)
    {
        object declared = ResolveBsmlTag(entry, id);
        Component target = declared as Component;
        if (declared is GameObject gameObject) target = gameObject.transform as Component;
        bool actual = target != null && target.gameObject.activeInHierarchy;
        Check("training-ui-conditional-" + id, target != null && actual == expectedActive,
            "expected active=" + expectedActive + "; actual=" + actual +
            "; found=" + (target != null) + "; source=" +
            "native-tag");
    }

    static Component FindTrainingScrollContainer(Component view)
    {
        return view.GetComponentsInChildren<HMUI.ScrollView>(true).FirstOrDefault(value => value != null && value.gameObject.activeInHierarchy);
    }

    static object ResolveBsmlTag(object entry, string tag)
    {
        object parser = GetMember(entry, "ParserParams");
        if (parser == null) return null;
        MethodInfo method = AccessTools.Method(parser.GetType(), "GetObjectsWithTag", new[] { typeof(string) });
        var objects = method.Invoke(parser, new object[] { tag }) as IEnumerable;
        return objects?.Cast<object>().FirstOrDefault();
    }

    sealed class ModeProbe
    {
        public readonly string Property;
        public readonly string[] Choices;

        public ModeProbe(string property, params string[] choices)
        {
            Property = property;
            Choices = choices;
        }
    }

    static IEnumerable<ModeProbe> GetModeProbes(string hostTypeName)
    {
        if (hostTypeName == "NoteMotionControl.SettingsMenu")
        {
            yield return new ModeProbe("NjsMode", "NjsModes", "njs-modes");
            yield return new ModeProbe("ReductionMode", "ReductionModes", "reduction-modes");
            yield return new ModeProbe("ApplicationPolicy", "ApplicationPolicies", "application-policies");
            yield return new ModeProbe("ReactionTimeMode", "ReactionModes", "reaction-modes");
        }
        else if (hostTypeName == "ActivityEnergyCounter.EnergySettingsHost")
        {
            yield return new ModeProbe("Mode", "Modes", "modes");
            yield return new ModeProbe("Unit", "Units", "units");
        }
        else if (hostTypeName == "ModUiChinese.LocalizationPanel")
            yield return new ModeProbe("SelectedScope", "ScopeChoices", "scopeChoices", "_scopeChoices");
    }

    static object ConvertModeValue(object value, Type targetType)
    {
        if (value == null || targetType.IsInstanceOfType(value)) return value;
        if (targetType.IsEnum)
        {
            try { return Enum.Parse(targetType, Convert.ToString(value, CultureInfo.InvariantCulture), true); }
            catch { return Enum.GetValues(targetType).GetValue(0); }
        }
        return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
    }

    static object ReadMemberAny(object value, params string[] names)
    {
        if (value == null) return null;
        Type type = value.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (string name in names)
        {
            PropertyInfo property = type.GetProperty(name, flags);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                try { return property.GetValue(value, null); } catch { }
            }
            FieldInfo field = type.GetField(name, flags);
            if (field != null)
            {
                try { return field.GetValue(value); } catch { }
            }
        }
        return null;
    }

    static object FindActivityEnergyCounterRegistration()
    {
        Type plugin = FindTrainingType("CountersPlus.Plugin");
        object loaded = GetStaticMember(plugin, "LoadedCustomCounters");
        if (!(loaded is IEnumerable values)) return null;
        foreach (object counter in values)
        {
            string name = Convert.ToString(GetMember(counter, "Name"), CultureInfo.InvariantCulture) ?? string.Empty;
            Type counterType = GetMember(counter, "CounterType") as Type;
            if (string.Equals(name, "Activity Energy", StringComparison.Ordinal) ||
                counterType?.FullName == "ActivityEnergyCounter.ActivityEnergyCounter") return counter;
        }
        return null;
    }

    static object CreateEnergySettingsMenu(object counter)
    {
        try
        {
            object bsml = GetMember(counter, "BSML");
            string resource = Convert.ToString(GetMember(bsml, "Resource"), CultureInfo.InvariantCulture);
            string title = Convert.ToString(GetMember(counter, "Name"), CultureInfo.InvariantCulture);
            Type hostType = GetMember(bsml, "HostType") as Type ??
                FindTrainingType("ActivityEnergyCounter.EnergySettingsHost");
            Assembly assembly = FindTrainingType("ActivityEnergyCounter.EnergySettingsHost")?.Assembly;
            object host = hostType == null ? null : Activator.CreateInstance(hostType, true);
            Type menuType = FindTrainingType("BeatSaberMarkupLanguage.Settings.SettingsMenu");
            if (menuType == null || host == null || assembly == null || string.IsNullOrEmpty(resource)) return null;
            return Activator.CreateInstance(menuType, new object[] { title, resource, host, assembly });
        }
        catch
        {
            return null;
        }
    }

    static Camera FindTrainingMenuCamera()
    {
        Camera value = Resources.FindObjectsOfTypeAll<Camera>()
            .FirstOrDefault(camera => camera != null && camera.gameObject.scene.IsValid() &&
                camera.gameObject.activeInHierarchy && camera.enabled &&
                string.Equals(camera.name, "MenuMainCamera", StringComparison.Ordinal));
        return value ?? Resources.FindObjectsOfTypeAll<Camera>()
            .FirstOrDefault(camera => camera != null && camera.gameObject.scene.IsValid() &&
                camera.gameObject.activeInHierarchy && camera.enabled);
    }

    void CaptureTrainingUiPage(string slug, Camera camera, Component focus = null)
    {
        string path = Path.Combine(directory, "training-ui-" + slug + ".png");
        RenderTexture previousOffscreen = offscreen;
        RenderTexture previousActive = RenderTexture.active;
        RenderTexture previousTarget = camera.targetTexture;
        bool previousEnabled = camera.enabled;
        Rect previousRect = camera.rect;
        float previousAspect = camera.aspect;
        float previousFov = camera.fieldOfView;
        Quaternion previousRotation = camera.transform.rotation;
        RenderTexture target = null;
        Texture2D pixels = null;
        try
        {
            target = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);
            target.Create();
            offscreen = target;
            camera.targetTexture = target;
            camera.enabled = true;
            camera.rect = new Rect(0f, 0f, 1f, 1f);
            camera.aspect = 1920f / 1080f;
            camera.fieldOfView = slug == "gameplay-energy" ? 90f : 60f;
            if (focus != null && focus.transform is RectTransform focusRect && focusRect.rect.width > 1f && focusRect.rect.height > 1f)
            {
                Vector3 center = focusRect.TransformPoint(focusRect.rect.center);
                Vector3 direction = center - camera.transform.position;
                if (direction.sqrMagnitude > 0.01f)
                {
                    camera.transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
                    Vector3[] corners = new Vector3[4];
                    focusRect.GetWorldCorners(corners);
                    float tangent = 0f;
                    foreach (Vector3 corner in corners)
                    {
                        Vector3 local = camera.transform.InverseTransformPoint(corner);
                        if (local.z <= 0.01f) continue;
                        tangent = Mathf.Max(tangent, Mathf.Abs(local.y) / local.z,
                            Mathf.Abs(local.x) / local.z / camera.aspect);
                    }
                    if (tangent > 0f) camera.fieldOfView = Mathf.Clamp(2f * Mathf.Atan(tangent * 1.12f) * Mathf.Rad2Deg, 60f, 110f);
                }
            }
            RenderForCapture(camera);
            RenderTexture.active = target;
            pixels = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
            pixels.ReadPixels(new Rect(0, 0, pixels.width, pixels.height), 0, 0, false);
            pixels.Apply();
            int visible = pixels.GetPixels32().Count(pixel => pixel.r > 20 || pixel.g > 20 || pixel.b > 20);
            Check("training-ui-image-" + slug, visible > pixels.width * pixels.height / 200,
                "visiblePixels=" + visible + "; camera=" + camera.name);
            SaveOpaqueScreenshot(pixels, path);
            CaptureLocalizationSnapshot(path);
        }
        finally
        {
            RenderTexture.active = previousActive;
            camera.targetTexture = previousTarget;
            camera.enabled = previousEnabled;
            camera.rect = previousRect;
            camera.aspect = previousAspect;
            camera.fieldOfView = previousFov;
            camera.transform.rotation = previousRotation;
            offscreen = previousOffscreen;
            if (pixels != null) UnityEngine.Object.Destroy(pixels);
            if (target != null)
            {
                target.Release();
                UnityEngine.Object.Destroy(target);
            }
        }
    }

    void CaptureLocalizationSnapshot(string screenshotPath)
    {
        Type translator = FindTrainingType("ModUiChinese.UiTranslator");
        MethodInfo capture = translator?.GetMethod("Capture", BindingFlags.Public | BindingFlags.Static);
        if (capture == null) return;
        File.WriteAllText(Path.ChangeExtension(screenshotPath, ".json"),
            Newtonsoft.Json.JsonConvert.SerializeObject(capture.Invoke(null, null), Newtonsoft.Json.Formatting.Indented));
    }
}
