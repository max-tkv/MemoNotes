using System.Windows;
using System.Windows.Threading;

namespace MemoNotes;

public partial class PopupButtonWindow
{
    private DispatcherTimer countdownTimer;
    private int countdownSeconds = 1000;
    private TextBoxWindow textBoxWindow;

    public PopupButtonWindow()
    {
        InitializeComponent();
        
        countdownTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1000)
        };
        countdownTimer.Tick += CountdownTimer_Tick;
        countdownTimer.Start();

        UpdateTimerText();
    }

    private void CountdownTimer_Tick(object sender, EventArgs e)
    {
        countdownSeconds -= 1000;

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
        if (textBoxWindow == null || !textBoxWindow.IsVisible)
        {
            textBoxWindow = new TextBoxWindow();
            textBoxWindow.Owner = this;
            
            textBoxWindow.Show();
            textBoxWindow.Closed += (_, _) => textBoxWindow = null;
        }
        else
        {
            textBoxWindow.Activate();
        }
    }
}