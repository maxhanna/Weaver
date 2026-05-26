#r "nuget: Jint, 4.1.0"

using System;
using System.IO;
using Jint;

// 1. Gather all files matching the glob pattern
string searchFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
if (!Directory.Exists(searchFolder))
{
    Console.WriteLine($"Directory not found: {searchFolder}");
    return 0; 
}

string[] jsFiles = Directory.GetFiles(searchFolder, "*.js", SearchOption.AllDirectories);
bool hasErrors = false;
var engine = new Engine();

Console.WriteLine($"Starting pure C# JavaScript linting pass on {jsFiles.Length} files...");

foreach (var filePath in jsFiles)
{
    try
    {
        string jsContent = File.ReadAllText(filePath);
        
        // Native C# interpreter parsing pass
        engine.Modules.Add("temp_module", jsContent);
    }
    catch (Exception ex)
    {
        // Formats errors cleanly so they display natively in the IDE build output console
        Console.Error.WriteLine($"{filePath}(1,1): error JS0001: JavaScript Lint/Syntax Error: {ex.Message}");
        hasErrors = true;
    }
}

// Exit code 1 halts 'dotnet build' if any JavaScript files are broken
return hasErrors ? 1 : 0;
