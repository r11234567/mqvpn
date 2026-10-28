using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace TruckersMP.Proxy;

internal sealed class LogView
{
    private readonly record struct RunRecord(Run InlineRun, int LineCount, LogSeverity Severity);

    private static readonly SolidColorBrush DarkTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)));
    private static readonly SolidColorBrush LightTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A)));
    private static readonly SolidColorBrush DarkMutedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)));
    private static readonly SolidColorBrush LightMutedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)));
    private static readonly SolidColorBrush DarkAmberBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)));
    private static readonly SolidColorBrush LightAmberBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09)));
    private static readonly SolidColorBrush DarkRedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)));
    private static readonly SolidColorBrush LightRedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)));

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush GetSeverityBrush(LogSeverity severity, bool isDark)
    {
        return severity switch
        {
            LogSeverity.Error => isDark ? DarkRedBrush : LightRedBrush,
            LogSeverity.Warning => isDark ? DarkAmberBrush : LightAmberBrush,
            LogSeverity.Debug => isDark ? DarkMutedBrush : LightMutedBrush,
            _ => isDark ? DarkTextBrush : LightTextBrush,
        };
    }

    private readonly RichTextBox _box;
    private readonly Paragraph _paragraph;
    private readonly int _maximumLines;
    private readonly StringBuilder _runText = new(8192);
    private readonly Queue<RunRecord> _runs = new();
    private LogSeverity _runSeverity = LogSeverity.Info;
    private int _pendingLines;
    private bool _batching;
    private bool _stickToBottom = true;
    private int _lines;

    public LogView(RichTextBox box, int maximumLines)
    {
        _box = box;
        _maximumLines = maximumLines;
        _paragraph = new Paragraph
        {
            Margin = new Thickness(0),
            LineHeight = 18,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"),
            FontSize = 12,
        };
        _box.Document = new FlowDocument(_paragraph)
        {
            PagePadding = new Thickness(10, 8, 10, 8),
            PageWidth = 2400,
        };
    }

    public void Append(LogEntry entry, LogSeverity severity)
    {
        if (!_batching)
        {
            _batching = true;
            _stickToBottom = IsScrolledToBottom();
        }
        else if (severity != _runSeverity)
        {
            FlushRun();
        }

        _runSeverity = severity;
        _runText.Append(entry.Timestamp.ToString("HH:mm:ss"))
            .Append(" [")
            .Append(entry.Source)
            .Append("] ")
            .Append(entry.Message)
            .Append('\n');
        _pendingLines++;
        _lines++;
    }

    public void RefreshThemeColors(bool isDark)
    {
        foreach (var record in _runs)
        {
            record.InlineRun.Foreground = GetSeverityBrush(record.Severity, isDark);
        }
    }

    public void EndBatch()
    {
        if (!_batching)
            return;

        _batching = false;
        FlushRun();
        Trim();

        if (_stickToBottom)
            _box.ScrollToEnd();
    }

    public void Clear()
    {
        _runText.Clear();
        _pendingLines = 0;
        _lines = 0;
        _batching = false;
        _runs.Clear();
        _paragraph.Inlines.Clear();
    }

    private void FlushRun()
    {
        if (_runText.Length == 0)
            return;

        var isDark = FluentUi.CurrentTheme == ApplicationTheme.Dark;
        var brush = GetSeverityBrush(_runSeverity, isDark);

        var inline = new Run(_runText.ToString())
        {
            Foreground = brush,
        };
        _paragraph.Inlines.Add(inline);
        _runs.Enqueue(new RunRecord(inline, _pendingLines, _runSeverity));
        _runText.Clear();
        _pendingLines = 0;
    }

    private void Trim()
    {
        if (_lines <= _maximumLines)
            return;

        var targetLines = _maximumLines * 3 / 4;
        while (_lines > targetLines && _runs.Count > 1)
        {
            var oldest = _runs.Dequeue();
            _paragraph.Inlines.Remove(oldest.InlineRun);
            _lines -= oldest.LineCount;
        }
    }

    private bool IsScrolledToBottom()
    {
        if (_box.ExtentHeight <= _box.ViewportHeight)
            return true;
        return _box.VerticalOffset + _box.ViewportHeight >= _box.ExtentHeight - 32;
    }
}
