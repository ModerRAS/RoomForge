namespace AudioOptimizer.SmokeTest;

using AudioOptimizer.Audio;

/// <summary>
/// Command line for the hardware smoke test. No arguments means list devices and exit — the harness must never
/// open a device unless it was explicitly told to measure. Parsing never throws: every bad argument comes back
/// as an error string so the tool can exit 2 with a message instead of a stack trace.
/// </summary>
internal sealed record SmokeTestOptions
{
    public bool ListOnly { get; private init; }
    public bool Help { get; private init; }
    public string? InputName { get; private init; }
    public string? OutputName { get; private init; }
    public int SampleRate { get; private init; } = 48000;
    public bool Exclusive { get; private init; }
    public bool Loopback { get; private init; }
    public double SweepStartHz { get; private init; } = 20;
    public double SweepEndHz { get; private init; } = 150;
    public double SweepSeconds { get; private init; } = 1.0;
    public double PlaybackGain { get; private init; } = 1.0;
    public double PreRollSeconds { get; private init; } = 0.5;
    public double PostRollSeconds { get; private init; } = 0.5;
    public string? WavPath { get; private init; }

    public static string Usage =>
        """
        RoomForge AudioOptimizer — hardware smoke test (WASAPI)

        Usage:
          AudioOptimizer.SmokeTest                          List input/output devices and exit 0 (opens nothing)
          AudioOptimizer.SmokeTest --measure [options]      Play a sweep, record it, deconvolve, report

        Options:
          --measure                Actually open devices and measure (required to touch hardware)
          --input=<name|id>        Capture device (default: first input device)
          --output=<name|id>       Render device  (default: first output device)
          --rate=48000             Sample rate: 44100 | 48000 | 96000 (default 48000)
          --exclusive              Exclusive mode (default: shared). Exclusive fails on a busy endpoint.
          --loopback               Record the output device's own stream instead of a microphone
                                   (validates playback/recording timing without a mic; shared mode only)
          --sweep=20-150           Sweep band in Hz (default 20-150)
          --sweep-seconds=1.0      Sweep duration (default 1.0 s — the Phase 1-6 convention)
          --gain=1.0               Playback level of the sweep, 0 < g <= 1 (default 1.0 = full scale;
                                   use a small value for a quiet loopback self-test)
          --pre=0.5 --post=0.5     Seconds recorded before/after the sweep (default 0.5 each)
          --wav=<path>             WAV output path (default: smoketest-<timestamp>.wav in the system temp
                                   directory, never the working tree — a capture can be tens of MB)
          --help                   This text

        Exit codes: 0 success, 2 bad arguments, 1 runtime failure (closed device, busy exclusive endpoint,
        rejected sample rate, no sweep detected). Failures print one clear message, never a stack trace.

        Note: near the sweep's own start frequency the ABSOLUTE level is unreliable (ESS edge artifact, ~12 dB
        at the very edge); use A/B or A/B/AB ratios for decisions — those are unaffected.
        """;

    public static bool TryParse(string[] args, out SmokeTestOptions options, out string? error)
    {
        var parsed = new SmokeTestOptions { ListOnly = args.Length == 0 };
        error = null;

        foreach (string arg in args)
        {
            string name = arg;
            string? value = null;
            int equals = arg.IndexOf('=');
            if (equals >= 0)
            {
                name = arg[..equals];
                value = arg[(equals + 1)..];
            }

            switch (name.ToLowerInvariant())
            {
                case "--measure":
                    parsed = parsed with { ListOnly = false };
                    if (value is not null) { error = "--measure takes no value."; break; }
                    continue;
                case "--help" or "-h":
                    parsed = parsed with { Help = true };
                    continue;
                case "--exclusive":
                    parsed = parsed with { Exclusive = true };
                    continue;
                case "--loopback":
                    parsed = parsed with { Loopback = true };
                    continue;
                case "--input":
                    parsed = parsed with { InputName = value };
                    break;
                case "--output":
                    parsed = parsed with { OutputName = value };
                    break;
                case "--wav":
                    parsed = parsed with { WavPath = value };
                    break;
                case "--rate":
                    if (!int.TryParse(value, out int rate)) { error = $"Invalid --rate '{value}': expected 44100, 48000 or 96000."; break; }
                    parsed = parsed with { SampleRate = rate };
                    continue;
                case "--sweep":
                    string[] parts = (value ?? string.Empty).Split('-');
                    if (parts.Length != 2 || !double.TryParse(parts[0], out double start) || !double.TryParse(parts[1], out double end))
                    { error = $"Invalid --sweep '{value}': expected e.g. 20-150."; break; }
                    parsed = parsed with { SweepStartHz = start, SweepEndHz = end };
                    continue;
                case "--sweep-seconds":
                    if (!double.TryParse(value, out double seconds)) { error = $"Invalid --sweep-seconds '{value}'."; break; }
                    parsed = parsed with { SweepSeconds = seconds };
                    continue;
                case "--gain":
                    if (!double.TryParse(value, out double gain)) { error = $"Invalid --gain '{value}'."; break; }
                    parsed = parsed with { PlaybackGain = gain };
                    continue;
                case "--pre":
                    if (!double.TryParse(value, out double pre)) { error = $"Invalid --pre '{value}'."; break; }
                    parsed = parsed with { PreRollSeconds = pre };
                    continue;
                case "--post":
                    if (!double.TryParse(value, out double post)) { error = $"Invalid --post '{value}'."; break; }
                    parsed = parsed with { PostRollSeconds = post };
                    continue;
                default:
                    error = $"Unknown argument '{arg}'.";
                    break;
            }

            if (error is not null) { options = parsed; return false; }
        }

        if (parsed.Help) { options = parsed; return true; }

        // Range checks last, so a bad combination reports once with a useful message.
        if (!parsed.ListOnly)
        {
            if (!AudioBackendSettings.SupportedSampleRates.Contains(parsed.SampleRate))
                error = $"--rate {parsed.SampleRate} is not supported; use {string.Join(", ", AudioBackendSettings.SupportedSampleRates)}.";
            else if (parsed.SweepStartHz <= 0) error = "--sweep start must be > 0 Hz.";
            else if (parsed.SweepEndHz <= parsed.SweepStartHz) error = "--sweep end must be greater than the start.";
            else if (parsed.SweepSeconds <= 0) error = "--sweep-seconds must be > 0.";
            else if (parsed.PlaybackGain <= 0 || parsed.PlaybackGain > 1) error = "--gain must be in (0, 1].";
            else if (parsed.PreRollSeconds < 0 || parsed.PostRollSeconds < 0) error = "--pre/--post must be >= 0.";
            else if (parsed.Loopback && parsed.Exclusive) error = "--loopback cannot be combined with --exclusive (WASAPI loopback is shared-mode only).";
        }

        options = parsed;
        return error is null;
    }
}
