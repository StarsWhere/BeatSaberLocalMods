using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace AdvancedSongFilters
{
    public static class CoreTests
    {
        public static int Main()
        {
            try
            {
                TestNumericRanges();
                TestSettingsValidationAndClone();
                TestNormalizationAndAssessment();
                TestJsonRoundTrip();
                TestMatchingUsesOneChart();
                TestMissingDataAndNps();
                TestSortDirectionUnknownsAndTies();
                TestFormatting();
                TestPreviewContract();
                TestExcludeOfficial();
                Console.WriteLine("AdvancedSongFilters core tests passed.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.ToString());
                return 1;
            }
        }

        private static void TestNumericRanges()
        {
            NumericRange range = new NumericRange { Min = 2, Max = 4 };
            Assert(range.IsSet, "a bounded range is set");
            Assert(range.Contains(2), "minimum endpoint is inclusive");
            Assert(range.Contains(4), "maximum endpoint is inclusive");
            Assert(!range.Contains(1.999), "value below both bounds is rejected");
            Assert(!range.Contains((double?)null), "unknown value does not satisfy a set range");

            range = new NumericRange { Min = 3 };
            Assert(range.Contains(3), "single-sided lower bound includes endpoint");
            Assert(range.Contains(300), "single-sided lower bound has no upper cap");
            Assert(!range.Contains(2.99), "single-sided lower bound rejects smaller value");

            range = new NumericRange { Max = 3 };
            Assert(range.Contains(-100), "single-sided upper bound has no lower cap");
            Assert(range.Contains(3), "single-sided upper bound includes endpoint");
            Assert(!range.Contains(3.01), "single-sided upper bound rejects larger value");

            Assert(new NumericRange().Validate(false) == null, "unset range validates");
            Assert(new NumericRange { Min = double.NaN }.Validate(false) != null, "NaN is rejected");
            Assert(new NumericRange { Max = double.PositiveInfinity }.Validate(false) != null, "infinity is rejected");
            Assert(new NumericRange { Min = -1 }.Validate(false) != null, "negative bound is rejected");
            Assert(new NumericRange { Min = 5, Max = 4 }.Validate(false) != null, "reversed range is rejected");
            Assert(new NumericRange { Min = 1.5 }.Validate(true) != null, "fractional integer-only bound is rejected");
        }

        private static void TestExcludeOfficial()
        {
            FilterSettings settings = new FilterSettings { ExcludeOfficial = true }.Normalize();
            Assert(settings.Enabled && settings.HasConstraints && settings.ActiveCount == 1
                && !settings.RequiresSongMetrics, "source-only filtering is active without statistics");
            Assert(settings.Clone().ExcludeOfficial, "cloning retains the source filter");
            Assert(FilterEngine.Assess(new SongMetrics { Id = "100Bills" }, settings) == MatchAssessment.NoMatch,
                "OST songs are excluded even without statistics");
            Assert(FilterEngine.Assess(new SongMetrics { Id = "dlc-song" }, settings) == MatchAssessment.NoMatch,
                "DLC songs are excluded");
            SongMetrics custom = new SongMetrics { Id = "custom_level_HASH", Charts = null };
            Assert(FilterEngine.Assess(custom, settings) == MatchAssessment.Match,
                "custom songs match a source-only filter before chart parsing");
            Assert(FilterEngine.Assess(new SongMetrics { Id = "not_custom_level_HASH" }, settings) == MatchAssessment.NoMatch,
                "only the canonical custom prefix identifies a custom song");
            settings.Nps.Min = 2;
            Assert(FilterEngine.Assess(custom, settings) == MatchAssessment.Unknown,
                "combining source and NPS still requires chart statistics");
            custom.Duration = 100;
            custom.Charts = new[] { Chart("Standard", 2, 10, 200) };
            Assert(FilterEngine.Assess(custom, settings) == MatchAssessment.Match,
                "custom songs use the existing metric predicate");
            settings.Enabled = false;
            Assert(FilterEngine.Assess(new SongMetrics { Id = "100Bills" }, settings) == MatchAssessment.Match,
                "disabling filtering ignores a retained source condition");
            FilterSettings legacy = JsonConvert.DeserializeObject<FilterSettings>("{\"SchemaVersion\":1}");
            Assert(!legacy.ExcludeOfficial && !legacy.Normalize().Enabled,
                "existing settings retain their unrestricted source scope");
            string json = JsonConvert.SerializeObject(new FilterSettings { ExcludeOfficial = true });
            Assert(JsonConvert.DeserializeObject<FilterSettings>(json).ExcludeOfficial
                && json.IndexOf("RequiresSongMetrics", StringComparison.Ordinal) < 0,
                "only the source condition is persisted, not its derived metadata requirement");
        }

        private static void TestSettingsValidationAndClone()
        {
            FilterSettings settings = new FilterSettings();
            Assert(settings.Validate() == null, "default settings validate");
            Assert(settings.ActiveCount == 0, "disabled settings have no active count");

            settings.Enabled = true;
            Assert(settings.ActiveCount == 1, "enabled settings have a visible active count");
            settings.Nps.Min = 2.5;
            settings.Notes.Max = 100;
            settings.Mode = "All";
            settings.Difficulties = new[] { 1, 3 };
            Assert(settings.ActiveCount == 3, "active count includes ranges and difficulty selection");

            FilterSettings clone = settings.Clone();
            Assert(!object.ReferenceEquals(settings, clone), "settings clone is a new object");
            Assert(!object.ReferenceEquals(settings.Difficulties, clone.Difficulties), "difficulty array is cloned");
            Assert(!object.ReferenceEquals(settings.Nps, clone.Nps), "NPS range is cloned");
            clone.Difficulties[0] = 4;
            clone.Nps.Min = 8;
            clone.Mode = "OneSaber";
            Assert(settings.Difficulties[0] == 1, "changing clone does not change difficulties");
            Assert(settings.Nps.Min == 2.5, "changing clone does not change ranges");
            Assert(settings.Mode == "All", "changing clone does not change scalar fields");

            FilterSettings invalid = new FilterSettings { SchemaVersion = 2 };
            Assert(invalid.Validate() != null, "unsupported schema version is rejected");
            invalid = new FilterSettings { Difficulties = new[] { 5 } };
            Assert(invalid.Validate() != null, "invalid difficulty is rejected");
            invalid = new FilterSettings { Mode = " " };
            Assert(invalid.Validate() != null, "blank mode is rejected");
            invalid = new FilterSettings();
            invalid.Notes.Min = 1.25;
            Assert(invalid.Validate() != null, "fractional notes range is rejected");
        }

        private static void TestJsonRoundTrip()
        {
            FilterSettings settings = new FilterSettings
            {
                SchemaVersion = 1,
                Enabled = true,
                Mode = "OneSaber",
                Difficulties = new[] { 0, 4 }
            };
            settings.Nps.Min = 2;
            settings.Njs.Max = 18.5;
            settings.Duration.Min = 60;
            settings.Bpm.Max = 150;
            settings.Notes.Min = 120;

            string json = JsonConvert.SerializeObject(settings);
            Assert(json.IndexOf("ActiveCount", StringComparison.Ordinal) < 0, "derived active count is not persisted");
            Assert(json.IndexOf("IsSet", StringComparison.Ordinal) < 0, "derived range state is not persisted");
            FilterSettings roundTrip = JsonConvert.DeserializeObject<FilterSettings>(json);
            Assert(roundTrip != null && roundTrip.Validate() == null, "JSON roundtrip validates");
            Assert(roundTrip.Enabled && roundTrip.Mode == settings.Mode, "JSON roundtrip preserves scalar fields");
            Assert(roundTrip.Difficulties.Length == 2 && roundTrip.Difficulties[1] == 4,
                "JSON roundtrip preserves difficulty selection");
            Assert(roundTrip.Nps.Min == 2 && roundTrip.Njs.Max == 18.5,
                "JSON roundtrip preserves numeric ranges");
        }

        private static void TestNormalizationAndAssessment()
        {
            FilterSettings defaults = new FilterSettings();
            Assert(defaults.Mode == "All" && !defaults.HasConstraints,
                "new settings are unrestricted VR defaults");

            FilterSettings normalized = new FilterSettings
            {
                Enabled = false,
                Mode = " NinetyDegree ",
                Difficulties = new[] { 3, 1, 3 }
            };
            FilterSettings canonical = normalized.Normalize();
            Assert(canonical.Enabled && canonical.Mode == "Degree90",
                "normalization canonicalizes mode and enables constraints");
            Assert(canonical.Difficulties.Length == 2 && canonical.Difficulties[0] == 1
                && canonical.Difficulties[1] == 3, "normalization sorts and deduplicates difficulties");
            Assert(FilterSettings.NormalizeMode("360Degree") == "Degree360"
                && FilterSettings.NormalizeMode("Degree360") == "Degree360",
                "mode aliases normalize to canonical degree modes");

            FilterSettings direct = EnabledSettings("90Degree", new[] { 3, 1, 3 });
            direct.Njs.Min = 10;
            SongMetrics directAlias = Song("direct-alias", "Direct alias", 100, 100,
                Chart("Degree90", 1, 10, 200));
            Assert(FilterEngine.Assess(directAlias, direct) == MatchAssessment.Match,
                "public assessment validates and reads unnormalized aliases without changing semantics");

            FilterSettings nps = EnabledSettings("All", new int[0]);
            nps.Nps.Min = 1;
            SongMetrics unknown = Song("unknown", "Unknown", 100, 100,
                Chart("Standard", 1, 10, null));
            Assert(FilterEngine.Assess(unknown, nps) == MatchAssessment.Unknown,
                "missing relevant metrics produce an unknown assessment");

            SongMetrics mixed = Song("mixed", "Mixed", 100, 100,
                Chart("Standard", 1, 10, null), Chart("Standard", 1, 10, 200));
            Assert(FilterEngine.Assess(mixed, nps) == MatchAssessment.Match,
                "a matching chart overrides an unknown chart");

            FilterSettings scope = EnabledSettings("Standard", new[] { 1 });
            scope.Njs.Min = 5;
            SongMetrics excluded = Song("excluded", "Excluded", 100, 100,
                Chart("OneSaber", 1, null, 200));
            Assert(FilterEngine.Assess(excluded, scope) == MatchAssessment.NoMatch,
                "known mode exclusion dominates missing chart metrics");

            FilterSettings globalUnknown = EnabledSettings("Standard", new[] { 1 });
            globalUnknown.Duration.Min = 50;
            SongMetrics globalExcluded = Song("global-excluded", "Global excluded", null, 100,
                Chart("OneSaber", 1, 10, 200));
            Assert(FilterEngine.Assess(globalExcluded, globalUnknown) == MatchAssessment.NoMatch,
                "known chart exclusion dominates an unknown song metric");

            FilterSettings disabled = new FilterSettings
            {
                Enabled = false,
                Mode = "Standard",
                Difficulties = new[] { 1 }
            };
            SongMetrics oneSaber = Song("one", "One", 100, 100,
                Chart("OneSaber", 1, 4, 10));
            Assert(FilterEngine.SortValue(oneSaber, disabled, Metric.Njs, true) == 4,
                "disabled stale mode and difficulty scope do not affect sorting");
        }

        private static void TestMatchingUsesOneChart()
        {
            FilterSettings settings = EnabledSettings("Standard", new[] { 2 });
            settings.Duration.Min = 120;
            settings.Duration.Max = 120;
            settings.Bpm.Min = 100;
            settings.Bpm.Max = 100;
            settings.Nps.Min = 1;
            settings.Nps.Max = 2;
            settings.Njs.Min = 10;
            settings.Njs.Max = 10;
            settings.Notes.Min = 240;
            settings.Notes.Max = 240;

            SongMetrics passing = Song("pass", "Passing", 120, 100,
                Chart("Standard", 2, 10, 240));
            Assert(FilterEngine.Matches(passing, settings), "one chart satisfying all filters passes");
            Assert(FilterEngine.Matches(passing, new FilterSettings()), "disabled settings do not filter");

            SongMetrics splitAcrossCharts = Song("split", "Split", 120, 100,
                Chart("Standard", 2, 10, 120),
                Chart("Standard", 2, 20, 240));
            Assert(!FilterEngine.Matches(splitAcrossCharts, settings),
                "different charts cannot combine to satisfy filters");

            settings.Mode = "OneSaber";
            Assert(!FilterEngine.Matches(passing, settings), "mode selection narrows chart candidates");
            settings.Mode = "All";
            settings.Difficulties = new[] { 3 };
            Assert(!FilterEngine.Matches(passing, settings), "difficulty selection narrows chart candidates");
            settings.Difficulties = new int[0];
            Assert(FilterEngine.Matches(passing, settings), "empty difficulty selection means all difficulties");
        }

        private static void TestMissingDataAndNps()
        {
            FilterSettings nps = EnabledSettings("All", new int[0]);
            nps.Nps.Min = 1;

            SongMetrics zeroDuration = Song("zero", "Zero", 0, 100, Chart("Standard", 1, 10, 10));
            Assert(!FilterEngine.Matches(zeroDuration, nps), "zero duration does not become zero NPS");
            Assert(FilterEngine.SortValue(zeroDuration, nps, Metric.Nps, true) == null,
                "zero duration produces unknown NPS sort value");

            SongMetrics missing = Song("missing", "Missing", null, null,
                new ChartMetrics { Mode = "Standard", Difficulty = 1, Njs = null, Notes = null });
            Assert(!FilterEngine.Matches(missing, nps), "missing data cannot satisfy a set NPS range");
            Assert(FilterEngine.SortValue(missing, new FilterSettings(), Metric.Njs, true) == null,
                "missing NJS remains unknown when sorting");

            FilterSettings noNpsRange = EnabledSettings("All", new int[0]);
            Assert(FilterEngine.Matches(zeroDuration, noNpsRange),
                "invalid duration is allowed when no NPS condition is active");
        }

        private static void TestSortDirectionUnknownsAndTies()
        {
            FilterSettings settings = new FilterSettings { Mode = "All" };
            SongMetrics low = Song("low", "Low", 100, 100, Chart("Standard", 1, 10, 20));
            SongMetrics high = Song("high", "High", 100, 100, Chart("Standard", 1, 30, 20));
            SongMetrics tieB = Song("b", "Same", 100, 100, Chart("Standard", 1, 20, 20));
            SongMetrics tieA = Song("a", "same", 100, 100, Chart("Standard", 1, 20, 20));
            SongMetrics unknown = Song("unknown", "Unknown", 100, 100,
                Chart("Standard", 1, null, 20));
            SongMetrics noChart = Song("no-chart", "No chart", 100, 100);

            Assert(FilterEngine.SortValue(
                Song("multi", "Multi", 100, 100,
                    Chart("Standard", 1, 30, 20), Chart("Standard", 1, 5, 20)),
                settings, Metric.Njs, true) == 5, "ascending sort value takes chart minimum");
            Assert(FilterEngine.SortValue(
                Song("multi", "Multi", 100, 100,
                    Chart("Standard", 1, 30, 20), Chart("Standard", 1, 5, 20)),
                settings, Metric.Njs, false) == 30, "descending sort value takes chart maximum");

            IList<SongMetrics> ascending = FilterEngine.Sort(
                new[] { unknown, high, tieB, noChart, low, tieA }, settings, Metric.Njs, true);
            AssertIds(ascending, "low", "a", "b", "high", "no-chart", "unknown");

            IList<SongMetrics> descending = FilterEngine.Sort(
                new[] { unknown, high, tieB, noChart, low, tieA }, settings, Metric.Njs, false);
            AssertIds(descending, "high", "a", "b", "low", "no-chart", "unknown");

            SongMetrics same = Song("same-id", "Same", 100, 100, Chart("Standard", 1, 20, 20));
            IList<SongMetrics> stable = FilterEngine.Sort(
                new[] { same, same }, settings, Metric.Njs, true);
            Assert(object.ReferenceEquals(stable[0], same) && object.ReferenceEquals(stable[1], same),
                "equal name and id retain input order");

            FilterSettings enabled = EnabledSettings("Standard", new[] { 1 });
            enabled.Njs.Min = 15;
            Assert(FilterEngine.SortValue(low, enabled, Metric.Njs, true) == null,
                "enabled sorting applies the complete chart filter");
            Assert(FilterEngine.SortValue(high, enabled, Metric.Njs, true) == 30,
                "enabled sorting keeps a chart satisfying all conditions");
        }

        private static void TestFormatting()
        {
            Assert(FilterEngine.Format(Metric.Nps, 2.5) == "2.5", "decimal metrics use invariant compact format");
            Assert(FilterEngine.Format(Metric.Notes, 120) == "120", "integer notes use integer format");
            Assert(FilterEngine.Format(Metric.Nps, null) == "—", "unknown values have an explicit marker");
        }

        private static void TestPreviewContract()
        {
            FilterPreview preview = new FilterPreview { State = FilterPreviewState.Loading };
            Assert(!preview.CanApply, "loading preview cannot apply");
            preview.State = FilterPreviewState.Ready;
            Assert(preview.CanApply, "ready preview can apply");
            preview.State = FilterPreviewState.Partial;
            Assert(preview.CanApply, "partial preview can apply");
            preview.State = FilterPreviewState.Error;
            Assert(!preview.CanApply, "error preview cannot apply");
        }

        private static FilterSettings EnabledSettings(string mode, int[] difficulties)
        {
            return new FilterSettings
            {
                Enabled = true,
                Mode = mode,
                Difficulties = difficulties
            };
        }

        private static SongMetrics Song(
            string id,
            string name,
            double? duration,
            double? bpm,
            params ChartMetrics[] charts)
        {
            return new SongMetrics
            {
                Id = id,
                Name = name,
                Duration = duration,
                Bpm = bpm,
                Charts = charts
            };
        }

        private static ChartMetrics Chart(string mode, int difficulty, double? njs, int? notes)
        {
            return new ChartMetrics
            {
                Mode = mode,
                Difficulty = difficulty,
                Njs = njs,
                Notes = notes
            };
        }

        private static void AssertIds(IList<SongMetrics> songs, params string[] expected)
        {
            Assert(songs.Count == expected.Length, "sorted result has expected length");
            for (int i = 0; i < expected.Length; i++)
            {
                Assert(songs[i] != null && songs[i].Id == expected[i],
                    "sorted result has expected stable order at index " + i);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Assertion failed: " + message);
            }
        }
    }
}
