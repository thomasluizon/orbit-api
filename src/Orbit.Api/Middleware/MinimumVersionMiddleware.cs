using Orbit.Api.Extensions;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Api.Middleware;

public sealed partial class MinimumVersionMiddleware(
    RequestDelegate next,
    ILogger<MinimumVersionMiddleware> logger)
{
    private const string AppVersionHeaderName = "X-App-Version";

    public async Task InvokeAsync(HttpContext context, IAppConfigService configService)
    {
        var clientVersion = context.Request.Headers[AppVersionHeaderName].ToString().Trim();
        var clientParts = NormalizeVersion(clientVersion);
        if (clientParts is null)
        {
            await next(context);
            return;
        }

        var minimumVersion = await configService.GetAsync(
            AppConfigKeys.MinSupportedVersion, "0.0.0", context.RequestAborted);

        if (!IsVersionBelow(clientParts, minimumVersion))
        {
            await next(context);
            return;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            var requestId = context.GetRequestId();
            LogUpgradeRequired(logger, clientVersion, minimumVersion, requestId);
        }

        context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        context.Response.ContentType = "application/json";
        context.Response.Headers[HttpContextExtensions.RequestIdHeaderName] = context.GetRequestId();

        await context.Response.WriteAsJsonAsync(new
        {
            error = ErrorMessages.UpgradeRequired.Message,
            errorCode = ErrorMessages.UpgradeRequired.Code,
            upgradeRequired = true,
            minVersion = minimumVersion,
        });
    }

    private static bool IsVersionBelow(int[] currentParts, string minimum)
    {
        var minimumParts = NormalizeVersion(minimum) ?? [0];
        var length = Math.Max(currentParts.Length, minimumParts.Length);
        for (var i = 0; i < length; i++)
        {
            var currentSegment = i < currentParts.Length ? currentParts[i] : 0;
            var minimumSegment = i < minimumParts.Length ? minimumParts[i] : 0;
            if (currentSegment < minimumSegment)
                return true;
            if (currentSegment > minimumSegment)
                return false;
        }

        return false;
    }

    private static int[]? NormalizeVersion(string version)
    {
        var numeric = new string(version.TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray());
        if (!numeric.Any(char.IsDigit))
            return null;

        return numeric
            .Split('.')
            .Select(segment => int.TryParse(segment, out var parsed) ? parsed : 0)
            .ToArray();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Upgrade required: client version {ClientVersion} is below floor {MinVersion}. RequestId={RequestId}")]
    private static partial void LogUpgradeRequired(ILogger logger, string clientVersion, string minVersion, string requestId);
}
