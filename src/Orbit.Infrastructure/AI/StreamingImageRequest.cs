#pragma warning disable OPENAI001

using System.Buffers.Text;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OpenAI.Responses;

namespace Orbit.Infrastructure.AI;

internal sealed record CurrentTurnImage(ReadOnlyMemory<byte> Payload, string Marker)
{
    internal static CurrentTurnImage Create(byte[] bytes, string mimeType)
    {
        var prefix = Encoding.UTF8.GetBytes($"data:{mimeType};base64,");
        var payload = new byte[prefix.Length + Base64.GetMaxEncodedToUtf8Length(bytes.Length)];
        prefix.CopyTo(payload, 0);
        Base64.EncodeToUtf8(bytes, payload.AsSpan(prefix.Length), out _, out _);
        return new(payload, $"https://orbit.invalid/turn-image/{Guid.NewGuid():N}");
    }
}

internal sealed class StreamingImageRequest : BinaryContent
{
    private readonly List<ReadOnlyMemory<byte>> _parts = [];

    internal StreamingImageRequest(CreateResponseOptions options, IReadOnlyList<CurrentTurnImage> images)
    {
        var skeleton = ModelReaderWriter.Write(options, new ModelReaderWriterOptions("W")).ToMemory();
        var replacements = images.Select(image =>
        {
            var marker = Encoding.UTF8.GetBytes(image.Marker);
            var offset = skeleton.Span.IndexOf(marker);
            if (offset < 0 || skeleton.Span[(offset + marker.Length)..].IndexOf(marker) >= 0)
                throw new InvalidOperationException("Each image input must occur exactly once.");
            return (Image: image, Offset: offset, Length: marker.Length);
        }).OrderBy(part => part.Offset);
        var position = 0;
        foreach (var replacement in replacements)
        {
            _parts.Add(skeleton[position..replacement.Offset]);
            _parts.Add(replacement.Image.Payload);
            position = replacement.Offset + replacement.Length;
        }
        _parts.Add(skeleton[position..]);
    }

    public override bool TryComputeLength(out long length)
    {
        length = _parts.Sum(part => (long)part.Length);
        return true;
    }

    public override async Task WriteToAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        foreach (var part in _parts) await stream.WriteAsync(part, cancellationToken);
    }

    public override void WriteTo(Stream stream, CancellationToken cancellationToken = default) =>
        WriteToAsync(stream, cancellationToken).GetAwaiter().GetResult();

    public override void Dispose() { }

    internal static async Task<ResponseResult> CompleteAsync(ResponsesClient client, CreateResponseOptions options,
        IReadOnlyList<CurrentTurnImage> images, CancellationToken ct)
    {
        using var content = new StreamingImageRequest(options, images);
        var result = await client.CreateResponseAsync(content, new RequestOptions { CancellationToken = ct });
        using var response = result.GetRawResponse();
        return (ResponseResult)result;
    }

    internal static async IAsyncEnumerable<StreamingResponseUpdate> ReadUpdatesAsync(ResponsesClient client,
        CreateResponseOptions options, IReadOnlyList<CurrentTurnImage>? images, [EnumeratorCancellation] CancellationToken ct)
    {
        if (images is not { Count: > 0 })
        {
            await foreach (var update in client.CreateResponseStreamingAsync(options, ct)) yield return update;
            yield break;
        }
        using var content = new StreamingImageRequest(options, images);
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
