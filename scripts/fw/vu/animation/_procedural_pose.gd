class_name FProceduralPose
extends RefCounted

const EASING_LINEAR: StringName = &"linear"
const EASING_SMOOTH: StringName = &"smooth"
const EASING_IN: StringName = &"in"
const EASING_OUT: StringName = &"out"
const EASING_IN_CUBIC: StringName = &"ease_in_cubic"
const EASING_IN_QUINT: StringName = &"ease_in_quint"
const EASING_OUT_CUBIC: StringName = &"ease_out_cubic"
const EASING_OUT_BACK: StringName = &"ease_out_back"

const EASING_NAMES: Array[StringName] = [
	EASING_LINEAR,
	EASING_SMOOTH,
	EASING_IN,
	EASING_OUT,
	EASING_IN_CUBIC,
	EASING_IN_QUINT,
	EASING_OUT_CUBIC,
	EASING_OUT_BACK,
]


static func ease(ratio: float, easing: StringName = EASING_SMOOTH) -> float:
	return _ease_ratio(ratio, easing)


static func _ease_ratio(ratio: float, easing: StringName) -> float:
	var value := clampf(ratio, 0.0, 1.0)
	match easing:
		EASING_LINEAR:
			return value
		EASING_IN:
			return value * value
		EASING_OUT:
			var inverse := 1.0 - value
			return 1.0 - inverse * inverse
		EASING_IN_CUBIC:
			return value * value * value
		EASING_IN_QUINT:
			var squared := value * value
			return squared * squared * value
		EASING_OUT_CUBIC:
			var inverse := 1.0 - value
			return 1.0 - inverse * inverse * inverse
		EASING_OUT_BACK:
			const C1 := 1.70158
			const C3 := C1 + 1.0
			var shifted := value - 1.0
			return 1.0 + C3 * shifted * shifted * shifted + C1 * shifted * shifted
		_:
			return value * value * (3.0 - 2.0 * value)


static func group_tracks(keys: Array, target_field: String = "target") -> Dictionary:
	var tracks: Dictionary = {}
	for value: Variant in keys:
		if not value is Dictionary:
			continue
		var key := value as Dictionary
		var target := str(key.get(target_field, "")).strip_edges()
		if target.is_empty():
			continue
		if not tracks.has(target):
			tracks[target] = []
		(tracks[target] as Array).append(key.duplicate(true))
	for target: Variant in tracks:
		(tracks[target] as Array).sort_custom(
			func(left: Dictionary, right: Dictionary) -> bool:
				return _key_time(left) < _key_time(right)
		)
	return tracks


static func sample_track(keys: Array, time: float) -> Dictionary:
	if keys.is_empty():
		return _identity_sample()
	var previous := _normalized_key(keys[0])
	if time <= float(previous.time):
		return _sample_from_key(previous)
	for index: int in range(1, keys.size()):
		var next := _normalized_key(keys[index])
		if time > float(next.time):
			previous = next
			continue
		var span := maxf(float(next.time) - float(previous.time), 0.000001)
		var ratio := _ease_ratio(
			(time - float(previous.time)) / span,
			StringName(next.easing)
		)
		var position: Vector3 = previous.position.lerp(next.position, ratio)
		var rotation: Quaternion = previous.rotation.slerp(next.rotation, ratio).normalized()
		var scale: Vector3 = previous.scale.lerp(next.scale, ratio)
		return _make_sample(position, rotation, scale)
	return _sample_from_key(previous)


static func sample_transform(keys: Array, time: float) -> Transform3D:
	return sample_track(keys, time).get("transform", Transform3D.IDENTITY) as Transform3D


static func sample_track_euler(keys: Array, time: float) -> Dictionary:
	var sample := sample_track(keys, time)
	return {
		"position": sample.get("position", Vector3.ZERO),
		"rotation_degrees": sample.get("rotation_degrees", Vector3.ZERO),
		"scale": sample.get("scale", Vector3.ONE),
	}


static func required_subsamples(
	from_pose: Dictionary,
	to_pose: Dictionary,
	max_angle_degrees: float = 10.0,
	max_distance: float = 0.12,
	max_samples: int = 4
) -> int:
	assert(max_angle_degrees > 0.0)
	assert(max_distance > 0.0)
	assert(max_samples > 0)
	var from_rotation: Quaternion = from_pose.get("rotation", Quaternion.IDENTITY)
	var to_rotation: Quaternion = to_pose.get("rotation", Quaternion.IDENTITY)
	from_rotation = from_rotation.normalized()
	to_rotation = to_rotation.normalized()
	var rotation_dot := clampf(absf(from_rotation.dot(to_rotation)), 0.0, 1.0)
	var angle_delta := rad_to_deg(2.0 * acos(rotation_dot))
	var from_position: Vector3 = from_pose.get("position", Vector3.ZERO)
	var to_position: Vector3 = to_pose.get("position", Vector3.ZERO)
	var angle_samples := int(ceil(angle_delta / max_angle_degrees))
	var distance_samples := int(ceil(from_position.distance_to(to_position) / max_distance))
	return clampi(maxi(angle_samples, distance_samples), 1, max_samples)


static func validate_keys(keys: Array, require_target: bool = true) -> PackedStringArray:
	var errors := PackedStringArray()
	var last_time_by_target: Dictionary = {}
	for index: int in range(keys.size()):
		if not keys[index] is Dictionary:
			errors.append("key[%d] must be a Dictionary" % index)
			continue
		var key := keys[index] as Dictionary
		var target := str(key.get("target", "")).strip_edges()
		if require_target and target.is_empty():
			errors.append("key[%d].target cannot be empty" % index)
		var time_value := _key_time(key, -1.0)
		if time_value < 0.0:
			errors.append("key[%d].time must be non-negative" % index)
		var order_key := target if not target.is_empty() else "_"
		if last_time_by_target.has(order_key) and time_value <= float(last_time_by_target[order_key]):
			errors.append("key[%d].time must increase within target '%s'" % [index, target])
		last_time_by_target[order_key] = time_value
		var easing := StringName(str(key.get("easing", EASING_SMOOTH)))
		if easing not in EASING_NAMES:
			errors.append("key[%d].easing '%s' is unsupported" % [index, easing])
		for field: String in ["position", "rotation_degrees", "scale"]:
			if key.has(field) and not key[field] is Vector3:
				errors.append("key[%d].%s must be a Vector3" % [index, field])
		var scale: Vector3 = key.get("scale", Vector3.ONE)
		if scale.x <= 0.0 or scale.y <= 0.0 or scale.z <= 0.0:
			errors.append("key[%d].scale must stay positive" % index)
	return errors


static func _normalized_key(value: Variant) -> Dictionary:
	var source: Dictionary = value if value is Dictionary else {}
	var rotation_degrees: Vector3 = source.get("rotation_degrees", Vector3.ZERO)
	return {
		"time": maxf(_key_time(source), 0.0),
		"position": source.get("position", Vector3.ZERO) as Vector3,
		"rotation": Quaternion.from_euler(Vector3(
			deg_to_rad(rotation_degrees.x),
			deg_to_rad(rotation_degrees.y),
			deg_to_rad(rotation_degrees.z)
		)).normalized(),
		"scale": source.get("scale", Vector3.ONE) as Vector3,
		"easing": StringName(str(source.get("easing", EASING_SMOOTH))),
	}


static func _key_time(key: Dictionary, fallback: float = 0.0) -> float:
	if key.has("tick"):
		return float(key.get("tick", fallback))
	return float(key.get("time", fallback))


static func _sample_from_key(key: Dictionary) -> Dictionary:
	return _make_sample(key.position, key.rotation, key.scale)


static func _identity_sample() -> Dictionary:
	return _make_sample(Vector3.ZERO, Quaternion.IDENTITY, Vector3.ONE)


static func _make_sample(
	position: Vector3,
	rotation: Quaternion,
	scale: Vector3
) -> Dictionary:
	var basis := Basis(rotation).scaled(scale)
	var rotation_radians := rotation.get_euler()
	return {
		"position": position,
		"rotation": rotation,
		"rotation_degrees": Vector3(
			rad_to_deg(rotation_radians.x),
			rad_to_deg(rotation_radians.y),
			rad_to_deg(rotation_radians.z)
		),
		"scale": scale,
		"transform": Transform3D(basis, position),
	}
