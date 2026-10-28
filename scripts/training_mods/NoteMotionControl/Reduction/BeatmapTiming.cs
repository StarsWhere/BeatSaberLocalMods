using System;

namespace NoteMotionControl.Reduction
{
    public static class BeatmapTiming
    {
        public static double BurstLinkTime(double headTime, double tailTime, int linkIndex, int sliceCount)
        {
            if (sliceCount <= 1)
                throw new ArgumentOutOfRangeException(nameof(sliceCount));
            if (linkIndex < 1 || linkIndex >= sliceCount)
                throw new ArgumentOutOfRangeException(nameof(linkIndex));
            return headTime + (tailTime - headTime) * linkIndex / (sliceCount - 1d);
        }
    }
}


