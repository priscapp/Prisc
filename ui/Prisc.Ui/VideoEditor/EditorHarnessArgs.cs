using System;
using System.IO;

namespace Prisc.UI.VideoEditor
{
    /// <summary>The command-line reading behind the video editor's hidden harness flags, apart from
    /// the window so it can be tested without one.</summary>
    internal static class EditorHarnessArgs
    {
        /// <summary>The argument after <paramref name="name"/>, or null when the flag is absent or
        /// nothing follows it. The flag must match whole: <c>--video-edit</c> does not answer to
        /// <c>--video-edit-session</c>.</summary>
        public static string ValueOf(string[] args, string name)
        {
            if (args == null)
                return null;

            for (var i = 0; i + 1 < args.Length; i++)
            {
                if (String.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }

            return null;
        }

        /// <summary>Whether <paramref name="name"/> is on the command line, whatever follows it.</summary>
        public static bool Has(string[] args, string name) =>
            args != null && Array.Exists(args, a => String.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>The session behind <c>--video-edit-session</c>, or null with
        /// <paramref name="problem"/> saying why it cannot be opened: no path, no file, a file that
        /// will not parse, or the refusal <see cref="VideoEditorWindow.ShowSession"/> gives a Recents
        /// row. Without it a mistyped path or a fixture with a misnamed field would open as an empty
        /// project and pass for a result. What goes wrong after this, opening the recording, the edit
        /// or the player, the window reports the same way (<c>ShowOpenFailure</c>). The file is
        /// checked for first because <see cref="SessionInfo"/> creates a missing one.</summary>
        public static SessionInfo OpenSession(string path, out string problem)
        {
            if (String.IsNullOrEmpty(path))
            {
                problem = "no session file was named";
                return null;
            }

            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                problem = $"there is no session file at {fullPath}";
                return null;
            }

            SessionInfo session;
            try
            {
                session = new SessionInfo(fullPath);
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
            {
                problem = $"{fullPath} cannot be read: {ex.Message}";
                return null;
            }

            if (session.IsRecordingMissing)
            {
                problem = String.IsNullOrEmpty(session.VideoPath)
                    ? $"{fullPath} is not a video project and has no VideoPath"
                    : $"the recording {fullPath} names is not there: {session.VideoPath}";
                session.Dispose();
                return null;
            }

            problem = null;
            return session;
        }
    }
}
