using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ConnectOnion.Protocol;

/// <summary>
/// Serializes the loose <c>Dictionary&lt;string, object?&gt;</c> trees this client sends on the
/// wire. Sibling of <see cref="CanonicalJson"/> — same hand-written <see cref="Utf8JsonWriter"/>
/// construction, different contract: canonical JSON sorts keys and signs, this one preserves
/// insertion order and ships.
///
/// <para><b>Why this exists rather than <c>JsonSerializer.Serialize(message)</c>, which is what
/// every one of these call sites used before.</b> Publishing with <c>PublishTrimmed=true</c> makes
/// the SDK set the <c>JsonSerializerIsReflectionEnabledByDefault</c> feature switch to false, so
/// the reflection-based serializer does not merely risk trimming away a member — it throws
/// <see cref="NotSupportedException"/> on the first call, for every frame, in the shipping
/// configuration. Those call sites were also the bulk of the app-owned <c>IL2026</c> inventory.
/// The wire shape is defined by the host and is deliberately untyped on our side (see
/// <see cref="InputMessageBuilder"/>), so a <c>JsonSerializerContext</c> has nothing to bind to;
/// writing the document out directly is trim-safe by construction and needs no DTO layer.</para>
///
/// <para>Output is byte-identical to what the reflection serializer produced: default escaping
/// (<c>JavaScriptEncoder.Default</c>, not the relaxed encoder <see cref="CanonicalJson"/> needs
/// to match JS), no indentation, and properties emitted in dictionary enumeration order — which
/// for these insertion-only dictionaries is the order the builders wrote them. Changing any of
/// that changes what the agent receives.</para>
/// </summary>
public static class WireJson
{
    // SkipValidation stays off, unlike CanonicalJson's flat single-object writer: these
    // documents nest arbitrarily deep through WriteValue, so a structural mistake here is
    // worth catching as an exception rather than as a malformed frame the agent rejects.
    private static readonly JsonWriterOptions Options = new() { Indented = false };

    /// <summary>Serializes one outgoing frame.</summary>
    public static string Serialize(IReadOnlyDictionary<string, object?> message)
        => Encoding.UTF8.GetString(SerializeToUtf8Bytes(message));

    /// <summary>Serializes directly to the UTF-8 bytes WebSocket requires.</summary>
    public static byte[] SerializeToUtf8Bytes(IReadOnlyDictionary<string, object?> message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var buffer = new ArrayBufferWriter<byte>();
        WriteTo(buffer, message);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Writes a frame into caller-owned storage. The WebSocket path uses this overload so
    /// it can send <see cref="ArrayBufferWriter{T}.WrittenMemory"/> directly instead of copying a
    /// potentially multi-megabyte attachment frame into a second byte array.</summary>
    internal static void WriteTo(
        IBufferWriter<byte> destination,
        IReadOnlyDictionary<string, object?> message)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(message);

        using var writer = new Utf8JsonWriter(destination, Options);
        WriteObject(writer, message);
    }

    /// <summary>
    /// Serializes a flat string map on its own. Used for the <c>ask_user</c> field-form answer,
    /// which the protocol carries as a JSON <i>string</i> inside the frame rather than as a
    /// nested object.
    /// </summary>
    public static string SerializeStringMap(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, Options))
        {
            writer.WriteStartObject();
            foreach (var pair in values)
            {
                writer.WritePropertyName(pair.Key);
                writer.WriteStringValue(pair.Value);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteObject(Utf8JsonWriter writer, IReadOnlyDictionary<string, object?> map)
    {
        writer.WriteStartObject();
        foreach (var pair in map)
        {
            writer.WritePropertyName(pair.Key);
            WriteValue(writer, pair.Value);
        }
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes one value. The accepted set is closed on purpose, and the <c>default</c> arm throws
    /// rather than falling back to <c>ToString</c> or writing an empty object: an unhandled type
    /// means a builder started putting something new on the wire, and a loud failure in the unit
    /// tests is the point at which that gets noticed. Silently emitting the wrong shape would
    /// surface as an agent-side parse error with no local trace.
    /// </summary>
    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case decimal m:
                writer.WriteNumberValue(m);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;

            // A raw element already carries its own formatting; copying it verbatim also keeps
            // any number's original precision, which round-tripping through double would not.
            case JsonElement element:
                element.WriteTo(writer);
                break;

            case IReadOnlyDictionary<string, object?> nested:
                WriteObject(writer, nested);
                break;

            // Matched before the general sequence arm below so a string map does not degrade
            // into an array of key/value pairs.
            case IReadOnlyDictionary<string, string> strings:
                writer.WriteStartObject();
                foreach (var pair in strings)
                {
                    writer.WritePropertyName(pair.Key);
                    writer.WriteStringValue(pair.Value);
                }
                writer.WriteEndObject();
                break;

            // Covers string[] (a multi-select ask_user answer) and the image data-URL list.
            case IEnumerable<string> texts:
                writer.WriteStartArray();
                foreach (var text in texts) writer.WriteStringValue(text);
                writer.WriteEndArray();
                break;

            // Covers the file-attachment list, which is a list of dictionaries.
            case IEnumerable<object?> items:
                writer.WriteStartArray();
                foreach (var item in items) WriteValue(writer, item);
                writer.WriteEndArray();
                break;

            default:
                throw new NotSupportedException(
                    $"Unsupported wire JSON value type: {value.GetType()}. Add an arm to " +
                    $"{nameof(WireJson)}.{nameof(WriteValue)} rather than reintroducing the " +
                    "reflection-based serializer, which throws under trimming.");
        }
    }
}
