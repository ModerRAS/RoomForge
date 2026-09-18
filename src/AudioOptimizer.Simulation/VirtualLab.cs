namespace AudioOptimizer.Simulation;

using AudioOptimizer.Core;
using AudioOptimizer.Dsp;
using AudioOptimizer.Measurement;
using AudioOptimizer.Optimization;

/// <summary>One captured point: which configuration, where, and what the shipped measurement chain made of it.</summary>
public sealed record SimulatedMeasurement(SubMode Mode, MeasurementPoint Point, PointMeasurementResult Result)
{
    public string Id => $"{Mode}/{Point.Id}";
}

/// <summary>
/// The Virtual Acoustic Lab: a rig, plus the product's own measurement chain pointed at it.
/// <para>
/// Every point goes through <c>PointMeasurement.Run</c> — the same call the smoke test and the hardware runner make —
/// with the virtual backend in place of WASAPI. So the numbers here are the product's numbers: the same sweep, the
/// same inverse filter, the same deconvolution, the same alignment, the same FFT, the same complex frequency
/// response and the same quality checks. Nothing in this class computes a frequency response.
/// </para>
/// </summary>
public sealed class VirtualLab
{
    private readonly SimulationConfig _config;

    public VirtualLab(
        SimulationConfig config,
        IReadOnlyList<VirtualSubwoofer> subs,
        IReadOnlyList<MeasurementPoint> microphones)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(subs);
        ArgumentNullException.ThrowIfNull(microphones);
        config.Validate();

        _config = config;
        Rig = new VirtualRoom(config, config.Room, subs, microphones);
        Backend = new VirtualAudioBackend(Rig);
        GroundTruth = BuildGroundTruth(config, Rig);
    }

    public VirtualRoom Rig { get; }

    public VirtualAudioBackend Backend { get; }

    /// <summary>What the room really is. Assertions and comparison rows only — never an optimizer input.</summary>
    public GroundTruth GroundTruth { get; }

    /// <summary>The analysis band: the sweep's own band, so nothing downstream can report a frequency it never excited.</summary>
    public FrequencyBand Band => FrequencyBand.Of(_config.Sweep);

    /// <summary>
    /// One capture through the shipped chain: play the sweep on the mode's sub configuration, record it at the point's
    /// microphone, deconvolve, align, transform, and grade.
    /// </summary>
    public SimulatedMeasurement Measure(SubMode mode, MeasurementPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        PointMeasurementResult result = PointMeasurement.Run(
            Backend,
            Backend.SubDevice(mode),
            Backend.MicrophoneDevice(point),
            _config.BackendSettings,
            _config.Sweep,
            TimeSpan.FromSeconds(_config.PreRollSeconds),
            TimeSpan.FromSeconds(_config.PostRollSeconds),
            _config.PlaybackGain);

        return new SimulatedMeasurement(mode, point, result);
    }

    /// <summary>Every configuration this rig can drive, over every microphone — 27 points × 3 modes when there are two subs.</summary>
    public IReadOnlyList<SimulatedMeasurement> MeasureAll()
    {
        var measurements = new List<SimulatedMeasurement>(Backend.SupportedModes.Count * Rig.Microphones.Count);
        foreach (SubMode mode in Backend.SupportedModes)
            foreach (MeasurementPoint point in Rig.Microphones)
                measurements.Add(Measure(mode, point));
        return measurements;
    }

    /// <summary>
    /// The measurement set the optimizer is allowed to see: only what the pipeline produced, only inside the band the
    /// sweep excited. The ground truth never reaches this path.
    /// </summary>
    public DualSubMeasurement AsOptimizerInput(IReadOnlyList<SimulatedMeasurement> measurements)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        if (Rig.Subs.Count < 2) throw new InvalidOperationException("A dual-sub measurement needs two subs; this rig has one.");

        var a = new List<PositionResponse>();
        var b = new List<PositionResponse>();
        var ab = new List<PositionResponse>();
        foreach (SimulatedMeasurement measurement in measurements)
        {
            FrequencyResponse[] bins = MeasurementSession.InBand(measurement.Result.Response, Band);
            if (bins.Length == 0) continue;
            var position = new PositionResponse(measurement.Point.Id, Band, bins);
            switch (measurement.Mode)
            {
                case SubMode.A: a.Add(position); break;
                case SubMode.B: b.Add(position); break;
                case SubMode.AB: ab.Add(position); break;
                default: throw new ArgumentOutOfRangeException(nameof(measurements), measurement.Mode, "Unknown sub mode.");
            }
        }

        var set = new DualSubMeasurement(a, b, ab.Count > 0 ? ab : null);
        set.Validate();
        return set;
    }

    private static GroundTruth BuildGroundTruth(SimulationConfig config, VirtualRoom rig)
    {
        var positions = new List<GroundTruthPosition>(rig.Microphones.Count);
        for (int m = 0; m < rig.Microphones.Count; m++)
        {
            double[] rirA = rig.ImpulseResponseOf(0, m);
            double[] rirB = rig.Subs.Count > 1 ? rig.ImpulseResponseOf(1, m) : [];
            double[] rirAb = rig.Subs.Count > 1 ? rig.CombinedImpulseResponse(m) : [];

            positions.Add(new GroundTruthPosition(
                rig.Microphones[m], rirA, rirB, rirAb,
                InBand(config, rirA, rig.Subs[0].PhaseDegrees),
                rirB.Length > 0 ? InBand(config, rirB, rig.Subs[1].PhaseDegrees) : [],
                rirAb.Length > 0 ? InBand(config, rirAb, 0.0) : []));
        }

        return new GroundTruth(config, rig.Room, rig.Subs, FrequencyBand.Of(config.Sweep), positions);
    }

    /// <summary>
    /// The truth's complex response: the same FFT the product uses, on the SAME frequency grid
    /// (<see cref="SimulationConfig.CaptureFftSize"/>), cut to the sweep's band. The phase setting is applied here in
    /// the frequency domain, which is where the model puts it and where the sub's own drive-side rotation lands once
    /// the chain has deconvolved it.
    /// </summary>
    private static FrequencyResponse[] InBand(SimulationConfig config, double[] impulseResponse, double phaseDegrees)
    {
        FrequencyResponse[] bins = MeasurementSession.InBand(
            FrequencyResponseCalculator.Compute(
                new ImpulseResponse(impulseResponse, config.SampleRate), WindowType.Rectangular, config.CaptureFftSize),
            FrequencyBand.Of(config.Sweep));

        if (phaseDegrees % 360.0 == 0.0) return bins;

        double radians = phaseDegrees * Math.PI / 180.0;
        return [.. bins.Select(bin => SubwooferModel.Bin(
            bin.FrequencyHz, ComplexMath.Rotate(new System.Numerics.Complex(bin.Real, bin.Imag), radians)))];
    }
}
