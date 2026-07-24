using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Orbit.Analyzers;

/// <summary>
/// Requires every concrete MVC controller (a non-abstract class ending in <c>Controller</c> that
/// derives from <c>ControllerBase</c>) to declare its authorization posture explicitly: an
/// <c>[Authorize]</c> or <c>[AllowAnonymous]</c> attribute at class level (own or inherited from a
/// base controller), or one of the two on every action. The orbit-api default is <c>[Authorize]</c>
/// at class level; <c>[AllowAnonymous]</c> is reserved for truly public endpoints (auth flows,
/// health, signature-verified webhooks). Skips compilations that do not reference ASP.NET Core
/// authorization, so only Orbit.Api is analyzed.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ControllerAuthorizationAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "ORBIT0003";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Controller is missing an authorization attribute",
        "Declare this controller's authorization posture. Add [Authorize] at class level (the orbit-api default), or [AllowAnonymous] only for truly public endpoints (auth flows, health, signature-verified webhooks), or one of the two on every action.",
        "Security",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every controller must carry [Authorize] or [AllowAnonymous] at class level, or on every action, so no endpoint ships with an implicit authorization posture.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var controllerBase = context.Compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.ControllerBase");
        var authorize = context.Compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Authorization.AuthorizeAttribute");
        var allowAnonymous = context.Compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute");
        if (controllerBase is null || authorize is null || allowAnonymous is null)
            return;

        var nonAction = context.Compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Mvc.NonActionAttribute");
        context.RegisterSymbolAction(
            symbolContext => AnalyzeType(symbolContext, controllerBase, authorize, allowAnonymous, nonAction),
            SymbolKind.NamedType);
    }

    private static void AnalyzeType(
        SymbolAnalysisContext context,
        INamedTypeSymbol controllerBase,
        INamedTypeSymbol authorize,
        INamedTypeSymbol allowAnonymous,
        INamedTypeSymbol? nonAction)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Class || type.IsAbstract)
            return;
        if (!type.Name.EndsWith("Controller", StringComparison.Ordinal))
            return;
        if (!DerivesFrom(type, controllerBase))
            return;

        for (var current = type; current is not null; current = current.BaseType)
        {
            if (HasAuthorizationAttribute(current, authorize, allowAnonymous))
                return;
        }

        var actions = type.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(method => method.MethodKind == MethodKind.Ordinary
                && method.DeclaredAccessibility == Accessibility.Public
                && !method.IsStatic
                && (nonAction is null || !HasAttribute(method, nonAction)))
            .ToImmutableArray();

        if (actions.Length > 0 && actions.All(action => HasAuthorizationAttribute(action, authorize, allowAnonymous)))
            return;

        var location = type.Locations.FirstOrDefault(candidate => candidate.IsInSource);
        if (location is null)
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, location));
    }

    private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol baseCandidate)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseCandidate))
                return true;
        }

        return false;
    }

    private static bool HasAuthorizationAttribute(ISymbol symbol, INamedTypeSymbol authorize, INamedTypeSymbol allowAnonymous)
    {
        return HasAttribute(symbol, authorize) || HasAttribute(symbol, allowAnonymous);
    }

    private static bool HasAttribute(ISymbol symbol, INamedTypeSymbol attributeType)
    {
        foreach (var attribute in symbol.GetAttributes())
        {
            for (var current = attribute.AttributeClass; current is not null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current, attributeType))
                    return true;
            }
        }

        return false;
    }
}
