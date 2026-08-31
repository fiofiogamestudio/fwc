using System.Collections.ObjectModel;
using System.Diagnostics;

namespace Fw.Rt.Systems;

public interface ISystem<TContext>
    where TContext : class
{
    void Init(TContext context);
    void Tick(float dt);
    void Shutdown();
}

public enum SystemRuntimeState
{
    Created,
    Initializing,
    Running,
    Faulted,
    Stopping,
    Stopped,
}

public enum SystemRemoveMode
{
    DenyIfReferenced,
    Cascade,
}

public sealed record SystemSnapshot(
    string Scope,
    string Id,
    string Phase,
    bool Initialized,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Dependents
);

public sealed record SystemTimingSnapshot(
    string Scope,
    string Id,
    string Phase,
    int SampleCount,
    double LastMilliseconds,
    double AverageMilliseconds,
    double P95Milliseconds,
    double MaxMilliseconds,
    long LastAllocatedBytes,
    double AverageAllocatedBytes,
    long MaxAllocatedBytes
);

public sealed class SystemRuntime
{
    private readonly SystemRuntime? _parent;
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<string, Entry> _entriesById = new(StringComparer.Ordinal);
    private readonly List<Entry> _initializedEntries = [];
    private readonly List<string> _phaseOrder = [];
    private readonly List<Entry> _tickEntries = [];
    private List<Entry>? _orderedEntries;

    public SystemRuntime()
        : this(null)
    {
    }

    public SystemRuntime(SystemRuntime? parent)
    {
        _parent = parent;
    }

    public event Action<string>? SystemAdded;
    public event Action<string>? SystemRemoved;

    public IReadOnlyList<string> PhaseOrder => new ReadOnlyCollection<string>(_phaseOrder);
    public SystemRuntimeState State { get; private set; } = SystemRuntimeState.Created;
    public bool IsRunning => State == SystemRuntimeState.Running;
    public bool IsStarted => IsRunning;
    public SystemRuntime? Parent => _parent;

    public void SetPhaseOrder(IEnumerable<string> order)
    {
        EnsureConfigurable();
        ArgumentNullException.ThrowIfNull(order);
        _phaseOrder.Clear();
        foreach (var rawPhase in order)
        {
            var phase = rawPhase?.Trim() ?? string.Empty;
            if (phase.Length == 0 || _phaseOrder.Contains(phase, StringComparer.Ordinal))
            {
                continue;
            }
            _phaseOrder.Add(phase);
        }
        InvalidateOrder();
    }

    public void Add<TContext>(string id, ISystem<TContext> system, TContext context, string phase = "")
        where TContext : class
    {
        AddCore(id, system, context, phase, null);
    }

    public void AddWithDependencies<TContext>(
        string id,
        ISystem<TContext> system,
        TContext context,
        string phase,
        IEnumerable<string> dependencies
    )
        where TContext : class
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        AddCore(id, system, context, phase, dependencies);
    }

    public void SetDependencies(string id, IEnumerable<string> dependencies)
    {
        EnsureConfigurable();
        ArgumentNullException.ThrowIfNull(dependencies);
        if (!_entriesById.TryGetValue(id, out var entry))
        {
            throw new KeyNotFoundException($"Missing local system: {id}");
        }
        entry.Dependencies.Clear();
        entry.Dependencies.AddRange(NormalizeDependencies(id, dependencies));
        InvalidateOrder();
    }

    public bool HasLocal(string id)
    {
        return _entriesById.ContainsKey(id);
    }

    public bool Has(string id)
    {
        return HasLocal(id) || (_parent?.Has(id) ?? false);
    }

    public bool IsInitialized(string id)
    {
        if (_entriesById.TryGetValue(id, out var entry))
        {
            return entry.Initialized;
        }
        return _parent?.IsInitialized(id) ?? false;
    }

    public TContext? GetContext<TContext>(string id)
        where TContext : class
    {
        if (_entriesById.TryGetValue(id, out var entry))
        {
            return entry.Context as TContext;
        }
        return _parent?.GetContext<TContext>(id);
    }

    public bool TryGetContext<TContext>(string id, out TContext? context)
        where TContext : class
    {
        context = GetContext<TContext>(id);
        return context != null;
    }

    public bool Remove(string id, SystemRemoveMode mode = SystemRemoveMode.DenyIfReferenced)
    {
        if (State is SystemRuntimeState.Initializing or SystemRuntimeState.Stopping or SystemRuntimeState.Stopped)
        {
            throw new InvalidOperationException($"System runtime cannot remove systems from state {State}.");
        }
        if (!_entriesById.TryGetValue(id, out var entry))
        {
            return false;
        }

        var dependents = GetLocalDependents(id);
        if (mode == SystemRemoveMode.DenyIfReferenced && dependents.Count > 0)
        {
            throw new InvalidOperationException(
                $"Cannot remove system '{id}'; dependents still exist: {string.Join(", ", dependents)}"
            );
        }

        var removalOrder = new List<Entry>();
        CollectRemovalOrder(entry, new HashSet<string>(StringComparer.Ordinal), removalOrder);
        List<Exception>? errors = null;
        foreach (var removeEntry in removalOrder)
        {
            try
            {
                ShutdownEntry(removeEntry);
            }
            catch (Exception exception)
            {
                (errors ??= []).Add(exception);
            }
            _entries.Remove(removeEntry);
            _entriesById.Remove(removeEntry.Id);
            _initializedEntries.Remove(removeEntry);
            SystemRemoved?.Invoke(removeEntry.Id);
        }
        InvalidateOrder();
        if (errors is { Count: > 0 })
        {
            throw new AggregateException("One or more systems failed during removal.", errors);
        }
        return true;
    }

    public IReadOnlyList<SystemSnapshot> GetSnapshots(bool includeParent = true)
    {
        var result = new List<SystemSnapshot>();
        if (includeParent && _parent != null)
        {
            foreach (var snapshot in _parent.GetSnapshots(true))
            {
                result.Add(snapshot with { Scope = $"parent/{snapshot.Scope}" });
            }
        }
        foreach (var entry in OrderedEntries())
        {
            result.Add(new SystemSnapshot(
                "local",
                entry.Id,
                entry.Phase,
                entry.Initialized,
                entry.Dependencies.AsReadOnly(),
                GetLocalDependents(entry.Id).AsReadOnly()
            ));
        }
        return result.AsReadOnly();
    }

    public IReadOnlyList<SystemTimingSnapshot> GetTimingSnapshots(bool includeParent = true)
    {
        var result = new List<SystemTimingSnapshot>();
        if (includeParent && _parent != null)
        {
            foreach (var snapshot in _parent.GetTimingSnapshots(true))
            {
                result.Add(snapshot with { Scope = $"parent/{snapshot.Scope}" });
            }
        }
        foreach (var entry in OrderedEntries())
        {
            result.Add(entry.TimingSnapshot("local"));
        }
        return result.AsReadOnly();
    }

    public void InitAll()
    {
        if (State != SystemRuntimeState.Created)
        {
            throw new InvalidOperationException($"System runtime cannot initialize from state {State}.");
        }

        State = SystemRuntimeState.Initializing;
        try
        {
            foreach (var entry in OrderedEntries())
            {
                EnsureDependenciesInitialized(entry);
                entry.Initialized = true;
                _initializedEntries.Add(entry);
                entry.Init();
            }
            State = SystemRuntimeState.Running;
        }
        catch (Exception initError)
        {
            State = SystemRuntimeState.Faulted;
            var errors = new List<Exception> { initError };
            ShutdownInitialized(errors);
            ClearRegistration();
            State = SystemRuntimeState.Stopped;
            throw new AggregateException("System runtime initialization failed and was rolled back.", errors);
        }
    }

    public void Tick(float dt)
    {
        if (State != SystemRuntimeState.Running)
        {
            throw new InvalidOperationException($"System runtime cannot tick from state {State}.");
        }

        try
        {
            _tickEntries.Clear();
            _tickEntries.AddRange(OrderedEntries());
            foreach (var entry in _tickEntries)
            {
                if (_entriesById.ContainsKey(entry.Id) && entry.Initialized)
                {
                    long started = Stopwatch.GetTimestamp();
                    long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                    try
                    {
                        entry.Tick(dt);
                    }
                    finally
                    {
                        entry.RecordTick(
                            (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency,
                            Math.Max(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore, 0L)
                        );
                    }
                }
            }
        }
        catch
        {
            State = SystemRuntimeState.Faulted;
            throw;
        }
    }

    public void ShutdownAll()
    {
        if (State is SystemRuntimeState.Stopped or SystemRuntimeState.Stopping)
        {
            return;
        }

        State = SystemRuntimeState.Stopping;
        var errors = new List<Exception>();
        ShutdownInitialized(errors);
        ClearRegistration();
        State = SystemRuntimeState.Stopped;
        if (errors.Count > 0)
        {
            throw new AggregateException("One or more systems failed to shut down.", errors);
        }
    }

    private void AddCore<TContext>(
        string id,
        ISystem<TContext> system,
        TContext context,
        string phase,
        IEnumerable<string>? dependencies
    )
        where TContext : class
    {
        EnsureConfigurable();
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("System id cannot be empty.", nameof(id));
        }
        if (Has(id))
        {
            throw new InvalidOperationException($"Duplicate system id in runtime scope: {id}");
        }

        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(context);
        var entry = new Entry(
            id,
            phase?.Trim() ?? string.Empty,
            context,
            () => system.Init(context),
            system.Tick,
            system.Shutdown,
            NormalizeDependencies(id, dependencies)
        );
        _entries.Add(entry);
        _entriesById[id] = entry;
        InvalidateOrder();
        SystemAdded?.Invoke(id);
    }

    private void EnsureConfigurable()
    {
        if (State != SystemRuntimeState.Created)
        {
            throw new InvalidOperationException($"System runtime cannot be configured from state {State}.");
        }
    }

    private void ShutdownInitialized(List<Exception> errors)
    {
        for (var index = _initializedEntries.Count - 1; index >= 0; index--)
        {
            try
            {
                ShutdownEntry(_initializedEntries[index]);
            }
            catch (Exception shutdownError)
            {
                errors.Add(shutdownError);
            }
        }
        _initializedEntries.Clear();
    }

    private void ClearRegistration()
    {
        _entries.Clear();
        _entriesById.Clear();
        _initializedEntries.Clear();
        _phaseOrder.Clear();
        _tickEntries.Clear();
        _orderedEntries = null;
    }

    private static void ShutdownEntry(Entry entry)
    {
        if (!entry.Initialized)
        {
            return;
        }
        entry.Initialized = false;
        entry.Shutdown();
    }

    private void EnsureDependenciesInitialized(Entry entry)
    {
        foreach (var dependencyId in entry.Dependencies)
        {
            if (_entriesById.TryGetValue(dependencyId, out var dependency))
            {
                if (!dependency.Initialized)
                {
                    throw new InvalidOperationException(
                        $"System '{entry.Id}' requires '{dependencyId}' to be initialized first."
                    );
                }
                continue;
            }
            if (!(_parent?.Has(dependencyId) ?? false))
            {
                throw new InvalidOperationException(
                    $"System '{entry.Id}' references missing dependency '{dependencyId}'."
                );
            }
            if (!(_parent?.IsInitialized(dependencyId) ?? false))
            {
                throw new InvalidOperationException(
                    $"System '{entry.Id}' requires parent system '{dependencyId}' to be initialized first."
                );
            }
        }
    }

    private IReadOnlyList<Entry> OrderedEntries()
    {
        if (_orderedEntries != null)
        {
            return _orderedEntries;
        }

        var baseOrder = _entries
            .Select((entry, index) => new { Entry = entry, Index = index })
            .OrderBy(item => PhaseRank(item.Entry.Phase))
            .ThenBy(item => item.Index)
            .Select(item => item.Entry)
            .ToArray();
        var ordered = new List<Entry>(_entries.Count);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in baseOrder)
        {
            Visit(entry, visited, visiting, ordered);
        }
        _orderedEntries = ordered;
        return _orderedEntries;
    }

    private void Visit(
        Entry entry,
        HashSet<string> visited,
        HashSet<string> visiting,
        List<Entry> ordered
    )
    {
        if (visited.Contains(entry.Id))
        {
            return;
        }
        if (!visiting.Add(entry.Id))
        {
            throw new InvalidOperationException($"System dependency cycle detected at '{entry.Id}'.");
        }
        foreach (var dependencyId in entry.Dependencies)
        {
            if (!_entriesById.TryGetValue(dependencyId, out var dependency))
            {
                if (!(_parent?.Has(dependencyId) ?? false))
                {
                    throw new InvalidOperationException(
                        $"System '{entry.Id}' references missing dependency '{dependencyId}'."
                    );
                }
                continue;
            }
            if (PhaseRank(dependency.Phase) > PhaseRank(entry.Phase))
            {
                throw new InvalidOperationException(
                    $"System '{entry.Id}' in phase '{entry.Phase}' depends on later phase system " +
                    $"'{dependency.Id}' in phase '{dependency.Phase}'."
                );
            }
            Visit(dependency, visited, visiting, ordered);
        }
        visiting.Remove(entry.Id);
        visited.Add(entry.Id);
        ordered.Add(entry);
    }

    private int PhaseRank(string phase)
    {
        var index = _phaseOrder.FindIndex(item => string.Equals(item, phase, StringComparison.Ordinal));
        return index >= 0 ? index : _phaseOrder.Count;
    }

    private List<string> GetLocalDependents(string id)
    {
        return _entries
            .Where(entry => entry.Dependencies.Contains(id, StringComparer.Ordinal))
            .Select(entry => entry.Id)
            .ToList();
    }

    private void CollectRemovalOrder(Entry entry, HashSet<string> visited, List<Entry> result)
    {
        if (!visited.Add(entry.Id))
        {
            return;
        }
        foreach (var dependentId in GetLocalDependents(entry.Id))
        {
            CollectRemovalOrder(_entriesById[dependentId], visited, result);
        }
        result.Add(entry);
    }

    private static List<string> NormalizeDependencies(string id, IEnumerable<string>? dependencies)
    {
        if (dependencies == null)
        {
            return [];
        }
        var result = new List<string>();
        foreach (var rawDependency in dependencies)
        {
            var dependency = rawDependency?.Trim() ?? string.Empty;
            if (dependency.Length == 0 || result.Contains(dependency, StringComparer.Ordinal))
            {
                continue;
            }
            if (string.Equals(id, dependency, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"System '{id}' cannot depend on itself.");
            }
            result.Add(dependency);
        }
        return result;
    }

    private void InvalidateOrder()
    {
        _orderedEntries = null;
    }

    private sealed class Entry(
        string id,
        string phase,
        object context,
        Action init,
        Action<float> tick,
        Action shutdown,
        List<string> dependencies
    )
    {
        private const int TimingCapacity = 160;
        private readonly double[] _tickMilliseconds = new double[TimingCapacity];
        private readonly long[] _allocatedBytes = new long[TimingCapacity];
        private int _timingCount;
        private int _timingIndex;

        public string Id { get; } = id;
        public string Phase { get; } = phase;
        public object Context { get; } = context;
        public Action Init { get; } = init;
        public Action<float> Tick { get; } = tick;
        public Action Shutdown { get; } = shutdown;
        public List<string> Dependencies { get; } = dependencies;
        public bool Initialized { get; set; }

        public void RecordTick(double milliseconds, long allocatedBytes)
        {
            _tickMilliseconds[_timingIndex] = Math.Max(milliseconds, 0.0);
            _allocatedBytes[_timingIndex] = Math.Max(allocatedBytes, 0L);
            _timingIndex = (_timingIndex + 1) % TimingCapacity;
            _timingCount = Math.Min(_timingCount + 1, TimingCapacity);
        }

        public SystemTimingSnapshot TimingSnapshot(string scope)
        {
            if (_timingCount == 0)
            {
                return new SystemTimingSnapshot(
                    scope,
                    Id,
                    Phase,
                    0,
                    -1.0,
                    -1.0,
                    -1.0,
                    -1.0,
                    0L,
                    0.0,
                    0L
                );
            }

            var ordered = new double[_timingCount];
            double totalMilliseconds = 0.0;
            long totalAllocatedBytes = 0L;
            double maxMilliseconds = 0.0;
            long maxAllocatedBytes = 0L;
            for (int index = 0; index < _timingCount; index += 1)
            {
                double milliseconds = _tickMilliseconds[index];
                long allocatedBytes = _allocatedBytes[index];
                ordered[index] = milliseconds;
                totalMilliseconds += milliseconds;
                totalAllocatedBytes += allocatedBytes;
                maxMilliseconds = Math.Max(maxMilliseconds, milliseconds);
                maxAllocatedBytes = Math.Max(maxAllocatedBytes, allocatedBytes);
            }
            Array.Sort(ordered);
            int lastIndex = (_timingIndex - 1 + TimingCapacity) % TimingCapacity;
            int p95Index = Math.Clamp((int)Math.Ceiling(_timingCount * 0.95) - 1, 0, _timingCount - 1);
            return new SystemTimingSnapshot(
                scope,
                Id,
                Phase,
                _timingCount,
                _tickMilliseconds[lastIndex],
                totalMilliseconds / _timingCount,
                ordered[p95Index],
                maxMilliseconds,
                _allocatedBytes[lastIndex],
                totalAllocatedBytes / (double)_timingCount,
                maxAllocatedBytes
            );
        }
    }
}
