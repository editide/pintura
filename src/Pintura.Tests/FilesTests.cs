using System.Text;
using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class FilesTests
{
  [Fact]
  public void Replacing_a_file_writes_it_whole_and_leaves_nothing_beside_it()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("slides/one.yaml", "title: Before");

    // Act
    Files.Replace(folder.PathTo("slides/one.yaml"), Encoding.UTF8.GetBytes("title: After\n"));

    // Assert
    File.ReadAllText(folder.PathTo("slides/one.yaml")).ShouldBe("title: After\n");
    Directory.GetFiles(folder.PathTo("slides")).ShouldBe([folder.PathTo("slides/one.yaml")]);
  }

  [Fact]
  public async Task A_file_being_replaced_reads_as_before_or_after_and_never_cut_short()
  {
    if (OperatingSystem.IsWindows())
    {
      // Windows refuses a rename over a file a reader holds open, which the retries absorb, but a test that races
      // them would only measure the retries.
      return;
    }

    // Arrange
    using var folder = new TempFolder();
    string path = folder.PathTo("slide.yaml");
    byte[] before = Encoding.UTF8.GetBytes(new string('a', 64 * 1024));
    byte[] after = Encoding.UTF8.GetBytes(new string('b', 64 * 1024));
    File.WriteAllBytes(path, before);
    var seen = new HashSet<int>();
    using var done = new CancellationTokenSource();
    var reading = new TaskCompletionSource();

    // Act
    Task reader = Task.Run(() =>
    {
      while (!done.IsCancellationRequested)
      {
        seen.Add(File.ReadAllBytes(path).Length);
        reading.TrySetResult();
      }
    });
    // A slow machine can finish every write before the reader first runs, leaving it nothing to race.
    await reading.Task;
    for (int write = 0; write < 200; write++)
    {
      Files.Replace(path, write % 2 == 0 ? after : before);
    }
    done.Cancel();
    await reader;

    // Assert
    seen.ShouldBe([64 * 1024]);
  }

  [Fact]
  public void A_replaced_file_keeps_its_permissions()
  {
    if (OperatingSystem.IsWindows())
    {
      return;
    }

    // Arrange
    using var folder = new TempFolder();
    folder.Write("comments.yaml", "[]");
    File.SetUnixFileMode(folder.PathTo("comments.yaml"), UnixFileMode.UserRead | UnixFileMode.UserWrite);

    // Act
    Files.Replace(folder.PathTo("comments.yaml"), Encoding.UTF8.GetBytes("- id: a1\n"));

    // Assert
    File.GetUnixFileMode(folder.PathTo("comments.yaml")).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
  }

  [Fact]
  public void A_linked_file_stays_a_link_and_the_file_it_links_to_is_replaced()
  {
    if (OperatingSystem.IsWindows())
    {
      // Making a link on Windows needs developer mode or an administrator.
      return;
    }

    // Arrange
    using var folder = new TempFolder();
    folder.Write("shared/one.yaml", "title: Before");
    Directory.CreateDirectory(folder.PathTo("deck/slides"));
    File.CreateSymbolicLink(folder.PathTo("deck/slides/one.yaml"), folder.PathTo("shared/one.yaml"));

    // Act
    Files.Replace(folder.PathTo("deck/slides/one.yaml"), Encoding.UTF8.GetBytes("title: After\n"));

    // Assert
    new FileInfo(folder.PathTo("deck/slides/one.yaml")).LinkTarget.ShouldNotBeNull();
    File.ReadAllText(folder.PathTo("shared/one.yaml")).ShouldBe("title: After\n");
    Directory.GetFiles(folder.PathTo("shared")).ShouldBe([folder.PathTo("shared/one.yaml")]);
  }

  [Fact]
  public void A_file_that_does_not_exist_yet_is_created()
  {
    // Arrange
    using var folder = new TempFolder();

    // Act
    Files.Replace(folder.PathTo("comments.yaml"), Encoding.UTF8.GetBytes("- id: a1\n"));

    // Assert
    File.ReadAllText(folder.PathTo("comments.yaml")).ShouldBe("- id: a1\n");
    Directory.GetFiles(folder.Root).ShouldBe([folder.PathTo("comments.yaml")]);
  }

  [Fact]
  public void A_file_in_a_folder_that_takes_no_new_files_is_written_in_place()
  {
    if (OperatingSystem.IsWindows())
    {
      // A Windows folder's read-only flag doesn't stop new files.
      return;
    }

    // Arrange
    using var folder = new TempFolder();
    folder.Write("slides/one.yaml", "title: Before");
    string slides = folder.PathTo("slides");
    UnixFileMode mode = File.GetUnixFileMode(slides);
    File.SetUnixFileMode(slides, UnixFileMode.UserRead | UnixFileMode.UserExecute);

    // Act
    try
    {
      Files.Replace(folder.PathTo("slides/one.yaml"), Encoding.UTF8.GetBytes("title: After\n"));
    }
    finally
    {
      File.SetUnixFileMode(slides, mode);
    }

    // Assert
    File.ReadAllText(folder.PathTo("slides/one.yaml")).ShouldBe("title: After\n");
    Directory.GetFiles(slides).ShouldBe([folder.PathTo("slides/one.yaml")]);
  }

  [Fact]
  public void A_file_another_program_holds_open_is_written_in_place()
  {
    if (!OperatingSystem.IsWindows())
    {
      // Elsewhere a rename replaces a file whoever holds it open.
      return;
    }

    // Arrange
    using var folder = new TempFolder();
    folder.Write("slides/one.yaml", "title: Before");

    // Act
    using (new FileStream(folder.PathTo("slides/one.yaml"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
    {
      Files.Replace(folder.PathTo("slides/one.yaml"), Encoding.UTF8.GetBytes("title: After\n"));
    }

    // Assert
    File.ReadAllText(folder.PathTo("slides/one.yaml")).ShouldBe("title: After\n");
    Directory.GetFiles(folder.PathTo("slides")).ShouldBe([folder.PathTo("slides/one.yaml")]);
  }

  [Fact]
  public void A_read_only_file_is_refused_as_writing_it_would_be()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("comments.yaml", "[]");
    string path = folder.PathTo("comments.yaml");
    if (OperatingSystem.IsWindows())
    {
      File.SetAttributes(path, FileAttributes.ReadOnly);
    }
    else
    {
      File.SetUnixFileMode(path, UnixFileMode.UserRead);
    }

    // Act
    Should.Throw<UnauthorizedAccessException>(() => Files.Replace(path, Encoding.UTF8.GetBytes("- id: a1\n")));

    // Assert
    File.ReadAllText(path).ShouldBe("[]\n");
  }


  [Fact]
  public void A_file_whose_new_contents_fail_to_write_is_left_as_it_was()
  {
    // Arrange
    using var folder = new TempFolder();
    folder.Write("slides/one.yaml", "title: Before");

    // Act
    Should.Throw<IOException>(() => Files.Replace(folder.PathTo("slides/one.yaml"), stream =>
    {
      stream.Write(Encoding.UTF8.GetBytes("title: Af"));
      throw new IOException("No space left on device");
    }));

    // Assert
    File.ReadAllText(folder.PathTo("slides/one.yaml")).ShouldBe("title: Before\n");
    Directory.GetFiles(folder.PathTo("slides")).ShouldBe([folder.PathTo("slides/one.yaml")]);
  }


}