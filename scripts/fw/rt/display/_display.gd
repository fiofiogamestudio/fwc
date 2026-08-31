class_name FDisplay
extends RefCounted

signal changed(settings: Dictionary)
signal applied(settings: Dictionary)

const DEFAULT_SIZE_OPTIONS := [
	Vector2i(1280, 720),
	Vector2i(1600, 900),
	Vector2i(1920, 1080),
	Vector2i(2560, 1440),
	Vector2i(3840, 2160),
]

var _window: Window = null
var _size_options: Array[Vector2i] = []
var _baseline: Dictionary = {}
var _pending: Dictionary = {}
var _windowed_size := Vector2i.ZERO
var _settings_path := ""
var _settings_section := "display"


func setup(
	window: Window,
	size_options: Array = [],
	settings_path: String = "",
	auto_load: bool = true
) -> void:
	_window = window
	set_size_options(DEFAULT_SIZE_OPTIONS if size_options.is_empty() else size_options)
	if _window != null:
		_windowed_size = _window.size
		_add_size_option(_windowed_size)
	configure_persistence(settings_path)
	begin_edit()
	if auto_load and not _settings_path.is_empty():
		load_saved(true)


func configure_persistence(path: String, section: String = "display") -> void:
	_settings_path = path.strip_edges()
	_settings_section = section.strip_edges() if not section.strip_edges().is_empty() else "display"


func set_size_options(values: Array) -> void:
	_size_options.clear()
	for raw_value in values:
		var value := _to_size(raw_value)
		if value.x <= 0 or value.y <= 0 or _size_options.has(value):
			continue
		_size_options.append(value)
	_size_options.sort_custom(
		func(left: Vector2i, right: Vector2i) -> bool:
			return left.x * left.y < right.x * right.y
	)


func size_options() -> Array[Vector2i]:
	return _size_options.duplicate()


func begin_edit() -> Dictionary:
	_baseline = current_settings()
	_pending = _baseline.duplicate(true)
	return pending_settings()


func pending_settings() -> Dictionary:
	return _pending.duplicate(true)


func pending_size() -> Vector2i:
	return _to_size(_pending.get("size", Vector2i.ZERO))


func pending_size_text(separator: String = " x ") -> String:
	var value := pending_size()
	return "%d%s%d" % [value.x, separator, value.y] if value != Vector2i.ZERO else ""


func has_changes() -> bool:
	return _normalized_settings(_pending) != _normalized_settings(_baseline)


func has_previous_size() -> bool:
	return _pending_size_index() > 0


func has_next_size() -> bool:
	var index := _pending_size_index()
	return index >= 0 and index < _size_options.size() - 1


func set_pending_size(value: Vector2i) -> void:
	if value.x <= 0 or value.y <= 0 or pending_size() == value:
		return
	_pending["size"] = value
	_emit_changed()


func step_pending_size(step: int) -> bool:
	if _size_options.is_empty() or step == 0:
		return false
	var index := _pending_size_index()
	if index < 0:
		index = _nearest_size_index(pending_size())
	var next_index := clampi(index + step, 0, _size_options.size() - 1)
	if next_index == index:
		return false
	_pending["size"] = _size_options[next_index]
	_emit_changed()
	return true


func set_pending_fullscreen(value: bool) -> void:
	if bool(_pending.get("fullscreen", false)) == value:
		return
	_pending["fullscreen"] = value
	_emit_changed()


func set_pending_vsync(value: bool) -> void:
	if bool(_pending.get("vsync", true)) == value:
		return
	_pending["vsync"] = value
	_emit_changed()


func apply(save: bool = true) -> bool:
	if _window == null:
		return false
	var requested := _normalized_settings(_pending)
	_apply_settings(requested)
	_baseline = _capture_applied_settings()
	_pending = _baseline.duplicate(true)
	if save and not _settings_path.is_empty():
		save_current()
	applied.emit(_baseline.duplicate(true))
	_emit_changed()
	return true


func cancel() -> bool:
	if _baseline.is_empty():
		return false
	var had_changes := has_changes()
	_pending = _baseline.duplicate(true)
	if had_changes:
		_emit_changed()
	return had_changes


func save_current() -> bool:
	if _settings_path.is_empty() or _baseline.is_empty():
		return false
	var config := ConfigFile.new()
	var settings := _normalized_settings(_baseline)
	config.set_value(_settings_section, "width", settings.size.x)
	config.set_value(_settings_section, "height", settings.size.y)
	config.set_value(_settings_section, "fullscreen", settings.fullscreen)
	config.set_value(_settings_section, "vsync", settings.vsync)
	var error := config.save(_settings_path)
	if error != OK:
		push_error("FDisplay could not save settings to '%s': %s" % [_settings_path, error])
		return false
	return true


func load_saved(apply_now: bool = false) -> bool:
	if _settings_path.is_empty() or not FileAccess.file_exists(_settings_path):
		return false
	var config := ConfigFile.new()
	var error := config.load(_settings_path)
	if error != OK:
		push_error("FDisplay could not load settings from '%s': %s" % [_settings_path, error])
		return false
	var fallback := _normalized_settings(_pending)
	var width := int(config.get_value(_settings_section, "width", fallback.size.x))
	var height := int(config.get_value(_settings_section, "height", fallback.size.y))
	_pending = {
		"size": Vector2i(maxi(1, width), maxi(1, height)),
		"fullscreen": bool(
			config.get_value(_settings_section, "fullscreen", fallback.fullscreen)
		),
		"vsync": bool(config.get_value(_settings_section, "vsync", fallback.vsync)),
	}
	if apply_now and _window != null:
		var requested := _normalized_settings(_pending)
		_apply_settings(requested)
		_baseline = _capture_applied_settings()
		_pending = _baseline.duplicate(true)
	_emit_changed()
	return true


func current_settings() -> Dictionary:
	if _window == null:
		return {}
	var window_id := _window.get_window_id()
	var fullscreen := _is_fullscreen()
	return {
		"size": _windowed_size if fullscreen else _window.size,
		"fullscreen": fullscreen,
		"vsync": DisplayServer.window_get_vsync_mode(window_id)
			!= DisplayServer.VSYNC_DISABLED,
	}


func _apply_settings(settings: Dictionary) -> void:
	var fullscreen := bool(settings.get("fullscreen", false))
	var target_size := _to_size(settings.get("size", _windowed_size))
	if fullscreen:
		if target_size.x > 0 and target_size.y > 0:
			_windowed_size = target_size
		_window.mode = Window.MODE_EXCLUSIVE_FULLSCREEN
	else:
		_window.mode = Window.MODE_WINDOWED
		if target_size.x > 0 and target_size.y > 0:
			_window.size = target_size
			_windowed_size = _window.size
			_center_window(_windowed_size)
	var vsync_mode := DisplayServer.VSYNC_ENABLED \
		if bool(settings.get("vsync", true)) else DisplayServer.VSYNC_DISABLED
	DisplayServer.window_set_vsync_mode(vsync_mode, _window.get_window_id())


func _center_window(target_size: Vector2i) -> void:
	var screen := _window.current_screen
	var usable := DisplayServer.screen_get_usable_rect(screen)
	_window.position = usable.position + (usable.size - target_size) / 2


func _pending_size_index() -> int:
	var index := _size_options.find(pending_size())
	return index if index >= 0 else _nearest_size_index(pending_size())


func _add_size_option(value: Vector2i) -> void:
	if value.x <= 0 or value.y <= 0 or _size_options.has(value):
		return
	_size_options.append(value)
	_size_options.sort_custom(
		func(left: Vector2i, right: Vector2i) -> bool:
			return left.x * left.y < right.x * right.y
	)


func _nearest_size_index(value: Vector2i) -> int:
	if _size_options.is_empty():
		return -1
	var best_index := 0
	var best_distance := INF
	for index in range(_size_options.size()):
		var candidate := _size_options[index]
		var distance := Vector2(candidate - value).length_squared()
		if distance < best_distance:
			best_distance = distance
			best_index = index
	return best_index


func _normalized_settings(settings: Dictionary) -> Dictionary:
	return {
		"size": _to_size(settings.get("size", Vector2i.ZERO)),
		"fullscreen": bool(settings.get("fullscreen", false)),
		"vsync": bool(settings.get("vsync", true)),
	}


func _capture_applied_settings() -> Dictionary:
	return _normalized_settings(current_settings())


func _is_fullscreen() -> bool:
	return _window.mode == Window.MODE_FULLSCREEN \
		or _window.mode == Window.MODE_EXCLUSIVE_FULLSCREEN


func _emit_changed() -> void:
	changed.emit(pending_settings())


func _to_size(value: Variant) -> Vector2i:
	if value is Vector2i:
		return value
	if value is Vector2:
		return Vector2i(roundi(value.x), roundi(value.y))
	if value is Array and value.size() >= 2:
		return Vector2i(int(value[0]), int(value[1]))
	return Vector2i.ZERO
