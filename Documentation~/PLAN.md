# DAVFX Texture Channel Packer: piano di progetto e implementazione

Versione del piano: 1.0 (riferito al package 1.0.0)

## 1. Obiettivo

Un tool editor per Unity che crea, combina, modifica, sposta ed elimina i canali (R, G, B, A) delle texture,
partendo da texture esistenti o da zero, senza mai degradare i dati quando l'operazione non lo richiede.

Requisiti espliciti:

1. Mescolare, scambiare, togliere e modificare canali.
2. Lavorare su texture esistenti (anche sovrascrivendole) e creare texture nuove.
3. Coprire casi d'uso particolari (detail map HDRP, flip normal, maschere VFX, HDR, 16 bit, dimensioni diverse).
4. Qualità invariata: un canale copiato deve arrivare identico, bit per bit.
5. Interfaccia semplice, pulita e leggibile.
6. Installabile da Package Manager con URL Git.
7. Progettato per essere integrato in futuro in un hub (vista riutilizzabile + API pubblica).

## 2. Il problema della qualità (e come lo risolviamo)

La maggior parte dei channel packer legge i pixel dalla texture *importata* da Unity. Quella texture è già stata:

- compressa (DXT/BC/ASTC: perdita di dati),
- ridimensionata (Max Size 2048 di default: una 4K diventa 2K),
- eventualmente convertita (normal map swizzle, alpha dilation con "Alpha Is Transparency", gamma PNG).

Il risultato si porta dietro tutti questi artefatti. Questo tool invece legge **il file sorgente originale**:

| Formato sorgente | Come viene letto | Esattezza |
|---|---|---|
| PNG (1/2/4/8/16 bit, gray, palette, RGB, RGBA, interlacciato) | decoder interno | bit per bit |
| TGA (8/16/24/32 bit, RLE, palette, qualsiasi origine) | decoder interno | bit per bit |
| JPG | `ImageConversion.LoadImage` | identico al file (che è già lossy di suo, viene segnalato) |
| EXR scanline (half/float; NONE, RLE, ZIPS, ZIP, PIZ) | decoder interno | bit per bit |
| HDR, PSD, TIF, BMP, GIF, IFF, PICT, EXR tiled/multipart/lossy | copia temporanea importata **non compressa, a piena risoluzione, readable, lineare**, letta e poi cancellata | bit per bit (float 32 per sorgenti oltre 8 bit) |
| Texture2D non legata a un file (.asset) | lettura diretta se readable e non compressa, altrimenti readback GPU | esatto se non compressa, segnalato se compressa |

Garanzie:

- **I file originali e i loro .meta non vengono mai toccati** in lettura (la copia temporanea ha un GUID nuovo e viene cancellata).
- Dopo ogni lettura via importer viene **verificato** che formato, dimensioni e assenza di mip siano quelli attesi. Se no, errore esplicito invece di un risultato degradato in silenzio.
- Internamente i valori sono float 32 nella forma `k / (2^bit - 1)`. La riconversione `round(v * max)` restituisce esattamente `k` per 8 e 16 bit (dimostrato dai test su tutti i valori possibili).
- Un canale senza modificatori viene copiato **senza nessuna operazione matematica**.
- Scrittura con encoder interni (PNG 8/16 bit, TGA, EXR half/float con compressione ZIP lossless): nessuna compressione lossy.
- Ogni operazione che può perdere informazione è segnalata prima del Pack: ridimensionamento, riduzione di bit depth, clamp HDR, sRGB in 8 bit, sorgenti JPG o compresse.
- L'output viene reimportato con Max Size adeguata alla risoluzione reale (una 4K non finisce a 2048).
- "Alpha Is Transparency" resta spento sugli output: Unity non altera l'RGB sotto i pixel trasparenti (fondamentale per le mask map).

## 3. Funzionalità

### 3.1 Modalità Pack

- **Input**: da 1 a 8 slot (A...H), drag and drop di Texture2D. Ogni slot mostra risoluzione, bit depth, canali.
- **Layout di uscita**: RGBA, RGB, Grayscale, Grayscale + Alpha.
- **Per ogni canale di uscita**:
  - Sorgente: canale di uno slot (R, G, B, A, Luminanza Rec.709, Media, Min, Max) oppure valore costante (0-1 e 0-255).
  - Combinazione opzionale con una seconda sorgente: Multiply, Add, Subtract, Min, Max, Average, Difference, Screen.
  - Conversione colore: sRGB → Lineare, Lineare → sRGB.
  - Inverti.
  - Levels (input min/max, gamma, output min/max).
  - Ordine fisso e mostrato in UI: Sorgente → Combina → Conversione → Inverti → Levels.
- **Slot vuoti con fallback**: i preset possono dire "se manca l'AO usa 1.0", così una mask map si crea anche con input parziali.
- **Dimensione**: input più grande, più piccolo, uguale a uno slot, personalizzata. Se le dimensioni non coincidono il default è *errore*. Il ridimensionamento (Nearest, Bilinear, Area) va scelto esplicitamente e viene segnalato.
- **Formato**: PNG (8/16 bit), TGA (8 bit, RLE opzionale), EXR (half/float). Bit depth "Auto" sceglie la minima che non perde dati.
- **Output**: cartella + nome (nome automatico da prefisso comune + suffisso del preset), sovrascrittura con conferma e backup automatico in `Library/DAVFX_ChannelPacker/Backups`.
- **Import settings dell'output**: impostazioni del preset (sRGB o lineare, tipo, compressione), copia da un input, oppure default Unity.
- **Preview** in tempo reale (RGB, R, G, B, A) calcolata con lo stesso motore del Pack.
- **Auto-assign**: seleziona più texture nel Project e il tool le assegna agli slot in base al nome (`_AO`, `_Metallic`, `_Roughness`, ...).

### 3.2 Modalità Split

Estrae i canali di una texture in file grayscale separati, con suffissi configurabili, mantenendo la bit depth.

### 3.3 Preset inclusi

| Categoria | Preset | Output |
|---|---|---|
| Unity | HDRP Mask Map | R Metallic, G AO, B Detail, A Smoothness |
| Unity | HDRP Mask Map (da Roughness) | come sopra, A = 1 - Roughness |
| Unity | Terrain Layer Mask Map (anche da Roughness) | R Metallic, G AO, B Height, A Smoothness (terrain URP e HDRP) |
| Unity | URP/Built-in Metallic + Smoothness | RGB Metallic, A Smoothness |
| Unity | HDRP Detail Map | R Albedo grigio, G Normal Y, B Smoothness, A Normal X |
| Unity | Albedo + Alpha | RGB Albedo, A Opacità |
| Unity | Albedo + Smoothness | RGB Albedo, A Smoothness |
| Unreal / altri | ORM | R AO, G Roughness, B Metallic |
| Normal | Flip Green (DirectX ↔ OpenGL) | G invertito, alpha conservato se presente |
| Canali | 4 maschere grayscale in RGBA (VFX) | R, G, B, A da 4 texture |
| Canali | 3 maschere grayscale in RGB | |
| Canali | Scambia Rosso ↔ Blu | |
| Canali | Rimuovi Alpha | |
| Canali | Estrai un canale in grayscale | |
| Canali | Inverti RGB | |
| Canali | Roughness ↔ Smoothness | |
| Nuova | Colore / maschera costante | dimensione personalizzata |
| Nuova | Normal map piatta (128,128,255) | |
| | Custom | vuoto |

### 3.4 Azioni rapide (tasto destro sulle texture nel Project)

`DAVFX Channel Packer/`: Apri nel Packer, Split Channels, Flip Normal Green, Remove Alpha, Invert RGB.
Creano sempre un file nuovo accanto all'originale (mai sovrascrittura silenziosa).

## 4. Architettura

```
com.davfx.texture-channel-packer/
  package.json, README.md, CHANGELOG.md, LICENSE.md
  Editor/                      assembly DAVFX.TextureChannelPacker.Editor (solo Editor)
    Core/                      C# puro, nessuna dipendenza da Unity, testabile ovunque
      PixelImage               immagine planare float, righe dall'alto
      PackRecipe               ricetta serializzabile (canali, operandi, modificatori)
      PackEngine               valuta la ricetta, parallelo, copia senza calcoli i canali puri
      Resampler                Nearest, Bilinear, Area
      ColorMath                sRGB, luminanza
      PackSettings             dimensione, formato, bit depth, import
      PackValidator            errori e avvisi prima del Pack
      Presets, NamingUtility
      Codecs/                  PNG (decode+encode), TGA (decode+encode), EXR (decode+encode), Zlib, CRC, probe header
    Unity/                     ponte con l'AssetDatabase
      TextureSourceLoader      lettura lossless (vedi sezione 2)
      DecodeCache              cache LRU con budget di memoria
      TextureOutputWriter      scrittura, backup, import settings
      ChannelPacker            API pubblica: Pack, Split, Load
      QuickActions             menu contestuali
    UI/
      ChannelPackerView        vista IMGUI riutilizzabile (per l'hub)
      ChannelPackerWindow      finestra Tools > DAVFX > Texture Channel Packer
      PackerState, PreviewBuilder, PackerStyles
  Tests/Editor/                test EditMode (Unity Test Framework)
    Core/                      test del core (girano anche fuori da Unity)
    Integration/               test end-to-end con AssetDatabase reale
  Tests~/DotNet/               harness .NET + verifica con librerie di riferimento (ignorato da Unity)
```

Scelte motivate:

- **Core senza Unity**: i codec e il motore sono verificabili con .NET e con Pillow/OpenEXR come riferimento indipendente.
- **Nessun AssetPostprocessor**: aggiungerne uno per le texture farebbe reimportare tutte le texture del progetto all'installazione.
- **IMGUI**: si incorpora in qualsiasi finestra (anche UI Toolkit tramite `IMGUIContainer`), quindi è pronto per l'hub.
- **Undo** su tutte le modifiche dell'interfaccia.

## 5. Piano di implementazione (ordine di lavoro)

1. Struttura package, asmdef, .meta con GUID stabili.
2. Core: PixelImage, ricetta, ColorMath, Resampler, PackEngine.
3. Codec: Zlib/CRC, PNG decoder (tutti i tipi e bit depth, Adam7), PNG encoder (filtri adattivi), TGA, EXR writer (ZIP, half/float), probe header (EXR, PSD, TIFF, HDR, BMP, GIF).
4. Validator, impostazioni, preset, naming.
5. Layer Unity: loader lossless con verifica, cache, writer con backup e import settings, API pubblica.
6. UI: finestra, preset, slot, righe canale, preview, output, avvisi, Split.
7. Azioni rapide.
8. Test core + integrazione Unity.
9. Harness .NET: compilazione contro le API Unity, test core, verifica con Pillow e OpenEXR.
10. README, CHANGELOG, istruzioni di installazione.

## 6. Casi d'uso e test

Ogni caso ha un test automatico. "Esatto" significa confronto bit per bit, nessuna tolleranza.

| # | Caso d'uso | Verifica | Dove |
|---|---|---|---|
| U1 | Copia RGBA identica da PNG 8 bit (rumore casuale) | output == input, esatto | core + Unity |
| U2 | Copia identica da PNG 16 bit | esatto | core + Unity |
| U3 | Mask map HDRP da 4 grayscale | ogni canale == sorgente, esatto | core + Unity |
| U4 | Mask map con slot mancanti (fallback) | AO = 255, Detail = 255 | core |
| U5 | Roughness → Smoothness | A = 255 - R, esatto | core |
| U6 | Flip green due volte | identico all'originale | core + Unity |
| U7 | Split e re-pack | identico all'originale | core + Unity |
| U8 | 8 bit → 16 bit → 8 bit | `k * 257` e ritorno esatto | core |
| U9 | Tutti i 256 / 65536 valori | quantizzazione round trip esatta | core |
| U10 | Normal map piatta nuova | (128,128,255) esatto | core |
| U11 | HDR EXR float → EXR float | float identici | core + Unity |
| U12 | EXR half | conversione half round-to-nearest-even corretta su tutti i 65536 half | core |
| U13 | Detail map HDRP | canali al posto giusto | core |
| U14 | Dimensioni diverse senza resample | errore bloccante | core |
| U15 | Resample Nearest x2 su/giù | ritorna all'originale esatto | core |
| U16 | Area su immagine costante | resta costante | core |
| U17 | Levels identità / Invert doppio | nessuna modifica | core |
| U18 | sRGB → Lineare → sRGB in float | errore < 1e-6 | core |
| U19 | TGA RLE e non compresso, origine alto/basso | esatto | core + Unity |
| U20 | Sorgente importata da Unity compressa e a 2048 | il tool legge comunque i dati originali a piena risoluzione | Unity |
| U21 | EXR letto dal decoder interno e via importer temporaneo | float identici nei due casi, nessun file temporaneo residuo | core + Unity |
| U22 | Sovrascrittura | backup creato, .meta originale conservato | Unity |
| U23 | Max Size output | >= risoluzione reale | Unity |
| U24 | Decoder PNG su 20 varianti generate da Pillow | identico a Pillow | harness |
| U25 | Encoder PNG/TGA letti da Pillow | identici | harness |
| U26 | Encoder EXR letto da OpenEXR ufficiale | identico | harness |
| U27 | File corrotti e troncati (oltre 100.000 varianti) | solo errori chiari, mai eccezioni grezze o blocchi | harness |
| U28 | 3000 ricette casuali | motore identico a un'implementazione di riferimento ingenua, validatore d'accordo | core |
| U29 | Set completo di materiale con Use Selection | albedo e normal non finiscono negli slot sbagliati | core + Unity |
| U30 | Split con suffisso vuoto o duplicato, cartella ignorata da Unity | rifiutato, sorgente intatto | Unity |

## 7. Ipotesi di utilizzo (scenari reali)

1. **Asset HDRP da Substance**: trascini `T_Rock_Metallic`, `T_Rock_AO`, `T_Rock_Roughness`, preset "Mask Map (da Roughness)", Pack. Esce `T_Rock_MaskMap.png` lineare.
2. **VFX**: quattro maschere di erosione/noise in un'unica RGBA per uno shader di dissolve.
3. **Normal map DirectX da un pack Unreal**: tasto destro, Flip Normal Green.
4. **Rimuovere un alpha inutile** da una texture per risparmiare memoria.
5. **Creare una texture di default** (maschera bianca, normal piatta) a una risoluzione precisa.
6. **Mettere la smoothness nell'alpha dell'albedo** per URP.
7. **Analizzare una texture impacchettata di terze parti**: Split in 4 grayscale.
8. **Texture con risoluzioni diverse**: il tool blocca, tu scegli consapevolmente il ridimensionamento.
9. **EXR HDR**: rimescoli canali di una texture float senza clamp né perdita.
10. **Modifica in place**: sovrascrivi la texture originale mantenendo il .meta (e quindi tutti i riferimenti nei materiali), con backup.

## 8. Fuori scope nella 1.0 (roadmap)

- Batch su cartelle intere (raggruppamento automatico per nome). L'auto-assign per nome è già la base.
- Scrittura PSD/TIF e lettura EXR senza importer.
- Integrazione nell'hub: la vista è già separata (`ChannelPackerView`), serve solo sapere come è fatto l'hub.
- Texture array, cubemap, 3D.

## 9. Compatibilità Unity

Versioni supportate: 2021.3 LTS, 2022.3 LTS, Unity 6 (6000.x) e successive.

Come è verificata:

1. Il sorgente C# ufficiale di Unity (UnityCsReference, branch 2021.3, 2022.1, 2022.3, 6000.0, 6000.3, 6000.7) è stato usato
   per controllare che ogni API usata esista e non sia marcata `[Obsolete]`.
2. Il package viene compilato, con i warning trattati come errori, contro gli assembly reali degli editor
   2021.3.45f2, 2022.3.62f3, 6000.0.84f1 e 6000.6.4f1 (`Tests~/UnityCompileCheck/compile_matrix.sh`).

Cosa è emerso e come è stato risolto:

- `Object.GetInstanceID()` è un errore di compilazione da Unity 6000.6 (sostituito da `GetEntityId`).
  Il tool non lo usa più: la cache identifica le texture senza file con una tabella debole interna.
- Da Unity 2022.1 l'importer ha `swizzle` e `flipGreenChannel`: un preset di progetto potrebbe rimappare i canali
  nell'import temporaneo. Il tool li forza ai valori neutri e li verifica, con `#if UNITY_2022_1_OR_NEWER`.
- `NamedBuildTarget` (2021.2+) è usato dietro `#if UNITY_2021_2_OR_NEWER`.

Per una nuova versione di Unity basta lanciare `compile_matrix.sh <versione>`.

## 10. Limiti noti (onesti)

- Il codice è compilato contro gli assembly reali di 2021.3, 2022.3 e Unity 6 e il core è testato fuori dall'editor
  (codec confrontati con Pillow/OpenEXR). Compilare non significa eseguire: i test di integrazione con l'AssetDatabase
  reale e l'interfaccia vanno provati nell'editor (Test Runner).
- Lettura via importer: risoluzione massima 16384 (limite di Unity).
- PSD: Unity legge l'immagine composita (livelli uniti), come qualsiasi texture PSD in Unity.
- I ridimensionamenti e le conversioni sRGB in 8 bit cambiano i dati per natura: per questo sono sempre segnalati.
