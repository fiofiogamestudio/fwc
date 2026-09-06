// Exact decimal conversion for generated Godot config loaders. Godot 4.6's
// String.to_float() is not correctly rounded at all binary32/binary64 boundaries.
static class ConfigNumericGd
{
    internal static string RuntimePrelude() => """

# Bounded, exact decimal -> IEEE round-to-nearest, ties-to-even. This is a config
# load path, not a per-frame operation. No significant digits are discarded.
const _DEC_TEXT_LIMIT: int = 4096
const _DEC_LIMB_BITS: int = 30
const _DEC_LIMB_BASE: int = 1 << _DEC_LIMB_BITS
const _DEC_LIMB_MASK: int = _DEC_LIMB_BASE - 1

static func _decimal_to_double(text: String, ctx: String) -> float:
	return _decimal_to_binary(text, ctx, 53, -1022, 1023)

static func _decimal_to_single(text: String, ctx: String) -> float:
	return _decimal_to_binary(text, ctx, 24, -126, 127)

static func _decimal_to_binary(text: String, ctx: String, precision: int, min_exponent: int, max_exponent: int) -> float:
	if text.length() > _DEC_TEXT_LIMIT:
		_fail("%s numeric text exceeds %d characters" % [ctx, _DEC_TEXT_LIMIT])
		return 0.0
	# NumberStyles.Float accepts ASCII leading/trailing whitespace, not grouping,
	# a trailing sign, non-decimal notation, NaN, or Infinity in this finite contract.
	var start: int = 0
	var end: int = text.length()
	while start < end and _dec_is_space(text.unicode_at(start)):
		start += 1
	while end > start and _dec_is_space(text.unicode_at(end - 1)):
		end -= 1
	var negative: bool = false
	if start < end and text[start] in ["+", "-"]:
		negative = text[start] == "-"
		start += 1
	var digits: String = ""
	var fractional_digits: int = 0
	var has_point: bool = false
	while start < end:
		var code: int = text.unicode_at(start)
		if code >= 48 and code <= 57:
			digits += text[start]
			if has_point:
				fractional_digits += 1
		elif code == 46 and not has_point:
			has_point = true
		else:
			break
		start += 1
	if digits.is_empty():
		_fail("%s must be finite decimal text" % ctx)
		return 0.0
	var exponent: int = 0
	if start < end and text[start] in ["e", "E"]:
		start += 1
		var exponent_negative: bool = false
		if start < end and text[start] in ["+", "-"]:
			exponent_negative = text[start] == "-"
			start += 1
		var exponent_start: int = start
		while start < end and text.unicode_at(start) >= 48 and text.unicode_at(start) <= 57:
			# Saturation cannot change the answer: at most TEXT_LIMIT significand
			# digits can cancel this exponent, leaving clear overflow/underflow.
			exponent = mini(_DEC_TEXT_LIMIT * 2, exponent * 10 + text.unicode_at(start) - 48)
			start += 1
		if exponent_start == start:
			_fail("%s must be finite decimal text" % ctx)
			return 0.0
		if exponent_negative:
			exponent = -exponent
	if start != end:
		_fail("%s must be finite decimal text" % ctx)
		return 0.0
	var first: int = 0
	var last: int = digits.length()
	while first < last and digits[first] == "0":
		first += 1
	if first == last:
		return _dec_from_bits(0, negative, precision)
	while digits[last - 1] == "0":
		last -= 1
	exponent += digits.length() - last - fractional_digits
	digits = digits.substr(first, last - first)
	var decimal_order: int = digits.length() + exponent - 1
	var largest_order: int = 308 if precision == 53 else 38
	var smallest_order: int = -324 if precision == 53 else -46
	if decimal_order > largest_order:
		_fail("%s is outside the finite numeric range" % ctx)
		return 0.0
	if decimal_order < smallest_order:
		return _dec_from_bits(0, negative, precision)
	var numerator: PackedInt64Array = PackedInt64Array()
	for index in range(digits.length()):
		numerator = _dec_big_mul_add(numerator, 10, digits.unicode_at(index) - 48)
	var denominator: PackedInt64Array = PackedInt64Array([1])
	if exponent >= 0:
		for _index in range(exponent):
			numerator = _dec_big_mul_add(numerator, 10, 0)
	else:
		for _index in range(-exponent):
			denominator = _dec_big_mul_add(denominator, 10, 0)
	# floor(log2(N/D)), using only integer comparisons.
	var binary_exponent: int = _dec_big_bits(numerator) - _dec_big_bits(denominator)
	var comparison: int
	if binary_exponent >= 0:
		comparison = _dec_big_compare(numerator, _dec_big_shift(denominator, binary_exponent))
	else:
		comparison = _dec_big_compare(_dec_big_shift(numerator, -binary_exponent), denominator)
	if comparison < 0:
		binary_exponent -= 1
	if binary_exponent > max_exponent:
		_fail("%s is outside the finite numeric range" % ctx)
		return 0.0
	var fraction_bits: int = precision - 1
	var shift: int = fraction_bits - maxi(binary_exponent, min_exponent)
	if shift >= 0:
		numerator = _dec_big_shift(numerator, shift)
	else:
		denominator = _dec_big_shift(denominator, -shift)
	var significand: int = _dec_big_round_ratio(numerator, denominator)
	if binary_exponent < min_exponent:
		# Rounding may produce the smallest normal; its bit pattern is exactly
		# 1 << fraction_bits, so the subnormal path handles that transition too.
		return _dec_from_bits(significand, negative, precision)
	if significand == (1 << precision):
		significand >>= 1
		binary_exponent += 1
	if binary_exponent > max_exponent:
		_fail("%s is outside the finite numeric range" % ctx)
		return 0.0
	var bits: int = ((binary_exponent + max_exponent) << fraction_bits) | (significand - (1 << fraction_bits))
	return _dec_from_bits(bits, negative, precision)

static func _dec_is_space(code: int) -> bool:
	return code == 32 or (code >= 9 and code <= 13)

static func _dec_from_bits(bits: int, negative: bool, precision: int) -> float:
	var bytes: PackedByteArray = PackedByteArray()
	if precision == 24:
		bytes.resize(4)
		bytes.encode_s32(0, bits | -2147483648 if negative else bits)
		return bytes.decode_float(0)
	bytes.resize(8)
	bytes.encode_s64(0, bits | (-9223372036854775807 - 1) if negative else bits)
	return bytes.decode_double(0)

# Little-endian nonnegative base-2^30 limbs; zero is the empty array. Functions
# return new arrays, avoiding PackedArray aliasing between numerator/denominator.
static func _dec_big_mul_add(value: PackedInt64Array, multiplier: int, carry: int) -> PackedInt64Array:
	var out: PackedInt64Array = PackedInt64Array()
	out.resize(value.size())
	for index in range(value.size()):
		var product: int = value[index] * multiplier + carry
		out[index] = product & _DEC_LIMB_MASK
		carry = product >> _DEC_LIMB_BITS
	if carry != 0:
		out.append(carry)
	return out

static func _dec_big_bits(value: PackedInt64Array) -> int:
	if value.is_empty():
		return 0
	var bits: int = (value.size() - 1) * _DEC_LIMB_BITS
	var high: int = value[value.size() - 1]
	while high > 0:
		high >>= 1
		bits += 1
	return bits

static func _dec_big_shift(value: PackedInt64Array, bits: int) -> PackedInt64Array:
	if value.is_empty():
		return PackedInt64Array()
	@warning_ignore("integer_division")
	var words: int = bits / _DEC_LIMB_BITS
	var offset: int = bits % _DEC_LIMB_BITS
	var out: PackedInt64Array = PackedInt64Array()
	out.resize(value.size() + words)
	var carry: int = 0
	for index in range(value.size()):
		var shifted: int = (value[index] << offset) | carry
		out[index + words] = shifted & _DEC_LIMB_MASK
		carry = shifted >> _DEC_LIMB_BITS
	if carry != 0:
		out.append(carry)
	return out

static func _dec_big_compare(left: PackedInt64Array, right: PackedInt64Array) -> int:
	if left.size() != right.size():
		return 1 if left.size() > right.size() else -1
	for index in range(left.size() - 1, -1, -1):
		if left[index] != right[index]:
			return 1 if left[index] > right[index] else -1
	return 0

static func _dec_big_subtract(left: PackedInt64Array, right: PackedInt64Array) -> PackedInt64Array:
	var out: PackedInt64Array = PackedInt64Array()
	out.resize(left.size())
	var borrow: int = 0
	for index in range(left.size()):
		var difference: int = left[index] - (right[index] if index < right.size() else 0) - borrow
		borrow = 1 if difference < 0 else 0
		out[index] = difference + (_DEC_LIMB_BASE if borrow else 0)
	while not out.is_empty() and out[out.size() - 1] == 0:
		out.resize(out.size() - 1)
	return out

static func _dec_big_round_ratio(numerator: PackedInt64Array, denominator: PackedInt64Array) -> int:
	# Scaling above guarantees at most precision+1 quotient bits (<=54), so the
	# quotient fits an int64 even though the exact dividend/divisor may be large.
	var remainder: PackedInt64Array = numerator
	var quotient: int = 0
	for bit in range(_dec_big_bits(numerator) - _dec_big_bits(denominator), -1, -1):
		var shifted: PackedInt64Array = _dec_big_shift(denominator, bit)
		if _dec_big_compare(remainder, shifted) >= 0:
			remainder = _dec_big_subtract(remainder, shifted)
			quotient |= 1 << bit
	var halfway: int = _dec_big_compare(_dec_big_shift(remainder, 1), denominator)
	if halfway > 0 or (halfway == 0 and (quotient & 1) != 0):
		quotient += 1
	return quotient

""";
}
