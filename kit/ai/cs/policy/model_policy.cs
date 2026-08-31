namespace Fw.Rt.AI.Policy;

public enum PolicyStatus
{
    Success,
    Rejected,
    Failed,
}

public sealed record PolicyResult<TOutput>(
    PolicyStatus Status,
    TOutput? Output,
    string Error = ""
)
{
    public static PolicyResult<TOutput> Success(TOutput output) => new(PolicyStatus.Success, output);
    public static PolicyResult<TOutput> Rejected(string error) => new(PolicyStatus.Rejected, default, error);
    public static PolicyResult<TOutput> Failed(string error) => new(PolicyStatus.Failed, default, error);
}

public interface IModelPolicy<TInput, TOutput>
{
    ValueTask<PolicyResult<TOutput>> EvaluateAsync(
        TInput input,
        CancellationToken cancellationToken = default
    );
}

public sealed class LocalPolicy<TInput, TOutput> : IModelPolicy<TInput, TOutput>
{
    private readonly Func<TInput, TOutput> _evaluate;

    public LocalPolicy(Func<TInput, TOutput> evaluate)
    {
        _evaluate = evaluate ?? throw new ArgumentNullException(nameof(evaluate));
    }

    public ValueTask<PolicyResult<TOutput>> EvaluateAsync(
        TInput input,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return ValueTask.FromResult(PolicyResult<TOutput>.Success(_evaluate(input)));
        }
        catch (Exception error)
        {
            return ValueTask.FromResult(PolicyResult<TOutput>.Failed(error.Message));
        }
    }
}

public sealed class RemotePolicy<TInput, TOutput> : IModelPolicy<TInput, TOutput>
{
    private readonly Func<TInput, CancellationToken, ValueTask<PolicyResult<TOutput>>> _evaluate;

    public RemotePolicy(Func<TInput, CancellationToken, ValueTask<PolicyResult<TOutput>>> evaluate)
    {
        _evaluate = evaluate ?? throw new ArgumentNullException(nameof(evaluate));
    }

    public ValueTask<PolicyResult<TOutput>> EvaluateAsync(
        TInput input,
        CancellationToken cancellationToken = default
    )
    {
        return _evaluate(input, cancellationToken);
    }
}

public sealed class FallbackPolicy<TInput, TOutput> : IModelPolicy<TInput, TOutput>
{
    private readonly IModelPolicy<TInput, TOutput> _primary;
    private readonly IModelPolicy<TInput, TOutput> _fallback;
    private readonly Predicate<TOutput>? _validate;

    public FallbackPolicy(
        IModelPolicy<TInput, TOutput> primary,
        IModelPolicy<TInput, TOutput> fallback,
        Predicate<TOutput>? validate = null
    )
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _validate = validate;
    }

    public async ValueTask<PolicyResult<TOutput>> EvaluateAsync(
        TInput input,
        CancellationToken cancellationToken = default
    )
    {
        PolicyResult<TOutput> result;
        try
        {
            result = await _primary.EvaluateAsync(input, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            result = PolicyResult<TOutput>.Failed(error.Message);
        }

        if (result.Status == PolicyStatus.Success && result.Output is TOutput output
            && (_validate == null || _validate(output)))
        {
            return result;
        }
        return await _fallback.EvaluateAsync(input, cancellationToken).ConfigureAwait(false);
    }
}
