using System;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Orbit.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TestEchoesConstantAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "ORBIT0006";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Test asserts a production constant equal to a literal",
        "This test asserts a production constant equal to a literal, so it restates the code (CODING_STANDARDS.md LIE-1). Assert the behaviour that the constant drives.",
        "Reliability",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A test that repeats the value of a constant cannot find a defect in the behaviour that uses the constant.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (!TestAssemblyName.Matches(context.Compilation.AssemblyName))
            return;

        var xunitAssertType = context.Compilation.GetTypeByMetadataName("Xunit.Assert");

        context.RegisterSyntaxNodeAction(
            nodeContext => AnalyzeInvocation(nodeContext, xunitAssertType),
            SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context, INamedTypeSymbol? xunitAssertType)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (RestatesInShouldBe(invocation, context.SemanticModel, context.CancellationToken)
            || RestatesInAssertEqual(invocation, xunitAssertType, context.SemanticModel, context.CancellationToken))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
        }
    }

    private static bool RestatesInShouldBe(
        InvocationExpressionSyntax invocation, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "Be",
                Expression: InvocationExpressionSyntax
                {
                    ArgumentList.Arguments.Count: 0,
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Should" } should,
                },
            })
        {
            return false;
        }

        var arguments = invocation.ArgumentList.Arguments;
        return arguments.Count > 0
            && Restates(should.Expression, arguments[0].Expression, semanticModel, cancellationToken);
    }

    private static bool RestatesInAssertEqual(
        InvocationExpressionSyntax invocation,
        INamedTypeSymbol? xunitAssertType,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var arguments = invocation.ArgumentList.Arguments;
        if (xunitAssertType is null || arguments.Count < 2)
            return false;

        if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol { Name: "Equal" } method
            || !SymbolEqualityComparer.Default.Equals(method.ContainingType, xunitAssertType))
        {
            return false;
        }

        var first = arguments[0].Expression;
        var second = arguments[1].Expression;
        return Restates(first, second, semanticModel, cancellationToken)
            || Restates(second, first, semanticModel, cancellationToken);
    }

    private static bool Restates(
        ExpressionSyntax subject,
        ExpressionSyntax expected,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        return IsLiteral(expected)
            && semanticModel.GetSymbolInfo(Unwrap(subject), cancellationToken).Symbol is IFieldSymbol field
            && IsProductionConstant(field);
    }

    private static bool IsLiteral(ExpressionSyntax expression)
    {
        var value = Unwrap(expression);
        if (value is PrefixUnaryExpressionSyntax negation && negation.IsKind(SyntaxKind.UnaryMinusExpression))
            return negation.Operand.IsKind(SyntaxKind.NumericLiteralExpression);

        return value.IsKind(SyntaxKind.StringLiteralExpression)
            || value.IsKind(SyntaxKind.NumericLiteralExpression)
            || value.IsKind(SyntaxKind.TrueLiteralExpression)
            || value.IsKind(SyntaxKind.FalseLiteralExpression)
            || value.IsKind(SyntaxKind.NullLiteralExpression);
    }

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;

        return expression;
    }

    private static bool IsProductionConstant(IFieldSymbol field)
    {
        var isFixedValue = field.IsConst
            || (field.IsStatic && field.IsReadOnly && IsStringOrNumeric(field.Type.SpecialType));
        var assemblyName = field.ContainingAssembly.Name;

        return isFixedValue
            && assemblyName.StartsWith("Orbit.", StringComparison.Ordinal)
            && !TestAssemblyName.Matches(assemblyName);
    }

    private static bool IsStringOrNumeric(SpecialType type)
    {
        return type == SpecialType.System_String
            || (type >= SpecialType.System_SByte && type <= SpecialType.System_Double);
    }
}
