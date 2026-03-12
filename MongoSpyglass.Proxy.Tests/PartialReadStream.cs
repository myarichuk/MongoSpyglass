using System;
using System.IO;

namespace MongoSpyglass.Proxy.Tests
{
    public class PartialReadStream : Stream
    {
        private readonly byte[] _data;
        private int _position;
        private readonly int _maxReadSize;

        public PartialReadStream(byte[] data, int maxReadSize)
        {
            _data = data;
            _maxReadSize = maxReadSize;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _data.Length) return 0;

            int toRead = Math.Min(count, _maxReadSize);
            toRead = Math.Min(toRead, _data.Length - _position);

            Array.Copy(_data, _position, buffer, offset, toRead);
            _position += toRead;
            return toRead;
        }

        // Ensure Span based Read uses the overridden method
        public override int Read(Span<byte> buffer)
        {
            byte[] temp = new byte[buffer.Length];
            int read = Read(temp, 0, buffer.Length);
            new Span<byte>(temp, 0, read).CopyTo(buffer);
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
