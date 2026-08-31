using Fw.Rt.AI.Environment;
using Fw.Rt.Randomness;

namespace Fw.Rt.AI.Training;

public sealed class PolicyTarget<TAction>
    where TAction : notnull
{
    public PolicyTarget(TAction action, double probability)
    {
        if (!double.IsFinite(probability) || probability < 0.0 || probability > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(probability));
        }
        Action = action ?? throw new ArgumentNullException(nameof(action));
        Probability = probability;
    }

    public TAction Action { get; }
    public double Probability { get; }
}

public sealed class PolicyValueSample<TObservation, TAction>
    where TObservation : notnull
    where TAction : notnull
{
    public PolicyValueSample(
        TObservation observation,
        int actor,
        IEnumerable<TAction> legalActions,
        TAction selectedAction,
        IEnumerable<PolicyTarget<TAction>> policyTargets,
        IEnumerable<double> rewards,
        IEnumerable<double> valueTargets,
        double weight = 1.0
    )
    {
        if (actor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(actor));
        }
        if (!double.IsFinite(weight) || weight <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(weight));
        }

        Observation = observation ?? throw new ArgumentNullException(nameof(observation));
        Actor = actor;
        SelectedAction = selectedAction ?? throw new ArgumentNullException(nameof(selectedAction));
        Weight = weight;

        var legalSnapshot = (legalActions ?? throw new ArgumentNullException(nameof(legalActions))).ToArray();
        if (legalSnapshot.Length == 0 || legalSnapshot.Distinct().Count() != legalSnapshot.Length)
        {
            throw new ArgumentException("Legal actions must be non-empty and unique.", nameof(legalActions));
        }
        if (!legalSnapshot.Contains(SelectedAction))
        {
            throw new ArgumentException("Selected action must be legal.", nameof(selectedAction));
        }

        var policySnapshot = (policyTargets ?? throw new ArgumentNullException(nameof(policyTargets))).ToArray();
        if (policySnapshot.Length == 0 || policySnapshot.Select(item => item.Action).Distinct().Count() != policySnapshot.Length)
        {
            throw new ArgumentException("Policy targets must be non-empty and unique.", nameof(policyTargets));
        }
        if (policySnapshot.Any(item => !legalSnapshot.Contains(item.Action)))
        {
            throw new ArgumentException("Policy targets may only contain legal actions.", nameof(policyTargets));
        }
        var policyMass = policySnapshot.Sum(item => item.Probability);
        if (Math.Abs(policyMass - 1.0) > 1e-9)
        {
            throw new ArgumentException("Policy target probabilities must sum to one.", nameof(policyTargets));
        }

        var rewardSnapshot = SnapshotFinite(rewards, nameof(rewards));
        var valueSnapshot = SnapshotFinite(valueTargets, nameof(valueTargets));
        if (rewardSnapshot.Count == 0 || rewardSnapshot.Count != valueSnapshot.Count)
        {
            throw new ArgumentException("Rewards and value targets must have the same non-zero player count.");
        }

        LegalActions = Array.AsReadOnly(legalSnapshot);
        PolicyTargets = Array.AsReadOnly(policySnapshot);
        Rewards = rewardSnapshot;
        ValueTargets = valueSnapshot;
    }

    public TObservation Observation { get; }
    public int Actor { get; }
    public IReadOnlyList<TAction> LegalActions { get; }
    public TAction SelectedAction { get; }
    public IReadOnlyList<PolicyTarget<TAction>> PolicyTargets { get; }
    public IReadOnlyList<double> Rewards { get; }
    public IReadOnlyList<double> ValueTargets { get; }
    public double Weight { get; }

    private static IReadOnlyList<double> SnapshotFinite(IEnumerable<double> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values);
        var snapshot = values.ToArray();
        if (snapshot.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Values must be finite.");
        }
        return Array.AsReadOnly(snapshot);
    }
}

public sealed class TrainingTrajectory<TObservation, TAction>
    where TObservation : notnull
    where TAction : notnull
{
    public TrainingTrajectory(
        string environmentId,
        string episodeId,
        int seed,
        IEnumerable<PolicyValueSample<TObservation, TAction>> samples,
        GameEpisodeResult result
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(episodeId);
        Result = result ?? throw new ArgumentNullException(nameof(result));
        if (!Result.IsFinished)
        {
            throw new ArgumentException("A training trajectory must have a terminal or truncated result.", nameof(result));
        }

        var snapshot = (samples ?? throw new ArgumentNullException(nameof(samples))).ToArray();
        if (snapshot.Any(sample => sample.ValueTargets.Count != Result.Payoffs.Count))
        {
            throw new ArgumentException("Sample player counts must match the episode result.", nameof(samples));
        }

        EnvironmentId = environmentId;
        EpisodeId = episodeId;
        Seed = seed;
        Samples = Array.AsReadOnly(snapshot);
    }

    public string EnvironmentId { get; }
    public string EpisodeId { get; }
    public int Seed { get; }
    public IReadOnlyList<PolicyValueSample<TObservation, TAction>> Samples { get; }
    public GameEpisodeResult Result { get; }
}

public sealed class ReplayBuffer<TItem>
    where TItem : notnull
{
    private readonly TItem[] _items;
    private int _start;
    private int _count;

    public ReplayBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        Capacity = capacity;
        _items = new TItem[capacity];
    }

    public int Capacity { get; }
    public int Count => _count;

    public void Add(TItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_count < Capacity)
        {
            _items[PhysicalIndex(_count)] = item;
            _count += 1;
            return;
        }
        _items[_start] = item;
        _start = (_start + 1) % Capacity;
    }

    public void AddRange(IEnumerable<TItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (var item in items)
        {
            Add(item);
        }
    }

    public IReadOnlyList<TItem> Snapshot()
    {
        return Array.AsReadOnly(Enumerable.Range(0, _count)
            .Select(index => _items[PhysicalIndex(index)])
            .ToArray());
    }

    public IReadOnlyList<TItem> Sample(int count, DeterministicRandomStream random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (count < 0 || count > _count)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var indices = Enumerable.Range(0, _count).ToArray();
        var result = new TItem[count];
        for (var index = 0; index < count; index += 1)
        {
            var selected = random.Next(index, indices.Length);
            (indices[index], indices[selected]) = (indices[selected], indices[index]);
            result[index] = _items[PhysicalIndex(indices[index])];
        }
        return Array.AsReadOnly(result);
    }

    private int PhysicalIndex(int logicalIndex) => (_start + logicalIndex) % Capacity;
}
