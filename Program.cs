// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;
using WinRT;

namespace PigeonPost;

/// <summary>
/// Custom application entry point.
///
/// <para>
/// The SDK normally generates a <c>Program.cs</c> automatically; we suppress it with
/// <c>DISABLE_XAML_GENERATED_MAIN</c> so we can place <see cref="VelopackApp.Build().Run()"/>
/// as the very first call — a hard requirement of Velopack's update framework.
/// </para>
///
/// The rest of the method replicates what the generated main would do:
///   1. Initialise COM wrappers (required by WinRT interop).
///   2. Start the WinUI application loop with a <see cref="DispatcherQueueSynchronizationContext"/>
///      so <c>await</c> continuations run on the UI thread.
/// </summary>
public static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // ── Velopack bootstrap ────────────────────────────────────────────────
        // MUST be the very first call. Handles install/uninstall hooks, shortcut
        // creation, and passes control back here immediately during normal startup.
        VelopackApp.Build().Run();

        // ── WinUI 3 startup (mirrors the SDK-generated main) ─────────────────
        ComWrappersSupport.InitializeComWrappers();

        Application.Start(p =>
        {
            // Bind the WinUI dispatcher to the current thread so that async
            // continuations scheduled with await resume on the UI thread.
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);

            _ = new App();
        });
    }
}
