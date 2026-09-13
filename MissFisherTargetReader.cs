using System.Reflection;
using System.Collections;

namespace MissFisherIceScheduler;

internal sealed record MissFisherResumeOption(
    MissFisherResumeKind Kind,
    string Key,
    string Name,
    string Label);

internal sealed class MissFisherTargetReader
{
    private enum ReflectionSchema
    {
        Version224,
        Version2301,
        Version2302,
        Version231,
        Version240,
    }

    public string Status { get; private set; } = "尚未探测";

    private Assembly? missFisherAssembly;
    private ReflectionSchema? reflectionSchema;
    private FieldInfo? checklistRunnerField;
    private PropertyInfo? currentTargetProperty;
    private MethodInfo? timingMethod;
    private MethodInfo? windowStartMethod;
    private MethodInfo? startCustomChecklistMethod;
    private MethodInfo? startAlbumMethod;
    private MethodInfo? startFishLogMethod;
    private MethodInfo? startSucceededMethod;
    private MethodInfo? startFailureMessageMethod;
    private IReadOnlyList<MissFisherResumeOption> cachedResumeOptions = [];
    private DateTime resumeOptionsValidUntilUtc = DateTime.MinValue;

    public bool TryGetCurrentTargetWindowStart(out DateTimeOffset windowStart)
    {
        windowStart = default;

        try
        {
            var assembly = FindActiveMissFisherAssembly();
            if (assembly is null)
            {
                Status = "MissFisher 未加载";
                return false;
            }

            if (assembly != missFisherAssembly && !ResolveMembers(assembly))
            {
                Status = "当前 MissFisher 版本不兼容精确目标读取";
                return false;
            }

            var runner = checklistRunnerField?.GetValue(null);
            var target = runner is null ? null : currentTargetProperty?.GetValue(runner);
            var timing = target is null ? null : timingMethod?.Invoke(target, null);
            var value = timing is null ? null : windowStartMethod?.Invoke(timing, null);
            if (value is not DateTimeOffset start)
            {
                Status = "兼容，当前没有可冻结的目标窗口";
                return false;
            }

            windowStart = start;
            Status = $"兼容 {GetAssemblyVersion(assembly)}，使用 MissFisher 当前目标窗口";
            return true;
        }
        catch (Exception ex)
        {
            Status = $"精确目标读取失败：{ex.GetType().Name}";
            ClearMembers();
            return false;
        }
    }

    public bool TryStartResumeTarget(
        MissFisherResumeKind kind,
        string key,
        string name,
        out string failureMessage)
    {
        failureMessage = string.Empty;

        try
        {
            var assembly = FindActiveMissFisherAssembly();
            if (assembly is null)
            {
                Status = "MissFisher 未加载";
                failureMessage = "MissFisher 程序集尚未加载";
                return false;
            }

            if (assembly != missFisherAssembly && !ResolveMembers(assembly))
            {
                Status = "当前 MissFisher 版本不兼容恢复目标启动";
                failureMessage = "当前 MissFisher 版本没有兼容的恢复目标启动入口";
                return false;
            }

            object? result;
            switch (kind)
            {
                case MissFisherResumeKind.FishLog:
                    result = startFishLogMethod?.Invoke(null, null);
                    break;
                case MissFisherResumeKind.Album:
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        failureMessage = "MissFisher 分组标识为空";
                        return false;
                    }
                    result = startAlbumMethod?.Invoke(null, [key, name]);
                    break;
                case MissFisherResumeKind.Collection:
                    if (!Guid.TryParse(key, out var checklistId))
                    {
                        failureMessage = "MissFisher 合集 ID 无效";
                        return false;
                    }
                    result = startCustomChecklistMethod?.Invoke(null, [checklistId, name]);
                    break;
                default:
                    failureMessage = "未知的 MissFisher 恢复目标类型";
                    return false;
            }

            if (result is null || startSucceededMethod?.Invoke(result, null) is not true)
            {
                failureMessage = result is null
                    ? "MissFisher 未返回启动结果"
                    : startFailureMessageMethod?.Invoke(result, null) as string ?? "MissFisher 拒绝启动该清单";
                return false;
            }

            return true;
        }
        catch (TargetInvocationException ex)
        {
            Status = $"清单启动失败：{ex.InnerException?.GetType().Name ?? ex.GetType().Name}";
            failureMessage = ex.InnerException?.Message ?? ex.Message;
            ClearMembers();
            return false;
        }
        catch (Exception ex)
        {
            Status = $"清单启动失败：{ex.GetType().Name}";
            failureMessage = ex.Message;
            ClearMembers();
            return false;
        }
    }

    public bool TryGetResumeOptions(out IReadOnlyList<MissFisherResumeOption> options)
    {
        options = cachedResumeOptions;
        if (DateTime.UtcNow < resumeOptionsValidUntilUtc && cachedResumeOptions.Count > 0)
            return true;

        try
        {
            var assembly = FindActiveMissFisherAssembly();
            if (assembly is null)
            {
                Status = "MissFisher 未加载";
                return false;
            }

            if (assembly != missFisherAssembly && !ResolveMembers(assembly))
            {
                Status = "当前 MissFisher 版本不兼容恢复目标读取";
                return false;
            }

            var discovered = new List<MissFisherResumeOption>
            {
                new(MissFisherResumeKind.FishLog, "fish-log", "鱼类图鉴（非副本）", "图鉴：鱼类图鉴（非副本）"),
            };

            AddAlbumOptions(assembly, reflectionSchema!.Value, discovered);
            AddCollectionOptions(assembly, reflectionSchema.Value, discovered);
            cachedResumeOptions = discovered;
            resumeOptionsValidUntilUtc = DateTime.UtcNow.AddSeconds(5);
            options = cachedResumeOptions;
            Status = $"兼容 {GetAssemblyVersion(assembly)}，已读取 {discovered.Count} 个恢复目标";
            return true;
        }
        catch (Exception ex)
        {
            Status = $"恢复目标读取失败：{ex.GetType().Name}";
            resumeOptionsValidUntilUtc = DateTime.UtcNow.AddSeconds(5);
            return false;
        }
    }

    private static void AddAlbumOptions(
        Assembly assembly,
        ReflectionSchema schema,
        List<MissFisherResumeOption> options)
    {
        var modern = schema != ReflectionSchema.Version224;
        var stateTypeName = schema switch
        {
            ReflectionSchema.Version240 => "g.gB",
            ReflectionSchema.Version231 => "G.GZ",
            _ when modern => "G.Gy",
            _ => "G.Gp",
        };
        var stateProviderName = schema switch
        {
            ReflectionSchema.Version240 => "G.GY",
            ReflectionSchema.Version231 => "G.GW",
            ReflectionSchema.Version2302 => "G.Gv",
            ReflectionSchema.Version2301 => "G.GV",
            _ => "G.GN",
        };
        var catalogProviderName = schema == ReflectionSchema.Version240
            ? "G.Go"
            : modern ? "G.GM" : "G.GE";
        var stateType = assembly.GetType(stateTypeName) ?? throw new MissingMemberException(stateTypeName);
        var state = GetParameterlessMethod(
                assembly.GetType(stateProviderName),
                "B",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Invoke(null, null);
        var catalog = assembly.GetType(catalogProviderName)?.GetMethod(
                "A",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: [stateType],
                modifiers: null)
            ?.Invoke(null, [state]);
        var groups = GetParameterlessMethod(
                catalog?.GetType(),
                "c",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Invoke(catalog, null) as IEnumerable;
        if (groups is null)
            throw new MissingMemberException("MissFisher 内置分组列表");

        foreach (var group in groups)
        {
            if (group is null)
                continue;
            var rawName = GetParameterlessMethod(group.GetType(), "a", BindingFlags.Instance | BindingFlags.Public)
                .Invoke(group, null) as string;
            var displayName = GetParameterlessMethod(group.GetType(), "b", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(group, null) as string;
            if (string.IsNullOrWhiteSpace(rawName))
                continue;
            displayName = string.IsNullOrWhiteSpace(displayName) ? rawName : displayName;
            options.Add(new(MissFisherResumeKind.Album, $"section:{rawName}", displayName, $"分组：{displayName}"));
        }
    }

    private static void AddCollectionOptions(
        Assembly assembly,
        ReflectionSchema schema,
        List<MissFisherResumeOption> options)
    {
        var plugin = GetParameterlessMethod(assembly.GetType("MissFisher.App.Plugin"), "A", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, null);
        var managerMethodName = schema == ReflectionSchema.Version224 ? "n" : "O";
        var idPropertyName = schema switch
        {
            ReflectionSchema.Version240 => "bEa",
            ReflectionSchema.Version231 => "bDy",
            ReflectionSchema.Version2302 => "bDT",
            ReflectionSchema.Version2301 => "bDl",
            _ => "baS",
        };
        var namePropertyName = schema switch
        {
            ReflectionSchema.Version240 => "bEB",
            ReflectionSchema.Version231 => "bDZ",
            ReflectionSchema.Version2302 => "bDt",
            ReflectionSchema.Version2301 => "bDM",
            _ => "bas",
        };
        var manager = GetParameterlessMethod(plugin?.GetType(), managerMethodName, BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(plugin, null);
        var collections = GetParameterlessMethod(manager?.GetType(), "A", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(manager, null) as IEnumerable;
        if (collections is null)
            throw new MissingMemberException("MissFisher 自定义合集列表");

        foreach (var collection in collections)
        {
            if (collection is null)
                continue;
            var id = collection.GetType().GetProperty(idPropertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(collection);
            var name = collection.GetType().GetProperty(namePropertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(collection) as string;
            if (id is not Guid checklistId || string.IsNullOrWhiteSpace(name))
                continue;
            options.Add(new(MissFisherResumeKind.Collection, checklistId.ToString(), name, $"合集：{name}"));
        }
    }

    private static MethodInfo GetParameterlessMethod(Type? type, string name, BindingFlags flags) =>
        type?.GetMethod(name, flags, binder: null, types: Type.EmptyTypes, modifiers: null)
        ?? throw new MissingMemberException(type?.FullName ?? "unknown", name);

    private static Assembly? FindActiveMissFisherAssembly() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(candidate => candidate.GetName().Name == "MissFisher")
            .OrderByDescending(candidate => candidate.GetName().Version)
            .FirstOrDefault();

    private static string GetAssemblyVersion(Assembly assembly) =>
        assembly.GetName().Version?.ToString() ?? "未知版本";

    private bool ResolveMembers(Assembly assembly)
    {
        ClearMembers();

        var bridge240Type = assembly.GetType("E.EQ");
        var runner240Field = bridge240Type?.GetField("yi", BindingFlags.Static | BindingFlags.NonPublic);
        var modern240 = runner240Field?.FieldType.FullName == "i.iS";
        var modernBridgeType = assembly.GetType("E.EP");
        var runnerField = modernBridgeType?.GetField("yf", BindingFlags.Static | BindingFlags.NonPublic);
        var modern231 = runnerField?.FieldType.Name == "iQ";
        var modern2302 = runnerField is not null && !modern231;
        var modern2301 = modernBridgeType?.GetField("yF", BindingFlags.Static | BindingFlags.NonPublic) is not null;
        reflectionSchema = modern240
            ? ReflectionSchema.Version240
            : modern231
            ? ReflectionSchema.Version231
            : modern2302
                ? ReflectionSchema.Version2302
            : modern2301
                ? ReflectionSchema.Version2301
                : ReflectionSchema.Version224;
        var modern = modern240 || modern231 || modern2302 || modern2301;
        var bridgeType = modern240 ? bridge240Type : modern ? modernBridgeType : assembly.GetType("E.EL");
        checklistRunnerField = modern240
            ? runner240Field
            : bridgeType?.GetField(
                modern231 || modern2302 ? "yf" : modern2301 ? "yF" : "YI",
                BindingFlags.Static | BindingFlags.NonPublic);
        var runnerType = checklistRunnerField?.FieldType;
        currentTargetProperty = runnerType?.GetProperty(
            modern240 ? "bfB" : modern231 ? "beZ" : modern2302 ? "bet" : modern2301 ? "beM" : "bCp",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var targetType = currentTargetProperty?.PropertyType;
        timingMethod = targetType?.GetMethod(
            "f",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        var timingType = timingMethod?.ReturnType;
        windowStartMethod = timingType?.GetMethod(
            "B",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        startCustomChecklistMethod = bridgeType?.GetMethod(
            "A",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(Guid), typeof(string)],
            modifiers: null);
        startAlbumMethod = bridgeType?.GetMethod(
            "A",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string), typeof(string)],
            modifiers: null);
        startFishLogMethod = bridgeType?.GetMethod(
            "e",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        var startResultType = startCustomChecklistMethod?.ReturnType;
        startSucceededMethod = startResultType?.GetMethod(
            "A",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        startFailureMessageMethod = startResultType?.GetMethod(
            "a",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);

        if (checklistRunnerField is null
            || currentTargetProperty is null
            || timingMethod is null
            || windowStartMethod is null
            || startCustomChecklistMethod is null
            || startAlbumMethod is null
            || startFishLogMethod is null
            || startSucceededMethod is null
            || startFailureMessageMethod is null)
        {
            ClearMembers();
            return false;
        }

        missFisherAssembly = assembly;
        return true;
    }

    private void ClearMembers()
    {
        missFisherAssembly = null;
        reflectionSchema = null;
        checklistRunnerField = null;
        currentTargetProperty = null;
        timingMethod = null;
        windowStartMethod = null;
        startCustomChecklistMethod = null;
        startAlbumMethod = null;
        startFishLogMethod = null;
        startSucceededMethod = null;
        startFailureMessageMethod = null;
        cachedResumeOptions = [];
        resumeOptionsValidUntilUtc = DateTime.MinValue;
    }
}
