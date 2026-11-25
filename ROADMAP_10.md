# ROADMAP 10 PROMPT: NÂNG CẤP BTOAN STUDIO IDE (`/agent`) ĐẠT CHUẨN 100% NHƯ ANTIGRAVITY IDE

## QUY TẮC THỰC THI TỰ ĐỘNG CHO ANTIGRAVITY AGENT (OVERNIGHT AUTONOMOUS MODE)
1. **Phạm vi làm việc duy nhất:** Chỉ thực hiện trên thư mục chạy thật `D:\ai.btoan.com\BtoanAiNet` và cập nhật thẳng lên dịch vụ phục vụ domain production `https://ai.btoan.com` (bỏ qua hoàn toàn `D:\github\ai-project`).
2. **Chạy xuyên suốt không dừng hỏi lại:** Thực hiện tuần tự từ **Prompt 1/10** đến **Prompt 10/10**. Sau khi hoàn tất và kiểm chứng build thành công từng Prompt, đánh dấu `[x]` vào mục tương ứng trong file này và **chuyển tiếp ngay sang Prompt kế tiếp** mà không dừng lại chờ người dùng xác nhận.
3. **Bảo toàn cấu hình Production:** Tuyệt đối không xóa hoặc ghi đè làm mất `Gemini:ApiKey`, `GoogleAuth:ClientId` và `GoogleAuth:ClientSecret` trong `appsettings.Development.json` và `appsettings.Production.json`.
4. **Kiểm chứng thực tế:** Sau mỗi Prompt có thay đổi code C#, chạy `dotnet build` đảm bảo **0 Error, 0 Warning**; tại Prompt 10/10 bắt buộc khởi động lại service production và dùng **Browser Agent** kiểm thử trực tiếp trên `https://ai.btoan.com/agent`.

---

## TIẾN ĐỘ TỔNG THỂ
- [x] **Prompt 1/10:** Vá dứt điểm lỗi treo `(288s)`, chuẩn hóa Model Routing & luồng SSE Streaming bất tử
- [x] **Prompt 2/10:** Thiết kế giao diện Agent chuẩn Antigravity (`Thought` thu gọn, Live Task Card, Action Pills)
- [x] **Prompt 3/10:** Hoàn thiện bộ công cụ đọc, tìm kiếm và sửa code chính xác (`patch_file`, `grep_search`, thống nhất Workspace)
- [x] **Prompt 4/10:** Trao quyền chạy lệnh Terminal (`run_command`) & fix triệt để nút đỏ Stop cổng động (`5200+`)
- [x] **Prompt 5/10:** Xây dựng vòng lặp Tự Chủ & Tự Sửa Lỗi biên dịch (`Autonomous Self-Healing Build Loop`)
- [x] **Prompt 6/10:** Hoàn thiện công cụ tạo ảnh nhị phân thật (`generate_image`) & trình xem ảnh chuẩn trong IDE
- [x] **Prompt 7/10:** Lưu trữ bền vững vào SQLite (hết mất dữ liệu khi `F5`) & tính năng Khôi phục Checkpoint
- [x] **Prompt 8/10:** Kích hoạt ngữ cảnh `@file`, `/commands`, nút nhờ Agent sửa lỗi Terminal & Diff View
- [x] **Prompt 9/10:** Chuẩn hóa khởi tạo dự án (Visual Studio / Angular CLI), thu gọn thư mục gốc & thông suốt Live Preview
- [x] **Prompt 10/10:** Cố định Layout Editor/Terminal, Build sạch 0 lỗi, Restart `ai.btoan.com` & Chạy kiểm thử thực tế

---

## PROMPT 1/10: VÁ DỨT ĐIỂM LỖI TREO `(288S)`, CHUẨN HÓA MODEL ROUTING & LUỒNG SSE REAL-TIME

Kiểm tra `Services/GeminiService.cs`, `Services/AgentService.cs`, `Controllers/AgentController.cs` và `wwwroot/js/agent_ide.js`:

1. **Chặn đứng lỗi treo vô hạn `(288s)` khi gọi Gemini API:**
   - Sửa ánh xạ tên model từ dropdown trên `/agent`:
     - `Btoan 3.5 Flash` và `Btoan 2.5 Flash` -> gọi trực tiếp `gemini-2.5-flash` (dùng `thinkingConfig: { thinkingBudget: 1024 }` ở bước lập kế hoạch và `thinkingBudget: 0` ở các bước sinh mã nguồn để tốc độ phản hồi dưới 5 giây/file).
     - `Btoan 2.5 Pro` -> gọi trực tiếp `gemini-2.5-pro`.
   - Ép `SocketsHttpHandler` ưu tiên kết nối IPv4 (`AddressFamily.InterNetwork`) để tránh treo bắt tay IPv6 trên Linux/Docker.
   - Tạo `CancellationTokenSource` **riêng biệt tối đa 35 giây cho mỗi lượt gọi HTTP** tới Gemini API (không dùng chung một token 300 giây cho cả vòng lặp). Nếu quá 35 giây hoặc gặp mã lỗi `429`, `500`, `502`, `503`, lập tức hủy request đó, nghỉ `1.5s` thử lại tối đa 1 lần rồi chuyển ngay sang model kế tiếp trong chuỗi dự phòng: `gemini-2.5-flash` -> `gemini-2.5-pro` -> `gemini-2.0-flash` -> `gemini-flash-lite-latest`.
   - Nếu toàn bộ chuỗi model đều thất bại, **bắt buộc bắn sự kiện `event: error` và đóng luồng SSE ngay lập tức**, không bao giờ để frontend kẹt đồng hồ đếm giây vô hạn.

2. **Chuẩn hóa luồng Server-Sent Events (SSE) chống ngắt kết nối Cloudflare:**
   - Ngay đầu endpoint xử lý chat của `/agent`, tắt buffering:
     ```csharp
     HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
     Response.ContentType = "text/event-stream; charset=utf-8";
     Response.Headers["Cache-Control"] = "no-cache, no-transform";
     Response.Headers["X-Accel-Buffering"] = "no";
     ```
   - Chạy tác vụ nền gửi comment SSE giữ kết nối **`: keepalive\n\n`** kèm `await Response.Body.FlushAsync()` **mỗi 3 giây một lần** (đồng bộ bằng `SemaphoreSlim`) để Cloudflare không cắt ngang HTTP/2 stream khi Agent viết file dài.
   - Kích hoạt nút **Stop (`■`)** ở khung chat Agent: khi người dùng bấm dừng, hủy ngay `CancellationToken` của phiên Agent ở backend, giữ nguyên các file đã tạo trên ổ đĩa và cập nhật trạng thái đã dừng trên UI.

---

## PROMPT 2/10: NÂNG CẤP GIAO DIỆN BTOAN AGENT GIỐNG ANTIGRAVITY IDE (`THOUGHT` ACCORDION + LIVE TASK CARD + ACTION PILLS)

Kiểm tra `Views/Agent/Index.cshtml`, `wwwroot/css/agent_ide.css` và `wwwroot/js/agent_ide.js`:

1. **Khối suy nghĩ thu gọn (`Thought for Xs`) giống Antigravity:**
   - Khi Agent đang phân tích yêu cầu hoặc lập kế hoạch, hiển thị hộp thu gọn có mũi tên `› Thought` kèm đồng hồ đếm giây thực tế (`Thought... 3s`); khi chuyển sang bước thực thi thì dừng đồng hồ và hiển thị `› Thought for 4s`. Bấm vào tiêu đề có thể mở rộng/thu gọn nội dung tóm tắt hướng phân tích của Agent.

2. **Thẻ Tiến Độ Nhiệm Vụ Trực Quan (Live Task & Plan Card):**
   - Không bao giờ đổ raw JSON plan vào bong bóng chat. Hiển thị một thẻ **Task Card** hiện đại ngay trong luồng hội thoại gồm:
     - Tiêu đề nhiệm vụ & bộ đếm tiến độ (`2/5 bước`).
     - Danh sách các bước con cập nhật theo thời gian thực qua SSE với 3 trạng thái rõ nét:
       - `[ ]` Chờ thực hiện (màu xám nhạt)
       - `[⟳]` Đang chạy (spinner xoay xanh + mô tả trực quan như *"Đang viết style.css..."*)
       - `[✓]` Hoàn tất (dấu tick xanh lá)

3. **Thẻ hành động từng tệp (File Action Pills) bấm tương tác trực tiếp:**
   - Mỗi lần Agent đọc, tạo hoặc sửa một tệp, hiển thị ngay một pill gọn đẹp trong luồng chat:
     - `👁 Đã đọc Program.cs`
     - `✨ Đã tạo index.html (+142 dòng)` — khi bấm vào, mở ngay tab `index.html` trên Monaco Editor.
     - `✏️ Đã sửa HomeController.cs (+28 / -5)` — khi bấm vào, mở file và nhảy tới vùng code vừa thay đổi.

---

## PROMPT 3/10: HOÀN THIỆN BỘ CÔNG CỤ ĐỌC, TÌM KIẾM VÀ SỬA CODE THÔNG MINH CHO AGENT

Kiểm tra `Services/AgentService.cs` và `Controllers/IdeController.cs`:

1. **Chuẩn hóa bộ công cụ thao tác mã nguồn trong `functionDeclarations`:**
   - `list_dir(path?)`: Liệt kê cấu trúc cây thư mục dự án kèm dung lượng (bytes) và số dòng của từng tệp.
   - `read_file(path, start_line?, end_line?)`: Đọc nội dung tệp kèm **số thứ tự từng dòng (`1: ...`, `2: ...`)** để Agent định vị chính xác vùng code cần chỉnh sửa.
   - `grep_search(query, include_pattern?)`: Tìm kiếm chuỗi, tên hàm, tên class trên toàn bộ mã nguồn của dự án hiện tại.
   - `write_file(path, content)`: Tạo tệp mới hoặc ghi đè tệp hoàn chỉnh. **Bắt buộc có Empty Content Guard:** nếu tham số `content` bị `null` hoặc trống trắng, tuyệt đối không ghi file 0 byte mà trả lỗi ngược lại cho Gemini yêu cầu truyền đầy đủ nội dung code.
   - `patch_file(path, target_block, replacement_block)`: Sửa chính xác một khối code bên trong các file lớn (> 120 dòng) mà không cần xuất lại toàn bộ file — tăng tốc độ sửa code gấp 3 lần và tránh cắt cụt token.
   - `delete_file(path)`: Xóa tệp thừa/tệp rác trong dự án.

2. **Thống nhất tuyệt đối đường dẫn Workspace (Chấm dứt lỗi `404 Failed to load file content`):**
   - Dùng chung một hàm `ResolveSafeProjectPath(userId, projectName, relativePath)` giữa `AgentService` và `IdeController` để cả hai bên luôn đọc/ghi trên cùng một thư mục `App_Data/UserWorkspaces/user_{userId}/{projectName}`.
   - Tự động chuẩn hóa tham số `path` (loại bỏ `./`, `/`, hoặc tiền tố lặp tên dự án như `shop/index.html` -> `index.html`) và chặn Path Traversal (`../`).

---

## PROMPT 4/10: TRAO QUYỀN CHẠY LỆNH TERMINAL (`run_command`) & FIX TRIỆT ĐỂ NÚT ĐỎ STOP CỔNG ĐỘNG (`5200+`)

Kiểm tra `Services/AgentService.cs`, `Services/TerminalService.cs` (hoặc `TerminalHub.cs`) và `Controllers/IdeController.cs`:

1. **Cài đặt công cụ `run_command(command, cwd?, timeout_seconds?)` cho Btoan Agent:**
   - Cho phép Agent trực tiếp chạy các lệnh CLI bên trong thư mục dự án hiện tại của user (ví dụ: `dotnet build`, `dotnet add package Microsoft.EntityFrameworkCore.Sqlite`, `npm install`, `npm run build`, `python -m py_compile ...`).
   - Chặn các lệnh nguy hiểm ngoài workspace (`rm -rf /`, `shutdown`, truy cập ngoài `App_Data/UserWorkspaces/user_{userId}`).
   - Thu thập đầy đủ `ExitCode`, `Stdout` và `Stderr` (giữ nguyên toàn bộ thông báo lỗi compiler `error CS...` / `TS...`) trả về cho Agent.
   - Phát sự kiện SSE `command_run` để khung chat hiển thị khối lệnh Terminal thu gọn kèm trạng thái `Exit Code: 0` (xanh) hoặc `Exit Code: 1` (đỏ).

2. **Fix triệt để nút đỏ Stop (`■`) trên thanh công cụ Editor:**
   - Frontend (`agent_ide.js`): Khi người dùng bấm nút **Stop (`■`)**, gửi chính xác số cổng đang chạy (`activeLivePort`, ví dụ `5200`) lên backend (không phụ thuộc vào tên project đang chọn trên dropdown).
   - Backend (`IdeController.cs`): Hủy toàn bộ cây tiến trình gắn với cổng đó (`process.Kill(entireProcessTree: true)`) và trên Linux/Docker chạy thêm lệnh dọn sạch tiến trình giữ cổng (`fuser -k {port}/tcp` hoặc tìm PID qua `/proc` / `ss -tlnp` và `kill -9`), giải phóng cổng 100% ngay lập tức.

---

## PROMPT 5/10: XÂY DỰNG VÒNG LẶP TỰ CHỦ & TỰ SỬA LỖI BIÊN DỊCH (`AUTONOMOUS SELF-HEALING LOOP`)

Kiểm tra `Services/AgentService.cs`:

1. **Mở rộng vòng lặp Agent lên tối đa 20 bước (`while (step < 20)`):**
   - Cập nhật `SystemInstruction` chuẩn kiến trúc sư phần mềm cho Btoan Agent theo quy trình 4 pha của Antigravity:
     - **Pha 1 (Explore):** Luôn gọi `list_dir` (và `read_file` các tệp cấu hình chính như `Program.cs`, `.csproj`, `package.json`, `index.html`) trước khi sửa một dự án đang có sẵn.
     - **Pha 2 (Plan):** Lập danh sách các file cần tạo/sửa ngắn gọn, thực tế.
     - **Pha 3 (Implement):** Viết mã nguồn hoàn chỉnh 100%, giao diện thẩm mỹ cao (CSS variables, Flexbox/Grid, responsive mobile, hiệu ứng mượt, nghiệp vụ chạy thật), **cấm tuyệt đối** để placeholder `// TODO`, `...` hay hàm rỗng. Gọi `write_file` / `patch_file` tuần tự từng file để SSE cập nhật liên tục lên UI.
     - **Pha 4 (Verify & Self-Heal):** Với dự án `.NET` (`.csproj`) hoặc `Node/Angular/TypeScript` (`package.json`), **bắt buộc phải gọi `run_command` chạy `dotnet build` hoặc `npm run build` trước khi kết thúc**. Nếu `ExitCode != 0`, Agent phải tự đọc chi tiết lỗi biên dịch, gọi `read_file` + `patch_file` để sửa lỗi và chạy build lại cho tới khi đạt `0 Error(s)`.

2. **Chống dừng sớm (Premature Stop Guard):**
   - Nếu Agent mới chỉ tạo 1–2 file trong danh sách kế hoạch hoặc chưa chạy bước build kiểm chứng cho dự án biên dịch mà đã định trả lời kết thúc, hệ thống tự động tiêm nhắc nhở nội bộ bắt buộc Agent hoàn tất toàn bộ các file còn lại và chạy build kiểm tra trước khi chốt câu trả lời cuối cùng.

---

## PROMPT 6/10: HOÀN THIỆN CÔNG CỤ TẠO ẢNH NHỊ PHÂN THẬT (`generate_image`) & HIỂN THỊ ẢNH TRONG IDE

Kiểm tra `Services/AgentService.cs`, `Controllers/IdeController.cs` và `wwwroot/js/agent_ide.js`:

1. **Phân loại thông minh khi nào cần gọi `generate_image` (`SystemInstruction`):**
   - Chỉ gọi `generate_image` khi yêu cầu có yếu tố hình ảnh/thương mại/nghệ thuật (web bán hàng/shop, landing page, blog, nhà hàng, thiệp/tỏ tình, portfolio).
   - Với các ứng dụng công cụ/tính toán thuần túy (web tính lương, tính điểm, quản lý công việc, máy tính, dashboard số liệu), tập trung 100% vào giao diện CSS/SVG sắc nét và logic tính toán chuẩn xác, không tạo ảnh thừa.

2. **Tạo dữ liệu ảnh nhị phân PNG/JPEG thật 100% (Cấm ghi mã `<svg>` vào file `.jpg`/`.png`):**
   - Khi `generate_image(prompt, output_path, aspect_ratio)` được gọi:
     - Gọi API tạo ảnh của Google (`gemini-2.5-flash-image-preview` / `imagen-3.0-generate-002`) với timeout chặt `15 giây`.
     - Nếu thành công, giải mã Base64 thành mảng byte nhị phân và lưu đúng định dạng ảnh xuống ổ đĩa.
     - Nếu API ảnh lỗi hoặc quá 15 giây, sử dụng thư viện đồ họa .NET (`SixLabors.ImageSharp` hoặc `SkiaSharp` — thêm NuGet package nếu chưa có) để vẽ tấm ảnh minh họa nghệ thuật (gradient màu theo chủ đề prompt, hình khối trang trí, tiêu đề sắc nét) và **xuất ra file nhị phân `.png` / `.jpg` thật** khớp với phần mở rộng của `output_path` (tuyệt đối không ghi văn bản XML `<svg>` vào file `.jpg`/`.png`).

3. **Xem ảnh mượt mà trong Explorer & Live Preview:**
   - Đảm bảo endpoint `GET /api/ide/raw?project=...&file=...&user_id=...` trả về đúng `Content-Type` (`image/jpeg`, `image/png`, `image/webp`, `image/svg+xml`).
   - Trong `agent_ide.js`: khi người dùng bấm vào file ảnh trong `EXPLORER`, mở thẳng khung xem trước hình ảnh trực quan (hiển thị ảnh từ `/api/ide/raw`, kích thước, nút tải về), không gọi API text `/api/ide/file-content`.

---

## PROMPT 7/10: LƯU TRỮ LỊCH SỬ CHAT BỀN VỮNG (SQLITE) & TÍNH NĂNG KHÔI PHỤC CHECKPOINT

Kiểm tra `Data/AppDbContext.cs`, `Controllers/AgentController.cs`, `Services/AgentService.cs` và `wwwroot/js/agent_ide.js`:

1. **Chống mất dữ liệu khi Reload (`F5`):**
   - Tạo entity/bảng `AgentChatMessages` (`Id`, `UserId`, `ProjectName`, `Role`, `Content`, `ThoughtText`, `PlanJson`, `ActionsJson`, `CheckpointId`, `CreatedAt`) trong SQLite và tự động tạo bảng lúc startup nếu chưa tồn tại.
   - Lưu toàn bộ tin nhắn, nội dung `Thought`, `Plan` và `File Action Pills` vào DB ngay trong quá trình chạy.
   - Khi tải lại trang `/agent` (`F5`) hoặc chuyển dự án, tự động khôi phục đúng dự án đang mở (`lastProject`), danh sách các tab đang mở (`openTabs`) với nội dung mới nhất từ ổ đĩa, và toàn bộ lịch sử trò chuyện của dự án đó. **Tuyệt đối không** ghi đè file mặc định lên dự án đã tồn tại khi reload trang.
   - Kích hoạt nút **`+` (New Chat)** ở góc trên khung Btoan Agent để tạo phiên hội thoại mới sạch sẽ cho dự án hiện tại.

2. **Tính năng Checkpoint & Restore (`↺ Khôi phục bản trước`):**
   - Trước mỗi lượt người dùng gửi yêu cầu sửa code cho Btoan Agent, tự động tạo bản sao lưu nhanh trạng thái các file hiện tại của dự án vào `.btoan_checkpoints/{checkpointId}` (bỏ qua `bin`, `obj`, `node_modules`, `.btoan_checkpoints`).
   - Hiển thị nút nhỏ **`↺ Khôi phục bản trước`** ngay dưới tin nhắn yêu cầu của người dùng: khi bấm vào, khôi phục toàn bộ tệp của dự án về đúng trạng thái trước lượt sửa đó, tự động làm mới `EXPLORER` và cập nhật lại nội dung các tab đang mở trên Monaco Editor.

---

## PROMPT 8/10: KÍCH HOẠT NGỮ CẢNH `@FILE`, `/COMMANDS`, NÚT SỬA LỖI TERMINAL & DIFF VIEW

Kiểm tra `Views/Agent/Index.cshtml`, `wwwroot/js/agent_ide.js` và `Services/AgentService.cs`:

1. **Làm cho tính năng `@` (Mention) và `/` (Slash Commands) hoạt động thật:**
   - Khi người dùng gõ `@` trong ô nhập của Btoan Agent: bật popup gợi ý danh sách toàn bộ tệp trong dự án hiện tại (lọc theo từ khóa đang gõ). Khi chọn tệp (hoặc với chip file đang gắn sẵn như `@index.html`), truyền đường dẫn và nội dung tệp đó vào ngữ cảnh ưu tiên cho Agent.
   - Khi người dùng gõ `/`: bật menu lệnh nhanh gồm:
     - `/fix` — Đọc lỗi biên dịch / lỗi Terminal gần nhất và tự động sửa toàn bộ.
     - `/explain` — Giải thích chi tiết cấu trúc và logic của tệp đang mở.
     - `/refactor` — Tối ưu hóa mã nguồn sạch đẹp, chuẩn cấu trúc hơn.
     - `/ui` — Nâng cấp giao diện hiện đại, responsive và bổ sung hiệu ứng mượt mà.

2. **Nút nhờ Agent sửa lỗi ngay trên Terminal:**
   - Khi lệnh chạy trong Terminal xuất hiện lỗi (`error CS...`, `Exception`, `ERR!`, `Unhandled`), hiển thị nút nổi gọn **`✨ Nhờ Agent sửa lỗi này`** để đẩy trực tiếp đoạn log lỗi từ Terminal vào Btoan Agent phân tích và sửa ngay.

3. **So sánh thay đổi mã nguồn (Monaco Diff View):**
   - Khi người dùng bấm vào nút so sánh trên pill `✏️ Đã sửa {file}`, mở chế độ **Monaco Diff Editor** (so sánh bản trước từ Checkpoint bên trái — bản mới AI vừa sửa bên phải) ngay trong khung Editor chính.

---

## PROMPT 9/10: CHUẨN HÓA KHỞI TẠO DỰ ÁN (VISUAL STUDIO / CLI) & MỞ THÔNG LIVE PREVIEW

Kiểm tra `Controllers/IdeController.cs`, `Program.cs` và `wwwroot/js/agent_ide.js`:

1. **Khởi tạo dự án chuẩn xác & Cho phép thu gọn thư mục gốc trong Explorer:**
   - Đảm bảo modal **Tạo Dự Án Workspace Mới** gọi đúng CLI chuẩn:
     - `ASP.NET Core Web App (MVC)` -> `dotnet new mvc -n "{ProjectName}" -o "{ProjectDir}" --no-https` (đầy đủ `Controllers/HomeController.cs`, `Models/ErrorViewModel.cs`, `Views/`, `Properties/launchSettings.json`).
     - `ASP.NET Core Web API` -> `dotnet new webapi -n "{ProjectName}" -o "{ProjectDir}" --use-controllers --no-https`.
     - `Angular App` -> `ng new` (đảm bảo `Dockerfile` đã cài sẵn Node.js LTS + `@angular/cli` và đặt `NPM_CONFIG_CACHE=/tmp/.npm`).
     - `HTML5 / CSS3 / JS Static Web` -> 3 tệp sạch tiêu chuẩn `index.html`, `style.css`, `script.js`.
   - Trong `EXPLORER`: cho phép click vào tên hoặc mũi tên `⌄`/`›` của thư mục dự án gốc ngoài cùng để thu gọn/mở rộng danh sách file con mượt mà. Không tự động gọi `/api/ide/file-content` cho `index.html` nếu tệp đó chưa tồn tại trong dự án (tránh lỗi Console `404`).

2. **Thông suốt Live Preview (Cả Web Tĩnh lẫn Cổng Động `5200+`):**
   - Khi bấm nút **`🌐 Preview`**:
     - Nếu dự án đang chạy cổng động (`● Live :{port}`), mở thẳng `/ide-preview/{port}/?user_id={uid}` trong khung Preview và nút `Tab mới` (cho phép Reverse Proxy `/ide-preview/{port}` hoạt động mượt trong `<iframe>` mà không bị chặn lỗi yêu cầu đăng nhập).
     - Nếu là web tĩnh (`HTML/CSS/JS`), mở `/api/ide/preview/{project}/index.html?user_id={uid}` và tự động phân giải đúng đường dẫn tương đối (`style.css`, `script.js`, `images/...`) để hiển thị đầy đủ giao diện và hình ảnh.

---

## PROMPT 10/10: CỐ ĐỊNH LAYOUT, BUILD SẠCH 0 LỖI, RESTART `AI.BTOAN.COM` & CHẠY KIỂM THỬ THỰC TẾ

Thực hiện trên `D:\ai.btoan.com\BtoanAiNet` và dịch vụ chạy thật `https://ai.btoan.com`:

1. **Cố định Layout toàn trang `/agent` (`wwwroot/css/agent_ide.css` & `wwwroot/js/agent_ide.js`):**
   - Đảm bảo không có rule `text-align: center` rò rỉ từ `site.css` vào `.monaco-editor` hoặc `.xterm` (ép `text-align: left !important` cho vùng code và terminal).
   - Gắn `ResizeObserver` cho khung Terminal `xterm.js` và Monaco Editor để luôn gọi `fitAddon.fit()` và `editor.layout()`, giúp chữ trong Terminal trải đều 100% chiều ngang khung dưới và cột số dòng Monaco sát mép trái chuẩn xác.
   - Tăng tham số cache-busting `?v=...` mới nhất cho toàn bộ tệp `.js` và `.css` trong `Views/Agent/Index.cshtml` và `Views/Home/Index.cshtml`.

2. **Build & Cập nhật Production (`https://ai.btoan.com`):**
   - Chạy `dotnet build` đạt **0 Error, 0 Warning**.
   - Khởi động lại / rebuild tiến trình hoặc Docker container đang phục vụ domain thật `https://ai.btoan.com` (kiểm tra chắc chắn `Gemini:ApiKey` và `GoogleAuth` hoạt động bình thường).

3. **Chạy 2 bài kiểm thử thực tế trên `https://ai.btoan.com/agent` bằng Browser Agent:**
   - **Bài test 1 (Ứng dụng tính toán phức tạp — kiểm chứng hết lỗi treo 288s):** Tạo dự án mới và gửi prompt: *"tạo trang web tính lương nhân viên đầy đủ lương Gross sang Net, bảo hiểm, thuế TNCN và xuất bảng lương"*. Xác nhận Agent phản hồi nhanh trong vài giây, hiện khối `Thought`, hiện Live Task Card và tạo lần lượt từng tệp `index.html`, `style.css`, `script.js` trực tiếp trên `EXPLORER`, mở `Preview` chạy mượt.
   - **Bài test 2 (Web có hình ảnh thật, Reload `F5` & Checkpoint):** Nhắn tiếp: *"thêm banner minh họa và 2 thẻ nhân viên mẫu có ảnh đại diện thật"*. Xác nhận Agent gọi `generate_image` tạo file ảnh nhị phân thật hiển thị sắc nét trong Preview; sau khi nhấn **`F5` (Reload trang)** thì toàn bộ file code, hình ảnh, lịch sử chat và nút `↺ Khôi phục bản trước` đều còn nguyên vẹn 100%.