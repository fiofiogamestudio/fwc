namespace Fw.Rt.AI.Environment;

public sealed class ParallelActorView<TObservation, TAction>
    where TObservation : notnull
    where TAction : notnull
{
    public ParallelActorView(
        int actor,
        TObservation observation,
        IEnumerable<TAction> legalActions
    )
    {
        if (actor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(actor));
        }
        Observation = observation ?? throw new ArgumentNullException(nameof(observation));
        var actions = (legalActions ?? throw new ArgumentNullException(nameof(legalActions))).ToArray();
        if (actions.Length == 0 || actions.Distinct().Count() != actions.Length)
        {
            throw new ArgumentException("Legal actions must be non-empty and unique.", nameof(legalActions));
        }

        Actor = actor;
        LegalActions = Array.AsReadOnly(actions);
    }

    public int Actor { get; }
    public TObservation Observation { get; }
    public IReadOnlyList<TAction> LegalActions { get; }
}

public sealed class ParallelGameTransition<TState>
    where TState : notnull
{
    public ParallelGameTransition(
        TState state,
        IEnumerable<double> rewards,
        GameEpisodeResult result
    )
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        Result = result ?? throw new ArgumentNullException(nameof(result));
        var values = (rewards ?? throw new ArgumentNullException(nameof(rewards))).ToArray();
        if (values.Length == 0 || values.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentOutOfRangeException(nameof(rewards), "At least one finite reward is required.");
        }
        if (values.Length != Result.Payoffs.Count)
        {
            throw new ArgumentException("Reward and payoff player counts must match.", nameof(rewards));
        }
        Rewards = Array.AsReadOnly(values);
    }

    public TState State { get; }
    public IReadOnlyList<double> Rewards { get; }
    public GameEpisodeResult Result { get; }
}

public interface IParallelGameEnvironment<TState, TObservation, TAction>
    where TState : notnull
    where TObservation : notnull
    where TAction : notnull
{
    GameEnvironmentSpec Spec { get; }
    TState Reset(int seed);
    string StateKey(TState state);
    IReadOnlyList<int> ActiveActors(TState state);
    ParallelActorView<TObservation, TAction> Observe(TState state, int actor);
    GameEpisodeResult Result(TState state);
    ParallelGameTransition<TState> Step(
        TState state,
        IReadOnlyDictionary<int, TAction> actions
    );
}

public static class ParallelEnvironmentGuard
{
    public static void ValidateActors(
        IReadOnlyList<int> actors,
        GameEnvironmentSpec spec
    )
    {
        ArgumentNullException.ThrowIfNull(actors);
        ArgumentNullException.ThrowIfNull(spec);
        if (actors.Count == 0 || actors.Distinct().Count() != actors.Count)
        {
            throw new InvalidOperationException("Parallel environments require unique active actors.");
        }
        foreach (int actor in actors)
        {
            if (actor < 0 || actor >= spec.PlayerCount)
            {
                throw new InvalidOperationException($"Parallel environment returned invalid actor {actor}.");
            }
        }
    }

    public static void ValidateActions<TAction>(
        IReadOnlyList<int> actors,
        IReadOnlyDictionary<int, TAction> actions
    )
        where TAction : notnull
    {
        ArgumentNullException.ThrowIfNull(actors);
        ArgumentNullException.ThrowIfNull(actions);
        if (actions.Count != actors.Count || actors.Any(actor => !actions.ContainsKey(actor)))
        {
            throw new InvalidOperationException("Parallel actions must contain exactly one action per active actor.");
        }
    }
}
