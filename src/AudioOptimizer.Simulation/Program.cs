namespace AudioOptimizer.Simulation;

using AudioOptimizer.Core;

/// <summary>
/// The Virtual Acoustic Lab's console entry point. Until the scenarios land (next commit) this is the smallest useful
/// thing it can do: report the rig it would measure — the room, the listening region, and the direct path from the
/// default sub to the middle of it, from the same image-source renderer everything else uses.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        _ = args;
        SimulationConfig config = SimulationConfig.Default;
        VirtualSubwoofer sub = new(new Position(0.30, 0.40, 0.35));
        MeasurementPoint middle = config.ListeningRegion.Points.Single(point => point.Id == "x0_y0_z0");

        double[] response = sub.ImpulseResponse(
            config.Room, new Position(middle.X, middle.Y, middle.Z), config.ImageSourceOrder, config.SampleRate);

        Console.WriteLine($"room {config.Room.LengthMetres} × {config.Room.WidthMetres} × {config.Room.HeightMetres} m at "
            + $"{config.Room.SpeedOfSound} m/s, ISM order {config.ImageSourceOrder}");
        Console.WriteLine($"region {config.ListeningRegion.Points.Count} points, middle at ({middle.X}, {middle.Y}, {middle.Z}) m");
        Console.WriteLine($"sub at {sub.Position}: {response.Length} samples, total amplitude {response.Sum():F4}, "
            + $"peak at {Array.IndexOf(response, response.Max(Math.Abs))}");
        return 0;
    }
}
