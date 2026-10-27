using Avalonia;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Composa.Text;
using SkiaSharp;

namespace Composa.App.Controls;

/// <summary>
/// Typing Chinese (or anything an input method composes) on the canvas. Windows keeps its IME switched off for a
/// focused element that offers no <see cref="TextInputMethodClient"/>: the language bar reports the input method as
/// unavailable, the composition keys never become characters and nothing reaches the canvas. So the Type tool lends
/// the window a client while a text layer is open — the caret, the selection and the surrounding text — and the
/// input method hands the text it is still composing back here to be drawn in place until it is committed.
/// </summary>
public sealed partial class CanvasView
{
    private CanvasTextClient? textClient;
    private TextEditor? textClientEditor;
    private bool textClientApplying;

    /// <summary>The string the input method is composing, drawn after the caret and underlined until it commits.</summary>
    private string? preedit;

    /// <summary>Whether the canvas wants the window's input method: only while a text layer is open for typing.</summary>
    private bool AcceptsTextInput => session?.TextEdit != null;

    private CanvasTextClient TextClient => textClient ??= new CanvasTextClient(this);

    /// <summary>
    /// Tells the window to ask the canvas for its input method client again. The answer changes when a text edit opens
    /// or closes, and only the request turns the IME on or takes it away.
    /// </summary>
    private void SyncTextInput()
    {
        if (session?.TextEdit is { } editor)
        {
            if (textClientEditor != editor)
            {
                if (textClientEditor != null) textClientEditor.Changed -= OnTextEditorChanged;
                textClientEditor = editor;
                textClientEditor.Changed += OnTextEditorChanged;
            }
        }
        else if (textClientEditor != null)
        {
            textClientEditor.Changed -= OnTextEditorChanged;
            textClientEditor = null;
            preedit = null;
        }
        RaiseEvent(new TextInputMethodClientRequeryRequestedEventArgs { RoutedEvent = InputMethod.TextInputMethodClientRequeryRequestedEvent });
    }

    private void OnInputMethodClientRequested(object? sender, TextInputMethodClientRequestedEventArgs e) =>
        e.Client = AcceptsTextInput ? TextClient : null;

    /// <summary>The caret moved or the text changed: the input method's idea of both has to follow.</summary>
    private void OnTextEditorChanged()
    {
        if (textClientApplying || !AcceptsTextInput) return;
        TextClient.NotifyTextChanged();
    }

    /// <summary>Where the caret is drawn, in control units: what Windows places the composition and candidate windows against.</summary>
    private Rect CaretRectangle()
    {
        if (session?.TextEdit is not { } editor || session.TextEditLayer is not { } layer) return default;
        var (x, top, bottom) = editor.Layout.CaretAt(editor.Caret);
        // The layout counts in the layer's own pixels, which the layer's place on the canvas maps to control units.
        var toView = layer.Matrix.PostConcat(ViewMatrix());
        var a = toView.MapPoint(x, top);
        var b = toView.MapPoint(x, bottom);
        return new Rect(a.X, Math.Min(a.Y, b.Y), Math.Max(1, Math.Abs(a.X - b.X)), Math.Abs(a.Y - b.Y));
    }

    /// <summary>
    /// The canvas as one text field for the platform. The indices are character indices into the layer's text, which is
    /// also what <see cref="TextEditor"/> counts in, so nothing has to be translated.
    /// </summary>
    private sealed class CanvasTextClient(CanvasView view) : TextInputMethodClient
    {
        public override Visual TextViewVisual => view;

        public override bool SupportsPreedit => true;

        public override bool SupportsSurroundingText => true;

        public override string SurroundingText => view.session?.TextEdit?.Text ?? string.Empty;

        public override Rect CursorRectangle => view.CaretRectangle();

        public override TextSelection Selection
        {
            get => view.session?.TextEdit is { } editor ? new TextSelection(editor.SelectionStart, editor.SelectionEnd) : default;
            set
            {
                if (view.session?.TextEdit is not { } editor) return;
                // Applying a selection is not the user changing one: no events back, or the platform and the editor
                // would keep correcting each other.
                view.textClientApplying = true;
                editor.MoveTo(value.Start, select: false);
                if (value.End != value.Start) editor.MoveTo(value.End, select: true);
                view.textClientApplying = false;
            }
        }

        public override void SetPreeditText(string? text, int? cursorPosition)
        {
            var next = string.IsNullOrEmpty(text) ? null : text;
            if (view.preedit == next) return;
            view.preedit = next;
            view.InvalidateVisual();
        }

        public void NotifyTextChanged()
        {
            RaiseSurroundingTextChanged();
            RaiseSelectionChanged();
            RaiseCursorRectangleChanged();
        }
    }
}
