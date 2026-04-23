using System;
using System.IO;
using System.Threading.Tasks;
using System.Net.Sockets;

class Program
{
    static async Task Main()
    {
        // Try passing a Memory<byte> to ReadExactlyAsync
        // Memory<byte> requires managed array backing, but Arena gives unmanaged.
        // There is no `ReadExactlyAsync(Span<byte>)` because async methods can't take ref structs.
        // To read directly into an unmanaged pointer, we have to either:
        // a) use synchronous `ReadExactly(Span)` which is blocking,
        // b) rent a managed array from ArrayPool, read async into it, then copy to unmanaged.
        Console.WriteLine("ArrayPool strategy.");
    }
}
