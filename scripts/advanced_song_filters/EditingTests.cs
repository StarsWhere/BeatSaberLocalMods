using System;
using System.Collections.Generic;
using System.Globalization;

namespace AdvancedSongFilters
{
    public static class EditingTests
    {
        public static int Main()
        {
            try
            {
                TestSessionIsolationAndRevision();
                TestBoundAdjustment();
                TestRangeEditing();
                TestPresets();
                TestSourceEditing();
                Console.WriteLine("AdvancedSongFilters editing tests passed.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.ToString());
                return 1;
            }
        }

        private static void TestSessionIsolationAndRevision()
        {
            FilterSettings applied = new FilterSettings
            {
                Enabled = false,
                Mode = "Standard",
                Difficulties = new[] { 2 }
            };
            applied.Nps.Min = -1;
            FilterEditSession session = new FilterEditSession(applied);
            FilterSettings initial = session.Draft;
            Assert(!initial.Enabled && initial.Mode == "All" && !initial.HasConstraints,
                "disabled applied settings open an unrestricted draft");
            Assert(session.Revision == 0, "new edit session starts at revision zero");
            Assert(applied.Mode == "Standard" && applied.Nps.Min == -1,
                "opening a draft never mutates the applied settings");

            session.SetMode("Standard");
            Assert(session.Revision == 1 && session.Draft.Enabled,
                "a real draft change increments revision and enables constraints");
            FilterSettings detached = session.Draft;
            detached.Mode = "All";
            Assert(session.Draft.Mode == "Standard", "Draft returns a detached clone");

            session.SetMode("Standard");
            Assert(session.Revision == 1, "setting the same normalized value is a no-op");
            session.Reset();
            Assert(session.Revision == 2 && !session.Draft.HasConstraints,
                "reset returns to the unrestricted boundary");

            FilterSettings preset = new FilterSettings
            {
                Enabled = false,
                Mode = "Standard"
            };
            preset.Nps.Min = 1.23456789;
            session.Load(preset);
            Assert(session.Draft.Enabled && session.Draft.Mode == "Standard"
                && session.Draft.Nps.Min == preset.Nps.Min,
                "loading a disabled preset preserves and activates its conditions");
        }

        private static void TestBoundAdjustment()
        {
            FilterEditSession session = new FilterEditSession(new FilterSettings());
            Assert(session.AdjustBound(Metric.Nps, true, 1), "plus seeds an unset lower bound");
            Assert(session.Draft.Nps.Min == 0.5, "NPS uses the approved half-point step");
            Assert(session.CanAdjustBound(Metric.Nps, true, 1), "a valid next bound is adjustable");

            session.SetRange(Metric.Nps, new NumericRange { Min = 2 });
            Assert(session.AdjustBound(Metric.Nps, false, 1),
                "plus seeds an unset upper bound at least at the lower bound");
            Assert(session.Draft.Nps.Max == 2, "an unset upper bound seeds from the lower bound");
            Assert(!session.AdjustBound(Metric.Nps, false, -1),
                "minus cannot invert a range");

            session.SetRange(Metric.Njs, new NumericRange { Min = 0 });
            Assert(session.Draft.Njs.Min.HasValue && session.Draft.Njs.Min.Value == 0,
                "zero remains distinct from an unset bound");
            Assert(!session.CanAdjustBound(Metric.Njs, true, -1),
                "minus is disabled when it would go below zero");
            Assert(session.AdjustBound(Metric.Njs, true, 1), "plus can move a zero bound");

            session.Reset();
            Assert(!session.CanAdjustBound(Metric.Duration, false, -1),
                "minus is disabled for an unset bound");
        }

        private static void TestSourceEditing()
        {
            FilterSettings applied = new FilterSettings { Mode = "Standard" }.Normalize();
            FilterEditSession session = new FilterEditSession(applied);
            session.SetExcludeOfficial(true);
            Assert(session.Revision == 1 && session.Draft.ExcludeOfficial && !applied.ExcludeOfficial,
                "source editing changes only the draft even when filtering was already enabled");
            session.SetExcludeOfficial(true);
            Assert(session.Revision == 1, "repeating the source value does not refresh the draft");
            session.Reset();
            Assert(!session.Draft.ExcludeOfficial && !session.Draft.Enabled,
                "reset clears the source filter");
            session.Load(new FilterSettings { ExcludeOfficial = true });
            Assert(session.Draft.ExcludeOfficial && session.Draft.Enabled,
                "loading a source-only preset activates its draft expression");
        }

        private static void TestRangeEditing()
        {
            FilterSettings settings = new FilterSettings();
            settings.Duration.Min = 65;
            RangeEditSession duration = new RangeEditSession(settings, Metric.Duration);
            Assert(duration.MinimumText == "1:05" && duration.MaximumText == String.Empty,
                "duration bounds use m:ss formatting");

            duration.MinimumText = "1:";
            Assert(duration.Validate() != null, "invalid intermediate input is retained for validation");
            bool threw = false;
            try { duration.GetRange(); }
            catch (Exception) { threw = true; }
            Assert(threw && settings.Duration.Min == 65,
                "invalid text cannot commit or mutate the source settings");

            duration.MinimumText = "1:30";
            duration.MaximumText = "2:00";
            NumericRange committed = duration.GetRange();
            Assert(committed.Min == 90 && committed.Max == 120,
                "valid keyboard text commits as seconds");

            settings.Nps.Min = 1.23456789012345;
            RangeEditSession preciseNps = new RangeEditSession(settings, Metric.Nps);
            Assert(preciseNps.MinimumText == settings.Nps.Min.Value.ToString("R", CultureInfo.InvariantCulture),
                "NPS input formatting preserves high precision");
            Assert(preciseNps.GetRange().Min == settings.Nps.Min,
                "confirming a precise NPS bound is lossless");

            settings.Duration.Min = 65.25;
            RangeEditSession fractionalDuration = new RangeEditSession(settings, Metric.Duration);
            Assert(fractionalDuration.MinimumText == "65.25",
                "fractional duration uses numeric seconds in the input editor");
            Assert(fractionalDuration.GetRange().Min == settings.Duration.Min,
                "confirming a fractional duration is lossless");

            double? parsed;
            Assert(FilterEditing.TryParse(Metric.Notes, String.Empty, out parsed) && !parsed.HasValue,
                "empty numeric input means an unset bound");
            Assert(!FilterEditing.TryParse(Metric.Notes, "1.5", out parsed),
                "notes reject fractional input");
            Assert(!FilterEditing.TryParse(Metric.Bpm, "-1", out parsed),
                "negative numeric input is rejected");
            Assert(FilterEditing.FormatInput(Metric.Duration, 90) == "1:30",
                "duration input formatting is compact");
            Assert(FilterEditing.FormatInput(Metric.Duration, 90.25) == "90.25",
                "fractional duration input formatting stays in seconds");
        }

        private static void TestPresets()
        {
            PresetCollection collection = new PresetCollection();
            FilterPreset first = new FilterPreset
            {
                Id = Guid.NewGuid().ToString("D"),
                Name = "方案 1",
                Settings = new FilterSettings()
            };
            collection.Presets.Add(first);
            Assert(collection.NextName() == "Preset 1", "English defaults do not rename an existing Chinese preset");
            collection.Validate();

            PresetCollection clone = collection.Clone();
            clone.Presets[0].Name = "Changed";
            Assert(collection.Presets[0].Name == "方案 1" && clone.Presets[0].Settings != first.Settings,
                "preset collection clones settings and entries deeply");

            FilterPreset duplicate = first.Clone();
            duplicate.Id = Guid.NewGuid().ToString("D");
            duplicate.Name = "方案 1";
            collection.Presets.Add(duplicate);
            bool rejected = false;
            try { collection.Validate(); }
            catch (System.IO.InvalidDataException) { rejected = true; }
            Assert(rejected, "duplicate preset names are rejected");
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
