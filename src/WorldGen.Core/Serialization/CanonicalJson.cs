using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WorldGen.Core.Serialization;

public static class CanonicalJson
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
        SkipValidation = false
    };

    public static string Serialize(JsonNode value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteNode(writer, value);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string Hash(JsonNode value)
    {
        var bytes = Encoding.UTF8.GetBytes(Serialize(value));
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static void WriteNode(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                return;
            case JsonObject jsonObject:
                writer.WriteStartObject();
                foreach (var property in jsonObject.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    WriteNode(writer, property.Value);
                }
                writer.WriteEndObject();
                return;
            case JsonArray jsonArray:
                writer.WriteStartArray();
                foreach (var item in jsonArray)
                {
                    WriteNode(writer, item);
                }
                writer.WriteEndArray();
                return;
            case JsonValue jsonValue:
                WriteValue(writer, jsonValue);
                return;
            default:
                throw new InvalidOperationException($"Неподдерживаемый JSON-узел: {node.GetType().Name}");
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonValue value)
    {
        if (value.TryGetValue<JsonElement>(out var element))
        {
            WriteValue(writer, element);
        }
        else if (value.TryGetValue<string>(out var text)) writer.WriteStringValue(text);
        else if (value.TryGetValue<bool>(out var boolean)) writer.WriteBooleanValue(boolean);
        else if (value.TryGetValue<double>(out var number)) writer.WriteRawValue(FormatEcmaScriptNumber(number), true);
        else if (value.TryGetValue<float>(out var single)) writer.WriteRawValue(FormatEcmaScriptNumber(single), true);
        else if (value.TryGetValue<decimal>(out var decimalNumber)) writer.WriteNumberValue(decimalNumber);
        else if (value.TryGetValue<int>(out var integer)) writer.WriteNumberValue(integer);
        else if (value.TryGetValue<uint>(out var unsignedInteger)) writer.WriteNumberValue(unsignedInteger);
        else if (value.TryGetValue<long>(out var longInteger)) writer.WriteNumberValue(longInteger);
        else if (value.TryGetValue<ulong>(out var unsignedLongInteger)) writer.WriteNumberValue(unsignedLongInteger);
        else throw new InvalidOperationException($"Неподдерживаемое JSON-значение: {value.GetType().Name}");
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(FormatEcmaScriptNumber(value.GetDouble()), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException($"Некорректное скалярное JSON-значение: {value.ValueKind}");
        }
    }

    private static string FormatEcmaScriptNumber(double value)
    {
        if (!double.IsFinite(value))
        {
            return "null";
        }

        if (value == 0)
        {
            return "0";
        }

        var negative = value < 0;
        var absolute = Math.Abs(value);
        var roundTrip = absolute.ToString("R", CultureInfo.InvariantCulture);
        var exponentSeparator = roundTrip.IndexOfAny(['E', 'e']);
        var coefficient = exponentSeparator >= 0 ? roundTrip[..exponentSeparator] : roundTrip;
        var exponent = exponentSeparator >= 0
            ? int.Parse(roundTrip[(exponentSeparator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture)
            : 0;
        var decimalSeparator = coefficient.IndexOf('.');
        var decimalPosition = (decimalSeparator >= 0 ? decimalSeparator : coefficient.Length) + exponent;
        var digits = coefficient.Replace(".", string.Empty, StringComparison.Ordinal);

        var leadingZeroCount = 0;
        while (leadingZeroCount < digits.Length - 1 && digits[leadingZeroCount] == '0')
        {
            leadingZeroCount++;
        }

        if (leadingZeroCount > 0)
        {
            digits = digits[leadingZeroCount..];
            decimalPosition -= leadingZeroCount;
        }

        string formatted;
        if (absolute >= 1e-6 && absolute < 1e21)
        {
            formatted = decimalPosition switch
            {
                <= 0 => $"0.{new string('0', -decimalPosition)}{digits}",
                _ when decimalPosition >= digits.Length => digits + new string('0', decimalPosition - digits.Length),
                _ => digits.Insert(decimalPosition, ".")
            };
        }
        else
        {
            var scientificExponent = decimalPosition - 1;
            var mantissa = digits.Length == 1 ? digits : $"{digits[0]}.{digits[1..]}";
            var sign = scientificExponent >= 0 ? "+" : string.Empty;
            formatted = $"{mantissa}e{sign}{scientificExponent}";
        }

        return negative ? $"-{formatted}" : formatted;
    }
}
