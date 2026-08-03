using Fw.Rt.Randomness;

namespace Fw.Rt.AI.Core;

public sealed class DecisionScope
{
    public DecisionScope(
        int tick,
        DecisionBudget budget,
        DeterministicRandomStream random,
        IDecisionTrace? trace = null
    )
    {
        Budget = budget ?? throw new ArgumentNullException(nameof(budget));
        Random = random ?? throw new ArgumentNullException(nameof(random));
        Tick = tick;
        Trace = trace;
    }

    public int Tick { get; }
    public DecisionBudget Budget { get; }
    public DeterministicRandomStream Random { get; }
    public IDecisionTrace? Trace { get; }
}
