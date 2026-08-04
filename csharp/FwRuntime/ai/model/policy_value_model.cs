namespace Fw.Rt.AI.Model;

public sealed class ActionPrior<TAction>
    where TAction : notnull
{
    public ActionPrior(TAction action, double prior)
    {
        if (!double.IsFinite(prior) || prior < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(prior));
        }
        Action = action ?? throw new ArgumentNullException(nameof(action));
        Prior = prior;
    }

    public TAction Action { get; }
    public double Prior { get; }
}

public sealed class PolicyValuePrediction<TAction>
    where TAction : notnull
{
    public PolicyValuePrediction(
        IEnumerable<ActionPrior<TAction>> priors,
        IEnumerable<double> values
    )
    {
        ArgumentNullException.ThrowIfNull(priors);
        ArgumentNullException.ThrowIfNull(values);
        var priorSnapshot = priors.ToArray();
        var valueSnapshot = values.ToArray();
        if (valueSnapshot.Length == 0 || valueSnapshot.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentOutOfRangeException(nameof(values), "At least one finite value is required.");
        }

        Priors = Array.AsReadOnly(priorSnapshot);
        Values = Array.AsReadOnly(valueSnapshot);
    }

    public IReadOnlyList<ActionPrior<TAction>> Priors { get; }
    public IReadOnlyList<double> Values { get; }
}

public interface IPolicyValueModel<TObservation, TAction>
    where TObservation : notnull
    where TAction : notnull
{
    PolicyValuePrediction<TAction> Predict(
        TObservation observation,
        int actor,
        IReadOnlyList<TAction> legalActions
    );
}

public sealed class DelegatePolicyValueModel<TObservation, TAction>
    : IPolicyValueModel<TObservation, TAction>
    where TObservation : notnull
    where TAction : notnull
{
    private readonly Func<TObservation, int, IReadOnlyList<TAction>, PolicyValuePrediction<TAction>> _predict;

    public DelegatePolicyValueModel(
        Func<TObservation, int, IReadOnlyList<TAction>, PolicyValuePrediction<TAction>> predict
    )
    {
        _predict = predict ?? throw new ArgumentNullException(nameof(predict));
    }

    public PolicyValuePrediction<TAction> Predict(
        TObservation observation,
        int actor,
        IReadOnlyList<TAction> legalActions
    )
    {
        return _predict(observation, actor, legalActions);
    }
}

public sealed class UniformPolicyValueModel<TObservation, TAction>
    : IPolicyValueModel<TObservation, TAction>
    where TObservation : notnull
    where TAction : notnull
{
    private readonly int _playerCount;

    public UniformPolicyValueModel(int playerCount)
    {
        if (playerCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(playerCount));
        }
        _playerCount = playerCount;
    }

    public PolicyValuePrediction<TAction> Predict(
        TObservation observation,
        int actor,
        IReadOnlyList<TAction> legalActions
    )
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(legalActions);
        var prior = legalActions.Count == 0 ? 0.0 : 1.0 / legalActions.Count;
        return new PolicyValuePrediction<TAction>(
            legalActions.Select(action => new ActionPrior<TAction>(action, prior)),
            new double[_playerCount]
        );
    }
}
