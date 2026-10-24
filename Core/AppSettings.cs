namespace GKME.Core;

/// <summary>User preferences persisted to <c>%LOCALAPPDATA%\GKME\settings.json</c>.</summary>
public sealed class AppSettings
{
    /// <summary>Language code: <c>system</c>, <c>en</c>, <c>zh-CN</c>,
    /// <c>zh-HK</c>, <c>zh-TW</c>, <c>ru</c> or <c>fr</c>.</summary>
    public string Language { get; set; } = "system";
}
