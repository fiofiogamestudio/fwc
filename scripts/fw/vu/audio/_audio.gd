class_name FAudio
extends RefCounted

var _host: Node = null


func setup(host: Node) -> void:
	_host = host


func clear() -> void:
	_host = null


func create_player_3d(
	parent: Node,
	player_name: StringName,
	bus: StringName = &"Master"
) -> AudioStreamPlayer3D:
	if parent == null:
		return null
	var existing := parent.get_node_or_null(NodePath(String(player_name))) as AudioStreamPlayer3D
	if existing != null:
		return existing
	var player := AudioStreamPlayer3D.new()
	player.name = player_name
	player.bus = bus
	parent.add_child(player)
	return player


func play_3d(
	player: AudioStreamPlayer3D,
	stream: AudioStream,
	volume_db: float = 0.0,
	pitch_scale: float = 1.0,
	max_distance: float = 12.0,
	unit_size: float = 1.0
) -> bool:
	if player == null or stream == null:
		return false
	player.stream = stream
	player.volume_db = volume_db
	player.pitch_scale = clampf(pitch_scale, 0.01, 4.0)
	player.max_distance = maxf(max_distance, 0.1)
	player.unit_size = maxf(unit_size, 0.01)
	player.play()
	return true
