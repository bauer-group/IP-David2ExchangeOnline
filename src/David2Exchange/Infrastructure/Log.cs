// SPDX-License-Identifier: MIT
using System.Text;

namespace David2Exchange.Infrastructure;

/// <summary>
/// Textdatei-Log neben der EXE plus farbige Konsolenmeldungen.
/// Console.Out/Error werden zusätzlich in die Datei gespiegelt.
/// </summary>
public static class Log
{
    private static readonly object Sync = new();
    private static StreamWriter? _file;
    private static TextWriter? _originalOut;
    private static TextWriter? _originalError;

    public static string? LogFilePath { get; private set; }

    public static void Initialize(string? directory = null)
    {
        if (_file is not null)
        {
            return;
        }

        try
        {
            var logDirectory = directory ?? ToolPaths.DefaultLogDirectory;
            Directory.CreateDirectory(logDirectory);
            LogFilePath = Path.Combine(
                logDirectory,
                $"david2exchange-{DateTime.Now:yyyyMMdd}.log");

            _file = new StreamWriter(
                new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(false))
            {
                AutoFlush = true
            };

            _originalOut = Console.Out;
            _originalError = Console.Error;
            Console.SetOut(new TeeWriter(_originalOut, _file));
            Console.SetError(new TeeWriter(_originalError, _file));

            _file.WriteLine($"=== david2exchange {ToolInfo.Version} · {DateTime.Now:O} ===");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Logdatei konnte nicht geöffnet werden: {ex.Message}");
        }
    }

    public static void Info(string message) => Write(ConsoleColor.Gray, "INF", message);

    public static void Success(string message) => Write(ConsoleColor.Green, "OK ", message);

    public static void Warn(string message) => Write(ConsoleColor.Yellow, "WRN", message);

    public static void Error(string message) => Write(ConsoleColor.Red, "ERR", message);

    public static void Shutdown()
    {
        lock (Sync)
        {
            try
            {
                _file?.Flush();
                _file?.Dispose();
            }
            catch (Exception)
            {
                // Beim Beenden nicht kritisch.
            }

            _file = null;
            if (_originalOut is not null)
            {
                Console.SetOut(_originalOut);
            }

            if (_originalError is not null)
            {
                Console.SetError(_originalError);
            }
        }
    }

    private static void Write(ConsoleColor color, string level, string message)
    {
        lock (Sync)
        {
            if (_originalOut is null)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {level} {message}");
                return;
            }

            var previous = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = color;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {level} {message}");
            }
            finally
            {
                Console.ForegroundColor = previous;
            }
        }
    }

    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _first;
        private readonly TextWriter _second;

        public TeeWriter(TextWriter first, TextWriter second)
        {
            _first = first;
            _second = second;
        }

        public override Encoding Encoding => _first.Encoding;

        public override void Write(char value)
        {
            _first.Write(value);
            _second.Write(value);
        }

        public override void Write(string? value)
        {
            _first.Write(value);
            _second.Write(value);
        }

        public override void WriteLine(string? value)
        {
            _first.WriteLine(value);
            _second.WriteLine(value);
        }

        public override void Flush()
        {
            _first.Flush();
            _second.Flush();
        }
    }
}
