using IpcProbe;

// Runs the stub host on the user-scoped pipe (or --pipe <name>) until Ctrl+C. Logs go to stderr.
var pipe = args.Length >= 2 && args[0] == "--pipe" ? args[1] : PipeNames.For(Envelope.ProtocolVersion);
await using var host = TestHost.Start(pipe);
Console.Error.WriteLine($"IpcProbe test host listening on {pipe}");
using var quit = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
quit.Wait();
