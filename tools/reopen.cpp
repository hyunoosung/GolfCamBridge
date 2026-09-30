// reopen - checks that a DirectShow camera (Golf Cam 1/2) can be opened, closed and opened again in one
// process, the way Foresight Premier does when you leave practice mode and come back.
//
// Each session does what a capture app does: enumerate video devices -> bind by FriendlyName ->
// IAMStreamConfig (caps + SetFormat) -> graph with sample grabber + null renderer -> Run -> count frames
// -> Stop -> release. Needs the bridge (GolfCamBridge.exe) running; no physical camera needed.
//
//   reopen.exe [device] [rounds] [ms]   A: new instance each time   B: same instance, graph rebuilt
//                                       C: new instance while the old one is still held
//                                       D: two instances streaming at once
//   reopen.exe --restart [device]       E: exit and restart the bridge while it runs (it tells you when);
//                                          instances from before / during the outage must reconnect
//
// A session counts as OK only with LIVE frames: mean pixel above the threshold (default 20, set with
// REOPEN_MIN_MEAN). The standby image is ~31; softcam's "sender gone" placeholder is that darkened to ~7.
// ponytail: brightness is a crude liveness test. With a real camera in a dark room, set REOPEN_MIN_MEAN=0.
//
// Exit code 0 = all sessions OK. Build: tools\build-tools.ps1 (-> build\tools\reopen.exe)
#include <windows.h>
#include <dshow.h>
#include <cstdio>
#include <atomic>
#include <string>

// qedit.h is gone from the modern SDK; declare the two interfaces we need.
static const CLSID CLSID_SampleGrabber_ = {0xC1F400A0, 0x3F08, 0x11d3, {0x9F, 0x0B, 0x00, 0x60, 0x08, 0x03, 0x9E, 0x37}};
static const CLSID CLSID_NullRenderer_  = {0xC1F400A4, 0x3F08, 0x11d3, {0x9F, 0x0B, 0x00, 0x60, 0x08, 0x03, 0x9E, 0x37}};
static const IID IID_ISampleGrabber_    = {0x6B652FFF, 0x11FE, 0x4fce, {0x92, 0xAD, 0x02, 0x66, 0xB5, 0xD7, 0xC7, 0x8F}};
static const IID IID_ISampleGrabberCB_  = {0x0579154A, 0x2B53, 0x4994, {0xB0, 0xD0, 0xE7, 0x73, 0x14, 0x8E, 0xFF, 0x85}};

struct ISampleGrabberCB_ : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE SampleCB(double, IMediaSample*) = 0;
    virtual HRESULT STDMETHODCALLTYPE BufferCB(double, BYTE*, long) = 0;
};
struct ISampleGrabber_ : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE SetOneShot(BOOL) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetMediaType(const AM_MEDIA_TYPE*) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetConnectedMediaType(AM_MEDIA_TYPE*) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetBufferSamples(BOOL) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetCurrentBuffer(long*, long*) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetCurrentSample(IMediaSample**) = 0;
    virtual HRESULT STDMETHODCALLTYPE SetCallback(ISampleGrabberCB_*, long) = 0;
};

struct Counter : ISampleGrabberCB_ {
    std::atomic<long> frames{0};
    std::atomic<long long> lumaSum{0};
    std::atomic<long> lumaN{0};
    ULONG STDMETHODCALLTYPE AddRef() override { return 2; }
    ULONG STDMETHODCALLTYPE Release() override { return 1; }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (riid == IID_IUnknown || riid == IID_ISampleGrabberCB_) { *ppv = this; return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    HRESULT STDMETHODCALLTYPE SampleCB(double, IMediaSample*) override { return S_OK; }
    HRESULT STDMETHODCALLTYPE BufferCB(double, BYTE* p, long n) override {
        frames++;
        long long s = 0; long cnt = 0;
        for (long i = 0; i < n; i += 4 * 97) { s += p[i]; cnt++; }   // sparse sample of the B channel
        lumaSum += s; lumaN += cnt;
        return S_OK;
    }
};

#define CHECK(x, what) do { HRESULT _hr = (x); if (FAILED(_hr)) { printf("    FAIL %s hr=0x%08lX\n", what, (unsigned long)_hr); return false; } } while (0)

static IBaseFilter* BindDevice(const wchar_t* name)
{
    ICreateDevEnum* de = nullptr; IEnumMoniker* em = nullptr; IBaseFilter* f = nullptr;
    if (FAILED(CoCreateInstance(CLSID_SystemDeviceEnum, nullptr, CLSCTX_INPROC_SERVER, IID_ICreateDevEnum, (void**)&de))) return nullptr;
    if (de->CreateClassEnumerator(CLSID_VideoInputDeviceCategory, &em, 0) == S_OK) {
        IMoniker* m = nullptr;
        while (!f && em->Next(1, &m, nullptr) == S_OK) {
            IPropertyBag* bag = nullptr;
            if (SUCCEEDED(m->BindToStorage(nullptr, nullptr, IID_IPropertyBag, (void**)&bag))) {
                VARIANT v; VariantInit(&v);
                if (SUCCEEDED(bag->Read(L"FriendlyName", &v, nullptr)) && wcscmp(v.bstrVal, name) == 0)
                    m->BindToObject(nullptr, nullptr, IID_IBaseFilter, (void**)&f);
                VariantClear(&v); bag->Release();
            }
            m->Release();
        }
        em->Release();
    }
    de->Release();
    return f;
}

static IPin* OutPin(IBaseFilter* f)
{
    IEnumPins* ep = nullptr; IPin* p = nullptr;
    f->EnumPins(&ep);
    while (ep->Next(1, &p, nullptr) == S_OK) {
        PIN_DIRECTION d; p->QueryDirection(&d);
        if (d == PINDIR_OUTPUT) break;
        p->Release(); p = nullptr;
    }
    ep->Release();
    return p;
}

// One capture session on an existing (or new) source filter.
static bool Session(IBaseFilter* src, int ms, const char* label)
{
    printf("  [%s]\n", label);
    IPin* out = OutPin(src);
    if (!out) { printf("    FAIL no output pin\n"); return false; }

    IAMStreamConfig* sc = nullptr;
    CHECK(out->QueryInterface(IID_IAMStreamConfig, (void**)&sc), "QI IAMStreamConfig");
    int count = 0, size = 0;
    CHECK(sc->GetNumberOfCapabilities(&count, &size), "GetNumberOfCapabilities");
    AM_MEDIA_TYPE* mt = nullptr; BYTE caps[1024] = {};
    CHECK(sc->GetStreamCaps(0, &mt, caps), "GetStreamCaps(0)");
    VIDEOINFOHEADER* vih = (VIDEOINFOHEADER*)mt->pbFormat;
    printf("    caps: %d formats, #0 = %ldx%ld %d bit\n", count, vih->bmiHeader.biWidth, vih->bmiHeader.biHeight, vih->bmiHeader.biBitCount);
    CHECK(sc->SetFormat(mt), "SetFormat");
    CoTaskMemFree(mt->pbFormat); CoTaskMemFree(mt);
    sc->Release();

    IGraphBuilder* g = nullptr; IBaseFilter *grabF = nullptr, *nullF = nullptr; ISampleGrabber_* grab = nullptr;
    CHECK(CoCreateInstance(CLSID_FilterGraph, nullptr, CLSCTX_INPROC_SERVER, IID_IGraphBuilder, (void**)&g), "FilterGraph");
    CHECK(CoCreateInstance(CLSID_SampleGrabber_, nullptr, CLSCTX_INPROC_SERVER, IID_IBaseFilter, (void**)&grabF), "SampleGrabber");
    CHECK(CoCreateInstance(CLSID_NullRenderer_, nullptr, CLSCTX_INPROC_SERVER, IID_IBaseFilter, (void**)&nullF), "NullRenderer");
    grabF->QueryInterface(IID_ISampleGrabber_, (void**)&grab);
    Counter cb;
    grab->SetCallback(&cb, 1);
    CHECK(g->AddFilter(src, L"src"), "AddFilter src");
    g->AddFilter(grabF, L"grab");
    g->AddFilter(nullF, L"null");
    IPin* gin = nullptr; IPin* gout = nullptr; IPin* nin = nullptr;
    { IEnumPins* e; grabF->EnumPins(&e); e->Next(1, &gin, nullptr); e->Next(1, &gout, nullptr); e->Release(); }
    { IEnumPins* e; nullF->EnumPins(&e); e->Next(1, &nin, nullptr); e->Release(); }
    CHECK(g->Connect(out, gin), "Connect src->grabber");
    CHECK(g->Connect(gout, nin), "Connect grabber->null");

    IMediaControl* mc = nullptr;
    g->QueryInterface(IID_IMediaControl, (void**)&mc);
    CHECK(mc->Run(), "Run");
    Sleep(ms);
    mc->Stop();
    long f = cb.frames;
    double mean = cb.lumaN ? (double)cb.lumaSum / cb.lumaN : 0;
    // Live standby image ~31; the darkened "sender gone" placeholder is ~31/4 - count that as a failure.
    static const double minMean = [] { char v[32]; return GetEnvironmentVariableA("REOPEN_MIN_MEAN", v, sizeof(v)) ? atof(v) : 20.0; }();
    const bool live = f > 0 && mean > minMean;
    printf("    frames in %d ms: %ld  (%.0f fps)   mean pixel %.1f  -> %s\n", ms, f, f * 1000.0 / ms, mean,
           f == 0 ? "NO FRAMES" : live ? "LIVE image" : "stale placeholder (not reconnected)");
    f = live ? f : 0;

    grab->SetCallback(nullptr, 1);
    g->RemoveFilter(src);   // keep src alive for scenario B; the caller owns it
    mc->Release(); gin->Release(); gout->Release(); nin->Release(); grab->Release();
    grabF->Release(); nullF->Release(); g->Release(); out->Release();
    return f > 0;
}

#include <tlhelp32.h>
static bool BridgeRunning()
{
    HANDLE s = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    PROCESSENTRY32W pe{ sizeof(pe) };
    bool found = false;
    for (BOOL ok = Process32FirstW(s, &pe); ok && !found; ok = Process32NextW(s, &pe))
        found = _wcsicmp(pe.szExeFile, L"GolfCamBridge.exe") == 0;
    CloseHandle(s);
    return found;
}

// Scenario E: what happens to a host app when the bridge (sender) goes away and comes back.
// The user exits the tray app and starts it again while this runs.
static int RestartTest(const wchar_t* name, int ms)
{
    int ok = 0, total = 0;
    if (!BridgeRunning())
    {
        printf(">>> Start GolfCamBridge (waiting up to 3 min)\n"); fflush(stdout);
        for (int i = 0; i < 1800 && !BridgeRunning(); i++) Sleep(100);
        Sleep(4000);
    }
    IBaseFilter* held = BindDevice(name);
    printf("E1 instance created while the bridge runs\n");
    total++; if (held && Session(held, ms, "E1")) ok++;

    printf(">>> Exit GolfCamBridge from the tray menu now (waiting up to 3 min)\n"); fflush(stdout);
    for (int i = 0; i < 1800 && BridgeRunning(); i++) Sleep(100);
    if (BridgeRunning()) { printf("bridge did not exit - aborting\n"); return 2; }
    Sleep(1500);
    printf("E2 bridge is DOWN: new instance created now (as if Premier enters practice mode)\n");
    IBaseFilter* bornDead = BindDevice(name);
    total++; if (bornDead && Session(bornDead, ms, "E2 bridge down")) ok++;

    printf(">>> Start GolfCamBridge again now (waiting up to 3 min)\n"); fflush(stdout);
    for (int i = 0; i < 1800 && !BridgeRunning(); i++) Sleep(100);
    Sleep(4000);   // let it create the virtual cams
    total++; if (held && Session(held, ms, "E3 instance from BEFORE the restart")) ok++;
    total++; if (bornDead && Session(bornDead, ms, "E4 instance created WHILE the bridge was down")) ok++;
    IBaseFilter* after = BindDevice(name);
    total++; if (after && Session(after, ms, "E5 new instance AFTER the restart")) ok++;
    if (after) after->Release();
    if (bornDead) bornDead->Release();
    if (held) held->Release();
    printf("RESULT: %d / %d sessions delivered frames\n", ok, total);
    return ok == total ? 0 : 1;
}

int wmain(int argc, wchar_t** argv)
{
    if (argc > 1 && wcscmp(argv[1], L"--restart") == 0)
    {
        CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        int r = RestartTest(argc > 2 ? argv[2] : L"Golf Cam 1", 2000);
        CoUninitialize();
        return r;
    }
    if (!BridgeRunning())
        printf("WARNING: GolfCamBridge.exe is not running - softcam has no format to offer, sessions will fail with E_FAIL.\n");
    const wchar_t* name = argc > 1 ? argv[1] : L"Golf Cam 1";
    int rounds = argc > 2 ? _wtoi(argv[2]) : 3;
    int ms = argc > 3 ? _wtoi(argv[3]) : 2000;
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    printf("device '%ls', %d rounds x %d ms\n", name, rounds, ms);

    int ok = 0, total = 0;
    printf("Scenario A: new filter instance every time (enumerate -> bind -> run -> release)\n");
    for (int i = 1; i <= rounds; i++) {
        IBaseFilter* src = BindDevice(name);
        char label[64]; sprintf_s(label, "A%d", i);
        total++;
        if (!src) { printf("  [%s]\n    FAIL device not found / bind failed\n", label); }
        else { if (Session(src, ms, label)) ok++; src->Release(); }
        Sleep(500);   // "main menu"
    }

    printf("Scenario B: same filter instance, graph rebuilt each time\n");
    IBaseFilter* src = BindDevice(name);
    for (int i = 1; i <= rounds; i++) {
        char label[64]; sprintf_s(label, "B%d", i);
        total++;
        if (!src) { printf("  [%s]\n    FAIL bind failed\n", label); continue; }
        if (Session(src, ms, label)) ok++;
        Sleep(500);
    }
    if (src) src->Release();

    printf("Scenario C: old instance still alive (not released) when a new one is created\n");
    {
        IBaseFilter* old = BindDevice(name);
        total++; if (old && Session(old, ms, "C1 old")) ok++;
        IBaseFilter* fresh = BindDevice(name);
        total++; if (fresh && Session(fresh, ms, "C2 new, old still held")) ok++;
        if (fresh) fresh->Release();
        if (old) old->Release();
    }

    printf("Scenario D: two instances streaming at the same time\n");
    {
        IBaseFilter* a = BindDevice(name);
        IBaseFilter* b = BindDevice(name);
        // run A in a thread while B runs here
        struct Arg { IBaseFilter* f; int ms; bool r; } arg{a, ms, false};
        HANDLE t = CreateThread(nullptr, 0, [](LPVOID p) -> DWORD {
            auto* x = (Arg*)p; CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            x->r = x->f && Session(x->f, x->ms, "D1 (thread)"); CoUninitialize(); return 0; }, &arg, 0, nullptr);
        Sleep(300);
        total++; if (b && Session(b, ms, "D2 (main)")) ok++;
        WaitForSingleObject(t, INFINITE); CloseHandle(t);
        total++; if (arg.r) ok++;
        if (a) a->Release();
        if (b) b->Release();
    }

    printf("RESULT: %d / %d sessions delivered frames\n", ok, total);
    CoUninitialize();
    return ok == total ? 0 : 1;
}
