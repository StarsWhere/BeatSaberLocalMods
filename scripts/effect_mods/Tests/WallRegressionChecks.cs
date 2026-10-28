using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using CustomJSONData.CustomBeatmap;

namespace EffectModsSmoke;

public sealed partial class Runner
{
    int negativeColliderWarnings, decorativeSamples, interactiveSamples;
    bool wallColorChecked, colliderReuseChecked, rocketMotionChecked, rocketFadeChecked;
    object wallColors, obstacleInitPatch, noodleDeserialized;
    IDictionary noodleObjects;
    object[] rocketInfos;

    void SetupWallRegression(Zenject.SceneContext context, CustomBeatmapData beatmap)
    {
        AnimationPropertyRegression();
        wallColors = context.Container.TryResolve(AccessTools.TypeByName("Chroma.Colorizer.ObstacleColorizerManager"));
        var affinities = context.Container.ResolveAll(AccessTools.TypeByName("SiraUtil.Affinity.IAffinity"));
        obstacleInitPatch = affinities.Cast<object>().FirstOrDefault(value => value.GetType().FullName == "NoodleExtensions.HarmonyPatches.Objects.ObstacleInitNoodlifier");
        if (obstacleInitPatch != null)
        {
            noodleDeserialized = AccessTools.Field(obstacleInitPatch.GetType(), "_deserializedData").GetValue(obstacleInitPatch);
            noodleObjects = (IDictionary)AccessTools.Property(noodleDeserialized.GetType(), "ObjectCustomDatas").GetValue(noodleDeserialized);
        }
        var rocket = beatmap.customEventDatas.FirstOrDefault(value => value.eventType == "AnimateTrack" && value.customData.TryGetValue("_track", out object track) && Equals(track, "starL18GP"));
        if (rocket != null)
        {
            var deserialized = context.Container.ResolveId(AccessTools.TypeByName("Heck.Deserialize.DeserializedData"), "Heck");
            var events = (IDictionary)AccessTools.Property(deserialized.GetType(), "CustomEventCustomDatas").GetValue(deserialized);
            Check("rocket-mixed-event-retained", events.Contains(rocket), "Original event at beat 783.8 must survive the unsupported field.");
            rocketInfos = ((IEnumerable)AccessTools.Property(events[rocket].GetType(), "CoroutineInfos").GetValue(events[rocket])).Cast<object>().ToArray();
            Check("rocket-valid-animation-channels", rocketInfos.Any(info => PropertyValueType(info) == typeof(Vector3)) && rocketInfos.Any(info => PropertyValueType(info) == typeof(float)), "Position and dissolve channels both retained.");
        }
    }

    static Type PropertyValueType(object info)
    {
        object property = AccessTools.Property(info.GetType(), "Property").GetValue(info);
        return property.GetType().GetGenericArguments().Single();
    }

    static object PropertyValue(object info)
    {
        object property = AccessTools.Property(info.GetType(), "Property").GetValue(info);
        return AccessTools.Property(property.GetType(), "Value").GetValue(property);
    }

    void AnimationPropertyRegression()
    {
        var trackType = AccessTools.TypeByName("Heck.Animation.Track");
        var tracks = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), trackType));
        tracks.Add("probe", Activator.CreateInstance(trackType));
        var type = AccessTools.TypeByName("Heck.HeckCoroutineEventData");
        object Parse(string json, bool v2, string kind = "AnimateTrack") => Activator.CreateInstance(type, AccessTools.all, null, new object[] {
            new CustomEventData(0, kind, CustomData.FromJSON(json), new Version(v2 ? "2.0.0" : "3.3.0")), new Dictionary<string,List<object>>(), tracks, v2
        }, null);
        object[] Infos(object value) => ((IEnumerable)AccessTools.Property(type, "CoroutineInfos").GetValue(value)).Cast<object>().ToArray();
        var legacy = Infos(Parse("{\"_track\":\"probe\",\"_position\":[[1,2,3,0]],\"_dissolve\":[[0.5,0]],\"_definitePosition\":[[0,0,0,0]]}", true));
        Check("legacy-mixed-property-event", legacy.Any(info => PropertyValueType(info) == typeof(Vector3)) && legacy.Any(info => PropertyValueType(info) == typeof(float)), "Unsupported path field cannot discard valid AnimateTrack fields.");
        var modern = Infos(Parse("{\"track\":\"probe\",\"position\":[[1,2,3,0]],\"unsupportedProbe\":[[1,0]]}", false));
        Check("modern-mixed-property-event", modern.Any(info => PropertyValueType(info) == typeof(Vector3)), "Modern unknown field preserves known position.");
        Check("legacy-path-animation-preserved", Infos(Parse("{\"_track\":\"probe\",\"_definitePosition\":[[1,2,3,0]]}", true, "AssignPathAnimation")).Length == 1, "Definite position remains a path property.");
        bool rejected = false;
        try { Parse("{\"_track\":\"probe\",\"_position\":42}", true); }
        catch (System.Reflection.TargetInvocationException) { rejected = true; }
        Check("malformed-known-animation-rejected", rejected, "Invalid data for a supported field must still fail validation.");
        var equal = AccessTools.Method(AccessTools.TypeByName("Heck.Deserialize.DeserializerManager"), "PointDefinitionsEqual");
        var first = new List<object> {new List<object>{0d,1d,"easeLinear"}};
        var same = new List<object> {new List<object>{0d,1d,"easeLinear"}};
        var different = new List<object> {new List<object>{0d,2d,"easeLinear"}};
        Check("identical-point-definitions", (bool)equal.Invoke(null,new object[]{first,same}), "Separately parsed equal definitions are equivalent.");
        Check("conflicting-point-definitions", !(bool)equal.Invoke(null,new object[]{first,different}), "Different point values must remain a conflict.");
    }

    void ObserveWallRegression(float songTime)
    {
        if(noodleDeserialized!=null)noodleObjects=(IDictionary)AccessTools.Property(noodleDeserialized.GetType(),"ObjectCustomDatas").GetValue(noodleDeserialized);
        if (manager != null && Time.frameCount % 30 == 0)
            foreach (var wall in obstacleInitPatch == null
                ? (IEnumerable<ObstacleController>)manager.activeObstacleControllers
                : ((IDictionary)AccessTools.Field(obstacleInitPatch.GetType(),"_colliders").GetValue(obstacleInitPatch)).Keys.Cast<ObstacleController>().Where(value=>value!=null && value.gameObject.activeInHierarchy))
            {
                if (!wallColorChecked && wallColors != null) { WallColorRegression(wall); wallColorChecked = true; }
                if (noodleObjects == null || !noodleObjects.Contains(wall.obstacleData)) continue;
                object data = noodleObjects[wall.obstacleData];
                bool decorative = Equals(AccessTools.Property(data.GetType(),"Fake").GetValue(data),true) || Equals(AccessTools.Property(data.GetType(),"Uninteractable").GetValue(data),true);
                var cache = (IDictionary)AccessTools.Field(obstacleInitPatch.GetType(),"_colliders").GetValue(obstacleInitPatch);
                var states = (Array)cache[wall];
                if (states == null) continue;
                foreach (object state in states)
                {
                    var collider = (BoxCollider)state.GetType().GetField("Item1").GetValue(state);
                    bool original = (bool)state.GetType().GetField("Item2").GetValue(state);
                    if (collider.enabled != (original && !decorative)) throw new InvalidOperationException("Obstacle collider state disagrees with its interaction policy.");
                    if (decorative) decorativeSamples++; else interactiveSamples++;
                }
                if (!colliderReuseChecked && states.Length > 0) { ColliderReuseRegression(wall,data,states); colliderReuseChecked = true; }
            }
        if (rocketInfos != null && !rocketMotionChecked && songTime >= 236.34f && songTime < 237f)
        {
            var values = rocketInfos.Where(info => PropertyValueType(info)==typeof(Vector3)).Select(PropertyValue).OfType<Vector3>().ToArray();
            Check("rocket-position-executes", values.Any(value => Math.Abs(value.x+3)<0.01f && Math.Abs(value.y-1)<0.01f && Math.Abs(value.z)<3), "Position reached the middle of its original authored path.");
            rocketMotionChecked = true;
        }
        if (rocketInfos != null && !rocketFadeChecked && songTime >= 237.7f)
        {
            Check("rocket-dissolve-executes", rocketInfos.Where(info=>PropertyValueType(info)==typeof(float)).Select(PropertyValue).OfType<float>().Any(value=>Math.Abs(value)<0.001f), "Original dissolve animation completed.");
            rocketFadeChecked = true;
        }
    }

    void ColliderReuseRegression(ObstacleController wall, object data, Array states)
    {
        var fake = AccessTools.Field(data.GetType(),"<Fake>k__BackingField");
        var inactive = AccessTools.Field(data.GetType(),"<Uninteractable>k__BackingField");
        object oldFake=fake.GetValue(data), oldInactive=inactive.GetValue(data), first=states.GetValue(0);
        var update = AccessTools.Method(obstacleInitPatch.GetType(),"UpdateColliders");
        var prepare = AccessTools.Method(obstacleInitPatch.GetType(),"PrepareColliders");
        var collider = (BoxCollider)first.GetType().GetField("Item1").GetValue(first);
        Transform core=wall.GetComponent(AccessTools.TypeByName("StretchableObstacle")).transform.Find("ObstacleCore");
        Vector3 oldScale=core.localScale;
        try
        {
            prepare.Invoke(obstacleInitPatch,new object[]{wall});core.localScale=new Vector3(-1,1,1);
            prepare.Invoke(obstacleInitPatch,new object[]{wall});
            Check("pooled-negative-collider-disabled-before-resize",!collider.enabled,"A negative scale from the previous use cannot rebuild a live PhysX shape.");
            core.localScale=Vector3.one;
            fake.SetValue(data,false);inactive.SetValue(data,false);update.Invoke(obstacleInitPatch,new object[]{wall,wall.obstacleData});
            Check("pooled-interactive-collider-restored", collider.enabled==(bool)first.GetType().GetField("Item2").GetValue(first), "Reuse restores the original collider state.");
            states.SetValue(Activator.CreateInstance(first.GetType(),collider,false),0);
            update.Invoke(obstacleInitPatch,new object[]{wall,wall.obstacleData});
            Check("originally-disabled-collider-preserved", !collider.enabled, "Reuse must not enable an originally disabled shape.");
        }
        finally
        {
            prepare.Invoke(obstacleInitPatch,new object[]{wall});core.localScale=oldScale;
            states.SetValue(first,0);fake.SetValue(data,oldFake);inactive.SetValue(data,oldInactive);
            update.Invoke(obstacleInitPatch,new object[]{wall,wall.obstacleData});
        }
    }

    void WallColorRegression(ObstacleController wall)
    {
        var colorizer=AccessTools.Method(wallColors.GetType(),"GetColorizer").Invoke(wallColors,new object[]{wall});
        var colorize=AccessTools.Method(colorizer.GetType(),"Colorize");
        var original=(Color)AccessTools.Property(colorizer.GetType(),"Color").GetValue(colorizer);
        var stretch=wall.GetComponent(AccessTools.TypeByName("StretchableObstacle"));
        var frame=AccessTools.Field(stretch.GetType(),"_obstacleFrame").GetValue(stretch);
        float width=(float)AccessTools.Field(frame.GetType(),"width").GetValue(frame), height=(float)AccessTools.Field(frame.GetType(),"height").GetValue(frame), length=(float)AccessTools.Field(frame.GetType(),"length").GetValue(frame);
        var controllers=((IEnumerable)AccessTools.Field(stretch.GetType(),"_materialPropertyBlockControllers").GetValue(stretch)).Cast<object>().ToArray();
        MaterialPropertyBlock Block(object controller)=>(MaterialPropertyBlock)AccessTools.Property(controller.GetType(),"materialPropertyBlock").GetValue(controller);
        float multiplier=(float)AccessTools.Field(stretch.GetType(),"_manualUVOffsetMultiplier").GetValue(stretch);
        float offset=multiplier==0?0:Block(controllers[0]).GetVector("_UVManualOffset").x/multiplier;
        var native=AccessTools.Method(stretch.GetType(),"SetAllProperties");
        var setter=wall.GetComponent(AccessTools.TypeByName("ObstacleMaterialSetter"));
        var renderers=wall.GetComponentsInChildren<Renderer>(true);var materials=renderers.Select(renderer=>renderer.sharedMaterials).ToArray();
        var properties=new[]{"_Color","_AddColor","_TintColor"};
        try
        {
            for(int quality=0;quality<3;quality++)
            {
                var init=AccessTools.Method(setter.GetType(),"Init");init.Invoke(setter,new[]{Enum.ToObject(init.GetParameters()[0].ParameterType,quality),(object)true});
                foreach(Color color in new[]{new Color(0.2f,0.8f,0.1f,0.3f),new Color(42,42,42,0),new Color(80,80,80,0)})
                {
                    native.Invoke(stretch,new object[]{width,height,length,color,offset});
                    var expected=controllers.Select(controller=>properties.Select(property=>Block(controller).GetColor(property)).ToArray()).ToArray();
                    native.Invoke(stretch,new object[]{width,height,length,Color.black,offset});
                    colorize.Invoke(colorizer,new object[]{(Color?)color});
                    bool match=controllers.Select((controller,i)=>properties.Select((property,k)=>((Vector4)(Block(controller).GetColor(property)-expected[i][k])).sqrMagnitude<0.00001f).All(value=>value)).All(value=>value);
                    Check("wall-colors-native-contract-"+quality+"-"+color.r,match,"Actual native SetAllProperties is the oracle for RGB, HDR and alpha across all quality materials.");
                }
            }
        }
        finally
        {
            native.Invoke(stretch,new object[]{width,height,length,original,offset});colorize.Invoke(colorizer,new object[]{(Color?)original});
            for(int i=0;i<renderers.Length;i++)renderers[i].sharedMaterials=materials[i];
        }
    }

    void FinishWallRegression()
    {
        if(wallColors!=null && obstacles>0)Check("wall-color-regression-exercised",wallColorChecked,"Observed a real spawned wall.");
        if(noodleObjects!=null && obstacles>0)Check("wall-collider-policy",colliderReuseChecked && decorativeSamples+interactiveSamples>0,"decorative="+decorativeSamples+"; interactive="+interactiveSamples);
        if(rocketInfos!=null && duration==0)
        {
            Check("rocket-animation-playback-sampled",rocketMotionChecked&&rocketFadeChecked,"Both position and dissolve evaluated during the original full song.");
            Check("no-negative-collider-rebuilds",negativeColliderWarnings==0,"warnings="+negativeColliderWarnings);
        }
    }
}
