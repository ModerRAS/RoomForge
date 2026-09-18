namespace AudioOptimizer.Visualization;

using System.Collections.Immutable;

/// <summary>
/// The colour mapping for one figure: dB → colour, with the endpoints taken from a
/// <see cref="LevelReference"/> rather than from a constant. That keeps the picture and its caption in step —
/// a scale labelled "relative to band mean" is centred on 0 dB, one labelled "normalised dB" ends at 0 dB, and
/// only a calibrated reference may span an absolute SPL window.
/// </summary>
public sealed record ColourScale
{
    /// <summary>
    /// Low → high ramp. Four stops is enough to read a room mode without a turbo palette. Immutable by type and not
    /// by convention, because an element write here would put writable shared state on the render path — the basis
    /// for serialising the render test classes — so reverting this type is not a style choice.
    /// </summary>
    private static readonly ImmutableArray<RgbColour> Ramp =
    [
        new(0x0B, 0x3D, 0x91),   // deep blue — a null
        new(0x2E, 0x9B, 0xD6),   // blue
        new(0xF2, 0xE2, 0x4C),   // yellow
        new(0xD6, 0x20, 0x2A),   // red — a peak
    ];

    public ColourScale(LevelReference reference, double minDb, double maxDb)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!double.IsFinite(minDb) || !double.IsFinite(maxDb) || maxDb <= minDb)
            throw new ArgumentOutOfRangeException(nameof(maxDb), maxDb, $"The colour range must be increasing; got [{minDb}, {maxDb}].");

        Reference = reference;
        MinDb = minDb;
        MaxDb = maxDb;
    }

    public LevelReference Reference { get; }

    public double MinDb { get; }

    public double MaxDb { get; }

    /// <summary>The endpoints the reference prescribes — the default for a figure that does not tighten them.</summary>
    public static ColourScale For(LevelReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        (double minDb, double maxDb) = reference.DefaultRangeDb;
        return new ColourScale(reference, minDb, maxDb);
    }

    /// <summary>Colour-bar caption: the reference's own label plus the span actually used.</summary>
    public string Label => $"{Reference.AxisLabel}  [{AxisScale.FormatTick(MinDb)} … {AxisScale.FormatTick(MaxDb)}]";

    /// <summary>Position of a level on the ramp, clamped into [0, 1].</summary>
    public double FractionAt(double levelDb) => Math.Clamp((levelDb - MinDb) / (MaxDb - MinDb), 0.0, 1.0);

    public RgbColour ColourAt(double levelDb)
    {
        double scaled = FractionAt(levelDb) * (Ramp.Length - 1);
        int index = Math.Min((int)Math.Floor(scaled), Ramp.Length - 2);
        return RgbColour.Lerp(Ramp[index], Ramp[index + 1], scaled - index);
    }
}
