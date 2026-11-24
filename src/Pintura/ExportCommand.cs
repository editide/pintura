namespace Pintura;

/// <summary><c>pintura --export-html</c> and <c>pintura --export</c>: standalone web page and PowerPoint deck
/// exports.</summary>
public static class ExportCommand
{
  public static int Html(string root, string output, TextWriter writer)
  {
    string file = Path.GetFullPath(output);
    Presentation presentation = PresentationLoader.Load(root);
    File.WriteAllText(file, HtmlExport.Build(presentation, PreviewServer.InterfaceFolder()));
    writer.WriteLine(file);
    if (HtmlExport.Warnings(presentation) is [_, ..] warnings)
    {
      writer.WriteLine();
      writer.WriteLine("what the page can't carry:");
      foreach (string warning in warnings)
      {
        writer.WriteLine($"- {warning}");
      }
    }
    return 0;
  }

  public static int Pptx(string root, string output, SlideSelection? only, TextWriter writer, TextWriter error,
    bool interactive)
  {
    Presentation presentation = PresentationLoader.Load(root);
    Action<double>? progress = interactive
      ? fraction => error.Write($"\rexporting {fraction:0%}")
      : null;
    ExportReport report;
    try
    {
      report = Exporter.Write(presentation, output, ToolHosts.Default(), progress, only: only);
    }
    finally
    {
      if (progress is not null)
      {
        error.Write("\r\u001b[K");
      }
    }
    string reused = report.Reused > 0 ? $" ({report.Reused} unchanged since the last export)" : "";
    writer.WriteLine($"{report.File}: {report.Slides} slides{reused}, {report.Calls} operations, " +
      $"{report.Elapsed.TotalSeconds:0.0}s");
    if (report.Billed.Slides + report.Billed.Layouts + report.Billed.Masters > 0)
    {
      ExportBilling billed = report.Billed;
      string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
      string design = billed.Masters > 0
        ? $", {Count(billed.Layouts, "layout")}, {Count(billed.Masters, "master")}"
        : "";
      writer.WriteLine($"billed: {Count(billed.Slides, "slide")}{design}");
      if (billed.Masters > 0)
      {
        writer.WriteLine("the master and layouts bill when the deck is built from scratch, as on a first export or " +
          "after a theme or layout change");
      }
    }
    if (report.Edited is string aside)
    {
      writer.WriteLine($"the deck had been saved since the last export, so it was moved to {aside} and the " +
        "presentation exported afresh");
    }
    void List(string heading, IReadOnlyCollection<string> lines)
    {
      if (lines.Count > 0)
      {
        writer.WriteLine();
        writer.WriteLine(heading);
        foreach (string line in lines)
        {
          writer.WriteLine($"- {line}");
        }
      }
    }
    List("picks waiting on the reviewer:", PickFile.Warnings(presentation));
    List("what the deck can't reproduce:", report.Gaps);
#if DEBUG
    List("workarounds:", report.Workarounds);
#endif
    return presentation.Slides.Any(slide => slide.Error is not null) ? 1 : 0;
  }


}
