using GamepadEmu.Protocol;
using GKMD;

namespace GKME.Core.Personas;

/// <summary>
/// Xbox 360 wired pad (vendor-class "xbox360" profile): writes the 20-byte
/// XInput-layout report and forwards its two rumble bytes. No LED, no audio, no
/// trigger rumble.
/// </summary>
internal sealed class Xbox360Persona : IControllerPersona
{
    private readonly byte[] _report = new byte[Xbox360InputReport.Size];

    public ControllerMode Mode => ControllerMode.Xbox360;
    public string PrimaryProfileId => "xbox360";
    public string? SecondaryProfileId => null;
    public PersonaAudioLayout AudioLayout => PersonaAudioLayout.None;
    public bool HasLed => false;
    public bool SendsTriggerRumble => false;
    public bool NeedsHapticKeepAlive => false;

    public void WriteInput(GamepadInput input, IPersonaHost host)
    {
        var controller = host.PrimaryController;
        if (controller == null) return;

        Xbox360InputReport.Build(input, _report);
        host.SubmitReport(controller, _report);
    }

    public void DecodeOutput(GKController controller, in GKOutputPacket packet, IPersonaHost host)
    {
        if (packet.Source == GKOutputSource.HidFeature && packet.ReportId == 0x80) return;

        var d = packet.Data.Span;
        if (d.Length < 4) return;

        // Interrupt-OUT payload (the RID byte is stripped by the backend):
        // [2]=large motor, [3]=small motor.
        host.SetVibration(d[2], d[3]);
    }

    public void PumpHaptics(IPersonaHost host) { }
}

/// <summary>
/// Shared implementation of the two GIP personas (Xbox One / Series). Both speak
/// the same motor command; the Series payload only appends the console-function
/// map byte that carries the Share key.
/// </summary>
internal abstract class GipPersona : IControllerPersona
{
    private protected GipPersona(ControllerMode mode, string profileId)
    {
        Mode = mode;
        PrimaryProfileId = profileId;
    }

    public ControllerMode Mode { get; }
    public string PrimaryProfileId { get; }
    public string? SecondaryProfileId => null;
    public PersonaAudioLayout AudioLayout => PersonaAudioLayout.None;
    public bool HasLed => false;
    public bool SendsTriggerRumble => true;
    public bool NeedsHapticKeepAlive => false;

    public abstract void WriteInput(GamepadInput input, IPersonaHost host);

    public void DecodeOutput(GKController controller, in GKOutputPacket packet, IPersonaHost host)
    {
        if (packet.Source == GKOutputSource.HidFeature && packet.ReportId == 0x80) return;

        var d = packet.Data.Span;
        if (d.Length < 4) return;

        if (packet.Source == GKOutputSource.HidOutput)
        {
            if (d.Length >= 6)
            {
                // GIP Direct Motor Command ([MS-GIPUSB] Table 56), body after the
                // frame header: [2]=left impulse, [3]=right impulse, [4]=left
                // (strong) motor, [5]=right (weak) motor. Every level is a
                // percentage (0-100); the phone's rumble lane and the DualSense /
                // Xbox 360 paths use 0-255, so scale up (full rumble would
                // otherwise only reach ~39% and feel far too faint).
                host.SetVibration(ScaleGipLevel(d[4]), ScaleGipLevel(d[5]));

                // Impulse-trigger amplitudes, forwarded as a separate lane. The
                // downlink owns the last-known pair and suppresses redundant
                // updates (and echoes that pair in the compact frame), so this
                // reports the current value unconditionally.
                host.SetTriggerRumble(ScaleGipLevel(d[2]), ScaleGipLevel(d[3]));
            }
            return;
        }

        host.SetVibration(d[2], d[3]);
    }

    public void PumpHaptics(IPersonaHost host) { }

    /// <summary>GIP levels are percentages (0-100); the phone expects 0-255.</summary>
    private static byte ScaleGipLevel(byte level)
        => (byte)Math.Min(255, level * 255 / 100);
}

/// <summary>Xbox One (Model 1708, "xbox-one"): 14-byte GIP gamepad payload.</summary>
internal sealed class XboxOnePersona : GipPersona
{
    public XboxOnePersona() : base(ControllerMode.XboxOne, "xbox-one") { }

    public override void WriteInput(GamepadInput input, IPersonaHost host)
    {
        var controller = host.PrimaryController;
        if (controller == null) return;

        Span<byte> payload = stackalloc byte[XboxOneGipInput.PayloadSize];
        XboxOneGipInput.BuildPayload(input, payload);
        host.SubmitReport(controller, payload);
    }
}

/// <summary>Xbox Series X|S (Model 1914, "xbox-series"): the 14-byte payload plus
/// the console-function-map byte that carries Share.</summary>
internal sealed class XboxSeriesPersona : GipPersona
{
    public XboxSeriesPersona() : base(ControllerMode.XboxSeries, "xbox-series") { }

    public override void WriteInput(GamepadInput input, IPersonaHost host)
    {
        var controller = host.PrimaryController;
        if (controller == null) return;

        Span<byte> payload = stackalloc byte[XboxOneGipInput.SeriesPayloadSize];
        XboxOneGipInput.BuildSeriesPayload(input, payload);
        host.SubmitReport(controller, payload);
    }
}
