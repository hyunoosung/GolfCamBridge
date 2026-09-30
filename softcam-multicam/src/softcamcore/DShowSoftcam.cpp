#include "DShowSoftcam.h"
#include "Variant.h"

#include <cstring>
#include <cstdlib>
#include <string>
#include <algorithm>
#include <cstdio>
#include <cmath>
#include <chrono>
#include <ctime>


namespace {

// Build with /p:SoftcamLog=1 to log every DirectShow call made by the host app
// (e.g. Foresight Premier) to C:\Users\Public\<camera name>.log
#if defined(SOFTCAM_ENABLE_LOG) && SOFTCAM_ENABLE_LOG
#define ENABLE_LOG
#define LOG_FILE_PATH "C:\\Users\\Public\\" SOFTCAM_NAME_A ".log"
#endif

#if defined(ENABLE_LOG)

FILE* logfile = nullptr;
#define OPEN_LOGFILE() logfile = std::fopen(LOG_FILE_PATH, "a")
#define CLOSE_LOGFILE() [&]{ \
        if (logfile) std::fclose(logfile); \
        logfile = nullptr; \
    }()
#define NOW() []{ \
        auto tp = std::chrono::system_clock::now(); \
        auto t = std::chrono::system_clock::to_time_t(tp); \
        static char buff[128]; \
        std::strftime(buff, sizeof(buff), "%H:%M:%S", std::localtime(&t)); \
        return buff; \
    }()
std::string IID_TO_STR(REFIID riid)
{
    std::string s =
            riid == IID_IPin                ? "IPin" :
            riid == IID_IBaseFilter         ? "IBaseFilter" :
            riid == IID_IAMovieSetup        ? "IAMovieSetup" :
            riid == IID_IQualityControl     ? "IQualityControl" :
            riid == IID_IAMStreamConfig     ? "IAMStreamConfig" :
            riid == IID_IKsPropertySet      ? "IKsPropertySet" :
            riid == IID_IAMFilterMiscFlags  ? "IAMFilterMiscFlags" :
            riid == IID_IPersistPropertyBag ? "IPersistPropertyBag" :
            riid == IID_IReferenceClock     ? "IReferenceClock" :
            riid == IID_IMediaSeeking       ? "IMediaSeeking" :
            riid == IID_IAMDeviceRemoval    ? "IAMDeviceRemoval" :
            riid == IID_IAMOpenProgress     ? "IAMOpenProgress" :
            riid == IID_IMediaPosition      ? "IMediaPosition" :
            riid == IID_IMediaFilter        ? "IMediaFilter" :
            riid == IID_IBasicVideo         ? "IBasicVideo" :
            riid == IID_IBasicAudio         ? "IBasicAudio" :
            riid == IID_IVideoWindow        ? "IVideoWindow" :
            riid == IID_IUnknown            ? "IUnknown" :
            [&]() -> std::string
            {
                char buff[128];
                snprintf(buff, sizeof(buff),
                    "{%08lX-%04hX-%04hX-%02hhX%02hhX-%02hhX%02hhX%02hhX%02hhX%02hhX%02hhX}",
                    riid.Data1, riid.Data2, riid.Data3,
                    riid.Data4[0], riid.Data4[1], riid.Data4[2], riid.Data4[3],
                    riid.Data4[4], riid.Data4[5], riid.Data4[6], riid.Data4[7]);
                return buff;
            }();
    return s;
}
#define LOG(fmt, ...) [&,funcname=__func__]{ \
        if (logfile) { std::fprintf(logfile, "%s: %s " fmt, NOW(), funcname, __VA_ARGS__); std::fflush(logfile); } \
        std::printf("%s: %s " fmt, NOW(), funcname, __VA_ARGS__); \
    }()

#else // ENABLE_LOG

#define OPEN_LOGFILE()
#define CLOSE_LOGFILE()
#define LOG(...)

#endif // ENABLE_LOG


AM_MEDIA_TYPE* allocateMediaType()
{
    BYTE *pbFormat = (BYTE*)CoTaskMemAlloc(sizeof(VIDEOINFOHEADER));
    if (!pbFormat)
    {
        return nullptr;
    }

    AM_MEDIA_TYPE *amt = (AM_MEDIA_TYPE*)CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE));
    if (!amt)
    {
        CoTaskMemFree(pbFormat);
        return nullptr;
    }
    std::memset(amt, 0, sizeof(*amt));
    amt->pbFormat = pbFormat;
    return amt;
}

std::size_t calcDIBSize(int width, int height, int bit_count = 24)
{
    std::size_t stride = (static_cast<unsigned>(width) * (unsigned)(bit_count / 8) + 3) & ~3u;
    return stride * static_cast<unsigned>(height);
}

// Advertised frame-interval range (100ns units): 1000 fps .. 1 fps, like hardware drivers do.
constexpr LONGLONG MIN_FRAME_INTERVAL = 10 * 1000;
constexpr LONGLONG MAX_FRAME_INTERVAL = 10 * 1000 * 1000;

void fillMediaType(AM_MEDIA_TYPE* amt, int width, int height, float framerate, int bit_count = 32)
{
    BYTE *pbFormat = amt->pbFormat;

    if (framerate <= 0.0f)
    {
        framerate = 60.0f;
    }
    const float bit_rate = (float)width * (float)height * (float)bit_count * framerate;
    const float period = 10 * 1000 * 1000 / framerate;

    VIDEOINFOHEADER* pFormat = (VIDEOINFOHEADER*)pbFormat;
    std::memset(pFormat, 0, sizeof(*pFormat));
    pFormat->dwBitRate = (uint32_t)(std::min)(bit_rate, (float)INT_MAX);
    pFormat->dwBitErrorRate = 0;
    pFormat->AvgTimePerFrame = (LONGLONG)std::round((std::min)(period, (float)LONG_MAX));
    pFormat->bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    pFormat->bmiHeader.biWidth = width;
    pFormat->bmiHeader.biHeight = height;
    pFormat->bmiHeader.biPlanes = 1;
    pFormat->bmiHeader.biBitCount = (WORD)bit_count;
    pFormat->bmiHeader.biCompression = BI_RGB;
    pFormat->bmiHeader.biSizeImage = static_cast<uint32_t>(calcDIBSize(width, height, bit_count));

    amt->majortype = MEDIATYPE_Video;
    amt->subtype = bit_count == 32 ? MEDIASUBTYPE_RGB32 : MEDIASUBTYPE_RGB24;
    amt->bFixedSizeSamples = TRUE;
    amt->bTemporalCompression = FALSE;
    amt->lSampleSize = static_cast<uint32_t>(calcDIBSize(width, height, bit_count));
    amt->formattype = FORMAT_VideoInfo;
    amt->pUnk = nullptr;
    amt->cbFormat = sizeof(VIDEOINFOHEADER);
    amt->pbFormat = pbFormat;
}

AM_MEDIA_TYPE* makeMediaType(int width, int height, float framerate, int bit_count = 32)
{
    AM_MEDIA_TYPE *amt = allocateMediaType();
    if (!amt)
    {
        return nullptr;
    }
    fillMediaType(amt, width, height, framerate, bit_count);
    return amt;
}

} //namespace

namespace softcam {


CUnknown * Softcam::CreateInstance(
                    LPUNKNOWN   lpunk,
                    const GUID& clsid,
                    HRESULT*    phr)
{
    OPEN_LOGFILE();
    LOG("===== logging started =====\n");

    return new Softcam(lpunk, clsid, phr);
}

Softcam::Softcam(LPUNKNOWN lpunk, const GUID& clsid, HRESULT *phr) :
    CSource(NAME("DirectShow Softcam"), lpunk, clsid),
    m_frame_buffer(FrameBuffer::open()),
    m_valid(m_frame_buffer ? true : false),
    m_width(m_frame_buffer.width()),
    m_height(m_frame_buffer.height()),
    m_framerate(m_frame_buffer.framerate())
{
    // This code is okay though it may look strange as the return value is ignored.
    // Calling the SoftcamStream constructor results in calling the CBaseOutputPin
    // constructor which registers the instance to this Softcam instance by calling
    // CSource::AddPin().
    (void)new SoftcamStream(phr, this, L"DirectShow Softcam Stream");
}


STDMETHODIMP Softcam::NonDelegatingQueryInterface(REFIID riid, __deref_out void **ppv)
{
    if (riid == IID_IAMStreamConfig)
    {
        LOG("(Softcam) IAMStreamConfig -> S_OK\n");
        return GetInterface(static_cast<IAMStreamConfig*>(this), ppv);
    }
    else
    {
        auto result = CSource::NonDelegatingQueryInterface(riid, ppv);
        LOG("(Softcam) %s -> %s\n", IID_TO_STR(riid).c_str(), result ? "(ERROR)" : "S_OK");
        return result;
    }
}

HRESULT
Softcam::SetFormat(AM_MEDIA_TYPE *mt)
{
    if (!mt)
    {
        LOG("-> E_POINTER\n");
        return E_POINTER;
    }
    if (!m_valid)
    {
        LOG("-> E_FAIL\n");
        return E_FAIL;
    }
    if (mt->formattype == FORMAT_VideoInfo && mt->pbFormat)
    {
        VIDEOINFOHEADER* req = (VIDEOINFOHEADER*)mt->pbFormat;
        LOG("requested subtype=%s %ldx%ld bits=%d compression=0x%08lX interval=%lld\n",
            IID_TO_STR(mt->subtype).c_str(), req->bmiHeader.biWidth, req->bmiHeader.biHeight,
            (int)req->bmiHeader.biBitCount, (unsigned long)req->bmiHeader.biCompression,
            (long long)req->AvgTimePerFrame);
        (void)req;
    }
    else
    {
        LOG("requested subtype=%s formattype=%s\n",
            IID_TO_STR(mt->subtype).c_str(), IID_TO_STR(mt->formattype).c_str());
    }
    if (mt->majortype != MEDIATYPE_Video ||
        (mt->subtype != MEDIASUBTYPE_RGB24 && mt->subtype != MEDIASUBTYPE_RGB32))
    {
        LOG("-> E_FAIL (invalid media type)\n");
        return E_FAIL;
    }
    const int bits = mt->subtype == MEDIASUBTYPE_RGB32 ? 32 : 24;
    if (mt->formattype == FORMAT_VideoInfo && mt->pbFormat)
    {
        VIDEOINFOHEADER* pFormat = (VIDEOINFOHEADER*)mt->pbFormat;
        if (pFormat->bmiHeader.biWidth != m_width ||
            std::abs(pFormat->bmiHeader.biHeight) != m_height)
        {
            LOG("-> E_FAIL (invalid dimension)\n");
            return E_FAIL;
        }
        if (pFormat->bmiHeader.biBitCount != bits ||
            pFormat->bmiHeader.biCompression != BI_RGB)
        {
            LOG("-> E_FAIL (invalid color format)\n");
            return E_FAIL;
        }
    }
    {
        CAutoLock lock(&m_critsec);
        m_bit_count = bits;
    }
    LOG("-> S_OK (%d bit)\n", bits);
    return S_OK;
}

HRESULT
Softcam::GetFormat(AM_MEDIA_TYPE **out_pmt)
{
    if (!out_pmt)
    {
        LOG("-> E_POINTER\n");
        return E_POINTER;
    }
    if (!m_valid)
    {
        LOG("-> E_FAIL\n");
        return E_FAIL;
    }
    AM_MEDIA_TYPE* mt = makeMediaType(m_width, m_height, m_framerate, bitCount());
    if (!mt)
    {
        LOG("-> E_OUTOFMEMORY\n");
        return E_OUTOFMEMORY;
    }
    *out_pmt = mt;
    LOG("-> S_OK\n");
    return S_OK;
}

HRESULT
Softcam::GetNumberOfCapabilities(int *out_count, int *out_size)
{
    if (!out_count || !out_size)
    {
        LOG("-> E_POINTER\n");
        return E_POINTER;
    }
    if (!m_valid)
    {
        LOG("-> E_FAIL\n");
        return E_FAIL;
    }
    *out_count = 2;     // 0: RGB32, 1: RGB24
    *out_size = sizeof(VIDEO_STREAM_CONFIG_CAPS);
    LOG("-> S_OK\n");
    return S_OK;
}

HRESULT
Softcam::GetStreamCaps(int index, AM_MEDIA_TYPE **out_pmt, BYTE *out_scc)
{
    if (!out_pmt || !out_scc)
    {
        LOG("-> E_POINTER\n");
        return E_POINTER;
    }
    if (!m_valid)
    {
        LOG("-> E_FAIL\n");
        return E_FAIL;
    }
    if (index < 0 || index >= 2)
    {
        LOG("-> S_FALSE (invalid index %d)\n", index);
        return S_FALSE;
    }
    AM_MEDIA_TYPE *mt = makeMediaType(m_width, m_height, m_framerate, index == 0 ? 32 : 24);
    if (!mt)
    {
        LOG("-> E_OUTOFMEMORY\n");
        return E_OUTOFMEMORY;
    }
    *out_pmt = mt;
    std::memset(out_scc, 0, sizeof(VIDEO_STREAM_CONFIG_CAPS));
    VIDEOINFOHEADER* format = (VIDEOINFOHEADER*)mt->pbFormat;
    VIDEO_STREAM_CONFIG_CAPS* scc = (VIDEO_STREAM_CONFIG_CAPS*)out_scc;
    scc->guid = FORMAT_VideoInfo;
    scc->InputSize = SIZE{m_width, m_height};
    scc->MinCroppingSize = SIZE{m_width, m_height};
    scc->MaxCroppingSize = SIZE{m_width, m_height};
    scc->CropGranularityX = 1;
    scc->CropGranularityY = 1;
    scc->CropAlignX = 1;
    scc->CropAlignY = 1;
    scc->MinOutputSize = SIZE{m_width, m_height};
    scc->MaxOutputSize = SIZE{m_width, m_height};
    scc->OutputGranularityX = 1;
    scc->OutputGranularityY = 1;
    scc->StretchTapsX = 0;
    scc->StretchTapsY = 0;
    scc->ShrinkTapsX = 0;
    scc->ShrinkTapsY = 0;
    scc->MinFrameInterval = (std::min)(MIN_FRAME_INTERVAL, format->AvgTimePerFrame);
    scc->MaxFrameInterval = (std::max)(MAX_FRAME_INTERVAL, format->AvgTimePerFrame);
    const double bits_per_frame = (double)m_width * m_height * (index == 0 ? 32 : 24);
    scc->MinBitsPerSecond = (LONG)(std::min)(bits_per_frame * 1e7 / (double)scc->MaxFrameInterval, (double)LONG_MAX);
    scc->MaxBitsPerSecond = (LONG)(std::min)(bits_per_frame * 1e7 / (double)scc->MinFrameInterval, (double)LONG_MAX);
    LOG("-> S_OK (index %d)\n", index);
    return S_OK;
}


FrameBuffer* Softcam::getFrameBuffer()
{
    if (!m_valid)
    {
        return nullptr;
    }

    CAutoLock lock(&m_critsec);
    if (!m_frame_buffer)
    {
        auto fb = FrameBuffer::open();
        if (fb &&
            fb.active() &&
            fb.width() == m_width &&
            fb.height() == m_height)
        {
            m_frame_buffer = fb;
        }
    }
    if (m_frame_buffer)
    {
        return &m_frame_buffer;
    }
    else
    {
        return nullptr;
    }
}

int
Softcam::bitCount()
{
    CAutoLock lock(&m_critsec);
    return m_bit_count;
}

void
Softcam::releaseFrameBuffer()
{
    CAutoLock lock(&m_critsec);
    m_frame_buffer.release();
}

SoftcamStream::SoftcamStream(HRESULT *phr,
                         Softcam *pParent,
                         LPCWSTR pPinName) :
    CSourceStream(NAME("DirectShow Softcam Stream"), phr, pParent, pPinName),
    m_valid(pParent->valid()),
    m_width(pParent->width()),
    m_height(pParent->height())
{
}


SoftcamStream::~SoftcamStream()
{
    LOG("===== logging finished =====\n");
    CLOSE_LOGFILE();
}


STDMETHODIMP SoftcamStream::NonDelegatingQueryInterface(REFIID riid, __deref_out void **ppv)
{
    if (riid == IID_IKsPropertySet )
    {
        LOG("(SoftcamStream) IKsPropertySet -> S_OK\n");
        return GetInterface(static_cast<IKsPropertySet*>(this), ppv);
    }
    else if(riid == IID_IAMStreamConfig)
    {
        LOG("(SoftcamStream) IAMStreamConfig -> S_OK\n");
        return GetInterface(static_cast<IAMStreamConfig*>(this), ppv);
    }
    else
    {
        auto result = CSourceStream::NonDelegatingQueryInterface(riid, ppv);
        LOG("(SoftcamStream) %s -> %s\n", IID_TO_STR(riid).c_str(), result ? "(ERROR)" : "S_OK");
        return result;
    }
}


HRESULT SoftcamStream::FillBuffer(IMediaSample *pms)
{
    CheckPointer(pms,E_POINTER);

    BYTE *pData;
    pms->GetPointer(&pData);
    long lDataLen = pms->GetSize();
    ZeroMemory(pData, (std::size_t)lDataLen);
    {
        if (auto fb = getParent()->getFrameBuffer())
        {
            bool active = fb->waitForNewFrame(m_frame_counter);
            const VIDEOINFOHEADER* vih = (const VIDEOINFOHEADER*)m_mt.Format();
            const int bits = vih ? vih->bmiHeader.biBitCount : 24;
            const bool top_down = vih && vih->bmiHeader.biHeight < 0;
            fb->transferToDIB(pData, &m_frame_counter, bits, top_down);

            if (!active)
            {
                // The sender has deactivated this stream and stopped sending frames.
                // We release this stream and will wait a new stream to be available.
                getParent()->releaseFrameBuffer();

                // Save the last image for a placeholder.
                const std::size_t size = (std::size_t)lDataLen;
                if (!m_screenshot || m_screenshot_size != size)
                {
                    m_screenshot.reset(new uint8_t[size]);
                    m_screenshot_size = size;
                }
                {
                    // Darken the image to indicate that the source is inactive.
                    for (std::size_t i = 0; i < size; i++)
                    {
                        pData[i] /= 4;
                    }
                }
                std::memcpy(m_screenshot.get(), pData, size);
            }
        }
        else
        {
            // Waiting for a new stream.
            m_frame_counter = 0;
            Timer::sleep(0.100f);

            if (m_screenshot)
            {
                std::memcpy(pData, m_screenshot.get(), (std::min)(m_screenshot_size, (std::size_t)lDataLen));
            }
        }

        CAutoLock lock(&m_critsec);
        CRefTime start = m_sample_time;
        m_sample_time += (LONG)m_interval_time_msec;
        pms->SetTime((REFERENCE_TIME*)&start,(REFERENCE_TIME*)&m_sample_time);
    }
    pms->SetSyncPoint(TRUE);
    //LOG("-> NOERROR\n");
    return NOERROR;
}


STDMETHODIMP SoftcamStream::Notify(IBaseFilter * pSender, Quality q)
{
    CAutoLock lock(&m_critsec);
    m_interval_time_msec = m_interval_time_msec * 1000 / (std::max)(q.Proportion, 1L);
    m_interval_time_msec = (std::min)((std::max)(m_interval_time_msec, 1L), 1000L);
    if (q.Late > 0) {
        m_sample_time += q.Late;
    }
    //LOG("-> NOERROR\n");
    return NOERROR;
}


HRESULT SoftcamStream::GetMediaType(int iPosition, CMediaType *pmt)
{
    CheckPointer(pmt,E_POINTER);

    if (!m_valid)
    {
        LOG("-> E_FAIL\n");
        return E_FAIL;
    }
    if (iPosition < 0)
    {
        return E_INVALIDARG;
    }
    if (iPosition >= 2)
    {
        return VFW_S_NO_MORE_ITEMS;
    }

    VIDEOINFOHEADER *pvi = (VIDEOINFOHEADER*)pmt->AllocFormatBuffer(sizeof(VIDEOINFOHEADER));
    if (pvi == nullptr)
    {
        LOG("-> E_OUTOFMEMORY\n");
        return E_OUTOFMEMORY;
    }

    // Position 0 is the format selected via IAMStreamConfig::SetFormat (RGB32 by default).
    const int preferred = getParent()->bitCount();
    const int bits = iPosition == 0 ? preferred : (preferred == 32 ? 24 : 32);
    fillMediaType(pmt, getParent()->width(), getParent()->height(), getParent()->framerate(), bits);

    LOG("-> NOERROR (position %d, %d bit)\n", iPosition, bits);
    return NOERROR;
}

HRESULT SoftcamStream::CheckMediaType(const CMediaType *pmt)
{
    CheckPointer(pmt,E_POINTER);
    if (!m_valid)
    {
        return E_FAIL;
    }
    if (*pmt->Type() != MEDIATYPE_Video ||
        (*pmt->Subtype() != MEDIASUBTYPE_RGB32 && *pmt->Subtype() != MEDIASUBTYPE_RGB24) ||
        *pmt->FormatType() != FORMAT_VideoInfo ||
        pmt->FormatLength() < sizeof(VIDEOINFOHEADER))
    {
        LOG("-> E_FAIL (type %s)\n", IID_TO_STR(*pmt->Subtype()).c_str());
        return E_FAIL;
    }
    const VIDEOINFOHEADER* vih = (const VIDEOINFOHEADER*)pmt->Format();
    const int bits = *pmt->Subtype() == MEDIASUBTYPE_RGB32 ? 32 : 24;
    if (vih->bmiHeader.biWidth != m_width ||
        std::abs(vih->bmiHeader.biHeight) != m_height ||
        vih->bmiHeader.biBitCount != bits ||
        vih->bmiHeader.biCompression != BI_RGB)
    {
        LOG("-> E_FAIL (%ldx%ld %d bit)\n", vih->bmiHeader.biWidth, vih->bmiHeader.biHeight,
            (int)vih->bmiHeader.biBitCount);
        return E_FAIL;
    }
    LOG("-> S_OK (%ldx%ld %d bit)\n", vih->bmiHeader.biWidth, vih->bmiHeader.biHeight, bits);
    return S_OK;
}


HRESULT SoftcamStream::DecideBufferSize(IMemAllocator *pAlloc,
                                        ALLOCATOR_PROPERTIES *pProperties)
{
    CheckPointer(pAlloc,E_POINTER);
    CheckPointer(pProperties,E_POINTER);

    CAutoLock lock(m_pFilter->pStateLock());
    HRESULT hr = NOERROR;

    VIDEOINFO *pvi = (VIDEOINFO *)m_mt.Format();
    pProperties->cBuffers = 1;
    // Some hosts propose a type with biSizeImage = 0, so compute the size ourselves as well.
    const std::size_t computed = calcDIBSize(m_width, m_height, pvi->bmiHeader.biBitCount == 32 ? 32 : 24);
    pProperties->cbBuffer = (long)(std::max)((std::size_t)pvi->bmiHeader.biSizeImage, computed);

    ALLOCATOR_PROPERTIES actual;
    hr = pAlloc->SetProperties(pProperties, &actual);
    if (FAILED(hr))
    {
        LOG("-> (FAILED)\n");
        return hr;
    }
    if (actual.cbBuffer < pProperties->cbBuffer)
    {
        LOG("-> E_FAIL\n");
        return E_FAIL;
    }
    LOG("-> NOERROR\n");
    return NOERROR;
}

HRESULT SoftcamStream::OnThreadCreate()
{
    CAutoLock lock(&m_critsec);
    m_sample_time = 0;
    float framerate = getParent()->framerate();
    if (framerate <= 0.0f)
    {
        framerate = 60.0f;
    }
    framerate = (std::min)((std::max)(framerate, 1.0f), 1000.0f);
    m_interval_time_msec = (long)std::round(1000.0f / framerate);

    LOG("-> NOERROR\n");
    return NOERROR;
}

HRESULT SoftcamStream::Set(REFGUID guidPropSet, DWORD dwPropID,
                           LPVOID pInstanceData, DWORD cbInstanceData,
                           LPVOID pPropData, DWORD cbPropData)
{
    LOG("-> E_NOTIMPL\n");
    return E_NOTIMPL;
}

HRESULT SoftcamStream::Get(REFGUID guidPropSet, DWORD dwPropID,
                           LPVOID pInstanceData, DWORD cbInstanceData,
                           LPVOID pPropData, DWORD cbPropData, DWORD *pcbReturned)
{
    if (guidPropSet != AMPROPSETID_Pin)
    {
        LOG("-> E_PROP_SET_UNSUPPORTED\n");
        return E_PROP_SET_UNSUPPORTED;
    }
    if (dwPropID != AMPROPERTY_PIN_CATEGORY)
    {
        LOG("-> E_PROP_ID_UNSUPPORTED\n");
        return E_PROP_ID_UNSUPPORTED;
    }
    if (pPropData == nullptr && pcbReturned == nullptr)
    {
        LOG("-> E_POINTER\n");
        return E_POINTER;
    }
    if (pcbReturned)
    {
        *pcbReturned = sizeof(GUID);
    }
    if (pPropData)
    {
        if (cbPropData < sizeof(GUID))
        {
            LOG("-> E_UNEXPECTED\n");
            return E_UNEXPECTED;
        }
        *(GUID*)pPropData = PIN_CATEGORY_CAPTURE;
    }
    LOG("-> S_OK\n");
    return S_OK;
}

HRESULT SoftcamStream::QuerySupported(REFGUID guidPropSet, DWORD dwPropID,
                              DWORD *pTypeSupport)
{
    if (guidPropSet != AMPROPSETID_Pin)
    {
        LOG("-> E_PROP_SET_UNSUPPORTED\n");
        return E_PROP_SET_UNSUPPORTED;
    }
    if (dwPropID != AMPROPERTY_PIN_CATEGORY)
    {
        LOG("-> E_PROP_ID_UNSUPPORTED\n");
        return E_PROP_ID_UNSUPPORTED;
    }
    if (pTypeSupport)
    {
        *pTypeSupport = KSPROPERTY_SUPPORT_GET;
    }
    LOG("-> S_OK\n");
    return S_OK;
}

HRESULT SoftcamStream::SetFormat(AM_MEDIA_TYPE *mt)
{
    return getParent()->SetFormat(mt);
}

HRESULT SoftcamStream::GetFormat(AM_MEDIA_TYPE **out_pmt)
{
    return getParent()->GetFormat(out_pmt);
}

HRESULT SoftcamStream::GetNumberOfCapabilities(int *out_count, int *out_size)
{
    return getParent()->GetNumberOfCapabilities(out_count, out_size);
}

HRESULT SoftcamStream::GetStreamCaps(int index, AM_MEDIA_TYPE **out_pmt, BYTE *out_scc)
{
    return getParent()->GetStreamCaps(index, out_pmt, out_scc);
}

Softcam* SoftcamStream::getParent()
{
    return static_cast<Softcam*>(m_pFilter);
}


} //namespace softcam
