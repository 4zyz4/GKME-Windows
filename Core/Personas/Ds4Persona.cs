using GamepadEmu.Protocol;
using GKMD;

namespace GKME.Core.Personas;

/// <summary>
/// DualShock 4 (USB/IP composite persona "dualshock-4-v2"): writes the 63-byte
/// DS4 input report, forwards the two rumble bytes of output report 0x02, and
/// exposes the 2-channel headset audio endpoint.
/// </summary>
internal sealed class Ds4Persona : IControllerPersona
{
    private readonly TouchTracker _touch = new();
    private readonly byte[] _report = new byte[Ds4InputReport.Size];

    public ControllerMode Mode => ControllerMode.Ds4;
    public string PrimaryProfileId => "dualshock-4-v2";
    public string? SecondaryProfileId => null;
    public PersonaAudioLayout AudioLayout => PersonaAudioLayout.Ds4Stereo;
    public bool HasLed => true;
    public bool SendsTriggerRumble => false;
    public bool NeedsHapticKeepAlive => false;

    public void WriteInput(GamepadInput input, IPersonaHost host)
    {
        var controller = host.PrimaryController;
        if (controller == null) return;

        Ds4InputReport.Build(input, _touch, host.TimestampMs, _report);
        host.SubmitReport(controller, _report);
    }

    public void DecodeOutput(GKController controller, in GKOutputPacket packet, IPersonaHost host)
    {
        // Feature report 0x80 is Sony's test-command surface. Only the DualSense
        // persona acts on it; the DS4 ignores it instead of misreading the
        // command bytes as rumble amplitudes.
        if (packet.Source == GKOutputSource.HidFeature && packet.ReportId == 0x80) return;

        var d = packet.Data.Span;
        if (d.Length < 4) return;

        // Legacy mapping kept verbatim: the session-level decoder used its
        // generic fallback here, which assigned [2] to the large lane and [3] to
        // the small one. (The DualSense persona below maps them the other way
        // round; see the note in the refactor summary before changing this.)
        host.SetVibration(d[2], d[3]);
    }

    public void PumpHaptics(IPersonaHost host) { }
}
