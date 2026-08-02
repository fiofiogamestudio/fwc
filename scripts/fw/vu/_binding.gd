class_name FBinding
extends RefCounted

var _connections: Array[Dictionary] = []


func bind_signal(emitter: Object, signal_name: StringName, callable: Callable, flags: int = 0) -> void:
	if emitter == null or not is_instance_valid(emitter) or not callable.is_valid():
		return
	if not emitter.has_signal(signal_name):
		push_error("FBinding emitter does not define signal '%s'." % String(signal_name))
		return
	# An existing connection may be owned by another binding or by the caller.
	# Only remember connections created here so unbind never disconnects caller-owned state.
	if emitter.is_connected(signal_name, callable):
		return
	var error := emitter.connect(signal_name, callable, flags)
	if error != OK:
		push_error("FBinding failed to connect signal '%s' (error %d)." % [String(signal_name), error])
		return
	_connections.append({
		"emitter": emitter,
		"signal": signal_name,
		"callable": callable,
	})


func bind_vm(vm: Variant, callable: Callable, immediate: bool = true) -> void:
	if vm == null or not callable.is_valid():
		return
	bind_signal(vm, &"changed", callable)
	if immediate:
		callable.call(&"")


func bind_view_model(vm: Variant, callable: Callable, immediate: bool = true) -> void:
	bind_vm(vm, callable, immediate)


func unbind() -> void:
	var connections := _connections
	_connections = []
	for entry in connections:
		var emitter = entry.get("emitter", null)
		var signal_name: StringName = entry.get("signal", &"")
		var callable: Callable = entry.get("callable", Callable())
		if (
			emitter != null
			and is_instance_valid(emitter)
			and callable.is_valid()
			and emitter.has_signal(signal_name)
			and emitter.is_connected(signal_name, callable)
		):
			emitter.disconnect(signal_name, callable)


func clear() -> void:
	unbind()
