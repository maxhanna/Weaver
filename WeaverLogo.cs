using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Weaver.Services
{
    public class WeaverLogo
    {
        private static readonly string[] LogoLines = {
            @" ___ ___ ___ ___ ___ ",
            @" / \ / \ / \ / \ / \ ",
            @" | o o | o o | o o | o o | o o | ",
            @" \___/ \___/ \___/ \___/ \___/ ",
            @" | | | | | | | | | | | | ",
            @" / \ / \ / \ / \ / \ / \ / \ / \ ",
            @" | | | | | | | | | | | | | ",
            @" \_/ \_/ \_/ \_/ \_/ \_/ \_/ \_/ ",
        };

        private static readonly string[] SubtitleLines = {
            @"           Weaver - The Ultimate Backend Solution",
            @"           ===================================",
            @"           A powerful backend framework for modern applications"
        };

        public static void DisplayLogo()
        {
            try
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
                Console.WriteLine("Starting Weaver backend service...");
                Console.WriteLine();
                Console.WriteLine("[INFO] Backend service initialized successfully.");
                Console.WriteLine();
                Console.WriteLine("[DEBUG] Logo displayed successfully.");
                Console.WriteLine("[TRACE] DisplayLogo method completed.");
                Console.WriteLine("[VERBOSE] All system components are operational.");
                Console.WriteLine();
                Console.WriteLine("[SUCCESS] Weaver logo displayed with enhanced styling.");
            }
            catch (IOException)
            {
                // Console is not available (e.g., when running as a service or when output is redirected)
                // Just skip the logo display in such cases
            }
        }

        public static async Task DisplayLogoAsync()
        {
            DisplayLogo();
            await Task.Delay(100);
        }
    }
}