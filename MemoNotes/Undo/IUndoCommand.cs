namespace MemoNotes.Undo;

/// <summary>
/// Интерфейс отменяемой команды для паттерна Command.
/// </summary>
public interface IUndoCommand
{
    /// <summary>Описание команды для отображения в UI.</summary>
    string Description { get; }

    /// <summary>Выполнить команду.</summary>
    void Execute();

    /// <summary>Отменить команду.</summary>
    void Undo();
}
