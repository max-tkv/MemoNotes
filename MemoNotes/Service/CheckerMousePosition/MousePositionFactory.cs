namespace MemoNotes.Service.CheckerMousePosition;

/// <summary>
/// Фабрика работы с позицией указателя мыши.
/// </summary>
public class MousePositionFactory
{
    private static readonly Dictionary<Enums.MousePosition, MousePosition> MousePositions = new();
    
    public MousePositionFactory()
    {
        MousePositions.Add(Enums.MousePosition.UpperLeft, new MousePositionUpperLeft());
        MousePositions.Add(Enums.MousePosition.UpperRight, new MousePositionUpperRight());
        MousePositions.Add(Enums.MousePosition.BottomLeft, new MousePositionBottomLeft());
        MousePositions.Add(Enums.MousePosition.BottomRight, new MousePositionBottomRight());
    }

    public MousePosition Create(Enums.MousePosition mousePosition) =>
        MousePositions[mousePosition];
}