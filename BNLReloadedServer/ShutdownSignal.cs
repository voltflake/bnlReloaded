namespace BNLReloadedServer;

public static class ShutdownSignal
{
    private static readonly TaskCompletionSource Tcs = new();

    public static Task WaitForShutdown => Tcs.Task;

    public static void Request()
    {
        Tcs.TrySetResult();
    }
}
