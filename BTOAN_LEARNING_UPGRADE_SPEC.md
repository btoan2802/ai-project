# ĐẶC TẢ KỸ THUẬT: NÂNG CẤP HỆ SINH THÁI "🎓 HỌC TẬP" BTOAN AI PRO (`ai.btoan.com`)

> **Phạm vi thực hiện:** `D:\ai.btoan.com\BtoanAiNet` → Triển khai thực tế lên `https://ai.btoan.com` (bỏ qua hoàn toàn `D:\github\ai-project`).
> **Nguyên tắc bất di bất dịch:**
> 1. **100% thương hiệu Btoan AI:** Không xuất hiện bất kỳ từ khóa thương hiệu bên thứ ba (`Gemini`, `Google`, `Astra`, `OpenAI`) trên giao diện, câu trả lời hay chú thích người dùng.
> 2. **Cô lập tuyệt đối theo `UserId`:** Mọi bảng SQLite mới và mọi truy vấn `SELECT / INSERT / UPDATE / DELETE` bắt buộc lọc chặt theo `UserId` đang đăng nhập.
> 3. **Đồng bộ Light Mode & Dark Mode:** Không hardcode nền đen chữ tối; toàn bộ thẻ, bảng, biểu đồ và nút bấm đều hiển thị sắc nét trên cả hai giao diện.
> 4. **Bảo toàn tính năng đang chạy ổn định:** Không làm hỏng bộ đề chuẩn THPTQG 2025–2026 (3 phần), xuất file Word `.docx`, Flashcards 3D, KaTeX và Btoan Studio IDE `/agent`.

---

## TỔNG QUAN 4 TRỤ CỘT CẦN XÂY DỰNG

1. **🎯 Radar Lỗ Hổng Kiến Thức (Knowledge Radar & Weak-Spot Tracker)** — Theo dõi tỷ lệ đúng/sai theo từng chuyên đề sau mỗi bài trắc nghiệm/flashcard và cho phép bấm 1 nút để ôn ngay phần yếu nhất.
2. **📅 Lộ Trình Đếm Ngược Ngày Thi (Exam Countdown & AI Study Roadmap)** — Nhập ngày thi, mục tiêu điểm, môn học (kèm tùy chọn tải đề cương) để AI lập checklist ôn tập từng ngày lưu vào SQLite.
3. **🧠 Chế Độ Gia Sư Gợi Mở (Socratic Tutor Mode)** — Nút gạt bật/tắt giúp AI dẫn dắt từng bước, gợi ý công thức và đặt câu hỏi ngược để học sinh tự tư duy thay vì chép sẵn đáp án.
4. **🧪 Phòng Thí Nghiệm & Mô Phỏng Tương Tác (Interactive STEM Lab)** — AI sinh trực tiếp ứng dụng mô phỏng HTML5 Canvas/SVG tương tác (kéo thanh trượt đồ thị Toán, con lắc Vật lý, phản ứng Hóa học...) chạy an toàn trong `iframe sandbox` ngay trong khung chat.

---

## PHẦN 1: CƠ SỞ DỮ LIỆU SQLITE & API BACKEND (`Data/`, `Controllers/LearningController.cs`)

### 1.1. Tự động khởi tạo 2 bảng mới trong SQLite (Auto-create on startup)
Đảm bảo hàm khởi tạo DB chạy an toàn (`CREATE TABLE IF NOT EXISTS`) khi ứng dụng khởi động trên Render mà không gây lỗi thiếu bảng:

```sql
CREATE TABLE IF NOT EXISTS LearningTopicStats (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId INTEGER NOT NULL,
    Subject TEXT NOT NULL,
    GradeLevel TEXT NOT NULL,
    TopicName TEXT NOT NULL,
    TotalQuestions INTEGER NOT NULL DEFAULT 0,
    CorrectQuestions INTEGER NOT NULL DEFAULT 0,
    MasteryPercent REAL NOT NULL DEFAULT 0,
    LastPracticedAt TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IDX_LearningTopicStats_User ON LearningTopicStats(UserId, Subject);

CREATE TABLE IF NOT EXISTS StudyRoadmaps (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId INTEGER NOT NULL,
    Title TEXT NOT NULL,
    Subject TEXT NOT NULL,
    GradeLevel TEXT NOT NULL,
    ExamDate TEXT NOT NULL,
    TargetScore TEXT NOT NULL,
    DailyMinutes INTEGER NOT NULL DEFAULT 60,
    SourceDocName TEXT NULL,
    PlanJson TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IDX_StudyRoadmaps_User ON StudyRoadmaps(UserId);
```

### 1.2. Các API Endpoint mới trong `Controllers/LearningController.cs` (Yêu cầu `[Authorize]`)
Toàn bộ endpoint phải lấy `currentUserId` từ Claims của phiên đăng nhập hiện tại (tuyệt đối không tin `userId` gửi từ body client):

1. **`POST /api/learning/record-result`**
   - **Body:** `{ subject, gradeLevel, items: [{ topic: "Nguyên hàm - Tích phân", isCorrect: true }, ...] }`
   - **Xử lý:** Cộng dồn `TotalQuestions` và `CorrectQuestions` cho từng `(UserId, Subject, TopicName)` trong `LearningTopicStats`, tự tính lại `MasteryPercent = Math.Round((double)CorrectQuestions / TotalQuestions * 100, 1)` và cập nhật `LastPracticedAt`.
2. **`GET /api/learning/radar`**
   - **Trả về:**
     - `summary`: `{ totalPracticed, overallAccuracy, weakTopicsCount, strongTopicsCount }`
     - `topics`: danh sách chuyên đề sắp xếp từ **yếu nhất đến mạnh nhất** (`MasteryPercent ASC`).
     - `weakestTopic`: chuyên đề có điểm thấp nhất (`< 70%`) để hiển thị nút hành động nhanh.
3. **`GET /api/learning/roadmaps`**
   - Trả về danh sách lộ trình ôn thi của `currentUserId`, kèm số ngày đếm ngược `daysRemaining` và phần trăm hoàn thành `progressPercent`.
4. **`POST /api/learning/roadmaps/generate`**
   - **Body:** `{ subject, gradeLevel, examDate, targetScore, dailyMinutes, syllabusText, sourceDocName }`
   - **Xử lý:** Gọi `ChatService` tạo lộ trình JSON khoa học chia theo từng ngày (từ hôm nay tới `examDate`, tối đa 30 mốc học cụ thể, ưu tiên ôn các chuyên đề yếu từ `LearningTopicStats` của user nếu có), lưu vào `StudyRoadmaps` và trả về object lộ trình vừa tạo.
5. **`POST /api/learning/roadmaps/{id}/toggle-task`**
   - **Body:** `{ taskId, completed: true/false }`
   - **Xử lý:** Kiểm tra `UserId == currentUserId`, cập nhật trạng thái `completed` bên trong `PlanJson`, lưu DB và trả về tiến độ mới.
6. **`DELETE /api/learning/roadmaps/{id}`**
   - Xóa lộ trình thuộc `currentUserId`.

---

## PHẦN 2: CHI TIẾT 4 TÍNH NĂNG NÂNG CẤP

### TÍNH NĂNG 1: 🎯 RADAR LỖ HỔNG KIẾN THỨC (`wwwroot/js/app.js`, `wwwroot/css/app.css`, `Services/ChatService.cs`)
1. **Gắn nhãn chuyên đề (`topic`) tự động cho mỗi câu hỏi:**
   - Cập nhật System Prompt tạo `[QUIZ_DATA]` trong `Services/ChatService.cs`: mỗi câu hỏi ở Phần I, Phần II và Phần III **bắt buộc** có thêm trường `"topic"` ngắn gọn (VD: `"Khảo sát hàm số"`, `"Tích phân"`, `"Xác suất Bayes"`, `"Hình học Oxyz"`, `"Dao động điều hòa"`...).
   - Nếu câu hỏi cũ không có `"topic"`, fallback về tên chủ đề hoặc tên môn học.
2. **Tự động ghi nhận kết quả học tập:**
   - Ngay khi người dùng bấm **`✅ Nộp bài & Chấm điểm`** trên Card Trắc nghiệm (cả đề thường lẫn đề 3 phần THPTQG), frontend gọi ngầm `POST /api/learning/record-result` gửi danh sách `{ topic, isCorrect }` lên server.
   - Khi người dùng bấm **`✓ Đã thuộc`** (`isCorrect: true`) hoặc **`↻ Cần ôn lại`** (`isCorrect: false`) trên Flashcards 3D, cũng ghi nhận tương tự vào `LearningTopicStats`.
3. **Giao diện Radar trong `🎓 Học tập`:**
   - Ngay trong không gian **`🎓 Học tập`**, bổ sung thanh điều hướng con gọn đẹp ở đầu trang:
     `📚 Phương pháp học` | `🎯 Radar lỗ hổng` | `📅 Lộ trình ôn thi`
   - Trong tab **`🎯 Radar lỗ hổng`**:
     - Hiển thị 4 thẻ thống kê nhanh: *Tổng câu đã luyện*, *Độ chính xác trung bình*, *Chuyên đề vững ($\ge 80\%$)*, *Lỗ hổng cần vá ($< 65\%$)*.
     - Danh sách chuyên đề kèm thanh tiến độ màu sắc trực quan:
       - Đỏ (`< 50%`): *Cần ưu tiên gấp*
       - Cam (`50% – 79%`): *Cần củng cố*
       - Xanh lá (`>= 80%`): *Đã vững*
     - Nút nổi bật **`⚡ Ôn ngay phần yếu nhất`** trên từng lỗ hổng: bấm vào tự động mở bài luyện tập 5 câu trắc nghiệm chuyên sâu (hoặc giải bài Socratic) nhắm đúng vào chuyên đề yếu đó.
     - Trạng thái rỗng (user mới chưa làm bài): hiển thị hướng dẫn thân thiện + nút chuyển sang làm bài kiểm tra đầu tiên.

---

### TÍNH NĂNG 2: 📅 LỘ TRÌNH ĐẾM NGƯỢC NGÀY THI (`Views/Home/Index.cshtml`, `wwwroot/js/app.js`, `wwwroot/css/app.css`)
1. **Giao diện tạo & quản lý lộ trình trong tab `📅 Lộ trình ôn thi`:**
   - **Form khởi tạo lộ trình gọn gàng:**
     - Chọn Cấp học & Môn học (tái sử dụng danh mục 100% môn từ Lớp 1 đến Đại học).
     - Chọn **Ngày thi** (`<input type="date">`, mặc định sau hôm nay 14 ngày).
     - Chọn **Mục tiêu điểm** (`7.0+`, `8.0+`, `8.5+`, `9.0+`, `Qua môn / Đạt A+ Đại học`).
     - Thời lượng học mỗi ngày (`30 phút`, `60 phút`, `90 phút`, `120 phút`).
     - Ô nhập phạm vi kiến thức / chương cần ôn **HOẶC** chọn tải file đề cương (`PDF / Docx / TXT` tái sử dụng endpoint `/api/learning/parse-document`).
     - Nút **`✨ Tạo Lộ trình Ôn thi AI`**.
2. **Hiển thị Lộ trình & Đồng hồ đếm ngược:**
   - Card tiêu đề lộ trình hiển thị **Badge đếm ngược nổi bật** (VD: `⏳ Còn 14 ngày tới kỳ thi`), mục tiêu điểm, thanh tiến độ phần trăm hoàn thành (`Hoàn thành 3 / 14 nhiệm vụ`).
   - Danh sách nhiệm vụ theo từng ngày/chặng (`Ngày 1`, `Ngày 2`...):
     - Checkbox tick hoàn thành (gọi `POST /api/learning/roadmaps/{id}/toggle-task` lưu vĩnh viễn vào SQLite).
     - Tên chuyên đề trọng tâm + phương pháp khuyến nghị (`Giảng bài`, `Trắc nghiệm`, `Flashcards`, `Thi thử THPTQG`).
     - Nút **`Học ngay →`** ngay trên mỗi dòng nhiệm vụ: bấm vào tự động mở đúng phương pháp học đó với môn học và chuyên đề của ngày hôm ấy điền sẵn!

---

### TÍNH NĂNG 3: 🧠 CHẾ ĐỘ GIA SƯ GỢI MỞ — SOCRATIC TUTOR (`Views/Home/Index.cshtml`, `wwwroot/js/app.js`, `Services/ChatService.cs`)
1. **Giao diện bật/tắt trực quan:**
   - Trong phương pháp **`🧭 Giải bài tập & Giảng bài từng bước`** của Learning Hub, bổ sung nút gạt nổi bật (Toggle Switch):
     - **`🧠 Chế độ Gia sư gợi mở (Hướng dẫn từng bước — Không lộ đáp án ngay)`** (mặc định **BẬT** khi chọn chế độ Gia sư).
   - Đồng thời hiển thị một badge nhỏ **`🧠 Đang bật Gia sư gợi mở`** kèm nút tắt nhanh ngay phía trên ô nhập chat trong suốt phiên học đó.
2. **Logic điều khiển AI ở Backend (`Services/ChatService.cs`):**
   - Nhận thêm cờ `socraticMode: true` trong request chat (hoặc nhận diện từ thẻ ngữ cảnh phiên học).
   - Khi `socraticMode == true`, chèn System Instruction chặt chẽ:
     - **Tuyệt đối không đưa ra đáp án cuối cùng hay lời giải trọn vẹn ngay ở lượt đầu.**
     - Trình bày ngắn gọn, thân thiện theo đúng 3 phần:
       1. *Phân tích nhanh:* Đề bài cho gì, yêu cầu tìm gì?
       2. *Gợi ý công thức / Định hướng Bước 1:* Nhắc lại định lý hoặc công thức chìa khóa (dùng KaTeX chuẩn, không bọc tiếng Việt trong `$...$`).
       3. *Câu hỏi tương tác cho bạn:* Đặt 1 câu hỏi nhỏ cụ thể để học sinh tự tính bước đầu tiên rồi trả lời.
     - Ở các lượt tiếp theo: nếu học sinh làm đúng → khen ngợi ngắn gọn và dẫn sang bước kế tiếp; nếu học sinh làm sai → chỉ rõ chỗ nhầm lẫn một cách khích lệ và gợi ý nhẹ hơn.
     - Chỉ trình bày lời giải hoàn chỉnh từ đầu đến cuối khi học sinh tự giải xong hoặc bấm nút yêu cầu xem đáp án.
3. **Nút phản hồi nhanh dưới mỗi tin nhắn Gia sư (`wwwroot/js/app.js`):**
   - Dưới mỗi câu trả lời ở chế độ Socratic, hiển thị 3 nút chip gọn nhẹ:
     - **`💡 Gợi ý thêm`** (tự gửi: *"Cho mình thêm một gợi ý nhỏ nữa cho bước này nhé."*)
     - **`🔄 Giảng dễ hiểu hơn`** (tự gửi: *"Bạn giải thích lại bước này bằng ví dụ đơn giản hơn giúp mình nhé."*)
     - **`🔓 Xem lời giải đầy đủ`** (tự gửi: *"Hãy trình bày lời giải chi tiết đầy đủ từ đầu đến cuối kèm đáp án chính xác cho bài này."*)

---

### TÍNH NĂNG 4: 🧪 PHÒNG THÍ NGHIỆM & MÔ PHỎNG TƯƠNG TÁC (`Views/Home/Index.cshtml`, `wwwroot/js/app.js`, `wwwroot/css/app.css`, `Services/ChatService.cs`)
1. **Nâng cấp thẻ `Mô phỏng đa tầng` trong Learning Hub thành `🧪 Mô phỏng tương tác trực quan`:**
   - Khi người dùng chọn thẻ này, hiển thị thêm các gợi ý mẫu bấm chọn nhanh theo môn:
     - *Toán:* `"Đồ thị hàm bậc 3 y = ax³ + bx² + cx + d thay đổi hệ số a, b, c, d"` | `"Ý nghĩa hình học của Tích phân (Tổng Riemann)"`
     - *Vật lý:* `"Dao động điều hòa của con lắc lò xo (thay đổi k, m, biên độ A)"` | `"Khúc xạ ánh sáng qua lăng kính / định luật Snell"`
     - *Hóa học:* `"Mô phỏng cân bằng pH axit - bazơ và đổi màu chất chỉ thị"`
     - *Sinh học / Địa lý:* `"Cơ chế nhân đôi ADN / Mạch máu tuần hoàn"`
2. **Giao thức dữ liệu `[SIMULATION_DATA]` tách trước Markdown (`Services/ChatService.cs` & `wwwroot/js/app.js`):**
   - Trong `ChatService.cs`: khi ở chế độ Mô phỏng tương tác, yêu cầu AI giải thích ngắn gọn lý thuyết cốt lõi (3–5 dòng) **VÀ** xuất kèm một ứng dụng mô phỏng độc lập bên trong thẻ:
     ```text
     [SIMULATION_DATA]
     {
       "title": "Mô phỏng Đồ thị Hàm số bậc 3 & Cực trị",
       "subject": "Toán học",
       "instructions": "Kéo các thanh trượt a, b, c, d để quan sát sự thay đổi của đồ thị và điểm cực trị.",
       "html": "<!DOCTYPE html>...(mã HTML + CSS + JS Canvas/SVG tự chạy hoàn chỉnh, KHÔNG gọi CDN ngoài, kích thước responsive 100%)..."
     }
     [/SIMULATION_DATA]
     ```
   - **CRITICAL trong `wwwroot/js/app.js`:**
     - Bắt buộc trích xuất khối `[SIMULATION_DATA]...[/SIMULATION_DATA]` thành placeholder **TRƯỚC KHI** chạy `marked.parse()` (giống cơ chế bảo vệ `[QUIZ_DATA]`) để Markdown không làm hỏng mã HTML/JS bên trong.
     - Render thành **Card Phòng thí nghiệm tương tác** chuyên nghiệp:
       - Thanh Header: Icon 🧪 + Tên mô phỏng + Hướng dẫn thao tác + Nút **`↻ Đặt lại`** (reload iframe) + Nút **`⛶ Toàn màn hình`** (mở rộng modal xem lớn).
       - Khung chạy an toàn: `<iframe class="simulation-iframe" sandbox="allow-scripts" loading="lazy"></iframe>` gán nội dung qua thuộc tính `iframe.srcdoc = simData.html` (chiều cao mặc định `480px`, bo góc mềm mại, viền đồng bộ Light/Dark Mode).
     - Áp dụng tương tự quy tắc **không tự động cuộn tuột xuống đáy** khi render Card Mô phỏng (cuộn tới đỉnh Card để người học nhìn thấy ngay màn hình mô phỏng).
3. **Tiêu chuẩn chất lượng mã HTML mô phỏng AI sinh ra (`Services/ChatService.cs`):**
   - Phải là **1 file HTML tự chạy 100%** (HTML5 `<canvas>` hoặc `<svg>` sắc nét, hỗ trợ Retina DPI, inline CSS/JS, **không** dùng link CDN bên ngoài dễ lỗi mạng).
   - Bắt buộc có **bảng điều khiển tương tác** (ít nhất 2–4 thanh trượt `<input type="range">` hiển thị giá trị số thời gian thực + nút Chạy/Tạm dừng nếu có chuyển động) và **khung hiển thị kết quả tính toán trực tiếp** (VD: tọa độ cực đại/cực tiểu, chu kỳ $T$, tần số góc $\omega$, góc khúc xạ $r$...).
   - Giao diện bên trong iframe hiện đại, sáng sủa, phông chữ `system-ui`, bố cục 2 cột (trái là Canvas/SVG trực quan, phải là bảng thanh trượt điều khiển).

---

## PHẦN 3: THỨ TỰ TRIỂN KHAI & TIÊU CHUẨN BÀN GIAO

### Thứ tự thực hiện khuyến nghị:
1. **Bước 1:** Tạo schema SQLite (`LearningTopicStats`, `StudyRoadmaps`) + `Controllers/LearningController.cs` với đầy đủ 6 endpoint cô lập theo `UserId`.
2. **Bước 2:** Cập nhật `Services/ChatService.cs` (thêm trường `topic` cho Quiz, hỗ trợ `socraticMode`, hỗ trợ tạo Roadmap JSON và `[SIMULATION_DATA]` HTML5 tương tác).
3. **Bước 3:** Cập nhật `Views/Home/Index.cshtml`, `wwwroot/css/app.css`, `wwwroot/js/app.js`:
   - Thêm 3 sub-tabs trong Learning Hub (`Phương pháp học`, `Radar lỗ hổng`, `Lộ trình ôn thi`).
   - Kết nối tự động ghi nhận kết quả Quiz/Flashcards vào Radar.
   - Thêm Toggle **Gia sư gợi mở** + 3 chip phản hồi nhanh.
   - Thêm bộ tách & render **`[SIMULATION_DATA]`** trong `iframe sandbox` kèm nút Toàn màn hình / Đặt lại.
4. **Bước 4:** Cập nhật cache-busting `?v=20261128_learning_pro_v1` cho tất cả file CSS/JS trong `Views/Home/Index.cshtml`.
5. **Bước 5:** Chạy `dotnet build` đạt **0 Error, 0 Warning**, commit & push lên nhánh `main` để Render triển khai lên `https://ai.btoan.com`.

---

## PHẦN 4: KỊCH BẢN KIỂM CHỨNG THỰC TẾ BẰNG BROWSER AGENT TRÊN `https://ai.btoan.com`

Sau khi Render deploy xong bản `?v=20261128_learning_pro_v1`, dùng Browser Agent kiểm tra thực tế và chụp ảnh báo cáo đủ **4 kịch bản** sau:

1. **Test Mô phỏng tương tác (`🧪 Mô phỏng`):**
   - Vào `🎓 Học tập` → chọn **Mô phỏng tương tác** → chọn môn **Toán học** → nhập `"Đồ thị hàm bậc 3 y = ax³ + bx² + cx + d"` → bấm Bắt đầu.
   - Xác nhận hiện khung `iframe` vẽ hệ trục tọa độ & đường cong mượt mà, có các thanh trượt $a, b, c, d$; kéo thử thanh trượt thấy đồ thị thay đổi trực tiếp. Chụp **Ảnh 1**.
2. **Test Gia sư gợi mở (`🧠 Socratic Mode`):**
   - Vào `🎓 Học tập` → chọn **Giải bài tập & Giảng từng bước** → bật công tắc **Chế độ Gia sư gợi mở** → gửi bài toán: `"Tính tích phân I = \int_0^1 (2x + 1)dx"`.
   - Xác nhận AI **không lộ đáp số `2` ngay**, mà chỉ gợi ý bước tìm nguyên hàm và hỏi ngược lại học sinh, kèm 3 nút chip (`Gợi ý thêm`, `Giảng dễ hiểu hơn`, `Xem lời giải đầy đủ`). Chụp **Ảnh 2**.
3. **Test Lộ trình đếm ngược ngày thi (`📅 Lộ trình ôn thi`):**
   - Mở tab **Lộ trình ôn thi** trong Learning Hub → tạo lộ trình môn **Vật lý (THPT)**, chọn ngày thi sau 10 ngày, mục tiêu `9.0+` → bấm Tạo lộ trình.
   - Xác nhận hiện thẻ đếm ngược `Còn 10 ngày`, danh sách nhiệm vụ từng ngày; tick thử 1 checkbox → bấm `F5` tải lại trang xác nhận trạng thái tick vẫn được lưu trong SQLite. Chụp **Ảnh 3**.
4. **Test Radar Lỗ Hổng Kiến Thức (`🎯 Radar lỗ hổng`):**
   - Làm nhanh 1 đề trắc nghiệm 5 câu (cố tình chọn đúng 2 câu, sai 3 câu) → bấm **Nộp bài & Chấm điểm**.
   - Quay lại tab **Radar lỗ hổng** trong Learning Hub → xác nhận biểu đồ chuyên đề đã tự động cập nhật tỷ lệ % đúng/sai và hiện nút **`⚡ Ôn ngay phần yếu nhất`**. Chụp **Ảnh 4**.
   - Kiểm tra nhanh bằng tài khoản thứ 2 (hoặc tab ẩn danh): xác nhận không bị lộ dữ liệu Radar hay Lộ trình của tài khoản admin.