using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace BitKraken.Services;

/// <summary>
/// Keeps one BitKraken per user. Clicking a magnet link in a browser launches a *new* process on
/// Windows and Linux, so later launches hand their arguments to the instance that is already
/// running (and then exit) instead of starting a second engine on the same listen port.
/// macOS routes both launches and <c>magnet:</c> links to the running app itself, so there the
/// handshake only matters when the binary is started outside an app bundle.
/// </summary>
public static class SingleInstance
{
    /// <summary>How long to wait for the running instance to accept us once we believe it is there.</summary>
    private const int ConnectTimeoutMs = 750;

    /// <summary>Waiting on a pipe that doesn't exist costs the full timeout, so cold starts get a token wait.</summary>
    private const int ProbeTimeoutMs = 120;

    private static readonly string PipeName = $"BitKraken.{UserToken()}";

    /// <summary>Where System.IO.Pipes puts the socket that backs a named pipe on Unix.</summary>
    private static readonly string PipeSocketPath = Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + PipeName);

    private static CancellationTokenSource? _cts;
    private static NamedPipeServerStream? _server;

    /// <summary>
    /// Tries to give <paramref name="args"/> to an already running instance.
    /// Returns true when it was accepted, meaning this process should quit without showing a window.
    /// </summary>
    public static bool TryHandOff(IReadOnlyList<string> args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(PipeLooksAlive() ? ConnectTimeoutMs : ProbeTimeoutMs);

            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            foreach (var arg in args)
                writer.WriteLine(arg.ReplaceLineEndings(" "));
            writer.Flush();
            return true;
        }
        catch
        {
            // Nobody is listening (or the pipe is stale): we are the first instance.
            return false;
        }
    }

    /// <summary>
    /// Starts listening for later launches. <paramref name="onArguments"/> is raised on a background
    /// thread with whatever that launch was given - possibly nothing, which just means "come to the front".
    /// </summary>
    public static void Listen(Action<IReadOnlyList<string>> onArguments)
    {
        if (_cts is not null) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => ListenLoopAsync(onArguments, token), token);
    }

    public static void Stop()
    {
        _cts?.Cancel();
        _cts = null;

        // Tear the pipe down rather than leave a dead socket behind: the next start would otherwise
        // wait out the connect timeout on it before deciding it is on its own.
        try { _server?.Dispose(); } catch { /* already gone */ }
        _server = null;
        DeleteSocketFile();
    }

    private static async Task ListenLoopAsync(Action<IReadOnlyList<string>> onArguments, CancellationToken token)
    {
        NamedPipeServerStream? server = null;
        try
        {
            server = CreateServer();
            _server = server;

            while (!token.IsCancellationRequested)
            {
                await server.WaitForConnectionAsync(token);
                try
                {
                    var args = new List<string>();
                    using (var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, leaveOpen: true))
                    {
                        while (await reader.ReadLineAsync(token) is { } line)
                            if (line.Length > 0) args.Add(line);
                    }

                    onArguments(args);
                }
                finally
                {
                    server.Disconnect();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch
        {
            // Losing the handshake is not fatal - later launches just open their own window.
        }
        finally
        {
            server?.Dispose();
        }
    }

    private static NamedPipeServerStream CreateServer()
    {
        try
        {
            return NewServer();
        }
        catch
        {
            // On Unix the pipe is a socket file in the temp directory, and a crashed instance can
            // leave it behind. We only get here after failing to connect to it, so it is stale.
            DeleteSocketFile();
            return NewServer();
        }
    }

    private static NamedPipeServerStream NewServer() => new(
        PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    /// <summary>
    /// Best-effort "is anyone home" check. Only used to decide how long to wait: a false negative
    /// costs a short connect attempt, never a missed hand-off.
    /// </summary>
    private static bool PipeLooksAlive()
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? Directory.EnumerateFiles(@"\\.\pipe\").Any(p => p.EndsWith(PipeName, StringComparison.Ordinal))
                : File.Exists(PipeSocketPath);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Removes the Unix socket file behind the pipe. A no-op on Windows, where pipes are not files.</summary>
    private static void DeleteSocketFile()
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            if (File.Exists(PipeSocketPath)) File.Delete(PipeSocketPath);
        }
        catch
        {
            // Nothing we can do; CreateServer will surface the original failure.
        }
    }

    /// <summary>Short per-user suffix so two users on one machine don't share a pipe.</summary>
    private static string UserToken()
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName));
        return Convert.ToHexString(hash.AsSpan(0, 4));
    }
}
