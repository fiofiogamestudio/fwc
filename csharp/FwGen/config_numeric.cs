using System.Globalization;
using System.Text.Json;

// The authoring JSON boundary must also survive Godot/JavaScript number parsing.
// Wide integers therefore use decimal strings outside the shared exact range.
static class ConfigNumeric
{
    private const long MaxJsonInteger = 9007199254740991;

    internal static object Json(JsonElement item, string type, string ctx)
    {
        if (item.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
            throw new InvalidOperationException($"{ctx} must be {type} numeric text or a number");
        string raw = item.ValueKind == JsonValueKind.String ? item.GetString()! : item.GetRawText();
        if (item.ValueKind == JsonValueKind.Number && type is "int64" or "sint64" or "uint64")
        {
            if (!item.TryGetInt64(out long integer) || integer < -MaxJsonInteger || integer > MaxJsonInteger)
                throw new InvalidOperationException($"{ctx}: {type} JSON numbers must be safe integers; use decimal strings for the full 64-bit range");
        }
        return Text(raw, type, ctx);
    }

    internal static object Text(string raw, string type, string ctx)
    {
        if (raw.Length > 4096)
            throw new InvalidOperationException($"{ctx} numeric text exceeds 4096 characters");
        try
        {
            return type switch
            {
                "int32" or "sint32" => int.Parse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture),
                "uint32" => uint.Parse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture),
                "int64" or "sint64" => long.Parse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                "uint64" => ulong.Parse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                "float" => FiniteSingle(raw),
                "double" => FiniteDouble(raw),
                _ => throw new InvalidOperationException($"unknown numeric type {type}"),
            };
        }
        catch (Exception error) when (error is FormatException or OverflowException)
        {
            throw new InvalidOperationException($"{ctx} must be a finite {type} value in its declared range: `{raw}`", error);
        }
    }

    private static float FiniteSingle(string raw)
    {
        float value = float.Parse(NormalizeDecimal(raw), NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!float.IsFinite(value)) throw new OverflowException("non-finite float");
        return value;
    }

    private static double FiniteDouble(string raw)
    {
        double value = double.Parse(NormalizeDecimal(raw), NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!double.IsFinite(value)) throw new OverflowException("non-finite double");
        return value;
    }

    // .NET 8's scale calculation can underflow a long integer coefficient before
    // its compensating exponent is applied. Keep every digit, normalize the scale,
    // and leave IEEE rounding to the platform parser.
    private static string NormalizeDecimal(string raw)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            raw.Trim(' ', '\t', '\r', '\n', '\v', '\f'),
            @"\A([+-]?)([0-9]*)(?:\.([0-9]*))?(?:[eE]([+-]?[0-9]+))?\z"
        );
        if (!match.Success || match.Groups[2].Length + match.Groups[3].Length == 0)
            throw new FormatException("invalid finite decimal text");
        string sign = match.Groups[1].Value;
        string digits = (match.Groups[2].Value + match.Groups[3].Value).TrimStart('0');
        if (digits.Length == 0) return sign + "0";
        string exponentText = match.Groups[4].Value;
        long exponent = 0;
        if (exponentText.Length != 0 && !long.TryParse(exponentText, NumberStyles.Integer, CultureInfo.InvariantCulture, out exponent))
            exponent = exponentText.StartsWith('-') ? -100000 : 100000;
        exponent = Math.Clamp(exponent, -100000, 100000) - match.Groups[3].Length + digits.Length - 1;
        return sign + digits[0] + "." + digits[1..] + "e" + exponent.ToString(CultureInfo.InvariantCulture);
    }
}
