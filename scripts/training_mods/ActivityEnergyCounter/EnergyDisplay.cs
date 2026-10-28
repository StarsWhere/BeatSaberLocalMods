using System;
using System.Collections.Generic;
using System.Globalization;

namespace ActivityEnergyCounter
{
    /// <summary>
    /// Converts the three energy scopes into the short strings used by the HUD
    /// and the results screen.  The returned strings intentionally keep their
    /// English format words intact so ModUiChinese can match its placeholder
    /// templates while retaining the live numeric values.
    /// </summary>
    public static class EnergyDisplay
    {
        private static readonly EnergyScope[] ScopeOrder =
        {
            EnergyScope.Song,
            EnergyScope.Session,
            EnergyScope.Today
        };

        public static string FormatHud(
            EnergySnapshot song,
            EnergyTotals session,
            EnergyTotals today,
            EnergySettings settings)
        {
            EnergySettings safe = Validated(settings);
            var lines = BuildLines(song, session, today, safe, true);
            return JoinLines(lines);
        }

        public static string FormatResults(
            EnergySnapshot song,
            EnergyTotals session,
            EnergyTotals today,
            EnergySettings settings)
        {
            EnergySettings safe = Validated(settings);
            var lines = BuildLines(song, session, today, safe, false);
            return JoinLines(lines);
        }

        public static string FormatSummary(EnergyTotals totals, EnergySettings settings)
        {
            if (totals == null) return string.Empty;

            EnergySettings safe = Validated(settings);
            string value = FormatValue(totals, safe);
            string line = value;
            if (safe.ShowEstimate) line += " · estimated";
            if (safe.ResultsShowDuration)
                line += " · " + FormatDuration(totals.ValidPlayingSeconds);
            return line;
        }

        public static string FormatDuration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
            long totalSeconds;
            try
            {
                totalSeconds = checked((long)Math.Round(seconds, MidpointRounding.AwayFromZero));
            }
            catch (OverflowException)
            {
                totalSeconds = long.MaxValue;
            }

            long hours = totalSeconds / 3600;
            long minutes = totalSeconds / 60 % 60;
            long remainder = totalSeconds % 60;
            if (hours > 0)
            {
                return hours.ToString(CultureInfo.InvariantCulture) + ":" +
                    minutes.ToString("00", CultureInfo.InvariantCulture) + ":" +
                    remainder.ToString("00", CultureInfo.InvariantCulture);
            }
            return (totalSeconds / 60).ToString("00", CultureInfo.InvariantCulture) + ":" +
                remainder.ToString("00", CultureInfo.InvariantCulture);
        }

        private static List<string> BuildLines(
            EnergySnapshot song,
            EnergyTotals session,
            EnergyTotals today,
            EnergySettings settings,
            bool hud)
        {
            var totals = new Dictionary<EnergyScope, EnergyTotals>
            {
                { EnergyScope.Song, song },
                { EnergyScope.Session, session },
                { EnergyScope.Today, today }
            };
            var lines = new List<string>(3);
            EnergyScope[] order = hud ? HudOrder(settings) : ScopeOrder;
            bool estimatePending = settings.ShowEstimate;
            foreach (EnergyScope scope in order)
            {
                EnergyTotals value = totals[scope];
                if (value == null) continue;
                if (hud ? !settings.IsHudScopeEnabled(scope) : !settings.IsResultsScopeEnabled(scope)) continue;
                lines.Add(FormatLine(scope, value, settings, hud ? settings.HudShowDuration : settings.ResultsShowDuration, estimatePending));
                estimatePending = false;
            }
            return lines;
        }

        private static EnergyScope[] HudOrder(EnergySettings settings)
        {
            var order = new List<EnergyScope>(3) { settings.HudPrimaryScope };
            foreach (EnergyScope scope in ScopeOrder)
                if (!order.Contains(scope)) order.Add(scope);
            return order.ToArray();
        }

        private static string FormatLine(EnergyScope scope, EnergyTotals totals, EnergySettings settings, bool showDuration, bool showEstimate)
        {
            string value = FormatValue(totals, settings);
            string line = settings.ShowLabels ? ScopeLabel(scope, settings.Mode) + ": " + value : value;

            if (showEstimate) line += " · estimated";
            if (showDuration)
                line += " · " + FormatDuration(totals.ValidPlayingSeconds);
            return line;
        }

        private static string FormatValue(EnergyTotals totals, EnergySettings settings)
        {
            double kiloJoules = settings.Mode == EnergyMode.GrossTotal
                ? totals.GrossKiloJoules
                : totals.ActiveKiloJoules;
            double value = settings.Unit == EnergyUnit.Kilocalories
                ? kiloJoules / EnergyAccumulator.KiloJoulesPerKilocalorie
                : kiloJoules;
            if (double.IsNaN(value) || double.IsInfinity(value)) value = 0;
            return value.ToString("F" + settings.DecimalPlaces, CultureInfo.InvariantCulture) + " " + UnitLabel(settings.Unit);
        }

        private static string ScopeLabel(EnergyScope scope, EnergyMode mode)
        {
            string suffix = mode == EnergyMode.GrossTotal ? " total" : string.Empty;
            switch (scope)
            {
                case EnergyScope.Song: return "Song" + suffix;
                case EnergyScope.Today: return "Today" + suffix;
                default: return "Session" + suffix;
            }
        }

        private static string UnitLabel(EnergyUnit unit)
        {
            return unit == EnergyUnit.Kilocalories ? "kcal" : "kJ";
        }

        private static EnergySettings Validated(EnergySettings settings)
        {
            if (settings == null) settings = new EnergySettings();
            settings.Validate();
            return settings;
        }

        private static string JoinLines(List<string> lines)
        {
            if (lines.Count == 0) return string.Empty;
            if (lines.Count == 1) return lines[0];
            return string.Join("\n", lines.ToArray());
        }
    }
}
