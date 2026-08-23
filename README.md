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

Đóng gói share: `tools/build-portable.ps1` (zip trong `dist\`, không đổi `app\`).

## Dữ liệu

- Config / ROI / icon: `%AppData%\GtaMiniGameBot` (`AppPaths`).
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
| `src/GtaMiniGameBot/` | Code |
| `app/` | Exe chạy hàng ngày |
| `packaging/defaults/` | ROI / icon mang đi share |
| `tools/` | Script build / kiểm tra exe đang chạy |
| `recordings/` | Ảnh demo / `--verify*` |

## Việc không làm

- Không tự tắt app đang chạy
- Không viết log / dump vào repo
- Không đụng `--verify*` trừ khi task liên quan
- Không sửa file plan nếu user dặn
