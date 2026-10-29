using System.Runtime.InteropServices;
using BepInEx.Logging;
using Il2CppInterop.Runtime;

namespace Il2cppConsoleMod;

/// <summary>
/// Drains the main-thread queue from a Dobby hook on <c>EventSystem.Update</c>, for hosts where
/// an injected MonoBehaviour never gets <c>Update</c> (some Android loaders).
/// </summary>
internal static class NativeUpdateHook
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UpdateFn(IntPtr instance, IntPtr method);

    private static readonly UpdateFn Hook = Update;
    private static UpdateFn? _original;
    private static IntPtr _target;
    private static ManualLogSource? _log;

    [DllImport("dobby")]
    private static extern int DobbyHook(IntPtr address, IntPtr replacement, out IntPtr original);

    [DllImport("dobby")]
    private static extern int DobbyDestroy(IntPtr address);

    public static void Install(ManualLogSource log)
    {
        var klass = IL2CPP.GetIl2CppClass("UnityEngine.UI.dll", "UnityEngine.EventSystems", "EventSystem");
        var method = klass == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_class_get_method_from_name(klass, "Update", 0);
        if (method == IntPtr.Zero)
            throw new InvalidOperationException("EventSystem.Update not found");

        var target = Marshal.ReadIntPtr(method);
        var status = DobbyHook(target, Marshal.GetFunctionPointerForDelegate(Hook), out var original);
        if (status != 0)
            throw new InvalidOperationException($"DobbyHook(EventSystem.Update) returned {status}");

        _log = log;
        _target = target;
        _original = Marshal.GetDelegateForFunctionPointer<UpdateFn>(original);
    }

    public static void Uninstall()
    {
        if (_target != IntPtr.Zero && DobbyDestroy(_target) == 0)
            _target = IntPtr.Zero;
    }

    private static void Update(IntPtr instance, IntPtr method)
    {
        try
        {
            _original!(instance, method);
            MainThreadDispatcher.DrainAll();
        }
        catch (Exception ex)
        {
            _log?.LogError(ex);
        }
    }
}
