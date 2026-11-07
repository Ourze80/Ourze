using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Hdlss.Core.Components;
using Hdlss.Core.Games;

namespace Hdlss.App.ViewModels;

public sealed partial class GameItemViewModel(Game game, CoverService? covers = null) : ObservableObject
{
    private ImageSource? _cover;
    private bool _coverLoaded;
    private ImageSource? _exeIcon;
    private bool _exeIconLoaded;
    private int _generation;

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

    /// <summary>Custom cover, store artwork or automatic cover (in that order); null when there is none.</summary>
    public ImageSource? Cover
    {
        get
        {
            if (_coverLoaded) return _cover;
            _coverLoaded = true;
            _cover = Images.Load(CoverPath, 300);
            return _cover;
        }
    }

    public string? CoverPath => covers != null ? covers.Current(Game) : Game.Cover;

    /// <summary>Icon of the game's exe, shown when there is no cover at all. Loaded in the background.</summary>
    public ImageSource? ExeIcon
    {
        get
        {
            if (_exeIconLoaded) return _exeIcon;
            if (Cover != null || !Game.HasExe) return null;   // exe not resolved yet: asked again later
            _exeIconLoaded = true;
            var exe = Game.Exe;
            var generation = _generation;
            _ = Task.Run(() => Images.ExeIcon(exe)).ContinueWith(t =>
            {
                if (generation != _generation || t.IsFaulted) return;
                _exeIcon = t.Result;
                OnPropertyChanged(nameof(ExeIcon));
                OnPropertyChanged(nameof(ShowInitial));
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return null;
        }
    }

    public bool ShowInitial => Cover == null && (ExeIcon == null);

    /// <summary>Reloads the tile image after a custom or automatic cover changed.</summary>
    public void RefreshCover()
    {
        _coverLoaded = false;
        _cover = null;
        _exeIconLoaded = false;
        _exeIcon = null;
        _generation++;
        OnPropertyChanged(nameof(Cover));
        OnPropertyChanged(nameof(ExeIcon));
        OnPropertyChanged(nameof(ShowInitial));
    }

    partial void OnSummaryChanged(GameSummary value)
    {
        // The exe is resolved together with the first summary: the icon fallback may now be available.
        if (_cover == null && !_exeIconLoaded)
        {
            OnPropertyChanged(nameof(ExeIcon));
            OnPropertyChanged(nameof(ShowInitial));
        }
    }
}

internal static class Images
{
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [System.Runtime.InteropServices.PreserveSig]
        int GetImage(long size, int flags, out IntPtr bitmap);   // SIZE passed by value as 2 ints packed in a long
    }

    /// <summary>The exe's icon: 256 px from the shell when available, else its first 32 px icon (read-only); null on failure.</summary>
    public static ImageSource? ExeIcon(string exe)
    {
        var big = LargeExeIcon(exe);
        if (big != null) return big;
        var large = new IntPtr[1];
        try
        {
            if (ExtractIconEx(exe, 0, large, null, 1) == 0 || large[0] == IntPtr.Zero) return null;
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
        finally { if (large[0] != IntPtr.Zero) DestroyIcon(large[0]); }
    }

    private static ImageSource? LargeExeIcon(string exe)
    {
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            SHCreateItemFromParsingName(exe, IntPtr.Zero, ref iid, out var factory);
            const long size = 256L | (256L << 32);
            const int iconOnly = 0x4, bigger = 0x1;   // SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK
            if (factory.GetImage(size, iconOnly | bigger, out hbitmap) != 0 || hbitmap == IntPtr.Zero) return null;
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or InvalidCastException)
        {
            return null;
        }
        finally { if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap); }
    }

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
