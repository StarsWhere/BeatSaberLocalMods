using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace NoteMotionControl
{
    internal static class SpeedRuntime
    {
        private static readonly Dictionary<VariableMovementDataProvider, SpeedPolicy> policies =
            new Dictionary<VariableMovementDataProvider, SpeedPolicy>();

        internal static bool ValidateInterface(out string error)
        {
            bool valid = AccessTools.Method(typeof(VariableMovementDataProvider), "Init") != null &&
                AccessTools.Method(typeof(VariableMovementDataProvider), "ManualUpdate", new[] { typeof(float) }) != null;
            foreach (string field in new[] { "_halfJumpDuration", "_jumpDuration", "_spawnAheadTime", "_waitingDuration", "_prevNoteJumpMovementSpeed" })
                valid &= AccessTools.Field(typeof(VariableMovementDataProvider), field)?.FieldType == typeof(float);
            error = valid ? string.Empty : "The native movement interface is unavailable; original movement was retained.";
            return valid;
        }

        internal static bool TryGetPolicy(VariableMovementDataProvider provider, out SpeedPolicy policy) => policies.TryGetValue(provider, out policy);
        internal static void Clear() => policies.Clear();

        internal static void Initialize(VariableMovementDataProvider provider, float halfBeats, float maxDistance,
            float initial, float minimum, float bpm, ref BeatmapObjectSpawnMovementData.NoteJumpValueType type, ref float value)
        {
            policies.Remove(provider);
            if (!RuntimeState.GameplayActive || !RuntimeState.MovementInterfaceReady || RuntimeState.PreserveAuthoredMotion ||
                !RuntimeState.OwnsProvider(provider) || !SpeedPolicy.IsRequested(RuntimeState.ActiveOptions)) return;
            if (provider.GetType() != typeof(VariableMovementDataProvider) || !SpeedPolicy.Finite(bpm) || bpm <= 0f ||
                !SpeedPolicy.Finite(initial) || initial <= 0f || !SpeedPolicy.Finite(minimum) || !SpeedPolicy.Finite(initial + minimum) ||
                !SpeedPolicy.Finite(value) || !SpeedPolicy.Finite(halfBeats) || halfBeats <= 0f || !SpeedPolicy.Finite(maxDistance) || maxDistance <= 0f)
            {
                RuntimeState.SetNotice("The native movement route could not be adjusted; original movement was retained.");
                return;
            }
            Options options = RuntimeState.ActiveOptions;
            bool fixedDuration = type == BeatmapObjectSpawnMovementData.NoteJumpValueType.JumpDuration;
            float beat = 60f / bpm;
            float outputInitial = options.NjsMode == NjsMode.Override ? options.OverrideNjs :
                options.NjsMode == NjsMode.Multiplier ? initial * options.SpeedMultiplier : initial;
            if (options.ApplicationPolicy == ApplicationPolicy.OnlyReduce) outputInitial = Math.Min(initial, outputInitial);
            float originalHalf = fixedDuration ? value : beat * CoreMathUtils.CalculateHalfJumpDurationInBeats(halfBeats, maxDistance, initial, beat, value);
            float outputHalf = fixedDuration ? value : beat * CoreMathUtils.CalculateHalfJumpDurationInBeats(halfBeats, maxDistance, outputInitial, beat, value);
            if (!SpeedPolicy.TryCreate(options, initial, minimum, originalHalf, outputHalf, fixedDuration, out SpeedPolicy policy) ||
                !RuntimeState.HasSafeSpeedEvents(options.NjsMode == NjsMode.Multiplier ? options.SpeedMultiplier : 1f)) return;

            bool changes = policy.ChangesAt(initial) || policy.ChangesAt(policy.MinimumOriginal);
            if (RuntimeState.ActiveSetup?.transformedBeatmapData != null)
                foreach (BeatmapDataItem item in RuntimeState.ActiveSetup.transformedBeatmapData.allBeatmapDataItems)
                    if (item is NoteJumpSpeedEventData speed)
                    {
                        float original = Math.Max(0.01f, initial + speed.relativeNoteJumpSpeed);
                        if (!SpeedPolicy.Finite(original)) return;
                        changes |= policy.ChangesAt(original);
                    }
            // Acquire the single gameplay gate before changing any Init input.
            // Failure leaves the entire native route intact and the next load
            // may retry. Truly identity settings leave it intact as well.
            if (!changes || !RuntimeState.EnsureMotionTicket("Note training settings changed movement or timing.")) return;
            policies.Add(provider, policy);
            provider._prevNoteJumpMovementSpeed = float.NaN;
            // Keep the original initial NJS and native interpolation as the
            // canonical source; duration is resolved before each native update.
            type = BeatmapObjectSpawnMovementData.NoteJumpValueType.JumpDuration;
            value = policy.Half(initial, policy.Speed(initial));
        }

        internal static float ApplySpeed(float original, VariableMovementDataProvider provider)
        {
            if (!policies.TryGetValue(provider, out SpeedPolicy policy)) return original;
            float output = policy.Speed(original);
            float half = policy.Half(original, output);
            if (!SpeedPolicy.Finite(output) || output <= 0f || !SpeedPolicy.Finite(half) || half <= 0f)
                throw new InvalidOperationException("Invalid native movement parameters.");
            if (Math.Abs(provider._halfJumpDuration - half) > 0.000001f)
            {
                // The native early-return compares speed only; a timing-only
                // change must recalculate distance, positions and waiting too.
                provider._prevNoteJumpMovementSpeed = float.NaN;
                provider._halfJumpDuration = half;
                provider._jumpDuration = half * 2f;
            }
            return output;
        }

        internal static void AfterInitialization(VariableMovementDataProvider provider)
        {
            if (!policies.TryGetValue(provider, out SpeedPolicy policy)) return;
            provider._spawnAheadTime = policy.SpawnAhead;
            provider._waitingDuration = policy.SpawnAhead - provider.moveDuration - provider._halfJumpDuration;
        }
    }

    [HarmonyPatch(typeof(VariableMovementDataProvider), "Init")]
    internal static class NativeSpeedInitializationPatch
    {
        private static void Prefix(VariableMovementDataProvider __instance, float startHalfJumpDurationInBeats,
            float maxHalfJumpDistance, float noteJumpMovementSpeed, float minRelativeNoteJumpSpeed, float bpm,
            ref BeatmapObjectSpawnMovementData.NoteJumpValueType noteJumpValueType, ref float noteJumpValue)
        {
            EarlySession.EnsurePrepared();
            SpeedRuntime.Initialize(__instance, startHalfJumpDurationInBeats, maxHalfJumpDistance,
                noteJumpMovementSpeed, minRelativeNoteJumpSpeed, bpm, ref noteJumpValueType, ref noteJumpValue);
        }
        private static void Postfix(VariableMovementDataProvider __instance) => SpeedRuntime.AfterInitialization(__instance);
    }

    [HarmonyPatch(typeof(VariableMovementDataProvider), "ManualUpdate")]
    internal static class NativeSpeedCurvePatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo max = AccessTools.Method(typeof(Mathf), nameof(Mathf.Max), new[] { typeof(float), typeof(float) });
            MethodInfo apply = AccessTools.Method(typeof(SpeedRuntime), nameof(SpeedRuntime.ApplySpeed));
            int matches = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                yield return instruction;
                if (!instruction.Calls(max)) continue;
                matches++;
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Call, apply);
            }
            if (matches != 1) throw new InvalidOperationException("The native NJS curve interface changed.");
        }
    }
}
