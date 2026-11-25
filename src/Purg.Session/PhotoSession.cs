namespace Purg.Session;

public sealed class PhotoSession
{
    private readonly List<PhotoEntry> _photos;
    private readonly Stack<DecisionChange> _undo = new();
    private readonly Stack<DecisionChange> _redo = new();

    public PhotoSession(IEnumerable<PhotoEntry> photos)
    {
        ArgumentNullException.ThrowIfNull(photos);
        _photos = photos.ToList();
    }

    public IReadOnlyList<PhotoEntry> Photos => _photos;
    public int Count => _photos.Count;
    public int CurrentIndex { get; private set; }
    public PhotoEntry? Current => CurrentIndex < _photos.Count ? _photos[CurrentIndex] : null;
    public int MarkedCount => _photos.Count(photo => photo.State == PhotoState.Del);
    public int KeptCount => _photos.Count(photo => photo.State == PhotoState.Keep);
    public int GoneCount => _photos.Count(photo => photo.State == PhotoState.Gone);
    public int ReviewedCount => _photos.Count(photo => photo.State != PhotoState.Undecided);
    public long MarkedBytes => _photos.Where(photo => photo.State == PhotoState.Del).Sum(photo => photo.SizeBytes);
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void MovePrevious() => MoveTo(CurrentIndex - 1);
    public void MoveNext() => MoveTo(CurrentIndex + 1);

    public void MoveTo(int index)
    {
        if (_photos.Count == 0)
        {
            CurrentIndex = 0;
            return;
        }

        CurrentIndex = Math.Clamp(index, 0, _photos.Count - 1);
    }

    public void Reorder(IEnumerable<PhotoEntry> photos)
    {
        ArgumentNullException.ThrowIfNull(photos);
        var reordered = photos.ToList();
        if (reordered.Count != _photos.Count ||
            reordered.Distinct().Count() != _photos.Count ||
            reordered.Any(photo => !_photos.Contains(photo)))
        {
            throw new ArgumentException("The new order must contain each session photo exactly once.", nameof(photos));
        }

        var current = Current;
        _photos.Clear();
        _photos.AddRange(reordered);
        CurrentIndex = current is null ? _photos.Count : _photos.IndexOf(current);
    }

    public void DecideCurrent(PhotoState state)
    {
        if (state is not (PhotoState.Keep or PhotoState.Del))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        var photo = Current;
        if (photo is null || photo.State == PhotoState.Gone)
        {
            return;
        }

        _redo.Clear();
        _undo.Push(new DecisionChange(photo, photo.State, state));
        photo.State = state;
        CurrentIndex++;
    }

    public bool Undo()
    {
        while (_undo.TryPop(out var change))
        {
            if (change.Photo.State == PhotoState.Gone || change.Photo.State != change.NextState)
            {
                continue;
            }

            change.Photo.State = change.PreviousState;
            CurrentIndex = _photos.IndexOf(change.Photo);
            _redo.Push(change);
            return true;
        }

        return false;
    }

    public bool Redo()
    {
        while (_redo.TryPop(out var change))
        {
            if (change.Photo.State == PhotoState.Gone || change.Photo.State != change.PreviousState)
            {
                continue;
            }

            var index = _photos.IndexOf(change.Photo);
            if (index < 0)
            {
                continue;
            }

            change.Photo.State = change.NextState;
            _undo.Push(change);
            CurrentIndex = FindNextAvailable(index + 1);
            return true;
        }

        return false;
    }

    public void MarkGone(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var gonePaths = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (gonePaths.Count == 0)
        {
            return;
        }

        foreach (var photo in _photos)
        {
            if (photo.State == PhotoState.Del && gonePaths.Contains(photo.Path))
            {
                photo.State = PhotoState.Gone;
            }
        }

        RemoveGoneChanges(_undo);
        RemoveGoneChanges(_redo);

        if (Current?.State == PhotoState.Gone)
        {
            CurrentIndex = FindNextAvailable(CurrentIndex);
        }
    }

    public void Unmark(PhotoEntry photo)
    {
        ArgumentNullException.ThrowIfNull(photo);
        var index = _photos.IndexOf(photo);
        if (index < 0 || photo.State != PhotoState.Del)
        {
            return;
        }

        _redo.Clear();
        _undo.Push(new DecisionChange(photo, photo.State, PhotoState.Undecided));
        photo.State = PhotoState.Undecided;
    }

    public void UnmarkAll()
    {
        var markedPhotos = _photos.Where(photo => photo.State == PhotoState.Del).ToArray();
        if (markedPhotos.Length == 0)
        {
            return;
        }

        _redo.Clear();
        foreach (var photo in markedPhotos)
        {
            _undo.Push(new DecisionChange(photo, photo.State, PhotoState.Undecided));
            photo.State = PhotoState.Undecided;
        }
    }

    private int FindNextAvailable(int startIndex)
    {
        for (var index = Math.Clamp(startIndex, 0, _photos.Count); index < _photos.Count; index++)
        {
            if (_photos[index].State != PhotoState.Gone)
            {
                return index;
            }
        }

        return _photos.Count;
    }

    private static void RemoveGoneChanges(Stack<DecisionChange> history)
    {
        var retainedChanges = history
            .Where(change => change.Photo.State != PhotoState.Gone)
            .Reverse()
            .ToArray();
        history.Clear();
        foreach (var change in retainedChanges)
        {
            history.Push(change);
        }
    }

    private readonly record struct DecisionChange(
        PhotoEntry Photo,
        PhotoState PreviousState,
        PhotoState NextState);
}
