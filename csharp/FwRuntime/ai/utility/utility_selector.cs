using Fw.Rt.AI.Core;

namespace Fw.Rt.AI.Utility;

public sealed record UtilityOption<TContext, TChoice>(
    string Id,
    TChoice Choice,
    Func<TContext, double> Score,
    double Weight = 1.0,
    double Noise = 0.0
);

public sealed record UtilityScore(string Id, double Score);

public sealed record UtilityResult<TChoice>(
    bool HasChoice,
    string Id,
    TChoice? Choice,
    double Score,
    IReadOnlyList<UtilityScore> Scores,
    bool Complete = true
);

public sealed class UtilitySelector<TContext, TChoice>
{
    private readonly List<UtilityOption<TContext, TChoice>> _options = [];

    public IReadOnlyList<UtilityOption<TContext, TChoice>> Options => _options;

    public void Add(UtilityOption<TContext, TChoice> option)
    {
        ArgumentNullException.ThrowIfNull(option);
        ArgumentException.ThrowIfNullOrWhiteSpace(option.Id);
        if (!double.IsFinite(option.Weight) || option.Weight < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(option));
        }
        if (!double.IsFinite(option.Noise) || option.Noise < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(option));
        }
        if (_options.Any(item => string.Equals(item.Id, option.Id, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Duplicate utility option id: {option.Id}", nameof(option));
        }
        _options.Add(option);
    }

    public bool Remove(string id)
    {
        var index = _options.FindIndex(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        if (index < 0)
        {
            return false;
        }
        _options.RemoveAt(index);
        return true;
    }

    public UtilityResult<TChoice> Select(
        TContext context,
        DecisionScope scope,
        string currentId = "",
        double switchThreshold = 0.0
    )
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!double.IsFinite(switchThreshold) || switchThreshold < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(switchThreshold));
        }

        var scores = new List<UtilityScore>(_options.Count);
        UtilityOption<TContext, TChoice>? best = null;
        var bestScore = double.NegativeInfinity;
        UtilityOption<TContext, TChoice>? current = null;
        var currentScore = double.NegativeInfinity;

        foreach (var option in _options)
        {
            if (!scope.Budget.TrySpend())
            {
                return new UtilityResult<TChoice>(
                    false,
                    "",
                    default,
                    0.0,
                    scores,
                    false
                );
            }
            var score = option.Score(context) * option.Weight;
            if (option.Noise > 0.0)
            {
                score += (scope.Random.NextDouble() * 2.0 - 1.0) * option.Noise;
            }
            score = double.IsFinite(score) ? score : double.NegativeInfinity;
            scores.Add(new UtilityScore(option.Id, score));
            if (score > bestScore)
            {
                best = option;
                bestScore = score;
            }
            if (string.Equals(option.Id, currentId, StringComparison.Ordinal))
            {
                current = option;
                currentScore = score;
            }
        }

        if (current != null && best != null && best.Id != current.Id
            && bestScore < currentScore + switchThreshold)
        {
            best = current;
            bestScore = currentScore;
        }

        if (best == null)
        {
            return new UtilityResult<TChoice>(false, "", default, 0.0, scores);
        }

        scope.Trace?.Write(
            scope.Tick,
            "utility",
            "selected",
            new Dictionary<string, object?>
            {
                ["id"] = best.Id,
                ["score"] = bestScore,
            }
        );
        return new UtilityResult<TChoice>(true, best.Id, best.Choice, bestScore, scores);
    }
}
