using System.Buffers;
using System.Text.Json;

namespace Spyglass.Proxy.Export;

/// <summary>A serialized record line (no trailing newline); valid until the producing writer's next record.</summary>
internal delegate void RecordSink(ReadOnlySpan<byte> line);

/// <summary>
/// NDJSON record serialization (02 §4). One writer per observer worker thread, reused for every record;
/// <see cref="End"/> returns the line as a span over the writer's buffer, copied by the consumer.
/// </summary>
internal sealed class RecordWriter
{
    private readonly ArrayBufferWriter<byte> _buffer = new(4096);
    private readonly Utf8JsonWriter _json;

    public RecordWriter()
    {
        // Structural validation is a per-token cost; the Debug build (all tests) keeps it.
#if DEBUG
        const bool skipValidation = false;
#else
        const bool skipValidation = true;
#endif
        _json = new Utf8JsonWriter(_buffer, new JsonWriterOptions { SkipValidation = skipValidation, Indented = false });
    }

    public Utf8JsonWriter Begin(string type, long ts, string src)
    {
        _buffer.ResetWrittenCount();
        _json.Reset(_buffer);
        _json.WriteStartObject();
        _json.WriteString(J.type, type);
        _json.WriteNumber(J.ts, ts);
        _json.WriteString(J.src, src);
        return _json;
    }

    public ReadOnlySpan<byte> End()
    {
        _json.WriteEndObject();
        _json.Flush();
        return _buffer.WrittenSpan;
    }
}

/// <summary>Pre-encoded property names: no UTF-16 transcoding or escaping scan per record.</summary>
internal static class J
{
    public static readonly JsonEncodedText app = JsonEncodedText.Encode("app");
    public static readonly JsonEncodedText bytesIn = JsonEncodedText.Encode("bytesIn");
    public static readonly JsonEncodedText bytesOut = JsonEncodedText.Encode("bytesOut");
    public static readonly JsonEncodedText client = JsonEncodedText.Encode("client");
    public static readonly JsonEncodedText closeReason = JsonEncodedText.Encode("closeReason");
    public static readonly JsonEncodedText cmdTruncated = JsonEncodedText.Encode("cmdTruncated");
    public static readonly JsonEncodedText cmd = JsonEncodedText.Encode("cmd");
    public static readonly JsonEncodedText code = JsonEncodedText.Encode("code");
    public static readonly JsonEncodedText codeName = JsonEncodedText.Encode("codeName");
    public static readonly JsonEncodedText coll = JsonEncodedText.Encode("coll");
    public static readonly JsonEncodedText command = JsonEncodedText.Encode("command");
    public static readonly JsonEncodedText comment = JsonEncodedText.Encode("comment");
    public static readonly JsonEncodedText compressor = JsonEncodedText.Encode("compressor");
    public static readonly JsonEncodedText connSeq = JsonEncodedText.Encode("connSeq");
    public static readonly JsonEncodedText cursorId = JsonEncodedText.Encode("cursorId");
    public static readonly JsonEncodedText cursorsKilled = JsonEncodedText.Encode("cursorsKilled");
    public static readonly JsonEncodedText cursorsNotFound = JsonEncodedText.Encode("cursorsNotFound");
    public static readonly JsonEncodedText db = JsonEncodedText.Encode("db");
    public static readonly JsonEncodedText driver = JsonEncodedText.Encode("driver");
    public static readonly JsonEncodedText driverVersion = JsonEncodedText.Encode("driverVersion");
    public static readonly JsonEncodedText errmsg = JsonEncodedText.Encode("errmsg");
    public static readonly JsonEncodedText @event = JsonEncodedText.Encode("event");
    public static readonly JsonEncodedText exporter = JsonEncodedText.Encode("exporter");
    public static readonly JsonEncodedText invalidHeader = JsonEncodedText.Encode("invalidHeader");
    public static readonly JsonEncodedText lsid = JsonEncodedText.Encode("lsid");
    public static readonly JsonEncodedText member = JsonEncodedText.Encode("member");
    public static readonly JsonEncodedText n = JsonEncodedText.Encode("n");
    public static readonly JsonEncodedText nModified = JsonEncodedText.Encode("nModified");
    public static readonly JsonEncodedText nReturned = JsonEncodedText.Encode("nReturned");
    public static readonly JsonEncodedText ok = JsonEncodedText.Encode("ok");
    public static readonly JsonEncodedText opCode = JsonEncodedText.Encode("opCode");
    public static readonly JsonEncodedText os = JsonEncodedText.Encode("os");
    public static readonly JsonEncodedText parsed = JsonEncodedText.Encode("parsed");
    public static readonly JsonEncodedText platform = JsonEncodedText.Encode("platform");
    public static readonly JsonEncodedText reply = JsonEncodedText.Encode("reply");
    public static readonly JsonEncodedText reqBytes = JsonEncodedText.Encode("reqBytes");
    public static readonly JsonEncodedText reqLast_us = JsonEncodedText.Encode("reqLast_us");
    public static readonly JsonEncodedText requestId = JsonEncodedText.Encode("requestId");
    public static readonly JsonEncodedText respBytes = JsonEncodedText.Encode("respBytes");
    public static readonly JsonEncodedText respFirst_us = JsonEncodedText.Encode("respFirst_us");
    public static readonly JsonEncodedText respLast_us = JsonEncodedText.Encode("respLast_us");
    public static readonly JsonEncodedText seq = JsonEncodedText.Encode("seq");
    public static readonly JsonEncodedText seqDocs = JsonEncodedText.Encode("seqDocs");
    public static readonly JsonEncodedText serverConnectionId = JsonEncodedText.Encode("serverConnectionId");
    public static readonly JsonEncodedText src = JsonEncodedText.Encode("src");
    public static readonly JsonEncodedText status = JsonEncodedText.Encode("status");
    public static readonly JsonEncodedText stream = JsonEncodedText.Encode("stream");
    public static readonly JsonEncodedText t = JsonEncodedText.Encode("t");
    public static readonly JsonEncodedText tls = JsonEncodedText.Encode("tls");
    public static readonly JsonEncodedText ts = JsonEncodedText.Encode("ts");
    public static readonly JsonEncodedText txnNumber = JsonEncodedText.Encode("txnNumber");
    public static readonly JsonEncodedText type = JsonEncodedText.Encode("type");
    public static readonly JsonEncodedText upserted = JsonEncodedText.Encode("upserted");
    public static readonly JsonEncodedText writeConcernError = JsonEncodedText.Encode("writeConcernError");
    public static readonly JsonEncodedText writeErrors = JsonEncodedText.Encode("writeErrors");
}

internal static class JsonExtensions
{
    // 02 §4: in wire records an absent field means null (or the field's default); these omit it.

    public static void WriteIfNotNull(this Utf8JsonWriter w, JsonEncodedText name, string? value)
    {
        if (value != null) w.WriteString(name, value);
    }

    public static void WriteIfNotNull(this Utf8JsonWriter w, JsonEncodedText name, long? value)
    {
        if (value is { } v) w.WriteNumber(name, v);
    }

    public static void WriteIfNotNull(this Utf8JsonWriter w, JsonEncodedText name, double? value)
    {
        if (value is { } v) w.WriteNumber(name, v);
    }

    public static void WriteIfNonZero(this Utf8JsonWriter w, JsonEncodedText name, long value)
    {
        if (value != 0) w.WriteNumber(name, value);
    }

    public static void WriteIfTrue(this Utf8JsonWriter w, JsonEncodedText name, bool value)
    {
        if (value) w.WriteBoolean(name, true);
    }
}
