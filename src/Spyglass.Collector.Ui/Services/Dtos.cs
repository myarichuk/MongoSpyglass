using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spyglass.Collector.Ui.Services;
// DTOs mirror the collector's /api/v1 JSON (03 §8). Absent fields are null.

public sealed record OpSummary(
    string Id, long Ts, string ProxyId, string Member, string? App, string Command, string? Db, string? Coll, string Status,
    [property: JsonPropertyName("serverLatency_us")] long? ServerLatencyUs, int? NReturned, int ReqBytes, int RespBytes,
    string ShapeHash, int ReplyCount, string? CodeName, string[]? Alerts, bool Joined = false,
    [property: JsonPropertyName("client_us")] long? ClientUs = null, [property: JsonPropertyName("poolWait_us")] long? PoolWaitUs = null,
    string? UowId = null, string? UowName = null)
{
    public string Ns => Fmt.Ns(Db, Coll);
}

public sealed record OpPage(OpSummary[] Items, string? Next);

public sealed record Timings(
    [property: JsonPropertyName("reqFirst_us")] long ReqFirst, [property: JsonPropertyName("reqLast_us")] long ReqLast,
    [property: JsonPropertyName("respFirst_us")] long? RespFirst, [property: JsonPropertyName("respLast_us")] long? RespLast);

public sealed record CursorDto(string CursorId, string ProxyId, string Member, string OpenOpId, string ShapeHash, string? App, string? Db, string? Coll,
    long OpenedTs, long LastActivityTs, long Documents, int GetMores, string? ClosedReason, long? ClosedTs);

public sealed record OpDetailDto(
    string Id, long Ts, string ProxyId, string Member, long? ServerConnectionId, int RequestId, long ConnSeq, string OpCode, string Command,
    string? Db, string? Coll, string? App, string? Driver, string Status, bool Ack, bool Stream, int ReplyCount, int ReqBytes, int RespBytes,
    [property: JsonPropertyName("serverLatency_us")] long? ServerLatencyUs, Timings Timings, JsonElement? Reply, string? Lsid, long? TxnNumber,
    string? Comment, string ShapeHash, string? ValueHash, JsonElement? Shape, [property: JsonPropertyName("command_ejson")] string? CommandEjson,
    bool CmdTruncated, Dictionary<string, int>? SeqDocs, string? Compressor, string[]? Alerts, CursorDto? Cursor, ContextDto? Context)
{
    public string Ns => Fmt.Ns(Db, Coll);
}

public sealed record UowDto(string? Id, string? Name, string? Kind);
public sealed record FrameDto(string Fn, string? File, int? Line);

/// <summary>The joined exporter <c>cmd</c> (03 §3.3 "from cmd").</summary>
public sealed record ContextDto(string Src, long Ts, string? OperationId, string Status,
    [property: JsonPropertyName("client_us")] long? ClientUs, [property: JsonPropertyName("poolWait_us")] long? PoolWaitUs,
    [property: JsonPropertyName("clientOverhead_us")] long? ClientOverheadUs, int? Attempt, UowDto? Uow, string? Traceparent,
    string? Callsite, string? Lang, FrameDto[]? Frames, string? CursorId, string? ErrorCode, string[]? ErrorLabels);

public sealed record AlertDto(string Id, string Rule, Dictionary<string, string> Scope, string Severity, string State, bool Muted,
    long FirstSeen, long LastSeen, long Occurrences, string[] SampleOpIds, string Summary, string? Details, long? ResolvedAt);

public sealed record OpWithAlerts(OpDetailDto Op, AlertDto[] Alerts);

public sealed record ShapeRow(string ShapeHash, string Command, string? Db, string? Coll, JsonElement Shape, long FirstSeen, long LastSeen,
    string[] Apps, long Count, long Errors, [property: JsonPropertyName("totalTime_us")] double TotalTimeUs,
    [property: JsonPropertyName("p50_us")] double P50Us, [property: JsonPropertyName("p95_us")] double P95Us,
    [property: JsonPropertyName("p99_us")] double P99Us, [property: JsonPropertyName("max_us")] double MaxUs,
    long RespBytes, long Returned, long MaxReturned, int? MaxInCardinality, double[][]? Series, long ReqBytes = 0)
{
    public string Ns => Fmt.Ns(Db, Coll);
}

public sealed record ShapePage(long From, long To, ShapeRow[] Items);

public sealed record SeriesPoint(long Minute, long Count, long Errors, [property: JsonPropertyName("p50_us")] double P50Us, [property: JsonPropertyName("p95_us")] double P95Us);

public sealed record CursorStatsDto(long Closed, long Documents, long GetMores, [property: JsonPropertyName("meanTimeToExhaustion_us")] long? MeanTimeToExhaustionUs, Dictionary<string, long> Reasons);

public sealed record ShapeDetail(string ShapeHash, string Command, string? Db, string? Coll, JsonElement Shape, string[] Apps, long Count, long Errors,
    [property: JsonPropertyName("totalTime_us")] double TotalTimeUs, [property: JsonPropertyName("p50_us")] double P50Us,
    [property: JsonPropertyName("p95_us")] double P95Us, [property: JsonPropertyName("p99_us")] double P99Us, [property: JsonPropertyName("max_us")] double MaxUs,
    SeriesPoint[] Series, OpSummary[] Slowest, OpSummary[] Latest, CursorStatsDto? Cursors,
    long FirstSeen = 0, long LastSeen = 0, long ReqBytes = 0, long RespBytes = 0, long Returned = 0, long MaxReturned = 0)
{
    public string Ns => Fmt.Ns(Db, Coll);
}

public sealed record ProducerDto(string Src, string Kind, string? Version, int Schema, string? Topology, string? SetName, JsonElement Members,
    string? Values, string? Compression, long FirstSeen, long LastSeen, long Records, long Rejected, long ClockSkewed, Dictionary<string, long> Dropped,
    string? Runtime = null, string? Driver = null, string? Host = null, long? Pid = null, string? App = null, string? AppVersion = null, JoinDto? Join = null);

/// <summary>02 §6 per exporter.</summary>
public sealed record JoinDto(long Cmds, long Joined, long Orphans, long Conflicts, double? Rate, [property: JsonPropertyName("skew_us")] long? SkewUs);

