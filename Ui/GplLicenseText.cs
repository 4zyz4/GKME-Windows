using System;
using System.IO;
using Avalonia.Platform;
using GKME.Core;
using GKME.Localization;

namespace GKME.Ui;

/// <summary>Loads the full GPLv3 text embedded in the application, shared by
/// the About dialog and the first-launch consent dialog.</summary>
internal static class GplLicenseText
{
    private static readonly Uri LicenseUri = new("avares://GKME/Assets/GPL-3.0.txt");

    public static string Load()
    {
        try
        {
            using var stream = AssetLoader.Open(LicenseUri);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            Log.Warn($"license: could not load GPL text: {ex.Message}");
            return Localizer.Instance.Get("About_GplUnavailable");
        }
    }
}
