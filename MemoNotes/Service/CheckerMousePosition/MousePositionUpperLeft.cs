using System.Windows;
using MemoNotes.Models;

namespace MemoNotes.Service.CheckerMousePosition;

/// <summary>
/// Обработчик проверки указателя мыши в верхнем левом угле.
/// </summary>
public class MousePositionUpperLeft : MousePosition
{
    /// <summary>
    /// Граница.
    /// </summary>
    private const double CornerMargin = 5;

    /// <inheritdoc />
    public override bool CursorIsInCorrectPlace(System.Windows.Point mousePosition) =>
        mousePosition is { X: <= CornerMargin, Y: <= 15 };

    /// <inheritdoc />
    public override PopupWindowPositionPoint GetPopupWindowPositionPoint()
    {
        return new PopupWindowPositionPoint(0, 0);
    }
}