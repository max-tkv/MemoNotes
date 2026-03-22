using System.Text.Json;
using System.Text.Json.Serialization;

namespace MemoNotes.Models;

/// <summary>
/// Базовая модель элемента на доске.
/// </summary>
[JsonPolymorphic]
[JsonDerivedType(typeof(TextBoardItem), "text")]
[JsonDerivedType(typeof(ImageBoardItem), "image")]
[JsonDerivedType(typeof(StrokeBoardItem), "stroke")]
public class BoardItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    [JsonIgnore]
    public BoardItemType ItemType { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// Текстовый элемент на доске.
/// </summary>
public class TextBoardItem : BoardItem
{
    public TextBoardItem()
    {
        ItemType = BoardItemType.Text;
    }
    
    public string Text { get; set; } = string.Empty;
    public double FontSize { get; set; } = 16;
}

/// <summary>
/// Изображение на доске.
/// </summary>
public class ImageBoardItem : BoardItem
{
    public ImageBoardItem()
    {
        ItemType = BoardItemType.Image;
    }
    
    /// <summary>
    /// Base64-encoded PNG данные изображения.
    /// </summary>
    public string ImageDataBase64 { get; set; } = string.Empty;
    public string? OriginalName { get; set; }
}

/// <summary>
/// Нарисованный штрих (кисть) на доске.
/// </summary>
public class StrokeBoardItem : BoardItem
{
    public StrokeBoardItem()
    {
        ItemType = BoardItemType.Stroke;
    }

    /// <summary>
    /// Список точек штриха в локальных координатах (относительно верхнего левого угла).
    /// Хранится как плоский массив: [x0, y0, x1, y1, ...].
    /// </summary>
    public List<double> Points { get; set; } = new();

    /// <summary>
    /// Оригинальная ширина штриха (до ресайза). Используется для пересчёта координат.
    /// </summary>
    public double OriginalWidth { get; set; }

    /// <summary>
    /// Оригинальная высота штриха (до ресайза). Используется для пересчёта координат.
    /// </summary>
    public double OriginalHeight { get; set; }

    /// <summary>
    /// Цвет обводки в формате ARGB (например, "#FF0088CC").
    /// </summary>
    public string ColorHex { get; set; } = "#FFFFFFFF";

    /// <summary>
    /// Толщина линии.
    /// </summary>
    public double StrokeThickness { get; set; } = 3;
}

public enum BoardItemType
{
    Text,
    Image,
    Stroke
}
