namespace AudioOptimizer.Art;

using System.Numerics;
using AudioOptimizer.Core;
using AudioOptimizer.Dsp;

/// <summary>
/// Replaceable extension-track support solver. Export reads <see cref="SupportOptimizationResult"/>
/// and <see cref="SupportOptimizationResult.ExportContractVersion"/>; swapping the implementation
/// does not retarget that contract. This is not the dual-sub optimizer.
/// </summary>
public interface ISupportOptimizer
{
    string OptimizerId { get; }

    SupportOptimizationResult Optimize(SupportProblem problem);
}

/// <summary>
/// P0 support FIR: per-bin regularized least squares, hard band limit, then a uniform level scale.
/// </summary>
public sealed class RegularizedSupportFirDesigner : ISupportOptimizer
{
    public string OptimizerId => "art-p0-wls";

    public SupportOptimizationResult Optimize(SupportProblem problem)
    {
        PreparedSupport problemData = SupportProblemValidator.Prepare(problem);
        int n = problemData.FftSize;
        int supports = problemData.SupportIds.Count;
        int positions = problemData.Positions.Count;
        int bins = n / 2 + 1;
        var gains = new Complex[supports][];
        for (int s = 0; s < supports; s++) gains[s] = new Complex[n];

        double low = problem.Options.BandLowHz;
        double high = problem.Options.BandHighHz;
        double lambda = problem.Options.Regularization;
        var ata = new Complex[supports, supports];
        var atb = new Complex[supports];

        for (int k = 0; k < bins; k++)
        {
            double hz = k * (double)problemData.SampleRate / n;
            if (hz < low || hz > high) continue;

            Array.Clear(atb, 0, supports);
            for (int r = 0; r < supports; r++)
                for (int c = 0; c < supports; c++)
                    ata[r, c] = Complex.Zero;

            for (int m = 0; m < positions; m++)
            {
                Complex target = problem.Options.FlatTargetGain is { } flat
                    ? new Complex(flat, 0)
                    : problemData.PrimaryMean[k];
                Complex residual = target - problemData.Primary[m][k];
                for (int s = 0; s < supports; s++)
                {
                    Complex column = problemData.Support[s][m][k];
                    atb[s] += Complex.Conjugate(column) * residual;
                    for (int t = 0; t < supports; t++)
                        ata[s, t] += Complex.Conjugate(column) * problemData.Support[t][m][k];
                }
            }

            for (int s = 0; s < supports; s++) ata[s, s] += lambda;
            Complex[] solved = Solve(ata, atb);
            for (int s = 0; s < supports; s++) gains[s][k] = solved[s];
        }

        double cap = ComplexMath.DbToLinear(problem.Options.SupportLevelMaxDb);
        double designed = RmsRatio(problemData, gains);
        double scale = designed > cap && designed > 0 ? cap / designed : 1.0;
        if (scale != 1.0)
        {
            for (int s = 0; s < supports; s++)
                for (int k = 0; k < n; k++)
                    gains[s][k] *= scale;
        }

        for (int s = 0; s < supports; s++) Mirror(gains[s]);

        double achievedRatio = RmsRatio(problemData, gains);
        double achievedDb = achievedRatio <= 1e-12 ? -120 : ComplexMath.LinearToDb(achievedRatio);
        var firs = new Dictionary<string, double[]>(StringComparer.Ordinal);
        for (int s = 0; s < supports; s++)
            firs[problemData.SupportIds[s]] = ArtFourier.InverseReal(gains[s]);

        return new SupportOptimizationResult
        {
            OptimizerVersion = "art-p0",
            OptimizerId = OptimizerId,
            ContractVersion = SupportOptimizationResult.ExportContractVersion,
            SupportFirs = firs,
            BandLowHz = low,
            BandHighHz = high,
            SupportLevelDb = problem.Options.SupportLevelMaxDb,
            AchievedSupportLevelDb = achievedDb,
            SampleRate = problemData.SampleRate,
            FirLength = n,
            Preview = BuildPreview(problemData, gains, problem.Options.SupportLevelMaxDb, achievedDb),
        };
    }

    static SupportPreview BuildPreview(PreparedSupport data, Complex[][] gains, double capDb, double achievedDb)
    {
        var frequencies = new List<double>();
        var predicted = new List<double>();
        var contribution = data.SupportIds.ToDictionary(id => id, _ => new List<double>(), StringComparer.Ordinal);
        int bins = data.FftSize / 2 + 1;
        for (int k = 0; k < bins; k++)
        {
            double hz = k * (double)data.SampleRate / data.FftSize;
            if (hz < data.BandLowHz || hz > data.BandHighHz) continue;
            frequencies.Add(hz);

            double predictedSum = 0;
            var contribSum = new double[data.SupportIds.Count];
            for (int m = 0; m < data.Positions.Count; m++)
            {
                Complex sum = data.Primary[m][k];
                for (int s = 0; s < data.SupportIds.Count; s++)
                {
                    Complex term = data.Support[s][m][k] * gains[s][k];
                    sum += term;
                    contribSum[s] += term.Magnitude;
                }

                predictedSum += sum.Magnitude;
            }

            double denom = data.Positions.Count;
            predicted.Add(ComplexMath.LinearToDb(Math.Max(predictedSum / denom, 1e-12)));
            for (int s = 0; s < data.SupportIds.Count; s++)
                contribution[data.SupportIds[s]].Add(ComplexMath.LinearToDb(Math.Max(contribSum[s] / denom, 1e-12)));
        }

        return new SupportPreview
        {
            FrequencyHz = [.. frequencies],
            PredictedMagnitudeDb = [.. predicted],
            SupportContributionDb = contribution.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal),
            LevelHeadroomDb = capDb - achievedDb,
        };
    }

    static double RmsRatio(PreparedSupport data, Complex[][] gains)
    {
        double num = 0, den = 0;
        int count = 0;
        int bins = data.FftSize / 2 + 1;
        for (int k = 1; k < bins; k++)
        {
            double hz = k * (double)data.SampleRate / data.FftSize;
            if (hz < data.BandLowHz || hz > data.BandHighHz) continue;
            for (int m = 0; m < data.Positions.Count; m++)
            {
                Complex contrib = Complex.Zero;
                for (int s = 0; s < gains.Length; s++)
                    contrib += data.Support[s][m][k] * gains[s][k];
                num += contrib.Magnitude * contrib.Magnitude;
                den += data.Primary[m][k].Magnitude * data.Primary[m][k].Magnitude;
                count++;
            }
        }

        if (count == 0 || den <= 0) return 0;
        return Math.Sqrt(num / count) / Math.Sqrt(den / count);
    }

    static void Mirror(Complex[] spectrum)
    {
        int n = spectrum.Length;
        spectrum[0] = new Complex(spectrum[0].Real, 0);
        if (n % 2 == 0) spectrum[n / 2] = new Complex(spectrum[n / 2].Real, 0);
        for (int k = 1; k < n / 2; k++) spectrum[n - k] = Complex.Conjugate(spectrum[k]);
    }

    static Complex[] Solve(Complex[,] ata, Complex[] atb)
    {
        int n = atb.Length;
        var a = new Complex[n, n + 1];
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++) a[r, c] = ata[r, c];
            a[r, n] = atb[r];
        }

        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            double best = a[col, col].Magnitude;
            for (int r = col + 1; r < n; r++)
            {
                double magnitude = a[r, col].Magnitude;
                if (magnitude > best)
                {
                    best = magnitude;
                    pivot = r;
                }
            }

            if (best < 1e-18)
            {
                for (int c = 0; c <= n; c++) a[col, c] = Complex.Zero;
                a[col, col] = Complex.One;
                continue;
            }

            if (pivot != col)
            {
                for (int c = 0; c <= n; c++)
                    (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
            }

            Complex div = a[col, col];
            for (int c = col; c <= n; c++) a[col, c] /= div;
            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                Complex factor = a[r, col];
                if (factor == Complex.Zero) continue;
                for (int c = col; c <= n; c++) a[r, c] -= factor * a[col, c];
            }
        }

        var x = new Complex[n];
        for (int r = 0; r < n; r++) x[r] = a[r, n];
        return x;
    }
}

/// <summary>
/// Second <see cref="ISupportOptimizer"/>: same validation and export contract, zero coefficients.
/// Present so a caller can swap solvers without changing the export DTO.
/// </summary>
public sealed class StubSupportOptimizer : ISupportOptimizer
{
    public string OptimizerId => "art-stub";

    public SupportOptimizationResult Optimize(SupportProblem problem)
    {
        PreparedSupport data = SupportProblemValidator.Prepare(problem);
        var firs = data.SupportIds.ToDictionary(id => id, _ => new double[data.FftSize], StringComparer.Ordinal);
        return new SupportOptimizationResult
        {
            OptimizerVersion = "art-stub",
            OptimizerId = OptimizerId,
            ContractVersion = SupportOptimizationResult.ExportContractVersion,
            SupportFirs = firs,
            BandLowHz = problem.Options.BandLowHz,
            BandHighHz = problem.Options.BandHighHz,
            SupportLevelDb = problem.Options.SupportLevelMaxDb,
            AchievedSupportLevelDb = -120,
            SampleRate = data.SampleRate,
            FirLength = data.FftSize,
            Preview = new SupportPreview
            {
                LevelHeadroomDb = problem.Options.SupportLevelMaxDb - (-120),
            },
        };
    }
}

static class SupportProblemValidator
{
    public static PreparedSupport Prepare(SupportProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var errors = new List<string>();
        SupportOptimizationOptions options = problem.Options ?? new SupportOptimizationOptions();
        if (string.IsNullOrWhiteSpace(problem.PrimaryChannelId)) errors.Add("Primary channel id is required.");
        if (problem.SupportChannelIds is null || problem.SupportChannelIds.Count < 1)
            errors.Add("At least one support channel is required.");
        if (problem.Positions is null || problem.Positions.Count < 2)
            errors.Add("Support design needs at least two positions.");
        if (options.FirLength < 32) errors.Add("Support FIR length must be at least 32.");
        if (!(options.BandLowHz >= 0) || !(options.BandHighHz > options.BandLowHz))
            errors.Add("Support band limits are not a non-empty range.");
        if (!double.IsFinite(options.SupportLevelMaxDb)) errors.Add("Support level cap must be finite.");
        if (!double.IsFinite(options.Regularization) || options.Regularization < 0)
            errors.Add("Regularization must be finite and non-negative.");
        if (options.FlatTargetGain is { } target && (!double.IsFinite(target) || target < 0))
            errors.Add("Flat target gain must be finite and non-negative.");

        var supportIds = new List<string>();
        if (problem.SupportChannelIds is not null)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in problem.SupportChannelIds)
            {
                if (string.IsNullOrWhiteSpace(id)) errors.Add("A support channel id is blank.");
                else if (!seen.Add(id)) errors.Add($"Duplicate support channel '{id}'.");
                else if (id == problem.PrimaryChannelId) errors.Add($"Primary '{id}' cannot also be a support.");
                else supportIds.Add(id);
            }
        }

        if (errors.Count > 0) throw new ArtValidationException(errors);

        int sampleRate = problem.Positions![0].Primary.SampleRate;
        int longest = problem.Positions[0].Primary.Samples.Length;
        foreach (SupportPosition position in problem.Positions)
        {
            Consider(position.Primary, sampleRate, ref longest, errors, position.PositionId, problem.PrimaryChannelId);
            foreach (string id in supportIds)
            {
                if (!position.Supports.TryGetValue(id, out ImpulseResponse? response))
                    errors.Add($"Position '{position.PositionId}' is missing support '{id}'.");
                else
                    Consider(response, sampleRate, ref longest, errors, position.PositionId, id);
            }
        }

        if (errors.Count > 0) throw new ArtValidationException(errors);

        int n = ArtFourier.PowerOfTwoAtLeast(Math.Max(options.FirLength, longest));
        double nyquist = sampleRate / 2.0;
        if (options.BandHighHz >= nyquist)
            throw new ArtValidationException($"Support band high {options.BandHighHz} Hz is not below Nyquist {nyquist} Hz.");

        int inBand = 0;
        for (int k = 0; k <= n / 2; k++)
        {
            double hz = k * (double)sampleRate / n;
            if (hz >= options.BandLowHz && hz <= options.BandHighHz) inBand++;
        }

        if (inBand < 1) throw new ArtValidationException("Support band contains no DFT bins.");

        var primary = new Complex[problem.Positions.Count][];
        var support = new Complex[supportIds.Count][][];
        for (int s = 0; s < supportIds.Count; s++) support[s] = new Complex[problem.Positions.Count][];
        var mean = new Complex[n];
        for (int m = 0; m < problem.Positions.Count; m++)
        {
            SupportPosition position = problem.Positions[m];
            primary[m] = ArtFourier.ForwardReal(position.Primary.Samples, n);
            for (int k = 0; k < n; k++) mean[k] += primary[m][k];
            for (int s = 0; s < supportIds.Count; s++)
                support[s][m] = ArtFourier.ForwardReal(position.Supports[supportIds[s]].Samples, n);
        }

        double inv = 1.0 / problem.Positions.Count;
        for (int k = 0; k < n; k++) mean[k] *= inv;

        return new PreparedSupport
        {
            SupportIds = supportIds,
            Positions = problem.Positions,
            SampleRate = sampleRate,
            FftSize = n,
            BandLowHz = options.BandLowHz,
            BandHighHz = options.BandHighHz,
            Primary = primary,
            PrimaryMean = mean,
            Support = support,
        };
    }

    static void Consider(ImpulseResponse response, int sampleRate, ref int longest, List<string> errors, string positionId, string channelId)
    {
        if (response is null || response.Samples is null || response.Samples.Length == 0)
        {
            errors.Add($"Position '{positionId}' channel '{channelId}' has no impulse response.");
            return;
        }

        if (response.SampleRate != sampleRate)
            errors.Add($"Position '{positionId}' channel '{channelId}' sample rate does not match.");
        longest = Math.Max(longest, response.Samples.Length);
    }
}

sealed class PreparedSupport
{
    public required List<string> SupportIds { get; init; }

    public required IReadOnlyList<SupportPosition> Positions { get; init; }

    public int SampleRate { get; init; }

    public int FftSize { get; init; }

    public double BandLowHz { get; init; }

    public double BandHighHz { get; init; }

    public required Complex[][] Primary { get; init; }

    public required Complex[] PrimaryMean { get; init; }

    /// <summary>Indexed [support, position].</summary>
    public required Complex[][][] Support { get; init; }
}
