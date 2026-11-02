using Google.Apis.Http;
using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.Services;

public sealed class FcmDeliveryHttpClientFactory(PushDeliverySettings settings) : Google.Apis.Http.HttpClientFactory
{
    protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) =>
        new BoundedHandler(base.CreateHandler(args), settings);

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
                return await base.SendAsync(request, deadline.Token);
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
