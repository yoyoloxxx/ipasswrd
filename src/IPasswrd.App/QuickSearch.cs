using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using IPasswrd.Core;

namespace IPasswrd.App;

// Global quick search: one keystroke from anywhere in Windows to the password you need.
//
// This is the thing people leave a password manager FOR. Without it the flow is: find the
// window, unlock, search, copy, switch back — five actions to paste one string. With it:
// Ctrl+Shift+Space, three letters, Enter.
//
// Filling the field directly would be nicer still, but Windows has no sanctioned way to type
// into another application's window that is not also how a keylogger works. So the honest
// version is: put it on the clipboard, and wipe the clipboard on the timer the user already set.
public partial class MainWindow
{
    private const int HotkeyId = 0xA17;
    private const uint WmHotkey = 0x0312, WmQuit = 0x0012;
    private const uint ModControl = 0x0002, ModShift = 0x0004, ModNoRepeat = 0x4000;
    private const uint VkSpace = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int ptX, ptY;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern int GetMessage(out Msg msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    
    private QuickSearchWindow? _quick;
    private Thread? _hotkeyThread;
    private uint _hotkeyThreadId;

    /// <summary>
    /// Claim Ctrl+Shift+Space system-wide.
    ///
    /// The hotkey lives on its own thread with its own message loop, and registers against a null
    /// window so the message lands in that thread's queue. The alternative — a private window
    /// class in the UI thread — put hand-marshalled Win32 structures next to Avalonia's own
    /// window handling and crashed the app on open. This way the worst a mistake here can do is
    /// lose the shortcut.
    ///
    /// Failure is silent by design: another program may already own the combination, and that is
    /// not worth a dialog at startup.
    /// </summary>
    private void SetupGlobalHotkey()
    {
        if (_hotkeyThread is not null) return;
        try
        {
            _hotkeyThread = new Thread(HotkeyPump) { IsBackground = true, Name = "IPasswrd hotkey" };
            _hotkeyThread.Start();
        }
        catch { _hotkeyThread = null; }
    }

    private void HotkeyPump()
    {
        try
        {
            _hotkeyThreadId = GetCurrentThreadId();   // объявлена в BrowserBridge.cs, класс частичный
            if (!RegisterHotKey(IntPtr.Zero, HotkeyId, ModControl | ModShift | ModNoRepeat, VkSpace)) return;

            try
            {
                while (GetMessage(out Msg msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.message == WmHotkey && msg.wParam.ToInt32() == HotkeyId)
                        Avalonia.Threading.Dispatcher.UIThread.Post(OpenQuickSearch);
                }
            }
            finally { UnregisterHotKey(IntPtr.Zero, HotkeyId); }
        }
        catch { /* the shortcut is a convenience; never take the app down with it */ }
    }

    private void ReleaseGlobalHotkey()
    {
        try
        {
            if (_hotkeyThreadId != 0) PostThreadMessage(_hotkeyThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }
        catch { /* shutting down anyway */ }
        finally { _hotkeyThread = null; _hotkeyThreadId = 0; }
    }

    private void OpenQuickSearch()
    {
        _lastActivity = DateTimeOffset.UtcNow;

        if (_quick is not null) { try { _quick.Activate(); } catch { /* ignore */ } return; }

        // A locked vault has nothing to search. Surfacing the main window is the useful answer:
        // the person pressed the shortcut because they want a password now.
        if (_vault is null) { ShowFromTray(); return; }

        _quick = new QuickSearchWindow(this);
        _quick.Closed += (_, _) => _quick = null;
        _quick.Show();
        _quick.Activate();
    }

    /// <summary>Entries the quick search offers, newest-looking first. Only things with something to copy.</summary>
    internal List<(string Id, string Title, string Subtitle, string Password, string Username)> QuickSearchRows(string query)
    {
        if (_vault is null) return new();
        string q = query.Trim();

        var rows = _vault.Items()
            .Where(x => x.Item.Type is "account" or "card" or "note" or "doc" or "identity")
            .Select(x => (
                Id: x.Id,
                Title: HeaderName(x.Item),
                Subtitle: x.Item.Fields.GetValueOrDefault("username", ""),
                Password: x.Item.Fields.GetValueOrDefault("password", ""),
                Username: x.Item.Fields.GetValueOrDefault("username", ""),
                Url: x.Item.Fields.GetValueOrDefault("url", ""),
                Folder: x.Item.Folder))
            .Where(r => r.Password.Length > 0 || r.Username.Length > 0);

        if (q.Length > 0)
        {
            rows = rows.Where(r =>
                r.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || r.Subtitle.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || r.Url.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || r.Folder.Contains(q, StringComparison.CurrentCultureIgnoreCase));

            // A title that starts with what was typed is almost always the one meant.
            rows = rows.OrderBy(r => r.Title.StartsWith(q, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
                       .ThenBy(r => r.Title, StringComparer.CurrentCulture);
        }
        else
        {
            rows = rows.OrderBy(r => r.Title, StringComparer.CurrentCulture);
        }

        return rows.Take(8).Select(r => (r.Id, r.Title, r.Subtitle, r.Password, r.Username)).ToList();
    }

    internal async Task QuickCopyAsync(string value)
    {
        try { if (Clipboard is { } cb) await cb.SetTextAsync(value); } catch { /* ignore */ }
        ScheduleClipboardClear(value);
    }

    internal IBrush QsBg => Bg;
    internal IBrush QsSurface => Surface;
    internal IBrush QsText => Text;
    internal IBrush QsText2 => Text2;
    internal IBrush QsText3 => Text3;
    internal IBrush QsAccent => Accent;
    internal IBrush QsHair => HairStrong;
    internal string QsTr(string ru) => Tr(ru);
}

/// <summary>
/// The overlay itself: a search box and up to eight results, no chrome, closes the moment it
/// loses focus. Deliberately not a second copy of the vault window — if you need more than a
/// name and a password, the real window is one more keystroke away.
/// </summary>
internal sealed class QuickSearchWindow : Window
{
    private readonly MainWindow _owner;
    private readonly TextBox _box;
    private readonly ListBox _list;
    private readonly TextBlock _hint;
    private List<(string Id, string Title, string Subtitle, string Password, string Username)> _rows = new();

    public QuickSearchWindow(MainWindow owner)
    {
        _owner = owner;

        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        _box = new TextBox
        {
            Watermark = _owner.QsTr("Название, логин или сайт"),
            FontSize = 17,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(16, 14),
            Foreground = _owner.QsText,
        };
        _box.TextChanged += (_, _) => Refresh();
        _box.KeyDown += OnKey;

        _list = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            MaxHeight = 340,
            // Avalonia вызывает фабрику шаблона и с пустым элементом — без проверки это
            // разыменование null посреди отрисовки списка, то есть падение всего приложения.
            ItemTemplate = new FuncDataTemplate<Row?>((r, _) =>
            {
                var sp = new StackPanel { Margin = new Thickness(4, 3) };
                if (r is null) return sp;
                sp.Children.Add(new TextBlock { Text = r.Title, Foreground = _owner.QsText, FontWeight = FontWeight.SemiBold, FontSize = 13.5 });
                if (r.Subtitle.Length > 0)
                    sp.Children.Add(new TextBlock { Text = r.Subtitle, Foreground = _owner.QsText3, FontSize = 11.5 });
                return sp;
            }),
        };
        _list.KeyDown += OnKey;
        _list.DoubleTapped += (_, _) => CopyAndClose(password: true);

        _hint = new TextBlock
        {
            Text = _owner.QsTr("Enter — пароль · Tab — логин · Esc — закрыть"),
            Foreground = _owner.QsText3, FontSize = 11.5, Margin = new Thickness(16, 0, 16, 12),
        };

        var stack = new StackPanel();
        stack.Children.Add(_box);
        stack.Children.Add(new Border { Height = 1, Background = _owner.QsHair });
        stack.Children.Add(_list);
        stack.Children.Add(_hint);

        Content = new Border
        {
            Background = _owner.QsSurface,
            BorderBrush = _owner.QsHair,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = stack,
            ClipToBounds = true,
        };

        Opened += (_, _) => { _box.Focus(); Refresh(); };
        Deactivated += (_, _) => Close();   // clicking away should dismiss, like every other launcher
    }

    private sealed record Row(string Id, string Title, string Subtitle);

    private void Refresh()
    {
        // Ни одна ошибка поиска не стоит того, чтобы уронить приложение вместе с открытым сейфом.
        try
        {
            _rows = _owner.QuickSearchRows(_box.Text ?? "");
            _list.ItemsSource = _rows.Select(r => new Row(r.Id, r.Title, r.Subtitle)).ToList();
            _list.IsVisible = _rows.Count > 0;
            if (_rows.Count > 0) _list.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            Program.LogCrash(ex, "quick-search");
            _rows = new();
            _list.ItemsSource = null;
            _list.IsVisible = false;
        }
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Close();
                break;

            case Key.Enter:
                e.Handled = true;
                CopyAndClose(password: true);
                break;

            case Key.Tab:
                e.Handled = true;
                CopyAndClose(password: false);
                break;

            case Key.Down:
                e.Handled = true;
                if (_rows.Count > 0) _list.SelectedIndex = Math.Min(_list.SelectedIndex + 1, _rows.Count - 1);
                break;

            case Key.Up:
                e.Handled = true;
                if (_rows.Count > 0) _list.SelectedIndex = Math.Max(_list.SelectedIndex - 1, 0);
                break;
        }
    }

    private async void CopyAndClose(bool password)
    {
        int i = _list.SelectedIndex;
        if (i < 0 || i >= _rows.Count) return;

        string value = password ? _rows[i].Password : _rows[i].Username;
        if (value.Length == 0) return;

        await _owner.QuickCopyAsync(value);
        Close();
    }
}
