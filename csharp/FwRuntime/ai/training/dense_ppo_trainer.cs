using Fw.Rt.AI.Model;
using Fw.Rt.Randomness;

namespace Fw.Rt.AI.Training;

public sealed class DensePpoSample
{
    public DensePpoSample(
        IEnumerable<double> observation,
        IEnumerable<bool> actionMask,
        int action,
        double oldLogProbability,
        double advantage,
        double valueTarget,
        double weight = 1.0
    )
    {
        Observation = Snapshot(observation, nameof(observation));
        ActionMask = (actionMask ?? throw new ArgumentNullException(nameof(actionMask))).ToArray();
        if (ActionMask.Length == 0 || !ActionMask.Any(item => item))
        {
            throw new ArgumentException("At least one action must be legal.", nameof(actionMask));
        }
        if (action < 0 || action >= ActionMask.Length || !ActionMask[action])
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }
        if (!double.IsFinite(oldLogProbability) || !double.IsFinite(advantage)
            || !double.IsFinite(valueTarget) || !double.IsFinite(weight) || weight <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(oldLogProbability));
        }
        Action = action;
        OldLogProbability = oldLogProbability;
        Advantage = advantage;
        ValueTarget = valueTarget;
        Weight = weight;
    }

    public double[] Observation { get; }
    public bool[] ActionMask { get; }
    public int Action { get; }
    public double OldLogProbability { get; }
    public double Advantage { get; }
    public double ValueTarget { get; }
    public double Weight { get; }

    private static double[] Snapshot(IEnumerable<double> values, string parameter)
    {
        var result = (values ?? throw new ArgumentNullException(parameter)).ToArray();
        if (result.Length == 0 || result.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException("Observation must contain finite values.", parameter);
        }
        return result;
    }
}

public sealed class DenseImitationSample
{
    public DenseImitationSample(
        IEnumerable<double> observation,
        IEnumerable<bool> actionMask,
        int action,
        double weight = 1.0
    )
    {
        Observation = (observation ?? throw new ArgumentNullException(nameof(observation))).ToArray();
        ActionMask = (actionMask ?? throw new ArgumentNullException(nameof(actionMask))).ToArray();
        if (Observation.Length == 0 || Observation.Any(value => !double.IsFinite(value))
            || ActionMask.Length == 0 || action < 0 || action >= ActionMask.Length
            || !ActionMask[action] || !double.IsFinite(weight) || weight <= 0.0)
        {
            throw new ArgumentException("Imitation sample is invalid.");
        }
        Action = action;
        Weight = weight;
    }

    public double[] Observation { get; }
    public bool[] ActionMask { get; }
    public int Action { get; }
    public double Weight { get; }
}

public sealed class DensePpoTrainingOptions
{
    public DensePpoTrainingOptions(
        int epochs = 4,
        int batchSize = 128,
        double learningRate = 0.0003,
        double clipRatio = 0.2,
        double valueCoefficient = 0.5,
        double entropyCoefficient = 0.01,
        double gradientClip = 1.0,
        double adamBeta1 = 0.9,
        double adamBeta2 = 0.999,
        double adamEpsilon = 1e-8,
        double targetKl = 0.0
    )
    {
        if (epochs <= 0 || batchSize <= 0 || !Positive(learningRate)
            || !Positive(clipRatio) || !NonNegative(valueCoefficient)
            || !NonNegative(entropyCoefficient) || !Positive(gradientClip)
            || !NonNegative(targetKl)
            || adamBeta1 <= 0.0 || adamBeta1 >= 1.0 || adamBeta2 <= 0.0 || adamBeta2 >= 1.0
            || !Positive(adamEpsilon))
        {
            throw new ArgumentOutOfRangeException(nameof(epochs));
        }
        Epochs = epochs;
        BatchSize = batchSize;
        LearningRate = learningRate;
        ClipRatio = clipRatio;
        ValueCoefficient = valueCoefficient;
        EntropyCoefficient = entropyCoefficient;
        GradientClip = gradientClip;
        TargetKl = targetKl;
        AdamBeta1 = adamBeta1;
        AdamBeta2 = adamBeta2;
        AdamEpsilon = adamEpsilon;
    }

    public int Epochs { get; }
    public int BatchSize { get; }
    public double LearningRate { get; }
    public double ClipRatio { get; }
    public double ValueCoefficient { get; }
    public double EntropyCoefficient { get; }
    public double GradientClip { get; }
    public double TargetKl { get; }
    public double AdamBeta1 { get; }
    public double AdamBeta2 { get; }
    public double AdamEpsilon { get; }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0.0;
    private static bool NonNegative(double value) => double.IsFinite(value) && value >= 0.0;
}

public sealed record DenseTrainingResult(
    int Samples,
    int Updates,
    double PolicyLoss,
    double ValueLoss,
    double Entropy,
    double ApproximateKl
);

public sealed class DensePpoTrainer
{
    private readonly DenseActorCriticModel _model;
    private readonly DensePpoTrainingOptions _options;
    private readonly AdamSet _adam;

    public DensePpoTrainer(
        DenseActorCriticModel model,
        DensePpoTrainingOptions? options = null
    )
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _options = options ?? new DensePpoTrainingOptions();
        _adam = new AdamSet(model);
    }

    public DenseTrainingResult Train(
        IEnumerable<DensePpoSample> samples,
        DeterministicRandomStream random
    )
    {
        ArgumentNullException.ThrowIfNull(random);
        DensePpoSample[] data = (samples ?? throw new ArgumentNullException(nameof(samples))).ToArray();
        ValidatePpo(data);
        int[] order = Enumerable.Range(0, data.Length).ToArray();
        var metrics = new MetricAccumulator();
        int firstUpdate = _adam.Step;
        int completedEpochs = 0;
        for (int epoch = 0; epoch < _options.Epochs; epoch += 1)
        {
            var epochMetrics = new MetricAccumulator();
            RandomPicker.Shuffle(order, random);
            for (int offset = 0; offset < order.Length; offset += _options.BatchSize)
            {
                int count = Math.Min(_options.BatchSize, order.Length - offset);
                var gradients = new GradientSet(_model);
                double batchWeight = 0.0;
                for (int index = 0; index < count; index += 1)
                {
                    DensePpoSample sample = data[order[offset + index]];
                    AccumulatePpo(sample, gradients, epochMetrics);
                    batchWeight += sample.Weight;
                }
                Apply(gradients, batchWeight);
            }
            completedEpochs += 1;
            metrics.Merge(epochMetrics);
            if (_options.TargetKl > 0.0 && epochMetrics.ApproximateKl > _options.TargetKl)
            {
                break;
            }
        }
        return metrics.Result(data.Length * completedEpochs, _adam.Step - firstUpdate);
    }

    public DenseTrainingResult TrainImitation(
        IEnumerable<DenseImitationSample> samples,
        DeterministicRandomStream random,
        int epochs = 1
    )
    {
        ArgumentNullException.ThrowIfNull(random);
        if (epochs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(epochs));
        }
        DenseImitationSample[] data = (samples ?? throw new ArgumentNullException(nameof(samples))).ToArray();
        if (data.Length == 0 || data.Any(sample => sample.Observation.Length != _model.InputCount
            || sample.ActionMask.Length != _model.ActionCount))
        {
            throw new ArgumentException("Imitation samples do not match model dimensions.", nameof(samples));
        }
        int[] order = Enumerable.Range(0, data.Length).ToArray();
        var metrics = new MetricAccumulator();
        int firstUpdate = _adam.Step;
        for (int epoch = 0; epoch < epochs; epoch += 1)
        {
            RandomPicker.Shuffle(order, random);
            for (int offset = 0; offset < order.Length; offset += _options.BatchSize)
            {
                int count = Math.Min(_options.BatchSize, order.Length - offset);
                var gradients = new GradientSet(_model);
                double batchWeight = 0.0;
                for (int index = 0; index < count; index += 1)
                {
                    DenseImitationSample sample = data[order[offset + index]];
                    AccumulateImitation(sample, gradients, metrics);
                    batchWeight += sample.Weight;
                }
                Apply(gradients, batchWeight);
            }
        }
        return metrics.Result(data.Length * epochs, _adam.Step - firstUpdate);
    }

    private void AccumulatePpo(
        DensePpoSample sample,
        GradientSet gradients,
        MetricAccumulator metrics
    )
    {
        DenseActorCriticForward forward = _model.Forward(sample.Observation, sample.ActionMask);
        double probability = Math.Max(forward.Probabilities[sample.Action], 1e-12);
        double logProbability = Math.Log(probability);
        double logRatio = Math.Clamp(logProbability - sample.OldLogProbability, -20.0, 20.0);
        double ratio = Math.Exp(logRatio);
        double clipped = Math.Clamp(ratio, 1.0 - _options.ClipRatio, 1.0 + _options.ClipRatio);
        bool useGradient = sample.Advantage >= 0.0 ? ratio <= clipped : ratio >= clipped;
        double coefficient = useGradient ? -sample.Advantage * ratio * sample.Weight : 0.0;
        var logitsGradient = new double[_model.ActionCount];
        for (int action = 0; action < _model.ActionCount; action += 1)
        {
            if (!sample.ActionMask[action])
            {
                continue;
            }
            double oneHot = action == sample.Action ? 1.0 : 0.0;
            logitsGradient[action] = coefficient * (oneHot - forward.Probabilities[action]);
        }
        AddEntropyGradient(forward.Probabilities, sample.ActionMask, logitsGradient, sample.Weight);

        double valueError = forward.Value - sample.ValueTarget;
        double valuePreGradient = _options.ValueCoefficient * 2.0 * valueError
            * (1.0 - forward.Value * forward.Value) * sample.Weight;
        Backpropagate(sample.Observation, forward, logitsGradient, valuePreGradient, gradients);

        double entropy = Entropy(forward.Probabilities);
        double surrogate = Math.Min(ratio * sample.Advantage, clipped * sample.Advantage);
        metrics.Add(
            -surrogate * sample.Weight,
            valueError * valueError * sample.Weight,
            entropy * sample.Weight,
            ((ratio - 1.0) - logRatio) * sample.Weight,
            sample.Weight
        );
    }

    private void AccumulateImitation(
        DenseImitationSample sample,
        GradientSet gradients,
        MetricAccumulator metrics
    )
    {
        DenseActorCriticForward forward = _model.Forward(sample.Observation, sample.ActionMask);
        var logitsGradient = new double[_model.ActionCount];
        for (int action = 0; action < _model.ActionCount; action += 1)
        {
            if (!sample.ActionMask[action])
            {
                continue;
            }
            logitsGradient[action] = (forward.Probabilities[action]
                - (action == sample.Action ? 1.0 : 0.0)) * sample.Weight;
        }
        AddEntropyGradient(forward.Probabilities, sample.ActionMask, logitsGradient, sample.Weight);
        Backpropagate(sample.Observation, forward, logitsGradient, 0.0, gradients);
        metrics.Add(
            -Math.Log(Math.Max(forward.Probabilities[sample.Action], 1e-12)) * sample.Weight,
            0.0,
            Entropy(forward.Probabilities) * sample.Weight,
            0.0,
            sample.Weight
        );
    }

    private void AddEntropyGradient(
        IReadOnlyList<double> probabilities,
        IReadOnlyList<bool> mask,
        double[] gradient,
        double weight
    )
    {
        if (_options.EntropyCoefficient <= 0.0)
        {
            return;
        }
        double entropy = Entropy(probabilities);
        for (int action = 0; action < gradient.Length; action += 1)
        {
            if (!mask[action] || probabilities[action] <= 0.0)
            {
                continue;
            }
            gradient[action] += _options.EntropyCoefficient * weight * probabilities[action]
                * (Math.Log(probabilities[action]) + entropy);
        }
    }

    private void Backpropagate(
        IReadOnlyList<double> observation,
        DenseActorCriticForward forward,
        IReadOnlyList<double> logitsGradient,
        double valuePreGradient,
        GradientSet gradients
    )
    {
        var hiddenGradient = new double[_model.HiddenCount];
        for (int action = 0; action < _model.ActionCount; action += 1)
        {
            double coefficient = logitsGradient[action];
            gradients.PolicyBias[action] += coefficient;
            int offset = action * _model.HiddenCount;
            for (int hidden = 0; hidden < _model.HiddenCount; hidden += 1)
            {
                gradients.PolicyWeights[offset + hidden] += coefficient * forward.Hidden[hidden];
                hiddenGradient[hidden] += coefficient * _model.PolicyWeights[offset + hidden];
            }
        }
        gradients.ValueBias[0] += valuePreGradient;
        for (int hidden = 0; hidden < _model.HiddenCount; hidden += 1)
        {
            gradients.ValueWeights[hidden] += valuePreGradient * forward.Hidden[hidden];
            hiddenGradient[hidden] += valuePreGradient * _model.ValueWeights[hidden];
            double hiddenPreGradient = hiddenGradient[hidden]
                * (1.0 - forward.Hidden[hidden] * forward.Hidden[hidden]);
            gradients.HiddenBias[hidden] += hiddenPreGradient;
            int offset = hidden * _model.InputCount;
            for (int input = 0; input < _model.InputCount; input += 1)
            {
                gradients.InputWeights[offset + input] += hiddenPreGradient * observation[input];
            }
        }
    }

    private void Apply(GradientSet gradients, double weight)
    {
        if (!double.IsFinite(weight) || weight <= 0.0)
        {
            throw new InvalidOperationException("Training batch weight must be positive.");
        }
        gradients.Scale(1.0 / weight);
        gradients.Clip(_options.GradientClip);
        _adam.Apply(_model, gradients, _options);
    }

    private void ValidatePpo(IReadOnlyList<DensePpoSample> data)
    {
        if (data.Count == 0 || data.Any(sample => sample.Observation.Length != _model.InputCount
            || sample.ActionMask.Length != _model.ActionCount))
        {
            throw new ArgumentException("PPO samples do not match model dimensions.", nameof(data));
        }
    }

    private static double Entropy(IReadOnlyList<double> probabilities)
    {
        double result = 0.0;
        foreach (double probability in probabilities)
        {
            if (probability > 0.0)
            {
                result -= probability * Math.Log(probability);
            }
        }
        return result;
    }

    private sealed class MetricAccumulator
    {
        private double _policy;
        private double _value;
        private double _entropy;
        private double _kl;
        private double _weight;

        public void Add(double policy, double value, double entropy, double kl, double weight)
        {
            _policy += policy;
            _value += value;
            _entropy += entropy;
            _kl += kl;
            _weight += weight;
        }

        public double ApproximateKl => _kl / Math.Max(_weight, 1e-12);

        public void Merge(MetricAccumulator other)
        {
            _policy += other._policy;
            _value += other._value;
            _entropy += other._entropy;
            _kl += other._kl;
            _weight += other._weight;
        }

        public DenseTrainingResult Result(int samples, int updates)
        {
            double divisor = Math.Max(_weight, 1e-12);
            return new DenseTrainingResult(
                samples,
                updates,
                _policy / divisor,
                _value / divisor,
                _entropy / divisor,
                _kl / divisor
            );
        }
    }

    private sealed class GradientSet
    {
        public GradientSet(DenseActorCriticModel model)
        {
            InputWeights = new double[model.InputWeights.Length];
            HiddenBias = new double[model.HiddenBias.Length];
            PolicyWeights = new double[model.PolicyWeights.Length];
            PolicyBias = new double[model.PolicyBias.Length];
            ValueWeights = new double[model.ValueWeights.Length];
            ValueBias = new double[1];
        }

        public double[] InputWeights { get; }
        public double[] HiddenBias { get; }
        public double[] PolicyWeights { get; }
        public double[] PolicyBias { get; }
        public double[] ValueWeights { get; }
        public double[] ValueBias { get; }

        public void Scale(double scale)
        {
            ForEach(array =>
            {
                for (int index = 0; index < array.Length; index += 1)
                {
                    array[index] *= scale;
                }
            });
        }

        public void Clip(double maxNorm)
        {
            double sum = 0.0;
            ForEach(array =>
            {
                foreach (double value in array)
                {
                    sum += value * value;
                }
            });
            double norm = Math.Sqrt(sum);
            if (norm > maxNorm)
            {
                Scale(maxNorm / norm);
            }
        }

        public void ForEach(Action<double[]> action)
        {
            action(InputWeights);
            action(HiddenBias);
            action(PolicyWeights);
            action(PolicyBias);
            action(ValueWeights);
            action(ValueBias);
        }
    }

    private sealed class AdamSet
    {
        private readonly AdamArray _inputWeights;
        private readonly AdamArray _hiddenBias;
        private readonly AdamArray _policyWeights;
        private readonly AdamArray _policyBias;
        private readonly AdamArray _valueWeights;
        private readonly AdamArray _valueBias;

        public AdamSet(DenseActorCriticModel model)
        {
            _inputWeights = new AdamArray(model.InputWeights.Length);
            _hiddenBias = new AdamArray(model.HiddenBias.Length);
            _policyWeights = new AdamArray(model.PolicyWeights.Length);
            _policyBias = new AdamArray(model.PolicyBias.Length);
            _valueWeights = new AdamArray(model.ValueWeights.Length);
            _valueBias = new AdamArray(1);
        }

        public int Step { get; private set; }

        public void Apply(
            DenseActorCriticModel model,
            GradientSet gradients,
            DensePpoTrainingOptions options
        )
        {
            Step += 1;
            _inputWeights.Apply(model.InputWeights, gradients.InputWeights, options, Step);
            _hiddenBias.Apply(model.HiddenBias, gradients.HiddenBias, options, Step);
            _policyWeights.Apply(model.PolicyWeights, gradients.PolicyWeights, options, Step);
            _policyBias.Apply(model.PolicyBias, gradients.PolicyBias, options, Step);
            _valueWeights.Apply(model.ValueWeights, gradients.ValueWeights, options, Step);
            _valueBias.Apply(model.ValueBias, gradients.ValueBias, options, Step);
        }
    }

    private sealed class AdamArray(int length)
    {
        private readonly double[] _first = new double[length];
        private readonly double[] _second = new double[length];

        public void Apply(
            double[] parameters,
            IReadOnlyList<double> gradients,
            DensePpoTrainingOptions options,
            int step
        )
        {
            double firstCorrection = 1.0 - Math.Pow(options.AdamBeta1, step);
            double secondCorrection = 1.0 - Math.Pow(options.AdamBeta2, step);
            for (int index = 0; index < parameters.Length; index += 1)
            {
                double gradient = gradients[index];
                _first[index] = options.AdamBeta1 * _first[index]
                    + (1.0 - options.AdamBeta1) * gradient;
                _second[index] = options.AdamBeta2 * _second[index]
                    + (1.0 - options.AdamBeta2) * gradient * gradient;
                double first = _first[index] / firstCorrection;
                double second = _second[index] / secondCorrection;
                parameters[index] -= options.LearningRate * first
                    / (Math.Sqrt(second) + options.AdamEpsilon);
            }
        }
    }
}
