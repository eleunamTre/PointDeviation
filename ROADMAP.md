# PointDeviation – roadmap

## 0. Disegno con shader – da fare
- (fatto, voce 270) Maniglie del riquadro, cubo e interfaccia sopra la nuvola: la nuvola si disegna prima del primo
  disegno di Revit nell'immagine della finestra, cosi' l'interfaccia viene dopo. Il punto sotto resta come storia.
- **Profondita' propria della nuvola** (superato dalla 270). Oggi la nuvola opaca scrive i suoi punti nel
  buffer di profondita' di Revit: Revit poi considera «nascosti» le maniglie del riquadro di sezione, le quote e gli
  altri elementi di controllo che stanno dietro un punto e li disegna sbiaditi, e trovare la maniglia da trascinare
  diventa difficile. Soluzione: un buffer di profondita' nostro per l'ordine dei punti tra loro, mentre quello di Revit
  si legge soltanto (perche' i muri coprano la nuvola) senza scriverci; maniglie e riquadro restano pieni sopra la
  nuvola. Da verificare: lettura del buffer di Revit come texture (formato R24G8), fotogrammi interrotti e parziali
  (selezione), VistaIniziale, trasparenza e raggi X.
- Zoom molto rapido e ravvicinato: per circa mezzo secondo la nuvola e' quasi vuota (blocchi fini non ancora letti,
  quelli grossolani al loro posto sono pochi puntini). Possibili rimedi: caricare prima i blocchi verso cui si zooma,
  punti piu' grandi per i blocchi grossolani usati come sostituti.
- Con la trasparenza la nuvola puo' passare davanti agli elementi selezionati (Revit li disegna con una profondita' a
  parte).
- Orbita con i link (voci 271-276): nelle immagini intermedie Revit ricompone la scena senza la profondita' completa
  del modello; anche l'RCP disegnato da Revit ha qualche incertezza. La mesh dell'analisi con lo scarto di profondita'
  misurato di Revit (7-16 cm) migliora ma non arriva al livello dell'RCP: i difetti rimasti vengono probabilmente
  dalla camera delle immagini intermedie (faro, camera precedente), non dalla profondita'. Il video dell'RCP con OBS
  (24/11) mostra che Revit con i link a volte disegna solo il link e salta modello e nuvola: buona parte dei difetti
  era quello. Dalla 278 la mesh si prepara da sola per la vista 3D attiva (DepthMesh.cs, Idling a passi di 40 ms).
  Possibili sviluppi: cache su disco con la versione del documento e dei link, aggiornamento dei soli elementi
  modificati, filtro dei collegamenti per ingombro della nuvola.

## 1. Formati
- **E57** (lettura diretta, anche con più scansioni e colori/intensità) – fatto e provato su un rilievo reale
  (4 scansioni, 49 M punti: conversione in 110 s, allineato all'RCP dello stesso rilievo).
- In seguito: LAZ, PTS/XYZ/PLY.

## 1b. Fotosfere (panoramiche) dentro Revit
Le immagini sferiche delle stazioni di scansione (negli E57 sono nella sezione images2D, con posizione e
orientamento; negli RCP nelle scansioni) mostrate in Revit: elenco delle stazioni e apertura della panoramica
in una vista dedicata di Revit (vista 3D prospettica creata per la stazione, camera nel punto di scansione).

## 1c. Conversioni in background – fatto
Coda di conversioni indipendente dalla finestra Libreria: ogni nuvola in conversione compare come scheda
«bloccata» con il proprio avanzamento, la finestra si può chiudere e Revit resta utilizzabile. LAS ed E57
interamente in background; RCP: la lettura attraverso Revit resta sul thread di Revit (come oggi), la
costruzione dell'octree passa in background.

## 2. Completezza (Completeness check) – fatto
Provato sul rilievo reale (48,5 M punti: 198 zone in mezzo secondo con le distanze già salvate). MODIFICHE 134–138.
- Profilo colori «Completezza»: punti a più di una soglia dal modello.
- Finestra «Zone mancanti» (pannello Analisi): i punti lontani si raggruppano in zone (cubetti che si toccano,
  rumore scartato), con superficie stimata, punti, tipo (verticale, orizzontale, inclinata, volume), ingombro,
  livello e quota dal livello. Elenco ordinabile; zoom sulla zona; «Isola la zona» con due recinti (pianta e
  prospetto) e ritorno ai recinti di prima; esportazione CSV (coordinate interne e condivise); opzione «solo
  dentro i recinti attivi».
- La zona selezionata si evidenzia in viola in qualunque modalità di visualizzazione (RGB, quota, normali, analisi);
  con i profili a tolleranza gli altri punti senza modello vanno in grigio per distinguerla.
- La finestra ricorda l'ultimo elenco del progetto: riaprendola ricompare senza ricalcolare; con un'analisi nuova
  la ricerca parte da sola.
Possibili sviluppi:
- con il profilo Completezza, attenuare le altre zone mentre una è selezionata;
- elenco salvato con il progetto (oggi si perde chiudendo Revit);
- regolazione automatica di cella e punti minimi sulla densità della nuvola.

## 2b. Elementi del modello non presenti nel rilievo («nuvola opposta»)
Verso opposto delle Zone mancanti: trovare elementi modellati che nel rilievo non esistono (esempio reale: un
montante modellato accanto a un infisso, nella posizione giusta per essere visto dallo scanner, ma senza punti;
oggi non risulta rosso perché lì non c'è nuvola da colorare). Da fare dopo aver stabilizzato il disegno con shader.
- Campionamento delle superfici degli elementi analizzati (le stesse triangolazioni dell'analisi deviazioni), con
  passo regolabile (es. 5 cm): è la «nuvola opposta».
- Per ogni campione, punto del rilievo più vicino cercato nell'octree; entro la soglia la parte è «confermata».
- Per ogni elemento: percentuale di superficie confermata e tre esiti: confermato, non trovato, non verificabile.
- Limitare i falsi allarmi:
  - solo la parte di elemento dentro l'ingombro reale del rilievo (celle con punti, non il solo riquadro della nuvola);
  - solo facce esposte, cioè che guardano verso spazio dove ci sono punti; escluse le facce a contatto con altri
    elementi o nascoste dentro muri e solai;
  - campioni «non verificabili» se fra campione e zona rilevata c'è altro modello che li copre (occlusione);
  - filtro per categoria (es. muri, solai, porte, finestre, pilastri, montanti).
- Risultato: colori in Revit (verde, giallo parziale, rosso non trovato, grigio non verificabile), elenco
  ordinabile per percentuale con zoom, selezione, isolamento ed esportazione CSV, come per le Zone mancanti.
- Presentato come elenco da verificare, non come verdetto (restano casi come un muro coperto da un armadio).

## 2d. Report esportabile delle verifiche
Report da consegnare con quello che l'analisi trova, dopo aver chiuso i problemi di profondita' del disegno.
- Elementi fuori tolleranza: nell'estrazione della mesh ogni triangolo porta l'ID dell'elemento e del collegamento da
  cui viene; per ogni elemento punti vicini, scostamento medio e massimo, percentuale di punti oltre la tolleranza.
  Elenco ordinato dal peggiore, con selezione dell'elemento in Revit.
- Zone non modellate: i gruppi delle Zone mancanti con posizione, ingombro, punti, livello e quota.
- Immagini generate dal plugin (non dall'esportazione di Revit, che non vede la nostra nuvola): vista dall'alto e
  laterale di ogni elemento o zona, punti colorati per scostamento e contorno del modello.
- Formato: Excel con le tabelle e gli ID, PDF con le immagini.

## 2e. Parti della nuvola da escludere (opzione)
Poter spegnere parti della nuvola, nella vista e nell'analisi (e quindi nel report), per non segnalare come «non
modellato» quello che non va modellato:
- per scansione: riconoscere le diverse scansioni quando il file le distingue (E57 con piu' scansioni, RCP con piu'
  RCS) e accenderle o spegnerle una per una;
- per classe, se la nuvola e' classificata (LAS: vegetazione, terreno, ...);
- vegetazione riconosciuta con regole (punti sparsi e non planari, colore prevalentemente verde, gruppi piccoli);
  un classificatore addestrato solo se le regole non bastano;
- zone escluse disegnate dall'utente.

## 2f. Nuvola del collegamento spenta insieme al collegamento (opzione)
Richiesta del 27/11/2026. Con l'analisi estesa ai modelli collegati, se nella vista si spegne un collegamento
(V/G › Revit Links, Nascondi elemento, Nascondi categoria) si spengono in automatico anche i punti della nuvola la cui
superficie più vicina è di quel collegamento. Opzione nella finestra dell'analisi, accanto a «sbiaditi / trasparenti»
(voce 259), spenta di serie.
Cosa c'è già: il bit `LinkBit` nella distanza salvata (2 byte/punto, DistCache) e l'opacità per punto nello shader
(`PdSetPointAlpha`): nascondere i punti è un caso dell'opacità a 0, nessun dato nuovo da salvare.
Da fare:
- visibilità dei collegamenti nella vista attiva (`RevitLinkInstance` nascosta, categoria, V/G), letta all'attivazione
  della vista e a DocumentChanged come per la nuvola (`CloudShownIn`); cambiando visibilità: nuovo stile (`LookKey`) e
  ridisegno, senza rifare l'analisi;
- un solo collegamento: basta il bit attuale. Più collegamenti: il bit dice solo «collegamento», non quale. Serve
  l'indice del collegamento per punto (almeno 3-4 bit, quindi meno precisione della distanza o un file a parte), oppure
  in prima versione si spengono i punti di tutti i collegamenti quando almeno uno è spento (da scegliere);
- nel disegno di sempre (senza shader) scartare i punti nella preparazione dei blocchi (`CloudBaker`), come i colori;
- riga nel registro: collegamenti spenti nella vista e punti nascosti per questo.

## 2c. Analisi con i modelli collegati: lentezza
Con l'analisi estesa ai modelli collegati sia l'analisi sia la navigazione diventano molto lente (log del
19/11/2026, stesso rilievo da 42,5 M punti):
- solo modello: 717 elementi, 272 mila triangoli, lato massimo dopo la suddivisione 0,53 m;
- con i collegati: 3.946 elementi, 1,73 M triangoli, lato massimo dopo la suddivisione **17,2 km**: almeno un
  elemento enorme (terreno, elemento di riferimento lontano o trasformazione del collegamento sbagliata) non viene
  suddiviso e finisce in molte celle della ricerca;
- durante il calcolo in background delle distanze di tutta la nuvola la navigazione scende a 1–2 fotogrammi al
  secondo (letture e preparazioni dei blocchi molto più lente) e torna a 6–7 quando il calcolo finisce.
Da fare:
- registrare nel log, per ogni collegamento, elementi, triangoli e l'elemento con il triangolo più grande
  (categoria, id, ingombro), per capire da dove arriva il lato da 17 km;
- usare solo gli elementi collegati che cadono nell'ingombro della nuvola (più un margine), scartare il resto
  prima della triangolazione; filtro per categoria anche per i collegati;
- suddividere sempre i triangoli grandi, senza un limite che lasci passare quelli enormi;
- tenere in memoria la triangolazione dei collegati finché il file collegato non cambia;
- calcolo in background con meno thread e priorità bassa mentre la camera si muove, a piena velocità da fermi;
- errore da correggere: «Salvataggio distanze: Cannot access a closed file» (DistCache.Write) quando un'analisi
  nuova parte mentre il salvataggio di quella precedente è ancora in corso.

## 3. Interfaccia
- (fatto) Eliminare i ricaricamenti visibili all'apertura delle finestre: contenuto costruito prima di mostrare la
  finestra (Profili colore), doppio buffer e prima comparsa disegnata fuori schermo per tutte le finestre
  (MODIFICHE 127–129). Da confermare sull'uso quotidiano.
- (fatto) Barra riorganizzata in pannelli e icone ridisegnate in stile Revit (MODIFICHE 225–226).
- Grafica personalizzata delle finestre e delle schede del plugin (Libreria nuvole con le schede delle nuvole, Profili
  colore, Zone mancanti, Impostazioni, Aspetto, Legenda...), da uniformare alla barra nuova: stessa palette (grigi,
  accento blu, arancione solo per i punti), stesse icone e linee sottili, intestazioni, pulsanti, spaziature e
  caratteri coerenti (base: UiTheme.cs). Obiettivo: un'identità propria del prodotto, distinta dal concorrente, che
  resti comunque coerente con l'interfaccia di Revit.

## 4. Verifiche
- Nuvola da qualche centinaio di milioni di punti (conversione e navigazione).
- Computer meno potente: profilo Leggero e Revit limitato a 4 processori
  (Gestione attività › Dettagli › Revit.exe › Imposta affinità).

## 5. Versioni di Revit successive alla 2024
Supporto di Revit 2025, 2026, 2027 e seguenti, oltre alla 2024.
- Runtime (verificato a novembre 2026): Revit 2024 = .NET Framework 4.8; Revit 2025 e 2026 = .NET 8, ma Autodesk le
  sta portando a .NET 10 con gli aggiornamenti di versione (2026.5 in beta è già su .NET 10; il supporto di
  Microsoft a .NET 8 finisce il 10/11/2026); Revit 2027 = .NET 10.
- Progetto con più destinazioni: `net48` per la 2024 e `net8.0-windows` (Windows Forms) per la 2025 e 2026; per la
  2027 `net10.0-windows` con SDK .NET 10. Un plugin compilato per .NET 8 dovrebbe caricarsi anche in Revit su .NET 10
  (non il contrario): per 2025/2026 restare su `net8.0-windows` copre sia le build .NET 8 sia quelle .NET 10; da
  provare comunque su entrambe. Riferimenti alle API della versione giusta per ogni destinazione.
- Controllare le modifiche incompatibili di .NET 10 rispetto a .NET 8 (elenco Microsoft «compatibility/10.0») e le
  novità API della 2027 (isolamento dei plugin, nuova cartella dei plugin per tutti gli utenti, regioni cloud con
  `ModelPathUtils.GetAllCloudRegions()`).
- Verificare le parti sensibili al cambio: serializzazione JSON delle impostazioni, riflessione sulle API di
  visibilità delle nuvole, DirectContext3D e scrittura diretta nei buffer, finestre Windows Forms, P/Invoke.
- File .addin e cartella di installazione per ogni versione di Revit; eventuale installatore unico.
- Prova su ogni versione con la nuvola di riferimento (conversione, navigazione, analisi).
