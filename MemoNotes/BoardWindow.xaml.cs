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
    private Border? _selectedImageBorder;

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
        // Клик по пустому пространству — снять выделение
        else if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
        {
            // Проверяем что клик не на элементах доски (TextBox, Image, Border)
            if (e.OriginalSource is not System.Windows.Controls.TextBox
                && e.OriginalSource is not System.Windows.Controls.Image
                && e.OriginalSource is not Border)
            {
                DeselectImageBorder();
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

        if (_draggedElement != null && e.LeftButton == MouseButtonState.Pressed)
        {
            var currentPos = e.GetPosition(BoardCanvas);
            var deltaX = currentPos.X - _dragStartPoint.X;
            var deltaY = currentPos.Y - _dragStartPoint.Y;

            var newX = _elementStartPos.X + deltaX;
            var newY = _elementStartPos.Y + deltaY;

            Canvas.SetLeft(_draggedElement, newX);
            Canvas.SetTop(_draggedElement, newY);

            if (_draggedElement.Tag is Guid itemId)
            {
                var item = _boardItems.FirstOrDefault(i => i.Id == itemId);
                if (item != null)
                {
                    item.X = newX;
                    item.Y = newY;
                }
            }
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);

        // Останавливаем перетаскивание элемента при отпускании ЛКМ
        if (_draggedElement != null && e.ChangedButton == MouseButton.Left)
        {
            _draggedElement.ReleaseMouseCapture();
            _draggedElement = null;
            SaveBoard();
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

            // Включаем режим редактирования
            textBox.IsReadOnly = false;
            textBox.IsReadOnlyCaretVisible = true;
            textBox.Cursor = System.Windows.Input.Cursors.IBeam;
            textBox.Focus();
            Keyboard.Focus(textBox);
            e.Handled = true;
            return;
        }

        // Если уже в режиме редактирования — не перехватываем одиночный клик
        if (!textBox.IsReadOnly) return;

        // Одиночный клик — перетаскивание
        e.Handled = true;
        StartDrag(textBox, e);
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
            Width = displayWidth,
            Height = displayHeight,
            Stretch = Stretch.Uniform
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

    private void ImageElement_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border) return;
        e.Handled = true;
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
        element.CaptureMouse();
    }

    #endregion

    #region Удаление элементов

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_draggedElement?.Tag is Guid id)
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
                Zoom = _currentZoom
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

            AddImageElement(image, item.ImageDataBase64, image.PixelWidth, image.PixelHeight, item.X, item.Y);
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

        // Delete — удалить последний добавленный элемент
        if (e.Key == Key.Delete && Keyboard.FocusedElement is not System.Windows.Controls.TextBox)
        {
            if (_boardItems.Count > 0)
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
}

/// <summary>
/// Модель для сериализации/десериализации доски.
/// </summary>
public class BoardSaveData
{
    public List<BoardItem> Items { get; set; } = new();
    public double Zoom { get; set; } = 1.0;
}
