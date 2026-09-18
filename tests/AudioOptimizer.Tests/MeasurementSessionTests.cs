namespace AudioOptimizer.Tests;

using AudioOptimizer.Core;
using AudioOptimizer.Dsp;
using AudioOptimizer.IO;
using AudioOptimizer.Measurement;
using Xunit.Abstractions;

/// <summary>
/// The session's whole reason to exist is resume: 3 modes × 27 points is 81 measurements, and a crash at point
/// 60 must not cost the first 59. Everything here runs in the system temp directory — a session that defaulted
/// into the working tree is the bug this class guards against.
/// </summary>
public sealed class MeasurementSessionTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly List<string> _directories = [];

    public MeasurementSessionTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        foreach (string directory in _directories)
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private string NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "roomforge-session-" + Guid.NewGuid().ToString("N"));
        _directories.Add(directory);
        return directory;
    }

    private static MeasurementGrid Grid => MeasurementGrid.Create(1.8, 1.0, 0.6);

    private static SweepSettings Sweep => new();

    /// <summary>Any signal will do here; the session stores recordings, it does not judge them.</summary>
    private static double[] Recording(int length = 1024)
    {
        var samples = new double[length];
        for (int i = 0; i < length; i++) samples[i] = 0.5 * Math.Sin(2 * Math.PI * 20 * i / 48000.0);
        return samples;
    }

    /// <summary>Any impulse response will do either; the session stores it, it does not deconvolve it.</summary>
    private static double[] ImpulseResponse(int length = 256)
    {
        var samples = new double[length];
        samples[0] = 0.004;
        return samples;
    }

    [Fact]
    public void A_new_session_is_three_modes_over_the_grid_with_every_slot_pending()
    {
        string directory = NewDirectory();
        MeasurementSession session = MeasurementSession.Start(directory, Grid, Sweep);

        // 3 modes × 27 points = 81 slots, mode-major: all of A, then B, then AB.
        Assert.Equal(81, session.Slots.Count);
        Assert.Equal(81, session.PendingCount);
        Assert.Equal(0, session.DoneCount);
        Assert.Equal(SubMode.A, session.Slots[0].Mode);
        Assert.Equal(SubMode.AB, session.Slots[80].Mode);
        Assert.Equal("A/x-1_y-1_z-1", session.Slots[0].Id);
        Assert.Equal("AB/x1_y1_z1", session.Slots[80].Id);
        Assert.Equal(session.Slots[0].Id, session.NextPending!.Id);

        // The manifest is real text in the session directory, not just in-memory state. Schema 2 adds the
        // per-measurement capture metadata and the explicit signal file names to the schema-1 resume record.
        string manifest = File.ReadAllText(Path.Combine(directory, MeasurementSession.ManifestFileName));
        _output.WriteLine(manifest[..Math.Min(400, manifest.Length)]);
        Assert.Contains($"\"schemaVersion\": {SessionManifest.CurrentSchemaVersion}", manifest);
        Assert.Contains("\"state\": \"Pending\"", manifest);   // enum stored by NAME, so renumbering cannot rewrite history
        Assert.False(File.Exists(Path.Combine(directory, "smoketest.wav")));
    }

    [Fact]
    public void Resume_keeps_every_finished_point_and_skips_past_it()
    {
        string directory = NewDirectory();
        double[] recording = Recording();

        MeasurementSession session = MeasurementSession.Start(directory, Grid, Sweep);
        session.MarkDone(session.NextPending!, recording, ImpulseResponse(), peakMagnitude: 0.0055);
        session.MarkDone(session.NextPending!, recording, ImpulseResponse(), peakMagnitude: 0.0060);
        session.MarkDone(session.NextPending!, recording, ImpulseResponse(), peakMagnitude: 0.0050);
        MeasurementSlot fourth = session.NextPending!;
        session.MarkInvalid(fourth, recording, ImpulseResponse(), [QualityIssue.LowSignalToNoise], peakMagnitude: 0.0001);
        MeasurementSlot fifth = session.NextPending!;
        session.MarkSkipped(fifth);

        // Simulate the crash: nothing is held in memory, the new session only sees the directory.
        MeasurementSession resumed = MeasurementSession.Start(directory, Grid, Sweep);

        Assert.Equal(3, resumed.DoneCount);
        Assert.Equal(1, resumed.InvalidCount);
        Assert.Equal(1, resumed.SkippedCount);
        Assert.Equal(76, resumed.PendingCount);                              // 81 − 3 − 1 − 1
        Assert.Equal(1, resumed.Slots.Count(slot => slot.Id == fifth.Id && slot.State == MeasurementSlotState.Skipped));
        Assert.Equal([QualityIssue.LowSignalToNoise], resumed.Slots.Single(slot => slot.Id == fourth.Id).Reasons);
        Assert.Equal(0.0001, resumed.Slots.Single(slot => slot.Id == fourth.Id).PeakMagnitude);
        Assert.Equal(session.Slots[5].Id, resumed.NextPending!.Id);          // resume picks up where it stopped
        Assert.Equal(session.CreatedUtc, resumed.CreatedUtc);                // same session, not a new one

        // The stored recording comes back bit-for-bit (float32 WAV, no quantisation to lose).
        double[] restored = resumed.ReadRecording(resumed.Slots[0])!;
        Assert.Equal(recording.Length, restored.Length);
        Assert.Equal(0.0, recording.Zip(restored).Max(pair => Math.Abs(pair.First - pair.Second)), 1e-7);
    }

    [Fact]
    public void A_finished_slot_whose_recording_vanished_is_demoted_back_to_pending()
    {
        string directory = NewDirectory();
        MeasurementSession session = MeasurementSession.Start(directory, Grid, Sweep);
        MeasurementSlot done = session.MarkDone(session.NextPending!, Recording(), ImpulseResponse(), peakMagnitude: 0.0055);
        Assert.True(File.Exists(Path.Combine(directory, done.RecordingFileName)));
        Assert.True(File.Exists(Path.Combine(directory, done.ImpulseResponseFileName)));

        // The manifest is a claim; the disk is the fact.
        File.Delete(Path.Combine(directory, done.RecordingFileName));
        MeasurementSession resumed = MeasurementSession.Start(directory, Grid, Sweep);

        Assert.Equal(0, resumed.DoneCount);
        Assert.Equal(81, resumed.PendingCount);
        Assert.Equal(MeasurementSlotState.Pending, resumed.Slots.Single(slot => slot.Id == done.Id).State);
        Assert.Null(resumed.Slots.Single(slot => slot.Id == done.Id).PeakMagnitude);
    }

    [Fact]
    public void The_session_median_is_the_middle_peak_magnitude_of_the_done_slots_only()
    {
        string directory = NewDirectory();
        MeasurementSession session = MeasurementSession.Start(directory, Grid, Sweep);

        Assert.Equal(0.0, session.MedianPeakMagnitude());                    // nothing measured yet: no reference

        session.MarkDone(session.NextPending!, Recording(), ImpulseResponse(), 0.004);
        session.MarkDone(session.NextPending!, Recording(), ImpulseResponse(), 0.006);
        session.MarkDone(session.NextPending!, Recording(), ImpulseResponse(), 0.008);
        // Median of {0.004, 0.006, 0.008} = 0.006. Invalid slots are not a reference: their peak is the thing
        // the quality checks already refused.
        session.MarkInvalid(session.NextPending!, Recording(), ImpulseResponse(), [QualityIssue.OutputClipping], peakMagnitude: 9.9);
        Assert.Equal(0.006, session.MedianPeakMagnitude(), 1e-12);

        session.MarkDone(session.NextPending!, Recording(), ImpulseResponse(), 0.010);
        // Median of {0.004, 0.006, 0.008, 0.010} = (0.006 + 0.008)/2 = 0.007.
        Assert.Equal(0.007, session.MedianPeakMagnitude(), 1e-12);
    }

    [Fact]
    public void Impulse_response_and_frequency_response_are_rebuilt_from_the_stored_recording()
    {
        string directory = NewDirectory();
        double[] recording = SweepGenerator.GenerateExponentialSweep(Sweep);   // a real 48000-sample sweep
        MeasurementSession session = MeasurementSession.Start(directory, Grid, Sweep);
        MeasurementSlot slot = session.MarkDone(session.NextPending!, recording, ImpulseResponse(64), peakMagnitude: 0.0055);

        // Linear convolution length: 48000 + 48000 − 1 = 95999 samples, rebuilt with no measurement involved.
        ImpulseResponse? impulseResponse = session.ImpulseResponseOf(slot);
        Assert.NotNull(impulseResponse);
        Assert.Equal(95999, impulseResponse.Samples.Length);
        FrequencyResponse[]? response = session.FrequencyResponseOf(slot);
        Assert.NotNull(response);
        Assert.NotEmpty(response);
        Assert.Equal(0.0, response[0].FrequencyHz);
    }

    [Fact]
    public void Resuming_into_a_different_grid_or_sweep_is_refused_rather_than_merged()
    {
        string directory = NewDirectory();
        MeasurementSession.Start(directory, Grid, Sweep);

        // A different grid: same idea, different points — merging would silently mix coordinates.
        Assert.Contains("point grid", Assert.Throws<InvalidOperationException>(
            () => MeasurementSession.Start(directory, MeasurementGrid.Create(1.8, 1.0, 0.6, nz: 2), Sweep)).Message);

        // A different sweep: the recordings on disk would no longer be comparable.
        Assert.Contains("different sweep", Assert.Throws<InvalidOperationException>(
            () => MeasurementSession.Start(directory, Grid, new SweepSettings(20, 200, 1.0, 48000))).Message);
    }

    [Fact]
    public void The_session_directory_must_be_explicit_and_slots_must_belong_to_the_session()
    {
        // No default directory exists, so nothing can quietly land in the working tree.
        Assert.Throws<ArgumentException>(() => MeasurementSession.Start("  ", Grid, Sweep));

        string directory = NewDirectory();
        MeasurementSession session = MeasurementSession.Start(directory, Grid, Sweep);
        // A point that cannot exist on this grid: every ±1/0 id of a smaller grid IS a point of the 3×3×3 one.
        MeasurementSlot other = MeasurementSlot.Pending(SubMode.A, new MeasurementPoint("x9_y9_z9", 9, 9, 9, 9, 9, 9));

        Assert.Throws<ArgumentException>(() => session.MarkDone(other, Recording(), ImpulseResponse(), 0.005));
        Assert.Throws<ArgumentException>(() => session.MarkInvalid(session.Slots[0], Recording(), ImpulseResponse(), []));
        Assert.Null(session.ReadRecording(session.Slots[0]));                 // pending: no file, no throw
        Assert.Null(session.ImpulseResponseOf(session.Slots[0]));
    }
}
