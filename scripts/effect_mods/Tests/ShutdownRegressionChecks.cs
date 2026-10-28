using System;
using System.Collections;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using HarmonyLib;

namespace EffectModsSmoke;

public sealed partial class Runner
{
    readonly bool traceShutdown=Environment.GetEnvironmentVariable("EFFECT_SMOKE_TRACE_SHUTDOWN")=="1";
    readonly bool quitFromGameplay=Environment.GetEnvironmentVariable("EFFECT_SMOKE_QUIT_GAMEPLAY")=="1";
    object shutdownOwner;
    void CaptureShutdownOwner(Zenject.SceneContext context)
    {
        if(traceShutdown)shutdownOwner=context.Container.TryResolve(AccessTools.TypeByName("Vivify.Managers.AssetBundleManager"));
    }
    void CheckShutdownOwnerDisposed()
    {
        if(shutdownOwner==null)return;
        Check("bundle-owner-disposed-on-menu-return",(bool)AccessTools.Field(shutdownOwner.GetType(),"_disposed").GetValue(shutdownOwner),"Normal scene disposal must unregister the quit handler.");
        var dispose=AccessTools.Method(shutdownOwner.GetType(),"Dispose");
        dispose.Invoke(shutdownOwner,null);dispose.Invoke(shutdownOwner,null);
        var assets=(IDictionary)AccessTools.Field(shutdownOwner.GetType(),"_assets").GetValue(shutdownOwner);
        var bundle=(AssetBundle)AccessTools.Field(shutdownOwner.GetType(),"_mainBundle").GetValue(shutdownOwner);
        Check("bundle-dispose-repeat-safe",assets.Count==0&&bundle==null,"Repeated disposal leaves no loaded bundle or cached assets.");
    }
    void InitializeShutdownTrace()
    {
        if(!traceShutdown)return;
        Application.SetStackTraceLogType(LogType.Error,StackTraceLogType.Full);
    }
    void CaptureQuittingTargets()=>CaptureShutdownTargets("quitting");
    void CaptureShutdownTargets(string stage)
    {
        if(!traceShutdown)return;
        if(stage=="before-quit")Application.quitting+=CaptureQuittingTargets;
        var cameras=Resources.FindObjectsOfTypeAll<Camera>().Where(camera=>camera!=null&&camera.targetTexture!=null).Select(camera=>new{
            camera=camera.GetInstanceID(),path=String.Join("/",Parents(camera.transform).Reverse()),
            scene=camera.gameObject.scene.name,sceneValid=camera.gameObject.scene.IsValid(),camera.enabled,active=camera.gameObject.activeInHierarchy,
            target=new{id=camera.targetTexture.GetInstanceID(),camera.targetTexture.name,created=camera.targetTexture.IsCreated(),camera.targetTexture.graphicsFormat,camera.targetTexture.depthStencilFormat},
            components=camera.GetComponents<Component>().Where(component=>component!=null).Select(component=>component.GetType().FullName)
        }).ToArray();
        File.WriteAllText(Path.Combine(directory,"camera-targets-"+stage+".json"),JsonConvert.SerializeObject(cameras,Formatting.Indented));
    }
}
