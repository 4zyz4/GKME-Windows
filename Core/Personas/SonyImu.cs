namespace GKME.Core.Personas;

/// <summary>Motion-sensor encoding shared by the Sony personas. The phone's IMU
/// reports can exceed the pad's wire range (DS4 full scale is ±2000 deg/s and
/// ±4 g), so values pass through a soft knee: linear up to the knee, then a
/// tapered slope that never reaches the full-scale value games treat as a
/// sensor fault.</summary>
internal static class SonyImu
{
    private const float GyroScale = 940f;
    private const float GyroKnee = 30f;
    private const float GyroMaxInput = 40f;
    private const float GyroMaxOutput = 32760f;

    private const float AccelScale = 835f;
    private const float AccelKnee = 35f;
    private const float AccelMaxInput = 160f;
    private const float AccelMaxOutput = 32760f;

    public static short EncodeGyro(float value)
        => (short)SoftLimit(value, GyroScale, GyroKnee, GyroMaxInput, GyroMaxOutput);

    public static short EncodeAccel(float value)
        => (short)SoftLimit(value, AccelScale, AccelKnee, AccelMaxInput, AccelMaxOutput);

    private static float SoftLimit(float input, float scale, float knee, float maxInput, float maxOutput)
    {
        float abs = MathF.Abs(input);
        if (abs <= knee)
            return input * scale;

        float sign = input < 0 ? -1f : 1f;
        if (abs >= maxInput)
            return sign * maxOutput;

        float atKnee = knee * scale;
        float slope = (maxOutput - atKnee) / (maxInput - knee);
        return sign * (atKnee + (abs - knee) * slope);
    }
}
