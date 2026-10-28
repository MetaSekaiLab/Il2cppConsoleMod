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

    /// <summary>
    /// Runs <paramref name="func"/> on the Unity main thread and waits for it. Exceptions
    /// are rethrown on the calling thread; <see cref="TimeoutException"/> when the main
    /// thread does not get to it in time (loading screen, game paused in background...).
    /// </summary>
    public static T Run<T>(Func<T> func, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        if (!((IAsyncResult)tcs.Task).AsyncWaitHandle.WaitOne(timeout))
            throw new TimeoutException("timeout waiting for main thread");

        return tcs.Task.GetAwaiter().GetResult();
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
