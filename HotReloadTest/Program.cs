using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.EditAndContinue;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Debugger.Contracts.EditAndContinue;

#pragma warning disable CA2007 // Consider calling ConfigureAwait on the awaited task

namespace HotReloadTest
{
    class Program
    {
        private static readonly SolutionActiveStatementSpanProvider noActiveSpans =
            (_, _) => new(ImmutableArray<TextSpan>.Empty);

        private static WebSocket? webSocket;
        private static readonly TaskCompletionSource tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        static async Task<int> Main(string[] args)
        {
            if (args.Length == 2)
            {
                if (args[0] != "--debug")
                {
                    Console.WriteLine("Usage --debug <path to project>");
                    return 1;
                }

                Console.WriteLine($"Waiting to attach to {Environment.ProcessId}.");
                Console.ReadKey();
                args = args.Skip(1).ToArray();
            }

            if (args.Length != 1)
            {
                Console.WriteLine("Usage <path to project>");
                return 1;
            }

            var projectDirectory = args[0];
            var projectPath = Directory.EnumerateFiles(projectDirectory, "*.csproj").First();

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "dotnet",
                ArgumentList = { "msbuild", "/t:Build", "/nologo", "/v:M" },
                WorkingDirectory = projectDirectory,
            })!;
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"dotnet build failed with {process.ExitCode}");
            }

            _ = Task.Run(async () =>
            {
                using var httpListener = new HttpListener();
                httpListener.Prefixes.Add("http://localhost:8080/");
                httpListener.Start();
                Console.WriteLine("Waiting for client to connect.");
                var context = await httpListener.GetContextAsync();
                if (context.Request.IsWebSocketRequest)
                {
                    var webSocketContext = await context.AcceptWebSocketAsync(null!);
                    Console.WriteLine("WebSocket client connected.");
                    webSocket = webSocketContext.WebSocket;
                }

                await tcs.Task;
            });

            var (workspace, editAndContinue) = await CreateMSBuildProjectAsync(projectPath);
            var output = workspace.CurrentSolution.Projects.First().OutputFilePath;
            using var p2 = Process.Start(new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "run --no-build",
                WorkingDirectory = projectDirectory,
                Environment = { ["ComPLUS_ForceENC"] = "1" },
            })!;

            try
            {
                var fsw = new FileSystemWatcher(projectDirectory, "*.cs");
                var debounceTimespan = TimeSpan.FromMilliseconds(100);
                var debouncedHandler = DebounceAndSerialize<string>(debounceTimespan, file => ChangeAsync(workspace, editAndContinue, file));
                fsw.Changed += (sender, eventArgs) => { if (File.Exists(eventArgs.FullPath)) debouncedHandler(eventArgs.FullPath); };
                fsw.EnableRaisingEvents = true;

                Console.WriteLine("Waiting for changes...");
                await p2.WaitForExitAsync();
            }
            finally
            {
                p2.Kill();
            }


            tcs.TrySetResult();

            return 1;
        }

        private static Action<T0> DebounceAndSerialize<T0>(TimeSpan duration, Func<T0, Task> action)
        {
            var semaphore = new SemaphoreSlim(1, 1);
            long callCount = 0;

#pragma warning disable VSTHRD101 // Avoid unsupported async delegates
            return async (arg) =>
            {
                // Our goal is to figure out if we're the last call within a cluster, *and*
                // to prevent overlapping inner calls.
                // To do that, snapshot the call count at the start, wait until there's no
                // other call in progress and some delay, and if the call count remains
                // unchanged then we know there's no newer call waiting.
                var callCountAtStart = Interlocked.Increment(ref callCount);
                try
                {
                    await Task.WhenAll(semaphore.WaitAsync(), Task.Delay(duration));
                    var callCountNow = Interlocked.Read(ref callCount);
                    if (callCountNow == callCountAtStart)
                    {
                        await action(arg);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            };
#pragma warning restore VSTHRD101 // Avoid unsupported async delegates
        }

        private static async Task ChangeAsync(MSBuildWorkspace workspace, IEditAndContinueWorkspaceService editAndContinue, string filePath)
        {
            Console.WriteLine($"Detected changes to {filePath}.");
            var document = workspace.CurrentSolution.Projects.Single().Documents.Single(f => f.FilePath == filePath);

            editAndContinue.StartEditSession(new FakeManagedEditAndContinueDebuggerService(), out _);

            var stream = File.OpenRead(filePath);
            var updated = document.WithText(SourceText.From(stream, Encoding.UTF8));
            stream.Close();

            if (!workspace.TryApplyChanges(updated.Project.Solution))
            {
                throw new InvalidOperationException("Apply changes failed.");
            }

            var (updates, diagnostics) = await editAndContinue.EmitSolutionUpdateAsync(
                workspace.CurrentSolution,
                noActiveSpans,
                default);

            if (updates.Status == ManagedModuleUpdateStatus.None)
            {
                return;
            }

            if (updates.Status == ManagedModuleUpdateStatus.Blocked)
            {
                throw new InvalidOperationException($"UpdateStatus is {updates.Status}");
            }

            var json = JsonSerializer.SerializeToUtf8Bytes(updates.Updates.Select(c => new Delta
            {
                ILDelta = c.ILDelta.ToArray(),
                MetadataDelta = c.MetadataDelta.ToArray(),
            }).ToArray());

            if (webSocket != null)
            {
                await webSocket.SendAsync(json, WebSocketMessageType.Binary, true, default);
            }

            editAndContinue.CommitSolutionUpdate();
            editAndContinue.EndEditSession(out _);

            Console.WriteLine("Successfully produced a deltas");
        }

        private static async Task<(MSBuildWorkspace, IEditAndContinueWorkspaceService)> CreateMSBuildProjectAsync(string projectPath)
        {
            var vs = MSBuildLocator.RegisterDefaults();
            Console.WriteLine($"Using MSBuild from {vs.MSBuildPath}.");

            Console.WriteLine($"Opening project at {projectPath}...");
            var msw = MSBuildWorkspace.Create();
            msw.SuppressSaveEditsToDisk = true;

            msw.WorkspaceFailed += (_sender, diag) =>
            {
                var warning = diag.Diagnostic.Kind == WorkspaceDiagnosticKind.Warning;
                if (!warning)
                {
                    Console.WriteLine($"msbuild failed opening project {projectPath}");
                }

                Console.WriteLine($"MSBuildWorkspace {diag.Diagnostic.Kind}: {diag.Diagnostic.Message}");

                if (!warning)
                {
                    throw new InvalidOperationException("failed workspace.");
                }
            };

            var enc = msw.Services.GetRequiredService<IEditAndContinueWorkspaceService>();
            var project = await msw.OpenProjectAsync(projectPath);
            enc.StartDebuggingSession(msw.CurrentSolution);

            foreach (var document in project.Documents)
            {
                await document.GetTextAsync();
                await enc.OnSourceFileUpdatedAsync(document);
            }
            return (msw, enc);
        }

        private class FakeManagedEditAndContinueDebuggerService : IManagedEditAndContinueDebuggerService
        {
            public Task<ImmutableArray<ManagedActiveStatementDebugInfo>> GetActiveStatementsAsync(CancellationToken cancellationToken)
                => Task.FromResult(ImmutableArray<ManagedActiveStatementDebugInfo>.Empty);

            public Task<ManagedEditAndContinueAvailability> GetAvailabilityAsync(Guid moduleVersionId, CancellationToken cancellationToken)
            {
                return Task.FromResult(new ManagedEditAndContinueAvailability(ManagedEditAndContinueAvailabilityStatus.Available));
            }

            public Task PrepareModuleForUpdateAsync(Guid moduleVersionId, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }
        }

        private readonly struct Delta
        {
            public byte[] ILDelta { get; init; }
            public byte[] MetadataDelta { get; init; }
        }
    }
}
#pragma warning restore CA2007 // Consider calling ConfigureAwait on the awaited task
