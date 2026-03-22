namespace MemoNotes.Undo;

/// <summary>
/// Менеджер стека команд для поддержки Отмена/Повтор.
/// </summary>
public class UndoManager
{
    private const int MaxHistorySize = 100;

    private readonly Stack<IUndoCommand> _undoStack = new();
    private readonly Stack<IUndoCommand> _redoStack = new();

    /// <summary>Есть ли команды для отмены.</summary>
    public bool CanUndo => _undoStack.Count > 0;

    /// <summary>Есть ли команды для повтора.</summary>
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>
    /// Выполнить новую команду и добавить её в стек отмены.
    /// При этом стек повтора очищается.
    /// </summary>
    public void ExecuteCommand(IUndoCommand command)
    {
        command.Execute();
        _undoStack.Push(command);
        _redoStack.Clear();

        // Ограничиваем размер истории
        if (_undoStack.Count > MaxHistorySize)
        {
            TrimStack(_undoStack);
        }
    }

    /// <summary>
    /// Выполнить команду напрямую, без добавления в стек.
    /// Используется для начальной загрузки.
    /// </summary>
    public void ExecuteSilent(IUndoCommand command)
    {
        command.Execute();
    }

    /// <summary>Отменить последнюю команду.</summary>
    public void Undo()
    {
        if (_undoStack.Count == 0) return;
        var command = _undoStack.Pop();
        command.Undo();
        _redoStack.Push(command);
    }

    /// <summary>Повторить последнюю отменённую команду.</summary>
    public void Redo()
    {
        if (_redoStack.Count == 0) return;
        var command = _redoStack.Pop();
        command.Execute();
        _undoStack.Push(command);
    }

    /// <summary>Очистить всю историю.</summary>
    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }

    private static void TrimStack(Stack<IUndoCommand> stack)
    {
        var temp = new List<IUndoCommand>();
        while (stack.Count > MaxHistorySize / 2)
        {
            temp.Add(stack.Pop());
        }
        // temp содержит самые старые команды (лишние) — просто отбрасываем
    }
}
