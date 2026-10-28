using System;

namespace ActivityEnergyCounter
{
    public struct MotionPoint
    {
        public double X, Y, Z;
        public MotionPoint(double x, double y, double z) { X = x; Y = y; Z = z; }
        public bool IsFinite { get { return Finite(X) && Finite(Y) && Finite(Z); } }
        public double Length { get { return Math.Sqrt(X * X + Y * Y + Z * Z); } }
        public static MotionPoint operator +(MotionPoint a, MotionPoint b) { return new MotionPoint(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static MotionPoint operator -(MotionPoint a, MotionPoint b) { return new MotionPoint(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static MotionPoint operator *(MotionPoint a, double scale) { return new MotionPoint(a.X * scale, a.Y * scale, a.Z * scale); }
        public static double Distance(MotionPoint a, MotionPoint b) { return (a - b).Length; }
        internal static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    }

    public struct MotionFrame
    {
        public MotionPoint Head, Left, Right;
        public bool HeadTracked, LeftTracked, RightTracked;
        public double Timestamp;
    }

    public struct MotionFeatures
    {
        public double ArmSpeedMps, BodyHorizontalSpeedMps, BodyVerticalSpeedMps, ElapsedSeconds;
        public bool HeadTracked, LeftTracked, RightTracked;
        public string TrackingMode
        {
            get
            {
                if (HeadTracked && LeftTracked && RightTracked) return "Three point";
                if (!HeadTracked && LeftTracked && RightTracked) return "Hands only";
                return "Partial tracking";
            }
        }
    }

    // Poses share the physical tracking origin. Gameplay/world transforms can
    // contain room offsets and map animations and are not measurements.
    public sealed class MotionIntegrator
    {
        public const double MinimumSampleSeconds = 0.0005;
        public const double MaximumSampleGapSeconds = 0.25;
        public const double MaximumHandJumpMeters = 1.5;
        private const double FilterSeconds = 0.02;
        private const double NoiseSpeedMps = 0.05;
        private struct Node
        {
            internal bool Ready;
            internal MotionPoint Raw, Filtered;
            internal void Reset() { Ready = false; }
            internal bool Advance(MotionPoint position, bool tracked, double dt, double maxSpeed, out MotionPoint velocity)
            {
                velocity = default(MotionPoint);
                if (!tracked || !position.IsFinite) { Reset(); return false; }
                if (!Ready) { Raw = Filtered = position; Ready = true; return false; }
                double distance = MotionPoint.Distance(position, Raw);
                if (!MotionPoint.Finite(distance) || distance > MaximumHandJumpMeters || distance / dt > maxSpeed)
                {
                    Raw = Filtered = position;
                    return false;
                }
                Raw = position;
                MotionPoint next = Filtered + (position - Filtered) * (1 - Math.Exp(-dt / FilterSeconds));
                velocity = (next - Filtered) * (1 / dt);
                Filtered = next;
                if (velocity.Length < NoiseSpeedMps) velocity = default(MotionPoint);
                return velocity.IsFinite;
            }
        }
        private Node head, left, right;
        private bool hasPrevious;
        private double previousTimestamp;
        public bool HasPrevious { get { return hasPrevious; } }
        public void Reset() { hasPrevious = false; previousTimestamp = 0; head.Reset(); left.Reset(); right.Reset(); }

        public bool TryAdvance(MotionFrame frame, bool paused, out MotionFeatures features)
        {
            features = default(MotionFeatures);
            if (paused || !MotionPoint.Finite(frame.Timestamp)) { Reset(); return false; }
            double elapsed = frame.Timestamp - previousTimestamp;
            bool baseline = !hasPrevious || elapsed < MinimumSampleSeconds || elapsed > MaximumSampleGapSeconds;
            if (baseline) { Reset(); elapsed = MinimumSampleSeconds; }
            hasPrevious = true;
            previousTimestamp = frame.Timestamp;
            MotionPoint headVelocity, leftVelocity, rightVelocity;
            bool haveHead = head.Advance(frame.Head, frame.HeadTracked, elapsed, 5, out headVelocity);
            bool haveLeft = left.Advance(frame.Left, frame.LeftTracked, elapsed, 20, out leftVelocity);
            bool haveRight = right.Advance(frame.Right, frame.RightTracked, elapsed, 20, out rightVelocity);
            if (baseline || (!haveHead && !haveLeft && !haveRight)) return false;
            features.ElapsedSeconds = elapsed;
            features.HeadTracked = haveHead; features.LeftTracked = haveLeft; features.RightTracked = haveRight;
            if (haveHead)
            {
                features.BodyHorizontalSpeedMps = Math.Sqrt(headVelocity.X * headVelocity.X + headVelocity.Z * headVelocity.Z);
                features.BodyVerticalSpeedMps = headVelocity.Y;
            }
            // Charge common translation once through the body channel. Capping
            // relative hand motion at measured hand speed also stops head-only
            // movement from inventing movement of stationary arms.
            if (haveLeft) features.ArmSpeedMps += haveHead
                ? Math.Min(leftVelocity.Length, (leftVelocity - headVelocity).Length) : leftVelocity.Length;
            if (haveRight) features.ArmSpeedMps += haveHead
                ? Math.Min(rightVelocity.Length, (rightVelocity - headVelocity).Length) : rightVelocity.Length;
            return true;
        }
    }

    public sealed class EnergyAccumulator
    {
        public const string ModelVersion = "three-point-1";
        public const double MaximumEquivalentIntensity = 3.0;
        public const double NetActivityMet = 3.45;
        public const double KiloJoulesPerKilocalorie = 4.184;
        // Explicit estimation parameters; these are not measured metabolic costs.
        public const double ReferenceBodySpeedMps = 0.8;
        public const double AscentWeight = 2.0;
        public const double DescentWeight = 1.2;
        private readonly double mass, referenceHandSpeed, factor;
        private readonly EnergyTotals totals = new EnergyTotals();
        public EnergyAccumulator(EnergySettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings.Validate();
            mass = settings.BodyMassKg; referenceHandSpeed = settings.ReferenceHandSpeedMps; factor = settings.CalibrationFactor;
        }
        public double EquivalentSeconds { get { return totals.EquivalentSeconds; } }
        public double ValidPlayingSeconds { get { return totals.ValidPlayingSeconds; } }

        public bool AddSample(MotionFeatures features, out EnergyTotals delta)
        {
            delta = null;
            double dt = features.ElapsedSeconds;
            if (!MotionPoint.Finite(dt) || dt < MotionIntegrator.MinimumSampleSeconds || dt > MotionIntegrator.MaximumSampleGapSeconds
                || !MotionPoint.Finite(features.ArmSpeedMps) || features.ArmSpeedMps < 0
                || !MotionPoint.Finite(features.BodyHorizontalSpeedMps) || features.BodyHorizontalSpeedMps < 0
                || !MotionPoint.Finite(features.BodyVerticalSpeedMps)
                || (!features.HeadTracked && !features.LeftTracked && !features.RightTracked)) return false;
            double vertical = features.BodyVerticalSpeedMps * (features.BodyVerticalSpeedMps >= 0 ? AscentWeight : DescentWeight);
            double body = features.HeadTracked
                ? Math.Sqrt(features.BodyHorizontalSpeedMps * features.BodyHorizontalSpeedMps + vertical * vertical) / ReferenceBodySpeedMps : 0;
            double normalized = features.ArmSpeedMps / referenceHandSpeed + body;
            if (!MotionPoint.Finite(normalized)) return false;
            // Smooth saturation keeps responding above the old 6 m/s hard cap.
            double intensity = normalized <= 1 ? normalized : 1 + 2 * (1 - Math.Exp(-(normalized - 1) / 2));
            double active = mass * intensity * dt / 3600 * NetActivityMet * factor * KiloJoulesPerKilocalorie;
            delta = new EnergyTotals {
                ActiveKiloJoules = active, GrossKiloJoules = active + mass * dt / 3600 * KiloJoulesPerKilocalorie,
                EquivalentSeconds = intensity * dt, ValidPlayingSeconds = dt
            };
            totals.ActiveKiloJoules += delta.ActiveKiloJoules; totals.GrossKiloJoules += delta.GrossKiloJoules;
            totals.EquivalentSeconds += delta.EquivalentSeconds; totals.ValidPlayingSeconds += dt;
            return true;
        }

        public EnergySnapshot CreateSnapshot(string beatmapKey, string trackingMode, bool simulated)
        {
            return new EnergySnapshot {
                BeatmapKey = beatmapKey ?? "", ActiveKiloJoules = totals.ActiveKiloJoules,
                GrossKiloJoules = totals.GrossKiloJoules, EquivalentSeconds = totals.EquivalentSeconds,
                ValidPlayingSeconds = totals.ValidPlayingSeconds, TrackingMode = trackingMode ?? "", IsSimulated = simulated
            };
        }
    }

    public sealed class EnergySnapshot : EnergyTotals
    {
        public string BeatmapKey { get; set; } = "";
        public string TrackingMode { get; set; } = "";
        public bool IsSimulated { get; set; }
    }
}

