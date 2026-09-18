namespace AudioOptimizer.Simulation;

/// <summary>
/// The Virtual Acoustic Lab's command line: it runs a scenario and prints the report, and it is the same code path the
/// UI calls — no logic here beyond argument handling and console output.
/// <code>
/// dotnet run --project src/AudioOptimizer.Simulation -- --list
/// dotnet run --project src/AudioOptimizer.Simulation -- --scenario dual-sub
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

        No audio device is opened and no external tool is invoked.
        """;
}
