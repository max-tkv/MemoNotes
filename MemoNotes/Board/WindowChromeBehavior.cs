using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FontAwesome.WPF;
using MemoNotes.Service.Logging;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace MemoNotes.Board;

/// <summary>
/// Поведение окна: Win32 ресайз за углы/рёбра, максимизация, управление заголовком.
/// </summary>
public class WindowChromeBehavior
{
    private const int WM_NCHITTEST = 0x0084;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;
    private const int ResizeGripSize = 6;

    private readonly Window _window;
    private readonly System.Windows.Controls.Button _maximizeButton;
    private HwndSource? _hwndSource;

    private Rect _normalBounds;
    private bool _isMaximized;

    /// <summary>Callback при закрытии окна.</summary>
    public Action? OnWindowClosing { get; set; }

    public WindowChromeBehavior(Window window, System.Windows.Controls.Button maximizeButton)
    {
        _window = window;
        _maximizeButton = maximizeButton;
    }

    public void Initialize()
    {
        _window.SourceInitialized += OnSourceInitialized;
        _window.Closing += OnClosing;
        Logger.Info<WindowChromeBehavior>("WindowChromeBehavior инициализирован");
    }

    /// <summary>Обработчик клика по заголовку — DragMove.</summary>
    public void TitleBarMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            if (e.OriginalSource is FrameworkElement fe && fe is not System.Windows.Controls.TextBlock)
            {
                var parent = fe;
                while (parent != null)
                {
                    if (parent is System.Windows.Controls.Button)
                        return;
                    parent = VisualTreeHelper.GetParent(parent) as FrameworkElement;
                }
            }

            _window.DragMove();
        }
    }

    /// <summary>Кнопка «Развернуть/Восстановить».</summary>
    public void MaximizeButton_Click()
    {
        if (_isMaximized)
        {
            _window.Left = _normalBounds.X;
            _window.Top = _normalBounds.Y;
            _window.Width = _normalBounds.Width;
            _window.Height = _normalBounds.Height;
            _maximizeButton.Content = new ImageAwesome { Icon = FontAwesomeIcon.Expand, Width = 14, Height = 14, Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)) };
            _maximizeButton.ToolTip = "Развернуть во весь экран";
            _isMaximized = false;
        }
        else
        {
            _normalBounds = new Rect(_window.Left, _window.Top, _window.Width, _window.Height);
            _window.Left = 0;
            _window.Top = 0;
            _window.Width = SystemParameters.PrimaryScreenWidth;
            _window.Height = SystemParameters.PrimaryScreenHeight;
            _maximizeButton.Content = new ImageAwesome { Icon = FontAwesomeIcon.Compress, Width = 14, Height = 14, Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)) };
            _maximizeButton.ToolTip = "Восстановить размер";
            _isMaximized = true;
        }
    }

    private void OnSourceInitialized(object sender, EventArgs e)
    {
        _hwndSource = PresentationSource.FromVisual(_window) as HwndSource;
        if (_hwndSource != null)
        {
            _hwndSource.AddHook(WndProcHook);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        OnWindowClosing?.Invoke();
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST)
        {
            int x = (short)((lParam.ToInt64() >> 0) & 0xFFFF);
            int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);

            var point = _window.PointFromScreen(new Point(x, y));

            double w = _window.ActualWidth;
            double h = _window.ActualHeight;
            int grip = ResizeGripSize;

            bool isTop = point.Y < grip;
            bool isBottom = point.Y > h - grip;
            bool isLeft = point.X < grip;
            bool isRight = point.X > w - grip;

            if (isTop && isLeft)
            {
                handled = true;
                return (IntPtr)HTTOPLEFT;
            }
            if (isTop && isRight)
            {
                handled = true;
                return (IntPtr)HTTOPRIGHT;
            }
            if (isBottom && isLeft)
            {
                handled = true;
                return (IntPtr)HTBOTTOMLEFT;
            }
            if (isBottom && isRight)
            {
                handled = true;
                return (IntPtr)HTBOTTOMRIGHT;
            }
            if (isTop)
            {
                handled = true;
                return (IntPtr)HTTOP;
            }
            if (isBottom)
            {
                handled = true;
                return (IntPtr)HTBOTTOM;
            }
            if (isLeft)
            {
                handled = true;
                return (IntPtr)HTLEFT;
            }
            if (isRight)
            {
                handled = true;
                return (IntPtr)HTRIGHT;
            }
        }

        return IntPtr.Zero;
    }
}
