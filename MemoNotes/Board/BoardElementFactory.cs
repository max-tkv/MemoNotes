using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MemoNotes.Models;
using MemoNotes.Service.Logging;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;

namespace MemoNotes.Board;

/// <summary>
/// Фабрика для создания UI-элементов доски (текст, изображение, штрих).
/// </summary>
public class BoardElementFactory
{
    private readonly Canvas _canvas;
    private readonly BoardState _state;
    private readonly Action _saveBoard;

    /// <summary>
    /// Callback для обработки клика по текстовому элементу.
    /// </summary>
    public Action<FrameworkElement, MouseButtonEventArgs>? OnTextElementClick { get; set; }

    /// <summary>
    /// Callback для обработки клика по изображению (ЛКМ).
    /// </summary>
    public Action<Border, MouseButtonEventArgs>? OnImageLeftClick { get; set; }

    /// <summary>
    /// Callback для обработки ПКМ по изображению.
    /// </summary>
    public Action<FrameworkElement, MouseButtonEventArgs>? OnElementRightClick { get; set; }

    /// <summary>
    /// Callback для обработки клика по штриху (ЛКМ).
    /// </summary>
    public Action<Border, MouseButtonEventArgs>? OnStrokeLeftClick { get; set; }

    /// <summary>
    /// Callback, вызываемый после изменения текста в TextBox.
    /// </summary>
    public Action<Guid>? OnTextChanged { get; set; }

    /// <summary>
    /// Callback, вызываемый при потере фокуса TextBox.
    /// </summary>
    public Action<TextBox, Guid>? OnTextBoxLostFocus { get; set; }

    /// <summary>
    /// Callback, вызываемый при получении фокуса TextBox.
    /// </summary>
    public Action<Guid>? OnTextBoxGotFocus { get; set; }

    public BoardElementFactory(Canvas canvas, BoardState state, Action saveBoard)
    {
        _canvas = canvas;
        _state = state;
        _saveBoard = saveBoard;
    }

    #region Текстовый элемент

    /// <summary>Создать UI-элемент для текстового блока и добавить на доску.</summary>
    public void CreateTextElement(TextBoardItem item)
    {
        Logger.Debug<BoardElementFactory>($"CreateTextElement: id={item.Id}, pos=({item.X:F0},{item.Y:F0}), размер=({item.Width:F0}x{item.Height:F0})");

        var textBox = new TextBox
        {
            Style = (Style)_canvas.FindResource("BoardTextStyle"),
            Text = item.Text,
            Width = item.Width,
            Height = item.Height,
            Tag = item.Id,
            FontSize = item.FontSize,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            Cursor = Cursors.SizeAll
        };

        textBox.PreviewMouseLeftButtonDown += (s, e) =>
        {
            if (OnTextElementClick != null && s is FrameworkElement fe)
                OnTextElementClick(fe, e);
        };

        Canvas.SetLeft(textBox, item.X);
        Canvas.SetTop(textBox, item.Y);
        _canvas.Children.Add(textBox);

        _state.BoardItems.Add(item);
        _state.ElementMap[item.Id] = textBox;

        textBox.TextChanged += (s, args) =>
        {
            if (s is TextBox tb && tb.Tag is Guid id)
            {
                var model = _state.GetItemById(id) as TextBoardItem;
                if (model != null)
                {
                    model.Text = tb.Text;
                    if (!string.IsNullOrEmpty(tb.Text))
                    {
                        tb.Width = double.NaN;
                        tb.Height = double.NaN;
                    }

                    OnTextChanged?.Invoke(id);
                }
            }
        };

        textBox.LostFocus += (s, args) =>
        {
            if (s is TextBox tb)
            {
                tb.IsReadOnly = true;
                tb.IsReadOnlyCaretVisible = false;
                tb.Cursor = Cursors.SizeAll;

                Guid id = Guid.Empty;

                // Фиксируем актуальные размеры в модель после автоподгонки
                if (tb.Tag is Guid tagId)
                {
                    id = tagId;
                    var model = _state.GetItemById(id) as TextBoardItem;
                    if (model != null && !double.IsNaN(tb.ActualWidth) && !double.IsNaN(tb.ActualHeight))
                    {
                        tb.Width = tb.ActualWidth;
                        tb.Height = tb.ActualHeight;
                    }
                }

                if (id != Guid.Empty)
                    OnTextBoxLostFocus?.Invoke(tb, id);
                _saveBoard();
            }
        };

        textBox.GotFocus += (s, args) =>
        {
            if (s is TextBox tb && tb.Tag is Guid id)
            {
                OnTextBoxGotFocus?.Invoke(id);
            }
        };

        _saveBoard();
    }

    #endregion

    #region Изображение

    /// <summary>Создать UI-элемент для изображения и добавить на доску.</summary>
    public void CreateImageElement(ImageBoardItem item, BitmapSource? imageSource = null)
    {
        Logger.Debug<BoardElementFactory>($"CreateImageElement: id={item.Id}, pos=({item.X:F0},{item.Y:F0}), размер=({item.Width:F0}x{item.Height:F0}), source={imageSource?.GetType().Name ?? "base64"}");

        BitmapSource source;
        if (imageSource != null)
        {
            source = imageSource;
        }
        else
        {
            try
            {
                var bytes = Convert.FromBase64String(item.ImageDataBase64);
                var bitmap = new BitmapImage();
                using var stream = new System.IO.MemoryStream(bytes);
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                source = bitmap;
            }
            catch (Exception ex)
            {
                Logger.Error<BoardElementFactory>($"CreateImageElement: ошибка декодирования base64 для {item.Id}", ex);
                return;
            }
        }

        var displayWidth = item.Width;
        var displayHeight = item.Height;

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(62, 62, 66)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4),
            Width = displayWidth,
            Height = displayHeight,
            Tag = item.Id,
            Cursor = Cursors.Hand,
            SnapsToDevicePixels = true
        };

        var image = new Image
        {
            Source = source,
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        border.Child = image;
        border.MouseLeftButtonDown += (s, e) =>
        {
            if (s is Border b && OnImageLeftClick != null)
                OnImageLeftClick(b, e);
        };
        border.MouseRightButtonDown += (s, e) =>
        {
            if (s is FrameworkElement fe && OnElementRightClick != null)
                OnElementRightClick(fe, e);
        };

        Canvas.SetLeft(border, item.X);
        Canvas.SetTop(border, item.Y);
        _canvas.Children.Add(border);

        _state.BoardItems.Add(item);
        _state.ElementMap[item.Id] = border;

        _saveBoard();
    }

    #endregion

    #region Штрих (кисть)

    /// <summary>Словарь оригинальных данных штрихов для ресайза.</summary>
    public Dictionary<Guid, (List<double> OriginalPoints, List<int> FigureLengths, double OrigWidth, double OrigHeight, Path Path)> StrokeOriginalData { get; } = new();

    /// <summary>Список обработчиков изменения зума для обновления толщины штрихов.</summary>
    public List<Action<double, double>> ZoomChangedHandlers { get; } = new();

    /// <summary>Создать UI-элемент (Border+Path) для штриха и добавить на доску.</summary>
    public void CreateStrokeElement(StrokeBoardItem item)
    {
        Logger.Debug<BoardElementFactory>($"CreateStrokeElement: id={item.Id}, pos=({item.X:F0},{item.Y:F0}), размер=({item.Width:F0}x{item.Height:F0}), точек={item.Points.Count / 2}, фигур={item.FigureLengths.Count}");

        var border = BuildStrokeBorder(item);

        Canvas.SetLeft(border, item.X);
        Canvas.SetTop(border, item.Y);

        // Если размеры были изменены — пересчитываем геометрию
        if (StrokeOriginalData.TryGetValue(item.Id, out var data))
        {
            var (origPoints, figLens, origW, origH, path) = data;
            if (origW > 0 && origH > 0 && (Math.Abs(origW - item.Width) > 0.1 || Math.Abs(origH - item.Height) > 0.1))
            {
                UpdateStrokePathGeometry(path, origPoints, figLens, origW, origH, item.Width, item.Height);
            }
        }

        _canvas.Children.Add(border);
        _state.BoardItems.Add(item);
        _state.ElementMap[item.Id] = border;

        _saveBoard();
    }

    /// <summary>Построить Border с Path внутри по модели StrokeBoardItem.</summary>
    private Border BuildStrokeBorder(StrokeBoardItem item)
    {
        var path = BuildStrokePath(item);

        var border = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(2),
            Width = item.Width,
            Height = item.Height,
            Tag = item.Id,
            Cursor = Cursors.SizeAll
        };

        border.Child = path;

        border.MouseLeftButtonDown += (s, e) =>
        {
            if (s is Border b && OnStrokeLeftClick != null)
                OnStrokeLeftClick(b, e);
        };
        border.MouseRightButtonDown += (s, e) =>
        {
            if (s is FrameworkElement fe && OnElementRightClick != null)
                OnElementRightClick(fe, e);
        };

        // Сохраняем оригинальные размеры и Path для пересчёта при ресайзе
        var origW = item.OriginalWidth > 0 ? item.OriginalWidth : item.Width;
        var origH = item.OriginalHeight > 0 ? item.OriginalHeight : item.Height;
        StrokeOriginalData[item.Id] = (item.Points.ToList(), item.FigureLengths.ToList(), origW, origH, path);

        // Обновляем толщину линии при изменении зума
        ZoomChangedHandlers.Add((_, newZoom) =>
        {
            path.StrokeThickness = item.StrokeThickness / newZoom;
        });

        return border;
    }

    /// <summary>Построить WPF Path по модели StrokeBoardItem (локальные координаты).</summary>
    public Path BuildStrokePath(StrokeBoardItem item)
    {
        var geometry = new PathGeometry();

        if (item.FigureLengths.Count > 0)
        {
            // Многофигурный штрих (непрерывное рисование)
            int pointIndex = 0;
            foreach (var figLen in item.FigureLengths)
            {
                if (figLen < 1 || pointIndex + 1 >= item.Points.Count) continue;

                var figure = new PathFigure
                {
                    StartPoint = new Point(item.Points[pointIndex], item.Points[pointIndex + 1]),
                    IsClosed = false
                };
                pointIndex += 2;

                var segments = new PathSegmentCollection();
                for (int j = 1; j < figLen && pointIndex + 1 < item.Points.Count; j++)
                {
                    segments.Add(new LineSegment(
                        new Point(item.Points[pointIndex], item.Points[pointIndex + 1]), true));
                    pointIndex += 2;
                }
                figure.Segments = segments;
                geometry.Figures.Add(figure);
            }
        }
        else
        {
            // Обычный однофигурный штрих (обратная совместимость)
            var figure = new PathFigure();
            var segments = new PathSegmentCollection();

            if (item.Points.Count >= 2)
            {
                figure.StartPoint = new Point(item.Points[0], item.Points[1]);

                for (int i = 2; i < item.Points.Count - 1; i += 2)
                {
                    segments.Add(new LineSegment(
                        new Point(item.Points[i], item.Points[i + 1]), true));
                }
            }

            figure.Segments = segments;
            figure.IsClosed = false;
            geometry.Figures.Add(figure);
        }

        var path = new Path
        {
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(item.ColorHex)),
            StrokeThickness = item.StrokeThickness / _state.CurrentZoom,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Data = geometry,
            IsHitTestVisible = false
        };

        return path;
    }

    /// <summary>Обновить толщину всех штрихов при изменении зума.</summary>
    public void UpdateStrokeThicknesses()
    {
        foreach (var handler in ZoomChangedHandlers)
        {
            handler(0, _state.CurrentZoom);
        }
    }

    /// <summary>Пересчитать координаты точек Path при изменении размера Border-а.</summary>
    public void UpdateStrokePathGeometry(Path path, List<double> origPoints,
        List<int> figureLengths, double origWidth, double origHeight, double newWidth, double newHeight)
    {
        if (origWidth <= 0 || origHeight <= 0 || newWidth <= 0 || newHeight <= 0) return;

        var scaleX = newWidth / origWidth;
        var scaleY = newHeight / origHeight;

        var geometry = new PathGeometry();

        if (figureLengths.Count > 0)
        {
            int pointIndex = 0;
            foreach (var figLen in figureLengths)
            {
                if (figLen < 1 || pointIndex + 1 >= origPoints.Count) continue;

                var figure = new PathFigure
                {
                    StartPoint = new Point(origPoints[pointIndex] * scaleX, origPoints[pointIndex + 1] * scaleY),
                    IsClosed = false
                };
                pointIndex += 2;

                var segments = new PathSegmentCollection();
                for (int j = 1; j < figLen && pointIndex + 1 < origPoints.Count; j++)
                {
                    segments.Add(new LineSegment(
                        new Point(origPoints[pointIndex] * scaleX, origPoints[pointIndex + 1] * scaleY), true));
                    pointIndex += 2;
                }
                figure.Segments = segments;
                geometry.Figures.Add(figure);
            }
        }
        else
        {
            var figure = new PathFigure();
            var segments = new PathSegmentCollection();

            if (origPoints.Count >= 2)
            {
                figure.StartPoint = new Point(origPoints[0] * scaleX, origPoints[1] * scaleY);

                for (int i = 2; i < origPoints.Count - 1; i += 2)
                {
                    segments.Add(new LineSegment(
                        new Point(origPoints[i] * scaleX, origPoints[i + 1] * scaleY), true));
                }
            }

            figure.Segments = segments;
            figure.IsClosed = false;
            geometry.Figures.Add(figure);
        }

        path.Data = geometry;
    }

    #endregion

    #region Вспомогательные методы

    /// <summary>Удалить UI-элемент и модель с доски по Id.</summary>
    public void RemoveElementById(Guid id)
    {
        var item = _state.GetItemById(id);
        if (item == null)
        {
            Logger.Warn<BoardElementFactory>($"RemoveElementById: элемент {id} не найден");
            return;
        }

        Logger.Debug<BoardElementFactory>($"RemoveElementById: id={id}, тип={item.GetType().Name}");
        _state.BoardItems.Remove(item);

        if (_state.ElementMap.TryGetValue(id, out var element))
        {
            _canvas.Children.Remove(element);
            _state.ElementMap.Remove(id);
        }

        if (_state.DraggedElement?.Tag is Guid draggedId && draggedId == id)
        {
            _state.DraggedElement = null;
        }

        _state.SelectedItemIds.Remove(id);
        StrokeOriginalData.Remove(id);
        _saveBoard();
    }

    /// <summary>Восстановить элемент на доску (для undo удаления).</summary>
    public void RestoreItem(BoardItem item)
    {
        switch (item)
        {
            case TextBoardItem textItem:
                CreateTextElement(textItem);
                break;
            case ImageBoardItem imageItem:
                CreateImageElement(imageItem, null);
                break;
            case StrokeBoardItem strokeItem:
                CreateStrokeElement(strokeItem);
                break;
        }
    }

    /// <summary>Поднять элемент наверх (Z-порядок).</summary>
    public void BringToFront(FrameworkElement element)
    {
        if (_canvas.Children.Contains(element))
        {
            _canvas.Children.Remove(element);
            _canvas.Children.Add(element);
        }
    }

    /// <summary>Деактивировать все текстовые поля (перевести в режим выделения).</summary>
    public void DeactivateAllTextBoxes()
    {
        foreach (var kvp in _state.ElementMap)
        {
            if (kvp.Value is TextBox tb && !tb.IsReadOnly)
            {
                tb.IsReadOnly = true;
                tb.IsReadOnlyCaretVisible = false;
                tb.Cursor = Cursors.SizeAll;
            }
        }
    }

    /// <summary>Деактивировать все текстовые поля и снять фокус.</summary>
    public void DeactivateAllTextBoxesAndClearFocus()
    {
        DeactivateAllTextBoxes();
        _canvas.Focus();
    }

    #endregion
}
