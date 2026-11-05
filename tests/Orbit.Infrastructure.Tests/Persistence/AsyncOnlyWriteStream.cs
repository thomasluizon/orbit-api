namespace Orbit.Infrastructure.Tests.Persistence;

internal sealed class AsyncOnlyWriteStream(Stream destination) : Stream
{
    private const string SyncIoError = "Synchronous operations are disallowed. Call WriteAsync or set AllowSynchronousIO to true instead.";

    public long LargestWrite { get; private set; }
    public int AsyncWrites { get; private set; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => destination.Length;
    public override long Position { get => destination.Position; set => throw new NotSupportedException(); }
    public override void Flush() => throw new InvalidOperationException(SyncIoError);
    public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException(SyncIoError);
    public override void Write(ReadOnlySpan<byte> buffer) => throw new InvalidOperationException(SyncIoError);
    public override void WriteByte(byte value) => throw new InvalidOperationException(SyncIoError);
    public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        AsyncWrites++;
        LargestWrite = Math.Max(LargestWrite, buffer.Length);
        await destination.WriteAsync(buffer, cancellationToken);
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
