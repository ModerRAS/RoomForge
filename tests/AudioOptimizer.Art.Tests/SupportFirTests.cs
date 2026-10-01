namespace AudioOptimizer.Art.Tests;

using System.Numerics;
using AudioOptimizer.Dsp;

public class SupportFirTests
{
    [Fact]
    public void Flat_target_designs_the_expected_in_band_gain()
    {
        SupportOptimizationResult result = Design(flatTarget: 1.5, levelDb: 12, regularization: 1e-8, firLength: 1024);
        Complex[] spectrum = Spectrum(result.SupportFirs["S"]);
        int bin = Bin(result, 500);
        Assert.Equal(1024, result.FirLength);
        Assert.InRange(spectrum[bin].Real, 0.5 - 1e-6, 0.5 + 1e-6);
        Assert.InRange(spectrum[bin].Imaginary, -1e-6, 1e-6);
        Assert.True(result.AchievedSupportLevelDb < result.SupportLevelDb);
        Assert.Equal(result.Preview.FrequencyHz.Length, result.Preview.PredictedMagnitudeDb.Length);
        Assert.Equal(result.Preview.FrequencyHz.Length, result.Preview.SupportContributionDb["S"].Length);
    }

    [Fact]
    public void Level_cap_and_band_limit_and_regularization_hold()
    {
        SupportOptimizationResult capped = Design(flatTarget: 3, levelDb: -6, regularization: 1e-10, firLength: 1024);
        Complex[] cappedSpectrum = Spectrum(capped.SupportFirs["S"]);
        int inside = Bin(capped, 500);
        int outside = Bin(capped, 4000);
        double cap = Math.Pow(10.0, -6.0 / 20.0);
        Assert.InRange(cappedSpectrum[inside].Magnitude, cap - 1e-5, cap + 1e-5);
        Assert.True(cappedSpectrum[outside].Magnitude < 1e-8);
        Assert.InRange(capped.AchievedSupportLevelDb, -6.05, -5.95);
        Assert.InRange(capped.Preview.LevelHeadroomDb, -0.05, 0.05);

        SupportOptimizationResult loaded = Design(flatTarget: 1.5, levelDb: 12, regularization: 1e6, firLength: 1024);
        Assert.True(Spectrum(loaded.SupportFirs["S"])[Bin(loaded, 500)].Magnitude < 1e-3);
    }

    [Fact]
    public void Two_supports_split_the_same_correction()
    {
        var problem = Problem(["S", "T"], flatTarget: 1.5, levelDb: 12, regularization: 1e-8, firLength: 1024);
        SupportOptimizationResult result = new RegularizedSupportFirDesigner().Optimize(problem);
        double left = Spectrum(result.SupportFirs["S"])[Bin(result, 500)].Real;
        double right = Spectrum(result.SupportFirs["T"])[Bin(result, 500)].Real;
        Assert.InRange(left, 0.25 - 1e-5, 0.25 + 1e-5);
        Assert.InRange(right, 0.25 - 1e-5, 0.25 + 1e-5);
    }

    [Fact]
    public void Illegal_problems_are_rejected()
    {
        Assert.Throws<ArtValidationException>(() => new RegularizedSupportFirDesigner().Optimize(
            Problem(["L"], flatTarget: 1, levelDb: 0, regularization: 0, firLength: 256)));

        SupportProblem onePosition = Problem(["S"], 1, 0, 0, 256);
        onePosition.Positions = [onePosition.Positions[0]];
        Assert.Throws<ArtValidationException>(() => new RegularizedSupportFirDesigner().Optimize(onePosition));

        SupportProblem inverted = Problem(["S"], 1, 0, 0, 256);
        inverted.Options = new SupportOptimizationOptions
        {
            FirLength = 256,
            BandLowHz = 1000,
            BandHighHz = 100,
            SupportLevelMaxDb = 0,
            Regularization = 0,
            FlatTargetGain = 1,
        };
        Assert.Throws<ArtValidationException>(() => new RegularizedSupportFirDesigner().Optimize(inverted));
    }

    [Fact]
    public void Stub_solver_keeps_the_export_contract_version()
    {
        SupportProblem problem = Problem(["S"], flatTarget: 1.5, levelDb: -6, regularization: 1e-4, firLength: 256);
        ISupportOptimizer primary = new RegularizedSupportFirDesigner();
        ISupportOptimizer stub = new StubSupportOptimizer();
        SupportOptimizationResult designed = primary.Optimize(problem);
        SupportOptimizationResult skipped = stub.Optimize(problem);

        Assert.NotEqual(primary.OptimizerId, stub.OptimizerId);
        Assert.Equal(SupportOptimizationResult.ExportContractVersion, designed.ContractVersion);
        Assert.Equal(designed.ContractVersion, skipped.ContractVersion);
        Assert.All(skipped.SupportFirs["S"], sample => Assert.Equal(0, sample));
        Assert.NotEmpty(designed.SupportFirs["S"]);
    }

    static SupportOptimizationResult Design(double flatTarget, double levelDb, double regularization, int firLength)
        => new RegularizedSupportFirDesigner().Optimize(Problem(["S"], flatTarget, levelDb, regularization, firLength));

    static SupportProblem Problem(string[] supports, double flatTarget, double levelDb, double regularization, int firLength)
    {
        var supportIrs = supports.ToDictionary(id => id, _ => SyntheticSignals.Ir([1]));
        return new SupportProblem
        {
            PrimaryChannelId = "L",
            SupportChannelIds = supports,
            Options = new SupportOptimizationOptions
            {
                FirLength = firLength,
                BandLowHz = 200,
                BandHighHz = 1000,
                SupportLevelMaxDb = levelDb,
                Regularization = regularization,
                FlatTargetGain = flatTarget,
            },
            Positions =
            [
                new SupportPosition { PositionId = "p0", Primary = SyntheticSignals.Ir([1]), Supports = supportIrs },
                new SupportPosition { PositionId = "p1", Primary = SyntheticSignals.Ir([1]), Supports = supportIrs },
            ],
        };
    }

    static Complex[] Spectrum(double[] fir)
    {
        var spectrum = new Complex[fir.Length];
        for (int i = 0; i < fir.Length; i++) spectrum[i] = fir[i];
        return Fft.Forward(spectrum);
    }

    static int Bin(SupportOptimizationResult result, double hz)
        => (int)Math.Round(hz * result.FirLength / result.SampleRate);
}
