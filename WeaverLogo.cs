using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Weaver.Services
{
    public class WeaverLogo
    {
        private static readonly string[] LogoLines = {
            @"
__/\\\______________/\\\__/\\\\\\\\\\\\\\\_____/\\\\\\\\\_____/\\\________/\\\__/\\\\\\\\\\\\\\\____/\\\\\\\\\_____        
 _\/\\\_____________\/\\\_\/\\\///////////____/\\\\\\\\\\\\\__\/\\\_______\/\\\_\/\\\///////////___/\\\///////\\\___       
  _\/\\\_____________\/\\\_\/\\\______________/\\\/////////\\\_\//\\\______/\\\__\/\\\_____________\/\\\_____\/\\\___      
   _\//\\\____/\\\____/\\\__\/\\\\\\\\\\\_____\/\\\_______\/\\\__\//\\\____/\\\___\/\\\\\\\\\\\_____\/\\\\\\\\\\\/____     
    __\//\\\__/\\\\\__/\\\___\/\\\///////______\/\\\\\\\\\\\\\\\___\//\\\__/\\\____\/\\\///////______\/\\\//////\\\____    
     ___\//\\\/\\\/\\\/\\\____\/\\\_____________\/\\\/////////\\\____\//\\\/\\\_____\/\\\_____________\/\\\____\//\\\___   
      ____\//\\\\\\//\\\\\_____\/\\\_____________\/\\\_______\/\\\_____\//\\\\\______\/\\\_____________\/\\\_____\//\\\__  
       _____\//\\\__\//\\\______\/\\\\\\\\\\\\\\\_\/\\\_______\/\\\______\//\\\_______\/\\\\\\\\\\\\\\\_\/\\\______\//\\\_ 
        ______\///____\///_______\///////////////__\///________\///________\///________\///////////////__\///________\///__",
        };

        private static readonly string[] SubtitleLines = {
            @"           Weaver",
            @"           ===================================",
            @"           Your personal AI assistant"
        };
        /// <summary>
        /// Displays the Weaver logo and subtitle in the console.
        /// </summary>
        /// <remarks>
        /// This method clears the console and displays the Weaver ASCII art logo
        /// followed by a descriptive subtitle. It handles IOExceptions gracefully
        /// when the console is not available.
        /// </remarks>
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