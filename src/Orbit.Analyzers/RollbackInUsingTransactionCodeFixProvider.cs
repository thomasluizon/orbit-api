using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Orbit.Analyzers;

/// <summary>
/// Removes the redundant transaction rollback reported by
/// <see cref="RollbackInUsingTransactionAnalyzer"/>. The whole <c>await tx.RollbackAsync(ct);</c>
/// statement is dropped so the enclosing catch keeps only its real recovery logic and the
/// using-scope disposes the transaction.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RollbackInUsingTransactionCodeFixProvider))]
[Shared]
public sealed class RollbackInUsingTransactionCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(RollbackInUsingTransactionAnalyzer.DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document
            .GetSyntaxRootAsync(context.CancellationToken)
            .ConfigureAwait(false);
        if (root is null)
            return;

        foreach (var diagnostic in context.Diagnostics)
        {
            context.RegisterCodeFix(
                CodeAction.Create(
                    "Remove redundant rollback",
                    ct => RemoveRollbackAsync(context.Document, diagnostic, ct),
                    equivalenceKey: "RemoveRollback"),
                diagnostic);
        }
    }

    private static async Task<Document> RemoveRollbackAsync(
        Document document, Diagnostic diagnostic, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
            return document;

        var invocation = root.FindNode(diagnostic.Location.SourceSpan)
            .FirstAncestorOrSelf<InvocationExpressionSyntax>();
        if (invocation is null)
            return document;

        if (invocation.FirstAncestorOrSelf<ExpressionStatementSyntax>() is not { } statement)
            return document;

        var statementExpression = statement.Expression is AwaitExpressionSyntax awaitExpression
            ? awaitExpression.Expression
            : statement.Expression;
        if (statementExpression != invocation)
            return document;

        var newRoot = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia);
        if (newRoot is null)
            return document;

        return document.WithSyntaxRoot(newRoot);
    }
}
