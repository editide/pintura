using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class PictureCacheTests
{
  [Fact]
  public void A_picture_encoded_once_is_read_back_without_encoding_again()
  {
    // Arrange
    using var folder = new TempFolder();
    var cache = new PictureCache(folder.PathTo("pictures"));
    int encodings = 0;
    cache.Get([1, 2, 3], "webp", () => { encodings++; return [9, 8]; });

    // Act
    byte[]? again = new PictureCache(folder.PathTo("pictures")).Get([1, 2, 3], "webp",
      () => { encodings++; return [7]; });

    // Assert
    again.ShouldBe([9, 8]);
    encodings.ShouldBe(1);
  }

  [Fact]
  public void A_picture_with_no_encoding_is_remembered_as_having_none()
  {
    // Arrange
    using var folder = new TempFolder();
    var cache = new PictureCache(folder.PathTo("pictures"));
    int encodings = 0;
    cache.Get([1, 2, 3], "webp", () => { encodings++; return null; });

    // Act
    byte[]? again = cache.Get([1, 2, 3], "webp", () => { encodings++; return [7]; });

    // Assert
    again.ShouldBeNull();
    encodings.ShouldBe(1);
  }

  [Fact]
  public void A_changed_picture_or_encoding_is_encoded_afresh()
  {
    // Arrange
    using var folder = new TempFolder();
    var cache = new PictureCache(folder.PathTo("pictures"));
    cache.Get([1, 2, 3], "webp", () => [9]);

    // Act
    byte[]? edited = cache.Get([1, 2, 4], "webp", () => [8]);
    byte[]? narrower = cache.Get([1, 2, 3], "webp width 700", () => [7]);

    // Assert
    edited.ShouldBe([8]);
    narrower.ShouldBe([7]);
  }

  [Fact]
  public void A_build_with_changed_code_encodes_afresh()
  {
    // Arrange
    using var folder = new TempFolder();
    new PictureCache(folder.PathTo("pictures"), Guid.NewGuid()).Get([1, 2, 3], "webp", () => [9]);

    // Act
    byte[]? rebuilt = new PictureCache(folder.PathTo("pictures"), Guid.NewGuid()).Get([1, 2, 3], "webp", () => [8]);

    // Assert
    rebuilt.ShouldBe([8]);
  }

  [Fact]
  public void An_entry_unused_for_a_month_is_deleted()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("pictures/stale", "");
    folder.Write("pictures/recent", "");
    File.SetLastWriteTimeUtc(folder.PathTo("pictures/stale"), DateTime.UtcNow.AddDays(-31));

    // Act
    new PictureCache(folder.PathTo("pictures")).Get([1, 2, 3], "webp", () => [9]);

    // Assert
    File.Exists(folder.PathTo("pictures/stale")).ShouldBeFalse();
    File.Exists(folder.PathTo("pictures/recent")).ShouldBeTrue();
  }


}