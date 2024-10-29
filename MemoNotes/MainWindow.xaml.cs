using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Point = System.Windows.Point;

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

    public MainWindow()
    {
        InitializeComponent();
        InitializeNotifyIcon();
        
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
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication(object sender, EventArgs e)
    {
        notifyIcon.Dispose(); // Удаляем иконку из трея
        Application.Current.Shutdown(); // Закрываем приложение
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        e.Cancel = true; // Отменяем закрытие окна
        Hide(); // Скрываем окно
    }

    private void CheckMousePosition(object? sender, EventArgs e)
    {
        var mousePosition = GetMousePosition();
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        
        if (mousePosition.X >= screenWidth - cornerMargin && mousePosition.Y <= cornerMargin)
        {
            if (popupButtonWindow == null || !popupButtonWindow.IsVisible)
            {
                popupButtonWindow = new PopupButtonWindow
                {
                    Left = mousePosition.X - 80, // Позиционируем окно на 80px левее от курсора
                    Top = mousePosition.Y // Позиционируем окно по вертикали на уровне курсора
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

    // Метод для получения позиции курсора
    private static Point GetMousePosition()
    {
        GetCursorPos(out POINT lpPoint);
        return new Point(lpPoint.X, lpPoint.Y);
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}