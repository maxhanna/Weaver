using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Weaver.Services;

public class BoardDataService
{
    private readonly string _filePath;
    private readonly ILogger<BoardDataService> _logger;

    public BoardDataService(string filePath, ILogger<BoardDataService> logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    public async Task SaveRawAsync(string json)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (directory != null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string backupPath = _filePath + ".bak";
        string tempPath = _filePath + ".tmp";

        try
        {
            // STEP 1: BACKUP
            // Only backup if the file currently exists.
            if (File.Exists(_filePath))
            {
                File.Copy(_filePath, backupPath, overwrite: true);
            }

            // STEP 2: WRITE TEMP
            // Write data to the temp file.
            await File.WriteAllTextAsync(tempPath, json);

            // STEP 3: COMMIT (SWAP OR MOVE)
            if (File.Exists(_filePath))
            {
                // SCENARIO A: File exists.
                // Use File.Replace for an atomic swap that preserves metadata.
                File.Replace(tempPath, _filePath, destinationBackupFileName: null);
            }
            else
            {
                // SCENARIO B: File does NOT exist (e.g., First Run).
                // File.Replace throws FileNotFoundException if the destination is missing.
                // Use File.Move instead (Atomic Rename) to create the new file.
                File.Move(tempPath, _filePath);
            }

            _logger.LogInformation("Board data saved successfully.");
        }
        catch (Exception ex)
        {
            // STEP 4: ROLLBACK IF NEEDED
            _logger.LogError(ex, "Save operation failed. Checking integrity...");

            try
            {
                // Check if the main file is corrupt (missing or empty)
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

            // Re-throw to let the Controller know it failed
            throw;
        }
        finally
        {
            // Cleanup temp file if it still exists (e.g. if Write succeeded but Move/Replace failed)
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* Ignore */ }
            }
        }
    }

    public async Task<string?> LoadRawAsync()
    {
        if (!File.Exists(_filePath)) return null;
        return await File.ReadAllTextAsync(_filePath);
    }
} 