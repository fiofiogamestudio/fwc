class_name FSkeletonAppearanceModifier
extends SkeletonModifier3D

const DEFAULT_MIN_SCALE := Vector3(0.75, 0.75, 0.75)
const DEFAULT_MAX_SCALE := Vector3(1.25, 1.25, 1.25)

var _profile: Dictionary = {}
var _bone_indices: Dictionary = {}
var _base_scales: Dictionary = {}
var _adjustments: Dictionary = {}
var _errors := PackedStringArray()
var _configured: bool = false
var _processed_frame_count: int = 0
var _last_process_usec: int = 0
var _max_process_usec: int = 0
var _clamp_count: int = 0
var _ignored_adjustment_count: int = 0


func configure(profile: Dictionary) -> PackedStringArray:
	if _configured:
		_restore_base_scales()
	_profile = profile.duplicate(true)
	_errors.clear()
	_bone_indices.clear()
	_base_scales.clear()
	_adjustments.clear()
	var skeleton := _resolve_skeleton()
	if skeleton == null:
		_errors.append("skeleton appearance modifier must be a child of Skeleton3D")
	else:
		var bones_value: Variant = _profile.get("bones", {})
		var bones: Dictionary = bones_value if bones_value is Dictionary else {}
		if bones.is_empty():
			_errors.append("skeleton appearance profile requires bones")
		_validate_limits(bones)
		for semantic_value: Variant in bones:
			var semantic := str(semantic_value)
			var bone_name := str(bones[semantic_value])
			if semantic.is_empty() or bone_name.is_empty():
				_errors.append("skeleton appearance bone mappings cannot be empty")
				continue
			var bone_index := skeleton.find_bone(bone_name)
			if bone_index < 0:
				_errors.append("missing appearance bone %s -> %s" % [semantic, bone_name])
				continue
			_bone_indices[semantic] = bone_index
			_base_scales[semantic] = skeleton.get_bone_pose_scale(bone_index)
	_configured = _errors.is_empty()
	active = _configured
	influence = 1.0
	return _errors.duplicate()


func submit_adjustments(adjustments: Dictionary) -> void:
	_adjustments.clear()
	for semantic_value: Variant in adjustments:
		var semantic := str(semantic_value)
		if not _bone_indices.has(semantic):
			_ignored_adjustment_count += 1
			continue
		var value: Variant = adjustments[semantic_value]
		if value is Vector3:
			_adjustments[semantic] = value
		elif value is Dictionary and value.get("scale", null) is Vector3:
			_adjustments[semantic] = {"scale": value.get("scale")}
		else:
			_ignored_adjustment_count += 1


func clear_adjustments() -> void:
	_adjustments.clear()
	_restore_base_scales()


func solve_now() -> bool:
	if not _configured:
		return false
	_apply_adjustments()
	return true


func is_configured() -> bool:
	return _configured


func errors() -> PackedStringArray:
	return _errors.duplicate()


func metrics() -> Dictionary:
	return {
		"configured": _configured,
		"errors": _errors.duplicate(),
		"processed_frame_count": _processed_frame_count,
		"last_process_usec": _last_process_usec,
		"max_process_usec": _max_process_usec,
		"clamp_count": _clamp_count,
		"ignored_adjustment_count": _ignored_adjustment_count,
		"adjustment_count": _adjustments.size(),
	}


func _process_modification_with_delta(_delta: float) -> void:
	if _configured:
		_apply_adjustments()


func _apply_adjustments() -> void:
	var skeleton := _resolve_skeleton()
	if skeleton == null:
		return
	var started_usec := Time.get_ticks_usec()
	for semantic_value: Variant in _bone_indices:
		var semantic := str(semantic_value)
		var bone_index := int(_bone_indices[semantic_value])
		var requested := _adjustment_scale(semantic)
		var limits := _limits(semantic)
		var clamped := Vector3(
			clampf(requested.x, limits[0].x, limits[1].x),
			clampf(requested.y, limits[0].y, limits[1].y),
			clampf(requested.z, limits[0].z, limits[1].z)
		)
		if not clamped.is_equal_approx(requested):
			_clamp_count += 1
		var base: Vector3 = _base_scales.get(semantic, Vector3.ONE)
		skeleton.set_bone_pose_scale(bone_index, base * clamped)
	skeleton.force_update_all_bone_transforms()
	_last_process_usec = maxi(Time.get_ticks_usec() - started_usec, 0)
	_max_process_usec = maxi(_max_process_usec, _last_process_usec)
	_processed_frame_count += 1


func _adjustment_scale(semantic: String) -> Vector3:
	var value: Variant = _adjustments.get(semantic, Vector3.ONE)
	if value is Vector3:
		return value
	if value is Dictionary:
		var scale_value: Variant = value.get("scale", Vector3.ONE)
		return scale_value if scale_value is Vector3 else Vector3.ONE
	return Vector3.ONE


func _limits(semantic: String) -> Array[Vector3]:
	var limits_value: Variant = _profile.get("limits", {})
	var limits: Dictionary = limits_value if limits_value is Dictionary else {}
	var entry_value: Variant = limits.get(semantic, {})
	var entry: Dictionary = entry_value if entry_value is Dictionary else {}
	var minimum_value: Variant = entry.get("min_scale", DEFAULT_MIN_SCALE)
	var maximum_value: Variant = entry.get("max_scale", DEFAULT_MAX_SCALE)
	var minimum: Vector3 = (
		minimum_value if minimum_value is Vector3 else DEFAULT_MIN_SCALE
	)
	var maximum: Vector3 = (
		maximum_value if maximum_value is Vector3 else DEFAULT_MAX_SCALE
	)
	return [minimum, maximum]


func _validate_limits(bones: Dictionary) -> void:
	var limits_value: Variant = _profile.get("limits", {})
	if not limits_value is Dictionary:
		_errors.append("skeleton appearance limits must be a dictionary")
		return
	var limits := limits_value as Dictionary
	for semantic_value: Variant in limits:
		var semantic := str(semantic_value)
		if not bones.has(semantic_value) and not bones.has(semantic):
			_errors.append("appearance limits reference unknown semantic bone: %s" % semantic)
			continue
		var entry_value: Variant = limits[semantic_value]
		if not entry_value is Dictionary:
			_errors.append("appearance limits for %s must be a dictionary" % semantic)
			continue
		var entry := entry_value as Dictionary
		var minimum_value: Variant = entry.get("min_scale", DEFAULT_MIN_SCALE)
		var maximum_value: Variant = entry.get("max_scale", DEFAULT_MAX_SCALE)
		if not minimum_value is Vector3 or not maximum_value is Vector3:
			_errors.append("appearance limits for %s require Vector3 min_scale/max_scale" % semantic)
			continue
		var minimum := minimum_value as Vector3
		var maximum := maximum_value as Vector3
		if minimum.x <= 0.0 or minimum.y <= 0.0 or minimum.z <= 0.0:
			_errors.append("appearance minimum scale for %s must be positive" % semantic)
		if maximum.x < minimum.x or maximum.y < minimum.y or maximum.z < minimum.z:
			_errors.append("appearance maximum scale for %s must not be below minimum" % semantic)


func _restore_base_scales() -> void:
	var skeleton := _resolve_skeleton()
	if skeleton == null:
		return
	for semantic_value: Variant in _bone_indices:
		var semantic := str(semantic_value)
		skeleton.set_bone_pose_scale(
			int(_bone_indices[semantic_value]),
			_base_scales.get(semantic, Vector3.ONE)
		)
	skeleton.force_update_all_bone_transforms()


func _resolve_skeleton() -> Skeleton3D:
	var skeleton := get_skeleton()
	if skeleton != null:
		return skeleton
	return get_parent() as Skeleton3D
