using FluentAssertions;
using Orbit.Application.Common;
using Xunit;

namespace Orbit.Application.Tests.Common;

public class AuditBodyRedactorTests
{
    [Fact]
    public void Redact_BearerToken_Replaced()
    {
        var input = """{"Authorization":"Bearer FAKE_NOT_A_REAL_TOKEN_FOR_TEST"}""";
        var result = AuditBodyRedactor.Redact(input);
        result.Should().NotContain("FAKE_NOT_A_REAL_TOKEN_FOR_TEST");
        result.Should().Contain("[REDACTED]");
    }

    [Fact]
    public void Redact_OrbApiKey_Replaced()
    {
        var input = """{"key":"orb_abcdefghijklmnopqrstuv"}""";
        var result = AuditBodyRedactor.Redact(input);
        result.Should().NotContain("orb_abcdefghijklmnopqrstuv");
        result.Should().Contain("[REDACTED]");
    }

    [Fact]
    public void Redact_Email_Replaced()
    {
        var input = """{"email":"victim@example.com"}""";
        var result = AuditBodyRedactor.Redact(input);
        result.Should().NotContain("victim@example.com");
        result.Should().Contain("[REDACTED]");
    }

    [Fact]
    public void Redact_Guid_Replaced()
    {
        var input = """{"userId":"3fa85f64-5717-4562-b3fc-2c963f66afa6"}""";
        var result = AuditBodyRedactor.Redact(input);
        result.Should().NotContain("3fa85f64-5717-4562-b3fc-2c963f66afa6");
        result.Should().Contain("[REDACTED]");
    }

    [Fact]
    public void Redact_PasswordKey_Replaced()
    {
        var input = """{"password":"hunter2"}""";
        var result = AuditBodyRedactor.Redact(input);
        result.Should().NotContain("hunter2");
        result.Should().Contain("[REDACTED]");
    }

    [Fact]
    public void Redact_AccessTokenKey_Replaced()
    {
        var input = """{"access_token":"FAKE_PLACEHOLDER_VALUE"}""";
        var result = AuditBodyRedactor.Redact(input);
        result.Should().NotContain("FAKE_PLACEHOLDER_VALUE");
        result.Should().Contain("[REDACTED]");
    }

    [Fact]
    public void Redact_TruncatesToMaxLength()
    {
        var input = new string('x', 5000);
        var result = AuditBodyRedactor.Redact(input, maxLength: 100);
        result.Length.Should().Be(100);
    }

    [Fact]
    public void Redact_BenignContentPreserved()
    {
        var input = """{"toolName":"create_habit","habit":"go for a walk"}""";
        var result = AuditBodyRedactor.Redact(input);
        result.Should().Contain("create_habit");
        result.Should().Contain("go for a walk");
    }

    [Fact]
    public void Redact_Empty_ReturnsEmpty()
    {
        AuditBodyRedactor.Redact(null).Should().BeEmpty();
        AuditBodyRedactor.Redact("").Should().BeEmpty();
    }

    [Fact]
    public void Redact_RealisticMcpBody_RedactsAllSecretsButKeepsShape()
    {
        var input = """
            {
              "jsonrpc":"2.0",
              "id":1,
              "method":"tools/call",
              "params":{
                "name":"create_habit",
                "arguments":{
                  "title":"Walk",
                  "ownerEmail":"user@example.com",
                  "ownerId":"3fa85f64-5717-4562-b3fc-2c963f66afa6",
                  "Authorization":"Bearer FAKE_TEST_TOKEN_PLACEHOLDER_NOT_REAL"
                }
              }
            }
            """;
        var result = AuditBodyRedactor.Redact(input, maxLength: 2000);
        result.Should().Contain("create_habit");
        result.Should().NotContain("user@example.com");
        result.Should().NotContain("3fa85f64-5717-4562-b3fc-2c963f66afa6");
        result.Should().NotContain("FAKE_TEST_TOKEN_PLACEHOLDER_NOT_REAL");
    }
}
