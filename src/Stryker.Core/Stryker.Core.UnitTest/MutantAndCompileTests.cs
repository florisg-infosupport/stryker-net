using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;
using Stryker.Abstractions;
using Stryker.Configuration.Options;
using Stryker.Core.Compiling;
using Stryker.Core.InjectedHelpers;
using Stryker.Core.Mutants;
using Stryker.Core.Mutators;
using Stryker.Core.UnitTest.Compiling;

namespace Stryker.Core.UnitTest;

[TestClass]
public class MutantAndCompileTests : TestBase
{
    private readonly CodeInjection _injector = new();

    [TestMethod]
    public void RollbackShouldPreserveDirectives()
    {
        const string source = @"using System.Diagnostics.CodeAnalysis;
namespace Lib;

[Experimental(""MYEXP001"")]
public static class Experimental
{
    public static string? Find(string key) => key == ""alpha"" ? key : null;
}

public static class Consumer
{
    public static int Get(string key)
    {
#pragma warning disable MYEXP001
        return key switch {
            { } k when Experimental.Find(k) is { } found => found.Length,
            _ => 0
        };
#pragma warning restore MYEXP001
    }
}";

        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        Type[] typeToLoad = [typeof(object), typeof(List<>), typeof(Enumerable), typeof(Nullable<>), typeof(MemoryMappedFile),
            typeof(ExperimentalAttribute), typeof(MemoryMappedViewAccessor)];
        var syntaxTrees = new List<SyntaxTree> { syntaxTree };
        syntaxTrees.AddRange(_injector.GetHelpersSyntaxTreesToInject(new CSharpParseOptions(LanguageVersion.CSharp14)));
        var mutator = new CsharpMutantOrchestrator(new MutantPlacer(_injector), options: new StrykerOptions
        {
            MutationLevel = MutationLevel.Complete,
        });

        List<MetadataReference> metadataReferences = [.. typeToLoad.Select(t => t.Assembly.Location).Distinct().
            Select(l => MetadataReference.CreateFromFile(l))];
        Assembly.GetAssembly(typeof(MemoryMappedViewAccessor)).GetReferencedAssemblies().Select(Assembly.Load).Select(a => a.Location).
            Distinct().Select(l => MetadataReference.CreateFromFile(l)).ToList().ForEach(metadataReferences.Add);
        var compilation = CSharpCompilation.Create("TestCompilation",
            syntaxTrees: syntaxTrees,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable),
            references: metadataReferences
        );
        var actualNode = mutator.Mutate(syntaxTree, compilation.GetSemanticModel(syntaxTree));

        compilation = compilation.ReplaceSyntaxTree(syntaxTree, actualNode);
        var target = new CSharpRollbackProcess();
        using var ms = new MemoryStream();
        var compileResult = compilation.Emit(ms);

        compileResult.Success.ShouldBeFalse();
        var compilerWrapper = new CompilerWrapper(compilation);

        target.RollbackMutationsInError(compilerWrapper, compileResult.Diagnostics, ICSharpRollbackProcess.Mode.Normal, false);

        var rollbackedResult = compilerWrapper.Emit(ms);

        rollbackedResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
    }

    [TestMethod]
    public void LinqQueryMutationsShouldEmitAcrossQueryContexts()
    {
        var source = """
using System.Collections.Generic;
using System.Linq;
class QuerySamples
{
    static IEnumerable<int> Local(int[] values)
    {
        var result = from value in values where value > 0 where value < 10 select value;
        return result;
    }
    static IEnumerable<int> Returned(int[] values) => from value in values orderby value descending, value select value;
    static int[] PassedToCall(int[] values) => Enumerable.ToArray(from value in values orderby value select value);
    static IEnumerable<IGrouping<int, int>> Continued(int[] values) => from value in values group value by value % 2 into grouped where grouped.Any() select grouped;
    static IEnumerable<int> Nested(int[] values) => from value in values where values.Any(other => other > value) select value;
    static IEnumerable<object> Anonymous(int[] values) => from value in values select new { Value = value };
    static IQueryable<int> Queryable(IQueryable<int> values) => from value in values where value > 0 orderby value select value;
}
""";

        var compilation = CreateMutatedCompilation(source, new LinqQueryMutator());
        using var output = new MemoryStream();

        var result = compilation.Emit(output);

        result.Success.ShouldBeTrue(string.Join(Environment.NewLine, result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
        result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
    }

    [TestMethod]
    public void MixedLinqQueryAndPredicateMutationsShouldEmit()
    {
        var source = "using System.Collections.Generic; using System.Linq; class QuerySamples { static IEnumerable<int> M(int[] values) => from value in values where value > 0 orderby value select value; }";

        var compilation = CreateMutatedCompilation(source, new LinqQueryMutator(), new BinaryExpressionMutator());
        using var output = new MemoryStream();

        var result = compilation.Emit(output);

        result.Success.ShouldBeTrue(string.Join(Environment.NewLine, result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
        result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
    }

    private static CSharpCompilation CreateMutatedCompilation(string source, params IMutator[] mutators)
    {
        var injector = new CodeInjection();
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp14);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        Type[] typesToLoad = [typeof(object), typeof(List<>), typeof(Enumerable), typeof(Queryable), typeof(IQueryable<>), typeof(Nullable<>), typeof(MemoryMappedFile),
            typeof(ExperimentalAttribute), typeof(MemoryMappedViewAccessor)];
        List<MetadataReference> references = [.. typesToLoad.Select(type => type.Assembly.Location).Distinct()
            .Select(location => MetadataReference.CreateFromFile(location))];
        Assembly.GetAssembly(typeof(MemoryMappedViewAccessor)).GetReferencedAssemblies().Select(Assembly.Load)
            .Select(assembly => assembly.Location).Distinct().Select(location => MetadataReference.CreateFromFile(location))
            .ToList().ForEach(references.Add);
        var syntaxTrees = new List<SyntaxTree> { syntaxTree };
        syntaxTrees.AddRange(injector.GetHelpersSyntaxTreesToInject(parseOptions));
        var compilation = CSharpCompilation.Create("LinqQueryCompilation", syntaxTrees: syntaxTrees,
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable));
        var orchestrator = new CsharpMutantOrchestrator(new MutantPlacer(injector), mutators,
            new StrykerOptions { MutationLevel = MutationLevel.Standard });
        var mutatedTree = orchestrator.Mutate(syntaxTree, compilation.GetSemanticModel(syntaxTree));

        return compilation.ReplaceSyntaxTree(syntaxTree, mutatedTree);
    }
}
