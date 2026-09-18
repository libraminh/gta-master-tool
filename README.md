# GTA Master Tool

WinForms .NET 8 (x64). Bot hỗ trợ job trên PlayXGTA: Dầu, Câu, Mỏ, Mộc, Điện + tab Tiện ích. Không phải web app.

Tài liệu này dành cho AI agent. Làm đúng các quy tắc dưới đây khi nhận task — đừng đoán chỗ chạy, chỗ ghi file, hay git.

## Chỗ chạy

Chỉ một exe: `app\GtaMiniGameBot.exe`.

F5 / `dotnet build` / Release đều ra đó (`src/GtaMiniGameBot/GtaMiniGameBot.csproj`). Không dùng `bin\Debug` hay `bin\Release` — đã bỏ.

## Build

```
dotnet build src/GtaMiniGameBot/GtaMiniGameBot.csproj -c Release
```

Nếu exe đang mở: **không kill**. Báo đúng câu này rồi dừng:

`Đang chạy app\GtaMiniGameBot.exe. Tắt app rồi bảo build lại.`

Đóng gói share: `tools/build-portable.ps1` (zip trong `dist\`, không đụng `app\` — bước Build ghi ra `%TEMP%`).

## Verify nghề Điện

```
app\GtaMiniGameBot.exe --verify-board
app\GtaMiniGameBot.exe --verify-capture
app\GtaMiniGameBot.exe --verify-capture --strict
app\GtaMiniGameBot.exe --verify-wire
app\GtaMiniGameBot.exe --verify-nav
app\GtaMiniGameBot.exe --verify-map
```

`--verify-map` kiểm bộ ghi bản đồ sân trạm biến áp (dò blip ⚡ / ✕ / 🍕 trên minimap, bộ giải pose,
bản ghi, bộ dựng bản đồ). Thêm `--learn` để học lại mẫu blip từ `nav-far.png`, thêm `--build` để dựng
bản đồ từ các `rec-*.csv` đã ghi được. Mẫu blip, bản ghi và `yard-map-v1.json` nằm trong
`%AppData%\GtaMiniGameBot\electric\<WxH>\map\`; ảnh soi bằng mắt ở `…\debug\map\`.

`--verify-map` còn mô phỏng **bám waypoint** (bộ bám đi qua sân tổng hợp có vật cản chữ U, mất pose,
teleport) và lập đường trên **bản đồ thật** nếu máy đã có `yard-map-v1.json` — in độ dài đường từ ⚡
tới tư thế tiếp cận của từng máy. Chỉ muốn kiểm thì **đừng** thêm `--build`: nó dựng lại và ghi đè bản
đồ đang dùng.

## Bám waypoint theo bản đồ sân (nghề Điện)

Có `yard-map-v1.json` thì bot đi tới máy theo **đường người đã đi**, chỉ giao lại ~3 m cuối cho luồng
cột vàng 3D → `[E] TƯƠNG TÁC`. Không có file (hoặc `Nav.UseYardMap = false` trong `electric.json`) thì
bộ bám nằm im vĩnh viễn và hành vi y hệt như trước — đây là đường lùi khi có nghi ngờ.

Đọc dòng trạng thái trong khung **Diễn biến** / `bot-log.txt`:

- `map=FIX2 c=0.93 mk=3 rem=41mu` — định vị bằng 2 mốc, đang đi tới máy 3, còn 41 mu (≈20 m) đường.
  `rem` phải tụt đều; `mk=–` mãi nghĩa là điểm vàng không rơi vào máy nào đã dạy (bot vẫn lái như cũ).
- `[MAP_BÁM|…]` ở đầu dòng = bộ bám đang cầm lái. `MAP_CHỜ_FIX2`, `MAP_MẤT_POSE`, `MAP_CHƯA_RÕ_MÁY`,
  `MAP_KHÔNG_CÓ_ĐƯỜNG`, `MAP_THOÁT_KẸT` là các lý do nó đứng ngoài.
- `[BẢN ĐỒ] khoá máy k` / `lập đường tới máy k` / `giao lại 3 m cuối cho luồng cũ` — các mốc bàn giao;
  sau dòng "giao lại" là `WORLD_DIRECT_*` rồi `[E ARM]` như mọi khi.

Water & Power ưu tiên DXGI Desktop Duplication (`Vortice.Direct3D11`) và tự lùi về GDI nếu GPU,
output xoay hoặc desktop mode không hỗ trợ. Capture chỉ sống khi job Điện chạy, chỉ xử lý frame mới.
Tuyến cache nằm trong `%AppData%\GtaMiniGameBot\electric\`; cache luôn được kiểm chứng lại trên
tường hiện tại trước khi gửi phím.

## Dữ liệu

- Config / ROI / icon: `%AppData%\GtaMiniGameBot` (`AppPaths`).
- `app.json` (`AppSettings`) — cài đặt chung: cờ log debug + overlay (bật/tắt, màn hình). Chỉnh ở tab
  Tiện ích. File này nhiều mục dùng chung, sửa phải đọc-sửa-ghi qua `AppSettings.Current`.
- Log / debug: `%AppData%\GtaMiniGameBot\logs\`
  - `bot-log.txt` — mặc định tắt, bật ở tab Tiện ích.
  - `overlay-log.txt`, `debug\` (dump dầu).
- Giữ 24 giờ (`LogHousekeeping`). Không ghi log/dump vào repo hay `app\`.
- Khung **Diễn biến** trên UI chỉ hiện trên màn — không phải file log.

## Git khi làm task

Trừ khi user nói khác:

1. Checkout `main`
2. `git pull`
3. Tạo branch `feat/…` hoặc `fix/…`
4. Làm trên branch đó

Không commit / push trừ khi được yêu cầu.

## Khi sửa UI hoặc bot

Build ra `app\` rồi bảo user chạy `app\GtaMiniGameBot.exe`. Không verify bằng browser.

## Cấu trúc

| Thư mục | Việc gì |
|---|---|
| `src/GtaMiniGameBot/` | Vỏ app: `Program.cs`, `HomeForm.cs` |
| `src/GtaMiniGameBot/Jobs/` | Từng job: Oil, Fishing, Miner, Wood, Electric (+ Nav) |
| `src/GtaMiniGameBot/Utils/` | Tab Tiện ích |
| `src/GtaMiniGameBot/Core/` | Input, vision, capture DXGI/GDI, log, discord, overlay |
| `src/GtaMiniGameBot/Ui/` | Theme / control dùng chung |
| `app/` | Exe chạy hàng ngày |
| `packaging/defaults/` | ROI / icon mang đi share |
| `tools/` | Script build / kiểm tra exe đang chạy |
| `recordings/` | Ảnh demo / `--verify*` |

## Việc không làm

- Không tự tắt app đang chạy
- Không viết log / dump vào repo
- Không đụng `--verify*` trừ khi task liên quan
- Không sửa file plan nếu user dặn
