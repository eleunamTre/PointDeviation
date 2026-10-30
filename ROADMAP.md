# PointDeviation – roadmap

## 1. Formati
- **E57** (lettura diretta, anche con più scansioni e colori/intensità) – fatto (da provare su file reali).
- In seguito: LAZ, PTS/XYZ/PLY.

## 1b. Fotosfere (panoramiche) dentro Revit
Le immagini sferiche delle stazioni di scansione (negli E57 sono nella sezione images2D, con posizione e
orientamento; negli RCP nelle scansioni) mostrate in Revit: elenco delle stazioni, «vai alla stazione»
(camera nel punto di scansione) e panoramica sovrapposta al modello o in una finestra affiancata.

## 2. Completezza (Completeness check)
Esiste già come profilo colori «Completezza»: evidenzia i punti a più di una soglia dal modello
(parti rilevate e non modellate). Da completare:
- raggruppamento delle zone mancanti (cluster) con elenco navigabile e «zoom sulla zona»;
- quantità per zona (punti, superficie stimata), esportabile.

## 3. Interfaccia
- Eliminare i ricaricamenti visibili all'apertura delle finestre (es. Analisi: compare prima solo il
  profilo attivo, poi gli altri): costruire il contenuto prima di mostrare la finestra.
- Armonizzare scelte grafiche e icone (stile, spessori, colori, dimensioni 16/32) su tutta la barra.

## 4. Verifiche
- Nuvola da qualche centinaio di milioni di punti (conversione e navigazione).
- Computer meno potente: profilo Leggero e Revit limitato a 4 processori
  (Gestione attività › Dettagli › Revit.exe › Imposta affinità).
