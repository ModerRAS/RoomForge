namespace AudioOptimizer.Audio;

/// <summary>Sample format of an interleaved device buffer. 32-bit float is what WASAPI shared mode hands over.</summary>
public enum AudioSampleFormat
{
    Float32,
    Int16,
    Int24,
}
