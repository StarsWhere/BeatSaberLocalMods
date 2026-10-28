using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CustomJSONData.CustomBeatmap;
using CustomJSONData.CustomBeatmap.BaseData;

namespace EffectModsSmoke;

public sealed partial class Runner
{
    void VerifyV4LoadedEvents(IReadonlyBeatmapData beatmap)
    {
        if (!CustomLevel || !((string)expectedData?["version"] ?? string.Empty).StartsWith("4.", StringComparison.Ordinal)) return;
        int originalNjs = (expectedData["njsEvents"] as Newtonsoft.Json.Linq.JArray)?.Count ?? 0;
        if (originalNjs > 0)
        {
            int loaded = beatmap.allBeatmapDataItems.OfType<NoteJumpSpeedEventData>().Count();
            Check("v4-native-speed-events-retained", loaded == originalNjs,
                "source=" + originalNjs + "; loaded=" + loaded);
        }
        int colorBoxes = (expectedLightshowData?["lightColorEventBoxes"] as Newtonsoft.Json.Linq.JArray)?.Count ?? 0;
        if (colorBoxes > 0)
        {
            var lights = beatmap.allBeatmapDataItems.OfType<LightColorBeatmapEventData>().ToArray();
            Check("v4-native-lightshow-events-loaded", lights.Length > 0 && lights.All(value => value.GetType() == typeof(LightColorBeatmapEventData)),
                "sourceColorBoxes=" + colorBoxes + "; loadedNativeColorEvents=" + lights.Length);
        }
    }

    // May run in the menu, before a v4 loader reaches event-box unpacking.
    void VerifyV4LightshowConversion()
    {
        try
        {
            Type patch = typeof(CustomBeatmapData).Assembly.GetType("CustomJSONData.HarmonyPatches.EventDataBoxUnpackCustomify", true);
            MethodInfo wrapper = patch.GetMethod("ConvertLightColorBeatmapEvent", BindingFlags.Static | BindingFlags.NonPublic);
            if (wrapper == null) throw new MissingMethodException(patch.FullName, "ConvertLightColorBeatmapEvent");

            int nativeCases = 0, nativeEvents = 0;
            foreach (bool ignore in new[] { false, true })
            foreach (bool previous in new[] { false, true })
            foreach (float? nextBrightness in new float?[] { null, 0.75f })
            {
                IBeatmapLightEventConverter native = new BeatmapLightEventConverterNoConvert(ignore, false, true);
                object[] arguments = V4LightArguments(native, previous, nextBrightness,
                    new LightColorBaseData(4f, previous, EaseType.InOutSine, EnvironmentColorType.Color1, 0.6f, 8, 0.2f, true));
                BeatmapEventData[] expected = native.ConvertLightColorBeatmapEvent(37, 12.5f, 2, 3, previous,
                    EaseType.InOutSine, EnvironmentColorType.Color1, 0.6f, 8, 0.2f, true, nextBrightness).ToArray();
                BeatmapEventData[] actual = InvokeV4LightWrapper(wrapper, arguments);
                bool same = actual.Length == expected.Length && actual.All(value => value.GetType() == typeof(LightColorBeatmapEventData));
                for (int index = 0; index < Math.Min(actual.Length, expected.Length); index++)
                    same &= V4LightEventEquals(actual[index], expected[index]);
                Check("v4-lightshow-native-converter-" + nativeCases, same,
                    "ignore=" + ignore + "; previous=" + previous + "; nextBrightness=" + nextBrightness + "; events=" + actual.Length);
                BeatmapData plain = new(4);
                foreach (BeatmapEventData value in actual) plain.InsertBeatmapEventDataInOrder(value);
                Check("v4-lightshow-native-container-" + nativeCases, plain.allBeatmapDataItems.Count() == expected.Length,
                    "Original native container accepted " + actual.Length + " light-color events");
                nativeCases++;
                nativeEvents += actual.Length;
            }
            Check("v4-lightshow-native-cases-covered", nativeCases == 8 && nativeEvents == 4,
                "cases=" + nativeCases + "; non-ignored native events=" + nativeEvents);

            foreach (float? nextBrightness in new float?[] { null, 0.375f })
            {
                V4LightRecordingConverter recorder = new();
                recorder.Events = new BeatmapEventData[] {
                    new LightColorBeatmapEventData(1f, 2, 3, false, EaseType.Linear, EnvironmentColorType.Color0, 0.5f, 0, 0f, false),
                    new LightColorBeatmapEventData(2f, 2, 4, true, EaseType.InOutSine, EnvironmentColorType.Color1, 0.8f, 7, 0.3f, true)
                };
                object[] arguments = V4LightArguments(recorder, true, nextBrightness,
                    new LightColorBaseData(4f, true, EaseType.InOutSine, EnvironmentColorType.Color1, 0.6f, 8, 0.2f, true));
                BeatmapEventData[] actual = InvokeV4LightWrapper(wrapper, arguments);
                bool parametersPreserved = recorder.Calls == 1 && recorder.Arguments.SequenceEqual(arguments.Skip(1).Take(12));
                bool eventsPreserved = actual.Length == recorder.Events.Length && actual.Zip(recorder.Events, ReferenceEquals).All(value => value);
                Check("v4-lightshow-arbitrary-converter-" + (nextBrightness.HasValue ? "next" : "null"), parametersPreserved && eventsPreserved,
                    "calls=" + recorder.Calls + "; complete parameter forwarding=" + parametersPreserved + "; original event identity=" + eventsPreserved);
            }

            CustomData authored = new() { ["track"] = "regression-authored-track", ["color"] = new List<object> { 0.2f, 0.4f, 0.8f }, ["authorValue"] = "retain me" };
            CustomLightColorBaseData customSource = new(4f, true, EaseType.InOutSine, EnvironmentColorType.Color1, 0.6f, 8, 0.2f, true, authored, new Version(3, 0, 0));
            IBeatmapLightEventConverter customConverter = new BeatmapLightEventConverterNoConvert(false, false, true);
            BeatmapEventData[] customOutput = InvokeV4LightWrapper(wrapper, V4LightArguments(customConverter, true, 0.75f, customSource));
            CustomLightColorBeatmapEventData custom = customOutput.SingleOrDefault() as CustomLightColorBeatmapEventData;
            bool customPreserved = custom != null && ReferenceEquals(custom.customData, authored) &&
                ReferenceEquals(custom.customData["color"], authored["color"]) && Equals(custom.customData["track"], "regression-authored-track") &&
                Equals(custom.customData["authorValue"], "retain me") && V4LightEventEquals(custom,
                    customConverter.ConvertLightColorBeatmapEvent(37, 12.5f, 2, 3, true, EaseType.InOutSine,
                        EnvironmentColorType.Color1, 0.6f, 8, 0.2f, true, 0.75f).Single());
            Check("v4-lightshow-custom-author-data-preserved", customPreserved,
                "custom event=" + (custom != null) + "; author dictionary and nested data retain identity=" + customPreserved);
            CustomBeatmapData customContainer = new(4, new CustomData(), new CustomData(), new CustomData(), new Version(3, 0, 0));
            foreach (BeatmapEventData value in customOutput) customContainer.InsertBeatmapEventDataInOrder(value);
            Check("v4-lightshow-custom-container", customContainer.allBeatmapDataItems.Count() == 1 &&
                customContainer.allBeatmapDataItems.Single() is CustomLightColorBeatmapEventData,
                "Custom source remains in the canonical custom container");
            BeatmapEventData[] ignoredCustom = InvokeV4LightWrapper(wrapper, V4LightArguments(
                new BeatmapLightEventConverterNoConvert(true, false, true), true, 0.75f, customSource));
            Check("v4-lightshow-custom-ignore-preserved", ignoredCustom.Length == 0, "ignoreColorEvents retains its original custom behavior");
        }
        catch (Exception error)
        {
            Exception actual = error is TargetInvocationException invocation && invocation.InnerException != null ? invocation.InnerException : error;
            Check("v4-lightshow-conversion-regression", false, actual.ToString());
        }
    }

    static object[] V4LightArguments(IBeatmapLightEventConverter converter, bool previous, float? nextBrightness, LightColorBaseData source) =>
        new object[] { converter, 37, 12.5f, 2, 3, previous, EaseType.InOutSine, EnvironmentColorType.Color1, 0.6f, 8, 0.2f, true, nextBrightness, source };

    static BeatmapEventData[] InvokeV4LightWrapper(MethodInfo wrapper, object[] arguments) =>
        ((IEnumerable<BeatmapEventData>)wrapper.Invoke(null, arguments)).ToArray();

    static bool V4LightEventEquals(BeatmapEventData first, BeatmapEventData second)
    {
        if (!(first is LightColorBeatmapEventData a) || !(second is LightColorBeatmapEventData b)) return false;
        return a.time == b.time && a.groupId == b.groupId && a.elementId == b.elementId && a.usePreviousValue == b.usePreviousValue &&
            a.easeType == b.easeType && a.colorType == b.colorType && a.brightness == b.brightness && a.strobeBeatFrequency == b.strobeBeatFrequency &&
            a.strobeBrightness == b.strobeBrightness && a.strobeFade == b.strobeFade;
    }

    sealed class V4LightRecordingConverter : IBeatmapLightEventConverter
    {
        internal int Calls;
        internal object[] Arguments;
        internal BeatmapEventData[] Events;
        public bool hasConversions => true;
        public bool convertBoxGroupsEvents => true;
        public bool convertBoxGroups => true;
        public bool useRotationDirection => true;
        public IEnumerable<BeatmapEventData> ConvertLightColorBeatmapEvent(int subtypeIdentifier, float time, int groupId, int elementId,
            bool usePreviousValue, EaseType easeType, EnvironmentColorType colorType, float brightness, int strobeBeatFrequency,
            float strobeBrightness, bool strobeFade, float? nextEventBrightness)
        {
            Calls++;
            Arguments = new object[] { subtypeIdentifier, time, groupId, elementId, usePreviousValue, easeType, colorType,
                brightness, strobeBeatFrequency, strobeBrightness, strobeFade, nextEventBrightness };
            return Events;
        }
        public IEnumerable<BeatmapEventData> ConvertBasicBeatmapEvent(int subtypeIdentifier, float time, BasicBeatmapEventType type, int value, float floatValue) =>
            throw new InvalidOperationException("Unexpected basic conversion");
        public IEnumerable<BeatmapEventData> ConvertLightRotationBeatmapEvent(int subtypeIdentifier, float time, int groupId, int elementId,
            bool usePreviousValue, EaseType easeType, LightAxis axis, float rotation, int loops, LightRotationDirection direction) =>
            throw new InvalidOperationException("Unexpected rotation conversion");
        public IEnumerable<BeatmapEventData> ConvertLightTranslationBeatmapEvent(int subtypeIdentifier, float time, int groupId, int elementId,
            bool usePreviousValue, EaseType easeType, LightAxis axis, float translation, float gap) =>
            throw new InvalidOperationException("Unexpected translation conversion");
        public void InsertActivateOnStartEvents(BeatmapData data) => throw new InvalidOperationException("Unexpected activation conversion");
    }
}
