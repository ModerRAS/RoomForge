namespace AudioOptimizer.Art;

using AudioOptimizer.Core;

/// <summary>
/// Extension-track loudspeaker role. Dual-sub A/B/AB stays on <see cref="SubMode"/>; this enum is additive
/// and is not a replacement for that session model.
/// </summary>
public enum SpeakerRole
{
    Unassigned = 0,
    Primary = 1,
    Support = 2,
}

/// <summary>One logical channel in an extension-track session.</summary>
public sealed class ArtChannelConfig
{
    public string ChannelId { get; set; } = "";

    /// <summary>Missing on older documents; readers default this to <see cref="SpeakerRole.Unassigned"/>.</summary>
    public SpeakerRole Role { get; set; } = SpeakerRole.Unassigned;

    public int? HardwareOutputIndex { get; set; }
}

/// <summary>
/// Versioned extension-track session document. Version 1 documents have no role field.
/// This is not the dual-sub <c>session.json</c> schema; that format is left unchanged.
/// </summary>
public sealed class ArtSessionDocument
{
    /// <summary>2 = roles are written explicitly. 1 = channels only; role defaults to Unassigned.</summary>
    public const int CurrentVersion = 2;

    public const int OldestReadableVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string SessionId { get; set; } = "";

    public int SampleRate { get; set; } = 48000;

    public List<ArtChannelConfig> Channels { get; set; } = [];
}

/// <summary>Domain error for an extension-track request that should not run.</summary>
public sealed class ArtValidationException : Exception
{
    public IReadOnlyList<string> Errors { get; }

    public ArtValidationException(string message) : this([message])
    {
    }

    public ArtValidationException(IReadOnlyList<string> errors)
        : base(errors.Count == 0 ? "Validation failed." : string.Join("; ", errors))
    {
        Errors = errors.Count == 0 ? ["Validation failed."] : errors;
    }
}

/// <summary>
/// Phase-calibration knobs. Decomposition is locked to the real cepstrum
/// (<see cref="Decomposition"/>); it is not a magnitude-only equalizer.
/// </summary>
public sealed class PhaseCalibrationOptions
{
    /// <summary>Locked algorithm id. Real cepstrum minimum-phase reconstruction (Oppenheim window).</summary>
    public const string Decomposition = "RealCepstrum";

    /// <summary>Samples earlier than the peak by more than this are held under <see cref="MaxPreRingDb"/>.</summary>
    public double MaxPreRingMs { get; init; } = 2.0;

    /// <summary>Level cap, relative to the FIR peak, for the region outside the allowed pre-ring.</summary>
    public double MaxPreRingDb { get; init; } = -40;

    /// <summary>Bulk delay that pulls the anti-causal excess inverse into the FIR. Must be at least the pre-ring window.</summary>
    public double ModelingDelayMs { get; init; } = 20;

    /// <summary>Minimum FIR length. Rounded up to a power of two and used as the design DFT size.</summary>
    public int FirLength { get; init; } = 2048;

    public double MetricLowHz { get; init; } = 80;

    public double MetricHighHz { get; init; } = 8000;

    /// <summary>P1: also run the same calibrator on support channels. Default off.</summary>
    public bool ApplyToSupports { get; init; }
}

/// <summary>Positive-frequency decomposition of one impulse.</summary>
public sealed class PhaseDecomposition
{
    public int FftSize { get; init; }

    public int SampleRate { get; init; }

    public double[] FrequencyHz { get; init; } = [];

    public double[] Magnitude { get; init; } = [];

    public double[] MinimumPhaseRad { get; init; } = [];

    public double[] ExcessPhaseRad { get; init; } = [];
}

/// <summary>Spatially common excess phase across two or more positions.</summary>
public sealed class CommonExcess
{
    public int FftSize { get; init; }

    public int SampleRate { get; init; }

    public double[] FrequencyHz { get; init; } = [];

    public double[] ExcessPhaseRad { get; init; } = [];

    /// <summary>|mean excess phasor| in [0, 1]. Low values are not inverted.</summary>
    public double[] Coherence { get; init; } = [];
}

/// <summary>Before/after preview. Impulse arrays share a length; frequency arrays share a length.</summary>
public sealed class PhasePreview
{
    public double[] ImpulseBefore { get; init; } = [];

    public double[] ImpulseAfter { get; init; } = [];

    public double[] FrequencyHz { get; init; } = [];

    /// <summary>Nonlinear excess phase of the corrected impulse, one value per <see cref="FrequencyHz"/> bin.</summary>
    public double[] ExcessResidualRad { get; init; } = [];

    public double ExcessMetricBefore { get; init; }

    public double ExcessMetricAfter { get; init; }
}

public sealed class PhaseCalibrationResult
{
    public string ChannelId { get; init; } = "";

    public double[] PhaseFir { get; init; } = [];

    public string PhaseCalVersion { get; init; } = "1";

    public string Decomposition { get; init; } = PhaseCalibrationOptions.Decomposition;

    public int ModelingDelaySamples { get; init; }

    public int SampleRate { get; init; }

    public PhasePreview Preview { get; init; } = new();
}

/// <summary>
/// Support-FIR knobs. Default length is 2048 taps (power of two): at 48 kHz that is 42.7 ms.
/// The designer uses that length as the DFT size, so the band limit is exact on the design grid.
/// </summary>
public sealed class SupportOptimizationOptions
{
    public const int DefaultFirLength = 2048;

    public int FirLength { get; init; } = DefaultFirLength;

    public double BandLowHz { get; init; } = 20;

    public double BandHighHz { get; init; } = 150;

    /// <summary>Cap on in-band RMS(|support contribution|) / RMS(|primary|).</summary>
    public double SupportLevelMaxDb { get; init; } = -6;

    /// <summary>Diagonal load on the per-bin normal equations. Negative is rejected.</summary>
    public double Regularization { get; init; } = 1e-4;

    /// <summary>
    /// Flat real in-band target. Null means the complex mean of the primary across positions.
    /// </summary>
    public double? FlatTargetGain { get; init; }
}

public sealed class SupportPosition
{
    public required string PositionId { get; init; }

    public required ImpulseResponse Primary { get; init; }

    public required IReadOnlyDictionary<string, ImpulseResponse> Supports { get; init; }
}

public sealed class SupportProblem
{
    public required string PrimaryChannelId { get; init; }

    public required IReadOnlyList<string> SupportChannelIds { get; init; }

    public required IReadOnlyList<SupportPosition> Positions { get; set; }

    public SupportOptimizationOptions Options { get; set; } = new();
}

public sealed class SupportPreview
{
    public double[] FrequencyHz { get; init; } = [];

    public double[] PredictedMagnitudeDb { get; init; } = [];

    public IReadOnlyDictionary<string, double[]> SupportContributionDb { get; init; }
        = new Dictionary<string, double[]>();

    /// <summary>Cap minus achieved level. Positive when the cap is not binding.</summary>
    public double LevelHeadroomDb { get; init; }
}

public sealed record SupportOptimizationResult
{
    /// <summary>Stable id consumed by export. Solver swaps must not change this.</summary>
    public const string ExportContractVersion = "1";

    public string OptimizerVersion { get; init; } = "art-p0";

    public string OptimizerId { get; init; } = "";

    public string ContractVersion { get; init; } = ExportContractVersion;

    public IReadOnlyDictionary<string, double[]> SupportFirs { get; init; }
        = new Dictionary<string, double[]>();

    public double BandLowHz { get; init; }

    public double BandHighHz { get; init; }

    public double SupportLevelDb { get; init; }

    public double AchievedSupportLevelDb { get; init; }

    public int SampleRate { get; init; }

    public int FirLength { get; init; }

    public PhaseCalibrationResult? PrimaryPhase { get; init; }

    public SupportPreview Preview { get; init; } = new();
}

/// <summary>Independent phase-bypass and support-bypass flags. All four combinations are representable.</summary>
public readonly record struct CorrectionBypass(bool Phase, bool Support)
{
    public static IReadOnlyList<CorrectionBypass> All { get; } =
    [
        new(false, false),
        new(true, false),
        new(false, true),
        new(true, true),
    ];
}
