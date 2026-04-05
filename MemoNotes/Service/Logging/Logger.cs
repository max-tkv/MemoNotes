using System;
using System.IO;

namespace MemoNotes.Service.Logging;

/// <summary>
/// Статический логгер с записью в файлы в папке logs/.
/// Файлы ротируются по дате: logs/app_2026-03-27.log
/// </summary>
public static class Logger
{
    private static readonly string LogsDirectory = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "logs");

    private static readonly object LockObj = new();
    private static LogLevel _minLevel = LogLevel.Debug;

    /// <summary>
    /// Минимальный уровень логирования. По умолчанию — Debug.
    /// </summary>
    public static LogLevel MinLevel
    {
        get => _minLevel;
        set => _minLevel = value;
    }

    /// <summary>
    /// Инициализация логгера: создаёт папку logs/, если её нет.
    /// </summary>
    static Logger()
    {
        try
        {
            if (!Directory.Exists(LogsDirectory))
            {
                Directory.CreateDirectory(LogsDirectory);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Logger] Ошибка создания папки logs/: {ex.Message}");
        }
    }

    #region Public API

    public static void Debug<T>(string message) where T : class
        => Log(LogLevel.Debug, typeof(T).Name, message);

    public static void Info<T>(string message) where T : class
        => Log(LogLevel.Info, typeof(T).Name, message);

    public static void Warn<T>(string message) where T : class
        => Log(LogLevel.Warn, typeof(T).Name, message);

    public static void Error<T>(string message, Exception? ex = null) where T : class
        => Log(LogLevel.Error, typeof(T).Name, ex != null ? $"{message} | Exception: {ex}" : message);

    public static void Error<T>(Exception ex) where T : class
        => Log(LogLevel.Error, typeof(T).Name, $"Exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    /// <summary>Негенерические перегрузки для использования в статических классах.</summary>
    public static void Debug(string source, string message)
        => Log(LogLevel.Debug, source, message);

    public static void Info(string source, string message)
        => Log(LogLevel.Info, source, message);

    public static void Warn(string source, string message)
        => Log(LogLevel.Warn, source, message);

    public static void Error(string source, string message, Exception? ex = null)
        => Log(LogLevel.Error, source, ex != null ? $"{message} | Exception: {ex}" : message);

    public static void Error(string source, Exception ex)
        => Log(LogLevel.Error, source, $"Exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    #endregion

    #region Core

    private static void Log(LogLevel level, string source, string message)
    {
        if (level < _minLevel)
            return;

        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var levelStr = GetLevelString(level);
        var logLine = $"[{timestamp}] [{levelStr}] [{source}] {message}";

        // Вывод в Debug (отображается в Output VS)
        System.Diagnostics.Debug.WriteLine(logLine);

        // Запись в файл
        WriteToFile(logLine);
    }

    private static void WriteToFile(string logLine)
    {
        lock (LockObj)
        {
            try
            {
                var fileName = $"app_{DateTime.Now:yyyy-MM-dd}.log";
                var filePath = Path.Combine(LogsDirectory, fileName);
                File.AppendAllText(filePath, logLine + Environment.NewLine);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Logger] Ошибка записи в файл: {ex.Message}");
            }
        }
    }

    private static string GetLevelString(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info  => "INFO ",
        LogLevel.Warn  => "WARN ",
        LogLevel.Error => "ERROR",
        _              => "?????"
    };

    #endregion

    /// <summary>
    /// Очистка старых файлов логов (старше указанного количества дней).
    /// </summary>
    public static void CleanOldLogs(int daysToKeep = 30)
    {
        try
        {
            if (!Directory.Exists(LogsDirectory))
                return;

            var cutoff = DateTime.Now.AddDays(-daysToKeep);
            var files = Directory.GetFiles(LogsDirectory, "app_*.log");

            foreach (var file in files)
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                    System.Diagnostics.Debug.WriteLine($"[Logger] Удалён старый лог: {Path.GetFileName(file)}");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Logger] Ошибка очистки логов: {ex.Message}");
        }
    }
}

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3
}
