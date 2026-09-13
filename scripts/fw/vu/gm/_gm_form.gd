class_name FGMForm
extends "../ui/form/_form.gd"

const _TEXT: Color = Color("e5e9f0")
const _MUTED: Color = Color("a1adbf")
const _ACCENT: Color = Color("8cc8ff")
const _ERROR: Color = Color("ffadab")
const _SUCCESS: Color = Color("9ddbb5")

var _connections: Array[Dictionary] = []
var _field_connections: Array[Dictionary] = []
var _shell: PanelContainer = null
var _backdrop: ColorRect = null
var _expanded_content: VBoxContainer = null
var _expanded_scroll: ScrollContainer = null
var _header: HBoxContainer = null
var _header_title: Label = null
var _header_hint: Label = null
var _minimize_button: Button = null
var _compact_row: HBoxContainer = null
var _compact_title: Label = null
var _compact_execute: Button = null
var _compact_status: Label = null
var _minimized: bool = false
var _has_position: bool = false
var _panel_center: Vector2 = Vector2.ZERO
var _dragging: bool = false
var _drag_offset: Vector2 = Vector2.ZERO
var _search: LineEdit = null
var _search_value: String = ""
var _section: OptionButton = null
var _category: OptionButton = null
var _favorites_only: CheckButton = null
var _command_list: ItemList = null
var _count: Label = null
var _title: Label = null
var _description: Label = null
var _risk: Label = null
var _favorite: Button = null
var _fields: VBoxContainer = null
var _field_controls: Dictionary = {}
var _field_errors: Dictionary = {}
var _field_schema: Array = []
var _field_command: String = ""
var _field_values: Dictionary = {}
var _validation: Label = null
var _execute: Button = null
var _execute_close: Button = null
var _output: Label = null
var _output_scroll: ScrollContainer = null
var _history: ItemList = null
var _history_label: Label = null
var _command_detail: VBoxContainer = null
var _history_detail: VBoxContainer = null
var _history_detail_text: Label = null
var _history_load: Button = null
var _history_replay: Button = null
var _diagnostics_page: ScrollContainer = null
var _diagnostics_text: Label = null
var _environment: Label = null
var _content: HBoxContainer = null
var _confirmation: VBoxContainer = null
var _confirmation_text: Label = null
var _confirm: Button = null
var _had_pending: bool = false
var _applying: bool = false


func on_setup() -> void:
	# Host screen descendants may use their own Z indices. A GM modal must mask
	# those descendants as well as their parent form, independent of FUI order.
	z_as_relative = false
	z_index = 4096
	set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	mouse_filter = Control.MOUSE_FILTER_STOP
	set_process_unhandled_input(true)
	set_process_input(true)
	theme = _make_theme()
	_build()
	_connect(self, &"resized", _on_viewport_resized)
	_resize_panel()


func on_clear() -> void:
	set_process_unhandled_input(false)
	set_process_input(false)
	_disconnect(_field_connections)
	_disconnect(_connections)
	_field_controls.clear()
	_field_errors.clear()
	_field_schema.clear()
	_field_command = ""
	_field_values.clear()
	_search_value = ""
	for child: Node in get_children():
		remove_child(child)
		child.queue_free()
	_shell = null
	_backdrop = null
	_has_position = false
	_minimized = false
	_dragging = false
	_had_pending = false
	theme = null


func focus_search() -> void:
	if not is_setup():
		return
	if _minimized and is_instance_valid(_minimize_button):
		_minimize_button.grab_focus()
	elif is_instance_valid(_search):
		_search.grab_focus()


func dismiss_popup() -> bool:
	if not is_setup():
		return false
	var dropdowns: Array = [_section, _category, _search]
	dropdowns.append_array(_field_controls.values())
	for control: Variant in dropdowns:
		if not is_instance_valid(control):
			continue
		var popup: PopupMenu = null
		if control is OptionButton:
			popup = control.get_popup()
		elif control is LineEdit or control is TextEdit:
			popup = control.get_menu()
		if is_instance_valid(popup) and popup.visible:
			popup.hide()
			return true
	return false


func apply(vm: Variant, _dt: float = 0.0) -> void:
	if not is_setup() or not vm is Dictionary:
		return
	_applying = true
	var query: String = String(vm.get("query", ""))
	# LineEdit emits text_changed after editing its text. A refresh between
	# those steps still has the previous VM: do not overwrite the fresh edit
	# or reset its caret merely because an unchanged VM was rendered again.
	if _search_value != query:
		_search_value = query
		if _search.text != query:
			_search.text = query
	_favorites_only.set_pressed_no_signal(bool(vm.get("favorites_only", false)))
	_apply_sections(vm)
	_apply_categories(vm)
	_apply_commands(vm)
	var section: String = String(vm.get("section", ""))
	var searching: bool = not String(vm.get("query", "")).strip_edges().is_empty()
	var showing_history: bool = section == "__recent" and not searching
	var showing_diagnostics: bool = section == "__diagnostics" and not searching
	_content.visible = not showing_diagnostics
	_diagnostics_page.visible = showing_diagnostics
	_command_list.visible = not showing_history
	_history.visible = showing_history
	_history_label.visible = showing_history
	_command_detail.visible = not showing_history
	_history_detail.visible = showing_history
	_environment.text = "当前环境：" + String(vm.get("environment", "未指定"))
	_environment.tooltip_text = _environment.text
	var command: Dictionary = vm.get("command", {})
	var selected: String = String(vm.get("selected", ""))
	var has_command: bool = not command.is_empty()
	_title.text = String(command.get("title", "尚未选择命令"))
	_description.text = String(command.get("description", "请先通过 app.gm_service().register_command() 注册宿主命令。"))
	if not has_command and int(vm.get("total", 0)) > 0:
		_description.text = "没有匹配的命令，请调整搜索、分类或收藏筛选。"
	_risk.text = "目录：%s / %s    风险：%s\n范围：%s    来源：%s" % [command.get("section", "通用"), _category_label(String(command.get("category", "General"))), _risk_label(String(command.get("risk", "normal"))), _scope_label(String(command.get("scope", ""))), command.get("source", command.get("owner", "未指定"))] if has_command else ""
	_risk.add_theme_color_override("font_color", _ERROR if String(command.get("risk", "normal")) == "destructive" else _MUTED)
	_favorite.disabled = not has_command
	var favorites: Dictionary = vm.get("favorites", {})
	_favorite.text = "取消收藏" if favorites.has(selected) else "收藏"
	var inspection: Dictionary = vm.get("inspection", {})
	_apply_fields(command, vm.get("values", {}), inspection)
	var valid: bool = has_command and not bool(command.get("unavailable", false)) and bool(inspection.get("ok", false))
	_validation.text = String(inspection.get("message", "")) if not valid else "当前可执行；执行前会再次检查参数与条件。"
	if bool(command.get("unavailable", false)):
		_validation.text = String(command.get("unavailable_reason", "此收藏命令在当前环境未注册，可保留或取消收藏。"))
	_validation.add_theme_color_override("font_color", _MUTED if valid else _ERROR)
	_validation.visible = has_command
	var pending: Dictionary = vm.get("pending", {})
	var confirmation_started: bool = not pending.is_empty() and not _had_pending
	_had_pending = not pending.is_empty()
	_execute.disabled = not valid or not pending.is_empty()
	_execute_close.disabled = _execute.disabled
	_execute.text = "确认后执行" if String(command.get("risk", "normal")) != "normal" else "执行命令"
	_apply_confirmation(pending, valid)
	_apply_result(vm.get("result", {}))
	_apply_history(vm.get("history", []), int(vm.get("history_selected", -1)))
	_apply_history_detail(vm.get("history_detail", {}))
	_apply_diagnostics(vm.get("diagnostics", []))
	if showing_history:
		_count.text = "最近使用  %d 条" % (vm.get("history", []) as Array).size()
	var compact_command: Dictionary = vm.get("compact_command", command)
	var compact_valid: bool = bool(vm.get("compact_valid", valid))
	_apply_compact(compact_command, compact_valid, vm.get("result", {}), String(vm.get("compact_reason", "")))
	_header_hint.text = "拖动标题栏移动 · %s / Esc 关闭" % String(vm.get("open_key_text", "F1"))
	_apply_panel_layout(vm)
	if confirmation_started and not _minimized:
		_ensure_confirmation_visible.call_deferred()
	_applying = false


func _unhandled_input(_event: InputEvent) -> void:
	if is_setup() and is_visible_in_tree():
		get_viewport().set_input_as_handled()


func _input(event: InputEvent) -> void:
	if not _dragging or not is_setup():
		return
	if event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_LEFT and not event.pressed:
		_dragging = false
	elif event is InputEventMouseMotion:
		if (event.button_mask & MOUSE_BUTTON_MASK_LEFT) == 0:
			_dragging = false
		else:
			_set_panel_center(_pointer_position(event.global_position) - _drag_offset)


func _notification(what: int) -> void:
	if what == NOTIFICATION_WM_WINDOW_FOCUS_OUT:
		_dragging = false


func _build() -> void:
	_backdrop = ColorRect.new()
	_backdrop.name = "InputMask"
	_backdrop.color = Color.TRANSPARENT
	_backdrop.set_anchors_and_offsets_preset(Control.PRESET_FULL_RECT)
	_backdrop.mouse_filter = Control.MOUSE_FILTER_STOP
	add_child(_backdrop)
	_shell = PanelContainer.new()
	_shell.name = "Panel"
	_shell.add_theme_stylebox_override("panel", _box(Color("171f2c"), Color("425168"), 12, 18))
	add_child(_shell)
	var body: VBoxContainer = VBoxContainer.new()
	body.add_theme_constant_override("separation", 10)
	_shell.add_child(body)
	_header = HBoxContainer.new()
	_header.name = "HeaderDragHandle"
	_header.mouse_filter = Control.MOUSE_FILTER_STOP
	_header.mouse_default_cursor_shape = Control.CURSOR_MOVE
	_header.add_theme_constant_override("separation", 8)
	body.add_child(_header)
	_connect(_header, &"gui_input", _on_header_input)
	var heading_text: VBoxContainer = VBoxContainer.new()
	heading_text.mouse_filter = Control.MOUSE_FILTER_PASS
	heading_text.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_header.add_child(heading_text)
	_header_title = _label("GM 调试面板", 24)
	heading_text.add_child(_header_title)
	_header_hint = _label("拖动标题栏移动 · F1 / Esc 关闭", 12, _MUTED)
	heading_text.add_child(_header_hint)
	_compact_row = HBoxContainer.new()
	_compact_row.name = "CompactCommand"
	_compact_row.mouse_filter = Control.MOUSE_FILTER_PASS
	_compact_row.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_compact_row.add_theme_constant_override("separation", 8)
	_compact_row.visible = false
	_header.add_child(_compact_row)
	_compact_title = _label("尚未选择命令", 14)
	_compact_title.name = "CompactCommandTitle"
	_compact_title.custom_minimum_size.x = 135
	_compact_title.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_compact_title.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
	_compact_row.add_child(_compact_title)
	_compact_execute = _button("执行", "CompactExecuteButton")
	_compact_execute.custom_minimum_size.x = 76
	_compact_row.add_child(_compact_execute)
	_connect(_compact_execute, &"pressed", func() -> void: emit_action(&"run"))
	_minimize_button = _button("收起", "MinimizeButton")
	_header.add_child(_minimize_button)
	_connect(_minimize_button, &"pressed", func() -> void: emit_action(&"minimize"))
	var close_button: Button = _button("关闭", "CloseButton")
	_header.add_child(close_button)
	_connect(close_button, &"pressed", func() -> void: emit_action(&"close"))
	_compact_status = _label("", 12, _MUTED)
	_compact_status.name = "CompactStatus"
	_compact_status.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_compact_status.max_lines_visible = 2
	_compact_status.visible = false
	body.add_child(_compact_status)
	_expanded_scroll = ScrollContainer.new()
	_expanded_scroll.name = "ExpandedScroll"
	_expanded_scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	_expanded_scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	body.add_child(_expanded_scroll)
	_expanded_content = VBoxContainer.new()
	_expanded_content.name = "ExpandedContent"
	_expanded_content.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_expanded_content.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_expanded_content.add_theme_constant_override("separation", 10)
	_expanded_scroll.add_child(_expanded_content)
	_environment = _label("当前环境：未指定", 12, _MUTED)
	_environment.name = "EnvironmentLabel"
	_environment.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
	_expanded_content.add_child(_environment)
	var filters: HBoxContainer = HBoxContainer.new()
	filters.add_theme_constant_override("separation", 8)
	_expanded_content.add_child(filters)
	_search = LineEdit.new()
	_search.name = "Search"
	_search.placeholder_text = "全局搜索命令，空格分隔多个关键词…"
	_search.clear_button_enabled = true
	_search.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_search.custom_minimum_size = Vector2(120, 38)
	filters.add_child(_search)
	_connect(_search, &"text_changed", func(value: String) -> void: _emit_value(&"search", value))
	var navigation: HBoxContainer = HBoxContainer.new()
	navigation.add_theme_constant_override("separation", 8)
	_expanded_content.add_child(navigation)
	_section = OptionButton.new()
	_section.name = "SectionFilter"
	_section.custom_minimum_size = Vector2(145, 38)
	_section.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_section.fit_to_longest_item = false
	navigation.add_child(_section)
	_connect(_section, &"item_selected", func(index: int) -> void: _emit_value(&"section", _section.get_item_metadata(index)))
	_category = OptionButton.new()
	_category.name = "CategoryFilter"
	_category.custom_minimum_size = Vector2(145, 38)
	_category.fit_to_longest_item = false
	_category.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	navigation.add_child(_category)
	_connect(_category, &"item_selected", _category_selected)
	_favorites_only = CheckButton.new()
	_favorites_only.name = "FavoritesFilter"
	_favorites_only.text = "仅收藏"
	navigation.add_child(_favorites_only)
	_connect(_favorites_only, &"toggled", func(value: bool) -> void: _emit_value(&"favorites_only", value))
	_content = HBoxContainer.new()
	_content.name = "Content"
	_content.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_content.add_theme_constant_override("separation", 16)
	_expanded_content.add_child(_content)
	var left: VBoxContainer = VBoxContainer.new()
	left.custom_minimum_size.x = 210
	left.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	left.size_flags_stretch_ratio = 0.36
	_content.add_child(left)
	_count = _label("命令列表", 12, _MUTED)
	left.add_child(_count)
	_command_list = ItemList.new()
	_command_list.name = "CommandList"
	_command_list.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_command_list.custom_minimum_size.y = 150
	_command_list.allow_reselect = true
	_command_list.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
	_command_list.add_theme_constant_override("v_separation", 8)
	left.add_child(_command_list)
	_connect(_command_list, &"item_selected", _command_selected)
	_history_label = _label("选择记录可查看参数、结果并再次执行。", 11, _MUTED)
	_history_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	left.add_child(_history_label)
	_history = ItemList.new()
	_history.name = "History"
	_history.custom_minimum_size.y = 150
	_history.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_history.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
	_history.allow_reselect = true
	left.add_child(_history)
	_connect(_history, &"item_selected", func(index: int) -> void:
		if not _applying:
			emit_action(&"history_select", {"sequence": _history.get_item_metadata(index)})
	)
	var right: VBoxContainer = VBoxContainer.new()
	right.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	right.size_flags_stretch_ratio = 0.64
	_content.add_child(right)
	_command_detail = VBoxContainer.new()
	_command_detail.name = "CommandDetail"
	_command_detail.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_command_detail.add_theme_constant_override("separation", 8)
	right.add_child(_command_detail)
	var title_row: HBoxContainer = HBoxContainer.new()
	_command_detail.add_child(title_row)
	_title = _label("尚未选择命令", 21)
	_title.name = "CommandTitle"
	_title.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_title.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
	title_row.add_child(_title)
	_favorite = _button("收藏", "FavoriteButton")
	title_row.add_child(_favorite)
	_connect(_favorite, &"pressed", func() -> void: emit_action(&"favorite", {"id": _field_command}))
	_risk = _label("", 12, _MUTED)
	_risk.name = "CommandMetadata"
	_risk.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_command_detail.add_child(_risk)
	var scroll: ScrollContainer = ScrollContainer.new()
	scroll.name = "ParameterScroll"
	scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	scroll.custom_minimum_size.y = 120
	scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_command_detail.add_child(scroll)
	var parameter_body: VBoxContainer = VBoxContainer.new()
	parameter_body.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	parameter_body.add_theme_constant_override("separation", 12)
	scroll.add_child(parameter_body)
	_description = _label("", 14, _MUTED)
	_description.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	parameter_body.add_child(_description)
	_fields = VBoxContainer.new()
	_fields.name = "Parameters"
	_fields.add_theme_constant_override("separation", 12)
	parameter_body.add_child(_fields)
	_validation = _label("", 12, _MUTED)
	_validation.name = "Validation"
	_validation.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_command_detail.add_child(_validation)
	var actions: HBoxContainer = HBoxContainer.new()
	actions.add_theme_constant_override("separation", 8)
	_command_detail.add_child(actions)
	var refresh_button: Button = _button("刷新", "RefreshButton")
	refresh_button.tooltip_text = "刷新命令可用条件与动态选项。"
	actions.add_child(refresh_button)
	_connect(refresh_button, &"pressed", func() -> void: emit_action(&"refresh"))
	_execute = _button("执行命令", "ExecuteButton")
	_execute.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_execute.add_theme_stylebox_override("normal", _box(Color("285789"), Color("578ac0"), 6, 10))
	actions.add_child(_execute)
	_connect(_execute, &"pressed", func() -> void: emit_action(&"run"))
	_execute_close = _button("执行并关闭", "ExecuteCloseButton")
	_command_detail.add_child(_execute_close)
	_connect(_execute_close, &"pressed", func() -> void: emit_action(&"run_close"))
	_history_detail = VBoxContainer.new()
	_history_detail.name = "HistoryDetail"
	_history_detail.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_history_detail.add_theme_constant_override("separation", 10)
	right.add_child(_history_detail)
	_history_detail.add_child(_label("历史详情", 21))
	var history_scroll: ScrollContainer = ScrollContainer.new()
	history_scroll.name = "HistoryDetailScroll"
	history_scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	history_scroll.custom_minimum_size.y = 150
	history_scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	_history_detail.add_child(history_scroll)
	_history_detail_text = _label("选择一条历史记录。", 14)
	_history_detail_text.name = "HistoryDetailText"
	_history_detail_text.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_history_detail_text.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	history_scroll.add_child(_history_detail_text)
	_history_load = _button("载入参数", "LoadHistoryButton")
	_history_detail.add_child(_history_load)
	_connect(_history_load, &"pressed", func() -> void: emit_action(&"history_load"))
	_history_replay = _button("再次执行", "ReplayHistoryButton")
	_history_detail.add_child(_history_replay)
	_connect(_history_replay, &"pressed", func() -> void: emit_action(&"history_replay"))
	_diagnostics_page = ScrollContainer.new()
	_diagnostics_page.name = "Diagnostics"
	_diagnostics_page.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_diagnostics_page.custom_minimum_size.y = 150
	_diagnostics_page.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	_expanded_content.add_child(_diagnostics_page)
	_diagnostics_text = _label("当前没有注册异常。", 14)
	_diagnostics_text.name = "DiagnosticsText"
	_diagnostics_text.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_diagnostics_text.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_diagnostics_page.add_child(_diagnostics_text)
	_confirmation = VBoxContainer.new()
	_confirmation.name = "Confirmation"
	_confirmation.visible = false
	_expanded_content.add_child(_confirmation)
	var confirmation_scroll: ScrollContainer = ScrollContainer.new()
	confirmation_scroll.name = "ConfirmationScroll"
	confirmation_scroll.custom_minimum_size.y = 90
	confirmation_scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	_confirmation.add_child(confirmation_scroll)
	_confirmation_text = _label("", 13, _ERROR)
	_confirmation_text.name = "ConfirmationText"
	_confirmation_text.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_confirmation_text.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	confirmation_scroll.add_child(_confirmation_text)
	var confirmation_actions: HBoxContainer = HBoxContainer.new()
	_confirmation.add_child(confirmation_actions)
	var cancel: Button = _button("取消", "CancelButton")
	confirmation_actions.add_child(cancel)
	_connect(cancel, &"pressed", func() -> void: emit_action(&"cancel_confirm"))
	_confirm = _button("确认执行", "ConfirmButton")
	_confirm.add_theme_stylebox_override("normal", _box(Color("713d40"), Color("b46a6d"), 6, 10))
	confirmation_actions.add_child(_confirm)
	_connect(_confirm, &"pressed", func() -> void: emit_action(&"confirm"))
	_output_scroll = ScrollContainer.new()
	_output_scroll.name = "OutputScroll"
	_output_scroll.custom_minimum_size.y = 76
	_output_scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	_expanded_content.add_child(_output_scroll)
	_output = _label("执行结果将显示在这里。", 13, _MUTED)
	_output.name = "Output"
	_output.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_output.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_output_scroll.add_child(_output)


func _resize_panel() -> void:
	if _shell == null:
		return
	var target_width: float = 680.0 if _minimized else 1100.0
	var target_height: float = 118.0 if _minimized else 790.0
	_shell.size = Vector2(minf(target_width, maxf(0.0, size.x - 24.0)), minf(target_height, maxf(0.0, size.y - 24.0)))
	if not _has_position:
		_panel_center = size * 0.5
	_set_panel_center(_panel_center)


func _on_viewport_resized() -> void:
	_resize_panel()
	if _shell == null:
		return
	# Preserve the same center when resizing, then recover a usable window.
	_set_panel_center(Vector2(
		clampf(_shell.position.x, 12.0, maxf(12.0, size.x - _shell.size.x - 12.0)),
		clampf(_shell.position.y, 12.0, maxf(12.0, size.y - _shell.size.y - 12.0))
	) + _shell.size * 0.5)
	if _had_pending and not _minimized:
		_ensure_confirmation_visible.call_deferred()


func _apply_panel_layout(vm: Dictionary) -> void:
	var minimized: bool = bool(vm.get("minimized", false))
	var layout_changed: bool = minimized != _minimized
	_minimized = minimized
	_expanded_content.visible = not _minimized
	_expanded_scroll.visible = not _minimized
	_compact_row.visible = _minimized
	_compact_status.visible = _minimized
	_header_hint.visible = not _minimized
	_header_title.add_theme_font_size_override("font_size", 18 if _minimized else 24)
	_minimize_button.text = "展开" if _minimized else "收起"
	_minimize_button.tooltip_text = "展开命令列表与参数。" if _minimized else "收起为快捷执行栏，保留参数并继续拦截游戏输入。"
	# A transparent mask deliberately keeps the same modal input ownership as
	# the expanded panel, matching the reference GM quick bar.
	_backdrop.color = Color.TRANSPARENT
	var saved_position: Variant = vm.get("panel_position", null)
	if bool(vm.get("has_panel_position", false)) and saved_position is Vector2 and not _dragging:
		_panel_center = saved_position
		_has_position = true
	elif not bool(vm.get("has_panel_position", false)) and not _dragging:
		_has_position = false
	_resize_panel()
	if layout_changed:
		# Containers settle after visibility changes; preserve the center as the
		# reference window does when expanding its compact execution bar.
		_resize_panel.call_deferred()
		if not _minimized and _had_pending:
			_ensure_confirmation_visible.call_deferred()


func _ensure_confirmation_visible() -> void:
	if not is_setup() or not is_inside_tree():
		return
	await get_tree().process_frame
	if is_setup() and not _minimized and _had_pending:
		_expanded_scroll.ensure_control_visible(_confirm)


func _apply_compact(command: Dictionary, valid: bool, result: Dictionary, reason: String = "") -> void:
	_compact_title.text = String(command.get("title", "尚未选择命令"))
	_compact_title.tooltip_text = "%s\n%s" % [command.get("id", ""), command.get("description", "")]
	_compact_execute.disabled = not valid
	_compact_execute.text = "确认执行" if String(command.get("risk", "normal")) != "normal" else "执行"
	_compact_execute.tooltip_text = "按保留的参数执行，执行前会重新校验。" if valid else "当前不可执行，请展开检查参数。\n" + _validation.text
	var status_color: Color = _MUTED
	if not valid:
		_compact_status.text = reason if not reason.is_empty() else (String(command.get("unavailable_reason", _validation.text)) if not command.is_empty() else "尚未选择命令，请展开面板选择。")
		if _compact_status.text.is_empty():
			_compact_status.text = "当前不可执行，请展开检查命令与参数。"
		status_color = _ERROR
	elif not result.is_empty():
		_compact_status.text = ("执行成功  " if bool(result.get("ok", false)) else "执行失败  ") + String(result.get("message", "操作已完成。"))
		status_color = _SUCCESS if bool(result.get("ok", false)) else _ERROR
	else:
		_compact_status.text = "参数已保留，当前可执行。"
	_compact_status.tooltip_text = _compact_status.text
	_compact_status.add_theme_color_override("font_color", status_color)


func _on_header_input(event: InputEvent) -> void:
	if event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_LEFT:
		_dragging = event.pressed
		if _dragging:
			_drag_offset = _pointer_position(event.global_position) - _panel_center
		_header.accept_event()
	elif event is InputEventMouseMotion and _dragging:
		if (event.button_mask & MOUSE_BUTTON_MASK_LEFT) == 0:
			_dragging = false
		else:
			_set_panel_center(_pointer_position(event.global_position) - _drag_offset)
		_header.accept_event()


func _pointer_position(pointer_global: Vector2) -> Vector2:
	return get_global_transform_with_canvas().affine_inverse() * pointer_global


func _set_panel_center(requested_center: Vector2) -> void:
	if _shell == null:
		return
	# Large panels can move partly off screen, but enough of the header stays
	# reachable to drag them back. This also recovers positions after resizing.
	var visible_width: float = minf(260.0, maxf(100.0, size.x - 24.0))
	var header_height: float = maxf(34.0, _header.get_combined_minimum_size().y) + 36.0
	var min_x: float = minf(12.0, visible_width - _shell.size.x)
	var max_x: float = maxf(12.0, size.x - visible_width - 12.0)
	var max_y: float = maxf(12.0, size.y - header_height - 12.0)
	var requested_position: Vector2 = requested_center - _shell.size * 0.5
	_shell.position = Vector2(clampf(requested_position.x, min_x, max_x), clampf(requested_position.y, 12.0, max_y))
	_panel_center = _shell.position + _shell.size * 0.5
	_has_position = true
	emit_action(&"position", {"position": _panel_center})


func _apply_sections(vm: Dictionary) -> void:
	var sections: Array = ["__recent", "__favorites", "__diagnostics"]
	sections.append_array(vm.get("sections", []))
	var selected: String = String(vm.get("section", ""))
	if selected == "__search":
		sections.append("__search")
	elif selected.is_empty():
		sections.push_front("")
	if _section.get_meta(&"sections", []) != sections:
		_section.set_meta(&"sections", sections.duplicate())
		_section.clear()
		for section: String in sections:
			_section.add_item(_section_label(section))
			_section.set_item_metadata(_section.item_count - 1, section)
	_section.select(-1)
	for index: int in range(_section.item_count):
		if String(_section.get_item_metadata(index)) == selected:
			_section.select(index)
			break
	var diagnostics: Array = vm.get("diagnostics", [])
	for index: int in range(_section.item_count):
		if String(_section.get_item_metadata(index)) == "__diagnostics":
			_section.set_item_text(index, "系统诊断" + ("（%d）" % diagnostics.size() if not diagnostics.is_empty() else ""))


func _apply_categories(vm: Dictionary) -> void:
	var categories: Array = vm.get("categories", [])
	var old_categories: Array = []
	for index: int in range(1, _category.item_count):
		old_categories.append(_category.get_item_metadata(index))
	if _category.item_count == 0 or old_categories != categories:
		_category.clear()
		_category.add_item("全部分类")
		_category.set_item_metadata(0, "")
		for category: String in categories:
			_category.add_item(_category_label(category))
			_category.set_item_metadata(_category.item_count - 1, category)
	var selected: String = String(vm.get("category", ""))
	_category.disabled = String(vm.get("section", "")) in ["__recent", "__diagnostics", "__search"]
	_category.select(0)
	for index: int in range(_category.item_count):
		if String(_category.get_item_metadata(index)) == selected:
			_category.select(index)
			break


func _apply_commands(vm: Dictionary) -> void:
	var commands: Array = vm.get("commands", [])
	var favorites: Dictionary = vm.get("favorites", {})
	var selected: String = String(vm.get("selected", ""))
	_count.text = "命令列表  %d / %d" % [commands.size(), int(vm.get("total", 0))]
	if String(vm.get("section", "")) == "__favorites":
		_count.text = "收藏列表  %d 项" % commands.size()
	var changed: bool = _command_list.item_count != commands.size()
	if not changed:
		for index: int in range(commands.size()):
			var command: Dictionary = commands[index]
			if _command_list.get_item_metadata(index) != command.get("id", "") or _command_list.get_item_text(index) != _command_text(command, favorites):
				changed = true
				break
	if changed:
		_command_list.clear()
		for command: Dictionary in commands:
			var index: int = _command_list.add_item(_command_text(command, favorites))
			_command_list.set_item_metadata(index, command.get("id", ""))
			_command_list.set_item_tooltip(index, "%s\n%s" % [command.get("id", ""), command.get("description", "")])
			if bool(command.get("unavailable", false)):
				_command_list.set_item_custom_fg_color(index, _MUTED)
				_command_list.set_item_tooltip(index, String(command.get("unavailable_reason", "当前环境不可用，可选中后取消收藏。")))
	_command_list.deselect_all()
	for index: int in range(_command_list.item_count):
		if String(_command_list.get_item_metadata(index)) == selected:
			_command_list.select(index)
			break


func _command_text(command: Dictionary, favorites: Dictionary) -> String:
	var id: String = String(command.get("id", ""))
	return ("★ " if favorites.has(id) else "") + String(command.get("title", id)) + ("（不可用）" if bool(command.get("unavailable", false)) else "")


func _apply_fields(command: Dictionary, values: Dictionary, inspection: Dictionary) -> void:
	var args: Array = command.get("args", [])
	var id: String = String(command.get("id", ""))
	if _field_command != id or _field_schema != args:
		_disconnect(_field_connections)
		for child: Node in _fields.get_children():
			_fields.remove_child(child)
			child.queue_free()
		_field_controls.clear()
		_field_errors.clear()
		_field_values.clear()
		_field_command = id
		_field_schema = args.duplicate(true)
		for arg: Dictionary in args:
			_build_field(arg)
		if not id.is_empty() and args.is_empty() and not bool(command.get("unavailable", false)):
			_fields.add_child(_label("此命令无需参数。", 13, _MUTED))
	var options: Dictionary = inspection.get("options", {})
	var errors: Dictionary = inspection.get("errors", {})
	for arg: Dictionary in args:
		var arg_id: String = String(arg.get("id", ""))
		var control: Control = _field_controls.get(arg_id, null)
		if control == null:
			continue
		var value: Variant = values.get(arg_id, arg.get("default", ""))
		if control is OptionButton:
			_apply_dropdown(control, options.get(arg_id, []), String(value))
		elif control is CheckBox:
			control.set_pressed_no_signal(bool(value) if value is bool else false)
		elif control is LineEdit or control is TextEdit:
			var text: String = str(value)
			if not _field_values.has(arg_id) or _field_values[arg_id] != text:
				_field_values[arg_id] = text
				if control.text != text:
					control.text = text
		var error_label: Label = _field_errors[arg_id]
		error_label.text = String(errors.get(arg_id, ""))
		error_label.visible = not error_label.text.is_empty()


func _build_field(arg: Dictionary) -> void:
	var id: String = String(arg.get("id", ""))
	var kind: String = String(arg.get("kind", "text"))
	var group: VBoxContainer = VBoxContainer.new()
	group.name = "Field_" + id.validate_node_name()
	group.add_theme_constant_override("separation", 4)
	_fields.add_child(group)
	var label_text: String = String(arg.get("label", id)) + (" *" if bool(arg.get("required", false)) else "")
	var bounds: Array[String] = []
	if arg.has("min"):
		bounds.append("最小值 %s" % str(arg["min"]))
	if arg.has("max"):
		bounds.append("最大值 %s" % str(arg["max"]))
	if not bounds.is_empty():
		label_text += "  (" + ", ".join(bounds) + ")"
	var field_label: Label = _label(label_text, 13)
	field_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	group.add_child(field_label)
	var control: Control = null
	match kind:
		"bool":
			var checkbox: CheckBox = CheckBox.new()
			checkbox.text = "启用"
			_connect(checkbox, &"toggled", func(value: bool) -> void: _field_changed(id, value), true)
			control = checkbox
		"dropdown":
			var dropdown: OptionButton = OptionButton.new()
			dropdown.fit_to_longest_item = false
			_connect(dropdown, &"item_selected", func(index: int) -> void: _field_changed(id, dropdown.get_item_metadata(index)), true)
			control = dropdown
		"text_area":
			var text_area: TextEdit = TextEdit.new()
			text_area.custom_minimum_size.y = 90
			text_area.wrap_mode = TextEdit.LINE_WRAPPING_BOUNDARY
			_connect(text_area, &"text_changed", func() -> void: _field_changed(id, text_area.text), true)
			control = text_area
		_:
			var line: LineEdit = LineEdit.new()
			line.placeholder_text = "请输入整数" if kind == "int" else ("请输入数值" if kind == "float" else "请输入内容")
			line.clear_button_enabled = true
			_connect(line, &"text_changed", func(value: String) -> void: _field_changed(id, value), true)
			control = line
	control.name = "Arg_" + id.validate_node_name()
	control.custom_minimum_size.y = maxf(control.custom_minimum_size.y, 34)
	control.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	group.add_child(control)
	_field_controls[id] = control
	var error_label: Label = _label("", 12, _ERROR)
	error_label.name = "Error_" + id.validate_node_name()
	error_label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	error_label.visible = false
	group.add_child(error_label)
	_field_errors[id] = error_label


func _apply_dropdown(dropdown: OptionButton, options: Array, value: String) -> void:
	var signature: Array = options.duplicate(true)
	var stale: bool = not value.is_empty()
	for option: Dictionary in options:
		if String(option.get("value", "")) == value:
			stale = false
			break
	signature.append(value if stale else "")
	if dropdown.get_meta(&"options", []) != signature:
		dropdown.set_meta(&"options", signature)
		dropdown.clear()
		dropdown.add_item("请选择…" if not options.is_empty() else "暂无可用选项")
		dropdown.set_item_metadata(0, "")
		for option: Dictionary in options:
			dropdown.add_item(String(option.get("label", option.get("value", ""))))
			dropdown.set_item_metadata(dropdown.item_count - 1, String(option.get("value", "")))
		if stale:
			dropdown.add_item("已失效：" + value)
			dropdown.set_item_metadata(dropdown.item_count - 1, value)
			dropdown.set_item_disabled(dropdown.item_count - 1, true)
	dropdown.select(0)
	for index: int in range(1, dropdown.item_count):
		if String(dropdown.get_item_metadata(index)) == value:
			dropdown.select(index)
			break
	dropdown.disabled = options.is_empty()


func _apply_confirmation(pending: Dictionary, valid: bool) -> void:
	_confirmation.visible = not pending.is_empty()
	if pending.is_empty():
		return
	var details: Array[String] = []
	var values: Dictionary = pending.get("values", {})
	for key: Variant in values:
		var label: String = str(key)
		for arg: Dictionary in _field_schema:
			if String(arg.get("id", "")) == str(key):
				label = String(arg.get("label", label))
				break
		var value: Variant = values[key]
		var displayed_value: String = ("是" if value else "否") if value is bool else str(value)
		details.append("%s = %s" % [label, displayed_value])
	var summary: String = "; ".join(details)
	var explanation: String = String(pending.get("confirmation", "")).strip_edges()
	if explanation.is_empty():
		explanation = "请确认参数与当前环境，确认后将执行此操作。"
	_confirmation_text.text = "请确认操作：%s（%s）\n%s\n%s" % [pending.get("title", pending.get("id", "")), _risk_label(String(pending.get("risk", "caution"))), explanation, summary if not summary.is_empty() else "无需参数。"]
	_confirmation_text.tooltip_text = _confirmation_text.text
	_confirm.disabled = not valid
	_confirm.text = "确认执行并关闭" if bool(pending.get("close_after", false)) else "确认执行"


func _apply_result(result: Dictionary) -> void:
	if result.is_empty():
		_output.text = "执行结果将显示在这里。"
		_output.add_theme_color_override("font_color", _MUTED)
		_output.tooltip_text = ""
		return
	var successful: bool = bool(result.get("ok", false))
	_output.text = ("执行成功  " if successful else "执行失败  ") + String(result.get("message", "操作已完成。" if successful else "操作未完成。"))
	if result.has("data"):
		_output.text += "\n" + _display_value(result["data"])
	_output.add_theme_color_override("font_color", _SUCCESS if successful else _ERROR)
	_output.tooltip_text = _output.text


func _apply_history(entries: Array, selected: int) -> void:
	var previous: Variant = _history.get_meta(&"entries", [])
	if previous != entries:
		_history.set_meta(&"entries", entries.duplicate(true))
		_history.clear()
		for index: int in range(entries.size() - 1, -1, -1):
			var entry: Dictionary = entries[index]
			var result: Dictionary = entry.get("result", {})
			var ok: bool = bool(result.get("ok", false))
			var row: String = "%s  %s" % ["成功" if ok else "失败", entry.get("title", entry.get("id", ""))]
			var item: int = _history.add_item(row)
			_history.set_item_metadata(item, int(entry.get("sequence", index)))
			_history.set_item_custom_fg_color(item, _SUCCESS if ok else _ERROR)
			_history.set_item_tooltip(item, "%s\n%s\n%s" % [row, result.get("message", ""), _display_value(entry.get("values", {}))])
	_history.deselect_all()
	for index: int in range(_history.item_count):
		if int(_history.get_item_metadata(index)) == selected:
			_history.select(index)
			break


func _apply_history_detail(entry: Dictionary) -> void:
	_history_load.disabled = entry.is_empty() or not bool(entry.get("available", false))
	_history_replay.disabled = entry.is_empty() or not bool(entry.get("replay_available", false))
	_history_replay.text = "载入并确认" if String(entry.get("risk", "normal")) != "normal" else "再次执行"
	if entry.is_empty():
		_history_detail_text.text = "尚无执行记录。执行命令后可在这里查看详情、载入参数和再次执行。"
		return
	var result: Dictionary = entry.get("result", {})
	var lines: Array[String] = [
		"%s\n%s" % [entry.get("title", entry.get("id", "")), entry.get("id", "")],
		"时间：" + _history_time(entry),
		"环境：" + String(entry.get("environment", entry.get("target_name", "未记录"))),
		"目录：%s / %s" % [entry.get("section", "通用"), entry.get("category", "通用")],
		"范围：%s    来源：%s" % [_scope_label(String(entry.get("scope", ""))), entry.get("source", entry.get("owner", "未记录"))],
		"参数：\n" + _display_value(entry.get("values", {})),
		"结果：%s\n%s" % ["成功" if bool(result.get("ok", false)) else "失败", result.get("message", "未记录")]
	]
	if result.has("data"):
		lines.append("结果数据：\n" + _display_value(result["data"]))
	if not bool(entry.get("available", false)) or not bool(entry.get("replay_available", false)):
		lines.append("当前不可执行：" + String(entry.get("unavailable_reason", entry.get("reason", "命令在当前环境不可用，或历史参数已经失效。"))))
	_history_detail_text.text = "\n\n".join(lines)


func _apply_diagnostics(entries: Array) -> void:
	var lines: Array[String] = ["系统诊断"]
	if entries.is_empty():
		lines.append("当前没有注册异常。")
	else:
		lines.append("共 %d 条注册诊断。请检查重复标识、命令定义和所属模块。" % entries.size())
		for index: int in range(entries.size()):
			var entry: Dictionary = entries[index]
			lines.append("%d. %s\n模块：%s\n错误码：%s\n%s" % [index + 1, entry.get("id", "未提供命令标识"), entry.get("owner", "未提供模块"), entry.get("code", "未提供"), entry.get("message", "未提供说明")])
	_diagnostics_text.text = "\n\n".join(lines)


func _history_time(entry: Dictionary) -> String:
	var readable: String = String(entry.get("timestamp_text", entry.get("time_text", "")))
	if not readable.is_empty():
		return readable
	var timestamp: Variant = entry.get("timestamp", entry.get("unix_time", entry.get("time_unix", null)))
	if timestamp is String:
		return timestamp
	if timestamp is int or timestamp is float:
		if float(timestamp) > 0.0:
			return Time.get_datetime_string_from_unix_time(int(timestamp), true) + " UTC"
	return "未记录"


func _display_value(value: Variant) -> String:
	if value is bool:
		return "是" if value else "否"
	if value is Dictionary or value is Array:
		return JSON.stringify(value, "  ")
	return str(value)


func _category_selected(index: int) -> void:
	_emit_value(&"category", _category.get_item_metadata(index))


func _command_selected(index: int) -> void:
	if not _applying:
		emit_action(&"select", {"id": _command_list.get_item_metadata(index)})


func _field_changed(id: String, value: Variant) -> void:
	if not _applying:
		emit_action(&"value", {"id": id, "value": value})


func _emit_value(action_name: StringName, value: Variant) -> void:
	if not _applying:
		emit_action(action_name, {"value": value})


func _connect(emitter: Object, signal_name: StringName, callback: Callable, field: bool = false) -> void:
	emitter.connect(signal_name, callback)
	var connections: Array[Dictionary] = _field_connections if field else _connections
	connections.append({"emitter": emitter, "signal": signal_name, "callback": callback})


func _disconnect(connections: Array[Dictionary]) -> void:
	for connection: Dictionary in connections:
		var emitter: Object = connection["emitter"]
		var callback: Callable = connection["callback"]
		if is_instance_valid(emitter) and emitter.is_connected(connection["signal"], callback):
			emitter.disconnect(connection["signal"], callback)
	connections.clear()


func _button(text: String, node_name: String) -> Button:
	var button: Button = Button.new()
	button.name = node_name
	button.text = text
	button.custom_minimum_size.y = 34
	button.mouse_default_cursor_shape = Control.CURSOR_POINTING_HAND
	return button


func _risk_label(risk: String) -> String:
	match risk:
		"caution": return "需确认"
		"destructive": return "高风险"
		_: return "普通"


func _category_label(category: String) -> String:
	return "通用" if category == "General" else category


func _section_label(section: String) -> String:
	match section:
		"__recent": return "最近使用"
		"__favorites": return "收藏"
		"__diagnostics": return "系统诊断"
		"__search": return "搜索结果"
		"": return "全部命令"
		_: return _category_label(section)


func _scope_label(scope: String) -> String:
	match scope:
		"global": return "全局"
		"target", "mode": return "当前环境"
		"": return "未指定"
		_: return scope


func _label(text: String, font_size: int, color: Color = _TEXT) -> Label:
	var label: Label = Label.new()
	label.text = text
	label.add_theme_font_size_override("font_size", font_size)
	label.add_theme_color_override("font_color", color)
	return label


func _box(background: Color, border: Color, radius: int, margin: int) -> StyleBoxFlat:
	var box: StyleBoxFlat = StyleBoxFlat.new()
	box.bg_color = background
	box.border_color = border
	box.set_border_width_all(1)
	box.set_corner_radius_all(radius)
	box.content_margin_left = margin
	box.content_margin_right = margin
	box.content_margin_top = margin
	box.content_margin_bottom = margin
	return box


func _make_theme() -> Theme:
	var result: Theme = Theme.new()
	result.default_font_size = 14
	for type: String in ["Label", "Button", "LineEdit", "TextEdit", "ItemList", "CheckBox", "CheckButton", "OptionButton", "PopupMenu"]:
		result.set_color("font_color", type, _TEXT)
		result.set_color("font_hover_color", type, Color.WHITE)
		result.set_color("font_pressed_color", type, Color.WHITE)
		result.set_color("font_focus_color", type, Color.WHITE)
		result.set_color("font_disabled_color", type, Color("6f7c8f"))
	for type: String in ["Button", "OptionButton"]:
		result.set_stylebox("normal", type, _box(Color("263348"), Color("42516a"), 6, 9))
		result.set_stylebox("hover", type, _box(Color("334865"), Color("7492b9"), 6, 9))
		result.set_stylebox("pressed", type, _box(Color("1d4166"), _ACCENT, 6, 9))
		result.set_stylebox("disabled", type, _box(Color("202938"), Color("303c4e"), 6, 9))
		var focus: StyleBoxFlat = _box(Color.TRANSPARENT, _ACCENT, 6, 0)
		focus.draw_center = false
		result.set_stylebox("focus", type, focus)
	for type: String in ["LineEdit", "TextEdit", "ItemList"]:
		result.set_stylebox("normal" if type != "ItemList" else "panel", type, _box(Color("111a27"), Color("354359"), 6, 8))
		var focus: StyleBoxFlat = _box(Color.TRANSPARENT, _ACCENT, 6, 0)
		focus.draw_center = false
		result.set_stylebox("focus", type, focus)
	result.set_stylebox("selected", "ItemList", _box(Color("284d74"), Color("4d79a6"), 4, 4))
	result.set_stylebox("selected_focus", "ItemList", _box(Color("284d74"), _ACCENT, 4, 4))
	result.set_stylebox("panel", "PopupMenu", _box(Color("1c293b"), Color("516580"), 6, 8))
	result.set_color("font_placeholder_color", "LineEdit", _MUTED)
	return result
