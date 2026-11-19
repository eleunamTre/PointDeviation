# PointDeviation – roadmap

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
- Armonizzare scelte grafiche e icone (stile, spessori, colori, dimensioni 16/32) su tutta la barra.

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
