using System.Windows;
using MemoNotes.Models;

namespace MemoNotes.Service.CheckerMousePosition;

/// <summary>
/// Обработчик проверки указателя мыши в верхнем правом угле.
/// </summary>
public class MousePositionUpperRight : MousePosition
{
    /// <summary>
    /// Граница.
    /// </summary>
    private const double CornerMargin = 30;

    /// <inheritdoc />
    public override bool CursorIsInCorrectPlace(System.Windows.Point mousePosition)
    {
        double screenWidth = SystemParameters.PrimaryScreenWidth;
        return mousePosition.X >= screenWidth - CornerMargin && 
               mousePosition.Y <= CornerMargin;
    }

    /// <inheritdoc />
    public override PopupWindowPositionPoint GetPopupWindowPositionPoint()
    {
        double screenWidth = SystemParameters.PrimaryScreenWidth;
        return new PopupWindowPositionPoint(screenWidth - PopupButtonWindowWidth, 0);
    }
}