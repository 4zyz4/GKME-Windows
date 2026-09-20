using System;
using GamepadEmu.Protocol;

namespace GKME.Core;

/// <summary>
/// Packs a phone <see cref="GamepadInput"/> into the 48-byte Nintendo Switch
/// Pro report-0x30 body the USB/IP backend frames and streams.
///
/// <para>Layout (matches HIDMaestro's SwitchProPacker / driver.c
/// SwitchFillLatestState): <c>[0]</c> counter, <c>[1]</c> battery,
/// <c>[2..4]</c> buttons, <c>[5..10]</c> two 12-bit little-nibble sticks,
/// <c>[11]</c> vibrator, <c>[12..47]</c> three 12-byte IMU frames
/// (accel x/y/z then gyro x/y/z, int16 LE). Counter/battery/vibrator are
/// overlaid device-side; the phone only needs to fill buttons/sticks/IMU.</para>
///
/// <para>Buttons follow the GKME convention (same as the DS4/DualSense
/// packers): 0=A 1=B 2=X 3=Y 4=L 5=R 6=ZL 7=ZR 8=Minus 9=Plus 10=LStick
/// 11=RStick 16=Home 17=Capture. The wire bits are SDL
/// HandleFullControllerState's.</para>
///
/// <para>IMU: the phone sends SI units (gyro rad/s, accel m/s^2 — the same
/// values the DS4 packer scales by 940 / 835). SDL's Switch driver maps a
/// wire sample <c>(v0,v1,v2)</c> to the SDL-standard sensor frame as
/// <c>sdl0=-Y, sdl1=+Z, sdl2=-X</c>, so the inverse permutation
/// <c>wire = (-sdlZ, -sdlX, +sdlY)</c> is applied (SwitchProPacker's
/// conversion), with wire = deg/s * 13371/936 and g * 4096.</para>
/// </summary>
internal static class SwitchProInput
{
    public const int BodySize = 48;

    private const uint BtnA = 1u << 0;
    private const uint BtnB = 1u << 1;
    private const uint BtnX = 1u << 2;
    private const uint BtnY = 1u << 3;
    private const uint BtnL = 1u << 4;
    private const uint BtnR = 1u << 5;
    private const uint BtnZL = 1u << 6;
    private const uint BtnZR = 1u << 7;
    private const uint BtnMinus = 1u << 8;
    private const uint BtnPlus = 1u << 9;
    private const uint BtnLStick = 1u << 10;
    private const uint BtnRStick = 1u << 11;
    private const uint BtnHome = 1u << 16;
    private const uint BtnCapture = 1u << 17;

    // Wire scale factors: gyro wire = deg/s * (13371/936) = rad/s * 818.42,
    // accel wire = g * 4096 = (m/s^2) * 417.68.
    private const float RadPerSecToWire = 818.4167f;
    private const float MetersPerSec2ToWire = 417.6779f;
    private const float SwitchAccelWirePerG = 4096.0f;

    public static void BuildBody(GamepadInput input, Span<byte> body)
    {
        body.Clear();
        if (body.Length < BodySize) return;

        uint b = input.Buttons;
        byte b0 = 0, b1 = 0, b2 = 0;
        // SDL wires the face buttons by POSITION, not by Nintendo label:
        //   wire bit2 = bottom (South), bit3 = right (East),
        //   wire bit0 = left  (West),  bit1 = top   (North).
        // GKME's bits are the same positions (0=bottom/Cross, 1=right/Circle,
        // 2=left/Square, 3=top/Triangle), so map position-to-position.
        if ((b & BtnX) != 0) b0 |= 0x01; // left  -> wire bit0
        if ((b & BtnY) != 0) b0 |= 0x02; // top   -> wire bit1
        if ((b & BtnA) != 0) b0 |= 0x04; // bottom-> wire bit2
        if ((b & BtnB) != 0) b0 |= 0x08; // right -> wire bit3
        if ((b & BtnR) != 0) b0 |= 0x40;
        if ((b & BtnZR) != 0) b0 |= 0x80;

        if ((b & BtnMinus) != 0) b1 |= 0x01;
        if ((b & BtnPlus) != 0) b1 |= 0x02;
        if ((b & BtnRStick) != 0) b1 |= 0x04;
        if ((b & BtnLStick) != 0) b1 |= 0x08;
        if ((b & BtnHome) != 0) b1 |= 0x10;
        if ((b & BtnCapture) != 0) b1 |= 0x20;

        uint d = input.Dpad;
        if ((d & 0x2) != 0) b2 |= 0x01; // down
        if ((d & 0x1) != 0) b2 |= 0x02; // up
        if ((d & 0x8) != 0) b2 |= 0x04; // right
        if ((d & 0x4) != 0) b2 |= 0x08; // left
        if ((b & BtnL) != 0) b2 |= 0x40;
        if ((b & BtnZL) != 0) b2 |= 0x80;

        body[2] = b0;
        body[3] = b1;
        body[4] = b2;

        PackStick(body, 5, StickRaw(ToUnit(input.LeftStickX), invert: false),
                          StickRaw(ToUnit(input.LeftStickY), invert: true));
        PackStick(body, 8, StickRaw(ToUnit(input.RightStickX), invert: false),
                          StickRaw(ToUnit(input.RightStickY), invert: true));

        // One sensor sample per SubmitRawReport; SDL keys delivery on frame
        // content and its per-frame timestamps advance regardless.
        short ax = ToShort(-input.AccelZ * MetersPerSec2ToWire);
        short ay = ToShort(-input.AccelX * MetersPerSec2ToWire);
        short az = ToShort(input.AccelY * MetersPerSec2ToWire);
        short gx = ToShort(-input.GyroZ * RadPerSecToWire);
        short gy = ToShort(-input.GyroX * RadPerSecToWire);
        short gz = ToShort(input.GyroY * RadPerSecToWire);

        for (int frame = 0; frame < 3; frame++)
        {
            int o = 12 + frame * 12;
            WriteInt16(body, o + 0, ax);
            WriteInt16(body, o + 2, ay);
            WriteInt16(body, o + 4, az);
            WriteInt16(body, o + 6, gx);
            WriteInt16(body, o + 8, gy);
            WriteInt16(body, o + 10, gz);
        }
    }

    private static double ToUnit(int v) => (v + 32768.0) / 65536.0;

    private static ushort StickRaw(double v, bool invert)
    {
        double centered = (Math.Clamp(v, 0.0, 1.0) * 2.0) - 1.0;
        if (invert) centered = -centered;
        int raw = 0x800 + (int)(centered * 0x600);
        return (ushort)Math.Clamp(raw, 0, 0xFFF);
    }

    /// <summary>12-bit little-nibble pair packing (inverse of SDL's
    /// extraction: x = b0 | ((b1 & 0xF) &lt;&lt; 8); y = (b1 &gt;&gt; 4) | (b2 &lt;&lt; 4)).</summary>
    private static void PackStick(Span<byte> dst, int offset, ushort x, ushort y)
    {
        dst[offset + 0] = (byte)(x & 0xFF);
        dst[offset + 1] = (byte)(((x >> 8) & 0x0F) | ((y & 0x0F) << 4));
        dst[offset + 2] = (byte)(y >> 4);
    }

    private static short ToShort(float value)
        => (short)Math.Clamp((int)MathF.Round(value), short.MinValue, short.MaxValue);

    private static void WriteInt16(Span<byte> dst, int offset, short v)
    {
        dst[offset] = (byte)(v & 0xFF);
        dst[offset + 1] = (byte)((v >> 8) & 0xFF);
    }
}
