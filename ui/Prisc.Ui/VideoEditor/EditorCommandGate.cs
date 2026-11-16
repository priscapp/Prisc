using System;
using System.Windows.Input;

namespace Prisc.UI.VideoEditor
{
    /// <summary>
    /// The one gate every keyboard command of the video editor goes through. A window key binding
    /// sees the key ahead of the focused control, and a binding whose command cannot execute leaves
    /// the key unhandled, so saying no here is what hands Ctrl+Z or Ctrl+V back to a text box.
    /// </summary>
    public sealed class GatedKeyCommand : ICommand
    {
        private readonly ICommand _inner;
        private readonly Func<bool> _allowed;

        public GatedKeyCommand(ICommand inner, Func<bool> allowed)
        {
            _inner = inner;
            _allowed = allowed;
        }

        public event EventHandler CanExecuteChanged
        {
            add => _inner.CanExecuteChanged += value;
            remove => _inner.CanExecuteChanged -= value;
        }

        public bool CanExecute(object parameter) => _allowed() && _inner.CanExecute(parameter);

        public void Execute(object parameter) => _inner.Execute(parameter);

        /// <summary>Whether a key may reach its command right now. A text field owns the keyboard, so
        /// the keys it uses itself go to it. An edit is also refused while a gesture is open, where
        /// it would raise only a preview, push no undo entry and be discarded with the drag, and
        /// while a voice take runs, whose ghost sits on a row the edit could take away.</summary>
        public static bool Allows(bool textEditorFocused, bool mutatesProject, bool gestureActive, bool voiceTakeActive) =>
            !textEditorFocused && (!mutatesProject || (!gestureActive && !voiceTakeActive));
    }
}
