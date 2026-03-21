using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MemoNotes.Models;
using MemoNotes.Properties;
using MemoNotes.Service.CheckerMousePosition;
using Application = System.Windows.Application;
using MousePosition = MemoNotes.Service.CheckerMousePosition.MousePosition;

namespace MemoNotes;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private NotifyIcon notifyIcon; // Иконка для системного трея
    private readonly double cornerMargin = 30; // Уменьшение порога в пикселях для правого верхнего угла
    private readonly DispatcherTimer checkMouseTimer;
    private PopupButtonWindow? popupButtonWindow;
    private readonly MousePositionFactory _mousePositionFactory;

    public MainWindow()
    {
        InitializeComponent();
        InitializeNotifyIcon();
        
        _mousePositionFactory = new MousePositionFactory();
        
        // Загружаем сохранённый угол активации
        LoadStartupCorner();

        // Скрываем главное окно
        Hide();

        // Настраиваем таймер для проверки позиции мыши
        checkMouseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        checkMouseTimer.Tick += CheckMousePosition;
        checkMouseTimer.Start();
    }

    /// <summary>
    /// Обработчик перетаскивания окна за заголовок.
    /// </summary>
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    /// <summary>
    /// Обработчик кнопки закрытия окна.
    /// </summary>
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void InitializeNotifyIcon()
    {
        notifyIcon = new NotifyIcon
        {
            Icon = new Icon("logo.ico"),
            Visible = true,
            Text = "Memo Notes"
        };
        
        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Открыть", null, ShowMainWindow);
        contextMenu.Items.Add("Закрыть", null, ExitApplication);
        notifyIcon.ContextMenuStrip = contextMenu;
        
        notifyIcon.DoubleClick += (s, e) => ShowMainWindow(s, e);
    }

    private void ShowMainWindow(object sender, EventArgs e)
    {
        Topmost = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication(object sender, EventArgs e)
    {
        Settings.Default.Save();
        notifyIcon.Dispose(); // Удаляем иконку из трея
        Application.Current.Shutdown(); // Закрываем приложение
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        Settings.Default.Save();
        e.Cancel = true;
        Hide();
    }

    /// <summary>
    /// Загружает сохранённый угол активации из настроек.
    /// </summary>
    private void LoadStartupCorner()
    {
        int savedCorner = Settings.Default.StartupCorner;
        CornerComboBox.SelectedIndex = savedCorner - 1; // Enum начинается с 1
        CornerComboBox.SelectionChanged += CornerComboBox_SelectionChanged;
    }

    /// <summary>
    /// Обработчик изменения выбранного угла в ComboBox.
    /// </summary>
    private void CornerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CornerComboBox.SelectedItem is ComboBoxItem { Tag: Enums.MousePosition corner })
        {
            Settings.Default.StartupCorner = (int)corner;
            Settings.Default.Save();
        }
    }

    private void CheckMousePosition(object? sender, EventArgs e)
    {
        System.Windows.Point mousePositionPoint = GetMousePosition();
        Enums.MousePosition selectedCorner = (Enums.MousePosition)Settings.Default.StartupCorner;
        MousePosition mousePosition = _mousePositionFactory.Create(selectedCorner);
        if (mousePosition.CursorIsInCorrectPlace(mousePositionPoint))
        {
            if (popupButtonWindow is not { IsVisible: true })
            {
                PopupWindowPositionPoint popupWindowPositionPoint = mousePosition.GetPopupWindowPositionPoint();
                popupButtonWindow = new PopupButtonWindow
                {
                    Left = popupWindowPositionPoint.Left,
                    Top = popupWindowPositionPoint.Top
                };
                popupButtonWindow.Show();
            }
        }
        else
        {
            // Скрываем окно с таймером, если курсор покидает угол
            popupButtonWindow?.Hide();
            popupButtonWindow?.TerminateRun();
            popupButtonWindow = null;
        }
    }

    /// <summary>
    /// Метод для получения позиции курсора
    /// </summary>
    /// <returns>Координаты указателя.</returns>
    private static System.Windows.Point GetMousePosition()
    {
        GetCursorPos(out Point lpPoint);
        return new System.Windows.Point(lpPoint.X, lpPoint.Y);
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }
}