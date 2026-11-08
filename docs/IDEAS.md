# Idee per MetaCodex: oltre il codice (2026-11-08)

Brainstorm su richiesta del proprietario: funzioni davvero utili o "wow" non ancora pensate, anche fuori dal
software (design, simulazioni, 3D, pipeline). Fonti: il catalogo dei plugin di bb (91 voci, `bb plugin search`),
quello che MetaCodex ha già (agenti in thread, Linux nel visore, viste degli agenti, fisica Unity, voce locale,
mani, passthrough) e quello che il Quest 3S può fare davvero.

Sforzo: **S** meno di un giorno, **M** 1-3 giorni, **L** una settimana o più. Scadenza contest: 18 novembre.

## L'idea che regge tutte le altre: i "Workbench"

Oggi un agente può mostrare grafici e diagrammi. Il passo dopo: **un modulo per ogni mestiere**, installabile
come un plugin di gesti, fatto di quattro pezzi che esistono già o quasi:

1. una **skill** che insegna all'agente il mestiere (il contesto "tipo MCP": cosa può fare, i formati, i limiti);
2. degli **strumenti Linux** (CadQuery, ffmpeg, Python, KiCad, ROS...) installati dal pannello SYSTEM;
3. un **visore Unity generico**: mesh (glTF/STL), nuvole di punti, linee, volumi, video, audio, una scena 3D
   aggiornabile in streaming (il "protocollo scena": glTF + patch JSON via WebSocket), oltre alle viste di oggi;
4. opzionale, una **macchina remota** (PC o cloud via MQH) per il lavoro pesante: il visore resta la cabina di
   regia, il calcolo va dove c'è una GPU.

Il visore non cambia per ogni dominio: cambiano skill e strumenti. Così "fare design" o "fare simulazioni" non
richiede una nuova APK, come oggi i plugin dei gesti.

## Design e 3D

| Idea | Perché è forte | Come | Sforzo |
|---|---|---|---|
| **CAD parametrico in scala 1:1** | "Fammi un supporto per il telefono": l'agente scrive il modello, lo vedi *sulla tua scrivania* a grandezza vera, lo misuri con due dita, dici "3 mm più spesso" e cambia. Poi lo mandi alla stampante 3D | CadQuery / build123d / OpenSCAD in Linux → STL/GLB → caricamento a runtime (glTFast) → righello tra le dita, piano di sezione con la mano; invio a OctoPrint / PrusaLink / Bambu in LAN | M |
| **Schizzo in aria → struttura** | Disegni con il dito nello spazio (scatole, frecce); l'agente lo trasforma in diagramma, primitive CAD o scheletro di codice. Per chi non programma | tratti 3D registrati dalle mani → immagine + coordinate → modello di visione (remoto) → vista o modello | M |
| **Arredare la stanza vera** | "Ci sta una scrivania da 180 cm qui?": mobili a grandezza vera appoggiati su pavimento e pareti veri, l'agente propone disposizioni | MRUK (scena: pareti, pavimento, mobili; permesso USE_SCENE) + modelli glTF | M |
| **Visore di asset** | Modelli 3D di un progetto (gioco, arte) ispezionati in scala: luci, wireframe, statistiche, UV; l'agente li corregge con Blender su un PC | glTFast + materiali di prova; Blender headless sulla macchina remota | M |

## Simulazioni

| Idea | Perché è forte | Come | Sforzo |
|---|---|---|---|
| **Ponte per simulazioni dal vivo** | Qualsiasi programma in Linux (Python, numpy, MuJoCo, PyBullet, Box2D, C) manda le pose dei corpi e Unity le disegna a 72 fps; le dita spingono i corpi (le forze tornano al programma). Fisica didattica, meccanismi, orbite, folle, epidemie | protocollo "sim-stream" su WebSocket (pose + id + forme), interpolazione in Unity; skill per l'agente; lo stage scene3d di oggi diventa il caso semplice | M |
| **Robot e gemello digitale** | Modello URDF del robot accanto al robot vero (passthrough), giunti mossi con le mani, traiettorie disegnate nello spazio; l'agente scrive i nodi ROS | lettore URDF/MJCF → mesh; ROS 2 via rosbridge (WebSocket) in LAN | L |
| **Elettronica** | PCB di KiCad in 3D a grandezza vera, schema come vista, grafici dal vivo dei sensori (seriale/MQTT) mentre l'agente scrive il firmware | export GLB/STEP di KiCad; MQTT/WebSocket → viste con lo stesso id (grafici che si aggiornano) | M |

## Dati e visualizzazione

| Idea | Perché è forte | Come | Sforzo |
|---|---|---|---|
| **Stanze dei dati** | Cammini dentro i tuoi dati: embedding in 3D (UMAP), cluster etichettati dall'agente, barre 3D sul tavolo, mappe su un terreno 3D; prendi un punto e vedi la riga | nuvola di punti istanziata (decine di migliaia di punti), Python in Linux per il calcolo | M |
| **Notebook spaziali** | Jupyter nel visore: celle come pannelli, output come viste, l'agente modifica le celle; utile davvero a chi fa analisi | kernel Python (ipykernel) in Linux, protocollo Jupyter; output → viste | M |
| **Volumi** | TAC/risonanze (DICOM), microscopia, risultati CFD: un volume tra le mani con piani di taglio | ray marching su texture 3D (volumi piccoli, ~128³, sul Quest); conversione in Linux | L |

## Pipeline di ogni tipo

| Idea | Perché è forte | Come | Sforzo |
|---|---|---|---|
| **Editor di pipeline a nodi** | Come Node-RED / ComfyUI ma nello spazio: nodi = comandi Linux, funzioni Python, agenti, chiamate HTTP, modelli AI; colleghi con il pizzico e vedi i dati scorrere (conteggi sui fili, anteprime). L'agente costruisce e corregge la pipeline | grafo (UiFlow di oggi, reso modificabile) + esecutore in Linux; si appoggia ai plugin MQH già presenti `workflows` e `automations` ("ogni mattina: aggiorna, testa, riassumi i fallimenti, apri una vista") | L |
| **Media senza GUI** | "Taglia questo video, sottotitoli, capitoli": ffmpeg + Whisper locale; forma d'onda e timeline come vista | ffmpeg in Linux, Whisper già nel visore (servizio AI) | M |
| **Training ML e job lunghi** | Curve di loss dal vivo, stato dei job sulla macchina remota, galleria di predizioni | viste con id fisso aggiornate da uno script; MQH multi-macchina | S |
| **CI / deploy** | Muro con run di GitHub Actions, PR, deploy: un fallimento → "sistemalo" all'agente | come i plugin bb GitHub Actions / PR Dashboard; token già nel vault | M |

## Gli agenti, ma come si può solo in VR

| Idea | Perché è forte | Come | Sforzo |
|---|---|---|---|
| **Mission control** | Ogni thread attivo è una sfera intorno a te: colore = stato, pulsa quando ti serve, guardala per un'anteprima, pizzica per aprirla; le approvazioni volano al polso | dati già in THREADS; idea di bb Work Map / Triage / Command Center | M |
| **Gara tra agenti** | Una richiesta → 3 fork (provider o modelli diversi) → tre workspace affiancati con diff e test → scegli il vincitore. La VR ha lo spazio per confrontarli davvero | FORK di oggi ×3 + vista di confronto; idea di bb Autoresearch (tiene solo ciò che il benchmark conferma) e Mixture of Agents | M |
| **Indica e chiedi** | Indichi un pannello, una vista... o **il mondo reale** (un circuito, una lavagna, una schermata del PC) e chiedi "cos'è / sistemalo": l'agente riceve l'immagine | Passthrough Camera Access (Quest 3/3S, permesso camera; skill hz-unity-passthrough-camera-access) + raggio della mano → ritaglio → input del thread; idea di bb Agentation | M |
| **Dettatura agli agenti** | TALK oggi esegue comandi; dettare *messaggi* lunghi ai thread (e farsi leggere i riassunti delle risposte) rende tutto senza tastiera | Whisper e Supertonic sono già nel visore: modalità "detta" nel compositore; idea di bb Voice Mode | S |
| **Albero dei thread** | Gli agenti che lanciano sotto-agenti diventano un albero 3D da esplorare | parentThreadId da MQH; idea di bb Thread Tree | S |
| **IDE completo quando serve** | code-server nel Linux del visore, in un pannello: per quando vuoi scrivere tu | WebView Android su texture (o plugin) → pannello; idea di bb VS Code Server | M |
| **Il desktop del PC dentro il visore** | Usi le app Windows dal Quest, accanto ai thread | client VNC/RDP in Linux su texture; idea di bb Screen Sharing | M |
| **Costi e parametri vitali** | Token e costo per thread, quote dei provider, fps, temperatura e batteria del visore in un widget | contextWindowUsage.cost, provider-usage; idea di bb Millivolt / Host Pulse | S |

## Ergonomia che nessuno ha

| Idea | Perché è forte | Come | Sforzo |
|---|---|---|---|
| **Menu sul palmo** | Guardi il palmo sinistro: thread attivi, notifiche, FOCUS, TALK. Nessun pannello da cercare | gesto palm-up già riconosciuto + pannello ancorato alla mano | S |
| **Palazzo della memoria dei progetti** | Ogni progetto vive in un punto della stanza (scrivania = MetaCodex, finestra = SandyBridge): ci vai e si aprono i suoi thread e pannelli, anche dopo un riavvio | OVRSpatialAnchor (persistenti) per i layout salvati | M |
| **Tastiera vera nel buio** | Con Ambient Focus al massimo resta visibile solo la tastiera fisica (e le mani) | finestra di passthrough sulla tastiera (posizione registrata una volta o tracciata) | S |
| **Sessioni di focus** | Focus + timer: durante la sessione le notifiche degli agenti aspettano; alla fine una vista riassuntiva | Ambient Focus + coda delle notifiche | S |

## La mia scelta per il contest (10 giorni)

1. **CAD 1:1 sulla scrivania** (M): la demo che fa capire in 5 secondi perché serve un visore, ed è fuori dal
   "solo codice". Serve il caricamento glTF a runtime, poi è skill + CadQuery.
2. **Indica e chiedi con la camera del passthrough** (M): l'agente vede quello che vedi tu.
3. **Mission control + dettatura** (M + S): la parte agenti diventa spaziale e a mani libere.
4. **Ponte per simulazioni** (M): generalizza scene3d: qualsiasi simulazione scritta dall'agente, dal vivo.

Poi, con più tempo: i Workbench come pacchetti installabili, l'editor di pipeline a nodi, i notebook spaziali.

## Cosa dice il catalogo bb (spunti presi)

Mermaid, Generative UI (già fatto a modo nostro), Work Map / Triage / Command Center (mission control),
Autoresearch + Mixture of Agents (gara tra agenti), Thread Tree, Voice Mode (dettatura), Agentation (indica e
chiedi), VS Code Server, Screen Sharing, Android Emulator, GitHub Actions / PR Dashboard, Millivolt / Host
Pulse / Cache Watch (costi), Backlog.md (kanban dei task), Pulse (Slack, Gmail, Calendar), Meetings, Souls
(persona per thread), Guest View (condividere un thread con chi non ha bb).
