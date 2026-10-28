using System;

namespace NoteMotionControl.Reduction
{
    /// <summary>
    /// Small facts copied from the 1.45.2 BeatmapCore/DataModels processors.
    /// BurstSliderSpawner uses the head grid position for every non-final link
    /// and the tail grid position for the final link. The time-row processor
    /// connects two normal sliders only when their heads share a time row, the
    /// first slider has no note tail, the second has no note head, and the
    /// native head/tail grid positions overlap.
    /// </summary>
    internal static class NativeStructurePolicy
    {
        internal const float TimeRowEpsilon = 0.001f;

        internal static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        internal static bool IsNativeNormalSliderConnection(SliderData head, SliderData tail)
        {
            if (head == null || tail == null || head == tail) return false;
            if (head.sliderType != SliderData.Type.Normal ||
                tail.sliderType != SliderData.Type.Normal)
                return false;
            if (head.hasTailNote || tail.hasHeadNote) return false;
            if (!IsFinite(head.time) || !IsFinite(tail.time) ||
                Math.Abs(head.time - tail.time) > TimeRowEpsilon)
                return false;
            return head.headLineIndex == tail.tailLineIndex &&
                   head.headLineLayer == tail.tailLineLayer;
        }

        internal static int BurstLinkLineIndex(SliderData slider, int linkNumber)
        {
            if (slider == null) throw new ArgumentNullException(nameof(slider));
            return linkNumber == slider.sliceCount - 1
                ? slider.tailLineIndex
                : slider.headLineIndex;
        }

        internal static NoteLineLayer BurstLinkLayer(SliderData slider, int linkNumber)
        {
            if (slider == null) throw new ArgumentNullException(nameof(slider));
            return linkNumber == slider.sliceCount - 1
                ? slider.tailLineLayer
                : slider.headLineLayer;
        }
    }
}


