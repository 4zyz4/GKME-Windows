using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using GKME.Core;
using GKME.Web;

namespace GKME.Ui;

/// <summary>
/// The embedded web controller's advertised address: the list of usable LAN
/// interfaces, the chosen one, and the URL / host / QR code that follow from it.
/// Split out of the view model so the web entry is one small module instead of
/// being interleaved with the device board and the dialogs.
/// </summary>
public sealed class WebAccess : INotifyPropertyChanged
{
    private LanInterfaceInfo? _selected;
    private string _url = "";
    private string _host = "";
    private Bitmap? _qrCode;

    public ObservableCollection<LanInterfaceInfo> Interfaces { get; } = new();

    /// <summary>Selected interface; drives the advertised URL and QR code.</summary>
    public LanInterfaceInfo? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            _selected = value;
            OnPropertyChanged();
            Update();
        }
    }

    /// <summary>LAN URL of the embedded web controller (HTTP).</summary>
    public string Url
    {
        get => _url;
        private set
        {
            _url = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasUrl));
        }
    }

    /// <summary>Current advertised host, e.g. "http://192.168.1.5:37285".</summary>
    public string Host
    {
        get => _host;
        private set { _host = value; OnPropertyChanged(); }
    }

    /// <summary>QR code pointing at <see cref="Url"/>.</summary>
    public Bitmap? QrCode
    {
        get => _qrCode;
        private set { _qrCode = value; OnPropertyChanged(); }
    }

    public bool HasUrl => !string.IsNullOrEmpty(_url);

    /// <summary>Populates the interface list and selects a sensible default.
    /// Called once the embedded HTTP host is up.</summary>
    public void Configure()
    {
        Interfaces.Clear();
        foreach (var nic in StaticHost.EnumerateLanInterfaces())
            Interfaces.Add(nic);

        var primary = StaticHost.GetPrimaryLanAddress();
        _selected = Interfaces.FirstOrDefault(i => i.Address == primary) ?? Interfaces.FirstOrDefault();
        OnPropertyChanged(nameof(Selected));
        Update();
    }

    private void Update()
    {
        var address = _selected?.Address;
        if (string.IsNullOrEmpty(address))
        {
            Url = "";
            Host = "";
            QrCode = null;
            return;
        }

        var url = StaticHost.BuildUrl(address);
        Url = url;
        Host = $"http://{address}:{StaticHost.HttpPort}";
        try { QrCode = QrCodeImage.Render(url); }
        catch { QrCode = null; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
