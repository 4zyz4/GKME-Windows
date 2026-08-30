using System.IO;
using GKME;
using System.Text.Json;

namespace GKME.Core;

public sealed class DeviceModeStore
{
    private readonly string _filePath;
    private readonly Dictionary<string, ControllerMode> _modes = new();

    public DeviceModeStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GKME", "device-modes.json");
        Load();
    }

    public ControllerMode? Get(string key)
    {
        return _modes.TryGetValue(key, out var mode) ? mode : null;
    }

    public void Set(string key, ControllerMode mode)
    {
        _modes[key] = mode;
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
#pragma warning disable IL2026
#pragma warning disable IL3050
            var data = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
#pragma warning restore IL3050
#pragma warning restore IL2026
            if (data == null) return;
            foreach (var (k, v) in data)
            {
                if (Enum.IsDefined(typeof(ControllerMode), v))
                    _modes[k] = (ControllerMode)v;
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_modes.ToDictionary(kv => kv.Key, kv => (int)kv.Value));
            File.WriteAllText(_filePath, json);
        }
        catch { }
    }
}
