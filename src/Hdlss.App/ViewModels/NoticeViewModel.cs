using System.Windows.Input;

namespace Hdlss.App.ViewModels;

/// <summary>
/// A warning in three parts: what ("Title"), why and what to do ("Detail"), and an optional button.
/// Core texts follow "Titolo breve: perché. Cosa fare."
/// </summary>
public sealed class NoticeViewModel(string title, string detail = "", string? actionLabel = null, ICommand? action = null)
{
    private const int MaxTitle = 70;

    public string Title { get; } = title;
    public string Detail { get; } = detail.Length > 0 ? char.ToUpper(detail[0], System.Globalization.CultureInfo.CurrentCulture) + detail[1..] : detail;
    public bool HasDetail => Detail.Length > 0;
    public string? ActionLabel { get; } = actionLabel;
    public ICommand? Action { get; } = action;
    public bool HasAction => Action != null && !string.IsNullOrEmpty(ActionLabel);

    /// <summary>Splits at the first ": " when the part before it is short enough to be a title.</summary>
    public static NoticeViewModel FromText(string text, string? actionLabel = null, ICommand? action = null)
    {
        text = text.Trim();
        var i = text.IndexOf(": ", StringComparison.Ordinal);
        return i > 0 && i <= MaxTitle
            ? new NoticeViewModel(text[..i], text[(i + 2)..].Trim(), actionLabel, action)
            : new NoticeViewModel(text, "", actionLabel, action);
    }
}
