using System.Drawing;
using System.Windows.Forms;

namespace MultiAudio.App;

/// <summary>Themed confirmation dialog for destructive actions (MessageBox can't follow dark mode).</summary>
public static class Prompt
{
    public static bool Confirm(Form owner, string title, string message, string okText)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = owner.Visible ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = !owner.Visible,
            AutoScaleMode = AutoScaleMode.None,
            BackColor = Form1.ContentBg, Font = Form1.FBody,
        };
        int S(int v) => form.LogicalToDeviceUnits(v);
        form.HandleCreated += (_, _) => ThemeInfo.ApplyTitleBar(form.Handle, Form1.Dark);

        var lbl = new Label { Text = message, Font = Form1.FBody, ForeColor = Form1.Ink, AutoSize = true, MaximumSize = new Size(S(360), 0), Location = new Point(S(20), S(20)), UseMnemonic = false };
        int btnTop = S(20) + lbl.GetPreferredSize(new Size(S(360), 0)).Height + S(24);
        form.ClientSize = new Size(S(400), btnTop + S(32 + 20));

        FluentButton MakeButton(string text, DialogResult result, bool primary) => new()
        {
            Text = text, DialogResult = result, Font = Form1.FBody, Size = new Size(S(96), S(32)), Radius = S(4), FocusColor = Form1.Ink,
            BackColor = primary ? Form1.Accent : Form1.CardBg,
            HoverColor = primary ? Color.FromArgb(225, Form1.Accent) : Form1.HoverBg,
            ForeColor = primary ? Form1.OnAccent : Form1.Ink,
            NoBorder = primary, BorderColor = Form1.ControlBorder,
        };
        var cancel = MakeButton("Cancel", DialogResult.Cancel, false);
        var ok = MakeButton(okText, DialogResult.OK, true);
        cancel.Location = new Point(form.ClientSize.Width - S(20) - cancel.Width, btnTop);
        ok.Location = new Point(cancel.Left - S(8) - ok.Width, btnTop);

        form.Controls.AddRange(new Control[] { lbl, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.ActiveControl = cancel; // destructive: the safe choice has focus

        return form.ShowDialog(owner.Visible ? owner : null) == DialogResult.OK;
    }
}
