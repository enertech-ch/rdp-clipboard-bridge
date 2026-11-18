using System.Drawing;
using System.Windows.Forms;

// A MessageBox cannot label its buttons, and these three answers need saying in words.
class InstallDialog : ToolDialog
{
    const int TextWidth = 420;
    const int Spacing = 12;

    InstallChoice choice = InstallChoice.Dismissed;

    public static InstallChoice Ask(bool replacesInstalledCopy)
    {
        using (InstallDialog dialog = new InstallDialog(replacesInstalledCopy))
        {
            dialog.ShowDialog();
            return dialog.choice;
        }
    }

    InstallDialog(bool replacesInstalledCopy)
    {
        Button installWithAutostart = NewChoiceButton(Texts.Get(TextId.ButtonInstallWithAutostart), InstallChoice.InstallWithAutostart);

        // One column: the wrapped explanation sets the width, and the buttons stack below it at that
        // same width - side by side, the longer translations made the dialog sprawl.
        TableLayoutPanel content = new TableLayoutPanel();
        content.ColumnCount = 1;
        content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        content.AutoSize = true;
        content.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        content.Padding = new Padding(Spacing);
        content.Controls.Add(NewExplanation(replacesInstalledCopy));
        content.Controls.Add(installWithAutostart);
        content.Controls.Add(NewChoiceButton(Texts.Get(TextId.ButtonInstallOnly), InstallChoice.InstallOnly));
        content.Controls.Add(NewChoiceButton(Texts.Get(TextId.ButtonRunWithoutInstalling), InstallChoice.RunWithoutInstalling));
        Controls.Add(content);

        AcceptButton = installWithAutostart;
        ActiveControl = installWithAutostart;
    }

    static Label NewExplanation(bool replacesInstalledCopy)
    {
        Label explanation = new Label();
        explanation.AutoSize = true;
        explanation.MaximumSize = new Size(TextWidth, 0);
        explanation.Margin = new Padding(0, 0, 0, Spacing);
        explanation.Text = Texts.Get(TextId.InstallExplanation, Texts.ProductName, Installation.InstallDirectory);
        if (replacesInstalledCopy) explanation.Text += "\n\n" + Texts.Get(TextId.InstallReplacesExisting);
        return explanation;
    }

    Button NewChoiceButton(string label, InstallChoice buttonChoice)
    {
        Button button = new Button();
        button.Text = label;
        button.AutoSize = true;
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(0, Spacing / 3, 0, 0);
        button.Padding = new Padding(Spacing / 2, 2, Spacing / 2, 2);
        button.Click += delegate
        {
            choice = buttonChoice;
            Close();
        };
        return button;
    }

    // A newer copy that takes over asks the running one to go - and right after its start the
    // running one is nothing but this dialog.
    protected override void WndProc(ref Message message)
    {
        if (SingleInstance.IsExitRequest(message.Msg))
        {
            SingleInstance.ExitRequested = true;
            Close();
            return;
        }
        base.WndProc(ref message);
    }

    // Esc closes like the X does: nothing chosen, nothing remembered.
    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }
}
