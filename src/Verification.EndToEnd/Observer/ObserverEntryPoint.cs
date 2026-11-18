using System.Runtime.CompilerServices;

namespace NativeWeave.Verification.EndToEnd;

/// <summary>Runs the exact application body on a joined thread before observing post-Main ownership.</summary>
/// <remarks>The copied application's only source edit changes Main accessibility. Moving the
/// entrypoint prevents an already completed application Task or entry frame from being mistaken
/// for a persistent framework root at ProcessExit. This is a verification artifact only.</remarks>
internal static class ObserverEntryPoint
{
    private static int Main(string[] args)
    {
        int result = 1;
        var application = new Thread(() => result = InvokeApplication(args)) { Name = "B22 application lifetime" };
        application.Start();
        application.Join();
        ReferenceObserver.AfterApplicationMain();
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int InvokeApplication(string[] args) =>
        NativeWeave.Reference.TaskService.Program.Main(args).GetAwaiter().GetResult();
}
