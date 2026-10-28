using NoteMotionControl.Reduction.Tests;

internal static class TestRunner
{
    private static void Main()
    {
        AlgorithmTests.RunAll();
        System.Console.WriteLine("NoteMotionControl reduction tests passed.");
    }
}
