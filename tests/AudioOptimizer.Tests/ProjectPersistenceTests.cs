namespace AudioOptimizer.Tests;

using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using AudioOptimizer.Core;
using AudioOptimizer.Dsp;
using AudioOptimizer.IO;
using AudioOptimizer.Measurement;
using Xunit.Abstractions;

/// <summary>
/// §10: a project directory must reopen without re-measuring. Everything here runs in the system temp
/// directory — a project that defaulted into the working tree is the bug this class guards against.
/// </summary>
public sealed class ProjectPersistenceTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly List<string> _directories = [];

    public ProjectPersistenceTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        foreach (string directory in _directories)
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private string NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "roomforge-project-" + Guid.NewGuid().ToString("N"));
        _directories.Add(directory);
        return directory;
    }

    private static MeasurementGrid Grid => MeasurementGrid.Create(1.8, 1.0, 0.6);   // 3 × 3 × 3 = 27 points

    private static SweepSettings Sweep => new();

    /// <summary>
    /// Deliberately NOT a sweep: if a reload re-measured, the recording it hands back would be the sweep and
    /// this sine would be gone. Values are float32-representable ((float) round-trip), which is the case the
    /// file layer must preserve exactly.
    /// </summary>
    private static double[] Recording(int length = 512, double frequencyHz = 20.0)
        => [.. Enumerable.Range(0, length).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * frequencyHz * i / 48000.0)))];

    private static double[] ImpulseResponse(int length = 128)
    {
        var samples = new double[length];
        // An exponential decay, again float32-representable: 0.25 · 0.9^i.
        for (int i = 0; i < length; i++) samples[i] = (float)(0.25 * Math.Pow(0.9, i));
        return samples;
    }

    private static MeasurementCapture Capture(string seed, double gain = 0.25, int polarity = 1, double phaseDegrees = 0.0)
        => new($"UMIK-1 #{seed}", $"Speakers (DAC) #{seed}", gain, polarity, phaseDegrees, 0.0);

    [Fact]
    public void A_project_round_trips_every_field_the_file_promises()
    {
        string directory = NewDirectory();
        MeasurementSession session = MeasurementSession.Start(directory, Grid, Sweep);

        MeasurementSlot done = session.MarkDone(session.NextPending!, Recording(512, 20.0), ImpulseResponse(128), 0.0055, Capture("1"));
        MeasurementSlot second = session.MarkDone(session.NextPending!, Recording(512, 30.0), ImpulseResponse(96), 0.0060, Capture("1", gain: 0.5, polarity: -1, phaseDegrees: 15.0));
        MeasurementSlot third = session.MarkDone(session.NextPending!, Recording(256, 40.0), ImpulseResponse(64), 0.0040, Capture("2"));
        MeasurementSlot invalid = session.MarkInvalid(session.NextPending!, Recording(256, 10.0), ImpulseResponse(32), [QualityIssue.LowSignalToNoise], 0.0001, Capture("2"));
        MeasurementSlot skipped = session.MarkSkipped(session.NextPending!);

        // Reopen from disk only: nothing is handed over in memory.
        ProjectLoadResult loaded = SessionStore.Load(directory, withSignals: true);
        Assert.Equal(ProjectLoadProblem.None, loaded.Problem);
        SessionManifest manifest = loaded.Require();

        // Metadata, bit-exact: strings and numbers are stored as themselves, so equality is exact, not within a
        // tolerance. 3 modes × 27 points = 81 entries.
        Assert.Equal(SessionManifest.CurrentSchemaVersion, manifest.SchemaVersion);   // 2
        Assert.Equal(session.ProjectId, manifest.ProjectId);
        Assert.Equal(session.CreatedUtc, manifest.CreatedUtc);
        Assert.Equal(81, manifest.Slots.Count);
        Assert.Equal(session.Sweep, new SweepSettings(manifest.Sweep.StartHz, manifest.Sweep.EndHz, manifest.Sweep.DurationSeconds, manifest.Sweep.SampleRate));
        Assert.Equal(27, manifest.Grid.CountX * manifest.Grid.CountY * manifest.Grid.CountZ);
        Assert.Equal(Grid.Points.Select(p => p.Id), manifest.Slots.Select(s => s.PointId).Distinct());

        SlotManifest reloaded = manifest.Slots.Single(s => s.MeasurementId == done.Id);
        Assert.Equal("A/x-1_y-1_z-1", reloaded.MeasurementId);
        Assert.Equal("A", reloaded.Mode);
        Assert.Equal("Done", reloaded.State);
        Assert.Empty(reloaded.Reasons);
        Assert.Equal(0.0055, reloaded.PeakMagnitude);
        Assert.Equal(done.CompletedUtc, reloaded.CompletedUtc);
        Assert.Equal(done.RecordingFileName, reloaded.RecordingFile);
        Assert.Equal(done.ImpulseResponseFileName, reloaded.ImpulseResponseFile);
        Assert.Equal(Capture("1"), reloaded.Capture);

        SlotManifest reloadedSecond = manifest.Slots.Single(s => s.MeasurementId == second.Id);
        Assert.Equal(-1, reloadedSecond.Capture!.Polarity);
        Assert.Equal(15.0, reloadedSecond.Capture.PhaseDegrees);
        Assert.Equal(0.5, reloadedSecond.Capture.PlaybackGain);

        // State and names round-trip for the non-Done cases too.
        Assert.Equal("Invalid", manifest.Slots.Single(s => s.MeasurementId == invalid.Id).State);
        Assert.Equal(["LowSignalToNoise"], manifest.Slots.Single(s => s.MeasurementId == invalid.Id).Reasons);
        Assert.Equal("Skipped", manifest.Slots.Single(s => s.MeasurementId == skipped.Id).State);

        // Signal data: recordings are float32-representable, so the file layer is exactly invertible.
        Assert.Equal(Recording(512, 20.0), loaded.Measurements.Single(m => m.Slot.MeasurementId == done.Id).Recording);
        Assert.Equal(ImpulseResponse(128), loaded.Measurements.Single(m => m.Slot.MeasurementId == done.Id).ImpulseResponse);
        Assert.Equal(Recording(512, 30.0), loaded.Measurements.Single(m => m.Slot.MeasurementId == second.Id).Recording);
        Assert.Equal(third.RecordingFileName, loaded.Measurements.Single(m => m.Slot.MeasurementId == third.Id).Slot.RecordingFile);
        // Every slot that claims a measurement has its files; pending and skipped ones have none by definition.
        Assert.All(loaded.Measurements.Where(m => m.Slot.State is "Done" or "Invalid"), m => Assert.True(m.HasSignals));
        Assert.All(loaded.Measurements.Where(m => m.Slot.State is "Pending" or "Skipped"), m => Assert.False(m.HasSignals));

        // "Reload does not re-measure": the recording that came back is the 20 Hz sine we wrote, not the
        // project's sweep — a loader that measured would have had to produce the sweep.
        double[] expectedSweep = SweepGenerator.GenerateExponentialSweep(Sweep);
        double[] returned = loaded.Measurements.Single(m => m.Slot.MeasurementId == done.Id).Recording!;
        Assert.NotEqual(expectedSweep.Length, returned.Length);
        Assert.Equal(Recording(512, 20.0)[1], returned[1]);

        _output.WriteLine($"round trip: {manifest.Slots.Count} slots, {loaded.Measurements.Count(m => m.HasSignals)} with signals, "
            + $"first recording {returned.Length} samples (sine), sweep is {expectedSweep.Length}");
    }

    [Fact]
    public void A_27_point_project_round_trips_and_resumes_without_re_measuring()
    {
        string directory = NewDirectory();
        MeasurementSession session = MeasurementSession.Start(directory, Grid, Sweep);

        foreach (MeasurementSlot slot in session.Slots.Where(s => s.Mode == SubMode.A).ToList())
            session.MarkDone(slot, Recording(256), ImpulseResponse(32), 0.0050, Capture("1"));

        // 27 recordings + 27 impulse responses + 1 manifest = 55 files, and no frequency response file at all:
        // the FR is derived, so persisting it would freeze one window/crop interpretation as truth.
        Assert.Equal(3 * 27, session.Slots.Count);
        Assert.Equal(27, session.DoneCount);
        Assert.Equal(55, Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length);
        Assert.DoesNotContain(Directory.GetFiles(directory, "*", SearchOption.AllDirectories), path => path.Contains(".fr.", StringComparison.Ordinal));

        ProjectLoadResult loaded = SessionStore.Load(directory, withSignals: true);
        Assert.Equal(ProjectLoadProblem.None, loaded.Problem);
        Assert.Equal(81, loaded.Require().Slots.Count);
        Assert.Equal(27, loaded.Measurements.Count(m => m.Slot.State == "Done"));
        Assert.Equal(27, loaded.Measurements.Count(m => m.Slot.State == "Done" && m.HasSignals));
        Assert.All(loaded.Measurements.Where(m => m.Slot.State == "Done"), m => Assert.Equal(Recording(256), m.Recording));

        // The session resumes from the same files: 27 done, 54 pending, the project id intact.
        MeasurementSession resumed = MeasurementSession.Start(directory, Grid, Sweep);
        Assert.Equal(session.ProjectId, resumed.ProjectId);
        Assert.Equal(27, resumed.DoneCount);
        Assert.Equal(54, resumed.PendingCount);
        Assert.Equal(0, resumed.InvalidCount);
        Assert.Equal(Capture("1"), resumed.Slots.Single(s => s.Id == "A/x-1_y-1_z-1").Capture);
        Assert.Equal(SubMode.B, resumed.NextPending!.Mode);                  // slots are mode-major: A, then B, then AB

        // Storage cost, measured: 27 × (256 recording + 32 IR) float32 samples = 27 × 4 × 288 bytes of audio.
        long audioBytes = loaded.Measurements.Where(m => m.HasSignals)
            .Sum(m => 4L * (m.Recording!.Length + m.ImpulseResponse!.Length));
        _output.WriteLine($"27-point project: {Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length} files, "
            + $"{audioBytes} audio bytes, manifest {new FileInfo(SessionStore.ManifestPath(directory)).Length} bytes");
    }

    [Fact]
    public void The_file_layer_is_exactly_invertible_for_float32_representable_values()
    {
        string directory = NewDirectory();
        double[] samples = Recording(4096, 37.5);

        SessionStore.Save(directory, Manifest(samples.Length));
        SessionStore.WriteRecording(directory, Slot(samples.Length), samples, 48000);

        double[] returned = SessionStore.Load(directory, withSignals: true).Measurements[0].Recording!;
        Assert.Equal(samples.Length, returned.Length);
        // Identical doubles, not "close": every value was already a float32, so the write is lossless.
        for (int i = 0; i < samples.Length; i++) Assert.Equal(samples[i], returned[i]);
    }

    [Fact]
    public void Storing_original_doubles_costs_at_most_half_an_ulp_of_float32()
    {
        string directory = NewDirectory();
        // Values that are NOT float32-representable: a 17.3-cycle sine plus an offset so very few samples land
        // exactly on a representable value.
        var samples = new double[4096];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = 0.7 * Math.Sin(2 * Math.PI * 17.3 * i / samples.Length) + 0.13 * Math.Cos(0.7 * i) + 0.011 * i / samples.Length;

        SessionStore.Save(directory, Manifest(samples.Length));
        SessionStore.WriteRecording(directory, Slot(samples.Length), samples, 48000);
        double[] returned = SessionStore.Load(directory, withSignals: true).Measurements[0].Recording!;

        double maxAbsolute = samples.Zip(returned).Max(pair => Math.Abs(pair.First - pair.Second));
        double maxRelative = samples.Zip(returned).Max(pair => Math.Abs(pair.First - pair.Second) / Math.Abs(pair.First));
        // Round-to-nearest float32 keeps the relative error at or below half an ulp = 2^-24 = 5.9604644775390625e-08.
        Assert.True(maxRelative <= Math.Pow(2, -24), $"relative error {maxRelative:E3} exceeds 2^-24");
        _output.WriteLine($"float32 quantization: max |Δ| = {maxAbsolute:E6} ({maxAbsolute / samples.Max(Math.Abs) * 100:F6}% of full scale), "
            + $"max relative = {maxRelative:E6} (bound 2^-24 = {Math.Pow(2, -24):E6})");
    }

    [Fact]
    public void A_missing_manifest_names_the_file_instead_of_throwing()
    {
        string directory = NewDirectory();
        Directory.CreateDirectory(directory);

        ProjectLoadResult loaded = SessionStore.Load(directory);
        Assert.Equal(ProjectLoadProblem.ManifestMissing, loaded.Problem);
        Assert.False(loaded.IsLoaded);
        Assert.Contains(SessionStore.ManifestFileName, loaded.Message);
        Assert.Equal([SessionStore.ManifestPath(directory)], loaded.AffectedPaths);
        Assert.Empty(loaded.Measurements);

        ProjectLoadResult absent = SessionStore.Load(NewDirectory());
        Assert.Equal(ProjectLoadProblem.DirectoryMissing, absent.Problem);
        Assert.Contains("does not exist", absent.Message);
    }

    [Fact]
    public void A_corrupt_manifest_is_reported_with_the_file_that_is_corrupt()
    {
        string directory = NewDirectory();
        Directory.CreateDirectory(directory);
        File.WriteAllText(SessionStore.ManifestPath(directory), "{ \"schemaVersion\": 2, \"slots\": [");

        ProjectLoadResult truncated = SessionStore.Load(directory);
        Assert.Equal(ProjectLoadProblem.ManifestUnreadable, truncated.Problem);
        Assert.False(truncated.IsLoaded);
        Assert.Contains(SessionStore.ManifestPath(directory), truncated.Message);

        // Structurally valid JSON that is missing the sections the format requires: reported, not dereferenced.
        File.WriteAllText(SessionStore.ManifestPath(directory),
            JsonSerializer.Serialize(new { schemaVersion = 2, createdUtc = DateTime.UtcNow, sweep = (object?)null, grid = (object?)null, slots = new List<object>() }));
        ProjectLoadResult incomplete = SessionStore.Load(directory);
        Assert.Equal(ProjectLoadProblem.ManifestUnreadable, incomplete.Problem);
        Assert.Contains("sweep, grid or measurement list", incomplete.Message);
        Assert.Null(incomplete.Manifest);
    }

    [Fact]
    public void A_newer_schema_version_is_refused_rather_than_misread()
    {
        string directory = NewDirectory();
        SessionStore.Save(directory, Manifest(0) with { SchemaVersion = SessionManifest.CurrentSchemaVersion + 1 });

        ProjectLoadResult loaded = SessionStore.Load(directory);
        Assert.Equal(ProjectLoadProblem.SchemaVersionUnsupported, loaded.Problem);
        Assert.Null(loaded.Manifest);                                        // nothing half-read is handed back
        Assert.Contains("version 3", loaded.Message);
        Assert.Contains("reads versions 1 to 2", loaded.Message);
        Assert.Contains("newer build", loaded.Message);

        SessionStore.Save(directory, Manifest(0) with { SchemaVersion = 0 });
        Assert.Equal(ProjectLoadProblem.SchemaVersionUnsupported, SessionStore.Load(directory).Problem);
    }

    [Fact]
    public void A_v1_manifest_still_loads_with_the_new_fields_derived_rather_than_invented()
    {
        string directory = NewDirectory();
        Directory.CreateDirectory(directory);
        // Exactly the shape phase 9/10 wrote: no projectId, no measurementId, no recordingFile, no capture.
        File.WriteAllText(SessionStore.ManifestPath(directory), """
            {
              "schemaVersion": 1,
              "createdUtc": "2026-09-18T11:06:27Z",
              "sweep": { "startHz": 20, "endHz": 150, "durationSeconds": 1.0, "sampleRate": 48000 },
              "grid": { "widthMetres": 1.8, "depthMetres": 1.0, "heightMetres": 0.6, "countX": 3, "countY": 3, "countZ": 3 },
              "slots": [ { "mode": "A", "pointId": "x0_y0_z0", "state": "Done", "reasons": [], "peakMagnitude": 0.005, "completedUtc": "2026-09-18T11:06:27Z" } ]
            }
            """);

        ProjectLoadResult loaded = SessionStore.Load(directory);
        Assert.Equal(ProjectLoadProblem.None, loaded.Problem);
        SlotManifest slot = loaded.Require().Slots.Single();
        Assert.Equal("A/x0_y0_z0", slot.MeasurementId);                      // derived from mode + point, not invented
        Assert.Equal("A/x0_y0_z0.wav", slot.RecordingFile);
        Assert.Equal("A/x0_y0_z0.ir.wav", slot.ImpulseResponseFile);
        Assert.Null(slot.Capture);                                          // genuinely absent in v1, reported as absent
        Assert.Equal(string.Empty, loaded.Require().ProjectId);
    }

    [Fact]
    public void An_enum_name_the_format_does_not_know_is_refused_with_the_offending_value_named()
    {
        string directory = NewDirectory();
        Directory.CreateDirectory(directory);
        // Valid JSON, valid schema version, meaningless content: the session must say which value is wrong
        // rather than letting a bare Enum.Parse message escape from three calls deep.
        File.WriteAllText(SessionStore.ManifestPath(directory), """
            {
              "schemaVersion": 2,
              "createdUtc": "2026-09-18T11:06:27Z",
              "sweep": { "startHz": 20, "endHz": 150, "durationSeconds": 1.0, "sampleRate": 48000 },
              "grid": { "widthMetres": 1.8, "depthMetres": 1.0, "heightMetres": 0.6, "countX": 3, "countY": 3, "countZ": 3 },
              "slots": [ { "mode": "C", "pointId": "x0_y0_z0", "state": "Done", "reasons": [], "peakMagnitude": 0.005, "completedUtc": "2026-09-18T11:06:27Z" } ]
            }
            """);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => MeasurementSession.Start(directory, Grid, Sweep));
        Assert.Contains("names the mode 'C'", failure.Message);
        Assert.Contains("A, B, AB", failure.Message);
        Assert.Contains(SessionStore.ManifestPath(directory), failure.Message);
    }

    [Fact]
    public void A_missing_recording_names_the_file_and_leaves_the_rest_usable()
    {
        string directory = NewDirectory();
        MeasurementSession session = MeasurementSession.Start(directory, Grid, Sweep);
        MeasurementSlot first = session.MarkDone(session.NextPending!, Recording(), ImpulseResponse(), 0.005, Capture("1"));
        MeasurementSlot second = session.MarkDone(session.NextPending!, Recording(), ImpulseResponse(), 0.006, Capture("1"));

        File.Delete(Path.Combine(directory, second.RecordingFileName));
        ProjectLoadResult loaded = SessionStore.Load(directory, withSignals: true);

        Assert.Equal(ProjectLoadProblem.SignalFileMissing, loaded.Problem);
        Assert.Equal([Path.Combine(directory, second.RecordingFileName)], loaded.AffectedPaths);
        Assert.Contains("missing", loaded.Message);
        Assert.True(loaded.IsLoaded);                                        // the metadata is still usable
        Assert.Equal(81, loaded.Manifest!.Slots.Count);
        Assert.True(loaded.Measurements.Single(m => m.Slot.MeasurementId == first.Id).HasSignals);
        Assert.False(loaded.Measurements.Single(m => m.Slot.MeasurementId == second.Id).HasSignals);

        // And the session's own rule still holds: a done slot whose recording vanished goes back to pending.
        MeasurementSession resumed = MeasurementSession.Start(directory, Grid, Sweep);
        Assert.Equal(1, resumed.DoneCount);
        Assert.Equal(MeasurementSlotState.Pending, resumed.Slots.Single(s => s.Id == second.Id).State);
    }

    [Fact]
    public void A_unreadable_recording_is_reported_and_not_treated_as_a_measurement()
    {
        string directory = NewDirectory();
        MeasurementSession session = MeasurementSession.Start(directory, Grid, Sweep);
        MeasurementSlot slot = session.MarkDone(session.NextPending!, Recording(), ImpulseResponse(), 0.005, Capture("1"));
        File.WriteAllBytes(Path.Combine(directory, slot.RecordingFileName), [1, 2, 3, 4, 5, 6, 7, 8]);

        ProjectLoadResult loaded = SessionStore.Load(directory, withSignals: true);
        Assert.Equal(ProjectLoadProblem.SignalFileUnreadable, loaded.Problem);
        Assert.Equal([Path.Combine(directory, slot.RecordingFileName)], loaded.AffectedPaths);
        Assert.False(loaded.Measurements.Single(m => m.Slot.MeasurementId == slot.Id).HasSignals);
    }

    [Fact]
    public void The_load_path_cannot_open_a_device_structural_proof()
    {
        Assembly loadAssembly = typeof(SessionStore).Assembly;
        string[] referenced = [.. loadAssembly.GetReferencedAssemblies().Select(name => name.Name!)];

        // 1) It does not reference the hardware assembly at all, nor the session type that drives it.
        Assert.DoesNotContain("AudioOptimizer.Audio", referenced);
        Assert.DoesNotContain("AudioOptimizer.Measurement", referenced);
        Assert.DoesNotContain("AudioOptimizer.Core", referenced);
        Assert.Contains("System.Runtime", referenced);

        // 2) Its target framework is plain net10.0, not the -windows TFM the hardware projects use: there is no
        //    Windows-only API it could even reach.
        string framework = loadAssembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName;
        Assert.Equal(".NETCoreApp,Version=v10.0", framework);

        // 3) No public member of the format or the reader can accept or return anything from AudioOptimizer.Audio.
        Assembly audio = typeof(AudioOptimizer.Audio.IAudioBackend).Assembly;
        foreach (Type type in new[] { typeof(SessionStore), typeof(SessionManifest), typeof(SlotManifest), typeof(MeasurementCapture), typeof(ProjectLoadResult) })
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                foreach (Type parameter in method.GetParameters().Select(p => p.ParameterType))
                    Assert.NotEqual(audio, parameter.Assembly);

        Assert.DoesNotContain(
            typeof(SessionStore).GetMethods().SelectMany(m => m.GetParameters()).Select(p => p.ParameterType.Name),
            name => name == "IAudioBackend");
        _output.WriteLine($"load assembly references: {string.Join(", ", referenced)}");
    }

    private static SlotManifest Slot(int recordingLength) => new(
        "A", "x0_y0_z0", "Done", [], 0.005, new DateTime(2026, 9, 18, 11, 6, 27, DateTimeKind.Utc),
        "A/x0_y0_z0", "A/x0_y0_z0.wav", "A/x0_y0_z0.ir.wav", Capture("1"));

    private static SessionManifest Manifest(int recordingLength) => new(
        SessionManifest.CurrentSchemaVersion,
        new DateTime(2026, 9, 18, 11, 6, 27, DateTimeKind.Utc),
        new SweepManifest(20, 150, 1.0, 48000),
        new GridManifest(1.8, 1.0, 0.6, 3, 3, 3),
        [Slot(recordingLength)],
        "project-1");
}
