using Lugworm.Core.Duet;
using Lugworm.Core.Geometry;
using Xunit;

namespace Lugworm.Core.Tests;

public class DeviationColorScaleTests
{
    [Fact]
    public void Endpoints_are_viridis_purple_and_yellow()
    {
        Assert.Equal(((byte)68, (byte)1, (byte)84), DeviationColorScale.Map(0));
        Assert.Equal(((byte)253, (byte)231, (byte)37), DeviationColorScale.Map(1));
        Assert.Equal(DeviationColorScale.Map(0), DeviationColorScale.ForDistance(0, 5));
        Assert.Equal(DeviationColorScale.Map(1), DeviationColorScale.ForDistance(5, 5));
    }

    [Fact]
    public void Above_range_saturates_and_never_rescales()
    {
        var top = DeviationColorScale.Map(1);
        Assert.Equal(top, DeviationColorScale.ForDistance(50, 5));
        Assert.Equal(top, DeviationColorScale.ForDistance(double.MaxValue, 5));
        Assert.True(DeviationColorScale.Saturated(5, 5));
        Assert.True(DeviationColorScale.Saturated(50, 5));
        Assert.False(DeviationColorScale.Saturated(4.999, 5));
        // the same distance keeps the same colour whatever else was observed
        Assert.Equal(DeviationColorScale.ForDistance(2.5, 5), DeviationColorScale.ForDistance(2.5, 5));
        Assert.Equal(DeviationColorScale.Map(0.5), DeviationColorScale.ForDistance(2.5, 5));
    }

    [Fact]
    public void Unavailable_is_grey_not_a_hue_and_bad_inputs_clamp()
    {
        Assert.Equal(DeviationColorScale.Unavailable, DeviationColorScale.ForDistance(double.NaN, 5));
        Assert.Equal(DeviationColorScale.Unavailable, DeviationColorScale.ForDistance(1, 0));
        Assert.Equal(DeviationColorScale.Unavailable, DeviationColorScale.ForDistance(1, double.NaN));
        Assert.False(DeviationColorScale.Saturated(double.NaN, 5));
        Assert.Equal(DeviationColorScale.Map(0), DeviationColorScale.ForDistance(-3, 5));
        Assert.Equal(DeviationColorScale.Map(0), DeviationColorScale.Map(double.NaN));
        Assert.Equal(DeviationColorScale.Map(1), DeviationColorScale.Map(7));
    }

    [Fact]
    public void Scale_is_monotone_in_luminance_direction()
    {
        // Viridis brightens monotonically: each step's green channel never decreases.
        byte last = 0;
        for (int i = 0; i <= 20; i++)
        {
            var c = DeviationColorScale.Map(i / 20.0);
            Assert.True(c.G >= last, $"green fell at t={i / 20.0}");
            last = c.G;
        }
    }

    [Fact]
    public void Deviation_is_invariant_under_the_machine_frame_reflection()
    {
        // The component converts G-code (machine frame) and telemetry (machine frame)
        // consistently; the reflection about y = -x is an isometry, so the distance the
        // index reports must not depend on which frame the comparison is made in.
        var machine = new[] { new Vec3(100, -400, 5), new Vec3(300, -400, 5), new Vec3(300, -200, 8) };
        var rhino = new Vec3[machine.Length];
        for (int i = 0; i < machine.Length; i++) rhino[i] = MachineFrame.ToRhino(machine[i]);
        var q = new Vec3(200, -403, 5);
        var a = LivePathDeviation.FromPolyline(machine).Query(q);
        var b = LivePathDeviation.FromPolyline(rhino).Query(MachineFrame.ToRhino(q));
        Assert.True(a.Available && b.Available);
        Assert.Equal(3, a.DistanceMm, 9);
        Assert.Equal(a.DistanceMm, b.DistanceMm, 9);
        Assert.Equal(a.SegmentIndex, b.SegmentIndex);
    }
}
