class_name FProceduralHumanoidModifier
extends SkeletonModifier3D

const EPSILON: float = 0.0001
const MIN_BEND_LENGTH: float = 0.002
const DEFAULT_SHOULDER_SWING_DEGREES: float = 22.0
const MAX_SHOULDER_SWING_DEGREES: float = 35.0
const HARD_MAX_SHOULDER_SWING_DEGREES: float = 65.0
const DEFAULT_TORSO_REACH_LEAN_DEGREES: float = 30.0
const MAX_TORSO_REACH_LEAN_DEGREES: float = 40.0
const ARM_CHAINS: Dictionary = {
	"left": ["left_upper_arm", "left_lower_arm", "left_wrist", "left_hand"],
	"right": ["right_upper_arm", "right_lower_arm", "right_wrist", "right_hand"],
}
const LEG_CHAINS: Dictionary = {
	"left": ["left_upper_leg", "left_lower_leg", "left_foot"],
	"right": ["right_upper_leg", "right_lower_leg", "right_foot"],
}
const TORSO_CHAIN: Array = ["hips", "spine", "chest"]

var _rig_root: Node3D = null
var _profile: Dictionary = {}
var _bone_indices: Dictionary = {}
var _bone_rests: Dictionary = {}
var _pose: Dictionary = {}
var _errors := PackedStringArray()
var _configured: bool = false
var _lod_counter: int = 0
var _basis_corrections: Dictionary = {}
var _endpoint_errors: Dictionary = {}
var _reach_clamps: Dictionary = {}
var _shoulder_offsets: Dictionary = {}
var _torso_reach_metrics: Dictionary = {}
var _base_torso_poses: Dictionary = {}
var _last_applied_torso_poses: Dictionary = {}
var _base_chain_poses: Dictionary = {}
var _last_applied_chain_poses: Dictionary = {}
var _processed_frame_count: int = 0
var _last_process_usec: int = 0
var _max_process_usec: int = 0


func configure(profile: Dictionary, rig_root: Node3D) -> PackedStringArray:
	_restore_chain_poses_if_unchanged()
	_restore_torso_poses_if_unchanged()
	_base_chain_poses.clear()
	_last_applied_chain_poses.clear()
	_base_torso_poses.clear()
	_last_applied_torso_poses.clear()
	_rig_root = rig_root
	_profile = profile.duplicate(true)
	_errors = _validate_profile()
	var skeleton := get_skeleton()
	if skeleton == null:
		_errors.append("procedural humanoid modifier must be a child of Skeleton3D")
	if not _errors.is_empty():
		_configured = false
		active = false
		return _errors.duplicate()
	_cache_bones(skeleton)
	_cache_basis_corrections(skeleton)
	_configured = true
	active = true
	influence = 1.0
	return PackedStringArray()


func submit_pose(pose: Dictionary) -> void:
	_pose = pose.duplicate(true)
	_capture_chain_poses()
	_capture_torso_poses()


func clear_pose() -> void:
	_restore_chain_poses_if_unchanged()
	_restore_torso_poses_if_unchanged()
	_pose.clear()
	_endpoint_errors.clear()
	_reach_clamps.clear()
	_shoulder_offsets.clear()
	_torso_reach_metrics.clear()
	_base_torso_poses.clear()
	_last_applied_torso_poses.clear()
	_base_chain_poses.clear()
	_last_applied_chain_poses.clear()


func solve_now() -> bool:
	if not _configured or _pose.is_empty():
		return false
	return _solve_pose()


func is_configured() -> bool:
	return _configured


func errors() -> PackedStringArray:
	return _errors.duplicate()


func metrics() -> Dictionary:
	return {
		"configured": _configured,
		"errors": _errors.duplicate(),
		"endpoint_errors": _endpoint_errors.duplicate(true),
		"reach_clamps": _reach_clamps.duplicate(true),
		"shoulder_offsets": _shoulder_offsets.duplicate(true),
		"torso_reach": _torso_reach_metrics.duplicate(true),
		"torso_bones": _configured_torso_semantics(),
		"processed_frame_count": _processed_frame_count,
		"last_process_usec": _last_process_usec,
		"max_process_usec": _max_process_usec,
		"root_motion_enabled": false,
	}


func fit_hand_targets(
	targets: Dictionary,
	pose: Dictionary = {},
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
	if not _configured or targets.is_empty():
		result["feasible"] = _configured and targets.is_empty()
		return result
	var skeleton := get_skeleton()
	if skeleton == null:
		return result

	var original_torso_poses: Dictionary = {}
	var previewed_torso := not pose.is_empty()
	if previewed_torso:
		original_torso_poses = _capture_current_torso_poses(skeleton)
		_apply_torso_pose(skeleton, pose, false)
		skeleton.force_update_all_bone_transforms()
		_apply_torso_reach_pose(skeleton, pose, false)
		skeleton.force_update_all_bone_transforms()

	var translation := Vector3.ZERO
	var residuals: Dictionary = {}
	var iteration_limit := clampi(max_iterations, 1, 128)
	for iteration: int in range(iteration_limit):
		for side_value: Variant in targets:
			var side := str(side_value)
			var target_value: Variant = targets[side_value]
			if not ARM_CHAINS.has(side) or not target_value is Transform3D:
				continue
			var shifted_target: Transform3D = target_value
			shifted_target.origin += translation
			var correction := _fit_hand_translation_current(
				skeleton,
				side,
				shifted_target,
				pose,
				translation_axes
			)
			translation += correction

		var max_residual := 0.0
		residuals.clear()
		for side_value: Variant in targets:
			var side := str(side_value)
			var target_value: Variant = targets[side_value]
			if not ARM_CHAINS.has(side) or not target_value is Transform3D:
				continue
			var shifted_target: Transform3D = target_value
			shifted_target.origin += translation
			var fitted_target := _fit_hand_target_current(
				skeleton,
				side,
				shifted_target,
				pose
			)
			var residual := fitted_target.origin.distance_to(shifted_target.origin)
			residuals["%s_hand" % side] = residual
			max_residual = maxf(max_residual, residual)
		result["iterations"] = iteration + 1
		if max_residual <= EPSILON:
			break

	if previewed_torso:
		_restore_torso_pose_map(skeleton, original_torso_poses)
		skeleton.force_update_all_bone_transforms()
	result["translation"] = translation
	result["residuals"] = residuals.duplicate(true)
	var maximum := 0.0
	for value: Variant in residuals.values():
		maximum = maxf(maximum, float(value))
	result["max_residual"] = maximum
	result["feasible"] = maximum <= EPSILON
	return result


func _process_modification_with_delta(_delta: float) -> void:
	if not _configured or _pose.is_empty():
		return
	var update_divisor := clampi(int(_pose.get("visual_lod_divisor", 1)), 1, 4)
	_lod_counter = (_lod_counter + 1) % update_divisor
	if _lod_counter == 0:
		_solve_pose()


func _solve_pose() -> bool:
	var skeleton := get_skeleton()
	if skeleton == null:
		return false
	var started_usec := Time.get_ticks_usec()
	_endpoint_errors.clear()
	_reach_clamps.clear()
	_shoulder_offsets.clear()
	_torso_reach_metrics.clear()
	_apply_torso_pose(skeleton, _pose, true)
	skeleton.force_update_all_bone_transforms()
	_apply_torso_reach_pose(skeleton, _pose, true)
	skeleton.force_update_all_bone_transforms()
	for side: String in ARM_CHAINS:
		if _pose.has("%s_hand_transform" % side):
			_solve_arm(skeleton, side)
	for side: String in LEG_CHAINS:
		if _pose.has("%s_foot_transform" % side):
			_solve_leg(skeleton, side)
	skeleton.force_update_all_bone_transforms()
	_record_applied_chain_poses(skeleton)
	_last_process_usec = maxi(Time.get_ticks_usec() - started_usec, 0)
	_max_process_usec = maxi(_max_process_usec, _last_process_usec)
	_processed_frame_count += 1
	return true


func _validate_profile() -> PackedStringArray:
	var result := PackedStringArray()
	if _rig_root == null:
		result.append("procedural humanoid rig_root cannot be null")
	var bones: Dictionary = _profile.get("bones", {})
	if bones.is_empty():
		result.append("procedural humanoid profile requires bones")
		return result
	var skeleton := get_skeleton()
	if skeleton == null:
		return result
	for semantic: Variant in bones:
		var bone_name := str(bones[semantic])
		if skeleton.find_bone(bone_name) < 0:
			result.append("missing humanoid bone %s -> %s" % [semantic, bone_name])
	for chains: Dictionary in [ARM_CHAINS, LEG_CHAINS]:
		for side: String in chains:
			var chain: Array = chains[side]
			if not _profile_has_chain(bones, chain):
				continue
			_validate_chain_parenting(result, skeleton, bones, chain)
	if _profile_has_chain(bones, TORSO_CHAIN):
		_validate_chain_parenting(result, skeleton, bones, TORSO_CHAIN)
	return result


func _validate_chain_parenting(
	result: PackedStringArray,
	skeleton: Skeleton3D,
	bones: Dictionary,
	chain: Array
) -> void:
	for index: int in range(1, chain.size()):
		var parent := skeleton.find_bone(str(bones[chain[index - 1]]))
		var child := skeleton.find_bone(str(bones[chain[index]]))
		if parent >= 0 and child >= 0 and skeleton.get_bone_parent(child) != parent:
			result.append(
				"humanoid bone %s must be a child of %s" % [
					chain[index],
					chain[index - 1],
				]
			)


func _profile_has_chain(bones: Dictionary, chain: Array) -> bool:
	for semantic: Variant in chain:
		if not bones.has(semantic):
			return false
	return true


func _cache_bones(skeleton: Skeleton3D) -> void:
	_bone_indices.clear()
	_bone_rests.clear()
	var bones: Dictionary = _profile.get("bones", {})
	for semantic: Variant in bones:
		var key := str(semantic)
		var index := skeleton.find_bone(str(bones[semantic]))
		_bone_indices[key] = index
		_bone_rests[key] = skeleton.get_bone_rest(index)


func _cache_basis_corrections(skeleton: Skeleton3D) -> void:
	_basis_corrections.clear()
	for semantic: String in ["left_hand", "right_hand", "left_foot", "right_foot"]:
		var index := int(_bone_indices.get(semantic, -1))
		if index >= 0:
			# Endpoint transforms already come from the rig adapter's semantic bone
			# frame. Applying the bone rest basis again double-rotates hands and feet.
			_basis_corrections[semantic] = Basis.IDENTITY


func _configured_torso_semantics() -> PackedStringArray:
	var result := PackedStringArray()
	for semantic: String in TORSO_CHAIN:
		if int(_bone_indices.get(semantic, -1)) >= 0:
			result.append(semantic)
	return result


func _targeted_chain_semantics() -> PackedStringArray:
	var result := PackedStringArray()
	var seen: Dictionary = {}
	for side: String in ARM_CHAINS:
		if not _pose.has("%s_hand_transform" % side):
			continue
		for semantic_value: Variant in ARM_CHAINS[side]:
			var semantic := str(semantic_value)
			if int(_bone_indices.get(semantic, -1)) >= 0 and not seen.has(semantic):
				seen[semantic] = true
				result.append(semantic)
	for side: String in LEG_CHAINS:
		if not _pose.has("%s_foot_transform" % side):
			continue
		for semantic_value: Variant in LEG_CHAINS[side]:
			var semantic := str(semantic_value)
			if int(_bone_indices.get(semantic, -1)) >= 0 and not seen.has(semantic):
				seen[semantic] = true
				result.append(semantic)
	return result


func _capture_chain_poses() -> void:
	var skeleton := get_skeleton()
	if skeleton == null:
		_base_chain_poses.clear()
		_last_applied_chain_poses.clear()
		return
	for semantic: String in _targeted_chain_semantics():
		var bone_index := int(_bone_indices[semantic])
		var current_pose := skeleton.get_bone_pose(bone_index)
		if not _base_chain_poses.has(semantic) or not _last_applied_chain_poses.has(semantic):
			_base_chain_poses[semantic] = current_pose
			continue
		var base_pose: Transform3D = _base_chain_poses[semantic]
		var last_pose: Transform3D = _last_applied_chain_poses[semantic]
		if not current_pose.origin.is_equal_approx(last_pose.origin):
			base_pose.origin = current_pose.origin
		if not current_pose.basis.is_equal_approx(last_pose.basis):
			base_pose.basis = current_pose.basis
		_base_chain_poses[semantic] = base_pose


func _record_applied_chain_poses(skeleton: Skeleton3D) -> void:
	for semantic: String in _targeted_chain_semantics():
		_last_applied_chain_poses[semantic] = skeleton.get_bone_pose(
			int(_bone_indices[semantic])
		)


func _restore_chain_poses_if_unchanged() -> void:
	if _base_chain_poses.is_empty() or _last_applied_chain_poses.is_empty():
		return
	var skeleton := get_skeleton()
	if skeleton == null:
		return
	var restored := false
	for semantic_value: Variant in _base_chain_poses:
		var semantic := str(semantic_value)
		if not _last_applied_chain_poses.has(semantic):
			continue
		var bone_index := int(_bone_indices.get(semantic, -1))
		if bone_index < 0:
			continue
		var current_pose := skeleton.get_bone_pose(bone_index)
		var base_pose: Transform3D = _base_chain_poses[semantic]
		var last_pose: Transform3D = _last_applied_chain_poses[semantic]
		var restored_pose := current_pose
		if current_pose.origin.is_equal_approx(last_pose.origin):
			restored_pose.origin = base_pose.origin
		if current_pose.basis.is_equal_approx(last_pose.basis):
			restored_pose.basis = base_pose.basis
		if not restored_pose.is_equal_approx(current_pose):
			skeleton.set_bone_pose(bone_index, restored_pose)
			restored = true
	if restored:
		skeleton.force_update_all_bone_transforms()


func _capture_torso_poses() -> void:
	var skeleton := get_skeleton()
	if skeleton == null:
		_base_torso_poses.clear()
		_last_applied_torso_poses.clear()
		return
	for semantic: String in _configured_torso_semantics():
		var bone_index := int(_bone_indices[semantic])
		var current_pose := skeleton.get_bone_pose(bone_index)
		if not _base_torso_poses.has(semantic) or not _last_applied_torso_poses.has(semantic):
			_base_torso_poses[semantic] = current_pose
			continue
		var base_pose: Transform3D = _base_torso_poses[semantic]
		var last_pose: Transform3D = _last_applied_torso_poses[semantic]
		if not current_pose.origin.is_equal_approx(last_pose.origin):
			base_pose.origin = current_pose.origin
		if not current_pose.basis.is_equal_approx(last_pose.basis):
			base_pose.basis = current_pose.basis
		_base_torso_poses[semantic] = base_pose


func _restore_torso_poses_if_unchanged() -> void:
	if _base_torso_poses.is_empty() or _last_applied_torso_poses.is_empty():
		return
	var skeleton := get_skeleton()
	if skeleton == null:
		return
	var restored := false
	for semantic: String in _configured_torso_semantics():
		if not _base_torso_poses.has(semantic) or not _last_applied_torso_poses.has(semantic):
			continue
		var bone_index := int(_bone_indices[semantic])
		var current_pose := skeleton.get_bone_pose(bone_index)
		var base_pose: Transform3D = _base_torso_poses[semantic]
		var last_pose: Transform3D = _last_applied_torso_poses[semantic]
		var restored_pose := current_pose
		if current_pose.origin.is_equal_approx(last_pose.origin):
			restored_pose.origin = base_pose.origin
		if current_pose.basis.is_equal_approx(last_pose.basis):
			restored_pose.basis = base_pose.basis
		if not restored_pose.is_equal_approx(current_pose):
			skeleton.set_bone_pose(bone_index, restored_pose)
			restored = true
	if restored:
		skeleton.force_update_all_bone_transforms()


func _capture_current_torso_poses(skeleton: Skeleton3D) -> Dictionary:
	var result: Dictionary = {}
	for semantic: String in _configured_torso_semantics():
		result[semantic] = skeleton.get_bone_pose(int(_bone_indices[semantic]))
	return result


func _restore_torso_pose_map(skeleton: Skeleton3D, poses: Dictionary) -> void:
	for semantic_value: Variant in poses:
		var semantic := str(semantic_value)
		var bone_index := int(_bone_indices.get(semantic, -1))
		if bone_index >= 0 and poses[semantic_value] is Transform3D:
			skeleton.set_bone_pose(bone_index, poses[semantic_value] as Transform3D)


func _apply_torso_pose(
	skeleton: Skeleton3D,
	pose: Dictionary,
	record_result: bool
) -> void:
	for semantic: String in _configured_torso_semantics():
		var bone_index := int(_bone_indices[semantic])
		var base_pose: Transform3D = _base_torso_poses.get(
			semantic,
			skeleton.get_bone_pose(bone_index)
		)
		var result := _compose_torso_pose(base_pose, semantic, pose)
		skeleton.set_bone_pose(bone_index, result)
		if record_result:
			_last_applied_torso_poses[semantic] = result


static func _compose_torso_pose(
	base_pose: Transform3D,
	semantic: String,
	pose: Dictionary
) -> Transform3D:
	var rotation_degrees: Vector3 = pose.get(
		"%s_rotation_degrees" % semantic,
		Vector3.ZERO
	)
	var rotation := Quaternion.from_euler(Vector3(
		deg_to_rad(rotation_degrees.x),
		deg_to_rad(rotation_degrees.y),
		deg_to_rad(rotation_degrees.z)
	))
	var scale_value: Vector3 = pose.get("%s_scale" % semantic, Vector3.ONE)
	var result := base_pose
	result.origin += pose.get("%s_position" % semantic, Vector3.ZERO) as Vector3
	result.basis = (
		base_pose.basis
		* Basis(rotation).scaled(scale_value)
	)
	return result


func _apply_torso_reach_pose(
	skeleton: Skeleton3D,
	pose: Dictionary,
	record_result: bool
) -> void:
	var requested_value: Variant = pose.get("torso_reach_offset", Vector3.ZERO)
	if not requested_value is Vector3 or not _has_cached_chain(TORSO_CHAIN):
		return
	var requested_rig := requested_value as Vector3
	if requested_rig.length_squared() <= EPSILON:
		return

	var hips_index := int(_bone_indices["hips"])
	var spine_index := int(_bone_indices["spine"])
	var chest_index := int(_bone_indices["chest"])
	var hips_global := skeleton.get_bone_global_pose(hips_index)
	var spine_global := skeleton.get_bone_global_pose(spine_index)
	var chest_global := skeleton.get_bone_global_pose(chest_index)
	var upper_length := hips_global.origin.distance_to(spine_global.origin)
	var lower_length := spine_global.origin.distance_to(chest_global.origin)
	var current_chain := chest_global.origin - hips_global.origin
	if (
		upper_length <= MIN_BEND_LENGTH
		or lower_length <= MIN_BEND_LENGTH
		or current_chain.length_squared() <= EPSILON
	):
		return

	var requested_skeleton := _rig_vector_to_skeleton(skeleton, requested_rig)
	var desired_chain := chest_global.origin + requested_skeleton - hips_global.origin
	if desired_chain.length_squared() <= EPSILON:
		return
	var full_swing := Quaternion(current_chain.normalized(), desired_chain.normalized())
	var full_angle := full_swing.get_angle()
	var lean_limit := deg_to_rad(clampf(
		float(pose.get(
			"torso_reach_lean_degrees",
			DEFAULT_TORSO_REACH_LEAN_DEGREES
		)),
		0.0,
		MAX_TORSO_REACH_LEAN_DEGREES
	))
	var selected_angle := minf(full_angle, lean_limit)
	var selected_swing := Quaternion.IDENTITY
	if full_angle > EPSILON:
		selected_swing = Quaternion.IDENTITY.slerp(
			full_swing,
			selected_angle / full_angle
		)
	var target_direction := Basis(selected_swing) * current_chain.normalized()
	var minimum_distance := absf(upper_length - lower_length) + MIN_BEND_LENGTH
	var maximum_distance := maxf(
		upper_length + lower_length - MIN_BEND_LENGTH,
		minimum_distance
	)
	var target_distance := clampf(
		desired_chain.length(),
		minimum_distance,
		maximum_distance
	)
	var target_chest_origin := hips_global.origin + target_direction * target_distance
	var torso_forward := _rig_vector_to_skeleton(skeleton, Vector3.FORWARD)
	var pole := spine_global.origin + torso_forward.normalized() * maxf(
		(upper_length + lower_length) * 0.35,
		0.1
	)
	var target_spine_origin := solve_two_bone_joint(
		hips_global.origin,
		target_chest_origin,
		pole,
		upper_length,
		lower_length
	)
	var current_child_offset := (
		spine_global.basis.inverse()
		* (chest_global.origin - spine_global.origin)
	)
	var target_spine_basis := align_chain_basis(
		spine_global.basis,
		current_child_offset,
		target_chest_origin - target_spine_origin
	)
	var target_spine_global := Transform3D(target_spine_basis, target_spine_origin)
	var target_chest_global := Transform3D(chest_global.basis, target_chest_origin)
	_set_global_pose(
		skeleton,
		spine_index,
		target_spine_global,
		hips_global,
		_bone_rests["spine"] as Transform3D
	)
	_set_global_pose(
		skeleton,
		chest_index,
		target_chest_global,
		target_spine_global,
		_bone_rests["chest"] as Transform3D
	)
	skeleton.force_update_all_bone_transforms()
	if record_result:
		_last_applied_torso_poses["spine"] = skeleton.get_bone_pose(spine_index)
		_last_applied_torso_poses["chest"] = skeleton.get_bone_pose(chest_index)
		_torso_reach_metrics = {
			"requested_offset": requested_rig,
			"applied_offset": _skeleton_vector_to_rig(
				skeleton,
				target_chest_origin - chest_global.origin
			),
			"lean_degrees": rad_to_deg(selected_angle),
			"angle_clamped": full_angle > lean_limit + EPSILON,
		}


func _solve_arm(skeleton: Skeleton3D, side: String) -> void:
	var chain: Array = ARM_CHAINS[side]
	if not _has_cached_chain(chain):
		return
	_reset_chain(skeleton, chain)
	var target_rig: Transform3D = _pose["%s_hand_transform" % side]
	var target := _rig_transform_to_skeleton(skeleton, target_rig)
	var hand_key := str(chain[3])
	var hand_rest: Transform3D = _bone_rests[hand_key]
	var correction: Basis = _basis_corrections.get(hand_key, Basis.IDENTITY)
	var desired_hand_basis := (target.basis.orthonormalized() * correction).orthonormalized()
	var desired_wrist_basis := (desired_hand_basis * hand_rest.basis.inverse()).orthonormalized()
	var desired_wrist_origin := target.origin - desired_wrist_basis * hand_rest.origin
	var upper_key := str(chain[0])
	var lower_key := str(chain[1])
	var wrist_key := str(chain[2])
	var upper_index := int(_bone_indices[upper_key])
	var lower_index := int(_bone_indices[lower_key])
	var wrist_index := int(_bone_indices[wrist_key])
	var hand_index := int(_bone_indices[hand_key])
	var parent_global := skeleton.get_bone_global_pose(skeleton.get_bone_parent(upper_index))
	var upper_rest: Transform3D = _bone_rests[upper_key]
	var lower_rest: Transform3D = _bone_rests[lower_key]
	var wrist_rest: Transform3D = _bone_rests[wrist_key]
	var upper_length := lower_rest.origin.length()
	var lower_length := wrist_rest.origin.length()
	var raw_upper_base := parent_global * upper_rest
	var upper_base := _articulate_shoulder_base(
		parent_global,
		raw_upper_base,
		desired_wrist_origin,
		upper_length + lower_length - MIN_BEND_LENGTH,
		_shoulder_swing_degrees(_pose, side)
	)
	_shoulder_offsets["%s_shoulder" % side] = (
		raw_upper_base.origin.distance_to(upper_base.origin) * _world_scale(skeleton)
	)
	var pole := _resolve_pole(skeleton, side, "arm")
	var reachable_wrist_origin := _clamp_two_bone_target(
		upper_base.origin,
		desired_wrist_origin,
		pole,
		upper_length,
		lower_length
	)
	var joint := solve_two_bone_joint(
		upper_base.origin,
		reachable_wrist_origin,
		pole,
		upper_length,
		lower_length
	)
	var upper_global := Transform3D(
		align_chain_basis(upper_base.basis, lower_rest.origin, joint - upper_base.origin),
		upper_base.origin
	)
	var lower_base := upper_global * lower_rest
	var lower_global := Transform3D(
		align_chain_basis(lower_base.basis, wrist_rest.origin, reachable_wrist_origin - joint),
		joint
	)
	var wrist_global := Transform3D(desired_wrist_basis, reachable_wrist_origin)
	_set_global_pose(skeleton, upper_index, upper_global, parent_global, upper_rest)
	_set_global_pose(skeleton, lower_index, lower_global, upper_global, lower_rest)
	_set_global_pose(skeleton, wrist_index, wrist_global, lower_global, wrist_rest)
	# The imported locomotion clips can animate the terminal hand bone. Write the
	# solved endpoint explicitly instead of reporting wrist * hand_rest while
	# leaving a stale hand-local pose on screen.
	var hand_global := wrist_global * hand_rest
	_set_global_pose(skeleton, hand_index, hand_global, wrist_global, hand_rest)
	var world_scale := _world_scale(skeleton)
	_endpoint_errors["%s_hand" % side] = (
		hand_global.origin.distance_to(target.origin) * world_scale
	)
	_reach_clamps["%s_hand" % side] = (
		desired_wrist_origin.distance_to(reachable_wrist_origin) * world_scale
	)


func _solve_leg(skeleton: Skeleton3D, side: String) -> void:
	var chain: Array = LEG_CHAINS[side]
	if not _has_cached_chain(chain):
		return
	_reset_chain(skeleton, chain)
	var target_rig: Transform3D = _pose["%s_foot_transform" % side]
	var target := _rig_transform_to_skeleton(skeleton, target_rig)
	var upper_key := str(chain[0])
	var lower_key := str(chain[1])
	var foot_key := str(chain[2])
	var upper_index := int(_bone_indices[upper_key])
	var lower_index := int(_bone_indices[lower_key])
	var foot_index := int(_bone_indices[foot_key])
	var parent_global := skeleton.get_bone_global_pose(skeleton.get_bone_parent(upper_index))
	var upper_rest: Transform3D = _bone_rests[upper_key]
	var lower_rest: Transform3D = _bone_rests[lower_key]
	var foot_rest: Transform3D = _bone_rests[foot_key]
	var upper_base := parent_global * upper_rest
	var upper_length := lower_rest.origin.length()
	var lower_length := foot_rest.origin.length()
	var pole := _resolve_pole(skeleton, side, "leg")
	var reachable_foot_origin := _clamp_two_bone_target(
		upper_base.origin,
		target.origin,
		pole,
		upper_length,
		lower_length
	)
	var joint := solve_two_bone_joint(
		upper_base.origin,
		reachable_foot_origin,
		pole,
		upper_length,
		lower_length
	)
	var upper_global := Transform3D(
		align_chain_basis(upper_base.basis, lower_rest.origin, joint - upper_base.origin),
		upper_base.origin
	)
	var lower_base := upper_global * lower_rest
	var lower_global := Transform3D(
		align_chain_basis(lower_base.basis, foot_rest.origin, reachable_foot_origin - joint),
		joint
	)
	var foot_correction: Basis = _basis_corrections.get(foot_key, Basis.IDENTITY)
	var foot_basis: Basis = (target.basis.orthonormalized() * foot_correction).orthonormalized()
	var foot_global := Transform3D(foot_basis, reachable_foot_origin)
	_set_global_pose(skeleton, upper_index, upper_global, parent_global, upper_rest)
	_set_global_pose(skeleton, lower_index, lower_global, upper_global, lower_rest)
	_set_global_pose(skeleton, foot_index, foot_global, lower_global, foot_rest)
	var world_scale := _world_scale(skeleton)
	_endpoint_errors["%s_foot" % side] = (
		foot_global.origin.distance_to(target.origin) * world_scale
	)
	_reach_clamps["%s_foot" % side] = (
		target.origin.distance_to(reachable_foot_origin) * world_scale
	)


func _has_cached_chain(chain: Array) -> bool:
	for semantic: Variant in chain:
		if int(_bone_indices.get(str(semantic), -1)) < 0:
			return false
	return true


func _reset_chain(skeleton: Skeleton3D, chain: Array) -> void:
	for semantic: Variant in chain:
		skeleton.reset_bone_pose(int(_bone_indices[str(semantic)]))


func _resolve_pole(skeleton: Skeleton3D, side: String, kind: String) -> Vector3:
	return _resolve_pole_from_pose(skeleton, side, kind, _pose)


func _resolve_pole_from_pose(
	skeleton: Skeleton3D,
	side: String,
	kind: String,
	pose: Dictionary
) -> Vector3:
	var key := "%s_%s_pole" % [side, kind]
	var side_sign := -1.0 if side == "left" else 1.0
	var fallback := (
		Vector3(side_sign * 0.7, 0.78, 0.34)
		if kind == "arm"
		else Vector3(side_sign * 0.2, 0.34, -0.5)
	)
	var pole_rig: Vector3 = pose.get(key, fallback)
	return _rig_transform_to_skeleton(skeleton, Transform3D(Basis.IDENTITY, pole_rig)).origin


func _shoulder_swing_degrees(pose: Dictionary, side: String) -> float:
	var profile_limit := clampf(
		float(_profile.get(
			"max_shoulder_swing_degrees",
			MAX_SHOULDER_SWING_DEGREES
		)),
		0.0,
		HARD_MAX_SHOULDER_SWING_DEGREES
	)
	return clampf(
		float(pose.get(
			"%s_shoulder_swing_degrees" % side,
			DEFAULT_SHOULDER_SWING_DEGREES
		)),
		0.0,
		profile_limit
	)


func _articulate_shoulder_base(
	parent_global: Transform3D,
	upper_base: Transform3D,
	target_origin: Vector3,
	arm_reach: float,
	max_swing_degrees: float
) -> Transform3D:
	var shoulder_offset := upper_base.origin - parent_global.origin
	var target_offset := target_origin - parent_global.origin
	if (
		shoulder_offset.length_squared() <= EPSILON
		or target_offset.length_squared() <= EPSILON
		or max_swing_degrees <= EPSILON
		or upper_base.origin.distance_to(target_origin) <= arm_reach
	):
		return upper_base

	var full_swing := Quaternion(shoulder_offset.normalized(), target_offset.normalized())
	var full_angle := full_swing.get_angle()
	if full_angle <= EPSILON:
		return upper_base
	var maximum_angle := minf(deg_to_rad(max_swing_degrees), full_angle)
	var maximum_ratio := maximum_angle / full_angle
	var maximum_rotation := Quaternion.IDENTITY.slerp(full_swing, maximum_ratio)
	var maximum_origin := parent_global.origin + Basis(maximum_rotation) * shoulder_offset
	if maximum_origin.distance_to(target_origin) >= upper_base.origin.distance_to(target_origin):
		return upper_base

	var selected_ratio := maximum_ratio
	if maximum_origin.distance_to(target_origin) <= arm_reach:
		var low := 0.0
		var high := maximum_ratio
		for _iteration: int in range(10):
			var middle := (low + high) * 0.5
			var middle_rotation := Quaternion.IDENTITY.slerp(full_swing, middle)
			var middle_origin := parent_global.origin + Basis(middle_rotation) * shoulder_offset
			if middle_origin.distance_to(target_origin) <= arm_reach:
				high = middle
			else:
				low = middle
		selected_ratio = high

	var shoulder_rotation := Quaternion.IDENTITY.slerp(full_swing, selected_ratio)
	return Transform3D(
		(Basis(shoulder_rotation) * upper_base.basis).orthonormalized(),
		parent_global.origin + Basis(shoulder_rotation) * shoulder_offset
	)


func _fit_hand_target_current(
	skeleton: Skeleton3D,
	side: String,
	target_rig: Transform3D,
	pose: Dictionary
) -> Transform3D:
	var chain: Array = ARM_CHAINS.get(side, [])
	if not _has_cached_chain(chain):
		return target_rig
	var target := _rig_transform_to_skeleton(skeleton, target_rig)
	var hand_key := str(chain[3])
	var hand_rest: Transform3D = _bone_rests[hand_key]
	var correction: Basis = _basis_corrections.get(hand_key, Basis.IDENTITY)
	var desired_hand_basis := (target.basis.orthonormalized() * correction).orthonormalized()
	var desired_wrist_basis := (desired_hand_basis * hand_rest.basis.inverse()).orthonormalized()
	var desired_wrist_origin := target.origin - desired_wrist_basis * hand_rest.origin
	var upper_key := str(chain[0])
	var lower_key := str(chain[1])
	var wrist_key := str(chain[2])
	var upper_index := int(_bone_indices[upper_key])
	var parent_global := skeleton.get_bone_global_pose(skeleton.get_bone_parent(upper_index))
	var upper_rest: Transform3D = _bone_rests[upper_key]
	var lower_rest: Transform3D = _bone_rests[lower_key]
	var wrist_rest: Transform3D = _bone_rests[wrist_key]
	var upper_base := _articulate_shoulder_base(
		parent_global,
		parent_global * upper_rest,
		desired_wrist_origin,
		lower_rest.origin.length() + wrist_rest.origin.length() - MIN_BEND_LENGTH,
		_shoulder_swing_degrees(pose, side)
	)
	var pole := _resolve_pole_from_pose(skeleton, side, "arm", pose)
	var reachable_wrist_origin := _clamp_two_bone_target(
		upper_base.origin,
		desired_wrist_origin,
		pole,
		lower_rest.origin.length(),
		wrist_rest.origin.length()
	)
	var resolved_hand := (
		Transform3D(desired_wrist_basis, reachable_wrist_origin) * hand_rest
	)
	var fitted := _skeleton_transform_to_rig(skeleton, resolved_hand)
	fitted.basis = target_rig.basis
	return fitted


func _fit_hand_translation_current(
	skeleton: Skeleton3D,
	side: String,
	target_rig: Transform3D,
	pose: Dictionary,
	translation_axes: Array
) -> Vector3:
	if translation_axes.is_empty():
		return (
			_fit_hand_target_current(skeleton, side, target_rig, pose).origin
			- target_rig.origin
		)
	var chain: Array = ARM_CHAINS.get(side, [])
	if not _has_cached_chain(chain):
		return Vector3.ZERO
	var target := _rig_transform_to_skeleton(skeleton, target_rig)
	var hand_key := str(chain[3])
	var hand_rest: Transform3D = _bone_rests[hand_key]
	var correction: Basis = _basis_corrections.get(hand_key, Basis.IDENTITY)
	var desired_hand_basis := (target.basis.orthonormalized() * correction).orthonormalized()
	var desired_wrist_basis := (desired_hand_basis * hand_rest.basis.inverse()).orthonormalized()
	var desired_wrist_origin := target.origin - desired_wrist_basis * hand_rest.origin
	var upper_key := str(chain[0])
	var lower_key := str(chain[1])
	var wrist_key := str(chain[2])
	var upper_index := int(_bone_indices[upper_key])
	var parent_global := skeleton.get_bone_global_pose(skeleton.get_bone_parent(upper_index))
	var upper_length := (_bone_rests[lower_key] as Transform3D).origin.length()
	var lower_length := (_bone_rests[wrist_key] as Transform3D).origin.length()
	var minimum_distance := absf(upper_length - lower_length) + MIN_BEND_LENGTH
	var maximum_distance := maxf(
		upper_length + lower_length - MIN_BEND_LENGTH,
		minimum_distance
	)
	var upper_base := _articulate_shoulder_base(
		parent_global,
		parent_global * (_bone_rests[upper_key] as Transform3D),
		desired_wrist_origin,
		maximum_distance,
		_shoulder_swing_degrees(pose, side)
	)

	var skeleton_axes: Array[Vector3] = []
	for axis_value: Variant in translation_axes:
		if not axis_value is Vector3:
			continue
		var axis := _rig_vector_to_skeleton(skeleton, axis_value as Vector3)
		for accepted: Vector3 in skeleton_axes:
			axis -= accepted * axis.dot(accepted)
		if axis.length_squared() <= EPSILON:
			continue
		skeleton_axes.append(axis.normalized())
	if skeleton_axes.is_empty():
		return Vector3.ZERO

	var target_delta := desired_wrist_origin - upper_base.origin
	var parallel := Vector3.ZERO
	for axis: Vector3 in skeleton_axes:
		parallel += axis * target_delta.dot(axis)
	var perpendicular := target_delta - parallel
	var distance := target_delta.length()
	if distance <= maximum_distance and distance >= minimum_distance:
		return Vector3.ZERO
	var desired_parallel_length := parallel.length()
	if distance > maximum_distance:
		var available_squared := (
			maximum_distance * maximum_distance
			- perpendicular.length_squared()
		)
		desired_parallel_length = sqrt(maxf(available_squared, 0.0))
	else:
		var required_squared := (
			minimum_distance * minimum_distance
			- perpendicular.length_squared()
		)
		desired_parallel_length = sqrt(maxf(required_squared, 0.0))
	var parallel_direction := (
		parallel.normalized() if parallel.length_squared() > EPSILON else skeleton_axes[0]
	)
	var skeleton_translation := (
		parallel_direction * desired_parallel_length - parallel
	)
	return _skeleton_vector_to_rig(skeleton, skeleton_translation)


static func solve_two_bone_joint(
	start: Vector3,
	target: Vector3,
	pole: Vector3,
	upper_length: float,
	lower_length: float
) -> Vector3:
	var reachable_target := _clamp_two_bone_target(
		start,
		target,
		pole,
		upper_length,
		lower_length
	)
	var target_delta := reachable_target - start
	var distance := maxf(target_delta.length(), EPSILON)
	var direction := target_delta / distance
	var pole_delta := pole - start
	var bend_direction := pole_delta - direction * pole_delta.dot(direction)
	if bend_direction.length_squared() <= EPSILON:
		bend_direction = direction.cross(Vector3.UP)
	if bend_direction.length_squared() <= EPSILON:
		bend_direction = direction.cross(Vector3.RIGHT)
	bend_direction = bend_direction.normalized()
	var along := (
		upper_length * upper_length
		- lower_length * lower_length
		+ distance * distance
	) / maxf(2.0 * distance, EPSILON)
	var height_squared := maxf(
		upper_length * upper_length - along * along,
		MIN_BEND_LENGTH * MIN_BEND_LENGTH
	)
	return start + direction * along + bend_direction * sqrt(height_squared)


static func _clamp_two_bone_target(
	start: Vector3,
	target: Vector3,
	pole: Vector3,
	upper_length: float,
	lower_length: float
) -> Vector3:
	var safe_upper := maxf(absf(upper_length), MIN_BEND_LENGTH)
	var safe_lower := maxf(absf(lower_length), MIN_BEND_LENGTH)
	var minimum_distance := absf(safe_upper - safe_lower) + MIN_BEND_LENGTH
	var maximum_distance := maxf(
		safe_upper + safe_lower - MIN_BEND_LENGTH,
		minimum_distance
	)
	var target_delta := target - start
	var raw_distance := target_delta.length()
	var direction := target_delta / raw_distance if raw_distance > EPSILON else Vector3.ZERO
	if direction.length_squared() <= EPSILON:
		var pole_delta := pole - start
		direction = pole_delta.normalized() if pole_delta.length_squared() > EPSILON else Vector3.FORWARD
	return start + direction * clampf(raw_distance, minimum_distance, maximum_distance)


static func align_chain_basis(
	base_basis: Basis,
	rest_child_offset: Vector3,
	desired_segment: Vector3
) -> Basis:
	if rest_child_offset.length_squared() <= EPSILON or desired_segment.length_squared() <= EPSILON:
		return base_basis.orthonormalized()
	var rest_direction := (base_basis.orthonormalized() * rest_child_offset.normalized()).normalized()
	var swing := Quaternion(rest_direction, desired_segment.normalized())
	return (Basis(swing) * base_basis).orthonormalized()


func _set_global_pose(
	skeleton: Skeleton3D,
	bone_index: int,
	desired_global: Transform3D,
	parent_global: Transform3D,
	_bone_rest: Transform3D
) -> void:
	# Skeleton3D bone poses already include the rest-local translation.
	skeleton.set_bone_pose(
		bone_index,
		parent_global.affine_inverse() * desired_global
	)


func _rig_transform_to_skeleton(skeleton: Skeleton3D, rig_transform: Transform3D) -> Transform3D:
	if _rig_root == null:
		return rig_transform
	return skeleton.global_transform.affine_inverse() * _rig_root.global_transform * rig_transform


func _skeleton_transform_to_rig(skeleton: Skeleton3D, skeleton_transform: Transform3D) -> Transform3D:
	if _rig_root == null:
		return skeleton_transform
	return _rig_root.global_transform.affine_inverse() * skeleton.global_transform * skeleton_transform


func _rig_vector_to_skeleton(skeleton: Skeleton3D, value: Vector3) -> Vector3:
	if _rig_root == null:
		return value
	return skeleton.global_basis.inverse() * _rig_root.global_basis * value


func _skeleton_vector_to_rig(skeleton: Skeleton3D, value: Vector3) -> Vector3:
	if _rig_root == null:
		return value
	return _rig_root.global_basis.inverse() * skeleton.global_basis * value


func _world_scale(skeleton: Skeleton3D) -> float:
	var scale_value := skeleton.global_transform.basis.get_scale().abs()
	return (scale_value.x + scale_value.y + scale_value.z) / 3.0
