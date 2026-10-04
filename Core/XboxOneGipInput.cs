using System;
using GamepadEmu.Protocol;

namespace GKME.Core;

/// <summary>
/// Packs a phone <see cref="GamepadInput"/> into the 14-byte payload of a
/// GIP <c>GIP_CMD_INPUT</c> (0x20) message. The USB/IP backend's GipResponder
/// wraps it with the GIP header and sequence number and streams it on the
/// data interface's interrupt IN endpoint.
///
/// <para>Layout matches xone's <c>gip_gamepad_pkt_input</c> (which in turn
/// matches xpad's byte offsets): <c>[0..1]</c> buttons (uint16 LE),
/// <c>[2..3]</c> left trigger (0-1023), <c>[4..5]</c> right trigger,
/// <c>[6..7]</c> left stick X, <c>[8..9]</c> left stick Y, <c>[10..11]</c>
/// right stick X, <c>[12..13]</c> right stick Y (all int16 LE).</para>
///
/// <para>Button bits: 2=Menu/Start, 3=View/Back, 4=A, 5=B, 6=X, 7=Y,
/// 8=Up, 9=Down, 10=Left, 11=Right, 12=LB, 13=RB, 14=LS, 15=RS. The real
/// GIP input frame has no Guide button: the guide key is a separate
/// <c>GIP_CMD_VIRTUAL_KEY</c> (0x07) report. As an internal convention the
/// packer sets an otherwise-unused bit (bit 0, the "sync" bit hosts ignore)
/// to carry the Guide state; <see cref="GuideMarker"/> is masked out by the
/// responder before the frame hits the wire and converted into a virtual-key
/// report.</para>
/// </summary>
internal static class XboxOneGipInput
{
    public const int PayloadSize = 14;

    /// <summary>Series payload: the 14-byte GIP gamepad body plus one trailing
    /// console-function-map byte whose bit 0 carries the Share key.</summary>
    public const int SeriesPayloadSize = PayloadSize + 1;

    /// <summary>Phone <c>buttons</c> bit for the Xbox Series Share key. The
    /// Android app encodes the touchpad press (the on-screen 触摸板按下 /
    /// double-tap and the physical pad's touchpad click) as
    /// <c>GamepadState.TOUCHPAD_CLICK</c> = bit 17, so that bit is what becomes
    /// the Series Share button on the wire.</summary>
    public const uint ShareButton = 1u << 17;

    /// <summary>Buttons bit 0: the internal Guide marker. Cleared by the
    /// responder before transmission and turned into a 0x07 report.</summary>
    public const ushort GuideMarker = 0x0001;

    /// <summary>Packs the Series payload: <see cref="BuildPayload"/>'s 14-byte
    /// body, then the console-function-map byte (bit 0 = Share). The USB/IP
    /// backend's GipResponder expands this into the 40-byte Series input report.</summary>
    public static void BuildSeriesPayload(GamepadInput input, Span<byte> dst)
    {
        if (dst.Length < SeriesPayloadSize) return;
        BuildPayload(input, dst);
        dst[PayloadSize] = (input.Buttons & ShareButton) != 0 ? (byte)0x01 : (byte)0x00;
    }

    public static void BuildPayload(GamepadInput input, Span<byte> dst)
    {
        dst.Clear();
        if (dst.Length < PayloadSize) return;

        uint b = input.Buttons;
        uint d = input.Dpad;

        ushort buttons = 0;
        if ((b & (1u << 9)) != 0) buttons |= 1 << 2;   // Start / Menu
        if ((b & (1u << 8)) != 0) buttons |= 1 << 3;   // Back / View
        if ((b & (1u << 0)) != 0) buttons |= 1 << 4;   // A
        if ((b & (1u << 1)) != 0) buttons |= 1 << 5;   // B
        if ((b & (1u << 2)) != 0) buttons |= 1 << 6;   // X
        if ((b & (1u << 3)) != 0) buttons |= 1 << 7;   // Y
        if ((d & 1) != 0) buttons |= 1 << 8;           // D-pad up
        if ((d & 2) != 0) buttons |= 1 << 9;           // D-pad down
        if ((d & 4) != 0) buttons |= 1 << 10;          // D-pad left
        if ((d & 8) != 0) buttons |= 1 << 11;          // D-pad right
        if ((b & (1u << 4)) != 0) buttons |= 1 << 12;  // LB
        if ((b & (1u << 5)) != 0) buttons |= 1 << 13;  // RB
        if ((b & (1u << 10)) != 0) buttons |= 1 << 14; // LS
        if ((b & (1u << 11)) != 0) buttons |= 1 << 15; // RS
        if ((b & (1u << 16)) != 0) buttons |= GuideMarker;
        buttons |= 0x0002; // "Keep Alive" (spec Table 57, buttons LSB bit 1)

        WriteU16(dst, 0, buttons);
        WriteU16(dst, 2, Trigger(input.LeftTrigger));
        WriteU16(dst, 4, Trigger(input.RightTrigger));
        WriteI16(dst, 6, (short)input.LeftStickX);
        WriteI16(dst, 8, (short)~input.LeftStickY);
        WriteI16(dst, 10, (short)input.RightStickX);
        WriteI16(dst, 12, (short)~input.RightStickY);
    }

    /// <summary>Phone triggers are 0-255; GIP triggers are 0-1023.</summary>
    private static ushort Trigger(uint v)
    {
        if (v == 0) return 0;
        long scaled = (long)v * 1023 / 255;
        return (ushort)(scaled > 1023 ? 1023 : scaled);
    }

    private static void WriteU16(Span<byte> dst, int off, ushort v)
    {
        dst[off] = (byte)(v & 0xFF);
        dst[off + 1] = (byte)(v >> 8);
    }

    private static void WriteI16(Span<byte> dst, int off, short v)
        => WriteU16(dst, off, (ushort)v);
}
