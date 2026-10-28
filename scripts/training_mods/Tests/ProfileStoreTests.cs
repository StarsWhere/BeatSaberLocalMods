using System;
using System.IO;
using LocalTraining;

internal sealed class SampleOptions : IValidatedSettings
{
    public double Value = 4;
    public void Validate() { if (Double.IsNaN(Value) || Double.IsInfinity(Value) || Value < 0) throw new InvalidDataException("Invalid value"); }
}
internal static class ProfileStoreTests
{
    static int checks;
    static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    static int Main(string[] args)
    {
        try {
            var root = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(root);
            var store = new ProfileStore<SampleOptions>("Sample", (category, error) => {}, root);
            Check(store.Global.Value == 4, "Defaults must work without a file");
            var detached = store.Global; detached.Value = 8;
            Check(store.Global.Value == 4, "Callers cannot mutate active settings");
            store.SaveGlobal(detached);
            store.SaveOverride("song|Standard|Expert", new SampleOptions { Value = 2 });
            Check(store.Resolve("song|Standard|Expert").Value == 2 && store.Resolve("other").Value == 8, "Override precedence");
            var read = new ProfileStore<SampleOptions>("Sample", null, root);
            Check(read.Resolve("song|Standard|Expert").Value == 2, "Persisted override");
            var path = Path.Combine(root, "UserData", "Sample.json");
            string before = File.ReadAllText(path);
            try { store.SaveGlobal(new SampleOptions { Value = Double.NaN }); throw new Exception("Accepted invalid settings"); }
            catch (InvalidDataException) { }
            Check(File.ReadAllText(path) == before && store.Global.Value == 8, "Invalid save preserves file and active snapshot");
            store.RemoveOverride("song|Standard|Expert");
            Check(!store.HasOverride("song|Standard|Expert") && store.Resolve("song|Standard|Expert").Value == 8, "Restore inheritance");
            File.WriteAllText(path, "{\"Global\":{},\"ChartOverrides\":{}}");
            var invalid = new ProfileStore<SampleOptions>("Sample", null, root);
            Check(invalid.LastError != null && invalid.Global.Value == 4 && File.ReadAllText(path).Contains("Global"), "Missing schema is rejected without overwriting source");
            File.WriteAllText(path, before.Replace("1.45.2_28848", "unsupported"));
            invalid = new ProfileStore<SampleOptions>("Sample", null, root);
            Check(invalid.LastError != null, "Unsupported build is rejected");
            Directory.CreateDirectory(Path.Combine(root, "UserData", "Blocked.json"));
            var blocked = new ProfileStore<SampleOptions>("Blocked", null, root);
            bool rejected = false;
            try { blocked.SaveGlobal(new SampleOptions { Value = 9 }); } catch (IOException) { rejected = true; }
            Check(rejected && blocked.Global.Value == 4 && blocked.LastError != null, "I/O failure retains active snapshot and error");
            Console.WriteLine("ProfileStore: " + checks + " checks passed");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
