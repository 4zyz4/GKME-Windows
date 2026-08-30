using System.Diagnostics.CodeAnalysis;

namespace GKME;

[RequiresUnreferencedCode("GKME uses System.Text.Json with runtime serialization for profile loading. Trimmed/NativeAOT builds may need runtimeconfig.json or source-generated serializers.")]
[RequiresDynamicCode("GKME uses System.Text.Json with runtime code generation for JSON deserialization.")]
internal static class AotAnnotations
{
}