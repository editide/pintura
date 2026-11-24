using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class VocabularyTests
{
  [Fact]
  public void Theme_color_schemes_round_trip()
  {
    foreach ((string model, string tool) in PowerPointVocabulary.ModelToToolScheme)
    {
      PowerPointVocabulary.ToolToModelScheme[tool].ShouldBe(model);
      PowerPointVocabulary.ToolToModelScheme[PowerPointVocabulary.ToThemeColorSlot(model)].ShouldBe(model);
    }
    PowerPointVocabulary.ThemeColors.Length.ShouldBe(12);
    foreach (string slot in PowerPointVocabulary.ThemeColors)
    {
      PowerPointVocabulary.ToolToModelScheme.ContainsKey(slot).ShouldBeTrue();
    }
  }

  [Fact]
  public void Label_positions_round_trip()
  {
    foreach ((string model, string dock) in PowerPointVocabulary.ModelToDock)
    {
      PowerPointVocabulary.DockToModel[dock].ShouldBe(model);
    }
  }

  [Fact]
  public void Autofit_values_round_trip()
  {
    PowerPointVocabulary.AutofitToModel(PowerPointVocabulary.ModelToAutofit["grow"]).ShouldBe("grow");
    PowerPointVocabulary.AutofitToModel(PowerPointVocabulary.ModelToAutofit["shrink"]).ShouldBe("shrink");
    PowerPointVocabulary.AutofitToModel(PowerPointVocabulary.ModelToAutofit["none"]).ShouldBeNull();
  }

  [Fact]
  public void Date_code_tokens_round_trip()
  {
    foreach ((string pattern, string excel) in PowerPointVocabulary.PatternToExcelDate)
    {
      PowerPointVocabulary.ExcelDateToPattern[excel].ShouldBe(pattern);
    }
    PowerPointVocabulary.ExcelDateToPattern.Count.ShouldBe(PowerPointVocabulary.PatternToExcelDate.Count);
  }

  [Fact]
  public void Chart_data_keys_round_trip_including_colons_in_names()
  {
    string encoded = PowerPointVocabulary.ChartDataKey(3, "Q1: Revenue");
    encoded.ShouldBe("3:Q1: Revenue");
    PowerPointVocabulary.ParseChartDataKey(encoded).ShouldBe((3, "Q1: Revenue"));
  }


}
