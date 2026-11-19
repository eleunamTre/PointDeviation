// PdRender: libreria nativa sperimentale di PointDeviation (voce 180).
// Primo passo: verifica se, durante il disegno del plugin dentro Revit, si può intercettare il contesto
// DirectX 11 che Revit sta usando (con la sua destinazione e il suo buffer di profondità), per poterci poi
// disegnare i punti con shader propri. Questa versione NON disegna nulla: osserva e riferisce.
//
// Compilazione: aprire «x64 Native Tools Command Prompt for VS» e lanciare build.bat in questa cartella.

#include <windows.h>
#include <d3d11_4.h>
#include <dxgi1_2.h>
#include <atomic>
#include <cstdio>
#include <cstring>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "user32.lib")

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

static std::atomic<UINT> g_lastCount{ 0 };
static void Marker(ID3D11DeviceContext* c, UINT count)
{
    g_draws++;
    g_lastCount.store(count, std::memory_order_relaxed);
    if (count != MarkerIndices) return;
    long n = ++g_markers;
    g_markerThread = GetCurrentThreadId();
    if (n % 30 == 1) DescribeState(c, g_markerInfo, sizeof(g_markerInfo));
}

static void STDMETHODCALLTYPE HookDI(ID3D11DeviceContext* c, UINT a, UINT b, INT d) { Marker(c, a); Capture(c); g_origDI(c, a, b, d); }
static void STDMETHODCALLTYPE HookD(ID3D11DeviceContext* c, UINT a, UINT b) { Marker(c, a); Capture(c); g_origD(c, a, b); }
static void STDMETHODCALLTYPE HookDII(ID3D11DeviceContext* c, UINT a, UINT b, UINT d, INT e, UINT f) { Marker(c, a); Capture(c); g_origDII(c, a, b, d, e, f); }
static void STDMETHODCALLTYPE HookDInst(ID3D11DeviceContext* c, UINT a, UINT b, UINT d, UINT e) { Marker(c, a); Capture(c); g_origDInst(c, a, b, d, e); }

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
static void STDMETHODCALLTYPE HookRealDI(ID3D11DeviceContext* c, UINT a, UINT b, INT d) { Marker(c, a); Capture(c); g_realDI(c, a, b, d); }
static void STDMETHODCALLTYPE HookRealD(ID3D11DeviceContext* c, UINT a, UINT b) { Marker(c, a); Capture(c); g_realD(c, a, b); }
static void STDMETHODCALLTYPE HookRealDII(ID3D11DeviceContext* c, UINT a, UINT b, UINT d, INT e, UINT f) { Marker(c, a); Capture(c); g_realDII(c, a, b, d, e, f); }
static void STDMETHODCALLTYPE HookRealDInst(ID3D11DeviceContext* c, UINT a, UINT b, UINT d, UINT e) { Marker(c, a); Capture(c); g_realDInst(c, a, b, d, e); }
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
static void STDMETHODCALLTYPE HookRClear(ID3D11DeviceContext* c, ID3D11RenderTargetView* v, const FLOAT col[4]) { g_cClear++; g_rClear(c, v, col); }
static void STDMETHODCALLTYPE HookRCopy(ID3D11DeviceContext* c, ID3D11Resource* d, ID3D11Resource* s) { g_cCopy++; g_rCopy(c, d, s); }
static void STDMETHODCALLTYPE HookRCopyReg(ID3D11DeviceContext* c, ID3D11Resource* d, UINT a, UINT x, UINT y, UINT z, ID3D11Resource* s, UINT b, const D3D11_BOX* bx) { g_cCopy++; g_rCopyReg(c, d, a, x, y, z, s, b, bx); }
static void STDMETHODCALLTYPE HookRUpdate(ID3D11DeviceContext* c, ID3D11Resource* r, UINT a, const D3D11_BOX* b, const void* d, UINT p, UINT q) { g_cUpdate++; g_rUpdate(c, r, a, b, d, p, q); }
static HRESULT STDMETHODCALLTYPE HookRMap(ID3D11DeviceContext* c, ID3D11Resource* r, UINT a, D3D11_MAP t, UINT f, D3D11_MAPPED_SUBRESOURCE* m) { g_cMap++; HRESULT h = g_rMap(c, r, a, t, f, m); Rehook(c); return h; }
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
static void STDMETHODCALLTYPE HookROMSet(ID3D11DeviceContext* c, UINT n, ID3D11RenderTargetView* const* r, ID3D11DepthStencilView* d) { g_cOMSet++; g_rOMSet(c, n, r, d); Rehook(c); }
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
    g_rUpdate = (Update_t)Patch(vt, 48, (void*)&HookRUpdate);
    g_rMap = (Map_t)Patch(vt, 14, (void*)&HookRMap);
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

static void OnRealSwapChain(IDXGISwapChain* s)
{
    if (g_realDone.load()) { NoteSwapChain(s); return; }
    g_realDone = 1;
    int n = 0;
    struct AtEnd { IDXGISwapChain* s; ~AtEnd() { NoteSwapChain(s); } } atEnd{ s };
    ID3D11Device* dev = nullptr;
    if (FAILED(s->GetDevice(__uuidof(ID3D11Device), (void**)&dev)) || !dev)
    {
        _snprintf_s(g_realInfo, sizeof(g_realInfo), _TRUNCATE, "la swap chain di Revit non appartiene a un dispositivo DirectX 11");
        return;
    }
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

static HRESULT STDMETHODCALLTYPE HookPresent(IDXGISwapChain* s, UINT a, UINT f) { g_presents++; OnRealSwapChain(s); return g_origPresent(s, a, f); }
static HRESULT STDMETHODCALLTYPE HookPresent1(IDXGISwapChain1* s, UINT a, UINT f, const DXGI_PRESENT_PARAMETERS* p) { g_presents++; OnRealSwapChain(s); return g_origPresent1(s, a, f, p); }

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
    _snprintf_s(msg, cap, _TRUNCATE, "aggancio %s, livello DirectX 0x%X; contesto differito %s; Present %s; librerie grafiche caricate: %s",
        g_hooked ? "attivo" : "NON riuscito", (unsigned)fl, g_defSame ? "stessa tabella" : (g_defDI ? "agganciato" : "no"),
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
    _snprintf_s(msg, cap, _TRUNCATE, "segnale arrivato %ld volte (disegni: immediati %ld, differiti %ld, indiretti %ld; liste eseguite %ld; Present %ld; %s), thread %s; %s; %s", m, d - dd, dd, ind, ex, pr, ops,
        g_markerThread == g_thread ? "di Revit dove gira il nostro disegno" : "diverso dal nostro", g_markerInfo, g_realInfo);
    return 1;
}
