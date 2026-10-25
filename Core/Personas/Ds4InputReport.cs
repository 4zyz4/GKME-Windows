using GamepadEmu.Protocol;

namespace GKME.Core.Personas;

/// <summary>
/// Packs one frame into the 63-byte DS4 input report (report 0x01), exactly as
/// the USB/IP backend expects it: sticks, button word, triggers, 16-bit
/// timestamp, motion (soft-limited) and six touch records.
///
/// Pure layout logic — no session, no device, no downlink — so the report can be
/// diffed against a captured DS4 packet without a phone attached.
/// </summary>
internal static class Ds4InputReport
{
    public const int Size = 63;

    public static void Build(GamepadInput input, TouchTracker touch, long timestampMs, byte[] buf)
    {
        Array.Clear(buf, 0, buf.Length);

        buf[0] = (byte)((input.LeftStickX + 32768) / 256);
        buf[1] = (byte)((input.LeftStickY + 32768) / 256);
        buf[2] = (byte)((input.RightStickX + 32768) / 256);
        buf[3] = (byte)((input.RightStickY + 32768) / 256);

        ushort w = (input.Dpad & 0x0F) switch
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
        if ((input.Buttons & (1 << 0)) != 0) w |= 1 << 5;
        if ((input.Buttons & (1 << 1)) != 0) w |= 1 << 6;
        if ((input.Buttons & (1 << 2)) != 0) w |= 1 << 4;
        if ((input.Buttons & (1 << 3)) != 0) w |= 1 << 7;
        if ((input.Buttons & (1 << 4)) != 0) w |= 1 << 8;
        if ((input.Buttons & (1 << 5)) != 0) w |= 1 << 9;
        if ((input.Buttons & (1 << 6)) != 0) w |= 1 << 10;
        if ((input.Buttons & (1 << 7)) != 0) w |= 1 << 11;
        if ((input.Buttons & (1 << 8)) != 0) w |= 1 << 12;
        if ((input.Buttons & (1 << 9)) != 0) w |= 1 << 13;
        if ((input.Buttons & (1 << 10)) != 0) w |= 1 << 14;
        if ((input.Buttons & (1 << 11)) != 0) w |= 1 << 15;
        buf[4] = (byte)w;
        buf[5] = (byte)(w >> 8);

        byte special = 0;
        if ((input.Buttons & (1 << 16)) != 0) special |= 1;
        if ((input.Buttons & (1 << 17)) != 0) special |= 2;
        buf[6] = special;

        var lt = input.LeftTrigger > 0 ? input.LeftTrigger : ((input.Buttons & (1 << 6)) != 0 ? 255u : 0u);
        var rt = input.RightTrigger > 0 ? input.RightTrigger : ((input.Buttons & (1 << 7)) != 0 ? 255u : 0u);
        buf[7] = (byte)Math.Clamp(lt, 0, 255);
        buf[8] = (byte)Math.Clamp(rt, 0, 255);

        WriteInt16LE(buf, 9, (short)(timestampMs & 0xFFFF));

        buf[11] = (byte)Math.Clamp(input.BatteryLevel / 10u, 0u, 10u);

        WriteInt16LE(buf, 12, SonyImu.EncodeGyro(input.GyroX));
        WriteInt16LE(buf, 14, SonyImu.EncodeGyro(input.GyroY));
        WriteInt16LE(buf, 16, SonyImu.EncodeGyro(input.GyroZ));

        WriteInt16LE(buf, 18, SonyImu.EncodeAccel(input.AccelX));
        WriteInt16LE(buf, 20, SonyImu.EncodeAccel(input.AccelY));
        WriteInt16LE(buf, 22, SonyImu.EncodeAccel(input.AccelZ));

        var powerState = (byte)Math.Clamp(input.BatteryLevel / 10u, 0u, 10u);
        if (input.IsCharging) powerState |= 0x10;
        buf[29] = powerState;

        var t0 = input.Touches.Count > 0 ? input.Touches[0] : null;
        var t1 = input.Touches.Count > 1 ? input.Touches[1] : null;

        touch.BeginFrame(input);

        buf[32] = 1;
        buf[33] = touch.CurrentCounter;
        WriteTouchData(buf, 34,
            (int)(t0?.X ?? 0), (int)(t0?.Y ?? 0),
            t0?.Active ?? false, touch.Id(0));
        WriteTouchData(buf, 38,
            (int)(t1?.X ?? 0), (int)(t1?.Y ?? 0),
            t1?.Active ?? false, touch.Id(1));

        WriteTouchData(buf, 42, 0, 0, false, touch.Peek(1));
        WriteTouchData(buf, 46, 0, 0, false, touch.Peek(2));
        WriteTouchData(buf, 50, 0, 0, false, touch.Peek(3));
        WriteTouchData(buf, 54, 0, 0, false, touch.Peek(4));
        WriteTouchData(buf, 58, 0, 0, false, touch.Peek(5));
    }

    private static void WriteTouchData(byte[] buf, int offset, int x, int y, bool active, byte id)
    {
        buf[offset] = active ? id : (byte)(0x80 | id);
        int cx = Math.Clamp(x, 0, 1919);
        int cy = Math.Clamp(y, 0, 1079);
        buf[offset + 1] = (byte)(cx & 0xFF);
        buf[offset + 2] = (byte)(((cx >> 8) & 0x0F) | ((cy & 0x0F) << 4));
        buf[offset + 3] = (byte)((cy >> 4) & 0xFF);
    }

    private static void WriteInt16LE(byte[] buf, int offset, short value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
    }
}
