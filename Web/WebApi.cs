using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Il2cppConsoleMod.Inspector;
using Il2CppInterop.Runtime.InteropTypes;

namespace Il2cppConsoleMod.Web;

/// <summary>
/// JSON API behind the web UI (<c>/api/*</c>, token required). Anything that reads game
/// objects hops to the Unity main thread; pure reflection / compiler work (type catalog,
/// completion, method lists, logs) answers from the HTTP thread, so the UI stays usable
/// while the game is busy.
/// </summary>
internal sealed class WebApi
{
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Changes on every game start so the UI can drop handles from a previous run.</summary>
    private static readonly string BootId = Guid.NewGuid().ToString("N");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly CSharpScriptSession _session;
    private readonly IGameHost _host;
    private readonly LogBuffer? _logs;

    public WebApi(CSharpScriptSession session, IGameHost host, LogBuffer? logs)
    {
        _session = session;
        _host = host;
        _logs = logs;
    }

    public void Handle(HttpRequest request, Stream stream)
    {
        object? result;
        try
        {
            result = Dispatch(request);
        }
        catch (TimeoutException ex)
        {
            Write(stream, 503, new { error = ex.Message, code = "main-thread-timeout" });
            return;
        }
        catch (ApiError ex)
        {
            Write(stream, ex.Status, new { error = ex.Message, code = ex.Code });
            return;
        }
        catch (Exception ex)
        {
            Write(stream, 500, new { error = ValueDescriber.Message(ex) });
            return;
        }

        if (result is byte[] png)
            HttpResponse.Write(stream, 200, "image/png", png);
        else
            Write(stream, 200, result);
    }

    private object? Dispatch(HttpRequest request)
    {
        var q = request.Query;
        var get = request.Method == "GET";
        var post = request.Method == "POST";
        var body = post ? ParseBody(request.Body) : default;

        switch (request.Route)
        {
            // ---- runtime ------------------------------------------------------------------
            case "/api/status" when get:
            {
                var status = MainThreadDispatcher.Run(() => _host.Status(), StatusTimeout);
                status["boot"] = BootId;
                status["mod"] = MyPluginInfo.PLUGIN_VERSION;
                status["handles"] = ObjectRegistry.Count;
                return status;
            }
            case "/api/scenes" when get:
                return Main(() => _host.Scenes());
            case "/api/children" when get:
                return Main(() => _host.Children(Target(QInt(q, "h"))));
            case "/api/components" when get:
                return Main(() => _host.Components(Target(QInt(q, "h"))));
            case "/api/nodes/search" when get:
                return Main(() => _host.SearchNodes(QStr(q, "q"), QInt(q, "limit", 200)));
            case "/api/active" when post:
                return Main(() =>
                {
                    _host.SetActive(Target(Int(body, "h")), Bool(body, "active"));
                    return _host.NodeInfo(Target(Int(body, "h")));
                });
            case "/api/enabled" when post:
                return Main(() =>
                {
                    _host.SetEnabled(Target(Int(body, "h")), Bool(body, "enabled"));
                    return new { ok = true };
                });
            case "/api/preview" when get:
                return Main(() => _host.Preview(Target(QInt(q, "h")), Math.Clamp(QInt(q, "max", 1024), 16, 4096)));

            // ---- objects ------------------------------------------------------------------
            case "/api/inspect" when get:
                return Main(() => Inspect(QInt(q, "h"), q.TryGetValue("all", out var all) && all == "1"));
            case "/api/member" when get:
                return Main(() => ObjectInspector.GetMember(Target(QInt(q, "h")), QStr(q, "name")));
            case "/api/items" when get:
                return Main(() => ObjectInspector.GetItems(Target(QInt(q, "h")), QInt(q, "offset", 0), QInt(q, "limit", 100)));
            case "/api/methods" when get:
                return ObjectInspector.GetMethods(Target(QInt(q, "h")));
            case "/api/text" when get:
                return new { text = Target(QInt(q, "h")) as string ?? throw new ApiError(400, "not a string handle") };
            case "/api/set" when post:
                return Main(() => ObjectInspector.SetMember(Target(Int(body, "h")), Str(body, "name"), Prop(body, "value")));
            case "/api/invoke" when post:
                return Main(() => Invoke(body));
            case "/api/find" when get:
                return Main(() => FindInstances(QInt(q, "h"), QInt(q, "limit", 500)));
            case "/api/handles/clear" when post:
                ObjectRegistry.Clear();
                return new { ok = true };

            // ---- types --------------------------------------------------------------------
            case "/api/assemblies" when get:
                return TypeCatalog.Assemblies();
            case "/api/types" when get:
                return TypeCatalog.TypesIn(QStr(q, "asm"))
                    .GroupBy(t => t.Namespace)
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new
                    {
                        ns = g.Key,
                        types = g.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(TypeRow).ToList(),
                    })
                    .ToList();
            case "/api/types/search" when get:
                return TypeCatalog.Search(QStr(q, "q"), QInt(q, "limit", 60)).Select(TypeRow).ToList();
            case "/api/type" when get:
            {
                var type = TypeCatalog.Resolve(QStr(q, "full"), q.TryGetValue("asm", out var asm) && asm.Length > 0 ? asm : null)
                           ?? throw new ApiError(404, "type not found");
                return new { h = ObjectRegistry.Register(StaticTypeRef.For(type)), name = TypeNames.Display(type) };
            }

            // ---- console ------------------------------------------------------------------
            case "/api/eval" when post:
                return Eval(body);
            case "/api/complete" when post:
                return ScriptCompletion.Complete(_session, Str(body, "code"), Int(body, "pos"));
            case "/api/vars" when get:
                return Main(() => _session.Variables()
                    .Select(v => new { name = v.Name, type = TypeNames.Display(v.Type), value = ValueDescriber.Describe(v.Value) })
                    .ToList());
            case "/api/watch" when post:
                return Watch(body);
            case "/api/script/reset" when post:
                _session.Reset();
                return new { ok = true };
            case "/api/logs" when get:
                return Logs(QLong(q, "after", -1), QInt(q, "limit", 2000));
        }

        throw new ApiError(404, $"not found: {request.Method} {request.Route}");
    }

    private object Inspect(int h, bool readStaticProperties)
    {
        var target = Target(h);
        if (target is string text)
            return new { h, text, value = ValueDescriber.Describe(text) };

        var (type, instance) = ObjectInspector.Target(target);
        var statics = instance == null;
        var facets = new List<string>();
        NodeInfoDto? node = null;

        if (statics)
        {
            if (_host.CanFindInstances(type))
                facets.Add("instances");
        }
        else
        {
            if (ValueDescriber.IsCollection(type))
                facets.Add("items");
            node = _host.NodeInfo(target);
            if (node != null)
                facets.Add(node.H == h ? "gameobject" : "component");
            if (_host.CanPreview(target))
                facets.Add("preview");
        }

        var self = statics
            ? new ValueDto { K = "type", T = "Type", V = TypeNames.Display(type) }
            : ValueDescriber.Describe(target);
        self.H = h;

        return new
        {
            h,
            isStatic = statics,
            type = TypeInfo(type),
            value = self,
            address = statics ? null : Address(target),
            facets,
            count = statics ? null : ValueDescriber.CountOf(target),
            bases = ObjectInspector.BaseChain(type),
            members = ObjectInspector.GetMembers(target, readStaticProperties),
            node,
        };
    }

    private static object TypeInfo(Type type)
    {
        var nameable = TypeNames.NameableType(type);
        return new
        {
            name = TypeNames.Display(type),
            full = type.FullName ?? type.Name,
            asm = type.Assembly.GetName().Name,
            kind = TypeCatalog.KindOf(type),
            native = typeof(Il2CppObjectBase).IsAssignableFrom(type) ? true : (bool?)null,
            cs = nameable == null ? null : TypeNames.CSharp(nameable),
        };
    }

    private static object TypeRow(TypeCatalog.TypeEntry t) => new
    {
        name = t.Name,
        full = t.FullName,
        ns = t.Namespace,
        asm = t.Assembly,
        kind = t.Kind,
        unity = t.Unity ? true : (bool?)null,
        native = t.Native ? true : (bool?)null,
    };

    private static string? Address(object target)
        => target is Il2CppObjectBase native ? NativeAddress(native) : null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? NativeAddress(Il2CppObjectBase native)
    {
        try
        {
            return "0x" + native.Pointer.ToString("X");
        }
        catch
        {
            return null;
        }
    }

    private object Invoke(JsonElement body)
    {
        var target = Target(Int(body, "h"));
        var args = body.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();

        var result = ObjectInspector.Invoke(target, Int(body, "id"), args, out var isVoid);
        if (isVoid)
            return new ValueDto { K = "void" };
        if (result != null)
            ObjectRegistry.Last = result;
        return ValueDescriber.Describe(result);
    }

    private object FindInstances(int h, int limit)
    {
        if (Target(h) is not StaticTypeRef typeRef)
            throw new ApiError(400, "expected a type handle");
        if (!_host.CanFindInstances(typeRef.Type))
            throw new ApiError(400, TypeNames.Display(typeRef.Type) + " is not a UnityEngine.Object type");

        var items = _host.FindInstances(typeRef.Type, Math.Clamp(limit, 1, 5000), out var total);
        return new { total, items };
    }

    private object Eval(JsonElement body)
    {
        var code = Str(body, "code");
        var program = body.TryGetProperty("mode", out var mode) && mode.GetString() == "program";
        var rewritten = HandleRewriter.Rewrite(code);

        var stopwatch = Stopwatch.StartNew();
        object? value;
        var result = program ? CSharpExecutor.Execute(rewritten, out value) : _session.Submit(rewritten, out value);
        stopwatch.Stop();

        ValueDto? described = null;
        if (result.Success && value != null)
        {
            ObjectRegistry.Last = value;
            described = Main(() => ValueDescriber.Describe(value));
        }

        return new
        {
            success = result.Success,
            output = string.IsNullOrEmpty(result.Output) ? null : result.Output,
            error = result.Error,
            value = described,
            ms = stopwatch.ElapsedMilliseconds,
        };
    }

    private sealed class WatchResult
    {
        public ValueDto? Value { get; set; }
        public string? Error { get; set; }
    }

    private object Watch(JsonElement body)
    {
        var expressions = body.TryGetProperty("exprs", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
            : new List<string>();

        // Compile on this thread; only evaluation needs the main thread.
        var compiled = expressions.Select(e => WatchEvaluator.Compile(_session, e)).ToList();
        return Main(() => compiled.Select(c =>
        {
            if (c.Run == null)
                return new WatchResult { Error = c.Error };
            try
            {
                return new WatchResult { Value = ValueDescriber.Describe(c.Run()) };
            }
            catch (Exception ex)
            {
                return new WatchResult { Error = ValueDescriber.Message(ex) };
            }
        }).ToList());
    }

    private object Logs(long after, int limit)
    {
        if (_logs == null)
            return new { entries = Array.Empty<object>(), next = 0L, dropped = false };

        var (entries, next, dropped) = _logs.Since(after, Math.Clamp(limit, 1, 5000));
        return new
        {
            entries = entries.Select(e => new { seq = e.Seq, t = e.Time, lv = e.Level, src = e.Source, text = e.Text }),
            next,
            dropped,
        };
    }

    // ---- plumbing ------------------------------------------------------------------------

    private static T Main<T>(Func<T> func) => MainThreadDispatcher.Run(func, UiTimeout);

    private static object Target(int h)
        => ObjectRegistry.TryGet(h, out var value) ? value : throw new ApiError(410, $"handle ${h} has expired", "expired");

    private static void Write(Stream stream, int status, object? value)
        => HttpResponse.Json(stream, status, JsonSerializer.Serialize(value, JsonOptions));

    private static JsonElement ParseBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return default;
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(body);
        }
        catch (JsonException ex)
        {
            throw new ApiError(400, "invalid JSON body: " + ex.Message);
        }
    }

    private static JsonElement Prop(JsonElement body, string name)
        => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value)
            ? value
            : throw new ApiError(400, $"missing '{name}'");

    private static string Str(JsonElement body, string name)
        => Prop(body, name) is { ValueKind: JsonValueKind.String } value ? value.GetString()! : throw new ApiError(400, $"'{name}' must be a string");

    private static int Int(JsonElement body, string name)
        => Prop(body, name) is { ValueKind: JsonValueKind.Number } value ? value.GetInt32() : throw new ApiError(400, $"'{name}' must be a number");

    private static bool Bool(JsonElement body, string name)
        => Prop(body, name).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ApiError(400, $"'{name}' must be a boolean"),
        };

    private static string QStr(Dictionary<string, string> q, string name)
        => q.TryGetValue(name, out var value) ? value : throw new ApiError(400, $"missing query '{name}'");

    private static int QInt(Dictionary<string, string> q, string name, int? fallback = null)
    {
        if (q.TryGetValue(name, out var text) && int.TryParse(text, out var value))
            return value;
        return fallback ?? throw new ApiError(400, $"missing query '{name}'");
    }

    private static long QLong(Dictionary<string, string> q, string name, long fallback)
        => q.TryGetValue(name, out var text) && long.TryParse(text, out var value) ? value : fallback;

    private sealed class ApiError : Exception
    {
        public ApiError(int status, string message, string? code = null) : base(message)
        {
            Status = status;
            Code = code;
        }

        public int Status { get; }
        public string? Code { get; }
    }
}
