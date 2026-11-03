using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Orbit.Api.Streaming;

public sealed class StreamingJsonFileResult(string fileDownloadName, Func<Stream, CancellationToken, Task> writeAsync) : IActionResult
{
    public string FileDownloadName { get; } = fileDownloadName;

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var response = context.HttpContext.Response;
        response.ContentType = "application/json";
        var disposition = new ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(FileDownloadName);
        response.Headers.ContentDisposition = disposition.ToString();
        await writeAsync(response.Body, context.HttpContext.RequestAborted);
    }
}
