class_name FUIEvent
extends "res://fw/scripts/fw/rt/event/_event_bus.gd"

signal emitted(kind: StringName, payload: Variant)


func emit_event(kind: StringName, payload: Variant = null) -> int:
	emitted.emit(kind, payload)
	return super.emit_event(kind, payload)
