// shadercompile.exe - feed DXBC shaders to D3D11 so that DXVK (running under
// Proton) translates them and compiles them into the GPU driver's shader cache.
//
// With VK_EXT_graphics_pipeline_library (NVIDIA, RADV), DXVK compiles a
// pipeline library for every vertex / pixel / compute shader as soon as the
// shader is created - the same work Overwatch triggers while loading. Running
// this under the game's Proton with the game's cache environment therefore
// leaves those compiled shaders in the cache the game reads.
//
// Usage: shadercompile.exe <dir-with-*.dxbc> [--quiet]
//   File names must look like <anything>.<vs|ps|gs|hs|ds|cs>.dxbc

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

static FILE* g_log = nullptr;
// Proton launches through a wrapper that drops console output, so every
// message also goes to <dir>\shadercompile.log.
static void say(const char* fmt, ...) {
    va_list ap;
    va_start(ap, fmt); vprintf(fmt, ap); va_end(ap); fflush(stdout);
    if (g_log) { va_start(ap, fmt); vfprintf(g_log, fmt, ap); va_end(ap); fflush(g_log); }
}

static double cpu_seconds() {
    FILETIME c, e, k, u;
    GetProcessTimes(GetCurrentProcess(), &c, &e, &k, &u);
    auto f = [](FILETIME t) { return (double)(((ULONGLONG)t.dwHighDateTime << 32) | t.dwLowDateTime) / 1e7; };
    return f(k) + f(u);
}

static bool read_file(const std::wstring& path, std::vector<char>& out) {
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER sz;
    GetFileSizeEx(h, &sz);
    out.resize((size_t)sz.QuadPart);
    DWORD rd = 0;
    BOOL ok = ReadFile(h, out.data(), (DWORD)out.size(), &rd, nullptr);
    CloseHandle(h);
    return ok && rd == out.size();
}

// --selftest: generate a few distinct VS/PS/CS blobs with d3dcompiler_47 and
// write them into <dir>, so the whole path can be tested without game files.
typedef HRESULT(WINAPI* PFN_D3DCompile)(const void*, SIZE_T, const char*, const void*, void*,
                                        const char*, const char*, UINT, UINT, ID3D10Blob**, ID3D10Blob**);

static int selftest_generate(const std::wstring& dir, int count) {
    HMODULE m = LoadLibraryA("d3dcompiler_47.dll");
    PFN_D3DCompile compile = m ? (PFN_D3DCompile)GetProcAddress(m, "D3DCompile") : nullptr;
    if (!compile) { say("d3dcompiler_47 not available\n"); return 1; }
    CreateDirectoryW(dir.c_str(), nullptr);
    int written = 0;
    for (int i = 0; i < count; i++) {
        char src[1024];
        const char* kinds[3] = { "vs", "ps", "cs" };
        const char* profiles[3] = { "vs_5_0", "ps_5_0", "cs_5_0" };
        int k = i % 3;
        if (k == 0)
            snprintf(src, sizeof src, "cbuffer C:register(b0){float4x4 m;};"
                     "float4 main(float4 p:POSITION):SV_Position{return mul(m,p)*%d.0+%d.5;}", i + 1, i);
        else if (k == 1)
            snprintf(src, sizeof src, "Texture2D t:register(t0);SamplerState s:register(s0);"
                     "float4 main(float4 p:SV_Position,float2 uv:TEXCOORD0):SV_Target"
                     "{return t.Sample(s,uv*%d.0)*%d.25;}", i + 1, i);
        else
            snprintf(src, sizeof src, "RWBuffer<float> b:register(u0);[numthreads(64,1,1)]"
                     "void main(uint3 id:SV_DispatchThreadID){b[id.x]=b[id.x]*%d.0+%d.0;}", i + 1, i);
        ID3D10Blob *code = nullptr, *err = nullptr;
        HRESULT hr = compile(src, strlen(src), "selftest", nullptr, nullptr, "main", profiles[k], 0, 0, &code, &err);
        if (FAILED(hr) || !code) {
            say("D3DCompile failed: %s\n", err ? (const char*)err->GetBufferPointer() : "?");
            continue;
        }
        wchar_t path[512];
        swprintf(path, 512, L"%ls\\selftest%04d.%hs.dxbc", dir.c_str(), i, kinds[k]);
        HANDLE h = CreateFileW(path, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr);
        DWORD wr;
        WriteFile(h, code->GetBufferPointer(), (DWORD)code->GetBufferSize(), &wr, nullptr);
        CloseHandle(h);
        code->Release();
        written++;
    }
    say("selftest: generated %d shaders\n", written);
    return written ? 0 : 1;
}

int wmain(int argc, wchar_t** argv) {
    if (argc < 2) {
        fwprintf(stderr, L"usage: shadercompile.exe <dir> [--batch N] [--quiet] [--selftest N]\n");
        return 2;
    }
    std::wstring dir = argv[1];
    g_log = _wfopen((dir + L"\\shadercompile.log").c_str(), L"w");
    bool quiet = false;
    size_t batch_size = 4000;
    for (int i = 2; i < argc; i++) {
        if (wcscmp(argv[i], L"--quiet") == 0) quiet = true;
        else if (wcscmp(argv[i], L"--batch") == 0 && i + 1 < argc) batch_size = (size_t)_wtoi(argv[++i]);
        else if (wcscmp(argv[i], L"--selftest") == 0 && i + 1 < argc) {
            if (selftest_generate(dir, _wtoi(argv[++i]))) return 1;
        }
    }

    D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    ID3D11Device* dev = nullptr;
    ID3D11DeviceContext* ctx = nullptr;
    D3D_FEATURE_LEVEL got;
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, levels, 2,
                                   D3D11_SDK_VERSION, &dev, &got, &ctx);
    if (FAILED(hr)) {
        say("D3D11CreateDevice failed: 0x%08lx\n", (unsigned long)hr);
        return 1;
    }
    say("device created (feature level 0x%x)\n", (unsigned)got);

    WIN32_FIND_DATAW fd;
    HANDLE fh = FindFirstFileW((dir + L"\\*.dxbc").c_str(), &fd);
    if (fh == INVALID_HANDLE_VALUE) {
        say("no .dxbc files found\n");
        return 1;
    }
    std::vector<std::wstring> files;
    do { files.push_back(fd.cFileName); } while (FindNextFileW(fh, &fd));
    FindClose(fh);
    say("%zu shader files\n", files.size());

    // Work in batches: create a batch, wait until DXVK's background workers
    // have compiled it (process goes idle), then release it. This bounds
    // memory use and gives real progress ("progress <done>/<total>").
    size_t ok = 0, fail = 0, skipped = 0;
    std::vector<char> buf;
    std::vector<IUnknown*> batch;
    DWORD t0 = GetTickCount();
    say("progress 0/%zu\n", files.size());

    auto wait_idle = []() {
        double last = cpu_seconds();
        int idle = 0;
        while (idle < 2) {
            Sleep(500);
            double now = cpu_seconds();
            idle = (now - last) < 0.05 ? idle + 1 : 0;
            last = now;
        }
    };

    for (size_t i = 0; i < files.size(); i++) {
        const std::wstring& name = files[i];
        if (!read_file(dir + L"\\" + name, buf)) { fail++; continue; }
        // kind = the extension before ".dxbc"
        size_t end = name.rfind(L".dxbc");
        size_t dot = name.rfind(L'.', end - 1);
        std::wstring kind = (dot == std::wstring::npos) ? L"" : name.substr(dot + 1, end - dot - 1);
        IUnknown* obj = nullptr;
        if (kind == L"vs")      hr = dev->CreateVertexShader(buf.data(), buf.size(), nullptr, (ID3D11VertexShader**)&obj);
        else if (kind == L"ps") hr = dev->CreatePixelShader(buf.data(), buf.size(), nullptr, (ID3D11PixelShader**)&obj);
        else if (kind == L"cs") hr = dev->CreateComputeShader(buf.data(), buf.size(), nullptr, (ID3D11ComputeShader**)&obj);
        else if (kind == L"gs") hr = dev->CreateGeometryShader(buf.data(), buf.size(), nullptr, (ID3D11GeometryShader**)&obj);
        else if (kind == L"hs") hr = dev->CreateHullShader(buf.data(), buf.size(), nullptr, (ID3D11HullShader**)&obj);
        else if (kind == L"ds") hr = dev->CreateDomainShader(buf.data(), buf.size(), nullptr, (ID3D11DomainShader**)&obj);
        else { skipped++; continue; }
        if (SUCCEEDED(hr) && obj) { batch.push_back(obj); ok++; }
        else { fail++; if (!quiet) say("failed: %ls (0x%08lx)\n", name.c_str(), (unsigned long)hr); }

        if (batch.size() >= batch_size || i + 1 == files.size()) {
            wait_idle();
            for (IUnknown* o : batch) o->Release();
            batch.clear();
            say("progress %zu/%zu\n", i + 1, files.size());
        }
    }
    say("done: %zu compiled, %zu failed, %zu skipped in %.1fs\n",
        ok, fail, skipped, (GetTickCount() - t0) / 1000.0);

    ctx->Release();
    dev->Release();
    return 0;
}
