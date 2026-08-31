namespace Fw.Rt.Net;

public enum NetCommandStatus
{
    Applied,
    Rejected,
    Expired,
}

public sealed record NetCommand(
    ulong Id,
    uint ClientTick,
    byte[] Payload,
    long CreatedTimestamp,
    int LifetimeMilliseconds
);

public sealed record NetCommandReceipt(
    ulong Id,
    NetCommandStatus Status,
    long AuthorityTick,
    string Reason
);

public sealed class NetCommandJournal
{
    private readonly SortedDictionary<ulong, NetCommand> _pending = [];
    private readonly int _capacity;
    private ulong _nextId = 1;

    public NetCommandJournal(int capacity = 4096)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        _capacity = capacity;
    }

    public int Count => _pending.Count;
    public int Capacity => _capacity;
    public bool IsStalled => _pending.Count >= _capacity;

    public bool TryAppend(
        uint clientTick,
        ReadOnlySpan<byte> payload,
        long timestamp,
        int lifetimeMilliseconds,
        out NetCommand? command
    )
    {
        command = null;
        if (payload.IsEmpty || lifetimeMilliseconds <= 0 || IsStalled)
        {
            return false;
        }

        ulong id = ReserveId();
        command = new NetCommand(
            id,
            clientTick,
            payload.ToArray(),
            timestamp,
            lifetimeMilliseconds
        );
        _pending.Add(id, command);
        return true;
    }

    public bool Complete(NetCommandReceipt receipt)
    {
        return _pending.Remove(receipt.Id);
    }

    public int CompleteThrough(ulong id)
    {
        if (id == 0 || _pending.Count == 0)
        {
            return 0;
        }
        ulong[] completed = _pending.Keys.TakeWhile(candidate => candidate <= id).ToArray();
        foreach (ulong commandId in completed)
        {
            _pending.Remove(commandId);
        }
        return completed.Length;
    }

    public bool TryPeek(out NetCommand? command)
    {
        command = _pending.Count == 0 ? null : _pending.First().Value;
        return command != null;
    }

    public IReadOnlyList<NetCommand> Pending(int maxCommands)
    {
        if (maxCommands <= 0 || _pending.Count == 0)
        {
            return [];
        }
        return _pending.Values.Take(maxCommands).ToArray();
    }

    public void Reset(bool preservePending)
    {
        if (!preservePending)
        {
            _pending.Clear();
            _nextId = 1;
        }
    }

    private ulong ReserveId()
    {
        ulong id = _nextId++;
        if (id == 0 || _nextId == 0)
        {
            throw new InvalidOperationException("Network command id space was exhausted.");
        }
        return id;
    }
}

public sealed class NetCommandLedger
{
    private readonly Dictionary<ulong, NetCommandReceipt> _receipts = [];
    private readonly Queue<ulong> _receiptOrder = [];
    private readonly int _capacity;

    public NetCommandLedger(int capacity = 8192)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        _capacity = capacity;
    }

    public int Count => _receipts.Count;

    public bool TryGet(ulong id, out NetCommandReceipt? receipt)
    {
        return _receipts.TryGetValue(id, out receipt);
    }

    public NetCommandReceipt Commit(
        ulong id,
        NetCommandStatus status,
        long authorityTick,
        string reason = ""
    )
    {
        if (id == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id));
        }
        if (_receipts.TryGetValue(id, out NetCommandReceipt? existing))
        {
            return existing;
        }

        var receipt = new NetCommandReceipt(id, status, authorityTick, reason ?? "");
        _receipts.Add(id, receipt);
        _receiptOrder.Enqueue(id);
        while (_receiptOrder.Count > _capacity)
        {
            _receipts.Remove(_receiptOrder.Dequeue());
        }
        return receipt;
    }

    public void Clear()
    {
        _receipts.Clear();
        _receiptOrder.Clear();
    }
}
