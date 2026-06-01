using System.Reflection;
using MaestroBackend.Services;

namespace MaestroBackend.UnitTests;

public class TerminalParsingTests
{
    [Theory]
    [InlineData("git status", "git")]
    [InlineData("powershell -command dotnet build", "dotnet")]
    [InlineData("cmd /c npm install", "npm")]
    [InlineData("\"C:\\Program Files\\Git\\bin\\git.exe\" status", "git")]
    public void ExtractCommandRoot_returns_expected_root(string command, string expected)
    {
        var actual = TerminalService.ExtractCommandRoot(command);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("dotnet", "info : Package 'MailKit' with version '4.17.0' was resolved.", true)]
    [InlineData("npm", "added 1 package, and audited 2 packages", true)]
    [InlineData("npm", "npm ERR! code E404", false)]
    [InlineData("pip", "Successfully installed requests-2.32.0", true)]
    public void DetectInstallSuccess_recognizes_manager_output(string manager, string output, bool expected)
    {
        var actual = InvokePrivateStatic<bool>(
            typeof(TerminalController),
            "DetectInstallSuccess",
            manager,
            output);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("added 1 package\n+ left-pad@1.3.0", "npm", "1.3.0")]
    [InlineData("info : Package 'MailKit' with version '4.17.0' was resolved.", "dotnet", "4.17.0")]
    [InlineData("Successfully installed requests-2.32.0", "pip", "2.32.0")]
    public void ExtractResolvedVersion_returns_expected_version(string output, string manager, string expected)
    {
        var actual = InvokePrivateStatic<string?>(
            typeof(TerminalController),
            "ExtractResolvedVersion",
            output,
            manager);

        Assert.Equal(expected, actual);
    }

    private static T InvokePrivateStatic<T>(Type type, string methodName, params object?[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (T)method!.Invoke(null, args)!;
    }
}
