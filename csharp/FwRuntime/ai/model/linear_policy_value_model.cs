using System.Text.Json;

namespace Fw.Rt.AI.Model;

public interface IPolicyValueFeatureEncoder<TObservation, TAction>
    where TObservation : notnull
    where TAction : notnull
{
    int PolicyFeatureCount { get; }
    int ValueFeatureCount { get; }

    IReadOnlyList<double> EncodePolicy(
        TObservation observation,
        int actor,
        TAction action
    );

    IReadOnlyList<double> EncodeValue(
        TObservation observation,
        int observer,
        int player
    );
}

public sealed class DelegatePolicyValueFeatureEncoder<TObservation, TAction>
    : IPolicyValueFeatureEncoder<TObservation, TAction>
    where TObservation : notnull
    where TAction : notnull
{
    private readonly Func<TObservation, int, TAction, IReadOnlyList<double>> _encodePolicy;
    private readonly Func<TObservation, int, int, IReadOnlyList<double>> _encodeValue;

    public DelegatePolicyValueFeatureEncoder(
        int policyFeatureCount,
        int valueFeatureCount,
        Func<TObservation, int, TAction, IReadOnlyList<double>> encodePolicy,
        Func<TObservation, int, int, IReadOnlyList<double>> encodeValue
    )
    {
        if (policyFeatureCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policyFeatureCount));
        }
        if (valueFeatureCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(valueFeatureCount));
        }
        PolicyFeatureCount = policyFeatureCount;
        ValueFeatureCount = valueFeatureCount;
        _encodePolicy = encodePolicy ?? throw new ArgumentNullException(nameof(encodePolicy));
        _encodeValue = encodeValue ?? throw new ArgumentNullException(nameof(encodeValue));
    }

    public int PolicyFeatureCount { get; }
    public int ValueFeatureCount { get; }

    public IReadOnlyList<double> EncodePolicy(
        TObservation observation,
        int actor,
        TAction action
    ) => _encodePolicy(observation, actor, action);

    public IReadOnlyList<double> EncodeValue(
        TObservation observation,
        int observer,
        int player
    ) => _encodeValue(observation, observer, player);
}

public sealed class LinearPolicyValueCheckpoint
{
    public const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions CompactJson = new();
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    public LinearPolicyValueCheckpoint(
        int version,
        int playerCount,
        IEnumerable<double> policyWeights,
        IEnumerable<IEnumerable<double>> valueWeights
    )
    {
        if (version != CurrentVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "Unsupported checkpoint version.");
        }
        if (playerCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(playerCount));
        }

        double[] policySnapshot = SnapshotFinite(policyWeights, nameof(policyWeights));
        double[][] valueSnapshot = (valueWeights ?? throw new ArgumentNullException(nameof(valueWeights)))
            .Select((weights, index) => SnapshotFinite(weights, $"{nameof(valueWeights)}[{index}]"))
            .ToArray();
        if (policySnapshot.Length == 0 || valueSnapshot.Length != playerCount
            || valueSnapshot.Any(weights => weights.Length == 0)
            || valueSnapshot.Select(weights => weights.Length).Distinct().Count() != 1)
        {
            throw new ArgumentException("Checkpoint weight dimensions are invalid.");
        }

        Version = version;
        PlayerCount = playerCount;
        PolicyWeights = Array.AsReadOnly(policySnapshot);
        ValueWeights = Array.AsReadOnly<IReadOnlyList<double>>(
            valueSnapshot.Select(weights => Array.AsReadOnly(weights)).ToArray()
        );
    }

    public int Version { get; }
    public int PlayerCount { get; }
    public IReadOnlyList<double> PolicyWeights { get; }
    public IReadOnlyList<IReadOnlyList<double>> ValueWeights { get; }

    public string ToJson(bool writeIndented = false)
    {
        var payload = new CheckpointPayload
        {
            Version = Version,
            PlayerCount = PlayerCount,
            PolicyWeights = PolicyWeights.ToArray(),
            ValueWeights = ValueWeights.Select(weights => weights.ToArray()).ToArray(),
        };
        return JsonSerializer.Serialize(payload, writeIndented ? IndentedJson : CompactJson);
    }

    public static LinearPolicyValueCheckpoint FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        CheckpointPayload payload = JsonSerializer.Deserialize<CheckpointPayload>(json, CompactJson)
            ?? throw new InvalidOperationException("Linear policy/value checkpoint is empty.");
        return new LinearPolicyValueCheckpoint(
            payload.Version,
            payload.PlayerCount,
            payload.PolicyWeights ?? throw new InvalidOperationException(
                "Linear policy/value checkpoint has no policy weights."
            ),
            payload.ValueWeights ?? throw new InvalidOperationException(
                "Linear policy/value checkpoint has no value weights."
            )
        );
    }

    private sealed class CheckpointPayload
    {
        public int Version { get; init; }
        public int PlayerCount { get; init; }
        public double[]? PolicyWeights { get; init; }
        public double[][]? ValueWeights { get; init; }
    }

    private static double[] SnapshotFinite(IEnumerable<double> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values);
        double[] snapshot = values.ToArray();
        if (snapshot.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Weights must be finite.");
        }
        return snapshot;
    }
}

public sealed class LinearPolicyValueModel<TObservation, TAction>
    : IPolicyValueModel<TObservation, TAction>
    where TObservation : notnull
    where TAction : notnull
{
    private readonly IPolicyValueFeatureEncoder<TObservation, TAction> _encoder;
    private readonly double[] _policyWeights;
    private readonly double[][] _valueWeights;

    public LinearPolicyValueModel(
        IPolicyValueFeatureEncoder<TObservation, TAction> encoder,
        int playerCount
    )
    {
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        ValidateEncoder(_encoder);
        if (playerCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(playerCount));
        }
        PlayerCount = playerCount;
        _policyWeights = new double[_encoder.PolicyFeatureCount];
        _valueWeights = Enumerable.Range(0, playerCount)
            .Select(_ => new double[_encoder.ValueFeatureCount])
            .ToArray();
    }

    public LinearPolicyValueModel(
        IPolicyValueFeatureEncoder<TObservation, TAction> encoder,
        LinearPolicyValueCheckpoint checkpoint
    )
    {
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        ArgumentNullException.ThrowIfNull(checkpoint);
        ValidateEncoder(_encoder);
        if (checkpoint.PolicyWeights.Count != _encoder.PolicyFeatureCount
            || checkpoint.ValueWeights.Any(weights => weights.Count != _encoder.ValueFeatureCount))
        {
            throw new ArgumentException("Checkpoint dimensions do not match the feature encoder.", nameof(checkpoint));
        }

        PlayerCount = checkpoint.PlayerCount;
        _policyWeights = checkpoint.PolicyWeights.ToArray();
        _valueWeights = checkpoint.ValueWeights.Select(weights => weights.ToArray()).ToArray();
    }

    public int PlayerCount { get; }
    public int ParameterCount => _policyWeights.Length + _valueWeights.Sum(weights => weights.Length);
    internal int PolicyFeatureCount => _policyWeights.Length;
    internal int ValueFeatureCount => _valueWeights[0].Length;

    public PolicyValuePrediction<TAction> Predict(
        TObservation observation,
        int actor,
        IReadOnlyList<TAction> legalActions
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(legalActions);
        ValidateActor(actor);
        if (legalActions.Distinct().Count() != legalActions.Count)
        {
            throw new ArgumentException("Legal actions must be unique.", nameof(legalActions));
        }

        double[] logits = legalActions
            .Select(action => Dot(_policyWeights, PolicyFeatures(observation, actor, action)))
            .ToArray();
        double[] probabilities = Softmax(logits);
        double[] values = Enumerable.Range(0, PlayerCount)
            .Select(player => Math.Tanh(Dot(
                _valueWeights[player],
                ValueFeatures(observation, actor, player)
            )))
            .ToArray();
        return new PolicyValuePrediction<TAction>(
            legalActions.Select((action, index) => new ActionPrior<TAction>(action, probabilities[index])),
            values
        );
    }

    public LinearPolicyValueCheckpoint ExportCheckpoint()
    {
        return new LinearPolicyValueCheckpoint(
            LinearPolicyValueCheckpoint.CurrentVersion,
            PlayerCount,
            _policyWeights,
            _valueWeights
        );
    }

    internal double[] PolicyFeatures(TObservation observation, int actor, TAction action)
    {
        return SnapshotFeatures(
            _encoder.EncodePolicy(observation, actor, action),
            _encoder.PolicyFeatureCount,
            "policy"
        );
    }

    internal double[] ValueFeatures(TObservation observation, int observer, int player)
    {
        ValidateActor(observer);
        ValidateActor(player);
        return SnapshotFeatures(
            _encoder.EncodeValue(observation, observer, player),
            _encoder.ValueFeatureCount,
            "value"
        );
    }

    internal double PolicyLogit(IReadOnlyList<double> features) => Dot(_policyWeights, features);

    internal double PredictValue(int player, IReadOnlyList<double> features)
    {
        ValidateActor(player);
        return Math.Tanh(Dot(_valueWeights[player], features));
    }

    internal void ApplyGradients(
        IReadOnlyList<double> policyGradient,
        IReadOnlyList<IReadOnlyList<double>> valueGradients,
        double scale,
        double learningRate,
        double l2,
        double gradientClip
    )
    {
        if (policyGradient.Count != _policyWeights.Length
            || valueGradients.Count != _valueWeights.Length
            || valueGradients.Where((gradient, player) => gradient.Count != _valueWeights[player].Length).Any())
        {
            throw new ArgumentException("Gradient dimensions do not match model parameters.");
        }

        var gradients = new double[ParameterCount];
        var cursor = 0;
        for (var index = 0; index < _policyWeights.Length; index += 1)
        {
            gradients[cursor++] = policyGradient[index] * scale + l2 * _policyWeights[index];
        }
        for (var player = 0; player < _valueWeights.Length; player += 1)
        {
            for (var index = 0; index < _valueWeights[player].Length; index += 1)
            {
                gradients[cursor++] = valueGradients[player][index] * scale + l2 * _valueWeights[player][index];
            }
        }

        if (gradients.Any(value => !double.IsFinite(value)))
        {
            throw new InvalidOperationException("Linear policy/value gradients must be finite.");
        }
        double clipScale = GradientClipScale(gradients, gradientClip);
        cursor = 0;
        for (var index = 0; index < _policyWeights.Length; index += 1)
        {
            _policyWeights[index] -= learningRate * gradients[cursor++] * clipScale;
        }
        for (var player = 0; player < _valueWeights.Length; player += 1)
        {
            for (var index = 0; index < _valueWeights[player].Length; index += 1)
            {
                _valueWeights[player][index] -= learningRate * gradients[cursor++] * clipScale;
            }
        }
    }

    internal static double[] Softmax(IReadOnlyList<double> logits)
    {
        if (logits.Count == 0)
        {
            return [];
        }
        if (logits.Any(value => !double.IsFinite(value)))
        {
            throw new InvalidOperationException("Policy logits must be finite.");
        }
        double maximum = logits.Max();
        double[] exponentials = logits.Select(value => Math.Exp(value - maximum)).ToArray();
        double total = exponentials.Sum();
        return exponentials.Select(value => value / total).ToArray();
    }

    private static double GradientClipScale(IReadOnlyList<double> values, double limit)
    {
        double scale = 0.0;
        foreach (double value in values)
        {
            scale = Math.Max(scale, Math.Abs(value));
        }
        if (scale == 0.0)
        {
            return 1.0;
        }
        double normalizedNorm = Math.Sqrt(values.Sum(value =>
        {
            double ratio = value / scale;
            return ratio * ratio;
        }));
        return scale <= limit / normalizedNorm
            ? 1.0
            : (limit / scale) / normalizedNorm;
    }

    private static void ValidateEncoder(IPolicyValueFeatureEncoder<TObservation, TAction> encoder)
    {
        if (encoder.PolicyFeatureCount <= 0 || encoder.ValueFeatureCount <= 0)
        {
            throw new ArgumentException("Feature counts must be positive.", nameof(encoder));
        }
    }

    private void ValidateActor(int actor)
    {
        if (actor < 0 || actor >= PlayerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(actor));
        }
    }

    private static double[] SnapshotFeatures(
        IReadOnlyList<double> features,
        int expectedCount,
        string label
    )
    {
        ArgumentNullException.ThrowIfNull(features);
        double[] snapshot = features.ToArray();
        if (snapshot.Length != expectedCount || snapshot.Any(value => !double.IsFinite(value)))
        {
            throw new InvalidOperationException(
                $"The {label} feature encoder must return {expectedCount} finite values."
            );
        }
        return snapshot;
    }

    private static double Dot(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        if (left.Count != right.Count)
        {
            throw new ArgumentException("Vector dimensions must match.");
        }
        var result = 0.0;
        for (var index = 0; index < left.Count; index += 1)
        {
            result += left[index] * right[index];
            if (!double.IsFinite(result))
            {
                throw new InvalidOperationException("Linear policy/value evaluation overflowed.");
            }
        }
        return result;
    }
}
