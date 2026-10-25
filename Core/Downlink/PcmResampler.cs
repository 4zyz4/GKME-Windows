namespace GKME.Core.Downlink;

/// <summary>Source layout of one PCM window a persona's audio endpoint hands to
/// the downlink.</summary>
internal enum PcmSourceLayout
{
    /// <summary>DS4 headset: 2 channels (left/right), folded to a mono mix on the
    /// phone's controller-audio lane.</summary>
    Ds4Stereo,

    /// <summary>DualSense: 4 channels (speaker L/R + haptic L/R), remapped onto the
    /// phone's controller-audio and voice-coil lanes.</summary>
    DualsenseFourChannel,
}

/// <summary>
/// One stateless linear-interpolation resampler shared by every persona's audio
/// path, plus the phone's fixed 4-channel output layout and the silence test.
/// Pure functions only: the caller owns all stream state, so the whole audio path
/// can be diffed offline against captured PCM.
/// </summary>
internal static class PcmResampler
{
    /// <summary>Channels the phone expects on every audio frame. Lane roles:
    /// ch0 silent, ch1 controller audio (mono), ch2 left voice coil, ch3 right
    /// voice coil.</summary>
    public const int OutputChannels = 4;

    /// <summary>16-bit samples are the only format any emulated endpoint produces,
    /// and the layout math below assumes it (as the pre-refactor code did).</summary>
    private const int BytesPerSample = 2;

    /// <summary>Channels a source layout delivers per frame.</summary>
    public static int SourceChannels(PcmSourceLayout layout) => layout switch
    {
        PcmSourceLayout.Ds4Stereo => 2,
        _ => 4,
    };

    /// <summary>Resamples one window from <paramref name="sourceSampleRate"/> to
    /// <paramref name="targetSampleRate"/> by linear interpolation and rewrites it
    /// into the phone's 4-channel layout. Returns the finished interleaved PCM.</summary>
    public static byte[] Resample(
        ReadOnlySpan<byte> src, int sourceSampleRate,
        int targetSampleRate, PcmSourceLayout layout)
    {
        int srcChannels = SourceChannels(layout);
        int srcBytesPerFrame = srcChannels * BytesPerSample;
        int srcNumSamples = src.Length / srcBytesPerFrame;

        double ratio = (double)targetSampleRate / sourceSampleRate;
        int dstNumSamples = (int)(srcNumSamples * ratio);
        int dstBytesPerFrame = OutputChannels * BytesPerSample;
        var dst = new byte[dstNumSamples * dstBytesPerFrame];

        Span<short> frame = stackalloc short[8];
        for (int s = 0; s < dstNumSamples; s++)
        {
            double srcPos = s / ratio;
            int srcIdx0 = (int)srcPos;
            int srcIdx1 = Math.Min(srcIdx0 + 1, srcNumSamples - 1);
            double frac = srcPos - srcIdx0;

            int srcOff0 = srcIdx0 * srcBytesPerFrame;
            int srcOff1 = srcIdx1 * srcBytesPerFrame;
            for (int c = 0; c < srcChannels; c++)
            {
                short a = BitConverter.ToInt16(src.Slice(srcOff0 + c * BytesPerSample, BytesPerSample));
                short b = BitConverter.ToInt16(src.Slice(srcOff1 + c * BytesPerSample, BytesPerSample));
                frame[c] = (short)(a + frac * (b - a));
            }

            int dstOff = s * dstBytesPerFrame;
            short mono = (short)((frame[0] + frame[1]) / 2);
            WriteSample(dst, dstOff, 0, 0);
            WriteSample(dst, dstOff, 1, mono);
            if (layout == PcmSourceLayout.Ds4Stereo)
            {
                WriteSample(dst, dstOff, 2, 0);
                WriteSample(dst, dstOff, 3, 0);
            }
            else
            {
                WriteSample(dst, dstOff, 2, frame[2]); // left voice coil
                WriteSample(dst, dstOff, 3, frame[3]); // right voice coil
            }
        }
        return dst;
    }

    /// <summary>True when every 16-bit sample is within a few LSB of zero. A game
    /// holding the DualSense audio stream open makes the endpoint deliver silent
    /// windows (often dithered, not exactly 0) continuously; the phone cannot tell
    /// them from real audio, so they are dropped at the source.</summary>
    public static bool IsSilent(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i + 1 < data.Length; i += 2)
        {
            int s = (short)(data[i] | (data[i + 1] << 8));
            if (s > 8 || s < -8) return false;
        }
        return true;
    }

    private static void WriteSample(byte[] dst, int frameOffset, int channel, short value)
    {
        int o = frameOffset + channel * BytesPerSample;
        dst[o] = (byte)(value & 0xFF);
        dst[o + 1] = (byte)((value >> 8) & 0xFF);
    }
}
