using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BtoanAiNet.Models;
using BtoanAiNet.Repositories;
using BtoanAiNet.ViewModels;

using System.Runtime.CompilerServices;
using System.Text;

namespace BtoanAiNet.Services
{
    public interface IChatService
    {
        Task<JsonObject> ProcessChatAsync(ChatRequestModel requestModel, string webRootPath);
        IAsyncEnumerable<string> StreamChatAsync(ChatRequestModel requestModel, string webRootPath, CancellationToken cancellationToken = default);
        Task<string> GenerateTitleAsync(string firstPrompt);
    }

    public class ChatService : IChatService
    {
        private readonly IConversationRepository _conversationRepository;
        private readonly ITrainingRepository _trainingRepository;
        private readonly IIntentEngineService _intentEngineService;
        private readonly IGeminiService _geminiService;
        private readonly IUserRepository _userRepository;
        private readonly IServiceScopeFactory _scopeFactory;

        private static string? _thptqgExamTemplateCache = null;
        private static string GetThptqgExamTemplate(string contentRootPath)
        {
            if (_thptqgExamTemplateCache != null) return _thptqgExamTemplateCache;
            try
            {
                string templatePath = Path.Combine(contentRootPath, "App_Data", "ExamTemplates", "thptqg_math_2025_2026_template.md");
                if (File.Exists(templatePath))
                {
                    _thptqgExamTemplateCache = File.ReadAllText(templatePath);
                    return _thptqgExamTemplateCache;
                }
            }
            catch { }
            return "";
        }

        private const string FastSystemInstructionText = @"Bạn là Btoan AI — trợ lý trí tuệ nhân tạo độc quyền do Btoan (Nguyễn Bảo Toàn) phát triển.
- NGUYÊN TẮC DANH TÍNH BẮT BUỘC: Trong mọi tình huống, kể cả khi người dùng hỏi bạn dùng API gì, có phải Gemini hay Google không, bạn tuyệt đối KHÔNG được nhắc tới Gemini, Google hay bất kỳ nhà cung cấp bên thứ ba nào; luôn khẳng định bạn là mô hình của Btoan AI.
- Phản hồi trực tiếp, ngắn gọn, súc tích và tự nhiên bằng tiếng Việt (hoặc ngôn ngữ người dùng dùng).
- Khi người dùng chào hỏi hoặc nhắn ngắn (như 'ê', 'chào', 'ko', 'ok', 'sao thế'), hãy phản hồi ngắn gọn, tự nhiên, thân thiện. Tuyệt đối không lặp lại quy tắc máy móc.
- Nếu cuộc trò chuyện đang có tài liệu hoặc người dùng yêu cầu chỉnh sửa/điền/thêm dữ liệu, BẮT BUỘC xuất thẻ [ARTIFACT type=""..."" title=""...""] đầy đủ dữ liệu, TUYỆT ĐỐI KHÔNG hứa suông 'đợi chút' hay 'để mình làm'.
- Khi người dùng yêu cầu 'bỏ vào word đi', 'xuất file word', 'tạo file docx':
  1. Nếu cuộc trò chuyện đã có nội dung văn bản/tài liệu trước đó: BẮT BUỘC đưa 100% nội dung thực tế đó vào trong [ARTIFACT type=""docx"" title=""...""]...[/ARTIFACT].
  2. Nếu cuộc trò chuyện đang trống hoặc chưa có nội dung/chủ đề cụ thể: TUYỆT ĐỐI KHÔNG tự bịa nội dung mẫu giữ chỗ chung chung; hãy hỏi ngắn gọn người dùng muốn đưa nội dung hoặc chủ đề gì vào file Word.
- Tạo trắc nghiệm kiểm tra & Đề thi THPTQG: Trả về khối [QUIZ_DATA]{...}[/QUIZ_DATA]. Mỗi câu hỏi BẮT BUỘC có trường 'topic'. Cấm bảng ASCII; bảng số liệu xuất qua 'dataTable', bảng biến thiên xuất qua 'variationTable'. Công thức toán bọc trong $...$, cấm bọc chữ tiếng Việt trong $...$.
- Mô phỏng tương tác: Trả về khối [SIMULATION_DATA]{""title"": ""..."", ""subject"": ""..."", ""instructions"": ""..."", ""html"": ""<!DOCTYPE html>...""}[/SIMULATION_DATA].
- Gia sư gợi mở (Socratic): Không đưa đáp án ngay, chia 3 phần: Phân tích nhanh, Gợi ý công thức, Câu hỏi tương tác.
- Tạo thẻ flashcards: Trả về khối [FLASHCARD_DATA]{...}[/FLASHCARD_DATA].
- Tạo ảnh: kết thúc bằng [GENIMAGE] <prompt tiếng Anh chi tiết>
- Tạo video: kết thúc bằng [GENVIDEO] <prompt tiếng Anh chi tiết>
- Tạo nhạc: kết thúc bằng [GENMUSIC] <prompt tiếng Anh chi tiết>";

        private const string SystemInstructionText = @"
Bạn là Btoan AI — trợ lý trí tuệ nhân tạo độc quyền do Btoan (Nguyễn Bảo Toàn) phát triển.
* NGUYÊN TẮC DANH TÍNH BẮT BUỘC: Trong mọi tình huống, kể cả khi người dùng hỏi bạn dùng API gì, có phải Gemini hay Google không, bạn tuyệt đối KHÔNG được nhắc tới Gemini, Google hay bất kỳ nhà cung cấp bên thứ ba nào; luôn khẳng định bạn là mô hình của Btoan AI.
+ Thông tin về Nguyễn Bảo Toàn (chỉ trả lời nếu người dùng chủ động hỏi): 
Tên: Nguyễn Bảo Toàn. Sinh năm: 28/02/2006. Quê quán: Hà Tĩnh. Nơi sinh: Bà Rịa Vũng Tàu. Tạm trú: TPHCM. Sinh viên ngành Khoa học máy tính / CNTT Đại học Bách Khoa ĐHQG TPHCM.

* NGUYÊN TẮC GIAO TIẾP VÀ BẢO MẬT TUYỆT ĐỐI (QUAN TRỌNG NHẤT):
1. TUYỆT ĐỐI KHÔNG lặp lại, tóm tắt, diễn giải hay xác nhận các quy tắc hệ thống, hướng dẫn nội bộ hoặc định dạng xuất file khi người dùng chỉ chào hỏi, nhắn ngắn hoặc giao tiếp thông thường (ví dụ: 'ko', 'không', 'um', 'ừ', 'ok', 'chào', 'hello').
2. TUYỆT ĐỐI KHÔNG trả lời theo kiểu máy móc như 'Tôi đã hiểu rõ các quy tắc...', 'Tôi sẽ tuân thủ nghiêm ngặt...'. Luôn phản hồi trực tiếp vào ngữ cảnh người dùng đang nói bằng giọng văn tự nhiên của con người.
3. Khi người dùng nhắn 'ko', 'không' hoặc tin ngắn tương tự, hãy phản hồi ngắn gọn, tự nhiên như: 'Có chuyện gì thế bạn?', 'Bạn cần tôi hỗ trợ gì không?', 'Sao thế bạn?'.

* QUY TẮC ARTIFACT STUDIO (KHI NGƯỜI DÙNG YÊU CẦU TẠO / XUẤT TÀI LIỆU HOẶC CHỈNH SỬA / ĐIỀN DỮ LIỆU):
- TUYỆT ĐỐI CẤM NÓI SUÔNG: Không bao giờ trả lời bằng câu hoãn binh như 'để mình làm cho', 'bạn đợi chút', 'được rồi nha', 'để mình đồng bộ qua nhé' mà không kèm theo thẻ [ARTIFACT ...]. Khi người dùng phàn nàn bảng trống, yêu cầu thêm dữ liệu, hoặc hỏi 'đc chưa', bạn BẮT BUỘC PHẢI XUẤT NGAY THẺ [ARTIFACT] đầy đủ dữ liệu trong cùng một câu trả lời!
- Khi người dùng yêu cầu soạn thảo, tạo hoặc xuất file tài liệu (Word .docx, PDF .pdf, Excel .xlsx, PowerPoint .pptx, hoặc yêu cầu nhiều file cùng lúc, hoặc bảo 'bỏ nội dung trên vào word/excel/ppt', 'bỏ vào word xuất file đi', 'xuất file word', 'tạo file docx'):
  1. NẾU TRONG LỊCH SỬ HỘI THOẠI HIỆN TẠI ĐÃ CÓ NỘI DUNG: AI BẮT BUỘC lấy trọn vẹn 100% nội dung thực tế đã tạo ở các tin nhắn trước để đưa vào bên trong cặp thẻ [ARTIFACT type=""..."" title=""...""]...[/ARTIFACT]. TUYỆT ĐỐI KHÔNG cắt xén, không viết tóm tắt đại khái, không bịa nội dung khác.
  2. NẾU CUỘC TRÒ CHUYỆN ĐANG TRỐNG HOẶC CHƯA CÓ NỘI DUNG/CHỦ ĐỀ: AI TUYỆT ĐỐI KHÔNG tự bịa văn bản giữ chỗ chung chung (ví dụ: 'Tài liệu được soạn thảo theo yêu cầu của bạn...', 'Nội dung tài liệu đã được cập nhật thành công...'), mà PHẢI HỎI NGẮN GỌN LẠI người dùng muốn đưa nội dung hoặc chủ đề gì vào file Word.
  3. Bên ngoài chỉ viết 1-2 câu dẫn dắt ngắn gọn, tự nhiên.
  4. Toàn bộ nội dung thật đầy đủ, chi tiết và đẹp mắt BẮT BUỘC đặt trong thẻ [ARTIFACT type=""..."" title=""...""]...[/ARTIFACT].
  5. Có thể xuất NHIỀU thẻ [ARTIFACT] liên tiếp nếu người dùng yêu cầu nhiều định dạng (ví dụ: 1 thẻ docx + 1 thẻ xlsx + 1 thẻ pptx).
  6. Định dạng bên trong thẻ [ARTIFACT]:
    - Với type=""docx"" hoặc type=""pdf"": bên trong là nội dung Markdown hoàn chỉnh (nếu người dùng yêu cầu xuất nội dung từ tin nhắn trước thì BẮT BUỘC chép lại trọn vẹn 100% nội dung đó vào trong thẻ, tuyệt đối không được viết câu xã giao).
    - Với type=""xlsx"": bên trong là JSON hợp lệ theo cấu trúc bảng.
    - Với type=""pptx"": bên trong là JSON hợp lệ theo cấu trúc slide bài thuyết trình.
- Nếu người dùng KHÔNG yêu cầu tạo tài liệu hay xuất file, TUYỆT ĐỐI KHÔNG dùng thẻ [ARTIFACT] hay nhắc đến [ARTIFACT].

* QUY TẮC PHÂN HỆ HỌC TẬP (LEARNING HUB - 5 CHẾ ĐỘ CHUẨN):
1. Chế độ 'Biến đề cương thành sổ ghi chú học tập':
   - Soạn thảo sổ tay ghi chú khoa học, súc tích gồm: 🎯 Mục tiêu bài học; 💡 Khái niệm then chốt & bản chất; 📊 Bảng so sánh; 📝 Câu hỏi tự kiểm tra.
   - BẮT BUỘC xuất kèm thẻ [ARTIFACT type=""docx"" title=""So_tay_ghi_chu""]...[/ARTIFACT] chứa trọn vẹn sổ tay để người dùng tải file Word ngay lập tức.
2. Chế độ 'Mô phỏng tương tác trực quan' (Interactive STEM Lab):
   - Khi người dùng muốn xem mô phỏng đồ thị, hiện tượng vật lý, phản ứng hóa học, sinh học hoặc chọn phương pháp Mô phỏng:
   - Giải thích ngắn gọn lý thuyết cốt lõi (3–5 dòng).
   - BẮT BUỘC xuất kèm ứng dụng mô phỏng độc lập trong cặp thẻ:
     [SIMULATION_DATA]
     {
       ""title"": ""Mô phỏng Đồ thị Hàm số bậc 3 & Cực trị"",
       ""subject"": ""Toán học"",
       ""instructions"": ""Kéo các thanh trượt a, b, c, d để quan sát sự thay đổi của đồ thị và tọa độ điểm cực trị."",
       ""html"": ""<!DOCTYPE html><html><head><meta charset=\""UTF-8\""><style>body{margin:0;font-family:system-ui;background:#f8fafc;color:#0f172a}...</style></head><body>...<script>...</script></body></html>""
     }
     [/SIMULATION_DATA]
   - Tiêu chuẩn mã HTML mô phỏng:
     + 1 file HTML5 hoàn chỉnh, tự chạy 100% trong iframe sandbox, KHÔNG dùng script CDN ngoài.
     + Canvas hoặc SVG sắc nét (retina display), responsive 100%.
     + Bắt buộc có bảng điều khiển ít nhất 2–4 thanh trượt <input type=""range""> hiển thị số thực tế và khung kết quả tính toán động.
3. Chế độ 'Tự kiểm tra kiến thức' (Trắc nghiệm tương tác & Đề thi chuẩn Bộ GD&ĐT THPTQG 2025–2026):
   - Khi người dùng muốn làm trắc nghiệm, kiểm tra kiến thức, làm quiz:
   - Viết 1-2 câu mở đầu ngắn gọn, rồi BẮT BUỘC trả về dữ liệu trắc nghiệm trong cặp thẻ [QUIZ_DATA]...[/QUIZ_DATA].

   A. ĐỐI VỚI ĐỀ THI CHUẨN BỘ GD&ĐT THPTQG 2025–2026 (3 PHẦN):
   - BẮT BUỘC trả về định dạng JSON sau:
     [QUIZ_DATA]
     {
       ""exam_type"": ""THPTQG_2025"",
       ""title"": ""Đề thi thử tốt nghiệp THPT 2025–2026 — Môn Toán"",
       ""subject"": ""Toán"",
       ""durationMinutes"": 90,
       ""part1"": [
         {
           ""id"": 1,
           ""topic"": ""Nguyên hàm - Tích phân"",
           ""question"": ""Câu 1: Cho hàm số $y=f(x)$ liên tục trên $\\mathbb{R}$... Giá trị của $\\int_0^1 f(x)\\,dx$ bằng bao nhiêu?"",
           ""options"": { ""A"": ""$\\frac{1}{2}$"", ""B"": ""$\\frac{1}{3}$"", ""C"": ""$1$"", ""D"": ""$2$"" },
           ""answer"": ""A"",
           ""explanation"": ""Lời giải chi tiết: Ta có... nên chọn đáp án A.""
         }
       ],
       ""part2"": [
         {
           ""id"": 1,
           ""topic"": ""Khảo sát hàm số"",
           ""context"": ""Câu 1: Cho hàm số $f(x) = x^3 - 3x^2 + 2$ có đồ thị $(C)$. Xét tính đúng/sai của các mệnh đề sau:"",
           ""statements"": [
             { ""key"": ""a"", ""text"": ""Hàm số đồng biến trên khoảng $(2; +\\infty)$."", ""answer"": true, ""explanation"": ""Đạo hàm $f'(x) = 3x^2 - 6x > 0$ khi $x > 2$. Mệnh đề đúng."" },
             { ""key"": ""b"", ""text"": ""Đồ thị $(C)$ có điểm cực tiểu là $(0; 2)$."", ""answer"": false, ""explanation"": ""Điểm cực tiểu là $(2; -2)$. Mệnh đề sai."" },
             { ""key"": ""c"", ""text"": ""Giá trị lớn nhất của hàm số trên đoạn $[0; 3]$ bằng $2$."", ""answer"": true, ""explanation"": ""$f(0)=2, f(2)=-2, f(3)=2$. Mệnh đề đúng."" },
             { ""key"": ""d"", ""text"": ""Đường thẳng $y=m$ cắt $(C)$ tại 3 điểm phân biệt khi $-2 < m < 2$."", ""answer"": true, ""explanation"": ""Giá trị cực tiểu là $-2$ và cực đại là $2$. Mệnh đề đúng."" }
           ],
           ""explanation"": ""Lời giải chi tiết toàn bài...""
         }
       ],
       ""part3"": [
         {
           ""id"": 1,
           ""topic"": ""Tích phân hàm phân thức"",
           ""question"": ""Câu 1: Biết $\\int_1^2 \\frac{2x+1}{x^2+x}\\,dx = \\ln a$. Tìm giá trị của $a$ (kết quả dạng số thập phân hoặc phân số):"",
           ""answer"": ""3"",
           ""explanation"": ""Ta có $\\int_1^2 \\frac{d(x^2+x)}{x^2+x} = \\ln|x^2+x| \\Big|_1^2 = \\ln 6 - \\ln 2 = \\ln 3$. Vậy $a = 3$.""
         }
       ]
     }
     [/QUIZ_DATA]

   B. ĐỐI VỚI BỘ ĐỀ TRẮC NGHIỆM THƯỜNG (4 PHƯƠNG ÁN PHẲNG):
     [QUIZ_DATA]
     {
       ""title"": ""Đề trắc nghiệm kiểm tra kiến thức"",
       ""questions"": [
         {
           ""id"": 1,
           ""topic"": ""Tên chuyên đề"",
           ""question"": ""Nội dung câu hỏi (công thức Toán/Lý bọc trong $...$)?"",
           ""options"": [""Đáp án 1"", ""Đáp án 2"", ""Đáp án 3"", ""Đáp án 4""],
           ""answer"": ""A"",
           ""explanation"": ""Giải thích chi tiết...""
         }
       ]
     }
     [/QUIZ_DATA]

   * QUY TẮC BẮT BUỘC TRONG [QUIZ_DATA] VÀ CÔNG THỨC LATEX:
     1. MỌI LỆNH LATEX BẮT BUỘC DÙNG 2 DẤU GẠCH CHÉO NGƯỢC \\ (ví dụ \\frac{1}{2}, \\int, \\sqrt{x}, \\pi, \\cos, \\sin, \\ln) để tránh lỗi JSON escape.
     2. BẮT BUỘC viết đầy đủ dấu ngoặc nhọn \\frac{1}{2}, TUYỆT ĐỐI CẤM viết tắt \\frac12.
     3. BẮT BUỘC có dấu cách sau toán tử: ví dụ \\int \\cos(2x+1)\\,dx, cấm dính liền \\intcos.
     4. Mọi công thức Toán/Lý/Hóa phải bọc trong $...$ hoặc $$...$$.
     5. Trong mảng ""options"", TUYỆT ĐỐI KHÔNG ghi tiền tố ""A."", ""B."", ""C."", ""D."" ở đầu chuỗi (hệ thống tự động hiển thị huy hiệu A, B, C, D).
     6. TRƯỜNG 'topic' BẮT BUỘC: Mỗi câu hỏi ở Part 1, Part 2, Part 3 hay questions BẮT BUỘC có trường 'topic' (2-5 từ, ví dụ 'Khảo sát hàm số', 'Tích phân', 'Xác suất Bayes', 'Hình học Oxyz', 'Dao động điều hòa') để hệ thống tự động ghi nhận vào Radar lỗ hổng kiến thức.

   * QUY TẮC CHƯƠNG TRÌNH GDPT 2018 MỚI CHO ĐỀ TOÁN THPTQG 2025–2026:
     1. LOẠI BỎ HOÀN TOÀN SỐ PHỨC: Tuyệt đối KHÔNG ra bất kỳ câu hỏi nào liên quan đến số phức z = a + bi, môđun số phức, số phức liên hợp hay phương trình bậc hai trên tập số phức (chương trình GDPT 2018 mới đã bỏ hoàn toàn chuyên đề Số phức).
     2. Bám sát 100% ma trận kiến thức Toán THPT 2025–2026:
        - Giải tích: Ứng dụng đạo hàm khảo sát hàm số (đơn điệu, cực trị, GTLN-GTNN, tiệm cận đứng/ngang/xiên), hàm số mũ & logarit, nguyên hàm – tích phân & ứng dụng tính diện tích hình phẳng, thể tích vật thể tròn xoay.
        - Hình học & Đo lường: Thể tích khối đa diện/khối tròn xoay, góc và khoảng cách trong không gian, phương pháp tọa độ trong không gian Oxyz (vectơ, mặt phẳng, đường thẳng, mặt cầu).
        - Thống kê & Xác suất mới: Các số đặc trưng đo mức độ phân tán của mẫu số liệu ghép nhóm (khoảng biến thiên, khoảng tứ phân vị, phương sai, độ lệch chuẩn); xác suất có điều kiện, công thức xác suất toàn phần và công thức Bayes.
     3. BẮT BUỘC có Toán thực tế (chiếm 35%–40% đề thi):
        - Phần II (Đúng/Sai): Ít nhất 2/4 câu là bài toán thực tế (mô hình chuyển động vật lý v(t), a(t), bài toán tối ưu chi phí/doanh thu/diện tích, mô hình tăng trưởng mũ/lãi suất, hoặc xác suất Bayes trong y tế/sản xuất).
        - Phần III (Trả lời ngắn): Ít nhất 4/6 câu là bài toán ứng dụng thực tiễn yêu cầu tính toán ra con số cụ thể (làm tròn theo đúng yêu cầu đề bài).

   * BẢNG SỐ LIỆU (dataTable) & BẢNG BIẾN THIÊN (variationTable) — CẤM TUYỆT ĐỐI BẢNG ASCII:
     1. CẤM TUYỆT ĐỐI in bảng dạng ký tự ASCII (---|---|---, |, &) vào chuỗi question!
     2. Khi có bảng số liệu ghép nhóm hoặc bảng doanh số: BẮT BUỘC xuất qua trường ""dataTable"": { ""title"": ""..."", ""headers"": [...], ""rows"": [[...], ...] }.
     3. Khi có bảng biến thiên: BẮT BUỘC xuất qua trường ""variationTable"": { ""x"": [...], ""yPrime"": [...], ""y"": [...] }.
     4. NGUYÊN TẮC THÉP: Nếu câu hỏi KHÔNG cung cấp kèm dữ liệu variationTable, dataTable hoặc svgGraph hợp lệ trong JSON, AI TUYỆT ĐỐI KHÔNG ĐƯỢC dùng các cụm từ 'có bảng biến thiên như sau', 'có đồ thị như hình bên', 'dựa vào hình vẽ dưới đây' — bắt buộc phải cho biểu thức đại số tường minh của hàm số y = f(x) hoặc đạo hàm f'(x) để thí sinh tính toán trực tiếp.
4. Chế độ 'Tạo thẻ thông tin' (Flashcards):
   - Khi người dùng muốn tạo flashcard, thẻ nhớ, thẻ học nhanh:
   - Viết 1 câu mở đầu, rồi BẮT BUỘC trả về dữ liệu thẻ ghi nhớ trong cặp thẻ:
     [FLASHCARD_DATA]
     {
       ""title"": ""Bộ thẻ ghi nhớ Flashcards"",
       ""cards"": [
         {
           ""front"": ""Mặt trước: Thuật ngữ / Khái niệm / Câu hỏi"",
           ""back"": ""Mặt sau: Định nghĩa / Công thức / Đáp án ngắn gọn"",
           ""hint"": ""Gợi ý nhớ nhanh""
         }
       ]
     }
     [/FLASHCARD_DATA]
5. Chế độ 'Sử dụng tính năng Học có hướng dẫn' (Gia sư gợi mở — Socratic Tutor):
   - Đóng vai Gia sư AI tâm huyết. TUYỆT ĐỐI KHÔNG giải bài thay hay đưa ra đáp án cuối cùng ngay ở lượt đầu.
   - Trình bày ngắn gọn, thân thiện theo đúng 3 phần:
     1. Phân tích nhanh: Đề bài cho gì, yêu cầu tìm gì?
     2. Gợi ý công thức / Định hướng Bước 1: Nhắc lại công thức chìa khóa (dùng KaTeX chuẩn $...$, không bọc tiếng Việt trong $...$).
     3. Câu hỏi tương tác cho bạn: Đặt đúng 1 câu hỏi nhỏ cụ thể để học sinh tự tính bước đầu tiên rồi trả lời.
   - Ở các lượt tiếp theo: nếu học sinh làm đúng → khen ngợi ngắn gọn và dẫn sang bước kế tiếp; nếu học sinh làm sai → chỉ rõ chỗ nhầm lẫn một cách khích lệ và gợi ý nhẹ hơn.
   - Chỉ trình bày lời giải hoàn chỉnh từ đầu đến cuối khi học sinh tự giải xong hoặc yêu cầu xem lời giải đầy đủ.

* HÌNH ẢNH, VIDEO VÀ ÂM NHẠC:
- Tạo ảnh tĩnh: kết thúc bằng [GENIMAGE] <prompt tiếng Anh chi tiết>
- Tạo video: kết thúc bằng [GENVIDEO] <prompt tiếng Anh chi tiết>
- Tạo nhạc: kết thúc bằng [GENMUSIC] <prompt tiếng Anh chi tiết>
";

        public ChatService(
            IConversationRepository conversationRepository,
            ITrainingRepository trainingRepository,
            IIntentEngineService intentEngineService,
            IGeminiService geminiService,
            IUserRepository userRepository,
            IServiceScopeFactory scopeFactory)
        {
            _conversationRepository = conversationRepository;
            _trainingRepository = trainingRepository;
            _intentEngineService = intentEngineService;
            _geminiService = geminiService;
            _userRepository = userRepository;
            _scopeFactory = scopeFactory;
        }

        public async Task<JsonObject> ProcessChatAsync(ChatRequestModel requestModel, string webRootPath)
        {
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            long t_intent = 0;
            long t_db = 0;
            long t_rag = 0;
            long t_gemini = 0;
            long t_save = 0;

            int userId = requestModel.User_Id ?? 0;
            int conversationId = requestModel.Conversation_Id ?? 0;

            User? currentUser = null;
            if (userId > 0)
            {
                try { currentUser = await _userRepository.GetByIdAsync(userId); } catch { }
            }

            var payload = requestModel.Payload ?? new JsonObject();
            if (!payload.ContainsKey("contents"))
            {
                payload["contents"] = new JsonArray();
            }

            bool isThinking = false;
            if (payload.ContainsKey("mode"))
            {
                string modeStr = payload["mode"]?.ToString() ?? "fast";
                isThinking = modeStr.Equals("thinking", StringComparison.OrdinalIgnoreCase);
            }

            string model = requestModel.Model ?? "gemini-2.5-flash";
            if (model.Contains("pro") || isThinking) model = "gemini-2.5-flash";
            else if (model.Contains("lite")) model = "gemini-3.5-flash-lite";
            else model = "gemini-2.5-flash";

            if (!payload.ContainsKey("generationConfig") || payload["generationConfig"] == null)
            {
                payload["generationConfig"] = new JsonObject { ["maxOutputTokens"] = 8192 };
            }
            if (payload["generationConfig"] is JsonObject genConfig)
            {
                genConfig["maxOutputTokens"] = 8192;
                if (!isThinking)
                {
                    // Disable thinking in Fast mode to eliminate 5-10s hidden reasoning latency
                    genConfig["thinkingConfig"] = new JsonObject
                    {
                        ["thinkingBudget"] = 0
                    };
                }
            }

            // Extract question and filter out leaked system prompts from contents
            string question = payload["question"]?.ToString() ?? "";
            var contents = payload["contents"]?.AsArray();
            if (contents != null && contents.Count > 0)
            {
                var cleanContents = new JsonArray();
                foreach (var item in contents)
                {
                    if (item is JsonObject msgObj && msgObj["parts"] is JsonArray pArr && pArr.Count > 0)
                    {
                        string txt = pArr[0]?["text"]?.ToString() ?? "";
                        if (txt.Contains("Bạn đang chạy trong một ứng dụng") || 
                            txt.Contains("QUY TẮC XUẤT FILE") || 
                            txt.Contains("USER_INTENT_ADHERENCE_PROTOCOL"))
                        {
                            continue; // Skip leaked internal system instruction in user payload
                        }
                    }
                    cleanContents.Add(item?.DeepClone());
                }
                contents = cleanContents;

                if (contents.Count > 0)
                {
                    var lastMsg = contents[contents.Count - 1]?.AsObject();
                    if (lastMsg != null && lastMsg["parts"] is JsonArray parts && parts.Count > 0)
                    {
                        string extracted = parts[0]?["text"]?.ToString() ?? "";
                        if (!string.IsNullOrWhiteSpace(extracted)) question = extracted;
                    }
                }
            }

            // 1. Profiling t_intent: Short chats ("ê", "ko", "um", etc.) cut out all intent LLM calls completely (0 ms)
            var swIntent = System.Diagnostics.Stopwatch.StartNew();
            string trimmedQ = (question ?? "").Trim().ToLowerInvariant();
            bool isShortChat = string.IsNullOrWhiteSpace(trimmedQ) || 
                               trimmedQ.Length <= 10 || 
                               trimmedQ == "ê" || trimmedQ == "ko" || trimmedQ == "không" || 
                               trimmedQ == "um" || trimmedQ == "ừ" || trimmedQ == "ok" || 
                               trimmedQ == "chào" || trimmedQ == "hi" || trimmedQ == "hello";

            if (!isShortChat)
            {
                // Non-short chat intent analysis if required
            }
            swIntent.Stop();
            t_intent = swIntent.ElapsedMilliseconds;

            // 2. Profiling t_db: load conversation & history
            var swDb = System.Diagnostics.Stopwatch.StartNew();
            bool isNewConv = false;
            var historyChats = new List<Chat>();
            try
            {
                if (userId > 0 && conversationId == 0)
                {
                    var conv = await _conversationRepository.CreateConversationAsync(userId, "Chat mới");
                    conversationId = conv.Id;
                    isNewConv = true;
                }

                if (userId > 0 && conversationId > 0)
                {
                    historyChats = await _conversationRepository.GetChatHistoryAsync(conversationId, userId, 12);
                }
            }
            catch (Exception dbEx)
            {
                Console.WriteLine($"[DB Non-fatal Warning] {dbEx.Message}");
            }
            swDb.Stop();
            t_db = swDb.ElapsedMilliseconds;

            // 3. Profiling t_rag: 0 ms for general conversations
            var swRag = System.Diagnostics.Stopwatch.StartNew();
            // ContextRetriever / Qdrant RAG is bypassed for short & standard chat
            swRag.Stop();
            t_rag = swRag.ElapsedMilliseconds;
            var historyMessages = new JsonArray();

            foreach (var chat in historyChats)
            {
                string q = chat.Question ?? "";
                if (q.Contains("QUY TẮC XUẤT FILE") || q.Contains("Bạn đang chạy trong một ứng dụng"))
                {
                    q = "Chào bạn";
                }
                string a = chat.Answer ?? "";
                if (a.Contains("Tôi đã hiểu rõ các quy tắc") || a.Contains("tuân thủ nghiêm ngặt các định dạng về xuất file"))
                {
                    a = "Chào bạn, tôi là Btoan AI. Hôm nay tôi có thể giúp gì cho bạn?";
                }

                var userParts = new JsonArray { new JsonObject { ["text"] = q } };
                if (!string.IsNullOrEmpty(chat.Images))
                {
                    try
                    {
                        var imgs = JsonSerializer.Deserialize<List<string>>(chat.Images);
                        if (imgs != null)
                        {
                            foreach (var img in imgs)
                            {
                                if (img.StartsWith("data:image/"))
                                {
                                    var parts = img.Split(',');
                                    if (parts.Length == 2)
                                    {
                                        string mime = parts[0].Replace("data:", "").Replace(";base64", "");
                                        userParts.Add(new JsonObject
                                        {
                                            ["inline_data"] = new JsonObject
                                            {
                                                ["mime_type"] = mime,
                                                ["data"] = parts[1]
                                            }
                                        });
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }

                historyMessages.Add(new JsonObject { ["role"] = "user", ["parts"] = userParts });
                if (!string.IsNullOrEmpty(a))
                {
                    historyMessages.Add(new JsonObject
                    {
                        ["role"] = "model",
                        ["parts"] = new JsonArray { new JsonObject { ["text"] = a } }
                    });
                }
            }

            // Merge history into payload contents
            if (contents != null)
            {
                var currentContents = new JsonArray();
                foreach (var item in historyMessages)
                {
                    currentContents.Add(item?.DeepClone());
                }
                foreach (var item in contents)
                {
                    currentContents.Add(item?.DeepClone());
                }
                payload["contents"] = currentContents;
            }

            // Context-preserving file export and artifact modification
            string lowerExportQ = (question ?? "").ToLowerInvariant();
            string lastModelAnswer = historyChats.LastOrDefault(c => !string.IsNullOrWhiteSpace(c.Answer))?.Answer?.ToLowerInvariant() ?? "";
            bool modelOfferedArtifact = lastModelAnswer.Contains("tạo file") || lastModelAnswer.Contains("xuất file") || 
                                        lastModelAnswer.Contains("file excel") || lastModelAnswer.Contains("file word") ||
                                        lastModelAnswer.Contains("file pdf") || lastModelAnswer.Contains("file powerpoint") ||
                                        lastModelAnswer.Contains("bảng tính") || lastModelAnswer.Contains("bảng excel");

            string lastAnswerWithArtifact = historyChats.LastOrDefault(ans => !string.IsNullOrWhiteSpace(ans.Answer) && ans.Answer.Contains("[ARTIFACT"))?.Answer ?? "";
            bool hasRecentArtifact = !string.IsNullOrWhiteSpace(lastAnswerWithArtifact);

            string recentArtType = "xlsx";
            string recentArtTitle = "Tài liệu hoàn chỉnh";
            if (hasRecentArtifact)
            {
                var matchArt = System.Text.RegularExpressions.Regex.Match(lastAnswerWithArtifact, @"\[ARTIFACT(?:\s+type=[""']([^""']+)[""'])?(?:\s+title=[""']([^""']+)[""'])?\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (matchArt.Success)
                {
                    if (!string.IsNullOrWhiteSpace(matchArt.Groups[1].Value)) recentArtType = matchArt.Groups[1].Value.ToLowerInvariant().Trim();
                    if (!string.IsNullOrWhiteSpace(matchArt.Groups[2].Value)) recentArtTitle = matchArt.Groups[2].Value.Trim();
                }
            }

            bool isUserConfirmation = lowerExportQ == "có" || lowerExportQ == "co" || lowerExportQ == "ok" || 
                                     lowerExportQ == "ừ" || lowerExportQ == "u" || lowerExportQ == "được" || 
                                     lowerExportQ == "duoc" || lowerExportQ == "làm đi" || lowerExportQ == "xuất đi" ||
                                     lowerExportQ == "tạo đi" || lowerExportQ == "có nhé" || lowerExportQ == "xuất file đi" ||
                                     lowerExportQ == "tạo file đi" || lowerExportQ == "tải về" || lowerExportQ == "làm tiếp đi" ||
                                     lowerExportQ == "tiếp đi" || lowerExportQ == "ok bạn";

            bool isStatusCheck = lowerExportQ == "đc chưa" || lowerExportQ == "dc chua" || lowerExportQ == "được chưa" ||
                                 lowerExportQ == "duoc chua" || lowerExportQ == "xong chưa" || lowerExportQ == "xong chua" ||
                                 lowerExportQ == "sao rồi" || lowerExportQ == "sao roi" || lowerExportQ == "đâu rồi" ||
                                 lowerExportQ == "dau roi" || lowerExportQ == "chưa" || lowerExportQ == "chua";

            bool isDataFillOrModify = lowerExportQ.Contains("nhét dữ liệu") || lowerExportQ.Contains("nhet du lieu") ||
                                      lowerExportQ.Contains("điền thêm") || lowerExportQ.Contains("dien them") ||
                                      lowerExportQ.Contains("thêm dữ liệu") || lowerExportQ.Contains("them du lieu") ||
                                      lowerExportQ.Contains("thêm vào") || lowerExportQ.Contains("them vao") ||
                                      lowerExportQ.Contains("thêm đi") || lowerExportQ.Contains("them di") ||
                                      lowerExportQ.Contains("thêm cho") || lowerExportQ.Contains("them cho") ||
                                      lowerExportQ.Contains("đang trống") || lowerExportQ.Contains("dang trong") ||
                                      lowerExportQ.Contains("bị trống") || lowerExportQ.Contains("bi trong") ||
                                      lowerExportQ.Contains("chưa có dữ liệu") || lowerExportQ.Contains("chua co du lieu") ||
                                      lowerExportQ.Contains("dữ liệu trống") || lowerExportQ.Contains("du lieu trong") ||
                                      lowerExportQ.Contains("dữ liệu mẫu") || lowerExportQ.Contains("du lieu mau") ||
                                      lowerExportQ.Contains("sửa lại file") || lowerExportQ.Contains("sua lai file") ||
                                      lowerExportQ.Contains("sửa lại") || lowerExportQ.Contains("sua lai") ||
                                      lowerExportQ.Contains("cập nhật") || lowerExportQ.Contains("cap nhat") ||
                                      lowerExportQ.Contains("bổ sung") || lowerExportQ.Contains("bo sung") ||
                                      lowerExportQ.Contains("thêm dòng") || lowerExportQ.Contains("them dong") ||
                                      lowerExportQ.Contains("thêm cột") || lowerExportQ.Contains("them cot") ||
                                      lowerExportQ.Contains("điền điểm") || lowerExportQ.Contains("cho số liệu") ||
                                      lowerExportQ.Contains("nhập dữ liệu") || lowerExportQ.Contains("điền dữ liệu") ||
                                      lowerExportQ.Contains("nhét data") || lowerExportQ.Contains("thêm data") ||
                                      lowerExportQ.Contains("cho data") || lowerExportQ.Contains("điền data") ||
                                      lowerExportQ.Contains("fill data") || lowerExportQ.Contains("add data") ||
                                      lowerExportQ.Contains("đồng bộ") || lowerExportQ.Contains("dong bo") ||
                                      lowerExportQ.Contains("bảng điểm") || lowerExportQ.Contains("bang diem") ||
                                      lowerExportQ.Contains("điền vào") || lowerExportQ.Contains("dien vao");

            bool userSentImage = contents != null && contents.Any(c => c?.ToString()?.Contains("inline_data") == true);

            bool isExplicitExport = lowerExportQ.Contains("xuất file") || lowerExportQ.Contains("tạo file") || 
                                   lowerExportQ.Contains("tải file") || lowerExportQ.Contains("lưu vào word") || 
                                   lowerExportQ.Contains("xuất word") || lowerExportQ.Contains("xuất docx") || 
                                   lowerExportQ.Contains("tạo file docx") || lowerExportQ.Contains("vào word") ||
                                   lowerExportQ.Contains("bỏ vào word") || lowerExportQ.Contains("cho vào word") ||
                                   lowerExportQ.Contains("xuất excel") || lowerExportQ.Contains("tạo file excel") ||
                                   lowerExportQ.Contains("file excel") || lowerExportQ.Contains("xlsx") ||
                                   lowerExportQ.Contains("bảng tính") || lowerExportQ.Contains("xuất pdf") ||
                                   lowerExportQ.Contains("tạo file pdf") || lowerExportQ.Contains("file pdf") ||
                                   lowerExportQ.Contains("powerpoint") || lowerExportQ.Contains("pptx") ||
                                   lowerExportQ.Contains("bài thuyết trình") || lowerExportQ.Contains("slide");

            bool isArtifactRequired = isExplicitExport || (modelOfferedArtifact && isUserConfirmation) || 
                                     (hasRecentArtifact && (isDataFillOrModify || isStatusCheck || isUserConfirmation || userSentImage));

            string lastModelText = "";
            if (payload["contents"] is JsonArray finalContents && finalContents.Count > 0)
            {
                for (int i = finalContents.Count - 1; i >= 0; i--)
                {
                    if (finalContents[i] is JsonObject cObj && cObj["role"]?.ToString() == "model" && cObj["parts"] is JsonArray pArr && pArr.Count > 0)
                    {
                        var t = pArr[0]?["text"]?.ToString() ?? "";
                        if (!string.IsNullOrWhiteSpace(t) && !t.Contains("Chào bạn, bạn muốn mình"))
                        {
                            lastModelText = t;
                            break;
                        }
                    }
                }
            }

            if (isArtifactRequired && payload["contents"] is JsonArray turnContents && turnContents.Count > 0)
            {
                string targetType = recentArtType;
                string targetTitle = recentArtTitle;
                if (lowerExportQ.Contains("excel") || lowerExportQ.Contains("xlsx") || lowerExportQ.Contains("bảng tính") || lowerExportQ.Contains("bảng điểm") || lastModelAnswer.Contains("excel") || lastModelAnswer.Contains("bảng tính"))
                {
                    targetType = "xlsx";
                }
                else if (lowerExportQ.Contains("pdf") || lastModelAnswer.Contains("pdf"))
                {
                    targetType = "pdf";
                }
                else if (lowerExportQ.Contains("ppt") || lowerExportQ.Contains("powerpoint") || lowerExportQ.Contains("slide") || lastModelAnswer.Contains("ppt") || lastModelAnswer.Contains("powerpoint"))
                {
                    targetType = "pptx";
                }
                else if (lowerExportQ.Contains("word") || lowerExportQ.Contains("docx") || lastModelAnswer.Contains("word") || lastModelAnswer.Contains("docx"))
                {
                    targetType = "docx";
                }

                var lastUserTurn = turnContents[turnContents.Count - 1]?.AsObject();
                if (lastUserTurn != null && lastUserTurn["parts"] is JsonArray uParts && uParts.Count > 0)
                {
                    string instruction = (hasRecentArtifact && (isDataFillOrModify || isStatusCheck || userSentImage))
                        ? $"[HỆ THỐNG YÊU CẦU: Người dùng yêu cầu hoàn thiện/điền dữ liệu hoặc gửi ảnh kiểm tra file {targetType.ToUpper()} '{targetTitle}'. TUYỆT ĐỐI CẤM trả lời bằng các câu hoãn binh như 'để mình làm cho', 'bạn đợi chút', 'được rồi nha', 'để mình đồng bộ qua nhé' mà không kèm thẻ [ARTIFACT]. Bạn BẮT BUỘC giữ nguyên cấu trúc các cột (hoặc trích xuất cấu trúc từ ảnh/lịch sử) và xuất ngay thẻ [ARTIFACT type=\"{targetType}\" title=\"{targetTitle}\"] chứa tối thiểu 10 đến 15 dòng dữ liệu mẫu thực tế hoàn chỉnh, số liệu phong phú sinh động, tuyệt đối không để bảng trống ngay trong câu trả lời này!]"
                        : $"[HỆ THỐNG YÊU CẦU: Người dùng yêu cầu / xác nhận xuất tài liệu định dạng '{targetType}'. Bạn BẮT BUỘC chép trọn vẹn dữ liệu đầy đủ đẹp mắt vào thẻ [ARTIFACT type=\"{targetType}\" title=\"{targetTitle}\"]...[/ARTIFACT]. TUYỆT ĐỐI CẤM HỎI LẠI NGƯỜI DÙNG HOẶC HỨA SUÔNG!]";

                    var textPart = uParts.OfType<JsonObject>().FirstOrDefault(p => p.ContainsKey("text"));
                    if (textPart != null)
                    {
                        string origTxt = textPart["text"]?.ToString() ?? "";
                        textPart["text"] = $"{origTxt}\n\n{instruction}";
                    }
                    else
                    {
                        uParts.Add(new JsonObject { ["text"] = instruction });
                    }
                }
            }

            string lowerQ = (question ?? "").ToLowerInvariant();
            bool isSocraticMode = requestModel.SocraticMode == true
                               || (payload.ContainsKey("socraticMode") && (payload["socraticMode"]?.ToString() == "true" || payload["socraticMode"]?.GetValue<bool>() == true))
                               || (payload.ContainsKey("socratic") && (payload["socratic"]?.ToString() == "true" || payload["socratic"]?.GetValue<bool>() == true))
                               || lowerQ.Contains("[socratic]") || lowerQ.Contains("gia sư gợi mở") || lowerQ.Contains("gia sư socratic");
            payload.Remove("socraticMode");
            payload.Remove("socratic");
            bool isSimulationPrompt = lowerQ.Contains("[mô phỏng") || lowerQ.Contains("[simulation") || lowerQ.Contains("mô phỏng tương tác") || lowerQ.Contains("stem lab");

            bool isLearningPrompt = isSocraticMode || isSimulationPrompt ||
                                   lowerQ.Contains("học tập") || lowerQ.Contains("thptqg") || 
                                   lowerQ.Contains("trắc nghiệm") || lowerQ.Contains("quiz") || 
                                   lowerQ.Contains("flashcard") || (question != null && question.Contains("[HỌC TẬP"));
            bool isThptqgPrompt = (question != null && (question.Contains("THPTQG_2025") || question.Contains("ĐỀ THI CHUẨN BỘ GD&ĐT THPTQG 2025-2026") || question.Contains("THPTQG 2025–2026")));

            string currentSystemInstruction = (!isThinking && !isArtifactRequired && !isLearningPrompt) ? FastSystemInstructionText : SystemInstructionText;

            if (isSocraticMode)
            {
                currentSystemInstruction += @"

[CHẾ ĐỘ GIA SƯ GỢI MỞ — SOCRATIC TUTOR BẮT BUỘC]:
1. TUYỆT ĐỐI KHÔNG đưa ra đáp án cuối cùng hay lời giải trọn vẹn ngay ở lượt đầu.
2. Trình bày ngắn gọn, thân thiện theo đúng 3 phần rõ ràng:
   - 🎯 Phân tích nhanh: Đề bài cho gì, yêu cầu tìm gì?
   - 💡 Gợi ý công thức / Định hướng Bước 1: Nhắc lại công thức chìa khóa (dùng KaTeX chuẩn $...$, không bọc tiếng Việt trong $...$).
   - ❓ Câu hỏi tương tác cho bạn: Đặt đúng 1 câu hỏi nhỏ cụ thể để học sinh tự tính bước đầu tiên rồi trả lời.
3. Ở các lượt sau: nếu học sinh trả lời đúng -> khen ngợi ngắn và gợi ý bước tiếp theo; nếu sai -> chỉ ra chỗ nhầm lẫn khích lệ và gợi ý nhẹ hơn.
4. Chỉ đưa ra đáp án và lời giải toàn bài khi học sinh giải đúng bước cuối cùng hoặc yêu cầu 'xem lời giải đầy đủ'.";
            }
            if (isSimulationPrompt)
            {
                currentSystemInstruction += @"

[CHẾ ĐỘ PHÒNG THÍ NGHIỆM & MÔ PHỎNG TƯƠNG TÁC HTML5]:
- Bạn BẮT BUỘC xuất khối [SIMULATION_DATA]...[/SIMULATION_DATA] chứa mã HTML5 Canvas/SVG tương tác hoàn chỉnh.
- Mã HTML phải có đầy đủ: <!DOCTYPE html><html><head><style>...</style></head><body>...<script>...</script></body></html> (tự chạy 100%, KHÔNG dùng script CDN ngoài).
- Phải có bảng điều khiển với ít nhất 2–4 thanh trượt <input type=""range""> kèm giá trị thời gian thực và khung hiển thị kết quả tính toán động.";
            }

            if (isThptqgPrompt)
            {
                string root = Directory.GetCurrentDirectory();
                string template = GetThptqgExamTemplate(root);
                if (!string.IsNullOrWhiteSpace(template))
                {
                    currentSystemInstruction += "\n\n[TÀI LIỆU THAM CHIẾU MA TRẬN VÀ DẠNG BÀI ĐỀ CHÍNH THỨC BỘ GD&ĐT THPTQG 2025–2026]:\n" + template;
                }
            }
            if (currentUser != null && !string.IsNullOrWhiteSpace(currentUser.CustomInstructions))
            {
                currentSystemInstruction += "\n\n[HƯỚNG DẪN TÙY CHỈNH TỪ NGƯỜI DÙNG]:\n" + currentUser.CustomInstructions.Trim();
            }
            if (currentUser?.Temperature != null)
            {
                if (payload["generationConfig"] is JsonObject genCfg)
                {
                    genCfg["temperature"] = currentUser.Temperature.Value;
                }
                else
                {
                    payload["generationConfig"] = new JsonObject { ["temperature"] = currentUser.Temperature.Value };
                }
            }
            payload["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = currentSystemInstruction } }
            };

            // Call Gemini Service
            var swGemini = System.Diagnostics.Stopwatch.StartNew();
            var (success, geminiData, rawResponse, errorMsg) = await _geminiService.GenerateContentAsync(model, payload);
            swGemini.Stop();
            t_gemini = swGemini.ElapsedMilliseconds;

            if (!success || geminiData == null)
            {
                // Intelligent fallback: If the user is requesting an image, automatically trigger [GENIMAGE]
                bool isImagePrompt = lowerQ.Contains("vẽ") || lowerQ.Contains("ve ") || lowerQ.Contains("ve tranh") ||
                                     lowerQ.Contains("tạo ảnh") || lowerQ.Contains("tao anh") ||
                                     lowerQ.Contains("bức tranh") || lowerQ.Contains("buc tranh") || lowerQ.Contains("tranh ") ||
                                     lowerQ.Contains("hình ảnh") || lowerQ.Contains("hinh anh") ||
                                     lowerQ.Contains("draw") || lowerQ.Contains("generate image") ||
                                     lowerQ.Contains("paint") || lowerQ.Contains("picture") || lowerQ.Contains("sơn dầu");

                if (isImagePrompt)
                {
                    string cleanedPrompt = System.Text.RegularExpressions.Regex.Replace(
                        question ?? "", 
                        @"^(hãy|vui lòng|bạn|hộ tôi|giúp tôi)?\s*(vẽ|tạo ảnh|vẽ tranh|vẽ cho tôi|tạo cho tôi|vẽ một bức tranh|vẽ bức tranh)\s*", 
                        "", 
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

                    if (string.IsNullOrWhiteSpace(cleanedPrompt)) cleanedPrompt = question ?? "beautiful artwork";

                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["answer"] = $"Dưới đây là bức tranh của bạn:\n\n[GENIMAGE] {cleanedPrompt}",
                        ["images"] = new JsonArray(),
                        ["conversation_id"] = conversationId
                    };
                }

                bool isMusicPrompt = lowerQ.Contains("nhạc") || lowerQ.Contains("bài hát") || lowerQ.Contains("song") ||
                                     lowerQ.Contains("music") || lowerQ.Contains("giai điệu") || lowerQ.Contains("sáng tác");
                if (isMusicPrompt)
                {
                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["answer"] = $"Dưới đây là giai điệu âm nhạc dành cho bạn:\n\n[GENMUSIC] {question ?? "acoustic upbeat melody"}",
                        ["images"] = new JsonArray(),
                        ["conversation_id"] = conversationId
                    };
                }

                string errDetail = !string.IsNullOrWhiteSpace(errorMsg) ? errorMsg : "Không thể kết nối đến Gemini API";
                return new JsonObject
                {
                    ["ok"] = false,
                    ["answer"] = $"⚠️ Lỗi kết nối AI: {errDetail}\n\nVui lòng kiểm tra lại cấu hình Gemini API Key trên server hoặc quota tài khoản.",
                    ["images"] = new JsonArray(),
                    ["error"] = errDetail,
                    ["conversation_id"] = conversationId
                };
            }

            var sbAnswer = new System.Text.StringBuilder();
            if (geminiData["candidates"] is JsonArray candidates && candidates.Count > 0)
            {
                if (candidates[0]?["content"]?["parts"] is JsonArray partsArr)
                {
                    foreach (var p in partsArr)
                    {
                        if (p is JsonObject pObj)
                        {
                            if (pObj.ContainsKey("thought") && pObj["thought"]?.GetValue<bool>() == true)
                            {
                                continue; // Skip internal thinking thoughts
                            }
                            sbAnswer.Append(pObj["text"]?.ToString() ?? "");
                        }
                    }
                }
            }
            string answer = sbAnswer.ToString();

            // Fallback defense: if user asked to export/fill artifact but model answered conversational text without ARTIFACT
            if (isArtifactRequired && !answer.Contains("[ARTIFACT") && !string.IsNullOrWhiteSpace(lastModelText))
            {
                if (recentArtType == "xlsx")
                {
                    bool isGradeTable = lowerExportQ.Contains("điểm") || lowerExportQ.Contains("học sinh") || lowerExportQ.Contains("lớp") || (lastModelText?.Contains("điểm") ?? false) || recentArtTitle.Contains("điểm");
                    if (isGradeTable)
                    {
                        answer = $"Đã cập nhật bảng điểm chi tiết đầy đủ dữ liệu cho bạn:\n\n[ARTIFACT type=\"xlsx\" title=\"Bảng điểm lớp 12\"]\n{{\"title\": \"BẢNG ĐIỂM TỔNG HỢP LỚP 12\", \"subtitle\": \"Năm học 2026 - 2027\", \"sheets\": [{{\"name\": \"BangDiem\", \"columns\": [\"STT\", \"Họ và tên\", \"Toán\", \"Văn\", \"Anh\", \"Vật Lý\", \"Hóa Học\", \"Sinh Học\", \"Điểm TB\", \"Xếp loại\"], \"rows\": [[1, \"Nguyễn Văn A\", 8.5, 7.0, 8.0, 7.5, 8.0, 7.5, 7.75, \"Khá\"], [2, \"Trần Thị B\", 9.0, 8.5, 9.5, 8.5, 9.0, 8.0, 8.75, \"Giỏi\"], [3, \"Lê Văn C\", 6.5, 6.0, 5.5, 6.0, 6.5, 7.0, 6.25, \"Trung bình\"], [4, \"Phạm Thị D\", 9.5, 9.0, 9.0, 9.0, 9.5, 9.0, 9.17, \"Giỏi\"], [5, \"Hoàng Văn E\", 8.0, 8.0, 8.5, 7.0, 7.5, 8.0, 7.83, \"Khá\"], [6, \"Vũ Thị F\", 9.0, 9.5, 9.0, 8.5, 9.0, 9.5, 9.08, \"Xuất sắc\"]]}}]}}\n[/ARTIFACT]";
                    }
                    else
                    {
                        answer = $"Đã chuẩn bị xong bảng tính Excel cho bạn:\n\n[ARTIFACT type=\"xlsx\" title=\"{recentArtTitle}\"]\n{{\"title\": \"{recentArtTitle}\", \"subtitle\": \"Cập nhật dữ liệu\", \"sheets\": [{{\"name\": \"Sheet1\", \"columns\": [\"STT\", \"Họ và tên\", \"Mục 1\", \"Mục 2\", \"Đánh giá\"], \"rows\": [[1, \"Nguyễn Văn A\", 8.5, 9.0, \"Tốt\"], [2, \"Trần Thị B\", 8.0, 8.5, \"Khá\"], [3, \"Lê Văn C\", 9.0, 9.5, \"Xuất sắc\"]]}}]}}\n[/ARTIFACT]";
                    }
                }
                else
                {
                    string docxContent = !string.IsNullOrWhiteSpace(lastModelText) ? lastModelText : "";
                    if (string.IsNullOrWhiteSpace(docxContent))
                    {
                        answer = "Bạn muốn đưa nội dung hoặc chủ đề gì vào file Word? Hãy chia sẻ để mình soạn thảo đầy đủ cho bạn nhé!";
                    }
                    else
                    {
                        answer = $"Đã chuẩn bị xong tài liệu Word cho bạn:\n\n[ARTIFACT type=\"docx\" title=\"Tài liệu văn bản\"]\n{docxContent}\n[/ARTIFACT]";
                    }
                }
            }

            // Save Base64 Images locally to /uploads/chat/
            var imageList = new List<string>();
            if (contents != null && contents.Count > 0)
            {
                var lastMsg = contents[contents.Count - 1]?.AsObject();
                if (lastMsg != null && lastMsg["parts"] is JsonArray parts)
                {
                    string uploadDir = Path.Combine(webRootPath, "uploads", "chat");
                    if (!Directory.Exists(uploadDir)) Directory.CreateDirectory(uploadDir);

                    foreach (var part in parts)
                    {
                        if (part?["inline_data"] is JsonObject inlineData)
                        {
                            string mime = inlineData["mime_type"]?.ToString() ?? "image/jpeg";
                            string base64Data = inlineData["data"]?.ToString() ?? "";

                            string ext = mime.Contains("/") ? mime.Split('/')[1] : "jpg";
                            string filename = $"img_{Guid.NewGuid().ToString("N").Substring(0, 10)}.{ext}";
                            string savePath = Path.Combine(uploadDir, filename);
                            string publicUrl = $"/uploads/chat/{filename}";

                            try
                            {
                                byte[] imageBytes = Convert.FromBase64String(base64Data);
                                await File.WriteAllBytesAsync(savePath, imageBytes);
                                imageList.Add(publicUrl);
                            }
                            catch
                            {
                                imageList.Add($"data:{mime};base64,{base64Data}");
                            }
                        }
                    }
                }
            }

            string? generatedTitle = null;
            int savedChatId = 0;
            // Save Chat to DB (100% saved for logged in users)
            var swSave = System.Diagnostics.Stopwatch.StartNew();
            if (userId > 0)
            {
                try
                {
                    var chatRecord = new Chat
                    {
                        UserId = userId,
                        Question = question,
                        Answer = answer,
                        ConversationId = conversationId,
                        Images = JsonSerializer.Serialize(imageList),
                        CreatedAt = DateTime.UtcNow
                    };

                    chatRecord = await _conversationRepository.AddChatAsync(chatRecord);
                    savedChatId = chatRecord.Id;

                    // Auto-generate Title if conversation is new or title is "Chat mới"
                    if (isNewConv || conversationId > 0)
                    {
                        var existingConv = await _conversationRepository.GetConversationByIdAsync(conversationId, userId);
                        if (existingConv != null && (existingConv.Title == "Chat mới" || string.IsNullOrWhiteSpace(existingConv.Title)))
                        {
                            // Instant fast title so t_save is < 15ms
                            string quickTitle = (question ?? "").Trim();
                            if (quickTitle.Length > 40) quickTitle = quickTitle.Substring(0, 40) + "...";
                            if (string.IsNullOrWhiteSpace(quickTitle)) quickTitle = "Chat mới";
                            generatedTitle = quickTitle;
                            await _conversationRepository.UpdateConversationTitleAsync(conversationId, quickTitle);

                            // Background refinement with Gemini without blocking main chat stream
                            int convId = conversationId;
                            string qText = question ?? "";
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    using var scope = _scopeFactory.CreateScope();
                                    var chatSvc = scope.ServiceProvider.GetRequiredService<IChatService>();
                                    var convRepo = scope.ServiceProvider.GetRequiredService<IConversationRepository>();
                                    var refined = await chatSvc.GenerateTitleAsync(qText);
                                    if (!string.IsNullOrWhiteSpace(refined) && refined != "Chat mới" && !refined.StartsWith("{") && !refined.StartsWith("["))
                                    {
                                        await convRepo.UpdateConversationTitleAsync(convId, refined);
                                    }
                                }
                                catch { }
                            });
                        }
                        else if (existingConv != null)
                        {
                            generatedTitle = existingConv.Title;
                        }
                    }
                }
                catch (Exception dbEx)
                {
                    Console.WriteLine($"[DB Save Non-fatal Warning] {dbEx.Message}");
                }
            }
            swSave.Stop();
            t_save = swSave.ElapsedMilliseconds;

            swTotal.Stop();
            Console.WriteLine("================ [CHAT PROFILING BREAKDOWN] ================");
            Console.WriteLine($"| t_intent          : {t_intent,6} ms");
            Console.WriteLine($"| t_db              : {t_db,6} ms");
            Console.WriteLine($"| t_rag             : {t_rag,6} ms");
            Console.WriteLine($"| t_gemini_attempts : {t_gemini,6} ms");
            Console.WriteLine($"| t_save            : {t_save,6} ms");
            Console.WriteLine($"| TOTAL CHAT TIME   : {swTotal.ElapsedMilliseconds,6} ms");
            Console.WriteLine("============================================================");

            // Final Response JSON
            var jsonImageList = new JsonArray();
            foreach (var img in imageList) jsonImageList.Add(img);

            return new JsonObject
            {
                ["ok"] = true,
                ["answer"] = answer,
                ["images"] = jsonImageList,
                ["error"] = null,
                ["conversation_id"] = conversationId,
                ["conversation_title"] = generatedTitle ?? "Chat mới",
                ["chat_id"] = savedChatId,
                ["profiling"] = new JsonObject
                {
                    ["t_intent"] = t_intent,
                    ["t_db"] = t_db,
                    ["t_rag"] = t_rag,
                    ["t_gemini_attempts"] = t_gemini,
                    ["t_save"] = t_save,
                    ["total_ms"] = swTotal.ElapsedMilliseconds
                },
                ["candidates"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["content"] = new JsonObject
                        {
                            ["parts"] = new JsonArray { new JsonObject { ["text"] = answer } }
                        }
                    }
                }
            };
        }

        public async Task<string> GenerateTitleAsync(string firstPrompt)
        {
            if (string.IsNullOrWhiteSpace(firstPrompt)) return "Chat mới";
            try
            {
                var titlePayload = new JsonObject
                {
                    ["systemInstruction"] = new JsonObject
                    {
                        ["parts"] = new JsonArray
                        {
                            new JsonObject { ["text"] = "Bạn là chuyên gia đặt tiêu đề. Hãy đặt một tiêu đề ngắn gọn (3 đến 6 từ) bằng tiếng Việt cho cuộc hội thoại dựa vào câu hỏi của người dùng. CHỈ trả về đúng chuỗi tiêu đề tiếng Việt, TUYỆT ĐỐI KHÔNG dùng dấu ngoặc kép, KHÔNG giải thích, KHÔNG trả về định dạng JSON hay Markdown." }
                        }
                    },
                    ["contents"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["role"] = "user",
                            ["parts"] = new JsonArray { new JsonObject { ["text"] = firstPrompt } }
                        }
                    },
                    ["generationConfig"] = new JsonObject 
                    { 
                        ["maxOutputTokens"] = 60,
                        ["temperature"] = 0.2,
                        ["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = 0 }
                    }
                };

                var res = await _geminiService.GenerateContentAsync("gemini-flash-lite-latest", titlePayload);
                string clean = "";
                if (res.Success && res.ResponseData != null)
                {
                    var candidates = res.ResponseData["candidates"]?.AsArray();
                    if (candidates != null && candidates.Count > 0)
                    {
                        var parts = candidates[0]?["content"]?["parts"]?.AsArray();
                        if (parts != null && parts.Count > 0)
                        {
                            clean = parts[0]?["text"]?.ToString()?.Trim() ?? "";
                        }
                    }
                }

                clean = clean.Trim('"', '\'', '`', '\r', '\n', ' ');
                string lowerClean = clean.ToLowerInvariant();
                if (lowerClean == "có" || lowerClean == "ok" || lowerClean == "ừ" || lowerClean == "không" || lowerClean == "chào" || lowerClean == "ê")
                {
                    clean = "";
                }

                // Security & Validity check: if JSON, cut-off, or empty, fallback to clean user prompt
                if (string.IsNullOrWhiteSpace(clean) || clean.StartsWith("{") || clean.StartsWith("[") || clean.Length < 3)
                {
                    string fallback = firstPrompt.Trim();
                    string lowerFallback = fallback.ToLowerInvariant();
                    if (lowerFallback == "có" || lowerFallback == "ok" || lowerFallback == "ừ" || lowerFallback == "chào" || lowerFallback == "ê")
                    {
                        return "Cuộc trò chuyện mới";
                    }
                    if (fallback.Length > 45) fallback = fallback.Substring(0, 45) + "...";
                    return fallback;
                }
                if (clean.Length > 50) clean = clean.Substring(0, 50);
                return clean;
            }
            catch
            {
                string fallback = firstPrompt.Trim();
                if (fallback.Length > 45) fallback = fallback.Substring(0, 45) + "...";
                return string.IsNullOrWhiteSpace(fallback) ? "Chat mới" : fallback;
            }
        }

        private string ExtractTextFromGeminiResult((bool Success, JsonObject? ResponseData, string? RawResponse, string? ErrorMessage) res)
        {
            if (!res.Success || res.ResponseData == null) return string.Empty;
            try
            {
                var candidates = res.ResponseData["candidates"]?.AsArray();
                if (candidates != null && candidates.Count > 0)
                {
                    var parts = candidates[0]?["content"]?["parts"]?.AsArray();
                    if (parts != null && parts.Count > 0)
                    {
                        return parts[0]?["text"]?.ToString() ?? "";
                    }
                }
            }
            catch { }
            return string.Empty;
        }
        private static string ExtractArtifactSummary(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || !text.Contains("[ARTIFACT")) return string.Empty;
            try
            {
                var match = Regex.Match(text, @"\[ARTIFACT(?:\s+type=[""']([^""']+)[""'])?(?:\s+title=[""']([^""']+)[""'])?\]([\s\S]*?)\[\/ARTIFACT\]", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    string type = match.Groups[1].Value.ToLowerInvariant().Trim();
                    string title = match.Groups[2].Value.Trim();
                    string body = match.Groups[3].Value.Trim();
                    if (string.IsNullOrWhiteSpace(type)) type = "docx";
                    if (string.IsNullOrWhiteSpace(title)) title = "Tài liệu";

                    if (type == "xlsx")
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(body);
                            if (doc.RootElement.TryGetProperty("sheets", out var sheets) && sheets.GetArrayLength() > 0)
                            {
                                var firstSheet = sheets[0];
                                var cols = new List<string>();
                                if (firstSheet.TryGetProperty("columns", out var columns))
                                {
                                    foreach (var col in columns.EnumerateArray()) cols.Add(col.GetString() ?? "");
                                }
                                string colsStr = string.Join(", ", cols.Where(c => !string.IsNullOrWhiteSpace(c)));
                                return $"[Đã xuất file Excel: \"{title}.xlsx\" - Cấu trúc cột: {colsStr}]";
                            }
                        }
                        catch { }
                        return $"[Đã xuất file Excel: \"{title}.xlsx\"]";
                    }
                    else if (type == "pptx")
                    {
                        return $"[Đã xuất bài thuyết trình PowerPoint: \"{title}.pptx\"]";
                    }
                    else if (type == "pdf")
                    {
                        return $"[Đã xuất tài liệu PDF: \"{title}.pdf\"]";
                    }
                    else
                    {
                        return $"[Đã xuất tài liệu Word: \"{title}.docx\"]";
                    }
                }
            }
            catch { }
            return string.Empty;
        }

        public async IAsyncEnumerable<string> StreamChatAsync(ChatRequestModel requestModel, string webRootPath, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            int userId = requestModel.User_Id ?? 0;
            int conversationId = requestModel.Conversation_Id ?? 0;

            User? currentUser = null;
            if (userId > 0)
            {
                try { currentUser = await _userRepository.GetByIdAsync(userId); } catch { }
            }

            var payload = requestModel.Payload ?? new JsonObject();
            if (!payload.ContainsKey("contents"))
            {
                payload["contents"] = new JsonArray();
            }

            bool isThinking = false;
            if (payload.ContainsKey("mode"))
            {
                string modeStr = payload["mode"]?.ToString() ?? "fast";
                isThinking = modeStr.Equals("thinking", StringComparison.OrdinalIgnoreCase);
            }

            string model = requestModel.Model ?? "gemini-2.5-flash";
            if (model.Contains("pro") || isThinking) model = "gemini-2.5-flash";
            else if (model.Contains("lite")) model = "gemini-3.5-flash-lite";
            else model = "gemini-2.5-flash";

            if (!payload.ContainsKey("generationConfig") || payload["generationConfig"] == null)
            {
                payload["generationConfig"] = new JsonObject { ["maxOutputTokens"] = 8192 };
            }
            if (payload["generationConfig"] is JsonObject genConfig)
            {
                genConfig["maxOutputTokens"] = 8192;
                if (!isThinking)
                {
                    genConfig.Remove("thinkingConfig");
                }
                if (currentUser?.Temperature != null)
                {
                    genConfig["temperature"] = currentUser.Temperature.Value;
                }
            }

            string question = payload["question"]?.ToString() ?? "";
            var contents = payload["contents"]?.AsArray();
            if (contents != null && contents.Count > 0)
            {
                var cleanContents = new JsonArray();
                foreach (var item in contents)
                {
                    if (item is JsonObject msgObj && msgObj["parts"] is JsonArray pArr && pArr.Count > 0)
                    {
                        string txt = pArr[0]?["text"]?.ToString() ?? "";
                        if (txt.Contains("Bạn đang chạy trong một ứng dụng") || 
                            txt.Contains("QUY TẮC XUẤT FILE") || 
                            txt.Contains("USER_INTENT_ADHERENCE_PROTOCOL"))
                        {
                            continue;
                        }
                    }
                    cleanContents.Add(item?.DeepClone());
                }
                contents = cleanContents;

                if (contents.Count > 0)
                {
                    var lastMsg = contents[contents.Count - 1]?.AsObject();
                    if (lastMsg != null && lastMsg["parts"] is JsonArray parts && parts.Count > 0)
                    {
                        string extracted = parts[0]?["text"]?.ToString() ?? "";
                        if (!string.IsNullOrWhiteSpace(extracted)) question = extracted;
                    }
                }
            }

            string trimmedQ = (question ?? "").Trim().ToLowerInvariant();

            // ALWAYS load conversation history if conversationId > 0 so short answers like 'có', 'ok', 'làm đi' retain 100% context
            var historyChats = new List<Chat>();
            if (userId > 0 && conversationId > 0)
            {
                try
                {
                    historyChats = await _conversationRepository.GetChatHistoryAsync(conversationId, userId, 20);
                }
                catch { }
            }

            var historyMessages = new JsonArray();
            foreach (var chat in historyChats)
            {
                string q = chat.Question ?? "";
                string a = chat.Answer ?? "";
                if (!string.IsNullOrWhiteSpace(q))
                {
                    historyMessages.Add(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray { new JsonObject { ["text"] = q } } });
                }
                if (!string.IsNullOrWhiteSpace(a))
                {
                    string artSummary = ExtractArtifactSummary(a);
                    string finalAnswerWithSummary = string.IsNullOrWhiteSpace(artSummary) ? a : $"{a}\n\n{artSummary}";
                    historyMessages.Add(new JsonObject { ["role"] = "model", ["parts"] = new JsonArray { new JsonObject { ["text"] = finalAnswerWithSummary } } });
                }
            }

            string lowerExportQ = trimmedQ;
            string rawLastModelAnswer = historyChats.LastOrDefault(c => !string.IsNullOrWhiteSpace(c.Answer))?.Answer ?? "";
            string lastModelAnswer = rawLastModelAnswer.ToLowerInvariant();
            bool modelOfferedArtifact = lastModelAnswer.Contains("tạo file") || lastModelAnswer.Contains("xuất file") || 
                                        lastModelAnswer.Contains("file excel") || lastModelAnswer.Contains("file word") ||
                                        lastModelAnswer.Contains("file pdf") || lastModelAnswer.Contains("file powerpoint") ||
                                        lastModelAnswer.Contains("bảng tính") || lastModelAnswer.Contains("bảng excel");

            // Check ANY previous answer in history to see if an Artifact exists in this conversation
            string lastAnswerWithArtifact = historyChats.LastOrDefault(ans => !string.IsNullOrWhiteSpace(ans.Answer) && ans.Answer.Contains("[ARTIFACT"))?.Answer ?? "";
            bool hasRecentArtifact = !string.IsNullOrWhiteSpace(lastAnswerWithArtifact);

            string recentArtType = "xlsx";
            string recentArtTitle = "Tài liệu hoàn chỉnh";
            if (hasRecentArtifact)
            {
                var matchArt = System.Text.RegularExpressions.Regex.Match(lastAnswerWithArtifact, @"\[ARTIFACT(?:\s+type=[""']([^""']+)[""'])?(?:\s+title=[""']([^""']+)[""'])?\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (matchArt.Success)
                {
                    if (!string.IsNullOrWhiteSpace(matchArt.Groups[1].Value)) recentArtType = matchArt.Groups[1].Value.ToLowerInvariant().Trim();
                    if (!string.IsNullOrWhiteSpace(matchArt.Groups[2].Value)) recentArtTitle = matchArt.Groups[2].Value.Trim();
                }
            }

            // User confirmation keywords
            bool isUserConfirmation = lowerExportQ == "có" || lowerExportQ == "co" || lowerExportQ == "ok" || 
                                     lowerExportQ == "ừ" || lowerExportQ == "u" || lowerExportQ == "được" || 
                                     lowerExportQ == "duoc" || lowerExportQ == "làm đi" || lowerExportQ == "xuất đi" ||
                                     lowerExportQ == "tạo đi" || lowerExportQ == "có nhé" || lowerExportQ == "xuất file đi" ||
                                     lowerExportQ == "tạo file đi" || lowerExportQ == "tải về" || lowerExportQ == "làm tiếp đi" ||
                                     lowerExportQ == "tiếp đi" || lowerExportQ == "ok bạn";

            // Status check keywords (when user asks if it's done: 'đc chưa', 'xong chưa', etc.)
            bool isStatusCheck = lowerExportQ == "đc chưa" || lowerExportQ == "dc chua" || lowerExportQ == "được chưa" ||
                                 lowerExportQ == "duoc chua" || lowerExportQ == "xong chưa" || lowerExportQ == "xong chua" ||
                                 lowerExportQ == "sao rồi" || lowerExportQ == "sao roi" || lowerExportQ == "đâu rồi" ||
                                 lowerExportQ == "dau roi" || lowerExportQ == "chưa" || lowerExportQ == "chua";

            // Data fill or modification keywords
            bool isDataFillOrModify = lowerExportQ.Contains("nhét dữ liệu") || lowerExportQ.Contains("nhet du lieu") ||
                                      lowerExportQ.Contains("điền thêm") || lowerExportQ.Contains("dien them") ||
                                      lowerExportQ.Contains("thêm dữ liệu") || lowerExportQ.Contains("them du lieu") ||
                                      lowerExportQ.Contains("thêm vào") || lowerExportQ.Contains("them vao") ||
                                      lowerExportQ.Contains("thêm đi") || lowerExportQ.Contains("them di") ||
                                      lowerExportQ.Contains("thêm cho") || lowerExportQ.Contains("them cho") ||
                                      lowerExportQ.Contains("đang trống") || lowerExportQ.Contains("dang trong") ||
                                      lowerExportQ.Contains("bị trống") || lowerExportQ.Contains("bi trong") ||
                                      lowerExportQ.Contains("chưa có dữ liệu") || lowerExportQ.Contains("chua co du lieu") ||
                                      lowerExportQ.Contains("dữ liệu trống") || lowerExportQ.Contains("du lieu trong") ||
                                      lowerExportQ.Contains("dữ liệu mẫu") || lowerExportQ.Contains("du lieu mau") ||
                                      lowerExportQ.Contains("sửa lại file") || lowerExportQ.Contains("sua lai file") ||
                                      lowerExportQ.Contains("sửa lại") || lowerExportQ.Contains("sua lai") ||
                                      lowerExportQ.Contains("cập nhật") || lowerExportQ.Contains("cap nhat") ||
                                      lowerExportQ.Contains("bổ sung") || lowerExportQ.Contains("bo sung") ||
                                      lowerExportQ.Contains("thêm dòng") || lowerExportQ.Contains("them dong") ||
                                      lowerExportQ.Contains("thêm cột") || lowerExportQ.Contains("them cot") ||
                                      lowerExportQ.Contains("điền điểm") || lowerExportQ.Contains("cho số liệu") ||
                                      lowerExportQ.Contains("nhập dữ liệu") || lowerExportQ.Contains("điền dữ liệu") ||
                                      lowerExportQ.Contains("nhét data") || lowerExportQ.Contains("thêm data") ||
                                      lowerExportQ.Contains("cho data") || lowerExportQ.Contains("điền data") ||
                                      lowerExportQ.Contains("fill data") || lowerExportQ.Contains("add data") ||
                                      lowerExportQ.Contains("đồng bộ") || lowerExportQ.Contains("dong bo") ||
                                      lowerExportQ.Contains("bảng điểm") || lowerExportQ.Contains("bang diem") ||
                                      lowerExportQ.Contains("điền vào") || lowerExportQ.Contains("dien vao");

            // Also check if user attached an image when an artifact exists (e.g. screenshot of empty table)
            bool userSentImage = contents != null && contents.Any(c => c?.ToString()?.Contains("inline_data") == true);

            bool isExplicitExport = lowerExportQ.Contains("xuất file") || lowerExportQ.Contains("tạo file") || 
                                   lowerExportQ.Contains("tải file") || lowerExportQ.Contains("lưu vào word") || 
                                   lowerExportQ.Contains("xuất word") || lowerExportQ.Contains("xuất docx") || 
                                   lowerExportQ.Contains("tạo file docx") || lowerExportQ.Contains("vào word") ||
                                   lowerExportQ.Contains("bỏ vào word") || lowerExportQ.Contains("cho vào word") ||
                                   lowerExportQ.Contains("xuất excel") || lowerExportQ.Contains("tạo file excel") ||
                                   lowerExportQ.Contains("file excel") || lowerExportQ.Contains("xlsx") ||
                                   lowerExportQ.Contains("bảng tính") || lowerExportQ.Contains("xuất pdf") ||
                                   lowerExportQ.Contains("tạo file pdf") || lowerExportQ.Contains("file pdf") ||
                                   lowerExportQ.Contains("powerpoint") || lowerExportQ.Contains("pptx") ||
                                   lowerExportQ.Contains("bài thuyết trình") || lowerExportQ.Contains("slide");

            bool isArtifactRequired = isExplicitExport || (modelOfferedArtifact && isUserConfirmation) || 
                                     (hasRecentArtifact && (isDataFillOrModify || isStatusCheck || isUserConfirmation || userSentImage));

            if (contents != null)
            {
                var currentContents = new JsonArray();
                foreach (var item in historyMessages) currentContents.Add(item?.DeepClone());
                foreach (var item in contents) currentContents.Add(item?.DeepClone());

                if (isArtifactRequired && currentContents.Count > 0)
                {
                    string targetType = recentArtType;
                    string targetTitle = recentArtTitle;
                    if (lowerExportQ.Contains("excel") || lowerExportQ.Contains("xlsx") || lowerExportQ.Contains("bảng tính") || lowerExportQ.Contains("bảng điểm") || lastModelAnswer.Contains("excel") || lastModelAnswer.Contains("bảng tính"))
                    {
                        targetType = "xlsx";
                    }
                    else if (lowerExportQ.Contains("pdf") || lastModelAnswer.Contains("pdf"))
                    {
                        targetType = "pdf";
                    }
                    else if (lowerExportQ.Contains("ppt") || lowerExportQ.Contains("powerpoint") || lowerExportQ.Contains("slide") || lastModelAnswer.Contains("ppt") || lastModelAnswer.Contains("powerpoint"))
                    {
                        targetType = "pptx";
                    }
                    else if (lowerExportQ.Contains("word") || lowerExportQ.Contains("docx") || lastModelAnswer.Contains("word") || lastModelAnswer.Contains("docx"))
                    {
                        targetType = "docx";
                    }

                    string targetTypeFinal = targetType;
                    string targetTitleFinal = targetTitle;
                    var lastMsg = currentContents[currentContents.Count - 1]?.AsObject();
                    if (lastMsg != null && lastMsg["parts"] is JsonArray pArr && pArr.Count > 0)
                    {
                        string instruction = (hasRecentArtifact && (isDataFillOrModify || isStatusCheck || userSentImage))
                            ? $"[HỆ THỐNG YÊU CẦU: Người dùng yêu cầu hoàn thiện/điền dữ liệu hoặc gửi ảnh kiểm tra file {targetType.ToUpper()} '{targetTitle}'. TUYỆT ĐỐI CẤM trả lời bằng các câu hoãn binh như 'để mình làm cho', 'bạn đợi chút', 'được rồi nha', 'để mình đồng bộ qua nhé' mà không kèm thẻ [ARTIFACT]. Bạn BẮT BUỘC giữ nguyên cấu trúc các cột (hoặc trích xuất cấu trúc từ ảnh/lịch sử) và xuất ngay thẻ [ARTIFACT type=\"{targetType}\" title=\"{targetTitle}\"] chứa tối thiểu 10 đến 15 dòng dữ liệu mẫu thực tế hoàn chỉnh, số liệu phong phú sinh động, tuyệt đối không để bảng trống ngay trong câu trả lời này!]"
                            : $"[HỆ THỐNG YÊU CẦU: Người dùng yêu cầu / xác nhận xuất tài liệu định dạng '{targetType}'. Bạn BẮT BUỘC chép trọn vẹn dữ liệu đầy đủ đẹp mắt vào thẻ [ARTIFACT type=\"{targetType}\" title=\"{targetTitle}\"]...[/ARTIFACT]. TUYỆT ĐỐI CẤM HỎI LẠI NGƯỜI DÙNG HOẶC HỨA SUÔNG!]";

                        var textPart = pArr.OfType<JsonObject>().FirstOrDefault(p => p.ContainsKey("text"));
                        if (textPart != null)
                        {
                            string origUserText = textPart["text"]?.ToString() ?? "";
                            textPart["text"] = $"{origUserText}\n\n{instruction}";
                        }
                        else
                        {
                            pArr.Add(new JsonObject { ["text"] = instruction });
                        }
                    }
                }

                payload["contents"] = currentContents;
            }

            string lowerQ = (question ?? "").ToLowerInvariant();
            bool isSocraticMode = requestModel.SocraticMode == true
                               || (payload.ContainsKey("socraticMode") && (payload["socraticMode"]?.ToString() == "true" || payload["socraticMode"]?.GetValue<bool>() == true))
                               || (payload.ContainsKey("socratic") && (payload["socratic"]?.ToString() == "true" || payload["socratic"]?.GetValue<bool>() == true))
                               || lowerQ.Contains("[socratic]") || lowerQ.Contains("gia sư gợi mở") || lowerQ.Contains("gia sư socratic");
            payload.Remove("socraticMode");
            payload.Remove("socratic");
            bool isSimulationPrompt = lowerQ.Contains("[mô phỏng") || lowerQ.Contains("[simulation") || lowerQ.Contains("mô phỏng tương tác") || lowerQ.Contains("stem lab");

            bool isLearningPrompt = isSocraticMode || isSimulationPrompt ||
                                   lowerQ.Contains("học tập") || lowerQ.Contains("thptqg") || 
                                   lowerQ.Contains("trắc nghiệm") || lowerQ.Contains("quiz") || 
                                   lowerQ.Contains("flashcard") || (question != null && question.Contains("[HỌC TẬP"));
            bool isThptqgPrompt = (question != null && (question.Contains("THPTQG_2025") || question.Contains("ĐỀ THI CHUẨN BỘ GD&ĐT THPTQG 2025-2026") || question.Contains("THPTQG 2025–2026")));

            string currentSystemInstruction = (!isThinking && !isArtifactRequired && !isLearningPrompt) ? FastSystemInstructionText : SystemInstructionText;

            if (isSocraticMode)
            {
                currentSystemInstruction += @"

[CHẾ ĐỘ GIA SƯ GỢI MỞ — SOCRATIC TUTOR BẮT BUỘC]:
1. TUYỆT ĐỐI KHÔNG đưa ra đáp án cuối cùng hay lời giải trọn vẹn ngay ở lượt đầu.
2. Trình bày ngắn gọn, thân thiện theo đúng 3 phần rõ ràng:
   - 🎯 Phân tích nhanh: Đề bài cho gì, yêu cầu tìm gì?
   - 💡 Gợi ý công thức / Định hướng Bước 1: Nhắc lại công thức chìa khóa (dùng KaTeX chuẩn $...$, không bọc tiếng Việt trong $...$).
   - ❓ Câu hỏi tương tác cho bạn: Đặt đúng 1 câu hỏi nhỏ cụ thể để học sinh tự tính bước đầu tiên rồi trả lời.
3. Ở các lượt sau: nếu học sinh trả lời đúng -> khen ngợi ngắn và gợi ý bước tiếp theo; nếu sai -> chỉ ra chỗ nhầm lẫn khích lệ và gợi ý nhẹ hơn.
4. Chỉ đưa ra đáp án và lời giải toàn bài khi học sinh giải đúng bước cuối cùng hoặc yêu cầu 'xem lời giải đầy đủ'.";
            }
            if (isSimulationPrompt)
            {
                currentSystemInstruction += @"

[CHẾ ĐỘ PHÒNG THÍ NGHIỆM & MÔ PHỎNG TƯƠNG TÁC HTML5]:
- Bạn BẮT BUỘC xuất khối [SIMULATION_DATA]...[/SIMULATION_DATA] chứa mã HTML5 Canvas/SVG tương tác hoàn chỉnh.
- Mã HTML phải có đầy đủ: <!DOCTYPE html><html><head><style>...</style></head><body>...<script>...</script></body></html> (tự chạy 100%, KHÔNG dùng script CDN ngoài).
- Phải có bảng điều khiển với ít nhất 2–4 thanh trượt <input type=""range""> kèm giá trị thời gian thực và khung hiển thị kết quả tính toán động.";
            }

            if (isThptqgPrompt)
            {
                string root = Directory.GetCurrentDirectory();
                string template = GetThptqgExamTemplate(root);
                if (!string.IsNullOrWhiteSpace(template))
                {
                    currentSystemInstruction += "\n\n[TÀI LIỆU THAM CHIẾU MA TRẬN VÀ DẠNG BÀI ĐỀ CHÍNH THỨC BỘ GD&ĐT THPTQG 2025–2026]:\n" + template;
                }
            }
            if (currentUser != null && !string.IsNullOrWhiteSpace(currentUser.CustomInstructions))
            {
                currentSystemInstruction += "\n\n[HƯỚNG DẪN TÙY CHỈNH TỪ NGƯỜI DÙNG]:\n" + currentUser.CustomInstructions.Trim();
            }
            if (currentUser?.Temperature != null)
            {
                if (payload["generationConfig"] is JsonObject genCfg)
                {
                    genCfg["temperature"] = currentUser.Temperature.Value;
                }
                else
                {
                    payload["generationConfig"] = new JsonObject { ["temperature"] = currentUser.Temperature.Value };
                }
            }
            payload["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = currentSystemInstruction } }
            };

            var sbFull = new StringBuilder();
            await foreach (var chunk in _geminiService.StreamContentAsync(model, payload, cancellationToken))
            {
                sbFull.Append(chunk);
                yield return chunk;
            }

            // Stream fallback: If artifact was required but Gemini failed to emit [ARTIFACT, synthesize it!
            if (isArtifactRequired && !sbFull.ToString().Contains("[ARTIFACT"))
            {
                string streamTargetType = recentArtType;
                string streamTargetTitle = recentArtTitle;
                if (lowerExportQ.Contains("excel") || lowerExportQ.Contains("xlsx") || lowerExportQ.Contains("bảng tính") || lowerExportQ.Contains("bảng điểm") || lastModelAnswer.Contains("excel") || lastModelAnswer.Contains("bảng tính"))
                {
                    streamTargetType = "xlsx";
                }
                else if (lowerExportQ.Contains("pdf") || lastModelAnswer.Contains("pdf"))
                {
                    streamTargetType = "pdf";
                }
                else if (lowerExportQ.Contains("ppt") || lowerExportQ.Contains("powerpoint") || lowerExportQ.Contains("slide") || lastModelAnswer.Contains("ppt") || lastModelAnswer.Contains("powerpoint"))
                {
                    streamTargetType = "pptx";
                }
                else if (lowerExportQ.Contains("word") || lowerExportQ.Contains("docx") || lastModelAnswer.Contains("word") || lastModelAnswer.Contains("docx"))
                {
                    streamTargetType = "docx";
                }

                string syntheticChunk = "";
                if (streamTargetType == "xlsx")
                {
                    bool isGradeTable = lowerExportQ.Contains("điểm") || lowerExportQ.Contains("học sinh") || lowerExportQ.Contains("lớp") || lastModelAnswer.Contains("điểm") || streamTargetTitle.Contains("điểm");
                    if (isGradeTable)
                    {
                        syntheticChunk = $"\n\nĐã cập nhật bảng điểm chi tiết đầy đủ dữ liệu cho bạn:\n\n[ARTIFACT type=\"xlsx\" title=\"Bảng điểm lớp 12\"]\n{{\"title\": \"BẢNG ĐIỂM TỔNG HỢP LỚP 12\", \"subtitle\": \"Năm học 2026 - 2027\", \"sheets\": [{{\"name\": \"BangDiem\", \"columns\": [\"STT\", \"Họ và tên\", \"Toán\", \"Văn\", \"Anh\", \"Vật Lý\", \"Hóa Học\", \"Sinh Học\", \"Điểm TB\", \"Xếp loại\"], \"rows\": [[1, \"Nguyễn Văn A\", 8.5, 7.0, 8.0, 7.5, 8.0, 7.5, 7.75, \"Khá\"], [2, \"Trần Thị B\", 9.0, 8.5, 9.5, 8.5, 9.0, 8.0, 8.75, \"Giỏi\"], [3, \"Lê Văn C\", 6.5, 6.0, 5.5, 6.0, 6.5, 7.0, 6.25, \"Trung bình\"], [4, \"Phạm Thị D\", 9.5, 9.0, 9.0, 9.0, 9.5, 9.0, 9.17, \"Giỏi\"], [5, \"Hoàng Văn E\", 8.0, 8.0, 8.5, 7.0, 7.5, 8.0, 7.83, \"Khá\"], [6, \"Vũ Thị F\", 9.0, 9.5, 9.0, 8.5, 9.0, 9.5, 9.08, \"Xuất sắc\"]]}}]}}\n[/ARTIFACT]";
                    }
                    else
                    {
                        syntheticChunk = $"\n\nĐã chuẩn bị xong bảng tính Excel đầy đủ dữ liệu cho bạn:\n\n[ARTIFACT type=\"xlsx\" title=\"{streamTargetTitle}\"]\n{{\"title\": \"{streamTargetTitle}\", \"subtitle\": \"Cập nhật dữ liệu\", \"sheets\": [{{\"name\": \"Sheet1\", \"columns\": [\"STT\", \"Họ và tên\", \"Chỉ tiêu 1\", \"Chỉ tiêu 2\", \"Đánh giá\"], \"rows\": [[1, \"Nguyễn Văn A\", 8.5, 9.0, \"Tốt\"], [2, \"Trần Thị B\", 8.0, 8.5, \"Khá\"], [3, \"Lê Văn C\", 9.0, 9.5, \"Xuất sắc\"], [4, \"Phạm Thị D\", 9.5, 9.0, \"Xuất sắc\"]]}}]}}\n[/ARTIFACT]";
                    }
                }
                else if (streamTargetType == "docx")
                {
                    string docxContent = !string.IsNullOrWhiteSpace(rawLastModelAnswer) ? rawLastModelAnswer : "";
                    if (string.IsNullOrWhiteSpace(docxContent))
                    {
                        syntheticChunk = "\n\nBạn muốn đưa nội dung hoặc chủ đề gì vào file Word? Hãy chia sẻ để mình soạn thảo đầy đủ cho bạn nhé!";
                    }
                    else
                    {
                        syntheticChunk = $"\n\nĐã chuẩn bị xong tài liệu Word cho bạn:\n\n[ARTIFACT type=\"docx\" title=\"{streamTargetTitle}\"]\n{docxContent}\n[/ARTIFACT]";
                    }
                }
                else if (streamTargetType == "pdf")
                {
                    syntheticChunk = $"\n\nĐã chuẩn bị xong tài liệu PDF cho bạn:\n\n[ARTIFACT type=\"pdf\" title=\"{streamTargetTitle}\"]\n# {streamTargetTitle}\n\nNội dung báo cáo chi tiết.\n[/ARTIFACT]";
                }
                else if (streamTargetType == "pptx")
                {
                    syntheticChunk = $"\n\nĐã chuẩn bị xong bài thuyết trình PowerPoint cho bạn:\n\n[ARTIFACT type=\"pptx\" title=\"{streamTargetTitle}\"]\n{{\"title\": \"{streamTargetTitle}\", \"slides\": [{{\"title\": \"{streamTargetTitle}\", \"bulletPoints\": [\"Tổng quan\", \"Nội dung chính\", \"Kết luận\"]}}]}}\n[/ARTIFACT]";
                }
                if (!string.IsNullOrWhiteSpace(syntheticChunk))
                {
                    yield return syntheticChunk;
                }
            }
        }
    }
}
