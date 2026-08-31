using System.Text.Json;
using Fw.Rt.Randomness;

namespace Fw.Rt.AI.Model;

public sealed class DenseActorCriticPrediction
{
    public DenseActorCriticPrediction(IEnumerable<double> probabilities, double value)
    {
        var values = (probabilities ?? throw new ArgumentNullException(nameof(probabilities))).ToArray();
        if (values.Length == 0 || values.Any(item => !double.IsFinite(item) || item < 0.0))
        {
            throw new ArgumentOutOfRangeException(nameof(probabilities));
        }
        double mass = values.Sum();
        if (Math.Abs(mass - 1.0) > 1e-8 || !double.IsFinite(value))
        {
            throw new ArgumentException("Policy probabilities must sum to one and value must be finite.");
        }

        Probabilities = Array.AsReadOnly(values);
        Value = value;
    }

    public IReadOnlyList<double> Probabilities { get; }
    public double Value { get; }
}

public readonly record struct DensePolicyChoice(int Action, double Probability, double Value);

public sealed class DenseActorCriticCheckpoint
{
    public const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    public DenseActorCriticCheckpoint(
        int version,
        string schemaId,
        int inputCount,
        int hiddenCount,
        int actionCount,
        IEnumerable<double> inputWeights,
        IEnumerable<double> hiddenBias,
        IEnumerable<double> policyWeights,
        IEnumerable<double> policyBias,
        IEnumerable<double> valueWeights,
        double valueBias
    )
    {
        if (version != CurrentVersion)
        {
            throw new ArgumentException($"Unsupported dense actor-critic checkpoint version {version}.", nameof(version));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaId);
        if (inputCount <= 0 || hiddenCount <= 0 || actionCount <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(inputCount));
        }

        Version = version;
        SchemaId = schemaId;
        InputCount = inputCount;
        HiddenCount = hiddenCount;
        ActionCount = actionCount;
        InputWeights = Snapshot(inputWeights, hiddenCount * inputCount, nameof(inputWeights));
        HiddenBias = Snapshot(hiddenBias, hiddenCount, nameof(hiddenBias));
        PolicyWeights = Snapshot(policyWeights, actionCount * hiddenCount, nameof(policyWeights));
        PolicyBias = Snapshot(policyBias, actionCount, nameof(policyBias));
        ValueWeights = Snapshot(valueWeights, hiddenCount, nameof(valueWeights));
        if (!double.IsFinite(valueBias))
        {
            throw new ArgumentOutOfRangeException(nameof(valueBias));
        }
        ValueBias = valueBias;
    }

    public int Version { get; }
    public string SchemaId { get; }
    public int InputCount { get; }
    public int HiddenCount { get; }
    public int ActionCount { get; }
    public IReadOnlyList<double> InputWeights { get; }
    public IReadOnlyList<double> HiddenBias { get; }
    public IReadOnlyList<double> PolicyWeights { get; }
    public IReadOnlyList<double> PolicyBias { get; }
    public IReadOnlyList<double> ValueWeights { get; }
    public double ValueBias { get; }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static DenseActorCriticCheckpoint FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("Checkpoint JSON is empty.", nameof(json));
        }
        CheckpointPayload payload = JsonSerializer.Deserialize<CheckpointPayload>(json, JsonOptions)
            ?? throw new ArgumentException("Checkpoint JSON is invalid.", nameof(json));
        return new DenseActorCriticCheckpoint(
            payload.Version,
            payload.SchemaId,
            payload.InputCount,
            payload.HiddenCount,
            payload.ActionCount,
            payload.InputWeights,
            payload.HiddenBias,
            payload.PolicyWeights,
            payload.PolicyBias,
            payload.ValueWeights,
            payload.ValueBias
        );
    }

    private static IReadOnlyList<double> Snapshot(
        IEnumerable<double> values,
        int expected,
        string parameter
    )
    {
        var snapshot = (values ?? throw new ArgumentNullException(parameter)).ToArray();
        if (snapshot.Length != expected || snapshot.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException($"Checkpoint array '{parameter}' has invalid dimensions or values.", parameter);
        }
        return Array.AsReadOnly(snapshot);
    }

    private sealed class CheckpointPayload
    {
        public int Version { get; set; }
        public string SchemaId { get; set; } = "";
        public int InputCount { get; set; }
        public int HiddenCount { get; set; }
        public int ActionCount { get; set; }
        public double[] InputWeights { get; set; } = [];
        public double[] HiddenBias { get; set; } = [];
        public double[] PolicyWeights { get; set; } = [];
        public double[] PolicyBias { get; set; } = [];
        public double[] ValueWeights { get; set; } = [];
        public double ValueBias { get; set; }
    }
}

public sealed class DenseActorCriticModel
{
    internal readonly double[] InputWeights;
    internal readonly double[] HiddenBias;
    internal readonly double[] PolicyWeights;
    internal readonly double[] PolicyBias;
    internal readonly double[] ValueWeights;
    internal readonly double[] ValueBias;

    public DenseActorCriticModel(
        string schemaId,
        int inputCount,
        int hiddenCount,
        int actionCount,
        int seed = 1
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaId);
        if (inputCount <= 0 || hiddenCount <= 0 || actionCount <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(inputCount));
        }
        SchemaId = schemaId;
        InputCount = inputCount;
        HiddenCount = hiddenCount;
        ActionCount = actionCount;
        InputWeights = new double[inputCount * hiddenCount];
        HiddenBias = new double[hiddenCount];
        PolicyWeights = new double[hiddenCount * actionCount];
        PolicyBias = new double[actionCount];
        ValueWeights = new double[hiddenCount];
        ValueBias = new double[1];

        var random = new DeterministicRandomStream(seed);
        Initialize(InputWeights, random, Math.Sqrt(2.0 / (inputCount + hiddenCount)));
        Initialize(PolicyWeights, random, Math.Sqrt(2.0 / (hiddenCount + actionCount)));
        Initialize(ValueWeights, random, Math.Sqrt(2.0 / (hiddenCount + 1)));
    }

    public DenseActorCriticModel(DenseActorCriticCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        SchemaId = checkpoint.SchemaId;
        InputCount = checkpoint.InputCount;
        HiddenCount = checkpoint.HiddenCount;
        ActionCount = checkpoint.ActionCount;
        InputWeights = checkpoint.InputWeights.ToArray();
        HiddenBias = checkpoint.HiddenBias.ToArray();
        PolicyWeights = checkpoint.PolicyWeights.ToArray();
        PolicyBias = checkpoint.PolicyBias.ToArray();
        ValueWeights = checkpoint.ValueWeights.ToArray();
        ValueBias = [checkpoint.ValueBias];
    }

    public string SchemaId { get; }
    public int InputCount { get; }
    public int HiddenCount { get; }
    public int ActionCount { get; }

    public DenseActorCriticPrediction Predict(
        IReadOnlyList<double> observation,
        IReadOnlyList<bool>? actionMask = null,
        double temperature = 1.0
    )
    {
        DenseActorCriticForward forward = Forward(observation, actionMask, temperature);
        return new DenseActorCriticPrediction(forward.Probabilities, forward.Value);
    }

    public DensePolicyChoice Choose(
        IReadOnlyList<double> observation,
        IReadOnlyList<bool>? actionMask,
        DeterministicRandomStream random,
        bool sample = true,
        double temperature = 1.0
    )
    {
        ArgumentNullException.ThrowIfNull(random);
        DenseActorCriticForward forward = Forward(observation, actionMask, temperature);
        int selected = 0;
        if (sample)
        {
            double roll = random.NextDouble();
            double cumulative = 0.0;
            for (int index = 0; index < forward.Probabilities.Length; index += 1)
            {
                cumulative += forward.Probabilities[index];
                if (roll <= cumulative)
                {
                    selected = index;
                    break;
                }
            }
        }
        else
        {
            double best = double.NegativeInfinity;
            for (int index = 0; index < forward.Probabilities.Length; index += 1)
            {
                if (forward.Probabilities[index] > best)
                {
                    selected = index;
                    best = forward.Probabilities[index];
                }
            }
        }
        return new DensePolicyChoice(selected, forward.Probabilities[selected], forward.Value);
    }

    public DenseActorCriticCheckpoint ExportCheckpoint()
    {
        return new DenseActorCriticCheckpoint(
            DenseActorCriticCheckpoint.CurrentVersion,
            SchemaId,
            InputCount,
            HiddenCount,
            ActionCount,
            InputWeights,
            HiddenBias,
            PolicyWeights,
            PolicyBias,
            ValueWeights,
            ValueBias[0]
        );
    }

    internal DenseActorCriticForward Forward(
        IReadOnlyList<double> observation,
        IReadOnlyList<bool>? actionMask,
        double temperature = 1.0
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.Count != InputCount || observation.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException("Observation dimensions or values are invalid.", nameof(observation));
        }
        if (actionMask != null && actionMask.Count != ActionCount)
        {
            throw new ArgumentException("Action mask dimensions are invalid.", nameof(actionMask));
        }
        if (!double.IsFinite(temperature) || temperature <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(temperature));
        }

        var hidden = new double[HiddenCount];
        for (int hiddenIndex = 0; hiddenIndex < HiddenCount; hiddenIndex += 1)
        {
            double sum = HiddenBias[hiddenIndex];
            int offset = hiddenIndex * InputCount;
            for (int inputIndex = 0; inputIndex < InputCount; inputIndex += 1)
            {
                sum += InputWeights[offset + inputIndex] * observation[inputIndex];
            }
            hidden[hiddenIndex] = Math.Tanh(sum);
        }

        var logits = new double[ActionCount];
        bool anyLegal = false;
        for (int action = 0; action < ActionCount; action += 1)
        {
            bool legal = actionMask == null || actionMask[action];
            anyLegal |= legal;
            if (!legal)
            {
                logits[action] = double.NegativeInfinity;
                continue;
            }
            double sum = PolicyBias[action];
            int offset = action * HiddenCount;
            for (int hiddenIndex = 0; hiddenIndex < HiddenCount; hiddenIndex += 1)
            {
                sum += PolicyWeights[offset + hiddenIndex] * hidden[hiddenIndex];
            }
            logits[action] = sum / temperature;
        }
        if (!anyLegal)
        {
            throw new ArgumentException("At least one action must be legal.", nameof(actionMask));
        }

        double max = logits.Where(double.IsFinite).Max();
        var probabilities = new double[ActionCount];
        double mass = 0.0;
        for (int action = 0; action < ActionCount; action += 1)
        {
            if (!double.IsFinite(logits[action]))
            {
                continue;
            }
            probabilities[action] = Math.Exp(logits[action] - max);
            mass += probabilities[action];
        }
        for (int action = 0; action < ActionCount; action += 1)
        {
            probabilities[action] /= mass;
        }

        double valuePre = ValueBias[0];
        for (int hiddenIndex = 0; hiddenIndex < HiddenCount; hiddenIndex += 1)
        {
            valuePre += ValueWeights[hiddenIndex] * hidden[hiddenIndex];
        }
        return new DenseActorCriticForward(hidden, probabilities, Math.Tanh(valuePre));
    }

    private static void Initialize(
        double[] values,
        DeterministicRandomStream random,
        double scale
    )
    {
        for (int index = 0; index < values.Length; index += 2)
        {
            double first = Math.Max(random.NextDouble(), 1e-12);
            double second = random.NextDouble();
            double radius = Math.Sqrt(-2.0 * Math.Log(first));
            double angle = 2.0 * Math.PI * second;
            values[index] = radius * Math.Cos(angle) * scale;
            if (index + 1 < values.Length)
            {
                values[index + 1] = radius * Math.Sin(angle) * scale;
            }
        }
    }
}

internal sealed record DenseActorCriticForward(
    double[] Hidden,
    double[] Probabilities,
    double Value
);
