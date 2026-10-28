using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using LocalModContracts;

var schema = JObject.Parse(@"{
  'definitions': {
    'Request': {
      'type': 'object',
      'properties': { 'audio': { 'type': 'string' } },
      'required': [ 'audio' ],
      'additionalProperties': false
    },
    'Response': {
      'type': 'object',
      'properties': { 'id': { 'type': 'string' } },
      'required': [ 'id' ],
      'additionalProperties': false
    },
    'Query': {
      'type': 'object',
      'properties': { 'token': { 'type': 'string' } },
      'required': [ 'token' ],
      'additionalProperties': false
    }
  }
}");

var registry = JObject.Parse(@"{
  'services': {
    'baiduToken': 'https://baidu.example/token/',
    'baiduAsr': 'https://baidu.example/asr/',
    'tencentAsr': 'https://tencent.example/'
  },
  'endpoints': [
    { 'name': 'token', 'service': 'baiduToken', 'method': 'POST', 'path': 'oauth', 'request': 'Request', 'response': 'Response', 'fields': { 'audio': 'audio' } },
    { 'name': 'baidu', 'service': 'baiduAsr', 'method': 'POST', 'path': 'v1/{task}', 'request': 'Request', 'response': 'Response', 'query': 'Query' },
    { 'name': 'tencent', 'service': 'tencentAsr', 'method': 'POST', 'path': '/', 'response': 'Response' }
  ]
}");

Assert(JsonContractRegistry.ValidateRegistry(schema, registry) == 3, "registry endpoint count");
var contracts = new JsonContractRegistry(schema.ToString(), registry.ToString());
Assert(contracts.Url("tencent").AbsoluteUri == "https://tencent.example/", "root URL");
Assert(contracts.Url("baidu", new Dictionary<string, string> { ["task"] = "a/b" }).AbsoluteUri.Contains("a%2Fb"), "escaped path parameter");
ExpectInvalid(() => contracts.Url("baidu"), "unresolved path parameter");

var overlap = JObject.Parse(registry.ToString());
((JObject)((JArray)overlap["endpoints"])[2])["service"] = "baiduAsr";
((JObject)((JArray)overlap["endpoints"])[2])["path"] = "v1/fixed";
ExpectInvalid(() => JsonContractRegistry.ValidateRegistry(schema, overlap), "dynamic/exact route overlap");

var badFields = JObject.Parse(registry.ToString());
((JObject)((JArray)badFields["endpoints"])[0])["fields"] = new JObject { ["other"] = "other" };
ExpectInvalid(() => JsonContractRegistry.ValidateRegistry(schema, badFields), "form field mapping");

var badService = JObject.Parse(registry.ToString());
((JObject)badService["services"])["baiduToken"] = "http://baidu.example/token/";
ExpectInvalid(() => JsonContractRegistry.ValidateRegistry(schema, badService), "HTTPS service requirement");

Console.WriteLine("Shared HTTP contract checks passed.");

static void Assert(bool condition, string name)
{
    if (!condition) throw new InvalidDataException("Failed check: " + name);
}

static void ExpectInvalid(Action action, string name)
{
    try
    {
        action();
    }
    catch (InvalidDataException)
    {
        return;
    }
    throw new InvalidDataException("Expected validation failure: " + name);
}
