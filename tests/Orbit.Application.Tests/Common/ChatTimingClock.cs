namespace Orbit.Application.Tests.Common;

internal sealed class ChatTimingClock : TimeProvider
{
    private long _milliseconds;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => Interlocked.Read(ref _milliseconds);
    public void Advance(int milliseconds) => Interlocked.Add(ref _milliseconds, milliseconds);
}
