namespace DisplayControl.Agent;

public sealed class BlackOverlayForm : Form
{
    public BlackOverlayForm(Screen screen)
    {
        StartPosition = FormStartPosition.Manual;
        Bounds = screen.Bounds;
        BackColor = Color.Black;
        ForeColor = Color.White;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        Cursor.Hide();
        MouseDown += (_, _) => EmergencyReleaseRequested?.Invoke(this, EventArgs.Empty);
        KeyDown += (_, args) =>
        {
            EmergencyReleaseRequested?.Invoke(this, EventArgs.Empty);
        };
        Shown += (_, _) => Activate();
    }

    public event EventHandler? EmergencyReleaseRequested;

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Cursor.Show();
        base.OnFormClosed(e);
    }
}
