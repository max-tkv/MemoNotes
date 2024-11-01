using System.Windows;
using MemoNotes.Models;

namespace MemoNotes.Service.CheckerMousePosition;

/// <summary>
/// Обработчик проверки указателя мыши в нижнем левом угле.
/// </summary>
public class MousePositionBottomLeft : MousePosition
{
    /// <summary>
    /// Граница.
    /// </summary>
    private const double CornerMargin = 5;

    /// <inheritdoc />
    public override bool CursorIsInCorrectPlace(System.Windows.Point mousePosition)
    {
        double screenHeight = SystemParameters.PrimaryScreenHeight;
        return mousePosition.X <= 0 && 
               mousePosition.Y >= screenHeight - CornerMargin;
    }

    /// <inheritdoc />
    public override PopupWindowPositionPoint GetPopupWindowPositionPoint()
    {
        double screenHeight = SystemParameters.PrimaryScreenHeight;
        return new PopupWindowPositionPoint(0, screenHeight - PopupButtonWindowsHeight);
    }
}