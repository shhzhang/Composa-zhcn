using Composa.Editing;
using Composa.Model;
using SkiaSharp;
using static Composa.Core.Tests.TestImages;

namespace Composa.Core.Tests;

/// <summary>The list of states the History panel shows, going to any of them at once, and knowing which one is saved.</summary>
public class HistoryTests
{
    /// <summary>A white canvas with three fills on top of each other, so every state has its own color.</summary>
    private static EditorSession ThreeFills()
    {
        var session = EditorSession.NewCanvas(8, 8, SKColors.White);
        session.Fill(SKColors.Red, "Red");
        session.Fill(SKColors.Green, "Green");
        session.Fill(SKColors.Blue, "Blue");
        return session;
    }

    private static List<string> Names(EditorSession session) => session.History.Steps.Select(s => s.Name).ToList();

    [Fact]
    public void Steps_list_every_state_oldest_first_with_the_current_one_marked()
    {
        var session = ThreeFills();
        Assert.Equal(["新建画布", "Red", "Green", "Blue"], Names(session));
        Assert.Equal(3, session.History.CurrentIndex);
        session.Undo();
        // Undone steps stay listed after the current one until the next edit drops them.
        Assert.Equal(["新建画布", "Red", "Green", "Blue"], Names(session));
        Assert.Equal(2, session.History.CurrentIndex);
        session.Fill(SKColors.Black, "Black");
        Assert.Equal(["新建画布", "Red", "Green", "Black"], Names(session));
    }

    [Fact]
    public void Going_to_a_step_moves_there_at_once_in_either_direction()
    {
        var session = ThreeFills();
        var layersChanged = 0;
        session.LayersChanged += () => layersChanged++;

        session.GoToHistory(0);
        AssertColor(SKColors.White, session.Composite().GetPixel(4, 4));
        Assert.Equal(0, session.History.CurrentIndex);
        Assert.Equal(1, layersChanged); // One restore, not one per step.
        Assert.Equal("Red", session.History.RedoName);

        session.GoToHistory(2);
        AssertColor(SKColors.Green, session.Composite().GetPixel(4, 4));
        session.Redo();
        AssertColor(SKColors.Blue, session.Composite().GetPixel(4, 4));
        session.GoToHistory(1);
        session.Undo();
        AssertColor(SKColors.White, session.Composite().GetPixel(4, 4));
        Assert.Equal(["新建画布", "Red", "Green", "Blue"], Names(session));
    }

    [Fact]
    public void Going_to_where_the_document_is_or_past_the_end_changes_nothing()
    {
        var session = ThreeFills();
        var revision = session.Revision;
        session.GoToHistory(3);
        session.GoToHistory(4);
        session.GoToHistory(-1);
        Assert.Equal(revision, session.Revision);
        Assert.Equal(3, session.History.CurrentIndex);
    }

    [Fact]
    public void Going_to_a_step_commits_text_being_typed_first()
    {
        var session = ThreeFills();
        session.Tool = Tool.Text;
        session.BeginText(new SKPoint(1, 6));
        session.TextEdit!.Insert("Hi");
        session.GoToHistory(1);
        Assert.False(session.IsEditingText);
        // The text became a step of its own before the move, so going back further still reaches it again.
        Assert.Equal(["新建画布", "Red", "Green", "Blue", "文字"], Names(session));
        Assert.Equal(1, session.History.CurrentIndex);
    }

    [Fact]
    public void Undoing_back_to_the_saved_state_makes_the_document_unmodified()
    {
        var session = ThreeFills();
        Assert.True(session.IsModified);
        session.MarkSaved("poster.cmps");
        Assert.False(session.IsModified);
        session.Fill(SKColors.Black);
        Assert.True(session.IsModified);
        session.Undo();
        Assert.False(session.IsModified);
        Assert.True(session.IsSavedState(session.History.CurrentId));
        session.GoToHistory(0);
        Assert.True(session.IsModified);
        session.GoToHistory(3);
        Assert.False(session.IsModified);
        // A new edit from an older state leaves the saved state out of reach.
        session.GoToHistory(2);
        session.Fill(SKColors.Black);
        session.Undo();
        Assert.True(session.IsModified);
    }

    [Fact]
    public void A_save_counts_the_state_it_wrote_not_the_one_the_document_reached_meanwhile()
    {
        var session = ThreeFills();
        var written = session.History.CurrentId; // The background save's snapshot.
        session.Fill(SKColors.Black);
        session.MarkSaved("poster.cmps", written);
        Assert.True(session.IsModified);
        session.Undo();
        Assert.False(session.IsModified);
    }

    [Fact]
    public void A_new_canvas_starts_unmodified_and_a_recovered_copy_stays_modified_until_it_is_saved()
    {
        var session = EditorSession.NewCanvas(8, 8, SKColors.White);
        Assert.False(session.IsModified);
        Assert.False(session.IsSavedState(session.History.CurrentId)); // Nothing is on disk yet.
        session.MarkModified();
        Assert.True(session.IsModified);
        session.Fill(SKColors.Red);
        session.Undo();
        Assert.True(session.IsModified);
        session.MarkSaved("recovered.cmps");
        Assert.False(session.IsModified);
    }

    [Fact]
    public void A_step_folded_into_the_one_before_is_announced()
    {
        var session = EditorSession.NewCanvas(200, 100, SKColors.White);
        session.AddText(new SKPoint(10, 60), new TextStyle { Text = "Hi", Size = 40 });
        var seen = new List<int>();
        session.HistoryChanged += () => seen.Add(session.History.Steps.Count);
        session.ChangeTextStyle(st => st with { Size = 41 });
        session.ChangeTextStyle(st => st with { Size = 42 });
        // The second change folds into the first, and whoever listens hears the list as it ends up.
        Assert.Equal(["新建画布", "文字", "更改文字样式"], Names(session));
        Assert.Equal(session.History.Steps.Count, seen[^1]);
    }

    [Fact]
    public void Folded_steps_keep_the_state_they_end_in()
    {
        var history = new History();
        var document = new Document(4, 4);
        history.Push("Size", document);
        history.Push("Size again", document);
        var end = history.CurrentId;
        history.MergeLast("更改文字样式");
        Assert.Equal(end, history.CurrentId);
        Assert.Equal(["打开", "更改文字样式"], history.Steps.Select(s => s.Name));
    }

    [Fact]
    public void Dropped_steps_leave_the_first_row_named_after_the_last_one_dropped()
    {
        var history = new History { MaxEntries = 2 };
        var document = new Document(4, 4);
        foreach (var name in new[] { "One", "Two", "Three", "Four" }) history.Push(name, document);
        Assert.True(history.EarlierStepsDropped);
        Assert.Equal(["Two", "Three", "Four"], history.Steps.Select(s => s.Name));
        Assert.Null(history.GoTo(-1, document));
        Assert.NotNull(history.GoTo(0, document));
        Assert.Equal(0, history.CurrentIndex);
    }
}
