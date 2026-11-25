using Purg.Session;
using Xunit;

namespace Purg.Tests;

public sealed class SessionTests
{
    [Fact]
    public void DecisionsAdvanceAndUndoRestoresThePhotoAndState()
    {
        var first = Photo("first.jpg");
        var second = Photo("second.jpg");
        var session = new PhotoSession(new[] { first, second });

        session.DecideCurrent(PhotoState.Del);

        Assert.Equal(1, session.CurrentIndex);
        Assert.Same(second, session.Current);
        Assert.Equal(PhotoState.Del, first.State);
        Assert.Equal(1, session.MarkedCount);
        Assert.True(session.Undo());
        Assert.Equal(0, session.CurrentIndex);
        Assert.Equal(PhotoState.Undecided, first.State);
        Assert.Equal(0, session.MarkedCount);
    }

    [Fact]
    public void RedoReappliesTheUndoneDecisionAndAdvancesToTheNextPhoto()
    {
        var first = Photo("first.jpg");
        var second = Photo("second.jpg");
        var session = new PhotoSession(new[] { first, second });
        session.DecideCurrent(PhotoState.Del);
        session.DecideCurrent(PhotoState.Keep);

        Assert.True(session.Undo());
        Assert.Same(second, session.Current);
        Assert.Equal(PhotoState.Undecided, second.State);
        Assert.True(session.CanRedo);

        Assert.True(session.Redo());

        Assert.Equal(PhotoState.Keep, second.State);
        Assert.Null(session.Current);
        Assert.False(session.CanRedo);
        Assert.True(session.CanUndo);
    }

    [Fact]
    public void NewDecisionClearsRedoHistory()
    {
        var first = Photo("first.jpg");
        var second = Photo("second.jpg");
        var session = new PhotoSession(new[] { first, second });
        session.DecideCurrent(PhotoState.Del);
        session.DecideCurrent(PhotoState.Keep);

        Assert.True(session.Undo());
        session.DecideCurrent(PhotoState.Del);

        Assert.Equal(PhotoState.Del, second.State);
        Assert.False(session.CanRedo);
        Assert.False(session.Redo());
    }

    [Fact]
    public void RedoReappliesUnmarkTransitions()
    {
        var photo = Photo("photo.jpg");
        var session = new PhotoSession(new[] { photo });
        session.DecideCurrent(PhotoState.Del);
        session.Unmark(photo);

        Assert.True(session.Undo());
        Assert.Equal(PhotoState.Del, photo.State);
        Assert.True(session.Redo());
        Assert.Equal(PhotoState.Undecided, photo.State);
        Assert.Equal(0, session.MarkedCount);
    }

    [Fact]
    public void ReorderingPreservesPhotoStateAndUndoTarget()
    {
        var first = Photo("first.jpg");
        var second = Photo("second.jpg");
        var session = new PhotoSession(new[] { first, second });
        session.DecideCurrent(PhotoState.Keep);
        session.Reorder(new[] { second, first });

        Assert.Same(second, session.Current);
        Assert.True(session.Undo());
        Assert.Equal(PhotoState.Undecided, first.State);
        Assert.Same(first, session.Current);
    }

    [Fact]
    public void UnmarkAllOnlyResetsMarkedPhotos()
    {
        var marked = Photo("marked.jpg");
        var kept = Photo("kept.jpg");
        var session = new PhotoSession(new[] { marked, kept });
        session.DecideCurrent(PhotoState.Del);
        session.DecideCurrent(PhotoState.Keep);

        session.UnmarkAll();

        Assert.Equal(PhotoState.Undecided, marked.State);
        Assert.Equal(PhotoState.Keep, kept.State);
        Assert.Equal(0, session.MarkedCount);
        Assert.Equal(1, session.KeptCount);
    }

    [Fact]
    public void GonePhotosCannotBeUndoneAndAreExcludedFromMarkedTotals()
    {
        var photo = Photo("gone.jpg");
        var session = new PhotoSession(new[] { photo });
        session.DecideCurrent(PhotoState.Del);

        Assert.Null(session.Current);
        session.MarkGone(new[] { photo.Path });

        Assert.Equal(PhotoState.Gone, photo.State);
        Assert.Equal(0, session.MarkedCount);
        Assert.Equal(0, session.MarkedBytes);
        Assert.Equal(1, session.GoneCount);
        Assert.False(session.CanUndo);
        Assert.False(session.Undo());
        Assert.Equal(PhotoState.Gone, photo.State);
    }

    [Fact]
    public void MarkingGoneRemovesOnlyThatPhotoFromUndoHistory()
    {
        var deleted = Photo("deleted.jpg");
        var kept = Photo("kept.jpg");
        var session = new PhotoSession(new[] { deleted, kept });
        session.DecideCurrent(PhotoState.Del);
        session.DecideCurrent(PhotoState.Keep);

        session.MarkGone(new[] { deleted.Path });

        Assert.True(session.Undo());
        Assert.Equal(PhotoState.Gone, deleted.State);
        Assert.Equal(PhotoState.Undecided, kept.State);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void MarkingGoneRemovesThatPhotoFromBothHistoryStacks()
    {
        var photo = Photo("photo.jpg");
        var session = new PhotoSession(new[] { photo });
        session.DecideCurrent(PhotoState.Del);
        session.Unmark(photo);
        Assert.True(session.Undo());

        session.MarkGone(new[] { photo.Path });

        Assert.Equal(PhotoState.Gone, photo.State);
        Assert.False(session.CanUndo);
        Assert.False(session.CanRedo);
        Assert.False(session.Undo());
        Assert.False(session.Redo());
    }

    private static PhotoEntry Photo(string name) =>
        new(name, name, 1024, DateTime.UnixEpoch);
}
