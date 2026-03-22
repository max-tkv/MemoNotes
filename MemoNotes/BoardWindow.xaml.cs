using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MemoNotes.Models;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using Cursor = System.Windows.Input.Cursor;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace MemoNotes;

public partial class BoardWindow : Window
{
    #region Поля

    private const string BoardDataFilePath = "board.memo";
    private const double MinZoom = 0.1;
    private const double MaxZoom = 5.0;
    private const double ZoomStep = 0.1;
    private const double DotSpacing = 30;
    private const double DotRadius = 1.5;

    private double _currentZoom = 1.0;
    private bool _isPanning;
    private Point _panStartPoint;
    private Point _scrollStartOffset;

    private FrameworkElement? _draggedElement;
    private Point _dragStartPoint;
    private Point _elementStartPos;
    // Начальные позиции всех перетаскиваемых элементов (для группового перетаскивания)
    private readonly Dictionary<Guid, Point> _dragStartPositions = new();
    private Border? _selectedImageBorder;

    // Resize handles (ручки изменения размера)
    private bool _isResizing;
    private ResizeHandlePosition? _activeResizeHandle;
    private Point _resizeStartPoint;
    private Rect _resizeInitialBounds;
    private Guid _resizeItemId = Guid.Empty;
    private readonly List<System.Windows.Shapes.Rectangle> _resizeHandles = new();
    private const double HandleSize = 8;

    // Множественное выделение (rubber band)
    private bool _isSelecting;
    private Point _selectionStartPoint;
    private System.Windows.Shapes.Rectangle? _selectionRectangle;
    private readonly HashSet<Guid> _selectedItemIds = new();

    private readonly List<BoardItem> _boardItems = new();
    private readonly Dictionary<Guid, FrameworkElement> _elementMap = new();

    #endregion

    #region Конструктор

    public BoardWindow()
    {
        InitializeComponent();

        Width = Properties.Settings.Default.BoardWindowWidth;
        Height = Properties.Settings.Default.BoardHeight;
        Topmost = Properties.Settings.Default.TopmostTextBoxWindow;
        RefreshPinnedButtonState();

        DrawDotGrid();
        LoadBoard();

        PreviewKeyDown += BoardWindow_PreviewKeyDown;

        BoardCanvas.Width = 4000;
        BoardCanvas.Height = 4000;
    }

    #endregion

    #region Сетка-фон

    private void DrawDotGrid()
    {
        var canvasWidth = 4000;
        var canvasHeight = 4000;

        for (double x = 0; x < canvasWidth; x += DotSpacing)
        {
            for (double y = 0; y < canvasHeight; y += DotSpacing)
            {
                var dot = new Ellipse
                {
                    Width = DotRadius * 2,
                    Height = DotRadius * 2,
                    Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255))
                };
                Canvas.SetLeft(dot, x - DotRadius);
                Canvas.SetTop(dot, y - DotRadius);
                BoardCanvas.Children.Insert(0, dot);
            }
        }
    }

    #endregion

    #region Зум (масштабирование)

    private void BoardCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var mousePos = e.GetPosition(BoardCanvas);

        var zoomFactor = e.Delta > 0 ? ZoomStep : -ZoomStep;
        var newZoom = _currentZoom + zoomFactor;

        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - _currentZoom) < 0.001) return;

        _currentZoom = newZoom;
        BoardScaleTransform.ScaleX = _currentZoom;
        BoardScaleTransform.ScaleY = _currentZoom;

        ZoomText.Text = $"{_currentZoom * 100:F0}%";

        var scrollViewer = BoardScrollViewer;
        var scrollOffsetX = scrollViewer.HorizontalOffset;
        var scrollOffsetY = scrollViewer.VerticalOffset;

        var relativeX = mousePos.X * _currentZoom;
        var relativeY = mousePos.Y * _currentZoom;

        scrollViewer.ScrollToHorizontalOffset(scrollOffsetX + relativeX * zoomFactor / _currentZoom);
        scrollViewer.ScrollToVerticalOffset(scrollOffsetY + relativeY * zoomFactor / _currentZoom);

        // Обновляем размеры и позиции resize handles при изменении зума
        if (_resizeItemId != Guid.Empty)
        {
            var resizeItem = _boardItems.FirstOrDefault(i => i.Id == _resizeItemId);
            if (resizeItem != null)
            {
                UpdateResizeHandlesPosition(resizeItem);
            }
        }

        e.Handled = true;
    }

    private void ResetZoom_Click(object sender, RoutedEventArgs e)
    {
        _currentZoom = 1.0;
        BoardScaleTransform.ScaleX = 1.0;
        BoardScaleTransform.ScaleY = 1.0;
        ZoomText.Text = "100%";
    }

    #endregion

    #region Панорамирование (перемещение доски)

    private void BoardCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var isCtrlPressed = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);

        // Панорамирование средней кнопкой мыши
        if (e.MiddleButton == MouseButtonState.Pressed)
        {
            DeselectImageBorder();
            StartPan(e);
            e.Handled = true;
        }
        // Панорамирование Ctrl + Правая кнопка мыши
        else if (e.ChangedButton == MouseButton.Right && e.ButtonState == MouseButtonState.Pressed && isCtrlPressed)
        {
            DeselectImageBorder();
            StartPan(e);
            e.Handled = true;
        }
        // Панорамирование Ctrl + Левая кнопка мыши
        else if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed && isCtrlPressed)
        {
            DeselectImageBorder();
            StartPan(e);
            e.Handled = true;
        }
        // Клик по пустому пространству — начать выделение рамкой
        else if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
        {
            // Проверяем что клик не на элементах доски (TextBox, Image, Border)
            if (e.OriginalSource is not System.Windows.Controls.TextBox
                && e.OriginalSource is not System.Windows.Controls.Image
                && e.OriginalSource is not Border)
            {
                ClearSelection();
                StartSelection(e);
                e.Handled = true;
            }
        }
    }

    private void StartPan(MouseButtonEventArgs e)
    {
        _isPanning = true;
        _panStartPoint = e.GetPosition(BoardScrollViewer);
        _scrollStartOffset = new Point(
            BoardScrollViewer.HorizontalOffset,
            BoardScrollViewer.VerticalOffset);
        BoardCanvas.CaptureMouse();
    }

    private void StopPan()
    {
        if (_isPanning)
        {
            _isPanning = false;
            BoardCanvas.ReleaseMouseCapture();
        }
    }

    private void BoardCanvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        StopPan();
        e.Handled = true;
    }

    #region Drag & Drop изображений

    private void BoardCanvas_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Any(f => IsImageFile(f)))
            {
                e.Effects = DragDropEffects.Copy;
                return;
            }
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void BoardCanvas_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Any(f => IsImageFile(f)))
            {
                e.Effects = DragDropEffects.Copy;
                return;
            }
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void BoardCanvas_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files == null) return;

        // Получаем позицию дропа на Canvas
        var dropPos = e.GetPosition(BoardCanvas);

        foreach (var file in files)
        {
            if (!IsImageFile(file)) continue;

            try
            {
                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.UriSource = new Uri(file, UriKind.Absolute);
                bitmapImage.EndInit();
                bitmapImage.Freeze();

                var fileBytes = File.ReadAllBytes(file);
                var base64 = Convert.ToBase64String(fileBytes);

                AddImageElement(bitmapImage, base64, bitmapImage.PixelWidth, bitmapImage.PixelHeight,
                    dropPos.X - bitmapImage.PixelWidth / 2,
                    dropPos.Y - bitmapImage.PixelHeight / 2);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка загрузки изображения (drag & drop): {ex.Message}");
            }
        }

        e.Handled = true;
    }

    private static bool IsImageFile(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tiff" or ".webp" or ".ico";
    }

    #endregion

    #region Множественное выделение (rubber band)

    private void StartSelection(MouseButtonEventArgs e)
    {
        _isSelecting = true;
        _selectionStartPoint = e.GetPosition(BoardCanvas);
        _selectedItemIds.Clear();

        _selectionRectangle = new System.Windows.Shapes.Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromArgb(180, 0, 120, 212)),
            StrokeThickness = 1,
            Fill = new SolidColorBrush(Color.FromArgb(30, 0, 120, 212)),
            StrokeDashArray = new DoubleCollection { 4, 2 },
            Width = 0,
            Height = 0,
            IsHitTestVisible = false
        };

        Canvas.SetLeft(_selectionRectangle, _selectionStartPoint.X);
        Canvas.SetTop(_selectionRectangle, _selectionStartPoint.Y);
        BoardCanvas.Children.Add(_selectionRectangle);
    }

    private void UpdateSelection(MouseEventArgs e)
    {
        if (!_isSelecting || _selectionRectangle == null) return;

        var currentPos = e.GetPosition(BoardCanvas);
        var x = Math.Min(_selectionStartPoint.X, currentPos.X);
        var y = Math.Min(_selectionStartPoint.Y, currentPos.Y);
        var width = Math.Abs(currentPos.X - _selectionStartPoint.X);
        var height = Math.Abs(currentPos.Y - _selectionStartPoint.Y);

        Canvas.SetLeft(_selectionRectangle, x);
        Canvas.SetTop(_selectionRectangle, y);
        _selectionRectangle.Width = width;
        _selectionRectangle.Height = height;

        // Выделяем элементы, попавшие в рамку
        var selectionRect = new Rect(x, y, width, height);
        _selectedItemIds.Clear();
        DeselectImageBorder();
        HideResizeHandles();

        foreach (var kvp in _elementMap)
        {
            var element = kvp.Value;
            var item = _boardItems.FirstOrDefault(i => i.Id == kvp.Key);
            if (item == null) continue;

            var elementRect = new Rect(item.X, item.Y, item.Width, item.Height);
            if (selectionRect.IntersectsWith(elementRect))
            {
                _selectedItemIds.Add(kvp.Key);
                HighlightElement(element, true);
            }
            else
            {
                HighlightElement(element, false);
            }
        }
    }

    private void EndSelection()
    {
        if (_selectionRectangle != null)
        {
            BoardCanvas.Children.Remove(_selectionRectangle);
            _selectionRectangle = null;
        }
        _isSelecting = false;
    }

    private void ClearSelection()
    {
        EndSelection();
        _selectedItemIds.Clear();

        // Завершаем редактирование всех текстовых полей и снимаем фокус
        DeactivateAllTextBoxesAndClearFocus();

        // Снимаем подсветку со всех элементов
        foreach (var kvp in _elementMap)
        {
            HighlightElement(kvp.Value, false);
        }
        DeselectImageBorder();
        HideResizeHandles();
    }

    private void DeactivateAllTextBoxes()
    {
        foreach (var kvp in _elementMap)
        {
            if (kvp.Value is System.Windows.Controls.TextBox tb && !tb.IsReadOnly)
            {
                tb.IsReadOnly = true;
                tb.IsReadOnlyCaretVisible = false;
                tb.Cursor = System.Windows.Input.Cursors.SizeAll;
            }
        }
    }

    private void DeactivateAllTextBoxesAndClearFocus()
    {
        DeactivateAllTextBoxes();
        BoardCanvas.Focus();
    }

    private void HighlightElement(FrameworkElement element, bool highlight)
    {
        if (element is Border border)
        {
            if (highlight)
            {
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 120, 212));
                border.BorderThickness = new Thickness(2);
                _selectedImageBorder = border;
            }
            else
            {
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(62, 62, 66));
                border.BorderThickness = new Thickness(1);
                if (_selectedImageBorder == border)
                    _selectedImageBorder = null;
            }
        }
        else if (element is System.Windows.Controls.TextBox textBox)
        {
            if (highlight)
            {
                textBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 120, 212));
                textBox.BorderThickness = new Thickness(2);
            }
            else
            {
                textBox.BorderBrush = new SolidColorBrush(Color.FromRgb(62, 62, 66));
                textBox.BorderThickness = new Thickness(1);
            }
        }
    }

    #endregion

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_isPanning)
        {
            var currentPoint = e.GetPosition(BoardScrollViewer);
            var delta = currentPoint - _panStartPoint;

            BoardScrollViewer.ScrollToHorizontalOffset(_scrollStartOffset.X - delta.X);
            BoardScrollViewer.ScrollToVerticalOffset(_scrollStartOffset.Y - delta.Y);
            return;
        }

        // Resize элементов
        if (_isResizing && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateResize(e);
            return;
        }

        // Обновляем рамку выделения
        if (_isSelecting && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateSelection(e);
            return;
        }

        if (_draggedElement != null && e.LeftButton == MouseButtonState.Pressed)
        {
            var currentPos = e.GetPosition(BoardCanvas);
            var deltaX = currentPos.X - _dragStartPoint.X;
            var deltaY = currentPos.Y - _dragStartPoint.Y;

            // Перемещаем все выделенные элементы
            foreach (var kvp in _dragStartPositions)
            {
                var id = kvp.Key;
                var startPos = kvp.Value;

                var newX = startPos.X + deltaX;
                var newY = startPos.Y + deltaY;

                if (_elementMap.TryGetValue(id, out var element))
                {
                    Canvas.SetLeft(element, newX);
                    Canvas.SetTop(element, newY);
                }

                var item = _boardItems.FirstOrDefault(i => i.Id == id);
                if (item != null)
                {
                    item.X = newX;
                    item.Y = newY;

                    // Обновляем resize handles для перемещаемого элемента
                    if (_selectedItemIds.Count == 1 && _resizeItemId == id)
                    {
                        UpdateResizeHandlesPosition(item);
                    }
                }
            }
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);

        // Завершаем resize
        if (_isResizing && e.ChangedButton == MouseButton.Left)
        {
            EndResize();
        }

        // Завершаем выделение рамкой
        if (_isSelecting && e.ChangedButton == MouseButton.Left)
        {
            EndSelection();
            // Если выбран ровно один элемент — показать resize handles
            if (_selectedItemIds.Count == 1)
            {
                ShowResizeHandles(_selectedItemIds.First());
            }
        }

        // Останавливаем перетаскивание элемента при отпускании ЛКМ
        if (_draggedElement != null && e.ChangedButton == MouseButton.Left)
        {
            _draggedElement.ReleaseMouseCapture();
            _draggedElement = null;
            _dragStartPositions.Clear();
            SaveBoard();

            // Обновляем resize handles после перетаскивания
            if (_selectedItemIds.Count == 1)
            {
                var id = _selectedItemIds.First();
                ShowResizeHandles(id);
            }
        }

        // Останавливаем панорамирование при отпускании любой кнопки
        if (_isPanning && (e.ChangedButton == MouseButton.Middle
                          || e.ChangedButton == MouseButton.Left
                          || e.ChangedButton == MouseButton.Right))
        {
            StopPan();
        }
    }

    #endregion

    #region Добавление текстового блока

    private void AddTextButton_Click(object sender, RoutedEventArgs e)
    {
        AddTextBlock();
    }

    private void AddTextBlock(string? text = null, double x = 0, double y = 0)
    {
        if (x == 0 && y == 0)
        {
            var center = GetVisibleCenter();
            x = center.X;
            y = center.Y;
        }

        var textBox = new System.Windows.Controls.TextBox
        {
            Style = (Style)FindResource("BoardTextStyle"),
            Text = text ?? string.Empty,
            Width = 200,
            Height = 100,
            Tag = Guid.Empty,
            FontSize = 16,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            Cursor = System.Windows.Input.Cursors.SizeAll
        };

        textBox.PreviewMouseLeftButtonDown += TextElement_PreviewMouseLeftButtonDown;

        Canvas.SetLeft(textBox, x);
        Canvas.SetTop(textBox, y);
        BoardCanvas.Children.Add(textBox);

        var item = new TextBoardItem
        {
            X = x,
            Y = y,
            Width = 200,
            Height = 100,
            Text = text ?? string.Empty,
            FontSize = 16
        };

        textBox.Tag = item.Id;
        _boardItems.Add(item);
        _elementMap[item.Id] = textBox;

        textBox.TextChanged += (s, args) =>
        {
            if (s is System.Windows.Controls.TextBox tb && tb.Tag is Guid id)
            {
                var model = _boardItems.FirstOrDefault(i => i.Id == id) as TextBoardItem;
                if (model != null)
                {
                    model.Text = tb.Text;
                    if (!string.IsNullOrEmpty(tb.Text))
                    {
                        tb.Width = double.NaN;
                        tb.Height = double.NaN;
                    }
                }
            }
        };

        textBox.LostFocus += (s, args) =>
        {
            if (s is System.Windows.Controls.TextBox tb)
            {
                tb.IsReadOnly = true;
                tb.IsReadOnlyCaretVisible = false;
                tb.Cursor = System.Windows.Input.Cursors.SizeAll;
            }
        };

        SaveBoard();
    }

    private void TextElement_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox) return;

        // Двойной клик — режим редактирования
        if (e.ClickCount == 2)
        {
            // Отменяем перетаскивание если было
            if (_draggedElement == textBox)
            {
                _draggedElement.ReleaseMouseCapture();
                _draggedElement = null;
            }

            // Поднимаем элемент наверх
            BringToFront(textBox);

            // Включаем режим редактирования
            textBox.IsReadOnly = false;
            textBox.IsReadOnlyCaretVisible = true;
            textBox.Cursor = System.Windows.Input.Cursors.IBeam;
            textBox.Focus();
            Keyboard.Focus(textBox);
            e.Handled = true;
            return;
        }

        // Одиночный клик — перетаскивание (сначала деактивируем все текстовые поля)
        e.Handled = true;
        DeactivateAllTextBoxes();

        // Если кликнутый элемент уже в выделении — перетаскиваем всю группу
        var clickedInSelection = textBox.Tag is Guid clickedId && _selectedItemIds.Contains(clickedId);
        if (!clickedInSelection)
        {
            // Снимаем подсветку со всех элементов и очищаем выделение
            _selectedItemIds.Clear();
            HideResizeHandles();
            foreach (var kvp in _elementMap)
            {
                HighlightElement(kvp.Value, false);
            }
            // Выделяем только кликнутый TextBox
            if (textBox.Tag is Guid id)
            {
                _selectedItemIds.Add(id);
                HighlightElement(textBox, true);
                ShowResizeHandles(id);
            }
        }

        BringToFront(textBox);
        StartDrag(textBox, e);
    }

    private void BringToFront(FrameworkElement element)
    {
        if (BoardCanvas.Children.Contains(element))
        {
            BoardCanvas.Children.Remove(element);
            BoardCanvas.Children.Add(element);
        }
    }

    #endregion

    #region Добавление изображения

    private void AddImageButton_Click(object sender, RoutedEventArgs e)
    {
        AddImageFromFile();
    }

    private void AddImageFromFile(double x = 0, double y = 0)
    {
        var openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff;*.webp|Все файлы|*.*",
            Title = "Выберите изображение"
        };

        if (openFileDialog.ShowDialog() == true)
        {
            try
            {
                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.UriSource = new Uri(openFileDialog.FileName, UriKind.Absolute);
                bitmapImage.EndInit();
                bitmapImage.Freeze();

                // Получаем base64 из файла
                var fileBytes = File.ReadAllBytes(openFileDialog.FileName);
                var base64 = Convert.ToBase64String(fileBytes);

                AddImageElement(bitmapImage, base64, bitmapImage.PixelWidth, bitmapImage.PixelHeight, x, y);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Ошибка загрузки изображения: {ex.Message}");
            }
        }
    }

    private void AddImageFromClipboard(double x = 0, double y = 0)
    {
        if (!Clipboard.ContainsImage()) return;

        var interopBitmap = Clipboard.GetImage();
        if (interopBitmap == null) return;

        var bitmapImage = new BitmapImage();
        bitmapImage.BeginInit();
        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
        bitmapImage.CreateOptions = BitmapCreateOptions.PreservePixelFormat;

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(interopBitmap));
        using (var memoryStream = new MemoryStream())
        {
            encoder.Save(memoryStream);
            memoryStream.Seek(0, SeekOrigin.Begin);
            bitmapImage.StreamSource = memoryStream;
            bitmapImage.EndInit();
            bitmapImage.Freeze();

            memoryStream.Seek(0, SeekOrigin.Begin);
            var base64 = Convert.ToBase64String(memoryStream.ToArray());
            AddImageElement(bitmapImage, base64, bitmapImage.PixelWidth, bitmapImage.PixelHeight, x, y);
        }
    }

    private void AddImageElement(BitmapSource imageSource, string base64, double imgWidth, double imgHeight, double x, double y)
    {
        if (x == 0 && y == 0)
        {
            var center = GetVisibleCenter();
            x = center.X - imgWidth / 2;
            y = center.Y - imgHeight / 2;
        }

        var maxWidth = 600.0;
        var maxHeight = 400.0;
        var displayWidth = imgWidth;
        var displayHeight = imgHeight;

        if (displayWidth > maxWidth)
        {
            var scale = maxWidth / displayWidth;
            displayWidth = maxWidth;
            displayHeight *= scale;
        }

        if (displayHeight > maxHeight)
        {
            var scale = maxHeight / displayHeight;
            displayHeight *= scale;
            displayWidth *= scale;
        }

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(62, 62, 66)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4),
            Width = displayWidth + 8,
            Height = displayHeight + 8,
            Tag = Guid.Empty,
            Cursor = System.Windows.Input.Cursors.Hand
        };

        var image = new System.Windows.Controls.Image
        {
            Source = imageSource,
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch
        };

        border.Child = image;

        border.MouseLeftButtonDown += ImageElement_MouseLeftButtonDown;
        border.MouseRightButtonDown += ImageElement_MouseRightButtonDown;

        Canvas.SetLeft(border, x);
        Canvas.SetTop(border, y);
        BoardCanvas.Children.Add(border);

        var item = new ImageBoardItem
        {
            X = x,
            Y = y,
            Width = displayWidth + 8,
            Height = displayHeight + 8,
            ImageDataBase64 = base64
        };

        border.Tag = item.Id;
        _boardItems.Add(item);
        _elementMap[item.Id] = border;

        SaveBoard();
    }

    /// <summary>
    /// Перегрузка для восстановления изображения с сохранёнными размерами и Id.
    /// </summary>
    private void AddImageElement(BitmapSource imageSource, string base64, double x, double y, double displayWidth, double displayHeight, Guid existingId)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(62, 62, 66)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4),
            Width = displayWidth,
            Height = displayHeight,
            Tag = existingId,
            Cursor = System.Windows.Input.Cursors.Hand
        };

        var image = new System.Windows.Controls.Image
        {
            Source = imageSource,
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch
        };

        border.Child = image;

        border.MouseLeftButtonDown += ImageElement_MouseLeftButtonDown;
        border.MouseRightButtonDown += ImageElement_MouseRightButtonDown;

        Canvas.SetLeft(border, x);
        Canvas.SetTop(border, y);
        BoardCanvas.Children.Add(border);

        var item = new ImageBoardItem
        {
            Id = existingId,
            X = x,
            Y = y,
            Width = displayWidth,
            Height = displayHeight,
            ImageDataBase64 = base64
        };

        _boardItems.Add(item);
        _elementMap[item.Id] = border;
    }

    private void ImageElement_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border) return;
        e.Handled = true;
        // Деактивируем редактирование текстовых полей и убираем фокус
        DeactivateAllTextBoxesAndClearFocus();

        // Если кликнутое изображение уже в выделении — перетаскиваем всю группу
        var clickedInSelection = border.Tag is Guid clickedImgId && _selectedItemIds.Contains(clickedImgId);
        if (!clickedInSelection)
        {
            // Снимаем подсветку со всех элементов и очищаем выделение
            _selectedItemIds.Clear();
            foreach (var kvp in _elementMap)
            {
                HighlightElement(kvp.Value, false);
            }
            // Выделяем только кликнутое изображение
            if (border.Tag is Guid imgId)
            {
                _selectedItemIds.Add(imgId);
                ShowResizeHandles(imgId);
            }
        }

        BringToFront(border);
        SelectImageBorder(border);
        StartDrag(border, e);
    }

    private void SelectImageBorder(Border border)
    {
        // Снимаем подсветку с предыдущего
        DeselectImageBorder();

        // Подсвечиваем новый
        _selectedImageBorder = border;
        border.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 120, 212));
        border.BorderThickness = new Thickness(2);
    }

    private void DeselectImageBorder()
    {
        if (_selectedImageBorder != null)
        {
            _selectedImageBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(62, 62, 66));
            _selectedImageBorder.BorderThickness = new Thickness(1);
            _selectedImageBorder = null;
        }
    }

    private void ImageElement_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not Guid id) return;

        var contextMenu = new ContextMenu();

        var deleteItem = new MenuItem
        {
            Header = "🗑 Удалить",
            FontWeight = FontWeights.Normal
        };
        deleteItem.Click += (_, _) => DeleteBoardItem(id);
        contextMenu.Items.Add(deleteItem);

        element.ContextMenu = contextMenu;
    }

    #endregion

    #region Перетаскивание элементов

    private void StartDrag(FrameworkElement element, MouseButtonEventArgs e)
    {
        _draggedElement = element;
        _dragStartPoint = e.GetPosition(BoardCanvas);
        _elementStartPos = new Point(Canvas.GetLeft(element), Canvas.GetTop(element));

        // Запоминаем начальные позиции всех выделенных элементов
        _dragStartPositions.Clear();
        foreach (var id in _selectedItemIds)
        {
            var item = _boardItems.FirstOrDefault(i => i.Id == id);
            if (item != null)
            {
                _dragStartPositions[id] = new Point(item.X, item.Y);
            }
        }

        // Если кликнутый элемент ещё не в выделении — добавляем только его
        if (!_dragStartPositions.ContainsKey(Guid.Empty) && element.Tag is Guid clickedId)
        {
            if (!_dragStartPositions.ContainsKey(clickedId))
            {
                _dragStartPositions[clickedId] = _elementStartPos;
            }
        }

        element.CaptureMouse();
    }

    #endregion

    #region Удаление элементов

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItemIds.Count > 0)
        {
            // Удаляем все выделенные элементы
            var idsToDelete = _selectedItemIds.ToList();
            foreach (var id in idsToDelete)
            {
                DeleteBoardItem(id);
            }
            _selectedItemIds.Clear();
        }
        else if (_draggedElement?.Tag is Guid id)
        {
            DeleteBoardItem(id);
        }
    }

    private void DeleteBoardItem(Guid id)
    {
        var item = _boardItems.FirstOrDefault(i => i.Id == id);
        if (item == null) return;

        _boardItems.Remove(item);

        if (_elementMap.TryGetValue(id, out var element))
        {
            BoardCanvas.Children.Remove(element);
            _elementMap.Remove(id);
        }

        if (_draggedElement?.Tag is Guid draggedId && draggedId == id)
        {
            _draggedElement = null;
        }

        SaveBoard();
    }

    #endregion

    #region Сохранение и загрузка доски

    private void SaveBoard()
    {
        try
        {
            var data = new BoardSaveData
            {
                Items = _boardItems.ToList(),
                Zoom = _currentZoom,
                ScrollOffsetX = BoardScrollViewer.HorizontalOffset,
                ScrollOffsetY = BoardScrollViewer.VerticalOffset
            };

            var json = System.Text.Json.JsonSerializer.Serialize(data, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = false
            });

            File.WriteAllText(BoardDataFilePath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Ошибка сохранения доски: {ex.Message}");
        }
    }

    private void LoadBoard()
    {
        if (!File.Exists(BoardDataFilePath)) return;

        try
        {
            var json = File.ReadAllText(BoardDataFilePath);
            var data = System.Text.Json.JsonSerializer.Deserialize<BoardSaveData>(json);
            if (data?.Items == null) return;

            _currentZoom = data.Zoom;
            BoardScaleTransform.ScaleX = _currentZoom;
            BoardScaleTransform.ScaleY = _currentZoom;
            ZoomText.Text = $"{_currentZoom * 100:F0}%";

            // Сохраняем позицию скролла для восстановления после рендеринга
            var scrollX = data.ScrollOffsetX;
            var scrollY = data.ScrollOffsetY;

            foreach (var item in data.Items)
            {
                switch (item)
                {
                    case TextBoardItem textItem:
                        RestoreTextItem(textItem);
                        break;
                    case ImageBoardItem imageItem:
                        RestoreImageItem(imageItem);
                        break;
                }
            }

            // Восстанавливаем позицию скролла после отрисовки элементов
            Dispatcher.BeginInvoke(() =>
            {
                BoardScrollViewer.ScrollToHorizontalOffset(scrollX);
                BoardScrollViewer.ScrollToVerticalOffset(scrollY);
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Ошибка загрузки доски: {ex.Message}");
        }
    }

    private void RestoreTextItem(TextBoardItem item)
    {
        var textBox = new System.Windows.Controls.TextBox
        {
            Style = (Style)FindResource("BoardTextStyle"),
            Text = item.Text,
            Width = item.Width,
            Height = item.Height,
            Tag = item.Id,
            FontSize = item.FontSize,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            Cursor = System.Windows.Input.Cursors.SizeAll
        };

        textBox.PreviewMouseLeftButtonDown += TextElement_PreviewMouseLeftButtonDown;
        textBox.TextChanged += (s, args) =>
        {
            if (s is System.Windows.Controls.TextBox tb && tb.Tag is Guid id)
            {
                var model = _boardItems.FirstOrDefault(i => i.Id == id) as TextBoardItem;
                if (model != null)
                {
                    model.Text = tb.Text;
                }
            }
        };
        textBox.LostFocus += (s, args) =>
        {
            if (s is System.Windows.Controls.TextBox tb)
            {
                tb.IsReadOnly = true;
                tb.IsReadOnlyCaretVisible = false;
                tb.Cursor = System.Windows.Input.Cursors.SizeAll;
            }
        };

        Canvas.SetLeft(textBox, item.X);
        Canvas.SetTop(textBox, item.Y);
        BoardCanvas.Children.Add(textBox);

        _boardItems.Add(item);
        _elementMap[item.Id] = textBox;
    }

    private void RestoreImageItem(ImageBoardItem item)
    {
        try
        {
            var bytes = Convert.FromBase64String(item.ImageDataBase64);
            var image = new BitmapImage();
            using (var stream = new MemoryStream(bytes))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
            }

            AddImageElement(image, item.ImageDataBase64, item.X, item.Y, item.Width, item.Height, item.Id);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Ошибка восстановления изображения: {ex.Message}");
        }
    }

    #endregion

    #region Утилиты

    private Point GetVisibleCenter()
    {
        var scrollViewer = BoardScrollViewer;
        var scrollOffsetX = scrollViewer.HorizontalOffset;
        var scrollOffsetY = scrollViewer.VerticalOffset;
        var viewportWidth = scrollViewer.ViewportWidth;
        var viewportHeight = scrollViewer.ViewportHeight;

        var centerX = (scrollOffsetX + viewportWidth / 2) / _currentZoom;
        var centerY = (scrollOffsetY + viewportHeight / 2) / _currentZoom;

        return new Point(centerX - 100 + Random.Shared.Next(-50, 50),
                         centerY - 50 + Random.Shared.Next(-50, 50));
    }

    #endregion

    #region Обработчики окна

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        SaveBoard();
        Close();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var mainWindow = Application.Current.MainWindow;
        if (mainWindow is MainWindow settingsWindow)
        {
            settingsWindow.Topmost = true;
        }

        mainWindow?.Show();
        mainWindow?.Activate();
    }

    private void PinnedButton_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        RefreshPinnedButtonState();
        Properties.Settings.Default.TopmostTextBoxWindow = Topmost;
        Properties.Settings.Default.Save();
    }

    private void RefreshPinnedButtonState()
    {
        PinnedButton.Foreground = Topmost
            ? new SolidColorBrush(Color.FromRgb(62, 62, 66))
            : new SolidColorBrush(Color.FromRgb(255, 255, 255));
    }

    /// <summary>
    /// Мигание рамкой при активации из PopupButtonWindow.
    /// </summary>
    public void BlinkBorder()
    {
        var animation = new ColorAnimation
        {
            From = Color.FromRgb(28, 28, 28),
            To = Color.FromRgb(0, 120, 212),
            Duration = TimeSpan.FromMilliseconds(300),
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(3)
        };

        var borderBrush = new SolidColorBrush(Colors.Transparent);
        ToolbarBorder.BorderBrush = borderBrush;
        borderBrush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SaveBoard();
        Properties.Settings.Default.BoardWindowWidth = Width;
        Properties.Settings.Default.BoardHeight = Height;
        Properties.Settings.Default.Save();
        base.OnClosing(e);
    }

    private void BoardWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+V — вставка изображения
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (Clipboard.ContainsImage())
            {
                AddImageFromClipboard();
                e.Handled = true;
            }
        }

        // T — добавить текст (только если не в фокусе TextBox)
        if (e.Key == Key.T && !Keyboard.IsKeyDown(Key.LeftCtrl) && !Keyboard.IsKeyDown(Key.RightCtrl))
        {
            if (Keyboard.FocusedElement is not System.Windows.Controls.TextBox)
            {
                AddTextBlock();
                e.Handled = true;
            }
        }

        // Delete — удалить выделенные элементы
        if (e.Key == Key.Delete)
        {
            var focusedElement = Keyboard.FocusedElement;
            // Если в режиме редактирования TextBox — не перехватываем Delete (пусть работает стандартное удаление текста)
            if (focusedElement is System.Windows.Controls.TextBox tb && !tb.IsReadOnly)
            {
                return;
            }

            if (_selectedItemIds.Count > 0)
            {
                var idsToDelete = _selectedItemIds.ToList();
                foreach (var id in idsToDelete)
                {
                    DeleteBoardItem(id);
                }
                _selectedItemIds.Clear();
                e.Handled = true;
            }
            else if (_selectedImageBorder != null && _selectedImageBorder.Tag is Guid imageId)
            {
                DeleteBoardItem(imageId);
                _selectedImageBorder = null;
                e.Handled = true;
            }
            else if (focusedElement is System.Windows.Controls.TextBox focusedTb && focusedTb.Tag is Guid focusedId)
            {
                // Удаляем TextBox по которому кликнули (он в режиме выделения, не редактирования)
                DeleteBoardItem(focusedId);
                e.Handled = true;
            }
            else if (focusedElement is not System.Windows.Controls.TextBox && _boardItems.Count > 0)
            {
                var lastItem = _boardItems[_boardItems.Count - 1];
                DeleteBoardItem(lastItem.Id);
                e.Handled = true;
            }
        }

        // Escape — снять фокус с элемента
        if (e.Key == Key.Escape)
        {
            Keyboard.Focus(this);
        }
    }

    #endregion

    #region Resize handles (ручки изменения размера)

    private enum ResizeHandlePosition
    {
        TopLeft, TopCenter, TopRight,
        MiddleLeft, MiddleRight,
        BottomLeft, BottomCenter, BottomRight
    }

    private void ShowResizeHandles(Guid itemId)
    {
        HideResizeHandles();

        var item = _boardItems.FirstOrDefault(i => i.Id == itemId);
        if (item == null) return;

        _resizeItemId = itemId;

        var positions = new[]
        {
            ResizeHandlePosition.TopLeft, ResizeHandlePosition.TopCenter, ResizeHandlePosition.TopRight,
            ResizeHandlePosition.MiddleLeft, ResizeHandlePosition.MiddleRight,
            ResizeHandlePosition.BottomLeft, ResizeHandlePosition.BottomCenter, ResizeHandlePosition.BottomRight
        };

        foreach (var pos in positions)
        {
            var visualSize = HandleSize / _currentZoom;
            var handle = new System.Windows.Shapes.Rectangle
            {
                Width = visualSize,
                Height = visualSize,
                Fill = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
                Stroke = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
                StrokeThickness = 1 / _currentZoom,
                Cursor = GetResizeCursor(pos),
                Tag = pos
            };

            handle.PreviewMouseLeftButtonDown += ResizeHandle_PreviewMouseLeftButtonDown;

            BoardCanvas.Children.Add(handle);
            _resizeHandles.Add(handle);
        }

        UpdateResizeHandlesPosition(item);
    }

    private void HideResizeHandles()
    {
        foreach (var handle in _resizeHandles)
        {
            BoardCanvas.Children.Remove(handle);
        }
        _resizeHandles.Clear();
        _resizeItemId = Guid.Empty;
        _isResizing = false;
        _activeResizeHandle = null;
    }

    private void UpdateResizeHandlesPosition(BoardItem item)
    {
        var visualHandleSize = HandleSize / _currentZoom;

        foreach (var handle in _resizeHandles)
        {
            if (handle.Tag is not ResizeHandlePosition pos) continue;

            // Обновляем визуальный размер при каждом позиционировании (на случай изменения зума)
            handle.Width = visualHandleSize;
            handle.Height = visualHandleSize;
            handle.StrokeThickness = 1 / _currentZoom;

            var x = pos switch
            {
                ResizeHandlePosition.TopLeft or ResizeHandlePosition.MiddleLeft or ResizeHandlePosition.BottomLeft
                    => item.X - visualHandleSize / 2,
                ResizeHandlePosition.TopCenter or ResizeHandlePosition.BottomCenter
                    => item.X + item.Width / 2 - visualHandleSize / 2,
                _ => item.X + item.Width - visualHandleSize / 2
            };

            var y = pos switch
            {
                ResizeHandlePosition.TopLeft or ResizeHandlePosition.TopCenter or ResizeHandlePosition.TopRight
                    => item.Y - visualHandleSize / 2,
                ResizeHandlePosition.MiddleLeft or ResizeHandlePosition.MiddleRight
                    => item.Y + item.Height / 2 - visualHandleSize / 2,
                _ => item.Y + item.Height - visualHandleSize / 2
            };

            Canvas.SetLeft(handle, x);
            Canvas.SetTop(handle, y);
        }
    }

    private static Cursor GetResizeCursor(ResizeHandlePosition pos)
    {
        return pos switch
        {
            ResizeHandlePosition.TopLeft or ResizeHandlePosition.BottomRight => System.Windows.Input.Cursors.SizeNWSE,
            ResizeHandlePosition.TopRight or ResizeHandlePosition.BottomLeft => System.Windows.Input.Cursors.SizeNESW,
            ResizeHandlePosition.TopCenter or ResizeHandlePosition.BottomCenter => System.Windows.Input.Cursors.SizeNS,
            ResizeHandlePosition.MiddleLeft or ResizeHandlePosition.MiddleRight => System.Windows.Input.Cursors.SizeWE,
            _ => System.Windows.Input.Cursors.SizeAll
        };
    }

    private void ResizeHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Shapes.Rectangle handle) return;
        if (handle.Tag is not ResizeHandlePosition pos) return;

        e.Handled = true;

        // Предотвращаем запуск перетаскивания элемента
        if (_draggedElement != null)
        {
            _draggedElement.ReleaseMouseCapture();
            _draggedElement = null;
            _dragStartPositions.Clear();
        }

        _isResizing = true;
        _activeResizeHandle = pos;
        _resizeStartPoint = e.GetPosition(BoardCanvas);

        var item = _boardItems.FirstOrDefault(i => i.Id == _resizeItemId);
        if (item != null)
        {
            _resizeInitialBounds = new Rect(item.X, item.Y, item.Width, item.Height);
        }

        BoardCanvas.CaptureMouse();
    }

    private void UpdateResize(MouseEventArgs e)
    {
        if (!_isResizing || _activeResizeHandle == null) return;

        var item = _boardItems.FirstOrDefault(i => i.Id == _resizeItemId);
        if (item == null) return;

        var currentPos = e.GetPosition(BoardCanvas);
        var deltaX = currentPos.X - _resizeStartPoint.X;
        var deltaY = currentPos.Y - _resizeStartPoint.Y;

        var minSize = 30.0;
        var newX = _resizeInitialBounds.X;
        var newY = _resizeInitialBounds.Y;
        var newWidth = _resizeInitialBounds.Width;
        var newHeight = _resizeInitialBounds.Height;

        // Для изображений сохраняем пропорции (aspect ratio)
        bool isImage = item is ImageBoardItem;
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
                if (isImage)
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
                if (isImage)
                {
                    newHeight = newWidth / aspectRatio;
                    newY = _resizeInitialBounds.Y + (_resizeInitialBounds.Height - newHeight) / 2;
                }
                break;
            case ResizeHandlePosition.MiddleRight:
                newWidth = _resizeInitialBounds.Width + deltaX;
                if (isImage)
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
                if (isImage)
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

        // Для угловых ручек изображений: определяем dominant axis по наибольшему смещению
        if (isImage && (_activeResizeHandle == ResizeHandlePosition.TopLeft
            || _activeResizeHandle == ResizeHandlePosition.TopRight
            || _activeResizeHandle == ResizeHandlePosition.BottomLeft
            || _activeResizeHandle == ResizeHandlePosition.BottomRight))
        {
            // Используем ширину как ведущую ось, высоту подстраиваем по пропорциям
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
            if (isImage)
                newHeight = minSize / aspectRatio;
        }

        if (newHeight < minSize)
        {
            if (_activeResizeHandle == ResizeHandlePosition.TopLeft || _activeResizeHandle == ResizeHandlePosition.TopCenter || _activeResizeHandle == ResizeHandlePosition.TopRight)
                newY = _resizeInitialBounds.Bottom - minSize;
            newHeight = minSize;
            if (isImage)
                newWidth = minSize * aspectRatio;
        }

        // Применяем новые размеры
        item.X = newX;
        item.Y = newY;
        item.Width = newWidth;
        item.Height = newHeight;

        if (_elementMap.TryGetValue(item.Id, out var element))
        {
            Canvas.SetLeft(element, newX);
            Canvas.SetTop(element, newY);

            if (element is Border border)
            {
                border.Width = newWidth;
                border.Height = newHeight;
                // Изображение внутри Border растягивается автоматически (Stretch=UniformToFill)
            }
            else if (element is System.Windows.Controls.TextBox textBox)
            {
                textBox.Width = newWidth;
                textBox.Height = newHeight;
            }
        }

        UpdateResizeHandlesPosition(item);
    }

    private void EndResize()
    {
        if (_isResizing)
        {
            _isResizing = false;
            _activeResizeHandle = null;
            BoardCanvas.ReleaseMouseCapture();
            SaveBoard();
        }
    }

    #endregion
}

/// <summary>
/// Модель для сериализации/десериализации доски.
/// </summary>
public class BoardSaveData
{
    public List<BoardItem> Items { get; set; } = new();
    public double Zoom { get; set; } = 1.0;
    public double ScrollOffsetX { get; set; }
    public double ScrollOffsetY { get; set; }
}
