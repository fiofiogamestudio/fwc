extends SceneTree

const BindingScript = preload("res://fw/scripts/fw/vu/_binding.gd")
const PoolScript = preload("res://fw/scripts/fw/rt/pool/_pool.gd")
const AssetScript = preload("res://fw/scripts/fw/rt/_asset.gd")
const GMServiceTest = preload("res://fw/tests/gm_service_test.gd")
const LocalizationScript = preload("res://fw/scripts/fw/rt/localization/_localization.gd")
const LocalizationCatalogScript = preload("res://fw/scripts/fw/rt/localization/_localization_catalog.gd")
const AppRootScript = preload("res://fw/scripts/fw/rt/system/_app_root.gd")
const BaseModeScript = preload("res://fw/scripts/fw/rt/system/_base_mode.gd")
const BaseSystemScript = preload("res://fw/scripts/fw/rt/system/_base_system.gd")
const SystemManagerScript = preload("res://fw/scripts/fw/rt/system/_system_manager.gd")
const ViewStoreScript = preload("res://fw/scripts/fw/vu/_view_store.gd")
const ViewRootScript = preload("res://fw/scripts/fw/vu/_view_root.gd")
const FFxScript = preload("res://fw/scripts/fw/vu/fx/_fx.gd")
const FFormScript = preload("res://fw/scripts/fw/vu/ui/form/_form.gd")
const FFormLogicScript = preload("res://fw/scripts/fw/vu/ui/form/_form_logic.gd")
const FFormsScript = preload("res://fw/scripts/fw/vu/ui/form/_forms.gd")
const FUIScript = preload("res://fw/scripts/fw/vu/ui/_ui.gd")
const FWidgetScript = preload("res://fw/scripts/fw/vu/ui/widget/_widget.gd")
const ProceduralPoseScript = preload("res://fw/scripts/fw/vu/animation/_procedural_pose.gd")
const ProceduralHumanoidModifierScript = preload("res://fw/scripts/fw/vu/animation/_procedural_humanoid_modifier.gd")
const ProceduralRigAdapterScript = preload(
	"res://fw/scripts/fw/vu/animation/_procedural_rig_adapter.gd"
)
const ProceduralHumanoidRigAdapterScript = preload(
	"res://fw/scripts/fw/vu/animation/_procedural_humanoid_rig_adapter.gd"
)
const SegmentedHumanoidRigAdapterScript = preload(
	"res://fw/scripts/fw/vu/animation/_segmented_humanoid_rig_adapter.gd"
)
const BodyOrientationScript = preload("res://fw/scripts/fw/vu/animation/_body_orientation.gd")
const MotionMatcherScript = preload("res://fw/scripts/fw/vu/animation/_motion_matcher.gd")
const SkeletonAppearanceModifierScript = preload(
	"res://fw/scripts/fw/vu/animation/_skeleton_appearance_modifier.gd"
)

const API_SNAPSHOT_PATH := "res://fw/tests/contracts/godot_api.json"
const FW_SCRIPT_ROOT := "res://fw/scripts/fw"

var _failures: Array[String] = []


class SignalProbe:
	extends RefCounted

	signal changed(value: int)


class NodeSignalProbe:
	extends Node

	signal changed(value: int)


class LocaleStoreProbe:
	extends RefCounted

	var loaded_locale := "EN"
	var saved_locale := ""

	func load_locale(_fallback: String) -> String:
		return loaded_locale

	func save_locale(locale: String) -> void:
		saved_locale = locale


class SystemProbe:
	extends RefCounted

	var calls: Array[String]
	var id: String
	var succeeds: bool

	func _init(call_log: Array[String], system_id: String, init_succeeds: bool) -> void:
		calls = call_log
		id = system_id
		succeeds = init_succeeds

	func init(_context: Variant) -> bool:
		calls.append("init:%s" % id)
		return succeeds

	func tick(_dt: float) -> void:
		calls.append("tick:%s" % id)

	func shutdown() -> void:
		calls.append("shutdown:%s" % id)


class RuntimeCallbackProbe:
	extends RefCounted

	var on_init: Callable
	var on_tick: Callable
	var on_shutdown: Callable

	func init(_context: Variant) -> bool:
		if on_init.is_valid():
			on_init.call()
		return true

	func tick(_dt: float) -> void:
		if on_tick.is_valid():
			on_tick.call()

	func shutdown() -> void:
		if on_shutdown.is_valid():
			on_shutdown.call()


class ModeProbe:
	extends RefCounted

	var calls: Array[String]
	var succeeds: bool

	func _init(call_log: Array[String], enter_succeeds: bool) -> void:
		calls = call_log
		succeeds = enter_succeeds

	func enter(app: Variant, _context: Variant) -> bool:
		calls.append("enter")
		var child: Node = Node.new()
		child.name = "PartialMode"
		app.mode_host().add_child(child)
		return succeeds

	func tick(_dt: float) -> void:
		calls.append("tick")

	func exit() -> void:
		calls.append("exit")


func _init() -> void:
	call_deferred("_run")


func _run() -> void:
	var gm_error: String = GMServiceTest.new().run()
	_check(gm_error.is_empty(), gm_error)
	_test_public_api()
	_test_binding()
	_test_localization()
	_test_pool()
	_test_view_store()
	_test_procedural_animation()
	_test_skeleton_appearance()
	_test_ui_stack()
	_test_system_rollback()
	_test_system_reentrancy()
	_test_mode_rollback()
	if _failures.is_empty():
		print("fw Godot runtime tests passed.")
		quit(0)
		return
	for failure in _failures:
		printerr("[runtime-test] %s" % failure)
	quit(1)


func _test_public_api() -> void:
	_test_exact_public_api()
	_check_methods(AssetScript.new(), [&"load", &"unload"], "FAsset")
	_check_methods(LocalizationScript.new(), [&"setup", &"translate", &"resolve_asset", &"bind_property"], "FLocalization")
	_check_methods(LocalizationCatalogScript.new(), [&"load_catalog", &"add_message", &"add_asset"], "FLocalizationCatalog")
	_check_methods(PoolScript.new(), [&"setup", &"register_prefab", &"spawn", &"recycle", &"flush"], "FPool")
	_check_methods(BaseModeScript.new(), [&"enter", &"tick", &"exit", &"add_system", &"init_systems"], "BaseMode")
	_check_methods(BaseSystemScript.new(), [&"init", &"tick", &"shutdown"], "BaseSystem")
	_check_methods(SystemManagerScript.new(), [&"set_phase_order", &"add_system", &"init_all", &"tick", &"shutdown_all"], "SystemManager")
	_check_methods(FUIScript.new(), [&"setup", &"clear", &"open", &"close", &"close_all"], "FUI")
	_check_methods(ViewRootScript.new(), [&"setup", &"clear", &"apply"], "FViewRoot")
	_check_methods(FFormScript.new(), [&"setup", &"clear", &"apply"], "FForm")
	_check_methods(FWidgetScript.new(), [&"setup", &"clear", &"apply"], "FWidget")
	_check_methods(
		ProceduralPoseScript.new(),
		[&"ease", &"group_tracks", &"sample_track", &"sample_transform", &"validate_keys"],
		"FProceduralPose"
	)
	_check_methods(
		ProceduralHumanoidModifierScript.new(),
		[&"configure", &"submit_pose", &"fit_hand_targets", &"clear_pose", &"solve_now", &"is_configured", &"errors", &"metrics"],
		"FProceduralHumanoidModifier"
	)
	_check_methods(
		ProceduralRigAdapterScript.new(),
		[&"setup", &"submit_pose", &"clear_pose", &"solve_now", &"is_ready", &"semantic_transform", &"fit_hand_targets", &"metrics"],
		"FProceduralRigAdapter"
	)
	_check_methods(
		ProceduralHumanoidRigAdapterScript.new(),
		[&"setup", &"submit_pose", &"clear_pose", &"solve_now", &"is_ready", &"semantic_transform", &"fit_hand_targets", &"metrics"],
		"FProceduralHumanoidRigAdapter"
	)
	_check_methods(
		SegmentedHumanoidRigAdapterScript.new(),
		[&"setup", &"submit_pose", &"clear_pose", &"solve_now", &"is_ready", &"semantic_transform", &"fit_hand_targets", &"metrics"],
		"FSegmentedHumanoidRigAdapter"
	)
	_check_methods(
		SkeletonAppearanceModifierScript.new(),
		[&"configure", &"submit_adjustments", &"clear_adjustments", &"solve_now", &"is_configured", &"errors", &"metrics"],
		"FSkeletonAppearanceModifier"
	)
	var fx: Variant = FFxScript.new()
	_check(fx.has_signal(&"finished"), "FFx must keep the finished signal")
	_check_methods(fx, [&"setup", &"clear", &"play"], "FFx")


func _test_exact_public_api() -> void:
	var actual: Dictionary = _public_api_snapshot()
	if OS.get_environment("FW_UPDATE_API") == "1":
		var output: FileAccess = FileAccess.open(API_SNAPSHOT_PATH, FileAccess.WRITE)
		_check(output != null, "Godot API snapshot must be writable")
		if output != null:
			output.store_string(JSON.stringify(actual, "\t") + "\n")
			output.close()
	var file: FileAccess = FileAccess.open(API_SNAPSHOT_PATH, FileAccess.READ)
	_check(file != null, "Godot API snapshot must exist")
	if file == null:
		return
	var expected_raw: Variant = JSON.parse_string(file.get_as_text())
	_check(expected_raw is Dictionary, "Godot API snapshot must be valid JSON")
	if not (expected_raw is Dictionary):
		return
	var expected: Dictionary = expected_raw
	if actual != expected:
		printerr("[runtime-test] Godot public API changed. Review the API change, then update %s:\n%s" % [
			API_SNAPSHOT_PATH,
			JSON.stringify(actual, "\t"),
		])
		_failures.append("Godot public API snapshot mismatch")


func _public_api_snapshot() -> Dictionary:
	var scripts: Dictionary = {}
	_collect_public_scripts(FW_SCRIPT_ROOT, scripts)
	var snapshot: Dictionary = {}
	var labels: Array = scripts.keys()
	labels.sort()
	for label: String in labels:
		var script: Script = scripts[label]
		snapshot[label] = _script_api(script)
	return snapshot


func _collect_public_scripts(path: String, scripts: Dictionary) -> void:
	var directory: DirAccess = DirAccess.open(path)
	_check(directory != null, "framework script directory must exist: %s" % path)
	if directory == null:
		return
	directory.list_dir_begin()
	var entry: String = directory.get_next()
	while not entry.is_empty():
		if not entry.begins_with("."):
			var child_path: String = path.path_join(entry)
			if directory.current_is_dir():
				_collect_public_scripts(child_path, scripts)
			elif entry.ends_with(".gd"):
				var resource: Resource = load(child_path)
				if resource is Script:
					var script: Script = resource
					var global_name: String = str(script.get_global_name())
					if not global_name.is_empty():
						_check(not scripts.has(global_name), "duplicate framework class_name: %s" % global_name)
						scripts[global_name] = script
		entry = directory.get_next()
	directory.list_dir_end()


func _script_api(script: Script) -> Dictionary:
	var api: Dictionary = _all_script_api(script)
	var base_script: Script = script.get_base_script()
	api["base"] = _script_base_name(script, base_script)
	if base_script == null:
		return api
	var inherited: Dictionary = _all_script_api(base_script)
	for section: String in ["methods", "signals", "properties", "constants"]:
		api[section] = _without(api[section], inherited[section])
	return api


func _script_base_name(script: Script, base_script: Script) -> String:
	if base_script != null:
		var global_name: String = str(base_script.get_global_name())
		return global_name if not global_name.is_empty() else str(base_script.resource_path)
	return str(script.get_instance_base_type())


func _all_script_api(script: Script) -> Dictionary:
	var methods: Array[String] = []
	for raw_method: Dictionary in script.get_script_method_list():
		var method_name: String = str(raw_method.get("name", ""))
		if method_name.begins_with("_"):
			continue
		var args: Array[String] = []
		for raw_arg: Dictionary in raw_method.get("args", []):
			args.append("%s:%s" % [str(raw_arg.get("name", "")), _api_type(raw_arg)])
		var returns: Dictionary = raw_method.get("return", {})
		var defaults: Array = raw_method.get("default_args", [])
		var default_values: Array[String] = []
		for value: Variant in defaults:
			default_values.append(var_to_str(value))
		methods.append("%s(%s)->%s;defaults=[%s]" % [
			method_name,
			", ".join(args),
			_api_type(returns),
			", ".join(default_values),
		])
	methods = _unique_sorted(methods)

	var signals: Array[String] = []
	for raw_signal: Dictionary in script.get_script_signal_list():
		var args: Array[String] = []
		for raw_arg: Dictionary in raw_signal.get("args", []):
			args.append("%s:%s" % [str(raw_arg.get("name", "")), _api_type(raw_arg)])
		signals.append("%s(%s)" % [str(raw_signal.get("name", "")), ", ".join(args)])
	signals = _unique_sorted(signals)

	var properties: Array[String] = []
	for raw_property: Dictionary in script.get_script_property_list():
		var property_name: String = str(raw_property.get("name", ""))
		if not property_name.begins_with("_"):
			properties.append("%s:%s" % [property_name, _api_type(raw_property)])
	properties = _unique_sorted(properties)

	var constants: Array[String] = []
	var constant_map: Dictionary = script.get_script_constant_map()
	for constant_name: String in constant_map:
		var value: Variant = constant_map[constant_name]
		if not constant_name.begins_with("_") and not (value is Script):
			constants.append("%s:%s=%s" % [constant_name, type_string(typeof(value)), var_to_str(value)])
	constants = _unique_sorted(constants)
	return {
		"methods": methods,
		"signals": signals,
		"properties": properties,
		"constants": constants,
	}


func _unique_sorted(values: Array[String]) -> Array[String]:
	var seen: Dictionary = {}
	for value: String in values:
		seen[value] = true
	var result: Array[String] = []
	for value: String in seen:
		result.append(value)
	result.sort()
	return result


func _without(values: Array, inherited: Array) -> Array[String]:
	var inherited_set: Dictionary = {}
	for value: String in inherited:
		inherited_set[value] = true
	var result: Array[String] = []
	for value: String in values:
		if not inherited_set.has(value):
			result.append(value)
	return result


func _api_type(info: Dictionary) -> String:
	var script_class: String = str(info.get("class_name", ""))
	if not script_class.is_empty():
		return script_class
	var type_id: int = int(info.get("type", TYPE_NIL))
	var hint: String = str(info.get("hint_string", ""))
	return type_string(type_id) if hint.is_empty() else "%s[%s]" % [type_string(type_id), hint]


func _check_methods(instance: Variant, methods: Array[StringName], label: String) -> void:
	for method in methods:
		_check(instance.has_method(method), "%s must keep method %s" % [label, method])
	if instance is Node:
		instance.free()


func _test_binding() -> void:
	var emitter: SignalProbe = SignalProbe.new()
	var binding: Variant = BindingScript.new()
	var state: Dictionary = {"calls": 0}
	var callback: Callable = func(_value: int) -> void:
		state["calls"] = int(state["calls"]) + 1
	binding.bind_signal(emitter, &"changed", callback)
	binding.bind_signal(emitter, &"changed", callback)
	emitter.changed.emit(1)
	_check(int(state["calls"]) == 1, "binding must reject duplicate connections")
	binding.unbind()
	emitter.changed.emit(2)
	_check(int(state["calls"]) == 1, "binding must disconnect owned connections")

	var external_emitter: SignalProbe = SignalProbe.new()
	var external_state: Dictionary = {"calls": 0}
	var external_callback: Callable = func(_value: int) -> void:
		external_state["calls"] = int(external_state["calls"]) + 1
	external_emitter.changed.connect(external_callback)
	var observer: Variant = BindingScript.new()
	observer.bind_signal(external_emitter, &"changed", external_callback)
	observer.unbind()
	external_emitter.changed.emit(3)
	_check(int(external_state["calls"]) == 1, "binding must not disconnect an external connection")
	external_emitter.changed.disconnect(external_callback)

	var freed_emitter: NodeSignalProbe = NodeSignalProbe.new()
	root.add_child(freed_emitter)
	var freed_binding: Variant = BindingScript.new()
	freed_binding.bind_signal(freed_emitter, &"changed", callback)
	freed_emitter.free()
	freed_binding.unbind()


func _test_localization() -> void:
	var base_catalog: Variant = LocalizationCatalogScript.new().setup("game")
	var catalog_issues: Array[String] = base_catalog.load_catalog({
		"schema_version": 1,
		"namespace": "game",
		"messages": {
			"welcome": {"CN": "Welcome {name}", "EN": "Hello {name}"},
			"items": {"CN": "{count} items", "EN": "{count, plural, =0 {No items} one {# item} other {# items}}"},
			"role": {"CN": "{role}", "EN": "{role, select, admin {Administrator} other {Player}}"},
			"escaped": {"CN": "{{name}} {name}", "EN": "{{name}} {name}"},
		},
		"assets": {
			"logo": {"CN": "res://logo-cn.png", "EN": "res://logo-en.png"},
		},
		"aliases": {"hello": "welcome"},
	})
	_check(catalog_issues.is_empty(), "localization catalog fixture must validate: " + str(catalog_issues))

	var override_catalog: Variant = LocalizationCatalogScript.new().setup("game")
	override_catalog.add_message("welcome", "CN", "Override {name}")
	var locale_store := LocaleStoreProbe.new()
	var localization: Variant = LocalizationScript.new()
	_check(
		localization.setup("CN", ["CN", "EN", LocalizationScript.PSEUDO_LOCALE], "", {"EN": ["CN"]}, locale_store),
		"localization setup must accept a valid locale configuration"
	)
	_check(localization.current_locale() == "EN", "localization must load its initial locale from the store")
	_check(localization.register_provider(&"base", base_catalog, 0), "localization must register a base provider")
	_check(localization.register_provider(&"override", override_catalog, 100), "localization must register an override provider")
	_check(localization.translate("game.welcome", {"name": "Ada"}) == "Hello Ada", "same-locale base text must win before a higher-priority fallback locale")
	_check(localization.translate("game.hello", {"name": "Ada"}) == "Hello Ada", "catalog aliases must resolve")
	_check(localization.translate("game.items", {"count": 0}) == "No items", "plural exact branch must resolve")
	_check(localization.translate("game.items", {"count": 1}) == "1 item", "plural one branch must resolve")
	_check(localization.translate("game.items", {"count": 3}) == "3 items", "plural other branch must resolve")
	_check(localization.translate("game.role", {"role": "admin"}) == "Administrator", "select branch must resolve")
	_check(localization.translate("game.escaped", {"name": "Ada"}) == "{name} Ada", "escaped braces must remain literal")
	_check(localization.translate_message({"id": "game.welcome", "args": {"name": "Lin"}}) == "Hello Lin", "localized message payload must resolve")
	_check(localization.resolve_asset("game.logo") == "res://logo-en.png", "localized assets must resolve")

	var label := Label.new()
	root.add_child(label)
	var binding_id: int = localization.bind_property(label, &"text", "game.welcome", {"name": "Mira"})
	_check(binding_id > 0 and label.text == "Hello Mira", "localization binding must apply immediately")
	_check(localization.set_locale("CN"), "localization must switch to a supported locale")
	_check(label.text == "Override Mira", "localization binding must refresh after locale changes")
	_check(locale_store.saved_locale == "CN", "localization must persist locale changes")
	_check(localization.update_binding(binding_id, {"name": "Tao"}), "localization binding args must update")
	_check(label.text == "Override Tao", "updated localization binding args must render")
	_check(localization.unbind(binding_id), "localization binding must be removable")
	label.free()

	_check(localization.set_locale(LocalizationScript.PSEUDO_LOCALE, false), "pseudo locale must be selectable")
	var pseudo_text: String = localization.translate("game.welcome", {"name": "QA"})
	_check(pseudo_text.contains("!!") and pseudo_text.contains("QA"), "pseudo locale must expand resolved default text without corrupting args")
	localization.set_locale("EN", false)
	_check(localization.translate("game.missing", {}, "Fallback") == "Fallback", "missing messages must use their explicit fallback")
	_check(not (localization.diagnostics().get("missing", {}) as Dictionary).is_empty(), "missing messages must be observable in diagnostics")

	var invalid_catalog: Variant = LocalizationCatalogScript.new().setup("invalid")
	invalid_catalog.add_message("placeholder", "CN", "{count}")
	invalid_catalog.add_message("placeholder", "EN", "{value}")
	_check(not invalid_catalog.validate().is_empty(), "catalog validation must reject placeholder drift")


func _test_pool() -> void:
	var pool: Variant = PoolScript.new()
	var packed: PackedScene = PackedScene.new()
	var source: Node = Node.new()
	_check(packed.pack(source) == OK, "pool fixture must pack")
	source.free()
	pool.register_prefab("node", packed, 1)
	var first: Node = pool.spawn("node")
	_check(first != null, "pool must spawn without a default parent")
	pool.recycle(first)
	pool.recycle(first)
	var second: Node = pool.spawn("node")
	_check(second == first, "pool must reuse a recycled node")
	var third: Node = pool.spawn("node")
	_check(third != first, "pool must not return one recycled node twice")
	pool.recycle(second)
	pool.recycle(third)
	var externally_freed: Node = pool.spawn("node")
	externally_freed.free()
	pool.flush()


func _test_view_store() -> void:
	var owner: Node = Node.new()
	root.add_child(owner)
	var first: Node = Node.new()
	first.name = "Target"
	owner.add_child(first)
	var store: Variant = ViewStoreScript.new()
	store.setup(owner, {&"target": ^"Target"}, {})
	_check(store.require_node(^"Target") == first, "view store must resolve its initial node")
	_check(store.require_ref(&"target") == first, "ref store must resolve its initial node")
	owner.remove_child(first)
	first.free()
	var replacement: Node = Node.new()
	replacement.name = "Target"
	owner.add_child(replacement)
	_check(store.require_node(^"Target") == replacement, "view store must replace an invalid cached node")
	_check(store.require_ref(&"target") == replacement, "ref store must replace an invalid cached node")
	store.clear()
	owner.free()


func _test_motion_matcher() -> void:
	var matcher: Variant = MotionMatcherScript.new()
	var forward_trajectory := PackedVector2Array([
		Vector2(0.0, 0.2),
		Vector2(0.0, 0.5),
		Vector2(0.0, 0.8),
	])
	var left_trajectory := PackedVector2Array([
		Vector2(-0.2, 0.0),
		Vector2(-0.5, 0.0),
		Vector2(-0.8, 0.0),
	])
	var candidates: Array[Dictionary] = [
		{
			"id": "walk_forward@0",
			"clip": "Walking_A",
			"gait": "walk",
			"time": 0.0,
			"duration": 1.0,
			"phase": 0.0,
			"velocity": Vector2(0.0, 1.0),
			"trajectory": forward_trajectory,
			"pose": PackedFloat32Array([0.0, 1.0, -1.0, 0.0]),
			"pose_velocity": PackedFloat32Array([1.0, 0.0, 0.0, -1.0]),
			"contacts": 1,
		},
		{
			"id": "walk_forward@1_reverse",
			"clip": "Walking_A",
			"gait": "walk",
			"time": 0.5,
			"duration": 1.0,
			"phase": 0.5,
			"velocity": Vector2(0.0, 1.0),
			"trajectory": forward_trajectory,
			"pose": PackedFloat32Array([1.0, 0.0, 0.0, -1.0]),
			"pose_velocity": PackedFloat32Array([1.0, 0.0, 0.0, -1.0]),
			"contacts": 2,
		},
		{
			"id": "walk_forward@1",
			"clip": "Walking_A",
			"gait": "walk",
			"time": 0.5,
			"duration": 1.0,
			"phase": 0.5,
			"velocity": Vector2(0.0, 1.0),
			"trajectory": forward_trajectory,
			"pose": PackedFloat32Array([1.0, 0.0, 0.0, -1.0]),
			"pose_velocity": PackedFloat32Array([-1.0, 0.0, 0.0, 1.0]),
			"contacts": 2,
		},
		{
			"id": "run_left@0",
			"clip": "Running_Strafe_Left",
			"gait": "sprint",
			"time": 0.0,
			"duration": 0.8,
			"phase": 0.0,
			"velocity": Vector2(-1.0, 0.0),
			"trajectory": left_trajectory,
			"pose": PackedFloat32Array([0.0, 1.0, -1.0, 0.0]),
			"pose_velocity": PackedFloat32Array([0.0, -1.0, 1.0, 0.0]),
			"contacts": 1,
		},
	]
	_check(
		matcher.configure(candidates, {"pose_velocity_weight": 2.0}).is_empty(),
		"motion matcher fixture must configure"
	)
	var phase_match: Dictionary = matcher.match_motion({
		"gait": "walk",
		"velocity": Vector2(0.0, 1.0),
		"trajectory": forward_trajectory,
		"pose": PackedFloat32Array([1.0, 0.0, 0.0, -1.0]),
		"pose_velocity": PackedFloat32Array([-1.0, 0.0, 0.0, 1.0]),
		"phase": 0.5,
		"contacts": 2,
		"current_clip": "Walking_A",
		"current_time": 0.48,
		"delta": 0.02,
	})
	_check(
		str(phase_match.get("id", "")) == "walk_forward@1",
		"motion matching must preserve the closest lower-body pose, velocity, and phase"
	)
	var direction_match: Dictionary = matcher.match_motion({
		"gait": "sprint",
		"velocity": Vector2(-1.0, 0.0),
		"trajectory": left_trajectory,
		"pose": PackedFloat32Array([0.0, 1.0, -1.0, 0.0]),
		"pose_velocity": PackedFloat32Array([0.0, -1.0, 1.0, 0.0]),
		"phase": 0.0,
		"contacts": 1,
		"current_clip": "Walking_A",
		"current_time": 0.5,
		"delta": 1.0 / 60.0,
	}, 1.0 / 60.0)
	_check(
		str(direction_match.get("clip", "")) == "Running_Strafe_Left"
		and bool(direction_match.get("changed", false)),
		"motion matching must switch gait and direction without a hold delay"
	)
	_check(
		float(direction_match.get("blend_seconds", 0.0)) >= 0.1,
		"motion matching must provide a non-zero transition blend"
	)


func _test_procedural_animation() -> void:
	_test_motion_matcher()
	var layered_orientation := BodyOrientationScript.resolve(
		-PI * 0.5,
		-PI * 0.5,
		0.0,
		true,
		true,
		&"upper",
		1.0 / 30.0
	)
	_check(
		absf(float(layered_orientation.action_yaw)) < 0.001,
		"upper-body actions must preserve authoritative aim yaw"
	)
	_check(
		absf(float(layered_orientation.lower_yaw)) > deg_to_rad(70.0),
		"upper-body actions must keep locomotion ownership in the lower body"
	)
	_check(
		absf(float(layered_orientation.upper_twist_yaw)) <= deg_to_rad(80.0) + 0.001,
		"upper-body twist must stay inside the configured hard limit"
	)
	_check(
		absf(float(layered_orientation.upper_twist_yaw)) > deg_to_rad(10.0)
		and absf(float(layered_orientation.upper_twist_yaw)) < deg_to_rad(40.0)
		and is_equal_approx(
			absf(float(layered_orientation.target_upper_twist_yaw)),
			deg_to_rad(80.0)
		),
		"upper-body twist must ease toward its anatomical target instead of snapping"
	)
	var aim_relative_orientation := BodyOrientationScript.resolve(
		-PI * 0.5,
		-PI * 0.5,
		0.0,
		true,
		true,
		&"upper",
		1.0 / 60.0,
		{"locomotion_frame": "aim", "aim_locked": true}
	)
	_check(
		is_zero_approx(float(aim_relative_orientation.lower_yaw))
		and is_zero_approx(float(aim_relative_orientation.action_yaw))
		and is_zero_approx(float(aim_relative_orientation.target_upper_twist_yaw)),
		"aim-relative locomotion must keep the body and action frame on authoritative aim"
	)
	var returning_orientation := BodyOrientationScript.resolve(
		-PI * 0.5,
		-PI * 0.5,
		0.0,
		true,
		false,
		&"upper",
		1.0 / 30.0,
		{"current_upper_twist_yaw": float(layered_orientation.upper_twist_yaw)}
	)
	_check(
		absf(float(returning_orientation.upper_twist_yaw)) > 0.0
		and absf(float(returning_orientation.upper_twist_yaw))
		< absf(float(layered_orientation.upper_twist_yaw)),
		"upper-body twist must ease back after the action ends"
	)
	var wrapped_orientation := BodyOrientationScript.resolve(
		deg_to_rad(179.0),
		deg_to_rad(179.0),
		deg_to_rad(-179.0),
		false,
		true,
		&"upper",
		1.0 / 30.0
	)
	_check(
		absf(rad_to_deg(float(wrapped_orientation.upper_twist_yaw))) < 3.0,
		"body orientation must take the shortest path across the angle wrap"
	)
	var full_body_orientation := BodyOrientationScript.resolve(
		-PI * 0.5,
		-PI * 0.5,
		0.25,
		true,
		true,
		&"full_body",
		1.0 / 30.0
	)
	_check(
		is_equal_approx(float(full_body_orientation.lower_yaw), 0.25)
		and is_zero_approx(float(full_body_orientation.upper_twist_yaw)),
		"full-body actions must align the feet and upper body to aim"
	)
	var committed_orientation := BodyOrientationScript.resolve(
		0.0,
		0.0,
		PI * 0.5,
		true,
		true,
		&"committed",
		1.0 / 30.0,
		{"lower_commit_weight": 0.5}
	)
	_check(
		is_zero_approx(float(committed_orientation.lower_yaw)),
		"committed moving actions must keep velocity ownership in the lower body"
	)
	var stationary_committed_orientation := BodyOrientationScript.resolve(
		0.0,
		0.0,
		PI * 0.5,
		false,
		true,
		&"committed",
		1.0 / 30.0,
		{"lower_commit_weight": 0.5}
	)
	_check(
		float(stationary_committed_orientation.lower_yaw) > 0.0,
		"committed stationary actions must transfer configured aim weight to the pelvis"
	)
	_check(
		BodyOrientationScript.validate_options({
			"soft_twist_degrees": 90.0,
			"hard_twist_degrees": 45.0,
		}).size() == 1,
		"body orientation options must reject an inverted twist range"
	)
	_check(
		BodyOrientationScript.validate_options({
			"locomotion_frame": "unsupported",
		}).size() == 1,
		"body orientation options must reject an unknown locomotion frame"
	)

	var keys: Array = [
		{
			"target": "upper_body",
			"time": 0.0,
			"position": Vector3.ZERO,
			"rotation_degrees": Vector3.ZERO,
			"scale": Vector3.ONE,
			"easing": &"linear",
		},
		{
			"target": "upper_body",
			"time": 1.0,
			"position": Vector3(2.0, 4.0, 6.0),
			"rotation_degrees": Vector3(0.0, 180.0, 0.0),
			"scale": Vector3(2.0, 2.0, 2.0),
			"easing": &"linear",
		},
	]
	_check(ProceduralPoseScript.validate_keys(keys).is_empty(), "procedural pose keys must validate")
	var tracks: Dictionary = ProceduralPoseScript.group_tracks(keys)
	_check(tracks.has("upper_body") and tracks["upper_body"].size() == 2, "procedural pose keys must group by target")
	var sample: Dictionary = ProceduralPoseScript.sample_track(tracks["upper_body"], 0.5)
	_check((sample.get("position", Vector3.ZERO) as Vector3).is_equal_approx(Vector3(1.0, 2.0, 3.0)), "procedural pose must interpolate position")
	_check((sample.get("scale", Vector3.ZERO) as Vector3).is_equal_approx(Vector3(1.5, 1.5, 1.5)), "procedural pose must interpolate scale")
	var sampled_rotation: Quaternion = sample.get("rotation", Quaternion.IDENTITY)
	_check(is_equal_approx(sampled_rotation.get_angle(), PI * 0.5), "procedural pose must slerp large rotations")
	var wrapped_keys: Array = [
		{
			"target": "weapon",
			"time": 0.0,
			"rotation_degrees": Vector3(0.0, 170.0, 0.0),
		},
		{
			"target": "weapon",
			"time": 1.0,
			"rotation_degrees": Vector3(0.0, -170.0, 0.0),
		},
	]
	var wrapped_sample := ProceduralPoseScript.sample_track(wrapped_keys, 0.5)
	var wrapped_forward: Vector3 = Basis(
		wrapped_sample.get("rotation", Quaternion.IDENTITY) as Quaternion
	) * Vector3.FORWARD
	_check(wrapped_forward.z > 0.999, "procedural pose must take the shortest quaternion path")
	var subsamples := ProceduralPoseScript.required_subsamples(
		ProceduralPoseScript.sample_track(wrapped_keys, 0.0),
		ProceduralPoseScript.sample_track(wrapped_keys, 1.0)
	)
	_check(subsamples == 2, "procedural pose must derive adaptive subsamples from quaternion distance")
	var duplicate_keys: Array = [keys[0], keys[0].duplicate(true)]
	_check(not ProceduralPoseScript.validate_keys(duplicate_keys).is_empty(), "procedural pose must reject duplicate target times")

	var rig := Node3D.new()
	rig.name = "ProceduralRigFixture"
	root.add_child(rig)
	var skeleton := Skeleton3D.new()
	skeleton.name = "Skeleton3D"
	rig.add_child(skeleton)
	var hips := _add_test_bone(skeleton, "hips", -1, Transform3D.IDENTITY)
	var spine := _add_test_bone(skeleton, "spine", hips, Transform3D.IDENTITY)
	var chest := _add_test_bone(skeleton, "chest", spine, Transform3D.IDENTITY)
	var upper := _add_test_bone(
		skeleton,
		"upperarm.l",
		chest,
		Transform3D(Basis.IDENTITY, Vector3(0.4, 1.0, 0.0))
	)
	var lower := _add_test_bone(
		skeleton,
		"lowerarm.l",
		upper,
		Transform3D(Basis.IDENTITY, Vector3(0.8, 0.0, 0.0))
	)
	var wrist := _add_test_bone(
		skeleton,
		"wrist.l",
		lower,
		Transform3D(Basis.IDENTITY, Vector3(0.7, 0.0, 0.0))
	)
	var hand := _add_test_bone(
		skeleton,
		"hand.l",
		wrist,
		Transform3D(Basis.IDENTITY, Vector3(0.2, 0.0, 0.0))
	)
	var animated_hand_pose := skeleton.get_bone_pose(hand)
	animated_hand_pose.origin += Vector3(0.07, -0.03, 0.04)
	animated_hand_pose.basis *= Basis.from_euler(Vector3(0.0, 0.0, deg_to_rad(18.0)))
	skeleton.set_bone_pose(hand, animated_hand_pose)
	var modifier: Variant = ProceduralHumanoidModifierScript.new()
	modifier.name = "ProceduralHumanoidModifier"
	skeleton.add_child(modifier)
	var profile := {
		"bones": {
			"hips": "hips",
			"spine": "spine",
			"chest": "chest",
			"left_upper_arm": "upperarm.l",
			"left_lower_arm": "lowerarm.l",
			"left_wrist": "wrist.l",
			"left_hand": "hand.l",
		}
	}
	_check(modifier.configure(profile, rig).is_empty(), "procedural humanoid fixture must configure")
	var base_upper_pose := skeleton.get_bone_pose(upper)
	var base_lower_pose := skeleton.get_bone_pose(lower)
	var base_wrist_pose := skeleton.get_bone_pose(wrist)
	var reachable_hand_target := Transform3D(
		Basis.IDENTITY,
		Vector3(1.65, 1.1, 0.25)
	)
	modifier.submit_pose({
		"left_hand_transform": reachable_hand_target,
		"left_arm_pole": Vector3(0.5, 1.8, 0.8),
		"hips_rotation_degrees": Vector3(0.0, 4.0, 0.0),
		"spine_rotation_degrees": Vector3(0.0, 8.0, 0.0),
		"chest_rotation_degrees": Vector3(0.0, 12.0, 0.0),
	})
	_check(modifier.solve_now(), "procedural humanoid modifier must solve an explicit pose")
	var metrics: Dictionary = modifier.metrics()
	var endpoint_errors: Dictionary = metrics.get("endpoint_errors", {})
	_check(float(endpoint_errors.get("left_hand", INF)) < 0.001, "procedural arm IK must reach its hand target")
	skeleton.force_update_all_bone_transforms()
	var solved_hand := skeleton.get_bone_global_pose(hand)
	_check(
		solved_hand.origin.distance_to(reachable_hand_target.origin) < 0.001
		and solved_hand.basis.orthonormalized().get_rotation_quaternion().angle_to(
			reachable_hand_target.basis.get_rotation_quaternion()
		) < 0.001,
		"procedural arm IK must write the solved terminal hand bone, not only its wrist"
	)
	var upper_rotation := skeleton.get_bone_global_pose(upper).basis.get_rotation_quaternion()
	_check(upper_rotation.angle_to(Quaternion.IDENTITY) > 0.01, "procedural arm IK must rotate the upper arm")
	var first_hips_pose := skeleton.get_bone_pose(hips)
	var first_spine_pose := skeleton.get_bone_pose(spine)
	var first_chest_pose := skeleton.get_bone_pose(chest)
	_check(
		Quaternion(first_hips_pose.basis).angle_to(Quaternion.IDENTITY) > 0.01
		and Quaternion(first_spine_pose.basis).angle_to(Quaternion.IDENTITY) > 0.01
		and Quaternion(first_chest_pose.basis).angle_to(Quaternion.IDENTITY) > 0.01,
		"procedural torso rotation must support hips, spine, and chest independently"
	)
	modifier.submit_pose({
		"hips_rotation_degrees": Vector3(0.0, 4.0, 0.0),
		"spine_rotation_degrees": Vector3(0.0, 8.0, 0.0),
		"chest_rotation_degrees": Vector3(0.0, 12.0, 0.0),
	})
	_check(modifier.solve_now(), "procedural humanoid modifier must solve a repeated pose")
	_check(
		skeleton.get_bone_pose(hips).is_equal_approx(first_hips_pose)
		and skeleton.get_bone_pose(spine).is_equal_approx(first_spine_pose)
		and skeleton.get_bone_pose(chest).is_equal_approx(first_chest_pose),
		"procedural torso poses must not accumulate while the base animation is paused"
	)
	var animated_upper_pose := skeleton.get_bone_pose(upper)
	var animated_upper_basis := Basis.from_euler(Vector3(0.0, 0.0, deg_to_rad(6.0)))
	animated_upper_pose.basis = animated_upper_basis
	skeleton.set_bone_pose(upper, animated_upper_pose)
	modifier.clear_pose()
	_check(
		skeleton.get_bone_pose(hips).is_equal_approx(Transform3D.IDENTITY)
		and skeleton.get_bone_pose(spine).is_equal_approx(Transform3D.IDENTITY)
		and skeleton.get_bone_pose(chest).is_equal_approx(Transform3D.IDENTITY),
		"clearing a procedural pose must restore every unchanged torso base"
	)
	var restored_upper_pose := skeleton.get_bone_pose(upper)
	_check(
		restored_upper_pose.origin.is_equal_approx(base_upper_pose.origin)
		and restored_upper_pose.basis.is_equal_approx(animated_upper_basis)
		and skeleton.get_bone_pose(lower).is_equal_approx(base_lower_pose)
		and skeleton.get_bone_pose(wrist).is_equal_approx(base_wrist_pose),
		"clearing procedural IK must restore unchanged chain channels and preserve animation updates"
	)
	skeleton.set_bone_pose(upper, base_upper_pose)
	skeleton.force_update_all_bone_transforms()
	modifier.submit_pose({
		"chest_position": Vector3(0.1, 0.0, 0.0),
		"chest_rotation_degrees": Vector3(0.0, 12.0, 0.0),
	})
	_check(modifier.solve_now(), "procedural torso must solve a translated test pose")
	var partial_animation_pose := skeleton.get_bone_pose(chest)
	var animation_basis := Basis.from_euler(Vector3(0.0, deg_to_rad(3.0), 0.0))
	partial_animation_pose.basis = animation_basis
	skeleton.set_bone_pose(chest, partial_animation_pose)
	modifier.submit_pose({
		"chest_position": Vector3(0.1, 0.0, 0.0),
		"chest_rotation_degrees": Vector3(0.0, 12.0, 0.0),
	})
	_check(modifier.solve_now(), "procedural torso must merge partial animation channels")
	_check(
		skeleton.get_bone_pose(chest).origin.is_equal_approx(Vector3(0.1, 0.0, 0.0)),
		"procedural torso translation must not accumulate when animation only changes rotation"
	)
	modifier.clear_pose()
	var restored_partial_pose := skeleton.get_bone_pose(chest)
	_check(
		restored_partial_pose.origin.is_zero_approx()
		and restored_partial_pose.basis.is_equal_approx(animation_basis),
		"clearing a procedural pose must preserve independently updated animation channels"
	)
	skeleton.set_bone_pose(chest, Transform3D.IDENTITY)
	skeleton.force_update_all_bone_transforms()
	var unreachable_target := Vector3(4.0, 1.1, 0.25)
	modifier.submit_pose({
		"left_hand_transform": Transform3D(Basis.IDENTITY, unreachable_target),
		"left_arm_pole": Vector3(0.5, 1.8, 0.8),
	})
	_check(modifier.solve_now(), "procedural humanoid modifier must solve an unreachable target safely")
	var solved_lower_origin := skeleton.get_bone_global_pose(lower).origin
	var solved_wrist_origin := skeleton.get_bone_global_pose(wrist).origin
	var solved_chest_origin := skeleton.get_bone_global_pose(chest).origin
	var solved_upper_origin := skeleton.get_bone_global_pose(upper).origin
	var solved_shoulder_segment := solved_chest_origin.distance_to(solved_upper_origin)
	var shoulder_rest_length := Vector3(0.4, 1.0, 0.0).length()
	var solved_lower_segment := solved_lower_origin.distance_to(solved_wrist_origin)
	_check(
		absf(solved_shoulder_segment - shoulder_rest_length) < 0.001,
		"procedural shoulder articulation must preserve the chest-to-shoulder segment length"
	)
	_check(
		absf(solved_lower_segment - 0.7) < 0.001,
		"procedural arm IK must preserve the lower-arm segment length (got %.4f)" % solved_lower_segment
	)
	metrics = modifier.metrics()
	endpoint_errors = metrics.get("endpoint_errors", {})
	var shoulder_offsets: Dictionary = metrics.get("shoulder_offsets", {})
	var maximum_shoulder_offset := (
		2.0 * shoulder_rest_length * sin(deg_to_rad(22.0) * 0.5) + 0.001
	)
	_check(
		float(shoulder_offsets.get("left_shoulder", INF)) <= maximum_shoulder_offset,
		"procedural shoulder articulation must remain bounded (got %.4f, max %.4f)" % [
			float(shoulder_offsets.get("left_shoulder", INF)),
			maximum_shoulder_offset,
		]
	)
	_check(
		float(endpoint_errors.get("left_hand", 0.0)) > 1.0,
		"an unreachable procedural hand target must report its residual error"
	)
	var fit_pose := {
		"left_hand_transform": Transform3D(Basis.IDENTITY, unreachable_target),
		"left_arm_pole": Vector3(0.5, 1.8, 0.8),
	}
	modifier.submit_pose(fit_pose)
	var fit: Dictionary = modifier.fit_hand_targets(
		{"left": fit_pose["left_hand_transform"]},
		fit_pose
	)
	_check(bool(fit.get("feasible", false)), "procedural target fitting must find a reachable rigid translation")
	var fitted_target: Transform3D = fit_pose["left_hand_transform"]
	fitted_target.origin += fit.get("translation", Vector3.ZERO) as Vector3
	modifier.submit_pose({
		"left_hand_transform": fitted_target,
		"left_arm_pole": Vector3(0.5, 1.8, 0.8),
	})
	_check(modifier.solve_now(), "procedural humanoid modifier must solve a fitted target")
	metrics = modifier.metrics()
	endpoint_errors = metrics.get("endpoint_errors", {})
	_check(
		float(endpoint_errors.get("left_hand", INF)) < 0.001,
		"a fitted procedural hand target must solve without endpoint separation"
	)
	modifier.clear_pose()
	var torso_spine_base := Transform3D(Basis.IDENTITY, Vector3(0.0, 0.5, 0.0))
	var torso_chest_base := Transform3D(Basis.IDENTITY, Vector3(0.0, 0.5, 0.0))
	skeleton.set_bone_pose(hips, Transform3D.IDENTITY)
	skeleton.set_bone_pose(spine, torso_spine_base)
	skeleton.set_bone_pose(chest, torso_chest_base)
	skeleton.force_update_all_bone_transforms()
	modifier.submit_pose({
		"torso_reach_offset": Vector3(0.3, 0.0, 0.0),
		"torso_reach_lean_degrees": 30.0,
	})
	_check(modifier.solve_now(), "procedural torso reach must solve a bounded lean")
	var leaned_hips_origin := skeleton.get_bone_global_pose(hips).origin
	var leaned_spine_origin := skeleton.get_bone_global_pose(spine).origin
	var leaned_chest_origin := skeleton.get_bone_global_pose(chest).origin
	_check(
		absf(leaned_hips_origin.distance_to(leaned_spine_origin) - 0.5) < 0.001
		and absf(leaned_spine_origin.distance_to(leaned_chest_origin) - 0.5) < 0.001,
		"procedural torso reach must preserve both torso segment lengths"
	)
	_check(
		leaned_chest_origin.x > 0.1
		and skeleton.get_bone_pose(hips).is_equal_approx(Transform3D.IDENTITY),
		"procedural torso reach must move the upper body without taking lower-body ownership"
	)
	metrics = modifier.metrics()
	var torso_reach_metrics: Dictionary = metrics.get("torso_reach", {})
	_check(
		float(torso_reach_metrics.get("lean_degrees", 0.0)) <= 30.001,
		"procedural torso reach must respect its configured lean limit"
	)
	modifier.clear_pose()
	_check(
		skeleton.get_bone_pose(spine).is_equal_approx(torso_spine_base)
		and skeleton.get_bone_pose(chest).is_equal_approx(torso_chest_base),
		"clearing torso reach must restore the underlying animation pose"
	)
	rig.free()
	_test_segmented_rig_adapter()


func _test_segmented_rig_adapter() -> void:
	var rig := Node3D.new()
	rig.name = "SegmentedRigFixture"
	root.add_child(rig)
	var skeleton := Skeleton3D.new()
	skeleton.name = "Skeleton3D"
	rig.add_child(skeleton)
	var hips := _add_test_bone(
		skeleton, "hips", -1,
		Transform3D(Basis.IDENTITY, Vector3(0.0, 0.82, 0.0))
	)
	var spine := _add_test_bone(
		skeleton, "spine", hips,
		Transform3D(Basis.IDENTITY, Vector3(0.0, 0.22, 0.0))
	)
	var chest := _add_test_bone(
		skeleton, "chest", spine,
		Transform3D(Basis.IDENTITY, Vector3(0.0, 0.24, 0.0))
	)
	_add_test_bone(
		skeleton, "head", chest,
		Transform3D(Basis.IDENTITY, Vector3(0.0, 0.31, 0.0))
	)
	var bones := {
		"hips": "hips", "spine": "spine", "chest": "chest", "head": "head",
	}
	for side: String in ["left", "right"]:
		var suffix := "l" if side == "left" else "r"
		var sign := -1.0 if side == "left" else 1.0
		var upper_arm := _add_test_bone(
			skeleton, "upperarm.%s" % suffix, chest,
			Transform3D(Basis.IDENTITY, Vector3(0.34 * sign, 0.18, 0.0))
		)
		var lower_arm := _add_test_bone(
			skeleton, "lowerarm.%s" % suffix, upper_arm,
			Transform3D(Basis.IDENTITY, Vector3(0.0, -0.34, 0.0))
		)
		_add_test_bone(
			skeleton, "hand.%s" % suffix, lower_arm,
			Transform3D(Basis.IDENTITY, Vector3(0.0, -0.32, 0.0))
		)
		var upper_leg := _add_test_bone(
			skeleton, "upperleg.%s" % suffix, hips,
			Transform3D(Basis.IDENTITY, Vector3(0.16 * sign, -0.08, 0.0))
		)
		var lower_leg := _add_test_bone(
			skeleton, "lowerleg.%s" % suffix, upper_leg,
			Transform3D(Basis.IDENTITY, Vector3(0.0, -0.4, 0.0))
		)
		_add_test_bone(
			skeleton, "foot.%s" % suffix, lower_leg,
			Transform3D(Basis.IDENTITY, Vector3(0.0, -0.38, -0.03))
		)
		bones["%s_upper_arm" % side] = "upperarm.%s" % suffix
		bones["%s_lower_arm" % side] = "lowerarm.%s" % suffix
		bones["%s_hand" % side] = "hand.%s" % suffix
		bones["%s_upper_leg" % side] = "upperleg.%s" % suffix
		bones["%s_lower_leg" % side] = "lowerleg.%s" % suffix
		bones["%s_foot" % side] = "foot.%s" % suffix
	skeleton.force_update_all_bone_transforms()
	var adapter: Variant = SegmentedHumanoidRigAdapterScript.new()
	_check(
		adapter.setup(rig, {
			"pose_family": "topdown_humanoid_v1",
			"skeleton": skeleton,
			"bones": bones,
		}).is_empty(),
		"segmented rig adapter fixture must configure"
	)
	var target := Transform3D(Basis.IDENTITY, Vector3(-0.48, 0.88, -0.12))
	adapter.submit_pose({
		"left_hand_transform": target,
		"locomotion_phase": PI * 0.5,
		"locomotion_speed_ratio": 1.0,
		"spine_rotation_degrees": Vector3(0.0, 8.0, 0.0),
		"chest_rotation_degrees": Vector3(0.0, 12.0, 0.0),
	})
	_check(adapter.solve_now(), "segmented rig adapter must solve a semantic pose")
	var metrics: Dictionary = adapter.metrics()
	var endpoint_errors: Dictionary = metrics.get("endpoint_errors", {})
	var left_hand_error := float(endpoint_errors.get("left_hand", INF))
	_check(
		left_hand_error < 0.001,
		"segmented rigid arm must reach an in-range hand target (error=%.6f)"
		% left_hand_error
	)
	_check(
		int(metrics.get("skinned_mesh_count", -1)) == 0
		and not bool(metrics.get("root_motion_enabled", true)),
		"segmented rig adapter must not rely on skin deformation or root motion"
	)
	var left_upper := skeleton.find_bone(&"upperarm.l")
	var left_lower := skeleton.find_bone(&"lowerarm.l")
	var left_hand := skeleton.find_bone(&"hand.l")
	_check(
		absf(
			skeleton.get_bone_global_pose(left_upper).origin.distance_to(
				skeleton.get_bone_global_pose(left_lower).origin
			) - 0.34
		) < 0.001
		and absf(
			skeleton.get_bone_global_pose(left_lower).origin.distance_to(
				skeleton.get_bone_global_pose(left_hand).origin
			) - 0.32
		) < 0.001,
		"segmented rigid arm must preserve both segment lengths"
	)
	adapter.submit_pose({
		"body_pose_kind": "roll",
		"body_pose_progress": 0.5,
		"locomotion_weight": 1.0,
	})
	_check(adapter.solve_now(), "segmented rig adapter must solve its semantic roll pose")
	var roll_metrics: Dictionary = adapter.metrics()
	var roll_hips: Transform3D = skeleton.get_bone_global_pose(hips)
	var roll_head: Transform3D = skeleton.get_bone_global_pose(skeleton.find_bone(&"head"))
	_check(
		roll_hips.origin.y < 0.6
		and roll_head.origin.y > 0.2
		and str(roll_metrics.get("pose_kind", "")) == "roll"
		and absf(float(roll_metrics.get("pose_progress", 0.0)) - 0.5) < 0.001,
		"segmented roll must lower the center while keeping the head clear of the floor (hips=%.3f head=%.3f kind=%s progress=%.3f)"
		% [
			roll_hips.origin.y,
			roll_head.origin.y,
			str(roll_metrics.get("pose_kind", "")),
			float(roll_metrics.get("pose_progress", 0.0)),
		]
	)
	_check(
		absf(
			skeleton.get_bone_global_pose(left_upper).origin.distance_to(
				skeleton.get_bone_global_pose(left_lower).origin
			) - 0.34
		) < 0.001
		and absf(
			skeleton.get_bone_global_pose(left_lower).origin.distance_to(
				skeleton.get_bone_global_pose(left_hand).origin
			) - 0.32
		) < 0.001,
		"segmented roll must preserve arm segment lengths"
	)
	var unreachable := Transform3D(Basis.IDENTITY, Vector3(-3.0, 1.0, 0.0))
	var fit: Dictionary = adapter.fit_hand_targets({"left": unreachable})
	_check(
		bool(fit.get("feasible", false))
		and (fit.get("translation", Vector3.ZERO) as Vector3).length() > 1.0,
		"segmented rig adapter must return a bounded fitting translation"
	)
	adapter.clear_pose()
	rig.free()


func _test_skeleton_appearance() -> void:
	var skeleton := Skeleton3D.new()
	root.add_child(skeleton)
	var head := _add_test_bone(skeleton, "AvatarB_Cranium", -1, Transform3D.IDENTITY)
	var modifier: Variant = SkeletonAppearanceModifierScript.new()
	modifier.name = "SkeletonAppearanceModifier"
	skeleton.add_child(modifier)
	_check(
		modifier.configure({
			"bones": {"head": "AvatarB_Cranium"},
			"limits": {
				"head": {
					"min_scale": Vector3(0.9, 0.9, 0.9),
					"max_scale": Vector3(1.1, 1.1, 1.1),
				}
			},
		}).is_empty(),
		"skeleton appearance fixture must configure"
	)
	modifier.submit_adjustments({"head": Vector3(1.2, 0.95, 0.92)})
	_check(modifier.solve_now(), "skeleton appearance modifier must solve explicitly")
	_check(
		skeleton.get_bone_pose_scale(head).is_equal_approx(Vector3(1.1, 0.95, 0.92)),
		"skeleton appearance modifier must clamp profile-safe bone scales"
	)
	_check(
		int(modifier.metrics().get("clamp_count", 0)) > 0,
		"skeleton appearance modifier must report clamped adjustments"
	)
	modifier.submit_adjustments({"unknown": Vector3.ONE, "head": "invalid"})
	_check(
		int(modifier.metrics().get("ignored_adjustment_count", 0)) == 2,
		"skeleton appearance modifier must report ignored adjustments"
	)
	modifier.clear_adjustments()
	_check(
		skeleton.get_bone_pose_scale(head).is_equal_approx(Vector3.ONE),
		"clearing skeleton appearance must restore base scales"
	)
	modifier.submit_adjustments({"head": Vector3(1.08, 1.02, 0.98)})
	modifier.solve_now()
	_check(
		modifier.configure({
			"bones": {"head": "AvatarB_Cranium"},
			"limits": {"head": {"min_scale": Vector3(0.9, 0.9, 0.9), "max_scale": Vector3(1.1, 1.1, 1.1)}},
		}).is_empty()
		and skeleton.get_bone_pose_scale(head).is_equal_approx(Vector3.ONE),
		"reconfiguring skeleton appearance must restore the original base scale"
	)
	_check(
		not modifier.configure({
			"bones": {"head": "AvatarB_Cranium"},
			"limits": {"head": {"min_scale": Vector3.ONE, "max_scale": Vector3(0.9, 1.0, 1.0)}},
		}).is_empty()
		and not modifier.is_configured(),
		"skeleton appearance modifier must reject inverted scale limits"
	)
	skeleton.free()


func _test_system_rollback() -> void:
	var calls: Array[String] = []
	var manager: Variant = SystemManagerScript.new()
	manager.set_phase_order([&"first", &"second"])
	_check(manager.add_system(&"first", SystemProbe.new(calls, "first", true), RefCounted.new(), &"first"), "first system must register")
	_check(manager.add_system(&"second", SystemProbe.new(calls, "second", false), RefCounted.new(), &"second"), "second system must register")
	_check(not manager.init_all(), "manager must reject a failed system init")
	_check(
		",".join(calls) == "init:first,init:second,shutdown:second,shutdown:first",
		"manager must roll back attempted systems in reverse order"
	)
	manager.shutdown_all()


func _test_system_reentrancy() -> void:
	var calls: Array[String] = []
	var manager: Variant = SystemManagerScript.new()
	var control := RuntimeCallbackProbe.new()
	control.on_tick = func() -> void:
		calls.append("tick:first")
		manager.shutdown_all()
	control.on_shutdown = func() -> void: calls.append("shutdown:first")
	manager.add_system(&"first", control)
	manager.add_system(&"second", SystemProbe.new(calls, "second", true))
	_check(manager.init_all(), "shutdown tick fixture initializes")
	calls.clear()
	manager.tick(0.1)
	_check(",".join(calls) == "tick:first,shutdown:second,shutdown:first", "shutdown inside tick stops later dispatch")
	_check(manager.lifecycle_state() == SystemManagerScript.LifecycleState.STOPPED, "tick shutdown remains stopped")
	manager.shutdown_all()
	control.on_tick = Callable()

	var cancelled: Variant = SystemManagerScript.new()
	var cancel_probe := RuntimeCallbackProbe.new()
	cancel_probe.on_init = func() -> void: cancelled.shutdown_all()
	cancelled.add_system(&"cancel", cancel_probe)
	_check(not cancelled.init_all(), "shutdown inside init cancels initialization")
	_check(cancelled.lifecycle_state() == SystemManagerScript.LifecycleState.STOPPED, "cancelled init must not resume running")
	_check(cancelled.snapshots().is_empty(), "cancelled init clears registrations")
	cancel_probe.on_init = Callable()

	var nested: Variant = SystemManagerScript.new()
	var recursive := RuntimeCallbackProbe.new()
	var tick_count := [0]
	recursive.on_tick = func() -> void:
		tick_count[0] += 1
		if tick_count[0] == 1:
			nested.tick(0.1)
	nested.add_system(&"recursive", recursive)
	_check(nested.init_all(), "nested tick fixture initializes")
	nested.tick(0.1)
	_check(tick_count[0] == 1, "nested tick cannot recursively dispatch callbacks")
	_check(nested.last_error() == "System manager tick cannot be reentered.", "nested tick reports precise diagnostic")
	nested.tick(0.1)
	_check(tick_count[0] == 2 and nested.is_running(), "subsequent outer tick remains usable")
	nested.shutdown_all()
	recursive.on_tick = Callable()


func _test_mode_rollback() -> void:
	var app: Variant = AppRootScript.new()
	root.add_child(app)
	var calls: Array[String] = []
	var mode: ModeProbe = ModeProbe.new(calls, false)
	_check(app.switch_mode(mode) == null, "app root must reject a failed mode enter")
	_check(",".join(calls) == "enter,exit", "app root must exit a partially entered mode")
	_check(app.mode_host().get_child_count() == 0, "app root must clear partial mode nodes")
	app.free()


func _test_ui_stack() -> void:
	var source: Control = Control.new()
	source.set_script(FFormScript)
	var packed: PackedScene = PackedScene.new()
	_check(packed.pack(source) == OK, "form fixture must pack")
	source.free()

	var host: CanvasLayer = CanvasLayer.new()
	root.add_child(host)
	var ui: Variant = FUIScript.new()
	ui.setup(host)
	var initial_root: Control = ui.root()
	ui.setup(host)
	_check(ui.root() != initial_root and host.get_child_count() == 1, "UI setup must replace its root without leaking")

	var invalid_source: Control = Control.new()
	var invalid_packed: PackedScene = PackedScene.new()
	_check(invalid_packed.pack(invalid_source) == OK, "invalid form fixture must pack")
	invalid_source.free()
	_check(ui.open(FUIScript.LAYER_SCREEN, &"invalid", invalid_packed) == null, "UI must reject non-form scenes")
	_check(not ui.has(&"invalid"), "failed UI open must not leave a registered form")
	_check(ui.open(FUIScript.LAYER_SCREEN, &"", packed) == null, "UI must reject an empty form id")

	var first: Variant = ui.open(FUIScript.LAYER_SCREEN, &"first", packed)
	var second: Variant = ui.open(FUIScript.LAYER_SCREEN, &"second", packed)
	_check(first != null and second != null, "UI must open valid forms")
	_check(not first.visible and second.visible, "screen stack must hide its previous form")
	var third: Variant = ui.open(FUIScript.LAYER_SCREEN, &"third", packed)
	_check(third != null and not second.visible, "screen stack must hide the second form")
	third.free()
	_check(not ui.has(&"third"), "UI must not report an externally freed form as live")
	_check(ui.top_form(FUIScript.LAYER_SCREEN) == second, "UI top lookup must discard an externally freed screen")
	_check(second.visible, "discarding an externally freed screen must reveal the previous form")
	var queued: Variant = ui.open(FUIScript.LAYER_SCREEN, &"queued", packed)
	_check(queued != null and not second.visible, "screen stack must hide the previous form")
	queued.queue_free()
	_check(not ui.has(&"queued"), "UI must not report a queued-for-deletion form as live")
	_check(ui.top_form(FUIScript.LAYER_SCREEN) == second, "UI top lookup must discard a queued screen")
	_check(second.visible, "discarding a queued screen must reveal the previous form")
	ui.close(&"second")
	_check(first.visible, "closing the top screen must reveal the previous form")
	ui.clear()
	host.free()

	var forms_host: CanvasLayer = CanvasLayer.new()
	root.add_child(forms_host)
	var forms: Variant = FFormsScript.new()
	forms.setup(forms_host)
	var forms_root: Control = forms.ui().root()
	forms.setup(forms_host)
	_check(forms.ui().root() != forms_root and forms_host.get_child_count() == 1, "FForms setup must be idempotent")
	var logic: Variant = FFormLogicScript.new()
	logic.attach_ui(forms.ui())
	_check(logic.open(FUIScript.LAYER_SCREEN, &"logic", packed) != null, "form logic must open a form")
	logic.detach_ui()
	_check(not forms.has(&"logic"), "detaching form logic must close its owned form")
	forms.clear()
	_check(forms_host.get_child_count() == 0, "FForms clear must release its UI root")
	forms_host.free()


func _add_test_bone(
	skeleton: Skeleton3D,
	bone_name: String,
	parent: int,
	rest: Transform3D
) -> int:
	skeleton.add_bone(bone_name)
	var index := skeleton.get_bone_count() - 1
	if parent >= 0:
		skeleton.set_bone_parent(index, parent)
	skeleton.set_bone_rest(index, rest)
	return index


func _check(value: bool, message: String) -> void:
	if not value:
		_failures.append(message)
