using Raven.Client.Documents.Indexes;
using System.Linq;

namespace MongoSpyglass.Service.Data;

public class Operations_ByCommandAndCollection : AbstractIndexCreationTask<MongoOperation, Operations_ByCommandAndCollection.Result>
{
    public class Result
    {
        public string SessionId { get; set; } = string.Empty;
        public string Collection { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public int Count { get; set; }
        public double MaxDurationMs { get; set; }
        public double TotalDurationMs { get; set; }
        public long TotalSizeBytes { get; set; }
        public long TotalDocumentCount { get; set; }
    }

    public Operations_ByCommandAndCollection()
    {
        Map = ops => from op in ops
                     select new Result
                     {
                         SessionId = op.SessionId,
                         Collection = op.Collection,
                         Command = op.Command,
                         Count = 1,
                         MaxDurationMs = op.DurationMs ?? 0,
                         TotalDurationMs = op.DurationMs ?? 0,
                         TotalSizeBytes = op.SizeBytes,
                         TotalDocumentCount = op.DocumentCount
                     };

        Reduce = results => from result in results
                            group result by new { result.SessionId, result.Collection, result.Command } into g
                            select new Result
                            {
                                SessionId = g.Key.SessionId,
                                Collection = g.Key.Collection,
                                Command = g.Key.Command,
                                Count = g.Sum(x => x.Count),
                                MaxDurationMs = g.Max(x => x.MaxDurationMs),
                                TotalDurationMs = g.Sum(x => x.TotalDurationMs),
                                TotalSizeBytes = g.Sum(x => x.TotalSizeBytes),
                                TotalDocumentCount = g.Sum(x => x.TotalDocumentCount)
                            };
    }
}
