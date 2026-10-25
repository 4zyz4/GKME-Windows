using System.Diagnostics;
using GamepadEmu.Protocol;
using Google.Protobuf;
using GKME.Core.Personas;

namespace GKME.Core.Downlink;

/// <summary>
/// The one module that turns emulated-pad state into the feedback messages the
/// phone consumes, and the single owner of the last-known values they are built
/// from. Before it, each value was written and echoed by its own shallow private
/// sender on <c>GamepadSession</c>, so "the last LED colour" and "the last trigger
/// rumble" lived in three places at once; now every phone-bound packet is
/// constructed here and the state it is built from lives only here.
///
/// The surface groups the feedback by meaning — vibration, triggers, LED/test tone,
/// HD haptics, audio — and holds no session lifecycle of any kind.
/// </summary>
internal sealed class PhoneDownlink
{
    /// <summary>Sample rate the phone's native path runs at. Forwarding at the
    /// source rate avoids a lossy double resample (48k -> 44.1k on the PC, then
    /// 44.1k -> 48k on the phone), whose stateless per-window conversion injects a
    /// click at every buffer boundary (heard as noise on the voice coils).</summary>
    private const int TargetSampleRate = 48000;

    private const int MaxAudioPacketBytes = 4096;

    /// <summary>An all-zero adaptive-trigger block: the "no effect" payload sent on
    /// teardown and mode switch so a latched effect does not survive.</summary>
    private static readonly byte[] ClearEffect = new byte[11];

    private readonly Func<IMessage, Task> _send;
    private readonly Action _onAudioSendStart;

    // Capabilities of the emulated persona, refreshed on connect and on every
    // mode switch (the only two moments the wire format changes).
    private bool _hasLed;
    private bool _sendsTriggerRumble;

    // Last-known feedback state, mirrored to the phone.
    private byte _largeMotor;
    private byte _smallMotor;
    private volatile int _ledColor;
    private volatile int _playerLed;
    private byte[]? _leftTriggerEffect;
    private byte[]? _rightTriggerEffect;
    private byte _leftTriggerRumble;
    private byte _rightTriggerRumble;
    private volatile bool _testToneEnabled;

    // Audio stream state, latched on the first window.
    private uint _audioFrameIndex;
    private bool _audioFormatReady;
    private uint _audioSampleRate;
    private byte _audioChannels;
    private byte _audioBitsPerSample;

    public PhoneDownlink(Func<IMessage, Task> send, Action onAudioSendStart)
    {
        _send = send;
        _onAudioSendStart = onAudioSendStart;
    }

    /// <summary>Refreshes the emulated persona's feedback capabilities. Called once
    /// on connect and again on every mode switch, before the new state is published.</summary>
    public void Configure(bool hasLed, bool sendsTriggerRumble)
    {
        _hasLed = hasLed;
        _sendsTriggerRumble = sendsTriggerRumble;
    }

    // ----- vibration -------------------------------------------------------

    public void SetVibration(byte largeMotor, byte smallMotor)
    {
        _largeMotor = largeMotor;
        _smallMotor = smallMotor;
    }

    public void SetLargeMotor(byte value) => _largeMotor = value;
    public void SetSmallMotor(byte value) => _smallMotor = value;

    /// <summary>Sends the phone-mirrored vibration plus (when the persona has one)
    /// the LED state and, for effect personas, the last trigger blocks. This is the
    /// periodic keep-alive as well as the per-input frame.</summary>
    public void PublishCompactFrame()
    {
        _ = _send(new ServerToClient
        {
            CompactFrame = new global::GamepadEmu.Protocol.CompactFrame
            {
                Vibration = new Vibration
                {
                    LargeMotor = _largeMotor,
                    SmallMotor = _smallMotor,
                },
                TriggerEffects = BuildCompactTriggerEffects(),
                LedState = new LedState
                {
                    Color = (uint)_ledColor,
                    PlayerLed = (uint)_playerLed,
                },
            }
        });
    }

    /// <summary>Builds the compact frame's TriggerEffects envelope. DualSense modes
    /// carry the adaptive-effect byte blocks; Xbox One carries the (always present,
    /// even 0) trigger-rumble amplitudes so the phone can stop a latched rumble.</summary>
    private TriggerEffects? BuildCompactTriggerEffects()
    {
        if (_leftTriggerEffect == null && _rightTriggerEffect == null && !_sendsTriggerRumble) return null;

        var effects = new TriggerEffects
        {
            LeftTriggerEffect = ByteString.CopyFrom(_leftTriggerEffect ?? Array.Empty<byte>()),
            RightTriggerEffect = ByteString.CopyFrom(_rightTriggerEffect ?? Array.Empty<byte>()),
        };
        if (_sendsTriggerRumble)
        {
            // Assigning marks the optional fields present, so the phone can tell an
            // Xbox source (including an explicit 0) from "no trigger-rumble source".
            effects.LeftTriggerRumble = _leftTriggerRumble;
            effects.RightTriggerRumble = _rightTriggerRumble;
        }
        return effects;
    }

    // ----- adaptive triggers (DualSense) -----------------------------------

    public void SetLeftTriggerEffect(byte[]? effect) => _leftTriggerEffect = effect;
    public void SetRightTriggerEffect(byte[]? effect) => _rightTriggerEffect = effect;

    /// <summary>Forwards the merged adaptive-trigger blocks. Always carries both
    /// sides (empty when a side has no effect), so the phone never keeps a stale half.</summary>
    public void PublishTriggerEffects()
    {
        _ = _send(new ServerToClient
        {
            TriggerEffects = new TriggerEffects
            {
                LeftTriggerEffect = ByteString.CopyFrom(_leftTriggerEffect ?? Array.Empty<byte>()),
                RightTriggerEffect = ByteString.CopyFrom(_rightTriggerEffect ?? Array.Empty<byte>()),
            }
        });
    }

    /// <summary>Sends the all-zero blocks that clear a latched adaptive-trigger
    /// effect on teardown / mode switch.</summary>
    public void ClearTriggerEffects()
    {
        _ = _send(new ServerToClient
        {
            TriggerEffects = new TriggerEffects
            {
                LeftTriggerEffect = ByteString.CopyFrom(ClearEffect),
                RightTriggerEffect = ByteString.CopyFrom(ClearEffect),
            }
        });
    }

    // ----- impulse-trigger rumble (Xbox GIP) -------------------------------

    /// <summary>Stores and forwards the Xbox impulse-trigger amplitudes (0-255).
    /// Redundant values are suppressed, so the phone is only told about changes.
    /// The stored copy is also what the compact frame's trigger-rumble lanes echo.</summary>
    public void SetTriggerRumble(byte left, byte right)
    {
        if (left == _leftTriggerRumble && right == _rightTriggerRumble) return;

        _leftTriggerRumble = left;
        _rightTriggerRumble = right;
        _ = _send(new ServerToClient
        {
            TriggerEffects = new TriggerEffects
            {
                LeftTriggerRumble = left,
                RightTriggerRumble = right,
            }
        });
    }

    // ----- LED / test tone -------------------------------------------------

    /// <summary>Records a decoded LED colour / player indicator. Returns true when
    /// either changed, so the caller only publishes on a real transition.</summary>
    public bool UpdateLed(int? color, int? playerLed)
    {
        bool changed = false;
        if (color.HasValue && color.Value != _ledColor)
        {
            _ledColor = color.Value;
            changed = true;
        }
        if (playerLed.HasValue && playerLed.Value != _playerLed)
        {
            _playerLed = playerLed.Value;
            changed = true;
        }
        return changed;
    }

    /// <summary>Forwards the LED state. Personas without an LED (Xbox) always
    /// report #000000 / no player indicator.</summary>
    public void PublishLedState()
    {
        int color = _hasLed ? _ledColor : 0;
        int player = _hasLed ? _playerLed : 0;
        _ = _send(new ServerToClient
        {
            LedState = new LedState
            {
                Color = (uint)color,
                PlayerLed = (uint)player,
            }
        });
    }

    public bool TestToneEnabled => _testToneEnabled;

    /// <summary>Records and forwards the DualSense 1 kHz test-tone on/off state. The
    /// phone synthesises the tone locally; no test-tone PCM is streamed.</summary>
    public void SetTestTone(bool enabled)
    {
        _testToneEnabled = enabled;
        PublishTestToneState();
    }

    public void PublishTestToneState()
    {
        _ = _send(new ServerToClient
        {
            TestTone = new TestTone
            {
                Enabled = _testToneEnabled,
            }
        });
    }

    // ----- HD haptics (Switch) ---------------------------------------------

    /// <summary>Forwards Switch HD-rumble band parameters to the phone, which
    /// synthesises the LRA waveform locally. The band state itself is owned by the
    /// persona (it needs it for its change suppression and keep-alive pump).</summary>
    public void PublishHdRumble(in HdRumbleBands bands)
    {
        _ = _send(new ServerToClient
        {
            HdRumble = new HdRumble
            {
                LeftHighFreqHz = bands.LeftHighFreqHz, LeftHighAmp = bands.LeftHighAmp,
                LeftLowFreqHz = bands.LeftLowFreqHz, LeftLowAmp = bands.LeftLowAmp,
                RightHighFreqHz = bands.RightHighFreqHz, RightHighAmp = bands.RightHighAmp,
                RightLowFreqHz = bands.RightLowFreqHz, RightLowAmp = bands.RightLowAmp,
            }
        });
    }

    // ----- audio -----------------------------------------------------------

    /// <summary>Ingests one PCM window from a persona's audio endpoint: latches the
    /// stream format on the first window, resamples to the phone's rate and 4-channel
    /// layout, drops all-silent DualSense windows, and queues frame-aligned chunks.</summary>
    public void PushPcm(
        PcmSourceLayout layout, int sourceChannels, int sourceSampleRate,
        int bitsPerSample, ReadOnlySpan<byte> pcm)
    {
        try
        {
            if (pcm.IsEmpty) return;

            int expectedChannels = PcmResampler.SourceChannels(layout);
            if (layout == PcmSourceLayout.Ds4Stereo)
            {
                if (sourceChannels != expectedChannels) return;
            }
            else if (sourceChannels < expectedChannels)
            {
                return;
            }

            if (!_audioFormatReady)
            {
                _audioSampleRate = (uint)TargetSampleRate;
                _audioChannels = (byte)PcmResampler.OutputChannels;
                _audioBitsPerSample = (byte)bitsPerSample;
                _audioFormatReady = true;
                string tag = layout == PcmSourceLayout.Ds4Stereo ? "Ds4Audio" : "DualSenseAudio";
                Debug.WriteLine(
                    $"[{tag}] stream opened: {sourceSampleRate}Hz {sourceChannels}ch {bitsPerSample}bit -> {PcmResampler.OutputChannels}ch @ {TargetSampleRate}Hz");
            }

            var dst = PcmResampler.Resample(pcm, sourceSampleRate, TargetSampleRate, layout);

            // Skip silent windows: the phone's native sender keeps the DualSense
            // endpoint fed with locally-generated silence, so forwarding silence
            // only floods the link and blocks the phone's receive thread.
            if (layout == PcmSourceLayout.DualsenseFourChannel && PcmResampler.IsSilent(dst)) return;

            Drain(dst);
        }
        catch { }
    }

    private void Drain(byte[] pcm)
    {
        if (!_audioFormatReady) return;

        int bytesPerFrame = _audioChannels * (_audioBitsPerSample / 8);
        if (bytesPerFrame <= 0) return;

        int completeBytes = (pcm.Length / bytesPerFrame) * bytesPerFrame;
        if (completeBytes == 0) return;

        _onAudioSendStart?.Invoke();

        int chunkBytes = (MaxAudioPacketBytes / bytesPerFrame) * bytesPerFrame;
        for (int sent = 0; sent < completeBytes; sent += chunkBytes)
        {
            int n = Math.Min(chunkBytes, completeBytes - sent);
            var bytes = new byte[n];
            Buffer.BlockCopy(pcm, sent, bytes, 0, n);
            _ = SendAudioFrame(bytes);
        }
    }

    private Task SendAudioFrame(byte[] pcm)
    {
        try
        {
            var frame = new ServerToClient
            {
                AudioFrame = new AudioFrame
                {
                    SampleRateHz = _audioSampleRate,
                    Channels = _audioChannels,
                    BitsPerSample = _audioBitsPerSample,
                    FrameIndex = _audioFrameIndex++,
                    SampleCount = (uint)(pcm.Length / (_audioChannels * (_audioBitsPerSample / 8))),
                    Pcm = ByteString.CopyFrom(pcm),
                }
            };
            return _send(frame);
        }
        catch
        {
            return Task.CompletedTask;
        }
    }

    // ----- mode-switch / teardown resets -----------------------------------

    public void ResetVibration()
    {
        _largeMotor = 0;
        _smallMotor = 0;
    }

    public void ResetTriggerEffects()
    {
        _leftTriggerEffect = null;
        _rightTriggerEffect = null;
    }

    public void ResetTriggerRumble()
    {
        _leftTriggerRumble = 0;
        _rightTriggerRumble = 0;
    }

    public void ResetLed()
    {
        _ledColor = 0;
        _playerLed = 0;
    }

    /// <summary>Drops the pending audio format/position and the test-tone flag on a
    /// mode switch or teardown.</summary>
    public void ResetAudio()
    {
        _audioFrameIndex = 0;
        _testToneEnabled = false;
        _audioFormatReady = false;
    }
}
