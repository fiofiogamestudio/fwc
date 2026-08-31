class_name FProceduralRigAdapter
extends RefCounted

const POSE_FAMILY_TOPDOWN_HUMANOID: StringName = &"topdown_humanoid_v1"

var _rig_root: Node3D = null
var _profile: Dictionary = {}
var _pose: Dictionary = {}
var _errors := PackedStringArray()
var _configured: bool = false


func setup(rig_root: Node3D, profile: Dictionary = {}) -> PackedStringArray:
	_rig_root = rig_root
	_profile = profile.duplicate(true)
	_pose.clear()
	_errors.clear()
	_configured = _rig_root != null and is_instance_valid(_rig_root)
	if not _configured:
		_errors.append("procedural rig adapter requires a valid rig root")
	return _errors.duplicate()


func submit_pose(pose: Dictionary) -> void:
	_pose = pose.duplicate(true)


func clear_pose() -> void:
	_pose.clear()


func solve_now() -> bool:
	return is_ready()


func is_ready() -> bool:
	return (
		_configured
		and _rig_root != null
		and is_instance_valid(_rig_root)
		and _errors.is_empty()
	)


func is_configured() -> bool:
	return is_ready()


func errors() -> PackedStringArray:
	return _errors.duplicate()


func backend_id() -> StringName:
	return &"base"


func pose_family() -> StringName:
	return StringName(str(_profile.get(
		"pose_family",
		POSE_FAMILY_TOPDOWN_HUMANOID
	)))


func supports_pose_family(value: StringName) -> bool:
	return value == pose_family()


func semantic_transform(_semantic: StringName) -> Transform3D:
	return Transform3D.IDENTITY


func get_socket(socket_id: StringName) -> Node3D:
	if _rig_root == null:
		return null
	var paths: Dictionary = _profile.get("sockets", {})
	var path_value: Variant = paths.get(str(socket_id), NodePath())
	if path_value is NodePath and not (path_value as NodePath).is_empty():
		return _rig_root.get_node_or_null(path_value as NodePath) as Node3D
	if path_value is String and not str(path_value).is_empty():
		return _rig_root.get_node_or_null(NodePath(str(path_value))) as Node3D
	return _rig_root.find_child(str(socket_id), true, false) as Node3D


func fit_hand_targets(
	_targets: Dictionary,
	_pose_override: Dictionary = {},
	_max_iterations: int = 8,
	_translation_axes: Array = []
) -> Dictionary:
	return {
		"translation": Vector3.ZERO,
		"residuals": {},
		"max_residual": 0.0,
		"iterations": 0,
		"feasible": is_ready(),
	}


func metrics() -> Dictionary:
	return {
		"backend": str(backend_id()),
		"pose_family": str(pose_family()),
		"ready": is_ready(),
		"errors": errors(),
		"root_motion_enabled": false,
	}
