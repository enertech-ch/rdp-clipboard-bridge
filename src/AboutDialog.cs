using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

// Laid out like SizeLimitDialog: in font units, one column, the button at the bottom right.
class AboutDialog : ToolDialog
{
    const string ProjectUrl = "https://github.com/enertech-ch/rdp-clipboard-bridge";
    const int TextWidth = 320;
    const int Spacing = 12;

    public static void Open()
    {
        using (AboutDialog dialog = new AboutDialog()) dialog.ShowDialog();
    }

    // build.ps1 stamps x.y.z.0; the fourth part is always 0 and says nothing
    static string StampedVersion()
    {
        return typeof(AboutDialog).Assembly.GetName().Version.ToString(3);
    }

    AboutDialog()
    {
        Button ok = new Button();
        ok.Text = Texts.Get(TextId.ButtonOk);
        ok.AutoSize = true;
        ok.DialogResult = DialogResult.OK;
        ok.Anchor = AnchorStyles.Right;
        ok.Margin = Padding.Empty;
        ok.Padding = new Padding(Spacing / 2, 2, Spacing / 2, 2);

        TableLayoutPanel content = new TableLayoutPanel();
        content.ColumnCount = 1;
        content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        content.AutoSize = true;
        content.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        content.Padding = new Padding(Spacing);
        content.Controls.Add(NewText(Texts.ProductName + "\n" + Texts.Get(TextId.AboutVersion, StampedVersion())));
        content.Controls.Add(NewText(Texts.Get(TextId.AboutExplanation, Texts.ProductName)));
        content.Controls.Add(NewProjectLink());
        content.Controls.Add(ok);
        Controls.Add(content);

        AcceptButton = ok;
        CancelButton = ok;
        ActiveControl = ok;
    }

    static Label NewText(string text)
    {
        Label label = new Label();
        label.AutoSize = true;
        label.MaximumSize = new Size(TextWidth, 0);
        label.Margin = new Padding(0, 0, 0, Spacing);
        label.Text = text;
        return label;
    }

    static LinkLabel NewProjectLink()
    {
        LinkLabel link = new LinkLabel();
        link.AutoSize = true;
        link.Margin = new Padding(0, 0, 0, Spacing);
        link.Text = ProjectUrl;
        link.LinkClicked += delegate { OpenInBrowser(ProjectUrl); };
        return link;
    }

    // Nothing may escape the message loop. Without a browser to start the link simply does
    // nothing - the address is on screen to be typed in.
    static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(url);
        }
        catch (Exception)
        {
        }
    }

    // a newer copy taking over closes this too
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
