using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class BookTests
{
  /// <summary>Two lines and a view of them, as Libreta would answer for the book at <c>book/</c>.</summary>
  private sealed class FakeBooks : IBookReader
  {
    public Dictionary<string, BookLine> Answer { get; } = new Dictionary<string, BookLine>(StringComparer.Ordinal)
    {
      ["historical.segments.connectivity_revenue"] = new BookLine("historical.segments.connectivity_revenue",
        "Connectivity", "millions", ["2024", "2025"], ["7599", "11387"], ["7,599", "11,387"]),
      ["historical.segments.ai_revenue"] = new BookLine("historical.segments.ai_revenue", "AI", "millions",
        ["2024", "2025"], ["2620", null], ["2,620", ""]),
      ["historical.segments.connectivity_share"] = new BookLine("historical.segments.connectivity_share",
        "Connectivity share", "percent", ["2024", "2025"], ["0.542", "0.6098"], ["54.2%", "61.0%"])
    };

    /// <summary>Rows the view shows after its own, such as a line it shows twice.</summary>
    public List<BookRow> Appended { get; } = [];

    public IReadOnlyDictionary<string, BookLine> Lines(string root)
    {
      return Answer;
    }

    public BookView View(string root, string id)
    {
      return new BookView(id, [new BookColumn("2024", "FY 2024"), new BookColumn("2025", "FY 2025")],
      [
        new BookRow(null, "Revenue", "header", "header", 0, null, null),
        new BookRow("historical.segments.connectivity_revenue", "Connectivity", "fact", null, 1, ["7599", "11387"],
          ["7,599", "11,387"]),
        new BookRow(null, null, "spacer", "compact", 0, null, null),
        new BookRow("historical.segments.total_revenue", "Total", "formula", "total", 0, ["14015", "18674"],
          ["14,015", "18,674"]),
        .. Appended
      ]);
    }

    public string Page(string root)
    {
      return "<html></html>";
    }


  }

  private static DeckFixture Deck(string slide)
  {
    var deck = new DeckFixture { Books = new FakeBooks() };
    deck.Write("book/book.yaml", "name: Acme");
    deck.Write("deck/presentation.yaml", """
      name: Test deck
      theme: ../theme
      books:
        - id: acme
          path: ../book
      slides:
        - two
      """);
    deck.Write("deck/slides/two.yaml", slide);
    return deck;
  }

  /// <summary>Books that take a while to answer, counting how many questions are open at once.</summary>
  private sealed class SlowBooks : IBookReader
  {
    private readonly FakeBooks inner = new FakeBooks();
    private int open;
    private int mostOpen;

    public int MostOpen => Volatile.Read(ref mostOpen);

    public IReadOnlyDictionary<string, BookLine> Lines(string root)
    {
      return Slowly(() => inner.Lines(root));
    }

    public BookView View(string root, string id)
    {
      return Slowly(() => inner.View(root, id));
    }

    public string Page(string root)
    {
      return inner.Page(root);
    }

    private T Slowly<T>(Func<T> answer)
    {
      Interlocked.Increment(ref open);
      // Waits for a second caller rather than sleeping, so a busy machine can't keep two concurrent calls apart.
      SpinWait.SpinUntil(() => Volatile.Read(ref open) > 1 || MostOpen > 1, TimeSpan.FromSeconds(5));
      lock (inner)
      {
        mostOpen = Math.Max(mostOpen, Volatile.Read(ref open));
      }
      Interlocked.Decrement(ref open);
      return answer();
    }


  }

  /// <summary>A libreta that runs <paramref name="script"/> in sh, which Windows reaches through a .cmd handing it to
  /// Git's sh.</summary>
  private static string FakeLibreta(TempFolder folder, string script)
  {
    folder.Write("libreta.sh", script);
    if (OperatingSystem.IsWindows())
    {
      folder.Write("libreta.cmd", $"@sh \"{folder.PathTo("libreta.sh")}\" %*");
      return folder.PathTo("libreta.cmd");
    }
    File.SetUnixFileMode(folder.PathTo("libreta.sh"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
    return folder.PathTo("libreta.sh");
  }

  private static string Text(SceneItem item)
  {
    return string.Concat(item.Paragraphs!.SelectMany(paragraph => paragraph.Runs).Select(run => run.Text));
  }

  [Fact]
  public void A_reference_shows_the_cell_as_libreta_displays_it_and_traces_to_the_book()
  {
    // Arrange
    using DeckFixture deck = Deck("""
      layout: content
      title: Connectivity made ${acme::historical.segments.connectivity_revenue["2025"]} in 2025
      """);

    // Act
    SceneItem title = deck.Item("two", "title");

    // Assert
    Text(title).ShouldBe("Connectivity made 11,387 in 2025");
    SceneRun reference = title.Paragraphs![0].Runs[1];
    reference.Field.ShouldBe("acme::historical.segments.connectivity_revenue[\"2025\"]");
    reference.Origin.ToString()
      .ShouldBe("deck/presentation.yaml:4 acme::historical.segments.connectivity_revenue[\"2025\"]");
  }

  [Fact]
  public void A_reference_with_a_format_spells_the_exact_value_in_it()
  {
    // Arrange
    using DeckFixture deck = Deck("""
      layout: content
      title: ${ acme::historical.segments.connectivity_revenue [ "2025" ] |billions } in revenue
      """);

    // Act
    SceneItem title = deck.Item("two", "title");

    // Assert
    Text(title).ShouldBe("$11.4B in revenue");
    title.Paragraphs![0].Runs[0].Field
      .ShouldBe("acme::historical.segments.connectivity_revenue[\"2025\"] | billions");
  }

  [Theory]
  [InlineData("${nasa::historical.segments.ai_revenue[\"2025\"]}",
    "unknown book nasa; the presentation's books are acme")]
  [InlineData("${acme::historical.segments.space_revenue[\"2025\"]}",
    "no line historical.segments.space_revenue in book acme; name it as libreta --lines lists it, from the " +
    "book's folder")]
  [InlineData("${acme::historical.segments.ai_revenue[\"2023\"]}",
    "historical.segments.ai_revenue has no column 2023; its columns are 2024 and 2025")]
  [InlineData("${acme::historical.segments.ai_revenue[\"2025\"]}",
    "historical.segments.ai_revenue has no value in 2025")]
  [InlineData("${acme::historical.segments.ai_revenue[\"2024\"] | millions}",
    "unknown format millions; the theme's formats are billions and dollars")]
  [InlineData("${acme::historical.segments.ai_revenue}",
    "${acme::historical.segments.ai_revenue} is not a reference; write ${book::line[\"column\"]}, with | and a " +
    "format's name before the } to spell it")]
  public void A_reference_that_does_not_resolve_fails_its_slide(string reference, string problem)
  {
    // Arrange
    using DeckFixture deck = Deck($"""
      layout: content
      title: Revenue of {reference}
      """);

    // Act
    LoadException error = deck.Error();

    // Assert
    error.Problem.ShouldBe(problem);
    error.Topic.ShouldBe("books");
  }

  [Fact]
  public void A_book_without_book_yaml_stops_the_presentation()
  {
    // Arrange
    using DeckFixture deck = Deck("layout: content");
    File.Delete(deck.Folder.PathTo("book/book.yaml"));

    // Act
    LoadException error = deck.Error();

    // Assert
    error.Problem.ShouldBe("no book.yaml in ../book; paths are relative to presentation.yaml");
    error.Origin.ToString().ShouldBe("deck/presentation.yaml:5 books[1].path");
  }

  [Fact]
  public void A_chart_takes_a_series_from_each_line_over_the_columns_it_names()
  {
    // Arrange
    using DeckFixture deck = Deck("""
      layout: content
      elements:
        - id: chart
          chart: revenue
          frame: left
      """);
    deck.Write("deck/charts/revenue.yaml", """
      type: clustered_column
      data:
        book: acme
        columns: ["2025", "2024"]
        series:
          - { line: historical.segments.connectivity_revenue }
          - { line: historical.segments.ai_revenue, label: Artificial intelligence }
      """);

    // Act
    Chart chart = deck.Load().Charts["revenue"];

    // Assert
    chart.Book.ShouldBe("acme");
    chart.Lines.ShouldBe(["historical.segments.connectivity_revenue", "historical.segments.ai_revenue"]);
    chart.Categories.ShouldBe(["2025", "2024"]);
    chart.Series.Select(series => series.Name).ShouldBe(["Connectivity", "Artificial intelligence"]);
    chart.Series[0].Values.ShouldBe([11387.0, 7599.0]);
    chart.Series[1].Values.ShouldBe([null, 2620.0]);
  }

  [Fact]
  public void A_chart_point_from_a_book_carries_the_cell_the_page_opens_on_a_click()
  {
    // Arrange
    using DeckFixture deck = Deck("""
      layout: content
      elements:
        - id: chart
          chart: revenue
          frame: left
      """);
    deck.Write("deck/charts/revenue.yaml", """
      type: clustered_column
      data:
        book: acme
        columns: ["2025", "2024"]
        series:
          - { line: historical.segments.connectivity_revenue }
          - { line: historical.segments.ai_revenue }
      """);

    // Act
    ChartPayload chart = PayloadBuilder.Build(deck.Load(), 1, (path, _) => path).Slides[0].Items!
      .Single(item => item.Id == "chart").Chart!;

    // Assert
    chart.Series[1].Traces.ShouldBe([
      new TracePayload("acme", "historical.segments.ai_revenue", "2025"),
      new TracePayload("acme", "historical.segments.ai_revenue", "2024")
    ]);
  }

  [Fact]
  public void A_chart_with_series_in_columns_takes_a_series_from_each_column_and_a_category_from_each_line()
  {
    // Arrange
    using DeckFixture deck = Deck("""
      layout: content
      elements:
        - id: chart
          chart: revenue
          frame: left
      """);
    deck.Write("deck/charts/revenue.yaml", """
      type: pie
      series_in: columns
      data:
        book: acme
        columns: ["2024"]
        series:
          - { line: historical.segments.connectivity_revenue }
          - { line: historical.segments.ai_revenue, label: Artificial intelligence }
      """);

    // Act
    Presentation presentation = deck.Load();
    ChartPayload payload = PayloadBuilder.Build(presentation, 1, (path, _) => path).Slides[0].Items!
      .Single(item => item.Id == "chart").Chart!;

    // Assert
    Chart chart = presentation.Charts["revenue"];
    chart.Categories.ShouldBe(["Connectivity", "Artificial intelligence"]);
    chart.Series.Single().Name.ShouldBe("2024");
    chart.Series.Single().Values.ShouldBe([7599.0, 2620.0]);
    payload.Series.Single().Traces.ShouldBe([
      new TracePayload("acme", "historical.segments.connectivity_revenue", "2024"),
      new TracePayload("acme", "historical.segments.ai_revenue", "2024")
    ]);
  }

  [Fact]
  public void A_chart_column_with_a_label_names_its_category_and_still_traces_to_the_book_column()
  {
    // Arrange
    using DeckFixture deck = Deck("""
      layout: content
      elements:
        - id: chart
          chart: revenue
          frame: left
      """);
    deck.Write("deck/charts/revenue.yaml", """
      type: clustered_column
      data:
        book: acme
        columns: [{ column: "2024", label: Prior year }, "2025"]
        series:
          - { line: historical.segments.ai_revenue }
      """);

    // Act
    Presentation presentation = deck.Load();
    ChartPayload payload = PayloadBuilder.Build(presentation, 1, (path, _) => path).Slides[0].Items!
      .Single(item => item.Id == "chart").Chart!;

    // Assert
    Chart chart = presentation.Charts["revenue"];
    chart.Categories.ShouldBe(["Prior year", "2025"]);
    chart.Series.Single().Values.ShouldBe([2620.0, null]);
    payload.Series.Single().Traces.ShouldBe([
      new TracePayload("acme", "historical.segments.ai_revenue", "2024"),
      new TracePayload("acme", "historical.segments.ai_revenue", "2025")
    ]);
  }

  [Fact]
  public void A_chart_with_series_in_columns_names_a_series_by_its_columns_label()
  {
    // Arrange
    using DeckFixture deck = Deck("""
      layout: content
      elements:
        - id: chart
          chart: revenue
          frame: left
      """);
    deck.Write("deck/charts/revenue.yaml", """
      type: pie
      series_in: columns
      data:
        book: acme
        columns: [{ column: "2024", label: Last year }]
        series:
          - { line: historical.segments.connectivity_revenue }
      """);

    // Act
    Presentation presentation = deck.Load();
    ChartPayload payload = PayloadBuilder.Build(presentation, 1, (path, _) => path).Slides[0].Items!
      .Single(item => item.Id == "chart").Chart!;

    // Assert
    presentation.Charts["revenue"].Series.Single().Name.ShouldBe("Last year");
    payload.Series.Single().Traces.ShouldBe([
      new TracePayload("acme", "historical.segments.connectivity_revenue", "2024")
    ]);
  }

  [Fact]
  public void A_chart_line_the_book_lacks_fails_the_slide_placing_it()
  {
    // Arrange
    using DeckFixture deck = Deck("""
      layout: content
      elements:
        - id: chart
          chart: revenue
          frame: left
      """);
    deck.Write("deck/charts/revenue.yaml", """
      type: clustered_column
      data:
        book: acme
        series:
          - { line: historical.segments.space_revenue }
      """);

    // Act
    LoadException error = deck.Error();

    // Assert
    error.Problem.ShouldStartWith("no line historical.segments.space_revenue in book acme");
    error.Origin.ToString().ShouldBe("deck/charts/revenue.yaml:5 data.series[1].line");
  }

  [Fact]
  public void A_table_presents_a_view_with_its_row_roles_as_row_styles()
  {
    // Arrange
    using DeckFixture deck = Deck("""
      layout: content
      elements:
        - id: results
          table: results
          frame: content
      """);
    deck.Write("deck/tables/results.yaml", """
      data:
        view: acme::historical/segments
      style: plain
      columns:
        - {}
        - { format: billions }
      """);

    // Act
    Table table = deck.Load().Tables["results"];
    SceneItem item = deck.Item("two", "results");

    // Assert
    table.View.ShouldBe("acme::historical/segments");
    table.HeaderRows.ShouldBe(1);
    table.Values.ShouldBe([
      ["", "FY 2024", "FY 2025"],
      ["Revenue", "", ""],
      ["Connectivity", "7,599", "11,387"],
      ["Total", "14,015", "18,674"]
    ]);
    table.Rows.Keys.ShouldBe([2, 4]);
    table.Rows[2].Style.ShouldBe("header");
    table.Rows[4].Style.ShouldBe("total");
    string Cell(int row, int column)
    {
      return string.Concat(item.Table!.Cells.Single(cell => cell.Row == row && cell.Column == column).Paragraphs
        .SelectMany(paragraph => paragraph.Runs).Select(run => run.Text));
    }
    Cell(3, 2).ShouldBe("$7.6B");
    Cell(3, 3).ShouldBe("11,387");
  }

  [Fact]
  public void A_view_row_keeps_its_role_style_when_the_table_sets_its_height_but_not_its_style()
  {
    // Arrange
    using DeckFixture deck = Deck("""
      layout: content
      elements:
        - id: results
          table: results
          frame: content
      """);
    deck.Write("deck/tables/results.yaml", """
      data:
        view: acme::historical/segments
      style: plain
      rows:
        4: { height: 30 }
      """);

    // Act
    Table table = deck.Load().Tables["results"];

    // Assert
    table.Rows[4].Style.ShouldBe("total");
    table.Rows[4].Height.ShouldBe(30);
  }

  [Fact]
  public void A_view_row_indents_its_label_by_the_table_style_indent_per_step()
  {
    // Arrange
    using DeckFixture deck = Deck("""
      layout: content
      elements:
        - id: results
          table: results
          frame: content
      """);
    deck.Write("deck/tables/results.yaml", """
      data:
        view: acme::historical/segments
      style: plain
      """);

    // Act
    SceneItem item = deck.Item("two", "results");

    // Assert
    SceneParagraph Label(int row, int column = 1)
    {
      return item.Table!.Cells.Single(cell => cell.Row == row && cell.Column == column).Paragraphs[0];
    }
    Label(3).Indent!.Value.ShouldBe(12.0);
    Label(3).Indent!.Origin!.ToString().ShouldBe("theme/styles.yaml:48 tables.plain.indent");
    Label(3).Hanging!.Value.ShouldBe(0.0);
    Label(3).Bullet.ShouldBeNull();
    Label(3, 2).Indent.ShouldBeNull();
    Label(2).Indent.ShouldBeNull();
  }

  [Fact]
  public void A_table_picks_lines_and_columns_from_the_view_in_its_own_order_and_labels()
  {
    // Arrange
    using DeckFixture deck = Deck("""
      layout: content
      elements:
        - id: results
          table: results
          frame: content
      """);
    deck.Write("deck/tables/results.yaml", """
      data:
        view: acme::historical/segments
        lines: [total_revenue, { line: historical.segments.connectivity_revenue, label: Subscriptions }]
        columns: [FY 2025]
      """);

    // Act
    Table table = deck.Load().Tables["results"];

    // Assert
    table.Values.ShouldBe([["", "FY 2025"], ["Total", "18,674"], ["Subscriptions", "11,387"]]);
    table.Sources![1][1].ShouldBe(("historical.segments.total_revenue", "2025"));
  }

  [Fact]
  public void A_table_relabels_its_header_above_the_labels_and_each_column()
  {
    // Arrange
    using DeckFixture deck = Deck("""
      layout: content
      elements:
        - id: results
          table: results
          frame: content
      """);
    deck.Write("deck/tables/results.yaml", """
      data:
        view: acme::historical/segments
        label: Revenue bridge
        lines: [total_revenue]
        columns: [{ column: "2024", label: Prior }, FY 2025]
      """);

    // Act
    Table table = deck.Load().Tables["results"];

    // Assert
    table.Values.ShouldBe([["Revenue bridge", "Prior", "FY 2025"], ["Total", "14,015", "18,674"]]);
    table.Sources![1][1].ShouldBe(("historical.segments.total_revenue", "2024"));
  }

  [Fact]
  public void The_views_a_presentations_tables_read_are_asked_for_at_once()
  {
    // Arrange
    using DeckFixture deck = Deck("""
      layout: content
      elements:
        - { id: first, table: first, frame: content }
        - { id: second, table: second, frame: content }
      """);
    var books = new SlowBooks();
    deck.Books = books;
    deck.Write("deck/tables/first.yaml", "data:\n  view: acme::historical/segments\n");
    deck.Write("deck/tables/second.yaml", "data:\n  view: acme::historical/totals\n");

    // Act
    deck.Load();

    // Assert
    books.MostOpen.ShouldBeGreaterThan(1);
  }

  [Fact]
  public void A_table_picks_a_line_the_view_shows_twice()
  {
    // Arrange
    using DeckFixture deck = Deck("""
      layout: content
      elements:
        - id: results
          table: results
          frame: content
      """);
    ((FakeBooks)deck.Books!).Appended.Add(new BookRow("historical.segments.connectivity_revenue",
      "Plus: connectivity", "fact", null, 0, ["7599", "11387"], ["7,599", "11,387"]));
    deck.Write("deck/tables/results.yaml", """
      data:
        view: acme::historical/segments
        lines: [connectivity_revenue]
        columns: [FY 2025]
      """);

    // Act
    Table table = deck.Load().Tables["results"];

    // Assert
    table.Values.ShouldBe([["", "FY 2025"], ["Connectivity", "11,387"]]);
  }

  [Fact]
  public void A_linked_number_carries_the_book_cell_the_page_opens_on_a_click()
  {
    // Arrange
    using DeckFixture deck = Deck("""
      layout: content
      title: ${acme::historical.segments.connectivity_revenue["2025"] | billions} in revenue
      elements:
        - id: results
          table: results
          frame: content
      """);
    deck.Write("deck/tables/results.yaml", """
      data:
        view: acme::historical/segments
        lines: [total_revenue]
      """);

    // Act
    string json = System.Text.Json.JsonSerializer.Serialize(PayloadBuilder.Build(deck.Load(), 1, (path, _) => path),
      PayloadJson.Options);

    // Assert
    json.ShouldContain("""
      "trace":{"book":"acme","line":"historical.segments.connectivity_revenue","column":"2025"}
      """.Trim());
    json.ShouldContain("""
      "trace":{"book":"acme","line":"historical.segments.total_revenue","column":"2024"}
      """.Trim());
  }

  [Fact]
  public void A_number_from_a_comparison_traces_to_the_cell_libreta_names_rather_than_its_row_and_column()
  {
    // Arrange
    using DeckFixture deck = Deck("""
      layout: content
      elements:
        - id: comps
          table: comps
          frame: content
      """);
    ((FakeBooks)deck.Books!).Appended.Add(new BookRow("amazon", "Amazon", "book", null, 0, ["3.3", "17"],
      ["3.3x", "17.0x"], [new BookCell("amazon::spread.ev_to_revenue", "Spread"),
        new BookCell("amazon::spread.ev_to_ebitda", "Spread")]));
    deck.Write("deck/tables/comps.yaml", """
      data:
        view: acme::historical/segments
        lines: [amazon]
      """);

    // Act
    string json = System.Text.Json.JsonSerializer.Serialize(PayloadBuilder.Build(deck.Load(), 1, (path, _) => path),
      PayloadJson.Options);

    // Assert
    json.ShouldContain("""
      "trace":{"book":"acme","line":"amazon::spread.ev_to_revenue","column":"Spread"}
      """.Trim());
    json.ShouldContain("""
      "trace":{"book":"acme","line":"amazon::spread.ev_to_ebitda","column":"Spread"}
      """.Trim());
  }

  [Fact]
  public void A_revision_reads_its_books_as_the_commit_left_them()
  {
    // Arrange
    using DeckFixture deck = Deck("""
      layout: content
      title: ${acme::historical.segments.connectivity_revenue["2025"]} in revenue
      """);
    RevisionTests.Git(deck.Folder.Root, "init", "-q");
    RevisionTests.Git(deck.Folder.Root, "add", "deck", "theme", "book");
    RevisionTests.Git(deck.Folder.Root, "commit", "-q", "-m", "first");
    deck.Write("book/book.yaml", "name: Acme, restated");
    Revision checkout = Revisions.Checkout(deck.Folder.PathTo("deck"), "HEAD", deck.Folder.Root);

    // Act
    Presentation committed = Revisions.Load(checkout, deck.Books);

    // Assert
    committed.Books["acme"].Root.ShouldBe(Path.Combine(checkout.Folder, "book"));
    File.ReadAllText(Path.Combine(committed.Books["acme"].Root, "book.yaml")).Trim().ShouldBe("name: Acme");
    PayloadBuilder.Title(committed, committed.Slides[0]).ShouldBe("11,387 in revenue");
  }

  [Fact]
  public void A_revision_keeps_the_formats_libreta_reads_above_its_books_as_far_as_the_repository_root()
  {
    // Arrange
    using DeckFixture deck = Deck("""
      layout: content
      title: Revenue
      """);
    deck.Write("formats.yaml", "formats: { dollars_per_share: { decimals: 2 } }");
    deck.Write("notes.md", "Not read by the book");
    RevisionTests.Git(deck.Folder.Root, "init", "-q");
    RevisionTests.Git(deck.Folder.Root, "add", ".");
    RevisionTests.Git(deck.Folder.Root, "commit", "-q", "-m", "first");

    // Act
    Revision checkout = Revisions.Checkout(deck.Folder.PathTo("deck"), "HEAD", deck.Folder.Root);

    // Assert
    File.ReadAllText(Path.Combine(checkout.Folder, "formats.yaml")).ShouldContain("dollars_per_share");
    File.Exists(Path.Combine(checkout.Folder, "notes.md")).ShouldBeFalse();
    // Libreta merges formats up to a worktree's root, which a .git marker names.
    File.Exists(Path.Combine(checkout.Folder, ".git")).ShouldBeTrue();
  }

  [Fact]
  public void A_revision_quoting_a_book_outside_the_repository_cant_be_compared()
  {
    // Arrange
    using DeckFixture deck = Deck("""
      layout: content
      title: Revenue
      """);
    using var elsewhere = new TempFolder();
    elsewhere.Write("book/book.yaml", "name: Acme");
    deck.Write("deck/presentation.yaml", $"""
      name: Test deck
      theme: ../theme
      books:
        - id: acme
          path: {elsewhere.PathTo("book")}
      slides:
        - two
      """);
    RevisionTests.Git(deck.Folder.Root, "init", "-q");
    RevisionTests.Git(deck.Folder.Root, "add", "deck", "theme");
    RevisionTests.Git(deck.Folder.Root, "commit", "-q", "-m", "first");

    // Act
    InvalidDataException error = Should.Throw<InvalidDataException>(() =>
      Revisions.Checkout(deck.Folder.PathTo("deck"), "HEAD", deck.Folder.Root));

    // Assert
    error.Message.ShouldBe($"the book acme at {elsewhere.PathTo("book")} is outside the repository, so HEAD " +
      "can't be compared");
  }

  [Fact]
  public void Libreta_is_asked_once_until_a_file_in_the_book_changes()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("book/book.yaml", "name: Acme");
    string libreta = FakeLibreta(folder, $$"""
      #!/bin/sh
      echo call >> '{{folder.PathTo("calls").Replace('\\', '/')}}'
      echo '[{"name":"a.b","label":"B","columns":["2025"],"exact":["1"],"display":["1.0"]},'
      echo '{"name":"a.c","error":"bad"}]'
      exit 1
      """);
    var reader = new LibretaReader(libreta);

    // Act
    IReadOnlyDictionary<string, BookLine> first = reader.Lines(folder.PathTo("book"));
    reader.Lines(folder.PathTo("book"));
    folder.Write("book/book.yaml", "name: Acme, restated");
    reader.Lines(folder.PathTo("book"));

    // Assert
    first.Keys.ShouldBe(["a.b"]);
    first["a.b"].Display.ShouldBe(["1.0"]);
    File.ReadAllLines(folder.PathTo("calls")).Length.ShouldBe(2);
  }

  [Fact]
  public void A_session_reads_each_book_as_it_first_found_it_and_the_next_session_sees_a_change()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("book/book.yaml", "name: Acme");
    string libreta = FakeLibreta(folder, $$"""
      #!/bin/sh
      echo call >> '{{folder.PathTo("calls").Replace('\\', '/')}}'
      echo '{"id":"v","columns":[],"rows":[]}'
      """);
    IBookReader reader = new LibretaReader(libreta);
    int Calls() => File.ReadAllLines(folder.PathTo("calls")).Length;

    // Act
    IBookReader session = reader.Session();
    session.View(folder.PathTo("book"), "v");
    folder.Write("book/book.yaml", "name: Acme, restated");
    session.View(folder.PathTo("book"), "v");
    int inSession = Calls();
    reader.Session().View(folder.PathTo("book"), "v");

    // Assert
    inSession.ShouldBe(1);
    Calls().ShouldBe(2);
  }

  [Fact]
  public void A_books_page_is_exported_again_only_once_the_book_changes()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("book/book.yaml", "name: Acme");
    string libreta = FakeLibreta(folder, $$"""
      #!/bin/sh
      echo call >> '{{folder.PathTo("calls").Replace('\\', '/')}}'
      echo "<html>$(cat "$1/book.yaml")</html>" > "$3"
      echo wrote
      """);
    var reader = new LibretaReader(libreta);
    int Calls() => File.ReadAllLines(folder.PathTo("calls")).Length;

    // Act
    reader.Page(folder.PathTo("book"));
    string unchanged = reader.Page(folder.PathTo("book"));
    int beforeChange = Calls();
    folder.Write("book/book.yaml", "name: Acme, restated");
    string changed = reader.Session().Page(folder.PathTo("book"));

    // Assert
    beforeChange.ShouldBe(1);
    unchanged.ShouldContain("name: Acme</html>");
    changed.ShouldContain("Acme, restated");
    Calls().ShouldBe(2);
  }

  [Fact]
  public void A_presentation_inside_the_book_changes_nothing_libreta_reads_but_formats_above_it_do()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("repo/formats.yaml", "formats: {}");
    folder.Write("repo/book/book.yaml", "name: Acme");
    folder.Write("repo/book/deck/presentation.yaml", "name: Deck");
    folder.Write("repo/book/deck/slides/one.yaml", "title: One");
    string libreta = FakeLibreta(folder, $$"""
      #!/bin/sh
      echo call >> '{{folder.PathTo("calls").Replace('\\', '/')}}'
      echo '[]'
      """);
    var reader = new LibretaReader(libreta);
    int Calls() => File.ReadAllLines(folder.PathTo("calls")).Length;

    // Act
    reader.Lines(folder.PathTo("repo/book"));
    folder.Write("repo/book/deck/slides/one.yaml", "title: One, reworded");
    reader.Lines(folder.PathTo("repo/book"));
    int afterSlide = Calls();
    folder.Write("repo/formats.yaml", "formats: { millions: { decimals: 1 } }");
    reader.Lines(folder.PathTo("repo/book"));

    // Assert
    afterSlide.ShouldBe(1);
    Calls().ShouldBe(2);
  }

  [Fact]
  public void A_libreta_that_answers_garbage_or_hangs_is_a_book_error()
  {
    // Arrange
    using var folder = new TempFolder();
    using var other = new TempFolder();
    folder.Write("book/book.yaml", "name: Acme");
    var garbled = new LibretaReader(FakeLibreta(folder, "#!/bin/sh\necho '[not json'"));
    var slow = new LibretaReader(FakeLibreta(other, "#!/bin/sh\nsleep 30"), TimeSpan.FromSeconds(1));

    // Act
    BookException unreadable = Should.Throw<BookException>(() => garbled.Lines(folder.PathTo("book")));
    BookException late = Should.Throw<BookException>(() => slow.Lines(folder.PathTo("book")));

    // Assert
    unreadable.Message.ShouldStartWith("Libreta answered with what Pintura can't read");
    late.Message.ShouldBe("Libreta didn't answer within 1 s");
  }

  [Fact]
  public void A_view_row_shown_negated_reads_its_exact_values_negated()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("book/book.yaml", "name: Acme");
    string libreta = FakeLibreta(folder, """
      #!/bin/sh
      echo '{"id":"v","columns":[{"name":"2024","label":"2024"},{"name":"2025","label":"2025"}],"rows":['
      echo '{"name":"v.cash","label":"Less: cash","kind":"formula","exact":["78.5","-2"],'
      echo '"display":["(78.5)","2.0"],"contra":[true,true]},'
      echo '{"name":"v.debt","label":"Debt","kind":"formula","exact":["4","0"],"display":["4.0","0.0"]}]}'
      """);
    var reader = new LibretaReader(libreta);

    // Act
    BookView view = reader.View(folder.PathTo("book"), "v");

    // Assert
    view.Rows[0].Exact.ShouldBe(["-78.5", "2"]);
    view.Rows[1].Exact.ShouldBe(["4", "0"]);
  }

  [Fact]
  public void A_view_row_keeps_the_cells_libreta_names_for_it()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("book/book.yaml", "name: Comps");
    string libreta = FakeLibreta(folder, """
      #!/bin/sh
      echo '{"id":"comps","columns":[{"name":"spread.ev","label":"EV"}],"rows":['
      echo '{"name":"amazon","label":"Amazon","kind":"book","exact":["2"],"display":["2.0"],'
      echo '"cells":[{"line":"amazon::spread.ev","column":"Spread"}]},'
      echo '{"name":"comps.median","label":"Median","kind":"scope","exact":["1"],"display":["1.0"]}]}'
      """);
    var reader = new LibretaReader(libreta);

    // Act
    BookView view = reader.View(folder.PathTo("book"), "comps");

    // Assert
    view.Rows[0].Cells.ShouldBe([new BookCell("amazon::spread.ev", "Spread")]);
    view.Rows[1].Cells.ShouldBeNull();
  }

  [Fact]
  public void A_cell_libreta_cant_open_fails_with_its_reason()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("book/book.yaml", "name: Comps");
    string libreta = FakeLibreta(folder, """
      #!/bin/sh
      echo "Unknown line '$3'." >&2
      exit 1
      """);
    var reader = new LibretaReader(libreta);

    // Act
    BookException error = Should.Throw<BookException>(() => reader.Open(folder.PathTo("book"), "amazon", "2025"));

    // Assert
    error.Message.ShouldBe("Libreta couldn't open amazon in 2025: Unknown line 'amazon'.");
  }

  [Fact]
  public void Opening_a_cell_answers_the_page_libreta_left_to_open_and_how_many_pages_selected_it()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("book/book.yaml", "name: Acme");
    string libreta = FakeLibreta(folder, """
      #!/bin/sh
      [ "$5 $6" = "--no-open --json" ] || exit 2
      echo "  serving http://localhost:5173"
      echo '{"url":"http://localhost:5173/?view=historical%2Fsegments&line='$3'&column='$4'","pages":1}'
      """);
    var reader = new LibretaReader(libreta);

    // Act
    LibretaPage page = reader.Open(folder.PathTo("book"), "historical.segments.ai_revenue", "2025");

    // Assert
    page.ShouldBe(new LibretaPage(
      "http://localhost:5173/?view=historical%2Fsegments&line=historical.segments.ai_revenue&column=2025", 1));
  }

  [Fact]
  public void A_libreta_too_old_to_leave_the_page_to_pintura_says_to_update_it()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("book/book.yaml", "name: Acme");
    string libreta = FakeLibreta(folder, """
      #!/bin/sh
      echo "Unknown option '--no-open'." >&2
      echo "usage:" >&2
      exit 1
      """);
    var reader = new LibretaReader(libreta);

    // Act
    BookException error = Should.Throw<BookException>(() => reader.Open(folder.PathTo("book"), "amazon", "2025"));

    // Assert
    error.Message.ShouldBe("this Libreta is too old to open a cell in a tab Pintura holds; update it");
  }

  [Fact]
  public void A_missing_libreta_names_how_to_install_it()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("book/book.yaml", "name: Acme");
    var reader = new LibretaReader(folder.PathTo("no-libreta"));

    // Act
    BookException error = Should.Throw<BookException>(() => reader.Lines(folder.PathTo("book")));

    // Assert
    error.Message.ShouldStartWith($"reading a book needs Libreta, and there is no {folder.PathTo("no-libreta")}");
    error.Message.ShouldContain(OperatingSystem.IsWindows()
      ? "irm https://raw.githubusercontent.com/editide/libreta/main/install.ps1 | iex"
      : "curl -fsSL https://raw.githubusercontent.com/editide/libreta/main/install.sh | sh");
  }

  [Theory]
  [InlineData(500.5, 0, "£501m")]
  [InlineData(346.5, 0, "£347m")]
  [InlineData(-2.5, 0, "(£3m)")]
  [InlineData(0.125, 2, "£0.13m")]
  public void A_reference_rounds_halves_away_from_zero_as_its_chart_labels_and_PowerPoint_do(double value,
    int decimals, string expected)
  {
    // Arrange
    var format = new NumberFormat { Decimals = decimals, Prefix = "£", Suffix = "m" };

    // Act
    string spelled = format.Spell(value);

    // Assert
    spelled.ShouldBe(expected);
  }

  [Fact]
  public void Tracing_a_name_libreta_would_read_as_an_option_runs_no_libreta()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("book/book.yaml", "name: Comps");
    var reader = new LibretaReader(folder.PathTo("no-libreta-here"));

    // Act
    BookException error = Should.Throw<BookException>(() =>
      reader.Open(folder.PathTo("book"), "--export", "/tmp/elsewhere.html"));

    // Assert
    error.Message.ShouldBe("--export in /tmp/elsewhere.html isn't a cell Libreta can open");
  }


  [Fact]
  public void A_view_libreta_would_read_as_an_option_runs_no_libreta()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("book/book.yaml", "name: Comps");
    var reader = new LibretaReader(folder.PathTo("no-libreta-here"));

    // Act
    BookException error = Should.Throw<BookException>(() =>
      reader.Session().View(folder.PathTo("book"), "--export"));

    // Assert
    error.Message.ShouldBe("--export isn't a view Libreta can show");
  }


}