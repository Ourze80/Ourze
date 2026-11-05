using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Hdlss.Core.Components;
using Hdlss.Core.Games;

namespace Hdlss.App.ViewModels;

public sealed partial class GameItemViewModel(Game game) : ObservableObject
{
    private ImageSource? _cover;
    private bool _coverLoaded;

    public Game Game { get; } = game;
    public string Name => Game.Name;
    public string StoreLabel => Game.StoreLabel;
    public string Initial => Game.Name.Length > 0 ? Game.Name[..1].ToUpperInvariant() : "?";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(StatusBrush), nameof(IsActive), nameof(NeedsAttention))]
    private GameSummary _summary = GameSummary.Checking;

    public string StatusText => Summary.Text;
    public bool IsActive => Summary.Badge == GameBadge.NrActive;
    public bool NeedsAttention => Summary.Badge is GameBadge.Legacy or GameBadge.Foreign or GameBadge.NoExe;

    public Brush StatusBrush => (Brush)Application.Current.Resources[Summary.Badge switch
    {
        GameBadge.NrActive => "Success",                                  // active: green
        GameBadge.Legacy or GameBadge.Foreign or GameBadge.NoExe => "Accent", // to look at: brand cyan
        GameBadge.AntiCheat => "Danger",
        _ => "Muted",
    }];

    public ImageSource? Cover
    {
        get
        {
            if (_coverLoaded) return _cover;
            _coverLoaded = true;
            _cover = Images.Load(Game.Cover, 300);
            return _cover;
        }
    }
}

internal static class Images
{
    /// <summary>Decodes a thumbnail without locking the file; returns null on any failure.</summary>
    public static ImageSource? Load(string? path, int decodeWidth)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = decodeWidth;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            return null;
        }
    }
}
