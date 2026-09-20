using System.Net.Sockets;
using GamepadEmu.Protocol;

namespace GKME.Core.Usb;

/// <summary>
/// One TCP link to the phone over an adb-forwarded port. Reuses the existing
/// protobuf messages, framed for the stream transport as:
/// <c>[4-byte big-endian length][1-byte type][protobuf payload]</c> where the
/// length counts the type byte plus the payload. This mirrors the phone's
/// <c>UsbService</c> framing exactly.
/// </summary>
public sealed class UsbConnection : IDisposable
{
    private const int MaxFrameBytes = 16 * 1024 * 1024;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private int _closed;

    public event Action<ClientToServer>? OnClientToServer;
    public event Action<GamepadInput>? OnGamepadInput;
    public event Action? OnClosed;

    public UsbConnection(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public Task StartReceiveLoopAsync() => Task.Run(ReceiveLoopAsync);

    public async Task SendAsync(byte type, byte[] payload)
    {
        if (payload.Length + 1 > MaxFrameBytes) return;
        try
        {
            await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                int length = 1 + payload.Length;
                var header = new byte[5];
                header[0] = (byte)(length >> 24);
                header[1] = (byte)(length >> 16);
                header[2] = (byte)(length >> 8);
                header[3] = (byte)length;
                header[4] = type;
                await _stream.WriteAsync(header, _cts.Token).ConfigureAwait(false);
                await _stream.WriteAsync(payload, _cts.Token).ConfigureAwait(false);
                await _stream.FlushAsync(_cts.Token).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch
        {
            // Broken pipe — the receive loop / watchdog will notice and tear down.
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var header = new byte[4];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await ReadExactAsync(header, 0, 4, _cts.Token).ConfigureAwait(false);
                int length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
                if (length <= 0 || length > MaxFrameBytes) break;

                var frame = new byte[length];
                await ReadExactAsync(frame, 0, length, _cts.Token).ConfigureAwait(false);

                byte type = frame[0];
                if (length <= 1) continue;
                try
                {
                    if (type == NetworkService.TypeClientToServer)
                    {
                        var msg = ClientToServer.Parser.ParseFrom(frame, 1, length - 1);
                        OnClientToServer?.Invoke(msg);
                    }
                    else if (type == NetworkService.TypeGamepadInput)
                    {
                        var input = GamepadInput.Parser.ParseFrom(frame, 1, length - 1);
                        OnGamepadInput?.Invoke(input);
                    }
                }
                catch { }
            }
        }
        catch { }
        finally
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                try { OnClosed?.Invoke(); } catch { }
            }
        }
    }

    private async Task ReadExactAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = await _stream.ReadAsync(buffer.AsMemory(offset + read, count - read), ct).ConfigureAwait(false);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _stream.Dispose(); } catch { }
        try { _client.Dispose(); } catch { }
        try { _sendLock.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}
