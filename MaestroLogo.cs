using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace MaestroBackend.Services
{
    public class MaestroLogo
    {
        private static readonly string[] LogoLines = {
            @"  __  __           _      _     _           ",
            @" |  \/  |         | |    (_)   | |          ",
            @" | \  / | ___  ___| |     _ ___| |_ ___ _ __ ",
            @" | |\/| |/ _ \/ __| |    | / __| __/ _ \ '__|",
            @" | |  | |  __/\__ \ |____| \__ \ ||  __/ |   ",
            @" |_|  |_|\___||___/______|_|___/\__\___|_|   ",
            @"                                             ",
            @"           _   _   _   _   _   _   _         ",
            @"          / \ / \ / \ / \ / \ / \ / \        ",
            @"         ( M | a | e | s | t | r | o )       ",
            @"          \_/ \_/ \_/ \_/ \_/ \_/ \_/        ",
            @"                                             ",
            @"         Enhanced Maestro Logo v2.0        "
        };

        private static readonly string[] SubtitleLines = {
            @"           Maestro - The Ultimate Backend Solution",
            @"           ===================================",
            @"           A powerful backend framework for modern applications"
        };

        public static void DisplayLogo()
        {
            Console.Clear();
            
            // Display the main logo
            foreach (string line in LogoLines)
            {
                Console.WriteLine(line);
            }
            
            Console.WriteLine();
            
            // Display the subtitle
            foreach (string line in SubtitleLines)
            {
                Console.WriteLine(line);
            }
            
            Console.WriteLine();
            Console.WriteLine("Starting Maestro backend service...");
            Console.WriteLine();
            Console.WriteLine("[INFO] Backend service initialized successfully.");
            Console.WriteLine();
            Console.WriteLine("[DEBUG] Logo displayed successfully.");
            Console.WriteLine("[TRACE] DisplayLogo method completed.");
            Console.WriteLine("[VERBOSE] All system components are operational.");
            Console.WriteLine();
            Console.WriteLine("[SUCCESS] Maestro logo displayed with enhanced styling.");
        }

        public static async Task DisplayLogoAsync()
        {
            DisplayLogo();
            await Task.Delay(100);
        }
    }
}