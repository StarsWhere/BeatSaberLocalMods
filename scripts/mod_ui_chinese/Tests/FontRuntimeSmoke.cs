using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BeatSaberMarkupLanguage;
using IPA;
using ModUiChinese;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ModUiFontSmoke
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public sealed class Plugin
    {
        GameObject owner;
        [OnStart] public void Start()
        {
            if (String.IsNullOrEmpty(Environment.GetEnvironmentVariable("MOD_UI_FONT_SMOKE_DIR"))) return;
            Application.logMessageReceivedThreaded += Runner.CaptureLog;
            owner = new GameObject("ModUiFontSmoke.Runner");
            UnityEngine.Object.DontDestroyOnLoad(owner);
            owner.AddComponent<Runner>();
        }
        [OnExit] public void Exit()
        {
            Application.logMessageReceivedThreaded -= Runner.CaptureLog;
            if (owner != null) UnityEngine.Object.Destroy(owner);
        }
    }

    public sealed class Runner : MonoBehaviour
    {
        const string Symbols = "|·⬜▼⏬▲⬛☑";
        const string KnownSongText = "ハナヒ\u3099ラ音頭 | 島 憂 ヴ";
        const string SymbolFontName = "ModUiChinese-Symbols";
        static readonly object LogLock = new object();
        static readonly List<string> MissingWarnings = new List<string>();
        static readonly List<string> RuntimeExceptions = new List<string>();
        readonly List<object> checks = new List<object>();
        readonly List<object> samples = new List<object>();
        readonly List<object> coverageSnapshots = new List<object>();
        readonly List<string> errors = new List<string>();
        readonly List<GameObject> fixtures = new List<GameObject>();
        string directory;
        float started, totalSeconds;
        bool finished, languageCaptured, pluginDisabled;
        object originalOptions, localizationPlugin, modifiers;
        FieldInfo optionsField, noFailField;
        bool originalNoFail;
        Type stateType;
        TMP_FontAsset[] originalFallbacks;
        public static void CaptureLog(string message, string stack, LogType type)
        {
            lock (LogLock)
            {
                if (message.Contains("The character with Unicode value") ||
                    (message.Contains("Unicode") && message.Contains("not found") && message.Contains("font")))
                    MissingWarnings.Add(message + "\n" + stack);
                if (type == LogType.Exception && (stack.Contains("ModUiChinese") || stack.Contains("TMPro") ||
                    message.Contains("NullReferenceException"))) RuntimeExceptions.Add(message + "\n" + stack);
            }
        }
        void Start()
        {
            directory = Environment.GetEnvironmentVariable("MOD_UI_FONT_SMOKE_DIR");
            started = Time.realtimeSinceStartup;
            if (!Single.TryParse(Environment.GetEnvironmentVariable("MOD_UI_FONT_SMOKE_SECONDS"), out totalSeconds)) totalSeconds = 250;
            StartCoroutine(Drive());
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
            while (Time.realtimeSinceStartup < deadline && Time.realtimeSinceStartup - started < totalSeconds)
            {
                if (predicate()) { Check(true, label, null); yield break; }
                yield return null;
            }
            throw new TimeoutException(label);
        }
        void Check(bool passed, string label, object detail)
        {
            checks.Add(new { label, passed, detail });
            if (!passed) throw new InvalidOperationException(label + ": " + JsonConvert.SerializeObject(detail));
        }
        static T Active<T>() where T : Component
        { return Resources.FindObjectsOfTypeAll<T>().FirstOrDefault(value => value != null && value.gameObject.activeInHierarchy); }
        static FieldInfo Field(Type type, string name)
        {
            for (; type != null; type = type.BaseType)
            {
                var found = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (found != null) return found;
            }
            throw new MissingFieldException(name);
        }
        static object Value(object target, string name) { return Field(target.GetType(), name).GetValue(target); }
        static object Invoke(object target, string name, params object[] arguments)
        {
            Type type = target as Type ?? target.GetType();
            var method = type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(value => value.Name == name && value.GetParameters().Length == arguments.Length);
            return method.Invoke(target is Type ? null : target, arguments);
        }
        static TMP_FontAsset[] NonSymbols()
        { return TMP_Settings.fallbackFontAssets.Where(value => value == null || value.name != SymbolFontName).ToArray(); }
        TMP_FontAsset SymbolFont()
        {
            var found = TMP_Settings.fallbackFontAssets.Where(value => value != null && value.name == SymbolFontName).ToArray();
            Check(found.Length == 1, "one global symbol fallback registered", found.Select(value => value.GetInstanceID()).ToArray());
            return found[0];
        }
        void ConfigureLanguage(bool enabled)
        {
            var copy = Activator.CreateInstance(originalOptions.GetType(), true);
            foreach (var field in originalOptions.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                field.SetValue(copy, field.GetValue(originalOptions));
            Field(copy.GetType(), "Enabled").SetValue(copy, enabled);
            optionsField.SetValue(null, copy);
            Invoke(typeof(UiTranslator), "Reload");
        }
        TMP_Text Probe(RectTransform canvas, string name, string content, float y)
        {
            var text = BeatSaberUI.CreateCurvedUIText(canvas, content);
            text.rectTransform.anchorMin = text.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            text.rectTransform.anchoredPosition = new Vector2(0, y);
            text.rectTransform.sizeDelta = new Vector2(145, 11);
            text.name = name;
            text.richText = true;
            text.fontSize = 5;
            text.enableWordWrapping = false;
            text.alignment = TextAlignmentOptions.Center;
            text.color = Color.white;
            return text;
        }
        TMP_Text[] CreateFixture(string phase)
        {
            var owner = new GameObject("FontProbe-" + phase, typeof(RectTransform), typeof(Canvas));
            fixtures.Add(owner);
            var canvas = owner.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 1000;
            canvas.additionalShaderChannels = AdditionalCanvasShaderChannels.TexCoord1 | AdditionalCanvasShaderChannels.TexCoord2;
            owner.layer = LayerMask.NameToLayer("UI");
            var rect = (RectTransform)owner.transform;
            rect.sizeDelta = new Vector2(150, 84);
            rect.position = new Vector3(0, 1.6f, 2.5f);
            rect.rotation = Quaternion.identity;
            rect.localScale = Vector3.one * 0.01f;
            return new[] {
                Probe(rect, "Symbols", "012.34 | 056.78 · ⬜ ⬛ ☑ ▼ ▲ ⏬", 32),
                Probe(rect, "Numeric", "12.34 | 56.78", 19),
                Probe(rect, "LiteralEnglish", "Font probe | literal · English", 6),
                Probe(rect, "SongName", "歌名 <color=#80FFFF>Test | Track · 01</color>", -7),
                Probe(rect, "Chinese", "中文字体测试", -20),
                Probe(rect, "SongAuthor", KnownSongText, -33)
            };
        }
        void CaptureFontCoverage(string phase)
        {
            const string observed = "島憂ヴ\u3099";
            var fonts = new List<object>();
            foreach (var asset in Resources.FindObjectsOfTypeAll<TMP_FontAsset>().Where(value => value != null)
                .OrderBy(value => value.name, StringComparer.Ordinal).ThenBy(value => value.GetInstanceID()))
            {
                var coverage = new List<object>();
                foreach (char code in observed)
                {
                    try
                    {
                        coverage.Add(new { character = code.ToString(), code = "U+" + ((int)code).ToString("X4"),
                            direct = (bool?)asset.HasCharacter(code, false, false),
                            recursive = (bool?)asset.HasCharacter(code, true, false), error = (string)null });
                    }
                    catch (Exception error)
                    {
                        coverage.Add(new { character = code.ToString(), code = "U+" + ((int)code).ToString("X4"),
                            direct = (bool?)null, recursive = (bool?)null, error = error.ToString() });
                        errors.Add("Font coverage query failed for " + asset.name + ": " + error.Message);
                    }
                }
                fonts.Add(new { name = asset.name, instanceId = asset.GetInstanceID(), atlasPopulationMode = asset.atlasPopulationMode.ToString(),
                    fallbackFonts = asset.fallbackFontAssetTable == null ? new string[0] : asset.fallbackFontAssetTable.Select(value => value == null ? null : value.name).ToArray(), coverage });
            }
            coverageSnapshots.Add(new { phase, elapsed = Time.realtimeSinceStartup - started, tryAddCharacter = false,
                globalFallbackFonts = TMP_Settings.fallbackFontAssets.Select(value => value == null ? null : value.name).ToArray(), fonts });
            File.WriteAllText(Path.Combine(directory, "font-coverage.json"), JsonConvert.SerializeObject(coverageSnapshots, Formatting.Indented));
        }
        void DestroyFixtures()
        {
            foreach (var fixture in fixtures) if (fixture != null) UnityEngine.Object.Destroy(fixture);
            fixtures.Clear();
        }
        void Glyphs(TMP_Text text, string required, string phase, bool requireChinese)
        {
            text.ForceMeshUpdate(true, true);
            var glyphs = new List<object>();
            foreach (char symbol in required.Distinct())
            {
                var candidates = text.textInfo.characterInfo.Take(text.textInfo.characterCount).Where(value => value.character == symbol).ToArray();
                Check(candidates.Length > 0, phase + " character retained U+" + ((int)symbol).ToString("X4"), text.text);
                foreach (var character in candidates)
                {
                    var glyph = character.textElement == null ? null : character.textElement.glyph;
                    bool mesh = character.isVisible && character.topRight.x > character.bottomLeft.x && character.topRight.y > character.bottomLeft.y;
                    bool atlas = glyph != null && glyph.index != 0 && glyph.glyphRect.width > 0 && glyph.glyphRect.height > 0 &&
                        character.fontAsset != null && character.fontAsset.atlasTextures.Length > glyph.atlasIndex && character.fontAsset.atlasTextures[glyph.atlasIndex] != null;
                    string fontName = character.fontAsset == null ? null : character.fontAsset.name;
                    Check(mesh && atlas, phase + " visible nonempty glyph U+" + ((int)symbol).ToString("X4"), new { text = text.text, fontName, mesh, atlas });
                    if (requireChinese) Check(fontName != null && fontName.StartsWith("SourceHanSansCN-", StringComparison.Ordinal),
                        phase + " uses Chinese Source Han family", fontName);
                    glyphs.Add(new { code = "U+" + ((int)symbol).ToString("X4"), font = fontName,
                        glyphIndex = glyph.index, atlasIndex = glyph.atlasIndex, vertexIndex = character.vertexIndex });
                }
            }
            samples.Add(new { phase, name = text.name, text = text.text, primaryFont = text.font.name, glyphs });
        }
        void CheckFixture(TMP_Text[] texts, string phase, bool chinese)
        {
            Glyphs(texts[0], Symbols, phase + " all control states", false);
            Check(texts[1].text == "12.34 | 56.78", phase + " numeric content preserved", texts[1].text);
            Glyphs(texts[1], "|", phase + " numeric HUD", false);
            Check(texts[2].text == "Font probe | literal · English", phase + " English content preserved", texts[2].text);
            Glyphs(texts[2], "|·", phase + " English label", false);
            Check(texts[3].text == "歌名 <color=#80FFFF>Test | Track · 01</color>", phase + " protected song data preserved", texts[3].text);
            Glyphs(texts[3], "|·", phase + " protected song symbols", false);
            Check(texts[5].text == KnownSongText, phase + " Japanese and combining song data preserved", texts[5].text);
            Glyphs(texts[5], new string(KnownSongText.Where(value => !Char.IsWhiteSpace(value)).ToArray()), phase + " Japanese and combining glyphs", false);
            if (chinese) { Glyphs(texts[3], "歌名", phase + " protected Chinese", true); Glyphs(texts[4], "中文字体测试", phase + " Chinese", true); }
        }
        IEnumerator Run()
        {
            yield return Wait(() => Active<MainMenuViewController>() != null && Active<MainMenuViewController>().isActivated &&
                SongCore.Loader.AreSongsLoaded && !SongCore.Loader.AreSongsLoading, 75, "main menu and SongCore ready");
            var font = SymbolFont();
            foreach (char code in Symbols)
            {
                TMP_Character character;
                Check(font.characterLookupTable.TryGetValue(code, out character) && character.glyph.index != 0 &&
                    character.glyph.glyphRect.width > 0 && character.glyph.glyphRect.height > 0,
                    "static symbol atlas contains U+" + ((int)code).ToString("X4"), font.name);
            }
            Check(font.atlasPopulationMode == AtlasPopulationMode.Static, "symbol atlas cannot replace other characters dynamically", font.atlasPopulationMode.ToString());
            originalFallbacks = NonSymbols();
            stateType = typeof(UiTranslator).Assembly.GetType("ModUiChinese.LocalizationState", true);
            optionsField = Field(stateType, "ActiveOptions");
            originalOptions = optionsField.GetValue(null);
            languageCaptured = true;
            ConfigureLanguage(true);
            CaptureFontCoverage("before-first-fixture");
            var probes = CreateFixture("Chinese-enabled");
            CaptureFontCoverage("fixture-created-before-first-mesh");
            // No yield here: the first mesh must work before the translator's periodic refresh can run.
            CheckFixture(probes, "first synchronous mesh", true);
            yield return new WaitForSecondsRealtime(0.8f);
            CheckFixture(probes, "Chinese enabled", true);
            Capture("font-probe.png");
            DestroyFixtures();
            yield return null;
            ConfigureLanguage(false);
            probes = CreateFixture("language-off");
            yield return new WaitForSecondsRealtime(0.2f);
            CheckFixture(probes, "language disabled and new labels", false);
            SymbolFont();
            Invoke(typeof(UiTranslator), "Reload");
            var reloaded = CreateFixture("language-off-reloaded");
            yield return new WaitForSecondsRealtime(0.2f);
            CheckFixture(reloaded, "language reload and new labels", false);
            DestroyFixtures();
            yield return null;
            // Exercise the real plugin methods without asking BSIPA to persist an enable/disable preference.
            var executors = (IEnumerable)Field(typeof(IPA.Loader.PluginManager), "_bsPlugins").GetValue(null);
            foreach (var executor in executors)
            {
                var metadata = executor.GetType().GetProperty("Metadata").GetValue(executor, null);
                if ((string)metadata.GetType().GetProperty("Id").GetValue(metadata, null) == "ModUiChinese")
                    localizationPlugin = executor.GetType().GetProperty("Instance").GetValue(executor, null);
            }
            Check(localizationPlugin != null, "actual localization plugin instance found", null);
            // No render frame occurs while the fallback is absent; synthetic labels have already been destroyed.
            Invoke(localizationPlugin, "Disable");
            pluginDisabled = true;
            Check(TMP_Settings.fallbackFontAssets.SequenceEqual(originalFallbacks), "disable restores the original global list and order", TMP_Settings.fallbackFontAssets.Count);
            Invoke(localizationPlugin, "Enable");
            pluginDisabled = false;
            var replacement = SymbolFont();
            Check(replacement != font && NonSymbols().SequenceEqual(originalFallbacks), "reenable replaces only the owned fallback", replacement.GetInstanceID());
            Invoke(typeof(UiTranslator).Assembly.GetType("ModUiChinese.SymbolFontFallback", true), "Enable");
            SymbolFont();
            ConfigureLanguage(true);
            probes = CreateFixture("reenabled");
            yield return new WaitForSecondsRealtime(0.8f);
            CheckFixture(probes, "plugin reenabled", true);
            DestroyFixtures();
            yield return null;
            optionsField.SetValue(null, originalOptions);
            Invoke(typeof(UiTranslator), "Reload");
            languageCaptured = false;
            Check(NonSymbols().SequenceEqual(originalFallbacks), "language changes preserve unrelated global fallbacks", null);
            if (Environment.GetEnvironmentVariable("MOD_UI_FONT_SMOKE_MENU_ONLY") != "1") yield return Play();
            lock (LogLock)
            {
                Check(MissingWarnings.Count == 0, "zero missing-glyph warnings for the entire run", MissingWarnings.ToArray());
                Check(RuntimeExceptions.Count == 0, "no font or UI runtime exceptions", RuntimeExceptions.ToArray());
            }
        }
        IEnumerator Play()
        {
            string requested = Environment.GetEnvironmentVariable("MOD_UI_FONT_SMOKE_LEVEL");
            var preferred = SongCore.Loader.CustomLevels.FirstOrDefault(pair => Path.GetFileName(pair.Key.TrimEnd('\\', '/')) == "BeatSage_Aiobahn_somunia_wish");
            var level = String.IsNullOrEmpty(requested) ? preferred.Value : SongCore.Loader.GetLevelById(requested);
            Check(level != null, "requested native font test song exists", requested ?? "BeatSage_Aiobahn_somunia_wish");
            var keys = level.GetBeatmapKeys().OrderBy(key => SongCore.Collections.GetCurrentCharacteristicSerializedName(key.characteristic) == "Standard" ? 0 : 1)
                .ThenBy(key => key.difficulty).ToArray();
            Check(keys.Length > 0, "native test song has a difficulty", level.levelID);
            var solo = Resources.FindObjectsOfTypeAll<SoloFreePlayFlowCoordinator>().First();
            var constructor = typeof(LevelSelectionFlowCoordinator.State).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(value => value.GetParameters().Length == 4);
            var state = constructor.Invoke(new object[] { SelectLevelCategoryViewController.LevelCategory.All, SongCore.Loader.CustomLevelsPack, keys[0], level });
            Invoke(solo, "Setup", state);
            ((Button)Value(Active<MainMenuViewController>(), "_soloButton")).onClick.Invoke();
            yield return Wait(() => Active<LevelCollectionNavigationController>() != null && Active<LevelCollectionNavigationController>().isActivated,
                30, "native solo song selection active");
            StandardLevelDetailView detail = null;
            yield return Wait(() => (detail = Active<StandardLevelDetailView>()) != null && detail.beatmapKey.levelId == level.levelID && detail.actionButton.interactable,
                30, "native play entry enabled");
            yield return Wait(() => solo.isActivated && !solo.isInTransition, 15, "native solo transition finished");
            yield return new WaitForSecondsRealtime(0.5f);
            Check(((Button)Value(detail, "_practiceButton")).interactable, "native practice entry enabled", level.levelID);
            var panel = Active<GameplayModifiersPanelController>();
            Check(panel != null, "native gameplay modifiers available", null);
            modifiers = Value(panel, "_gameplayModifiers");
            noFailField = Field(modifiers.GetType(), "_noFailOn0Energy");
            originalNoFail = (bool)noFailField.GetValue(modifiers);
            noFailField.SetValue(modifiers, true);
            detail.actionButton.onClick.Invoke();
            yield return Wait(() => {
                var settingsType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("Heck.SettingsSetter.SettingsSetterViewController")).FirstOrDefault(type => type != null);
                var settings = settingsType == null ? null : Resources.FindObjectsOfTypeAll(settingsType).OfType<HMUI.ViewController>().FirstOrDefault(view => view.isActivated && view.gameObject.activeInHierarchy);
                if (settings != null) Invoke(settings, "OnAcceptClick");
                return Active<AudioTimeSyncController>() != null && Active<AudioTimeSyncController>().songTime > 0.5f;
            },
                40, "real native gameplay and audio started");
            var audio = Active<AudioTimeSyncController>();
            float firstTime = audio.songTime;
            yield return Wait(() => audio != null && audio.songTime - firstTime >= 8.5f, 25, "native audio advances for more than eight seconds");
            var texts = Resources.FindObjectsOfTypeAll<TMP_Text>().Where(value => value != null && value.gameObject.activeInHierarchy).ToArray();
            var speeds = texts.Where(value => Regex.IsMatch(Regex.Replace(value.text ?? "", "<[^>]+>", ""), @"\d[\d.]*\s*\|\s*\d")).ToArray();
            Check(speeds.Length > 0, "live Counters+ split speed HUD contains its numeric separator", texts.Where(value => value.name == "CustomUIText").Select(value => value.text).ToArray());
            foreach (var speed in speeds) Glyphs(speed, "|", "live native speed HUD", false);
            var energy = texts.Where(value => (value.text ?? "").Contains("·") && ((value.text ?? "").Contains("kcal") || (value.text ?? "").Contains("kJ"))).ToArray();
            Check(energy.Length > 0, "live Activity Energy HUD contains its descriptor separator", texts.Where(value => value.name == "CustomUIText").Select(value => value.text).ToArray());
            foreach (var label in energy) Glyphs(label, "·", "live native activity energy HUD", false);
            Capture("gameplay-fonts.png");
            if (Environment.GetEnvironmentVariable("MOD_UI_FONT_SMOKE_RETURN_TO_MENU") == "1")
            {
                var pauseController = Active<PauseController>();
                Check(pauseController != null, "native pause controller available for scene-return check", null);
                pauseController.Pause();
                yield return new WaitForSecondsRealtime(1f);
                PauseMenuManager pause = null;
                yield return Wait(() => (pause = Active<PauseMenuManager>()) != null &&
                    ((Button)Value(pause, "_backButton")).interactable, 10, "native pause menu exit entry enabled");
                ((Button)Value(pause, "_backButton")).onClick.Invoke();
                yield return Wait(() => !SceneManager.GetSceneByName("GameCore").isLoaded &&
                    (Active<StandardLevelDetailView>() != null || Active<PracticeViewController>() != null),
                    35, "native scene disposal and return to song menu");
                yield return null;
                yield return null;
                yield return new WaitForSecondsRealtime(2f);
                Check(true, "two menu frames completed before application quit", SceneManager.GetActiveScene().name);
            }
        }
        void Capture(string name)
        {
            var main = Active<MainCamera>();
            if (main == null) throw new InvalidOperationException("MainCamera missing for font screenshot");
            var camera = main.camera;
            var oldPosition = camera.transform.localPosition;
            var oldRotation = camera.transform.localRotation;
            var oldTarget = camera.targetTexture;
            var oldActive = RenderTexture.active;
            var oldRect = camera.rect;
            float oldAspect = camera.aspect, oldFov = camera.fieldOfView;
            bool oldEnabled = camera.enabled;
            var data = camera.GetComponent<UniversalAdditionalCameraData>();
            bool xr = data != null && data.allowXRRendering;
            var avatarManager = Resources.FindObjectsOfTypeAll<MonoBehaviour>().FirstOrDefault(value => value != null && value.GetType().FullName == "CustomAvatar.Player.PlayerAvatarManager");
            var avatar = avatarManager == null ? null : avatarManager.GetType().GetProperty("currentlySpawnedAvatar").GetValue(avatarManager, null) as Component;
            var renderers = avatar == null ? new Renderer[0] : avatar.GetComponentsInChildren<Renderer>(true).Where(value => value.enabled).ToArray();
            var target = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32);
            var texture = new Texture2D(1600, 900, TextureFormat.RGBA32, false);
            try
            {
                foreach (var renderer in renderers) renderer.enabled = false;
                target.Create();
                camera.transform.localPosition = new Vector3(0, 1.6f, 0);
                camera.transform.localRotation = Quaternion.identity;
                camera.targetTexture = target;
                camera.enabled = true;
                camera.rect = new Rect(0, 0, 1, 1);
                camera.aspect = 1600f / 900;
                camera.fieldOfView = 60;
                if (data != null) data.allowXRRendering = false;
                var request = new RenderPipeline.StandardRequest { destination = target, face = CubemapFace.Unknown };
                RenderPipeline.SubmitRenderRequest(camera, request);
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0, false);
                texture.Apply();
                var pixels = texture.GetPixels32();
                for (int index = 0; index < pixels.Length; index++) pixels[index].a = 255;
                texture.SetPixels32(pixels); texture.Apply();
                File.WriteAllBytes(Path.Combine(directory, name), texture.EncodeToPNG());
            }
            finally
            {
                camera.transform.localPosition = oldPosition; camera.transform.localRotation = oldRotation;
                camera.targetTexture = oldTarget; camera.enabled = oldEnabled; camera.rect = oldRect;
                camera.aspect = oldAspect; camera.fieldOfView = oldFov; RenderTexture.active = oldActive;
                if (data != null) data.allowXRRendering = xr;
                foreach (var renderer in renderers) if (renderer != null) renderer.enabled = true;
                target.Release(); UnityEngine.Object.Destroy(target); UnityEngine.Object.Destroy(texture);
            }
        }
        void Restore()
        {
            DestroyFixtures();
            if (noFailField != null && modifiers != null) { noFailField.SetValue(modifiers, originalNoFail); noFailField = null; }
            if (languageCaptured && optionsField != null)
            {
                optionsField.SetValue(null, originalOptions);
                if (!pluginDisabled) Invoke(typeof(UiTranslator), "Reload");
                languageCaptured = false;
            }
            if (pluginDisabled && localizationPlugin != null) { Invoke(localizationPlugin, "Enable"); pluginDisabled = false; }
        }
        void Finish()
        {
            if (finished) return;
            finished = true;
            try { Restore(); } catch (Exception error) { errors.Add("Restore: " + error); }
            string[] warnings, exceptions;
            lock (LogLock) { warnings = MissingWarnings.ToArray(); exceptions = RuntimeExceptions.ToArray(); }
            if (warnings.Length != 0) errors.Add(warnings.Length + " missing-glyph warnings");
            if (exceptions.Length != 0) errors.Add(exceptions.Length + " runtime exceptions");
            Application.logMessageReceivedThreaded -= CaptureLog;
            File.WriteAllText(Path.Combine(directory, "pre-exit-report.json"), JsonConvert.SerializeObject(new {
                passed = errors.Count == 0, menuOnly = Environment.GetEnvironmentVariable("MOD_UI_FONT_SMOKE_MENU_ONLY") == "1",
                returnToMenu = Environment.GetEnvironmentVariable("MOD_UI_FONT_SMOKE_RETURN_TO_MENU") == "1",
                elapsed = Time.realtimeSinceStartup - started, checks, samples, missingGlyphWarnings = warnings,
                runtimeExceptions = exceptions, errors
            }, Formatting.Indented));
            Application.Quit(errors.Count == 0 ? 0 : 1);
        }
        void OnDestroy() { if (!finished) { try { Restore(); } catch (Exception error) { Debug.LogError("Font probe restoration failed: " + error); } } }
    }
}
