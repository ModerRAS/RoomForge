namespace AudioOptimizer.IO;

/// <summary>
/// The conditions one measurement was captured under, as they are written to the project file. Devices are
/// stored by NAME, not by index: an endpoint index is only meaningful on the machine that produced it and
/// silently points somewhere else on the next reboot. The optimizer's recommendation is only comparable to a
/// measurement taken with the same gain/polarity/phase setting, so those are recorded per measurement rather
/// than assumed.
/// </summary>
/// <param name="PlaybackGain">Linear gain applied to the sweep on playback (1.0 = full scale).</param>
/// <param name="Polarity">+1 or −1, the sub's polarity during the capture.</param>
public sealed record MeasurementCapture(
    string InputDevice = "",
    string OutputDevice = "",
    double PlaybackGain = 1.0,
    int Polarity = 1,
    double PhaseDegrees = 0.0,
    double DelayMilliseconds = 0.0);
