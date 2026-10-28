using System;

namespace ActivityEnergyCounter
{
    internal static class EnergySnapshotStore
    {
        private static readonly object Sync = new object();
        private static EnergySnapshot latest;
        private static long session;
        internal static long CurrentSession { get { lock (Sync) return session; } }
        internal static void Begin() { lock (Sync) { session++; latest = null; } }
        public static void Publish(EnergySnapshot snapshot, long ownerSession)
        {
            if (snapshot == null) return;
            lock (Sync) { if (ownerSession == session) latest = snapshot; }
        }
        public static bool TryGet(string key, out EnergySnapshot snapshot)
        {
            lock (Sync)
            {
                snapshot = latest != null && !string.IsNullOrEmpty(key)
                    && string.Equals(key, latest.BeatmapKey, StringComparison.Ordinal) ? latest : null;
                return snapshot != null;
            }
        }
        internal static void Clear() { lock (Sync) { latest = null; session++; } }
        internal static EnergySnapshot Latest { get { lock (Sync) return latest; } }
    }
}

