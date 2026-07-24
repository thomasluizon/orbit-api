using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Orbit.Analyzers;

/// <summary>
/// Requires every <c>DbSet&lt;T&gt;</c> property a <c>DbContext</c> subclass declares to have an
/// explicit fluent configuration: a <c>modelBuilder.Entity&lt;T&gt;(...)</c> call anywhere in the
/// context class (the configuration helpers live in the same class), or an
/// <c>ApplyConfiguration(new TConfiguration())</c> registration. Without one, EF infers the mapping
/// by convention, which silently produces wrong keys, indexes, and column types in the next
/// migration. A context that calls <c>ApplyConfigurationsFromAssembly</c> is skipped, since the
/// configured set is not resolvable statically.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DbSetFluentConfigurationAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "ORBIT0005";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "DbSet without explicit fluent configuration",
        "Configure this entity explicitly: add modelBuilder.Entity<T>(entity => { ... }) in OnModelCreating (or register an IEntityTypeConfiguration<T> via ApplyConfiguration). Every mapped entity is configured explicitly - keys, indexes, and column types must not be left to EF convention inference.",
        "Design",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A DbSet<T> without a matching modelBuilder.Entity<T>(...) configuration lets EF infer the mapping by convention, which produces wrong schema in the next migration.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var dbContext = context.Compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.DbContext");
        var dbSet = context.Compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.DbSet`1");
        var modelBuilder = context.Compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.ModelBuilder");
        if (dbContext is null || dbSet is null || modelBuilder is null)
            return;

        context.RegisterSymbolStartAction(
            symbolStartContext => OnSymbolStart(symbolStartContext, dbContext, dbSet, modelBuilder),
            SymbolKind.NamedType);
    }

    private static void OnSymbolStart(
        SymbolStartAnalysisContext context,
        INamedTypeSymbol dbContext,
        INamedTypeSymbol dbSet,
        INamedTypeSymbol modelBuilder)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Class || !DerivesFrom(type, dbContext))
            return;

        var dbSetProperties = type.GetMembers()
            .OfType<IPropertySymbol>()
            .Select(property => (Property: property, EntityType: DbSetElementType(property, dbSet)))
            .Where(pair => pair.EntityType is not null)
            .ToImmutableArray();

        if (dbSetProperties.IsEmpty)
            return;

        var scan = new ConfigurationScan();
        context.RegisterSyntaxNodeAction(
            nodeContext => CollectConfiguredEntityType(nodeContext, modelBuilder, scan),
            SyntaxKind.InvocationExpression);
        context.RegisterSymbolEndAction(endContext => ReportUnconfigured(endContext, dbSetProperties, scan));
    }

    private static void CollectConfiguredEntityType(
        SyntaxNodeAnalysisContext context, INamedTypeSymbol modelBuilder, ConfigurationScan scan)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var methodName = MethodNameOf(invocation);
        if (methodName is not ("Entity" or "ApplyConfiguration" or "ApplyConfigurationsFromAssembly"))
            return;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;
        if (!SymbolEqualityComparer.Default.Equals(method.ContainingType, modelBuilder))
            return;

        lock (scan.Gate)
        {
            if (method.Name == "ApplyConfigurationsFromAssembly")
                scan.Unresolvable = true;
            else if (method.TypeArguments.Length == 1)
                scan.ConfiguredEntityTypes.Add(method.TypeArguments[0]);
        }
    }

    private static void ReportUnconfigured(
        SymbolAnalysisContext context,
        ImmutableArray<(IPropertySymbol Property, ITypeSymbol? EntityType)> dbSetProperties,
        ConfigurationScan scan)
    {
        lock (scan.Gate)
        {
            if (scan.Unresolvable)
                return;

            foreach (var (property, entityType) in dbSetProperties)
            {
                if (scan.ConfiguredEntityTypes.Contains(entityType!))
                    continue;

                var location = property.Locations.FirstOrDefault(candidate => candidate.IsInSource);
                if (location is not null)
                    context.ReportDiagnostic(Diagnostic.Create(Rule, location));
            }
        }
    }

    private sealed class ConfigurationScan
    {
        public readonly HashSet<ISymbol> ConfiguredEntityTypes = new(SymbolEqualityComparer.Default);
        public readonly object Gate = new();
        public bool Unresolvable;
    }

    private static ITypeSymbol? DbSetElementType(IPropertySymbol property, INamedTypeSymbol dbSet)
    {
        return property.Type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } propertyType
            && SymbolEqualityComparer.Default.Equals(propertyType.ConstructedFrom, dbSet)
            ? propertyType.TypeArguments[0]
            : null;
    }

    private static string? MethodNameOf(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
            GenericNameSyntax genericName => genericName.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            _ => null,
        };
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
}
