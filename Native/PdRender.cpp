// PdRender: libreria nativa sperimentale di PointDeviation (voce 180).
// Primo passo: verifica se, durante il disegno del plugin dentro Revit, si può intercettare il contesto
// DirectX 11 che Revit sta usando (con la sua destinazione e il suo buffer di profondità), per poterci poi
// disegnare i punti con shader propri. Questa versione NON disegna nulla: osserva e riferisce.
//
// Compilazione: aprire «x64 Native Tools Command Prompt for VS» e lanciare build.bat in questa cartella.

#include <windows.h>
#include <d3d11_4.h>
#include <dxgi1_2.h>
#include <d3dcompiler.h>
#include <atomic>
#include <cstdio>
#include <cstdarg>
#include <cstring>
#include <cmath>
#include <unordered_map>
#include <vector>
#include <algorithm>
#include <string>
#include <mutex>
#include <dxgi1_4.h>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "d3dcompiler.lib")

// =====================================================================================================================
// voce 234: diario dei fotogrammi. Ogni evento del disegno (segnale, Present completo o interrotto, fotogramma saltato
// con il motivo, controllo della matrice, tempo della scheda video) diventa una riga «ms;tipo;campi». Il plugin le
// preleva a ogni fotogramma (PdTraceTake) e le scrive nel file del diario insieme alle sue, con lo stesso orologio
// (QueryPerformanceCounter, come Stopwatch in .NET), cosi' un difetto visto in un video si ritrova riga per riga.
// =====================================================================================================================
static std::mutex g_ftMx;
static std::string g_ft;
static volatile bool g_ftOn = false;
static double QpcMs()
{
    static double inv = 0;
    LARGE_INTEGER c; QueryPerformanceCounter(&c);
    if (inv == 0) { LARGE_INTEGER f; QueryPerformanceFrequency(&f); inv = 1000.0 / (double)f.QuadPart; }
    return (double)c.QuadPart * inv;
}
static void FT(const char* fmt, ...)
{
    if (!g_ftOn) return;
    char b[4096];   // voce 262: era 320, le righe X;sequenza si troncavano
    int n = _snprintf_s(b, sizeof(b), _TRUNCATE, "%.1f;", QpcMs());
    va_list a; va_start(a, fmt);
    if (n > 0) _vsnprintf_s(b + n, sizeof(b) - n, _TRUNCATE, fmt, a);
    va_end(a);
    std::lock_guard<std::mutex> l(g_ftMx);
    if (g_ft.size() < (2u << 20)) { g_ft += b; g_ft += '\n'; }
}
// voce 234: memoria della scheda video usata dal processo (Revit intero) e disponibile, in MB; 0 se non si legge
static ID3D11Device* g_realDevFwd();
extern "C" __declspec(dllexport) int PdVideoMem(double* usedMB, double* budgetMB)
{
    if (usedMB) *usedMB = 0; if (budgetMB) *budgetMB = 0;
    ID3D11Device* dev = g_realDevFwd(); if (!dev) return 0;
    IDXGIDevice* dd = nullptr; if (FAILED(dev->QueryInterface(__uuidof(IDXGIDevice), (void**)&dd)) || !dd) return 0;
    IDXGIAdapter* ad = nullptr; dd->GetAdapter(&ad); dd->Release(); if (!ad) return 0;
    IDXGIAdapter3* a3 = nullptr; int ok = 0;
    if (SUCCEEDED(ad->QueryInterface(__uuidof(IDXGIAdapter3), (void**)&a3)) && a3)
    {
        DXGI_QUERY_VIDEO_MEMORY_INFO mi = {};
        if (SUCCEEDED(a3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &mi)))
        { if (usedMB) *usedMB = mi.CurrentUsage / 1048576.0; if (budgetMB) *budgetMB = mi.Budget / 1048576.0; ok = 1; }
        a3->Release();
    }
    ad->Release();
    return ok;
}
extern "C" __declspec(dllexport) void PdTraceOn(int on) { g_ftOn = on != 0; if (!on) { std::lock_guard<std::mutex> l(g_ftMx); g_ft.clear(); } }
// Copia in buf al massimo cap-1 caratteri del diario (righe intere) e li toglie; restituisce quanti.
extern "C" __declspec(dllexport) int PdTraceTake(char* buf, int cap)
{
    if (!buf || cap < 2) return 0;
    std::lock_guard<std::mutex> l(g_ftMx);
    if (g_ft.empty()) { buf[0] = 0; return 0; }
    size_t n = g_ft.size() < (size_t)(cap - 1) ? g_ft.size() : (size_t)(cap - 1);
    if (n < g_ft.size()) { size_t k = g_ft.rfind('\n', n - 1); if (k != std::string::npos) n = k + 1; }
    memcpy(buf, g_ft.data(), n); buf[n] = 0;
    g_ft.erase(0, n);
    return (int)n;
}

typedef void (STDMETHODCALLTYPE* DrawIndexed_t)(ID3D11DeviceContext*, UINT, UINT, INT);
typedef void (STDMETHODCALLTYPE* Draw_t)(ID3D11DeviceContext*, UINT, UINT);
typedef void (STDMETHODCALLTYPE* DrawIndexedInst_t)(ID3D11DeviceContext*, UINT, UINT, UINT, INT, UINT);
typedef void (STDMETHODCALLTYPE* DrawInst_t)(ID3D11DeviceContext*, UINT, UINT, UINT, UINT);

static DrawIndexed_t g_origDI = nullptr;
static Draw_t g_origD = nullptr;
static DrawIndexedInst_t g_origDII = nullptr;
static DrawInst_t g_origDInst = nullptr;

static std::atomic<int> g_capture{ 0 };
static std::atomic<long> g_calls{ 0 };
static std::atomic<long> g_otherThread{ 0 };
static ID3D11DeviceContext* volatile g_ctx = nullptr;
static DWORD g_thread = 0;
static bool g_hooked = false;

static void Capture(ID3D11DeviceContext* c)
{
    if (!g_capture.load(std::memory_order_relaxed)) return;
    if (GetCurrentThreadId() != g_thread) { g_otherThread++; return; }
    g_ctx = c;
    g_calls++;
}

// ---- segnale (voce 181): il plugin disegna a ogni fotogramma un oggetto invisibile di MarkerIndices indici; quando
// Revit lo manda davvero alla scheda video, qui si legge lo stato di quel momento (destinazione, profondità, finestra,
// matrici non disponibili): è il punto in cui poi disegneremo i punti con shader nostri.
static const UINT MarkerIndices = 3711;   // 1237 triangoli
static std::atomic<long> g_markers{ 0 };
static std::atomic<long> g_draws{ 0 };
static char g_markerInfo[600] = "";
static DWORD g_markerThread = 0;
static void DescribeState(ID3D11DeviceContext* c, char* buf, int cap);

// voce 216: il cubo di prova (189-193) e' stato tolto. Resta il punto guardato dalla camera, usato per riconoscere le
// matrici dei tasselli nei fotogrammi interrotti (213-215).
static float g_focus[3];
static ID3D11BlendState* g_bs = nullptr;

// ---- voce 190: la matrice di Revit. Quando Revit disegna il nostro segnale, nei constant buffer del suo vertex shader
// c'è la matrice con cui trasforma le coordinate del nostro contenuto. La si cerca confrontando, per alcuni punti
// attorno al punto guardato, la posizione a schermo che dà ogni gruppo di 16 numeri con quella data dalla nostra matrice.
// Trovata (slot, posizione, verso), la nuvola si disegna con quella matrice letta direttamente dal buffer di Revit
// (nessuna copia per fotogramma) e con il buffer di profondità di Revit acceso nel verso giusto.
static int g_mSlot = -1, g_mOff = 0, g_mT = 0, g_mRev = 0, g_mOff2 = -1, g_mFound = 0;
static long g_mTries = 0;
static char g_matInfo[300] = "matrice di Revit non ancora cercata";
static ID3D11DepthStencilState* g_dssLess = nullptr;
static ID3D11DepthStencilState* g_dssGreater = nullptr;

static void ClipOf(const float* M, int t, const float* p, float* out)
{
    for (int j = 0; j < 4; j++)
        out[j] = t ? (M[j * 4] * p[0] + M[j * 4 + 1] * p[1] + M[j * 4 + 2] * p[2] + M[j * 4 + 3])
                   : (p[0] * M[j] + p[1] * M[4 + j] + p[2] * M[8 + j] + M[12 + j]);
}
// vettore omogeneo (x,y,z,w) per matrice
static void Clip4(const float* M, int t, const float* q, float* out)
{
    for (int j = 0; j < 4; j++)
        out[j] = t ? (M[j * 4] * q[0] + M[j * 4 + 1] * q[1] + M[j * 4 + 2] * q[2] + M[j * 4 + 3] * q[3])
                   : (q[0] * M[j] + q[1] * M[4 + j] + q[2] * M[8 + j] + q[3] * M[12 + j]);
}

// camera del fotogramma dal plugin (voce 190): occhio/centro E, direzione di vista D, destra R, distanza del bersaglio T,
// mezza larghezza HalfW, prospettiva sì/no. Serve solo a riconoscere la matrice di Revit, non a disegnare.
static float g_camE[3], g_camD[3], g_camR[3], g_camT = 0, g_camHW = 0;
static int g_camPersp = 0;
static long long g_mView = -1;
// voce 214: generazione della vista (cambia con la vista o con la forma del buffer di Revit). La camera di riferimento
// per i fotogrammi interrotti vale solo se presa nella generazione corrente: aprendo la {3D} si vedeva per un attimo la
// nuvola piccola e storta, disegnata con la camera della vista precedente.
static long g_viewGen = 0, g_refGen = -1;
static UINT g_mWidth = 0;
static int g_mForce = 0;
// voce 198: la disposizione del buffer di Revit cambia da vista a vista (la matrice era alla riga 15 nella vista con il
// riquadro di sezione; in {3D} senza riquadro i punti e il cubo non comparivano): a ogni cambio di vista, o se cambia
// la dimensione del buffer, la matrice si cerca di nuovo.
// voce 215: cambiando vista l'elenco dei blocchi e la finestra restavano quelli della vista precedente finche' il plugin
// non mandava il nuovo elenco: per un attimo si vedeva la nuvola della vista di prima, senza il riquadro di sezione.
static void ClearForNewView();
// voce 286: vista piana (pianta, sezione, prospetto). Li' la camera ricavata dal faro non e' quella del modello (diario del
// 24/11 17:21: righe diverse dalla camera vera del 95%, «camera dal faro uguale alla vera 0 su 13») e la nuvola, nelle
// immagini intermedie, finiva fuori posto o fuori dalla finestra. Nelle viste piane il faro non si usa: le immagini
// intermedie tornano al sistema di prima (camera dell'ultimo fotogramma, profondita' copiata).
static bool g_flatView = false, g_flatBeacon = false;
// voce 290: piante e sezioni come prima di oggi (senza 229-238 «fotogrammi parziali», 287, 289; la 270, nuvola prima
// dell'interfaccia, resta - voce 291):
// la nuvola si confronta con la profondita' che Revit ha in quel fotogramma; nelle immagini intermedie (209) resta la
// copia dell'ultimo fotogramma completo, come sempre. Le correzioni della camera (285, 286, 288) restano.
static bool g_flatSimple = false;   // voce 293: spento di serie
extern "C" __declspec(dllexport) void PdSetFlatSimple(int on) { g_flatSimple = on != 0; }
static bool FlatSimple() { return g_flatView && g_flatSimple; }
// voce 294 (video del 25/11 10:29:25, pianta): in pianta e sezione Revit non mette il modello nella profondita' (al
// segnale e' un piano uniforme, z 0,547-0,550): cosa sta sopra lo decide l'ordine dei disegni. Nei fotogrammi completi
// la nuvola si disegna al segnale e i disegni che Revit fa dopo (riempimenti, 11-69 disegni) le passano sopra; nelle
// immagini intermedie la nuvola si disegnava alla fine della scena e copriva tutto («sopra il modello» cliccando con la
// rotella, zoomando, passando col mouse). Ora nei fotogrammi completi si impara il primo disegno di Revit dopo il segnale
// (shader e posizione nella scena) e nelle immagini intermedie la nuvola si disegna subito prima di quel disegno.
static bool g_postWant = false, g_postOk = false, g_postDone = false, g_postNow = false, g_noPost = false;
static void* g_postVS = nullptr; static void* g_postPS = nullptr; static long long g_postIdx = 0, g_imgStart = 0;
static long g_postDraws = 0, g_postLearn = 0;
extern "C" __declspec(dllexport) void PdSetFlatPostDraw(int on) { g_noPost = on == 0; }
// voce 297 (25/11 18:45: in pianta «continuiamo ad andare sopra e sotto»; la 294 riconosceva il punto giusto solo in
// 22 immagini su 933). In pianta e sezione l'ordine dei disegni decide cosa sta sopra, e Revit disegna le sue nuvole
// (l'RCP, e il nostro faro) nello stesso punto in TUTTE le immagini, complete e intermedie: per questo l'RCP non salta.
// Ora nelle viste piane la nuvola si disegna li', al posto del faro, in ogni immagine, con la camera dell'ultimo
// fotogramma completo; al segnale e nelle immagini intermedie non si disegna piu'. Stesso ordine dell'RCP ovunque.
static bool g_faroDrawn = false, g_noFaroDraw = false; static long g_faroDraws = 0, g_faroFail = 0;
// voce 298: al posto del faro si usa lo stato di profondita' che Revit ha impostato per le sue nuvole (come un RCP)
static bool g_noFaroDss = false; static ID3D11DepthStencilState* g_faroDss = nullptr; static UINT g_faroDssRef = 0;
static long g_faroVis = 0, g_faroHid = 0, g_faroDssUsed = 0; static char g_faroDssInfo[96] = "nessuno";
extern "C" __declspec(dllexport) void PdSetFlatFaroDepth(int on) { g_noFaroDss = on == 0; }
// voce 299: in pianta e sezione, nelle immagini in cui Revit non ridisegna la scena (passaggio del mouse) l'immagine
// della scena tenuta da Revit contiene gia' la nuvola disegnata al faro: ridisegnarla sopra l'interfaccia la metteva
// sopra i riempimenti del modello un'immagine si' e una no. Si ridisegna solo se Revit ha ridisegnato la scena senza faro.
static void* g_sceneRes = nullptr; static bool g_sceneBound = false, g_sceneDrawn = false, g_noFaroKeep = false; static long g_faroKept = 0;
extern "C" __declspec(dllexport) void PdSetFlatFaroKeep(int on) { g_noFaroKeep = on == 0; }
static void GoodSwap(bool flat);
extern "C" __declspec(dllexport) void PdSetFlatFaroDraw(int on) { g_noFaroDraw = on == 0; }
static bool FaroMode() { return g_flatView && !g_noFaroDraw; }
static bool FaroCovers() { return FaroMode() && (g_faroDrawn || (!g_noFaroKeep && g_sceneRes && !g_sceneDrawn)); }
extern "C" __declspec(dllexport) void PdSetFlatView(int flat, int useBeacon) { if ((flat != 0) != g_flatView) GoodSwap(flat != 0); g_flatView = flat != 0; g_flatBeacon = useBeacon != 0; }
extern "C" __declspec(dllexport) void PdSetView(long long viewId)
{
    if (viewId != g_mView) { g_mView = viewId; g_mSlot = -1; g_mForce = 1; g_viewGen++; ClearForNewView(); }
}
extern "C" __declspec(dllexport) void PdSetCameraInfo(const float* e, const float* d, const float* r, float target, float halfW, int persp)
{
    for (int i = 0; i < 3; i++) { g_camE[i] = e[i]; g_camD[i] = d[i]; g_camR[i] = r[i]; }
    g_camT = target; g_camHW = halfW; g_camPersp = persp;
}

// punteggio di una trasformazione (una o due matrici): <0 se non è quella dal modello allo schermo
static float Score(const float* A, int ta, const float* B, int tb, bool loose = false)
{
    float tgt[4], side[4], eye[4], far4[4];
    for (int i = 0; i < 3; i++)
    {
        float c = g_camPersp ? g_camE[i] + g_camD[i] * g_camT : g_camE[i];
        tgt[i] = c; side[i] = c + g_camR[i] * g_camHW; eye[i] = g_camE[i]; far4[i] = c + g_camD[i] * 10.0f;
    }
    tgt[3] = side[3] = eye[3] = far4[3] = 1;
    float ct[4], cs[4], ce[4], cf[4], tmp[4];
    Clip4(A, ta, tgt, ct); Clip4(A, ta, side, cs); Clip4(A, ta, eye, ce); Clip4(A, ta, far4, cf);
    if (B) { memcpy(tmp, ct, 16); Clip4(B, tb, tmp, ct); memcpy(tmp, cs, 16); Clip4(B, tb, tmp, cs);
             memcpy(tmp, ce, 16); Clip4(B, tb, tmp, ce); memcpy(tmp, cf, 16); Clip4(B, tb, tmp, cf); }
    if (!(ct[3] > 1e-6f) || !(cs[3] > 1e-6f)) return -1;
    float x = ct[0] / ct[3], y = ct[1] / ct[3], z = ct[2] / ct[3];
    if (!(fabsf(x) <= 1.5f && fabsf(y) <= 1.5f)) return -1;
    // voce 233: nel controllo della matrice gia' trovata non si guarda la profondita': il punto della camera puo' cadere
    // oltre i piani vicino/lontano che Revit adatta al modello (zoom verso un punto lontano), e la matrice giusta
    // risultava «non piu' valida»
    if (!loose && !(z >= -0.02f && z <= 1.02f)) return -1;
    float dx = fabsf(cs[0] / cs[3] - x);
    if (!(dx > 0.3f && dx < 3.0f)) return -1;                    // mezza finestra a destra: circa 1 in coordinate dello schermo
    if (g_camPersp) { if (!(fabsf(ce[3]) < 0.02f * ct[3])) return -1; }   // prospettiva: w nullo nell'occhio
    else { if (!(fabsf(cf[3] - ct[3]) < 1e-3f * fabsf(ct[3]))) return -1; } // parallela: w costante
    return 10.0f - (fabsf(x) + fabsf(y)) - fabsf(dx - 1.0f);
}

// voce 285 (pianta 009_1_0_FFL del 24/11 17:20): aperta la vista, la ricerca ha scelto una coppia di matrici (riga 2 e
// seconda matrice 7) che porta la camera al centro dello schermo ma non il modello: nei fotogrammi normali la nuvola
// finiva tutta fuori dalla finestra (67 disegni su 73, «nuvola fuori dalla finestra») e non si vedeva mai. Ora, se nei
// fotogrammi normali la nuvola esce tutta dalla finestra 3 volte di fila (con i blocchi che il plugin ha scelto perche'
// dentro l'inquadratura), quella posizione si scarta per questa vista e si cerca di nuovo; al massimo 4 volte per vista,
// e se non si trova altro si torna a quella di prima.
struct BadM { int slot, off, t, off2, t2; };
static BadM g_badM[8]; static int g_badN = 0; static long long g_badGen = -1;
static bool g_reSearch = false; static int g_reSearchN = 0; static long long g_reSearchGen = -1; static long g_reSearchDone = 0, g_reSearchKept = 0;
static int g_sigOut = 0;
static bool g_noReSearch = false;
extern "C" __declspec(dllexport) void PdSetMatrixReSearch(int on) { g_noReSearch = on == 0; }
// voce 288 (diario del 24/11 17:29-17:35): tornando in pianta dalle sezioni la ricerca sceglieva ogni volta una posizione
// diversa (righe 16, 13, 4, 1) mentre quella giusta, in pianta e in sezione, era sempre la riga 15. Si ricorda l'ultima
// posizione con cui la nuvola e' risultata dentro la finestra in 3 fotogrammi normali di fila e, cambiando vista, si
// prova prima quella: se passa i controlli della ricerca si tiene, senza cercarne altre.
static int g_goodSlot = -1, g_goodOff = 0, g_goodT = 0, g_goodOff2 = -1, g_goodT2 = 0; static UINT g_goodW = 0;
static int g_sigVis = 0; static long g_goodUsed = 0;
// voce 298: una posizione buona per le viste 3D e una per piante e sezioni (in 3D Revit usa un'altra posizione: provarla
// per prima in pianta portava alla riga 12 con la seconda matrice 4, nuvola ruotata e poi sparita)
struct GoodPos { int slot, off, t, off2, t2; UINT w; };
static GoodPos g_goodOther = { -1, 0, 0, -1, 0, 0 };
static void GoodSwap(bool)
{
    GoodPos cur = { g_goodSlot, g_goodOff, g_goodT, g_goodOff2, g_goodT2, g_goodW };
    g_goodSlot = g_goodOther.slot; g_goodOff = g_goodOther.off; g_goodT = g_goodOther.t; g_goodOff2 = g_goodOther.off2; g_goodT2 = g_goodOther.t2; g_goodW = g_goodOther.w;
    g_goodOther = cur;
}
static bool IsBadM(int slot, int off, int t, int off2, int t2)
{
    if (g_badGen != g_mView) return false;
    for (int i = 0; i < g_badN; i++)
        if (g_badM[i].slot == slot && g_badM[i].off == off && g_badM[i].t == t && g_badM[i].off2 == off2 && (off2 < 0 || g_badM[i].t2 == t2)) return true;
    return false;
}
static void FindRevitMatrixEx(ID3D11DeviceContext* c, bool loose)
{
    g_mTries++; g_sigVis = 0;
    if (g_camHW <= 0) { _snprintf_s(g_matInfo, sizeof(g_matInfo), _TRUNCATE, "camera non ancora ricevuta"); return; }
    ID3D11Device* dev = nullptr; c->GetDevice(&dev);
    if (!dev) return;
    ID3D11Buffer* cbs[4] = {};
    c->VSGetConstantBuffers(0, 4, cbs);
    int n = _snprintf_s(g_matInfo, sizeof(g_matInfo), _TRUNCATE, "buffer del vertex shader di Revit:");
    float best = -1; int bSlot = -1, bOff = 0, bT = 0, bOff2 = -1, bT2 = 0;
    for (int slot = 0; slot < 4; slot++)
    {
        if (!cbs[slot]) continue;
        D3D11_BUFFER_DESC d; cbs[slot]->GetDesc(&d);
        n += _snprintf_s(g_matInfo + n, sizeof(g_matInfo) - n, _TRUNCATE, " b%d=%u", slot, d.ByteWidth);
        D3D11_BUFFER_DESC sd = {}; sd.ByteWidth = d.ByteWidth; sd.Usage = D3D11_USAGE_STAGING; sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        ID3D11Buffer* st = nullptr;
        if (FAILED(dev->CreateBuffer(&sd, nullptr, &st)) || !st) continue;
        c->CopyResource(st, cbs[slot]);
        D3D11_MAPPED_SUBRESOURCE m;
        if (SUCCEEDED(c->Map(st, 0, D3D11_MAP_READ, 0, &m)))
        {
            const float* f = (const float*)m.pData;
            int nb = (int)(d.ByteWidth / 16) - 3;        // posizioni di 4 righe da 16 byte
            if (nb > 512) nb = 512;
            if (slot == g_goodSlot && d.ByteWidth == g_goodW && g_goodOff < nb && g_goodOff2 < nb && !IsBadM(g_goodSlot, g_goodOff, g_goodT, g_goodOff2, g_goodT2))
            {   // voce 288: prima la posizione che ha gia' funzionato
                // voce 292: basta il controllo senza la profondita' (nel diario delle 17:46-20:20, appena cambiata vista,
                // la posizione giusta non passava quello completo e la ricerca prendeva le righe 13, 12 o 0)
                float sc = Score(f + g_goodOff * 4, g_goodT, g_goodOff2 >= 0 ? f + g_goodOff2 * 4 : nullptr, g_goodT2, true);
                if (sc >= 0) { best = 1000.0f; bSlot = slot; bOff = g_goodOff; bT = g_goodT; bOff2 = g_goodOff2; bT2 = g_goodT2; g_goodUsed++; }
            }
            if (best < 0 && slot == g_goodOther.slot && d.ByteWidth == g_goodOther.w && g_goodOther.off < nb && g_goodOther.off2 < nb && !IsBadM(g_goodOther.slot, g_goodOther.off, g_goodOther.t, g_goodOther.off2, g_goodOther.t2))
            {   // voce 299: poi quella che ha funzionato nell'altro tipo di vista (dopo il riavvio, in sezione la 298 cercava 5 s)
                float sc = Score(f + g_goodOther.off * 4, g_goodOther.t, g_goodOther.off2 >= 0 ? f + g_goodOther.off2 * 4 : nullptr, g_goodOther.t2, true);
                if (sc >= 0) { best = 999.0f; bSlot = slot; bOff = g_goodOther.off; bT = g_goodOther.t; bOff2 = g_goodOther.off2; bT2 = g_goodOther.t2; g_goodUsed++; }
            }
            for (int o = 0; o < nb; o++)
                for (int t = 0; t < 2; t++)
                {
                    float sc = Score(f + o * 4, t, nullptr, 0, loose);
                    if (sc > best && !IsBadM(slot, o, t, -1, 0)) { best = sc; bSlot = slot; bOff = o; bT = t; bOff2 = -1; }
                }
            if (best < 0)   // nessuna matrice unica: coppie (vista e proiezione separate)
                for (int o = 0; o < nb; o++)
                    for (int o2 = 0; o2 < nb; o2++)
                    {
                        if (o2 >= o && o2 < o + 4) continue;
                        for (int t = 0; t < 2; t++)
                            for (int t2 = 0; t2 < 2; t2++)
                            {
                                float sc = Score(f + o * 4, t, f + o2 * 4, t2, loose);
                                if (sc > best && !IsBadM(slot, o, t, o2, t2)) { best = sc; bSlot = slot; bOff = o; bT = t; bOff2 = o2; bT2 = t2; }
                            }
                    }
            c->Unmap(st, 0);
        }
        st->Release();
    }
    for (int k = 0; k < 4; k++) if (cbs[k]) cbs[k]->Release();
    if (best >= 0)
    {
        g_mSlot = bSlot; g_mOff = bOff; g_mT = bT | (bT2 << 1); g_mOff2 = bOff2;
        { ID3D11Buffer* fb[4] = {}; c->VSGetConstantBuffers(0, 4, fb); if (fb[bSlot]) { D3D11_BUFFER_DESC fd; fb[bSlot]->GetDesc(&fd); g_mWidth = fd.ByteWidth; }
          for (int q = 0; q < 4; q++) if (fb[q]) fb[q]->Release(); }
        // verso della profondità con la trasformazione trovata: un punto più lontano ha z/w maggiore o minore?
        n += _snprintf_s(g_matInfo + n, sizeof(g_matInfo) - n, _TRUNCATE, "; TROVATA in b%d riga %d%s%s (%s)",
            bSlot, bOff, bOff2 >= 0 ? " per la riga " : "", bOff2 >= 0 ? "seconda matrice" : "", bT ? "per colonne" : "per righe");
        if (bOff2 >= 0) n += _snprintf_s(g_matInfo + n, sizeof(g_matInfo) - n, _TRUNCATE, " %d", bOff2);
        g_mFound = 1;
    }
    else n += _snprintf_s(g_matInfo + n, sizeof(g_matInfo) - n, _TRUNCATE, "; matrice NON trovata (tentativo %ld)", g_mTries);
    if (best >= 0 && loose) n += _snprintf_s(g_matInfo + n, sizeof(g_matInfo) - n, _TRUNCATE, " (senza il controllo della profondita')");
    if (best >= 0 && !g_dssLess)
    {
        D3D11_DEPTH_STENCIL_DESC dd = {}; dd.DepthEnable = TRUE; dd.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ZERO;
        dd.DepthFunc = D3D11_COMPARISON_LESS_EQUAL; dev->CreateDepthStencilState(&dd, &g_dssLess);
        dd.DepthFunc = D3D11_COMPARISON_GREATER_EQUAL; dev->CreateDepthStencilState(&dd, &g_dssGreater);
    }
    dev->Release();
}
// voce 233: se la ricerca normale non trova niente si riprova senza guardare la profondita' del punto della camera
static void FindRevitMatrix(ID3D11DeviceContext* c)
{
    FindRevitMatrixEx(c, false);
    if (g_mSlot < 0 && g_camHW > 0) FindRevitMatrixEx(c, true);
}

// voce 199: controllo della matrice trovata. Ogni 30 segnali si rilegge il buffer di Revit e si verifica che la
// matrice alla posizione trovata porti ancora la camera di questo fotogramma al centro dello schermo; se no si cerca
// di nuovo (nuvola che «orbita per conto suo» in {3D}: matrice valida al momento della ricerca ma non dopo).
static long g_mChecks = 0, g_mBad = 0, g_mKept = 0;
static bool CheckMatrix(ID3D11DeviceContext* c)
{
    ID3D11Buffer* b = nullptr; c->VSGetConstantBuffers(g_mSlot, 1, &b);
    if (!b) return false;
    ID3D11Device* dev = nullptr; c->GetDevice(&dev);
    bool ok = true;
    if (dev)
    {
        D3D11_BUFFER_DESC d; b->GetDesc(&d);
        D3D11_BUFFER_DESC sd = {}; sd.ByteWidth = d.ByteWidth; sd.Usage = D3D11_USAGE_STAGING; sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        ID3D11Buffer* st = nullptr;
        if (SUCCEEDED(dev->CreateBuffer(&sd, nullptr, &st)) && st)
        {
            c->CopyResource(st, b);
            D3D11_MAPPED_SUBRESOURCE m;
            if (SUCCEEDED(c->Map(st, 0, D3D11_MAP_READ, 0, &m)))
            {
                const float* f = (const float*)m.pData;
                ok = Score(f + g_mOff * 4, g_mT & 1, g_mOff2 >= 0 ? f + g_mOff2 * 4 : nullptr, (g_mT >> 1) & 1, true) >= 0;
                c->Unmap(st, 0);
            }
            st->Release();
        }
        dev->Release();
    }
    b->Release();
    g_mChecks++;
    if (!ok) g_mBad++;
    return ok;
}

// matrice di Revit valida per questo disegno? Se il buffer ha cambiato dimensione o è cambiata la vista si cerca di nuovo.
static bool EnsureMatrix(ID3D11DeviceContext* c)
{
    if (g_reSearch && g_mSlot >= 0)
    {   // voce 285: la posizione attuale mette la nuvola fuori dalla finestra
        g_reSearch = false;
        if (g_badGen != g_mView) { g_badGen = g_mView; g_badN = 0; }
        int t2 = (g_mT >> 1) & 1;
        if (g_badN < 8) g_badM[g_badN++] = BadM{ g_mSlot, g_mOff, g_mT & 1, g_mOff2, t2 };
        int os = g_mSlot, oo = g_mOff, ot = g_mT, oo2 = g_mOff2; UINT ow = g_mWidth;
        g_mSlot = -1; FindRevitMatrix(c);
        bool found = g_mSlot >= 0;
        if (!found) { g_mSlot = os; g_mOff = oo; g_mT = ot; g_mOff2 = oo2; g_mWidth = ow; g_reSearchKept++; g_reSearchN = 99; }
        else { g_reSearchDone++; g_viewGen++; }   // camere salvate con la posizione vecchia: non piu' valide
        g_sigOut = 0;
        FT("E;matrice_nuvola_fuori_finestra;scartata=b%d_riga%d_%d;ricerca=%s;b%d_riga=%d;seconda=%d", os, oo, oo2, found ? "trovata" : "niente_tenuta_precedente", g_mSlot, g_mOff, g_mOff2);
    }
    if (g_mSlot >= 0)
    {
        ID3D11Buffer* b = nullptr; c->VSGetConstantBuffers(g_mSlot, 1, &b);
        UINT w = 0; if (b) { D3D11_BUFFER_DESC d; b->GetDesc(&d); w = d.ByteWidth; b->Release(); }
        if (w != g_mWidth) { FT("X;buffer_cambiato=%u->%u", g_mWidth, w); g_mSlot = -1; g_mForce = 1; g_viewGen++; }
        else if ((g_markers.load() % 30) == 0 && !CheckMatrix(c))
        {
            // voce 233 (registro 20/11 14:58): un controllo fallito toglieva la matrice e, se la ricerca non trovava
            // nulla, la nuvola spariva per 15 s (matrice non pronta in 150 segnali); poi la ricerca ritrovava la stessa
            // posizione. Ora si cerca subito e, se non si trova niente, si tiene quella di prima.
            int os = g_mSlot, oo = g_mOff, ot = g_mT, oo2 = g_mOff2; UINT ow = g_mWidth;
            g_mSlot = -1; FindRevitMatrix(c);
            bool found = g_mSlot >= 0;
            if (g_mSlot < 0) { g_mSlot = os; g_mOff = oo; g_mT = ot; g_mOff2 = oo2; g_mWidth = ow; g_mKept++; }
            FT("X;controllo_matrice=fallito;ricerca=%s;b%d_riga=%d", found ? "trovata" : "niente_tenuta_precedente", g_mSlot, g_mOff);
        }
    }
    if (g_mSlot < 0 && (g_mForce || (g_markers.load() % 30) == 1)) { g_mForce = 0; FindRevitMatrix(c); FT("X;ricerca_matrice=%s;b%d_riga=%d", g_mSlot >= 0 ? "trovata" : "NON_trovata", g_mSlot, g_mOff); }
    return g_mSlot >= 0;
}


// =====================================================================================================================
//  voce 194: disegno della nuvola con shader nostri
//
//  I punti dei blocchi stanno in buffer nostri sul dispositivo di Revit (posizione nel modello + colore, 16 byte a
//  punto), creati dal plugin sul thread di Revit. A ogni fotogramma il plugin passa l'elenco dei blocchi da disegnare
//  (con quanti punti e la loro distanza nel modello); al segnale la libreria li disegna con la matrice di Revit e il
//  suo buffer di profondità. Ogni punto è un quadratino a schermo (4 vertici generati nello shader, istanze), con gli
//  angoli scartati: punti tondi. La grandezza la calcola lo shader a ogni fotogramma dalla distanza tra i punti del
//  blocco e dalla scala della matrice: allo zoom non si rifà nessun buffer. I piani del riquadro di sezione di Revit
//  (prime 6 righe del suo buffer) tagliano i punti come fa Revit.
// =====================================================================================================================
static ID3D11Device* g_realDev = nullptr;
static ID3D11Device* g_realDevFwd() { return g_realDev; }
struct PdNode { ID3D11Buffer* vb; ID3D11ShaderResourceView* srv; int count; };
struct PdItem { ID3D11ShaderResourceView* srv; int count; float spacing; float grow; };   // voce 240: grow = comparsa graduale (0..1)
static ID3D11Buffer* g_quadIb = nullptr;
static const int QuadMax = 262144;   // punti per chiamata di disegno (indici a 32 bit, 6 per punto)
static std::unordered_map<long long, PdNode> g_nodes;
static std::vector<PdItem> g_build, g_ready;
static std::atomic<int> g_shaderOn{ 0 };
static float g_shMaxPx = 3.0f, g_shScale = 0.6f;
// voce 217: opacita' dei punti (1 = opachi). Con la trasparenza i punti non scrivono la profondita' e si mescolano con
// quello che c'e' dietro; si disegnano al Present, quando Revit ha finito tutto il modello (vedi DrawAtPresent).
static float g_alpha = 1.0f;
static ID3D11BlendState* g_bsAlpha = nullptr;
static ID3D11DepthStencilState* g_sdssNoWrite = nullptr;
static ID3D11DepthStencilState* g_sdssOff = nullptr;   // voce 241: senza confronto di profondita'
static bool Transparent() { return g_alpha < 0.999f; }
static long long g_shBytes = 0;
static long g_shUploads = 0, g_shDrawn = 0, g_shFrames = 0;
static long long g_shPoints = 0;
static char g_shErr[200] = "";
static ID3D11VertexShader* g_svs = nullptr;
static ID3D11PixelShader* g_sps = nullptr;
static ID3D11InputLayout* g_sil = nullptr;
static ID3D11Buffer* g_scb = nullptr;
static ID3D11DepthStencilState* g_sdss = nullptr;
static ID3D11RasterizerState* g_srs = nullptr;
static ID3D11RasterizerState* g_srsSc = nullptr;   // voce 229: come g_srs ma con il rettangolo di ritaglio (forbici)
static bool g_useScissor = false;
static bool g_shFailed = false;

// voce 195: i punti si leggono dallo shader (Buffer<uint4>, 16 byte a punto) e si disegnano con DrawIndexed e un
// indice condiviso a 6 indici per punto: è la chiamata già verificata con il cubo. Con l'istanziamento (194) la nuvola
// non compariva. I piani del riquadro si usano solo se sono piani veri (normale lunga 1).
static const char* g_shHlsl =
"cbuffer C : register(b0) { int4 Info; float4 P; float4 Pl[6]; int4 Info2; float4 Bz; };\n"
"cbuffer R : register(b1) { float4 Rv[4096]; };\n"
"Buffer<uint4> Pts : register(t0);\n"
"struct VO { float4 pos : SV_Position; float4 c : COLOR; float2 uv : TEXCOORD0; float4 cd0 : SV_ClipDistance0; float2 cd1 : SV_ClipDistance1; };\n"
"VO VS(uint vid : SV_VertexID) {\n"
"  VO o; uint pi = vid >> 2; uint cr = vid & 3;\n"
"  uint4 d = Pts.Load(pi);\n"
"  float3 p = asfloat(d.xyz);\n"
"  float4 q = float4(p, 1);\n"
"  int r = Info.y;\n"
"  float4x4 M = float4x4(Rv[r], Rv[r + 1], Rv[r + 2], Rv[r + 3]);\n"
"  if ((Info.z & 1) == 0) M = transpose(M);\n"
"  if (Info2.x >= 0) { int r2 = Info2.x; float4x4 B = float4x4(Rv[r2], Rv[r2 + 1], Rv[r2 + 2], Rv[r2 + 3]);\n"
"    if (Info2.y == 0) B = transpose(B); M = mul(B, M); }\n"
"  float4 cl = mul(M, q);\n"
"  cl.z -= Bz.x * cl.w;\n"
"  float3 rx = M[0].xyz;\n"
"  float rpx = P.x * length(rx) / max(abs(cl.w), 1e-6) * P.y * 0.5;\n"
"  rpx = clamp(rpx, 0.75, P.w);\n"
"  float gr = asfloat(Info2.w); bool fading = gr > 0 && gr < 1; if (fading) rpx *= gr;\n"
"  float2 cn = float2((cr & 1) != 0 ? 1.0 : -1.0, (cr & 2) != 0 ? 1.0 : -1.0);\n"
"  cl.xy += cn * float2(rpx * 2.0 / P.y, rpx * 2.0 / P.z) * cl.w;\n"
"  o.pos = cl; o.uv = rpx < 1.25 ? cn * 0.5 : cn;\n"
"  float op = ((d.w >> 24) & 255) / 255.0;\n"
"  o.c = float4((d.w & 255) / 255.0, ((d.w >> 8) & 255) / 255.0, ((d.w >> 16) & 255) / 255.0, asfloat(Info2.z) * (fading ? gr : 1.0) * op);\n"
"  if ((Bz.y > 0.5 && Bz.y < 1.5 && op < 0.999) || (Bz.y > 1.5 && op >= 0.999)) o.pos = float4(2, 2, 2, 1);\n"
"  float dd[6];\n"
"  [unroll] for (int k = 0; k < 6; k++) dd[k] = Info.w != 0 ? dot(Pl[k].xyz, p) + Pl[k].w : 1.0;\n"
"  o.cd0 = float4(dd[0], dd[1], dd[2], dd[3]); o.cd1 = float2(dd[4], dd[5]);\n"
"  return o; }\n"
"float4 PS(VO i) : SV_Target { if (dot(i.uv, i.uv) > 1.0) discard; return i.c; }\n";

static bool ShaderInit(ID3D11Device* dev)
{
    if (g_svs) return true;
    if (g_shFailed) return false;
    ID3DBlob *vb = nullptr, *pb = nullptr, *err = nullptr;
    if (FAILED(D3DCompile(g_shHlsl, strlen(g_shHlsl), "pdpts", nullptr, nullptr, "VS", "vs_4_0", 0, 0, &vb, &err)))
    { _snprintf_s(g_shErr, sizeof(g_shErr), _TRUNCATE, "shader VS: %s", err ? (char*)err->GetBufferPointer() : "?"); g_shFailed = true; return false; }
    if (FAILED(D3DCompile(g_shHlsl, strlen(g_shHlsl), "pdpts", nullptr, nullptr, "PS", "ps_4_0", 0, 0, &pb, &err)))
    { _snprintf_s(g_shErr, sizeof(g_shErr), _TRUNCATE, "shader PS: %s", err ? (char*)err->GetBufferPointer() : "?"); g_shFailed = true; return false; }
    dev->CreateVertexShader(vb->GetBufferPointer(), vb->GetBufferSize(), nullptr, &g_svs);
    dev->CreatePixelShader(pb->GetBufferPointer(), pb->GetBufferSize(), nullptr, &g_sps);
    vb->Release(); pb->Release();
    {
        std::vector<unsigned int> idx((size_t)QuadMax * 6);
        for (int k = 0; k < QuadMax; k++)
        {
            unsigned int b = (unsigned int)k * 4;
            unsigned int* q = &idx[(size_t)k * 6];
            q[0] = b; q[1] = b + 1; q[2] = b + 2; q[3] = b + 2; q[4] = b + 1; q[5] = b + 3;
        }
        D3D11_BUFFER_DESC ib = {}; ib.ByteWidth = (UINT)(idx.size() * 4); ib.Usage = D3D11_USAGE_IMMUTABLE; ib.BindFlags = D3D11_BIND_INDEX_BUFFER;
        D3D11_SUBRESOURCE_DATA isd = {}; isd.pSysMem = idx.data();
        dev->CreateBuffer(&ib, &isd, &g_quadIb);
    }
    D3D11_BUFFER_DESC bd = {}; bd.ByteWidth = 160; bd.Usage = D3D11_USAGE_DYNAMIC; bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER; bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    dev->CreateBuffer(&bd, nullptr, &g_scb);
    D3D11_DEPTH_STENCIL_DESC dd = {}; dd.DepthEnable = TRUE; dd.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ALL; dd.DepthFunc = D3D11_COMPARISON_LESS_EQUAL;
    dev->CreateDepthStencilState(&dd, &g_sdss);
    D3D11_RASTERIZER_DESC rd = {}; rd.FillMode = D3D11_FILL_SOLID; rd.CullMode = D3D11_CULL_NONE; rd.DepthClipEnable = TRUE;
    dev->CreateRasterizerState(&rd, &g_srs);
    rd.ScissorEnable = TRUE; dev->CreateRasterizerState(&rd, &g_srsSc); rd.ScissorEnable = FALSE;
    if (!g_bs) { D3D11_BLEND_DESC bld = {}; bld.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL; dev->CreateBlendState(&bld, &g_bs); }
    {
        D3D11_BLEND_DESC bld = {}; D3D11_RENDER_TARGET_BLEND_DESC& rt = bld.RenderTarget[0];
        rt.BlendEnable = TRUE; rt.SrcBlend = D3D11_BLEND_SRC_ALPHA; rt.DestBlend = D3D11_BLEND_INV_SRC_ALPHA; rt.BlendOp = D3D11_BLEND_OP_ADD;
        rt.SrcBlendAlpha = D3D11_BLEND_ZERO; rt.DestBlendAlpha = D3D11_BLEND_ONE; rt.BlendOpAlpha = D3D11_BLEND_OP_ADD;
        rt.RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
        dev->CreateBlendState(&bld, &g_bsAlpha);
        dd.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ZERO;
        dev->CreateDepthStencilState(&dd, &g_sdssNoWrite);
        dd.DepthEnable = FALSE; dd.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ZERO;
        dev->CreateDepthStencilState(&dd, &g_sdssOff);
    }
    if (!g_svs || !g_sps || !g_quadIb || !g_scb || !g_sdss || !g_srs || !g_bs || !g_bsAlpha || !g_sdssNoWrite)
    { _snprintf_s(g_shErr, sizeof(g_shErr), _TRUNCATE, "creazione delle risorse dello shader dei punti non riuscita"); g_shFailed = true; return false; }
    return true;
}

static std::atomic<int> g_shaderWanted{ 0 };
// voce 201: tempo della scheda video per il nostro disegno (query di tempo DirectX, lette qualche fotogramma dopo
// senza aspettare). Il tempo misurato dal plugin comprende solo la preparazione dei comandi, non il disegno vero.
static const int QN = 6;
static ID3D11Query* g_qDisj[QN] = {}; static ID3D11Query* g_qBeg[QN] = {}; static ID3D11Query* g_qEnd[QN] = {};
static bool g_qPend[QN] = {};
// voce 241 (video e diario del 21/11 17:45:32, con i collegamenti): per secondi la nuvola non si vedeva affatto mentre
// Revit mostrava il modello in fil di ferro, anche se ogni Present risultava «disegnato». Per ogni disegno ora si misura
// quanti triangoli dei punti cadono nella finestra (statistiche della pipeline, CPrimitives) e quanti pixel superano il
// confronto di profondita' (occlusione). Triangoli in finestra ma zero pixel = la profondita' copiata copre tutto;
// zero triangoli in finestra = camera sbagliata. Nel diario: G;...;fonte=...;in_finestra=...;pixel=...
static ID3D11Query* g_qOcc[QN] = {}; static ID3D11Query* g_qStat[QN] = {};
static int g_qSrc[QN] = {}; static long long g_qPts[QN] = {}; static bool g_qNoDepth[QN] = {};
static int g_drawSrc = 0;          // 0 segnale, 1 Present di un fotogramma completo, 2 interrotto con la sua camera, 3 interrotto con la precedente
static bool g_noDepth = false;     // disegno senza confronto di profondita' (vedi g_depthOffUntil)
static double g_depthOffUntil = 0; // fino a quando i disegni al Present ignorano la profondita' copiata
static double g_badCurUntil = 0;   // fino a quando non si usano le camere trovate nei fotogrammi interrotti
static long g_qDepthBlock = 0, g_qCamOut = 0, g_qVisible = 0;
static const char* SrcName(int k) { return k == 0 ? "segnale" : k == 1 ? "present_completo" : k == 2 ? "interrotto_sua_camera" : k == 4 ? "faro" : "interrotto_precedente"; }
static int g_qIdx = 0;
static double g_gpuEma = -1, g_gpuSum = 0, g_gpuMax = 0; static long g_gpuN = 0;
static long g_skipMatrix = 0, g_skipEmpty = 0;
static void GpuCollect(ID3D11DeviceContext* c)
{
    for (int k = 0; k < QN; k++)
    {
        if (!g_qPend[k]) continue;
        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT dj;
        if (c->GetData(g_qDisj[k], &dj, sizeof(dj), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK) continue;
        UINT64 b = 0, e = 0;
        if (c->GetData(g_qBeg[k], &b, sizeof(b), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK) continue;
        if (c->GetData(g_qEnd[k], &e, sizeof(e), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK) continue;
        UINT64 occ = 0; D3D11_QUERY_DATA_PIPELINE_STATISTICS ps = {};
        bool hasOcc = g_qOcc[k] && c->GetData(g_qOcc[k], &occ, sizeof(occ), D3D11_ASYNC_GETDATA_DONOTFLUSH) == S_OK;
        bool hasSt = g_qStat[k] && c->GetData(g_qStat[k], &ps, sizeof(ps), D3D11_ASYNC_GETDATA_DONOTFLUSH) == S_OK;
        if ((g_qOcc[k] && !hasOcc) || (g_qStat[k] && !hasSt)) continue;
        g_qPend[k] = false;
        if (hasOcc && hasSt && g_qPts[k] > 20000)
        {
            double now = QpcMs();
            if (g_qSrc[k] == 4) { if (ps.CPrimitives > 2000 && occ == 0) g_faroHid++; else if (occ > 0) g_faroVis++; }   // voce 298
            if (ps.CPrimitives > 2000 && occ == 0 && !g_qNoDepth[k] && g_qSrc[k] != 4)
            {   // punti nella finestra ma nessun pixel oltre la profondita': per 400 ms al Present si disegna senza
                g_qDepthBlock++;
                if (g_qSrc[k] != 0) { if (now > g_depthOffUntil) FT("E;profondita_copre_tutto;fonte=%s;in_finestra=%llu", SrcName(g_qSrc[k]), ps.CPrimitives); g_depthOffUntil = now + 400; }
            }
            else if (ps.CPrimitives == 0)
            {   // nessun punto nella finestra: con una camera presa da un fotogramma interrotto, la si scarta per 1 s
                g_qCamOut++;
                if ((g_qSrc[k] == 0 || g_qSrc[k] == 4) && !g_noReSearch)   // voce 298: in pianta e sezione la nuvola si disegna al faro
                {   // voce 285: fotogramma normale, camera di Revit: la posizione della matrice e' sbagliata?
                    if (g_reSearchGen != g_mView) { g_reSearchGen = g_mView; g_reSearchN = 0; }
                    // voce 292: la posizione che ha gia' funzionato non si scarta mai. In sezione e in 3D la nuvola puo' essere
                    // davvero tutta fuori (piani di ritaglio, zoom su una zona senza punti): la 285 scartava la riga 15,
                    // quella giusta, e passava alla 11 (nuvola ridotta a una striscia, «come se ruotasse tutto»).
                    bool isGood = g_mSlot == g_goodSlot && g_mOff == g_goodOff && (g_mT & 1) == g_goodT && g_mOff2 == g_goodOff2 && g_mWidth == g_goodW;
                    if (++g_sigOut >= 3 && g_reSearchN < 4 && !isGood) { g_reSearchN++; g_reSearch = true; }
                }
                if (g_qSrc[k] == 2) { if (now > g_badCurUntil) FT("E;camera_interrotto_fuori_finestra"); g_badCurUntil = now + 1000; }
            }
            else
            {
                g_qVisible++;
                if (g_qSrc[k] == 0 || g_qSrc[k] == 4)
                {
                    g_sigOut = 0;
                    if (++g_sigVis >= 3 && g_mSlot >= 0)
                    {   // voce 288: posizione buona da riprovare per prima alla prossima vista
                        g_goodSlot = g_mSlot; g_goodOff = g_mOff; g_goodT = g_mT & 1; g_goodOff2 = g_mOff2; g_goodT2 = (g_mT >> 1) & 1; g_goodW = g_mWidth;
                    }
                }
            }
        }
        if (dj.Disjoint || dj.Frequency == 0 || e < b) continue;
        double ms = (double)(e - b) * 1000.0 / (double)dj.Frequency;
        g_gpuEma = g_gpuEma < 0 ? ms : g_gpuEma * 0.7 + ms * 0.3;
        if (hasOcc && hasSt) FT("G;gpu_ms=%.2f;fonte=%s;punti=%lld;in_finestra=%llu;pixel=%llu;senza_profondita=%d", ms, SrcName(g_qSrc[k]), g_qPts[k], ps.CPrimitives, occ, g_qNoDepth[k] ? 1 : 0);
        else FT("G;gpu_ms=%.2f", ms);
        g_gpuSum += ms; g_gpuN++; if (ms > g_gpuMax) g_gpuMax = ms;
    }
}
static int GpuBegin(ID3D11DeviceContext* c)
{
    GpuCollect(c);
    int k = g_qIdx % QN;
    if (g_qPend[k]) return -1;
    if (!g_qDisj[k] || !g_qBeg[k] || !g_qEnd[k])
    {
        ID3D11Device* dev = nullptr; c->GetDevice(&dev);
        if (!dev) return -1;
        D3D11_QUERY_DESC qd = {}; qd.Query = D3D11_QUERY_TIMESTAMP_DISJOINT;
        if (!g_qDisj[k]) dev->CreateQuery(&qd, &g_qDisj[k]);
        qd.Query = D3D11_QUERY_TIMESTAMP;
        if (!g_qBeg[k]) dev->CreateQuery(&qd, &g_qBeg[k]);
        if (!g_qEnd[k]) dev->CreateQuery(&qd, &g_qEnd[k]);
        qd.Query = D3D11_QUERY_OCCLUSION; if (!g_qOcc[k]) dev->CreateQuery(&qd, &g_qOcc[k]);
        qd.Query = D3D11_QUERY_PIPELINE_STATISTICS; if (!g_qStat[k]) dev->CreateQuery(&qd, &g_qStat[k]);
        dev->Release();
        if (!g_qDisj[k] || !g_qBeg[k] || !g_qEnd[k]) return -1;
    }
    c->Begin(g_qDisj[k]);
    c->End(g_qBeg[k]);
    if (g_qOcc[k]) c->Begin(g_qOcc[k]);
    if (g_qStat[k]) c->Begin(g_qStat[k]);
    g_qSrc[k] = g_drawSrc; g_qNoDepth[k] = g_noDepth; g_qPts[k] = 0;
    return k;
}
static void GpuEnd(ID3D11DeviceContext* c, int k)
{
    if (k < 0) return;
    if (g_qStat[k]) c->End(g_qStat[k]);
    if (g_qOcc[k]) c->End(g_qOcc[k]);
    c->End(g_qEnd[k]);
    c->End(g_qDisj[k]);
    g_qPend[k] = true;
    g_qIdx++;
}
extern "C" __declspec(dllexport) float PdShaderGpuMs() { return (float)g_gpuEma; }
static int g_clipOn = 0;
static float g_clip[24] = {};
extern "C" __declspec(dllexport) void PdSetClip(int on, const float* planes)
{
    g_clipOn = on ? 1 : 0;
    if (on) memcpy(g_clip, planes, sizeof(g_clip));
}
// Stato di Revit salvato e rimesso attorno al nostro disegno
struct SavedState
{
    ID3D11InputLayout* il = nullptr; ID3D11Buffer* ib = nullptr; DXGI_FORMAT ifmt = DXGI_FORMAT_UNKNOWN; UINT io = 0;
    D3D11_PRIMITIVE_TOPOLOGY topo = D3D11_PRIMITIVE_TOPOLOGY_UNDEFINED;
    ID3D11VertexShader* vs = nullptr; ID3D11PixelShader* ps = nullptr; ID3D11GeometryShader* gs = nullptr;
    ID3D11Buffer* vcbs[2] = {}; ID3D11ShaderResourceView* vsrv = nullptr;
    ID3D11DepthStencilState* dss = nullptr; UINT sref = 0; ID3D11RasterizerState* rs = nullptr;
    ID3D11BlendState* bs = nullptr; FLOAT bf[4] = {}; UINT sm = 0;
    bool target = false; ID3D11RenderTargetView* rtv = nullptr; ID3D11DepthStencilView* dsv = nullptr;
    D3D11_VIEWPORT vps[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE]; UINT nvp = 0;
    void Save(ID3D11DeviceContext* c, bool withTarget)
    {
        c->IAGetInputLayout(&il); c->IAGetIndexBuffer(&ib, &ifmt, &io); c->IAGetPrimitiveTopology(&topo);
        c->VSGetShader(&vs, nullptr, nullptr); c->PSGetShader(&ps, nullptr, nullptr); c->GSGetShader(&gs, nullptr, nullptr);
        c->VSGetConstantBuffers(0, 2, vcbs); c->VSGetShaderResources(0, 1, &vsrv);
        c->OMGetDepthStencilState(&dss, &sref); c->RSGetState(&rs); c->OMGetBlendState(&bs, bf, &sm);
        target = withTarget;
        if (withTarget) { c->OMGetRenderTargets(1, &rtv, &dsv); nvp = D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE; c->RSGetViewports(&nvp, vps); }
    }
    void Restore(ID3D11DeviceContext* c)
    {
        c->IASetInputLayout(il); c->IASetIndexBuffer(ib, ifmt, io); c->IASetPrimitiveTopology(topo);
        c->VSSetShader(vs, nullptr, 0); c->PSSetShader(ps, nullptr, 0); c->GSSetShader(gs, nullptr, 0);
        c->VSSetConstantBuffers(0, 2, vcbs); c->VSSetShaderResources(0, 1, &vsrv);
        c->OMSetDepthStencilState(dss, sref); c->RSSetState(rs); c->OMSetBlendState(bs, bf, sm);
        if (target) { c->OMSetRenderTargets(1, &rtv, dsv); if (nvp > 0) c->RSSetViewports(nvp, vps); }
        if (il) il->Release(); if (ib) ib->Release();
        if (vs) vs->Release(); if (ps) ps->Release(); if (gs) gs->Release();
        if (vcbs[0]) vcbs[0]->Release(); if (vcbs[1]) vcbs[1]->Release(); if (vsrv) vsrv->Release();
        if (dss) dss->Release(); if (rs) rs->Release(); if (bs) bs->Release();
        if (rtv) rtv->Release(); if (dsv) dsv->Release();
    }
};

// voce 250: spostamento della nuvola verso chi guarda, in profondita' normalizzata (0 = nessuno). Si usa solo con la
// camera ricavata dal faro, la cui profondita' e' approssimata di qualche centimetro.
static float g_depthBias = 0;
static int g_inOurDraw = 0;
static bool g_curWin = false;   // voce 270: la destinazione attuale e' l'immagine della finestra (aggiornato a ogni cambio)   // voce 269: dentro un nostro disegno (DrawPointsCore): i disegni che passano dagli agganci sono nostri
static ID3D11DepthStencilView* g_winModelDsv = nullptr;
static bool g_uiArm = false, g_uiDone = false, g_uiFull = false, g_depthNoWrite = false;
static ID3D11RenderTargetView* g_uiRtv = nullptr;
static long g_uiDraws = 0, g_presentDraws = 0;
// voce 259: punti con la propria opacita' (byte alto del colore; analisi con i link «trasparenti»). sel: 0 = tutti,
// 1 = solo quelli pieni, 2 = solo quelli trasparenti (sopra il modello finito, al Present, con la profondita' copiata e
// senza scriverla). Gli altri punti finiscono fuori dalla finestra nel vertex shader.
static bool g_ptAlpha = false;
static long g_ptAlphaDraws = 0;
extern "C" __declspec(dllexport) void PdSetPointAlpha(int on) { g_ptAlpha = on != 0; }
// Disegno dei punti in g_ready con la matrice in matBuf (righe off..off+3, e off2..off2+3 se c'e' la seconda).
static void DrawPointsCore(ID3D11DeviceContext* c, ID3D11Buffer* matBuf, int off, int t, int off2, int t2, const D3D11_VIEWPORT& vp, int sel = 0)
{
    struct In { In() { g_inOurDraw++; } ~In() { g_inOurDraw--; } } inOur;
    int qk = GpuBegin(c);
    c->IASetInputLayout(nullptr);
    c->IASetIndexBuffer(g_quadIb, DXGI_FORMAT_R32_UINT, 0);
    c->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    c->VSSetShader(g_svs, nullptr, 0);
    c->PSSetShader(g_sps, nullptr, 0);
    c->GSSetShader(nullptr, nullptr, 0);
    ID3D11Buffer* ours[2] = { g_scb, matBuf };
    c->VSSetConstantBuffers(0, 2, ours);
    bool tr = Transparent();
    bool blend = tr || sel == 2;
    if (sel == 2) g_ptAlphaDraws++;
    if (g_faroDss) { c->OMSetDepthStencilState(g_faroDss, g_faroDssRef); g_faroDssUsed++; }   // voce 298: come le nuvole di Revit
    else c->OMSetDepthStencilState(g_noDepth && g_sdssOff ? g_sdssOff : (blend || g_depthNoWrite ? g_sdssNoWrite : g_sdss), 0);
    c->RSSetState(g_useScissor && g_srsSc ? g_srsSc : g_srs);
    FLOAT zero[4] = { 0, 0, 0, 0 };
    c->OMSetBlendState(blend ? g_bsAlpha : g_bs, zero, 0xffffffff);
    float alpha = tr ? g_alpha : 1.0f;
    // voce 200: piani di ritaglio dal plugin (riquadro di sezione, intervallo di vista, campo della sezione letti
    // dalle API di Revit), non piu' dal buffer di Revit (voce 196: li' la loro posizione cambia da vista a vista)
    int planes = g_clipOn;
    float lastSp = -1, lastGr = -1;
    for (const PdItem& it : g_ready)
    {
        if (!it.srv || it.count <= 0) continue;
        if (it.spacing != lastSp || it.grow != lastGr)
        {
            D3D11_MAPPED_SUBRESOURCE m;
            if (SUCCEEDED(c->Map(g_scb, 0, D3D11_MAP_WRITE_DISCARD, 0, &m)))
            {
                int* in = (int*)m.pData; float* f = (float*)m.pData;
                in[0] = 1; in[1] = off; in[2] = t; in[3] = planes;
                f[4] = it.spacing * g_shScale; f[5] = vp.Width; f[6] = vp.Height; f[7] = g_shMaxPx;
                memcpy(f + 8, g_clip, sizeof(g_clip));
                // voce 205: seconda matrice (vista e proiezione separate nel buffer di Revit), come nel cubo di prova
                in[32] = off2; in[33] = t2; f[34] = alpha; f[35] = it.grow;
                f[36] = g_depthBias; f[37] = (float)sel; f[38] = f[39] = 0;   // voce 250, 259
                c->Unmap(g_scb, 0);
            }
            lastSp = it.spacing; lastGr = it.grow;
        }
        c->VSSetShaderResources(0, 1, &it.srv);
        for (int st0 = 0; st0 < it.count; st0 += QuadMax)
        {
            int nq = it.count - st0 < QuadMax ? it.count - st0 : QuadMax;
            c->DrawIndexed((UINT)nq * 6, 0, st0 * 4);   // l'indice di vertice parte da st0*4: punto st0
        }
        g_shDrawn++; g_shPoints += it.count;
        if (qk >= 0) g_qPts[qk] += it.count;
    }
    GpuEnd(c, qk);
}

// =====================================================================================================================
// Voce 209: fotogrammi interrotti da Revit. Con «Allow navigation during redraw» (attiva di serie) Revit, mentre ci si
// muove, mostra fotogrammi in cui non ha finito di disegnare e non ha chiamato il plugin: la nuvola sparisce (video e
// registro del 20/11; spegnendo l'opzione non sparisce piu'). In quei fotogrammi la nuvola si disegna noi, subito prima
// che Revit presenti l'immagine (Present), nella stessa destinazione e con lo stesso buffer di profondita' dell'ultimo
// fotogramma normale. La camera di quel fotogramma si ricava dal buffer di Revit: a ogni scrittura (Map/Unmap) del
// buffer che contiene la matrice si legge la matrice scritta; quella usata da piu' disegni del fotogramma e' la
// trasformazione del modello. Si accetta solo se ha la stessa forma della matrice dell'ultimo fotogramma normale.
// =====================================================================================================================
static ID3D11Resource* g_capBuf = nullptr;      // buffer di Revit con la matrice (solo per confronto, nessun riferimento)
static void* g_capPtr = nullptr;
static long g_capWrites = 0;
static float g_lastCap[32]; static bool g_hasLastCap = false;
static float g_refRaw[32]; static bool g_hasRef = false;
static ID3D11DepthStencilView* g_fbDsv = nullptr;   // con riferimento: va usata nei fotogrammi interrotti
static D3D11_VIEWPORT g_fbVp = {};
// voce 227: copia della profondita' del modello presa quando Revit smette di usarla dopo il segnale (vedi HookROMSet)
static bool g_dzArm = false, g_keepThisFrame = false;
static void KeepDepthFrom(ID3D11DeviceContext* c);
// voce 229: fotogrammi parziali. Passando col mouse o selezionando, Revit ridisegna solo un rettangolo attorno
// all'elemento (video del 20/11 14:13): fuori dal rettangolo la profondita' della scena e' vuota e l'immagine e' quella
// di prima, ma la nuvola si disegnava su tutta la finestra, davanti ai muri. Il rettangolo si legge dalle forbici attive
// al segnale o dai rettangoli passati a Present1; la nuvola si disegna solo li'.
static bool g_partial = false; static D3D11_RECT g_partRect = {};
static bool g_scRect = false, g_noCropFix = false; static long g_cropFrames = 0;   // voce 287
extern "C" __declspec(dllexport) void PdSetCropFix(int on) { g_noCropFix = on == 0; }
static long g_partMarker = 0, g_partDirty = 0, g_partScPresent = 0, g_partKeepSkip = 0; static D3D11_RECT g_partLast = {};
static float g_keepCam[32]; static bool g_keepCamOk = false;
static bool g_keptOld = false;   // voce 230: in questo fotogramma parziale si usa la profondita' intera di prima
static bool PartialRect(D3D11_RECT r, const D3D11_VIEWPORT& vp, D3D11_RECT& out)
{
    LONG x0 = (LONG)vp.TopLeftX, y0 = (LONG)vp.TopLeftY, x1 = x0 + (LONG)vp.Width, y1 = y0 + (LONG)vp.Height;
    if (r.left < x0) r.left = x0; if (r.top < y0) r.top = y0; if (r.right > x1) r.right = x1; if (r.bottom > y1) r.bottom = y1;
    if (r.right <= r.left || r.bottom <= r.top) return false;
    if (r.right - r.left >= x1 - x0 - 1 && r.bottom - r.top >= y1 - y0 - 1) return false;   // tutta la finestra
    out = r; return true;
}
static bool KeepStillGood();
static ID3D11DepthStencilView* KeepDsvForPartial();   // voce 289
static bool g_noPartialKeep = false; static long g_partialKeepDraws = 0;
extern "C" __declspec(dllexport) void PdSetPartialKeepDepth(int on) { g_noPartialKeep = on == 0; }

// =====================================================================================================================
// Voce 224: diagnostica per la trasparenza nella VistaIniziale (nuvola davanti ai muri solo li'). Ogni 5 s si registra
// la sequenza di un fotogramma (come nella 220): destinazioni impostate T(r#colore d#profondita'), pulizie C/Z, copie K,
// risoluzioni R, disegni dN, SEGNALE, PRESENT. Con la trasparenza accesa, al segnale e al Present si legge anche il
// buffer di profondita' (copia in memoria, una volta ogni 5 s) e si scrive quanta parte della finestra e' coperta da
// qualcosa (profondita' sotto il valore di sfondo) e il valore medio: si vede se al Present il modello c'e' ancora.
// =====================================================================================================================
static bool g_trArm = false, g_trRec = false, g_trDone = false;
static int g_trSrvSig = -1;   // voce 263: ultima combinazione di texture lette (vedi TrSrv)
// voce 260: sequenze di immagini consecutive nel diario (righe X;sequenza), partono alla prima immagine col faro di una
// vista e poi al massimo ogni 15 s: si vede su quale buffer di profondita' Revit disegna il faro, se dopo il faro
// disegna ancora il modello su quel buffer, quando lo lascia e cosa c'e' nella nostra copia.
static int g_seqLeft = 0; static bool g_seqOn = false; static double g_seqAt = -1e9; static long g_seqGen = -1;
static char g_trBuf[3900] = ""; static int g_trLen = 0; static long g_trDraws = 0;
static void* g_trRes[32] = {}; static int g_trN = 0;
static void TrPut(const char* f, ...)
{
    if (!g_trRec || g_trLen >= (int)sizeof(g_trBuf) - 80) return;
    va_list a; va_start(a, f);
    int k = _vsnprintf_s(g_trBuf + g_trLen, sizeof(g_trBuf) - g_trLen, _TRUNCATE, f, a);
    va_end(a);
    if (k > 0) g_trLen += k; else g_trLen = (int)strlen(g_trBuf);
}
static void TrFlush() { if (g_trRec && g_trDraws > 0) { TrPut(" d%ld", g_trDraws); g_trDraws = 0; } }
static int TrId(ID3D11Resource* r)
{
    if (!r) return 0;
    for (int i = 0; i < g_trN; i++) if (g_trRes[i] == r) return i + 1;
    if (g_trN >= 32) return 99;
    g_trRes[g_trN++] = r;
    D3D11_RESOURCE_DIMENSION dim; r->GetType(&dim);
    if (dim == D3D11_RESOURCE_DIMENSION_TEXTURE2D)
    {
        D3D11_TEXTURE2D_DESC d = {}; ((ID3D11Texture2D*)r)->GetDesc(&d);
        TrPut(" [#%d=%ux%u f%d c%u]", g_trN, d.Width, d.Height, (int)d.Format, d.SampleDesc.Count);
    }
    return g_trN;
}
static int TrView(ID3D11View* v)
{
    if (!v) return 0;
    ID3D11Resource* r = nullptr; v->GetResource(&r);
    int id = TrId(r);
    if (r) r->Release();
    return id;
}
// copertura del buffer di profondita' (solo formati a 24 bit + stencil o float a 32 bit, un campione)
static void TrDepth(ID3D11DeviceContext* c, ID3D11DepthStencilView* dsv, const char* what)
{
    if (!g_trRec || !dsv || !g_realDev) return;
    ID3D11Resource* r = nullptr; dsv->GetResource(&r);
    if (!r) return;
    D3D11_TEXTURE2D_DESC d = {}; ((ID3D11Texture2D*)r)->GetDesc(&d);
    int id = TrId(r);
    bool d24 = d.Format == DXGI_FORMAT_R24G8_TYPELESS || d.Format == DXGI_FORMAT_D24_UNORM_S8_UINT;
    bool f32 = d.Format == DXGI_FORMAT_R32_TYPELESS || d.Format == DXGI_FORMAT_D32_FLOAT;
    if ((!d24 && !f32) || d.SampleDesc.Count != 1) { TrPut(" %s(#%d formato non letto)", what, id); r->Release(); return; }
    D3D11_TEXTURE2D_DESC sd = d; sd.Usage = D3D11_USAGE_STAGING; sd.BindFlags = 0; sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ; sd.MiscFlags = 0;
    sd.MipLevels = 1; sd.ArraySize = 1;
    ID3D11Texture2D* st = nullptr;
    if (FAILED(g_realDev->CreateTexture2D(&sd, nullptr, &st)) || !st) { r->Release(); return; }
    c->CopyResource(st, r);
    D3D11_MAPPED_SUBRESOURCE m;
    if (SUCCEEDED(c->Map(st, 0, D3D11_MAP_READ, 0, &m)))
    {
        long n = 0, cov = 0; double sum = 0, mn = 1e9, mx = -1e9;
        for (UINT y = 0; y < d.Height; y += 8)
        {
            const unsigned char* row = (const unsigned char*)m.pData + (size_t)y * m.RowPitch;
            for (UINT x = 0; x < d.Width; x += 8)
            {
                double z = d24 ? (double)(((const unsigned int*)row)[x] & 0xFFFFFF) / 16777215.0 : (double)((const float*)row)[x];
                n++;
                if (z < 0.99999 && z > 0.00001) { cov++; sum += z; if (z < mn) mn = z; if (z > mx) mx = z; }
            }
        }
        if (cov > 0) TrPut(" %s(#%d coperto %.0f%%, z %.4f-%.4f media %.4f)", what, id, 100.0 * cov / n, mn, mx, sum / cov);
        else TrPut(" %s(#%d vuoto)", what, id);
        c->Unmap(st, 0);
    }
    st->Release(); r->Release();
}
// voce 262: contenuto di un buffer di profondita' (copertura, z minima, massima e media) fuori dalle sequenze
static bool DepthRead(ID3D11DeviceContext* c, ID3D11DepthStencilView* dsv, double& cov, double& mn, double& mx, double& mean)
{
    cov = 0; mn = 1; mx = 0; mean = 0;
    if (!dsv || !g_realDev) return false;
    ID3D11Resource* r = nullptr; dsv->GetResource(&r);
    if (!r) return false;
    D3D11_TEXTURE2D_DESC d = {}; ((ID3D11Texture2D*)r)->GetDesc(&d);
    bool d24 = d.Format == DXGI_FORMAT_R24G8_TYPELESS || d.Format == DXGI_FORMAT_D24_UNORM_S8_UINT;
    bool f32 = d.Format == DXGI_FORMAT_R32_TYPELESS || d.Format == DXGI_FORMAT_D32_FLOAT;
    if ((!d24 && !f32) || d.SampleDesc.Count != 1) { r->Release(); return false; }
    D3D11_TEXTURE2D_DESC sd = d; sd.Usage = D3D11_USAGE_STAGING; sd.BindFlags = 0; sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ; sd.MiscFlags = 0;
    sd.MipLevels = 1; sd.ArraySize = 1;
    ID3D11Texture2D* st = nullptr;
    if (FAILED(g_realDev->CreateTexture2D(&sd, nullptr, &st)) || !st) { r->Release(); return false; }
    c->CopyResource(st, r);
    D3D11_MAPPED_SUBRESOURCE m; bool ok = false;
    if (SUCCEEDED(c->Map(st, 0, D3D11_MAP_READ, 0, &m)))
    {
        long n = 0, k = 0; double sum = 0;
        for (UINT y = 0; y < d.Height; y += 8)
        {
            const unsigned char* row = (const unsigned char*)m.pData + (size_t)y * m.RowPitch;
            for (UINT x = 0; x < d.Width; x += 8)
            {
                double z = d24 ? (double)(((const unsigned int*)row)[x] & 0xFFFFFF) / 16777215.0 : (double)((const float*)row)[x];
                n++;
                if (z < 0.99999 && z > 0.00001) { k++; sum += z; if (z < mn) mn = z; if (z > mx) mx = z; }
            }
        }
        cov = n > 0 ? 100.0 * k / n : 0; mean = k > 0 ? sum / k : 0; ok = true;
        c->Unmap(st, 0);
    }
    st->Release(); r->Release();
    return ok;
}
// voce 271: valori di profondita' di un buffer, un punto ogni 8 pixel (0 = vuoto)
static bool DepthGrid(ID3D11DeviceContext* c, ID3D11DepthStencilView* dsv, std::vector<float>& z)
{
    z.clear();
    if (!dsv || !g_realDev) return false;
    ID3D11Resource* r = nullptr; dsv->GetResource(&r);
    if (!r) return false;
    D3D11_TEXTURE2D_DESC d = {}; ((ID3D11Texture2D*)r)->GetDesc(&d);
    bool d24 = d.Format == DXGI_FORMAT_R24G8_TYPELESS || d.Format == DXGI_FORMAT_D24_UNORM_S8_UINT;
    bool f32 = d.Format == DXGI_FORMAT_R32_TYPELESS || d.Format == DXGI_FORMAT_D32_FLOAT;
    if ((!d24 && !f32) || d.SampleDesc.Count != 1) { r->Release(); return false; }
    D3D11_TEXTURE2D_DESC sd = d; sd.Usage = D3D11_USAGE_STAGING; sd.BindFlags = 0; sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ; sd.MiscFlags = 0;
    sd.MipLevels = 1; sd.ArraySize = 1;
    ID3D11Texture2D* st = nullptr;
    if (FAILED(g_realDev->CreateTexture2D(&sd, nullptr, &st)) || !st) { r->Release(); return false; }
    c->CopyResource(st, r);
    D3D11_MAPPED_SUBRESOURCE m; bool ok = false;
    if (SUCCEEDED(c->Map(st, 0, D3D11_MAP_READ, 0, &m)))
    {
        for (UINT y = 0; y < d.Height; y += 8)
        {
            const unsigned char* row = (const unsigned char*)m.pData + (size_t)y * m.RowPitch;
            for (UINT x = 0; x < d.Width; x += 8)
            {
                double v = d24 ? (double)(((const unsigned int*)row)[x] & 0xFFFFFF) / 16777215.0 : (double)((const float*)row)[x];
                z.push_back(v < 0.99999 && v > 0.00001 ? (float)v : 0.0f);
            }
        }
        ok = true;
        c->Unmap(st, 0);
    }
    st->Release(); r->Release();
    return ok;
}
// voce 271: confronto tra la copia della scena (a) e l'altro buffer di profondita' della scena (b), per il diario
static void DepthPairLog(ID3D11DeviceContext* c, ID3D11DepthStencilView* a, ID3D11DepthStencilView* b, const char* tag)
{
    std::vector<float> za, zb;
    if (!DepthGrid(c, a, za) || !DepthGrid(c, b, zb) || za.size() != zb.size() || za.empty()) { FT("X;%s;confronto_non_fatto", tag); return; }
    long n = (long)za.size(), ka = 0, kb = 0, ku = 0, kk = 0, bNear = 0, aNear = 0; double diff = 0, mn = 1, mx = 0;
    for (long i = 0; i < n; i++)
    {
        bool va = za[i] > 0, vb = zb[i] > 0;
        if (va) ka++; if (vb) { kb++; if (zb[i] < mn) mn = zb[i]; if (zb[i] > mx) mx = zb[i]; }
        if (va || vb) ku++;
        if (va && vb) { kk++; diff += fabs((double)za[i] - zb[i]); if (zb[i] < za[i] - 0.0005f) bNear++; else if (za[i] < zb[i] - 0.0005f) aNear++; }
    }
    FT("X;%s;scena=%.0f;altra=%.0f;unione=%.0f;entrambe=%.0f;scarto_medio=%.5f;altra_piu_vicina=%.0f;scena_piu_vicina=%.0f;altra_z=%.4f-%.4f",
       tag, 100.0 * ka / n, 100.0 * kb / n, 100.0 * ku / n, 100.0 * kk / n, kk ? diff / kk : 0.0, kk ? 100.0 * bNear / kk : 0.0, kk ? 100.0 * aNear / kk : 0.0, kb ? mn : 0.0, kb ? mx : 0.0);
}
static void ColFormD(const float* raw, double* C);
static double g_cloudT[8][16]; static int g_nCloudT = 0;
// profondita' (0..1) del centro della nuvola con la camera cam (formato del buffer di Revit), -1 se non si sa
static double CloudCenterZ(const float* cam, bool whole)
{
    if (g_nCloudT <= 0) return -1;
    double C[16]; ColFormD(cam, C);
    double x = g_cloudT[0][3], y = g_cloudT[0][7], z = g_cloudT[0][11];
    double cz = C[8] * x + C[9] * y + C[10] * z + C[11], cw = C[12] * x + C[13] * y + C[14] * z + C[15];
    (void)whole;
    return cw != 0 ? cz / cw : -1;
}
static void TrPresent(IDXGISwapChain* s, bool full, ID3D11DepthStencilView* fbDsv)
{
    if (g_trRec)
    {
        TrFlush();
        if (full && fbDsv)
        {
            ID3D11DeviceContext* c = nullptr; g_realDev->GetImmediateContext(&c);
            if (c) { TrDepth(c, fbDsv, "profondita' usata al Present"); c->Release(); }
        }
        ID3D11Texture2D* bb = nullptr; int id = 0;
        if (SUCCEEDED(s->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&bb)) && bb) { id = TrId(bb); bb->Release(); }
        TrPut(" PRESENT(#%d, %s)", id, full ? "completo" : "interrotto");
        g_trRec = false; g_trDone = true;
        if (g_seqOn)
        {   // voce 260: nel diario, e si continua con l'immagine successiva
            FT("X;sequenza;%s", g_trBuf);
            g_trDone = false;
            if (--g_seqLeft > 0) { g_trRec = true; g_trLen = 0; g_trBuf[0] = 0; g_trN = 0; g_trDraws = 0; g_trSrvSig = -1; }
            else g_seqOn = false;
        }
    }
    else if (g_trArm && !g_trDone) { g_trArm = false; g_trRec = true; g_trLen = 0; g_trBuf[0] = 0; g_trN = 0; g_trDraws = 0; g_trSrvSig = -1; }
}
static UINT g_fbW = 0, g_fbH = 0;
static bool g_drawnThisFrame = false;
static ID3D11Buffer* g_fbMat = nullptr;
static long g_fbNormal = 0, g_fbDrawn = 0, g_fbNoMat = 0, g_fbOther = 0, g_fbAgree = 0, g_fbCompared = 0, g_fbRejected = 0;
static int MatFloats() { return g_mOff2 >= 0 ? 32 : 16; }

static void CaptureWrite(const void* data)
{
    if (g_mSlot < 0) return;
    const float* f = (const float*)data;
    memcpy(g_lastCap, f + g_mOff * 4, 64);
    if (g_mOff2 >= 0) memcpy(g_lastCap + 16, f + g_mOff2 * 4, 64);
    g_hasLastCap = true;
    g_capWrites++;
}

// ---- voce 211: dove tiene Revit la camera del modello? In alcuni fotogrammi completi si copiano le prime centinaia di
// byte dei buffer di costanti che Revit scrive, e a fine fotogramma vi si cerca la matrice vera di quel fotogramma
// (la parte di rotazione e scala, e la matrice intera). Se c'e', nei fotogrammi interrotti la camera si puo' leggere da
// li' invece di usare quella vecchia (la nuvola ora resta indietro rispetto al modello). Solo diagnostica nel registro.
struct LearnBuf { UINT width; float data[128]; };
static std::vector<LearnBuf> g_learn;
static int g_learnFrames = 4;
static char g_learnInfo[700] = "";
static ID3D11Resource* g_lmRes[64] = {}; static void* g_lmPtr[64] = {}; static UINT g_lmW[64] = {}; static int g_lmN = 0;
static void LearnMap(ID3D11Resource* r, void* p)
{
    if (g_learnFrames <= 0 || !r || !p || g_learn.size() >= 4000) return;
    D3D11_RESOURCE_DIMENSION dim; r->GetType(&dim);
    if (dim != D3D11_RESOURCE_DIMENSION_BUFFER) return;
    D3D11_BUFFER_DESC d; ((ID3D11Buffer*)r)->GetDesc(&d);
    if (!(d.BindFlags & D3D11_BIND_CONSTANT_BUFFER) || d.ByteWidth < 64 || d.ByteWidth > 8192) return;
    int k = g_lmN < 64 ? g_lmN++ : 63;
    g_lmRes[k] = r; g_lmPtr[k] = p; g_lmW[k] = d.ByteWidth;
}
static void LearnUnmap(ID3D11Resource* r)
{
    if (g_learnFrames <= 0) return;
    for (int k = g_lmN - 1; k >= 0; k--)
        if (g_lmRes[k] == r)
        {
            LearnBuf b; b.width = g_lmW[k];
            UINT n = b.width < 512 ? b.width : 512;
            memset(b.data, 0, sizeof(b.data)); memcpy(b.data, g_lmPtr[k], n);
            g_learn.push_back(b);
            g_lmRes[k] = g_lmRes[g_lmN - 1]; g_lmPtr[k] = g_lmPtr[g_lmN - 1]; g_lmW[k] = g_lmW[g_lmN - 1]; g_lmN--;
            return;
        }
}
static bool Near(float a, float b) { return fabsf(a - b) <= 1e-4f * (fabsf(a) + fabsf(b)) + 1e-9f; }
// a fine fotogramma completo: ricerca della matrice vera nei buffer copiati
static void LearnSearch()
{
    if (g_learnFrames <= 0 || !g_hasRef) { g_learn.clear(); g_lmN = 0; return; }
    const float* M = g_refRaw;
    long withRot = 0, whole = 0, rotT = 0;
    char where[300] = ""; int wl = 0; int listed = 0;
    for (const LearnBuf& b : g_learn)
    {
        int nf = (int)(b.width < 512 ? b.width : 512) / 4;
        for (int o = 0; o + 16 <= nf; o += 4)
        {
            const float* f = b.data + o;
            bool rot = true, rotTr = true;
            for (int r = 0; r < 3 && (rot || rotTr); r++)
                for (int k = 0; k < 3; k++)
                {
                    if (!Near(f[r * 4 + k], M[r * 4 + k])) rot = false;
                    if (!Near(f[k * 4 + r], M[r * 4 + k])) rotTr = false;
                }
            if (!rot && !rotTr) continue;
            if (rot) withRot++; else rotT++;
            bool all = true;
            for (int q = 0; q < 16; q++) if (!Near(f[q], M[q])) { all = false; break; }
            if (all) whole++;
            if (listed < 6)
            {
                wl += _snprintf_s(where + wl, sizeof(where) - wl, _TRUNCATE, "%s%u byte riga %d%s%s", listed ? ", " : "", b.width, o / 4, rot ? "" : " trasposta", all ? " intera" : " (traslazione diversa)");
                listed++;
            }
        }
    }
    _snprintf_s(g_learnInfo, sizeof(g_learnInfo), _TRUNCATE, "camera del modello: %zu scritture di buffer esaminate, rotazione trovata %ld volte (trasposta %ld), matrice intera uguale %ld volte%s%s",
        g_learn.size(), withRot, rotT, whole, wl ? "; dove: " : "", where);
    g_learn.clear(); g_lmN = 0;
    g_learnFrames--;
}

// ---- voce 212: camera dei fotogrammi interrotti. La ricerca della 211 (registro 20/11 10:12) ha trovato la matrice vera
// in 1.462 scritture di buffer di Revit per fotogramma, tutte con la stessa forma del nostro (stessa dimensione, stessa
// riga). A ogni scrittura di un buffer di costanti di quella dimensione si legge la matrice in quella riga; a fine
// fotogramma la piu' scritta e' la camera del modello di quel fotogramma, anche se Revit lo interrompe prima di noi.
struct MatCand { float m[32]; int n; };
static MatCand g_cand[16]; static int g_nCand = 0;
static ID3D11Resource* g_cmRes[32] = {}; static void* g_cmPtr[32] = {}; static int g_cmN = 0;
static long g_camUsed = 0, g_camStale = 0;
static void CamMap(ID3D11Resource* r, void* p)
{
    if (g_mSlot < 0 || g_mWidth == 0 || !r || !p) return;
    D3D11_RESOURCE_DIMENSION dim; r->GetType(&dim);
    if (dim != D3D11_RESOURCE_DIMENSION_BUFFER) return;
    D3D11_BUFFER_DESC d; ((ID3D11Buffer*)r)->GetDesc(&d);
    if (d.ByteWidth != g_mWidth || !(d.BindFlags & D3D11_BIND_CONSTANT_BUFFER)) return;
    int k = g_cmN < 32 ? g_cmN++ : 31;
    g_cmRes[k] = r; g_cmPtr[k] = p;
}
static void CamUnmap(ID3D11Resource* r)
{
    for (int k = g_cmN - 1; k >= 0; k--)
        if (g_cmRes[k] == r)
        {
            float m[32] = {};
            const float* f = (const float*)g_cmPtr[k];
            memcpy(m, f + g_mOff * 4, 64);
            if (g_mOff2 >= 0) memcpy(m + 16, f + g_mOff2 * 4, 64);
            g_cmRes[k] = g_cmRes[g_cmN - 1]; g_cmPtr[k] = g_cmPtr[g_cmN - 1]; g_cmN--;
            int nf = g_mOff2 >= 0 ? 32 : 16;
            for (int i = 0; i < g_nCand; i++) if (memcmp(g_cand[i].m, m, nf * 4) == 0) { g_cand[i].n++; return; }
            int slot = g_nCand;
            if (slot >= 16) { slot = 0; for (int i = 1; i < 16; i++) if (g_cand[i].n < g_cand[slot].n) slot = i; if (g_cand[slot].n > 1) return; }
            else g_nCand++;
            memcpy(g_cand[slot].m, m, sizeof(m)); g_cand[slot].n = 1;
            return;
        }
}
static int BestCand() { int b = -1; for (int i = 0; i < g_nCand; i++) if (b < 0 || g_cand[i].n > g_cand[b].n) b = i; return b; }

// voce 213: nei fotogrammi interrotti la matrice piu' scritta a volte non e' la camera della vista intera ma quella di un
// riquadro (Revit disegna a tasselli: la matrice ingrandisce un pezzo della vista a tutto schermo) e la nuvola compariva
// ingrandita in basso a sinistra (video del 20/11 10:18). Si accetta solo una matrice con la stessa forma e quasi la
// stessa scala di quella vera dell'ultimo fotogramma completo: proiezione dello stesso tipo, stesse proporzioni, assi
// perpendicolari, scala tra 0,75 e 1,33 volte.
static long g_camRejected = 0;
// voce 215: il confronto si fa con l'ultima camera usata (fotogramma completo o interrotto accettato), non con quella
// dell'ultimo fotogramma completo: con il modello impianti i completi sono rari (6 su 104) e durante zoom e orbite la
// camera vera si allontana presto da quella vecchia, quindi la 214 scartava anche la camera giusta (scartate 110).
static float g_chainRaw[32]; static long g_chainGen = -1;
static bool g_chainFromBeacon = false;   // voce 249: la camera precedente viene dal faro (profondita' non affidabile)
static IDXGISwapChain* g_fbSwap = nullptr;   // finestra della vista (solo confronto, senza riferimento)
static void ColForm(const float* raw, float* C)
{
    float A[16], B[16];
    for (int r = 0; r < 4; r++) for (int k = 0; k < 4; k++) A[r * 4 + k] = (g_mT & 1) ? raw[r * 4 + k] : raw[k * 4 + r];
    if (g_mOff2 < 0) { memcpy(C, A, 64); return; }
    for (int r = 0; r < 4; r++) for (int k = 0; k < 4; k++) B[r * 4 + k] = ((g_mT >> 1) & 1) ? raw[16 + r * 4 + k] : raw[16 + k * 4 + r];
    for (int r = 0; r < 4; r++) for (int k = 0; k < 4; k++) { float v = 0; for (int j = 0; j < 4; j++) v += B[r * 4 + j] * A[j * 4 + k]; C[r * 4 + k] = v; }
}
static float L3(const float* v) { return sqrtf(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]); }
// voce 237: motivi per cui le matrici dei fotogrammi interrotti vengono scartate (diario: riga P;interrotto)
static int g_rjProj, g_rjAsp, g_rjOrtho, g_rjScale, g_rjFocus, g_rjOther;
static float g_rjKmin, g_rjKmax, g_rjShift;
static bool LikeRef(const float* raw, bool focus = true)
{
    float C[16], R[16]; ColForm(raw, C); ColForm(g_chainGen == g_viewGen ? g_chainRaw : g_refRaw, R);
    float c0 = L3(C), c1 = L3(C + 4), r0 = L3(R), r1 = L3(R + 4);
    if (!(c0 > 0 && c1 > 0 && r0 > 0 && r1 > 0)) { g_rjOther++; return false; }
    bool cPar = L3(C + 12) < 1e-6f * c0, rPar = L3(R + 12) < 1e-6f * r0;
    if (cPar != rPar) { g_rjProj++; return false; }
    if (fabsf((c0 / c1) / (r0 / r1) - 1) > 0.02f) { g_rjAsp++; return false; }
    if (fabsf(C[0] * C[4] + C[1] * C[5] + C[2] * C[6]) > 0.02f * c0 * c1) { g_rjOrtho++; return false; }
    float k = c0 / r0;
    if (k < g_rjKmin) g_rjKmin = k; if (k > g_rjKmax) g_rjKmax = k;
    if (!(k > 0.75f && k < 1.33f)) { g_rjScale++; return false; }
    // voce 214: stessa scala ma spostata (tassello disegnato con la vista traslata: nuvola in basso a sinistra). Il punto
    // guardato a schermo non puo' saltare di oltre un quarto della finestra da un fotogramma all'altro (voce 216: prima il
    // centro della nuvola, che in prospettiva poteva stare dietro la camera).
    if (!focus) return true;   // voce 245: solo la forma (camera riconosciuta per altra via)
    float pc[4], pr[4], q[4] = { g_focus[0], g_focus[1], g_focus[2], 1 };
    for (int r = 0; r < 4; r++) { pc[r] = C[r * 4] * q[0] + C[r * 4 + 1] * q[1] + C[r * 4 + 2] * q[2] + C[r * 4 + 3]; pr[r] = R[r * 4] * q[0] + R[r * 4 + 1] * q[1] + R[r * 4 + 2] * q[2] + R[r * 4 + 3]; }
    if (!(pc[3] > 1e-6f && pr[3] > 1e-6f)) { g_rjOther++; return false; }
    float sh = fmaxf(fabsf(pc[0] / pc[3] - pr[0] / pr[3]), fabsf(pc[1] / pc[3] - pr[1] / pr[3]));
    if (sh > g_rjShift) g_rjShift = sh;
    if (!(sh < 0.5f)) { g_rjFocus++; return false; }
    return true;
}
// voce 239 (diario del 21/11 17:21, modelli collegati accesi): nei fotogrammi interrotti Revit scrive soprattutto le
// matrici degli elementi dei collegamenti, cioe' camera x trasformazione del collegamento. Scartate (punto guardato
// spostato fino a 4500 volte la finestra), la nuvola si disegnava con la camera precedente e restava indietro rispetto
// al modello. Il plugin passa le trasformazioni inverse dei collegamenti (dal progetto al collegamento): moltiplicando
// la matrice per l'inversa si ritrova la camera del progetto.
static float g_linkInv[16][16]; static int g_nLinks = 0; static long g_linkHits = 0; static bool g_candLink = false;
extern "C" __declspec(dllexport) void PdSetLinks(const float* m, int n)
{
    if (n < 0) n = 0; if (n > 16) n = 16;
    if (m && n > 0) memcpy(g_linkInv, m, (size_t)n * 64);
    g_nLinks = n;
}
static bool UnLink(const float* raw, int L, float* out)
{
    if (g_mOff2 >= 0) return false;   // solo con la matrice unica (vista per proiezione)
    float C[16], D[16]; ColForm(raw, C);
    const float* R = g_linkInv[L];
    for (int r = 0; r < 4; r++) for (int k = 0; k < 4; k++) { float v = 0; for (int j = 0; j < 4; j++) v += C[r * 4 + j] * R[j * 4 + k]; D[r * 4 + k] = v; }
    memcpy(out, raw, 32 * sizeof(float));
    for (int r = 0; r < 4; r++) for (int k = 0; k < 4; k++) { if (g_mT & 1) out[r * 4 + k] = D[r * 4 + k]; else out[k * 4 + r] = D[r * 4 + k]; }
    return true;
}
// voce 245 (video e diario del 21/11 18:07:24, a 6,7 s): con i collegamenti Revit fa un fotogramma completo ogni 200-800 ms
// e in mezzo presenta 6-10 immagini in cui il modello si muove. In quelle immagini ci sono 16 matrici, ma venivano tutte
// scartate perche' il punto guardato si era spostato di oltre un quarto di finestra rispetto all'ultima camera accettata:
// ruotando in fretta la camera vera si allontana presto da quella vecchia e da li' in poi nessuna passa piu' fino al
// fotogramma completo successivo (modello e nuvola staccati fino a 0,8 s). Ora la camera del progetto si riconosce
// senza confronto con quella vecchia: e' la matrice M per cui un'altra matrice dello stesso fotogramma vale M x T, con T
// la trasformazione di un collegamento (gli elementi dei collegamenti si disegnano con camera x trasformazione).
static long g_hostHits = 0;
static long g_noBcnImgs = 0, g_noBcnMoved = 0;   // voce 279
static double g_candDumpAt = 0;
static void Mul4(const float* A, const float* B, float* O)
{
    for (int r = 0; r < 4; r++) for (int k = 0; k < 4; k++) { float v = 0; for (int j = 0; j < 4; j++) v += A[r * 4 + j] * B[j * 4 + k]; O[r * 4 + k] = v; }
}
static bool NearMat(const float* A, const float* B)
{
    for (int r = 0; r < 4; r++)
    {
        float n = fabsf(A[r * 4]) + fabsf(A[r * 4 + 1]) + fabsf(A[r * 4 + 2]) + fabsf(A[r * 4 + 3]) + 1e-6f;
        for (int k = 0; k < 4; k++) if (fabsf(A[r * 4 + k] - B[r * 4 + k]) > 1e-3f * n) return false;
    }
    return true;
}
static int HostByLinks()
{
    if (g_mOff2 >= 0 || g_nLinks <= 0 || g_nCand < 2) return -1;
    static float Cf[16][16];
    for (int i = 0; i < g_nCand; i++) ColForm(g_cand[i].m, Cf[i]);
    for (int L = 0; L < g_nLinks; L++)
    {
        // inversa (dal progetto al collegamento), la sua inversa (rigida) e le trasposte: l'ordine con cui Revit compone
        // camera e collegamento non e' documentato, si provano tutte
        const float* Ti = g_linkInv[L];
        float T[16] = {}, TiT[16], TT[16];
        for (int r = 0; r < 3; r++) for (int k = 0; k < 3; k++) T[r * 4 + k] = Ti[k * 4 + r];
        for (int r = 0; r < 3; r++) T[r * 4 + 3] = -(T[r * 4] * Ti[3] + T[r * 4 + 1] * Ti[7] + T[r * 4 + 2] * Ti[11]);
        T[15] = 1;
        for (int r = 0; r < 4; r++) for (int k = 0; k < 4; k++) { TiT[r * 4 + k] = Ti[k * 4 + r]; TT[r * 4 + k] = T[k * 4 + r]; }
        const float* tr[4] = { Ti, T, TiT, TT };
        for (int v = 0; v < 4; v++)
            for (int j = 0; j < g_nCand; j++)
            {
                float D[16]; Mul4(Cf[j], tr[v], D);
                for (int i = 0; i < g_nCand; i++)
                    if (i != j && NearMat(Cf[i], D))
                    {
                        // D = Cf[j] x tr: se tr porta dal progetto al collegamento, Cf[j] e' la camera del progetto
                        int host = (v == 0 || v == 2) ? i : j;
                        int other = host == i ? j : i;
                        if (LikeRef(g_cand[host].m, false)) { (void)other; return host; }
                    }
            }
    }
    return -1;
}
// voce 245: se nessuna matrice va bene, una volta al secondo il diario elenca le candidate (scala rispetto alla camera
// precedente, spostamento del punto guardato, quante volte scritta) per capire com'e' fatta quella giusta
static void DumpCands()
{
    double now = QpcMs();
    if (now < g_candDumpAt) return;
    g_candDumpAt = now + 1000;
    float R[16]; ColForm(g_chainGen == g_viewGen ? g_chainRaw : g_refRaw, R);
    float q[4] = { g_focus[0], g_focus[1], g_focus[2], 1 };
    float pr[4]; for (int r = 0; r < 4; r++) pr[r] = R[r * 4] * q[0] + R[r * 4 + 1] * q[1] + R[r * 4 + 2] * q[2] + R[r * 4 + 3];
    for (int i = 0; i < g_nCand; i++)
    {
        float C[16]; ColForm(g_cand[i].m, C);
        float pc[4]; for (int r = 0; r < 4; r++) pc[r] = C[r * 4] * q[0] + C[r * 4 + 1] * q[1] + C[r * 4 + 2] * q[2] + C[r * 4 + 3];
        float k = L3(R) > 0 ? L3(C) / L3(R) : 0;
        float dot = (L3(C) > 0 && L3(R) > 0) ? (C[0] * R[0] + C[1] * R[1] + C[2] * R[2]) / (L3(C) * L3(R)) : 0;
        FT("X;candidata;i=%d;scritta=%d;scala=%.3f;coseno_asse_x=%.4f;fuoco_x=%.3f;fuoco_y=%.3f;vecchio_x=%.3f;vecchio_y=%.3f",
           i, g_cand[i].n, k, dot, pc[3] != 0 ? pc[0] / pc[3] : 0, pc[3] != 0 ? pc[1] / pc[3] : 0, pr[3] != 0 ? pr[0] / pr[3] : 0, pr[3] != 0 ? pr[1] / pr[3] : 0);
    }
}
static int BestValidCand()
{
    int b = -1; g_candLink = false;
    for (int i = 0; i < g_nCand; i++)
    {
        if (!LikeRef(g_cand[i].m))
        {
            bool ok = false;
            float t[32];
            for (int L = 0; L < g_nLinks && !ok; L++)
                if (UnLink(g_cand[i].m, L, t) && LikeRef(t)) { memcpy(g_cand[i].m, t, sizeof(t)); ok = true; g_linkHits++; g_candLink = true; }
            if (!ok) { g_camRejected++; continue; }
        }
        if (b < 0 || g_cand[i].n > g_cand[b].n) b = i;
    }
    return b;
}

static void ShaderDraw(ID3D11DeviceContext* c)
{
    // la matrice si cerca anche prima che il plugin passi i blocchi (il plugin aspetta che sia trovata)
    if (!EnsureMatrix(c)) { g_skipMatrix++; FT("M;salto=matrice_non_pronta"); return; }
    // voce 209: buffer della matrice, destinazione e profondita' di questo fotogramma normale, per i fotogrammi interrotti
    {
        ID3D11Buffer* b = nullptr; c->VSGetConstantBuffers(g_mSlot, 1, &b);
        if (b) { g_capBuf = b; b->Release(); }
        // voce 287: camera uguale a quella del fotogramma completo precedente? (serve a riconoscere i veri fotogrammi parziali)
        bool sameCam = g_hasRef && g_hasLastCap && g_refGen == g_viewGen && memcmp(g_refRaw, g_lastCap, (g_mOff2 >= 0 ? 32 : 16) * 4) == 0;
        if (g_hasLastCap) { memcpy(g_refRaw, g_lastCap, sizeof(g_refRaw)); g_hasRef = true; g_refGen = g_viewGen; memcpy(g_chainRaw, g_lastCap, sizeof(g_chainRaw)); g_chainGen = g_viewGen; g_chainFromBeacon = false; }
        ID3D11RenderTargetView* rtv = nullptr; ID3D11DepthStencilView* dsv = nullptr;
        c->OMGetRenderTargets(1, &rtv, &dsv);
        if (rtv) rtv->Release();
        if (g_fbDsv != dsv) { if (g_fbDsv) g_fbDsv->Release(); g_fbDsv = dsv; } else if (dsv) dsv->Release();
        UINT nv = 1; c->RSGetViewports(&nv, &g_fbVp);
        g_drawnThisFrame = true;
        g_dzArm = true;
        g_postWant = g_flatView && !g_noPost;   // voce 294: impara il prossimo disegno di Revit
        // voce 229: forbici di Revit attive al segnale = fotogramma parziale
        // voce 287 (sezione 38 del 24/11 17:32): nelle sezioni Revit tiene le forbici sul riquadro di ritaglio della vista
        // in ogni fotogramma, anche mentre si sposta o si zooma. Tutti i fotogrammi risultavano «parziali» (137 su 138):
        // la nuvola teneva l'elenco dei blocchi e la profondita' del fotogramma prima, con un'altra camera, e si vedeva
        // tagliata o coperta male. Ora un fotogramma e' parziale solo se la camera non e' cambiata (mouse sopra un
        // elemento, selezione); altrimenti le forbici si rispettano ma il fotogramma vale come completo.
        g_partial = false; g_scRect = false;
        ID3D11RasterizerState* rrs = nullptr; c->RSGetState(&rrs);
        D3D11_RASTERIZER_DESC rrd = {}; if (rrs) { rrs->GetDesc(&rrd); rrs->Release(); }
        if (rrd.ScissorEnable)
        {
            UINT ns = 1; D3D11_RECT sr = {}; c->RSGetScissorRects(&ns, &sr);
            if (ns > 0 && PartialRect(sr, g_fbVp, g_partRect))
            {
                g_scRect = true;
                if (FlatSimple()) g_cropFrames++;   // voce 290: in pianta/sezione mai «parziale», le forbici si rispettano
                else if (sameCam || g_noCropFix) { g_partial = true; g_partMarker++; g_partLast = g_partRect; } else g_cropFrames++;
            }
        }
    }
    {
        long long pts = 0; for (const PdItem& it : g_ready) pts += it.count;
        FT("M;blocchi=%zu;punti=%lld;parziale=%d;rett=%ldx%ld;vp=%.0fx%.0f", g_ready.size(), pts, g_partial ? 1 : 0,
           g_partial ? (long)(g_partRect.right - g_partRect.left) : 0L, g_partial ? (long)(g_partRect.bottom - g_partRect.top) : 0L, g_fbVp.Width, g_fbVp.Height);
    }
    if (!g_shaderOn || g_ready.empty()) { if (g_shaderOn) g_skipEmpty++; return; }
    if (Transparent()) return;   // voce 217: con la trasparenza si disegna al Present (DrawAtPresent)
    ID3D11Device* dev = nullptr; c->GetDevice(&dev);
    if (!dev) return;
    bool ok = ShaderInit(dev);
    dev->Release();
    if (!ok) return;
    ID3D11Buffer* rcb = nullptr; c->VSGetConstantBuffers(g_mSlot, 1, &rcb);
    if (!rcb) return;
    D3D11_VIEWPORT vp; UINT nv = 1; c->RSGetViewports(&nv, &vp);
    if (nv == 0 || vp.Width < 1) { rcb->Release(); return; }
    // voce 289 (Sezione 39 del 24/11 17:52: passando col mouse la nuvola andava ogni tanto sopra il modello). Nei
    // fotogrammi parziali (camera ferma, Revit ridisegna solo un rettangolo attorno all'elemento sotto il mouse) la
    // profondita' di Revit in quel momento non contiene tutto il modello: la nuvola disegnata al segnale passava davanti.
    // Con la camera uguale c'e' la profondita' intera del fotogramma completo di prima: si usa quella, senza scriverla.
    if (FaroMode() && g_faroDrawn) { rcb->Release(); return; }   // voce 297: gia' disegnata al posto del faro
    ID3D11DepthStencilView* pdz = (g_partial && !g_noPartialKeep && KeepStillGood()) ? KeepDsvForPartial() : nullptr;
    SavedState st; st.Save(c, pdz != nullptr);
    if (pdz)
    {
        ID3D11RenderTargetView* crt = nullptr; ID3D11DepthStencilView* cds = nullptr; c->OMGetRenderTargets(1, &crt, &cds);
        c->OMSetRenderTargets(1, &crt, pdz);
        if (crt) crt->Release(); if (cds) cds->Release();
        g_depthNoWrite = true; g_partialKeepDraws++;
    }
    g_useScissor = g_partial || g_scRect;   // voce 229, 287: le forbici di Revit sono gia' impostate, basta rispettarle
    g_drawSrc = 0; g_noDepth = false;
    DrawPointsCore(c, rcb, g_mOff, g_mT & 1, g_mOff2, (g_mT >> 1) & 1, vp, g_ptAlpha ? 1 : 0);   // voce 259: i trasparenti al Present
    g_useScissor = false;
    if (pdz) g_depthNoWrite = false;
    st.Restore(c);
    rcb->Release();
}

// Al Present della finestra della vista: se in questo fotogramma Revit non ha chiamato il plugin, disegna la nuvola qui.
// Voce 210 (registro 20/11 9:31): Revit disegna la vista in un'immagine a parte (la destinazione del nostro disegno non e'
// mai l'immagine della finestra: «altre destinazioni» a ogni Present) e il buffer della matrice viene scritto solo per il
// nostro contenuto (una scrittura per fotogramma), quindi la camera dei fotogrammi interrotti non si puo' ricavare.
// Ora: la finestra della vista e' quella con l'immagine grande quanto la nostra destinazione; nei fotogrammi interrotti
// la nuvola si disegna direttamente nell'immagine della finestra, con il buffer di profondita' di Revit e la matrice
// vera dell'ultimo fotogramma completo (la nuvola puo' restare indietro di qualche fotogramma rispetto al modello).
// Disegno dei punti nell'immagine della finestra, con il buffer di profondita' di Revit e la camera cam (formato del
// buffer di Revit). Restituisce true se ha disegnato.
// voce 259: sel -1 = tutta la nuvola (pieni e poi trasparenti se ce ne sono), 2 = solo i punti trasparenti
static bool DrawAtPresent(IDXGISwapChain* s, const float* cam, ID3D11DepthStencilView* dsv, const D3D11_RECT* sc, bool whole = false, int sel = -1)
{
    if (!dsv || !ShaderInit(g_realDev)) return false;
    bool done = false;
    ID3D11Texture2D* bb = nullptr;
    if (g_uiRtv) { ID3D11Resource* ur = nullptr; g_uiRtv->GetResource(&ur); bb = (ID3D11Texture2D*)ur; if (!bb) return false; }   // voce 268
    else if (FAILED(s->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&bb)) || !bb) return false;
    ID3D11Resource* dr = nullptr; dsv->GetResource(&dr);
    D3D11_TEXTURE2D_DESC bd2 = {}, dd2 = {};
    bb->GetDesc(&bd2);
    if (dr) { ((ID3D11Texture2D*)dr)->GetDesc(&dd2); dr->Release(); }
    if (dd2.Width != bd2.Width || dd2.Height != bd2.Height || dd2.SampleDesc.Count != bd2.SampleDesc.Count) { g_fbRejected++; bb->Release(); return false; }
    ID3D11DeviceContext* c = nullptr; g_realDev->GetImmediateContext(&c);
    if (!g_fbMat)
    {
        D3D11_BUFFER_DESC bd = {}; bd.ByteWidth = 128; bd.Usage = D3D11_USAGE_DYNAMIC; bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER; bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        g_realDev->CreateBuffer(&bd, nullptr, &g_fbMat);
    }
    ID3D11RenderTargetView* rtv = nullptr;
    if (g_uiRtv) { rtv = g_uiRtv; rtv->AddRef(); }   // voce 268: la destinazione che Revit sta usando per l'interfaccia
    if (c && g_fbMat && (rtv || (SUCCEEDED(g_realDev->CreateRenderTargetView(bb, nullptr, &rtv)) && rtv)))
    {
        D3D11_MAPPED_SUBRESOURCE m;
        if (SUCCEEDED(c->Map(g_fbMat, 0, D3D11_MAP_WRITE_DISCARD, 0, &m)))
        {
            memcpy(m.pData, cam, 128);
            c->Unmap(g_fbMat, 0);
            SavedState st; st.Save(c, true);
            c->OMSetRenderTargets(1, &rtv, dsv);
            c->RSSetViewports(1, &g_fbVp);
            // voce 229: fotogramma parziale, solo dentro il rettangolo ridisegnato da Revit
            D3D11_RECT oldSc[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE]; UINT nOld = 0;
            if (sc)
            {
                nOld = D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE; c->RSGetScissorRects(&nOld, oldSc);
                c->RSSetScissorRects(1, sc); g_useScissor = true;
            }
            // voce 247: camera dal faro = matrice intera nella prima, identita' nella seconda (usata solo se serve)
            int o2 = whole ? 4 : (g_mOff2 >= 0 ? 4 : -1), t2 = whole ? 1 : (g_mT >> 1) & 1;
            if (sel == 2) DrawPointsCore(c, g_fbMat, 0, g_mT & 1, o2, t2, g_fbVp, 2);
            else if (g_ptAlpha && !Transparent()) { DrawPointsCore(c, g_fbMat, 0, g_mT & 1, o2, t2, g_fbVp, 1); DrawPointsCore(c, g_fbMat, 0, g_mT & 1, o2, t2, g_fbVp, 2); }
            else DrawPointsCore(c, g_fbMat, 0, g_mT & 1, o2, t2, g_fbVp);
            if (sc) { g_useScissor = false; c->RSSetScissorRects(nOld, nOld ? oldSc : nullptr); }
            st.Restore(c);
            done = true;
        }
        rtv->Release();
    }
    if (c) c->Release();
    bb->Release();
    return done;
}

static long g_fbAlpha = 0;   // voce 217: fotogrammi completi disegnati al Present per la trasparenza

// voce 222: copia della profondita' del modello presa al Present di ogni fotogramma completo, usata nei fotogrammi
// interrotti. Revit scambia i suoi buffer di profondita' tra un fotogramma e l'altro (sequenza della 220, con un elemento
// selezionato: quello della scena diventa quello della finestra), quindi nei fotogrammi in cui ricompone l'immagine
// (passaggio del mouse, selezione) il buffer tenuto dal segnale poteva contenere altro e la nuvola finiva davanti al
// modello. Al Present il fotogramma completo e' finito: la copia contiene tutto il modello visto da quella camera.
static ID3D11Texture2D* g_keepTex = nullptr;
static ID3D11DepthStencilView* g_keepDsv = nullptr;
static bool g_keepOk = false;
static long g_keepCopies = 0;
// La copia della profondita' gia' tenuta va ancora bene per un fotogramma parziale se la camera e' la stessa: fuori dal
// rettangolo la copia nuova sarebbe vuota (e servirebbe ai fotogrammi interrotti successivi).
static bool KeepStillGood()
{
    return g_partial && g_keepOk && g_keepCamOk && memcmp(g_keepCam, g_refRaw, sizeof(g_keepCam)) == 0;
}
static ID3D11DepthStencilView* KeepDsvForPartial() { return g_keepOk ? g_keepDsv : nullptr; }
static void KeepDepth()
{
    ID3D11DeviceContext* c = nullptr; if (g_realDev) g_realDev->GetImmediateContext(&c);
    if (c) { KeepDepthFrom(c); c->Release(); }
}
static void KeepDepthFrom(ID3D11DeviceContext* ctx)
{
    g_keepOk = false; g_keepCamOk = false;
    if (!g_fbDsv || !g_realDev) return;
    ID3D11Resource* r = nullptr; g_fbDsv->GetResource(&r);
    if (!r) return;
    D3D11_TEXTURE2D_DESC d = {}; ((ID3D11Texture2D*)r)->GetDesc(&d);
    D3D11_TEXTURE2D_DESC k = {};
    if (g_keepTex) g_keepTex->GetDesc(&k);
    if (!g_keepTex || k.Width != d.Width || k.Height != d.Height || k.Format != d.Format || k.SampleDesc.Count != d.SampleDesc.Count)
    {
        if (g_keepDsv) { g_keepDsv->Release(); g_keepDsv = nullptr; }
        if (g_keepTex) { g_keepTex->Release(); g_keepTex = nullptr; }
        D3D11_TEXTURE2D_DESC nd = d;
        nd.Usage = D3D11_USAGE_DEFAULT; nd.BindFlags = D3D11_BIND_DEPTH_STENCIL; nd.CPUAccessFlags = 0; nd.MiscFlags = 0;
        nd.MipLevels = 1; nd.ArraySize = 1;
        D3D11_DEPTH_STENCIL_VIEW_DESC vd = {}; g_fbDsv->GetDesc(&vd);
        if (SUCCEEDED(g_realDev->CreateTexture2D(&nd, nullptr, &g_keepTex)) && g_keepTex)
            if (FAILED(g_realDev->CreateDepthStencilView(g_keepTex, &vd, &g_keepDsv))) { g_keepTex->Release(); g_keepTex = nullptr; g_keepDsv = nullptr; }
    }
    if (g_keepTex && g_keepDsv && d.MipLevels == 1 && d.ArraySize == 1) { ctx->CopyResource(g_keepTex, r); g_keepOk = true; g_keepCopies++; memcpy(g_keepCam, g_refRaw, sizeof(g_keepCam)); g_keepCamOk = g_hasRef; }
    r->Release();
}
// voce 246: «faro». Il motore delle nuvole del plugin (quello che Revit disegna da se', come un RCP) consegna a Revit
// BeaconPts punti tutti nello stesso punto al centro della nuvola, invece di nessuno. Revit disegna le sue nuvole anche
// nelle immagini intermedie (con l'RCP la nuvola resta sempre attaccata al modello): se il disegno del faro compare anche
// li', la matrice attiva in quel momento da' la camera vera. Questa prima prova solo registra i disegni con un numero
// di punti compatibile (riga B, al massimo 10 al secondo) e, a ogni Present, quanti ce ne sono stati (riga F).
static const UINT BeaconPts = 41;
static long g_bcnPts = 0, g_bcnOther = 0, g_bcnFull = 0, g_bcnInt = 0, g_bcnPresFull = 0, g_bcnPresInt = 0;
static double g_bcnLogAt = 0; static int g_bcnLogs = 0;
static bool BeaconCount(UINT n) { if (n == 0 || n % BeaconPts != 0) return false; UINT k = n / BeaconPts; return k == 1 || k == 2 || k == 3 || k == 4 || k == 6; }
// voce 247 (diario del 21/11 19:16): il faro si vede come Draw(41) a punti (topologia 1), uno per fotogramma completo, e
// c'e' in 28 su 29 delle immagini intermedie con 16 matrici (quelle in cui il modello si muove e la nuvola restava
// indietro); manca solo in quelle senza nessuna matrice, dove Revit ripresenta l'immagine di prima. Ora:
// - si tiene una copia dell'ultimo contenuto scritto di ogni buffer di costanti piccolo (Map/Unmap);
// - al disegno del faro si fotografano i buffer di costanti del suo vertex shader e il disegno non arriva allo schermo;
// - nei fotogrammi completi, dove la camera vera e' nota, si cerca in quella fotografia la matrice M del faro: per la
//   posizione giusta K = camera^-1 x M (trasformazione della nuvola) resta uguale anche quando la camera cambia;
// - nelle immagini intermedie con il faro, camera = M x K^-1.
struct ShadowBuf { std::vector<float> f; };
static std::unordered_map<ID3D11Resource*, ShadowBuf> g_shadow;
static ID3D11Resource* g_shPendRes[32] = {}; static void* g_shPendPtr[32] = {}; static UINT g_shPendLen[32] = {}; static int g_shPendN = 0;
static bool g_bcnEverSeen = false;   // voce 254: copie dei buffer solo se il faro e' attivo
static void ShadowMap(ID3D11Resource* r, void* p)
{
    if (!g_shaderWanted || !g_bcnEverSeen || !r || !p) return;
    D3D11_RESOURCE_DIMENSION dim; r->GetType(&dim);
    if (dim != D3D11_RESOURCE_DIMENSION_BUFFER) return;
    D3D11_BUFFER_DESC d; ((ID3D11Buffer*)r)->GetDesc(&d);
    if (!(d.BindFlags & D3D11_BIND_CONSTANT_BUFFER) || d.ByteWidth > 4096) return;
    int k = g_shPendN < 32 ? g_shPendN++ : 31;
    g_shPendRes[k] = r; g_shPendPtr[k] = p; g_shPendLen[k] = d.ByteWidth;
}
static void ShadowUnmap(ID3D11Resource* r)
{
    for (int k = g_shPendN - 1; k >= 0; k--)
        if (g_shPendRes[k] == r)
        {
            if (g_shadow.size() > 4096) g_shadow.clear();
            ShadowBuf& b = g_shadow[r];
            b.f.resize(g_shPendLen[k] / 4);
            memcpy(b.f.data(), g_shPendPtr[k], (g_shPendLen[k] / 4) * 4);
            g_shPendRes[k] = g_shPendRes[g_shPendN - 1]; g_shPendPtr[k] = g_shPendPtr[g_shPendN - 1]; g_shPendLen[k] = g_shPendLen[g_shPendN - 1]; g_shPendN--;
            return;
        }
}
static const int BcnSlots = 4;
static std::vector<float> g_bcnSnap[BcnSlots];   // buffer di costanti del vertex shader al disegno del faro
static bool g_bcnSnapOk = false;                // fotografia presa in questo fotogramma
static long g_bcnHidden = 0;
// voce 248: profondita' dell'immagine del faro. Nelle immagini intermedie la nuvola si disegnava con la camera nuova (dal
// faro) ma con la profondita' copiata all'ultimo fotogramma completo, cioe' vista dalla camera vecchia: il modello di
// prima copriva la nuvola dove non doveva e mancavano pezzi interi (video del 21/11 19:28:42, 25-28 s). Ora al disegno
// del faro si prende nota del buffer di profondita' della scena e, quando Revit lo lascia, se ne fa una copia: e' la
// profondita' di quella stessa immagine. Se la copia non c'e', in quell'immagine la profondita' si ignora.
static ID3D11Resource* g_bcnDzSrc = nullptr;   // solo confronto, senza riferimento
static bool g_bcnDzArm = false, g_bcnDepthOk = false;
static ID3D11Texture2D* g_bcnTex = nullptr; static ID3D11DepthStencilView* g_bcnDsv = nullptr;
static long g_bcnDepthCopies = 0, g_bcnDepthMissing = 0, g_bcnStill = 0;
static long g_bcnCopySerial = 0;   // voce 272: numero dell'ultima copia della profondita' del faro
static bool g_bcnLocked = false;   // voce 247: posizione della matrice del faro appresa
// voce 251: trasformazioni esatte delle istanze delle nostre nuvole (dal plugin, doppia precisione)
static long g_cloudTVer = 0;   // g_cloudT e g_nCloudT sono dichiarati prima (voce 262)
static bool g_bcnExact = false; static long g_bcnExactVer = -1; static int g_bcnExactIdx = -1;
extern "C" __declspec(dllexport) void PdSetCloudTransforms(const double* m, int n)
{
    if (n < 0) n = 0; if (n > 8) n = 8;
    bool ch = n != g_nCloudT || (m && n > 0 && memcmp(g_cloudT, m, (size_t)n * 128) != 0);
    if (m && n > 0) memcpy(g_cloudT, m, (size_t)n * 128);
    g_nCloudT = n;
    if (ch) g_cloudTVer++;
}
static D3D11_DEPTH_STENCIL_VIEW_DESC g_bcnDzDesc = {};
// voce 264 (diario del 23/11 10:36): nelle immagini col faro la copia della profondita' della scena (#5) a volte contiene
// solo il 3-4% della finestra contro il 37-40% dei fotogrammi completi: li' Revit sta ancora ridisegnando la scena a pezzi,
// e il modello che si vede e' quello disegnato direttamente nell'immagine della finestra (#1) con un altro buffer di
// profondita' (#2), prima dell'interfaccia (#3). Quando Revit lascia quel buffer dopo averci disegnato il modello se ne fa
// una copia; nelle immagini col faro la nuvola si confronta con quella.
static ID3D11Resource* g_bbRes = nullptr;      // non nullo = immagine della finestra conosciuta (vedi IsWindowImage)
static D3D11_TEXTURE2D_DESC g_bbDesc = {};
// l'immagine della finestra si riconosce da dimensioni e formato (con lo scambio dei buffer il puntatore puo' cambiare)
static bool IsWindowImage(ID3D11Resource* r)
{
    if (!r || !g_bbRes) return false;
    D3D11_RESOURCE_DIMENSION dim; r->GetType(&dim);
    if (dim != D3D11_RESOURCE_DIMENSION_TEXTURE2D) return false;
    D3D11_TEXTURE2D_DESC d = {}; ((ID3D11Texture2D*)r)->GetDesc(&d);
    return d.Width == g_bbDesc.Width && d.Height == g_bbDesc.Height && d.Format == g_bbDesc.Format;
}
static long long g_drawSeq = 0, g_bindSeq = 0;   // disegni di Revit, e al momento dell'ultimo cambio di destinazione
static ID3D11Texture2D* g_winTex = nullptr; static ID3D11DepthStencilView* g_winDsv = nullptr;
static bool g_winOk = false; static long g_winCopies = 0, g_winUsed = 0; static long long g_winDraws = 0;
static long g_presentNo = 0, g_winPresent = -1;   // voce 266: la copia vale per l'immagine in corso (Present numerato)
// voce 267: le nostre stesse operazioni (ripristino dello stato dopo il disegno al Present, sulla finestra e dopo
// centinaia di nostri disegni) facevano una copia della NOSTRA profondita': g_ourDraw le esclude
static thread_local bool g_ourDraw = false;   // voce 268: per thread (il Present puo' arrivare da un altro thread)
// voce 268: la nuvola si disegna nell'immagine della finestra subito prima dell'interfaccia (cubo, maniglie, evidenziazione
// al passaggio del mouse), con la profondita' del modello appena disegnato nella finestra; al Present solo se non e' stato
// possibile prima. g_winModelDsv: buffer di profondita' di Revit con il modello della finestra (con riferimento).
// (dichiarati prima, vicino a g_depthBias)
static long g_winAfterFull = 0;   // numero del Present dell'ultimo fotogramma completo
static void WinDepthCopy(ID3D11DeviceContext* ctx, ID3D11DepthStencilView* src)
{
    if (!src || !g_realDev) return;
    ID3D11Resource* r = nullptr; src->GetResource(&r);
    if (!r) return;
    D3D11_TEXTURE2D_DESC d = {}; ((ID3D11Texture2D*)r)->GetDesc(&d);
    D3D11_TEXTURE2D_DESC k = {}; if (g_winTex) g_winTex->GetDesc(&k);
    if (!g_winTex || k.Width != d.Width || k.Height != d.Height || k.Format != d.Format || k.SampleDesc.Count != d.SampleDesc.Count)
    {
        if (g_winDsv) { g_winDsv->Release(); g_winDsv = nullptr; }
        if (g_winTex) { g_winTex->Release(); g_winTex = nullptr; }
        D3D11_TEXTURE2D_DESC nd = d; nd.Usage = D3D11_USAGE_DEFAULT; nd.BindFlags = D3D11_BIND_DEPTH_STENCIL; nd.CPUAccessFlags = 0; nd.MiscFlags = 0; nd.MipLevels = 1; nd.ArraySize = 1;
        D3D11_DEPTH_STENCIL_VIEW_DESC vd = {}; src->GetDesc(&vd);
        HRESULT h1 = g_realDev->CreateTexture2D(&nd, nullptr, &g_winTex), h2 = E_FAIL;
        if (SUCCEEDED(h1) && g_winTex)
        {
            h2 = g_realDev->CreateDepthStencilView(g_winTex, &vd, &g_winDsv);
            if (FAILED(h2)) { g_winTex->Release(); g_winTex = nullptr; g_winDsv = nullptr; }
        }
        // voce 266: nella prova della 264 la copia risultava fatta ma non veniva mai usata: si scrive l'esito
        FT("E;finestra_buffer;%ux%u;formato=%d;campioni=%u;livelli=%u;strati=%u;vista=%d/%d;texture=0x%08lx;vista_profondita=0x%08lx",
           d.Width, d.Height, (int)d.Format, d.SampleDesc.Count, d.MipLevels, d.ArraySize, (int)vd.Format, (int)vd.ViewDimension, (unsigned long)h1, (unsigned long)h2);
    }
    if (g_winTex && g_winDsv && d.MipLevels == 1 && d.ArraySize == 1) { ctx->CopyResource(g_winTex, r); g_winOk = true; g_winCopies++; g_winPresent = g_presentNo; }
    else { static double lastAt = 0; if (QpcMs() - lastAt > 2000) { lastAt = QpcMs(); FT("E;finestra_copia_non_fatta;texture=%d;vista=%d;livelli=%u;strati=%u", g_winTex ? 1 : 0, g_winDsv ? 1 : 0, d.MipLevels, d.ArraySize); } }
    r->Release();
}
static ID3D11Resource* g_bcnDzRef = nullptr;   // voce 250: con riferimento, per la copia al Present se Revit non lo lascia prima
static bool g_bcnDepthChain = false;            // la copia vale anche per le immagini che riusano la camera del faro
// voce 271: nelle immagini intermedie Revit usa con l'immagine della scena (#4) due buffer di profondita': quello dove
// disegna il faro e il modello nuovo (#5, quello che copiamo) e un altro (#6, usato anche con le immagini a 10 bit #7/#8).
// Si tengono gli ultimi due buffer di profondita' della scena (grandi come la finestra, non della finestra, non nostri) e,
// quando si copia quello del faro, si copia anche l'altro: per ora solo per il diario (X;faro_altra, X;completo_altra).
static ID3D11Resource* g_sideRef[2] = { nullptr, nullptr };   // con riferimento
static ID3D11Texture2D* g_sideTex = nullptr; static ID3D11DepthStencilView* g_sideDsv = nullptr;
static bool g_sideOk = false; static long g_sideCopies = 0;
// voce 271: immagini intermedie con la copia della scena molto piu' scarsa di quella dell'ultimo fotogramma completo
// (meno della meta'): 0 = come prima, 1 = la nuvola non si disegna, 2 = si disegna trasparente senza profondita'
static int g_thinMode = 0; static double g_fullCov = -1; static long g_fullCovGen = -1;
static long g_thinSkip = 0, g_thinGhost = 0;
extern "C" __declspec(dllexport) void PdSetThinDepth(int mode) { g_thinMode = mode < 0 ? 0 : (mode > 2 ? 2 : mode); }
static void SideNote(ID3D11Resource* r)
{
    if (!r || r == g_sideRef[0]) return;
    if (r == g_sideRef[1]) { g_sideRef[1] = g_sideRef[0]; g_sideRef[0] = r; return; }
    if (g_sideRef[1]) g_sideRef[1]->Release();
    g_sideRef[1] = g_sideRef[0]; g_sideRef[0] = r; r->AddRef();
}
static bool DepthCopyTo(ID3D11DeviceContext* ctx, ID3D11Resource* r, ID3D11Texture2D*& tex, ID3D11DepthStencilView*& dsv)
{
    if (!r || !g_realDev) return false;
    D3D11_TEXTURE2D_DESC d = {}; ((ID3D11Texture2D*)r)->GetDesc(&d);
    D3D11_TEXTURE2D_DESC k = {}; if (tex) tex->GetDesc(&k);
    if (!tex || k.Width != d.Width || k.Height != d.Height || k.Format != d.Format || k.SampleDesc.Count != d.SampleDesc.Count)
    {
        if (dsv) { dsv->Release(); dsv = nullptr; }
        if (tex) { tex->Release(); tex = nullptr; }
        D3D11_TEXTURE2D_DESC nd = d; nd.Usage = D3D11_USAGE_DEFAULT; nd.BindFlags = D3D11_BIND_DEPTH_STENCIL; nd.CPUAccessFlags = 0; nd.MiscFlags = 0; nd.MipLevels = 1; nd.ArraySize = 1;
        if (SUCCEEDED(g_realDev->CreateTexture2D(&nd, nullptr, &tex)) && tex)
            if (FAILED(g_realDev->CreateDepthStencilView(tex, &g_bcnDzDesc, &dsv))) { tex->Release(); tex = nullptr; dsv = nullptr; }
    }
    if (tex && dsv && d.MipLevels == 1 && d.ArraySize == 1) { ctx->CopyResource(tex, r); return true; }
    return false;
}
// copia dell'altro buffer della scena, quello che non e' del faro
static void SideCopy(ID3D11DeviceContext* ctx, ID3D11Resource* bcn)
{
    ID3D11Resource* o = g_sideRef[0] && g_sideRef[0] != bcn ? g_sideRef[0] : (g_sideRef[1] && g_sideRef[1] != bcn ? g_sideRef[1] : nullptr);
    if (!o || !bcn) return;
    D3D11_TEXTURE2D_DESC a = {}, b = {}; ((ID3D11Texture2D*)o)->GetDesc(&a); ((ID3D11Texture2D*)bcn)->GetDesc(&b);
    if (a.Format != b.Format || a.Width != b.Width || a.Height != b.Height) return;
    if (DepthCopyTo(ctx, o, g_sideTex, g_sideDsv)) { g_sideOk = true; g_sideCopies++; if (g_trRec) TrPut(" COPIA-ALTRA(#%d)", TrId(o)); }
}
static void BeaconDepthCopyRes(ID3D11DeviceContext* ctx, ID3D11Resource* r)
{
    if (!r || !g_realDev) return;
    D3D11_TEXTURE2D_DESC d = {}; ((ID3D11Texture2D*)r)->GetDesc(&d);
    D3D11_TEXTURE2D_DESC k = {}; if (g_bcnTex) g_bcnTex->GetDesc(&k);
    if (!g_bcnTex || k.Width != d.Width || k.Height != d.Height || k.Format != d.Format || k.SampleDesc.Count != d.SampleDesc.Count)
    {
        if (g_bcnDsv) { g_bcnDsv->Release(); g_bcnDsv = nullptr; }
        if (g_bcnTex) { g_bcnTex->Release(); g_bcnTex = nullptr; }
        D3D11_TEXTURE2D_DESC nd = d; nd.Usage = D3D11_USAGE_DEFAULT; nd.BindFlags = D3D11_BIND_DEPTH_STENCIL; nd.CPUAccessFlags = 0; nd.MiscFlags = 0; nd.MipLevels = 1; nd.ArraySize = 1;
        if (SUCCEEDED(g_realDev->CreateTexture2D(&nd, nullptr, &g_bcnTex)) && g_bcnTex)
            if (FAILED(g_realDev->CreateDepthStencilView(g_bcnTex, &g_bcnDzDesc, &g_bcnDsv))) { g_bcnTex->Release(); g_bcnTex = nullptr; g_bcnDsv = nullptr; }
    }
    if (g_bcnTex && g_bcnDsv && d.MipLevels == 1 && d.ArraySize == 1) { ctx->CopyResource(g_bcnTex, r); g_bcnDepthOk = true; g_bcnDepthCopies++; g_bcnCopySerial++; }
}
// =====================================================================================================================
// Voce 272: profondita' del modello disegnata da noi. Il diario della 271 ha mostrato che nelle immagini intermedie la
// profondita' del modello che si vede a schermo non sta in nessun buffer di Revit: #5 e #6 si alternano (uno ha il
// modello nuovo, l'altro e' vuoto o uguale) e il resto dell'immagine e' ricomposto senza profondita'. Ora nella copia
// della profondita' dell'immagine col faro si disegna anche la mesh del modello dell'analisi (modello e collegamenti, dal
// plugin), con la stessa camera della nuvola: dove Revit ha scritto la profondita' vale la piu' vicina delle due, dove non
// l'ha scritta vale la mesh. La mesh si carica nella scheda video una volta; il plugin la cambia solo con una nuova analisi.
// =====================================================================================================================
static std::mutex g_meshMx;
static std::vector<float> g_meshCpu; static int g_meshCpuTris = 0; static double g_meshCpuO[3] = {}; static bool g_meshDirty = false;
static ID3D11Buffer* g_meshBuf = nullptr; static ID3D11ShaderResourceView* g_meshSrv = nullptr; static int g_meshTris = 0;
static double g_meshO[3] = {};
static ID3D11VertexShader* g_meshVs = nullptr; static ID3D11Buffer* g_meshCb = nullptr; static bool g_meshShFailed = false;
static long g_meshOnSerial = -1; static long g_meshDraws = 0, g_meshFail = 0;   // g_bcnCopySerial: vicino a g_bcnTex
// voce 274: tolleranza sulle superfici. I punti rilevati su un muro stanno per meta' pochi millimetri dietro la faccia
// modellata (rumore dello scanner, modello non identico al reale): con la mesh nella profondita' quei punti sparivano e si
// vedeva la facciata del modello (273 spostava la nuvola nel confronto, tolto). Ora la mesh si scrive nella profondita'
// 1 cm piu' lontana, piu' una quota proporzionale all'inclinazione della superficie (polygon offset della scheda video).
// La nuvola non si tocca: i punti restano nella loro posizione e cambia solo chi vince i pareggi entro la tolleranza.
// Solo nella profondita' invisibile delle immagini intermedie; spegnibile da Avanzate (PdSetMeshTolerance).
static bool g_meshTol = true; static ID3D11RasterizerState* g_meshRs = nullptr;
// voce 276: scarto di profondita' con cui Revit scrive il suo modello (diario della 275: Revit piu' lontano della mesh nel
// 90% dei pixel, mediana 7-13 cm, quasi costante in una vista). Si misura nei fotogrammi completi (mesh contro copia della
// profondita' di Revit, stessa camera) e la mesh delle immagini intermedie si scrive con lo stesso scarto: in movimento la
// nuvola si comporta come da ferma. Finche' non c'e' una misura per la vista vale la tolleranza di 1 cm (274).
static bool g_meshRevUsed = false;
static double g_revOff = 0; static long g_revOffGen = -1; static double g_revOffAt = -1e9, g_revOffA = 0;
static long g_revOffN = 0; static double g_revOffCm = 0;
extern "C" __declspec(dllexport) void PdSetMeshTolerance(int on) { g_meshTol = on != 0; }
extern "C" __declspec(dllexport) void PdSetModelMesh(const float* xyz, int tris, const double* origin)
{
    std::lock_guard<std::mutex> l(g_meshMx);
    if (tris < 0 || !xyz || !origin) tris = 0;
    g_meshCpu.assign(xyz ? xyz : (const float*)nullptr, xyz ? xyz + (size_t)tris * 9 : (const float*)nullptr);
    g_meshCpuTris = tris;
    if (origin) { g_meshCpuO[0] = origin[0]; g_meshCpuO[1] = origin[1]; g_meshCpuO[2] = origin[2]; }
    g_meshDirty = true;
}
static const char* g_meshHlsl =
"cbuffer C : register(b0) { float4 R0; float4 R1; float4 R2; float4 R3; float4 Pl[6]; int4 Inf; };\n"
"Buffer<float> V : register(t0);\n"
"struct VO { float4 pos : SV_Position; float4 cd0 : SV_ClipDistance0; float2 cd1 : SV_ClipDistance1; };\n"
"VO VS(uint vid : SV_VertexID) {\n"
"  VO o; float4 q = float4(V.Load(vid * 3), V.Load(vid * 3 + 1), V.Load(vid * 3 + 2), 1);\n"
"  o.pos = float4(dot(R0, q), dot(R1, q), dot(R2, q), dot(R3, q));\n"
"  o.pos.z += asfloat(Inf.y) * o.pos.w;\n"
"  float dd[6];\n"
"  [unroll] for (int k = 0; k < 6; k++) dd[k] = Inf.x != 0 ? dot(Pl[k].xyz, q.xyz) + Pl[k].w : 1.0;\n"
"  o.cd0 = float4(dd[0], dd[1], dd[2], dd[3]); o.cd1 = float2(dd[4], dd[5]);\n"
"  return o; }\n";
static bool MeshReady(ID3D11Device* dev)
{
    if (!g_meshVs && !g_meshShFailed)
    {
        ID3DBlob *vb = nullptr, *err = nullptr;
        if (FAILED(D3DCompile(g_meshHlsl, strlen(g_meshHlsl), "pdmesh", nullptr, nullptr, "VS", "vs_4_0", 0, 0, &vb, &err)) || !vb)
        { FT("E;mesh_shader;%s", err ? (char*)err->GetBufferPointer() : "?"); if (err) err->Release(); g_meshShFailed = true; return false; }
        dev->CreateVertexShader(vb->GetBufferPointer(), vb->GetBufferSize(), nullptr, &g_meshVs); vb->Release();
        D3D11_BUFFER_DESC bd = {}; bd.ByteWidth = 176; bd.Usage = D3D11_USAGE_DYNAMIC; bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER; bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        dev->CreateBuffer(&bd, nullptr, &g_meshCb);
        D3D11_RASTERIZER_DESC rd = {}; rd.FillMode = D3D11_FILL_SOLID; rd.CullMode = D3D11_CULL_NONE; rd.DepthClipEnable = TRUE;
        rd.DepthBias = 0; rd.DepthBiasClamp = 0; rd.SlopeScaledDepthBias = 1.5f;   // voce 274
        dev->CreateRasterizerState(&rd, &g_meshRs);
        if (!g_meshVs || !g_meshCb) { g_meshShFailed = true; FT("E;mesh_shader;creazione_non_riuscita"); return false; }
    }
    if (g_meshShFailed) return false;
    std::lock_guard<std::mutex> l(g_meshMx);
    if (g_meshDirty)
    {
        g_meshDirty = false;
        if (g_meshSrv) { g_meshSrv->Release(); g_meshSrv = nullptr; }
        if (g_meshBuf) { g_meshBuf->Release(); g_meshBuf = nullptr; }
        g_meshTris = 0;
        if (g_meshCpuTris > 0)
        {
            D3D11_BUFFER_DESC bd = {}; bd.ByteWidth = (UINT)((size_t)g_meshCpuTris * 36); bd.Usage = D3D11_USAGE_IMMUTABLE; bd.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            D3D11_SUBRESOURCE_DATA sd = {}; sd.pSysMem = g_meshCpu.data();
            HRESULT h = dev->CreateBuffer(&bd, &sd, &g_meshBuf);
            if (SUCCEEDED(h) && g_meshBuf)
            {
                D3D11_SHADER_RESOURCE_VIEW_DESC vd = {}; vd.Format = DXGI_FORMAT_R32_FLOAT; vd.ViewDimension = D3D11_SRV_DIMENSION_BUFFER;
                vd.Buffer.FirstElement = 0; vd.Buffer.NumElements = (UINT)((size_t)g_meshCpuTris * 9);
                if (SUCCEEDED(dev->CreateShaderResourceView(g_meshBuf, &vd, &g_meshSrv)) && g_meshSrv) { g_meshTris = g_meshCpuTris; memcpy(g_meshO, g_meshCpuO, sizeof(g_meshO)); }
                else { g_meshBuf->Release(); g_meshBuf = nullptr; }
            }
            FT("E;mesh_caricata;triangoli=%d;esito=%s", g_meshCpuTris, g_meshTris > 0 ? "ok" : "non_riuscita");
        }
        else FT("E;mesh_tolta");
        std::vector<float>().swap(g_meshCpu);
    }
    return g_meshTris > 0 && g_meshSrv;
}
// disegna la mesh nel buffer di profondita' dsv con la camera cam (formato del buffer di Revit); true se disegnata
static void ColFormD(const float* raw, double* C);
static bool MeshDepth(ID3D11DeviceContext* c, ID3D11DepthStencilView* dsv, const float* cam)
{
    if (!dsv || !cam || !g_realDev || !ShaderInit(g_realDev) || !MeshReady(g_realDev)) return false;
    double C[16]; ColFormD(cam, C);
    D3D11_MAPPED_SUBRESOURCE m;
    if (FAILED(c->Map(g_meshCb, 0, D3D11_MAP_WRITE_DISCARD, 0, &m))) { g_meshFail++; return false; }
    float* f = (float*)m.pData; int* in = (int*)m.pData;
    for (int r = 0; r < 4; r++)
    {   // vertici relativi all'origine della mesh: la traslazione entra nella colonna 3 (in doppia precisione)
        for (int k = 0; k < 3; k++) f[r * 4 + k] = (float)C[r * 4 + k];
        f[r * 4 + 3] = (float)(C[r * 4] * g_meshO[0] + C[r * 4 + 1] * g_meshO[1] + C[r * 4 + 2] * g_meshO[2] + C[r * 4 + 3]);
    }
    for (int k = 0; k < 6; k++)
    {
        const float* pl = g_clip + k * 4;
        f[16 + k * 4] = pl[0]; f[17 + k * 4] = pl[1]; f[18 + k * 4] = pl[2];
        f[19 + k * 4] = (float)(pl[3] + pl[0] * g_meshO[0] + pl[1] * g_meshO[1] + pl[2] * g_meshO[2]);
    }
    in[40] = g_clipOn; in[42] = in[43] = 0;
    {   // voce 274: 1 cm (0,0328 ft) piu' lontano, nella scala della profondita' di questa camera
        double a = sqrt(C[8] * C[8] + C[9] * C[9] + C[10] * C[10]);
        bool rev = g_revOffGen == g_viewGen && g_revOff > 0;   // voce 276: lo scarto di Revit, se misurato in questa vista
        f[41] = g_meshTol ? (float)(rev ? g_revOff : 0.0328 * a) : 0.0f;
        g_meshRevUsed = g_meshTol && rev;
    }
    c->Unmap(g_meshCb, 0);
    struct In { In() { g_inOurDraw++; } ~In() { g_inOurDraw--; } } inOur;
    SavedState st; st.Save(c, true);
    ID3D11ShaderResourceView* ps0 = nullptr; c->PSGetShaderResources(0, 1, &ps0);
    c->OMSetRenderTargets(0, nullptr, dsv);
    c->RSSetViewports(1, &g_fbVp);
    c->IASetInputLayout(nullptr);
    c->IASetIndexBuffer(nullptr, DXGI_FORMAT_R32_UINT, 0);
    c->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    c->VSSetShader(g_meshVs, nullptr, 0);
    c->PSSetShader(nullptr, nullptr, 0);
    c->GSSetShader(nullptr, nullptr, 0);
    c->VSSetConstantBuffers(0, 1, &g_meshCb);
    c->VSSetShaderResources(0, 1, &g_meshSrv);
    c->OMSetDepthStencilState(g_sdss, 0);
    c->RSSetState(g_meshTol && !g_meshRevUsed && g_meshRs ? g_meshRs : g_srs);   // con lo scarto di Revit niente quota per l'inclinazione
    FLOAT zero[4] = { 0, 0, 0, 0 };
    c->OMSetBlendState(g_bs, zero, 0xffffffff);
    c->Draw((UINT)g_meshTris * 3, 0);
    ID3D11ShaderResourceView* nul = nullptr; c->VSSetShaderResources(0, 1, &nul);
    st.Restore(c);
    if (ps0) ps0->Release();
    g_meshDraws++;
    return true;
}
// voce 275: confronto tra la profondita' del modello scritta da Revit (copia di un fotogramma completo) e la nostra mesh
// disegnata con la stessa camera, senza tolleranza, in una profondita' a parte. Dice se Revit scrive i muri spinti
// indietro (e di quanto) e se la mesh coincide con il modello di Revit. Solo nel diario (X;mesh_revit), durante le sequenze.
static ID3D11Texture2D* g_cmpTex = nullptr; static ID3D11DepthStencilView* g_cmpDsv = nullptr;
static void MeshVsRevit(ID3D11DeviceContext* c, ID3D11DepthStencilView* revit, const float* cam, bool log = true)
{
    if (!c || !revit || !cam || g_meshTris <= 0 || !g_realDev) return;
    g_revOffAt = QpcMs();   // voce 276: anche se la misura non riesce, non si riprova a ogni fotogramma
    ID3D11Resource* r = nullptr; revit->GetResource(&r);
    if (!r) return;
    D3D11_TEXTURE2D_DESC d = {}; ((ID3D11Texture2D*)r)->GetDesc(&d); r->Release();
    D3D11_DEPTH_STENCIL_VIEW_DESC vd = {}; revit->GetDesc(&vd);
    D3D11_TEXTURE2D_DESC k = {}; if (g_cmpTex) g_cmpTex->GetDesc(&k);
    if (!g_cmpTex || k.Width != d.Width || k.Height != d.Height || k.Format != d.Format || k.SampleDesc.Count != d.SampleDesc.Count)
    {
        if (g_cmpDsv) { g_cmpDsv->Release(); g_cmpDsv = nullptr; }
        if (g_cmpTex) { g_cmpTex->Release(); g_cmpTex = nullptr; }
        D3D11_TEXTURE2D_DESC nd = d; nd.Usage = D3D11_USAGE_DEFAULT; nd.BindFlags = D3D11_BIND_DEPTH_STENCIL; nd.CPUAccessFlags = 0; nd.MiscFlags = 0; nd.MipLevels = 1; nd.ArraySize = 1;
        if (SUCCEEDED(g_realDev->CreateTexture2D(&nd, nullptr, &g_cmpTex)) && g_cmpTex)
            if (FAILED(g_realDev->CreateDepthStencilView(g_cmpTex, &vd, &g_cmpDsv))) { g_cmpTex->Release(); g_cmpTex = nullptr; g_cmpDsv = nullptr; }
    }
    if (!g_cmpDsv) { FT("X;mesh_revit;copia_non_creata"); return; }
    c->ClearDepthStencilView(g_cmpDsv, D3D11_CLEAR_DEPTH | D3D11_CLEAR_STENCIL, 1.0f, 0);
    bool tol = g_meshTol; g_meshTol = false;
    bool ok = MeshDepth(c, g_cmpDsv, cam);
    g_meshTol = tol;
    if (!ok) { FT("X;mesh_revit;mesh_non_disegnata"); return; }
    g_meshDraws--;   // non e' un'immagine col faro
    std::vector<float> zr, zm;
    if (!DepthGrid(c, revit, zr) || !DepthGrid(c, g_cmpDsv, zm) || zr.size() != zm.size() || zr.empty()) { FT("X;mesh_revit;lettura_non_riuscita"); return; }
    std::vector<float> df; df.reserve(zr.size());
    long n = (long)zr.size(), onlyM = 0, onlyR = 0, eq = 0, rFar = 0; double sum = 0;
    for (long i = 0; i < n; i++)
    {
        bool vr = zr[i] > 0, vm = zm[i] > 0;
        if (vm && !vr) onlyM++; else if (vr && !vm) onlyR++;
        else if (vr && vm) { float x = zr[i] - zm[i]; df.push_back(x); sum += x; if (fabs(x) < 2e-6f) eq++; else if (x > 0) rFar++; }
    }
    double C[16]; ColFormD(cam, C);
    double a = sqrt(C[8] * C[8] + C[9] * C[9] + C[10] * C[10]);
    bool ortho = fabs(C[12]) + fabs(C[13]) + fabs(C[14]) < 1e-9;
    size_t m = df.size();
    if (m == 0) { if (log) FT("X;mesh_revit;entrambe=0;solo_mesh=%.1f;solo_revit=%.1f", 100.0 * onlyM / n, 100.0 * onlyR / n); return; }
    std::vector<float> s = df; std::sort(s.begin(), s.end());
    auto q = [&](double f) { return (double)s[(size_t)(f * (m - 1))]; };
    double toCm = ortho && a > 0 ? 30.48 / a : 0;   // differenza di profondita' in cm (solo vista ortogonale)
    {   // voce 276: mediana dei pixel con scarto piccolo (esclusi elementi diversi tra Revit e mesh), con almeno il 2% di pixel
        std::vector<float> w; w.reserve(m);
        for (float x : s) if (x > -0.002f && x < 0.002f) w.push_back(x);
        if (w.size() >= (size_t)(0.02 * n))
        {
            double med = w[w.size() / 2];
            if (med > 0) { g_revOff = med; g_revOffGen = g_viewGen; g_revOffA = a; g_revOffN++; g_revOffCm = med * toCm; }
            if (log) FT("X;scarto_revit;mediano=%.3g;cm=%.2f;pixel=%zu;usato=%d", med, med * toCm, w.size(), med > 0 ? 1 : 0);
        }
    }
    g_revOffAt = QpcMs();
    if (!log) return;
    FT("X;mesh_revit;entrambe=%.1f;solo_mesh=%.1f;solo_revit=%.1f;uguali=%.1f;revit_piu_lontana=%.1f;scarto_p10=%.3g;mediano=%.3g;p90=%.3g;medio=%.3g;cm_p10=%.2f;cm_mediano=%.2f;cm_p90=%.2f;ortogonale=%d",
       100.0 * m / n, 100.0 * onlyM / n, 100.0 * onlyR / n, 100.0 * eq / m, 100.0 * rFar / m, q(0.1), q(0.5), q(0.9), sum / m,
       q(0.1) * toCm, q(0.5) * toCm, q(0.9) * toCm, ortho ? 1 : 0);
}
static void DrawFaro(ID3D11DeviceContext* c)
{   // voce 297
    if (!FaroMode() || g_faroDrawn || g_inOurDraw || !g_shaderOn || g_ready.empty() || !g_hasRef || g_refGen != g_viewGen) return;
    ID3D11RenderTargetView* rt = nullptr; ID3D11DepthStencilView* ds = nullptr; c->OMGetRenderTargets(1, &rt, &ds);
    if (rt && ds)
    {
        float cam[32]; memcpy(cam, g_refRaw, sizeof(cam));
        ID3D11DepthStencilState* rd = nullptr; UINT rref = 0; c->OMGetDepthStencilState(&rd, &rref);
        if (rd) { D3D11_DEPTH_STENCIL_DESC dd; rd->GetDesc(&dd); _snprintf_s(g_faroDssInfo, sizeof(g_faroDssInfo), _TRUNCATE, "profondita' %s, confronto %d, scrittura %s, stencil %s", dd.DepthEnable ? "si'" : "no", (int)dd.DepthFunc, dd.DepthWriteMask ? "si'" : "no", dd.StencilEnable ? "si'" : "no"); }
        else _snprintf_s(g_faroDssInfo, sizeof(g_faroDssInfo), _TRUNCATE, "stato predefinito");
        SavedState st; st.Save(c, true);
        g_uiRtv = rt; g_drawSrc = 4; g_noDepth = false;   // voce 298: fonte "faro" (misure come al segnale)
        if (!g_noFaroDss && rd) { g_faroDss = rd; g_faroDssRef = rref; }
        { ID3D11Resource* sr = nullptr; rt->GetResource(&sr); g_sceneRes = sr; g_sceneBound = true; g_sceneDrawn = true; if (sr) sr->Release(); }   // voce 299
        bool ok = DrawAtPresent(g_fbSwap, cam, ds, nullptr);
        g_faroDss = nullptr;
        if (rd) rd->Release();
        g_uiRtv = nullptr;
        st.Restore(c);
        if (ok) { g_faroDrawn = true; g_faroDraws++; } else g_faroFail++;
        if (g_trRec) TrPut(ok ? " NUVOLA-AL-FARO" : " NUVOLA-AL-FARO-NON-RIUSCITA");
    }
    if (rt) rt->Release(); if (ds) ds->Release();
}
static void BeaconDzDisarm() { g_bcnDzArm = false; if (g_bcnDzRef) { g_bcnDzRef->Release(); g_bcnDzRef = nullptr; } g_bcnDzSrc = nullptr; }
static bool BeaconSeen(ID3D11DeviceContext* c, UINT count, UINT inst, const char* kind)
{
    if (!g_shaderWanted) return false;
    if (!(BeaconCount(count) || (inst > 1 && BeaconCount(inst)))) return false;
    D3D11_PRIMITIVE_TOPOLOGY tp = D3D11_PRIMITIVE_TOPOLOGY_UNDEFINED; c->IAGetPrimitiveTopology(&tp);
    bool pts = tp == D3D11_PRIMITIVE_TOPOLOGY_POINTLIST;
    if (pts) g_bcnPts++; else g_bcnOther++;
    bool beacon = pts && count == BeaconPts && inst == 0;
    if (beacon) g_bcnEverSeen = true;
    if (beacon && !g_bcnSnapOk)
    {
        ID3D11Buffer* cb[BcnSlots] = {}; c->VSGetConstantBuffers(0, BcnSlots, cb);
        for (int k = 0; k < BcnSlots; k++)
        {
            g_bcnSnap[k].clear();
            if (cb[k]) { auto f = g_shadow.find(cb[k]); if (f != g_shadow.end()) g_bcnSnap[k] = f->second.f; cb[k]->Release(); }
        }
        g_bcnSnapOk = true;
        if (g_trRec) { TrFlush(); ID3D11DepthStencilView* tv = nullptr; c->OMGetRenderTargets(0, nullptr, &tv); TrPut(" FARO(d#%d)", TrView(tv)); if (tv) tv->Release(); }   // voce 260
        DrawFaro(c);   // voce 297
        if (g_bcnLocked)   // voce 250: profondita' di questa immagine, da copiare quando Revit la lascia
        {
            ID3D11DepthStencilView* cur = nullptr; c->OMGetRenderTargets(0, nullptr, &cur);
            BeaconDzDisarm();
            if (cur) { cur->GetDesc(&g_bcnDzDesc); cur->GetResource(&g_bcnDzRef); g_bcnDzSrc = g_bcnDzRef; cur->Release(); g_bcnDzArm = g_bcnDzSrc != nullptr; }
        }
    }
    double now = QpcMs();
    if (now > g_bcnLogAt) { g_bcnLogAt = now + 1000; g_bcnLogs = 0; }
    if (g_bcnLogs++ < 4 && !beacon)
    {
        ID3D11VertexShader* vs = nullptr; c->VSGetShader(&vs, nullptr, nullptr);
        FT("B;faro;tipo=%s;conteggio=%u;istanze=%u;topologia=%d;vs=%p;segnale_gia_visto=%d", kind, count, inst, (int)tp, (void*)vs, g_drawnThisFrame ? 1 : 0);
        if (vs) vs->Release();
    }
    if (beacon) g_bcnHidden++;
    return beacon;   // il disegno del faro non arriva allo schermo
}

static bool Inv4(const double* a, double* o)
{
    double m[4][8];
    for (int r = 0; r < 4; r++) for (int k = 0; k < 8; k++) m[r][k] = k < 4 ? a[r * 4 + k] : (k - 4 == r ? 1 : 0);
    for (int col = 0; col < 4; col++)
    {
        int p = col; for (int r = col + 1; r < 4; r++) if (fabs(m[r][col]) > fabs(m[p][col])) p = r;
        if (fabs(m[p][col]) < 1e-12) return false;
        if (p != col) for (int k = 0; k < 8; k++) { double t = m[p][k]; m[p][k] = m[col][k]; m[col][k] = t; }
        double d = m[col][col]; for (int k = 0; k < 8; k++) m[col][k] /= d;
        for (int r = 0; r < 4; r++) if (r != col) { double f = m[r][col]; if (f != 0) for (int k = 0; k < 8; k++) m[r][k] -= f * m[col][k]; }
    }
    for (int r = 0; r < 4; r++) for (int k = 0; k < 4; k++) o[r * 4 + k] = m[r][k + 4];
    return true;
}
static void MulD(const double* A, const double* B, double* O)
{
    for (int r = 0; r < 4; r++) for (int k = 0; k < 4; k++) { double v = 0; for (int j = 0; j < 4; j++) v += A[r * 4 + j] * B[j * 4 + k]; O[r * 4 + k] = v; }
}
static bool NearD(const double* A, const double* B, double tol)
{
    for (int r = 0; r < 4; r++)
    {
        double n = fabs(A[r * 4]) + fabs(A[r * 4 + 1]) + fabs(A[r * 4 + 2]) + fabs(A[r * 4 + 3]) + 1e-9;
        for (int k = 0; k < 4; k++) if (fabs(A[r * 4 + k] - B[r * 4 + k]) > tol * n) return false;
    }
    return true;
}
static bool ReadM(int slot, int off, int t, double* M)
{
    const std::vector<float>& v = g_bcnSnap[slot];
    if (off + 16 > (int)v.size()) return false;
    for (int r = 0; r < 4; r++) for (int k = 0; k < 4; k++) M[r * 4 + k] = t ? v[off + k * 4 + r] : v[off + r * 4 + k];
    for (int i = 0; i < 16; i++) if (!std::isfinite(M[i])) return false;
    return true;
}
// apprendimento della posizione della matrice del faro
struct BcnTry { int slot, off, t; double K[16]; int hits; };
static std::vector<BcnTry> g_bcnTry;
static double g_bcnLastCam[16]; static bool g_bcnHasLast = false;
static int g_bcnSlot = -1, g_bcnOff = -1, g_bcnT = 0; static double g_bcnKinv[16];
static long g_bcnAgree = 0, g_bcnCompared = 0, g_bcnUsed = 0, g_bcnDumped = 0;
static void ColFormD(const float* raw, double* C) { float Cf[16]; ColForm(raw, Cf); for (int i = 0; i < 16; i++) C[i] = Cf[i]; }
static void BeaconLearn(const float* refRaw)
{
    double C[16], Ci[16]; ColFormD(refRaw, C);
    if (!Inv4(C, Ci)) return;
    // voce 251: con la trasformazione esatta della nuvola la matrice attesa del faro e' camera x T; la si cerca nella
    // fotografia e, trovata, la camera delle immagini intermedie e' matrice del faro x T^-1, senza approssimazioni
    if (g_bcnExact && g_bcnExactVer != g_cloudTVer) { g_bcnExact = false; g_bcnLocked = false; g_bcnTry.clear(); FT("E;faro_trasformazione_cambiata;si_ricomincia"); }
    if (g_nCloudT > 0 && !g_bcnExact)
    {
        for (int L = 0; L < g_nCloudT && !g_bcnExact; L++)
        {
            double Mexp[16], Ti[16]; MulD(C, g_cloudT[L], Mexp);
            if (!Inv4(g_cloudT[L], Ti)) continue;
            for (int sl = 0; sl < BcnSlots && !g_bcnExact; sl++)
                for (int off = 0; off + 16 <= (int)g_bcnSnap[sl].size() && !g_bcnExact; off += 4)
                    for (int t = 0; t < 2 && !g_bcnExact; t++)
                    {
                        double M[16];
                        if (!ReadM(sl, off, t, M) || !NearD(Mexp, M, 1e-4)) continue;
                        g_bcnSlot = sl; g_bcnOff = off; g_bcnT = t; memcpy(g_bcnKinv, Ti, sizeof(Ti));
                        g_bcnLocked = true; g_bcnExact = true; g_bcnExactVer = g_cloudTVer; g_bcnExactIdx = L; g_bcnTry.clear();
                        FT("E;faro_trasformazione_esatta;nuvola=%d;slot=%d;riga=%d;trasposta=%d", L, sl, off / 4, t);
                    }
        }
    }
    if (g_bcnLocked)
    {   // verifica continua: camera ricavata dal faro contro quella vera
        double M[16], P[16];
        if (ReadM(g_bcnSlot, g_bcnOff, g_bcnT, M))
        {
            MulD(M, g_bcnKinv, P); g_bcnCompared++;
            if (NearD(C, P, 2e-3)) g_bcnAgree++;
            else
            {   // voce 248: quali righe non tornano (x, y, profondita', w), al massimo una volta al secondo
                static double at = 0; double now = QpcMs();
                if (now > at)
                {
                    at = now + 1000; double e[4];
                    for (int r = 0; r < 4; r++)
                    {
                        double nn = fabs(C[r * 4]) + fabs(C[r * 4 + 1]) + fabs(C[r * 4 + 2]) + fabs(C[r * 4 + 3]) + 1e-9, mx = 0;
                        for (int k = 0; k < 4; k++) mx = fmax(mx, fabs(C[r * 4 + k] - P[r * 4 + k]) / nn);
                        e[r] = mx;
                    }
                    FT("E;faro_diverso;errore_righe=%.2e,%.2e,%.2e,%.2e;vera_riga2=%.6g,%.6g,%.6g,%.6g;faro_riga2=%.6g,%.6g,%.6g,%.6g",
                       e[0], e[1], e[2], e[3], C[8], C[9], C[10], C[11], P[8], P[9], P[10], P[11]);
                }
            }
        }
        return;
    }
    // una volta: fotografia e camera vera nel diario, per l'analisi se l'apprendimento non riesce
    if (g_bcnDumped < 2)
    {
        g_bcnDumped++;
        FT("X;faro_camera_vera;%.6g;%.6g;%.6g;%.6g;%.6g;%.6g;%.6g;%.6g;%.6g;%.6g;%.6g;%.6g;%.6g;%.6g;%.6g;%.6g", C[0], C[1], C[2], C[3], C[4], C[5], C[6], C[7], C[8], C[9], C[10], C[11], C[12], C[13], C[14], C[15]);
        for (int sl = 0; sl < BcnSlots; sl++)
        {
            const std::vector<float>& v = g_bcnSnap[sl];
            std::string line;
            char b[32];
            for (size_t i = 0; i < v.size() && i < 160; i++) { _snprintf_s(b, sizeof(b), _TRUNCATE, ";%.6g", v[i]); line += b; }
            FT("X;faro_buffer;slot=%d;float=%zu%s", sl, v.size(), line.c_str());
        }
    }
    bool moved = !g_bcnHasLast || !NearD(C, g_bcnLastCam, 1e-3);
    memcpy(g_bcnLastCam, C, sizeof(C)); g_bcnHasLast = true;
    if (g_bcnTry.empty())
    {
        for (int sl = 0; sl < BcnSlots; sl++)
            for (int off = 0; off + 16 <= (int)g_bcnSnap[sl].size(); off += 4)
                for (int t = 0; t < 2; t++)
                {
                    BcnTry bt = { sl, off, t, {}, 0 }; double M[16];
                    if (!ReadM(sl, off, t, M)) continue;
                    MulD(Ci, M, bt.K);
                    g_bcnTry.push_back(bt);
                }
        return;
    }
    if (!moved) return;   // con la stessa camera ogni posizione sembra coerente: serve un movimento
    for (BcnTry& bt : g_bcnTry)
    {
        double M[16], K[16];
        if (bt.hits < 0 || !ReadM(bt.slot, bt.off, bt.t, M)) { bt.hits = -1; continue; }
        MulD(Ci, M, K);
        if (NearD(bt.K, K, 2e-3)) bt.hits++; else bt.hits = -1;
        if (bt.hits >= 3)
        {
            double Ki[16];
            if (Inv4(bt.K, Ki))
            {
                g_bcnSlot = bt.slot; g_bcnOff = bt.off; g_bcnT = bt.t; memcpy(g_bcnKinv, Ki, sizeof(Ki)); g_bcnLocked = true;
                FT("E;faro_appreso;slot=%d;riga=%d;trasposta=%d", bt.slot, bt.off / 4, bt.t);
                g_bcnTry.clear();
                return;
            }
        }
    }
    bool any = false; for (BcnTry& bt : g_bcnTry) if (bt.hits >= 0) { any = true; break; }
    if (!any) { FT("E;faro_nessuna_posizione_coerente;si_ricomincia"); g_bcnTry.clear(); }
}
// camera di un'immagine intermedia dal faro, nel formato del buffer di Revit (matrice intera nella prima, identita' nella seconda)
static bool BeaconCamera(float* raw)
{
    if (!g_bcnLocked) return false;
    double M[16], C[16];
    if (!ReadM(g_bcnSlot, g_bcnOff, g_bcnT, M)) return false;
    MulD(M, g_bcnKinv, C);
    for (int r = 0; r < 4; r++) for (int k = 0; k < 4; k++) { float v = (float)C[r * 4 + k]; if (g_mT & 1) raw[r * 4 + k] = v; else raw[k * 4 + r] = v; }
    for (int i = 0; i < 16; i++) raw[16 + i] = (i % 5 == 0) ? 1.0f : 0.0f;
    return true;
}
// voce 257 (diario del 21/11 20:54): la matrice del faro ha x, y e w esatte ma la riga della profondita' e' quella con cui
// Revit disegna le nuvole, non quella del modello: stessa direzione, scala circa 2000 volte piu' piccola e tutti i punti
// a circa 0,5 (righe «E;faro_diverso»: errore 0,07-0,19 sulla riga 2, zero sulle altre). La nuvola disegnata cosi' finiva
// tutta davanti al modello o tutta dietro, a seconda dell'immagine: il modello passava sopra e sotto. La riga giusta e'
// quella della camera del modello della stessa immagine: tra le matrici scritte da Revit in quell'immagine si cerca quella
// con x, y e w uguali al faro (le matrici degli elementi dei collegamenti hanno x e y diverse) e si usa quella intera.
// Se non c'e' (vista ortogonale), si stima la riga dalla camera precedente: stessa scala, direzione del faro, e il punto
// al centro dello schermo resta alla stessa profondita'.
static long g_bcnZFound = 0, g_bcnZEst = 0, g_bcnZNone = 0, g_bcnZRej = 0;
// voce 282 (pianta e sezione del 24/11 16:58, la nuvola andava sopra e sotto): nelle viste ortogonali la riga del faro
// e' quella «da nuvola» (0, 0, -5e-7, 0,5: tutti i punti a 0,4996), nessuna candidata e la stima non partiva (il faro ha
// w diverso da 1). La nuvola finiva tutta davanti, con la copia del faro quasi vuota, alternata alle immagini con la
// profondita' del fotogramma completo: sopra e sotto. Ora, se la vista e' ortogonale e la direzione non cambia (pianta,
// sezione, prospetto, pan e zoom), si prende la riga della profondita' dell'ultimo fotogramma completo e la sua
// profondita' copiata: x e y dal faro, profondita' confrontata alla pari con quella del modello. Solo visibilita'.
static long g_bcnZFlat = 0;
static bool g_bcnFlatNow = false, g_chainFlat = false;
static bool g_noFlatFix = false;
extern "C" __declspec(dllexport) void PdSetFlatBeaconFix(int on) { g_noFlatFix = on == 0; }
static const char* g_bcnZKind = "";
static int g_bcnZLogs = 0; static long g_bcnZLogGen = -1;
// voce 258 (diario e video del 23/11 09:44:21): con la 257 la riga e' sempre «trovata», ma nelle immagini col faro i pixel
// della nuvola sono 1,18 per triangolo (mediana) contro 0,62 dei fotogrammi completi, cioe' quanti se ne hanno senza
// profondita' (1,2-1,3): la nuvola passava sempre davanti al modello (nel video i punti dentro l'edificio sopra il tetto).
// Nella 256, con la riga del faro, la stessa copia di profondita' copriva la nuvola a tratti: la copia contiene il modello,
// e' la riga scelta a essere sbagliata. Tra le matrici con x, y e w del faro si prendeva la piu' scritta: con ogni
// probabilita' quella di un passaggio disegnato sopra il modello (linee, contorni) con la profondita' schiacciata. Ora si
// prende quella con la riga della profondita' piu' vicina a quella dell'ultima camera vera (scala entro 1/2-2 volte), e
// le prime immagini di ogni vista scrivono nel diario tutte le candidate (righe X;faro_z).
static void PutRaw(const double* C, float* raw)
{
    for (int r = 0; r < 4; r++) for (int k = 0; k < 4; k++) { float v = (float)C[r * 4 + k]; if (g_mT & 1) raw[r * 4 + k] = v; else raw[k * 4 + r] = v; }
    for (int i = 0; i < 16; i++) raw[16 + i] = (i % 5 == 0) ? 1.0f : 0.0f;
}
static void BeaconFixDepth(float* raw, int nCand, const float* prevRaw)
{
    double B[16]; ColFormD(raw, B);
    auto rowsXYW = [&](const double* M, double tol) -> bool {
        for (int r = 0; r < 4; r++)
        {
            if (r == 2) continue;
            double nn = fabs(B[r * 4]) + fabs(B[r * 4 + 1]) + fabs(B[r * 4 + 2]) + fabs(B[r * 4 + 3]) + 1e-12;
            for (int k = 0; k < 4; k++) if (!(fabs(B[r * 4 + k] - M[r * 4 + k]) <= tol * nn)) return false;
        }
        return true;
    };
    double P[16], Pi[16]; ColFormD(prevRaw, P);
    if (g_bcnZLogGen != g_viewGen) { g_bcnZLogGen = g_viewGen; g_bcnZLogs = 0; }
    bool logZ = g_bcnZLogs < 30; if (logZ) g_bcnZLogs++;
    if (logZ) FT("X;faro_z;precedente;%g;%g;%g;%g;faro;%g;%g;%g;%g;candidate=%d", P[8], P[9], P[10], P[11], B[8], B[9], B[10], B[11], nCand);
    // voce 260 (diario del 23/11 10:03): nelle immagini intermedie la riga del faro e' quasi sempre gia' quella del modello
    // (uguale alla candidata); quella «da nuvola» (scala ~1/2000) compare solo a tratti. In rotazione veloce la 258
    // scartava la riga giusta (direzione cambiata piu' del previsto) e passava alla stima, peggio. Ora: riga del faro
    // tenuta se la sua scala e' entro 1/5-5 volte quella della camera precedente; altrimenti la candidata con x, y, w
    // uguali e scala in quell'intervallo (la piu' scritta); altrimenti la stima.
    auto zScale = [](const double* M) { return sqrt(M[8] * M[8] + M[9] * M[9] + M[10] * M[10]); };
    double pS = zScale(P), bS = zScale(B);
    if (pS > 0 && bS > 0.2 * pS && bS < 5 * pS)
    {
        if (logZ) FT("X;faro_z;riga_del_faro_tenuta;scala=%.3f", bS / pS);
        g_bcnZNone++; g_bcnZKind = "del_faro"; return;
    }
    int best = -1, matched = 0; double bestD = 1e30;
    for (int i = 0; i < nCand && i < 16; i++)
    {
        double M[16]; ColFormD(g_cand[i].m, M);
        double z = fabs(M[8]) + fabs(M[9]) + fabs(M[10]);
        if (!(z > 0) || !rowsXYW(M, 1e-5)) continue;
        matched++;
        double mS = zScale(M);
        double d = pS > 0 && mS > 0.2 * pS && mS < 5 * pS ? 1.0 / (1 + g_cand[i].n) : 1e30;
        if (logZ) FT("X;faro_z;candidata=%d;scritture=%d;%g;%g;%g;%g;distanza=%g", i, g_cand[i].n, M[8], M[9], M[10], M[11], d);
        if (d < bestD) { bestD = d; best = i; }
    }
    if (best < 0 && matched > 0) g_bcnZRej++;
    if (logZ) FT("X;faro_z;scelta=%d;con_xyw_uguali=%d", best, matched);
    if (best >= 0)
    {
        double M[16]; ColFormD(g_cand[best].m, M); B[8] = M[8]; B[9] = M[9]; B[10] = M[10]; B[11] = M[11];
        PutRaw(B, raw); g_bcnZFound++; g_bcnZKind = "trovata"; return;
    }
    bool ortho = fabs(B[12]) + fabs(B[13]) + fabs(B[14]) < 1e-9 && fabs(B[15] - 1) < 1e-6 && fabs(P[12]) + fabs(P[13]) + fabs(P[14]) < 1e-9;
    double bl = sqrt(B[8] * B[8] + B[9] * B[9] + B[10] * B[10]), pl = sqrt(P[8] * P[8] + P[9] * P[9] + P[10] * P[10]);
    if (ortho && bl > 0 && pl > 0 && Inv4(P, Pi))
    {   // punto al centro dello schermo, a meta' profondita', con la camera precedente
        double c[3]; for (int i = 0; i < 3; i++) c[i] = Pi[i * 4 + 2] * 0.5 + Pi[i * 4 + 3];
        double zPrev = P[8] * c[0] + P[9] * c[1] + P[10] * c[2] + P[11];
        double f = pl / bl;
        B[8] *= f; B[9] *= f; B[10] *= f;
        B[11] = zPrev - (B[8] * c[0] + B[9] * c[1] + B[10] * c[2]);
        PutRaw(B, raw); g_bcnZEst++; g_bcnZKind = "stimata"; return;
    }
    g_bcnZNone++; g_bcnZKind = "del_faro";
}
// voce 282: vista ortogonale con la stessa direzione dell'ultimo fotogramma completo: riga della profondita' di quel fotogramma
static bool BeaconFlatDepth(float* raw)
{
    if (g_noFlatFix || g_refGen != g_viewGen) return false;
    double B[16], R[16]; ColFormD(raw, B); ColFormD(g_refRaw, R);
    auto ortho = [](const double* M) { return fabs(M[15]) > 0 && fabs(M[12]) + fabs(M[13]) + fabs(M[14]) < 1e-6 * fabs(M[15]); };
    if (!ortho(B) || !ortho(R)) return false;
    for (int r = 0; r < 2; r++)
    {
        double b = sqrt(B[r * 4] * B[r * 4] + B[r * 4 + 1] * B[r * 4 + 1] + B[r * 4 + 2] * B[r * 4 + 2]);
        double q = sqrt(R[r * 4] * R[r * 4] + R[r * 4 + 1] * R[r * 4 + 1] + R[r * 4 + 2] * R[r * 4 + 2]);
        if (!(b > 0) || !(q > 0)) return false;
        double d = (B[r * 4] * R[r * 4] + B[r * 4 + 1] * R[r * 4 + 1] + B[r * 4 + 2] * R[r * 4 + 2]) / (b * q);
        if (d < 0.99999) return false;   // direzione cambiata (orbita in 3D ortogonale): no
    }
    double f = B[15] / R[15];
    for (int k = 0; k < 4; k++) B[8 + k] = R[8 + k] * f;
    PutRaw(B, raw); g_bcnZFlat++; g_bcnZKind = "pianta";
    return true;
}
static void BeaconPresent(bool full)
{
    if (g_bcnPts == 0 && g_bcnOther == 0) return;
    FT("F;faro;fotogramma=%s;disegni_a_punti=%ld;altri_disegni_compatibili=%ld", full ? "completo" : "intermedio", g_bcnPts, g_bcnOther);
    if (g_bcnPts > 0) { if (full) g_bcnFull++; else g_bcnInt++; }
    g_bcnPts = g_bcnOther = 0;
}
static void FallbackAtPresent(IDXGISwapChain* s, const DXGI_PRESENT_PARAMETERS* pp)
{
    // voce 264: la copia della profondita' della finestra vale solo per questa immagine
    // voce 267 (diario del 23/11 19:30): con il controllo per immagine (264, 266) la copia della finestra risultava sempre
    // vecchia al momento di usarla, anche se Revit l'aveva appena fatta (l'ordine tra il Present e il disegno
    // dell'immagine seguente non e' quello atteso). Si usa l'ultima copia fatta da Revit, se e' successiva all'ultimo
    // fotogramma completo; nel diario X;finestra_eta = Present trascorsi dalla copia.
    bool ui = g_uiRtv != nullptr;   // voce 268: chiamata prima dell'interfaccia, non dal Present
    if (!ui && g_uiDone && s == g_fbSwap)
    {   // nuvola gia' disegnata prima dell'interfaccia in questa immagine: al Present solo la fine della sequenza
        g_uiDone = false; g_uiArm = true; g_presentNo++;
        TrPresent(s, g_uiFull, g_fbDsv);
        return;
    }
    struct Rearm { bool on; ~Rearm() { if (on) g_uiArm = true; } } rearm{ !ui && s == g_fbSwap };   // voce 270: l'immagine seguente
    struct OurDraw { ~OurDraw() { g_ourDraw = false; } } ourDraw; g_ourDraw = true;
    struct UiDone { bool on; ~UiDone() { if (on) { g_uiDone = true; g_uiDraws++; } } } uiDone{ ui };
    struct WinNext { bool on; ~WinNext() { if (on) g_presentNo++; } } winNext{ !ui && (s == g_fbSwap || g_fbSwap == nullptr) };
    if (!ui) g_presentDraws++;
    if (s == g_fbSwap || g_fbSwap == nullptr)
    {
        ID3D11Texture2D* bb = nullptr;
        if (SUCCEEDED(s->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&bb)) && bb) { bb->GetDesc(&g_bbDesc); g_bbRes = bb; bb->Release(); }
    }
    BeaconPresent(g_drawnThisFrame);
    bool bcnSnap = g_bcnSnapOk; g_bcnSnapOk = false;   // voce 247: fotografia del faro di questa immagine
    if (g_bcnDzArm && g_bcnDzRef && g_realDev)
    {   // voce 250: Revit non ha lasciato quel buffer prima del Present: si copia ora
        ID3D11DeviceContext* cx = nullptr; g_realDev->GetImmediateContext(&cx);
        if (cx) { BeaconDepthCopyRes(cx, g_bcnDzRef); cx->Release(); }
        if (g_trRec) TrPut(" COPIA-FARO-AL-PRESENT");
    }
    BeaconDzDisarm();
    bool bcnDepth = g_bcnDepthOk; g_bcnDepthOk = false;   // voce 248
    bool sideDepth = g_sideOk; g_sideOk = false;          // voce 271
    if (g_trRec && g_drawnThisFrame && g_keepThisFrame && g_keepOk && g_realDev)
    {
        ID3D11DeviceContext* c = nullptr; g_realDev->GetImmediateContext(&c);
        if (c) { TrDepth(c, g_keepDsv, "profondita' copiata all'uscita dalla scena"); c->Release(); }
    }
    if (g_trRec && bcnDepth && g_bcnDsv && g_realDev)
    {   // voce 260: cosa c'e' nella copia della profondita' dell'immagine col faro
        ID3D11DeviceContext* c = nullptr; g_realDev->GetImmediateContext(&c);
        if (c) { TrDepth(c, g_bcnDsv, "copia del faro"); c->Release(); }
    }
    if (ui) g_uiFull = g_drawnThisFrame; else TrPresent(s, g_drawnThisFrame, g_fbDsv);
    if (!g_realDev || g_fbVp.Width < 1) return;
    DXGI_SWAP_CHAIN_DESC sd = {};
    if (FAILED(s->GetDesc(&sd))) return;
    if (sd.BufferDesc.Width != (UINT)g_fbVp.Width || sd.BufferDesc.Height != (UINT)g_fbVp.Height) { g_fbOther++; FT("P;altra_finestra=%ux%u", sd.BufferDesc.Width, sd.BufferDesc.Height); return; }
    // voce 229: rettangolo ridisegnato in questo fotogramma (forbici al segnale, altrimenti rettangoli di Present1)
    const D3D11_RECT* sc = nullptr; D3D11_RECT dirty = {};
    // voce 230 (video del 20/11 14:26): fuori dal rettangolo Revit ricompone l'immagine dal modello senza nuvola (con la
    // trasparenza la nuvola sta solo nell'immagine della finestra), quindi limitarsi al rettangolo lasciava il resto bianco.
    // Se c'e' la profondita' intera del fotogramma precedente con la stessa camera, la nuvola si ridisegna ovunque con
    // quella; il rettangolo serve solo quando quella copia non c'e'.
    // (una copia presa in questo fotogramma parziale e' vuota fuori dal rettangolo: allora si resta nel rettangolo)
    // voce 237 (video del 20/11 19:40, VistaIniziale): con la profondita' intera di prima si disegna su tutta la finestra
    // anche se Revit passa a Present1 i rettangoli modificati; prima si ripiegava su quei rettangoli e la nuvola restava
    // solo nel riquadro attorno all'elemento selezionato (registro: «tenuta_precedente;parziale=1;forbici=1»).
    bool fullOk = g_partial && (g_keptOld || (!g_keepThisFrame && KeepStillGood()));
    if (g_drawnThisFrame && g_partial && !fullOk) sc = &g_partRect;
    else if (!fullOk && pp && pp->DirtyRectsCount > 0 && pp->pDirtyRects)
    {
        RECT u = pp->pDirtyRects[0];
        for (UINT k = 1; k < pp->DirtyRectsCount; k++)
        {
            const RECT& q = pp->pDirtyRects[k];
            if (q.left < u.left) u.left = q.left; if (q.top < u.top) u.top = q.top;
            if (q.right > u.right) u.right = q.right; if (q.bottom > u.bottom) u.bottom = q.bottom;
        }
        D3D11_RECT ur = { u.left, u.top, u.right, u.bottom };
        if (PartialRect(ur, g_fbVp, dirty)) { sc = &dirty; g_partDirty++; g_partLast = dirty; }
    }
    {   // solo per il registro: forbici attive al Present (ultimo disegno di Revit nella finestra)
        ID3D11DeviceContext* c = nullptr; g_realDev->GetImmediateContext(&c);
        if (c)
        {
            ID3D11RasterizerState* rrs = nullptr; c->RSGetState(&rrs);
            D3D11_RASTERIZER_DESC rrd = {}; if (rrs) { rrs->GetDesc(&rrd); rrs->Release(); }
            if (rrd.ScissorEnable) { UINT ns = 1; D3D11_RECT sr = {}, o = {}; c->RSGetScissorRects(&ns, &sr); if (ns > 0 && PartialRect(sr, g_fbVp, o)) g_partScPresent++; }
            c->Release();
        }
    }
    if (g_drawnThisFrame)
    {
        g_fbNormal++; g_drawnThisFrame = false; g_fbSwap = s; LearnSearch(); g_winAfterFull = g_presentNo;   // voce 267
        int b = BestCand();
        if (b >= 0 && g_hasRef) { g_fbCompared++; if (memcmp(g_cand[b].m, g_refRaw, (g_mOff2 >= 0 ? 32 : 16) * 4) == 0) g_fbAgree++; }
        if (bcnSnap && g_hasRef && g_refGen == g_viewGen && !g_partial) BeaconLearn(g_refRaw);   // voce 247
        g_bcnDepthChain = false;
        g_nCand = 0; g_cmN = 0;
        // voce 217: con la trasparenza la nuvola del fotogramma completo si disegna qui, sopra il modello finito, con la
        // camera vera di questo fotogramma (al segnale si e' solo presa la matrice)
        // voce 227: profondita' copiata quando Revit ha lasciato il buffer della scena (nella VistaIniziale al Present
        // quel buffer e' gia' stato svuotato); se la copia non c'e' stata, si copia ora come nella 222
        if (!(g_keepThisFrame && g_keepOk) && !KeepStillGood() && g_shaderOn && !g_ready.empty()) KeepDepth();
        if (g_partial && KeepStillGood()) g_keptOld = true;
        ID3D11DepthStencilView* dz = g_keepOk ? g_keepDsv : g_fbDsv;
        bool tr = Transparent(), drew = false;
        g_drawSrc = 1; g_noDepth = QpcMs() < g_depthOffUntil;
        if (g_thinMode && dz && g_keepOk && !g_partial)
        {   // voce 271: copertura della copia della scena nell'ultimo fotogramma completo di questa vista
            ID3D11DeviceContext* cx = nullptr; g_realDev->GetImmediateContext(&cx);
            double cov, mn, mx, me;
            if (cx && DepthRead(cx, dz, cov, mn, mx, me) && cov > 0) { g_fullCov = cov; g_fullCovGen = g_viewGen; }
            if (cx) cx->Release();
        }
        if (dz && g_keepOk && !g_partial && g_hasRef && g_refGen == g_viewGen && g_meshTris > 0 && (g_seqOn || g_meshTol))
        {   // voce 275 (diario), 276 (scarto di Revit): nelle sequenze sempre, altrimenti ogni 0,7 s o se cambia lo zoom
            double Cz[16]; ColFormD(g_refRaw, Cz);
            double az = sqrt(Cz[8] * Cz[8] + Cz[9] * Cz[9] + Cz[10] * Cz[10]);
            bool due = g_revOffGen != g_viewGen || QpcMs() - g_revOffAt > 700 || (g_revOffA > 0 && fabs(az / g_revOffA - 1) > 0.05);
            if (g_seqOn || due)
            {
                ID3D11DeviceContext* cx = nullptr; g_realDev->GetImmediateContext(&cx);
                if (cx) { MeshVsRevit(cx, dz, g_refRaw, g_seqOn); cx->Release(); }
            }
        }
        if (g_seqOn && dz && sideDepth && g_sideDsv)
        {   // voce 271
            ID3D11DeviceContext* cx = nullptr; g_realDev->GetImmediateContext(&cx);
            if (cx) { DepthPairLog(cx, dz, g_sideDsv, "completo_altra"); cx->Release(); }
        }
        if (g_seqOn && dz && g_hasRef)
        {   // voce 262: stesso confronto per i fotogrammi completi
            ID3D11DeviceContext* cx = nullptr; g_realDev->GetImmediateContext(&cx);
            double cov, mn, mx, me;
            if (cx && DepthRead(cx, dz, cov, mn, mx, me))
                FT("X;completo_copia;coperto=%.0f;z=%.4f-%.4f;media=%.4f;centro_nuvola_z=%.4f", cov, mn, mx, me, CloudCenterZ(g_refRaw, false));
            if (cx && g_winOk && g_winDsv && DepthRead(cx, g_winDsv, cov, mn, mx, me))
                FT("X;finestra_copia;coperto=%.0f;z=%.4f-%.4f;media=%.4f;disegni=%lld;completo=1", cov, mn, mx, me, g_winDraws);
            if (cx) cx->Release();
        }
        if (tr && g_shaderOn && !g_ready.empty() && g_hasRef && g_refGen == g_viewGen && DrawAtPresent(s, g_refRaw, dz, sc)) { g_fbAlpha++; drew = true; }
        // voce 259: punti trasparenti (link) sopra il modello finito, con la camera vera e la profondita' copiata
        else if (!tr && g_ptAlpha && g_shaderOn && !g_ready.empty() && g_hasRef && g_refGen == g_viewGen && DrawAtPresent(s, g_refRaw, dz, sc, false, 2)) drew = true;
        g_noDepth = false;
        FT("P;completo;trasparente=%d;disegnato_al_present=%d;opacita=%.2f;profondita=%s;parziale=%d;forbici=%d;blocchi=%zu;prima_interfaccia=%d",
           tr ? 1 : 0, drew ? 1 : 0, g_alpha, QpcMs() < g_depthOffUntil ? "ignorata" : (g_keptOld ? "tenuta_precedente" : (g_keepOk ? "copia" : "revit")), g_partial ? 1 : 0, sc ? 1 : 0, g_ready.size(), ui ? 1 : 0);
        g_keepThisFrame = false; g_dzArm = false; g_partial = false; g_keptOld = false;
        return;
    }
    if (s != g_fbSwap) { g_fbOther++; FT("P;interrotto;salto=altra_swapchain"); return; }   // voce 215: solo la finestra dell'ultimo fotogramma completo
    g_learn.clear(); g_lmN = 0;   // fotogramma interrotto: niente matrice vera con cui confrontare
    g_rjProj = g_rjAsp = g_rjOrtho = g_rjScale = g_rjFocus = g_rjOther = 0; g_rjKmin = 1e9f; g_rjKmax = 0; g_rjShift = 0;
    int nCandHere = g_nCand;
    // voce 247: camera dal faro, se Revit l'ha disegnato in questa immagine
    float bcnRaw[32]; bool bcn = bcnSnap && g_hasRef && g_refGen == g_viewGen && !(g_flatView && !g_flatBeacon) && BeaconCamera(bcnRaw);
    // voce 249 (video del 21/11 19:35:40): passando col mouse sul modello (camera ferma) la nuvola spariva sopra l'edificio.
    // La camera ricavata dal faro ha x e y giuste ma la profondita' solo approssimata; diventata camera «precedente», le
    // immagini successive confrontavano la nuvola con la profondita' vera del modello e la perdevano (pixel visibili da
    // 9,8 a 6,1 milioni). Ora: se x, y e w del faro coincidono con la camera precedente (camera ferma) si usa quella
    // esatta; in movimento la camera del faro si usa ma la profondita' si ignora, finche' arriva un fotogramma completo.
    bool bcnStill = false;
    if (bcn)
    {
        auto sameXY = [&](const float* raw) -> bool {
            double B[16], R[16]; ColFormD(bcnRaw, B); ColFormD(raw, R);
            for (int r = 0; r < 4; r++)
            {
                if (r == 2) continue;
                double nn = fabs(R[r * 4]) + fabs(R[r * 4 + 1]) + fabs(R[r * 4 + 2]) + fabs(R[r * 4 + 3]) + 1e-9;
                for (int k = 0; k < 4; k++) if (fabs(B[r * 4 + k] - R[r * 4 + k]) > 1e-3 * nn) return false;
            }
            return true;
        };
        if (sameXY(g_refRaw))
        {   // camera dell'ultimo fotogramma completo, esatta anche nella profondita'
            bcnStill = true; memcpy(g_chainRaw, g_refRaw, sizeof(g_chainRaw)); g_chainGen = g_viewGen; g_chainFromBeacon = false;
        }
        else if (g_chainGen == g_viewGen && sameXY(g_chainRaw)) bcnStill = true;
        if (bcnStill) { bcn = false; g_bcnStill++; }
    }
    if (bcn) g_bcnUsed++;
    if (bcn && !g_seqOn && (g_seqGen != g_viewGen || QpcMs() - g_seqAt > 15000))
    {   // voce 260: da qui le prossime 12 immagini
        g_seqGen = g_viewGen; g_seqAt = QpcMs(); g_seqOn = true; g_seqLeft = 12; g_trArm = true; g_trDone = false;
    }
    g_bcnZKind = "";
    g_bcnFlatNow = bcn && BeaconFlatDepth(bcnRaw);   // voce 282: pianta, sezione, prospetto
    if (bcn && !g_bcnFlatNow) BeaconFixDepth(bcnRaw, nCandHere, g_chainGen == g_viewGen ? g_chainRaw : g_refRaw);   // voce 257
    // voce 256 (diario del 21/11 20:18, faro acceso): nelle immagini senza faro si cercava ancora la camera tra le matrici
    // scritte (212-216, 245). Con i collegamenti quelle matrici sono spesso degli elementi dei collegamenti (camera x
    // trasformazione del collegamento) e passano i controlli: la nuvola si disegnava spostata e il modello le passava
    // sopra o sotto (in quelle immagini 0,9-1,2 pixel per triangolo contro 0,35 dei fotogrammi completi). Con il faro
    // attivo, un'immagine senza faro usa la camera precedente: se Revit ha ridisegnato il modello con una camera nuova
    // il faro c'e' (28 immagini su 29 nella prova delle 19:16).
    bool noGuess = g_bcnLocked && !bcn && !(g_flatView && !g_flatBeacon);   // voce 286: in pianta come prima del faro
    if (noGuess && !bcnStill && g_chainGen == g_viewGen)
    {   // voce 279 (24/11 16:23, senza registrazione: la nuvola sfarfalla in orbita con i link, l'RCP no). Immagini senza
        // faro: la nuvola usa la camera precedente. Se in quell'immagine Revit ha disegnato qualcosa con una camera nuova
        // (per esempio solo il link) la nuvola resta indietro di un'immagine e salta. Si contano le matrici scritte da
        // Revit (anche tolta la trasformazione di ogni collegamento) con x, y, w uguali alla camera usata o diverse ma con
        // la stessa scala (camera spostata).
        double P0[16]; ColFormD(g_chainRaw, P0);
        auto nrm = [](const double* M, int r) { return sqrt(M[r * 4] * M[r * 4] + M[r * 4 + 1] * M[r * 4 + 1] + M[r * 4 + 2] * M[r * 4 + 2]); };
        auto cls = [&](const float* raw) -> int {   // 1 uguale, 2 camera spostata, 0 altro
            double R[16]; ColFormD(raw, R);
            bool same = true;
            for (int r = 0; r < 4 && same; r++)
            {
                if (r == 2) continue;
                double nn = fabs(P0[r * 4]) + fabs(P0[r * 4 + 1]) + fabs(P0[r * 4 + 2]) + fabs(P0[r * 4 + 3]) + 1e-9;
                for (int k = 0; k < 4; k++) if (fabs(R[r * 4 + k] - P0[r * 4 + k]) > 1e-3 * nn) { same = false; break; }
            }
            if (same) return 1;
            for (int r = 0; r < 2; r++) { double a = nrm(P0, r), b = nrm(R, r); if (!(a > 0) || fabs(b / a - 1) > 0.02) return 0; }
            return 2;
        };
        int nSame = 0, nMoved = 0, nOther = 0;
        for (int i = 0; i < nCandHere && i < 16; i++)
        {
            int c = cls(g_cand[i].m);
            for (int L = 0; L < g_nLinks && c != 1; L++) { float u[32]; if (UnLink(g_cand[i].m, L, u)) { int c2 = cls(u); if (c2 == 1 || (c2 == 2 && c == 0)) c = c2; } }
            if (c == 1) nSame++; else if (c == 2) nMoved++; else nOther++;
        }
        g_noBcnImgs++; if (nMoved > 0 && nSame == 0) g_noBcnMoved++;
        FT("X;senza_faro;candidate=%d;uguali=%d;spostate=%d;altre=%d", nCandHere, nSame, nMoved, nOther);
    }
    int host = !bcn && !bcnStill && !noGuess && g_hasRef ? HostByLinks() : -1;
    if (host >= 0) g_hostHits++;
    int best = (bcn || bcnStill || noGuess) ? -1 : (host >= 0 ? host : (g_hasRef ? BestValidCand() : -1));
    if (!bcn && !bcnStill && !noGuess && best < 0 && g_hasRef && nCandHere > 0) DumpCands();
    // voce 241: le camere dei fotogrammi interrotti hanno appena messo la nuvola fuori dalla finestra: per 1 s si usa
    // l'ultima camera di un fotogramma completo, e quella sbagliata non diventa la camera «precedente»
    bool curBlocked = !bcn && best >= 0 && host < 0 && QpcMs() < g_badCurUntil;
    if (curBlocked) { best = -1; if (g_refGen == g_viewGen) { memcpy(g_chainRaw, g_refRaw, sizeof(g_chainRaw)); g_chainGen = g_viewGen; g_chainFromBeacon = false; } }
    const float* camNow = bcn ? bcnRaw : (best >= 0 ? g_cand[best].m : nullptr);
    float camBuf[32];
    if (camNow) { memcpy(camBuf, camNow, sizeof(camBuf)); camNow = camBuf; if (g_refGen == g_viewGen) { memcpy(g_chainRaw, camBuf, sizeof(camBuf)); g_chainGen = g_viewGen; g_chainFromBeacon = bcn; g_chainFlat = bcn && g_bcnFlatNow; } }
    g_nCand = 0; g_cmN = 0;
    if (!g_shaderOn || g_ready.empty() || !g_fbDsv) { FT("P;interrotto;salto=%s", !g_shaderOn ? "spento" : (g_ready.empty() ? "elenco_vuoto" : "niente_profondita")); return; }
    if (!g_hasRef || g_refGen != g_viewGen) { g_fbNoMat++; FT("P;interrotto;salto=camera_di_un'altra_vista"); return; }
    // voce 212: camera del modello di questo fotogramma se trovata, altrimenti l'ultima usata
    g_drawSrc = camNow ? 2 : 3; g_noDepth = QpcMs() < g_depthOffUntil;
    // voce 248: con la camera del faro, la profondita' della stessa immagine; senza, nessuna (quella vecchia apre buchi)
    ID3D11DepthStencilView* dzUse = g_keepOk ? g_keepDsv : g_fbDsv;
    if (g_postNow && g_fbDsv) dzUse = g_fbDsv;   // voce 294: dentro la scena, la profondita' che Revit sta usando
    // voce 250: con la camera del faro si usa la profondita' della stessa immagine (copiata al disegno del faro) e la
    // nuvola si avvicina di 0,3 ft (9 cm) verso chi guarda: la profondita' del faro e' approssimata di qualche centimetro e
    // i punti che stanno sui muri, confrontati alla pari, finivano dietro (nuvola sparita sull'edificio, 249). Lo stesso
    // nelle immagini senza disegni che riusano quella camera. Senza copia, la profondita' si ignora.
    bool useBcnCam = bcn || (!camNow && g_chainFromBeacon);
    // voce 282: in pianta e sezione la profondita' e' quella del fotogramma completo, come nelle immagini senza faro
    if (useBcnCam && (bcn ? g_bcnFlatNow : g_chainFlat)) useBcnCam = false;
    if (bcn) g_bcnDepthChain = bcnDepth;
    if (useBcnCam)
    {
        if (bcn && ui && g_winModelDsv)
        {   // voce 268: profondita' del modello disegnato nella finestra in questa immagine, senza scriverla
            dzUse = g_winModelDsv; g_depthNoWrite = true; g_winUsed++;
        }
        else if (bcn && g_winOk && g_winDsv && g_winPresent >= g_winAfterFull)
        {   // voce 264, 267: profondita' del modello disegnato nella finestra (ultima copia di Revit)
            dzUse = g_winDsv; g_winUsed++;
            if (g_seqOn) FT("X;finestra_eta;present=%ld;disegni=%lld", g_presentNo - g_winPresent, g_winDraws);
        }
        else if (g_bcnDepthChain && g_bcnDsv)
        {
            dzUse = g_bcnDsv;
            if (g_meshOnSerial != g_bcnCopySerial && g_realDev)
            {   // voce 272: mesh del modello nella copia, una volta per copia
                ID3D11DeviceContext* cx = nullptr; g_realDev->GetImmediateContext(&cx);
                if (cx)
                {
                    double c0 = -1, c1 = -1, mn, mx, me;
                    if (g_seqOn) DepthRead(cx, g_bcnDsv, c0, mn, mx, me);
                    bool md = MeshDepth(cx, g_bcnDsv, camNow ? camNow : g_chainRaw);
                    if (md) g_meshOnSerial = g_bcnCopySerial;
                    if (g_seqOn && md) { DepthRead(cx, g_bcnDsv, c1, mn, mx, me); FT("X;mesh_profondita;prima=%.0f;dopo=%.0f;triangoli=%d", c0, c1, g_meshTris); }
                    cx->Release();
                }
            }
            double Cb[16]; ColFormD(camNow ? camNow : g_chainRaw, Cb);
            double a = sqrt(Cb[8] * Cb[8] + Cb[9] * Cb[9] + Cb[10] * Cb[10]);
            g_depthBias = (float)((g_bcnExact ? 0.0 : 0.3) * a);   // voce 251: con la trasformazione esatta nessuno spostamento
            if (g_bcnZKind[0] == 's') g_depthBias = (float)(0.3 * a);   // voce 257: profondita' stimata, 9 cm verso chi guarda
            // voce 274: niente piu' spostamento della nuvola per la mesh (273); la tolleranza sta nella mesh (MeshDepth)
        }
        else { g_noDepth = true; g_bcnDepthMissing++; }
    }
    // voce 271: copia della scena molto piu' scarsa di quella dell'ultimo fotogramma completo
    float alphaWas = -1; double thinCov = -1;
    if (g_thinMode && useBcnCam && dzUse == g_bcnDsv && !g_noDepth && g_fullCov > 0 && g_fullCovGen == g_viewGen && g_realDev)
    {
        ID3D11DeviceContext* cx = nullptr; g_realDev->GetImmediateContext(&cx);
        double cov, mn, mx, me;
        if (cx && DepthRead(cx, dzUse, cov, mn, mx, me)) thinCov = cov;
        if (cx) cx->Release();
        if (thinCov >= 0 && thinCov < 0.5 * g_fullCov)
        {
            if (g_thinMode == 1)
            {
                g_thinSkip++; g_depthBias = 0;
                FT("P;interrotto;salto=profondita_scarsa;coperto=%.0f;completo=%.0f;prima_interfaccia=%d", thinCov, g_fullCov, ui ? 1 : 0);
                return;
            }
            g_thinGhost++; g_noDepth = true; alphaWas = g_alpha; if (g_alpha > 0.35f) g_alpha = 0.35f;
            FT("X;profondita_scarsa;coperto=%.0f;completo=%.0f;nuvola=trasparente", thinCov, g_fullCov);
        }
    }
    bool depthOff = g_noDepth;
    if (g_seqOn && useBcnCam && dzUse == g_bcnDsv && sideDepth && g_sideDsv && g_realDev)
    {   // voce 271: l'altro buffer della scena contiene il resto del modello?
        ID3D11DeviceContext* cx = nullptr; g_realDev->GetImmediateContext(&cx);
        if (cx) { DepthPairLog(cx, dzUse, g_sideDsv, bcn ? "faro_altra" : "faro_altra_precedente"); cx->Release(); }
    }
    if (g_seqOn && useBcnCam && (dzUse == g_bcnDsv || dzUse == g_winDsv) && g_realDev)
    {   // voce 262: cosa c'e' nella copia e dove cade la nuvola con questa camera
        ID3D11DeviceContext* cx = nullptr; g_realDev->GetImmediateContext(&cx);
        double cov, mn, mx, me;
        if (cx && DepthRead(cx, dzUse, cov, mn, mx, me))
            FT("X;faro_copia;coperto=%.0f;z=%.4f-%.4f;media=%.4f;centro_nuvola_z=%.4f;camera=%s;copia=%s", cov, mn, mx, me, CloudCenterZ(camNow ? camNow : g_chainRaw, bcn), bcn ? "faro" : "faro_precedente", dzUse == g_winDsv ? "finestra" : "scena");
        if (cx && g_winOk && g_winDsv && dzUse != g_winDsv && DepthRead(cx, g_winDsv, cov, mn, mx, me))
            FT("X;finestra_copia;coperto=%.0f;z=%.4f-%.4f;media=%.4f;disegni=%lld", cov, mn, mx, me, g_winDraws);
        if (cx) cx->Release();
    }
    if (FaroCovers() && !g_faroDrawn) g_faroKept++;
    bool drew = FaroCovers() ? false : DrawAtPresent(s, camNow ? camNow : g_chainRaw, dzUse, sc, bcn);   // voce 297
    g_noDepth = false; g_depthBias = 0; g_depthNoWrite = false;
    if (alphaWas >= 0 && g_alpha == 0.35f) g_alpha = alphaWas;   // voce 271 (se nel frattempo il plugin non l'ha cambiata)
    if (drew) { g_fbDrawn++; if (camNow) g_camUsed++; else g_camStale++; }
    FT("P;interrotto;disegnato=%d;camera=%s%s;collegamenti=%d;profondita=%s;forbici=%d;blocchi=%zu;matrici=%d;scarti=proiezione:%d,proporzioni:%d,assi:%d,scala:%d,fuoco:%d,altro:%d;scala_min=%.2f;scala_max=%.2f;spostamento_max=%.2f;profondita_camera=%s;finestra=%d;copie_finestra=%ld;prima_interfaccia=%d",
       drew ? 1 : 0, camNow ? (bcn ? "dal_faro" : host >= 0 ? "del_fotogramma_riconosciuta_dai_collegamenti" : "del_fotogramma") : (curBlocked ? "precedente_sua_scartata" : "precedente"), camNow && g_candLink ? "_da_collegamento" : "", g_nLinks, depthOff ? "ignorata" : (useBcnCam ? (dzUse == g_winModelDsv ? "del_modello_nella_finestra" : dzUse == g_winDsv ? "della_finestra" : "del_faro_avvicinata") : (g_keepOk ? "copia" : "revit")), sc ? 1 : 0, g_ready.size(),
       nCandHere, g_rjProj, g_rjAsp, g_rjOrtho, g_rjScale, g_rjFocus, g_rjOther, g_rjKmin < 1e8f ? g_rjKmin : 0.0f, g_rjKmax, g_rjShift, bcn ? g_bcnZKind : "-", g_winOk ? 1 : 0, g_winCopies, ui ? 1 : 0);
}

// voce 268: chiamata da ogni disegno di Revit dopo che ha lasciato il buffer di profondita' del modello nella finestra.
// Al primo disegno nella finestra con un altro buffer di profondita' (l'interfaccia: cubo, maniglie, testi) si disegna la
// nuvola, con la logica del Present (FallbackAtPresent) ma nella destinazione attuale e prima di quei disegni.
static void BeforeUi(ID3D11DeviceContext* c)
{
    if (FaroCovers()) { g_uiArm = false; return; }   // voce 297, 299
    // voce 270 (diario della 269): i 540-880 disegni «del modello nella finestra» con il buffer #2 visti dalla 263 erano il
    // NOSTRO disegno al Present dell'immagine prima (la sequenza registrata comincia subito dopo il Present, e il nostro
    // disegno sta dentro il Present): LASCIO mostrava nostro=1. Revit disegna la scena solo nell'immagine a parte (#4/#5) e
    // nella finestra solo l'interfaccia (#1/#3). Quindi: la nuvola si disegna prima del primo disegno di Revit
    // nell'immagine della finestra, in ogni immagine.
    ID3D11RenderTargetView* rt = nullptr; ID3D11DepthStencilView* ds = nullptr; c->OMGetRenderTargets(1, &rt, &ds);
    bool go = false;
    if (rt)
    {
        ID3D11Resource* rr = nullptr; rt->GetResource(&rr);
        go = IsWindowImage(rr);
        if (rr) rr->Release();
    }
    if (go && g_fbSwap)
    {
        g_uiArm = false;
        if (g_trRec) TrPut(" NUVOLA-PRIMA-DELL'INTERFACCIA");
        g_uiRtv = rt;
        FallbackAtPresent(g_fbSwap, nullptr);
        g_uiRtv = nullptr;
    }
    if (rt) rt->Release(); if (ds) ds->Release();
}

// voce 294: vedi g_postWant
static void PostCheck(ID3D11DeviceContext* c)
{
    if (g_inOurDraw || !g_flatView || g_noPost || (FaroMode() && !g_noFaroDraw)) { g_postWant = false; return; }   // voce 297: la 294 non serve
    if (g_postWant)
    {
        g_postWant = false;
        ID3D11RenderTargetView* rt = nullptr; c->OMGetRenderTargets(1, &rt, nullptr);
        bool scene = false;
        if (rt) { ID3D11Resource* rr = nullptr; rt->GetResource(&rr); scene = rr && !IsWindowImage(rr); if (rr) rr->Release(); rt->Release(); }
        if (scene)
        {
            ID3D11VertexShader* vs = nullptr; ID3D11PixelShader* ps = nullptr; c->VSGetShader(&vs, nullptr, nullptr); c->PSGetShader(&ps, nullptr, nullptr);
            g_postVS = vs; g_postPS = ps; g_postIdx = g_drawSeq - g_imgStart; g_postOk = true; g_postLearn++;
            if (vs) vs->Release(); if (ps) ps->Release();
        }
        return;
    }
    if (!g_postOk || g_drawnThisFrame || g_postDone || !g_uiArm || !g_fbSwap) return;
    if (g_drawSeq - g_imgStart + 30 < g_postIdx) return;
    ID3D11VertexShader* vs = nullptr; ID3D11PixelShader* ps = nullptr; c->VSGetShader(&vs, nullptr, nullptr); c->PSGetShader(&ps, nullptr, nullptr);
    bool same = (void*)vs == g_postVS && (void*)ps == g_postPS;
    if (vs) vs->Release(); if (ps) ps->Release();
    if (!same) return;
    ID3D11RenderTargetView* rt = nullptr; c->OMGetRenderTargets(1, &rt, nullptr);
    if (!rt) return;
    ID3D11Resource* rr = nullptr; rt->GetResource(&rr);
    bool scene = rr && !IsWindowImage(rr);
    if (rr) rr->Release();
    if (scene)
    {
        g_postDone = true; g_uiArm = false; g_postNow = true; g_postDraws++;
        if (g_trRec) TrPut(" NUVOLA-PRIMA-DEI-RIEMPIMENTI");
        g_uiRtv = rt;
        FallbackAtPresent(g_fbSwap, nullptr);
        g_uiRtv = nullptr; g_postNow = false;
    }
    rt->Release();
}

static long long NodeKey(int layer, int node) { return ((long long)layer << 32) | (unsigned int)node; }

// voce 218: un blocco liberato o ricaricato non deve restare negli elenchi da disegnare. Spegnendo l'opzione dello shader
// il plugin liberava i blocchi ma l'elenco restava quello vecchio, con i buffer gia' rilasciati: Revit si chiudeva al
// primo disegno (segnale o Present).
static void DropSrv(ID3D11ShaderResourceView* srv)
{
    if (!srv) return;
    std::vector<PdItem>* lists[2] = { &g_ready, &g_build };
    for (std::vector<PdItem>* v : lists)
        for (size_t k = 0; k < v->size(); )
            if ((*v)[k].srv == srv) { (*v)[k] = v->back(); v->pop_back(); } else k++;
}

// Buffer di un blocco: data = count × (x, y, z float + colore RGBA 4 byte), coordinate del modello in piedi.
extern "C" __declspec(dllexport) int PdShaderUpload(int layer, int node, const void* data, int count)
{
    if (!g_realDev || count <= 0) return 0;
    long long k = NodeKey(layer, node);
    auto f = g_nodes.find(k);
    if (f != g_nodes.end())
    {
        if (f->second.srv) { DropSrv(f->second.srv); f->second.srv->Release(); }
        if (f->second.vb) { f->second.vb->Release(); g_shBytes -= (long long)f->second.count * 16; }
        g_nodes.erase(f);
    }
    D3D11_BUFFER_DESC bd = {}; bd.ByteWidth = (UINT)count * 16; bd.Usage = D3D11_USAGE_IMMUTABLE; bd.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    D3D11_SUBRESOURCE_DATA sd = {}; sd.pSysMem = data;
    ID3D11Buffer* b = nullptr;
    if (FAILED(g_realDev->CreateBuffer(&bd, &sd, &b)) || !b) return 0;
    D3D11_SHADER_RESOURCE_VIEW_DESC vd = {}; vd.Format = DXGI_FORMAT_R32G32B32A32_UINT; vd.ViewDimension = D3D11_SRV_DIMENSION_BUFFER;
    vd.Buffer.FirstElement = 0; vd.Buffer.NumElements = (UINT)count;
    ID3D11ShaderResourceView* v = nullptr;
    if (FAILED(g_realDev->CreateShaderResourceView(b, &vd, &v)) || !v) { b->Release(); return 0; }
    g_nodes[k] = PdNode{ b, v, count };
    g_shBytes += (long long)count * 16;
    g_shUploads++;
    return 1;
}

extern "C" __declspec(dllexport) void PdShaderFree(int layer, int node)
{
    auto f = g_nodes.find(NodeKey(layer, node));
    if (f == g_nodes.end()) return;
    if (f->second.srv) { DropSrv(f->second.srv); f->second.srv->Release(); }
    if (f->second.vb) { f->second.vb->Release(); g_shBytes -= (long long)f->second.count * 16; }
    g_nodes.erase(f);
}

extern "C" __declspec(dllexport) void PdShaderAdd(int layer, int node, int count, float spacing, float grow)
{
    auto f = g_nodes.find(NodeKey(layer, node));
    if (f == g_nodes.end() || !f->second.srv) return;
    if (count > f->second.count) count = f->second.count;
    g_build.push_back(PdItem{ f->second.srv, count, spacing, grow >= 1.0f || grow <= 0.0f ? 1.0f : grow });
}

// Fine dell'elenco del fotogramma: diventa quello da disegnare al prossimo segnale. maxPx = raggio massimo dei punti.
// voce 197: wanted = opzione accesa (la libreria cerca la matrice); on = elenco valido da disegnare. Spegnendo l'opzione
// a Revit aperto l'elenco si svuota: prima restava l'ultimo e si continuava a disegnarlo.
extern "C" __declspec(dllexport) void PdShaderEndFrame(int on, float maxPx, float scale, int wanted)
{
    // voce 238 (diario del 21/11 17:05): nei fotogrammi parziali (elemento selezionato o sotto il mouse) Revit da' al
    // plugin una camera ristretta al rettangolo ridisegnato, e l'elenco nuovo conteneva solo i blocchi li' dentro (es.
    // 291 blocchi contro 2882): disegnato su tutta la finestra, la nuvola restava solo attorno all'elemento. In quei
    // fotogrammi la camera non cambia, quindi si tiene l'elenco del fotogramma intero precedente.
    if (on && g_partial && !g_ready.empty())
    {
        FT("E;elenco_tenuto_nel_fotogramma_parziale;nuovo=%zu;tenuto=%zu", g_build.size(), g_ready.size());
        g_build.clear();
        g_shMaxPx = maxPx; g_shScale = scale; g_shaderOn = on; g_shaderWanted = wanted; g_shFrames++;
        return;
    }
    g_ready.swap(g_build);
    g_build.clear();
    if (!on) g_ready.clear();
    g_shMaxPx = maxPx; g_shScale = scale;
    g_shaderOn = on;
    g_shaderWanted = wanted;
    g_shFrames++;
}
static void ClearForNewView()
{
    g_ready.clear(); g_build.clear();
    g_clipOn = 0;
    g_hasRef = false; g_hasLastCap = false; g_chainGen = -1;
    g_nCand = 0; g_cmN = 0;
    g_fbSwap = nullptr; g_drawnThisFrame = false; g_keepOk = false; g_keepThisFrame = false; g_dzArm = false;
    g_partial = false; g_keepCamOk = false; g_keptOld = false;
}
// voce 223: il plugin non disegna piu' nella vista (nuvola spenta o nascosta): niente da ridisegnare nei fotogrammi
// senza il plugin, altrimenti restava a schermo l'ultima nuvola
// voce 295 (25/11: «a volte resta a schermo la nuvola quando cambio file o scheda»): la libreria dimenticava l'elenco solo
// se la vista era quella dell'ultimo disegno; con un'altra vista (altro file, altra scheda) continuava a ridisegnare la
// nuvola di prima nelle immagini senza il plugin. Ora -1 (cambio di vista dal plugin) o una vista diversa: si dimentica
// l'elenco e la finestra dell'ultimo fotogramma, finche' il plugin non disegna di nuovo.
extern "C" __declspec(dllexport) void PdViewOff(long long viewId)
{
    g_ready.clear(); g_build.clear(); g_sceneRes = nullptr;
    if (viewId == -1) { g_fbSwap = nullptr; g_drawnThisFrame = false; }
}

extern "C" __declspec(dllexport) int PdShaderReport(char* msg, int cap)
{
    _snprintf_s(msg, cap, _TRUNCATE, "disegno con shader: %ld fotogrammi, %ld blocchi disegnati (%lld punti), %ld blocchi caricati nella scheda video, %zu blocchi pronti (%.0f MB); scheda video per i punti: media %.1f ms, massimo %.1f ms (%ld misure); segnali senza disegno: matrice non pronta %ld, elenco vuoto %ld; finestra della vista: fotogrammi completi %ld (di cui trasparenti disegnati al Present %ld, opacita' %.2f; profondita' tenuta per i fotogrammi interrotti %ld volte), interrotti ridisegnati da noi %ld (con la camera del fotogramma %ld, con quella precedente %ld), camera dedotta uguale a quella vera %ld su %ld fotogrammi completi, matrici scartate (tasselli) %ld, interrotti senza matrice %ld, profondita' diversa %ld; Present di altre finestre %ld; scritture della matrice lette %ld; %s%s%s",
        g_shFrames, g_shDrawn, g_shPoints, g_shUploads, g_nodes.size(), g_shBytes / 1048576.0,
        g_gpuN > 0 ? g_gpuSum / g_gpuN : 0.0, g_gpuMax, g_gpuN, g_skipMatrix, g_skipEmpty,
        g_fbNormal, g_fbAlpha, g_alpha, g_keepCopies, g_fbDrawn, g_camUsed, g_camStale, g_fbAgree, g_fbCompared, g_camRejected, g_fbNoMat, g_fbRejected, g_fbOther, g_capWrites, g_learnInfo[0] ? g_learnInfo : "camera del modello: ricerca non ancora fatta", g_shErr[0] ? "; errore: " : "", g_shErr);
    {
        size_t L = strlen(msg);
        _snprintf_s(msg + L, cap - L, _TRUNCATE, "; fotogrammi parziali (mouse/selezione): rettangolo al segnale %ld, rettangoli di Present1 %ld, forbici al Present %ld, copia della profondita' tenuta %ld volte; ultimo rettangolo %ldx%ld in %ld,%ld; forbici con la camera cambiata (riquadro della vista, fotogramma completo) %ld; parziali disegnati con la profondita' intera di prima %ld; pianta/sezione: immagini intermedie con la nuvola prima dei riempimenti %ld (posizione imparata %ld volte); nuvola disegnata al posto del faro %ld (non riuscita %ld; immagini con la scena tenuta da Revit, nuvola gia' dentro %ld; visibile %ld, coperta dalla profondita' %ld; con lo stato di profondita' di Revit %ld: %s)",
            g_partMarker, g_partDirty, g_partScPresent, g_partKeepSkip, (long)(g_partLast.right - g_partLast.left), (long)(g_partLast.bottom - g_partLast.top), (long)g_partLast.left, (long)g_partLast.top, g_cropFrames, g_partialKeepDraws, g_postDraws, g_postLearn, g_faroDraws, g_faroFail, g_faroKept, g_faroVis, g_faroHid, g_faroDssUsed, g_faroDssInfo);
        g_partMarker = g_partDirty = g_partScPresent = g_partKeepSkip = 0; g_cropFrames = 0; g_partialKeepDraws = 0; g_postDraws = 0; g_postLearn = 0; g_faroDraws = 0; g_faroFail = 0; g_faroVis = g_faroHid = g_faroDssUsed = 0; g_faroKept = 0;
        L = strlen(msg);
        _snprintf_s(msg + L, cap - L, _TRUNCATE, "; disegni misurati: visibili %ld, coperti del tutto dalla profondita' %ld, nuvola fuori dalla finestra %ld (matrice cercata di nuovo per questo %ld volte, tenuta la precedente %ld; ripresa la posizione che funzionava %ld volte)",
            g_qVisible, g_qDepthBlock, g_qCamOut, g_reSearchDone, g_reSearchKept, g_goodUsed);
        g_qVisible = g_qDepthBlock = g_qCamOut = 0; g_reSearchDone = g_reSearchKept = 0; g_goodUsed = 0;
        L = strlen(msg);
        _snprintf_s(msg + L, cap - L, _TRUNCATE, "; camere dei fotogrammi interrotti ricavate dai collegamenti %ld, riconosciute dalle coppie camera/collegamento %ld (trasformazioni ricevute %d)", g_linkHits, g_hostHits, g_nLinks);
        g_linkHits = 0; g_hostHits = 0;
        L = strlen(msg);
        _snprintf_s(msg + L, cap - L, _TRUNCATE, "; faro: fotogrammi con il disegno a punti, completi %ld, intermedi %ld; disegni del faro nascosti %ld; %s; camera dal faro uguale alla vera %ld su %ld fotogrammi completi; immagini intermedie disegnate con la camera del faro %ld (profondita' della stessa immagine copiata %ld volte, mancante %ld), con la camera esatta perche' ferma %ld; profondita' del modello nelle immagini col faro: trovata %ld, stimata %ld, dal fotogramma completo in pianta/sezione %ld, quella del faro %ld (candidate con x, y, w uguali ma profondita' lontana dalla camera vera %ld)",
            g_bcnFull, g_bcnInt, g_bcnHidden, g_bcnExact ? "trasformazione della nuvola esatta (dalle API)" : g_bcnLocked ? "posizione della matrice appresa (trasformazione dedotta)" : "posizione della matrice non ancora appresa", g_bcnAgree, g_bcnCompared, g_bcnUsed, g_bcnDepthCopies, g_bcnDepthMissing, g_bcnStill, g_bcnZFound, g_bcnZEst, g_bcnZFlat, g_bcnZNone, g_bcnZRej);
        L = strlen(msg);
        _snprintf_s(msg + L, cap - L, _TRUNCATE, "; immagini senza faro con la camera precedente %ld, di cui con matrici di una camera spostata e nessuna uguale %ld", g_noBcnImgs, g_noBcnMoved);
        g_noBcnImgs = g_noBcnMoved = 0;
        L = strlen(msg);
        _snprintf_s(msg + L, cap - L, _TRUNCATE, "; mesh del modello per la profondita': %d triangoli, disegnata in %ld immagini col faro (non riuscita %ld), tolleranza sulle superfici %s%s; scarto di Revit misurato %ld volte, ultimo %.3g (%.1f cm)%s",
            g_meshTris, g_meshDraws, g_meshFail, g_meshTol ? "accesa" : "spenta", g_meshShFailed ? ", shader della mesh non compilato" : "",
            g_revOffN, g_revOff, g_revOffCm, g_revOffGen == g_viewGen ? "" : " (di un'altra vista: si usa 1 cm)");
        g_meshDraws = g_meshFail = 0; g_revOffN = 0;
        g_bcnFull = g_bcnInt = 0; g_bcnHidden = 0; g_bcnAgree = g_bcnCompared = g_bcnUsed = 0; g_bcnDepthCopies = g_bcnDepthMissing = g_bcnStill = 0; g_bcnZFound = g_bcnZEst = g_bcnZNone = g_bcnZRej = 0; g_bcnZFlat = 0;
    }
    if (g_trDone && g_trBuf[0])
    {
        size_t L = strlen(msg);
        _snprintf_s(msg + L, cap - L, _TRUNCATE, "; sequenza di un fotogramma:%s", g_trBuf);
    }
    // voce 228: la sequenza di un fotogramma (224) non si registra piu' a ogni resoconto: la lettura della profondita'
    // ferma la scheda video per un attimo. Il codice resta per una prova mirata.
    g_trDone = false; g_trArm = false;
    g_keepCopies = 0;
    g_fbNormal = g_fbAlpha = g_fbDrawn = g_fbNoMat = g_fbRejected = g_fbOther = g_fbAgree = g_fbCompared = g_capWrites = 0;
    g_camUsed = g_camStale = g_camRejected = 0;
    g_shFrames = 0; g_shDrawn = 0; g_shPoints = 0; g_shUploads = 0;
    g_gpuSum = 0; g_gpuMax = 0; g_gpuN = 0; g_skipMatrix = 0; g_skipEmpty = 0;
    return 1;
}

static std::atomic<int> g_inTest{ 0 };

extern "C" __declspec(dllexport) void PdSetAlpha(float a)
{
    g_alpha = a < 0.02f ? 0.02f : (a > 1.0f ? 1.0f : a);
}
extern "C" __declspec(dllexport) void PdSetFocus(const float* p)
{
    g_focus[0] = p[0]; g_focus[1] = p[1]; g_focus[2] = p[2];
}

static std::atomic<UINT> g_lastCount{ 0 };
static void Marker(ID3D11DeviceContext* c, UINT count)
{
    g_draws++;
    g_lastCount.store(count, std::memory_order_relaxed);
    if (count != MarkerIndices) return;
    long n = ++g_markers;
    if (g_trRec)
    {
        TrFlush();
        ID3D11RenderTargetView* rtv = nullptr; ID3D11DepthStencilView* dsv = nullptr; c->OMGetRenderTargets(1, &rtv, &dsv);
        TrPut(" SEGNALE(r#%d d#%d)", TrView(rtv), TrView(dsv));
        TrDepth(c, dsv, "profondita' al segnale");
        if (rtv) rtv->Release(); if (dsv) dsv->Release();
    }
    if (g_shaderWanted && !g_inTest.exchange(1)) { ShaderDraw(c); g_inTest = 0; }
    g_markerThread = GetCurrentThreadId();
    if (n % 30 == 1) DescribeState(c, g_markerInfo, sizeof(g_markerInfo));
}

static void STDMETHODCALLTYPE HookDI(ID3D11DeviceContext* c, UINT a, UINT b, INT d) { BeaconSeen(c, a, 0, "indicizzato"); Marker(c, a); Capture(c); g_origDI(c, a, b, d); }
static void STDMETHODCALLTYPE HookD(ID3D11DeviceContext* c, UINT a, UINT b) { if (BeaconSeen(c, a, 0, "semplice")) return; Marker(c, a); Capture(c); g_origD(c, a, b); }
static void STDMETHODCALLTYPE HookDII(ID3D11DeviceContext* c, UINT a, UINT b, UINT d, INT e, UINT f) { BeaconSeen(c, a, b, "indicizzato_istanze"); Marker(c, a); Capture(c); g_origDII(c, a, b, d, e, f); }
static void STDMETHODCALLTYPE HookDInst(ID3D11DeviceContext* c, UINT a, UINT b, UINT d, UINT e) { BeaconSeen(c, a, b, "istanze"); Marker(c, a); Capture(c); g_origDInst(c, a, b, d, e); }

static void* Patch(void** vt, int index, void* hook)
{
    DWORD old;
    if (!VirtualProtect(&vt[index], sizeof(void*), PAGE_READWRITE, &old)) return nullptr;
    void* orig = vt[index];
    vt[index] = hook;
    VirtualProtect(&vt[index], sizeof(void*), old, &old);
    return orig;
}

// ---- voce 182: Revit non passa dalla tabella del contesto immediato (registro 18/11 20:48: 0 disegni in tutto).
// Si guarda dove passa davvero: contesti differiti (registrano i comandi e li eseguono dopo con ExecuteCommandList),
// disegni indiretti, presentazione a schermo (Present) e quali librerie grafiche sono caricate nel processo.
static DrawIndexed_t g_defDI = nullptr;
static Draw_t g_defD = nullptr;
static DrawIndexedInst_t g_defDII = nullptr;
static DrawInst_t g_defDInst = nullptr;
typedef void (STDMETHODCALLTYPE* ExecCmd_t)(ID3D11DeviceContext*, ID3D11CommandList*, BOOL);
typedef void (STDMETHODCALLTYPE* Indirect_t)(ID3D11DeviceContext*, ID3D11Buffer*, UINT);
typedef HRESULT(STDMETHODCALLTYPE* Present_t)(IDXGISwapChain*, UINT, UINT);
typedef HRESULT(STDMETHODCALLTYPE* Present1_t)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);
static ExecCmd_t g_origExec = nullptr;
static Indirect_t g_origIndI = nullptr, g_origInd = nullptr, g_defIndI = nullptr, g_defInd = nullptr;
static Present_t g_origPresent = nullptr;
static Present1_t g_origPresent1 = nullptr;
static std::atomic<long> g_defDraws{ 0 }, g_exec{ 0 }, g_indirect{ 0 }, g_presents{ 0 };
static bool g_defSame = false;
static char g_mods[300] = "";

static void STDMETHODCALLTYPE HookDefDI(ID3D11DeviceContext* c, UINT a, UINT b, INT d) { g_defDraws++; Marker(c, a); g_defDI(c, a, b, d); }
static void STDMETHODCALLTYPE HookDefD(ID3D11DeviceContext* c, UINT a, UINT b) { g_defDraws++; Marker(c, a); g_defD(c, a, b); }
static void STDMETHODCALLTYPE HookDefDII(ID3D11DeviceContext* c, UINT a, UINT b, UINT d, INT e, UINT f) { g_defDraws++; Marker(c, a); g_defDII(c, a, b, d, e, f); }
static void STDMETHODCALLTYPE HookDefDInst(ID3D11DeviceContext* c, UINT a, UINT b, UINT d, UINT e) { g_defDraws++; Marker(c, a); g_defDInst(c, a, b, d, e); }
static void STDMETHODCALLTYPE HookExec(ID3D11DeviceContext* c, ID3D11CommandList* l, BOOL r) { g_exec++; g_origExec(c, l, r); }
static void STDMETHODCALLTYPE HookIndI(ID3D11DeviceContext* c, ID3D11Buffer* b, UINT o) { g_indirect++; g_origIndI(c, b, o); }
static void STDMETHODCALLTYPE HookInd(ID3D11DeviceContext* c, ID3D11Buffer* b, UINT o) { g_indirect++; g_origInd(c, b, o); }
static void STDMETHODCALLTYPE HookDefIndI(ID3D11DeviceContext* c, ID3D11Buffer* b, UINT o) { g_indirect++; g_defIndI(c, b, o); }
static void STDMETHODCALLTYPE HookDefInd(ID3D11DeviceContext* c, ID3D11Buffer* b, UINT o) { g_indirect++; g_defInd(c, b, o); }
// ---- voce 183: il contesto vero di Revit. Dal registro del 18/11 20:53 Present di Revit passa (55-115 ogni 5 s) ma
// nessun disegno passa dalla tabella del nostro dispositivo di prova: il dispositivo di Revit ha una tabella diversa
// (altra scheda video: nel processo ci sono sia il driver NVIDIA sia quello Intel, o altre opzioni di creazione).
// Alla prima Present di Revit si prende il suo dispositivo dalla swap chain, il suo contesto immediato, e si aggancia
// la tabella di quello.
static DrawIndexed_t g_realDI = nullptr;
static Draw_t g_realD = nullptr;
static DrawIndexedInst_t g_realDII = nullptr;
static DrawInst_t g_realDInst = nullptr;
static DrawIndexed_t g_rdefDI = nullptr;
static DrawIndexedInst_t g_rdefDII = nullptr;
static std::atomic<int> g_realDone{ 0 };
static char g_realInfo[700] = "";
static void BeforeUi(ID3D11DeviceContext* c);
// voce 263: nelle sequenze, quali texture leggono i disegni (pixel shader, slot 0-3): si vede quando Revit copia
// l'immagine della scena (#4) nella finestra e quali disegni vengono dopo (cubo di navigazione, maniglie del riquadro)
static void TrSrv(ID3D11DeviceContext* c)
{
    ID3D11ShaderResourceView* v[4] = {}; c->PSGetShaderResources(0, 4, v);
    int id[4]; for (int k = 0; k < 4; k++) { id[k] = TrView(v[k]); if (v[k]) v[k]->Release(); }
    int sig = id[0] | (id[1] << 7) | (id[2] << 14) | (id[3] << 21);
    if (sig == g_trSrvSig) return;
    g_trSrvSig = sig; TrFlush();
    TrPut(" S(#%d,#%d,#%d,#%d)", id[0], id[1], id[2], id[3]);
}
static void STDMETHODCALLTYPE HookRealDI(ID3D11DeviceContext* c, UINT a, UINT b, INT d) { if (g_sceneBound && !g_inOurDraw) g_sceneDrawn = true; PostCheck(c); if (g_uiArm && g_curWin && !g_inOurDraw) BeforeUi(c); g_drawSeq++; if (g_trRec) { TrSrv(c); g_trDraws++; } BeaconSeen(c, a, 0, "indicizzato"); Marker(c, a); Capture(c); g_realDI(c, a, b, d); }
static void STDMETHODCALLTYPE HookRealD(ID3D11DeviceContext* c, UINT a, UINT b) { if (g_sceneBound && !g_inOurDraw) g_sceneDrawn = true; PostCheck(c); if (g_uiArm && g_curWin && !g_inOurDraw) BeforeUi(c); g_drawSeq++; if (g_trRec) { TrSrv(c); g_trDraws++; } if (BeaconSeen(c, a, 0, "semplice")) return; Marker(c, a); Capture(c); g_realD(c, a, b); }
static void STDMETHODCALLTYPE HookRealDII(ID3D11DeviceContext* c, UINT a, UINT b, UINT d, INT e, UINT f) { if (g_sceneBound && !g_inOurDraw) g_sceneDrawn = true; PostCheck(c); if (g_uiArm && g_curWin && !g_inOurDraw) BeforeUi(c); g_drawSeq++; if (g_trRec) { TrSrv(c); g_trDraws++; } BeaconSeen(c, a, b, "indicizzato_istanze"); Marker(c, a); Capture(c); g_realDII(c, a, b, d, e, f); }
static void STDMETHODCALLTYPE HookRealDInst(ID3D11DeviceContext* c, UINT a, UINT b, UINT d, UINT e) { if (g_sceneBound && !g_inOurDraw) g_sceneDrawn = true; PostCheck(c); if (g_uiArm && g_curWin && !g_inOurDraw) BeforeUi(c); g_drawSeq++; if (g_trRec) { TrSrv(c); g_trDraws++; } BeaconSeen(c, a, b, "istanze"); Marker(c, a); Capture(c); g_realDInst(c, a, b, d, e); }
static void STDMETHODCALLTYPE HookRDefDI(ID3D11DeviceContext* c, UINT a, UINT b, INT d) { g_defDraws++; Marker(c, a); g_rdefDI(c, a, b, d); }
static void STDMETHODCALLTYPE HookRDefDII(ID3D11DeviceContext* c, UINT a, UINT b, UINT d, INT e, UINT f) { g_defDraws++; Marker(c, a); g_rdefDII(c, a, b, d, e, f); }

static void Rehook(ID3D11DeviceContext* c);
// ---- voce 184: con il contesto vero agganciato i disegni restano 0 (registro 18/11 21:43). Si contano anche le altre
// operazioni che Revit fa a ogni fotogramma sul contesto vero (pulizie, copie, scritture di memoria, liste eseguite,
// disegni indiretti) e si agganciano le altre interfacce del contesto (ID3D11DeviceContext1..4), se hanno tabelle diverse.
typedef void (STDMETHODCALLTYPE* Clear_t)(ID3D11DeviceContext*, ID3D11RenderTargetView*, const FLOAT[4]);
typedef void (STDMETHODCALLTYPE* Copy_t)(ID3D11DeviceContext*, ID3D11Resource*, ID3D11Resource*);
typedef void (STDMETHODCALLTYPE* CopyRegion_t)(ID3D11DeviceContext*, ID3D11Resource*, UINT, UINT, UINT, UINT, ID3D11Resource*, UINT, const D3D11_BOX*);
typedef void (STDMETHODCALLTYPE* Update_t)(ID3D11DeviceContext*, ID3D11Resource*, UINT, const D3D11_BOX*, const void*, UINT, UINT);
typedef HRESULT(STDMETHODCALLTYPE* Map_t)(ID3D11DeviceContext*, ID3D11Resource*, UINT, D3D11_MAP, UINT, D3D11_MAPPED_SUBRESOURCE*);
static Clear_t g_rClear = nullptr; static Copy_t g_rCopy = nullptr; static CopyRegion_t g_rCopyReg = nullptr;
static Update_t g_rUpdate = nullptr; static Map_t g_rMap = nullptr; static ExecCmd_t g_rExec = nullptr;
static Indirect_t g_rIndI = nullptr;
static std::atomic<long> g_cClear{ 0 }, g_cCopy{ 0 }, g_cUpdate{ 0 }, g_cMap{ 0 }, g_cExec{ 0 }, g_cIndI{ 0 };
static void STDMETHODCALLTYPE HookRClear(ID3D11DeviceContext* c, ID3D11RenderTargetView* v, const FLOAT col[4]) { g_cClear++; if (g_trRec) { TrFlush(); TrPut(" C#%d", TrView(v)); } g_rClear(c, v, col); }
static void STDMETHODCALLTYPE HookRCopy(ID3D11DeviceContext* c, ID3D11Resource* d, ID3D11Resource* s) { g_cCopy++; if (g_trRec) { TrFlush(); TrPut(" K#%d<#%d", TrId(d), TrId(s)); } g_rCopy(c, d, s); }
static void STDMETHODCALLTYPE HookRCopyReg(ID3D11DeviceContext* c, ID3D11Resource* d, UINT a, UINT x, UINT y, UINT z, ID3D11Resource* s, UINT b, const D3D11_BOX* bx) { g_cCopy++; if (g_trRec) { TrFlush(); TrPut(" K#%d<#%d", TrId(d), TrId(s)); } g_rCopyReg(c, d, a, x, y, z, s, b, bx); }
typedef void (STDMETHODCALLTYPE* ClearDS_t)(ID3D11DeviceContext*, ID3D11DepthStencilView*, UINT, FLOAT, UINT8);
typedef void (STDMETHODCALLTYPE* Resolve_t)(ID3D11DeviceContext*, ID3D11Resource*, UINT, ID3D11Resource*, UINT, DXGI_FORMAT);
static ClearDS_t g_rClearDS = nullptr; static Resolve_t g_rResolve = nullptr;
static void STDMETHODCALLTYPE HookRClearDS(ID3D11DeviceContext* c, ID3D11DepthStencilView* v, UINT fl, FLOAT dp, UINT8 st) { if (g_trRec) { TrFlush(); TrPut(" Z#%d=%.2f", TrView(v), dp); } g_rClearDS(c, v, fl, dp, st); }
static void STDMETHODCALLTYPE HookRResolve(ID3D11DeviceContext* c, ID3D11Resource* d, UINT a, ID3D11Resource* s, UINT b, DXGI_FORMAT f) { if (g_trRec) { TrFlush(); TrPut(" R#%d<#%d", TrId(d), TrId(s)); } g_rResolve(c, d, a, s, b, f); }
static void STDMETHODCALLTYPE HookRUpdate(ID3D11DeviceContext* c, ID3D11Resource* r, UINT a, const D3D11_BOX* b, const void* d, UINT p, UINT q) { g_cUpdate++; g_rUpdate(c, r, a, b, d, p, q); }
static HRESULT STDMETHODCALLTYPE HookRMap(ID3D11DeviceContext* c, ID3D11Resource* r, UINT a, D3D11_MAP t, UINT f, D3D11_MAPPED_SUBRESOURCE* m)
{
    g_cMap++; HRESULT h = g_rMap(c, r, a, t, f, m);
    if (r && r == g_capBuf && SUCCEEDED(h) && m && (t == D3D11_MAP_WRITE_DISCARD || t == D3D11_MAP_WRITE_NO_OVERWRITE || t == D3D11_MAP_WRITE)) g_capPtr = m->pData;
    if (SUCCEEDED(h) && m && (t == D3D11_MAP_WRITE_DISCARD || t == D3D11_MAP_WRITE_NO_OVERWRITE)) { LearnMap(r, m->pData); CamMap(r, m->pData); ShadowMap(r, m->pData); }
    Rehook(c); return h;
}
// voce 209: alla chiusura della scrittura del buffer della matrice si legge la matrice appena scritta
typedef void (STDMETHODCALLTYPE* Unmap_t)(ID3D11DeviceContext*, ID3D11Resource*, UINT);
static Unmap_t g_rUnmap = nullptr;
static void STDMETHODCALLTYPE HookRUnmap(ID3D11DeviceContext* c, ID3D11Resource* r, UINT a)
{
    if (r && r == g_capBuf && g_capPtr) { CaptureWrite(g_capPtr); g_capPtr = nullptr; }
    LearnUnmap(r);
    CamUnmap(r);
    ShadowUnmap(r);
    g_rUnmap(c, r, a);
}
static void STDMETHODCALLTYPE HookRExec(ID3D11DeviceContext* c, ID3D11CommandList* l, BOOL r) { g_cExec++; g_rExec(c, l, r); }
static void STDMETHODCALLTYPE HookRIndI(ID3D11DeviceContext* c, ID3D11Buffer* b, UINT o) { g_cIndI++; g_rIndI(c, b, o); }

// altre interfacce del contesto: se una ha una tabella diversa, lì si aggancia di nuovo il disegno (solo DrawIndexed)
static DrawIndexed_t g_ifDI[4] = {};
static void STDMETHODCALLTYPE HookIf0(ID3D11DeviceContext* c, UINT a, UINT b, INT d) { Marker(c, a); g_ifDI[0](c, a, b, d); }
static void STDMETHODCALLTYPE HookIf1(ID3D11DeviceContext* c, UINT a, UINT b, INT d) { Marker(c, a); g_ifDI[1](c, a, b, d); }
static void STDMETHODCALLTYPE HookIf2(ID3D11DeviceContext* c, UINT a, UINT b, INT d) { Marker(c, a); g_ifDI[2](c, a, b, d); }
static void STDMETHODCALLTYPE HookIf3(ID3D11DeviceContext* c, UINT a, UINT b, INT d) { Marker(c, a); g_ifDI[3](c, a, b, d); }

// ---- voce 188: il registro del 19/11 10:12 mostra sul contesto agganciato decine di migliaia di impostazioni di shader,
// vertici e finestre per fotogramma ma nessun disegno, e una seconda swap chain con la stessa tabella dove la voce 12
// (DrawIndexed) non era più la nostra: il runtime di DirectX 11 riscrive da sé le voci di disegno della tabella del
// contesto (sceglie la funzione veloce adatta allo stato corrente) e cancella il nostro aggancio. Si riaggancia subito
// dopo ogni impostazione di stato (le chiamate prima di un disegno), salvando ogni volta la funzione scelta dal runtime.
static std::atomic<long> g_rehooks{ 0 };
static void Rehook(ID3D11DeviceContext* c)
{
    void** vt = *(void***)c;
    if (vt[12] != (void*)&HookRealDI && vt[12] != (void*)&HookDI) { void* o = Patch(vt, 12, (void*)&HookRealDI); if (o) { g_realDI = (DrawIndexed_t)o; g_rehooks++; } }
    if (vt[13] != (void*)&HookRealD && vt[13] != (void*)&HookD) { void* o = Patch(vt, 13, (void*)&HookRealD); if (o) { g_realD = (Draw_t)o; g_rehooks++; } }
    if (vt[20] != (void*)&HookRealDII && vt[20] != (void*)&HookDII) { void* o = Patch(vt, 20, (void*)&HookRealDII); if (o) { g_realDII = (DrawIndexedInst_t)o; g_rehooks++; } }
    if (vt[21] != (void*)&HookRealDInst && vt[21] != (void*)&HookDInst) { void* o = Patch(vt, 21, (void*)&HookRealDInst); if (o) { g_realDInst = (DrawInst_t)o; g_rehooks++; } }
}

// voce 187: altre operazioni sul contesto di Revit, per capire se la pipeline di disegno passa di qui
typedef void (STDMETHODCALLTYPE* Dispatch_t)(ID3D11DeviceContext*, UINT, UINT, UINT);
typedef void (STDMETHODCALLTYPE* DrawAuto_t)(ID3D11DeviceContext*);
typedef void (STDMETHODCALLTYPE* OMSet_t)(ID3D11DeviceContext*, UINT, ID3D11RenderTargetView* const*, ID3D11DepthStencilView*);
typedef void (STDMETHODCALLTYPE* SetShader_t)(ID3D11DeviceContext*, void*, void* const*, UINT);
typedef void (STDMETHODCALLTYPE* SetVB_t)(ID3D11DeviceContext*, UINT, UINT, ID3D11Buffer* const*, const UINT*, const UINT*);
typedef void (STDMETHODCALLTYPE* SetVP_t)(ID3D11DeviceContext*, UINT, const D3D11_VIEWPORT*);
static Dispatch_t g_rDispatch = nullptr; static Indirect_t g_rDispatchInd = nullptr, g_rDrawInstInd = nullptr; static DrawAuto_t g_rDrawAuto = nullptr;
static OMSet_t g_rOMSet = nullptr; static SetShader_t g_rVS = nullptr, g_rPS = nullptr; static SetVB_t g_rSetVB = nullptr; static SetVP_t g_rSetVP = nullptr;
static std::atomic<long> g_cDispatch{ 0 }, g_cDrawOther{ 0 }, g_cOMSet{ 0 }, g_cShader{ 0 }, g_cSetVB{ 0 }, g_cSetVP{ 0 };
static void STDMETHODCALLTYPE HookRDispatch(ID3D11DeviceContext* c, UINT x, UINT y, UINT z) { g_cDispatch++; g_rDispatch(c, x, y, z); }
static void STDMETHODCALLTYPE HookRDispatchInd(ID3D11DeviceContext* c, ID3D11Buffer* b, UINT o) { g_cDispatch++; g_rDispatchInd(c, b, o); }
static void STDMETHODCALLTYPE HookRDrawInstInd(ID3D11DeviceContext* c, ID3D11Buffer* b, UINT o) { g_cDrawOther++; g_rDrawInstInd(c, b, o); }
static void STDMETHODCALLTYPE HookRDrawAuto(ID3D11DeviceContext* c) { g_cDrawOther++; g_rDrawAuto(c); }
static void STDMETHODCALLTYPE HookROMSet(ID3D11DeviceContext* c, UINT n, ID3D11RenderTargetView* const* r, ID3D11DepthStencilView* d)
{
    g_cOMSet++;
    // voce 269: nel diario della 268 (come nella 267) la copia della finestra non si faceva mai: la condizione «non e' un
    // nostro disegno» (g_ourDraw) risultava sempre falsa per motivi non chiari. I nostri ripristini si riconoscono ora dal
    // buffer di profondita': e' una delle nostre copie (scena, faro, finestra). Nelle sequenze: LASCIO(...) con i motivi.
    if (g_drawSeq - g_bindSeq >= 200)   // 266: l'interfaccia (#3) ha circa 150 disegni, il modello 540-680
    {   // voce 264: Revit lascia il buffer di profondita' con cui ha disegnato il modello nell'immagine della finestra
        ID3D11RenderTargetView* crt = nullptr; ID3D11DepthStencilView* cds = nullptr; c->OMGetRenderTargets(1, &crt, &cds);
        if (crt && cds)
        {
            ID3D11Resource *rr = nullptr, *rd = nullptr, *rn = nullptr; crt->GetResource(&rr); cds->GetResource(&rd); if (d) d->GetResource(&rn);
            bool ours = rd && (rd == (ID3D11Resource*)g_keepTex || rd == (ID3D11Resource*)g_bcnTex || rd == (ID3D11Resource*)g_winTex);
            bool win = IsWindowImage(rr);
            if (g_trRec && win) TrPut(" LASCIO(finestra_nota=%d,nostro=%d,flag=%d,stesso=%d,disegni=%lld)", g_bbRes ? 1 : 0, ours ? 1 : 0, g_ourDraw ? 1 : 0, rn == rd ? 1 : 0, g_drawSeq - g_bindSeq);
            if (win && !ours && rn != rd)
            {
                WinDepthCopy(c, cds); g_winDraws = g_drawSeq - g_bindSeq; if (g_trRec) TrPut(" COPIA-FINESTRA(%lld)", g_winDraws);
                // voce 270: quel «modello nella finestra» era il nostro disegno al Present (vedi BeforeUi): niente armamento qui
            }
            if (rr) rr->Release(); if (rd) rd->Release(); if (rn) rn->Release();
        }
        if (crt) crt->Release(); if (cds) cds->Release();
    }
    g_bindSeq = g_drawSeq;
    {   // voce 270
        bool w = false;
        if (n > 0 && r && r[0]) { ID3D11Resource* rr = nullptr; r[0]->GetResource(&rr); w = IsWindowImage(rr); if (rr) rr->Release(); }
        g_curWin = w;
        { ID3D11Resource* sr = nullptr; if (n > 0 && r && r[0]) r[0]->GetResource(&sr); g_sceneBound = sr && sr == g_sceneRes; if (sr) sr->Release(); }   // voce 299
        // voce 271: buffer di profondita' della scena (non della finestra, non nostri, grandi come la finestra)
        if (!w && d && g_bbRes)
        {
            ID3D11Resource* rn = nullptr; d->GetResource(&rn);
            if (rn)
            {
                D3D11_RESOURCE_DIMENSION dim; rn->GetType(&dim);
                bool ours = rn == (ID3D11Resource*)g_keepTex || rn == (ID3D11Resource*)g_bcnTex || rn == (ID3D11Resource*)g_winTex || rn == (ID3D11Resource*)g_sideTex;
                if (dim == D3D11_RESOURCE_DIMENSION_TEXTURE2D && !ours)
                {
                    D3D11_TEXTURE2D_DESC dd = {}; ((ID3D11Texture2D*)rn)->GetDesc(&dd);
                    if (dd.Width == g_bbDesc.Width && dd.Height == g_bbDesc.Height && dd.SampleDesc.Count == 1 && dd.MipLevels == 1 && dd.ArraySize == 1) SideNote(rn);
                }
                rn->Release();
            }
        }
    }
    if (g_trRec) { TrFlush(); TrPut(" T(r#%d d#%d)", (n > 0 && r) ? TrView(r[0]) : 0, TrView(d)); }
    // voce 227: dopo il segnale, la prima volta che Revit lascia il buffer di profondita' della scena se ne fa una copia:
    // li' c'e' il modello finito, anche se poi Revit riusa o svuota quel buffer prima del Present (VistaIniziale)
    // voce 248: profondita' dell'immagine del faro
    ID3D11DepthStencilView* bcnLeave = nullptr;
    if (g_bcnDzArm)
    {
        ID3D11DepthStencilView* cur = nullptr; c->OMGetRenderTargets(0, nullptr, &cur);
        if (cur)
        {
            ID3D11Resource *rc = nullptr, *rn = nullptr; cur->GetResource(&rc); if (d) d->GetResource(&rn);
            if (rc && rc == g_bcnDzSrc && rn != rc) bcnLeave = cur; else cur->Release();
            if (rc) rc->Release(); if (rn) rn->Release();
        }
    }
    bool leave = false;
    if (g_dzArm && g_fbDsv)
    {
        ID3D11DepthStencilView* cur = nullptr; c->OMGetRenderTargets(0, nullptr, &cur);
        if (cur)
        {
            ID3D11Resource *rc = nullptr, *rf = nullptr, *rn = nullptr;
            cur->GetResource(&rc); g_fbDsv->GetResource(&rf); if (d) d->GetResource(&rn);
            leave = rc && rc == rf && rn != rf;
            if (rc) rc->Release(); if (rf) rf->Release(); if (rn) rn->Release();
            cur->Release();
        }
    }
    g_rOMSet(c, n, r, d);
    if (bcnLeave) { ID3D11Resource* rr = nullptr; bcnLeave->GetResource(&rr); BeaconDepthCopyRes(c, rr); if (rr) rr->Release(); bcnLeave->Release(); BeaconDzDisarm(); if (g_trRec) TrPut(" COPIA-FARO"); }
    if (leave)
    {
        g_dzArm = false;
        if (KeepStillGood()) { g_partKeepSkip++; g_keptOld = true; } else KeepDepthFrom(c);   // voce 229
        g_keepThisFrame = g_keepOk; if (g_trRec) TrPut(" COPIA-PROF");
    }
    Rehook(c);
}
static void STDMETHODCALLTYPE HookRVS(ID3D11DeviceContext* c, void* s, void* const* i, UINT n) { g_cShader++; g_rVS(c, s, i, n); Rehook(c); }
static void STDMETHODCALLTYPE HookRPS(ID3D11DeviceContext* c, void* s, void* const* i, UINT n) { g_cShader++; g_rPS(c, s, i, n); Rehook(c); }
static void STDMETHODCALLTYPE HookRSetVB(ID3D11DeviceContext* c, UINT a, UINT n, ID3D11Buffer* const* b, const UINT* s, const UINT* o) { g_cSetVB++; g_rSetVB(c, a, n, b, s, o); Rehook(c); }
static void STDMETHODCALLTYPE HookRSetVP(ID3D11DeviceContext* c, UINT n, const D3D11_VIEWPORT* v) { g_cSetVP++; g_rSetVP(c, n, v); Rehook(c); }

static int HookExtra(ID3D11DeviceContext* imm, char* buf, int cap)
{
    void** vt = *(void***)imm;
    g_rClear = (Clear_t)Patch(vt, 50, (void*)&HookRClear);
    g_rCopy = (Copy_t)Patch(vt, 47, (void*)&HookRCopy);
    g_rCopyReg = (CopyRegion_t)Patch(vt, 46, (void*)&HookRCopyReg);
    g_rClearDS = (ClearDS_t)Patch(vt, 53, (void*)&HookRClearDS);
    g_rResolve = (Resolve_t)Patch(vt, 57, (void*)&HookRResolve);
    g_rUpdate = (Update_t)Patch(vt, 48, (void*)&HookRUpdate);
    g_rMap = (Map_t)Patch(vt, 14, (void*)&HookRMap);
    g_rUnmap = (Unmap_t)Patch(vt, 15, (void*)&HookRUnmap);
    g_rExec = (ExecCmd_t)Patch(vt, 58, (void*)&HookRExec);
    g_rIndI = (Indirect_t)Patch(vt, 39, (void*)&HookRIndI);
    g_rDrawInstInd = (Indirect_t)Patch(vt, 40, (void*)&HookRDrawInstInd);
    g_rDrawAuto = (DrawAuto_t)Patch(vt, 38, (void*)&HookRDrawAuto);
    g_rDispatch = (Dispatch_t)Patch(vt, 41, (void*)&HookRDispatch);
    g_rDispatchInd = (Indirect_t)Patch(vt, 42, (void*)&HookRDispatchInd);
    g_rOMSet = (OMSet_t)Patch(vt, 33, (void*)&HookROMSet);
    g_rVS = (SetShader_t)Patch(vt, 11, (void*)&HookRVS);
    g_rPS = (SetShader_t)Patch(vt, 9, (void*)&HookRPS);
    g_rSetVB = (SetVB_t)Patch(vt, 18, (void*)&HookRSetVB);
    g_rSetVP = (SetVP_t)Patch(vt, 44, (void*)&HookRSetVP);
    int n = 0;
    const IID* ids[4] = { &__uuidof(ID3D11DeviceContext1), &__uuidof(ID3D11DeviceContext2), &__uuidof(ID3D11DeviceContext3), &__uuidof(ID3D11DeviceContext4) };
    void* hooks[4] = { (void*)&HookIf0, (void*)&HookIf1, (void*)&HookIf2, (void*)&HookIf3 };
    void** seen[5] = { vt };
    int ns = 1;
    for (int k = 0; k < 4; k++)
    {
        IUnknown* p = nullptr;
        if (FAILED(imm->QueryInterface(*ids[k], (void**)&p)) || !p) { n += _snprintf_s(buf + n, cap - n, _TRUNCATE, " if%d:no", k + 1); continue; }
        void** v = *(void***)p;
        bool dup = false;
        for (int j = 0; j < ns; j++) if (seen[j] == v) dup = true;
        if (dup) n += _snprintf_s(buf + n, cap - n, _TRUNCATE, " if%d:%s", k + 1, (void*)p == (void*)imm ? "stessa" : "stessa tabella");
        else
        {
            seen[ns++] = v;
            g_ifDI[k] = (DrawIndexed_t)Patch(v, 12, hooks[k]);
            n += _snprintf_s(buf + n, cap - n, _TRUNCATE, " if%d:tabella diversa, agganciata", k + 1);
        }
        p->Release();
    }
    return n;
}

// voce 185: tutte le swap chain che presentano, non solo la prima (la prima poteva essere di un pannello)
static IDXGISwapChain* g_seenSc[16] = {};
static int g_nSc = 0;
static char g_scInfo[600] = "";
static int g_scLen = 0;
static void* g_firstVt = nullptr;

static void NoteSwapChain(IDXGISwapChain* s)
{
    for (int i = 0; i < g_nSc; i++) if (g_seenSc[i] == s) return;
    if (g_nSc >= 16) return;
    g_seenSc[g_nSc++] = s;
    DXGI_SWAP_CHAIN_DESC d = {};
    s->GetDesc(&d);
    ID3D11Device* dev = nullptr;
    const char* kind = "non DirectX 11";
    if (SUCCEEDED(s->GetDevice(__uuidof(ID3D11Device), (void**)&dev)) && dev)
    {
        ID3D11DeviceContext* imm = nullptr;
        dev->GetImmediateContext(&imm);
        if (imm)
        {
            void** vt = *(void***)imm;
            if (vt[12] == (void*)&HookRealDI || vt[12] == (void*)&HookDI) kind = "contesto gia' agganciato";
            else if (g_firstVt && (void*)vt != g_firstVt) kind = "contesto con ALTRA tabella (non agganciato)";
            else kind = "contesto non agganciato";
            imm->Release();
        }
        dev->Release();
    }
    g_scLen += _snprintf_s(g_scInfo + g_scLen, sizeof(g_scInfo) - g_scLen, _TRUNCATE, "%s#%d %ux%u %s", g_nSc > 1 ? ", " : "", g_nSc,
        d.BufferDesc.Width, d.BufferDesc.Height, kind);
}

// voce 188: a ogni Present, per ogni swap chain, si rimette l'aggancio sul contesto del suo dispositivo se ha la stessa
// tabella già agganciata (Rehook è idempotente: scrive solo le voci che il runtime ha cambiato)
static void RehookSwapChain(IDXGISwapChain* s)
{
    if (!g_firstVt) return;
    ID3D11Device* dev = nullptr;
    if (FAILED(s->GetDevice(__uuidof(ID3D11Device), (void**)&dev)) || !dev) return;
    ID3D11DeviceContext* imm = nullptr;
    dev->GetImmediateContext(&imm);
    if (imm)
    {
        if (*(void***)imm == g_firstVt) Rehook(imm);
        imm->Release();
    }
    dev->Release();
}

static void OnRealSwapChain(IDXGISwapChain* s)
{
    if (g_realDone.load()) { NoteSwapChain(s); RehookSwapChain(s); return; }
    g_realDone = 1;
    int n = 0;
    struct AtEnd { IDXGISwapChain* s; ~AtEnd() { NoteSwapChain(s); } } atEnd{ s };
    ID3D11Device* dev = nullptr;
    if (FAILED(s->GetDevice(__uuidof(ID3D11Device), (void**)&dev)) || !dev)
    {
        _snprintf_s(g_realInfo, sizeof(g_realInfo), _TRUNCATE, "la swap chain di Revit non appartiene a un dispositivo DirectX 11");
        return;
    }
    dev->AddRef(); g_realDev = dev;   // voce 194: dispositivo su cui creare i buffer della nuvola
    n += _snprintf_s(g_realInfo + n, sizeof(g_realInfo) - n, _TRUNCATE, "dispositivo di Revit: livello 0x%X, opzioni 0x%X", (unsigned)dev->GetFeatureLevel(), dev->GetCreationFlags());
    IDXGIDevice* xd = nullptr; IDXGIAdapter* ad = nullptr;
    if (SUCCEEDED(dev->QueryInterface(__uuidof(IDXGIDevice), (void**)&xd)) && SUCCEEDED(xd->GetAdapter(&ad)))
    {
        DXGI_ADAPTER_DESC d; ad->GetDesc(&d);
        char name[128]; WideCharToMultiByte(CP_UTF8, 0, d.Description, -1, name, sizeof(name), nullptr, nullptr);
        n += _snprintf_s(g_realInfo + n, sizeof(g_realInfo) - n, _TRUNCATE, ", scheda %s", name);
        ad->Release();
    }
    if (xd) xd->Release();
    ID3D11DeviceContext* imm = nullptr;
    dev->GetImmediateContext(&imm);
    if (imm)
    {
        void** vt = *(void***)imm;
        if (vt[12] == (void*)&HookDI || vt[12] == (void*)&HookRealDI)
            n += _snprintf_s(g_realInfo + n, sizeof(g_realInfo) - n, _TRUNCATE, "; contesto con la stessa tabella del dispositivo di prova");
        else
        {
            g_firstVt = (void*)vt;
            g_realDI = (DrawIndexed_t)Patch(vt, 12, (void*)&HookRealDI);
            g_realD = (Draw_t)Patch(vt, 13, (void*)&HookRealD);
            g_realDII = (DrawIndexedInst_t)Patch(vt, 20, (void*)&HookRealDII);
            g_realDInst = (DrawInst_t)Patch(vt, 21, (void*)&HookRealDInst);
            n += _snprintf_s(g_realInfo + n, sizeof(g_realInfo) - n, _TRUNCATE, "; contesto con tabella propria, agganciato %s", g_realDI ? "si'" : "NO");
        }
        n += _snprintf_s(g_realInfo + n, sizeof(g_realInfo) - n, _TRUNCATE, "; interfacce:");
        n += HookExtra(imm, g_realInfo + n, (int)sizeof(g_realInfo) - n);
        imm->Release();
    }
    ID3D11DeviceContext* def = nullptr;
    if (SUCCEEDED(dev->CreateDeferredContext(0, &def)) && def)
    {
        void** vd = *(void***)def;
        if (vd[12] != (void*)&HookDefDI && vd[12] != (void*)&HookRDefDI && vd[12] != (void*)&HookRealDI && vd[12] != (void*)&HookDI)
        {
            g_rdefDI = (DrawIndexed_t)Patch(vd, 12, (void*)&HookRDefDI);
            g_rdefDII = (DrawIndexedInst_t)Patch(vd, 20, (void*)&HookRDefDII);
            n += _snprintf_s(g_realInfo + n, sizeof(g_realInfo) - n, _TRUNCATE, "; contesti differiti agganciati");
        }
        def->Release();
    }
    dev->Release();
}

static HRESULT STDMETHODCALLTYPE HookPresent(IDXGISwapChain* s, UINT a, UINT f) { g_presents++; OnRealSwapChain(s); if (!(f & DXGI_PRESENT_TEST)) { FallbackAtPresent(s, nullptr); g_imgStart = g_drawSeq; g_postDone = false; g_postWant = false; g_faroDrawn = false; g_sceneDrawn = false; } return g_origPresent(s, a, f); }
static HRESULT STDMETHODCALLTYPE HookPresent1(IDXGISwapChain1* s, UINT a, UINT f, const DXGI_PRESENT_PARAMETERS* p) { g_presents++; OnRealSwapChain(s); if (!(f & DXGI_PRESENT_TEST)) { FallbackAtPresent(s, p); g_imgStart = g_drawSeq; g_postDone = false; g_postWant = false; g_faroDrawn = false; g_sceneDrawn = false; } return g_origPresent1(s, a, f, p); }

static void* Patch(void** vt, int index, void* hook);

static void ListModules()
{
    const char* names[] = { "d3d11.dll", "d3d12.dll", "dxgi.dll", "d3d10_1.dll", "d3d9.dll", "opengl32.dll", "d3d11on12.dll", "nvwgf2umx.dll", "atidxx64.dll", "igd10iumd64.dll" };
    int n = 0;
    for (const char* m : names)
        if (GetModuleHandleA(m)) n += _snprintf_s(g_mods + n, sizeof(g_mods) - n, _TRUNCATE, "%s%s", n ? " " : "", m);
}

static void HookMore(ID3D11Device* dev, ID3D11DeviceContext* imm)
{
    void** vi = *(void***)imm;
    g_origExec = (ExecCmd_t)Patch(vi, 58, (void*)&HookExec);
    g_origIndI = (Indirect_t)Patch(vi, 39, (void*)&HookIndI);
    g_origInd = (Indirect_t)Patch(vi, 40, (void*)&HookInd);
    ID3D11DeviceContext* def = nullptr;
    if (SUCCEEDED(dev->CreateDeferredContext(0, &def)) && def)
    {
        void** vd = *(void***)def;
        if (vd == vi) g_defSame = true;   // stessa tabella: già agganciata
        else
        {
            g_defDI = (DrawIndexed_t)Patch(vd, 12, (void*)&HookDefDI);
            g_defD = (Draw_t)Patch(vd, 13, (void*)&HookDefD);
            g_defDII = (DrawIndexedInst_t)Patch(vd, 20, (void*)&HookDefDII);
            g_defDInst = (DrawInst_t)Patch(vd, 21, (void*)&HookDefDInst);
            g_defIndI = (Indirect_t)Patch(vd, 39, (void*)&HookDefIndI);
            g_defInd = (Indirect_t)Patch(vd, 40, (void*)&HookDefInd);
        }
    }
    // swap chain di prova su una finestra nascosta, per agganciare Present (la tabella è quella di dxgi.dll)
    HWND w = CreateWindowExA(0, "STATIC", "PdRenderProbe", WS_OVERLAPPED, 0, 0, 64, 64, nullptr, nullptr, GetModuleHandleA(nullptr), nullptr);
    if (!w) return;
    IDXGIDevice* xd = nullptr; IDXGIAdapter* ad = nullptr; IDXGIFactory2* f2 = nullptr;
    if (SUCCEEDED(dev->QueryInterface(__uuidof(IDXGIDevice), (void**)&xd)) && SUCCEEDED(xd->GetAdapter(&ad))
        && SUCCEEDED(ad->GetParent(__uuidof(IDXGIFactory2), (void**)&f2)))
    {
        DXGI_SWAP_CHAIN_DESC1 d = {};
        d.Width = 64; d.Height = 64; d.Format = DXGI_FORMAT_B8G8R8A8_UNORM; d.SampleDesc.Count = 1;
        d.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; d.BufferCount = 2; d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
        IDXGISwapChain1* sc = nullptr;
        if (SUCCEEDED(f2->CreateSwapChainForHwnd(dev, w, &d, nullptr, nullptr, &sc)) && sc)
        {
            void** vs = *(void***)sc;
            g_origPresent = (Present_t)Patch(vs, 8, (void*)&HookPresent);
            g_origPresent1 = (Present1_t)Patch(vs, 22, (void*)&HookPresent1);
        }
    }
}

// Indici nella tabella dei metodi di ID3D11DeviceContext (IUnknown 0-2, ID3D11DeviceChild 3-6):
// 12 DrawIndexed, 13 Draw, 20 DrawIndexedInstanced, 21 DrawInstanced.
extern "C" __declspec(dllexport) int PdInit(char* msg, int cap)
{
    if (g_hooked) { _snprintf_s(msg, cap, _TRUNCATE, "gia' attiva"); return 1; }
    ID3D11Device* dev = nullptr;
    ID3D11DeviceContext* ctx = nullptr;
    D3D_FEATURE_LEVEL fl;
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &dev, &fl, &ctx);
    if (FAILED(hr) || !ctx) { _snprintf_s(msg, cap, _TRUNCATE, "D3D11CreateDevice non riuscito (0x%08X)", (unsigned)hr); return 0; }
    void** vt = *(void***)ctx;
    g_origDI = (DrawIndexed_t)Patch(vt, 12, (void*)&HookDI);
    g_origD = (Draw_t)Patch(vt, 13, (void*)&HookD);
    g_origDII = (DrawIndexedInst_t)Patch(vt, 20, (void*)&HookDII);
    g_origDInst = (DrawInst_t)Patch(vt, 21, (void*)&HookDInst);
    // il dispositivo di prova resta aperto: la tabella dei metodi è quella di d3d11.dll, comune a tutti i contesti
    g_hooked = g_origDI && g_origD && g_origDII && g_origDInst;
    if (g_hooked) HookMore(dev, ctx);
    ListModules();
    _snprintf_s(msg, cap, _TRUNCATE, "PdRender.dll compilata il %s alle %s; aggancio %s, livello DirectX 0x%X; contesto differito %s; Present %s; librerie grafiche caricate: %s",
        __DATE__, __TIME__, g_hooked ? "attivo" : "NON riuscito", (unsigned)fl, g_defSame ? "stessa tabella" : (g_defDI ? "agganciato" : "no"),
        g_origPresent ? "agganciato" : "no", g_mods);
    return g_hooked ? 1 : 0;
}

extern "C" __declspec(dllexport) void PdCaptureBegin()
{
    g_thread = GetCurrentThreadId();
    g_ctx = nullptr;
    g_calls = 0;
    g_otherThread = 0;
    g_capture = 1;
}

extern "C" __declspec(dllexport) int PdCaptureEnd(char* msg, int cap)
{
    g_capture = 0;
    ID3D11DeviceContext* c = g_ctx;
    if (!c)
    {
        _snprintf_s(msg, cap, _TRUNCATE, "nessuna chiamata di disegno DirectX durante il nostro disegno (altri thread: %ld)", g_otherThread.load());
        return 0;
    }
    char buf[700];
    int n = _snprintf_s(buf, sizeof(buf), _TRUNCATE, "chiamate %ld, altri thread %ld; ", g_calls.load(), g_otherThread.load());
    DescribeState(c, buf + n, (int)sizeof(buf) - n);
    strncpy_s(msg, cap, buf, _TRUNCATE);
    return 1;
}

static void DescribeState(ID3D11DeviceContext* c, char* buf, int cap)
{
    int n = _snprintf_s(buf, cap, _TRUNCATE, "contesto %s", c->GetType() == D3D11_DEVICE_CONTEXT_IMMEDIATE ? "immediato" : "differito");
    ID3D11RenderTargetView* rtv = nullptr;
    ID3D11DepthStencilView* dsv = nullptr;
    c->OMGetRenderTargets(1, &rtv, &dsv);
    if (rtv)
    {
        ID3D11Resource* r = nullptr; rtv->GetResource(&r);
        ID3D11Texture2D* t = nullptr;
        if (r && SUCCEEDED(r->QueryInterface(__uuidof(ID3D11Texture2D), (void**)&t)) && t)
        {
            D3D11_TEXTURE2D_DESC d; t->GetDesc(&d);
            n += _snprintf_s(buf + n, cap - n, _TRUNCATE, "; destinazione %ux%u formato %u campioni %u", d.Width, d.Height, (unsigned)d.Format, d.SampleDesc.Count);
            t->Release();
        }
        if (r) r->Release();
        rtv->Release();
    }
    else n += _snprintf_s(buf + n, cap - n, _TRUNCATE, "; nessuna destinazione");
    if (dsv)
    {
        D3D11_DEPTH_STENCIL_VIEW_DESC dd; dsv->GetDesc(&dd);
        n += _snprintf_s(buf + n, cap - n, _TRUNCATE, "; profondita' formato %u", (unsigned)dd.Format);
        dsv->Release();
    }
    else n += _snprintf_s(buf + n, cap - n, _TRUNCATE, "; nessun buffer di profondita'");
    D3D11_VIEWPORT vp; UINT nv = 1;
    c->RSGetViewports(&nv, &vp);
    if (nv > 0) _snprintf_s(buf + n, cap - n, _TRUNCATE, "; finestra %.0fx%.0f in %.0f,%.0f", vp.Width, vp.Height, vp.TopLeftX, vp.TopLeftY);
}

// Resoconto del segnale dall'ultima chiamata: quante volte è arrivato alla scheda video, su quale thread, e lo stato.
extern "C" __declspec(dllexport) int PdReport(char* msg, int cap)
{
    long m = g_markers.exchange(0), d = g_draws.exchange(0);
    long dd = g_defDraws.exchange(0), ex = g_exec.exchange(0) + g_cExec.exchange(0), ind = g_indirect.exchange(0) + g_cIndI.exchange(0), pr = g_presents.exchange(0);
    char ops[500];
    _snprintf_s(ops, sizeof(ops), _TRUNCATE, "operazioni sul contesto di Revit: pulizie %ld, copie %ld, aggiornamenti %ld, scritture (Map) %ld, "
        "calcolo (Dispatch) %ld, altri disegni %ld, destinazioni impostate %ld, shader impostati %ld, vertici impostati %ld, finestre impostate %ld; ultimo conteggio di disegno %u; riagganci %ld",
        g_cClear.exchange(0), g_cCopy.exchange(0), g_cUpdate.exchange(0), g_cMap.exchange(0),
        g_cDispatch.exchange(0), g_cDrawOther.exchange(0), g_cOMSet.exchange(0), g_cShader.exchange(0), g_cSetVB.exchange(0), g_cSetVP.exchange(0),
        g_lastCount.load(), g_rehooks.exchange(0));
    if (m == 0)
    {
        _snprintf_s(msg, cap, _TRUNCATE, "segnale mai arrivato (disegni: immediati %ld, differiti %ld, indiretti %ld; liste eseguite %ld; Present %ld); %s; %s; swap chain viste: %s", d - dd, dd, ind, ex, pr, ops, g_realInfo, g_scInfo);
        return 0;
    }
    _snprintf_s(msg, cap, _TRUNCATE, "%s (matrice controllata %ld volte, non piu' valida %ld, tenuta perche' la ricerca non ne trovava altre %ld); segnale arrivato %ld volte (disegni: immediati %ld, differiti %ld, indiretti %ld; liste eseguite %ld; Present %ld; %s), thread %s; %s; %s", g_matInfo, g_mChecks, g_mBad, g_mKept, m, d - dd, dd, ind, ex, pr, ops,
        g_markerThread == g_thread ? "di Revit dove gira il nostro disegno" : "diverso dal nostro", g_markerInfo, g_realInfo);
    return 1;
}


// pronto per disegnare con shader: dispositivo di Revit e matrice trovati
extern "C" __declspec(dllexport) int PdShaderReady()
{
    return g_realDev != nullptr && g_mSlot >= 0 && !g_shFailed ? 1 : 0;
}
