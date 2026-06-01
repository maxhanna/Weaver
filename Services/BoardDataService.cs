using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MaestroBackend.Services
{
    public class BoardDataService
    {
        private readonly string _filePath;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        public BoardDataService(string basePath)
        {
            if (string.IsNullOrEmpty(basePath)) basePath = Directory.GetCurrentDirectory();
            _filePath = Path.Combine(basePath, ".boarddata");
        }

        public async Task<string?> LoadRawAsync()
        {
            try
            {
                if (!File.Exists(_filePath)) return null;
                using var fs = File.Open(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var sr = new StreamReader(fs);
                return await sr.ReadToEndAsync();
            }
            catch
            {
                return null;
            }
        }

        public async Task SaveRawAsync(string json)
        {
            if (json == null) json = string.Empty;
            await _lock.WaitAsync();
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                using var fs = File.Open(_filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                using var sw = new StreamWriter(fs);
                await sw.WriteAsync(json);
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}
