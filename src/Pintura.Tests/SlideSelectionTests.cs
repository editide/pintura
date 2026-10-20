using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class SlideSelectionTests
{
  private static readonly string[] Ids = ["cover", "summary", "growth", "cash", "debt"];

  [Fact]
  public void Ids_numbers_and_ranges_pick_slides_in_the_decks_order()
  {
    // Arrange
    SlideSelection selection = SlideSelection.Parse("debt,1,2-3");

    // Act
    List<string> picked = selection.Pick(Ids, id => id);

    // Assert
    picked.ShouldBe(["cover", "summary", "growth", "debt"]);
  }

  [Theory]
  [InlineData("6", "no slide 6; the deck has 5")]
  [InlineData("3-9", "no slide 9; the deck has 5")]
  [InlineData("ending", "no slide 'ending'. Available: cover, summary, growth, cash, debt")]
  public void A_slide_the_deck_lacks_is_refused(string list, string message)
  {
    // Arrange
    SlideSelection selection = SlideSelection.Parse(list);

    // Act
    Action pick = () => selection.Pick(Ids, id => id);

    // Assert
    pick.ShouldThrow<ArgumentException>().Message.ShouldBe(message);
  }

  [Fact]
  public void A_deck_without_ids_is_picked_by_number()
  {
    // Act
    Action pick = () => SlideSelection.Parse("cover").Pick(Ids, _ => null);

    // Assert
    pick.ShouldThrow<ArgumentException>().Message.ShouldBe("no slide 'cover': this deck's slides are picked by number");
  }

  [Theory]
  [InlineData("")]
  [InlineData("1,,2")]
  [InlineData("3-1")]
  [InlineData("0")]
  public void A_malformed_list_is_refused(string list)
  {
    // Act
    Action parse = () => SlideSelection.Parse(list);

    // Assert
    parse.ShouldThrow<ArgumentException>();
  }


}
