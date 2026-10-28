using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using NoteMotionControl;
using NoteMotionControl.Reduction;

namespace NoteMotionControl.Reduction.Tests
{
    /// <summary>
    /// Framework-free regression checks.  The local test command compiles this
    /// file with ReductionAlgorithm.cs and invokes RunAll, so algorithm changes
    /// can be checked without loading Unity or the game process.
    /// </summary>
    public static class AlgorithmTests
    {
        public static void RunAll()
        {
            PercentageUsesWeightedErrorDiffusion();
            DensityUsesPlaybackSeconds();
            ReducerUsesConfiguredDensityMode();
            PeakNpsUsesTheSelectorWindow();
            SameHandIntervalsAreIndependent();
            BurstGroupRemainsAtomicWhenTooDense();
            EmptySelectionRetainsMinimumGroup();
            BurstLinksUseTailInclusiveTiming();
            EnabledProfilesBypassTransformedCache();
            OptionsSnapshotFreezesGlobalSettings();
            TransformGenerationRejectsStaleOutcome();
            StaleFlowedSessionCannotFallBackToCurrentGeneration();
            PracticeSpeedIsPartOfTransformSession();
            RetainedArcRestoresScoringMetadata();
            NativeBurstTypesRemainScoringTypes();
            NativeSliderConnectionsUseGridAndCopyFlagsAreRebuilt();
            NativeBurstLinksUseHeadGridUntilTail();
            FullReducerRetainsArcScoringAndDoesNotMutateSource();
            FullReducerRebuildsSliderConnectionsAfterDrop();
            FullReducerKeepsBurstChainAtomic();
            FullReducerRejectsDamagedMetadata();
                    }

        private static void PercentageUsesWeightedErrorDiffusion()
        {
            var groups = Enumerable.Range(0, 4)
                .Select(i => new CandidateGroup(i, 2, new[] { new ActionPoint(i, ActionHand.Left) }))
                .ToArray();
            SelectionResult result = SelectionAlgorithm.SelectPercentage(groups, 0.5);
            int keptWeight = groups.Where(g => result.KeptGroupIds.Contains(g.Id)).Sum(g => g.Weight);
            Assert(keptWeight == 4, "50% of four weight-2 groups must keep weight 4.");
            Assert(result.KeptGroupIds.Contains(0) && result.KeptGroupIds.Contains(2),
                "Weighted ties must resolve deterministically to alternating groups.");
        }

        private static void DensityUsesPlaybackSeconds()
        {
            var groups = Enumerable.Range(0, 8)
                .Select(i => new CandidateGroup(i, 1,
                    new[] { new ActionPoint(i * 0.25, ActionHand.Left) }))
                .ToArray();
            // The caller has already divided chart time by song speed.  At 2x,
            // the same source timestamps would be 0.125 seconds apart; this
            // fixture directly verifies the cap consumes the supplied time.
            var fast = groups.Select(g => new CandidateGroup(g.Id, g.Weight,
                new[] { new ActionPoint(g.Actions[0].Time / 2, ActionHand.Left) })).ToArray();
            SelectionResult result = SelectionAlgorithm.SelectDensity(fast, 4, 0, 1);
            var keptTimes = fast.Where(g => result.KeptGroupIds.Contains(g.Id))
                .Select(g => g.Actions[0].Time).ToArray();
            for (int i = 0; i < keptTimes.Length; i++)
            {
                int count = keptTimes.Count(t => t >= keptTimes[i] - 1 - 1e-9 && t <= keptTimes[i] + 1e-9);
                Assert(count <= 4, "Playback-second density window must stay at or below 4 NPS.");
            }
            Assert(keptTimes.Length < fast.Length, "2x playback timestamps must affect density selection.");
        }

        private static void ReducerUsesConfiguredDensityMode()
        {
            BeatmapData source = CreateBeatmap(
                BasicNote(0f, 0, ColorType.ColorA),
                BasicNote(0.1f, 1, ColorType.ColorA),
                BasicNote(0.2f, 2, ColorType.ColorA));
            var options = new Options
            {
                Enabled = true,
                ReductionMode = ReductionMode.Density,
                MaxNps = 1f,
                SameHandMinIntervalMs = 0f,
                WindowSeconds = 1f,
            };
            ReductionApplication application = BeatmapReducer.Apply(
                source, new OptionsSnapshot(options), "density-mode", 1f);
            Assert(application.Outcome.Applied &&
                   application.Outcome.ReductionMode == ReductionMode.Density,
                "The reducer must apply the captured density mode.");
            Assert(application.Outcome.FilteredScoringObjects < application.Outcome.OriginalScoringObjects,
                "A dense chart must be reduced by the density mode.");
        }

        private static void SameHandIntervalsAreIndependent()
        {
            var groups = new[]
            {
                new CandidateGroup(0, 1, new[] { new ActionPoint(0, ActionHand.Left) }),
                new CandidateGroup(1, 1, new[] { new ActionPoint(0, ActionHand.Right) }),
                new CandidateGroup(2, 1, new[] { new ActionPoint(0.1, ActionHand.Left) }),
                new CandidateGroup(3, 1, new[] { new ActionPoint(0.1, ActionHand.Right) }),
                new CandidateGroup(4, 1, new[] { new ActionPoint(0.3, ActionHand.Left) }),
            };
            SelectionResult result = SelectionAlgorithm.SelectDensity(groups, 10, 0.25, 1);
            Assert(result.KeptGroupIds.Contains(0) && result.KeptGroupIds.Contains(1),
                "A row chord may retain one action per hand.");
            Assert(!result.KeptGroupIds.Contains(2) && !result.KeptGroupIds.Contains(3),
                "Each hand rejects its own short interval independently.");
            Assert(result.KeptGroupIds.Contains(4), "A later left-hand action may be retained.");
        }

        private static void PeakNpsUsesTheSelectorWindow()
        {
            var actions = new[]
            {
                new ActionPoint(0, ActionHand.Left),
                new ActionPoint(0.5, ActionHand.Right),
                new ActionPoint(1.0, ActionHand.Left),
            };
            double peak = DensityStatistics.PeakNps(actions, 1.0);
            AssertNear(3, peak, "peak NPS includes both window endpoints");
            double shorter = DensityStatistics.PeakNps(actions, 0.5);
            AssertNear(2.0 / 0.5, shorter, "peak NPS follows configured window");
        }

        private static void BurstGroupRemainsAtomicWhenTooDense()
        {
            var chain = new CandidateGroup(
                7,
                5,
                Enumerable.Range(0, 5).Select(i => new ActionPoint(i * 0.1, ActionHand.Right)));
            SelectionResult result = SelectionAlgorithm.SelectDensity(new[] { chain }, 4, 0, 1);
            Assert(result.KeptGroupIds.Contains(7), "An otherwise empty chart retains one valid atomic chain.");
            Assert(result.UsedFallback, "The atomic chain must be reported as a minimum-group fallback.");
        }

        private static void EmptySelectionRetainsMinimumGroup()
        {
            var groups = new[]
            {
                new CandidateGroup(3, 2, new[] { new ActionPoint(3, ActionHand.Left) }),
                new CandidateGroup(4, 1, new[] { new ActionPoint(4, ActionHand.Right) }),
            };
            SelectionResult result = SelectionAlgorithm.SelectPercentage(groups, 0);
            Assert(result.UsedFallback && result.KeptGroupIds.Contains(4),
                "Zero percentage still retains the smallest valid group.");
        }

        private static void BurstLinksUseTailInclusiveTiming()
        {
            AssertNear(1.0 / 3.0, BeatmapTiming.BurstLinkTime(0, 1, 1, 4), "burst link 1");
            AssertNear(2.0 / 3.0, BeatmapTiming.BurstLinkTime(0, 1, 2, 4), "burst link 2");
            AssertNear(1.0, BeatmapTiming.BurstLinkTime(0, 1, 3, 4), "burst link 3");
        }

        private static void EnabledProfilesBypassTransformedCache()
        {
            Assert(TransformPolicy.ShouldBypassTransformedCache(true),
                "Enabled reduction profiles must bypass the native transformed-data cache.");
            Assert(!TransformPolicy.ShouldBypassTransformedCache(false),
                "Disabled reduction profiles may use the native transformed-data cache.");
        }

        private static void OptionsSnapshotFreezesGlobalSettings()
        {
            var options = new Options
            {
                Enabled = true,
                ReductionMode = ReductionMode.Density,
                MaxNps = 4f,
                SameHandMinIntervalMs = 250f,
                RetainPercent = 50f,
                WindowSeconds = 1f,
            };
            OptionsSnapshot snapshot = new OptionsSnapshot(options);
            options.Enabled = false;
            options.ReductionMode = ReductionMode.Percentage;
            options.MaxNps = 99f;
            Assert(snapshot.Enabled && snapshot.ReductionMode == ReductionMode.Density &&
                   Math.Abs(snapshot.MaxNps - 4f) < 0.0001f,
                "A chart-load snapshot must not re-resolve or observe later global setting changes.");

            options.MaxNps = 4f;
            options.ReductionMode = ReductionMode.Off;
            OptionsSnapshot disabled = new OptionsSnapshot(options);
            Assert(!disabled.Enabled && disabled.ReductionMode == ReductionMode.Off,
                "Mode Off must disable reduction even when the global toggle is enabled.");
        }

        private static void TransformGenerationRejectsStaleOutcome()
        {
            var oldSession = new TransformSession("chart", 1, 1f);
            var currentSession = new TransformSession("chart", 2, 2f);
            Assert(!TransformPolicy.IsCurrent(oldSession, currentSession),
                "An older async transform must not publish its outcome into a newer chart generation.");
            Assert(TransformPolicy.IsCurrent(currentSession, currentSession),
                "The current chart generation must remain publishable.");
        }

        private static void StaleFlowedSessionCannotFallBackToCurrentGeneration()
        {
            var oldSession = new TransformSession("chart", 1, 1f);
            var currentSession = new TransformSession("chart", 2, 1.5f);
            Assert(!TransformPolicy.TryResolveSession(
                       oldSession, currentSession, "chart", out _),
                "A stale same-chart AsyncLocal session must be rejected instead of relabeled as current.");
            Assert(TransformPolicy.TryResolveSession(
                       null, currentSession, "chart", out TransformSession resolved) &&
                   resolved == currentSession,
                "A callback without flowed state may use the current same-chart session.");
        }

        private static void PracticeSpeedIsPartOfTransformSession()
        {
            var normal = new TransformSession("chart", 1, 1f);
            var practice = new TransformSession("chart", 2, 1.5f);
            Assert(normal != practice,
                "A practice-speed reload must receive a distinct transform generation.");
            Assert(Math.Abs(practice.PlaybackSpeed - 1.5f) < 0.0001f,
                "The transform snapshot must carry the effective practice speed.");
        }

        private static void RetainedArcRestoresScoringMetadata()
        {
            NoteData original = NoteData.CreateBasicNoteData(
                1f,
                1f,
                0,
                0,
                NoteLineLayer.Base,
                ColorType.ColorA,
                NoteCutDirection.Down);
            original.MarkAsSliderHead();
            NoteData copy = (NoteData)original.GetCopy();

            Assert(!copy.isArcHead && copy.scoringType == NoteData.ScoringType.ArcHead,
                "GetCopy must reproduce the current private-flag loss used by the regression.");
            ArcMetadata.Restore(copy, true, false);
            Assert(copy.isArcHead && !copy.isArcTail && copy.scoringType == NoteData.ScoringType.ArcHead,
                "Retained arc head must restore its flag and scoring type.");

            ArcMetadata.Restore(copy, true, true);
            Assert(copy.isArcHead && copy.isArcTail &&
                   copy.scoringType == NoteData.ScoringType.ArcHeadArcTail,
                "Two retained endpoint roles must use the combined arc scoring type.");
        }

        private static void NativeBurstTypesRemainScoringTypes()
        {
            NoteData head = NoteData.CreateBurstSliderNoteData(
                1f, 1f, 0, 0, NoteLineLayer.Base, NoteLineLayer.Base,
                ColorType.ColorA, NoteCutDirection.Down, 1f);
            head.ChangeToBurstSliderHead();
            Assert(head.gameplayType == NoteData.GameplayType.BurstSliderHead &&
                   head.scoringType == NoteData.ScoringType.ChainHead,
                "The native Burst head must retain its ChainHead gameplay and scoring types.");
            ArcMetadata.Restore(head, true, false);
            Assert(head.isArcHead &&
                   head.scoringType == NoteData.ScoringType.ChainHeadArcHead,
                "A retained Burst head arc must use ChainHeadArcHead scoring.");

            NoteData link = NoteData.CreateBurstSliderNoteData(
                1.25f, 1.25f, 0, 0, NoteLineLayer.Base, NoteLineLayer.Base,
                ColorType.ColorA, NoteCutDirection.Down, 1f);
            ArcMetadata.NormalizeBurstElement(link);
            Assert(link.gameplayType == NoteData.GameplayType.BurstSliderElement &&
                   link.scoringType == NoteData.ScoringType.ChainLink,
                "A retained Burst link whose arc was removed must remain a ChainLink.");
        }

        private static void NativeSliderConnectionsUseGridAndCopyFlagsAreRebuilt()
        {
            SliderData head = SliderData.CreateSliderData(
                ColorType.ColorA, 1f, 1f, 0, 0, NoteLineLayer.Base, NoteLineLayer.Base,
                1f, NoteCutDirection.Down, 1f, 0, 0, NoteLineLayer.Base,
                NoteLineLayer.Base, 1f, NoteCutDirection.Down, SliderMidAnchorMode.Straight);
            SliderData tail = SliderData.CreateSliderData(
                ColorType.ColorA, 1f, 1f, 0, 1, NoteLineLayer.Base, NoteLineLayer.Base,
                1f, NoteCutDirection.Down, 1f, 0, 0, NoteLineLayer.Base,
                NoteLineLayer.Base, 1f, NoteCutDirection.Down, SliderMidAnchorMode.Straight);
            head.SetHasHeadNote(false);
            head.SetHasTailNote(false);
            tail.SetHasHeadNote(false);
            tail.SetHasTailNote(false);

            Assert(NativeStructurePolicy.IsNativeNormalSliderConnection(head, tail),
                "Native slider connection must use the head-to-tail grid overlap.");

            head.SetHasTailSlider(true);
            tail.SetHasHeadSlider(true);
            SliderData copy = (SliderData)head.GetCopy();
            Assert(!copy.hasHeadSlider && !copy.hasTailSlider,
                "SliderData.GetCopy must be treated as dropping native connection flags.");

            head.SetHasTailNote(true);
            Assert(!NativeStructurePolicy.IsNativeNormalSliderConnection(head, tail),
                "A native connection is blocked when the first slider has a tail note.");
        }

        private static void NativeBurstLinksUseHeadGridUntilTail()
        {
            SliderData burst = SliderData.CreateBurstSliderData(
                ColorType.ColorA, 2f, 3f, 1, 1, NoteLineLayer.Base, NoteLineLayer.Base,
                NoteCutDirection.Down, 3f, 0, 2, NoteLineLayer.Top, NoteLineLayer.Top,
                4, 1f);

            Assert(NativeStructurePolicy.BurstLinkLineIndex(burst, 1) == burst.headLineIndex &&
                   NativeStructurePolicy.BurstLinkLayer(burst, 1) == burst.headLineLayer,
                "Native Burst links before the final slice use the head grid position.");
            Assert(NativeStructurePolicy.BurstLinkLineIndex(burst, 2) == burst.headLineIndex &&
                   NativeStructurePolicy.BurstLinkLayer(burst, 2) == burst.headLineLayer,
                "Native Burst middle links use the head grid position.");
            Assert(NativeStructurePolicy.BurstLinkLineIndex(burst, 3) == burst.tailLineIndex &&
                   NativeStructurePolicy.BurstLinkLayer(burst, 3) == burst.tailLineLayer,
                "Native Burst final link uses the tail grid position.");
        }

        private static void FullReducerRetainsArcScoringAndDoesNotMutateSource()
        {
            BeatmapData source = CreateBeatmap(
                BasicNote(1f, 0, ColorType.ColorA),
                BasicNote(2f, 2, ColorType.ColorB),
                BasicNote(3f, 1, ColorType.ColorA),
                NormalArc(1f, 3f, 0, 1, ColorType.ColorA));
            NoteData sourceHead = source.allBeatmapDataItems.OfType<NoteData>()
                .Single(note => Math.Abs(note.time - 1f) < 0.0001f);
            NoteData sourceTail = source.allBeatmapDataItems.OfType<NoteData>()
                .Single(note => Math.Abs(note.time - 3f) < 0.0001f);
            NoteData sourceExtra = source.allBeatmapDataItems.OfType<NoteData>()
                .Single(note => Math.Abs(note.time - 2f) < 0.0001f);
            sourceHead.MarkAsSliderHead();
            sourceTail.MarkAsSliderTail();
            Assert(sourceHead.isArcHead && sourceTail.isArcTail &&
                   sourceExtra.gameplayType == NoteData.GameplayType.Normal,
                "The real BeatmapData fixture must contain the native arc metadata before filtering.");

            var options = new Options
            {
                Enabled = true,
                ReductionMode = ReductionMode.Percentage,
                RetainPercent = 50f,
            };
            ReductionApplication application = BeatmapReducer.Apply(
                source, new OptionsSnapshot(options), "integration-arc", 1f);
            BeatmapData filtered = (BeatmapData)application.Data;

            var filteredNotes = filtered.allBeatmapDataItems.OfType<NoteData>().ToArray();
            Assert(filteredNotes.Length == 2,
                "The percentage fixture must drop the independent middle note only.");
            NoteData filteredHead = filteredNotes.Single(note => Math.Abs(note.time - 1f) < 0.0001f);
            NoteData filteredTail = filteredNotes.Single(note => Math.Abs(note.time - 3f) < 0.0001f);
            Assert(filteredHead.isArcHead && filteredHead.scoringType == NoteData.ScoringType.ArcHead,
                "A retained arc head must keep its native arc scoring type.");
            Assert(filteredTail.isArcTail && filteredTail.scoringType == NoteData.ScoringType.ArcTail,
                "A retained arc tail must keep its native arc scoring type.");
            Assert(filtered.allBeatmapDataItems.OfType<SliderData>().Count() == 1,
                "A normal arc with both retained endpoints must remain in the filtered copy.");
            Assert(source.allBeatmapDataItems.Count == 4 && sourceExtra.scoringType == NoteData.ScoringType.Normal &&
                   sourceHead.isArcHead && sourceTail.isArcTail,
                "Apply must not mutate the source BeatmapData or its NoteData metadata.");
        }

        private static void FullReducerRebuildsSliderConnectionsAfterDrop()
        {
            BeatmapData source = CreateBeatmap(
                BasicNote(2f, 2, ColorType.ColorA),
                BasicNote(4f, 0, ColorType.ColorA),
                ConnectionSlider(1f, 0, 0, 1f, false),
                ConnectionSlider(1f, 1, 0, 4f, true));
            SliderData[] sourceSliders = source.allBeatmapDataItems.OfType<SliderData>().ToArray();
            SliderData sourceHeadSlider = sourceSliders.Single(slider => slider.headLineIndex == 0);
            SliderData sourceTailSlider = sourceSliders.Single(slider => slider.headLineIndex == 1);
            source.allBeatmapDataItems.OfType<NoteData>()
                .Single(note => Math.Abs(note.time - 4f) < 0.0001f)
                .MarkAsSliderTail();
            sourceHeadSlider.SetHasTailSlider(true);
            sourceTailSlider.SetHasHeadSlider(true);

            var dropTail = new Options
            {
                Enabled = true,
                ReductionMode = ReductionMode.Percentage,
                RetainPercent = 50f,
            };
            ReductionApplication dropped = BeatmapReducer.Apply(
                source, new OptionsSnapshot(dropTail), "integration-connection-drop", 1f);
            SliderData[] droppedSliders = dropped.Data.allBeatmapDataItems.OfType<SliderData>().ToArray();
            Assert(droppedSliders.Length == 1 && droppedSliders[0].headLineIndex == 0,
                "The dependent normal slider must be removed as one arc relation.");
            Assert(!droppedSliders[0].hasHeadSlider && !droppedSliders[0].hasTailSlider,
                "A retained slider must clear its flag when its adjacent slider was removed (head=" +
                droppedSliders[0].hasHeadSlider + ", tail=" + droppedSliders[0].hasTailSlider + ").");

            var keepAll = new Options
            {
                Enabled = true,
                ReductionMode = ReductionMode.Percentage,
                RetainPercent = 100f,
            };
            ReductionApplication retained = BeatmapReducer.Apply(
                source, new OptionsSnapshot(keepAll), "integration-connection-keep", 1f);
            SliderData[] retainedSliders = retained.Data.allBeatmapDataItems.OfType<SliderData>().ToArray();
            SliderData retainedHeadSlider = retainedSliders.Single(slider => slider.headLineIndex == 0);
            SliderData retainedTailSlider = retainedSliders.Single(slider => slider.headLineIndex == 1);
            Assert(retainedHeadSlider.hasTailSlider && retainedTailSlider.hasHeadSlider,
                "Retained normal slider connections must be rebuilt in the filtered copy.");
            Assert(sourceHeadSlider.hasTailSlider && sourceTailSlider.hasHeadSlider,
                "Repeated Apply calls must continue to see the unmodified source connection flags.");
        }

        private static void FullReducerKeepsBurstChainAtomic()
        {
            SliderData burst = SliderData.CreateBurstSliderData(
                ColorType.ColorA, 1f, 4f, 0, 0, NoteLineLayer.Base, NoteLineLayer.Base,
                NoteCutDirection.Down, 4f, 0, 1, NoteLineLayer.Base, NoteLineLayer.Base,
                4, 1f);
            // The final transformed copy can reach the reducer before the
            // native time-row processor sets this flag; the matching head
            // NoteData still makes the chain valid.
            burst.SetHasHeadNote(false);
            NoteData head = NoteData.CreateBurstSliderNoteData(
                1f, 1f, 0, 0, NoteLineLayer.Base, NoteLineLayer.Base,
                ColorType.ColorA, NoteCutDirection.Down, 1f);
            NoteData independent = BasicNote(0f, 3, ColorType.ColorB);
            BeatmapData source = CreateBeatmap(independent, head, burst);
            NoteData sourceHead = source.allBeatmapDataItems.OfType<NoteData>()
                .Single(note => Math.Abs(note.time - 1f) < 0.0001f);
            sourceHead.ChangeToGameNote();
            source.allBeatmapDataItems.OfType<SliderData>().Single().SetHasHeadNote(false);
            Assert(source.allBeatmapDataItems.OfType<NoteData>().Any(note =>
                       Math.Abs(note.time - 1f) < 0.0001f &&
                       note.gameplayType == NoteData.GameplayType.Normal),
                "The real Burst fixture must retain an unmarked source head note.");

            var options = new Options
            {
                Enabled = true,
                ReductionMode = ReductionMode.Percentage,
                RetainPercent = 10f,
            };
            ReductionApplication application = BeatmapReducer.Apply(
                source, new OptionsSnapshot(options), "integration-burst", 1f);
            Assert(application.Data.allBeatmapDataItems.OfType<SliderData>().Count() == 0,
                "A rejected Burst group must remove its SliderData together with every link.");
            Assert(application.Outcome.OriginalScoringObjects == 5 &&
                   application.Outcome.FilteredScoringObjects == 1,
                "Generated Burst links must count as scoring objects even though they are spawned later.");
            Assert(application.Data.allBeatmapDataItems.OfType<NoteData>().Count() == 1 &&
                   application.Data.allBeatmapDataItems.OfType<NoteData>().Single().time == 0f,
                "A Burst chain must not be partially filtered into orphan notes.");
            Assert(source.allBeatmapDataItems.OfType<NoteData>().Count() == 2 &&
                   source.allBeatmapDataItems.OfType<SliderData>().Count() == 1,
                "Burst filtering must leave the source chain intact.");

            var keepAll = new Options
            {
                Enabled = true,
                ReductionMode = ReductionMode.Percentage,
                RetainPercent = 100f,
            };
            ReductionApplication retained = BeatmapReducer.Apply(
                source, new OptionsSnapshot(keepAll), "integration-burst-head-normalization", 1f);
            NoteData retainedHead = retained.Data.allBeatmapDataItems.OfType<NoteData>()
                .Single(note => Math.Abs(note.time - 1f) < 0.0001f);
            Assert(retainedHead.gameplayType == NoteData.GameplayType.BurstSliderHead &&
                   retainedHead.scoringType == NoteData.ScoringType.ChainHead,
                "A valid matched Burst head must be normalized even when the source flag was false.");
        }

        private static void FullReducerRejectsDamagedMetadata()
        {
            BeatmapData noHead = new BeatmapData(4);
            SliderData burst = SliderData.CreateBurstSliderData(
                ColorType.ColorA, 1f, 2f, 0, 0, NoteLineLayer.Base, NoteLineLayer.Base,
                NoteCutDirection.Down, 2f, 0, 0, NoteLineLayer.Base, NoteLineLayer.Base,
                2, 1f);
            noHead.allBeatmapDataItems.AddLast(burst);
            AssertTransformRejected(noHead, "a Burst slider without a declared head");

            BeatmapData nonFinite = new BeatmapData(4);
            nonFinite.allBeatmapDataItems.AddLast(BasicNote(float.NaN, 0, ColorType.ColorA));
            AssertTransformRejected(nonFinite, "a scoring note with a non-finite time");
        }

        private static void AssertTransformRejected(BeatmapData source, string caseName)
        {
            try
            {
                BeatmapReducer.Apply(
                    source,
                    new OptionsSnapshot(new Options { Enabled = true, ReductionMode = ReductionMode.Percentage, RetainPercent = 50f }),
                    "integration-invalid", 1f);
                throw new InvalidOperationException("Expected rejection for " + caseName + ".");
            }
            catch (ReductionTransformException error)
            {
                Assert(error.Category == "density.endpoint_invalid",
                    "Invalid " + caseName + " must use the stable endpoint-invalid category.");
            }
        }

        private static BeatmapData CreateBeatmap(params BeatmapDataItem[] items)
        {
            var beatmap = new BeatmapData(4);
            foreach (BeatmapDataItem item in items)
                beatmap.AddBeatmapObjectDataInOrder((BeatmapObjectData)item);
            beatmap.ProcessAndSortBeatmapData();
            return beatmap;
        }

        private static NoteData BasicNote(float time, int lineIndex, ColorType color)
        {
            return NoteData.CreateBasicNoteData(
                time, time, 0, lineIndex, NoteLineLayer.Base, color, NoteCutDirection.Down);
        }

        private static SliderData NormalArc(
            float headTime,
            float tailTime,
            int headLineIndex,
            int tailLineIndex,
            ColorType color)
        {
            SliderData slider = SliderData.CreateSliderData(
                color, headTime, headTime, 0, headLineIndex, NoteLineLayer.Base, NoteLineLayer.Base,
                1f, NoteCutDirection.Down, tailTime, 0, tailLineIndex, NoteLineLayer.Base,
                NoteLineLayer.Base, 1f, NoteCutDirection.Down, SliderMidAnchorMode.Straight);
            slider.SetHasHeadNote(true);
            slider.SetHasTailNote(true);
            return slider;
        }

        private static SliderData ConnectionSlider(
            float time,
            int headLineIndex,
            int tailLineIndex,
            float tailTime,
            bool hasTailNote)
        {
            SliderData slider = SliderData.CreateSliderData(
                ColorType.ColorA, time, time, 0, headLineIndex, NoteLineLayer.Base, NoteLineLayer.Base,
                1f, NoteCutDirection.Down, tailTime, 0, tailLineIndex, NoteLineLayer.Base,
                NoteLineLayer.Base, 1f, NoteCutDirection.Down, SliderMidAnchorMode.Straight);
            slider.SetHasHeadNote(false);
            slider.SetHasTailNote(hasTailNote);
            return slider;
        }

        private static NoteData BurstLink(float time, int lineIndex, int layer)
        {
            NoteData link = NoteData.CreateBurstSliderNoteData(
                time, time, 0, lineIndex, (NoteLineLayer)layer, (NoteLineLayer)layer,
                ColorType.ColorA, NoteCutDirection.Down, 1f);
            ArcMetadata.NormalizeBurstElement(link);
            return link;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void AssertNear(double expected, double actual, string name)
        {
            if (Math.Abs(expected - actual) > 0.0001)
                throw new InvalidOperationException(name + ": expected " + expected + ", got " + actual);
        }
    }
}


