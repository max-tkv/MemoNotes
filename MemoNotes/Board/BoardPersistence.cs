using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MemoNotes.Models;
using MemoNotes.Service.CloudSync;
using MemoNotes.Service.Logging;
using Timer = System.Threading.Timer;

namespace MemoNotes.Board;

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

/// <summary>
/// Управление сохранением и загрузкой состояния доски.
/// </summary>
public class BoardPersistence
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MemoNotes");
    private static readonly string BoardDataFilePath = Path.Combine(AppDataDir, "board.memo");

    public static string DataFilePath => BoardDataFilePath;

    private readonly BoardState _state;
    private readonly BoardElementFactory _factory;
    private readonly Dispatcher _dispatcher;

    /// <summary>
    /// Debounce-таймер для облачной синхронизации при сохранении.
    /// Предотвращает множественные upload'ы при быстрой серии SaveBoard (например, при загрузке доски).
    /// </summary>
    private static Timer? _syncDebounceTimer;
    private static string? _pendingSyncFilePath;
    private static readonly object _syncDebounceLock = new();
    private const int SyncDebounceMs = 2000;

    public BoardPersistence(BoardState state, BoardElementFactory factory, Dispatcher dispatcher)
    {
        _state = state;
        _factory = factory;
        _dispatcher = dispatcher;
    }

    /// <summary>Сохранить текущее состояние доски в файл.</summary>
    public void SaveBoard(ScrollViewer scrollViewer)
    {
        try
        {
            var data = new BoardSaveData
            {
                Items = _state.BoardItems.ToList(),
                Zoom = _state.CurrentZoom,
                ScrollOffsetX = scrollViewer.HorizontalOffset,
                ScrollOffsetY = scrollViewer.VerticalOffset
            };

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = false
            });

            File.WriteAllText(BoardDataFilePath, json);
            Logger.Debug<BoardPersistence>($"Доска сохранена: {data.Items.Count} элементов, зум={data.Zoom:F2}, скролл=({data.ScrollOffsetX:F0},{data.ScrollOffsetY:F0})");

            // Debounce: откладываем выгрузку в облако, чтобы не спамить при серии SaveBoard
            ScheduleCloudSync(BoardDataFilePath);
        }
        catch (Exception ex)
        {
            Logger.Error<BoardPersistence>("Ошибка сохранения доски", ex);
        }
    }

    /// <summary>
    /// Планирует выгрузку в облако с debounce-задержкой.
    /// Если вызывается повторно до истечения задержки — таймер сбрасывается.
    /// </summary>
    private static void ScheduleCloudSync(string filePath)
    {
        lock (_syncDebounceLock)
        {
            _pendingSyncFilePath = filePath;
            _syncDebounceTimer?.Dispose();
            _syncDebounceTimer = new Timer(_ => SyncDebounced(), null, SyncDebounceMs, Timeout.Infinite);
        }
    }

    private static void SyncDebounced()
    {
        string? path;
        lock (_syncDebounceLock)
        {
            path = _pendingSyncFilePath;
            _pendingSyncFilePath = null;
            _syncDebounceTimer?.Dispose();
            _syncDebounceTimer = null;
        }

        if (path != null)
        {
            _ = CloudSyncManager.SyncOnSaveAsync(path);
        }
    }

    /// <summary>
    /// Сохранить текущее состояние доски и немедленно выгрузить в облако (без debounce).
    /// Используется при закрытии окна.
    /// </summary>
    public void SaveBoardImmediate(ScrollViewer scrollViewer)
    {
        try
        {
            var data = new BoardSaveData
            {
                Items = _state.BoardItems.ToList(),
                Zoom = _state.CurrentZoom,
                ScrollOffsetX = scrollViewer.HorizontalOffset,
                ScrollOffsetY = scrollViewer.VerticalOffset
            };

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = false
            });

            File.WriteAllText(BoardDataFilePath, json);
            Logger.Debug<BoardPersistence>($"Доска сохранена (немедленно): {data.Items.Count} элементов, зум={data.Zoom:F2}");

            // Отменяем pending debounce-задачу
            lock (_syncDebounceLock)
            {
                _syncDebounceTimer?.Dispose();
                _syncDebounceTimer = null;
                _pendingSyncFilePath = null;
            }

            // Асинхронная выгрузка в облако (fire-and-forget, без задержки)
            _ = CloudSyncManager.SyncOnSaveAsync(BoardDataFilePath);
        }
        catch (Exception ex)
        {
            Logger.Error<BoardPersistence>("Ошибка сохранения доски", ex);
        }
    }

    /// <summary>Загрузить состояние доски из файла.</summary>
    public LoadResult LoadBoard(ScrollViewer scrollViewer, ScaleTransform scaleTransform, FrameworkElement zoomText)
    {
        // Миграция данных в AppData
        Directory.CreateDirectory(AppDataDir);
        var legacyPath = "board.memo";
        if (!File.Exists(BoardDataFilePath) && File.Exists(legacyPath))
        {
            try
            {
                File.Move(legacyPath, BoardDataFilePath);
                Logger.Info<BoardPersistence>("Файл доски мигрирован в AppData");
            }
            catch (Exception ex)
            {
                Logger.Warn<BoardPersistence>($"Не удалось мигрировать файл доски: {ex.Message}");
            }
        }

        if (!File.Exists(BoardDataFilePath))
        {
            Logger.Info<BoardPersistence>("Файл доски не найден — первая загрузка");
            return LoadResult.FileNotExists;
        }

        try
        {
            var json = File.ReadAllText(BoardDataFilePath);
            return LoadFromJson(json, scrollViewer, scaleTransform, zoomText);
        }
        catch (Exception ex)
        {
            Logger.Error<BoardPersistence>("Ошибка загрузки доски", ex);
            return LoadResult.Error;
        }
    }

    /// <summary>
    /// Асинхронная загрузка доски с синхронизацией из облака.
    /// Проверяет наличие обновлений в облаке перед локальной загрузкой.
    /// Если в облаке более новая версия — использует её.
    /// </summary>
    public async Task<LoadResult> LoadBoardWithCloudSyncAsync(ScrollViewer scrollViewer, ScaleTransform scaleTransform, FrameworkElement zoomText)
    {
        // Миграция и подготовка директории
        Directory.CreateDirectory(AppDataDir);
        var legacyPath = "board.memo";
        if (!File.Exists(BoardDataFilePath) && File.Exists(legacyPath))
        {
            try
            {
                File.Move(legacyPath, BoardDataFilePath);
                Logger.Info<BoardPersistence>("Файл доски мигрирован в AppData");
            }
            catch (Exception ex)
            {
                Logger.Warn<BoardPersistence>($"Не удалось мигрировать файл доски: {ex.Message}");
            }
        }

        // Проверяем наличие обновлений в облаке (не блокирует, если облако недоступно)
        try
        {
            var cloudData = await CloudSyncManager.SyncOnLoadAsync(BoardDataFilePath);
            
            if (cloudData != null)
            {
                Logger.Info<BoardPersistence>("Используется версия из облака (более новая)");
                
                // Сохраняем облачную версию в локальный файл
                try
                {
                    File.WriteAllText(BoardDataFilePath, cloudData.Content);
                }
                catch (Exception ex)
                {
                    Logger.Error<BoardPersistence>($"Не удалось сохранить облачную версию локально: {ex.Message}");
                }
                
                // Загружаем из облачных данных
                return LoadFromJson(cloudData.Content, scrollViewer, scaleTransform, zoomText);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn<BoardPersistence>($"Ошибка синхронизации при загрузке, используется локальный файл: {ex.Message}");
        }

        // Обычная локальная загрузка
        if (!File.Exists(BoardDataFilePath))
        {
            Logger.Info<BoardPersistence>("Файл доски не найден — первая загрузка");
            return LoadResult.FileNotExists;
        }

        try
        {
            var json = File.ReadAllText(BoardDataFilePath);
            return LoadFromJson(json, scrollViewer, scaleTransform, zoomText);
        }
        catch (Exception ex)
        {
            Logger.Error<BoardPersistence>("Ошибка загрузки доски", ex);
            return LoadResult.Error;
        }
    }

    public enum LoadResult
    {
        Success,
        FileNotExists,
        Empty,
        Error
    }

    /// <summary>Загрузить доску из JSON-строки.</summary>
    public LoadResult LoadFromJson(string json, ScrollViewer scrollViewer, ScaleTransform scaleTransform, FrameworkElement zoomText)
    {
        var data = JsonSerializer.Deserialize<BoardSaveData>(json);
        if (data?.Items == null)
        {
            Logger.Warn<BoardPersistence>("Данные доски пусты или повреждены");
            return LoadResult.Empty;
        }

        _state.CurrentZoom = data.Zoom;
        scaleTransform.ScaleX = _state.CurrentZoom;
        scaleTransform.ScaleY = _state.CurrentZoom;
        zoomText.SetCurrentValue(System.Windows.Controls.TextBlock.TextProperty, $"{_state.CurrentZoom * 100:F0}%");

        var scrollX = data.ScrollOffsetX;
        var scrollY = data.ScrollOffsetY;

        Logger.Info<BoardPersistence>($"Загрузка {data.Items.Count} элементов, зум={data.Zoom:F2}");

        foreach (var item in data.Items)
        {
            switch (item)
            {
                case TextBoardItem textItem:
                    _factory.CreateTextElement(textItem);
                    break;
                case ImageBoardItem imageItem:
                    try { _factory.CreateImageElement(imageItem, null); }
                    catch (Exception ex) { Logger.Error<BoardPersistence>($"Ошибка восстановления изображения {imageItem.Id}: {ex.Message}"); }
                    break;
                case StrokeBoardItem strokeItem:
                    try { _factory.CreateStrokeElement(strokeItem); }
                    catch (Exception ex) { Logger.Error<BoardPersistence>($"Ошибка восстановления штриха {strokeItem.Id}: {ex.Message}"); }
                    break;
            }
        }

        // Восстанавливаем позицию скролла после отрисовки элементов
        _dispatcher.BeginInvoke(() =>
        {
            scrollViewer.ScrollToHorizontalOffset(scrollX);
            scrollViewer.ScrollToVerticalOffset(scrollY);
        }, DispatcherPriority.Loaded);

        Logger.Info<BoardPersistence>($"Доска загружена успешно: {data.Items.Count} элементов");
        return LoadResult.Success;
    }
}
