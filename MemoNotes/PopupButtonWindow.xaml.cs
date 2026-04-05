using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace MemoNotes;

public partial class PopupButtonWindow
{
    private DispatcherTimer countdownTimer;
    private int countdownRemaining;
    private readonly int countdownTotal = 1500;
    private readonly int tickInterval = 30;
    
    /// <summary>
    /// Длина окружности для прогресс-бара (36 * π ≈ 113.1).
    /// </summary>
    private const double Circumference = 113.1;

    public PopupButtonWindow()
    {
        InitializeComponent();
        
        // Начальное состояние — скрыто
        RootGrid.Opacity = 0;
        RootScale.ScaleX = 0;
        RootScale.ScaleY = 0;
        ProgressRing.StrokeDashOffset = Circumference;
        
        countdownTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(tickInterval)
        };
        countdownTimer.Tick += CountdownTimer_Tick;
    }

    /// <summary>
    /// Запуск анимации появления и обратного отсчёта.
    /// </summary>
    public void StartRun()
    {
        countdownRemaining = countdownTotal;
        countdownTimer.Start();
        
        PlayAppearAnimation();
        AnimateProgressRing(countdownTotal);
    }
    
    public void TerminateRun()
    {
        countdownTimer.Stop();
    }

    private void CountdownTimer_Tick(object? sender, EventArgs e)
    {
        countdownRemaining -= tickInterval;

        if (countdownRemaining <= 0)
        {
            countdownTimer.Stop();
            PlayDisappearAnimation(() =>
            {
                OpenTextBoxWindow();
            });
        }
    }

    private void PlayAppearAnimation()
    {
        var duration = TimeSpan.FromMilliseconds(250);
        
        // Scale animation (bounce effect)
        var scaleXAnim = new DoubleAnimationUsingKeyFrames
        {
            Duration = duration
        };
        scaleXAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        scaleXAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.15, KeyTime.FromPercent(0.7)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        scaleXAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        
        var scaleYAnim = new DoubleAnimationUsingKeyFrames
        {
            Duration = duration
        };
        scaleYAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        scaleYAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.15, KeyTime.FromPercent(0.7)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        scaleYAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);
        
        // Fade in
        var fadeInAnim = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        RootGrid.BeginAnimation(OpacityProperty, fadeInAnim);
    }

    private void PlayDisappearAnimation(Action? onCompleted = null)
    {
        var duration = TimeSpan.FromMilliseconds(150);
        
        // Scale down
        var scaleAnim = new DoubleAnimation
        {
            To = 0,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        
        // Fade out
        var fadeOutAnim = new DoubleAnimation
        {
            To = 0,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        
        if (onCompleted != null)
        {
            fadeOutAnim.Completed += (_, _) => onCompleted();
        }
        
        RootGrid.BeginAnimation(OpacityProperty, fadeOutAnim);
    }

    private void AnimateProgressRing(int durationMs)
    {
        var animation = new DoubleAnimation
        {
            From = Circumference,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        
        ProgressRing.BeginAnimation(Shape.StrokeDashOffsetProperty, animation);
    }

    private void OpenTextBoxWindow()
    {
        BoardWindow? boardWindow = Application.Current.Windows
            .OfType<BoardWindow>()
            .FirstOrDefault();
        
        if (boardWindow == null)
        {
            var newBoardWindow = new BoardWindow();
            newBoardWindow.Show();
        }
        else
        {
            CenterWindowOnScreen(boardWindow);
            boardWindow.Activate();
            boardWindow.BlinkBorder();
        }
    }
    
    private void CenterWindowOnScreen(Window window)
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var screenHeight = SystemParameters.PrimaryScreenHeight;
        
        window.Left = (screenWidth - window.Width) / 2;
        window.Top = (screenHeight - window.Height) / 2;
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        StartRun();
    }
}
