using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace MemoNotes;

public partial class PopupButtonWindow
{
    private DispatcherTimer countdownTimer;
    private int countdownSeconds = 1500;

    public PopupButtonWindow()
    {
        InitializeComponent();
        
        countdownTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        countdownTimer.Tick += CountdownTimer_Tick;
        countdownTimer.Start();

        UpdateTimerText();
    }
    
    public void TerminateRun()
    {
        countdownTimer.Stop();
    }

    private void CountdownTimer_Tick(object? sender, EventArgs e)
    {
        countdownSeconds -= 500;

        if (countdownSeconds <= 0)
        {
            countdownTimer.Stop();
            OpenTextBoxWindow();
        }
        else
        {
            UpdateTimerText();
        }
    }

    private void UpdateTimerText()
    {
        timerTextBlock.Text = "...";
    }

    private void OpenTextBoxWindow()
    {
        TextBoxWindow? textBoxWindow = Application.Current.Windows
            .OfType<TextBoxWindow>()
            .FirstOrDefault();
        
        if (textBoxWindow == null)
        {
            var newTextBoxWindow = new TextBoxWindow
            {
                Owner = this
            };

            newTextBoxWindow.Show();
        }
        else
        {
            CenterWindowOnScreen(textBoxWindow);
            textBoxWindow.Activate();
            textBoxWindow.BlinkBorder();
        }
    }
    
    private void CenterWindowOnScreen(Window window)
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var screenHeight = SystemParameters.PrimaryScreenHeight;
        
        window.Left = (screenWidth - window.Width) / 2;
        window.Top = (screenHeight - window.Height) / 2;
    }
}