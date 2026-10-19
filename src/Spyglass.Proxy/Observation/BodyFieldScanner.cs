using System.Buffers.Binary;
using Spyglass.Proxy.Wire;

namespace Spyglass.Proxy.Observation;

/// <summary>
/// Streams over the elements of a request's command document (after its length prefix) and keeps the
/// fields drivers put at the end — <c>$db</c>, <c>lsid.id</c>, <c>txnNumber</c> — so they survive a
/// capture truncated at <c>commandBytes</c> (e.g. Node 6.x sends <c>insert.documents</c> inline, ahead of
/// <c>$db</c>). Everything else is skipped by length prefix. Zero allocation; reused per frame.
/// </summary>
public sealed class BodyFieldScanner
{
    public const int DbCap = 128;

    private enum S : byte { Type, Name, Fixed, Len, Capture, Skip, Regex, Done }
    private enum Plan : byte { Skip, Db, TxnNumber, LsidId, DescendLsid }

    public readonly byte[] Db = new byte[DbCap];
    public int DbLength = -1;
    public long? TxnNumber;
    public readonly byte[] LsidId = new byte[16];
    public int LsidIdLength = -1;

    private S _s;
    private int _depth;
    private byte _type;
    private readonly byte[] _name = new byte[16];
    private int _nameLen;
    private Plan _plan;
    private readonly byte[] _scratch = new byte[8];
    private int _need, _have;
    private long _skip;
    private int _regexNuls;
    private byte[]? _captureInto;
    private int _captureLen, _captureSkipFirst, _captureDone;

    public void Begin()
    {
        DbLength = LsidIdLength = -1;
        TxnNumber = null;
        _depth = 0;
        _have = 0;
        _s = S.Type;
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        var i = 0;
        while (i < data.Length && _s != S.Done)
        {
            switch (_s)
            {
                case S.Type:
                    _type = data[i++];
                    if (_type == 0)
                    {
                        if (_depth == 0) _s = S.Done;
                        else _depth--;
                        break;
                    }
                    _nameLen = 0;
                    _s = S.Name;
                    break;

                case S.Name:
                {
                    var rest = data[i..];
                    var nul = rest.IndexOf((byte)0);
                    var take = nul < 0 ? rest.Length : nul;
                    if (_nameLen + take <= _name.Length) rest[..take].CopyTo(_name.AsSpan(_nameLen));
                    _nameLen += take;
                    i += take;
                    if (nul >= 0)
                    {
                        i++;
                        StartValue();
                    }
                    break;
                }

                case S.Fixed:
                case S.Len:
                {
                    var n = Math.Min(_need - _have, data.Length - i);
                    data.Slice(i, n).CopyTo(_scratch.AsSpan(_have));
                    _have += n;
                    i += n;
                    if (_have < _need) break;
                    _have = 0;
                    if (_s == S.Fixed)
                    {
                        if (_plan == Plan.TxnNumber && _type == BsonType.Int64)
                            TxnNumber = BinaryPrimitives.ReadInt64LittleEndian(_scratch);
                        _s = S.Type;
                    }
                    else OnLength(BinaryPrimitives.ReadInt32LittleEndian(_scratch));
                    break;
                }

                case S.Capture:
                {
                    // bytes: [skipFirst bytes][captureLen bytes][rest to skip]
                    var n = (int)Math.Min(_skip, data.Length - i);
                    for (var k = 0; k < n; k++)
                    {
                        var pos = _captureDone + k - _captureSkipFirst;
                        if (pos >= 0 && pos < _captureLen) _captureInto![pos] = data[i + k];
                    }
                    _captureDone += n;
                    _skip -= n;
                    i += n;
                    if (_skip == 0)
                    {
                        if (_plan == Plan.Db) DbLength = _captureLen;
                        else LsidIdLength = _captureLen;
                        _s = S.Type;
                    }
                    break;
                }

                case S.Skip:
                {
                    var n = (int)Math.Min(_skip, data.Length - i);
                    i += n;
                    _skip -= n;
                    if (_skip == 0) _s = S.Type;
                    break;
                }

                case S.Regex:
                    if (data[i++] == 0 && ++_regexNuls == 2) _s = S.Type;
                    break;
            }
        }
    }

    private bool NameIs(ReadOnlySpan<byte> s) => _nameLen == s.Length && _name.AsSpan(0, _nameLen).SequenceEqual(s);

    private void StartValue()
    {
        _plan = Plan.Skip;
        if (_depth == 0)
        {
            if (NameIs("$db"u8) && _type == BsonType.String) _plan = Plan.Db;
            else if (NameIs("txnNumber"u8)) _plan = Plan.TxnNumber;
            else if (NameIs("lsid"u8) && _type == BsonType.Document) _plan = Plan.DescendLsid;
        }
        else if (NameIs("id"u8) && _type == BsonType.Binary) _plan = Plan.LsidId;

        _have = 0;
        switch (_type)
        {
            case BsonType.Double or BsonType.DateTime or BsonType.Timestamp or BsonType.Int64: Fixed(8); break;
            case BsonType.Int32: Fixed(4); break;
            case BsonType.Boolean: Fixed(1); break;
            case BsonType.ObjectId: Fixed(12); break;
            case BsonType.Decimal128: Skip(16); break;
            case BsonType.Undefined or BsonType.Null or BsonType.MinKey or BsonType.MaxKey: _s = S.Type; break;
            case BsonType.Regex: _regexNuls = 0; _s = S.Regex; break;
            case BsonType.String or BsonType.JavaScript or BsonType.Symbol or BsonType.Document or BsonType.Array
                or BsonType.JavaScriptWithScope or BsonType.Binary or BsonType.DbPointer:
                _need = 4;
                _s = S.Len;
                break;
            default: _s = S.Done; break;
        }
    }

    private void Fixed(int n)
    {
        if (n > _scratch.Length) { Skip(n); return; }
        _need = n;
        _s = S.Fixed;
    }

    private void Skip(long n)
    {
        if (n <= 0) { _s = S.Type; return; }
        _skip = n;
        _s = S.Skip;
    }

    private void OnLength(int len)
    {
        switch (_type)
        {
            case BsonType.Document or BsonType.Array or BsonType.JavaScriptWithScope:
                if (len < 5) { _s = S.Done; return; }
                if (_plan == Plan.DescendLsid) { _depth = 1; _s = S.Type; return; }
                Skip(len - 4L);
                return;
            case BsonType.Binary:
                if (len < 0) { _s = S.Done; return; }
                if (_plan == Plan.LsidId && len <= LsidId.Length)
                {
                    StartCapture(LsidId, skipFirst: 1, captureLen: len, total: len + 1L); // subtype byte, then data
                    return;
                }
                Skip(len + 1L);
                return;
            case BsonType.DbPointer:
                Skip(len + 12L);
                return;
            default: // string-like
                if (len < 1) { _s = S.Done; return; }
                if (_plan == Plan.Db)
                {
                    StartCapture(Db, skipFirst: 0, captureLen: Math.Min(DbCap, len - 1), total: len);
                    return;
                }
                Skip(len);
                return;
        }
    }

    private void StartCapture(byte[] into, int skipFirst, int captureLen, long total)
    {
        _captureInto = into;
        _captureSkipFirst = skipFirst;
        _captureLen = captureLen;
        _captureDone = 0;
        _skip = total;
        _s = S.Capture;
    }
}
