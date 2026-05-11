// Copyright (c) 2026 Holger Imbery. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
#if !STORE_BUILD
using Velopack;
#endif
using WinRT;

namespace PigeonPost;

/// <summary>
/// Custom application entry point.
///
/// <para>
/// The SDK normally generates a <c>Program.cs</c> automatically; we suppress it with
/// <c>DISABLE_XAML_GENERATED_MAIN</c> so we can control startup for both build modes:
/// <list type="bullet">
///   <item><description>
///     <b>Winget/Velopack build:</b> <see cref="VelopackApp.Build().Run()"/> MUST be the
///     very first call — a hard requirement of Velopack's update framework.
///   </description></item>
///   <item><description>
///     <b>Store/MSIX build:</b> No Velopack; startup proceeds directly to WinUI.
///     StartupTask activation is detected in <c>App.OnLaunched</c>.
///   </description></item>
/// </list>
/// </para>
/// </summary>
public static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
#if !STORE_BUILD
        // ── Velopack bootstrap (Winget build only) ────────────────────────────
        // MUST be the very first call. Handles install/uninstall hooks, shortcut
        // creation, and passes control back here immediately during normal startup.
        VelopackApp.Build().Run();
#endif

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
