namespace Orbit.Application.Common;

/// <summary>
/// An idempotent command that can call a model provider. The idempotency behavior opens no transaction around
/// such a call, because an open transaction holds its connection and its row locks for the whole provider call.
/// The command commits its own writes, and the behavior stores the response after them. A replay with the same
/// key returns the stored response and calls no provider.
/// </summary>
public interface IProviderCallCommand
{
    bool CallsProvider { get; }
}
