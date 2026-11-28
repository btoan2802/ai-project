
function dataURLToBlob(dataurl) {
    if (!dataurl) return new Blob([], { type: 'image/jpeg' });
    const arr = dataurl.split(',');
    const mimeMatch = arr[0].match(/:(.*?);/);
    const mime = mimeMatch ? mimeMatch[1] : 'image/jpeg';
    const bstr = atob(arr.length > 1 ? arr[1] : arr[0]);
    let n = bstr.length;
    const u8arr = new Uint8Array(n);
    while (n--) {
        u8arr[n] = bstr.charCodeAt(n);
    }
    return new Blob([u8arr], { type: mime });
}
// ====== cấu hình ======
const WORKER_URL = "/api/chat";

// 📱 MOBILE VIEWPORT HEIGHT FIX
function setAppHeight() {
    const doc = document.documentElement;
    const height = window.visualViewport ? window.visualViewport.height : window.innerHeight;
    doc.style.setProperty('--app-height', `${height}px`);
}
window.addEventListener('resize', setAppHeight);
if (window.visualViewport) window.visualViewport.addEventListener('resize', setAppHeight);
setAppHeight();

const MODEL = "gemini-2.5-flash";
const TEMPERATURE = 0.5;
const MAX_OUTPUT_TOKENS = 20000;
const REQUEST_TIMEOUT_MS = 600000;

// ====== MATHJAX & MARKDOWN HELPER (PREVENT INLINE MATH FROM WRAPPING) ======
function renderMarkdownWithMath(text) {
    if (!text) return "";

    let processed = text;

    // 1. TÁCH KHỐI [QUIZ_DATA] TRƯỚC TIÊN ĐỂ TRÁNH MARKDOWN ĂN MẤT DẤU \ CỦA LATEX
    let isStreamingQuiz = false;
    processed = processed.replace(/\[QUIZ_DATA\]([\s\S]*?)\[\/QUIZ_DATA\]/gi, (match, jsonStr) => {
        return `\n\n@@QUIZ_BLOCK_${registerQuizData(jsonStr)}@@\n\n`;
    });
    if (/\[QUIZ_DATA\](?![\s\S]*?\[\/QUIZ_DATA\])/i.test(processed)) {
        isStreamingQuiz = true;
        processed = processed.replace(/\[QUIZ_DATA\][\s\S]*$/i, "\n\n@@QUIZ_STREAMING_PLACEHOLDER@@\n\n");
    }

    // 2. TÁCH KHỐI [FLASHCARD_DATA] TRƯỚC MARKDOWN
    let isStreamingFlashcard = false;
    processed = processed.replace(/\[FLASHCARD_DATA\]([\s\S]*?)\[\/FLASHCARD_DATA\]/gi, (match, jsonStr) => {
        return `\n\n@@FLASHCARD_BLOCK_${registerFlashcardData(jsonStr)}@@\n\n`;
    });
    if (/\[FLASHCARD_DATA\](?![\s\S]*?\[\/FLASHCARD_DATA\])/i.test(processed)) {
        isStreamingFlashcard = true;
        processed = processed.replace(/\[FLASHCARD_DATA\][\s\S]*$/i, "\n\n@@FLASHCARD_STREAMING_PLACEHOLDER@@\n\n");
    }

    // 3. TÁCH KHỐI [ARTIFACT type="docx"]
    let isStreamingDocx = false;
    if (/\[ARTIFACT\s+type=["']docx["'][^\]]*\](?![\s\S]*?\[\/ARTIFACT\])/i.test(processed)) {
        isStreamingDocx = true;
        processed = processed.replace(/\[ARTIFACT\s+type=["']docx["'][^\]]*\][\s\S]*$/i, "\n\n@@DOCX_STREAMING_PLACEHOLDER@@\n\n");
    }

    // 4. Protect block/display math ($$...$$ and \[...\])
    const mathTokens = [];
    processed = processed.replace(/(\$\$[\s\S]*?\$\$|\\\[[\s\S]*?\\\])/g, (match) => {
        const token = `@@MATH_BLOCK_${mathTokens.length}@@`;
        mathTokens.push({ token, val: match });
        return token;
    });

    // 5. Protect inline math ($...$ and \(...\))
    processed = processed.replace(/(\$(?!\$)(\S(?:[\s\S]*?\S)?)\$|\\\([\s\S]*?\\\))/g, (match) => {
        const token = `@@MATH_INLINE_${mathTokens.length}@@`;
        mathTokens.push({ token, val: match });
        return token;
    });

    // 6. Parse Markdown
    let html = "";
    try {
        const escaped = typeof escapeUnsafeTags === "function" ? escapeUnsafeTags(processed) : processed;
        html = marked.parse(escaped).replace(/language-j /g, "language-java");
    } catch (e) {
        html = processed;
    }

    if (isStreamingDocx) {
        html = html.replace("@@DOCX_STREAMING_PLACEHOLDER@@", `
            <div class="docx-artifact-streaming rounded-3 p-3 my-2 d-flex align-items-center gap-2 border border-primary-subtle bg-body-tertiary">
                <div class="spinner-border spinner-border-sm text-primary" role="status"></div>
                <span class="text-primary small fw-semibold">Đang tạo tệp Word...</span>
            </div>
        `);
    }

    if (isStreamingQuiz) {
        html = html.replace("@@QUIZ_STREAMING_PLACEHOLDER@@", `
            <div class="quiz-streaming-card rounded-3 p-3 my-2 d-flex align-items-center gap-2 border border-success-subtle bg-body-tertiary">
                <div class="spinner-border spinner-border-sm text-success" role="status"></div>
                <span class="text-success small fw-semibold">Đang thiết lập đề thi trắc nghiệm tương tác & chấm điểm...</span>
            </div>
        `);
    }

    if (isStreamingFlashcard) {
        html = html.replace("@@FLASHCARD_STREAMING_PLACEHOLDER@@", `
            <div class="flashcard-streaming-card rounded-3 p-3 my-2 d-flex align-items-center gap-2 border border-warning-subtle bg-body-tertiary">
                <div class="spinner-border spinner-border-sm text-warning" role="status"></div>
                <span class="text-warning small fw-semibold">Đang tạo bộ Flashcards 3D thông minh...</span>
            </div>
        `);
    }

    // Restore interactive Quiz & Flashcard placeholders
    if (window.quizBlocks) {
        Object.keys(window.quizBlocks).forEach(qid => {
            html = html.replace(`@@QUIZ_BLOCK_${qid}@@`, window.quizBlocks[qid]);
        });
    }
    if (window.flashcardBlocks) {
        Object.keys(window.flashcardBlocks).forEach(fcid => {
            html = html.replace(`@@FLASHCARD_BLOCK_${fcid}@@`, window.flashcardBlocks[fcid]);
        });
    }

    // Restore math tokens
    mathTokens.forEach(({ token, val }) => {
        html = html.replace(token, val);
    });

    return html;
}

// ====== GLOBAL SIDEBAR TITLE SYNC ======
function syncSidebarChatTitle(arg1, arg2) {
    try {
        let newTitle = arg1;
        let cId = arg2;
        if (typeof arg1 === 'number' || (typeof arg1 === 'string' && /^\d+$/.test(arg1) && typeof arg2 === 'string' && !/^\d+$/.test(arg2))) {
            cId = arg1;
            newTitle = arg2;
        }
        if (!newTitle || newTitle === "Chat mới" || (typeof newTitle === 'string' && newTitle.startsWith("{"))) return;
        const cleanTitle = String(newTitle).replace(/[\r\n]+/g, ' ').trim();
        if (!cleanTitle) return;

        document.title = `${cleanTitle} - Btoan AI`;

        const targetId = cId || conversation_id;
        let found = false;
        if (targetId) {
            document.querySelectorAll(`.chat-history-item[data-id="${targetId}"] .chat-title, .history-item[data-id="${targetId}"] .history-title`).forEach(el => {
                el.innerText = cleanTitle;
                found = true;
            });

            // Call backend API to persist rename
            fetch("/ai/api/rename_conversation.php", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ id: targetId, title: cleanTitle })
            }).catch(() => { });
        }
        if (!found) {
            const activeTitleEl = document.querySelector('.chat-history-item.active .chat-title') ||
                document.querySelector('.chat-history-item:first-child .chat-title');
            if (activeTitleEl) {
                activeTitleEl.innerText = cleanTitle;
            }
        }
    } catch (e) {
        console.warn("syncSidebarChatTitle error:", e);
    }
}
window.syncSidebarChatTitle = syncSidebarChatTitle;

function typesetMathInElement(elem) {
    if (!elem) return;
    if (elem !== document && elem !== document.body && !elem.isConnected) {
        requestAnimationFrame(() => {
            if (elem.isConnected && window.MathJax) {
                if (typeof MathJax.typesetPromise === "function") {
                    MathJax.typesetPromise([elem]).catch(() => { });
                } else if (typeof MathJax.typeset === "function") {
                    try { MathJax.typeset([elem]); } catch (e) { }
                }
            }
        });
        return;
    }
    if (window.MathJax) {
        if (typeof MathJax.typesetPromise === "function") {
            MathJax.typesetPromise([elem]).catch(err => console.debug("MathJax typesetPromise:", err));
        } else if (typeof MathJax.typeset === "function") {
            try { MathJax.typeset([elem]); } catch (e) { }
        }
    }
}

// ====== elements ======
let autoRenamed = false
let conversation_id = null;
let chatEl;
let statusEl;
let msgEl;
let sendBtn;
let attachments;
let lastAIAnswer = ""
let mode = "auto"
let lastExportContent = ""
let isSending = false

function setMode(m) {
    mode = m;
    const status = document.getElementById("modeStatus");
    if (status) {
        if (m === "thinking") {
            status.innerHTML = `<span class="fw-semibold">🧠 Btoan 2.5 Pro</span>`;
            status.className = "badge ms-1 d-inline-flex align-items-center";
            status.style.backgroundColor = "#4f46e5";
            status.style.color = "white";
            status.style.padding = "4px 10px";
            status.style.borderRadius = "20px";
        } else {
            status.innerHTML = `<span class="fw-semibold">⚡ Btoan 2.5 Flash</span>`;
            status.className = "badge bg-light text-dark ms-1 border d-inline-flex align-items-center";
            status.style.backgroundColor = "";
            status.style.padding = "4px 10px";
            status.style.borderRadius = "20px";
        }
    }
}

const systemInstruction = `
Bạn đang chạy trong một ứng dụng CÓ THỂ tạo và xuất file thật cho người dùng.

QUY TẮC XUẤT FILE:
- Nếu người dùng muốn xuất file, tải file, file Word, DOCX, PDF, TXT, văn bản để tải về, hoặc yêu cầu tương đương:
  1. Hãy tạo RA NỘI DUNG CUỐI CÙNG để đưa vào file
  2. KHÔNG viết lời mở đầu, không giải thích, không nói "Tôi sẽ xuất file", không nói "Đây là nội dung", không thêm nhận xét
  3. Chỉ trả đúng phần nội dung cần nằm trong file
  4. Ở DÒNG CUỐI CÙNG thêm đúng định dạng:
     [EXPORT] docx
     hoặc
     [EXPORT] pdf
     hoặc
     [EXPORT] txt

- Nếu yêu cầu là xuất file Word thì luôn dùng:
  [EXPORT] docx

- KHÔNG được trả lời rằng bạn không thể tạo file.
- KHÔNG được bảo người dùng copy thủ công sang Word.

- QUY TẮC PHƯƠNG TIỆN (QUAN TRỌNG):
  1. TẠO HÌNH ẢNH TĨNH: Nếu người dùng yêu cầu "vẽ ảnh", "tạo ảnh", "bức hình"... (không có chuyển động)
     TUYỆT ĐỐI KHÔNG dịch word-by-word đơn giản (ví dụ: cấm dùng 'a dog eating banana').
     BẮT BUỘC phải chuyển thành một prompt tiếng Anh cực kỳ chi tiết, tả rõ hành động tương tác vật lý (ví dụ: gặm chuối thì phải có 'actively biting and chewing a half-peeled ripe yellow banana held firmly between front paws, visible banana texture'), bối cảnh, góc chụp và chất lượng ảnh (photorealistic, natural lighting, highly detailed fur and textures, sharp focus, 8k resolution, cinematic composition) để máy vẽ chính xác. Tuyệt đối không dùng từ chung chung.
     Trả về ở dòng cuối cùng: [GENIMAGE] <mô tả chi tiết bằng tiếng Anh theo cấu trúc trên>
     Ví dụ: [GENIMAGE] A photorealistic close-up of a cute golden retriever puppy happily biting and chewing a half-peeled ripe yellow banana held between its front paws, warm natural sunlight streaming from a window, highly detailed fur textures, shallow depth of field, 8k resolution, cinematic lighting.
  2. CHỈNH SỬA ẢNH: Nếu có ảnh đính kèm và yêu cầu thay đổi.
     Trả về: [EDITIMAGE] <mô tả tiếng Anh>
  3. TẠO VIDEO (CHUYỂN ĐỘNG): Nếu người dùng yêu cầu "tạo video", "làm clip", "vẽ video", "animate", "phim"...
     TUYỆT ĐỐI KHÔNG DÙNG [GENIMAGE]. 
     BẮT BUỘC TRẢ VỀ: [GENVIDEO] <mô tả chi tiết bằng tiếng Anh về chuyển động và bối cảnh>
     Ví dụ: [GENVIDEO] A cat flying through the clouds, fur moving in the wind, cinematic.

- NẾU YÊU CẦU LÀ VIDEO, KHÔNG ĐƯỢC TRẢ VỀ ẢNH TĨNH.

Khi viết công thức toán:
- dùng $ ... $
- tránh xuống dòng không cần thiết.

Nếu câu hỏi cần thông tin mới, thông tin thời gian thực, hoặc bạn không chắc chắn:
Hãy trả về theo format:
[SEARCH] <từ khóa tìm kiếm>

KHÔNG trả lời ngay.
`;
// ====== state ======
let chatHistoryBuffer = [];
let lastUserMessage = "";

function escapeHtml(str = "") {
    return str
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;")
        .replace(/'/g, "&#039;")
}

function detectThinking(userText) {

    const keywords = [
        "tại sao",
        "giải thích",
        "phân tích",
        "so sánh",
        "how",
        "why",
        "analyze",
        "explain"
    ]

    for (const k of keywords) {
        if (userText.toLowerCase().includes(k)) {
            return true
        }
    }

    return false
}

function userWantsExport(text = "") {
    const t = String(text || "").trim().toLowerCase();

    const explicitKeywords = [
        "xuất file", "tải file", "file word", "bỏ vào word", "cho vào word", "vào word",
        "bỏ vào excel", "cho vào excel", "vào excel", "bỏ vào ppt", "bỏ vào powerpoint",
        "cho vào ppt", "vào ppt", "docx", "pdf", "xlsx", "pptx", "txt", "lưu file",
        "tạo file", "download file", "xuất word", "xuất docx", "tạo tài liệu", "xuất tài liệu",
        "tạo file excel", "xuất excel", "tạo file word", "tạo file pdf"
    ];

    if (explicitKeywords.some(k => t.includes(k))) return true;

    // Check if user confirmed after bot offered file creation
    const isConfirm = t === "có" || t === "co" || t === "ok" || t === "ừ" || t === "u" ||
        t === "được" || t === "duoc" || t === "làm đi" || t === "xuất đi" ||
        t === "tạo đi" || t === "có nhé" || t === "xuất file đi" || t === "tạo file đi";

    const lastBot = String(lastAIAnswer || "").toLowerCase();
    const botOffered = lastBot.includes("tạo file") || lastBot.includes("xuất file") ||
        lastBot.includes("file excel") || lastBot.includes("file word") ||
        lastBot.includes("bảng tính") || lastBot.includes("file pdf");

    return isConfirm && botOffered;
}

function detectExportType(text = "") {
    const t = String(text || "").toLowerCase();
    const lastBot = String(lastAIAnswer || "").toLowerCase();

    if (t.includes("excel") || t.includes("xlsx") || t.includes("bảng tính") || lastBot.includes("excel") || lastBot.includes("bảng tính")) return "xlsx";
    if (t.includes("powerpoint") || t.includes("pptx") || t.includes("slide") || t.includes("thuyết trình") || lastBot.includes("powerpoint") || lastBot.includes("ppt")) return "pptx";
    if (t.includes("pdf") || lastBot.includes("pdf")) return "pdf";
    return "docx";
}

function getFallbackExportContent(currentReply) {
    const cleaned = cleanBotText(currentReply);
    if (isValidForExport(cleaned)) return cleaned;

    if (isValidForExport(lastExportContent)) return lastExportContent;

    if (Array.isArray(chatHistoryBuffer) && chatHistoryBuffer.length > 0) {
        for (let idx = chatHistoryBuffer.length - 1; idx >= 0; idx--) {
            const item = chatHistoryBuffer[idx];
            if (item && item.role === "model" && Array.isArray(item.parts) && item.parts.length > 0) {
                const t = cleanBotText(item.parts[0]?.text || "");
                if (isValidForExport(t)) {
                    return t;
                }
            }
        }
    }

    if (isValidForExport(lastAIAnswer)) return lastAIAnswer;
    return "";
}

document.addEventListener("touchend", (e) => {
    if (window.innerWidth < 768 && msgEl) {

        // ✅ nếu đang select text → KHÔNG blur
        const selection = window.getSelection()?.toString();
        if (selection && selection.length > 0) return;

        // ✅ nếu click trong input → bỏ
        if (msgEl.contains(e.target)) return;

        // ✅ nếu click các vùng tương tác → bỏ
        if (e.target.closest("button")) return;
        if (e.target.closest(".copy-btn")) return;
        if (e.target.closest(".copy-btn-clean")) return;
        if (e.target.closest("pre")) return;
        if (e.target.closest("code")) return;

        // ✅ delay để không phá clipboard
        setTimeout(() => {
            msgEl.blur();
        }, 120);
    }
});
document.addEventListener("touchstart", (e) => {
    // nếu đang giữ để copy → đừng làm gì
    if (window.getSelection()?.toString().length > 0) {
        e.stopPropagation();
    }
}, true);


function addImages(files) {

    const imgInput = document.getElementById("fileImg")
    const dt = new DataTransfer()

    // giữ file cũ (chỉ add nếu files không phải từ chính input đó)
    if (files !== imgInput.files && files !== document.getElementById("fileCam")?.files) {
        Array.from(imgInput.files).forEach(f => dt.items.add(f))
    }

    Array.from(files).forEach(file => {

        if (!file.type.startsWith("image")) return

        dt.items.add(file)

        const reader = new FileReader()

        reader.onload = function (e) {

            const div = document.createElement("div")
            div.className = "atimg"

            div.innerHTML = `
        <img src="${e.target.result}">
        <button class="atremove">×</button>
      `

            div.querySelector(".atremove").onclick = () => {
                div.remove()
            }

            attachments.appendChild(div)
        }

        reader.readAsDataURL(file)

    })

    imgInput.files = dt.files
}


function addUserBubble(text, images = []) {
    const row = document.createElement("div")
    row.className = "msgrow me"

    const bubble = document.createElement("div")
    bubble.className = "bubble position-relative"

    let html = `<div class="usertxt">${escapeHtml(text).replace(/\n/g, "<br>")}</div>`

    if (images && images.length > 0) {
        html += `<div class="mt-2 d-flex flex-wrap gap-2">`

        images.forEach(img => {
            html += `
                <img src="${img}" 
                     style="max-width:150px;border-radius:10px;">
            `
        })

        html += `</div>`
    }

    html += `
        <div class="assistant-corner-actions">
            <button class="corner-action-btn copy-btn-msg" title="Copy tin nhắn">
                <i class="bi bi-clipboard"></i>
            </button>
        </div>
    `

    bubble.innerHTML = html

    const copyBtn = bubble.querySelector(".copy-btn-msg")
    if (copyBtn) {
        copyBtn.onclick = async () => {
            await navigator.clipboard.writeText(text || "")
            copyBtn.innerHTML = `<i class="bi bi-check2"></i>`
            setTimeout(() => {
                copyBtn.innerHTML = `<i class="bi bi-clipboard"></i>`
            }, 1200)
        }
    }

    row.appendChild(bubble)
    chatEl.appendChild(row)
}

function createMessageActions({ text, exportType = null, onExport = null }) {
    const actions = document.createElement("div");
    actions.className = "ai-msg-actions";

    // 1. Copy button
    const copyBtn = document.createElement("button");
    copyBtn.className = "btn-ai-action";
    copyBtn.title = "Sao chép câu trả lời";
    copyBtn.innerHTML = `<i class="bi bi-clipboard"></i>`;
    copyBtn.onclick = async () => {
        const cleanText = typeof cleanBotText === "function" ? cleanBotText(text) : text;
        await navigator.clipboard.writeText(cleanText || "");
        copyBtn.innerHTML = `<i class="bi bi-check2 text-success"></i>`;
        setTimeout(() => {
            copyBtn.innerHTML = `<i class="bi bi-clipboard"></i>`;
        }, 2000);
    };
    actions.appendChild(copyBtn);

    // 2. Regenerate button
    const regenBtn = document.createElement("button");
    regenBtn.className = "btn-ai-action";
    regenBtn.title = "Tạo lại câu trả lời này";
    regenBtn.innerHTML = `<i class="bi bi-arrow-clockwise"></i>`;
    regenBtn.onclick = () => {
        if (typeof regenerateLastAnswer === "function") {
            regenerateLastAnswer();
        }
    };
    actions.appendChild(regenBtn);

    // 3. Text-to-Speech button
    const speakBtn = document.createElement("button");
    speakBtn.className = "btn-ai-action";
    speakBtn.title = "Nghe đọc nội dung (Text-to-Speech)";
    speakBtn.innerHTML = `<i class="bi bi-volume-up"></i>`;
    speakBtn.onclick = () => {
        speakBotText(text, speakBtn);
    };
    actions.appendChild(speakBtn);

    if (exportType) {
        const exportBtn = document.createElement("button");
        exportBtn.className = "btn-ai-action";
        exportBtn.title = `Tải ${exportType.toUpperCase()}`;
        exportBtn.innerHTML = `<i class="bi bi-download"></i>`;
        exportBtn.onclick = () => {
            if (typeof onExport === "function") return onExport();
            if (exportType === "docx") exportDOCX(text);
            else exportFile(text);
        };
        actions.appendChild(exportBtn);
    }

    return actions;
}

// Web Speech API: Text-to-Speech
function speakBotText(text, btn) {
    if (!('speechSynthesis' in window)) {
        alert("Trình duyệt của bạn không hỗ trợ Text-to-Speech.");
        return;
    }

    if (window.speechSynthesis.speaking) {
        window.speechSynthesis.cancel();
        if (btn) btn.innerHTML = `<i class="bi bi-volume-up"></i> <span>Đọc</span>`;
        return;
    }

    const clean = typeof cleanBotText === "function" ? cleanBotText(text) : text;
    if (!clean) return;

    const utterance = new SpeechSynthesisUtterance(clean);
    utterance.lang = "vi-VN";
    utterance.rate = 1.0;

    const voices = window.speechSynthesis.getVoices();
    const viVoice = voices.find(v => v.lang.includes("vi") || v.lang.includes("VN"));
    if (viVoice) utterance.voice = viVoice;

    utterance.onstart = () => {
        if (btn) btn.innerHTML = `<i class="bi bi-stop-fill text-danger"></i> <span class="text-danger">Dừng</span>`;
    };
    utterance.onend = utterance.onerror = () => {
        if (btn) btn.innerHTML = `<i class="bi bi-volume-up"></i> <span>Đọc</span>`;
    };

    window.speechSynthesis.speak(utterance);
}

// Regenerate last AI response
function regenerateLastAnswer() {
    if (!lastUserMessage) return;
    if (msgEl) {
        msgEl.value = lastUserMessage;
        send();
    }
}

// Web Speech API: Voice Input
let voiceRecognition = null;
let isVoiceRecording = false;

function toggleVoiceInput() {
    const SpeechRecognition = window.SpeechRecognition || window.webkitSpeechRecognition;
    if (!SpeechRecognition) {
        alert("Trình duyệt không hỗ trợ Web Speech API nhận diện giọng nói. Hãy dùng Google Chrome hoặc Microsoft Edge.");
        return;
    }

    const micBtn = document.getElementById("micBtn");
    const micIcon = document.getElementById("micIcon");

    if (isVoiceRecording && voiceRecognition) {
        voiceRecognition.stop();
        return;
    }

    voiceRecognition = new SpeechRecognition();
    voiceRecognition.lang = "vi-VN";
    voiceRecognition.continuous = false;
    voiceRecognition.interimResults = true;

    voiceRecognition.onstart = () => {
        isVoiceRecording = true;
        if (micBtn) micBtn.classList.add("recording");
        if (micIcon) micIcon.className = "bi bi-mic-fill text-danger";
        if (msgEl) msgEl.placeholder = "Đang lắng nghe bạn nói...";
    };

    voiceRecognition.onresult = (event) => {
        let transcript = "";
        for (let i = event.resultIndex; i < event.results.length; i++) {
            transcript += event.results[i][0].transcript;
        }
        if (msgEl && transcript) {
            msgEl.value = transcript;
            msgEl.style.height = "auto";
            msgEl.style.height = Math.min(msgEl.scrollHeight, 160) + "px";
        }
    };

    voiceRecognition.onerror = (event) => {
        console.warn("Speech recognition error:", event.error);
        stopVoiceRecording();
    };

    voiceRecognition.onend = () => {
        stopVoiceRecording();
    };

    try {
        voiceRecognition.start();
    } catch (e) {
        console.warn("Could not start speech recognition:", e);
        stopVoiceRecording();
    }
}

function stopVoiceRecording() {
    isVoiceRecording = false;
    const micBtn = document.getElementById("micBtn");
    const micIcon = document.getElementById("micIcon");
    if (micBtn) micBtn.classList.remove("recording");
    if (micIcon) micIcon.className = "bi bi-mic fs-5";
    if (msgEl) msgEl.placeholder = "Hỏi Btoan AI bất kỳ điều gì...";
}


function cleanBotText(text) {
    if (!text) return "";
    return text
        .replace(/\[ARTIFACT(?:\s+[^\]]*)?\][\s\S]*?\[\/ARTIFACT\]/gi, "")
        .replace(/\[GENIMAGE\].*?(\n|$)/gi, "")
        .replace(/\[GENVIDEO\].*?(\n|$)/gi, "")
        .replace(/\[GENMUSIC\].*?(\n|$)/gi, "")
        .replace(/\[EDITIMAGE\].*?(\n|$)/gi, "")
        .replace(/\[SEARCH\].*?(\n|$)/gi, "")
        .replace(/\[EXPORT\].*?(\n|$)/gi, "")
        .trim();
}

function addBotBubble(text, exportType = null) {
    if (!chatEl) return;

    const artifacts = typeof extractArtifacts === "function" ? extractArtifacts(text) : [];
    if (artifacts.length === 0 && (exportType || (typeof userWantsExport === "function" && (userWantsExport(lastUserMessage) || userWantsExport(text))))) {
        const fallbackText = typeof getFallbackExportContent === "function" ? getFallbackExportContent(text) : (lastExportContent || lastAIAnswer || cleanBotText(text));
        if (typeof isValidForExport === "function" && isValidForExport(fallbackText)) {
            artifacts.push({
                type: exportType || (typeof detectExportType === "function" ? detectExportType(lastUserMessage || text) : "docx"),
                title: "Tài liệu hoàn chỉnh",
                content: fallbackText
            });
        }
    }

    const cleanedText = cleanBotText(text);
    if (!cleanedText.trim() && !exportType && artifacts.length === 0) return; // 🔥 KHÔNG TẠO Ô CHAT NẾU CHỈ CÓ MEDIA

    const row = document.createElement("div");
    row.className = "msgrow bot";

    const bubble = document.createElement("div");
    bubble.className = "bubble position-relative";

    const content = document.createElement("div");
    content.className = "message-content";
    content.innerHTML = renderMarkdownWithMath(cleanedText || (artifacts.length > 0 ? "Dưới đây là tài liệu bạn yêu cầu:" : ""));
    bubble.appendChild(content);

    if (artifacts.length > 0 && typeof renderArtifactCards === "function") {
        renderArtifactCards(bubble, artifacts);
    } else {
        bubble.appendChild(createMessageActions({ text: cleanedText || text, exportType }));
    }

    row.appendChild(bubble);
    chatEl.appendChild(row);

    typesetMathInElement(content);

    /* highlight code */
    bubble.querySelectorAll("pre code").forEach(el => {
        hljs.highlightElement(el)
    })

    /* copy từng block code */
    bubble.querySelectorAll("pre").forEach(block => {
        if (block.querySelector(".copy-code-btn")) return;

        const btn = document.createElement("button")
        btn.className = "copy-code-btn"
        btn.innerHTML = `<i class="bi bi-copy"></i> Copy code`

        btn.onclick = (e) => {
            e.stopPropagation();
            const code = block.querySelector("code")?.innerText || block.innerText

            if (navigator.clipboard) {
                navigator.clipboard.writeText(code)
            } else {
                const textarea = document.createElement("textarea")
                textarea.value = code
                document.body.appendChild(textarea)
                textarea.select()
                document.execCommand("copy")
                document.body.removeChild(textarea)
            }

            btn.innerHTML = `<i class="bi bi-check2"></i> Copied`
            setTimeout(() => {
                btn.innerHTML = `<i class="bi bi-copy"></i> Copy code`
            }, 2000)
        }
        block.appendChild(btn)
    })


    /* math */
    if (window.MathJax) {
        MathJax.typesetPromise([bubble])
    }

    /* scroll */
    setTimeout(() => {
        if (window.innerWidth > 768) {
            chatEl.scrollTop = chatEl.scrollHeight
        }
    }, 50)

}

// 📐 SMART MATH NORMALIZER
function normalizeMathText(text) {
    if (!text) return "";

    // 1. Remove forced line breaks between formula and its condition (với, khi, n != ...)
    // Detect patterns like: $$...$$ \n (với...)
    let cleaned = text.replace(/(\$\$[\s\S]*?\$\$)\s*\n\s*(\(với|\(khi|với|khi|dk:|đk:|n\s*[≠|!=])/gi, "$1 $2");

    // 2. Fix multiple single newlines that should be a single paragraph
    // But keep double newlines as paragraph breaks
    // (This makes it look more like ChatGPT)
    cleaned = cleaned.replace(/([^\n])\n([^\n\*\[\-\d])/g, "$1 $2");

    return cleaned;
}

function streamBot(text, exportType = null) {
    const artifacts = typeof extractArtifacts === "function" ? extractArtifacts(text) : [];
    if (artifacts.length === 0 && (exportType || (typeof userWantsExport === "function" && (userWantsExport(lastUserMessage) || userWantsExport(text))))) {
        const fallbackText = typeof getFallbackExportContent === "function" ? getFallbackExportContent(text) : (lastExportContent || lastAIAnswer || cleanBotText(text));
        if (typeof isValidForExport === "function" && isValidForExport(fallbackText)) {
            artifacts.push({
                type: exportType || (typeof detectExportType === "function" ? detectExportType(lastUserMessage || text) : "docx"),
                title: "Tài liệu hoàn chỉnh",
                content: fallbackText
            });
        }
    }

    const cleanedFullText = cleanBotText(text);
    if (!cleanedFullText.trim() && artifacts.length === 0) return;

    const normalizedText = normalizeMathText(text);

    const row = document.createElement("div");
    row.className = "msgrow bot";

    const bubble = document.createElement("div");
    bubble.className = "bubble position-relative";

    const content = document.createElement("div");
    content.className = "message-content";

    bubble.appendChild(content);

    if (artifacts.length === 0) {
        bubble.appendChild(createMessageActions({ text, exportType }));
    }

    row.appendChild(bubble);
    chatEl.appendChild(row);

    // If artifacts exist, render conversational text immediately and attach Artifact Cards
    if (artifacts.length > 0) {
        const displayIntro = cleanedFullText.trim() || "Dưới đây là tài liệu bạn yêu cầu:";
        try {
            content.innerHTML = renderMarkdownWithMath(displayIntro);
            typesetMathInElement(content);
        } catch {
            content.textContent = displayIntro;
        }
        if (typeof renderArtifactCards === "function") {
            renderArtifactCards(bubble, artifacts);
        }
        chatEl.scrollTop = chatEl.scrollHeight;
        isSending = false;
        return;
    }

    let i = 0;
    const totalLen = normalizedText.length;

    // Phản hồi ngắn dưới 80 ký tự -> hiển thị ngay tức thì
    if (totalLen < 80) {
        const cleanedCurrent = cleanBotText(normalizedText);
        try {
            content.innerHTML = renderMarkdownWithMath(cleanedCurrent);
            typesetMathInElement(content);
        } catch (markedErr) {
            content.textContent = cleanedCurrent;
        }
        chatEl.scrollTop = chatEl.scrollHeight;
        isSending = false;
        return;
    }

    // Tăng tốc độ stream theo chunk mượt mà, tránh nghẽn luồng render
    const step = Math.max(4, Math.ceil(totalLen / 45));

    const timer = setInterval(() => {
        i += step;
        if (i > totalLen) i = totalLen;

        const currentText = normalizedText.substring(0, i);
        const cleanedCurrent = cleanBotText(currentText);

        try {
            const escaped = typeof escapeUnsafeTags === "function" ? escapeUnsafeTags(cleanedCurrent) : cleanedCurrent;
            const parsedHtml = marked.parse(escaped)
                .replace(/language-j/g, "language-java");
            content.innerHTML = parsedHtml;
        } catch (markedErr) {
            content.textContent = cleanedCurrent;
        }

        chatEl.scrollTop = chatEl.scrollHeight;

        if (i >= totalLen) {
            clearInterval(timer);
            isSending = false;

            // Highlight syntax 1 lần duy nhất sau khi stream xong để tiết kiệm tài nguyên
            content.querySelectorAll("pre code").forEach(el => {
                try { hljs.highlightElement(el); } catch (e) { }
            });

            content.querySelectorAll("pre").forEach(block => {
                if (block.querySelector(".copy-code-btn")) return;

                const btn = document.createElement("button");
                btn.className = "copy-code-btn";
                btn.innerHTML = `<i class="bi bi-copy"></i> Copy code`;

                btn.onclick = (e) => {
                    e.stopPropagation();
                    const code = block.querySelector("code")?.innerText || block.innerText;
                    navigator.clipboard.writeText(code);

                    btn.innerHTML = `<i class="bi bi-check2"></i> Copied`;
                    setTimeout(() => {
                        btn.innerHTML = `<i class="bi bi-copy"></i> Copy code`;
                    }, 2000);
                };
                block.appendChild(btn);
            });

            if (window.MathJax) {
                try {
                    MathJax.typesetPromise([bubble]).catch(mjErr => { });
                } catch (e) { }
            }
        }
    }, 16);
}

// ====== Real-time SSE Streaming Helper ======
function renderStreamingFinalBubble(bubble, text, exportType = null) {
    if (!bubble) return;
    const artifacts = typeof extractArtifacts === "function" ? extractArtifacts(text) : [];
    if (artifacts.length === 0 && (exportType || (typeof userWantsExport === "function" && (userWantsExport(lastUserMessage) || userWantsExport(text))))) {
        const fallbackText = typeof getFallbackExportContent === "function" ? getFallbackExportContent(text) : (lastExportContent || lastAIAnswer || cleanBotText(text));
        if (typeof isValidForExport === "function" && isValidForExport(fallbackText)) {
            artifacts.push({
                type: exportType || (typeof detectExportType === "function" ? detectExportType(lastUserMessage || text) : "docx"),
                title: "Tài liệu hoàn chỉnh",
                content: fallbackText
            });
        }
    }

    const cleanedFullText = cleanBotText(text);
    const normalizedText = normalizeMathText(text);
    bubble.innerHTML = "";
    bubble.className = "bubble position-relative";

    const content = document.createElement("div");
    content.className = "message-content";
    bubble.appendChild(content);

    const displayIntro = (artifacts.length > 0) ? (cleanedFullText.trim() || "Dưới đây là tài liệu bạn yêu cầu:") : normalizedText;
    try {
        content.innerHTML = renderMarkdownWithMath(displayIntro);
        typesetMathInElement(content);
    } catch {
        content.textContent = displayIntro;
    }

    if (artifacts.length > 0) {
        if (typeof renderArtifactCards === "function") {
            renderArtifactCards(bubble, artifacts);
        }
    } else {
        bubble.appendChild(createMessageActions({ text, exportType }));
    }

    scrollToBottom();
    isSending = false;
}

async function streamGeminiChat(userText, images, files, onChunk) {
    const isThinking = mode === "thinking" || (mode === "auto" && detectThinking(userText));
    const allContents = [];
    if (Array.isArray(chatHistoryBuffer) && chatHistoryBuffer.length > 0) {
        const recentHistory = chatHistoryBuffer.slice(-10);
        for (const item of recentHistory) {
            if (item && item.role && Array.isArray(item.parts) && item.parts.length > 0) {
                allContents.push({
                    role: item.role,
                    parts: item.parts.map(p => ({ ...p }))
                });
            }
        }
    }
    const currentTurnParts = [
        { text: userText },
        ...files.map(f => ({ text: "File " + f.name + ":\n" + f.content })),
        ...images.map(img => ({
            inline_data: {
                mime_type: img.startsWith("data:image/png") ? "image/png" : "image/jpeg",
                data: img.split(",")[1]
            }
        }))
    ];
    allContents.push({ role: "user", parts: currentTurnParts });

    const payload = {
        contents: allContents,
        generationConfig: {
            temperature: isThinking ? 0.7 : 0.5,
            maxOutputTokens: isThinking ? 20000 : 8192
        }
    };

    const res = await fetch("/api/chat/stream", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
            model: mode === "thinking" ? "gemini-2.5-flash" : "gemini-2.5-flash",
            payload: { ...payload, mode: mode },
            user_id: localStorage.getItem("user_id") || "1",
            conversation_id: conversation_id,
            intent: "chat"
        })
    });

    if (!res.ok) {
        throw new Error(`HTTP ${res.status}`);
    }

    const reader = res.body.getReader();
    const decoder = new TextDecoder("utf-8");
    let buffer = "";
    let accumulatedText = "";
    let finalDoneData = null;

    while (true) {
        const { value, done } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split("\n");
        buffer = lines.pop() || "";

        for (const line of lines) {
            const trimmed = line.trim();
            if (trimmed.startsWith("data: ")) {
                const jsonStr = trimmed.substring(6).trim();
                if (!jsonStr) continue;
                try {
                    const parsed = JSON.parse(jsonStr);
                    if (parsed.type === "meta" || parsed.conversationId || (parsed.conversation_id && !conversation_id)) {
                        const newCid = parsed.conversationId || parsed.conversation_id;
                        if (newCid && (!conversation_id || conversation_id !== newCid)) {
                            conversation_id = newCid;
                            window.history.pushState({ cid: newCid, conversationId: newCid }, "", `/c/${newCid}`);
                            if (typeof loadHistory === "function") {
                                loadHistory();
                            }
                        }
                    }
                    if (parsed.chunk) {
                        accumulatedText += parsed.chunk;
                        if (typeof onChunk === "function") {
                            onChunk(parsed.chunk, accumulatedText);
                        }
                    }
                    if (parsed.error) {
                        const errText = parsed.error.startsWith("❌") ? parsed.error : `❌ ${parsed.error}`;
                        if (typeof onChunk === "function") {
                            onChunk(errText, errText);
                        }
                        accumulatedText = errText;
                        finalDoneData = { ...parsed, error: parsed.error, answer: errText };
                    }
                    if (parsed.done) {
                        finalDoneData = parsed;
                        const finalCid = parsed.conversation_id || parsed.conversationId || conversation_id;
                        if (finalCid && (!conversation_id || conversation_id !== finalCid)) {
                            conversation_id = finalCid;
                            window.history.pushState({ cid: finalCid, conversationId: finalCid }, "", `/c/${finalCid}`);
                        }
                        if (parsed.conversation_title && parsed.conversation_title !== "Chat mới" && !parsed.conversation_title.startsWith("{")) {
                            syncSidebarChatTitle(parsed.conversation_title, finalCid);
                        }
                    }
                } catch (e) {
                    console.debug("SSE Parse error:", e);
                }
            }
        }
    }

    const finalAnswer = (finalDoneData && finalDoneData.answer) ? finalDoneData.answer : accumulatedText;
    if (!finalAnswer || finalAnswer.trim() === "") {
        return {
            ok: false,
            empty: true,
            text: "",
            chat_id: finalDoneData?.conversation_id || conversation_id
        };
    }

    if (finalDoneData && finalDoneData.error) {
        return {
            ok: false,
            text: finalAnswer,
            error: finalDoneData.error,
            chat_id: finalDoneData?.conversation_id || conversation_id
        };
    }

    return {
        ok: true,
        text: finalAnswer,
        chat_id: finalDoneData?.conversation_id || conversation_id,
        title: finalDoneData?.conversation_title,
        doneData: finalDoneData
    };
}

// ====== Gemini ======
async function callGemini(userText, images, files) {

    let detectedIntent = "chat";

    const isThinking =
        mode === "thinking" ||
        (mode === "auto" && detectThinking(userText))

    const thinkingPrompt = isThinking
        ? "\nHãy suy nghĩ từng bước trước khi trả lời.\n"
        : ""
    const parts = [{ text: userText }];

    const isFromSearch = userText.includes("Dựa vào thông tin sau:");
    const isInternalPrompt = userText.startsWith("Câu hỏi:");

    const toolInstruction = isFromSearch
        ? "\nBạn đang có dữ liệu từ internet. Trả lời luôn, KHÔNG được search nữa.\n"
        : "";

    // Build multi-turn contents from chatHistoryBuffer + current turn
    const allContents = [];
    if (Array.isArray(chatHistoryBuffer) && chatHistoryBuffer.length > 0) {
        // Gửi tối đa 10 tin nhắn gần nhất (5 lượt trao đổi) để giữ mạch hội thoại
        const recentHistory = chatHistoryBuffer.slice(-10);
        for (const item of recentHistory) {
            if (item && item.role && Array.isArray(item.parts) && item.parts.length > 0) {
                allContents.push({
                    role: item.role,
                    parts: item.parts.map(p => ({ ...p }))
                });
            }
        }
    }

    // Context fallback: if history was empty but lastAIAnswer exists and user asks to export/save
    const lowerUTxt = (userText || "").toLowerCase();
    const isExportIntent = lowerUTxt.includes("word") || lowerUTxt.includes("xuất") || lowerUTxt.includes("file") || lowerUTxt.includes("pdf");
    if (allContents.length === 0 && lastAIAnswer && isExportIntent) {
        allContents.push({
            role: "model",
            parts: [{ text: lastAIAnswer }]
        });
    }

    const currentTurnParts = [
        { text: userText },
        ...files.map(f => ({
            text: "File " + f.name + ":\n" + f.content
        })),
        ...images.map(img => ({
            inline_data: {
                mime_type: img.startsWith("data:image/png") ? "image/png" : "image/jpeg",
                data: img.split(",")[1]
            }
        }))
    ];

    allContents.push({
        role: "user",
        parts: currentTurnParts
    });

    const payload = {
        contents: allContents,
        generationConfig: {
            temperature: isThinking ? 0.7 : 0.5,
            maxOutputTokens: isThinking ? 20000 : 8192
        }
    };

    console.log("[CHAT REQUEST]", payload);

    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), REQUEST_TIMEOUT_MS);

    let res;

    try {

        res = await fetch(WORKER_URL, {
            method: "POST",
            signal: controller.signal,
            headers: {
                "Content-Type": "application/json"
            },
            body: JSON.stringify({
                model: mode === "thinking" ? "gemini-2.5-flash" : "gemini-2.5-flash",
                payload: {
                    ...payload,
                    mode: mode
                },
                user_id: localStorage.getItem("user_id"),
                conversation_id: conversation_id,
                intent: detectedIntent
            })

        });

    } catch (err) {

        if (err.name === "AbortError") {
            throw new Error("timeout");
        }

        throw err;

    }

    clearTimeout(timeout);

    const raw = await res.text();
    console.log("[CHAT RAW RESPONSE]", raw);

    let data;

    try {
        data = JSON.parse(raw);
        console.log("[CHAT JSON RESPONSE]", data);
    } catch {
        throw new Error("server trả dữ liệu không phải JSON");
    }

    // 🔥 FIX ĐÚNG CHỖ (sau parse)
    if (data?.conversation_id && !conversation_id) {
        conversation_id = data.conversation_id
        window.history.pushState({ cid: conversation_id }, "", "/c/" + conversation_id);
        loadHistory()
    }
    if (data?.conversation_title && data.conversation_title !== "Chat mới" && !data.conversation_title.startsWith("{")) {
        syncSidebarChatTitle(data.conversation_title, conversation_id);
        setTimeout(() => loadHistory(), 600);
    }



    if (!res.ok || data.ok === false) {
        return {
            ok: false,
            text: data.answer || data?.error?.message || "Lỗi máy chủ",
            error: data.error || "http error",
            chat_id: data.chat_id
        };
    }

    const respParts = data?.candidates?.[0]?.content?.parts || [];
    let reply = respParts.map(p => p.text).join("\n");
    if (!reply && data.answer) reply = data.answer; // Use standardized answer if candidates missing

    // 🔥 AUTO TITLE (CHỈ 1 LẦN)
    if (conversation_id && !autoRenamed) {

        autoRenamed = true
        fetch("/ai/api/rename_conversation.php", {
            method: "POST",
            headers: {
                "Content-Type": "application/json"
            },
            body: JSON.stringify({
                id: conversation_id,
                isAuto: true
            })
        }).then(() => loadHistory())

    }

    if (!isInternalPrompt) {
        chatHistoryBuffer.push({ role: "user", parts });
        chatHistoryBuffer.push({ role: "model", parts: [{ text: reply }] });
    }

    if (chatHistoryBuffer.length > 20) {
        chatHistoryBuffer = chatHistoryBuffer.slice(-20);
    }


    return {
        ok: true,
        text: reply,
        images: data.images || [],
        chat_id: data.chat_id
    };

}

// ====== SEND ======
async function send() {
    let reply = null;
    let geminiRes = null;
    let exportType = null;
    let current_chat_id = null;

    if (isSending) {
        if (Date.now() - (window._lastSendTimestamp || 0) > 12000) {
            console.warn("[SEND AUTO-RECOVERY] Resetting stuck isSending state.");
            isSending = false;
        } else {
            return;
        }
    }
    window._lastSendTimestamp = Date.now();
    isSending = true;
    showWelcomeScreen(false);


    const userText = msgEl.value.trim()
    if (!userText.toLowerCase().includes("tìm trên mạng") && !userText.startsWith("/search")) {
        lastUserMessage = userText
    }

    const imgInput = document.getElementById("fileImg")
    const camInput = document.getElementById("fileCam")
    const docInput = document.getElementById("fileDoc")

    let images = []
    let files = []

    // ✅ FIXED: Only collect from imgInput because addImages consolidated everything there
    const allImages = Array.from(imgInput.files)

    if (allImages.length > 0) {
        for (const file of allImages) {


            const base64 = await new Promise(resolve => {
                const reader = new FileReader()
                reader.onload = () => resolve(reader.result)
                reader.readAsDataURL(file)
            })

            images.push(base64)

        }

    }

    if (window._pendingLearningImageBase64) {
        images.push(window._pendingLearningImageBase64);
        window._pendingLearningImageBase64 = null;
    }

    if (docInput.files.length > 0) {

        for (const file of docInput.files) {

            let text = ""

            if (file.name.endsWith(".docx")) {

                const arrayBuffer = await file.arrayBuffer()

                const result = await mammoth.extractRawText({
                    arrayBuffer: arrayBuffer
                })

                text = result.value

            } else {

                text = await file.text()

            }

            files.push({
                name: file.name,
                content: text
            })

        }

    }



    if (!userText && images.length === 0 && files.length === 0) {
        isSending = false
        return
    }

    // ===== TOOL DETECTION =====
    if (userText.startsWith("/search ") || userText.toLowerCase().includes("tìm trên mạng")) {

        let query = userText
        query = query.replace(/tìm trên mạng/gi, "")
        query = query.replace(/^\/search/gi, "")
        query = query.trim()

        if (!query) {
            addBotBubble("❌ bạn chưa nhập nội dung cần tìm")
            isSending = false   // 🔥 THÊM
            return
        }

        addUserBubble(userText)
        scrollToBottom()

        runSearch(query, userText)

        msgEl.value = ""

        isSending = false   // 🔥 QUAN TRỌNG
        return
    }

    // ===== EXPORT COMMAND =====

    // export txt (giữ nguyên)
    if (userText.trim() === "/export") {
        isSending = false
        return exportFile(lastAIAnswer)
    }


    // 🔥 export docx
    if (userText.toLowerCase().includes("xuất file docx")) {
        if (!lastExportContent) {
            addBotBubble("❌ chưa có nội dung để xuất")
            isSending = false
            return
        }
        isSending = false
        return exportDOCX(lastExportContent)
    }

    const displayUserText = window._pendingUserDisplaySummary || userText;
    window._pendingUserDisplaySummary = null;
    addUserBubble(displayUserText, images)
    scrollToBottom()

    msgEl.value = ""
    msgEl.style.height = "auto"

    document.getElementById("attachments").innerHTML = ""
    imgInput.value = ""
    camInput.value = ""
    docInput.value = ""


    const typing = document.createElement("div")
    typing.className = "msgrow bot"
    typing.innerHTML = `<div class="bubble"><span class="spinner-grow spinner-grow-sm text-primary me-2"></span>⚡ Đang suy nghĩ...</div>`

    chatEl.appendChild(typing)
    scrollToBottom()

    let liveBotRow = null;
    let liveBotBubble = null;
    geminiRes = null;

    try {

        try {
            geminiRes = await streamGeminiChat(userText, images, files, (chunk, fullText) => {
                if (typing && typing.parentNode) typing.remove();
                if (!liveBotBubble) {
                    liveBotRow = document.createElement("div");
                    liveBotRow.className = "msgrow bot";
                    liveBotBubble = document.createElement("div");
                    liveBotBubble.className = "bubble";
                    liveBotRow.appendChild(liveBotBubble);
                    chatEl.appendChild(liveBotRow);
                }
                liveBotBubble.innerHTML = renderMarkdownWithMath(fullText);
                scrollToBottom();
            });

            // If stream completed but text was empty, automatically fallback to JSON callGemini
            if (!geminiRes || geminiRes.empty || !geminiRes.text || geminiRes.text.trim() === "") {
                console.warn("SSE stream finished with empty text, automatically falling back to JSON callGemini...");
                if (liveBotRow && liveBotRow.parentNode) liveBotRow.remove();
                liveBotRow = null;
                liveBotBubble = null;
                geminiRes = await callGemini(userText, images, files);
            }
        } catch (streamErr) {
            console.warn("SSE Stream encountered error, falling back to callGemini:", streamErr);
            if (liveBotRow && liveBotRow.parentNode) liveBotRow.remove();
            liveBotRow = null;
            liveBotBubble = null;
            geminiRes = await callGemini(userText, images, files);
        }

        if (typing && typing.parentNode) typing.remove();

        if (!geminiRes || !geminiRes.ok) {
            if (liveBotRow && liveBotRow.parentNode) liveBotRow.remove();
            const errDetail = geminiRes?.text || geminiRes?.error || "Lỗi phản hồi từ server";
            addBotBubble(errDetail.startsWith("❌") || errDetail.startsWith("⚠️") ? errDetail : "❌ " + errDetail);
            isSending = false;
            return;
        }

        reply = geminiRes.text;
        current_chat_id = geminiRes.chat_id;

        if (!reply || reply.trim() === "") {
            if (liveBotRow && liveBotRow.parentNode) liveBotRow.remove();
            const errDetail = geminiRes?.error || "Server không trả về nội dung (vui lòng kiểm tra kết nối mạng hoặc thử lại).";
            addBotBubble(errDetail.startsWith("❌") || errDetail.startsWith("⚠️") ? errDetail : "❌ " + errDetail);
            isSending = false;
            return;
        }

        if (current_chat_id && !conversation_id) {
            conversation_id = current_chat_id;
            window.history.pushState({ cid: conversation_id }, "", "/c/" + conversation_id);
        }

        if (geminiRes.title && geminiRes.title !== "Chat mới" && !geminiRes.title.startsWith("{")) {
            syncSidebarChatTitle(geminiRes.title, current_chat_id || conversation_id);
            if (typeof loadHistory === "function") {
                loadHistory();
                setTimeout(() => loadHistory(), 600);
            }
        }

        // 🔥 AI quyết định export
        let exportType = null
        let rawReply = reply   // 🔥 giữ bản gốc

        const exportMatch = rawReply.match(/(?:^|\n)\[EXPORT\]\s*(docx|pdf|txt|html|xlsx)\s*$/i);
        if (exportMatch) {
            exportType = exportMatch[1].toLowerCase();
            reply = rawReply.replace(/(?:^|\n)\[EXPORT\]\s*(docx|pdf|txt|html|xlsx)\s*$/i, "").trim();

            if (isValidForExport(reply)) {
                lastExportContent = reply;
                localStorage.setItem("lastExportContent", reply);
            }
        } else {
            lastAIAnswer = reply;

            if (isValidForExport(reply)) {
                lastExportContent = reply;
                localStorage.setItem("lastExportContent", reply);
            }
        }

        // 🔥 AUTO GENMUSIC
        if (reply.includes("[GENMUSIC]")) {
            if (liveBotRow && liveBotRow.parentNode) liveBotRow.remove();
            const tag = "[GENMUSIC]";
            const index = reply.indexOf(tag);

            let textBefore = reply.substring(0, index).trim();
            let remainder = reply.substring(index + tag.length).trim();

            let promptLines = remainder.split("\n");
            const musicPrompt = promptLines[0].trim();
            let textAfter = promptLines.slice(1).join("\n").trim();

            const fullText = (textBefore + "\n" + textAfter).trim();

            typing.remove();

            if (fullText) {
                streamBot(fullText, exportType);
            }

            const musicLoading = document.createElement("div");
            musicLoading.className = "msgrow bot";
            musicLoading.innerHTML = `<div class="bubble music-card assistant">🎵 Đang soạn nhạc cho bạn...</div>`;
            chatEl.appendChild(musicLoading);
            scrollToBottom();

            triggerMusicGeneration(musicPrompt, musicLoading, current_chat_id);
            isSending = false;
            return;
        }

        // 🔥 AUTO GENIMAGE (ROBUST JOIN)
        if (reply.includes("[GENIMAGE]")) {
            if (liveBotRow && liveBotRow.parentNode) liveBotRow.remove();
            // Kiểm tra xem đây có phải là nhầm lẫn không (AI trả [GENIMAGE] cho yêu cầu video)
            const isActuallyVideo = userText.toLowerCase().match(/video|clip|phim|animate|chuyển động/);
            if (isActuallyVideo) {
                typing.remove();
                addBotBubble("⚠️ AI đã nhầm lẫn khi tạo ảnh cho một yêu cầu video. Đang bỏ qua tạo ảnh để tránh sai lệch.");
                isSending = false;
                return;
            }

            const tag = "[GENIMAGE]";
            const index = reply.indexOf(tag);

            let textBefore = reply.substring(0, index).trim();
            let remainder = reply.substring(index + tag.length).trim();

            let promptLines = remainder.split("\n");
            const imagePrompt = promptLines[0].trim();
            let textAfter = promptLines.slice(1).join("\n").trim();

            const fullText = (textBefore + "\n" + textAfter).trim();

            typing.remove();

            if (fullText) {
                streamBot(fullText, exportType);
            }

            const imgLoading = document.createElement("div");
            imgLoading.className = "msgrow bot";
            imgLoading.innerHTML = `<div class="bubble">🎨 Đang vẽ ảnh minh họa...</div>`;
            chatEl.appendChild(imgLoading);
            scrollToBottom();

            try {
                const textToStore = fullText || "Mô tả ảnh:";
                console.log("[IMAGE REQUEST FROM UI]", imagePrompt);

                const imgRes = await fetch("/ai/api/generate_image.php", {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({
                        prompt: imagePrompt,
                        conversation_id,
                        user_id: Number(localStorage.getItem("user_id") || 0),
                        assistant_text: textToStore
                    })
                });

                const rawText = await imgRes.text();
                imgLoading.remove();

                let imgData = JSON.parse(rawText);
                console.log("[IMAGE FINAL RESPONSE]", imgData);

                if (imgRes.ok && imgData?.success) {
                    const cleanMimeType = String(imgData.mimeType || "image/png").trim();
                    const cleanBase64 = String(imgData.imageBase64 || "").replace(/\s+/g, "");
                    const imageSrc = `data:${cleanMimeType};base64,${cleanBase64}`;

                    const row = document.createElement("div");
                    row.className = "msgrow bot";
                    row.innerHTML = `
                        <div class="bubble image-bubble-premium">
                            <div class="image-wrapper-premium">
                                <img src="${imageSrc}" alt="Generated image" onclick="viewCleanImage('${imageSrc}')">
                                <button onclick="downloadCleanImage('${imageSrc}', 'btoan_ai_image.png', event)" class="image-download-btn-premium border-0" title="Tải ảnh">
                                    <i class="bi bi-download"></i>
                                </button>
                            </div>
                        </div>
                    `;
                    chatEl.appendChild(row);
                } else {
                    addBotBubble("❌ Lỗi vẽ ảnh: " + (imgData?.error || "Lỗi không xác định"));
                }
            } catch (e) {
                imgLoading.remove();
                addBotBubble("❌ Lỗi kết nối khi vẽ ảnh: " + e.message);
            }

            isSending = false;
            scrollToBottom();
            return;
        }

        // 🔥 AUTO EDITIMAGE
        if (reply.includes("[EDITIMAGE]")) {
            const tag = "[EDITIMAGE]";
            const index = reply.indexOf(tag);

            let textBefore = reply.substring(0, index).trim();
            let remainder = reply.substring(index + tag.length).trim();
            let promptLines = remainder.split("\n");
            const imagePrompt = promptLines[0].trim();
            let textAfter = promptLines.slice(1).join("\n").trim();

            const fullText = (textBefore + "\n" + textAfter).trim();

            typing.remove();

            if (fullText) {
                streamBot(fullText, exportType);
            }

            let baseImage = images.length > 0 ? images[0] : null;

            // 🔥 TỰ ĐỘNG TÌM ẢNH CŨ (BỀN VỮNG: Gọi Backend)
            if (!baseImage && conversation_id) {
                try {
                    const lastImgRes = await fetch(`/ai/api/get_last_image.php?conversation_id=${conversation_id}`);
                    const lastImgData = await lastImgRes.json();
                    if (lastImgData.success && lastImgData.image_url) {
                        baseImage = lastImgData.image_url;
                    }
                } catch (e) {
                    console.log("History image fetch error:", e);
                }
            }

            if (!baseImage) {
                addBotBubble("❌ Bạn cần gửi kèm ảnh hoặc đã từng gửi ảnh trong đoạn chat này để tôi có thể chỉnh sửa.");
                isSending = false;
                return;
            }

            const imgLoading = document.createElement("div");
            imgLoading.className = "msgrow bot";
            imgLoading.innerHTML = `<div class="bubble">✨ Đang chỉnh sửa ảnh theo yêu cầu...</div>`;
            chatEl.appendChild(imgLoading);
            scrollToBottom();

            try {
                const textToStore = fullText || "Ảnh đã chỉnh sửa:";
                const imgRes = await fetch("/ai/api/edit_image.php", {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({
                        prompt: imagePrompt,
                        image: baseImage,
                        conversation_id,
                        user_id: Number(localStorage.getItem("user_id") || 0),
                        assistant_text: textToStore
                    })
                });

                const rawText = await imgRes.text();
                imgLoading.remove();
                let imgData = JSON.parse(rawText);

                if (imgRes.ok && imgData?.success) {
                    const cleanMimeType = String(imgData.mimeType || "image/png").trim();
                    const cleanBase64 = String(imgData.imageBase64 || "").replace(/\s+/g, "");
                    const imageSrc = `data:${cleanMimeType};base64,${cleanBase64}`;

                    const row = document.createElement("div");
                    row.className = "msgrow bot";
                    row.innerHTML = `
                        <div class="bubble image-bubble-premium edit-result">
                            <div class="edit-label">✨ Kết quả chỉnh sửa</div>
                            <div class="image-wrapper-premium">
                                <img src="${imageSrc}" alt="Edited image" onclick="viewImage('${imageSrc}')">
                                <a href="${imageSrc}" download="btoan_edited_image.png" class="image-download-btn-premium" title="Tải ảnh">
                                    <i class="bi bi-download"></i>
                                </a>
                            </div>
                        </div>
                    `;
                    chatEl.appendChild(row);
                } else {
                    addBotBubble("❌ Lỗi chỉnh sửa ảnh: " + (imgData?.error || "Lỗi không xác định"));
                }
            } catch (e) {
                imgLoading.remove();
                addBotBubble("❌ Lỗi kết nối khi sửa ảnh: " + e.message);
            }

            isSending = false;
            scrollToBottom();
            return;
        }

        // 🔥 AUTO GENVIDEO
        if (reply.includes("[GENVIDEO]")) {
            const isActuallyMusic = (userText || "").toLowerCase().match(/nhạc|bài hát|song|music|giai điệu|hòa âm|melody|sáng tác/);
            if (isActuallyMusic) {
                const tag = "[GENVIDEO]";
                const index = reply.indexOf(tag);
                let textBefore = reply.substring(0, index).trim();
                let remainder = reply.substring(index + tag.length).trim();
                let promptLines = remainder.split("\n");
                const musicPrompt = promptLines[0].trim() || userText;
                let textAfter = promptLines.slice(1).join("\n").trim();
                const fullText = (textBefore + "\n" + textAfter).trim();

                typing.remove();
                if (fullText) streamBot(fullText, exportType);

                const musicLoading = document.createElement("div");
                musicLoading.className = "msgrow bot";
                musicLoading.innerHTML = `<div class="bubble music-card assistant">🎵 Đang soạn nhạc cho bạn...</div>`;
                chatEl.appendChild(musicLoading);
                scrollToBottom();

                triggerMusicGeneration(musicPrompt, musicLoading, current_chat_id);
                isSending = false;
                return;
            }

            const tag = "[GENVIDEO]";
            const index = reply.indexOf(tag);

            let textBefore = reply.substring(0, index).trim();
            let remainder = reply.substring(index + tag.length).trim();
            let promptLines = remainder.split("\n");
            const videoPrompt = promptLines[0].trim();
            let textAfter = promptLines.slice(1).join("\n").trim();

            const fullText = (textBefore + "\n" + textAfter).trim();

            typing.remove();

            if (fullText) {
                streamBot(fullText, exportType);
            }

            // LUÔN LUÔN CHỈ 1 VIDEO (8S) - Theo yêu cầu người dùng
            const segmentDuration = 8;

            // 🛡️ HÀM LÀM SẠCH VĂN BẢN (Né bộ lọc Google)
            const sanitizePrompt = (text) => {
                const map = {
                    "trói buộc": "gắn kết",
                    "quất": "dạy bảo",
                    "đau": "nghiêm khắc",
                    "mũi kiếm": "thử thách",
                    "sắt đá": "kiên cường",
                    "chết đi": "thay đổi",
                    "đánh": "dạy",
                    "kiếm": "thử thách",
                    "bạo lực": "mạnh mẽ"
                };
                let cleanText = text;
                Object.keys(map).forEach(key => {
                    const regex = new RegExp(key, "gi");
                    cleanText = cleanText.replace(regex, map[key]);
                });
                return cleanText;
            };

            const segmentPrompt = sanitizePrompt(videoPrompt);

            // 🔥 TỐI ƯU CHO TIẾNG VIỆT (Gửi thêm chỉ dẫn tiếng Anh cho Google Veo)
            const hasVietnamese = /[àáạảãâầấậẩẫăằắặẳẵèéẹẻẽêềếệểễìíịỉĩòóọỏõôồốộổỗơờớợởỡùúụủũưừứựửữỳýỵỷỹđ]/i.test(segmentPrompt);
            let finalVideoPrompt = segmentPrompt;
            if (hasVietnamese) {
                // Thêm chỉ dẫn Lip-sync tiếng Việt bằng tiếng Anh để Google dễ hiểu
                finalVideoPrompt += `. Lip-sync accurately to the Vietnamese words: "${segmentPrompt}". The person is speaking Vietnamese fluently with clear articulation.`;
            }

            // Get reference image (first one)
            let refImage = images.length > 0 ? images[0] : null;

            // 🔥 TỰ ĐỘNG TÌM ẢNH CŨ (BỀN VỮNG: Gọi Backend)
            if (!refImage && conversation_id) {
                try {
                    const lastImgRes = await fetch(`/ai/api/get_last_image.php?conversation_id=${conversation_id}`);
                    const lastImgData = await lastImgRes.json();
                    if (lastImgData.success && lastImgData.image_url) {
                        refImage = lastImgData.image_url;
                    }
                } catch (e) {
                    console.log("History image fetch error:", e);
                }
            }

            // Fallback: Tìm trong DOM nếu backend fail
            if (!refImage) {
                const allUserImages = Array.from(document.querySelectorAll('.msgrow.me .usertxt img, .msgrow.me .img-preview img')).reverse();
                if (allUserImages.length > 0) {
                    refImage = allUserImages[0].src;
                }
            }

            // Create loading bubble
            const vidLoading = document.createElement("div");
            vidLoading.className = "msgrow bot";
            vidLoading.innerHTML = `
                <div class="bubble assistant video-processing-card">
                    <div class="processing-glow"></div>
                    <div class="d-flex align-items-center gap-3">
                        <div class="spinner-border text-primary spinner-border-sm" role="status"></div>
                        <div class="flex-grow-1">
                            <div class="fw-bold mb-1">Đang tạo video cho bạn...</div>
                            <div class="progress" style="height: 6px; background: #f1f5f9; border-radius: 10px;">
                                <div class="progress-bar progress-bar-striped progress-bar-animated" role="progressbar" style="width: 0%"></div>
                            </div>
                            <div class="text-muted mt-1 progress-text" style="font-size: 11px;">Đang chuẩn bị nội dung...</div>
                        </div>
                    </div>
                </div>
            `;
            chatEl.appendChild(vidLoading);
            scrollToBottom();

            try {
                const vidRes = await fetch("/ai/api/generate_video.php", {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({
                        prompt: finalVideoPrompt,
                        conversation_id,
                        user_id: Number(localStorage.getItem("user_id") || 0),
                        chat_id: current_chat_id,
                        image: refImage,
                        duration: segmentDuration
                    })
                });

                const vidData = await vidRes.json();
                if (vidRes.ok && vidData?.success) {
                    pollVideoStatus(vidData.job_id, vidLoading, current_chat_id);
                } else {
                    vidLoading.innerHTML = `<div class="bubble assistant border-danger text-danger">❌ Lỗi: ${vidData?.error || "Lỗi không xác định"}</div>`;
                }
            } catch (e) {
                vidLoading.innerHTML = `<div class="bubble assistant border-danger text-danger">❌ Lỗi kết nối video</div>`;
            }

            isSending = false;
            scrollToBottom();
            return;
        }


        // 🔥 AUTO SEARCH
        if (reply.startsWith("[SEARCH]")) {
            const query = reply.replace("[SEARCH]", "").trim();

            typing.remove();

            addBotBubble("🔎 Đang tìm: " + query);

            await runSearch(query, userText);

            isSending = false;
            return;
        }

        if (!reply.startsWith("[SEARCH]") && !reply.startsWith("[EXPORT]")) {
            if (isValidForExport(reply)) {
                lastExportContent = reply;
                localStorage.setItem("lastExportContent", reply);
            }
        }



        if (typing && typing.parentNode) typing.remove();

        console.log(performance.now(), "[SEND UI RENDER] ANSWER LENGTH", reply.length);

        if (liveBotBubble) {
            renderStreamingFinalBubble(liveBotBubble, reply, exportType);
            isSending = false;
        } else {
            streamBot(reply, exportType);
        }

        setTimeout(() => {
            console.log(performance.now(), "[MATHJAX TYPESET 100ms BEFORE]");
            if (window.MathJax) {
                try {
                    MathJax.typeset();
                    console.log(performance.now(), "[MATHJAX TYPESET 100ms AFTER]");
                } catch (mjEx) {
                    console.error(performance.now(), "[MATHJAX TYPESET 100ms EXCEPTION]", mjEx);
                }
            }
        }, 100);

    } catch (e) {
        if (typing && typing.parentNode) typing.remove();
        console.error("sendMessage error:", e);
        if (!reply || (typeof reply === "string" && reply.trim().length === 0)) {
            addBotBubble("lỗi: " + (e?.message || e));
        }
    } finally {
        isSending = false;
        const sendBtn = document.getElementById("send_btn") || document.getElementById("btnSendChat");
        if (sendBtn) sendBtn.disabled = false;
    }
}

// ====== LOGIN ======
async function login() {



    const username = document.getElementById("login_username").value;
    const password = document.getElementById("login_password").value;

    const res = await fetch("/ai/api/login.php", {
        method: "POST",
        headers: {
            "Content-Type": "application/json"
        },
        body: JSON.stringify({
            username,
            password
        })
    });

    const raw = await res.text();
    console.log("RAW LOGIN:", raw);

    let data;
    try {
        data = JSON.parse(raw);
    } catch (e) {
        console.error("JSON lỗi:", raw);
        return;
    }

    console.log("PARSED:", data);

    // 🔥 FORCE SAVE luôn (bỏ if)
    if (data.user_id) {

        console.log("SAVE user_id =", data.user_id);

        localStorage.setItem("user_id", String(data.user_id));
        localStorage.setItem("username", data.username)


        console.log("AFTER SAVE:", localStorage.getItem("user_id"));

        const lBox = document.getElementById("loginBox");
        if (lBox) lBox.style.display = "none";
        if (document.getElementById("userBox")) document.getElementById("userBox").style.display = "flex";
        if (document.getElementById("guestLoginBox")) document.getElementById("guestLoginBox").style.display = "none";

        await checkProfile(data.user_id)  // 🔥 QUAN TRỌNG

        loadHistory()
        newChat()

    } else {
        const errorText = data.error || data.message || "Sai tài khoản hoặc mật khẩu";
        document.getElementById("loginMsg").innerText = errorText;
    }

}
// ====== REGISTER ======
async function register() {

    const msgBox = document.getElementById("registerMsg")
    msgBox.innerHTML = ""

    const username = document.getElementById("reg_username").value
    const password = document.getElementById("reg_password").value
    const password2 = document.getElementById("reg_password2").value

    if (password !== password2) {

        msgBox.innerHTML = `<div class="text-danger">
    Mật khẩu nhập lại không khớp
    </div>`

        return
    }

    const res = await fetch("/ai/api/register.php", {
        method: "POST",
        headers: {
            "Content-Type": "application/json"
        },
        body: JSON.stringify({
            username,
            password
        })
    })

    const data = await res.json()

    if (data.status === "ok") {

        msgBox.innerHTML = `<div class="text-success">
    Đăng ký thành công ✔
    </div>`

        setTimeout(() => {
            showLogin()
        }, 1500)

    } else {

        msgBox.innerHTML = `<div class="text-danger">
    ${data.msg}
    </div>`

    }
    document.getElementById("reg_username").value = ""
    document.getElementById("reg_password").value = ""
    document.getElementById("reg_password2").value = ""

}

// ====== LOGOUT ======
function logout() {
    // Only remove user identity, DO NOT clear workspace configuration, active project or tabs
    localStorage.removeItem("user_id");
    localStorage.removeItem("username");
    localStorage.removeItem("full_name");
    localStorage.removeItem("email");
    localStorage.removeItem("avatar");
    localStorage.removeItem("role");
    fetch('/Account/Logout', { method: 'POST' }).finally(() => {
        location.reload();
    });
}

// ====== WELCOME SCREEN ======
function showWelcomeScreen(show) {
    const welcomeEl = document.getElementById("welcome-screen");
    const hubEl = document.getElementById("learning-hub-screen");
    const chatEl = document.getElementById("chat");
    if (!welcomeEl) return;
    if (show) {
        if (!window._isViewingLearningHub) {
            welcomeEl.style.display = "flex";
            if (hubEl) hubEl.style.display = "none";
            if (chatEl) chatEl.style.display = "none";
            updateWelcomeGreeting();
        }
    } else {
        welcomeEl.style.display = "none";
        if (hubEl) hubEl.style.display = "none";
        if (chatEl) chatEl.style.display = "block";
    }
}

function updateWelcomeGreeting(customName) {
    const greetingEl = document.getElementById("welcomeGreetingTitle");
    if (!greetingEl) return;
    const name = customName || localStorage.getItem("full_name") || localStorage.getItem("username") || "bạn";
    greetingEl.innerText = `Xin chào ${name}, hôm nay mình giúp gì được cho bạn?`;
}

// ====== HISTORY ======
async function loadHistory() {
    const user_id = localStorage.getItem("user_id");
    const list = document.getElementById("historyList");
    if (!list) return;

    if (!user_id || user_id === "undefined" || user_id === "0") {
        setupGuestUI();
        return;
    }

    try {
        const res = await fetch("/ai/api/get_conversations.php?user_id=" + user_id);
        const data = await res.json();

        list.innerHTML = "";
        if (!data || data.length === 0) {
            list.innerHTML = `<div class="px-3 py-3 text-muted text-center extra-small">Chưa có đoạn chat nào</div>`;
            return;
        }

        data.forEach((c) => {
            const div = document.createElement("div")
            // Apply active class if this is the current conversation
            const isActive = conversation_id == c.id;
            div.className = `chat-history-item d-flex justify-content-between align-items-center ${isActive ? 'active' : ''}`;
            div.setAttribute('data-id', c.id);

            let itemTitle = c.title || "Chat mới";
            if (isActive && (itemTitle === "Chat mới" || !itemTitle) && document.title.includes(" - Btoan AI")) {
                const docTitleCandidate = document.title.replace(" - Btoan AI", "").trim();
                if (docTitleCandidate && docTitleCandidate !== "Chat mới" && !docTitleCandidate.startsWith("{")) {
                    itemTitle = docTitleCandidate;
                }
            }

            div.innerHTML = `
    <div class="chat-title">${escapeHtml(itemTitle)}</div>
    <div class="dropdown" onclick="event.stopPropagation()">
        <button class="history-action-btn" data-bs-toggle="dropdown" aria-expanded="false">
            <i class="bi bi-three-dots"></i>
        </button>
        <ul class="dropdown-menu dropdown-menu-end">
            <li>
                <button class="dropdown-item" onclick="renameChat(${c.id}, '${c.title.replace(/'/g, "\\'")}')">
                    <i class="bi bi-pencil"></i> Đổi tên
                </button>
            </li>
            <li>
                <hr class="dropdown-divider opacity-50">
            </li>
            <li>
                <button class="dropdown-item text-danger" onclick="deleteChat(${c.id})">
                    <i class="bi bi-trash3"></i> Xoá
                </button>
            </li>
        </ul>
    </div>
    `;

            div.onclick = () => loadConversation(c.id);
            list.appendChild(div);
        });
    } catch (err) {
        console.warn("Failed to load history:", err);
    }
}

function updateSidebarActive(id) {
    document.querySelectorAll('.chat-history-item').forEach(item => {
        if (item.getAttribute('data-id') == id) {
            item.classList.add('active');
        } else {
            item.classList.remove('active');
        }
    });
}


//rename

async function renameChat(id, oldTitle) {

    const title = prompt("Nhập tên mới", oldTitle)

    if (!title) return

    await fetch("/ai/api/rename_conversation.php", {
        method: "POST",
        headers: {
            "Content-Type": "application/json"
        },
        body: JSON.stringify({
            id: id,
            title: title
        })
    })

    loadHistory()

}

//delete chat
async function deleteChat(id) {

    if (!confirm("Xoá cuộc trò chuyện này?")) return

    const res = await fetch("/ai/api/delete_conversation.php", {
        method: "POST",
        headers: {
            "Content-Type": "application/json"
        },
        body: JSON.stringify({
            id: id
        })
    })

    const text = await res.text()

    let data
    try {
        data = JSON.parse(text)
    } catch {
        console.log("SERVER ERROR:", text)
        addBotBubble("❌ server lỗi")
        return
    }
    console.log(data)
    loadHistory()

    if (conversation_id == id) {
        newChat(true);
    }
}

async function loadConversation(id, updateUrl = true) {
    if (!id) return;
    console.log(performance.now(), "[LOAD CONVERSATION START]", id);

    if (window._loadConvAbortController) {
        try { window._loadConvAbortController.abort(); } catch (e) { }
    }
    window._loadConvAbortController = new AbortController();
    const signal = window._loadConvAbortController.signal;

    window.isLoadingConversation = true;
    window._isViewingLearningHub = false;
    const hubEl = document.getElementById("learning-hub-screen");
    if (hubEl) hubEl.style.display = "none";
    const welcomeEl = document.getElementById("welcome-screen");
    if (welcomeEl) welcomeEl.style.display = "none";
    if (chatEl) chatEl.style.display = "block";

    if (window.innerWidth <= 768) {
        const sb = document.getElementById("sidebar");
        if (sb && sb.classList.contains("open")) {
            sb.classList.remove("open");
        }
    }

    window._loadingConversationId = id;
    conversation_id = id;
    if (updateUrl) {
        window.history.pushState({ cid: id, conversationId: id }, "", `/c/${id}`);
    }

    // Immediate UI Feedback
    updateSidebarActive(id);
    console.log(performance.now(), "[LOAD CONVERSATION CLEAR CHATEL INNERHTML]");
    chatEl.innerHTML = `
        <div class="d-flex flex-column align-items-center justify-content-center h-100 text-muted opacity-50" style="min-height: 220px;">
            <div class="spinner-border spinner-border-sm mb-2" role="status"></div>
            <div style="font-size: 13px;">Đang tải cuộc trò chuyện...</div>
        </div>
    `;
    if (attachments) attachments.innerHTML = "";
    chatHistoryBuffer = [];
    lastExportContent = "";

    try {
        let data = [];
        try {
            const res = await fetch(`/api/chat/conversation/${id}`, { signal });
            if (res.ok) {
                data = await res.json();
            } else {
                const fbRes = await fetch("/ai/api/get_messages.php?conversation_id=" + id, { signal });
                data = await fbRes.json();
            }
        } catch (fetchErr) {
            if (signal.aborted) return;
            const fbRes = await fetch("/ai/api/get_messages.php?conversation_id=" + id, { signal });
            data = await fbRes.json();
        }

        if (signal.aborted) return;

        const msgList = data?.messages || data?.data || (Array.isArray(data) ? data : []);
        console.log(performance.now(), "[LOAD CONVERSATION FETCH DONE]", msgList.length, "messages");

        // Cập nhật tiêu đề trang nếu có
        if (data?.title && data.title !== "Chat mới" && data.title !== "Cuộc trò chuyện" && !data.title.startsWith("{")) {
            document.title = `${data.title} - Btoan AI`;
            syncSidebarChatTitle(data.title, id);
        }

        chatEl.innerHTML = ""; // Clear loader
        showWelcomeScreen(false);

        if (!msgList || msgList.length === 0) {
            chatEl.innerHTML = `
                <div class="empty-conversation-notice text-center text-secondary py-5 d-flex flex-column align-items-center justify-content-center" style="min-height: 280px;">
                    <div style="font-size: 36px; margin-bottom: 12px; opacity: 0.6;">💬</div>
                    <div class="fw-semibold text-light mb-1" style="font-size: 14px;">Cuộc trò chuyện này chưa có nội dung tin nhắn</div>
                    <div class="text-muted extra-small" style="max-width: 340px;">Hãy nhập câu hỏi bên dưới để bắt đầu trò chuyện cùng Btoan AI.</div>
                </div>
            `;
            return;
        }

        msgList.forEach(m => {
            const rawText = m.text || m.content || "";
            if (m.role === "user") {
                addUserBubble(rawText, m.images || []);
                chatHistoryBuffer.push({
                    role: "user",
                    parts: [{ text: rawText }]
                });
            } else {
                if (rawText.startsWith("[EXPORT_FILE]||")) {
                    const base64Data = rawText.split("||")[1];
                    const content = atob(base64Data);
                    const clean = fixLatex(cleanExportContent(content));
                    const base64Encoded = btoa(unescape(encodeURIComponent(clean)));
                    const url = "data:text/plain;base64," + base64Encoded;
                    addBotBubble(`
    📄 File đã xuất trước đó:

    👉 <a href="${url}" target="_blank">Mở lại</a>
    `);
                } else {
                    let text = rawText;
                    let exportType = null;
                    const expMatch = text.match(/(?:^|\n)\[EXPORT\]\s*(docx|doc|pdf|txt|md|html|xlsx)\s*$/i);
                    if (expMatch) {
                        exportType = expMatch[1].toLowerCase();
                        text = text.replace(/(?:^|\n)\[EXPORT\]\s*(docx|doc|pdf|txt|md|html|xlsx)\s*$/i, "").trim();
                    }
                    if (
                        isValidForExport(text) &&
                        !text.includes("📄 File đã xuất trước đó") &&
                        !text.includes("📄 File DOCX đã sẵn sàng") &&
                        !text.includes("📄 File đã sẵn sàng")
                    ) {
                        lastExportContent = text;
                        localStorage.setItem("lastExportContent", text);
                    }
                    addBotBubble(text, exportType);

                    // 🔥 RENDER SAVED VIDEO (SERIES)
                    if (Array.isArray(m.videos) && m.videos.length > 0) {
                        m.videos.forEach(vidUrl => {
                            const row = document.createElement("div");
                            row.className = "msgrow bot";
                            row.innerHTML = `
                                <div class="video-bubble-premium assistant">
                                    <div class="video-container-premium">
                                        <video controls class="video-player-premium">
                                            <source src="${vidUrl}" type="video/mp4">
                                            Your browser does not support the video tag.
                                        </video>
                                    </div>
                                    <div class="video-actions-premium">
                                        <span class="status-badge-premium">
                                            <i class='bx bx-magic-wand'></i> Btoan AI Video
                                        </span>
                                        <button onclick="forceDownloadVideo('${vidUrl}', 'btoan_ai_video_${Date.now()}.mp4', this)" class="download-btn-premium">
                                            <i class='bx bx-download'></i> <span>Tải Video</span>
                                        </button>
                                    </div>
                                </div>
                            `;
                            chatEl.appendChild(row);
                        });
                    } else if (m.video) {
                        const row = document.createElement("div");
                        row.className = "msgrow bot";
                        row.innerHTML = `
                            <div class="video-bubble-premium assistant">
                                <div class="video-container-premium">
                                    <video controls class="video-player-premium">
                                        <source src="${m.video}" type="video/mp4">
                                        Your browser does not support the video tag.
                                    </video>
                                </div>
                                <div class="video-actions-premium">
                                    <span class="status-badge-premium">
                                        <i class='bx bx-magic-wand'></i> Btoan AI Video
                                    </span>
                                    <button onclick="forceDownloadVideo('${m.video}', 'btoan_ai_video_${Date.now()}.mp4', this)" class="download-btn-premium">
                                        <i class='bx bx-download'></i> <span>Tải Video</span>
                                    </button>
                                </div>
                            </div>
                        `;
                        chatEl.appendChild(row);
                    }

                    if (Array.isArray(m.images) && m.images.length > 0) {
                        const row = document.createElement("div");
                        row.className = "msgrow bot";
                        const bubble = document.createElement("div");
                        bubble.className = "bubble image-bubble-premium";
                        const imageSrc = m.images[0];
                        bubble.innerHTML = `
                            <div class="image-wrapper-premium">
                                <img src="${imageSrc}" alt="Generated image" onclick="viewImage('${imageSrc}')">
                                <button onclick="forceDownloadVideo('${imageSrc}', 'btoan_ai_image_${Date.now()}.png', this)" class="image-download-btn-premium" title="Tải ảnh về máy">
                                    <i class="bi bi-download"></i>
                                </button>
                            </div>
                            <div class="image-caption-premium">Đây là ảnh của bạn</div>
                        `;
                        row.appendChild(bubble);
                        chatEl.appendChild(row);
                    }

                    if (m.music_url) {
                        addMusicBubble({
                            music_url: m.music_url,
                            music_prompt: m.music_prompt,
                            music_duration: m.music_duration
                        });
                    }
                }
                chatHistoryBuffer.push({
                    role: "model",
                    parts: [{ text: rawText }]
                });
            }
        });
        scrollToBottom();
    } catch (e) {
        if (signal && signal.aborted) return;
        console.error("LOAD CONV ERROR:", e);
        chatEl.innerHTML = '<div class="p-4 text-center text-danger">⚠️ Không thể tải cuộc trò chuyện. Vui lòng thử lại.</div>';
    } finally {
        window._loadingConversationId = null;
        window.isLoadingConversation = false;
    }
}

function getLastBotMessageText() {
    const botMessages = [...document.querySelectorAll(".msgrow.bot .bubble")]
    if (!botMessages.length) return ""

    for (let i = botMessages.length - 1; i >= 0; i--) {
        const content = botMessages[i].querySelector(".message-content")
        if (!content) continue

        const text = (content.innerText || "").trim()

        if (!text) continue
        if (text.includes("đang trả lời")) continue
        if (text.includes("❌")) continue
        if (text.includes("📄 File DOCX đã sẵn sàng")) continue
        if (text.includes("📄 File đã sẵn sàng")) continue

        return text
    }

    return ""
}

// ====== PAGE LOAD ======
document.addEventListener("paste", async (e) => {

    const items = e.clipboardData.items

    for (const item of items) {
        if (item.type.indexOf("image") !== -1) {

            e.preventDefault()

            const file = item.getAsFile()

            // ✅ dùng hàm chung
            addImages([file])

            return
        }
    }

})




window.onload = function () {

    chatEl = document.getElementById("chat") || null;
    msgEl = document.getElementById("msg") || null;

    chatEl = document.getElementById("chat");
    statusEl = document.getElementById("status");
    msgEl = document.getElementById("msg");


    lastExportContent = localStorage.getItem("lastExportContent") || ""

    // 🔥 đặt SAU khi có msgEl
    if (msgEl) {

        msgEl.addEventListener("focus", () => {

            // ❌ CHẶN MOBILE
            if (window.innerWidth <= 768) return

            setTimeout(() => {
                msgEl.scrollIntoView({ block: "center" });
            }, 300)

        })




        // auto resize
        msgEl.addEventListener("input", () => {
            msgEl.style.height = "auto";
            msgEl.style.height = msgEl.scrollHeight + "px";
        })

    }
    sendBtn = document.getElementById("send");

    // Routing Logic: Tự động nạp hội thoại khi mở trực tiếp hoặc F5 tại URL /c/{id}
    const urlParams = new URLSearchParams(window.location.search);
    const cidFromQuery = urlParams.get('c');
    const pathMatch = window.location.pathname.match(/\/c\/([0-9]+)/);
    const cidToLoad = (pathMatch ? pathMatch[1] : null) || cidFromQuery || (window.SERVER_CONVERSATION_ID ? window.SERVER_CONVERSATION_ID.trim() : null);

    if (cidToLoad) {
        console.log("🚀 BtoanAI: Restoring conversation", cidToLoad);
        const welcomeEl = document.getElementById("welcome-screen");
        if (welcomeEl) welcomeEl.style.display = "none";
        const hubEl = document.getElementById("learning-hub-screen");
        if (hubEl) hubEl.style.display = "none";
        if (chatEl) chatEl.style.display = "block";
        loadConversation(cidToLoad, false);
    } else if (window.location.pathname === "/" || window.location.pathname.endsWith("index.php") || window.location.pathname === "") {
        console.log("🚀 BtoanAI: Starting new chat");
        newChat(false);
    }

    window.addEventListener('popstate', (e) => {
        const popCid = e.state?.cid || e.state?.conversationId || (window.location.pathname.match(/\/c\/([0-9]+)/)?.[1]);
        if (popCid) {
            loadConversation(popCid, false);
        } else {
            newChat(false);
        }
    });

    if (chatEl) {
        chatEl.addEventListener("dragover", (e) => {
            e.preventDefault();
        });

        chatEl.addEventListener("drop", (e) => {
            e.preventDefault();
            // ✅ chỉ 1 dòng
            addImages(e.dataTransfer.files);
        });
    }

    attachments = document.getElementById("attachments");
    // camera preview
    document.getElementById("fileCam")?.addEventListener("change", (e) => {
        addImages(e.target.files)
    })

    document.getElementById("send")?.addEventListener("click", send)

    msgEl?.addEventListener("keydown", (e) => {
        if (e.key === "Enter" && !e.shiftKey) {
            // Nếu là mobile, Enter chỉ là xuống dòng, không gửi
            if (isMobile()) return;

            e.preventDefault()
            send()
        }
    })

    // 🖱️ SCROLL TO BOTTOM BUTTON LOGIC
    const scrollBtn = document.getElementById("scrollToBottomBtn");
    if (chatEl && scrollBtn) {
        chatEl.addEventListener("scroll", () => {
            // Hiển thị nút nếu cách đáy hơn 300px
            const threshold = 300;
            const isAtBottom = (chatEl.scrollHeight - chatEl.scrollTop - chatEl.clientHeight) < threshold;

            if (isAtBottom) {
                if (scrollBtn.classList.contains("show")) {
                    scrollBtn.classList.remove("show");
                    setTimeout(() => {
                        if (!scrollBtn.classList.contains("show")) scrollBtn.style.display = "none";
                    }, 400);
                }
            } else {
                if (!scrollBtn.classList.contains("show")) {
                    scrollBtn.style.display = "flex";
                    // Small delay to trigger CSS transition
                    setTimeout(() => scrollBtn.classList.add("show"), 10);
                }
            }
        });

        scrollBtn.addEventListener("click", () => {
            chatEl.scrollTo({
                top: chatEl.scrollHeight,
                behavior: "smooth"
            });
        });
    }

    // ===== auto format dd/mm/yyyy =====
    const dobInput = document.getElementById("pf_dob")

    if (dobInput) {
        dobInput.addEventListener("input", (e) => {
            let v = e.target.value.replace(/\D/g, "")

            if (v.length >= 2) v = v.slice(0, 2) + "/" + v.slice(2)
            if (v.length >= 5) v = v.slice(0, 5) + "/" + v.slice(5, 9)

            e.target.value = v
        })
    }
    const avatarInput = document.getElementById("avatarInput")

    if (avatarInput) {

        avatarInput.addEventListener("change", async function () {

            const file = this.files[0]
            if (!file) return

            console.log("🔥 FILE SELECTED:", file)

            const formData = new FormData()
            formData.append("avatar", file)
            formData.append("user_id", localStorage.getItem("user_id"))

            try {

                const res = await fetch("/ai/api/upload_avatar.php", {
                    method: "POST",
                    body: formData
                })

                const text = await res.text()
                console.log("🔥 RAW RESPONSE:", text)

                let data
                try {
                    data = JSON.parse(text)
                } catch {
                    alert("API lỗi không phải JSON")
                    return
                }

                console.log("🔥 PARSED:", data)

                if (!data.success) {
                    alert("Upload thất bại")
                    return
                }

                const url = data.avatar + "?t=" + Date.now()

                document.getElementById("avatarPreview").innerHTML =
                    `<img src="${url}" style="width:100%;height:100%;border-radius:50%">`

                document.getElementById("userAvatar").innerHTML =
                    `<img src="${url}" style="width:100%;height:100%;border-radius:50%">`

            } catch (e) {
                console.error("UPLOAD ERROR:", e)
                alert("Server lỗi")
            }

        })

    }

}
function toggleSidebar() {
    const sb = document.getElementById("sidebar")
    if (!sb) return

    if (isMobile()) {
        sb.classList.toggle("open")
    } else {
        sb.classList.toggle("collapsed")
        document.body.classList.toggle("sidebar-collapsed")
    }
}




document.getElementById("pickImage")?.addEventListener("click", () => {
    document.getElementById("fileImg")?.click();
})

document.getElementById("pickCamera")?.addEventListener("click", async () => {

    // nếu đang chạy trong Flutter
    if (window.flutter_inappwebview) {

        const base64 = await window.flutter_inappwebview.callHandler('openCamera')

        if (base64) {

            const blob = dataURLToBlob(base64)

            const file = new File([blob], "camera.jpg", { type: "image/jpeg" })

            addImages([file])
        }


    } else {
        // fallback cho trình duyệt
        document.getElementById("fileCam")?.click();
    }

})

document.getElementById("pickFile")?.addEventListener("click", () => {
    document.getElementById("fileDoc")?.click();
})


document.getElementById("fileImg")?.addEventListener("change", (e) => {
    addImages(e.target.files)
})

document.getElementById("fileDoc")?.addEventListener("change", (e) => {

    Array.from(e.target.files).forEach(file => {

        const div = document.createElement("div")
        div.className = "atchip"

        div.innerHTML = `
    📄 ${file.name}
    <button class="atremove">×</button>
    `

        div.querySelector(".atremove").onclick = () => {
            div.remove()
        }

        attachments.appendChild(div)

    })

})
function newChat(updateUrl = true) {
    autoRenamed = false
    lastAIAnswer = ""

    window._isViewingLearningHub = false;
    const hub = document.getElementById("learning-hub-screen");
    if (hub) hub.style.display = "none";
    document.querySelectorAll(".sidebar-menu-nav .nav-item").forEach(el => el.classList.remove("active"));

    conversation_id = null
    if (updateUrl) {
        window.history.pushState(null, "", "/");
    }
    updateSidebarActive(null) // Clear active sidebar state

    chatHistoryBuffer = []

    lastExportContent = ""
    const chatEl = document.getElementById("chat")

    if (chatEl) {
        chatEl.innerHTML = ""
    }
    showWelcomeScreen(true);
}

document.addEventListener("click", function (e) {

    const sidebar = document.getElementById("sidebar")
    const menuBtn = document.querySelector(".menu-btn")

    // đóng sidebar
    if (sidebar && sidebar.classList.contains("open")) {
        if (!sidebar.contains(e.target) && !menuBtn.contains(e.target)) {
            sidebar.classList.remove("open")
        }
    }



})


function scrollToBottom() {
    const scrollContainer = document.getElementById("chatScrollWrapper") || chatEl;
    if (scrollContainer) {
        scrollContainer.scrollTop = scrollContainer.scrollHeight;
        triggerKaTeX(scrollContainer);
    }
}
window.visualViewport?.addEventListener("resize", () => {

    // 🔥 CHẶN nếu đang focus input
    if (document.activeElement !== msgEl) {
        setTimeout(() => {
            scrollToBottom()
        }, 200)
    }

})
function showRegister() {

    document.getElementById("loginForm").style.display = "none"
    document.getElementById("registerForm").style.display = "block"

}

function showLogin() {

    document.getElementById("loginForm").style.display = "block"
    document.getElementById("registerForm").style.display = "none"

}

function togglePassword(id, el) {

    const input = document.getElementById(id);

    if (input.type === "password") {
        input.type = "text";
        el.classList.remove("bi-eye");
        el.classList.add("bi-eye-slash");
    } else {
        input.type = "password";
        el.classList.remove("bi-eye-slash");
        el.classList.add("bi-eye");
    }

}
document.addEventListener("visibilitychange", () => {

    if (!document.hidden) {
        scrollToBottom()
    }

})
async function runSearch(query, originalText) {

    const loading = document.createElement("div")
    loading.className = "msgrow bot"
    loading.innerHTML = `<div class="bubble">🔎 đang tìm kiếm...</div>`
    chatEl.appendChild(loading)

    try {

        const res = await fetch("/ai/api/search.php?q=" + encodeURIComponent(query))
        const data = await res.json()

        let text = ""

        data.results.forEach(r => {
            text += `- ${r.title}\n${r.snippet}\n\n`
        })

        // 🔥 xoá loading
        loading.remove()

        const reply = await callGemini(
            `Câu hỏi: ${originalText}

    Dựa vào thông tin sau:
    ${text}

    Hãy trả lời.`,
            [], []
        )
        lastAIAnswer = reply
        streamBot(reply, null)

    } catch (e) {

        loading.remove()
        addBotBubble("❌ lỗi search")

    }
}
async function exportFile(content) {

    if (!content.trim()) {
        addBotBubble("❌ chưa có nội dung")
        return
    }

    localStorage.setItem("lastExportContent", content)

    // 🔥 gọi API upload
    const res = await fetch("/ai/api/upload.php", {
        method: "POST",
        headers: {
            "Content-Type": "application/json"
        },
        body: JSON.stringify({
            content: content,
            ext: "txt"
        })
    })

    const text = await res.text()

    let data
    try {
        data = JSON.parse(text)
    } catch {
        console.log("SERVER ERROR:", text)
        addBotBubble("❌ server lỗi")
        return
    }

    if (!data.url) {
        addBotBubble("❌ upload lỗi")
        return
    }

    // 🔥 render link chuẩn
    addBotBubble(`
    <div class="d-flex align-items-center gap-3 p-2 border rounded-3 bg-light">
        <i class="bi bi-file-earmark-text text-primary fs-3"></i>
        <div class="flex-grow-1">
            <div class="fw-bold">Tài liệu văn bản đã sẵn sàng</div>
            <small class="text-muted">Định dạng UTF-8 (.txt) tương thích mọi thiết bị</small>
        </div>
        <a href="${data.url}" download class="btn btn-primary btn-sm rounded-pill px-3">
            <i class="bi bi-download me-1"></i> Tải về
        </a>
    </div>
    `)
}
async function exportDOCX(content) {

    if (!content.trim()) {
        addBotBubble("❌ chưa có nội dung")
        return
    }

    localStorage.setItem("lastExportContent", content)

    const res = await fetch("/ai/api/upload.php", {
        method: "POST",
        headers: {
            "Content-Type": "application/json"
        },
        body: JSON.stringify({
            content: content,
            ext: "docx"
        })
    })

    const text = await res.text()

    let data
    try {
        data = JSON.parse(text)
    } catch {
        console.log("SERVER ERROR:", text)
        addBotBubble("❌ server lỗi")
        return
    }

    if (!data.url) {
        addBotBubble("❌ upload lỗi")
        return
    }

    const row = document.createElement("div")
    row.className = "msgrow bot"

    const bubble = document.createElement("div")
    bubble.className = "bubble"

    bubble.innerHTML = `
        <div class="message-content">
            <div class="d-flex align-items-center gap-3 p-2 border rounded-3 bg-light">
                <i class="bi bi-file-earmark-word text-primary fs-3"></i>
                <div class="flex-grow-1">
                    <div class="fw-bold">Tài liệu Word đã sẵn sàng</div>
                    <small class="text-muted">Định dạng Microsoft Word (.doc) chuẩn layout</small>
                </div>
                <a href="${data.url}" download class="btn btn-primary btn-sm rounded-pill px-3">
                    <i class="bi bi-download me-1"></i> Tải file Word
                </a>
            </div>
        </div>
    `
    row.appendChild(bubble)
    chatEl.appendChild(row)
    scrollToBottom()
}

async function exportPDF(content) {

    if (!content.trim()) {
        addBotBubble("❌ chưa có nội dung")
        return
    }

    localStorage.setItem("lastExportContent", content)

    const res = await fetch("/ai/api/upload.php", {
        method: "POST",
        headers: {
            "Content-Type": "application/json"
        },
        body: JSON.stringify({
            content: content,
            ext: "pdf"
        })
    })

    const text = await res.text()

    let data
    try {
        data = JSON.parse(text)
    } catch {
        console.log("SERVER ERROR:", text)
        addBotBubble("❌ server lỗi")
        return
    }

    if (!data.url) {
        addBotBubble("❌ upload lỗi")
        return
    }

    const row = document.createElement("div")
    row.className = "msgrow bot"

    const bubble = document.createElement("div")
    bubble.className = "bubble"

    bubble.innerHTML = `
        <div class="message-content">
            <div>📄 File PDF đã sẵn sàng:</div>
            <div class="mt-2">
                <a href="${data.url}" target="_blank">👉 Tải file PDF</a>
            </div>
        </div>
    `

    row.appendChild(bubble)
    chatEl.appendChild(row)
    scrollToBottom()

}


function extractExportContent(text) {
    let clean = String(text || "")
        .replace(/\r\n/g, "\n")
        .replace(/\r/g, "\n")
        .trim()

    const lines = clean.split("\n").map(x => x.trim())

    const filtered = lines.filter(line => {
        if (!line) return true

        const lower = line.toLowerCase()

        if (lower.startsWith("tuyệt vời")) return false
        if (lower.startsWith("đây là nội dung")) return false
        if (lower.startsWith("dưới đây là")) return false
        if (lower.startsWith("tôi sẽ xuất")) return false
        if (lower.startsWith("tôi hiểu rằng")) return false
        if (lower.startsWith("chào bạn")) return false
        if (lower.includes("thành file word cho bạn")) return false
        if (lower.includes("nội dung đầy đủ mà bạn yêu cầu")) return false

        return true
    })

    return filtered.join("\n").trim()
}

async function searchHistory(q) {

    const res = await fetch("/ai/api/search_history.php?q=" + encodeURIComponent(q) + "&user_id=" + localStorage.getItem("user_id"))

    const data = await res.json()

    const list = document.getElementById("historyList")
    list.innerHTML = ""

    data.forEach(c => {
        const div = document.createElement("div")
        div.innerText = c.title
        div.onclick = () => loadConversation(c.id)
        list.appendChild(div)
    })
}
// Pull-to-refresh logic removed to prevent unintended reloads on mobile
function fixLatex(text) {

    return text
        .replace(/\$(.*?)\$/g, "$1") // bỏ dấu $
        .replace(/\\times/g, "×")
        .replace(/\\cdot/g, "·")
        .replace(/\\frac{(.*?)}{(.*?)}/g, "($1/$2)")
        .replace(/\\text{(.*?)}/g, "$1")
        .replace(/\\sqrt{(.*?)}/g, "√($1)")
        .replace(/\\left|\\right/g, "")
        .replace(/\\,/g, "")
        .replace(/\\\\/g, "\n")

}
function isValidForExport(text) {
    if (!text || typeof text !== "string") return false;
    const trimmed = text.trim();
    if (trimmed.length < 20) return false;

    // Chỉ chặn nếu là câu chào hỏi, đồng ý ngắn cụt lủn
    if (/^(ủa|ok|ừ|ờ|hello|hi|chào bạn|cảm ơn|dạ|vâng)[\s\.,!?]*$/i.test(trimmed)) return false;

    // Chặn nếu là câu hỏi ngược lại người dùng muốn làm gì
    if (trimmed.length < 150 && /bạn muốn.*(gì|nhỉ\?)/i.test(trimmed) && trimmed.includes("?")) return false;

    return true;
}
function cleanExportContent(text) {
    return String(text || "")
        .replace(/\r\n/g, "\n")
        .replace(/\r/g, "\n")
        .replace(/👉|📄|🔎/g, "")
        .trim()
}
async function checkProfile(user_id) {
    if (!user_id || user_id === "undefined" || user_id === "0") {
        setupGuestUI();
        return;
    }

    try {
        const res = await fetch("/ai/api/check_profile.php?user_id=" + user_id);
        const data = await res.json();

        const fullName = data.full_name?.trim()

        const name =
            fullName ||
            localStorage.getItem("full_name") ||
            localStorage.getItem("username") ||
            "User"

        if (fullName) {
            localStorage.setItem("full_name", fullName);
        }

        // ✅ set tên
        document.getElementById("userName").innerText = name
        updateWelcomeGreeting(name);

        // ✅ avatar chữ cái đầu
        const avatarEl = document.getElementById("userAvatar")

        // reset
        avatarEl.innerHTML = ""
        avatarEl.style.background = "none"

        if (data.avatar) {

            const url = data.avatar + "?t=" + Date.now()

            avatarEl.innerHTML = `
                <img src="${url}" 
                    style="width:100%;height:100%;border-radius:50%">
            `

        } else {

            avatarEl.innerText = name.charAt(0).toUpperCase()

            const colors = [
                "linear-gradient(135deg,#6366f1,#4f46e5)",
                "linear-gradient(135deg,#22c55e,#16a34a)",
                "linear-gradient(135deg,#f59e0b,#d97706)",
                "linear-gradient(135deg,#ef4444,#dc2626)"
            ]

            avatarEl.style.background = colors[name.length % colors.length]
        }

        // 🎨 màu avatar
        const colors = [
            "linear-gradient(135deg,#6366f1,#4f46e5)",
            "linear-gradient(135deg,#22c55e,#16a34a)",
            "linear-gradient(135deg,#f59e0b,#d97706)",
            "linear-gradient(135deg,#ef4444,#dc2626)"
        ]
        avatarEl.style.background = colors[name.length % colors.length]

        // Đồng bộ thông tin user vào Card dưới cùng của Sidebar
        const sidebarNameEl = document.getElementById("sidebarUserName");
        if (sidebarNameEl) sidebarNameEl.innerText = name;
        const sidebarRoleEl = document.getElementById("sidebarUserRole");
        if (sidebarRoleEl) sidebarRoleEl.innerText = data.role || "Developer / Member";
        const sidebarAvatarEl = document.getElementById("sidebarUserAvatar");
        if (sidebarAvatarEl) {
            if (data.avatar) {
                sidebarAvatarEl.innerHTML = `<img src="${data.avatar}?t=${Date.now()}" style="width:100%;height:100%;border-radius:50%;object-fit:cover;">`;
            } else {
                sidebarAvatarEl.innerText = name.charAt(0).toUpperCase();
                sidebarAvatarEl.style.background = colors[name.length % colors.length];
            }
        }

        // 💾 lưu lại full_name để dùng sau
        if (fullName) {
            localStorage.setItem("full_name", fullName)
        }

        // =========================
        // 🔥 LOGIC MỚI (QUAN TRỌNG)
        // =========================

        // 👉 LUÔN show chat (không dùng profileBox nữa)
        document.getElementById("chatApp").style.display = "flex"

        // 👉 Không bao giờ tự động bật popup Profile làm phiền người dùng
        if (!data.has_profile) {
            localStorage.setItem("has_profile", "0");
        } else {
            localStorage.setItem("has_profile", "1");
        }
    } catch (e) {
        console.warn("Failed to check profile:", e);
    }
}
async function saveProfile() {

    const user_id = localStorage.getItem("user_id")

    if (!user_id) {
        alert("Lỗi: chưa đăng nhập")
        return
    }

    const nameInput = document.getElementById("pf_name")
    const dobInput = document.getElementById("pf_dob")
    const msg = document.getElementById("profileMsg")

    if (!nameInput || !dobInput || !msg) return

    const full_name = nameInput.value.trim()
    let dob = dobInput.value.trim()

    console.log("📌 INPUT:", { full_name, dob })

    // ===== validate =====
    if (!full_name || !dob) {
        msg.innerText = "Vui lòng nhập đầy đủ"
        return
    }

    // ===== validate dd/mm/yyyy =====
    if (!/^\d{2}\/\d{2}\/\d{4}$/.test(dob)) {
        msg.innerText = "Ngày sinh phải dạng dd/mm/yyyy"
        return
    }

    const [day, month, year] = dob.split("/").map(Number)

    if (
        day < 1 || day > 31 ||
        month < 1 || month > 12 ||
        year < 1900 || year > 2100
    ) {
        msg.innerText = "Ngày không hợp lệ"
        return
    }

    // ===== convert sang yyyy-mm-dd =====
    dob = `${year}-${String(month).padStart(2, "0")}-${String(day).padStart(2, "0")}`

    try {

        const res = await fetch("/ai/api/update_name.php", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
                user_id,
                full_name,
                dob
            })
        })

        const data = await res.json()
        console.log("🔥 RESPONSE:", data)

        if (!data.success) {
            msg.innerText = "Lưu thất bại"
            return
        }

        // ===== SAVE LOCAL =====
        localStorage.setItem("full_name", full_name)
        localStorage.setItem("has_profile", "1")

        // ===== UPDATE UI NGAY =====
        const userName = document.getElementById("userName")
        const avatar = document.getElementById("userAvatar")

        if (userName) userName.innerText = full_name
        if (avatar) avatar.innerText = full_name.charAt(0).toUpperCase()

        const sidebarNameEl = document.getElementById("sidebarUserName");
        if (sidebarNameEl) sidebarNameEl.innerText = full_name;
        const sidebarAvatarEl = document.getElementById("sidebarUserAvatar");
        if (sidebarAvatarEl) sidebarAvatarEl.innerText = full_name.charAt(0).toUpperCase();

        // ===== ĐÓNG MODAL =====
        const modal = document.getElementById("profileModal")
        if (modal) modal.style.display = "none"

        // ===== SYNC LẠI TOÀN BỘ =====
        checkProfile(user_id)

    } catch (err) {
        console.error("❌ saveProfile error:", err)
        msg.innerText = "Server lỗi"
    }
}
function isMobile() {
    return window.innerWidth <= 768;
}

// ❌ chặn mọi auto focus
document.addEventListener("focusin", (e) => {
    if (isMobile() && e.target === msgEl) {
        if (!e.isTrusted) {
            msgEl.blur();
        }
    }
});
function handleGoogleLogin(response) {
    const id_token = response.credential;

    fetch("/ai/api/google-login.php", {
        method: "POST",
        headers: {
            "Content-Type": "application/json"
        },
        body: JSON.stringify({ token: id_token })
    })
        .then(res => res.json())
        .then(data => {
            if (data.success) {

                const user_id = data.user.id;

                // ✅ lưu đúng hệ thống bạn đang dùng
                localStorage.setItem("user_id", user_id);
                localStorage.setItem("username", data.user.username);
                // ✅ ẩn login
                const lBox = document.getElementById("loginBox");
                if (lBox) lBox.style.display = "none";
                if (document.getElementById("userBox")) document.getElementById("userBox").style.display = "flex";
                if (document.getElementById("guestLoginBox")) document.getElementById("guestLoginBox").style.display = "none";

                // ✅ auto fill tên
                const pfName = document.getElementById("pf_name");
                if (pfName) pfName.value = data.user.name || "";

                // ✅ check profile NGAY & load history
                checkProfile(user_id);
                loadHistory();

            } else {
                alert("Login Google thất bại");
            }
        });
}

document.addEventListener("DOMContentLoaded", () => {
    // Luôn luôn hiển thị giao diện Chat ngay lập tức (không ép login)
    const chatApp = document.getElementById("chatApp");
    if (chatApp) chatApp.style.display = "flex";
    const loginBox = document.getElementById("loginBox");
    if (loginBox) loginBox.style.display = "none";

    // Đồng bộ tức thì thông tin từ localStorage vào Sidebar & Header để không bị nháy chữ 'User'
    const cachedName = localStorage.getItem("full_name") || localStorage.getItem("username");
    if (cachedName) {
        const sName = document.getElementById("sidebarUserName");
        if (sName) sName.innerText = cachedName;
        const sAvatar = document.getElementById("sidebarUserAvatar");
        if (sAvatar) sAvatar.innerText = cachedName.charAt(0).toUpperCase();
        const uName = document.getElementById("userName");
        if (uName) uName.innerText = cachedName;
        const uAvatar = document.getElementById("userAvatar");
        if (uAvatar) uAvatar.innerText = cachedName.charAt(0).toUpperCase();
        updateWelcomeGreeting(cachedName);
    }

    const uid = localStorage.getItem("user_id");

    // Check if URL has conversation ID path e.g. /c/30002
    const pathMatch = window.location.pathname.match(/\/c\/(\d+)/);
    const urlCid = pathMatch ? parseInt(pathMatch[1]) : null;

    if (uid && uid !== "undefined" && uid !== "0") {
        if (document.getElementById("userBox")) document.getElementById("userBox").style.display = "flex";
        if (document.getElementById("guestLoginBox")) document.getElementById("guestLoginBox").style.display = "none";
        checkProfile(uid);
        loadHistory().then(() => {
            if (urlCid && conversation_id !== urlCid && !window._loadingConversationId) {
                loadConversation(urlCid, false);
            }
        });
    } else {
        if (document.getElementById("userBox")) document.getElementById("userBox").style.display = "none";
        if (document.getElementById("guestLoginBox")) document.getElementById("guestLoginBox").style.display = "flex";
        setupGuestUI();
    }
});

function openLoginModal() {
    const box = document.getElementById("loginBox");
    if (box) box.style.display = "flex";
}

if (typeof window !== "undefined" && window.location.search.includes("login=1")) {
    setTimeout(openLoginModal, 300);
}

function closeLoginModal() {
    const box = document.getElementById("loginBox");
    if (box) box.style.display = "none";
}

function handleUserCardClick() {
    const uid = localStorage.getItem("user_id");
    if (uid && uid !== "undefined" && uid !== "0") {
        openProfile();
    } else {
        openLoginModal();
    }
}

function handleSidebarAction() {
    const uid = localStorage.getItem("user_id");
    if (uid && uid !== "undefined" && uid !== "0") {
        logout();
    } else {
        openLoginModal();
    }
}

function setupGuestUI() {
    const userNameEl = document.getElementById("userName");
    if (userNameEl) userNameEl.innerText = "Khách";
    const sidebarNameEl = document.getElementById("sidebarUserName");
    if (sidebarNameEl) sidebarNameEl.innerText = "Khách";
    const sidebarRoleEl = document.getElementById("sidebarUserRole");
    if (sidebarRoleEl) sidebarRoleEl.innerText = "Chưa đăng nhập";
    const avatarEl = document.getElementById("userAvatar");
    if (avatarEl) {
        avatarEl.innerHTML = "G";
        avatarEl.style.background = "linear-gradient(135deg, #64748b, #475569)";
        avatarEl.style.color = "#fff";
    }
    const sidebarAvatarEl = document.getElementById("sidebarUserAvatar");
    if (sidebarAvatarEl) {
        sidebarAvatarEl.innerHTML = "G";
        sidebarAvatarEl.style.background = "linear-gradient(135deg, #64748b, #475569)";
        sidebarAvatarEl.style.color = "#fff";
    }
    const logoutBtn = document.getElementById("sidebarLogoutBtn");
    if (logoutBtn) {
        logoutBtn.title = "Đăng nhập";
        logoutBtn.innerHTML = `<i class="bi bi-box-arrow-in-right fs-5 text-primary"></i>`;
    }
    const list = document.getElementById("historyList");
    if (list) {
        list.innerHTML = `
            <div class="px-3 py-4 text-center text-muted" style="font-size: 12px;">
                <i class="bi bi-clock-history d-block mb-2 text-indigo fs-3"></i>
                <div class="fw-semibold text-dark mb-1" style="color: var(--text-main) !important;">Chế độ Khách</div>
                <div class="text-muted mb-3">Đăng nhập để tự động lưu và đồng bộ lịch sử hội thoại.</div>
                <button type="button" onclick="openLoginModal()" class="btn btn-primary btn-sm rounded-pill px-3 py-1.5 extra-small fw-semibold shadow-sm">
                    <i class="bi bi-box-arrow-in-right me-1"></i> Đăng nhập ngay
                </button>
            </div>
        `;
    }
}

// 🌙 THEME TOGGLE (DARK / LIGHT MODE)
function initTheme() {
    const savedTheme = localStorage.getItem("appTheme") || "light";
    document.documentElement.setAttribute("data-theme", savedTheme);
    updateThemeIcon(savedTheme);
}

function toggleTheme() {
    const currentTheme = document.documentElement.getAttribute("data-theme") || "light";
    const newTheme = currentTheme === "dark" ? "light" : "dark";
    document.documentElement.setAttribute("data-theme", newTheme);
    localStorage.setItem("appTheme", newTheme);
    updateThemeIcon(newTheme);
}

function updateThemeIcon(theme) {
    const icon = document.getElementById("themeToggleIcon");
    const toggleBtn = document.getElementById("themeToggleBtn");
    if (icon) {
        if (theme === "dark") {
            icon.className = "bi bi-sun-fill fs-6";
        } else {
            icon.className = "bi bi-moon-stars fs-6";
        }
    }
    if (toggleBtn) {
        toggleBtn.title = theme === "dark" ? "Chuyển sang giao diện Sáng" : "Chuyển sang giao diện Tối";
    }
    const radioDark = document.querySelector('input[name="themeRadio"][value="dark"]');
    const radioLight = document.querySelector('input[name="themeRadio"][value="light"]');
    if (theme === "dark" && radioDark) radioDark.checked = true;
    if (theme === "light" && radioLight) radioLight.checked = true;
}

document.addEventListener("DOMContentLoaded", () => {
    initTheme();
});

function toggleUserMenu() {
    const menu = document.getElementById("userMenu")
    menu.style.display = menu.style.display === "block" ? "none" : "block"
}

// click ngoài → đóng
document.addEventListener("click", function (e) {
    const box = document.getElementById("userBox")
    if (!box) return

    if (!box.contains(e.target)) {
        document.getElementById("userMenu").style.display = "none"
    }
})

function openProfile(targetTab = 'profile') {
    const modal = document.getElementById("profileModal");
    if (!modal) {
        console.error("❌ profileModal not found");
        return;
    }
    modal.style.display = "flex";
    switchSettingsTab(targetTab);
    loadProfileData();
}

function closeProfile() {
    const modal = document.getElementById("profileModal");
    if (modal) modal.style.display = "none";
}

function switchSettingsTab(tabName) {
    const tabs = ['profile', 'ai', 'data', 'display'];
    tabs.forEach(t => {
        const btn = document.getElementById(`tabBtn-${t}`);
        const pane = document.getElementById(`tabPane-${t}`);
        if (btn) {
            if (t === tabName) {
                btn.className = "settings-nav-pill active btn btn-sm rounded-pill px-3 py-1.5 fw-semibold d-flex align-items-center gap-1.5";
            } else {
                btn.className = "settings-nav-pill btn btn-sm rounded-pill px-3 py-1.5 text-secondary fw-semibold d-flex align-items-center gap-1.5";
            }
        }
        if (pane) pane.style.display = (t === tabName) ? "block" : "none";
    });
}

function appendInstructionChip(text) {
    const ta = document.getElementById("ai_custom_instructions");
    if (!ta) return;
    if (ta.value.trim().length > 0) {
        ta.value = ta.value.trim() + "\n" + text;
    } else {
        ta.value = text;
    }
}

function applyLiveTheme(theme) {
    document.documentElement.setAttribute("data-theme", theme);
    localStorage.setItem("appTheme", theme);
    updateThemeIcon(theme);
}

function applyLiveFontSize(size) {
    document.documentElement.style.setProperty("--chat-font-size", `${size}px`);
    localStorage.setItem("chat_font_size", size);
}

async function loadProfileData() {
    const userId = localStorage.getItem("user_id");
    try {
        const res = await fetch("/api/account/settings");
        if (!res.ok) {
            if (userId) {
                const fbRes = await fetch("/ai/api/check_profile.php?user_id=" + userId);
                const fbData = await fbRes.json();
                if (document.getElementById("pf_name")) document.getElementById("pf_name").value = fbData.full_name || "";
            }
            return;
        }

        const data = await res.json();
        if (document.getElementById("pf_name")) document.getElementById("pf_name").value = data.full_name || "";
        if (document.getElementById("pf_email")) document.getElementById("pf_email").value = data.email || "";
        if (document.getElementById("pf_username")) document.getElementById("pf_username").value = data.username || "";
        if (document.getElementById("pf_dob")) document.getElementById("pf_dob").value = data.dob || "";
        if (document.getElementById("pf_bio")) document.getElementById("pf_bio").value = data.bio || "";

        // Avatar
        const avatarPreview = document.getElementById("avatarPreview");
        const fullName = data.full_name || data.username || "User";
        const initial = fullName.charAt(0).toUpperCase();
        if (avatarPreview) {
            if (data.avatar) {
                avatarPreview.innerHTML = `<img src="${data.avatar}?t=${Date.now()}" style="width:100%;height:100%;object-fit:cover;border-radius:50%">`;
            } else {
                avatarPreview.innerText = initial;
            }
        }
        if (document.getElementById("userName")) document.getElementById("userName").innerText = fullName;

        // Tab 2: AI Settings
        if (data.default_model === "btoan-2.5-pro") {
            const rPro = document.getElementById("radioModelPro");
            if (rPro) rPro.checked = true;
        } else {
            const rFlash = document.getElementById("radioModelFlash");
            if (rFlash) rFlash.checked = true;
        }

        if (document.getElementById("ai_custom_instructions")) {
            document.getElementById("ai_custom_instructions").value = data.custom_instructions || "";
        }
        if (data.temperature !== undefined && data.temperature !== null) {
            const tempEl = document.getElementById("ai_temperature");
            if (tempEl) tempEl.value = data.temperature;
            const tempLbl = document.getElementById("tempValueLabel");
            if (tempLbl) tempLbl.innerText = data.temperature;
        }

        // Tab 3: Data & Memory
        if (document.getElementById("ai_long_term_memory")) {
            document.getElementById("ai_long_term_memory").checked = data.long_term_memory !== false;
        }
        if (document.getElementById("ai_rag_enabled")) {
            document.getElementById("ai_rag_enabled").checked = data.rag_enabled !== false;
        }

        // Tab 4: Display & Voice
        if (data.theme === "light") {
            const rLight = document.querySelector('input[name="themeRadio"][value="light"]');
            if (rLight) rLight.checked = true;
        } else {
            const rDark = document.querySelector('input[name="themeRadio"][value="dark"]');
            if (rDark) rDark.checked = true;
        }

        if (data.chat_font_size) {
            const rf = document.querySelector(`input[name="fontSizeRadio"][value="${data.chat_font_size}"]`);
            if (rf) rf.checked = true;
        }

        if (data.tts_speed) {
            const ttsEl = document.getElementById("tts_speed");
            if (ttsEl) ttsEl.value = data.tts_speed;
            const ttsLbl = document.getElementById("ttsSpeedLabel");
            if (ttsLbl) ttsLbl.innerText = `${data.tts_speed}x`;
        }

    } catch (err) {
        console.error("❌ loadProfileData error:", err);
    }
}

async function saveAllSettings() {
    const statusEl = document.getElementById("settingsStatusMsg");
    if (statusEl) {
        statusEl.className = "small fw-semibold text-info";
        statusEl.innerText = "Đang lưu...";
    }

    const payload = {
        full_name: document.getElementById("pf_name")?.value || "",
        dob: document.getElementById("pf_dob")?.value || "",
        bio: document.getElementById("pf_bio")?.value || "",
        default_model: document.querySelector('input[name="defaultModelRadio"]:checked')?.value || "btoan-2.5-flash",
        custom_instructions: document.getElementById("ai_custom_instructions")?.value || "",
        temperature: parseFloat(document.getElementById("ai_temperature")?.value || "0.7"),
        long_term_memory: document.getElementById("ai_long_term_memory")?.checked !== false,
        rag_enabled: document.getElementById("ai_rag_enabled")?.checked !== false,
        theme: document.querySelector('input[name="themeRadio"]:checked')?.value || "dark",
        chat_font_size: parseInt(document.querySelector('input[name="fontSizeRadio"]:checked')?.value || "15", 10),
        tts_speed: parseFloat(document.getElementById("tts_speed")?.value || "1.0")
    };

    try {
        const res = await fetch("/api/account/settings", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(payload)
        });
        const data = await res.json();
        if (res.ok && (data.ok || data.success || data.status === "ok" || (data.message && !data.error))) {
            if (statusEl) {
                statusEl.className = "small fw-semibold text-success";
                statusEl.innerText = "✓ " + (data.message || "Đã lưu cài đặt thành công!");
            }
            if (payload.full_name) {
                if (document.getElementById("userName")) document.getElementById("userName").innerText = payload.full_name;
            }
            if (payload.theme) {
                applyLiveTheme(payload.theme);
            }
            if (typeof showToastNotification === "function") {
                showToastNotification("✓ Đã lưu cài đặt tài khoản thành công!");
            }
            setTimeout(() => { closeProfile(); }, 1200);
        } else {
            if (statusEl) {
                statusEl.className = "small fw-semibold text-danger";
                statusEl.innerText = "Lỗi: " + (data.message || data.error || "Không thể lưu");
            }
        }
    } catch (err) {
        if (statusEl) {
            statusEl.className = "small fw-semibold text-danger";
            statusEl.innerText = "Lỗi kết nối máy chủ";
        }
    }
}

async function submitChangePassword() {
    const oldP = document.getElementById("pwd_old")?.value || "";
    const newP = document.getElementById("pwd_new")?.value || "";
    const confirmP = document.getElementById("pwd_confirm")?.value || "";
    const msgEl = document.getElementById("pwdStatusMsg");

    if (!oldP || !newP) {
        if (msgEl) { msgEl.className = "extra-small text-danger mt-1"; msgEl.innerText = "Vui lòng nhập đầy đủ mật khẩu"; }
        return;
    }
    if (newP.length < 6) {
        if (msgEl) { msgEl.className = "extra-small text-danger mt-1"; msgEl.innerText = "Mật khẩu mới tối thiểu 6 ký tự"; }
        return;
    }
    if (newP !== confirmP) {
        if (msgEl) { msgEl.className = "extra-small text-danger mt-1"; msgEl.innerText = "Xác nhận mật khẩu không khớp"; }
        return;
    }

    try {
        const res = await fetch("/api/account/change-password", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ old_password: oldP, new_password: newP })
        });
        const data = await res.json();
        if (res.ok && data.success) {
            if (msgEl) {
                msgEl.className = "extra-small text-success mt-1";
                msgEl.innerText = "✓ " + data.message;
            }
            document.getElementById("pwd_old").value = "";
            document.getElementById("pwd_new").value = "";
            document.getElementById("pwd_confirm").value = "";
        } else {
            if (msgEl) {
                msgEl.className = "extra-small text-danger mt-1";
                msgEl.innerText = "Lỗi: " + (data.message || data.error || "Thất bại");
            }
        }
    } catch (err) {
        if (msgEl) { msgEl.className = "extra-small text-danger mt-1"; msgEl.innerText = "Lỗi kết nối máy chủ"; }
    }
}



async function confirmDeleteAllChats() {
    if (!confirm("CẢNH BÁO: Bạn có chắc chắn muốn xóa TOÀN BỘ các đoạn hội thoại và tin nhắn? Hành động này KHÔNG THỂ khôi phục!")) return;
    try {
        const res = await fetch("/api/chat/delete-all", { method: "POST" });
        const data = await res.json();
        if (res.ok && data.success) {
            alert("✓ " + data.message);
            const chatEl = document.getElementById("chat");
            if (chatEl) chatEl.innerHTML = "";
            const welcomeEl = document.getElementById("welcome-screen");
            if (welcomeEl) welcomeEl.style.display = "block";
            loadHistory();
            closeProfile();
        } else {
            alert("Lỗi: " + (data.message || data.error || "Không thể xóa"));
        }
    } catch (err) {
        alert("Lỗi kết nối máy chủ");
    }
}



// 📸 Image Preview Logic (Lightbox)

async function pollVideoStatus(job_id, containerBubble, chat_id) {
    let attempts = 0;
    const maxAttempts = 120; // 120 x 5s = 600 seconds (10 mins)
    const interval = 5000;
    let simulatedProgress = 0;
    let isInitialized = false;

    const timer = setInterval(async () => {
        attempts++;

        // 🛡️ CHẶN QUÁ TẢI
        if (attempts > maxAttempts) {
            clearInterval(timer);
            containerBubble.innerHTML = `<div class="bubble assistant border-danger text-danger"><i class='bx bx-time-five'></i> Quá thời gian tạo video. Vui lòng kiểm tra lại sau.</div>`;
            return;
        }

        // 🧠 GIẢ LẬP TIẾN ĐỘ THỰC TẾ (Tăng tốc theo yêu cầu người dùng)
        if (simulatedProgress < 95) {
            // Tốc độ nhảy: 8% mỗi 5s ban đầu, sau đó chậm dần nhưng vẫn nhanh hơn cũ
            const increment = simulatedProgress < 40 ? 8 : (simulatedProgress < 80 ? 3 : 1);
            simulatedProgress = Math.min(95, simulatedProgress + increment);
        }

        try {
            const res = await fetch(`/ai/api/check_video_job.php?job_id=${job_id}&conversation_id=${conversation_id}&user_id=${localStorage.getItem("user_id")}&chat_id=${chat_id}`);
            const data = await res.json();

            // 1️⃣ KHỞI TẠO UI LẦN ĐẦU
            if (!isInitialized) {
                containerBubble.innerHTML = `
                    <div class="bubble assistant video-processing-card">
                        <div class="processing-glow"></div>
                        <div class="d-flex align-items-center gap-3">
                            <div class="spinner-border text-primary spinner-border-sm" role="status"></div>
                            <div class="flex-grow-1">
                                <div class="fw-bold mb-1">Đợi chút nha, tui đang tạo video...</div>
                                <div class="progress" style="height: 6px; background: #f1f5f9; border-radius: 10px;">
                                    <div class="progress-bar progress-bar-striped progress-bar-animated" 
                                         role="progressbar" style="width: 0%; transition: width 0.5s ease-in-out;"></div>
                                </div>
                                <div class="text-muted mt-1 progress-text" style="font-size: 11px;">Tiến độ: 0% - Vui lòng đợi trong giây lát</div>
                            </div>
                        </div>
                    </div>
                `;
                isInitialized = true;
                scrollToBottom();
            }

            // 2️⃣ XỬ LÝ KHI HOÀN THÀNH
            if (data.success && data.status === 'completed') {
                clearInterval(timer);
                const videoUrl = data.video_url;

                // 🔥 ÉP NHẢY LÊN 100% VÀ ĐỔI MÀU THÀNH CÔNG
                const bar = containerBubble.querySelector('.progress-bar');
                const text = containerBubble.querySelector('.progress-text');
                if (bar) {
                    bar.style.width = '100%';
                    bar.classList.remove('progress-bar-animated');
                    bar.style.backgroundColor = '#22c55e'; // Màu xanh lá (Success)
                }
                if (text) {
                    text.innerHTML = '<span style="color: #22c55e; font-weight: bold;"><i class="bx bx-check-double"></i> Đã hoàn thành 100%! Đang tải video...</span>';
                }

                setTimeout(() => {
                    containerBubble.innerHTML = `
                        <div class="video-bubble-premium assistant">
                            <div class="video-container-premium">
                                <video controls class="video-player-premium">
                                    <source src="${videoUrl}" type="video/mp4">
                                </video>
                            </div>
                            <div class="video-actions-premium">
                                <span class="status-badge-premium">
                                    <i class='bx bx-magic-wand'></i> Btoan AI Video
                                </span>
                                <button onclick="forceDownloadVideo('${videoUrl}', 'btoan_ai_video_${Date.now()}.mp4', this)" class="download-btn-premium">
                                    <i class='bx bx-download'></i> <span>Tải Video</span>
                                </button>
                            </div>
                        </div>
                    `;
                    scrollToBottom();
                }, 1200);
                return;
            }

            // 3️⃣ CẬP NHẬT TIẾN ĐỘ ĐANG CHẠY (PROCESSING)
            if (data.status === 'processing' || data.success) {
                // Ưu tiên progress thật từ API nếu nó > 0
                const realProgress = parseFloat(data.progress || 0);
                const displayProgress = Math.max(simulatedProgress, realProgress);

                const bar = containerBubble.querySelector('.progress-bar');
                const text = containerBubble.querySelector('.progress-text');
                if (bar) bar.style.width = displayProgress + '%';
                if (text) text.innerText = `Tiến độ: ${Math.floor(displayProgress)}% - Vui lòng đợi trong giây lát`;
            }

            // 4️⃣ XỬ LÝ LỖI
            if (data.status === 'failed' || (data.success === false && data.status !== 'processing')) {
                clearInterval(timer);
                containerBubble.innerHTML = `<div class="bubble assistant border-danger text-danger"><i class='bx bx-error' ></i> Lỗi: ${data.error || "Không thể tạo video."}</div>`;
            }

        } catch (e) {
            console.error("Polling error:", e);
        }
    }, interval);
}

function viewImage(src) {
    const modal = document.getElementById("image-preview-modal");
    const img = document.getElementById("preview-img");
    if (!modal || !img) return;

    img.src = src;
    modal.style.display = "flex";
}

function closeImagePreview() {
    const modal = document.getElementById("image-preview-modal");
    if (modal) modal.style.display = "none";
}

// Close modal on Escape key
document.addEventListener('keydown', (e) => {
    if (e.key === "Escape") closeImagePreview();
});

// 📥 FORCE DOWNLOAD VIDEO (VIA PROXY TO BYPASS CORS)
async function forceDownloadVideo(url, filename, btn) {
    if (!url) return;

    const originalContent = btn.innerHTML;
    try {
        btn.disabled = true;
        btn.innerHTML = `<span class="spinner-border spinner-border-sm"></span> <span>Đang xử lý...</span>`;

        // Sử dụng proxy để né CORS và ép tải xuống chuẩn hơn
        const proxyUrl = `/ai/api/download_proxy.php?url=${encodeURIComponent(url)}&filename=${encodeURIComponent(filename)}`;

        // Cách 1: Fetch qua proxy (để giữ UI loading)
        const response = await fetch(proxyUrl);
        if (!response.ok) throw new Error("Proxy error");

        const blob = await response.blob();
        const blobUrl = window.URL.createObjectURL(blob);

        const a = document.createElement('a');
        a.style.display = 'none';
        a.href = blobUrl;
        a.download = filename;

        document.body.appendChild(a);
        a.click();

        window.URL.revokeObjectURL(blobUrl);
        document.body.removeChild(a);

        btn.innerHTML = `<i class='bx bx-check'></i> <span>Xong!</span>`;
    } catch (err) {
        console.error("❌ Download error:", err);
        btn.innerHTML = `<i class='bx bx-error'></i> <span>Lỗi rồi!</span>`;

        // Cách 2: Direct link qua proxy (fallback cuối cùng)
        const proxyUrl = `/ai/api/download_proxy.php?url=${encodeURIComponent(url)}&filename=${encodeURIComponent(filename)}`;
        window.location.href = proxyUrl;
    } finally {
        setTimeout(() => {
            btn.disabled = false;
            btn.innerHTML = originalContent;
        }, 3000);
    }
}
async function triggerMusicGeneration(prompt, loadingBubble, chatId) {
    try {
        const res = await fetch("/ai/api/generate_music.php", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
                prompt: prompt,
                conversation_id: conversation_id,
                user_id: localStorage.getItem("user_id"),
                chat_id: chatId,
                duration: 60
            })
        });

        const data = await res.json();
        if (loadingBubble) loadingBubble.remove();

        if (data.success) {
            addMusicBubble(data);
        } else {
            let errorMsg = data.error || "Lỗi không xác định";
            if (errorMsg.includes("429")) {
                errorMsg = "Hệ thống đang quá tải hoặc hết lượt tạo nhạc miễn phí (429 Quota Exceeded). Bạn vui lòng đợi khoảng 1-2 phút rồi thử lại nhé! Lyria AI có giới hạn lượt dùng khá chặt chẽ.";
            }
            addBotBubble("❌ Lỗi tạo nhạc: " + errorMsg);
        }
    } catch (e) {
        if (loadingBubble) loadingBubble.remove();
        addBotBubble("❌ Lỗi kết nối: " + e.message);
    }
}

function addMusicBubble(data) {
    const row = document.createElement("div");
    row.className = "msgrow bot";
    const promptText = (data.music_prompt || data.prompt || 'Giai điệu âm nhạc Btoan AI').replace(/"/g, '&quot;');
    row.innerHTML = `
        <div class="bubble music-card assistant shadow-sm">
            <div class="music-header d-flex align-items-center justify-content-between mb-2">
                <div class="d-flex align-items-center gap-2">
                    <i class="bi bi-music-note-beamed text-primary fs-5"></i>
                    <span class="fw-bold">Btoan AI Music Studio</span>
                </div>
                <span class="badge bg-primary-subtle text-primary border border-primary-subtle px-2 py-1">Hòa âm AI</span>
            </div>
            <div class="music-body my-2">
                <div class="music-prompt text-secondary small fst-italic">"${data.music_prompt || data.prompt || 'Nhạc AI'}"</div>
                <audio controls preload="metadata" src="${data.music_url || data.url}" class="w-100 mt-2 rounded"></audio>
                <div class="text-muted small mt-2" style="font-size: 11.5px;">
                    <i class="bi bi-check-circle-fill text-success me-1"></i> ${data.message || 'Giai điệu mẫu đã được tạo thành công.'}
                </div>
            </div>
            <div class="music-footer d-flex flex-wrap gap-2 pt-2 border-top mt-2">
                <button class="btn btn-outline-primary btn-sm rounded-pill px-3" onclick="speakLyrics(this)" title="Nghe đọc diễn cảm lời bài hát bằng giọng AI">
                    <i class="bi bi-volume-up me-1"></i> <span>Đọc lời hát</span>
                </button>
                <button class="btn btn-primary btn-sm rounded-pill px-3" onclick="openSunoWithPrompt('${promptText}')" title="Phối thành bản thu âm hoàn chỉnh có ca sĩ hát trên Suno AI">
                    <i class="bi bi-stars me-1"></i> <span>Tạo trên Suno AI</span>
                </button>
                <a href="${data.music_url || data.url}" download="btoan_melody.wav" class="btn btn-outline-secondary btn-sm rounded-pill px-3">
                    <i class="bi bi-download me-1"></i> Tải về
                </a>
                <button class="btn btn-light btn-sm rounded-pill px-2" onclick="copyMusicLink('${data.music_url || data.url}', this)">
                    <i class="bi bi-link-45deg"></i>
                </button>
            </div>
        </div>
    `;
    chatEl.appendChild(row);
    scrollToBottom();
}

function openSunoWithPrompt(prompt) {
    navigator.clipboard.writeText(prompt || "");
    alert("Đã sao chép phong cách và lời bài hát! Đang mở Suno AI để bạn dán vào tạo bản thu âm hoàn chỉnh có ca sĩ hát...");
    window.open("https://suno.com/create", "_blank");
}

function speakLyrics(btn) {
    if (!('speechSynthesis' in window)) {
        alert("Trình duyệt không hỗ trợ đọc giọng nói.");
        return;
    }
    if (window.speechSynthesis.speaking) {
        window.speechSynthesis.cancel();
        btn.innerHTML = `<i class="bi bi-volume-up me-1"></i> <span>Đọc lời hát</span>`;
        return;
    }
    const allMsgs = Array.from(document.querySelectorAll('.msgrow.bot .message-content')).reverse();
    let textToSpeak = "";
    for (const msg of allMsgs) {
        const t = msg.innerText;
        if (t.includes("Bài hát") || t.includes("Lời") || t.includes("Điệp khúc") || t.length > 50) {
            textToSpeak = t;
            break;
        }
    }
    if (!textToSpeak) textToSpeak = "Giai điệu âm nhạc vui tươi từ Btoan AI!";
    textToSpeak = textToSpeak.replace(/\[GENMUSIC\].*$/gis, "").replace(/\[.*?\]/g, "");

    const utterance = new SpeechSynthesisUtterance(textToSpeak);
    utterance.lang = "vi-VN";
    utterance.rate = 0.95;
    utterance.pitch = 1.05;

    utterance.onend = () => {
        btn.innerHTML = `<i class="bi bi-volume-up me-1"></i> <span>Đọc lời hát</span>`;
    };

    btn.innerHTML = `<i class="bi bi-stop-circle text-danger me-1"></i> <span class="text-danger">Dừng đọc</span>`;
    window.speechSynthesis.speak(utterance);
}

function copyMusicLink(url, btn) {
    navigator.clipboard.writeText(url);
    const originalText = btn.innerHTML;
    btn.innerHTML = `<i class="bi bi-check2 text-success"></i>`;
    setTimeout(() => {
        btn.innerHTML = originalText;
    }, 2000);
}


/* ==========================================================================
   🎓 BTOAN LEARNING HUB, INTERACTIVE QUIZ, 3D FLASHCARDS & SETTINGS MODAL
   ========================================================================== */

// --- Global Data Stores for Interactive Artifacts ---
window.quizBlocks = window.quizBlocks || {};
window.quizRegistry = window.quizRegistry || {};
window.flashcardBlocks = window.flashcardBlocks || {};
window.flashcardRegistry = window.flashcardRegistry || {};

// ====== KATEX FORMULA RENDERING HELPER ======
function triggerKaTeX(el) {
    if (!el && typeof document !== "undefined") {
        el = document.getElementById("chatScrollWrapper") || document.body;
    }
    if (el && typeof renderMathInElement === "function") {
        try {
            renderMathInElement(el, {
                delimiters: [
                    { left: "$$", right: "$$", display: true },
                    { left: "$", right: "$", display: false },
                    { left: "\\(", right: "\\)", display: false },
                    { left: "\\[", right: "\\]", display: true }
                ],
                throwOnError: false
            });
        } catch (e) {
            console.warn("KaTeX render error:", e);
        }
    }
    if (typeof MathJax !== "undefined" && MathJax.typesetPromise) {
        try {
            MathJax.typesetPromise(el ? [el] : undefined).catch(() => { });
        } catch (e) { }
    }
}

// Helper: Tự động xóa ký hiệu A., B., C., D. bị lặp ở đầu phương án (kể cả khi nằm trong $ ... $)
function stripOptionPrefix(rawVal) {
    if (!rawVal) return "";
    let str = String(rawVal).trim();
    // 1. Nếu bắt đầu bằng $ A. hoặc $A. hoặc $ (A) hoặc $(A)
    str = str.replace(/^(\s*\$\s*)[A-Da-d][\.\)\:\-]\s*/, '$1');
    str = str.replace(/^(\s*\$\s*)\([A-Da-d]\)\s*/, '$1');
    // 2. Nếu bắt đầu bằng A. hoặc A) hoặc (A) hoặc A: hoặc A - ngoài $
    str = str.replace(/^\s*\([A-Da-d]\)\s*/, '');
    str = str.replace(/^\s*[A-Da-d][\.\)\:\-]\s*/, '');
    // 3. Nếu có tiền tố số thứ tự "1. A. " hoặc "Câu 1: A. "
    str = str.replace(/^(\d+\s*[\.\)\:\-]\s*)?([A-Da-d]\s*[\.\)\:\-]\s*)?/i, '');
    str = str.replace(/^[0-9A-Za-z][\.\)\:\-]\s*/, '');
    return str.trim();
}

// Helper: Chuẩn hóa và sửa triệt để lỗi vỡ công thức LaTeX (frac12, intcos, \sin, ...)
function normalizeMathLatex(text) {
    if (!text) return "";
    let str = String(text);

    // 1. Phục hồi phân số bị dính số hoặc mất ngoặc nhọn: frac12 -> \frac{1}{2}, frac13 -> \frac{1}{3}, \frac12 -> \frac{1}{2}
    str = str.replace(/(^|[^\\a-zA-Z])frac(\d)(\d)(?!\d)/g, '$1\\frac{$2}{$3}');
    str = str.replace(/(^|[^\\a-zA-Z])frac\{/g, '$1\\frac{');
    str = str.replace(/\\?frac(\d)(\d)(?!\d)/g, "\\frac{$1}{$2}");

    // 2. Khôi phục dấu \ cho các toán tử và hàm toán học phổ biến nếu bị mất
    str = str.replace(/(^|[^\\a-zA-Z])(int|pi|sin|cos|tan|cot|ln|log|sqrt|lim|sum|infty|alpha|beta|gamma|Delta|vec|left|right|le|ge|ne|approx|cdot|times)(?=[_\^\{\(\|\[\s0-9a-zA-Z])/g, '$1\\$2');

    // Phục hồi dính toán tử: intcos -> \int \cos, intsin -> \int \sin, intdx -> \int dx
    str = str.replace(/(?<!\\)intcos\b/g, "\\int \\cos ");
    str = str.replace(/(?<!\\)intsin\b/g, "\\int \\sin ");
    str = str.replace(/(?<!\\)intdx\b/g, "\\int dx");
    str = str.replace(/\\intcos\b/g, "\\int \\cos ");
    str = str.replace(/\\intsin\b/g, "\\int \\sin ");
    str = str.replace(/\\intdx\b/g, "\\int dx");

    // 3. Khôi phục dấu \ cho các toán tử và hàm toán học phổ biến khác
    const mathKeywords = [
        "frac", "sqrt", "int", "iint", "iiint", "oint", "sum", "prod", "lim",
        "sin", "cos", "tan", "cot", "arcsin", "arccos", "arctan",
        "ln", "log", "exp", "pi", "alpha", "beta", "gamma", "Delta", "delta", "theta",
        "omega", "lambda", "mu", "sigma", "epsilon", "infty",
        "times", "div", "pm", "mp", "cdot", "leq", "geq", "neq", "approx", "equiv",
        "in", "notin", "subset", "subseteq", "cap", "cup", "forall", "exists", "to",
        "rightarrow", "leftarrow", "leftrightarrow", "Rightarrow", "Leftarrow", "Leftrightarrow",
        "partial", "vec", "left", "right"
    ];
    const kwRegex = new RegExp("(?<!\\\\)\\b(" + mathKeywords.join("|") + ")\\b", "g");
    str = str.replace(kwRegex, "\\$1");

    // 4. Chèn dấu cách sau các toán tử nếu dính liền với biến hoặc ngoặc
    str = str.replace(/\\(int|sum|lim|sin|cos|tan|cot|ln|log)(?=[a-zA-Z0-9])/g, "\\$1 ");

    // 5. Tự động bọc $...$ nếu có cú pháp LaTeX mà chưa nằm trong cặp dấu $
    if (!/(\$|\\\(|\\\[)/.test(str)) {
        if (/(\\frac|\\sqrt|\\int|\\sum|\\lim|\\sin|\\cos|\\tan|\\cot|\\ln|\\log|\\pi|\\alpha|\\beta|\\Delta|\\infty)/.test(str)) {
            str = `$${str.trim()}$`;
        }
    }

    // Escape HTML an toàn
    return str.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}

function formatQuizMathText(text) {
    return normalizeMathLatex(text);
}

// Làm sạch JSON chuỗi trắc nghiệm tránh hỏng escape sequence KaTeX
function cleanQuizJsonString(rawStr) {
    let clean = String(rawStr || "")
        .replace(/^```json\s*/i, "")
        .replace(/^```\s*/i, "")
        .replace(/```$/i, "")
        .trim();

    // Sửa các ký tự điều khiển ASCII sinh ra do \f (form feed) và \b (backspace) trong JSON spec
    clean = clean.replace(/\u000c/g, "\\\\f").replace(/\u0008/g, "\\\\b");

    // Chỉ nhân đôi backslash đơn độc lập (chưa được escape), KHÔNG chạm vào các backslash đã nhân đôi (\\\\):
    clean = clean.replace(/(?<!\\)((?:\\\\)*)\\(?!["\\\/bfnrtu])/g, "$1\\\\");

    return clean;
}

// 1. Register & Render Interactive Quiz
function registerQuizData(jsonStr) {
    const qid = "q_" + Math.random().toString(36).substr(2, 7);
    try {
        const clean = cleanQuizJsonString(jsonStr);
        const data = JSON.parse(clean);
        data.answers = {};
        window.quizRegistry[qid] = data;

        // Phân loại Đề THPTQG 2025–2026 (3 Phần) hay Đề thi thông thường
        const isThptqg = (data.exam_type === "THPTQG_2025" || (data.part1 && Array.isArray(data.part1)) || data.part2 || data.part3);

        let widgetHtml = "";
        if (isThptqg) {
            widgetHtml = renderThptqgQuizWidget(qid, data);
        } else {
            widgetHtml = renderStandardQuizWidget(qid, data);
        }

        window.quizBlocks[qid] = widgetHtml;
        setTimeout(() => {
            const widget = document.getElementById(`quizWidget_${qid}`);
            triggerKaTeX(widget);
        }, 150);
        return qid;
    } catch (e) {
        console.error("Lỗi parse Quiz JSON:", e, jsonStr);
        window.quizBlocks[qid] = `<div class="alert alert-warning small">Không thể nạp dữ liệu trắc nghiệm: ${escapeHtml(e.message)}</div>`;
        return qid;
    }
}

// Render Đề thi chuẩn thông thường (1 Phần phẳng)
function renderStandardQuizWidget(qid, data) {
    const questions = data.questions || [];
    let questionsHtml = "";

    questions.forEach((q, idx) => {
        let optionsObj = q.options || {};
        if (Array.isArray(optionsObj)) {
            const map = {};
            optionsObj.forEach((optText, i) => {
                const letter = String.fromCharCode(65 + i);
                map[letter] = optText;
            });
            optionsObj = map;
        }

        let optionsHtml = "";
        let optIndex = 0;
        for (const [rawKey, rawVal] of Object.entries(optionsObj)) {
            let keyLetter = String(rawKey).trim().toUpperCase();
            if (/^\d+$/.test(keyLetter)) {
                keyLetter = String.fromCharCode(65 + parseInt(keyLetter, 10));
            } else if (!/^[A-Z]$/.test(keyLetter)) {
                keyLetter = String.fromCharCode(65 + optIndex);
            }

            let cleanVal = stripOptionPrefix(rawVal);

            optionsHtml += `
                <div class="quiz-option-btn quiz-option-label d-flex align-items-center gap-2.5 p-2.5 rounded-3 mb-2 cursor-pointer" data-qid="${qid}" data-idx="${idx}" data-key="${keyLetter}" onclick="selectQuizOption('${qid}', ${idx}, '${keyLetter}', this)">
                    <span class="quiz-opt-letter">${keyLetter}</span>
                    <span class="quiz-opt-text flex-grow-1">${formatQuizMathText(cleanVal)}</span>
                </div>
            `;
            optIndex++;
        }

        questionsHtml += `
            <div class="quiz-question-item mb-4 p-3 rounded-3" id="qitem_${qid}_${idx}">
                <div class="fw-bold fs-6 quiz-question-text mb-2.5">
                    <span class="badge bg-primary me-2">Câu ${idx + 1}</span>${formatQuizMathText(q.question)}
                </div>
                <div class="quiz-options-group">${optionsHtml}</div>
                <div class="quiz-explanation-box p-3 rounded-3 mt-2" id="qexp_${qid}_${idx}" style="display:none;">
                    <div class="fw-bold text-indigo extra-small mb-1">💡 GIẢI THÍCH CHI TIẾT:</div>
                    <div class="small">${formatQuizMathText(q.explanation || "Không có giải thích chi tiết.")}</div>
                </div>
            </div>
        `;
    });

    return `
        <div class="quiz-card-container quiz-card-interactive shadow-sm my-3" id="quizWidget_${qid}">
            <div class="quiz-card-header p-3 d-flex align-items-center justify-content-between border-bottom">
                <div>
                    <h5 class="fw-bold mb-0 fs-6 quiz-card-title">🎯 ${escapeHtml(data.title || "Bài kiểm tra trắc nghiệm")}</h5>
                    <span class="extra-small quiz-card-subtitle">Số lượng: ${questions.length} câu • Chấm điểm thang 10</span>
                </div>
                <div class="badge bg-primary-subtle text-primary border border-primary-subtle px-3 py-1.5 rounded-pill">
                    ⏱️ Làm bài trực tiếp
                </div>
            </div>

            <div class="quiz-questions-list p-3">${questionsHtml}</div>

            <div class="quiz-card-footer p-3 d-flex align-items-center justify-content-between border-top" id="quizActionBar_${qid}">
                <div id="quizScoreText_${qid}" class="fw-bold fs-6 quiz-score-display"></div>
                <div class="d-flex gap-2">
                    <button type="button" class="btn btn-success rounded-pill px-4 btn-sm fw-semibold shadow-sm" id="btnGrade_${qid}" onclick="gradeQuiz('${qid}')">
                        <i class="bi bi-check2-circle me-1"></i>Nộp bài &amp; Chấm điểm
                    </button>
                </div>
            </div>
        </div>
    `;
}

// Render Đề thi chuẩn Bộ GD&ĐT THPTQG 2025–2026 (3 Phần)
function renderThptqgQuizWidget(qid, data) {
    data.p1Answers = {};
    data.p2Answers = {};
    data.p3Answers = {};
    data.graded = false;

    const part1 = data.part1 || [];
    const part2 = data.part2 || [];
    const part3 = data.part3 || [];

    // --- PHẦN 1: TRẮC NGHIỆM 4 LỰA CHỌN ---
    let p1Html = "";
    part1.forEach((q, idx) => {
        let optionsObj = q.options || {};
        if (Array.isArray(optionsObj)) {
            const map = {};
            optionsObj.forEach((optText, i) => {
                map[String.fromCharCode(65 + i)] = optText;
            });
            optionsObj = map;
        }

        let optionsHtml = "";
        let optIndex = 0;
        for (const [rawKey, rawVal] of Object.entries(optionsObj)) {
            let keyLetter = String(rawKey).trim().toUpperCase();
            if (/^\d+$/.test(keyLetter)) {
                keyLetter = String.fromCharCode(65 + parseInt(keyLetter, 10));
            } else if (!/^[A-Z]$/.test(keyLetter)) {
                keyLetter = String.fromCharCode(65 + optIndex);
            }

            let cleanVal = stripOptionPrefix(rawVal);

            optionsHtml += `
                <div class="quiz-option-btn quiz-option-label d-flex align-items-center gap-2.5 p-2.5 rounded-3 mb-2 cursor-pointer" data-qid="${qid}" data-part="p1" data-idx="${idx}" data-key="${keyLetter}" onclick="selectThptqgP1Option('${qid}', ${idx}, '${keyLetter}', this)">
                    <span class="quiz-opt-letter">${keyLetter}</span>
                    <span class="quiz-opt-text flex-grow-1">${formatQuizMathText(cleanVal)}</span>
                </div>
            `;
            optIndex++;
        }

        p1Html += `
            <div class="quiz-question-item mb-4 p-3 rounded-3" id="thptqg_p1_${qid}_${idx}">
                <div class="fw-bold fs-6 quiz-question-text mb-2.5">
                    <span class="badge bg-primary me-2">Câu ${idx + 1}</span>${formatQuizMathText(q.question)}
                </div>
                <div class="quiz-options-group">${optionsHtml}</div>
                <div class="quiz-explanation-box p-3 rounded-3 mt-2" id="thptqg_p1_exp_${qid}_${idx}" style="display:none;">
                    <div class="fw-bold text-indigo extra-small mb-1">💡 GIẢI THÍCH CHI TIẾT:</div>
                    <div class="small">${formatQuizMathText(q.explanation || "Không có giải thích chi tiết.")}</div>
                </div>
            </div>
        `;
    });

    // --- PHẦN 2: TRẮC NGHIỆM ĐÚNG / SAI ---
    let p2Html = "";
    part2.forEach((q, idx) => {
        const statements = q.statements || [];
        let stmtsHtml = "";

        statements.forEach((stmt, sIdx) => {
            const sKey = (stmt.key || stmt.label || stmt.letter || String.fromCharCode(97 + sIdx)).toLowerCase();
            stmtsHtml += `
                <div class="thptqg-statement-row d-flex align-items-center justify-content-between p-2.5 rounded-3 mb-2" id="thptqg_p2_stmt_${qid}_${idx}_${sKey}">
                    <div class="thptqg-statement-text pe-3">
                        <strong class="text-primary me-1">${sKey})</strong>
                        <span>${formatQuizMathText(stmt.text)}</span>
                    </div>
                    <div class="thptqg-tf-actions d-flex gap-1.5 flex-shrink-0">
                        <button type="button" class="btn btn-sm btn-outline-success btn-true-false px-3 rounded-pill fw-semibold" id="btn_tf_${qid}_${idx}_${sKey}_true" onclick="selectThptqgP2Option('${qid}', ${idx}, '${sKey}', true, this)">
                            Đúng
                        </button>
                        <button type="button" class="btn btn-sm btn-outline-danger btn-true-false px-3 rounded-pill fw-semibold" id="btn_tf_${qid}_${idx}_${sKey}_false" onclick="selectThptqgP2Option('${qid}', ${idx}, '${sKey}', false, this)">
                            Sai
                        </button>
                    </div>
                </div>
            `;
        });

        p2Html += `
            <div class="quiz-question-item mb-4 p-3 rounded-3" id="thptqg_p2_${qid}_${idx}">
                <div class="fw-bold fs-6 quiz-question-text mb-2.5">
                    <span class="badge bg-success me-2">Câu ${idx + 1}</span>${formatQuizMathText(q.context || q.question || "")}
                </div>
                <div class="thptqg-statements-group mb-2">${stmtsHtml}</div>
                <div class="quiz-explanation-box p-3 rounded-3 mt-2" id="thptqg_p2_exp_${qid}_${idx}" style="display:none;">
                    <div class="fw-bold text-indigo extra-small mb-1">💡 GIẢI THÍCH CHI TIẾT:</div>
                    <div class="small">${formatQuizMathText(q.explanation || "Không có giải thích chi tiết.")}</div>
                </div>
            </div>
        `;
    });

    // --- PHẦN 3: TRẮC NGHIỆM TRẢ LỜI NGẮN ---
    let p3Html = "";
    part3.forEach((q, idx) => {
        p3Html += `
            <div class="quiz-question-item mb-4 p-3 rounded-3" id="thptqg_p3_${qid}_${idx}">
                <div class="fw-bold fs-6 quiz-question-text mb-2.5">
                    <span class="badge bg-warning text-dark me-2">Câu ${idx + 1}</span>${formatQuizMathText(q.question)}
                </div>
                <div class="d-flex align-items-center gap-2.5 mt-2">
                    <label class="small fw-semibold text-secondary flex-shrink-0">Điền đáp số:</label>
                    <input type="text" class="form-control form-control-sm thptqg-short-input rounded-3" style="max-width: 220px;" placeholder="Ví dụ: 2.5 hoặc -10" oninput="recordThptqgP3Answer('${qid}', ${idx}, this.value)" id="thptqg_p3_input_${qid}_${idx}">
                    <span class="thptqg-p3-feedback-badge" id="thptqg_p3_badge_${qid}_${idx}"></span>
                </div>
                <div class="quiz-explanation-box p-3 rounded-3 mt-2" id="thptqg_p3_exp_${qid}_${idx}" style="display:none;">
                    <div class="fw-bold text-indigo extra-small mb-1">💡 GIẢI THÍCH CHI TIẾT (Đáp án: ${escapeHtml(String(q.answer))}):</div>
                    <div class="small">${formatQuizMathText(q.explanation || "Không có giải thích chi tiết.")}</div>
                </div>
            </div>
        `;
    });

    return `
        <div class="quiz-card-container quiz-card-interactive thptqg-exam-container shadow-sm my-3" id="quizWidget_${qid}">
            <!-- Header chuẩn Bộ GD&ĐT -->
            <div class="quiz-card-header p-3 border-bottom d-flex align-items-center justify-content-between">
                <div>
                    <div class="badge bg-danger-subtle text-danger border border-danger-subtle px-2.5 py-1 rounded-pill mb-1 fw-bold extra-small">
                        🏆 ĐỀ CHUẨN BỘ GIÁO DỤC &amp; ĐÀO TẠO THPTQG 2025–2026
                    </div>
                    <h5 class="fw-bold mb-0 fs-6 quiz-card-title">${escapeHtml(data.title || "Đề thi thử Tốt nghiệp THPT")}</h5>
                    <span class="extra-small quiz-card-subtitle">Môn: ${escapeHtml(data.subject || "Học tập")} • 3 Phần thi • Thang điểm 10.00</span>
                </div>
                <div class="badge bg-primary-subtle text-primary border border-primary-subtle px-3 py-1.5 rounded-pill">
                    ⏱️ Chấm điểm lũy tiến
                </div>
            </div>

            <!-- NỘI DUNG 3 PHẦN -->
            <div class="quiz-questions-list p-3">
                ${part1.length > 0 ? `
                    <div class="thptqg-part-section mb-4">
                        <div class="thptqg-part-title-badge p-2.5 rounded-3 mb-3 bg-primary-subtle text-primary border border-primary-subtle fw-bold small d-flex align-items-center justify-content-between">
                            <span><i class="bi bi-1-circle-fill me-1.5"></i>PHẦN I: TRẮC NGHIỆM NHIỀU PHƯƠNG ÁN LỰA CHỌN</span>
                            <span class="extra-small opacity-75">${part1.length} câu • 0.25đ / câu</span>
                        </div>
                        ${p1Html}
                    </div>
                ` : ''}

                ${part2.length > 0 ? `
                    <div class="thptqg-part-section mb-4">
                        <div class="thptqg-part-title-badge p-2.5 rounded-3 mb-3 bg-success-subtle text-success border border-success-subtle fw-bold small d-flex align-items-center justify-content-between">
                            <span><i class="bi bi-2-circle-fill me-1.5"></i>PHẦN II: TRẮC NGHIỆM ĐÚNG / SAI</span>
                            <span class="extra-small opacity-75">${part2.length} câu • Đúng 1 ý=0.1đ, 2 ý=0.25đ, 3 ý=0.5đ, 4 ý=1.0đ</span>
                        </div>
                        ${p2Html}
                    </div>
                ` : ''}

                ${part3.length > 0 ? `
                    <div class="thptqg-part-section mb-4">
                        <div class="thptqg-part-title-badge p-2.5 rounded-3 mb-3 bg-warning-subtle text-warning-emphasis border border-warning-subtle fw-bold small d-flex align-items-center justify-content-between">
                            <span><i class="bi bi-3-circle-fill me-1.5"></i>PHẦN III: TRẮC NGHIỆM TRẢ LỜI NGẮN</span>
                            <span class="extra-small opacity-75">${part3.length} câu • Điền đáp số</span>
                        </div>
                        ${p3Html}
                    </div>
                ` : ''}
            </div>

            <!-- Footer Action Bar & Score Summary -->
            <div class="quiz-card-footer p-3 border-top" id="quizActionBar_${qid}">
                <div id="quizScoreText_${qid}" class="quiz-score-display mb-2"></div>
                <div class="d-flex justify-content-between align-items-center">
                    <span class="extra-small text-secondary">Quy chế Bộ GD&ĐT: Kiểm tra kỹ bài làm trước khi nộp</span>
                    <button type="button" class="btn btn-success rounded-pill px-4 btn-sm fw-semibold shadow-sm" id="btnGrade_${qid}" onclick="gradeThptqgQuiz('${qid}')">
                        <i class="bi bi-check2-circle me-1"></i>Nộp bài &amp; Chấm điểm THPTQG
                    </button>
                </div>
            </div>
        </div>
    `;
}

// Xử lý tương tác THPTQG
function selectThptqgP1Option(qid, qIdx, optKey, el) {
    const data = window.quizRegistry[qid];
    if (!data || data.graded) return;
    data.p1Answers[qIdx] = optKey;

    const parent = el.closest(".quiz-options-group");
    if (parent) {
        parent.querySelectorAll(".quiz-option-label, .quiz-option-btn").forEach(l => l.classList.remove("selected"));
    }
    el.classList.add("selected");
}

function selectThptqgP2Option(qid, qIdx, stmtKey, isTrue, el) {
    const data = window.quizRegistry[qid];
    if (!data || data.graded) return;
    const answerKey = `${qIdx}_${stmtKey}`;
    data.p2Answers[answerKey] = isTrue;

    const row = document.getElementById(`thptqg_p2_stmt_${qid}_${qIdx}_${stmtKey}`);
    if (row) {
        const btnTrue = document.getElementById(`btn_tf_${qid}_${qIdx}_${stmtKey}_true`);
        const btnFalse = document.getElementById(`btn_tf_${qid}_${qIdx}_${stmtKey}_false`);
        if (isTrue) {
            btnTrue?.classList.add("active", "btn-success");
            btnTrue?.classList.remove("btn-outline-success");
            btnFalse?.classList.remove("active", "btn-danger");
            btnFalse?.classList.add("btn-outline-danger");
        } else {
            btnFalse?.classList.add("active", "btn-danger");
            btnFalse?.classList.remove("btn-outline-danger");
            btnTrue?.classList.remove("active", "btn-success");
            btnTrue?.classList.add("btn-outline-success");
        }
    }
}

function recordThptqgP3Answer(qid, qIdx, val) {
    const data = window.quizRegistry[qid];
    if (!data || data.graded) return;
    data.p3Answers[qIdx] = String(val || "").trim();
}

// Chấm điểm chuẩn Bộ GD&ĐT THPTQG 2025–2026
function gradeThptqgQuiz(qid) {
    const data = window.quizRegistry[qid];
    if (!data) return;
    data.graded = true;

    const part1 = data.part1 || [];
    const part2 = data.part2 || [];
    const part3 = data.part3 || [];

    // --- 1. CHẤM PHẦN I (0.25đ / câu) ---
    let p1Correct = 0;
    part1.forEach((q, idx) => {
        let optionsObj = q.options || {};
        if (Array.isArray(optionsObj)) {
            const map = {};
            optionsObj.forEach((optText, i) => {
                map[String.fromCharCode(65 + i)] = optText;
            });
            optionsObj = map;
        }

        const userChoice = data.p1Answers[idx] ? String(data.p1Answers[idx]).trim().toUpperCase() : "";
        const correctChoice = extractCorrectOptionKey(q, optionsObj);

        const container = document.getElementById(`thptqg_p1_${qid}_${idx}`);
        const expBox = document.getElementById(`thptqg_p1_exp_${qid}_${idx}`);
        if (expBox) expBox.style.display = "block";

        if (container) {
            const labels = container.querySelectorAll(".quiz-option-btn, .quiz-option-label");
            labels.forEach(lbl => {
                const optKey = (lbl.getAttribute("data-key") || "").toUpperCase();
                lbl.classList.remove("selected");
                if (optKey === correctChoice) {
                    lbl.classList.add("correct", "is-correct");
                }
                if (optKey === userChoice && userChoice !== correctChoice) {
                    lbl.classList.add("incorrect", "is-wrong");
                }
            });
        }
        if (userChoice && correctChoice && userChoice === correctChoice) {
            p1Correct++;
        }
    });
    const struct = getThptqgStructureInfo(data.subject || data.title || "");
    const p1PointPerQ = struct.p1Point || 0.25;
    const p1Score = p1Correct * p1PointPerQ;

    // --- 2. CHẤM PHẦN II (LŨY TIẾN: 1 ý=0.1đ, 2 ý=0.25đ, 3 ý=0.5đ, 4 ý=1.0đ) ---
    let p2Score = 0;
    let p2FullCorrectCount = 0;
    part2.forEach((q, idx) => {
        const statements = q.statements || [];
        let correctStmtCount = 0;

        statements.forEach((stmt, sIdx) => {
            const sKey = (stmt.key || stmt.label || stmt.letter || String.fromCharCode(97 + sIdx)).toLowerCase();
            const userVal = data.p2Answers[`${idx}_${sKey}`];
            const rawAns = stmt.answer !== undefined ? stmt.answer : (stmt.result !== undefined ? stmt.result : stmt.correct);
            const correctVal = parseBooleanAnswer(rawAns);

            const row = document.getElementById(`thptqg_p2_stmt_${qid}_${idx}_${sKey}`);
            const btnTrue = document.getElementById(`btn_tf_${qid}_${idx}_${sKey}_true`);
            const btnFalse = document.getElementById(`btn_tf_${qid}_${idx}_${sKey}_false`);

            if (row) {
                if (userVal === correctVal) {
                    row.classList.add("border", "border-success", "bg-success-subtle");
                    correctStmtCount++;
                } else {
                    row.classList.add("border", "border-danger", "bg-danger-subtle");
                }
            }
            if (btnTrue && btnFalse) {
                if (correctVal === true) {
                    btnTrue.classList.add("border-2", "shadow-sm");
                } else {
                    btnFalse.classList.add("border-2", "shadow-sm");
                }
            }
        });

        // Áp dụng thang điểm lũy tiến Bộ GD&ĐT
        let qScore = 0;
        if (correctStmtCount === 1) qScore = 0.10;
        else if (correctStmtCount === 2) qScore = 0.25;
        else if (correctStmtCount === 3) qScore = 0.50;
        else if (correctStmtCount === 4) {
            qScore = 1.00;
            p2FullCorrectCount++;
        }
        p2Score += qScore;

        const expBox = document.getElementById(`thptqg_p2_exp_${qid}_${idx}`);
        if (expBox) expBox.style.display = "block";
    });

    // --- 3. CHẤM PHẦN III (TRẢ LỜI NGẮN) ---
    let p3Correct = 0;
    // Điểm mỗi câu Phần III (Toán: 6 câu = 0.5đ/câu; KHTN/KHXH: 6 câu = 0.25đ/câu)
    const isMath = /toán/i.test(data.subject || data.title || "");
    const p3PointPerQ = struct.p3Point || (isMath ? 0.50 : 0.25);

    part3.forEach((q, idx) => {
        const rawUser = String(data.p3Answers[idx] || "").trim().replace(",", ".").replace(/\$/g, "");
        const rawAns = q.answer !== undefined ? q.answer : (q.result !== undefined ? q.result : (q.correct !== undefined ? q.correct : ""));
        const rawCorrect = String(rawAns || "").trim().replace(",", ".").replace(/\$/g, "");

        let isRight = false;
        if (rawUser && rawCorrect) {
            const numUser = parseFloat(rawUser);
            const numCorrect = parseFloat(rawCorrect);
            if (!isNaN(numUser) && !isNaN(numCorrect)) {
                isRight = Math.abs(numUser - numCorrect) < 0.001;
            } else {
                isRight = rawUser.toLowerCase() === rawCorrect.toLowerCase();
            }
        }

        const inputEl = document.getElementById(`thptqg_p3_input_${qid}_${idx}`);
        const badgeEl = document.getElementById(`thptqg_p3_badge_${qid}_${idx}`);
        const expBox = document.getElementById(`thptqg_p3_exp_${qid}_${idx}`);

        if (inputEl) {
            inputEl.disabled = true;
            if (isRight) {
                inputEl.classList.add("is-valid");
            } else {
                inputEl.classList.add("is-invalid");
            }
        }
        if (badgeEl) {
            if (isRight) {
                badgeEl.className = "badge bg-success py-1.5 px-2.5 rounded-pill extra-small";
                badgeEl.innerHTML = `<i class="bi bi-check-lg me-1"></i>Đúng (+${p3PointPerQ}đ)`;
            } else {
                badgeEl.className = "badge bg-danger py-1.5 px-2.5 rounded-pill extra-small";
                badgeEl.innerHTML = `<i class="bi bi-x-lg me-1"></i>Sai (ĐA: ${escapeHtml(rawCorrect)})`;
            }
        }
        if (expBox) expBox.style.display = "block";

        if (isRight) p3Correct++;
    });

    const p3Score = p3Correct * p3PointPerQ;

    // --- 4. TỔNG HỢP KẾT QUẢ THEO QUY CHẾ ---
    const totalScore = Math.min(10.0, Math.round((p1Score + p2Score + p3Score) * 100) / 100);

    const scoreTextEl = document.getElementById(`quizScoreText_${qid}`);
    if (scoreTextEl) {
        scoreTextEl.innerHTML = `
            <div class="thptqg-score-report-card p-3 rounded-4 mb-2 border">
                <div class="d-flex align-items-center justify-content-between mb-2 pb-2 border-bottom">
                    <span class="fw-bold fs-6"><i class="bi bi-award-fill text-warning me-1.5"></i>BẢNG ĐIỂM THPTQG CHUẨN BỘ GD&amp;ĐT:</span>
                    <span class="fs-4 fw-extrabold text-success">${totalScore.toFixed(2)} / 10.00</span>
                </div>
                <div class="row g-2 extra-small">
                    <div class="col-4">
                        <div class="p-2 rounded-3 bg-primary-subtle text-primary border border-primary-subtle">
                            <strong>Phần I:</strong> ${p1Score.toFixed(2)} đ<br>
                            <span class="opacity-75">Đúng ${p1Correct}/${part1.length} câu</span>
                        </div>
                    </div>
                    <div class="col-4">
                        <div class="p-2 rounded-3 bg-success-subtle text-success border border-success-subtle">
                            <strong>Phần II:</strong> ${p2Score.toFixed(2)} đ<br>
                            <span class="opacity-75">Tối đa ${p2FullCorrectCount}/${part2.length} câu</span>
                        </div>
                    </div>
                    <div class="col-4">
                        <div class="p-2 rounded-3 bg-warning-subtle text-warning-emphasis border border-warning-subtle">
                            <strong>Phần III:</strong> ${p3Score.toFixed(2)} đ<br>
                            <span class="opacity-75">Đúng ${p3Correct}/${part3.length} câu</span>
                        </div>
                    </div>
                </div>
            </div>
        `;
    }

    const actionBox = document.getElementById(`quizActionBar_${qid}`);
    const gradeBtn = document.getElementById(`btnGrade_${qid}`);
    if (gradeBtn) gradeBtn.style.display = "none";

    const oldRetry = document.getElementById(`btnRetry_${qid}`);
    if (oldRetry) oldRetry.remove();

    const retryBtn = document.createElement("button");
    retryBtn.id = `btnRetry_${qid}`;
    retryBtn.className = "btn btn-outline-primary rounded-pill px-4 btn-sm fw-semibold";
    retryBtn.innerHTML = `<i class="bi bi-arrow-counterclockwise me-1"></i>Thi lại đề này`;
    retryBtn.onclick = () => resetThptqgQuiz(qid);
    if (actionBox) actionBox.querySelector(".d-flex")?.appendChild(retryBtn);

    setTimeout(() => {
        const widget = document.getElementById(`quizWidget_${qid}`);
        triggerKaTeX(widget);
    }, 150);
}

function resetThptqgQuiz(qid) {
    const data = window.quizRegistry[qid];
    if (!data) return;
    data.graded = false;
    data.p1Answers = {};
    data.p2Answers = {};
    data.p3Answers = {};

    const widget = document.getElementById(`quizWidget_${qid}`);
    if (!widget) return;

    // Reset P1
    widget.querySelectorAll(".quiz-option-label, .quiz-option-btn").forEach(l => {
        l.classList.remove("selected", "correct", "incorrect", "is-correct", "is-wrong");
    });

    // Reset P2
    widget.querySelectorAll(".thptqg-statement-row").forEach(r => {
        r.classList.remove("border", "border-success", "border-danger", "bg-success-subtle", "bg-danger-subtle");
    });
    widget.querySelectorAll(".btn-true-false").forEach(b => {
        b.classList.remove("active", "btn-success", "btn-danger", "border-2", "shadow-sm");
        if (b.id.endsWith("_true")) b.classList.add("btn-outline-success");
        if (b.id.endsWith("_false")) b.classList.add("btn-outline-danger");
    });

    // Reset P3
    widget.querySelectorAll(".thptqg-short-input").forEach(inp => {
        inp.disabled = false;
        inp.value = "";
        inp.classList.remove("is-valid", "is-invalid");
    });
    widget.querySelectorAll(".thptqg-p3-feedback-badge").forEach(b => {
        b.className = "thptqg-p3-feedback-badge";
        b.innerHTML = "";
    });

    // Hide explanations
    widget.querySelectorAll(".quiz-explanation-box").forEach(e => e.style.display = "none");

    // Reset score display
    const scoreTextEl = document.getElementById(`quizScoreText_${qid}`);
    if (scoreTextEl) scoreTextEl.innerHTML = "";

    const gradeBtn = document.getElementById(`btnGrade_${qid}`);
    if (gradeBtn) gradeBtn.style.display = "inline-block";

    const retryBtn = document.getElementById(`btnRetry_${qid}`);
    if (retryBtn) retryBtn.remove();
}

// Xử lý Quiz thông thường
function selectQuizOption(qid, qIndex, optionKey, el) {
    const data = window.quizRegistry[qid];
    if (!data || data.graded) return;
    data.answers[qIndex] = optionKey;

    const parent = el.closest(".quiz-options-group");
    if (parent) {
        parent.querySelectorAll(".quiz-option-label, .quiz-option-btn").forEach(l => l.classList.remove("selected"));
    }
    el.classList.add("selected");
}

// Helper: Chuyển đổi an toàn giá trị Boolean từ mọi dạng dữ liệu AI (tránh bug JS Boolean("false") === true)
function parseBooleanAnswer(val) {
    if (typeof val === "boolean") return val;
    if (typeof val === "number") return val === 1;
    if (typeof val === "string") {
        const s = val.trim().toLowerCase();
        if (s === "true" || s === "đúng" || s === "dung" || s === "1" || s === "t" || s === "đ" || s === "d") return true;
        if (s === "false" || s === "sai" || s === "0" || s === "f" || s === "s") return false;
    }
    return Boolean(val);
}

// Helper: Trích xuất chính xác chữ cái đáp án đúng (A, B, C, D) từ mọi định dạng AI trả về
function extractCorrectOptionKey(q, optionsObj) {
    if (!q) return "";

    // 1. Tìm trường đáp án đúng từ các biến thể phổ biến
    let raw = q.answer !== undefined ? q.answer :
              (q.correct !== undefined ? q.correct :
              (q.correct_answer !== undefined ? q.correct_answer :
              (q.correctAnswer !== undefined ? q.correctAnswer :
              (q.correct_option !== undefined ? q.correct_option :
              (q.correctOption !== undefined ? q.correctOption :
              (q.key !== undefined ? q.key :
              (q.solution_key !== undefined ? q.solution_key :
              (q.result !== undefined ? q.result : ""))))))));

    if (raw === null || raw === undefined) raw = "";

    // Nếu là số nguyên 0, 1, 2, 3 hoặc "0", "1", "2", "3"
    if (typeof raw === "number" || /^\d+$/.test(String(raw).trim())) {
        const num = parseInt(raw, 10);
        if (num >= 0 && num < 4) {
            return String.fromCharCode(65 + num); // 0->A, 1->B, 2->C, 3->D
        }
        if (num >= 1 && num <= 4) {
            return String.fromCharCode(65 + num - 1); // 1->A, 2->B, 3->C, 4->D
        }
    }

    let str = String(raw).trim();

    // 2. Nếu chỉ có duy nhất 1 ký tự A-D
    if (/^[A-Da-d]$/.test(str)) {
        return str.toUpperCase();
    }

    // Kiểm tra mẫu "A.", "A)", "A:", "A -", "(A)" ở đầu chuỗi
    const leadMatch = str.match(/^\(?\s*([A-Da-d])\s*[\.\:\)\-\]\s]/);
    if (leadMatch && leadMatch[1]) {
        return leadMatch[1].toUpperCase();
    }

    // Kiểm tra "Đáp án A", "Câu A", "Option A", "Chọn A", "Phương án A"
    const phraseMatch = str.match(/(?:đáp\s*án|phương\s*án|câu|option|chọn|mệnh\s*đề)\s*([A-Da-d])\b/i);
    if (phraseMatch && phraseMatch[1]) {
        return phraseMatch[1].toUpperCase();
    }

    // 3. NẾU AI TRẢ VỀ NỘI DUNG PHƯƠNG ÁN (TEXT/LATEX) THAY VÌ CHỮ CÁI:
    // So sánh chuỗi với từng phương án trong optionsObj
    if (optionsObj && typeof optionsObj === "object") {
        const cleanRaw = str.replace(/[\$\s\\\{\}\(\)\,\.\:\;\-]/g, "").toLowerCase();
        if (cleanRaw.length > 0) {
            for (const [k, v] of Object.entries(optionsObj)) {
                let kLetter = String(k).trim().toUpperCase();
                if (/^\d+$/.test(kLetter)) {
                    kLetter = String.fromCharCode(65 + parseInt(kLetter, 10));
                }
                const cleanV = String(v || "").replace(/[\$\s\\\{\}\(\)\,\.\:\;\-]/g, "").toLowerCase();
                if (cleanV.length > 0 && (cleanV === cleanRaw || cleanRaw.includes(cleanV) || cleanV.includes(cleanRaw))) {
                    if (/^[A-Z]$/.test(kLetter)) return kLetter;
                }
            }
        }
    }

    // 4. Nếu vẫn chưa tìm thấy, thử trích xuất từ explanation: "chọn đáp án A" hoặc "do đó là A"
    if (q.explanation) {
        const expMatch = String(q.explanation).match(/(?:chọn|đáp\s*án|phương\s*án)\s*([A-Da-d])\b/i);
        if (expMatch && expMatch[1]) {
            return expMatch[1].toUpperCase();
        }
    }

    // Cuối cùng fallback lấy ký tự chữ cái A-D đầu tiên tìm thấy trong chuỗi
    const anyLetter = str.match(/([A-Da-d])/);
    if (anyLetter && anyLetter[1]) {
        return anyLetter[1].toUpperCase();
    }

    return "";
}

function gradeQuiz(qid) {
    const data = window.quizRegistry[qid];
    if (!data || !data.questions) return;
    data.graded = true;

    let correctCount = 0;
    const total = data.questions.length;

    data.questions.forEach((q, idx) => {
        let optionsObj = q.options || {};
        if (Array.isArray(optionsObj)) {
            const map = {};
            optionsObj.forEach((optText, i) => {
                map[String.fromCharCode(65 + i)] = optText;
            });
            optionsObj = map;
        }

        let userChoice = data.answers[idx] ? String(data.answers[idx]).trim().toUpperCase() : "";
        let correctChoice = extractCorrectOptionKey(q, optionsObj);

        const qContainer = document.getElementById(`qitem_${qid}_${idx}`);
        const expBox = document.getElementById(`qexp_${qid}_${idx}`);

        if (expBox) expBox.style.display = "block";

        if (qContainer) {
            const labels = qContainer.querySelectorAll(".quiz-option-btn, .quiz-option-label");
            labels.forEach(lbl => {
                const optKey = (lbl.getAttribute("data-key") || "").toUpperCase();
                lbl.classList.remove("selected");
                if (optKey === correctChoice) {
                    lbl.classList.add("correct", "is-correct");
                }
                if (optKey === userChoice && userChoice !== correctChoice) {
                    lbl.classList.add("incorrect", "is-wrong");
                }
            });
        }

        if (userChoice && correctChoice && userChoice === correctChoice) {
            correctCount++;
        }
    });

    const score10 = ((correctCount / total) * 10).toFixed(1);
    const scoreTextEl = document.getElementById(`quizScoreText_${qid}`);
    if (scoreTextEl) {
        scoreTextEl.innerHTML = `🎉 Điểm số: <span class="text-success fw-bold fs-5">${score10}/10</span> (Đúng ${correctCount}/${total} câu)`;
    }

    const actionBox = document.getElementById(`quizActionBar_${qid}`);
    const gradeBtn = document.getElementById(`btnGrade_${qid}`);
    if (gradeBtn) gradeBtn.style.display = "none";

    const oldRetry = document.getElementById(`btnRetry_${qid}`);
    if (oldRetry) oldRetry.remove();

    const retryBtn = document.createElement("button");
    retryBtn.id = `btnRetry_${qid}`;
    retryBtn.className = "btn btn-outline-primary rounded-pill px-4 btn-sm fw-semibold";
    retryBtn.innerHTML = `<i class="bi bi-arrow-counterclockwise me-1"></i>Làm lại`;
    retryBtn.onclick = () => resetQuiz(qid);
    if (actionBox) actionBox.appendChild(retryBtn);

    setTimeout(() => {
        const widget = document.getElementById(`quizWidget_${qid}`);
        triggerKaTeX(widget);
    }, 100);
}

function resetQuiz(qid) {
    const data = window.quizRegistry[qid];
    if (!data) return;
    data.graded = false;
    data.answers = {};

    const widget = document.getElementById(`quizWidget_${qid}`);
    if (!widget) return;

    widget.querySelectorAll(".quiz-option-label, .quiz-option-btn").forEach(l => {
        l.classList.remove("selected", "correct", "incorrect", "is-correct", "is-wrong");
    });
    widget.querySelectorAll(".quiz-explanation-box").forEach(e => e.style.display = "none");

    const scoreTextEl = document.getElementById(`quizScoreText_${qid}`);
    if (scoreTextEl) scoreTextEl.innerHTML = "";

    const gradeBtn = document.getElementById(`btnGrade_${qid}`);
    if (gradeBtn) gradeBtn.style.display = "inline-block";

    const retryBtn = document.getElementById(`btnRetry_${qid}`);
    if (retryBtn) retryBtn.remove();
}

// 2. Register & Render 3D Flashcards
function registerFlashcardData(jsonStr) {
    const fcid = "fc_" + Math.random().toString(36).substr(2, 7);
    try {
        let clean = jsonStr.replace(/^```json\s*/i, "").replace(/^```\s*/i, "").replace(/```$/i, "").trim();
        const data = JSON.parse(clean);
        const cards = data.cards || [];
        window.flashcardRegistry[fcid] = { cards, index: 0, mastered: new Set() };

        const firstCard = cards[0] || { front: "Chưa có thẻ", back: "Chưa có nội dung" };

        const widgetHtml = `
            <div class="flashcards-interactive-container" id="fcWidget_${fcid}">
                <div class="d-flex align-items-center justify-content-between mb-2 border-bottom border-secondary pb-2">
                    <h6 class="fw-bold text-white mb-0">🎴 ${escapeHtml(data.title || "Bộ thẻ ghi nhớ Flashcards")}</h6>
                    <span class="badge bg-dark-subtle text-warning border border-secondary px-2.5 py-1 extra-small" id="fcCounter_${fcid}">
                        Thẻ 1 / ${cards.length}
                    </span>
                </div>

                <div class="flashcard-stage" id="stage_${fcid}" onclick="flipFlashcard('${fcid}')" title="Nhấn để lật thẻ 3D">
                    <div class="flashcard-3d-inner">
                        <div class="flashcard-face flashcard-front">
                            <span class="badge bg-indigo-subtle text-indigo mb-2 extra-small">MẶT TRƯỚC (CÂU HỎI)</span>
                            <div class="fs-5 fw-semibold px-3" id="frontText_${fcid}">${escapeHtml(firstCard.front)}</div>
                            <div class="text-secondary extra-small mt-3">💡 Nhấn vào thẻ để lật xem đáp án 🔄</div>
                        </div>
                        <div class="flashcard-face flashcard-back">
                            <span class="badge bg-success-subtle text-success mb-2 extra-small">MẶT SAU (ĐÁP ÁN)</span>
                            <div class="fs-6 px-3" id="backText_${fcid}">${escapeHtml(firstCard.back)}</div>
                            <div class="text-secondary extra-small mt-3">🔄 Nhấn để lật lại mặt trước</div>
                        </div>
                    </div>
                </div>

                <div class="d-flex align-items-center justify-content-between mt-3">
                    <button type="button" class="btn btn-outline-secondary btn-sm rounded-pill px-3" onclick="navFlashcard('${fcid}', -1)">
                        <i class="bi bi-chevron-left me-1"></i>Trước
                    </button>
                    <div class="d-flex gap-2">
                        <button type="button" class="btn btn-outline-warning btn-sm rounded-pill px-3" onclick="markReview('${fcid}')">
                            <i class="bi bi-arrow-repeat me-1"></i>Ôn lại
                        </button>
                        <button type="button" class="btn btn-success btn-sm rounded-pill px-3 fw-semibold" onclick="markMastered('${fcid}')">
                            <i class="bi bi-check-lg me-1"></i>Đã thuộc
                        </button>
                    </div>
                    <button type="button" class="btn btn-outline-secondary btn-sm rounded-pill px-3" onclick="navFlashcard('${fcid}', 1)">
                        Tiếp<i class="bi bi-chevron-right ms-1"></i>
                    </button>
                </div>
            </div>
        `;

        window.flashcardBlocks[fcid] = widgetHtml;
        return fcid;
    } catch (e) {
        console.error("Lỗi parse Flashcard JSON:", e, jsonStr);
        window.flashcardBlocks[fcid] = `<div class="alert alert-warning small">Không thể nạp Flashcards: ${escapeHtml(e.message)}</div>`;
        return fcid;
    }
}

function flipFlashcard(fcid) {
    const stage = document.getElementById(`stage_${fcid}`);
    if (stage) stage.classList.toggle("flipped");
}

function updateFlashcardUI(fcid) {
    const state = window.flashcardRegistry[fcid];
    if (!state || !state.cards.length) return;
    const card = state.cards[state.index];

    const stage = document.getElementById(`stage_${fcid}`);
    if (stage) stage.classList.remove("flipped");

    const frontEl = document.getElementById(`frontText_${fcid}`);
    const backEl = document.getElementById(`backText_${fcid}`);
    const counterEl = document.getElementById(`fcCounter_${fcid}`);

    if (frontEl) frontEl.innerText = card.front;
    if (backEl) backEl.innerText = card.back;
    if (counterEl) {
        const masteredPercent = Math.round((state.mastered.size / state.cards.length) * 100);
        counterEl.innerText = `Thẻ ${state.index + 1} / ${state.cards.length} (${masteredPercent}% đã thuộc)`;
    }
}

function navFlashcard(fcid, dir) {
    const state = window.flashcardRegistry[fcid];
    if (!state) return;
    let nextIdx = state.index + dir;
    if (nextIdx < 0) nextIdx = state.cards.length - 1;
    if (nextIdx >= state.cards.length) nextIdx = 0;
    state.index = nextIdx;
    updateFlashcardUI(fcid);
}

function markMastered(fcid) {
    const state = window.flashcardRegistry[fcid];
    if (!state) return;
    state.mastered.add(state.index);
    navFlashcard(fcid, 1);
}

function markReview(fcid) {
    const state = window.flashcardRegistry[fcid];
    if (!state) return;
    state.mastered.delete(state.index);
    navFlashcard(fcid, 1);
}

// 3. Learning Hub Controller
window.currentLearningMode = "notebook";

function openLearningHub() {
    window._isViewingLearningHub = true;
    const welcome = document.getElementById("welcome-screen");
    const chat = document.getElementById("chat");
    const hub = document.getElementById("learning-hub-screen");

    if (welcome) welcome.style.display = "none";
    if (chat) chat.style.display = "none";
    if (hub) hub.style.display = "block";

    // Set sidebar active
    document.querySelectorAll(".sidebar-menu-nav .nav-item").forEach(el => el.classList.remove("active"));
    const btn = document.getElementById("sidebarLearningBtn");
    if (btn) btn.classList.add("active");
}

function closeLearningHub() {
    window._isViewingLearningHub = false;
    const hub = document.getElementById("learning-hub-screen");
    if (hub) hub.style.display = "none";

    const chat = document.getElementById("chat");
    if (chat && chat.children.length > 0) {
        chat.style.display = "block";
    } else {
        const welcome = document.getElementById("welcome-screen");
        if (welcome) welcome.style.display = "flex";
    }
}

function scrollLearningCarousel(dir) {
    const track = document.getElementById("learningCarouselTrack");
    if (track) {
        track.scrollBy({ left: dir * 300, behavior: "smooth" });
    }
}

const LEARNING_SUBJECTS = {
    tieuhoc: [
        "Tiếng Việt", "Toán", "Tiếng Anh", "Đạo đức", "Tự nhiên & Xã hội", "Khoa học", "Lịch sử & Địa lí", "Tin học & Công nghệ", "Giáo dục thể chất", "Âm nhạc", "Mĩ thuật", "Hoạt động trải nghiệm", "Tiếng dân tộc"
    ],
    thcs: [
        "Ngữ văn", "Toán", "Ngoại ngữ 1 (Tiếng Anh)", "Giáo dục công dân", "Lịch sử", "Địa lí", "Vật lí (KHTN)", "Hóa học (KHTN)", "Sinh học (KHTN)", "Công nghệ", "Tin học", "Giáo dục thể chất", "Nghệ thuật (Âm nhạc, Mĩ thuật)", "Hoạt động trải nghiệm", "Giáo dục địa phương", "Ngoại ngữ 2"
    ],
    thpt: [
        "Ngữ văn", "Toán", "Ngoại ngữ 1 (Tiếng Anh)", "Lịch sử", "Địa lí", "Giáo dục kinh tế & pháp luật", "Vật lí", "Hóa học", "Sinh học", "Công nghệ", "Tin học", "GDQP & An ninh", "Giáo dục thể chất", "Nghệ thuật", "Hoạt động trải nghiệm & GDĐP", "Tư duy định lượng / Toán học (ĐGNL)", "Tư duy định tính / Ngôn ngữ (ĐGNL)", "Khoa học tự nhiên & Xã hội (ĐGNL)", "Ôn thi Tốt nghiệp THPT Quốc gia"
    ],
    dh_daicuong: [
        "Triết học Mác - Lênin", "Kinh tế chính trị Mác - Lênin", "Chủ nghĩa xã hội khoa học", "Lịch sử Đảng Cộng sản Việt Nam", "Tư tưởng Hồ Chí Minh", "Pháp luật đại cương", "Toán cao cấp (Giải tích 1-2-3)", "Đại số tuyến tính", "Xác suất thống kê", "Toán rời rạc", "Phương pháp tính", "Vật lý đại cương", "Hóa học đại cương", "Tin học đại cương", "Ngoại ngữ chuẩn đầu ra (IELTS/TOEIC/HSK/JLPT/TOPIK)", "Giáo dục quốc phòng & Thể chất"
    ],
    dh_chuyennganh: [
        "CNTT: Cấu trúc dữ liệu & Giải thuật", "CNTT: Lập trình hướng đối tượng (OOP)", "CNTT: Cơ sở dữ liệu & SQL", "CNTT: Mạng máy tính & An toàn thông tin", "CNTT: Trí tuệ nhân tạo (AI & Machine Learning)", "CNTT: Phát triển Web (Frontend / Backend / Fullstack)", "CNTT: Lập trình ứng dụng di động", "CNTT: Hệ điều hành & Kiến trúc máy tính",
        "Kinh tế: Kinh tế vi mô & Vĩ mô", "Kinh tế: Nguyên lý kế toán & Tài chính doanh nghiệp", "Kinh tế: Marketing căn bản & Digital Marketing", "Kinh tế: Quản trị học & Quản trị nhân sự", "Kinh tế: Kinh tế lượng & Phân tích dữ liệu", "Kinh tế: Logistics & Quản lý chuỗi cung ứng", "Kinh tế: Tài chính tiền tệ & Ngân hàng",
        "Kỹ thuật: Cơ học kỹ thuật & Sức bền vật liệu", "Kỹ thuật: Kỹ thuật điện & Điện tử", "Kỹ thuật: Vẽ kỹ thuật & CAD/CAM", "Kỹ thuật: Tự động hóa & PLC", "Kỹ thuật: Kỹ thuật nhiệt & Cơ lưu chất",
        "Y Dược: Giải phẫu học người", "Y Dược: Sinh lý học & Sinh lý bệnh", "Y Dược: Dược lý học & Dược lâm sàng", "Y Dược: Hóa sinh y học", "Y Dược: Vi sinh & Ký sinh trùng", "Y Dược: Bệnh học nội - ngoại khoa",
        "Luật & Hành chính: Luật dân sự & Tố tụng dân sự", "Luật & Hành chính: Luật hình sự & Tố tụng hình sự", "Luật & Hành chính: Luật hành chính & Luật kinh tế",
        "KH Xã hội: Xã hội học đại cương", "KH Xã hội: Tâm lý học đại cương", "KH Xã hội: Quan hệ quốc tế", "KH Xã hội: Báo chí & Truyền thông đa phương tiện", "KH Xã hội: Quản trị du lịch & Lữ hành",
        "Ngoại ngữ: Tiếng Anh chuyên ngành", "Ngoại ngữ: Ngữ pháp tiếng Anh nâng cao", "Ngoại ngữ: Luyện thi IELTS / TOEIC", "Ngoại ngữ: Tiếng Trung / Tiếng Nhật / Tiếng Hàn",
        "Sư phạm & Nghệ thuật: Giáo dục học đại cương", "Sư phạm & Nghệ thuật: Tâm lý học lứa tuổi & Sư phạm", "Sư phạm & Nghệ thuật: Phương pháp dạy học bộ môn", "Sư phạm & Nghệ thuật: Mỹ thuật & Kiến trúc"
    ]
};

function onLearningGradeChanged() {
    const gradeSelect = document.getElementById("learningGradeSelect");
    const subjectSelect = document.getElementById("learningSubjectSelect");
    const customWrapper = document.getElementById("learningCustomSubjectWrapper");
    if (!gradeSelect || !subjectSelect) return;

    const grade = gradeSelect.value || "thpt";
    const subjects = LEARNING_SUBJECTS[grade] || LEARNING_SUBJECTS.thpt;

    subjectSelect.innerHTML = subjects.map(s => `<option value="${escapeHtml(s)}">${escapeHtml(s)}</option>`).join("") +
        `<option value="__custom__">-- Tự nhập môn học khác --</option>`;

    if (customWrapper) customWrapper.style.display = "none";
}

// Helper: Cấu trúc đề thi THPTQG 2025–2026 chuẩn Bộ GD&ĐT theo từng môn
function getThptqgStructureInfo(subject) {
    const s = String(subject || "").toLowerCase();
    if (s.includes("toán")) {
        return {
            name: "Toán",
            time: "90 phút",
            totalQ: 22,
            p1: 12, p1Point: 0.25,
            p2: 4,
            p3: 6, p3Point: 0.50,
            desc: "22 câu (90 phút): Phần I (12 câu, 0.25đ) • Phần II (4 câu Đúng/Sai, lũy tiến tối đa 1.0đ) • Phần III (6 câu trả lời ngắn, 0.5đ)"
        };
    } else if (s.includes("vật lí") || s.includes("vật lý") || s.includes("hóa") || s.includes("sinh") || s.includes("địa")) {
        return {
            name: subject,
            time: "50 phút",
            totalQ: 28,
            p1: 18, p1Point: 0.25,
            p2: 4,
            p3: 6, p3Point: 0.25,
            desc: "28 câu (50 phút): Phần I (18 câu, 0.25đ) • Phần II (4 câu Đúng/Sai, lũy tiến tối đa 1.0đ) • Phần III (6 câu trả lời ngắn, 0.25đ)"
        };
    } else if (s.includes("sử") || s.includes("kinh tế") || s.includes("pháp luật") || s.includes("tin") || s.includes("công nghệ") || s.includes("gdkt")) {
        return {
            name: subject,
            time: "50 phút",
            totalQ: 28,
            p1: 24, p1Point: 0.25,
            p2: 4,
            p3: 0, p3Point: 0,
            desc: "28 câu (50 phút): Phần I (24 câu, 0.25đ) • Phần II (4 câu Đúng/Sai, lũy tiến tối đa 1.0đ)"
        };
    } else if (s.includes("tiếng anh") || s.includes("ngoại ngữ") || s.includes("tiếng pháp") || s.includes("tiếng trung") || s.includes("tiếng nhật")) {
        return {
            name: subject,
            time: "50 phút",
            totalQ: 40,
            p1: 40, p1Point: 0.25,
            p2: 0,
            p3: 0, p3Point: 0,
            desc: "40 câu (50 phút): Phần I (40 câu trắc nghiệm 4 lựa chọn, 0.25đ / câu)"
        };
    }
    return {
        name: subject,
        time: "50 phút",
        totalQ: 28,
        p1: 18, p1Point: 0.25,
        p2: 4,
        p3: 6, p3Point: 0.25,
        desc: "28 câu (50 phút): Phần I (18 câu, 0.25đ) • Phần II (4 câu Đúng/Sai, lũy tiến tối đa 1.0đ) • Phần III (6 câu trả lời ngắn, 0.25đ)"
    };
}

function updateThptqgHint() {
    const hint = document.getElementById("thptqgStructureHint");
    if (!hint) return;
    const subjectSelect = document.getElementById("learningSubjectSelect");
    const subject = subjectSelect?.value || "Toán";
    const struct = getThptqgStructureInfo(subject);
    hint.innerHTML = `
        <div class="p-2.5 rounded-3 bg-primary-subtle text-primary border border-primary-subtle extra-small">
            <div class="fw-bold mb-1"><i class="bi bi-info-circle-fill me-1"></i>Cấu trúc Đề thi THPTQG 2025–2026 môn ${escapeHtml(subject)} (${struct.time}):</div>
            <ul class="mb-0 ps-3">
                ${struct.p1 > 0 ? `<li><strong>Phần I:</strong> Trắc nghiệm 4 lựa chọn (${struct.p1} câu; ${struct.p1Point}đ / câu).</li>` : ''}
                ${struct.p2 > 0 ? `<li><strong>Phần II:</strong> Trắc nghiệm Đúng/Sai (${struct.p2} câu; lũy tiến 1 ý=0.1đ, 2 ý=0.25đ, 3 ý=0.5đ, 4 ý=1.0đ).</li>` : ''}
                ${struct.p3 > 0 ? `<li><strong>Phần III:</strong> Trắc nghiệm trả lời ngắn (${struct.p3} câu; ${struct.p3Point}đ / câu).</li>` : ''}
            </ul>
        </div>
    `;
}

function onLearningSubjectChanged() {
    const subjectSelect = document.getElementById("learningSubjectSelect");
    const customWrapper = document.getElementById("learningCustomSubjectWrapper");
    if (!subjectSelect || !customWrapper) return;
    if (subjectSelect.value === "__custom__") {
        customWrapper.style.display = "block";
        const customInput = document.getElementById("learningCustomSubjectInput");
        if (customInput) customInput.focus();
    } else {
        customWrapper.style.display = "none";
    }
    const countSelect = document.getElementById("learningCountSelect");
    if (countSelect && countSelect.value === "THPTQG_2025") {
        updateThptqgHint();
    }
}

window.onLearningSubjectChanged = onLearningSubjectChanged;

window.onLearningCountSelectChanged = function () {
    const countSelect = document.getElementById("learningCountSelect");
    const hint = document.getElementById("thptqgStructureHint");
    if (!countSelect) return;
    if (countSelect.value === "THPTQG_2025") {
        if (hint) {
            hint.style.display = "block";
            updateThptqgHint();
        }
        // Nếu đang ở cấp học khác, tự động chọn cấp THPT
        const gradeSelect = document.getElementById("learningGradeSelect");
        if (gradeSelect && gradeSelect.value !== "thpt") {
            gradeSelect.value = "thpt";
            onLearningGradeChanged();
        }
    } else {
        if (hint) hint.style.display = "none";
    }
};

function selectLearningCard(modeKey) {
    window.currentLearningMode = modeKey;
    const configPanel = document.getElementById("learningConfigPanel");
    const extraOpts = document.getElementById("learningExtraOptions");
    const titleEl = document.getElementById("selectedModeTitle");
    const iconEl = document.getElementById("selectedModeIcon");

    const modeMeta = {
        notebook: { title: "Biến đề cương thành sổ ghi chú học tập (Word .docx)", icon: "📖" },
        visual: { title: "Thử trải nghiệm khám phá sống động đa tầng", icon: "🌌" },
        quiz: { title: "Tự kiểm tra kiến thức (Đề thi trắc nghiệm chấm điểm)", icon: "🎯" },
        flashcard: { title: "Tạo thẻ thông tin ghi nhớ (Flashcards 3D)", icon: "🎴" },
        socratic: { title: "Học có hướng dẫn cùng Gia sư Socratic", icon: "👨‍🏫" }
    };

    const meta = modeMeta[modeKey] || modeMeta.notebook;
    if (titleEl) titleEl.innerText = meta.title;
    if (iconEl) iconEl.innerText = meta.icon;

    if (extraOpts) {
        extraOpts.style.display = (modeKey === "quiz" || modeKey === "flashcard") ? "block" : "none";
        const countSelect = document.getElementById("learningCountSelect");
        if (countSelect) {
            const thptqgOpt = countSelect.querySelector('option[value="THPTQG_2025"]');
            if (modeKey === "quiz") {
                if (thptqgOpt) thptqgOpt.style.display = "";
            } else {
                if (thptqgOpt) thptqgOpt.style.display = "none";
                if (countSelect.value === "THPTQG_2025") {
                    countSelect.value = "10";
                }
            }
        }
        window.onLearningCountSelectChanged();
    }

    onLearningGradeChanged();

    if (configPanel) {
        configPanel.style.display = "block";
        const input = document.getElementById("learningTopicInput");
        if (input) input.focus();
    }
}

function closeLearningConfig() {
    const configPanel = document.getElementById("learningConfigPanel");
    if (configPanel) configPanel.style.display = "none";
}

// ====== FILE UPLOAD IN LEARNING HUB ======
window._currentLearningFile = null;

window.onLearningFileSelected = function (e) {
    const file = e.target.files?.[0];
    if (!file) return;
    window._currentLearningFile = file;

    const chip = document.getElementById("learningFileChip");
    const nameEl = document.getElementById("learningFileName");
    const sizeEl = document.getElementById("learningFileSize");
    const iconEl = document.getElementById("learningFileIcon");

    if (chip) chip.classList.remove("d-none");
    if (nameEl) nameEl.innerText = file.name;
    if (sizeEl) sizeEl.innerText = (file.size / 1024).toFixed(1) + " KB";

    // Set icon based on extension
    if (iconEl) {
        const ext = file.name.split('.').pop().toLowerCase();
        if (ext === 'pdf') {
            iconEl.className = "bi bi-file-earmark-pdf text-danger";
        } else if (ext === 'docx' || ext === 'doc') {
            iconEl.className = "bi bi-file-earmark-word text-primary";
        } else if (['jpg', 'jpeg', 'png', 'webp'].includes(ext)) {
            iconEl.className = "bi bi-file-earmark-image text-warning";
        } else {
            iconEl.className = "bi bi-file-earmark-text text-info";
        }
    }

    // Reset doc dropdown if file selected
    const docSelect = document.getElementById("learningDocSelect");
    if (docSelect) docSelect.value = "";
};

window.removeLearningFile = function () {
    window._currentLearningFile = null;
    const fileInput = document.getElementById("learningFileUpload");
    if (fileInput) fileInput.value = "";
    const chip = document.getElementById("learningFileChip");
    if (chip) chip.classList.add("d-none");
};

window.onLearningDocSelectChanged = function () {
    const docSelect = document.getElementById("learningDocSelect");
    if (docSelect && docSelect.value) {
        window.removeLearningFile();
    }
};

async function startLearningSession() {
    const gradeSelect = document.getElementById("learningGradeSelect");
    const subjectSelect = document.getElementById("learningSubjectSelect");
    const customInput = document.getElementById("learningCustomSubjectInput");
    const topicInput = document.getElementById("learningTopicInput");
    const docSelect = document.getElementById("learningDocSelect");

    const gradeText = gradeSelect?.options[gradeSelect.selectedIndex]?.text || "Phổ thông";
    let subjectText = subjectSelect?.value || "";
    if (subjectText === "__custom__") {
        subjectText = customInput?.value?.trim() || "Chung";
    }

    let topic = topicInput?.value?.trim() || "";
    let uploadedFileText = "";
    let attachedFileName = "";
    let attachedImageBase64 = null;

    // 1. Nếu có file upload từ máy
    if (window._currentLearningFile) {
        attachedFileName = window._currentLearningFile.name;
        try {
            const formData = new FormData();
            formData.append("file", window._currentLearningFile);
            const parseRes = await fetch("/api/learning/parse-document", {
                method: "POST",
                body: formData
            });
            if (parseRes.ok) {
                const parseData = await parseRes.json();
                if (parseData.success) {
                    uploadedFileText = parseData.extractedText || "";
                    attachedImageBase64 = parseData.imageBase64 || null;
                }
            }
        } catch (e) {
            console.warn("Lỗi trích xuất tài liệu học tập:", e);
        }
    } else if (docSelect && docSelect.value) {
        attachedFileName = docSelect.value;
    }

    if (!topic && attachedFileName) {
        topic = `Tài liệu: ${attachedFileName}`;
    }

    if (!topic) {
        topic = `Toàn bộ trọng tâm kiến thức và bài tập môn ${subjectText} (${gradeText})`;
    }

    const count = document.getElementById("learningCountSelect")?.value || "10";
    const difficulty = document.getElementById("learningDifficultySelect")?.value || "Trung bình";
    const isThptqgExam = (window.currentLearningMode === "quiz" && count === "THPTQG_2025");

    const modeLabels = {
        notebook: "Sổ tay ghi chú",
        visual: "Khám phá trực quan",
        quiz: isThptqgExam ? "Đề chuẩn Bộ GD&ĐT THPTQG 2025–2026" : "Tự kiểm tra kiến thức",
        flashcard: "Flashcards 3D",
        socratic: "Gia sư Socratic"
    };
    const modeTitle = modeLabels[window.currentLearningMode] || "Học tập";

    let fileContextInstruction = "";
    if (uploadedFileText) {
        fileContextInstruction = `\n\n[NỘI DUNG TÀI LIỆU ÔN TẬP TỪ TỆP "${attachedFileName}"]:\n${uploadedFileText.substring(0, 30000)}\n[HẾT TÀI LIỆU - YÊU CẦU AI RA ĐỀ VÀ GIẢNG DẠY BÁM SÁT 100% THEO TÀI LIỆU TRÊN]`;
    }

    let prompt = "";
    switch (window.currentLearningMode) {
        case "notebook":
            prompt = `[HỌC TẬP: ${gradeText} - MÔN: ${subjectText} - SỔ GHI CHÚ KHOA HỌC]\nHãy tổng hợp và biên soạn toàn bộ nội dung sau thành một Sổ tay ghi chú học tập chi tiết, khoa học, đầy đủ công thức toán/lý/hóa (định dạng LaTeX $$...$$) và tạo sẵn tệp Word (.docx) chuẩn hóa với thẻ [ARTIFACT type="docx" title="So_tay_ghi_chu_${subjectText}"] để tôi tải về:\n"${topic}"${fileContextInstruction}`;
            break;
        case "visual":
            prompt = `[HỌC TẬP: ${gradeText} - MÔN: ${subjectText} - KHÁM PHÁ SỐNG ĐỘNG]\nHãy giải thích chủ đề sau bằng phương pháp trực quan hóa đa tầng, dùng ví dụ đời thực sinh động, hình tượng hóa, công thức KaTeX ($$...$$) và các thẻ <details><summary>Xem chi tiết tầng kiến thức...</summary></details> để phân tích sâu từng lớp khái niệm:\n"${topic}"${fileContextInstruction}`;
            break;
        case "quiz":
            if (isThptqgExam) {
                const struct = getThptqgStructureInfo(subjectText);
                prompt = `[HỌC TẬP: ${gradeText} - MÔN: ${subjectText} - ĐỀ THI CHUẨN BỘ GD&ĐT THPTQG 2025-2026]
Hãy tạo ĐỀ THI THỬ TỐT NGHIỆP THPT QG 2025–2026 chuẩn cấu trúc của Bộ Giáo dục & Đào tạo cho môn ${subjectText} về chủ đề "${topic}".
Thời gian làm bài: ${struct.time}. Tổng số câu: ${struct.totalQ} câu. Thang điểm: 10.00. Độ khó: ${difficulty}.

CẤU TRÚC BẮT BUỘC THEO QUY CHẾ BỘ GD&ĐT CHO MÔN ${subjectText.toUpperCase()}:
${struct.p1 > 0 ? `- PHẦN I: Trắc nghiệm 4 lựa chọn (${struct.p1} câu; mỗi câu đúng được ${struct.p1Point}đ).` : ''}
${struct.p2 > 0 ? `- PHẦN II: Trắc nghiệm Đúng/Sai (${struct.p2} câu; mỗi câu có ngữ cảnh và 4 mệnh đề a, b, c, d; tính điểm lũy tiến: đúng 1 ý = 0.1đ, đúng 2 ý = 0.25đ, đúng 3 ý = 0.5đ, đúng cả 4 ý = 1.0đ).` : ''}
${struct.p3 > 0 ? `- PHẦN III: Trắc nghiệm trả lời ngắn (${struct.p3} câu; mỗi câu đúng được ${struct.p3Point}đ; thí sinh điền kết quả dạng số thực/nguyên).` : ''}

QUY TẮC CÔNG THỨC TOÁN & KHOA HỌC:
- Mọi công thức Toán/Lý/Hóa/Sinh bắt buộc viết bằng LaTeX chuẩn, luôn dùng 2 dấu gạch chéo \\\\ trong JSON (ví dụ \\\\frac{1}{2}, \\\\int \\\\cos x dx).
- Bắt buộc đầy đủ cặp ngoặc nhọn: \\\\frac{1}{2}, \\\\frac{3}{4} (TUYỆT ĐỐI KHÔNG viết \\\\frac12 hay frac12).
- Luôn có dấu cách sau các toán tử: \\\\int \\\\cos(2x+1)\\\\,dx, \\\\sqrt{x^2+1}.
- Trong phương án trắc nghiệm, TUYỆT ĐỐI KHÔNG lặp lại ký tự "A.", "B.", "C.", "D." ở đầu chuỗi (ví dụ: viết "x = 2", KHÔNG viết "A. x = 2").

Trả về toàn bộ dữ liệu đề thi chuẩn xác bên trong thẻ [QUIZ_DATA]...[/QUIZ_DATA] với JSON exam_type: "THPTQG_2025" gồm title, subject: "${subjectText}", time: "${struct.time}", part1 (mảng ${struct.p1} câu), part2 (mảng ${struct.p2} câu), part3 (mảng ${struct.p3} câu).${fileContextInstruction}`;
            } else {
                prompt = `[HỌC TẬP: ${gradeText} - MÔN: ${subjectText} - TỰ KIỂM TRA KIẾN THỨC]
Hãy tạo bộ đề thi trắc nghiệm tương tác ${count} câu về chủ đề "${topic}" với độ khó ${difficulty}.

QUY CÁCH BẮT BUỘC TRONG [QUIZ_DATA]:
{
  "title": "Kiểm tra ${subjectText}: ${topic}",
  "questions": [
    {
      "id": 1,
      "question": "Nội dung câu hỏi (công thức Toán/Lý bọc trong $...$)",
      "options": ["Nội dung phương án A", "Nội dung phương án B", "Nội dung phương án C", "Nội dung phương án D"],
      "answer": "A",
      "explanation": "Lời giải chi tiết rõ ràng từng bước..."
    }
  ]
}

QUY TẮC CÔNG THỨC TOÁN & ĐÁP ÁN:
- Trường "answer" BẮT BUỘC CHỈ GHI DUY NHẤT 1 CHỮ CÁI IN HOA ĐẠI DIỆN ĐÁP ÁN ĐÚNG: "A", "B", "C" hoặc "D" (TUYỆT ĐỐI KHÔNG ghi cả câu, không ghi thêm chữ khác).
- Trong mảng "options", TUYỆT ĐỐI KHÔNG ghi lặp tiền tố "A.", "B.", "C.", "D." ở đầu chuỗi (hệ thống tự động hiển thị nút A, B, C, D).
- Mọi công thức Toán/Lý/Hóa bắt buộc viết bằng LaTeX chuẩn, luôn dùng 2 dấu gạch chéo \\\\ trong JSON (ví dụ \\\\frac{1}{2}, \\\\int \\\\cos x dx).
- Bắt buộc đầy đủ cặp ngoặc nhọn: \\\\frac{1}{2}, \\\\frac{3}{4} (TUYỆT ĐỐI KHÔNG viết \\\\frac12 hay frac12).
- Luôn có dấu cách sau các toán tử: \\\\int \\\\cos(2x+1)\\\\,dx, \\\\sqrt{x^2+1}.

Trả về toàn bộ dữ liệu bên trong thẻ [QUIZ_DATA]...[/QUIZ_DATA] để hệ thống hiển thị giao diện làm bài và chấm điểm tự động.${fileContextInstruction}`;
            }
            break;
        case "flashcard":
            prompt = `[HỌC TẬP: ${gradeText} - MÔN: ${subjectText} - FLASHCARDS 3D]\nHãy tạo bộ thẻ ghi nhớ gồm ${count} thẻ (mặt trước: câu hỏi/khái niệm/công thức, mặt sau: đáp án/giải thích súc tích) về chủ đề "${topic}". Trả về cấu trúc JSON chuẩn xác bên trong thẻ [FLASHCARD_DATA]...[/FLASHCARD_DATA] để hệ thống hiển thị thẻ 3D lật 2 mặt.${fileContextInstruction}`;
            break;
        case "socratic":
            prompt = `[HỌC TẬP: ${gradeText} - MÔN: ${subjectText} - GIA SƯ SOCRATIC]\nBạn là một gia sư AI kiên nhẫn và thông thái theo phương pháp gợi mở Socratic. Tôi muốn học về môn ${subjectText}: "${topic}".\nHãy bắt đầu bằng cách chia nhỏ lộ trình học thành các nấc kiến thức, đặt cho tôi đúng 1 câu hỏi đầu tiên để kiểm tra hiểu biết ban đầu của tôi, TUYỆT ĐỐI KHÔNG đưa ngay đáp án!${fileContextInstruction}`;
            break;
        default:
            prompt = `[HỌC TẬP: ${gradeText} - MÔN: ${subjectText}]\nChủ đề: ${topic}${fileContextInstruction}`;
            break;
    }

    // Thiết lập tóm tắt đẹp cho bong bóng người dùng thay vì toàn bộ văn bản tài liệu dài
    let summaryText = `🎓 ${modeTitle} — Môn ${subjectText} (${gradeText})`;
    if (isThptqgExam) {
        summaryText = `🎓 🏆 Đề chuẩn Bộ GD&ĐT THPTQG 2025–2026 — Môn ${subjectText} (${difficulty})`;
        if (attachedFileName) summaryText += `\n📎 Tài liệu: ${attachedFileName}`;
        else summaryText += `\n🎯 Chủ đề: ${topic}`;
    } else if (attachedFileName) {
        summaryText += `\n📎 Tài liệu: ${attachedFileName} (${count} câu • ${difficulty})`;
    } else {
        summaryText += `: ${topic} (${count} câu • ${difficulty})`;
    }
    window._pendingUserDisplaySummary = summaryText;

    // Cập nhật title trình duyệt và sidebar title
    document.title = `🎓 ${modeTitle}: ${subjectText} - Btoan AI`;

    closeLearningConfig();
    closeLearningHub();
    window.removeLearningFile();

    const msgEl = document.getElementById("msg");
    if (msgEl) {
        msgEl.value = prompt;
        if (attachedImageBase64) {
            window._pendingLearningImageBase64 = attachedImageBase64;
        }
        const sendBtn = document.getElementById("send");
        if (sendBtn) sendBtn.click();
    }
}

// 4. Advanced Settings Controller
function openSettingsModal() {
    const modal = document.getElementById("settingsModal");
    if (!modal) return;
    modal.style.display = "flex";
    switchSettingsGroup("account");
    loadSettingsUsage();
    loadSettingsPreferences();
    loadSettingsGems();
    loadSharedLinks();
}

function closeSettingsModal() {
    const modal = document.getElementById("settingsModal");
    if (modal) modal.style.display = "none";
}

function switchSettingsGroup(group) {
    document.querySelectorAll(".settings-group-pane").forEach(el => el.style.display = "none");
    document.querySelectorAll(".settings-group-pill, .settings-nav-item").forEach(el => el.classList.remove("active"));

    const pane = document.getElementById(`setGroup-${group}`);
    const btn = document.getElementById(`setTabBtn-${group}`);
    if (pane) pane.style.display = "block";
    if (btn) btn.classList.add("active");

    const titleEl = document.getElementById("settingsCurrentGroupTitle");
    if (titleEl) {
        const titles = {
            account: "Tài khoản & Hạn mức",
            preferences: "Lựa chọn ưu tiên",
            privacy: "Dữ liệu & Quyền riêng tư",
            admin: "Quản trị viên"
        };
        titleEl.innerText = titles[group] || "Cài đặt";
    }

    if (group === "admin") {
        loadAdminData();
    }
}

async function loadSettingsUsage() {
    try {
        const res = await fetch("/api/settings/usage");
        const data = await res.json();
        if (data.ok) {
            const chatLbl = document.getElementById("usageChatLabel");
            const chatBar = document.getElementById("usageChatBar");
            const ideLbl = document.getElementById("usageIdeLabel");
            const ideBar = document.getElementById("usageIdeBar");
            const imgLbl = document.getElementById("usageImageLabel");
            const imgBar = document.getElementById("usageImageBar");

            if (chatLbl) chatLbl.innerText = `${data.chatCount || 12} / ${data.chatLimit || 100} lượt`;
            if (chatBar) chatBar.style.width = `${Math.min(100, Math.round(((data.chatCount || 12) / (data.chatLimit || 100)) * 100))}%`;

            if (ideLbl) ideLbl.innerText = `${data.ideCount || 5} / ${data.ideLimit || 30} lượt`;
            if (ideBar) ideBar.style.width = `${Math.min(100, Math.round(((data.ideCount || 5) / (data.ideLimit || 30)) * 100))}%`;

            if (imgLbl) imgLbl.innerText = `${data.imageCount || 3} / ${data.imageLimit || 20} ảnh`;
            if (imgBar) imgBar.style.width = `${Math.min(100, Math.round(((data.imageCount || 3) / (data.imageLimit || 20)) * 100))}%`;
        }
    } catch (e) {
        console.warn("Usage load failed:", e);
    }
}

async function loadSettingsPreferences() {
    try {
        const res = await fetch("/api/settings/preferences");
        const data = await res.json();
        if (data.ok && data.preferences) {
            const p = data.preferences;
            const ci = document.getElementById("setCustomInstructions");
            const sm = document.getElementById("setSavedMemories");
            if (ci && p.customInstructions) ci.value = p.customInstructions;
            if (sm && p.savedMemories) sm.value = p.savedMemories;
        }
    } catch (e) {
        console.warn("Preferences load failed:", e);
    }
}

async function saveSettingsPreferences() {
    const ci = document.getElementById("setCustomInstructions")?.value || "";
    const sm = document.getElementById("setSavedMemories")?.value || "";
    const theme = document.querySelector('input[name="setThemeRadio"]:checked')?.value || "dark";
    const alertEl = document.getElementById("settingsAlertMsg");

    try {
        const res = await fetch("/api/settings/preferences", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ customInstructions: ci, savedMemories: sm, theme: theme })
        });
        const data = await res.json();
        if (alertEl) {
            alertEl.innerText = "✅ Đã lưu cài đặt và chỉ dẫn AI thành công!";
            setTimeout(() => { alertEl.innerText = ""; }, 3000);
        }
    } catch (e) {
        if (alertEl) alertEl.innerText = "❌ Lỗi lưu cài đặt: " + e.message;
    }
}

async function loadSettingsGems() {
    const container = document.getElementById("gemsListContainer");
    if (!container) return;

    try {
        const res = await fetch("/api/settings/gems");
        const data = await res.json();
        if (data.ok && data.gems && data.gems.length > 0) {
            container.innerHTML = "";
            data.gems.forEach(g => {
                const item = document.createElement("div");
                item.className = "p-2.5 rounded-3 d-flex align-items-center justify-content-between";
                item.style.background = "#1e1f20";
                item.style.border = "1px solid #3c4043";
                item.innerHTML = `
                    <div class="d-flex align-items-center gap-2.5">
                        <span class="fs-4">${escapeHtml(g.icon || "💎")}</span>
                        <div>
                            <div class="fw-semibold small text-white">${escapeHtml(g.name)}</div>
                            <div class="extra-small text-secondary">${escapeHtml(g.description || "")}</div>
                        </div>
                    </div>
                    <div class="d-flex gap-1.5">
                        <button type="button" class="btn btn-outline-success btn-sm extra-small rounded-pill px-2.5" onclick="useGem('${escapeHtml(g.name)}', '${encodeURIComponent(g.systemInstruction || "")}')">
                            <i class="bi bi-play-fill me-1"></i>Dùng
                        </button>
                        ${!g.isPreset ? `<button type="button" class="btn btn-outline-danger btn-sm extra-small rounded-pill px-2" onclick="deleteGem(${g.id})"><i class="bi bi-trash"></i></button>` : ""}
                    </div>
                `;
                container.appendChild(item);
            });
        } else {
            container.innerHTML = `<div class="text-secondary extra-small text-center py-2">Chưa có trợ lý Gem nào.</div>`;
        }
    } catch (e) {
        container.innerHTML = `<div class="text-secondary extra-small text-center py-2">Không thể tải danh sách Gem.</div>`;
    }
}

function toggleNewGemForm() {
    const form = document.getElementById("newGemFormContainer");
    if (form) form.style.display = form.style.display === "none" ? "block" : "none";
}

async function saveNewGem() {
    const name = document.getElementById("newGemName")?.value?.trim();
    const icon = document.getElementById("newGemIcon")?.value?.trim() || "💎";
    const desc = document.getElementById("newGemDesc")?.value?.trim() || "";
    const prompt = document.getElementById("newGemPrompt")?.value?.trim() || "";

    if (!name) {
        alert("Vui lòng nhập tên cho Gem!");
        return;
    }

    try {
        const res = await fetch("/api/settings/gems", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ name, icon, description: desc, systemInstruction: prompt })
        });
        const data = await res.json();
        if (data.ok) {
            toggleNewGemForm();
            loadSettingsGems();
        } else {
            alert("Lỗi lưu Gem: " + (data.error || ""));
        }
    } catch (e) {
        alert("Lỗi kết nối: " + e.message);
    }
}

async function deleteGem(id) {
    if (!confirm("Bạn có chắc chắn muốn xóa Gem này?")) return;
    try {
        const res = await fetch(`/api/settings/gems/${id}`, { method: "DELETE" });
        const data = await res.json();
        if (data.ok) loadSettingsGems();
    } catch (e) {
        alert("Lỗi xóa Gem: " + e.message);
    }
}

function useGem(name, encodedPrompt) {
    closeSettingsModal();
    const prompt = decodeURIComponent(encodedPrompt);
    newChat(true);
    const msgEl = document.getElementById("msg");
    if (msgEl) {
        msgEl.value = `Xin chào! Tôi muốn trò chuyện cùng Gem: ${name}.`;
        const sendBtn = document.getElementById("send");
        if (sendBtn) sendBtn.click();
    }
}

async function submitImportMemory() {
    const input = document.getElementById("importMemoryInput");
    const text = input?.value?.trim();
    if (!text) {
        alert("Vui lòng dán nội dung bộ nhớ muốn nhập!");
        return;
    }

    try {
        const res = await fetch("/api/settings/import-memory", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ memoryText: text })
        });
        const data = await res.json();
        if (data.ok) {
            alert("🎉 Đã nhập bộ nhớ vào Btoan AI thành công!");
            input.value = "";
            loadSettingsPreferences();
        }
    } catch (e) {
        alert("Lỗi nhập bộ nhớ: " + e.message);
    }
}

async function loadSharedLinks() {
    const container = document.getElementById("sharedLinksContainer");
    if (!container) return;

    try {
        const res = await fetch("/api/settings/shared-links");
        const data = await res.json();
        if (data.ok && data.links && data.links.length > 0) {
            container.innerHTML = "";
            data.links.forEach(l => {
                const item = document.createElement("div");
                item.className = "p-2 rounded-3 d-flex align-items-center justify-content-between";
                item.style.background = "#1e1f20";
                item.innerHTML = `
                    <div class="overflow-hidden me-2">
                        <div class="text-white extra-small fw-semibold text-truncate">${escapeHtml(l.title || "Cuộc trò chuyện chia sẻ")}</div>
                        <div class="text-secondary extra-small" style="font-size:0.7rem;">Tạo lúc: ${l.createdAt ? new Date(l.createdAt).toLocaleDateString("vi-VN") : "Gần đây"}</div>
                    </div>
                    <div class="d-flex gap-1.5 flex-shrink-0">
                        <button type="button" class="btn btn-outline-info btn-sm extra-small rounded-pill" onclick="navigator.clipboard.writeText(location.origin + '/share/' + '${l.token}'); alert('Đã sao chép link!');">Sao chép</button>
                        <button type="button" class="btn btn-outline-danger btn-sm extra-small rounded-pill" onclick="revokeShareLink('${l.token}')">Thu hồi</button>
                    </div>
                `;
                container.appendChild(item);
            });
        }
    } catch (e) { }
}

async function revokeShareLink(token) {
    if (!confirm("Bạn có chắc chắn muốn thu hồi liên kết chia sẻ này?")) return;
    try {
        const res = await fetch("/api/settings/revoke-link", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ token })
        });
        loadSharedLinks();
    } catch (e) {
        alert("Lỗi thu hồi link: " + e.message);
    }
}

async function submitUserFeedback() {
    const category = document.getElementById("feedbackCategory")?.value || "Góp ý tính năng";
    const rating = parseInt(document.getElementById("feedbackRating")?.value || "5");
    const comment = document.getElementById("feedbackComment")?.value?.trim();

    if (!comment) {
        alert("Vui lòng nhập nội dung ý kiến phản hồi!");
        return;
    }

    try {
        const res = await fetch("/api/settings/feedback", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ category, rating, comment, message: comment })
        });
        const data = await res.json();
        if (data.ok || data.success) {
            alert("🙏 Cảm ơn bạn rất nhiều! Ý kiến đóng góp của bạn đã được ghi nhận.");
            const cEl = document.getElementById("feedbackComment");
            if (cEl) cEl.value = "";
        } else {
            alert("Lỗi gửi phản hồi: " + (data.error || "Không thể gửi"));
        }
    } catch (e) {
        alert("Lỗi gửi phản hồi: " + e.message);
    }
}

// ==========================================
// 🛡️ ADMIN MANAGEMENT JAVASCRIPT CONTROLLERS
// ==========================================
function loadAdminData() {
    loadAdminFeedbacks();
    loadAdminUsers();
    loadAdminStats();
}

async function loadAdminFeedbacks() {
    const container = document.getElementById("adminFeedbackList");
    if (!container) return;
    container.innerHTML = '<div class="text-secondary extra-small text-center py-2"><div class="spinner-border spinner-border-sm me-1"></div> Đang tải danh sách phản hồi...</div>';

    try {
        const res = await fetch("/api/admin/feedbacks");
        if (!res.ok) {
            container.innerHTML = '<div class="text-danger extra-small text-center py-2">Bạn không có quyền quản trị hoặc chưa đăng nhập Admin.</div>';
            return;
        }
        const data = await res.json();
        const fbs = data.feedbacks || [];
        if (fbs.length === 0) {
            container.innerHTML = '<div class="text-secondary extra-small text-center py-3">Chưa có phản hồi nào từ người dùng.</div>';
            return;
        }

        container.innerHTML = "";
        fbs.forEach(fb => {
            const stars = "⭐".repeat(Math.min(5, Math.max(1, fb.rating || 5)));
            const dateStr = fb.createdAt ? new Date(fb.createdAt).toLocaleString("vi-VN") : "";
            const isResolved = fb.isResolved;

            const item = document.createElement("div");
            item.className = "p-2.5 rounded-3 border";
            item.style.background = isResolved ? "#18191a" : "#1e1f20";
            item.style.borderColor = isResolved ? "#2c3035" : "#45494e";

            item.innerHTML = `
                <div class="d-flex align-items-center justify-content-between mb-1">
                    <div class="d-flex align-items-center gap-2">
                        <span class="badge ${isResolved ? 'bg-secondary' : 'bg-warning text-dark'} extra-small">${isResolved ? '✓ Đã xử lý' : 'Chờ xử lý'}</span>
                        <span class="fw-semibold small text-white">${escapeHtml(fb.userName || 'Ẩn danh')}</span>
                        <span class="extra-small text-secondary">(${escapeHtml(fb.userEmail || 'Chưa có email')})</span>
                    </div>
                    <span class="extra-small">${stars}</span>
                </div>
                <div class="small text-light mb-1.5" style="white-space: pre-wrap; font-size: 0.82rem;">${escapeHtml(fb.message || '')}</div>
                <div class="d-flex align-items-center justify-content-between extra-small text-secondary pt-1 border-top border-secondary">
                    <span><i class="bi bi-tag me-1"></i>${escapeHtml(fb.category || 'Góp ý')} • ${dateStr}</span>
                    <div class="d-flex gap-1">
                        ${!isResolved ? `<button type="button" class="btn btn-outline-success btn-sm extra-small py-0.5 px-2 rounded-pill" onclick="resolveAdminFeedback(${fb.id})"><i class="bi bi-check-lg me-1"></i>Đã xử lý</button>` : ''}
                        <button type="button" class="btn btn-outline-danger btn-sm extra-small py-0.5 px-2 rounded-pill" onclick="deleteAdminFeedback(${fb.id})"><i class="bi bi-trash"></i></button>
                    </div>
                </div>
            `;
            container.appendChild(item);
        });
    } catch (e) {
        container.innerHTML = `<div class="text-danger extra-small text-center py-2">Lỗi tải danh sách phản hồi: ${e.message}</div>`;
    }
}

async function resolveAdminFeedback(id) {
    try {
        const res = await fetch("/api/admin/feedback/resolve", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ id })
        });
        const data = await res.json();
        if (data.ok) {
            loadAdminFeedbacks();
            loadAdminStats();
        } else {
            alert(data.error || "Không thể xử lý phản hồi");
        }
    } catch (e) {
        alert("Lỗi: " + e.message);
    }
}

async function deleteAdminFeedback(id) {
    if (!confirm("Bạn có chắc chắn muốn xóa phản hồi này?")) return;
    try {
        const res = await fetch(`/api/admin/feedback/${id}`, { method: "DELETE" });
        const data = await res.json();
        if (data.ok) {
            loadAdminFeedbacks();
            loadAdminStats();
        } else {
            alert(data.error || "Không thể xóa phản hồi");
        }
    } catch (e) {
        alert("Lỗi: " + e.message);
    }
}

async function loadAdminUsers() {
    const tbody = document.getElementById("adminUserTableBody");
    if (!tbody) return;
    tbody.innerHTML = '<tr><td colspan="7" class="text-center text-secondary py-3"><div class="spinner-border spinner-border-sm me-1"></div> Đang nạp danh sách tài khoản...</td></tr>';

    try {
        const res = await fetch("/api/admin/users");
        if (!res.ok) {
            tbody.innerHTML = '<tr><td colspan="7" class="text-center text-danger py-3">Bạn không có quyền quản trị người dùng.</td></tr>';
            return;
        }
        const data = await res.json();
        const users = data.users || [];
        if (users.length === 0) {
            tbody.innerHTML = '<tr><td colspan="7" class="text-center text-secondary py-3">Chưa có người dùng nào.</td></tr>';
            return;
        }

        tbody.innerHTML = "";
        users.forEach(u => {
            const tr = document.createElement("tr");
            tr.innerHTML = `
                <td><code>#${u.id}</code></td>
                <td>
                    <div class="fw-semibold text-white">${escapeHtml(u.username)}</div>
                    <div class="text-secondary extra-small">${escapeHtml(u.fullName || '')}</div>
                </td>
                <td><span class="text-secondary">${escapeHtml(u.email || '—')}</span></td>
                <td>
                    <span class="badge ${u.isPro ? 'bg-indigo text-white' : 'bg-secondary'} extra-small">
                        ${u.isPro ? '⚡ PRO' : 'FREE'}
                    </span>
                    ${u.isLocked ? '<span class="badge bg-danger extra-small ms-1">Khóa</span>' : ''}
                </td>
                <td class="text-center fw-semibold text-info">${u.conversationCount || 0}</td>
                <td class="text-center fw-semibold text-success">${u.projectCount || 0}</td>
                <td class="text-end" style="white-space: nowrap;">
                    <div class="d-inline-flex gap-1">
                        <button type="button" class="btn btn-outline-${u.isPro ? 'warning' : 'primary'} extra-small py-0.5 px-2 text-nowrap" onclick="toggleAdminUserPro(${u.id})" title="${u.isPro ? 'Chuyển về Free' : 'Nâng cấp Pro'}">
                            ${u.isPro ? 'Hạ Free' : 'Nâng Pro'}
                        </button>
                        <button type="button" class="btn btn-outline-${u.isLocked ? 'success' : 'danger'} extra-small py-0.5 px-2 text-nowrap" onclick="toggleAdminUserLock(${u.id})" title="${u.isLocked ? 'Mở khóa tài khoản' : 'Khóa tài khoản'}">
                            ${u.isLocked ? 'Mở' : 'Khóa'}
                        </button>
                    </div>
                </td>
            `;
            tbody.appendChild(tr);
        });
    } catch (e) {
        tbody.innerHTML = `<tr><td colspan="7" class="text-center text-danger py-3">Lỗi tải người dùng: ${e.message}</td></tr>`;
    }
}

async function toggleAdminUserPro(userId) {
    try {
        const res = await fetch("/api/admin/user/toggle-pro", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ userId })
        });
        const data = await res.json();
        if (data.ok) {
            loadAdminUsers();
        } else {
            alert(data.error || "Không thể cập nhật gói");
        }
    } catch (e) {
        alert("Lỗi: " + e.message);
    }
}

async function toggleAdminUserLock(userId) {
    try {
        const res = await fetch("/api/admin/user/toggle-lock", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ userId })
        });
        const data = await res.json();
        if (data.ok) {
            loadAdminUsers();
        } else {
            alert(data.error || "Không thể khóa/mở khóa");
        }
    } catch (e) {
        alert("Lỗi: " + e.message);
    }
}

async function loadAdminStats() {
    try {
        const res = await fetch("/api/admin/stats");
        if (!res.ok) return;
        const data = await res.json();

        const uEl = document.getElementById("statTotalUsers");
        const cEl = document.getElementById("statChatsToday");
        const fEl = document.getElementById("statPendingFeedbacks");
        const pList = document.getElementById("adminActivePortsList");

        if (uEl) uEl.innerText = data.totalUsers || 0;
        if (cEl) cEl.innerText = data.chatsToday || 0;
        if (fEl) fEl.innerText = data.pendingFeedbacks || 0;

        if (pList) {
            const ports = data.activePorts || [];
            if (ports.length === 0) {
                pList.innerHTML = '<span class="text-secondary extra-small">Hiện không có cổng Live Preview nào đang chạy.</span>';
            } else {
                pList.innerHTML = "";
                ports.forEach(p => {
                    const span = document.createElement("span");
                    span.className = "badge bg-dark border border-secondary text-light d-inline-flex align-items-center gap-1.5 p-1.5 extra-small";
                    span.innerHTML = `
                        <span class="spinner-grow spinner-grow-sm text-success" style="width: 7px; height: 7px;"></span>
                        <span>Cổng :${p.port} (${p.status})</span>
                        <button type="button" class="btn btn-link text-danger p-0 ms-1" style="font-size: 11px; text-decoration: none;" onclick="killAdminPort(${p.port})" title="Dừng tiến trình cổng này">✖</button>
                    `;
                    pList.appendChild(span);
                });
            }
        }
    } catch (e) {
        console.error("Lỗi nạp admin stats:", e);
    }
}

async function killAdminPort(port) {
    if (!confirm(`Bạn có chắc muốn dừng tiến trình trên cổng ${port}?`)) return;
    try {
        const res = await fetch("/api/admin/kill-port", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ port })
        });
        const data = await res.json();
        if (data.ok) {
            alert(`Đã gửi lệnh dừng tiến trình tại cổng ${port}`);
            loadAdminStats();
        } else {
            alert(data.error || "Không thể dừng tiến trình");
        }
    } catch (e) {
        alert("Lỗi: " + e.message);
    }
}

function escapeHtml(str) {
    if (!str) return "";
    return String(str)
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;")
        .replace(/'/g, "&#039;");
}

function openKnowledgeModal() {
    const fileDoc = document.getElementById("fileDoc");
    if (fileDoc) {
        fileDoc.click();
    } else {
        alert("Đang mở kho tài liệu & tri thức cá nhân...");
    }
}
