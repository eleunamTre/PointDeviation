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

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "d3dcompiler.lib")

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
static float Score(const float* A, int ta, const float* B, int tb)
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
    if (!(fabsf(x) <= 1.5f && fabsf(y) <= 1.5f && z >= -0.02f && z <= 1.02f)) return -1;
    float dx = fabsf(cs[0] / cs[3] - x);
    if (!(dx > 0.3f && dx < 3.0f)) return -1;                    // mezza finestra a destra: circa 1 in coordinate dello schermo
    if (g_camPersp) { if (!(fabsf(ce[3]) < 0.02f * ct[3])) return -1; }   // prospettiva: w nullo nell'occhio
    else { if (!(fabsf(cf[3] - ct[3]) < 1e-3f * fabsf(ct[3]))) return -1; } // parallela: w costante
    return 10.0f - (fabsf(x) + fabsf(y)) - fabsf(dx - 1.0f);
}

static void FindRevitMatrix(ID3D11DeviceContext* c)
{
    g_mTries++;
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
            for (int o = 0; o < nb; o++)
                for (int t = 0; t < 2; t++)
                {
                    float sc = Score(f + o * 4, t, nullptr, 0);
                    if (sc > best) { best = sc; bSlot = slot; bOff = o; bT = t; bOff2 = -1; }
                }
            if (best < 0)   // nessuna matrice unica: coppie (vista e proiezione separate)
                for (int o = 0; o < nb; o++)
                    for (int o2 = 0; o2 < nb; o2++)
                    {
                        if (o2 >= o && o2 < o + 4) continue;
                        for (int t = 0; t < 2; t++)
                            for (int t2 = 0; t2 < 2; t2++)
                            {
                                float sc = Score(f + o * 4, t, f + o2 * 4, t2);
                                if (sc > best) { best = sc; bSlot = slot; bOff = o; bT = t; bOff2 = o2; bT2 = t2; }
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
    if (best >= 0 && !g_dssLess)
    {
        D3D11_DEPTH_STENCIL_DESC dd = {}; dd.DepthEnable = TRUE; dd.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ZERO;
        dd.DepthFunc = D3D11_COMPARISON_LESS_EQUAL; dev->CreateDepthStencilState(&dd, &g_dssLess);
        dd.DepthFunc = D3D11_COMPARISON_GREATER_EQUAL; dev->CreateDepthStencilState(&dd, &g_dssGreater);
    }
    dev->Release();
}

// voce 199: controllo della matrice trovata. Ogni 30 segnali si rilegge il buffer di Revit e si verifica che la
// matrice alla posizione trovata porti ancora la camera di questo fotogramma al centro dello schermo; se no si cerca
// di nuovo (nuvola che «orbita per conto suo» in {3D}: matrice valida al momento della ricerca ma non dopo).
static long g_mChecks = 0, g_mBad = 0;
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
                ok = Score(f + g_mOff * 4, g_mT & 1, g_mOff2 >= 0 ? f + g_mOff2 * 4 : nullptr, (g_mT >> 1) & 1) >= 0;
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
    if (g_mSlot >= 0)
    {
        ID3D11Buffer* b = nullptr; c->VSGetConstantBuffers(g_mSlot, 1, &b);
        UINT w = 0; if (b) { D3D11_BUFFER_DESC d; b->GetDesc(&d); w = d.ByteWidth; b->Release(); }
        if (w != g_mWidth) { g_mSlot = -1; g_mForce = 1; g_viewGen++; }
        else if ((g_markers.load() % 30) == 0 && !CheckMatrix(c)) { g_mSlot = -1; g_mForce = 1; }
    }
    if (g_mSlot < 0 && (g_mForce || (g_markers.load() % 30) == 1)) { g_mForce = 0; FindRevitMatrix(c); }
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
struct PdNode { ID3D11Buffer* vb; ID3D11ShaderResourceView* srv; int count; };
struct PdItem { ID3D11ShaderResourceView* srv; int count; float spacing; };
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
static bool g_shFailed = false;

// voce 195: i punti si leggono dallo shader (Buffer<uint4>, 16 byte a punto) e si disegnano con DrawIndexed e un
// indice condiviso a 6 indici per punto: è la chiamata già verificata con il cubo. Con l'istanziamento (194) la nuvola
// non compariva. I piani del riquadro si usano solo se sono piani veri (normale lunga 1).
static const char* g_shHlsl =
"cbuffer C : register(b0) { int4 Info; float4 P; float4 Pl[6]; int4 Info2; };\n"
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
"  float3 rx = M[0].xyz;\n"
"  float rpx = P.x * length(rx) / max(abs(cl.w), 1e-6) * P.y * 0.5;\n"
"  rpx = clamp(rpx, 0.75, P.w);\n"
"  float2 cn = float2((cr & 1) != 0 ? 1.0 : -1.0, (cr & 2) != 0 ? 1.0 : -1.0);\n"
"  cl.xy += cn * float2(rpx * 2.0 / P.y, rpx * 2.0 / P.z) * cl.w;\n"
"  o.pos = cl; o.uv = rpx < 1.25 ? cn * 0.5 : cn;\n"
"  o.c = float4((d.w & 255) / 255.0, ((d.w >> 8) & 255) / 255.0, ((d.w >> 16) & 255) / 255.0, asfloat(Info2.z));\n"
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
    D3D11_BUFFER_DESC bd = {}; bd.ByteWidth = 144; bd.Usage = D3D11_USAGE_DYNAMIC; bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER; bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    dev->CreateBuffer(&bd, nullptr, &g_scb);
    D3D11_DEPTH_STENCIL_DESC dd = {}; dd.DepthEnable = TRUE; dd.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ALL; dd.DepthFunc = D3D11_COMPARISON_LESS_EQUAL;
    dev->CreateDepthStencilState(&dd, &g_sdss);
    D3D11_RASTERIZER_DESC rd = {}; rd.FillMode = D3D11_FILL_SOLID; rd.CullMode = D3D11_CULL_NONE; rd.DepthClipEnable = TRUE;
    dev->CreateRasterizerState(&rd, &g_srs);
    if (!g_bs) { D3D11_BLEND_DESC bld = {}; bld.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL; dev->CreateBlendState(&bld, &g_bs); }
    {
        D3D11_BLEND_DESC bld = {}; D3D11_RENDER_TARGET_BLEND_DESC& rt = bld.RenderTarget[0];
        rt.BlendEnable = TRUE; rt.SrcBlend = D3D11_BLEND_SRC_ALPHA; rt.DestBlend = D3D11_BLEND_INV_SRC_ALPHA; rt.BlendOp = D3D11_BLEND_OP_ADD;
        rt.SrcBlendAlpha = D3D11_BLEND_ZERO; rt.DestBlendAlpha = D3D11_BLEND_ONE; rt.BlendOpAlpha = D3D11_BLEND_OP_ADD;
        rt.RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
        dev->CreateBlendState(&bld, &g_bsAlpha);
        dd.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ZERO;
        dev->CreateDepthStencilState(&dd, &g_sdssNoWrite);
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
        g_qPend[k] = false;
        if (dj.Disjoint || dj.Frequency == 0 || e < b) continue;
        double ms = (double)(e - b) * 1000.0 / (double)dj.Frequency;
        g_gpuEma = g_gpuEma < 0 ? ms : g_gpuEma * 0.7 + ms * 0.3;
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
        dev->Release();
        if (!g_qDisj[k] || !g_qBeg[k] || !g_qEnd[k]) return -1;
    }
    c->Begin(g_qDisj[k]);
    c->End(g_qBeg[k]);
    return k;
}
static void GpuEnd(ID3D11DeviceContext* c, int k)
{
    if (k < 0) return;
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

// Disegno dei punti in g_ready con la matrice in matBuf (righe off..off+3, e off2..off2+3 se c'e' la seconda).
static void DrawPointsCore(ID3D11DeviceContext* c, ID3D11Buffer* matBuf, int off, int t, int off2, int t2, const D3D11_VIEWPORT& vp)
{
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
    c->OMSetDepthStencilState(tr ? g_sdssNoWrite : g_sdss, 0);
    c->RSSetState(g_srs);
    FLOAT zero[4] = { 0, 0, 0, 0 };
    c->OMSetBlendState(tr ? g_bsAlpha : g_bs, zero, 0xffffffff);
    float alpha = tr ? g_alpha : 1.0f;
    // voce 200: piani di ritaglio dal plugin (riquadro di sezione, intervallo di vista, campo della sezione letti
    // dalle API di Revit), non piu' dal buffer di Revit (voce 196: li' la loro posizione cambia da vista a vista)
    int planes = g_clipOn;
    float lastSp = -1;
    for (const PdItem& it : g_ready)
    {
        if (!it.srv || it.count <= 0) continue;
        if (it.spacing != lastSp)
        {
            D3D11_MAPPED_SUBRESOURCE m;
            if (SUCCEEDED(c->Map(g_scb, 0, D3D11_MAP_WRITE_DISCARD, 0, &m)))
            {
                int* in = (int*)m.pData; float* f = (float*)m.pData;
                in[0] = 1; in[1] = off; in[2] = t; in[3] = planes;
                f[4] = it.spacing * g_shScale; f[5] = vp.Width; f[6] = vp.Height; f[7] = g_shMaxPx;
                memcpy(f + 8, g_clip, sizeof(g_clip));
                // voce 205: seconda matrice (vista e proiezione separate nel buffer di Revit), come nel cubo di prova
                in[32] = off2; in[33] = t2; f[34] = alpha; in[35] = 0;
                c->Unmap(g_scb, 0);
            }
            lastSp = it.spacing;
        }
        c->VSSetShaderResources(0, 1, &it.srv);
        for (int st0 = 0; st0 < it.count; st0 += QuadMax)
        {
            int nq = it.count - st0 < QuadMax ? it.count - st0 : QuadMax;
            c->DrawIndexed((UINT)nq * 6, 0, st0 * 4);   // l'indice di vertice parte da st0*4: punto st0
        }
        g_shDrawn++; g_shPoints += it.count;
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

// =====================================================================================================================
// Voce 224: diagnostica per la trasparenza nella VistaIniziale (nuvola davanti ai muri solo li'). Ogni 5 s si registra
// la sequenza di un fotogramma (come nella 220): destinazioni impostate T(r#colore d#profondita'), pulizie C/Z, copie K,
// risoluzioni R, disegni dN, SEGNALE, PRESENT. Con la trasparenza accesa, al segnale e al Present si legge anche il
// buffer di profondita' (copia in memoria, una volta ogni 5 s) e si scrive quanta parte della finestra e' coperta da
// qualcosa (profondita' sotto il valore di sfondo) e il valore medio: si vede se al Present il modello c'e' ancora.
// =====================================================================================================================
static bool g_trArm = false, g_trRec = false, g_trDone = false;
static char g_trBuf[3500] = ""; static int g_trLen = 0; static long g_trDraws = 0;
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
    }
    else if (g_trArm && !g_trDone) { g_trArm = false; g_trRec = true; g_trLen = 0; g_trBuf[0] = 0; g_trN = 0; g_trDraws = 0; }
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
static bool LikeRef(const float* raw)
{
    float C[16], R[16]; ColForm(raw, C); ColForm(g_chainGen == g_viewGen ? g_chainRaw : g_refRaw, R);
    float c0 = L3(C), c1 = L3(C + 4), r0 = L3(R), r1 = L3(R + 4);
    if (!(c0 > 0 && c1 > 0 && r0 > 0 && r1 > 0)) return false;
    bool cPar = L3(C + 12) < 1e-6f * c0, rPar = L3(R + 12) < 1e-6f * r0;
    if (cPar != rPar) return false;
    if (fabsf((c0 / c1) / (r0 / r1) - 1) > 0.02f) return false;
    if (fabsf(C[0] * C[4] + C[1] * C[5] + C[2] * C[6]) > 0.02f * c0 * c1) return false;
    float k = c0 / r0;
    if (!(k > 0.75f && k < 1.33f)) return false;
    // voce 214: stessa scala ma spostata (tassello disegnato con la vista traslata: nuvola in basso a sinistra). Il punto
    // guardato a schermo non puo' saltare di oltre un quarto della finestra da un fotogramma all'altro (voce 216: prima il
    // centro della nuvola, che in prospettiva poteva stare dietro la camera).
    float pc[4], pr[4], q[4] = { g_focus[0], g_focus[1], g_focus[2], 1 };
    for (int r = 0; r < 4; r++) { pc[r] = C[r * 4] * q[0] + C[r * 4 + 1] * q[1] + C[r * 4 + 2] * q[2] + C[r * 4 + 3]; pr[r] = R[r * 4] * q[0] + R[r * 4 + 1] * q[1] + R[r * 4 + 2] * q[2] + R[r * 4 + 3]; }
    if (!(pc[3] > 1e-6f && pr[3] > 1e-6f)) return false;
    return fabsf(pc[0] / pc[3] - pr[0] / pr[3]) < 0.5f && fabsf(pc[1] / pc[3] - pr[1] / pr[3]) < 0.5f;
}
static int BestValidCand()
{
    int b = -1;
    for (int i = 0; i < g_nCand; i++)
    {
        if (!LikeRef(g_cand[i].m)) { g_camRejected++; continue; }
        if (b < 0 || g_cand[i].n > g_cand[b].n) b = i;
    }
    return b;
}

static void ShaderDraw(ID3D11DeviceContext* c)
{
    // la matrice si cerca anche prima che il plugin passi i blocchi (il plugin aspetta che sia trovata)
    if (!EnsureMatrix(c)) { g_skipMatrix++; return; }
    // voce 209: buffer della matrice, destinazione e profondita' di questo fotogramma normale, per i fotogrammi interrotti
    {
        ID3D11Buffer* b = nullptr; c->VSGetConstantBuffers(g_mSlot, 1, &b);
        if (b) { g_capBuf = b; b->Release(); }
        if (g_hasLastCap) { memcpy(g_refRaw, g_lastCap, sizeof(g_refRaw)); g_hasRef = true; g_refGen = g_viewGen; memcpy(g_chainRaw, g_lastCap, sizeof(g_chainRaw)); g_chainGen = g_viewGen; }
        ID3D11RenderTargetView* rtv = nullptr; ID3D11DepthStencilView* dsv = nullptr;
        c->OMGetRenderTargets(1, &rtv, &dsv);
        if (rtv) rtv->Release();
        if (g_fbDsv != dsv) { if (g_fbDsv) g_fbDsv->Release(); g_fbDsv = dsv; } else if (dsv) dsv->Release();
        UINT nv = 1; c->RSGetViewports(&nv, &g_fbVp);
        g_drawnThisFrame = true;
        g_dzArm = true;
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
    SavedState st; st.Save(c, false);
    DrawPointsCore(c, rcb, g_mOff, g_mT & 1, g_mOff2, (g_mT >> 1) & 1, vp);
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
static bool DrawAtPresent(IDXGISwapChain* s, const float* cam, ID3D11DepthStencilView* dsv)
{
    if (!dsv || !ShaderInit(g_realDev)) return false;
    bool done = false;
    ID3D11Texture2D* bb = nullptr;
    if (FAILED(s->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&bb)) || !bb) return false;
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
    if (c && g_fbMat && SUCCEEDED(g_realDev->CreateRenderTargetView(bb, nullptr, &rtv)) && rtv)
    {
        D3D11_MAPPED_SUBRESOURCE m;
        if (SUCCEEDED(c->Map(g_fbMat, 0, D3D11_MAP_WRITE_DISCARD, 0, &m)))
        {
            memcpy(m.pData, cam, 128);
            c->Unmap(g_fbMat, 0);
            SavedState st; st.Save(c, true);
            c->OMSetRenderTargets(1, &rtv, dsv);
            c->RSSetViewports(1, &g_fbVp);
            DrawPointsCore(c, g_fbMat, 0, g_mT & 1, g_mOff2 >= 0 ? 4 : -1, (g_mT >> 1) & 1, g_fbVp);
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
static void KeepDepth()
{
    ID3D11DeviceContext* c = nullptr; if (g_realDev) g_realDev->GetImmediateContext(&c);
    if (c) { KeepDepthFrom(c); c->Release(); }
}
static void KeepDepthFrom(ID3D11DeviceContext* ctx)
{
    g_keepOk = false;
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
    if (g_keepTex && g_keepDsv && d.MipLevels == 1 && d.ArraySize == 1) { ctx->CopyResource(g_keepTex, r); g_keepOk = true; g_keepCopies++; }
    r->Release();
}
static void FallbackAtPresent(IDXGISwapChain* s)
{
    if (g_trRec && g_drawnThisFrame && g_keepThisFrame && g_keepOk && g_realDev)
    {
        ID3D11DeviceContext* c = nullptr; g_realDev->GetImmediateContext(&c);
        if (c) { TrDepth(c, g_keepDsv, "profondita' copiata all'uscita dalla scena"); c->Release(); }
    }
    TrPresent(s, g_drawnThisFrame, g_fbDsv);
    if (!g_realDev || g_fbVp.Width < 1) return;
    DXGI_SWAP_CHAIN_DESC sd = {};
    if (FAILED(s->GetDesc(&sd))) return;
    if (sd.BufferDesc.Width != (UINT)g_fbVp.Width || sd.BufferDesc.Height != (UINT)g_fbVp.Height) { g_fbOther++; return; }
    if (g_drawnThisFrame)
    {
        g_fbNormal++; g_drawnThisFrame = false; g_fbSwap = s; LearnSearch();
        int b = BestCand();
        if (b >= 0 && g_hasRef) { g_fbCompared++; if (memcmp(g_cand[b].m, g_refRaw, (g_mOff2 >= 0 ? 32 : 16) * 4) == 0) g_fbAgree++; }
        g_nCand = 0; g_cmN = 0;
        // voce 217: con la trasparenza la nuvola del fotogramma completo si disegna qui, sopra il modello finito, con la
        // camera vera di questo fotogramma (al segnale si e' solo presa la matrice)
        // voce 227: profondita' copiata quando Revit ha lasciato il buffer della scena (nella VistaIniziale al Present
        // quel buffer e' gia' stato svuotato); se la copia non c'e' stata, si copia ora come nella 222
        if (!(g_keepThisFrame && g_keepOk) && g_shaderOn && !g_ready.empty()) KeepDepth();
        ID3D11DepthStencilView* dz = g_keepOk ? g_keepDsv : g_fbDsv;
        if (Transparent() && g_shaderOn && !g_ready.empty() && g_hasRef && g_refGen == g_viewGen && DrawAtPresent(s, g_refRaw, dz)) g_fbAlpha++;
        g_keepThisFrame = false; g_dzArm = false;
        return;
    }
    if (s != g_fbSwap) { g_fbOther++; return; }   // voce 215: solo la finestra dell'ultimo fotogramma completo
    g_learn.clear(); g_lmN = 0;   // fotogramma interrotto: niente matrice vera con cui confrontare
    int best = g_hasRef ? BestValidCand() : -1;
    const float* camNow = best >= 0 ? g_cand[best].m : nullptr;
    float camBuf[32];
    if (camNow) { memcpy(camBuf, camNow, sizeof(camBuf)); camNow = camBuf; if (g_refGen == g_viewGen) { memcpy(g_chainRaw, camBuf, sizeof(camBuf)); g_chainGen = g_viewGen; } }
    g_nCand = 0; g_cmN = 0;
    if (!g_shaderOn || g_ready.empty() || !g_fbDsv) return;
    if (!g_hasRef || g_refGen != g_viewGen) { g_fbNoMat++; return; }
    // voce 212: camera del modello di questo fotogramma se trovata, altrimenti l'ultima usata
    if (DrawAtPresent(s, camNow ? camNow : g_chainRaw, g_keepOk ? g_keepDsv : g_fbDsv)) { g_fbDrawn++; if (camNow) g_camUsed++; else g_camStale++; }
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

extern "C" __declspec(dllexport) void PdShaderAdd(int layer, int node, int count, float spacing)
{
    auto f = g_nodes.find(NodeKey(layer, node));
    if (f == g_nodes.end() || !f->second.srv) return;
    if (count > f->second.count) count = f->second.count;
    g_build.push_back(PdItem{ f->second.srv, count, spacing });
}

// Fine dell'elenco del fotogramma: diventa quello da disegnare al prossimo segnale. maxPx = raggio massimo dei punti.
// voce 197: wanted = opzione accesa (la libreria cerca la matrice); on = elenco valido da disegnare. Spegnendo l'opzione
// a Revit aperto l'elenco si svuota: prima restava l'ultimo e si continuava a disegnarlo.
extern "C" __declspec(dllexport) void PdShaderEndFrame(int on, float maxPx, float scale, int wanted)
{
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
}
// voce 223: il plugin non disegna piu' nella vista (nuvola spenta o nascosta): niente da ridisegnare nei fotogrammi
// senza il plugin, altrimenti restava a schermo l'ultima nuvola
extern "C" __declspec(dllexport) void PdViewOff(long long viewId)
{
    if (viewId == g_mView) { g_ready.clear(); g_build.clear(); }
}

extern "C" __declspec(dllexport) int PdShaderReport(char* msg, int cap)
{
    _snprintf_s(msg, cap, _TRUNCATE, "disegno con shader: %ld fotogrammi, %ld blocchi disegnati (%lld punti), %ld blocchi caricati nella scheda video, %zu blocchi pronti (%.0f MB); scheda video per i punti: media %.1f ms, massimo %.1f ms (%ld misure); segnali senza disegno: matrice non pronta %ld, elenco vuoto %ld; finestra della vista: fotogrammi completi %ld (di cui trasparenti disegnati al Present %ld, opacita' %.2f; profondita' tenuta per i fotogrammi interrotti %ld volte), interrotti ridisegnati da noi %ld (con la camera del fotogramma %ld, con quella precedente %ld), camera dedotta uguale a quella vera %ld su %ld fotogrammi completi, matrici scartate (tasselli) %ld, interrotti senza matrice %ld, profondita' diversa %ld; Present di altre finestre %ld; scritture della matrice lette %ld; %s%s%s",
        g_shFrames, g_shDrawn, g_shPoints, g_shUploads, g_nodes.size(), g_shBytes / 1048576.0,
        g_gpuN > 0 ? g_gpuSum / g_gpuN : 0.0, g_gpuMax, g_gpuN, g_skipMatrix, g_skipEmpty,
        g_fbNormal, g_fbAlpha, g_alpha, g_keepCopies, g_fbDrawn, g_camUsed, g_camStale, g_fbAgree, g_fbCompared, g_camRejected, g_fbNoMat, g_fbRejected, g_fbOther, g_capWrites, g_learnInfo[0] ? g_learnInfo : "camera del modello: ricerca non ancora fatta", g_shErr[0] ? "; errore: " : "", g_shErr);
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
static void STDMETHODCALLTYPE HookRealDI(ID3D11DeviceContext* c, UINT a, UINT b, INT d) { if (g_trRec) g_trDraws++; Marker(c, a); Capture(c); g_realDI(c, a, b, d); }
static void STDMETHODCALLTYPE HookRealD(ID3D11DeviceContext* c, UINT a, UINT b) { if (g_trRec) g_trDraws++; Marker(c, a); Capture(c); g_realD(c, a, b); }
static void STDMETHODCALLTYPE HookRealDII(ID3D11DeviceContext* c, UINT a, UINT b, UINT d, INT e, UINT f) { if (g_trRec) g_trDraws++; Marker(c, a); Capture(c); g_realDII(c, a, b, d, e, f); }
static void STDMETHODCALLTYPE HookRealDInst(ID3D11DeviceContext* c, UINT a, UINT b, UINT d, UINT e) { if (g_trRec) g_trDraws++; Marker(c, a); Capture(c); g_realDInst(c, a, b, d, e); }
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
    if (SUCCEEDED(h) && m && (t == D3D11_MAP_WRITE_DISCARD || t == D3D11_MAP_WRITE_NO_OVERWRITE)) { LearnMap(r, m->pData); CamMap(r, m->pData); }
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
    if (g_trRec) { TrFlush(); TrPut(" T(r#%d d#%d)", (n > 0 && r) ? TrView(r[0]) : 0, TrView(d)); }
    // voce 227: dopo il segnale, la prima volta che Revit lascia il buffer di profondita' della scena se ne fa una copia:
    // li' c'e' il modello finito, anche se poi Revit riusa o svuota quel buffer prima del Present (VistaIniziale)
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
    if (leave) { g_dzArm = false; KeepDepthFrom(c); g_keepThisFrame = g_keepOk; if (g_trRec) TrPut(" COPIA-PROF"); }
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

static HRESULT STDMETHODCALLTYPE HookPresent(IDXGISwapChain* s, UINT a, UINT f) { g_presents++; OnRealSwapChain(s); if (!(f & DXGI_PRESENT_TEST)) FallbackAtPresent(s); return g_origPresent(s, a, f); }
static HRESULT STDMETHODCALLTYPE HookPresent1(IDXGISwapChain1* s, UINT a, UINT f, const DXGI_PRESENT_PARAMETERS* p) { g_presents++; OnRealSwapChain(s); if (!(f & DXGI_PRESENT_TEST)) FallbackAtPresent(s); return g_origPresent1(s, a, f, p); }

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
    _snprintf_s(msg, cap, _TRUNCATE, "%s (matrice controllata %ld volte, non piu' valida %ld); segnale arrivato %ld volte (disegni: immediati %ld, differiti %ld, indiretti %ld; liste eseguite %ld; Present %ld; %s), thread %s; %s; %s", g_matInfo, g_mChecks, g_mBad, m, d - dd, dd, ind, ex, pr, ops,
        g_markerThread == g_thread ? "di Revit dove gira il nostro disegno" : "diverso dal nostro", g_markerInfo, g_realInfo);
    return 1;
}


// pronto per disegnare con shader: dispositivo di Revit e matrice trovati
extern "C" __declspec(dllexport) int PdShaderReady()
{
    return g_realDev != nullptr && g_mSlot >= 0 && !g_shFailed ? 1 : 0;
}
