//
//  OverlayWindow.cs
//  Clicky for Windows
//
//  System-wide transparent overlay hosting the blue glowing cursor buddy.
//  One OverlayWindow is created per screen so the buddy seamlessly follows
//  the cursor across multiple monitors — a direct port of the original's
//  per-screen NSPanel + BlueCursorView.
//
//  The window is click-through (WS_EX_TRANSPARENT), never activates
//  (WS_EX_NOACTIVATE), hides from alt-tab (WS_EX_TOOLWINDOW), and is
//  excluded from screen capture (WDA_EXCLUDEFROMCAPTURE) so the buddy
//  never appears in its own screenshots.
//
//  All motion is driven frame-by-frame from CompositionTarget.Rendering:
//  a critically tuned spring for cursor following (matching SwiftUI's
//  spring(response: 0.2, dampingFraction: 0.6)), and the same quadratic
//  bezier arc math as the original for element-pointing flights.
//

using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Clicky.Native;
using WinFormsScreen = System.Windows.Forms.Screen;

namespace Clicky.Ui;

/// The buddy's behavioral mode — following the cursor, flying to a
/// detected UI element, or pointing at it.
public enum BuddyNavigationMode
{
    FollowingCursor,
    NavigatingToTarget,
    PointingAtTarget,
}

public class OverlayWindow : Window
{
    private readonly System.Drawing.Rectangle screenBoundsInPixels;
    private readonly bool isFirstAppearance;
    private readonly CompanionManager companionManager;

    // ── Visual elements ──────────────────────────────────────────────

    private readonly Canvas rootCanvas;
    private readonly Path triangleCursorPath;
    private readonly RotateTransform triangleRotation;
    private readonly ScaleTransform triangleScale;
    private readonly DropShadowEffect triangleGlow;
    private readonly Canvas waveformCanvas;
    private readonly System.Windows.Shapes.Rectangle[] waveformBars;
    private readonly Canvas spinnerCanvas;
    private readonly RotateTransform spinnerRotation;
    private readonly Border welcomeBubble;
    private readonly TextBlock welcomeBubbleText;
    private readonly Border onboardingPromptBubble;
    private readonly TextBlock onboardingPromptBubbleText;
    private readonly Border navigationBubble;
    private readonly TextBlock navigationBubbleText;
    private readonly ScaleTransform navigationBubbleScale;
    private readonly DropShadowEffect navigationBubbleGlow;

    // ── Animation state (mirrors BlueCursorView's @State) ───────────

    private System.Windows.Point buddyPosition;
    private System.Windows.Point buddyVelocity;
    private bool isCursorOnThisScreen;
    private double cursorOpacity;
    private double triangleStateOpacity = 1;
    private double waveformStateOpacity;
    private double spinnerStateOpacity;
    private double triangleRotationDegrees = -35.0;
    private double buddyFlightScale = 1.0;

    private BuddyNavigationMode buddyNavigationMode = BuddyNavigationMode.FollowingCursor;
    private bool isReturningToCursor;
    private System.Windows.Point cursorPositionWhenNavigationStarted;

    // Bezier flight parameters, valid while navigating.
    private System.Windows.Point flightStartPosition;
    private System.Windows.Point flightControlPoint;
    private System.Windows.Point flightEndPosition;
    private double flightDurationSeconds;
    private double flightElapsedSeconds;
    private Action? flightCompletionAction;

    private bool showWelcome;
    private double welcomeBubbleOpacity;
    private double navigationBubbleOpacityTarget;
    private double navigationBubbleCurrentOpacity;

    private TimeSpan lastRenderTime = TimeSpan.Zero;
    private double animationClockSeconds;
    private bool isRenderLoopAttached;

    /// The buddy trails the real cursor by this offset (in DIPs), exactly
    /// like the original's +35/+25 point offset.
    private static readonly Vector BuddyTrackingOffset = new(35, 25);

    private static readonly string[] NavigationPointerPhrases =
    {
        "right here!",
        "this one!",
        "over here!",
        "click this!",
        "here it is!",
        "found it!",
    };

    private const string FullWelcomeMessage = "hey! i'm clicky";

    public OverlayWindow(WinFormsScreen screen, bool isFirstAppearance, CompanionManager companionManager)
    {
        this.screenBoundsInPixels = screen.Bounds;
        this.isFirstAppearance = isFirstAppearance;
        this.companionManager = companionManager;

        // Transparent, borderless, topmost, no taskbar entry, never focused.
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        rootCanvas = new Canvas { IsHitTestVisible = false };
        Content = rootCanvas;

        // ── Triangle cursor ──────────────────────────────────────────
        triangleRotation = new RotateTransform(triangleRotationDegrees, 8, 8);
        triangleScale = new ScaleTransform(1, 1, 8, 8);
        triangleGlow = new DropShadowEffect
        {
            Color = DS.Colors.OverlayCursorBlue,
            ShadowDepth = 0,
            BlurRadius = 8,
            Opacity = 1,
        };
        triangleCursorPath = new Path
        {
            Data = BuildTriangleGeometry(16),
            Fill = DS.Brushes.OverlayCursorBlue,
            Width = 16,
            Height = 16,
            Effect = triangleGlow,
            RenderTransform = new TransformGroup { Children = { triangleScale, triangleRotation } },
        };
        rootCanvas.Children.Add(triangleCursorPath);

        // ── Waveform (listening) ─────────────────────────────────────
        waveformCanvas = new Canvas
        {
            Width = 18,
            Height = 16,
            Effect = new DropShadowEffect
            {
                Color = DS.Colors.OverlayCursorBlue,
                ShadowDepth = 0,
                BlurRadius = 6,
                Opacity = 0.6,
            },
        };
        waveformBars = new System.Windows.Shapes.Rectangle[5];
        for (int barIndex = 0; barIndex < 5; barIndex++)
        {
            var bar = new System.Windows.Shapes.Rectangle
            {
                Width = 2,
                Height = 3,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = DS.Brushes.OverlayCursorBlue,
            };
            Canvas.SetLeft(bar, barIndex * 4);
            waveformBars[barIndex] = bar;
            waveformCanvas.Children.Add(bar);
        }
        rootCanvas.Children.Add(waveformCanvas);

        // ── Spinner (processing) ─────────────────────────────────────
        spinnerRotation = new RotateTransform(0, 7, 7);
        spinnerCanvas = BuildSpinnerCanvas();
        spinnerCanvas.RenderTransform = spinnerRotation;
        spinnerCanvas.Effect = new DropShadowEffect
        {
            Color = DS.Colors.OverlayCursorBlue,
            ShadowDepth = 0,
            BlurRadius = 6,
            Opacity = 0.6,
        };
        rootCanvas.Children.Add(spinnerCanvas);

        // ── Bubbles ──────────────────────────────────────────────────
        (welcomeBubble, welcomeBubbleText) = BuildSpeechBubble();
        rootCanvas.Children.Add(welcomeBubble);

        (onboardingPromptBubble, onboardingPromptBubbleText) = BuildSpeechBubble();
        rootCanvas.Children.Add(onboardingPromptBubble);

        (navigationBubble, navigationBubbleText) = BuildSpeechBubble();
        navigationBubbleScale = new ScaleTransform(1, 1);
        navigationBubble.RenderTransform = navigationBubbleScale;
        navigationBubbleGlow = (DropShadowEffect)navigationBubble.Effect!;
        rootCanvas.Children.Add(navigationBubble);

        // Seed the buddy position from the current cursor so it doesn't
        // flash at (0,0) before the first frame.
        var initialCursor = GetCursorPositionInLocalDips() ?? new System.Windows.Point(100, 100);
        buddyPosition = initialCursor + BuddyTrackingOffset;
        isCursorOnThisScreen = IsGlobalCursorOnThisScreen();

        cursorOpacity = 0;
        showWelcome = isFirstAppearance;

        SourceInitialized += HandleSourceInitialized;
        Loaded += HandleLoaded;
        Closed += HandleClosed;

        companionManager.DetectedElementChanged += HandleDetectedElementChanged;
    }

    // ── Window setup ─────────────────────────────────────────────────

    private void HandleSourceInitialized(object? sender, EventArgs e)
    {
        var windowHandle = new WindowInteropHelper(this).Handle;

        // Click-through + never-activate + hidden from alt-tab.
        IntPtr currentStyle = NativeMethods.GetWindowLongPtr(windowHandle, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(
            windowHandle,
            NativeMethods.GWL_EXSTYLE,
            new IntPtr(currentStyle.ToInt64()
                | NativeMethods.WS_EX_TRANSPARENT
                | NativeMethods.WS_EX_NOACTIVATE
                | NativeMethods.WS_EX_TOOLWINDOW
                | NativeMethods.WS_EX_LAYERED));

        // Keep the buddy out of screenshots and screen recordings — the
        // equivalent of the original excluding its own windows from capture.
        NativeMethods.SetWindowDisplayAffinity(windowHandle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);

        // Cover the screen exactly, in physical pixels (DPI-safe).
        NativeMethods.SetWindowPos(
            windowHandle,
            NativeMethods.HWND_TOPMOST,
            screenBoundsInPixels.X,
            screenBoundsInPixels.Y,
            screenBoundsInPixels.Width,
            screenBoundsInPixels.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    private void HandleLoaded(object? sender, RoutedEventArgs e)
    {
        if (!isRenderLoopAttached)
        {
            CompositionTarget.Rendering += HandleRendering;
            isRenderLoopAttached = true;
        }

        if (isFirstAppearance && isCursorOnThisScreen)
        {
            // Fade the cursor in over 2s, then start the welcome bubble —
            // same choreography as the original's first appearance.
            AnimateCursorOpacityTo(1.0, durationSeconds: 2.0);
            var welcomeDelayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.0) };
            welcomeDelayTimer.Tick += (_, _) =>
            {
                welcomeDelayTimer.Stop();
                StartWelcomeAnimation();
            };
            welcomeDelayTimer.Start();
        }
        else
        {
            cursorOpacity = 1.0;
        }
    }

    private void HandleClosed(object? sender, EventArgs e)
    {
        if (isRenderLoopAttached)
        {
            CompositionTarget.Rendering -= HandleRendering;
            isRenderLoopAttached = false;
        }
        companionManager.DetectedElementChanged -= HandleDetectedElementChanged;
    }

    private DoubleAnimationState? cursorOpacityAnimation;

    private record DoubleAnimationState(double From, double To, double DurationSeconds, double StartClock);

    private void AnimateCursorOpacityTo(double target, double durationSeconds)
    {
        cursorOpacityAnimation = new DoubleAnimationState(cursorOpacity, target, durationSeconds, animationClockSeconds);
    }

    // ── Geometry builders ────────────────────────────────────────────

    /// Equilateral-ish triangle matching the original's Triangle shape:
    /// top vertex raised by height/1.5, base at height/3 below center.
    private static Geometry BuildTriangleGeometry(double size)
    {
        double height = size * Math.Sqrt(3.0) / 2.0;
        double midX = size / 2.0;
        double midY = size / 2.0;

        var figure = new PathFigure
        {
            StartPoint = new System.Windows.Point(midX, midY - height / 1.5),
            IsClosed = true,
        };
        figure.Segments.Add(new LineSegment(new System.Windows.Point(midX - size / 2, midY + height / 3), isStroked: true));
        figure.Segments.Add(new LineSegment(new System.Windows.Point(midX + size / 2, midY + height / 3), isStroked: true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    /// The processing spinner: a 0.15→0.85 trimmed circle whose stroke fades
    /// in along the sweep (approximating the original's angular gradient),
    /// rotated continuously by the render loop.
    private static Canvas BuildSpinnerCanvas()
    {
        var canvas = new Canvas { Width = 14, Height = 14 };
        const double radius = 5.75;
        const double centerX = 7, centerY = 7;
        const int segmentCount = 24;
        const double startFraction = 0.15, endFraction = 0.85;

        for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
        {
            double fractionA = startFraction + (endFraction - startFraction) * segmentIndex / segmentCount;
            double fractionB = startFraction + (endFraction - startFraction) * (segmentIndex + 1) / segmentCount;
            double angleA = fractionA * 2 * Math.PI - Math.PI / 2;
            double angleB = fractionB * 2 * Math.PI - Math.PI / 2;

            var segment = new Line
            {
                X1 = centerX + radius * Math.Cos(angleA),
                Y1 = centerY + radius * Math.Sin(angleA),
                X2 = centerX + radius * Math.Cos(angleB),
                Y2 = centerY + radius * Math.Sin(angleB),
                StrokeThickness = 2.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Stroke = new SolidColorBrush(DS.Colors.OverlayCursorBlue)
                {
                    Opacity = (double)(segmentIndex + 1) / segmentCount,
                },
            };
            canvas.Children.Add(segment);
        }

        return canvas;
    }

    /// Speech bubble matching the original: blue rounded rect, white 11pt
    /// medium text, soft blue glow.
    private static (Border bubble, TextBlock text) BuildSpeechBubble()
    {
        var text = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = DS.Brushes.White,
            FontFamily = new FontFamily("Segoe UI"),
        };
        var bubble = new Border
        {
            Child = text,
            Background = DS.Brushes.OverlayCursorBlue,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            Opacity = 0,
            Effect = new DropShadowEffect
            {
                Color = DS.Colors.OverlayCursorBlue,
                ShadowDepth = 0,
                BlurRadius = 6,
                Opacity = 0.5,
            },
        };
        return (bubble, text);
    }

    // ── Cursor coordinate conversion ─────────────────────────────────

    private bool IsGlobalCursorOnThisScreen()
    {
        NativeMethods.GetCursorPos(out var cursorPx);
        return screenBoundsInPixels.Contains(cursorPx.X, cursorPx.Y);
    }

    /// Converts the global cursor position (physical pixels) into this
    /// window's local DIP space, handling per-monitor DPI.
    private System.Windows.Point? GetCursorPositionInLocalDips()
    {
        NativeMethods.GetCursorPos(out var cursorPx);
        var localPixels = new System.Windows.Point(
            cursorPx.X - screenBoundsInPixels.X,
            cursorPx.Y - screenBoundsInPixels.Y);

        var presentationSource = PresentationSource.FromVisual(this);
        if (presentationSource?.CompositionTarget == null)
        {
            // Window not yet realized — approximate with this screen's DPI.
            var dpi = VisualTreeHelper.GetDpi(this);
            return new System.Windows.Point(localPixels.X / dpi.DpiScaleX, localPixels.Y / dpi.DpiScaleY);
        }

        return presentationSource.CompositionTarget.TransformFromDevice.Transform(localPixels);
    }

    private System.Windows.Point ConvertGlobalPixelsToLocalDips(System.Drawing.Point globalPixels)
    {
        var localPixels = new System.Windows.Point(
            globalPixels.X - screenBoundsInPixels.X,
            globalPixels.Y - screenBoundsInPixels.Y);

        var presentationSource = PresentationSource.FromVisual(this);
        if (presentationSource?.CompositionTarget == null)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            return new System.Windows.Point(localPixels.X / dpi.DpiScaleX, localPixels.Y / dpi.DpiScaleY);
        }
        return presentationSource.CompositionTarget.TransformFromDevice.Transform(localPixels);
    }

    // ── Render loop ──────────────────────────────────────────────────

    private void HandleRendering(object? sender, EventArgs e)
    {
        var renderingArgs = (RenderingEventArgs)e;
        if (lastRenderTime == TimeSpan.Zero)
        {
            lastRenderTime = renderingArgs.RenderingTime;
            return;
        }

        double deltaSeconds = (renderingArgs.RenderingTime - lastRenderTime).TotalSeconds;
        if (deltaSeconds <= 0)
        {
            return;
        }
        lastRenderTime = renderingArgs.RenderingTime;
        // Clamp huge gaps (system sleep, debugger pause) so springs don't explode.
        deltaSeconds = Math.Min(deltaSeconds, 1.0 / 30.0);
        animationClockSeconds += deltaSeconds;

        isCursorOnThisScreen = IsGlobalCursorOnThisScreen();

        UpdateCursorOpacityAnimation();
        UpdateBuddyMotion(deltaSeconds);
        UpdateStateCrossFades(deltaSeconds);
        UpdateWaveformBars();
        UpdateSpinnerRotation();
        ApplyVisualPositions();
    }

    private void UpdateCursorOpacityAnimation()
    {
        if (cursorOpacityAnimation is not { } animation)
        {
            return;
        }
        double progress = Math.Clamp((animationClockSeconds - animation.StartClock) / animation.DurationSeconds, 0, 1);
        // easeIn curve to match the original's fade-in
        double eased = progress * progress;
        cursorOpacity = animation.From + (animation.To - animation.From) * eased;
        if (progress >= 1)
        {
            cursorOpacityAnimation = null;
        }
    }

    private void UpdateBuddyMotion(double deltaSeconds)
    {
        if (buddyNavigationMode == BuddyNavigationMode.NavigatingToTarget)
        {
            UpdateFlightAnimation(deltaSeconds);
            return;
        }

        if (buddyNavigationMode == BuddyNavigationMode.PointingAtTarget)
        {
            // Hold position while pointing.
            return;
        }

        // Normal cursor following with a spring matching SwiftUI's
        // spring(response: 0.2, dampingFraction: 0.6).
        var cursorDips = GetCursorPositionInLocalDips();
        if (cursorDips == null)
        {
            return;
        }
        var target = cursorDips.Value + BuddyTrackingOffset;

        const double springResponseSeconds = 0.2;
        const double springDampingFraction = 0.6;
        double angularFrequency = 2 * Math.PI / springResponseSeconds;

        double accelerationX = -angularFrequency * angularFrequency * (buddyPosition.X - target.X)
                               - 2 * springDampingFraction * angularFrequency * buddyVelocity.X;
        double accelerationY = -angularFrequency * angularFrequency * (buddyPosition.Y - target.Y)
                               - 2 * springDampingFraction * angularFrequency * buddyVelocity.Y;

        buddyVelocity = new System.Windows.Point(
            buddyVelocity.X + accelerationX * deltaSeconds,
            buddyVelocity.Y + accelerationY * deltaSeconds);
        buddyPosition = new System.Windows.Point(
            buddyPosition.X + buddyVelocity.X * deltaSeconds,
            buddyPosition.Y + buddyVelocity.Y * deltaSeconds);

        // Ease the triangle's rotation back toward the default pointer angle.
        triangleRotationDegrees += (-35.0 - triangleRotationDegrees) * Math.Min(1, deltaSeconds / 0.1);
        buddyFlightScale += (1.0 - buddyFlightScale) * Math.Min(1, deltaSeconds / 0.1);
    }

    private void UpdateFlightAnimation(double deltaSeconds)
    {
        // During the RETURN flight only, cursor movement can cancel the
        // animation so the buddy snaps back to following.
        if (isReturningToCursor)
        {
            var currentCursor = GetCursorPositionInLocalDips();
            if (currentCursor != null)
            {
                double distanceFromNavigationStart = Distance(currentCursor.Value, cursorPositionWhenNavigationStarted);
                if (distanceFromNavigationStart > 100)
                {
                    CancelNavigationAndResumeFollowing();
                    return;
                }
            }
        }

        flightElapsedSeconds += deltaSeconds;
        double linearProgress = Math.Min(flightElapsedSeconds / flightDurationSeconds, 1.0);

        // Smoothstep easeInOut: 3t² - 2t³
        double t = linearProgress * linearProgress * (3.0 - 2.0 * linearProgress);
        double oneMinusT = 1.0 - t;

        // Quadratic bezier: B(t) = (1-t)²·P0 + 2(1-t)t·P1 + t²·P2
        buddyPosition = new System.Windows.Point(
            oneMinusT * oneMinusT * flightStartPosition.X
                + 2.0 * oneMinusT * t * flightControlPoint.X
                + t * t * flightEndPosition.X,
            oneMinusT * oneMinusT * flightStartPosition.Y
                + 2.0 * oneMinusT * t * flightControlPoint.Y
                + t * t * flightEndPosition.Y);

        // Face the direction of travel: tangent B'(t), +90° because the
        // triangle's tip points up at 0°.
        double tangentX = 2.0 * oneMinusT * (flightControlPoint.X - flightStartPosition.X)
                          + 2.0 * t * (flightEndPosition.X - flightControlPoint.X);
        double tangentY = 2.0 * oneMinusT * (flightControlPoint.Y - flightStartPosition.Y)
                          + 2.0 * t * (flightEndPosition.Y - flightControlPoint.Y);
        triangleRotationDegrees = Math.Atan2(tangentY, tangentX) * (180.0 / Math.PI) + 90.0;

        // Scale pulse peaking mid-flight for the swooping feel.
        buddyFlightScale = 1.0 + Math.Sin(linearProgress * Math.PI) * 0.3;

        if (linearProgress >= 1.0)
        {
            buddyPosition = flightEndPosition;
            buddyFlightScale = 1.0;
            buddyVelocity = new System.Windows.Point(0, 0);
            var completion = flightCompletionAction;
            flightCompletionAction = null;
            completion?.Invoke();
        }
    }

    private void UpdateStateCrossFades(double deltaSeconds)
    {
        var voiceState = companionManager.VoiceState;
        double triangleTarget = voiceState is CompanionVoiceState.Idle or CompanionVoiceState.Responding ? 1 : 0;
        double waveformTarget = voiceState == CompanionVoiceState.Listening ? 1 : 0;
        double spinnerTarget = voiceState == CompanionVoiceState.Processing ? 1 : 0;

        // Cross-fade speeds matching the original (0.25s in / 0.15s for
        // waveform+spinner).
        triangleStateOpacity = StepToward(triangleStateOpacity, triangleTarget, deltaSeconds / 0.25);
        waveformStateOpacity = StepToward(waveformStateOpacity, waveformTarget, deltaSeconds / 0.15);
        spinnerStateOpacity = StepToward(spinnerStateOpacity, spinnerTarget, deltaSeconds / 0.15);

        navigationBubbleCurrentOpacity = StepToward(navigationBubbleCurrentOpacity, navigationBubbleOpacityTarget, deltaSeconds / 0.4);

        // The bubble's pop-in scale springs toward 1 after starting at 0.5.
        double currentScale = navigationBubbleScale.ScaleX;
        double newScale = currentScale + (1.0 - currentScale) * Math.Min(1, deltaSeconds / 0.12);
        navigationBubbleScale.ScaleX = newScale;
        navigationBubbleScale.ScaleY = newScale;
        // The glow flares while the bubble is small and settles as it lands.
        navigationBubbleGlow.Opacity = Math.Clamp(0.5 + (1.0 - newScale) * 1.0, 0, 1);
        navigationBubbleGlow.BlurRadius = 6 + (1.0 - newScale) * 16;
    }

    private static double StepToward(double current, double target, double fraction)
    {
        if (Math.Abs(target - current) < 0.001)
        {
            return target;
        }
        return current + (target - current) * Math.Min(1, fraction);
    }

    private void UpdateWaveformBars()
    {
        // Same reactive-height formula as the original's waveform.
        double[] listeningBarProfile = { 0.4, 0.7, 1.0, 0.7, 0.4 };
        double audioPowerLevel = companionManager.CurrentAudioPowerLevel;

        for (int barIndex = 0; barIndex < waveformBars.Length; barIndex++)
        {
            double animationPhase = animationClockSeconds * 3.6 + barIndex * 0.35;
            double normalizedAudioPowerLevel = Math.Max(audioPowerLevel - 0.008, 0);
            double easedAudioPowerLevel = Math.Pow(Math.Min(normalizedAudioPowerLevel * 2.85, 1), 0.76);
            double reactiveHeight = easedAudioPowerLevel * 10 * listeningBarProfile[barIndex];
            double idlePulse = (Math.Sin(animationPhase) + 1) / 2 * 1.5;
            double barHeight = 3 + reactiveHeight + idlePulse;

            waveformBars[barIndex].Height = barHeight;
            Canvas.SetTop(waveformBars[barIndex], (16 - barHeight) / 2);
        }
    }

    private void UpdateSpinnerRotation()
    {
        // One revolution per 0.8s, matching the original.
        spinnerRotation.Angle = animationClockSeconds % 0.8 / 0.8 * 360.0;
    }

    /// Whether the buddy should be visible on this screen — hides duplicates
    /// while another screen's overlay is running a pointing animation.
    private bool BuddyIsVisibleOnThisScreen()
    {
        switch (buddyNavigationMode)
        {
            case BuddyNavigationMode.FollowingCursor:
                if (companionManager.DetectedElementScreenLocation != null)
                {
                    return false;
                }
                return isCursorOnThisScreen;
            case BuddyNavigationMode.NavigatingToTarget:
            case BuddyNavigationMode.PointingAtTarget:
                return true;
            default:
                return false;
        }
    }

    private void ApplyVisualPositions()
    {
        bool buddyVisible = BuddyIsVisibleOnThisScreen();
        double visibilityMultiplier = buddyVisible ? cursorOpacity : 0;

        // Triangle — positioned by its center.
        Canvas.SetLeft(triangleCursorPath, buddyPosition.X - 8);
        Canvas.SetTop(triangleCursorPath, buddyPosition.Y - 8);
        triangleCursorPath.Opacity = triangleStateOpacity * visibilityMultiplier;
        triangleRotation.Angle = triangleRotationDegrees;
        triangleScale.ScaleX = buddyFlightScale;
        triangleScale.ScaleY = buddyFlightScale;
        triangleGlow.BlurRadius = 8 + (buddyFlightScale - 1.0) * 20;

        // Waveform + spinner sit exactly where the triangle is.
        Canvas.SetLeft(waveformCanvas, buddyPosition.X - 9);
        Canvas.SetTop(waveformCanvas, buddyPosition.Y - 8);
        waveformCanvas.Opacity = waveformStateOpacity * visibilityMultiplier;

        Canvas.SetLeft(spinnerCanvas, buddyPosition.X - 7);
        Canvas.SetTop(spinnerCanvas, buddyPosition.Y - 7);
        spinnerCanvas.Opacity = spinnerStateOpacity * visibilityMultiplier;

        // Bubbles sit to the right of the buddy: left edge at +10, vertical
        // center at +18 — same offsets as the original.
        PositionBubble(welcomeBubble);
        welcomeBubble.Opacity = (isCursorOnThisScreen && showWelcome ? 1 : 0) * welcomeBubbleOpacity;

        PositionBubble(onboardingPromptBubble);
        onboardingPromptBubbleText.Text = companionManager.OnboardingPromptText;
        onboardingPromptBubble.Opacity =
            isCursorOnThisScreen && companionManager.ShowOnboardingPrompt && companionManager.OnboardingPromptText.Length > 0
                ? companionManager.OnboardingPromptOpacity
                : 0;

        PositionBubble(navigationBubble);
        navigationBubble.Opacity =
            buddyNavigationMode == BuddyNavigationMode.PointingAtTarget && navigationBubbleText.Text.Length > 0
                ? navigationBubbleCurrentOpacity
                : 0;
    }

    private void PositionBubble(Border bubble)
    {
        Canvas.SetLeft(bubble, buddyPosition.X + 10);
        Canvas.SetTop(bubble, buddyPosition.Y + 18 - bubble.ActualHeight / 2);
    }

    private static double Distance(System.Windows.Point a, System.Windows.Point b)
    {
        return Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }

    // ── Element navigation ───────────────────────────────────────────

    private void HandleDetectedElementChanged()
    {
        var screenLocation = companionManager.DetectedElementScreenLocation;
        var displayBounds = companionManager.DetectedElementDisplayBounds;
        if (screenLocation == null || displayBounds == null)
        {
            return;
        }

        // Only navigate if the target is on THIS screen.
        if (displayBounds.Value != screenBoundsInPixels)
        {
            return;
        }

        StartNavigatingToElement(screenLocation.Value);
    }

    private void StartNavigatingToElement(System.Drawing.Point globalPixelLocation)
    {
        // Don't interrupt the welcome animation.
        if (showWelcome && welcomeBubbleText.Text.Length > 0)
        {
            return;
        }

        var targetDips = ConvertGlobalPixelsToLocalDips(globalPixelLocation);

        // Offset so the buddy sits beside the element rather than on top of
        // it, then clamp inside the screen with padding.
        var offsetTarget = new System.Windows.Point(targetDips.X + 8, targetDips.Y + 12);
        var clampedTarget = new System.Windows.Point(
            Math.Clamp(offsetTarget.X, 20, Math.Max(21, ActualWidth - 20)),
            Math.Clamp(offsetTarget.Y, 20, Math.Max(21, ActualHeight - 20)));

        var currentCursor = GetCursorPositionInLocalDips() ?? buddyPosition;
        cursorPositionWhenNavigationStarted = currentCursor;

        buddyNavigationMode = BuddyNavigationMode.NavigatingToTarget;
        isReturningToCursor = false;

        BeginBezierFlight(clampedTarget, () =>
        {
            if (buddyNavigationMode == BuddyNavigationMode.NavigatingToTarget)
            {
                StartPointingAtElement();
            }
        });
    }

    private void BeginBezierFlight(System.Windows.Point destination, Action onComplete)
    {
        flightStartPosition = buddyPosition;
        flightEndPosition = destination;

        double deltaX = destination.X - buddyPosition.X;
        double deltaY = destination.Y - buddyPosition.Y;
        double distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);

        // Flight duration scales with distance: short hops are quick, long
        // flights more dramatic. Clamped 0.6s–1.4s like the original.
        flightDurationSeconds = Math.Clamp(distance / 800.0, 0.6, 1.4);
        flightElapsedSeconds = 0;

        // Control point: midpoint raised for a parabolic arc.
        double arcHeight = Math.Min(distance * 0.2, 80.0);
        flightControlPoint = new System.Windows.Point(
            (flightStartPosition.X + flightEndPosition.X) / 2.0,
            (flightStartPosition.Y + flightEndPosition.Y) / 2.0 - arcHeight);

        flightCompletionAction = onComplete;
    }

    private void StartPointingAtElement()
    {
        buddyNavigationMode = BuddyNavigationMode.PointingAtTarget;
        triangleRotationDegrees = -35.0;

        navigationBubbleText.Text = "";
        navigationBubbleOpacityTarget = 1.0;
        navigationBubbleCurrentOpacity = 1.0;
        navigationBubbleScale.ScaleX = 0.5;
        navigationBubbleScale.ScaleY = 0.5;

        string pointerPhrase = companionManager.DetectedElementBubbleText
            ?? NavigationPointerPhrases[Random.Shared.Next(NavigationPointerPhrases.Length)];

        StreamNavigationBubbleCharacter(pointerPhrase, 0, () =>
        {
            // All characters streamed — hold 3 seconds, fade, then fly back.
            var holdTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.0) };
            holdTimer.Tick += (_, _) =>
            {
                holdTimer.Stop();
                if (buddyNavigationMode != BuddyNavigationMode.PointingAtTarget) return;
                navigationBubbleOpacityTarget = 0.0;

                var fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
                fadeTimer.Tick += (_, _) =>
                {
                    fadeTimer.Stop();
                    if (buddyNavigationMode != BuddyNavigationMode.PointingAtTarget) return;
                    StartFlyingBackToCursor();
                };
                fadeTimer.Start();
            };
            holdTimer.Start();
        });
    }

    /// Streams bubble text one character at a time with variable 30–60ms
    /// delays for a natural "speaking" rhythm.
    private void StreamNavigationBubbleCharacter(string phrase, int characterIndex, Action onComplete)
    {
        if (buddyNavigationMode != BuddyNavigationMode.PointingAtTarget)
        {
            return;
        }
        if (characterIndex >= phrase.Length)
        {
            onComplete();
            return;
        }

        navigationBubbleText.Text += phrase[characterIndex];

        double characterDelay = 0.03 + Random.Shared.NextDouble() * 0.03;
        var characterTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(characterDelay) };
        characterTimer.Tick += (_, _) =>
        {
            characterTimer.Stop();
            StreamNavigationBubbleCharacter(phrase, characterIndex + 1, onComplete);
        };
        characterTimer.Start();
    }

    private void StartFlyingBackToCursor()
    {
        var currentCursor = GetCursorPositionInLocalDips() ?? buddyPosition;
        cursorPositionWhenNavigationStarted = currentCursor;

        buddyNavigationMode = BuddyNavigationMode.NavigatingToTarget;
        isReturningToCursor = true;

        BeginBezierFlight(currentCursor + BuddyTrackingOffset, FinishNavigationAndResumeFollowing);
    }

    private void CancelNavigationAndResumeFollowing()
    {
        navigationBubbleText.Text = "";
        navigationBubbleOpacityTarget = 0;
        navigationBubbleCurrentOpacity = 0;
        buddyFlightScale = 1.0;
        FinishNavigationAndResumeFollowing();
    }

    private void FinishNavigationAndResumeFollowing()
    {
        buddyNavigationMode = BuddyNavigationMode.FollowingCursor;
        isReturningToCursor = false;
        triangleRotationDegrees = -35.0;
        buddyFlightScale = 1.0;
        buddyVelocity = new System.Windows.Point(0, 0);
        navigationBubbleText.Text = "";
        navigationBubbleOpacityTarget = 0;
        navigationBubbleCurrentOpacity = 0;
        companionManager.ClearDetectedElementLocation();
    }

    // ── Welcome animation ────────────────────────────────────────────

    private void StartWelcomeAnimation()
    {
        welcomeBubbleOpacity = 1.0;
        welcomeBubbleText.Text = "";

        int currentIndex = 0;
        var characterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        characterTimer.Tick += (_, _) =>
        {
            if (currentIndex >= FullWelcomeMessage.Length)
            {
                characterTimer.Stop();
                // Hold 2 seconds, fade, then hand off to the onboarding flow
                // (the original started its intro video here; the port goes
                // straight to the try-it prompt + pointing demo).
                var holdTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.0) };
                holdTimer.Tick += (_, _) =>
                {
                    holdTimer.Stop();
                    welcomeBubbleOpacity = 0.0;
                    var cleanupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
                    cleanupTimer.Tick += (_, _) =>
                    {
                        cleanupTimer.Stop();
                        showWelcome = false;
                        if (isFirstAppearance)
                        {
                            companionManager.OnWelcomeAnimationFinished();
                        }
                    };
                    cleanupTimer.Start();
                };
                holdTimer.Start();
                return;
            }

            welcomeBubbleText.Text += FullWelcomeMessage[currentIndex];
            currentIndex++;
        };
        characterTimer.Start();
    }
}

// ── Overlay manager ──────────────────────────────────────────────────

/// Creates one overlay window per screen so the buddy follows the cursor
/// across all monitors — a direct port of OverlayWindowManager.
public class OverlayWindowManager
{
    private readonly List<OverlayWindow> overlayWindows = new();
    public bool HasShownOverlayBefore { get; set; }

    public void ShowOverlay(CompanionManager companionManager)
    {
        HideOverlay();

        bool isFirstAppearance = !HasShownOverlayBefore;
        HasShownOverlayBefore = true;

        foreach (var screen in WinFormsScreen.AllScreens)
        {
            var overlayWindow = new OverlayWindow(screen, isFirstAppearance, companionManager);
            overlayWindows.Add(overlayWindow);
            overlayWindow.Show();
        }
    }

    public void HideOverlay()
    {
        foreach (var window in overlayWindows)
        {
            try
            {
                window.Close();
            }
            catch
            {
                // Already closed — fine.
            }
        }
        overlayWindows.Clear();
    }

    /// Fades the overlay out over `durationSeconds`, then removes it.
    public void FadeOutAndHideOverlay(double durationSeconds = 0.4)
    {
        var windowsToFade = overlayWindows.ToList();
        overlayWindows.Clear();

        foreach (var window in windowsToFade)
        {
            var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromSeconds(durationSeconds))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            };
            fadeOut.Completed += (_, _) =>
            {
                try
                {
                    window.Close();
                }
                catch
                {
                    // Already closed — fine.
                }
            };
            window.BeginAnimation(System.Windows.Window.OpacityProperty, fadeOut);
        }
    }

    public bool IsShowingOverlay() => overlayWindows.Count > 0;
}
