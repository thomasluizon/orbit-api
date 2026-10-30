using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

public sealed class AppSurfaceCatalog : IAppSurfaceCatalog
{
    private readonly IReadOnlyList<AppSurface> _surfaces = AgentCatalogService.BuildSurfaces();

    public IReadOnlyList<AppSurface> GetSurfaces() => _surfaces;
}
