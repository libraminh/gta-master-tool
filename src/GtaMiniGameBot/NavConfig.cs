namespace GtaMiniGameBot;

/// <summary>
/// Hằng số của bộ tự đi tới điểm làm việc (<see cref="NavBot"/>).
///
/// Vì sao ĐỂ RIÊNG FILE thay vì nhét cạnh <see cref="WireSettings"/>/<see cref="BoardSettings"/>
/// trong <c>ElectricConfig.cs</c>: file đó đã 600+ dòng, và bộ này gần 40 tham số.
///
/// Vì sao MỌI con số phải nằm ở đây, không được gõ cứng trong code: phần thị giác kiểm được ngoài
/// game bằng <c>--verify-nav</c>, nhưng BỘ LÁI là vòng điều khiển kín — mỗi lần chỉnh một ngưỡng
/// là một lượt thử trong game. Để trong json thì chỉnh xong chạy lại luôn, không phải build lại.
/// Bản Python mất tới V6.68 vì mỗi lần chỉnh là một lần sửa mã.
///
/// Các mốc pixel đều đo ở 1920×1080 (hậu tố <c>Ref</c>) rồi nhân <see cref="ElectricProfile.Sx"/>
/// / <see cref="ElectricProfile.Sy"/>, đúng quy ước sẵn có của job này.
/// </summary>
internal sealed class NavSettings
{
    // ---------------------------------------------------------------- chuot / camera

    /// <summary>
    /// Số count chuột bắn ra lúc tự hiệu chuẩn để đo "bao nhiêu count được một độ".
    ///
    /// Phải đủ lớn để góc đổi rõ hơn nhiễu của bộ dò chấm (±1–2°), nhưng đủ nhỏ để không quay quá
    /// một phần tư vòng — quay quá thì chấm vàng có thể chạy sang phía đối diện và hiệu góc bị
    /// gấp vòng.
    /// </summary>
    public int CalibrateCounts { get; set; } = 200;

    /// <summary>Chờ ngần này ms sau mỗi cú bắn chuột hiệu chuẩn rồi mới đọc lại góc.</summary>
    public int CalibrateSettleMs { get; set; } = 140;

    /// <summary>
    /// Góc đổi ít hơn ngần này thì coi như minimap KHÔNG xoay theo camera → chuyển sang chế độ
    /// dò dốc (xoay thử rồi đo lại). Đặt trên hẳn mức nhiễu của bộ dò chấm.
    /// </summary>
    public double CalibrateMinDeltaDeg { get; set; } = 5.0;

    /// <summary>Không tin nổi kết quả hiệu chuẩn thì lấy tỉ lệ này (count trên mỗi độ).</summary>
    public double FallbackCountsPerDeg { get; set; } = 3.0;

    /// <summary>Sai số góc nhỏ hơn ngần này thì thôi không chỉnh — tránh rung camera liên tục.</summary>
    public double YawDeadzoneDeg { get; set; } = 2.0;

    /// <summary>Hệ số P: mỗi vòng chỉ bù ngần này phần sai số, để không vọt qua.</summary>
    public double YawKp { get; set; } = 0.45;

    /// <summary>Trần count chuột mỗi vòng. Chặn cú giật lớn khi bộ dò trả một khung rác.</summary>
    public int YawMaxCounts { get; set; } = 60;

    /// <summary>Sai số còn lớn hơn ngần này thì XOAY TẠI CHỖ, chưa đi — đi luôn là đi vòng cung.</summary>
    public double TurnOnlyDeg { get; set; } = 55.0;

    /// <summary>
    /// Nửa góc nhìn ngang, để đổi "lệch bao nhiêu pixel trên màn" ra "lệch bao nhiêu độ" lúc bám
    /// mốc 3D. Xấp xỉ tuyến tính là đủ: đây là vòng kín, sai một chút thì vòng sau bù nốt.
    /// </summary>
    public double HalfFovDeg { get; set; } = 30.0;

    // ---------------------------------------------------------------- chuan hoa pitch

    /// <summary>
    /// Dí camera xuống hết chốt: bắn ngần này count xuống. Pitch trong GTA có chốt cứng hai đầu
    /// nên dí quá tay cũng không sao, đó chính là cách lấy được một mốc biết trước mà KHÔNG bắt
    /// người dùng tự canh góc như bản Python.
    /// </summary>
    public int PitchDownCounts { get; set; } = 1200;

    /// <summary>
    /// Từ chốt dưới ngẩng lên ngần này count để về góc nhìn đi đường.
    ///
    /// 380 là số cho GÓC 1 và nó CHƯA được đo trong game — tầm pitch góc 1 khác góc 3 (số cũ 520
    /// đo ở góc 3), mà đây là loại hằng số chỉ đo được bằng một lượt chạy thật. Nếu lượt đầu thấy
    /// bot nhìn quá cao (mất mốc dưới đất) thì hạ xuống, nhìn quá thấp (chỉ thấy mặt đường) thì
    /// nâng lên.
    /// </summary>
    public int PitchUpCounts { get; set; } = 380;

    /// <summary>Chia cú dí/ngẩng thành từng nhát ngần này count, tránh bắn một phát quá lớn.</summary>
    public int PitchStepCounts { get; set; } = 120;

    // ---------------------------------------------------------------- cham vang tren minimap

    /// <summary>HSV thấp của chấm vàng. Mốc Python <c>yellow_hsv_low</c>.</summary>
    public int DotHueLo { get; set; } = 18;

    public int DotHueHi { get; set; } = 45;

    public int DotSatMin { get; set; } = 132;

    public int DotValMin { get; set; } = 138;

    /// <summary>Diện tích chấm, đo ở mốc 1080p. Python <c>dot_area_min/max</c>.</summary>
    public double DotAreaMinRef { get; set; } = 48.0;

    public double DotAreaMaxRef { get; set; } = 215.0;

    /// <summary>Bề rộng/cao chấm ở mốc 1080p. Python <c>dot_w_min…dot_h_max</c>.</summary>
    public double DotSideMinRef { get; set; } = 8.0;

    public double DotSideMaxRef { get; set; } = 22.0;

    /// <summary>Tỉ lệ ngang/dọc — chấm là hình TRÒN nên gần 1.</summary>
    public double DotAspectMin { get; set; } = 0.80;

    public double DotAspectMax { get; set; } = 1.25;

    /// <summary>
    /// Độ tròn <c>4πA/P²</c> tối thiểu. Đây là cửa chính loại ICON SÉT của điểm giao việc: nó
    /// cũng vàng, cũng nằm trên minimap, nhưng là tia răng cưa nên chu vi dài mà diện tích nhỏ.
    /// </summary>
    public double DotCircularityMin { get; set; } = 0.70;

    /// <summary>Tỉ lệ lấp đầy hộp bao. Python <c>dot_fill_min 0.58</c>.</summary>
    public double DotFillMin { get; set; } = 0.55;

    /// <summary>
    /// Vị trí mũi tên người chơi TRONG vùng minimap, theo tỉ lệ. Mọi góc đều đo từ điểm này.
    ///
    /// Mặc định suy từ bản Python: <c>player_origin_ref [163, 980.4]</c> nằm trong
    /// <c>target_roi_ref [18,770,320,1026]</c> → (163−18)/302 = 0.480 và (980.4−770)/256 = 0.822.
    /// Không phải tâm ô vì ô quét trùm rộng hơn cái minimap. Đây là số ĐO TAY ở 1080p —
    /// <c>--verify-nav</c> in ra vị trí chấm để kiểm lại ở 2K.
    /// </summary>
    public double MinimapOriginXFrac { get; set; } = 0.480;

    public double MinimapOriginYFrac { get; set; } = 0.822;

    /// <summary>Khung này chấm nhảy xa hơn ngần này (mốc 1080p) thì không phải chấm cũ.</summary>
    public double DotTrackGateRef { get; set; } = 34.0;

    /// <summary>Mất dấu chấm dưới ngần này ms thì vẫn lái theo vị trí cuối.</summary>
    public int DotHoldMs { get; set; } = 420;

    // ---------------------------------------------------------------- moc vang 3D

    /// <summary>HSV mốc vàng dưới đất. Mốc Python <c>world_hsv_low/high</c>.</summary>
    public int MarkerHueLo { get; set; } = 17;

    public int MarkerHueHi { get; set; } = 47;

    public int MarkerSatMin { get; set; } = 105;

    public int MarkerValMin { get; set; } = 125;

    /// <summary>
    /// Diện tích tối thiểu ở mốc 1080p. Để thấp (Python 1200) vì mốc thật hay bị cột bê tông che
    /// gần hết, chỉ còn một mảng vỡ — siết cửa này là mất mốc bị che.
    /// </summary>
    public double MarkerAreaMinRef { get; set; } = 900.0;

    /// <summary>
    /// Trần diện tích. Nới rộng hẳn cho góc 1: đứng trong cột sáng thì nó trùm phần lớn khung, mà
    /// bị loại "quá to" đúng lúc đó là mất mốc ngay khoảnh khắc cần nó nhất — sát điểm làm.
    /// </summary>
    public double MarkerAreaMaxRef { get; set; } = 400000.0;

    /// <summary>Quét mốc theo bước này (pixel) để giảm chi phí — mốc to nên không cần từng pixel.</summary>
    public int MarkerSampleStep { get; set; } = 2;

    /// <summary>
    /// Dải quét mốc theo chiều dọc, tỉ lệ màn hình. Cắt trời phía trên và hàng HUD phía dưới.
    ///
    /// Đây là chỗ tốn nhất của vòng chạy: chụp cả 2560×1440 mất ~30 ms (đo được: ROI 1814×1053
    /// hết 16 ms). Cắt còn 80% chiều cao là bớt được một phần năm, mà mốc thì nằm DƯỚI ĐẤT nên
    /// không bao giờ ở dải trời. Bề NGANG thì giữ nguyên cả màn — bản Python đã phải nới ra hết cỡ
    /// sau khi thấy mốc thật đi vào từ sát mép trái.
    /// </summary>
    public double MarkerRoiTopFrac { get; set; } = 0.12;

    public double MarkerRoiBottomFrac { get; set; } = 0.92;

    /// <summary>
    /// Hộp che BÓNG NHÂN VẬT, tỉ lệ theo màn hình: (rộng, cao) tính từ đáy màn, canh giữa.
    /// 0 = TẮT, và đó là mặc định vì job này chạy ở GÓC NHÌN THỨ NHẤT.
    ///
    /// Hộp này sinh ra cho góc 3, để chặn logo "FLASH" vàng sau lưng áo — thứ lọt hết mọi cửa hình
    /// học (đo trên ảnh thật: ~77×48 px quy về mốc 1080p, sat/val đều cao). Ở góc 1 không có nhân
    /// vật trên màn, nên hộp 410×648 ngay đáy giữa màn chỉ còn là VÙNG CHẾT — mà đó đúng là chỗ
    /// mốc vàng chiếm khi đứng sát nó. Bản Python cũng chạy góc 1 và không hề có hộp tương ứng.
    ///
    /// Hệ quả phải nhớ: bỏ hộp đi thì <see cref="ParallaxMinPxRef"/> thành hàng rào DUY NHẤT chặn
    /// vật vàng đứng im (biển báo, đèn, tay cầm vũ khí). Ảnh <c>nav-pair-a/b</c> đúng chuẩn vì thế
    /// mà quan trọng hẳn lên — nó là ca kiểm duy nhất soi được hàng rào đó trên khung thật.
    /// </summary>
    public double SilhouetteWidthFrac { get; set; } = 0.0;

    public double SilhouetteHeightFrac { get; set; } = 0.0;

    /// <summary>
    /// Kiểm THỊ SAI: camera xoay sang phải thì vật TRONG THẾ GIỚI phải trôi sang TRÁI trên màn, và
    /// ngược lại. Ứng viên chỉ được khoá khi tâm nó dịch đúng chiều đó, ít nhất ngần này pixel
    /// (mốc 1080p). HUD và logo áo dịch 0 nên trượt cửa.
    ///
    /// Vì sao chỉ đòi DẤU + độ lớn tối thiểu, không đòi khớp một tỉ lệ px/count: tỉ lệ đó phụ
    /// thuộc FOV lẫn độ nhạy chuột, mà thứ cần loại (logo áo, HUD) thì đứng im tuyệt đối — dấu và
    /// một ngưỡng nhỏ đã tách được, còn đòi thêm là tự thêm một hằng số phải đo.
    /// </summary>
    public double ParallaxMinPxRef { get; set; } = 5.0;

    /// <summary>Camera phải xoay ít nhất ngần này count thì phép kiểm thị sai mới có nghĩa.</summary>
    public int ParallaxMinCounts { get; set; } = 8;

    /// <summary>Chưa qua được kiểm thị sai thì mốc chỉ là "ứng viên", chưa lái theo.</summary>
    public int MarkerConfirmFrames { get; set; } = 2;

    /// <summary>
    /// Đã khoá rồi thì khối vàng cách chỗ khoá cũ trong ngần này pixel (mốc 1080p) vẫn coi là
    /// cùng một mốc — khỏi phải kiểm thị sai lại mỗi khung.
    ///
    /// Cần rộng tay: ở nhịp 8 Hz và đang đi bộ, mốc trôi khá nhiều mỗi khung. Nhưng KHÔNG được bỏ
    /// hẳn phép kiểm liên tục: mốc khuất sau cột thì khối vàng lớn nhất còn lại rất có thể là logo
    /// trên áo, và nhảy khoá sang đó là bot quay vào chính mình.
    /// </summary>
    public double MarkerTrackGateRef { get; set; } = 260.0;

    /// <summary>Mất mốc dưới ngần này ms thì vẫn giữ khoá cũ (mốc hay bị vật cản che chớp nhoáng).</summary>
    public int MarkerHoldMs { get; set; } = 700;

    // ---------------------------------------------------------------- do tien do / ket

    /// <summary>
    /// Hai dải đất hai bên bóng nhân vật, tỉ lệ theo chiều cao màn. Sai phân khung ở đây là tín
    /// hiệu NHANH "hình có đang trôi không" — nhanh hơn cự ly nhưng KHÔNG đáng tin một mình,
    /// xem <see cref="GroundFlowMin"/>.
    ///
    /// Tránh chính giữa vì nhân vật đứng đó, và cái bóng của nhân vật cũng động theo.
    /// </summary>
    public double GroundBandTopFrac { get; set; } = 0.80;

    public double GroundBandBottomFrac { get; set; } = 0.93;

    /// <summary>Bề rộng mỗi dải, tỉ lệ màn, đặt sát mép trái/phải.</summary>
    public double GroundBandWidthFrac { get; set; } = 0.22;

    /// <summary>
    /// Sai khác trung bình mỗi pixel dưới ngần này thì coi như hình đứng yên.
    ///
    /// CHỈ là tín hiệu phụ. Đo trong game 23/08 thì nó sai cả hai chiều: lúc đang đi thật có khung
    /// chỉ đạt 2.5 (dưới ngưỡng → báo kẹt oan), lúc húc tường đứng im lại vọt lên 9.4 (trên ngưỡng
    /// → bỏ sót). Nguồn nhiễu là hoạt ảnh nhân vật khi húc tường, cộng với camera vẫn đang xoay
    /// theo bộ lái. Trọng tài thật là <see cref="MinProgressRef"/> trên cự ly chấm.
    /// </summary>
    public double GroundFlowMin { get; set; } = 3.0;

    /// <summary>Hình đứng yên suốt ngần này ms thì mới coi là tín hiệu nghi ngờ đáng kể.</summary>
    public int StuckMs { get; set; } = 900;

    // ---------------------------------------------------------------- tien do that

    /// <summary>
    /// Cửa sổ đo tiến độ trên CỰ LY chấm minimap.
    ///
    /// Vì sao cự ly là trọng tài chứ không phải sai phân khung: xoay camera đổi GÓC của chấm chứ
    /// không đổi CỰ LY, nên nó miễn nhiễm với chính thứ mà bộ lái đang làm suốt. Log 23/08 chứng
    /// minh: kẹt cứng thì cự ly đứng nguyên 31 suốt 30 giây, rồi 12↔13 suốt 25 giây; đi thật thì
    /// nó giảm đều 42→31, 29→7, 32→13. Sạch tuyệt đối.
    ///
    /// Bản Python cũng đo trên minimap nhưng lấy ngưỡng <c>stuck_displacement_px 1.15</c> trong
    /// 1.05 s — quá ngắn nên phải bắt một tín hiệu dưới hai pixel. Cửa sổ 3 s thì lượng dịch đủ to
    /// để đọc chắc chắn.
    /// </summary>
    public int ProgressWindowMs { get; set; } = 3000;

    /// <summary>Cự ly phải giảm được ngần này (mốc 1080p) trong cửa sổ trên thì mới tính là có đi.</summary>
    public double MinProgressRef { get; set; } = 1.0;

    /// <summary>
    /// Mẫu cự ly mới nhất cũ quá ngần này ms thì bộ theo dõi tiến độ TỰ NHẬN là không kết luận
    /// được, nhường lại cho tín hiệu đất trôi.
    ///
    /// Bắt buộc phải có, và thiếu nó là lỗi đã giết cả phiên chạy 25/08: mất dấu chấm thì không
    /// còn mẫu mới, nhưng lịch sử cũ vẫn nằm đó — mốc cửa sổ trôi tới cho tới khi mọi mẫu đều nằm
    /// trước nó, lúc đó Δ tính ra đúng 0, mà 0 > −<see cref="MinProgressRef"/> nên bot bị tuyên
    /// KẸT VĨNH VIỄN. Nó nổ đúng vào lúc bot chuyển sang bám mốc 3D, tức pha sắp tới nơi.
    ///
    /// Đặt hơi lớn hơn <see cref="DotHoldMs"/> một chút: chấm chớp tắt vài khung là chuyện thường
    /// và đã có cơ chế nhớ vị trí lo, chưa cần bỏ cả cửa sổ.
    /// </summary>
    public int ProgressStaleMs { get; set; } = 700;

    /// <summary>Cả lượt không cải thiện được cự ly quá ngần này ms thì bỏ lượt sớm.</summary>
    public int NoProgressAbortMs { get; set; } = 30000;

    // ---------------------------------------------------------------- thoat ket

    /// <summary>
    /// Độ dài từng bậc trượt ngang, ms. Bậc sau dài hơn bậc trước.
    ///
    /// Trượt bằng A/D THUẦN, không kèm W: W+A là đi chéo 45°, tức vẫn húc vào tường và thành phần
    /// đi ngang chỉ còn một nửa. Muốn men theo tường thì phải đi ngang thật.
    /// </summary>
    public int[] StrafeRungsMs { get; set; } = { 1200, 2400, 4000 };

    /// <summary>
    /// Sau mỗi bậc, đi bình thường ngần này ms rồi mới chấm điểm bằng cự ly.
    ///
    /// Bản cũ hỏi sai phân khung NGAY tại chỗ và luôn nhận được "đã thoát" — cú trượt làm nhân vật
    /// cựa quậy sát tường nên pixel đổi. Vì thế thang không bao giờ leo được quá bậc 2, và log
    /// 23/08 lặp "kẹt → thoát được → kẹt" đúng 8 lần liên tiếp trong 25 giây.
    /// </summary>
    public int ResumeCheckMs { get; set; } = 2000;

    /// <summary>
    /// Kẹt lại trong ngần này ms mà cự ly chưa cải thiện thì vẫn tính là CÙNG MỘT ĐỢT kẹt: giữ
    /// nguyên bên đã chọn và leo lên bậc tiếp theo.
    ///
    /// Thiếu đúng cơ chế này là lý do thang không leo: bản cũ mỗi lần kẹt đều bắt đầu lại từ bậc 1,
    /// và chọn bên theo dấu sai số — mà lúc kẹt sai số dao động quanh 0 (+0.9° rồi −0.2°) nên bên
    /// bị lật liên tục, đúng cái bẫy ghi chú V6.8 của bản Python đã cảnh báo.
    /// </summary>
    public int EpisodeJoinMs { get; set; } = 4000;

    /// <summary>Hết bậc một bên thì lùi S ngần này ms rồi đổi bên.</summary>
    public int BackupMs { get; set; } = 600;

    /// <summary>Đổi bên cũng hết bậc thì nhảy — GTA hay kẹt ở gờ thấp mà nhảy là qua.</summary>
    public bool UseJump { get; set; } = true;

    /// <summary>
    /// Vừa trượt xong thì đi lệch ngần này độ về phía đã trượt, rồi suy giảm dần về 0 trong
    /// <see cref="DetourBiasMs"/>. Không có nó thì nhắm lại mục tiêu ở 0° là cắm thẳng vào lại
    /// đúng cái khe vừa thoát ra.
    /// </summary>
    public double DetourBiasDeg { get; set; } = 25.0;

    public int DetourBiasMs { get; set; } = 2000;

    // ---------------------------------------------------------------- di chuyen

    /// <summary>
    /// Giữ Shift khi sai số góc nhỏ hơn ngần này. Mốc Python <c>sprint_angle_deg 52.0</c>.
    ///
    /// Rộng tay là cố ý: bản cũ để 12° nên hễ lệch một chút là bỏ chạy, mà đi đường thì lúc nào
    /// cũng lệch một chút. Đây là nút ĐẦU TIÊN hạ xuống (~30) nếu log cho thấy bot chạy vòng cung
    /// húc vật cản — chạy nhanh mà lệch thì cú va cũng nặng hơn.
    /// </summary>
    public double SprintMaxDeg { get; set; } = 50.0;

    /// <summary>
    /// Thấy khối vàng hợp lệ to từ ngần này (mốc 1080p) trở lên thì THÔI CHẠY, chuyển sang đi bộ.
    ///
    /// Đây là luật "chỉ đi bộ khi đã thấy vòng tròn vàng dưới đất", lấy thẳng từ bản Python
    /// (<c>world_sprint_area_max 2600</c>). Bản cũ dùng cự ly minimap (<c>SprintMinDistRef 26</c>)
    /// và tắt chạy quá sớm: log 25/08 chỉ chạy được 32→29 rồi đi bộ suốt quãng 26→7.
    ///
    /// Hai ảnh thật kẹp đúng hai bên ngưỡng này, nên nó không phải số bịa: <c>nav-far</c> không có
    /// ứng viên hợp lệ nào (→ chạy), <c>nav-marker</c> ứng viên lớn nhất dt=5443 (→ đi bộ).
    /// </summary>
    public double WalkMarkerAreaRef { get; set; } = 2600.0;

    /// <summary>
    /// Lớp chặn dự phòng: cự ly minimap xuống dưới ngần này thì đi bộ, kể cả khi chưa thấy mốc.
    ///
    /// Cần vì bộ dò mốc có thể trượt đúng lúc tới gần — mốc khuất sau cột, hoặc cả cụm vàng vượt
    /// <see cref="MarkerAreaMaxRef"/>. Không có lớp này thì bot chạy thẳng vọt qua điểm làm.
    /// </summary>
    public double WalkMinDistRef { get; set; } = 8.0;

    /// <summary>
    /// Cự ly (mốc 1080p) coi là "đã tới gần" — bật <c>wasClose</c>, tức mở đường cho
    /// <see cref="NearPushMs"/>/<see cref="NearHoldMs"/> và cho phép đọc prompt.
    ///
    /// Tách riêng, KHÔNG suy từ ngưỡng chạy nữa: bản cũ viết <c>SprintMinDistRef * 2</c>, nên chỉnh
    /// luật chạy là vô tình chỉnh luôn cả cơ chế "đã từng tới gần" — hai thứ chẳng liên quan gì
    /// nhau. Giá trị 52 giữ đúng hành vi cũ (26 × 2).
    /// </summary>
    public double NearDistRef { get; set; } = 52.0;

    // ---------------------------------------------------------------- prompt E

    /// <summary>Ngưỡng NCC cho mẫu chữ "TƯƠNG TÁC".</summary>
    public double PromptNccMin { get; set; } = 0.62;

    /// <summary>
    /// Kênh tối nhất phải sáng từ đây trở lên mới tính là mực chữ.
    ///
    /// CAO HƠN job thợ mộc (200) và đó là cố ý. Cảnh thợ mộc là rừng đêm; cảnh ở đây là trạm điện
    /// giữa trưa với cột bê tông trắng nắng. Đo được trên ảnh tự vẽ theo đúng cảnh đó: ở ngưỡng
    /// 200, một cái cột sáng chạy dọc hết ô khoanh gộp nhiều mực hơn cả dòng chữ, mà nó lại nằm
    /// ĐÚNG những hàng của dòng chữ — nên phép tách theo hàng không gỡ ra được, và cỡ chữ đo ra
    /// bằng chiều cao cả ô (70px thay vì 20px). Sai một lần đó là hỏng cả hai đầu: hiệu chuẩn ra
    /// mẫu rác, rồi bộ dò từ chối đúng dòng chữ thật vì "chữ quá thấp".
    ///
    /// Vì sao nâng ngưỡng là cách đúng chứ không phải cách chữa cháy: chữ HUD được vẽ SAU khâu ánh
    /// sáng nên luôn gần 255, còn mặt vật trong thế giới thì hiếm khi vượt ~235 dù nắng gắt.
    /// </summary>
    public int PromptInkMinBright { get; set; } = 240;

    /// <summary>Lệch kênh tối đa — chặn biển báo vàng và mọi thứ có sắc.</summary>
    public int PromptInkSpreadTol { get; set; } = 45;

    /// <summary>Hàng nào mực chiếm quá tỉ lệ này bề rộng băng thì là cảnh vật.</summary>
    public double PromptRowMaxFrac { get; set; } = 0.50;

    /// <summary>Thấy prompt ổn định ngần này khung mới bấm E.</summary>
    public int PromptConfirmFrames { get; set; } = 2;

    /// <summary>Giữ phím E bao lâu. Bằng <c>simple_e_hold_ms</c> của bản Python.</summary>
    public int EHoldMs { get; set; } = 90;

    /// <summary>Bấm E xong chờ tối đa ngần này ms xem bảng/panel có hiện ra không.</summary>
    public int WaitPanelMs { get; set; } = 4000;

    // ---------------------------------------------------------------- nhip va gioi han

    /// <summary>Nhịp vòng điều khiển.</summary>
    public int TickMs { get; set; } = 50;

    /// <summary>Đọc mốc 3D và prompt cách nhau ít nhất ngần này ms (chúng đắt hơn minimap).</summary>
    public int HeavyReadEveryMs { get; set; } = 125;

    /// <summary>
    /// SÀN cho hạn giờ quét (đếm từ lúc VÀO pha quét). Hạn thật do
    /// <c>NavBot.ScanBudgetMs()</c> tính ra từ tốc độ quay đo được, và luôn ≥ số này.
    ///
    /// Vì sao không dùng thẳng con số này: tốc độ quay phụ thuộc độ nhạy chuột của từng máy, nên
    /// một hằng số cố định không thể bảo đảm quét đủ 360°. Log 25/08 đo được 16.89 count/độ →
    /// 21.3 °/s → một vòng cần 16.9 s, trong khi hạn là 12 s. Cả ba lượt đều chết vì bỏ cuộc ở
    /// khoảng 256°.
    /// </summary>
    public int ScanTimeoutMs { get; set; } = 12000;

    /// <summary>
    /// Nhân thêm ngần này lần thời gian một vòng đầy, để chấm không bị bỏ sót vì rơi đúng vào
    /// khung cuối cùng của vòng.
    /// </summary>
    public double ScanTurnMargin { get; set; } = 1.25;

    /// <summary>Trần cứng cho hạn quét, phòng khi hiệu chuẩn ra tỉ lệ vô lý làm hạn phình vô hạn.</summary>
    public int ScanMaxMs { get; set; } = 45000;

    /// <summary>Count chuột mỗi vòng lúc quét tìm chấm. Đủ để xoay hết một vòng trong vài giây.</summary>
    public int ScanYawCounts { get; set; } = 18;

    /// <summary>
    /// Đã tới gần rồi mà mất dấu chấm thì ĐỨNG YÊN chờ prompt ngần này ms, chưa quay đi quét.
    ///
    /// Vì sao cần: tới sát nơi thì chấm đích chui xuống dưới mũi tên người chơi và biến mất khỏi
    /// minimap (bản Python có hẳn <c>occlusion_near_distance_px 15</c> cho ca này). Nếu lúc đó bot
    /// nhảy sang pha quét, nó sẽ xoay camera ngay khi đang đứng đúng chỗ — mà xoay thì prompt trôi
    /// ra khỏi băng quét, thành ra đứng ngay trên mốc mà không bao giờ bấm được E.
    /// </summary>
    public int NearHoldMs { get; set; } = 2500;

    /// <summary>
    /// Mất dấu chấm khi đã tới gần thì ĐI TIẾP ngần này ms theo hướng cuối, TRƯỚC KHI đứng chờ.
    ///
    /// Log 23/08: bot tới `xa=7` rồi mất chấm, đứng im 2.5 s, không có prompt, rồi quay đi quét
    /// 12 s và bỏ lượt. Ở cự ly đó nhân vật còn cách mốc vài mét — đứng im thì không bao giờ tới.
    /// </summary>
    public int NearPushMs { get; set; } = 2000;

    /// <summary>Một lượt tiếp cận dài quá ngần này ms thì tính là hỏng.</summary>
    public int ApproachTimeoutMs { get; set; } = 90000;

    /// <summary>Hỏng ngần này lượt liên tiếp thì dừng hẳn và báo.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>In log mỗi ngần này ms lúc đang đi, để không ngập bot-log.txt.</summary>
    public int LogEveryMs { get; set; } = 1000;

    public void Normalize()
    {
        CalibrateCounts = Math.Clamp(CalibrateCounts <= 0 ? 200 : CalibrateCounts, 40, 2000);
        CalibrateSettleMs = Math.Clamp(CalibrateSettleMs <= 0 ? 140 : CalibrateSettleMs, 30, 1000);
        CalibrateMinDeltaDeg = Math.Clamp(CalibrateMinDeltaDeg <= 0 ? 5.0 : CalibrateMinDeltaDeg, 1.0, 45.0);
        FallbackCountsPerDeg = Math.Clamp(FallbackCountsPerDeg <= 0 ? 3.0 : FallbackCountsPerDeg, 0.2, 60.0);

        YawDeadzoneDeg = Math.Clamp(YawDeadzoneDeg < 0 ? 2.0 : YawDeadzoneDeg, 0.0, 20.0);
        YawKp = Math.Clamp(YawKp <= 0 ? 0.45 : YawKp, 0.05, 1.5);
        YawMaxCounts = Math.Clamp(YawMaxCounts <= 0 ? 60 : YawMaxCounts, 5, 600);
        TurnOnlyDeg = Math.Clamp(TurnOnlyDeg <= 0 ? 55.0 : TurnOnlyDeg, 10.0, 179.0);
        HalfFovDeg = Math.Clamp(HalfFovDeg <= 0 ? 30.0 : HalfFovDeg, 5.0, 80.0);

        PitchDownCounts = Math.Clamp(PitchDownCounts <= 0 ? 1200 : PitchDownCounts, 100, 20000);
        PitchUpCounts = Math.Clamp(PitchUpCounts < 0 ? 520 : PitchUpCounts, 0, 20000);
        PitchStepCounts = Math.Clamp(PitchStepCounts <= 0 ? 120 : PitchStepCounts, 20, 2000);

        DotHueLo = Math.Clamp(DotHueLo, 0, 179);
        DotHueHi = Math.Clamp(DotHueHi, DotHueLo + 1, 179);
        DotSatMin = Math.Clamp(DotSatMin, 0, 255);
        DotValMin = Math.Clamp(DotValMin, 0, 255);
        DotAreaMinRef = Math.Clamp(DotAreaMinRef <= 0 ? 48.0 : DotAreaMinRef, 4.0, 5000.0);
        DotAreaMaxRef = Math.Clamp(DotAreaMaxRef <= DotAreaMinRef ? DotAreaMinRef * 4 : DotAreaMaxRef,
                                   DotAreaMinRef + 1, 20000.0);
        DotSideMinRef = Math.Clamp(DotSideMinRef <= 0 ? 8.0 : DotSideMinRef, 2.0, 200.0);
        DotSideMaxRef = Math.Clamp(DotSideMaxRef <= DotSideMinRef ? DotSideMinRef * 3 : DotSideMaxRef,
                                   DotSideMinRef + 1, 400.0);
        DotAspectMin = Math.Clamp(DotAspectMin <= 0 ? 0.80 : DotAspectMin, 0.1, 1.0);
        DotAspectMax = Math.Clamp(DotAspectMax < DotAspectMin ? 1.25 : DotAspectMax, 1.0, 10.0);
        DotCircularityMin = Math.Clamp(DotCircularityMin < 0 ? 0.70 : DotCircularityMin, 0.0, 1.0);
        DotFillMin = Math.Clamp(DotFillMin < 0 ? 0.55 : DotFillMin, 0.0, 1.0);
        DotTrackGateRef = Math.Clamp(DotTrackGateRef <= 0 ? 34.0 : DotTrackGateRef, 4.0, 400.0);
        DotHoldMs = Math.Clamp(DotHoldMs < 0 ? 420 : DotHoldMs, 0, 5000);

        MarkerHueLo = Math.Clamp(MarkerHueLo, 0, 179);
        MarkerHueHi = Math.Clamp(MarkerHueHi, MarkerHueLo + 1, 179);
        MarkerSatMin = Math.Clamp(MarkerSatMin, 0, 255);
        MarkerValMin = Math.Clamp(MarkerValMin, 0, 255);
        MarkerAreaMinRef = Math.Clamp(MarkerAreaMinRef <= 0 ? 900.0 : MarkerAreaMinRef, 50.0, 100000.0);
        MarkerAreaMaxRef = Math.Clamp(
            MarkerAreaMaxRef <= MarkerAreaMinRef ? MarkerAreaMinRef * 10 : MarkerAreaMaxRef,
            MarkerAreaMinRef + 1, 2000000.0);
        MarkerSampleStep = Math.Clamp(MarkerSampleStep <= 0 ? 2 : MarkerSampleStep, 1, 8);
        MarkerRoiTopFrac = Math.Clamp(MarkerRoiTopFrac < 0 ? 0.12 : MarkerRoiTopFrac, 0.0, 0.7);
        MarkerRoiBottomFrac = Math.Clamp(
            MarkerRoiBottomFrac <= MarkerRoiTopFrac ? MarkerRoiTopFrac + 0.2 : MarkerRoiBottomFrac,
            MarkerRoiTopFrac + 0.1, 1.0);
        SilhouetteWidthFrac = Math.Clamp(SilhouetteWidthFrac < 0 ? 0.16 : SilhouetteWidthFrac, 0.0, 0.6);
        SilhouetteHeightFrac = Math.Clamp(SilhouetteHeightFrac < 0 ? 0.45 : SilhouetteHeightFrac, 0.0, 0.9);
        ParallaxMinPxRef = Math.Clamp(ParallaxMinPxRef < 0 ? 5.0 : ParallaxMinPxRef, 0.0, 200.0);
        MinimapOriginXFrac = Math.Clamp(MinimapOriginXFrac <= 0 ? 0.480 : MinimapOriginXFrac, 0.0, 1.0);
        MinimapOriginYFrac = Math.Clamp(MinimapOriginYFrac <= 0 ? 0.822 : MinimapOriginYFrac, 0.0, 1.0);
        ParallaxMinCounts = Math.Clamp(ParallaxMinCounts <= 0 ? 8 : ParallaxMinCounts, 1, 500);
        MarkerConfirmFrames = Math.Clamp(MarkerConfirmFrames <= 0 ? 2 : MarkerConfirmFrames, 1, 20);
        MarkerTrackGateRef = Math.Clamp(MarkerTrackGateRef <= 0 ? 260.0 : MarkerTrackGateRef, 20.0, 1920.0);
        MarkerHoldMs = Math.Clamp(MarkerHoldMs < 0 ? 700 : MarkerHoldMs, 0, 5000);

        GroundBandTopFrac = Math.Clamp(GroundBandTopFrac <= 0 ? 0.80 : GroundBandTopFrac, 0.3, 0.97);
        GroundBandBottomFrac = Math.Clamp(
            GroundBandBottomFrac <= GroundBandTopFrac ? GroundBandTopFrac + 0.08 : GroundBandBottomFrac,
            GroundBandTopFrac + 0.02, 1.0);
        GroundBandWidthFrac = Math.Clamp(GroundBandWidthFrac <= 0 ? 0.22 : GroundBandWidthFrac, 0.05, 0.45);
        GroundFlowMin = Math.Clamp(GroundFlowMin <= 0 ? 3.0 : GroundFlowMin, 0.2, 80.0);
        StuckMs = Math.Clamp(StuckMs <= 0 ? 900 : StuckMs, 200, 10000);

        ProgressWindowMs = Math.Clamp(ProgressWindowMs <= 0 ? 3000 : ProgressWindowMs, 500, 20000);
        MinProgressRef = Math.Clamp(MinProgressRef <= 0 ? 1.0 : MinProgressRef, 0.1, 50.0);
        ProgressStaleMs = Math.Clamp(ProgressStaleMs <= 0 ? 700 : ProgressStaleMs, 100, 10000);
        NoProgressAbortMs = Math.Clamp(NoProgressAbortMs <= 0 ? 30000 : NoProgressAbortMs, 3000, 300000);

        if (StrafeRungsMs is null || StrafeRungsMs.Length == 0)
            StrafeRungsMs = new[] { 1200, 2400, 4000 };
        StrafeRungsMs = StrafeRungsMs.Select(m => Math.Clamp(m <= 0 ? 1200 : m, 150, 15000)).ToArray();

        ResumeCheckMs = Math.Clamp(ResumeCheckMs <= 0 ? 2000 : ResumeCheckMs, 300, 15000);
        EpisodeJoinMs = Math.Clamp(EpisodeJoinMs <= 0 ? 4000 : EpisodeJoinMs, 500, 30000);
        BackupMs = Math.Clamp(BackupMs <= 0 ? 600 : BackupMs, 100, 4000);
        DetourBiasDeg = Math.Clamp(DetourBiasDeg < 0 ? 25.0 : DetourBiasDeg, 0.0, 90.0);
        DetourBiasMs = Math.Clamp(DetourBiasMs < 0 ? 2000 : DetourBiasMs, 0, 20000);

        SprintMaxDeg = Math.Clamp(SprintMaxDeg < 0 ? 50.0 : SprintMaxDeg, 0.0, 90.0);
        WalkMarkerAreaRef = Math.Clamp(WalkMarkerAreaRef <= 0 ? 2600.0 : WalkMarkerAreaRef, 50.0, 2000000.0);
        WalkMinDistRef = Math.Clamp(WalkMinDistRef < 0 ? 8.0 : WalkMinDistRef, 0.0, 400.0);
        NearDistRef = Math.Clamp(NearDistRef <= 0 ? 52.0 : NearDistRef, 1.0, 800.0);

        PromptNccMin = Math.Clamp(PromptNccMin <= 0 ? 0.62 : PromptNccMin, 0.10, 0.99);
        PromptInkMinBright = Math.Clamp(PromptInkMinBright <= 0 ? 240 : PromptInkMinBright, 80, 252);
        PromptInkSpreadTol = Math.Clamp(PromptInkSpreadTol <= 0 ? 45 : PromptInkSpreadTol, 5, 120);
        PromptRowMaxFrac = Math.Clamp(PromptRowMaxFrac <= 0 ? 0.50 : PromptRowMaxFrac, 0.05, 1.0);
        PromptConfirmFrames = Math.Clamp(PromptConfirmFrames <= 0 ? 2 : PromptConfirmFrames, 1, 20);
        EHoldMs = Math.Clamp(EHoldMs <= 0 ? 90 : EHoldMs, 20, 1000);
        WaitPanelMs = Math.Clamp(WaitPanelMs <= 0 ? 4000 : WaitPanelMs, 500, 30000);

        TickMs = Math.Clamp(TickMs <= 0 ? 50 : TickMs, 10, 500);
        HeavyReadEveryMs = Math.Clamp(HeavyReadEveryMs <= 0 ? 125 : HeavyReadEveryMs, 30, 2000);
        ScanTimeoutMs = Math.Clamp(ScanTimeoutMs <= 0 ? 12000 : ScanTimeoutMs, 1000, 120000);
        ScanTurnMargin = Math.Clamp(ScanTurnMargin <= 0 ? 1.25 : ScanTurnMargin, 1.0, 4.0);
        ScanMaxMs = Math.Clamp(ScanMaxMs <= 0 ? 45000 : ScanMaxMs, ScanTimeoutMs, 300000);
        ScanYawCounts = Math.Clamp(ScanYawCounts <= 0 ? 18 : ScanYawCounts, 1, 400);
        NearHoldMs = Math.Clamp(NearHoldMs < 0 ? 2500 : NearHoldMs, 0, 20000);
        NearPushMs = Math.Clamp(NearPushMs < 0 ? 2000 : NearPushMs, 0, 20000);
        ApproachTimeoutMs = Math.Clamp(ApproachTimeoutMs <= 0 ? 90000 : ApproachTimeoutMs, 5000, 600000);
        MaxAttempts = Math.Clamp(MaxAttempts <= 0 ? 3 : MaxAttempts, 1, 20);
        LogEveryMs = Math.Clamp(LogEveryMs <= 0 ? 1000 : LogEveryMs, 100, 10000);
    }

    /// <summary>Con số cho bộ dò prompt, dựng từ cài đặt của job này.</summary>
    public PromptTuning PromptTuning(ElectricProfile p) => new()
    {
        InkMinBright = PromptInkMinBright,
        InkSpreadTol = PromptInkSpreadTol,
        InkRowMin = 3,
        RowMaxFrac = PromptRowMaxFrac,
        RowGapMerge = 2,
        LineBandMaxRatio = 8.0,
        MaxLines = 6,
        NccMin = PromptNccMin,
        TextH = p?.PromptTextH ?? 0,
        GapSplit = p?.PromptGapSplit ?? 0,
        MatchOnInk = true
    };
}
