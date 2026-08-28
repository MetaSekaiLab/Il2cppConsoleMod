using Il2CppInterop.Runtime;
using UnityEngine;

namespace Il2cppConsoleMod;

public class RuntimeCSharpHost : MonoBehaviour
{
    public RuntimeCSharpHost(IntPtr ptr) : base(ptr)
    {
    }

    public static void Create()
    {
        var go = new GameObject("Il2cppConsoleModHost");
        DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.AddComponent(Il2CppType.Of<RuntimeCSharpHost>());
    }

    private void Update()
    {
        MainThreadDispatcher.DrainAll();
    }
}
