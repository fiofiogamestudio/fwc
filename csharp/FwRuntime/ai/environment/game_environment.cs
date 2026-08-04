namespace Fw.Rt.AI.Environment;

public static class GameActors
{
    public const int Chance = -1;
    public const int Terminal = -2;
    public const int Simultaneous = -3;
}

public enum GameDynamics
{
    Deterministic,
    Stochastic,
}

public enum GameInformation
{
    Perfect,
    Imperfect,
}

public enum GameMoveMode
{
    Sequential,
    Simultaneous,
}

public enum GamePayoffMode
{
    SinglePlayer,
    ZeroSum,
    Cooperative,
    GeneralSum,
}

public enum GameResultStatus
{
    Running,
    Terminated,
    Truncated,
}

public sealed class GameEnvironmentSpec
{
    public GameEnvironmentSpec(
        string id,
        int playerCount,
        GameDynamics dynamics = GameDynamics.Deterministic,
        GameInformation information = GameInformation.Perfect,
        GameMoveMode moveMode = GameMoveMode.Sequential,
        GamePayoffMode payoffMode = GamePayoffMode.SinglePlayer
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (playerCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(playerCount));
        }
        if (payoffMode == GamePayoffMode.SinglePlayer && playerCount != 1)
        {
            throw new ArgumentException("Single-player environments must have exactly one player.", nameof(playerCount));
        }

        Id = id;
        PlayerCount = playerCount;
        Dynamics = dynamics;
        Information = information;
        MoveMode = moveMode;
        PayoffMode = payoffMode;
    }

    public string Id { get; }
    public int PlayerCount { get; }
    public GameDynamics Dynamics { get; }
    public GameInformation Information { get; }
    public GameMoveMode MoveMode { get; }
    public GamePayoffMode PayoffMode { get; }
}

public sealed class GameEpisodeResult
{
    public GameEpisodeResult(
        GameResultStatus status,
        IEnumerable<double> payoffs,
        string outcome = "",
        string reason = ""
    )
    {
        Payoffs = SnapshotFinite(payoffs, nameof(payoffs));
        if (Payoffs.Count == 0)
        {
            throw new ArgumentException("At least one player payoff is required.", nameof(payoffs));
        }

        Status = status;
        Outcome = outcome ?? string.Empty;
        Reason = reason ?? string.Empty;
    }

    public GameResultStatus Status { get; }
    public IReadOnlyList<double> Payoffs { get; }
    public string Outcome { get; }
    public string Reason { get; }
    public bool IsFinished => Status != GameResultStatus.Running;

    public static GameEpisodeResult Running(int playerCount)
    {
        if (playerCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(playerCount));
        }
        return new GameEpisodeResult(GameResultStatus.Running, new double[playerCount]);
    }

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

public sealed class GameTransition<TState>
    where TState : notnull
{
    public GameTransition(
        TState state,
        IEnumerable<double> rewards,
        GameEpisodeResult result
    )
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        ArgumentNullException.ThrowIfNull(rewards);
        var snapshot = rewards.ToArray();
        if (snapshot.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentOutOfRangeException(nameof(rewards), "Rewards must be finite.");
        }

        Rewards = Array.AsReadOnly(snapshot);
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    public TState State { get; }
    public IReadOnlyList<double> Rewards { get; }
    public GameEpisodeResult Result { get; }
}

public sealed class ChanceOutcome<TAction>
    where TAction : notnull
{
    public ChanceOutcome(TAction action, double probability)
    {
        if (!double.IsFinite(probability) || probability <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(probability));
        }
        Action = action ?? throw new ArgumentNullException(nameof(action));
        Probability = probability;
    }

    public TAction Action { get; }
    public double Probability { get; }
}

public interface IGameEnvironment<TState, TObservation, TAction>
    where TState : notnull
    where TObservation : notnull
    where TAction : notnull
{
    GameEnvironmentSpec Spec { get; }
    TState Reset(int seed);
    TState Clone(TState state);
    string StateKey(TState state);
    int CurrentActor(TState state);
    TObservation Observe(TState state, int actor);
    IReadOnlyList<TAction> LegalActions(TState state);
    IReadOnlyList<ChanceOutcome<TAction>> ChanceOutcomes(TState state);
    GameEpisodeResult Result(TState state);
    GameTransition<TState> Step(TState state, TAction action);
}

internal static class GameEnvironmentGuard
{
    public static void ValidateActor(int actor, GameEnvironmentSpec spec)
    {
        if (actor >= 0 && actor < spec.PlayerCount)
        {
            return;
        }
        if (actor is GameActors.Chance or GameActors.Terminal or GameActors.Simultaneous)
        {
            return;
        }
        throw new InvalidOperationException($"Environment returned invalid actor {actor}.");
    }

    public static void ValidateResult(GameEpisodeResult result, GameEnvironmentSpec spec)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Payoffs.Count != spec.PlayerCount)
        {
            throw new InvalidOperationException(
                $"Environment returned {result.Payoffs.Count} payoffs for {spec.PlayerCount} players."
            );
        }
    }

    public static void ValidateTransition<TState>(GameTransition<TState> transition, GameEnvironmentSpec spec)
        where TState : notnull
    {
        ArgumentNullException.ThrowIfNull(transition);
        if (transition.Rewards.Count != spec.PlayerCount)
        {
            throw new InvalidOperationException(
                $"Environment returned {transition.Rewards.Count} rewards for {spec.PlayerCount} players."
            );
        }
        ValidateResult(transition.Result, spec);
    }
}
