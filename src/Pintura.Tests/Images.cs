using System.Buffers.Binary;

namespace Pintura.Tests;

/// <summary>Just enough of each picture format's header for its size to be read.</summary>
public static class Images
{
  public static byte[] Png(int width, int height)
  {
    byte[] bytes = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, .. new byte[8]];
    BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
    BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
    return bytes;
  }

  /// <summary>A JPEG's start, an APP0 segment ahead of the frame header holding its size.</summary>
  public static byte[] Jpeg(int width, int height)
  {
    return [0xff, 0xd8, 0xff, 0xe0, 0, 4, 0, 0, 0xff, 0xc0, 0, 11, 8, (byte)(height >> 8), (byte)height,
      (byte)(width >> 8), (byte)width, 1, 1, 0x11, 0];
  }

  public static byte[] Gif(int width, int height)
  {
    return [.. "GIF89a"u8, (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0, 0, 0];
  }

  /// <summary>A lossless WebP, whose size is packed into 14 bits each, less one.</summary>
  public static byte[] WebP(int width, int height)
  {
    uint packed = (uint)(width - 1) | (uint)(height - 1) << 14;
    return [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBPVP8L"u8, 0, 0, 0, 0, 0x2f, (byte)packed, (byte)(packed >> 8),
      (byte)(packed >> 16), (byte)(packed >> 24), .. new byte[5]];
  }


}