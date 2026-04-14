using Microsoft.AspNetCore.Mvc.Testing;

namespace Orbit.IntegrationTests;

public sealed class IntegrationTestWebApplicationFactory : WebApplicationFactory<Program>
{
    private static int _clientCounter;

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);

        client.DefaultRequestHeaders.Remove("X-Forwarded-For");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-For", BuildClientIpAddress());
    }

    private static string BuildClientIpAddress()
    {
        var clientNumber = Interlocked.Increment(ref _clientCounter);
        var thirdOctet = (clientNumber / 254) % 254;
        var fourthOctet = clientNumber % 254;

        if (fourthOctet == 0)
            fourthOctet = 1;

        return $"198.51.{thirdOctet}.{fourthOctet}";
    }
}
