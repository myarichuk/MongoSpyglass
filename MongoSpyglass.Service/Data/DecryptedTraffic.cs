namespace MongoSpyglass.Service.Data;

public class DecryptedTraffic
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Tag { get; set; } = string.Empty;
    public int RequestId { get; set; }
    public string OpCode { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string Collection { get; set; } = string.Empty;
    public byte[]? RawBson { get; set; }
    public byte[]? ResponseBson { get; set; }
    public double? DurationMs { get; set; }
    public int SizeBytes { get; set; }
    public int DocumentCount { get; set; }

    private string? _payloadJson;
    public string PayloadJson => _payloadJson ??= BsonUtils.ToJson(RawBson, OpCode, true);

    private string? _responseJson;
    public string ResponseJson => _responseJson ??= BsonUtils.ToJson(ResponseBson, OpCode, true);

    private string? _summaryJson;
    public string SummaryJson => _summaryJson ??= BsonUtils.ToJson(RawBson, OpCode, false);
}
