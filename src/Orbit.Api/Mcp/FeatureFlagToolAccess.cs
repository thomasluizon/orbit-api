using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Orbit.Application.Common;
using Orbit.Application.FeatureFlags;

namespace Orbit.Api.Mcp;

internal static class FeatureFlagToolAccess
{
    internal static async Task<bool> IsAdminAsync(ClaimsPrincipal? user, IServiceProvider? services) =>
        user is not null && services is not null && (await services.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(user, null, AdminPolicy.Name)).Succeeded;

    internal static IMcpServerBuilder WithFeatureFlagAccess(this IMcpServerBuilder builder) =>
        builder.WithRequestFilters(filters =>
        {
            filters.AddListToolsFilter(next => async (context, cancellationToken) =>
            {
                var result = await next(context, cancellationToken);
                if (!await IsAdminAsync(context.User, context.Services))
                {
                    foreach (var tool in result.Tools.Where(tool => tool.Name == FeatureFlagActions.ToolName).ToList())
                        result.Tools.Remove(tool);
                }
                return result;
            });
            filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                if (context.Params.Name == FeatureFlagActions.ToolName && !await IsAdminAsync(context.User, context.Services))
                    throw NotFound();
                return await next(context, cancellationToken);
            });
        });

    internal static McpProtocolException NotFound() =>
        new($"Unknown tool: '{FeatureFlagActions.ToolName}'", McpErrorCode.InvalidParams);
}
