using System;
using System.IO;
using NUnit.Framework;

namespace ActivityEnergyCounter.Tests
{
    [TestFixture]
    public sealed class EnergyPipelineTests
    {
        [Test]
        public void AllSampledIntervalsReachHistoryAcrossSongsMidnightAndRestart()
        {
            string root = NewRoot();
            DateTimeOffset now = new DateTimeOffset(2026, 10, 16, 23, 59, 55, TimeSpan.FromHours(8)).ToUniversalTime();
            var zone = TimeZoneInfo.CreateCustomTimeZone("EnergyTestUTC8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
            double accumulated = 0, today = 0;
            try
            {
                using (var history = new EnergyHistoryStore(root, null, () => now, zone))
                {
                    foreach (int duration in new[] { 10, 4 })
                    {
                        var settings = new EnergySettings();
                        var motion = new MotionIntegrator();
                        var accumulator = new EnergyAccumulator(settings);
                        string play = history.BeginPlay("pipeline-" + duration, EnergyAccumulator.ModelVersion, 70, 4, 1);
                        DateTimeOffset anchor = now, previous = now;
                        long sequence = 0;
                        for (int i = 0; i <= 90 * duration; i++)
                        {
                            double t = i / 90.0;
                            now = anchor.AddTicks((long)Math.Round(t * TimeSpan.TicksPerSecond));
                            var head = new MotionPoint(0.1 * Math.Sin(t * 2), 1.7 - 0.1 * (1 - Math.Cos(t * 3)), 0);
                            var frame = new MotionFrame {
                                Timestamp = t, Head = head, HeadTracked = true, LeftTracked = true, RightTracked = true,
                                Left = head + new MotionPoint(-0.3 + 0.2 * Math.Sin(t * 8), -0.5, 0),
                                Right = head + new MotionPoint(0.3, -0.5 + 0.2 * Math.Sin(t * 8), 0)
                            };
                            MotionFeatures features; EnergyTotals delta;
                            if (motion.TryAdvance(frame, false, out features) && accumulator.AddSample(features, out delta))
                                Assert.That(history.AddInterval(play, ++sequence, previous, now, delta), Is.True, "sample " + i + ": " + history.LastError);
                            previous = now;
                        }
                        var snapshot = accumulator.CreateSnapshot("pipeline-" + duration, "Three point", false);
                        accumulated += snapshot.ActiveKiloJoules;
                        history.EndPlay(play, "completed"); history.EndPlay(play, "completed");
                        Assert.That(history.SessionTotals.ActiveKiloJoules, Is.EqualTo(accumulated).Within(1e-9));
                        Assert.That(history.Flush(TimeSpan.FromSeconds(3)), Is.True, history.LastError);
                        // Menu/selection time creates no interval.
                        now = now.AddSeconds(3);
                    }
                    today = history.TodayTotals.ActiveKiloJoules;
                    Assert.That(today, Is.GreaterThan(0).And.LessThan(accumulated));
                }
                using (var restored = new EnergyHistoryStore(root, null, () => now, zone))
                {
                    Assert.That(restored.SessionTotals.ActiveKiloJoules, Is.EqualTo(0));
                    Assert.That(restored.TodayTotals.ActiveKiloJoules, Is.EqualTo(today).Within(1e-9));
                }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Test]
        public void FailedCheckpointRetainsDataAndCanRetryAfterTheFilesystemRecovers()
        {
            string root = NewRoot();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            try
            {
                using (var history = new EnergyHistoryStore(root, null, () => now, TimeZoneInfo.Utc))
                {
                    string play = history.BeginPlay("write-retry", EnergyAccumulator.ModelVersion, 70, 4, 1);
                    Assert.That(history.AddInterval(play, 1, now, now.AddSeconds(0.1),
                        new EnergyTotals { ActiveKiloJoules = 1, GrossKiloJoules = 2, EquivalentSeconds = 0.1, ValidPlayingSeconds = 0.1 }), Is.True);
                    Directory.CreateDirectory(Path.Combine(root, "UserData"));
                    string blocker = Path.Combine(root, "UserData", "ActivityEnergyCounter");
                    File.WriteAllText(blocker, "intentional test of a temporarily unwritable history path");
                    Assert.That(history.Flush(TimeSpan.FromSeconds(3)), Is.False);
                    Assert.That(history.HasUnsavedChanges, Is.True);
                    Assert.That(history.LastError, Is.Not.Null.And.Not.Empty);
                    Assert.That(history.SessionTotals.ActiveKiloJoules, Is.EqualTo(1));
                    File.Delete(blocker);
                    Assert.That(history.Flush(TimeSpan.FromSeconds(3)), Is.True, history.LastError);
                    Assert.That(history.HasUnsavedChanges, Is.False);
                    Assert.That(history.LastError, Is.Null.Or.Empty);
                    Assert.That(File.Exists(Path.Combine(blocker, "history.json")), Is.True);
                }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        private static string NewRoot()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "pipeline-" + Guid.NewGuid().ToString("N"));
        }
    }
}
