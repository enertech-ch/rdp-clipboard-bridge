using System.Drawing;
using System.Windows.Forms;

// The window setup the tool's dialogs share. Each lays out its own content below it, in font units
// and in one column.
class ToolDialog : Form
{
    protected ToolDialog()
    {
        Text = Texts.ProductName;
        Icon = AppIcon.Large;
        Font = SystemFonts.MessageBoxFont;
        // the layout is in font units, so it grows with the display scaling instead of clipping text
        AutoScaleDimensions = new SizeF(6F, 13F);
        AutoScaleMode = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        // Left on, so the window stays unowned: only then does the exit request of a newer copy,
        // a broadcast, reach it. And the install dialog is shown before any other window of this
        // app exists, so the taskbar is what keeps it findable.
        ShowInTaskbar = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
    }
}
