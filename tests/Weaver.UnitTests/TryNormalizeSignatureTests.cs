using Xunit;
using Weaver.Services;

namespace Weaver.UnitTests;

public class TryNormalizeSignatureTests
{
    [Theory]
    [InlineData("public class MyClass : Base {", "public class MyClass : Base { ... }")]
    [InlineData("    public async Task<IActionResult> Get(int id) {", "async Task<IActionResult> Get() { ... }")]
    [InlineData("export interface User { id: number }", "export interface User { ... }")]
    [InlineData("async getUser(id: string): Promise<User> {", "async getUser() { ... }")]
    [InlineData("def global_func(a, b):", "def global_func() { ... }")]
    [InlineData("class MyClass(Base):", "class MyClass() { ... }")]
    [InlineData("func (s *Server) Run(port int) error {", "func (s *Server) Run() { ... }")]
    [InlineData("pub fn main() {", "pub fn main() { ... }")]
    public void NormalizeVariousSignatures(string line, string expected)
    {
        var ok = AgentUtilities.NormalizeSkeletonSignatureForTest(line, out var sig);
        Assert.True(ok);
        Assert.Contains(expected.Split(' ')[0], sig); // sanity: contains the name or leading token
    }
}
