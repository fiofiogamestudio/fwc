class_name FAsset
extends RefCounted

class AssetHandle extends RefCounted:
	var asset: Resource = null
	var path := ""
	var _owner: WeakRef = null
	var _released := false

	func _init(owner: FAsset, cache_path: String, value: Resource) -> void:
		_owner = weakref(owner)
		path = cache_path
		asset = value

	func release() -> void:
		if _released:
			return
		_released = true
		var owner := _owner.get_ref() as FAsset if _owner != null else null
		if owner != null:
			owner.release(path)
		asset = null
		_owner = null

	func is_released() -> bool:
		return _released

	func _notification(what: int) -> void:
		if what != NOTIFICATION_PREDELETE or _released:
			return
		_released = true
		var owner := _owner.get_ref() as FAsset if _owner != null else null
		if owner != null:
			owner.release(path)
		asset = null
		_owner = null


class PendingLoad extends RefCounted:
	signal completed

	var source: StringName = &""
	var provider: Variant = null
	var resource: Resource = null
	var cancelled := false
	var done := false

	func _init(load_source: StringName, load_provider: Variant) -> void:
		source = load_source
		provider = load_provider

	func wait() -> Resource:
		if not done:
			await completed
		return resource

	func complete(value: Resource) -> void:
		resource = value
		done = true
		completed.emit()


var _cache: Dictionary = {}
var _load_counts: Dictionary = {}
var _ref_counts: Dictionary = {}
var _pinned: Dictionary = {}
var _providers: Dictionary = {}
var _cache_sources: Dictionary = {}
var _cache_providers: Dictionary = {}
var _inflight: Dictionary = {}


func register_provider(source: StringName, provider: Variant) -> bool:
	if source == &"" or not _is_provider_valid(provider) or not provider.has_method("load"):
		return false
	var existing = _providers.get(source, null)
	if existing != null and existing != provider:
		if _has_source_references(source):
			return false
		unregister_provider(source, true)
	_providers[source] = provider
	return true


func unregister_provider(
	source: StringName,
	unload_assets: bool = true,
	force: bool = false
) -> bool:
	if not _providers.has(source):
		return false
	if not force and _has_source_references(source):
		return false
	_cancel_source_inflight(source)
	if unload_assets:
		for raw_path in _cache_sources.keys().duplicate():
			var path := String(raw_path)
			if StringName(_cache_sources.get(path, &"")) == source:
				_unload_cached(path)
	_providers.erase(source)
	return true


func exists(path: String, type_hint: String = "") -> bool:
	var resolved := _resolve(path)
	if resolved.is_empty():
		return false
	var source: StringName = resolved.source
	if source == &"":
		return ResourceLoader.exists(String(resolved.path), type_hint)
	var provider = _providers.get(source, null)
	if not _is_provider_valid(provider):
		return false
	if provider.has_method("exists"):
		return bool(provider.exists(String(resolved.path), type_hint))
	return true


func try_load(path: String, expected_type: Variant = null) -> Resource:
	if not exists(path):
		return null
	return _load_internal(path, expected_type, true)


func load(path: String, expected_type: Variant = null) -> Resource:
	return _load_internal(path, expected_type, true)


func load_async(path: String, expected_type: Variant = null) -> Resource:
	return await _load_async_internal(path, expected_type, true)


func acquire(path: String, expected_type: Variant = null) -> AssetHandle:
	var resolved := _resolve(path)
	if resolved.is_empty():
		return null
	var resource := _load_resolved(resolved, expected_type, false)
	if resource == null:
		return null
	var cache_path := String(resolved.cache_path)
	_ref_counts[cache_path] = int(_ref_counts.get(cache_path, 0)) + 1
	return AssetHandle.new(self, cache_path, resource)


func acquire_async(path: String, expected_type: Variant = null) -> AssetHandle:
	var resolved := _resolve(path)
	if resolved.is_empty():
		return null
	var resource := await _load_resolved_async(resolved, expected_type, false)
	if resource == null:
		return null
	var cache_path := String(resolved.cache_path)
	_ref_counts[cache_path] = int(_ref_counts.get(cache_path, 0)) + 1
	return AssetHandle.new(self, cache_path, resource)


func release(path: String) -> bool:
	var resolved := _resolve(path)
	var cache_path := String(resolved.get("cache_path", path))
	var refs := int(_ref_counts.get(cache_path, 0))
	if refs <= 0:
		return false
	refs -= 1
	if refs > 0:
		_ref_counts[cache_path] = refs
		return true
	_ref_counts.erase(cache_path)
	if not bool(_pinned.get(cache_path, false)):
		_unload_cached(cache_path)
	return true


func require(path: String, expected_type: Variant = null) -> Resource:
	var resource := self.load(path, expected_type)
	if resource == null:
		push_error("FAsset could not load required resource: %s" % path)
	return resource


func instantiate(path: String) -> Node:
	var scene := self.load(path, PackedScene) as PackedScene
	if scene == null:
		return null
	return scene.instantiate()


func reload(path: String, expected_type: Variant = null) -> Resource:
	unload(path)
	return self.load(path, expected_type)


func has_cached(path: String) -> bool:
	var resolved := _resolve(path)
	var cache_path := String(resolved.get("cache_path", path))
	var resource := _cache.get(cache_path, null) as Resource
	return resource != null and is_instance_valid(resource)


func cached_paths() -> Array[String]:
	var result: Array[String] = []
	for raw_path in _cache.keys():
		var path := String(raw_path)
		if has_cached(path):
			result.append(path)
	return result


func stats() -> Dictionary:
	var pinned_paths: Array[String] = []
	for raw_path in _pinned.keys():
		if bool(_pinned[raw_path]):
			pinned_paths.append(String(raw_path))
	return {
		"cached_count": cached_paths().size(),
		"inflight_count": _inflight.size(),
		"inflight_paths": _inflight.keys().duplicate(),
		"load_counts": _load_counts.duplicate(true),
		"references": _ref_counts.duplicate(true),
		"pinned_paths": pinned_paths,
		"providers": _providers.keys().duplicate(),
	}


func unload(path: String = "") -> void:
	if path.is_empty():
		for raw_path in _inflight.keys().duplicate():
			_cancel_inflight(String(raw_path))
		for raw_path in _cache.keys().duplicate():
			_unload_cached(String(raw_path))
		_load_counts.clear()
		return
	var resolved := _resolve(path)
	var cache_path := String(resolved.get("cache_path", path))
	_cancel_inflight(cache_path)
	_unload_cached(cache_path)


func _load_internal(path: String, expected_type: Variant, pin: bool) -> Resource:
	var resolved := _resolve(path)
	if resolved.is_empty():
		return null
	return _load_resolved(resolved, expected_type, pin)


func _load_async_internal(path: String, expected_type: Variant, pin: bool) -> Resource:
	var resolved := _resolve(path)
	if resolved.is_empty():
		return null
	return await _load_resolved_async(resolved, expected_type, pin)


func _load_resolved(resolved: Dictionary, expected_type: Variant, pin: bool) -> Resource:
	var cache_path := String(resolved.cache_path)
	var resource := _cache.get(cache_path, null) as Resource
	if resource == null or not is_instance_valid(resource):
		var provider = _provider_for(resolved)
		resource = _provider_load(resolved, expected_type, provider)
		if resource == null:
			return null
		_store_loaded(resolved, resource, provider)
	if not _validate_type(cache_path, resource, expected_type):
		return null
	if pin:
		_pinned[cache_path] = true
	return resource


func _load_resolved_async(
	resolved: Dictionary,
	expected_type: Variant,
	pin: bool
) -> Resource:
	var cache_path := String(resolved.cache_path)
	var resource := _cache.get(cache_path, null) as Resource
	if resource == null or not is_instance_valid(resource):
		var pending := _inflight.get(cache_path, null) as PendingLoad
		if pending != null:
			resource = await pending.wait()
		else:
			var provider = _provider_for(resolved)
			if StringName(resolved.source) != &"" and provider == null:
				return null
			pending = PendingLoad.new(StringName(resolved.source), provider)
			_inflight[cache_path] = pending
			resource = await _provider_load_async(resolved, expected_type, provider)
			if pending.cancelled:
				_release_resource(pending.provider, resource)
				resource = null
			elif resource != null:
				_store_loaded(resolved, resource, provider)
			pending.complete(resource)
			if _inflight.get(cache_path, null) == pending:
				_inflight.erase(cache_path)
		if resource == null:
			return null
	if not _validate_type(cache_path, resource, expected_type):
		return null
	if pin:
		_pinned[cache_path] = true
	return resource


func _load_native_async(path: String) -> Resource:
	var error := ResourceLoader.load_threaded_request(path)
	if error != OK:
		return ResourceLoader.load(path)
	var tree := Engine.get_main_loop() as SceneTree
	if tree == null:
		return ResourceLoader.load(path)
	var status := ResourceLoader.load_threaded_get_status(path)
	while status == ResourceLoader.THREAD_LOAD_IN_PROGRESS:
		await tree.process_frame
		status = ResourceLoader.load_threaded_get_status(path)
	if status != ResourceLoader.THREAD_LOAD_LOADED:
		return null
	return ResourceLoader.load_threaded_get(path)


func _provider_for(resolved: Dictionary) -> Variant:
	var source: StringName = resolved.source
	if source == &"":
		return null
	var provider = _providers.get(source, null)
	return provider if _is_provider_valid(provider) else null


func _provider_load(
	resolved: Dictionary,
	expected_type: Variant,
	provider: Variant
) -> Resource:
	var source: StringName = resolved.source
	if source == &"":
		return ResourceLoader.load(String(resolved.path))
	if not _is_provider_valid(provider):
		push_error("FAsset is missing provider '%s'." % source)
		return null
	return provider.load(String(resolved.path), expected_type) as Resource


func _provider_load_async(
	resolved: Dictionary,
	expected_type: Variant,
	provider: Variant
) -> Resource:
	if StringName(resolved.source) == &"":
		return await _load_native_async(String(resolved.path))
	if not _is_provider_valid(provider):
		push_error("FAsset is missing provider '%s'." % StringName(resolved.source))
		return null
	if provider.has_method("load_async"):
		return await provider.load_async(String(resolved.path), expected_type) as Resource
	return provider.load(String(resolved.path), expected_type) as Resource


func _store_loaded(
	resolved: Dictionary,
	resource: Resource,
	provider: Variant
) -> void:
	var cache_path := String(resolved.cache_path)
	_cache[cache_path] = resource
	_cache_sources[cache_path] = resolved.source
	_cache_providers[cache_path] = provider
	_load_counts[cache_path] = int(_load_counts.get(cache_path, 0)) + 1


func _validate_type(path: String, resource: Resource, expected_type: Variant) -> bool:
	if expected_type == null or is_instance_of(resource, expected_type):
		return true
	push_error(
		"FAsset '%s' expected %s but loaded %s."
		% [path, str(expected_type), resource.get_class()]
	)
	return false


func _unload_cached(cache_path: String) -> void:
	var resource := _cache.get(cache_path, null) as Resource
	var provider = _cache_providers.get(cache_path, null)
	_release_resource(provider, resource)
	_cache.erase(cache_path)
	_cache_sources.erase(cache_path)
	_cache_providers.erase(cache_path)
	_ref_counts.erase(cache_path)
	_pinned.erase(cache_path)


func _release_resource(provider: Variant, resource: Resource) -> void:
	if resource != null and _is_provider_valid(provider) and provider.has_method("release"):
		provider.release(resource)


func _is_provider_valid(provider: Variant) -> bool:
	if typeof(provider) != TYPE_OBJECT:
		return false
	return is_instance_valid(provider)


func _cancel_inflight(cache_path: String) -> void:
	var pending := _inflight.get(cache_path, null) as PendingLoad
	if pending != null:
		pending.cancelled = true
	_inflight.erase(cache_path)


func _cancel_source_inflight(source: StringName) -> void:
	for raw_path in _inflight.keys().duplicate():
		var cache_path := String(raw_path)
		var pending := _inflight.get(cache_path, null) as PendingLoad
		if pending != null and pending.source == source:
			_cancel_inflight(cache_path)


func _has_source_references(source: StringName) -> bool:
	for pending in _inflight.values():
		if pending is PendingLoad and pending.source == source and not pending.cancelled:
			return true
	for raw_path in _cache_sources.keys():
		var path := String(raw_path)
		if StringName(_cache_sources.get(path, &"")) != source:
			continue
		if int(_ref_counts.get(path, 0)) > 0:
			return true
	return false


func _resolve(path: String) -> Dictionary:
	var normalized := path.strip_edges()
	if normalized.is_empty():
		return {}
	if normalized.begins_with("res://") or normalized.begins_with("user://"):
		return {"cache_path": normalized, "source": &"", "path": normalized}
	var separator := normalized.find(":")
	if separator > 0 and separator < normalized.length() - 1:
		var source := StringName(normalized.left(separator))
		var source_path := normalized.substr(separator + 1).trim_prefix("//")
		if source == &"res" or source == &"user":
			var native_path := "%s://%s" % [source, source_path]
			return {"cache_path": native_path, "source": &"", "path": native_path}
		return {"cache_path": normalized, "source": source, "path": source_path}
	return {"cache_path": normalized, "source": &"", "path": normalized}
