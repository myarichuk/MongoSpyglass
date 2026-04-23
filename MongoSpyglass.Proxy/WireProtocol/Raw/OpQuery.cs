using SharpArena.Allocators;
﻿using SharpArena.Allocators;
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


    }
}
