using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Orbit.Analyzers;

/// <summary>
/// Forbids narration comments. Code must read without prose. The only comments allowed are
/// XML-doc comments (<c>///</c> or <c>/** */</c>, which document a symbol's intent and contract)
/// and a WHY note that links an upstream issue/PR/doc URL (a real external constraint). Everything
/// else is reported as an error and stripped by the fixer. Trivia-based, so <c>//</c> sequences
/// inside strings, verbatim strings, and interpolations are never touched. Generated code (EF
/// migrations, designer files) is excluded.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NoCommentsAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "ORBIT0001";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Remove this comment",
        "Remove this comment. Only XML-doc comments (/// or /** */) or a WHY note linking an upstream URL are allowed -- rename or extract instead of narrating.",
        "Style",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Narration comments are banned; document intent with XML-doc or link an upstream URL for a WHY note.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxTreeAction(AnalyzeTree);
    }

    private static void AnalyzeTree(SyntaxTreeAnalysisContext context)
    {
        if (IsExcludedPath(context.Tree.FilePath))
            return;

        var root = context.Tree.GetRoot(context.CancellationToken);
        foreach (var trivia in root.DescendantTrivia())
        {
            if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                && !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
            {
                continue;
            }

            if (IsAllowed(trivia))
                continue;

            context.ReportDiagnostic(Diagnostic.Create(Rule, trivia.GetLocation()));
        }
    }

    private static bool IsAllowed(SyntaxTrivia trivia)
    {
        var text = trivia.ToString();

        if (trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
        {
            if (text.StartsWith("/**", StringComparison.Ordinal))
                return true;
        }
        else
        {
            var value = text.Length > 2 ? text.Substring(2).Trim() : string.Empty;
            if (value.StartsWith("/", StringComparison.Ordinal))
                return true;
        }

        return text.IndexOf("http://", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("https://", StringComparison.OrdinalIgnoreCase) >= 0;
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
