// TileWall's own implementation of the Windows SDK COM contracts. No sample code is copied.
// A static CRT supplies the standard DLL startup and security-cookie initialization.
// This DLL has no .NET, WinUI, registry writes, or wall-state dependency.
#define WIN32_LEAN_AND_MEAN
#define COBJMACROS
#define CONST_VTABLE
#include <windows.h>
#include <shellapi.h>
#include <shobjidl.h>
#include "Resource.h"
#include "ExplorerMenuPreference.h"

#if !defined(_M_X64)
#error This package ships an x64 Explorer extension; build with an x64 Windows target.
#endif

static const CLSID CommandClsid = {0xcb391e98, 0x9cf2, 0x4a54, {0xab, 0xea, 0xfb, 0xc5, 0x29, 0xca, 0x2d, 0x44}};
static const GUID CanonicalName = {0x75034654, 0x9467, 0x4a8e, {0xaf, 0x75, 0x84, 0xee, 0x63, 0xae, 0x3f, 0x8d}};
static const WCHAR AppUserModelId[] = L"Ruozhi.TileWall_k0yb5fxbm3zce!App";
static const WCHAR CanonicalVerb[] = L"AddToTileWall";
static HMODULE Module;
static LONG ModuleReferences;
static WCHAR IconLocation[32768];

typedef struct Command {
    IExplorerCommand Interface;
    IShellExtInit Initialization;
    IContextMenu ClassicMenu;
    IShellItemArray* Selection;
    LONG References;
} Command;

typedef struct Factory {
    IClassFactory Interface;
    LONG References;
} Factory;

static BOOL SameGuid(REFGUID left, REFGUID right)
{
    UINT i;
    if (left->Data1 != right->Data1 || left->Data2 != right->Data2 || left->Data3 != right->Data3)
        return FALSE;
    for (i = 0; i < 8; ++i)
        if (left->Data4[i] != right->Data4[i]) return FALSE;
    return TRUE;
}

static HRESULT CopyString(PCWSTR value, PWSTR* result)
{
    SIZE_T i, length;
    if (!result) return E_POINTER;
    *result = NULL;
    length = (SIZE_T)lstrlenW(value);
    *result = (PWSTR)CoTaskMemAlloc((length + 1) * sizeof(WCHAR));
    if (!*result) return E_OUTOFMEMORY;
    for (i = 0; i <= length; ++i) (*result)[i] = value[i];
    return S_OK;
}

static ULONG STDMETHODCALLTYPE CommandAddRef(IExplorerCommand* self)
{
    return (ULONG)InterlockedIncrement(&((Command*)self)->References);
}

static ULONG STDMETHODCALLTYPE CommandRelease(IExplorerCommand* self)
{
    LONG count = InterlockedDecrement(&((Command*)self)->References);
    if (!count) {
        if (((Command*)self)->Selection) IShellItemArray_Release(((Command*)self)->Selection);
        HeapFree(GetProcessHeap(), 0, self);
        InterlockedDecrement(&ModuleReferences);
    }
    return (ULONG)count;
}

static HRESULT STDMETHODCALLTYPE CommandQueryInterface(IExplorerCommand* self, REFIID iid, void** result)
{
    if (!result) return E_POINTER;
    *result = NULL;
    if (SameGuid(iid, &IID_IUnknown) || SameGuid(iid, &IID_IExplorerCommand)) *result = self;
    else if (SameGuid(iid, &IID_IShellExtInit)) *result = &((Command*)self)->Initialization;
    else if (SameGuid(iid, &IID_IContextMenu)) *result = &((Command*)self)->ClassicMenu;
    else return E_NOINTERFACE;
    CommandAddRef(self);
    return S_OK;
}

static HRESULT STDMETHODCALLTYPE GetTitle(IExplorerCommand* self, IShellItemArray* items, PWSTR* title)
{
    WCHAR text[64];
    UNREFERENCED_PARAMETER(self);
    UNREFERENCED_PARAMETER(items);
    // LoadString reads this module's mapped resource, using the user's Windows UI language.
    if (!LoadStringW(Module, IDS_MENU_TITLE, text, (int)ARRAYSIZE(text))) return CopyString(L"Add to TileWall", title);
    return CopyString(text, title);
}

static HRESULT STDMETHODCALLTYPE GetIcon(IExplorerCommand* self, IShellItemArray* items, PWSTR* icon)
{
    UNREFERENCED_PARAMETER(self);
    UNREFERENCED_PARAMETER(items);
    if (!icon) return E_POINTER;
    *icon = NULL;
    return IconLocation[0] ? CopyString(IconLocation, icon) : E_NOTIMPL;
}

static HRESULT STDMETHODCALLTYPE GetToolTip(IExplorerCommand* self, IShellItemArray* items, PWSTR* text)
{
    UNREFERENCED_PARAMETER(self);
    UNREFERENCED_PARAMETER(items);
    if (!text) return E_POINTER;
    *text = NULL;
    return E_NOTIMPL;
}

static HRESULT STDMETHODCALLTYPE GetCanonicalName(IExplorerCommand* self, GUID* name)
{
    UNREFERENCED_PARAMETER(self);
    if (!name) return E_POINTER;
    *name = CanonicalName;
    return S_OK;
}

static HRESULT STDMETHODCALLTYPE GetState(IExplorerCommand* self, IShellItemArray* items, BOOL mayBeSlow, EXPCMDSTATE* state)
{
    DWORD count;
    UNREFERENCED_PARAMETER(self);
    UNREFERENCED_PARAMETER(mayBeSlow);
    if (!state) return E_POINTER;
    *state = ECS_HIDDEN;
    if (!items || FAILED(IShellItemArray_GetCount(items, &count)) || count != 1) return S_OK;
    // Win10's packaged classic menu can retain the pending state without applying a later
    // slow query. Read only the bounded local preference so each opening gets a final state.
    if (ReadExplorerMenuEnabled()) *state = ECS_ENABLED;
    return S_OK;
}

static BOOL IsSeparator(WCHAR value) { return value == L'\\' || value == L'/'; }
static BOOL IsDriveLetter(WCHAR value) { return (value >= L'A' && value <= L'Z') || (value >= L'a' && value <= L'z'); }

static BOOL IsRoutablePath(PCWSTR path)
{
    PCWSTR drive = path;
    SIZE_T length = (SIZE_T)lstrlenW(path), offset;
    if (length >= 4 && path[0] == L'\\' && path[1] == L'\\' && path[2] == L'?' && path[3] == L'\\')
        drive += 4;
    // Shell supplies FILESYSPATH; the application still performs the authoritative path checks.
    if (IsDriveLetter(drive[0]) && drive[1] == L':' && IsSeparator(drive[2])) {
        offset = 3;
        while (IsSeparator(drive[offset])) ++offset;
        return drive[offset] != 0; // Discard local drive roots, including extended-length roots.
    }
    return length > 2 && path[0] == L'\\' && path[1] == L'\\';
}

// Same Windows argv escaping contract and 32,000-character payload limit as ExplorerAddCommandLine.
// No selected path is ever interpreted as an executable, verb, or command-line fragment.
static PWSTR BuildArguments(PCWSTR path)
{
    const SIZE_T limit = 32000;
    SIZE_T length = (SIZE_T)lstrlenW(path), needed = 8, i, slashes = 0, at = 0;
    PWSTR result;
    for (i = 0; i < length; ++i) {
        if (path[i] == L'\\') { ++slashes; continue; }
        needed += (path[i] == L'"' ? slashes * 2 + 2 : slashes + 1);
        slashes = 0;
    }
    needed += slashes * 2;
    if (needed > limit) return NULL;
    result = (PWSTR)HeapAlloc(GetProcessHeap(), 0, (needed + 1) * sizeof(WCHAR));
    if (!result) return NULL;
    result[at++] = L'-'; result[at++] = L'-'; result[at++] = L'a'; result[at++] = L'd';
    result[at++] = L'd'; result[at++] = L' '; result[at++] = L'"';
    slashes = 0;
    for (i = 0; i < length; ++i) {
        SIZE_T escaping;
        if (path[i] == L'\\') { ++slashes; continue; }
        escaping = path[i] == L'"' ? slashes * 2 + 1 : slashes;
        while (escaping--) result[at++] = L'\\';
        result[at++] = path[i];
        slashes = 0;
    }
    slashes *= 2;
    while (slashes--) result[at++] = L'\\';
    result[at++] = L'"';
    result[at] = 0;
    return result;
}

static HRESULT InvokeSelection(IShellItemArray* items)
{
    DWORD count, processId;
    IShellItem* item = NULL;
    PWSTR path = NULL, arguments = NULL;
    IApplicationActivationManager* activator = NULL;
    HRESULT hr;
    // Recheck at invocation, independently of menu construction and any retained selection.
    if (!ReadExplorerMenuEnabled()) return S_OK;
    if (!items || FAILED(IShellItemArray_GetCount(items, &count)) || count != 1) return S_OK;
    hr = IShellItemArray_GetItemAt(items, 0, &item);
    if (SUCCEEDED(hr)) hr = IShellItem_GetDisplayName(item, SIGDN_FILESYSPATH, &path);
    if (SUCCEEDED(hr) && path && IsRoutablePath(path)) {
        arguments = BuildArguments(path);
        if (!arguments) hr = HRESULT_FROM_WIN32(ERROR_FILENAME_EXCED_RANGE);
        else {
            // System activation retains package identity. A running TileWall receives the same
            // --add intent through its normal startup/single-instance path; this DLL never sends IPC.
            // An independent COM host keeps activation arguments alive if this menu surrogate exits.
            hr = CoCreateInstance(&CLSID_ApplicationActivationManager, NULL, CLSCTX_LOCAL_SERVER,
                &IID_IApplicationActivationManager, (void**)&activator);
            if (SUCCEEDED(hr)) hr = IApplicationActivationManager_ActivateApplication(activator,
                AppUserModelId, arguments, AO_NOERRORUI, &processId);
        }
    }
    if (activator) IApplicationActivationManager_Release(activator);
    if (arguments) HeapFree(GetProcessHeap(), 0, arguments);
    if (path) CoTaskMemFree(path);
    if (item) IShellItem_Release(item);
    if (FAILED(hr)) OutputDebugStringW(L"TileWall Explorer command: selection or package activation failed.\n");
    // Do not show Explorer's generic error dialog or fall back to launching an un-packaged EXE.
    return S_OK;
}

static HRESULT STDMETHODCALLTYPE Invoke(IExplorerCommand* self, IShellItemArray* items, IBindCtx* context)
{
    UNREFERENCED_PARAMETER(self);
    UNREFERENCED_PARAMETER(context);
    return InvokeSelection(items);
}

static Command* FromInitialization(IShellExtInit* self) { return CONTAINING_RECORD(self, Command, Initialization); }
static Command* FromClassic(IContextMenu* self) { return CONTAINING_RECORD(self, Command, ClassicMenu); }

static HRESULT STDMETHODCALLTYPE InitQueryInterface(IShellExtInit* self, REFIID iid, void** result)
{ return CommandQueryInterface(&FromInitialization(self)->Interface, iid, result); }
static ULONG STDMETHODCALLTYPE InitAddRef(IShellExtInit* self)
{ return CommandAddRef(&FromInitialization(self)->Interface); }
static ULONG STDMETHODCALLTYPE InitRelease(IShellExtInit* self)
{ return CommandRelease(&FromInitialization(self)->Interface); }

static HRESULT STDMETHODCALLTYPE Initialize(IShellExtInit* self, PCIDLIST_ABSOLUTE folder, IDataObject* data, HKEY programId)
{
    Command* command = FromInitialization(self);
    IShellItemArray* selection = NULL;
    DWORD count = 0;
    HRESULT hr;
    UNREFERENCED_PARAMETER(folder);
    UNREFERENCED_PARAMETER(programId);
    // Reinitialization failures must not leave a prior object's selection invokable.
    if (command->Selection) { IShellItemArray_Release(command->Selection); command->Selection = NULL; }
    if (!data) return E_INVALIDARG;
    hr = SHCreateShellItemArrayFromDataObject(data, &IID_IShellItemArray, (void**)&selection);
    if (SUCCEEDED(hr)) hr = IShellItemArray_GetCount(selection, &count);
    if (SUCCEEDED(hr) && count != 1) hr = E_INVALIDARG;
    if (SUCCEEDED(hr)) command->Selection = selection;
    else if (selection) IShellItemArray_Release(selection);
    return hr;
}

static HRESULT STDMETHODCALLTYPE ClassicQueryInterface(IContextMenu* self, REFIID iid, void** result)
{ return CommandQueryInterface(&FromClassic(self)->Interface, iid, result); }
static ULONG STDMETHODCALLTYPE ClassicAddRef(IContextMenu* self)
{ return CommandAddRef(&FromClassic(self)->Interface); }
static ULONG STDMETHODCALLTYPE ClassicRelease(IContextMenu* self)
{ return CommandRelease(&FromClassic(self)->Interface); }

static HRESULT STDMETHODCALLTYPE QueryContextMenu(IContextMenu* self, HMENU menu, UINT position,
    UINT firstId, UINT lastId, UINT flags)
{
    Command* command = FromClassic(self);
    WCHAR title[64];
    MENUITEMINFOW item;
    DWORD count;
    if ((flags & CMF_DEFAULTONLY) || firstId > lastId || !command->Selection ||
        FAILED(IShellItemArray_GetCount(command->Selection, &count)) || count != 1 || !ReadExplorerMenuEnabled())
        return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, 0);
    if (!LoadStringW(Module, IDS_MENU_TITLE, title, (int)ARRAYSIZE(title))) lstrcpyW(title, L"Add to TileWall");
    item.cbSize = (UINT)sizeof(item);
    item.fMask = MIIM_ID | MIIM_STRING | MIIM_STATE;
    item.wID = firstId;
    item.fState = MFS_ENABLED;
    item.dwTypeData = title;
    if (!InsertMenuItemW(menu, position, TRUE, &item)) return HRESULT_FROM_WIN32(GetLastError());
    // No host-call ordering, OS-version, title scanning, or undocumented deduplication heuristics.
    return MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, 1);
}

static BOOL IsCanonicalVerbA(PCSTR value)
{
    static const CHAR verb[] = "AddToTileWall";
    SIZE_T at;
    if (!value) return FALSE;
    for (at = 0; at < sizeof(verb); ++at) {
        CHAR actual = value[at], expected = verb[at];
        if (actual >= 'A' && actual <= 'Z') actual = (CHAR)(actual + ('a' - 'A'));
        if (expected >= 'A' && expected <= 'Z') expected = (CHAR)(expected + ('a' - 'A'));
        if (actual != expected) return FALSE;
    }
    return TRUE;
}

static HRESULT STDMETHODCALLTYPE InvokeCommand(IContextMenu* self, CMINVOKECOMMANDINFO* info)
{
    BOOL accepted = FALSE;
    if (!info || info->cbSize < sizeof(CMINVOKECOMMANDINFO)) return E_INVALIDARG;
    if (info->cbSize >= sizeof(CMINVOKECOMMANDINFOEX) && (info->fMask & CMIC_MASK_UNICODE)) {
        CMINVOKECOMMANDINFOEX* extended = (CMINVOKECOMMANDINFOEX*)info;
        accepted = IS_INTRESOURCE(extended->lpVerbW)
            ? IS_INTRESOURCE(info->lpVerb) && LOWORD((ULONG_PTR)extended->lpVerbW) == 0 && LOWORD((ULONG_PTR)info->lpVerb) == 0
            : CompareStringOrdinal(extended->lpVerbW, -1, CanonicalVerb, -1, TRUE) == CSTR_EQUAL;
    }
    else if (IS_INTRESOURCE(info->lpVerb)) accepted = LOWORD((ULONG_PTR)info->lpVerb) == 0;
    else accepted = IsCanonicalVerbA(info->lpVerb);
    if (!accepted) return E_INVALIDARG;
    return InvokeSelection(FromClassic(self)->Selection);
}

static HRESULT STDMETHODCALLTYPE GetCommandString(IContextMenu* self, UINT_PTR id, UINT flags,
    UINT* reserved, LPSTR text, UINT capacity)
{
    static const CHAR verb[] = "AddToTileWall";
    UINT at;
    UNREFERENCED_PARAMETER(self);
    UNREFERENCED_PARAMETER(reserved);
    if (id != 0) return E_INVALIDARG;
    if (flags == GCS_VALIDATEA || flags == GCS_VALIDATEW) return S_OK;
    if (flags != GCS_VERBA && flags != GCS_VERBW) return E_NOTIMPL;
    if (!text || capacity < ARRAYSIZE(verb)) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    if (flags == GCS_VERBW) {
        PWSTR wide = (PWSTR)text;
        for (at = 0; at < ARRAYSIZE(CanonicalVerb); ++at) wide[at] = CanonicalVerb[at];
    }
    else for (at = 0; at < ARRAYSIZE(verb); ++at) text[at] = verb[at];
    return S_OK;
}

static HRESULT STDMETHODCALLTYPE GetFlags(IExplorerCommand* self, EXPCMDFLAGS* flags)
{
    UNREFERENCED_PARAMETER(self);
    if (!flags) return E_POINTER;
    *flags = ECF_DEFAULT;
    return S_OK;
}

static HRESULT STDMETHODCALLTYPE EnumSubCommands(IExplorerCommand* self, IEnumExplorerCommand** commands)
{
    UNREFERENCED_PARAMETER(self);
    if (!commands) return E_POINTER;
    *commands = NULL;
    return E_NOTIMPL;
}

static const IExplorerCommandVtbl CommandVtable = {CommandQueryInterface, CommandAddRef, CommandRelease,
    GetTitle, GetIcon, GetToolTip, GetCanonicalName, GetState, Invoke, GetFlags, EnumSubCommands};
static const IShellExtInitVtbl InitializationVtable = {InitQueryInterface, InitAddRef, InitRelease, Initialize};
static const IContextMenuVtbl ClassicVtable = {ClassicQueryInterface, ClassicAddRef, ClassicRelease,
    QueryContextMenu, InvokeCommand, GetCommandString};

static ULONG STDMETHODCALLTYPE FactoryAddRef(IClassFactory* self)
{
    return (ULONG)InterlockedIncrement(&((Factory*)self)->References);
}

static ULONG STDMETHODCALLTYPE FactoryRelease(IClassFactory* self)
{
    LONG count = InterlockedDecrement(&((Factory*)self)->References);
    if (!count) {
        HeapFree(GetProcessHeap(), 0, self);
        InterlockedDecrement(&ModuleReferences);
    }
    return (ULONG)count;
}

static HRESULT STDMETHODCALLTYPE FactoryQueryInterface(IClassFactory* self, REFIID iid, void** result)
{
    if (!result) return E_POINTER;
    *result = NULL;
    if (!SameGuid(iid, &IID_IUnknown) && !SameGuid(iid, &IID_IClassFactory)) return E_NOINTERFACE;
    *result = self;
    FactoryAddRef(self);
    return S_OK;
}

static HRESULT STDMETHODCALLTYPE CreateInstance(IClassFactory* self, IUnknown* outer, REFIID iid, void** result)
{
    Command* command;
    HRESULT hr;
    UNREFERENCED_PARAMETER(self);
    if (!result) return E_POINTER;
    *result = NULL;
    if (outer) return CLASS_E_NOAGGREGATION;
    command = (Command*)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(Command));
    if (!command) return E_OUTOFMEMORY;
    command->Interface.lpVtbl = &CommandVtable;
    command->Initialization.lpVtbl = &InitializationVtable;
    command->ClassicMenu.lpVtbl = &ClassicVtable;
    command->References = 1;
    InterlockedIncrement(&ModuleReferences);
    hr = CommandQueryInterface(&command->Interface, iid, result);
    CommandRelease(&command->Interface);
    return hr;
}

static HRESULT STDMETHODCALLTYPE LockServer(IClassFactory* self, BOOL lock)
{
    UNREFERENCED_PARAMETER(self);
    if (lock) InterlockedIncrement(&ModuleReferences);
    else InterlockedDecrement(&ModuleReferences);
    return S_OK;
}

static const IClassFactoryVtbl FactoryVtable = {FactoryQueryInterface, FactoryAddRef, FactoryRelease, CreateInstance, LockServer};

HRESULT WINAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** result)
{
    Factory* factory;
    HRESULT hr;
    if (!result) return E_POINTER;
    *result = NULL;
    if (!SameGuid(clsid, &CommandClsid)) return CLASS_E_CLASSNOTAVAILABLE;
    factory = (Factory*)HeapAlloc(GetProcessHeap(), 0, sizeof(Factory));
    if (!factory) return E_OUTOFMEMORY;
    factory->Interface.lpVtbl = &FactoryVtable;
    factory->References = 1;
    InterlockedIncrement(&ModuleReferences);
    hr = FactoryQueryInterface(&factory->Interface, iid, result);
    FactoryRelease(&factory->Interface);
    return hr;
}

HRESULT WINAPI DllCanUnloadNow(void)
{
    return InterlockedCompareExchange(&ModuleReferences, 0, 0) == 0 ? S_OK : S_FALSE;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    UNREFERENCED_PARAMETER(reserved);
    if (reason == DLL_PROCESS_ATTACH) {
        DWORD length;
        Module = instance;
        // The static CRT needs thread notifications; do not disable them.
        // Cache our own resource location once. No COM, registry, or application work under loader lock.
        length = GetModuleFileNameW(instance, IconLocation, ARRAYSIZE(IconLocation) - 6);
        if (!length || length >= ARRAYSIZE(IconLocation) - 6) IconLocation[0] = 0;
        else {
            IconLocation[length++] = L','; IconLocation[length++] = L'-';
            IconLocation[length++] = L'1'; IconLocation[length++] = L'0'; IconLocation[length++] = L'1';
            IconLocation[length] = 0;
        }
    }
    return TRUE;
}
