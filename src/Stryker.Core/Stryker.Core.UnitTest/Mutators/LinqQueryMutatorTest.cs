using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;
using Stryker.Abstractions;
using Stryker.Core.Mutators;

namespace Stryker.Core.UnitTest.Mutators;

[TestClass]
public class LinqQueryMutatorTest : TestBase
{
    [TestMethod]
    public void ShouldBeMutationLevelStandard()
    {
        new LinqQueryMutator().MutationLevel.ShouldBe(MutationLevel.Standard);
    }

    [TestMethod]
    [DataRow("orderby x select x", "orderby x descending select x")]
    [DataRow("orderby x ascending select x", "orderby x descending select x")]
    [DataRow("orderby x descending select x", "orderby x ascending select x")]
    public void ShouldFlipOrderingDirection(string query, string expected)
    {
        var mutation = Mutate($"from x in xs {query}").ShouldHaveSingleItem();

        mutation.ReplacementNode.ToString().ShouldBe($"from x in xs {expected}");
        mutation.OriginalNode.ShouldBeOfType<QueryExpressionSyntax>();
        mutation.Type.ShouldBe(Mutator.Linq);
        mutation.DisplayName.ShouldContain("ordering mutation");
    }

    [TestMethod]
    public void ShouldCreateAnIndependentMutationForEachOrdering()
    {
        var mutations = Mutate("from x in xs orderby x descending, x.Name select x").ToList();

        mutations.Count.ShouldBe(2);
        mutations[0].ReplacementNode.ToString().ShouldBe("from x in xs orderby x ascending, x.Name select x");
        mutations[1].ReplacementNode.ToString().ShouldBe("from x in xs orderby x descending, x.Name descending select x");
    }

    [TestMethod]
    public void ShouldRestartOrderingIndexForEachOrderByClause()
    {
        var mutations = Mutate("from x in xs orderby x descending orderby x.Name select x").ToList();

        mutations.Count.ShouldBe(2);
        mutations[0].ReplacementNode.ToString().ShouldBe("from x in xs orderby x ascending orderby x.Name select x");
        mutations[1].ReplacementNode.ToString().ShouldBe("from x in xs orderby x descending orderby x.Name descending select x");
    }

    [TestMethod]
    public void ShouldRemoveEachWhereClauseIndependently()
    {
        var mutations = Mutate("from x in xs where x > 0 where x < 10 select x").ToList();

        mutations.Count.ShouldBe(2);
        ShouldHaveReplacement(mutations[0], "from x in xs where x < 10 select x");
        ShouldHaveReplacement(mutations[1], "from x in xs where x > 0 select x");
        mutations.ShouldAllBe(mutation => mutation.DisplayName == "Linq query where clause removal");
    }

    [TestMethod]
    public void ShouldHandleGroupingsAndContinuations()
    {
        Mutate("from x in xs where x > 0 group x by x.Key into grouped where grouped.Any() orderby grouped.Key select grouped")
            .Count().ShouldBe(3);
    }

    [TestMethod]
    public void ShouldHandleSelectEndingAndPreserveOtherClauses()
    {
        var mutation = Mutate("from x in xs let y = x + 1 join z in zs on x equals z where y > 1 select new { x, y, z }")
            .ShouldHaveSingleItem();

        SyntaxFactory.AreEquivalent(ParseQuery("from x in xs let y = x + 1 join z in zs on x equals z select new { x, y, z }"), mutation.ReplacementNode).ShouldBeTrue();
    }

    [TestMethod]
    public void ShouldNotGenerateDuplicateOuterMutationsForNestedQueries()
    {
        var query = ParseQuery("from x in xs where (from y in ys where y > 0 select y).Any() select x");

        new LinqQueryMutator().ApplyMutations(query, null).Count().ShouldBe(1);
    }

    [TestMethod]
    [DataRow("from x in xs where (true) select x")]
    [DataRow("from x in xs where x is var value select x")]
    [DataRow("from x in xs where x is { Value: > 0 } value select x")]
    [DataRow("from x in xs where int.TryParse(x, out var value) select x")]
    public void ShouldSkipTrueAndVariableDeclaringPredicates(string query)
    {
        Mutate(query).ShouldBeEmpty();
    }

    [TestMethod]
    public void ShouldPreserveCommentsWhenRemovingWhereClause()
    {
        var mutation = Mutate("from x in xs /*before*/ where x > 0 /*after*/ select x").ShouldHaveSingleItem();

        mutation.ReplacementNode.ToFullString().ShouldContain("/*before*/");
        mutation.ReplacementNode.ToFullString().ShouldContain("/*after*/");
    }

    [TestMethod]
    public void ShouldNotThrowForIncompleteQuery()
    {
        Should.NotThrow(() => Mutate("from x in xs where").ToList());
    }

    [TestMethod]
    public void ShouldNotMutateQueryWithoutSupportedClauses()
    {
        Mutate("from x in xs select x").ShouldBeEmpty();
    }

    private static System.Collections.Generic.IEnumerable<Mutation> Mutate(string query)
    {
        var node = ParseQuery(query);
        return new LinqQueryMutator().ApplyMutations(node, null);
    }

    private static void ShouldHaveReplacement(Mutation mutation, string expected) =>
        SyntaxFactory.AreEquivalent(ParseQuery(expected), mutation.ReplacementNode).ShouldBeTrue();

    private static QueryExpressionSyntax ParseQuery(string query) =>
        CSharpSyntaxTree.ParseText($"class C {{ object M() => {query}; }}")
            .GetRoot()
            .DescendantNodes()
            .OfType<QueryExpressionSyntax>()
            .First();
}