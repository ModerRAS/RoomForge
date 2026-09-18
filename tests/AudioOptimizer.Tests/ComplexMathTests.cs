namespace AudioOptimizer.Tests;

using System.Numerics;
using AudioOptimizer.Dsp;

/// <summary>
/// T2–T6: complex sum gain/polarity, decibel conversion and the delay/rotation identity
/// φ = -2π·f·Δt.
/// </summary>
public class ComplexMathTests
{
    private static readonly Complex Unit = new(1.0, 0.0);

    /// <summary>T2: two identical signals 0° apart add coherently → 20·log10(2).</summary>
    [Fact]
    public void Identical_signals_0_degrees_apart_add_to_6_0206_db()
    {
        var sum = ComplexMath.Sum(Unit, ComplexMath.Rotate(Unit, 0.0));
        Assert.Equal(2.0, ComplexMath.Magnitude(sum), 1e-15);
        // 20·log10(2) = 6.020599913279624 dB (tol 1e-9)
        Assert.Equal(6.020599913279624, ComplexMath.LinearToDb(ComplexMath.Magnitude(sum)), 1e-9);
    }

    /// <summary>T3: 180° apart cancels exactly.</summary>
    [Fact]
    public void Identical_signals_180_degrees_apart_cancel()
    {
        var sum = ComplexMath.Sum(Unit, ComplexMath.Rotate(Unit, Math.PI));
        // 1 + 1·e^(jπ) = 1 - 1 = 0, residual is the sin(π) = 1.22e-16 round-off
        Assert.True(ComplexMath.Magnitude(sum) < 1e-12, $"residual was {ComplexMath.Magnitude(sum):E3}");
    }

    /// <summary>T4: 90° apart → |1 + j| = √2 → 20·log10(√2).</summary>
    [Fact]
    public void Identical_signals_90_degrees_apart_add_to_3_0103_db()
    {
        var sum = ComplexMath.Sum(Unit, ComplexMath.Rotate(Unit, Math.PI / 2.0));
        Assert.Equal(Math.Sqrt(2.0), ComplexMath.Magnitude(sum), 1e-15);
        // 20·log10(√2) = 3.010299956639812 dB (tol 1e-9)
        Assert.Equal(3.010299956639812, ComplexMath.LinearToDb(ComplexMath.Magnitude(sum)), 1e-9);
    }

    /// <summary>T5: a delay Δt is a rotation by φ = -2π·f·Δt.</summary>
    [Fact]
    public void Delay_rotates_by_minus_two_pi_f_dt()
    {
        // Δt = 5 ms at 50 Hz → φ = -2π·50·0.005 = -π/2 = -90° → (1+0j)·e^(-jπ/2) = -j
        var at5ms = ComplexMath.ApplyDelay(Unit, frequencyHz: 50.0, delaySeconds: 0.005);
        Assert.Equal(0.0, at5ms.Real, 1e-12);
        Assert.Equal(-1.0, at5ms.Imaginary, 1e-12);
        Assert.Equal(-Math.PI / 2.0, ComplexMath.Phase(at5ms), 1e-12);

        // Δt = 10 ms at 50 Hz → φ = -2π·50·0.010 = -π = -180° → -1 + 0j
        var at10ms = ComplexMath.ApplyDelay(Unit, frequencyHz: 50.0, delaySeconds: 0.010);
        Assert.Equal(-1.0, at10ms.Real, 1e-12);
        Assert.Equal(0.0, at10ms.Imaginary, 1e-12);

        // Δt = 15 ms at 50 Hz → φ = -3π/2 (-270°), which atan2 reports as +π/2 (+90°):
        // the true value is still -j·(1+0j) = +j, and only unwrapping recovers -3π/2.
        var at15ms = ComplexMath.ApplyDelay(Unit, frequencyHz: 50.0, delaySeconds: 0.015);
        Assert.Equal(0.0, at15ms.Real, 1e-12);
        Assert.Equal(1.0, at15ms.Imaginary, 1e-12);
        Assert.Equal(Math.PI / 2.0, ComplexMath.Phase(at15ms), 1e-12);
    }

    /// <summary>Unwrap turns the wrapped atan2 sequence back into a monotone -2π ramp.</summary>
    [Fact]
    public void Unwrap_removes_two_pi_jumps()
    {
        // wrapped atan2 samples for φ = 0, -π/2, -π, -3π/2, -2π are 0, -π/2, -π, +π/2, 0
        double[] wrapped = [0.0, -Math.PI / 2.0, -Math.PI, Math.PI / 2.0, 0.0];
        double[] expected = [0.0, -Math.PI / 2.0, -Math.PI, -3.0 * Math.PI / 2.0, -2.0 * Math.PI];

        double[] unwrapped = ComplexMath.Unwrap(wrapped);
        Assert.Equal(expected.Length, unwrapped.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], unwrapped[i], 1e-12);
    }

    /// <summary>T6: gain, polarity and the dB↔linear round trip.</summary>
    [Fact]
    public void Sum_applies_gain_and_polarity()
    {
        // B = A·0.5 at 0° → |1 + 0.5| = 1.5
        Assert.Equal(1.5, ComplexMath.Magnitude(ComplexMath.Sum(Unit, 0.5 * ComplexMath.Rotate(Unit, 0.0))), 1e-15);
        // polarity flip: B = -A·0.5 → |1 - 0.5| = 0.5
        Assert.Equal(0.5, ComplexMath.Magnitude(ComplexMath.Sum(Unit, -0.5 * ComplexMath.Rotate(Unit, 0.0))), 1e-15);

        // 20·log10(0.5) = -6.020599913279624 dB, 20·log10(1.5) = 3.5218251811136248 dB, 20·log10(1) = 0
        Assert.Equal(-6.020599913279624, ComplexMath.LinearToDb(0.5), 1e-9);
        Assert.Equal(3.5218251811136248, ComplexMath.LinearToDb(1.5), 1e-9);
        Assert.Equal(0.0, ComplexMath.LinearToDb(1.0), 1e-15);

        // dB → linear → dB round trip is lossless to 1e-12
        Assert.Equal(0.5, ComplexMath.DbToLinear(ComplexMath.LinearToDb(0.5)), 1e-12);
        Assert.Equal(1.5, ComplexMath.DbToLinear(ComplexMath.LinearToDb(1.5)), 1e-12);
        Assert.Equal(1.0, ComplexMath.DbToLinear(0.0), 1e-15);
    }
}
