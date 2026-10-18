using System.Buffers.Binary;

namespace Spyglass.Proxy.Observation;

/// <summary>
/// Counts the documents in each OP_MSG kind-1 section of a request as it streams through (01 §9.4):
/// the capture holds at most <c>commandBytes</c>, but <c>seqDocs</c> must count every document.
/// Zero allocation; reused per frame.
/// </summary>
public sealed class SequenceScanner
{
    public const int MaxSections = 4, MaxIdentifier = 32;

    private enum S : byte { Flags, Kind, Len4, Skip, SeqId, Done }
    private enum LenFor : byte { BodyDoc, SeqSize, SeqDoc }

    public int SectionCount;
    public readonly int[] DocCounts = new int[MaxSections];
    public readonly byte[][] Identifiers = [new byte[MaxIdentifier], new byte[MaxIdentifier], new byte[MaxIdentifier], new byte[MaxIdentifier]];
    public readonly int[] IdentifierLengths = new int[MaxSections];

    private S _s;
    private LenFor _lenFor;
    private readonly byte[] _scratch = new byte[4];
    private int _have;
    private long _remainingBody; // bytes of sections left (excludes checksum)
    private long _skip;
    private long _seqRemaining;  // bytes left in the current kind-1 section
    private int _section = -1;
    private bool _checksum;

    public void Begin(int opCode, int bodyLength)
    {
        SectionCount = 0;
        _section = -1;
        _have = 0;
        _remainingBody = bodyLength;
        _s = opCode == Wire.OpCodes.Msg ? S.Flags : S.Done;
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        var i = 0;
        while (i < data.Length && _s != S.Done)
        {
            switch (_s)
            {
                case S.Flags:
                case S.Len4:
                {
                    var n = Math.Min(4 - _have, data.Length - i);
                    data.Slice(i, n).CopyTo(_scratch.AsSpan(_have));
                    _have += n;
                    i += n;
                    Consume(n);
                    if (_have < 4) break;
                    _have = 0;
                    var v = BinaryPrimitives.ReadInt32LittleEndian(_scratch);
                    if (_s == S.Flags)
                    {
                        _checksum = (v & 1) != 0;
                        if (_checksum) _remainingBody -= 4;
                        _s = _remainingBody > 0 ? S.Kind : S.Done;
                        break;
                    }
                    OnLength(v);
                    break;
                }
                case S.Kind:
                {
                    var kind = data[i++];
                    Consume(1);
                    if (kind == 0)
                    {
                        _lenFor = LenFor.BodyDoc;
                        _s = S.Len4;
                    }
                    else if (kind == 1)
                    {
                        _lenFor = LenFor.SeqSize;
                        _s = S.Len4;
                    }
                    else _s = S.Done;
                    break;
                }
                case S.SeqId:
                {
                    var rest = data[i..];
                    var nul = rest.IndexOf((byte)0);
                    var take = nul < 0 ? rest.Length : nul + 1;
                    if (_section < MaxSections)
                    {
                        var copy = Math.Min(nul < 0 ? take : nul, MaxIdentifier - IdentifierLengths[_section]);
                        if (copy > 0)
                        {
                            rest[..copy].CopyTo(Identifiers[_section].AsSpan(IdentifierLengths[_section]));
                            IdentifierLengths[_section] += copy;
                        }
                    }
                    i += take;
                    Consume(take);
                    _seqRemaining -= take;
                    if (nul >= 0)
                        NextInSequence();
                    break;
                }
                case S.Skip:
                {
                    var n = (int)Math.Min(_skip, data.Length - i);
                    i += n;
                    _skip -= n;
                    Consume(n);
                    if (_seqRemaining > 0) _seqRemaining -= n;
                    if (_skip == 0)
                    {
                        if (_lenFor == LenFor.SeqDoc) NextInSequence();
                        else NextSection();
                    }
                    break;
                }
            }
        }
    }

    private void Consume(long n) => _remainingBody -= n;

    private void OnLength(int v)
    {
        switch (_lenFor)
        {
            case LenFor.BodyDoc:
                if (v < 5) { _s = S.Done; return; }
                _skip = v - 4;
                _s = S.Skip;
                return;
            case LenFor.SeqSize:
                if (v < 4) { _s = S.Done; return; }
                _section++;
                if (_section < MaxSections)
                {
                    SectionCount = _section + 1;
                    DocCounts[_section] = 0;
                    IdentifierLengths[_section] = 0;
                }
                _seqRemaining = v - 4;
                _s = S.SeqId;
                return;
            case LenFor.SeqDoc:
                if (v < 5) { _s = S.Done; return; }
                if (_section < MaxSections) DocCounts[_section]++;
                _seqRemaining -= 4;
                _skip = v - 4;
                _s = S.Skip;
                return;
        }
    }

    private void NextInSequence()
    {
        if (_seqRemaining <= 0)
        {
            NextSection();
            return;
        }
        _lenFor = LenFor.SeqDoc;
        _s = S.Len4;
    }

    private void NextSection() => _s = _remainingBody > 0 ? S.Kind : S.Done;
}
