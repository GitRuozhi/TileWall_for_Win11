// Read-only contract shared with ExplorerMenuPreferenceStore. Never read wall/config data here.
// The main application owns writes; the Shell only reads this independently replaceable preference.
#include <shlobj.h>

static const WCHAR PreferenceSuffix[] = L"\\TileWall\\explorer-menu.state";
static const CHAR PreferenceEnabled[] = "TileWall.ExplorerMenu/1\n1\n";
// The exact disabled document is "TileWall.ExplorerMenu/1\n0\n". All other bytes also fail closed.

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

static BOOL ParsePreferenceBytes(const BYTE* bytes, DWORD count)
{
    DWORD at;
    if (count != sizeof(PreferenceEnabled) - 1) return FALSE;
    for (at = 0; at < count; ++at)
        if (bytes[at] != (BYTE)PreferenceEnabled[at]) return FALSE;
    return TRUE;
}

static BOOL ReadExplorerMenuEnabled(void)
{
    PWSTR local = NULL, path = NULL;
    WCHAR drive[4];
    SIZE_T length, at;
    DWORD driveType, read = 0;
    HANDLE file = INVALID_HANDLE_VALUE;
    BY_HANDLE_FILE_INFORMATION info;
    LARGE_INTEGER size;
    BYTE bytes[65];
    BOOL exists, enabled = FALSE;
    HRESULT hr = SHGetKnownFolderPath(&FOLDERID_LocalAppData,
        KF_FLAG_NO_PACKAGE_REDIRECTION | KF_FLAG_DONT_VERIFY, NULL, &local);
    if (FAILED(hr) || !local) goto done;
    length = (SIZE_T)lstrlenW(local);
    // Never let a relocated/remote Known Folder introduce a network operation into menu building.
    if (length < 3 || !((local[0] >= L'A' && local[0] <= L'Z') || (local[0] >= L'a' && local[0] <= L'z')) ||
        local[1] != L':' || local[2] != L'\\') goto done;
    drive[0] = local[0]; drive[1] = L':'; drive[2] = L'\\'; drive[3] = 0;
    driveType = GetDriveTypeW(drive);
    if (driveType != DRIVE_FIXED && driveType != DRIVE_REMOVABLE && driveType != DRIVE_RAMDISK) goto done;
    while (length > 2 && local[length - 1] == L'\\') --length;
    if (length + ARRAYSIZE(PreferenceSuffix) > 32768) goto done;
    path = (PWSTR)HeapAlloc(GetProcessHeap(), 0, (length + ARRAYSIZE(PreferenceSuffix)) * sizeof(WCHAR));
    if (!path) goto done;
    for (at = 0; at < length; ++at) path[at] = local[at];
    for (at = 0; at < ARRAYSIZE(PreferenceSuffix); ++at) path[length + at] = PreferenceSuffix[at];
    if (!CheckPreferencePath(path, &exists)) goto done;
    if (!exists) { enabled = TRUE; goto done; }
    file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL,
        OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_SEQUENTIAL_SCAN, NULL);
    if (file == INVALID_HANDLE_VALUE) {
        if (MissingPreferencePath(GetLastError()) && CheckPreferencePath(path, &exists) && !exists) enabled = TRUE;
        goto done;
    }
    if (GetFileType(file) != FILE_TYPE_DISK || !GetFileInformationByHandle(file, &info) ||
        (info.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)) ||
        !GetFileSizeEx(file, &size) || size.QuadPart < 0 || size.QuadPart > 64 ||
        !PreferenceHandleMatches(file, path) || !CheckPreferencePath(path, &exists) || !exists) goto done;
    // Read one extra byte so growth/oversize cannot turn a valid prefix into an accepted state.
    if (!ReadFile(file, bytes, (DWORD)sizeof(bytes), &read, NULL) ||
        !CheckPreferencePath(path, &exists) || !exists) goto done;
    enabled = ParsePreferenceBytes(bytes, read);
done:
    if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
    if (path) HeapFree(GetProcessHeap(), 0, path);
    if (local) CoTaskMemFree(local);
    return enabled;
}
