static constexpr wchar_t PortableLauncherName[] = L"TileWall.PortableUninstall.exe";
static constexpr wchar_t PortableManifestName[] = L"TileWall.portable-files.txt";
static constexpr DWORD MaximumManifestBytes = 2 * 1024 * 1024;

struct PortableText {
    const wchar_t* Title;
    const wchar_t* DataOption;
    const wchar_t* RegistryOption;
    const wchar_t* DataWarning;
    const wchar_t* RegistryExplanation;
    const wchar_t* Continue;
    const wchar_t* StillRunning;
    const wchar_t* CleanupFailed;
    const wchar_t* DataRemoved;
    const wchar_t* DataAbsent;
    const wchar_t* DataBlocked;
    const wchar_t* DataIncomplete;
    const wchar_t* RegistryRollbackIncomplete;
    const wchar_t* PayloadUnsafe;
    const wchar_t* PayloadIncomplete;
    const wchar_t* PayloadRemoved;
    const wchar_t* Cancel;
};

#include "PortableUninstallBuild.h"

static const PortableText& ReadPortableText() {
    auto language = GetUserDefaultUILanguage();
    switch (PRIMARYLANGID(language)) {
    case LANG_CHINESE:
        return PortableMessages[language == 0x0404 || language == 0x0C04 || language == 0x1404 ? 2 : 1];
    case LANG_JAPANESE: return PortableMessages[3];
    case LANG_KOREAN: return PortableMessages[4];
    case LANG_GERMAN: return PortableMessages[5];
    case LANG_FRENCH: return PortableMessages[6];
    case LANG_SPANISH: return PortableMessages[7];
    default: return PortableMessages[0];
    }
}

struct PortableDialog {
    const PortableText& Text;
    const wstring& Directory;
    CleanupOptions Options{};
};

static bool DialogControl(HWND dialog, const wchar_t* type, const wchar_t* caption,
    DWORD style, int id, RECT bounds) {
    if (!MapDialogRect(dialog, &bounds)) return false;
    auto control = CreateWindowExW(0, type, caption, WS_CHILD | WS_VISIBLE | style, bounds.left, bounds.top,
        bounds.right - bounds.left, bounds.bottom - bounds.top, dialog, reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)),
        GetModuleHandleW(nullptr), nullptr);
    if (!control) return false;
    SendMessageW(control, WM_SETFONT, SendMessageW(dialog, WM_GETFONT, 0, 0), FALSE);
    return true;
}

static INT_PTR CALLBACK PortableOptionsDialog(HWND dialog, UINT message, WPARAM wParam, LPARAM lParam) {
    constexpr int Data = 100, Registry = 101;
    if (message == WM_INITDIALOG) {
        auto& state = *reinterpret_cast<PortableDialog*>(lParam);
        const auto& ui = state.Text;
        SetWindowLongPtrW(dialog, DWLP_USER, lParam);
        SetWindowTextW(dialog, ui.Title);
        if (!DialogControl(dialog, L"Static", state.Directory.c_str(), SS_PATHELLIPSIS, -1, {12, 11, 328, 25}) ||
            !DialogControl(dialog, L"Button", ui.DataOption, WS_TABSTOP | BS_AUTOCHECKBOX, Data, {12, 29, 328, 44}) ||
            !DialogControl(dialog, L"Static", ui.DataWarning, SS_LEFT, -1, {24, 47, 328, 79}) ||
            !DialogControl(dialog, L"Button", ui.RegistryOption, WS_TABSTOP | BS_AUTOCHECKBOX, Registry, {12, 82, 328, 97}) ||
            !DialogControl(dialog, L"Static", ui.RegistryExplanation, SS_LEFT, -1, {24, 100, 328, 132}) ||
            !DialogControl(dialog, L"Button", ui.Continue, WS_TABSTOP | BS_PUSHBUTTON, IDOK, {192, 133, 256, 150}) ||
            !DialogControl(dialog, L"Button", ui.Cancel, WS_TABSTOP | BS_DEFPUSHBUTTON, IDCANCEL, {264, 133, 328, 150})) {
            EndDialog(dialog, -1);
            return TRUE;
        }
        CheckDlgButton(dialog, Registry, BST_CHECKED);
        SendMessageW(dialog, DM_SETDEFID, IDCANCEL, 0);
        SetFocus(GetDlgItem(dialog, IDCANCEL));
        return FALSE;
    }
    if (message == WM_COMMAND && LOWORD(wParam) == IDOK) {
        auto& state = *reinterpret_cast<PortableDialog*>(GetWindowLongPtrW(dialog, DWLP_USER));
        state.Options.Data = IsDlgButtonChecked(dialog, Data) == BST_CHECKED;
        state.Options.Registry = IsDlgButtonChecked(dialog, Registry) == BST_CHECKED;
        EndDialog(dialog, IDOK);
        return TRUE;
    }
    if (message == WM_CLOSE || (message == WM_COMMAND && LOWORD(wParam) == IDCANCEL)) {
        EndDialog(dialog, IDCANCEL);
        return TRUE;
    }
    return FALSE;
}

static bool ConfirmPortableOptions(PortableDialog& state) {
#pragma pack(push, 2)
    struct DialogTemplate {
        DLGTEMPLATE templateData;
        WORD menu = 0, windowClass = 0, title = 0, fontSize = 9;
        wchar_t font[9] = L"Segoe UI";
    };
#pragma pack(pop)
    alignas(DWORD) DialogTemplate form{{DS_MODALFRAME | DS_CENTER | DS_SETFONT |
        WS_POPUP | WS_CAPTION | WS_SYSMENU, 0, 0, 0, 0, 340, 161}};
    auto result = DialogBoxIndirectParamW(GetModuleHandleW(nullptr), &form.templateData, nullptr,
        PortableOptionsDialog, reinterpret_cast<LPARAM>(&state));
    Require(result == IDOK || result == IDCANCEL);
    return result == IDOK;
}

static wstring DataResult(const PortableText& text, BYTE status) {
    switch (status) {
    case 'W': return wstring(L"\n\n") + text.DataRemoved;
    case 'N': return wstring(L"\n\n") + text.DataAbsent;
    case 'I': return wstring(L"\n\n") + text.DataIncomplete;
    case 'B': return wstring(L"\n\n") + text.DataBlocked;
    default: return L"";
    }
}

static void PortableMessage(const PortableText& text, const wstring& message, UINT icon) {
    MessageBoxW(nullptr, message.c_str(), text.Title, MB_OK | icon | MB_SETFOREGROUND);
}

static std::string ReadManifest(HANDLE file) {
    LARGE_INTEGER size{};
    Require(GetFileSizeEx(file, &size) && size.QuadPart > 0 && size.QuadPart <= MaximumManifestBytes);
    std::string content(static_cast<size_t>(size.QuadPart), '\0');
    DWORD count = 0;
    Require(ReadFile(file, content.data(), static_cast<DWORD>(content.size()), &count, nullptr) && count == content.size());
    Require(Hex(Sha256Bytes(content.data(), count)) == PortableManifestDigest);
    return content;
}

struct PortableFile { wstring Path; wstring Digest; LONGLONG Size; };

static std::vector<PortableFile> PortableFiles(const std::string& manifest) {
    constexpr std::string_view header = "TileWall Portable Files 1\n";
    Require(manifest.starts_with(header) && manifest.back() == '\n');
    std::vector<PortableFile> files;
    auto compare = [](const wstring& left, const wstring& right) {
        return CompareStringOrdinal(left.c_str(), -1, right.c_str(), -1, TRUE) == CSTR_LESS_THAN;
    };
    std::set<wstring, decltype(compare)> names(compare);
    for (auto start = header.size(); start < manifest.size();) {
        auto end = manifest.find('\n', start);
        auto digestEnd = manifest.find('\t', start);
        Require(digestEnd == start + 64 && digestEnd < end);
        auto sizeEnd = manifest.find('\t', digestEnd + 1);
        Require(sizeEnd > digestEnd + 1 && sizeEnd < end && sizeEnd - digestEnd <= 20);
        auto digest = manifest.substr(start, 64);
        Require(digest.find_first_not_of("0123456789ABCDEF") == std::string::npos);
        auto size = manifest.substr(digestEnd + 1, sizeEnd - digestEnd - 1);
        Require(size.find_first_not_of("0123456789") == std::string::npos && (size == "0" || size[0] != '0'));
        auto byteCount = static_cast<int>(end - sizeEnd - 1);
        Require(byteCount > 0);
        auto bytes = manifest.data() + sizeEnd + 1;
        int characters = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, bytes, byteCount, nullptr, 0);
        Require(characters > 0);
        wstring path(static_cast<size_t>(characters), L'\0');
        Require(MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, bytes, byteCount, path.data(), characters) == characters);
        Require(ValidRelativePath(path) && !SamePath(path, PortableManifestName) && !SamePath(path, PortableLauncherName) &&
            names.insert(path).second);
        files.push_back({std::move(path), wstring(digest.begin(), digest.end()), std::stoll(size)});
        start = end + 1;
    }
    Require(names.contains(L"TileWall.exe"));
    return files;
}

static wstring HashFile(HANDLE file, LONGLONG length) {
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    Require(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0);
    struct HashCleanup {
        BCRYPT_ALG_HANDLE Algorithm;
        BCRYPT_HASH_HANDLE& Hash;
        ~HashCleanup() { if (Hash) BCryptDestroyHash(Hash); BCryptCloseAlgorithmProvider(Algorithm, 0); }
    } cleanup{algorithm, hash};
    Require(BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) >= 0);
    BYTE buffer[65536];
    DWORD count = 0;
    LONGLONG total = 0;
    do {
        Require(ReadFile(file, buffer, sizeof(buffer), &count, nullptr));
        total += count;
        Require(total <= length);
        if (count) Require(BCryptHashData(hash, buffer, count, 0) >= 0);
    } while (count);
    std::vector<BYTE> digest(32);
    Require(total == length && BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0) >= 0);
    return Hex(digest);
}

static void VerifyPayload(HANDLE file, const PortableFile& expected) {
    LARGE_INTEGER length{};
    Require(GetFileSizeEx(file, &length) && length.QuadPart == expected.Size && HashFile(file, length.QuadPart) == expected.Digest);
}

static void RemoveVerifiedPayload(const Installation& install, const PortableFile& expected) {
    auto path = install.Directory + L'\\' + expected.Path;
    std::vector<Handle> ancestors;
    {
        auto file = OpenWithAncestors(path, false, GENERIC_READ | DELETE, ancestors, true);
        if (!file.valid()) return;
        VerifyPayload(file.value, expected);
        MarkForDeletion(file.value);
    }
    Require(!OpenVerified(path, false, 0, true).valid());
}

static wstring PortableTempDirectory() {
    std::vector<wchar_t> text(32768);
    auto count = GetTempPathW(static_cast<DWORD>(text.size()), text.data());
    Require(count > 3 && count < text.size() && text[count - 1] == L'\\');
    wstring directory(text.data(), count - 1);
    Require(IsDrivePath(directory));
    return directory;
}

static wstring PortableClone(const wstring& nonce) {
    return PortableTempDirectory() + L"\\TileWall.PortableUninstall-" + nonce + L".tmp";
}

static wstring PortablePipe(const wstring& nonce) { return L"\\\\.\\pipe\\TileWall.PortableUninstall." + nonce; }

static wstring NewPortableNonce() {
    GUID id{};
    Com(CoCreateGuid(&id));
    auto data = reinterpret_cast<const BYTE*>(&id);
    auto nonce = Hex(std::vector<BYTE>(data, data + sizeof(id)));
    for (auto& character : nonce) if (character >= L'A' && character <= L'F') character += L'a' - L'A';
    return nonce;
}

static void LaunchPortableFinalizer(const wstring& self, BYTE dataStatus) {
    auto nonce = NewPortableNonce();
    auto clone = PortableClone(nonce);
    std::vector<Handle> selfAncestors, tempAncestors;
    auto temporaryDirectory = OpenWithAncestors(Parent(clone), true, 0, tempAncestors);
    Require(temporaryDirectory.valid());
    {
        auto source = OpenWithAncestors(self, false, GENERIC_READ, selfAncestors);
        Require(CopyFileW(Extended(self).c_str(), Extended(clone).c_str(), TRUE));
    }
    auto image = OpenVerified(clone, false, GENERIC_READ);
    Require(Digest(self) == Digest(clone));
    auto sddl = L"D:P(A;;GA;;;" + Sid(GetCurrentProcess()) + L")";
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    Require(ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &descriptor, nullptr));
    std::unique_ptr<void, decltype(&LocalFree)> freeDescriptor(descriptor, LocalFree);
    SECURITY_ATTRIBUTES security{sizeof(security), descriptor, FALSE};
    Handle pipe(CreateNamedPipeW(PortablePipe(nonce).c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE | FILE_FLAG_OVERLAPPED,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 1024, 1024, 0, &security));
    Require(pipe.valid());
    Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    Require(event.valid());
    auto command = L"\"" + clone + L"\" --portable-finish " + nonce + L" " + std::to_wstring(GetCurrentProcessId());
    STARTUPINFOW startup{}; startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    OVERLAPPED connection{}; connection.hEvent = event.value;
    BOOL connected = ConnectNamedPipe(pipe.value, &connection);
    auto error = connected ? ERROR_SUCCESS : GetLastError();
    bool pending = error == ERROR_IO_PENDING;
    try {
        Require(pending || error == ERROR_SUCCESS || error == ERROR_PIPE_CONNECTED);
        Require(CreateProcessW(clone.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr,
            Parent(clone).c_str(), &startup, &process));
        Handle child(process.hProcess), thread(process.hThread);
        if (pending) {
            HANDLE waits[]{event.value, child.value};
            Require(WaitForMultipleObjects(2, waits, FALSE, 10000) == WAIT_OBJECT_0);
            DWORD ignored = 0;
            Require(GetOverlappedResult(pipe.value, &connection, &ignored, FALSE));
            pending = false;
        }
        ULONG client = 0;
        Require(GetNamedPipeClientProcessId(pipe.value, &client) && client == process.dwProcessId);
        WriteByte(pipe.value, dataStatus);
        BYTE ready = 0;
        Require(ReadByte(pipe.value, child.value, ready, 10000) && ready == 'A');
    } catch (...) {
        if (pending) {
            CancelIoEx(pipe.value, &connection);
            DWORD ignored = 0;
            GetOverlappedResult(pipe.value, &connection, &ignored, TRUE);
        }
        throw;
    }
}

static int FinishPortableUninstall(const wstring& nonce, DWORD parentPid) {
    const auto& text = ReadPortableText();
    BYTE dataStatus = 0;
    bool armed = false;
    try {
        OrdinaryDesktopUser();
        auto self = Image(GetCurrentProcess());
        Require(SamePath(self, PortableClone(nonce)));
        Handle parent(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, parentPid));
        Require(parent.valid() && Session(parentPid) == Session(GetCurrentProcessId()) &&
            Sid(parent.value) == Sid(GetCurrentProcess()) && !Elevated(parent.value));
        auto original = Image(parent.value);
        auto directory = Parent(original);
        auto manifest = directory + L'\\' + PortableManifestName;
        Require(SamePath(original, directory + L'\\' + PortableLauncherName));
        auto digest = Hex(Digest(self));
        Handle pipe(CreateFileW(PortablePipe(nonce).c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr));
        Require(pipe.valid());
        ULONG server = 0;
        Require(GetNamedPipeServerProcessId(pipe.value, &server) && server == parentPid);
        Require(ReadByte(pipe.value, parent.value, dataStatus, 10000) &&
            (dataStatus == 'O' || dataStatus == 'W' || dataStatus == 'N'));
        std::vector<Handle> originalAncestors, manifestAncestors;
        {
            auto launcher = OpenWithAncestors(original, false, GENERIC_READ | DELETE, originalAncestors);
            LARGE_INTEGER length{};
            Require(GetFileSizeEx(launcher.value, &length) && length.QuadPart > 0 &&
                length.QuadPart <= 128LL * 1024 * 1024 && HashFile(launcher.value, length.QuadPart) == digest);
            auto fileList = OpenWithAncestors(manifest, false, GENERIC_READ | DELETE, manifestAncestors);
            ReadManifest(fileList.value);
            WriteByte(pipe.value, 'A');
            armed = true;
            Require(WaitForSingleObject(parent.value, 10000) == WAIT_OBJECT_0);
            MarkForDeletion(launcher.value);
            MarkForDeletion(fileList.value);
        }
        Require(!OpenVerified(original, false, 0, true).valid() && !OpenVerified(manifest, false, 0, true).valid());
        PortableMessage(text, wstring(text.PayloadRemoved) + DataResult(text, dataStatus), MB_ICONINFORMATION);
        return 0;
    } catch (...) {
        if (armed) PortableMessage(text, wstring(text.PayloadIncomplete) + L"\n" + PortableLauncherName +
            L"\n" + PortableManifestName + DataResult(text, dataStatus), MB_ICONERROR);
        return Failed;
    }
}

static int RunPortableUninstall() {
    const auto& text = ReadPortableText();
    BYTE dataStatus = 0;
    try {
        OrdinaryDesktopUser();
        Installation install;
        install.Self = Image(GetCurrentProcess());
        install.Directory = Parent(install.Self);
        install.Executable = install.Directory + L"\\TileWall.exe";
        Require(SamePath(install.Self, install.Directory + L'\\' + PortableLauncherName));
        std::vector<Handle> ancestors;
        auto directory = OpenWithAncestors(install.Directory, true, 0, ancestors);
        Require(!OpenVerified(install.Directory + L"\\TileWall.install-files.txt", false, 0, true).valid());
        auto manifestPath = install.Directory + L'\\' + PortableManifestName;
        auto manifest = OpenVerified(manifestPath, false, GENERIC_READ, false, FILE_SHARE_READ | FILE_SHARE_DELETE);
        auto files = PortableFiles(ReadManifest(manifest.value));
        PortableDialog dialog{text, install.Directory};
        if (!ConfirmPortableOptions(dialog)) return 21;
        if (!dialog.Options.Data) {
            try { RequireProgramStopped(install.Executable); }
            catch (...) { PortableMessage(text, text.StillRunning, MB_ICONERROR); return Failed; }
        }
        auto cleaned = CleanupSelected(install, dialog.Options, dataStatus, [](BYTE) {});
        if (cleaned != 0) {
            auto message = wstring(text.CleanupFailed) + DataResult(text, dataStatus);
            if (cleaned == RollbackFailed) message += wstring(L"\n\n") + text.RegistryRollbackIncomplete;
            PortableMessage(text, message, MB_ICONERROR);
            return cleaned;
        }
        wstring remaining;
        unsigned count = 0;
        for (const auto& file : files) {
            try { RemoveVerifiedPayload(install, file); }
            catch (...) { if (++count <= 10) remaining += L'\n' + file.Path; }
        }
        if (count) {
            PortableMessage(text, wstring(text.PayloadIncomplete) + remaining + DataResult(text, dataStatus), MB_ICONERROR);
            return Failed;
        }
        LaunchPortableFinalizer(install.Self, dataStatus);
        return Accepted;
    } catch (...) {
        PortableMessage(text, (dataStatus ? wstring(text.PayloadIncomplete) : wstring(text.PayloadUnsafe)) +
            DataResult(text, dataStatus), MB_ICONERROR);
        return Failed;
    }
}
