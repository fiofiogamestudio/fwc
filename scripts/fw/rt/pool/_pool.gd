class_name FPool
extends RefCounted

var _prefabs: Dictionary = {}
var _generations: Dictionary = {}
var _free: Dictionary = {}
var _active: Dictionary = {}
var _max_free: Dictionary = {}
var _default_parent: Node = null


func setup(default_parent: Node) -> void:
	_default_parent = default_parent


func register_prefab(
	key: String,
	packed_scene: PackedScene,
	warmup: int = 0,
	max_free: int = -1
) -> bool:
	if key.is_empty():
		push_error("FPool prefab key cannot be empty.")
		return false
	if packed_scene == null:
		push_error("FPool cannot register empty prefab for key: %s" % key)
		return false

	var changed: bool = not _prefabs.has(key) or _prefabs[key] != packed_scene
	if changed and _prefabs.has(key):
		_flush_bucket(key)
	_generations[key] = int(_generations.get(key, 0)) + (1 if changed else 0)

	_prefabs[key] = packed_scene
	_max_free[key] = max_free
	if not _free.has(key):
		_free[key] = []
	_trim_bucket(key)
	return self.warmup(key, warmup)


func warmup(key: String, count: int) -> bool:
	if not _prefabs.has(key):
		push_error("FPool missing prefab for key: %s" % key)
		return false
	var bucket: Array = _free.get(key, [])
	var target_count := maxi(count, 0)
	var limit := int(_max_free.get(key, -1))
	if limit >= 0:
		target_count = mini(target_count, limit)
	while bucket.size() < target_count:
		var node: Node = _instantiate(key)
		if node == null:
			return false
		bucket.append(node)
	_free[key] = bucket
	return true


func spawn(
	key: String,
	parent: Node = null,
	owner: Variant = null,
	props: Dictionary = {}
) -> Node:
	if not _prefabs.has(key):
		push_error("FPool missing prefab for key: %s" % key)
		return null

	var bucket: Array = _free.get(key, [])
	var node: Node = _take_free_node(key, bucket)
	if node == null:
		node = _instantiate(key)
		if node == null:
			return null
	_free[key] = bucket

	var target_parent: Node = parent if parent != null else _default_parent
	if target_parent != null:
		if not is_instance_valid(target_parent):
			push_error("FPool cannot spawn '%s' under an invalid parent." % key)
			bucket.append(node)
			_free[key] = bucket
			return null
		target_parent.add_child(node)
	_active[node.get_instance_id()] = node
	if node.has_method("setup"):
		node.setup(owner, props)
	return node


func recycle(node: Node) -> bool:
	if node == null or not is_instance_valid(node):
		return false
	var instance_id: int = node.get_instance_id()
	if not _active.has(instance_id) or _active[instance_id] != node:
		push_warning("FPool ignored recycle for a node that is not active.")
		return false
	_active.erase(instance_id)
	if not node.has_meta("_pool_key"):
		if node.has_method("clear"):
			node.clear()
		node.queue_free()
		return true

	var key: String = String(node.get_meta("_pool_key"))
	if node.has_method("clear"):
		node.clear()
	if node.get_parent():
		node.get_parent().remove_child(node)
	var node_generation: int = int(node.get_meta("_pool_generation", 0))
	var current_generation: int = int(_generations.get(key, 0))
	if not _prefabs.has(key) or node_generation != current_generation:
		if not node.is_queued_for_deletion():
			node.queue_free()
		return true
	if not _free.has(key):
		_free[key] = []
	var limit := int(_max_free.get(key, -1))
	if limit >= 0 and _free[key].size() >= limit:
		node.queue_free()
		return true
	_free[key].append(node)
	return true


func owns(node: Node) -> bool:
	return (
		node != null
		and is_instance_valid(node)
		and _active.get(node.get_instance_id(), null) == node
	)


func flush(key: String = "") -> void:
	if key != "":
		_flush_active(key)
		_flush_bucket(key)
		return

	_flush_active()
	for bucket_key in _free.keys():
		_flush_bucket(String(bucket_key))
	_free.clear()
	_active.clear()
	_prefabs.clear()
	_generations.clear()
	_max_free.clear()


func unregister_prefab(key: String, recycle_active: bool = false) -> void:
	if recycle_active:
		_flush_active(key)
	_flush_bucket(key)
	_prefabs.erase(key)
	_generations.erase(key)
	_max_free.erase(key)


func clear_active() -> void:
	_flush_active()


func stats(key: String = "") -> Dictionary:
	if key != "":
		return {
			"registered": _prefabs.has(key),
			"active": _active_count(key),
			"free": _valid_free_count(key),
			"max_free": int(_max_free.get(key, -1)),
		}
	var free_total := 0
	for bucket_key in _free.keys():
		free_total += _valid_free_count(String(bucket_key))
	return {
		"registered": _prefabs.size(),
		"active": _active.size(),
		"free": free_total,
	}


func _instantiate(key: String) -> Node:
	var packed_scene: PackedScene = _prefabs[key]
	var node: Node = packed_scene.instantiate()
	if node == null:
		push_error("FPool failed to instantiate prefab: %s" % key)
		return null
	node.set_meta("_pool_key", key)
	node.set_meta("_pool_generation", int(_generations.get(key, 0)))
	return node


func _take_free_node(key: String, bucket: Array) -> Node:
	var generation: int = int(_generations.get(key, 0))
	while not bucket.is_empty():
		var candidate: Variant = bucket.pop_back()
		if candidate == null or not is_instance_valid(candidate):
			continue
		var node: Node = candidate
		if node.is_queued_for_deletion():
			continue
		if String(node.get_meta("_pool_key", "")) != key:
			node.queue_free()
			continue
		if int(node.get_meta("_pool_generation", 0)) != generation:
			node.queue_free()
			continue
		if _active.has(node.get_instance_id()):
			push_warning("FPool discarded a free bucket entry that is still active.")
			continue
		return node
	return null


func _flush_bucket(key: String) -> void:
	var bucket: Array = _free.get(key, [])
	for item in bucket:
		if item and is_instance_valid(item):
			item.queue_free()
	_free.erase(key)


func _trim_bucket(key: String) -> void:
	var limit := int(_max_free.get(key, -1))
	if limit < 0:
		return
	var bucket: Array = _free.get(key, [])
	while bucket.size() > limit:
		var item: Variant = bucket.pop_back()
		if item is Node and is_instance_valid(item):
			var node := item as Node
			if node.get_parent() != null:
				node.get_parent().remove_child(node)
			node.queue_free()
	_free[key] = bucket


func _flush_active(key: String = "") -> void:
	for raw_id in _active.keys():
		var raw_node: Variant = _active[raw_id]
		if not is_instance_valid(raw_node) or not (raw_node is Node):
			_active.erase(raw_id)
			continue
		var node: Node = raw_node
		if key != "" and String(node.get_meta("_pool_key", "")) != key:
			continue
		if node.has_method("clear"):
			node.clear()
		if not node.is_queued_for_deletion():
			node.queue_free()
		_active.erase(raw_id)


func _active_count(key: String) -> int:
	var count := 0
	for raw_node in _active.values():
		if (
			raw_node is Node
			and is_instance_valid(raw_node)
			and String(raw_node.get_meta("_pool_key", "")) == key
		):
			count += 1
	return count


func _valid_free_count(key: String) -> int:
	var count := 0
	for item in _free.get(key, []):
		if item is Node and is_instance_valid(item) and not item.is_queued_for_deletion():
			count += 1
	return count
