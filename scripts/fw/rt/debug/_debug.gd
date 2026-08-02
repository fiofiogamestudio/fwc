class_name FDebug
extends RefCounted

signal changed(enabled: bool)

var _enabled := false
var _allow_in_release := false


func setup(initial_enabled: bool = false, allow_in_release: bool = false) -> void:
	_allow_in_release = allow_in_release
	_enabled = initial_enabled and can_enable()


func can_enable() -> bool:
	return _allow_in_release or OS.is_debug_build() or Engine.is_editor_hint()


func is_enabled() -> bool:
	return _enabled


func set_enabled(value: bool) -> bool:
	var resolved := value and can_enable()
	if _enabled == resolved:
		return false
	_enabled = resolved
	changed.emit(_enabled)
	return true


func toggle() -> bool:
	return set_enabled(not _enabled)


func clear() -> void:
	set_enabled(false)
