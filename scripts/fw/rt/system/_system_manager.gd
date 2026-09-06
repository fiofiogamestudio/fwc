class_name SystemManager
extends RefCounted

enum LifecycleState {
	CREATED,
	INITIALIZING,
	RUNNING,
	FAULTED,
	STOPPING,
	STOPPED,
}

signal system_added(id: StringName)
signal system_removed(id: StringName)

var _parent: Variant = null
var _entries: Array[Dictionary] = []
var _entries_by_id: Dictionary = {}
var _initialized_entries: Array[Dictionary] = []
var _phase_order: Array[StringName] = []
var _ordered_cache: Array[Dictionary] = []
var _order_dirty := true
var _state: LifecycleState = LifecycleState.CREATED
var _last_error := ""
var _is_ticking := false


func _init(parent: Variant = null) -> void:
	_parent = parent


func set_phase_order(order: Array) -> void:
	if not _ensure_configurable():
		return
	_phase_order.clear()
	for raw_phase in order:
		var phase := StringName(raw_phase)
		if phase != &"" and not _phase_order.has(phase):
			_phase_order.append(phase)
	_invalidate_order()


func add_system(
	id: StringName,
	system: Variant,
	context: Variant = null,
	phase: StringName = &"",
	dependencies: Array = []
) -> bool:
	if not _ensure_configurable():
		return false
	if id == &"":
		return _fail("System id cannot be empty.")
	if system == null:
		return _fail("System '%s' cannot be null." % id)
	if has_system(id):
		return _fail("Duplicate system id in manager scope: %s" % id)

	var normalized_dependencies: Array[StringName] = []
	for raw_dependency in dependencies:
		var dependency := StringName(raw_dependency)
		if dependency == id:
			return _fail("System '%s' cannot depend on itself." % id)
		if dependency != &"" and not normalized_dependencies.has(dependency):
			normalized_dependencies.append(dependency)

	var entry := {
		"id": id,
		"system": system,
		"context": context,
		"phase": phase,
		"dependencies": normalized_dependencies,
		"dependents": [] as Array[StringName],
		"initialized": false,
	}
	_entries.append(entry)
	_entries_by_id[id] = entry
	_rebuild_dependents()
	_invalidate_order()
	system_added.emit(id)
	return true


func set_dependencies(id: StringName, dependencies: Array) -> bool:
	if not _ensure_configurable():
		return false
	var entry: Variant = _entries_by_id.get(id, null)
	if entry == null:
		return _fail("Missing local system: %s" % id)
	var normalized: Array[StringName] = []
	for raw_dependency in dependencies:
		var dependency := StringName(raw_dependency)
		if dependency == id:
			return _fail("System '%s' cannot depend on itself." % id)
		if dependency != &"" and not normalized.has(dependency):
			normalized.append(dependency)
	entry["dependencies"] = normalized
	_rebuild_dependents()
	_invalidate_order()
	return true


func remove_system(system: Variant, cascade: bool = false) -> bool:
	for entry in _entries:
		if entry.get("system", null) == system:
			return remove_system_by_id(StringName(entry.get("id", &"")), cascade)
	return false


func remove_system_by_id(id: StringName, cascade: bool = false) -> bool:
	if _state in [LifecycleState.INITIALIZING, LifecycleState.STOPPING, LifecycleState.STOPPED]:
		return _fail("System manager cannot remove systems from state: %s" % _state)
	var entry: Variant = _entries_by_id.get(id, null)
	if entry == null:
		return false
	var dependents: Array = entry.get("dependents", [])
	if not cascade and not dependents.is_empty():
		return _fail(
			"Cannot remove system '%s'; dependents still exist: %s"
			% [id, ", ".join(dependents)]
		)

	var removal_order: Array[StringName] = []
	_collect_removal_order(id, {}, removal_order)
	for remove_id in removal_order:
		var remove_entry: Variant = _entries_by_id.get(remove_id, null)
		if remove_entry == null:
			continue
		_shutdown_entry(remove_entry)
		_erase_entry(remove_id)
		system_removed.emit(remove_id)
	_rebuild_dependents()
	return true


func get_system(index: int) -> Variant:
	if index < 0 or index >= _entries.size():
		return null
	return _entries[index].get("system", null)


func get_system_by_id(id: StringName) -> Variant:
	var entry: Variant = _entries_by_id.get(id, null)
	if entry != null:
		return entry.get("system", null)
	if _parent != null and _parent.has_method("get_system_by_id"):
		return _parent.get_system_by_id(id)
	return null


func has_local_system(id: StringName) -> bool:
	return _entries_by_id.has(id)


func has_system(id: StringName) -> bool:
	if has_local_system(id):
		return true
	return _parent != null and _parent.has_method("has_system") and _parent.has_system(id)


func is_system_initialized(id: StringName) -> bool:
	var entry: Variant = _entries_by_id.get(id, null)
	if entry != null:
		return bool(entry.get("initialized", false))
	return (
		_parent != null
		and _parent.has_method("is_system_initialized")
		and _parent.is_system_initialized(id)
	)


func get_context(id: StringName) -> Variant:
	var entry: Variant = _entries_by_id.get(id, null)
	if entry != null:
		return entry.get("context", null)
	if _parent != null and _parent.has_method("get_context"):
		return _parent.get_context(id)
	return null


func bind_refs(graph_refs: Dictionary) -> bool:
	if not _ensure_configurable():
		return false
	_last_error = ""
	for entry in _entries:
		var entry_id: StringName = entry.get("id", &"")
		if entry_id != &"" and not graph_refs.has(entry_id):
			return _fail("System graph missing registered system: %s" % entry_id)

	var binding_plan: Array[Dictionary] = []
	var dependencies_by_id: Dictionary = {}
	for raw_system_id in graph_refs.keys():
		var system_id := StringName(raw_system_id)
		var local_entry: Variant = _entries_by_id.get(system_id, null)
		if local_entry == null:
			return _fail("System graph references non-local system: %s" % system_id)
		var context = local_entry.get("context", null)
		if context == null:
			return _fail("System graph references missing system: %s" % system_id)
		if not _has_property(context, &"refs") or context.refs == null:
			return _fail("System context has no refs: %s" % system_id)

		var refs: Dictionary = graph_refs[raw_system_id]
		var dependencies: Array[StringName] = []
		for raw_ref_name in refs.keys():
			var ref_name := StringName(raw_ref_name)
			var target_id := StringName(refs[raw_ref_name])
			var target_context = get_context(target_id)
			if target_context == null:
				return _fail(
					"%s.refs.%s references missing system: %s"
					% [system_id, ref_name, target_id]
				)
			if not _has_property(context.refs, ref_name):
				return _fail(
					"System context %s.refs has no field: %s" % [system_id, ref_name]
				)
			binding_plan.append({
				"refs": context.refs,
				"name": ref_name,
				"value": target_context,
			})
			if target_id != system_id and not dependencies.has(target_id):
				dependencies.append(target_id)
		dependencies_by_id[system_id] = dependencies

	for entry in _entries:
		var entry_id: StringName = entry.get("id", &"")
		entry["dependencies"] = dependencies_by_id.get(entry_id, [] as Array[StringName])
	for binding in binding_plan:
		var refs_object: Object = binding.get("refs", null)
		refs_object.set(StringName(binding.get("name", &"")), binding.get("value", null))
	_rebuild_dependents()
	_invalidate_order()
	return true


func init_all() -> bool:
	if _state != LifecycleState.CREATED:
		return _fail("System manager cannot initialize from state: %s" % _state)
	_last_error = ""
	_state = LifecycleState.INITIALIZING
	var ordered := _ordered_entries()
	if not _last_error.is_empty():
		_state = LifecycleState.FAULTED
		_clear_registration()
		_state = LifecycleState.STOPPED
		return false

	for entry in ordered:
		if not _dependencies_initialized(entry):
			_state = LifecycleState.FAULTED
			_shutdown_initialized()
			_clear_registration()
			_state = LifecycleState.STOPPED
			return false
		entry["initialized"] = true
		_initialized_entries.append(entry)
		var system = entry.get("system", null)
		if system != null and system.has_method("init"):
			var result: Variant = system.init(entry.get("context", null))
			if _state != LifecycleState.INITIALIZING:
				_last_error = "System manager initialization was cancelled by shutdown."
				return false
			if not (result is bool) or not result:
				_fail("System '%s' init must return true; initialization failed." % entry.get("id", &""))
				_state = LifecycleState.FAULTED
				_shutdown_initialized()
				_clear_registration()
				_state = LifecycleState.STOPPED
				return false
	_state = LifecycleState.RUNNING
	return true


func tick(dt: float) -> void:
	if _state != LifecycleState.RUNNING:
		return
	if _is_ticking:
		_fail("System manager tick cannot be reentered.")
		return
	_is_ticking = true
	var snapshot := _ordered_entries().duplicate()
	for entry in snapshot:
		if _state != LifecycleState.RUNNING:
			break
		var id: StringName = entry.get("id", &"")
		if not _entries_by_id.has(id) or not bool(entry.get("initialized", false)):
			continue
		var system = entry.get("system", null)
		if system != null and system.has_method("tick"):
			system.tick(dt)
	_is_ticking = false


func shutdown_all() -> void:
	if _state in [LifecycleState.STOPPED, LifecycleState.STOPPING]:
		return
	_state = LifecycleState.STOPPING
	_shutdown_initialized()
	_clear_registration()
	_state = LifecycleState.STOPPED


func snapshots(include_parent: bool = true) -> Array[Dictionary]:
	var result: Array[Dictionary] = []
	if include_parent and _parent != null and _parent.has_method("snapshots"):
		for parent_snapshot in _parent.snapshots(true):
			var inherited: Dictionary = parent_snapshot.duplicate(true)
			inherited["scope"] = "parent/%s" % inherited.get("scope", "local")
			result.append(inherited)
	for entry in _ordered_entries():
		result.append({
			"scope": "local",
			"id": entry.get("id", &""),
			"phase": entry.get("phase", &""),
			"initialized": entry.get("initialized", false),
			"dependencies": Array(entry.get("dependencies", [])).duplicate(),
			"dependents": Array(entry.get("dependents", [])).duplicate(),
		})
	return result


func is_running() -> bool:
	return _state == LifecycleState.RUNNING


func is_started() -> bool:
	return is_running()


func lifecycle_state() -> LifecycleState:
	return _state


func last_error() -> String:
	return _last_error


func _shutdown_initialized() -> void:
	for index in range(_initialized_entries.size() - 1, -1, -1):
		_shutdown_entry(_initialized_entries[index])
	_initialized_entries.clear()


func _shutdown_entry(entry: Dictionary) -> void:
	if not bool(entry.get("initialized", false)):
		return
	entry["initialized"] = false
	var system = entry.get("system", null)
	if system != null and system.has_method("shutdown"):
		system.shutdown()
	_initialized_entries.erase(entry)


func _collect_removal_order(
	id: StringName,
	visited: Dictionary,
	result: Array[StringName]
) -> void:
	if visited.has(id):
		return
	visited[id] = true
	var entry: Variant = _entries_by_id.get(id, null)
	if entry == null:
		return
	for raw_dependent in entry.get("dependents", []):
		_collect_removal_order(StringName(raw_dependent), visited, result)
	result.append(id)


func _erase_entry(id: StringName) -> void:
	_entries_by_id.erase(id)
	for index in range(_entries.size() - 1, -1, -1):
		if StringName(_entries[index].get("id", &"")) == id:
			_entries.remove_at(index)
	_invalidate_order()


func _rebuild_dependents() -> void:
	for entry in _entries:
		entry["dependents"] = [] as Array[StringName]
	for entry in _entries:
		var source_id: StringName = entry.get("id", &"")
		for raw_dependency in entry.get("dependencies", []):
			var dependency := StringName(raw_dependency)
			var target: Variant = _entries_by_id.get(dependency, null)
			if target == null:
				continue
			var dependents: Array[StringName] = target.get("dependents", [])
			if not dependents.has(source_id):
				dependents.append(source_id)
			target["dependents"] = dependents


func _clear_registration() -> void:
	_entries.clear()
	_entries_by_id.clear()
	_initialized_entries.clear()
	_phase_order.clear()
	_ordered_cache.clear()
	_order_dirty = true


func _ensure_configurable() -> bool:
	if _state == LifecycleState.CREATED:
		return true
	return _fail("System manager cannot be configured from state: %s" % _state)


func _ordered_entries() -> Array[Dictionary]:
	if not _order_dirty:
		return _ordered_cache
	_last_error = ""
	var base_order := _entries.duplicate()
	base_order.sort_custom(
		func(left: Dictionary, right: Dictionary) -> bool:
			var left_rank := _phase_rank(StringName(left.get("phase", &"")))
			var right_rank := _phase_rank(StringName(right.get("phase", &"")))
			if left_rank != right_rank:
				return left_rank < right_rank
			return _entries.find(left) < _entries.find(right)
	)
	var visited: Dictionary = {}
	var visiting: Dictionary = {}
	for entry in base_order:
		if not _visit_entry(entry, visited, visiting):
			_ordered_cache.clear()
			_order_dirty = false
			return _ordered_cache
	_order_dirty = false
	return _ordered_cache


func _invalidate_order() -> void:
	_ordered_cache.clear()
	_order_dirty = true


func _visit_entry(entry: Dictionary, visited: Dictionary, visiting: Dictionary) -> bool:
	var id: StringName = entry.get("id", &"")
	if visited.has(id):
		return true
	if visiting.has(id):
		return _fail("System dependency cycle detected at '%s'." % id)
	visiting[id] = true
	for raw_dependency in entry.get("dependencies", []):
		var dependency_id := StringName(raw_dependency)
		var dependency: Variant = _entries_by_id.get(dependency_id, null)
		if dependency == null:
			if not (
				_parent != null
				and _parent.has_method("has_system")
				and _parent.has_system(dependency_id)
			):
				return _fail(
					"System '%s' references missing dependency '%s'." % [id, dependency_id]
				)
			continue
		if _phase_rank(StringName(dependency.get("phase", &""))) > _phase_rank(
			StringName(entry.get("phase", &""))
		):
			return _fail(
				"System '%s' depends on later phase system '%s'." % [id, dependency_id]
			)
		if not _visit_entry(dependency, visited, visiting):
			return false
	visiting.erase(id)
	visited[id] = true
	_ordered_cache.append(entry)
	return true


func _phase_rank(phase: StringName) -> int:
	var index := _phase_order.find(phase)
	return index if index >= 0 else _phase_order.size()


func _dependencies_initialized(entry: Dictionary) -> bool:
	var id: StringName = entry.get("id", &"")
	for raw_dependency in entry.get("dependencies", []):
		var dependency_id := StringName(raw_dependency)
		var dependency: Variant = _entries_by_id.get(dependency_id, null)
		if dependency != null:
			if not bool(dependency.get("initialized", false)):
				return _fail(
					"System '%s' requires '%s' to be initialized first." % [id, dependency_id]
				)
			continue
		if not (
			_parent != null
			and _parent.has_method("is_system_initialized")
			and _parent.is_system_initialized(dependency_id)
		):
			return _fail(
				"System '%s' requires initialized parent system '%s'." % [id, dependency_id]
			)
	return true


func _fail(message: String) -> bool:
	_last_error = message
	push_error(message)
	return false


func _has_property(obj: Object, property_name: StringName) -> bool:
	if obj == null:
		return false
	for info in obj.get_property_list():
		if StringName(info.name) == property_name:
			return true
	return false
