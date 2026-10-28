using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using NUnit.Framework;

namespace ActivityEnergyCounter.Tests
{
    [TestFixture]
    public sealed class EnergyHistoryTests
    {
        [Test]
        public void CrossDayTotalsAreSplitByRealDurationAndSessionResetKeepsToday()
        {
            string root = NewRoot();
            DateTimeOffset now = Utc(2026, 1, 1, 23, 59, 50);
            try
            {
                using (EnergyHistoryStore store = new EnergyHistoryStore(root, null, () => now, TimeZoneInfo.Utc))
                {
                    string play = store.BeginPlay("song-cross-day", "model-1", 70, 4, 1);
                    Assert.That(store.AddInterval(play, 0, now, now.AddSeconds(20), Totals(10, 20, 20, 20)), Is.True);
                    Assert.That(store.SessionTotals.ActiveKiloJoules, Is.EqualTo(10).Within(1e-12));
                    Assert.That(store.TodayTotals.ActiveKiloJoules, Is.EqualTo(5).Within(1e-12));

                    now = now.AddSeconds(20);
                    Assert.That(store.TodayTotals.ActiveKiloJoules, Is.EqualTo(5).Within(1e-12));
                    string oldSession = store.SessionId;
                    store.StartNewSession();
                    Assert.That(store.SessionId, Is.Not.EqualTo(oldSession));
                    Assert.That(store.SessionTotals.ActiveKiloJoules, Is.EqualTo(0));
                    Assert.That(store.TodayTotals.ActiveKiloJoules, Is.EqualTo(5).Within(1e-12));
                }
            }
            finally { DeleteRoot(root); }
        }

        [Test]
        public void ReloadStartsFreshSessionAndPreservesPreviousDailyTotals()
        {
            string root = NewRoot();
            DateTimeOffset now = Utc(2026, 2, 3, 12, 0, 0);
            try
            {
                string firstSession;
                using (EnergyHistoryStore store = new EnergyHistoryStore(root, null, () => now, TimeZoneInfo.Utc))
                {
                    firstSession = store.SessionId;
                    string play = store.BeginPlay("song-reload", "model-1", 72, 4, 1);
                    Assert.That(store.AddInterval(play, 1, now, now.AddSeconds(2), Totals(1, 2, 3, 2)), Is.True);
                    store.EndPlay(play, "completed");
                    Assert.That(store.Flush(TimeSpan.FromSeconds(3)), Is.True);
                }

                using (EnergyHistoryStore restored = new EnergyHistoryStore(root, null, () => now, TimeZoneInfo.Utc))
                {
                    Assert.That(restored.SessionId, Is.Not.EqualTo(firstSession));
                    Assert.That(restored.SessionTotals.ActiveKiloJoules, Is.EqualTo(0));
                    Assert.That(restored.TodayTotals.ActiveKiloJoules, Is.EqualTo(1).Within(1e-12));
                    Assert.That(File.ReadAllText(HistoryPath(root)), Does.Contain("model-1"));
                }
            }
            finally { DeleteRoot(root); }
        }

        [Test]
        public void DuplicateOldAndInvalidIntervalsAreRejectedWithoutChangingTotals()
        {
            string root = NewRoot();
            DateTimeOffset now = Utc(2026, 3, 4, 12, 0, 0);
            try
            {
                using (EnergyHistoryStore store = new EnergyHistoryStore(root, null, () => now, TimeZoneInfo.Utc))
                {
                    string play = store.BeginPlay("song-sequence", "model-1", 70, 4, 1);
                    EnergyTotals delta = Totals(2, 3, 4, 1);
                    Assert.That(store.AddInterval(play, 3, now, now.AddSeconds(1), delta), Is.True);
                    Assert.That(store.AddInterval(play, 3, now.AddSeconds(1), now.AddSeconds(2), delta), Is.False);
                    Assert.That(store.AddInterval(play, 2, now.AddSeconds(1), now.AddSeconds(2), delta), Is.False);
                    Assert.That(store.AddInterval(play, 4, now.AddSeconds(0.5), now.AddSeconds(2), delta), Is.False);
                    Assert.That(store.AddInterval(play, 5, now.AddSeconds(2), now.AddSeconds(3), Totals(4, 3, 1, 1)), Is.False);
                    Assert.That(store.SessionTotals.ActiveKiloJoules, Is.EqualTo(2).Within(1e-12));
                    Assert.That(store.SessionTotals.GrossKiloJoules, Is.EqualTo(3).Within(1e-12));
                }
            }
            finally { DeleteRoot(root); }
        }

        [Test]
        public void EndingAPlayIsIdempotentAndPersistedParametersAreUnchanged()
        {
            string root = NewRoot();
            DateTimeOffset now = Utc(2026, 4, 5, 12, 0, 0);
            try
            {
                using (EnergyHistoryStore store = new EnergyHistoryStore(root, null, () => now, TimeZoneInfo.Utc))
                {
                    string play = store.BeginPlay("song-params", "model-original", 81.5, 5.25, 1.15);
                    Assert.That(store.AddInterval(play, 0, now, now.AddSeconds(1), Totals(1, 2, 3, 1)), Is.True);
                    store.EndPlay(play, "completed");
                    store.EndPlay(play, "failed");
                    Assert.That(store.Flush(TimeSpan.FromSeconds(3)), Is.True);
                }
                string json = File.ReadAllText(HistoryPath(root));
                Assert.That(json, Does.Contain("model-original"));
                Assert.That(json, Does.Contain("81.5"));
                Assert.That(json, Does.Contain("5.25"));
                Assert.That(json, Does.Contain("1.15"));
                Assert.That(json, Does.Contain("completed"));
                Assert.That(json, Does.Not.Contain("\"Status\": \"failed\""));
            }
            finally { DeleteRoot(root); }
        }

        [Test]
        public void CorruptPrimaryRecoversLastGoodBackupAndUnknownSchemaStaysReadOnly()
        {
            string recoveredRoot = NewRoot();
            DateTimeOffset now = Utc(2026, 5, 6, 12, 0, 0);
            try
            {
                using (EnergyHistoryStore store = new EnergyHistoryStore(recoveredRoot, null, () => now, TimeZoneInfo.Utc))
                {
                    string play = store.BeginPlay("song-recovery", "model-1", 70, 4, 1);
                    Assert.That(store.AddInterval(play, 0, now, now.AddSeconds(1), Totals(7, 8, 9, 1)), Is.True);
                    Assert.That(store.Flush(TimeSpan.FromSeconds(3)), Is.True);
                    Assert.That(store.AddInterval(play, 1, now.AddSeconds(1), now.AddSeconds(2), Totals(1, 1, 1, 1)), Is.True);
                    Assert.That(store.Flush(TimeSpan.FromSeconds(3)), Is.True);
                }
                string path = HistoryPath(recoveredRoot);
                string backup = path + ".bak";
                Assert.That(File.Exists(backup), Is.True);
                File.WriteAllText(path, "{ broken primary");

                List<string> loadReports = new List<string>();
                using (EnergyHistoryStore restored = new EnergyHistoryStore(recoveredRoot,
                    (category, error) => loadReports.Add(category + ": " + error), () => now, TimeZoneInfo.Utc))
                {
                    Assert.That(restored.LastError, Is.Not.Null.And.Not.Empty);
                    Assert.That(restored.IsAvailable, Is.True, String.Join(" | ", loadReports));
                    Assert.That(restored.TodayTotals, Is.Not.Null, String.Join(" | ", loadReports));
                    Assert.That(restored.TodayTotals.ActiveKiloJoules, Is.EqualTo(7).Within(1e-12));
                    Assert.That(restored.Flush(TimeSpan.FromSeconds(3)), Is.True);
                }
                Assert.That(Directory.GetFiles(Path.GetDirectoryName(path), "history.json.corrupt-*.json"), Is.Not.Empty);
            }
            finally { DeleteRoot(recoveredRoot); }

            string blockedRoot = NewRoot();
            try
            {
                string path = HistoryPath(blockedRoot);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "{\"SchemaVersion\":999,\"GameBuild\":\"future\",\"Sessions\":{}}");
                string original = File.ReadAllText(path);
                using (EnergyHistoryStore blocked = new EnergyHistoryStore(blockedRoot, null, () => now, TimeZoneInfo.Utc))
                {
                    Assert.That(blocked.IsAvailable, Is.False);
                    Assert.That(blocked.LastError, Is.Not.Null.And.Not.Empty);
                    Assert.That(blocked.SessionTotals, Is.Null);
                    Assert.That(blocked.TodayTotals, Is.Null);
                    Assert.That(blocked.Flush(TimeSpan.FromMilliseconds(50)), Is.False);
                }
                Assert.That(File.ReadAllText(path), Is.EqualTo(original));
            }
            finally { DeleteRoot(blockedRoot); }

            string futureRoot = NewRoot();
            try
            {
                string path = HistoryPath(futureRoot);
                using (EnergyHistoryStore store = new EnergyHistoryStore(futureRoot, null, () => now, TimeZoneInfo.Utc))
                {
                    string play = store.BeginPlay("song-future", "model-1", 70, 4, 1);
                    Assert.That(store.AddInterval(play, 0, now, now.AddSeconds(1), Totals(2, 3, 4, 1)), Is.True);
                    Assert.That(store.Flush(TimeSpan.FromSeconds(3)), Is.True);
                    Assert.That(store.AddInterval(play, 1, now.AddSeconds(1), now.AddSeconds(2), Totals(1, 1, 1, 1)), Is.True);
                    Assert.That(store.Flush(TimeSpan.FromSeconds(3)), Is.True);
                }
                File.WriteAllText(path, "{\"SchemaVersion\":999,\"GameBuild\":\"future\",\"Sessions\":{}}");
                using (EnergyHistoryStore blocked = new EnergyHistoryStore(futureRoot, null, () => now, TimeZoneInfo.Utc))
                {
                    Assert.That(blocked.IsAvailable, Is.False);
                    Assert.That(blocked.SessionTotals, Is.Null);
                    Assert.That(blocked.TodayTotals, Is.Null);
                    Assert.That(blocked.Flush(TimeSpan.FromMilliseconds(50)), Is.False);
                }
                Assert.That(File.ReadAllText(path), Does.Contain("\"SchemaVersion\":999"));
                Assert.That(File.Exists(path + ".bak"), Is.True);
                Assert.That(File.ReadAllText(path + ".bak"), Does.Contain("\"SchemaVersion\": 1"));
            }
            finally { DeleteRoot(futureRoot); }
        }

        [Test]
        public void InvalidTotalsAndParametersAreRejected()
        {
            string root = NewRoot();
            DateTimeOffset now = Utc(2026, 6, 7, 12, 0, 0);
            try
            {
                using (EnergyHistoryStore store = new EnergyHistoryStore(root, null, () => now, TimeZoneInfo.Utc))
                {
                    Assert.Throws<ArgumentOutOfRangeException>(() => store.BeginPlay("song", "model", double.NaN, 4, 1));
                    string play = store.BeginPlay("song", "model", 70, 4, 1);
                    Assert.That(store.AddInterval(play, 0, now, now.AddSeconds(1), Totals(2, 1, 0, 0)), Is.False);
                    Assert.That(store.AddInterval(play, 0, now, now.AddSeconds(1), Totals(double.PositiveInfinity, 2, 0, 0)), Is.False);
                    Assert.That(store.AddInterval(play, 0, now, now.AddSeconds(1), Totals(0, 0, 0, 2)), Is.False);
                    Assert.That(store.SessionTotals.ActiveKiloJoules, Is.EqualTo(0));
                }
            }
            finally { DeleteRoot(root); }
        }

        [Test]
        public void EndTimesNeverPrecedePlayStartOrLastAcceptedInterval()
        {
            string root = NewRoot();
            DateTimeOffset now = Utc(2026, 7, 8, 12, 0, 0);
            string started = PersistedUtc(now);
            try
            {
                using (EnergyHistoryStore store = new EnergyHistoryStore(root, null, () => now, TimeZoneInfo.Utc))
                {
                    string play = store.BeginPlay("song-clock", "model-1", 70, 4, 1);
                    now = now.AddHours(-1);
                    store.EndPlay(play, "quit");
                    Assert.That(store.Flush(TimeSpan.FromSeconds(3)), Is.True);
                }
                string json = File.ReadAllText(HistoryPath(root));
                Assert.That(json, Does.Contain("\"StartedUtc\": \"" + started + "\""));
                Assert.That(json, Does.Contain("\"EndedUtc\": \"" + started + "\""));
            }
            finally { DeleteRoot(root); }

            string interruptedRoot = NewRoot();
            now = Utc(2026, 7, 8, 12, 0, 0);
            string lastEnd = PersistedUtc(now.AddSeconds(2));
            try
            {
                using (EnergyHistoryStore store = new EnergyHistoryStore(interruptedRoot, null, () => now, TimeZoneInfo.Utc))
                {
                    string play = store.BeginPlay("song-interrupted", "model-1", 70, 4, 1);
                    Assert.That(store.AddInterval(play, 0, now, now.AddSeconds(2), Totals(1, 2, 3, 2)), Is.True);
                    Assert.That(store.Flush(TimeSpan.FromSeconds(3)), Is.True);
                }
                now = now.AddHours(-1);
                using (EnergyHistoryStore restored = new EnergyHistoryStore(interruptedRoot, null, () => now, TimeZoneInfo.Utc))
                {
                    Assert.That(restored.IsAvailable, Is.True);
                    Assert.That(restored.Flush(TimeSpan.FromSeconds(3)), Is.True);
                }
                string json = File.ReadAllText(HistoryPath(interruptedRoot));
                Assert.That(json, Does.Contain("\"Status\": \"interrupted\""));
                Assert.That(json, Does.Contain("\"EndedUtc\": \"" + lastEnd + "\""));
            }
            finally { DeleteRoot(interruptedRoot); }
        }

        [Test]
        public void MissingRequiredJsonFieldMakesHistoryUnavailableInsteadOfUsingDefaults()
        {
            string root = NewRoot();
            string sessionId = "00000000000000000000000000000001";
            try
            {
                string path = HistoryPath(root);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "{\"SchemaVersion\":1,\"GameBuild\":\"1.45.2_28848\",\"Sessions\":{\""
                    + sessionId + "\":{\"SessionId\":\"" + sessionId + "\",\"Plays\":{}}}}");
                using (EnergyHistoryStore store = new EnergyHistoryStore(root, null,
                    () => Utc(2026, 8, 9, 12, 0, 0), TimeZoneInfo.Utc))
                {
                    Assert.That(store.IsAvailable, Is.False);
                    Assert.That(store.SessionTotals, Is.Null);
                    Assert.That(store.TodayTotals, Is.Null);
                    Assert.That(store.LastError, Is.Not.Null.And.Not.Empty);
                }
            }
            finally { DeleteRoot(root); }
        }

        private static EnergyTotals Totals(double active, double gross, double equivalent, double playing)
        {
            return new EnergyTotals
            {
                ActiveKiloJoules = active,
                GrossKiloJoules = gross,
                EquivalentSeconds = equivalent,
                ValidPlayingSeconds = playing
            };
        }

        private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute, int second)
        {
            return new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero);
        }

        private static string PersistedUtc(DateTimeOffset value)
        {
            return value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
        }

        private static string NewRoot()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", Guid.NewGuid().ToString("N").Substring(0, 12));
        }

        private static string HistoryPath(string root)
        {
            return Path.Combine(root, "UserData", "ActivityEnergyCounter", "history.json");
        }

        private static void DeleteRoot(string root)
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
