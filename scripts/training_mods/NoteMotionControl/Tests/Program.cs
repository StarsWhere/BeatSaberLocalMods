using NoteMotionControl.Tests;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args != null && args.Length > 0)
        {
            if (args.Length != 2 || !string.Equals(args[0], "--migrate", System.StringComparison.OrdinalIgnoreCase))
            {
                System.Console.Error.WriteLine("Usage: --migrate <gameRoot>");
                return 2;
            }

            var store = new NoteMotionControl.SettingsStore(args[1]);
            if (!string.IsNullOrEmpty(store.LastError))
            {
                System.Console.Error.WriteLine(store.LastError);
                return 1;
            }

            NoteMotionControl.Options settings = store.Global;
            System.Console.WriteLine("Migration succeeded.");
            System.Console.WriteLine("Enabled={0}; NjsMode={1}; ReductionMode={2}; ApplicationPolicy={3}",
                settings.Enabled, settings.NjsMode, settings.ReductionMode, settings.ApplicationPolicy);
            return 0;
        }

        SpeedPolicyTests.RunAll();
        OptionsMigrationTests.RunAll();
        PresetsTests.RunAll();
        System.Console.WriteLine("NoteMotionControl native speed, settings, migration and preset tests: PASS");
        return 0;
    }
}
