using System.Text.Json;
using TileWall.Core.Configuration;
using TileWall.Core.Entries;

namespace TileWall.Core.Tests.Fixtures;

public enum LnkFaultPoint
{
    None,

    /// <summary>新建 .lnk 之前（F-1 同路：暂存写入失败）。</summary>
    BeforeCreate,

    /// <summary>编辑目标之前（F-3 家族：正式区被改动前抛出）。</summary>
    BeforeEditTarget,

    /// <summary>读取之前。</summary>
    BeforeRead,
}

/// <summary>
/// ILnkFileService 测试替身：字段状态序列化存入 <see cref="IFileStore"/> 字节内
/// （与真 .lnk 一样随 store.Move/Copy 自动跟随——回滚/撤销的字节级断言才有意义）。
/// 故障一次性触发（触发即解除），同步回滚例程不受同一故障二次阻断。
/// </summary>
public sealed class FakeLinkFileService(IFileStore files) : ILnkFileService
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IFileStore _files = files;
    private LnkFaultPoint _fault = LnkFaultPoint.None;

    public LnkFaultPoint Fault
    {
        get => _fault;
        set => _fault = value;
    }

    public void Create(string path, string target, string arguments, string workingDirectory)
    {
        Fire(LnkFaultPoint.BeforeCreate, path);
        if (_files.Exists(path))
        {
            throw new IOException($"[伪链服务] 入口已存在：{path}");
        }

        Write(path, new FakeLinkState(target, arguments, workingDirectory, HasIdList: false));
    }

    public LinkFields Read(string path)
    {
        Fire(LnkFaultPoint.BeforeRead, path);
        var state = ReadState(path);
        return new LinkFields(state.TargetPath, state.Arguments, state.WorkingDirectory, IconPath: null, IconIndex: 0, ShowCmd: 1, state.HasIdList);
    }

    public void EditTargetOnly(string path, string newTarget)
    {
        Fire(LnkFaultPoint.BeforeEditTarget, path);
        var state = ReadState(path);
        Write(path, state with { TargetPath = newTarget }); // 只改目标，参数/工作目录/IDList 原样 [R12][R13]
    }

    /// <summary>测试种子：直接落一份既有入口（模拟历史数据/特殊 IDList 入口）。</summary>
    public void Register(string path, string? target, string arguments = "", string workingDirectory = "", bool hasIdList = false) =>
        Write(path, new FakeLinkState(target, arguments, workingDirectory, hasIdList));

    public bool Exists(string path) => _files.Exists(path);

    public FakeLinkState StateOf(string path) => ReadState(path);

    private FakeLinkState ReadState(string path)
    {
        if (!_files.Exists(path))
        {
            throw new FileNotFoundException("[伪链服务] 入口不存在", path);
        }

        return JsonSerializer.Deserialize<FakeLinkState>(_files.ReadAllBytes(path), Options)
            ?? throw new IOException($"[伪链服务] 入口状态为空：{path}");
    }

    private void Write(string path, FakeLinkState state) =>
        _files.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(state, Options));

    private void Fire(LnkFaultPoint point, string path)
    {
        if (_fault != point)
        {
            return;
        }

        _fault = LnkFaultPoint.None;
        throw new IOException($"注入的链服务失败：{point}（{path}）");
    }
}

/// <summary>FakeLinkFileService 的持久化状态（JSON）。</summary>
public sealed record FakeLinkState(string? TargetPath, string Arguments, string WorkingDirectory, bool HasIdList);
