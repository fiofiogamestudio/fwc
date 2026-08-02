class_name AppRoot
extends Node

const FPoolScript = preload("../pool/_pool.gd")
const FAssetScript = preload("../_asset.gd")
const FEventBusScript = preload("../event/_event_bus.gd")
const FLogScript = preload("../log/_log.gd")
const FDisplayScript = preload("../display/_display.gd")
const FDebugScript = preload("../debug/_debug.gd")
const FUIScript = preload("../../vu/ui/_ui.gd")
const FAudioScript = preload("../../vu/audio/_audio.gd")
const SystemManagerScript = preload("_system_manager.gd")

var _mode_host: Node
var _pool: RefCounted
var _asset: RefCounted
var _events: RefCounted
var _log: RefCounted
var _display: RefCounted
var _debug: RefCounted
var _ui_root: CanvasLayer
var _ui: RefCounted
var _audio: RefCounted
var _app_system_manager: RefCounted
var _active_mode: Variant = null
var _is_switching_mode: bool = false


func _ready() -> void:
	_mode_host = Node.new()
	_mode_host.name = "ModeHost"
	add_child(_mode_host)

	_log = FLogScript.new()
	_events = FEventBusScript.new()
	_asset = FAssetScript.new()
	_pool = FPoolScript.new()
	_pool.setup(_mode_host)

	_display = FDisplayScript.new()
	_display.setup(get_window())
	_debug = FDebugScript.new()
	_debug.setup()

	_ui_root = CanvasLayer.new()
	_ui_root.name = "UIRoot"
	add_child(_ui_root)

	_ui = FUIScript.new()
	_ui.setup(_ui_root)
	_audio = FAudioScript.new()
	_audio.setup(self)

	_app_system_manager = SystemManagerScript.new()
	on_app_setup()
	if not _app_system_manager.init_all():
		push_error("AppRoot failed to initialize global systems.")
		return

	on_app_ready()


func _exit_tree() -> void:
	if _active_mode:
		_active_mode.exit()
	_active_mode = null
	if _app_system_manager:
		_app_system_manager.shutdown_all()
	_app_system_manager = null
	if _ui:
		_ui.clear()
	if _pool:
		_pool.flush()
	if _asset:
		_asset.unload()
	if _audio:
		_audio.clear()
	if _events:
		_events.clear()
	if _debug:
		_debug.clear()
	_ui = null
	_audio = null
	_pool = null
	_asset = null
	_events = null
	_log = null
	_display = null
	_debug = null


func _physics_process(dt: float) -> void:
	if _app_system_manager:
		_app_system_manager.tick(dt)
	if _active_mode:
		_active_mode.tick(dt)


func _unhandled_input(event: InputEvent) -> void:
	if _active_mode and _active_mode.has_method("handle_input"):
		_active_mode.handle_input(event)


func on_app_setup() -> void:
	pass


func on_app_ready() -> void:
	pass


func switch_mode(mode: Variant, context: Variant = null) -> Variant:
	if _is_switching_mode:
		push_error("AppRoot rejected a reentrant mode switch.")
		return _active_mode
	if not _is_valid_mode(mode):
		push_error("AppRoot requires a mode with enter/tick/exit methods.")
		return _active_mode

	_is_switching_mode = true
	if _active_mode:
		_active_mode.exit()
		_active_mode = null
	_ui.close_all()
	_pool.flush()
	_clear_mode_host()

	var entered: Variant = mode.enter(self, context)
	if not (entered is bool and entered):
		if mode.has_method("exit"):
			mode.exit()
		_ui.close_all()
		_pool.flush()
		_clear_mode_host()
		_is_switching_mode = false
		on_mode_switch_failed(mode)
		return null
	_active_mode = mode
	_is_switching_mode = false
	return _active_mode


func on_mode_switch_failed(_mode: Variant) -> void:
	pass


func add_app_system(
	id: StringName,
	system: Variant,
	context: Variant = null,
	phase: StringName = &"",
	dependencies: Array = []
) -> bool:
	if _app_system_manager == null:
		push_error("App system manager is not ready.")
		return false
	return _app_system_manager.add_system(id, system, context, phase, dependencies)


func set_app_system_phase_order(order: Array) -> void:
	if _app_system_manager:
		_app_system_manager.set_phase_order(order)


func bind_app_system_refs(refs: Dictionary) -> bool:
	if _app_system_manager == null:
		return false
	return _app_system_manager.bind_refs(refs)


func app_system_manager() -> Variant:
	return _app_system_manager


func _is_valid_mode(mode: Variant) -> bool:
	return mode != null and mode.has_method("enter") and mode.has_method("tick") and mode.has_method("exit")


func _clear_mode_host() -> void:
	for child in _mode_host.get_children():
		if child.is_queued_for_deletion():
			continue
		_mode_host.remove_child(child)
		child.queue_free()


func mode_host() -> Node:
	return _mode_host


func pool() -> Variant:
	return _pool


func asset() -> Variant:
	return _asset


func events() -> Variant:
	return _events


func log_service() -> Variant:
	return _log


func display() -> Variant:
	return _display


func debug_service() -> Variant:
	return _debug


func ui_root() -> CanvasLayer:
	return _ui_root


func ui() -> Variant:
	return _ui


func audio() -> Variant:
	return _audio
