using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using MemoNotes.Models;
using MemoNotes.Undo;
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

    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MemoNotes");
    private static readonly string BoardDataFilePath = Path.Combine(AppDataDir, "board.memo");
    private const double MinZoom = 0.1;
    private const double MaxZoom = 5.0;
    private const double ZoomStep = 0.1;

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
    private readonly Dictionary<Guid, Border> _selectionOverlays = new(); // Оверлейные бордеры для выделения, не влияют на layout

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

    // Режим рисования кистью
    private bool _isDrawingMode;
    private bool _isDrawing;
    private bool _hasContinuousStrokeStarted; // Есть ли незавершённый непрерывный штрих
    private System.Windows.Shapes.Path? _currentStrokePath;
    private System.Windows.Media.PathGeometry? _currentStrokeGeometry;
    private System.Windows.Media.PathFigure? _currentStrokeFigure;
    private readonly List<double> _currentStrokePoints = new();
    private readonly List<int> _currentStrokeFigureLengths = new(); // Количество точек (пар X,Y) в каждой фигуре
    private int _currentFigurePointCount; // Счётчик точек в текущей фигуре
    private Point _strokeMinPoint;
    private Point _strokeMaxPoint;
    private readonly Dictionary<Guid, (List<double> OriginalPoints, List<int> FigureLengths, double OrigWidth, double OrigHeight, System.Windows.Shapes.Path Path)> _strokeOriginalData = new();

    private readonly List<BoardItem> _boardItems = new();
    private readonly Dictionary<Guid, FrameworkElement> _elementMap = new();

    // Undo/Redo
    private readonly UndoManager _undoManager = new();
    private string? _editingTextBeforeChange;

    #endregion

    #region Конструктор

    public BoardWindow()
    {
        InitializeComponent();

        Width = Properties.Settings.Default.BoardWindowWidth;
        Height = Properties.Settings.Default.BoardHeight;
        Topmost = Properties.Settings.Default.TopmostTextBoxWindow;
        RefreshPinnedButtonState();

        // Миграция данных в AppData: создаём директорию и переносим старый файл если есть
        Directory.CreateDirectory(AppDataDir);
        var legacyPath = "board.memo";
        if (!File.Exists(BoardDataFilePath) && File.Exists(legacyPath))
        {
            try { File.Move(legacyPath, BoardDataFilePath); } catch { /* игнорируем */ }
        }

        LoadBoard();

        PreviewKeyDown += BoardWindow_PreviewKeyDown;
        PreviewKeyUp += BoardWindow_PreviewKeyUp;

        BoardCanvas.Width = 4000;
        BoardCanvas.Height = 4000;

        BoardScrollViewer.SizeChanged += (s, e) => UpdateCanvasSize();
        UpdateCanvasSize();

        // Если нет сохранённой позиции — скроллим в центр доски
        if (!File.Exists(BoardDataFilePath))
        {
            Dispatcher.BeginInvoke(() =>
            {
                BoardScrollViewer.ScrollToHorizontalOffset((BoardCanvas.Width * _currentZoom - BoardScrollViewer.ViewportWidth) / 2);
                BoardScrollViewer.ScrollToVerticalOffset((BoardCanvas.Height * _currentZoom - BoardScrollViewer.ViewportHeight) / 2);
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    #endregion

    #region Динамический размер Canvas

    private void UpdateCanvasSize()
    {
        if (BoardScrollViewer.ViewportWidth > 0 && BoardScrollViewer.ViewportHeight > 0 && _currentZoom > 0)
        {
            var minWidth = BoardScrollViewer.ViewportWidth / _currentZoom;
            var minHeight = BoardScrollViewer.ViewportHeight / _currentZoom;
            BoardCanvas.Width = Math.Max(4000, minWidth);
            BoardCanvas.Height = Math.Max(4000, minHeight);
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

        // Обновляем толщину всех штрихов
        UpdateStrokeThicknesses();

        e.Handled = true;
    }

    private void ResetZoom_Click(object sender, RoutedEventArgs e)
    {
        _currentZoom = 1.0;
        BoardScaleTransform.ScaleX = 1.0;
        BoardScaleTransform.ScaleY = 1.0;
        ZoomText.Text = "100%";
        UpdateStrokeThicknesses();
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
        // Режим рисования кистью — рисуем только по пустому пространству
        else if (_isDrawingMode && e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
        {
            // Если клик по элементу доски (Border, TextBox, Image) — не рисуем
            if (e.OriginalSource is Border or System.Windows.Controls.TextBox or System.Windows.Controls.Image)
            {
                // Выходим из режима рисования и даём событию обработаться стандартно
                ToggleDrawingMode();
            }
            else
            {
                StartDrawingStroke(e);
                e.Handled = true;
            }
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
                // Для всех Border-элементов (изображения и штрихи) — оверлейный бордер, не трогаем layout
                AddSelectionOverlay(border);
                _selectedImageBorder = border;
            }
            else if (border.Tag is Guid id)
            {
                // Убираем оверлей, не трогаем сам элемент
                RemoveSelectionOverlay(id);
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

        // Рисование кистью
        if (_isDrawing && e.LeftButton == MouseButtonState.Pressed)
        {
            ContinueDrawingStroke(e);
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

            // Обновляем позиции всех оверлейных бордеров выделения
            UpdateAllSelectionOverlayPositions();
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);

        // Завершаем рисование кистью
        if (_isDrawing && e.ChangedButton == MouseButton.Left)
        {
            EndDrawingStroke();
        }

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
            // Формируем команду перемещения (только если были реальные сдвиги)
            var movements = new Dictionary<Guid, (Point OldPos, Point NewPos)>();
            foreach (var kvp in _dragStartPositions)
            {
                var item = _boardItems.FirstOrDefault(i => i.Id == kvp.Key);
                if (item != null)
                {
                    var newPos = new Point(item.X, item.Y);
                    if (Math.Abs(kvp.Value.X - newPos.X) > 0.5 || Math.Abs(kvp.Value.Y - newPos.Y) > 0.5)
                    {
                        movements[kvp.Key] = (kvp.Value, newPos);
                    }
                }
            }

            _draggedElement.ReleaseMouseCapture();
            _draggedElement = null;
            _dragStartPositions.Clear();

            if (movements.Count > 0)
            {
                _undoManager.ExecuteCommand(new MoveItemsCommand(movements, SetItemPosition));
            }

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

    #region Undo/Redo вспомогательные методы

    /// <summary>Создать UI-элемент для текстового блока и добавить на доску.</summary>
    private void CreateTextElement(TextBoardItem item)
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

        Canvas.SetLeft(textBox, item.X);
        Canvas.SetTop(textBox, item.Y);
        BoardCanvas.Children.Add(textBox);

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

                    // Обновляем ресайз-ручки по фактическим размерам после layout pass
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (_resizeItemId == id && !double.IsNaN(tb.ActualWidth) && !double.IsNaN(tb.ActualHeight))
                        {
                            UpdateResizeHandlesPosition(tb, id);
                        }
                    }, System.Windows.Threading.DispatcherPriority.Loaded);
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

                // Фиксируем актуальные размеры в модель после автоподгонки
                if (tb.Tag is Guid id)
                {
                    var model = _boardItems.FirstOrDefault(i => i.Id == id) as TextBoardItem;
                    if (model != null && !double.IsNaN(tb.ActualWidth) && !double.IsNaN(tb.ActualHeight))
                    {
                        tb.Width = tb.ActualWidth;
                        tb.Height = tb.ActualHeight;
                        if (_resizeItemId == id)
                        {
                            UpdateResizeHandlesPosition(tb, id);
                        }
                    }
                }

                // При потере фокуса — фиксируем команду изменения текста
                if (tb.Tag is Guid textId && _editingTextBeforeChange != null)
                {
                    var currentItem = _boardItems.FirstOrDefault(i => i.Id == textId) as TextBoardItem;
                    if (currentItem != null && currentItem.Text != _editingTextBeforeChange)
                    {
                        _undoManager.ExecuteCommand(new EditTextCommand(
                            textId, _editingTextBeforeChange, currentItem.Text, SetItemText));
                    }
                    _editingTextBeforeChange = null;
                }

                SaveBoard();
            }
        };

        textBox.GotFocus += (s, args) =>
        {
            // Запоминаем текст до начала редактирования
            if (s is System.Windows.Controls.TextBox tb && tb.Tag is Guid id)
            {
                var model = _boardItems.FirstOrDefault(i => i.Id == id) as TextBoardItem;
                if (model != null)
                {
                    _editingTextBeforeChange = model.Text;
                }
            }
        };

        SaveBoard();
    }

    /// <summary>Создать UI-элемент для изображения и добавить на доску (для новых изображений).</summary>
    private void CreateImageElementForCommand(ImageBoardItem item, BitmapSource? imageSource)
    {
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
                using var stream = new MemoryStream(bytes);
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                source = bitmap;
            }
            catch
            {
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
            Cursor = System.Windows.Input.Cursors.Hand,
            SnapsToDevicePixels = true
        };

        var image = new System.Windows.Controls.Image
        {
            Source = source,
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch
        };

        border.Child = image;
        border.MouseLeftButtonDown += ImageElement_MouseLeftButtonDown;
        border.MouseRightButtonDown += ImageElement_MouseRightButtonDown;

        Canvas.SetLeft(border, item.X);
        Canvas.SetTop(border, item.Y);
        BoardCanvas.Children.Add(border);

        _boardItems.Add(item);
        _elementMap[item.Id] = border;

        SaveBoard();
    }

    /// <summary>Восстановить элемент на доску (для undo удаления).</summary>
    private void RestoreItem(BoardItem item)
    {
        switch (item)
        {
            case TextBoardItem textItem:
                CreateTextElement(textItem);
                break;
            case ImageBoardItem imageItem:
                CreateImageElementForCommand(imageItem, null);
                break;
            case StrokeBoardItem strokeItem:
                CreateStrokeElement(strokeItem);
                break;
        }
    }

    /// <summary>Удалить элемент с доски по Id (без undo).</summary>
    private void RemoveElementById(Guid id)
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

        _selectedItemIds.Remove(id);
        _strokeOriginalData.Remove(id);
        SaveBoard();
    }

    /// <summary>Установить позицию элемента (для undo/redo перемещения).</summary>
    private void SetItemPosition(Guid id, double x, double y)
    {
        var item = _boardItems.FirstOrDefault(i => i.Id == id);
        if (item == null) return;

        item.X = x;
        item.Y = y;

        if (_elementMap.TryGetValue(id, out var element))
        {
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
        }

        if (_selectedItemIds.Count == 1 && _resizeItemId == id)
        {
            UpdateResizeHandlesPosition(item);
        }
    }

    /// <summary>Установить границы элемента (для undo/redo изменения размера).</summary>
    private void SetItemBounds(Guid id, Rect bounds)
    {
        var item = _boardItems.FirstOrDefault(i => i.Id == id);
        if (item == null) return;

        item.X = bounds.X;
        item.Y = bounds.Y;
        item.Width = bounds.Width;
        item.Height = bounds.Height;

        if (_elementMap.TryGetValue(id, out var element))
        {
            Canvas.SetLeft(element, bounds.X);
            Canvas.SetTop(element, bounds.Y);

            if (element is Border border)
            {
                border.Width = bounds.Width;
                border.Height = bounds.Height;

                // Для штрихов — пересчитываем координаты точек Path
                if (_strokeOriginalData.TryGetValue(id, out var strokeInfo))
                {
                    var (origPoints, figLens, origW, origH, path) = strokeInfo;
                    UpdateStrokePathGeometry(path, origPoints, figLens, origW, origH, bounds.Width, bounds.Height);
                }
            }
            else if (element is System.Windows.Controls.TextBox textBox)
            {
                textBox.Width = bounds.Width;
                textBox.Height = bounds.Height;
            }
        }

        UpdateResizeHandlesPosition(item);
        SaveBoard();
    }

    /// <summary>Установить текст элемента (для undo/redo редактирования текста).</summary>
    private void SetItemText(Guid id, string text)
    {
        var item = _boardItems.FirstOrDefault(i => i.Id == id) as TextBoardItem;
        if (item == null) return;

        item.Text = text;

        if (_elementMap.TryGetValue(id, out var element) && element is System.Windows.Controls.TextBox textBox)
        {
            textBox.Text = text;
        }

        SaveBoard();
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

        var item = new TextBoardItem
        {
            X = x,
            Y = y,
            Width = 200,
            Height = 100,
            Text = text ?? string.Empty,
            FontSize = 16
        };

        _undoManager.ExecuteCommand(new AddItemCommand(
            item,
            addItem: i => CreateTextElement((TextBoardItem)i),
            removeItem: RemoveElementById
        ));
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

        var item = new ImageBoardItem
        {
            X = x,
            Y = y,
            Width = displayWidth + 8,
            Height = displayHeight + 8,
            ImageDataBase64 = base64
        };

        // Сохраняем imageSource для немедленного использования (чтобы не декодировать base64 дважды)
        var frozenSource = imageSource;

        _undoManager.ExecuteCommand(new AddItemCommand(
            item,
            addItem: i => CreateImageElementForCommand((ImageBoardItem)i, frozenSource),
            removeItem: RemoveElementById
        ));
    }



    private void ImageElement_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border) return;
        e.Handled = true;

        // Если это двойной клик — открываем изображение в приложении по умолчанию
        if (e.ClickCount == 2)
        {
            OpenImageInDefaultApp(border);
            return;
        }

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

    private void OpenImageInDefaultApp(Border border)
    {
        if (border.Tag is not Guid imageId) return;

        var imageItem = _boardItems.FirstOrDefault(i => i.Id == imageId) as ImageBoardItem;
        if (imageItem == null || string.IsNullOrEmpty(imageItem.ImageDataBase64)) return;

        try
        {
            var bytes = Convert.FromBase64String(imageItem.ImageDataBase64);
            var tempDir = Path.Combine(Path.GetTempPath(), "MemoNotes");
            Directory.CreateDirectory(tempDir);

            var fileName = string.IsNullOrEmpty(imageItem.OriginalName)
                ? $"image_{imageId:N}.png"
                : imageItem.OriginalName;

            var tempPath = Path.Combine(tempDir, fileName);
            File.WriteAllBytes(tempPath, bytes);

            Process.Start(new ProcessStartInfo
            {
                FileName = tempPath,
                UseShellExecute = true
            });
        }
        catch
        {
            // Игнорируем ошибки при открытии
        }
    }

    private void AddSelectionOverlay(Border border)
    {
        if (border.Tag is not Guid id) return;

        // Убираем существующий оверлей для этого элемента (если есть)
        RemoveSelectionOverlay(id);

        _selectedImageBorder = border;

        // Создаём оверлейный бордер поверх элемента — не влияет на layout
        var overlay = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            Background = System.Windows.Media.Brushes.Transparent,
            IsHitTestVisible = false
        };

        var left = Canvas.GetLeft(border);
        var top = Canvas.GetTop(border);
        Canvas.SetLeft(overlay, left);
        Canvas.SetTop(overlay, top);
        overlay.Width = border.ActualWidth > 0 ? border.ActualWidth : border.Width;
        overlay.Height = border.ActualHeight > 0 ? border.ActualHeight : border.Height;

        _selectionOverlays[id] = overlay;
        BoardCanvas.Children.Add(overlay);
    }

    private void RemoveSelectionOverlay(Guid id)
    {
        if (_selectionOverlays.TryGetValue(id, out var overlay))
        {
            BoardCanvas.Children.Remove(overlay);
            _selectionOverlays.Remove(id);
        }
    }

    private void SelectImageBorder(Border border)
    {
        AddSelectionOverlay(border);
    }

    private void DeselectImageBorder()
    {
        // Убираем все оверлейные бордеры
        foreach (var overlay in _selectionOverlays.Values.ToList())
        {
            BoardCanvas.Children.Remove(overlay);
        }
        _selectionOverlays.Clear();
        _selectedImageBorder = null;
    }

    /// <summary>Обновить позицию и размер оверлейного бордера выделения (при перетаскивании/ресайзе).</summary>
    private void UpdateSelectionOverlayPosition(FrameworkElement element)
    {
        if (element.Tag is not Guid id) return;
        if (!_selectionOverlays.TryGetValue(id, out var overlay)) return;

        var left = Canvas.GetLeft(element);
        var top = Canvas.GetTop(element);
        Canvas.SetLeft(overlay, left);
        Canvas.SetTop(overlay, top);
        overlay.Width = element.ActualWidth > 0 ? element.ActualWidth : element.Width;
        overlay.Height = element.ActualHeight > 0 ? element.ActualHeight : element.Height;
    }

    /// <summary>Обновить позиции всех оверлейных бордеров (при групповом перетаскивании).</summary>
    private void UpdateAllSelectionOverlayPositions()
    {
        foreach (var kvp in _selectionOverlays)
        {
            if (_elementMap.TryGetValue(kvp.Key, out var element))
            {
                var overlay = kvp.Value;
                var left = Canvas.GetLeft(element);
                var top = Canvas.GetTop(element);
                Canvas.SetLeft(overlay, left);
                Canvas.SetTop(overlay, top);
                overlay.Width = element.ActualWidth > 0 ? element.ActualWidth : element.Width;
                overlay.Height = element.ActualHeight > 0 ? element.ActualHeight : element.Height;
            }
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
            DeleteSelectedItems();
        }
        else if (_draggedElement?.Tag is Guid id)
        {
            DeleteItemsWithUndo(new List<Guid> { id });
        }
    }

    private void DeleteBoardItem(Guid id)
    {
        DeleteItemsWithUndo(new List<Guid> { id });
    }

    private void DeleteSelectedItems()
    {
        var idsToDelete = _selectedItemIds.ToList();
        if (idsToDelete.Count == 0) return;
        DeleteItemsWithUndo(idsToDelete);
        _selectedItemIds.Clear();
        HideResizeHandles();
    }

    private void DeleteItemsWithUndo(List<Guid> ids)
    {
        var itemsToDelete = _boardItems.Where(i => ids.Contains(i.Id)).ToList();
        if (itemsToDelete.Count == 0) return;

        // Очищаем выделение перед удалением
        foreach (var id in ids)
        {
            if (_elementMap.TryGetValue(id, out var element))
            {
                HighlightElement(element, false);
            }
        }
        _selectedItemIds.ExceptWith(ids);
        HideResizeHandles();

        _undoManager.ExecuteCommand(new DeleteItemsCommand(
            itemsToDelete,
            restoreItem: RestoreItem,
            executeDelete: () =>
            {
                foreach (var item in itemsToDelete)
                {
                    _boardItems.Remove(item);
                    if (_elementMap.TryGetValue(item.Id, out var element))
                    {
                        BoardCanvas.Children.Remove(element);
                        _elementMap.Remove(item.Id);
                    }
                }
                SaveBoard();
            }
        ));
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
                    case StrokeBoardItem strokeItem:
                        RestoreStrokeItem(strokeItem);
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

        // При загрузке доски очищаем историю undo/redo
        _undoManager.Clear();
    }

    private void RestoreTextItem(TextBoardItem item)
    {
        CreateTextElement(item);
    }

    private void RestoreImageItem(ImageBoardItem item)
    {
        try
        {
            CreateImageElementForCommand(item, null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Ошибка восстановления изображения: {ex.Message}");
        }
    }

    private void RestoreStrokeItem(StrokeBoardItem item)
    {
        try
        {
            CreateStrokeElement(item);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Ошибка восстановления штриха: {ex.Message}");
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
            // Не начинаем перетаскивание, если кликнули по кнопке тулбара
            if (e.OriginalSource is FrameworkElement fe && fe is not TextBlock)
            {
                // Поднимаемся по визуальному дереву — если родитель кнопка, не DragMove
                var parent = fe;
                while (parent != null)
                {
                    if (parent is System.Windows.Controls.Button)
                        return;
                    parent = VisualTreeHelper.GetParent(parent) as FrameworkElement;
                }
            }

            DragMove();
        }
    }

    private Rect _normalBounds;
    private bool _isMaximized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isMaximized)
        {
            // Восстанавливаем обычный размер
            Left = _normalBounds.X;
            Top = _normalBounds.Y;
            Width = _normalBounds.Width;
            Height = _normalBounds.Height;
            MaximizeButton.Content = new FontAwesome.WPF.ImageAwesome { Icon = FontAwesome.WPF.FontAwesomeIcon.Expand, Width = 14, Height = 14, Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)) };
            MaximizeButton.ToolTip = "Развернуть во весь экран";
            _isMaximized = false;
        }
        else
        {
            // Сохраняем текущие размеры
            _normalBounds = new Rect(Left, Top, Width, Height);
            // Разворачиваем на весь экран
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
            MaximizeButton.Content = new FontAwesome.WPF.ImageAwesome { Icon = FontAwesome.WPF.FontAwesomeIcon.Compress, Width = 14, Height = 14, Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)) };
            MaximizeButton.ToolTip = "Восстановить размер";
            _isMaximized = true;
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
        var icon = PinnedButton.Content as FontAwesome.WPF.ImageAwesome;
        if (icon != null)
        {
            icon.Foreground = Topmost
                ? new SolidColorBrush(Color.FromRgb(255, 215, 0))   // Золотой при закреплении
                : new SolidColorBrush(Color.FromRgb(204, 204, 204)); // Стандартный серый
        }
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
        Properties.Settings.Default.TopmostTextBoxWindow = Topmost;
        Properties.Settings.Default.Save();
        base.OnClosing(e);
    }

    private void BoardWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        // Ctrl+Z — Отмена (только если не в режиме редактирования TextBox)
        if (e.Key == Key.Z && isCtrl && !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
        {
            var focusedElement = Keyboard.FocusedElement;
            // Если TextBox в режиме редактирования — не перехватываем (пусть работает стандартный undo текста)
            if (focusedElement is System.Windows.Controls.TextBox tb && !tb.IsReadOnly)
            {
                return;
            }

            if (_undoManager.CanUndo)
            {
                _undoManager.Undo();
                e.Handled = true;
            }
            return;
        }

        // Ctrl+Shift+Z или Ctrl+Y — Повтор
        if ((e.Key == Key.Z && isCtrl && (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)))
            || (e.Key == Key.Y && isCtrl))
        {
            var focusedElement = Keyboard.FocusedElement;
            if (focusedElement is System.Windows.Controls.TextBox tb && !tb.IsReadOnly)
            {
                return;
            }

            if (_undoManager.CanRedo)
            {
                _undoManager.Redo();
                e.Handled = true;
            }
            return;
        }

        // Ctrl+V — вставка изображения
        if (e.Key == Key.V && isCtrl)
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

        // B — переключить режим кисти
        if (e.Key == Key.B && !Keyboard.IsKeyDown(Key.LeftCtrl) && !Keyboard.IsKeyDown(Key.RightCtrl))
        {
            if (Keyboard.FocusedElement is not System.Windows.Controls.TextBox)
            {
                ToggleDrawingMode();
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
                DeleteSelectedItems();
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

        // Escape — снять фокус с элемента / выйти из режима рисования
        if (e.Key == Key.Escape)
        {
            if (_isDrawingMode)
            {
                ToggleDrawingMode();
            }
            Keyboard.Focus(this);
        }
    }

    #endregion

    #region Рисование кистью

    private void BrushButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleDrawingMode();
    }

    private void ToggleDrawingMode()
    {
        _isDrawingMode = !_isDrawingMode;

        if (_isDrawingMode)
        {
            BrushButton.Style = (Style)FindResource("ToolbarButtonActiveStyle");
            BoardCanvas.Cursor = System.Windows.Input.Cursors.Cross;
            ClearSelection();
        }
        else
        {
            // При выключении режима — финализируем незавершённый непрерывный штрих
            if (_hasContinuousStrokeStarted)
            {
                FinalizeCurrentStroke();
            }

            _isDrawing = false;
            BrushButton.Style = (Style)FindResource("ToolbarButtonStyle");
            BoardCanvas.Cursor = System.Windows.Input.Cursors.Arrow;
        }
    }

    /// <summary>При отпускании клавиши непрерывного рисования или модификатора — финализируем текущий штрих.</summary>
    private void BoardWindow_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_hasContinuousStrokeStarted) return;

        int vkCode = Properties.Settings.Default.ContinuousStrokeKey;
        if (vkCode == 0) return;

        int modifier = Properties.Settings.Default.ContinuousStrokeModifier;

        // Определяем, является ли отпущенная клавиша частью настроенного сочетания
        bool isPartOfCombo = false;

        if (e.Key == System.Windows.Input.KeyInterop.KeyFromVirtualKey(vkCode))
            isPartOfCombo = true;

        if ((modifier & 1) != 0 && (e.Key == System.Windows.Input.Key.LeftShift || e.Key == System.Windows.Input.Key.RightShift))
            isPartOfCombo = true;
        if ((modifier & 2) != 0 && (e.Key == System.Windows.Input.Key.LeftCtrl || e.Key == System.Windows.Input.Key.RightCtrl))
            isPartOfCombo = true;
        if ((modifier & 4) != 0 && (e.Key == System.Windows.Input.Key.LeftAlt || e.Key == System.Windows.Input.Key.RightAlt))
            isPartOfCombo = true;

        // После отпускания этой клавиши сочетание больше не активно — финализируем штрих
        if (isPartOfCombo && !IsContinuousStrokeKeyDown())
        {
            if (_isDrawing)
            {
                _isDrawing = false;
                BoardCanvas.ReleaseMouseCapture();
            }
            FinalizeCurrentStroke();
        }
    }

    /// <summary>Проверяет, зажато ли настроенное сочетание клавиш непрерывного рисования.</summary>
    private bool IsContinuousStrokeKeyDown()
    {
        int vkCode = Properties.Settings.Default.ContinuousStrokeKey;
        if (vkCode == 0) return false;

        int modifier = Properties.Settings.Default.ContinuousStrokeModifier;

        // Проверяем модификаторы
        bool shiftOk = (modifier & 1) == 0 || Keyboard.IsKeyDown(System.Windows.Input.Key.LeftShift) || Keyboard.IsKeyDown(System.Windows.Input.Key.RightShift);
        bool ctrlOk = (modifier & 2) == 0 || Keyboard.IsKeyDown(System.Windows.Input.Key.LeftCtrl) || Keyboard.IsKeyDown(System.Windows.Input.Key.RightCtrl);
        bool altOk = (modifier & 4) == 0 || Keyboard.IsKeyDown(System.Windows.Input.Key.LeftAlt) || Keyboard.IsKeyDown(System.Windows.Input.Key.RightAlt);

        if (!shiftOk || !ctrlOk || !altOk) return false;

        // Проверяем основную клавишу
        var key = System.Windows.Input.KeyInterop.KeyFromVirtualKey(vkCode);
        return Keyboard.IsKeyDown(key);
    }

    private void StartDrawingStroke(MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(BoardCanvas);
        var isContinuous = IsContinuousStrokeKeyDown();

        _isDrawing = true;

        if (!isContinuous || !_hasContinuousStrokeStarted)
        {
            // Начинаем новый штрих — очищаем предыдущие данные
            _currentStrokePoints.Clear();
            _currentStrokeFigureLengths.Clear();
            _strokeMinPoint = pos;
            _strokeMaxPoint = pos;
            _currentFigurePointCount = 0;

            _currentStrokeFigure = new System.Windows.Media.PathFigure
            {
                StartPoint = pos,
                IsClosed = false
            };

            _currentStrokeGeometry = new System.Windows.Media.PathGeometry();
            _currentStrokeGeometry.Figures.Add(_currentStrokeFigure);

            _currentStrokePath = new System.Windows.Shapes.Path
            {
                Stroke = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                StrokeThickness = 3 / _currentZoom,
                StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
                Data = _currentStrokeGeometry,
                IsHitTestVisible = false
            };

            BoardCanvas.Children.Add(_currentStrokePath);
            _hasContinuousStrokeStarted = true;
        }
        else
        {
            // Продолжаем непрерывный штрих — сохраняем длину предыдущей фигуры и начинаем новую
            _currentStrokeFigureLengths.Add(_currentFigurePointCount);
            _currentFigurePointCount = 0;

            _currentStrokeFigure = new System.Windows.Media.PathFigure
            {
                StartPoint = pos,
                IsClosed = false
            };

            if (_currentStrokeGeometry != null)
            {
                _currentStrokeGeometry.Figures.Add(_currentStrokeFigure);
            }
        }

        _currentStrokePoints.Add(pos.X);
        _currentStrokePoints.Add(pos.Y);
        _currentFigurePointCount++;

        BoardCanvas.CaptureMouse();
    }

    private void ContinueDrawingStroke(MouseEventArgs e)
    {
        if (_currentStrokePath == null || _currentStrokeFigure == null) return;

        var pos = e.GetPosition(BoardCanvas);

        _currentStrokePoints.Add(pos.X);
        _currentStrokePoints.Add(pos.Y);
        _currentFigurePointCount++;

        // Обновляем границы
        _strokeMinPoint.X = Math.Min(_strokeMinPoint.X, pos.X);
        _strokeMinPoint.Y = Math.Min(_strokeMinPoint.Y, pos.Y);
        _strokeMaxPoint.X = Math.Max(_strokeMaxPoint.X, pos.X);
        _strokeMaxPoint.Y = Math.Max(_strokeMaxPoint.Y, pos.Y);

        // Добавляем отрезок линии
        var segment = new System.Windows.Media.LineSegment(pos, true) { IsStroked = true };
        _currentStrokeFigure.Segments.Add(segment);
    }

    private void EndDrawingStroke()
    {
        if (_currentStrokePath == null) return;

        _isDrawing = false;
        BoardCanvas.ReleaseMouseCapture();

        var isContinuous = IsContinuousStrokeKeyDown();

        if (!isContinuous)
        {
            // Обычный режим — финализируем сразу
            FinalizeCurrentStroke();
        }
        // В непрерывном режиме — штрих остаётся на доске, ждём следующего нажатия ЛКМ
    }

    /// <summary>
    /// Финализирует текущий штрих: удаляет временный Path с доски и создаёт элемент BoardItem.
    /// </summary>
    private void FinalizeCurrentStroke()
    {
        if (_currentStrokePath == null) return;

        _hasContinuousStrokeStarted = false;

        // Удаляем временный Path с доски
        BoardCanvas.Children.Remove(_currentStrokePath);

        // Если было слишком мало точек — не создаём элемент (одиночный клик)
        if (_currentStrokePoints.Count < 4)
        {
            _currentStrokePath = null;
            _currentStrokeGeometry = null;
            _currentStrokeFigure = null;
            _currentStrokePoints.Clear();
            _currentStrokeFigureLengths.Clear();
            return;
        }

        var padding = 10.0; // Отступ внутри Border для удобного выделения
        var strokeThickness = 3.0;

        // Вычисляем bounding box
        var minX = _strokeMinPoint.X - padding;
        var minY = _strokeMinPoint.Y - padding;
        var maxX = _strokeMaxPoint.X + padding;
        var maxY = _strokeMaxPoint.Y + padding;
        var width = Math.Max(maxX - minX, 10);
        var height = Math.Max(maxY - minY, 10);

        // Переводим точки в локальные координаты (относительно minX/minY)
        var localPoints = new List<double>();
        for (int i = 0; i < _currentStrokePoints.Count; i += 2)
        {
            localPoints.Add(_currentStrokePoints[i] - minX);
            localPoints.Add(_currentStrokePoints[i + 1] - minY);
        }

        // Формируем FigureLengths: длины всех фигур, включая последнюю
        var figureLengths = new List<int>(_currentStrokeFigureLengths);
        figureLengths.Add(_currentFigurePointCount);

        // Создаём модель штриха
        var strokeItem = new StrokeBoardItem
        {
            X = minX,
            Y = minY,
            Width = width,
            Height = height,
            OriginalWidth = width,
            OriginalHeight = height,
            Points = localPoints,
            FigureLengths = figureLengths,
            ColorHex = "#FFFFFFFF",
            StrokeThickness = strokeThickness
        };

        // Добавляем команду undo (Execute() создаст элемент на доске)
        _undoManager.ExecuteCommand(new AddItemCommand(
            strokeItem,
            addItem: i => CreateStrokeElement((StrokeBoardItem)i),
            removeItem: RemoveElementById
        ));

        _currentStrokePath = null;
        _currentStrokeGeometry = null;
        _currentStrokeFigure = null;
        _currentStrokePoints.Clear();
        _currentStrokeFigureLengths.Clear();
    }

    /// <summary>Создать UI-элемент (Border+Path) для штриха и добавить на доску.</summary>
    private void CreateStrokeElement(StrokeBoardItem item)
    {
        var border = BuildStrokeBorder(item);

        Canvas.SetLeft(border, item.X);
        Canvas.SetTop(border, item.Y);

        // Если размеры были изменены (например после загрузки) — пересчитываем геометрию
        if (_strokeOriginalData.TryGetValue(item.Id, out var data))
        {
            var (origPoints, figLens, origW, origH, path) = data;
            if (origW > 0 && origH > 0 && (Math.Abs(origW - item.Width) > 0.1 || Math.Abs(origH - item.Height) > 0.1))
            {
                UpdateStrokePathGeometry(path, origPoints, figLens, origW, origH, item.Width, item.Height);
            }
        }

        BoardCanvas.Children.Add(border);
        _boardItems.Add(item);
        _elementMap[item.Id] = border;

        SaveBoard();
    }

    /// <summary>Построить Border с Path внутри по модели StrokeBoardItem.</summary>
    private Border BuildStrokeBorder(StrokeBoardItem item)
    {
        // Path с локальными координатами
        var path = BuildStrokePath(item);

        // Обёртка Border (как у изображений)
        var border = new Border
        {
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(2),
            Width = item.Width,
            Height = item.Height,
            Tag = item.Id,
            Cursor = System.Windows.Input.Cursors.SizeAll
        };

        border.Child = path;

        // Обработка клика и перетаскивания (как у изображений)
        border.MouseLeftButtonDown += StrokeElement_MouseLeftButtonDown;
        border.MouseRightButtonDown += StrokeElement_MouseRightButtonDown;

        // Сохраняем оригинальные размеры и Path для пересчёта при ресайзе
        var origW = item.OriginalWidth > 0 ? item.OriginalWidth : item.Width;
        var origH = item.OriginalHeight > 0 ? item.OriginalHeight : item.Height;
        _strokeOriginalData[item.Id] = (item.Points.ToList(), item.FigureLengths.ToList(), origW, origH, path);

        // Обновляем толщину линии при изменении зума
        _zoomChangedHandlers.Add((_, newZoom) =>
        {
            path.StrokeThickness = item.StrokeThickness / newZoom;
        });

        return border;
    }

    private void StrokeElement_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border) return;
        e.Handled = true;

        // Деактивируем редактирование текстовых полей и убираем фокус
        DeactivateAllTextBoxesAndClearFocus();

        // Если кликнутый элемент уже в выделении — перетаскиваем всю группу
        var clickedInSelection = border.Tag is Guid clickedId && _selectedItemIds.Contains(clickedId);
        if (!clickedInSelection)
        {
            // Снимаем подсветку со всех элементов и очищаем выделение
            _selectedItemIds.Clear();
            foreach (var kvp in _elementMap)
            {
                HighlightElement(kvp.Value, false);
            }
            // Выделяем только кликнутый штрих
            if (border.Tag is Guid id)
            {
                _selectedItemIds.Add(id);
                ShowResizeHandles(id);
            }
        }

        BringToFront(border);
        SelectImageBorder(border);
        StartDrag(border, e);
    }

    private void StrokeElement_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
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

    /// <summary>Построить WPF Path по модели StrokeBoardItem (локальные координаты).</summary>
    private System.Windows.Shapes.Path BuildStrokePath(StrokeBoardItem item)
    {
        var geometry = new System.Windows.Media.PathGeometry();

        if (item.FigureLengths.Count > 0)
        {
            // Многофигурный штрих (непрерывное рисование)
            int pointIndex = 0;
            foreach (var figLen in item.FigureLengths)
            {
                if (figLen < 1 || pointIndex + 1 >= item.Points.Count) continue;

                var figure = new System.Windows.Media.PathFigure
                {
                    StartPoint = new Point(item.Points[pointIndex], item.Points[pointIndex + 1]),
                    IsClosed = false
                };
                pointIndex += 2;

                var segments = new System.Windows.Media.PathSegmentCollection();
                for (int j = 1; j < figLen && pointIndex + 1 < item.Points.Count; j++)
                {
                    segments.Add(new System.Windows.Media.LineSegment(
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
            var figure = new System.Windows.Media.PathFigure();
            var segments = new System.Windows.Media.PathSegmentCollection();

            if (item.Points.Count >= 2)
            {
                figure.StartPoint = new Point(item.Points[0], item.Points[1]);

                for (int i = 2; i < item.Points.Count - 1; i += 2)
                {
                    segments.Add(new System.Windows.Media.LineSegment(
                        new Point(item.Points[i], item.Points[i + 1]), true));
                }
            }

            figure.Segments = segments;
            figure.IsClosed = false;
            geometry.Figures.Add(figure);
        }

        var path = new System.Windows.Shapes.Path
        {
            Stroke = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(item.ColorHex)),
            StrokeThickness = item.StrokeThickness / _currentZoom,
            StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
            Data = geometry,
            IsHitTestVisible = false
        };

        return path;
    }

    private readonly List<Action<double, double>> _zoomChangedHandlers = new();

    /// <summary>Обновить толщину всех штрихов при изменении зума.</summary>
    private void UpdateStrokeThicknesses()
    {
        foreach (var handler in _zoomChangedHandlers)
        {
            handler(0, _currentZoom);
        }
    }

    /// <summary>Пересчитать координаты точек Path при изменении размера Border-а.</summary>
    private void UpdateStrokePathGeometry(System.Windows.Shapes.Path path, List<double> origPoints,
        List<int> figureLengths, double origWidth, double origHeight, double newWidth, double newHeight)
    {
        if (origWidth <= 0 || origHeight <= 0 || newWidth <= 0 || newHeight <= 0) return;

        var scaleX = newWidth / origWidth;
        var scaleY = newHeight / origHeight;

        var geometry = new System.Windows.Media.PathGeometry();

        if (figureLengths.Count > 0)
        {
            // Многофигурный штрих
            int pointIndex = 0;
            foreach (var figLen in figureLengths)
            {
                if (figLen < 1 || pointIndex + 1 >= origPoints.Count) continue;

                var figure = new System.Windows.Media.PathFigure
                {
                    StartPoint = new Point(origPoints[pointIndex] * scaleX, origPoints[pointIndex + 1] * scaleY),
                    IsClosed = false
                };
                pointIndex += 2;

                var segments = new System.Windows.Media.PathSegmentCollection();
                for (int j = 1; j < figLen && pointIndex + 1 < origPoints.Count; j++)
                {
                    segments.Add(new System.Windows.Media.LineSegment(
                        new Point(origPoints[pointIndex] * scaleX, origPoints[pointIndex + 1] * scaleY), true));
                    pointIndex += 2;
                }
                figure.Segments = segments;
                geometry.Figures.Add(figure);
            }
        }
        else
        {
            // Обычный однофигурный штрих (обратная совместимость)
            var figure = new System.Windows.Media.PathFigure();
            var segments = new System.Windows.Media.PathSegmentCollection();

            if (origPoints.Count >= 2)
            {
                figure.StartPoint = new Point(origPoints[0] * scaleX, origPoints[1] * scaleY);

                for (int i = 2; i < origPoints.Count - 1; i += 2)
                {
                    segments.Add(new System.Windows.Media.LineSegment(
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
        // Если есть UI-элемент — используем его фактические размеры и позицию
        if (_elementMap.TryGetValue(item.Id, out var element) && element != null)
        {
            UpdateResizeHandlesPosition(element, item.Id);
            return;
        }

        var visualHandleSize = HandleSize / _currentZoom;
        var elementWidth = item.Width;
        var elementHeight = item.Height;

        foreach (var handle in _resizeHandles)
        {
            if (handle.Tag is not ResizeHandlePosition pos) continue;

            handle.Width = visualHandleSize;
            handle.Height = visualHandleSize;
            handle.StrokeThickness = 1 / _currentZoom;

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

    /// <summary>Обновить позиции ресайз-ручек на основе фактического размера UI-элемента.</summary>
    private void UpdateResizeHandlesPosition(FrameworkElement element, Guid id)
    {
        var visualHandleSize = HandleSize / _currentZoom;
        var elementX = Canvas.GetLeft(element);
        var elementY = Canvas.GetTop(element);
        var elementWidth = element.ActualWidth;
        var elementHeight = element.ActualHeight;

        // Обновляем размеры в модели
        var model = _boardItems.FirstOrDefault(i => i.Id == id);
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
            handle.StrokeThickness = 1 / _currentZoom;

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

        // Для изображений и штрихов сохраняем пропорции (aspect ratio)
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

        // Для угловых ручек изображений и штрихов: определяем dominant axis по наибольшему смещению
        if ((isImage || isStroke) && (_activeResizeHandle == ResizeHandlePosition.TopLeft
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

        if (_elementMap.TryGetValue(item.Id, out var element))
        {
            Canvas.SetLeft(element, newX);
            Canvas.SetTop(element, newY);

            if (element is Border border)
            {
                border.Width = newWidth;
                border.Height = newHeight;
                // Изображение внутри Border растягивается автоматически (Stretch=UniformToFill)
                // Для штрихов — пересчитываем координаты точек Path в реальном времени
                if (_strokeOriginalData.TryGetValue(item.Id, out var strokeInfo))
                {
                    var (origPoints, figLens, origW, origH, path) = strokeInfo;
                    UpdateStrokePathGeometry(path, origPoints, figLens, origW, origH, newWidth, newHeight);
                }
            }
            else if (element is System.Windows.Controls.TextBox textBox)
            {
                textBox.Width = newWidth;
                // Для текстовых полей автоматически подгоняем высоту под текст при заданной ширине
                textBox.Height = double.NaN;
                textBox.Measure(new System.Windows.Size(newWidth, double.PositiveInfinity));
                var desiredHeight = textBox.DesiredSize.Height;
                if (desiredHeight > newHeight || double.IsNaN(newHeight))
                {
                    // Если высота увеличилась — фиксируем нижний край (сдвигаем Y вверх)
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

        // Обновляем высоту в модели после авторазмера текста
        item.Height = newHeight;

        UpdateResizeHandlesPosition(item);

        // Обновляем позицию оверлейного бордера выделения
        if (_elementMap.TryGetValue(item.Id, out var elem) && elem == _selectedImageBorder)
        {
            UpdateSelectionOverlayPosition(elem);
        }
    }

    private void EndResize()
    {
        if (_isResizing)
        {
            // Формируем команду изменения размера
            var item = _boardItems.FirstOrDefault(i => i.Id == _resizeItemId);
            if (item != null)
            {
                var newBounds = new Rect(item.X, item.Y, item.Width, item.Height);
                if (Math.Abs(_resizeInitialBounds.X - newBounds.X) > 0.5 ||
                    Math.Abs(_resizeInitialBounds.Y - newBounds.Y) > 0.5 ||
                    Math.Abs(_resizeInitialBounds.Width - newBounds.Width) > 0.5 ||
                    Math.Abs(_resizeInitialBounds.Height - newBounds.Height) > 0.5)
                {
                    _undoManager.ExecuteCommand(new ResizeItemCommand(
                        _resizeItemId, _resizeInitialBounds, newBounds, SetItemBounds));
                }
            }

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
