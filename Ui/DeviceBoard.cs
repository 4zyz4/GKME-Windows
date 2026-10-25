using System.Collections.ObjectModel;
using GKME.Core;

namespace GKME.Ui;

/// <summary>
/// The device board: every discovered endpoint, the session ledger behind them,
/// and the rules that turn endpoints into cards and fold a manual card into a
/// discovered one. Pure data logic — no window, dialog or dispatcher — so the
/// grouping and merge rules can be exercised without Avalonia ever starting.
///
/// The view model forwards network events (already marshalled to the UI thread)
/// into it and republishes <see cref="Changed"/> and <see cref="Status"/> as
/// bindings; the shell owns the dialogs, and <see cref="WebAccess"/> owns the
/// advertised web address.
/// </summary>
public sealed class DeviceBoard
{
    private readonly NetworkService? _network;
    private readonly DeviceModeStore _modeStore;
    private readonly Dictionary<string, GamepadSession> _sessionMap = new();

    public DeviceBoard(NetworkService? network, DeviceModeStore modeStore)
    {
        _network = network;
        _modeStore = modeStore;
        // Every connection path (manual, USB, web, auto-reconnect) asks the board
        // for the device's saved mode before creating its controller.
        if (_network != null)
            _network.SavedModeResolver = device => _modeStore.Get(device.Identity.DeviceId);
    }

    public ObservableCollection<DeviceCardViewModel> Cards { get; } = new();
    public ObservableCollection<GamepadSession> Sessions { get; } = new();
    public bool HasActiveSession => Sessions.Count > 0;

    /// <summary>Raised when the card or session lists changed.</summary>
    public event Action? Changed;
    /// <summary>Raised with a localization key + args for the status line.</summary>
    public event Action<string, object[]>? Status;

    private void Report(string key, params object[] args) => Status?.Invoke(key, args);
    private void RaiseChanged() => Changed?.Invoke();

    // ----- lookups ---------------------------------------------------------

    public DeviceEndpointViewModel? FindEndpoint(string endpointId)
    {
        foreach (var card in Cards)
        {
            var ep = card.FindEndpoint(endpointId);
            if (ep != null) return ep;
        }
        return null;
    }

    public bool IsConnected(string endpointId) => _sessionMap.ContainsKey(endpointId);

    /// <summary>Adds a card for a manually entered address (applying its saved
    /// mode) so the caller can connect it like any discovered endpoint.</summary>
    public DeviceCardViewModel AddManualCard(DiscoveredDevice device)
    {
        var card = new DeviceCardViewModel(device);
        ApplySavedMode(card);
        Cards.Add(card);
        return card;
    }

    // ----- connect / disconnect -------------------------------------------

    public Task ConnectAsync(DeviceCardViewModel card)
    {
        var ep = card.ActiveEndpoint;
        return ep == null ? Task.CompletedTask : ConnectEndpointAsync(ep);
    }

    public async Task ConnectEndpointAsync(DeviceEndpointViewModel ep)
    {
        if (_network == null) return;

        var identity = ep.Device.Identity;
        if (_sessionMap.ContainsKey(identity.EndpointId)) return;

        ep.IsConnecting = true;
        Report("Status_ConnectingTo", identity.EndpointLabel);
        var session = await _network.ConnectAsync(ep.Device);
        if (session != null)
        {
            _sessionMap[identity.EndpointId] = session;
            Report("Status_ConnectedTo", identity.EndpointLabel);
        }
        else
        {
            ep.IsConnecting = false;
            Report("Status_ConnectFailed", identity.EndpointLabel);
        }
    }

    public Task DisconnectAsync(DeviceCardViewModel card)
    {
        var ep = card.ActiveEndpoint;
        return ep == null ? Task.CompletedTask : DisconnectEndpointAsync(ep);
    }

    public async Task DisconnectEndpointAsync(DeviceEndpointViewModel ep)
    {
        var endpointId = ep.Endpoint;
        if (_sessionMap.TryGetValue(endpointId, out var session))
        {
            if (_network != null) await _network.SendDisconnectAsync(session.Device);
            else session.Disconnect();
            _sessionMap.Remove(endpointId);
        }
        ep.IsConnected = false;
        ep.IsConnecting = false;
        ep.IsReconnecting = false;
    }

    public async Task DisconnectAllAsync()
    {
        if (_network != null) await _network.DisconnectAllAsync();

        Sessions.Clear();
        _sessionMap.Clear();
        RaiseChanged();

        foreach (var card in Cards)
        {
            foreach (var ep in card.Endpoints)
            {
                ep.IsConnected = false;
                ep.IsConnecting = false;
                ep.IsReconnecting = false;
            }
        }

        Report("Status_AllDisconnected");
    }

    public async Task SetModeAsync(DeviceCardViewModel card, ControllerMode mode)
    {
        var ep = card.ActiveEndpoint;
        if (ep == null) return;

        if (_sessionMap.TryGetValue(ep.Endpoint, out var session))
        {
            ControllerMode actualMode = await session.SetControllerModeAsync(mode);
            card.Mode = actualMode;
            _modeStore.Set(card.DeviceId, actualMode);
            if (actualMode != mode)
                Report("Status_ModeFallback");
        }
        else
        {
            card.Mode = mode;
            _modeStore.Set(card.DeviceId, mode);
        }
    }

    /// <summary>Rebuilds the board from the network service's current snapshot.</summary>
    public void Refresh()
    {
        if (_network == null) return;

        Cards.Clear();
        Sessions.Clear();
        _sessionMap.Clear();

        foreach (var session in _network.ActiveSessions)
        {
            Sessions.Add(session);
            _sessionMap[session.Device.Identity.EndpointId] = session;
        }

        foreach (var device in _network.DiscoveredDevices)
            AddOrUpdateCard(device);

        foreach (var (endpointId, _) in _sessionMap)
        {
            var ep = FindEndpoint(endpointId);
            if (ep != null) ep.IsConnected = true;
        }

        RaiseChanged();
    }

    // ----- network events (already marshalled to the UI thread) -----------

    public void OnDeviceDiscovered(DiscoveredDevice device)
    {
        var identity = device.Identity;
        var existed = Cards.Any(c => c.DeviceId == identity.DeviceId && c.FindEndpoint(identity.EndpointId) != null);
        AddOrUpdateCard(device);
        if (!existed)
            Report("Status_DeviceFound", identity.EndpointLabel);
    }

    public void OnDeviceLost(DiscoveredDevice device)
    {
        var identity = device.Identity;
        // Never drop a card that is currently in use.
        if (_sessionMap.ContainsKey(identity.EndpointId)) return;

        var card = Cards.FirstOrDefault(c => c.DeviceId == identity.DeviceId);
        if (card == null) return;

        var ep = card.FindEndpoint(identity.EndpointId);
        if (ep == null) return;
        if (ep.IsConnected || ep.IsConnecting) return;

        card.RemoveEndpoint(ep);
        if (card.Endpoints.Count == 0)
            Cards.Remove(card);
    }

    public void OnDeviceInfoUpdated(DiscoveredDevice device)
    {
        var identity = device.Identity;
        var ep = FindEndpoint(identity.EndpointId);
        if (ep == null) return;
        var card = FindCardByEndpoint(identity.EndpointId);
        if (card == null) return;

        if (!string.IsNullOrWhiteSpace(device.DeviceName)) card.DeviceName = device.DeviceName;
        if (!string.IsNullOrWhiteSpace(device.MacAddress)) card.MacAddress = device.MacAddress;

        // The handshake may reveal a MAC that another (broadcast) card already
        // owns. Merge the manual card into the discovered one so a device is
        // never listed twice. USB cards keep their own identity (adb serial)
        // and must never be folded into a WiFi card.
        var mac = device.MacAddress;
        if (!device.IsUsb && !string.IsNullOrWhiteSpace(mac))
        {
            var other = Cards.FirstOrDefault(c => c != card && c.DeviceId == mac);
            if (other != null)
                MergeCards(other, card);
        }
    }

    public void OnSessionStarted(GamepadSession session)
    {
        var identity = session.Device.Identity;
        if (!Sessions.Contains(session))
            Sessions.Add(session);
        _sessionMap[identity.EndpointId] = session;
        RaiseChanged();

        var card = AddOrUpdateCard(session.Device);
        if (!string.IsNullOrWhiteSpace(session.Device.DeviceName)) card.DeviceName = session.Device.DeviceName;
        if (!string.IsNullOrWhiteSpace(session.Device.MacAddress)) card.MacAddress = session.Device.MacAddress;

        var ep = card.FindEndpoint(identity.EndpointId);
        if (ep != null)
        {
            ep.IsConnected = true;
            ep.IsConnecting = false;
            ep.IsReconnecting = false;
        }
        card.SetModeRestored(session.Mode);
    }

    public void OnSessionEnded(GamepadSession session)
    {
        Sessions.Remove(session);
        RaiseChanged();
        var identity = session.Device.Identity;
        _sessionMap.Remove(identity.EndpointId);

        var card = FindCardByEndpoint(identity.EndpointId);
        var ep = card?.FindEndpoint(identity.EndpointId);
        if (ep != null)
        {
            ep.IsConnected = false;
            ep.IsConnecting = false;
            ep.IsReconnecting = false;
        }
        if (card != null)
            card.SetModeRestored(ControllerMode.Ds4);
        Report("Status_DisconnectedFrom", identity.EndpointLabel);
    }

    public void OnSessionConnectionLost(DeviceIdentity identity)
    {
        var card = FindCardByEndpoint(identity.EndpointId);
        var ep = card?.FindEndpoint(identity.EndpointId);
        if (ep != null)
        {
            ep.IsConnected = false;
            ep.IsReconnecting = true;
        }
        Report("Status_ConnectionLost", identity.EndpointLabel);
    }

    // ----- card grouping ---------------------------------------------------

    private void ApplySavedMode(DeviceCardViewModel card)
    {
        var saved = _modeStore.Get(card.DeviceId);
        if (saved != null)
            card.Mode = saved.Value;
    }

    private DeviceCardViewModel AddOrUpdateCard(DiscoveredDevice device)
    {
        var key = device.Identity.DeviceId;
        var card = Cards.FirstOrDefault(c => c.DeviceId == key);
        if (card == null)
        {
            card = new DeviceCardViewModel(device);
            ApplySavedMode(card);
            Cards.Add(card);
        }
        else
        {
            card.AddOrUpdateEndpoint(device);
            if (!string.IsNullOrWhiteSpace(device.DeviceName)) card.DeviceName = device.DeviceName;
            if (!string.IsNullOrWhiteSpace(device.MacAddress)) card.MacAddress = device.MacAddress;
        }
        return card;
    }

    private DeviceCardViewModel? FindCardByEndpoint(string endpointId) =>
        Cards.FirstOrDefault(c => c.FindEndpoint(endpointId) != null);

    /// <summary>Move every endpoint of <paramref name="remove"/> into
    /// <paramref name="keep"/> and drop the now-empty card.</summary>
    private void MergeCards(DeviceCardViewModel keep, DeviceCardViewModel remove)
    {
        foreach (var ep in remove.Endpoints.ToList())
        {
            var device = ep.Device;
            remove.RemoveEndpoint(ep);
            var kep = keep.AddOrUpdateEndpoint(device);
            if (!string.IsNullOrWhiteSpace(device.DeviceName)) keep.DeviceName = device.DeviceName;
            if (!string.IsNullOrWhiteSpace(device.MacAddress)) keep.MacAddress = device.MacAddress;
            kep.IsConnected = ep.IsConnected;
            kep.IsConnecting = ep.IsConnecting;
            kep.IsReconnecting = ep.IsReconnecting;
        }

        // Every endpoint is on `keep` now. Without this the emptied card stayed
        // in the list and rendered as a blank, button-less card (HEAD leaked it).
        Cards.Remove(remove);
    }
}
