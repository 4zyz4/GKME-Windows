using GamepadEmu.Protocol;
using GKMD;

namespace GKME.Core.Personas;

/// <summary>
/// Switch HD-rumble state for one persona: decodes the pad's 4-byte per-side
/// rumble blocks into (frequency, amplitude) bands, suppresses redundant updates,
/// and holds the last state for the keep-alive pump.
///
/// The phone synthesises the LRA waveform locally, so only these parameters
/// cross the link. A stutter then delays an update instead of tearing a hole in
/// the PCM stream, which the phone hears as a pop.
/// </summary>
internal sealed class SwitchHaptics
{
    private readonly object _sync = new();
    private HdRumbleBands _last = HdRumbleBands.Silent;

    /// <summary>The rumble payload of an output 0x01/0x10 report, or empty when
    /// this packet carries no rumble. <paramref name="minLength"/> differs per
    /// persona: a Pro report carries both sides, a single Joy-Con only its own.</summary>
    public static ReadOnlySpan<byte> RumblePayload(in GKOutputPacket packet, int minLength)
    {
        if (packet.Source != GKOutputSource.HidOutput) return default;
        if (packet.ReportId != 0x01 && packet.ReportId != 0x10) return default;

        var d = packet.Data.Span;
        return d.Length < minLength ? default : d;
    }

    /// <summary>Switch Pro: one report carries the left and right LRA blocks.</summary>
    public void ApplyBoth(ReadOnlySpan<byte> payload, IPersonaHost host)
    {
        Decode(payload.Slice(1, 4), out float lhf, out float lha, out float llf, out float lla);
        Decode(payload.Slice(5, 4), out float rhf, out float rha, out float rlf, out float rla);

        HdRumbleBands next = new(lhf, lha, llf, lla, rhf, rha, rlf, rla);
        lock (_sync)
        {
            bool changed =
                FreqChanged(lhf, _last.LeftHighFreqHz) || FreqChanged(llf, _last.LeftLowFreqHz) ||
                FreqChanged(rhf, _last.RightHighFreqHz) || FreqChanged(rlf, _last.RightLowFreqHz) ||
                AmpChanged(lha, _last.LeftHighAmp) || AmpChanged(lla, _last.LeftLowAmp) ||
                AmpChanged(rha, _last.RightHighAmp) || AmpChanged(rla, _last.RightLowAmp);

            _last = next;
            if (!changed) return;
        }

        host.SendHdRumble(next);
    }

    /// <summary>Joy-Con: one accent per report, driven into the lane of the half
    /// that produced it; the other lane keeps its last value.</summary>
    public void ApplyHalf(ReadOnlySpan<byte> payload, bool left, IPersonaHost host)
    {
        Decode(payload.Slice(1, 4), out float hf, out float ha, out float lf, out float la);

        HdRumbleBands next;
        lock (_sync)
        {
            next = left
                ? _last.WithLeft(hf, ha, lf, la)
                : _last.WithRight(hf, ha, lf, la);

            bool changed = left
                ? FreqChanged(hf, _last.LeftHighFreqHz) || FreqChanged(lf, _last.LeftLowFreqHz) ||
                  AmpChanged(ha, _last.LeftHighAmp) || AmpChanged(la, _last.LeftLowAmp)
                : FreqChanged(hf, _last.RightHighFreqHz) || FreqChanged(lf, _last.RightLowFreqHz) ||
                  AmpChanged(ha, _last.RightHighAmp) || AmpChanged(la, _last.RightLowAmp);

            _last = next;
            if (!changed) return;
        }

        host.SendHdRumble(next);
    }

    /// <summary>Re-sends the current state so the phone's synthesis thread and
    /// output device stay warm (silent when idle).</summary>
    public void Pump(IPersonaHost host)
    {
        HdRumbleBands bands;
        lock (_sync) bands = _last;
        host.SendHdRumble(bands);
    }

    /// <summary>Decode one 4-byte HD rumble block ([hf, hfAmp|hfHi, lf|lfAmpHi,
    /// lfAmpLo], SDL/dekuNukem encoding). Frequency: 10 * 2^(index/32); high
    /// index = hf/4 + 0x60, low index = lf + 0x40. Amplitude: table index / 100.</summary>
    private static void Decode(ReadOnlySpan<byte> b,
        out float highFreqHz, out float highAmp, out float lowFreqHz, out float lowAmp)
    {
        int encHiF = b[0] | ((b[1] & 0x01) << 8);
        int hiCode = b[1] & 0xFE;
        int encLoF = b[2] & 0x7F;
        int loMsb = (b[2] & 0x80) != 0 ? 1 : 0;
        int loLow = b[3];

        int hiIndex = encHiF / 4 + 0x60;
        highFreqHz = 10.0f * MathF.Pow(2.0f, hiIndex / 32.0f);
        highAmp = Math.Clamp((hiCode / 2) / 100.0f, 0f, 1f);

        int loIndex = encLoF + 0x40;
        lowFreqHz = 10.0f * MathF.Pow(2.0f, loIndex / 32.0f);
        int loAmpIdx = loLow >= 0x40 ? (loLow - 0x40) * 2 + loMsb : 0;
        lowAmp = Math.Clamp(loAmpIdx / 100.0f, 0f, 1f);
    }

    private static bool FreqChanged(float a, float b) => MathF.Abs(a - b) > 0.5f;
    private static bool AmpChanged(float a, float b) => MathF.Abs(a - b) > 0.002f;
}

/// <summary>Switch Pro Controller ("switch-pro"): one USB HID device, both rumble
/// lanes in one output report, 48-byte report-0x30 input body.</summary>
internal sealed class SwitchProPersona : IControllerPersona
{
    /// <summary>Both rumble blocks plus the report-number prefix byte.</summary>
    private const int RumblePayloadLength = 9;

    private readonly SwitchHaptics _haptics = new();

    public ControllerMode Mode => ControllerMode.SwitchPro;
    public string PrimaryProfileId => "switch-pro";
    public string? SecondaryProfileId => null;
    public PersonaAudioLayout AudioLayout => PersonaAudioLayout.None;
    public bool HasLed => true;
    public bool SendsTriggerRumble => false;
    public bool NeedsHapticKeepAlive => true;

    public void WriteInput(GamepadInput input, IPersonaHost host)
    {
        var controller = host.PrimaryController;
        if (controller == null) return;

        Span<byte> body = stackalloc byte[SwitchProInput.BodySize];
        SwitchProInput.BuildBody(input, body);
        host.SubmitReport(controller, body);
    }

    public void DecodeOutput(GKController controller, in GKOutputPacket packet, IPersonaHost host)
    {
        var payload = SwitchHaptics.RumblePayload(packet, RumblePayloadLength);
        if (payload.IsEmpty) return;

        _haptics.ApplyBoth(payload, host);
    }

    public void PumpHaptics(IPersonaHost host) => _haptics.Pump(host);
}

/// <summary>
/// Dual Joy-Con ("joycon-l" + "joycon-r"): two independent USB HID devices so
/// eden sees a genuine pair. Both halves receive the same 48-byte body (each
/// side ignores the other's bits), and each half's rumble accent is mapped to
/// the phone lane of the matching side.
/// </summary>
internal sealed class JoyConPersona : IControllerPersona
{
    /// <summary>One rumble block plus the report-number prefix byte.</summary>
    private const int RumblePayloadLength = 5;

    private readonly SwitchHaptics _haptics = new();

    public ControllerMode Mode => ControllerMode.JoyCon;
    public string PrimaryProfileId => "joycon-l";
    public string? SecondaryProfileId => "joycon-r";
    public PersonaAudioLayout AudioLayout => PersonaAudioLayout.None;
    public bool HasLed => true;
    public bool SendsTriggerRumble => false;
    public bool NeedsHapticKeepAlive => true;

    public void WriteInput(GamepadInput input, IPersonaHost host)
    {
        Span<byte> body = stackalloc byte[SwitchProInput.BodySize];
        SwitchProInput.BuildBody(input, body);

        if (host.PrimaryController is { } primary) host.SubmitReport(primary, body);
        if (host.SecondaryController is { } secondary) host.SubmitReport(secondary, body);
    }

    public void DecodeOutput(GKController controller, in GKOutputPacket packet, IPersonaHost host)
    {
        var payload = SwitchHaptics.RumblePayload(packet, RumblePayloadLength);
        if (payload.IsEmpty) return;

        // Reports from a controller being torn down during a mode switch are
        // ignored: it matches neither live half.
        if (ReferenceEquals(controller, host.PrimaryController))
            _haptics.ApplyHalf(payload, left: true, host);
        else if (ReferenceEquals(controller, host.SecondaryController))
            _haptics.ApplyHalf(payload, left: false, host);
    }

    public void PumpHaptics(IPersonaHost host) => _haptics.Pump(host);
}
