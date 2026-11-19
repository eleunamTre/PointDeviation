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

// ---- voce 189: primo disegno con shader nostro. Quando arriva il segnale (Revit sta disegnando il nostro contenuto,
// con la sua destinazione e il suo buffer di profondità), si disegna un cubo rosso di prova in un punto fisso del
// modello, con vertex e pixel shader nostri e la matrice della camera passata dal plugin. Lo stato di Revit si salva
// prima e si rimette dopo. Profondità spenta in questa prova: il cubo resta sempre davanti.
static float g_viewProj[16];
static float g_cubePos[3];
static std::atomic<int> g_testOn{ 0 };
static std::atomic<long> g_testDrawn{ 0 };
static char g_testErr[200] = "";
static ID3D11VertexShader* g_vs = nullptr;
static ID3D11PixelShader* g_ps = nullptr;
static ID3D11InputLayout* g_il = nullptr;
static ID3D11Buffer* g_vb = nullptr;
static ID3D11Buffer* g_ib = nullptr;
static ID3D11Buffer* g_cb = nullptr;
static ID3D11DepthStencilState* g_dss = nullptr;
static ID3D11RasterizerState* g_rs = nullptr;
static ID3D11RasterizerState* g_rsWire = nullptr;
static ID3D11BlendState* g_bs = nullptr;
static bool g_testFailed = false;

static const char* g_hlsl =
"cbuffer C : register(b0) { row_major float4x4 VP; float4 Col; int4 Info; };\n"
"cbuffer R : register(b1) { float4 Rv[4096]; };\n"
"float4 VS(float3 p : POSITION) : SV_Position {\n"
"  float4 q = float4(p, 1);\n"
"  if (Info.x >= 1) {\n"
"    int o = Info.y;\n"
"    float4x4 A = float4x4(Rv[o], Rv[o + 1], Rv[o + 2], Rv[o + 3]);\n"
"    q = (Info.z & 1) != 0 ? mul(A, q) : mul(q, A);\n"
"    if (Info.x == 2) {\n"
"      int o2 = Info.w;\n"
"      float4x4 B = float4x4(Rv[o2], Rv[o2 + 1], Rv[o2 + 2], Rv[o2 + 3]);\n"
"      q = (Info.z & 2) != 0 ? mul(B, q) : mul(q, B);\n"
"    }\n"
"    return q;\n"
"  }\n"
"  return mul(q, VP); }\n"
"float4 PS() : SV_Target { return Col; }\n";

static bool TestInit(ID3D11Device* dev)
{
    if (g_vs) return true;
    if (g_testFailed) return false;
    ID3DBlob *vb = nullptr, *pb = nullptr, *err = nullptr;
    HRESULT hr = D3DCompile(g_hlsl, strlen(g_hlsl), "pd", nullptr, nullptr, "VS", "vs_4_0", 0, 0, &vb, &err);
    if (FAILED(hr)) { _snprintf_s(g_testErr, sizeof(g_testErr), _TRUNCATE, "shader VS: %s", err ? (char*)err->GetBufferPointer() : "?"); g_testFailed = true; return false; }
    hr = D3DCompile(g_hlsl, strlen(g_hlsl), "pd", nullptr, nullptr, "PS", "ps_4_0", 0, 0, &pb, &err);
    if (FAILED(hr)) { _snprintf_s(g_testErr, sizeof(g_testErr), _TRUNCATE, "shader PS: %s", err ? (char*)err->GetBufferPointer() : "?"); g_testFailed = true; return false; }
    dev->CreateVertexShader(vb->GetBufferPointer(), vb->GetBufferSize(), nullptr, &g_vs);
    dev->CreatePixelShader(pb->GetBufferPointer(), pb->GetBufferSize(), nullptr, &g_ps);
    D3D11_INPUT_ELEMENT_DESC el = { "POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D11_INPUT_PER_VERTEX_DATA, 0 };
    dev->CreateInputLayout(&el, 1, vb->GetBufferPointer(), vb->GetBufferSize(), &g_il);
    vb->Release(); pb->Release();
    float h = 10.0f; // voce 193: cubo di 20 piedi di lato (visibile anche da lontano), centrato in g_cubePos (le coordinate si aggiungono nello shader tramite VP? no: qui)
    float v[24] = { -h,-h,-h,  h,-h,-h,  h,h,-h,  -h,h,-h,  -h,-h,h,  h,-h,h,  h,h,h,  -h,h,h };
    for (int i = 0; i < 8; i++) { v[i * 3] += g_cubePos[0]; v[i * 3 + 1] += g_cubePos[1]; v[i * 3 + 2] += g_cubePos[2]; }
    unsigned short idx[36] = { 0,1,2, 0,2,3, 4,6,5, 4,7,6, 0,4,5, 0,5,1, 1,5,6, 1,6,2, 2,6,7, 2,7,3, 3,7,4, 3,4,0 };
    D3D11_BUFFER_DESC bd = {}; D3D11_SUBRESOURCE_DATA sd = {};
    bd.ByteWidth = sizeof(v); bd.Usage = D3D11_USAGE_IMMUTABLE; bd.BindFlags = D3D11_BIND_VERTEX_BUFFER; sd.pSysMem = v;
    dev->CreateBuffer(&bd, &sd, &g_vb);
    bd.ByteWidth = sizeof(idx); bd.BindFlags = D3D11_BIND_INDEX_BUFFER; sd.pSysMem = idx;
    dev->CreateBuffer(&bd, &sd, &g_ib);
    bd.ByteWidth = 96; bd.Usage = D3D11_USAGE_DYNAMIC; bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER; bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    dev->CreateBuffer(&bd, nullptr, &g_cb);
    D3D11_DEPTH_STENCIL_DESC dd = {}; dd.DepthEnable = FALSE; dd.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ZERO; dd.DepthFunc = D3D11_COMPARISON_ALWAYS;
    dev->CreateDepthStencilState(&dd, &g_dss);
    D3D11_RASTERIZER_DESC rd = {}; rd.FillMode = D3D11_FILL_SOLID; rd.CullMode = D3D11_CULL_NONE; rd.DepthClipEnable = FALSE;
    dev->CreateRasterizerState(&rd, &g_rs);
    rd.FillMode = D3D11_FILL_WIREFRAME;
    dev->CreateRasterizerState(&rd, &g_rsWire);
    D3D11_BLEND_DESC bld = {}; bld.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
    dev->CreateBlendState(&bld, &g_bs);
    if (!g_vs || !g_ps || !g_il || !g_vb || !g_ib || !g_cb || !g_dss || !g_rs || !g_bs)
    {
        _snprintf_s(g_testErr, sizeof(g_testErr), _TRUNCATE, "creazione delle risorse non riuscita");
        g_testFailed = true; return false;
    }
    return true;
}

// ---- voce 190: la matrice di Revit. Quando Revit disegna il nostro segnale, nei constant buffer del suo vertex shader
// c'è la matrice con cui trasforma le coordinate del nostro contenuto. La si cerca confrontando, per alcuni punti
// attorno al cubo, la posizione a schermo che dà ogni gruppo di 16 numeri con quella data dalla nostra matrice.
// Trovata (slot, posizione, verso), il cubo si disegna con quella matrice letta direttamente dal buffer di Revit
// (nessuna copia per fotogramma) e con il buffer di profondità di Revit acceso nel verso giusto.
static int g_mSlot = -1, g_mOff = 0, g_mT = 0, g_mRev = 0, g_mOff2 = -1, g_mFound = 0;
static long g_mTries = 0;
static char g_matInfo[300] = "matrice di Revit non ancora cercata";
static char g_dump[6000] = "";
static int g_dumped = 0;
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
static UINT g_mWidth = 0;
static int g_mForce = 0;
// voce 198: la disposizione del buffer di Revit cambia da vista a vista (la matrice era alla riga 15 nella vista con il
// riquadro di sezione; in {3D} senza riquadro i punti e il cubo non comparivano): a ogni cambio di vista, o se cambia
// la dimensione del buffer, la matrice si cerca di nuovo.
extern "C" __declspec(dllexport) void PdSetView(long long viewId)
{
    if (viewId != g_mView) { g_mView = viewId; g_mSlot = -1; g_mForce = 1; }
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
            // voce 191: contenuto del buffer e camera, una volta, per capire a mano quale sia la matrice
            if (!g_dumped && slot == 0)
            {
                g_dumped = 1;
                int k = _snprintf_s(g_dump, sizeof(g_dump), _TRUNCATE, "camera: E %.3f %.3f %.3f D %.4f %.4f %.4f R %.4f %.4f %.4f T %.3f HW %.3f %s; cubo %.3f %.3f %.3f; b0:",
                    g_camE[0], g_camE[1], g_camE[2], g_camD[0], g_camD[1], g_camD[2], g_camR[0], g_camR[1], g_camR[2], g_camT, g_camHW, g_camPersp ? "prospettiva" : "parallela",
                    g_cubePos[0], g_cubePos[1], g_cubePos[2]);
                int nf = (int)(d.ByteWidth / 4); if (nf > 256) nf = 256;
                for (int q = 0; q < nf && k < (int)sizeof(g_dump) - 40; q++)
                    k += _snprintf_s(g_dump + k, sizeof(g_dump) - k, _TRUNCATE, "%s%.6g", (q % 4) == 0 ? (q ? " | " : " ") : " ", f[q]);
            }
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

// matrice di Revit valida per questo disegno? Se il buffer ha cambiato dimensione o è cambiata la vista si cerca di nuovo.
static bool EnsureMatrix(ID3D11DeviceContext* c)
{
    if (g_mSlot >= 0)
    {
        ID3D11Buffer* b = nullptr; c->VSGetConstantBuffers(g_mSlot, 1, &b);
        UINT w = 0; if (b) { D3D11_BUFFER_DESC d; b->GetDesc(&d); w = d.ByteWidth; b->Release(); }
        if (w != g_mWidth) { g_mSlot = -1; g_mForce = 1; }
    }
    if (g_mSlot < 0 && (g_mForce || (g_markers.load() % 30) == 1)) { g_mForce = 0; FindRevitMatrix(c); }
    return g_mSlot >= 0;
}

static void TestDraw(ID3D11DeviceContext* c)
{
    ID3D11Device* dev = nullptr; c->GetDevice(&dev);
    if (!dev) return;
    bool ok = TestInit(dev);
    dev->Release();
    if (!ok) return;
    EnsureMatrix(c);
    ID3D11Buffer* rcb = nullptr;
    if (g_mSlot >= 0) c->VSGetConstantBuffers(g_mSlot, 1, &rcb);   // buffer di Revit con la matrice di questo disegno
    // stato di Revit
    ID3D11InputLayout* il = nullptr; c->IAGetInputLayout(&il);
    ID3D11Buffer* vb = nullptr; UINT vs = 0, vo = 0; c->IAGetVertexBuffers(0, 1, &vb, &vs, &vo);
    ID3D11Buffer* ib = nullptr; DXGI_FORMAT ifmt; UINT io = 0; c->IAGetIndexBuffer(&ib, &ifmt, &io);
    D3D11_PRIMITIVE_TOPOLOGY topo; c->IAGetPrimitiveTopology(&topo);
    ID3D11VertexShader* ovs = nullptr; c->VSGetShader(&ovs, nullptr, nullptr);
    ID3D11PixelShader* ops = nullptr; c->PSGetShader(&ops, nullptr, nullptr);
    ID3D11GeometryShader* ogs = nullptr; c->GSGetShader(&ogs, nullptr, nullptr);
    ID3D11Buffer* vcbs[2] = {}; c->VSGetConstantBuffers(0, 2, vcbs);
    ID3D11Buffer* pcb = nullptr; c->PSGetConstantBuffers(0, 1, &pcb);
    ID3D11DepthStencilState* dss = nullptr; UINT sref = 0; c->OMGetDepthStencilState(&dss, &sref);
    ID3D11RasterizerState* rs = nullptr; c->RSGetState(&rs);
    ID3D11BlendState* bs = nullptr; FLOAT bf[4]; UINT sm = 0; c->OMGetBlendState(&bs, bf, &sm);
    // nostro disegno
    D3D11_MAPPED_SUBRESOURCE m;
    if (SUCCEEDED(c->Map(g_cb, 0, D3D11_MAP_WRITE_DISCARD, 0, &m)))
    {
        float* f = (float*)m.pData;
        memcpy(f, g_viewProj, 64);
        f[16] = 1.0f; f[17] = 0.1f; f[18] = 0.1f; f[19] = 1.0f;
        int* info = (int*)(f + 20);
        info[0] = rcb ? (g_mOff2 >= 0 ? 2 : 1) : 0; info[1] = g_mOff; info[2] = g_mT; info[3] = g_mOff2 >= 0 ? g_mOff2 : 0;
        c->Unmap(g_cb, 0);
    }
    UINT stride = 12, off = 0;
    c->IASetInputLayout(g_il);
    c->IASetVertexBuffers(0, 1, &g_vb, &stride, &off);
    c->IASetIndexBuffer(g_ib, DXGI_FORMAT_R16_UINT, 0);
    c->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    c->VSSetShader(g_vs, nullptr, 0);
    c->PSSetShader(g_ps, nullptr, 0);
    c->GSSetShader(nullptr, nullptr, 0);
    ID3D11Buffer* ours[2] = { g_cb, rcb };
    c->VSSetConstantBuffers(0, 2, ours);
    c->PSSetConstantBuffers(0, 1, &g_cb);
    // voce 192: con la matrice di Revit la profondita' si confronta con il buffer di Revit (verso normale: dal buffer del
    // 19/11 12:03 la riga di profondita' cresce lungo la direzione di vista)
    c->OMSetDepthStencilState(rcb && g_dssLess ? g_dssLess : g_dss, 0);
    c->RSSetState(g_rs);
    FLOAT zero[4] = { 0, 0, 0, 0 };
    c->OMSetBlendState(g_bs, zero, 0xffffffff);
    c->DrawIndexed(36, 0, 0);
    // voce 193: seconda passata in verde a fil di ferro senza profondita': mostra sempre dove sta il cubo, anche se il
    // modello lo copre (il pieno rosso compare solo dove il cubo e' davanti)
    if (rcb && g_rsWire)
    {
        D3D11_MAPPED_SUBRESOURCE m2;
        if (SUCCEEDED(c->Map(g_cb, 0, D3D11_MAP_WRITE_DISCARD, 0, &m2)))
        {
            float* f = (float*)m2.pData;
            memcpy(f, g_viewProj, 64);
            f[16] = 0.1f; f[17] = 0.9f; f[18] = 0.1f; f[19] = 1.0f;
            int* info = (int*)(f + 20);
            info[0] = g_mOff2 >= 0 ? 2 : 1; info[1] = g_mOff; info[2] = g_mT; info[3] = g_mOff2 >= 0 ? g_mOff2 : 0;
            c->Unmap(g_cb, 0);
        }
        c->OMSetDepthStencilState(g_dss, 0);
        c->RSSetState(g_rsWire);
        c->DrawIndexed(36, 0, 0);
    }
    g_testDrawn++;
    // stato di Revit di nuovo al suo posto
    c->IASetInputLayout(il);
    c->IASetVertexBuffers(0, 1, &vb, &vs, &vo);
    c->IASetIndexBuffer(ib, ifmt, io);
    c->IASetPrimitiveTopology(topo);
    c->VSSetShader(ovs, nullptr, 0);
    c->PSSetShader(ops, nullptr, 0);
    c->GSSetShader(ogs, nullptr, 0);
    c->VSSetConstantBuffers(0, 2, vcbs);
    c->PSSetConstantBuffers(0, 1, &pcb);
    c->OMSetDepthStencilState(dss, sref);
    c->RSSetState(rs);
    c->OMSetBlendState(bs, bf, sm);
    if (il) il->Release(); if (vb) vb->Release(); if (ib) ib->Release();
    if (ovs) ovs->Release(); if (ops) ops->Release(); if (ogs) ogs->Release();
    if (vcbs[0]) vcbs[0]->Release(); if (vcbs[1]) vcbs[1]->Release(); if (pcb) pcb->Release(); if (rcb) rcb->Release();
    if (dss) dss->Release(); if (rs) rs->Release(); if (bs) bs->Release();
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
static long long g_shBytes = 0;
static long g_shUploads = 0, g_shDrawn = 0, g_shPoints = 0, g_shFrames = 0;
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
"cbuffer C : register(b0) { int4 Info; float4 P; };\n"
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
"  float4 cl = (Info.z & 1) != 0 ? mul(M, q) : mul(q, M);\n"
"  float3 rx = (Info.z & 1) != 0 ? Rv[r].xyz : float3(Rv[r].x, Rv[r + 1].x, Rv[r + 2].x);\n"
"  float rpx = P.x * length(rx) / max(abs(cl.w), 1e-6) * P.y * 0.5;\n"
"  rpx = clamp(rpx, 0.75, P.w);\n"
"  float2 cn = float2((cr & 1) != 0 ? 1.0 : -1.0, (cr & 2) != 0 ? 1.0 : -1.0);\n"
"  cl.xy += cn * float2(rpx * 2.0 / P.y, rpx * 2.0 / P.z) * cl.w;\n"
"  o.pos = cl; o.uv = cn;\n"
"  o.c = float4((d.w & 255) / 255.0, ((d.w >> 8) & 255) / 255.0, ((d.w >> 16) & 255) / 255.0, 1);\n"
"  float dd[6];\n"
"  [unroll] for (int k = 0; k < 6; k++) { float ln = length(Rv[k].xyz); dd[k] = (Info.w != 0 && abs(ln - 1) < 0.01) ? dot(Rv[k].xyz, p) + Rv[k].w : 1.0; }\n"
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
    D3D11_BUFFER_DESC bd = {}; bd.ByteWidth = 32; bd.Usage = D3D11_USAGE_DYNAMIC; bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER; bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    dev->CreateBuffer(&bd, nullptr, &g_scb);
    D3D11_DEPTH_STENCIL_DESC dd = {}; dd.DepthEnable = TRUE; dd.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ALL; dd.DepthFunc = D3D11_COMPARISON_LESS_EQUAL;
    dev->CreateDepthStencilState(&dd, &g_sdss);
    D3D11_RASTERIZER_DESC rd = {}; rd.FillMode = D3D11_FILL_SOLID; rd.CullMode = D3D11_CULL_NONE; rd.DepthClipEnable = TRUE;
    dev->CreateRasterizerState(&rd, &g_srs);
    if (!g_bs) { D3D11_BLEND_DESC bld = {}; bld.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL; dev->CreateBlendState(&bld, &g_bs); }
    if (!g_svs || !g_sps || !g_quadIb || !g_scb || !g_sdss || !g_srs || !g_bs)
    { _snprintf_s(g_shErr, sizeof(g_shErr), _TRUNCATE, "creazione delle risorse dello shader dei punti non riuscita"); g_shFailed = true; return false; }
    return true;
}

static std::atomic<int> g_shaderWanted{ 0 };
static void ShaderDraw(ID3D11DeviceContext* c)
{
    // la matrice si cerca anche prima che il plugin passi i blocchi (il plugin aspetta che sia trovata)
    if (!EnsureMatrix(c)) return;
    if (!g_shaderOn || g_ready.empty()) return;
    ID3D11Device* dev = nullptr; c->GetDevice(&dev);
    if (!dev) return;
    bool ok = ShaderInit(dev);
    dev->Release();
    if (!ok) return;
    ID3D11Buffer* rcb = nullptr; c->VSGetConstantBuffers(g_mSlot, 1, &rcb);
    if (!rcb) return;
    D3D11_VIEWPORT vp; UINT nv = 1; c->RSGetViewports(&nv, &vp);
    if (nv == 0 || vp.Width < 1) { rcb->Release(); return; }
    // stato di Revit
    ID3D11InputLayout* il = nullptr; c->IAGetInputLayout(&il);
    ID3D11Buffer* ib = nullptr; DXGI_FORMAT ifmt; UINT io = 0; c->IAGetIndexBuffer(&ib, &ifmt, &io);
    D3D11_PRIMITIVE_TOPOLOGY topo; c->IAGetPrimitiveTopology(&topo);
    ID3D11VertexShader* ovs = nullptr; c->VSGetShader(&ovs, nullptr, nullptr);
    ID3D11PixelShader* ops = nullptr; c->PSGetShader(&ops, nullptr, nullptr);
    ID3D11GeometryShader* ogs = nullptr; c->GSGetShader(&ogs, nullptr, nullptr);
    ID3D11Buffer* vcbs[2] = {}; c->VSGetConstantBuffers(0, 2, vcbs);
    ID3D11ShaderResourceView* vsrv = nullptr; c->VSGetShaderResources(0, 1, &vsrv);
    ID3D11DepthStencilState* dss = nullptr; UINT sref = 0; c->OMGetDepthStencilState(&dss, &sref);
    ID3D11RasterizerState* rs = nullptr; c->RSGetState(&rs);
    ID3D11BlendState* bs = nullptr; FLOAT bf[4]; UINT sm = 0; c->OMGetBlendState(&bs, bf, &sm);
    // nostro disegno
    c->IASetInputLayout(nullptr);
    c->IASetIndexBuffer(g_quadIb, DXGI_FORMAT_R32_UINT, 0);
    c->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    c->VSSetShader(g_svs, nullptr, 0);
    c->PSSetShader(g_sps, nullptr, 0);
    c->GSSetShader(nullptr, nullptr, 0);
    ID3D11Buffer* ours[2] = { g_scb, rcb };
    c->VSSetConstantBuffers(0, 2, ours);
    c->OMSetDepthStencilState(g_sdss, 0);
    c->RSSetState(g_srs);
    FLOAT zero[4] = { 0, 0, 0, 0 };
    c->OMSetBlendState(g_bs, zero, 0xffffffff);
    // voce 196: piani del riquadro spenti (con la 195 la nuvola compariva nella vista iniziale ma non in {3D}); il
    // riquadro di sezione resta applicato a blocchi dal motore
    int planes = 0;
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
                in[0] = 1; in[1] = g_mOff; in[2] = g_mT & 1; in[3] = planes;
                f[4] = it.spacing * g_shScale; f[5] = vp.Width; f[6] = vp.Height; f[7] = g_shMaxPx;
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
    // stato di Revit di nuovo al suo posto
    c->IASetInputLayout(il);
    c->IASetIndexBuffer(ib, ifmt, io);
    c->IASetPrimitiveTopology(topo);
    c->VSSetShader(ovs, nullptr, 0);
    c->PSSetShader(ops, nullptr, 0);
    c->GSSetShader(ogs, nullptr, 0);
    c->VSSetConstantBuffers(0, 2, vcbs);
    c->VSSetShaderResources(0, 1, &vsrv);
    c->OMSetDepthStencilState(dss, sref);
    c->RSSetState(rs);
    c->OMSetBlendState(bs, bf, sm);
    if (il) il->Release(); if (ib) ib->Release();
    if (ovs) ovs->Release(); if (ops) ops->Release(); if (ogs) ogs->Release();
    if (vcbs[0]) vcbs[0]->Release(); if (vcbs[1]) vcbs[1]->Release(); if (vsrv) vsrv->Release();
    if (dss) dss->Release(); if (rs) rs->Release(); if (bs) bs->Release();
    rcb->Release();
}

static long long NodeKey(int layer, int node) { return ((long long)layer << 32) | (unsigned int)node; }

// Buffer di un blocco: data = count × (x, y, z float + colore RGBA 4 byte), coordinate del modello in piedi.
extern "C" __declspec(dllexport) int PdShaderUpload(int layer, int node, const void* data, int count)
{
    if (!g_realDev || count <= 0) return 0;
    long long k = NodeKey(layer, node);
    auto f = g_nodes.find(k);
    if (f != g_nodes.end())
    {
        if (f->second.srv) f->second.srv->Release();
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
    if (f->second.srv) f->second.srv->Release();
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

extern "C" __declspec(dllexport) int PdShaderReport(char* msg, int cap)
{
    _snprintf_s(msg, cap, _TRUNCATE, "disegno con shader: %ld fotogrammi, %ld blocchi disegnati (%ld punti), %ld blocchi caricati nella scheda video, %zu blocchi pronti (%.0f MB)%s%s",
        g_shFrames, g_shDrawn, g_shPoints, g_shUploads, g_nodes.size(), g_shBytes / 1048576.0, g_shErr[0] ? "; errore: " : "", g_shErr);
    g_shFrames = 0; g_shDrawn = 0; g_shPoints = 0; g_shUploads = 0;
    return 1;
}

static std::atomic<int> g_inTest{ 0 };

extern "C" __declspec(dllexport) void PdSetTest(int on, const float* viewProj, const float* cubePos)
{
    memcpy(g_viewProj, viewProj, 64);
    if (!g_vs) { g_cubePos[0] = cubePos[0]; g_cubePos[1] = cubePos[1]; g_cubePos[2] = cubePos[2]; }
    g_testOn = on;
}

static std::atomic<UINT> g_lastCount{ 0 };
static void Marker(ID3D11DeviceContext* c, UINT count)
{
    g_draws++;
    g_lastCount.store(count, std::memory_order_relaxed);
    if (count != MarkerIndices) return;
    long n = ++g_markers;
    if (g_shaderWanted && !g_inTest.exchange(1)) { ShaderDraw(c); g_inTest = 0; }
    else if (g_testOn && !g_inTest.exchange(1)) { TestDraw(c); g_inTest = 0; }
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
    long td = g_testDrawn.exchange(0);
    _snprintf_s(msg, cap, _TRUNCATE, "cubo di prova disegnato %ld volte%s%s (%s); segnale arrivato %ld volte (disegni: immediati %ld, differiti %ld, indiretti %ld; liste eseguite %ld; Present %ld; %s), thread %s; %s; %s", td, g_testErr[0] ? ", errore: " : "", g_testErr, g_matInfo, m, d - dd, dd, ind, ex, pr, ops,
        g_markerThread == g_thread ? "di Revit dove gira il nostro disegno" : "diverso dal nostro", g_markerInfo, g_realInfo);
    return 1;
}

extern "C" __declspec(dllexport) int PdDump(char* msg, int cap)
{
    if (!g_dump[0]) return 0;
    strncpy_s(msg, cap, g_dump, _TRUNCATE);
    g_dump[0] = 0;
    return 1;
}

// pronto per disegnare con shader: dispositivo di Revit e matrice trovati
extern "C" __declspec(dllexport) int PdShaderReady()
{
    return g_realDev != nullptr && g_mSlot >= 0 && !g_shFailed ? 1 : 0;
}
