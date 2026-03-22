using System.Text.Json;
using System.Text.Json.Serialization;

namespace MemoNotes.Models;

/// <summary>
/// Базовая модель элемента на доске.
/// </summary>
[JsonPolymorphic]
[JsonDerivedType(typeof(TextBoardItem), "text")]
[JsonDerivedType(typeof(ImageBoardItem), "image")]
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

public enum BoardItemType
{
    Text,
    Image
}
