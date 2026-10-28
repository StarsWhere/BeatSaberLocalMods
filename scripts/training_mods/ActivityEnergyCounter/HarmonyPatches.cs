using HarmonyLib;
using LocalTraining;
using System.Runtime.CompilerServices;
using TMPro;
using UnityEngine;

namespace ActivityEnergyCounter
{
    [HarmonyPatch(typeof(GameplayCoreInstaller), "InstallBindings")]
    internal static class NewSessionPatch
    {
        // Invalidate old results for every game mode, including modes that do
        // not install an energy recorder (such as multiplayer).
        private static void Prefix() { EnergySnapshotStore.Begin(); }
    }

    [HarmonyPatch(typeof(ResultsViewController), "Init")]
    internal static class ResultsInitPatch
    {
        private static void Postfix(ResultsViewController __instance, ref BeatmapKey beatmapKey)
        {
            EnergyResultsOverlay.Clear(__instance);
            if (!beatmapKey.IsValid()) return;
            EnergySnapshot snapshot;
            if (EnergySnapshotStore.TryGet(ChartKey.For(beatmapKey), out snapshot))
            {
                EnergyResultsOverlay.Remember(__instance, snapshot);
                EnergyResultsOverlay.Apply(__instance);
            }
        }
    }
    [HarmonyPatch(typeof(ResultsViewController), "SetDataToUI")]
    internal static class ResultsDataPatch
    {
        private static void Postfix(ResultsViewController __instance) { EnergyResultsOverlay.Apply(__instance); }
    }

    internal static class EnergyResultsOverlay
    {
        private const string ObjectName = "ActivityEnergyCounter.ResultEnergy";
        private sealed class Result
        {
            internal EnergySnapshot Snapshot;
        }
        private static readonly ConditionalWeakTable<ResultsViewController, Result> Results = new ConditionalWeakTable<ResultsViewController, Result>();
        internal static void Clear(ResultsViewController view)
        {
            if (view == null) return;
            Results.Remove(view);
            Transform existing = view.transform.Find(ObjectName);
            if (existing != null) existing.gameObject.SetActive(false);
        }
        public static void Remember(ResultsViewController view, EnergySnapshot snapshot)
        {
            if (view == null || snapshot == null) return;
            Results.Remove(view); Results.Add(view, new Result { Snapshot = snapshot });
        }
        public static void Apply(ResultsViewController view)
        {
            if (view == null) return;
            Result result;
            if (!Results.TryGetValue(view, out result)) return;
            EnergySettings settings = Plugin.DisplaySettings;
            string content = EnergyDisplay.FormatResults(result.Snapshot, Plugin.History?.SessionTotals,
                Plugin.History?.TodayTotals, settings);
            if (Plugin.History != null && !Plugin.History.IsAvailable
                && (settings.ResultsShowSession || settings.ResultsShowToday))
                content += (string.IsNullOrEmpty(content) ? "" : "\n") + "Activity energy history is unavailable.";
            content = content.Replace("\n", "   |   ");
            Transform existing = view.transform.Find(ObjectName);
            if (string.IsNullOrEmpty(content))
            {
                if (existing != null) existing.gameObject.SetActive(false);
                return;
            }
            TMP_Text text = existing == null ? null : existing.GetComponent<TMP_Text>();
            if (text == null)
            {
                RectTransform parent = view.transform as RectTransform;
                if (parent == null) return;
                GameObject host = new GameObject(ObjectName, typeof(RectTransform));
                host.transform.SetParent(parent, false);
                RectTransform rect = (RectTransform)host.transform;
                rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1);
                rect.pivot = new Vector2(0.5f, 0.5f);
                text = host.AddComponent<TextMeshProUGUI>();
                text.raycastTarget = false;
                host.AddComponent<EnergyLocalizationScope>();
                TMP_Text source = null;
                foreach (TMP_Text candidate in view.GetComponentsInChildren<TMP_Text>(true))
                    if (candidate != text) { source = candidate; break; }
                if (source != null) { text.font = source.font; text.color = source.color; }
                else text.color = Color.white;
                text.alignment = TextAlignmentOptions.Center;
                text.enableAutoSizing = true;
                text.richText = true; text.characterSpacing = -1; text.lineSpacing = -2;
                text.enableWordWrapping = false; text.overflowMode = TextOverflowModes.Overflow;
            }
            // Keep the row above the song information, including at the largest
            // configured font size, without moving the native result layout.
            RectTransform viewRect = view.transform as RectTransform;
            RectTransform levelBar = view.transform.Find("Container/LevelBarSimple") as RectTransform;
            RectTransform textRect = text.rectTransform;
            float fontSize = 4.5f * (float)settings.ResultsFontScale;
            text.fontSizeMax = fontSize;
            text.fontSizeMin = Mathf.Min(4, fontSize);
            text.fontSize = fontSize;
            textRect.sizeDelta = new Vector2(144, fontSize * 1.8f);
            float rowCenter = 12;
            if (viewRect != null && levelBar != null)
            {
                float songTop = viewRect.InverseTransformPoint(levelBar.TransformPoint(
                    new Vector3(0, levelBar.rect.yMax, 0))).y;
                rowCenter = songTop - viewRect.rect.yMax + 3 + textRect.sizeDelta.y * 0.5f;
            }
            textRect.anchoredPosition = new Vector2(0, rowCenter);
            text.text = content; text.gameObject.SetActive(true);
        }
    }
}

