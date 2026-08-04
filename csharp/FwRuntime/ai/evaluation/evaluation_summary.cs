using Fw.Rt.AI.Environment;

namespace Fw.Rt.AI.Evaluation;

public sealed class EvaluationSummary
{
    public EvaluationSummary(
        int episodes,
        int completed,
        int truncated,
        int successes,
        int failures,
        int draws,
        double successRate,
        double successRateLower,
        double successRateUpper,
        double meanPayoff,
        double meanSteps
    )
    {
        Episodes = episodes;
        Completed = completed;
        Truncated = truncated;
        Successes = successes;
        Failures = failures;
        Draws = draws;
        SuccessRate = successRate;
        SuccessRateLower = successRateLower;
        SuccessRateUpper = successRateUpper;
        MeanPayoff = meanPayoff;
        MeanSteps = meanSteps;
    }

    public int Episodes { get; }
    public int Completed { get; }
    public int Truncated { get; }
    public int Successes { get; }
    public int Failures { get; }
    public int Draws { get; }
    public double SuccessRate { get; }
    public double SuccessRateLower { get; }
    public double SuccessRateUpper { get; }
    public double MeanPayoff { get; }
    public double MeanSteps { get; }
}

public sealed class EvaluationAccumulator
{
    private const double WilsonZ = 1.959963984540054;
    private readonly int _actor;
    private readonly double _successThreshold;
    private readonly double _failureThreshold;
    private int _episodes;
    private int _completed;
    private int _truncated;
    private int _successes;
    private int _failures;
    private int _draws;
    private double _payoffTotal;
    private long _stepTotal;

    public EvaluationAccumulator(
        int actor = 0,
        double successThreshold = 0.5,
        double failureThreshold = -0.5
    )
    {
        if (actor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(actor));
        }
        if (!double.IsFinite(successThreshold) || !double.IsFinite(failureThreshold)
            || failureThreshold >= successThreshold)
        {
            throw new ArgumentException("Evaluation thresholds must be finite and ordered.");
        }
        _actor = actor;
        _successThreshold = successThreshold;
        _failureThreshold = failureThreshold;
    }

    public void Add(GameEpisodeResult result, int steps = 0)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.IsFinished)
        {
            throw new ArgumentException("Only terminal or truncated episodes can be evaluated.", nameof(result));
        }
        if (_actor >= result.Payoffs.Count)
        {
            throw new ArgumentException("Evaluation actor is outside the episode payoff range.", nameof(result));
        }
        if (steps < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(steps));
        }

        _episodes += 1;
        _stepTotal += steps;
        _payoffTotal += result.Payoffs[_actor];
        if (result.Status == GameResultStatus.Truncated)
        {
            _truncated += 1;
            return;
        }

        _completed += 1;
        var payoff = result.Payoffs[_actor];
        if (payoff >= _successThreshold)
        {
            _successes += 1;
        }
        else if (payoff <= _failureThreshold)
        {
            _failures += 1;
        }
        else
        {
            _draws += 1;
        }
    }

    public EvaluationSummary Snapshot()
    {
        var successRate = _completed == 0 ? 0.0 : (double)_successes / _completed;
        var (lower, upper) = WilsonInterval(_successes, _completed);
        return new EvaluationSummary(
            _episodes,
            _completed,
            _truncated,
            _successes,
            _failures,
            _draws,
            successRate,
            lower,
            upper,
            _episodes == 0 ? 0.0 : _payoffTotal / _episodes,
            _episodes == 0 ? 0.0 : (double)_stepTotal / _episodes
        );
    }

    private static (double Lower, double Upper) WilsonInterval(int successes, int samples)
    {
        if (samples == 0)
        {
            return (0.0, 1.0);
        }
        var ratio = (double)successes / samples;
        var zSquared = WilsonZ * WilsonZ;
        var denominator = 1.0 + zSquared / samples;
        var center = (ratio + zSquared / (2.0 * samples)) / denominator;
        var margin = WilsonZ * Math.Sqrt(
            (ratio * (1.0 - ratio) + zSquared / (4.0 * samples)) / samples
        ) / denominator;
        return (Math.Max(0.0, center - margin), Math.Min(1.0, center + margin));
    }
}
