namespace Orbit.Application.Common;

/// <summary>
/// An idempotent command that can call a model provider. The idempotency behavior commits the key reservation
/// alone and opens no transaction around the call, because an open transaction holds its connection and its row
/// locks for the whole call. It stores the response after the call, and a failed call releases the key. A retry
/// that finds a reservation with no response runs no handler: the first call still runs, or its process stopped.
/// </summary>
public interface IProviderCallCommand
{
    bool CallsProvider { get; }
}
