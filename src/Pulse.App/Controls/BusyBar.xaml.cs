using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Pulse.App.Controls;

/// <summary>
/// Her uzun işlemde aynı görünür ilerleme: ince çubuk (ölçülebiliyorsa dolar, değilse kayar), çalışan adımın adı,
/// "3/9" sayacı ve geçen süre. IsActive false iken tamamen gizlenir.
/// </summary>
public partial class BusyBar : UserControl
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(BusyBar), new PropertyMetadata(false, (d, _) => ((BusyBar)d).Refresh()));

    /// <summary>0..1 arası ilerleme; eksi bir değer "ölçülemiyor" demektir ve çubuk kayar.</summary>
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(BusyBar), new PropertyMetadata(-1.0, (d, _) => ((BusyBar)d).Refresh()));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(BusyBar), new PropertyMetadata("", (d, e) => ((BusyBar)d).Label.Text = (string)e.NewValue));

    /// <summary>Sağdaki sayaç, örn. "3/9".</summary>
    public static readonly DependencyProperty CounterProperty = DependencyProperty.Register(
        nameof(Counter), typeof(string), typeof(BusyBar), new PropertyMetadata("", (d, _) => ((BusyBar)d).UpdateRight()));

    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Stopwatch _elapsed = new();
    private bool _sliding;

    public BusyBar()
    {
        InitializeComponent();
        _clock.Tick += (_, _) => UpdateRight();
    }

    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public double Fraction { get => (double)GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Counter { get => (string)GetValue(CounterProperty); set => SetValue(CounterProperty, value); }

    private void OnTrackSizeChanged(object sender, SizeChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (!IsActive)
        {
            StopSlide();
            _clock.Stop();
            _elapsed.Reset();
            Visibility = Visibility.Collapsed;
            return;
        }

        if (Visibility != Visibility.Visible)
        {
            Visibility = Visibility.Visible;
            _elapsed.Restart();
            _clock.Start();
        }

        var width = Track.ActualWidth;
        if (width <= 0) return;

        if (Fraction < 0)
        {
            if (_sliding) return;
            _sliding = true;
            Fill.BeginAnimation(WidthProperty, null);
            Fill.Width = width * 0.28;
            var anim = new DoubleAnimation(-width * 0.28, width, TimeSpan.FromMilliseconds(1100))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Slide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, anim);
        }
        else
        {
            StopSlide();
            var target = width * Math.Clamp(Fraction, 0, 1);
            Fill.BeginAnimation(WidthProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(220)));
        }
    }

    private void StopSlide()
    {
        if (!_sliding) return;
        _sliding = false;
        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        Slide.X = 0;
        Fill.BeginAnimation(WidthProperty, null);
        Fill.Width = 0;
    }

    private void UpdateRight()
    {
        var t = _elapsed.Elapsed;
        var time = $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        Right.Text = string.IsNullOrEmpty(Counter) ? time : $"{Counter}  ·  {time}";
    }
}
