using System;
using SharpArena.Allocators;

namespace MongoSpyglass.Proxy
{
    public static unsafe class ArenaAllocatorExtensions
    {
        public static Span<T> Allocate<T>(this ArenaAllocator allocator, int length = 1) where T : unmanaged
        {
            if (length <= 0) return Span<T>.Empty;

            var size = (nuint)(sizeof(T) * length);
            var align = (nuint)sizeof(T);

            // Limit alignment to 8 bytes maximum to avoid over-aligning issues
            if (align > 8) align = 8;

            void* ptr = allocator.Alloc(size, align);
            return new Span<T>(ptr, length);
        }
    }
}
