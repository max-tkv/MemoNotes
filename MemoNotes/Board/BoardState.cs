using System.Windows;
using System.Windows.Controls;
using MemoNotes.Models;
using MemoNotes.Service.Logging;
using MemoNotes.Undo;
using Point = System.Windows.Point;

namespace MemoNotes.Board;

/// <summary>
/// Центральное хранилище состояния доски.
/// Содержит коллекции элементов, карту UI-элементов, состояние выделения и зума.
/// </summary>
public class BoardState
{
    #region Данные доски

    /// <summary>Все элементы модели на доске.</summary>
    public List<BoardItem> BoardItems { get; } = new();

    /// <summary>Маппинг Id элемента → UI-элемент на Canvas.</summary>
    public Dictionary<Guid, FrameworkElement> ElementMap { get; } = new();

    /// <summary>Менеджер undo/redo.</summary>
    public UndoManager UndoManager { get; } = new();

    #endregion

    #region Зум

    public double CurrentZoom { get; set; } = 1.0;
    public const double MinZoom = 0.1;
    public const double MaxZoom = 5.0;
    public const double ZoomStep = 0.1;

    #endregion

    #region Панорамирование

    public bool IsPanning { get; set; }
    public Point PanStartPoint { get; set; }
    public Point ScrollStartOffset { get; set; }

    #endregion

    #region Перетаскивание элементов

    public FrameworkElement? DraggedElement { get; set; }
    public Point DragStartPoint { get; set; }
    public Point ElementStartPos { get; set; }
    public Dictionary<Guid, Point> DragStartPositions { get; } = new();

    #endregion

    #region Выделение

    public Border? SelectedImageBorder { get; set; }
    public HashSet<Guid> SelectedItemIds { get; set; } = new();
    public Dictionary<Guid, Border> SelectionOverlays { get; } = new();

    // Множественное выделение (rubber band)
    public bool IsSelecting { get; set; }
    public Point SelectionStartPoint { get; set; }
    public System.Windows.Shapes.Rectangle? SelectionRectangle { get; set; }

    #endregion

    #region Undo/Redo текстового редактирования

    public string? EditingTextBeforeChange { get; set; }

    #endregion

    #region Вспомогательные методы

    /// <summary>Получить BoardItem по Id.</summary>
    public BoardItem? GetItemById(Guid id)
    {
        var item = BoardItems.FirstOrDefault(i => i.Id == id);
        if (item == null)
            Logger.Warn<BoardState>($"GetItemById: элемент {id} не найден");
        return item;
    }

    /// <summary>Получить UI-элемент по Id.</summary>
    public FrameworkElement? GetElementById(Guid id)
    {
        return ElementMap.TryGetValue(id, out var element) ? element : null;
    }

    /// <summary>Есть ли выделенные элементы.</summary>
    public bool HasSelection => SelectedItemIds.Count > 0;

    /// <summary>Удалить элемент из всех коллекций состояния.</summary>
    public void RemoveFromState(Guid id)
    {
        Logger.Debug<BoardState>($"RemoveFromState: {id}, тип={GetItemById(id)?.GetType().Name ?? "неизвестен"}");
        BoardItems.RemoveAll(i => i.Id == id);
        ElementMap.Remove(id);
        SelectedItemIds.Remove(id);
        if (DraggedElement?.Tag is Guid draggedId && draggedId == id)
            DraggedElement = null;
    }

    #endregion
}
