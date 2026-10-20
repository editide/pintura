using System.Text.Json;
using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class CommandLineTests
{
  [Fact]
  public void Element_takes_a_slide_and_an_id()
  {
    // Act
    CommandLineOptions options = CommandLineOptions.Parse(["deck", "--element", "revenue", "takeaway", "--json"]);

    // Assert
    options.Root.ShouldBe("deck");
    options.Command.ShouldBe(HeadlessCommand.Element);
    options.Slide.ShouldBe("revenue");
    options.Element.ShouldBe("takeaway");
    options.Json.ShouldBeTrue();
  }

  [Theory]
  [InlineData("--check --json", HeadlessCommand.Check, null, null, true, false)]
  [InlineData("deck --comments --json", HeadlessCommand.Comments, null, null, true, false)]
  [InlineData("deck --screenshot all --output sheet.png", HeadlessCommand.Screenshot, "all", "sheet.png", false, false)]
  [InlineData("deck --port 5000 --no-open", null, null, null, false, true)]
  public void Options_parse_into_what_they_name(string arguments, HeadlessCommand? command, string? slide,
    string? output, bool json, bool noOpen)
  {
    // Act
    CommandLineOptions options = CommandLineOptions.Parse(arguments.Split(' '));

    // Assert
    options.Command.ShouldBe(command);
    options.Slide.ShouldBe(slide);
    options.Output.ShouldBe(output);
    options.Json.ShouldBe(json);
    options.NoOpen.ShouldBe(noOpen);
  }

  [Fact]
  public void Slides_narrow_an_export_or_an_import()
  {
    // Act
    CommandLineOptions export = CommandLineOptions.Parse(["deck", "--export", "out.pptx", "--slides", "cover,3-4"]);
    CommandLineOptions import = CommandLineOptions.Parse(["--import", "deck.pptx", "out", "--slides", "2"]);

    // Assert
    export.Slides.ShouldNotBeNull();
    import.Slides.ShouldNotBeNull();
  }

  [Theory]
  [InlineData("deck --list --slides 1")]
  [InlineData("deck --check --export --slides 1")]
  [InlineData("--import deck.pptx out --theme --slides 1")]
  public void Slides_combine_with_nothing_else(string arguments)
  {
    // Act
    Action parse = () => CommandLineOptions.Parse(arguments.Split(' '));

    // Assert
    parse.ShouldThrow<ArgumentException>().Message.ShouldContain("--slides");
  }

  [Fact]
  public void Diff_compares_against_head_unless_given_a_revision()
  {
    // Act
    CommandLineOptions head = CommandLineOptions.Parse(["deck", "--diff", "--json"]);
    CommandLineOptions range = CommandLineOptions.Parse(["deck", "--diff", "main..HEAD"]);

    // Assert
    head.Command.ShouldBe(HeadlessCommand.Diff);
    head.Revision.ShouldBe("HEAD");
    head.Json.ShouldBeTrue();
    range.Revision.ShouldBe("main..HEAD");
  }

  [Fact]
  public void Diff_narrows_a_check_and_pairs_a_screenshot_and_nothing_else()
  {
    // Act
    CommandLineOptions check = CommandLineOptions.Parse(["deck", "--diff", "main", "--check", "--json"]);
    CommandLineOptions screenshot = CommandLineOptions.Parse(["deck", "--screenshot", "cover", "--diff"]);
    ArgumentException list = Should.Throw<ArgumentException>(() =>
      CommandLineOptions.Parse(["deck", "--list", "--diff"]));

    // Assert
    check.Command.ShouldBe(HeadlessCommand.Check);
    check.Revision.ShouldBe("main");
    check.Json.ShouldBeTrue();
    screenshot.Command.ShouldBe(HeadlessCommand.Screenshot);
    screenshot.Revision.ShouldBe("HEAD");
    list.Message.ShouldBe("--diff combines only with --check and --screenshot.");
  }

  [Fact]
  public void Export_html_takes_the_file_it_writes()
  {
    // Act
    CommandLineOptions options = CommandLineOptions.Parse(["deck", "--export-html", "deck.html"]);
    ArgumentException missing =
      Should.Throw<ArgumentException>(() => CommandLineOptions.Parse(["deck", "--export-html"]));

    // Assert
    options.Command.ShouldBe(HeadlessCommand.ExportHtml);
    options.Output.ShouldBe("deck.html");
    missing.Message.ShouldBe("--export-html requires 1 value(s).");
  }

  [Fact]
  public void Export_with_a_file_writes_the_deck_and_combines_with_nothing()
  {
    // Act
    CommandLineOptions options = CommandLineOptions.Parse(["deck", "--export", "deck.pptx"]);
    ArgumentException error = Should.Throw<ArgumentException>(() =>
      CommandLineOptions.Parse(["deck", "--check", "--export", "deck.pptx"]));

    // Assert
    options.Command.ShouldBe(HeadlessCommand.ExportPptx);
    options.Output.ShouldBe("deck.pptx");
    options.Export.ShouldBeFalse();
    error.Message.ShouldStartWith("Use only one of");
  }

  [Fact]
  public void Export_adds_to_check_and_nothing_else()
  {
    // Act
    CommandLineOptions options = CommandLineOptions.Parse(["deck", "--check", "--export"]);
    ArgumentException error = Should.Throw<ArgumentException>(() => CommandLineOptions.Parse(["deck", "--export"]));

    // Assert
    options.Export.ShouldBeTrue();
    error.Message.ShouldBe("--export requires --check.");
  }

  [Theory]
  [InlineData("--json")]
  [InlineData("--screenshot")]
  [InlineData("--screenshot cover --json")]
  [InlineData("--check --output out.png")]
  [InlineData("--check --port 5000")]
  [InlineData("--list --slide cover")]
  [InlineData("--element cover")]
  [InlineData("--docs fonts")]
  [InlineData("--list --port 5000")]
  [InlineData("deck --login")]
  [InlineData("--whoami --json")]
  [InlineData("--check --no-open")]
  [InlineData("--docs --no-open")]
  [InlineData("--login --no-open")]
  public void Contradictory_or_incomplete_options_are_rejected(string arguments)
  {
    // Act
    Action parse = () => CommandLineOptions.Parse(arguments.Split(' '));

    // Assert
    parse.ShouldThrow<ArgumentException>();
  }

  [Theory]
  [InlineData("--login", AccountCommand.Login)]
  [InlineData("--logout", AccountCommand.Logout)]
  [InlineData("--whoami", AccountCommand.WhoAmI)]
  public void Account_commands_stand_alone(string argument, AccountCommand command)
  {
    // Act
    CommandLineOptions options = CommandLineOptions.Parse([argument]);

    // Assert
    options.Account.ShouldBe(command);
    options.Command.ShouldBeNull();
  }

  [Fact]
  public void Every_documentation_topic_is_packaged()
  {
    // Arrange
    var topics = new List<string> { "" };
    topics.AddRange(Documentation.Topics);

    foreach (string topic in topics)
    {
      // Act
      var writer = new StringWriter();
      Documentation.Write(topic, writer);

      // Assert
      writer.ToString().ShouldStartWith(topic.Length == 0 ? "# Pintura" : $"# {topic}");
    }
  }

  [Fact]
  public void Discovery_finds_each_presentation_and_stops_at_it()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("a/presentation.yaml", "");
    folder.Write("a/nested/presentation.yaml", "");
    folder.Write("b/c/presentation.yaml", "");
    folder.Write("bin/d/presentation.yaml", "");

    // Act
    List<string> found = PresentationDiscovery.Find(folder.Root);

    // Assert
    found.ShouldBe([folder.PathTo("a"), folder.PathTo("b/c")]);
  }

  [Fact]
  public void List_prints_slides_under_their_sections()
  {
    // Arrange
    using var deck = new DeckFixture();
    var output = new StringWriter();

    // Act
    int exit = HeadlessRunner.Run(deck.Folder.PathTo("deck"), HeadlessCommand.List, null, null, false, output);

    // Assert
    exit.ShouldBe(0);
    output.ToString().ReplaceLineEndings("\n")
      .ShouldBe(" 1 one  cover    Hello\n   Results\n 2 two  content  Results in Results\n");
  }

  [Fact]
  public void List_exits_nonzero_when_a_slide_fails()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", "layout: content\ncolour: red");
    var output = new StringWriter();

    // Act
    int exit = HeadlessRunner.Run(deck.Folder.PathTo("deck"), HeadlessCommand.List, null, null, true, output);

    // Assert
    exit.ShouldBe(1);
    JsonDocument.Parse(output.ToString()).RootElement[1].GetProperty("error").GetString()!
      .ShouldStartWith("unknown key colour in ");
  }

  [Fact]
  public void Element_prints_each_property_beside_the_line_that_set_it()
  {
    // Arrange
    string root = Path.Combine(LoaderTests.RepositoryRoot(), "presentations", "example");
    var output = new StringWriter();

    // Act
    HeadlessRunner.Run(root, HeadlessCommand.Element, "revenue", "takeaway", false, output);

    // Assert
    string[] lines = output.ToString().ReplaceLineEndings("\n").Split('\n');
    lines.ShouldContain(line => line.Contains("fill") && line.Contains("accent1 lighter 80 (#") &&
      line.EndsWith("slides/revenue.yaml:12 elements.takeaway.fill"));
    lines.ShouldContain(line => line.Contains("size") &&
      line.EndsWith("themes/acme/styles.yaml:29 styles.callout.size"));
    lines.ShouldContain(line => line.Contains("run \"$11.4B\" .emphasis"));
  }

  [Fact]
  public void Element_names_the_ids_on_the_slide_when_one_is_unknown()
  {
    // Arrange
    using var deck = new DeckFixture();

    // Act
    Action run = () => HeadlessRunner.Run(deck.Folder.PathTo("deck"), HeadlessCommand.Element, "two", "chart",
      false, new StringWriter());

    // Assert
    run.ShouldThrow<InvalidDataException>().Message
      .ShouldBe("No element 'chart' on slide 'two'. Available: page, title");
  }


}