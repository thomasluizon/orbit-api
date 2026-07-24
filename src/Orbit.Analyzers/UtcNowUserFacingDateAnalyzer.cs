using System;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Orbit.Analyzers;

/// <summary>
/// Guards user-facing dates against the server clock. Inside the Orbit.Api, Orbit.Application,
/// Orbit.Domain, and Orbit.Infrastructure assemblies it reports two shapes:
/// <c>DateOnly.FromDateTime(DateTime.UtcNow...)</c> (a calendar date derived from the raw UTC
/// instant, always wrong for a user-facing "today" and reported with no exemption, in all four
/// assemblies), and any other <c>DateTime.UtcNow</c> read (reported outside Orbit.Domain unless the
/// source line names an <c>*AtUtc</c> timestamp or a cache key, or the value feeds
/// <c>TimeZoneInfo</c> conversion into a user's timezone - the sanctioned pattern behind
/// <c>IUserDateService</c>). Orbit.Domain keeps the pre-analyzer hook's exemption for plain
/// instants (entity timestamps and plan-expiry checks). Generated code (EF migrations, designer
/// files) is excluded.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UtcNowUserFacingDateAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "ORBIT0004";

    private static readonly DiagnosticDescriptor InstantRule = new(
        DiagnosticId,
        "Raw DateTime.UtcNow for a user-facing date",
        "Do not use DateTime.UtcNow here. User-facing dates MUST come from IUserDateService.GetUserTodayAsync(userId); DateTime.UtcNow is only acceptable for *AtUtc timestamps, cache keys, or as input to a TimeZoneInfo conversion into the user's timezone.",
        "Reliability",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Raw DateTime.UtcNow reads outside timestamp, cache-key, and timezone-conversion contexts compute server-clock dates that are wrong for users in other timezones.");

    private static readonly DiagnosticDescriptor DateOnlyRule = new(
        DiagnosticId,
        "Calendar date derived from DateTime.UtcNow",
        "Do not derive a DateOnly from DateTime.UtcNow. The server's UTC date is not the user's date; use IUserDateService.GetUserTodayAsync(userId), or convert via TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, userTimeZone) first.",
        "Reliability",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "DateOnly.FromDateTime(DateTime.UtcNow) collapses the UTC instant to the server's calendar date, which disagrees with the user's date around midnight in their timezone.");

    private static readonly ImmutableHashSet<string> CoveredAssemblies = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "Orbit.Api",
        "Orbit.Application",
        "Orbit.Domain",
        "Orbit.Infrastructure");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(InstantRule, DateOnlyRule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var assemblyName = context.Compilation.AssemblyName;
        if (assemblyName is null || !CoveredAssemblies.Contains(assemblyName))
            return;

        var dateOnlyType = context.Compilation.GetTypeByMetadataName("System.DateOnly");
        var timeZoneInfoType = context.Compilation.GetTypeByMetadataName("System.TimeZoneInfo");
        var instantRuleApplies = assemblyName != "Orbit.Domain";

        context.RegisterSyntaxNodeAction(
            nodeContext => AnalyzeMemberAccess(nodeContext, dateOnlyType, timeZoneInfoType, instantRuleApplies),
            SyntaxKind.SimpleMemberAccessExpression);
    }

    private static void AnalyzeMemberAccess(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol? dateOnlyType,
        INamedTypeSymbol? timeZoneInfoType,
        bool instantRuleApplies)
    {
        var memberAccess = (MemberAccessExpressionSyntax)context.Node;
        if (memberAccess.Name.Identifier.ValueText != "UtcNow")
            return;
        if (IsExcludedPath(memberAccess.SyntaxTree.FilePath))
            return;
        if (!IsDateTimeUtcNow(memberAccess, context.SemanticModel, context.CancellationToken))
            return;

        var consumingInvocation = FindConsumingInvocation(memberAccess);
        if (consumingInvocation is not null)
        {
            var consumerType = ResolveMethodContainingType(consumingInvocation, context.SemanticModel, context.CancellationToken);

            if (dateOnlyType is not null
                && SymbolEqualityComparer.Default.Equals(consumerType, dateOnlyType)
                && IsMethodNamed(consumingInvocation, "FromDateTime"))
            {
                context.ReportDiagnostic(Diagnostic.Create(DateOnlyRule, consumingInvocation.GetLocation()));
                return;
            }

            if (timeZoneInfoType is not null && SymbolEqualityComparer.Default.Equals(consumerType, timeZoneInfoType))
                return;
        }

        if (!instantRuleApplies)
            return;
        if (LineNamesTimestampOrCacheKey(memberAccess, context.CancellationToken))
            return;

        context.ReportDiagnostic(Diagnostic.Create(InstantRule, memberAccess.GetLocation()));
    }

    private static bool IsDateTimeUtcNow(
        MemberAccessExpressionSyntax memberAccess, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        return semanticModel.GetSymbolInfo(memberAccess, cancellationToken).Symbol is IPropertySymbol property
            && property.ContainingType?.SpecialType == SpecialType.System_DateTime;
    }

    /// <summary>
    /// Walks up from the <c>DateTime.UtcNow</c> access through the member-access/invocation chain it
    /// roots (for example <c>DateTime.UtcNow.AddDays(-1)</c>) and returns the invocation that consumes
    /// the chain as an argument, or null when the value is not a direct argument to a call.
    /// </summary>
    private static InvocationExpressionSyntax? FindConsumingInvocation(MemberAccessExpressionSyntax memberAccess)
    {
        SyntaxNode node = memberAccess;
        while (true)
        {
            switch (node.Parent)
            {
                case MemberAccessExpressionSyntax parentAccess when parentAccess.Expression == node:
                    node = parentAccess;
                    continue;
                case InvocationExpressionSyntax parentInvocation when parentInvocation.Expression == node:
                    node = parentInvocation;
                    continue;
                case ParenthesizedExpressionSyntax parenthesized:
                    node = parenthesized;
                    continue;
                case ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax consumer } }:
                    return consumer;
                default:
                    return null;
            }
        }
    }

    private static INamedTypeSymbol? ResolveMethodContainingType(
        InvocationExpressionSyntax invocation, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        return semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol method
            ? method.ContainingType
            : null;
    }

    private static bool IsMethodNamed(InvocationExpressionSyntax invocation, string name)
    {
        return invocation.Expression is MemberAccessExpressionSyntax memberAccess
            && memberAccess.Name.Identifier.ValueText == name;
    }

    private static bool LineNamesTimestampOrCacheKey(
        MemberAccessExpressionSyntax memberAccess, CancellationToken cancellationToken)
    {
        var lineIndex = memberAccess.GetLocation().GetLineSpan().StartLinePosition.Line;
        var line = memberAccess.SyntaxTree.GetText(cancellationToken).Lines[lineIndex].ToString();
        return line.IndexOf("AtUtc", StringComparison.OrdinalIgnoreCase) >= 0
            || line.IndexOf("cache", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsExcludedPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var normalized = path.Replace('\\', '/');
        return normalized.IndexOf("/Migrations/", StringComparison.OrdinalIgnoreCase) >= 0
            || normalized.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase);
    }
}
