using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Stryker.Abstractions;

namespace Stryker.Core.Mutators;

/// <summary>Mutations for LINQ query expressions.</summary>
public class LinqQueryMutator : MutatorBase<QueryExpressionSyntax>
{
    public override MutationLevel MutationLevel => MutationLevel.Standard;

    public override IEnumerable<Mutation> ApplyMutations(QueryExpressionSyntax node, SemanticModel semanticModel)
    {
        foreach (var body in GetQueryBodies(node))
        {
            foreach (var orderBy in body.Clauses.OfType<OrderByClauseSyntax>())
            {
                foreach (var ordering in orderBy.Orderings)
                {
                    var originalKeyword = ordering.AscendingOrDescendingKeyword;
                    var isDescending = originalKeyword.IsKind(SyntaxKind.DescendingKeyword);
                    var changedOrdering = FlipOrderingDirection(ordering);
                    var replacement = node.ReplaceNode(ordering, changedOrdering);

                    yield return CreateMutation(node, replacement,
                        $"Linq query ordering mutation ({(isDescending ? "descending" : "ascending")} to {(isDescending ? "ascending" : "descending")})");
                }
            }

            foreach (var whereClause in body.Clauses.OfType<WhereClauseSyntax>())
            {
                if (IsUninformativePredicate(whereClause.Condition))
                {
                    continue;
                }

                var replacement = (QueryExpressionSyntax)node.RemoveNode(whereClause, SyntaxRemoveOptions.KeepExteriorTrivia);
                if (replacement is not null)
                {
                    yield return CreateMutation(node, replacement, "Linq query where clause removal");
                }
            }
        }
    }

    internal static OrderingSyntax FlipOrderingDirection(OrderingSyntax ordering)
    {
        var originalKeyword = ordering.AscendingOrDescendingKeyword;
        var isDescending = originalKeyword.IsKind(SyntaxKind.DescendingKeyword);
        var replacementKeyword = SyntaxFactory.Token(isDescending ? SyntaxKind.AscendingKeyword : SyntaxKind.DescendingKeyword)
            .WithTriviaFrom(originalKeyword);
        if (originalKeyword.IsMissing || originalKeyword.RawKind == 0)
        {
            var expressionHasSeparator = ordering.Expression.GetTrailingTrivia()
                .Any(trivia => trivia.IsKind(SyntaxKind.WhitespaceTrivia) || trivia.IsKind(SyntaxKind.EndOfLineTrivia));
            var keywordHasLeadingSeparator = replacementKeyword.LeadingTrivia
                .Any(trivia => trivia.IsKind(SyntaxKind.WhitespaceTrivia) || trivia.IsKind(SyntaxKind.EndOfLineTrivia));
            if (!expressionHasSeparator && !keywordHasLeadingSeparator)
            {
                replacementKeyword = replacementKeyword.WithLeadingTrivia(SyntaxFactory.Space);
            }

            if (ordering.Parent is OrderByClauseSyntax orderBy &&
                orderBy.Orderings.IndexOf(ordering) == orderBy.Orderings.Count - 1 &&
                replacementKeyword.TrailingTrivia.Count == 0)
            {
                replacementKeyword = replacementKeyword.WithTrailingTrivia(SyntaxFactory.Space);
            }
        }

        return ordering.WithAscendingOrDescendingKeyword(replacementKeyword);
    }

    private static IEnumerable<QueryBodySyntax> GetQueryBodies(QueryExpressionSyntax query)
    {
        yield return query.Body;

        var continuation = query.Body.Continuation;
        while (continuation is not null)
        {
            yield return continuation.Body;
            continuation = continuation.Body.Continuation;
        }
    }

    private static bool IsUninformativePredicate(ExpressionSyntax condition)
    {
        while (condition is ParenthesizedExpressionSyntax parenthesized)
        {
            condition = parenthesized.Expression;
        }

        return condition.IsKind(SyntaxKind.TrueLiteralExpression) ||
            condition.DescendantNodesAndSelf(descendIntoChildren: node => node is not QueryExpressionSyntax)
                .Any(node => node is DeclarationExpressionSyntax or SingleVariableDesignationSyntax);
    }

    private static Mutation CreateMutation(QueryExpressionSyntax original, QueryExpressionSyntax replacement, string displayName) => new()
    {
        DisplayName = displayName,
        OriginalNode = original,
        ReplacementNode = replacement,
        Type = Mutator.Linq
    };
}