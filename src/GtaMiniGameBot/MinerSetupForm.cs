using System.Text;

namespace GtaMiniGameBot;

/// <summary>
/// Khoanh ba ô HUD của job thợ mỏ.
///
/// Luôn là CHỤP ẢNH TĨNH RỒI KHOANH TRÊN ẢNH, không khoanh trực tiếp: ô "ĐANG KHAI THÁC…" chỉ
/// sống đúng 10 giây và gợi ý thang máy tắt ngay khi bước ra khỏi bệ — không đủ để alt-tab sang
/// app rồi kéo chuột. Cùng lý do với màn hình đổ cốp của job câu cá, xem <see cref="TrunkSetupForm"/>.
/// </summary>
internal sealed class MinerSetupForm : Form
{
    private enum Slot { Mining, Lift, Cash }

    private sealed class SlotInfo
    {
        public string Label;
        public string Hint;
        public string Shot;
        public string Instruction;
    }

    private static readonly Dictionary<Slot, SlotInfo> Slots = new()
    {
        [Slot.Mining] = new SlotInfo
        {
            Label = "Ô đào",
            Shot = "mining",
            Instruction = "Vào game, đứng vào cột sáng vàng và bấm E để bắt đầu đào.\r\n" +
                          "Ảnh phải chụp lúc ô “ĐANG KHAI THÁC…” đang hiện (bạn có 10 giây).",
            Hint = "Khoanh gọn quanh ô “ĐANG KHAI THÁC…”. Lấy cả khối vuông tiến trình bên trái " +
                   "và chữ bên phải, nhưng ĐỪNG lấy nền hầm xung quanh — nền đổi theo chỗ đứng."
        },
        [Slot.Lift] = new SlotInfo
        {
            Label = "Gợi ý thang máy",
            Shot = "lift",
            Instruction = "Vào game, đẩy xe cút kít tới giếng thang máy và đứng yên ở đó.\r\n" +
                          "Ảnh phải chụp lúc gợi ý “[E] DÙNG THANG MÁY” đang hiện.",
            Hint = "Khoanh gọn quanh gợi ý “[E] DÙNG THANG MÁY”, lấy cả ô phím E lẫn dòng chữ."
        },
        [Slot.Cash] = new SlotInfo
        {
            Label = "Toast tiền",
            Shot = "cash",
            Instruction = "Vào game, đẩy xe vào mốc “?” để giao hàng.\r\n" +
                          "Ảnh phải chụp lúc dòng “Tiền mặt: + $…” còn hiện ở góc trái dưới.",
            Hint = "Khoanh phần CHỮ “Tiền mặt: +” thôi, đừng lấy con số — số tiền đổi theo chuyến " +
                   "thì mẫu sẽ không khớp lại được."
        }
    };

    private readonly MinerConfig _cfg;
    private readonly Screen _screen;
    private readonly MinerProfile _profile;
    private readonly string _key;

    private readonly Dictionary<Slot, Label> _status = new();
    private readonly TextBox _log = new();

    public MinerSetupForm(MinerConfig cfg, Screen screen)
    {
        _cfg = cfg;
        _screen = screen;
        _profile = cfg.ProfileFor(screen);
        _key = _profile.Key;

        Text = $"Khoanh vùng HUD thợ mỏ — {_key}";
        Font = new Font("Segoe UI", 9F);
        ClientSize = new Size(820, 560);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;

        BuildUi();
        RefreshAll();

        Append($"màn hình {_key}. Ảnh tĩnh lưu ở {MinerConfig.ShotDir(_key)}");
        Append("Với mỗi hàng: “Chụp ảnh” trước, rồi “Khoanh” trên ảnh vừa chụp.");
    }

    private void BuildUi()
    {
        int y = 12;
        const int w = 796;

        Controls.Add(new Label
        {
            Text = "Mỗi ô cần một ảnh riêng vì chúng không bao giờ cùng hiện một lúc.",
            Location = new Point(12, y),
            AutoSize = true,
            ForeColor = Color.DimGray
        });
        y += 26;

        foreach (Slot slot in Enum.GetValues<Slot>())
        {
            var info = Slots[slot];
            var box = new GroupBox
            {
                Text = info.Label,
                Location = new Point(12, y),
                Size = new Size(w, 92)
            };
            Controls.Add(box);

            var shot = new Button { Text = "Chụp ảnh", Bounds = new Rectangle(16, 26, 120, 30) };
            shot.Click += (_, _) => DoShot(slot);
            box.Controls.Add(shot);

            var crop = new Button { Text = "Khoanh", Bounds = new Rectangle(146, 26, 120, 30) };
            crop.Click += (_, _) => DoCrop(slot);
            box.Controls.Add(crop);

            var st = new Label
            {
                Font = new Font("Consolas", 9.5F),
                AutoSize = false,
                Bounds = new Rectangle(280, 26, 500, 20)
            };
            box.Controls.Add(st);
            _status[slot] = st;

            box.Controls.Add(new Label
            {
                Text = info.Hint,
                AutoSize = false,
                ForeColor = Color.DimGray,
                Bounds = new Rectangle(16, 60, 764, 26)
            });

            y += 100;
        }

        _log.SetBounds(12, y, w, ClientSize.Height - y - 52);
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Font = new Font("Consolas", 9F);
        Controls.Add(_log);

        var close = new Button
        {
            Text = "Xong",
            Bounds = new Rectangle(w - 88, ClientSize.Height - 36, 100, 28),
            DialogResult = DialogResult.OK
        };
        Controls.Add(close);
        AcceptButton = close;
    }

    // ---------------------------------------------------------------- chụp

    private void DoShot(Slot slot)
    {
        var info = Slots[slot];
        var shot = StillPicker.CaptureWithCountdown(
            this, _screen, info.Instruction, _cfg.ShotCountdownSec, _cfg.WindowMatch, out string problem);

        if (shot is null)
        {
            Append($"chụp “{info.Label}”: {problem ?? "không chụp được"}");
            return;
        }

        using (shot)
        {
            try
            {
                StillPicker.Save(shot, MinerConfig.ShotPath(_key, info.Shot));
                Append($"đã chụp “{info.Label}” {shot.Width}×{shot.Height}");
            }
            catch (Exception ex)
            {
                Append($"lưu ảnh “{info.Label}” lỗi: {ex.Message}");
            }
        }
        RefreshAll();
    }

    // ---------------------------------------------------------------- khoanh

    private void DoCrop(Slot slot)
    {
        var info = Slots[slot];
        using var still = StillPicker.Load(MinerConfig.ShotPath(_key, info.Shot));
        if (still is null)
        {
            Append($"chưa có ảnh “{info.Label}” — bấm “Chụp ảnh” ở hàng này trước");
            return;
        }
        if (still.Width != _profile.Width || still.Height != _profile.Height)
        {
            Append($"ảnh {still.Width}×{still.Height} lệch màn hình {_profile.Width}×{_profile.Height} — chụp lại");
            return;
        }

        var res = StillCropForm.Run(this, still, info.Label, info.Hint, Current(slot).ToRectangle());
        if (res is null) { Append($"đã huỷ khoanh “{info.Label}”"); return; }

        try
        {
            SaveTemplate(MinerConfig.ShotPath(_key, info.Shot), res.Rect, TemplatePath(slot));
            Apply(slot, FishingRect.FromRelative(res.Rect));
            _cfg.Save();
            Append($"“{info.Label}” = {res.Rect.Width}×{res.Rect.Height} @ {res.Rect.X},{res.Rect.Y} " +
                   $"→ {Path.GetFileName(TemplatePath(slot))}");
        }
        catch (Exception ex)
        {
            Append($"lưu “{info.Label}” lỗi: {ex.Message}");
        }
        RefreshAll();
    }

    /// <summary>Cắt đúng ô vừa khoanh khỏi ảnh tĩnh rồi lưu thành mẫu xám để so NCC.</summary>
    private static void SaveTemplate(string stillPath, Rectangle rect, string outPath)
    {
        var tpl = GrayTemplate.FromFileCrop(stillPath, rect);
        if (tpl.IsFlat) throw new InvalidOperationException("ô phẳng tuyệt đối — khoanh trúng chỗ trống");
        tpl.Save(outPath);
    }

    private FishingRect Current(Slot slot) => slot switch
    {
        Slot.Mining => _profile.MiningBox,
        Slot.Lift => _profile.LiftPrompt,
        _ => _profile.CashToast
    };

    private void Apply(Slot slot, FishingRect r)
    {
        switch (slot)
        {
            case Slot.Mining: _profile.MiningBox = r; break;
            case Slot.Lift: _profile.LiftPrompt = r; break;
            default: _profile.CashToast = r; break;
        }
    }

    private string TemplatePath(Slot slot) => slot switch
    {
        Slot.Mining => MinerConfig.MiningTemplatePath(_key),
        Slot.Lift => MinerConfig.LiftTemplatePath(_key),
        _ => MinerConfig.CashTemplatePath(_key)
    };

    // ---------------------------------------------------------------- hiển thị

    private void RefreshAll()
    {
        foreach (Slot slot in Enum.GetValues<Slot>())
        {
            var r = Current(slot);
            bool hasShot = File.Exists(MinerConfig.ShotPath(_key, Slots[slot].Shot));
            bool hasTpl = File.Exists(TemplatePath(slot));

            var st = _status[slot];
            if (r.IsSet && hasTpl)
            {
                st.Text = $"đủ — {r.W}×{r.H} @ {r.X},{r.Y}";
                st.ForeColor = Color.DarkGreen;
            }
            else
            {
                st.Text = hasShot ? "đã có ảnh, chưa khoanh" : "chưa chụp ảnh";
                st.ForeColor = Color.Firebrick;
            }
        }
    }

    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "bot-log.txt");
    private static readonly Encoding LogEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    private void Append(string line)
    {
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        try
        {
            File.AppendAllText(LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [khoanh mỏ] {line}{Environment.NewLine}", LogEncoding);
        }
        catch { }
    }
}
