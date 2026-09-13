class_name FGM
extends RefCounted

## Generic command registry. Host callbacks own all gameplay changes.
## Callbacks are synchronous and return dictionaries with an explicit bool `ok`.
## GDScript errors cannot be caught here; callbacks must report expected failures.

signal changed()
signal registry_changed()
signal executed(entry: Dictionary)

const _KINDS := ["text", "text_area", "int", "float", "bool", "dropdown"]
const _RISKS := ["normal", "caution", "destructive"]
const _Storage = preload("_gm_storage.gd")
const _COMMAND_FIELDS := ["id", "title", "description", "section", "category", "scope", "source", "target_name", "confirmation", "order", "risk", "args"]
const _ARG_FIELDS := ["id", "label", "kind", "required", "default", "min", "max", "options", "depends_on"]
const _DISPLAY_FIELDS := ["id", "title", "section", "category", "source", "scope", "target_name"]
const _MAX_SAVED_ENTRIES := 10000

var _debug: Variant
var _log: Variant
var _commands: Dictionary = {}
var _entries: Array[Dictionary] = []
var _favorites: Array[Dictionary] = []
var _diagnostics: Array[Dictionary] = []
var _storage_path := ""
var _historyLimit := 100
var _sequence := 0
var _revision := 0
var _epoch := 0
var _executing := false


func setup(debug: Variant, log: Variant = null, history_limit: int = 100) -> void:
	clear()
	_debug = debug
	_log = log
	_historyLimit = maxi(0, history_limit)
	if _valid_object(_debug) and _debug.has_signal("changed"):
		_debug.connect("changed", _on_debug_changed)
	changed.emit()


func is_enabled() -> bool:
	return _valid_object(_debug) and _debug.has_method("is_enabled") and _debug.is_enabled() == true


func register_command(
	owner: StringName,
	definition: Dictionary,
	handler: Callable,
	options_provider: Callable = Callable(),
	availability: Callable = Callable()
) -> Dictionary:
	if String(owner).strip_edges().is_empty():
		return _registration_failure(owner, definition, _failure("invalid_definition", "指令所属模块不能为空。"))
	var checked := _validate_definition(definition, handler, options_provider, availability)
	if not checked.ok:
		return _registration_failure(owner, definition, checked)
	var description: Dictionary = checked.definition
	var id: String = description.id
	if _commands.has(id):
		return _registration_failure(owner, definition, _failure("duplicate_id", "指令“%s”已注册。" % id))
	_revision += 1
	_commands[id] = {
		"owner": owner,
		"definition": description,
		"handler": handler,
		"provider": options_provider,
		"availability": availability,
		"revision": _revision,
	}
	_erase_diagnostic(String(owner), id)
	registry_changed.emit()
	changed.emit()
	return {"ok": true, "message": ""}


func unregister_owner(owner: StringName) -> void:
	var removed := false
	for id: String in _commands.keys():
		if _commands[id].owner == owner:
			_commands.erase(id)
			removed = true
	var diagnostics_removed := _erase_owner_diagnostics(String(owner))
	if removed:
		registry_changed.emit()
	if removed or diagnostics_removed:
		changed.emit()


func unregister_command(id: StringName) -> void:
	var command_id := String(id)
	if _commands.has(command_id):
		_erase_diagnostic(String(_commands[command_id].owner), command_id)
		_commands.erase(command_id)
		registry_changed.emit()
		changed.emit()


func commands() -> Array:
	var result: Array = []
	for record: Dictionary in _commands.values():
		result.append(record.definition.duplicate(true))
	return result


func inspect_command(id: StringName, values: Dictionary = {}) -> Dictionary:
	return _inspect(String(id), values)


func execute(id: StringName, values: Dictionary = {}, confirmed: bool = false) -> Dictionary:
	if _executing:
		return _failure("busy", "另一条 GM 指令正在执行。")
	_executing = true
	var epoch := _epoch
	var command_id := String(id)
	var inspection := _inspect(command_id, values)
	var result: Dictionary
	if not inspection.ok:
		result = _failure(inspection.code, inspection.message)
	elif inspection.command.risk != "normal" and not confirmed:
		result = _failure("confirmation_required", "请先确认，再执行此指令。")
	elif not is_enabled():
		result = _failure("disabled", "GM 功能未启用。")
	else:
		# _inspect checks registry identity after each external callback. The handler
		# receives its own copy so mutation cannot rewrite validation or history.
		var record: Dictionary = _commands[command_id]
		var handler: Callable = record.handler
		if not _callable_accepts(handler, 1):
			result = _failure("callback_invalid", "指令处理函数已失效。")
		else:
			var callback_result: Variant = handler.call(inspection.values.duplicate(true))
			if not _valid_result(callback_result) or not _snapshot_safe(callback_result):
				result = _failure("callback_invalid", "指令处理函数返回的结果无效。")
			else:
				result = {
					"ok": callback_result.ok,
					"code": "ok" if callback_result.ok else "rejected",
					"message": String(callback_result.get("message", "指令执行完成。" if callback_result.ok else "指令被拒绝。")),
				}
				if callback_result.has("data"):
					result.data = _copy_value(callback_result.data)
	if _epoch == epoch:
		_record(command_id, inspection.command, inspection.values, result)
	_executing = false
	return result.duplicate(true)


func history() -> Array:
	return _entries.duplicate(true)


func set_storage_path(path: String) -> Dictionary:
	if path.is_empty():
		_storage_path = ""
		_erase_owner_diagnostics("@storage")
		changed.emit()
		return {"ok": true, "message": ""}
	var loaded: Dictionary = _Storage.read(path)
	if not loaded.ok:
		_set_diagnostic("@storage", path, loaded.code, loaded.message)
		changed.emit()
		return _failure(loaded.code, loaded.message)
	if loaded.has("data"):
		var checked := _validate_storage(loaded.data)
		if not checked.ok:
			_set_diagnostic("@storage", path, checked.code, checked.message)
			changed.emit()
			return checked
		_favorites.assign(checked.data.favorites)
		_entries.assign(checked.data.history)
		while _entries.size() > _historyLimit:
			_entries.pop_front()
		_sequence = checked.data.sequence
	_storage_path = path
	_erase_owner_diagnostics("@storage")
	var saved := _persist()
	changed.emit()
	return saved


func favorites() -> Array:
	return _favorites.duplicate(true)


func is_favorite(id: StringName) -> bool:
	for entry: Dictionary in _favorites:
		if entry.id == String(id):
			return true
	return false


func toggle_favorite(id: StringName) -> Dictionary:
	if is_favorite(id):
		remove_favorite(id)
		return {"ok": true, "message": "已取消收藏。"}
	if not _commands.has(String(id)):
		return _failure("not_found", "指令“%s”尚未注册。" % String(id))
	if _favorites.size() >= _MAX_SAVED_ENTRIES:
		return _failure("favorite_limit", "收藏数量已达到上限。")
	_favorites.append(_display_metadata(_commands[String(id)].definition, String(id)))
	_persist()
	changed.emit()
	return {"ok": true, "message": "已收藏指令。"}


func remove_favorite(id: StringName) -> void:
	for index: int in _favorites.size():
		if _favorites[index].id == String(id):
			_favorites.remove_at(index)
			_persist()
			changed.emit()
			return


func diagnostics() -> Array:
	return _diagnostics.duplicate(true)


func clear_diagnostics(owner: StringName = &"") -> void:
	if String(owner).is_empty():
		if _diagnostics.is_empty():
			return
		_diagnostics.clear()
	elif not _erase_owner_diagnostics(String(owner)):
		return
	changed.emit()


func clear() -> void:
	_epoch += 1
	if _valid_object(_debug) and _debug.has_signal("changed") and _debug.is_connected("changed", _on_debug_changed):
		_debug.disconnect("changed", _on_debug_changed)
	_debug = null
	_log = null
	_commands.clear()
	_entries.clear()
	_favorites.clear()
	_diagnostics.clear()
	_storage_path = ""
	_sequence = 0
	# Do not release an active execution guard if a callback tears the service down.
	registry_changed.emit()
	changed.emit()


func _inspect(id: String, raw_values: Dictionary) -> Dictionary:
	var snapshot := {
		"ok": false, "code": "not_found", "message": "指令“%s”尚未注册。" % id,
		"command": {}, "values": {}, "options": {}, "errors": {}, "available": false,
	}
	if not _commands.has(id):
		return snapshot
	var record: Dictionary = _commands[id]
	var description: Dictionary = record.definition
	snapshot.command = description.duplicate(true)
	if not is_enabled():
		return _inspection_failure(snapshot, "disabled", "GM 功能未启用。")
	if not _callable_accepts(record.handler, 1):
		return _inspection_failure(snapshot, "callback_invalid", "指令处理函数已失效。")
	var inputs: Dictionary = {}
	var known: Dictionary = {}
	for arg: Dictionary in description.args:
		known[arg.id] = true
	for raw_key: Variant in raw_values:
		if not _is_text(raw_key) or not known.has(String(raw_key)):
			snapshot.errors[str(raw_key)] = "未知参数。"
		else:
			inputs[String(raw_key)] = raw_values[raw_key]
	var callback_failed := false
	for arg: Dictionary in description.args:
		var arg_id: String = arg.id
		var has_value := inputs.has(arg_id) or arg.has("default")
		var raw: Variant = inputs.get(arg_id, arg.get("default"))
		var normalized := _normalize_value(arg, raw, has_value)
		if not normalized.ok:
			snapshot.errors[arg_id] = normalized.message
		if arg.kind == "dropdown":
			var option_result := _resolve_options(record, arg, snapshot.values, snapshot.errors)
			if not _is_current(id, record):
				return _inspection_failure(snapshot, "command_changed", "校验期间，指令已被移除或替换。")
			if not option_result.ok:
				snapshot.options[arg_id] = []
				snapshot.errors[arg_id] = option_result.message
				callback_failed = callback_failed or option_result.get("callback_failed", false)
			else:
				snapshot.options[arg_id] = option_result.options
				if normalized.ok and normalized.has("value") and normalized.value != "" and not _option_exists(option_result.options, normalized.value):
					snapshot.errors[arg_id] = "所选选项已失效，请重新选择。"
		if normalized.ok and not snapshot.errors.has(arg_id) and normalized.has("value"):
			snapshot.values[arg_id] = normalized.value
	if not is_enabled():
		return _inspection_failure(snapshot, "disabled", "GM 功能未启用。")
	snapshot.available = true
	if not snapshot.errors.is_empty():
		return _inspection_failure(snapshot, "callback_invalid" if callback_failed else "invalid_arguments", String(snapshot.errors.values()[0]), false)
	var availability: Callable = record.availability
	if not availability.is_null():
		if not _callable_accepts(availability, 1):
			return _inspection_failure(snapshot, "callback_invalid", "可用性检查函数已失效。")
		var available_result: Variant = availability.call(snapshot.values.duplicate(true))
		if not _is_current(id, record):
			return _inspection_failure(snapshot, "command_changed", "校验期间，指令已被移除或替换。")
		if not _valid_result(available_result):
			return _inspection_failure(snapshot, "callback_invalid", "可用性检查函数返回的结果无效。")
		if not available_result.ok:
			return _inspection_failure(snapshot, "unavailable", String(available_result.get("message", "当前状态下无法执行此指令。")))
	if not is_enabled():
		return _inspection_failure(snapshot, "disabled", "GM 功能未启用。")
	snapshot.ok = true
	snapshot.code = "ok"
	snapshot.message = ""
	return snapshot


func _resolve_options(record: Dictionary, arg: Dictionary, values: Dictionary, errors: Dictionary) -> Dictionary:
	for dependency: String in arg.get("depends_on", []):
		if errors.has(dependency) or not values.has(dependency) or (_is_text(values[dependency]) and String(values[dependency]).is_empty()):
			return {"ok": false, "message": "请先为“%s”填写或选择有效值。" % dependency}
	if arg.has("options"):
		return {"ok": true, "options": arg.options.duplicate(true)}
	var provider: Callable = record.provider
	if not _callable_accepts(provider, 2):
		return {"ok": false, "message": "选项提供函数已失效。", "callback_failed": true}
	var provided: Variant = provider.call(StringName(arg.id), values.duplicate(true))
	if not _valid_result(provided):
		return {"ok": false, "message": "选项提供函数返回的结果无效。", "callback_failed": true}
	if not provided.ok:
		return {"ok": false, "message": String(provided.get("message", "当前没有可用选项。")), "callback_failed": true}
	var checked := _validate_options(provided.get("options"))
	if not checked.ok:
		return {"ok": false, "message": checked.message, "callback_failed": true}
	return checked


func _validate_definition(definition: Dictionary, handler: Callable, provider: Callable, availability: Callable) -> Dictionary:
	if not _callable_accepts(handler, 1):
		return _failure("invalid_definition", "指令处理函数必须有效，且接收一个参数值字典。")
	if not provider.is_null() and not _callable_accepts(provider, 2):
		return _failure("invalid_definition", "选项提供函数必须接收参数 ID 和前序参数值。")
	if not availability.is_null() and not _callable_accepts(availability, 1):
		return _failure("invalid_definition", "可用性检查函数必须接收一个参数值字典。")
	for key: Variant in definition:
		if not _is_text(key) or String(key) not in _COMMAND_FIELDS:
			return _failure("invalid_definition", "未知指令字段“%s”。" % str(key))
	var description: Dictionary = {}
	for key: String in ["id", "title", "description", "section", "category", "scope", "source", "target_name", "confirmation", "risk"]:
		var value: Variant = definition.get(key, {"description": "", "section": "通用", "category": "通用", "scope": "global", "source": "", "target_name": "", "confirmation": "", "risk": "normal"}.get(key))
		if not _is_text(value) or (key not in ["description", "source", "target_name", "confirmation"] and String(value).strip_edges().is_empty()):
			return _failure("invalid_definition", "指令字段“%s”必须为非空字符串。" % key)
		description[key] = String(value)
	if description.scope not in ["global", "target"]:
		return _failure("invalid_definition", "指令的 scope 只支持 global 或 target。")
	if typeof(definition.get("order", 0)) != TYPE_INT:
		return _failure("invalid_definition", "指令的 order 必须为整数。")
	description.order = definition.get("order", 0)
	if description.risk not in _RISKS:
		return _failure("invalid_definition", "未知指令风险级别“%s”。" % description.risk)
	var args: Variant = definition.get("args", [])
	if not args is Array:
		return _failure("invalid_definition", "指令的 args 必须为数组。")
	description.args = []
	var previous: Dictionary = {}
	for raw_arg: Variant in args:
		if not raw_arg is Dictionary:
			return _failure("invalid_definition", "每个参数定义必须为字典。")
		for key: Variant in raw_arg:
			if not _is_text(key) or String(key) not in _ARG_FIELDS:
				return _failure("invalid_definition", "未知参数字段“%s”。" % str(key))
		if not _is_text(raw_arg.get("id")) or String(raw_arg.id).strip_edges().is_empty():
			return _failure("invalid_definition", "参数 ID 必须为非空字符串。")
		var arg_id := String(raw_arg.id)
		if previous.has(arg_id):
			return _failure("invalid_definition", "参数“%s”重复。" % arg_id)
		if not _is_text(raw_arg.get("kind")) or String(raw_arg.kind) not in _KINDS:
			return _failure("invalid_definition", "参数“%s”的类型未知。" % arg_id)
		if not _is_text(raw_arg.get("label", arg_id)) or String(raw_arg.get("label", arg_id)).strip_edges().is_empty():
			return _failure("invalid_definition", "参数“%s”的名称不能为空。" % arg_id)
		if typeof(raw_arg.get("required", false)) != TYPE_BOOL:
			return _failure("invalid_definition", "参数“%s”的 required 必须为布尔值。" % arg_id)
		var arg := {"id": arg_id, "label": String(raw_arg.get("label", arg_id)), "kind": String(raw_arg.kind), "required": raw_arg.get("required", false)}
		for bound: String in ["min", "max"]:
			if not raw_arg.has(bound):
				continue
			var limit: Variant = raw_arg[bound]
			if arg.kind not in ["int", "float"] or not _is_number(limit) or not is_finite(float(limit)) or (arg.kind == "int" and typeof(limit) != TYPE_INT):
				return _failure("invalid_definition", "参数“%s”的 %s 边界无效。" % [arg_id, bound])
			arg[bound] = limit
		if arg.has("min") and arg.has("max") and arg.min > arg.max:
			return _failure("invalid_definition", "参数“%s”的最小值大于最大值。" % arg_id)
		if raw_arg.has("options"):
			if arg.kind != "dropdown":
				return _failure("invalid_definition", "只有下拉参数可以定义选项。")
			var option_result := _validate_options(raw_arg.options)
			if not option_result.ok:
				return _failure("invalid_definition", "参数“%s”：%s" % [arg_id, option_result.message])
			arg.options = option_result.options
		elif arg.kind == "dropdown" and not _callable_accepts(provider, 2):
			return _failure("invalid_definition", "下拉参数“%s”必须提供选项或选项提供函数。" % arg_id)
		if raw_arg.has("depends_on"):
			if arg.kind != "dropdown" or not raw_arg.depends_on is Array:
				return _failure("invalid_definition", "只有下拉参数可以定义依赖数组。")
			arg.depends_on = []
			for dependency: Variant in raw_arg.depends_on:
				if not _is_text(dependency) or not previous.has(String(dependency)) or String(dependency) in arg.depends_on:
					return _failure("invalid_definition", "参数依赖必须指向前序参数，且不能重复。")
				arg.depends_on.append(String(dependency))
		if raw_arg.has("default"):
			var normalized := _normalize_value(arg, raw_arg.default, true)
			if not normalized.ok:
				return _failure("invalid_definition", "参数“%s”的默认值无效：%s" % [arg_id, normalized.message])
			if normalized.has("value"):
				if arg.kind == "dropdown" and arg.has("options") and normalized.value != "" and not _option_exists(arg.options, normalized.value):
					return _failure("invalid_definition", "参数“%s”的默认值不在选项中。" % arg_id)
				arg.default = normalized.value
		previous[arg_id] = true
		description.args.append(arg)
	return {"ok": true, "message": "", "definition": description}


func _validate_options(raw: Variant) -> Dictionary:
	if not raw is Array:
		return {"ok": false, "message": "选项必须为数组。"}
	var options: Array = []
	var seen: Dictionary = {}
	for option: Variant in raw:
		if not option is Dictionary or not _is_text(option.get("value")) or not _is_text(option.get("label")):
			return {"ok": false, "message": "选项的 value 和 label 必须为字符串。"}
		var value := String(option.value)
		var label := String(option.label)
		if value.is_empty() or label.strip_edges().is_empty() or seen.has(value):
			return {"ok": false, "message": "选项值不能为空或重复，名称不能为空。"}
		if option.size() != 2:
			return {"ok": false, "message": "选项仅支持 value 和 label 字段。"}
		seen[value] = true
		options.append({"value": value, "label": label})
	return {"ok": true, "options": options}


func _normalize_value(arg: Dictionary, raw: Variant, present: bool) -> Dictionary:
	var empty := not present or raw == null or (_is_text(raw) and String(raw).strip_edges().is_empty())
	if empty:
		if arg.required:
			return {"ok": false, "message": "此参数必填。"}
		match arg.kind:
			"text", "text_area", "dropdown":
				return {"ok": true, "value": ""}
			"bool":
				return {"ok": true, "value": false}
		return {"ok": true}
	var value: Variant
	match arg.kind:
		"text", "text_area", "dropdown":
			if not _is_text(raw):
				return {"ok": false, "message": "请输入文本。"}
			value = String(raw)
		"bool":
			if typeof(raw) != TYPE_BOOL:
				return {"ok": false, "message": "参数必须为布尔值。"}
			value = raw
		"int":
			var parsed := _parse_integer(raw)
			if not parsed.ok:
				return parsed
			value = parsed.value
		"float":
			if not _is_number(raw) and not (_is_text(raw) and String(raw).strip_edges().is_valid_float()):
				return {"ok": false, "message": "请输入有限数值。"}
			value = String(raw).strip_edges().to_float() if _is_text(raw) else float(raw)
			if not is_finite(value):
				return {"ok": false, "message": "请输入有限数值。"}
	if arg.has("min") and value < arg.min:
		return {"ok": false, "message": "数值不能小于 %s。" % str(arg.min)}
	if arg.has("max") and value > arg.max:
		return {"ok": false, "message": "数值不能大于 %s。" % str(arg.max)}
	return {"ok": true, "value": value}


func _parse_integer(raw: Variant) -> Dictionary:
	if typeof(raw) == TYPE_INT:
		return {"ok": true, "value": raw}
	if not _is_text(raw):
		return {"ok": false, "message": "请输入整数。"}
	var source := String(raw).strip_edges()
	var negative := source.begins_with("-")
	var digits := source.substr(1) if negative or source.begins_with("+") else source
	if digits.is_empty():
		return {"ok": false, "message": "请输入整数。"}
	for index: int in digits.length():
		var digit := digits.unicode_at(index)
		if digit < 48 or digit > 57:
			return {"ok": false, "message": "请输入整数。"}
	while digits.length() > 1 and digits.begins_with("0"):
		digits = digits.substr(1)
	var bound := "9223372036854775808" if negative else "9223372036854775807"
	if digits.length() > bound.length() or (digits.length() == bound.length() and digits > bound):
		return {"ok": false, "message": "整数超出 64 位有符号整数范围。"}
	return {"ok": true, "value": ("-" + digits if negative else digits).to_int()}


func _record(id: String, description: Dictionary, values: Dictionary, result: Dictionary) -> void:
	_sequence += 1
	var entry := _display_metadata(description, id)
	entry.merge({"sequence": _sequence, "time_msec": Time.get_ticks_msec(), "time_text": Time.get_datetime_string_from_system(false, true),
		"values": values.duplicate(true), "result": result.duplicate(true)})
	_entries.append(entry)
	while _entries.size() > _historyLimit:
		_entries.pop_front()
	_persist()
	if _valid_object(_log) and _log.has_method("info"):
		_log.info(&"gm", "%s: %s" % [id, result.message], entry.duplicate(true))
	executed.emit(entry.duplicate(true))
	changed.emit()


func _inspection_failure(snapshot: Dictionary, code: String, message: String, unavailable: bool = true) -> Dictionary:
	snapshot.ok = false
	snapshot.code = code
	snapshot.message = message
	if unavailable:
		snapshot.available = false
	return snapshot


func _failure(code: String, message: String) -> Dictionary:
	return {"ok": false, "code": code, "message": message}


func _display_metadata(description: Dictionary, id: String) -> Dictionary:
	var result := {"id": id, "title": id, "section": "通用", "category": "通用", "source": "", "scope": "global", "target_name": ""}
	for key: String in _DISPLAY_FIELDS:
		if description.has(key):
			result[key] = String(description[key])
	return result


func _registration_failure(owner: StringName, definition: Dictionary, failure: Dictionary) -> Dictionary:
	var id := String(definition.id) if _is_text(definition.get("id")) else ""
	_set_diagnostic(String(owner), id, failure.code, failure.message)
	changed.emit()
	return failure


func _set_diagnostic(owner: String, id: String, code: String, message: String) -> void:
	_erase_diagnostic(owner, id)
	_diagnostics.append({"owner": owner, "id": id, "code": code, "message": message})
	while _diagnostics.size() > 256:
		_diagnostics.pop_front()


func _erase_diagnostic(owner: String, id: String) -> void:
	for index: int in range(_diagnostics.size() - 1, -1, -1):
		if _diagnostics[index].owner == owner and _diagnostics[index].id == id:
			_diagnostics.remove_at(index)


func _erase_owner_diagnostics(owner: String) -> bool:
	var removed := false
	for index: int in range(_diagnostics.size() - 1, -1, -1):
		if _diagnostics[index].owner == owner:
			_diagnostics.remove_at(index)
			removed = true
	return removed


func _persist() -> Dictionary:
	if _storage_path.is_empty():
		return {"ok": true, "message": ""}
	var payload := {"version": 1, "favorites": _favorites.duplicate(true), "history": _entries.duplicate(true), "sequence": _sequence}
	var checked := _validate_storage(payload)
	var result: Dictionary = _Storage.write(_storage_path, payload) if checked.ok else checked
	if not result.ok:
		_set_diagnostic("@storage", _storage_path, result.code, result.message)
	else:
		_erase_diagnostic("@storage", _storage_path)
	return result


func _validate_storage(raw: Variant) -> Dictionary:
	var invalid := _failure("storage_invalid", "GM 缓存格式无效，已保留当前收藏和历史。")
	if not raw is Dictionary or raw.size() != 4 or typeof(raw.get("version")) != TYPE_INT or raw.version != 1:
		return invalid
	if not raw.get("favorites") is Array or not raw.get("history") is Array or typeof(raw.get("sequence")) != TYPE_INT:
		return invalid
	if raw.favorites.size() > _MAX_SAVED_ENTRIES or raw.history.size() > _MAX_SAVED_ENTRIES or raw.sequence < 0 or raw.sequence >= 9223372036854775807:
		return invalid
	var seen: Dictionary = {}
	for favorite: Variant in raw.favorites:
		if not _valid_display_metadata(favorite) or favorite.size() != _DISPLAY_FIELDS.size() or seen.has(favorite.id):
			return invalid
		seen[favorite.id] = true
	var previous_sequence := 0
	for entry: Variant in raw.history:
		if not _valid_display_metadata(entry, false) or entry.size() != _DISPLAY_FIELDS.size() + 5:
			return invalid
		if typeof(entry.get("sequence")) != TYPE_INT or entry.sequence <= previous_sequence or entry.sequence > raw.sequence:
			return invalid
		if typeof(entry.get("time_msec")) != TYPE_INT or entry.time_msec < 0 or typeof(entry.get("time_text")) != TYPE_STRING or entry.time_text.length() > 64:
			return invalid
		if not entry.get("values") is Dictionary or entry.values.size() > 256 or not _valid_result(entry.get("result")):
			return invalid
		for key: Variant in entry.values:
			if typeof(key) != TYPE_STRING or key.length() > 1024 or typeof(entry.values[key]) not in [TYPE_NIL, TYPE_BOOL, TYPE_INT, TYPE_FLOAT, TYPE_STRING]:
				return invalid
		if typeof(entry.result.get("code")) != TYPE_STRING or entry.result.code.length() > 1024 or not _storage_value_safe(entry):
			return invalid
		previous_sequence = entry.sequence
	return {"ok": true, "message": "", "data": raw.duplicate(true)}


func _valid_display_metadata(raw: Variant, require_identity: bool = true) -> bool:
	if not raw is Dictionary:
		return false
	for key: String in _DISPLAY_FIELDS:
		if typeof(raw.get(key)) != TYPE_STRING or raw[key].length() > 4096:
			return false
	return (not require_identity or (not raw.id.strip_edges().is_empty() and not raw.title.strip_edges().is_empty())) and not raw.section.strip_edges().is_empty() and not raw.category.strip_edges().is_empty() and raw.scope in ["global", "target"]


func _storage_value_safe(value: Variant, depth: int = 0) -> bool:
	if depth > 32 or typeof(value) in [TYPE_OBJECT, TYPE_CALLABLE, TYPE_SIGNAL, TYPE_RID]:
		return false
	if typeof(value) in [TYPE_STRING, TYPE_STRING_NAME, TYPE_NODE_PATH]:
		return String(value).length() <= 65536
	if typeof(value) == TYPE_FLOAT:
		return is_finite(value)
	if value is Dictionary:
		if value.size() > _MAX_SAVED_ENTRIES:
			return false
		for key: Variant in value:
			if key is Dictionary or key is Array or not _storage_value_safe(key, depth + 1) or not _storage_value_safe(value[key], depth + 1):
				return false
	elif value is Array:
		if value.size() > _MAX_SAVED_ENTRIES:
			return false
		for element: Variant in value:
			if not _storage_value_safe(element, depth + 1):
				return false
	return true


func _is_current(id: String, record: Dictionary) -> bool:
	return _commands.has(id) and _commands[id].revision == record.revision


func _option_exists(options: Array, value: String) -> bool:
	for option: Dictionary in options:
		if option.value == value:
			return true
	return false


func _callable_accepts(callback: Callable, argument_count: int) -> bool:
	return callback.is_valid() and callback.get_argument_count() == argument_count


func _valid_result(value: Variant) -> bool:
	return value is Dictionary and typeof(value.get("ok")) == TYPE_BOOL and (not value.has("message") or _is_text(value.message))


func _valid_object(value: Variant) -> bool:
	return typeof(value) == TYPE_OBJECT and is_instance_valid(value)


func _is_text(value: Variant) -> bool:
	return typeof(value) == TYPE_STRING or typeof(value) == TYPE_STRING_NAME


func _is_number(value: Variant) -> bool:
	return typeof(value) == TYPE_INT or typeof(value) == TYPE_FLOAT


func _snapshot_safe(value: Variant, depth: int = 0) -> bool:
	if depth > 32 or typeof(value) in [TYPE_OBJECT, TYPE_CALLABLE, TYPE_SIGNAL]:
		return false
	if value is Dictionary:
		for key: Variant in value:
			if key is Dictionary or key is Array or not _snapshot_safe(key, depth + 1) or not _snapshot_safe(value[key], depth + 1):
				return false
	elif value is Array:
		for element: Variant in value:
			if not _snapshot_safe(element, depth + 1):
				return false
	return true


func _copy_value(value: Variant) -> Variant:
	return value.duplicate(true) if value is Dictionary or value is Array else value


func _on_debug_changed(_enabled: bool) -> void:
	changed.emit()
