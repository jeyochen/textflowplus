using System;
using System.Reflection;
using System.IO;

namespace TextFlow
{
    public class TestResource
    {
        public static void Test()
        {
            var assembly = Assembly.GetExecutingAssembly();
            Console.WriteLine("Assembly: " + assembly.FullName);
            
            // 列出所有资源
            Console.WriteLine("\nAll resources:");
            foreach (var resourceName in assembly.GetManifestResourceNames())
            {
                Console.WriteLine("- " + resourceName);
            }
            
            // 检查 JSON 资源
            string jsonResourceName = "TextFlow.Highlighting.JSON-Mode.xshd";
            Console.WriteLine($"\nChecking for JSON resource: {jsonResourceName}");
            using (Stream? stream = assembly.GetManifestResourceStream(jsonResourceName))
            {
                if (stream != null)
                {
                    Console.WriteLine("JSON resource found!");
                    using (var reader = new StreamReader(stream))
                    {
                        string content = reader.ReadToEnd();
                        Console.WriteLine("Content length: " + content.Length);
                        Console.WriteLine("First 500 characters: " + content.Substring(0, Math.Min(500, content.Length)));
                    }
                }
                else
                {
                    Console.WriteLine("JSON resource NOT found!");
                }
            }
        }
    }
}