namespace AudioOptimizer.Visualization;

using System.Globalization;
using AudioOptimizer.Core;

/// <summary>Which reference a level is stated against. Exists so labels and colour scales stay in step.</summary>
public enum LevelReferenceKind
{
    /// <summary>Relative to the mean of the analysed band, in dB. What the measurement pipeline produces today.</summary>
    RelativeToBandMean,

    /// <summary>Relative to the loudest measured bin of the same series, in dB, always ≤ 0.</summary>
    NormalisedToMax,

    /// <summary>Absolute dB SPL against a named calibration, reachable only through a parsed calibration payload.</summary>
    SplCalibrated,
}

/// <summary>The raw (relative) levels a reference needs in order to restate a value in its own units.</summary>
/// <param name="BandMeanDb">Mean of the analysed band, in the pipeline's own (relative) dB.</param>
/// <param name="MaxDb">Loudest bin of the same series, in the pipeline's own (relative) dB.</param>
public sealed record LevelContext(double BandMeanDb = 0.0, double MaxDb = 0.0);

/// <summary>
/// What the plotted magnitudes are referenced to. One source of truth for two things that must never disagree:
/// the axis / colour-bar <see cref="AxisLabel"/> and the colour scale's endpoints
/// (<see cref="DefaultRangeDb"/>). A caller may not pass a literal "dB SPL" string or invent endpoints; it must
/// choose a reference and take both from it.
/// <para>
/// The hierarchy is closed (<c>private</c> base constructor), and the SPL case carries the calibration it
/// claims. There is deliberately no parameterless way to say "SPL": the state without calibration is
/// unrepresentable rather than merely discouraged, the same way an invalid measurement outcome has to carry a
/// reason.
/// </para>
/// </summary>
public abstract record LevelReference
{
    private LevelReference()
    {
    }

    /// <summary>Relative to the band mean. The pipeline has no microphone calibration, so this is what production uses.</summary>
    public static LevelReference RelativeToBandMean { get; } = new RelativeToBandMeanLevels();

    /// <summary>Relative to the loudest measured bin, so the top of the scale is 0 dB by construction.</summary>
    public static LevelReference NormalisedToMax { get; } = new NormalisedToMaxLevels();

    /// <summary>
    /// Absolute SPL against <paramref name="calibration"/>. The calibration is a required argument, so the
    /// label this reference produces names the microphone and sensitivity it is based on.
    /// </summary>
    public static LevelReference SplCalibrated(MicrophoneCalibration calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        return new SplCalibratedLevels(calibration);
    }

    public abstract LevelReferenceKind Kind { get; }

    /// <summary>Axis or colour-bar text, e.g. "dB (relative to band mean)". Derived, never a literal at the call site.</summary>
    public abstract string AxisLabel { get; }

    /// <summary>
    /// Default colour-scale endpoints for this reference, in this reference's own units. The reference sets the
    /// endpoints because it sets what 0 dB means: a relative scale is centred on its mean, a max-normalised one
    /// ends at 0 dB, and an SPL scale spans an absolute window.
    /// </summary>
    public abstract (double MinDb, double MaxDb) DefaultRangeDb { get; }

    /// <summary>Restates a raw pipeline level in this reference's own units.</summary>
    public abstract double ToReferenceUnits(double rawDb, LevelContext context);

    private sealed record RelativeToBandMeanLevels : LevelReference
    {
        public override LevelReferenceKind Kind => LevelReferenceKind.RelativeToBandMean;

        public override string AxisLabel => "dB (relative to band mean)";

        public override (double MinDb, double MaxDb) DefaultRangeDb => (-12.0, 12.0);

        public override double ToReferenceUnits(double rawDb, LevelContext context) => rawDb - context.BandMeanDb;
    }

    private sealed record NormalisedToMaxLevels : LevelReference
    {
        public override LevelReferenceKind Kind => LevelReferenceKind.NormalisedToMax;

        public override string AxisLabel => "normalised dB (0 dB = maximum)";

        public override (double MinDb, double MaxDb) DefaultRangeDb => (-30.0, 0.0);

        public override double ToReferenceUnits(double rawDb, LevelContext context) => rawDb - context.MaxDb;
    }

    private sealed record SplCalibratedLevels(MicrophoneCalibration Calibration) : LevelReference
    {
        public override LevelReferenceKind Kind => LevelReferenceKind.SplCalibrated;

        // The label states the ACHIEVED calibrated extent, not the range anyone hoped for: a file that stops at
        // 359 Hz must not let a 5 kHz value look calibrated. Same configured-versus-achieved pair as the band and
        // the frequency selector.
        public override string AxisLabel
        {
            get
            {
                string extent = Calibration.CalibratedExtent is { } range
                    ? string.Create(CultureInfo.InvariantCulture, $"{range.MinHz:0.#}-{range.MaxHz:0.#} Hz calibrated")
                    : "no correction points";
                return string.Create(CultureInfo.InvariantCulture,
                    $"dB SPL ({Calibration.Identity}, {AxisScale.FormatTick(Calibration.SensitivityDbSplPerFullScale, 1)} dBFS; {extent})");
            }
        }

        public override (double MinDb, double MaxDb) DefaultRangeDb => (60.0, 100.0);

        public override double ToReferenceUnits(double rawDb, LevelContext context) => rawDb + Calibration.SensitivityDbSplPerFullScale;
    }
}
