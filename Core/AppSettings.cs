namespace GKME.Core;

/// <summary>User preferences persisted to <c>%LOCALAPPDATA%\GKME\settings.json</c>.</summary>
public sealed class AppSettings
{
    /// <summary>Language code: <c>system</c>, <c>en</c>, <c>zh-CN</c>,
    /// <c>zh-HK</c>, <c>zh-TW</c>, <c>ru</c> or <c>fr</c>.</summary>
    public string Language { get; set; } = "system";

    /// <summary>True once the user accepted the GPLv3 on first launch. The
    /// Windows installer asks for the same consent itself, so this flag is
    /// only consulted on platforms without an installer (Linux).</summary>
    public bool LicenseAccepted { get; set; }
}
