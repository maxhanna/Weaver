using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Weaver.Services;

public class BoardDataService
{
    private readonly string _filePath;
    private readonly ILogger<BoardDataService> _logger;

    // A static lock ensures thread safety across all instances (Agent + HTTP Controller)
    private static readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1, 1);

    public BoardDataService(string filePath, ILogger<BoardDataService> logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    public async Task SaveRawAsync(string json, int maxRetries = 15, int baseDelayMs = 500)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (directory != null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string backupPath = _filePath + ".bak";
        string tempPath = _filePath + ".tmp";

        await _fileLock.WaitAsync();
        try
        {
            for (var attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    // STEP 1: BACKUP
                    if (File.Exists(_filePath))
                    {
                        File.Copy(_filePath, backupPath, overwrite: true);
                    }

                    // STEP 2: WRITE TEMP
                    await File.WriteAllTextAsync(tempPath, json);

                    // STEP 3: COMMIT (SWAP OR MOVE)
                    if (File.Exists(_filePath))
                    {
                        File.Replace(tempPath, _filePath, destinationBackupFileName: null);
                    }
                    else
                    {
                        File.Move(tempPath, _filePath);
                    }

                    return; // Success
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SaveRawAsync attempt {Attempt}/{MaxRetries} failed.", attempt, maxRetries);

                    // STEP 4: ROLLBACK IF NEEDED
                    try
                    {
                        bool mainFileIsCorrupt = !File.Exists(_filePath) || new FileInfo(_filePath).Length == 0;
                        if (mainFileIsCorrupt)
                        {
                            if (File.Exists(backupPath))
                            {
                                _logger.LogWarning("Main file corrupted. Restoring from backup...");
                                File.Copy(backupPath, _filePath, overwrite: true);
                            }
                            else
                            {
                                _logger.LogCritical("Save failed AND no backup exists.");
                            }
                        }
                    }
                    catch (Exception rollbackEx)
                    {
                        _logger.LogCritical(rollbackEx, "Catastrophic failure during rollback.");
                    }

                    if (attempt >= maxRetries)
                    {
                        _logger.LogCritical("SaveRawAsync failed after {MaxRetries} attempts. Throwing exception.", maxRetries);
                        throw;
                    }

                    var delay = baseDelayMs * (1 << (attempt - 1));
                    await Task.Delay(delay);
                }
                finally
                {
                    if (File.Exists(tempPath))
                    {
                        try { File.Delete(tempPath); } catch { /* Ignore */ }
                    }
                }
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<string?> LoadRawAsync(int maxRetries = 5, int baseDelayMs = 200)
    {
        if (!File.Exists(_filePath)) return null;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await File.ReadAllTextAsync(_filePath);
            }
            catch (IOException) when (attempt < maxRetries)
            {
                var delay = baseDelayMs * (1 << (attempt - 1));
                _logger.LogWarning("LoadRawAsync attempt {Attempt}/{MaxRetries} failed (file locked), retrying in {Delay}ms",
                    attempt, maxRetries, delay);
                await Task.Delay(delay);
            }
        }

        // Last attempt — let it throw
        return await File.ReadAllTextAsync(_filePath);
    }
}