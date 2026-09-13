extends RefCounted

const GMScript = preload("res://scripts/_fw/fw/rt/gm/_gm.gd")
const DebugScript = preload("res://scripts/_fw/fw/rt/debug/_debug.gd")

var _failures: Array[String] = []
var _calls := 0
var _lastValues: Dictionary = {}
var _liveOptions: Array = [{"value": "a", "label": "Alpha"}]
var _providerMode := "ok"
var _available := true
var _availabilityMode := "ok"
var _providerCalls := 0
var _previousValues: Dictionary = {}
var _nestedResult: Dictionary = {}
var _gm: Variant


class CallbackProbe:
	extends Node

	func handle(_values: Dictionary) -> Dictionary:
		return {"ok": true}

	func options(_id: StringName, _values: Dictionary) -> Dictionary:
		return {"ok": true, "options": [{"value": "a", "label": "A"}]}


func run() -> String:
	_failures.clear()
	_test_registry()
	_test_definitions()
	_test_arguments()
	_test_live_callbacks()
	_test_execution_and_history()
	_test_callback_lifecycle()
	return "\n".join(_failures)


func _service(limit: int = 100) -> Variant:
	var debug := DebugScript.new()
	debug.setup(true, true)
	var service := GMScript.new()
	service.setup(debug, null, limit)
	return service


func _test_registry() -> void:
	var service: Variant = _service()
	var definition := {"id": "one", "title": "First", "args": [{"id": "name", "kind": "text", "default": "before"}]}
	_check(service.register_command(&"owner_a", definition, _handle).ok, "Register valid command")
	definition.title = "Mutated"
	definition.args[0].default = "after"
	var listed: Array = service.commands()
	_check(listed[0].title == "First" and listed[0].args[0].default == "before", "Registration owns descriptor snapshot")
	listed[0].args[0].default = "outside"
	_check(service.commands()[0].args[0].default == "before", "Command listing owns nested snapshot")
	_check(not service.register_command(&"owner_b", {"id": "one", "title": "Collision"}, _handle).ok, "Duplicate command ids rejected")
	_check(service.commands().size() == 1, "Duplicate registration remains atomic")
	service.register_command(&"owner_b", {"id": "two", "title": "Second"}, _handle)
	service.unregister_owner(&"owner_a")
	_check(service.commands().size() == 1 and service.commands()[0].id == "two", "Owner unregister preserves other owners")
	service.unregister_command(&"two")
	service.unregister_command(&"missing")
	_check(service.commands().is_empty(), "Unregister command is idempotent")
	_check(service.execute(&"missing").code == "not_found", "Unknown command rejects execution")
	service.clear()
	_check(not service.is_enabled() and service.history().is_empty(), "Clear resets registry history and debug gate")


func _test_definitions() -> void:
	var service: Variant = _service()
	var invalid: Array = [
		{}, {"id": "x", "title": ""}, {"id": "x", "title": "X", "risk": "fatal"},
		{"id": "x", "title": "X", "args": {}}, {"id": "x", "title": "X", "unknown": true},
		{"id": "x", "title": "X", "args": [12]},
		{"id": "x", "title": "X", "args": [{"id": "a", "kind": "number"}]},
		{"id": "x", "title": "X", "args": [{"id": "a", "kind": "text"}, {"id": "a", "kind": "bool"}]},
		{"id": "x", "title": "X", "args": [{"id": "a", "kind": "text", "required": "yes"}]},
		{"id": "x", "title": "X", "args": [{"id": "a", "kind": "int", "min": 9, "max": 1}]},
		{"id": "x", "title": "X", "args": [{"id": "a", "kind": "float", "max": INF}]},
		{"id": "x", "title": "X", "args": [{"id": "a", "kind": "int", "min": 0.5}]},
		{"id": "x", "title": "X", "args": [{"id": "a", "kind": "int", "default": "1.5"}]},
		{"id": "x", "title": "X", "args": [{"id": "a", "kind": "dropdown"}]},
		{"id": "x", "title": "X", "args": [{"id": "a", "kind": "dropdown", "options": [{"value": "v", "label": "V"}, {"value": "v", "label": "Again"}]}]},
		{"id": "x", "title": "X", "args": [{"id": "a", "kind": "dropdown", "options": [], "depends_on": ["later"]}, {"id": "later", "kind": "text"}]},
		{"id": "x", "title": "X", "args": [{"id": "a", "kind": "dropdown", "options": [{"value": "v", "label": "V"}], "default": "missing"}]},
	]
	for index: int in invalid.size():
		_check(not service.register_command(&"owner", invalid[index], _handle).ok, "Malformed descriptor rejected %d" % index)
	_check(service.commands().is_empty(), "All invalid definitions leave registry empty")
	_check(not service.register_command(&"", {"id": "x", "title": "X"}, _handle).ok, "Empty owner rejected")
	_check(not service.register_command(&"owner", {"id": "x", "title": "X"}, Callable()).ok, "Missing handler rejected")
	_check(not service.register_command(&"owner", {"id": "x", "title": "X"}, _wrong_arity).ok, "Wrong callback signature rejected before invocation")


func _test_arguments() -> void:
	var service: Variant = _service()
	service.register_command(&"owner", {"id": "args", "title": "Arguments", "args": [
		{"id": "count", "kind": "int", "required": true, "min": 1, "max": 10},
		{"id": "scale", "kind": "float", "default": 1.0, "min": 0.0, "max": 4.0},
		{"id": "enabled", "kind": "bool", "default": false},
		{"id": "name", "kind": "text", "required": true},
		{"id": "note", "kind": "text_area"},
		{"id": "optional", "kind": "int"},
		{"id": "choice", "kind": "dropdown", "required": true, "options": [{"value": "a", "label": "Alpha"}]},
	]}, _handle)
	var values := {"count": "+002", "scale": "2.5", "name": "Name", "choice": "a"}
	var inspected: Dictionary = service.inspect_command(&"args", values)
	_check(inspected.ok and inspected.values.count == 2 and inspected.values.scale == 2.5 and inspected.values.enabled == false, "Arguments normalize to declared scalar types")
	_check(not inspected.values.has("optional") and inspected.values.note == "", "Optional empty numeric values omitted and text normalized")
	inspected.options.choice[0].value = "outside"
	_check(service.inspect_command(&"args", values).options.choice[0].value == "a", "Inspection options cannot mutate registered descriptor")
	var invalid: Array = [{"count": "1.0"}, {"count": 2.0}, {"count": "1e2"}, {"count": "12x"}, {"count": 0}, {"count": 11}, {"count": true}, {"count": "9223372036854775808"}, {"count": "-9223372036854775809"}, {"scale": INF}, {"scale": NAN}, {"scale": "NaN"}, {"scale": 5}, {"enabled": "false"}, {"name": "  "}, {"choice": "stale"}, {"unknown": 1}]
	for index: int in invalid.size():
		var changed_values := values.duplicate(true)
		changed_values.merge(invalid[index], true)
		_check(not service.execute(&"args", changed_values).ok, "Invalid argument rejected %d" % index)
	_check(not service.execute(&"args", {}).ok, "Missing required arguments rejected")
	_calls = 0
	_check(service.execute(&"args", values).ok and _calls == 1 and _lastValues.count == 2, "Handler receives validated values once")
	service.register_command(&"owner", {"id": "int64", "title": "Int64", "args": [{"id": "value", "kind": "int", "required": true}]}, _handle)
	_check(service.inspect_command(&"int64", {"value": "9223372036854775807"}).values.get("value") == 9223372036854775807, "Signed integer maximum preserved")
	_check(service.inspect_command(&"int64", {"value": "-9223372036854775808"}).values.get("value") == -9223372036854775807 - 1, "Signed integer minimum preserved")


func _test_live_callbacks() -> void:
	var service: Variant = _service()
	_providerMode = "ok"
	_availabilityMode = "ok"
	_available = true
	_liveOptions = [{"value": "a", "label": "Alpha"}]
	service.register_command(&"owner", {"id": "live", "title": "Live", "args": [
		{"id": "parent", "kind": "int", "required": true},
		{"id": "choice", "kind": "dropdown", "required": true, "depends_on": ["parent"]},
	]}, _handle, _options, _availability)
	_calls = 0
	_providerCalls = 0
	_check(service.inspect_command(&"live", {"parent": "3", "choice": "a"}).ok and _calls == 0, "Inspection never invokes the handler")
	_check(_previousValues == {"parent": 3}, "Provider sees only normalized previous values")
	_liveOptions = [{"value": "b", "label": "Beta"}]
	_check(service.execute(&"live", {"parent": "3", "choice": "a"}).code == "invalid_arguments" and _calls == 0, "Execution refreshes stale dropdown options")
	var provider_calls := _providerCalls
	_check(not service.inspect_command(&"live", {"parent": "bad", "choice": "b"}).ok and _providerCalls == provider_calls, "Invalid dependency never reaches option provider")
	for mode: String in ["failure", "malformed", "bad_options", "duplicate"]:
		_providerMode = mode
		var inspection: Dictionary = service.inspect_command(&"live", {"parent": 3, "choice": "b"})
		_check(not inspection.ok and inspection.options.choice.is_empty(), "Provider failure clears stale options: %s" % mode)
		_check(not service.execute(&"live", {"parent": 3, "choice": "b"}).ok and _calls == 0, "Provider failure blocks handler: %s" % mode)
	_providerMode = "ok"
	_check(service.inspect_command(&"live", {"parent": 3, "choice": "b"}).ok, "Provider recovery refreshes options")
	_available = false
	_check(service.execute(&"live", {"parent": 3, "choice": "b"}).code == "unavailable" and _calls == 0, "Availability is rechecked at execution")
	_available = true
	_availabilityMode = "malformed"
	_check(service.execute(&"live", {"parent": 3, "choice": "b"}).code == "callback_invalid", "Malformed availability response fails closed")
	_availabilityMode = "ok"
	_check(service.execute(&"live", {"parent": 3, "choice": "b"}).ok and _calls == 1, "Valid live command runs once after recovery")


func _test_execution_and_history() -> void:
	var service: Variant = _service(2)
	service.register_command(&"owner", {"id": "risky", "title": "Risky", "risk": "destructive"}, _handle)
	_calls = 0
	_check(service.execute(&"risky").code == "confirmation_required" and _calls == 0, "Risk confirmation enforced in service")
	_check(service.execute(&"risky", {}, true).ok and _calls == 1, "Confirmed risky command executes")
	service.register_command(&"owner", {"id": "reject", "title": "Rejected"}, _reject)
	_check(service.execute(&"reject").code == "rejected", "Business rejection remains a failure")
	service.register_command(&"owner", {"id": "malformed", "title": "Malformed"}, _malformed)
	_check(service.execute(&"malformed").code == "callback_invalid", "Malformed handler result fails closed")
	service.register_command(&"owner", {"id": "data", "title": "Data"}, _data)
	service.executed.connect(_mutate_signal)
	var result: Dictionary = service.execute(&"data")
	_check(result.data.nested[0].value == "original", "Signal listeners cannot mutate execute result")
	result.data.nested[0].value = "outside"
	var entries: Array = service.history()
	_check(entries.size() == 2 and entries[1].result.data.nested[0].value == "original", "History is bounded and separate from result")
	entries[1].result.data.nested[0].value = "outside"
	_check(service.history()[1].result.data.nested[0].value == "original", "History snapshots are deeply independent")
	_gm = service
	service.register_command(&"owner", {"id": "nested", "title": "Nested"}, _nested)
	_check(service.execute(&"nested").ok and _nestedResult.code == "busy", "Reentrant execution is blocked")
	_gm = null
	var debug := DebugScript.new()
	debug.setup(false, true)
	service.setup(debug, null, 0)
	service.register_command(&"owner", {"id": "disabled", "title": "Disabled"}, _handle)
	_calls = 0
	_check(service.execute(&"disabled").code == "disabled" and _calls == 0, "Debug gate blocks execution")
	debug.set_enabled(true)
	_check(service.execute(&"disabled").ok and _calls == 1 and service.history().is_empty(), "Enabled gate works with history disabled")
	debug.set_enabled(false)
	_check(not service.inspect_command(&"disabled").available, "Inspect follows live debug state")


func _test_callback_lifecycle() -> void:
	var service: Variant = _service()
	var probe := CallbackProbe.new()
	_check(service.register_command(&"probe", {"id": "freed", "title": "Freed"}, probe.handle).ok, "Live object callback registers")
	probe.free()
	_check(service.execute(&"freed").code == "callback_invalid", "Freed handler is rejected without invocation")
	probe = CallbackProbe.new()
	service.register_command(&"probe", {"id": "freed_provider", "title": "Freed provider", "args": [{"id": "choice", "kind": "dropdown", "required": true}]}, _handle, probe.options)
	service.register_command(&"probe", {"id": "freed_availability", "title": "Freed availability"}, _handle, Callable(), probe.handle)
	probe.free()
	_check(service.execute(&"freed_provider", {"choice": "a"}).code == "callback_invalid", "Freed provider cannot reuse cached options")
	_check(service.execute(&"freed_availability").code == "callback_invalid", "Freed availability cannot silently become unrestricted")
	_gm = service
	service.register_command(&"owner", {"id": "changed", "title": "Changed"}, _handle, Callable(), _remove_during_availability)
	_calls = 0
	_check(service.execute(&"changed").code == "command_changed" and _calls == 0, "Removing command during validation blocks handler")
	service.register_command(&"owner", {"id": "unsafe_data", "title": "Unsafe data"}, _unsafe_data)
	_check(service.execute(&"unsafe_data").code == "callback_invalid", "Mutable object result cannot enter snapshot history")
	service.register_command(&"owner", {"id": "clear", "title": "Clear"}, _clear_during_handler)
	_check(service.execute(&"clear").ok and service.history().is_empty() and not service.is_enabled(), "Clear during execution preserves teardown and empty history")
	_gm = null


func _handle(values: Dictionary) -> Dictionary:
	_calls += 1
	_lastValues = values.duplicate(true)
	return {"ok": true, "message": "Done"}


func _wrong_arity() -> Dictionary:
	return {"ok": true}


func _options(_arg_id: StringName, previous_values: Dictionary) -> Variant:
	_providerCalls += 1
	_previousValues = previous_values.duplicate(true)
	match _providerMode:
		"failure":
			return {"ok": false, "message": "Provider unavailable"}
		"malformed":
			return null
		"bad_options":
			return {"ok": true, "options": ["bad"]}
		"duplicate":
			return {"ok": true, "options": [{"value": "a", "label": "A"}, {"value": "a", "label": "A again"}]}
	return {"ok": true, "options": _liveOptions}


func _availability(_values: Dictionary) -> Variant:
	if _availabilityMode == "malformed":
		return {"ok": "yes"}
	return {"ok": _available, "message": "Unavailable"}


func _reject(_values: Dictionary) -> Dictionary:
	return {"ok": false, "message": "Business rule rejected this command."}


func _malformed(_values: Dictionary) -> Variant:
	return "success"


func _data(_values: Dictionary) -> Dictionary:
	return {"ok": true, "data": {"nested": [{"value": "original"}]}}


func _nested(_values: Dictionary) -> Dictionary:
	_nestedResult = _gm.execute(&"data")
	return {"ok": true}


func _remove_during_availability(_values: Dictionary) -> Dictionary:
	_gm.unregister_command(&"changed")
	return {"ok": true}


func _unsafe_data(_values: Dictionary) -> Dictionary:
	return {"ok": true, "data": self}


func _clear_during_handler(_values: Dictionary) -> Dictionary:
	_gm.clear()
	_nestedResult = _gm.execute(&"clear")
	_check(_nestedResult.code == "busy", "Clear does not release active execution guard")
	return {"ok": true}


func _mutate_signal(entry: Dictionary) -> void:
	if entry.result.has("data"):
		entry.result.data.nested[0].value = "signal mutation"


func _check(condition: bool, message: String) -> void:
	if not condition:
		_failures.append("FGM: " + message)
