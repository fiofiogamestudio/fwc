class_name FLocalizationCatalog
extends RefCounted

const SCHEMA_VERSION := 1

var _namespace := ""
var _messages: Dictionary = {}
var _assets: Dictionary = {}
var _aliases: Dictionary = {}
var _metadata: Dictionary = {}
var _locales: Dictionary = {}


func setup(catalog_namespace: String = "") -> FLocalizationCatalog:
	_namespace = catalog_namespace.strip_edges().trim_suffix(".")
	return self


func clear() -> void:
	_messages.clear()
	_assets.clear()
	_aliases.clear()
	_metadata.clear()
	_locales.clear()


func load_catalog(data: Dictionary) -> Array[String]:
	clear()
	var issues: Array[String] = []
	var schema_version := int(data.get("schema_version", SCHEMA_VERSION))
	if schema_version != SCHEMA_VERSION:
		issues.append("Unsupported localization catalog schema_version: %d." % schema_version)
	_namespace = str(data.get("namespace", _namespace)).strip_edges().trim_suffix(".")

	var raw_messages: Variant = data.get("messages", {})
	if not raw_messages is Dictionary:
		issues.append("Localization catalog messages must be a dictionary.")
	else:
		for raw_id: Variant in raw_messages:
			var message_id := str(raw_id)
			var entry: Variant = raw_messages[raw_id]
			var translations: Variant = entry
			if entry is Dictionary and entry.has("translations"):
				translations = entry.get("translations", {})
				var entry_metadata: Dictionary = entry.get("metadata", {}) if entry.get("metadata", {}) is Dictionary else {}
				if not entry_metadata.is_empty():
					_metadata[_qualify(message_id)] = entry_metadata.duplicate(true)
			if not translations is Dictionary:
				issues.append("Message '%s' translations must be a dictionary." % message_id)
				continue
			for raw_locale: Variant in translations:
				add_message(message_id, str(raw_locale), str(translations[raw_locale]))

	var raw_assets: Variant = data.get("assets", {})
	if not raw_assets is Dictionary:
		issues.append("Localization catalog assets must be a dictionary.")
	else:
		for raw_id: Variant in raw_assets:
			var asset_id := str(raw_id)
			var translations: Variant = raw_assets[raw_id]
			if translations is Dictionary and translations.has("values"):
				translations = translations.get("values", {})
			if not translations is Dictionary:
				issues.append("Asset '%s' values must be a dictionary." % asset_id)
				continue
			for raw_locale: Variant in translations:
				add_asset(asset_id, str(raw_locale), translations[raw_locale])

	var raw_aliases: Variant = data.get("aliases", {})
	if not raw_aliases is Dictionary:
		issues.append("Localization catalog aliases must be a dictionary.")
	else:
		for raw_alias: Variant in raw_aliases:
			add_alias(str(raw_alias), str(raw_aliases[raw_alias]))
	issues.append_array(validate())
	return issues


func add_message(message_id: String, locale: String, text: String) -> bool:
	var qualified := _qualify(message_id)
	var normalized_locale := locale.strip_edges()
	if not _is_valid_id(qualified) or normalized_locale.is_empty():
		return false
	var translations: Dictionary = _messages.get(qualified, {})
	translations[normalized_locale] = text
	_messages[qualified] = translations
	_locales[normalized_locale] = true
	return true


func add_asset(asset_id: String, locale: String, value: Variant) -> bool:
	var qualified := _qualify(asset_id)
	var normalized_locale := locale.strip_edges()
	if not _is_valid_id(qualified) or normalized_locale.is_empty() or value == null:
		return false
	var translations: Dictionary = _assets.get(qualified, {})
	translations[normalized_locale] = value
	_assets[qualified] = translations
	_locales[normalized_locale] = true
	return true


func add_alias(alias_id: String, target_id: String) -> bool:
	var qualified_alias := _qualify(alias_id)
	var qualified_target := _qualify(target_id)
	if not _is_valid_id(qualified_alias) or not _is_valid_id(qualified_target):
		return false
	_aliases[qualified_alias] = qualified_target
	return true


func has_message(locale: String, message_id: String) -> bool:
	var resolved := resolve_id(message_id)
	var translations: Dictionary = _messages.get(resolved, {})
	return _dictionary_locale_key(translations, locale) != ""


func message(locale: String, message_id: String) -> Variant:
	var resolved := resolve_id(message_id)
	var translations: Dictionary = _messages.get(resolved, {})
	var locale_key := _dictionary_locale_key(translations, locale)
	return translations.get(locale_key, null) if not locale_key.is_empty() else null


func has_asset(locale: String, asset_id: String) -> bool:
	var resolved := resolve_id(asset_id)
	var translations: Dictionary = _assets.get(resolved, {})
	return _dictionary_locale_key(translations, locale) != ""


func asset(locale: String, asset_id: String) -> Variant:
	var resolved := resolve_id(asset_id)
	var translations: Dictionary = _assets.get(resolved, {})
	var locale_key := _dictionary_locale_key(translations, locale)
	return translations.get(locale_key, null) if not locale_key.is_empty() else null


func resolve_id(raw_id: String) -> String:
	var current := _qualify(raw_id)
	var visited: Dictionary = {}
	while _aliases.has(current) and not visited.has(current):
		visited[current] = true
		current = str(_aliases[current])
	return current


func locales() -> Array[String]:
	var result: Array[String] = []
	for raw_locale: Variant in _locales:
		result.append(str(raw_locale))
	result.sort()
	return result


func message_ids() -> Array[String]:
	return _sorted_string_keys(_messages)


func asset_ids() -> Array[String]:
	return _sorted_string_keys(_assets)


func metadata(message_id: String) -> Dictionary:
	return (_metadata.get(resolve_id(message_id), {}) as Dictionary).duplicate(true)


func validate() -> Array[String]:
	var issues: Array[String] = []
	for alias_id: Variant in _aliases:
		var target := resolve_id(str(alias_id))
		if target == str(alias_id):
			issues.append("Localization alias cycle: %s." % alias_id)
		elif not _messages.has(target) and not _assets.has(target):
			issues.append("Localization alias '%s' targets missing id '%s'." % [alias_id, target])
	for message_id: Variant in _messages:
		var translations: Dictionary = _messages[message_id]
		var reference_tokens: Array[String] = []
		var has_reference := false
		for locale: Variant in translations:
			var text := str(translations[locale])
			if text.is_empty():
				issues.append("Message '%s' has an empty '%s' translation." % [message_id, locale])
			var tokens := _placeholder_tokens(text)
			if not has_reference:
				reference_tokens = tokens
				has_reference = true
			elif tokens != reference_tokens:
				issues.append("Message '%s' has mismatched placeholders in locale '%s'." % [message_id, locale])
	return issues


func _qualify(raw_id: String) -> String:
	var normalized := raw_id.strip_edges().trim_prefix(".")
	if _namespace.is_empty() or normalized.begins_with(_namespace + "."):
		return normalized
	return "%s.%s" % [_namespace, normalized]


func _is_valid_id(value: String) -> bool:
	if value.is_empty() or value.begins_with(".") or value.ends_with("."):
		return false
	for segment in value.split(".", false):
		if segment.is_empty():
			return false
		for index in range(segment.length()):
			var code := segment.unicode_at(index)
			var valid := (
				(code >= 48 and code <= 57)
				or (code >= 65 and code <= 90)
				or (code >= 97 and code <= 122)
				or code == 45
				or code == 95
			)
			if not valid:
				return false
	return true


func _dictionary_locale_key(values: Dictionary, locale: String) -> String:
	var requested := locale.strip_edges()
	if values.has(requested):
		return requested
	for raw_locale: Variant in values:
		if str(raw_locale).nocasecmp_to(requested) == 0:
			return str(raw_locale)
	return ""


func _sorted_string_keys(values: Dictionary) -> Array[String]:
	var result: Array[String] = []
	for raw_key: Variant in values:
		result.append(str(raw_key))
	result.sort()
	return result


func _placeholder_tokens(text: String) -> Array[String]:
	var found: Dictionary = {}
	var identifier := RegEx.new()
	if identifier.compile("^[A-Za-z_][A-Za-z0-9_]*$") != OK:
		return []
	var index := 0
	while index < text.length():
		if text.substr(index, 2) == "{{":
			var escaped_close := text.find("}}", index + 2)
			index = escaped_close + 2 if escaped_close >= 0 else text.length()
			continue
		if text.substr(index, 1) != "{":
			index += 1
			continue
		var close := _matching_brace(text, index)
		if close < 0:
			break
		var expression := text.substr(index + 1, close - index - 1)
		var name := expression.get_slice(",", 0).strip_edges()
		if identifier.search(name) != null:
			found[name] = true
		index = close + 1
	return _sorted_string_keys(found)


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
