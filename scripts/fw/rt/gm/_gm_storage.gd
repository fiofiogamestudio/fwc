extends RefCounted

## Private, versioned Variant envelope. Object decoding is never allowed.
const _MAGIC := "FWGM0001"
const _MAX_BYTES := 2097152
const _HEADER_BYTES := 44


static func read(path: String) -> Dictionary:
	if not _valid_path(path):
		return _failure("storage_path", "GM 缓存路径必须是 user:// 文件或绝对文件路径，且不能包含父目录跳转。")
	if not FileAccess.file_exists(path):
		return {"ok": true, "message": ""}
	var file := FileAccess.open(path, FileAccess.READ)
	if file == null:
		return _failure("storage_read", "无法读取 GM 缓存：%s。" % error_string(FileAccess.get_open_error()))
	var length := file.get_length()
	if length < _HEADER_BYTES or length > _MAX_BYTES:
		return _failure("storage_invalid", "GM 缓存文件长度无效，已保留当前数据。")
	var bytes := file.get_buffer(length)
	file.close()
	if bytes.size() != length or bytes.slice(0, 8).get_string_from_ascii() != _MAGIC:
		return _failure("storage_invalid", "GM 缓存文件头无效，已保留当前数据。")
	var size := bytes.decode_u32(8)
	if size != length - _HEADER_BYTES:
		return _failure("storage_invalid", "GM 缓存内容长度不匹配，已保留当前数据。")
	var payload := bytes.slice(_HEADER_BYTES)
	if _digest(payload) != bytes.slice(12, _HEADER_BYTES):
		return _failure("storage_invalid", "GM 缓存校验失败，已保留当前数据。")
	# bytes_to_var does not permit Object decoding (unlike bytes_to_var_with_objects).
	return {"ok": true, "message": "", "data": bytes_to_var(payload)}


static func write(path: String, data: Dictionary) -> Dictionary:
	if not _valid_path(path):
		return _failure("storage_path", "GM 缓存路径必须是 user:// 文件或绝对文件路径。")
	var payload := var_to_bytes(data)
	if payload.size() + _HEADER_BYTES > _MAX_BYTES:
		return _failure("storage_limit", "GM 缓存超过 2 MiB，当前会话数据仍可使用。")
	var directory := ProjectSettings.globalize_path(path.get_base_dir())
	var made := DirAccess.make_dir_recursive_absolute(directory)
	if made != OK:
		return _failure("storage_write", "无法创建 GM 缓存目录：%s。" % error_string(made))
	var temporary := path + ".tmp"
	var file := FileAccess.open(temporary, FileAccess.WRITE)
	if file == null:
		return _failure("storage_write", "无法写入 GM 缓存：%s。" % error_string(FileAccess.get_open_error()))
	file.store_buffer(_MAGIC.to_ascii_buffer())
	file.store_32(payload.size())
	file.store_buffer(_digest(payload))
	file.store_buffer(payload)
	file.flush()
	var saved := file.get_error()
	file.close()
	if saved != OK:
		DirAccess.remove_absolute(ProjectSettings.globalize_path(temporary))
		return _failure("storage_write", "GM 缓存写入未完成：%s。" % error_string(saved))
	# Rename within the same directory atomically replaces the old file.
	var renamed := DirAccess.rename_absolute(ProjectSettings.globalize_path(temporary), ProjectSettings.globalize_path(path))
	if renamed != OK:
		DirAccess.remove_absolute(ProjectSettings.globalize_path(temporary))
		return _failure("storage_write", "无法替换 GM 缓存：%s。" % error_string(renamed))
	return {"ok": true, "message": ""}


static func _valid_path(path: String) -> bool:
	var normalized := path.replace("\\", "/")
	var allowed_root := normalized.begins_with("user://") or (normalized.is_absolute_path() and not normalized.contains("://"))
	return allowed_root and not normalized.get_file().is_empty() and not ".." in normalized.split("/") and normalized.length() <= 1024


static func _digest(payload: PackedByteArray) -> PackedByteArray:
	var hashing := HashingContext.new()
	hashing.start(HashingContext.HASH_SHA256)
	hashing.update(payload)
	return hashing.finish()


static func _failure(code: String, message: String) -> Dictionary:
	return {"ok": false, "code": code, "message": message}
