namespace AudioOptimizer.Simulation;

/// <summary>
/// The checked-in seed list of known-sensitive scenarios, kept as plain C# data rather than a file: the list is
/// compile-time checked, needs no parser or deserializer, and cannot drift from the generator that produced it.
/// <para>
/// Every seed here is part of EVERY regression plan (<see cref="RandomScenarioGenerator.Plan"/>), even when its index
/// is outside the mode's generated range, and every one is replayable with <c>dotnet run … -- --replay &lt;seed&gt;</c>.
/// So the maintenance flow for a new failure is: the run prints the exact seed and says to add it here; append it with
/// a comment naming the index and the classification it failed with. The list never gets pruned for speed.
/// </para>
/// </summary>
public static class RegressionFixtures
{
    /// <summary>
    /// Fixture seeds, each <c>MasterSeed + index</c> (see <see cref="RandomScenarioGenerator.SeedForIndex"/>).
    /// Comments state the draw family (index % 3 == 0 → independent, otherwise misaligned) and imperfection class
    /// (index % 10: 0–6 light, 7–8 moderate, 9 stress) the index was drawn with.
    /// </summary>
    public static readonly IReadOnlyList<int> Seeds =
    [
        20260919,   // index 0  — independent, light    (the baseline draw; the optimizer legitimately declines it)
        20260926,   // index 7  — misaligned,  moderate (first index of the moderate band)
        20260928,   // index 9  — independent, stress   (first index of the stress band)
        20260938,   // index 19 — misaligned,  stress   (last index of the quick plan)
    ];

    public static bool Contains(int seed) => Seeds.Contains(seed);
}
