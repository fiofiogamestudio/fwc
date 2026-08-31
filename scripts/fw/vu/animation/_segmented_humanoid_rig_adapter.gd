class_name FSegmentedHumanoidRigAdapter
extends "res://fw/scripts/fw/vu/animation/_procedural_rig_adapter.gd"

const EPSILON: float = 0.0001
const DEFAULT_ARM_LENGTHS := Vector2(0.34, 0.32)
const DEFAULT_LEG_LENGTHS := Vector2(0.4, 0.38)

const REQUIRED_BONES: PackedStringArray = [
	"hips",
	"spine",
	"chest",
	"head",
	"left_upper_arm",
	"left_lower_arm",
	"left_hand",
	"right_upper_arm",
	"right_lower_arm",
	"right_hand",
	"left_upper_leg",
	"left_lower_leg",
	"left_foot",
	"right_upper_leg",
	"right_lower_leg",
	"right_foot",
]

var _skeleton: Skeleton3D = null
var _bone_indices: Dictionary = {}
var _rest_transforms: Dictionary = {}
var _segments: Dictionary = {}
var _processed_frame_count: int = 0
var _last_process_usec: int = 0
var _max_process_usec: int = 0
var _endpoint_errors: Dictionary = {}
var _stretch_ratios: Dictionary = {}
var _joint_positions: Dictionary = {}
var _shoulder_offsets: Dictionary = {}
var _last_pose_kind: String = "idle"


func setup(rig_root: Node3D, profile: Dictionary = {}) -> PackedStringArray:
	super.setup(rig_root, profile)
	_skeleton = profile.get("skeleton", null) as Skeleton3D
	_bone_indices.clear()
	_rest_transforms.clear()
	_segments.clear()
	_endpoint_errors.clear()
	_stretch_ratios.clear()
	_joint_positions.clear()
	_shoulder_offsets.clear()
	if _skeleton == null or not is_instance_valid(_skeleton):
		_errors.append("segmented humanoid adapter requires a semantic Skeleton3D")
		_configured = false
		return errors()
	var bones: Dictionary = profile.get("bones", {})
	for semantic_value: Variant in bones:
		var semantic := str(semantic_value)
		var bone_name := StringName(str(bones[semantic_value]))
		var bone_index := _skeleton.find_bone(bone_name)
		if not bone_name.is_empty() and bone_index >= 0:
			_bone_indices[semantic] = bone_index
	for semantic: String in REQUIRED_BONES:
		if not _bone_indices.has(semantic):
			_errors.append("segmented humanoid rig is missing semantic bone '%s'" % semantic)
	for semantic_value: Variant in _bone_indices:
		var semantic := str(semantic_value)
		_rest_transforms[semantic] = _global_rest(int(_bone_indices[semantic]))
	# Runtime-created skeletons start with identity poses. Seed every mapped bone
	# from the rest hierarchy so attachments and sockets are valid before frame 1.
	var semantics_by_index: Dictionary = {}
	for semantic_value: Variant in _bone_indices:
		semantics_by_index[int(_bone_indices[semantic_value])] = str(semantic_value)
	for bone_index: int in range(_skeleton.get_bone_count()):
		if semantics_by_index.has(bone_index):
			var semantic := str(semantics_by_index[bone_index])
			_set_bone(semantic, _rest(semantic))
	_skeleton.force_update_all_bone_transforms()
	var segment_paths: Dictionary = profile.get("segments", {})
	for semantic_value: Variant in segment_paths:
		var semantic := str(semantic_value)
		var path := NodePath(str(segment_paths[semantic_value]))
		var node := _rig_root.get_node_or_null(path) as Node3D
		if node != null:
			_segments[semantic] = node
	_configured = _errors.is_empty()
	if _configured:
		_apply_pose({})
	return errors()


func submit_pose(pose: Dictionary) -> void:
	super.submit_pose(pose)


func clear_pose() -> void:
	super.clear_pose()
	if is_ready():
		_apply_pose({})


func solve_now() -> bool:
	if not is_ready():
		return false
	var started_usec := Time.get_ticks_usec()
	_apply_pose(_pose)
	_last_process_usec = maxi(Time.get_ticks_usec() - started_usec, 0)
	_max_process_usec = maxi(_max_process_usec, _last_process_usec)
	_processed_frame_count += 1
	return true


func backend_id() -> StringName:
	return &"segmented_rigid_humanoid"


func semantic_transform(semantic: StringName) -> Transform3D:
	return _rest_transforms.get(str(semantic), Transform3D.IDENTITY) as Transform3D


func fit_hand_targets(
	targets: Dictionary,
	pose_override: Dictionary = {},
	max_iterations: int = 8,
	translation_axes: Array = []
) -> Dictionary:
	var result := {
		"translation": Vector3.ZERO,
		"residuals": {},
		"max_residual": 0.0,
		"iterations": 0,
		"feasible": false,
	}
	if not is_ready() or targets.is_empty():
		result["feasible"] = is_ready() and targets.is_empty()
		return result
	var preview_pose := pose_override if not pose_override.is_empty() else _pose
	var torso := _resolve_torso(preview_pose)
	var translation := Vector3.ZERO
	var residuals: Dictionary = {}
	var iterations := clampi(max_iterations, 1, 128)
	for iteration: int in range(iterations):
		var accumulated := Vector3.ZERO
		var accepted := 0
		for side_value: Variant in targets:
			var side := str(side_value)
			var target_value: Variant = targets[side_value]
			if side not in ["left", "right"] or not target_value is Transform3D:
				continue
			var target := target_value as Transform3D
			target.origin += translation
			var lengths := _arm_lengths(side)
			var shoulder := _resolved_shoulder_position(
				torso, side, target.origin, lengths, preview_pose
			)
			var fitted := _clamp_target(shoulder, target.origin, lengths.x, lengths.y)
			var correction := fitted - target.origin
			correction = _project_translation(correction, translation_axes)
			accumulated += correction
			accepted += 1
		if accepted <= 0:
			break
		var step := accumulated / float(accepted)
		translation += step
		result["iterations"] = iteration + 1
		if step.length() <= EPSILON:
			break

	var maximum := 0.0
	for side_value: Variant in targets:
		var side := str(side_value)
		var target_value: Variant = targets[side_value]
		if side not in ["left", "right"] or not target_value is Transform3D:
			continue
		var shifted := (target_value as Transform3D).origin + translation
		var lengths := _arm_lengths(side)
		var shoulder := _resolved_shoulder_position(
			torso, side, shifted, lengths, preview_pose
		)
		var fitted := _clamp_target(shoulder, shifted, lengths.x, lengths.y)
		var residual := fitted.distance_to(shifted)
		residuals["%s_hand" % side] = residual
		maximum = maxf(maximum, residual)
	result["translation"] = translation
	result["residuals"] = residuals
	result["max_residual"] = maximum
	result["feasible"] = maximum <= EPSILON
	return result


func metrics() -> Dictionary:
	var result := super.metrics()
	result["backend"] = str(backend_id())
	result["torso_bones"] = PackedStringArray(["hips", "spine", "chest", "head"])
	result["arm_chains"] = {
		"left": PackedStringArray(["left_upper_arm", "left_lower_arm", "left_hand"]),
		"right": PackedStringArray(["right_upper_arm", "right_lower_arm", "right_hand"]),
	}
	result["leg_chains"] = {
		"left": PackedStringArray(["left_upper_leg", "left_lower_leg", "left_foot"]),
		"right": PackedStringArray(["right_upper_leg", "right_lower_leg", "right_foot"]),
	}
	result["semantic_count"] = _bone_indices.size()
	result["processed_frame_count"] = _processed_frame_count
	result["last_process_usec"] = _last_process_usec
	result["max_process_usec"] = _max_process_usec
	result["endpoint_errors"] = _endpoint_errors.duplicate(true)
	result["stretch_ratios"] = _stretch_ratios.duplicate(true)
	result["joint_positions"] = _joint_positions.duplicate(true)
	result["shoulder_offsets"] = _shoulder_offsets.duplicate(true)
	result["pose_kind"] = _last_pose_kind
	result["pose_progress"] = _mobility_progress(_pose)
	result["skinned_mesh_count"] = 0
	return result


func _apply_pose(pose: Dictionary) -> void:
	_endpoint_errors.clear()
	_stretch_ratios.clear()
	_joint_positions.clear()
	_shoulder_offsets.clear()
	_last_pose_kind = str(pose.get("body_pose_kind", "idle"))
	var torso := _resolve_torso(pose)
	_set_bone("hips", torso.hips)
	_set_bone("spine", torso.spine)
	_set_bone("chest", torso.chest)
	_set_bone("head", torso.head)
	_solve_arm("left", torso, pose)
	_solve_arm("right", torso, pose)
	_solve_leg("left", torso, pose)
	_solve_leg("right", torso, pose)
	_skeleton.force_update_all_bone_transforms()


func _resolve_torso(pose: Dictionary) -> Dictionary:
	var phase := float(pose.get("locomotion_phase", 0.0))
	var speed_ratio := clampf(float(pose.get("locomotion_speed_ratio", 0.0)), 0.0, 1.5)
	var grounded_weight := clampf(float(pose.get("locomotion_weight", 1.0)), 0.0, 1.0)
	var roll_progress := _roll_progress(pose)
	var roll_tuck := sin(roll_progress * PI)
	var wave_progress := _wave_progress(pose)
	var wave_drive := sin(wave_progress * PI)
	if roll_progress > 0.0 or wave_progress > 0.0:
		grounded_weight = 0.0
	var bob := sin(phase * 2.0) * 0.025 * speed_ratio * grounded_weight
	var sway := sin(phase) * 2.4 * speed_ratio * grounded_weight
	var hips: Transform3D = _rest("hips")
	hips.origin += pose.get("hips_position", Vector3.ZERO) as Vector3
	if roll_progress > 0.0:
		hips.origin += Vector3(0.0, -0.24 * roll_tuck, -0.1 * roll_tuck)
	elif wave_progress > 0.0:
		hips.origin += Vector3(0.0, -0.1 * wave_drive, -0.14 * wave_drive)
	hips.origin.y += bob
	var hips_rotation := (
		pose.get("hips_rotation_degrees", Vector3.ZERO) as Vector3
		+ Vector3(0.0, 0.0, sway)
	)
	if roll_progress > 0.0:
		hips_rotation += Vector3(-62.0 * roll_tuck, 0.0, sin(roll_progress * TAU) * 7.0)
	elif wave_progress > 0.0:
		hips_rotation += Vector3(-28.0 * wave_drive, 0.0, sin(wave_progress * TAU) * 3.0)
	hips.basis = _local_pose_basis(
		hips.basis,
		hips_rotation,
		Vector3.ONE
	)

	var hips_delta := hips * _rest("hips").affine_inverse()
	var spine := hips_delta * _rest("spine")
	spine.basis = _local_pose_basis(
		spine.basis,
		pose.get("spine_rotation_degrees", Vector3.ZERO) as Vector3,
		pose.get("spine_scale", Vector3.ONE) as Vector3
	)

	var spine_delta := spine * _rest("spine").affine_inverse()
	var chest := spine_delta * _rest("chest")
	chest.basis = _local_pose_basis(
		chest.basis,
		pose.get("chest_rotation_degrees", Vector3.ZERO) as Vector3,
		pose.get("chest_scale", Vector3.ONE) as Vector3
	)
	var torso_offset := (
		pose.get("torso_reach_offset", Vector3.ZERO) as Vector3
		+ pose.get("spine_position", Vector3.ZERO) as Vector3
		+ pose.get("chest_position", Vector3.ZERO) as Vector3
	)
	if torso_offset.length_squared() > EPSILON:
		var upper_length := _rest("hips").origin.distance_to(_rest("spine").origin)
		var lower_length := _rest("spine").origin.distance_to(_rest("chest").origin)
		var torso_solution := _solve_two_link(
			hips.origin,
			chest.origin + torso_offset,
			spine.origin + Vector3(0.0, 0.0, 0.4),
			upper_length,
			lower_length
		)
		spine.origin = torso_solution.joint
		chest.origin = torso_solution.endpoint
	var head: Transform3D = _rest("head")
	var chest_delta := chest * _rest("chest").affine_inverse()
	head = chest_delta * head
	return {"hips": hips, "spine": spine, "chest": chest, "head": head}


func _solve_arm(side: String, torso: Dictionary, pose: Dictionary) -> void:
	var rest_hand := _rest("%s_hand" % side)
	var target: Transform3D = pose.get("%s_hand_transform" % side, rest_hand)
	if not pose.has("%s_hand_transform" % side):
		var roll_progress := _roll_progress(pose)
		if roll_progress > 0.0:
			var roll_tuck := sin(roll_progress * PI)
			var chest: Transform3D = torso.chest
			var followed := chest * _rest("chest").affine_inverse() * rest_hand
			var side_sign := -1.0 if side == "left" else 1.0
			var tucked := followed
			tucked.origin = chest.origin + Vector3(side_sign * 0.2, -0.2, -0.2)
			target = followed.interpolate_with(tucked, roll_tuck)
		elif _wave_progress(pose) > 0.0:
			var wave_drive := sin(_wave_progress(pose) * PI)
			var chest: Transform3D = torso.chest
			var followed := chest * _rest("chest").affine_inverse() * rest_hand
			var side_sign := -1.0 if side == "left" else 1.0
			var swept := followed
			swept.origin = chest.origin + Vector3(side_sign * 0.24, -0.18, 0.2)
			target = followed.interpolate_with(swept, wave_drive)
		else:
			var locomotion_phase := float(pose.get("locomotion_phase", 0.0))
			var speed_ratio := clampf(float(pose.get("locomotion_speed_ratio", 0.0)), 0.0, 1.0)
			var side_phase := locomotion_phase + (PI if side == "left" else 0.0)
			target.origin += Vector3(
				0.0,
				sin(side_phase * 2.0) * 0.025,
				sin(side_phase) * 0.16 * speed_ratio
			)
	var lengths := _arm_lengths(side)
	var shoulder := _resolved_shoulder_position(torso, side, target.origin, lengths, pose)
	var pole: Vector3 = pose.get(
		"%s_arm_pole" % side,
		shoulder + Vector3(-0.45 if side == "left" else 0.45, 0.04, 0.32)
	)
	var solved := _solve_two_link(shoulder, target.origin, pole, lengths.x, lengths.y)
	var joint: Vector3 = solved.joint
	var endpoint: Vector3 = solved.endpoint
	_set_chain_bone("%s_upper_arm" % side, shoulder, joint)
	_set_chain_bone("%s_lower_arm" % side, joint, endpoint)
	var hand_pose := target
	hand_pose.origin = endpoint
	_set_bone("%s_hand" % side, hand_pose)
	_endpoint_errors["%s_hand" % side] = endpoint.distance_to(target.origin)
	_stretch_ratios["%s_arm" % side] = float(solved.stretch_ratio)
	_joint_positions["%s_elbow" % side] = joint


func _solve_leg(side: String, torso: Dictionary, pose: Dictionary) -> void:
	var hip := _hip_position(torso, side)
	var rest_foot := _rest("%s_foot" % side)
	var target: Transform3D = pose.get("%s_foot_transform" % side, rest_foot)
	if not pose.has("%s_foot_transform" % side):
		var roll_progress := _roll_progress(pose)
		if roll_progress > 0.0:
			var roll_tuck := sin(roll_progress * PI)
			var side_sign := -1.0 if side == "left" else 1.0
			var tucked_origin := hip + Vector3(side_sign * 0.15, -0.27, -0.2)
			target.origin = rest_foot.origin.lerp(tucked_origin, roll_tuck)
		elif _wave_progress(pose) > 0.0:
			var wave_drive := sin(_wave_progress(pose) * PI)
			var stride_sign := -1.0 if side == "left" else 1.0
			target.origin += Vector3(0.0, 0.06 * wave_drive, 0.16 * stride_sign * wave_drive)
		else:
			var phase := float(pose.get("locomotion_phase", 0.0))
			var speed_ratio := clampf(float(pose.get("locomotion_speed_ratio", 0.0)), 0.0, 1.5)
			var side_phase := phase + (PI if side == "right" else 0.0)
			var stride := sin(side_phase) * 0.24 * speed_ratio
			var lift := maxf(sin(side_phase), 0.0) * 0.12 * minf(speed_ratio, 1.0)
			target.origin += Vector3(0.0, lift, stride)
	var lengths := _leg_lengths(side)
	var pole := hip + Vector3(-0.05 if side == "left" else 0.05, -0.28, -0.52)
	var solved := _solve_two_link(hip, target.origin, pole, lengths.x, lengths.y)
	var knee: Vector3 = solved.joint
	var endpoint: Vector3 = solved.endpoint
	_set_chain_bone("%s_upper_leg" % side, hip, knee)
	_set_chain_bone("%s_lower_leg" % side, knee, endpoint)
	var foot_pose := target
	foot_pose.origin = endpoint
	_set_bone("%s_foot" % side, foot_pose)
	_endpoint_errors["%s_foot" % side] = endpoint.distance_to(target.origin)
	_stretch_ratios["%s_leg" % side] = float(solved.stretch_ratio)
	_joint_positions["%s_knee" % side] = knee


func _roll_progress(pose: Dictionary) -> float:
	if str(pose.get("body_pose_kind", "")) != "roll":
		return 0.0
	return clampf(float(pose.get("body_pose_progress", 0.0)), 0.0, 1.0)


func _wave_progress(pose: Dictionary) -> float:
	if str(pose.get("body_pose_kind", "")) != "wave_form":
		return 0.0
	return clampf(float(pose.get("body_pose_progress", 0.0)), 0.0, 1.0)


func _mobility_progress(pose: Dictionary) -> float:
	return maxf(_roll_progress(pose), _wave_progress(pose))


func _solve_two_link(
	start: Vector3,
	target: Vector3,
	pole: Vector3,
	upper_length: float,
	lower_length: float
) -> Dictionary:
	var delta := target - start
	var distance := delta.length()
	var direction := delta / distance if distance > EPSILON else Vector3.FORWARD
	var minimum := absf(upper_length - lower_length) + EPSILON
	var maximum := upper_length + lower_length - EPSILON
	var clamped_distance := clampf(distance, minimum, maximum)
	var endpoint := start + direction * clamped_distance
	var along := (
		upper_length * upper_length
		- lower_length * lower_length
		+ clamped_distance * clamped_distance
	) / (2.0 * clamped_distance)
	var height := sqrt(maxf(upper_length * upper_length - along * along, 0.0))
	var pole_delta := pole - start
	var bend := pole_delta - direction * pole_delta.dot(direction)
	if bend.length_squared() <= EPSILON:
		bend = direction.cross(Vector3.UP)
	if bend.length_squared() <= EPSILON:
		bend = direction.cross(Vector3.RIGHT)
	bend = bend.normalized()
	return {
		"joint": start + direction * along + bend * height,
		"endpoint": endpoint,
		"stretch_ratio": distance / maxf(upper_length + lower_length, EPSILON),
	}


func _set_chain_bone(semantic: String, start: Vector3, end: Vector3) -> void:
	var direction := end - start
	var basis := Basis.IDENTITY
	if direction.length_squared() > EPSILON:
		basis = Basis(Quaternion(Vector3.UP, direction.normalized()))
	_set_bone(semantic, Transform3D(basis.orthonormalized(), start))
	var segment := _segments.get(semantic) as Node3D
	if segment != null:
		segment.scale.y = maxf(direction.length(), EPSILON)


func _set_bone(semantic: String, value: Transform3D) -> void:
	if not _bone_indices.has(semantic):
		return
	var bone_index := int(_bone_indices[semantic])
	var appearance_scale := _skeleton.get_bone_pose_scale(bone_index)
	_skeleton.set_bone_global_pose(bone_index, value)
	_skeleton.set_bone_pose_scale(bone_index, appearance_scale)


func _rest(semantic: String) -> Transform3D:
	return _rest_transforms.get(semantic, Transform3D.IDENTITY) as Transform3D


func _global_rest(bone_index: int) -> Transform3D:
	var result := _skeleton.get_bone_rest(bone_index)
	var parent := _skeleton.get_bone_parent(bone_index)
	while parent >= 0:
		result = _skeleton.get_bone_rest(parent) * result
		parent = _skeleton.get_bone_parent(parent)
	return result


func _shoulder_position(torso: Dictionary, side: String) -> Vector3:
	var chest: Transform3D = torso.chest
	var rest_chest := _rest("chest")
	var rest_shoulder := _rest("%s_upper_arm" % side)
	return chest * rest_chest.affine_inverse() * rest_shoulder.origin


func _resolved_shoulder_position(
	torso: Dictionary,
	side: String,
	target: Vector3,
	lengths: Vector2,
	pose: Dictionary
) -> Vector3:
	var shoulder := _shoulder_position(torso, side)
	var delta := target - shoulder
	var distance := delta.length()
	var chest: Transform3D = torso.chest
	var clavicle := shoulder - chest.origin
	var clavicle_length := clavicle.length()
	if distance <= EPSILON or clavicle_length <= EPSILON:
		return shoulder
	var required := maxf(distance - lengths.x - lengths.y + EPSILON, 0.0)
	if required <= EPSILON:
		_shoulder_offsets["%s_shoulder" % side] = Vector3.ZERO
		return shoulder
	var swing_degrees := clampf(float(pose.get(
		"%s_shoulder_swing_degrees" % side,
		22.0
	)), 0.0, 35.0)
	var profile_limit := maxf(float(_profile.get(
		"%s_shoulder_reach" % side,
		0.12
	)), 0.0)
	var desired_direction := (target - chest.origin).normalized()
	var current_direction := clavicle / clavicle_length
	var full_rotation := Quaternion(current_direction, desired_direction)
	var full_angle := full_rotation.get_angle()
	var reach_angle := 2.0 * asin(clampf(
		profile_limit / maxf(2.0 * clavicle_length, EPSILON),
		0.0,
		1.0
	))
	var selected_angle := minf(
		full_angle,
		minf(deg_to_rad(swing_degrees), reach_angle)
	)
	var selected_rotation := Quaternion.IDENTITY
	if full_angle > EPSILON:
		selected_rotation = Quaternion.IDENTITY.slerp(
			full_rotation,
			selected_angle / full_angle
		)
	var candidate := chest.origin + selected_rotation * clavicle
	var offset := candidate - shoulder
	_shoulder_offsets["%s_shoulder" % side] = offset
	return candidate


func _hip_position(torso: Dictionary, side: String) -> Vector3:
	var hips: Transform3D = torso.hips
	var rest_hips := _rest("hips")
	var rest_hip := _rest("%s_upper_leg" % side)
	return hips * rest_hips.affine_inverse() * rest_hip.origin


func _arm_lengths(side: String) -> Vector2:
	return _profile_length_pair("%s_arm_lengths" % side, DEFAULT_ARM_LENGTHS)


func _leg_lengths(side: String) -> Vector2:
	return _profile_length_pair("%s_leg_lengths" % side, DEFAULT_LEG_LENGTHS)


func _profile_length_pair(key: String, fallback: Vector2) -> Vector2:
	var value: Variant = _profile.get(key, fallback)
	if value is Vector2:
		var pair := value as Vector2
		return Vector2(maxf(pair.x, EPSILON), maxf(pair.y, EPSILON))
	return fallback


func _clamp_target(
	start: Vector3,
	target: Vector3,
	upper_length: float,
	lower_length: float
) -> Vector3:
	var delta := target - start
	var distance := delta.length()
	if distance <= EPSILON:
		return start + Vector3.FORWARD * absf(upper_length - lower_length)
	var minimum := absf(upper_length - lower_length) + EPSILON
	var maximum := upper_length + lower_length - EPSILON
	return start + delta / distance * clampf(distance, minimum, maximum)


func _project_translation(value: Vector3, axes: Array) -> Vector3:
	if axes.is_empty():
		return value
	var result := Vector3.ZERO
	var accepted: Array[Vector3] = []
	for axis_value: Variant in axes:
		if not axis_value is Vector3:
			continue
		var axis := axis_value as Vector3
		for existing: Vector3 in accepted:
			axis -= existing * axis.dot(existing)
		if axis.length_squared() <= EPSILON:
			continue
		axis = axis.normalized()
		accepted.append(axis)
		result += axis * value.dot(axis)
	return result


func _local_pose_basis(base: Basis, rotation_degrees: Vector3, scale_value: Vector3) -> Basis:
	return base * Basis.from_euler(Vector3(
		deg_to_rad(rotation_degrees.x),
		deg_to_rad(rotation_degrees.y),
		deg_to_rad(rotation_degrees.z)
	)).scaled(scale_value)
