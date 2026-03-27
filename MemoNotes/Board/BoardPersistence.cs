using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MemoNotes.Models;
using MemoNotes.Service.Logging;

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
            var data = JsonSerializer.Deserialize<BoardSaveData>(json);
            if (data?.Items == null)
            {
                Logger.Warn<BoardPersistence>("Файл доски пуст или повреждён");
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
}
