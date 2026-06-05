using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Weaver.Services
{
    public class CalendarService
    {
        private readonly string _filePath;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        public CalendarService(string basePath)
        {
            if (string.IsNullOrEmpty(basePath)) basePath = Directory.GetCurrentDirectory();
            _filePath = Path.Combine(basePath, ".calendardata");
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
            if (json == null) json = "[]";
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
