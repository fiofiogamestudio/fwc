using Fw.Rt.Randomness;

namespace Fw.Rt.AI.Evaluation;

public sealed record LeaguePayoff(int Games, double Mean, double Wins, double Draws, double Losses);

public sealed class ZeroSumLeague
{
    private readonly Dictionary<(string First, string Second), Entry> _entries = [];

    public void Add(string first, string second, double firstPayoff)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(first);
        ArgumentException.ThrowIfNullOrWhiteSpace(second);
        if (string.Equals(first, second, StringComparison.Ordinal) || !double.IsFinite(firstPayoff)
            || firstPayoff < -1.0 || firstPayoff > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(firstPayoff));
        }
        AddOne(first, second, firstPayoff);
        AddOne(second, first, -firstPayoff);
    }

    public LeaguePayoff Get(string first, string second)
    {
        return _entries.TryGetValue((first, second), out Entry? entry)
            ? entry.Snapshot()
            : new LeaguePayoff(0, 0.0, 0.0, 0.0, 0.0);
    }

    public IReadOnlyDictionary<string, double> MetaStrategy(
        IEnumerable<string> modelIds,
        int iterations = 2000
    )
    {
        string[] ids = (modelIds ?? throw new ArgumentNullException(nameof(modelIds)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0 || iterations <= 0)
        {
            throw new ArgumentException("A league strategy needs models and positive iterations.");
        }
        if (ids.Length == 1)
        {
            return new Dictionary<string, double>(StringComparer.Ordinal) { [ids[0]] = 1.0 };
        }

        var regrets = new double[ids.Length];
        var strategyTotal = new double[ids.Length];
        var strategy = Enumerable.Repeat(1.0 / ids.Length, ids.Length).ToArray();
        for (int iteration = 0; iteration < iterations; iteration += 1)
        {
            double positiveMass = regrets.Where(value => value > 0.0).Sum();
            for (int index = 0; index < strategy.Length; index += 1)
            {
                strategy[index] = positiveMass > 0.0
                    ? Math.Max(regrets[index], 0.0) / positiveMass
                    : 1.0 / strategy.Length;
                strategyTotal[index] += strategy[index];
            }

            var actionValues = new double[ids.Length];
            for (int first = 0; first < ids.Length; first += 1)
            {
                for (int second = 0; second < ids.Length; second += 1)
                {
                    actionValues[first] += strategy[second] * Get(ids[first], ids[second]).Mean;
                }
            }
            double mixedValue = 0.0;
            for (int index = 0; index < ids.Length; index += 1)
            {
                mixedValue += strategy[index] * actionValues[index];
            }
            for (int index = 0; index < ids.Length; index += 1)
            {
                regrets[index] += actionValues[index] - mixedValue;
            }
        }

        double total = strategyTotal.Sum();
        return ids.Select((id, index) => (id, probability: strategyTotal[index] / total))
            .ToDictionary(item => item.id, item => item.probability, StringComparer.Ordinal);
    }

    public string SampleOpponent(
        IReadOnlyDictionary<string, double> strategy,
        DeterministicRandomStream random
    )
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(random);
        if (strategy.Count == 0 || strategy.Any(item => string.IsNullOrWhiteSpace(item.Key)
            || !double.IsFinite(item.Value) || item.Value < 0.0))
        {
            throw new ArgumentException("League strategy is invalid.", nameof(strategy));
        }
        double mass = strategy.Values.Sum();
        if (mass <= 0.0)
        {
            throw new ArgumentException("League strategy has no probability mass.", nameof(strategy));
        }
        double roll = random.NextDouble() * mass;
        foreach ((string id, double weight) in strategy.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            roll -= weight;
            if (roll <= 0.0)
            {
                return id;
            }
        }
        return strategy.Keys.OrderBy(id => id, StringComparer.Ordinal).Last();
    }

    private void AddOne(string first, string second, double payoff)
    {
        if (!_entries.TryGetValue((first, second), out Entry? entry))
        {
            entry = new Entry();
            _entries[(first, second)] = entry;
        }
        entry.Add(payoff);
    }

    private sealed class Entry
    {
        private int _games;
        private double _total;
        private double _wins;
        private double _draws;
        private double _losses;

        public void Add(double payoff)
        {
            _games += 1;
            _total += payoff;
            if (payoff > 0.0)
            {
                _wins += 1.0;
            }
            else if (payoff < 0.0)
            {
                _losses += 1.0;
            }
            else
            {
                _draws += 1.0;
            }
        }

        public LeaguePayoff Snapshot()
        {
            return new LeaguePayoff(
                _games,
                _games == 0 ? 0.0 : _total / _games,
                _wins,
                _draws,
                _losses
            );
        }
    }
}
