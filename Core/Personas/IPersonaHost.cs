using GKMD;

namespace GKME.Core.Personas;

/// <summary>Audio stream layout an emulated persona exposes to the phone.</summary>
internal enum PersonaAudioLayout
{
    /// <summary>No audio endpoint (Xbox, Switch).</summary>
    None,

    /// <summary>DS4: 2-channel 32 kHz headset audio, folded to ch1 as a mono mix.</summary>
    Ds4Stereo,

    /// <summary>DualSense: 4 channels (speaker L/R + haptic L/R), remapped 1:1.</summary>
    DualsenseFourChannel,
}

/// <summary>Switch HD-rumble band parameters forwarded to the phone, which
/// synthesises the LRA waveform locally.</summary>
internal readonly record struct HdRumbleBands(
    float LeftHighFreqHz, float LeftHighAmp, float LeftLowFreqHz, float LeftLowAmp,
    float RightHighFreqHz, float RightHighAmp, float RightLowFreqHz, float RightLowAmp)
{
    public static readonly HdRumbleBands Silent = new(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f);

    public HdRumbleBands WithLeft(float highFreq, float highAmp, float lowFreq, float lowAmp)
        => new(highFreq, highAmp, lowFreq, lowAmp,
               RightHighFreqHz, RightHighAmp, RightLowFreqHz, RightLowAmp);

    public HdRumbleBands WithRight(float highFreq, float highAmp, float lowFreq, float lowAmp)
        => new(LeftHighFreqHz, LeftHighAmp, LeftLowFreqHz, LeftLowAmp,
               highFreq, highAmp, lowFreq, lowAmp);
}

/// <summary>
/// Everything a persona may ask of its session: write a finished input report
/// into an emulated device, or update the state the phone mirrors. A persona
/// never touches the session's queues, watchdog, downlink or teardown — that is
/// why all seven wire formats can live outside <see cref="GamepadSession"/>.
/// </summary>
internal interface IPersonaHost
{
    /// <summary>The emulated device an input report goes to.</summary>
    GKController? PrimaryController { get; }

    /// <summary>The second device of a dual-controller persona (Joy-Con right),
    /// or null for every single-device persona.</summary>
    GKController? SecondaryController { get; }

    /// <summary>Session clock used for the report timestamps some personas carry.</summary>
    long TimestampMs { get; }

    /// <summary>Hands a completed input report to one emulated device.</summary>
    void SubmitReport(GKController controller, ReadOnlySpan<byte> report);

    /// <summary>Updates the rumble lanes the phone mirrors (0-255 each).</summary>
    void SetVibration(byte largeMotor, byte smallMotor);

    /// <summary>Updates the Xbox impulse-trigger amplitudes (0-255 each).</summary>
    void SetTriggerRumble(byte left, byte right);

    /// <summary>Updates the DualSense 1 kHz test-tone state.</summary>
    void SetTestTone(bool enabled);

    /// <summary>Sends Switch HD-rumble band parameters to the phone.</summary>
    void SendHdRumble(in HdRumbleBands bands);
}
