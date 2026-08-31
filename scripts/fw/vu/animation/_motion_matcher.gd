class_name FMotionMatcher
extends RefCounted

const EPSILON: float = 0.000001
const DEFAULT_OPTIONS: Dictionary = {
	"velocity_weight": 1.6,
	"trajectory_weight": 1.0,
	"pose_weight": 0.75,
	"pose_velocity_weight": 0.08,
	"phase_weight": 0.25,
	"contact_weight": 0.35,
	"continuity_weight": 0.65,
	"switch_cost": 0.12,
	"minimum_hold_seconds": 0.1,
	"minimum_improvement": 0.04,
	"base_blend_seconds": 0.12,
	"maximum_blend_seconds": 0.22,
	"blend_score_scale": 0.035,
}

var _candidates: Array[Dictionary] = []
var _candidates_by_gait: Dictionary = {}
var _options: Dictionary = DEFAULT_OPTIONS.duplicate(true)
var _errors := PackedStringArray()
var _current_clip: StringName = &""
var _current_gait: StringName = &""
var _held_seconds: float = INF
var _last_result: Dictionary = {}


func configure(
	candidates: Array[Dictionary],
	options: Dictionary = {}
) -> PackedStringArray:
	_candidates.clear()
	_candidates_by_gait.clear()
	_errors.clear()
	_options = DEFAULT_OPTIONS.duplicate(true)
	for key: Variant in options:
		if _options.has(key):
			_options[key] = options[key]

	var ids: Dictionary = {}
	var pose_size := -1
	var pose_velocity_size := -1
	for source: Dictionary in candidates:
		var candidate := _normalize_candidate(source)
		var candidate_id := str(candidate.get("id", ""))
		if candidate_id.is_empty():
			_errors.append("motion candidate id cannot be empty")
			continue
		if ids.has(candidate_id):
			_errors.append("duplicate motion candidate id %s" % candidate_id)
			continue
		ids[candidate_id] = true
		if str(candidate.get("clip", "")).is_empty():
			_errors.append("motion candidate %s requires a clip" % candidate_id)
			continue
		if str(candidate.get("gait", "")).is_empty():
			_errors.append("motion candidate %s requires a gait" % candidate_id)
			continue
		var pose: PackedFloat32Array = candidate.get("pose", PackedFloat32Array())
		if pose_size < 0 and not pose.is_empty():
			pose_size = pose.size()
		if not pose.is_empty() and pose.size() != pose_size:
			_errors.append(
				"motion candidate %s pose size %d does not match %d"
				% [candidate_id, pose.size(), pose_size]
			)
			continue
		var pose_velocity: PackedFloat32Array = candidate.get(
			"pose_velocity",
			PackedFloat32Array()
		)
		if pose_velocity_size < 0 and not pose_velocity.is_empty():
			pose_velocity_size = pose_velocity.size()
		if not pose_velocity.is_empty() and pose_velocity.size() != pose_velocity_size:
			_errors.append(
				"motion candidate %s pose velocity size %d does not match %d"
				% [candidate_id, pose_velocity.size(), pose_velocity_size]
			)
			continue
		_candidates.append(candidate)
		var gait_key := StringName(str(candidate.get("gait", "")))
		if not _candidates_by_gait.has(gait_key):
			_candidates_by_gait[gait_key] = []
		(_candidates_by_gait[gait_key] as Array).append(candidate)
	if _candidates.is_empty():
		_errors.append("motion matcher requires at least one valid candidate")
	reset()
	return _errors.duplicate()


func reset() -> void:
	_current_clip = &""
	_current_gait = &""
	_held_seconds = INF
	_last_result.clear()


func is_ready() -> bool:
	return _errors.is_empty() and not _candidates.is_empty()


func errors() -> PackedStringArray:
	return _errors.duplicate()


func candidate_count() -> int:
	return _candidates.size()


func last_result() -> Dictionary:
	return _last_result.duplicate(true)


func match_motion(query: Dictionary, delta: float = 0.0) -> Dictionary:
	if not is_ready():
		return {}
	_held_seconds += maxf(delta, 0.0)
	var gait := StringName(str(query.get("gait", "idle")))
	var actual_clip := StringName(str(query.get("current_clip", "")))
	var gait_changed := not _current_gait.is_empty() and gait != _current_gait
	var gait_candidates_value: Variant = _candidates_by_gait.get(gait, [])
	if not gait_candidates_value is Array:
		return {}
	var gait_candidates: Array = gait_candidates_value
	var selected_candidate: Dictionary = {}
	var selected_breakdown: Dictionary = {}
	var selected_score := INF
	var current_candidate: Dictionary = {}
	var current_breakdown: Dictionary = {}
	var current_score := INF
	for candidate_value: Variant in gait_candidates:
		if not candidate_value is Dictionary:
			continue
		var candidate: Dictionary = candidate_value
		var breakdown := _score_candidate(candidate, query)
		var score := float(breakdown.get("total", INF))
		if score < selected_score:
			selected_candidate = candidate
			selected_breakdown = breakdown
			selected_score = score
		if StringName(str(candidate.get("clip", ""))) == actual_clip and score < current_score:
			current_candidate = candidate
			current_breakdown = breakdown
			current_score = score
	if selected_candidate.is_empty():
		return {}
	var selected_clip := StringName(str(selected_candidate.get("clip", "")))
	if (
		selected_clip != actual_clip
		and not current_candidate.is_empty()
		and not gait_changed
	):
		var improvement := current_score - selected_score
		var minimum_improvement := float(_options.minimum_improvement)
		if (
			_held_seconds < float(_options.minimum_hold_seconds)
			or improvement < minimum_improvement
		):
			selected_candidate = current_candidate
			selected_breakdown = current_breakdown
			selected_score = current_score
			selected_clip = StringName(str(selected_candidate.get("clip", "")))

	var changed := selected_clip != actual_clip
	if changed:
		_held_seconds = 0.0
	_current_clip = selected_clip
	_current_gait = gait
	var score := selected_score
	var blend_seconds := clampf(
		float(_options.base_blend_seconds)
		+ sqrt(maxf(score, 0.0)) * float(_options.blend_score_scale),
		0.0,
		float(_options.maximum_blend_seconds)
	)
	_last_result = selected_candidate.duplicate(true)
	_last_result["score"] = score
	_last_result["score_breakdown"] = selected_breakdown.duplicate(true)
	_last_result["changed"] = changed
	_last_result["gait_changed"] = gait_changed
	_last_result["blend_seconds"] = blend_seconds
	_last_result["held_seconds"] = _held_seconds
	_last_result["candidate_count"] = gait_candidates.size()
	return _last_result.duplicate(true)


func _score_candidate(candidate: Dictionary, query: Dictionary) -> Dictionary:
	var velocity_error := _vector2_error(
		candidate.get("velocity", Vector2.ZERO) as Vector2,
		query.get("velocity", Vector2.ZERO) as Vector2
	)
	var trajectory_error := _trajectory_error(
		candidate.get("trajectory", PackedVector2Array()) as PackedVector2Array,
		query.get("trajectory", PackedVector2Array()) as PackedVector2Array
	)
	var pose_error := _pose_error(
		candidate.get("pose", PackedFloat32Array()) as PackedFloat32Array,
		query.get("pose", PackedFloat32Array()) as PackedFloat32Array
	)
	var pose_velocity_error := _pose_error(
		candidate.get("pose_velocity", PackedFloat32Array()) as PackedFloat32Array,
		query.get("pose_velocity", PackedFloat32Array()) as PackedFloat32Array
	)
	var phase_error := _cyclic_distance(
		float(candidate.get("phase", 0.0)),
		float(query.get("phase", 0.0))
	)
	phase_error *= phase_error
	var contact_error := _contact_error(
		int(candidate.get("contacts", 0)),
		int(query.get("contacts", 0))
	)
	var continuity_error := _continuity_error(candidate, query)
	var switching := (
		not str(query.get("current_clip", "")).is_empty()
		and str(candidate.get("clip", "")) != str(query.get("current_clip", ""))
	)
	var switch_error := float(_options.switch_cost) if switching else 0.0
	var total := (
		velocity_error * float(_options.velocity_weight)
		+ trajectory_error * float(_options.trajectory_weight)
		+ pose_error * float(_options.pose_weight)
		+ pose_velocity_error * float(_options.pose_velocity_weight)
		+ phase_error * float(_options.phase_weight)
		+ contact_error * float(_options.contact_weight)
		+ continuity_error * float(_options.continuity_weight)
		+ switch_error
	)
	return {
		"total": total,
		"velocity": velocity_error,
		"trajectory": trajectory_error,
		"pose": pose_error,
		"pose_velocity": pose_velocity_error,
		"phase": phase_error,
		"contact": contact_error,
		"continuity": continuity_error,
		"switch": switch_error,
	}


func _continuity_error(candidate: Dictionary, query: Dictionary) -> float:
	if str(candidate.get("clip", "")) != str(query.get("current_clip", "")):
		return 0.0
	var duration := maxf(float(candidate.get("duration", 0.0)), EPSILON)
	var expected_time := float(query.get("current_time", 0.0)) + float(
		query.get("delta", 0.0)
	)
	var expected_phase := fposmod(expected_time, duration) / duration
	var distance := _cyclic_distance(
		float(candidate.get("phase", 0.0)),
		expected_phase
	)
	return distance * distance


func _normalize_candidate(source: Dictionary) -> Dictionary:
	var candidate := source.duplicate(true)
	candidate["id"] = str(candidate.get("id", ""))
	candidate["clip"] = str(candidate.get("clip", ""))
	candidate["gait"] = str(candidate.get("gait", ""))
	candidate["time"] = maxf(float(candidate.get("time", 0.0)), 0.0)
	candidate["duration"] = maxf(float(candidate.get("duration", 0.0)), EPSILON)
	candidate["phase"] = fposmod(float(candidate.get("phase", 0.0)), 1.0)
	candidate["velocity"] = candidate.get("velocity", Vector2.ZERO) as Vector2
	candidate["trajectory"] = _as_vector2_array(candidate.get("trajectory", []))
	candidate["pose"] = _as_float_array(candidate.get("pose", []))
	candidate["pose_velocity"] = _as_float_array(candidate.get("pose_velocity", []))
	candidate["contacts"] = int(candidate.get("contacts", 0))
	return candidate


func _as_vector2_array(value: Variant) -> PackedVector2Array:
	if value is PackedVector2Array:
		return (value as PackedVector2Array).duplicate()
	var result := PackedVector2Array()
	if value is Array:
		for item: Variant in value:
			if item is Vector2:
				result.append(item as Vector2)
	return result


func _as_float_array(value: Variant) -> PackedFloat32Array:
	if value is PackedFloat32Array:
		return (value as PackedFloat32Array).duplicate()
	var result := PackedFloat32Array()
	if value is Array:
		for item: Variant in value:
			result.append(float(item))
	return result


func _vector2_error(a: Vector2, b: Vector2) -> float:
	return a.distance_squared_to(b)


func _trajectory_error(a: PackedVector2Array, b: PackedVector2Array) -> float:
	var count := mini(a.size(), b.size())
	if count <= 0:
		return 0.0
	var total := 0.0
	for index: int in range(count):
		total += a[index].distance_squared_to(b[index])
	return total / float(count)


func _pose_error(a: PackedFloat32Array, b: PackedFloat32Array) -> float:
	var count := mini(a.size(), b.size())
	if count <= 0:
		return 0.0
	var total := 0.0
	for index: int in range(count):
		var difference := a[index] - b[index]
		total += difference * difference
	return total / float(count)


func _contact_error(a: int, b: int) -> float:
	var bits := a ^ b
	var mismatches := 0
	while bits != 0:
		mismatches += bits & 1
		bits >>= 1
	return float(mismatches) * 0.5


func _cyclic_distance(a: float, b: float) -> float:
	var distance := absf(fposmod(a, 1.0) - fposmod(b, 1.0))
	return minf(distance, 1.0 - distance)
