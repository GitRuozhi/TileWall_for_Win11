using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace TileWall.Shell.Interop;

// Only the string values and one command subkey authored by TileWall are mutable here.
// A key with any other shape is represented explicitly and cannot be transferred.
internal sealed record ExplorerMenuVerb(string? Title = null, string? MuiVerb = null,
    string? Icon = null, string? Selection = null, string? AppliesTo = null, string? Command = null,
    bool Unknown = false)
{
    internal bool IsPartOf(ExplorerMenuVerb complete) => !Unknown &&
        (Title is null || Title == complete.Title) && (MuiVerb is null || MuiVerb == complete.MuiVerb) &&
        (Icon is null || Icon == complete.Icon) && (Selection is null || Selection == complete.Selection) &&
        (AppliesTo is null || AppliesTo == complete.AppliesTo) && (Command is null || Command == complete.Command);
}

internal abstract class ExplorerContextMenuRegistry : IDisposable
{
    protected sealed record Key(string[] Children, Dictionary<string, string>? Values);

    private static readonly string[] ValueNames = ["", "MUIVerb", "Icon", "MultiSelectModel", "AppliesTo"];
    private const string CommandSubkey = @"\command";
    private static readonly ExplorerMenuVerb UnknownVerb = new(Unknown: true);

    internal ExplorerMenuVerb? Read(string path)
    {
        var key = ReadKey(path);
        if (key is null) return null;
        if (key.Values is null || key.Values.Keys.Any(name => !ValueNames.Contains(name)) ||
            key.Children.Any(name => name != "command")) return UnknownVerb;

        string? command = null;
        if (key.Children.Length != 0)
        {
            var entry = ReadKey(path + CommandSubkey);
            if (entry?.Values is null || entry.Children.Length != 0 ||
                entry.Values.Keys.Any(name => name != string.Empty)) return UnknownVerb;
            command = entry.Values.GetValueOrDefault(string.Empty);
        }
        return new(key.Values.GetValueOrDefault(string.Empty), key.Values.GetValueOrDefault("MUIVerb"),
            key.Values.GetValueOrDefault("Icon"), key.Values.GetValueOrDefault("MultiSelectModel"),
            key.Values.GetValueOrDefault("AppliesTo"), command);
    }

    internal void Delete(string path) => DeleteVerb(path, path + CommandSubkey);

    internal void Write(string path, ExplorerMenuVerb entry)
    {
        CreateKey(path);
        if (entry.Title is not null) SetString(path, string.Empty, entry.Title);
        if (entry.MuiVerb is not null) SetString(path, "MUIVerb", entry.MuiVerb);
        if (entry.Icon is not null) SetString(path, "Icon", entry.Icon);
        if (entry.Selection is not null) SetString(path, "MultiSelectModel", entry.Selection);
        if (entry.AppliesTo is not null) SetString(path, "AppliesTo", entry.AppliesTo);
        if (entry.Command is null) return;
        var command = path + CommandSubkey;
        CreateKey(command);
        SetString(command, string.Empty, entry.Command);
    }

    protected abstract Key? ReadKey(string path);
    protected abstract void DeleteVerb(string path, string commandPath);
    protected abstract void CreateKey(string path);
    protected abstract void SetString(string path, string name, string value);
    public abstract void Dispose();

    [SupportedOSPlatform("windows")]
    internal sealed class CurrentUser : ExplorerContextMenuRegistry
    {
        private readonly RegistryKey _root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);

        protected override Key? ReadKey(string path)
        {
            using var key = _root.OpenSubKey(path);
            if (key is null) return null;
            var children = key.GetSubKeyNames();
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in key.GetValueNames())
            {
                if (key.GetValueKind(name) != RegistryValueKind.String || key.GetValue(name) is not string value)
                    return new(children, null);
                values.Add(name, value);
            }
            return new(children, values);
        }

        protected override void DeleteVerb(string path, string commandPath)
        {
            _root.DeleteSubKey(commandPath, throwOnMissingSubKey: false);
            _root.DeleteSubKey(path, throwOnMissingSubKey: false);
        }

        protected override void CreateKey(string path)
        {
            using var key = _root.CreateSubKey(path);
        }

        protected override void SetString(string path, string name, string value)
        {
            using var key = _root.OpenSubKey(path, writable: true) ??
                throw new IOException($"Explorer menu key disappeared: {path}");
            key.SetValue(name, value, RegistryValueKind.String);
        }

        public override void Dispose() => _root.Dispose();
    }

#if TILEWALL_PACKAGED
    // WMI's built-in local registry provider runs outside the package and impersonates
    // this ordinary user. Never use the MSIX process's merged HKCU for transfer/readback.
    internal sealed class PhysicalCurrentUser : ExplorerContextMenuRegistry
    {
        private const int HkeyCurrentUser = unchecked((int)0x80000001u);
        private const int Impersonate = 3, ConnectWithMaximumWait = 0x80;
        private const uint Success = 0, FileNotFound = 2, PathNotFound = 3;
        private readonly dynamic _services;
        private readonly dynamic _context;
        private readonly dynamic _provider;

        internal PhysicalCurrentUser()
        {
            dynamic locator = CreateAutomationObject("WbemScripting.SWbemLocator");
            try
            {
                _services = locator.ConnectServer(".", @"root\default", "", "", "", "", ConnectWithMaximumWait);
                try
                {
                    dynamic security = _services.Security_;
                    try { security.ImpersonationLevel = Impersonate; }
                    finally { Release(security); }
                    _context = CreateAutomationObject("WbemScripting.SWbemNamedValueSet");
                    try
                    {
                        Release(_context.Add("__ProviderArchitecture", 64));
                        Release(_context.Add("__RequiredArchitecture", true));
                        _provider = _services.Get("StdRegProv");
                    }
                    catch { Release(_context); throw; }
                }
                catch { Release(_services); throw; }
            }
            finally { Release(locator); }
        }

        protected override Key? ReadKey(string path)
        {
            var subkeys = Run("EnumKey", path, outputs: ["sNames"]);
            if (subkeys.Status is FileNotFound or PathNotFound) return null;
            Check(subkeys.Status, "EnumKey", path);
            var children = Strings(subkeys.Values[0]);
            var entries = Run("EnumValues", path, outputs: ["sNames", "Types"]);
            Check(entries.Status, "EnumValues", path);
            var names = Strings(entries.Values[0]);
            var types = entries.Values[1] as Array;
            if ((types?.Length ?? 0) != names.Length) throw new IOException($"Invalid registry provider result: {path}");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < names.Length; index++)
            {
                if (Convert.ToInt32(types!.GetValue(index)) != (int)RegistryValueKind.String) return new(children, null);
                var value = Run("GetStringValue", path, names[index], outputs: ["sValue"]);
                Check(value.Status, "GetStringValue", path);
                if (value.Values[0] is not string text) return new(children, null);
                values.Add(names[index], text);
            }
            // The provider can omit the default value when it is the key's only value.
            if (!values.ContainsKey(string.Empty))
            {
                var unnamed = Run("GetStringValue", path, string.Empty, outputs: ["sValue"]);
                if (unnamed.Status is not (FileNotFound or PathNotFound))
                {
                    Check(unnamed.Status, "GetStringValue", path);
                    if (unnamed.Values[0] is not string text) return new(children, null);
                    values.Add(string.Empty, text);
                }
            }
            return new(children, values);
        }

        protected override void DeleteVerb(string path, string commandPath)
        {
            DeleteKey(commandPath);
            DeleteKey(path);
        }

        private void DeleteKey(string path)
        {
            var result = Run("DeleteKey", path).Status;
            if (result is not (FileNotFound or PathNotFound)) Check(result, "DeleteKey", path);
        }

        protected override void CreateKey(string path) => Check(Run("CreateKey", path).Status, "CreateKey", path);

        protected override void SetString(string path, string name, string value) =>
            Check(Run("SetStringValue", path, name, value).Status, "SetStringValue", path);

        private (uint Status, object?[] Values) Run(string method, string path, string? name = null,
            string? value = null, string[]? outputs = null)
        {
            dynamic methods = _provider.Methods_;
            dynamic? definition = null, parameters = null, input = null, output = null;
            try
            {
                definition = methods.Item(method);
                parameters = definition.InParameters;
                input = parameters.SpawnInstance_();
                SetProperty(input, "hDefKey", HkeyCurrentUser);
                SetProperty(input, "sSubKeyName", path);
                if (name is not null) SetProperty(input, "sValueName", name);
                if (value is not null) SetProperty(input, "sValue", value);
                output = _services.ExecMethod("StdRegProv", method, input, 0, _context);
                var status = Convert.ToUInt32(GetProperty((object)output, "ReturnValue"));
                return (status, status == Success && outputs is not null
                    ? outputs.Select(propertyName => GetProperty((object)output, propertyName)).ToArray() : []);
            }
            finally
            {
                Release(output);
                Release(input);
                Release(parameters);
                Release(definition);
                Release(methods);
            }
        }

        private static void SetProperty(dynamic instance, string name, object value)
        {
            dynamic properties = instance.Properties_;
            dynamic? property = null;
            try { property = properties.Item(name); property.Value = value; }
            finally { Release(property); Release(properties); }
        }

        private static object? GetProperty(dynamic instance, string name)
        {
            dynamic properties = instance.Properties_;
            dynamic? property = null;
            try { property = properties.Item(name); return property.Value; }
            finally { Release(property); Release(properties); }
        }

        private static string[] Strings(object? value) => value switch
        {
            null or DBNull => [],
            string[] names => names,
            Array names => names.Cast<string>().ToArray(),
            _ => throw new IOException("The registry provider returned an unexpected list.")
        };

        private static void Check(uint result, string method, string path)
        {
            if (result != Success)
                throw new IOException($"Physical HKCU {method} failed for {path} (error {result}).");
        }

        private static object CreateAutomationObject(string progId) =>
            Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!) ??
                throw new IOException($"Windows registry provider is unavailable: {progId}");

        private static void Release(object? instance)
        {
            if (instance is not null && Marshal.IsComObject(instance)) _ = Marshal.ReleaseComObject(instance);
        }

        public override void Dispose()
        {
            Release(_provider);
            Release(_context);
            Release(_services);
        }
    }
#endif
}
