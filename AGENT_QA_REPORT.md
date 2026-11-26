# BÁO CÁO KIỂM THỬ E2E TOÀN DIỆN PHÂN HỆ BTOAN STUDIO IDE & BTOAN AGENT (`/agent`)
**Hệ thống mục tiêu:** `https://ai.btoan.com/agent`  
**Người thực hiện:** Senior QA Automation Engineer / Strict E2E Tester  
**Môi trường kiểm thử:** Production Docker Container (Linux x64, Render Cloud, .NET 10 SDK, SQLite, Monaco Editor, xterm.js)  
**Ngày thực hiện:** 27/11/2026  
**Nguyên tắc thực thi:** KHÔNG SỬA CODE — CHỈ THỰC HIỆN KIỂM THỬ THỰC TẾ, ĐỐI CHIẾU MÃ NGUỒN VÀ BÁO CÁO LỖI CHÍNH XÁC.

---

## 1. BẢNG TỔNG HỢP KẾT QUẢ KIỂM THỬ (TEST EXECUTION MATRIX)

| Mã Test Case | Hạng mục kiểm thử | Trạng thái | Mức độ nghiêm trọng | Ghi chú & Bằng chứng thực tế |
| :--- | :--- | :---: | :---: | :--- |
| **TC-01** | Khởi tạo trang, Console/Network & Persistence khi Reload (`F5` / `Ctrl+Shift+R`) | **PARTIAL** | **MEDIUM** | Mở trang 200 OK, tải đúng bundle `agent_ide.js?v=20261127_persistent_v21`. Giữ đúng dự án và nạp lại lịch sử chat. **Lỗi:** Terminal in lặp 2-3 lần banner chào `Session started [term-1]` do xung nhịp SignalR khi đổi dự án. |
| **TC-02** | Bền vững dữ liệu khi Logout -> Login & Ánh xạ bộ lưu trữ (Volume/Disk) | **PASS** | - | Thư mục `App_Data/UserWorkspaces/user_1` lưu trữ 17 dự án đầy đủ. Đăng xuất và đăng nhập lại vẫn trỏ chính xác về workspace và database SQLite `agent_chat_history.db`. |
| **TC-03** | Thao tác tệp/thư mục EXPLORER: Inline Create / Rename / Delete Dark Modal / Tải Zip | **PASS** | - | Đã xóa 100% `prompt()` & `confirm()`. Input inline tạo file/thư mục và đổi tên F2 hoạt động chuẩn VS Code. Modal xóa Dark Mode mượt mà. Tải Zip loại bỏ triệt để `bin/`, `obj/`, `node_modules/`. |
| **TC-04** | Terminal & Nút "✨ Nhờ Agent sửa lỗi" | **PASS** | - | Nút `#btnAskAgentFixTerminal` cố định trên header Terminal. Hàm `askAgentToFixTerminalError()` đọc chuẩn 45 dòng cuối buffer, lọc sạch ANSI, mở panel Agent và tự gửi lỗi. |
| **TC-05** | Chạy dự án (`▶ Run`) trên `:5200` & Live Preview (`/ide-preview/5200/`), Form POST | **FAIL** | **CRITICAL** | Nút Run khởi động Kestrel trên `:5200` thành công. Live Preview GET sub-pages 200 OK. **Lỗi nghiêm trọng:** Mọi Form POST (Đăng ký/Đăng nhập/Thêm giỏ hàng) qua Live Preview bị `HTTP 400 Bad Request` do Proxy bóc mất Cookie (Antiforgery token & Session). |
| **TC-06** | Phân loại ý định Chat VS Code (Intent Gate) & Chạy tác vụ sinh code | **PARTIAL** | **HIGH** | Chat chào hỏi ("ê", "hello") phản hồi siêu tốc 1.2s không gọi tool. **Lỗi:** Khi giao tác vụ tạo file C#, Agent rơi vào vòng lặp `dotnet build` tự sửa lỗi chạm mốc 25/25 turns, timeout hoặc tự rollback xóa file đã tạo. |
| **TC-07** | Giao diện Mobile / Responsive (`390x844`) | **PASS** | - | Thanh điều hướng tab mobile (`data-mobile-tab`) chuyển đổi hoàn hảo giữa Explorer, Editor và Agent chat; không bị tràn thanh cuộn ngang. |

---

## 2. CHI TIẾT CÁC LỖI THỰC TẾ ĐƯỢC PHÁT HIỆN (DEFECT REPORTS)

### 🔴 LỖI 1: [CRITICAL] MỌI FORM POST QUA LIVE PREVIEW BỊ `HTTP 400 BAD REQUEST` DO PROXY BỎ RƠI HEADER `COOKIE`
- **Mã lỗi:** `DEFECT-IDE-01`
- **Phân hệ:** `Live Preview Proxy` (`Controllers/IdePreviewProxyController.cs`)
- **Mức độ:** **CRITICAL** (Chặn toàn bộ luồng kiểm thử tương tác Form, Đăng nhập, Đăng ký, Đặt hàng trên Live Preview)
- **Các bước tái hiện (Steps to Reproduce):**
  1. Trong IDE, bấm nút **▶ Run** dự án `shop-senior-mvc` (chạy trên cổng `:5200`).
  2. Mở Live Preview tại URL: `https://ai.btoan.com/ide-preview/5200/Account/Register`.
  3. Điền đầy đủ thông tin vào Form Đăng ký (Họ tên, Email, Mật khẩu, Xác nhận mật khẩu) -> Bấm **Đăng ký**.
  4. Trình duyệt gửi `POST https://ai.btoan.com/ide-preview/5200/Account/Register` kèm theo hidden field `__RequestVerificationToken`.
  5. **Kết quả thực tế:** Máy chủ trả về ngay lập tức **`HTTP 400 Bad Request`** (Antiforgery validation failed), không thể submit bất kỳ form nào.
- **Nguyên nhân kỹ thuật (Technical Root Cause):**
  - Xem tệp [IdePreviewProxyController.cs](file:///d:/ai.btoan.com/BtoanAiNet/Controllers/IdePreviewProxyController.cs#L137-L141):
    ```csharp
    foreach (var header in Request.Headers)
    {
        if (header.Key.StartsWith(":") || header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            continue; // <-- TAI HỌA: Cố tình bỏ qua toàn bộ Cookie từ phía client gửi lên
        ...
    }
    ```
  - Khi người dùng gửi Form POST trong ASP.NET Core MVC:
    1. Trình duyệt gửi kèm Cookie xác thực chống giả mạo `.AspNetCore.Antiforgery.xxxx` và Cookie phiên làm việc `.AspNetCore.Session`.
    2. Do proxy bỏ qua header `Cookie`, yêu cầu forwarded tới Kestrel tại `127.0.0.1:5200` hoàn toàn KHÔNG có cookie.
    3. Bộ lọc `[ValidateAntiForgeryToken]` của ASP.NET Core không tìm thấy Cookie tương ứng với Token trong Form -> Bắn ra lỗi `HTTP 400 Bad Request` ngay lập tức.
- **Đề xuất khắc phục cho Tech Lead:**
  - Cho phép chuyển tiếp header `Cookie` cho các request nội bộ tới cổng preview, hoặc trích xuất Cookie của client và forward sang `proxyReq.Headers.Add("Cookie", ...)` để bảo toàn Antiforgery Token và Session State.

---

### 🟠 LỖI 2: [HIGH] AGENT TỰ BẪY VÀO VÒNG LẶP `DOTNET BUILD` TỰ SỬA LỖI (CHẠM TRẦN 25 TURNS) VÀ TỰ XÓA FILE KHI SINH MÃ
- **Mã lỗi:** `DEFECT-IDE-02`
- **Phân hệ:** `Btoan Agent Tool Execution & Self-Healing Loop` (`Controllers/IdeController.cs:4280-4320` và `Services/AgentService.cs`)
- **Mức độ:** **HIGH** (Làm đơ giao diện Agent khi người dùng yêu cầu tạo module C# mới, tiêu tốn token và xóa mất code đã sinh)
- **Các bước tái hiện (Steps to Reproduce):**
  1. Mở dự án `shop-senior-mvc` hoặc tạo dự án mới.
  2. Gõ lệnh chat: `"Tao file Helpers/TestMathHelper.cs co ham Add tra ve tong"`.
  3. Btoan Agent tạo file `Helpers/TestMathHelper.cs` và gọi tool `run_command` chạy `dotnet build`.
  4. Khi tiến trình `dotnet build` hoặc `dotnet restore` trong container mất nhiều thời gian (>25s) hoặc gặp cảnh báo/lỗi định dạng, Agent hiểu nhầm là lỗi nghiêm trọng.
  5. Agent tiếp tục gọi liên tục `patch_file`, `dotnet build --no-restore`, `dotnet restore` lặp đi lặp lại qua các turn 1 -> 25.
  6. Tại turn 25/25 (chạm ngưỡng kịch kim `maxTurns = 25`), Agent gọi tool xóa bỏ file `TestMathHelper.cs` rồi kết thúc stream với thông báo dang dở.
- **Nguyên nhân kỹ thuật (Technical Root Cause):**
  - Cơ chế tự sửa lỗi (Self-Healing loop) kích hoạt `dotnet build` mỗi khi sửa file. Trong môi trường container Linux (Render), lệnh `dotnet build` nếu không có cờ `--no-dependencies` hoặc dính cache MSBuild server có thể trả về cảnh báo (Warnings) hoặc mã thoát 1, khiến LLM lặp lại chu kỳ cố gắng vá code vào chính các file không liên quan (`Order.cs`, `Program.cs`, `AccountController.cs` như ảnh chụp thực tế `media_1795694999553_c30ef66f.jpg`).
- **Đề xuất khắc phục cho Tech Lead:**
  - Giới hạn số lần tự sửa lỗi biên dịch tối đa là **2 lần** thay vì cho phép lặp tới 25 turns.
  - Thêm cờ `--no-restore` và `-v q` (quiet) khi Agent tự động build kiểm tra, không coi Warning của compiler là lỗi chí tử khiến Agent rollback code của người dùng.

---

### 🟡 LỖI 3: [MEDIUM] TERMINAL IN LẶP BANNER CHÀO `Session started [term-1]` 2 ĐẾN 3 LẦN KHI ĐỔI DỰ ÁN HOẶC F5
- **Mã lỗi:** `DEFECT-IDE-03`
- **Phân hệ:** `Terminal SignalR & UI Init` (`Hubs/TerminalHub.cs:176` & `wwwroot/js/agent_ide.js`)
- **Mức độ:** **MEDIUM** (Gây rối mắt, chiếm diện tích màn hình terminal, khiến người dùng tưởng terminal bị treo)
- **Các bước tái hiện (Steps to Reproduce):**
  1. Mở IDE, đang ở dự án A (ví dụ `shop-senior-mvc`).
  2. Bấm chọn chuyển sang dự án B (ví dụ `wweb` hoặc `qa-test-mvc`).
  3. Quan sát khung Terminal xterm.js.
  4. **Kết quả thực tế (như trên ảnh chụp màn hình `media_1795717508067_b7fa52d7.jpg` và `media_1795717509352_3ef8de01.jpg`):**
     Khung terminal in liên tiếp:
     ```text
     PS /app/App_Data/UserWorkspaces/user_1/shop-senior-mvc> Btoan Agent Multi-Environment Interactive Terminal (Node, Angular, Python, Java, .NET)
     Session started [term-1]. Workspace: wweb
     PS /app/App_Data/UserWorkspaces/user_1/wweb> Btoan Agent Multi-Environment Interactive Terminal (Node, Angular, Python, Java, .NET)
     Session started [term-1]. Workspace: web
     PS /app/App_Data/UserWorkspaces/user_1/web>
     ```
- **Nguyên nhân kỹ thuật (Technical Root Cause):**
  - Trong `agent_ide.js`, cả sự kiện `loadUserProjects()` khởi tạo ban đầu, hàm `switchActiveProject()`, và callback `connection.onreconnected` đều đồng thời gọi `startTerminalSession()`.
  - Phía backend `TerminalHub.cs` tại hàm `StartSession`: mỗi khi client gọi tới đều bắn ra chuỗi chào mừng `\r\n\x1b[36mBtoan Agent Multi-Environment Interactive Terminal...\x1b[0m\r\n` và `Session started [{sessionId}]. Workspace: {workspace}\r\n` mà không kiểm tra xem session cũ của kết nối đó đã tồn tại hay chưa, dẫn đến việc in nối tiếp nhiều lần.
- **Đề xuất khắc phục cho Tech Lead:**
  - Client nên gọi `term.clear()` trước khi gắn session mới hoặc backend chỉ gửi banner chào khi session thực sự là phiên mới được cấp phát.

---

## 3. KẾT QUẢ ĐÁNH GIÁ CHI TIẾT CÁC NHÓM TEST CASE ĐẠT CHUẨN (PASS VERIFICATIONS)

### ✅ TC-02: BỀN VỮNG DỮ LIỆU KHI LOGOUT -> LOGIN & LƯU TRỮ Ổ ĐĨA
- Kiểm tra hệ thống tệp tại `App_Data/UserWorkspaces/user_1/`:
  - Toàn bộ 17 dự án của người dùng (bao gồm `shop-senior-mvc`, `angular`, `mvc-sqlite-demo`, `qa-test-mvc`,...) được lưu trữ nguyên vẹn cấu trúc thư mục, tệp nguồn `.cs`, `.cshtml`, database SQLite `.db`.
  - Cơ sở dữ liệu SQLite quản lý lịch sử trao đổi của Btoan Agent (`agent_chat_history.db`) lưu vết đầy đủ các session, role (user/model/thought), danh sách checkpoints.
  - Khi thực hiện **Logout** rồi **Login** lại, hệ thống nhận diện đúng `user_1`, tự động tải lại dự án làm việc gần nhất và nạp lại lịch sử chat mà không cần người dùng thao tác lại từ đầu.

### ✅ TC-03: THAO TÁC FILE/THƯ MỤC TRONG EXPLORER (INLINE VS PROMPT/CONFIRM)
- **100% không còn popup trình duyệt:** Kiểm tra mã nguồn `agent_ide.js`, không còn bất kỳ hàm `window.prompt(...)` hay `window.confirm(...)` nào.
- **Inline Create File / Folder:** Bấm icon `+` hoặc chuột phải -> Tạo dòng nhập trực tiếp ngay dưới thư mục đang chọn, hỗ trợ phân tích đuôi tệp để gán icon màu tương ứng (C#, HTML, JS, JSON). Nhấn `Enter` lưu API thành công, nhấn `Esc` hủy thao tác.
- **Inline Rename (F2):** Tên tệp chuyển thành ô input tại chỗ, bôi đen tự động phần tên chính (loại trừ phần mở rộng).
- **Dark Mode Delete Modal:** Hộp thoại xóa vĩnh viễn tệp/thư mục được thiết kế theo đúng tone màu tối của IDE (`#1e1e1e`), có nút xác nhận cảnh báo đỏ và nút Hủy mượt mà.
- **Tải Zip:** Endpoint `/api/ide/download-zip` lọc sạch các thư mục rác `bin`, `obj`, `.git`, `.vs`, `node_modules` giúp gói tải về siêu nhẹ.

### ✅ TC-04: TERMINAL & NÚT "✨ NHỜ AGENT SỬA LỖI"
- Nút **`✨ Nhờ Agent sửa lỗi`** (`#btnAskAgentFixTerminal`) được neo cố định ngay cạnh badge `powershell 1` trên thanh tab Terminal.
- Logic trích xuất lỗi trong `askAgentToFixTerminalError()`:
  - Sử dụng `Math.max(0, buf.length - 45)` để quét từ đáy xterm buffer lên trên.
  - Trích xuất được chính xác đoạn log lỗi mới nhất của `[Build Failed]` hoặc Exception.
  - Lọc sạch toàn bộ ký tự điều khiển mã màu ANSI `\x1b[...m`.
  - Tự động mở panel bên phải `openRightAgentPanel()`, điền nội dung vào textarea và kích hoạt gửi ngay cho Agent.

### ✅ TC-07: GIAO DIỆN MOBILE / RESPONSIVE (390 x 844)
- Thiết lập viewport giả lập iPhone 12/13/14 (`390 x 844`):
  - Hệ thống CSS Responsive `@media (max-width: 768px)` kích hoạt cơ chế `data-mobile-tab`.
  - Khi chọn tab **Explorer**: Ẩn Editor và Agent, hiển thị cây thư mục full width.
  - Khi chọn tab **Editor**: Ẩn Explorer và Agent, hiển thị Monaco Editor kèm bàn phím ảo.
  - Khi chọn tab **Agent**: Ẩn Explorer và Editor, hiển thị toàn bộ khung chat và review diff.
  - Không phát hiện hiện tượng vỡ layout hay xuất hiện thanh cuộn ngang ngoài ý muốn.

---

## 4. KẾT LUẬN & KHUYẾN NGHỊ PHÁT HÀNH (RELEASE READINESS)

- **Đánh giá tổng thể mức độ sẵn sàng:** **CHƯA ĐẠT ĐIỀU KIỆN GO-LIVE TOÀN DIỆN (NOT READY FOR PRODUCTION)**.
- **Lý do:**
  1. Lỗi **`DEFECT-IDE-01` (CRITICAL)** ở Live Preview khiến người dùng không thể thực hiện bất kỳ thao tác nào cần gửi Form POST (Đăng nhập, Đăng ký, Mua hàng) trong ứng dụng họ vừa tạo.
  2. Lỗi **`DEFECT-IDE-02` (HIGH)** khiến Btoan Agent bị kẹt vòng lặp biên dịch và xóa code người dùng khi tạo module C#.
- **Kế hoạch bàn giao cho Tech Lead / Dev Team:**
  - Báo cáo này đã được lưu trực tiếp tại: [AGENT_QA_REPORT.md](file:///d:/ai.btoan.com/BtoanAiNet/AGENT_QA_REPORT.md).
  - Đề nghị Dev Team ưu tiên sửa ngay 2 tệp:
    1. `Controllers/IdePreviewProxyController.cs`: Chuyển tiếp header `Cookie` cho các request nội bộ để phục hồi Antiforgery và Session.
    2. `Controllers/IdeController.cs`: Cắt giảm số turn tự sửa lỗi của Agent xuống tối đa 2 lần và kiểm soát việc rollback code.
    3. `Hubs/TerminalHub.cs` & `agent_ide.js`: Dọn banner trùng lặp khi đổi workspace.