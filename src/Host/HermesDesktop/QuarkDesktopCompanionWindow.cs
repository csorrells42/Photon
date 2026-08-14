using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace HermesDesktop;

internal sealed class QuarkDesktopCompanionWindow : Window, IDisposable
{
    internal const int ProtocolVersion = 1;
    private static readonly string PositionPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "hermes", "quark-position.json");

    private readonly Action<object> _postMessage;
    private readonly TextBlock _status;
    private readonly TextBlock _face;
    private readonly Button _talk;
    private readonly Canvas _stage;
    private readonly TranslateTransform _stageMotion = new();
    private readonly RotateTransform _stageTilt = new();
    private readonly DispatcherTimer _motionTimer;
    private int _reactionFrame;
    private bool _allowClose;
    private bool _active;
    private string _phase = "idle";

    internal QuarkDesktopCompanionWindow(Action<object> postMessage)
    {
        _postMessage = postMessage;
        Width = 178;
        Height = 142;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(92) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _stage = new Canvas { Width = 92, Height = 88, Cursor = Cursors.SizeAll, ToolTip = "Drag Quark anywhere on your desktop" };
        _stage.RenderTransformOrigin = new Point(0.5, 0.82);
        var stageTransforms = new TransformGroup();
        stageTransforms.Children.Add(_stageTilt);
        stageTransforms.Children.Add(_stageMotion);
        _stage.RenderTransform = stageTransforms;
        _stage.MouseLeftButtonDown += (_, eventArgs) =>
        {
            if (eventArgs.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch (InvalidOperationException) { }
        };
        _stage.MouseLeftButtonUp += (_, _) => SavePosition();

        AddEllipse(_stage, 18, 74, 58, 10, Color.FromArgb(85, 0, 0, 0));
        AddRounded(_stage, 30, 50, 38, 31, 8, "#6954B4", "#332B48", 3);
        AddRounded(_stage, 20, 17, 58, 48, 13, "#D8C9A8", "#332B48", 4);
        AddRounded(_stage, 13, 29, 12, 24, 5, "#8C7A61", "#332B48", 3);
        AddRounded(_stage, 73, 29, 12, 24, 5, "#8C7A61", "#332B48", 3);
        AddRounded(_stage, 40, 2, 5, 20, 2, "#8F7BDD", "#443957", 1);
        AddRounded(_stage, 25, 78, 22, 8, 4, "#9E896D", "#332B48", 2);
        AddRounded(_stage, 55, 78, 22, 8, 4, "#9E896D", "#332B48", 2);
        AddEllipse(_stage, 34, 34, 6, 8, Color.FromRgb(54, 44, 65));
        AddEllipse(_stage, 58, 34, 6, 8, Color.FromRgb(54, 44, 65));
        AddRounded(_stage, 42, 48, 14, 4, 2, "#564256", null, 0);
        AddEllipse(_stage, 26, 47, 9, 5, Color.FromRgb(219, 140, 146));
        AddEllipse(_stage, 64, 47, 9, 5, Color.FromRgb(219, 140, 146));
        AddRounded(_stage, 45, 59, 8, 12, 2, "#79E3D0", null, 0);

        _face = new TextBlock
        {
            Text = "",
            Foreground = new SolidColorBrush(Color.FromRgb(169, 151, 245)),
            FontWeight = FontWeights.Bold,
            FontSize = 15,
        };
        Canvas.SetLeft(_face, 76);
        Canvas.SetTop(_face, 13);
        _stage.Children.Add(_face);
        Grid.SetRow(_stage, 0);
        root.Children.Add(_stage);

        var controls = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(230, 19, 21, 31)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(67, 63, 87)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(5, 3, 5, 3),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _talk = Button("🎙", "Talk with Quark");
        _talk.Click += (_, _) => PostAction(_active && _phase is "thinking" or "speaking" ? "stop" : "talk");
        row.Children.Add(_talk);
        _status = new TextBlock
        {
            Text = "Talk with Quark",
            Foreground = new SolidColorBrush(Color.FromRgb(188, 184, 207)),
            FontSize = 10,
            Margin = new Thickness(5, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 100,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        row.Children.Add(_status);
        var exit = Button("×", "Exit conversation mode");
        exit.Click += (_, _) => PostAction("exit");
        row.Children.Add(exit);
        controls.Child = row;
        Grid.SetRow(controls, 1);
        root.Children.Add(controls);
        Content = root;

        RestorePosition();
        LocationChanged += (_, _) => SavePosition();
        Closing += (_, args) => { if (!_allowClose) { args.Cancel = true; Hide(); } };
        // This is deliberately a one-shot timer. Quark used to restart WPF
        // animation clocks every 720 ms forever, including 0 -> 0 "idle"
        // animations. A topmost window doing that keeps DWM compositing even
        // when Quark appears still. Reactions are now sparse, bounded events.
        _motionTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle);
        _motionTimer.Tick += (_, _) =>
        {
            _motionTimer.Stop();
            AnimateNextReaction();
        };
        ScheduleNextMotion(initial: true);
    }

    internal void Sync(bool active, string? phase, string? reason, bool reviewActive)
    {
        _active = active;
        _phase = phase is "idle" or "paused" or "listening" or "thinking" or "speaking" or "error" ? phase : "idle";
        _status.Text = _phase switch
        {
            "listening" => "Listening…",
            "thinking" => "Thinking…",
            "speaking" => "Talking…",
            "error" => string.IsNullOrWhiteSpace(reason) ? "Audio needs attention" : reason,
            "paused" => "Paused",
            _ => active ? "Ready to chat" : "Talk with Quark",
        };
        _face.Text = _phase switch { "thinking" => "…", "speaking" => "))", "error" => "!", _ => "" };
        _talk.Content = _phase is "thinking" or "speaking" ? "■" : "🎙";
        _talk.ToolTip = _phase is "thinking" or "speaking" ? "Stop Quark" : "Talk with Quark";
        _reactionFrame = 0;
        Topmost = !reviewActive;
        if (!IsVisible) Show();
        AnimateStateOnce();
        ScheduleNextMotion(initial: false);
    }

    private void AnimateNextReaction()
    {
        if (!IsVisible || _stage.IsMouseCaptured)
        {
            ScheduleNextMotion(initial: false);
            return;
        }

        switch (_phase)
        {
            case "listening":
                AnimatePose(_reactionFrame++ % 2 == 0 ? -2 : 2, -3, 1.08, 260);
                break;
            case "thinking":
                AnimatePose(_reactionFrame++ % 3 - 1, _reactionFrame % 2 == 0 ? -2 : 0, _reactionFrame % 2 == 0 ? -4 : 4, 390);
                break;
            case "speaking":
                AnimatePose(_reactionFrame++ % 2 == 0 ? -3 : 3, -5, 0, 220);
                break;
            case "error":
                AnimatePose(_reactionFrame++ % 2 == 0 ? -5 : 5, 0, _reactionFrame % 2 == 0 ? -3 : 3, 160);
                break;
            case "paused":
                AnimatePose(0, 1, -2, 520);
                break;
            default:
                AnimateIdleGesture();
                break;
        }

        ScheduleNextMotion(initial: false);
    }

    private void AnimateStateOnce()
    {
        if (!IsVisible || _stage.IsMouseCaptured) return;
        switch (_phase)
        {
            case "listening": AnimatePose(0, -3, 0, 240); break;
            case "thinking": AnimatePose(2, -1, 5, 360); break;
            case "speaking": AnimatePose(0, -5, 0, 210); break;
            case "error": AnimatePose(-5, 0, -3, 160); break;
            case "paused": AnimatePose(0, 1, -2, 420); break;
        }
    }

    private void AnimateIdleGesture()
    {
        switch (Random.Shared.Next(5))
        {
            case 0: // A small hello hop.
                _face.Text = "!";
                AnimatePose(0, -9, 0, 220);
                ClearIdleFaceLater();
                break;
            case 1: // Curious head tilt.
                _face.Text = "?";
                AnimatePose(2, -1, 8, 430);
                ClearIdleFaceLater();
                break;
            case 2: // Look toward the work and lean in.
                AnimatePose(-5, -2, -5, 380);
                break;
            case 3: // Tiny victory bounce.
                _face.Text = "*";
                AnimatePose(0, -6, -7, 190);
                ClearIdleFaceLater();
                break;
            default: // Contented sway.
                AnimatePose(4, -1, 5, 520);
                break;
        }
    }

    private void ScheduleNextMotion(bool initial)
    {
        _motionTimer.Stop();
        // Active conversation phases stay expressive without becoming a
        // permanent render loop. Idle gestures are intentionally infrequent.
        var delay = _phase == "idle"
            ? (initial ? Random.Shared.Next(8, 16) : Random.Shared.Next(18, 46))
            : Random.Shared.Next(2, 5);
        _motionTimer.Interval = TimeSpan.FromSeconds(delay);
        _motionTimer.Start();
    }

    private void AnimatePose(double x, double y, double angle, int milliseconds)
    {
        if (x == 0 && y == 0 && angle == 0) return;
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        _stageMotion.BeginAnimation(TranslateTransform.XProperty, BoundedAnimation(x, duration));
        _stageMotion.BeginAnimation(TranslateTransform.YProperty, BoundedAnimation(y, duration));
        _stageTilt.BeginAnimation(RotateTransform.AngleProperty, BoundedAnimation(angle, duration));
    }

    private static DoubleAnimation BoundedAnimation(double value, TimeSpan duration) => new(value, duration)
    {
        AutoReverse = true,
        FillBehavior = FillBehavior.Stop,
    };

    private async void ClearIdleFaceLater()
    {
        await Task.Delay(900);
        if (_phase == "idle") _face.Text = "";
    }

    internal void Recall(double anchorLeft, double anchorTop, double anchorWidth, double anchorHeight)
    {
        var left = anchorLeft + Math.Max(24, anchorWidth - Width - 32);
        var top = anchorTop + Math.Max(72, Math.Min(anchorHeight - Height - 32, 132));
        Left = Clamp(left, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width);
        Top = Clamp(top, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height);
        if (!IsVisible) Show();
        Topmost = false;
        Topmost = true;
        SavePosition();
    }

    private void PostAction(string action) => _postMessage(new
    {
        type = "quark.companion.action",
        version = ProtocolVersion,
        action,
    });

    private static Button Button(string text, string tooltip) => new()
    {
        Content = text,
        ToolTip = tooltip,
        Width = 25,
        Height = 23,
        Padding = new Thickness(0),
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        Foreground = new SolidColorBrush(Color.FromRgb(174, 158, 246)),
        Cursor = Cursors.Hand,
        Focusable = false,
    };

    private static void AddRounded(Canvas canvas, double left, double top, double width, double height, double radius, string fill, string? stroke, double thickness)
    {
        var shape = new Rectangle
        {
            Width = width,
            Height = height,
            RadiusX = radius,
            RadiusY = radius,
            Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fill)),
            Stroke = stroke is null ? null : new SolidColorBrush((Color)ColorConverter.ConvertFromString(stroke)),
            StrokeThickness = thickness,
        };
        Canvas.SetLeft(shape, left); Canvas.SetTop(shape, top); canvas.Children.Add(shape);
    }

    private static void AddEllipse(Canvas canvas, double left, double top, double width, double height, Color fill)
    {
        var shape = new Ellipse { Width = width, Height = height, Fill = new SolidColorBrush(fill) };
        Canvas.SetLeft(shape, left); Canvas.SetTop(shape, top); canvas.Children.Add(shape);
    }

    private void RestorePosition()
    {
        try
        {
            if (File.Exists(PositionPath))
            {
                var value = JsonSerializer.Deserialize<Position>(File.ReadAllText(PositionPath));
                if (value is not null && double.IsFinite(value.Left) && double.IsFinite(value.Top))
                {
                    Left = Clamp(value.Left, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width);
                    Top = Clamp(value.Top, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height);
                    return;
                }
            }
        }
        catch (Exception exception) { DesktopLog.Write($"Quark position could not be restored: {exception.GetType().Name}"); }
        Left = SystemParameters.WorkArea.Right - Width - 24;
        Top = SystemParameters.WorkArea.Bottom - Height - 72;
    }

    private static double Clamp(double value, double minimum, double maximum) =>
        maximum < minimum ? minimum : Math.Max(minimum, Math.Min(maximum, value));

    private void SavePosition()
    {
        if (!IsLoaded || !double.IsFinite(Left) || !double.IsFinite(Top)) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PositionPath)!);
            File.WriteAllText(PositionPath, JsonSerializer.Serialize(new Position(Left, Top)));
        }
        catch (Exception exception) { DesktopLog.Write($"Quark position could not be saved: {exception.GetType().Name}"); }
    }

    public void Dispose()
    {
        _motionTimer.Stop();
        SavePosition();
        _allowClose = true;
        Close();
    }

    private sealed record Position(double Left, double Top);
}
