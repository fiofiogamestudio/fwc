class_name BaseMode
extends RefCounted

const SystemManagerScript = preload("_system_manager.gd")

var _root: Variant
var _context: Variant = null
var _system_manager: Variant
var _is_entered: bool = false
var _enter_error: String = ""


func enter(root: Variant, context: Variant = null) -> bool:
	if _is_entered:
		push_error("Mode is already entered.")
		return false
	if root == null:
		push_error("Mode requires a valid app root.")
		return false
	_root = root
	_context = context
	var parent_manager: Variant = null
	if _root.has_method("app_system_manager"):
		parent_manager = _root.app_system_manager()
	_system_manager = SystemManagerScript.new(parent_manager)
	_is_entered = true
	_enter_error = ""
	return true


func tick(dt: float) -> void:
	if _system_manager:
		_system_manager.tick(dt)


func handle_input(_event: InputEvent) -> void:
	pass


func exit() -> void:
	if not _is_entered:
		return
	if _system_manager:
		_system_manager.shutdown_all()
	_system_manager = null
	_context = null
	_root = null
	_is_entered = false


func add_system(
	id: StringName,
	system: Variant,
	context: Variant = null,
	phase: StringName = &"",
	dependencies: Array = []
) -> bool:
	if _system_manager == null:
		push_error("Mode system manager is not ready.")
		return false
	return _system_manager.add_system(id, system, context, phase, dependencies)


func set_system_phase_order(order: Array) -> void:
	if _system_manager == null:
		push_error("Mode system manager is not ready.")
		return
	_system_manager.set_phase_order(order)


func bind_system_refs(refs: Dictionary) -> bool:
	if _system_manager == null:
		push_error("Mode system manager is not ready.")
		return false
	return _system_manager.bind_refs(refs)


func init_systems() -> bool:
	if _system_manager:
		return _system_manager.init_all()
	return false


func is_entered() -> bool:
	return _is_entered


func fail_enter(message: String) -> bool:
	_enter_error = message
	return false


func enter_error() -> String:
	return _enter_error


func system_manager() -> Variant:
	return _system_manager


func system_context(id: StringName) -> Variant:
	if _system_manager == null:
		return null
	return _system_manager.get_context(id)


func mode_host() -> Node:
	return _root.mode_host()


func ui_root() -> Node:
	return _root.ui_root()


func pool() -> Variant:
	return _root.pool()


func asset() -> Variant:
	return _root.asset()


func ui() -> Variant:
	return _root.ui()


func events() -> Variant:
	return _root.events()


func audio() -> Variant:
	return _root.audio()


func display() -> Variant:
	return _root.display()


func debug_service() -> Variant:
	return _root.debug_service()


func log_service() -> Variant:
	return _root.log_service()
