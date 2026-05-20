using Microsoft.Web.WebView2.WinForms;
using System.Drawing;
using System.Windows.Forms;

namespace PaykitTotem;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing) components?.Dispose();
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        _wv = new WebView2();
        SuspendLayout();

        _wv.Dock = DockStyle.Fill;
        _wv.TabIndex = 0;

        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1280, 800);
        Controls.Add(_wv);
        Name = "MainForm";
        Text = "Paykit Totem";
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;

        ResumeLayout(false);
        // Modo kiosk
        // this.FormBorderStyle = FormBorderStyle.None;
        // this.WindowState = FormWindowState.Maximized;
        // this.TopMost = true;
    }
}