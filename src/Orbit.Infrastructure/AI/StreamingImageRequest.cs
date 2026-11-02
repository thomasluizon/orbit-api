#pragma warning disable OPENAI001

using System.Buffers;
using System.Buffers.Text;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OpenAI.Responses;

namespace Orbit.Infrastructure.AI;

internal sealed record CurrentTurnImage(byte[] Bytes, string MimeType, string Marker)
{
    internal static CurrentTurnImage Create(byte[] bytes, string mimeType) => new(bytes, mimeType,
        $"https://orbit.invalid/turn-image/{Guid.NewGuid():N}");
}

internal sealed class StreamingImageRequest : BinaryContent
{
    private readonly ReadOnlyMemory<byte> _prefix;
    private readonly ReadOnlyMemory<byte> _suffix;
    private readonly CurrentTurnImage _image;
    private readonly byte[] _dataPrefix;
    private const int InputChunkSize = 24 * 1024;

    internal StreamingImageRequest(CreateResponseOptions options, CurrentTurnImage image)
    {
        _image = image;
        var skeleton = ModelReaderWriter.Write(options, new ModelReaderWriterOptions("W")).ToMemory();
        var marker = Encoding.UTF8.GetBytes(image.Marker);
        var offset = skeleton.Span.IndexOf(marker);
        if (offset < 0 || skeleton.Span[(offset + marker.Length)..].IndexOf(marker) >= 0)
            throw new InvalidOperationException("The current image input must occur exactly once.");
        _prefix = skeleton[..offset];
        _suffix = skeleton[(offset + marker.Length)..];
        _dataPrefix = JsonEncodedText.Encode($"data:{image.MimeType};base64,").EncodedUtf8Bytes.ToArray();
    }

    public override bool TryComputeLength(out long length)
    {
        length = _prefix.Length + (long)_dataPrefix.Length + ((_image.Bytes.LongLength + 2) / 3 * 4) + _suffix.Length;
        return true;
    }

    public override async Task WriteToAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        await stream.WriteAsync(_prefix, cancellationToken);
        await stream.WriteAsync(_dataPrefix, cancellationToken);
        var buffer = ArrayPool<byte>.Shared.Rent(InputChunkSize / 3 * 4);
        try
        {
            for (var offset = 0; offset < _image.Bytes.Length; offset += InputChunkSize)
            {
                var count = Math.Min(InputChunkSize, _image.Bytes.Length - offset);
                Base64.EncodeToUtf8(_image.Bytes.AsSpan(offset, count), buffer, out _, out var written);
                await stream.WriteAsync(buffer.AsMemory(0, written), cancellationToken);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        await stream.WriteAsync(_suffix, cancellationToken);
    }

    public override void WriteTo(Stream stream, CancellationToken cancellationToken = default) =>
        WriteToAsync(stream, cancellationToken).GetAwaiter().GetResult();

    public override void Dispose() { }

    internal static async Task<ResponseResult> CompleteAsync(ResponsesClient client, CreateResponseOptions options,
        CurrentTurnImage image, CancellationToken ct)
    {
        using var content = new StreamingImageRequest(options, image);
        var result = await client.CreateResponseAsync(content, new RequestOptions { CancellationToken = ct });
        using var response = result.GetRawResponse();
        return (ResponseResult)result;
    }

    internal static async IAsyncEnumerable<StreamingResponseUpdate> ReadUpdatesAsync(ResponsesClient client,
        CreateResponseOptions options, CurrentTurnImage? image, [EnumeratorCancellation] CancellationToken ct)
    {
        if (image is null)
        {
            await foreach (var update in client.CreateResponseStreamingAsync(options, ct)) yield return update;
            yield break;
        }
        using var content = new StreamingImageRequest(options, image);
        var result = await client.CreateResponseAsync(content, new RequestOptions { CancellationToken = ct, BufferResponse = false });
        using var response = result.GetRawResponse();
        var stream = response.ContentStream ?? throw new InvalidOperationException("The provider returned no response stream.");
        await foreach (var item in SseParser.Create(stream, (_, bytes) => bytes.ToArray()).EnumerateAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (item.Data.AsSpan().SequenceEqual("[DONE]"u8)) yield break;
            var update = ModelReaderWriter.Read<StreamingResponseUpdate>(BinaryData.FromBytes(item.Data), new ModelReaderWriterOptions("W"));
            if (update is not null) yield return update;
        }
    }
}
