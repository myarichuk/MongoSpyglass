using Simple.Arena;
using System.Text;
using System.Runtime.InteropServices;

namespace MongoSpyglass.Proxy.WireProtocol.Raw
{
    [StructLayout(LayoutKind.Sequential, Pack = 0)]
    public ref struct OpQuery
    {
        public OperationFlags Flags;

        public Span<char> FullCollectionName; //C-style string

        public int NumberToSkip;
        public int NumberToReturn;

        public Span<byte> Query; //BSON document
        public Span<byte> ReturnFieldsSelector; //BSON document

        public static unsafe OpQuery FromBytes(Span<byte> pBytes)
        {
            var pOpQuery = (OpQuery*)pBytes.ToIntPtr().ToPointer();

            return new OpQuery
            {
                Flags = pOpQuery->Flags,
                FullCollectionName = pOpQuery->FullCollectionName,
                NumberToSkip = pOpQuery->NumberToSkip,
                NumberToReturn = pOpQuery->NumberToReturn,
                Query = pOpQuery->Query,
                ReturnFieldsSelector = pOpQuery->ReturnFieldsSelector
            };
        }

        public Span<byte> ToBytes(GrowableArena allocator)
        {
            var opQuery = this;
            var collectionNameLength = Encoding.UTF8.GetByteCount(opQuery.FullCollectionName);
            var outputValue = allocator.Allocate<byte>(
                sizeof(int) +
                collectionNameLength + 1 +
                sizeof(int) +
                sizeof(int) +
                opQuery.Query.Length +
                opQuery.ReturnFieldsSelector.Length);

            var offset = 0;
            opQuery.Flags.AsBytes(allocator).CopyTo(outputValue[offset..]);
            offset += sizeof(int);

            offset += Encoding.UTF8.GetBytes(opQuery.FullCollectionName, outputValue[offset..]);
            outputValue[offset++] = 0;

            opQuery.NumberToSkip.AsBytes(allocator).CopyTo(outputValue[offset..]);
            offset += sizeof(int);

            opQuery.NumberToReturn.AsBytes(allocator).CopyTo(outputValue[offset..]);
            offset += sizeof(int);

            opQuery.Query.CopyTo(outputValue[offset..]);
            offset += opQuery.Query.Length;

            opQuery.ReturnFieldsSelector.CopyTo(outputValue[offset..]);
            return outputValue;
        }
    }
}
