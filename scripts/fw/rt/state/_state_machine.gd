class_name FStateMachine
extends RefCounted

signal started(state: Variant, payload: Variant)
signal transitioned(previous: Variant, current: Variant, payload: Variant)
signal stopped(previous: Variant)

const MAX_CHAINED_TRANSITIONS := 32

var _context: Variant = null
var _states: Dictionary = {}
var _transitions: Dictionary = {}
var _active := false
var _current: Variant = null
var _transitioning := false
var _pending_transition: Dictionary = {}


func setup(context: Variant = null) -> void:
	_context = context


func register_state(
	state: Variant,
	on_enter: Callable = Callable(),
	on_tick: Callable = Callable(),
	on_exit: Callable = Callable()
) -> void:
	_states[state] = {
		"enter": on_enter,
		"tick": on_tick,
		"exit": on_exit,
	}


func register_transition(
	from: Variant,
	to: Variant,
	action: Callable = Callable(),
	guard: Callable = Callable()
) -> void:
	var bucket: Dictionary = _transitions.get(from, {})
	bucket[to] = {
		"action": action,
		"guard": guard,
	}
	_transitions[from] = bucket


func start(initial: Variant, payload: Variant = null) -> bool:
	if _active or not _states.has(initial):
		return false
	_active = true
	_current = initial
	_transitioning = true
	_call(_states[initial].get("enter", Callable()), [_context, payload])
	_transitioning = false
	started.emit(initial, payload)
	_drain_pending_transitions()
	return true


func tick(dt: float) -> void:
	if not _active or not _states.has(_current):
		return
	_call(_states[_current].get("tick", Callable()), [_context, dt])


func transition_to(target: Variant, payload: Variant = null) -> bool:
	if not _active or not _states.has(target):
		return false
	if _transitioning:
		_pending_transition = {"target": target, "payload": payload}
		return true
	return _perform_transition(target, payload)


func stop() -> void:
	if not _active:
		return
	var previous: Variant = _current
	_pending_transition.clear()
	_transitioning = true
	if _states.has(previous):
		_call(_states[previous].get("exit", Callable()), [_context])
	_pending_transition.clear()
	_transitioning = false
	_active = false
	_current = null
	stopped.emit(previous)


func clear() -> void:
	stop()
	_states.clear()
	_transitions.clear()
	_pending_transition.clear()
	_context = null


func is_active() -> bool:
	return _active


func current_state() -> Variant:
	return _current


func _perform_transition(target: Variant, payload: Variant) -> bool:
	var previous: Variant = _current
	var transition: Dictionary = _transitions.get(previous, {}).get(target, {})
	var guard: Callable = transition.get("guard", Callable())
	if guard.is_valid() and not bool(guard.call(_context, previous, target, payload)):
		return false

	_transitioning = true
	_call(_states[previous].get("exit", Callable()), [_context])
	_call(transition.get("action", Callable()), [_context, previous, target, payload])
	_current = target
	_call(_states[target].get("enter", Callable()), [_context, payload])
	_transitioning = false
	transitioned.emit(previous, target, payload)
	_drain_pending_transitions()
	return true


func _drain_pending_transitions() -> void:
	var count := 0
	while _active and not _transitioning and not _pending_transition.is_empty():
		if count >= MAX_CHAINED_TRANSITIONS:
			push_error("FStateMachine exceeded the chained transition limit.")
			_pending_transition.clear()
			return
		var pending := _pending_transition
		_pending_transition = {}
		_perform_transition(pending.get("target", null), pending.get("payload", null))
		count += 1


func _call(callable: Callable, args: Array) -> Variant:
	if not callable.is_valid():
		return null
	return callable.callv(args)
