class_name FLocalization
extends RefCounted

signal locale_changed(locale: String)
signal missing_message(message_id: String, locale: String)
signal format_error(message_id: String, locale: String, detail: String)

const PSEUDO_LOCALE := "qps-ploc"

var _default_locale := "en"
var _locale := "en"
var _supported_locales: Array[String] = []
var _locale_aliases: Dictionary = {}
var _fallbacks: Dictionary = {}
var _providers: Array[Dictionary] = []
var _provider_order := 0
var _locale_store: Variant = null
var _bindings: Dictionary = {}
var _next_binding_id := 1
var _missing: Dictionary = {}
var _format_errors: Dictionary = {}


func setup(
	default_locale: String,
	supported_locales: Array = [],
	initial_locale: String = "",
	fallback_chains: Dictionary = {},
	locale_store: Variant = null
) -> bool:
	var normalized_default := default_locale.strip_edges()
	if normalized_default.is_empty():
		return false
	_default_locale = normalized_default
	_supported_locales.clear()
	for candidate: String in supported_locales:
		_add_supported_locale(candidate)
	_add_supported_locale(_default_locale)
	_fallbacks.clear()
	for raw_locale: Variant in fallback_chains:
		var raw_values: Variant = fallback_chains[raw_locale]
		if raw_values is Array:
			set_fallbacks(str(raw_locale), _to_string_array(raw_values))
	_locale_store = locale_store if _is_valid_object(locale_store) else null
	var requested := initial_locale.strip_edges()
	if requested.is_empty() and _locale_store != null and _locale_store.has_method("load_locale"):
		requested = str(_locale_store.load_locale(_default_locale))
	var resolved := _resolve_supported_locale(requested if not requested.is_empty() else _default_locale)
	_locale = resolved if not resolved.is_empty() else _default_locale
	refresh_bindings()
	return true


func clear() -> void:
	_providers.clear()
	_bindings.clear()
	_supported_locales.clear()
	_locale_aliases.clear()
	_fallbacks.clear()
	_provider_order = 0
	_next_binding_id = 1
	_locale_store = null
	clear_diagnostics()


func set_locale(raw_locale: String, persist: bool = true) -> bool:
	var resolved := _resolve_supported_locale(raw_locale)
	if resolved.is_empty():
		return false
	if resolved == _locale:
		if persist:
			_persist_locale()
		return true
	_locale = resolved
	if persist:
		_persist_locale()
	refresh_bindings()
	locale_changed.emit(_locale)
	return true


func current_locale() -> String:
	return _locale


func default_locale() -> String:
	return _default_locale


func available_locales() -> Array[String]:
	return _supported_locales.duplicate()


func set_locale_alias(alias: String, locale: String) -> bool:
	var resolved := _resolve_supported_locale(locale)
	var normalized_alias := canonicalize_locale(alias)
	if normalized_alias.is_empty() or resolved.is_empty():
		return false
	_locale_aliases[normalized_alias] = resolved
	return true


func set_fallbacks(locale: String, fallback_locales: Array) -> bool:
	var resolved := _resolve_supported_locale(locale)
	if resolved.is_empty():
		return false
	var values: Array[String] = []
	for fallback: String in fallback_locales:
		var resolved_fallback := _resolve_supported_locale(fallback)
		if not resolved_fallback.is_empty() and resolved_fallback != resolved and not values.has(resolved_fallback):
			values.append(resolved_fallback)
	_fallbacks[resolved] = values
	return true


func register_provider(provider_id: StringName, provider: Variant, priority: int = 0) -> bool:
	if provider_id == &"" or not _is_valid_object(provider):
		return false
	if not provider.has_method("message") and not provider.has_method("asset"):
		return false
	unregister_provider(provider_id)
	_provider_order += 1
	_providers.append({
		"id": provider_id,
		"provider": provider,
		"priority": priority,
		"order": _provider_order,
	})
	_providers.sort_custom(_provider_precedes)
	refresh_bindings()
	return true


func unregister_provider(provider_id: StringName) -> bool:
	for index in range(_providers.size()):
		if StringName(_providers[index].get("id", &"")) == provider_id:
			_providers.remove_at(index)
			refresh_bindings()
			return true
	return false


func provider_ids() -> Array[StringName]:
	var result: Array[StringName] = []
	for entry: Dictionary in _providers:
		result.append(StringName(entry.get("id", &"")))
	return result


func has_message(message_id: String, locale: String = "") -> bool:
	var requested := _resolve_requested_locale(locale)
	if requested.is_empty():
		return false
	return bool(_lookup_message(message_id, requested).get("found", false))


func translate(message_id: String, args: Dictionary = {}, fallback: String = "") -> String:
	return translate_for(_locale, message_id, args, fallback)


func translate_for(
	locale: String,
	message_id: String,
	args: Dictionary = {},
	fallback: String = ""
) -> String:
	var requested := _resolve_requested_locale(locale)
	if requested.is_empty():
		requested = _default_locale
	var lookup := _lookup_message(message_id, requested)
	var template: String
	if bool(lookup.get("found", false)):
		template = str(lookup.get("value", ""))
	else:
		template = fallback if not fallback.is_empty() else message_id
		_record_missing(message_id, requested)
	if requested.nocasecmp_to(PSEUDO_LOCALE) == 0:
		template = _pseudo_localize(template)
	return _format_template(template, args, requested, message_id)


func translate_message(message: Dictionary, locale: String = "") -> String:
	var message_id := str(message.get("id", message.get("message_id", "")))
	var args: Dictionary = message.get("args", message.get("arguments", {})) if message.get("args", message.get("arguments", {})) is Dictionary else {}
	var fallback := str(message.get("fallback", ""))
	return translate_for(_resolve_requested_locale(locale), message_id, args, fallback)


func has_asset(asset_id: String, locale: String = "") -> bool:
	var requested := _resolve_requested_locale(locale)
	if requested.is_empty():
		return false
	return bool(_lookup_asset(asset_id, requested).get("found", false))


func resolve_asset(asset_id: String, fallback: Variant = null) -> Variant:
	return resolve_asset_for(_locale, asset_id, fallback)


func resolve_asset_for(locale: String, asset_id: String, fallback: Variant = null) -> Variant:
	var requested := _resolve_requested_locale(locale)
	if requested.is_empty():
		requested = _default_locale
	var lookup := _lookup_asset(asset_id, requested)
	return lookup.get("value", fallback) if bool(lookup.get("found", false)) else fallback


func bind_property(
	target: Object,
	property: StringName,
	message_id: String,
	args: Variant = {},
	fallback: String = ""
) -> int:
	return _bind(target, property, &"message", message_id, args, fallback)


func bind_asset_property(
	target: Object,
	property: StringName,
	asset_id: String,
	fallback: Variant = null
) -> int:
	return _bind(target, property, &"asset", asset_id, {}, fallback)


func update_binding(
	binding_id: int,
	args: Variant = null,
	localization_id: String = ""
) -> bool:
	if not _bindings.has(binding_id):
		return false
	var binding: Dictionary = _bindings[binding_id]
	if args != null:
		binding["args"] = args
	if not localization_id.is_empty():
		binding["localization_id"] = localization_id
	_bindings[binding_id] = binding
	_refresh_binding(binding_id)
	return true


func unbind(binding_id: int) -> bool:
	return _bindings.erase(binding_id)


func refresh_bindings() -> void:
	for raw_binding_id: Variant in _bindings.keys().duplicate():
		_refresh_binding(int(raw_binding_id))


func diagnostics() -> Dictionary:
	return {
		"missing": _missing.duplicate(true),
		"format_errors": _format_errors.duplicate(true),
	}


func clear_diagnostics() -> void:
	_missing.clear()
	_format_errors.clear()


static func canonicalize_locale(raw_locale: String) -> String:
	var normalized := raw_locale.strip_edges().replace("_", "-")
	if normalized.is_empty():
		return ""
	var parts := normalized.split("-", false)
	if parts.is_empty():
		return ""
	var result: Array[String] = [str(parts[0]).to_lower()]
	for index in range(1, parts.size()):
		var part := str(parts[index])
		result.append(part.to_upper() if part.length() == 2 else part)
	return "-".join(result)


func _add_supported_locale(raw_locale: String) -> void:
	var normalized := raw_locale.strip_edges()
	if normalized.is_empty():
		return
	for existing: String in _supported_locales:
		if existing.nocasecmp_to(normalized) == 0:
			return
	_supported_locales.append(normalized)


func _resolve_requested_locale(raw_locale: String) -> String:
	return _locale if raw_locale.strip_edges().is_empty() else _resolve_supported_locale(raw_locale)


func _resolve_supported_locale(raw_locale: String) -> String:
	var requested := raw_locale.strip_edges()
	if requested.is_empty():
		return ""
	var canonical := canonicalize_locale(requested)
	if _locale_aliases.has(canonical):
		return str(_locale_aliases[canonical])
	for supported: String in _supported_locales:
		if supported.nocasecmp_to(requested) == 0 or canonicalize_locale(supported) == canonical:
			return supported
	return requested if _supported_locales.is_empty() else ""


func _persist_locale() -> void:
	if _locale_store != null and _locale_store.has_method("save_locale"):
		_locale_store.save_locale(_locale)


func _provider_precedes(left: Dictionary, right: Dictionary) -> bool:
	var left_priority := int(left.get("priority", 0))
	var right_priority := int(right.get("priority", 0))
	if left_priority != right_priority:
		return left_priority > right_priority
	return int(left.get("order", 0)) < int(right.get("order", 0))


func _lookup_message(message_id: String, locale: String) -> Dictionary:
	var lookup_locale := _default_locale if locale.nocasecmp_to(PSEUDO_LOCALE) == 0 else locale
	for candidate_locale: String in _locale_chain(lookup_locale):
		for entry: Dictionary in _providers:
			var provider: Variant = entry.get("provider", null)
			if not _is_valid_object(provider) or not provider.has_method("message"):
				continue
			if provider.has_method("has_message") and not bool(provider.has_message(candidate_locale, message_id)):
				continue
			var value: Variant = provider.message(candidate_locale, message_id)
			if value != null:
				return {"found": true, "value": str(value), "locale": candidate_locale, "provider": entry.get("id", &"")}
	return {"found": false}


func _lookup_asset(asset_id: String, locale: String) -> Dictionary:
	var lookup_locale := _default_locale if locale.nocasecmp_to(PSEUDO_LOCALE) == 0 else locale
	for candidate_locale: String in _locale_chain(lookup_locale):
		for entry: Dictionary in _providers:
			var provider: Variant = entry.get("provider", null)
			if not _is_valid_object(provider) or not provider.has_method("asset"):
				continue
			if provider.has_method("has_asset") and not bool(provider.has_asset(candidate_locale, asset_id)):
				continue
			var value: Variant = provider.asset(candidate_locale, asset_id)
			if value != null:
				return {"found": true, "value": value, "locale": candidate_locale, "provider": entry.get("id", &"")}
	return {"found": false}


func _locale_chain(locale: String) -> Array[String]:
	var result: Array[String] = []
	var queue: Array[String] = [locale]
	var visited: Dictionary = {}
	while not queue.is_empty():
		var candidate: String = queue.pop_front()
		if candidate.is_empty() or visited.has(candidate.to_lower()):
			continue
		visited[candidate.to_lower()] = true
		result.append(candidate)
		var configured: Array = _fallbacks.get(candidate, [])
		for raw_fallback: Variant in configured:
			queue.append(str(raw_fallback))
		var canonical := canonicalize_locale(candidate)
		if canonical.contains("-"):
			var language := canonical.get_slice("-", 0)
			var language_locale := _resolve_supported_locale(language)
			if not language_locale.is_empty():
				queue.append(language_locale)
	if not visited.has(_default_locale.to_lower()):
		result.append(_default_locale)
	return result


func _bind(
	target: Object,
	property: StringName,
	kind: StringName,
	localization_id: String,
	args: Variant,
	fallback: Variant
) -> int:
	if target == null or not is_instance_valid(target) or property == &"" or localization_id.is_empty():
		return 0
	if not _has_property(target, property):
		return 0
	var binding_id := _next_binding_id
	_next_binding_id += 1
	_bindings[binding_id] = {
		"target": weakref(target),
		"property": property,
		"kind": kind,
		"localization_id": localization_id,
		"args": args,
		"fallback": fallback,
	}
	_refresh_binding(binding_id)
	return binding_id


func _refresh_binding(binding_id: int) -> void:
	if not _bindings.has(binding_id):
		return
	var binding: Dictionary = _bindings[binding_id]
	var target_ref: WeakRef = binding.get("target", null) as WeakRef
	var target: Object = target_ref.get_ref() if target_ref != null else null
	if target == null or not is_instance_valid(target):
		_bindings.erase(binding_id)
		return
	var localization_id := str(binding.get("localization_id", ""))
	var value: Variant
	if StringName(binding.get("kind", &"")) == &"asset":
		value = resolve_asset(localization_id, binding.get("fallback", null))
	else:
		var raw_args: Variant = binding.get("args", {})
		if raw_args is Callable:
			raw_args = raw_args.call()
		var args: Dictionary = raw_args if raw_args is Dictionary else {}
		value = translate(localization_id, args, str(binding.get("fallback", "")))
	target.set(StringName(binding.get("property", &"")), value)


func _has_property(target: Object, property: StringName) -> bool:
	for info: Dictionary in target.get_property_list():
		if StringName(info.get("name", &"")) == property:
			return true
	return false


func _record_missing(message_id: String, locale: String) -> void:
	var key := "%s|%s" % [locale, message_id]
	var first := not _missing.has(key)
	_missing[key] = int(_missing.get(key, 0)) + 1
	if first:
		missing_message.emit(message_id, locale)


func _record_format_error(message_id: String, locale: String, detail: String) -> void:
	var key := "%s|%s|%s" % [locale, message_id, detail]
	var first := not _format_errors.has(key)
	_format_errors[key] = int(_format_errors.get(key, 0)) + 1
	if first:
		format_error.emit(message_id, locale, detail)


func _format_template(template: String, args: Dictionary, locale: String, message_id: String) -> String:
	if template.is_empty() or not template.contains("{"):
		return template.replace("{{", "{").replace("}}", "}")
	var state := {"error": ""}
	var result := _format_segment(template, args, locale, state)
	var detail := str(state.get("error", ""))
	if not detail.is_empty():
		_record_format_error(message_id, locale, detail)
	return result


func _format_segment(text: String, args: Dictionary, locale: String, state: Dictionary) -> String:
	var output := ""
	var index := 0
	while index < text.length():
		if text.substr(index, 2) == "{{":
			var escaped_close := text.find("}}", index + 2)
			if escaped_close >= 0:
				output += "{" + text.substr(index + 2, escaped_close - index - 2) + "}"
				index = escaped_close + 2
				continue
		if text.substr(index, 1) != "{":
			output += text.substr(index, 1)
			index += 1
			continue
		var close := _matching_brace(text, index)
		if close < 0:
			_set_format_error(state, "Unclosed format expression.")
			output += text.substr(index)
			break
		var expression := text.substr(index + 1, close - index - 1)
		output += _format_expression(expression, args, locale, state)
		index = close + 1
	return output


func _format_expression(expression: String, args: Dictionary, locale: String, state: Dictionary) -> String:
	var parts := _split_expression(expression)
	var argument_name := str(parts[0]).strip_edges() if not parts.is_empty() else ""
	if argument_name.is_empty() or not args.has(argument_name):
		_set_format_error(state, "Missing argument '%s'." % argument_name)
		return "{%s}" % expression
	if parts.size() == 1:
		return str(args[argument_name])
	if parts.size() < 3:
		_set_format_error(state, "Invalid format expression '%s'." % expression)
		return "{%s}" % expression
	var format_type := str(parts[1]).strip_edges().to_lower()
	var options := _parse_options(str(parts[2]), state)
	if format_type == "select":
		var select_key := str(args[argument_name])
		var selected := str(options.get(select_key, options.get("other", "")))
		if selected.is_empty() and not options.has(select_key) and not options.has("other"):
			_set_format_error(state, "Select '%s' has no '%s' or 'other' branch." % [argument_name, select_key])
			return "{%s}" % expression
		return _format_segment(selected, args, locale, state)
	if format_type == "plural" or format_type == "selectordinal":
		var number_result := _to_number(args[argument_name])
		if not bool(number_result.get("valid", false)):
			_set_format_error(state, "Plural argument '%s' is not numeric." % argument_name)
			return "{%s}" % expression
		var number := float(number_result.get("value", 0.0))
		var exact_key := "=%s" % _number_text(number)
		var category := _plural_category(locale, number, format_type == "selectordinal")
		var selected := str(options.get(exact_key, options.get(category, options.get("other", ""))))
		if selected.is_empty() and not options.has(exact_key) and not options.has(category) and not options.has("other"):
			_set_format_error(state, "Plural '%s' has no matching or 'other' branch." % argument_name)
			return "{%s}" % expression
		return _format_segment(selected, args, locale, state).replace("#", _number_text(number))
	_set_format_error(state, "Unknown format type '%s'." % format_type)
	return "{%s}" % expression


func _matching_brace(text: String, open_index: int) -> int:
	var depth := 0
	for index in range(open_index, text.length()):
		var character := text.substr(index, 1)
		if character == "{":
			depth += 1
		elif character == "}":
			depth -= 1
			if depth == 0:
				return index
	return -1


func _split_expression(expression: String) -> Array[String]:
	var parts: Array[String] = []
	var start := 0
	var depth := 0
	for index in range(expression.length()):
		var character := expression.substr(index, 1)
		if character == "{":
			depth += 1
		elif character == "}":
			depth -= 1
		elif character == "," and depth == 0 and parts.size() < 2:
			parts.append(expression.substr(start, index - start))
			start = index + 1
	parts.append(expression.substr(start))
	return parts


func _parse_options(text: String, state: Dictionary) -> Dictionary:
	var options: Dictionary = {}
	var index := 0
	while index < text.length():
		while index < text.length() and text.substr(index, 1).strip_edges().is_empty():
			index += 1
		if index >= text.length():
			break
		var key_start := index
		while index < text.length() and text.substr(index, 1) != "{" and not text.substr(index, 1).strip_edges().is_empty():
			index += 1
		var key := text.substr(key_start, index - key_start).strip_edges()
		while index < text.length() and text.substr(index, 1).strip_edges().is_empty():
			index += 1
		if key.is_empty() or index >= text.length() or text.substr(index, 1) != "{":
			_set_format_error(state, "Invalid plural/select option near '%s'." % text.substr(key_start))
			break
		var close := _matching_brace(text, index)
		if close < 0:
			_set_format_error(state, "Unclosed plural/select option '%s'." % key)
			break
		options[key] = text.substr(index + 1, close - index - 1)
		index = close + 1
	return options


func _plural_category(locale: String, value: float, ordinal: bool) -> String:
	var language := canonicalize_locale(locale).get_slice("-", 0)
	var integer := int(value)
	var is_integer := is_equal_approx(value, float(integer))
	if ordinal and language == "en" and is_integer:
		var mod_10 := posmod(integer, 10)
		var mod_100 := posmod(integer, 100)
		if mod_10 == 1 and mod_100 != 11:
			return "one"
		if mod_10 == 2 and mod_100 != 12:
			return "two"
		if mod_10 == 3 and mod_100 != 13:
			return "few"
		return "other"
	if not is_integer:
		return "other"
	match language:
		"fr", "pt":
			return "one" if integer == 0 or integer == 1 else "other"
		"ru", "uk":
			if posmod(integer, 10) == 1 and posmod(integer, 100) != 11:
				return "one"
			if posmod(integer, 10) in [2, 3, 4] and not posmod(integer, 100) in [12, 13, 14]:
				return "few"
			return "many"
		"pl":
			if integer == 1:
				return "one"
			if posmod(integer, 10) in [2, 3, 4] and not posmod(integer, 100) in [12, 13, 14]:
				return "few"
			return "many"
		"ar":
			if integer == 0:
				return "zero"
			if integer == 1:
				return "one"
			if integer == 2:
				return "two"
			if posmod(integer, 100) in range(3, 11):
				return "few"
			if posmod(integer, 100) in range(11, 100):
				return "many"
			return "other"
		_:
			return "one" if integer == 1 else "other"


func _to_number(value: Variant) -> Dictionary:
	if value is int or value is float:
		return {"valid": true, "value": float(value)}
	var text := str(value).strip_edges()
	return {"valid": text.is_valid_float(), "value": text.to_float() if text.is_valid_float() else 0.0}


func _number_text(value: float) -> String:
	var rounded := roundi(value)
	return str(rounded) if is_equal_approx(value, float(rounded)) else str(value)


func _set_format_error(state: Dictionary, detail: String) -> void:
	if str(state.get("error", "")).is_empty():
		state["error"] = detail


func _pseudo_localize(text: String) -> String:
	# Avoid square-bracket wrappers because RichTextLabel treats them as BBCode.
	var output := "!! "
	var index := 0
	while index < text.length():
		var character := text.substr(index, 1)
		if character in ["<", "[", "{"]:
			var closing := ">" if character == "<" else "]" if character == "[" else "}"
			var close := text.find(closing, index + 1)
			if close >= 0:
				output += text.substr(index, close - index + 1)
				index = close + 1
				continue
		output += character
		if character.to_lower() in ["a", "e", "i", "o", "u"]:
			output += character
		index += 1
	return output + " !!"


func _to_string_array(values: Array) -> Array[String]:
	var result: Array[String] = []
	for value: Variant in values:
		result.append(str(value))
	return result


func _is_valid_object(value: Variant) -> bool:
	return typeof(value) == TYPE_OBJECT and is_instance_valid(value)
