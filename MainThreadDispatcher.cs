using System.Collections.Concurrent;

using UnityEngine;

namespace Il2cppConsoleMod;

public static class MainThreadDispatcher
{
    private static readonly ConcurrentQueue<Action> Queue = new();

    public static void Enqueue(Action action)
    {
        Queue.Enqueue(action);
    }

    public static void DrainAll()
    {
        while (Queue.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Debug.LogError($"Il2cppConsoleMod dispatch error: {ex}");
            }
        }
    }
}
