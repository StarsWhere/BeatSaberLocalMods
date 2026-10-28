using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using IPA;
using IPA.Logging;

[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyInformationalVersion("0.1.0+bs1.45.2-unity6000.3.19f1")]

namespace UnityShutdownFix
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public sealed class Plugin
    {
        private readonly Logger log;
        public static bool Installed { get { return NativeGuard.Installed; } }
        public static string FailureReason { get { return NativeGuard.FailureReason; } }
        public static long IslandAddress { get { return NativeGuard.Island.ToInt64(); } }
        [Init] public Plugin(Logger logger) { log = logger; }
        [OnStart] public void Start()
        {
            string detail;
            string gameRoot = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
            if (NativeGuard.Install(gameRoot, out detail)) log.Info(detail);
            else log.Error(detail);
        }
    }
}
