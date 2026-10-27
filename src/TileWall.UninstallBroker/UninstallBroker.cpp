// TileWall's one-shot, unelevated registry cleanup broker. No .NET/WinUI dependency.
// The Inno native uninstaller owns confirmation, file removal and the final exit code.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <shldisp.h>
#include <exdisp.h>
#include <servprov.h>
#include <sddl.h>
#include <bcrypt.h>
#include <wrl/client.h>
#include <string>
#include <vector>
#include <memory>
#include <stdexcept>
#include <algorithm>
#include <cwchar>
#ifdef TILEWALL_UNINSTALL_BROKER_TESTS
#include <cstdio>
#endif
using Microsoft::WRL::ComPtr;
using std::wstring;
static constexpr int Accepted = 20, Failed = 4, RollbackFailed = 5;
// Private diagnostic exit stages. Inno accepts only 20 for handoff and 0 for
// confirmed cleanup; no stage code can be mistaken for uninstall success.
static int FailureStage = Failed;
static constexpr wchar_t Registration[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{55D7663D-DFB4-4735-B28A-2EF358DB19F4}_is1";
static constexpr wchar_t RunKey[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
static const wchar_t* Verbs[] = {
    L"Software\\Classes\\*\\shell\\TileWall.AddToWall", L"Software\\Classes\\Directory\\shell\\TileWall.AddToWall",
    L"Software\\Classes\\AllFilesystemObjects\\shell\\TileWall.AddToWall", L"Software\\Classes\\InternetShortcut\\shell\\TileWall.AddToWall",
    L"Software\\Classes\\IE.AssocFile.URL\\shell\\TileWall.AddToWall", L"Software\\Classes\\lnkfile\\shell\\TileWall.AddToWall" };
struct Handle {
    HANDLE value = nullptr;
    explicit Handle(HANDLE h = nullptr) : value(h) {}
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    Handle(const Handle&) = delete; Handle& operator=(const Handle&) = delete;
};
struct Key {
    HKEY value = nullptr;
    ~Key() { if (value) RegCloseKey(value); }
};
static void Require(bool condition) { if (!condition) throw std::runtime_error("Uninstall boundary rejected"); }
static void Win(LSTATUS status) { if (status != ERROR_SUCCESS) throw std::runtime_error("Registry status " + std::to_string(status)); }
static void Com(HRESULT hr) { Require(SUCCEEDED(hr)); }
static bool Same(const wstring& a, const wstring& b) { return _wcsicmp(a.c_str(), b.c_str()) == 0; }
static wstring Parent(const wstring& path) { auto at = path.find_last_of(L'\\'); Require(at != wstring::npos); return path.substr(0, at); }
static wstring Image(HANDLE process) {
    std::vector<wchar_t> text(32768); DWORD count = static_cast<DWORD>(text.size());
    Require(QueryFullProcessImageNameW(process, 0, text.data(), &count) != 0);
    return wstring(text.data(), count);
}
static DWORD Session(DWORD pid) { DWORD result = 0; Require(ProcessIdToSessionId(pid, &result) != 0); return result; }
static std::vector<BYTE> TokenData(HANDLE process, TOKEN_INFORMATION_CLASS kind) {
    Handle token; Require(OpenProcessToken(process, TOKEN_QUERY, &token.value) != 0);
    DWORD bytes = 0; GetTokenInformation(token.value, kind, nullptr, 0, &bytes); Require(bytes > 0 && bytes <= 65536);
    std::vector<BYTE> data(bytes); Require(GetTokenInformation(token.value, kind, data.data(), bytes, &bytes) != 0); return data;
}
static bool Elevated(HANDLE process) { auto data = TokenData(process, TokenElevation); return reinterpret_cast<TOKEN_ELEVATION*>(data.data())->TokenIsElevated != 0; }
static wstring Sid(HANDLE process) {
    auto data = TokenData(process, TokenUser); LPWSTR text = nullptr;
    Require(ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(data.data())->User.Sid, &text) != 0);
    wstring result(text); LocalFree(text); return result;
}
static void OrdinaryDesktopUser() {
    Require(!Elevated(GetCurrentProcess()));
    HWND shell = GetShellWindow(); DWORD pid = 0; Require(shell && GetWindowThreadProcessId(shell, &pid) != 0);
    Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid)); Require(process.value != nullptr);
    Require(Session(pid) == Session(GetCurrentProcessId()) && Sid(process.value) == Sid(GetCurrentProcess()));
    Require(!Elevated(process.value));
}
static void SafePath(const wstring& path, bool file) {
    Require(path.size() > 3 && path[1] == L':' && path[2] == L'\\' && path.find(L'/', 0) == wstring::npos);
    for (size_t end = 3; end <= path.size(); ++end) {
        if (end != path.size() && path[end] != L'\\') continue;
        auto part = path.substr(0, end); DWORD attributes = GetFileAttributesW(part.c_str());
        Require(attributes != INVALID_FILE_ATTRIBUTES && !(attributes & FILE_ATTRIBUTE_REPARSE_POINT));
        Require(end == path.size() ? ((attributes & FILE_ATTRIBUTE_DIRECTORY) == 0) == file : (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0);
    }
}
static wstring StringValue(HKEY key, const wchar_t* name) {
    DWORD type = 0, bytes = 0; Win(RegQueryValueExW(key, name, nullptr, &type, nullptr, &bytes));
    Require(type == REG_SZ && bytes >= sizeof(wchar_t) && bytes <= 65536 && bytes % sizeof(wchar_t) == 0);
    std::vector<wchar_t> value(bytes / sizeof(wchar_t)); Win(RegQueryValueExW(key, name, nullptr, &type, reinterpret_cast<BYTE*>(value.data()), &bytes));
    Require(value.back() == 0 && wcslen(value.data()) + 1 == value.size()); return value.data();
}
struct Installation { wstring Self, Directory, Executable, Uninstaller; };
static Installation ReadInstallation() {
    Installation result; result.Self = Image(GetCurrentProcess()); result.Directory = Parent(result.Self);
    Require(Same(result.Self, result.Directory + L"\\TileWall.UninstallBroker.exe")); SafePath(result.Self, true);
    result.Executable = result.Directory + L"\\TileWall.exe";
    Key key; Win(RegOpenKeyExW(HKEY_LOCAL_MACHINE, Registration, 0, KEY_QUERY_VALUE | KEY_WOW64_64KEY, &key.value));
    DWORD type = 0, protocol = 0, bytes = sizeof(protocol);
    Win(RegQueryValueExW(key.value, L"TWCurrentUserMenuProtocol", nullptr, &type, reinterpret_cast<BYTE*>(&protocol), &bytes));
    Require(type == REG_DWORD && bytes == sizeof(protocol) && protocol == 2);
    auto location = StringValue(key.value, L"InstallLocation"); while (!location.empty() && location.back() == L'\\') location.pop_back();
    Require(Same(location, result.Directory)); result.Uninstaller = StringValue(key.value, L"TWUserUninstallExe");
    auto name = result.Uninstaller.substr(result.Uninstaller.find_last_of(L'\\') + 1);
    Require(Same(Parent(result.Uninstaller), result.Directory) && name.size() == 12 &&
        _wcsnicmp(name.c_str(), L"unins", 5) == 0 && Same(name.substr(8), L".exe") &&
        name[5] >= L'0' && name[5] <= L'9' && name[6] >= L'0' && name[6] <= L'9' && name[7] >= L'0' && name[7] <= L'9');
    SafePath(result.Uninstaller, true); return result;
}
static std::vector<BYTE> Digest(const wstring& path) {
    SafePath(path, true);
    Handle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, nullptr));
    Require(file.value != INVALID_HANDLE_VALUE);
    auto confirmPath = [&]() {
        std::vector<wchar_t> text(32768); DWORD count = GetFinalPathNameByHandleW(file.value, text.data(), static_cast<DWORD>(text.size()), FILE_NAME_NORMALIZED);
        Require(count > 0 && count < text.size()); wstring actual(text.data(), count);
        if (actual.starts_with(L"\\\\?\\")) actual.erase(0, 4);
        Require(Same(actual, path)); SafePath(path, true);
    };
    confirmPath();
    LARGE_INTEGER length{}; Require(GetFileSizeEx(file.value, &length) && length.QuadPart > 0 && length.QuadPart <= 128LL * 1024 * 1024);
    BCRYPT_ALG_HANDLE algorithm = nullptr; BCRYPT_HASH_HANDLE hash = nullptr;
    Require(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0);
    std::vector<BYTE> result(32); BYTE buffer[65536];
    try {
        Require(BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) >= 0);
        DWORD count = 0; LONGLONG total = 0;
        do { Require(ReadFile(file.value, buffer, sizeof(buffer), &count, nullptr) != 0); total += count;
            Require(total <= length.QuadPart); if (count) Require(BCryptHashData(hash, buffer, count, 0) >= 0);
        } while (count);
        Require(total == length.QuadPart && BCryptFinishHash(hash, result.data(), static_cast<ULONG>(result.size()), 0) >= 0); confirmPath();
    } catch (...) { if (hash) BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(algorithm, 0); throw; }
    BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(algorithm, 0); return result;
}
static void AuthenticateUninstaller(HANDLE process, DWORD pid, const Installation& install, bool verifyCloneBytes = true) {
    Require(Session(pid) == Session(GetCurrentProcessId()) && WaitForSingleObject(process, 0) == WAIT_TIMEOUT);
    auto image = Image(process); auto name = image.substr(image.find_last_of(L'\\') + 1);
    auto expectedClone = install.Uninstaller.substr(install.Uninstaller.find_last_of(L'\\') + 1);
    expectedClone.replace(expectedClone.size() - 4, 4, L".tmp");
    // Inno 6.7 uses a protected is-*-uninstall.tmp directory and _unins.tmp.
    // The elevated handoff still verifies its complete bytes against the
    // registered original; the ordinary server later proves the actual client token.
    Require(Same(image, install.Uninstaller) || Same(name, L"_unins.tmp") || Same(name, expectedClone));
    // The elevated handoff can read an alternate UAC account's private temp
    // directory. Ordinary-user cleanup instead authenticates the actual pipe
    // client's identification token after its first message, before any writes.
    if (verifyCloneBytes) Require(Elevated(process) && Digest(image) == Digest(install.Uninstaller));
}
static void AuthenticatePipeClient(HANDLE pipe) {
    Require(ImpersonateNamedPipeClient(pipe) != 0);
    bool elevated = false;
    try {
        Handle token; Require(OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, TRUE, &token.value) != 0);
        TOKEN_ELEVATION elevation{}; DWORD bytes = 0;
        Require(GetTokenInformation(token.value, TokenElevation, &elevation, sizeof(elevation), &bytes) != 0);
        elevated = elevation.TokenIsElevated != 0;
    } catch (...) { RevertToSelf(); throw; }
    Require(RevertToSelf() != 0);
    Require(elevated);
}
static void DesktopLaunch(const Installation& install, const wstring& arguments) {
    Com(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE));
    try {
        HWND shell = GetShellWindow(); DWORD pid = 0; Require(shell && GetWindowThreadProcessId(shell, &pid));
        Require(Session(pid) == Session(GetCurrentProcessId()));
        ComPtr<IShellWindows> windows; Com(CoCreateInstance(CLSID_ShellWindows, nullptr, CLSCTX_LOCAL_SERVER, IID_PPV_ARGS(&windows)));
        VARIANT empty; VariantInit(&empty); long window = 0; ComPtr<IDispatch> desktop;
        Com(windows->FindWindowSW(&empty, &empty, SWC_DESKTOP, &window, SWFO_NEEDDISPATCH, &desktop));
        ComPtr<IServiceProvider> services; Com(desktop.As(&services)); ComPtr<IShellBrowser> browser;
        Com(services->QueryService(SID_STopLevelBrowser, IID_PPV_ARGS(&browser)));
        HWND actual = nullptr; Com(browser->GetWindow(&actual)); DWORD actualPid = 0;
        Require(actual && GetWindowThreadProcessId(actual, &actualPid) && actualPid == pid);
        ComPtr<IShellView> view; Com(browser->QueryActiveShellView(&view)); ComPtr<IDispatch> background;
        Com(view->GetItemObject(SVGIO_BACKGROUND, IID_PPV_ARGS(&background)));
        ComPtr<IShellFolderViewDual> folder; Com(background.As(&folder)); ComPtr<IDispatch> application; Com(folder->get_Application(&application));
        ComPtr<IShellDispatch2> dispatch; Com(application.As(&dispatch));
        BSTR file = SysAllocString(install.Self.c_str()); BSTR args = SysAllocString(arguments.c_str()); BSTR dir = SysAllocString(install.Directory.c_str());
        Require(file && args && dir); VARIANT va, vd, op, show; VariantInit(&va); VariantInit(&vd); VariantInit(&op); VariantInit(&show);
        va.vt = vd.vt = VT_BSTR; va.bstrVal = args; vd.bstrVal = dir; show.vt = VT_I4; show.lVal = SW_SHOWNORMAL;
        HRESULT hr = dispatch->ShellExecute(file, va, vd, op, show); SysFreeString(file); SysFreeString(args); SysFreeString(dir); Com(hr);
        Require(GetShellWindow() == shell);
    } catch (...) { CoUninitialize(); throw; }
    CoUninitialize();
}
struct Value { wstring Name; DWORD Type; std::vector<BYTE> Bytes; };
struct Node { std::vector<Value> Values; std::vector<std::pair<wstring, Node>> Children; };
static Node Snapshot(HKEY key, unsigned depth, size_t& budget) {
    Require(depth < 32); Node node;
    for (DWORD i = 0;; ++i) {
        wchar_t name[16384]; DWORD nameLength = ARRAYSIZE(name), type = 0, size = 0;
        auto status = RegEnumValueW(key, i, name, &nameLength, nullptr, &type, nullptr, &size); if (status == ERROR_NO_MORE_ITEMS) break; Win(status);
        budget += size + (nameLength + 1) * sizeof(wchar_t); Require(budget <= 8 * 1024 * 1024 && i < 4096);
        Value value{wstring(name, nameLength), type, std::vector<BYTE>(size)};
        Win(RegQueryValueExW(key, value.Name.c_str(), nullptr, &type, value.Bytes.data(), &size)); Require(type == value.Type && size == value.Bytes.size());
        node.Values.push_back(std::move(value));
    }
    for (DWORD i = 0;; ++i) {
        wchar_t name[256]; DWORD length = ARRAYSIZE(name); auto status = RegEnumKeyExW(key, i, name, &length, nullptr, nullptr, nullptr, nullptr);
        if (status == ERROR_NO_MORE_ITEMS) break; Win(status); Require(i < 1024); budget += sizeof(name); Require(budget <= 8 * 1024 * 1024);
        Key child; Win(RegOpenKeyExW(key, name, 0, KEY_READ | KEY_WOW64_64KEY, &child.value));
        node.Children.emplace_back(wstring(name, length), Snapshot(child.value, depth + 1, budget));
    }
    return node;
}
static void Restore(HKEY key, const Node& node) {
    for (const auto& value : node.Values) Win(RegSetValueExW(key, value.Name.c_str(), 0, value.Type, value.Bytes.data(), static_cast<DWORD>(value.Bytes.size())));
    for (const auto& child : node.Children) { Key created; Win(RegCreateKeyExW(key, child.first.c_str(), 0, nullptr, 0,
        KEY_ALL_ACCESS | KEY_WOW64_64KEY, nullptr, &created.value, nullptr)); Restore(created.value, child.second); }
}
static void DeleteTree(const wstring& path, HKEY userRoot = HKEY_CURRENT_USER) {
    Key parent; auto status = RegOpenKeyExW(userRoot, Parent(path).c_str(), 0, KEY_ALL_ACCESS | KEY_WOW64_64KEY, &parent.value);
    if (status == ERROR_FILE_NOT_FOUND) return; Win(status); auto name = path.substr(path.find_last_of(L'\\') + 1);
    status = RegDeleteTreeW(parent.value, name.c_str()); if (status != ERROR_FILE_NOT_FOUND) Win(status);
}
static bool Owned(HKEY key, const wstring& executable) {
    Key command; auto status = RegOpenKeyExW(key, L"command", 0, KEY_QUERY_VALUE | KEY_WOW64_64KEY, &command.value);
    if (status == ERROR_FILE_NOT_FOUND) return false; Win(status);
    DWORD type = 0, size = 0; status = RegQueryValueExW(command.value, L"DelegateExecute", nullptr, &type, nullptr, &size);
    if (status != ERROR_FILE_NOT_FOUND) return false;
    try {
        auto value = StringValue(command.value, L""); auto expected = L"\"" + executable + L"\" --add \"%1\"";
        return value.size() == expected.size() && Same(value.substr(0, executable.size() + 2), expected.substr(0, executable.size() + 2)) &&
            value.substr(executable.size() + 2) == expected.substr(executable.size() + 2);
    } catch (...) { return false; }
}
static bool Transfer(HANDLE pipe, BYTE& value, bool write) {
    Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr)); if (!event.value) return false;
    OVERLAPPED operation{}; operation.hEvent = event.value; DWORD count = 0;
    BOOL done = write ? WriteFile(pipe, &value, 1, &count, &operation) : ReadFile(pipe, &value, 1, &count, &operation);
    if (!done && GetLastError() != ERROR_IO_PENDING) return false;
    if (!done && WaitForSingleObject(event.value, 5000) != WAIT_OBJECT_0) {
        CancelIoEx(pipe, &operation); GetOverlappedResult(pipe, &operation, &count, TRUE); return false;
    }
    return GetOverlappedResult(pipe, &operation, &count, FALSE) && count == 1;
}
static bool ReadByte(HANDLE pipe, HANDLE parent, BYTE& value, DWORD timeout) {
    ULONGLONG end = GetTickCount64() + timeout;
    while (GetTickCount64() < end && WaitForSingleObject(parent, 0) == WAIT_TIMEOUT) {
        DWORD available = 0; if (!PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr)) return false;
        if (available) return Transfer(pipe, value, false); Sleep(25);
    }
    return false;
}
static void WriteByte(HANDLE pipe, BYTE value) { Require(Transfer(pipe, value, true)); }
static bool NotificationImageAvailable(const wstring& path) {
    // This is only a bounded sanity check for the optional managed cleanup,
    // not a PE validator or an authentication substitute. Do not ask Windows
    // to launch an obviously truncated/non-PE apphost or load its broken CLR.
    try {
        SafePath(path, true);
        Handle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
        if (file.value == INVALID_HANDLE_VALUE) return false;
        LARGE_INTEGER size{}; IMAGE_DOS_HEADER dos{}; DWORD count = 0;
        if (!GetFileSizeEx(file.value, &size) || !ReadFile(file.value, &dos, sizeof(dos), &count, nullptr) ||
            count != sizeof(dos) || dos.e_magic != IMAGE_DOS_SIGNATURE || dos.e_lfanew < static_cast<LONG>(sizeof(dos)) ||
            dos.e_lfanew > 1024 * 1024 || static_cast<LONGLONG>(dos.e_lfanew) + static_cast<LONGLONG>(sizeof(DWORD)) > size.QuadPart) return false;
        LARGE_INTEGER offset{}; offset.QuadPart = dos.e_lfanew; DWORD signature = 0;
        return SetFilePointerEx(file.value, offset, nullptr, FILE_BEGIN) &&
            ReadFile(file.value, &signature, sizeof(signature), &count, nullptr) && count == sizeof(signature) &&
            signature == IMAGE_NT_SIGNATURE;
    } catch (...) { return false; }
}
static void NotificationCleanup(const Installation& install, const wstring& nonce) {
    if (nonce.empty() || !NotificationImageAvailable(install.Executable) ||
        !NotificationImageAvailable(install.Directory + L"\\TileWall.dll") ||
        !NotificationImageAvailable(install.Directory + L"\\coreclr.dll")) return;
    // Optional SDK cleanup runs only after confirmation. Missing/corrupt managed
    // runtime and SDK cleanup errors are best effort. Releasing our child image
    // is mandatory: inability to stop it safely rejects uninstall before HKCU writes.
    PSECURITY_DESCRIPTOR descriptor = nullptr; Handle process, thread, pipe, event;
    OVERLAPPED connection{}; bool pending = false;
    try {
        SafePath(install.Executable, true);
        auto sddl = L"D:P(A;;GA;;;" + Sid(GetCurrentProcess()) + L")";
        Require(ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &descriptor, nullptr));
        SECURITY_ATTRIBUTES security{sizeof(security), descriptor, FALSE}; auto name = L"\\\\.\\pipe\\TileWall.Uninstall." + nonce + L".notify";
        pipe.value = CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE | FILE_FLAG_OVERLAPPED,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 1024, 1024, 0, &security);
        LocalFree(descriptor); descriptor = nullptr; Require(pipe.value != INVALID_HANDLE_VALUE);
        event.value = CreateEventW(nullptr, TRUE, FALSE, nullptr); Require(event.value != nullptr); connection.hEvent = event.value;
        BOOL connected = ConnectNamedPipe(pipe.value, &connection); auto error = connected ? ERROR_SUCCESS : GetLastError();
        pending = error == ERROR_IO_PENDING; Require(pending || error == ERROR_SUCCESS || error == ERROR_PIPE_CONNECTED);
        auto command = L"\"" + install.Executable + L"\" --uninstall-notification-cleanup " + nonce + L" " + std::to_wstring(GetCurrentProcessId());
        STARTUPINFOW startup{}; startup.cb = sizeof(startup); PROCESS_INFORMATION child{};
        // Inherit this management process's noninteractive error mode, including
        // before the child's loader runs. CREATE_DEFAULT_ERROR_MODE would undo it.
        Require(CreateProcessW(install.Executable.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr,
            install.Directory.c_str(), &startup, &child)); process.value = child.hProcess; thread.value = child.hThread;
        if (pending) {
            HANDLE waits[]{event.value, process.value}; Require(WaitForMultipleObjects(2, waits, FALSE, 10000) == WAIT_OBJECT_0);
            DWORD ignored; Require(GetOverlappedResult(pipe.value, &connection, &ignored, FALSE)); pending = false;
        }
        ULONG client = 0; Require(GetNamedPipeClientProcessId(pipe.value, &client) && client == child.dwProcessId);
        WriteByte(pipe.value, 0x4E);
        Require(WaitForSingleObject(process.value, 10000) == WAIT_OBJECT_0);
    } catch (...) { /* SDK work is best effort; the child-exit contract below is mandatory. */ }
    if (descriptor) LocalFree(descriptor);
    if (pending) { CancelIoEx(pipe.value, &connection); DWORD ignored; GetOverlappedResult(pipe.value, &connection, &ignored, TRUE); }
    if (process.value && WaitForSingleObject(process.value, 0) == WAIT_TIMEOUT) {
        // This is our fixed, authenticated cleanup-only child, never a user GUI.
        Require(TerminateProcess(process.value, Failed) != 0);
        Require(WaitForSingleObject(process.value, 5000) == WAIT_OBJECT_0);
    }
}
static int Cleanup(HANDLE pipe, HANDLE parent, const Installation& install, const wstring& nonce = L"", HKEY userRoot = HKEY_CURRENT_USER) {
    BYTE request = 0; if (!ReadByte(pipe, parent, request, 120000)) return Failed; Require(request == 0x44);
    if (userRoot == HKEY_CURRENT_USER) AuthenticatePipeClient(pipe);
    NotificationCleanup(install, nonce);
    auto mutexName = L"Global\\TileWall.ContextMenu.Write." + Sid(GetCurrentProcess()) + L".v1";
    Handle mutex(CreateMutexW(nullptr, FALSE, mutexName.c_str())); Require(mutex.value != nullptr);
    auto waited = WaitForSingleObject(mutex.value, 5000); Require(waited == WAIT_OBJECT_0 || waited == WAIT_ABANDONED);
    struct Saved { wstring Path; Node Data; }; std::vector<Saved> before; std::vector<size_t> changed;
    Value startup{}; bool startupOwned = false, startupChanged = false; int result = Failed;
    try {
        size_t budget = 0;
        for (auto path : Verbs) {
            Key key; auto status = RegOpenKeyExW(userRoot, path, 0, KEY_READ | KEY_WOW64_64KEY, &key.value);
            if (status == ERROR_FILE_NOT_FOUND) continue; Win(status);
            if (Owned(key.value, install.Executable)) before.push_back({path, Snapshot(key.value, 0, budget)});
        }
        { Key key; auto status = RegOpenKeyExW(userRoot, RunKey, 0, KEY_QUERY_VALUE | KEY_WOW64_64KEY, &key.value);
            if (status != ERROR_FILE_NOT_FOUND) { Win(status); DWORD type = 0, size = 0;
                status = RegQueryValueExW(key.value, L"TileWall", nullptr, &type, nullptr, &size);
                if (status != ERROR_FILE_NOT_FOUND) { Win(status); Require(size <= 65536); startup = {L"TileWall", type, std::vector<BYTE>(size)};
                    Win(RegQueryValueExW(key.value, L"TileWall", nullptr, &type, startup.Bytes.data(), &size));
                    wstring expected = L"\"" + install.Executable + L"\" --background";
                    startupOwned = (type == REG_SZ || type == REG_EXPAND_SZ) && size == (expected.size() + 1) * sizeof(wchar_t) &&
                        memcmp(startup.Bytes.data(), expected.c_str(), size) == 0;
                }
            }
        }
        for (size_t i = 0; i < before.size(); ++i) { changed.push_back(i); DeleteTree(before[i].Path, userRoot); }
        if (startupOwned) { Key key; Win(RegOpenKeyExW(userRoot, RunKey, 0, KEY_SET_VALUE | KEY_WOW64_64KEY, &key.value));
            startupChanged = true; auto status = RegDeleteValueW(key.value, L"TileWall"); if (status != ERROR_FILE_NOT_FOUND) Win(status); }
        WriteByte(pipe, 0x4F); Require(ReadByte(pipe, parent, request, 5000) && request == 0x41); result = 0;
    } catch (const std::exception& error) {
#ifdef TILEWALL_UNINSTALL_BROKER_TESTS
        std::fprintf(stderr, "Native cleanup transaction failed: %s (lastError=%lu)\n", error.what(), GetLastError());
#else
        (void)error;
#endif
        bool restored = true;
        if (startupChanged) try { Key key; Win(RegCreateKeyExW(userRoot, RunKey, 0, nullptr, 0,
            KEY_SET_VALUE | KEY_WOW64_64KEY, nullptr, &key.value, nullptr));
            Win(RegSetValueExW(key.value, L"TileWall", 0, startup.Type, startup.Bytes.data(), static_cast<DWORD>(startup.Bytes.size()))); } catch (...) { restored = false; }
        for (auto it = changed.rbegin(); it != changed.rend(); ++it) try {
            auto& saved = before[*it]; DeleteTree(saved.Path, userRoot); Key key;
            Win(RegCreateKeyExW(userRoot, saved.Path.c_str(), 0, nullptr, 0, KEY_ALL_ACCESS | KEY_WOW64_64KEY, nullptr, &key.value, nullptr)); Restore(key.value, saved.Data);
        } catch (...) { restored = false; }
        result = restored ? Failed : RollbackFailed; try { WriteByte(pipe, 0x45); } catch (...) {}
    }
    ReleaseMutex(mutex.value); if (!changed.empty()) SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr); return result;
}
static int Serve(const Installation& install, const wstring& nonce, DWORD parentPid) {
    FailureStage = 40;
    OrdinaryDesktopUser(); Handle parent(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, parentPid)); Require(parent.value != nullptr);
    FailureStage = 41;
    AuthenticateUninstaller(parent.value, parentPid, install, false);
    FailureStage = 42;
    auto sddl = L"D:P(A;;GA;;;" + Sid(GetCurrentProcess()) + L")(A;;GA;;;BA)(A;;GA;;;SY)";
    PSECURITY_DESCRIPTOR descriptor = nullptr; Require(ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &descriptor, nullptr));
    SECURITY_ATTRIBUTES security{sizeof(security), descriptor, FALSE}; auto name = L"\\\\.\\pipe\\TileWall.Uninstall." + nonce;
    Handle pipe(CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE | FILE_FLAG_OVERLAPPED,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 1024, 1024, 0, &security)); LocalFree(descriptor);
    Require(pipe.value != INVALID_HANDLE_VALUE); Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr)); Require(event.value != nullptr);
    FailureStage = 43;
    OVERLAPPED overlapped{}; overlapped.hEvent = event.value; BOOL connected = ConnectNamedPipe(pipe.value, &overlapped);
    DWORD error = connected ? ERROR_SUCCESS : GetLastError();
    if (error == ERROR_IO_PENDING) { HANDLE waits[]{event.value, parent.value};
        if (WaitForMultipleObjects(2, waits, FALSE, 30000) != WAIT_OBJECT_0) { CancelIoEx(pipe.value, &overlapped); DWORD ignored; GetOverlappedResult(pipe.value, &overlapped, &ignored, TRUE); return Failed; }
        DWORD ignored; Require(GetOverlappedResult(pipe.value, &overlapped, &ignored, FALSE));
    } else Require(error == ERROR_SUCCESS || error == ERROR_PIPE_CONNECTED);
    FailureStage = 44;
    ULONG client = 0; Require(GetNamedPipeClientProcessId(pipe.value, &client) && client == parentPid);
    AuthenticateUninstaller(parent.value, parentPid, install, false);
    // Switch to blocking byte IO after the bounded connection. PeekNamedPipe bounds every read.
    FailureStage = 45;
    return Cleanup(pipe.value, parent.value, install, nonce);
}
#ifndef TILEWALL_UNINSTALL_BROKER_TESTS
int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    // Process-wide mode is deliberate: CreateProcess children inherit it;
    // SetThreadErrorMode alone would not establish that child-process contract.
    // This one-shot helper has no user UI, and must never leave loader dialogs
    // behind when the installed managed runtime is damaged.
    SetErrorMode(GetErrorMode() | SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    try {
        int count = 0; auto arguments = CommandLineToArgvW(GetCommandLineW(), &count); Require(arguments != nullptr);
        struct Args { LPWSTR* Value; ~Args() { LocalFree(Value); } } release{arguments};
        Require(count == 4); wstring mode(arguments[1]), nonce(arguments[2]), pidText(arguments[3]);
        Require((mode == L"--handoff" || mode == L"--broker") && nonce.size() == 32 &&
            std::all_of(nonce.begin(), nonce.end(), [](wchar_t c) { return (c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f'); }) &&
            !pidText.empty() && pidText.size() <= 10 && std::all_of(pidText.begin(), pidText.end(), [](wchar_t c) { return c >= L'0' && c <= L'9'; }));
        auto parsed = std::stoull(pidText); Require(parsed > 0 && parsed <= MAXDWORD); DWORD pid = static_cast<DWORD>(parsed);
        FailureStage = 30;
        auto install = ReadInstallation();
        if (mode == L"--handoff") {
            FailureStage = 31;
            Handle parent(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, pid)); Require(parent.value != nullptr);
            AuthenticateUninstaller(parent.value, pid, install);
            FailureStage = 32;
            DesktopLaunch(install, L"--broker " + nonce + L" " + pidText); return Accepted;
        }
        return Serve(install, nonce, pid);
    } catch (...) { return FailureStage; }
}
#endif
