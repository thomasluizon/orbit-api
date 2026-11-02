using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;

namespace Orbit.Api.Middleware;

public sealed class RouteBodyLimitMiddleware(RequestDelegate next, ILogger<RouteBodyLimitMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var limit = context.Request.Path.StartsWithSegments("/mcp")
            ? 1024 * 1024L
            : context.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize;
        if (limit is null)
        {
            await next(context);
            return;
        }

        var original = context.Request.Body;
        try
        {
            if (context.Request.ContentLength > limit)
                throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);
            var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false })
                feature.MaxRequestBodySize = limit;
            context.Request.Body = new LimitedReadStream(original, limit.Value);
            await next(context);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge && !context.Response.HasStarted)
        {
            logger.LogWarning("Request body rejected. Route={Route} DeclaredLength={DeclaredLength} Limit={Limit}",
                context.Request.Path, context.Request.ContentLength, limit);
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        }
        finally
        {
            context.Request.Body = original;
        }
    }

    private sealed class LimitedReadStream(Stream inner, long limit) : Stream
    {
        private long _read;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        private int Allowed(int count) => (int)Math.Min(count, limit - _read + 1);
        private int Count(int count)
        {
            _read += count;
            if (_read > limit)
                throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, Allowed(count)));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer[..Allowed(buffer.Length)]));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer[..Allowed(buffer.Length)], cancellationToken));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
