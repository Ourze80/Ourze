<img src="assets/brand-logo.png" alt="HDLSS5" height="64">

# HDLSS5 2.0 · anteprima

> **Versione di anteprima.** La versione stabile resta la [1.5.1 sul ramo `main`](https://github.com/giorgiobascialla-hub/Ourze/tree/main).
> La 2.0 si scarica dalla pre-release [v2.0.0-preview](https://github.com/giorgiobascialla-hub/Ourze/releases): estrai lo zip in una cartella scrivibile e avvia `HDLSS5.exe` (Windows x64, nessun runtime da installare). Interfaccia per ora solo in italiano.

Riscrittura da zero di HDLSS5 1.5 in **.NET 8 + WPF (MVVM)**. Stessi percorsi Neural Rendering, architettura nuova.

## Struttura

```
src/Hdlss.Core     logica senza UI (testabile)
  IO/              PathGuard, FileHash (cache), PeFile, IniDocument, Archives
  Storage/         AppPaths, ContentStore (backup per hash), JsonFile
  Transactions/    PlanBuilder, RemovalPlanner, TransactionEngine, Maintenance
  Games/           GameLibrary (Steam + manuali), ExecutableDetector (exe, motore, API)
  Hardware/        GpuInfo (nvidia-smi)
  Net/             HttpService (ETag GitHub, cache, download verificati)
  Components/      ReShade, RenoDX NR, Feeder, OptiScaler, Modello NR, Vulkan, regole NR
  Legacy/          lettura e ripristino delle installazioni 1.x
src/Hdlss.App      WPF: AppHost, ViewModels, Views, i18n (it/en)
tests/             xUnit (motore, INI, percorsi, 1.x)
bundled/           add-on RenoDX NR inclusi (verificati via SHA-256, non più dentro l'exe)
```

## Cosa cambia rispetto alla 1.x

| 1.x | 2.0 |
|---|---|
| Ogni componente reimplementa Add/Prepare/Restore (35 varianti) | Un solo `PlanBuilder` + rimozione generica dal registro |
| Backup e copia di recupero completi a ogni operazione (`dlss-recovery` ~20 GB) | Archivio per contenuto: ogni file salvato una volta; pulizia automatica |
| INI ripristinati per intero o con codice ad hoc | Modifiche INI semantiche: si annulla solo ciò che l'app ha cambiato |
| `catch {}` silenziosi | Errori tipizzati, tutto nel registro attività |
| UI costruita nel code-behind, testi in 6 lingue inline | XAML + MVVM, stringhe in `i18n/*.json` |
| `WebClient`, richieste GitHub non cache | `HttpClient` condiviso, ETag, copia locale offline |
| DLL incluse come risorse nell'exe | File in `bundled/` verificati all'uso |
| Marker json nella cartella del gioco | Stato in `data-v2/state`, cronologia annullabile |

## Compilare e testare

```bash
dotnet build HDLSS5.sln -c Release
```

```bash
dotnet test tests/Hdlss.Core.Tests
```

Pubblicazione portatile: `dotnet publish src/Hdlss.App -c Release -o out`, poi copiare `nr-packages` accanto a `HDLSS5.exe` (oppure indicare la cartella 1.x nelle Impostazioni).

## Interfaccia

- **Barra laterale**: Giochi, DLL, Avanzate, Guida, Impostazioni.
- **Libreria a copertine** con lo stato di ogni gioco in una riga (NR attivo, da portare sulla 2.0, anti-cheat…), filtri e ricerca (Ctrl+F).
- **Pagina del gioco**: una frase chiara su com'è messo il gioco e un solo pulsante principale (Attiva Neural Rendering / Porta sulla 2.0 / Ha funzionato?). Il percorso consigliato tiene conto di API e presenza di DLSS.
- **Ha funzionato?** legge i log del gioco dopo la partita e risponde in italiano semplice.
- **Modalità avanzata**: percorso a scelta, opzioni di ogni componente, anteprima file per file, installazioni 1.x, cronologia.
- **Pagina DLL**: versioni DLSS (SR, FG, RR) e Streamline di ogni gioco contro le più recenti NVIDIA; per ogni gioco un menu sceglie la più recente, una versione precedente o «Originale del gioco»; «Porta tutti alle più recenti» aggiorna in un colpo.
- Esc torna indietro, F5 riscansiona, registro attività in un cassetto in basso.

## Componenti

| Categoria | Componenti |
|---|---|
| Neural Rendering | ReShade, RenoDX NR, Feeder, OptiScaler (F5/Standard), Modello NR |
| DLL del gioco | DLSS (SR/FG/RR) e Streamline alla versione più recente o scelta, con ripristino degli originali |
| HDR | RenoDX HDR (catalogo o file importato), HDR Unreal (Engine.ini, profilo nativo o RenoDX) |
| Generazione fotogrammi | MFG Unlock (RTX 4000) |
| Shader ReShade | Lilium, CAS |
| Loader per mod | UE4SS (+ lettore HDR in gioco), Ultimate ASI Loader, REFramework |

Libreria: Steam, Xbox / Game Pass (anche eseguibili protetti, avvio via `shell:AppsFolder`) e giochi aggiunti a mano.

## Installazioni 1.x

- **Adotta**: la 2.0 prende in gestione l'installazione senza modificare i file del gioco (i backup 1.x entrano nell'archivio 2.0).
- **Ripristina**: riporta i file allo stato originale con i backup della 1.x.

## Non portato dalla 1.x

Overlay/scorciatoia monitor e filtro HDR «PeakLimit» ritirato. Le loro installazioni 1.x restano ripristinabili.
