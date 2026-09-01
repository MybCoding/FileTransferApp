// Behaviors/EditorEnterBehavior.cs
using Microsoft.Maui.Controls;
using System.Windows.Input;

namespace FileTransferApp.Behaviors
{
    public class EditorEnterBehavior : Behavior<Editor>
    {
        public static readonly BindableProperty CommandProperty =
            BindableProperty.Create(nameof(Command), typeof(ICommand), typeof(EditorEnterBehavior));

        public ICommand Command
        {
            get => (ICommand)GetValue(CommandProperty);
            set => SetValue(CommandProperty, value);
        }

        protected override void OnAttachedTo(Editor editor)
        {
            base.OnAttachedTo(editor);
            editor.TextChanged += OnTextChanged;
        }


        protected override void OnDetachingFrom(Editor editor)
        {
            base.OnDetachingFrom(editor);
            editor.TextChanged -= OnTextChanged;
        }
        private void OnCompleted(object sender, EventArgs e)
        {
            if (Command?.CanExecute(null) == true)
                Command.Execute(null);
        }
        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (e.NewTextValue?.EndsWith("\n") == true)
            {
                var editor = (Editor)sender;
                editor.Text = e.NewTextValue.TrimEnd('\n');

                if (Command?.CanExecute(null) == true)
                    Command.Execute(null);
            }
        }
    }
}
