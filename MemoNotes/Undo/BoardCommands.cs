using System.Text.Json;
using MemoNotes.Models;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;

namespace MemoNotes.Undo;

/// <summary>
/// Команда добавления элемента на доску.
/// </summary>
public class AddItemCommand : IUndoCommand
{
    private readonly Action<BoardItem> _addItem;
    private readonly Action<Guid> _removeItem;
    private readonly BoardItem _item;

    public string Description => _item is TextBoardItem ? "Добавить текст" : "Добавить изображение";

    public AddItemCommand(BoardItem item, Action<BoardItem> addItem, Action<Guid> removeItem)
    {
        _item = item;
        _addItem = addItem;
        _removeItem = removeItem;
    }

    public void Execute() => _addItem(_item);

    public void Undo() => _removeItem(_item.Id);
}

/// <summary>
/// Команда удаления элементов с доски (поддержка множественного удаления).
/// </summary>
public class DeleteItemsCommand : IUndoCommand
{
    private readonly List<DeletedItemSnapshot> _snapshots;
    private readonly Action<BoardItem> _restoreItem;
    private readonly Action _executeDelete;

    public string Description => $"Удалить {_snapshots.Count} элемент(ов)";

    public DeleteItemsCommand(
        List<BoardItem> itemsToDelete,
        Action<BoardItem> restoreItem,
        Action executeDelete)
    {
        _restoreItem = restoreItem;
        _executeDelete = executeDelete;

        _snapshots = itemsToDelete.Select(item => new DeletedItemSnapshot
        {
            SerializedItem = JsonSerializer.Serialize(item),
            ItemId = item.Id
        }).ToList();
    }

    public void Execute() => _executeDelete();

    public void Undo()
    {
        foreach (var snapshot in _snapshots)
        {
            var item = JsonSerializer.Deserialize<BoardItem>(snapshot.SerializedItem);
            if (item != null)
            {
                item.Id = snapshot.ItemId; // Гарантируем оригинальный Id
                _restoreItem(item);
            }
        }
    }

    private class DeletedItemSnapshot
    {
        public string SerializedItem { get; set; } = string.Empty;
        public Guid ItemId { get; set; }
    }
}

/// <summary>
/// Команда перемещения элементов (групповое).
/// </summary>
public class MoveItemsCommand : IUndoCommand
{
    private readonly Dictionary<Guid, (Point OldPos, Point NewPos)> _movements;
    private readonly Action<Guid, double, double> _setPosition;

    public string Description => $"Переместить {_movements.Count} элемент(ов)";

    public MoveItemsCommand(
        Dictionary<Guid, (Point OldPos, Point NewPos)> movements,
        Action<Guid, double, double> setPosition)
    {
        _movements = movements;
        _setPosition = setPosition;
    }

    public void Execute()
    {
        foreach (var kvp in _movements)
        {
            _setPosition(kvp.Key, kvp.Value.NewPos.X, kvp.Value.NewPos.Y);
        }
    }

    public void Undo()
    {
        foreach (var kvp in _movements)
        {
            _setPosition(kvp.Key, kvp.Value.OldPos.X, kvp.Value.OldPos.Y);
        }
    }
}

/// <summary>
/// Команда изменения размера элемента.
/// </summary>
public class ResizeItemCommand : IUndoCommand
{
    private readonly Guid _itemId;
    private readonly Rect _oldBounds;
    private readonly Rect _newBounds;
    private readonly Action<Guid, Rect> _setBounds;

    public string Description => "Изменить размер";

    public ResizeItemCommand(Guid itemId, Rect oldBounds, Rect newBounds, Action<Guid, Rect> setBounds)
    {
        _itemId = itemId;
        _oldBounds = oldBounds;
        _newBounds = newBounds;
        _setBounds = setBounds;
    }

    public void Execute() => _setBounds(_itemId, _newBounds);

    public void Undo() => _setBounds(_itemId, _oldBounds);
}

/// <summary>
/// Команда редактирования текста.
/// </summary>
public class EditTextCommand : IUndoCommand
{
    private readonly Guid _itemId;
    private readonly string _oldText;
    private readonly string _newText;
    private readonly Action<Guid, string> _setText;

    public string Description => "Изменить текст";

    public EditTextCommand(Guid itemId, string oldText, string newText, Action<Guid, string> setText)
    {
        _itemId = itemId;
        _oldText = oldText;
        _newText = newText;
        _setText = setText;
    }

    public void Execute() => _setText(_itemId, _newText);

    public void Undo() => _setText(_itemId, _oldText);
}
