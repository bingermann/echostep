namespace Echostep.Core;

/// <summary>
/// Undo and redo for an action list, by snapshot: after every change the list is copied, so any
/// kind of edit (insert, edit, delete, move, paste, a whole recording) undoes in one step.
/// </summary>
public sealed class UndoHistory
{
    public const int MaxSteps = 200;

    private readonly LinkedList<List<MacroAction>> _undo = new();
    private readonly Stack<List<MacroAction>> _redo = new();
    private List<MacroAction> _current;

    public UndoHistory(IReadOnlyList<MacroAction> initial) => _current = MacroFile.CloneAll(initial);

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Call after the list has changed.</summary>
    public void Record(IReadOnlyList<MacroAction> actions)
    {
        _undo.AddLast(_current);
        if (_undo.Count > MaxSteps) _undo.RemoveFirst();
        _redo.Clear();
        _current = MacroFile.CloneAll(actions);
    }

    /// <summary>Puts the previous state back into <paramref name="actions"/> (in place). False if there is nothing to undo.</summary>
    public bool Undo(List<MacroAction> actions)
    {
        if (_undo.Last is not { } node) return false;
        _undo.RemoveLast();
        _redo.Push(_current);
        _current = node.Value;
        Load(actions);
        return true;
    }

    public bool Redo(List<MacroAction> actions)
    {
        if (_redo.Count == 0) return false;
        _undo.AddLast(_current);
        _current = _redo.Pop();
        Load(actions);
        return true;
    }

    private void Load(List<MacroAction> actions)
    {
        actions.Clear();
        actions.AddRange(MacroFile.CloneAll(_current));
    }
}
