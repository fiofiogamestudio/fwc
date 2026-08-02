class_name FEventBus
extends RefCounted

signal published(kind: StringName, payload: Variant)

var _listeners: Dictionary = {}
var _subscription_kinds: Dictionary = {}
var _next_subscription_id := 1


func subscribe(
	kind: StringName,
	callable: Callable,
	once: bool = false,
	priority: int = 0
) -> int:
	if kind == &"" or not callable.is_valid():
		return 0
	var bucket: Array = _listeners.get(kind, [])
	for existing in bucket:
		if existing.get("callable", Callable()) == callable:
			return int(existing.get("id", 0))

	var subscription_id := _next_subscription_id
	_next_subscription_id += 1
	bucket.append({
		"id": subscription_id,
		"callable": callable,
		"once": once,
		"priority": priority,
	})
	bucket.sort_custom(
		func(left: Dictionary, right: Dictionary) -> bool:
			var left_priority := int(left.get("priority", 0))
			var right_priority := int(right.get("priority", 0))
			if left_priority != right_priority:
				return left_priority > right_priority
			return int(left.get("id", 0)) < int(right.get("id", 0))
	)
	_listeners[kind] = bucket
	_subscription_kinds[subscription_id] = kind
	return subscription_id


func on(kind: StringName, callable: Callable, priority: int = 0) -> int:
	return subscribe(kind, callable, false, priority)


func once(kind: StringName, callable: Callable, priority: int = 0) -> int:
	return subscribe(kind, callable, true, priority)


func unsubscribe(subscription_id: int) -> bool:
	if not _subscription_kinds.has(subscription_id):
		return false
	var kind: StringName = _subscription_kinds[subscription_id]
	var bucket: Array = _listeners.get(kind, [])
	for index in range(bucket.size() - 1, -1, -1):
		if int(bucket[index].get("id", 0)) == subscription_id:
			bucket.remove_at(index)
	if bucket.is_empty():
		_listeners.erase(kind)
	else:
		_listeners[kind] = bucket
	_subscription_kinds.erase(subscription_id)
	return true


func off(kind: StringName, callable: Callable) -> int:
	var removed := 0
	var bucket: Array = _listeners.get(kind, []).duplicate()
	for entry in bucket:
		if entry.get("callable", Callable()) != callable:
			continue
		if unsubscribe(int(entry.get("id", 0))):
			removed += 1
	return removed


func emit_event(kind: StringName, payload: Variant = null) -> int:
	published.emit(kind, payload)
	var invoked := 0
	var snapshot: Array = _listeners.get(kind, []).duplicate()
	for entry in snapshot:
		var subscription_id := int(entry.get("id", 0))
		if not _subscription_kinds.has(subscription_id):
			continue
		if bool(entry.get("once", false)):
			unsubscribe(subscription_id)
		var callable: Callable = entry.get("callable", Callable())
		if not callable.is_valid():
			unsubscribe(subscription_id)
			continue
		callable.call(payload)
		invoked += 1
	return invoked


func listener_count(kind: StringName = &"") -> int:
	if kind != &"":
		return Array(_listeners.get(kind, [])).size()
	return _subscription_kinds.size()


func clear(kind: StringName = &"") -> void:
	if kind == &"":
		_listeners.clear()
		_subscription_kinds.clear()
		return
	var bucket: Array = _listeners.get(kind, [])
	for entry in bucket:
		_subscription_kinds.erase(int(entry.get("id", 0)))
	_listeners.erase(kind)
