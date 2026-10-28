using System;
using System.Linq;
using NUnit.Framework;

internal static class TestRunner
{
    static int Main()
    {
        try {
            int count = 0;
            foreach (var fixtureType in typeof(TestRunner).Assembly.GetTypes().Where(t => t.GetCustomAttributes(typeof(TestFixtureAttribute), true).Length != 0))
            {
                foreach (var test in fixtureType.GetMethods().Where(x => x.GetCustomAttributes(typeof(TestAttribute), true).Length != 0))
                {
                    object fixture = Activator.CreateInstance(fixtureType);
                    try
                    {
                        foreach (var setup in fixtureType.GetMethods().Where(x => x.GetCustomAttributes(typeof(SetUpAttribute), true).Length != 0)) setup.Invoke(fixture, null);
                        test.Invoke(fixture, null); count++;
                        Console.WriteLine("PASS " + fixtureType.Name + "." + test.Name);
                    }
                    finally
                    {
                        foreach (var teardown in fixtureType.GetMethods().Where(x => x.GetCustomAttributes(typeof(TearDownAttribute), true).Length != 0)) teardown.Invoke(fixture, null);
                        (fixture as IDisposable)?.Dispose();
                    }
                }
            }
            if (count == 0) throw new InvalidOperationException("No energy tests were discovered.");
            Console.WriteLine("ActivityEnergyCounter: " + count + " tests passed");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error.InnerException ?? error); return 1; }
    }
}
