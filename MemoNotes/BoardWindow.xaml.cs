using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MemoNotes.Board;
using MemoNotes.Service.CloudSync;
using MemoNotes.Service.Logging;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace MemoNotes;

public partial class BoardWindow : Window
{
    private BoardState _state = null!;
    private BoardElementFactory _factory = null!;
    private BoardPersistence _persistence = null!;
    private ResizeHandleManager _resizeManager = null!;
    private DrawingModeHandler _drawingHandler = null!;
    private BoardInteractionHandler _interaction = null!;
    private WindowChromeBehavior _chromeBehavior = null!;
    private volatile bool _isBoardLoaded;

    public BoardWindow()
    {
        Logger.Info<BoardWindow>("Инициализация BoardWindow...");
        InitializeComponent();

        Width = Properties.Settings.Default.BoardWindowWidth;
        Height = Properties.Settings.Default.BoardHeight;
        Topmost = Properties.Settings.Default.TopmostTextBoxWindow;
        RefreshPinnedButtonState();

        Logger.Info<BoardWindow>($"Размеры окна: {Width}x{Height}, Topmost={Topmost}");

        // Инициализация компонентов
        _state = new BoardState();
        _factory = new BoardElementFactory(BoardCanvas, _state, SaveBoard);
        _persistence = new BoardPersistence(_state, _factory, Dispatcher);
        _resizeManager = new ResizeHandleManager(BoardCanvas, _state, _factory, SaveBoard);
        _drawingHandler = new DrawingModeHandler(BoardCanvas, _state, _factory, ClearSelection, BrushButton);
        _interaction = new BoardInteractionHandler(
            BoardCanvas, BoardScrollViewer, BoardScaleTransform,
            _state, _factory, _resizeManager, _drawingHandler, _persistence);

        Logger.Info<BoardWindow>("Все компоненты доски инициализированы");

        // Подключаем обработчики элементов
        WireFactoryCallbacks();

        // Подписка на события облака
        CloudSyncManager.CloudConnected += OnCloudConnected;
        CloudSyncManager.CloudDataUpdated += OnCloudDataUpdated;
        CloudSyncManager.SyncStarted += OnSyncStarted;
        CloudSyncManager.SyncCompleted += OnSyncCompleted;

        // Загрузка доски: сначала локальная (быстро), потом фоновая синхронизация с облаком
        LoadBoardLocal();
        _ = SyncBoardFromCloudAsync();

        // События
        PreviewKeyDown += BoardWindow_PreviewKeyDown;
        PreviewKeyUp += BoardWindow_PreviewKeyUp;

        BoardCanvas.Width = 4000;
        BoardCanvas.Height = 4000;

        BoardScrollViewer.SizeChanged += (s, e) => UpdateCanvasSize();
        UpdateCanvasSize();

        // Скролл в центр выполняется внутри LoadBoardAsync при необходимости

        // Инициализация поведения окна (Win32 ресайз)
        _chromeBehavior = new WindowChromeBehavior(this, MaximizeButton);
        _chromeBehavior.OnWindowClosing = () =>
        {
            Logger.Info<BoardWindow>("Окно закрывается, сохранение...");

            // Отписка от событий облака (предотвращает утечку и вызовы на закрытом окне)
            CloudSyncManager.CloudConnected -= OnCloudConnected;
            CloudSyncManager.CloudDataUpdated -= OnCloudDataUpdated;
            CloudSyncManager.SyncStarted -= OnSyncStarted;
            CloudSyncManager.SyncCompleted -= OnSyncCompleted;

            // Не сохраняем пустую доску, если загрузка ещё не завершена
            if (!_isBoardLoaded)
            {
                Logger.Warn<BoardWindow>("Окно закрывается до завершения загрузки доски — сохранение пропущено");
            }
            else
            {
                _persistence.SaveBoardImmediate(BoardScrollViewer);
            }
            Properties.Settings.Default.BoardWindowWidth = Width;
            Properties.Settings.Default.BoardHeight = Height;
            Properties.Settings.Default.TopmostTextBoxWindow = Topmost;
            Properties.Settings.Default.Save();
        };
        _chromeBehavior.Initialize();

        // Очистка истории undo при загрузке
        _state.UndoManager.Clear();

        // Очистка старых логов
        Logger.CleanOldLogs(30);

        Logger.Info<BoardWindow>("BoardWindow полностью инициализирован");
    }

    #region Подключение callback'ов фабрики

    private void WireFactoryCallbacks()
    {
        _factory.OnTextElementClick = _interaction.OnTextElementClick;
        _factory.OnImageLeftClick = _interaction.OnImageLeftClick;
        _factory.OnElementRightClick = _interaction.OnElementRightClick;
        _factory.OnStrokeLeftClick = _interaction.OnStrokeLeftClick;
        _factory.OnTextChanged = _interaction.OnTextChanged;
        _factory.OnTextBoxLostFocus = _interaction.OnTextBoxLostFocus;
        _factory.OnTextBoxGotFocus = _interaction.OnTextBoxGotFocus;
    }

    #endregion

    #region Canvas Size

    private void UpdateCanvasSize()
    {
        if (BoardScrollViewer.ViewportWidth > 0 && BoardScrollViewer.ViewportHeight > 0 && _state.CurrentZoom > 0)
        {
            var minWidth = BoardScrollViewer.ViewportWidth / _state.CurrentZoom;
            var minHeight = BoardScrollViewer.ViewportHeight / _state.CurrentZoom;
            BoardCanvas.Width = Math.Max(4000, minWidth);
            BoardCanvas.Height = Math.Max(4000, minHeight);
        }
    }

    #endregion

    #region Сохранение и загрузка

    private void SaveBoard()
    {
        Logger.Info<BoardWindow>("Вызов SaveBoard");
        _persistence.SaveBoard(BoardScrollViewer);
    }

    /// <summary>
    /// Синхронная загрузка доски из локального файла (без обращения к облаку).
    /// Оконьо открывается мгновенно.
    /// </summary>
    private void LoadBoardLocal()
    {
        var loadResult = _persistence.LoadBoard(BoardScrollViewer, BoardScaleTransform, ZoomText);
        _isBoardLoaded = true;
        Logger.Info<BoardWindow>($"Локальная загрузка доски: результат={loadResult}");

        if (loadResult == BoardPersistence.LoadResult.FileNotExists)
        {
            ScrollToCenter();
        }
    }

    /// <summary>
    /// Фоновая синхронизация доски с облаком после локальной загрузки.
    /// Показывает индикатор синхронизации. Если в облаке более новая версия — перезагружает доску.
    /// </summary>
    private async Task SyncBoardFromCloudAsync()
    {
        if (!CloudSyncManager.IsEnabled)
            return;

        try
        {
            var cloudData = await CloudSyncManager.SyncOnLoadAsync(BoardPersistence.DataFilePath);

            if (cloudData != null && !IsLoaded)
                return; // Окно уже закрыто

            if (cloudData != null)
            {
                Logger.Info<BoardWindow>("Облако: загружена более новая версия — перезагрузка доски");
                Dispatcher.Invoke(() =>
                {
                    _state.BoardItems.Clear();
                    BoardCanvas.Children.Clear();
                    var loadResult = _persistence.LoadFromJson(
                        cloudData.Content, BoardScrollViewer, BoardScaleTransform, ZoomText);
                    Logger.Info<BoardWindow>($"Перезагрузка из облака: результат={loadResult}");
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Warn<BoardWindow>($"Ошибка фоновой синхронизации: {ex.Message}");
        }
    }

    private void ScrollToCenter()
    {
        Dispatcher.BeginInvoke(() =>
        {
            BoardScrollViewer.ScrollToHorizontalOffset((BoardCanvas.Width * _state.CurrentZoom - BoardScrollViewer.ViewportWidth) / 2);
            BoardScrollViewer.ScrollToVerticalOffset((BoardCanvas.Height * _state.CurrentZoom - BoardScrollViewer.ViewportHeight) / 2);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnSyncStarted() => SetSyncIndicatorVisible(true);
    private void OnSyncCompleted() => SetSyncIndicatorVisible(false);

    private void SetSyncIndicatorVisible(bool visible)
    {
        Dispatcher.BeginInvoke(() =>
        {
            SyncIndicator.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    /// <summary>
    /// Обработчик подключения облака — перезагружает доску с облачными данными.
    /// </summary>
    private async void OnCloudConnected()
    {
        Logger.Info<BoardWindow>("Облако подключено — перезагрузка доски с синхронизацией");
        try
        {
            var cloudData = await CloudSyncManager.SyncOnLoadAsync(BoardPersistence.DataFilePath);
            if (cloudData != null && IsLoaded)
            {
                Logger.Info<BoardWindow>("Облако: загружена более новая версия — перезагрузка доски");
                _state.BoardItems.Clear();
                BoardCanvas.Children.Clear();
                _persistence.LoadFromJson(cloudData.Content, BoardScrollViewer, BoardScaleTransform, ZoomText);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn<BoardWindow>($"Ошибка перезагрузки при подключении облака: {ex.Message}");
        }
    }

    /// <summary>
    /// Обработчик обновления данных в облаке (polling) — перезагружает доску.
    /// </summary>
    private async void OnCloudDataUpdated()
    {
        if (!IsLoaded) return;
        Logger.Info<BoardWindow>("Облако: данные обновлены — перезагрузка доски");
        try
        {
            var cloudData = await CloudSyncManager.SyncOnLoadAsync(BoardPersistence.DataFilePath);
            if (cloudData != null && IsLoaded)
            {
                _state.BoardItems.Clear();
                BoardCanvas.Children.Clear();
                _persistence.LoadFromJson(cloudData.Content, BoardScrollViewer, BoardScaleTransform, ZoomText);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn<BoardWindow>($"Ошибка перезагрузки при обновлении облака: {ex.Message}");
        }
    }

    #endregion

    #region Делегирование событий XAML

    // Зум
    private void BoardCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        => _interaction.HandleMouseWheel(e);

    // Нажатие мыши на Canvas
    private void BoardCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        => _interaction.HandleMouseDown(e);

    private void BoardCanvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        => _interaction.HandleRightButtonUp(e);

    // Drag & Drop
    private void BoardCanvas_DragEnter(object sender, DragEventArgs e)
        => _interaction.HandleDragEnter(e);

    private void BoardCanvas_DragOver(object sender, DragEventArgs e)
        => _interaction.HandleDragOver(e);

    private void BoardCanvas_Drop(object sender, DragEventArgs e)
        => _interaction.HandleDrop(e);

    // Движение мыши
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _interaction.HandleMouseMove(e);
    }

    // Отпускание кнопки мыши
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        _interaction.HandleMouseUp(e);
    }

    // Кнопки тулбара
    private void AddTextButton_Click(object sender, RoutedEventArgs e)
    {
        Logger.Debug<BoardWindow>("Нажата кнопка 'Добавить текст'");
        _interaction.AddTextBlock();
    }

    private void AddImageButton_Click(object sender, RoutedEventArgs e)
    {
        Logger.Debug<BoardWindow>("Нажата кнопка 'Добавить изображение'");
        _interaction.AddImageFromFile();
    }

    private void BrushButton_Click(object sender, RoutedEventArgs e)
        => _drawingHandler.ToggleDrawingMode();

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state.HasSelection)
        {
            Logger.Debug<BoardWindow>("Нажата кнопка 'Удалить' (есть выделение)");
            _interaction.DeleteSelectedItems();
        }
        else if (_state.DraggedElement?.Tag is Guid id)
        {
            Logger.Debug<BoardWindow>($"Нажата кнопка 'Удалить' (перетаскиваемый элемент: {id})");
            _interaction.DeleteBoardItem(id);
        }
    }

    // Горячие клавиши
    private void BoardWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        => _interaction.HandlePreviewKeyDown(e);

    private void BoardWindow_PreviewKeyUp(object sender, KeyEventArgs e)
        => _drawingHandler.HandlePreviewKeyUp(e);

    // Заголовок окна
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        => _chromeBehavior.TitleBarMouseDown(e);

    // Кнопки управления окном
    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => _chromeBehavior.MaximizeButton_Click();

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Logger.Info<BoardWindow>("Нажата кнопка 'Закрыть'");
        Close(); // OnWindowClosing вызовет SaveBoardImmediate
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        Logger.Debug<BoardWindow>("Нажата кнопка 'Настройки'");
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
        Logger.Info<BoardWindow>($"Поверх всех окон: {Topmost}");
    }

    private void ResetZoom_Click(object sender, RoutedEventArgs e)
    {
        _state.CurrentZoom = 1.0;
        BoardScaleTransform.ScaleX = 1.0;
        BoardScaleTransform.ScaleY = 1.0;
        ZoomText.Text = "100%";
        _factory.UpdateStrokeThicknesses();
        Logger.Info<BoardWindow>("Зум сброшен до 100%");
    }

    #endregion

    #region Вспомогательные методы окна

    private void ClearSelection() => _interaction.ClearSelection();

    private void RefreshPinnedButtonState()
    {
        var icon = PinnedButton.Content as FontAwesome.WPF.ImageAwesome;
        if (icon != null)
        {
            icon.Foreground = Topmost
                ? new SolidColorBrush(Color.FromRgb(255, 215, 0))
                : new SolidColorBrush(Color.FromRgb(204, 204, 204));
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

    #endregion
}
