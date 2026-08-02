class_name FAudio
extends RefCounted

const KIND_BGM: StringName = &"bgm"
const KIND_SFX: StringName = &"sfx"
const SILENT_DB := -80.0

var max_sfx_players := 32

var _host: Node = null
var _root: Node = null
var _bgm_players: Array[AudioStreamPlayer] = []
var _current_bgm_player: AudioStreamPlayer = null
var _current_bgm_id: StringName = &""
var _bgm_tween: Tween = null
var _streams: Dictionary = {}
var _active_sfx: Dictionary = {}
var _free_sfx: Array[AudioStreamPlayer] = []
var _sfx_tweens: Dictionary = {}


func setup(host: Node) -> void:
	clear()
	_host = host
	if _host == null:
		return
	_root = Node.new()
	_root.name = "FWAudio"
	_host.add_child(_root)
	for index in range(2):
		var player := AudioStreamPlayer.new()
		player.name = "BGM%d" % (index + 1)
		_root.add_child(player)
		_bgm_players.append(player)


func register_stream(
	id: StringName,
	stream: AudioStream,
	kind: StringName = KIND_SFX,
	bus: StringName = &"Master",
	volume_db: float = 0.0,
	max_instances: int = 0,
	loop_stream: AudioStream = null
) -> bool:
	if id == &"" or stream == null:
		return false
	_streams[id] = {
		"stream": stream,
		"kind": kind,
		"bus": bus,
		"volume_db": volume_db,
		"max_instances": maxi(0, max_instances),
		"loop_stream": _make_looping_stream(loop_stream) if loop_stream != null else null,
	}
	return true


func register_bgm(
	id: StringName,
	stream: AudioStream,
	bus: StringName = &"Master",
	volume_db: float = 0.0
) -> bool:
	return register_stream(id, stream, KIND_BGM, bus, volume_db)


func register_bgm_intro_loop(
	id: StringName,
	intro_stream: AudioStream,
	loop_stream: AudioStream,
	bus: StringName = &"Master",
	volume_db: float = 0.0
) -> bool:
	if intro_stream == null or loop_stream == null:
		return false
	return register_stream(id, intro_stream, KIND_BGM, bus, volume_db, 0, loop_stream)


func register_sfx(
	id: StringName,
	stream: AudioStream,
	bus: StringName = &"Master",
	volume_db: float = 0.0,
	max_instances: int = 0
) -> bool:
	return register_stream(id, stream, KIND_SFX, bus, volume_db, max_instances)


func unregister_stream(id: StringName) -> void:
	if _current_bgm_id == id:
		stop_bgm()
	stop_sfx_by_id(id)
	_streams.erase(id)


func has_stream(id: StringName) -> bool:
	return _streams.has(id)


func registered_ids() -> Array[StringName]:
	var result: Array[StringName] = []
	for raw_id in _streams.keys():
		result.append(StringName(raw_id))
	return result


func stream_config(id: StringName) -> Dictionary:
	var config: Dictionary = _streams.get(id, {})
	return config.duplicate()


func current_bgm_id() -> StringName:
	return _current_bgm_id


func play_bgm(
	id: StringName,
	fade_in: float = 0.0,
	fade_out: float = 0.0,
	restart: bool = false
) -> bool:
	var config: Dictionary = _streams.get(id, {})
	if config.is_empty() or StringName(config.get("kind", &"")) != KIND_BGM:
		return false
	if (
		_current_bgm_id == id
		and _current_bgm_player != null
		and _current_bgm_player.playing
		and not restart
	):
		return true

	var next_player := _next_bgm_player()
	if next_player == null:
		return false
	var previous := _current_bgm_player
	_configure_bgm_player(next_player, id, config)
	var target_db := float(config.get("volume_db", 0.0))
	next_player.volume_db = SILENT_DB if fade_in > 0.0 else target_db
	_current_bgm_player = next_player
	_current_bgm_id = id
	next_player.play()

	_kill_bgm_tween()
	if _host != null and (fade_in > 0.0 or (previous != null and fade_out > 0.0)):
		_bgm_tween = _host.create_tween()
		_bgm_tween.set_parallel(true)
		if fade_in > 0.0:
			_bgm_tween.tween_property(next_player, "volume_db", target_db, fade_in)
		if previous != null and previous != next_player:
			if fade_out > 0.0:
				_bgm_tween.tween_property(previous, "volume_db", SILENT_DB, fade_out)
				_bgm_tween.chain().tween_callback(
					Callable(self, "_stop_bgm_player").bind(previous)
				)
			else:
				_stop_bgm_player(previous)
	elif previous != null and previous != next_player:
		_stop_bgm_player(previous)

	return true


func stop_bgm(fade_out: float = 0.0) -> void:
	var player := _current_bgm_player
	_current_bgm_player = null
	_current_bgm_id = &""
	_kill_bgm_tween()
	if player == null:
		return
	if fade_out > 0.0 and _host != null:
		_bgm_tween = _host.create_tween()
		_bgm_tween.tween_property(player, "volume_db", SILENT_DB, fade_out)
		_bgm_tween.tween_callback(Callable(self, "_stop_bgm_player").bind(player))
	else:
		_stop_bgm_player(player)


func play_sfx(
	id: StringName,
	volume_db_offset: float = 0.0,
	pitch_scale: float = 1.0,
	loop: bool = false
) -> AudioStreamPlayer:
	var config: Dictionary = _streams.get(id, {})
	if config.is_empty() or StringName(config.get("kind", &"")) != KIND_SFX:
		return null
	if _root == null or (max_sfx_players > 0 and _active_sfx.size() >= max_sfx_players):
		return null
	var max_instances := int(config.get("max_instances", 0))
	if max_instances > 0 and _active_count(id) >= max_instances:
		return null

	var player := _acquire_sfx_player()
	_configure_player(player, config)
	if loop:
		player.stream = _make_looping_stream(player.stream)
	player.volume_db += volume_db_offset
	player.pitch_scale = maxf(0.01, pitch_scale)
	var callback := Callable(self, "_on_sfx_finished").bind(player, loop)
	player.set_meta("_fw_finished_callback", callback)
	player.finished.connect(callback, CONNECT_ONE_SHOT)
	_active_sfx[player.get_instance_id()] = {"player": player, "id": id}
	player.play()
	return player


func play_loop(
	id: StringName,
	volume_db_offset: float = 0.0,
	pitch_scale: float = 1.0
) -> AudioStreamPlayer:
	return play_sfx(id, volume_db_offset, pitch_scale, true)


func stop_sfx(player: AudioStreamPlayer, fade_out: float = 0.0) -> void:
	if player == null or not is_instance_valid(player):
		return
	_kill_sfx_tween(player)
	if fade_out > 0.0 and _host != null and _active_sfx.has(player.get_instance_id()):
		var tween := _host.create_tween()
		_sfx_tweens[player.get_instance_id()] = tween
		tween.tween_property(player, "volume_db", SILENT_DB, fade_out)
		tween.tween_callback(Callable(self, "_release_sfx_player").bind(player))
		return
	_release_sfx_player(player)


func stop_sfx_by_id(id: StringName, fade_out: float = 0.0) -> void:
	for entry in _active_sfx.values().duplicate():
		if StringName(entry.get("id", &"")) == id:
			stop_sfx(entry.get("player", null), fade_out)


func stop_all_sfx(fade_out: float = 0.0) -> void:
	for entry in _active_sfx.values().duplicate():
		stop_sfx(entry.get("player", null), fade_out)


func set_bus_volume_linear(bus: StringName, value: float) -> bool:
	var index := AudioServer.get_bus_index(String(bus))
	if index < 0:
		return false
	var normalized := clampf(value, 0.0, 1.0)
	AudioServer.set_bus_mute(index, normalized <= 0.0001)
	if normalized > 0.0001:
		AudioServer.set_bus_volume_db(index, linear_to_db(normalized))
	return true


func bus_volume_linear(bus: StringName) -> float:
	var index := AudioServer.get_bus_index(String(bus))
	if index < 0 or AudioServer.is_bus_mute(index):
		return 0.0
	return clampf(db_to_linear(AudioServer.get_bus_volume_db(index)), 0.0, 1.0)


func set_bus_muted(bus: StringName, muted: bool) -> bool:
	var index := AudioServer.get_bus_index(String(bus))
	if index < 0:
		return false
	AudioServer.set_bus_mute(index, muted)
	return true


func is_bus_muted(bus: StringName) -> bool:
	var index := AudioServer.get_bus_index(String(bus))
	return index >= 0 and AudioServer.is_bus_mute(index)


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


func clear() -> void:
	_kill_bgm_tween()
	stop_all_sfx()
	for player in _bgm_players:
		if is_instance_valid(player):
			_stop_bgm_player(player)
	_bgm_players.clear()
	_free_sfx.clear()
	_active_sfx.clear()
	_sfx_tweens.clear()
	_streams.clear()
	_current_bgm_player = null
	_current_bgm_id = &""
	if _root != null and is_instance_valid(_root):
		var parent := _root.get_parent()
		if parent != null:
			parent.remove_child(_root)
		_root.queue_free()
	_root = null
	_host = null


func _next_bgm_player() -> AudioStreamPlayer:
	for player in _bgm_players:
		if player != _current_bgm_player:
			return player
	return _bgm_players[0] if not _bgm_players.is_empty() else null


func _configure_player(player: AudioStreamPlayer, config: Dictionary) -> void:
	player.stream = config.get("stream", null)
	player.bus = String(config.get("bus", &"Master"))
	player.volume_db = float(config.get("volume_db", 0.0))
	player.pitch_scale = 1.0


func _configure_bgm_player(
	player: AudioStreamPlayer,
	id: StringName,
	config: Dictionary
) -> void:
	_clear_bgm_finished_callback(player)
	_configure_player(player, config)
	var loop_stream := config.get("loop_stream", null) as AudioStream
	if loop_stream == null:
		return
	var callback := Callable(self, "_on_bgm_intro_finished").bind(player, id)
	player.set_meta("_fw_bgm_finished_callback", callback)
	player.finished.connect(callback, CONNECT_ONE_SHOT)


func _acquire_sfx_player() -> AudioStreamPlayer:
	while not _free_sfx.is_empty():
		var player := _free_sfx.pop_back() as AudioStreamPlayer
		if is_instance_valid(player):
			return player
	var created := AudioStreamPlayer.new()
	created.name = "SFX"
	_root.add_child(created)
	return created


func _release_sfx_player(player: AudioStreamPlayer) -> void:
	if player == null or not is_instance_valid(player):
		return
	_kill_sfx_tween(player)
	_active_sfx.erase(player.get_instance_id())
	var callback: Callable = player.get_meta("_fw_finished_callback", Callable())
	if callback.is_valid() and player.finished.is_connected(callback):
		player.finished.disconnect(callback)
	player.remove_meta("_fw_finished_callback")
	player.stop()
	player.stream = null
	player.volume_db = 0.0
	player.pitch_scale = 1.0
	if not _free_sfx.has(player):
		_free_sfx.append(player)


func _active_count(id: StringName) -> int:
	var count := 0
	for entry in _active_sfx.values():
		if StringName(entry.get("id", &"")) == id:
			count += 1
	return count


func _on_sfx_finished(player: AudioStreamPlayer, loop: bool) -> void:
	if loop and player != null and _active_sfx.has(player.get_instance_id()):
		var callback := Callable(self, "_on_sfx_finished").bind(player, true)
		player.set_meta("_fw_finished_callback", callback)
		player.finished.connect(callback, CONNECT_ONE_SHOT)
		player.play()
		return
	_release_sfx_player(player)


func _on_bgm_intro_finished(player: AudioStreamPlayer, id: StringName) -> void:
	if player != _current_bgm_player or id != _current_bgm_id:
		return
	var config: Dictionary = _streams.get(id, {})
	var loop_stream := config.get("loop_stream", null) as AudioStream
	if loop_stream == null:
		return
	_clear_bgm_finished_callback(player)
	player.stream = loop_stream
	player.play()


func _stop_bgm_player(player: AudioStreamPlayer) -> void:
	if player == null or not is_instance_valid(player):
		return
	_clear_bgm_finished_callback(player)
	player.stop()


func _clear_bgm_finished_callback(player: AudioStreamPlayer) -> void:
	var callback: Callable = player.get_meta("_fw_bgm_finished_callback", Callable())
	if callback.is_valid() and player.finished.is_connected(callback):
		player.finished.disconnect(callback)
	player.remove_meta("_fw_bgm_finished_callback")


func _kill_sfx_tween(player: AudioStreamPlayer) -> void:
	var instance_id := player.get_instance_id()
	var tween := _sfx_tweens.get(instance_id, null) as Tween
	if tween != null and tween.is_valid():
		tween.kill()
	_sfx_tweens.erase(instance_id)


func _make_looping_stream(stream: AudioStream) -> AudioStream:
	if stream == null:
		return null
	var looping := stream.duplicate(true) as AudioStream
	if looping is AudioStreamWAV:
		looping.loop_mode = AudioStreamWAV.LOOP_FORWARD
	elif looping is AudioStreamOggVorbis:
		looping.loop = true
	elif looping is AudioStreamMP3:
		looping.loop = true
	return looping


func _kill_bgm_tween() -> void:
	if _bgm_tween != null and _bgm_tween.is_valid():
		_bgm_tween.kill()
	_bgm_tween = null
