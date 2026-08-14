namespace GtaMiniGameBot;

/// <summary>
/// Hộp thoại bắt một phím (hoặc tổ hợp) cho hotkey.
/// Bat trong ProcessCmdKey chu khong phai KeyDown: F1/F9/Tab/mui ten bi
/// WinForms nuot lam phim dieu huong truoc khi toi KeyDown.
/// </summary>
internal sealed class KeyCaptureDialog : Form
{
    private readonly bool _allowModifiers;
    private readonly Label _hint = new();

    public uint Vk { get; private set; }
    public uint Mods { get; private set; }

    public KeyCaptureDialog(string action, bool allowModifiers)
    {
        _allowModifiers = allowModifiers;

        Text = "Đổi phím — " + action;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(420, 130);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        KeyPreview = true;
        Font = new Font("Segoe UI", 9F);

        var title = new Label
        {
            Text = allowModifiers
                ? "Bấm phím hoặc tổ hợp mới (Ctrl/Shift/Alt + phím)."
                : "Bấm phím mới. Phím này không nhận tổ hợp.",
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            AutoSize = false
        };
        title.SetBounds(16, 20, 388, 24);
        Controls.Add(title);

        _hint.Text = "Esc để hủy.";
        _hint.ForeColor = Color.DimGray;
        _hint.AutoSize = false;
        _hint.SetBounds(16, 52, 388, 48);
        Controls.Add(_hint);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var code = keyData & Keys.KeyCode;

        if (code == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            Close();
            return true;
        }

        // Bam rieng Ctrl/Shift/Alt/Win thi cho bam tiep, chua phai lua chon.
        if (code is Keys.None or Keys.ControlKey or Keys.ShiftKey or Keys.Menu
            or Keys.LWin or Keys.RWin)
            return true;

        uint mods = 0;
        if ((keyData & Keys.Control) == Keys.Control) mods |= Native.MOD_CONTROL;
        if ((keyData & Keys.Shift) == Keys.Shift) mods |= Native.MOD_SHIFT;
        if ((keyData & Keys.Alt) == Keys.Alt) mods |= Native.MOD_ALT;

        if (!_allowModifiers && mods != 0)
        {
            _hint.Text = "Phím này chỉ nhận phím đơn — bỏ Ctrl/Shift/Alt rồi bấm lại.\r\nEsc để hủy.";
            _hint.ForeColor = Color.Firebrick;
            return true;
        }

        Vk = (uint)code;
        Mods = mods;
        DialogResult = DialogResult.OK;
        Close();
        return true;
    }
}
