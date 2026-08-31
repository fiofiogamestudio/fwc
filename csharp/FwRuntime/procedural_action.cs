using System.Numerics;

namespace Fw.Rt.Animation;

public enum ProceduralEasing
{
    Linear,
    Smooth,
    In,
    Out,
    EaseInCubic,
    EaseInQuint,
    EaseOutCubic,
    EaseOutBack,
}

public readonly record struct ProceduralPoseKey(
    float Tick,
    Vector3 Position,
    Vector3 RotationDegrees,
    Vector3 Scale,
    ProceduralEasing Easing = ProceduralEasing.Smooth
);

public readonly record struct ProceduralPose(
    Vector3 Position,
    Quaternion Rotation,
    Vector3 Scale
)
{
    public static ProceduralPose Identity { get; } = new(
        Vector3.Zero,
        Quaternion.Identity,
        Vector3.One
    );
}

public static class ProceduralActionSampler
{
    public const float DefaultMaxSampleAngleDegrees = 10.0f;
    public const float DefaultMaxSampleDistance = 0.12f;
    public const int DefaultMaxSamplesPerTick = 4;

    public static ProceduralPose Sample(
        IReadOnlyList<ProceduralPoseKey> keys,
        float timelineTick
    )
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            return ProceduralPose.Identity;
        }

        float tick = Math.Max(timelineTick, 0.0f);
        ProceduralPoseKey previous = keys[0];
        if (tick <= previous.Tick)
        {
            return FromKey(previous);
        }

        for (int index = 1; index < keys.Count; index += 1)
        {
            ProceduralPoseKey next = keys[index];
            if (tick > next.Tick)
            {
                previous = next;
                continue;
            }

            float span = Math.Max(next.Tick - previous.Tick, 0.000001f);
            float ratio = Ease((tick - previous.Tick) / span, next.Easing);
            return new ProceduralPose(
                Vector3.Lerp(previous.Position, next.Position, ratio),
                Quaternion.Normalize(Quaternion.Slerp(
                    FromEulerYxz(previous.RotationDegrees),
                    FromEulerYxz(next.RotationDegrees),
                    ratio
                )),
                Vector3.Lerp(previous.Scale, next.Scale, ratio)
            );
        }

        return FromKey(previous);
    }

    public static int RequiredSubsamples(
        ProceduralPose from,
        ProceduralPose to,
        float maxAngleDegrees = DefaultMaxSampleAngleDegrees,
        float maxDistance = DefaultMaxSampleDistance,
        int maxSamples = DefaultMaxSamplesPerTick
    )
    {
        if (maxAngleDegrees <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAngleDegrees));
        }
        if (maxDistance <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDistance));
        }
        if (maxSamples < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSamples));
        }

        float dot = Math.Clamp(Math.Abs(Quaternion.Dot(
            Quaternion.Normalize(from.Rotation),
            Quaternion.Normalize(to.Rotation)
        )), 0.0f, 1.0f);
        float angleDelta = 2.0f * MathF.Acos(dot) * (180.0f / MathF.PI);
        float distanceDelta = Vector3.Distance(from.Position, to.Position);
        int angleSamples = (int)Math.Ceiling(angleDelta / maxAngleDegrees);
        int distanceSamples = (int)Math.Ceiling(distanceDelta / maxDistance);
        return Math.Clamp(Math.Max(angleSamples, distanceSamples), 1, maxSamples);
    }

    public static float Ease(float ratio, ProceduralEasing easing)
    {
        float value = Math.Clamp(ratio, 0.0f, 1.0f);
        return easing switch
        {
            ProceduralEasing.Linear => value,
            ProceduralEasing.In => value * value,
            ProceduralEasing.Out => 1.0f - Square(1.0f - value),
            ProceduralEasing.EaseInCubic => value * value * value,
            ProceduralEasing.EaseInQuint => Square(value) * Square(value) * value,
            ProceduralEasing.EaseOutCubic => 1.0f - Cube(1.0f - value),
            ProceduralEasing.EaseOutBack => EaseOutBack(value),
            _ => value * value * (3.0f - 2.0f * value),
        };
    }

    public static Vector3 RotateLocalYxz(Vector3 value, Vector3 rotationDegrees)
    {
        return RotateLocal(value, FromEulerYxz(rotationDegrees));
    }

    public static Vector3 RotateLocal(Vector3 value, Quaternion rotation)
    {
        Quaternion normalized = Quaternion.Normalize(rotation);
        Vector3 axis = new(normalized.X, normalized.Y, normalized.Z);
        Vector3 twiceCross = 2.0f * Vector3.Cross(axis, value);
        return value
            + normalized.W * twiceCross
            + Vector3.Cross(axis, twiceCross);
    }

    public static Quaternion FromEulerYxz(Vector3 rotationDegrees)
    {
        Vector3 radians = rotationDegrees * (MathF.PI / 180.0f);
        Quaternion yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, radians.Y);
        Quaternion pitch = Quaternion.CreateFromAxisAngle(Vector3.UnitX, radians.X);
        Quaternion roll = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, radians.Z);
        return Quaternion.Normalize(yaw * pitch * roll);
    }

    private static ProceduralPose FromKey(ProceduralPoseKey key)
    {
        return new ProceduralPose(
            key.Position,
            FromEulerYxz(key.RotationDegrees),
            key.Scale
        );
    }

    private static float Square(float value) => value * value;

    private static float Cube(float value) => value * value * value;

    private static float EaseOutBack(float value)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1.0f;
        float shifted = value - 1.0f;
        return 1.0f + c3 * Cube(shifted) + c1 * Square(shifted);
    }
}
