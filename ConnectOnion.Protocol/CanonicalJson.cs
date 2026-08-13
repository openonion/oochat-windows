using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ConnectOnion.Protocol;

/// <summary>
/// Produces the exact canonical JSON the ConnectOnion SDK signs: keys sorted,
/// no whitespace, JS-compatible escaping. Port of <c>canonicalJSON</c> in
/// <c>address.ts</c> (which does <c>Object.keys().sort()</c> then
/// <c>JSON.stringify</c>).
///
/// The signature only verifies if this string is BYTE-IDENTICAL to what the JS
/// side produced, so this is validated by a JS↔C# conformance test.
/// </summary>
public static class CanonicalJson
{
    // UnsafeRelaxedJsonEscaping matches JS JSON.stringify, which does not escape
    // '+', '<', '>', '&' etc. System.Text.Json's default encoder would, breaking
    // byte-for-byte equality for payloads containing those characters.
    private static readonly JsonWriterOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // Indented would insert whitespace the JS side never emits — the bytes must match
        // exactly, so every formatting knob here is part of the wire contract, not a style
        // choice. Do not change any of these without re-running the conformance gate.
        Indented = false,
        // Safe here because every value written below comes from the closed set in WriteValue,
        // and the structure is a single flat object this method emits itself.
        SkipValidation = true,
    };

    /// <summary>
    /// Serializes an object with keys sorted by UTF-16 code unit (matching JS
    /// <c>Array.prototype.sort</c> on ASCII keys). Values may be string, bool,
    /// or an integral/floating number.
    /// </summary>
    public static string Serialize(IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Options))
        {
            writer.WriteStartObject();
            // StringComparer.Ordinal, never the culture-aware default: JS sorts by code unit,
            // and a culture-sensitive comparison would reorder keys on some machines and not
            // others — producing signatures that verify locally and fail elsewhere.
            foreach (var pair in pairs.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(pair.Key);
                WriteValue(writer, pair.Value);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Writes one value. The accepted types are deliberately a short closed list:
    /// anything richer (nested objects, arrays, decimals, DateTime) has no agreed canonical
    /// form across the C# and JS writers, so it would silently produce a payload that signs
    /// here and fails to verify there. Throwing turns that into a build-time discovery.</summary>
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
            default:
                throw new NotSupportedException($"Unsupported canonical JSON value type: {value.GetType()}");
        }
    }
}
