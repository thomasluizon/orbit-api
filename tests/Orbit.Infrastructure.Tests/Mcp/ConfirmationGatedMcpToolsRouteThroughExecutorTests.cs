using System.Text.RegularExpressions;
using FluentAssertions;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Mcp;

/// <summary>
/// Architecture guard behind the MCP confirmation gate. The selective-auth middleware in
/// <c>WebApplicationExtensions</c> forwards a tool whose capability carries a confirmation
/// requirement straight to the tool, because <c>IAgentOperationExecutor</c> owns that gate and is
/// the only layer that receives the caller's confirmation token. That deferral is safe only while
/// five invariants hold, and each one is pinned by a test here.
/// <list type="number">
/// <item>A confirmation requirement always sits on a mutation, because only a mutation is
/// guaranteed to reach the executor.</item>
/// <item>The source scan reaches every confirmation-gated MCP tool the catalog declares. The three
/// guards below assert over whatever the scan returns, so a scan that silently stopped finding
/// gated tools would report every one of them clean while enforcing nothing. The other three
/// source-scan invariants rest on this one.</item>
/// <item>Every confirmation-gated MCP tool reaches the executor through
/// <c>McpExecutorBridge</c>.</item>
/// <item>The operation id a gated tool forwards resolves to the tool's own capability, carrying the
/// same id and the same confirmation requirement. The executor gates on
/// <c>GetCapability(operation.CapabilityId)</c>, never on the MCP tool name, so a gated tool
/// forwarding an ungated operation id keeps the middleware stepping aside while the executor finds
/// nothing to enforce, and the tool runs with no confirmation at all.</item>
/// <item>Every gated tool declares a <c>string? confirmationToken</c> parameter and forwards that
/// identifier to the executor, directly or through a helper it calls. Any other expression in the
/// token slot refuses the tool forever, because <c>HasFreshConfirmation</c> can never see the
/// caller's token. The guard resolves the forwarded expression back to the parameter rather than
/// rejecting a list of null spellings, so <c>default</c>, <c>null!</c>, <c>(string?)null</c>,
/// <c>""</c> and an unrelated local all fail the same way.</item>
/// </list>
/// </summary>
public partial class ConfirmationGatedMcpToolsRouteThroughExecutorTests
{
    private const string BridgeCall = "executorBridge.ExecuteAsync(";
    private const int OperationIdArgumentIndex = 1;
    private const int ConfirmationTokenArgumentIndex = 3;
    private const string ConfirmationTokenParameterName = "confirmationToken";

    [GeneratedRegex(@"\[McpServerTool\(Name = ""(?<name>[^""]+)""")]
    private static partial Regex ToolAttributePattern();

    [GeneratedRegex(@"(?m)^    (?![\[/])(?:[\w<>\[\],\.\?]+\s+)+(?<name>\w+)\s*(?:<[^<>()]*>)?\s*\(")]
    private static partial Regex MemberDeclarationPattern();

    [GeneratedRegex(@"(?m)^\s*(?:public|private|protected|internal)?\s*const\s+string\s+(?<name>\w+)\s*=\s*""(?<value>[^""]*)""\s*;")]
    private static partial Regex StringConstantPattern();

    [GeneratedRegex(@"\b(?<name>\w+)\s*\(")]
    private static partial Regex InvocationPattern();

    [GeneratedRegex(@"\bstring\?\s+" + ConfirmationTokenParameterName + @"\b")]
    private static partial Regex ConfirmationTokenParameterPattern();

    [GeneratedRegex(@"^\w+\s*:(?!:)")]
    private static partial Regex NamedArgumentPrefixPattern();

    [Fact]
    public void EveryConfirmationRequirement_SitsOnAMutation()
    {
        var offenders = new AgentCatalogService()
            .GetCapabilities()
            .Where(capability =>
                capability.ConfirmationRequirement is not AgentConfirmationRequirement.None &&
                !capability.IsMutation)
            .Select(capability => capability.Id)
            .ToList();

        offenders.Should().BeEmpty(
            "the MCP middleware defers the confirmation gate to the executor, and only mutations " +
            "are guaranteed to reach it. Offending capability/capabilities: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void TheSourceScan_ReachesEveryConfirmationGatedMcpToolInTheCatalog()
    {
        var expected = new AgentCatalogService()
            .GetCapabilities()
            .Where(capability => capability.ConfirmationRequirement is not AgentConfirmationRequirement.None)
            .SelectMany(capability => capability.McpToolNames ?? [])
            .OrderBy(toolName => toolName, StringComparer.Ordinal)
            .ToList();

        var scanned = ScanConfirmationGatedMcpTools()
            .Select(tool => tool.ToolName)
            .OrderBy(toolName => toolName, StringComparer.Ordinal)
            .ToList();

        scanned.Should().Equal(expected,
            "the three guards below assert over whatever this scan finds, so a scan that silently " +
            "stopped finding gated tools would turn every one of them green while enforcing nothing");
    }

    [Fact]
    public void EveryConfirmationGatedMcpTool_RoutesThroughTheExecutorBridge()
    {
        var offenders = ScanConfirmationGatedMcpTools()
            .Where(tool => tool.BridgeCalls.Count == 0)
            .Select(tool => $"{tool.FileName}: {tool.ToolName} ({tool.Capability.ConfirmationRequirement})")
            .ToList();

        offenders.Should().BeEmpty(
            "a confirmation-gated MCP tool must reach IAgentOperationExecutor through " +
            "McpExecutorBridge, otherwise nothing enforces its confirmation or step-up. " +
            "Offending tool(s):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void EveryConfirmationGatedMcpTool_ForwardsAnOperationIdCarryingTheSameConfirmationRequirement()
    {
        var catalogService = new AgentCatalogService();
        var offenders = new List<string>();

        foreach (var tool in ScanConfirmationGatedMcpTools())
        {
            foreach (var call in tool.BridgeCalls)
            {
                var forwarded = call.OperationId is null
                    ? null
                    : catalogService.GetCapabilityByChatTool(call.OperationId);

                if (forwarded is not null &&
                    forwarded.Id == tool.Capability.Id &&
                    forwarded.ConfirmationRequirement == tool.Capability.ConfirmationRequirement)
                    continue;

                offenders.Add(
                    $"{tool.FileName}: {tool.ToolName} ({tool.Capability.Id}, " +
                    $"{tool.Capability.ConfirmationRequirement}) forwards " +
                    $"operation '{call.OperationIdExpression}' resolving to " +
                    $"{forwarded?.Id ?? "an unknown capability"} " +
                    $"({forwarded?.ConfirmationRequirement.ToString() ?? "no requirement"})");
            }
        }

        offenders.Should().BeEmpty(
            "the executor gates on the capability behind the forwarded operation id, not on the " +
            "MCP tool name, so a gated tool forwarding an ungated operation id runs with no " +
            "confirmation while the middleware still steps aside, and a gated tool forwarding " +
            "another gated capability's operation id has the executor enforce the wrong scope. " +
            "Offending tool(s):\n" +
            string.Join("\n", offenders));
    }

    [Fact]
    public void EveryConfirmationGatedMcpTool_AcceptsAndForwardsAConfirmationToken()
    {
        var offenders = new List<string>();

        foreach (var tool in ScanConfirmationGatedMcpTools())
        {
            if (!tool.DeclaresConfirmationTokenParameter)
                offenders.Add(
                    $"{tool.FileName}: {tool.ToolName} declares no " +
                    $"'string? {ConfirmationTokenParameterName}' parameter");

            offenders.AddRange(tool.BridgeCalls
                .Where(call => call.ConfirmationTokenExpression != ConfirmationTokenParameterName)
                .Select(call =>
                    $"{tool.FileName}: {tool.ToolName} forwards '{call.ConfirmationTokenExpression ?? "nothing"}' " +
                    $"to the executor instead of its own {ConfirmationTokenParameterName} parameter"));
        }

        offenders.Should().BeEmpty(
            "a confirmation-gated MCP tool is refused until the executor sees a fresh confirmation " +
            "token, so the tool must declare the parameter and forward that identifier through. " +
            "Asserting the forwarded expression resolves to the parameter, rather than rejecting a " +
            "list of null spellings, is what keeps 'default', 'null!', '(string?)null', '\"\"' and " +
            "an unrelated local from passing. Offending tool(s):\n" + string.Join("\n", offenders));
    }

    private static IReadOnlyList<GatedMcpTool> ScanConfirmationGatedMcpTools()
    {
        var catalogService = new AgentCatalogService();
        var scanned = new List<GatedMcpTool>();

        foreach (var file in Directory.EnumerateFiles(LocateMcpToolsDirectory(), "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            var members = BuildMembers(source);
            var membersByName = members
                .GroupBy(member => member.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var constants = BuildStringConstants(source);

            foreach (var attribute in ToolAttributePattern().Matches(source).Cast<Match>())
            {
                var toolName = attribute.Groups["name"].Value;
                var capability = catalogService.GetCapabilityByMcpTool(toolName);
                if (capability is null || capability.ConfirmationRequirement == AgentConfirmationRequirement.None)
                    continue;

                var member = members.FirstOrDefault(candidate => candidate.Start > attribute.Index)
                    ?? throw new InvalidOperationException(
                        $"MCP tool '{toolName}' in {Path.GetFileName(file)} has no method declaration after its attribute.");

                scanned.Add(new GatedMcpTool(
                    Path.GetFileName(file),
                    toolName,
                    capability,
                    CollectBridgeCalls(member, membersByName, constants),
                    DeclaresConfirmationTokenParameter(member)));
            }
        }

        return scanned;
    }

    private static bool DeclaresConfirmationTokenParameter(MemberSource member)
    {
        return SplitTopLevelArguments(member.ParameterList)
            .Any(parameter => ConfirmationTokenParameterPattern().IsMatch(parameter));
    }

    private static IReadOnlyList<BridgeCallSite> CollectBridgeCalls(
        MemberSource member,
        IReadOnlyDictionary<string, MemberSource> membersByName,
        IReadOnlyDictionary<string, string> constants)
    {
        var direct = ReadBridgeCalls(member.Body, constants, [], []);
        if (direct.Count > 0)
            return direct;

        var indirect = new List<BridgeCallSite>();
        foreach (var invocation in InvocationPattern().Matches(member.Body).Cast<Match>())
        {
            var callee = invocation.Groups["name"].Value;
            if (callee == member.Name || !membersByName.TryGetValue(callee, out var target))
                continue;

            var callerArguments = SplitTopLevelArguments(
                ReadBalancedText(member.Body, invocation.Index + invocation.Length - 1));

            indirect.AddRange(ReadBridgeCalls(target.Body, constants, target.Parameters, callerArguments));
        }

        return indirect;
    }

    private static List<BridgeCallSite> ReadBridgeCalls(
        string body,
        IReadOnlyDictionary<string, string> constants,
        List<string> calleeParameters,
        List<string> callerArguments)
    {
        var calls = new List<BridgeCallSite>();
        var searchFrom = 0;

        while (true)
        {
            var callIndex = body.IndexOf(BridgeCall, searchFrom, StringComparison.Ordinal);
            if (callIndex < 0)
                return calls;

            var openIndex = callIndex + BridgeCall.Length - 1;
            var arguments = SplitTopLevelArguments(ReadBalancedText(body, openIndex));
            var operation = ArgumentAt(arguments, OperationIdArgumentIndex);
            var token = ArgumentAt(arguments, ConfirmationTokenArgumentIndex);

            calls.Add(new BridgeCallSite(
                operation ?? "nothing",
                ResolveStringValue(operation, constants, calleeParameters, callerArguments),
                ResolveExpression(token, calleeParameters, callerArguments)));

            searchFrom = openIndex + 1;
        }
    }

    private static string? ArgumentAt(List<string> arguments, int index)
    {
        if (index >= arguments.Count)
            return null;

        var argument = arguments[index];
        var namedPrefix = NamedArgumentPrefixPattern().Match(argument);
        return namedPrefix.Success ? argument[namedPrefix.Length..].Trim() : argument;
    }

    private static string? ResolveExpression(
        string? expression,
        List<string> calleeParameters,
        List<string> callerArguments)
    {
        if (expression is null)
            return null;

        var parameterIndex = calleeParameters.IndexOf(expression);
        if (parameterIndex < 0 || parameterIndex >= callerArguments.Count)
            return expression;

        return ArgumentAt(callerArguments, parameterIndex);
    }

    private static string? ResolveStringValue(
        string? expression,
        IReadOnlyDictionary<string, string> constants,
        List<string> calleeParameters,
        List<string> callerArguments)
    {
        var resolved = ResolveExpression(expression, calleeParameters, callerArguments);
        if (resolved is null)
            return null;

        if (resolved.StartsWith('"') && resolved.EndsWith('"') && resolved.Length >= 2)
            return resolved[1..^1];

        return constants.GetValueOrDefault(resolved);
    }

    private static List<MemberSource> BuildMembers(string source)
    {
        var declarations = MemberDeclarationPattern().Matches(source).Cast<Match>().ToList();
        var members = new List<MemberSource>();

        for (var index = 0; index < declarations.Count; index++)
        {
            var declaration = declarations[index];
            var end = index + 1 < declarations.Count ? declarations[index + 1].Index : source.Length;
            var parameterListStart = declaration.Index + declaration.Length - 1;
            var parameterList = ReadBalancedText(source, parameterListStart);

            members.Add(new MemberSource(
                declaration.Groups["name"].Value,
                declaration.Index,
                source[declaration.Index..end],
                parameterList,
                ReadParameterNames(parameterList)));
        }

        return members;
    }

    private static Dictionary<string, string> BuildStringConstants(string source)
    {
        return StringConstantPattern().Matches(source)
            .Cast<Match>()
            .GroupBy(match => match.Groups["name"].Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Groups["value"].Value, StringComparer.Ordinal);
    }

    private static List<string> ReadParameterNames(string parameterList)
    {
        return SplitTopLevelArguments(parameterList)
            .Select(parameter =>
            {
                var withoutDefault = parameter.Split('=')[0].Trim();
                var tokens = withoutDefault.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                return tokens.Length == 0 ? string.Empty : tokens[^1];
            })
            .ToList();
    }

    private static List<string> SplitTopLevelArguments(string argumentList)
    {
        var arguments = new List<string>();
        var depth = 0;
        var start = 0;
        var index = 0;

        while (index < argumentList.Length)
        {
            var current = argumentList[index];
            if (current == '"')
            {
                index = SkipStringLiteral(argumentList, index);
                continue;
            }

            if (current is '(' or '[' or '{')
                depth++;
            else if (current is ')' or ']' or '}')
                depth--;
            else if (current == ',' && depth == 0)
            {
                arguments.Add(argumentList[start..index].Trim());
                start = index + 1;
            }

            index++;
        }

        var tail = argumentList[start..].Trim();
        if (tail.Length > 0)
            arguments.Add(tail);

        return arguments;
    }

    private static string ReadBalancedText(string source, int openIndex)
    {
        var depth = 0;
        var index = openIndex;

        while (index < source.Length)
        {
            var current = source[index];
            if (current == '"')
            {
                index = SkipStringLiteral(source, index);
                continue;
            }

            if (current is '(' or '[' or '{')
            {
                depth++;
            }
            else if (current is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0)
                    return source[(openIndex + 1)..index];
            }

            index++;
        }

        throw new InvalidOperationException(
            $"Unbalanced argument list while scanning MCP tool source at offset {openIndex}.");
    }

    private static int SkipStringLiteral(string source, int quoteIndex)
    {
        var index = quoteIndex + 1;

        while (index < source.Length)
        {
            if (source[index] == '\\')
            {
                index += 2;
                continue;
            }

            if (source[index] == '"')
                return index + 1;

            index++;
        }

        return index;
    }

    private static string LocateMcpToolsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Orbit.Api", "Mcp", "Tools");
            if (Directory.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate src/Orbit.Api/Mcp/Tools by walking up from the test output directory.");
    }

    private sealed record GatedMcpTool(
        string FileName,
        string ToolName,
        AgentCapability Capability,
        IReadOnlyList<BridgeCallSite> BridgeCalls,
        bool DeclaresConfirmationTokenParameter);

    private sealed record BridgeCallSite(
        string OperationIdExpression,
        string? OperationId,
        string? ConfirmationTokenExpression);

    private sealed record MemberSource(
        string Name,
        int Start,
        string Body,
        string ParameterList,
        List<string> Parameters);

}
