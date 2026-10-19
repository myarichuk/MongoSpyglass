using System.Buffers.Binary;
using Spyglass.Proxy.Wire;

namespace Spyglass.Proxy.Observation;

/// <summary>Facts extracted from a reply without buffering it (01 §10). Raw string bytes; the observer decodes them.</summary>
public sealed class ReplyFacts
{
    public const int ErrmsgCap = 256, CodeNameCap = 64, NsCap = 256, MaxIds = 16;

    public bool Parsed;          // the first document's top level was walked to its end
    public double? Ok;
    public int? Code;
    public readonly byte[] CodeName = new byte[CodeNameCap];
    public int CodeNameLength = -1;
    public readonly byte[] Errmsg = new byte[ErrmsgCap];
    public int ErrmsgLength = -1;
    public long? CursorId;
    public readonly byte[] Ns = new byte[NsCap];
    public int NsLength = -1;
    public int? NReturned;
    public long? N;
    public long? NModified;
    public int WriteErrors;
    public bool WriteConcernError;
    public int Upserted;
    public int CursorsKilledCount, CursorsNotFoundCount;
    public readonly long[] CursorsKilled = new long[MaxIds];
    public readonly long[] CursorsNotFound = new long[MaxIds];

    public void Reset()
    {
        Parsed = false;
        Ok = null;
        Code = null;
        CodeNameLength = ErrmsgLength = NsLength = -1;
        CursorId = null;
        NReturned = null;
        N = NModified = null;
        WriteErrors = Upserted = 0;
        WriteConcernError = false;
        CursorsKilledCount = CursorsNotFoundCount = 0;
    }

    public void CopyFrom(ReplyFacts o)
    {
        Parsed = o.Parsed; Ok = o.Ok; Code = o.Code;
        CodeNameLength = o.CodeNameLength; o.CodeName.AsSpan(0, Math.Max(0, o.CodeNameLength)).CopyTo(CodeName);
        ErrmsgLength = o.ErrmsgLength; o.Errmsg.AsSpan(0, Math.Max(0, o.ErrmsgLength)).CopyTo(Errmsg);
        NsLength = o.NsLength; o.Ns.AsSpan(0, Math.Max(0, o.NsLength)).CopyTo(Ns);
        CursorId = o.CursorId; NReturned = o.NReturned; N = o.N; NModified = o.NModified;
        WriteErrors = o.WriteErrors; WriteConcernError = o.WriteConcernError; Upserted = o.Upserted;
        CursorsKilledCount = o.CursorsKilledCount; CursorsNotFoundCount = o.CursorsNotFoundCount;
        o.CursorsKilled.CopyTo(CursorsKilled, 0);
        o.CursorsNotFound.CopyTo(CursorsNotFound, 0);
    }
}

/// <summary>
/// Resumable state machine over a reply body (bytes after the 16-byte header), fed in arbitrary chunks.
/// Descends only into <c>cursor</c>, the batch arrays and the write/killCursors arrays; everything else is
/// skipped by its length prefix. Zero allocation; reused per frame via <see cref="Begin"/>.
/// </summary>
public sealed class ReplyScanner
{
    private enum S : byte { Prefix, DocLen, ElemType, ElemName, Fixed, Len, StringBody, Skip, Regex, Done }

    private enum Ctx : byte { Top, Cursor, Batch, WriteErrors, Upserted, Killed, NotFound, Skip }

    private enum Plan : byte
    {
        Skip,
        Ok, Code, N, NModified, CursorId, KilledId, NotFoundId,  // fixed numerics
        CodeName, Errmsg, Ns,                                     // captured strings
        Descend,
        WriteConcernError,
    }

    private ReplyFacts _f = null!;
    private S _s;
    private int _prefixNeed;
    private readonly Ctx[] _stack = new Ctx[4];
    private int _depth;

    private byte _type;
    private readonly byte[] _name = new byte[24];
    private int _nameLen;            // > _name.Length means "too long to be interesting"
    private Plan _plan;
    private Ctx _descendInto;
    private readonly byte[] _scratch = new byte[16];
    private int _need, _have;
    private long _skip;
    private int _regexNuls;
    private int _strLen, _strCopied;

    public void Begin(int opCode, ReplyFacts facts)
    {
        _f = facts;
        facts.Reset();
        _depth = 0;
        switch (opCode)
        {
            case OpCodes.Msg:
                _s = S.Prefix;
                _prefixNeed = 5; // flags + section kind
                break;
            case OpCodes.Reply:
                _s = S.Prefix;
                _prefixNeed = 20; // responseFlags, cursorId, startingFrom, numberReturned
                break;
            default:
                _s = S.Done; // OP_COMPRESSED and legacy: opaque
                break;
        }
        _have = 0;
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        var i = 0;
        while (i < data.Length && _s != S.Done)
        {
            switch (_s)
            {
                case S.Prefix:
                {
                    var n = Math.Min(_prefixNeed - _have, data.Length - i);
                    // only OP_MSG's 5-byte prefix (flags + kind) is kept; OP_REPLY's 20 bytes are skipped
                    if (_prefixNeed == 5)
                        data.Slice(i, n).CopyTo(_scratch.AsSpan(_have));
                    _have += n;
                    i += n;
                    if (_have == _prefixNeed)
                    {
                        if (_prefixNeed == 5 && _scratch[4] != 0)
                        {
                            _s = S.Done; // first section is a document sequence: not a command reply shape
                            break;
                        }
                        _have = 0;
                        _s = S.DocLen;
                    }
                    break;
                }

                case S.DocLen:
                {
                    var n = Math.Min(4 - _have, data.Length - i);
                    data.Slice(i, n).CopyTo(_scratch.AsSpan(_have));
                    _have += n;
                    i += n;
                    if (_have == 4)
                    {
                        _stack[0] = Ctx.Top;
                        _depth = 1;
                        _s = S.ElemType;
                    }
                    break;
                }

                case S.ElemType:
                {
                    _type = data[i++];
                    if (_type == 0)
                    {
                        _depth--;
                        if (_depth == 0)
                        {
                            _f.Parsed = true;
                            _s = S.Done;
                        }
                        break;
                    }
                    switch (_stack[_depth - 1])
                    {
                        case Ctx.Batch: _f.NReturned = (_f.NReturned ?? 0) + 1; break;
                        case Ctx.WriteErrors: _f.WriteErrors++; break;
                        case Ctx.Upserted: _f.Upserted++; break;
                    }
                    _nameLen = 0;
                    _s = S.ElemName;
                    break;
                }

                case S.ElemName:
                {
                    var rest = data[i..];
                    var nul = rest.IndexOf((byte)0);
                    var take = nul < 0 ? rest.Length : nul;
                    if (_nameLen + take <= _name.Length)
                        rest[..take].CopyTo(_name.AsSpan(_nameLen));
                    _nameLen += take;
                    i += take;
                    if (nul >= 0)
                    {
                        i++; // NUL
                        StartValue();
                    }
                    break;
                }

                case S.Fixed:
                {
                    var n = Math.Min(_need - _have, data.Length - i);
                    data.Slice(i, n).CopyTo(_scratch.AsSpan(_have));
                    _have += n;
                    i += n;
                    if (_have == _need)
                    {
                        ApplyFixed();
                        _s = S.ElemType;
                    }
                    break;
                }

                case S.Len:
                {
                    var n = Math.Min(4 - _have, data.Length - i);
                    data.Slice(i, n).CopyTo(_scratch.AsSpan(_have));
                    _have += n;
                    i += n;
                    if (_have == 4)
                        OnLength(BinaryPrimitives.ReadInt32LittleEndian(_scratch));
                    break;
                }

                case S.StringBody:
                {
                    var n = Math.Min(_strLen - _strCopied, data.Length - i);
                    var (dest, cap) = _plan switch
                    {
                        Plan.CodeName => (_f.CodeName, ReplyFacts.CodeNameCap),
                        Plan.Errmsg => (_f.Errmsg, ReplyFacts.ErrmsgCap),
                        _ => (_f.Ns, ReplyFacts.NsCap),
                    };
                    var copy = Math.Clamp(Math.Min(cap, _strLen - 1) - _strCopied, 0, n); // never the trailing NUL
                    if (copy > 0)
                        data.Slice(i, copy).CopyTo(dest.AsSpan(_strCopied));
                    _strCopied += n;
                    i += n;
                    if (_strCopied == _strLen)
                    {
                        var len = Math.Min(cap, _strLen - 1);
                        switch (_plan)
                        {
                            case Plan.CodeName: _f.CodeNameLength = len; break;
                            case Plan.Errmsg: _f.ErrmsgLength = len; break;
                            default: _f.NsLength = len; break;
                        }
                        _s = S.ElemType;
                    }
                    break;
                }

                case S.Skip:
                {
                    var n = (int)Math.Min(_skip, data.Length - i);
                    i += n;
                    _skip -= n;
                    if (_skip == 0)
                        _s = S.ElemType;
                    break;
                }

                case S.Regex:
                {
                    var b = data[i++];
                    if (b == 0 && ++_regexNuls == 2)
                        _s = S.ElemType;
                    break;
                }
            }
        }
    }

    private bool NameIs(ReadOnlySpan<byte> s) => _nameLen == s.Length && _name.AsSpan(0, _nameLen).SequenceEqual(s);

    private void StartValue()
    {
        _plan = Plan.Skip;
        var ctx = _stack[_depth - 1];
        if (_depth < _stack.Length)
            ChoosePlan(ctx);

        _have = 0;
        switch (_type)
        {
            case BsonType.Double:
            case BsonType.DateTime:
            case BsonType.Timestamp:
            case BsonType.Int64:
                StartFixed(8);
                break;
            case BsonType.Int32:
                StartFixed(4);
                break;
            case BsonType.Boolean:
                StartFixed(1);
                break;
            case BsonType.ObjectId:
                StartFixed(12);
                break;
            case BsonType.Decimal128:
                StartFixed(16);
                break;
            case BsonType.Undefined:
            case BsonType.Null:
            case BsonType.MinKey:
            case BsonType.MaxKey:
                _s = S.ElemType;
                break;
            case BsonType.Regex:
                _regexNuls = 0;
                _s = S.Regex;
                break;
            case BsonType.String:
            case BsonType.JavaScript:
            case BsonType.Symbol:
            case BsonType.Document:
            case BsonType.Array:
            case BsonType.JavaScriptWithScope:
            case BsonType.Binary:
            case BsonType.DbPointer:
                _s = S.Len;
                break;
            default:
                _s = S.Done; // unknown type: we can no longer frame the document
                break;
        }
    }

    private void StartFixed(int n)
    {
        _need = n;
        _s = S.Fixed;
    }

    private void ChoosePlan(Ctx ctx)
    {
        switch (ctx)
        {
            case Ctx.Top:
                if (NameIs("ok"u8)) _plan = Plan.Ok;
                else if (NameIs("code"u8)) _plan = Plan.Code;
                else if (NameIs("codeName"u8) && _type == BsonType.String) _plan = Plan.CodeName;
                else if (NameIs("errmsg"u8) && _type == BsonType.String) _plan = Plan.Errmsg;
                else if (NameIs("n"u8)) _plan = Plan.N;
                else if (NameIs("nModified"u8)) _plan = Plan.NModified;
                else if (NameIs("writeConcernError"u8)) _plan = Plan.WriteConcernError;
                else if (_type == BsonType.Document && NameIs("cursor"u8)) Descend(Ctx.Cursor);
                else if (_type == BsonType.Array && NameIs("writeErrors"u8)) Descend(Ctx.WriteErrors);
                else if (_type == BsonType.Array && NameIs("upserted"u8)) Descend(Ctx.Upserted);
                else if (_type == BsonType.Array && NameIs("cursorsKilled"u8)) Descend(Ctx.Killed);
                else if (_type == BsonType.Array && NameIs("cursorsNotFound"u8)) Descend(Ctx.NotFound);
                break;
            case Ctx.Cursor:
                if (NameIs("id"u8)) _plan = Plan.CursorId;
                else if (NameIs("ns"u8) && _type == BsonType.String) _plan = Plan.Ns;
                else if (_type == BsonType.Array && (NameIs("firstBatch"u8) || NameIs("nextBatch"u8)))
                {
                    _f.NReturned ??= 0;
                    Descend(Ctx.Batch);
                }
                break;
            case Ctx.Killed:
                _plan = Plan.KilledId;
                break;
            case Ctx.NotFound:
                _plan = Plan.NotFoundId;
                break;
        }
    }

    private void Descend(Ctx into)
    {
        _plan = Plan.Descend;
        _descendInto = into;
    }

    private void ApplyFixed()
    {
        var s = _scratch.AsSpan(0, _need);
        long? asLong = _type switch
        {
            BsonType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(s),
            BsonType.Int64 => BinaryPrimitives.ReadInt64LittleEndian(s),
            BsonType.Double => (long)BinaryPrimitives.ReadDoubleLittleEndian(s),
            BsonType.Boolean => s[0],
            _ => null
        };
        switch (_plan)
        {
            case Plan.Ok:
                _f.Ok = _type switch
                {
                    BsonType.Double => BinaryPrimitives.ReadDoubleLittleEndian(s),
                    _ => asLong
                };
                break;
            case Plan.Code: _f.Code = (int?)asLong; break;
            case Plan.N: _f.N = asLong; break;
            case Plan.NModified: _f.NModified = asLong; break;
            case Plan.CursorId: _f.CursorId = asLong; break;
            case Plan.KilledId:
                if (asLong is { } k && _f.CursorsKilledCount < ReplyFacts.MaxIds) _f.CursorsKilled[_f.CursorsKilledCount] = k;
                _f.CursorsKilledCount++;
                break;
            case Plan.NotFoundId:
                if (asLong is { } nf && _f.CursorsNotFoundCount < ReplyFacts.MaxIds) _f.CursorsNotFound[_f.CursorsNotFoundCount] = nf;
                _f.CursorsNotFoundCount++;
                break;
        }
    }

    private void OnLength(int len)
    {
        switch (_type)
        {
            case BsonType.Document:
            case BsonType.Array:
            case BsonType.JavaScriptWithScope:
                if (len < 5) { _s = S.Done; return; }
                if (_plan == Plan.WriteConcernError)
                    _f.WriteConcernError = true;
                if (_plan == Plan.Descend && _type != BsonType.JavaScriptWithScope)
                {
                    _stack[_depth++] = _descendInto;
                    _s = S.ElemType;
                    return;
                }
                BeginSkip(len - 4L);
                return;
            case BsonType.Binary:
                if (len < 0) { _s = S.Done; return; }
                BeginSkip(len + 1L);
                return;
            case BsonType.DbPointer:
                if (len < 1) { _s = S.Done; return; }
                BeginSkip(len + 12L);
                return;
            default: // string, code, symbol
                if (len < 1) { _s = S.Done; return; }
                if (_plan is Plan.CodeName or Plan.Errmsg or Plan.Ns)
                {
                    _strLen = len;
                    _strCopied = 0;
                    _s = S.StringBody;
                    return;
                }
                BeginSkip(len);
                return;
        }
    }

    private void BeginSkip(long n)
    {
        if (n == 0)
        {
            _s = S.ElemType;
            return;
        }
        _skip = n;
        _s = S.Skip;
    }
}
