using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Pintura.Tests;

public class ServerInstanceTests
{
  [Fact]
  public void A_headless_server_starts_holding_none_of_the_commands_standard_streams()
  {
    // Arrange
    using var folder = new TempFolder();

    // Act
    System.Diagnostics.ProcessStartInfo start = HeadlessServer.StartInfo(folder.PathTo("deck"), folder.Root);

    // Assert
    start.RedirectStandardInput.ShouldBeFalse();
    start.RedirectStandardOutput.ShouldBeFalse();
    start.RedirectStandardError.ShouldBeFalse();
    if (OperatingSystem.IsWindows())
    {
      start.UseShellExecute.ShouldBeTrue();
    }
    else
    {
      start.FileName.ShouldBe("/bin/sh");
      start.ArgumentList[1].ShouldContain("</dev/null");
    }
  }

  [Fact]
  public void A_server_is_found_by_its_process_in_the_registry()
  {
    // Arrange
    using var folder = new TempFolder();
    var registry = new InstanceRegistry(folder.PathTo("servers"));
    var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    registry.Register(new ServerInstance(Environment.ProcessId, folder.Root, port, true, ServerInstance.CurrentBuild));

    // Act
    ServerInstance? found = registry.Find(Environment.ProcessId);
    ServerInstance? other = registry.Find(Environment.ProcessId + 1);
    listener.Stop();

    // Assert
    found.ShouldNotBeNull().Url.ShouldBe($"http://127.0.0.1:{port}");
    other.ShouldBeNull();
  }

  [Fact]
  public async Task A_preview_running_another_build_isnt_borrowed_to_draw()
  {
    // Arrange
    using var folder = new TempFolder();
    var registry = new InstanceRegistry(folder.PathTo("servers"));
    var preview = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
    var headless = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
    preview.Start();
    headless.Start();
    int previewPort = ((IPEndPoint)preview.LocalEndpoint).Port;
    int headlessPort = ((IPEndPoint)headless.LocalEndpoint).Port;
    // The registry trusts only a live process; git waits on its open input on every system.
    using System.Diagnostics.Process server = System.Diagnostics.Process.Start(
      new System.Diagnostics.ProcessStartInfo("git", "hash-object --stdin")
      {
        RedirectStandardInput = true,
        RedirectStandardOutput = true
      })!;
    registry.Register(new ServerInstance(Environment.ProcessId, folder.Root, previewPort, false, "older"));
    registry.Register(new ServerInstance(server.Id, folder.Root, headlessPort, true, ServerInstance.CurrentBuild));

    // Act
    string url = await HeadlessServer.Find(folder.Root, registry);
    preview.Stop();
    headless.Stop();
    server.StandardInput.Close();
    server.WaitForExit();

    // Assert
    url.ShouldBe($"http://127.0.0.1:{headlessPort}");
  }

  [Fact]
  public void A_preview_instance_uses_localhost_and_a_headless_one_uses_loopback_ip()
  {
    // Act
    var preview = new ServerInstance(1, "/deck", 5000, Headless: false);
    var headless = new ServerInstance(2, "/deck", 5001, Headless: true);

    // Assert
    preview.Url.ShouldBe("http://localhost:5000");
    headless.Url.ShouldBe("http://127.0.0.1:5001");
  }

  [Fact]
  public void Stop_does_not_kill_a_process_whose_start_time_does_not_match()
  {
    // Arrange
    using var folder = new TempFolder();
    var registry = new InstanceRegistry(folder.PathTo("servers"));
    var stale = new ServerInstance(Environment.ProcessId, folder.Root, 5555, Headless: true, Build: "older",
      Started: 1);
    registry.Register(stale);

    // Act
    HeadlessServer.Stop(stale, registry);

    // Assert: this process survived and the stale record was removed.
    registry.Find(Environment.ProcessId).ShouldBeNull();
  }

  [Fact]
  public void Viewers_retire_waits_for_active_requests_then_stops_immediately()
  {
    // Arrange
    int stopped = 0;
    using var viewers = new Viewers(TimeSpan.FromMinutes(5), () => stopped++);
    IDisposable active = viewers.Connect();

    // Act
    viewers.Retire();
    int whileActive = stopped;
    active.Dispose();

    // Assert
    whileActive.ShouldBe(0);
    stopped.ShouldBe(1);
  }

  [Fact]
  public async Task Viewers_stops_after_grace_when_expected_page_never_connects()
  {
    // Arrange
    var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var viewers = new Viewers(TimeSpan.FromMilliseconds(40), () => stopped.TrySetResult());

    // Act
    viewers.Expect();

    // Assert
    await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
  }

  [Fact]
  public async Task Viewers_stops_after_last_viewer_leaves_and_reconnect_cancels_the_countdown()
  {
    // Arrange
    var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var viewers = new Viewers(TimeSpan.FromMilliseconds(80), () => stopped.TrySetResult());
    IDisposable first = viewers.Connect();

    // Act: disconnecting and reconnecting within grace keeps the server alive; double-disposing doesn't undercount.
    first.Dispose();
    first.Dispose();
    IDisposable second = viewers.Connect();
    await Task.Delay(120);
    bool stoppedWhileConnected = stopped.Task.IsCompleted;
    second.Dispose();

    // Assert
    stoppedWhileConnected.ShouldBeFalse();
    await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
  }

  [Fact]
  public void Browser_picks_the_right_opener_for_each_platform()
  {
    // Assert
    Browser.Openers(isWindows: true, isMacOs: false, wslDistro: null).ShouldBe(["explorer.exe"]);
    Browser.Openers(isWindows: false, isMacOs: true, wslDistro: null).ShouldBe(["open"]);
    Browser.Openers(isWindows: false, isMacOs: false, wslDistro: "Ubuntu").ShouldBe(["wslview", "explorer.exe"]);
    Browser.Openers(isWindows: false, isMacOs: false, wslDistro: null).ShouldBe(["xdg-open"]);
  }

  [Fact]
  public async Task A_headless_server_that_stops_before_registering_names_its_log()
  {
    // Arrange
    using var folder = new TempFolder();
    var registry = new InstanceRegistry(folder.PathTo("servers"));
    using System.Diagnostics.Process process = System.Diagnostics.Process.Start(
      new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--version")
      {
        RedirectStandardOutput = true
      })!;

    // Act
    InvalidDataException error = await Should.ThrowAsync<InvalidDataException>(() =>
      HeadlessServer.WaitForRegistration(process, registry, folder.PathTo("server.log")));

    // Assert
    error.Message.ShouldContain(folder.PathTo("server.log"));
  }




}
