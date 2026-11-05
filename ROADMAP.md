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
  prospetto) e ritorno ai recinti di prima; esportazione CSV; opzione «solo dentro i recinti attivi».
- La zona selezionata si evidenzia in viola in qualunque modalità di visualizzazione (RGB, quota, normali, analisi);
  con i profili a tolleranza gli altri punti senza modello vanno in grigio per distinguerla.
- La finestra ricorda l'ultimo elenco del progetto: riaprendola ricompare senza ricalcolare; con un'analisi nuova
  la ricerca parte da sola.
Possibili sviluppi:
- con il profilo Completezza, attenuare le altre zone mentre una è selezionata;
- coordinate condivise nell'esportazione; elenco salvato con il progetto (oggi si perde chiudendo Revit);
- regolazione automatica di cella e punti minimi sulla densità della nuvola.

## 3. Interfaccia
- (fatto) Eliminare i ricaricamenti visibili all'apertura delle finestre: contenuto costruito prima di mostrare la
  finestra (Profili colore), doppio buffer e prima comparsa disegnata fuori schermo per tutte le finestre
  (MODIFICHE 127–129). Da confermare sull'uso quotidiano.
- Armonizzare scelte grafiche e icone (stile, spessori, colori, dimensioni 16/32) su tutta la barra.

## 4. Verifiche
- Nuvola da qualche centinaio di milioni di punti (conversione e navigazione).
- Computer meno potente: profilo Leggero e Revit limitato a 4 processori
  (Gestione attività › Dettagli › Revit.exe › Imposta affinità).
