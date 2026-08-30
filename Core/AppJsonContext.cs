using System.Text.Json.Serialization;

namespace GKME;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(Dictionary<string, int>))]
public partial class AppJsonContext : JsonSerializerContext
{
}