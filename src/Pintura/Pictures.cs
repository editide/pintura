using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Pintura;

/// <summary>Supported picture formats and their intrinsic pixel sizes, read from file headers.</summary>
public static partial class Pictures
{
  public static string? ContentType(string path)
  {
    return Path.GetExtension(path).ToLowerInvariant() switch
    {
      ".png" => "image/png",
      ".jpg" or ".jpeg" => "image/jpeg",
      ".gif" => "image/gif",
      ".svg" => "image/svg+xml",
      ".webp" => "image/webp",
      _ => null
    };
  }

  /// <summary>The picture's own size, for fitting it inside its frame as the preview does.</summary>
  public static double[]? Intrinsic(string file, IFiles? files = null)
  {
    byte[] bytes = (files ?? DiskFiles.Instance).ReadAllBytes(file);
    if (file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
    {
      string text = Encoding.UTF8.GetString(bytes);
      Match box = ViewBox().Match(text);
      if (box.Success)
      {
        return [Parse(box.Groups[1].Value), Parse(box.Groups[2].Value)];
      }
      Match width = SvgWidth().Match(text);
      Match height = SvgHeight().Match(text);
      return width.Success && height.Success ? [Parse(width.Groups[1].Value), Parse(height.Groups[1].Value)] : null;
    }
    if (bytes.Length >= 24 && BinaryPrimitives.ReadUInt32BigEndian(bytes) == 0x89504e47)
    {
      return [BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)), BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20))];
    }
    if (bytes.Length >= 10 && bytes.AsSpan(0, 3).SequenceEqual("GIF"u8))
    {
      return [BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)), BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8))];
    }
    if (bytes.Length >= 30 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8))
    {
      return WebPSize(bytes);
    }
    return bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8 ? JpegSize(bytes) : null;
  }

  private static double[]? WebPSize(byte[] bytes)
  {
    ReadOnlySpan<byte> chunk = bytes.AsSpan(12, 4);
    if (chunk.SequenceEqual("VP8 "u8))
    {
      return [BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26)) & 0x3fff,
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28)) & 0x3fff];
    }
    if (chunk.SequenceEqual("VP8L"u8))
    {
      uint packed = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(21));
      return [(packed & 0x3fff) + 1, (packed >> 14 & 0x3fff) + 1];
    }
    if (chunk.SequenceEqual("VP8X"u8))
    {
      return [(bytes[24] | bytes[25] << 8 | bytes[26] << 16) + 1, (bytes[27] | bytes[28] << 8 | bytes[29] << 16) + 1];
    }
    return null;
  }

  /// <summary>A JPEG's size, from the first start-of-frame segment; markers C4, C8, and CC share the range but
  /// aren't frames.</summary>
  private static double[]? JpegSize(byte[] bytes)
  {
    int at = 2;
    while (at + 9 <= bytes.Length && bytes[at] == 0xff)
    {
      byte marker = bytes[at + 1];
      if (marker is >= 0xc0 and <= 0xcf and not 0xc4 and not 0xc8 and not 0xcc)
      {
        return [BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at + 7)), BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at + 5))];
      }
      at += 2 + BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at + 2));
    }
    return null;
  }

  private static double Parse(string text)
  {
    return double.Parse(text, CultureInfo.InvariantCulture);
  }

  [GeneratedRegex(@"viewBox=""\s*[-\d.]+[\s,]+[-\d.]+[\s,]+([\d.]+)[\s,]+([\d.]+)")]
  private static partial Regex ViewBox();

  [GeneratedRegex(@"\bwidth=""([\d.]+)")]
  private static partial Regex SvgWidth();

  [GeneratedRegex(@"\bheight=""([\d.]+)")]
  private static partial Regex SvgHeight();


}
