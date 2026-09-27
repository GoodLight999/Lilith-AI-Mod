using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace LilithVoiceHost;

internal static class Program
{
    private static readonly SemaphoreSlim LogLock = new(1, 1);

    private static async Task Main(string[] args)
    {
        var parentPid = 0;
        var parentIndex = Array.FindIndex(args, value => string.Equals(value, "--parent", StringComparison.OrdinalIgnoreCase));
        if (parentIndex >= 0 && parentIndex + 1 < args.Length)
            int.TryParse(args[parentIndex + 1], out parentPid);

        var language = ReadArgument(args, "--language")?.Trim().ToLowerInvariant() ?? "zh";
        var provider = ReadArgument(args, "--provider")?.Trim().ToLowerInvariant() ?? "gpt-sovits";

        using var mutex = new Mutex(true, "Local\\LilithAIVoiceHost", out var created);
        if (!created) return;

        var root = AppContext.BaseDirectory;
        var logDirectory = Path.Combine(root, "logs");
        Directory.CreateDirectory(logDirectory);
        var log = Path.Combine(logDirectory, "voice-host.log");
        var owned = new List<Process>();

        try
        {
            if (language == "ja" && provider == "irodori")
                await StartIrodoriAsync(root, log, owned, 9881);
            else
                await StartGptSoVitsAsync(root, log, owned, language == "ja" ? 9881 : 9880, language == "ja" ? "ja" : "zh");

            await LogAsync(log, $"Voice host started (language={language}, provider={provider}); owned processes={owned.Count}.");

            while (parentPid > 0)
            {
                try
                {
                    using var parent = Process.GetProcessById(parentPid);
                    if (parent.HasExited) break;
                }
                catch
                {
                    break;
                }

                await Task.Delay(2000);
            }
        }
        catch (Exception exception)
        {
            await LogAsync(log, exception.ToString());
        }
        finally
        {
            foreach (var process in owned)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(true);
                }
                catch
                {
                    // Best effort during game shutdown.
                }

                process.Dispose();
            }

            await LogAsync(log, "Voice host stopped.");
        }
    }

    private static string? ReadArgument(string[] args, string name)
    {
        var index = Array.FindIndex(args, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static async Task StartGptSoVitsAsync(string root, string log, List<Process> owned, int port, string language)
    {
        if (await PortOpenAsync(port, 300))
        {
            await LogAsync(log, $"GPT-SoVITS endpoint on port {port} is already available; using the existing service.");
            return;
        }

        var python = Path.Combine(root, "python", "Scripts", "python.exe");
        var api = Path.Combine(root, "gpt-sovits", "api_v2.py");
        if (!File.Exists(python) || !File.Exists(api) || !File.Exists(Path.Combine(root, ".ready")))
        {
            await LogAsync(log, $"GPT-SoVITS runtime '{language}' is not ready.");
            return;
        }

        var device = File.Exists(Path.Combine(root, "device.txt"))
            ? File.ReadAllText(Path.Combine(root, "device.txt")).Trim().ToLowerInvariant()
            : HasNvidiaGpu() ? "cuda" : "cpu";
        var config = Path.Combine(root, "config", $"{language}-{device}.yaml");
        if (!File.Exists(config))
            throw new FileNotFoundException("Voice configuration is missing.", config);

        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            Arguments = $"\"{api}\" -a 127.0.0.1 -p {port} -c \"{config}\"",
            WorkingDirectory = Path.Combine(root, "gpt-sovits"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start the GPT-SoVITS service '{language}'.");
        process.OutputDataReceived += async (_, e) =>
        {
            if (e.Data != null) await LogAsync(log, $"[{language}] {e.Data}");
        };
        process.ErrorDataReceived += async (_, e) =>
        {
            if (e.Data != null) await LogAsync(log, $"[{language}:err] {e.Data}");
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        owned.Add(process);
    }

    private static async Task StartIrodoriAsync(string root, string log, List<Process> owned, int port)
    {
        if (await PortOpenAsync(port, 300))
        {
            if (await IrodoriHealthyAsync(port))
            {
                await LogAsync(log, $"Irodori endpoint on port {port} is already healthy; using the existing service.");
                return;
            }

            throw new InvalidOperationException(
                $"Port {port} is already occupied, but the service did not answer as Irodori-TTS. Stop the conflicting process or change the configured endpoint.");
        }

        var serverRoot = Path.Combine(root, "Irodori-TTS-Server");
        var python = Path.Combine(serverRoot, ".venv", "Scripts", "python.exe");
        if (!File.Exists(python))
        {
            await LogAsync(log, "Irodori runtime is not ready. Expected Irodori-TTS-Server/.venv/Scripts/python.exe.");
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            Arguments = $"-m irodori_openai_tts --host 127.0.0.1 --port {port}",
            WorkingDirectory = serverRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the Irodori TTS service.");
        process.OutputDataReceived += async (_, e) =>
        {
            if (e.Data != null) await LogAsync(log, $"[irodori] {e.Data}");
        };
        process.ErrorDataReceived += async (_, e) =>
        {
            if (e.Data != null) await LogAsync(log, $"[irodori:err] {e.Data}");
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        owned.Add(process);
    }

    private static async Task<bool> IrodoriHealthyAsync(int port)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasNvidiaGpu()
    {
        foreach (var candidate in new[]
                 {
                     "nvidia-smi.exe",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
                 })
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(candidate, "-L")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                if (process != null
                    && process.WaitForExit(3000)
                    && process.ExitCode == 0
                    && process.StandardOutput.ReadToEnd().Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                // Fall back to CPU if detection fails.
            }
        }

        return false;
    }

    private static async Task<bool> PortOpenAsync(int port, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync("127.0.0.1", port, timeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task LogAsync(string path, string message)
    {
        await LogLock.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(
                path,
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        finally
        {
            LogLock.Release();
        }
    }
}
