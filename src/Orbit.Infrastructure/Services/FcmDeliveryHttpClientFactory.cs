using Google.Apis.Core;
using Google.Apis.Http;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public sealed class FcmDeliveryHttpClientFactory(PushDeliverySettings settings)
    : Google.Apis.Http.HttpClientFactory
{
    private readonly HttpMessageHandler? _transport;

    internal FcmDeliveryHttpClientFactory(PushDeliverySettings settings, HttpMessageHandler transport) : this(settings)
    {
        _transport = transport;
    }

    protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args)
    {
        args.Initializers.Add(new HttpTimeoutInitializer(Timeout.InfiniteTimeSpan));
        return new BoundedHandler(_transport ?? base.CreateHandler(args), settings);
    }

    internal sealed class BoundedHandler(HttpMessageHandler inner, PushDeliverySettings settings) : DelegatingHandler(inner)
    {
        private readonly SemaphoreSlim _gate = new(Math.Clamp(settings.Concurrency, 1, 32));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.AttemptTimeoutSeconds, 1, 10)));
                var response = await base.SendAsync(request, deadline.Token);
                try
                {
                    await response.Content.LoadIntoBufferAsync(deadline.Token);
                    deadline.Token.ThrowIfCancellationRequested();
                    return response;
                }
                catch
                {
                    response.Dispose();
                    throw;
                }
            }
            finally { _gate.Release(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _gate.Dispose();
            base.Dispose(disposing);
        }
    }
}
