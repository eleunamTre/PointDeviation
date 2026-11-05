# PointDeviation – roadmap

## 1. Formati
- **E57** (lettura diretta, anche con più scansioni e colori/intensità) – fatto (da provare su file reali).
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

## 2. Completezza (Completeness check)
Profilo colori «Completezza» (punti a più di una soglia dal modello) e finestra «Zone mancanti» (fatto, punto 134
di MODIFICHE): zone raggruppate con superficie stimata, tipo, livello, zoom, isolamento, esportazione CSV.
Possibili sviluppi, dopo le prove sulla nuvola reale:
- evidenziare nella vista la zona selezionata (contorno o colore) senza isolarla;
- coordinate condivise nell'esportazione; salvataggio dell'elenco con il progetto;
- regolazione automatica di cella e punti minimi sulla densità della nuvola.

## 3. Interfaccia
- (fatto per Profili colore) Eliminare i ricaricamenti visibili all'apertura delle finestre (es. Analisi: compare prima solo il
  profilo attivo, poi gli altri): costruire il contenuto prima di mostrare la finestra.
- Armonizzare scelte grafiche e icone (stile, spessori, colori, dimensioni 16/32) su tutta la barra.

## 4. Verifiche
- Nuvola da qualche centinaio di milioni di punti (conversione e navigazione).
- Computer meno potente: profilo Leggero e Revit limitato a 4 processori
  (Gestione attività › Dettagli › Revit.exe › Imposta affinità).
