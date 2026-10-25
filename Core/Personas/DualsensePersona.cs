using GamepadEmu.Protocol;
using GKMD;

namespace GKME.Core.Personas;

/// <summary>
/// DualSense (USB/IP composite persona "dualsense"): writes the 63-byte DS5
/// input report, forwards output report 0x02's rumble bytes, decodes Sony's
/// feature-report 0x80 test commands (1 kHz tone), and exposes the 4-channel
/// speaker + haptic audio endpoint.
/// </summary>
internal sealed class DualsensePersona : IControllerPersona
{
    private readonly TouchTracker _touch = new();
    private readonly byte[] _report = new byte[DualsenseInputReport.Size];
    private byte _sequence;

    public ControllerMode Mode => ControllerMode.Dualsense;
    public string PrimaryProfileId => "dualsense";
    public string? SecondaryProfileId => null;
    public PersonaAudioLayout AudioLayout => PersonaAudioLayout.DualsenseFourChannel;
    public bool HasLed => true;
    public bool SendsTriggerRumble => false;
    public bool NeedsHapticKeepAlive => false;

    public void WriteInput(GamepadInput input, IPersonaHost host)
    {
        var controller = host.PrimaryController;
        if (controller == null) return;

        DualsenseInputReport.Build(input, _touch, _sequence++, host.TimestampMs, _report);
        host.SubmitReport(controller, _report);
    }

    public void DecodeOutput(GKController controller, in GKOutputPacket packet, IPersonaHost host)
    {
        if (packet.Source == GKOutputSource.HidFeature && packet.ReportId == 0x80)
        {
            ApplyTestCommand(packet.Data.Span, host);
            return;
        }

        var d = packet.Data.Span;
        if (d.Length < 4) return;

        // DualSense output report 0x02 (no report-id byte): [0]=validFlag0,
        // [1]=validFlag1, [2]=rightMotor (small), [3]=leftMotor (large).
        host.SetVibration(d[3], d[2]);
    }

    public void PumpHaptics(IPersonaHost host) { }

    /// <summary>Feature report 0x80 with [0]=6 (AUDIO), [1]=2 (WAVEOUT_CTRL),
    /// [2]!=0 turns the pad's internal 1 kHz sine generator on. The phone
    /// synthesises the tone locally, so only the on/off state is forwarded.</summary>
    private static void ApplyTestCommand(ReadOnlySpan<byte> d, IPersonaHost host)
    {
        if (d.Length < 3) return;
        if (d[0] != 6 || d[1] != 2) return;

        host.SetTestTone(d[2] != 0);
    }
}
