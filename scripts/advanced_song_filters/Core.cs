using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AdvancedSongFilters
{
    public enum Metric
    {
        Nps,
        Njs,
        Duration,
        Bpm,
        Notes
    }

    public enum MatchAssessment
    {
        NoMatch,
        Match,
        Unknown
    }

    public enum FilterPreviewState
    {
        Ready,
        Loading,
        Partial,
        Error
    }

    public sealed class FilterPreview
    {
        public FilterPreviewState State;
        public int Matched;
        public int Total;
        public int Unknown;
        public string ErrorCategory;

        public bool CanApply
        {
            get { return State == FilterPreviewState.Ready || State == FilterPreviewState.Partial; }
        }

        public bool ShouldSerializeCanApply()
        {
            return false;
        }
    }

    public sealed class NumericRange
    {
        public double? Min;
        public double? Max;

        public bool IsSet
        {
            get { return Min.HasValue || Max.HasValue; }
        }

        // Json.NET includes readable properties by default. Keep this derived
        // value out of persisted settings while leaving the model dependency-free.
        public bool ShouldSerializeIsSet()
        {
            return false;
        }

        public bool Contains(double? value)
        {
            if (!IsSet)
            {
                return true;
            }

            if (!value.HasValue || !IsFinite(value.Value))
            {
                return false;
            }

            if (Min.HasValue && value.Value < Min.Value)
            {
                return false;
            }

            if (Max.HasValue && value.Value > Max.Value)
            {
                return false;
            }

            return true;
        }

        public NumericRange Clone()
        {
            return new NumericRange
            {
                Min = Min,
                Max = Max
            };
        }

        public string Validate(bool integerOnly)
        {
            if (Min.HasValue && !IsValidBound(Min.Value, integerOnly))
            {
                return integerOnly
                    ? "Min must be a finite non-negative integer."
                    : "Min must be finite and non-negative.";
            }

            if (Max.HasValue && !IsValidBound(Max.Value, integerOnly))
            {
                return integerOnly
                    ? "Max must be a finite non-negative integer."
                    : "Max must be finite and non-negative.";
            }

            if (Min.HasValue && Max.HasValue && Max.Value < Min.Value)
            {
                return "Max must be greater than or equal to Min.";
            }

            return null;
        }

        private static bool IsValidBound(double value, bool integerOnly)
        {
            if (!IsFinite(value) || value < 0)
            {
                return false;
            }

            return !integerOnly || Math.Truncate(value) == value;
        }

        internal static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    public sealed class FilterSettings
    {
        public int SchemaVersion = 1;
        public bool Enabled = false;
        public bool ExcludeOfficial = false;
        public string Mode = "All";
        public int[] Difficulties = new int[0];
        public NumericRange Nps = new NumericRange();
        public NumericRange Njs = new NumericRange();
        public NumericRange Duration = new NumericRange();
        public NumericRange Bpm = new NumericRange();
        public NumericRange Notes = new NumericRange();

        // Enabled is an application state. This property describes the actual
        // filter expression even when a legacy file left Enabled false or true.
        public bool HasConstraints
        {
            get { return ExcludeOfficial || RequiresSongMetrics; }
        }

        public bool RequiresSongMetrics
        {
            get
            {
                if (!IsAllMode(Mode))
                {
                    return true;
                }

                if (Difficulties != null && Difficulties.Length > 0)
                {
                    return true;
                }

                return (Nps != null && Nps.IsSet)
                    || (Njs != null && Njs.IsSet)
                    || (Duration != null && Duration.IsSet)
                    || (Bpm != null && Bpm.IsSet)
                    || (Notes != null && Notes.IsSet);
            }
        }

        public bool ShouldSerializeHasConstraints()
        {
            return false;
        }

        public bool ShouldSerializeRequiresSongMetrics()
        {
            return false;
        }

        public FilterSettings Clone()
        {
            return new FilterSettings
            {
                SchemaVersion = SchemaVersion,
                Enabled = Enabled,
                ExcludeOfficial = ExcludeOfficial,
                Mode = Mode,
                Difficulties = Difficulties == null ? null : (int[])Difficulties.Clone(),
                Nps = Nps == null ? null : Nps.Clone(),
                Njs = Njs == null ? null : Njs.Clone(),
                Duration = Duration == null ? null : Duration.Clone(),
                Bpm = Bpm == null ? null : Bpm.Clone(),
                Notes = Notes == null ? null : Notes.Clone()
            };
        }

        public NumericRange Range(Metric metric)
        {
            switch (metric)
            {
                case Metric.Nps:
                    return Nps;
                case Metric.Njs:
                    return Njs;
                case Metric.Duration:
                    return Duration;
                case Metric.Bpm:
                    return Bpm;
                case Metric.Notes:
                    return Notes;
                default:
                    throw new ArgumentOutOfRangeException("metric");
            }
        }

        public string Validate()
        {
            if (SchemaVersion != 1)
            {
                return "Unsupported schema version.";
            }

            string normalizedMode = NormalizeMode(Mode);
            if (String.IsNullOrWhiteSpace(normalizedMode))
            {
                return "Mode is required.";
            }

            if (Difficulties == null)
            {
                return "Difficulties cannot be null.";
            }

            for (int i = 0; i < Difficulties.Length; i++)
            {
                if (Difficulties[i] < 0 || Difficulties[i] > 4)
                {
                    return "Difficulty must be between 0 and 4.";
                }
            }

            if (Nps == null)
            {
                return "Nps range is required.";
            }

            string error = Nps.Validate(false);
            if (error != null)
            {
                return "Nps: " + error;
            }

            if (Njs == null)
            {
                return "Njs range is required.";
            }

            error = Njs.Validate(false);
            if (error != null)
            {
                return "Njs: " + error;
            }

            if (Duration == null)
            {
                return "Duration range is required.";
            }

            error = Duration.Validate(false);
            if (error != null)
            {
                return "Duration: " + error;
            }

            if (Bpm == null)
            {
                return "Bpm range is required.";
            }

            error = Bpm.Validate(false);
            if (error != null)
            {
                return "Bpm: " + error;
            }

            if (Notes == null)
            {
                return "Notes range is required.";
            }

            error = Notes.Validate(true);
            if (error != null)
            {
                return "Notes: " + error;
            }

            return null;
        }

        // Produce the canonical, detached form used by all domain operations.
        // Invalid input is rejected before the caller can observe a partial edit.
        public FilterSettings Normalize()
        {
            FilterSettings result = Clone();
            string error = result.Validate();
            if (error != null)
            {
                throw new InvalidDataException(error);
            }

            result.Mode = NormalizeMode(result.Mode);
            Array.Sort(result.Difficulties);
            if (result.Difficulties.Length > 1)
            {
                List<int> distinct = new List<int>();
                for (int i = 0; i < result.Difficulties.Length; i++)
                {
                    if (i == 0 || result.Difficulties[i] != result.Difficulties[i - 1])
                    {
                        distinct.Add(result.Difficulties[i]);
                    }
                }

                result.Difficulties = distinct.ToArray();
            }

            result.Enabled = result.HasConstraints;
            return result;
        }

        public int ActiveCount
        {
            get
            {
                if (!Enabled)
                {
                    return 0;
                }

                int count = 0;
                if (ExcludeOfficial)
                {
                    count++;
                }
                if (!IsAllMode(Mode))
                {
                    count++;
                }

                if (Difficulties != null && Difficulties.Length > 0)
                {
                    count++;
                }

                if (Nps != null && Nps.IsSet)
                {
                    count++;
                }

                if (Njs != null && Njs.IsSet)
                {
                    count++;
                }

                if (Duration != null && Duration.IsSet)
                {
                    count++;
                }

                if (Bpm != null && Bpm.IsSet)
                {
                    count++;
                }

                if (Notes != null && Notes.IsSet)
                {
                    count++;
                }

                return count == 0 ? 1 : count;
            }
        }

        // Keep derived UI state out of the persisted settings.
        public bool ShouldSerializeActiveCount()
        {
            return false;
        }

        public static string NormalizeMode(string mode)
        {
            if (mode == null)
            {
                return null;
            }

            string value = mode.Trim();
            if (value.Length == 0)
            {
                return value;
            }

            string compact = value.Replace(" ", String.Empty)
                .Replace("-", String.Empty)
                .Replace("_", String.Empty);
            if (String.Equals(compact, "All", StringComparison.OrdinalIgnoreCase))
            {
                return "All";
            }

            if (String.Equals(compact, "90Degree", StringComparison.OrdinalIgnoreCase)
                || String.Equals(compact, "NinetyDegree", StringComparison.OrdinalIgnoreCase)
                || String.Equals(compact, "Degree90", StringComparison.OrdinalIgnoreCase)
                || String.Equals(compact, "90", StringComparison.OrdinalIgnoreCase))
            {
                return "Degree90";
            }

            if (String.Equals(compact, "360Degree", StringComparison.OrdinalIgnoreCase)
                || String.Equals(compact, "ThreeSixtyDegree", StringComparison.OrdinalIgnoreCase)
                || String.Equals(compact, "Degree360", StringComparison.OrdinalIgnoreCase)
                || String.Equals(compact, "ThreeSixty", StringComparison.OrdinalIgnoreCase)
                || String.Equals(compact, "360", StringComparison.OrdinalIgnoreCase))
            {
                return "Degree360";
            }

            if (String.Equals(value, "Standard", StringComparison.OrdinalIgnoreCase))
            {
                return "Standard";
            }

            if (String.Equals(value, "OneSaber", StringComparison.OrdinalIgnoreCase))
            {
                return "OneSaber";
            }

            return value;
        }

        internal static bool IsAllMode(string mode)
        {
            return String.Equals(NormalizeMode(mode), "All", StringComparison.OrdinalIgnoreCase);
        }
    }

    public sealed class ChartMetrics
    {
        public string Mode;
        public int Difficulty;
        public double? Njs;
        public int? Notes;
    }

    public sealed class SongMetrics
    {
        public string Id;
        public string Name;
        public double? Duration;
        public double? Bpm;
        public ChartMetrics[] Charts;
    }

    public static class FilterEngine
    {
        public static bool IsCustomLevelId(string levelId)
        {
            return levelId != null && levelId.StartsWith("custom_level_", StringComparison.Ordinal);
        }

        public static MatchAssessment Assess(SongMetrics song, FilterSettings settings)
        {
            if (song == null || settings == null)
            {
                return MatchAssessment.NoMatch;
            }

            // A disabled filter is intentionally unrestricted, including any
            // stale mode/difficulty values left by an older settings file.
            if (!settings.Enabled)
            {
                return MatchAssessment.Match;
            }

            // Runtime normalizes settings once at the entry point. Assess is
            // also public, so retain validation here, but read the validated
            // object directly to avoid cloning it for every song in a preview.
            if (settings.Validate() != null)
            {
                return MatchAssessment.NoMatch;
            }

            if (settings.ExcludeOfficial && !IsCustomLevelId(song.Id))
            {
                return MatchAssessment.NoMatch;
            }

            if (!settings.RequiresSongMetrics)
            {
                return MatchAssessment.Match;
            }

            FilterSettings effective = settings;

            MatchAssessment global = GlobalAssessment(song, effective);
            if (global == MatchAssessment.NoMatch)
            {
                return MatchAssessment.NoMatch;
            }

            if (song.Charts == null)
            {
                return MatchAssessment.Unknown;
            }

            if (song.Charts.Length == 0)
            {
                return MatchAssessment.NoMatch;
            }

            bool sawUnknown = false;
            for (int i = 0; i < song.Charts.Length; i++)
            {
                MatchAssessment chart = ChartAssessment(song, song.Charts[i], effective);
                if (chart == MatchAssessment.NoMatch)
                {
                    continue;
                }

                if (chart == MatchAssessment.Match && global == MatchAssessment.Match)
                {
                    return MatchAssessment.Match;
                }

                // An unknown global value or chart value may become a match,
                // but only after all known constraints have passed.
                if (global == MatchAssessment.Unknown || chart == MatchAssessment.Unknown)
                {
                    sawUnknown = true;
                }
            }

            return sawUnknown ? MatchAssessment.Unknown : MatchAssessment.NoMatch;
        }

        public static bool Matches(SongMetrics song, FilterSettings settings)
        {
            return Assess(song, settings) == MatchAssessment.Match;
        }

        public static double? SortValue(
            SongMetrics song,
            FilterSettings settings,
            Metric metric,
            bool ascending)
        {
            if (song == null || settings == null || !IsMetric(metric))
            {
                return null;
            }

            bool fullFilter = settings.Enabled && settings.HasConstraints;
            FilterSettings effective;
            if (!fullFilter)
            {
                // Do not let disabled legacy mode/difficulty scope affect
                // chart selection during sorting.
                effective = new FilterSettings();
            }
            else
            {
                if (settings.Validate() != null)
                {
                    return null;
                }

                // The runtime supplies a normalized instance. SortValue is
                // public as well, so validate but keep this call allocation-free.
                effective = settings;
            }

            if (fullFilter && GlobalAssessment(song, effective) != MatchAssessment.Match)
            {
                return null;
            }

            double? selected = null;
            bool foundChart = false;
            if (song.Charts != null)
            {
                for (int i = 0; i < song.Charts.Length; i++)
                {
                    ChartMetrics chart = song.Charts[i];
                    if (ChartAssessment(song, chart, effective) != MatchAssessment.Match)
                    {
                        continue;
                    }

                    foundChart = true;
                    double? value = GetMetricValue(song, chart, metric);
                    if (!value.HasValue || !NumericRange.IsFinite(value.Value))
                    {
                        continue;
                    }

                    if (!selected.HasValue || (ascending && value.Value < selected.Value)
                        || (!ascending && value.Value > selected.Value))
                    {
                        selected = value;
                    }
                }
            }

            return foundChart ? selected : null;
        }

        public static IList<SongMetrics> Sort(
            IEnumerable<SongMetrics> songs,
            FilterSettings settings,
            Metric metric,
            bool ascending)
        {
            List<SortEntry> entries = new List<SortEntry>();
            if (songs != null)
            {
                int index = 0;
                foreach (SongMetrics song in songs)
                {
                    entries.Add(new SortEntry
                    {
                        Song = song,
                        Value = SortValue(song, settings, metric, ascending),
                        Index = index++
                    });
                }
            }

            entries.Sort(delegate (SortEntry left, SortEntry right)
            {
                bool leftKnown = left.Value.HasValue && NumericRange.IsFinite(left.Value.Value);
                bool rightKnown = right.Value.HasValue && NumericRange.IsFinite(right.Value.Value);
                if (leftKnown != rightKnown)
                {
                    return leftKnown ? -1 : 1;
                }

                if (leftKnown)
                {
                    int valueComparison = left.Value.Value.CompareTo(right.Value.Value);
                    if (!ascending)
                    {
                        valueComparison = -valueComparison;
                    }

                    if (valueComparison != 0)
                    {
                        return valueComparison;
                    }
                }

                int nameComparison = StringComparer.OrdinalIgnoreCase.Compare(
                    left.Song == null ? null : left.Song.Name,
                    right.Song == null ? null : right.Song.Name);
                if (nameComparison != 0)
                {
                    return nameComparison;
                }

                int idComparison = StringComparer.Ordinal.Compare(
                    left.Song == null ? null : left.Song.Id,
                    right.Song == null ? null : right.Song.Id);
                if (idComparison != 0)
                {
                    return idComparison;
                }

                return left.Index.CompareTo(right.Index);
            });

            List<SongMetrics> result = new List<SongMetrics>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                result.Add(entries[i].Song);
            }

            return result;
        }

        public static string Format(Metric metric, double? value)
        {
            if (!value.HasValue || !NumericRange.IsFinite(value.Value))
            {
                return "—";
            }

            switch (metric)
            {
                case Metric.Notes:
                    if (Math.Truncate(value.Value) != value.Value)
                    {
                        return value.Value.ToString("0.##", CultureInfo.InvariantCulture);
                    }

                    return value.Value.ToString("0", CultureInfo.InvariantCulture);
                case Metric.Nps:
                case Metric.Njs:
                case Metric.Duration:
                case Metric.Bpm:
                    return value.Value.ToString("0.##", CultureInfo.InvariantCulture);
                default:
                    return value.Value.ToString("0.##", CultureInfo.InvariantCulture);
            }
        }

        private static bool IsMetric(Metric metric)
        {
            return metric == Metric.Nps || metric == Metric.Njs || metric == Metric.Duration
                || metric == Metric.Bpm || metric == Metric.Notes;
        }

        private static MatchAssessment GlobalAssessment(SongMetrics song, FilterSettings settings)
        {
            MatchAssessment duration = RangeAssessment(settings.Duration, SanitizeMetric(song.Duration));
            if (duration == MatchAssessment.NoMatch)
            {
                return MatchAssessment.NoMatch;
            }

            MatchAssessment bpm = RangeAssessment(settings.Bpm, SanitizeMetric(song.Bpm));
            if (bpm == MatchAssessment.NoMatch)
            {
                return MatchAssessment.NoMatch;
            }

            return duration == MatchAssessment.Unknown || bpm == MatchAssessment.Unknown
                ? MatchAssessment.Unknown : MatchAssessment.Match;
        }

        private static MatchAssessment RangeAssessment(NumericRange range, double? value)
        {
            if (range == null || !range.IsSet)
            {
                return MatchAssessment.Match;
            }

            if (!value.HasValue || !NumericRange.IsFinite(value.Value))
            {
                return MatchAssessment.Unknown;
            }

            if (range.Min.HasValue && value.Value < range.Min.Value)
            {
                return MatchAssessment.NoMatch;
            }

            if (range.Max.HasValue && value.Value > range.Max.Value)
            {
                return MatchAssessment.NoMatch;
            }

            return MatchAssessment.Match;
        }

        private static MatchAssessment ChartAssessment(
            SongMetrics song,
            ChartMetrics chart,
            FilterSettings settings)
        {
            if (chart == null)
            {
                return MatchAssessment.Unknown;
            }

            MatchAssessment mode = ModeAssessment(chart.Mode, settings.Mode);
            if (mode == MatchAssessment.NoMatch)
            {
                return MatchAssessment.NoMatch;
            }

            MatchAssessment difficulty = DifficultyAssessment(chart.Difficulty, settings.Difficulties);
            if (difficulty == MatchAssessment.NoMatch)
            {
                return MatchAssessment.NoMatch;
            }

            MatchAssessment nps = RangeAssessment(settings.Nps, CalculateNps(song, chart));
            if (nps == MatchAssessment.NoMatch)
            {
                return MatchAssessment.NoMatch;
            }

            MatchAssessment njs = RangeAssessment(settings.Njs, SanitizeMetric(chart.Njs));
            if (njs == MatchAssessment.NoMatch)
            {
                return MatchAssessment.NoMatch;
            }

            double? notes = chart.Notes.HasValue && chart.Notes.Value >= 0
                ? (double?)chart.Notes.Value : null;
            MatchAssessment noteCount = RangeAssessment(settings.Notes, notes);
            if (noteCount == MatchAssessment.NoMatch)
            {
                return MatchAssessment.NoMatch;
            }

            return mode == MatchAssessment.Unknown || difficulty == MatchAssessment.Unknown
                || nps == MatchAssessment.Unknown || njs == MatchAssessment.Unknown
                || noteCount == MatchAssessment.Unknown
                ? MatchAssessment.Unknown : MatchAssessment.Match;
        }

        private static MatchAssessment ModeAssessment(string chartMode, string selectedMode)
        {
            if (FilterSettings.IsAllMode(selectedMode))
            {
                return MatchAssessment.Match;
            }

            if (String.IsNullOrWhiteSpace(chartMode))
            {
                return MatchAssessment.Unknown;
            }

            return String.Equals(FilterSettings.NormalizeMode(chartMode),
                FilterSettings.NormalizeMode(selectedMode), StringComparison.OrdinalIgnoreCase)
                ? MatchAssessment.Match : MatchAssessment.NoMatch;
        }

        private static MatchAssessment DifficultyAssessment(int difficulty, int[] selectedDifficulties)
        {
            if (selectedDifficulties == null)
            {
                return MatchAssessment.Unknown;
            }

            if (selectedDifficulties.Length == 0)
            {
                return MatchAssessment.Match;
            }

            for (int i = 0; i < selectedDifficulties.Length; i++)
            {
                if (selectedDifficulties[i] == difficulty)
                {
                    return MatchAssessment.Match;
                }
            }

            return MatchAssessment.NoMatch;
        }

        private static double? CalculateNps(SongMetrics song, ChartMetrics chart)
        {
            if (song == null || chart == null || !song.Duration.HasValue || !chart.Notes.HasValue)
            {
                return null;
            }

            double duration = song.Duration.Value;
            if (!NumericRange.IsFinite(duration) || duration <= 0 || chart.Notes.Value < 0)
            {
                return null;
            }

            double nps = (double)chart.Notes.Value / duration;
            return NumericRange.IsFinite(nps) && nps >= 0 ? (double?)nps : null;
        }

        private static double? GetMetricValue(SongMetrics song, ChartMetrics chart, Metric metric)
        {
            switch (metric)
            {
                case Metric.Nps:
                    return CalculateNps(song, chart);
                case Metric.Njs:
                    return chart == null ? null : SanitizeMetric(chart.Njs);
                case Metric.Duration:
                    return song == null ? null : SanitizeMetric(song.Duration);
                case Metric.Bpm:
                    return song == null ? null : SanitizeMetric(song.Bpm);
                case Metric.Notes:
                    return chart == null || !chart.Notes.HasValue || chart.Notes.Value < 0
                        ? null
                        : (double?)chart.Notes.Value;
                default:
                    return null;
            }
        }

        private static double? SanitizeMetric(double? value)
        {
            if (!value.HasValue || !NumericRange.IsFinite(value.Value) || value.Value < 0)
            {
                return null;
            }

            return value;
        }

        private sealed class SortEntry
        {
            public SongMetrics Song;
            public double? Value;
            public int Index;
        }
    }
}
