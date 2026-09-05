using System.Reflection;

namespace GtaMiniGameBot;

/// <summary>
/// Kiểm tra vòng đời của các control tự vẽ. Hiện chỉ có một ca, nhưng nó là ca đã thật sự nổ vào
/// mặt người dùng: chọn một mục trong <see cref="DarkPick"/> làm cả app ném
/// <c>ObjectDisposedException</c>.
///
/// Vì sao cần phép thử này thay vì bấm tay: bấm tay thì lỗi loại "vòng đời" chỉ lộ ra khi có người
/// đúng lúc mở đúng cái dropdown đó. Bản lỗi cũ nằm trong <see cref="DarkPick"/> — dùng ở cả tab
/// Câu và tab Điện — mà không ai gặp cho tới khi tab Điện có ba ô chọn.
///
/// Cách gọi vào phần private bằng reflection là CÓ CHỦ Ý: nó giữ cho <see cref="DarkPick"/> không
/// phải mọc thêm API "chỉ để test". Phép thử biết mình đang chọc vào chi tiết bên trong, và nếu
/// tên hàm đổi thì nó báo hỏng chứ không im lặng bỏ qua.
///
/// Chạy: GtaMiniGameBot.exe --verify-ui
/// </summary>
internal static class VerifyUi
{
    public static int Run(string[] args)
    {
        Console.WriteLine("== kiểm tra vòng đời control ==");

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        int fail = DarkPickOpenClose(pump: true, rounds: 4, "bấm chậm (có nghỉ giữa các bước)")
                 + DarkPickOpenClose(pump: false, rounds: 12, "bấm NHANH (không nghỉ)")
                 + DarkPickReopenWhileOpen();

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "TẤT CẢ ĐẠT" : $"HỎNG {fail} ca");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// Mở rồi đóng danh sách chọn nhiều lần, đóng bằng lý do <c>ItemClicked</c> — đúng đường mà
    /// WinForms đi khi người dùng bấm một mục:
    ///
    ///   ToolStrip.HandleItemClick → ContextMenuStrip.SetVisibleCore → Control.get_Handle()
    ///
    /// <paramref name="pump"/> là tham số QUAN TRỌNG nhất ở đây, không phải tuỳ chọn cho đẹp.
    /// Bơm message giữa các bước tặng cho <c>ToolStripManager.ModalMenuFilter</c> đúng khoảng nghỉ
    /// để nó nhả tham chiếu tới drop-down — và che mất đúng cái lỗi cần bắt. Người dùng bấm liên
    /// tục thì không có khoảng nghỉ đó, nên ca <c>pump: false</c> mới là ca thật.
    ///
    /// Cũng vì thế mà một handler <see cref="DarkPick.SelectedIndexChanged"/> LÀM VIỆC THẬT được
    /// gắn vào: WinForms gọi handler ở giữa <c>HandleClick</c> và <c>SetVisibleCore</c>, nên một
    /// handler rỗng là bỏ qua nửa đường thật.
    /// </summary>
    private static int DarkPickOpenClose(bool pump, int rounds, string label)
    {
        Console.WriteLine();
        Console.WriteLine($"-- DarkPick: mở/đóng danh sách chọn — {label} --");

        var drop = typeof(DarkPick).GetMethod("Drop", BindingFlags.NonPublic | BindingFlags.Instance);
        var menuField = typeof(DarkPick).GetField("_menu", BindingFlags.NonPublic | BindingFlags.Instance);
        if (drop is null || menuField is null)
        {
            Console.WriteLine("  HỎNG — không tìm thấy DarkPick.Drop()/_menu; " +
                              "đổi tên rồi thì sửa cả phép thử này");
            return 1;
        }

        // Dat cua so ra ngoai moi man hinh: phep thu buoc phai SHOW menu that moi dong that duoc,
        // va khong co ly do gi de no nhay len truoc mat nguoi dang dung may.
        using var form = new Form
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(200, 80),
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None
        };

        var pick = new DarkPick();
        pick.Items.Add("một");
        pick.Items.Add("hai");
        pick.Items.Add("ba");
        pick.SetBounds(0, 0, 180, 24);

        var note = new Label { Text = "x", Bounds = new Rectangle(0, 30, 180, 18) };
        form.Controls.Add(note);

        int changed = 0;
        pick.SelectedIndexChanged += () =>
        {
            // Bat chuoi viec ma handler that lam: ghi file + doi text control. Chay o GIUA
            // HandleClick va SetVisibleCore, dung nhu ElectricPanel.OnModeChanged.
            changed++;
            note.Text = "chọn " + pick.SelectedIndex;
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), "gtamgb-verify-ui.tmp");
                File.WriteAllText(tmp, pick.SelectedIndex.ToString());
            }
            catch { }
        };
        form.Controls.Add(pick);

        try
        {
            form.Show();
            Pump();

            for (int round = 1; round <= rounds; round++)
            {
                drop.Invoke(pick, null);
                if (pump) Pump();

                if (menuField.GetValue(pick) is not ContextMenuStrip menu)
                {
                    Console.WriteLine($"  HỎNG — vòng {round}: Drop() không tạo được menu");
                    return 1;
                }

                // Chon mot muc, roi dong bang dung ly do ItemClicked.
                var item = (ToolStripMenuItem)menu.Items[round % menu.Items.Count];
                item.PerformClick();
                menu.Close(ToolStripDropDownCloseReason.ItemClicked);
                if (pump) Pump();
            }

            if (changed == 0)
            {
                Console.WriteLine("  HỎNG — bấm mục mà SelectedIndexChanged không nổ lần nào");
                return 1;
            }

            // Huy control khi menu van con song: day la duong Dispose.
            pick.Dispose();
            form.Dispose();
            Pump();

            Console.WriteLine($"  đạt — {rounds} vòng không ném, SelectedIndexChanged nổ " +
                              $"{changed} lần, huỷ control sạch");
            return 0;
        }
        catch (TargetInvocationException ex)
        {
            Console.WriteLine("  HỎNG — " + (ex.InnerException?.ToString() ?? ex.ToString()));
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  HỎNG — " + ex);
            return 1;
        }
    }

    /// <summary>
    /// Bấm vào hộp trong khi danh sách ĐANG mở — thao tác mà ai cũng làm với combo box.
    ///
    /// Bản cũ mở thêm một menu nữa và bỏ rơi cái đang mở (bản 1), hoặc huỷ thẳng cái đang mở
    /// (bản 2). Bản đúng phải ĐÓNG nó lại và không mở gì thêm.
    /// </summary>
    private static int DarkPickReopenWhileOpen()
    {
        Console.WriteLine();
        Console.WriteLine("-- DarkPick: bấm lại vào hộp khi danh sách đang mở --");

        var drop = typeof(DarkPick).GetMethod("Drop", BindingFlags.NonPublic | BindingFlags.Instance);
        var menuField = typeof(DarkPick).GetField("_menu", BindingFlags.NonPublic | BindingFlags.Instance);
        if (drop is null || menuField is null)
        {
            Console.WriteLine("  HỎNG — không tìm thấy DarkPick.Drop()/_menu");
            return 1;
        }

        using var form = new Form
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(200, 80),
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None
        };

        var pick = new DarkPick();
        pick.Items.Add("một");
        pick.Items.Add("hai");
        pick.SetBounds(0, 0, 180, 24);
        form.Controls.Add(pick);

        try
        {
            form.Show();
            Pump();

            drop.Invoke(pick, null);      // mở
            Pump();
            drop.Invoke(pick, null);      // bấm lại khi đang mở → phải đóng
            Pump();

            var menu = (ContextMenuStrip)menuField.GetValue(pick);
            if (menu is null)
            {
                Console.WriteLine("  HỎNG — mất menu sau khi bấm lại");
                return 1;
            }

            // Bam mot muc NGAY sau do, TRUOC khi kiem hanh vi. Thu tu nay la co y: neu buoc tren
            // vua huy mot menu dang mo thi chinh cu bam nay la luc WinForms cham lai vao object da
            // chet — tuc la cho tai hien ObjectDisposedException, khong phai chi bao "van con mo".
            ((ToolStripMenuItem)menu.Items[0]).PerformClick();
            menu.Close(ToolStripDropDownCloseReason.ItemClicked);
            Pump();

            if (menu.Visible)
            {
                Console.WriteLine("  HỎNG — bấm lại mà danh sách vẫn mở");
                return 1;
            }

            // Va phai mo lai duoc binh thuong sau do.
            drop.Invoke(pick, null);
            Pump();
            if (menuField.GetValue(pick) is not ContextMenuStrip again || again.Items.Count != 2)
            {
                Console.WriteLine("  HỎNG — mở lại sau khi đóng không dựng đủ mục");
                return 1;
            }

            pick.Dispose();
            form.Dispose();
            Pump();

            Console.WriteLine("  đạt — bấm lại thì đóng, mở lại vẫn đủ mục, huỷ sạch");
            return 0;
        }
        catch (TargetInvocationException ex)
        {
            Console.WriteLine("  HỎNG — " + (ex.InnerException?.ToString() ?? ex.ToString()));
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  HỎNG — " + ex);
            return 1;
        }
    }

    /// <summary>
    /// Chạy hết message đang chờ. Bắt buộc: <c>Closed</c>, huỷ handle và phần lớn chuyện vòng đời
    /// của ToolStrip đều xảy ra trong lúc bơm message, không phải ngay tại chỗ gọi.
    /// </summary>
    private static void Pump()
    {
        for (int i = 0; i < 3; i++)
        {
            Application.DoEvents();
            Thread.Sleep(30);
        }
    }
}
