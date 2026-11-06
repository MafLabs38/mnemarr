namespace Mnemarr.Api.V1.Collectors;

/// <summary>The command a person runs on the machine: the same words in the interface and in the answer of the MCP tool.</summary>
public static class EnrolCommand
{
    public static string For(string server, string code, string machine, bool insecure = false) =>
        $"mnemarr-collector enroll --server {server} --code {code} --machine {machine}{(insecure ? " --allow-insecure-http" : string.Empty)}";
}
