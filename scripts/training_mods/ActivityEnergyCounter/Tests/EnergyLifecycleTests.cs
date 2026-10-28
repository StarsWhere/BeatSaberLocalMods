using NUnit.Framework;

namespace ActivityEnergyCounter.Tests
{
    [TestFixture]
    public sealed class EnergyLifecycleTests
    {
        [Test]
        public void StartingAnyGameplayInvalidatesOldResultsAndRejectsLateCallbacks()
        {
            EnergySnapshotStore.Begin();
            long oldOwner = EnergySnapshotStore.CurrentSession;
            var oldResult = new EnergySnapshot { BeatmapKey = "same-chart", ActiveKiloJoules = 10, GrossKiloJoules = 11 };
            EnergySnapshotStore.Publish(oldResult, oldOwner);
            EnergySnapshotStore.Begin(); // Includes modes without an energy recorder.
            EnergySnapshotStore.Publish(oldResult, oldOwner);
            EnergySnapshot result;
            Assert.That(EnergySnapshotStore.TryGet("same-chart", out result), Is.False);
            Assert.That(EnergySnapshotStore.Latest, Is.Null);
            var currentResult = new EnergySnapshot { BeatmapKey = "same-chart", ActiveKiloJoules = 2, GrossKiloJoules = 3 };
            EnergySnapshotStore.Publish(currentResult, EnergySnapshotStore.CurrentSession);
            Assert.That(EnergySnapshotStore.TryGet("same-chart", out result), Is.True);
            Assert.That(result.ActiveKiloJoules, Is.EqualTo(2));
        }

        [Test]
        public void ReadingResultsAgainDoesNotConsumeOrReassignTheSong()
        {
            EnergySnapshotStore.Begin();
            var expected = new EnergySnapshot { BeatmapKey = "result-song" };
            EnergySnapshotStore.Publish(expected, EnergySnapshotStore.CurrentSession);
            EnergySnapshot first, second, unrelated;
            Assert.That(EnergySnapshotStore.TryGet("result-song", out first), Is.True);
            Assert.That(EnergySnapshotStore.TryGet("other-song", out unrelated), Is.False);
            Assert.That(EnergySnapshotStore.TryGet("result-song", out second), Is.True);
            Assert.That(first, Is.SameAs(expected));
            Assert.That(second, Is.SameAs(expected));
        }
    }
}
