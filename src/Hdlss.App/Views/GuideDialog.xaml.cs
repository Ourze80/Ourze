using System.Windows;
using System.Windows.Controls;

namespace Hdlss.App.Views;

public sealed record GuideChapter(string Title, string Body);

public partial class GuideDialog : Window
{
    public GuideDialog()
    {
        InitializeComponent();
        Chapters.ItemsSource = Loc.Instance.Language == "en" ? English : Italian;
        Chapters.SelectedIndex = 0;
    }

    private void Chapters_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Chapters.SelectedItem is not GuideChapter c) return;
        ChapterTitle.Text = c.Title;
        ChapterBody.Text = c.Body;
    }

    private static readonly GuideChapter[] Italian =
    [
        new("Come funziona", "HDLSS5 installa componenti nella cartella dell'eseguibile del gioco. Ogni operazione mostra prima un'anteprima dei file, salva gli originali e verifica il risultato. Se qualcosa va storto, tutto torna com'era.\n\nPrima di ogni modifica chiudi il gioco. I file installati non garantiscono che un effetto sia attivo: verifica sempre nel menu in gioco (ReShade: Home, OptiScaler: Insert) e nei log."),
        new("Scegliere l'eseguibile e l'API", "Il gioco viene associato a un eseguibile x64. Nei giochi Unreal è quello in «Binaries\\Win64». Se l'app sceglie quello sbagliato usa «Eseguibile e API…».\n\nL'API grafica è letta dalle importazioni dell'eseguibile. «(probabile)» indica una deduzione dalla cartella del gioco. Una scelta manuale non aggiunge supporto: seleziona la stessa API anche nelle impostazioni del gioco."),
        new("Neural Rendering · quale percorso", "Con DLSS nativo (DX12): ReShade → RenoDX NR. Per giochi che offrono già DLSS.\n\nSenza DLSS · Feeder: per giochi DX11/DX12/Vulkan senza DLSS. Feeder stima profondità e movimento (LumeniteFX): possono comparire scie o effetti sull'HUD. In gioco verifica Generic Depth e metti LUMENITE: Kernel sopra DLSS 5 Feed; disattiva MSAA/SSAA.\n\nOptiScaler F5: proxy OptiScaler con NR multipass; si configura nel menu Insert.\n\nUsa un solo percorso NR per gioco: l'app blocca le combinazioni."),
        new("Modello NR e GPU", "Il modello 310.9.16.0 viene scelto in base alla GPU: RTX 4000 → variante Compat FP16, RTX 5000 → originale. Serve un driver almeno 616.56. La cartella nr-packages deve stare accanto a HDLSS5.exe o nella cartella della 1.x indicata nelle impostazioni."),
        new("HDR", "RenoDX HDR: mod specifica per il gioco, cercata nel catalogo RenoDX per titolo esatto. Se la mod è su Nexus Mods scaricala e usa «Importa add-on…».\n\nHDR Unreal (Engine.ini): attiva l'HDR del motore nei giochi Unreal senza menu HDR. Avvia il gioco una volta prima, così esistono i file di configurazione. Con UE4SS e il lettore HDLSS la scheda mostra i valori attivi in gioco."),
        new("Generazione fotogrammi", "MFG Unlock abilita la generazione multi-fotogramma su RTX 4000 nei giochi con DLSS Frame Generation nativo. Attiva FG nel gioco, poi scegli il moltiplicatore in ReShade › MFG Unlock."),
        new("Shader e loader", "Lilium (analisi e strumenti HDR) e CAS (nitidezza) vengono scaricati dal repository dell'autore alla revisione corrente e installati in cartelle separate. Se Lilium è installato, CAS è già incluso: attiva «Lilium CAS» nel menu ReShade.\n\nUE4SS (Unreal), Ultimate ASI Loader e REFramework (giochi Capcom RE Engine) sono loader per mod: installali solo quando una mod li richiede."),
        new("DLL dei giochi (DLSS e Streamline)", "La pagina «DLL» elenca per ogni gioco le DLL NVIDIA che contiene: DLSS Super Resolution, Frame Generation, Ray Reconstruction e Streamline (sl.*.dll), con la versione installata e quella più recente.\n\n«Aggiorna» porta il gioco alle versioni più recenti, «Aggiorna tutti» lo fa per tutti i giochi indietro. Gli originali restano salvati: «Ripristina originali» li rimette.\n\nIn modalità avanzata puoi scegliere una versione precisa di DLSS e di Streamline (anche più vecchia). Streamline viene aggiornato solo nei moduli che il gioco usa già, con l'SDK ufficiale NVIDIA.\n\nNei giochi online con anti-cheat è meglio non sostituire le DLL."),
        new("Rimuovere e annullare", "«Rimuovi» riporta i file allo stato precedente all'installazione e annulla solo le impostazioni INI cambiate dall'app: le tue modifiche successive restano.\n\nSe un file è stato modificato da un altro programma, l'app te lo dice e propone «Rimuovi comunque»: quel file resta com'è e il resto viene rimosso.\n\n«Cronologia» elenca le ultime operazioni del gioco e permette di annullarle."),
        new("Installazioni della 1.x", "I giochi modificati da HDLSS5 1.x mostrano un riquadro dedicato.\n\n«Adotta» passa l'installazione sotto la gestione della 2.0 senza toccare i file del gioco: da quel momento puoi aggiornarla o rimuoverla da qui.\n\n«Ripristina» riporta i file allo stato precedente usando i backup della 1.x. La cartella data della 1.x non viene mai modificata."),
        new("Spazio su disco", "Gli originali sono conservati una sola volta per contenuto (un file identico non viene duplicato). All'avvio l'app tiene le ultime 10 operazioni per gioco e quelle dell'ultimo mese, poi elimina i backup non più necessari. La cache dei download è limitata a 2 GB. Puoi lanciare la pulizia da Impostazioni."),
        new("Problemi frequenti", "Il gioco non parte o crasha: rimuovi l'ultimo componente installato (o annullalo da Cronologia) e verifica l'integrità dei file dal launcher.\n\nL'effetto non compare: controlla che il gioco usi l'API giusta, che ReShade si apra con Home e che non ci siano due loader nella cartella.\n\nAnti-cheat: l'app blocca l'installazione nei giochi con EasyAntiCheat/BattlEye."),
    ];

    private static readonly GuideChapter[] English =
    [
        new("How it works", "HDLSS5 installs components into the game executable's folder. Every operation previews the files, saves the originals and verifies the result; on any error everything is rolled back.\n\nClose the game before changing anything. Installed files do not prove an effect is active: check the in-game menu (ReShade: Home, OptiScaler: Insert) and the logs."),
        new("Executable and API", "Each game is linked to an x64 executable (Unreal: Binaries\\Win64). Use «Executable & API…» if the guess is wrong. The graphics API comes from the executable imports; «(probabile)» means it was inferred from the folder. A manual choice does not add support to the game."),
        new("Neural Rendering paths", "Native DLSS (DX12): ReShade → RenoDX NR.\nWithout DLSS · Feeder: DX11/DX12/Vulkan games without DLSS; estimated motion may cause ghosting.\nOptiScaler F5: OptiScaler proxy with multipass NR.\nUse a single NR path per game."),
        new("HDR", "RenoDX HDR: per-game mod matched by exact title in the RenoDX catalog, or imported from a downloaded file.\nUnreal HDR (Engine.ini): enables engine HDR in Unreal games; launch the game once first."),
        new("Game DLLs (DLSS and Streamline)", "The «DLL» page lists each game's NVIDIA DLLs (DLSS SR/FG/RR and Streamline) against the newest builds. «Update» installs the newest, «Update all» does every outdated game, «Restore originals» puts the game's own files back. Advanced mode can pick an exact DLSS or Streamline version."),
        new("Remove and undo", "«Remove» restores the files and reverts only the INI settings the app changed. «History» lists recent operations and can undo them. 1.x installs can be adopted (managed by 2.0 without touching game files) or restored from 1.x backups."),
    ];
}
