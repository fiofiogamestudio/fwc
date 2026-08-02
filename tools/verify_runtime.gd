extends SceneTree

const FAssetScript = preload("res://fw/scripts/fw/rt/_asset.gd")
const FAudioScript = preload("res://fw/scripts/fw/vu/audio/_audio.gd")
const FDisplayScript = preload("res://fw/scripts/fw/rt/display/_display.gd")
const FDebugScript = preload("res://fw/scripts/fw/rt/debug/_debug.gd")
const FEventBusScript = preload("res://fw/scripts/fw/rt/event/_event_bus.gd")
const FLogScript = preload("res://fw/scripts/fw/rt/log/_log.gd")
const FPoolScript = preload("res://fw/scripts/fw/rt/pool/_pool.gd")
const FStateMachineScript = preload("res://fw/scripts/fw/rt/state/_state_machine.gd")
const SystemManagerScript = preload("res://fw/scripts/fw/rt/system/_system_manager.gd")

var _trace: Array[String] = []


class TestRefs extends RefCounted:
	var dependency: Variant = null
	var input: Variant = null


class TestContext extends RefCounted:
	var refs := TestRefs.new()
	var name := ""
	var trace: Array[String] = []

	func _init(value: String, target_trace: Array[String]) -> void:
		name = value
		trace = target_trace


class TestSystem extends RefCounted:
	var _context: Variant = null

	func init(context: Variant) -> bool:
		_context = context
		context.trace.append("init:" + context.name)
		return true

	func tick(_dt: float) -> void:
		_context.trace.append("tick:" + _context.name)

	func shutdown() -> void:
		_context.trace.append("shutdown:" + _context.name)
		_context = null


class TestAssetProvider extends RefCounted:
	var load_count := 0
	var release_count := 0

	func exists(_path: String, _type_hint: String = "") -> bool:
		return true

	func load(_path: String, _expected_type: Variant = null) -> Resource:
		load_count += 1
		return Resource.new()

	func release(_asset: Resource) -> void:
		release_count += 1


class AsyncAssetProvider extends TestAssetProvider:
	func load_async(_path: String, _expected_type: Variant = null) -> Resource:
		load_count += 1
		var tree := Engine.get_main_loop() as SceneTree
		if tree != null:
			await tree.process_frame
		return Resource.new()


class EphemeralAssetProvider extends Node:
	func load(_path: String, _expected_type: Variant = null) -> Resource:
		return Resource.new()


class AsyncAssetConsumer extends RefCounted:
	signal finished(handle: Variant)

	func run(asset: Variant, path: String) -> void:
		var handle: Variant = await asset.acquire_async(path, Resource)
		finished.emit(handle)


func _initialize() -> void:
	_run.call_deferred()


func _run() -> void:
	var error := _verify_event_bus()
	if error.is_empty():
		error = _verify_state_machine()
	if error.is_empty():
		error = _verify_system_manager()
	if error.is_empty():
		error = await _verify_asset()
	if error.is_empty():
		error = _verify_pool()
	if error.is_empty():
		error = _verify_log()
	if error.is_empty():
		error = _verify_audio_and_display()
	if error.is_empty():
		error = _verify_debug()
	if not error.is_empty():
		push_error(error)
		quit(1)
		return
	if not _write_success_marker():
		push_error("Could not write the FW runtime verification success marker.")
		quit(1)
		return
	print("Verified FW Godot runtime services and lifecycle invariants.")
	quit(0)


func _write_success_marker() -> bool:
	var path := OS.get_environment("FW_RUNTIME_VERIFY_MARKER")
	if path.is_empty():
		return true
	var file := FileAccess.open(path, FileAccess.WRITE)
	if file == null:
		return false
	file.store_string("ok\n")
	file.close()
	return true


func _verify_event_bus() -> String:
	_trace.clear()
	var bus = FEventBusScript.new()
	bus.on(&"value", Callable(self, "_record_low_event"), 0)
	bus.once(&"value", Callable(self, "_record_high_event"), 10)
	if bus.emit_event(&"value", 7) != 2 or bus.emit_event(&"value", 8) != 1:
		return "FEventBus returned an incorrect invocation count."
	if ",".join(_trace) != "high:7,low:7,low:8":
		return "FEventBus priority or once semantics are incorrect: %s" % _trace
	return ""


func _verify_state_machine() -> String:
	_trace.clear()
	var machine = FStateMachineScript.new()
	machine.setup(_trace)
	machine.register_state(
		&"idle",
		Callable(self, "_state_enter").bind("idle"),
		Callable(self, "_state_tick").bind("idle"),
		Callable(self, "_state_exit").bind("idle")
	)
	machine.register_state(
		&"run",
		Callable(self, "_state_enter").bind("run"),
		Callable(),
		Callable(self, "_state_exit").bind("run")
	)
	machine.register_transition(
		&"idle",
		&"run",
		Callable(self, "_state_transition"),
		Callable(self, "_state_guard")
	)
	if not machine.start(&"idle", "start"):
		return "FStateMachine could not start."
	machine.tick(0.1)
	if machine.transition_to(&"run", "blocked"):
		return "FStateMachine ignored its transition guard."
	if not machine.transition_to(&"run", "go"):
		return "FStateMachine rejected a valid transition."
	machine.stop()
	var expected := "enter:idle:start,tick:idle,exit:idle,transition:idle:run,enter:run:go,exit:run"
	if ",".join(_trace) != expected:
		return "FStateMachine lifecycle order is incorrect: %s" % _trace
	var stop_machine = FStateMachineScript.new()
	stop_machine.setup([])
	stop_machine.register_state(
		&"idle",
		Callable(),
		Callable(),
		Callable(self, "_queue_transition_on_exit").bind(stop_machine)
	)
	stop_machine.register_state(&"run")
	if not stop_machine.start(&"idle"):
		return "FStateMachine stop-transition fixture could not start."
	stop_machine.stop()
	if not stop_machine.start(&"idle") or stop_machine.current_state() != &"idle":
		return "FStateMachine retained a transition queued by a stop callback."
	stop_machine.clear()
	return ""


func _verify_system_manager() -> String:
	_trace.clear()
	var parent = SystemManagerScript.new()
	var root_context := TestContext.new("root", _trace)
	if not parent.add_system(&"root", TestSystem.new(), root_context, &"app"):
		return "SystemManager could not add a parent system."
	if not parent.init_all():
		return "SystemManager could not initialize parent systems."

	var child = SystemManagerScript.new(parent)
	child.set_phase_order([&"input", &"present"])
	var input_context := TestContext.new("input", _trace)
	var present_context := TestContext.new("present", _trace)
	child.add_system(&"present", TestSystem.new(), present_context, &"present")
	child.add_system(&"input", TestSystem.new(), input_context, &"present")
	if not child.bind_refs(
		{&"present": {&"dependency": &"root", &"input": &"input"}, &"input": {}}
	):
		return "SystemManager could not bind a parent context reference."
	if present_context.refs.dependency != root_context or present_context.refs.input != input_context:
		return "SystemManager bound the wrong parent context."
	if not child.init_all():
		return "SystemManager could not initialize child systems."
	child.tick(0.1)
	var expected := "init:root,init:input,init:present,tick:input,tick:present"
	if ",".join(_trace) != expected:
		return "SystemManager dependency order is incorrect: %s" % _trace
	var snapshots: Array[Dictionary] = child.snapshots()
	if not snapshots.any(func(item: Dictionary) -> bool: return str(item.get("scope", "")).begins_with("parent/")):
		return "SystemManager did not expose parent diagnostics."
	child.shutdown_all()
	parent.shutdown_all()
	return ""


func _verify_asset() -> String:
	var asset = FAssetScript.new()
	var path := "res://fw/scripts/fw/rt/_asset.gd"
	if not asset.exists(path):
		return "FAsset did not find a framework script resource."
	var first := asset.load(path, Script)
	var second := asset.load(path, Script)
	if first == null or first != second:
		return "FAsset cache did not return the same resource instance."
	if int(asset.stats().get("cached_count", 0)) != 1:
		return "FAsset reported incorrect cache statistics."
	asset.unload()
	var handle = asset.acquire("res:fw/scripts/fw/rt/_asset.gd", Script)
	if handle == null or handle.asset == null:
		return "FAsset could not acquire a normalized res: key."
	if int(asset.stats().references.get(path, 0)) != 1:
		return "FAsset did not track an acquired reference."
	handle.release()
	if asset.has_cached(path):
		return "FAsset retained an unpinned resource after its last release."
	var async_handle = await asset.acquire_async(path, Script)
	if async_handle == null or async_handle.asset == null:
		return "FAsset threaded acquire failed."
	async_handle.release()
	var provider := TestAssetProvider.new()
	if not asset.register_provider(&"test", provider):
		return "FAsset rejected a valid provider."
	var provider_handle = asset.acquire("test:item", Resource)
	if provider_handle == null or provider.load_count != 1:
		return "FAsset provider did not load the requested resource."
	var replacement_provider := TestAssetProvider.new()
	if asset.register_provider(&"test", replacement_provider):
		return "FAsset replaced a provider while an acquired handle was active."
	if asset.unregister_provider(&"test"):
		return "FAsset unregistered a provider while an acquired handle was active."
	provider_handle.release()
	if provider.release_count != 1:
		return "FAsset provider release was not paired with the final handle."
	if not asset.register_provider(&"test", replacement_provider):
		return "FAsset could not replace an idle provider."
	var retained := asset.load("test:retained", Resource)
	if retained == null or not asset.unregister_provider(&"test", false):
		return "FAsset could not retain a pinned cache while unregistering its provider."
	asset.unload("test:retained")
	if replacement_provider.release_count != 1:
		return "FAsset lost the provider needed to release a retained cache entry."

	var async_provider := AsyncAssetProvider.new()
	if not asset.register_provider(&"async", async_provider):
		return "FAsset rejected an async provider."
	var async_handles: Array = []
	var first_consumer := AsyncAssetConsumer.new()
	var second_consumer := AsyncAssetConsumer.new()
	first_consumer.finished.connect(func(value: Variant) -> void: async_handles.append(value))
	second_consumer.finished.connect(func(value: Variant) -> void: async_handles.append(value))
	first_consumer.call_deferred("run", asset, "async:item")
	second_consumer.call_deferred("run", asset, "async:item")
	var frames := 0
	while async_handles.size() < 2 and frames < 8:
		await process_frame
		frames += 1
	if async_handles.size() != 2 or async_provider.load_count != 1:
		return "FAsset did not coalesce concurrent async provider requests."
	if async_handles[0] == null or async_handles[1] == null:
		return "FAsset async coalescing returned an empty handle."
	if async_handles[0].asset != async_handles[1].asset:
		return "FAsset async coalescing returned different resources."
	async_handles[0].release()
	async_handles[1].release()
	if async_provider.release_count != 1:
		return "FAsset async coalescing did not pair provider release with the last handle."
	var ephemeral_provider := EphemeralAssetProvider.new()
	if not asset.register_provider(&"ephemeral", ephemeral_provider):
		return "FAsset rejected a valid Node provider."
	ephemeral_provider.free()
	if asset.exists("ephemeral:item"):
		return "FAsset treated a freed Node provider as live."
	if not asset.unregister_provider(&"ephemeral"):
		return "FAsset could not unregister a freed Node provider."
	asset.unload()
	return ""


func _verify_pool() -> String:
	var template := Node.new()
	var scene := PackedScene.new()
	if scene.pack(template) != OK:
		template.free()
		return "Could not build the FPool verification scene."
	template.free()
	var host := Node.new()
	root.add_child(host)
	var pool = FPoolScript.new()
	pool.setup(host)
	if not pool.register_prefab("node", scene, 3, 1):
		return "FPool could not register a prefab."
	if int(pool.stats("node").get("free", -1)) != 1:
		return "FPool warmup exceeded max_free."
	var first := pool.spawn("node")
	if first == null or not pool.owns(first):
		return "FPool did not track a spawned node."
	if not pool.recycle(first):
		return "FPool could not recycle an owned node."
	if pool.recycle(first):
		return "FPool accepted the same node twice."
	var second := pool.spawn("node")
	if second != first:
		return "FPool did not reuse its free node."
	pool.recycle(second)
	var stats: Dictionary = pool.stats("node")
	if int(stats.get("active", -1)) != 0 or int(stats.get("free", -1)) != 1:
		return "FPool reported incorrect active/free counts."
	var active := pool.spawn("node")
	if active == null:
		return "FPool could not spawn an instance before full flush."
	pool.flush()
	var flushed_stats: Dictionary = pool.stats()
	if int(flushed_stats.get("registered", -1)) != 0 or int(flushed_stats.get("active", -1)) != 0:
		return "FPool full flush retained registrations or active instances."
	host.queue_free()
	return ""


func _verify_log() -> String:
	var log = FLogScript.new()
	log.output_enabled = false
	log.history_limit = 2
	log.minimum_level = FLogScript.Level.INFO
	if not log.debug(&"test", "hidden").is_empty():
		return "FLog ignored its minimum level."
	log.info(&"test", "one")
	log.warning(&"test", "two")
	log.error(&"test", "three")
	var entries: Array[Dictionary] = log.recent_entries()
	if entries.size() != 2 or entries[0].message != "two" or entries[1].message != "three":
		return "FLog history capacity or order is incorrect."
	return ""


func _verify_audio_and_display() -> String:
	var host := Node.new()
	root.add_child(host)
	var audio = FAudioScript.new()
	audio.setup(host)
	var stream := AudioStreamWAV.new()
	if not audio.register_sfx(&"test", stream):
		return "FAudio could not register an AudioStream."
	if not audio.has_stream(&"test") or audio.registered_ids() != [&"test"]:
		return "FAudio registry state is incorrect."
	if not audio.register_bgm_intro_loop(&"bgm", AudioStreamWAV.new(), AudioStreamWAV.new()):
		return "FAudio could not register an intro-loop BGM."
	var bgm_config: Dictionary = audio.stream_config(&"bgm")
	if bgm_config.get("loop_stream", null) == null:
		return "FAudio did not retain the BGM loop stream."
	audio.clear()
	host.queue_free()

	var display = FDisplayScript.new()
	display.setup(
		root as Window,
		[Vector2i(1920, 1080), Vector2i(1280, 720), Vector2i(1280, 720)]
	)
	var options: Array[Vector2i] = display.size_options()
	if options.count(Vector2i(1280, 720)) != 1 or options.count(Vector2i(1920, 1080)) != 1:
		return "FDisplay did not normalize size options."
	display.set_pending_size(Vector2i(1280, 720))
	if display.pending_size_text() != "1280 x 720" or not display.has_next_size():
		return "FDisplay did not expose its pending size state."
	display.set_pending_fullscreen(not bool(display.pending_settings().get("fullscreen", false)))
	if not display.has_changes():
		return "FDisplay did not detect pending changes."
	display.cancel()
	if display.has_changes():
		return "FDisplay did not cancel pending changes."
	return ""


func _verify_debug() -> String:
	var runtime = FDebugScript.new()
	runtime.setup(false, true)
	if runtime.is_enabled() or not runtime.toggle() or not runtime.is_enabled():
		return "FDebug toggle state is incorrect."
	if not runtime.set_enabled(false) or runtime.is_enabled():
		return "FDebug could not disable its runtime state."
	return ""


func _record_low_event(payload: Variant) -> void:
	_trace.append("low:%s" % payload)


func _record_high_event(payload: Variant) -> void:
	_trace.append("high:%s" % payload)


func _state_enter(context: Array[String], payload: Variant, state: String) -> void:
	context.append("enter:%s:%s" % [state, payload])


func _state_tick(context: Array[String], _dt: float, state: String) -> void:
	context.append("tick:" + state)


func _state_exit(context: Array[String], state: String) -> void:
	context.append("exit:" + state)


func _state_transition(
	context: Array[String],
	from: Variant,
	to: Variant,
	_payload: Variant
) -> void:
	context.append("transition:%s:%s" % [from, to])


func _state_guard(
	_context: Array[String],
	_from: Variant,
	_to: Variant,
	payload: Variant
) -> bool:
	return payload == "go"


func _queue_transition_on_exit(_context: Variant, machine: Variant) -> void:
	machine.transition_to(&"run")
