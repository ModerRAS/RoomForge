// Deliberately OUTSIDE the AudioOptimizer.Tests.Ui namespace this scans: a guard that belongs to the set it audits
// makes its own presence part of its own assertion.
namespace AudioOptimizer.Tests;

using System.Reflection;
using AudioOptimizer.Tests.Ui;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Pins the two halves of the render serialisation where the next render class will run into them: every
/// test-declaring class in <c>AudioOptimizer.Tests.Ui</c> must be in the render collection (the lock), and that
/// collection's definition must opt out of parallelism (the belt). The reasoning lives on the definition, in
/// <see cref="RenderCollection"/>; what these facts enforce is the CONFIGURATION, not the hypothesis — a missing
/// entry reintroduces a race that takes about twelve runs to observe rather than failing here. Neither fact renders
/// or touches a dispatcher, so neither can change render concurrency itself.
/// <para>
/// A namespace count would be the wrong predicate in both directions. Three types in that namespace declare no tests
/// and are deliberately outside the rule: <c>RenderHarness</c> (internal static), <c>FakeAudioBackend</c> (internal
/// sealed, the injected device layer) and <see cref="RenderCollection"/> itself — the last of which is public and
/// non-abstract, so only the test-declaration clause can exclude it, and that clause is xUnit's own discovery rule.
/// </para>
/// <para>
/// Scope: this rule is enforced for test-declaring classes in <c>AudioOptimizer.Tests.Ui</c>. A render-bearing test
/// placed in another namespace is OUTSIDE this scan — such a test must extend this guard's scope as well as carry
/// <c>[Collection(RenderCollection.Name)]</c>. The scope cannot be inferred structurally: the obvious
/// <c>*ViewTests</c> name pattern misses <c>RenderEvidenceTests</c>, and reflection cannot see "calls RenderHarness"
/// without IL analysis, so it is documented here rather than guessed at.
/// </para>
/// </summary>
public sealed class RenderCollectionGuardTests(ITestOutputHelper output)
{
    private const string UiNamespace = "AudioOptimizer.Tests.Ui";

    [Fact]
    public void Every_render_test_class_is_in_the_render_collection()
    {
        // Vacuity guards first: a reflection bug that enumerates nothing would satisfy the "all ..." claim below.
        Assert.True(IsInRenderCollection(typeof(MeasurementWizardViewTests)), "the scan does not even see a known render class");
        Assert.True(IsInRenderCollection(typeof(RenderEvidenceTests)), "the scan does not see the oldest render class");

        Type[] members =
        [
            .. typeof(RenderCollectionGuardTests).Assembly.GetTypes()
                .Where(type => type.Namespace == UiNamespace && HasTests(type)),
        ];
        Assert.True(
            members.Length >= 7,
            $"only {members.Length} test-declaring classes were found in {UiNamespace}; the scan is looking in the wrong place");

        // The NAME is checked, not the presence of some collection attribute: classes in different collections each
        // serialise internally while still running parallel to one another, so a renamed collection would look
        // protected and change nothing.
        Assert.All(members, type => Assert.True(
            IsInRenderCollection(type),
            $"{type.Name} declares tests in {UiNamespace} and this class is not in the render collection"));

        // Negative twins: the same predicate rejects a different collection name and a class with no attribute.
        Assert.False(IsInRenderCollection(typeof(SampleInAnotherCollection)));
        Assert.False(IsInRenderCollection(typeof(SampleWithNoCollection)));
        Assert.False(IsInRenderCollection(typeof(FakeAudioBackend)), "a helper with no tests is not part of the rule");
        output.WriteLine($"{members.Length} classes in '{RenderCollection.Name}': " + string.Join(", ", members.Select(type => type.Name)));
    }

    [Fact]
    public void The_render_collection_definition_disables_parallelism()
    {
        // Found via the ATTRIBUTE, not the test-declaring predicate: this type declares no tests and is legitimately
        // outside that predicate, which is why the lock's scan cannot be the one that finds the belt.
        Type[] definitions =
        [
            .. typeof(RenderCollectionGuardTests).Assembly.GetTypes()
                .Where(type => DefinitionName(type) == RenderCollection.Name),
        ];
        Assert.True(
            definitions.Length == 1,
            $"{definitions.Length} types carry a [CollectionDefinition] named '{RenderCollection.Name}'; exactly one is required");

        Type definition = definitions[0];
        Assert.True(
            definition == typeof(RenderCollection),
            $"[CollectionDefinition(\"{RenderCollection.Name}\")] belongs on {typeof(RenderCollection).Name}, not on {definition.Name}");
        Assert.True(
            DisablesParallelism(definition),
            $"the render collection permits parallel work: {definition.Name} must set DisableParallelization = true, or the "
            + "render classes still run beside the other tests — which is the measured failure, not a style preference");

        // Discriminating twin: the flag is false by default, so DisablesParallelism returning true for the render
        // collection is a read of a real attribute rather than a constant true.
        Assert.False(
            new CollectionDefinitionAttribute("guard-sample-not-render").DisableParallelization,
            "a definition without the flag must read false");
        output.WriteLine($"'{RenderCollection.Name}' definition on {definition.Name}, DisableParallelization = {DisablesParallelism(definition)}");
    }

    /// <summary>Discriminating twin: a class that IS attributed, but with a different collection name.</summary>
    [Collection(RenderCollection.Name + "-other")]
    private sealed class SampleInAnotherCollection;

    /// <summary>Discriminating twin: a class in the namespace with no collection attribute at all.</summary>
    private sealed class SampleWithNoCollection;

    private static bool IsInRenderCollection(Type type)
        => ConstructorName(type, typeof(CollectionAttribute)) == RenderCollection.Name;

    private static string? DefinitionName(Type type) => ConstructorName(type, typeof(CollectionDefinitionAttribute));

    private static bool DisablesParallelism(Type type)
        => type.GetCustomAttribute<CollectionDefinitionAttribute>() is { DisableParallelization: true };

    /// <summary>
    /// The name an attribute was constructed with. xUnit 2.9.3's <c>CollectionAttribute</c> and
    /// <c>CollectionDefinitionAttribute</c> expose no name property — its absence is verified by compiling, not
    /// assumed — so it is read off the attribute data instead.
    /// </summary>
    private static string? ConstructorName(Type type, Type attributeType)
    {
        foreach (CustomAttributeData data in type.GetCustomAttributesData())
        {
            if (data.AttributeType == attributeType && data.ConstructorArguments.Count == 1)
                return data.ConstructorArguments[0].Value as string;
        }

        return null;
    }

    /// <summary>xUnit's own discovery rule, minus the assembly-level clauses that cannot apply to one type.</summary>
    private static bool HasTests(Type type)
        => type is { IsClass: true, IsAbstract: false }
            && type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Any(method => method.GetCustomAttributes<FactAttribute>().Any());
}
