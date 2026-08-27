using System.Globalization;

namespace GtaMiniGameBot;

/// <summary>
/// Xem và sửa bảng "kg mỗi con" của từng loài.
///
/// Bot tự điền bảng này mỗi khi đọc được panel vật phẩm ("15 ĐƠN VỊ / 26.250 KG" → 1.750), nên
/// bình thường không phải mở tới. Hộp thoại tồn tại cho đúng một tình huống, mà tình huống đó
/// lại là mặc định trên máy mới: chữ số trên panel là cỡ thứ ba, khác cả tử số lẫn mẫu số của
/// thanh KG, nên bộ mẫu rất có thể chưa có nó. Chưa đọc được panel mà bảng cũng trống thì lần
/// tách đầu tiên hỏng, và bot lại bỏ trắng mấy kg cuối cốp như trước.
///
/// Điền một số vào đây là gỡ được nút đó ngay, không cần dạy thêm mẫu chữ nào.
/// </summary>
internal sealed class KgPerUnitForm : Form
{
    private readonly FishingConfig _cfg;
    private readonly List<(string Species, TextBox Box)> _rows = new();
    private readonly Label _status = new();

    public KgPerUnitForm(FishingConfig cfg, FishingProfile profile)
    {
        _cfg = cfg;

        Text = "Kg mỗi con";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(420, 520);
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.White;

        BuildUi(Species(profile));
    }

    /// <summary>
    /// Loài để hiện. Lấy hợp của danh sách cá đã tích và những gì bảng đang giữ — bảng có thể
    /// còn số của một loài vừa bị bỏ tích, và giấu nó đi thì người dùng không xoá được.
    /// </summary>
    private List<string> Species(FishingProfile profile)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string s in profile?.FishItems ?? new List<string>()) set.Add(s);
        foreach (string s in _cfg.KgPerUnit.Keys) set.Add(s);
        return set.ToList();
    }

    private void BuildUi(List<string> species)
    {
        int y = 12;

        Controls.Add(new Label
        {
            Text = "Bot tự điền bảng này khi đọc được panel vật phẩm trong game. " +
                   "Chỉ cần sửa tay khi nó chưa đọc được — để trống nghĩa là chưa biết.",
            Bounds = new Rectangle(12, y, 396, 46),
            AutoSize = false
        });
        y += 52;

        var list = new Panel
        {
            Bounds = new Rectangle(12, y, 396, 366),
            AutoScroll = true,
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(list);

        if (species.Count == 0)
        {
            list.Controls.Add(new Label
            {
                Text = "Chưa tích loài nào là cá — vào “Vật phẩm & cá” tích trước.",
                Bounds = new Rectangle(10, 10, 360, 40),
                ForeColor = Color.DimGray,
                AutoSize = false
            });
        }

        int ry = 8;
        foreach (string s in species)
        {
            list.Controls.Add(new Label
            {
                Text = s,
                Bounds = new Rectangle(10, ry + 4, 230, 20),
                AutoSize = false
            });

            double have = _cfg.KgPerUnitOf(s);
            var box = new TextBox
            {
                Bounds = new Rectangle(248, ry, 80, 24),
                Text = have > 0 ? have.ToString("0.###", CultureInfo.InvariantCulture) : "",
                TextAlign = HorizontalAlignment.Right
            };
            list.Controls.Add(box);

            list.Controls.Add(new Label
            {
                Text = "kg",
                Bounds = new Rectangle(334, ry + 4, 30, 20),
                AutoSize = false
            });

            _rows.Add((s, box));
            ry += 30;
        }
        y += 374;

        _status.SetBounds(12, y, 396, 20);
        _status.ForeColor = Color.Firebrick;
        Controls.Add(_status);
        y += 26;

        var ok = new Button { Text = "Lưu", Bounds = new Rectangle(228, y, 84, 28) };
        ok.Click += (_, _) => Save();
        Controls.Add(ok);

        var cancel = new Button
        {
            Text = "Đóng",
            Bounds = new Rectangle(324, y, 84, 28),
            DialogResult = DialogResult.Cancel
        };
        Controls.Add(cancel);
        CancelButton = cancel;
    }

    /// <summary>
    /// Ghi bảng. Từ chối TOÀN BỘ khi có một ô sai thay vì lưu phần đúng: lưu nửa vời để lại một
    /// bảng mà người dùng tưởng đã sửa xong, và con số họ gõ nhầm thì im lặng biến mất.
    /// </summary>
    private void Save()
    {
        var next = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var (species, box) in _rows)
        {
            string text = box.Text.Trim().Replace(',', '.');
            if (text.Length == 0) continue;

            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                || v < _cfg.SplitMinUnitKg || v > _cfg.SplitMaxUnitKg)
            {
                _status.Text = $"“{box.Text}” của {species} không phải số kg trong khoảng " +
                               $"{_cfg.SplitMinUnitKg:0.###}–{_cfg.SplitMaxUnitKg:0.###}";
                box.Focus();
                box.SelectAll();
                return;
            }
            next[species] = v;
        }

        _cfg.KgPerUnit = next;
        try
        {
            _cfg.Save();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _status.Text = "lưu lỗi: " + ex.Message;
        }
    }
}
