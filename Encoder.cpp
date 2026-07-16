#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <wrl.h>
#include <icodecapi.h>
#include <codecapi.h>

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
    __declspec(dllexport) bool ClipVideo(const wchar_t** filePaths, const double* fileStartOffsets, int fileCount, double clipStartSec, double clipEndSec, int width, int height, int fps, const wchar_t* outputFilePath);
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

    // Configure keyframe interval (GOP size) via ICodecAPI
    ComPtr<ICodecAPI> pCodecAPI = nullptr;
    hr = g_pSinkWriter->GetServiceForStream(g_streamIndex, GUID_NULL, IID_PPV_ARGS(&pCodecAPI));
    if (SUCCEEDED(hr) && pCodecAPI != nullptr) {
        VARIANT var;
        VariantInit(&var);
        var.vt = VT_UI4;
        var.ulVal = fps * 2; // Keyframe every 2 seconds
        pCodecAPI->SetValue(&CODECAPI_AVEncMPVGOPSize, &var);
    }

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

bool ClipVideo(const wchar_t** filePaths, const double* fileStartOffsets, int fileCount, double clipStartSec, double clipEndSec, int width, int height, int fps, const wchar_t* outputFilePath) {
    HRESULT hr = S_OK;

    ComPtr<IMFSinkWriter> pSinkWriter = nullptr;
    DWORD outStreamIndex = 0;
    hr = MFCreateSinkWriterFromURL(outputFilePath, NULL, NULL, &pSinkWriter);
    if (FAILED(hr)) return false;

    // Use the first source file's media type directly for the stream output and input
    ComPtr<IMFSourceReader> pFirstReader = nullptr;
    hr = MFCreateSourceReaderFromURL(filePaths[0], NULL, &pFirstReader);
    if (FAILED(hr)) return false;

    ComPtr<IMFMediaType> pSrcType = nullptr;
    hr = pFirstReader->GetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, &pSrcType);
    if (FAILED(hr)) return false;

    hr = pSinkWriter->AddStream(pSrcType.Get(), &outStreamIndex);
    if (FAILED(hr)) return false;

    hr = pSinkWriter->SetInputMediaType(outStreamIndex, pSrcType.Get(), NULL);
    if (FAILED(hr)) return false;

    hr = pSinkWriter->BeginWriting();
    if (FAILED(hr)) return false;

    bool hasSetActualStart = false;
    double actualClipStartSec = clipStartSec;

    for (int i = 0; i < fileCount; i++) {
        ComPtr<IMFSourceReader> pReader = nullptr;
        hr = MFCreateSourceReaderFromURL(filePaths[i], NULL, &pReader);
        if (FAILED(hr)) continue;

        // Select first video stream
        hr = pReader->SetStreamSelection(MF_SOURCE_READER_ALL_STREAMS, FALSE);
        hr = pReader->SetStreamSelection(MF_SOURCE_READER_FIRST_VIDEO_STREAM, TRUE);
        if (FAILED(hr)) continue;

        // Do not request decoding (remuxing/stream copying)

        while (true) {
            DWORD streamIndex = 0;
            DWORD flags = 0;
            LONGLONG timestamp = 0;
            ComPtr<IMFSample> pSample = nullptr;

            hr = pReader->ReadSample(MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, &streamIndex, &flags, &timestamp, &pSample);
            if (FAILED(hr)) break;
            if (flags & MF_SOURCE_READERF_ENDOFSTREAM) break;

            if (pSample != nullptr) {
                double sampleSec = (double)timestamp / 10000000.0;
                double absTimeSec = fileStartOffsets[i] + sampleSec;

                // Retrieve keyframe info to align the first sample on a keyframe
                UINT32 isCleanPoint = 0;
                pSample->GetUINT32(MFSampleExtension_CleanPoint, &isCleanPoint);

                if (!hasSetActualStart) {
                    if (isCleanPoint && absTimeSec >= clipStartSec - 3.0) {
                        actualClipStartSec = absTimeSec;
                        hasSetActualStart = true;
                    }
                }

                if (hasSetActualStart) {
                    if (absTimeSec <= clipEndSec) {
                        // Set adjusted sample time relative to actual start
                        LONGLONG adjustedTime = (LONGLONG)((absTimeSec - actualClipStartSec) * 10000000.0);
                        hr = pSample->SetSampleTime(adjustedTime);
                        if (SUCCEEDED(hr)) {
                            pSinkWriter->WriteSample(outStreamIndex, pSample.Get());
                        }
                    }
                }
            }
        }
    }

    pSinkWriter->Finalize();
    return true;
}
