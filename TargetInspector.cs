using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;

namespace NarrowVision;

internal enum TargetKind { Unknown, Background, Action }

internal sealed record TargetDetails(TargetKind Kind, string Name, string Role, Rect Bounds, int[] Id)
{
    public static readonly TargetDetails Unknown = new(TargetKind.Unknown, "", "", Rect.Empty, []);

    public bool Matches(TargetDetails other)
        => Kind == other.Kind && Name == other.Name && Role == other.Role &&
           Bounds == other.Bounds && Id.SequenceEqual(other.Id);
}

internal sealed class TargetInspector
{
    private int _working;

    public async Task<TargetDetails> InspectAsync(ClickTarget target, int timeoutMs = 420)
    {
        // An unresponsive accessibility provider can delay a call beyond our UI deadline.
        // Keep at most one outstanding worker, even after a timeout.
        if (Interlocked.CompareExchange(ref _working, 1, 0) != 0)
            return TargetDetails.Unknown;

        Task<TargetDetails> work = Task.Run(() =>
        {
            try { return Inspect(target.Position); }
            catch (Exception) { return TargetDetails.Unknown; }
            finally { Volatile.Write(ref _working, 0); }
        });
        try
        {
            return await work.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }
        catch (TimeoutException)
        {
            return TargetDetails.Unknown;
        }
    }

    private static TargetDetails Inspect(Native.Point point)
    {
        var element = AutomationElement.FromPoint(new Point(point.X, point.Y));
        if (element is null || element.Current.ProcessId == Environment.ProcessId)
            return TargetDetails.Unknown;

        var initial = element;
        for (int depth = 0; element is not null && depth < 10; depth++)
        {
            var type = element.Current.ControlType;
            if (IsAction(type) || element.TryGetCurrentPattern(InvokePattern.Pattern, out _) ||
                element.TryGetCurrentPattern(TogglePattern.Pattern, out _) ||
                element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _))
                return Describe(element, TargetKind.Action);

            if (type == ControlType.Document || type == ControlType.Window)
                break;
            element = TreeWalker.RawViewWalker.GetParent(element);
        }

        var initialType = initial.Current.ControlType;
        if (initialType == ControlType.Text || initialType == ControlType.Edit ||
            initialType == ControlType.Document)
            return Describe(initial, TargetKind.Background);
        return TargetDetails.Unknown;
    }

    private static bool IsAction(ControlType type) =>
        type == ControlType.Hyperlink || type == ControlType.Button || type == ControlType.MenuItem ||
        type == ControlType.CheckBox || type == ControlType.RadioButton || type == ControlType.SplitButton ||
        type == ControlType.ComboBox || type == ControlType.ListItem || type == ControlType.TreeItem ||
        type == ControlType.TabItem || type == ControlType.DataItem || type == ControlType.Thumb ||
        type == ControlType.Slider;

    private static TargetDetails Describe(AutomationElement element, TargetKind kind)
    {
        var current = element.Current;
        return new(kind, SafeLabel(current.Name), SafeLabel(current.LocalizedControlType, 50),
            current.BoundingRectangle, element.GetRuntimeId());
    }

    public static string SafeLabel(string? value, int maxLength = 160)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var text = new StringBuilder();
        foreach (char character in value)
        {
            if (text.Length >= maxLength)
            {
                text.Append('…');
                break;
            }
            if (char.IsWhiteSpace(character))
            {
                if (text.Length > 0 && text[^1] != ' ')
                    text.Append(' ');
            }
            else if (!char.IsControl(character) && char.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.Format)
                text.Append(character);
        }
        return text.ToString().Trim();
    }
}
