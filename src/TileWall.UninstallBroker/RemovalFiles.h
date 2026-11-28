// Windows handles pin each ancestor until the actual operation has completed.
static bool SamePath(const wstring& left, const wstring& right) {
    return CompareStringOrdinal(left.c_str(), -1, right.c_str(), -1, TRUE) == CSTR_EQUAL;
}

static bool ValidRelativePath(const wstring& path) {
    if (path.empty() || path.size() > 32763) return false;
    size_t begin = 0;
    for (size_t end = 0; end <= path.size(); ++end) {
        if (end != path.size() && path[end] != L'\\') {
            if (path[end] < 32 || wcschr(L"/:*?\"<>|", path[end])) return false;
            continue;
        }
        if (end == begin || end - begin > 255 || path[end - 1] == L'.' || path[end - 1] == L' ') return false;
        auto component = path.substr(begin, end - begin);
        auto base = component.substr(0, component.find(L'.'));
        while (!base.empty() && base.back() == L' ') base.pop_back();
        if (SamePath(base, L"CON") || SamePath(base, L"PRN") || SamePath(base, L"AUX") ||
            SamePath(base, L"NUL") || SamePath(base, L"CONIN$") || SamePath(base, L"CONOUT$")) return false;
        if (base.size() == 4 && (SamePath(base.substr(0, 3), L"COM") || SamePath(base.substr(0, 3), L"LPT")) &&
            wcschr(L"123456789\u00B9\u00B2\u00B3", base.back())) return false;
        begin = end + 1;
    }
    return true;
}

static bool IsDrivePath(const wstring& path) {
    return path.size() >= 3 && ((path[0] >= L'A' && path[0] <= L'Z') ||
        (path[0] >= L'a' && path[0] <= L'z')) && path[1] == L':' && path[2] == L'\\' &&
        (path.size() == 3 || ValidRelativePath(path.substr(3)));
}

static wstring Extended(const wstring& path) { return L"\\\\?\\" + path; }
static bool Missing(DWORD error) { return error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND; }

static Handle OpenVerified(const wstring& path, bool directory, DWORD access, bool allowMissing = false,
    DWORD sharing = FILE_SHARE_READ) {
    Handle handle(CreateFileW(Extended(path).c_str(), access | FILE_READ_ATTRIBUTES, sharing, nullptr,
        OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    if (!handle.valid() && allowMissing && Missing(GetLastError())) return Handle{};
    Require(handle.valid());
    FILE_ATTRIBUTE_TAG_INFO attributes{};
    Require(GetFileInformationByHandleEx(handle.value, FileAttributeTagInfo, &attributes, sizeof(attributes)) &&
        !(attributes.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) &&
        ((attributes.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) == directory);
    std::vector<wchar_t> text(32768);
    DWORD count = GetFinalPathNameByHandleW(handle.value, text.data(), static_cast<DWORD>(text.size()), FILE_NAME_NORMALIZED);
    Require(count && count < text.size());
    wstring actual(text.data(), count);
    if (path.size() == 3 && actual.size() == 6 && actual[5] == L':') actual += L'\\';
    Require(SamePath(actual, Extended(path)));
    return handle;
}

static Handle OpenWithAncestors(const wstring& path, bool directory, DWORD access,
    std::vector<Handle>& ancestors, bool allowMissing = false, DWORD sharing = FILE_SHARE_READ) {
    Require(IsDrivePath(path));
    if (path.size() > 3) {
        ancestors.push_back(OpenVerified(path.substr(0, 3), true, 0));
        for (auto end = path.find(L'\\', 3); end != wstring::npos; end = path.find(L'\\', end + 1)) {
            auto ancestor = OpenVerified(path.substr(0, end), true, 0, allowMissing);
            if (!ancestor.valid()) return Handle{};
            ancestors.push_back(std::move(ancestor));
        }
    }
    return OpenVerified(path, directory, access, allowMissing, sharing);
}

static void MarkForDeletion(HANDLE file) {
    FILE_DISPOSITION_INFO disposition{TRUE};
    Require(SetFileInformationByHandle(file, FileDispositionInfo, &disposition, sizeof(disposition)));
}

static std::vector<BYTE> Sha256Bytes(const void* data, ULONG size) {
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    Require(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0);
    std::vector<BYTE> digest(32);
    auto status = BCryptHash(algorithm, nullptr, 0, static_cast<PUCHAR>(const_cast<void*>(data)),
        size, digest.data(), static_cast<ULONG>(digest.size()));
    BCryptCloseAlgorithmProvider(algorithm, 0);
    Require(status >= 0);
    return digest;
}

static wstring Hex(const std::vector<BYTE>& digest) {
    wstring text;
    for (BYTE byte : digest) {
        text += L"0123456789ABCDEF"[byte >> 4];
        text += L"0123456789ABCDEF"[byte & 15];
    }
    return text;
}

static wstring LocalDataRoot() {
    PWSTR known = nullptr;
    Com(SHGetKnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_NO_PACKAGE_REDIRECTION, nullptr, &known));
    std::unique_ptr<wchar_t, decltype(&CoTaskMemFree)> freeKnown(known, CoTaskMemFree);
    Require(known != nullptr);
    std::vector<wchar_t> text(32768);
    DWORD count = GetFullPathNameW(known, static_cast<DWORD>(text.size()), text.data(), nullptr);
    Require(count > 3 && count < text.size());
    wstring local(text.data(), count);
    while (local.size() > 3 && local.back() == L'\\') local.pop_back();
    Require(local.size() > 3 && IsDrivePath(local));
    return local + L"\\TileWall";
}

static wstring LegacyInstanceHash(const wstring& root) {
    int length = LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_UPPERCASE, root.c_str(), -1, nullptr, 0, nullptr, nullptr, 0);
    Require(length > 1);
    std::vector<wchar_t> upper(static_cast<size_t>(length));
    Require(LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_UPPERCASE, root.c_str(), -1, upper.data(),
        length, nullptr, nullptr, 0) == length);
    int bytes = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, upper.data(), length - 1, nullptr, 0, nullptr, nullptr);
    Require(bytes > 0);
    std::vector<char> utf8(static_cast<size_t>(bytes));
    Require(WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, upper.data(), length - 1, utf8.data(),
        bytes, nullptr, nullptr) == bytes);
    return Hex(Sha256Bytes(utf8.data(), static_cast<ULONG>(bytes))).substr(0, 24);
}

class OwnedMutex {
    Handle handle;
    bool owned = false;
public:
    explicit OwnedMutex(const wstring& name) : handle(CreateMutexW(nullptr, FALSE, name.c_str())) {
        Require(handle.valid());
    }
    ~OwnedMutex() { if (owned) ReleaseMutex(handle.value); }
    bool TryLock(DWORD timeout = 0) {
        Require(!owned);
        auto wait = WaitForSingleObject(handle.value, timeout);
        owned = wait == WAIT_OBJECT_0 || wait == WAIT_ABANDONED;
        return owned;
    }
};

static bool ConfirmWindowProcess(HWND window, DWORD process) {
    DWORD found = 0;
    return GetWindowThreadProcessId(window, &found) != 0 && found == process;
}

static BOOL CALLBACK CloseLegacyWindow(HWND window, LPARAM process) {
    if (ConfirmWindowProcess(window, static_cast<DWORD>(process))) {
        wchar_t title[16]{};
        if (GetWindowTextW(window, title, ARRAYSIZE(title)) && wcscmp(title, L"TileWall") == 0 &&
            ConfirmWindowProcess(window, static_cast<DWORD>(process)) && PostMessageW(window, WM_CLOSE, 0, 0)) return FALSE;
    }
    return TRUE;
}

static void CloseDataOwner(const wstring& hash, const wstring& userSid) {
    HWND host = FindWindowW(L"TileWall_ShellHost_Class", (L"TileWall.ShellHost." + hash).c_str());
    DWORD pid = 0; Require(host && GetWindowThreadProcessId(host, &pid));
    Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, pid));
    Require(process.valid() && Sid(process.value) == userSid && Session(pid) == Session(GetCurrentProcessId()));
    auto image = Image(process.value);
    Require(SamePath(image.substr(image.find_last_of(L'\\') + 1), L"TileWall.exe") &&
        WaitForSingleObject(process.value, 0) == WAIT_TIMEOUT && ConfirmWindowProcess(host, pid));
    auto close = RegisterWindowMessageW(L"TileWall.SingleInstance.CloseForUninstall");
    DWORD_PTR accepted = 0;
    if (!close || !SendMessageTimeoutW(host, close, 0, 0, SMTO_ABORTIFHUNG | SMTO_BLOCK, 1000, &accepted) || accepted != 1)
        EnumWindows(CloseLegacyWindow, static_cast<LPARAM>(pid));
    Require(WaitForSingleObject(process.value, 30000) == WAIT_OBJECT_0);
}

static bool SessionHasEnded(DWORD target, DWORD current) {
    LPWSTR state = nullptr;
    DWORD bytes = 0;
    if (WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, target, WTSConnectState, &state, &bytes)) {
        WTSFreeMemory(state);
        return false;
    }
    auto error = GetLastError();
    if (error != ERROR_CTX_WINSTATION_NOT_FOUND && error != ERROR_FILE_NOT_FOUND) return false;

    // Win10 can report ERROR_FILE_NOT_FOUND for a retired station while LSA still
    // retains its logon record. Corroborate this with the current session table.
    PWTS_SESSION_INFOW sessions = nullptr;
    DWORD count = 0;
    if (!WTSEnumerateSessionsW(WTS_CURRENT_SERVER_HANDLE, 0, 1, &sessions, &count) || !sessions) return false;
    std::unique_ptr<WTS_SESSION_INFOW, decltype(&WTSFreeMemory)> release(sessions, WTSFreeMemory);
    bool foundCurrent = false;
    for (DWORD index = 0; index < count; ++index) {
        if (sessions[index].SessionId == target) return false;
        if (sessions[index].SessionId == current) foundCurrent = true;
    }
    return foundCurrent;
}

static void VerifyNoOtherLogonSession() {
    // Existing releases use the Local mutex. The ordinary user may read their own
    // LSA session records and must close other logon sessions before deleting.
    auto token = TokenData(GetCurrentProcess(), TokenUser);
    auto sid = reinterpret_cast<const TOKEN_USER*>(token.data())->User.Sid;
    auto currentSession = Session(GetCurrentProcessId());
    ULONG count = 0; PLUID sessions = nullptr;
    Require(LsaEnumerateLogonSessions(&count, &sessions) == 0);
    std::unique_ptr<LUID, decltype(&LsaFreeReturnBuffer)> release(sessions, LsaFreeReturnBuffer);
    for (ULONG index = 0; index < count; ++index) {
        PSECURITY_LOGON_SESSION_DATA data = nullptr;
        if (LsaGetLogonSessionData(sessions + index, &data) != 0 || !data) continue;
        std::unique_ptr<SECURITY_LOGON_SESSION_DATA, decltype(&LsaFreeReturnBuffer)> entry(data, LsaFreeReturnBuffer);
        if (data->Sid && EqualSid(sid, data->Sid) && data->Session != currentSession && data->Session != 0)
            Require(SessionHasEnded(data->Session, currentSession));
    }
}

static void RequireProgramStopped(const wstring& executable, DWORD graceMilliseconds = 0) {
    const auto deadline = GetTickCount64() + graceMilliseconds;
    Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
    Require(snapshot.valid());
    PROCESSENTRY32W entry{}; entry.dwSize = sizeof(entry);
    Require(Process32FirstW(snapshot.value, &entry));
    do {
        if (!SamePath(entry.szExeFile, L"TileWall.exe")) continue;
        Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, entry.th32ProcessID));
        if (!process.valid()) continue;
        if (WaitForSingleObject(process.value, 0) != WAIT_TIMEOUT) continue;
        auto waitForExit = [&]() {
            const auto now = GetTickCount64();
            const auto remaining = now < deadline ? static_cast<DWORD>(deadline - now) : 0;
            return WaitForSingleObject(process.value, remaining) == WAIT_OBJECT_0;
        };
        bool ownsImage = false;
        try { ownsImage = SamePath(Image(process.value), executable); }
        catch (...) {
            // The taskbar recovery helper can exit between the process scan and
            // the image query. A still-running process with unknown identity blocks.
            Require(waitForExit());
            continue;
        }
        if (ownsImage) Require(waitForExit());
    } while (Process32NextW(snapshot.value, &entry));
    Require(GetLastError() == ERROR_NO_MORE_FILES);
}

static void ConfirmFileIdentity(HANDLE expected, HANDLE actual) {
    BY_HANDLE_FILE_INFORMATION before{}, current{};
    Require(GetFileInformationByHandle(expected, &before) && GetFileInformationByHandle(actual, &current) &&
        before.dwVolumeSerialNumber == current.dwVolumeSerialNumber &&
        before.nFileIndexHigh == current.nFileIndexHigh && before.nFileIndexLow == current.nFileIndexLow);
}

static void WalkDataTree(const wstring& path, bool remove, bool& removed, HANDLE pinnedRoot = nullptr, unsigned depth = 0) {
    Require(depth < 256);
    auto directory = OpenVerified(path, true, remove ? DELETE : 0);
    if (pinnedRoot) ConfirmFileIdentity(pinnedRoot, directory.value);
    WIN32_FIND_DATAW child{};
    auto search = FindFirstFileW(Extended(path + L"\\*").c_str(), &child);
    if (search != INVALID_HANDLE_VALUE) {
        struct FindCursor { HANDLE value; ~FindCursor() { FindClose(value); } } cursor{search};
        do {
            wstring name(child.cFileName);
            if (name == L"." || name == L"..") continue;
            Require(ValidRelativePath(name) && !(child.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT));
            auto full = path + L'\\' + name;
            if (child.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
                WalkDataTree(full, remove, removed, nullptr, depth + 1);
            } else {
                auto file = OpenVerified(full, false, remove ? DELETE : 0);
                if (remove) { MarkForDeletion(file.value); removed = true; }
            }
        } while (FindNextFileW(search, &child));
        Require(GetLastError() == ERROR_NO_MORE_FILES);
    } else {
        Require(GetLastError() == ERROR_FILE_NOT_FOUND);
    }
    if (remove) { MarkForDeletion(directory.value); removed = true; }
}

class SharedDataRemoval {
    const wstring root = LocalDataRoot();
    const wstring sid = Sid(GetCurrentProcess());
    const wstring hash = LegacyInstanceHash(root);
    OwnedMutex local{L"Local\\TileWall.Instance." + hash};
    OwnedMutex global{L"Global\\TileWall.Data." + sid + L".v1"};
    std::vector<Handle> ancestors;
    std::optional<Handle> pinnedRoot;
public:
    explicit SharedDataRemoval(const wstring& executable) {
        auto program = Parent(executable);
        Require(!SamePath(program, root) && !(program.size() > root.size() && program[root.size()] == L'\\' &&
            SamePath(program.substr(0, root.size()), root)));
        if (!local.TryLock()) {
            CloseDataOwner(hash, sid);
            Require(local.TryLock(2000));
        }
        Require(global.TryLock());
        VerifyNoOtherLogonSession();
        // The wall process has exited and both data gates are held. Allow its
        // same-image taskbar recovery helper to finish without forcing it out.
        RequireProgramStopped(executable, 5000);
        pinnedRoot.emplace(OpenWithAncestors(root, true, 0, ancestors, true, FILE_SHARE_READ | FILE_SHARE_DELETE));
        if (pinnedRoot->valid()) {
            bool unused = false;
            WalkDataTree(root, false, unused, pinnedRoot->value);
        }
    }
    BYTE Remove() {
        bool removed = false;
        try {
            VerifyNoOtherLogonSession();
            if (!pinnedRoot->valid()) {
                Require(!OpenVerified(root, true, 0, true).valid());
                return 'N';
            }
            WalkDataTree(root, true, removed, pinnedRoot->value);
            pinnedRoot.reset();
            Require(!OpenVerified(root, true, 0, true).valid());
            return 'W';
        } catch (...) { return removed ? 'I' : 'B'; }
    }
};
