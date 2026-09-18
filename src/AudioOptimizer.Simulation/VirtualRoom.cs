namespace AudioOptimizer.Simulation;

using AudioOptimizer.Core;
using AudioOptimizer.Dsp;

/// <summary>
/// The whole off-line rig in one object: one room, N subs, M microphones, and the rules for turning a (sub, mic)
/// pair into a sampled signal. It is the "virtual hardware" the measurement chain runs against — a
/// <see cref="VirtualAudioBackend"/> is this object wearing <c>IAudioBackend</c>.
/// <para>
/// The recording for a configuration is the digital chain's own arithmetic and nothing else:
/// </para>
/// <code>
/// recording = Σ_sub micGain·( rotate_φ_sub(playback) ⊛ IR_sub ) + noise          IR_AB = IR_A + IR_B (time domain)
/// </code>
/// <para>
/// Gain, polarity and delay live in each sub's impulse response; the phase is a rotation of that sub's drive signal,
/// which is where a phase control physically sits and the only place the rotation is realisable
/// (see <see cref="PhaseRotation"/>). The A+B capture is therefore the physical sum of the two subs' recordings — the
/// two impulse responses summed in the time domain, never a magnitude or dB addition — and it is bit-for-bit the sum
/// of the A and B captures with the noise floor switched off. The noise is added once per capture, at the microphone,
/// after the gain (a microphone's self-noise does not scale with what is playing), and comes from a seeded generator,
/// so the same configuration produces the same bytes.
/// </para>
/// </summary>
public sealed class VirtualRoom
{
    private readonly double[][][] _impulseResponses;   // [sub][microphone] → samples
    private readonly MeasurementPoint[] _microphones;

    public VirtualRoom(
        SimulationConfig config,
        RoomModel room,
        IReadOnlyList<VirtualSubwoofer> subs,
        IReadOnlyList<MeasurementPoint> microphones)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(subs);
        ArgumentNullException.ThrowIfNull(microphones);
        room.Validate();
        if (subs.Count is < 1 or > 2) throw new ArgumentException("A virtual room carries one or two subs (A, and optionally B).", nameof(subs));
        if (microphones.Count == 0) throw new ArgumentException("A virtual room needs at least one microphone.", nameof(microphones));
        foreach (VirtualSubwoofer sub in subs) sub.Validate();

        Config = config;
        Room = room;
        Subs = subs;
        _microphones = [.. microphones];

        // The response of each (sub, microphone) pair is a property of the geometry, so it is rendered once here.
        _impulseResponses = new double[subs.Count][][];
        for (int s = 0; s < subs.Count; s++)
        {
            _impulseResponses[s] = new double[_microphones.Length][];
            for (int m = 0; m < _microphones.Length; m++)
                _impulseResponses[s][m] = subs[s].ImpulseResponse(
                    room, _microphones[m].Position(), config.ImageSourceOrder, config.SampleRate,
                    config.ReferenceDistanceMetres, config.Placement);
        }
    }

    public SimulationConfig Config { get; }

    public RoomModel Room { get; }

    public IReadOnlyList<VirtualSubwoofer> Subs { get; }

    public IReadOnlyList<MeasurementPoint> Microphones => _microphones;

    /// <summary>The effective impulse response of one sub at one microphone, as rendered (settings included).</summary>
    public double[] ImpulseResponseOf(int subIndex, int microphoneIndex) => _impulseResponses[subIndex][microphoneIndex];

    /// <summary>IR_A + IR_B for the A+B configuration — the physical sum, in the time domain.</summary>
    public double[] CombinedImpulseResponse(int microphoneIndex)
    {
        if (_impulseResponses.Length < 2) throw new InvalidOperationException("This room has only sub A; there is no A+B response.");
        double[] a = _impulseResponses[0][microphoneIndex];
        double[] b = _impulseResponses[1][microphoneIndex];
        double[] sum = new double[Math.Max(a.Length, b.Length)];
        for (int i = 0; i < sum.Length; i++)
            sum[i] = (i < a.Length ? a[i] : 0.0) + (i < b.Length ? b[i] : 0.0);
        return sum;
    }

    /// <summary>
    /// The microphone records the drive signal convolved with the configuration's impulse responses, with the
    /// microphone's noise added across the whole capture.
    /// <para>
    /// The convolution tail is cut at the end of the capture, which is what a finite capture does; the scenarios keep
    /// the ISM order and the post-roll far enough apart that the tail is inside the window. The noise seed is derived
    /// from the configuration, the sub mode and the microphone, not from a call counter, so measuring one point twice
    /// — or measuring points in a different order — reproduces the same bytes.
    /// </para>
    /// </summary>
    public double[] Record(
        double[] playback,
        SubMode mode,
        int microphoneIndex,
        double preRollSeconds,
        double postRollSeconds,
        uint noiseSeed)
    {
        ArgumentNullException.ThrowIfNull(playback);
        if (microphoneIndex < 0 || microphoneIndex >= _microphones.Length)
            throw new ArgumentOutOfRangeException(nameof(microphoneIndex), microphoneIndex, "No such microphone.");

        int sampleRate = Config.SampleRate;
        int preRoll = (int)Math.Round(preRollSeconds * sampleRate);
        int postRoll = (int)Math.Round(postRollSeconds * sampleRate);
        var recording = new double[preRoll + playback.Length + postRoll];

        foreach (int subIndex in Contributors(mode))
        {
            // The sub's own rotation of the drive signal. A non-zero phase returns a padded drive whose ring lives in
            // the padding, so it is placed that much earlier — the rotation adds no delay of its own.
            double[] drive = PhaseRotation.Apply(playback, Subs[subIndex].PhaseDegrees, out int offsetSamples);
            int start = preRoll - offsetSamples;
            if (start < 0)
                throw new InvalidOperationException(
                    $"A {Subs[subIndex].PhaseDegrees}° phase needs {offsetSamples} samples of pre-roll for the rotation's ring, "
                    + $"but only {preRoll} samples were configured.");

            double[] impulseResponse = _impulseResponses[subIndex][microphoneIndex];
            double[] convolved = Fft.Convolve(drive, impulseResponse);
            TrimRoundOff(convolved, impulseResponse, drive.Length);

            int copyLength = Math.Min(convolved.Length, recording.Length - start);
            for (int i = 0; i < copyLength; i++) recording[start + i] += Config.MicrophoneGain * convolved[i];
        }

        AddNoise(recording, noiseSeed);
        return recording;
    }

    /// <summary>Which subs a configuration drives. The A+B pass drives both at once, which is the physical sum.</summary>
    private int[] Contributors(SubMode mode) => mode switch
    {
        SubMode.A => [0],
        SubMode.B => Subs.Count > 1 ? [1] : throw new InvalidOperationException("Sub mode B was measured, but this room has only sub A."),
        SubMode.AB => Subs.Count > 1
            ? [0, 1]
            : throw new InvalidOperationException("Sub mode A+B was measured, but this room has only sub A."),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown sub mode."),
    };

    /// <summary>
    /// Zeroes the convolution where it cannot have any support: before the impulse response's first sample and after
    /// its last plus the playback length. The FFT convolution leaves round-off in those regions (~1e-10 relative to the
    /// signal, so well below audibility but above the dropout check's 1e-9 silence level), and a rig with the noise
    /// floor switched off must render silence as silence rather than as numerical dust. This is arithmetic hygiene, not
    /// a model: it removes nothing the inputs contain.
    /// </summary>
    private static void TrimRoundOff(double[] convolved, double[] impulseResponse, int playbackLength)
    {
        int first = 0;
        while (first < impulseResponse.Length && impulseResponse[first] == 0.0) first++;
        if (first == impulseResponse.Length) return;                       // an all-zero response: leave the silence alone

        int last = impulseResponse.Length - 1;
        while (last > first && impulseResponse[last] == 0.0) last--;

        int start = Math.Min(first, convolved.Length);
        int end = Math.Min(last + playbackLength, convolved.Length);
        for (int i = 0; i < start; i++) convolved[i] = 0.0;
        for (int i = end; i < convolved.Length; i++) convolved[i] = 0.0;
    }

    /// <summary>
    /// Deterministic uniform noise with RMS = <see cref="SimulationConfig.MicrophoneNoiseLevel"/>. A hand-rolled LCG
    /// rather than <c>Random</c>: the point of the noise here is that it is reproducible, and this generator's
    /// sequence is fixed by this file rather than by a framework version.
    /// </summary>
    private void AddNoise(double[] recording, uint noiseSeed)
    {
        double level = Config.MicrophoneNoiseLevel;
        if (level == 0.0) return;

        // Uniform in [−1, 1] has RMS 1/√3, so this reaches the configured RMS exactly.
        double amplitude = level * Math.Sqrt(3.0);
        uint state = noiseSeed;
        for (int i = 0; i < recording.Length; i++)
        {
            state = (state * 1664525u) + 1013904223u;
            double uniform = ((state >> 8) / (double)(1 << 23)) - 1.0;
            recording[i] += amplitude * uniform;
        }
    }

    /// <summary>One noise seed per (run, mode, microphone): same inputs, same noise — independent of call order.</summary>
    public uint NoiseSeedFor(SubMode mode, int microphoneIndex)
        => unchecked((uint)(Config.NoiseSeed + (7919 * (int)mode) + (104729 * microphoneIndex)));
}

/// <summary>Which microphone a <c>MeasurementPoint</c> is, for the backend's device labels.</summary>
internal static class MeasurementPointExtensions
{
    /// <summary>The point's position: the grid type carries the coordinates it was built from.</summary>
    public static Position Position(this MeasurementPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        return new Position(point.X, point.Y, point.Z);
    }
}
