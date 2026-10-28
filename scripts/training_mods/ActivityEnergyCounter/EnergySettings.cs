using LocalTraining;
using System;

namespace ActivityEnergyCounter
{
    public enum EnergyMode
    {
        NetActive = 0,
        GrossTotal = 1
    }

    public enum EnergyUnit
    {
        KiloJoules = 0,
        Kilocalories = 1
    }

    public enum EnergyScope
    {
        Song = 0,
        Session = 1,
        Today = 2
    }

    public sealed class EnergySettings : IValidatedSettings
    {
        public const double DefaultBodyMassKg = 70.0;
        public const double DefaultReferenceHandSpeedMps = 4.0;
        public const double DefaultCalibrationFactor = 1.0;

        public EnergyMode Mode { get; set; } = EnergyMode.NetActive;
        public EnergyUnit Unit { get; set; } = EnergyUnit.KiloJoules;
        public double BodyMassKg { get; set; } = DefaultBodyMassKg;
        public double ReferenceHandSpeedMps { get; set; } = DefaultReferenceHandSpeedMps;
        public double CalibrationFactor { get; set; } = DefaultCalibrationFactor;

        public bool Enabled { get; set; } = true;
        public bool UseHeadTracking { get; set; } = true;
        public bool HudShowSong { get; set; } = false;
        public bool HudShowSession { get; set; } = false;
        public bool HudShowToday { get; set; } = true;
        public bool ResultsShowSong { get; set; } = false;
        public bool ResultsShowSession { get; set; } = false;
        public bool ResultsShowToday { get; set; } = true;
        public bool HudShowDuration { get; set; } = false;
        public bool ResultsShowDuration { get; set; } = true;
        public bool ShowLabels { get; set; } = true;
        public bool ShowEstimate { get; set; } = true;
        public int DecimalPlaces { get; set; } = 1;
        public double HudFontScale { get; set; } = 1.0;
        public double ResultsFontScale { get; set; } = 1.0;
        public EnergyScope HudPrimaryScope { get; set; } = EnergyScope.Today;

        public void Validate()
        {
            if (!Enum.IsDefined(typeof(EnergyMode), Mode)) Mode = EnergyMode.NetActive;
            if (!Enum.IsDefined(typeof(EnergyUnit), Unit)) Unit = EnergyUnit.KiloJoules;
            if (!Enum.IsDefined(typeof(EnergyScope), HudPrimaryScope)) HudPrimaryScope = EnergyScope.Today;
            BodyMassKg = ClampOrDefault(BodyMassKg, 30.0, 200.0, DefaultBodyMassKg);
            ReferenceHandSpeedMps = ClampOrDefault(ReferenceHandSpeedMps, 1.0, 10.0, DefaultReferenceHandSpeedMps);
            CalibrationFactor = ClampOrDefault(CalibrationFactor, 0.5, 1.5, DefaultCalibrationFactor);
            DecimalPlaces = Math.Max(0, Math.Min(2, DecimalPlaces));
            HudFontScale = ClampOrDefault(HudFontScale, 0.5, 1.5, 1.0);
            ResultsFontScale = ClampOrDefault(ResultsFontScale, 0.75, 1.5, 1.0);
        }

        private static double ClampOrDefault(double value, double minimum, double maximum, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return fallback;
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        public bool IsHudScopeEnabled(EnergyScope scope)
        {
            switch (scope)
            {
                case EnergyScope.Song: return HudShowSong;
                case EnergyScope.Today: return HudShowToday;
                default: return HudShowSession;
            }
        }

        public bool IsResultsScopeEnabled(EnergyScope scope)
        {
            switch (scope)
            {
                case EnergyScope.Song: return ResultsShowSong;
                case EnergyScope.Today: return ResultsShowToday;
                default: return ResultsShowSession;
            }
        }
    }

}
