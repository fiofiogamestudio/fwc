class_name FBodyOrientation
extends RefCounted

const POLICY_UPPER: StringName = &"upper"
const POLICY_COMMITTED: StringName = &"committed"
const POLICY_FULL_BODY: StringName = &"full_body"
const POLICIES: Array[StringName] = [
	POLICY_UPPER,
	POLICY_COMMITTED,
	POLICY_FULL_BODY,
]

const LOCOMOTION_FRAME_VELOCITY: StringName = &"velocity"
const LOCOMOTION_FRAME_AIM: StringName = &"aim"
const LOCOMOTION_FRAMES: Array[StringName] = [
	LOCOMOTION_FRAME_VELOCITY,
	LOCOMOTION_FRAME_AIM,
]

const DEFAULT_LOWER_TURN_SPEED_DEGREES: float = 720.0
const DEFAULT_RECENTER_SPEED_DEGREES: float = 420.0
const DEFAULT_SOFT_TWIST_DEGREES: float = 55.0
const DEFAULT_HARD_TWIST_DEGREES: float = 80.0
const DEFAULT_UPPER_RESPONSE_SECONDS: float = 0.12
const DEFAULT_UPPER_RETURN_SECONDS: float = 0.16


static func resolve(
	current_lower_yaw: float,
	move_yaw: float,
	aim_yaw: float,
	is_moving: bool,
	upper_active: bool,
	policy: StringName = POLICY_UPPER,
	delta: float = 0.0,
	options: Dictionary = {}
) -> Dictionary:
	var resolved_policy := normalize_policy(policy)
	var lower_yaw := _wrap_angle(current_lower_yaw)
	var resolved_aim_yaw := _wrap_angle(aim_yaw)
	var resolved_move_yaw := _wrap_angle(move_yaw)
	var locomotion_frame := normalize_locomotion_frame(StringName(str(
		options.get("locomotion_frame", LOCOMOTION_FRAME_VELOCITY)
	)))
	var step_seconds := maxf(delta, 0.0)
	var lower_turn_speed := deg_to_rad(maxf(float(
		options.get("lower_turn_speed_degrees", DEFAULT_LOWER_TURN_SPEED_DEGREES)
	), 0.0))
	var recenter_speed := deg_to_rad(maxf(float(
		options.get("recenter_speed_degrees", DEFAULT_RECENTER_SPEED_DEGREES)
	), 0.0))
	var soft_twist := deg_to_rad(clampf(float(
		options.get("soft_twist_degrees", DEFAULT_SOFT_TWIST_DEGREES)
	), 0.0, 180.0))
	var hard_twist := deg_to_rad(clampf(float(
		options.get("hard_twist_degrees", DEFAULT_HARD_TWIST_DEGREES)
	), rad_to_deg(soft_twist), 180.0))
	var commit_weight := clampf(float(options.get("lower_commit_weight", 0.0)), 0.0, 1.0)
	var current_upper_twist := clampf(
		float(options.get("current_upper_twist_yaw", 0.0)),
		-hard_twist,
		hard_twist
	)
	var upper_response_seconds := maxf(float(
		options.get("upper_response_seconds", DEFAULT_UPPER_RESPONSE_SECONDS)
	), 0.0)
	var upper_return_seconds := maxf(float(
		options.get("upper_return_seconds", DEFAULT_UPPER_RETURN_SECONDS)
	), 0.0)

	# Aim-relative locomotion keeps the character frame on authoritative aim.
	# The caller expresses strafing/backpedalling through local movement features,
	# rather than rotating the pelvis away from the action frame.
	if locomotion_frame == LOCOMOTION_FRAME_AIM:
		lower_yaw = (
			resolved_aim_yaw
			if bool(options.get("aim_locked", true))
			else _approach_angle(
				lower_yaw,
				resolved_aim_yaw,
				lower_turn_speed * step_seconds
			)
		)
		var returning_twist := _smooth_angle(
			current_upper_twist,
			0.0,
			step_seconds,
			upper_return_seconds
		)
		return _result(
			lower_yaw,
			resolved_aim_yaw,
			returning_twist,
			0.0,
			resolved_policy,
			false,
			_angle_delta(lower_yaw, resolved_aim_yaw)
		)

	if not upper_active:
		var idle_target := resolved_move_yaw if is_moving else resolved_aim_yaw
		lower_yaw = _approach_angle(lower_yaw, idle_target, lower_turn_speed * step_seconds)
		var returning_twist := _smooth_angle(
			current_upper_twist,
			0.0,
			step_seconds,
			upper_return_seconds
		)
		return _result(
			lower_yaw,
			lower_yaw,
			returning_twist,
			0.0,
			resolved_policy,
			false,
			0.0
		)

	if resolved_policy == POLICY_FULL_BODY:
		var full_body_turned := absf(_angle_delta(lower_yaw, resolved_aim_yaw)) > 0.0001
		return _result(
			resolved_aim_yaw,
			resolved_aim_yaw,
			0.0,
			0.0,
			resolved_policy,
			full_body_turned,
			0.0
		)

	var lower_target := resolved_move_yaw if is_moving else lower_yaw
	if resolved_policy == POLICY_COMMITTED and not is_moving:
		lower_target = lerp_angle(lower_target, resolved_aim_yaw, commit_weight)

	var current_twist := _angle_delta(lower_yaw, resolved_aim_yaw)
	var recentered := not is_moving and absf(current_twist) > soft_twist
	if recentered:
		lower_target = _wrap_angle(
			resolved_aim_yaw - signf(current_twist) * soft_twist
		)
		lower_yaw = _approach_angle(lower_yaw, lower_target, recenter_speed * step_seconds)
	else:
		lower_yaw = _approach_angle(lower_yaw, lower_target, lower_turn_speed * step_seconds)

	var raw_twist := _angle_delta(lower_yaw, resolved_aim_yaw)
	var target_upper_twist := clampf(raw_twist, -hard_twist, hard_twist)
	var upper_twist := _smooth_angle(
		current_upper_twist,
		target_upper_twist,
		step_seconds,
		upper_response_seconds
	)
	return _result(
		lower_yaw,
		resolved_aim_yaw,
		upper_twist,
		target_upper_twist,
		resolved_policy,
		recentered,
		raw_twist
	)


static func normalize_policy(policy: StringName) -> StringName:
	return policy if policy in POLICIES else POLICY_UPPER


static func normalize_locomotion_frame(frame: StringName) -> StringName:
	return frame if frame in LOCOMOTION_FRAMES else LOCOMOTION_FRAME_VELOCITY


static func validate_options(options: Dictionary) -> PackedStringArray:
	var errors := PackedStringArray()
	var locomotion_frame := StringName(str(
		options.get("locomotion_frame", LOCOMOTION_FRAME_VELOCITY)
	))
	if locomotion_frame not in LOCOMOTION_FRAMES:
		errors.append("locomotion_frame must be velocity or aim")
	var soft_twist := float(options.get("soft_twist_degrees", DEFAULT_SOFT_TWIST_DEGREES))
	var hard_twist := float(options.get("hard_twist_degrees", DEFAULT_HARD_TWIST_DEGREES))
	if soft_twist < 0.0 or soft_twist > 180.0:
		errors.append("soft_twist_degrees must be between 0 and 180")
	if hard_twist < soft_twist or hard_twist > 180.0:
		errors.append("hard_twist_degrees must be between soft_twist_degrees and 180")
	for field: String in [
		"lower_turn_speed_degrees",
		"recenter_speed_degrees",
		"upper_response_seconds",
		"upper_return_seconds",
	]:
		if float(options.get(field, 0.0)) < 0.0:
			errors.append("%s must be non-negative" % field)
	var commit_weight := float(options.get("lower_commit_weight", 0.0))
	if commit_weight < 0.0 or commit_weight > 1.0:
		errors.append("lower_commit_weight must be between 0 and 1")
	return errors


static func _result(
	lower_yaw: float,
	action_yaw: float,
	upper_twist_yaw: float,
	target_upper_twist_yaw: float,
	policy: StringName,
	recentered: bool,
	raw_twist_yaw: float
) -> Dictionary:
	return {
		"lower_yaw": _wrap_angle(lower_yaw),
		"upper_yaw": _wrap_angle(action_yaw),
		"action_yaw": _wrap_angle(action_yaw),
		"upper_twist_yaw": upper_twist_yaw,
		"target_upper_twist_yaw": target_upper_twist_yaw,
		"raw_twist_yaw": raw_twist_yaw,
		"policy": policy,
		"recentered": recentered,
	}


static func _approach_angle(from: float, to: float, maximum_delta: float) -> float:
	var delta := _angle_delta(from, to)
	if absf(delta) <= maximum_delta:
		return _wrap_angle(to)
	return _wrap_angle(from + signf(delta) * maximum_delta)


static func _smooth_angle(
	from: float,
	to: float,
	delta: float,
	response_seconds: float
) -> float:
	if response_seconds <= 0.0:
		return _wrap_angle(to)
	if delta <= 0.0:
		return _wrap_angle(from)
	var blend := 1.0 - exp(-delta / response_seconds)
	return _wrap_angle(lerp_angle(from, to, blend))


static func _angle_delta(from: float, to: float) -> float:
	return wrapf(to - from, -PI, PI)


static func _wrap_angle(value: float) -> float:
	return wrapf(value, -PI, PI)
