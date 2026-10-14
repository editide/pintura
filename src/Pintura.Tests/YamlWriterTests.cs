using Shouldly;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Pintura.Tests;

public class YamlWriterTests
{
  [Theory]
  [InlineData("Q2 2026\rBoard")]
  [InlineData("Q2 2026\u2028Board")]
  [InlineData("Q2 2026\u0085Board")]
  [InlineData("first\nsecond\rthird")]
  [InlineData("bell\u0007")]
  public void Text_with_a_line_break_or_control_character_reads_back_as_written(string text)
  {
    // Arrange
    var map = new OrderedDictionary<string, object?> { ["text"] = text, ["items"] = new List<object?> { text } };

    // Act
    string yaml = YamlWriter.Write(map);

    // Assert
    var stream = new YamlStream();
    stream.Load(new StringReader(yaml));
    var root = (YamlMappingNode)stream.Documents[0].RootNode;
    ((YamlScalarNode)root["text"]).Value.ShouldBe(text);
    ((YamlScalarNode)((YamlSequenceNode)root["items"]).Children[0]).Value.ShouldBe(text);
  }


}