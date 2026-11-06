using System.Buffers;
using System.IO.Compression;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Orbit.Infrastructure.AI;

/// <summary>
/// Counts <c>o200k_base</c> tokens with the pre-tokenizer and byte-pair merge of <c>TiktokenTokenizer</c>.
/// The library keeps an encoder, a decoder and a vocabulary, about 42 MB that its static cache never releases.
/// This counter keeps the token bytes and a rank table in four arrays, about 4 MB. See infra/memory-budget.md.
/// The merge follows tiktoken's <c>byte_pair_merge</c>: https://github.com/openai/tiktoken/blob/main/src/lib.rs
/// </summary>
internal sealed partial class O200kTokenCounter
{
    private const string DataAssemblyName = "Microsoft.ML.Tokenizers.Data.O200kBase";
    private const string ResourceName = "o200k_base.tiktoken.deflate";
    private const string CapacityPrefix = "Capacity: ";
    private const int NoRank = int.MaxValue;
    private const int StackPieceBytes = 256;

    private static readonly Lazy<O200kTokenCounter> SharedCounter = new(Load);

    private readonly byte[] _tokenBytes;
    private readonly int[] _tokenOffsets;
    private readonly int[] _slots;
    private readonly int _slotMask;

    private O200kTokenCounter(byte[] tokenBytes, int[] tokenOffsets)
    {
        _tokenBytes = tokenBytes;
        _tokenOffsets = tokenOffsets;
        _slots = new int[(int)BitOperations.RoundUpToPowerOf2((uint)TokenCount * 2)];
        _slotMask = _slots.Length - 1;
        for (var rank = 0; rank < TokenCount; rank++)
        {
            var slot = Hash(Token(rank)) & _slotMask;
            while (_slots[slot] != 0)
                slot = (slot + 1) & _slotMask;
            _slots[slot] = rank + 1;
        }
    }

    public static O200kTokenCounter Shared => SharedCounter.Value;

    internal int TokenCount => _tokenOffsets.Length - 1;

    internal long RetainedBytes =>
        _tokenBytes.LongLength + ((long)_tokenOffsets.Length + _slots.Length) * sizeof(int);

    public int CountTokens(string text)
    {
        var tokens = 0;
        var start = 0;
        foreach (var special in SpecialTokens().EnumerateMatches(text))
        {
            tokens = checked(tokens + CountOrdinary(text.AsSpan(start, special.Index - start)) + 1);
            start = special.Index + special.Length;
        }
        return checked(tokens + CountOrdinary(text.AsSpan(start)));
    }

    internal int Rank(ReadOnlySpan<byte> piece)
    {
        var slot = Hash(piece) & _slotMask;
        for (var entry = _slots[slot]; entry != 0; entry = _slots[slot])
        {
            if (Token(entry - 1).SequenceEqual(piece))
                return entry - 1;
            slot = (slot + 1) & _slotMask;
        }
        return NoRank;
    }

    private int CountOrdinary(ReadOnlySpan<char> text)
    {
        var tokens = 0;
        byte[]? rented = null;
        Span<byte> buffer = stackalloc byte[StackPieceBytes];
        try
        {
            foreach (var match in PreTokens().EnumerateMatches(text))
            {
                var chars = text.Slice(match.Index, match.Length);
                var length = Encoding.UTF8.GetMaxByteCount(chars.Length);
                if (length > buffer.Length)
                {
                    if (rented is not null)
                        ArrayPool<byte>.Shared.Return(rented);
                    rented = ArrayPool<byte>.Shared.Rent(length);
                    buffer = rented;
                }
                var written = Encoding.UTF8.GetBytes(chars, buffer);
                tokens = checked(tokens + CountPiece(buffer[..written]));
            }
            return tokens;
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private int CountPiece(ReadOnlySpan<byte> piece)
    {
        if (piece.Length == 1 || Rank(piece) != NoRank)
            return 1;

        var parts = piece.Length + 1;
        var starts = ArrayPool<int>.Shared.Rent(parts);
        var ranks = ArrayPool<int>.Shared.Rent(parts);
        try
        {
            for (var index = 0; index < piece.Length - 1; index++)
            {
                starts[index] = index;
                ranks[index] = Rank(piece.Slice(index, 2));
            }
            starts[piece.Length - 1] = piece.Length - 1;
            ranks[piece.Length - 1] = NoRank;
            starts[piece.Length] = piece.Length;
            ranks[piece.Length] = NoRank;

            while (LowestRank(ranks, parts) is var at && at >= 0)
            {
                if (at > 0)
                    ranks[at - 1] = MergedRank(piece, starts, parts, at - 1);
                ranks[at] = MergedRank(piece, starts, parts, at);
                Array.Copy(starts, at + 2, starts, at + 1, parts - at - 2);
                Array.Copy(ranks, at + 2, ranks, at + 1, parts - at - 2);
                parts--;
            }
            return parts - 1;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(starts);
            ArrayPool<int>.Shared.Return(ranks);
        }
    }

    private static int LowestRank(int[] ranks, int parts)
    {
        var lowest = NoRank;
        var at = -1;
        for (var index = 0; index < parts - 1; index++)
        {
            if (ranks[index] < lowest)
            {
                lowest = ranks[index];
                at = index;
            }
        }
        return at;
    }

    private int MergedRank(ReadOnlySpan<byte> piece, int[] starts, int parts, int index) =>
        index + 3 < parts ? Rank(piece[starts[index]..starts[index + 3]]) : NoRank;

    private ReadOnlySpan<byte> Token(int rank) =>
        _tokenBytes.AsSpan(_tokenOffsets[rank], _tokenOffsets[rank + 1] - _tokenOffsets[rank]);

    private static int Hash(ReadOnlySpan<byte> bytes)
    {
        var hash = new HashCode();
        hash.AddBytes(bytes);
        return hash.ToHashCode();
    }

    private static O200kTokenCounter Load()
    {
        using var resource = Assembly.Load(DataAssemblyName).GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException($"{DataAssemblyName} has no {ResourceName} resource.");
        using var vocabulary = new StreamReader(new DeflateStream(resource, CompressionMode.Decompress), Encoding.UTF8);
        var header = vocabulary.ReadLine();
        if (header is null || !header.StartsWith(CapacityPrefix, StringComparison.Ordinal)
            || !int.TryParse(header.AsSpan(CapacityPrefix.Length), out var capacity) || capacity <= 0)
            throw new InvalidDataException($"{ResourceName} has no capacity header.");

        var offsets = new List<int>(capacity + 1) { 0 };
        var bytes = new byte[capacity * 8];
        var length = 0;
        Span<byte> token = stackalloc byte[StackPieceBytes];
        while (vocabulary.ReadLine() is { } line)
        {
            if (!Convert.TryFromBase64String(line, token, out var written))
                throw new InvalidDataException($"{ResourceName} line {offsets.Count} is not a base64 token.");
            if (length + written > bytes.Length)
                Array.Resize(ref bytes, bytes.Length * 2);
            token[..written].CopyTo(bytes.AsSpan(length));
            length += written;
            offsets.Add(length);
        }
        Array.Resize(ref bytes, length);
        return new O200kTokenCounter(bytes, [.. offsets]);
    }

    /// <summary>The <c>o200k_base</c> split pattern from the tiktoken registry:
    /// https://github.com/openai/tiktoken/blob/main/tiktoken_ext/openai_public.py</summary>
    [GeneratedRegex(@"[^\r\n\p{L}\p{N}]?[\p{Lu}\p{Lt}\p{Lm}\p{Lo}\p{M}]*[\p{Ll}\p{Lm}\p{Lo}\p{M}]+(?i:'s|'t|'re|'ve|'m|'ll|'d)?|[^\r\n\p{L}\p{N}]?[\p{Lu}\p{Lt}\p{Lm}\p{Lo}\p{M}]+[\p{Ll}\p{Lm}\p{Lo}\p{M}]*(?i:'s|'t|'re|'ve|'m|'ll|'d)?|\p{N}{1,3}| ?[^\s\p{L}\p{N}]+[\r\n/]*|\s*[\r\n]+|\s+(?!\S)|\s+")]
    private static partial Regex PreTokens();

    [GeneratedRegex(@"<\|endoftext\|>|<\|endofprompt\|>")]
    private static partial Regex SpecialTokens();
}
