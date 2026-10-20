using System.Globalization;

namespace Pintura;

/// <summary>The slides <c>--slides</c> names for an export or an import, as a comma-separated list of ids, numbers
/// from 1, and ranges such as <c>3-5</c>. Picked slides keep the deck's order, whatever order the list gives.</summary>
public class SlideSelection
{
  private readonly List<string> ids = new List<string>();
  private readonly List<(int First, int Last)> ranges = new List<(int First, int Last)>();

  public static SlideSelection Parse(string list)
  {
    var selection = new SlideSelection();
    foreach (string entry in list.Split(','))
    {
      string token = entry.Trim();
      string[] ends = token.Split('-');
      if (ends.Length == 2 && Number(ends[0]) is int first && Number(ends[1]) is int last)
      {
        if (last < first)
        {
          throw new ArgumentException($"--slides: the range {token} runs backwards.");
        }
        selection.ranges.Add((first, last));
      }
      else if (Number(token) is int number)
      {
        selection.ranges.Add((number, number));
      }
      else if (token.Length == 0 || token.All(character => char.IsDigit(character) || character == '-'))
      {
        throw new ArgumentException($"--slides takes ids, numbers from 1, and ranges such as 3-5, comma-separated; " +
          $"found '{token}' in '{list}'.");
      }
      else
      {
        selection.ids.Add(token);
      }
    }
    return selection;
  }

  /// <summary>The slides of <paramref name="slides"/> the list names, where <paramref name="id"/> gives a slide's id,
  /// or null for a deck whose slides have none.</summary>
  public List<T> Pick<T>(IReadOnlyList<T> slides, Func<T, string?> id)
  {
    foreach ((int _, int last) in ranges.Where(range => range.Last > slides.Count))
    {
      throw new ArgumentException($"no slide {last}; the deck has {slides.Count}");
    }
    List<string?> known = slides.Select(id).ToList();
    foreach (string missing in ids.Where(wanted => !known.Contains(wanted)))
    {
      throw new ArgumentException(known.All(candidate => candidate is null)
        ? $"no slide '{missing}': this deck's slides are picked by number"
        : $"no slide '{missing}'. Available: {string.Join(", ", known.OfType<string>())}");
    }
    return slides.Where((slide, index) => ids.Contains(id(slide) ?? "") ||
      ranges.Any(range => index + 1 >= range.First && index + 1 <= range.Last)).ToList();
  }

  private static int? Number(string text)
  {
    return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > 0
      ? number
      : null;
  }


}
