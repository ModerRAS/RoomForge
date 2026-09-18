namespace AudioOptimizer.Tests.Ui;

using Xunit;

/// <summary>
/// The render test classes run one at a time, and this definition is the load-bearing half of that: xUnit's
/// parallelisation contract says "tests within a single test collection will not be run in parallel against each
/// other, but tests in different test collections will run in parallel against each other. By default, there is a
/// test collection per test class." Seven render classes with no collection attribute were therefore seven
/// collections, all rendering beside each other — and that is the measured failure: two threads inside one resource
/// package's part, where <c>System.IO.Packaging.PackagePart</c> synchronises nothing (no lock/Monitor/Interlocked in
/// the type; its <c>_requestedStreams</c> list is per-instance and mutated on every <c>GetStream</c>), producing
/// <c>ArgumentOutOfRangeException</c> from <c>RemoveAt</c> and <c>NullReferenceException</c> from <c>IsStreamClosed</c>
/// inside <c>Application.LoadComponent</c>, in framework frames only. One collection cannot overlap itself.
/// <para>
/// The opt-out that does the work here is <c>DisableParallelization</c> on this definition, which the same docs
/// describe as valid for parallel modes "collections" and "all". Do not simplify it away in favour of a per-class,
/// per-method or per-data-source opt-out: those are spelled <c>DisableParallelism</c> and are silently ignored in
/// collections mode — our mode — so they would buy silence rather than serialisation.
/// </para>
/// <para>
/// The API reference says nothing about concurrent package loading either, so this is a REQUIREMENT for rendering
/// several views in parallel rather than a workaround for a known framework bug — revisit if a runtime change adds
/// synchronisation, or a release note states that concurrent package loading is supported.
/// </para>
/// </summary>
[CollectionDefinition(RenderCollection.Name, DisableParallelization = true)]
public sealed class RenderCollection
{
    /// <summary>
    /// Named once, so the definition and the seven <c>[Collection]</c> attributes cannot drift apart — and
    /// <c>RenderCollectionGuardTests</c> compares against this constant rather than restating the string.
    /// </summary>
    public const string Name = "render";
}
