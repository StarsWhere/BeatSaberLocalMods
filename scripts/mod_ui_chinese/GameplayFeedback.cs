using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace ModUiChinese
{
    // The native MISS feedback is a sprite. Replace only its image; the game
    // retains ownership of movement, material, fade, lifetime and pooling.
    public static class GameplayFeedback
    {
        public sealed class SwapState
        {
            public object Spawner;
            public FieldInfo Field;
            public Sprite Original;
        }
        sealed class Image
        {
            public Sprite Original, Localized;
            public Texture2D Texture;
            public string Text;
        }
        sealed class Letter
        {
            public TMP_FontAsset Font;
            public TMP_Character Character;
        }
        static readonly List<Image> images = new List<Image>();
        static readonly HashSet<string> reported = new HashSet<string>(StringComparer.Ordinal);
        const string Scope = "GameFeedback";
        static string Caption()
        {
            return !LocalizationState.Enabled || UiTranslator.Engine == null ? "Miss" : UiTranslator.Engine.Translate("Miss", Scope);
        }
        public static void MissPrefix(object __instance, out SwapState __state)
        {
            __state = null;
            string caption = Caption();
            if (caption == "Miss") return;
            try
            {
                var spawner = AccessTools.Field(__instance.GetType(), "_missedNoteFlyingSpriteSpawner").GetValue(__instance);
                var field = AccessTools.Field(spawner.GetType(), "_sprite");
                var original = field.GetValue(spawner) as Sprite;
                if (original == null) return;
                var localized = Localize(original, caption);
                __state = new SwapState { Spawner = spawner, Field = field, Original = original };
                field.SetValue(spawner, localized);
            }
            catch (Exception error)
            {
                if (reported.Add(caption + ":" + error.Message)) UiTranslator.Log.Warn("Gameplay feedback translation failed: " + error);
            }
        }
        public static Exception MissFinalizer(Exception __exception, SwapState __state)
        {
            if (__state != null) __state.Field.SetValue(__state.Spawner, __state.Original);
            return __exception;
        }
        public static void Warmup(object __instance)
        {
            SwapState state;
            MissPrefix(__instance, out state);
            MissFinalizer(null, state);
        }
        static Letter FindLetter(TMP_FontAsset font, uint code, HashSet<int> visited)
        {
            if (font == null || !visited.Add(font.GetInstanceID())) return null;
            TMP_Character character;
            if (font.characterLookupTable.TryGetValue(code, out character)) return new Letter { Font = font, Character = character };
            if (font.fallbackFontAssetTable != null)
                foreach (var fallback in font.fallbackFontAssetTable)
                {
                    var result = FindLetter(fallback, code, visited);
                    if (result != null) return result;
                }
            return null;
        }
        static Sprite Localize(Sprite original, string caption)
        {
            var existing = images.FirstOrDefault(x => x.Original == original && x.Text == caption);
            if (existing != null) return existing.Localized;
            var font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(x => x.name == "SourceHanSansCN-Bold-SDF-Common-1(2k)");
            if (font == null) throw new InvalidOperationException("Chinese feedback font is unavailable");
            if (caption.Length > 64 || caption.Any(Char.IsControl)) throw new InvalidOperationException("Feedback caption must be a single line of at most 64 characters");
            var letters = new List<Letter>();
            for (int i = 0; i < caption.Length; i++)
            {
                uint code = (uint)Char.ConvertToUtf32(caption, i);
                if (Char.IsHighSurrogate(caption[i])) i++;
                var letter = FindLetter(font, code, new HashSet<int>());
                if (letter == null) throw new InvalidOperationException("Feedback glyph is unavailable: U+" + code.ToString("X"));
                letters.Add(letter);
            }
            int width = Mathf.RoundToInt(original.rect.width), height = Mathf.RoundToInt(original.rect.height);
            float advance = letters.Sum(x => x.Character.glyph.metrics.horizontalAdvance);
            float top = letters.Max(x => x.Character.glyph.metrics.horizontalBearingY);
            float bottom = letters.Min(x => x.Character.glyph.metrics.horizontalBearingY - x.Character.glyph.metrics.height);
            float scale = Mathf.Min(width * 0.94f / Mathf.Max(1f, advance), height * 0.9f / Mathf.Max(1f, top - bottom));
            float pen = (width - advance * scale) * 0.5f;
            float baseline = (height - (top - bottom) * scale) * 0.5f - bottom * scale;
            var pixels = new Color32[width * height];
            var atlases = new Dictionary<Texture2D, Texture2D>();
            Texture2D texture = null;
            Sprite sprite = null;
            try
            {
                foreach (var letter in letters)
                {
                    var glyph = letter.Character.glyph;
                    var metrics = glyph.metrics;
                    var rect = glyph.glyphRect;
                    if (rect.width > 0 && rect.height > 0)
                    {
                        var atlas = letter.Font.atlasTextures[glyph.atlasIndex];
                        Texture2D readable;
                        if (!atlases.TryGetValue(atlas, out readable))
                        {
                            readable = ReadAtlas(atlas);
                            atlases.Add(atlas, readable);
                        }
                        float left = pen + metrics.horizontalBearingX * scale;
                        float low = baseline + (metrics.horizontalBearingY - metrics.height) * scale;
                        float w = metrics.width * scale, h = metrics.height * scale;
                        for (int y = Mathf.Max(0, Mathf.FloorToInt(low)); y < Mathf.Min(height, Mathf.CeilToInt(low + h)); y++)
                            for (int x = Mathf.Max(0, Mathf.FloorToInt(left)); x < Mathf.Min(width, Mathf.CeilToInt(left + w)); x++)
                            {
                                float u = (rect.x + (x + 0.5f - left) / w * rect.width) / atlas.width;
                                float v = (rect.y + (y + 0.5f - low) / h * rect.height) / atlas.height;
                                float alpha = Mathf.Clamp01((readable.GetPixelBilinear(u, v).a - 0.5f) * Mathf.Max(2f, scale * 32f) + 0.5f);
                                byte value = (byte)Mathf.RoundToInt(alpha * 255f);
                                int index = y * width + x;
                                if (value > pixels[index].a) pixels[index] = new Color32(value, value, value, value);
                            }
                    }
                    pen += metrics.horizontalAdvance * scale;
                }
                if (!pixels.Any(x => x.a > 128)) throw new InvalidOperationException("Feedback glyph rasterization produced an empty image");
                texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                texture.name = "ModUiChinese.Feedback." + caption;
                texture.SetPixels32(pixels);
                texture.Apply(false, true);
                sprite = Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(original.pivot.x / original.rect.width, original.pivot.y / original.rect.height), original.pixelsPerUnit, 1, SpriteMeshType.FullRect);
                sprite.name = "ModUiChinese.Feedback." + caption;
                images.Add(new Image { Original = original, Localized = sprite, Texture = texture, Text = caption });
                return sprite;
            }
            catch
            {
                if (sprite != null) UnityEngine.Object.Destroy(sprite);
                if (texture != null) UnityEngine.Object.Destroy(texture);
                throw;
            }
            finally { foreach (var readable in atlases.Values) UnityEngine.Object.Destroy(readable); }
        }
        static Texture2D ReadAtlas(Texture2D atlas)
        {
            var previous = RenderTexture.active;
            var target = RenderTexture.GetTemporary(atlas.width, atlas.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Texture2D copy = null;
            try
            {
                Graphics.Blit(atlas, target);
                RenderTexture.active = target;
                copy = new Texture2D(atlas.width, atlas.height, TextureFormat.RGBA32, false, true);
                copy.ReadPixels(new Rect(0, 0, atlas.width, atlas.height), 0, 0, false);
                copy.Apply();
                return copy;
            }
            catch { if (copy != null) UnityEngine.Object.Destroy(copy); throw; }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); }
        }
        public static object[] Capture()
        {
            return Resources.FindObjectsOfTypeAll<SpriteRenderer>().Where(x => x.gameObject.scene.IsValid() && x.gameObject.activeInHierarchy && x.enabled).Select(x =>
            {
                var image = images.FirstOrDefault(value => value.Localized == x.sprite || value.Original == x.sprite);
                bool nativeMiss = x.sprite != null && x.sprite.name == "MissedNote";
                return image == null && !nativeMiss ? null : (object)new { path = "GameplayFeedback/" + x.name, text = image != null && x.sprite == image.Localized ? image.Text : "Miss", source = "Miss", scope = Scope,
                    localizationEnabled = LocalizationState.Enabled, scopeEnabled = UiTranslator.Engine != null && UiTranslator.Engine.IsScopeEnabled(Scope), protectedData = false, font = "native-sprite" };
            }).Where(x => x != null).ToArray();
        }
        internal static void Refresh()
        {
            string caption = Caption();
            if (caption == "Miss") return;
            var originals = images.Where(x => x.Original != null).Select(x => x.Original).Distinct().ToArray();
            foreach (var obsolete in images.Where(x => x.Original == null || x.Text != caption).ToArray())
            {
                // Restore runs before reload: renderers no longer reference these
                // images. Bound the cache to the current translation per source.
                UnityEngine.Object.Destroy(obsolete.Localized, 1f);
                UnityEngine.Object.Destroy(obsolete.Texture, 1f);
                images.Remove(obsolete);
            }
            foreach (var renderer in Resources.FindObjectsOfTypeAll<SpriteRenderer>())
            {
                if (!renderer.gameObject.scene.IsValid() || !renderer.gameObject.activeInHierarchy) continue;
                var image = images.FirstOrDefault(x => x.Original == renderer.sprite || x.Localized == renderer.sprite);
                var original = image == null ? originals.FirstOrDefault(x => x == renderer.sprite) : image.Original;
                if (original == null) continue;
                try { renderer.sprite = Localize(original, caption); }
                catch (Exception error)
                {
                    if (reported.Add(caption + ":" + error.Message)) UiTranslator.Log.Warn("Gameplay feedback translation failed: " + error);
                }
            }
        }
        internal static void Restore(bool dispose)
        {
            foreach (var renderer in Resources.FindObjectsOfTypeAll<SpriteRenderer>())
            {
                var image = images.FirstOrDefault(x => x.Localized == renderer.sprite);
                if (image != null) renderer.sprite = image.Original;
            }
            if (!dispose) return;
            foreach (var image in images) { UnityEngine.Object.Destroy(image.Localized, 1f); UnityEngine.Object.Destroy(image.Texture, 1f); }
            images.Clear(); reported.Clear();
        }
    }
}
