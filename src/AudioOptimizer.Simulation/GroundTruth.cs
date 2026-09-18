namespace AudioOptimizer.Simulation;

using AudioOptimizer.Core;
using AudioOptimizer.Optimization;

/// <summary>
/// The answer the simulation was built from, kept beside the answer the pipeline produced.
/// <para>
/// This object is the ONLY place the truth is allowed to live. The production path — a scenario handing a
/// <c>DualSubMeasurement</c> to <c>SubwooferOptimizer</c> — never sees it; the assembly references make the opposite
/// direction structurally impossible (<c>Optimization</c> does not reference <c>Simulation</c>), and a test asserts
/// that. Tests and the comparison rows of a scenario report read this; the optimizer does not.
/// </para>
/// </summary>
public sealed record GroundTruth(
    SimulationConfig Config,
    RoomModel Room,
    IReadOnlyList<VirtualSubwoofer> Subs,
    FrequencyBand Band,
    IReadOnlyList<GroundTruthPosition> Positions)
{
    public GroundTruthPosition At(string pointId)
        => Positions.FirstOrDefault(position => position.Microphone.Id == pointId)
           ?? throw new ArgumentException($"'{pointId}' is not one of this rig's microphone positions.", nameof(pointId));

    /// <summary>
    /// The modelled responses as the optimizer's input shape — the A/B/AB complex bins taken straight from the
    /// geometry. For ASSERTIONS and for the "theoretical best" reference only: the production input is the
    /// measurement the pipeline produced (<see cref="VirtualLab.AsOptimizerInput"/>), which is a different object
    /// with different numbers in it.
    /// </summary>
    public DualSubMeasurement AsOptimizerInput()
    {
        if (Subs.Count < 2)
            throw new InvalidOperationException("A dual-sub measurement needs two subs; this rig has one.");

        var a = new List<PositionResponse>(Positions.Count);
        var b = new List<PositionResponse>(Positions.Count);
        var ab = new List<PositionResponse>(Positions.Count);
        foreach (GroundTruthPosition position in Positions)
        {
            a.Add(new PositionResponse(position.Microphone.Id, Band, position.BinsA));
            b.Add(new PositionResponse(position.Microphone.Id, Band, position.BinsB));
            ab.Add(new PositionResponse(position.Microphone.Id, Band, position.BinsAb));
        }

        return new DualSubMeasurement(a, b, ab);
    }
}

/// <summary>One microphone's truth: the two (or three) impulse responses and their in-band complex responses.</summary>
public sealed record GroundTruthPosition(
    MeasurementPoint Microphone,
    double[] RirA,
    double[] RirB,
    double[] RirAb,
    FrequencyResponse[] BinsA,
    FrequencyResponse[] BinsB,
    FrequencyResponse[] BinsAb);
