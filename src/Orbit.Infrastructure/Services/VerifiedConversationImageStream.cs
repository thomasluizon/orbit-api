using System.Security.Cryptography;

namespace Orbit.Infrastructure.Services;

internal sealed class VerifiedConversationImageStream(Stream source, string expectedDigest) : Stream
{
    private readonly IncrementalHash _digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _verified;
    public override bool CanRead => source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        var count = source.Read(buffer);
        if (buffer.Length > 0) Observe(buffer[..count]);
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var count = await source.ReadAsync(buffer, cancellationToken);
        if (buffer.Length > 0) Observe(buffer.Span[..count]);
        return count;
    }

    private void Observe(ReadOnlySpan<byte> bytes)
    {
        if (_verified) return;
        if (!bytes.IsEmpty) { _digest.AppendData(bytes); return; }
        if (!string.Equals(Convert.ToHexStringLower(_digest.GetHashAndReset()), expectedDigest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Conversation image checksum did not match.");
        _verified = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { source.Dispose(); _digest.Dispose(); }
        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
