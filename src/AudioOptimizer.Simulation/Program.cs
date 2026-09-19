namespace AudioOptimizer.Simulation;

using System.Diagnostics;
using System.Globalization;

/// <summary>
/// The Virtual Acoustic Lab's command line: it runs a scenario and prints the report, and it is the same code path the
/// UI calls — no logic here beyond argument handling and console output.
/// <code>
/// dotnet run --project src/AudioOptimizer.Simulation -- --list
/// dotnet run --project src/AudioOptimizer.Simulation -- --scenario dual-sub
/// dotnet run --project src/AudioOptimizer.Simulation -- --regression quick
/// dotnet run --project src/AudioOptimizer.Simulation -- --replay 20260928
/// </code>
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        if (args.Contains("--list"))
        {
            foreach (SimulationScenario listed in SimulationScenarios.All)
                Console.WriteLine($"{listed.Id,-12} {listed.Title}");
            return 0;
        }

        if (args.Contains("--regression"))
            return RunRegression(args);

        if (args.Contains("--replay"))
            return RunReplay(args);

        string? id = Value(args, "--scenario") ?? args.FirstOrDefault(argument => !argument.StartsWith('-'));
        if (id is null)
        {
            Console.Error.WriteLine("No scenario named. Use --list to see them.");
            return 1;
        }

        if (SimulationScenarios.Find(id) is not { } scenario)
        {
            Console.Error.WriteLine($"Unknown scenario '{id}'. Use --list to see them.");
            return 1;
        }

        SimulationOutcome outcome = SimulationRunner.Run(scenario);
        Console.Write(outcome.Format());
        return 0;
    }

    /// <summary>
    /// The randomized plan: 20 / 100 / 500 generated scenarios (plus any checked-in fixture), judged in parallel and
    /// aggregated in plan order. Exit 0 only when every scenario passed.
    /// </summary>
    private static int RunRegression(string[] args)
    {
        string? value = Value(args, "--regression");
        if (value is null || !Enum.TryParse(value, ignoreCase: true, out RegressionMode mode) || !Enum.IsDefined(mode))
        {
            Console.Error.WriteLine($"Unknown regression plan '{value ?? "<missing>"}'. Use --regression quick|standard|stress.");
            return 1;
        }

        RegressionReport report = RegressionRunner.Run(mode);
        Console.Write(report.Format());
        Console.WriteLine($"Run wall clock: {report.RuntimeMilliseconds / 1000.0:F1} s, {Environment.ProcessorCount} logical cores");
        return report.AllPassed ? 0 : 1;
    }

    /// <summary>One generated scenario, identified only by its seed — the reproduction path a failure line prints.</summary>
    private static int RunReplay(string[] args)
    {
        string? value = Value(args, "--replay");
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed))
        {
            Console.Error.WriteLine($"--replay needs a scenario seed (an integer); got '{value ?? "<missing>"}'.");
            return 1;
        }

        RegressionScenario scenario;
        try
        {
            scenario = RandomScenarioGenerator.Replay(seed);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        ScenarioJudgement judgement = RegressionRunner.RunScenario(scenario);
        stopwatch.Stop();

        Console.WriteLine(scenario.Describe());
        Console.WriteLine(judgement.Metrics.SummaryLine());
        var report = new RegressionReport([judgement]) { RuntimeMilliseconds = stopwatch.Elapsed.TotalMilliseconds };
        Console.Write(report.Format());
        return judgement.Passed ? 0 : 1;
    }

    private static string? Value(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private const string Usage =
        """
        AudioOptimizer.Simulation — the offline Virtual Acoustic Lab.

        Runs a shoebox image-source room through RoomForge's OWN measurement chain (sweep, deconvolution,
        impulse response, FFT, complex frequency response, quality checks) and, where a scenario asks for it,
        hands the resulting measurement set to the shipped dual-sub optimizer.

          --list                 print the scenarios
          --scenario <id>        run one of them, e.g. --scenario dual-sub
          --regression <plan>    run the randomized regression lab: quick (20), standard (100), stress (500)
          --replay <seed>        run one generated scenario by its seed, e.g. --replay 20260928

        No audio device is opened and no external tool is invoked.
        """;
}
