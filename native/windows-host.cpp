#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <commdlg.h>
#include <WebView2.h>
#include <wrl/client.h>
#include <atomic>
#include <functional>
#include <iterator>
#include <mutex>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

using Microsoft::WRL::ComPtr;
using ReceiveMessage = void (__cdecl *)(const wchar_t*);
using CreateEnvironment = HRESULT (STDAPICALLTYPE *)(PCWSTR, PCWSTR, ICoreWebView2EnvironmentOptions*,
    ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler*);

namespace {
constexpr wchar_t windowClass[] = L"VISM.NativeHost";
constexpr wchar_t page[] = L"https://app.vism/index.html";
constexpr wchar_t origin[] = L"https://app.vism/";
constexpr UINT deliverMessages = WM_APP + 1;
constexpr UINT handleRequests = WM_APP + 2;
std::atomic<HWND> window;
ReceiveMessage receive;
std::wstring uiFolder;
ComPtr<ICoreWebView2Controller> controller;
ComPtr<ICoreWebView2> view;
std::mutex messagesLock;
std::vector<std::wstring> queuedMessages;
std::vector<std::wstring> queuedRequests;
bool startupFailed = false;
bool dispatchingRequest = false;

template<class Interface, class First, class Second>
class Callback final : public Interface {
    std::atomic<ULONG> references{1};
    std::function<HRESULT(First, Second)> function;
public:
    explicit Callback(std::function<HRESULT(First, Second)> fn) : function(std::move(fn)) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** out) override {
        if (!out) return E_POINTER;
        *out = nullptr;
        if (iid != IID_IUnknown && iid != __uuidof(Interface)) return E_NOINTERFACE;
        *out = static_cast<Interface*>(this);
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references; }
    ULONG STDMETHODCALLTYPE Release() override {
        const auto left = --references;
        if (left == 0) delete this;
        return left;
    }
    HRESULT STDMETHODCALLTYPE Invoke(First first, Second second) override {
        try { return function(first, second); }
        catch (...) { return E_FAIL; }
    }
};

template<class Interface, class First, class Second, class Function>
ComPtr<Interface> MakeCallback(Function function) {
    ComPtr<Interface> value;
    value.Attach(new Callback<Interface, First, Second>(std::move(function)));
    return value;
}

void ResizeView(HWND hwnd) {
    if (controller) {
        RECT bounds{};
        GetClientRect(hwnd, &bounds);
        controller->put_Bounds(bounds);
    }
}

void StartupError(const wchar_t* message) {
    startupFailed = true;
    if (const auto hwnd = window.load()) {
        MessageBoxW(hwnd, message, L"VI Save Manager", MB_OK | MB_ICONERROR);
        DestroyWindow(hwnd);
    }
}

LRESULT CALLBACK WindowProcess(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
    switch (message) {
    case WM_SIZE:
        ResizeView(hwnd);
        return 0;
    case WM_GETMINMAXINFO: {
        auto sizes = reinterpret_cast<MINMAXINFO*>(lParam);
        sizes->ptMinTrackSize = {740, 600};
        return 0;
    }
    case WM_DPICHANGED: {
        const auto rect = reinterpret_cast<const RECT*>(lParam);
        SetWindowPos(hwnd, nullptr, rect->left, rect->top, rect->right - rect->left,
            rect->bottom - rect->top, SWP_NOACTIVATE | SWP_NOZORDER);
        return 0;
    }
    case deliverMessages: {
        std::vector<std::wstring> messages;
        {
            std::lock_guard lock(messagesLock);
            messages.swap(queuedMessages);
        }
        if (view) for (const auto& item : messages) view->PostWebMessageAsJson(item.c_str());
        return 0;
    }
    case handleRequests: {
        if (dispatchingRequest) return 0;
        std::vector<std::wstring> requests;
        requests.swap(queuedRequests);
        dispatchingRequest = true;
        for (const auto& request : requests) {
            if (!window.load()) break;
            receive(request.c_str());
        }
        dispatchingRequest = false;
        if (!queuedRequests.empty() && window.load()) PostMessageW(hwnd, handleRequests, 0, 0);
        return 0;
    }
    case WM_DESTROY:
        window.store(nullptr);
        if (controller) controller->Close();
        view.Reset();
        controller.Reset();
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd, message, wParam, lParam);
}

HRESULT InitializeView(ICoreWebView2Controller* webController) {
    controller = webController;
    HRESULT hr = controller->get_CoreWebView2(&view);
    if (FAILED(hr)) return hr;
    ComPtr<ICoreWebView2_3> webView;
    hr = view.As(&webView);
    if (FAILED(hr)) return hr;
    hr = webView->SetVirtualHostNameToFolderMapping(L"app.vism", uiFolder.c_str(), COREWEBVIEW2_HOST_RESOURCE_ACCESS_KIND_DENY_CORS);
    if (FAILED(hr)) return hr;

    ComPtr<ICoreWebView2Controller2> colorController;
    if (SUCCEEDED(controller.As(&colorController)))
        colorController->put_DefaultBackgroundColor({255, 0x12, 0x0b, 0x1f});

    ComPtr<ICoreWebView2Settings> settings;
    if (SUCCEEDED(view->get_Settings(&settings))) {
        settings->put_AreDefaultContextMenusEnabled(FALSE);
        settings->put_IsStatusBarEnabled(FALSE);
        settings->put_AreDevToolsEnabled(FALSE);
    }
    auto navigation = MakeCallback<ICoreWebView2NavigationStartingEventHandler, ICoreWebView2*, ICoreWebView2NavigationStartingEventArgs*>(
        [](ICoreWebView2*, ICoreWebView2NavigationStartingEventArgs* args) {
            LPWSTR uri = nullptr;
            args->get_Uri(&uri);
            if (!uri || std::wstring_view(uri).substr(0, std::size(origin) - 1) != origin) args->put_Cancel(TRUE);
            CoTaskMemFree(uri);
            return S_OK;
        });
    auto newWindow = MakeCallback<ICoreWebView2NewWindowRequestedEventHandler, ICoreWebView2*, ICoreWebView2NewWindowRequestedEventArgs*>(
        [](ICoreWebView2*, ICoreWebView2NewWindowRequestedEventArgs* args) { return args->put_Handled(TRUE); });
    auto webMessage = MakeCallback<ICoreWebView2WebMessageReceivedEventHandler, ICoreWebView2*, ICoreWebView2WebMessageReceivedEventArgs*>(
        [](ICoreWebView2*, ICoreWebView2WebMessageReceivedEventArgs* args) {
            LPWSTR source = nullptr;
            args->get_Source(&source);
            const bool trusted = source && std::wstring_view(source) == page;
            CoTaskMemFree(source);
            if (!trusted) return S_OK;
            LPWSTR json = nullptr;
            const auto hr = args->get_WebMessageAsJson(&json);
            if (FAILED(hr) || !json) return hr;
            try { queuedRequests.emplace_back(json); }
            catch (...) { CoTaskMemFree(json); return E_OUTOFMEMORY; }
            CoTaskMemFree(json);
            // Dispatch after WebView2 returns so a file dialog can run its modal loop.
            PostMessageW(window.load(), handleRequests, 0, 0);
            return S_OK;
        });
    EventRegistrationToken token{};
    if (FAILED(hr = view->add_NavigationStarting(navigation.Get(), &token))) return hr;
    if (FAILED(hr = view->add_NewWindowRequested(newWindow.Get(), &token))) return hr;
    if (FAILED(hr = view->add_WebMessageReceived(webMessage.Get(), &token))) return hr;
    ResizeView(window.load());
    return view->Navigate(page);
}
}

extern "C" __declspec(dllexport) int __cdecl vism_run(ReceiveMessage callback, const wchar_t* userData,
    const wchar_t* assets, const wchar_t* title, const wchar_t* iconPath) {
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    if (FAILED(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED))) return 1;
    receive = callback;
    uiFolder = assets;
    const auto instance = GetModuleHandleW(nullptr);
    auto icon = static_cast<HICON>(LoadImageW(nullptr, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE));
    WNDCLASSW settings{};
    settings.lpfnWndProc = WindowProcess;
    settings.hInstance = instance;
    settings.hIcon = icon;
    settings.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512));
    settings.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
    settings.lpszClassName = windowClass;
    RegisterClassW(&settings);
    const auto hwnd = CreateWindowExW(0, windowClass, title, WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT, CW_USEDEFAULT, 996, 860, nullptr, nullptr, instance, nullptr);
    if (!hwnd) { CoUninitialize(); return 1; }
    window.store(hwnd);
    ShowWindow(hwnd, SW_SHOW);

    HMODULE loader = LoadLibraryExW(L"WebView2Loader.dll", nullptr, LOAD_LIBRARY_SEARCH_APPLICATION_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    auto create = loader ? reinterpret_cast<CreateEnvironment>(GetProcAddress(loader, "CreateCoreWebView2EnvironmentWithOptions")) : nullptr;
    auto onEnvironment = MakeCallback<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler, HRESULT, ICoreWebView2Environment*>(
        [](HRESULT result, ICoreWebView2Environment* environment) {
            const auto hwnd = window.load();
            if (!hwnd) return S_OK;
            if (FAILED(result) || !environment) {
                StartupError(L"Microsoft Edge WebView2 Runtime is required to start VI Save Manager.");
                return S_OK;
            }
            auto onController = MakeCallback<ICoreWebView2CreateCoreWebView2ControllerCompletedHandler, HRESULT, ICoreWebView2Controller*>(
                [](HRESULT result, ICoreWebView2Controller* webController) {
                    if (!window.load()) return S_OK;
                    if (FAILED(result) || !webController || FAILED(InitializeView(webController)))
                        StartupError(L"VI Save Manager could not initialize the WebView2 window.");
                    return S_OK;
                });
            if (FAILED(environment->CreateCoreWebView2Controller(hwnd, onController.Get())))
                StartupError(L"VI Save Manager could not create its WebView2 window.");
            return S_OK;
        });
    if (!create || FAILED(create(nullptr, userData, nullptr, onEnvironment.Get())))
        StartupError(L"VI Save Manager could not load WebView2. Install or repair the Microsoft Edge WebView2 Runtime.");

    MSG message{};
    BOOL result;
    while ((result = GetMessageW(&message, nullptr, 0, 0)) > 0) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    CoUninitialize();
    if (icon) DestroyIcon(icon);
    return startupFailed || result < 0 ? 1 : 0;
}

extern "C" __declspec(dllexport) void __cdecl vism_post(const wchar_t* json) {
    try {
        std::lock_guard lock(messagesLock);
        if (const auto hwnd = window.load()) {
            queuedMessages.emplace_back(json);
            PostMessageW(hwnd, deliverMessages, 0, 0);
        }
    } catch (...) {}
}

extern "C" __declspec(dllexport) int __cdecl vism_choose_files(int save, wchar_t* buffer, DWORD capacity, const wchar_t* initial) {
    OPENFILENAMEW dialog{};
    dialog.lStructSize = sizeof(dialog);
    dialog.hwndOwner = window.load();
    dialog.lpstrFilter = L"Save archives (*.zip)\0*.zip\0\0";
    dialog.lpstrFile = buffer;
    dialog.nMaxFile = capacity;
    dialog.lpstrInitialDir = initial;
    dialog.lpstrDefExt = L"zip";
    dialog.lpstrTitle = save ? L"Share this save as a zip" : L"Import GTA VI saves";
    dialog.Flags = OFN_EXPLORER | OFN_NOCHANGEDIR | OFN_PATHMUSTEXIST;
    if (save) {
        dialog.Flags |= OFN_OVERWRITEPROMPT;
        return GetSaveFileNameW(&dialog);
    }
    dialog.Flags |= OFN_ALLOWMULTISELECT | OFN_FILEMUSTEXIST;
    return GetOpenFileNameW(&dialog);
}
