using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NotiLink.Core.Networking;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Orientation = System.Windows.Controls.Orientation;

namespace NotiLink.App;

/// <summary>Shows another linked PC's screen (PC ↔ PC screen sharing) and, once that PC grants
/// it, drives it: mouse (move, buttons, wheel) and keyboard go over as PeerInput, 0..1 across the
/// picture. Frames arrive on the socket thread through <see cref="PushFrame"/>; only the newest
/// one is ever decoded, so a burst never queues up behind the UI. Mouse moves are coalesced to at
/// most one every ~8 ms. Closing the window stops viewing (and control).
///
/// While controlling, every key goes to the other PC — including Alt+F4 — except Ctrl+Alt+Enter
/// (full screen on/off) and Ctrl+Alt+Q (stop controlling). Keys Windows itself keeps (the Windows
/// key, Alt+Tab, Ctrl+Alt+Supr) stay on this PC.</summary>
internal sealed class PeerScreenWindow : Window
{
    private readonly System.Windows.Controls.Image _image = new()
    {
        Stretch = Stretch.Uniform,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        VerticalAlignment = System.Windows.VerticalAlignment.Center,
    };
    private readonly TextBlock _status = new()
    {
        Foreground = Brushes.White, FontSize = 15, Opacity = 0.8,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center,
    };
    private readonly Border _toolbar;
    private readonly Button _controlButton;
    private readonly TextBlock _fpsText = new() { Foreground = Brushes.White, Opacity = 0.7, VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0) };
    private readonly string _peerName;
    private readonly Action _onUserClose;
    private readonly Action<bool> _onControlToggle;
    private readonly Action<PeerInput> _sendInput;
    private readonly DispatcherTimer _moveTimer;
    private readonly DispatcherTimer _fpsTimer;
    private readonly HashSet<int> _keysDown = new();
    private byte[]? _pending;
    private int _drawScheduled;
    private int _framesThisSecond;
    private bool _closedByPeer;
    private bool _controlling;
    private bool _controlRequested;
    private (double X, double Y)? _pendingMove;
    private WindowState _stateBeforeFullScreen = WindowState.Normal;

    public PeerScreenWindow(string peerName, Action onUserClose, Action<bool> onControlToggle, Action<PeerInput> sendInput)
    {
        _peerName = peerName;
        _onUserClose = onUserClose;
        _onControlToggle = onControlToggle;
        _sendInput = sendInput;
        Title = $"Pantalla de {peerName} — NotiLink";
        Width = 1280;
        Height = 800;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _status.Text = $"Esperando la pantalla de {peerName}…";

        _controlButton = ToolbarButton("🖱️ Controlar", (_, _) => ToggleControl());
        var fullScreen = ToolbarButton("⛶ Pantalla completa", (_, _) => ToggleFullScreen());
        _toolbar = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 22, 32)),
            Padding = new Thickness(8, 5, 8, 5),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _controlButton, fullScreen, _fpsText } },
        };
        DockPanel.SetDock(_toolbar, Dock.Top);
        var surface = new Grid { Background = Brushes.Black, Children = { _image, _status } };
        Content = new DockPanel { LastChildFill = true, Children = { _toolbar, surface } };

        _moveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(8), DispatcherPriority.Input, (_, _) => FlushMove(), Dispatcher);
        _fpsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            _fpsText.Text = $"{_framesThisSecond} fps";
            _framesThisSecond = 0;
        }, Dispatcher);
        _fpsTimer.Start();

        _image.MouseMove += OnMouseMove;
        _image.MouseDown += OnMouseDown;
        _image.MouseUp += OnMouseUp;
        _image.MouseWheel += OnMouseWheel;
        PreviewKeyDown += OnKey;
        PreviewKeyUp += OnKey;
        Deactivated += (_, _) => ReleaseKeys();
        MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2 && !_controlling) ToggleFullScreen(); };
        Closed += (_, _) =>
        {
            _moveTimer.Stop();
            _fpsTimer.Stop();
            if (!_closedByPeer) _onUserClose();
        };
    }

    private static Button ToolbarButton(string text, RoutedEventHandler onClick)
    {
        var b = new Button
        {
            Content = text, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 3, 10, 3),
            Foreground = Brushes.White, Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(52, 46, 72)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(124, 92, 255)), Focusable = false,
        };
        b.Click += onClick;
        return b;
    }

    /// <summary>Any thread. Keeps only the latest frame and draws it on the UI thread.</summary>
    public void PushFrame(byte[] jpeg)
    {
        Volatile.Write(ref _pending, jpeg);
        if (Interlocked.Exchange(ref _drawScheduled, 1) == 1) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            Interlocked.Exchange(ref _drawScheduled, 0);
            var bytes = Interlocked.Exchange(ref _pending, null);
            if (bytes == null) return;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                _image.Source = bmp;
                _status.Visibility = Visibility.Collapsed;
                _framesThisSecond++;
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException) { /* a bad frame — wait for the next */ }
        });
    }

    /// <summary>The other PC granted (true) or ended (false) control.</summary>
    public void SetControlling(bool controlling)
    {
        if (!controlling) ReleaseKeys(); // while _controlling is still true, so the key-ups go out
        _controlling = controlling;
        _controlRequested = false;
        _controlButton.Content = controlling ? "⏹ Dejar de controlar" : "🖱️ Controlar";
        _controlButton.Background = new SolidColorBrush(controlling
            ? System.Windows.Media.Color.FromRgb(140, 40, 40)
            : System.Windows.Media.Color.FromRgb(52, 46, 72));
        _image.Cursor = controlling ? Cursors.Arrow : null;
        if (controlling) { _moveTimer.Start(); Activate(); Focus(); }
        else { _moveTimer.Stop(); _pendingMove = null; }
        Title = controlling ? $"Controlando {_peerName} — NotiLink" : $"Pantalla de {_peerName} — NotiLink";
    }

    /// <summary>The control request was rejected.</summary>
    public void ControlDenied()
    {
        _controlRequested = false;
        _controlButton.Content = "🖱️ Controlar";
    }

    /// <summary>The sharing PC ended it (or the link dropped) — close without telling it again.</summary>
    public void CloseFromPeer()
    {
        _closedByPeer = true;
        Close();
    }

    private void ToggleControl()
    {
        if (_controlling) { _onControlToggle(false); return; }
        if (_controlRequested) return;
        _controlRequested = true;
        _controlButton.Content = "⏳ Esperando permiso…";
        _onControlToggle(true);
    }

    // ---- input forwarding

    private (double X, double Y)? Normalize(MouseEventArgs e)
    {
        if (_image.ActualWidth <= 0 || _image.ActualHeight <= 0) return null;
        var p = e.GetPosition(_image);
        return (Math.Clamp(p.X / _image.ActualWidth, 0, 1), Math.Clamp(p.Y / _image.ActualHeight, 0, 1));
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_controlling) return;
        _pendingMove = Normalize(e);
    }

    private void FlushMove()
    {
        if (_pendingMove is not { } m) return;
        _pendingMove = null;
        _sendInput(new PeerInput("move", m.X, m.Y));
    }

    private static string? ButtonName(MouseButton b) => b switch
    {
        MouseButton.Left => "left",
        MouseButton.Right => "right",
        MouseButton.Middle => "middle",
        MouseButton.XButton1 => "x1",
        MouseButton.XButton2 => "x2",
        _ => null,
    };

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_controlling || Normalize(e) is not { } p || ButtonName(e.ChangedButton) is not { } button) return;
        e.Handled = true;
        _pendingMove = null;
        _image.CaptureMouse(); // keep receiving moves/ups while dragging outside the window
        _sendInput(new PeerInput("down", p.X, p.Y, button));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_controlling || Normalize(e) is not { } p || ButtonName(e.ChangedButton) is not { } button) return;
        e.Handled = true;
        FlushMove();
        _sendInput(new PeerInput("up", p.X, p.Y, button));
        if (e.LeftButton == MouseButtonState.Released && e.RightButton == MouseButtonState.Released && e.MiddleButton == MouseButtonState.Released)
            _image.ReleaseMouseCapture();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_controlling) return;
        e.Handled = true;
        FlushMove();
        _sendInput(new PeerInput("wheel", Delta: e.Delta));
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var down = e.RoutedEvent == PreviewKeyDownEvent;
        var mods = Keyboard.Modifiers;
        if (down && (mods & (ModifierKeys.Control | ModifierKeys.Alt)) == (ModifierKeys.Control | ModifierKeys.Alt))
        {
            if (key == Key.Enter) { e.Handled = true; ToggleFullScreen(); return; }
            if (key == Key.Q && _controlling) { e.Handled = true; _onControlToggle(false); return; }
        }
        if (!_controlling)
        {
            if (down && key == Key.F11) { e.Handled = true; ToggleFullScreen(); }
            else if (down && key == Key.Escape && WindowStyle == WindowStyle.None) { e.Handled = true; ToggleFullScreen(); }
            return;
        }
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk <= 0 || key is Key.ImeProcessed or Key.DeadCharProcessed) return;
        e.Handled = true; // never acted on here (Alt+F4 included) — it's for the other PC
        if (down) _keysDown.Add(vk); else _keysDown.Remove(vk);
        _sendInput(new PeerInput("key", Vk: vk, Down: down));
    }

    /// <summary>Lifts every key still held on the other PC (focus left this window mid-shortcut).</summary>
    private void ReleaseKeys()
    {
        if (!_controlling) { _keysDown.Clear(); return; }
        foreach (var vk in _keysDown.ToList()) _sendInput(new PeerInput("key", Vk: vk, Down: false));
        _keysDown.Clear();
    }

    private void ToggleFullScreen()
    {
        if (WindowStyle == WindowStyle.None)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _stateBeforeFullScreen;
            _toolbar.Visibility = Visibility.Visible;
        }
        else
        {
            _stateBeforeFullScreen = WindowState;
            WindowState = WindowState.Normal; // re-maximizing below is what covers the taskbar too
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            _toolbar.Visibility = Visibility.Collapsed;
        }
    }
}
