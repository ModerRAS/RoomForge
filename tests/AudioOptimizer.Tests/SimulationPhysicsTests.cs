namespace AudioOptimizer.Tests;

using AudioOptimizer.Core;
using AudioOptimizer.Dsp;
using AudioOptimizer.Simulation;
using Xunit.Abstractions;

/// <summary>
/// The simulator's own physics, checked against closed forms rather than against itself. Every response here is taken
/// straight from the image-source renderer — no sweep, no deconvolution — so a failure means the acoustics is wrong
/// and not that the measurement chain is in the way. The chain's own end-to-end check lives in
/// <see cref="SimulationPipelineTests"/>.
/// </summary>
public class SimulationPhysicsTests(ITestOutputHelper output)
{
    private const int SampleRate = 48000;

    /// <summary>A sub with no settings: the room's own response, which is what these tests are about.</summary>
    private static VirtualSubwoofer Sub(Position position) => new(position);

    private static readonly Position MiddleOfTheRoom = new(1.65, 1.80, 1.20);

    /// <summary>The total amplitude of a rendered response — placement-preserving, so it survives fractional delays.</summary>
    private static double TotalAmplitude(double[] impulseResponse) => impulseResponse.Sum();

    /// <summary>Amplitude-weighted mean sample index: the arrival time, fractional and all.</summary>
    private static double CentroidSamples(double[] impulseResponse)
    {
        double weighted = 0.0, total = 0.0;
        for (int i = 0; i < impulseResponse.Length; i++)
        {
            double magnitude = Math.Abs(impulseResponse[i]);
            weighted += i * magnitude;
            total += magnitude;
        }
        return total == 0 ? double.NaN : weighted / total;
    }

    private static FrequencyResponse[] Response(double[] impulseResponse, int fftSize = 131072)
        => FrequencyResponseCalculator.Compute(new ImpulseResponse(impulseResponse, SampleRate), WindowType.Rectangular, fftSize);

    private static FrequencyResponse BinAt(IReadOnlyList<FrequencyResponse> response, double frequencyHz)
        => response.OrderBy(bin => Math.Abs(bin.FrequencyHz - frequencyHz)).First();

    private static double LevelAt(IReadOnlyList<FrequencyResponse> response, double frequencyHz)
        => BinAt(response, frequencyHz).MagnitudeDb;

    [Fact]
    public void Direct_path_amplitude_is_the_reference_distance_over_the_distance()
    {
        RoomModel room = RoomModel.Default;
        VirtualSubwoofer sub = Sub(new Position(0.0, 0.0, 0.0));

        double[] atOneMetre = sub.ImpulseResponse(room, new Position(1.0, 0.0, 0.0), imageSourceOrder: 0, SampleRate);
        double[] atTwoMetres = sub.ImpulseResponse(room, new Position(2.0, 0.0, 0.0), imageSourceOrder: 0, SampleRate);
        double[] atThreeAndAHalf = sub.ImpulseResponse(room, new Position(3.5, 0.0, 0.0), imageSourceOrder: 0, SampleRate);

        output.WriteLine($"total amplitude: 1 m {TotalAmplitude(atOneMetre):R}, 2 m {TotalAmplitude(atTwoMetres):R}, "
            + $"3.5 m {TotalAmplitude(atThreeAndAHalf):R}");

        Assert.Equal(1.0, TotalAmplitude(atOneMetre), 12);
        Assert.Equal(0.5, TotalAmplitude(atTwoMetres), 12);
        Assert.Equal(1.0 / 3.5, TotalAmplitude(atThreeAndAHalf), 12);
    }

    [Fact]
    public void Propagation_delay_is_distance_over_the_speed_of_sound()
    {
        RoomModel room = RoomModel.Default;
        VirtualSubwoofer sub = Sub(new Position(0.30, 0.40, 0.35));

        double[] response = sub.ImpulseResponse(room, MiddleOfTheRoom, imageSourceOrder: 0, SampleRate);
        double distance = sub.Position.DistanceTo(MiddleOfTheRoom);
        double expectedSamples = distance / room.SpeedOfSound * SampleRate;

        output.WriteLine($"d = {distance:R} m → {expectedSamples:R} samples; measured centroid {CentroidSamples(response):R}");

        // The centroid is the exact arrival time even when it falls between two samples, which is the point of the
        // fractional placement: it is the one arrival-time measure that does not depend on the placement rule.
        Assert.Equal(expectedSamples, CentroidSamples(response), 9);
        Assert.Equal(distance / room.SpeedOfSound, expectedSamples / SampleRate, 12);
    }

    [Fact]
    public void A_single_path_has_phase_minus_two_pi_f_tau()
    {
        RoomModel room = RoomModel.Default;
        VirtualSubwoofer sub = Sub(new Position(0.30, 0.40, 0.35));
        double tau = sub.Position.DistanceTo(MiddleOfTheRoom) / room.SpeedOfSound;

        FrequencyResponse[] response = Response(sub.ImpulseResponse(room, MiddleOfTheRoom, imageSourceOrder: 0, SampleRate));
        double maxError = 0.0;
        foreach (FrequencyResponse bin in response.Where(bin => bin.FrequencyHz is >= 30.0 and <= 150.0))
        {
            double expected = Math.IEEERemainder(-Math.Tau * bin.FrequencyHz * tau, Math.Tau);
            maxError = Math.Max(maxError, Math.Abs(Math.IEEERemainder(bin.PhaseWrappedRad - expected, Math.Tau)));
        }

        output.WriteLine($"tau = {tau:R} s; max phase error over 30–150 Hz = {maxError:E3} rad "
            + $"(bin spacing {SampleRate / 131072.0:F4} Hz)");

        // The two-tap fractional delay is linear-phase to within O(ω³): at 150 Hz that is ~1e-6 rad.
        Assert.True(maxError < 1e-4, $"max phase error was {maxError:E3} rad");
    }

    [Fact]
    public void Wall_hit_counts_match_the_mirror_lattice()
    {
        // The mirror lattice places copy ±1 through the far and near walls, and every further copy alternating.
        Assert.Equal((0, 0), ImageSourceMethod.WallHits(0));
        Assert.Equal((0, 1), ImageSourceMethod.WallHits(1));
        Assert.Equal((1, 0), ImageSourceMethod.WallHits(-1));
        Assert.Equal((1, 1), ImageSourceMethod.WallHits(2));
        Assert.Equal((1, 2), ImageSourceMethod.WallHits(3));
        Assert.Equal((2, 1), ImageSourceMethod.WallHits(-3));

        RoomModel room = RoomModel.Default;
        Assert.Equal(5.6, ImageSourceMethod.Coordinate(1, room.LengthMetres, 1.0), 12);      // 2·3.3 − 1.0
        Assert.Equal(-1.0, ImageSourceMethod.Coordinate(-1, room.LengthMetres, 1.0), 12);
        Assert.Equal(7.6, ImageSourceMethod.Coordinate(2, room.LengthMetres, 1.0), 12);      // 2·3.3 + 1.0
    }

    [Fact]
    public void Reflection_arrivals_land_at_the_mirror_distances_with_their_reflection_product()
    {
        RoomModel room = RoomModel.Default;
        VirtualSubwoofer sub = Sub(new Position(0.50, 0.50, 0.50));
        var microphone = new Position(1.00, 1.00, 1.00);

        IReadOnlyList<Arrival> arrivals = ImageSourceMethod.Arrivals(room, sub.Position, microphone, order: 2);

        output.WriteLine($"{arrivals.Count} arrivals at order 2; reflections {room.Reflections}");
        foreach (Arrival arrival in arrivals.Take(3))
            output.WriteLine($"  {arrival.DelaySeconds * 1000.0:F4} ms, amplitude {arrival.Amplitude:F6}");

        // "Order N" is N per axis, so the lattice is (2N+1)³ = 125 points at order 2 — which is what makes a
        // two-wall reflection reachable at all ("order 1" is one reflection PER AXIS, not one in total).
        Assert.Equal(125, arrivals.Count);

        // The direct sound: distance 0.8660254 m, amplitude 1/d with the 1 m reference.
        double directDistance = sub.Position.DistanceTo(microphone);
        Arrival direct = arrivals.Single(arrival => Math.Abs(arrival.DelaySeconds - (directDistance / room.SpeedOfSound)) < 1e-12);
        Assert.Equal(1.0 / directDistance, direct.Amplitude, 12);

        // The first-order reflection through the far x wall: image at (2·3.3 − 0.5, 0.5, 0.5), one reflection at 0.7.
        var image = new Position((2 * room.LengthMetres) - 0.5, 0.5, 0.5);
        double imageDistance = image.DistanceTo(microphone);
        Arrival reflection = arrivals.Single(arrival => Math.Abs(arrival.DelaySeconds - (imageDistance / room.SpeedOfSound)) < 1e-12);
        Assert.Equal(room.Reflections.Right / imageDistance, reflection.Amplitude, 12);

        // A second-order image that went out through one x wall and back through the other carries each coefficient
        // once: index 2 crosses the far wall and then the near one, so its amplitude has Right·Left in it.
        var secondOrder = new Position(ImageSourceMethod.Coordinate(2, room.LengthMetres, 0.5), 0.5, 0.5);
        double secondOrderDistance = secondOrder.DistanceTo(microphone);
        Arrival twice = arrivals.Single(arrival => Math.Abs(arrival.DelaySeconds - (secondOrderDistance / room.SpeedOfSound)) < 1e-12);
        Assert.Equal(room.Reflections.Right * room.Reflections.Left / secondOrderDistance, twice.Amplitude, 12);

        // And the rendered response keeps every one of those arrivals: same total amplitude, later centroid.
        double[] rendered = sub.ImpulseResponse(room, microphone, imageSourceOrder: 2, SampleRate);
        Assert.Equal(arrivals.Sum(arrival => arrival.Amplitude), TotalAmplitude(rendered), 12);
        Assert.True(CentroidSamples(rendered) > CentroidSamples(sub.ImpulseResponse(room, microphone, imageSourceOrder: 1, SampleRate)));
    }

    /// <summary>
    /// The room's modal behaviour is nowhere written down in this project — the image sources sum into it. What the
    /// analytic formula predicts is used here, in the test, as the reference: the axial mode along the width sits at
    /// c/2W, and at that frequency the standing wave has its node on the room's centre plane and its antinodes on the
    /// two walls.
    /// <para>
    /// The frequency is checked in a reverberant room (reflections at 0.97), because a mode is only resolvable when its
    /// bandwidth is narrower than the distance to the next one: at the default 0.7 the walls damp the room to roughly
    /// T60 ≈ 0.14 s, which is a 15 Hz mode — wider than the 4.3 Hz that separates its two lowest axial neighbours, and
    /// indeed no discrete peak is measurable there. The SHAPE is checked at the default damping, where it survives.
    /// </para>
    /// </summary>
    [Fact]
    public void Axial_room_modes_emerge_from_the_image_source_sum()
    {
        const int Order = 20;
        RoomModel room = RoomModel.Default;
        double widthMode = room.SpeedOfSound / (2.0 * room.WidthMetres);
        VirtualSubwoofer sub = Sub(new Position(0.35, 0.35, 0.30));

        // The antinode mics are 1 m either side of the node plane at y = Width/2; comparing them to the node plane is
        // the mode shape, measured.
        FrequencyResponse[] antinodeLow = Response(sub.ImpulseResponse(room, new Position(1.65, 0.80, 1.20), Order, SampleRate));
        FrequencyResponse[] centre = Response(sub.ImpulseResponse(room, new Position(1.65, 1.80, 1.20), Order, SampleRate));
        FrequencyResponse[] antinodeHigh = Response(sub.ImpulseResponse(room, new Position(1.65, 2.80, 1.20), Order, SampleRate));

        output.WriteLine($"width axial mode c/2W = {widthMode:F3} Hz at the default reflections: node plane "
            + $"{LevelAt(centre, widthMode):F2} dB, antinodes {LevelAt(antinodeLow, widthMode):F2} and "
            + $"{LevelAt(antinodeHigh, widthMode):F2} dB");

        double dip = Math.Min(LevelAt(antinodeLow, widthMode), LevelAt(antinodeHigh, widthMode)) - LevelAt(centre, widthMode);
        Assert.True(dip > 6.0, $"the node plane was only {dip:F2} dB below the antinodes at {widthMode:F2} Hz");
        Assert.True(Math.Abs(LevelAt(antinodeLow, widthMode) - LevelAt(antinodeHigh, widthMode)) < 1.0,
            "the two antinode positions should see the same level at an axial mode frequency");

        // The same standing wave in a reverberant room, where the resonance is sharp enough to be located in
        // frequency: the level difference between the antinode plane and the node plane must peak at c/2W. Measured
        // at 46.88 Hz for c/2W = 47.64 Hz; the residual is the neighbouring axial mode 4.3 Hz away, which a 1 m
        // antinode offset cannot fully separate.
        RoomModel reverberant = room with
        {
            Reflections = new SurfaceReflections(0.97, 0.97, 0.97, 0.97, 0.5, 0.5),
        };
        FrequencyResponse[] sharpLow = Response(sub.ImpulseResponse(reverberant, new Position(1.65, 0.80, 1.20), Order, SampleRate), 1 << 17);
        FrequencyResponse[] sharpCentre = Response(sub.ImpulseResponse(reverberant, new Position(1.65, 1.80, 1.20), Order, SampleRate), 1 << 17);
        FrequencyResponse[] sharpHigh = Response(sub.ImpulseResponse(reverberant, new Position(1.65, 2.80, 1.20), Order, SampleRate), 1 << 17);

        double bestContrast = double.NegativeInfinity, bestHz = 0.0;
        for (int k = 0; k < sharpCentre.Length; k++)
        {
            double frequency = sharpCentre[k].FrequencyHz;
            if (frequency is < 35.0 or > 62.0) continue;
            double contrast = ((sharpLow[k].MagnitudeDb + sharpHigh[k].MagnitudeDb) / 2.0) - sharpCentre[k].MagnitudeDb;
            if (contrast > bestContrast) { bestContrast = contrast; bestHz = frequency; }
        }

        output.WriteLine($"reverberant room: node/antinode contrast peaks at {bestHz:F3} Hz ({bestContrast:F2} dB), "
            + $"analytic c/2W = {widthMode:F3} Hz");

        Assert.True(bestContrast > 10.0, $"the mode contrast only reached {bestContrast:F2} dB");
        Assert.True(Math.Abs(bestHz - widthMode) < 1.5, $"the mode contrast peaked at {bestHz:F3} Hz, not near {widthMode:F3} Hz");
    }

    [Fact]
    public void A_different_sub_position_and_a_different_microphone_position_change_the_response()
    {
        RoomModel room = RoomModel.Default;
        double[] front = Sub(new Position(0.35, 0.35, 0.30)).ImpulseResponse(room, MiddleOfTheRoom, 3, SampleRate);
        double[] mirrored = Sub(new Position(2.95, 3.25, 0.30)).ImpulseResponse(room, MiddleOfTheRoom, 3, SampleRate);
        double[] besideIt = Sub(new Position(1.00, 1.00, 1.00)).ImpulseResponse(room, MiddleOfTheRoom, 3, SampleRate);
        double[] elsewhere = Sub(new Position(0.35, 0.35, 0.30)).ImpulseResponse(room, new Position(2.85, 0.80, 0.90), 3, SampleRate);

        double mirroredDifference = front.Zip(mirrored).Max(pair => Math.Abs(pair.First - pair.Second));
        double movedDifference = front.Zip(besideIt).Max(pair => Math.Abs(pair.First - pair.Second));
        double microphoneDifference = front.Zip(elsewhere).Max(pair => Math.Abs(pair.First - pair.Second));
        output.WriteLine($"max |front − mirrored| = {mirroredDifference:E3}, |front − moved within the same room| = {movedDifference:E3}, "
            + $"|front − other microphone| = {microphoneDifference:E3}; centroids "
            + $"{CentroidSamples(front):F1} / {CentroidSamples(mirrored):F1} / {CentroidSamples(besideIt):F1} / {CentroidSamples(elsewhere):F1}");

        // The mirrored position is a symmetry of a shoebox whose opposite walls have equal reflection coefficients, so
        // it must produce the SAME response — a property of the construction, and the test that would catch a
        // coefficient applied on the wrong wall. The moved position is not a mirror image of the first, so it must not.
        Assert.Equal(0.0, mirroredDifference, 12);
        Assert.Equal(CentroidSamples(front), CentroidSamples(mirrored), 6);

        // Moving the sub anywhere else, or the microphone, must not.
        Assert.True(movedDifference > 1e-3, $"two different sub positions produced the same response ({movedDifference:E3})");
        Assert.True(microphoneDifference > 1e-3, $"two different microphone positions produced the same response ({microphoneDifference:E3})");
    }

    [Fact]
    public void The_listening_region_is_27_points_at_a_quarter_a_half_and_three_quarters()
    {
        ListeningRegion region = ListeningRegion.Default;
        IReadOnlyList<MeasurementPoint> points = region.Points;

        Assert.Equal(27, points.Count);
        Assert.Equal(27, points.Select(point => point.Id).Distinct().Count());

        foreach (MeasurementPoint point in points)
        {
            double expectedX = region.CentreX + ((((point.GridX + 1) * 0.5) - 0.5) * region.WidthMetres * 0.5);
            double expectedZ = region.CentreZ + ((((point.GridZ + 1) * 0.5) - 0.5) * region.HeightMetres * 0.5);
            Assert.Equal(expectedX, point.X, 12);
            Assert.Equal(expectedZ, point.Z, 12);
        }

        // The coordinates follow from the extents: halving the region halves every offset from its centre.
        ListeningRegion half = region with { WidthMetres = region.WidthMetres / 2.0 };
        foreach (MeasurementPoint point in half.Points)
        {
            MeasurementPoint full = points.Single(candidate => candidate.Id == point.Id);
            Assert.Equal(half.CentreX + ((full.X - region.CentreX) / 2.0), point.X, 12);
        }
    }

    [Fact]
    public void Fractional_placement_is_more_accurate_than_integer_placement()
    {
        RoomModel room = RoomModel.Default;
        VirtualSubwoofer sub = Sub(new Position(0.30, 0.40, 0.35));
        double exactSamples = sub.Position.DistanceTo(MiddleOfTheRoom) / room.SpeedOfSound * SampleRate;

        double[] fractional = sub.ImpulseResponse(room, MiddleOfTheRoom, 0, SampleRate, placement: ArrivalPlacement.LinearInterpolated);
        double[] integer = sub.ImpulseResponse(room, MiddleOfTheRoom, 0, SampleRate, placement: ArrivalPlacement.NearestSample);

        double fractionalError = Math.Abs(CentroidSamples(fractional) - exactSamples);
        double integerError = Math.Abs(CentroidSamples(integer) - exactSamples);
        output.WriteLine($"exact {exactSamples:R} samples: fractional placement is {fractionalError:E3} out, "
            + $"nearest-sample placement {integerError:E3} out");

        Assert.True(fractionalError < 1e-9, $"fractional placement was {fractionalError:E3} samples out");
        Assert.True(integerError is > 0.0 and <= 0.5, $"integer placement was {integerError:E3} samples out");
    }

    /// <summary>
    /// The rotation is applied to the drive signal, and the honest way to check it is where it is used: through the
    /// measurement chain, which reports the complex response. That is <c>SimulationPipelineTests</c>' business. What
    /// is checkable here is the one angle with a closed form — 180° is a sign flip — and that the two ways of saying
    /// "invert this sub" agree.
    /// </summary>
    [Fact]
    public void A_hundred_and_eighty_degrees_is_exactly_a_polarity_flip()
    {
        double[] signal = SweepGenerator.GenerateExponentialSweep(new SweepSettings());
        double[] rotated = PhaseRotation.Apply(signal, 180.0, out int offset);
        double maxError = 0.0;
        for (int i = 0; i < signal.Length; i++) maxError = Math.Max(maxError, Math.Abs(rotated[offset + i] + signal[i]));

        output.WriteLine($"180° against a sign flip: max difference {maxError:E3}");
        Assert.Equal(PhaseRotation.PadSamples, offset);
        Assert.True(maxError < 1e-12, $"180° was {maxError:E3} away from a sign flip");

        // ...which is also what the polarity setting does, so the two are the same physical move.
        RoomModel room = RoomModel.Default;
        double[] flipped = new VirtualSubwoofer(new Position(0.30, 0.40, 0.35), Polarity: -1).ImpulseResponse(room, MiddleOfTheRoom, 3, SampleRate);
        double[] plain = Sub(new Position(0.30, 0.40, 0.35)).ImpulseResponse(room, MiddleOfTheRoom, 3, SampleRate);
        Assert.Equal(0.0, flipped.Zip(plain).Max(pair => Math.Abs(pair.First + pair.Second)), 12);
    }

    [Theory]
    [InlineData(0.001)]
    [InlineData(0.002)]
    [InlineData(0.005)]
    public void The_delay_setting_shifts_the_time_and_the_phase_by_the_same_amount(double delaySeconds)
    {
        RoomModel room = RoomModel.Default;
        VirtualSubwoofer sub = Sub(new Position(0.30, 0.40, 0.35));

        double[] without = sub.ImpulseResponse(room, MiddleOfTheRoom, 3, SampleRate);
        double[] with = (sub with { DelaySeconds = delaySeconds }).ImpulseResponse(room, MiddleOfTheRoom, 3, SampleRate);

        double shiftSamples = CentroidSamples(with) - CentroidSamples(without);
        output.WriteLine($"{delaySeconds * 1000.0:F0} ms: centroid shift {shiftSamples:R} samples, expected {delaySeconds * SampleRate:R}");

        Assert.Equal(delaySeconds * SampleRate, shiftSamples, 6);

        FrequencyResponse[] before = Response(without);
        double maxPhaseError = 0.0;
        foreach (FrequencyResponse bin in Response(with).Where(bin => bin.FrequencyHz is >= 30.0 and <= 150.0))
        {
            double reference = Math.IEEERemainder(
                BinAt(before, bin.FrequencyHz).PhaseWrappedRad - (Math.Tau * bin.FrequencyHz * delaySeconds), Math.Tau);
            maxPhaseError = Math.Max(maxPhaseError, Math.Abs(Math.IEEERemainder(bin.PhaseWrappedRad - reference, Math.Tau)));
        }

        output.WriteLine($"  max phase error against −360·f·Δt = {maxPhaseError:E3} rad");
        Assert.True(maxPhaseError < 1e-4, $"delay phase error was {maxPhaseError:E3} rad");
    }
}
