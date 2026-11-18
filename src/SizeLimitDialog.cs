using System.Drawing;
using System.Windows.Forms;

// Laid out like InstallDialog: in font units, one column under a wrapped explanation.
class SizeLimitDialog : ToolDialog
{
    const int TextWidth = 320;
    const int Spacing = 12;

    readonly NumericUpDown megabytes = new NumericUpDown();

    // Returns the chosen limit in MB, or null when the dialog was cancelled or closed.
    public static int? Ask(int currentMegabytes)
    {
        using (SizeLimitDialog dialog = new SizeLimitDialog(currentMegabytes))
        {
            if (dialog.ShowDialog() != DialogResult.OK) return null;
            // reading Value takes over what was typed but not yet confirmed, within the range
            return decimal.ToInt32(dialog.megabytes.Value);
        }
    }

    SizeLimitDialog(int currentMegabytes)
    {
        megabytes.Minimum = SizeLimit.MinMegabytes;
        megabytes.Maximum = SizeLimit.MaxMegabytes;
        megabytes.DecimalPlaces = 0;
        megabytes.Value = currentMegabytes;
        megabytes.Margin = new Padding(0, 0, 0, Spacing);

        Button ok = NewButton(Texts.Get(TextId.ButtonOk), DialogResult.OK);
        Button cancel = NewButton(Texts.Get(TextId.ButtonCancel), DialogResult.Cancel);
        FlowLayoutPanel buttons = new FlowLayoutPanel();
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.AutoSize = true;
        buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        buttons.Dock = DockStyle.Fill;
        buttons.Margin = Padding.Empty;
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        TableLayoutPanel content = new TableLayoutPanel();
        content.ColumnCount = 1;
        content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        content.AutoSize = true;
        content.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        content.Padding = new Padding(Spacing);
        content.Controls.Add(NewExplanation());
        content.Controls.Add(megabytes);
        content.Controls.Add(buttons);
        Controls.Add(content);

        AcceptButton = ok;
        CancelButton = cancel;
        ActiveControl = megabytes;
    }

    static Label NewExplanation()
    {
        Label explanation = new Label();
        explanation.AutoSize = true;
        explanation.MaximumSize = new Size(TextWidth, 0);
        explanation.Margin = new Padding(0, 0, 0, Spacing);
        explanation.Text = Texts.Get(TextId.SizeLimitExplanation);
        return explanation;
    }

    static Button NewButton(string label, DialogResult result)
    {
        Button button = new Button();
        button.Text = label;
        button.AutoSize = true;
        button.DialogResult = result;
        button.Margin = new Padding(Spacing / 2, 0, 0, 0);
        button.Padding = new Padding(Spacing / 2, 2, Spacing / 2, 2);
        return button;
    }

    // a newer copy taking over closes this too, as nothing chosen
    protected override void WndProc(ref Message message)
    {
        if (SingleInstance.IsExitRequest(message.Msg))
        {
            Close();
            return;
        }
        base.WndProc(ref message);
    }
}
