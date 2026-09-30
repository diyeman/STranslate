using STranslate.Helpers;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace STranslate.Views;

/// <summary>
/// 灵动岛窗口：以屏幕顶部居中的胶囊形态展示翻译结果，
/// </summary>
public partial class DynamicIslandWindow : IDisposable
{
    /// <summary>
    /// 用户双击灵动岛时触发（由 ViewModel 展开原翻译窗口）。
    /// </summary>
    public event EventHandler? IslandDoubleClicked;

    /// <summary>
    /// 灵动岛收起后触发（含自动隐藏），供 ViewModel 同步自身激活状态。
    /// </summary>
    public event EventHandler? IslandHidden;

    #region 可配置外观（由 ViewModel 从 Settings 同步）

    public TimeSpan AutoHideDuration { get; set; } = TimeSpan.FromSeconds(10);
    public double IslandMinWidth { get; set; } = 180;
    public double IslandMaxWidth { get; set; } = 520;

    private double _islandHeight = 54;

    /// <summary>
    /// 胶囊高度。低于 <see cref="MinIslandHeight"/> 会裁切左侧图标，这里统一做下限保护。
    /// </summary>
    public double IslandHeight
    {
        get => _islandHeight;
        set => _islandHeight = double.IsFinite(value) ? Math.Max(MinIslandHeight, value) : MinIslandHeight;
    }

    public double IslandTopMargin { get; set; } = 16;
    public double IslandFontSize { get; set; } = 15;

    #endregion

    private const double ShadowPadding = 12;
    private const string LoadingText = "翻译中…";
    private const double LoadingOpacity = 0.55;

    /// <summary>左侧图标 28px + 上下呼吸空间，低于该高度会被裁切。</summary>
    internal const double MinIslandHeight = 36;

    private DispatcherTimer? _autoHideTimer;
    private Storyboard? _activeStoryboard;
    private bool _hasResult;
    private bool _isHovering;
    private bool _isHiding;
    private int _showRequestId;
    private bool _disposed;

    public DynamicIslandWindow()
    {
        InitializeComponent();
    }

    public bool IsIslandShowing => IsVisible;

    /// <summary>
    /// 灵动岛当前是否处于鼠标悬停状态（悬停期间抑制自动隐藏）。
    /// </summary>
    internal bool IsHovering => _isHovering;

    /// <summary>
    /// 是否正在播放收起动画。收起动画中途被打断时不会触发 Completed，
    /// 需要靠这个标记决定是否把视觉基值恢复为可见状态。
    /// </summary>
    internal bool IsHiding => _isHiding;

    /// <summary>
    /// 注入悬停状态。生产代码由鼠标事件驱动，测试中用于验证"隐藏后必须复位"的不变量。
    /// </summary>
    internal void SetHovering(bool hovering) => _isHovering = hovering;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 与鼠标划词悬浮图标保持一致：不激活窗口，避免点击/显示灵动岛时抢占当前应用焦点。
        var hwnd = new HWND(new WindowInteropHelper(this).Handle);
        var extendedStyle = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLongPtr(
            hwnd,
            WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetWindowLongPtr(
            hwnd,
            WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE,
            (nint)(extendedStyle | WINDOW_EX_STYLE.WS_EX_NOACTIVATE));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ResultText.FontSize = IslandFontSize;
    }

    /// <summary>
    /// 显示灵动岛。<paramref name="text"/> 为空时进入"翻译中"状态。
    /// </summary>
    public void ShowIsland(string? text = null)
    {
        if (_disposed) return;

        _autoHideTimer?.Stop();
        // 收起动画中途被打断时，视觉基值仍是"已收起"，需先恢复为可见状态。
        CancelPendingHide();

        ResultText.FontSize = IslandFontSize;
        SetContent(text);
        ApplySizeAndPosition(text);

        // 首帧隐藏，随后展开动画接管
        PillScale.ScaleX = 0.3;
        PillScale.ScaleY = 0.3;
        Pill.Opacity = 0;
        ResultText.Opacity = 0;

        // 与其它悬浮窗保持一致：先在 cloak 状态下建立窗口，等 DWM 接收透明初始帧后再揭开，
        // 避免透明分层窗口在 DWM 合成繁忙（截图/全屏/远程桌面）时偶发不渲染。
        Win32Helper.SetWindowCloaked(this, cloaked: true);
        Show();
        // 窗口句柄建立后 DPI 才准确，二次校正位置
        PositionOnScreen();

        var showRequestId = ++_showRequestId;
        _ = Dispatcher.InvokeAsync(() => RevealAndAnimate(showRequestId), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 更新灵动岛上的翻译结果文本，并重置自动隐藏计时。
    /// </summary>
    public void UpdateResult(string text)
    {
        if (_disposed) return;

        // 结果迟到（岛已自动隐藏）时重新展示，而不是静默丢弃。
        if (!IsVisible)
        {
            ShowIsland(text);
            return;
        }

        CancelPendingHide();
        StopActiveAnimation();
        SetContent(text);
        ApplySizeAndPosition(text);
        PlayContentPopAnimation();
        if (_hasResult && !_isHovering)
            StartAutoHideTimer();
    }

    /// <summary>
    /// 收起灵动岛（播放收起动画后隐藏）。
    /// </summary>
    public void HideIsland()
    {
        if (_disposed)
            return;

        _autoHideTimer?.Stop();
        // 隐藏时鼠标可能正压在岛上，MouseLeave 不会触发，必须手动复位，否则此后永不自动隐藏。
        _isHovering = false;

        if (!IsVisible)
        {
            _hasResult = false;
            return;
        }

        PlayHideAnimation();
    }

    #region 显隐管线（与其它悬浮窗对齐）

    /// <summary>
    /// 揭开已 cloaked 的窗口：先等 DWM 提交透明初始帧，再挂动画时钟并取消 cloaked。
    /// </summary>
    private void RevealAndAnimate(int showRequestId)
    {
        if (_disposed || showRequestId != _showRequestId || !IsVisible)
            return;

        // 等 DWM 接收透明初始帧，并在 Cloak 内挂好动画时钟后再显示，避免暴露动画前后的中间基值。
        Win32Helper.FlushDesktopComposition();
        PlayShowAnimation();
        Win32Helper.SetWindowCloaked(this, cloaked: false);

        if (_hasResult && !_isHovering)
            StartAutoHideTimer();
    }

    /// <summary>
    /// 统一收尾：cloak 后隐藏窗口并通知外部。
    /// </summary>
    private void CompleteHide()
    {
        var wasVisible = IsVisible;

        _isHiding = false;
        _hasResult = false;
        _isHovering = false;
        _autoHideTimer?.Stop();
        // 使仍在排队的 RevealAndAnimate 失效
        _showRequestId++;

        if (!wasVisible)
            return;

        Win32Helper.SetWindowCloaked(this, cloaked: true);
        Hide();
        IslandHidden?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 取消正在播放的收起动画并恢复可见基值（收起动画会把基值改成"已收起"）。
    /// </summary>
    private void CancelPendingHide()
    {
        if (!_isHiding)
            return;

        StopActiveAnimation();
        _isHiding = false;
        Pill.Opacity = 1;
        PillScale.ScaleX = 1;
        PillScale.ScaleY = 1;
        ResultText.Opacity = _hasResult ? 1.0 : LoadingOpacity;
    }

    #endregion

    #region 尺寸与位置

    private void ApplySizeAndPosition(string? text)
    {
        var textWidth = MeasureTextWidth(text);
        // 左侧图标 + 内边距 + 文本
        var desiredWidth = Math.Max(IslandMinWidth, Math.Min(IslandMaxWidth, textWidth + 96));
        var islandHeight = Math.Max(MinIslandHeight, IslandHeight);

        Width = desiredWidth + ShadowPadding * 2;
        Height = islandHeight + ShadowPadding * 2;
        Pill.CornerRadius = new CornerRadius(islandHeight / 2);
        Pill.Height = islandHeight;

        PositionOnScreen();
    }

    private double MeasureTextWidth(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 120;

        var typeface = new Typeface(
            ResultText.FontFamily,
            ResultText.FontStyle,
            ResultText.FontWeight,
            ResultText.FontStretch);
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            ResultText.FontSize,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    private void PositionOnScreen()
    {
        try
        {
            var monitor = MonitorInfo.GetCursorDisplayMonitor();
            var workArea = monitor.WorkingArea;
            // MonitorInfo 的 Bounds/WorkingArea 直接来自 Win32 RECT，本身就是物理像素，
            // 这里按该显示器自身的 DPI 换算，避免窗口尚未移动到目标屏幕时用错 DPI 导致居中偏移。
            var dpi = Win32Helper.GetDpiScaleForPhysicalPoint(
                (int)Math.Round(workArea.X + workArea.Width / 2),
                (int)Math.Round(workArea.Y + workArea.Height / 2));

            var physicalWidth = Math.Max(1, (int)Math.Round(Width * dpi.DpiScaleX));
            var physicalHeight = Math.Max(1, (int)Math.Round(Height * dpi.DpiScaleY));
            var left = (int)Math.Round(workArea.X + (workArea.Width - physicalWidth) / 2);
            var top = (int)Math.Round(workArea.Y + IslandTopMargin * dpi.DpiScaleY);

            Left = left / dpi.DpiScaleX;
            Top = top / dpi.DpiScaleY;
            Win32Helper.SetWindowPhysicalBounds(this, left, top, physicalWidth, physicalHeight, showWindow: false);
        }
        catch
        {
            Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2;
            Top = SystemParameters.WorkArea.Top + IslandTopMargin;
        }
    }

    private void SetContent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _hasResult = false;
            ResultText.Text = LoadingText;
            ResultText.Opacity = LoadingOpacity;
            // 避免新翻译开始时仍挂着上一次结果的提示
            Pill.ToolTip = null;
        }
        else
        {
            _hasResult = true;
            ResultText.Text = text;
            ResultText.Opacity = 1.0;
            Pill.ToolTip = text;
        }
    }

    #endregion

    #region 动画

    /// <summary>
    /// 停止当前动画并清除回调，避免被中断的动画随后再触发 Completed。
    /// </summary>
    private void StopActiveAnimation()
    {
        if (_activeStoryboard == null)
            return;

        _activeStoryboard.Stop(this);
        _activeStoryboard = null;
    }

    /// <summary>
    /// 所有动画使用默认 FillBehavior.Stop，基值设为最终状态，避免中途停止时闪回。
    /// </summary>
    private void PlayShowAnimation()
    {
        var targetContentOpacity = _hasResult ? 1.0 : LoadingOpacity;

        // 基值 = 最终状态
        Pill.Opacity = 1;
        PillScale.ScaleX = 1;
        PillScale.ScaleY = 1;
        ResultText.Opacity = targetContentOpacity;

        var sb = new Storyboard();

        // 横向展开（轻微回弹）
        var scaleX = new DoubleAnimation(0.3, 1.0, new Duration(TimeSpan.FromMilliseconds(380)))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }
        };
        Storyboard.SetTarget(scaleX, PillScale);
        Storyboard.SetTargetProperty(scaleX, new PropertyPath(ScaleTransform.ScaleXProperty));
        sb.Children.Add(scaleX);

        // 纵向展开
        var scaleY = new DoubleAnimation(0.3, 1.0, new Duration(TimeSpan.FromMilliseconds(380)))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }
        };
        Storyboard.SetTarget(scaleY, PillScale);
        Storyboard.SetTargetProperty(scaleY, new PropertyPath(ScaleTransform.ScaleYProperty));
        sb.Children.Add(scaleY);

        // 胶囊淡入
        var opacity = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(220)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(opacity, Pill);
        Storyboard.SetTargetProperty(opacity, new PropertyPath(OpacityProperty));
        sb.Children.Add(opacity);

        // 内容延迟淡入
        var contentOpacity = new DoubleAnimation(0, targetContentOpacity, new Duration(TimeSpan.FromMilliseconds(220)))
        {
            BeginTime = TimeSpan.FromMilliseconds(120)
        };
        Storyboard.SetTarget(contentOpacity, ResultText);
        Storyboard.SetTargetProperty(contentOpacity, new PropertyPath(OpacityProperty));
        sb.Children.Add(contentOpacity);

        RunStoryboard(sb);
    }

    /// <summary>
    /// 结果更新时的小弹动动画。
    /// </summary>
    private void PlayContentPopAnimation()
    {
        // 基值 = 最终状态
        Pill.Opacity = 1;
        PillScale.ScaleX = 1;
        PillScale.ScaleY = 1;
        ResultText.Opacity = _hasResult ? 1.0 : LoadingOpacity;

        var sb = new Storyboard();

        var scaleX = new DoubleAnimation(0.9, 1.0, new Duration(TimeSpan.FromMilliseconds(240)))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 }
        };
        Storyboard.SetTarget(scaleX, PillScale);
        Storyboard.SetTargetProperty(scaleX, new PropertyPath(ScaleTransform.ScaleXProperty));
        sb.Children.Add(scaleX);

        var scaleY = new DoubleAnimation(0.9, 1.0, new Duration(TimeSpan.FromMilliseconds(240)))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 }
        };
        Storyboard.SetTarget(scaleY, PillScale);
        Storyboard.SetTargetProperty(scaleY, new PropertyPath(ScaleTransform.ScaleYProperty));
        sb.Children.Add(scaleY);

        var contentOpacity = new DoubleAnimation(0.4, _hasResult ? 1.0 : LoadingOpacity, new Duration(TimeSpan.FromMilliseconds(240)));
        Storyboard.SetTarget(contentOpacity, ResultText);
        Storyboard.SetTargetProperty(contentOpacity, new PropertyPath(OpacityProperty));
        sb.Children.Add(contentOpacity);

        RunStoryboard(sb);
    }

    /// <summary>
    /// 收起动画：内容淡出，胶囊收缩后隐藏。
    /// </summary>
    private void PlayHideAnimation()
    {
        // 先取当前（可能仍在动画中的）值作为动画起点
        var currentContentOpacity = ResultText.Opacity;
        var currentScaleX = PillScale.ScaleX;
        var currentScaleY = PillScale.ScaleY;
        var currentPillOpacity = Pill.Opacity;

        // 基值 = 收起后的隐藏状态，动画结束后隐藏窗口
        PillScale.ScaleX = 0.3;
        PillScale.ScaleY = 0.3;
        Pill.Opacity = 0;
        ResultText.Opacity = 0;

        var sb = new Storyboard();

        var contentOpacity = new DoubleAnimation(currentContentOpacity, 0, new Duration(TimeSpan.FromMilliseconds(120)));
        Storyboard.SetTarget(contentOpacity, ResultText);
        Storyboard.SetTargetProperty(contentOpacity, new PropertyPath(OpacityProperty));
        sb.Children.Add(contentOpacity);

        var scaleX = new DoubleAnimation(currentScaleX, 0.3, new Duration(TimeSpan.FromMilliseconds(260)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(scaleX, PillScale);
        Storyboard.SetTargetProperty(scaleX, new PropertyPath(ScaleTransform.ScaleXProperty));
        sb.Children.Add(scaleX);

        var scaleY = new DoubleAnimation(currentScaleY, 0.3, new Duration(TimeSpan.FromMilliseconds(260)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(scaleY, PillScale);
        Storyboard.SetTargetProperty(scaleY, new PropertyPath(ScaleTransform.ScaleYProperty));
        sb.Children.Add(scaleY);

        var opacity = new DoubleAnimation(currentPillOpacity, 0, new Duration(TimeSpan.FromMilliseconds(280)));
        Storyboard.SetTarget(opacity, Pill);
        Storyboard.SetTargetProperty(opacity, new PropertyPath(OpacityProperty));
        sb.Children.Add(opacity);

        _isHiding = true;
        _activeStoryboard = sb;
        sb.Completed += (_, _) =>
        {
            // 已被新动画（展示/更新）替换时不再收尾，避免误隐藏
            if (!ReferenceEquals(_activeStoryboard, sb))
                return;

            _activeStoryboard = null;
            CompleteHide();
        };
        sb.Begin(this, true);
    }

    private void RunStoryboard(Storyboard sb)
    {
        _isHiding = false;
        _activeStoryboard = sb;
        sb.Completed += (_, _) =>
        {
            // 已被后续动画替换时不能清空引用，否则收起动画将无法被取消/收尾
            if (ReferenceEquals(_activeStoryboard, sb))
                _activeStoryboard = null;
        };
        sb.Begin(this, true);
    }

    #endregion

    #region 自动隐藏

    private void StartAutoHideTimer()
    {
        _autoHideTimer?.Stop();
        _autoHideTimer = new DispatcherTimer { Interval = AutoHideDuration };
        _autoHideTimer.Tick += OnAutoHideTick;
        _autoHideTimer.Start();
    }

    private void OnAutoHideTick(object? sender, EventArgs e)
    {
        _autoHideTimer?.Stop();
        // 悬停时暂不隐藏，鼠标移出后 OnPillMouseLeave 会重启计时。
        // 同时用 IsMouseOver 兜底：避免 _isHovering 因隐藏时收不到 MouseLeave 而卡死。
        if (_isHovering && IsMouseOver)
            return;

        _isHovering = false;
        HideIsland();
    }

    private void OnPillMouseEnter(object sender, MouseEventArgs e)
    {
        _isHovering = true;
        _autoHideTimer?.Stop();
    }

    private void OnPillMouseLeave(object sender, MouseEventArgs e)
    {
        _isHovering = false;
        if (IsVisible && _hasResult)
            StartAutoHideTimer();
    }

    private void OnPillMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
            return;

        e.Handled = true;
        IslandDoubleClicked?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _autoHideTimer?.Stop();
        StopActiveAnimation();
        _isHiding = false;
        _hasResult = false;
        Close();
    }

    #endregion
}
