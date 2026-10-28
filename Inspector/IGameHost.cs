namespace Il2cppConsoleMod.Inspector;

public sealed class NodeDto
{
    public int H { get; set; }
    public string Name { get; set; } = "";
    public bool Active { get; set; }
    public bool? Inactive { get; set; }
    public int Children { get; set; }
    public string[]? Comps { get; set; }
    /// <summary>Hierarchy path, only filled in search results.</summary>
    public string? Path { get; set; }
}

public sealed class SceneDto
{
    public string Name { get; set; } = "";
    public int Index { get; set; }
    public bool Loaded { get; set; }
    public bool? Special { get; set; }
    public List<NodeDto> Roots { get; set; } = new();
}

public sealed class ComponentDto
{
    public ValueDto Value { get; set; } = ValueDescriber.Null;
    public bool? Enabled { get; set; }
    public bool? Missing { get; set; }
}

/// <summary>What the inspector can say about a scene node (GameObject) or something attached to one.</summary>
public sealed class NodeInfoDto
{
    public int H { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Active { get; set; }
    public bool ActiveInHierarchy { get; set; }
    public string? Layer { get; set; }
    public string? Tag { get; set; }
    public string? Scene { get; set; }
    public List<ValueDto> Ancestors { get; set; } = new();
}

/// <summary>
/// Engine-specific views. The Unity implementation talks to the Il2Cpp runtime; keeping it
/// behind an interface lets the web UI be developed against a fake object graph.
/// </summary>
public interface IGameHost
{
    Dictionary<string, object?> Status();
    List<SceneDto> Scenes();
    List<NodeDto> Children(object node);
    List<ComponentDto> Components(object node);
    NodeInfoDto? NodeInfo(object obj);
    void SetActive(object node, bool active);
    void SetEnabled(object component, bool enabled);
    List<NodeDto> SearchNodes(string query, int limit);
    List<ValueDto> FindInstances(Type type, int limit, out int total);
    bool CanFindInstances(Type type);
    bool CanPreview(object obj);
    byte[] Preview(object obj, int maxSize);
}
