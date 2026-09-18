namespace AudioOptimizer.Measurement;

using AudioOptimizer.Core;
using AudioOptimizer.Dsp;
using AudioOptimizer.IO;

/// <summary>
/// A measurement session: three modes over one grid (3 × nx·ny·nz slots), each slot pending, done, invalid with
/// named reasons, or explicitly skipped — with its raw recording on disk and a project manifest that makes the
/// whole thing resumable and reopenable. 81 points is a long sitting and a crash at point 60 must not cost the
/// first 59; that resume capability is the reason this type exists. The recording and the impulse response are
/// written out; the frequency response is NOT, because fs/window/crop change it and persisting one would freeze
/// one interpretation as truth.
/// Artifacts go to an EXPLICIT directory — nothing here can default into the working tree.
/// Not thread-safe: one runner drives one session, in one thread.
/// </summary>
public sealed class MeasurementSession
{
    public const string ManifestFileName = SessionStore.ManifestFileName;

    private readonly Dictionary<string, MeasurementSlot> _byId;
    private readonly List<MeasurementSlot> _slots;

    private MeasurementSession(string directory, string projectId, MeasurementGrid grid, SweepSettings sweep, DateTime createdUtc, List<MeasurementSlot> slots)
    {
        Directory = directory;
        ProjectId = projectId;
        Grid = grid;
        Sweep = sweep;
        CreatedUtc = createdUtc;
        _slots = slots;
        _byId = slots.ToDictionary(slot => slot.Id);
    }

    /// <summary>Session directory: every manifest and recording lives below it.</summary>
    public string Directory { get; }

    /// <summary>Stable id of the project this session belongs to; persists across resume.</summary>
    public string ProjectId { get; }

    public MeasurementGrid Grid { get; }

    public SweepSettings Sweep { get; }

    public DateTime CreatedUtc { get; }

    /// <summary>All slots, mode-major: every point of A, then of B, then of AB.</summary>
    public IReadOnlyList<MeasurementSlot> Slots => _slots;

    public IReadOnlyList<MeasurementSlot> Pending => [.. Slots.Where(slot => slot.State == MeasurementSlotState.Pending)];

    public MeasurementSlot? NextPending => Slots.FirstOrDefault(slot => slot.State == MeasurementSlotState.Pending);

    public int PendingCount => Slots.Count(slot => slot.State == MeasurementSlotState.Pending);

    public int DoneCount => Slots.Count(slot => slot.State == MeasurementSlotState.Done);

    public int InvalidCount => Slots.Count(slot => slot.State == MeasurementSlotState.Invalid);

    public int SkippedCount => Slots.Count(slot => slot.State == MeasurementSlotState.Skipped);

    /// <summary>
    /// Opens the session in <paramref name="directory"/>: resumes the project already there, or starts a fresh
    /// one when there is none. The directory is required — a defaulted path is how a session's WAVs end up in a
    /// repository. A project that describes a different grid or sweep is refused rather than merged, a project
    /// with an unreadable or too-new manifest is refused with the reason the loader wrote, and a slot recorded
    /// as done whose recording file has gone missing is demoted back to pending, because the manifest is only a
    /// claim about the disk.
    /// </summary>
    public static MeasurementSession Start(string directory, MeasurementGrid grid, SweepSettings sweep)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(grid);
        sweep.Validate();

        ProjectLoadResult loaded = SessionStore.Load(directory);
        MeasurementSession session = loaded switch
        {
            // No directory yet, or a directory with no manifest: both mean "start fresh here".
            { Problem: ProjectLoadProblem.ManifestMissing or ProjectLoadProblem.DirectoryMissing } => new MeasurementSession(
                directory, Guid.NewGuid().ToString("N"), grid, sweep, DateTime.UtcNow, FreshSlots(grid)),
            { IsLoaded: true } => Resume(directory, loaded.Manifest!, grid, sweep),
            _ => throw new InvalidOperationException(loaded.Message),
        };

        System.IO.Directory.CreateDirectory(directory);
        session.Save();
        return session;
    }

    /// <summary>Writes the recording and the impulse response, then marks the slot done with the capture conditions.</summary>
    public MeasurementSlot MarkDone(
        MeasurementSlot slot,
        double[] recording,
        double[] impulseResponse,
        double peakMagnitude,
        MeasurementCapture? capture = null)
    {
        MeasurementSlot current = Require(slot);
        WriteSignals(current, recording, impulseResponse);
        return Update(current with
        {
            State = MeasurementSlotState.Done,
            Reasons = [],
            PeakMagnitude = peakMagnitude,
            CompletedUtc = DateTime.UtcNow,
            Capture = capture,
        });
    }

    /// <summary>Writes the recording and the impulse response, then marks the slot invalid with the reasons that say why.</summary>
    public MeasurementSlot MarkInvalid(
        MeasurementSlot slot,
        double[] recording,
        double[] impulseResponse,
        IReadOnlyList<QualityIssue> reasons,
        double? peakMagnitude = null,
        MeasurementCapture? capture = null)
    {
        ArgumentNullException.ThrowIfNull(reasons);
        if (reasons.Count == 0) throw new ArgumentException("An invalid slot needs at least one named reason.", nameof(reasons));

        MeasurementSlot current = Require(slot);
        WriteSignals(current, recording, impulseResponse);
        return Update(current with
        {
            State = MeasurementSlotState.Invalid,
            Reasons = [.. reasons],
            PeakMagnitude = peakMagnitude,
            CompletedUtc = DateTime.UtcNow,
            Capture = capture,
        });
    }

    /// <summary>Marks the slot skipped: a deliberate decision, so resume will not re-measure it.</summary>
    public MeasurementSlot MarkSkipped(MeasurementSlot slot)
    {
        MeasurementSlot current = Require(slot);
        return Update(current with { State = MeasurementSlotState.Skipped, Reasons = [], PeakMagnitude = null, CompletedUtc = DateTime.UtcNow });
    }

    /// <summary>The raw recording for a slot, or null when no file was ever written for it.</summary>
    public double[]? ReadRecording(MeasurementSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        string path = Path.Combine(Directory, slot.RecordingFileName);
        return File.Exists(path) ? WavFile.Read(path).Samples : null;
    }

    /// <summary>
    /// The impulse response as the project stored it. Reading it costs nothing and is what a reopened project
    /// actually used; <see cref="ImpulseResponseOf"/> instead rebuilds it from the recording.
    /// </summary>
    public double[]? StoredImpulseResponse(MeasurementSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        string path = Path.Combine(Directory, slot.ImpulseResponseFileName);
        return File.Exists(path) ? WavFile.Read(path).Samples : null;
    }

    /// <summary>Rebuilds the impulse response from the stored recording — the same chain the measurement ran.</summary>
    public ImpulseResponse? ImpulseResponseOf(MeasurementSlot slot)
    {
        if (ReadRecording(slot) is not { } recording) return null;
        double[] inverseFilter = InverseFilter.BuildExponentialInverseSweep(Sweep);
        return new ImpulseResponse(Deconvolver.Deconvolve(recording, inverseFilter), (int)Sweep.SampleRate);
    }

    /// <summary>Rebuilds the frequency response from the stored recording, rectangular window on the full IR.</summary>
    public FrequencyResponse[]? FrequencyResponseOf(MeasurementSlot slot)
        => ImpulseResponseOf(slot) is { } impulseResponse
            ? FrequencyResponseCalculator.Compute(impulseResponse, WindowType.Rectangular)
            : null;

    /// <summary>
    /// The measured bins inside this session's own sweep band — the band belongs to the measurement, not to a figure.
    /// The sweep excited nothing outside it, so a figure, an axis or an optimizer input built from the full spectrum
    /// would claim a range that was never measured. <see cref="InverseFilter"/> has band-limited for the same reason
    /// since Phase 1, and <see cref="SessionManifest"/> already refuses to resume a session with a different sweep.
    /// </summary>
    public static FrequencyResponse[] InBand(IReadOnlyList<FrequencyResponse> bins, FrequencyBand band)
    {
        ArgumentNullException.ThrowIfNull(bins);
        return [.. bins.Where(bin => band.Contains(bin.FrequencyHz))];
    }

    /// <summary>
    /// The same for one slot: the only frequencies anything downstream of the session may report, plot or optimise
    /// over. Null when the slot has not been measured, like <see cref="FrequencyResponseOf"/>.
    /// </summary>
    public FrequencyResponse[]? InBandResponseOf(MeasurementSlot slot)
        => FrequencyResponseOf(slot) is { Length: > 0 } bins ? InBand(bins, FrequencyBand.Of(Sweep)) : null;

    /// <summary>This session's sweep band. The band belongs to the measurement, not to a figure.</summary>
    public FrequencyBand Band => FrequencyBand.Of(Sweep);

    /// <summary>
    /// Median |IR peak| over the done slots — the reference the quality checks compare a new point against. 0
    /// when nothing has been measured yet, which the caller treats as "the first point defines the scale".
    /// Taken from the stored magnitudes, so it costs no deconvolution.
    /// </summary>
    public double MedianPeakMagnitude()
    {
        double[] magnitudes = [.. Slots
            .Where(slot => slot.State == MeasurementSlotState.Done && slot.PeakMagnitude is > 0)
            .Select(slot => slot.PeakMagnitude!.Value)
            .Order()];
        if (magnitudes.Length == 0) return 0;
        int middle = magnitudes.Length / 2;
        return magnitudes.Length % 2 == 1
            ? magnitudes[middle]
            : (magnitudes[middle - 1] + magnitudes[middle]) / 2.0;
    }

    private static List<MeasurementSlot> FreshSlots(MeasurementGrid grid)
    {
        var slots = new List<MeasurementSlot>(Enum.GetValues<SubMode>().Length * grid.PointCount);
        foreach (SubMode mode in Enum.GetValues<SubMode>())
            foreach (MeasurementPoint point in grid.Points)
                slots.Add(MeasurementSlot.Pending(mode, point));
        return slots;
    }

    private static MeasurementSession Resume(string directory, SessionManifest manifest, MeasurementGrid grid, SweepSettings sweep)
    {
        string manifestPath = SessionStore.ManifestPath(directory);
        var stored = MeasurementGrid.Create(
            manifest.Grid.WidthMetres, manifest.Grid.DepthMetres, manifest.Grid.HeightMetres,
            manifest.Grid.CountX, manifest.Grid.CountY, manifest.Grid.CountZ);
        if (!stored.Points.Select(p => p.Id).SequenceEqual(grid.Points.Select(p => p.Id)))
            throw new InvalidOperationException($"'{manifestPath}' holds a {stored.PointCount}-point session over "
                + $"{stored.WidthMetres}×{stored.DepthMetres}×{stored.HeightMetres} m, not the {grid.PointCount}-point grid asked for. "
                + "Point at a different directory, or delete the manifest to start over.");

        if (manifest.Sweep.SampleRate != sweep.SampleRate
            || manifest.Sweep.StartHz != sweep.StartHz
            || manifest.Sweep.EndHz != sweep.EndHz
            || manifest.Sweep.DurationSeconds != sweep.DurationSeconds)
            throw new InvalidOperationException($"'{manifestPath}' was measured with sweep {manifest.Sweep.StartHz}-{manifest.Sweep.EndHz} Hz, "
                + $"{manifest.Sweep.DurationSeconds} s at {manifest.Sweep.SampleRate} Hz; resuming it with a different sweep would mix two measurements.");

        var slots = new List<MeasurementSlot>(manifest.Slots.Count);
        foreach (SlotManifest entry in manifest.Slots)
        {
            MeasurementPoint point = grid.Points.SingleOrDefault(p => p.Id == entry.PointId)
                ?? throw new InvalidOperationException($"'{manifestPath}' names the point '{entry.PointId}', which is not on this grid.");
            SubMode mode = ParseEnum<SubMode>(entry.Mode, manifestPath, "mode");
            var slot = new MeasurementSlot(
                mode,
                point,
                ParseEnum<MeasurementSlotState>(entry.State, manifestPath, "slot state"),
                [.. entry.Reasons.Select(reason => ParseEnum<QualityIssue>(reason, manifestPath, "quality reason"))],
                entry.PeakMagnitude,
                entry.CompletedUtc,
                entry.Capture);

            // The manifest is a claim; the disk is the fact. A finished slot whose recording is gone is pending.
            if (slot.State != MeasurementSlotState.Pending
                && slot.State != MeasurementSlotState.Skipped
                && !File.Exists(Path.Combine(directory, slot.RecordingFileName)))
                slot = slot with { State = MeasurementSlotState.Pending, Reasons = [], PeakMagnitude = null, CompletedUtc = null, Capture = null };

            slots.Add(slot);
        }

        return new MeasurementSession(directory, manifest.ProjectId, grid, sweep, manifest.CreatedUtc, slots);
    }

    /// <summary>
    /// A name the format does not know is a file that cannot be turned into a session; say which value and
    /// which file instead of letting Enum.Parse's message float out of a deep call.
    /// </summary>
    private static T ParseEnum<T>(string value, string manifestPath, string what) where T : struct, Enum
        => Enum.TryParse(value, out T parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"'{manifestPath}' names the {what} '{value}', which this build does not know ({string.Join(", ", Enum.GetNames<T>())}).");

    private MeasurementSlot Require(MeasurementSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return _byId.TryGetValue(slot.Id, out MeasurementSlot? current)
            ? current
            : throw new ArgumentException($"'{slot.Id}' is not a slot of this session.", nameof(slot));
    }

    private MeasurementSlot Update(MeasurementSlot slot)
    {
        _byId[slot.Id] = slot;
        _slots[_slots.FindIndex(existing => existing.Id == slot.Id)] = slot;
        Save();
        return slot;
    }

    private void WriteSignals(MeasurementSlot slot, double[] recording, double[] impulseResponse)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(impulseResponse);
        int sampleRate = (int)Sweep.SampleRate;
        SessionStore.WriteRecording(Directory, slot.ToManifest(), recording, sampleRate);
        SessionStore.WriteImpulseResponse(Directory, slot.ToManifest(), impulseResponse, sampleRate);
    }

    private void Save()
    {
        var manifest = new SessionManifest(
            SessionManifest.CurrentSchemaVersion,
            CreatedUtc,
            new SweepManifest(Sweep.StartHz, Sweep.EndHz, Sweep.DurationSeconds, Sweep.SampleRate),
            new GridManifest(Grid.WidthMetres, Grid.DepthMetres, Grid.HeightMetres, Grid.CountX, Grid.CountY, Grid.CountZ),
            [.. Slots.Select(slot => slot.ToManifest())],
            ProjectId);

        SessionStore.Save(Directory, manifest);
    }
}
