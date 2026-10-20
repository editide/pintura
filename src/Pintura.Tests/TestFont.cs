using System.Buffers.Binary;
using System.Text;

namespace Pintura.Tests;

/// <summary>A font file holding only the name and OS/2 tables, which is all a theme's fonts are read for.</summary>
public static class TestFont
{
  /// <summary>A face whose Windows family is <paramref name="family"/>, grouped under <paramref name="typographic"/>
  /// when it differs, as a weight beyond regular and bold is.</summary>
  public static byte[] Bytes(string family, int weight, string? typographic = null, bool bold = false,
    bool italic = false)
  {
    var names = new List<(int Id, string Value)> { (1, family), (2, bold ? italic ? "Bold Italic" : "Bold" :
      italic ? "Italic" : "Regular") };
    if (typographic is not null)
    {
      names.Add((16, typographic));
    }
    byte[] name = NameTable(names);
    byte[] os2 = new byte[78];
    BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(4), (ushort)weight);
    ushort selection = (ushort)((italic ? 1 : 0) | (bold ? 1 << 5 : 0) | (!bold && !italic ? 1 << 6 : 0));
    BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(62), selection);

    (string Tag, byte[] Data)[] tables = [("OS/2", os2), ("name", name)];
    var file = new MemoryStream();
    Write(file, 0x00010000u);
    Write(file, (ushort)tables.Length);
    Write(file, (ushort)0);
    Write(file, (ushort)0);
    Write(file, (ushort)0);
    int offset = 12 + 16 * tables.Length;
    foreach ((string tag, byte[] data) in tables)
    {
      file.Write(Encoding.ASCII.GetBytes(tag));
      Write(file, 0u);
      Write(file, (uint)offset);
      Write(file, (uint)data.Length);
      offset += data.Length;
    }
    foreach ((_, byte[] data) in tables)
    {
      file.Write(data);
    }
    return file.ToArray();
  }

  private static byte[] NameTable(List<(int Id, string Value)> names)
  {
    var table = new MemoryStream();
    Write(table, (ushort)0);
    Write(table, (ushort)names.Count);
    Write(table, (ushort)(6 + 12 * names.Count));
    var strings = new MemoryStream();
    foreach ((int id, string value) in names)
    {
      byte[] text = Encoding.BigEndianUnicode.GetBytes(value);
      Write(table, (ushort)3);
      Write(table, (ushort)1);
      Write(table, (ushort)0x409);
      Write(table, (ushort)id);
      Write(table, (ushort)text.Length);
      Write(table, (ushort)strings.Length);
      strings.Write(text);
    }
    table.Write(strings.ToArray());
    return table.ToArray();
  }

  private static void Write(Stream stream, ushort value)
  {
    Span<byte> bytes = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
    stream.Write(bytes);
  }

  private static void Write(Stream stream, uint value)
  {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
    stream.Write(bytes);
  }


}