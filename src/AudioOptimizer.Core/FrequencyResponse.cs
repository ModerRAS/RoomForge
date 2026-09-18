namespace AudioOptimizer.Core;

/// <summary>
/// One bin of a complex frequency response. Real/Imag are the source of truth — the later
/// dual-subwoofer optimizer needs the complex sum, not a level — while MagnitudeDb and the two phase
/// views are derived by <c>FrequencyResponseCalculator</c>.
/// </summary>
/// <param name="FrequencyHz">Bin frequency k·fs/N.</param>
/// <param name="Real">Re{H(f)} after coherent-gain normalisation.</param>
/// <param name="Imag">Im{H(f)} after coherent-gain normalisation.</param>
/// <param name="MagnitudeDb">20·log10|H(f)|.</param>
/// <param name="PhaseWrappedRad">arg H(f) via atan2, in (-π, π].</param>
/// <param name="PhaseUnwrappedRad">Continuous phase after removing 2π jumps.</param>
public sealed record FrequencyResponse(
    double FrequencyHz,
    double Real,
    double Imag,
    double MagnitudeDb,
    double PhaseWrappedRad,
    double PhaseUnwrappedRad);
