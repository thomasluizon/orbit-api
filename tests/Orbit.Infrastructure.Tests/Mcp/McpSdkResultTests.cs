using System.IO.Pipelines;
using FluentAssertions;
using ModelContextProtocol.Client;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;

namespace Orbit.Infrastructure.Tests.Mcp;

public class McpSdkResultTests
{
    [Fact]
    public async Task InstalledSdk_StringFailureHasNoErrorFlag_AndDirectInvocationPropagatesException()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new RequestContext<CallToolRequestParams>(Substitute.For<McpServer>(),
            new JsonRpcRequest { Id = new RequestId(1), Method = "tools/call" }, new CallToolRequestParams { Name = "probe" })
        { Services = services };
        var text = await McpServerTool.Create((Func<string>)(() => "Error: rejected")).InvokeAsync(context);
        text.IsError.Should().NotBeTrue();
        text.Content.Should().ContainSingle().Which.Should().BeOfType<TextContentBlock>().Which.Text.Should().Be("Error: rejected");
        var failed = async () => await McpServerTool.Create((Func<string>)(() => throw new ArgumentException("private parse detail"))).InvokeAsync(context);
        await failed.Should().ThrowAsync<ArgumentException>().WithMessage("private parse detail");
    }
    [Fact]
    public async Task InstalledSdk_ClientReceivesStringAsSuccessAndExceptionAsGenericError()
    {
        var input = new Pipe();
        var output = new Pipe();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var textTool = McpServerTool.Create((Func<string>)(() => "Error: rejected"), new() { Name = "text" });
        var exceptionTool = McpServerTool.Create((Func<string>)(() => throw new ArgumentException("private parse detail")), new() { Name = "exception" });
        await using var server = McpServer.Create(new StreamServerTransport(input.Reader.AsStream(), output.Writer.AsStream()),
            new McpServerOptions { ToolCollection = [textTool, exceptionTool] });
        var run = server.RunAsync(timeout.Token);
        await using (var client = await McpClient.CreateAsync(new StreamClientTransport(input.Writer.AsStream(), output.Reader.AsStream()), cancellationToken: timeout.Token))
        {
            var text = await client.CallToolAsync("text", cancellationToken: timeout.Token);
            text.IsError.Should().NotBeTrue();
            McpInputSafetyTests.Text(text).Should().Be("Error: rejected");
            var error = await client.CallToolAsync("exception", cancellationToken: timeout.Token);
            error.IsError.Should().BeTrue();
            McpInputSafetyTests.Text(error).Should().Be("An error occurred invoking 'exception'.");
        }
        await timeout.CancelAsync();
        try { await run; }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { run.IsCanceled.Should().BeTrue(); }
    }
}
