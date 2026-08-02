class_name FLog
extends RefCounted

enum Level {
	DEBUG,
	INFO,
	WARNING,
	ERROR,
}

signal written(entry: Dictionary)

var minimum_level: Level = Level.INFO
var history_limit := 200
var output_enabled := true

var _category_levels: Dictionary = {}
var _entries: Array[Dictionary] = []
var _sequence := 0


func set_category_level(category: StringName, level: Level) -> void:
	_category_levels[category] = level


func clear_category_level(category: StringName) -> void:
	_category_levels.erase(category)


func is_enabled(level: Level, category: StringName = &"") -> bool:
	var threshold: int = int(_category_levels.get(category, minimum_level))
	return int(level) >= threshold


func debug(category: StringName, message: String, data: Dictionary = {}) -> Dictionary:
	return write(Level.DEBUG, category, message, data)


func info(category: StringName, message: String, data: Dictionary = {}) -> Dictionary:
	return write(Level.INFO, category, message, data)


func warning(category: StringName, message: String, data: Dictionary = {}) -> Dictionary:
	return write(Level.WARNING, category, message, data)


func error(category: StringName, message: String, data: Dictionary = {}) -> Dictionary:
	return write(Level.ERROR, category, message, data)


func write(
	level: Level,
	category: StringName,
	message: String,
	data: Dictionary = {}
) -> Dictionary:
	if not is_enabled(level, category):
		return {}
	_sequence += 1
	var entry := {
		"sequence": _sequence,
		"time_msec": Time.get_ticks_msec(),
		"level": level,
		"level_name": level_name(level),
		"category": category,
		"message": message,
		"data": data.duplicate(true),
	}
	_entries.append(entry)
	var limit := maxi(0, history_limit)
	while _entries.size() > limit:
		_entries.pop_front()
	written.emit(entry)
	if output_enabled:
		_write_to_engine(level, category, message)
	return entry


func recent_entries(limit: int = -1) -> Array[Dictionary]:
	if limit < 0 or limit >= _entries.size():
		return _entries.duplicate(true)
	return _entries.slice(_entries.size() - limit).duplicate(true)


func clear() -> void:
	_entries.clear()


func level_name(level: Level) -> String:
	match level:
		Level.DEBUG:
			return "debug"
		Level.INFO:
			return "info"
		Level.WARNING:
			return "warning"
		Level.ERROR:
			return "error"
	return "unknown"


func _write_to_engine(level: Level, category: StringName, message: String) -> void:
	var prefix := "[%s] " % String(category) if category != &"" else ""
	match level:
		Level.DEBUG:
			print_verbose(prefix + message)
		Level.INFO:
			print(prefix + message)
		Level.WARNING:
			push_warning(prefix + message)
		Level.ERROR:
			push_error(prefix + message)
