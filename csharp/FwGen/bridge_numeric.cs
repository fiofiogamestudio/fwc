using System.Text;

static class BridgeNumeric
{
    internal static bool IsNumeric(string type, ProtoSchema? schema = null) => type is "float" or "double" or "int32" or "sint32"
        or "uint32" or "int64" or "sint64" or "uint64" or "__bridge_integer" or "PlayerId" or "EntityId"
        || (type.EndsWith("Id", StringComparison.Ordinal) && (schema == null || !schema.Enums.ContainsKey(type)));

    internal static string Method(string type) => type switch
    {
        "float" => "Float", "double" => "Double", "uint32" => "UInt", "uint64" => "ULong",
        "int64" or "sint64" or "PlayerId" => "Long", "__bridge_integer" => "IntegerUnion", _ => "Int"
    };

    internal static void AppendCs(StringBuilder text)
    {
        text.AppendLine("""
    private static global::System.FormatException InvalidNumber(string type) => new($"Invalid bridge {type} value.");

    public static long RequireLong(Variant value)
    {
        if (value.VariantType != Variant.Type.Int) throw InvalidNumber("int64");
        return value.AsInt64();
    }

    public static int RequireInt(Variant value)
    {
        long number = RequireLong(value);
        if (number < int.MinValue || number > int.MaxValue) throw InvalidNumber("int32");
        return (int)number;
    }

    public static uint RequireUInt(Variant value)
    {
        long number = RequireLong(value);
        if (number < 0 || number > uint.MaxValue) throw InvalidNumber("uint32");
        return (uint)number;
    }

    public static ulong RequireULong(Variant value)
    {
        if (value.VariantType != Variant.Type.String) throw InvalidNumber("uint64 decimal string");
        string text = value.AsString();
        if (!ulong.TryParse(text, global::System.Globalization.NumberStyles.None, global::System.Globalization.CultureInfo.InvariantCulture, out ulong number)
            || number.ToString(global::System.Globalization.CultureInfo.InvariantCulture) != text) throw InvalidNumber("uint64 decimal string");
        return number;
    }

    public static decimal RequireIntegerUnion(Variant value) => value.VariantType == Variant.Type.String ? RequireULong(value) : RequireLong(value);

    public static decimal RequireIntegerUnionValue(decimal value)
    {
        if (decimal.Truncate(value) != value) throw InvalidNumber("integral oneof value");
        return value;
    }

    public static double RequireDouble(Variant value)
    {
        if (value.VariantType is not (Variant.Type.Float or Variant.Type.Int)) throw InvalidNumber("double");
        double number = value.AsDouble();
        if (!double.IsFinite(number)) throw InvalidNumber("finite double");
        return number;
    }

    public static float RequireFloat(Variant value)
    {
        float number = (float)RequireDouble(value);
        if (!float.IsFinite(number)) throw InvalidNumber("finite float");
        return number;
    }

    public static Variant EncodeInt(int value) => value;
    public static Variant EncodeUInt(uint value) => (long)value;
    public static Variant EncodeLong(long value) => value;
    public static Variant EncodeULong(ulong value) => value.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
    public static Variant EncodeFloat(float value)
    {
        if (!float.IsFinite(value)) throw InvalidNumber("finite float");
        return value;
    }
    public static Variant EncodeDouble(double value)
    {
        if (!double.IsFinite(value)) throw InvalidNumber("finite double");
        return value;
    }

    private static T ReadNumber<T>(GdDictionary packet, string field, T fallback, global::System.Func<Variant, T> read)
        => packet.TryGetValue(field, out Variant value) ? read(value) : fallback;
    public static int ReadInt(GdDictionary packet, string field, int fallback = 0) => ReadNumber(packet, field, fallback, RequireInt);
    public static uint ReadUInt(GdDictionary packet, string field, uint fallback = 0) => ReadNumber(packet, field, fallback, RequireUInt);
    public static long ReadLong(GdDictionary packet, string field, long fallback = 0) => ReadNumber(packet, field, fallback, RequireLong);
    public static ulong ReadULong(GdDictionary packet, string field, ulong fallback = 0) => ReadNumber(packet, field, fallback, RequireULong);
    public static decimal ReadIntegerUnion(GdDictionary packet, string field, decimal fallback = 0) => ReadNumber(packet, field, fallback, RequireIntegerUnion);
    public static float ReadFloat(GdDictionary packet, string field, float fallback = 0) => ReadNumber(packet, field, fallback, RequireFloat);
    public static double ReadDouble(GdDictionary packet, string field, double fallback = 0) => ReadNumber(packet, field, fallback, RequireDouble);

    public static bool TryReadNumber<T>(GdDictionary packet, string field, global::System.Func<Variant, T> read, out T value)
    {
        value = default!;
        if (!packet.TryGetValue(field, out Variant raw)) return false;
        try { value = read(raw); return true; }
        catch (global::System.FormatException) { return false; }
    }

    public static global::System.Collections.Generic.List<T> ReadNumbers<T>(GdDictionary packet, string field, global::System.Func<Variant, T> read)
    {
        if (!packet.TryGetValue(field, out Variant raw)) return new();
        if (raw.VariantType != Variant.Type.Array) throw InvalidNumber("numeric array");
        var result = new global::System.Collections.Generic.List<T>();
        foreach (Variant value in raw.AsGodotArray()) result.Add(read(value));
        return result;
    }

    public static GdArray EncodeNumbers<T>(global::System.Collections.Generic.IEnumerable<T> values, global::System.Func<T, Variant> encode)
    {
        var result = new GdArray();
        foreach (T value in values) result.Add(encode(value));
        return result;
    }

    public static GdArray RequireNumbers(GdArray values, global::System.Func<Variant, Variant> validate)
    {
        var result = new GdArray();
        foreach (Variant value in values) result.Add(validate(value));
        return result;
    }

    public static GdArray RequireNumberArray(Variant value, global::System.Func<Variant, Variant> validate)
    {
        if (value.VariantType != Variant.Type.Array) throw InvalidNumber("numeric array");
        return RequireNumbers(value.AsGodotArray(), validate);
    }
""");
    }

    internal static string GdHelpers() => """
class Numeric:
	extends RefCounted

	static func read(value: Variant, type: String, label: String) -> Variant:
		if type.ends_with("Id"):
			type = "int64" if type == "PlayerId" else "int32"
		if type == "uint64":
			if value is String and not value.is_empty() and (value == "0" or not value.begins_with("0")):
				var digits := true
				for character in value:
					if character < "0" or character > "9":
						digits = false
				if digits and (value.length() < 20 or (value.length() == 20 and value <= "18446744073709551615")):
					return value
		elif type in ["int32", "sint32", "uint32", "int64", "sint64"]:
			if typeof(value) == TYPE_INT:
				if type in ["int64", "sint64"] or (type == "uint32" and value >= 0 and value <= 4294967295) or (type in ["int32", "sint32"] and value >= -2147483648 and value <= 2147483647):
					return value
		elif typeof(value) in [TYPE_INT, TYPE_FLOAT]:
			var number := float(value)
			if is_finite(number):
				if type == "float":
					number = PackedFloat32Array([number])[0]
				if is_finite(number):
					return number
		push_error("Invalid bridge %s value at %s." % [type, label])
		return null

	static func read_array(value: Variant, type: String, label: String) -> Array:
		if not value is Array:
			push_error("Invalid bridge numeric array at %s." % label)
			return []
		var result: Array = []
		for item in value:
			var parsed: Variant = read(item, type, label)
			if parsed == null:
				return []
			result.append(parsed)
		return result

	static func valid(value: Variant, type: String, label: String, repeated: bool) -> bool:
		if not repeated:
			return read(value, type, label) != null
		if not value is Array:
			push_error("Invalid bridge numeric array at %s." % label)
			return false
		for item in value:
			if read(item, type, label) == null:
				return false
		return true

""";
}
