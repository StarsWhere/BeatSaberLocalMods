#nullable enable annotations
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using CustomJSONData.CustomBeatmap;
using NoteMotionControl;

namespace NoteMotionControl.Reduction
{
    internal sealed class BeatmapReducer
    {
        private const float EndpointEpsilon = 0.002f;

        public static ReductionApplication Apply(
            IReadonlyBeatmapData source,
            OptionsSnapshot options,
            string chartKey,
            float playbackSpeed)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (playbackSpeed <= 0 || float.IsNaN(playbackSpeed) || float.IsInfinity(playbackSpeed))
                playbackSpeed = 1f;

            var outcome = new ReductionOutcome
            {
                ChartKey = chartKey,
                Applied = options.Enabled,
                PlaybackSpeed = playbackSpeed,
                ReductionMode = options.ReductionMode,
                WindowSeconds = options.WindowSeconds,
            };

            if (!options.Enabled)
            {
                outcome.Reason = "Disabled.";
                return new ReductionApplication(source, outcome);
            }

            try
            {
                FilterPlan plan = FilterPlan.Build(source, options, playbackSpeed);
                outcome.OriginalScoringObjects = plan.OriginalScoringObjects;
                outcome.OriginalPeakNps = ComputePeakNps(source, playbackSpeed, options.WindowSeconds);

                if (plan.Groups.Count == 0)
                {
                    outcome.Reason = "Chart contains no selectable scoring groups.";
                    outcome.FilteredScoringObjects = plan.OriginalScoringObjects;
                    outcome.FilteredPeakNps = outcome.OriginalPeakNps;
                    return new ReductionApplication(source, outcome);
                }

                SelectionResult selection = options.ReductionMode == ReductionMode.Density
                    ? SelectionAlgorithm.SelectDensity(
                        plan.Groups,
                        options.MaxNps,
                        options.SameHandMinIntervalMs / 1000.0,
                        options.WindowSeconds)
                    : SelectionAlgorithm.SelectPercentage(plan.Groups, options.RetainPercent / 100.0);

                plan.ApplySelection(selection);
                IReadonlyBeatmapData filtered = plan.CreateFilteredCopy(source);
                outcome.UsedFallback = selection.UsedFallback;
                outcome.FilteredScoringObjects = CountScoringObjects(filtered);
                outcome.RemovedItems = plan.RemovedItems;
                outcome.OriginalMaxScore = ScoreModel.ComputeMaxMultipliedScoreForBeatmap(source);
                outcome.FilteredMaxScore = ScoreModel.ComputeMaxMultipliedScoreForBeatmap(filtered);
                outcome.FilteredPeakNps = ComputePeakNps(filtered, playbackSpeed, options.WindowSeconds);
                // A removed SliderData can leave both endpoint NoteData objects
                // as ordinary notes, so the scoring-object count may stay equal
                // even though the chart was changed.  RemovedItems is the direct
                // transformation fact used for score-gate activation.
                outcome.EffectiveReduction = outcome.RemovedItems > 0;
                outcome.Reason = selection.UsedFallback
                    ? "The target selected no valid group; one minimum valid group was retained."
                    : "Reduction applied.";
                return new ReductionApplication(filtered, outcome);
            }
            catch (ReductionTransformException)
            {
                throw;
            }
            catch (EndpointValidationException validation)
            {
                throw new ReductionTransformException(
                    "density.endpoint_invalid",
                    "Invalid slider endpoint; chart transformation was stopped: " + validation.Message,
                    validation);
            }
            catch (Exception error)
            {
                throw new ReductionTransformException(
                    "density.transform_failed",
                    "Chart transformation failed; play was stopped: " + error.Message,
                    error);
            }
        }

        private static int CountScoringObjects(IReadonlyBeatmapData source)
        {
            return EnumerateScoringActions(source, 1f).Count();
        }

        private static double ComputePeakNps(
            IReadonlyBeatmapData source,
            float playbackSpeed,
            double windowSeconds)
        {
            return DensityStatistics.PeakNps(
                EnumerateScoringActions(source, playbackSpeed), windowSeconds);
        }

        private static IEnumerable<ActionPoint> EnumerateScoringActions(
            IReadonlyBeatmapData source,
            float playbackSpeed)
        {
            BeatmapDataItem[] items = source.allBeatmapDataItems.ToArray();
            var notes = items.OfType<NoteData>().ToArray();
            foreach (NoteData note in notes.Where(IsScoring))
                yield return new ActionPoint(note.time / playbackSpeed, GetHand(note));

            foreach (SliderData slider in items.OfType<SliderData>()
                         .Where(x => x.sliderType == SliderData.Type.Burst))
            {
                if (slider.sliceCount < 2) continue;
                for (int linkNumber = 1; linkNumber < slider.sliceCount; linkNumber++)
                {
                    float expectedTime = (float)BeatmapTiming.BurstLinkTime(
                        slider.time, slider.tailTime, linkNumber, slider.sliceCount);
                    bool represented = notes.Any(note =>
                        note.gameplayType == NoteData.GameplayType.BurstSliderElement &&
                        Math.Abs(note.time - expectedTime) <= EndpointEpsilon &&
                        note.lineIndex == NativeStructurePolicy.BurstLinkLineIndex(slider, linkNumber) &&
                        note.noteLineLayer == NativeStructurePolicy.BurstLinkLayer(slider, linkNumber) &&
                        (!HasColor(slider.colorType) || note.colorType == slider.colorType));
                    if (!represented)
                        yield return new ActionPoint(expectedTime / playbackSpeed, GetHand(slider.colorType));
                }
            }
        }

        private static bool IsScoring(NoteData note)
        {
            return note.scoringType != NoteData.ScoringType.Ignore &&
                   note.scoringType != NoteData.ScoringType.NoScore;
        }

        private static ActionHand GetHand(NoteData note)
        {
            return GetHand(note.colorType);
        }

        private static ActionHand GetHand(ColorType color)
        {
            return color == ColorType.ColorA
                ? ActionHand.Left
                : color == ColorType.ColorB ? ActionHand.Right : ActionHand.Any;
        }

        private static bool HasColor(ColorType color)
        {
            return color == ColorType.ColorA || color == ColorType.ColorB;
        }

        private sealed class FilterPlan
        {
            private readonly List<Decision> _decisions;
            private readonly Dictionary<int, GroupSpec> _groupsById;
            private readonly Dictionary<int, GroupSpec> _noteGroups;
            private readonly Dictionary<int, List<ArcRelation>> _arcsByNote;
            private readonly Dictionary<int, GroupSpec> _burstGroupsBySlider;
            private readonly IReadOnlyList<SliderConnection> _sliderConnections;
            private readonly HashSet<int> _burstHeadNotes;
            private readonly Dictionary<int, bool> _sliderKeep = new Dictionary<int, bool>();
            private int _removedItems;

            private FilterPlan(
                List<Decision> decisions,
                Dictionary<int, GroupSpec> groupsById,
                Dictionary<int, GroupSpec> noteGroups,
                Dictionary<int, List<ArcRelation>> arcsByNote,
                Dictionary<int, GroupSpec> burstGroupsBySlider,
                IReadOnlyList<SliderConnection> sliderConnections,
                HashSet<int> burstHeadNotes,
                int originalScoringObjects)
            {
                _decisions = decisions;
                _groupsById = groupsById;
                _noteGroups = noteGroups;
                _arcsByNote = arcsByNote;
                _burstGroupsBySlider = burstGroupsBySlider;
                _sliderConnections = sliderConnections;
                _burstHeadNotes = burstHeadNotes;
                OriginalScoringObjects = originalScoringObjects;
            }

            public IReadOnlyList<CandidateGroup> Groups { get; private set; } = Array.Empty<CandidateGroup>();

            public int OriginalScoringObjects { get; }

            public int RemovedItems => _removedItems;

            public static FilterPlan Build(IReadonlyBeatmapData source, OptionsSnapshot options, float playbackSpeed)
            {
                BeatmapDataItem[] items = source.allBeatmapDataItems.ToArray();
                var notes = new List<(int Index, NoteData Note)>();
                var sliders = new List<(int Index, SliderData Slider)>();
                for (int i = 0; i < items.Length; i++)
                {
                    if (items[i] is NoteData note) notes.Add((i, note));
                    else if (items[i] is SliderData slider) sliders.Add((i, slider));
                }

                ValidateNoteMetadata(notes);
                ValidateSliderMetadata(sliders);

                var groupsById = new Dictionary<int, GroupSpec>();
                var noteGroups = new Dictionary<int, GroupSpec>();
                var arcsByNote = new Dictionary<int, List<ArcRelation>>();
                var burstGroupsBySlider = new Dictionary<int, GroupSpec>();
                var mappedBurstNotes = new HashSet<int>();
                var burstHeadNotes = new HashSet<int>();
                int generatedBurstScoringObjects = 0;
                int nextGroupId = 0;

                // Burst sliders are built first so a chain head that is also an
                // arc endpoint remains one atomic group.
                foreach ((int sliderIndex, SliderData slider) in sliders.Where(x => x.Slider.sliderType == SliderData.Type.Burst))
                {
                    int headIndex = FindUniqueEndpoint(source, notes, slider.time, slider.headLineIndex,
                        slider.headLineLayer, slider.colorType, false, slider);
                    NoteData head = items[headIndex] as NoteData
                        ?? throw new EndpointValidationException("Burst head is not a note.");
                    if (head.gameplayType == NoteData.GameplayType.BurstSliderElement)
                    {
                        throw new EndpointValidationException("Burst slider head has an unexpected gameplay type.");
                    }

                    var members = new List<int> { headIndex };
                    mappedBurstNotes.Add(headIndex);
                    burstHeadNotes.Add(headIndex);
                    var generatedLinkActions = new List<ActionPoint>();
                    int expectedLinks = Math.Max(0, slider.sliceCount - 1);
                    for (int linkNumber = 1; linkNumber <= expectedLinks; linkNumber++)
                    {
                        float expectedTime = (float)BeatmapTiming.BurstLinkTime(
                            slider.time,
                            slider.tailTime,
                            linkNumber,
                            slider.sliceCount);
                        var matches = notes.Where(x =>
                                x.Note.gameplayType == NoteData.GameplayType.BurstSliderElement &&
                                Math.Abs(x.Note.time - expectedTime) <= EndpointEpsilon &&
                                x.Note.lineIndex == NativeStructurePolicy.BurstLinkLineIndex(slider, linkNumber) &&
                                x.Note.noteLineLayer == NativeStructurePolicy.BurstLinkLayer(slider, linkNumber) &&
                                (!HasColor(slider.colorType) || x.Note.colorType == slider.colorType))
                            .Select(x => x.Index)
                            .ToArray();
                        if (matches.Length > 1)
                        {
                            throw new EndpointValidationException(
                                "Burst slider link position or timing matches more than one declared chain link.");
                        }

                        if (matches.Length == 0)
                        {
                            // Beat Saber 1.45.2 stores the BurstSliderData
                            // and its head NoteData in BeatmapData.  The
                            // BurstSliderSpawner creates the remaining links
                            // later during gameplay, so missing source link
                            // items are normal and must contribute synthetic
                            // scoring actions instead of failing closed.
                            generatedLinkActions.Add(new ActionPoint(
                                expectedTime / playbackSpeed,
                                GetHand(slider.colorType)));
                            continue;
                        }

                        if (!mappedBurstNotes.Add(matches[0]))
                            throw new EndpointValidationException("A Burst slider link belongs to more than one chain.");
                        members.Add(matches[0]);
                    }

                    GroupSpec group = new GroupSpec(nextGroupId++);
                    foreach (int member in members)
                    {
                        NoteData memberNote = (NoteData)items[member];
                        group.Items.Add(member);
                        if (IsScoring(memberNote))
                        {
                            group.Weight++;
                            group.Actions.Add(new ActionPoint(memberNote.time / playbackSpeed, GetHand(memberNote)));
                        }

                        if (Preserve(memberNote)) group.ForcedKeep = true;
                        if (member == headIndex) noteGroups[member] = group;
                    }

                    foreach (ActionPoint generatedLink in generatedLinkActions)
                    {
                        group.Weight++;
                        group.Actions.Add(generatedLink);
                        generatedBurstScoringObjects++;
                    }

                    if (Preserve(slider)) group.ForcedKeep = true;
                    if (group.Weight == 0) group.ForcedKeep = true;
                    groupsById.Add(group.Id, group);
                    burstGroupsBySlider.Add(sliderIndex, group);
                    foreach (int member in members)
                    {
                        if (noteGroups.ContainsKey(member) && noteGroups[member] != group)
                            throw new EndpointValidationException("A note belongs to multiple atomic groups.");
                        noteGroups[member] = group;
                    }
                }

                // Every Burst element/head must have been assigned above.  A
                // silent orphan would otherwise be deleted independently and
                // leave a broken chain.
                foreach ((int index, NoteData note) in notes)
                {
                    if ((note.gameplayType == NoteData.GameplayType.BurstSliderHead ||
                         note.gameplayType == NoteData.GameplayType.BurstSliderElement) &&
                        !mappedBurstNotes.Contains(index))
                    {
                        throw new EndpointValidationException("Burst note has no unique owning Burst slider.");
                    }
                }

                // Normal arcs are relations between independently selectable
                // endpoint note groups.  The slider itself is retained only if
                // every declared endpoint survives.
                foreach ((int sliderIndex, SliderData slider) in sliders.Where(x => x.Slider.sliderType == SliderData.Type.Normal))
                {
                    var relation = new ArcRelation(sliderIndex);
                    if (slider.hasHeadNote)
                    {
                        relation.HeadNote = FindUniqueEndpoint(source, notes, slider.time, slider.headLineIndex,
                            slider.headLineLayer, slider.colorType, false, slider);
                        AddArcEndpoint(relation.HeadNote.Value, items, noteGroups, groupsById, ref nextGroupId,
                            playbackSpeed, arcsByNote);
                    }
                    else
                    {
                        relation.HeadNote = TryFindEndpoint(source, notes, slider.time, slider.headLineIndex,
                            slider.headLineLayer, slider.colorType, false, slider);
                        if (relation.HeadNote.HasValue)
                            AddArcEndpoint(relation.HeadNote.Value, items, noteGroups, groupsById, ref nextGroupId,
                                playbackSpeed, arcsByNote);
                    }

                    if (slider.hasTailNote)
                    {
                        relation.TailNote = FindUniqueEndpoint(source, notes, slider.tailTime, slider.tailLineIndex,
                            slider.tailLineLayer, slider.colorType, true, slider);
                        AddArcEndpoint(relation.TailNote.Value, items, noteGroups, groupsById, ref nextGroupId,
                            playbackSpeed, arcsByNote);
                    }
                    else
                    {
                        relation.TailNote = TryFindEndpoint(source, notes, slider.tailTime, slider.tailLineIndex,
                            slider.tailLineLayer, slider.colorType, true, slider);
                        if (relation.TailNote.HasValue)
                            AddArcEndpoint(relation.TailNote.Value, items, noteGroups, groupsById, ref nextGroupId,
                                playbackSpeed, arcsByNote);
                    }

                    if (relation.HeadNote.HasValue || relation.TailNote.HasValue)
                    {
                        if (!arcsByNote.TryGetValue(relation.HeadNote ?? relation.TailNote!.Value, out _))
                            throw new EndpointValidationException("Arc endpoint relation was not registered.");
                        if (relation.HeadNote.HasValue)
                            arcsByNote[relation.HeadNote.Value].Add(relation);
                        if (relation.TailNote.HasValue && relation.TailNote != relation.HeadNote)
                            arcsByNote[relation.TailNote.Value].Add(relation);
                    }

                    if (Preserve(slider))
                    {
                        relation.ForcedKeep = true;
                        if (relation.HeadNote.HasValue) groupsById[noteGroups[relation.HeadNote.Value].Id].ForcedKeep = true;
                        if (relation.TailNote.HasValue) groupsById[noteGroups[relation.TailNote.Value].Id].ForcedKeep = true;
                    }

                    relation.SourceSlider = slider;
                    relation.Valid = true;
                }

                // The native processor marks arc endpoints on NoteData.  If a
                // transformed chart carries an arc mark without a matching
                // declared normal slider, dropping that note could leave a
                // stale ArcHead/ArcTail scoring type, so the reducer keeps the
                // complete transformed source instead.
                foreach ((int index, NoteData note) in notes)
                {
                    if ((note.isArcHead || note.isArcTail || HasArcScoring(note.scoringType)) &&
                        !arcsByNote.ContainsKey(index))
                    {
                        throw new EndpointValidationException("Arc-marked note has no owning normal slider.");
                    }
                }

                // Resolve normal slider-to-slider flags only after optional
                // endpoint inference.  A final transformed copy can carry
                // hasHeadNote/hasTailNote=false before the native processor
                // closes its time slice; inferred endpoint relations still
                // block a false slider-to-slider connection.
                List<SliderConnection> sliderConnections =
                    BuildSliderConnections(sliders, arcsByNote);

                // Remaining normal scoring notes are one-note groups.  Fake and
                // non-interactable notes are always preserved and never consume
                // the reduction budget.
                foreach ((int index, NoteData note) in notes)
                {
                    if (noteGroups.ContainsKey(index)) continue;
                    if (note.gameplayType == NoteData.GameplayType.BurstSliderHead ||
                        note.gameplayType == NoteData.GameplayType.BurstSliderElement)
                        throw new EndpointValidationException("Burst note escaped atomic grouping.");
                    if (!IsScoring(note)) continue;
                    GroupSpec group = CreateNoteGroup(index, note, ref nextGroupId, playbackSpeed);
                    groupsById.Add(group.Id, group);
                    noteGroups.Add(index, group);
                }

                var decisions = new List<Decision>(items.Length);
                for (int i = 0; i < items.Length; i++)
                {
                    decisions.Add(new Decision { Original = items[i], Keep = true });
                }

                var plan = new FilterPlan(decisions, groupsById, noteGroups, arcsByNote,
                    burstGroupsBySlider, sliderConnections, burstHeadNotes,
                    notes.Count(x => IsScoring(x.Note)) + generatedBurstScoringObjects);
                plan.Groups = groupsById.Values
                    .Where(g => g.Weight > 0 || g.ForcedKeep)
                    .Select(g => g.ToCandidate())
                    .OrderBy(g => g.FirstTime)
                    .ThenBy(g => g.Id)
                    .ToArray();

                foreach ((int sliderIndex, SliderData slider) in sliders)
                {
                    if (slider.sliderType == SliderData.Type.Burst)
                    {
                        if (!burstGroupsBySlider.TryGetValue(sliderIndex, out GroupSpec? group))
                        {
                            plan._sliderKeep[sliderIndex] = true;
                        }
                        else
                        {
                            plan._sliderKeep[sliderIndex] = group.ForcedKeep;
                            plan._decisions[sliderIndex].Keep = group.ForcedKeep;
                        }

                        continue;
                    }

                    List<ArcRelation> relations = arcsByNote.Values
                        .SelectMany(x => x)
                        .Where(x => x.SliderIndex == sliderIndex)
                        .Distinct()
                        .ToList();
                    plan._sliderKeep[sliderIndex] = relations.Count == 0;
                    plan._decisions[sliderIndex].Keep = plan._sliderKeep[sliderIndex];
                }

                foreach ((int index, NoteData note) in notes)
                {
                    if (!noteGroups.TryGetValue(index, out GroupSpec? group)) continue;
                    bool keep = group.ForcedKeep;
                    decisions[index].Group = group;
                    decisions[index].Keep = keep;
                    decisions[index].Scoring = IsScoring(note);
                }

                return plan;
            }

            private static void ValidateNoteMetadata(
                IReadOnlyList<(int Index, NoteData Note)> notes)
            {
                foreach ((int index, NoteData note) in notes)
                {
                    if (!NativeStructurePolicy.IsFinite(note.time))
                        throw new EndpointValidationException(
                            "NoteData " + index + " has a non-finite scoring time.");
                    if (!IsKnownGameplayType(note.gameplayType))
                        throw new EndpointValidationException(
                            "NoteData " + index + " has an unsupported gameplay type.");
                    if (!IsKnownScoringType(note.scoringType))
                        throw new EndpointValidationException(
                            "NoteData " + index + " has an unsupported scoring type.");

                    switch (note.gameplayType)
                    {
                        case NoteData.GameplayType.BurstSliderHead:
                            if (!IsChainHeadScoring(note.scoringType))
                                throw new EndpointValidationException(
                                    "Burst slider head has a non-chain scoring type.");
                            break;
                        case NoteData.GameplayType.BurstSliderElement:
                            if (!IsChainLinkScoring(note.scoringType))
                                throw new EndpointValidationException(
                                    "Burst slider element has a non-chain scoring type.");
                            break;
                        case NoteData.GameplayType.Normal:
                            if (IsChainScoring(note.scoringType))
                                throw new EndpointValidationException(
                                    "Normal NoteData has a chain scoring type.");
                            break;
                    }
                }
            }

            private static void ValidateSliderMetadata(
                IReadOnlyList<(int Index, SliderData Slider)> sliders)
            {
                foreach ((int index, SliderData slider) in sliders)
                {
                    if (!IsKnownSliderType(slider.sliderType))
                        throw new EndpointValidationException(
                            "SliderData " + index + " has an unsupported slider type.");
                    if (!NativeStructurePolicy.IsFinite(slider.time) ||
                        !NativeStructurePolicy.IsFinite(slider.tailTime) ||
                        slider.tailTime < slider.time - EndpointEpsilon)
                        throw new EndpointValidationException(
                            "SliderData " + index + " has invalid endpoint times.");
                    if (!NativeStructurePolicy.IsFinite(slider.headControlPointLengthMultiplier) ||
                        !NativeStructurePolicy.IsFinite(slider.tailControlPointLengthMultiplier) ||
                        !NativeStructurePolicy.IsFinite(slider.headCutDirectionAngleOffset) ||
                        !NativeStructurePolicy.IsFinite(slider.tailCutDirectionAngleOffset) ||
                        !NativeStructurePolicy.IsFinite(slider.squishAmount))
                        throw new EndpointValidationException(
                            "SliderData " + index + " has non-finite metadata.");

                    if (slider.sliderType != SliderData.Type.Burst) continue;
                    if (slider.sliceCount < 2)
                        throw new EndpointValidationException(
                            "Burst slider has no valid slice/link count.");
                }
            }

            private static List<SliderConnection> BuildSliderConnections(
                IReadOnlyList<(int Index, SliderData Slider)> sliders,
                Dictionary<int, List<ArcRelation>> arcsByNote)
            {
                var connections = new List<SliderConnection>();
                var seen = new HashSet<(int HeadSlider, int TailSlider)>();
                var slidersWithHeadNote = new HashSet<int>();
                var slidersWithTailNote = new HashSet<int>();
                foreach (List<ArcRelation> relations in arcsByNote.Values)
                {
                    foreach (ArcRelation relation in relations)
                    {
                        if (relation.HeadNote.HasValue) slidersWithHeadNote.Add(relation.SliderIndex);
                        if (relation.TailNote.HasValue) slidersWithTailNote.Add(relation.SliderIndex);
                    }
                }

                for (int i = 0; i < sliders.Count; i++)
                {
                    for (int j = 0; j < sliders.Count; j++)
                    {
                        if (i == j) continue;
                        (int headIndex, SliderData head) = sliders[i];
                        (int tailIndex, SliderData tail) = sliders[j];
                        if (!NativeStructurePolicy.IsNativeNormalSliderConnection(head, tail))
                            continue;
                        if (slidersWithTailNote.Contains(headIndex) ||
                            slidersWithHeadNote.Contains(tailIndex))
                            continue;
                        if (seen.Add((headIndex, tailIndex)))
                            connections.Add(new SliderConnection(headIndex, tailIndex));
                    }
                }

                return connections;
            }

            private static bool IsKnownSliderType(SliderData.Type type)
            {
                return type == SliderData.Type.Normal || type == SliderData.Type.Burst;
            }

            private static bool IsKnownGameplayType(NoteData.GameplayType type)
            {
                return type == NoteData.GameplayType.Normal ||
                       type == NoteData.GameplayType.Bomb ||
                       type == NoteData.GameplayType.BurstSliderHead ||
                       type == NoteData.GameplayType.BurstSliderElement;
            }

            private static bool IsKnownScoringType(NoteData.ScoringType type)
            {
                return type == NoteData.ScoringType.Ignore ||
                       type == NoteData.ScoringType.NoScore ||
                       type == NoteData.ScoringType.Normal ||
                       type == NoteData.ScoringType.ArcHead ||
                       type == NoteData.ScoringType.ArcTail ||
                       type == NoteData.ScoringType.ChainHead ||
                       type == NoteData.ScoringType.ChainLink ||
                       type == NoteData.ScoringType.ArcHeadArcTail ||
                       type == NoteData.ScoringType.ChainHeadArcTail ||
                       type == NoteData.ScoringType.ChainLinkArcHead ||
                       type == NoteData.ScoringType.ChainHeadArcHead ||
                       type == NoteData.ScoringType.ChainHeadArcHeadArcTail;
            }

            private static bool IsChainScoring(NoteData.ScoringType type)
            {
                return IsChainHeadScoring(type) || IsChainLinkScoring(type);
            }

            private static bool IsChainHeadScoring(NoteData.ScoringType type)
            {
                return type == NoteData.ScoringType.ChainHead ||
                       type == NoteData.ScoringType.ChainHeadArcTail ||
                       type == NoteData.ScoringType.ChainHeadArcHead ||
                       type == NoteData.ScoringType.ChainHeadArcHeadArcTail;
            }

            private static bool IsChainLinkScoring(NoteData.ScoringType type)
            {
                return type == NoteData.ScoringType.ChainLink ||
                       type == NoteData.ScoringType.ChainLinkArcHead;
            }

            public void ApplySelection(SelectionResult selection)
            {
                var selected = new HashSet<int>(selection.KeptGroupIds);
                foreach (GroupSpec group in _groupsById.Values)
                {
                    bool keep = group.ForcedKeep || selected.Contains(group.Id);
                    foreach (int item in group.Items)
                    {
                        _decisions[item].Keep = keep;
                        _decisions[item].Group = group;
                    }
                }

                // The native time-row processor may not have run on the final
                // transformed copy yet, leaving a legitimate Burst head as a
                // plain NoteData with hasHeadNote=false on SliderData.  The
                // declared grid/time match is the authoritative relationship;
                // normalize a retained head back to the native chain type when
                // producing the filtered copy.
                foreach (int headIndex in _burstHeadNotes)
                {
                    if (_decisions[headIndex].Keep)
                        _decisions[headIndex].NormalizeBurstHead = true;
                }

                // Re-evaluate every ordinary arc after the endpoint groups have
                // been selected.  This is the point where a selected head or
                // tail can cause its slider to disappear while the remaining
                // endpoint is retained as a plain game note.
                foreach (KeyValuePair<int, List<ArcRelation>> entry in _arcsByNote)
                {
                    foreach (ArcRelation relation in entry.Value)
                    {
                        if (_sliderKeep.ContainsKey(relation.SliderIndex))
                        {
                            _sliderKeep[relation.SliderIndex] =
                                EndpointSelected(relation.HeadNote, selected) &&
                                EndpointSelected(relation.TailNote, selected);
                        }
                    }
                }

                foreach (KeyValuePair<int, GroupSpec> burst in _burstGroupsBySlider)
                {
                    _sliderKeep[burst.Key] = burst.Value.ForcedKeep || selected.Contains(burst.Value.Id);
                }

                foreach (KeyValuePair<int, bool> slider in _sliderKeep)
                {
                    Decision decision = _decisions[slider.Key];
                    decision.Keep = slider.Value;
                    decision.Drop = !slider.Value;
                }

                // A filtered copy does not go through BeatmapObjectsInTimeRowProcessor
                // again.  Record exactly which endpoint roles still own a retained
                // slider so the copy can restore both the private arc flags and the
                // matching scoring enum without reviving a removed relation.
                foreach (KeyValuePair<int, List<ArcRelation>> entry in _arcsByNote)
                {
                    Decision decision = _decisions[entry.Key];
                    foreach (ArcRelation relation in entry.Value)
                    {
                        if (!_sliderKeep.TryGetValue(relation.SliderIndex, out bool keep) || !keep)
                            continue;
                        if (relation.HeadNote == entry.Key) decision.RestoreArcHead = true;
                        if (relation.TailNote == entry.Key) decision.RestoreArcTail = true;
                    }

                    if (!decision.Keep || decision.Group == null)
                        continue;

                    bool keepArc = decision.RestoreArcHead || decision.RestoreArcTail;
                    NoteData original = (NoteData)decision.Original!;
                    if (keepArc)
                    {
                        decision.RestoreArcMetadata = true;
                    }
                    else if (original.gameplayType == NoteData.GameplayType.BurstSliderHead)
                    {
                        decision.NormalizeBurstHead = true;
                    }
                    else if (original.gameplayType == NoteData.GameplayType.BurstSliderElement)
                    {
                        decision.NormalizeBurstElement = true;
                    }
                    else
                    {
                        decision.NormalizeToGameNote = true;
                    }
                }

                _removedItems = 0;
                for (int i = 0; i < _decisions.Count; i++)
                {
                    Decision decision = _decisions[i];
                    if (!decision.Keep && (decision.Group != null || decision.Original is SliderData))
                    {
                        decision.Drop = true;
                        _removedItems++;
                    }
                }
            }

            public IReadonlyBeatmapData CreateFilteredCopy(IReadonlyBeatmapData source)
            {
                int ordinal = 0;
                var retainedSliders = new List<(int SourceIndex, SliderData Slider)>();
                IReadonlyBeatmapData filtered = source.GetFilteredCopy(copy =>
                {
                    if (ordinal >= _decisions.Count)
                        throw new InvalidOperationException("Filtered-copy item order changed while planning.");
                    Decision decision = _decisions[ordinal++];
                    if (decision.Drop) return null!;
                    if (copy is NoteData note && decision.NormalizeBurstHead)
                    {
                        note.ChangeToBurstSliderHead();
                    }
                    else if (copy is NoteData burstElement && decision.NormalizeBurstElement)
                    {
                        NormalizeBurstElement(burstElement);
                    }
                    else if (decision.NormalizeToGameNote && copy is NoteData normalNote)
                        normalNote.ChangeToGameNote();

                    if (copy is NoteData arcNote && decision.RestoreArcMetadata)
                        RestoreArcMetadata(arcNote, decision);

                    if (copy is SliderData sliderCopy)
                        retainedSliders.Add((ordinal - 1, sliderCopy));
                    return copy;
                });
                if (ordinal != _decisions.Count)
                    throw new InvalidOperationException("Filtered-copy item count changed while planning.");

                // BeatmapData.GetFilteredCopy inserts the returned objects into
                // a fresh BeatmapData and recomputes native slider flags while
                // doing so.  Apply the final post-selection truth after that
                // insertion pass, otherwise a removed neighbour (or a native
                // self-overlap) can recreate a stale hasHead/hasTailSlider bit.
                foreach ((int sourceIndex, SliderData slider) in retainedSliders)
                    RestoreSliderConnections(slider, sourceIndex);
                return filtered;
            }

            private static void NormalizeBurstElement(NoteData note)
            {
                ArcMetadata.NormalizeBurstElement(note);
            }

            private static void RestoreArcMetadata(NoteData note, Decision decision)
            {
                ArcMetadata.Restore(note, decision.RestoreArcHead, decision.RestoreArcTail);
            }

            private void RestoreSliderConnections(SliderData slider, int sourceIndex)
            {
                bool hasHeadSlider = false;
                bool hasTailSlider = false;
                foreach (SliderConnection connection in _sliderConnections)
                {
                    if (connection.HeadSlider == sourceIndex && IsRetainedSlider(connection.TailSlider))
                        hasTailSlider = true;
                    if (connection.TailSlider == sourceIndex && IsRetainedSlider(connection.HeadSlider))
                        hasHeadSlider = true;
                    if (hasHeadSlider && hasTailSlider) break;
                }

                // GetFilteredCopy invokes SliderData.GetCopy(), which does not
                // copy these private native connection flags.  Rebuild both
                // flags from the post-selection slider set so a removed
                // neighbour cannot leave a dangling slider-to-slider relation.
                slider.SetHasHeadSlider(hasHeadSlider);
                slider.SetHasTailSlider(hasTailSlider);
            }

            private bool IsRetainedSlider(int sourceIndex)
            {
                if (sourceIndex < 0 || sourceIndex >= _decisions.Count) return false;
                Decision decision = _decisions[sourceIndex];
                return decision.Keep && !decision.Drop && decision.Original is SliderData;
            }

            private bool EndpointSelected(int? noteIndex, HashSet<int> selected)
            {
                return !noteIndex.HasValue ||
                       (_noteGroups.TryGetValue(noteIndex.Value, out GroupSpec? group) &&
                        (group.ForcedKeep || selected.Contains(group.Id)));
            }

            private static void AddArcEndpoint(
                int noteIndex,
                BeatmapDataItem[] items,
                Dictionary<int, GroupSpec> noteGroups,
                Dictionary<int, GroupSpec> groupsById,
                ref int nextGroupId,
                float playbackSpeed,
                Dictionary<int, List<ArcRelation>> arcsByNote)
            {
                if (!arcsByNote.ContainsKey(noteIndex)) arcsByNote[noteIndex] = new List<ArcRelation>();
                if (noteGroups.ContainsKey(noteIndex)) return;
                NoteData note = items[noteIndex] as NoteData
                    ?? throw new EndpointValidationException("Arc endpoint is not a NoteData object.");
                GroupSpec group = IsScoring(note)
                    ? CreateNoteGroup(noteIndex, note, ref nextGroupId, playbackSpeed)
                    : CreateForcedGroup(noteIndex, note, ref nextGroupId);
                groupsById.Add(group.Id, group);
                noteGroups.Add(noteIndex, group);
            }

            private static GroupSpec CreateNoteGroup(int index, NoteData note, ref int nextGroupId, float playbackSpeed)
            {
                var group = new GroupSpec(nextGroupId++);
                group.Items.Add(index);
                group.Weight = 1;
                group.Actions.Add(new ActionPoint(note.time / playbackSpeed, GetHand(note)));
                group.ForcedKeep = Preserve(note);
                return group;
            }

            private static GroupSpec CreateForcedGroup(int index, NoteData note, ref int nextGroupId)
            {
                var group = new GroupSpec(nextGroupId++);
                group.Items.Add(index);
                group.ForcedKeep = true;
                return group;
            }

            private static int FindUniqueEndpoint(
                IReadonlyBeatmapData source,
                IReadOnlyList<(int Index, NoteData Note)> notes,
                float time,
                int lineIndex,
                NoteLineLayer layer,
                ColorType color,
                bool tail,
                SliderData slider)
            {
                int? match = TryFindEndpoint(source, notes, time, lineIndex, layer, color, tail, slider);
                if (!match.HasValue)
                    throw new EndpointValidationException("Declared slider endpoint has no matching note.");
                return match.Value;
            }

            private static int? TryFindEndpoint(
                IReadonlyBeatmapData source,
                IReadOnlyList<(int Index, NoteData Note)> notes,
                float time,
                int lineIndex,
                NoteLineLayer layer,
                ColorType color,
                bool tail,
                SliderData slider)
            {
                EndpointPoint expected = EndpointPoint.ForSlider(
                    source, time, lineIndex, layer, color, tail, slider);
                var matches = notes.Where(x =>
                {
                    if (x.Note.gameplayType == NoteData.GameplayType.Bomb) return false;
                    EndpointPoint point = EndpointPoint.ForNote(source, x.Note);
                    return Math.Abs(point.Time - expected.Time) <= EndpointEpsilon &&
                           Math.Abs(point.X - expected.X) <= EndpointEpsilon &&
                           Math.Abs(point.Y - expected.Y) <= EndpointEpsilon &&
                           (!HasColor(color) || x.Note.colorType == color);
                }).Select(x => x.Index).ToArray();

                if (matches.Length > 1)
                {
                    throw new EndpointValidationException("Declared slider endpoint matches more than one note.");
                }

                return matches.Length == 0 ? (int?)null : matches[0];
            }

            private static bool HasColor(ColorType color)
            {
                return color == ColorType.ColorA || color == ColorType.ColorB;
            }

            private static bool HasArcScoring(NoteData.ScoringType scoringType)
            {
                return scoringType == NoteData.ScoringType.ArcHead ||
                       scoringType == NoteData.ScoringType.ArcTail ||
                       scoringType == NoteData.ScoringType.ArcHeadArcTail ||
                       scoringType == NoteData.ScoringType.ChainHeadArcTail ||
                       scoringType == NoteData.ScoringType.ChainLinkArcHead ||
                       scoringType == NoteData.ScoringType.ChainHeadArcHead ||
                       scoringType == NoteData.ScoringType.ChainHeadArcHeadArcTail;
            }
        }

        private sealed class GroupSpec
        {
            public GroupSpec(int id) { Id = id; }

            public int Id { get; }

            public int Weight { get; set; }

            public bool ForcedKeep { get; set; }

            public List<int> Items { get; } = new List<int>();

            public List<ActionPoint> Actions { get; } = new List<ActionPoint>();

            public CandidateGroup ToCandidate() => new CandidateGroup(Id, Weight, Actions, ForcedKeep);
        }

        private sealed class Decision
        {
            public bool Keep { get; set; }

            public bool Drop { get; set; }

            public bool NormalizeToGameNote { get; set; }

            public bool NormalizeBurstHead { get; set; }

            public bool NormalizeBurstElement { get; set; }

            public bool RestoreArcMetadata { get; set; }

            public bool RestoreArcHead { get; set; }

            public bool RestoreArcTail { get; set; }

            public bool Scoring { get; set; }

            public GroupSpec? Group { get; set; }

            public BeatmapDataItem? Original { get; set; }
        }

        private sealed class ArcRelation
        {
            public ArcRelation(int sliderIndex) { SliderIndex = sliderIndex; }

            public int SliderIndex { get; }

            public int? HeadNote { get; set; }

            public int? TailNote { get; set; }

            public SliderData? SourceSlider { get; set; }

            public bool ForcedKeep { get; set; }

            public bool Valid { get; set; }
        }

        private sealed class SliderConnection
        {
            public SliderConnection(int headSlider, int tailSlider)
            {
                HeadSlider = headSlider;
                TailSlider = tailSlider;
            }

            public int HeadSlider { get; }

            public int TailSlider { get; }
        }

        private readonly struct EndpointPoint
        {
            private EndpointPoint(float time, float x, float y)
            {
                Time = time;
                X = x;
                Y = y;
            }

            public float Time { get; }

            public float X { get; }

            public float Y { get; }

            public static EndpointPoint ForNote(IReadonlyBeatmapData source, NoteData note)
            {
                float x = note.lineIndex;
                float y = (float)note.noteLineLayer;
                if (note is ICustomData custom)
                {
                    if (TryCoordinates(custom.customData, "_position", out float px, out float py))
                    {
                        x = px;
                        y = py;
                    }
                    else if (TryCoordinates(custom.customData, "coordinates", out px, out py))
                    {
                        x = px + source.numberOfLines / 2f;
                        y = py;
                    }
                }

                return new EndpointPoint(note.time, x, y);
            }

            public static EndpointPoint ForSlider(
                IReadonlyBeatmapData source,
                float time,
                int lineIndex,
                NoteLineLayer layer,
                ColorType color,
                bool tail,
                SliderData? slider)
            {
                float x = lineIndex;
                float y = (float)layer;
                if (slider is ICustomData custom)
                {
                    string[] names = tail
                        ? new[] { "_tailPosition", "tailCoordinates" }
                        : new[] { "_position", "coordinates" };
                    foreach (string name in names)
                    {
                        if (!TryCoordinates(custom.customData, name, out float px, out float py)) continue;
                        x = name == "coordinates" || name == "tailCoordinates"
                            ? px + source.numberOfLines / 2f
                            : px;
                        y = py;
                        break;
                    }
                }

                return new EndpointPoint(time, x, y);
            }

            private static bool TryCoordinates(CustomData customData, string name, out float x, out float y)
            {
                x = 0;
                y = 0;
                if (!customData.TryGetValue(name, out object? value) || value is string || value is not IEnumerable enumerable)
                    return false;
                var values = new List<float>();
                foreach (object? entry in enumerable)
                {
                    if (entry == null) return false;
                    try { values.Add(Convert.ToSingle(entry)); }
                    catch { return false; }
                    if (values.Count == 2) break;
                }

                if (values.Count < 2) return false;
                x = values[0];
                y = values[1];
                return true;
            }
        }

        private static bool Preserve(NoteData note)
        {
            return note is ICustomData custom && IsPreservedCustomData(custom.customData);
        }

        private static bool Preserve(SliderData slider)
        {
            return slider is ICustomData custom && IsPreservedCustomData(custom.customData);
        }

        private static bool IsPreservedCustomData(CustomData customData)
        {
            if (IsTrue(customData, "_fake") || IsTrue(customData, "fake") || IsTrue(customData, "NE_fake"))
                return true;
            if (customData.TryGetValue("_interactable", out object? v) && IsFalse(v)) return true;
            if (customData.TryGetValue("interactable", out v) && IsFalse(v)) return true;
            if (customData.TryGetValue("uninteractable", out v) && IsTrue(v)) return true;
            return false;
        }

        private static bool IsTrue(CustomData data, string key)
        {
            return data.TryGetValue(key, out object? value) && IsTrue(value);
        }

        private static bool IsTrue(object? value)
        {
            if (value is bool boolean) return boolean;
            if (value is string text && bool.TryParse(text, out bool parsed)) return parsed;
            try { return Convert.ToDouble(value) != 0; }
            catch { return false; }
        }

        private static bool IsFalse(object? value)
        {
            if (value is bool boolean) return !boolean;
            if (value is string text && bool.TryParse(text, out bool parsed)) return !parsed;
            try { return Convert.ToDouble(value) == 0; }
            catch { return false; }
        }
    }

    internal sealed class OptionsSnapshot
    {
        public OptionsSnapshot(NoteMotionControl.Options options)
        {
            options.Validate();
            // Mode Off is a hard disable even when the global toggle remains
            // enabled.  This is also the cache and score-gate contract.
            Enabled = options.Enabled && options.ReductionMode != ReductionMode.Off;
            ReductionMode = options.ReductionMode;
            MaxNps = options.MaxNps;
            SameHandMinIntervalMs = options.SameHandMinIntervalMs;
            RetainPercent = options.RetainPercent;
            WindowSeconds = options.WindowSeconds;
        }

        public bool Enabled { get; }
        public ReductionMode ReductionMode { get; }
        public float MaxNps { get; }
        public float SameHandMinIntervalMs { get; }
        public float RetainPercent { get; }
        public float WindowSeconds { get; }
    }

}


