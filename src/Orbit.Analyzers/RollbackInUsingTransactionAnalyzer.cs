using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Orbit.Analyzers;

/// <summary>
/// Forbids an explicit <c>RollbackAsync()</c>/<c>Rollback()</c> call on an EF
/// <c>IDbContextTransaction</c> (or ADO <c>DbTransaction</c>) that is declared with
/// <c>using</c>/<c>await using</c> in the enclosing scope. Scope disposal already rolls back any
/// uncommitted transaction, so the explicit rollback is redundant and can double-roll-back. A
/// genuinely manually-owned transaction (declared without <c>using</c>, or reached through a field
/// or parameter) is left alone. Generated code (EF migrations, designer files) is excluded.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RollbackInUsingTransactionAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "ORBIT0002";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Remove redundant transaction rollback",
        "Remove this rollback. Inside an 'await using'/'using' transaction scope, disposal already rolls back any uncommitted transaction, so an explicit RollbackAsync()/Rollback() is redundant and can double-rollback. Let the using-scope dispose the transaction.",
        "Reliability",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "An explicit rollback inside a using-scoped EF transaction is redundant; scope disposal rolls back any uncommitted transaction.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>();

        var dbContextTransaction = context.Compilation.GetTypeByMetadataName(
            "Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction");
        if (dbContextTransaction is not null)
            builder.Add(dbContextTransaction);

        var dbTransaction = context.Compilation.GetTypeByMetadataName("System.Data.Common.DbTransaction");
        if (dbTransaction is not null)
            builder.Add(dbTransaction);

        if (builder.Count == 0)
            return;

        var transactionTypes = builder.ToImmutable();
        context.RegisterSyntaxNodeAction(
            nodeContext => AnalyzeInvocation(nodeContext, transactionTypes),
            SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(
        SyntaxNodeAnalysisContext context, ImmutableArray<INamedTypeSymbol> transactionTypes)
    {
        if (IsExcludedPath(context.Node.SyntaxTree.FilePath))
            return;

        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return;

        if (!IsRollbackName(memberAccess.Name.Identifier.ValueText))
            return;

        var semanticModel = context.SemanticModel;

        if (semanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method)
            return;
        if (!IsRollbackName(method.Name) || !IsTransactionType(method.ContainingType, transactionTypes))
            return;

        var receiverType = semanticModel.GetTypeInfo(memberAccess.Expression, context.CancellationToken).Type;
        if (!IsTransactionType(receiverType, transactionTypes))
            return;

        if (memberAccess.Expression is not IdentifierNameSyntax receiver)
            return;
        if (semanticModel.GetSymbolInfo(receiver, context.CancellationToken).Symbol is not ILocalSymbol local)
            return;
        if (!IsDeclaredWithUsing(local, context.CancellationToken))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }

    private static bool IsRollbackName(string name) => name is "RollbackAsync" or "Rollback";

    private static bool IsTransactionType(ITypeSymbol? type, ImmutableArray<INamedTypeSymbol> transactionTypes)
    {
        if (type is null)
            return false;

        foreach (var transactionType in transactionTypes)
        {
            if (SymbolEqualityComparer.Default.Equals(type, transactionType))
                return true;

            foreach (var implemented in type.AllInterfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(implemented, transactionType))
                    return true;
            }
        }

        return false;
    }

    private static bool IsDeclaredWithUsing(ILocalSymbol local, CancellationToken cancellationToken)
    {
        var reference = local.DeclaringSyntaxReferences.IsDefaultOrEmpty
            ? null
            : local.DeclaringSyntaxReferences[0];
        if (reference is null)
            return false;

        for (var node = reference.GetSyntax(cancellationToken).Parent; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case LocalDeclarationStatementSyntax localDeclaration:
                    return localDeclaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword);
                case UsingStatementSyntax:
                    return true;
                case StatementSyntax:
                    return false;
            }
        }

        return false;
    }

    private static bool IsExcludedPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var normalized = path.Replace('\\', '/');
        return normalized.IndexOf("/Migrations/", System.StringComparison.OrdinalIgnoreCase) >= 0
            || normalized.EndsWith(".Designer.cs", System.StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".g.cs", System.StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".generated.cs", System.StringComparison.OrdinalIgnoreCase);
    }
}
