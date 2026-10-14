using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class AxisScaleTests
{
  private static Chart Chart(string type, params (string Name, double?[] Values)[] series)
  {
    return new Chart
    {
      Type = type,
      Categories = series[0].Values.Select((_, index) => $"{index}").ToList(),
      Series = series.Select(entry => new ChartSeries { Name = entry.Name, Values = [.. entry.Values] }).ToList()
    };
  }

  [Fact]
  public void A_stack_scales_from_zero_to_its_end_with_headroom_in_whole_steps()
  {
    // Arrange
    Chart chart = Chart("stacked_bar", ("A", [830]), ("B", [24.5]), ("C", [66.18]));

    // Act
    Scale scale = AxisScale.Of(chart, false, null, null, null);

    // Assert
    scale.ShouldBe(new Scale(0, 1000, 100));
  }

  [Fact]
  public void A_total_leaves_the_axis_to_its_stack()
  {
    // Arrange
    Chart chart = Chart("stacked_bar", ("A", [830]), ("B", [90.68]), ("Total", [920.68]));
    chart.SeriesOptions["Total"] = new ChartSeriesOptions { Type = "total" };

    // Act
    Scale scale = AxisScale.Of(chart, false, null, null, null);

    // Assert
    scale.ShouldBe(new Scale(0, 1000, 100));
  }

  [Fact]
  public void Negatives_take_headroom_below_zero_on_the_same_step()
  {
    // Arrange
    Chart chart = Chart("clustered_column", ("Space", [3796, 4086]), ("AI", [2620, -1237]));

    // Act
    Scale scale = AxisScale.Of(chart, false, null, null, null);

    // Assert
    scale.ShouldBe(new Scale(-2000, 5000, 1000));
  }

  [Fact]
  public void A_percent_stack_runs_to_one_exactly()
  {
    // Arrange
    Chart chart = Chart("percent_stacked_column", ("A", [3, 1]), ("B", [1, 3]));

    // Act
    Scale scale = AxisScale.Of(chart, false, null, null, null);

    // Assert
    scale.ShouldBe(new Scale(0, 1, 0.1));
  }

  [Fact]
  public void A_fixed_end_or_step_is_kept_and_the_rest_fit_around_it()
  {
    // Arrange
    Chart chart = Chart("line", ("A", [12, 540]));

    // Act
    Scale fixedMax = AxisScale.Of(chart, false, null, 800, null);
    Scale fixedStep = AxisScale.Of(chart, false, null, null, 150);

    // Assert
    fixedMax.ShouldBe(new Scale(0, 800, 100));
    fixedStep.ShouldBe(new Scale(0, 600, 150));
  }

  [Fact]
  public void PowerPoints_automatic_axis_lifts_off_zero_when_every_stack_end_sits_near_the_top()
  {
    // Arrange
    Chart chart = Chart("stacked_bar", ("A", [830]), ("B", [24.5]), ("C", [66.18]));

    // Act
    Scale scale = AxisScale.PowerPoint(chart, false, null, null, null);

    // Assert
    scale.ShouldBe(new Scale(780, 940, 20));
  }

  [Fact]
  public void PowerPoints_automatic_axis_counts_a_total_on_bars_as_one_more_segment()
  {
    // Arrange
    Chart chart = Chart("stacked_bar", ("A", [830]), ("B", [90.68]), ("Total", [920.68]));
    chart.SeriesOptions["Total"] = new ChartSeriesOptions { Type = "total" };

    // Act
    Scale scale = AxisScale.PowerPoint(chart, false, null, null, null);

    // Assert
    scale.ShouldBe(new Scale(0, 2000, 200));
  }

  [Fact]
  public void An_axis_with_nothing_on_it_runs_from_zero_to_one()
  {
    // Arrange
    Chart chart = Chart("clustered_column", ("A", [0, null]));

    // Act
    Scale scale = AxisScale.Of(chart, false, null, null, null);

    // Assert
    scale.ShouldBe(new Scale(0, 1, 0.1));
  }


}