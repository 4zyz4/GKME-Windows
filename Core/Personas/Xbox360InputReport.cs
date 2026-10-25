using GamepadEmu.Protocol;

namespace GKME.Core.Personas;

/// <summary>
/// Packs one frame into the 20-byte Xbox 360 wired input report. The button word
/// uses the native XInput layout (A/B/X/Y in the high byte, LB/RB/Guide and the
/// menu/d-pad buttons in the low byte) so the host's XInput stack decodes it
/// correctly without translation. Pure layout logic.
/// </summary>
internal static class Xbox360InputReport
{
    public const int Size = 20;

    public static void Build(GamepadInput input, byte[] buf)
    {
        Array.Clear(buf, 0, buf.Length);

        ushort xb = 0;
        if ((input.Buttons & (1u << 0)) != 0) xb |= 0x1000; // A
        if ((input.Buttons & (1u << 1)) != 0) xb |= 0x2000; // B
        if ((input.Buttons & (1u << 2)) != 0) xb |= 0x4000; // X
        if ((input.Buttons & (1u << 3)) != 0) xb |= 0x8000; // Y
        if ((input.Buttons & (1u << 4)) != 0) xb |= 0x0100; // LB
        if ((input.Buttons & (1u << 5)) != 0) xb |= 0x0200; // RB
        if ((input.Buttons & (1u << 8)) != 0) xb |= 0x0020; // Back
        if ((input.Buttons & (1u << 9)) != 0) xb |= 0x0010; // Start
        if ((input.Buttons & (1u << 10)) != 0) xb |= 0x0040; // LS
        if ((input.Buttons & (1u << 11)) != 0) xb |= 0x0080; // RS
        if ((input.Buttons & (1u << 16)) != 0) xb |= 0x0400; // Guide
        if ((input.Dpad & 1) != 0) xb |= 0x0001; // Up
        if ((input.Dpad & 2) != 0) xb |= 0x0002; // Down
        if ((input.Dpad & 4) != 0) xb |= 0x0004; // Left
        if ((input.Dpad & 8) != 0) xb |= 0x0008; // Right

        var lt = input.LeftTrigger > 0 ? input.LeftTrigger : ((input.Buttons & (1u << 6)) != 0 ? 255u : 0u);
        var rt = input.RightTrigger > 0 ? input.RightTrigger : ((input.Buttons & (1u << 7)) != 0 ? 255u : 0u);

        buf[0] = 0x00; // report id
        buf[1] = 0x14; // payload size
        buf[2] = (byte)(xb & 0xFF);
        buf[3] = (byte)(xb >> 8);
        buf[4] = (byte)Math.Clamp(lt, 0u, 255u);
        buf[5] = (byte)Math.Clamp(rt, 0u, 255u);

        short lx = (short)input.LeftStickX;
        short ly = (short)Math.Clamp(-(long)input.LeftStickY, short.MinValue, short.MaxValue);
        short rx = (short)input.RightStickX;
        short ry = (short)Math.Clamp(-(long)input.RightStickY, short.MinValue, short.MaxValue);
        buf[6] = (byte)(lx & 0xFF); buf[7] = (byte)((lx >> 8) & 0xFF);
        buf[8] = (byte)(ly & 0xFF); buf[9] = (byte)((ly >> 8) & 0xFF);
        buf[10] = (byte)(rx & 0xFF); buf[11] = (byte)((rx >> 8) & 0xFF);
        buf[12] = (byte)(ry & 0xFF); buf[13] = (byte)((ry >> 8) & 0xFF);
        // 14..19 reserved (zero)
    }
}
