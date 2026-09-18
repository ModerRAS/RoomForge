namespace AudioOptimizer.Core;

/// <summary>
/// Which speaker configuration a measurement belongs to. Each mode is a full grid pass, which is why a
/// session is 3 × the grid: A, B, and both together.
/// </summary>
public enum SubMode
{
    A,
    B,
    AB,
}
