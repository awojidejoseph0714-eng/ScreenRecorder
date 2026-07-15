#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <wrl.h>

using Microsoft::WRL::ComPtr;

// Global variables for the encoder instance
ComPtr<IMFSinkWriter> g_pSinkWriter = nullptr;
DWORD g_streamIndex = 0;
int g_width = 0;
int g_height = 0;
int g_fps = 0;
UINT64 g_frameDuration = 0; // in 100-nanosecond units

extern "C" {
    __declspec(dllexport) bool InitializeEncoder(const wchar_t* filePath, int width, int height, int fps);
    __declspec(dllexport) bool WriteFrame(const BYTE* pixelData, int frameIndex);
    __declspec(dllexport) void CloseEncoder();
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved) {
    switch (ul_reason_for_call) {
    case DLL_PROCESS_ATTACH:
        CoInitializeEx(NULL, COINIT_APARTMENTTHREADED);
        MFStartup(MF_VERSION);
        break;
    case DLL_PROCESS_DETACH:
        MFShutdown();
        CoUninitialize();
        break;
    }
    return TRUE;
}

bool InitializeEncoder(const wchar_t* filePath, int width, int height, int fps) {
    if (g_pSinkWriter != nullptr) {
        CloseEncoder();
    }

    g_width = width;
    g_height = height;
    g_fps = fps;
    g_frameDuration = 10000000ULL / fps; // 1 second = 10,000,000 100-nanosecond units

    HRESULT hr = S_OK;

    // Create Sink Writer
    hr = MFCreateSinkWriterFromURL(filePath, NULL, NULL, &g_pSinkWriter);
    if (FAILED(hr)) return false;

    // Set output media type (H.264)
    ComPtr<IMFMediaType> pOutputType = nullptr;
    hr = MFCreateMediaType(&pOutputType);
    if (FAILED(hr)) return false;

    hr = pOutputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (FAILED(hr)) return false;

    hr = pOutputType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
    if (FAILED(hr)) return false;

    hr = pOutputType->SetUINT32(MF_MT_AVG_BITRATE, 4000000); // 4 Mbps
    if (FAILED(hr)) return false;

    hr = pOutputType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    if (FAILED(hr)) return false;

    hr = MFSetAttributeSize(pOutputType.Get(), MF_MT_FRAME_SIZE, width, height);
    if (FAILED(hr)) return false;

    hr = MFSetAttributeRatio(pOutputType.Get(), MF_MT_FRAME_RATE, fps, 1);
    if (FAILED(hr)) return false;

    hr = MFSetAttributeRatio(pOutputType.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    if (FAILED(hr)) return false;

    hr = g_pSinkWriter->AddStream(pOutputType.Get(), &g_streamIndex);
    if (FAILED(hr)) return false;

    // Set input media type (RGB32)
    ComPtr<IMFMediaType> pInputType = nullptr;
    hr = MFCreateMediaType(&pInputType);
    if (FAILED(hr)) return false;

    hr = pInputType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (FAILED(hr)) return false;

    hr = pInputType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);
    if (FAILED(hr)) return false;

    hr = pInputType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    if (FAILED(hr)) return false;

    hr = MFSetAttributeSize(pInputType.Get(), MF_MT_FRAME_SIZE, width, height);
    if (FAILED(hr)) return false;

    hr = MFSetAttributeRatio(pInputType.Get(), MF_MT_FRAME_RATE, fps, 1);
    if (FAILED(hr)) return false;

    hr = MFSetAttributeRatio(pInputType.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
    if (FAILED(hr)) return false;

    hr = g_pSinkWriter->SetInputMediaType(g_streamIndex, pInputType.Get(), NULL);
    if (FAILED(hr)) return false;

    // Begin writing
    hr = g_pSinkWriter->BeginWriting();
    if (FAILED(hr)) return false;

    return true;
}

bool WriteFrame(const BYTE* pixelData, int frameIndex) {
    if (g_pSinkWriter == nullptr) return false;

    HRESULT hr = S_OK;

    // Create a new memory buffer
    ComPtr<IMFMediaBuffer> pBuffer = nullptr;
    int bufferSize = g_width * g_height * 4; // RGB32 is 4 bytes per pixel
    hr = MFCreateMemoryBuffer(bufferSize, &pBuffer);
    if (FAILED(hr)) return false;

    // Lock the buffer and copy the pixel data
    BYTE* pData = nullptr;
    hr = pBuffer->Lock(&pData, NULL, NULL);
    if (FAILED(hr)) return false;

    // Flip the image vertically (GDI+ is top-down, MF RGB32 is bottom-up)
    int stride = g_width * 4;
    for (int y = 0; y < g_height; y++) {
        const BYTE* pSrcRow = pixelData + (y * stride);
        BYTE* pDestRow = pData + ((g_height - 1 - y) * stride);
        memcpy(pDestRow, pSrcRow, stride);
    }

    hr = pBuffer->Unlock();
    if (FAILED(hr)) return false;

    hr = pBuffer->SetCurrentLength(bufferSize);
    if (FAILED(hr)) return false;

    // Create a sample and add the buffer
    ComPtr<IMFSample> pSample = nullptr;
    hr = MFCreateSample(&pSample);
    if (FAILED(hr)) return false;

    hr = pSample->AddBuffer(pBuffer.Get());
    if (FAILED(hr)) return false;

    // Set sample time and duration
    LONGLONG sampleTime = frameIndex * g_frameDuration;
    hr = pSample->SetSampleTime(sampleTime);
    if (FAILED(hr)) return false;

    hr = pSample->SetSampleDuration(g_frameDuration);
    if (FAILED(hr)) return false;

    // Write the sample
    hr = g_pSinkWriter->WriteSample(g_streamIndex, pSample.Get());
    if (FAILED(hr)) return false;

    return true;
}

void CloseEncoder() {
    if (g_pSinkWriter != nullptr) {
        g_pSinkWriter->Finalize();
        g_pSinkWriter = nullptr;
    }
}
