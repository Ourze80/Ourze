using System.Text.RegularExpressions;
using Hdlss.Core.IO;

namespace Hdlss.Core.Components;

public enum VerifyLevel { NotRun, Working, Partial, Failed }

public sealed record VerifyResult(VerifyLevel Level, string Title, IReadOnlyList<string> Details);

/// <summary>
/// "Did it work?": reads the logs the game wrote after the last install and says in plain
/// words whether the Neural Rendering pass actually ran, and what to try if it did not.
/// </summary>
public static class Verifier
{
    public static VerifyResult Check(GameContext ctx)
    {
        var path = NrAdvisor.Installed(ctx);
        if (path == null) return new(VerifyLevel.NotRun, "Neural Rendering non è installato su questo gioco.", []);
        var record = ctx.State.Get(NrAdvisor.Components[path.Value].Last())!;
        var since = record.UpdatedAt.AddMinutes(-1);
        var details = new List<string>();

        // A crash report written after the install outranks everything else.
        var crash = ExecutableDetectorFiles(ctx.Folder, "*crash*.log").Select(f => new FileInfo(f))
            .Where(f => f.LastWriteTime >= since).OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
        if (crash != null)
            return new(VerifyLevel.Failed, "No: il gioco è andato in crash con il componente attivo.",
                [$"Rapporto: {crash.Name} ({crash.LastWriteTime:g}).",
                 path == NrPath.Native ? "Prova «Disattiva» e poi il percorso Feeder (Avanzate), oppure aggiorna il driver NVIDIA." : "Prova «Disattiva»: il gioco tornerà com'era. Puoi riprovare con un altro percorso in Avanzate."]);

        var logs = new List<(string Name, string[] Lines)>();
        foreach (var name in LogNames(ctx, path.Value))
        {
            var file = PathGuard.Resolve(ctx.Folder, name);
            if (!File.Exists(file) || File.GetLastWriteTime(file) < since) continue;
            logs.Add((name, ReadShared(file)));
        }
        if (logs.Count == 0)
            return new(VerifyLevel.NotRun, "Non hai ancora giocato dopo l'installazione.",
                ["Avvia il gioco, entra in una scena giocabile per almeno un minuto, chiudilo e premi di nuovo «Ha funzionato?»."]);

        bool loaded = false, ran = false, failed = false;
        foreach (var (name, lines) in logs)
        {
            foreach (var line in lines)
            {
                if (Regex.IsMatch(line, @"(Loaded|Registered).*(renodx-dlss5|dlss5-feed|Neural Rendering|DLSS ?5)", RegexOptions.IgnoreCase)) loaded = true;
                if (Regex.IsMatch(line, @"feature ready|frame delivered|ENGAGED|DlssNr.*(created|init(ialized)?\b.*(ok|success))|Neural Rendering (active|enabled|ready)", RegexOptions.IgnoreCase)) ran = true;
                if (Regex.IsMatch(line, @"(failed to (load|create|initialize)).*(dlss|ngx|neural|add-?on)|NVSDK_NGX_Result_Fail|access violation", RegexOptions.IgnoreCase))
                {
                    failed = true;
                    if (details.Count < 4) details.Add($"{name}: {line.Trim()[..Math.Min(160, line.Trim().Length)]}");
                }
            }
        }

        if (ran && !failed) return new(VerifyLevel.Working, "Sì: il Neural Rendering è partito.", ["Se l'immagine non cambia, attivalo nel menu ReShade (tasto Home) nella scheda dell'add-on."]);
        if (failed)
        {
            details.Insert(0, "Il log riporta un errore del componente:");
            details.Add(path == NrPath.Native
                ? "Prova il percorso Feeder oppure aggiorna il driver NVIDIA. Se il gioco crasha, usa «Disattiva»."
                : "Verifica in ReShade che «Generic Depth» trovi la profondità e che Kernel sia sopra DLSS 5 Feed.");
            return new(VerifyLevel.Failed, "No: il componente si è caricato ma ha dato errore.", details);
        }
        if (loaded) return new(VerifyLevel.Partial, "Quasi: l'add-on è stato caricato, ma non risulta che il modello abbia elaborato fotogrammi.",
            ["Apri il menu ReShade (Home), attiva Neural Rendering nella scheda dell'add-on e gioca qualche minuto.",
             path == NrPath.Feeder ? "Con Feeder controlla anche che gli effetti LUMENITE Kernel e DLSS 5 Feed siano attivi." : "Assicurati che DLSS sia attivo nelle opzioni grafiche del gioco."]);
        return new(VerifyLevel.Failed, "No: il gioco è partito ma il componente non è stato caricato.",
            ["ReShade potrebbe non essersi avviato: controlla che il menu ReShade compaia premendo Home.",
             "Se usi un overlay o un'altra mod che sostituisce dxgi.dll, possono entrare in conflitto."]);
    }

    private static string[] ExecutableDetectorFiles(string dir, string pattern) => Games.ExecutableDetector.SafeFiles(dir, pattern);

    private static IEnumerable<string> LogNames(GameContext ctx, NrPath path)
    {
        yield return "ReShade.log";
        foreach (var loader in ctx.Scan.ReShadeLoaders.Where(l => !l.Contains('/')))
            yield return Path.ChangeExtension(loader, ".log");
        if (path == NrPath.Feeder) yield return "dlss5-feed.log";
        if (path == NrPath.OptiScaler) yield return "OptiScaler.log";
    }

    private static string[] ReadShared(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(Math.Max(0, stream.Length - 400_000), SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n');
    }
}
