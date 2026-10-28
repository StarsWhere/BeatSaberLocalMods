#nullable disable

using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NJsonSchema;
using NJsonSchema.CodeGeneration.CSharp;

namespace LocalModContracts;

/// <summary>
/// Shared contract generator entrypoint. A mod-specific executable supplies
/// its defaults while schema and endpoint validation remain one implementation.
/// </summary>
public static class ContractBuild
{
    public static async Task<int> RunAsync(
        string[] args,
        string defaultNamespace,
        string defaultRootName,
        string displayName = "contract")
    {
        try
        {
            var options = ParseArguments(args, defaultNamespace, defaultRootName);
            var result = await GenerateAsync(options);
            Console.WriteLine("Generated " + result.GeneratedPath);
            Console.WriteLine("Schema SHA256: " + result.SchemaSha256);
            Console.WriteLine("Registry SHA256: " + result.RegistrySha256);
            Console.WriteLine("Generated SHA256: " + result.GeneratedSha256);
            Console.WriteLine("Validated endpoints: " + result.EndpointCount);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(displayName + " contract build failed: " + exception.Message);
            return 1;
        }
    }

    static async Task<GenerationResult> GenerateAsync(GeneratorOptions options)
    {
        var schemaPath = Path.GetFullPath(options.SchemaPath);
        var endpointsPath = Path.GetFullPath(options.EndpointsPath);
        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        if (!File.Exists(schemaPath)) throw new FileNotFoundException("Contract schema was not found.", schemaPath);
        if (!File.Exists(endpointsPath)) throw new FileNotFoundException("Endpoint registry was not found.", endpointsPath);

        var schemaJson = await File.ReadAllTextAsync(schemaPath, Encoding.UTF8);
        var endpointJson = await File.ReadAllTextAsync(endpointsPath, Encoding.UTF8);
        var schemaObject = JObject.Parse(schemaJson);
        var endpointObject = JObject.Parse(endpointJson);
        var endpointCount = JsonContractRegistry.ValidateRegistry(schemaObject, endpointObject);

        var schema = await JsonSchema.FromJsonAsync(schemaJson);
        var settings = new CSharpGeneratorSettings
        {
            Namespace = options.Namespace,
            ClassStyle = CSharpClassStyle.Poco,
            GenerateDataAnnotations = false
        };
        var generated = new CSharpGenerator(schema, settings).GenerateFile(options.RootName);
        var regenerated = new CSharpGenerator(schema, settings).GenerateFile(options.RootName);
        if (!string.Equals(NormalizeGeneratedText(generated), NormalizeGeneratedText(regenerated), StringComparison.Ordinal))
            throw new InvalidDataException("Regenerated DTO output is not deterministic.");

        Directory.CreateDirectory(outputDirectory);
        var generatedPath = Path.Combine(outputDirectory, options.RootName + ".cs");
        File.WriteAllText(generatedPath, NormalizeGeneratedText(generated), new UTF8Encoding(false));
        var generatedSha256 = ComputeSha256(generatedPath);
        var manifestPath = Path.Combine(outputDirectory, "contract.generation.json");
        var manifest = new JObject
        {
            ["schemaSha256"] = ComputeSha256(schemaPath),
            ["registrySha256"] = ComputeSha256(endpointsPath),
            ["generatedSha256"] = generatedSha256,
            ["namespace"] = options.Namespace,
            ["rootName"] = options.RootName,
            ["endpointCount"] = endpointCount
        };
        File.WriteAllText(manifestPath, manifest.ToString(Formatting.Indented) + Environment.NewLine, new UTF8Encoding(false));

        return new GenerationResult(
            generatedPath,
            (string)manifest["schemaSha256"],
            (string)manifest["registrySha256"],
            generatedSha256,
            endpointCount);
    }

    static string NormalizeGeneratedText(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).TrimEnd() + "\n";
    }

    static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    static GeneratorOptions ParseArguments(string[] args, string defaultNamespace, string defaultRootName)
    {
        if (string.IsNullOrWhiteSpace(defaultNamespace)) throw new ArgumentException("A default namespace is required.");
        if (string.IsNullOrWhiteSpace(defaultRootName)) throw new ArgumentException("A default root name is required.");

        string schema = null;
        string endpoints = null;
        string output = null;
        var ns = defaultNamespace;
        var rootName = defaultRootName;
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (!option.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                throw new ArgumentException("Expected --option value arguments.");

            var value = args[++index];
            switch (option)
            {
                case "--schema": schema = value; break;
                case "--endpoints": endpoints = value; break;
                case "--output": output = value; break;
                case "--namespace": ns = value; break;
                case "--root-name": rootName = value; break;
                default: throw new ArgumentException("Unknown option: " + option);
            }
        }

        if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(endpoints) || string.IsNullOrWhiteSpace(output))
            throw new ArgumentException("Required options: --schema, --endpoints, and --output.");
        if (string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(rootName))
            throw new ArgumentException("Namespace and root name cannot be empty.");

        return new GeneratorOptions(schema, endpoints, output, ns, rootName);
    }

    sealed record GeneratorOptions(string SchemaPath, string EndpointsPath, string OutputDirectory, string Namespace, string RootName);

    sealed record GenerationResult(string GeneratedPath, string SchemaSha256, string RegistrySha256, string GeneratedSha256, int EndpointCount);
}
