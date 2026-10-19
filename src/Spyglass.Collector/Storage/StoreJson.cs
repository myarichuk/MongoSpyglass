using System.Text.Json.Serialization;
using Spyglass.Collector.Analysis;

namespace Spyglass.Collector.Storage;

internal sealed record StoredFrame(string Fn, string? File, int? Line);
internal sealed record StoredCallsite(string? Lang, List<StoredFrame> Frames);

/// <summary>
/// The JSON the SQLite store keeps (shapes, alerts, callsites), source-generated: no reflection, so the trimmed
/// container build keeps exactly what it needs. Fields included, as the entities are field-based.
/// </summary>
[JsonSourceGenerationOptions(IncludeFields = true)]
[JsonSerializable(typeof(ShapeInfo))]
[JsonSerializable(typeof(Alert))]
[JsonSerializable(typeof(StoredCallsite))]
internal sealed partial class StoreJson : JsonSerializerContext;
