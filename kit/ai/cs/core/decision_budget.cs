namespace Fw.Rt.AI.Core;

public sealed class DecisionBudget
{
    public DecisionBudget(int limit)
    {
        if (limit < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
        Limit = limit;
    }

    public int Limit { get; }
    public int Used { get; private set; }
    public int Remaining => Limit - Used;
    public bool IsExhausted => Used >= Limit;

    public bool TrySpend(int amount = 1)
    {
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }
        if (amount > Remaining)
        {
            return false;
        }
        Used += amount;
        return true;
    }

    public void Reset()
    {
        Used = 0;
    }
}
