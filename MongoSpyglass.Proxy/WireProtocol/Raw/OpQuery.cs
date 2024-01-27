using Simple.Arena;
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

        public unsafe Span<byte> ToBytes(GrowableArena allocator)
        {
            var opQuery = this;
            var outputValue = allocator.Allocate<byte>((sizeof(int) * 3) + opQuery.FullCollectionName.Length + opQuery.Query.Length + opQuery.ReturnFieldsSelector.Length);

            var pOpQuery = (OpQuery*)outputValue.ToIntPtr().ToPointer();

            pOpQuery->Flags = opQuery.Flags;
            pOpQuery->FullCollectionName = opQuery.FullCollectionName;
            pOpQuery->NumberToSkip = opQuery.NumberToSkip;
            pOpQuery->NumberToReturn = opQuery.NumberToReturn;
            pOpQuery->Query = opQuery.Query;
            pOpQuery->ReturnFieldsSelector = opQuery.ReturnFieldsSelector;

            return outputValue;
        }
    }
}
