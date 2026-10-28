using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Il2cppConsoleMod.Inspector;

/// <summary>Unity scene graph, components, texture previews and runtime status. Main thread only.</summary>
public sealed class UnityGameHost : IGameHost
{
    private const int MaxCompNames = 4;
    private const int SearchVisitLimit = 60000;

    public Dictionary<string, object?> Status()
    {
        var active = SceneManager.GetActiveScene();
        var scenes = new List<string>();
        for (var i = 0; i < SceneManager.sceneCount; i++)
            scenes.Add(SceneManager.GetSceneAt(i).name);

        return new Dictionary<string, object?>
        {
            ["product"] = Application.productName,
            ["version"] = Application.version,
            ["unity"] = Application.unityVersion,
            ["platform"] = Application.platform.ToString(),
            ["fps"] = Time.smoothDeltaTime > 0 ? 1f / Time.smoothDeltaTime : 0f,
            ["frame"] = Time.frameCount,
            ["time"] = Time.realtimeSinceStartup,
            ["timeScale"] = Time.timeScale,
            ["focused"] = Application.isFocused,
            ["runInBackground"] = Application.runInBackground,
            ["activeScene"] = active.name,
            ["scenes"] = scenes,
            ["plugins"] = IL2CPPChainloader.Instance.Plugins.Values
                .Select(p => new { guid = p.Metadata.GUID, name = p.Metadata.Name, version = p.Metadata.Version.ToString() })
                .OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    public List<SceneDto> Scenes()
    {
        var result = new List<SceneDto>();
        for (var i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            var dto = new SceneDto { Name = scene.name, Index = scene.buildIndex, Loaded = scene.isLoaded };
            if (scene.isLoaded)
                dto.Roots.AddRange(scene.GetRootGameObjects().Select(Node));
            result.Add(dto);
        }

        var ddol = new SceneDto { Name = "DontDestroyOnLoad", Index = -1, Loaded = true, Special = true };
        ddol.Roots.AddRange(DontDestroyOnLoadRoots().Select(Node));
        result.Add(ddol);
        return result;
    }

    public List<NodeDto> Children(object node)
    {
        var transform = AsGameObject(node)?.transform ?? throw new ArgumentException("not a GameObject");
        var result = new List<NodeDto>(transform.childCount);
        for (var i = 0; i < transform.childCount; i++)
            result.Add(Node(transform.GetChild(i).gameObject));
        return result;
    }

    public List<ComponentDto> Components(object node)
    {
        var go = AsGameObject(node) ?? throw new ArgumentException("not a GameObject");
        var result = new List<ComponentDto>();
        foreach (var component in go.GetComponents(Il2CppType.Of<Component>()))
        {
            if (component == null)
            {
                result.Add(new ComponentDto { Missing = true, Value = new ValueDto { K = "null", T = "Missing script" } });
                continue;
            }

            var dto = new ComponentDto { Value = ValueDescriber.Describe(component) };
            var behaviour = component.TryCast<Behaviour>();
            if (behaviour != null)
                dto.Enabled = behaviour.enabled;
            else if (component.TryCast<Renderer>() is { } renderer)
                dto.Enabled = renderer.enabled;
            result.Add(dto);
        }

        return result;
    }

    public NodeInfoDto? NodeInfo(object obj)
    {
        var go = AsGameObject(obj);
        if (go == null)
            return null;

        var ancestors = new List<ValueDto>();
        var names = new List<string>();
        for (var t = go.transform.parent; t != null; t = t.parent)
        {
            ancestors.Insert(0, ValueDescriber.Describe(t.gameObject));
            names.Insert(0, t.gameObject.name);
        }

        names.Add(go.name);
        var layer = LayerMask.LayerToName(go.layer);
        string? tag = null;
        try
        {
            tag = go.tag;
        }
        catch
        {
            // undefined tags throw
        }

        return new NodeInfoDto
        {
            H = ObjectRegistry.Register(go),
            Name = go.name,
            Path = string.Join("/", names),
            Active = go.activeSelf,
            ActiveInHierarchy = go.activeInHierarchy,
            Layer = string.IsNullOrEmpty(layer) ? go.layer.ToString() : $"{layer} ({go.layer})",
            Tag = tag,
            Scene = go.scene.name,
            Ancestors = ancestors,
        };
    }

    public void SetActive(object node, bool active)
        => (AsGameObject(node) ?? throw new ArgumentException("not a GameObject")).SetActive(active);

    public void SetEnabled(object component, bool enabled)
    {
        if (component is not Component c)
            throw new ArgumentException("not a Component");
        if (c.TryCast<Behaviour>() is { } behaviour)
            behaviour.enabled = enabled;
        else if (c.TryCast<Renderer>() is { } renderer)
            renderer.enabled = enabled;
        else
            throw new InvalidOperationException(c.GetIl2CppType().Name + " cannot be enabled or disabled");
    }

    public List<NodeDto> SearchNodes(string query, int limit)
    {
        var result = new List<NodeDto>();
        var stack = new Stack<Transform>();
        for (var i = SceneManager.sceneCount - 1; i >= 0; i--)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (scene.isLoaded)
                foreach (var root in scene.GetRootGameObjects().Reverse())
                    stack.Push(root.transform);
        }

        foreach (var root in DontDestroyOnLoadRoots())
            stack.Push(root.transform);

        var visited = 0;
        while (stack.Count > 0 && result.Count < limit && visited++ < SearchVisitLimit)
        {
            var t = stack.Pop();
            var go = t.gameObject;
            if (go.name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                var node = Node(go);
                node.Path = PathOf(t);
                result.Add(node);
            }
            for (var i = t.childCount - 1; i >= 0; i--)
                stack.Push(t.GetChild(i));
        }

        return result;
    }

    public bool CanFindInstances(Type type)
        => typeof(Object).IsAssignableFrom(type);

    public List<ValueDto> FindInstances(Type type, int limit, out int total)
    {
        var found = Resources.FindObjectsOfTypeAll(Il2CppType.From(type));
        total = found.Length;
        var result = new List<ValueDto>(Math.Min(limit, total));
        for (var i = 0; i < total && i < limit; i++)
            result.Add(ValueDescriber.Describe(found[i]));
        return result;
    }

    public bool CanPreview(object obj) => obj is Sprite || obj is Texture;

    /// <summary>
    /// Renders a Sprite (its atlas rect) or any Texture into a PNG via a temporary
    /// RenderTexture, so non-readable GPU textures work too.
    /// </summary>
    public byte[] Preview(object obj, int maxSize)
    {
        Texture source;
        Rect? crop = null;
        switch (obj)
        {
            case Sprite sprite:
                source = sprite.texture;
                crop = sprite.textureRect;
                break;
            case Texture texture:
                source = texture;
                break;
            default:
                throw new ArgumentException("only Sprite and Texture can be previewed");
        }

        if (source == null)
            throw new InvalidOperationException("no texture");

        float srcW = source.width, srcH = source.height;
        var width = crop?.width ?? srcW;
        var height = crop?.height ?? srcH;
        var scale = Math.Min(1f, maxSize / Math.Max(width, height));
        var outW = Math.Max(1, (int)Math.Round(width * scale));
        var outH = Math.Max(1, (int)Math.Round(height * scale));

        var rt = RenderTexture.GetTemporary(outW, outH, 0, RenderTextureFormat.ARGB32);
        var previous = RenderTexture.active;
        Texture2D? readback = null;
        try
        {
            if (crop is { } r)
                Graphics.Blit(source, rt, new Vector2(r.width / srcW, r.height / srcH), new Vector2(r.x / srcW, r.y / srcH));
            else
                Graphics.Blit(source, rt);

            RenderTexture.active = rt;
            readback = new Texture2D(outW, outH, TextureFormat.RGBA32, false);
            readback.ReadPixels(new Rect(0, 0, outW, outH), 0, 0);
            readback.Apply();
            return ImageConversion.EncodeToPNG(readback);
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            if (readback != null)
                Object.Destroy(readback);
        }
    }

    private static NodeDto Node(GameObject go)
    {
        var dto = new NodeDto
        {
            H = ObjectRegistry.Register(go),
            Name = go.name,
            Active = go.activeSelf,
            Inactive = go.activeInHierarchy ? null : true,
            Children = go.transform.childCount,
        };

        var names = new List<string>();
        foreach (var component in go.GetComponents(Il2CppType.Of<Component>()))
        {
            var name = component == null ? "Missing" : ShortName(component);
            if (name is "Transform" or null)
                continue;
            names.Add(name);
            if (names.Count >= MaxCompNames)
                break;
        }

        dto.Comps = names.Count > 0 ? names.ToArray() : null;
        return dto;
    }

    private static string PathOf(Transform transform)
    {
        var names = new List<string>();
        for (var t = transform.parent; t != null; t = t.parent)
            names.Insert(0, t.gameObject.name);
        return string.Join("/", names);
    }

    private static string? ShortName(Il2CppObjectBase component)
    {
        var full = Il2CppSupport.NativeTypeName(component);
        if (full == null)
            return null;
        var dot = full.LastIndexOf('.');
        return dot >= 0 ? full.Substring(dot + 1) : full;
    }

    private static GameObject? AsGameObject(object obj) => obj switch
    {
        GameObject go => go,
        Component c => c.gameObject,
        _ => null,
    };

    /// <summary>
    /// The DontDestroyOnLoad scene cannot be enumerated directly; a throwaway probe
    /// object moved into it exposes the scene handle.
    /// </summary>
    private static IEnumerable<GameObject> DontDestroyOnLoadRoots()
    {
        var probe = new GameObject("Il2cppConsoleModProbe");
        try
        {
            Object.DontDestroyOnLoad(probe);
            return probe.scene.GetRootGameObjects().Where(go => go.GetInstanceID() != probe.GetInstanceID()).ToList();
        }
        finally
        {
            Object.DestroyImmediate(probe);
        }
    }
}
