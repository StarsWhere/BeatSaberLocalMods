using System;
using CountersPlus.Counters.Custom;
using CountersPlus.Custom;
using TMPro;
using UnityEngine;

namespace ActivityEnergyCounter
{
    public sealed class ActivityEnergyCounter : BasicCustomCounter
    {
        private TMP_Text label;
        private EnergyHudView view;
        public override void CounterInit()
        {
            try
            {
                if (CanvasUtility == null || Settings == null) return;
                label = CanvasUtility.CreateTextFromSettings(Settings);
                label.gameObject.AddComponent<EnergyLocalizationScope>();
                label.richText = true; label.characterSpacing = -1.5f; label.lineSpacing = -3f;
                label.enableWordWrapping = false; label.overflowMode = TextOverflowModes.Overflow;
                view = label.gameObject.AddComponent<EnergyHudView>();
                view.Initialize(label);
            }
            catch (Exception error) { Plugin.Log?.Warn("Activity Energy HUD could not be created: " + error); }
        }
        public override void CounterDestroy()
        {
            if (view != null) view.enabled = false;
            view = null; label = null;
        }
    }

    public sealed class EnergyHudView : MonoBehaviour
    {
        private TMP_Text label;
        private float originalFontSize;
        private double nextUpdate;
        internal void Initialize(TMP_Text text)
        {
            label = text; originalFontSize = text.fontSize; Refresh();
        }
        private void Update()
        {
            if (Time.unscaledTimeAsDouble < nextUpdate) return;
            nextUpdate = Time.unscaledTimeAsDouble + 0.1; Refresh();
        }
        private void Refresh()
        {
            if (label == null) return;
            EnergySettings settings = Plugin.DisplaySettings;
            string content = EnergyDisplay.FormatHud(EnergySnapshotStore.Latest,
                Plugin.History?.SessionTotals, Plugin.History?.TodayTotals, settings);
            if (Plugin.History != null && !Plugin.History.IsAvailable && (settings.HudShowSession || settings.HudShowToday))
                content += (string.IsNullOrEmpty(content) ? "" : "\n") + "Activity energy history is unavailable.";
            label.enabled = !string.IsNullOrEmpty(content);
            label.fontSize = originalFontSize * (float)settings.HudFontScale;
            label.text = content;
        }
    }
    public sealed class EnergyLocalizationScope : MonoBehaviour { }
}

