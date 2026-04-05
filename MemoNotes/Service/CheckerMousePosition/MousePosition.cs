using MemoNotes.Models;

namespace MemoNotes.Service.CheckerMousePosition;

/// <summary>
/// Базовый класс позиции указателя мыши.
/// </summary>
public abstract class MousePosition
{
    /// <summary>
    /// Ширина окна/кнопки.
    /// </summary>
    protected const double PopupButtonWindowWidth = 54;
    
    /// <summary>
    /// Высота окна/кнопки.
    /// </summary>
    protected const double PopupButtonWindowsHeight = 54;

    /// <summary>
    /// Проверить позицию указателя мыши.
    /// </summary>
    /// <param name="point">Координаты указателя.</param>
    /// <returns>Указатель мыши находится в правильном месте.</returns>
    public abstract bool CursorIsInCorrectPlace(System.Windows.Point point);
    
    /// <summary>
    /// Получить позицию всплывающего окна при наведении мыши.
    /// </summary>
    /// <returns>Позиция всплывающего окна при наведении мыши.</returns>
    public abstract PopupWindowPositionPoint GetPopupWindowPositionPoint();
}