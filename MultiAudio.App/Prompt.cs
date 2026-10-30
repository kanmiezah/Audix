using System.Drawing;
using System.Windows.Forms;

namespace MultiAudio.App;

/// <summary>Minimal single-line text prompt (WinForms ships no InputBox).</summary>
public static class Prompt
{
    public static string? Text(IWin32Window owner, string title, string label, string initial = "")
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(360, 130),
            MinimizeBox = false,
            MaximizeBox = false,
            Font = new Font("Segoe UI", 9f),
        };

        var lbl = new Label { Text = label, Left = 12, Top = 14, Width = 336 };
        var box = new TextBox { Left = 12, Top = 40, Width = 336, Text = initial };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 192, Top = 80, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 273, Top = 80, Width = 75 };

        form.Controls.AddRange(new Control[] { lbl, box, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog(owner) == DialogResult.OK && box.Text.Trim().Length > 0
            ? box.Text.Trim()
            : null;
    }
}
