namespace Orbit.Infrastructure.AI;

internal static class EvalAudioReservation
{
    /// <summary>Whisper bills $0.006 per minute. https://developers.openai.com/api/docs/models/whisper-1</summary>
    internal static long EstimateMicroDollars(Stream audio, string fileName, string model)
    {
        if (model != "whisper-1" || !audio.CanSeek)
            throw new InvalidOperationException("Evaluation audio needs a supported duration estimate.");
        var position = audio.Position;
        try
        {
            using var media = TagLib.File.Create(new ReadOnlyAudio(fileName, audio), TagLib.ReadStyle.Average);
            var seconds = media.Properties.Duration.TotalSeconds;
            if (!double.IsFinite(seconds) || seconds <= 0)
                throw new InvalidOperationException("Evaluation audio duration is unavailable.");
            return checked((long)(Math.Ceiling(seconds) + 1) * 100);
        }
        finally
        {
            audio.Position = position;
        }
    }

    private sealed class ReadOnlyAudio(string name, Stream audio) : TagLib.File.IFileAbstraction
    {
        public string Name => name;
        public Stream ReadStream => audio;
        public Stream WriteStream => throw new NotSupportedException();
        public void CloseStream(Stream stream) => stream.Position = 0;
    }
}
