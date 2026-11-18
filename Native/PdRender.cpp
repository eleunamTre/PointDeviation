// PdRender: libreria nativa sperimentale di PointDeviation (voce 180).
// Primo passo: verifica se, durante il disegno del plugin dentro Revit, si può intercettare il contesto
// DirectX 11 che Revit sta usando (con la sua destinazione e il suo buffer di profondità), per poterci poi
// disegnare i punti con shader propri. Questa versione NON disegna nulla: osserva e riferisce.
//
// Compilazione: aprire «x64 Native Tools Command Prompt for VS» e lanciare build.bat in questa cartella.

#include <windows.h>
#include <d3d11.h>
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

static void Marker(ID3D11DeviceContext* c, UINT count)
{
    g_draws++;
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
static char g_realInfo[400] = "";
static void STDMETHODCALLTYPE HookRealDI(ID3D11DeviceContext* c, UINT a, UINT b, INT d) { Marker(c, a); Capture(c); g_realDI(c, a, b, d); }
static void STDMETHODCALLTYPE HookRealD(ID3D11DeviceContext* c, UINT a, UINT b) { Marker(c, a); Capture(c); g_realD(c, a, b); }
static void STDMETHODCALLTYPE HookRealDII(ID3D11DeviceContext* c, UINT a, UINT b, UINT d, INT e, UINT f) { Marker(c, a); Capture(c); g_realDII(c, a, b, d, e, f); }
static void STDMETHODCALLTYPE HookRealDInst(ID3D11DeviceContext* c, UINT a, UINT b, UINT d, UINT e) { Marker(c, a); Capture(c); g_realDInst(c, a, b, d, e); }
static void STDMETHODCALLTYPE HookRDefDI(ID3D11DeviceContext* c, UINT a, UINT b, INT d) { g_defDraws++; Marker(c, a); g_rdefDI(c, a, b, d); }
static void STDMETHODCALLTYPE HookRDefDII(ID3D11DeviceContext* c, UINT a, UINT b, UINT d, INT e, UINT f) { g_defDraws++; Marker(c, a); g_rdefDII(c, a, b, d, e, f); }

static void OnRealSwapChain(IDXGISwapChain* s)
{
    if (g_realDone.exchange(1)) return;
    int n = 0;
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
            g_realDI = (DrawIndexed_t)Patch(vt, 12, (void*)&HookRealDI);
            g_realD = (Draw_t)Patch(vt, 13, (void*)&HookRealD);
            g_realDII = (DrawIndexedInst_t)Patch(vt, 20, (void*)&HookRealDII);
            g_realDInst = (DrawInst_t)Patch(vt, 21, (void*)&HookRealDInst);
            n += _snprintf_s(g_realInfo + n, sizeof(g_realInfo) - n, _TRUNCATE, "; contesto con tabella propria, agganciato %s", g_realDI ? "si'" : "NO");
        }
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
    long dd = g_defDraws.exchange(0), ex = g_exec.exchange(0), ind = g_indirect.exchange(0), pr = g_presents.exchange(0);
    if (m == 0)
    {
        _snprintf_s(msg, cap, _TRUNCATE, "segnale mai arrivato (disegni: immediati %ld, differiti %ld, indiretti %ld; liste eseguite %ld; Present %ld); %s", d - dd, dd, ind, ex, pr, g_realInfo);
        return 0;
    }
    _snprintf_s(msg, cap, _TRUNCATE, "segnale arrivato %ld volte (disegni: immediati %ld, differiti %ld, indiretti %ld; liste eseguite %ld; Present %ld), thread %s; %s; %s", m, d - dd, dd, ind, ex, pr,
        g_markerThread == g_thread ? "di Revit dove gira il nostro disegno" : "diverso dal nostro", g_markerInfo, g_realInfo);
    return 1;
}
