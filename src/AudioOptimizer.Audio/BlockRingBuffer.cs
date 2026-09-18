namespace AudioOptimizer.Audio;

/// <summary>
/// Bounded accumulator for capture callbacks: keeps the newest <see cref="Capacity"/> samples and counts
/// everything it was handed, so the smoke test can tell "the device went quiet" from "the device flooded us".
/// </summary>
public sealed class BlockRingBuffer
{
    // ponytail: Queue-backed, one enqueue/dequeue per sample. A capture callback is not hot enough to
    // matter; swap for a double[] with head/tail indices if this ever shows up in a profile.
    private readonly Queue<double> samples = new();

    public BlockRingBuffer(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be >= 1 sample.");
        Capacity = capacity;
    }

    public int Capacity { get; }

    /// <summary>Samples currently held (never more than <see cref="Capacity"/>).</summary>
    public int Count => samples.Count;

    public bool IsFull => samples.Count >= Capacity;

    /// <summary>Total samples ever handed to <see cref="Write"/>.</summary>
    public long TotalWritten { get; private set; }

    /// <summary>Oldest samples dropped because the buffer was full — always TotalWritten − Count − cleared.</summary>
    public long TotalDropped { get; private set; }

    public void Write(ReadOnlySpan<double> block)
    {
        foreach (double sample in block)
        {
            if (samples.Count == Capacity)
            {
                samples.Dequeue();
                TotalDropped++;
            }
            samples.Enqueue(sample);
            TotalWritten++;
        }
    }

    /// <summary>Oldest → newest snapshot, so deconvolution sees the recording in real time order.</summary>
    public double[] ToArray() => samples.ToArray();

    public void Clear() => samples.Clear();
}
