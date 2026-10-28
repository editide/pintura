using System.Globalization;

namespace Pintura;

/// <summary>A value axis's ends and the step between its labels.</summary>
public record Scale(double Min, double Max, double Step);

/// <summary>Pintura's automatic value axis, which the preview draws and the export writes as fixed bounds, so
/// PowerPoint never scales on its own. An axis reaches zero and every stack's end, less totals, 5% past each end;
/// its step is PowerPoint's: 1, 2, or 5 times a power of ten giving five to ten steps, nearest
/// seven and a half; and its ends round out to whole steps. A percent stack runs to 100% exactly.</summary>
public static class AxisScale
{
  public static Scale Of(Chart chart, bool secondary, double? min, double? max, double? step)
  {
    (double low, double high) = Extent(chart, secondary);
    bool exact = chart.Grouping == "percent_stacked" && !secondary;
    return Fit(exact ? low : low * 1.05, exact ? high : high * 1.05, min, max, step);
  }

  public static Scale Fit(double low, double high, double? min, double? max, double? step)
  {
    low = min ?? low;
    high = max ?? high;
    if (high <= low)
    {
      high = low + 1;
    }
    double unit = step ?? Step(low, high);
    double first = min ?? Tidy(Math.Floor(Tidy(low / unit)) * unit);
    double last = max ?? Tidy(Math.Ceiling(Tidy(high / unit)) * unit);
    return new Scale(first, last, unit);
  }

  /// <summary>Of the candidates giving five to ten steps over the range, the one nearest seven and a half, the
  /// smaller on a tie; with none, the one nearest five.</summary>
  public static double Step(double low, double high)
  {
    double range = high - low;
    int power = (int)Math.Round(Math.Log10(range));
    double[] candidates = [5 * Math.Pow(10, power - 2), Math.Pow(10, power - 1), 2 * Math.Pow(10, power - 1),
      5 * Math.Pow(10, power - 1)];
    double[] fitting = candidates.Where(candidate => range / candidate is >= 5 - 1e-9 and <= 10 + 1e-9).ToArray();
    double best = fitting.Length > 0
      ? fitting.OrderBy(candidate => Math.Round(Math.Abs(range / candidate - 7.5), 9)).First()
      : candidates.MinBy(candidate => Math.Abs(range / candidate - 5));
    return Tidy(best);
  }

  /// <summary>An estimate of the axis PowerPoint scales on its own, for an import to fix as it was drawn. Its ends
  /// follow Excel's rule, measured exact: over the series' values, stacked, 5% of their range past each end, and a
  /// minimum of zero unless every value sits within the top sixth of the largest, where it drops half the range
  /// instead. Its step is <see cref="Step"/>'s, which PowerPoint widens on a short axis or one with wide labels.
  /// </summary>
  public static Scale PowerPoint(Chart chart, bool secondary, double? min, double? max, double? step)
  {
    List<double> ends = Ends(chart, secondary, powerPoint: true);
    if (chart.Grouping == "percent_stacked" && !secondary)
    {
      return Fit(ends.Any(end => end < 0) ? -1 : 0, 1, min, max, step);
    }
    double dataMin = ends.DefaultIfEmpty(0).Min();
    double dataMax = ends.DefaultIfEmpty(0).Max();
    if (dataMin == dataMax)
    {
      dataMin = Math.Min(0, dataMin);
      dataMax = Math.Max(0, dataMax);
    }
    double range = dataMax - dataMin;
    double low = dataMin >= 0
      ? (range == 0 || range / dataMax > 1.0 / 6 ? 0 : Math.Max(0, dataMin - range / 2))
      : dataMin - range * 0.05;
    double high = dataMax <= 0
      ? (range == 0 || range / -dataMin > 1.0 / 6 ? 0 : Math.Min(0, dataMax + range / 2))
      : dataMax + range * 0.05;
    return Fit(low, high, min, max, step);
  }

  /// <summary>The lowest and highest value the axis must show, zero included: the stacks' ends, less totals.
  /// </summary>
  public static (double Low, double High) Extent(Chart chart, bool secondary)
  {
    List<double> ends = Ends(chart, secondary, powerPoint: false);
    if (chart.Grouping == "percent_stacked" && !secondary)
    {
      return (ends.Any(end => end < 0) ? -1 : 0, 1);
    }
    return (Math.Min(0, ends.DefaultIfEmpty(0).Min()), Math.Max(0, ends.DefaultIfEmpty(0).Max()));
  }

  /// <summary>Where each value on the axis ends, stacked as the chart draws it: lines on the running total, bars with
  /// positives and negatives apart, and a waterfall's bars at both ends. A stack's totals are left out, or with
  /// <paramref name="powerPoint"/>, counted as the export writes them: a line of their own over columns, and one more
  /// segment on bars.</summary>
  private static List<double> Ends(Chart chart, bool secondary, bool powerPoint)
  {
    if (chart.Type == "waterfall")
    {
      return secondary ? [] : chart.Bars().OfType<(double From, double To)>()
        .SelectMany(bar => new[] { bar.From, bar.To }).ToList();
    }
    string Type(ChartSeries series) => chart.SeriesOptions.GetValueOrDefault(series.Name)?.Type ?? chart.Family;
    bool Secondary(ChartSeries series) => chart.SeriesOptions.GetValueOrDefault(series.Name)?.Axis == "secondary";
    bool Stacks(ChartSeries series) => chart.Grouping != "clustered" &&
      (Type(series) == chart.Family || (powerPoint && Type(series) == "total" && chart.Family == "bar"));
    List<ChartSeries> own = chart.Series
      .Where(series => Secondary(series) == secondary && (powerPoint || Type(series) != "total"))
      .ToList();
    bool percent = chart.Grouping == "percent_stacked";
    int count = chart.Categories.Count;
    double[] sizes = new double[count];
    foreach (ChartSeries series in own.Where(Stacks))
    {
      for (int point = 0; point < Math.Min(count, series.Values.Count); point++)
      {
        sizes[point] += Math.Abs(series.Values[point] ?? 0);
      }
    }

    var ends = new List<double>();
    double[] above = new double[count];
    double[] below = new double[count];
    foreach (ChartSeries series in own)
    {
      bool stacks = Stacks(series);
      for (int point = 0; point < series.Values.Count; point++)
      {
        if (series.Values[point] is not double raw)
        {
          continue;
        }
        double end = raw;
        if (stacks && point < count)
        {
          double value = percent ? (sizes[point] == 0 ? 0 : raw / sizes[point]) : raw;
          double[] side = chart.Family == "line" || value >= 0 ? above : below;
          side[point] += value;
          end = side[point];
        }
        ends.Add(end);
      }
    }
    return ends;
  }

  private static double Tidy(double value)
  {
    return double.Parse(value.ToString("G12", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
  }


}