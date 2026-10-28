using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace ModUiChinese
{
    // TMP's global fallback also reaches numeric HUDs, protected song data and
    // texts created after a scene change, independently of translation settings.
    internal static class SymbolFontFallback
    {
        const string RequiredSymbols = "\u007c\u00b7\u2b1c\u25bc\u23ec\u25b2\u2b1b\u2611";
        static TMP_FontAsset font;
        static List<TMP_FontAsset> registered;

        public static void Enable()
        {
            if (font != null) return;
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "seguisym.ttf");
                if (!File.Exists(path)) throw new FileNotFoundException("The Windows symbol font is unavailable", path);
                font = TMP_FontAsset.CreateFontAsset(path, 0, 90, 9, GlyphRenderMode.SDFAA, 512, 512);
                if (font == null) throw new InvalidOperationException("TMP could not load the Windows symbol font");
                font.name = "ModUiChinese-Symbols";
                string missing;
                if (!font.TryAddCharacters(RequiredSymbols, out missing, false) || !String.IsNullOrEmpty(missing))
                    throw new InvalidOperationException("The symbol font is missing required characters: " + missing);
                // Keep the existing Latin and Chinese faces in charge of all
                // other text. This atlas supplies only the observed missing symbols.
                font.atlasPopulationMode = AtlasPopulationMode.Static;
                font.hideFlags = HideFlags.HideAndDontSave;
                font.material.hideFlags = HideFlags.HideAndDontSave;
                foreach (var atlas in font.atlasTextures) atlas.hideFlags = HideFlags.HideAndDontSave;
                registered = TMP_Settings.fallbackFontAssets;
                if (registered == null) throw new InvalidOperationException("TMP has no global fallback font list");
                registered.Add(font);
                Invalidate();
                UiTranslator.Log.Info("Symbol font fallback registered for | · ⬜ ▼ ⏬ ▲ ⬛ ☑");
            }
            catch (Exception error)
            {
                Disable();
                UiTranslator.Log.Error("Symbol font fallback initialization failed: " + error);
            }
        }

        static void Invalidate()
        {
            AccessTools.Method(typeof(TMP_FontAsset).Assembly.GetType("TMPro.TMP_ResourceManager"), "ClearFontAssetGlyphCache").Invoke(null, null);
            foreach (var text in Resources.FindObjectsOfTypeAll<TMP_Text>())
                if (text != null) text.havePropertiesChanged = true;
        }

        public static void Disable()
        {
            if (registered != null && font != null) registered.Remove(font);
            registered = null;
            if (font == null) return;
            Invalidate();
            // TMP_FontAsset.OnDestroy owns its generated atlas and material.
            UnityEngine.Object.Destroy(font);
            font = null;
        }
    }
}
