using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Orbit.IntegrationTests;

[CollectionDefinition("Sequential", DisableParallelization = true)]
public class SequentialTestCollection : ICollectionFixture<WebApplicationFactory<Program>>
{
}
