// Read-only contract shared with ExplorerMenuPreferenceStore. Never read wall/config data here.
// The main application owns writes; the Shell only reads this independently replaceable preference.
#include <shlobj.h>

static BOOL MissingPreferencePath(DWORD error)
{
    return error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND;
}

// Inspect every component at each real IO boundary, including missing-root cases. This also
// rejects a file in an ancestor position and links at the final preference path.
static BOOL CheckPreferencePath(PWSTR path, BOOL* exists)
{
    SIZE_T at;
    *exists = FALSE;
    for (at = 3; ; ++at) {
        WCHAR saved = path[at];
        DWORD attributes;
        if (saved && saved != L'\\') continue;
        path[at] = 0;
        attributes = GetFileAttributesW(path);
        path[at] = saved;
        if (attributes == INVALID_FILE_ATTRIBUTES) return MissingPreferencePath(GetLastError());
        if ((attributes & FILE_ATTRIBUTE_REPARSE_POINT) ||
            (saved ? !(attributes & FILE_ATTRIBUTE_DIRECTORY) : (attributes & FILE_ATTRIBUTE_DIRECTORY)))
            return FALSE;
        if (!saved) { *exists = TRUE; return TRUE; }
    }
}

static BOOL PreferenceHandleMatches(HANDLE file, PCWSTR expected)
{
    DWORD capacity = GetFinalPathNameByHandleW(file, NULL, 0, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    DWORD length;
    PWSTR actual;
    BOOL matches = FALSE;
    if (!capacity || capacity > 32768) return FALSE;
    actual = (PWSTR)HeapAlloc(GetProcessHeap(), 0, (SIZE_T)capacity * sizeof(WCHAR));
    if (!actual) return FALSE;
    length = GetFinalPathNameByHandleW(file, actual, capacity, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
    if (length && length < capacity) {
        // KnownFolder below is a drive-absolute DOS path; the opened physical path must agree.
        PCWSTR comparable = actual;
        if (length >= 4 && actual[0] == L'\\' && actual[1] == L'\\' && actual[2] == L'?' && actual[3] == L'\\')
            comparable += 4;
        matches = CompareStringOrdinal(comparable, -1, expected, -1, TRUE) == CSTR_EQUAL;
    }
    HeapFree(GetProcessHeap(), 0, actual);
    return matches;
}

static BOOL EqualPreferenceBytes(const BYTE* bytes, DWORD count, PCSTR expected, DWORD length)
{
    DWORD at;
    if (count != length) return FALSE;
    for (at = 0; at < count; ++at)
        if (bytes[at] != (BYTE)expected[at]) return FALSE;
    return TRUE;
}

// Returns one for a readable document, zero for a verified missing path, or -1 on failure.
static int ReadExplorerMenuDocument(PCWSTR suffix, BYTE* bytes, DWORD capacity, DWORD* read)
{
    PWSTR local = NULL, path = NULL;
    WCHAR drive[4];
    SIZE_T length, at, suffixLength = (SIZE_T)lstrlenW(suffix) + 1;
    DWORD driveType;
    HANDLE file = INVALID_HANDLE_VALUE;
    BY_HANDLE_FILE_INFORMATION info;
    LARGE_INTEGER size;
    BOOL exists;
    int status = -1;
    HRESULT hr = SHGetKnownFolderPath(&FOLDERID_LocalAppData,
        KF_FLAG_NO_PACKAGE_REDIRECTION | KF_FLAG_DONT_VERIFY, NULL, &local);
    *read = 0;
    if (FAILED(hr) || !local) goto done;
    length = (SIZE_T)lstrlenW(local);
    // Never let a relocated/remote Known Folder introduce a network operation into menu building.
    if (length < 3 || !((local[0] >= L'A' && local[0] <= L'Z') || (local[0] >= L'a' && local[0] <= L'z')) ||
        local[1] != L':' || local[2] != L'\\') goto done;
    drive[0] = local[0]; drive[1] = L':'; drive[2] = L'\\'; drive[3] = 0;
    driveType = GetDriveTypeW(drive);
    if (driveType != DRIVE_FIXED && driveType != DRIVE_REMOVABLE && driveType != DRIVE_RAMDISK) goto done;
    while (length > 2 && local[length - 1] == L'\\') --length;
    if (length + suffixLength > 32768) goto done;
    path = (PWSTR)HeapAlloc(GetProcessHeap(), 0, (length + suffixLength) * sizeof(WCHAR));
    if (!path) goto done;
    for (at = 0; at < length; ++at) path[at] = local[at];
    for (at = 0; at < suffixLength; ++at) path[length + at] = suffix[at];
    if (!CheckPreferencePath(path, &exists)) goto done;
    if (!exists) { status = 0; goto done; }
    file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL,
        OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_SEQUENTIAL_SCAN, NULL);
    if (file == INVALID_HANDLE_VALUE) {
        if (MissingPreferencePath(GetLastError()) && CheckPreferencePath(path, &exists) && !exists) status = 0;
        goto done;
    }
    if (GetFileType(file) != FILE_TYPE_DISK || !GetFileInformationByHandle(file, &info) ||
        (info.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)) ||
        !GetFileSizeEx(file, &size) || size.QuadPart < 0 || size.QuadPart >= capacity ||
        !PreferenceHandleMatches(file, path) || !CheckPreferencePath(path, &exists) || !exists) goto done;
    // Read one extra byte so growth/oversize cannot turn a valid prefix into an accepted state.
    if (!ReadFile(file, bytes, capacity, read, NULL) || *read >= capacity ||
        !CheckPreferencePath(path, &exists) || !exists) goto done;
    status = 1;
done:
    if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
    if (path) HeapFree(GetProcessHeap(), 0, path);
    if (local) CoTaskMemFree(local);
    return status;
}

static BOOL ReadExplorerMenuEnabled(void)
{
    static const CHAR enabled[] = "TileWall.ExplorerMenu/1\n1\n";
    BYTE bytes[65];
    DWORD read;
    int status = ReadExplorerMenuDocument(L"\\TileWall\\explorer-menu.state", bytes, ARRAYSIZE(bytes), &read);
    return status == 0 || (status == 1 && EqualPreferenceBytes(bytes, read, enabled, sizeof(enabled) - 1));
}

static LANGID ReadExplorerMenuLanguage(void)
{
    static const CHAR header[] = "TileWall.ExplorerMenuLanguage/1\n";
    static const struct {
        CHAR Tag[7];
        LANGID Language;
    } languages[] = {
        {"en-US\n", MAKELANGID(LANG_ENGLISH, SUBLANG_ENGLISH_US)},
        {"zh-CN\n", MAKELANGID(LANG_CHINESE, SUBLANG_CHINESE_SIMPLIFIED)},
        {"zh-TW\n", MAKELANGID(LANG_CHINESE, SUBLANG_CHINESE_TRADITIONAL)},
        {"ja-JP\n", MAKELANGID(LANG_JAPANESE, SUBLANG_NEUTRAL)},
        {"ko-KR\n", MAKELANGID(LANG_KOREAN, SUBLANG_NEUTRAL)},
        {"de-DE\n", MAKELANGID(LANG_GERMAN, SUBLANG_NEUTRAL)},
        {"fr-FR\n", MAKELANGID(LANG_FRENCH, SUBLANG_NEUTRAL)},
        {"es-ES\n", MAKELANGID(LANG_SPANISH, SUBLANG_NEUTRAL)}
    };
    const DWORD headerLength = sizeof(header) - 1;
    BYTE bytes[65];
    DWORD read;
    UINT at;
    if (ReadExplorerMenuDocument(L"\\TileWall\\explorer-menu.language", bytes, ARRAYSIZE(bytes), &read) != 1 ||
        read != headerLength + sizeof(languages[0].Tag) - 1 ||
        !EqualPreferenceBytes(bytes, headerLength, header, headerLength)) return 0;
    for (at = 0; at < ARRAYSIZE(languages); ++at)
        if (EqualPreferenceBytes(bytes + headerLength, read - headerLength,
            languages[at].Tag, sizeof(languages[at].Tag) - 1)) return languages[at].Language;
    return 0;
}
