using System.Numerics;
using AudioOptimizer.Core;
using AudioOptimizer.Optimization;

namespace AudioOptimizer.Tests;

/// <summary>
/// Shared synthetic data for the optimizer tests. Deterministic, small, and synthesised from physical
/// quantities (arrival times in seconds) so every number in the tests can be recomputed by hand.
/// </summary>
internal static class OptimizationTestData
{
    /// <summary>The band every fixture below was "measured" in: 5–200 Hz, which contains all of its bins.</summary>
    public static readonly FrequencyBand Band = new(5.0, 200.0);

    /// <summary>One complex bin, with the derived fields filled in the way the calculator would.</summary>
    public static FrequencyResponse Bin(double frequencyHz, double re, double im)
    {
        double magnitude = Math.Sqrt(re * re + im * im);
        double phase = Math.Atan2(im, re);
        return new FrequencyResponse(frequencyHz, re, im,
            magnitude > 0 ? 20.0 * Math.Log10(magnitude) : double.NegativeInfinity, phase, phase);
    }

    /// <summary>magnitude·exp(−j2πf·delay): a reflection arriving delaySeconds late.</summary>
    public static (double Re, double Im) Phasor(double magnitude, double delaySeconds, double frequencyHz)
    {
        double phase = -2.0 * Math.PI * frequencyHz * delaySeconds;
        return (magnitude * Math.Cos(phase), magnitude * Math.Sin(phase));
    }

    /// <summary>A one-bin position: the simplest possible measurement for identity tests.</summary>
    /// <summary>
    /// A one-bin position inside the fixture band. Its band is the shared fixture band, not a degenerate (f,f): several
    /// fixtures mix positions at different single frequencies (50 and 100 Hz), and those are one analysis, not two.
    /// </summary>
    public static PositionResponse OneBin(double frequencyHz, double re, double im)
        => new("p0", Band, [Bin(frequencyHz, re, im)]);

    public static SubwooferSetting Setting(double gainDb, double phaseDegrees, int polarity = 1, double delayMilliseconds = 0.0)
        => new(gainDb, phaseDegrees * Math.PI / 180.0, polarity, delayMilliseconds / 1000.0);

    /// <summary>Magnitude and dB of the single position/bin of a one-bin measurement after a setting.</summary>
    public static double TotalMagnitude(DualSubMeasurement measurement, SubwooferSetting setting)
    {
        FrequencyResponse bin = SubwooferModel.Combine(measurement, setting)[0].Bins[0];
        return Math.Sqrt(bin.Real * bin.Real + bin.Imag * bin.Imag);
    }

    /// <summary>
    /// A synthetic room: two-path (direct + reflection) responses whose arrival times vary with position, so
    /// the response is comb-filtered differently at every point and no setting can flatten it exactly.
    /// 6 positions × 40 bins from 5 Hz to 200 Hz.
    /// </summary>
    public static DualSubMeasurement Room(int positions = 6, int bins = 40)
    {
        var a = new List<PositionResponse>();
        var b = new List<PositionResponse>();
        for (int i = 0; i < positions; i++)
        {
            var aBins = new List<FrequencyResponse>();
            var bBins = new List<FrequencyResponse>();
            for (int k = 0; k < bins; k++)
            {
                double f = 5.0 + 5.0 * k;
                var (a1r, a1i) = Phasor(1.00, 0.0025 + 0.0003 * i, f);
                var (a2r, a2i) = Phasor(0.60, 0.0041 + 0.0005 * i, f);
                var (b1r, b1i) = Phasor(0.90, 0.0031 + 0.0004 * i, f);
                var (b2r, b2i) = Phasor(0.45, 0.0052 + 0.0006 * i, f);
                aBins.Add(Bin(f, a1r + a2r, a1i + a2i));
                bBins.Add(Bin(f, b1r + b2r, b1i + b2i));
            }
            a.Add(new PositionResponse($"p{i}", Band, aBins));
            b.Add(new PositionResponse($"p{i}", Band, bBins));
        }
        return new DualSubMeasurement(a, b);
    }

    /// <summary>
    /// A room built around a known optimum: A = T − c*·B, so the predicted total is exactly T at
    /// (gain*, phase*). With T = 1∠θ the total is a unit phasor at every position — the most uniform
    /// response possible, σ = 0 — so the optimum is the global minimum of the score and is unique, because
    /// B wanders per position and frequency.
    /// </summary>
    public static DualSubMeasurement KnownOptimum(double gainDb, double phaseDegrees, int positions = 5, int bins = 40)
    {
        Complex c = Complex.FromPolarCoordinates(Math.Pow(10.0, gainDb / 20.0), phaseDegrees * Math.PI / 180.0);
        var a = new List<PositionResponse>();
        var b = new List<PositionResponse>();
        for (int i = 0; i < positions; i++)
        {
            var aBins = new List<FrequencyResponse>();
            var bBins = new List<FrequencyResponse>();
            for (int k = 0; k < bins; k++)
            {
                double f = 5.0 + 5.0 * k;
                Complex bz = Complex.FromPolarCoordinates(0.5, 2.0 * Math.PI * (0.03 * i + 0.0004 * k * (i + 1)));
                Complex tz = Complex.FromPolarCoordinates(1.0, 2.0 * Math.PI * (0.017 * i + 0.0007 * k));
                Complex az = tz - c * bz;
                aBins.Add(Bin(f, az.Real, az.Imaginary));
                bBins.Add(Bin(f, bz.Real, bz.Imaginary));
            }
            a.Add(new PositionResponse($"p{i}", Band, aBins));
            b.Add(new PositionResponse($"p{i}", Band, bBins));
        }
        return new DualSubMeasurement(a, b);
    }

    /// <summary>
    /// A room whose uniformity wants one setting and whose boost limit wants another. Nine frequencies have
    /// their exact flat point at (gain*, phase*); the tenth (50 Hz) has A = [0,1,1,1], B = [1,0,0,0], so it is
    /// exactly flat at the measured setting and boosts a single position as B is raised — at B's +6 dB it is
    /// [2,1,1,1], i.e. +6.02 dB on that position. The compromise is the point of the constraint tests.
    /// </summary>
    public static DualSubMeasurement ConflictedRoom(double gainDb, double phaseDegrees)
    {
        Complex c = Complex.FromPolarCoordinates(Math.Pow(10.0, gainDb / 20.0), phaseDegrees * Math.PI / 180.0);
        const int positions = 4;
        var aBins = new List<FrequencyResponse>[positions];
        var bBins = new List<FrequencyResponse>[positions];
        for (int i = 0; i < positions; i++) { aBins[i] = []; bBins[i] = []; }

        for (int k = 0; k < 10; k++)
        {
            double f = 5.0 + 5.0 * k;
            for (int i = 0; i < positions; i++)
            {
                if (k < 9)
                {
                    Complex bz = Complex.FromPolarCoordinates(0.8, 2.0 * Math.PI * (0.031 * i + 0.0005 * k * (i + 1)));
                    Complex tz = Complex.FromPolarCoordinates(1.0, 2.0 * Math.PI * (0.019 * i + 0.0009 * k));
                    Complex az = tz - c * bz;
                    aBins[i].Add(Bin(f, az.Real, az.Imaginary));
                    bBins[i].Add(Bin(f, bz.Real, bz.Imaginary));
                }
                else
                {
                    double[] aBad = [0.0, 1.0, 1.0, 1.0];
                    double[] bBad = [1.0, 0.0, 0.0, 0.0];
                    aBins[i].Add(Bin(f, aBad[i], 0.0));
                    bBins[i].Add(Bin(f, bBad[i], 0.0));
                }
            }
        }

        var a = new List<PositionResponse>();
        var b = new List<PositionResponse>();
        for (int i = 0; i < positions; i++)
        {
            a.Add(new PositionResponse($"p{i}", Band, aBins[i]));
            b.Add(new PositionResponse($"p{i}", Band, bBins[i]));
        }
        return new DualSubMeasurement(a, b);
    }
}
