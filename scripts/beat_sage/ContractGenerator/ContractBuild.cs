namespace BeatSage.ContractGenerator;

internal static class ContractBuild
{
    public static Task<int> RunAsync(string[] args)
    {
        return LocalModContracts.ContractBuild.RunAsync(
            args,
            "BeatSage.Contracts",
            "ContractDocument",
            "Beat Sage");
    }
}
