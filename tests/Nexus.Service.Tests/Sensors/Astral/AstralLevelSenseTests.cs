using Nexus.Service.Sensors.Astral;

namespace Nexus.Service.Tests.Sensors.Astral;

public class AstralLevelSenseTests
{
    private static AstralGSensorParser.Angles Sample(float x, float y) => new(x, y, 90f);

    private static void AddTimes(AstralLevelSense levelSense, AstralGSensorParser.Angles sample, int count)
    {
        for (var i = 0; i < count; i++)
        {
            levelSense.Add(sample);
        }
    }

    [Fact]
    public void Add_seeds_the_filter_with_the_first_reading_then_averages_each_new_one_in()
    {
        var levelSense = new AstralLevelSense();

        levelSense.Add(Sample(10f, 2f));
        Assert.Equal(Sample(10f, 2f), levelSense.Smoothed);

        levelSense.Add(Sample(20f, 4f));
        Assert.Equal(Sample(15f, 3f), levelSense.Smoothed);
    }

    [Fact]
    public void Calibration_completes_on_the_fifth_reading()
    {
        var levelSense = new AstralLevelSense();
        levelSense.StartCalibration();

        for (var i = 1; i < AstralLevelSense.CalibrationSamples; i++)
        {
            Assert.Null(levelSense.Add(Sample(88.5f, 1.2f)));
            Assert.Null(levelSense.Deviation);
        }

        var calibration = levelSense.Add(Sample(88.5f, 1.2f));

        Assert.Equal(new AstralLevelSense.Calibration(88.5f, 1.2f), calibration);
        Assert.False(levelSense.IsCalibrating);
        Assert.Equal((0f, 0f), levelSense.Deviation!.Value);
    }

    [Fact]
    public void Deviation_is_null_without_a_calibration()
    {
        var levelSense = new AstralLevelSense();
        levelSense.Add(Sample(88.5f, 1.2f));

        Assert.Null(levelSense.Deviation);
    }

    [Fact]
    public void Deviation_measures_against_a_loaded_calibration()
    {
        var levelSense = new AstralLevelSense();
        levelSense.Load(new AstralLevelSense.Calibration(88.5f, 1.2f));

        levelSense.Add(Sample(88f, 1.7f));

        var deviation = levelSense.Deviation;
        Assert.NotNull(deviation);
        Assert.Equal(-0.5f, deviation.Value.X, 0.0001f);
        Assert.Equal(0.5f, deviation.Value.Y, 0.0001f);
    }

    [Fact]
    public void Deviation_compares_magnitudes_when_a_reading_crosses_zero()
    {
        // GPU Tweak: same sign -> current - calibrated, opposite signs -> current + calibrated.
        var levelSense = new AstralLevelSense();
        levelSense.Load(new AstralLevelSense.Calibration(1.2f, -0.5f));

        levelSense.Add(Sample(-1f, -0.8f));

        var deviation = levelSense.Deviation;
        Assert.NotNull(deviation);
        Assert.Equal(0.2f, deviation.Value.X, 0.0001f);
        Assert.Equal(-0.3f, deviation.Value.Y, 0.0001f);
    }

    [Fact]
    public void Recalibration_keeps_measuring_against_the_previous_level_until_it_completes()
    {
        var levelSense = new AstralLevelSense();
        levelSense.Load(new AstralLevelSense.Calibration(0f, 0f));
        levelSense.Add(Sample(0f, 2f));

        levelSense.StartCalibration();
        AddTimes(levelSense, Sample(0f, 2f), AstralLevelSense.CalibrationSamples - 1);
        Assert.True(levelSense.IsCalibrating);
        Assert.Equal(2f, levelSense.Deviation!.Value.Y, 0.0001f);

        levelSense.Add(Sample(0f, 2f));
        Assert.False(levelSense.IsCalibrating);
        Assert.Equal(0f, levelSense.Deviation!.Value.Y, 0.0001f);
    }

    [Fact]
    public void Mode_picks_the_most_frequent_value()
    {
        Assert.Equal(2f, AstralLevelSense.Mode(new[] { 1f, 2f, 2f, 3f, 2f }));
    }

    [Fact]
    public void Mode_breaks_a_tie_toward_the_smaller_value()
    {
        Assert.Equal(1f, AstralLevelSense.Mode(new[] { 3f, 3f, 1f, 1f, 5f }));
    }

    [Fact]
    public void Mode_of_all_distinct_values_is_the_smallest()
    {
        Assert.Equal(3f, AstralLevelSense.Mode(new[] { 5f, 4f, 3f, 6f, 7f }));
    }

    [Fact]
    public void Mode_counts_values_within_the_tolerance_as_equal()
    {
        Assert.Equal(1f, AstralLevelSense.Mode(new[] { 1f, 1.000005f, 2f }));
    }
}
