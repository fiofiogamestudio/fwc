@tool
class_name FAutoFitLabel
extends Label

@export_range(1, 256, 1) var minimum_font_size := 12
@export_range(1, 256, 1) var maximum_font_size := 32
@export var fit_on_ready := true
@export var fit_on_resize := true

var _fit_queued := false


func _ready() -> void:
	if fit_on_ready:
		_queue_fit()


func _notification(what: int) -> void:
	if what == NOTIFICATION_RESIZED and fit_on_resize and is_node_ready():
		_queue_fit()


func fit_text() -> void:
	var font := get_theme_font("font")
	if font == null:
		return
	var lower := mini(minimum_font_size, maximum_font_size)
	var upper := maxi(minimum_font_size, maximum_font_size)
	var selected_size := lower
	for font_size in range(upper, lower - 1, -1):
		var text_size := font.get_string_size(text, HORIZONTAL_ALIGNMENT_LEFT, -1.0, font_size)
		if text_size.x <= size.x and font.get_height(font_size) <= size.y:
			selected_size = font_size
			break
	add_theme_font_size_override("font_size", selected_size)


func _queue_fit() -> void:
	if _fit_queued:
		return
	_fit_queued = true
	call_deferred("_fit_queued_text")


func _fit_queued_text() -> void:
	_fit_queued = false
	fit_text()
