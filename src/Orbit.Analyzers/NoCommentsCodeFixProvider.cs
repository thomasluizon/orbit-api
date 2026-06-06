using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;

namespace Orbit.Analyzers;

/// <summary>
/// Removes a banned comment reported by <see cref="NoCommentsAnalyzer"/>. A full-line comment takes
/// its line (leading indent and trailing newline) with it; a trailing comment drops the whitespace
/// that preceded it, leaving the code intact.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(NoCommentsCodeFixProvider))]
[Shared]
public sealed class NoCommentsCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds =>
        ImmutableArray.Create(NoCommentsAnalyzer.DiagnosticId);

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
                    "Remove comment",
                    ct => RemoveCommentAsync(context.Document, diagnostic, ct),
                    equivalenceKey: "RemoveComment"),
                diagnostic);
        }
    }

    private static async Task<Document> RemoveCommentAsync(
        Document document, Diagnostic diagnostic, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
            return document;

        var comment = root.FindTrivia(diagnostic.Location.SourceSpan.Start);
        var token = comment.Token;

        var leadingIndex = token.LeadingTrivia.IndexOf(comment);
        if (leadingIndex >= 0)
        {
            var newLeading = StripComment(token.LeadingTrivia, leadingIndex);
            return document.WithSyntaxRoot(root.ReplaceToken(token, token.WithLeadingTrivia(newLeading)));
        }

        var trailingIndex = token.TrailingTrivia.IndexOf(comment);
        if (trailingIndex >= 0)
        {
            var newTrailing = StripComment(token.TrailingTrivia, trailingIndex);
            return document.WithSyntaxRoot(root.ReplaceToken(token, token.WithTrailingTrivia(newTrailing)));
        }

        return document.WithSyntaxRoot(root.ReplaceTrivia(comment, Enumerable.Empty<SyntaxTrivia>()));
    }

    private static SyntaxTriviaList StripComment(SyntaxTriviaList list, int index)
    {
        var trivia = new List<SyntaxTrivia>(list);
        trivia.RemoveAt(index);

        if (index < trivia.Count && trivia[index].IsKind(SyntaxKind.EndOfLineTrivia))
        {
            trivia.RemoveAt(index);
            if (index - 1 >= 0 && trivia[index - 1].IsKind(SyntaxKind.WhitespaceTrivia))
                trivia.RemoveAt(index - 1);
        }
        else if (index - 1 >= 0 && trivia[index - 1].IsKind(SyntaxKind.WhitespaceTrivia))
        {
            trivia.RemoveAt(index - 1);
        }

        return SyntaxFactory.TriviaList(trivia);
    }
}
