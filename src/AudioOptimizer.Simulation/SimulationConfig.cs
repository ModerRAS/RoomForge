namespace AudioOptimizer.Simulation;

using AudioOptimizer.Audio;
using AudioOptimizer.Core;
using AudioOptimizer.Dsp;

/// <summary>
/// The ONE place the Virtual Acoustic Lab's defaults live. Nothing else in this project carries a room dimension, a
/// sample rate or a sweep frequency as a literal: a scenario that wants something different starts from
/// <see cref="Default"/> and changes that one field, so the configuration is greppable in one file.
/// </summary>
public sealed record SimulationConfig
{
    /// <summary>48 kHz, 20–150 Hz over 1 s, 3.3 × 3.6 × 2.6 m at 343 m/s, ISM order 3, −12 dBFS playback.</summary>
    public static readonly SimulationConfig Default = new();

    /// <summary>The DSP and capture sample rate, Hz.</summary>
    public int SampleRate { get; init; } = 48000;

    /// <summary>The RoomForge Phase 1 sweep: an exponential sweep from 20 Hz to 150 Hz over one second.</summary>
    public double SweepStartHz { get; init; } = 20.0;

    public double SweepEndHz { get; init; } = 150.0;

    public double SweepSeconds { get; init; } = 1.0;

    public double PreRollSeconds { get; init; } = 0.5;

    public double PostRollSeconds { get; init; } = 0.5;

    /// <summary>
    /// The electrical gain the virtual rig plays at, as a linear fraction of the generated sweep. The inverse filter
    /// is built from the UNSCALED analytical sweep, so this changes the level of the recording without changing the
    /// measured chain, and −12 dBFS leaves room for the room's own gain above the direct sound.
    /// </summary>
    public double PlaybackGain { get; init; } = 0.25;

    public RoomModel Room { get; init; } = RoomModel.Default;

    /// <summary>Reflections per axis in the image-source lattice: 0 is the direct sound alone, 3 is the default.</summary>
    public int ImageSourceOrder { get; init; } = 3;

    /// <summary>How each image-source arrival is written into the impulse response (integer or fractional delay).</summary>
    public ArrivalPlacement Placement { get; init; } = ArrivalPlacement.LinearInterpolated;

    /// <summary>The distance at which a source's direct path has amplitude 1.0 — the level calibration.</summary>
    public double ReferenceDistanceMetres { get; init; } = 1.0;

    /// <summary>Linear microphone gain, applied to the acoustic signal only (the noise floor is added after it).</summary>
    public double MicrophoneGain { get; init; } = 1.0;

    /// <summary>Microphone self-noise RMS as a fraction of full scale. 0 is a noiseless rig; 1e-6 ≈ −120 dBFS.</summary>
    public double MicrophoneNoiseLevel { get; init; } = 1e-6;

    /// <summary>
    /// The same floor in dBFS, the unit a person states it in. Reading derives it from <see cref="MicrophoneNoiseLevel"/>
    /// (0 reads as −∞, exact silence); writing stores the linear level, so both spellings are one physical knob, and
    /// the default 1e-6 reads as −120 dBFS.
    /// </summary>
    public double MicrophoneNoiseFloorDb
    {
        get => MicrophoneNoiseLevel == 0.0 ? double.NegativeInfinity : ComplexMath.LinearToDb(MicrophoneNoiseLevel);
        init => MicrophoneNoiseLevel = double.IsNegativeInfinity(value) ? 0.0 : ComplexMath.DbToLinear(value);
    }

    /// <summary>The microphone's own response shape; <see cref="MicrophoneResponseProfile.Perfect"/> is flat 0 dB.</summary>
    public MicrophoneResponseProfile MicrophoneProfile { get; init; } = MicrophoneResponseProfile.Perfect;

    /// <summary>
    /// Signed peak deviation of the microphone's response, dB: the low-frequency tilt sits at +deviation at 20 Hz and
    /// −deviation at 150 Hz, the high-frequency tilt is its mirror. 0 is flat, whatever the profile.
    /// </summary>
    public double MicrophoneDeviationDb { get; init; } = 0.0;

    /// <summary>
    /// Playback-versus-capture clock error in parts per million: the capture reads the recording's time axis at
    /// 1 + ppm·1e-6. 0 is an exact no-op, so every capture without it is bit-identical to before this knob existed.
    /// </summary>
    public double ClockPpm { get; init; } = 0.0;

    /// <summary>Seed of the noise generator. Same seed and same measurement → the same recording bytes.</summary>
    public int NoiseSeed { get; init; } = 20260101;

    public ListeningRegion ListeningRegion { get; init; } = ListeningRegion.Default;

    /// <summary>What a real capture session would use: 48 kHz, shared mode. Nothing here opens a device.</summary>
    public AudioBackendSettings BackendSettings => new(SampleRate, AudioShareMode.Shared, 100);

    /// <summary>The sweep the whole room is measured with.</summary>
    public SweepSettings Sweep => new(SweepStartHz, SweepEndHz, SweepSeconds, SampleRate);

    /// <summary>The duration a full capture occupies: pre-roll + sweep + post-roll.</summary>
    public double CaptureSeconds => PreRollSeconds + SweepSeconds + PostRollSeconds;

    /// <summary>
    /// The FFT size the measurement chain will use for a capture's frequency response: the deconvolution is
    /// (capture ⊛ inverse filter) samples long, and <c>PointMeasurement</c> takes the next power of two of that. It is
    /// derived here, from the same configuration, so the ground truth can be transformed onto the SAME frequency grid
    /// as the measurement — comparing two resolutions of a comb-filtered room response is comparing two different
    /// numbers, and it looks like a physics error.
    /// </summary>
    public int CaptureFftSize => Dsp.Fft.NextPowerOfTwo(
        (int)Math.Round(CaptureSeconds * SampleRate) + Sweep.SampleCount - 1);

    public SimulationConfig WithRoom(RoomModel room) => this with { Room = room };

    public void Validate()
    {
        Sweep.Validate();
        Room.Validate();
        if (ImageSourceOrder < 0) throw new ArgumentOutOfRangeException(nameof(ImageSourceOrder), ImageSourceOrder, "ISM order must be >= 0.");
        if (PreRollSeconds < 0 || PostRollSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(PreRollSeconds), PreRollSeconds, "Roll lengths must be >= 0 s.");
        if (!(PlaybackGain > 0 && PlaybackGain <= 1))
            throw new ArgumentOutOfRangeException(nameof(PlaybackGain), PlaybackGain, "Playback gain must be in (0, 1].");
        if (!(MicrophoneGain > 0)) throw new ArgumentOutOfRangeException(nameof(MicrophoneGain), MicrophoneGain, "Microphone gain must be > 0.");
        if (MicrophoneNoiseLevel < 0) throw new ArgumentOutOfRangeException(nameof(MicrophoneNoiseLevel), MicrophoneNoiseLevel, "Noise level must be >= 0.");
        if (!double.IsFinite(ClockPpm) || Math.Abs(ClockPpm) > 500.0)
            throw new ArgumentOutOfRangeException(nameof(ClockPpm), ClockPpm, "Clock error must be finite and within ±500 ppm.");
        if (!double.IsFinite(MicrophoneDeviationDb) || Math.Abs(MicrophoneDeviationDb) > 6.0)
            throw new ArgumentOutOfRangeException(nameof(MicrophoneDeviationDb), MicrophoneDeviationDb, "Microphone deviation must be finite and within ±6 dB.");
        if (MicrophoneProfile == MicrophoneResponseProfile.Perfect && MicrophoneDeviationDb != 0.0)
            throw new ArgumentOutOfRangeException(nameof(MicrophoneDeviationDb), MicrophoneDeviationDb,
                "A perfect microphone is flat, so it cannot carry a deviation: pick a tilt profile or leave the deviation at 0.");
        BackendSettings.Validate();
    }
}
