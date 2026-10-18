using System.Text.Json.Serialization;
using GKME.Core;

namespace GKME;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(AppSettings))]
public partial class AppJsonContext : JsonSerializerContext
{
}