using System;
using System.Reflection;
namespace NoteMotionControl.Reduction
{
    public static class ArcMetadata
    {
        private static readonly PropertyInfo GameplayTypeProperty =
            typeof(NoteData).GetProperty(
                nameof(NoteData.gameplayType),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
            throw new MissingMemberException(typeof(NoteData).FullName, nameof(NoteData.gameplayType));

        private static readonly PropertyInfo ScoringTypeProperty =
            typeof(NoteData).GetProperty(
                nameof(NoteData.scoringType),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
            throw new MissingMemberException(typeof(NoteData).FullName, nameof(NoteData.scoringType));

        private static readonly PropertyInfo ArcHeadProperty =
            typeof(NoteData).GetProperty(
                nameof(NoteData.isArcHead),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
            throw new MissingMemberException(typeof(NoteData).FullName, nameof(NoteData.isArcHead));

        private static readonly PropertyInfo ArcTailProperty =
            typeof(NoteData).GetProperty(
                nameof(NoteData.isArcTail),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
            throw new MissingMemberException(typeof(NoteData).FullName, nameof(NoteData.isArcTail));

        public static void Restore(NoteData note, bool head, bool tail)
        {
            if (note == null) throw new ArgumentNullException(nameof(note));
            ArcHeadProperty.SetValue(note, head, null);
            ArcTailProperty.SetValue(note, tail, null);

            switch (note.gameplayType)
            {
                case NoteData.GameplayType.Normal:
                    ScoringTypeProperty.SetValue(
                        note,
                        head && tail
                            ? NoteData.ScoringType.ArcHeadArcTail
                            : head
                                ? NoteData.ScoringType.ArcHead
                                : tail
                                    ? NoteData.ScoringType.ArcTail
                                    : NoteData.ScoringType.Normal,
                        null);
                    break;
                case NoteData.GameplayType.BurstSliderHead:
                    ScoringTypeProperty.SetValue(
                        note,
                        head && tail
                            ? NoteData.ScoringType.ChainHeadArcHeadArcTail
                            : head
                                ? NoteData.ScoringType.ChainHeadArcHead
                                : tail
                                    ? NoteData.ScoringType.ChainHeadArcTail
                                    : NoteData.ScoringType.ChainHead,
                        null);
                    break;
                case NoteData.GameplayType.BurstSliderElement:
                    if (tail)
                        throw new InvalidOperationException(
                            "A retained Burst chain element cannot be a normal arc tail.");
                    ScoringTypeProperty.SetValue(
                        note,
                        head ? NoteData.ScoringType.ChainLinkArcHead : NoteData.ScoringType.ChainLink,
                        null);
                    break;
                default:
                    throw new InvalidOperationException(
                        "Unsupported NoteData gameplay type for arc metadata.");
            }
        }

        public static void NormalizeBurstElement(NoteData note)
        {
            if (note == null) throw new ArgumentNullException(nameof(note));
            note.ChangeToGameNote();
            GameplayTypeProperty.SetValue(note, NoteData.GameplayType.BurstSliderElement, null);
            ScoringTypeProperty.SetValue(note, NoteData.ScoringType.ChainLink, null);
        }
    }
}


