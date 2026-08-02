@tool
class_name FAutoFitRichTextLabel
extends RichTextLabel

@export_range(1, 256, 1) var minimum_font_size := 12
@export_range(1, 256, 1) var maximum_font_size := 32
@export_range(1.0, 2.0, 0.05) var measurement_height_tolerance := 1.0
@export_range(0.1, 1.0, 0.05) var quoted_text_scale := 1.0
@export var fit_on_ready := true
@export var fit_on_resize := true

var _fit_queued := false
var _source_bbcode := ""
var _rendered_bbcode := ""
var _fit_source_text := ""
var _fit_renderer := Callable()


func _ready() -> void:
	if fit_on_ready:
		_queue_fit()


func _notification(what: int) -> void:
	if what == NOTIFICATION_RESIZED and fit_on_resize and is_node_ready():
		_queue_fit()


func fit_text() -> void:
	var font := get_theme_font("normal_font")
	if font == null:
		return
	if _fit_renderer.is_valid() and text == _rendered_bbcode:
		_fit_rendered_text()
		return
	if _fit_renderer.is_valid():
		_fit_renderer = Callable()
		_fit_source_text = ""
	var lower := mini(minimum_font_size, maximum_font_size)
	var upper := maxi(minimum_font_size, maximum_font_size)
	var selected_size := lower
	if text != _rendered_bbcode:
		_source_bbcode = text
	var source_bbcode := _source_bbcode
	var plain_text := get_parsed_text()
	for font_size in range(upper, lower - 1, -1):
		var text_size := font.get_multiline_string_size(
			plain_text,
			HORIZONTAL_ALIGNMENT_LEFT,
			size.x,
			font_size
		)
		if text_size.y <= size.y * measurement_height_tolerance + 0.5:
			selected_size = font_size
			break
	_apply_size(source_bbcode, selected_size)
	while selected_size > lower and get_content_height() > size.y + 0.5:
		selected_size -= 1
		_apply_size(source_bbcode, selected_size)


func set_fit_text_renderer(source_text: String, renderer: Callable) -> void:
	_fit_source_text = source_text
	_fit_renderer = renderer
	_source_bbcode = ""
	if not _fit_renderer.is_valid():
		text = source_text
		_rendered_bbcode = text
		_queue_fit()
		return
	var upper := maxi(minimum_font_size, maximum_font_size)
	_apply_size(_render_fit_source(upper), upper)
	_queue_fit()


func clear_fit_text_renderer() -> void:
	_fit_renderer = Callable()
	_fit_source_text = ""


func _fit_rendered_text() -> void:
	var lower := mini(minimum_font_size, maximum_font_size)
	var upper := maxi(minimum_font_size, maximum_font_size)
	var selected_size := lower
	for font_size in range(upper, lower - 1, -1):
		_apply_size(_render_fit_source(font_size), font_size)
		if get_content_height() <= size.y + 0.5:
			selected_size = font_size
			break
	if selected_size == lower:
		_apply_size(_render_fit_source(lower), lower)


func _render_fit_source(font_size: int) -> String:
	if not _fit_renderer.is_valid():
		return _fit_source_text
	return str(_fit_renderer.call(_fit_source_text, font_size))


func _apply_size(source_bbcode: String, font_size: int) -> void:
	_set_font_size(font_size)
	text = source_bbcode
	_apply_quoted_text_size(font_size)
	_rendered_bbcode = text


func _set_font_size(font_size: int) -> void:
	add_theme_font_size_override("normal_font_size", font_size)
	add_theme_font_size_override("bold_font_size", font_size)
	add_theme_font_size_override("italics_font_size", font_size)
	add_theme_font_size_override("bold_italics_font_size", font_size)


func _apply_quoted_text_size(base_font_size: int) -> void:
	if quoted_text_scale >= 0.999:
		return
	var quote_size := maxi(1, roundi(float(base_font_size) * quoted_text_scale))
	var regex := RegEx.new()
	if regex.compile("(\"[^\"]+\"|“[^”]+”)") != OK:
		return
	text = regex.sub(text, "[font_size=%d]$1[/font_size]" % quote_size, true)


func _queue_fit() -> void:
	if _fit_queued:
		return
	_fit_queued = true
	call_deferred("_fit_queued_text")


func _fit_queued_text() -> void:
	_fit_queued = false
	fit_text()
