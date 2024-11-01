using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Color = System.Drawing.Color;

namespace MemoNotes;

public partial class TextBoxWindow
{
    private const string FilePath = "Notes.txt";

    public TextBoxWindow()
    {
        InitializeComponent();
        
        LoadTextFromFile();
        
        // Восстанавливаем размеры окна при запуске
        Width = Properties.Settings.Default.WindowWidth;
        Height = Properties.Settings.Default.WindowHeight;

        Topmost = Properties.Settings.Default.TopmostTextBoxWindow;
        ApplyForegroundPinnedButtonByTopmost();
        
        // Подписываемся на события
        InputTextBox.GotFocus += InputTextBox_GotFocus;
        InputTextBox.LostFocus += InputTextBox_LostFocus;
        InputTextBox.TextChanged += InputTextBox_TextChanged;

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

    private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePlaceholder();
    }

    private void InputTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        UpdatePlaceholder();
    }

    private void InputTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        UpdatePlaceholder();
    }

    private void UpdatePlaceholder()
    {
        PlaceholderLabel.Visibility = string.IsNullOrWhiteSpace(InputTextBox.Text) 
            ? Visibility.Visible 
            : Visibility.Collapsed;
    }
    
    private void LoadTextFromFile()
    {
        if (File.Exists(FilePath))
        {
            InputTextBox.Text = File.ReadAllText(FilePath);
        }
    }
    
    private void MainTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SaveTextToFile(InputTextBox.Text); 
    }
    
    private void SaveTextToFile(string text)
    {
        File.WriteAllText(FilePath, text);
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

    private void PinnedButton_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        ApplyForegroundPinnedButtonByTopmost();
        Properties.Settings.Default.TopmostTextBoxWindow = Topmost;
    }

    private void ApplyForegroundPinnedButtonByTopmost()
    {
        PinnedButton.Foreground = Topmost 
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(62, 62, 66)) 
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255));
    }
}