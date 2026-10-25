using GamepadEmu.Protocol;
using GKMD;

namespace GKME.Core.Personas;

/// <summary>
/// One emulated controller persona: which GKMD profiles it creates, how it packs
/// an input frame onto the wire, how it reads that pad's output reports, and
/// whether the phone needs persona-specific feedback (LED, trigger rumble, HD
/// rumble keep-alive).
///
/// A persona instance is per session and per mode: it is replaced when the user
/// switches mode, so per-persona counters (touch tracking, report sequence)
/// live with the format that owns them instead of on the session.
/// </summary>
internal interface IControllerPersona
{
    ControllerMode Mode { get; }

    /// <summary>GKMD profile of the (first) emulated USB device.</summary>
    string PrimaryProfileId { get; }

    /// <summary>GKMD profile of the second emulated device, or null when the
    /// persona presents a single controller.</summary>
    string? SecondaryProfileId { get; }

    /// <summary>Audio layout this persona exposes to the phone.</summary>
    PersonaAudioLayout AudioLayout { get; }

    /// <summary>True when the emulated pad has an LED / player indicator.</summary>
    bool HasLed { get; }

    /// <summary>True when output reports carry impulse-trigger rumble instead of
    /// adaptive-trigger effects.</summary>
    bool SendsTriggerRumble { get; }

    /// <summary>True when the phone must keep receiving HD-rumble parameters so
    /// its synthesis thread stays warm (Switch Pro / Joy-Con).</summary>
    bool NeedsHapticKeepAlive { get; }

    /// <summary>Packs one input frame and submits it to the emulated device(s).</summary>
    void WriteInput(GamepadInput input, IPersonaHost host);

    /// <summary>Reads one output report from an emulated device.</summary>
    void DecodeOutput(GKController controller, in GKOutputPacket packet, IPersonaHost host);

    /// <summary>Re-sends the persona's haptic state; a no-op for personas that do
    /// not need a keep-alive.</summary>
    void PumpHaptics(IPersonaHost host);
}

/// <summary>Composition root of the persona seam: the one place that turns a
/// <see cref="ControllerMode"/> into a persona. Everywhere else the session
/// talks to <see cref="IControllerPersona"/> only.</summary>
internal static class PersonaCatalog
{
    public static IControllerPersona Create(ControllerMode mode) => mode switch
    {
        ControllerMode.Xbox360 => new Xbox360Persona(),
        ControllerMode.XboxOne => new XboxOnePersona(),
        ControllerMode.XboxSeries => new XboxSeriesPersona(),
        ControllerMode.Dualsense => new DualsensePersona(),
        ControllerMode.SwitchPro => new SwitchProPersona(),
        ControllerMode.JoyCon => new JoyConPersona(),
        _ => new Ds4Persona(),
    };
}
