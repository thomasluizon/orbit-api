using System.Text.Json;
using FluentAssertions;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Validators;

public class ExecuteAgentMutationCommandValidatorTests
{
    [Theory]
    [InlineData(true, "create_habit", true)]
    [InlineData(false, "create_habit", false)]
    [InlineData(true, "", false)]
    public void RequiresUserAndOperation(bool hasUser, string operation, bool valid)
    {
        var command = new ExecuteAgentMutationCommand(new AgentExecuteOperationRequest(hasUser ? Guid.NewGuid() : Guid.Empty,
            operation, JsonSerializer.SerializeToElement(new { }), AgentExecutionSurface.Mcp, AgentAuthMethod.ApiKey));
        new ExecuteAgentMutationCommandValidator().Validate(command).IsValid.Should().Be(valid);
    }
}
