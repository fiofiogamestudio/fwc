using Fw.Rt.AI.Model;

namespace Fw.Rt.AI.Training;

public sealed class LinearPolicyValueTrainingOptions
{
    public LinearPolicyValueTrainingOptions(
        int epochs = 1,
        int batchSize = 32,
        double learningRate = 0.01,
        double l2 = 0.0001,
        double gradientClip = 10.0
    )
    {
        if (epochs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(epochs));
        }
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }
        if (!double.IsFinite(learningRate) || learningRate <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(learningRate));
        }
        if (!double.IsFinite(l2) || l2 < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(l2));
        }
        if (!double.IsFinite(gradientClip) || gradientClip <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(gradientClip));
        }
        Epochs = epochs;
        BatchSize = batchSize;
        LearningRate = learningRate;
        L2 = l2;
        GradientClip = gradientClip;
    }

    public int Epochs { get; }
    public int BatchSize { get; }
    public double LearningRate { get; }
    public double L2 { get; }
    public double GradientClip { get; }
}

public sealed class LinearPolicyValueTrainingResult
{
    public LinearPolicyValueTrainingResult(
        int samples,
        int updates,
        double meanPolicyLoss,
        double meanValueLoss
    )
    {
        Samples = samples;
        Updates = updates;
        MeanPolicyLoss = meanPolicyLoss;
        MeanValueLoss = meanValueLoss;
    }

    public int Samples { get; }
    public int Updates { get; }
    public double MeanPolicyLoss { get; }
    public double MeanValueLoss { get; }
}

public static class LinearPolicyValueTrainer
{
    public static LinearPolicyValueTrainingResult Train<TObservation, TAction>(
        LinearPolicyValueModel<TObservation, TAction> model,
        IEnumerable<PolicyValueSample<TObservation, TAction>> samples,
        LinearPolicyValueTrainingOptions? options = null
    )
        where TObservation : notnull
        where TAction : notnull
    {
        ArgumentNullException.ThrowIfNull(model);
        PolicyValueSample<TObservation, TAction>[] data = (
            samples ?? throw new ArgumentNullException(nameof(samples))
        ).ToArray();
        if (data.Length == 0)
        {
            throw new ArgumentException("At least one training sample is required.", nameof(samples));
        }
        options ??= new LinearPolicyValueTrainingOptions();

        var updates = 0;
        var weightedPolicyLoss = 0.0;
        var weightedValueLoss = 0.0;
        var totalWeight = 0.0;
        for (var epoch = 0; epoch < options.Epochs; epoch += 1)
        {
            for (var offset = 0; offset < data.Length; offset += options.BatchSize)
            {
                int count = Math.Min(options.BatchSize, data.Length - offset);
                var policyGradient = new double[model.PolicyFeatureCount];
                var valueGradient = Enumerable.Range(0, model.PlayerCount)
                    .Select(_ => new double[model.ValueFeatureCount])
                    .ToArray();
                var batchWeight = 0.0;

                for (var sampleIndex = offset; sampleIndex < offset + count; sampleIndex += 1)
                {
                    PolicyValueSample<TObservation, TAction> sample = data[sampleIndex];
                    if (sample.Actor >= model.PlayerCount || sample.ValueTargets.Count != model.PlayerCount)
                    {
                        throw new ArgumentException("Training sample player counts do not match the model.", nameof(samples));
                    }
                    if (sample.ValueTargets.Any(value => value < -1.0 || value > 1.0))
                    {
                        throw new ArgumentOutOfRangeException(nameof(samples), "Linear value targets must be in [-1, 1].");
                    }

                    double[][] policyFeatures = sample.LegalActions
                        .Select(action => model.PolicyFeatures(sample.Observation, sample.Actor, action))
                        .ToArray();
                    double[] logits = policyFeatures.Select(model.PolicyLogit).ToArray();
                    double[] probabilities = LinearPolicyValueModel<TObservation, TAction>.Softmax(logits);
                    var targets = sample.PolicyTargets.ToDictionary(
                        item => item.Action,
                        item => item.Probability
                    );
                    for (var actionIndex = 0; actionIndex < sample.LegalActions.Count; actionIndex += 1)
                    {
                        double target = targets.GetValueOrDefault(sample.LegalActions[actionIndex]);
                        weightedPolicyLoss -= sample.Weight * target
                            * Math.Log(Math.Max(1e-15, probabilities[actionIndex]));
                        double coefficient = sample.Weight * (probabilities[actionIndex] - target);
                        AddScaled(policyGradient, policyFeatures[actionIndex], coefficient);
                    }

                    for (var player = 0; player < model.PlayerCount; player += 1)
                    {
                        double[] features = model.ValueFeatures(sample.Observation, sample.Actor, player);
                        double prediction = model.PredictValue(player, features);
                        double error = prediction - sample.ValueTargets[player];
                        weightedValueLoss += sample.Weight * error * error;
                        double coefficient = sample.Weight * 2.0 * error * (1.0 - prediction * prediction);
                        AddScaled(valueGradient[player], features, coefficient);
                    }
                    batchWeight += sample.Weight;
                    totalWeight += sample.Weight;
                }

                model.ApplyGradients(
                    policyGradient,
                    valueGradient,
                    1.0 / batchWeight,
                    options.LearningRate,
                    options.L2,
                    options.GradientClip
                );
                updates += 1;
            }
        }

        double lossDenominator = totalWeight;
        return new LinearPolicyValueTrainingResult(
            data.Length * options.Epochs,
            updates,
            weightedPolicyLoss / lossDenominator,
            weightedValueLoss / (lossDenominator * model.PlayerCount)
        );
    }

    private static void AddScaled(double[] target, IReadOnlyList<double> source, double scale)
    {
        for (var index = 0; index < target.Length; index += 1)
        {
            target[index] += source[index] * scale;
        }
    }
}
