using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Stryker.Abstractions;
using Stryker.Abstractions.Options;
using Stryker.Abstractions.ProjectComponents;
using Stryker.Core.Mutators;

namespace Stryker.Core.MutantFilters;

/// <summary>
/// Checks if the linq expression of the mutant should be excluded.
/// </summary>
/// <seealso cref="IMutantFilter" />
public class ExcludeLinqExpressionFilter : IMutantFilter
{
    public MutantFilter Type => MutantFilter.IgnoreLinqMutation;
    public string DisplayName => "linq expression filter";
    private SyntaxTriviaRemover _triviaRemover { get; init; } = new SyntaxTriviaRemover();

    public IEnumerable<IMutant> FilterMutants(IEnumerable<IMutant> mutants, IReadOnlyFileLeaf file, IStrykerOptions options) => options.ExcludedLinqExpressions.Any() ?
                mutants.Where(m => m.Mutation.Type != Mutator.Linq || !IsIgnoreMutation(m.Mutation, options)) :
                mutants;

    private bool IsIgnoreMutation(Mutation mutation, IStrykerOptions options)
    {
        if (mutation.OriginalNode is QueryExpressionSyntax originalQuery &&
            mutation.ReplacementNode is QueryExpressionSyntax replacementQuery)
        {
            return GetChangedQueryExpression(originalQuery, replacementQuery) is { } expression &&
                options.ExcludedLinqExpressions.Contains(expression);
        }

        return IsIgnoreExpression(mutation.OriginalNode, options);
    }

    private static LinqExpression? GetChangedQueryExpression(QueryExpressionSyntax original, QueryExpressionSyntax replacement)
    {
        foreach (var body in GetQueryBodies(original))
        {
            foreach (var whereClause in body.Clauses.OfType<WhereClauseSyntax>())
            {
                if (original.RemoveNode(whereClause, SyntaxRemoveOptions.KeepExteriorTrivia) is QueryExpressionSyntax withoutWhere &&
                    SyntaxFactory.AreEquivalent(withoutWhere, replacement))
                {
                    return LinqExpression.Where;
                }
            }

            foreach (var ordering in body.Clauses.OfType<OrderByClauseSyntax>().SelectMany(clause => clause.Orderings))
            {
                var originalKeyword = ordering.AscendingOrDescendingKeyword;
                var isDescending = originalKeyword.IsKind(SyntaxKind.DescendingKeyword);
                var changedOrdering = LinqQueryMutator.FlipOrderingDirection(ordering);
                if (SyntaxFactory.AreEquivalent(original.ReplaceNode(ordering, changedOrdering), replacement))
                {
                    var isSecondaryKey = ordering.Parent is OrderByClauseSyntax orderBy && orderBy.Orderings.IndexOf(ordering) > 0;
                    return (isSecondaryKey, isDescending) switch
                    {
                        (false, false) => LinqExpression.OrderBy,
                        (false, true) => LinqExpression.OrderByDescending,
                        (true, false) => LinqExpression.ThenBy,
                        (true, true) => LinqExpression.ThenByDescending
                    };
                }
            }
        }

        return null;
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

    private bool IsIgnoreExpression(SyntaxNode syntaxNode, IStrykerOptions options) =>
        syntaxNode switch
        {
            // Check if the current node is an invocation. This will also ignore invokable properties like `Func<bool> MyProp { get;}`
            InvocationExpressionSyntax invocation => MatchesAnIgnoredExpression(_triviaRemover.Visit(invocation.Expression).ToString(), options),

            // Check if the current node is an object creation syntax (constructor invocation).
            ObjectCreationExpressionSyntax creation => MatchesAnIgnoredExpression(_triviaRemover.Visit(creation.Type) + ".ctor", options),

            // Traverse the tree upwards.
            SyntaxNode node when node.Parent != null => IsIgnoreExpression(syntaxNode.Parent, options),
            _ => false,
        };


    private static bool MatchesAnIgnoredExpression(string expressionString, IStrykerOptions options) => options.ExcludedLinqExpressions.Any(r => expressionString.EndsWith(Enum.GetName(r)));

    /// <summary>
    /// Removes comments, whitespace, and other junk from a syntax tree.
    /// </summary>
    private sealed class SyntaxTriviaRemover : CSharpSyntaxRewriter
    {
        public override SyntaxTrivia VisitTrivia(SyntaxTrivia trivia) => default;
    }
}
