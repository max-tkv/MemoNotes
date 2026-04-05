using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MemoNotes.Models;
using MemoNotes.Properties;
using MemoNotes.Service.Logging;
using MemoNotes.Undo;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using Image = System.Windows.Controls.Image;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;

namespace MemoNotes.Board;

/// <summary>
/// Обработчик режима рисования кистью на доске.
/// </summary>
public class DrawingModeHandler
{
    private readonly Canvas _canvas;
    private readonly BoardState _state;
    private readonly BoardElementFactory _factory;
    private readonly Action _clearSelection;
    private readonly Button _brushButton;

    private bool _isDrawingMode;
    private bool _isDrawing;
    private bool _hasContinuousStrokeStarted;
    private Path? _currentStrokePath;
    private PathGeometry? _currentStrokeGeometry;
    private PathFigure? _currentStrokeFigure;
    private readonly List<double> _currentStrokePoints = new();
    private readonly List<int> _currentStrokeFigureLengths = new();
    private int _currentFigurePointCount;
    private Point _strokeMinPoint;
    private Point _strokeMaxPoint;

    /// <summary>Включён ли режим рисования.</summary>
    public bool IsDrawingMode => _isDrawingMode;

    /// <summary>Идёт ли в данный момент процесс рисования.</summary>
    public bool IsDrawing => _isDrawing;

    public DrawingModeHandler(Canvas canvas, BoardState state, BoardElementFactory factory,
        Action clearSelection, Button brushButton)
    {
        _canvas = canvas;
        _state = state;
        _factory = factory;
        _clearSelection = clearSelection;
        _brushButton = brushButton;
    }

    /// <summary>Переключить режим рисования.</summary>
    public void ToggleDrawingMode()
    {
        _isDrawingMode = !_isDrawingMode;
        Logger.Info<DrawingModeHandler>($"Режим рисования: {(_isDrawingMode ? "ВКЛ" : "ВЫКЛ")}");

        if (_isDrawingMode)
        {
            _brushButton.Style = (Style)_canvas.FindResource("ToolbarButtonActiveStyle");
            _canvas.Cursor = Cursors.Cross;
            _clearSelection();
        }
        else
        {
            // При выключении режима — финализируем незавершённый непрерывный штрих
            if (_hasContinuousStrokeStarted)
            {
                FinalizeCurrentStroke();
            }

            _isDrawing = false;
            _brushButton.Style = (Style)_canvas.FindResource("ToolbarButtonStyle");
            _canvas.Cursor = Cursors.Arrow;
        }
    }

    /// <summary>Начать рисование штриха.</summary>
    public void StartDrawingStroke(MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(_canvas);
        var isContinuous = IsContinuousStrokeKeyDown();

        Logger.Debug<DrawingModeHandler>($"Начало рисования в ({pos.X:F0},{pos.Y:F0}), непрерывный={isContinuous}, уже_начат={_hasContinuousStrokeStarted}");

        _isDrawing = true;

        if (!isContinuous || !_hasContinuousStrokeStarted)
        {
            _currentStrokePoints.Clear();
            _currentStrokeFigureLengths.Clear();
            _strokeMinPoint = pos;
            _strokeMaxPoint = pos;
            _currentFigurePointCount = 0;

            _currentStrokeFigure = new PathFigure
            {
                StartPoint = pos,
                IsClosed = false
            };

            _currentStrokeGeometry = new PathGeometry();
            _currentStrokeGeometry.Figures.Add(_currentStrokeFigure);

            _currentStrokePath = new Path
            {
                Stroke = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                StrokeThickness = 3 / _state.CurrentZoom,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Data = _currentStrokeGeometry,
                IsHitTestVisible = false
            };

            _canvas.Children.Add(_currentStrokePath);
            _hasContinuousStrokeStarted = true;
        }
        else
        {
            _currentStrokeFigureLengths.Add(_currentFigurePointCount);
            _currentFigurePointCount = 0;

            _currentStrokeFigure = new PathFigure
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

        _canvas.CaptureMouse();
    }

    /// <summary>Продолжить рисование штриха.</summary>
    public void ContinueDrawingStroke(MouseEventArgs e)
    {
        if (_currentStrokePath == null || _currentStrokeFigure == null) return;

        var pos = e.GetPosition(_canvas);

        _currentStrokePoints.Add(pos.X);
        _currentStrokePoints.Add(pos.Y);
        _currentFigurePointCount++;

        _strokeMinPoint.X = Math.Min(_strokeMinPoint.X, pos.X);
        _strokeMinPoint.Y = Math.Min(_strokeMinPoint.Y, pos.Y);
        _strokeMaxPoint.X = Math.Max(_strokeMaxPoint.X, pos.X);
        _strokeMaxPoint.Y = Math.Max(_strokeMaxPoint.Y, pos.Y);

        var segment = new LineSegment(pos, true) { IsStroked = true };
        _currentStrokeFigure.Segments.Add(segment);
    }

    /// <summary>Завершить рисование (отпускание кнопки мыши).</summary>
    public void EndDrawingStroke()
    {
        if (_currentStrokePath == null) return;

        _isDrawing = false;
        _canvas.ReleaseMouseCapture();

        var isContinuous = IsContinuousStrokeKeyDown();
        Logger.Debug<DrawingModeHandler>($"Конец рисования, непрерывный={isContinuous}, точек_всего={_currentStrokePoints.Count / 2}");

        if (!isContinuous)
        {
            FinalizeCurrentStroke();
        }
    }

    /// <summary>Обработка PreviewKeyUp для непрерывного рисования.</summary>
    public void HandlePreviewKeyUp(KeyEventArgs e)
    {
        if (!_hasContinuousStrokeStarted) return;

        int vkCode = Settings.Default.ContinuousStrokeKey;
        if (vkCode == 0) return;

        Logger.Debug<DrawingModeHandler>($"PreviewKeyUp: key={e.Key}, vkCode={vkCode}, modifier={Settings.Default.ContinuousStrokeModifier}");

        int modifier = Settings.Default.ContinuousStrokeModifier;
        bool isPartOfCombo = false;

        if (e.Key == KeyInterop.KeyFromVirtualKey(vkCode))
            isPartOfCombo = true;

        if ((modifier & 1) != 0 && (e.Key == Key.LeftShift || e.Key == Key.RightShift))
            isPartOfCombo = true;
        if ((modifier & 2) != 0 && (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl))
            isPartOfCombo = true;
        if ((modifier & 4) != 0 && (e.Key == Key.LeftAlt || e.Key == Key.RightAlt))
            isPartOfCombo = true;

        if (isPartOfCombo && !IsContinuousStrokeKeyDown())
        {
            if (_isDrawing)
            {
                _isDrawing = false;
                _canvas.ReleaseMouseCapture();
            }
            FinalizeCurrentStroke();
        }
    }

    /// <summary>Если мы в режиме рисования и клик не по элементу — начать рисование.</summary>
    public bool TryStartDrawingOnEmptySpace(MouseButtonEventArgs e)
    {
        if (!_isDrawingMode || e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed)
            return false;

        // Если клик по элементу доски — не рисуем
        if (e.OriginalSource is Border or TextBox or Image)
        {
            ToggleDrawingMode();
            return false;
        }

        StartDrawingStroke(e);
        return true;
    }

    #region Приватные методы

    private void FinalizeCurrentStroke()
    {
        if (_currentStrokePath == null) return;

        _hasContinuousStrokeStarted = false;

        _canvas.Children.Remove(_currentStrokePath);

        if (_currentStrokePoints.Count < 4)
        {
            _currentStrokePath = null;
            _currentStrokeGeometry = null;
            _currentStrokeFigure = null;
            _currentStrokePoints.Clear();
            _currentStrokeFigureLengths.Clear();
            return;
        }

        var padding = 10.0;
        var strokeThickness = 3.0;

        var minX = _strokeMinPoint.X - padding;
        var minY = _strokeMinPoint.Y - padding;
        var maxX = _strokeMaxPoint.X + padding;
        var maxY = _strokeMaxPoint.Y + padding;
        var width = Math.Max(maxX - minX, 10);
        var height = Math.Max(maxY - minY, 10);

        var localPoints = new List<double>();
        for (int i = 0; i < _currentStrokePoints.Count; i += 2)
        {
            localPoints.Add(_currentStrokePoints[i] - minX);
            localPoints.Add(_currentStrokePoints[i + 1] - minY);
        }

        var figureLengths = new List<int>(_currentStrokeFigureLengths);
        figureLengths.Add(_currentFigurePointCount);

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

        _state.UndoManager.ExecuteCommand(new AddItemCommand(
            strokeItem,
            addItem: i => _factory.CreateStrokeElement((StrokeBoardItem)i),
            removeItem: _factory.RemoveElementById
        ));

        _currentStrokePath = null;
        _currentStrokeGeometry = null;
        _currentStrokeFigure = null;
        _currentStrokePoints.Clear();
        _currentStrokeFigureLengths.Clear();
    }

    private bool IsContinuousStrokeKeyDown()
    {
        int vkCode = Settings.Default.ContinuousStrokeKey;
        if (vkCode == 0) return false;

        int modifier = Settings.Default.ContinuousStrokeModifier;

        bool shiftOk = (modifier & 1) == 0 || Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        bool ctrlOk = (modifier & 2) == 0 || Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
        bool altOk = (modifier & 4) == 0 || Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt);

        if (!shiftOk || !ctrlOk || !altOk) return false;

        var key = KeyInterop.KeyFromVirtualKey(vkCode);
        var isDown = Keyboard.IsKeyDown(key);
        Logger.Debug<DrawingModeHandler>($"IsContinuousStrokeKeyDown: vkCode=0x{vkCode:X} ({key}), modifier={modifier}, shiftOk={shiftOk}, ctrlOk={ctrlOk}, altOk={altOk}, keyDown={isDown}");
        return isDown;
    }

    #endregion
}
