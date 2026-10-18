using System.Buffers;
using System.Text.Json;

namespace Spyglass.Proxy.Export;

/// <summary>
/// NDJSON record serialization (02 §4). One writer per observer worker thread; each record becomes one
/// byte[] line (no trailing newline) handed to the sender.
/// </summary>
internal sealed class RecordWriter
{
    private readonly ArrayBufferWriter<byte> _buffer = new(4096);
    private readonly Utf8JsonWriter _json;

    public RecordWriter()
    {
        _json = new Utf8JsonWriter(_buffer, new JsonWriterOptions { SkipValidation = false, Indented = false });
    }

    public Utf8JsonWriter Begin(string type, long ts, string src)
    {
        _buffer.ResetWrittenCount();
        _json.Reset(_buffer);
        _json.WriteStartObject();
        _json.WriteString("type", type);
        _json.WriteNumber("ts", ts);
        _json.WriteString("src", src);
        return _json;
    }

    public byte[] End()
    {
        _json.WriteEndObject();
        _json.Flush();
        return _buffer.WrittenSpan.ToArray();
    }
}

internal static class JsonExtensions
{
    public static void WriteStringOrNull(this Utf8JsonWriter w, string name, string? value)
    {
        if (value == null) w.WriteNull(name);
        else w.WriteString(name, value);
    }

    public static void WriteNumberOrNull(this Utf8JsonWriter w, string name, long? value)
    {
        if (value is { } v) w.WriteNumber(name, v);
        else w.WriteNull(name);
    }

    public static void WriteNumberOrNull(this Utf8JsonWriter w, string name, double? value)
    {
        if (value is { } v) w.WriteNumber(name, v);
        else w.WriteNull(name);
    }
}
