using SharpArena.Allocators;
﻿// Ignore Spelling: Mongo

using SharpArena.Allocators;
using System.Runtime.InteropServices;

namespace MongoSpyglass.Proxy.WireProtocol.Raw
{
    [StructLayout(LayoutKind.Sequential)]
    public unsafe ref partial struct OpMsg
    {
        public FlagBits Flags;

        public byte Kind;

        public Span<byte> DataSection;


    }
}
