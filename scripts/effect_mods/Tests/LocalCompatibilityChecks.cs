using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace EffectModsSmoke;

public sealed partial class Runner
{
    readonly bool localCompatibilityChecks=Environment.GetEnvironmentVariable("EFFECT_SMOKE_LOCAL_COMPATIBILITY")=="1";
    readonly List<object> localDiagnostics=new();
    void InitializeLocalCompatibilityChecks()
    {
        if(!localCompatibilityChecks)return;
        Application.SetStackTraceLogType(LogType.Warning,StackTraceLogType.Full);
        Application.SetStackTraceLogType(LogType.Error,StackTraceLogType.Full);
        Application.logMessageReceived+=CaptureLocalMessage;
    }
    void CaptureLocalMessage(string message,string stack,LogType kind)
    {
        if(!message.Contains("referenced script")&&!message.Contains("invalid command name"))return;
        localDiagnostics.Add(new{message,stack,kind=kind.ToString()});
        File.WriteAllText(Path.Combine(directory,"local-compatibility-messages.json"),JsonConvert.SerializeObject(localDiagnostics,Formatting.Indented));
    }
    void CaptureLocalCompatibility(Zenject.SceneContext context)
    {
        if(!localCompatibilityChecks)return;
        var missing=Resources.FindObjectsOfTypeAll<GameObject>().Where(go=>go.GetComponents<Component>().Any(c=>c==null)).Select(go=>new{
            path=String.Join("/",Parents(go.transform).Reverse()),scene=go.scene.name,sceneValid=go.scene.IsValid(),active=go.activeInHierarchy,
            components=go.GetComponents<Component>().Select(c=>c==null?"MISSING":c.GetType().FullName),
            children=go.GetComponentsInChildren<Component>(true).Where(c=>c!=null).Select(c=>c.GetType().FullName).Distinct()
        }).ToArray();
        File.WriteAllText(Path.Combine(directory,"missing-components.json"),JsonConvert.SerializeObject(missing,Formatting.Indented));
        Check("gameplay-has-no-missing-components",missing.Length==0,JsonConvert.SerializeObject(missing));
        foreach(var name in new[]{"CoreGameHUDController","NoteCutSoundEffectManager"})
        {
            var type=AccessTools.TypeByName(name);
            var instance=context.Container.TryResolve(type);
            Check("native-binding-"+name,instance!=null && Resources.FindObjectsOfTypeAll(type).Contains(instance),"Native scene instance resolves without an extra exposure request.");
        }
    }
    IEnumerator VerifyLocalCompatibility(Zenject.SceneContext context)
    {
        if(!localCompatibilityChecks)yield break;
        var terminalType=AccessTools.TypeByName("TerminalController");
        var terminal=context.Container.TryResolve(terminalType);
        Check("native-terminal-resolves",terminal!=null,"Native console remains available.");
        var commands=(IDictionary)AccessTools.Field(terminalType,"_commands").GetValue(terminal);
        var messageType=AccessTools.TypeByName("ConsoleMessage");
        foreach(var name in new[]{"cvar.bool","cvar.int","cvar.float"})
        {
            Check("terminal-registers-"+name,commands.Contains(name),String.Join(",",commands.Keys.Cast<string>()));
            var messages=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(messageType));
            var task=(Task)AccessTools.Method(terminalType,"TryExecuteCommand").Invoke(terminal,new object[]{name,messages});
            yield return Wait(()=>task.IsCompleted,5,"terminal-read-"+name);
            task.GetAwaiter().GetResult();
            var messageKind=AccessTools.Field(messageType,"type");
            Check("terminal-reads-"+name,messages.Count>0 && messages.Cast<object>().All(m=>(LogType)messageKind.GetValue(m)==LogType.Log),JsonConvert.SerializeObject(messages));
        }
        var failureText=Resources.FindObjectsOfTypeAll<TMPro.TextMeshPro>().Single(text=>text.gameObject.scene.IsValid() && text.name=="Text" && text.transform.parent.name=="LevelFailedTextEffect");
        var root=failureText.transform.parent.gameObject;
        var clone=UnityEngine.Object.Instantiate(root,transform);
        try
        {
            foreach(var renderer in clone.GetComponentsInChildren<Renderer>(true))renderer.enabled=false;
            var effect=clone.GetComponent<LevelFailedTextEffect>();
            Check("level-failed-native-effect-present",effect!=null && AccessTools.Field(typeof(LevelFailedTextEffect),"_animator").GetValue(effect)!=null,"Native failure animation retains its animator.");
            effect.ShowEffect();
            yield return null;
            var text=clone.GetComponentInChildren<TMPro.TextMeshPro>(true);
            var localizers=text.GetComponents<BGLib.Polyglot.LocalizedTextMeshPro>();
            Check("level-failed-single-working-localizer",localizers.Length==1,"Exactly one working localization component owns the text.");
            var key=(string)AccessTools.Field(localizers[0].GetType(),"key").GetValue(localizers[0]);
            var expected=BGLib.Polyglot.Localization.Get(key);
            text.ForceMeshUpdate(true,true);
            Check("level-failed-localized-text-and-mesh",text.text==expected && text.textInfo.characterCount>0 && text.mesh.vertexCount>0,"key="+key+"; text="+text.text);
            File.WriteAllText(Path.Combine(directory,"failure-text.json"),JsonConvert.SerializeObject(new{key,text.text,text.mesh.vertexCount},Formatting.Indented));
        }
        finally{UnityEngine.Object.Destroy(clone);}
    }
}
