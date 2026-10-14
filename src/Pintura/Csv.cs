using System.Text;

namespace Pintura;

/// <summary>A CSV row and the line it starts on.</summary>
public record CsvRow(int Line, List<string> Fields);

/// <summary>Reads RFC 4180 CSV: fields with commas, quotes, or line breaks are quoted, and a quote inside one is
/// doubled. Blank lines are skipped.</summary>
public static class Csv
{
  public static List<CsvRow> Parse(string text, Func<int, string, Exception> error)
  {
    var rows = new List<CsvRow>();
    var fields = new List<string>();
    var field = new StringBuilder();
    int line = 1;
    int start = 1;
    bool quoted = false;
    bool wasQuoted = false;

    void EndField()
    {
      fields.Add(wasQuoted ? field.ToString() : field.ToString().Trim());
      field.Clear();
      wasQuoted = false;
    }

    void EndRow()
    {
      bool quotedField = wasQuoted;
      EndField();
      if (fields.Count > 1 || fields[0].Length > 0 || quotedField)
      {
        rows.Add(new CsvRow(start, fields));
      }
      fields = new List<string>();
    }

    for (int index = 0; index < text.Length; index++)
    {
      char current = text[index];
      if (quoted)
      {
        if (current == '"' && index + 1 < text.Length && text[index + 1] == '"')
        {
          field.Append('"');
          index++;
        }
        else if (current == '"')
        {
          quoted = false;
        }
        else
        {
          line += current == '\n' ? 1 : 0;
          field.Append(current);
        }
        continue;
      }
      switch (current)
      {
        case '"' when field.ToString().Trim().Length == 0:
          field.Clear();
          quoted = true;
          wasQuoted = true;
          break;
        case '"':
          throw error(line, "a quote inside an unquoted field; quote the whole field and double the quote");
        case ',':
          EndField();
          break;
        case '\r':
          break;
        case '\n':
          EndRow();
          line++;
          start = line;
          break;
        default:
          field.Append(current);
          break;
      }
    }
    if (quoted)
    {
      throw error(start, "a quoted field is never closed");
    }
    if (field.Length > 0 || fields.Count > 0)
    {
      EndRow();
    }
    return rows;
  }


}