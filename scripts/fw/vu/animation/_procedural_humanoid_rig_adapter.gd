class_name FProceduralHumanoidRigAdapter
extends "res://fw/scripts/fw/vu/animation/_procedural_rig_adapter.gd"

const HumanoidModifierScript = preload(
	"res://fw/scripts/fw/vu/animation/_procedural_humanoid_modifier.gd"
)

var _skeleton: Skeleton3D = null
var _modifier: Variant = null
var _semantic_bones: Dictionary = {}
var _semantic_transforms: Dictionary = {}


func setup(rig_root: Node3D, profile: Dictionary = {}) -> PackedStringArray:
	super.setup(rig_root, profile)
	_skeleton = profile.get("skeleton", null) as Skeleton3D
	_semantic_bones = (profile.get("bones", {}) as Dictionary).duplicate(true)
	_semantic_transforms.clear()
	if _skeleton == null or not is_instance_valid(_skeleton):
		_errors.append("humanoid rig adapter requires a Skeleton3D")
		_configured = false
		return errors()
	if _semantic_bones.is_empty():
		_errors.append("humanoid rig adapter requires semantic bones")
		_configured = false
		return errors()

	_modifier = HumanoidModifierScript.new()
	_modifier.name = "ProceduralHumanoidModifier"
	_skeleton.add_child(_modifier)
	var modifier_profile := profile.duplicate(true)
	modifier_profile.erase("skeleton")
	modifier_profile.erase("pose_family")
	modifier_profile.erase("sockets")
	_errors.append_array(_modifier.configure(modifier_profile, _rig_root))
	_configured = _errors.is_empty()
	if _configured:
		# The adapter owns the solve order. Automatic processing would solve twice
		# and can overwrite the host's lower/upper-body composition in the same frame.
		_modifier.active = false
	_capture_semantic_transforms()
	return errors()


func submit_pose(pose: Dictionary) -> void:
	super.submit_pose(pose)
	if _modifier != null:
		_modifier.submit_pose(pose)


func clear_pose() -> void:
	super.clear_pose()
	if _modifier != null:
		_modifier.clear_pose()


func solve_now() -> bool:
	return is_ready() and bool(_modifier.solve_now())


func is_ready() -> bool:
	return (
		super.is_ready()
		and _skeleton != null
		and is_instance_valid(_skeleton)
		and _modifier != null
		and bool(_modifier.is_configured())
	)


func backend_id() -> StringName:
	return &"humanoid_skeleton"


func semantic_transform(semantic: StringName) -> Transform3D:
	return _semantic_transforms.get(str(semantic), Transform3D.IDENTITY) as Transform3D


func fit_hand_targets(
	targets: Dictionary,
	pose_override: Dictionary = {},
	max_iterations: int = 8,
	translation_axes: Array = []
) -> Dictionary:
	if not is_ready():
		return super.fit_hand_targets(
			targets,
			pose_override,
			max_iterations,
			translation_axes
		)
	return _modifier.fit_hand_targets(
		targets,
		pose_override,
		max_iterations,
		translation_axes
	) as Dictionary


func metrics() -> Dictionary:
	var result := super.metrics()
	result["backend"] = str(backend_id())
	result["semantic_count"] = _semantic_transforms.size()
	var modifier_metrics: Dictionary = _modifier.metrics() if _modifier != null else {}
	for key: Variant in modifier_metrics:
		result[key] = modifier_metrics[key]
	return result


func _capture_semantic_transforms() -> void:
	_semantic_transforms.clear()
	if _skeleton == null:
		return
	_skeleton.force_update_all_bone_transforms()
	var skeleton_to_rig := _transform_between(_skeleton, _rig_root)
	for semantic_value: Variant in _semantic_bones:
		var semantic := str(semantic_value)
		var bone_name := StringName(str(_semantic_bones[semantic_value]))
		var bone_index := _skeleton.find_bone(bone_name)
		if bone_index < 0:
			continue
		_semantic_transforms[semantic] = (
			skeleton_to_rig * _skeleton.get_bone_global_pose(bone_index)
		)


func _transform_between(from: Node3D, to: Node3D) -> Transform3D:
	if from == to:
		return Transform3D.IDENTITY
	if to.is_ancestor_of(from):
		var result := Transform3D.IDENTITY
		var current: Node = from
		while current != null and current != to:
			if current is Node3D:
				result = (current as Node3D).transform * result
			current = current.get_parent()
		return result if current == to else Transform3D.IDENTITY
	return to.global_transform.affine_inverse() * from.global_transform
