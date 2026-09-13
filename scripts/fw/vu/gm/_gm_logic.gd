class_name FGMLogic
extends "../ui/form/_form_logic.gd"

const FGMFormScript = preload("_gm_form.gd")
var _gm: Variant = null
var _service_binding: Variant = null
var _scene: PackedScene = null
var _query: String = ""
var _section: String = "__recent"
var _category: String = ""
var _selected: String = ""
var _values: Dictionary = {}
var _drafts: Dictionary = {}
var _pending: Dictionary = {}
var _result: Dictionary = {}
var _history_sequence: int = -1
var _refresh_elapsed: float = 0.0
var _blocked_through_frame: int = -1
var _refreshing: bool = false
var _minimized: bool = false
var _panel_position: Vector2 = Vector2.ZERO
var _has_panel_position: bool = false
var _open_key: Key = KEY_F1


func setup(ui: Variant, gm: Variant) -> void:
	clear()
	attach_ui(ui)
	_gm = gm
	if _gm != null:
		_service_binding = FBindingScript.new()
		_service_binding.bind_signal(_gm, &"changed", _on_changed)
		_service_binding.bind_signal(_gm, &"registry_changed", _on_registry_changed)
		_service_binding.bind_signal(_gm, &"executed", _on_executed)


func open_panel() -> Variant:
	if _gm == null or not _gm.is_enabled():
		return null
	if is_open():
		return form()
	if _scene == null:
		var root: Control = FGMFormScript.new()
		_scene = PackedScene.new()
		var error: Error = _scene.pack(root)
		root.free()
		if error != OK:
			_scene = null
			push_error("FGMLogic 无法创建 GM 面板场景。")
			return null
	var opened: Variant = open(FUIScript.LAYER_MODAL, &"fw_gm", _scene)
	if opened != null:
		bind_signal(opened, &"action", _on_action)
		_refresh_elapsed = 0.0
		refresh()
		opened.focus_search.call_deferred()
	return opened


func close() -> void:
	if is_open():
		_blocked_through_frame = Engine.get_process_frames() + 1
	super.close()
	# Persistent favorites/history belong to the service. Drafts last only for
	# the current open window, matching the reference GM session.
	_query = ""
	_section = "__recent"
	_category = ""
	_selected = ""
	_values.clear()
	_drafts.clear()
	_pending.clear()
	_history_sequence = -1
	_minimized = false
	_panel_position = Vector2.ZERO
	_has_panel_position = false


func toggle() -> void:
	if is_open():
		close()
	else:
		open_panel()


func is_open() -> bool:
	return form() != null


func set_open_key(key: Key) -> void:
	if key != KEY_NONE and key != KEY_ESCAPE:
		_open_key = key
		refresh()


func is_open_shortcut(event: InputEvent) -> bool:
	return event is InputEventKey and event.pressed and not event.echo and event.keycode == _open_key


func set_minimized(value: bool) -> void:
	if not is_open() or _minimized == value:
		return
	_minimized = value
	refresh()
	if is_open():
		form().focus_search.call_deferred()


func is_minimized() -> bool:
	return is_open() and _minimized


func blocks_input() -> bool:
	return is_open() or Engine.get_process_frames() <= _blocked_through_frame


func handle_input(event: InputEvent) -> bool:
	if not is_open():
		return false
	if event is InputEventKey and event.pressed and not event.echo and event.keycode == KEY_ESCAPE:
		if not form().dismiss_popup():
			close()
		return true
	if is_open_shortcut(event):
		close()
		return true
	return false


func tick(dt: float) -> void:
	if not is_open():
		return
	if _gm == null or not _gm.is_enabled():
		close()
		return
	_refresh_elapsed += dt
	if _refresh_elapsed >= 0.4:
		_refresh_elapsed = 0.0
		refresh()


func refresh() -> void:
	if _refreshing or not is_open():
		return
	if _gm == null or not _gm.is_enabled():
		close()
		return
	_refreshing = true
	var all_commands: Array = _gm.commands()
	all_commands.sort_custom(_command_before)
	var favorites: Dictionary = {}
	for entry: Dictionary in _gm.favorites():
		favorites[String(entry.get("id", ""))] = entry
	var sections: Array[String] = []
	var categories: Array[String] = []
	var visible_commands: Array = []
	for command: Dictionary in all_commands:
		var section: String = String(command.get("section", "通用"))
		if not sections.has(section):
			sections.append(section)
		if _section == section:
			var category: String = String(command.get("category", "通用"))
			if not categories.has(category):
				categories.append(category)
	if not _section.begins_with("__") and not sections.has(_section):
		_section = sections[0] if not sections.is_empty() else "__recent"
		_category = ""
	if not _category.is_empty() and not categories.has(_category):
		_category = ""
	var searching: bool = not _query.strip_edges().is_empty()
	for command: Dictionary in all_commands:
		var id: String = String(command.get("id", ""))
		if searching:
			if _matches_search(command):
				visible_commands.append(command)
		elif _section == "__favorites":
			if favorites.has(id):
				visible_commands.append(command)
		elif String(command.get("section", "通用")) == _section:
			if _category.is_empty() or String(command.get("category", "通用")) == _category:
				visible_commands.append(command)
	if not searching and _section == "__favorites":
		for id: String in favorites:
			if _find_command(all_commands, id).is_empty():
				var unavailable: Dictionary = favorites[id].duplicate(true)
				unavailable["args"] = []
				unavailable["unavailable"] = true
				unavailable["unavailable_reason"] = "此命令在当前环境中暂不可用；可取消收藏或切换环境。"
				unavailable["description"] = unavailable.unavailable_reason
				visible_commands.append(unavailable)
	var command: Dictionary = _find_command(visible_commands, _selected)
	if command.is_empty():
		command = {} if visible_commands.is_empty() else visible_commands[0]
		_select_command(command)
	var inspection: Dictionary = {}
	if not command.is_empty():
		if bool(command.get("unavailable", false)):
			inspection = {"ok": false, "message": command.unavailable_reason, "errors": {}}
		else:
			inspection = _gm.inspect_command(_selected, _values)
	if not is_open() or _gm == null or not _gm.is_enabled():
		_refreshing = false
		close()
		return
	var entries: Array = _gm.history()
	var detail: Dictionary = _history_detail(entries, all_commands)
	if not is_open() or _gm == null or not _gm.is_enabled():
		_refreshing = false
		close()
		return
	var recent: bool = _section == "__recent" and not searching
	var compact_command: Dictionary = command
	var compact_valid: bool = bool(inspection.get("ok", false))
	if recent:
		compact_command = _find_command(all_commands, String(detail.get("id", "")))
		if compact_command.is_empty() and not detail.is_empty():
			compact_command = {"id": detail.get("id", ""), "title": detail.get("title", ""), "unavailable": true}
		compact_valid = bool(detail.get("replay_available", false))
	var environment: String = "全局"
	for current: Dictionary in all_commands:
		var name: String = String(current.get("target_name", ""))
		if not name.is_empty():
			environment = name
			break
	form().apply({
		"commands": visible_commands, "sections": sections, "section": "__search" if searching else _section,
		"categories": categories, "query": _query, "category": _category,
		"favorites_only": _section == "__favorites" and not searching, "favorites": favorites,
		"selected": _selected, "command": command, "values": _values,
		"inspection": inspection, "result": _result, "pending": _pending,
		"history": entries, "history_selected": _history_sequence, "history_detail": detail,
		"diagnostics": _gm.diagnostics(), "environment": environment, "total": all_commands.size(),
		"compact_command": compact_command, "compact_valid": compact_valid,
		"compact_reason": String(detail.get("unavailable_reason", "")) if recent else String(inspection.get("message", "")),
		"minimized": _minimized, "panel_position": _panel_position, "has_panel_position": _has_panel_position,
		"open_key_text": OS.get_keycode_string(_open_key)
	})
	_refreshing = false


func clear() -> void:
	close()
	if _service_binding != null:
		_service_binding.unbind()
	_service_binding = null
	_gm = null
	detach_ui()
	_scene = null
	_result.clear()
	_open_key = KEY_F1


func _find_command(commands: Array, id: String) -> Dictionary:
	for command: Dictionary in commands:
		if String(command.get("id", "")) == id:
			return command
	return {}


func _command_before(a: Dictionary, b: Dictionary) -> bool:
	var left: String = String(a.get("section", "通用")) + "\n" + String(a.get("category", "通用"))
	var right: String = String(b.get("section", "通用")) + "\n" + String(b.get("category", "通用"))
	if left != right:
		return left.naturalnocasecmp_to(right) < 0
	var left_order: int = int(a.get("order", 0))
	var right_order: int = int(b.get("order", 0))
	return left_order < right_order if left_order != right_order else String(a.get("title", "")).naturalnocasecmp_to(String(b.get("title", ""))) < 0


func _matches_search(command: Dictionary) -> bool:
	var haystack: String = "%s %s %s %s %s" % [command.get("id", ""), command.get("title", ""), command.get("section", ""), command.get("category", ""), command.get("description", "")]
	for term: String in _query.replace("\t", " ").replace("\n", " ").replace("\r", " ").to_lower().split(" ", false):
		if not haystack.to_lower().contains(term):
			return false
	return true


func _select_command(command: Dictionary) -> void:
	var id: String = String(command.get("id", ""))
	if id == _selected:
		return
	if not _selected.is_empty():
		_drafts[_selected] = _values.duplicate(true)
	_selected = id
	_values = {}
	_pending.clear()
	var draft: Dictionary = _drafts.get(id, {})
	for arg: Dictionary in command.get("args", []):
		var arg_id: String = String(arg.get("id", ""))
		if draft.has(arg_id):
			_values[arg_id] = draft[arg_id]
		elif arg.has("default"):
			_values[arg_id] = arg.default
		elif String(arg.get("kind", "text")) == "bool":
			_values[arg_id] = false


func _history_detail(entries: Array, commands: Array) -> Dictionary:
	var detail: Dictionary = {}
	for entry: Dictionary in entries:
		if int(entry.get("sequence", -1)) == _history_sequence:
			detail = entry.duplicate(true)
			break
	if detail.is_empty() and not entries.is_empty():
		detail = entries.back().duplicate(true)
		_history_sequence = int(detail.get("sequence", -1))
	if detail.is_empty():
		_history_sequence = -1
		return {}
	var command: Dictionary = _find_command(commands, String(detail.get("id", "")))
	detail["available"] = not command.is_empty()
	detail["replay_available"] = false
	detail["unavailable_reason"] = "此历史命令在当前环境中暂不可用。"
	if not command.is_empty():
		detail["risk"] = command.get("risk", "normal")
		var checked: Dictionary = _gm.inspect_command(String(detail.id), detail.get("values", {}))
		detail.replay_available = bool(checked.get("ok", false))
		detail.unavailable_reason = String(checked.get("message", ""))
	return detail


func _on_action(name: StringName, payload: Dictionary) -> void:
	if _gm == null:
		close()
		return
	match name:
		&"position":
			var position: Variant = payload.get("position", null)
			if position is Vector2:
				_panel_position = position
				_has_panel_position = true
			return
		&"minimize":
			set_minimized(not _minimized)
			return
		&"close":
			close()
			return
		&"search":
			_query = String(payload.get("value", ""))
		&"section":
			var section: String = String(payload.get("value", "__recent"))
			if section != "__search":
				_section = section
				_category = ""
				_query = ""
		&"category":
			_category = String(payload.get("value", ""))
		&"favorites_only":
			_section = "__favorites" if bool(payload.get("value", false)) else "__recent"
			_query = ""
			_category = ""
		&"favorite":
			if not _selected.is_empty():
				_result = _gm.toggle_favorite(_selected)
		&"select":
			var id: String = String(payload.get("id", ""))
			var command: Dictionary = _find_command(_gm.commands(), id)
			if not command.is_empty():
				if _section in ["__recent", "__diagnostics"]:
					_section = String(command.get("section", "通用"))
					_category = ""
				_select_command(command)
			elif _section == "__favorites":
				for favorite: Dictionary in _gm.favorites():
					if String(favorite.id) == id:
						_select_command(favorite)
		&"value":
			_set_value(String(payload.get("id", "")), payload.get("value", null))
		&"run", &"run_close":
			if _section == "__recent" and _query.strip_edges().is_empty():
				_replay_history()
			else:
				_request_execute(name == &"run_close")
		&"confirm":
			if not _pending.is_empty():
				var pending: Dictionary = _pending.duplicate(true)
				_pending.clear()
				_execute(String(pending.id), pending.values, true, bool(pending.get("close_after", false)))
		&"cancel_confirm":
			_pending.clear()
		&"refresh":
			_pending.clear()
		&"history_select":
			_history_sequence = int(payload.get("sequence", -1))
			_pending.clear()
		&"history_load":
			_load_history()
		&"history_replay":
			_replay_history()
	refresh()


func _set_value(id: String, value: Variant) -> void:
	_values[id] = value
	_pending.clear()
	_result.clear()
	var invalidated: Array[String] = [id]
	var command: Dictionary = _find_command(_gm.commands(), _selected)
	for arg: Dictionary in command.get("args", []):
		var arg_id: String = String(arg.get("id", ""))
		for dependency: Variant in arg.get("depends_on", []):
			if invalidated.has(String(dependency)):
				_values[arg_id] = ""
				invalidated.append(arg_id)
				break
	_drafts[_selected] = _values.duplicate(true)


func _load_history() -> bool:
	var detail: Dictionary = _history_detail(_gm.history(), _gm.commands())
	if detail.is_empty() or not bool(detail.get("available", false)):
		return false
	var command: Dictionary = _find_command(_gm.commands(), String(detail.id))
	_query = ""
	_section = String(command.get("section", "通用"))
	_category = ""
	_select_command(command)
	_values = _current_arguments(command, detail.get("values", {}))
	_drafts[_selected] = _values.duplicate(true)
	_pending.clear()
	_minimized = false
	return true


func _replay_history() -> void:
	var detail: Dictionary = _history_detail(_gm.history(), _gm.commands())
	if detail.is_empty() or not bool(detail.get("replay_available", false)):
		_result = {"ok": false, "message": detail.get("unavailable_reason", "尚未选择可执行的历史记录。")}
		return
	var command: Dictionary = _find_command(_gm.commands(), String(detail.id))
	if String(command.get("risk", "normal")) != "normal":
		if _load_history():
			_request_execute()
	else:
		_execute(String(detail.id), detail.get("values", {}), false, false)


func _request_execute(close_after: bool = false) -> void:
	if _selected.is_empty():
		return
	var inspection: Dictionary = _gm.inspect_command(_selected, _values)
	if _gm == null or not is_open():
		return
	if not bool(inspection.get("ok", false)):
		_result = {"ok": false, "message": inspection.get("message", "请检查命令参数。")}
		return
	var command: Dictionary = inspection.get("command", {})
	if String(command.get("risk", "normal")) != "normal":
		_minimized = false
		if _pending.get("id", "") == _selected and _pending.get("values", {}) == _values:
			_pending.clear()
			_execute(_selected, _values, true, close_after)
		else:
			_pending = {"id": _selected, "title": command.get("title", _selected), "values": _values.duplicate(true), "risk": command.get("risk", "caution"), "confirmation": command.get("confirmation", ""), "close_after": close_after}
			_result.clear()
		return
	_execute(_selected, _values, false, close_after)


func _execute(id: String, values: Dictionary, confirmed: bool, close_after: bool) -> void:
	_result = _gm.execute(id, values.duplicate(true), confirmed)
	if close_after:
		close()


func _on_executed(entry: Dictionary) -> void:
	_result = entry.get("result", {})
	refresh()


func _on_registry_changed() -> void:
	_pending.clear()
	if _gm != null and not _selected.is_empty():
		var current: Dictionary = _find_command(_gm.commands(), _selected)
		if not current.is_empty():
			_values = _current_arguments(current, _values)
			_drafts[_selected] = _values.duplicate(true)
	refresh()


func _current_arguments(command: Dictionary, supplied: Dictionary) -> Dictionary:
	var values: Dictionary = {}
	for arg: Dictionary in command.get("args", []):
		var id: String = String(arg.get("id", ""))
		if supplied.has(id):
			values[id] = supplied[id]
		elif arg.has("default"):
			values[id] = arg.default
		elif String(arg.get("kind", "text")) == "bool":
			values[id] = false
	return values


func _on_changed() -> void:
	refresh()
