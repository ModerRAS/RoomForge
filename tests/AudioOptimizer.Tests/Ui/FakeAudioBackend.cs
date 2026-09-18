namespace AudioOptimizer.Tests.Ui;

using AudioOptimizer.Audio;

/// <summary>
/// A test double for the device layer, so the flow's success, rejection, device-failure, abort and
/// responsiveness paths can be exercised with no hardware and no timing. It exists only in the test project: the
/// shipped implementation is still the single <c>WasapiAudioBackend</c>, and nothing in production gained an
/// abstraction to make this possible.
/// <para>
/// The recording it returns is synthesized from the sweep it was asked to play — a perfect rig — or digital
/// silence, which is what a muted microphone looks like and yields named quality reasons deterministically.
/// </para>
/// </summary>
internal sealed class FakeAudioBackend : IAudioBackend
{
    /// <summary>Set as soon as a capture is entered, so a test can wait for the work to genuinely start.</summary>
    public TaskCompletionSource Started { get; private set; } = NewSource();

    /// <summary>
    /// Arms a fresh start signal for the next capture. Call before invoking a measurement: the signal is one-shot,
    /// so without arming, a wait for "this capture has started" is satisfied by an earlier capture and the test can
    /// race ahead of the work it means to interleave with (measured: that race made an abort land before the
    /// runner's delegate was scheduled, which is a different code path).
    /// </summary>
    public void ArmCaptureStart() => Started = NewSource();

    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>When set, <c>PlayAndRecord</c> blocks here until the test releases it: the deterministic gate.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public AudioDeviceLists Devices { get; set; } = new(
        [new AudioDeviceInfo("in-1", "UMIK-1")],
        [new AudioDeviceInfo("out-1", "DAC")]);

    public bool ThrowOnEnumerate { get; set; }

    public bool ReturnSilence { get; set; }

    public AudioOpenFailureKind? FailWith { get; set; }

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> once the capture is released. This models the one failure mode
    /// the fake cannot otherwise reach: the device layer torn down by another thread while a capture is in flight,
    /// which is what the real backend would plausibly do if it were ever disposed concurrently.
    /// </summary>
    public bool ThrowObjectDisposedOnRelease { get; set; }

    /// <summary>True while a capture is in progress — the flag that proves an abort did not leave the device open.</summary>
    public bool CaptureOpen { get; private set; }

    public int Opens { get; private set; }

    public int WorkerThreadId { get; private set; }

    public bool Disposed { get; private set; }

    public string Name => "fake";

    public AudioDeviceLists EnumerateDevices()
        => ThrowOnEnumerate ? throw new InvalidOperationException("the audio service is not running") : Devices;

    public double[] PlayAndRecord(
        AudioDeviceInfo renderDevice,
        AudioDeviceInfo captureDevice,
        AudioBackendSettings settings,
        double[] monoSweep,
        TimeSpan preRoll,
        TimeSpan postRoll,
        AudioCaptureMode captureMode = AudioCaptureMode.Device,
        Action<AudioEngineInfo>? onEngineReady = null)
    {
        Opens++;
        WorkerThreadId = Environment.CurrentManagedThreadId;
        CaptureOpen = true;
        Started.TrySetResult();
        try
        {
            Gate?.Task.Wait();
            if (ThrowObjectDisposedOnRelease) throw new ObjectDisposedException("fake capture device");
            if (FailWith is { } kind)
                throw new AudioDeviceOpenException($"fake failure: {kind}", kind, renderDevice.Name);

            int pre = (int)Math.Round(preRoll.TotalSeconds * settings.SampleRate);
            int post = (int)Math.Round(postRoll.TotalSeconds * settings.SampleRate);
            var recording = new double[pre + monoSweep.Length + post];
            if (!ReturnSilence)
            {
                monoSweep.CopyTo(recording, pre);
                // A noise floor, because a real capture always has one and because digital silence is
                // measurably not the same thing: with exact zeros the dropout check correctly flags the
                // 64-sample overshoot of the window-aligned onset (measured: issues [DropoutDetected] at
                // noise 0, [] at 1e-6). Digital silence is what ReturnSilence is for — a muted input.
                uint state = 12345;                       // deterministic LCG: fixed seed, no allocation
                for (int index = 0; index < recording.Length; index++)
                {
                    state = (state * 1664525u) + 1013904223u;
                    recording[index] += 1e-6 * (((state >> 8) / (double)(1 << 23)) - 1.0);
                }
            }

            return recording;
        }
        finally
        {
            CaptureOpen = false;
        }
    }

    public void Dispose() => Disposed = true;
}
