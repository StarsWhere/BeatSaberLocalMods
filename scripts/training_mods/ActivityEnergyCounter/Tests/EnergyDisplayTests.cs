using NUnit.Framework;
using System;

namespace ActivityEnergyCounter.Tests
{
    [TestFixture]
    public sealed class EnergyDisplayTests
    {
        [Test]
        public void DefaultsShowOnlyTodayInHudAndResults()
        {
            var settings = new EnergySettings();
            EnergySnapshot song = Song(1, 2, 60);
            EnergyTotals session = Totals(3, 4, 120);
            EnergyTotals today = Totals(5, 6, 180);
            Assert.That(settings.HudPrimaryScope, Is.EqualTo(EnergyScope.Today));
            Assert.That(EnergyDisplay.FormatHud(song, session, today, settings),
                Is.EqualTo("Today: 5.0 kJ · estimated"));
            Assert.That(EnergyDisplay.FormatResults(song, session, today, settings),
                Is.EqualTo("Today: 5.0 kJ · estimated · 03:00"));
        }

        private static EnergyTotals Totals(double active, double gross, double seconds)
        {
            return new EnergyTotals
            {
                ActiveKiloJoules = active,
                GrossKiloJoules = gross,
                EquivalentSeconds = seconds,
                ValidPlayingSeconds = seconds
            };
        }

        private static EnergySnapshot Song(double active, double gross, double seconds)
        {
            return new EnergySnapshot
            {
                BeatmapKey = "test-song",
                ActiveKiloJoules = active,
                GrossKiloJoules = gross,
                EquivalentSeconds = seconds,
                ValidPlayingSeconds = seconds
            };
        }

        [Test]
        public void HudCanShowZeroOneOrThreeRows()
        {
            EnergySettings settings = new EnergySettings { HudShowSong = false, HudShowSession = false, HudShowToday = false };
            EnergySnapshot song = Song(1, 2, 60);
            EnergyTotals session = Totals(3, 4, 120);
            EnergyTotals today = Totals(5, 6, 180);

            Assert.That(EnergyDisplay.FormatHud(song, session, today, settings), Is.Empty);

            settings.HudShowSession = true;
            string one = EnergyDisplay.FormatHud(song, session, today, settings);
            Assert.That(one.Split(new[] { '\n' }, StringSplitOptions.None), Has.Length.EqualTo(1));
            Assert.That(one, Does.Contain("Session:"));

            settings.HudShowSong = true;
            settings.HudShowToday = true;
            string three = EnergyDisplay.FormatHud(song, session, today, settings);
            Assert.That(three.Split(new[] { '\n' }, StringSplitOptions.None), Has.Length.EqualTo(3));
        }

        [Test]
        public void HudPrimaryScopeChangesOnlyEnabledRowOrder()
        {
            EnergySettings settings = new EnergySettings
            {
                HudPrimaryScope = EnergyScope.Today,
                HudShowSong = true,
                HudShowSession = true,
                HudShowToday = true,
                ShowEstimate = false
            };
            string output = EnergyDisplay.FormatHud(
                Song(1, 2, 1),
                Totals(3, 4, 1),
                Totals(5, 6, 1),
                settings);

            Assert.That(output.IndexOf("Today:", StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
            Assert.That(output.IndexOf("Song:", StringComparison.Ordinal), Is.GreaterThan(output.IndexOf("Today:", StringComparison.Ordinal)));
            Assert.That(output.IndexOf("Session:", StringComparison.Ordinal), Is.GreaterThan(output.IndexOf("Song:", StringComparison.Ordinal)));

            settings.HudShowToday = false;
            string withoutPrimary = EnergyDisplay.FormatHud(
                Song(1, 2, 1), Totals(3, 4, 1), Totals(5, 6, 1), settings);
            Assert.That(withoutPrimary.IndexOf("Song:", StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
            Assert.That(withoutPrimary.IndexOf("Session:", StringComparison.Ordinal), Is.GreaterThan(withoutPrimary.IndexOf("Song:", StringComparison.Ordinal)));
        }

        [Test]
        public void EstimateMarkerAppearsOnlyOnFirstVisibleRow()
        {
            EnergySettings settings = new EnergySettings
            {
                HudShowSong = true,
                HudShowSession = true,
                HudShowToday = true,
                HudPrimaryScope = EnergyScope.Today,
                ShowEstimate = true
            };
            string output = EnergyDisplay.FormatHud(
                Song(1, 2, 1), Totals(3, 4, 1), Totals(5, 6, 1), settings);

            Assert.That(output.Split(new[] { "estimated" }, StringSplitOptions.None), Has.Length.EqualTo(2));
            Assert.That(output, Does.StartWith("Today: 5.0 kJ · estimated"));
            Assert.That(output, Does.Contain("Song: 1.0 kJ"));
            Assert.That(output, Does.Contain("Session: 3.0 kJ"));
        }

        [Test]
        public void UnitModePrecisionDurationAndEstimateAreApplied()
        {
            EnergySettings settings = new EnergySettings
            {
                Mode = EnergyMode.GrossTotal,
                Unit = EnergyUnit.Kilocalories,
                DecimalPlaces = 2,
                HudShowSong = true,
                HudShowSession = false,
                HudShowToday = false,
                HudShowDuration = true,
                ResultsShowSong = true,
                ShowLabels = true,
                ShowEstimate = true
            };
            string output = EnergyDisplay.FormatHud(Song(4.184, 8.368, 61), null, null, settings);
            Assert.That(output, Does.Contain("Song total: 2.00 kcal"));
            Assert.That(output, Does.Contain("estimated"));
            Assert.That(output, Does.Contain("· 01:01"));

            settings.ShowLabels = false;
            settings.ShowEstimate = false;
            string compact = EnergyDisplay.FormatResults(Song(4.184, 8.368, 3661), null, null, settings);
            Assert.That(compact, Is.EqualTo("2.00 kcal · 1:01:01"));
        }

        [Test]
        public void DurationUsesMinuteAndHourForms()
        {
            Assert.That(EnergyDisplay.FormatDuration(0), Is.EqualTo("00:00"));
            Assert.That(EnergyDisplay.FormatDuration(61), Is.EqualTo("01:01"));
            Assert.That(EnergyDisplay.FormatDuration(3661), Is.EqualTo("1:01:01"));
        }

        [Test]
        public void SettingsValidateNewDisplayBoundsAndEnums()
        {
            EnergySettings settings = new EnergySettings
            {
                Mode = (EnergyMode)99,
                Unit = (EnergyUnit)99,
                HudPrimaryScope = (EnergyScope)99,
                DecimalPlaces = 99,
                HudFontScale = double.PositiveInfinity,
                ResultsFontScale = double.NaN
            };
            settings.Validate();

            Assert.That(settings.Mode, Is.EqualTo(EnergyMode.NetActive));
            Assert.That(settings.Unit, Is.EqualTo(EnergyUnit.KiloJoules));
            Assert.That(settings.HudPrimaryScope, Is.EqualTo(EnergyScope.Today));
            Assert.That(settings.DecimalPlaces, Is.EqualTo(2));
            Assert.That(settings.HudFontScale, Is.EqualTo(1.0));
            Assert.That(settings.ResultsFontScale, Is.EqualTo(1.0));

            settings.DecimalPlaces = -1;
            settings.HudFontScale = 0.1;
            settings.ResultsFontScale = 2;
            settings.Validate();
            Assert.That(settings.DecimalPlaces, Is.EqualTo(0));
            Assert.That(settings.HudFontScale, Is.EqualTo(0.5));
            Assert.That(settings.ResultsFontScale, Is.EqualTo(1.5));
            settings.ResultsFontScale = 0;
            settings.Validate();
            Assert.That(settings.ResultsFontScale, Is.EqualTo(0.75));
        }

        [Test]
        public void SummaryUsesResultsDurationAndCanHideLabels()
        {
            EnergySettings settings = new EnergySettings { ResultsShowDuration = true, ShowLabels = true, ShowEstimate = false };
            string labelled = EnergyDisplay.FormatSummary(Totals(4.184, 8.368, 61), settings);
            Assert.That(labelled, Is.EqualTo("4.2 kJ · 01:01"));

            settings.Unit = EnergyUnit.Kilocalories;
            settings.DecimalPlaces = 2;
            settings.ShowLabels = false;
            settings.ResultsShowDuration = false;
            string compact = EnergyDisplay.FormatSummary(Totals(4.184, 8.368, 61), settings);
            Assert.That(compact, Is.EqualTo("1.00 kcal"));
        }
    }
}
