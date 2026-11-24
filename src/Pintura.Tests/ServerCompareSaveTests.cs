using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Pintura.Tests;

[Trait("Needs", "git")]
public class ServerCompareSaveTests
{
  [Fact]
  public async Task Compare_answers_with_the_revision_as_drawn_and_what_changed_since()
  {
    // Arrange
    using var deck = new DeckFixture();
    GitHelper.Commit(deck.Folder.Root);
    deck.Write("deck/slides/two.yaml", "layout: content\ntitle: Changed");
    await using Served served = await Served.Start(deck);

    // Act
    System.Text.Json.JsonElement answer =
      await served.Client.GetFromJsonAsync<System.Text.Json.JsonElement>($"{served.Url}/api/compare?base=HEAD");

    // Assert
    answer.GetProperty("before").GetProperty("slides")[1].GetProperty("title").GetString()
      .ShouldBe("Results in Results");
    System.Text.Json.JsonElement slide = answer.GetProperty("diff").GetProperty("slides").EnumerateArray().Single();
    slide.GetProperty("id").GetString().ShouldBe("two");
    slide.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetString().ShouldBe("title");
  }

  [Fact]
  public async Task Versions_list_the_presentation_s_commits_and_a_comparison_describes_the_one_it_is_against()
  {
    // Arrange
    using var deck = new DeckFixture();
    GitHelper.Commit(deck.Folder.Root);
    deck.Write("deck/slides/two.yaml", "layout: content\ntitle: Changed");
    GitHelper.Git(deck.Folder.Root, "commit", "-q", "-am", "retitled");
    await using Served served = await Served.Start(deck);

    // Act
    System.Text.Json.JsonElement versions =
      await served.Client.GetFromJsonAsync<System.Text.Json.JsonElement>($"{served.Url}/api/versions");
    string first = versions[1].GetProperty("commit").GetString()!;
    System.Text.Json.JsonElement compare =
      await served.Client.GetFromJsonAsync<System.Text.Json.JsonElement>($"{served.Url}/api/compare?base={first}");

    // Assert
    versions.GetArrayLength().ShouldBe(2);
    versions[0].GetProperty("subject").GetString().ShouldBe("retitled");
    versions[0].GetProperty("number").GetInt32().ShouldBe(2);
    compare.GetProperty("version").GetProperty("subject").GetString().ShouldBe("first");
    compare.GetProperty("version").GetProperty("number").GetInt32().ShouldBe(1);
    compare.GetProperty("diff").GetProperty("slides").GetArrayLength().ShouldBe(1);
  }

  [Fact]
  public async Task Compare_without_a_revision_shows_the_last_save_s_changes_when_nothing_is_unsaved()
  {
    // Arrange
    using var deck = new DeckFixture();
    GitHelper.Commit(deck.Folder.Root);
    deck.Write("deck/slides/two.yaml", "layout: content\ntitle: Changed");
    GitHelper.Git(deck.Folder.Root, "commit", "-q", "-am", "retitled");
    deck.Write("deck/comments.yaml", "- { id: a1, slide: two, at: [0, 0], text: Shorter }\n");
    await using Served served = await Served.Start(deck);

    // Act
    System.Text.Json.JsonElement compare =
      await served.Client.GetFromJsonAsync<System.Text.Json.JsonElement>($"{served.Url}/api/compare");

    // Assert
    compare.GetProperty("version").GetProperty("number").GetInt32().ShouldBe(1);
    compare.GetProperty("lastSave").GetProperty("subject").GetString().ShouldBe("retitled");
    compare.GetProperty("diff").GetProperty("slides").EnumerateArray().Single().GetProperty("id").GetString()
      .ShouldBe("two");
  }

  [Fact]
  public async Task Compare_without_a_revision_shows_the_unsaved_changes_when_there_are_some()
  {
    // Arrange
    using var deck = new DeckFixture();
    GitHelper.Commit(deck.Folder.Root);
    deck.Write("deck/slides/two.yaml", "layout: content\ntitle: Changed");
    GitHelper.Git(deck.Folder.Root, "commit", "-q", "-am", "retitled");
    deck.Write("deck/slides/one.yaml", "layout: cover\ntitle: Unsaved");
    await using Served served = await Served.Start(deck);

    // Act
    System.Text.Json.JsonElement compare =
      await served.Client.GetFromJsonAsync<System.Text.Json.JsonElement>($"{served.Url}/api/compare");

    // Assert
    compare.GetProperty("version").GetProperty("number").GetInt32().ShouldBe(2);
    compare.TryGetProperty("lastSave", out _).ShouldBeFalse();
    compare.GetProperty("diff").GetProperty("slides").EnumerateArray().Single().GetProperty("id").GetString()
      .ShouldBe("one");
  }

  [Fact]
  public async Task Compare_without_a_revision_shows_no_changes_on_the_first_version()
  {
    // Arrange
    using var deck = new DeckFixture();
    GitHelper.Commit(deck.Folder.Root);
    await using Served served = await Served.Start(deck);

    // Act
    System.Text.Json.JsonElement compare =
      await served.Client.GetFromJsonAsync<System.Text.Json.JsonElement>($"{served.Url}/api/compare");

    // Assert
    compare.GetProperty("version").GetProperty("number").GetInt32().ShouldBe(1);
    compare.TryGetProperty("lastSave", out _).ShouldBeFalse();
    compare.GetProperty("diff").GetProperty("slides").GetArrayLength().ShouldBe(0);
  }

  [Fact]
  public async Task Compare_between_two_versions_draws_the_later_one_as_the_after_side()
  {
    // Arrange
    using var deck = new DeckFixture();
    string[] commits = CommitVersions(deck, ("deck/slides/two.yaml", "Second"), ("deck/slides/one.yaml", "Third"));
    deck.Write("deck/slides/two.yaml", "layout: content\ntitle: Unsaved");
    await using Served served = await Served.Start(deck);

    // Act
    System.Text.Json.JsonElement compare = await served.Client.GetFromJsonAsync<System.Text.Json.JsonElement>(
      $"{served.Url}/api/compare?base={commits[0]}&head={commits[1]}");

    // Assert
    compare.GetProperty("version").GetProperty("number").GetInt32().ShouldBe(1);
    compare.GetProperty("head").GetProperty("number").GetInt32().ShouldBe(2);
    compare.GetProperty("after").GetProperty("slides")[1].GetProperty("title").GetString().ShouldBe("Second");
    compare.GetProperty("diff").GetProperty("slides").EnumerateArray().Single().GetProperty("id").GetString()
      .ShouldBe("two");
  }

  [Fact]
  public async Task A_slide_s_history_steps_through_the_versions_that_changed_how_it_draws()
  {
    // Arrange
    using var deck = new DeckFixture();
    string[] commits = CommitVersions(deck, ("deck/slides/two.yaml", "Second"), ("deck/slides/one.yaml", "Third"),
      ("deck/slides/two.yaml", "Fourth"));
    await using Served served = await Served.Start(deck);

    // Act
    string history = $"{served.Url}/api/history?slide=two";
    HttpResponseMessage fromLatest = await served.Client.GetAsync($"{history}&before={commits[2]}");
    HttpResponseMessage fromFirst = await served.Client.GetAsync($"{history}&before={commits[0]}");
    HttpResponseMessage forward = await served.Client.GetAsync($"{history}&after={commits[1]}");
    HttpResponseMessage pastNewest = await served.Client.GetAsync($"{history}&after={commits[3]}");

    // Assert
    System.Text.Json.JsonElement older = await fromLatest.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
    older.GetProperty("base").GetProperty("number").GetInt32().ShouldBe(1);
    older.GetProperty("head").GetProperty("number").GetInt32().ShouldBe(2);
    fromFirst.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    System.Text.Json.JsonElement newer = await forward.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
    newer.GetProperty("base").GetProperty("number").GetInt32().ShouldBe(3);
    newer.GetProperty("head").GetProperty("number").GetInt32().ShouldBe(4);
    pastNewest.StatusCode.ShouldBe(HttpStatusCode.NotFound);
  }

  [Fact]
  public async Task A_version_stepped_to_names_the_slides_it_draws_as_the_working_tree_does()
  {
    // Arrange
    using var deck = new DeckFixture();
    string[] commits = CommitVersions(deck, ("deck/slides/two.yaml", "Second"), ("deck/slides/one.yaml", "Third"));
    await using Served served = await Served.Start(deck);

    // Act
    System.Text.Json.JsonElement compare = await served.Client.GetFromJsonAsync<System.Text.Json.JsonElement>(
      $"{served.Url}/api/compare?base={commits[0]}&head={commits[1]}");

    // Assert
    compare.GetProperty("latest").EnumerateArray().Select(id => id.GetString()).ShouldBe(["two"]);
  }

  [Fact]
  public async Task A_slide_s_history_counts_a_theme_change_that_restyles_it()
  {
    // Arrange
    using var deck = new DeckFixture();
    CommitVersions(deck, ("deck/slides/two.yaml", "Second"));
    deck.Write("theme/styles.yaml", DeckFixture.Styles.Replace("size: 28", "size: 30"));
    GitHelper.Git(deck.Folder.Root, "commit", "-q", "-am", "bigger titles");
    deck.Write("deck/slides/one.yaml", "layout: cover\ntitle: Fourth");
    GitHelper.Git(deck.Folder.Root, "commit", "-q", "-am", "retitled one");
    await using Served served = await Served.Start(deck);
    string latestBase = (await served.Client.GetFromJsonAsync<System.Text.Json.JsonElement>(
      $"{served.Url}/api/versions"))[1].GetProperty("commit").GetString()!;

    // Act
    System.Text.Json.JsonElement step = await served.Client.GetFromJsonAsync<System.Text.Json.JsonElement>(
      $"{served.Url}/api/history?slide=two&before={latestBase}");

    // Assert
    step.GetProperty("head").GetProperty("subject").GetString().ShouldBe("bigger titles");
  }

  [Fact]
  public async Task Compare_pairs_a_slide_renamed_since_the_revision_with_the_slide_it_was()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", GitHelper.Body("Results"));
    GitHelper.Commit(deck.Folder.Root);
    string first = Revisions.Commit(deck.Folder.Root, "HEAD");
    GitHelper.Rename(deck, "two", "results", "Retitled");
    await using Served served = await Served.Start(deck);

    // Act
    System.Text.Json.JsonElement diff = (await served.Client.GetFromJsonAsync<System.Text.Json.JsonElement>(
      $"{served.Url}/api/compare?base={first}")).GetProperty("diff");

    // Assert
    System.Text.Json.JsonElement slide = diff.GetProperty("slides").EnumerateArray().Single();
    slide.GetProperty("id").GetString().ShouldBe("results");
    slide.GetProperty("status").GetString().ShouldBe("changed");
    diff.GetProperty("renamed").GetProperty("results").GetString().ShouldBe("two");
  }

  [Fact]
  public async Task A_slide_s_history_follows_it_through_its_renames_under_the_name_each_version_has_it_by()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", GitHelper.Body("First"));
    GitHelper.Commit(deck.Folder.Root);
    var commits = new List<string> { Revisions.Commit(deck.Folder.Root, "HEAD") };
    void Version(Action change)
    {
      change();
      commits.Add(Revisions.Commit(deck.Folder.Root, "HEAD"));
    }
    Version(() => GitHelper.Rename(deck, "two", "results", "Second"));
    Version(() =>
    {
      deck.Write("deck/slides/one.yaml", "layout: cover\ntitle: Third");
      GitHelper.Git(deck.Folder.Root, "commit", "-q", "-am", "third");
    });
    Version(() =>
    {
      deck.Write("deck/slides/results.yaml", GitHelper.Body("Fourth"));
      GitHelper.Git(deck.Folder.Root, "commit", "-q", "-am", "fourth");
    });
    Version(() => GitHelper.Rename(deck, "results", "outcome"));
    await using Served served = await Served.Start(deck);

    // Act
    string history = $"{served.Url}/api/history";
    System.Text.Json.JsonElement older = await served.Client.GetFromJsonAsync<System.Text.Json.JsonElement>(
      $"{history}?slide=outcome&before={commits[4]}");
    System.Text.Json.JsonElement oldest = await served.Client.GetFromJsonAsync<System.Text.Json.JsonElement>(
      $"{history}?slide=results&before={commits[2]}");
    System.Text.Json.JsonElement newer = await served.Client.GetFromJsonAsync<System.Text.Json.JsonElement>(
      $"{history}?slide=two&after={commits[0]}");
    HttpResponseMessage pastNewest = await served.Client.GetAsync($"{history}?slide=results&after={commits[3]}");

    // Assert
    (older.GetProperty("head").GetProperty("number").GetInt32(), older.GetProperty("slide").GetString())
      .ShouldBe((4, "results"));
    (oldest.GetProperty("head").GetProperty("number").GetInt32(), oldest.GetProperty("slide").GetString())
      .ShouldBe((2, "results"));
    (newer.GetProperty("head").GetProperty("number").GetInt32(), newer.GetProperty("slide").GetString())
      .ShouldBe((2, "results"));
    pastNewest.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    (await pastNewest.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("slide").GetString()
      .ShouldBe("outcome");
  }

  [Fact]
  public async Task Reverting_a_renamed_slide_writes_it_back_as_the_slide_it_was()
  {
    // Arrange
    using var deck = new DeckFixture();
    deck.Write("deck/slides/two.yaml", GitHelper.Body("Results"));
    string committed = File.ReadAllText(deck.Folder.PathTo("deck/slides/two.yaml"));
    GitHelper.Commit(deck.Folder.Root);
    string first = Revisions.Commit(deck.Folder.Root, "HEAD");
    GitHelper.Rename(deck, "two", "results", "Retitled");
    await using Served served = await Served.Start(deck);
    var revert = new { @base = first, slide = "results" };

    // Act
    HttpResponseMessage reverted = await served.Client.PostAsJsonAsync($"{served.Url}/api/revert", revert);

    // Assert
    reverted.StatusCode.ShouldBe(HttpStatusCode.OK);
    File.ReadAllText(deck.Folder.PathTo("deck/slides/results.yaml")).ShouldBe(committed);
  }

  // The fixture as the first version, then one version per slide retitled, answering each version's commit, oldest
  // first.
  private static string[] CommitVersions(DeckFixture deck, params (string File, string Title)[] retitles)
  {
    GitHelper.Commit(deck.Folder.Root);
    List<string> commits = [Revisions.Commit(deck.Folder.Root, "HEAD")];
    foreach ((string file, string title) in retitles)
    {
      string layout = file.EndsWith("one.yaml", StringComparison.Ordinal) ? "cover" : "content";
      deck.Write(file, $"layout: {layout}\ntitle: {title}");
      GitHelper.Git(deck.Folder.Root, "commit", "-q", "-am", title);
      commits.Add(Revisions.Commit(deck.Folder.Root, "HEAD"));
    }
    return [.. commits];
  }

  [Fact]
  public async Task Saving_commits_a_nudge_under_its_draft_and_leaves_an_agents_edit_unsaved()
  {
    // Arrange
    using var deck = new DeckFixture();
    GitHelper.Commit(deck.Folder.Root);
    await using Served served = await Served.Start(deck);
    deck.Write("deck/slides/one.yaml", "layout: cover\ntitle: An agent's");
    await served.Client.PostAsJsonAsync($"{served.Url}/api/edit", new { slide = "two", edits = new[] {
      new { path = "title.frame", source = (string?)null, numbers = new[] { 60.0, 180, 840, 90 } } } });
    System.Text.Json.JsonElement unsaved =
      await served.Client.GetFromJsonAsync<System.Text.Json.JsonElement>($"{served.Url}/api/save");
    System.Text.Json.JsonElement draft =
      await served.Client.GetFromJsonAsync<System.Text.Json.JsonElement>($"{served.Url}/api/save/draft");

    // Act
    HttpResponseMessage response = await served.Client.PostAsJsonAsync($"{served.Url}/api/save", new { });
    System.Text.Json.JsonElement saved = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
    System.Text.Json.JsonElement after =
      await served.Client.GetFromJsonAsync<System.Text.Json.JsonElement>($"{served.Url}/api/save");

    // Assert
    unsaved.GetProperty("unsaved").GetBoolean().ShouldBeTrue();
    unsaved.GetProperty("next").GetInt32().ShouldBe(2);
    unsaved.GetProperty("slides").EnumerateArray().Select(slide => slide.GetString()).ShouldBe(["two"]);
    unsaved.GetProperty("shared").GetBoolean().ShouldBeFalse();
    unsaved.GetProperty("edited").GetBoolean().ShouldBeTrue();
    draft.GetProperty("message").GetString().ShouldBe("Rearranged slide 2");
    draft.TryGetProperty("mixed", out _).ShouldBeFalse();
    saved.GetProperty("number").GetInt32().ShouldBe(2);
    saved.GetProperty("subject").GetString().ShouldBe("Rearranged slide 2");
    after.GetProperty("unsaved").GetBoolean().ShouldBeTrue();
    after.GetProperty("slides").GetArrayLength().ShouldBe(0);
  }

  [Fact]
  public async Task Saving_the_files_the_box_names_drafts_and_commits_only_their_part()
  {
    // Arrange
    using var deck = new DeckFixture();
    GitHelper.Commit(deck.Folder.Root);
    await using Served served = await Served.Start(deck);
    foreach (string slide in new[] { "one", "two" })
    {
      await served.Client.PostAsJsonAsync($"{served.Url}/api/edit", new { slide, edits = new[] {
        new { path = "title.frame", source = (string?)null, numbers = new[] { 60.0, 180, 840, 90 } } } });
    }
    System.Text.Json.JsonElement whole =
      await served.Client.GetFromJsonAsync<System.Text.Json.JsonElement>($"{served.Url}/api/save/draft");
    System.Text.Json.JsonElement chosen = await served.Client.GetFromJsonAsync<System.Text.Json.JsonElement>(
      $"{served.Url}/api/save/draft?files=deck/slides/two.yaml");

    // Act
    HttpResponseMessage response = await served.Client.PostAsJsonAsync($"{served.Url}/api/save",
      new { files = new[] { "deck/slides/two.yaml" } });
    System.Text.Json.JsonElement after =
      await served.Client.GetFromJsonAsync<System.Text.Json.JsonElement>($"{served.Url}/api/save");

    // Assert
    whole.GetProperty("parts").EnumerateArray().Select(part => part.GetProperty("label").GetString())
      .ShouldBe(["Slide 1", "Slide 2"]);
    whole.GetProperty("message").GetString().ShouldBe("Rearranged slides 1 and 2");
    chosen.GetProperty("message").GetString().ShouldBe("Rearranged slide 2");
    response.IsSuccessStatusCode.ShouldBeTrue();
    after.GetProperty("slides").EnumerateArray().Select(slide => slide.GetString()).ShouldBe(["one"]);
  }



}
