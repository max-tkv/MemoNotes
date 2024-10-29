using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MemoNotes;

public partial class TextBoxWindow
{
    private readonly string filePath = "Notes.txt";
    
    public TextBoxWindow()
    {
        InitializeComponent();
        
        LoadTextFromFile();
        
        // Восстанавливаем размеры окна при запуске
        Width = Properties.Settings.Default.WindowWidth;
        Height = Properties.Settings.Default.WindowHeight;
        
        // Подписываемся на события
        InputTextBox.GotFocus += InputTextBox_GotFocus;
        InputTextBox.LostFocus += InputTextBox_LostFocus;
        InputTextBox.TextChanged += InputTextBox_TextChanged;

        // Инициализируем состояние заполнителя
        UpdatePlaceholder();
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
        if (File.Exists(filePath))
        {
            InputTextBox.Text = File.ReadAllText(filePath);
        }
    }
    
    private void MainTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SaveTextToFile(InputTextBox.Text); 
    }
    
    private void SaveTextToFile(string text)
    {
        File.WriteAllText(filePath, text);
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
}