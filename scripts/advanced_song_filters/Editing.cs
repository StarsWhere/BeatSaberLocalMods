using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AdvancedSongFilters
{
    public static class FilterEditing
    {
        public static double Step(Metric metric)
        {
            switch (metric)
            {
                case Metric.Nps:
                    return 0.5;
                case Metric.Njs:
                    return 1;
                case Metric.Duration:
                    return 15;
                case Metric.Bpm:
                    return 5;
                case Metric.Notes:
                    return 50;
                default:
                    throw new ArgumentOutOfRangeException("metric");
            }
        }

        public static string FormatBound(Metric metric, double? value)
        {
            if (!value.HasValue)
            {
                return "Any";
            }

            return FormatValue(metric, value.Value);
        }

        public static string FormatInput(Metric metric, double? value)
        {
            if (!value.HasValue)
            {
                return String.Empty;
            }

            return FormatInputValue(metric, value.Value);
        }

        public static bool TryParse(Metric metric, string text, out double? value)
        {
            value = null;
            if (String.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            string input = text.Trim();
            if (metric == Metric.Duration && input.IndexOf(":", StringComparison.Ordinal) >= 0)
            {
                string[] parts = input.Split(':');
                int minutes;
                int seconds;
                if (parts.Length != 2
                    || !Int32.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes)
                    || !Int32.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds)
                    || minutes < 0 || seconds < 0 || seconds >= 60)
                {
                    return false;
                }

                value = minutes * 60d + seconds;
                return NumericRange.IsFinite(value.Value);
            }

            double parsed;
            if (!Double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                || !NumericRange.IsFinite(parsed) || parsed < 0)
            {
                return false;
            }

            if (metric == Metric.Notes && Math.Truncate(parsed) != parsed)
            {
                return false;
            }

            value = parsed;
            return true;
        }

        private static string FormatValue(Metric metric, double value)
        {
            if (!NumericRange.IsFinite(value))
            {
                return "—";
            }

            if (metric == Metric.Duration)
            {
                double rounded = Math.Round(value);
                if (rounded <= Int32.MaxValue)
                {
                    int total = Math.Max(0, (int)rounded);
                    return (total / 60).ToString(CultureInfo.InvariantCulture) + ":"
                        + (total % 60).ToString("00", CultureInfo.InvariantCulture);
                }
            }

            if (metric == Metric.Notes && Math.Truncate(value) == value)
            {
                return value.ToString("0", CultureInfo.InvariantCulture);
            }

            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private static string FormatInputValue(Metric metric, double value)
        {
            if (!NumericRange.IsFinite(value))
            {
                return "—";
            }

            // Colon input represents whole seconds. Fractional duration stays
            // in seconds so opening and confirming an editor is lossless.
            if (metric == Metric.Duration && Math.Truncate(value) == value
                && value <= Int32.MaxValue)
            {
                int total = Math.Max(0, (int)value);
                return (total / 60).ToString(CultureInfo.InvariantCulture) + ":"
                    + (total % 60).ToString("00", CultureInfo.InvariantCulture);
            }

            if (metric == Metric.Notes && Math.Truncate(value) == value)
            {
                return value.ToString("0", CultureInfo.InvariantCulture);
            }

            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }

    public sealed class FilterEditSession
    {
        FilterSettings draft;
        int revision;

        public FilterEditSession(FilterSettings applied)
        {
            draft = NormalizeForEditing(applied);
            revision = 0;
        }

        public FilterSettings Draft
        {
            get { return draft.Clone(); }
        }

        public int Revision
        {
            get { return revision; }
        }

        public void Reset()
        {
            ApplyCandidate(Unrestricted());
        }

        public void Load(FilterSettings applied)
        {
            if (applied == null)
            {
                throw new ArgumentNullException("applied");
            }

            // Loading a preset is different from opening the currently
            // applied filter: a disabled preset may still carry conditions
            // that should become active when it is loaded into the draft.
            ApplyCandidate(applied.Normalize());
        }

        public void SetMode(string mode)
        {
            FilterSettings candidate = draft.Clone();
            candidate.Mode = mode;
            ApplyCandidate(candidate.Normalize());
        }

        public void SetExcludeOfficial(bool exclude)
        {
            FilterSettings candidate = draft.Clone();
            candidate.ExcludeOfficial = exclude;
            ApplyCandidate(candidate.Normalize());
        }

        public void ToggleDifficulty(int difficulty)
        {
            if (difficulty < 0 || difficulty > 4)
            {
                throw new ArgumentOutOfRangeException("difficulty");
            }

            FilterSettings candidate = draft.Clone();
            List<int> values = new List<int>();
            if (candidate.Difficulties != null)
            {
                values.AddRange(candidate.Difficulties);
            }

            int index = values.IndexOf(difficulty);
            if (index >= 0)
            {
                values.RemoveAt(index);
            }
            else
            {
                values.Add(difficulty);
            }

            candidate.Difficulties = values.ToArray();
            ApplyCandidate(candidate.Normalize());
        }

        public void SelectAllDifficulties()
        {
            FilterSettings candidate = draft.Clone();
            candidate.Difficulties = new int[0];
            ApplyCandidate(candidate.Normalize());
        }

        public void SetRange(Metric metric, NumericRange range)
        {
            if (range == null)
            {
                throw new ArgumentNullException("range");
            }

            FilterSettings candidate = draft.Clone();
            SetRangeValue(candidate, metric, range.Clone());
            ApplyCandidate(candidate.Normalize());
        }

        public bool CanAdjustBound(Metric metric, bool isMin, int direction)
        {
            FilterSettings candidate;
            return TryBuildAdjusted(metric, isMin, direction, out candidate);
        }

        public bool AdjustBound(Metric metric, bool isMin, int direction)
        {
            FilterSettings candidate;
            if (!TryBuildAdjusted(metric, isMin, direction, out candidate))
            {
                return false;
            }

            ApplyCandidate(candidate);
            return true;
        }

        bool TryBuildAdjusted(Metric metric, bool isMin, int direction, out FilterSettings candidate)
        {
            candidate = null;
            if (direction != -1 && direction != 1)
            {
                return false;
            }

            NumericRange currentRange;
            try
            {
                currentRange = draft.Range(metric);
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }

            if (currentRange == null)
            {
                return false;
            }

            double? current = isMin ? currentRange.Min : currentRange.Max;
            double step = FilterEditing.Step(metric);
            if (!current.HasValue && direction < 0)
            {
                return false;
            }

            double next;
            if (!current.HasValue)
            {
                if (isMin && currentRange.Max.HasValue)
                {
                    next = Math.Min(step, currentRange.Max.Value);
                }
                else if (!isMin && currentRange.Min.HasValue)
                {
                    next = Math.Max(step, currentRange.Min.Value);
                }
                else
                {
                    next = step;
                }
            }
            else
            {
                next = current.Value + step * direction;
            }

            if (!NumericRange.IsFinite(next) || next < 0)
            {
                return false;
            }

            if (direction < 0 && !isMin && currentRange.Min.HasValue
                && next < currentRange.Min.Value)
            {
                return false;
            }

            if (isMin && currentRange.Max.HasValue && next > currentRange.Max.Value)
            {
                next = currentRange.Max.Value;
            }

            if (!isMin && currentRange.Min.HasValue && next < currentRange.Min.Value)
            {
                next = currentRange.Min.Value;
            }

            if (current.HasValue && next == current.Value)
            {
                return false;
            }

            candidate = draft.Clone();
            NumericRange changed = candidate.Range(metric);
            if (isMin)
            {
                changed.Min = next;
            }
            else
            {
                changed.Max = next;
            }

            try
            {
                candidate = candidate.Normalize();
            }
            catch (Exception)
            {
                candidate = null;
                return false;
            }

            return !Equivalent(draft, candidate);
        }

        void ApplyCandidate(FilterSettings candidate)
        {
            FilterSettings normalized = candidate.Normalize();
            if (!Equivalent(draft, normalized))
            {
                draft = normalized;
                revision++;
            }
        }

        static FilterSettings NormalizeForEditing(FilterSettings applied)
        {
            if (applied == null || !applied.Enabled)
            {
                return Unrestricted();
            }

            return applied.Normalize();
        }

        static FilterSettings Unrestricted()
        {
            return new FilterSettings
            {
                SchemaVersion = 1,
                Enabled = false,
                Mode = "All",
                Difficulties = new int[0],
                Nps = new NumericRange(),
                Njs = new NumericRange(),
                Duration = new NumericRange(),
                Bpm = new NumericRange(),
                Notes = new NumericRange()
            };
        }

        static void SetRangeValue(FilterSettings settings, Metric metric, NumericRange range)
        {
            switch (metric)
            {
                case Metric.Nps:
                    settings.Nps = range;
                    break;
                case Metric.Njs:
                    settings.Njs = range;
                    break;
                case Metric.Duration:
                    settings.Duration = range;
                    break;
                case Metric.Bpm:
                    settings.Bpm = range;
                    break;
                case Metric.Notes:
                    settings.Notes = range;
                    break;
                default:
                    throw new ArgumentOutOfRangeException("metric");
            }
        }

        static bool Equivalent(FilterSettings left, FilterSettings right)
        {
            if (left == null || right == null)
            {
                return left == right;
            }

            return left.SchemaVersion == right.SchemaVersion
                && left.Enabled == right.Enabled
                && left.ExcludeOfficial == right.ExcludeOfficial
                && String.Equals(left.Mode, right.Mode, StringComparison.Ordinal)
                && EqualDifficulties(left.Difficulties, right.Difficulties)
                && EqualRange(left.Nps, right.Nps)
                && EqualRange(left.Njs, right.Njs)
                && EqualRange(left.Duration, right.Duration)
                && EqualRange(left.Bpm, right.Bpm)
                && EqualRange(left.Notes, right.Notes);
        }

        static bool EqualDifficulties(int[] left, int[] right)
        {
            if (left == null || right == null)
            {
                return left == right;
            }

            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        static bool EqualRange(NumericRange left, NumericRange right)
        {
            if (left == null || right == null)
            {
                return left == right;
            }

            return left.Min == right.Min && left.Max == right.Max;
        }
    }

    public sealed class RangeEditSession
    {
        readonly Metric metric;

        public RangeEditSession(FilterSettings settings, Metric metric)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            NumericRange range = settings.Range(metric);
            if (range == null)
            {
                throw new InvalidDataException("Range is required.");
            }

            this.metric = metric;
            MinimumText = FilterEditing.FormatInput(metric, range.Min);
            MaximumText = FilterEditing.FormatInput(metric, range.Max);
        }

        public Metric Metric
        {
            get { return metric; }
        }

        public string MinimumText { get; set; }
        public string MaximumText { get; set; }

        public string Validate()
        {
            double? minimum;
            if (!FilterEditing.TryParse(metric, MinimumText, out minimum))
            {
                return "Minimum is invalid.";
            }

            double? maximum;
            if (!FilterEditing.TryParse(metric, MaximumText, out maximum))
            {
                return "Maximum is invalid.";
            }

            if (minimum.HasValue && maximum.HasValue && maximum.Value < minimum.Value)
            {
                return "Maximum must be greater than or equal to Minimum.";
            }

            return null;
        }

        public NumericRange GetRange()
        {
            string error = Validate();
            if (error != null)
            {
                throw new InvalidDataException(error);
            }

            double? minimum;
            double? maximum;
            FilterEditing.TryParse(metric, MinimumText, out minimum);
            FilterEditing.TryParse(metric, MaximumText, out maximum);
            return new NumericRange { Min = minimum, Max = maximum };
        }
    }

    public sealed class FilterPreset
    {
        public string Id;
        public string Name;
        public FilterSettings Settings;

        public FilterPreset Clone()
        {
            return new FilterPreset
            {
                Id = Id,
                Name = Name,
                Settings = Settings == null ? null : Settings.Clone()
            };
        }
    }

    public sealed class PresetCollection
    {
        public int SchemaVersion = 1;
        public List<FilterPreset> Presets = new List<FilterPreset>();

        public void Validate()
        {
            if (SchemaVersion != 1)
            {
                throw new InvalidDataException("Unsupported preset schema version.");
            }

            if (Presets == null)
            {
                throw new InvalidDataException("Presets cannot be null.");
            }

            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Presets.Count; i++)
            {
                FilterPreset preset = Presets[i];
                if (preset == null)
                {
                    throw new InvalidDataException("Preset cannot be null.");
                }

                Guid parsedId;
                if (String.IsNullOrEmpty(preset.Id) || preset.Id != preset.Id.Trim()
                    || !Guid.TryParse(preset.Id, out parsedId))
                {
                    throw new InvalidDataException("Preset id must be a GUID.");
                }

                if (!ids.Add(parsedId.ToString("D")))
                {
                    throw new InvalidDataException("Preset ids must be unique.");
                }

                if (preset.Name == null || preset.Name.Length == 0 || preset.Name != preset.Name.Trim()
                    || preset.Name.Length > 32)
                {
                    throw new InvalidDataException("Preset name must be trimmed and contain 1 to 32 characters.");
                }

                if (!names.Add(preset.Name))
                {
                    throw new InvalidDataException("Preset names must be unique.");
                }

                if (preset.Settings == null)
                {
                    throw new InvalidDataException("Preset settings are required.");
                }

                string error = preset.Settings.Validate();
                if (error != null)
                {
                    throw new InvalidDataException("Preset settings: " + error);
                }
            }
        }

        public PresetCollection Clone()
        {
            PresetCollection result = new PresetCollection { SchemaVersion = SchemaVersion };
            if (Presets == null)
            {
                result.Presets = null;
                return result;
            }

            result.Presets = new List<FilterPreset>(Presets.Count);
            for (int i = 0; i < Presets.Count; i++)
            {
                result.Presets.Add(Presets[i] == null ? null : Presets[i].Clone());
            }

            return result;
        }

        public string NextName()
        {
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Presets != null)
            {
                for (int i = 0; i < Presets.Count; i++)
                {
                    if (Presets[i] != null && Presets[i].Name != null)
                    {
                        names.Add(Presets[i].Name.Trim());
                    }
                }
            }

            int number = 1;
            while (names.Contains("Preset " + number.ToString(CultureInfo.InvariantCulture)))
            {
                number++;
            }

            return "Preset " + number.ToString(CultureInfo.InvariantCulture);
        }
    }
}
