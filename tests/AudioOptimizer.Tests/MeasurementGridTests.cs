namespace AudioOptimizer.Tests;

using AudioOptimizer.Core;
using Xunit.Abstractions;

/// <summary>
/// The grid is nx·ny·nz points over the physical extents — no constant 27 anywhere. The two cases that matter
/// are the classic 3×3×3 box and a 3×3×2 box, whose z axis only visits the two ends (18 points).
/// </summary>
public sealed class MeasurementGridTests(ITestOutputHelper output)
{
    [Fact]
    public void A_3x3x3_grid_over_1_8_by_1_0_by_0_6_metres_has_27_points_at_the_expected_coordinates()
    {
        MeasurementGrid grid = MeasurementGrid.Create(1.8, 1.0, 0.6);

        // Level −1/0/+1 → (level + 1)/2 · extent, so X = {0, 0.9, 1.8}, Y = {0, 0.5, 1.0}, Z = {0, 0.3, 0.6}.
        // Count = nx·ny·nz = 3·3·3 = 27.
        Assert.Equal(27, grid.PointCount);
        Assert.Equal(27, grid.Points.Count);
        Assert.Equal(new[] { 0.0, 0.9, 1.8 }, grid.Points.Select(p => p.X).Distinct().Order());
        Assert.Equal(new[] { 0.0, 0.5, 1.0 }, grid.Points.Select(p => p.Y).Distinct().Order());
        Assert.Equal(new[] { 0.0, 0.3, 0.6 }, grid.Points.Select(p => p.Z).Distinct().Order());

        // Spot values, from the same rule:
        MeasurementPoint corner = grid.Points.Single(p => p.Id == "x-1_y-1_z-1");
        Assert.Equal((0.0, 0.0, 0.0), (corner.X, corner.Y, corner.Z));          // (−1+1)/2·1.8 = 0 m
        MeasurementPoint centre = grid.Points.Single(p => p.Id == "x0_y0_z0");
        Assert.Equal((0.9, 0.5, 0.3), (centre.X, centre.Y, centre.Z));          // the middle of each extent
        MeasurementPoint far = grid.Points.Single(p => p.Id == "x1_y1_z1");
        Assert.Equal((1.8, 1.0, 0.6), (far.X, far.Y, far.Z));                   // (+1+1)/2·1.8 = 1.8 m

        Assert.Equal(27, grid.Points.Select(p => p.Id).Distinct().Count());
        output.WriteLine(string.Join(" | ", grid.Points.Select(p => $"{p.Id}=({p.X:F2},{p.Y:F2},{p.Z:F2})")));
    }

    [Fact]
    public void A_3x3x2_grid_has_18_points_because_the_count_is_nx_times_ny_times_nz()
    {
        MeasurementGrid grid = MeasurementGrid.Create(1.8, 1.0, 0.6, nx: 3, ny: 3, nz: 2);

        // Count = 3·3·2 = 18, and LevelsFor(2) = {−1, +1}, so Z ∈ {0, 0.6} — the middle plane is simply absent.
        Assert.Equal(18, grid.PointCount);
        Assert.Equal(new[] { 0.0, 0.6 }, grid.Points.Select(p => p.Z).Distinct().Order());
        Assert.Equal(new[] { -1, 1 }, grid.Points.Select(p => p.GridZ).Distinct().Order());
        Assert.DoesNotContain(grid.Points, p => Math.Abs(p.Z - 0.3) < 1e-12);
        output.WriteLine(string.Join(" | ", grid.Points.Take(6).Select(p => $"{p.Id}=({p.X:F2},{p.Y:F2},{p.Z:F2})")));
    }

    [Fact]
    public void Any_axis_count_produces_exactly_nx_times_ny_times_nz_points()
    {
        // The point count must follow the counts, not a remembered 27: 1·1·2 = 2 points at the box's z ends.
        MeasurementGrid thin = MeasurementGrid.Create(2.0, 2.0, 2.0, nx: 1, ny: 1, nz: 2);
        Assert.Equal(2, thin.PointCount);
        Assert.Equal(new[] { "x0_y0_z-1", "x0_y0_z1" }, thin.Points.Select(p => p.Id));

        // A single point per axis is the centre: (level + 1)/2 · extent = 0.5 · 2 = 1 m on every axis.
        MeasurementGrid single = MeasurementGrid.Create(2.0, 2.0, 2.0, nx: 1, ny: 1, nz: 1);
        Assert.Equal(1, single.PointCount);
        Assert.Equal((1.0, 1.0, 1.0), (single.Points[0].X, single.Points[0].Y, single.Points[0].Z));
    }

    [Fact]
    public void Levels_and_extents_are_validated()
    {
        // 4 points on one axis cannot be expressed with signed ±1 levels (documented ceiling), 0 is nonsense.
        Assert.Throws<ArgumentOutOfRangeException>(() => MeasurementGrid.Create(1, 1, 1, nx: 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeasurementGrid.Create(1, 1, 1, nz: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeasurementGrid.Create(-1.8, 1.0, 0.6));

        Assert.Equal(new[] { 0 }, MeasurementGrid.LevelsFor(1));       // one point: the centre
        Assert.Equal(new[] { -1, 1 }, MeasurementGrid.LevelsFor(2));    // two points: the ends
        Assert.Equal(new[] { -1, 0, 1 }, MeasurementGrid.LevelsFor(3)); // three: the ends and the centre
    }

    [Fact]
    public void A_session_is_three_modes_over_one_grid()
    {
        // 3 modes × 27 points = 81 slots (the reason a session needs resume at all).
        MeasurementGrid grid = MeasurementGrid.Create(1.8, 1.0, 0.6);
        Assert.Equal(81, Enum.GetValues<SubMode>().Length * grid.PointCount);
        Assert.Equal(new[] { SubMode.A, SubMode.B, SubMode.AB }, Enum.GetValues<SubMode>());
    }
}
