using System;
using System.IO;
using Newtonsoft.Json;

namespace EffectModsSmoke;

public sealed partial class Runner
{
    void VerifyUnityShutdownGuard()
    {
        if (!File.Exists(Path.Combine(Environment.CurrentDirectory, "Plugins", "UnityShutdownFix.dll"))) return;
        Type plugin = FindTrainingType("UnityShutdownFix.Plugin");
        bool installed = ReadBool(GetStaticMember(plugin, "Installed"), string.Empty, false);
        string failure = Convert.ToString(GetStaticMember(plugin, "FailureReason"));
        Check("unity-shutdown-guard-installed", plugin != null && installed,
            "Exact-build native null guard installed=" + installed + "; failure=" + failure);
        File.WriteAllText(Path.Combine(directory, "unity-shutdown-guard.json"), JsonConvert.SerializeObject(new {
            installed, failure, island = Convert.ToString(GetStaticMember(plugin, "IslandAddress"))
        }, Formatting.Indented));
    }
}
