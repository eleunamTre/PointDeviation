using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PointDeviation
{
    /// <summary>
    /// Impostazioni avanzate: registro di diagnostica (livello, file), strumenti di diagnostica del disegno,
    /// prestazioni (limite di punti, memoria). Valgono per il computer, non per il progetto.
    /// </summary>
    internal sealed class AdvancedForm : PdForm
    {
        private readonly ListBox _nav = new ListBox { Dock = DockStyle.Left, Width = 170, BorderStyle = BorderStyle.None, IntegralHeight = false, ItemHeight = 30, DrawMode = DrawMode.OwnerDrawFixed };
        private readonly Panel _host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 14, 20, 14) };
        private readonly Panel[] _pages = new Panel[3];

        private readonly System.Windows.Forms.CheckBox _logOn = new System.Windows.Forms.CheckBox { AutoSize = true };
        private readonly ListBox _level = new ListBox { Height = 4 * 44, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 44, BorderStyle = BorderStyle.FixedSingle };
        private readonly TextBox _path = new TextBox { ReadOnly = true, Width = 380 };
        private readonly System.Windows.Forms.CheckBox _levels = new System.Windows.Forms.CheckBox { AutoSize = true };
        private readonly NumericUpDown _budget = new NumericUpDown { Minimum = 25, Maximum = 400, Increment = 25, Width = 80 };
        private readonly NumericUpDown _gpuKeep = new NumericUpDown { Minimum = 2, Maximum = 600, Width = 80 };
        private readonly NumericUpDown _memKeep = new NumericUpDown { Minimum = 5, Maximum = 3600, Width = 80 };
        private readonly ComboBox _profile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
        private readonly Label _profileInfo = new Label { AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = UiTheme.Muted, Margin = new Padding(0, 6, 0, 4) };
        private readonly System.Windows.Forms.CheckBox _fullWait = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _holdView = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _fixedBudget = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _shader = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _probe = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _native = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _beacon = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _meshDepth = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _meshTol = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _flatFix = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _flatBeacon = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _flatSimple = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _flatPost = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _meshNoLinks = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _meshCache = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };   // voce 312
        private readonly System.Windows.Forms.CheckBox _meshBox = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _hangWatch = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _meshDisk = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };   // voce 313
        private readonly System.Windows.Forms.CheckBox _meshIdle = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _meshFine = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };   // voce 314
        private readonly System.Windows.Forms.CheckBox _meshSmall = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _reproj = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _flatFaro = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _flatFaroDepth = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _flatFaroKeep = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _flatFaroAlpha = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _flatFaroCamera = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _flatHoverFix = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _flatHoverRestore = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _fastMatrix = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _otherBuf = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        private readonly System.Windows.Forms.CheckBox _uniform = new System.Windows.Forms.CheckBox { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };

        private static readonly string[][] Levels =
        {
            new[] { "Errori", "Solo gli errori che il plugin non riesce a gestire." },
            new[] { "Avvisi", "Anche le situazioni anomale gestite (es. camera non disponibile)." },
            new[] { "Info", "Anche caricamento delle nuvole, posizionamento, estrazione (consigliato)." },
            new[] { "Debug", "Anche lo stato del disegno ogni pochi secondi. Rallenta un poco: solo per indagare un problema." }
        };

        public AdvancedForm()
        {
            Text = L.T("Impostazioni avanzate");
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 600);
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = UiTheme.Surface;

            _nav.BackColor = UiTheme.Panel;
            foreach (string s in new[] { "Registro", "Diagnostica", "Prestazioni" }) _nav.Items.Add(L.T(s));
            _nav.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var br = new SolidBrush(sel ? Color.FromArgb(200, 222, 245) : UiTheme.Panel)) e.Graphics.FillRectangle(br, e.Bounds);
                TextRenderer.DrawText(e.Graphics, _nav.Items[e.Index].ToString(), UiTheme.Base, new Rectangle(e.Bounds.X + 14, e.Bounds.Y, e.Bounds.Width - 14, e.Bounds.Height),
                    UiTheme.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            };
            _nav.SelectedIndexChanged += (s, e) => ShowPage(_nav.SelectedIndex);

            _pages[0] = PageLog();
            _pages[1] = PageDiag();
            _pages[2] = PagePerf();
            foreach (Panel p in _pages) { p.Dock = DockStyle.Fill; p.Visible = false; _host.Controls.Add(p); }

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 46, Padding = new Padding(10, 8, 10, 8) };
            var close = new Button { Text = L.T("Chiudi"), AutoSize = true };
            var apply = new Button { Text = L.T("Applica"), AutoSize = true };
            close.Click += (s, e) => Close();
            apply.Click += (s, e) => Apply();
            buttons.Controls.Add(close);
            buttons.Controls.Add(apply);

            Controls.Add(_host);
            Controls.Add(_nav);
            Controls.Add(buttons);
            LoadValues();
            _nav.SelectedIndex = 0;
            UiTheme.Apply(this, apply);
        }

        private void ShowPage(int i)
        {
            for (int k = 0; k < _pages.Length; k++) _pages[k].Visible = k == i;
        }

        private static Label Title(string text)
        {
            return new Label { Text = text, AutoSize = true, Font = new Font("Segoe UI", 15f), Margin = new Padding(0, 0, 0, 8) };
        }

        private static Label Note(string text)
        {
            return new Label { Text = text, AutoSize = true, MaximumSize = new Size(470, 0), ForeColor = UiTheme.Muted, Margin = new Padding(0, 2, 0, 10) };
        }

        private static FlowLayoutPanel Stack()
        {
            return new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        }

        private Panel PageLog()
        {
            var p = Stack();
            p.Controls.Add(Title(L.T("Registro")));
            p.Controls.Add(Note(L.T("Il registro aiuta a capire cosa succede quando qualcosa non va: caricamento delle nuvole, errori di disegno e di lettura, posizionamento.")));
            _logOn.Text = L.T("Scrivi il registro");
            _logOn.CheckedChanged += (s, e) => _level.Enabled = _logOn.Checked;
            p.Controls.Add(_logOn);
            p.Controls.Add(new Label { Text = L.T("Livello di dettaglio:"), AutoSize = true, Margin = new Padding(0, 12, 0, 4) });
            _level.Width = 470;
            foreach (string[] l in Levels) _level.Items.Add(l);
            _level.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                var it = (string[])_level.Items[e.Index];
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var br = new SolidBrush(sel ? UiTheme.Accent : UiTheme.Surface)) e.Graphics.FillRectangle(br, e.Bounds);
                Color fg = sel ? Color.White : _level.Enabled ? UiTheme.Ink : UiTheme.Muted;
                using (var bold = new Font("Segoe UI", 9f, FontStyle.Bold))
                    TextRenderer.DrawText(e.Graphics, L.T(it[0]), bold, new Point(e.Bounds.X + 8, e.Bounds.Y + 4), fg);
                TextRenderer.DrawText(e.Graphics, L.T(it[1]), UiTheme.Base, new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 22, e.Bounds.Width - 12, 20),
                    sel ? Color.White : UiTheme.Muted, TextFormatFlags.EndEllipsis);
            };
            p.Controls.Add(_level);

            p.Controls.Add(new Label { Text = L.T("File del registro:"), AutoSize = true, Margin = new Padding(0, 14, 0, 4) });
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            _path.Text = Log.FilePath;
            var open = new Button { Text = L.T("Apri"), AutoSize = true };
            var folder = new Button { Text = L.T("Cartella"), AutoSize = true };
            open.Click += (s, e) =>
            {
                try
                {
                    if (!File.Exists(Log.FilePath)) { Directory.CreateDirectory(Log.Folder); File.AppendAllText(Log.FilePath, ""); }
                    Process.Start("notepad.exe", "\"" + Log.FilePath + "\"");
                }
                catch { }
            };
            folder.Click += (s, e) => { try { Directory.CreateDirectory(Log.Folder); Process.Start("explorer.exe", "\"" + Log.Folder + "\""); } catch { } };
            row.Controls.Add(_path); row.Controls.Add(open); row.Controls.Add(folder);
            p.Controls.Add(row);
            return p;
        }

        private Panel PageDiag()
        {
            var p = Stack();
            p.Controls.Add(Title(L.T("Diagnostica")));
            p.Controls.Add(Note(BuildInfo.Describe()));
            _levels.Text = L.T("Colori per livello dell'octree");
            p.Controls.Add(_levels);
            p.Controls.Add(Note(L.T("Ogni livello ha un colore (rosso il più grossolano, poi arancio, giallo, verde, azzurro, blu, viola…). Punti grandi a colori pieni, punti di un pixel più chiari. Serve a vedere quali blocchi si disegnano e dove.")));
            p.Controls.Add(Note(L.T("La riga di stato con punti, blocchi da caricare e da costruire è in Visibilità › Gestisci.")));
            _probe.Text = L.T("Prova DirectX (sperimentale)");
            p.Controls.Add(_probe);
            p.Controls.Add(Note(L.T("Carica la libreria PdRender.dll e scrive nel registro, ogni 5 secondi, se il disegno di Revit passa da un contesto DirectX 11 che possiamo usare. Non cambia la visualizzazione. Serve Revit riavviato dopo l'attivazione.")));
            // voce 309
            var reload = new Button { Text = L.T("Ricarica PdRender.dll"), AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
            reload.Click += (s2, e2) => { NativeProbe.RequestReload(); reload.Text = L.T("Ricarica chiesta: guarda il registro"); };
            p.Controls.Add(reload);
            p.Controls.Add(Note(L.T("Toglie gli agganci della libreria nativa, la scarica e carica la PdRender.dll appena compilata, senza riavviare Revit. Si usa dopo aver ricompilato solo la libreria nativa; le modifiche al plugin (C#) richiedono ancora il riavvio. La prima volta serve un riavvio con una libreria che sa togliere i suoi agganci (dalla voce 309).")));
            return p;
        }

        private Panel PagePerf()
        {
            var p = Stack();
            p.Controls.Add(Title(L.T("Prestazioni")));
            _profile.Items.Add(string.Format(L.T("Automatico (questo computer: {0})"), L.T(Hardware.Detected.ToString())));
            foreach (string n in new[] { "Leggero", "Medio", "Potente", "Personalizzato" }) _profile.Items.Add(L.T(n));
            _profile.SelectedIndexChanged += (s, e) => ShowProfile();
            p.Controls.Add(Row(L.T("Profilo"), _profile));
            p.Controls.Add(_profileInfo);
            p.Controls.Add(Row(L.T("Limite di punti disegnati (% della qualità scelta)"), _budget));
            p.Controls.Add(Note(L.T("Più punti danno più dettaglio ma chiedono più memoria video e rendono la navigazione meno fluida.")));
            _fixedBudget.Text = L.T("Limite di punti fisso");
            p.Controls.Add(_fixedBudget);
            p.Controls.Add(Note(L.T("Spento (consigliato): il limite si adatta alla velocità di disegno misurata, scendendo quando la scheda video è lenta (altri programmi aperti, batteria) e risalendo quando c'è margine. Acceso: vale sempre il limite del profilo.")));
            p.Controls.Add(Row(L.T("Libera la memoria video dopo (secondi fuori vista)"), _gpuKeep));
            p.Controls.Add(Row(L.T("Libera la memoria del computer dopo (secondi fuori vista)"), _memKeep));
            p.Controls.Add(Note(L.T("Tempi lunghi: tornando su una zona appena vista è già pronta. Tempi brevi: meno memoria occupata.")));
            _holdView.Text = L.T("Navigazione: mostra la vista nuova solo quando è pronta");
            p.Controls.Add(_holdView);
            p.Controls.Add(Note(L.T("Spento (consigliato): zoomando e spostandosi il dettaglio compare man mano, zona per zona. Acceso: resta la vista precedente, ingrandita come un'immagine, e si passa a quella nuova tutta insieme quando è pronta (al massimo dopo 1,2 s).")));
            _uniform.Text = L.T("Navigazione: stesso dettaglio su tutta la vista");
            p.Controls.Add(_uniform);
            p.Controls.Add(Note(L.T("Spento (consigliato): dettaglio pieno al centro della finestra, sempre più rado verso i bordi; si leggono e si preparano molti meno punti. Acceso: tutta la vista con lo stesso dettaglio.")));
            _native.Text = L.T("Prova: punti disegnati da Revit");
            p.Controls.Add(_native);
            p.Controls.Add(Note(L.T("Spento (consigliato): i punti li disegna il plugin. Acceso: li disegna Revit come le sue nuvole (punti tondi, grandezza che segue lo zoom senza ricostruzioni), senza colori per deviazione, fuoco e «Dimensione punti». Dopo il cambio ricarica la nuvola in Gestisci › Gestisci collegamenti › Nuvole di punti › Ricarica.")));
            _shader.Text = L.T("Disegno con shader (sperimentale)");
            p.Controls.Add(_shader);
            p.Controls.Add(Note(L.T("Acceso: la nuvola la disegna la libreria PdRender.dll direttamente nella scheda video, con punti tondi la cui grandezza segue lo zoom senza ricostruire nulla. Serve PdRender.dll accanto al plugin e Revit riavviato. Finché la libreria non è pronta si usa il disegno di sempre. Salva il progetto prima di provarlo.")));
            _beacon.Text = L.T("Camera dal faro con i collegamenti");
            p.Controls.Add(_beacon);
            p.Controls.Add(Note(L.T("Solo con il disegno con shader. Acceso (consigliato): nelle immagini che Revit ridisegna in movimento senza chiamare il plugin (con i collegamenti accesi sono molte) la nuvola usa la camera ricavata da pochi punti invisibili consegnati a Revit e segue il modello. Spento: in quelle immagini la nuvola resta indietro di qualche fotogramma. Dopo il cambio ricarica la nuvola in Gestisci › Gestisci collegamenti › Nuvole di punti › Ricarica.")));
            _meshDepth.Text = L.T("In movimento: profondità del modello dalla mesh");
            p.Controls.Add(_meshDepth);
            p.Controls.Add(Note(L.T("Solo con il disegno con shader e la camera dal faro. Acceso (consigliato): nelle immagini che Revit mostra in movimento, la parte del modello che Revit non ha ancora ridisegnato copre la nuvola grazie a una mesh del modello e dei collegamenti visibili, preparata da sola in background per la vista 3D attiva (qualche secondo, Revit resta usabile) e rifatta dopo le modifiche; finché non è pronta si usa quella dell'ultima analisi, se c'è.")));
            _reproj.Text = L.T("In movimento: profondità dall'ultima immagine completa");
            p.Controls.Add(_reproj);
            p.Controls.Add(Note(L.T("Solo con il disegno con shader e la camera dal faro. Acceso (consigliato): nelle immagini in movimento la profondità che Revit ha scritto nell'ultima immagine completa si sposta con la camera nuova (lo fa la scheda video, circa 1 ms) e copre la nuvola dove Revit non ha ancora ridisegnato il modello. Non serve leggere la geometria. Dove nell'ultima immagine completa non si vedeva niente (bordi che entrano nell'inquadratura, zone scoperte) decide la mesh, se accesa. Non sposta né la nuvola né il modello: decide solo quale dei due si vede.")));
            _meshFine.Text = L.T("Mesh: dettaglio fine");
            p.Controls.Add(_meshFine);
            p.Controls.Add(Note(L.T("Solo con la profondità dalla mesh. Spento (consigliato): la mesh si legge a dettaglio grossolano, con le superfici curve divise in meno triangoli e senza il calcolo delle normali (per la profondità non servono): molto più veloce e leggera. Acceso: dettaglio fine e lettura completa come prima.")));
            _meshSmall.Text = L.T("Mesh: anche gli elementi piccoli");
            p.Controls.Add(_meshSmall);
            p.Controls.Add(Note(L.T("Solo con la profondità dalla mesh e dettaglio grossolano. Spento (consigliato): gli elementi con tutte le misure sotto 30 cm (accessori, piccoli arredi) restano fuori dalla mesh; in movimento la nuvola può vedersi davanti a loro per un attimo, finché Revit non ridisegna. Acceso: tutti gli elementi.")));
            _meshNoLinks.Text = L.T("Mesh anche senza collegamenti");
            p.Controls.Add(_meshNoLinks);
            p.Controls.Add(Note(L.T("Solo con la profondità dalla mesh. Spento (consigliato): la mesh del modello si prepara solo nelle viste 3D con collegamenti visibili, dove serve (senza collegamenti la profondità di Revit basta) e Revit resta più leggero. Acceso: la mesh si prepara in tutte le viste 3D.")));
            _meshCache.Text = L.T("Mesh: rifai solo le parti cambiate");
            p.Controls.Add(_meshCache);
            p.Controls.Add(Note(L.T("Solo con la profondità dalla mesh. Acceso (consigliato): la mesh è divisa in parti (il modello in uso per ogni vista, ogni collegamento nella sua posizione) tenute in memoria; dopo una modifica o un cambio di vista si rifà solo la parte cambiata. Spento: ogni volta si rilegge tutto (decine di secondi con collegamenti grandi).")));
            _meshBox.Text = L.T("Mesh: rifalla anche quando cambia il riquadro di sezione");
            p.Controls.Add(_meshBox);
            p.Controls.Add(Note(L.T("Solo con la profondità dalla mesh. Spento (consigliato): spostare il riquadro di sezione non rifà la mesh (i collegamenti la usano comunque per intero e il riquadro taglia anche la nuvola). Acceso: come prima, mesh rifatta a ogni modifica del riquadro.")));
            _meshDisk.Text = L.T("Mesh: salva i collegamenti su disco");
            p.Controls.Add(_meshDisk);
            p.Controls.Add(Note(L.T("Solo con la profondità dalla mesh. Acceso (consigliato): la mesh di ogni file collegato si salva in %LOCALAPPDATA%\\PointDeviation\\mesh (al massimo 1 GB) e alla riapertura di Revit si rilegge da lì in un attimo, finché il file collegato non cambia. Spento: a ogni apertura si rilegge la geometria dei collegamenti (decine di secondi).")));
            _meshIdle.Text = L.T("Mesh: prepara solo con mouse e tastiera fermi");
            p.Controls.Add(_meshIdle);
            p.Controls.Add(Note(L.T("Solo con la profondità dalla mesh. Acceso (consigliato): la geometria per la mesh si legge quando non usi mouse e tastiera da 1 s (al massimo un passo ogni 3 s mentre lavori), perché un passo può fermare Revit per qualche centinaio di ms. Spento: si legge sempre, anche mentre ti muovi nella vista.")));
            _hangWatch.Text = L.T("Registra i blocchi di Revit");
            p.Controls.Add(_hangWatch);
            p.Controls.Add(Note(L.T("Acceso (consigliato): quando Windows segna Revit come «Non risponde», nel registro (sezione [Blocco]) si scrivono l'ora, quanta CPU usa Revit e se in quel momento stava lavorando il plugin. Non cambia il disegno.")));
            _meshTol.Text = L.T("In movimento: stessa tolleranza di Revit sulle superfici del modello");
            p.Controls.Add(_meshTol);
            p.Controls.Add(Note(L.T("Solo con la profondità dalla mesh. Acceso (consigliato): in movimento la mesh usa lo stesso scarto di profondità che Revit usa da fermo per il suo modello (misurato nei fotogrammi completi; finché non c'è, 1 cm), così i punti sulle superfici modellate si vedono come da fermo. Non sposta né la nuvola né il modello: decide solo quale dei due si vede. Spento: in movimento le facciate possono comparire a chiazze.")));
            _flatFaro.Text = L.T("In pianta e sezione: nuvola nello stesso punto dell'RCP");
            p.Controls.Add(_flatFaro);
            p.Controls.Add(Note(L.T("Solo con il disegno con shader. Acceso (consigliato): in piante, sezioni e prospetti Revit decide cosa sta sopra con l'ordine dei disegni; la nuvola si disegna nel punto in cui Revit disegna le sue nuvole (come un RCP), in tutte le immagini, con la camera dell'ultimo fotogramma completo: niente più nuvola che salta sopra e sotto il modello. Mentre sposti la vista può restare indietro di un attimo. Spento: come prima (voce 294).")));
            _flatFaroDepth.Text = L.T("In pianta e sezione: profondità come l'RCP");
            p.Controls.Add(_flatFaroDepth);
            p.Controls.Add(Note(L.T("Solo con «nuvola nello stesso punto dell'RCP». Acceso (consigliato): al posto del faro la nuvola usa le stesse regole di profondità che Revit usa per le sue nuvole, così si vede come un RCP invece di sparire dietro il modello in un'immagine sì e una no. Non sposta né la nuvola né il modello: decide solo quale dei due si vede. Spento: regole di profondità del plugin (voce 297).")));
            _flatFaroKeep.Text = L.T("In pianta e sezione: niente ridisegno sopra l'interfaccia al passaggio del mouse");
            p.Controls.Add(_flatFaroKeep);
            p.Controls.Add(Note(L.T("Solo con «nuvola nello stesso punto dell'RCP». Acceso (consigliato): quando passi col mouse Revit non ridisegna il modello e riusa l'immagine di prima, che contiene già la nuvola; il plugin non la ridisegna sopra, così non salta sopra e sotto i riempimenti del modello a ogni movimento. Non sposta né la nuvola né il modello. Spento: come la 298 (nuvola ridisegnata in ogni immagine).")));
            _flatFaroAlpha.Text = L.T("In pianta e sezione: nuvola trasparente solo al posto dell'RCP");
            p.Controls.Add(_flatFaroAlpha);
            p.Controls.Add(Note(L.T("Solo con «nuvola nello stesso punto dell'RCP» e con la trasparenza del profilo. Acceso (consigliato): la nuvola trasparente si disegna una volta sola, nel punto in cui Revit disegna le sue nuvole; prima si disegnava anche sopra tutto il modello, con la camera di un altro momento, e durante gli spostamenti si vedevano due nuvole. Non sposta né la nuvola né il modello. Spento: come la 300.")));
            _flatFaroCamera.Text = L.T("In pianta e sezione: camera dell'immagine attuale al posto dell'RCP");
            p.Controls.Add(_flatFaroCamera);
            p.Controls.Add(Note(L.T("Solo con «nuvola nello stesso punto dell'RCP». Acceso (consigliato): la nuvola usa la camera che Revit sta usando in quella immagine, letta dal faro, così segue il modello mentre sposti o zoomi. Spento: camera dell'ultimo fotogramma completo, che arriva dopo il faro, quindi la nuvola resta indietro di un'immagine (come la 301). Non sposta i punti: cambia solo il momento da cui si prende la camera.")));
            _flatHoverFix.Text = L.T("In pianta e sezione: passaggio del mouse senza nuvola sopra i riempimenti");
            p.Controls.Add(_flatHoverFix);
            p.Controls.Add(Note(L.T("Solo con «nuvola nello stesso punto dell'RCP». Acceso (consigliato): quando passi col mouse Revit aggiorna solo pochi elementi sopra l'immagine di prima, che contiene già la nuvola sotto i riempimenti; il plugin non la ridisegna sopra e, se l'ha appena disegnata al posto del faro, rimette l'immagine com'era. Non sposta né la nuvola né il modello: decide solo chi copre chi. Spento: come la 302.")));
            _flatHoverRestore.Text = L.T("In pianta e sezione, passaggio del mouse: ridisegna e rimetti la copia (303)");
            p.Controls.Add(_flatHoverRestore);
            p.Controls.Add(Note(L.T("Solo con «passaggio del mouse senza nuvola sopra i riempimenti». Spento (consigliato, voce 304): nelle immagini in cui Revit aggiorna solo pochi elementi la nuvola non si ridisegna al posto del faro, l'immagine di prima la contiene già. Acceso: la nuvola si ridisegna e poi si rimette la copia della scena, come nella 303. Serve a confrontare le due strade se vedi sfarfallio.")));
            _fastMatrix.Text = L.T("Ricerca rapida della matrice dopo il cambio di vista");
            p.Controls.Add(_fastMatrix);
            p.Controls.Add(Note(L.T("Solo con il disegno con shader. Acceso (consigliato): aprendo una vista, se la camera di Revit non si trova subito, la si cerca di nuovo a ogni immagine invece che una ogni 30, così la nuvola compare prima (in sezione ci metteva più di 5 secondi). Spento: come prima.")));
            _otherBuf.Text = L.T("Ignora i segnali di un altro passaggio di Revit");
            p.Controls.Add(_otherBuf);
            p.Controls.Add(Note(L.T("Solo con il disegno con shader. Acceso (consigliato): se Revit disegna il segnale del plugin più volte per immagine con buffer diversi (succede in 3D), si usa solo quello con la camera già trovata, invece di cercarla di nuovo ogni volta (la nuvola spariva per secondi). Spento: come prima.")));
            _flatPost.Text = L.T("In pianta e sezione: nuvola sotto i riempimenti del modello");
            p.Controls.Add(_flatPost);
            p.Controls.Add(Note(L.T("Solo con il disegno con shader. Acceso (consigliato): in piante, sezioni e prospetti Revit decide cosa sta sopra con l'ordine dei disegni, non con la profondità. Nelle immagini che Revit mostra mentre sposti, zoomi o passi col mouse la nuvola si disegna nello stesso punto dei fotogrammi completi (prima dei riempimenti del modello), invece che sopra tutto. Spento: in quelle immagini la nuvola può coprire il modello.")));
            _flatSimple.Text = L.T("In pianta e sezione: disegno semplice (come prima)");
            p.Controls.Add(_flatSimple);
            p.Controls.Add(Note(L.T("Solo con il disegno con shader. Spento (consigliato): piante, sezioni e prospetti seguono le stesse regole della vista 3D. Acceso, come riserva se nelle viste 2D qualcosa non va: la nuvola si confronta con la profondità che Revit ha in quel fotogramma, senza le correzioni per i fotogrammi parziali (mouse sopra un elemento). In entrambi i casi l'interfaccia resta sopra la nuvola e le correzioni della camera restano.")));
            _flatBeacon.Text = L.T("In pianta e sezione: camera dal faro");
            p.Controls.Add(_flatBeacon);
            p.Controls.Add(Note(L.T("Solo con il disegno con shader. Spento (consigliato): in piante, sezioni e prospetti le immagini che Revit mostra mentre sposti o zoomi usano la camera dell'ultimo fotogramma completo, come prima del faro (la nuvola puo' restare indietro di un attimo ma resta al suo posto). Acceso: anche li' si usa la camera del faro, che in queste viste spesso non coincide con quella del modello.")));
            _flatFix.Text = L.T("In pianta e sezione: profondità del fotogramma completo");
            p.Controls.Add(_flatFix);
            p.Controls.Add(Note(L.T("Solo con il disegno con shader e la camera dal faro. Acceso (consigliato): in piante, sezioni e prospetti, mentre sposti o zoomi, la nuvola si confronta con la profondità del modello dell'ultimo fotogramma completo, come da fermo, invece che con quella approssimata del faro: non passa più sopra e sotto il modello. Non sposta né la nuvola né il modello: decide solo quale dei due si vede. Spento: comportamento della voce 281.")));
            _fullWait.Text = L.T("Analisi: aspetta il calcolo completo delle distanze");
            p.Controls.Add(_fullWait);
            p.Controls.Add(Note(L.T("Spento (consigliato): la finestra si chiude quando sono pronti i livelli principali (circa un terzo del tempo) e il dettaglio fine si completa in background. Acceso: si aspetta tutto, poi ogni zoom è pronto e la percentuale di punti vicini al modello è esatta.")));
            return p;
        }

        /// <summary>Mostra i valori del profilo scelto; si modificano solo con «Personalizzato».</summary>
        private void ShowProfile()
        {
            var prof = (HardwareProfile)Math.Max(0, _profile.SelectedIndex);
            bool custom = prof == HardwareProfile.Personalizzato;
            _budget.Enabled = _gpuKeep.Enabled = _memKeep.Enabled = custom;
            PerfValues v = PerfValues.For(prof == HardwareProfile.Automatico || custom ? Hardware.Detected : prof);
            if (!custom)
            {
                _budget.Value = Math.Max(_budget.Minimum, Math.Min(_budget.Maximum, v.BudgetPercent));
                _gpuKeep.Value = Math.Max(_gpuKeep.Minimum, Math.Min(_gpuKeep.Maximum, v.GpuKeepSeconds));
                _memKeep.Value = Math.Max(_memKeep.Minimum, Math.Min(_memKeep.Maximum, v.MemKeepSeconds));
            }
            _profileInfo.Text = string.Format(L.T("Computer: {0}. Thread di preparazione {1}, di lettura {2}; precaricamento attorno alla vista {3}. I thread cambiano al riavvio di Revit."),
                Hardware.Describe(), v.BakerThreads, v.LoaderThreads, v.PrefetchMargin > 0 ? L.T("attivo") : L.T("spento"));
        }

        private static Control Row(string text, Control c)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 8, 0, 0) };
            row.Controls.Add(new Label { Text = text, AutoSize = true, Width = 360, Margin = new Padding(0, 5, 10, 0) });
            row.Controls.Add(c);
            return row;
        }

        private void LoadValues()
        {
            AdvancedSettings a = AdvancedSettings.Current;
            _logOn.Checked = a.LogLevel != LogLevel.Spento;
            _level.SelectedIndex = Math.Max(0, (int)(a.LogLevel == LogLevel.Spento ? LogLevel.Info : a.LogLevel) - 1);
            _level.Enabled = _logOn.Checked;
            _levels.Checked = CloudDebug.Levels;
            _budget.Value = a.BudgetPercent;
            _gpuKeep.Value = a.GpuKeepSeconds;
            _memKeep.Value = a.MemKeepSeconds;
            _profile.SelectedIndex = (int)a.Profile;
            ShowProfile();
            _fullWait.Checked = a.AnalysisFullWait;
            _holdView.Checked = a.HoldViewUntilReady;
            _uniform.Checked = a.UniformDetail;
            _fixedBudget.Checked = a.FixedBudget;
            _native.Checked = a.NativeDisplay;
            _beacon.Checked = a.BeaconCamera;
            _meshDepth.Checked = !a.NoMeshDepth;
            _meshTol.Checked = !a.NoMeshTolerance;
            _flatFix.Checked = !a.NoFlatBeaconFix;
            _flatBeacon.Checked = a.FlatBeacon;
            _flatSimple.Checked = a.FlatSimple;
            _flatPost.Checked = !a.NoFlatPostDraw;
            _meshNoLinks.Checked = a.MeshWithoutLinks;
            _meshCache.Checked = !a.NoMeshCache;
            _meshBox.Checked = a.MeshOnSectionBox;
            _hangWatch.Checked = !a.NoHangWatch;
            _meshDisk.Checked = !a.NoMeshDisk;
            _meshIdle.Checked = !a.MeshWhileActive;
            _meshFine.Checked = a.MeshFineDetail;
            _meshSmall.Checked = a.MeshSmallElements;
            _reproj.Checked = !a.NoDepthReproject;
            _flatFaro.Checked = !a.NoFlatFaroDraw;
            _flatFaroDepth.Checked = !a.NoFlatFaroDepth;
            _flatFaroKeep.Checked = !a.NoFlatFaroKeep;
            _flatFaroAlpha.Checked = !a.NoFlatFaroAlpha;
            _flatFaroCamera.Checked = !a.NoFlatFaroCamera;
            _flatHoverFix.Checked = !a.NoFlatHoverFix;
            _flatHoverRestore.Checked = a.FlatHoverRestore;
            _fastMatrix.Checked = !a.NoFastMatrixRetry;
            _otherBuf.Checked = !a.NoOtherBufferSkip;
            _probe.Checked = a.NativeProbe;
            _shader.Checked = a.NativeShader;
        }

        private void Apply()
        {
            AdvancedSettings a = AdvancedSettings.Current;
            a.LogLevel = _logOn.Checked ? (LogLevel)(_level.SelectedIndex + 1) : LogLevel.Spento;
            a.Profile = (HardwareProfile)Math.Max(0, _profile.SelectedIndex);
            if (a.Profile == HardwareProfile.Personalizzato)
            {
                a.BudgetPercent = (int)_budget.Value;
                a.GpuKeepSeconds = (int)_gpuKeep.Value;
                a.MemKeepSeconds = (int)_memKeep.Value;
            }
            a.AnalysisFullWait = _fullWait.Checked;
            a.HoldViewUntilReady = _holdView.Checked;
            a.UniformDetail = _uniform.Checked;
            a.FixedBudget = _fixedBudget.Checked;
            a.NativeDisplay = _native.Checked;
            a.BeaconCamera = _beacon.Checked;
            a.NoMeshDepth = !_meshDepth.Checked;
            a.NoMeshTolerance = !_meshTol.Checked;
            a.NoFlatBeaconFix = !_flatFix.Checked;
            a.FlatBeacon = _flatBeacon.Checked;
            a.FlatSimple = _flatSimple.Checked;
            a.NoFlatPostDraw = !_flatPost.Checked;
            a.MeshWithoutLinks = _meshNoLinks.Checked;
            a.NoMeshCache = !_meshCache.Checked;
            a.MeshOnSectionBox = _meshBox.Checked;
            a.NoHangWatch = !_hangWatch.Checked;
            a.NoMeshDisk = !_meshDisk.Checked;
            a.MeshWhileActive = !_meshIdle.Checked;
            a.MeshFineDetail = _meshFine.Checked;
            a.MeshSmallElements = _meshSmall.Checked;
            a.NoDepthReproject = !_reproj.Checked;
            a.NoFlatFaroDraw = !_flatFaro.Checked;
            a.NoFlatFaroDepth = !_flatFaroDepth.Checked;
            a.NoFlatFaroKeep = !_flatFaroKeep.Checked;
            a.NoFlatFaroAlpha = !_flatFaroAlpha.Checked;
            a.NoFlatFaroCamera = !_flatFaroCamera.Checked;
            a.NoFlatHoverFix = !_flatHoverFix.Checked;
            a.FlatHoverRestore = _flatHoverRestore.Checked;
            a.NoFastMatrixRetry = !_fastMatrix.Checked;
            a.NoOtherBufferSkip = !_otherBuf.Checked;
            a.NativeProbe = _probe.Checked;
            a.NativeShader = _shader.Checked;
            a.Save();
            bool levelsChanged = CloudDebug.Levels != _levels.Checked;
            CloudDebug.Levels = _levels.Checked;
            if (levelsChanged) DeviationController.Apply(Job.Recolor);
            Redraw.Request();
        }
    }
}
