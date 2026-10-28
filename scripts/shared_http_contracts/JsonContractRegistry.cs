#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NJsonSchema;

namespace LocalModContracts;

/// <summary>
/// Runtime access to a JSON schema and its HTTP endpoint registry.
/// This file is linked directly into each mod so the runtime does not need a
/// shared assembly that could conflict with the game's dependency loader.
/// </summary>
public sealed class JsonContractRegistry
{
    static readonly Regex PathParameterPattern = new Regex("\\{([^{}\\/]+)\\}", RegexOptions.CultureInvariant);
    static readonly Regex PathParameterNamePattern = new Regex("^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);
    static readonly HashSet<string> Methods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"
    };

    readonly JObject endpoints;
    readonly Uri baseUrl;
    readonly Dictionary<string, Uri> serviceUrls;

    public JsonContractRegistry(string schemaJson, string endpointsJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson)) throw new InvalidDataException("Contract schema is empty.");
        if (string.IsNullOrWhiteSpace(endpointsJson)) throw new InvalidDataException("Endpoint registry is empty.");

        Source = JObject.Parse(schemaJson);
        endpoints = JObject.Parse(endpointsJson);
        ValidateRegistry(Source, endpoints);
        baseUrl = ParseHttpsUrl(endpoints["baseUrl"], "baseUrl", allowMissing: true);
        serviceUrls = ReadServices(endpoints["services"] as JObject);
        Schema = JsonSchema.FromJsonAsync(Source.ToString(Formatting.None)).GetAwaiter().GetResult();
    }

    public JObject Source { get; }

    public JsonSchema Schema { get; }

    public void Validate(string name, JToken value)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Contract name is empty.");
        if (value == null) throw new InvalidDataException(name + ": value is null.");
        if (!Schema.Definitions.TryGetValue(name, out var definition))
            throw new InvalidDataException("Unknown contract: " + name);

        var errors = definition.Validate(value.ToString(Formatting.None));
        if (errors.Count != 0)
            throw new InvalidDataException(name + ": " + string.Join("; ", errors.Select(e => e.ToString())));
    }

    public JObject Endpoint(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Endpoint name is empty.");
        var rows = (JArray)endpoints["endpoints"];
        var endpoint = rows.Children<JObject>().SingleOrDefault(x => string.Equals((string)x["name"], name, StringComparison.Ordinal));
        if (endpoint == null) throw new InvalidDataException("Unknown endpoint: " + name);
        return (JObject)endpoint.DeepClone();
    }

    public Uri Url(string name, IDictionary<string, string> pathParameters = null)
    {
        var endpoint = Endpoint(name);
        var service = ResolveService(endpoint, baseUrl, serviceUrls, name);
        var path = NormalizePath((string)endpoint["path"], name);
        var unresolved = new HashSet<string>(StringComparer.Ordinal);
        var resolvedPath = PathParameterPattern.Replace(path, match =>
        {
            var parameterName = match.Groups[1].Value;
            string value;
            if (pathParameters == null || !pathParameters.TryGetValue(parameterName, out value) || string.IsNullOrEmpty(value))
            {
                unresolved.Add(parameterName);
                return match.Value;
            }

            return Uri.EscapeDataString(value);
        });

        if (unresolved.Count != 0)
            throw new InvalidDataException("Endpoint '" + name + "' has unresolved path parameters: " + string.Join(", ", unresolved.OrderBy(x => x, StringComparer.Ordinal)));

        var baseUri = EnsureDirectoryUri(service);
        if (resolvedPath == "/") return baseUri;
        return new Uri(baseUri, resolvedPath.TrimStart('/'));
    }

    /// <summary>
    /// Validates schema references and the endpoint registry without constructing
    /// a runtime schema. The generator calls this before producing DTOs.
    /// </summary>
    public static int ValidateRegistry(JObject source, JObject registry)
    {
        if (source == null) throw new InvalidDataException("Contract schema is missing.");
        if (registry == null) throw new InvalidDataException("Endpoint registry is missing.");
        ValidateSchemaReferences(source);

        var definitionNames = GetDefinitionNames(source);
        var baseUrl = ParseHttpsUrl(registry["baseUrl"], "baseUrl", allowMissing: true);
        var services = registry["services"] as JObject;
        var serviceUrls = new Dictionary<string, Uri>(StringComparer.Ordinal);
        if (services != null)
        {
            foreach (var service in services.Properties())
            {
                if (string.IsNullOrWhiteSpace(service.Name))
                    throw new InvalidDataException("Endpoint registry contains an empty service name.");
                serviceUrls.Add(service.Name, ParseHttpsUrl(service.Value, "service '" + service.Name + "'", allowMissing: false));
            }
        }

        if (baseUrl == null && serviceUrls.Count == 0)
            throw new InvalidDataException("Endpoint registry must contain baseUrl or services.");

        var rows = registry["endpoints"] as JArray
            ?? throw new InvalidDataException("Endpoint registry must contain an endpoints array.");
        if (rows.Count == 0) throw new InvalidDataException("Endpoint registry cannot be empty.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var routes = new List<Route>();
        foreach (var token in rows)
        {
            var endpoint = token as JObject
                ?? throw new InvalidDataException("Each endpoint registry entry must be an object.");
            var name = RequireString(endpoint, "name", "endpoint");
            if (!names.Add(name)) throw new InvalidDataException("Duplicate endpoint name: " + name);

            var method = RequireString(endpoint, "method", "endpoint '" + name + "'").ToUpperInvariant();
            if (!Methods.Contains(method))
                throw new InvalidDataException("Endpoint '" + name + "' has an unsupported method '" + method + "'.");

            var path = NormalizePath(RequireString(endpoint, "path", "endpoint '" + name + "'"), name);
            var endpointService = ResolveService(endpoint, baseUrl, serviceUrls, name);
            var host = endpointService.GetLeftPart(UriPartial.Authority);
            foreach (var prior in routes)
            {
                if (!string.Equals(prior.Method, method, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(prior.Host, host, StringComparison.OrdinalIgnoreCase)) continue;
                if (PathsOverlap(prior.Path, path))
                    throw new InvalidDataException("Endpoint routes overlap for " + method + " " + host + ": '" + prior.Name + "' and '" + name + "'.");
            }
            routes.Add(new Route(name, method, path, host));

            ValidateSchemaReference(endpoint, "request", definitionNames, name, required: false);
            var response = RequireString(endpoint, "response", "endpoint '" + name + "'");
            if (!string.Equals(response, "zip", StringComparison.OrdinalIgnoreCase))
                ValidateSchemaReference(endpoint, "response", definitionNames, name, required: true);
            ValidateOptionalSchemaReference(endpoint, "query", definitionNames, name);
            ValidateOptionalSchemaReference(endpoint, "headers", definitionNames, name);
            ValidateOptionalSchemaReference(endpoint, "querySchema", definitionNames, name, "query");
            ValidateOptionalSchemaReference(endpoint, "headersSchema", definitionNames, name, "headers");
            ValidateFields(endpoint, definitionNames, source, name);
        }

        return rows.Count;
    }

    static void ValidateSchemaReferences(JObject schema)
    {
        var definitions = schema["definitions"] as JObject;
        var dollarDefinitions = schema["$defs"] as JObject;
        if (definitions == null && dollarDefinitions == null)
            throw new InvalidDataException("The contract schema must declare definitions or $defs.");

        foreach (var reference in schema.Descendants().OfType<JValue>().Where(value =>
                     value.Type == JTokenType.String
                     && value.Parent is JProperty property
                     && string.Equals(property.Name, "$ref", StringComparison.Ordinal)))
        {
            var referenceText = (string)reference;
            if (string.IsNullOrWhiteSpace(referenceText)) throw new InvalidDataException("A schema $ref is empty.");
            if (!referenceText.StartsWith("#/", StringComparison.Ordinal))
                throw new InvalidDataException("Only local JSON Schema references are supported: " + referenceText);

            var parts = referenceText.Substring(2).Split('/');
            if (parts.Length != 2 || (parts[0] != "definitions" && parts[0] != "$defs"))
                throw new InvalidDataException("Schema reference must point to definitions or $defs: " + referenceText);

            var source = parts[0] == "definitions" ? definitions : dollarDefinitions;
            var definitionName = parts[1].Replace("~1", "/").Replace("~0", "~");
            if (source == null || source[definitionName] == null)
                throw new InvalidDataException("Schema reference target was not found: " + referenceText);
        }
    }

    static HashSet<string> GetDefinitionNames(JObject source)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in (source["definitions"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>()) names.Add(property.Name);
        foreach (var property in (source["$defs"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>()) names.Add(property.Name);
        return names;
    }

    static Dictionary<string, Uri> ReadServices(JObject services)
    {
        var result = new Dictionary<string, Uri>(StringComparer.Ordinal);
        if (services == null) return result;
        foreach (var service in services.Properties())
            result.Add(service.Name, ParseHttpsUrl(service.Value, "service '" + service.Name + "'", allowMissing: false));
        return result;
    }

    static void ValidateSchemaReference(JObject endpoint, string propertyName, HashSet<string> definitions, string endpointName, bool required)
    {
        var value = endpoint[propertyName];
        if (value == null || value.Type == JTokenType.Null || string.IsNullOrWhiteSpace((string)value))
        {
            if (!required) return;
            throw new InvalidDataException("Endpoint '" + endpointName + "' is missing " + propertyName + ".");
        }
        if (value.Type != JTokenType.String || !definitions.Contains((string)value))
            throw new InvalidDataException("Endpoint '" + endpointName + "' references unknown " + propertyName + " schema '" + (string)value + "'.");
    }

    static void ValidateOptionalSchemaReference(JObject endpoint, string propertyName, HashSet<string> definitions, string endpointName, string canonicalName = null)
    {
        if (endpoint[propertyName] == null) return;
        ValidateSchemaReference(endpoint, propertyName, definitions, endpointName, required: true);
        if (canonicalName != null && endpoint[canonicalName] != null)
            throw new InvalidDataException("Endpoint '" + endpointName + "' declares both " + canonicalName + " and " + propertyName + ".");
    }

    static void ValidateFields(JObject endpoint, HashSet<string> definitions, JObject source, string endpointName)
    {
        if (!(endpoint["fields"] is JObject fields)) return;
        var requestName = (string)endpoint["request"];
        if (string.IsNullOrWhiteSpace(requestName))
            throw new InvalidDataException("Endpoint '" + endpointName + "' fields require a request schema.");
        var requestSchema = FindDefinition(source, requestName);
        if (requestSchema == null || !(requestSchema["properties"] is JObject requestProperties))
            throw new InvalidDataException("Endpoint '" + endpointName + "' request '" + requestName + "' has no properties.");

        foreach (var field in fields.Properties())
        {
            if (field.Value.Type != JTokenType.String && field.Value.Type != JTokenType.Null)
                throw new InvalidDataException("Endpoint '" + endpointName + "' field '" + field.Name + "' must map to a string or null.");
            if (field.Value.Type == JTokenType.String && string.IsNullOrWhiteSpace((string)field.Value))
                throw new InvalidDataException("Endpoint '" + endpointName + "' field '" + field.Name + "' must map to a non-empty string.");
            if (requestProperties[field.Name] == null)
                throw new InvalidDataException("Endpoint '" + endpointName + "' field '" + field.Name + "' is absent from request schema '" + requestName + "'.");
        }

        foreach (var required in (requestSchema["required"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(required) || fields[required] == null)
                throw new InvalidDataException("Endpoint '" + endpointName + "' does not map required request field '" + required + "'.");
        }
    }

    static JObject FindDefinition(JObject source, string name)
    {
        return (source["definitions"] as JObject)?[name] as JObject
            ?? (source["$defs"] as JObject)?[name] as JObject;
    }

    static Uri ResolveService(JObject endpoint, Uri baseUrl = null, IDictionary<string, Uri> services = null, string endpointName = null)
    {
        var serviceName = (string)endpoint["service"];
        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            if (services == null || !services.TryGetValue(serviceName, out var service))
                throw new InvalidDataException("Endpoint '" + endpointName + "' references unknown service '" + serviceName + "'.");
            return service;
        }
        if (baseUrl != null) return baseUrl;
        throw new InvalidDataException("Endpoint '" + endpointName + "' must declare a service when baseUrl is absent.");
    }

    static Uri ParseHttpsUrl(JToken value, string label, bool allowMissing)
    {
        if (value == null || value.Type == JTokenType.Null || string.IsNullOrWhiteSpace((string)value))
        {
            if (allowMissing) return null;
            throw new InvalidDataException("Endpoint registry " + label + " must be an absolute HTTPS URL.");
        }
        if (value.Type != JTokenType.String || !Uri.TryCreate((string)value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("Endpoint registry " + label + " must be an absolute HTTPS URL without query or fragment.");
        return uri;
    }

    static string RequireString(JObject objectValue, string propertyName, string label)
    {
        var value = objectValue[propertyName];
        if (value == null || value.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)value))
            throw new InvalidDataException(label + " requires a non-empty " + propertyName + ".");
        return ((string)value).Trim();
    }

    static string NormalizePath(string path, string endpointName)
    {
        if (path == null) throw new InvalidDataException("Endpoint '" + endpointName + "' has no path.");
        path = path.Trim();
        if (path == "/") return "/";
        if (path.StartsWith("/", StringComparison.Ordinal)) path = path.Substring(1);
        if (path.Length == 0 || path.IndexOf("//", StringComparison.Ordinal) >= 0
            || path.IndexOf("?", StringComparison.Ordinal) >= 0 || path.IndexOf("#", StringComparison.Ordinal) >= 0
            || path.IndexOf('\\') >= 0)
            throw new InvalidDataException("Endpoint '" + endpointName + "' has an invalid path '" + path + "'.");

        var parameters = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in PathParameterPattern.Matches(path))
        {
            var parameterName = match.Groups[1].Value;
            if (!PathParameterNamePattern.IsMatch(parameterName))
                throw new InvalidDataException("Endpoint '" + endpointName + "' has an invalid path parameter '" + parameterName + "'.");
            if (!parameters.Add(parameterName))
                throw new InvalidDataException("Endpoint '" + endpointName + "' repeats path parameter '" + parameterName + "'.");
        }

        var withoutParameters = PathParameterPattern.Replace(path, string.Empty);
        if (withoutParameters.IndexOf('{') >= 0 || withoutParameters.IndexOf('}') >= 0)
            throw new InvalidDataException("Endpoint '" + endpointName + "' has an invalid path parameter expression.");
        return path;
    }

    static bool PathsOverlap(string first, string second)
    {
        var firstSegments = PathSegments(first);
        var secondSegments = PathSegments(second);
        if (firstSegments.Length != secondSegments.Length) return false;
        for (var index = 0; index < firstSegments.Length; index++)
        {
            var firstDynamic = firstSegments[index].IndexOf('{') >= 0;
            var secondDynamic = secondSegments[index].IndexOf('{') >= 0;
            if (!firstDynamic && !secondDynamic && !string.Equals(firstSegments[index], secondSegments[index], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    static string[] PathSegments(string path)
    {
        if (path == "/") return new string[0];
        return path.Split(new[] { '/' }, StringSplitOptions.None);
    }

    static Uri EnsureDirectoryUri(Uri uri)
    {
        if (uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal)) return uri;
        return new Uri(uri.AbsoluteUri + "/", UriKind.Absolute);
    }

    sealed class Route
    {
        public Route(string name, string method, string path, string host)
        {
            Name = name; Method = method; Path = path; Host = host;
        }

        public string Name { get; }
        public string Method { get; }
        public string Path { get; }
        public string Host { get; }
    }
}
