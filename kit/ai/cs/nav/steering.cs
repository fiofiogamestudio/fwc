using System.Numerics;

namespace Fw.Rt.AI.Nav;

public readonly record struct SteerResult(Vector2 Linear)
{
    public static SteerResult Zero => new(Vector2.Zero);
}

public static class Steering
{
    public static SteerResult Seek(Vector2 position, Vector2 target, float maxSpeed)
    {
        ValidateSpeed(maxSpeed);
        var offset = target - position;
        return offset.LengthSquared() == 0.0f
            ? SteerResult.Zero
            : new SteerResult(Vector2.Normalize(offset) * maxSpeed);
    }

    public static SteerResult Arrive(
        Vector2 position,
        Vector2 target,
        float maxSpeed,
        float slowRadius,
        float stopRadius = 0.0f
    )
    {
        ValidateSpeed(maxSpeed);
        if (!float.IsFinite(slowRadius) || slowRadius <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(slowRadius));
        }
        if (!float.IsFinite(stopRadius) || stopRadius < 0.0f || stopRadius > slowRadius)
        {
            throw new ArgumentOutOfRangeException(nameof(stopRadius));
        }
        var offset = target - position;
        var distance = offset.Length();
        if (distance <= stopRadius || distance == 0.0f)
        {
            return SteerResult.Zero;
        }
        var speed = maxSpeed * Math.Clamp((distance - stopRadius) / (slowRadius - stopRadius), 0.0f, 1.0f);
        return new SteerResult(offset / distance * speed);
    }

    public static SteerResult Separate(
        Vector2 position,
        IEnumerable<Vector2> neighbors,
        float radius,
        float maxSpeed
    )
    {
        ArgumentNullException.ThrowIfNull(neighbors);
        ValidateSpeed(maxSpeed);
        if (!float.IsFinite(radius) || radius <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(radius));
        }
        var force = Vector2.Zero;
        foreach (var neighbor in neighbors)
        {
            var offset = position - neighbor;
            var distanceSquared = offset.LengthSquared();
            if (distanceSquared <= 0.0f || distanceSquared >= radius * radius)
            {
                continue;
            }
            force += Vector2.Normalize(offset) * (1.0f - MathF.Sqrt(distanceSquared) / radius);
        }
        if (force.LengthSquared() == 0.0f)
        {
            return SteerResult.Zero;
        }
        var length = force.Length();
        return new SteerResult(force / length * MathF.Min(length, maxSpeed));
    }

    public static SteerResult Blend(float maxSpeed, params (SteerResult Result, float Weight)[] inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ValidateSpeed(maxSpeed);
        var linear = Vector2.Zero;
        foreach (var input in inputs)
        {
            if (!float.IsFinite(input.Weight) || input.Weight < 0.0f)
            {
                throw new ArgumentOutOfRangeException(nameof(inputs));
            }
            linear += input.Result.Linear * input.Weight;
        }
        var length = linear.Length();
        return length > maxSpeed && length > 0.0f
            ? new SteerResult(linear / length * maxSpeed)
            : new SteerResult(linear);
    }

    private static void ValidateSpeed(float maxSpeed)
    {
        if (!float.IsFinite(maxSpeed) || maxSpeed < 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSpeed));
        }
    }
}
