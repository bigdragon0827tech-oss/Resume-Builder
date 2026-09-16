using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace ResumeBuilder;

/// <summary>
/// A6.6.13 — the "answer ready" notification on the right side of the screen.
///
/// It never takes focus away from whatever the user is doing: it is shown without activation and
/// marked no-activate. Clicking it is a deliberate user action, which is what brings ResumeBuilder to
/// the front and puts keyboard focus in the ChatGPT pane, so ChatGPT's own copy shortcut works.
/// </summary>
public sealed class ReadyToast : Window {
    const int GWL_EXSTYLE = -20;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    /// <summary>Raised when the user clicks the notification body.</summary>
    public event Action? Clicked;

    public ReadyToast(string title, string detail, string instruction) {
        Width = 340;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0x4E));
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x16, 0x52, 0x3A));
        BorderThickness = new Thickness(1);
        Cursor = Cursors.Hand;

        var close = new Button {
            Content = "✕", Width = 24, Height = 24, Padding = new Thickness(0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = Brushes.White, Cursor = Cursors.Arrow,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top
        };
        close.Click += (_, e) => { e.Handled = true; Close(); };

        var header = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(Text(title, 15, FontWeights.SemiBold));

        var body = new StackPanel { Margin = new Thickness(14, 10, 10, 12), Orientation = Orientation.Vertical };
        body.Children.Add(header);
        body.Children.Add(Text(detail, 12.5, FontWeights.Normal, top: 2));
        body.Children.Add(Text(instruction, 12.5, FontWeights.SemiBold, top: 8));
        Content = body;

        MouseLeftButtonUp += (_, _) => Clicked?.Invoke();
        SourceInitialized += (_, _) => MakeNonActivating();
        Loaded += (_, _) => PlaceOnRightSide();
    }

    static TextBlock Text(string text, double size, FontWeight weight, double top = 0) => new() {
        Text = text, FontSize = size, FontWeight = weight, Foreground = Brushes.White,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0),
        FontFamily = new FontFamily("Segoe UI")
    };

    /// <summary>Right-hand side of the primary work area, just above the taskbar.</summary>
    void PlaceOnRightSide() {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 16;
        Top = area.Bottom - ActualHeight - 16;
    }

    void MakeNonActivating() {
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }
}

/// <summary>Flashes a window's taskbar button until the user brings it to the front.</summary>
public static class WindowAttention {
    [StructLayout(LayoutKind.Sequential)]
    struct FLASHWINFO {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    const uint FLASHW_ALL = 3;
    const uint FLASHW_TIMERNOFG = 12;

    [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO info);

    public static void FlashUntilForeground(Window window) {
        try {
            var info = new FLASHWINFO {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = new WindowInteropHelper(window).Handle,
                dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
                uCount = uint.MaxValue,
                dwTimeout = 0
            };
            FlashWindowEx(ref info);
        } catch {
            // A missing flash is cosmetic; the toast and banner still show.
        }
    }
}
