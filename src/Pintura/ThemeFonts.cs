using System.Buffers.Binary;
using System.Text;

namespace Pintura;

/// <summary>A font file a theme ships, as a family the page can ask for at a weight and slant.</summary>
public record FontFace(string Family, int Weight, bool Italic, string Path);

/// <summary>Reads the faces in a theme's <c>fonts/</c> under the names PowerPoint and Windows know them by.</summary>
public static class ThemeFonts
{
  /// <summary>Each file answers to its family at its weight, and, for a weight beyond regular and bold, to the
  /// Windows family that names the weight, such as "IBM Plex Sans Medm", which a slide may write instead.</summary>
  public static List<FontFace> Read(string folder)
  {
    var faces = new List<FontFace>();
    if (!Directory.Exists(folder))
    {
      return faces;
    }
    foreach (string path in Directory.GetFiles(folder).Where(path => ContentType(path) is not null)
      .Order(StringComparer.Ordinal))
    {
      if (Names(File.ReadAllBytes(path)) is not var (family, typographic, weight, bold, italic))
      {
        continue;
      }
      faces.Add(new FontFace(typographic ?? family, weight, italic, path));
      if (typographic is not null && typographic != family)
      {
        faces.Add(new FontFace(family, bold ? 700 : 400, italic, path));
      }
    }
    return faces.DistinctBy(face => (face.Family, face.Weight, face.Italic)).ToList();
  }

  public static string? ContentType(string path)
  {
    return Path.GetExtension(path).ToLowerInvariant() switch
    {
      ".ttf" => "font/ttf",
      ".otf" => "font/otf",
      _ => null
    };
  }

  /// <summary>The Windows family, the typographic family when the file names one, and the OS/2 weight and style;
  /// null for a file that isn't an OpenType font.</summary>
  private static (string Family, string? Typographic, int Weight, bool Bold, bool Italic)? Names(byte[] file)
  {
    try
    {
      ReadOnlySpan<byte> bytes = file;
      uint version = BinaryPrimitives.ReadUInt32BigEndian(bytes);
      if (version is not (0x00010000 or 0x4F54544F or 0x74727565))
      {
        return null;
      }
      ReadOnlySpan<byte> name = default;
      ReadOnlySpan<byte> os2 = default;
      int tables = BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]);
      for (int index = 0; index < tables; index++)
      {
        ReadOnlySpan<byte> record = bytes.Slice(12 + 16 * index, 16);
        int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
        int length = (int)BinaryPrimitives.ReadUInt32BigEndian(record[12..]);
        switch (Encoding.ASCII.GetString(record[..4]))
        {
          case "name":
            name = bytes.Slice(offset, length);
            break;
          case "OS/2":
            os2 = bytes.Slice(offset, length);
            break;
        }
      }
      if (Name(name, 1) is not string family)
      {
        return null;
      }
      int weight = os2.Length >= 6 ? BinaryPrimitives.ReadUInt16BigEndian(os2[4..]) : 400;
      int selection = os2.Length >= 64 ? BinaryPrimitives.ReadUInt16BigEndian(os2[62..]) : 0;
      return (family, Name(name, 16), weight is 0 ? 400 : weight, (selection & 1 << 5) != 0,
        (selection & (1 | 1 << 9)) != 0);
    }
    catch (ArgumentOutOfRangeException)
    {
      return null;
    }
  }

  /// <summary>The name in Windows' English, Unicode records, which is what PowerPoint shows.</summary>
  private static string? Name(ReadOnlySpan<byte> table, int id)
  {
    if (table.Length < 6)
    {
      return null;
    }
    int count = BinaryPrimitives.ReadUInt16BigEndian(table[2..]);
    int strings = BinaryPrimitives.ReadUInt16BigEndian(table[4..]);
    string? fallback = null;
    for (int index = 0; index < count; index++)
    {
      ReadOnlySpan<byte> record = table.Slice(6 + 12 * index, 12);
      int platform = BinaryPrimitives.ReadUInt16BigEndian(record);
      int encoding = BinaryPrimitives.ReadUInt16BigEndian(record[2..]);
      int language = BinaryPrimitives.ReadUInt16BigEndian(record[4..]);
      if (BinaryPrimitives.ReadUInt16BigEndian(record[6..]) != id || platform != 3 || encoding is not (1 or 10))
      {
        continue;
      }
      ReadOnlySpan<byte> text = table.Slice(strings + BinaryPrimitives.ReadUInt16BigEndian(record[10..]),
        BinaryPrimitives.ReadUInt16BigEndian(record[8..]));
      string value = Encoding.BigEndianUnicode.GetString(text);
      if (language == 0x409)
      {
        return value;
      }
      fallback ??= value;
    }
    return fallback;
  }


}