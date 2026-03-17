using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Documents;
using System.Windows.Markup;
using Color = System.Drawing.Color;

namespace MemoNotes;

public partial class TextBoxWindow
{
    private const string FilePath = "Notes.xaml";
    private System.Windows.Controls.Image selectedImage;

    public TextBoxWindow()
    {
        InitializeComponent();
        
        LoadTextFromFile();
        
        // Восстанавливаем размеры окна при запуске
        Width = Properties.Settings.Default.WindowWidth;
        Height = Properties.Settings.Default.WindowHeight;

        // Восстанавливаем значения закрепления окна ввода по вверх всех.
        Topmost = Properties.Settings.Default.TopmostTextBoxWindow;
        RefreshForegroundPinnedButtonByTopmost();
        
        // Подписываемся на события
        InputRichTextBox.GotFocus += InputRichTextBox_GotFocus;
        InputRichTextBox.LostFocus += InputRichTextBox_LostFocus;
        InputRichTextBox.TextChanged += InputRichTextBox_TextChanged;
        InputRichTextBox.PreviewMouseDown += InputRichTextBox_PreviewMouseDown;

        // Инициализируем состояние заполнителя
        UpdatePlaceholder();
    }
    
    public void BlinkBorder()
    {
        var animation = new ColorAnimation
        {
            From = System.Windows.Media.Color.FromRgb(28, 28, 28),
            To = Colors.Blue,
            Duration = TimeSpan.FromMilliseconds(300),
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(3)
        };

        var borderBrush = new SolidColorBrush(Colors.Transparent);
        MainBorder.BorderBrush = borderBrush;
        
        borderBrush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }
    
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Properties.Settings.Default.WindowWidth = Width;
        Properties.Settings.Default.WindowHeight = Height;
        Properties.Settings.Default.Save();
    }

    private void InputRichTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePlaceholder();
        SaveDocumentToFile();
    }

    private void InputRichTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        UpdatePlaceholder();
    }

    private void InputRichTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        UpdatePlaceholder();
    }

    private void InputRichTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.V && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control)
        {
            // Вставка изображения из буфера обмена
            if (System.Windows.Clipboard.ContainsImage())
            {
                var interopBitmap = System.Windows.Clipboard.GetImage();
                if (interopBitmap != null)
                {
                    // Конвертируем InteropBitmap в BitmapImage для сериализации
                    BitmapImage bitmapImage = new BitmapImage();
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
                    }
                    
                    // Создаём InlineUIContainer с Image
                    var imageControl = new System.Windows.Controls.Image
                    {
                        Source = bitmapImage,
                        Width = bitmapImage.PixelWidth,
                        Height = bitmapImage.PixelHeight,
                        Stretch = System.Windows.Media.Stretch.None
                    };
                    var container = new InlineUIContainer(imageControl);
                    InputRichTextBox.CaretPosition.Paragraph?.Inlines.Add(container);
                    InputRichTextBox.CaretPosition = InputRichTextBox.CaretPosition.GetPositionAtOffset(1);
                    e.Handled = true; // Предотвращаем стандартную вставку текста
                }
            }
        }
    }

    private void UpdatePlaceholder()
    {
        string text = new TextRange(InputRichTextBox.Document.ContentStart, InputRichTextBox.Document.ContentEnd).Text;
        PlaceholderLabel.Visibility = string.IsNullOrWhiteSpace(text) 
            ? Visibility.Visible 
            : Visibility.Collapsed;
    }
    
    private void LoadTextFromFile()
    {
        if (File.Exists(FilePath))
        {
            try
            {
                using (FileStream stream = new FileStream(FilePath, FileMode.Open))
                {
                    // Пытаемся загрузить как RTF
                    TextRange range = new TextRange(InputRichTextBox.Document.ContentStart, InputRichTextBox.Document.ContentEnd);
                    try
                    {
                        range.Load(stream, System.Windows.DataFormats.Rtf);
                        ApplyImageSettings(InputRichTextBox.Document);
                        return;
                    }
                    catch
                    {
                        // Если не RTF, пробуем загрузить как XAML
                        stream.Seek(0, SeekOrigin.Begin);
                        FlowDocument document = XamlReader.Load(stream) as FlowDocument;
                        if (document != null)
                        {
                            InputRichTextBox.Document = document;
                            ApplyImageSettings(document);
                            return;
                        }
                        else
                        {
                            // Если не XAML, пробуем загрузить как текст
                            stream.Seek(0, SeekOrigin.Begin);
                            using (StreamReader reader = new StreamReader(stream))
                            {
                                string text = reader.ReadToEnd();
                                InputRichTextBox.Document.Blocks.Clear();
                                InputRichTextBox.Document.Blocks.Add(new Paragraph(new Run(text)));
                            }
                        }
                    }
                }
            }
            catch
            {
                // Если не удалось загрузить, пробуем загрузить как простой текст
                string text = File.ReadAllText(FilePath);
                InputRichTextBox.Document.Blocks.Clear();
                InputRichTextBox.Document.Blocks.Add(new Paragraph(new Run(text)));
            }
        }
    }
    
    private void ApplyImageSettings(FlowDocument document)
    {
        if (document == null) return;
        
        foreach (var block in document.Blocks)
        {
            ApplyImageSettingsToBlock(block);
        }
    }
    
    private void ApplyImageSettingsToBlock(Block block)
    {
        if (block is Paragraph paragraph)
        {
            foreach (var inline in paragraph.Inlines)
            {
                if (inline is InlineUIContainer container && container.Child is System.Windows.Controls.Image image)
                {
                    // Убираем ограничения для возможности ресайза
                    image.MaxWidth = double.PositiveInfinity;
                    image.MaxHeight = double.PositiveInfinity;
                    image.Stretch = System.Windows.Media.Stretch.None;
                    // Если Width/Height не заданы, устанавливаем исходные размеры
                    if (double.IsNaN(image.Width) && image.Source is BitmapSource bitmap)
                    {
                        image.Width = bitmap.PixelWidth;
                        image.Height = bitmap.PixelHeight;
                    }
                }
                else if (inline is Span span)
                {
                    // Рекурсивно проверяем вложенные Inlines
                    foreach (var childInline in span.Inlines)
                    {
                        if (childInline is InlineUIContainer childContainer && childContainer.Child is System.Windows.Controls.Image childImage)
                        {
                            // Убираем ограничения для возможности ресайза
                            childImage.MaxWidth = double.PositiveInfinity;
                            childImage.MaxHeight = double.PositiveInfinity;
                            childImage.Stretch = System.Windows.Media.Stretch.None;
                            // Если Width/Height не заданы, устанавливаем исходные размеры
                            if (double.IsNaN(childImage.Width) && childImage.Source is BitmapSource bitmap)
                            {
                                childImage.Width = bitmap.PixelWidth;
                                childImage.Height = bitmap.PixelHeight;
                            }
                        }
                    }
                }
            }
        }
        else if (block is Section section)
        {
            foreach (var childBlock in section.Blocks)
            {
                ApplyImageSettingsToBlock(childBlock);
            }
        }
        else if (block is List list)
        {
            foreach (var listItem in list.ListItems)
            {
                foreach (var childBlock in listItem.Blocks)
                {
                    ApplyImageSettingsToBlock(childBlock);
                }
            }
        }
        // Другие типы блоков можно добавить при необходимости
    }
    
    
    private void SaveDocumentToFile()
    {
        try
        {
            using (FileStream stream = new FileStream(FilePath, FileMode.Create))
            {
                // Сохраняем документ в формате XAML для сохранения всех свойств (включая размеры изображений)
                string xaml = System.Windows.Markup.XamlWriter.Save(InputRichTextBox.Document);
                using (StreamWriter writer = new StreamWriter(stream))
                {
                    writer.Write(xaml);
                }
            }
        }
        catch (Exception ex)
        {
            // Обработка ошибки сохранения
            System.Windows.MessageBox.Show($"Ошибка сохранения: {ex.Message}", "Ошибка", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }
    
    private void InputRichTextBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Находим элемент под курсором
        var hit = InputRichTextBox.InputHitTest(e.GetPosition(InputRichTextBox));
        if (hit is DependencyObject dependencyObject)
        {
            var image = FindParentImage(dependencyObject);
            if (image != null)
            {
                SelectImage(image);
                e.Handled = true;
                return;
            }
        }
        ClearSelection();
    }
    
    private System.Windows.Controls.Image FindParentImage(DependencyObject element)
    {
        while (element != null)
        {
            if (element is System.Windows.Controls.Image img)
                return img;
            // Используем LogicalTreeHelper, так как VisualTreeHelper не работает с FlowDocument
            element = LogicalTreeHelper.GetParent(element);
        }
        return null;
    }
    
    private void SelectImage(System.Windows.Controls.Image image)
    {
        if (selectedImage == image) return;
        
        ClearSelection();
        
        selectedImage = image;
        // Ресайз отключен, адорнер не добавляется
    }
    
    private void ClearSelection()
    {
        selectedImage = null;
    }
    
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
    
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    /// <summary>
    /// Событие закрепления окна ввода.
    /// </summary>
    private void PinnedButton_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        RefreshForegroundPinnedButtonByTopmost();
        Properties.Settings.Default.TopmostTextBoxWindow = Topmost;
    }

    /// <summary>
    /// Обновить цвет цвета кнопки относительно Topmost.
    /// </summary>
    private void RefreshForegroundPinnedButtonByTopmost()
    {
        PinnedButton.Foreground = Topmost 
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(62, 62, 66)) 
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255));
    }
}