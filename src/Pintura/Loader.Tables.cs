using System.Globalization;

namespace Pintura;

public partial class PresentationLoader
{
  private void LoadTable(string stem)
  {
    if (tables.ContainsKey(stem))
    {
      return;
    }
    string path = Path.Combine(root, "tables", $"{stem}.yaml");
    Node document = File(path).Load("tables");
    MapNode map = document.AsMap();
    var origins = new Dictionary<string, Origin>();

    Node dataNode = map.Require("data");
    string data = dataNode.IsMap ? "" : dataNode.AsString();
    origins["data"] = dataNode.Origin;
    ViewData? view = null;
    List<CsvRow> rows = [];
    SourceFile? csv = null;
    if (dataNode.IsMap)
    {
      view = ReadViewData(dataNode);
    }
    else
    {
      string csvPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, data));
      if (!files.Exists(csvPath))
      {
        throw dataNode.Error($"no CSV at {data}; paths are relative to the file naming them");
      }
      RequireCsv(dataNode, data, csvPath);
      csv = File(csvPath);
      rows = ReadTableData(csv);
    }
    List<List<string>> values = view?.Values ?? rows.Select(row => row.Fields).ToList();
    int rowCount = values.Count;
    int columnCount = values[0].Count;

    TableStyle? style = null;
    string? styleName = null;
    if (map.Take("style") is Node styleNode)
    {
      styleName = styleNode.AsString();
      style = styles.Tables.GetValueOrDefault(styleName) ?? throw styleNode.Error(styles.Tables.Count == 0
        ? $"unknown table style {styleName}; the theme has none"
        : $"unknown table style {styleName}; the theme's are {string.Join(", ", styles.Tables.Keys)}");
      origins["style"] = styleNode.Origin;
    }

    int headerRows = view is null ? 0 : 1;
    if (map.Take("header_rows") is Node headerNode)
    {
      headerRows = headerNode.AsInteger() is int count and >= 0 && count <= rowCount
        ? count
        : throw headerNode.Error($"header_rows runs from 0 to the table's {rowCount} rows");
      origins["header_rows"] = headerNode.Origin;
    }

    var columns = new List<TableColumn>();
    if (map.Take("columns") is Node columnsNode)
    {
      List<Node> entries = columnsNode.AsList();
      if (entries.Count > columnCount)
      {
        throw columnsNode.Error($"columns lists {entries.Count} columns, and {view?.View ?? data} has {columnCount}");
      }
      foreach (Node entry in entries)
      {
        MapNode column = entry.AsMap();
        columns.Add((TableColumn)ReadTableFormatting(column, "column", styles.Styles, styles.Formats));
        column.Done();
      }
    }

    var tableRows = new Dictionary<int, TableRow>();
    if (map.Take("rows") is Node rowsNode)
    {
      foreach ((string key, Node value) in rowsNode.AsMap().Entries())
      {
        if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 1)
        {
          throw value.Error($"rows are keyed by row number, from 1 with the header; {key} isn't one");
        }
        RowInRange(value, number, rowCount);
        MapNode row = value.AsMap();
        var formatting = (TableRow)ReadTableFormatting(row, "row", styles.Styles, styles.Formats);
        row.Done();
        if (formatting.Style is string rowStyle && style?.Rows.ContainsKey(rowStyle) != true)
        {
          Origin at = formatting.Origins["style"];
          throw new LoadException(style is null
            ? $"row style {rowStyle} needs a table style; name one with style"
            : $"unknown row style {rowStyle}; table style {styleName} has {And([.. style.Rows.Keys])}", at,
            "tables");
        }
        tableRows[number] = formatting;
      }
    }

    var cells = new List<TableCell>();
    if (map.Take("cells") is Node cellsNode)
    {
      var seen = new HashSet<(int Row, int Column)>();
      foreach (Node entry in cellsNode.AsList())
      {
        MapNode cell = entry.AsMap();
        var formatting = (TableCell)ReadTableFormatting(cell, "cell", styles.Styles, styles.Formats);
        cell.Done();
        RowInRange(entry, formatting.Row, rowCount);
        ColumnInRange(entry, formatting.Column, columnCount);
        if (!seen.Add((formatting.Row, formatting.Column)))
        {
          throw entry.Error($"row {formatting.Row}, column {formatting.Column} is listed more than once in cells");
        }
        cells.Add(formatting);
      }
    }

    List<TableMerge> merges = map.Take("merge") is Node mergeNode
      ? ReadMerges(mergeNode, values)
      : new List<TableMerge>();
    map.Done();

    List<List<List<Paragraph>>> paragraphs;
    if (view is null)
    {
      MarkupContext context = Markup();
      paragraphs = rows
        .Select(row => row.Fields
          .Select(field => Pintura.Markup.Parse(field, _ => new Origin(csv!.Display, row.Line, 1), context))
          .ToList())
        .ToList();
    }
    else
    {
      // A view's labels and numbers are text as Libreta wrote them, never markup.
      paragraphs = values
        .Select(row => row
          .Select(value => new List<Paragraph>
          {
            new Paragraph
            {
              Runs = value.Length == 0 ? [] : [new Run { Text = value, Origin = dataNode.Origin }],
              Origin = dataNode.Origin
            }
          })
          .ToList())
        .ToList();
      for (int row = 0; row < view.Styles.Count; row++)
      {
        if (view.Styles[row] is not string role || style?.Rows.ContainsKey(role) != true)
        {
          continue;
        }
        // A row entry without a style of its own, such as one setting a height, keeps the role's row style.
        if (tableRows.TryGetValue(row + 1, out TableRow? written))
        {
          if (written.Style is null)
          {
            written.Style = role;
            written.Origins["style"] = dataNode.Origin;
          }
        }
        else
        {
          tableRows[row + 1] = new TableRow { Style = role, Origins = { ["style"] = dataNode.Origin } };
        }
      }
    }

    tables[stem] = new Table
    {
      Id = stem,
      Style = styleName,
      Data = data,
      View = view?.View,
      Exact = view?.Exact,
      Sources = view?.Sources,
      Indents = view?.Indents,
      HeaderRows = headerRows,
      Columns = columns,
      Rows = tableRows,
      Cells = cells,
      Merges = merges,
      Values = values,
      Paragraphs = paragraphs,
      Origin = document.Origin,
      Origins = origins
    };
  }

  /// <summary>A view as a table's rows: a header of column labels, then each row's label and displayed values, with
  /// the row's role in <c>Styles</c> for a table style to match by name.</summary>
  private record ViewData(string View, List<List<string>> Values, List<List<string?>> Exact,
    List<List<(string Line, string Column)?>> Sources, List<string?> Styles, List<int> Indents);

  /// <summary>A revision draws a column or line the view no longer has as <c>#REF!</c> cells rather than failing.
  /// </summary>
  private ViewData ReadViewData(Node node)
  {
    MapNode map = node.WithTopic("books").AsMap();
    Node viewNode = map.Require("view");
    string text = viewNode.AsString();
    int split = text.IndexOf("::", StringComparison.Ordinal);
    if (split < 0)
    {
      throw viewNode.Error($"write the view after its book's id, such as acme::{text}");
    }
    string id = text[..split];
    string viewId = text[(split + 2)..];
    Book book = FindBook(id) ?? throw viewNode.Error(UnknownBook(id));
    string corner = map.Take("label")?.AsString() ?? "";
    Node? linesNode = map.Take("lines");
    Node? columnsNode = map.Take("columns");
    map.Done();
    BookView view;
    try
    {
      view = reader.View(book.Root, viewId);
    }
    catch (BookException exception)
    {
      throw viewNode.Error($"view {viewId} can't be read from book {id}: {exception.Message}");
    }

    var columns = new List<int>();
    var headers = new List<string>();
    foreach (Node entry in columnsNode?.AsList() ?? [])
    {
      Node columnNode = entry;
      string? label = null;
      if (entry.IsMap)
      {
        MapNode columnMap = entry.AsMap();
        columnNode = columnMap.Require("column");
        label = columnMap.Require("label").AsString();
        columnMap.Done();
      }
      string name = columnNode.AsString();
      int index = view.Columns.FindIndex(column => column.Name == name || column.Label == name);
      if (index < 0 && files is not GitTree)
      {
        throw columnNode.Error($"view {viewId} has no column {name}; its columns are " +
          $"{And([.. view.Columns.Select(column => column.Label)])}");
      }
      columns.Add(index);
      headers.Add(label ?? (index >= 0 ? view.Columns[index].Label : name));
    }
    if (columnsNode is null)
    {
      columns.AddRange(Enumerable.Range(0, view.Columns.Count));
      headers.AddRange(view.Columns.Select(column => column.Label));
    }

    var rows = new List<BookRow>();
    foreach (Node entry in linesNode?.AsList() ?? [])
    {
      Node lineNode = entry;
      string? label = null;
      if (entry.IsMap)
      {
        MapNode lineMap = entry.AsMap();
        lineNode = lineMap.Require("line");
        label = lineMap.Require("label").AsString();
        lineMap.Done();
      }
      string name = lineNode.AsString();
      List<BookRow> matches = view.Rows
        .Where(row => row.Name is not null &&
          (row.Name == name || row.Name.EndsWith($".{name}", StringComparison.Ordinal)))
        // A view showing a line twice, as in a bridge that adds it back, shows the same values both times.
        .DistinctBy(row => row.Name)
        .ToList();
      rows.Add(matches.Count switch
      {
        1 => label is null ? matches[0] : matches[0] with { Label = label },
        0 when files is GitTree => new BookRow(null, label ?? name, "fact", null, 0, null,
          [.. view.Columns.Select(_ => Unresolved)]),
        0 => throw lineNode.Error($"view {viewId} shows no line {name}"),
        _ => throw lineNode.Error($"{name} is {And([.. matches.Select(row => row.Name!)])} in view {viewId}; " +
          "name the one you mean in full")
      });
    }
    if (linesNode is null)
    {
      rows.AddRange(view.Rows.Where(row => row.Kind != "spacer"));
    }

    List<List<string>> values = [[corner, .. headers]];
    List<List<string?>> exact = [[null, .. columns.Select(_ => (string?)null)]];
    List<List<(string, string)?>> sources = [[null, .. columns.Select(_ => ((string, string)?)null)]];
    List<string?> roles = [null];
    List<int> indents = [0];
    foreach (BookRow row in rows)
    {
      values.Add([row.Label ?? "", .. columns.Select(column => column < 0 ? Unresolved : row.Display?[column] ?? "")]);
      exact.Add([null, .. columns.Select(column => column < 0 ? null : row.Exact?[column])]);
      sources.Add([null, .. columns.Select(column => column < 0 ? null : row.Cells is not null
        ? (row.Cells[column].Line, row.Cells[column].Column)
        : row.Name is null
          ? ((string, string)?)null
          : (row.Name, view.Columns[column].Name))]);
      roles.Add(row.Style);
      indents.Add(row.Indent);
    }
    return new ViewData(text, values, exact, sources, roles, indents);
  }

  private List<CsvRow> ReadTableData(SourceFile file)
  {
    LoadException Error(int line, string problem)
    {
      return new LoadException(problem, new Origin(file.Display, line, 1), "tables");
    }
    List<CsvRow> rows = Csv.Parse(file.ReadText("tables"), Error);
    if (rows.Count == 0)
    {
      throw Error(1, "a table's CSV needs at least one row");
    }
    int width = rows[0].Fields.Count;
    foreach (CsvRow row in rows.Skip(1).Where(row => row.Fields.Count != width))
    {
      throw Error(row.Line, $"the row has {row.Fields.Count} cells and the first row {width}; leave a cell empty " +
        "rather than out");
    }
    return rows;
  }

  private static List<TableMerge> ReadMerges(Node node, List<List<string>> values)
  {
    var merges = new List<TableMerge>();
    foreach (Node entry in node.AsList())
    {
      MapNode map = entry.AsMap();
      int row = map.Require("row").AsInteger();
      int column = map.Require("column").AsInteger();
      int rows = map.Take("rows") is Node rowsNode ? AtLeastOne(rowsNode) : 1;
      int columns = map.Take("columns") is Node columnsNode ? AtLeastOne(columnsNode) : 1;
      map.Done();
      RowInRange(entry, row + rows - 1, values.Count);
      ColumnInRange(entry, column + columns - 1, values[0].Count);
      var merge = new TableMerge { Row = row, Column = column, Rows = rows, Columns = columns, Origin = entry.Origin };
      TableMerge? overlapped = merges.FirstOrDefault(other =>
        other.Row < row + rows && row < other.Row + other.Rows &&
        other.Column < column + columns && column < other.Column + other.Columns);
      if (overlapped is not null)
      {
        throw entry.Error($"this merge overlaps the one from row {overlapped.Row}, column {overlapped.Column}");
      }
      for (int covered = row; covered < row + rows; covered++)
      {
        for (int across = column; across < column + columns; across++)
        {
          string value = values[covered - 1][across - 1];
          if ((covered, across) != (row, column) && value.Length > 0)
          {
            throw entry.Error($"the merge covers row {covered}, column {across}, which holds {value}; a merge shows " +
              "its first cell, so leave the rest empty in the CSV");
          }
        }
      }
      merges.Add(merge);
    }
    return merges;
  }

  /// <summary>Libreta gives a line no columns when its folder has neither periods nor a parent's to inherit, as
  /// befalls a folder of formulas whose view.yaml is deleted.</summary>
  private static string NoColumn(BookLine line, string column)
  {
    return line.Columns.Count == 0
      ? $"{line.Name} has no column {column}, nor any other; a folder of formulas alone names its columns in its " +
        "view.yaml, as columns: [Value] does"
      : $"{line.Name} has no column {column}; its columns are {And([.. line.Columns])}";
  }

  private static string And(string[] names)
  {
    return names.Length == 1 ? names[0] : $"{string.Join(", ", names[..^1])} and {names[^1]}";
  }

  private static int AtLeastOne(Node node)
  {
    return node.AsInteger() is int count and >= 1 ? count : throw node.Error($"{node.Name} must be 1 or more");
  }

  private static void RowInRange(Node node, int row, int rows)
  {
    if (row < 1 || row > rows)
    {
      throw node.Error($"row {row} is past the table's {rows} rows; rows are numbered from 1, header included");
    }
  }

  private static void ColumnInRange(Node node, int column, int columns)
  {
    if (column < 1 || column > columns)
    {
      throw node.Error($"column {column} is past the table's {columns} columns; columns are numbered from 1");
    }
  }


}
