using System.Collections;
using System.Globalization;
using System.Text.Json;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Debugging;
using MoonSharp.Interpreter.Execution.VM;
using LuaException = MoonSharp.Interpreter.ScriptRuntimeException;
using LuaScript = MoonSharp.Interpreter.Script;

namespace Fw.Rt.Script;

public sealed class ScriptRuntime : IDisposable
{
    private const int MaxNameLength = 128;
    private const CoreModules SafeModules =
        CoreModules.Basic |
        CoreModules.GlobalConsts |
        CoreModules.TableIterators |
        CoreModules.String |
        CoreModules.Table |
        CoreModules.ErrorHandling |
        CoreModules.Math |
        CoreModules.Bit32;

    private readonly Dictionary<string, Module> _modules = new(StringComparer.Ordinal);
    private readonly ScriptRuntimeOptions _options;
    private bool _disposed;

    public ScriptRuntime(ScriptRuntimeOptions? options = null)
    {
        _options = (options ?? new ScriptRuntimeOptions()).Validate();
    }

    public IReadOnlyCollection<string> Modules => _modules.Keys.Order(StringComparer.Ordinal).ToArray();

    public void Load(string id, string source)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Script module id cannot be empty.", nameof(id));
        }
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Script module source cannot be empty.", nameof(source));
        }
        string normalizedId = id.Trim();
        if (normalizedId.Length > MaxNameLength)
        {
            throw new ArgumentException(
                $"Script module id cannot exceed {MaxNameLength} characters.",
                nameof(id)
            );
        }
        if (source.Length > _options.MaxSourceLength)
        {
            throw new ScriptRuntimeException(
                $"Script module '{normalizedId}' exceeds the source length limit."
            );
        }
        if (_modules.ContainsKey(normalizedId))
        {
            throw new InvalidOperationException($"Duplicate script module: {normalizedId}");
        }

        var limiter = new InstructionLimiter(normalizedId);
        var script = new LuaScript(SafeModules);
        script.Options.DebugPrint = _ => { };
        script.Options.CheckThreadAccess = true;
        RemoveUnsafeGlobals(script);
        script.AttachDebugger(limiter);
        limiter.Reset(_options.LoadInstructionLimit);
        try
        {
            DynValue result = script.DoString(source, codeFriendlyName: normalizedId);
            if (result.Type != DataType.Table)
            {
                throw new ScriptRuntimeException($"Script module '{normalizedId}' must return a table.");
            }
            _modules.Add(normalizedId, new Module(script, result.Table, limiter));
        }
        catch (ScriptBudgetException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Wrap(normalizedId, "load", exception);
        }
    }

    public bool HasFunction(string module, string function)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(module) || string.IsNullOrWhiteSpace(function))
        {
            return false;
        }
        string normalizedModule = module.Trim();
        string normalizedFunction = function.Trim();
        if (normalizedModule.Length > MaxNameLength || normalizedFunction.Length > MaxNameLength)
        {
            return false;
        }
        if (!_modules.TryGetValue(normalizedModule, out var loaded))
        {
            return false;
        }

        lock (loaded.Gate)
        {
            DynValue value = loaded.Exports.Get(normalizedFunction);
            return value.Type is DataType.Function or DataType.ClrFunction;
        }
    }

    public void RequireFunction(string module, string function)
    {
        if (!HasFunction(module, function))
        {
            throw new ScriptRuntimeException(
                $"Script module '{module}' has no function '{function}'."
            );
        }
    }

    public ScriptCallResult Call(
        string module,
        string function,
        object? input = null,
        ScriptHost? host = null
    )
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(module))
        {
            throw new ArgumentException("Script module cannot be empty.", nameof(module));
        }
        string normalizedModule = module.Trim();
        if (normalizedModule.Length > MaxNameLength)
        {
            throw new ArgumentException(
                $"Script module name cannot exceed {MaxNameLength} characters.",
                nameof(module)
            );
        }
        if (!_modules.TryGetValue(normalizedModule, out var loaded))
        {
            throw new KeyNotFoundException($"Missing script module: {normalizedModule}");
        }
        if (string.IsNullOrWhiteSpace(function))
        {
            throw new ArgumentException("Script function cannot be empty.", nameof(function));
        }

        string normalizedFunction = function.Trim();
        if (normalizedFunction.Length > MaxNameLength)
        {
            throw new ArgumentException(
                $"Script function name cannot exceed {MaxNameLength} characters.",
                nameof(function)
            );
        }
        lock (loaded.Gate)
        {
            DynValue callable = loaded.Exports.Get(normalizedFunction);
            if (callable.Type is not (DataType.Function or DataType.ClrFunction))
            {
                throw new ScriptRuntimeException(
                    $"Script module '{normalizedModule}' has no function '{normalizedFunction}'."
                );
            }

            var commands = new List<ScriptCommand>();
            ValueBudget callBudget = CreateValueBudget();
            Table api = BuildApi(loaded.Script, normalizedModule, host, commands, callBudget);
            loaded.Limiter.Reset(_options.CallInstructionLimit);
            try
            {
                DynValue result = loaded.Script.Call(
                    callable,
                    ToDynValue(loaded.Script, input, 0, callBudget),
                    DynValue.NewTable(api)
                );
                object? value = FromDynValue(result, 0, callBudget);
                return new ScriptCallResult(value, commands.AsReadOnly(), loaded.Limiter.Instructions);
            }
            catch (ScriptBudgetException)
            {
                commands.Clear();
                throw;
            }
            catch (Exception exception)
            {
                commands.Clear();
                throw Wrap(normalizedModule, normalizedFunction, exception);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _modules.Clear();
        _disposed = true;
    }

    private static void RemoveUnsafeGlobals(LuaScript script)
    {
        foreach (string name in new[]
        {
            "collectgarbage",
            "debug",
            "dofile",
            "io",
            "load",
            "loadfile",
            "os",
            "package",
            "require",
        })
        {
            script.Globals.Set(name, DynValue.Nil);
        }

        DynValue math = script.Globals.Get("math");
        if (math.Type == DataType.Table)
        {
            math.Table.Set("random", DynValue.Nil);
            math.Table.Set("randomseed", DynValue.Nil);
        }
    }

    private Table BuildApi(
        LuaScript script,
        string module,
        ScriptHost? host,
        List<ScriptCommand> commands,
        ValueBudget callBudget
    )
    {
        var api = new Table(script);
        api.Set("query", DynValue.NewCallback((_, args) =>
        {
            if (host == null)
            {
                throw new ScriptRuntimeException($"Script '{module}' requested a query without a host.");
            }
            string name = RequiredName(args, 0, "query", module, callBudget);
            var values = ReadArgs(args, 1, callBudget);
            return ToDynValue(script, host.Query(name, values), 0, callBudget);
        }));
        api.Set("command", DynValue.NewCallback((_, args) =>
        {
            string name = RequiredName(args, 0, "command", module, callBudget);
            if (commands.Count >= _options.MaxCommands)
            {
                throw new ScriptRuntimeException(
                    $"Script '{module}' exceeds the command count limit."
                );
            }
            commands.Add(new ScriptCommand(name, ReadArgs(args, 1, callBudget)));
            return DynValue.True;
        }));
        return api;
    }

    private IReadOnlyList<object?> ReadArgs(
        CallbackArguments args,
        int start,
        ValueBudget budget
    )
    {
        var values = new List<object?>(Math.Max(args.Count - start, 0));
        for (int index = start; index < args.Count; index += 1)
        {
            budget.TakeItem();
            values.Add(FromDynValue(args[index], 0, budget));
        }
        return values.AsReadOnly();
    }

    private static string RequiredName(
        CallbackArguments args,
        int index,
        string kind,
        string module,
        ValueBudget budget
    )
    {
        if (args.Count <= index || args[index].Type != DataType.String)
        {
            throw new ScriptRuntimeException($"Script '{module}' {kind} needs a string name.");
        }
        string name = args[index].String.Trim();
        if (name.Length == 0)
        {
            throw new ScriptRuntimeException($"Script '{module}' {kind} name cannot be empty.");
        }
        if (name.Length > MaxNameLength)
        {
            throw new ScriptRuntimeException(
                $"Script '{module}' {kind} name cannot exceed {MaxNameLength} characters."
            );
        }
        budget.TakeString(name);
        return name;
    }

    private DynValue ToDynValue(
        LuaScript script,
        object? value,
        int depth,
        ValueBudget budget
    )
    {
        EnsureDepth(depth);
        if (value == null)
        {
            return DynValue.Nil;
        }
        if (value is JsonElement json)
        {
            return JsonToDynValue(script, json, depth, budget);
        }
        if (value is bool boolean)
        {
            return DynValue.NewBoolean(boolean);
        }
        if (value is string text)
        {
            budget.TakeString(text);
            return DynValue.NewString(text);
        }
        if (value is char character)
        {
            budget.TakeString(character.ToString());
            return DynValue.NewString(character.ToString());
        }
        if (value is Enum enumValue)
        {
            string enumText = enumValue.ToString();
            budget.TakeString(enumText);
            return DynValue.NewString(enumText);
        }
        if (IsNumber(value))
        {
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (!double.IsFinite(number))
            {
                throw new ScriptRuntimeException("Script numbers must be finite.");
            }
            return DynValue.NewNumber(number);
        }
        if (value is IDictionary dictionary)
        {
            var table = new Table(script);
            foreach (DictionaryEntry entry in dictionary)
            {
                budget.TakeItem();
                if (entry.Key is not string key || string.IsNullOrEmpty(key))
                {
                    throw new ScriptRuntimeException("Script dictionaries require non-empty string keys.");
                }
                budget.TakeString(key);
                table.Set(key, ToDynValue(script, entry.Value, depth + 1, budget));
            }
            return DynValue.NewTable(table);
        }
        if (TryReadStringDictionary(value, out var stringDictionary))
        {
            var table = new Table(script);
            foreach ((string key, object? item) in stringDictionary)
            {
                budget.TakeItem();
                if (string.IsNullOrEmpty(key))
                {
                    throw new ScriptRuntimeException("Script dictionaries require non-empty string keys.");
                }
                budget.TakeString(key);
                table.Set(key, ToDynValue(script, item, depth + 1, budget));
            }
            return DynValue.NewTable(table);
        }
        if (value is IEnumerable enumerable)
        {
            var table = new Table(script);
            int index = 1;
            foreach (object? item in enumerable)
            {
                budget.TakeItem();
                table.Set(index, ToDynValue(script, item, depth + 1, budget));
                index += 1;
            }
            return DynValue.NewTable(table);
        }
        throw new ScriptRuntimeException($"Unsupported script value type: {value.GetType().FullName}");
    }

    private object? FromDynValue(DynValue value, int depth, ValueBudget budget)
    {
        EnsureDepth(depth);
        return value.Type switch
        {
            DataType.Nil or DataType.Void => null,
            DataType.Boolean => value.Boolean,
            DataType.Number when double.IsFinite(value.Number) => value.Number,
            DataType.Number => throw new ScriptRuntimeException("Script numbers must be finite."),
            DataType.String => ReadString(value.String, budget),
            DataType.Table => ReadTable(value.Table, depth + 1, budget),
            _ => throw new ScriptRuntimeException($"Unsupported script result type: {value.Type}"),
        };
    }

    private object ReadTable(Table table, int depth, ValueBudget budget)
    {
        EnsureDepth(depth);
        foreach (var _ in table.Pairs)
        {
            budget.TakeItem();
        }
        var pairs = table.Pairs.ToArray();
        bool array = pairs.Length == 0 || pairs.All(pair =>
            pair.Key.Type == DataType.Number &&
            pair.Key.Number >= 1 &&
            pair.Key.Number <= pairs.Length &&
            Math.Abs(pair.Key.Number - Math.Round(pair.Key.Number)) < double.Epsilon
        );
        if (array)
        {
            var result = new object?[pairs.Length];
            foreach (var pair in pairs)
            {
                result[(int)pair.Key.Number - 1] = FromDynValue(pair.Value, depth + 1, budget);
            }
            return result;
        }

        var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            if (pair.Key.Type != DataType.String)
            {
                throw new ScriptRuntimeException("Script result objects require string keys.");
            }
            if (string.IsNullOrEmpty(pair.Key.String))
            {
                throw new ScriptRuntimeException("Script result objects require non-empty string keys.");
            }
            budget.TakeString(pair.Key.String);
            dictionary.Add(pair.Key.String, FromDynValue(pair.Value, depth + 1, budget));
        }
        return dictionary;
    }

    private DynValue JsonToDynValue(
        LuaScript script,
        JsonElement value,
        int depth,
        ValueBudget budget
    )
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null => DynValue.Nil,
            JsonValueKind.True => DynValue.True,
            JsonValueKind.False => DynValue.False,
            JsonValueKind.String => ToDynValue(script, value.GetString() ?? string.Empty, depth, budget),
            JsonValueKind.Number => ToDynValue(script, value.GetDouble(), depth, budget),
            JsonValueKind.Array => JsonArrayToDynValue(script, value, depth + 1, budget),
            JsonValueKind.Object => JsonObjectToDynValue(script, value, depth + 1, budget),
            _ => throw new ScriptRuntimeException($"Unsupported JSON value kind: {value.ValueKind}"),
        };
    }

    private DynValue JsonArrayToDynValue(
        LuaScript script,
        JsonElement value,
        int depth,
        ValueBudget budget
    )
    {
        EnsureDepth(depth);
        var table = new Table(script);
        int index = 1;
        foreach (JsonElement item in value.EnumerateArray())
        {
            budget.TakeItem();
            table.Set(index, JsonToDynValue(script, item, depth + 1, budget));
            index += 1;
        }
        return DynValue.NewTable(table);
    }

    private DynValue JsonObjectToDynValue(
        LuaScript script,
        JsonElement value,
        int depth,
        ValueBudget budget
    )
    {
        EnsureDepth(depth);
        var table = new Table(script);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (string.IsNullOrEmpty(property.Name) || !keys.Add(property.Name))
            {
                throw new ScriptRuntimeException(
                    "Script JSON objects require unique, non-empty string keys."
                );
            }
            budget.TakeItem();
            budget.TakeString(property.Name);
            table.Set(
                property.Name,
                JsonToDynValue(script, property.Value, depth + 1, budget)
            );
        }
        return DynValue.NewTable(table);
    }

    private void EnsureDepth(int depth)
    {
        if (depth > _options.MaxValueDepth)
        {
            throw new ScriptRuntimeException("Script value exceeds the nesting limit.");
        }
    }

    private ValueBudget CreateValueBudget()
    {
        return new ValueBudget(_options.MaxCollectionItems, _options.MaxStringLength);
    }

    private static string ReadString(string value, ValueBudget budget)
    {
        budget.TakeString(value);
        return value;
    }

    private static bool TryReadStringDictionary(
        object value,
        out IEnumerable<KeyValuePair<string, object?>> dictionary
    )
    {
        if (value is IReadOnlyDictionary<string, object?> readOnly)
        {
            dictionary = readOnly;
            return true;
        }
        if (value is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            dictionary = pairs;
            return true;
        }
        dictionary = [];
        return false;
    }

    private static bool IsNumber(object value)
    {
        return value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
    }

    private static ScriptRuntimeException Wrap(string module, string operation, Exception exception)
    {
        string detail = exception is InterpreterException interpreter
            ? interpreter.DecoratedMessage ?? interpreter.Message
            : exception.Message;
        return new ScriptRuntimeException($"Script '{module}' failed during '{operation}': {detail}", exception);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class Module(LuaScript script, Table exports, InstructionLimiter limiter)
    {
        public LuaScript Script { get; } = script;
        public Table Exports { get; } = exports;
        public InstructionLimiter Limiter { get; } = limiter;
        public object Gate { get; } = new();
    }

    private sealed class ValueBudget(int itemLimit, int stringLimit)
    {
        private int _items;
        private int _stringChars;

        public void TakeItem()
        {
            _items += 1;
            if (_items > itemLimit)
            {
                throw new ScriptRuntimeException("Script value exceeds the collection item limit.");
            }
        }

        public void TakeString(string value)
        {
            _stringChars = checked(_stringChars + value.Length);
            if (_stringChars > stringLimit)
            {
                throw new ScriptRuntimeException("Script value exceeds the string length limit.");
            }
        }
    }

    private sealed class InstructionLimiter(string module) : IDebugger
    {
        private readonly List<DynamicExpression> _watches = [];
        private int _limit;

        public int Instructions { get; private set; }

        public void Reset(int limit)
        {
            _limit = limit;
            Instructions = 0;
        }

        public DebuggerAction GetAction(int ip, SourceRef sourceRef)
        {
            Instructions += 1;
            if (Instructions > _limit)
            {
                throw new ScriptBudgetException(module, _limit);
            }
            return new DebuggerAction { Action = DebuggerAction.ActionType.StepIn };
        }

        public bool IsPauseRequested() => true;
        public DebuggerCaps GetDebuggerCaps() => DebuggerCaps.CanDebugSourceCode;
        public void SetDebugService(DebugService debugService) { }
        public bool SignalRuntimeException(LuaException exception) => false;
        public void SetByteCode(string[] byteCode) { }
        public void SetSourceCode(SourceCode sourceCode) { }
        public void SignalExecutionEnded() { }
        public void Update(WatchType watchType, IEnumerable<WatchItem> items) { }
        public List<DynamicExpression> GetWatchItems() => _watches;
        public void RefreshBreakpoints(IEnumerable<SourceRef> refs) { }
    }
}
