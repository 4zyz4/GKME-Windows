using GamepadEmu.Protocol;

namespace GKME.Core.Personas;

/// <summary>
/// Packs one frame into the 63-byte DualSense input report: sticks, triggers,
/// touch sequence + contacts on the DS5 offsets (32-bit timestamp, two contacts,
/// no placeholder records), motion and battery. Pure layout logic.
/// </summary>
internal static class DualsenseInputReport
{
    public const int Size = 63;

    public static void Build(GamepadInput input, TouchTracker touch, byte sequence, long timestampMs, byte[] buf)
    {
        Array.Clear(buf, 0, buf.Length);

        buf[0] = (byte)((input.LeftStickX + 32768) / 256);
        buf[1] = (byte)((input.LeftStickY + 32768) / 256);
        buf[2] = (byte)((input.RightStickX + 32768) / 256);
        buf[3] = (byte)((input.RightStickY + 32768) / 256);

        var lt = input.LeftTrigger > 0 ? input.LeftTrigger : ((input.Buttons & (1 << 6)) != 0 ? 255u : 0u);
        var rt = input.RightTrigger > 0 ? input.RightTrigger : ((input.Buttons & (1 << 7)) != 0 ? 255u : 0u);
        buf[4] = (byte)Math.Clamp(lt, 0, 255);
        buf[5] = (byte)Math.Clamp(rt, 0, 255);

        buf[6] = sequence;
        buf[7] = (byte)(input.Dpad & 0x0F) switch
        {
            1 => 0,
            2 => 4,
            4 => 6,
            8 => 2,
            5 => 7,
            6 => 5,
            9 => 1,
            10 => 3,
            _ => 8,
        };
        if ((input.Buttons & (1 << 2)) != 0) buf[7] |= 1 << 4; // Square
        if ((input.Buttons & (1 << 0)) != 0) buf[7] |= 1 << 5; // Cross
        if ((input.Buttons & (1 << 1)) != 0) buf[7] |= 1 << 6; // Circle
        if ((input.Buttons & (1 << 3)) != 0) buf[7] |= 1 << 7; // Triangle

        if ((input.Buttons & (1 << 4)) != 0) buf[8] |= 1 << 0; // L1
        if ((input.Buttons & (1 << 5)) != 0) buf[8] |= 1 << 1; // R1
        if ((input.Buttons & (1 << 6)) != 0) buf[8] |= 1 << 2; // L2
        if ((input.Buttons & (1 << 7)) != 0) buf[8] |= 1 << 3; // R2
        if ((input.Buttons & (1 << 8)) != 0) buf[8] |= 1 << 4; // Share
        if ((input.Buttons & (1 << 9)) != 0) buf[8] |= 1 << 5; // Options
        if ((input.Buttons & (1 << 10)) != 0) buf[8] |= 1 << 6; // L3
        if ((input.Buttons & (1 << 11)) != 0) buf[8] |= 1 << 7; // R3

        if ((input.Buttons & (1 << 16)) != 0) buf[9] |= 1 << 0; // PS
        if ((input.Buttons & (1 << 17)) != 0) buf[9] |= 1 << 1; // Touchpad
        if ((input.Buttons & (1u << 18)) != 0) buf[9] |= 1 << 2; // Mic Mute

        WriteInt16LE(buf, 15, SonyImu.EncodeGyro(input.GyroX));
        WriteInt16LE(buf, 17, SonyImu.EncodeGyro(input.GyroY));
        WriteInt16LE(buf, 19, SonyImu.EncodeGyro(input.GyroZ));

        WriteInt16LE(buf, 21, SonyImu.EncodeAccel(input.AccelX));
        WriteInt16LE(buf, 23, SonyImu.EncodeAccel(input.AccelY));
        WriteInt16LE(buf, 25, SonyImu.EncodeAccel(input.AccelZ));

        buf[27] = (byte)(timestampMs & 0xFF);
        buf[28] = (byte)((timestampMs >> 8) & 0xFF);
        buf[29] = (byte)((timestampMs >> 16) & 0xFF);
        buf[30] = (byte)((timestampMs >> 24) & 0xFF);
        buf[31] = 0x80; // no-touch placeholder for byte 32 (reserved/temp in real DS5); prevents phantom id=0 touch

        var t0 = input.Touches.Count > 0 ? input.Touches[0] : null;
        var t1 = input.Touches.Count > 1 ? input.Touches[1] : null;

        touch.BeginFrame(input);

        int f0x = (int)(t0?.X ?? 0);
        int f0y = (int)(t0?.Y ?? 0);
        int f1x = (int)(t1?.X ?? 0);
        int f1y = (int)(t1?.Y ?? 0);
        bool f0Active = t0?.Active ?? false;
        bool f1Active = t1?.Active ?? false;

        buf[32] = f0Active
            ? (byte)(touch.Id(0) & 0x7F)
            : (byte)0x80;
        buf[33] = (byte)(Math.Clamp(f0x, 0, 1919) & 0xFF);
        buf[34] = (byte)(((Math.Clamp(f0x, 0, 1919) >> 8) & 0x0F) | ((Math.Clamp(f0y, 0, 1079) & 0x0F) << 4));
        buf[35] = (byte)(Math.Clamp(f0y, 0, 1079) >> 4);

        buf[36] = f1Active
            ? (byte)(touch.Id(1) & 0x7F)
            : (byte)0x80;
        buf[37] = (byte)(Math.Clamp(f1x, 0, 1919) & 0xFF);
        buf[38] = (byte)(((Math.Clamp(f1x, 0, 1919) >> 8) & 0x0F) | ((Math.Clamp(f1y, 0, 1079) & 0x0F) << 4));
        buf[39] = (byte)(Math.Clamp(f1y, 0, 1079) >> 4);
        buf[40] = touch.CurrentCounter;
        buf[41] = touch.PreviousCounter;

        buf[52] = input.BatteryLevel > 0 ? (byte)Math.Clamp(input.BatteryLevel / 10u, 0u, 9u) : (byte)0x09;
        if (input.IsCharging) buf[52] |= 0x10;
    }

    private static void WriteInt16LE(byte[] buf, int offset, short value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
    }
}
