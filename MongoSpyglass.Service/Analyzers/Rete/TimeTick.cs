// MongoSpyglass.Service/Analyzers/Rete/TimeTick.cs
using System;

namespace MongoSpyglass.Service.Analyzers.Rete;

public class TimeTick
{
    public DateTime CurrentTime { get; set; } = DateTime.UtcNow;
}
