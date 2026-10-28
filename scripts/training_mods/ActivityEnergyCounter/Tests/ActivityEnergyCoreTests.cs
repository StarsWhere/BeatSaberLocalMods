using System;
using NUnit.Framework;

namespace ActivityEnergyCounter.Tests
{
    [TestFixture]
    public sealed class ActivityEnergyCoreTests
    {
        private static MotionFrame Frame(double t, MotionPoint head, MotionPoint left, MotionPoint right)
        {
            return new MotionFrame { Timestamp = t, Head = head, Left = left, Right = right,
                HeadTracked = true, LeftTracked = true, RightTracked = true };
        }
        private static MotionFeatures Arms(double speed, double dt = 0.2)
        {
            return new MotionFeatures { ArmSpeedMps = speed, ElapsedSeconds = dt, LeftTracked = true, RightTracked = true };
        }
        private static EnergySnapshot AtSpeed(double speed)
        {
            var accumulator = new EnergyAccumulator(new EnergySettings());
            EnergyTotals delta; accumulator.AddSample(Arms(speed), out delta);
            return accumulator.CreateSnapshot("test", "Hands only", false);
        }

        [Test]
        public void StationaryHasZeroActivityAndOnlyPlayingTimeRestingEnergy()
        {
            EnergySnapshot result = AtSpeed(0);
            Assert.That(result.ActiveKiloJoules, Is.EqualTo(0));
            Assert.That(result.GrossKiloJoules, Is.EqualTo(70 * 0.2 / 3600 * 4.184).Within(1e-12));
            Assert.That(result.ValidPlayingSeconds, Is.EqualTo(0.2));
        }
        [Test]
        public void ReferenceMotionUsesNetMetAndAddsRestExactlyOnce()
        {
            EnergySnapshot result = AtSpeed(4);
            double expected = 70.0 * 0.2 / 3600 * 3.45 * 4.184;
            Assert.That(result.ActiveKiloJoules, Is.EqualTo(expected).Within(1e-12));
            Assert.That(result.GrossKiloJoules - result.ActiveKiloJoules,
                Is.EqualTo(70.0 * 0.2 / 3600 * 4.184).Within(1e-12));
        }
        [Test]
        public void HigherSpeedsStillDifferBeyondTheOldSixMetersPerSecondCap()
        {
            Assert.That(AtSpeed(12).ActiveKiloJoules, Is.GreaterThan(AtSpeed(6).ActiveKiloJoules));
            Assert.That(AtSpeed(40).EquivalentSeconds, Is.LessThanOrEqualTo(0.2 * EnergyAccumulator.MaximumEquivalentIntensity));
        }
        [Test]
        public void BodyTranslationIsCountedOnceAndCreatesNoRelativeArmMovement()
        {
            var motion = new MotionIntegrator(); MotionFeatures features;
            motion.TryAdvance(Frame(0, new MotionPoint(0, 1.7, 0), new MotionPoint(-0.3, 1, 0), new MotionPoint(0.3, 1, 0)), false, out features);
            motion.TryAdvance(Frame(0.1, new MotionPoint(0.1, 1.7, 0), new MotionPoint(-0.2, 1, 0), new MotionPoint(0.4, 1, 0)), false, out features);
            Assert.That(features.ArmSpeedMps, Is.LessThan(1e-12));
            Assert.That(features.BodyHorizontalSpeedMps, Is.GreaterThan(0.9));
            Assert.That(features.BodyVerticalSpeedMps, Is.EqualTo(0));
        }
        [Test]
        public void MovingOnlyTheHeadDoesNotInventArmMotion()
        {
            var motion = new MotionIntegrator(); MotionFeatures features;
            motion.TryAdvance(Frame(0, new MotionPoint(0, 1.7, 0), new MotionPoint(-0.3, 1, 0), new MotionPoint(0.3, 1, 0)), false, out features);
            motion.TryAdvance(Frame(0.1, new MotionPoint(0, 1.6, 0), new MotionPoint(-0.3, 1, 0), new MotionPoint(0.3, 1, 0)), false, out features);
            Assert.That(features.ArmSpeedMps, Is.EqualTo(0));
            Assert.That(features.BodyVerticalSpeedMps, Is.LessThan(-0.9));
        }
        [Test]
        public void StationaryHeadPreservesIndependentHandMotion()
        {
            var motion = new MotionIntegrator(); MotionFeatures features;
            motion.TryAdvance(Frame(0, new MotionPoint(0, 1.7, 0), new MotionPoint(0, 1, 0), new MotionPoint(0, 1, 0)), false, out features);
            Assert.That(motion.TryAdvance(Frame(0.1, new MotionPoint(0, 1.7, 0), new MotionPoint(0.2, 1, 0), new MotionPoint(0, 1.2, 0)), false, out features), Is.True);
            Assert.That(features.ArmSpeedMps, Is.GreaterThan(3.9));
            Assert.That(features.BodyHorizontalSpeedMps, Is.EqualTo(0));
        }
        [Test]
        public void HeadLossFallsBackToHandsAndRecoveryOnlyRebaselinesThatNode()
        {
            var motion = new MotionIntegrator(); MotionFeatures features;
            MotionFrame frame = Frame(0, new MotionPoint(0, 1.7, 0), new MotionPoint(), new MotionPoint());
            motion.TryAdvance(frame, false, out features);
            frame.Timestamp = 0.1; frame.HeadTracked = false; frame.Left = new MotionPoint(0.1, 0, 0);
            Assert.That(motion.TryAdvance(frame, false, out features), Is.True);
            Assert.That(features.TrackingMode, Is.EqualTo("Hands only"));
            frame.Timestamp = 0.2; frame.HeadTracked = true; frame.Head = new MotionPoint(4, 1.7, 0); frame.Left = new MotionPoint(0.2, 0, 0);
            Assert.That(motion.TryAdvance(frame, false, out features), Is.True);
            Assert.That(features.HeadTracked, Is.False);
            Assert.That(features.ArmSpeedMps, Is.LessThan(2));
        }
        [Test]
        public void OneLostOrJumpingHandDoesNotDiscardOtherValidTracking()
        {
            var motion = new MotionIntegrator(); MotionFeatures features;
            motion.TryAdvance(Frame(0, new MotionPoint(), new MotionPoint(), new MotionPoint()), false, out features);
            MotionFrame frame = Frame(0.1, new MotionPoint(), new MotionPoint(10, 0, 0), new MotionPoint(0.1, 0, 0));
            Assert.That(motion.TryAdvance(frame, false, out features), Is.True);
            Assert.That(features.LeftTracked, Is.False);
            Assert.That(features.RightTracked, Is.True);
            Assert.That(features.ArmSpeedMps, Is.InRange(0.9, 1.1));
        }
        [Test]
        public void PauseLongGapAndBackwardTimeCannotChargeTheMissingInterval()
        {
            var motion = new MotionIntegrator(); MotionFeatures features;
            MotionFrame frame = Frame(0, new MotionPoint(), new MotionPoint(), new MotionPoint());
            motion.TryAdvance(frame, false, out features);
            frame.Timestamp = 1;
            Assert.That(motion.TryAdvance(frame, false, out features), Is.False);
            frame.Timestamp = 0.5;
            Assert.That(motion.TryAdvance(frame, false, out features), Is.False);
            frame.Timestamp = 0.6;
            Assert.That(motion.TryAdvance(frame, true, out features), Is.False);
            frame.Timestamp = 0.7;
            Assert.That(motion.TryAdvance(frame, false, out features), Is.False);
            frame.Timestamp = 0.8;
            Assert.That(motion.TryAdvance(frame, false, out features), Is.True);
            Assert.That(features.ElapsedSeconds, Is.EqualTo(0.1).Within(1e-12));
        }
        [Test]
        public void SmallStationaryTrackingNoiseDoesNotAccumulateActivityAtDifferentRates()
        {
            foreach (int rate in new[] { 30, 60, 90, 144 })
            {
                var motion = new MotionIntegrator(); var accumulator = new EnergyAccumulator(new EnergySettings());
                for (int i = 0; i <= rate * 3; i++)
                {
                    double offset = i % 2 == 0 ? 0.0005 : -0.0005;
                    MotionFeatures features; EnergyTotals delta;
                    if (motion.TryAdvance(Frame((double)i / rate, new MotionPoint(offset, 1.7, 0),
                        new MotionPoint(offset, 1, 0), new MotionPoint(-offset, 1, 0)), false, out features))
                        accumulator.AddSample(features, out delta);
                }
                Assert.That(accumulator.CreateSnapshot("noise", "", false).ActiveKiloJoules, Is.EqualTo(0).Within(1e-12), "rate " + rate);
            }
        }
        [Test]
        public void TimeBasedFilteringGivesSimilarEnergyAcrossRenderRates()
        {
            double expected = 0;
            foreach (int rate in new[] { 30, 60, 90, 144 })
            {
                var motion = new MotionIntegrator(); var accumulator = new EnergyAccumulator(new EnergySettings());
                for (int i = 0; i <= rate * 10; i++)
                {
                    double t = (double)i / rate; MotionFeatures features; EnergyTotals delta;
                    if (motion.TryAdvance(Frame(t, new MotionPoint(0, 1.7, 0),
                        new MotionPoint(2 * t, 1, 0), new MotionPoint(0, 1 + 2 * t, 0)), false, out features))
                        accumulator.AddSample(features, out delta);
                }
                double energy = accumulator.CreateSnapshot("rate", "", false).ActiveKiloJoules;
                if (expected == 0) expected = energy;
                Assert.That(energy, Is.EqualTo(expected).Within(expected * 0.005), "rate " + rate);
            }
        }
        [Test]
        public void DownAndUpBodyMovementBothContributeWithoutHandMotion()
        {
            var accumulator = new EnergyAccumulator(new EnergySettings()); EnergyTotals down, up;
            var features = new MotionFeatures { HeadTracked = true, BodyVerticalSpeedMps = -0.3, ElapsedSeconds = 0.1 };
            Assert.That(accumulator.AddSample(features, out down), Is.True);
            features.BodyVerticalSpeedMps = 0.3;
            Assert.That(accumulator.AddSample(features, out up), Is.True);
            Assert.That(down.ActiveKiloJoules, Is.GreaterThan(0));
            Assert.That(up.ActiveKiloJoules, Is.GreaterThan(down.ActiveKiloJoules));
        }
        [Test]
        public void NonfiniteAndInvalidIntervalsAreRejectedWithoutChangingTotals()
        {
            var accumulator = new EnergyAccumulator(new EnergySettings()); EnergyTotals delta;
            Assert.That(accumulator.AddSample(Arms(double.NaN), out delta), Is.False);
            Assert.That(accumulator.AddSample(Arms(-1), out delta), Is.False);
            Assert.That(accumulator.AddSample(Arms(4, 0.3), out delta), Is.False);
            Assert.That(accumulator.AddSample(Arms(4, -0.1), out delta), Is.False);
            Assert.That(accumulator.AddSample(new MotionFeatures { ElapsedSeconds = 0.1 }, out delta), Is.False);
            Assert.That(accumulator.ValidPlayingSeconds, Is.EqualTo(0));
        }
        [Test]
        public void ParametersAreFrozenForEachPlayAndDoNotRevalueExistingEnergy()
        {
            var settings = new EnergySettings(); var first = new EnergyAccumulator(settings); EnergyTotals delta;
            first.AddSample(Arms(4), out delta);
            double initial = first.CreateSnapshot("first", "", false).ActiveKiloJoules;
            settings.BodyMassKg = 140;
            first.AddSample(Arms(4), out delta);
            Assert.That(first.CreateSnapshot("first", "", false).ActiveKiloJoules, Is.EqualTo(initial * 2).Within(1e-12));
            var second = new EnergyAccumulator(settings); second.AddSample(Arms(4), out delta);
            Assert.That(second.CreateSnapshot("second", "", false).ActiveKiloJoules, Is.EqualTo(initial * 2).Within(1e-12));
        }
    }
}

