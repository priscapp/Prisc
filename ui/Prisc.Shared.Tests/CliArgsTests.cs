using System;
using Xunit;

namespace Prisc.Shared.Tests
{
    public class CliArgsTests
    {
        [Fact]
        public void UploadCommand_IsStripped()
        {
            var paths = CliArgs.ExtractUploadPaths(new[] { "upload", @"C:\a.txt", @"C:\b.txt" });
            Assert.Equal(new[] { @"C:\a.txt", @"C:\b.txt" }, paths);
        }

        [Fact]
        public void UploadCommand_IsCaseInsensitive()
        {
            var paths = CliArgs.ExtractUploadPaths(new[] { "Upload", @"C:\a.txt" });
            Assert.Equal(new[] { @"C:\a.txt" }, paths);
        }

        [Fact]
        public void UploadCommand_Alone_YieldsNothing()
        {
            Assert.Empty(CliArgs.ExtractUploadPaths(new[] { "upload" }));
        }

        [Fact]
        public void LegacyBarePaths_PassThrough()
        {
            // files dragged onto the exe and pre-command shortcuts send raw paths
            var args = new[] { @"C:\a.txt", @"C:\b.txt" };
            Assert.Equal(args, CliArgs.ExtractUploadPaths(args));
        }

        [Fact]
        public void PathNamedUpload_IsNotMistakenForTheCommand()
        {
            // real paths are always fully qualified, so only a bare token is the command
            var args = new[] { @"C:\files\upload" };
            Assert.Equal(args, CliArgs.ExtractUploadPaths(args));
        }

        [Fact]
        public void NullOrEmpty_YieldsNothing()
        {
            Assert.Empty(CliArgs.ExtractUploadPaths(null));
            Assert.Empty(CliArgs.ExtractUploadPaths(Array.Empty<string>()));
        }

        [Fact]
        public void UploadVerbCommand_YieldsTheQuotedExecutable()
        {
            // the shape earlier builds registered; profile paths routinely contain spaces
            var exe = @"C:\Users\Some One\AppData\Local\Prisc\current\Prisc.Ui.exe";
            Assert.Equal(exe, CliArgs.ParseUploadVerbExecutable("\"" + exe + "\" upload \"%1\""));
        }

        [Fact]
        public void UploadVerbCommand_AcceptsAnUnquotedPath()
        {
            Assert.Equal(@"C:\Prisc\Prisc.Ui.exe", CliArgs.ParseUploadVerbExecutable(@"C:\Prisc\Prisc.Ui.exe upload ""%1"""));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("\"C:\\Prisc\\Prisc.Ui.exe\"")]
        [InlineData("\"C:\\Prisc\\Prisc.Ui.exe\" \"%1\"")]
        [InlineData("\"C:\\Prisc\\Prisc.Ui.exe\" upload \"%1\" --other")]
        [InlineData("\"C:\\Prisc\\Prisc.Ui.exe upload \"%1\"")]
        [InlineData("\"\" upload \"%1\"")]
        public void UploadVerbCommand_RejectsOtherCommands(string command)
        {
            Assert.Null(CliArgs.ParseUploadVerbExecutable(command));
        }
    }
}
