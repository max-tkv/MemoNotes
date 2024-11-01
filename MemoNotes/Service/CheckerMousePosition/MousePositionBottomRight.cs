using System.Windows;
using MemoNotes.Models;

namespace MemoNotes.Service.CheckerMousePosition;

/// <summary>
/// Обработчик проверки указателя мыши в нижнем правом угле.
/// </summary>
public class MousePositionBottomRight : MousePosition
{
    /// <summary>
    /// Граница.
    /// </summary>
    private const double CornerMargin = 5;
    
    /// <inheritdoc />
    public override bool CursorIsInCorrectPlace(System.Windows.Point mousePosition)
    {
        double screenHeight = SystemParameters.PrimaryScreenHeight;
        double screenWidth = SystemParameters.PrimaryScreenWidth;
        
        return mousePosition.X >= screenWidth - CornerMargin && 
               mousePosition.Y >= screenHeight - CornerMargin;
    }

    public override PopupWindowPositionPoint GetPopupWindowPositionPoint()
    {
        double screenHeight = SystemParameters.PrimaryScreenHeight;
        double screenWidth = SystemParameters.PrimaryScreenWidth;
        return new PopupWindowPositionPoint(
            screenWidth - PopupButtonWindowWidth, 
            screenHeight - PopupButtonWindowsHeight);
    }
}