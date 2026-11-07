using System.Windows;
using System.Windows.Controls;

namespace Hdlss.App.Views;

public sealed record GuideChapter(string Title, string Body);

public partial class GuideDialog : Window
{
    public GuideDialog()
    {
        InitializeComponent();
        Chapters.ItemsSource = BuildChapters();
        Chapters.SelectedIndex = 0;
    }

    private void Chapters_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Chapters.SelectedItem is not GuideChapter c) return;
        ChapterTitle.Text = c.Title;
        ChapterBody.Text = c.Body;
    }

    private const int ChapterCount = 12;

    private static GuideChapter[] BuildChapters() =>
        [.. Enumerable.Range(1, ChapterCount).Select(i => new GuideChapter(Loc.T($"guide.{i}.title"), Loc.T($"guide.{i}.body")))];
}
