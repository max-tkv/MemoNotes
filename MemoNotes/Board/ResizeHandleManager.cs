using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MemoNotes.Models;
using MemoNotes.Service.Logging;
using MemoNotes.Undo;
using Color = System.Windows.Media.Color;
using Cursor = System.Windows.Input.Cursor;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;
using TextBox = System.Windows.Controls.TextBox;

namespace MemoNotes.Board;

/// <summary>
/// Позиции ручек ресайза.
/// </summary>
public enum ResizeHandlePosition
{
    TopLeft, TopCenter, TopRight,
    MiddleLeft, MiddleRight,
    BottomLeft, BottomCenter, BottomRight
}

/// <summary>
/// Управление ручками изменения размера элементов на доске.
/// </summary>
public class ResizeHandleManager
{
    private const double HandleSize = 8;

    private readonly Canvas _canvas;
    private readonly BoardState _state;
    private readonly BoardElementFactory _factory;
    private readonly Action _saveBoard;

    private readonly List<Rectangle> _resizeHandles = new();
    private bool _isResizing;
    private ResizeHandlePosition? _activeResizeHandle;
    private Point _resizeStartPoint;
    private Rect _resizeInitialBounds;
    private Guid _resizeItemId = Guid.Empty;

    /// <summary>Id элемента, для которого показаны ресайз-ручки.</summary>
    public Guid ResizeItemId => _resizeItemId;

    /// <summary>Активен ли в данный момент процесс ресайза.</summary>
    public bool IsResizing => _isResizing;

    /// <summary>Callback, вызываемый после каждого обновления ресайза (для обновления оверлея и т.д.).</summary>
    public Action? OnResizeUpdated { get; set; }

    public ResizeHandleManager(Canvas canvas, BoardState state, BoardElementFactory factory, Action saveBoard)
    {
        _canvas = canvas;
        _state = state;
        _factory = factory;
        _saveBoard = saveBoard;
    }

    /// <summary>Показать ресайз-ручки для элемента.</summary>
    public void ShowResizeHandles(Guid itemId)
    {
        HideResizeHandles();

        var item = _state.GetItemById(itemId);
        if (item == null) return;

        _resizeItemId = itemId;
        Logger.Debug<ResizeHandleManager>($"ShowResizeHandles: id={itemId:N}, тип={item.GetType().Name}, размер=({item.Width:F0}x{item.Height:F0})");

        var positions = new[]
        {
            ResizeHandlePosition.TopLeft, ResizeHandlePosition.TopCenter, ResizeHandlePosition.TopRight,
            ResizeHandlePosition.MiddleLeft, ResizeHandlePosition.MiddleRight,
            ResizeHandlePosition.BottomLeft, ResizeHandlePosition.BottomCenter, ResizeHandlePosition.BottomRight
        };

        foreach (var pos in positions)
        {
            var visualSize = HandleSize / _state.CurrentZoom;
            var handle = new Rectangle
            {
                Width = visualSize,
                Height = visualSize,
                Fill = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
                Stroke = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
                StrokeThickness = 1 / _state.CurrentZoom,
                Cursor = GetResizeCursor(pos),
                Tag = pos
            };

            handle.PreviewMouseLeftButtonDown += ResizeHandle_PreviewMouseLeftButtonDown;

            _canvas.Children.Add(handle);
            _resizeHandles.Add(handle);
        }

        UpdateResizeHandlesPosition(item);
    }

    /// <summary>Скрыть все ресайз-ручки.</summary>
    public void HideResizeHandles()
    {
        foreach (var handle in _resizeHandles)
        {
            _canvas.Children.Remove(handle);
        }
        _resizeHandles.Clear();
        _resizeItemId = Guid.Empty;
        _isResizing = false;
        _activeResizeHandle = null;
    }

    /// <summary>Обновить позиции ресайз-ручек по модели.</summary>
    public void UpdateResizeHandlesPosition(BoardItem item)
    {
        if (_state.ElementMap.TryGetValue(item.Id, out var element) && element != null)
        {
            UpdateResizeHandlesPosition(element, item.Id);
            return;
        }

        var visualHandleSize = HandleSize / _state.CurrentZoom;
        var elementWidth = item.Width;
        var elementHeight = item.Height;

        foreach (var handle in _resizeHandles)
        {
            if (handle.Tag is not ResizeHandlePosition pos) continue;

            handle.Width = visualHandleSize;
            handle.Height = visualHandleSize;
            handle.StrokeThickness = 1 / _state.CurrentZoom;

            var x = pos switch
            {
                ResizeHandlePosition.TopLeft or ResizeHandlePosition.MiddleLeft or ResizeHandlePosition.BottomLeft
                    => item.X - visualHandleSize / 2,
                ResizeHandlePosition.TopCenter or ResizeHandlePosition.BottomCenter
                    => item.X + elementWidth / 2 - visualHandleSize / 2,
                _ => item.X + elementWidth - visualHandleSize / 2
            };

            var y = pos switch
            {
                ResizeHandlePosition.TopLeft or ResizeHandlePosition.TopCenter or ResizeHandlePosition.TopRight
                    => item.Y - visualHandleSize / 2,
                ResizeHandlePosition.MiddleLeft or ResizeHandlePosition.MiddleRight
                    => item.Y + elementHeight / 2 - visualHandleSize / 2,
                _ => item.Y + elementHeight - visualHandleSize / 2
            };

            Canvas.SetLeft(handle, x);
            Canvas.SetTop(handle, y);
        }
    }

    /// <summary>Обновить позиции ресайз-ручек по фактическому UI-элементу.</summary>
    public void UpdateResizeHandlesPosition(FrameworkElement element, Guid id)
    {
        var visualHandleSize = HandleSize / _state.CurrentZoom;
        var elementX = Canvas.GetLeft(element);
        var elementY = Canvas.GetTop(element);
        var elementWidth = element.ActualWidth;
        var elementHeight = element.ActualHeight;

        // Обновляем размеры в модели
        var model = _state.GetItemById(id);
        if (model != null)
        {
            model.X = elementX;
            model.Y = elementY;
            model.Width = elementWidth;
            model.Height = elementHeight;
        }

        foreach (var handle in _resizeHandles)
        {
            if (handle.Tag is not ResizeHandlePosition pos) continue;

            handle.Width = visualHandleSize;
            handle.Height = visualHandleSize;
            handle.StrokeThickness = 1 / _state.CurrentZoom;

            var x = pos switch
            {
                ResizeHandlePosition.TopLeft or ResizeHandlePosition.MiddleLeft or ResizeHandlePosition.BottomLeft
                    => elementX - visualHandleSize / 2,
                ResizeHandlePosition.TopCenter or ResizeHandlePosition.BottomCenter
                    => elementX + elementWidth / 2 - visualHandleSize / 2,
                _ => elementX + elementWidth - visualHandleSize / 2
            };

            var y = pos switch
            {
                ResizeHandlePosition.TopLeft or ResizeHandlePosition.TopCenter or ResizeHandlePosition.TopRight
                    => elementY - visualHandleSize / 2,
                ResizeHandlePosition.MiddleLeft or ResizeHandlePosition.MiddleRight
                    => elementY + elementHeight / 2 - visualHandleSize / 2,
                _ => elementY + elementHeight - visualHandleSize / 2
            };

            Canvas.SetLeft(handle, x);
            Canvas.SetTop(handle, y);
        }
    }

    /// <summary>Обработка движения мыши при ресайзе. Вызывается из OnMouseMove.</summary>
    public void HandleMouseMove(MouseEventArgs e)
    {
        if (!_isResizing || _activeResizeHandle == null) return;

        var item = _state.GetItemById(_resizeItemId);
        if (item == null) return;

        var currentPos = e.GetPosition(_canvas);
        var deltaX = currentPos.X - _resizeStartPoint.X;
        var deltaY = currentPos.Y - _resizeStartPoint.Y;

        var minSize = 30.0;
        var newX = _resizeInitialBounds.X;
        var newY = _resizeInitialBounds.Y;
        var newWidth = _resizeInitialBounds.Width;
        var newHeight = _resizeInitialBounds.Height;

        bool isImage = item is ImageBoardItem;
        bool isStroke = item is StrokeBoardItem;
        double aspectRatio = _resizeInitialBounds.Width / Math.Max(_resizeInitialBounds.Height, 0.001);

        switch (_activeResizeHandle)
        {
            case ResizeHandlePosition.TopLeft:
                newX = _resizeInitialBounds.X + deltaX;
                newY = _resizeInitialBounds.Y + deltaY;
                newWidth = _resizeInitialBounds.Width - deltaX;
                newHeight = _resizeInitialBounds.Height - deltaY;
                break;
            case ResizeHandlePosition.TopCenter:
                newY = _resizeInitialBounds.Y + deltaY;
                newHeight = _resizeInitialBounds.Height - deltaY;
                if (isImage || isStroke)
                {
                    newWidth = newHeight * aspectRatio;
                    newX = _resizeInitialBounds.X + (_resizeInitialBounds.Width - newWidth) / 2;
                }
                break;
            case ResizeHandlePosition.TopRight:
                newY = _resizeInitialBounds.Y + deltaY;
                newWidth = _resizeInitialBounds.Width + deltaX;
                newHeight = _resizeInitialBounds.Height - deltaY;
                break;
            case ResizeHandlePosition.MiddleLeft:
                newX = _resizeInitialBounds.X + deltaX;
                newWidth = _resizeInitialBounds.Width - deltaX;
                if (isImage || isStroke)
                {
                    newHeight = newWidth / aspectRatio;
                    newY = _resizeInitialBounds.Y + (_resizeInitialBounds.Height - newHeight) / 2;
                }
                break;
            case ResizeHandlePosition.MiddleRight:
                newWidth = _resizeInitialBounds.Width + deltaX;
                if (isImage || isStroke)
                {
                    newHeight = newWidth / aspectRatio;
                    newY = _resizeInitialBounds.Y + (_resizeInitialBounds.Height - newHeight) / 2;
                }
                break;
            case ResizeHandlePosition.BottomLeft:
                newX = _resizeInitialBounds.X + deltaX;
                newWidth = _resizeInitialBounds.Width - deltaX;
                newHeight = _resizeInitialBounds.Height + deltaY;
                break;
            case ResizeHandlePosition.BottomCenter:
                newHeight = _resizeInitialBounds.Height + deltaY;
                if (isImage || isStroke)
                {
                    newWidth = newHeight * aspectRatio;
                    newX = _resizeInitialBounds.X + (_resizeInitialBounds.Width - newWidth) / 2;
                }
                break;
            case ResizeHandlePosition.BottomRight:
                newWidth = _resizeInitialBounds.Width + deltaX;
                newHeight = _resizeInitialBounds.Height + deltaY;
                break;
        }

        // Для угловых ручек изображений и штрихов: dominant axis
        if ((isImage || isStroke) && (_activeResizeHandle == ResizeHandlePosition.TopLeft
            || _activeResizeHandle == ResizeHandlePosition.TopRight
            || _activeResizeHandle == ResizeHandlePosition.BottomLeft
            || _activeResizeHandle == ResizeHandlePosition.BottomRight))
        {
            newHeight = newWidth / aspectRatio;
            switch (_activeResizeHandle)
            {
                case ResizeHandlePosition.TopLeft:
                case ResizeHandlePosition.TopRight:
                    newY = _resizeInitialBounds.Bottom - newHeight;
                    break;
                case ResizeHandlePosition.BottomLeft:
                case ResizeHandlePosition.BottomRight:
                    newY = _resizeInitialBounds.Y;
                    break;
            }
        }

        // Ограничение минимального размера
        if (newWidth < minSize)
        {
            if (_activeResizeHandle == ResizeHandlePosition.TopLeft || _activeResizeHandle == ResizeHandlePosition.MiddleLeft || _activeResizeHandle == ResizeHandlePosition.BottomLeft)
                newX = _resizeInitialBounds.Right - minSize;
            newWidth = minSize;
            if (isImage || isStroke)
                newHeight = minSize / aspectRatio;
        }

        if (newHeight < minSize)
        {
            if (_activeResizeHandle == ResizeHandlePosition.TopLeft || _activeResizeHandle == ResizeHandlePosition.TopCenter || _activeResizeHandle == ResizeHandlePosition.TopRight)
                newY = _resizeInitialBounds.Bottom - minSize;
            newHeight = minSize;
            if (isImage || isStroke)
                newWidth = minSize * aspectRatio;
        }

        // Применяем новые размеры
        item.X = newX;
        item.Y = newY;
        item.Width = newWidth;
        item.Height = newHeight;

        if (_state.ElementMap.TryGetValue(item.Id, out var element))
        {
            Canvas.SetLeft(element, newX);
            Canvas.SetTop(element, newY);

            if (element is Border border)
            {
                border.Width = newWidth;
                border.Height = newHeight;
                // Для штрихов — пересчитываем координаты точек Path
                if (_factory.StrokeOriginalData.TryGetValue(item.Id, out var strokeInfo))
                {
                    var (origPoints, figLens, origW, origH, path) = strokeInfo;
                    _factory.UpdateStrokePathGeometry(path, origPoints, figLens, origW, origH, newWidth, newHeight);
                }
            }
            else if (element is TextBox textBox)
            {
                textBox.Width = newWidth;
                textBox.Height = double.NaN;
                textBox.Measure(new Size(newWidth, double.PositiveInfinity));
                var desiredHeight = textBox.DesiredSize.Height;
                if (desiredHeight > newHeight || double.IsNaN(newHeight))
                {
                    if (_activeResizeHandle == ResizeHandlePosition.TopLeft
                        || _activeResizeHandle == ResizeHandlePosition.TopCenter
                        || _activeResizeHandle == ResizeHandlePosition.TopRight)
                    {
                        newY = item.Y + item.Height - desiredHeight;
                        Canvas.SetTop(textBox, newY);
                        item.Y = newY;
                    }
                    newHeight = desiredHeight;
                }
                else
                {
                    textBox.Height = newHeight;
                }
            }
        }

        item.Height = newHeight;

        UpdateResizeHandlesPosition(item);

        // Уведомляем об обновлении (для оверлея выделения и др.)
        OnResizeUpdated?.Invoke();
    }

    /// <summary>Завершить ресайз. Вызывается из OnMouseUp.</summary>
    public void HandleMouseUp()
    {
        if (_isResizing)
        {
            var item = _state.GetItemById(_resizeItemId);
            if (item != null)
            {
                var newBounds = new Rect(item.X, item.Y, item.Width, item.Height);
                if (Math.Abs(_resizeInitialBounds.X - newBounds.X) > 0.5 ||
                    Math.Abs(_resizeInitialBounds.Y - newBounds.Y) > 0.5 ||
                    Math.Abs(_resizeInitialBounds.Width - newBounds.Width) > 0.5 ||
                    Math.Abs(_resizeInitialBounds.Height - newBounds.Height) > 0.5)
                {
                    _state.UndoManager.ExecuteCommand(new ResizeItemCommand(
                        _resizeItemId, _resizeInitialBounds, newBounds, SetItemBounds));
                }
            }

            _isResizing = false;
            _activeResizeHandle = null;
            _canvas.ReleaseMouseCapture();
            _saveBoard();
        }
    }

    /// <summary>Установить границы элемента (для undo/redo изменения размера).</summary>
    public void SetItemBounds(Guid id, Rect bounds)
    {
        var item = _state.GetItemById(id);
        if (item == null) return;

        item.X = bounds.X;
        item.Y = bounds.Y;
        item.Width = bounds.Width;
        item.Height = bounds.Height;

        if (_state.ElementMap.TryGetValue(id, out var element))
        {
            Canvas.SetLeft(element, bounds.X);
            Canvas.SetTop(element, bounds.Y);

            if (element is Border border)
            {
                border.Width = bounds.Width;
                border.Height = bounds.Height;

                if (_factory.StrokeOriginalData.TryGetValue(id, out var strokeInfo))
                {
                    var (origPoints, figLens, origW, origH, path) = strokeInfo;
                    _factory.UpdateStrokePathGeometry(path, origPoints, figLens, origW, origH, bounds.Width, bounds.Height);
                }
            }
            else if (element is TextBox textBox)
            {
                textBox.Width = bounds.Width;
                textBox.Height = bounds.Height;
            }
        }

        UpdateResizeHandlesPosition(item);
        _saveBoard();
    }

    #region Приватные методы

    private static Cursor GetResizeCursor(ResizeHandlePosition pos)
    {
        return pos switch
        {
            ResizeHandlePosition.TopLeft or ResizeHandlePosition.BottomRight => Cursors.SizeNWSE,
            ResizeHandlePosition.TopRight or ResizeHandlePosition.BottomLeft => Cursors.SizeNESW,
            ResizeHandlePosition.TopCenter or ResizeHandlePosition.BottomCenter => Cursors.SizeNS,
            ResizeHandlePosition.MiddleLeft or ResizeHandlePosition.MiddleRight => Cursors.SizeWE,
            _ => Cursors.SizeAll
        };
    }

    private void ResizeHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Rectangle handle) return;
        if (handle.Tag is not ResizeHandlePosition pos) return;

        e.Handled = true;

        // Предотвращаем запуск перетаскивания элемента
        if (_state.DraggedElement != null)
        {
            _state.DraggedElement.ReleaseMouseCapture();
            _state.DraggedElement = null;
            _state.DragStartPositions.Clear();
        }

        _isResizing = true;
        _activeResizeHandle = pos;
        _resizeStartPoint = e.GetPosition(_canvas);

        var item = _state.GetItemById(_resizeItemId);
        if (item != null)
        {
            _resizeInitialBounds = new Rect(item.X, item.Y, item.Width, item.Height);
        }

        _canvas.CaptureMouse();
    }

    #endregion
}
