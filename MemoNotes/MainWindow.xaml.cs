using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using MemoNotes.Enums;
using MemoNotes.Models;
using MemoNotes.Properties;
using MemoNotes.Service.CheckerMousePosition;
using MemoNotes.Service.CloudSync;
using MemoNotes.Service.Logging;
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
    private bool _isCapturingKey; // Флаг: TextBox в режиме захвата клавиши
    private HwndSource? _hwndSource;

    // Win32 сообщения для перехвата клавиш
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12; // Alt


    public MainWindow()
    {
        InitializeComponent();
        InitializeNotifyIcon();
        
        _mousePositionFactory = new MousePositionFactory();
        
        // Загружаем сохранённый угол активации
        LoadStartupCorner();
        LoadContinuousStrokeKey();
        LoadCloudSettings();

        // Инициализируем менеджер облачной синхронизации
        CloudSyncManager.Initialize();

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
        _isCapturingKey = false;
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
        contextMenu.Items.Add("Настройки", null, ShowMainWindow);
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

    /// <summary>
    /// Инициализация Win32 хука после создания окна (перехват WM_KEYDOWN / WM_SYSKEYDOWN).
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        if (_hwndSource != null)
        {
            _hwndSource.AddHook(WndProcHook);
        }
    }

    /// <summary>
    /// Win32 WndProc хук — перехватывает WM_KEYDOWN и WM_SYSKEYDOWN до WPF.
    /// Это единственный надёжный способ перехватить Alt+key в WPF.
    /// </summary>
    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_isCapturingKey && (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN))
        {
            int vk = wParam.ToInt32();

            // Escape — сбросить
            if (vk == 0x1B)
            {
                Dispatcher.Invoke(() =>
                {
                    Settings.Default.ContinuousStrokeKey = 0;
                    Settings.Default.ContinuousStrokeModifier = 0;
                    Settings.Default.Save();
                    ContinuousStrokeKeyTextBox.Text = "Нет";
                });
                _isCapturingKey = false;
                handled = true;
                return IntPtr.Zero;
            }

            // Пропускаем чистые модификаторы
            if (vk == VK_SHIFT || vk == VK_CONTROL || vk == VK_MENU)
            {
                handled = true;
                return IntPtr.Zero;
            }

            // Вычисляем модификатор по состоянию клавиш через Win32
            int modifier = 0;
            short shiftState = GetAsyncKeyState(VK_SHIFT);
            short ctrlState = GetAsyncKeyState(VK_CONTROL);
            short altState = GetAsyncKeyState(VK_MENU);
            if ((shiftState & 0x8000) != 0) modifier |= 1;
            if ((ctrlState & 0x8000) != 0) modifier |= 2;
            if ((altState & 0x8000) != 0) modifier |= 4;

            // Для Alt-комбинаций vk будет кодом основной клавиши (не VK_MENU)
            // Для Ctrl — тоже код основной клавиши
            int finalVk = vk;
            // Если vk это модификатор (не должен быть, но на всякий случай)
            if (finalVk == VK_SHIFT || finalVk == VK_CONTROL || finalVk == VK_MENU)
                return IntPtr.Zero;

            Settings.Default.ContinuousStrokeKey = finalVk;
            Settings.Default.ContinuousStrokeModifier = modifier;
            Settings.Default.Save();

            Dispatcher.Invoke(() =>
            {
                ContinuousStrokeKeyTextBox.Text = FormatKeyCombo(modifier, finalVk);
            });

            _isCapturingKey = false;
            handled = true;
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Загружает сохранённую клавишу непрерывного рисования.
    /// </summary>
    private void LoadContinuousStrokeKey()
    {
        int savedKey = Settings.Default.ContinuousStrokeKey;
        int savedMod = Settings.Default.ContinuousStrokeModifier;
        ContinuousStrokeKeyTextBox.Text = savedKey == 0 ? "Нет" : FormatKeyCombo(savedMod, savedKey);
    }

    /// <summary>
    /// Клик по TextBox — активируем режим захвата клавиши.
    /// Используем PreviewMouseDown, т.к. GotFocus не срабатывает повторно.
    /// </summary>
    private void ContinuousStrokeKeyTextBox_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _isCapturingKey = true;
        ContinuousStrokeKeyTextBox.Focus();
    }

    /// <summary>
    /// Потеря фокуса TextBox — сбрасываем режим захвата.
    /// </summary>
    private void ContinuousStrokeKeyTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _isCapturingKey = false;
    }

    /// <summary>
    /// Сброс клавиши непрерывного рисования.
    /// </summary>
    private void ResetContinuousStrokeKey_Click(object sender, RoutedEventArgs e)
    {
        Settings.Default.ContinuousStrokeKey = 0;
        Settings.Default.ContinuousStrokeModifier = 0;
        Settings.Default.Save();
        ContinuousStrokeKeyTextBox.Text = "Нет";
    }

    #region Облачная синхронизация

    private void LoadCloudSettings()
    {
        CloudProviderComboBox.SelectionChanged += CloudProviderComboBox_SelectionChanged;
        CloudSyncManager.TokenRevoked += OnTokenRevoked;

        var provider = (CloudProvider)Settings.Default.CloudProvider;
        CloudProviderComboBox.SelectedIndex = (int)provider;

        UpdateCloudUI((CloudProvider)Settings.Default.CloudProvider);

        if (CloudSyncManager.IsEnabled)
        {
            SetCloudStatus(true, "Подключено");
            CloudAuthButton.Visibility = Visibility.Collapsed;
            CloudLogoutButton.Visibility = Visibility.Visible;
        }
    }

    private void OnTokenRevoked(string? message)
    {
        // Вызывается через Dispatcher из CloudSyncManager.HandleTokenRevoked
        SetCloudStatus(false, message ?? "Токен отозван");
        CloudAuthButton.Visibility = Visibility.Visible;
        CloudLogoutButton.Visibility = Visibility.Collapsed;
    }

    private void UpdateCloudUI(CloudProvider provider)
    {
        if (CloudHintText == null) return;
        
        var isCloudEnabled = provider != CloudProvider.None;
        
        CloudHintText.Visibility = isCloudEnabled ? Visibility.Visible : Visibility.Collapsed;
        CloudAuthPanel.Visibility = isCloudEnabled ? Visibility.Visible : Visibility.Collapsed;
        CloudStatusPanel.Visibility = isCloudEnabled ? Visibility.Visible : Visibility.Collapsed;
        
        if (isCloudEnabled && provider == CloudProvider.YandexDisk)
            CloudHintText.Text = "Данные автоматически сохраняются на Яндекс Диск при каждом изменении доски";

        // Скрываем кнопку авторизации, если уже подключены
        if (CloudSyncManager.IsEnabled)
        {
            CloudAuthButton.Visibility = Visibility.Collapsed;
            CloudLogoutButton.Visibility = Visibility.Visible;
        }
    }

    private void CloudProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || CloudProviderComboBox == null) return;
        if (CloudProviderComboBox.SelectedItem is not ComboBoxItem item) return;
        
        if (!int.TryParse(item.Tag?.ToString(), out var providerValue)) return;
        var provider = (CloudProvider)providerValue;
        
        Settings.Default.CloudProvider = providerValue;
        Settings.Default.Save();
        
        UpdateCloudUI(provider);
        CloudSyncManager.Reinitialize();
        
        if (provider == CloudProvider.None)
        {
            SetCloudStatus(false, "Синхронизация отключена");
            CloudAuthButton.Visibility = Visibility.Visible;
            CloudLogoutButton.Visibility = Visibility.Collapsed;
        }
        else if (!CloudSyncManager.IsEnabled)
        {
            SetCloudStatus(false, "Нажмите «Авторизоваться» для подключения");
            CloudAuthButton.Visibility = Visibility.Visible;
            CloudLogoutButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            SetCloudStatus(true, "Подключено");
            CloudAuthButton.Visibility = Visibility.Collapsed;
            CloudLogoutButton.Visibility = Visibility.Visible;
        }
    }

    private async void CloudAuthButton_Click(object sender, RoutedEventArgs e)
    {
        SetCloudStatus(null, "Авторизация...");
        CloudAuthButton.IsEnabled = false;

        try
        {
            var success = await CloudSyncManager.AuthorizeAsync(this);
             
            if (success)
            {
                SetCloudStatus(true, "Подключено");
                CloudAuthButton.Visibility = Visibility.Collapsed;
                CloudLogoutButton.Visibility = Visibility.Visible;
            }
            else
            {
                SetCloudStatus(false, "Авторизация отменена");
            }
        }
        catch (Exception ex)
        {
            SetCloudStatus(false, $"Ошибка: {ex.Message}");
        }
        finally
        {
            CloudAuthButton.IsEnabled = true;
        }
    }

    private void CloudLogoutButton_Click(object sender, RoutedEventArgs e)
    {
        CloudSyncManager.Logout();
        SetCloudStatus(false, "Не подключено");
        CloudAuthButton.Visibility = Visibility.Visible;
        CloudLogoutButton.Visibility = Visibility.Collapsed;
    }

    private void SetCloudStatus(bool? isConnected, string text)
    {
        Dispatcher.Invoke(() =>
        {
            if (isConnected == true)
                CloudStatusDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4c, 0xaf, 0x50));
            else if (isConnected == false)
                CloudStatusDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xf4, 0x43, 0x36));
            else
                CloudStatusDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xff, 0xb3, 0x00));

            CloudStatusText.Text = text;
        });
    }

    #endregion

    /// <summary>
    /// Форматировать сочетание клавиш в читаемую строку.
    /// </summary>
    private static string FormatKeyCombo(int modifier, int vkCode)
    {
        var parts = new List<string>();
        if ((modifier & 1) != 0) parts.Add("Shift");
        if ((modifier & 2) != 0) parts.Add("Ctrl");
        if ((modifier & 4) != 0) parts.Add("Alt");

        try
        {
            var key = KeyInterop.KeyFromVirtualKey(vkCode);
            parts.Add(key.ToString());
        }
        catch
        {
            parts.Add($"VK({vkCode})");
        }

        return string.Join(" + ", parts);
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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }
}
