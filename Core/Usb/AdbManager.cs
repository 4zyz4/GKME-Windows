using System.Diagnostics;
using System.Text;

namespace GKME.Core.Usb;

/// <summary>A device reported by `adb devices -l`.</summary>
public sealed class AdbDeviceInfo
{
    public required string Serial { get; init; }
    public string State { get; set; } = "unknown";
    public string? Model { get; set; }

    /// <summary>Human friendly name (model if known, otherwise the serial).</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Model) ? Serial : Model!;

    public bool IsReady => string.Equals(State, "device", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Locates and drives the Android Debug Bridge (adb) executable. Resolution
/// order follows the product requirement: an already-running adb server first
/// (so its version is reused and never killed/restarted), then adb available on
/// PATH or in a standard Android SDK location, and finally the adb bundled next
/// to the application in <c>platform-tools\</c>.
/// </summary>
public sealed class AdbManager : IDisposable
{
    /// <summary>Port the phone listens on for the USB/TCP transport.</summary>
    public const int DevicePort = 37284;

    private readonly object _runLock = new();
    private bool _serverEnsured;
    private bool _missingLogged;
    private bool _availableLogged;

    public string? AdbPath { get; private set; }
    public bool Available => !string.IsNullOrEmpty(AdbPath);
    public string? LastError { get; private set; }

    /// <summary>True while the app is using an adb server that was already
    /// running before GKME started (as opposed to one GKME launched).</summary>
    public bool UsingRunningServer { get; private set; }

    public bool TryInitialize()
    {
        var path = LocateAdb();
        if (path == null)
        {
            LastError = "未找到 adb（请安装 Android SDK platform-tools，或将 adb 放入程序目录的 platform-tools 文件夹）";
            AdbPath = null;
            if (!_missingLogged)
            {
                Log.Warn("adb not found; USB mode unavailable");
                _missingLogged = true;
            }
            return false;
        }

        AdbPath = path;
        LastError = null;
        if (!_availableLogged)
        {
            Log.Info($"adb: {path}");
            _availableLogged = true;
        }
        return true;
    }

    private static string? LocateAdb()
    {
        var candidates = new List<string>();

        // 1) An adb server that is already running — reuse its binary so we do
        //    not force a version mismatch restart of the shared server.
        try
        {
            foreach (var p in Process.GetProcessesByName("adb"))
            {
                try
                {
                    var file = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(file) && File.Exists(file))
                        candidates.Add(file);
                }
                catch { }
            }
        }
        catch { }

        // 2) adb on PATH.
        try
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    var full = Path.Combine(dir.Trim(), PlatformInfo.AdbFileName);
                    if (File.Exists(full)) candidates.Add(full);
                }
                catch { }
            }
        }
        catch { }

        // 3) Standard Android SDK locations (ANDROID_HOME / ANDROID_SDK_ROOT /
        //    the default per-user SDK path: %LOCALAPPDATA%\Android\Sdk on
        //    Windows, ~/Android/Sdk on Linux and macOS).
        var sdkRoots = new[]
        {
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Android", "Sdk"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Android", "Sdk"),
        };
        foreach (var root in sdkRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            try
            {
                var full = Path.Combine(root, "platform-tools", PlatformInfo.AdbFileName);
                if (File.Exists(full)) candidates.Add(full);
            }
            catch { }
        }

        // 4) Bundled adb shipped next to the executable.
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "platform-tools", PlatformInfo.AdbFileName));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "adb", PlatformInfo.AdbFileName));

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return EnsureExecutable(c);
        }
        return null;
    }

    /// <summary>Linux tarballs and zip extracts do not always keep the
    /// executable bit; adb is useless without it.</summary>
    private static string EnsureExecutable(string path)
    {
        if (PlatformInfo.IsWindows) return path;
#pragma warning disable CA1416 // guarded by the platform check above
        try
        {
            var mode = File.GetUnixFileMode(path);
            var wanted = mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            if (wanted != mode) File.SetUnixFileMode(path, wanted);
        }
        catch { }
#pragma warning restore CA1416
        return path;
    }

    /// <summary>Starts the adb server if none is running. Returns false when adb
    /// cannot be launched (e.g. missing USB driver / DLLs).</summary>
    public bool EnsureServer()
    {
        if (!Available) return false;
        if (_serverEnsured) return true;

        bool wasRunning = IsServerRunning();
        UsingRunningServer = wasRunning;
        if (wasRunning)
        {
            _serverEnsured = true;
            return true;
        }

        var (exit, _, stderr) = Run("start-server");
        if (exit != 0)
        {
            LastError = string.IsNullOrWhiteSpace(stderr) ? "adb start-server 失败" : stderr.Trim();
            return false;
        }
        _serverEnsured = true;
        return true;
    }

    private static bool IsServerRunning()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("adb")) return true;
        }
        catch { }
        return false;
    }

    public IReadOnlyList<AdbDeviceInfo> ListDevices()
    {
        var result = new List<AdbDeviceInfo>();
        if (!Available) return result;

        var (exit, stdout, _) = Run("devices", "-l");
        if (exit != 0) return result;

        bool started = false;
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (!started)
            {
                if (line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
                    started = true;
                continue;
            }
            if (line.Length == 0) continue;

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            // USB mode only: skip wireless-adb entries — mDNS serials
            // ("adb-..._adb-tls-connect._tcp") and "host:port" serials created by
            // `adb connect`. Those are not physically attached over USB.
            if (parts[0].Contains(':') || parts[0].Contains("_adb-tls-")) continue;

            var info = new AdbDeviceInfo { Serial = parts[0], State = parts[1] };
            foreach (var token in parts)
            {
                if (token.StartsWith("model:", StringComparison.Ordinal))
                    info.Model = token.Substring("model:".Length).Replace('_', ' ');
            }
            result.Add(info);
        }
        return result;
    }

    /// <summary>Discovery snapshot for one attached phone. Mirrors what the WiFi
    /// broadcast carries: the real (system) device name, the ANDROID_ID used as
    /// the phone identity, and whether the phone's GKME service is actually
    /// listening on <see cref="DevicePort"/>.</summary>
    public sealed record AdbProbeResult
    {
        public bool ServiceListening { get; init; }
        /// <summary>System device name, falling back to the adb model.</summary>
        public string? DeviceName { get; init; }
        public string? AndroidId { get; init; }
    }

    /// <summary>
    /// Reads the device's system name and ANDROID_ID over a single `adb shell`
    /// round-trip and checks whether the GKME USB service is listening on
    /// <paramref name="port"/>. A phone that has not started its service is not
    /// reported (its card stays hidden, like an idle WiFi phone that is not
    /// broadcasting). Returns an empty result on any failure.
    /// </summary>
    public AdbProbeResult ProbeUsbDevice(string serial, int port = DevicePort)
    {
        if (!Available) return new AdbProbeResult();

        var portHex = port.ToString("X4");
        const string script =
            "echo N=; settings get global device_name; " +
            "echo M=; getprop ro.product.model; " +
            "echo I=; settings get secure android_id; " +
            "echo T=; cat /proc/net/tcp /proc/net/tcp6 2>/dev/null";

        var (exit, stdout, _) = Run("-s", serial, "shell", script);
        if (exit != 0) return new AdbProbeResult();

        string? systemName = null, model = null, androidId = null;
        var listening = false;
        var section = '\0';
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            switch (line)
            {
                case "N=": section = 'N'; continue;
                case "M=": section = 'M'; continue;
                case "I=": section = 'I'; continue;
                case "T=": section = 'T'; continue;
            }

            switch (section)
            {
                case 'N':
                    if (line.Length > 0) { systemName = NormalizeSetting(line); section = '\0'; }
                    break;
                case 'M':
                    if (line.Length > 0) { model = NormalizeSetting(line); section = '\0'; }
                    break;
                case 'I':
                    if (line.Length > 0) { androidId = NormalizeSetting(line); section = '\0'; }
                    break;
                case 'T':
                    // /proc/net/tcp[6] line: "sl local_address rem_address st ..."
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 4 &&
                        parts[1].EndsWith(":" + portHex, StringComparison.OrdinalIgnoreCase) &&
                        parts[3] == "0A")
                        listening = true;
                    break;
            }
        }

        return new AdbProbeResult
        {
            ServiceListening = listening,
            DeviceName = systemName ?? model,
            AndroidId = androidId,
        };
    }

    /// <summary>Trims a shell value, mapping empty/"null" (unset settings) to null.</summary>
    private static string? NormalizeSetting(string value)
    {
        var v = value.Trim();
        return v.Length == 0 || v.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : v;
    }

    /// <summary>Creates a port forward from the PC to the phone's transport port
    /// and returns the loopback port the PC should connect to, or null on
    /// failure. Uses <c>tcp:0</c> so adb picks a free port (avoids collisions
    /// when several phones are attached).</summary>
    public int? AddForward(string serial, int devicePort = DevicePort)
    {
        if (!Available) return null;
        var (exit, stdout, stderr) = Run("-s", serial, "forward", "tcp:0", $"tcp:{devicePort}");
        var text = (stdout + "\n" + stderr).Trim();
        foreach (var token in text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, out var port) && port > 0 && port <= 65535)
                return port;
        }
        return null;
    }

    public void RemoveForward(string serial, int localPort)
    {
        if (!Available) return;
        Run("-s", serial, "forward", "--remove", $"tcp:{localPort}");
    }

    private (int exit, string stdout, string stderr) Run(params string[] args)
    {
        if (!Available) return (-1, "", "adb not available");
        lock (_runLock)
        {
            try
            {
                var psi = new ProcessStartInfo(AdbPath!)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                foreach (var a in args) psi.ArgumentList.Add(a);

                using var p = Process.Start(psi);
                if (p == null) return (-1, "", "failed to start adb");

                var stdout = p.StandardOutput.ReadToEnd();
                var stderr = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(15000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return (-1, stdout, "adb 超时");
                }
                return (p.ExitCode, stdout, stderr);
            }
            catch (Exception ex)
            {
                return (-1, "", ex.Message);
            }
        }
    }

    internal string? RunForDiagnostics(params string[] args) => Run(args).stdout;

    public void Dispose()
    {
        // Do not kill the shared adb server: other tools may be using it.
    }
}
