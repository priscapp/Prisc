using System;

namespace Prisc
{
    /// <summary>
    /// The app's command-line surface. Explorer integrations (the Win11 shell extension, and
    /// the classic registry verb earlier builds registered) launch <c>Prisc.Ui.exe upload "path" ...</c>; bare paths —
    /// files dragged onto the exe, shortcuts from before the command existed — remain accepted
    /// as an implicit upload. The launch-origin flags (<c>--autostarted</c>, <c>--updated</c>) never
    /// reach here — Program strips them before anything else sees the command line. Deliberately
    /// tiny: grow a real parser when a second command shows up, not before.
    /// </summary>
    public static class CliArgs
    {
        public const string UploadCommand = "upload";

        /// <summary>The file paths an argument list asks to upload: the arguments after a
        /// leading "upload" command, or the whole list when it is legacy bare paths. Never
        /// null; an empty result means the arguments carried nothing to upload.</summary>
        public static string[] ExtractUploadPaths(string[] args)
        {
            if (args == null || args.Length == 0)
                return Array.Empty<string>();

            // a real path is always absolute here (Explorer hands verbs fully-qualified
            // paths), so a bare "upload" token can only be the command word
            if (String.Equals(args[0], UploadCommand, StringComparison.OrdinalIgnoreCase))
                return args[1..];

            return args;
        }

        /// <summary>The executable of a classic Explorer verb command line in the shape earlier
        /// builds registered, <c>"&lt;exe&gt;" upload "%1"</c> (or with the path unquoted), or null when
        /// <paramref name="command"/> is not such an upload verb. Used to recognise those verbs
        /// before removing them.</summary>
        public static string ParseUploadVerbExecutable(string command)
        {
            if (String.IsNullOrWhiteSpace(command))
                return null;

            command = command.Trim();
            string exe, rest;
            if (command[0] == '"')
            {
                var close = command.IndexOf('"', 1);
                if (close < 0)
                    return null;

                exe = command.Substring(1, close - 1);
                rest = command.Substring(close + 1);
            }
            else
            {
                var space = command.IndexOf(' ');
                if (space < 0)
                    return null;

                exe = command.Substring(0, space);
                rest = command.Substring(space);
            }

            var expected = UploadCommand + " \"%1\"";
            if (exe.Length == 0 || !String.Equals(rest.Trim(), expected, StringComparison.OrdinalIgnoreCase))
                return null;

            return exe;
        }
    }
}
