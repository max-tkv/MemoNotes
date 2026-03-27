using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MemoNotes.Models;
using MemoNotes.Service.Logging;
using MemoNotes.Undo;
using Brushes = System.Windows.Media.Brushes;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using Image = System.Windows.Controls.Image;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Rectangle = System.Windows.Shapes.Rectangle;
using TextBox = System.Windows.Controls.TextBox;

namespace MemoNotes.Board;

/// <summary>
/// Обработчик взаимодействия с доской: панорамирование, перетаскивание, выделение, зум,
/// добавление элементов, удаление, контекстные меню, горячие клавиши.
/// </summary>
public class BoardInteractionHandler
{
    private readonly Canvas _canvas;
    private readonly ScrollViewer _scrollViewer;
    private readonly ScaleTransform _scaleTransform;
    private readonly BoardState _state;
    private readonly BoardElementFactory _factory;
    private readonly ResizeHandleManager _resizeManager;
    private readonly DrawingModeHandler _drawingHandler;
    private readonly BoardPersistence _persistence;

    public BoardInteractionHandler(
        Canvas canvas, ScrollViewer scrollViewer, ScaleTransform scaleTransform,
        BoardState state, BoardElementFactory factory, ResizeHandleManager resizeManager,
        DrawingModeHandler drawingHandler, BoardPersistence persistence)
    {
        _canvas = canvas;
        _scrollViewer = scrollViewer;
        _scaleTransform = scaleTransform;
        _state = state;
        _factory = factory;
        _resizeManager = resizeManager;
        _drawingHandler = drawingHandler;
        _persistence = persistence;

        // При ресайзе — обновляем оверлей выделения
        _resizeManager.OnResizeUpdated = UpdateCurrentResizeSelectionOverlay;
    }

    #region Зум

    /// <summary>Обработать прокрутку колёсика мыши (зум).</summary>
    public void HandleMouseWheel(MouseWheelEventArgs e)
    {
        var mousePos = e.GetPosition(_canvas);

        var zoomFactor = e.Delta > 0 ? BoardState.ZoomStep : -BoardState.ZoomStep;
        var newZoom = _state.CurrentZoom + zoomFactor;

        newZoom = Math.Clamp(newZoom, BoardState.MinZoom, BoardState.MaxZoom);
        if (Math.Abs(newZoom - _state.CurrentZoom) < 0.001) return;

        _state.CurrentZoom = newZoom;
        _scaleTransform.ScaleX = _state.CurrentZoom;
        _scaleTransform.ScaleY = _state.CurrentZoom;
        Logger.Debug<BoardInteractionHandler>($"Зум: {_state.CurrentZoom:F2} (delta={e.Delta})");

        var scrollOffsetX = _scrollViewer.HorizontalOffset;
        var scrollOffsetY = _scrollViewer.VerticalOffset;

        var relativeX = mousePos.X * _state.CurrentZoom;
        var relativeY = mousePos.Y * _state.CurrentZoom;

        _scrollViewer.ScrollToHorizontalOffset(scrollOffsetX + relativeX * zoomFactor / _state.CurrentZoom);
        _scrollViewer.ScrollToVerticalOffset(scrollOffsetY + relativeY * zoomFactor / _state.CurrentZoom);

        if (_resizeManager.ResizeItemId != Guid.Empty)
        {
            var resizeItem = _state.GetItemById(_resizeManager.ResizeItemId);
            if (resizeItem != null)
                _resizeManager.UpdateResizeHandlesPosition(resizeItem);
        }

        _factory.UpdateStrokeThicknesses();

        e.Handled = true;
    }

    #endregion

    #region Панорамирование

    /// <summary>Обработать нажатие кнопки мыши на Canvas.</summary>
    public void HandleMouseDown(MouseButtonEventArgs e)
    {
        var isCtrlPressed = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);

        Logger.Debug<BoardInteractionHandler>($"MouseDown: button={e.ChangedButton}, ctrl={isCtrlPressed}, drawingMode={_drawingHandler.IsDrawingMode}, source={e.OriginalSource.GetType().Name}");

        // Панорамирование средней кнопкой мыши (всегда, даже в режиме рисования)
        if (e.MiddleButton == MouseButtonState.Pressed)
        {
            ClearSelection();
            StartPan(e);
            e.Handled = true;
            return;
        }
        // Панорамирование Ctrl + Правая кнопка мыши (всегда)
        if (e.ChangedButton == MouseButton.Right && e.ButtonState == MouseButtonState.Pressed && isCtrlPressed)
        {
            ClearSelection();
            StartPan(e);
            e.Handled = true;
            return;
        }
        // Панорамирование Ctrl + Левая кнопка мыши — только если НЕ в режиме рисования
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed && isCtrlPressed
            && !_drawingHandler.IsDrawingMode)
        {
            ClearSelection();
            StartPan(e);
            e.Handled = true;
            return;
        }
        // Режим рисования кистью — рисуем только по пустому пространству
        if (_drawingHandler.TryStartDrawingOnEmptySpace(e))
        {
            e.Handled = true;
            return;
        }
        // Панорамирование Ctrl + Левая кнопка мыши — в режиме рисования: если не нарисовали, панорамируем
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed && isCtrlPressed)
        {
            ClearSelection();
            StartPan(e);
            e.Handled = true;
            return;
        }
        // Клик по пустому пространству — начать выделение рамкой
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
        {
            if (e.OriginalSource is not TextBox and not Image and not Border)
            {
                ClearSelection();
                StartSelection(e);
                e.Handled = true;
            }
        }
    }

    private void StartPan(MouseButtonEventArgs e)
    {
        _state.IsPanning = true;
        _state.PanStartPoint = e.GetPosition(_scrollViewer);
        _state.ScrollStartOffset = new Point(
            _scrollViewer.HorizontalOffset,
            _scrollViewer.VerticalOffset);
        _canvas.CaptureMouse();
    }

    private void StopPan()
    {
        if (_state.IsPanning)
        {
            _state.IsPanning = false;
            _canvas.ReleaseMouseCapture();
        }
    }

    public void HandleRightButtonUp(MouseButtonEventArgs e)
    {
        StopPan();
        e.Handled = true;
    }

    #endregion

    #region OnMouseMove

    public void HandleMouseMove(MouseEventArgs e)
    {
        if (_state.IsPanning)
        {
            var currentPoint = e.GetPosition(_scrollViewer);
            var delta = currentPoint - _state.PanStartPoint;

            _scrollViewer.ScrollToHorizontalOffset(_state.ScrollStartOffset.X - delta.X);
            _scrollViewer.ScrollToVerticalOffset(_state.ScrollStartOffset.Y - delta.Y);
            return;
        }

        // Рисование кистью
        if (_drawingHandler.IsDrawing && e.LeftButton == MouseButtonState.Pressed)
        {
            _drawingHandler.ContinueDrawingStroke(e);
            return;
        }

        // Resize элементов
        if (_resizeManager.IsResizing && e.LeftButton == MouseButtonState.Pressed)
        {
            _resizeManager.HandleMouseMove(e);
            return;
        }

        // Обновляем рамку выделения
        if (_state.IsSelecting && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateSelection(e);
            return;
        }

        // Перетаскивание элементов
        if (_state.DraggedElement != null && e.LeftButton == MouseButtonState.Pressed)
        {
            DragElements(e);
        }
    }

    #endregion

    #region OnMouseUp

    public void HandleMouseUp(MouseButtonEventArgs e)
    {
        // Завершаем рисование кистью
        if (_drawingHandler.IsDrawing && e.ChangedButton == MouseButton.Left)
        {
            _drawingHandler.EndDrawingStroke();
        }

        // Завершаем resize
        if (_resizeManager.IsResizing && e.ChangedButton == MouseButton.Left)
        {
            _resizeManager.HandleMouseUp();
        }

        // Завершаем выделение рамкой
        if (_state.IsSelecting && e.ChangedButton == MouseButton.Left)
        {
            EndSelection();
            if (_state.SelectedItemIds.Count == 1)
            {
                _resizeManager.ShowResizeHandles(_state.SelectedItemIds.First());
            }
        }

        // Останавливаем перетаскивание элемента
        if (_state.DraggedElement != null && e.ChangedButton == MouseButton.Left)
        {
            EndDrag();
        }

        // Останавливаем панорамирование
        if (_state.IsPanning && (e.ChangedButton == MouseButton.Middle
                                  || e.ChangedButton == MouseButton.Left
                                  || e.ChangedButton == MouseButton.Right))
        {
            StopPan();
        }
    }

    #endregion

    #region Drag & Drop изображений

    public void HandleDragEnter(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Any(IsImageFile))
            {
                e.Effects = DragDropEffects.Copy;
                return;
            }
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    public void HandleDragOver(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Any(IsImageFile))
            {
                e.Effects = DragDropEffects.Copy;
                return;
            }
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    public void HandleDrop(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        Logger.Info<BoardInteractionHandler>("Drop: файлы перетаскиваются на доску");

        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files == null) return;

        var dropPos = e.GetPosition(_canvas);

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

                var fileBytes = System.IO.File.ReadAllBytes(file);
                var base64 = Convert.ToBase64String(fileBytes);

                AddImageElement(bitmapImage, base64, bitmapImage.PixelWidth, bitmapImage.PixelHeight,
                    dropPos.X - bitmapImage.PixelWidth / 2,
                    dropPos.Y - bitmapImage.PixelHeight / 2);
            }
            catch (System.Exception ex)
            {
                Logger.Error<BoardInteractionHandler>($"Ошибка загрузки изображения (drag & drop): {ex.Message}");
            }
        }

        e.Handled = true;
    }

    #endregion

    #region Множественное выделение (rubber band)

    private void StartSelection(MouseButtonEventArgs e)
    {
        _state.IsSelecting = true;
        _state.SelectionStartPoint = e.GetPosition(_canvas);
        _state.SelectedItemIds.Clear();

        _state.SelectionRectangle = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromArgb(180, 0, 120, 212)),
            StrokeThickness = 1,
            Fill = new SolidColorBrush(Color.FromArgb(30, 0, 120, 212)),
            StrokeDashArray = new DoubleCollection { 4, 2 },
            Width = 0,
            Height = 0,
            IsHitTestVisible = false
        };

        Canvas.SetLeft(_state.SelectionRectangle, _state.SelectionStartPoint.X);
        Canvas.SetTop(_state.SelectionRectangle, _state.SelectionStartPoint.Y);
        _canvas.Children.Add(_state.SelectionRectangle);
    }

    private void UpdateSelection(MouseEventArgs e)
    {
        if (!_state.IsSelecting || _state.SelectionRectangle == null) return;

        var currentPos = e.GetPosition(_canvas);
        var x = Math.Min(_state.SelectionStartPoint.X, currentPos.X);
        var y = Math.Min(_state.SelectionStartPoint.Y, currentPos.Y);
        var width = Math.Abs(currentPos.X - _state.SelectionStartPoint.X);
        var height = Math.Abs(currentPos.Y - _state.SelectionStartPoint.Y);

        Canvas.SetLeft(_state.SelectionRectangle, x);
        Canvas.SetTop(_state.SelectionRectangle, y);
        _state.SelectionRectangle.Width = width;
        _state.SelectionRectangle.Height = height;

        var selectionRect = new Rect(x, y, width, height);
        _state.SelectedItemIds.Clear();
        DeselectImageBorder();
        _resizeManager.HideResizeHandles();

        foreach (var kvp in _state.ElementMap)
        {
            var element = kvp.Value;
            var item = _state.GetItemById(kvp.Key);
            if (item == null) continue;

            var elementRect = new Rect(item.X, item.Y, item.Width, item.Height);
            if (selectionRect.IntersectsWith(elementRect))
            {
                _state.SelectedItemIds.Add(kvp.Key);
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
        if (_state.SelectionRectangle != null)
        {
            _canvas.Children.Remove(_state.SelectionRectangle);
            _state.SelectionRectangle = null;
        }
        _state.IsSelecting = false;
    }

    #endregion

    #region Выделение и подсветка

    public void ClearSelection()
    {
        EndSelection();
        _state.SelectedItemIds.Clear();
        _factory.DeactivateAllTextBoxesAndClearFocus();

        foreach (var kvp in _state.ElementMap)
        {
            HighlightElement(kvp.Value, false);
        }
        DeselectImageBorder();
        _resizeManager.HideResizeHandles();
    }

    private void HighlightElement(FrameworkElement element, bool highlight)
    {
        if (element is Border border)
        {
            if (highlight)
            {
                AddSelectionOverlay(border);
                _state.SelectedImageBorder = border;
            }
            else if (border.Tag is Guid id)
            {
                RemoveSelectionOverlay(id);
                if (_state.SelectedImageBorder == border)
                    _state.SelectedImageBorder = null;
            }
        }
        else if (element is TextBox textBox)
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

    private void AddSelectionOverlay(Border border)
    {
        if (border.Tag is not Guid id) return;

        RemoveSelectionOverlay(id);
        _state.SelectedImageBorder = border;

        var overlay = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0, 120, 212)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            IsHitTestVisible = false
        };

        var left = Canvas.GetLeft(border);
        var top = Canvas.GetTop(border);
        Canvas.SetLeft(overlay, left);
        Canvas.SetTop(overlay, top);
        overlay.Width = border.ActualWidth > 0 ? border.ActualWidth : border.Width;
        overlay.Height = border.ActualHeight > 0 ? border.ActualHeight : border.Height;

        _state.SelectionOverlays[id] = overlay;
        _canvas.Children.Add(overlay);
    }

    private void RemoveSelectionOverlay(Guid id)
    {
        if (_state.SelectionOverlays.TryGetValue(id, out var overlay))
        {
            _canvas.Children.Remove(overlay);
            _state.SelectionOverlays.Remove(id);
        }
    }

    private void DeselectImageBorder()
    {
        foreach (var overlay in _state.SelectionOverlays.Values.ToList())
        {
            _canvas.Children.Remove(overlay);
        }
        _state.SelectionOverlays.Clear();
        _state.SelectedImageBorder = null;
    }

    private void SelectImageBorder(Border border) => AddSelectionOverlay(border);

    private void UpdateSelectionOverlayPosition(FrameworkElement element)
    {
        if (element.Tag is not Guid id) return;
        if (!_state.SelectionOverlays.TryGetValue(id, out var overlay)) return;

        var left = Canvas.GetLeft(element);
        var top = Canvas.GetTop(element);
        Canvas.SetLeft(overlay, left);
        Canvas.SetTop(overlay, top);
        overlay.Width = element.ActualWidth > 0 ? element.ActualWidth : element.Width;
        overlay.Height = element.ActualHeight > 0 ? element.ActualHeight : element.Height;
    }

    /// <summary>Обновить оверлей выделения для текущего ресайз-элемента (вызывается из ResizeHandleManager).</summary>
    public void UpdateCurrentResizeSelectionOverlay()
    {
        if (_state.ElementMap.TryGetValue(_resizeManager.ResizeItemId, out var elem))
        {
            UpdateSelectionOverlayPosition(elem);
        }
    }

    private void UpdateAllSelectionOverlayPositions()
    {
        foreach (var kvp in _state.SelectionOverlays)
        {
            if (_state.ElementMap.TryGetValue(kvp.Key, out var element))
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

    #endregion

    #region Перетаскивание элементов

    public void StartDrag(FrameworkElement element, MouseButtonEventArgs e)
    {
        _state.DraggedElement = element;
        _state.DragStartPoint = e.GetPosition(_canvas);
        _state.ElementStartPos = new Point(Canvas.GetLeft(element), Canvas.GetTop(element));

        _state.DragStartPositions.Clear();
        foreach (var id in _state.SelectedItemIds)
        {
            var item = _state.GetItemById(id);
            if (item != null)
            {
                _state.DragStartPositions[id] = new Point(item.X, item.Y);
            }
        }

        if (!_state.DragStartPositions.ContainsKey(Guid.Empty) && element.Tag is Guid clickedId)
        {
            if (!_state.DragStartPositions.ContainsKey(clickedId))
            {
                _state.DragStartPositions[clickedId] = _state.ElementStartPos;
            }
        }

        element.CaptureMouse();
    }

    private void DragElements(MouseEventArgs e)
    {
        var currentPos = e.GetPosition(_canvas);
        var deltaX = currentPos.X - _state.DragStartPoint.X;
        var deltaY = currentPos.Y - _state.DragStartPoint.Y;

        foreach (var kvp in _state.DragStartPositions)
        {
            var id = kvp.Key;
            var startPos = kvp.Value;
            var newX = startPos.X + deltaX;
            var newY = startPos.Y + deltaY;

            if (_state.ElementMap.TryGetValue(id, out var element))
            {
                Canvas.SetLeft(element, newX);
                Canvas.SetTop(element, newY);
            }

            var item = _state.GetItemById(id);
            if (item != null)
            {
                item.X = newX;
                item.Y = newY;

                if (_state.SelectedItemIds.Count == 1 && _resizeManager.ResizeItemId == id)
                {
                    _resizeManager.UpdateResizeHandlesPosition(item);
                }
            }
        }

        UpdateAllSelectionOverlayPositions();
    }

    private void EndDrag()
    {
        var movements = new Dictionary<Guid, (Point OldPos, Point NewPos)>();
        foreach (var kvp in _state.DragStartPositions)
        {
            var item = _state.GetItemById(kvp.Key);
            if (item != null)
            {
                var newPos = new Point(item.X, item.Y);
                if (Math.Abs(kvp.Value.X - newPos.X) > 0.5 || Math.Abs(kvp.Value.Y - newPos.Y) > 0.5)
                {
                    movements[kvp.Key] = (kvp.Value, newPos);
                }
            }
        }

        _state.DraggedElement.ReleaseMouseCapture();
        _state.DraggedElement = null;
        _state.DragStartPositions.Clear();

        if (movements.Count > 0)
        {
            _state.UndoManager.ExecuteCommand(new MoveItemsCommand(movements, SetItemPosition));
        }

        _persistence.SaveBoard(_scrollViewer);

        if (_state.SelectedItemIds.Count == 1)
        {
            var id = _state.SelectedItemIds.First();
            _resizeManager.ShowResizeHandles(id);
        }
    }

    private void SetItemPosition(Guid id, double x, double y)
    {
        var item = _state.GetItemById(id);
        if (item == null) return;

        item.X = x;
        item.Y = y;

        if (_state.ElementMap.TryGetValue(id, out var element))
        {
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
        }

        if (_state.SelectedItemIds.Count == 1 && _resizeManager.ResizeItemId == id)
        {
            _resizeManager.UpdateResizeHandlesPosition(item);
        }
    }

    #endregion

    #region Добавление элементов

    /// <summary>Добавить текстовый блок.</summary>
    public void AddTextBlock(string? text = null, double x = 0, double y = 0)
    {
        Logger.Info<BoardInteractionHandler>($"Добавление текстового блока в ({x:F0},{y:F0})");

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

        _state.UndoManager.ExecuteCommand(new AddItemCommand(
            item,
            addItem: i => _factory.CreateTextElement((TextBoardItem)i),
            removeItem: _factory.RemoveElementById
        ));
    }

    /// <summary>Добавить изображение из файла.</summary>
    public void AddImageFromFile(double x = 0, double y = 0)
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

                var fileBytes = System.IO.File.ReadAllBytes(openFileDialog.FileName);
                var base64 = Convert.ToBase64String(fileBytes);

                AddImageElement(bitmapImage, base64, bitmapImage.PixelWidth, bitmapImage.PixelHeight, x, y);
            }
            catch (System.Exception ex)
            {
                Logger.Error<BoardInteractionHandler>($"Ошибка загрузки изображения: {ex.Message}");
            }
        }
    }

    /// <summary>Добавить изображение из буфера обмена.</summary>
    public void AddImageFromClipboard(double x = 0, double y = 0)
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
        using (var memoryStream = new System.IO.MemoryStream())
        {
            encoder.Save(memoryStream);
            memoryStream.Seek(0, System.IO.SeekOrigin.Begin);
            bitmapImage.StreamSource = memoryStream;
            bitmapImage.EndInit();
            bitmapImage.Freeze();

            memoryStream.Seek(0, System.IO.SeekOrigin.Begin);
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

        var frozenSource = imageSource;

        _state.UndoManager.ExecuteCommand(new AddItemCommand(
            item,
            addItem: i => _factory.CreateImageElement((ImageBoardItem)i, frozenSource),
            removeItem: _factory.RemoveElementById
        ));
    }

    #endregion

    #region Удаление элементов

    public void DeleteSelectedItems()
    {
        var idsToDelete = _state.SelectedItemIds.ToList();
        if (idsToDelete.Count == 0) return;
        DeleteItemsWithUndo(idsToDelete);
        _state.SelectedItemIds.Clear();
        _resizeManager.HideResizeHandles();
    }

    public void DeleteBoardItem(Guid id)
    {
        DeleteItemsWithUndo(new List<Guid> { id });
    }

    private void DeleteItemsWithUndo(List<Guid> ids)
    {
        var itemsToDelete = _state.BoardItems.Where(i => ids.Contains(i.Id)).ToList();
        if (itemsToDelete.Count == 0) return;

        Logger.Info<BoardInteractionHandler>($"Удаление {itemsToDelete.Count} элементов: {string.Join(", ", itemsToDelete.Select(i => $"{i.GetType().Name}:{i.Id:N}"))}");

        foreach (var id in ids)
        {
            if (_state.ElementMap.TryGetValue(id, out var element))
            {
                HighlightElement(element, false);
            }
        }
        _state.SelectedItemIds.ExceptWith(ids);
        _resizeManager.HideResizeHandles();

        _state.UndoManager.ExecuteCommand(new DeleteItemsCommand(
            itemsToDelete,
            restoreItem: _factory.RestoreItem,
            executeDelete: () =>
            {
                foreach (var item in itemsToDelete)
                {
                    _state.BoardItems.Remove(item);
                    if (_state.ElementMap.TryGetValue(item.Id, out var element))
                    {
                        _canvas.Children.Remove(element);
                        _state.ElementMap.Remove(item.Id);
                    }
                }
                _persistence.SaveBoard(_scrollViewer);
            }
        ));
    }

    #endregion

    #region Обработчики кликов элементов (подписываются в фабрике)

    /// <summary>Обработка клика по текстовому элементу.</summary>
    public void OnTextElementClick(FrameworkElement sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox textBox) return;

        // Двойной клик — режим редактирования
        if (e.ClickCount == 2)
        {
            if (_state.DraggedElement == textBox)
            {
                _state.DraggedElement.ReleaseMouseCapture();
                _state.DraggedElement = null;
            }

            _factory.BringToFront(textBox);

            textBox.IsReadOnly = false;
            textBox.IsReadOnlyCaretVisible = true;
            textBox.Cursor = Cursors.IBeam;
            textBox.Focus();
            Keyboard.Focus(textBox);
            e.Handled = true;
            return;
        }

        // Одиночный клик — перетаскивание
        e.Handled = true;
        _factory.DeactivateAllTextBoxes();

        var clickedInSelection = textBox.Tag is Guid clickedId && _state.SelectedItemIds.Contains(clickedId);
        if (!clickedInSelection)
        {
            _state.SelectedItemIds.Clear();
            _resizeManager.HideResizeHandles();
            foreach (var kvp in _state.ElementMap)
            {
                HighlightElement(kvp.Value, false);
            }
            if (textBox.Tag is Guid id)
            {
                _state.SelectedItemIds.Add(id);
                HighlightElement(textBox, true);
                _resizeManager.ShowResizeHandles(id);
            }
        }

        _factory.BringToFront(textBox);
        StartDrag(textBox, e);
    }

    /// <summary>Обработка клика по изображению (ЛКМ).</summary>
    public void OnImageLeftClick(Border border, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (e.ClickCount == 2)
        {
            OpenImageInDefaultApp(border);
            return;
        }

        _factory.DeactivateAllTextBoxesAndClearFocus();

        var clickedInSelection = border.Tag is Guid clickedImgId && _state.SelectedItemIds.Contains(clickedImgId);
        if (!clickedInSelection)
        {
            _state.SelectedItemIds.Clear();
            foreach (var kvp in _state.ElementMap)
            {
                HighlightElement(kvp.Value, false);
            }
            if (border.Tag is Guid imgId)
            {
                _state.SelectedItemIds.Add(imgId);
                _resizeManager.ShowResizeHandles(imgId);
            }
        }

        _factory.BringToFront(border);
        SelectImageBorder(border);
        StartDrag(border, e);
    }

    /// <summary>Обработка клика по штриху (ЛКМ).</summary>
    public void OnStrokeLeftClick(Border border, MouseButtonEventArgs e)
    {
        e.Handled = true;

        _factory.DeactivateAllTextBoxesAndClearFocus();

        var clickedInSelection = border.Tag is Guid clickedId && _state.SelectedItemIds.Contains(clickedId);
        if (!clickedInSelection)
        {
            _state.SelectedItemIds.Clear();
            foreach (var kvp in _state.ElementMap)
            {
                HighlightElement(kvp.Value, false);
            }
            if (border.Tag is Guid id)
            {
                _state.SelectedItemIds.Add(id);
                _resizeManager.ShowResizeHandles(id);
            }
        }

        _factory.BringToFront(border);
        SelectImageBorder(border);
        StartDrag(border, e);
    }

    /// <summary>Обработка ПКМ по элементу (контекстное меню).</summary>
    public void OnElementRightClick(FrameworkElement element, MouseButtonEventArgs e)
    {
        if (element.Tag is not Guid id) return;

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

    /// <summary>Обработка изменения текста — обновление resize handles.</summary>
    public void OnTextChanged(Guid id)
    {
        if (_resizeManager.ResizeItemId == id && _state.ElementMap.TryGetValue(id, out var element) && element is TextBox tb)
        {
            if (!double.IsNaN(tb.ActualWidth) && !double.IsNaN(tb.ActualHeight))
            {
                _resizeManager.UpdateResizeHandlesPosition(tb, id);
            }
        }
    }

    /// <summary>Обработка потери фокуса TextBox.</summary>
    public void OnTextBoxLostFocus(TextBox tb, Guid id)
    {
        if (_resizeManager.ResizeItemId == id)
        {
            _resizeManager.UpdateResizeHandlesPosition(tb, id);
        }

        if (_state.EditingTextBeforeChange != null)
        {
            var currentItem = _state.GetItemById(id) as TextBoardItem;
            if (currentItem != null && currentItem.Text != _state.EditingTextBeforeChange)
            {
                _state.UndoManager.ExecuteCommand(new EditTextCommand(
                    id, _state.EditingTextBeforeChange, currentItem.Text, SetItemText));
            }
            _state.EditingTextBeforeChange = null;
        }
    }

    /// <summary>Обработка получения фокуса TextBox.</summary>
    public void OnTextBoxGotFocus(Guid id)
    {
        var model = _state.GetItemById(id) as TextBoardItem;
        if (model != null)
        {
            _state.EditingTextBeforeChange = model.Text;
        }
    }

    private void SetItemText(Guid id, string text)
    {
        var item = _state.GetItemById(id) as TextBoardItem;
        if (item == null) return;

        item.Text = text;

        if (_state.ElementMap.TryGetValue(id, out var element) && element is TextBox textBox)
        {
            textBox.Text = text;
        }

        _persistence.SaveBoard(_scrollViewer);
    }

    #endregion

    #region Открытие изображения

    private void OpenImageInDefaultApp(Border border)
    {
        if (border.Tag is not Guid imageId) return;

        var imageItem = _state.GetItemById(imageId) as ImageBoardItem;
        if (imageItem == null || string.IsNullOrEmpty(imageItem.ImageDataBase64)) return;

        try
        {
            var bytes = Convert.FromBase64String(imageItem.ImageDataBase64);
            var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MemoNotes");
            System.IO.Directory.CreateDirectory(tempDir);

            var fileName = string.IsNullOrEmpty(imageItem.OriginalName)
                ? $"image_{imageId:N}.png"
                : imageItem.OriginalName;

            var tempPath = System.IO.Path.Combine(tempDir, fileName);
            System.IO.File.WriteAllBytes(tempPath, bytes);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
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

    #endregion

    #region Горячие клавиши

    public void HandlePreviewKeyDown(KeyEventArgs e)
    {
        if (e.IsRepeat)
            return;

        var isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        Logger.Debug<BoardInteractionHandler>($"KeyDown: key={e.Key}, ctrl={isCtrl}, focused={Keyboard.FocusedElement?.GetType().Name ?? "null"}");

        // Ctrl+Z — Отмена
        if (e.Key == Key.Z && isCtrl && !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
        {
            var focusedElement = Keyboard.FocusedElement;
            if (focusedElement is TextBox tb && !tb.IsReadOnly)
                return;

            if (_state.UndoManager.CanUndo)
            {
                _state.UndoManager.Undo();
                e.Handled = true;
            }
            return;
        }

        // Ctrl+Shift+Z или Ctrl+Y — Повтор
        if ((e.Key == Key.Z && isCtrl && (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)))
            || (e.Key == Key.Y && isCtrl))
        {
            var focusedElement = Keyboard.FocusedElement;
            if (focusedElement is TextBox tb && !tb.IsReadOnly)
                return;

            if (_state.UndoManager.CanRedo)
            {
                _state.UndoManager.Redo();
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

        // T — добавить текст
        if (e.Key == Key.T && !isCtrl)
        {
            if (Keyboard.FocusedElement is not TextBox)
            {
                AddTextBlock();
                e.Handled = true;
            }
        }

        // B — переключить режим кисти
        if (e.Key == Key.B && !isCtrl)
        {
            if (Keyboard.FocusedElement is not TextBox)
            {
                _drawingHandler.ToggleDrawingMode();
                e.Handled = true;
            }
        }

        // Delete — удалить выделенные элементы
        if (e.Key == Key.Delete)
        {
            var focusedElement = Keyboard.FocusedElement;
            if (focusedElement is TextBox tb && !tb.IsReadOnly)
                return;

            if (_state.HasSelection)
            {
                DeleteSelectedItems();
                e.Handled = true;
            }
            else if (_state.SelectedImageBorder != null && _state.SelectedImageBorder.Tag is Guid imageId)
            {
                DeleteBoardItem(imageId);
                _state.SelectedImageBorder = null;
                e.Handled = true;
            }
            else if (focusedElement is TextBox focusedTb && focusedTb.Tag is Guid focusedId)
            {
                DeleteBoardItem(focusedId);
                e.Handled = true;
            }
            else if (focusedElement is not TextBox && _state.BoardItems.Count > 0)
            {
                var lastItem = _state.BoardItems[_state.BoardItems.Count - 1];
                DeleteBoardItem(lastItem.Id);
                e.Handled = true;
            }
        }

        // Escape
        if (e.Key == Key.Escape)
        {
            if (_drawingHandler.IsDrawingMode)
            {
                _drawingHandler.ToggleDrawingMode();
            }
            Keyboard.Focus(_canvas);
            e.Handled = true;
            return;
        }

        // Если фокус не в TextBox — блокируем передачу события дальше,
        // чтобы не перехватывался фокус и MouseDown продолжал работать
        if (Keyboard.FocusedElement is not TextBox)
        {
            e.Handled = true;
        }
    }

    #endregion

    #region Утилиты

    private Point GetVisibleCenter()
    {
        var scrollOffsetX = _scrollViewer.HorizontalOffset;
        var scrollOffsetY = _scrollViewer.VerticalOffset;
        var viewportWidth = _scrollViewer.ViewportWidth;
        var viewportHeight = _scrollViewer.ViewportHeight;

        var centerX = (scrollOffsetX + viewportWidth / 2) / _state.CurrentZoom;
        var centerY = (scrollOffsetY + viewportHeight / 2) / _state.CurrentZoom;

        return new Point(centerX - 100 + Random.Shared.Next(-50, 50),
                         centerY - 50 + Random.Shared.Next(-50, 50));
    }

    private static bool IsImageFile(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tiff" or ".webp" or ".ico";
    }

    #endregion
}
