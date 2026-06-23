using Xunit;
using System;
using Weaver.Services;

namespace MaestroBackend.UnitTests;

public class DebugSkeletonTests
{
        [Fact]
        public void DumpSkeletons()
        {
            var csharp = new[]
            {
                "    public class MyClass {",
                "        private int _field;",
                "        [HttpGet]",
                "        public async Task<IActionResult> GetData(int id) {",
                "            return Ok();",
                "        }",
                "    }"
            };
            var s1 = AgentUtilities.GetSkeletonForRange(csharp, 0, csharp.Length);
            Console.WriteLine("--- C# Skeleton ---");
            Console.WriteLine(s1);

            var go = new[]
            {
                "func (s *Server) Run(port int) error {",
                "    return nil",
                "}",
                "pub fn main() {",
                "    println!(\"hello\");",
                "}"
            };
            var s2 = AgentUtilities.GetSkeletonForRange(go, 0, go.Length);
            Console.WriteLine("--- Go/Rust Skeleton ---");
            Console.WriteLine(s2);

            Assert.True(true);
        }
    }
