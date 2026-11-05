HDLSS5 2.0 · portable version for Windows
=========================================

REQUIREMENTS
- Windows 10/11 64-bit.
- NVIDIA RTX 4000 or RTX 5000 GPU for Neural Rendering, driver 616.56 or later.
- No need to install .NET: it is already included in HDLSS5.exe.
- The interface is currently in Italian.

GETTING STARTED
1. Extract the zip to a folder you can write to (e.g. D:\Games\HDLSS5).
   Don't use "Program Files" and don't run it from inside the zip.
2. Run HDLSS5.exe (no administrator rights needed).
3. The app finds Steam and Xbox/Game Pass games by itself. Others can be added with "+ Aggiungi gioco" (Add game).

QUICK GUIDE
- Open a game from the library: the card at the top explains its status and offers a single button
  (Enable Neural Rendering, Move to 2.0, Import into HDLSS5, Did it work?).
- Always close the game before installing or removing anything.
- After playing for a few minutes, "Ha funzionato?" (Did it work?) reads the logs and tells you whether Neural Rendering started.
- "Avanzate" (Advanced, left sidebar) shows paths, options, components, 1.x installations and history.
- Every replaced file is saved first: you can undo from the History or with "Rimuovi" (Remove).

FOLDER CONTENTS
- HDLSS5.exe        the application
- bundled\          RenoDX NR add-ons (Lecram 10.8, ShortFuse) and the HDR reader for UE4SS
- nr-packages\      NR models 310.9.16.0 for RTX 4000 and RTX 5000 (required, don't delete them)
- SHA256SUMS.txt    checksums to verify the files
- data-v2\          created on first launch: settings, backups, history, activity log

UPGRADING FROM HDLSS5 1.x
- You can extract 2.0 into a new folder. In Settings, point to the 1.x folder
  (the one containing "data"): its backups are needed to move games to 2.0.
- Version 2.0 never modifies the 1.x "data" folder.

MOVING OR BACKING UP YOUR DATA
- To keep the data elsewhere, create a data-location.txt file next to HDLSS5.exe
  containing the path of the folder to use (one line).
- To back up, just copy the data-v2 folder.

NOTES
- HDLSS5.exe is not digitally signed: Windows SmartScreen may ask for confirmation on first launch
  ("More info" > "Run anyway"). Some antivirus programs flag tools that install DLLs into games
  heuristically: verify the file with SHA256SUMS.txt.
- Components are downloaded from their authors' official repositories and verified.
- Installation is blocked in games with anti-cheat (EasyAntiCheat, BattlEye).
