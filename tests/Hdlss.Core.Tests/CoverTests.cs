using Xunit;
using System.Net;
using Hdlss.Core.Diagnostics;
using Hdlss.Core.Games;
using Hdlss.Core.Net;

namespace Hdlss.Core.Tests;

public sealed class CoverTests : IDisposable
{
    private static readonly byte[] Jpeg =
        [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 8, 0x00, 0x10, 0x00, 0x20, 1, 1, 0x11, 0, 0xFF, 0xD9];
    private static byte[] MakePng(uint w = 2, uint h = 3)
    {
        var b = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        b.AddRange([(byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w, (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h, 8, 2, 0, 0, 0, 1, 2, 3, 4]);
        b.AddRange([0, 0, 0, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0xAE, 0x42, 0x60, 0x82]);
        return [.. b];
    }
    private readonly Fixture _f = new();
    public void Dispose() => _f.Dispose();

    private Game NewGame(string name = "Some Game", string id = "manual:abc") =>
        new() { Id = id, Name = name, Root = Path.Combine(_f.Game, name), Store = GameStore.Manual };

    private sealed class FakeHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request.RequestUri.ToString()));
        }
    }

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private static HttpResponseMessage Bytes(byte[] b) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };

    private (CoverService svc, FakeHandler handler) Make(Func<string, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        return (new CoverService(_f.Paths, new HttpService(_f.Paths, Log.Null, handler), Log.Null), handler);
    }

    [Theory]
    [InlineData("Hades™", "Hades", true)]
    [InlineData("The Witcher 3: Wild Hunt - Game of the Year Edition", "Witcher 3 Wild Hunt", true)]
    [InlineData("CYBERPUNK 2077®", "Cyberpunk 2077", true)]
    [InlineData("Resident Evil 2", "Resident Evil 3", false)]
    [InlineData("Hades", "Hades II", false)]
    [InlineData("Portal", "Portal 2", false)]
    [InlineData("Dark Souls Remastered", "Dark Souls", true)]
    public void Matching(string a, string b, bool expected) => Assert.Equal(expected, CoverMatcher.IsMatch(a, b));

    [Theory]
    [InlineData("Launcher", true)]
    [InlineData("game.exe", true)]
    [InlineData("UnrealGame", true)]
    [InlineData("UE4Game", true)]
    [InlineData("Win64-Shipping", true)]
    [InlineData("Hades", false)]
    public void GenericNames(string name, bool generic) => Assert.Equal(generic, CoverMatcher.IsGeneric(name));

    [Fact]
    public void ShippingSuffixIsStripped() => Assert.Equal("Dying Light 2", CoverMatcher.Clean("Dying Light 2-Win64-Shipping.exe"));

    [Fact]
    public void CandidatesSkipGeneric()
    {
        var game = NewGame("Launcher");
        var list = CoverMatcher.Candidates(game, "UnrealGame");
        Assert.DoesNotContain("UnrealGame", list);
        Assert.DoesNotContain("Launcher", list);
    }

    [Fact]
    public void PickAppRejectsWrongGame()
    {
        const string json = """{"items":[{"type":"app","name":"Resident Evil 3","id":1},{"type":"app","name":"Resident Evil 2 Soundtrack","id":2}]}""";
        Assert.Null(CoverMatcher.PickApp(json, "Resident Evil 2"));
        Assert.Equal(1, CoverMatcher.PickApp(json, "Resident Evil 3"));
    }

    [Fact]
    public void ImageValidationAndCustomCover()
    {
        var (svc, _) = Make(_ => Ok("{}"));
        var game = NewGame();
        var bad = Path.Combine(_f.Root, "bad.jpg");
        File.WriteAllText(bad, "not an image");
        Assert.Throws<HdlssException>(() => svc.SetCustom(game, bad));
        Assert.False(svc.HasCustom(game));

        var good = Path.Combine(_f.Root, "c.jpg");
        File.WriteAllBytes(good, Jpeg);
        var stored = svc.SetCustom(game, good);
        Assert.StartsWith(_f.Paths.Covers, stored);
        Assert.True(svc.HasCustom(game));
        Assert.Equal(stored, svc.Current(game));

        game.Cover = good;   // store art never beats the custom cover
        Assert.Equal(stored, svc.Current(game));
        svc.ClearCustom(game);
        Assert.False(svc.HasCustom(game));
        Assert.Equal(good, svc.Current(game));
    }

    [Fact]
    public async Task ResolveDownloadsCoverOnGoodMatch()
    {
        var (svc, _) = Make(url => url.Contains("storesearch")
            ? Ok("""{"items":[{"type":"app","name":"Some Game","id":42}]}""")
            : Bytes(Jpeg));
        var game = NewGame();
        var path = await svc.ResolveAsync(game, CancellationToken.None);
        Assert.NotNull(path);
        Assert.Equal(path, svc.Current(game));
    }

    [Fact]
    public async Task NegativeCacheStopsRetries()
    {
        var (svc, handler) = Make(_ => Ok("""{"items":[{"type":"app","name":"Other Thing","id":7}]}"""));
        var game = NewGame();
        Assert.Null(await svc.ResolveAsync(game, CancellationToken.None));
        var calls = handler.Urls.Count;
        Assert.True(calls > 0);
        Assert.Null(await svc.ResolveAsync(game, CancellationToken.None));
        Assert.Equal(calls, handler.Urls.Count);

        var key = string.Join("|", CoverMatcher.Candidates(game, null));
        Assert.True(svc.IsNegativeCached(game, key, DateTime.Now));
        Assert.False(svc.IsNegativeCached(game, key, DateTime.Now.AddDays(8)));
    }

    [Fact]
    public async Task NetworkErrorIsNotCachedAndNeverThrows()
    {
        var (svc, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var game = NewGame();
        Assert.Null(await svc.ResolveAsync(game, CancellationToken.None));
        Assert.False(svc.IsNegativeCached(game, string.Join("|", CoverMatcher.Candidates(game, null)), DateTime.Now));
    }

    [Fact]
    public void TruncatedOrEmptyImagesAreRejected()
    {
        Assert.Equal(".jpg", ImageCheck.Validate(Jpeg));
        Assert.Equal(".png", ImageCheck.Validate(MakePng()));
        Assert.Null(ImageCheck.Validate(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10 }));
        Assert.Null(ImageCheck.Validate(Jpeg[..^2]));
        Assert.Null(ImageCheck.Validate(MakePng(0, 3)));
        Assert.Null(ImageCheck.Validate(MakePng()[..^12]));
        var (svc, _) = Make(_ => Ok("{}"));
        var file = Path.Combine(_f.Root, "t.jpg");
        File.WriteAllBytes(file, [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10]);
        Assert.Throws<HdlssException>(() => svc.SetCustom(NewGame(), file));
        Assert.False(svc.HasCustom(NewGame()));
    }

    [Fact]
    public async Task HeroIsDownloadedAndHeroPriority()
    {
        var (svc, handler) = Make(url => url.Contains("storesearch")
            ? Ok("{\"items\":[{\"type\":\"app\",\"name\":\"Some Game\",\"id\":42}]}")
            : Bytes(Jpeg));
        var game = NewGame();
        Assert.Null(svc.CurrentHero(game));
        Assert.NotNull(await svc.ResolveAsync(game, CancellationToken.None));
        var hero = svc.CurrentHero(game);
        Assert.NotNull(hero);
        Assert.Contains("auto-hero-", hero);
        Assert.Contains(handler.Urls, u => u.EndsWith("42/library_hero.jpg"));

        var store = Path.Combine(_f.Root, "hero.jpg");
        File.WriteAllBytes(store, Jpeg);
        game.Hero = store;
        Assert.Equal(store, svc.CurrentHero(game));
        var custom = Path.Combine(_f.Root, "c.jpg");
        File.WriteAllBytes(custom, Jpeg);
        Assert.Equal(svc.SetCustom(game, custom), svc.CurrentHero(game));
    }

    [Fact]
    public async Task ExistingAutoCoverGetsHeroLater()
    {
        bool heroOk = false;
        var (svc, _) = Make(url => url.Contains("storesearch") ? Ok("{\"items\":[{\"type\":\"app\",\"name\":\"Some Game\",\"id\":42}]}")
            : url.EndsWith("library_hero.jpg") && !heroOk ? new HttpResponseMessage(HttpStatusCode.NotFound) : Bytes(Jpeg));
        var game = NewGame();
        Assert.NotNull(await svc.ResolveAsync(game, CancellationToken.None));
        Assert.Null(svc.CurrentHero(game));
        heroOk = true;
        File.Delete(Path.Combine(_f.Paths.Covers, "apps.json"));
        Assert.NotNull(await svc.ResolveAsync(game, CancellationToken.None));
        Assert.NotNull(svc.CurrentHero(game));
    }
}
