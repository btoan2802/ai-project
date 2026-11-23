# BÁO CÁO HỒ SƠ FRONTEND & BẢN ĐỒ ĐỐI CHIẾU MÃ NGUỒN: BtoanAiNet
**Dự án:** `BtoanAiNet` (Frontend UI, Scripts & Client-Server Integration)  
**Thời gian quét:** Tháng 11/2026  
**Chế độ:** CHỈ ĐỌC (READ-ONLY AUDIT - Tuyệt đối không can thiệp code)  
**Bảo mật:** Toàn bộ API Key, Password, Token nhạy cảm đã được che thành `<REDACTED>`.

---

## PHẦN 1: BẢNG ĐỐI CHIẾU CHÉO FRONTEND JS ↔ BACKEND CONTROLLER & SIGNALR

Bảng dưới đây quét toàn bộ các lời gọi `fetch(...)`, `$.ajax`, `$.post`, `$.get`, `axios`, `HubConnectionBuilder`, `.invoke(...)`, `.on(...)` trong các file client chính:
1. `wwwroot/login/SignUp_LogIn_Form.js`
2. `wwwroot/js/app.js`
3. `wwwroot/js/agent_ide.js`
4. `Views/Home/Index.cshtml`
5. `Views/Agent/Index.cshtml`

### BẢNG ĐỐI CHIẾU CHÉO CHI TIẾT

| File JS + Số dòng | Endpoint / Hub Event gọi từ JS | Controller / Hub tương ứng ở Backend | Trạng thái kỹ thuật | Đánh giá & Rủi ro khi chạy Kestrel / Docker |
| :--- | :--- | :--- | :--- | :--- |
| **`SignUp_LogIn_Form.js:316`** | `POST ai/api/save_face.php` | `AccountController.cs` (`[HttpPost("save-face-descriptor")]`) | **Lệch URL & Định dạng** | Gọi tới file PHP cũ. Kestrel trả về **404 Not Found** trừ khi có reverse proxy rewrite. Cần sửa về `/api/account/save-face-descriptor` hoặc `/Account/SaveFaceDescriptor`. |
| **`SignUp_LogIn_Form.js:402`** | `POST ai/api/login_face.php` | `AccountController.cs` (`[HttpPost("verify-face-login")]`) | **Lệch URL & Định dạng** | Gọi endpoint PHP cũ. Kestrel trả về **404 Not Found**. Backend C# chờ endpoint verify face login dạng JSON descriptor. |
| **`app.js:2 & 576`** | `POST /ai/api/chat.php` (`WORKER_URL`) | `ChatController.cs` (`[HttpPost("send")]` / `[HttpPost("/api/chat")]`) | **Lệch URL** | Gửi tin nhắn chat tới file PHP cũ thay vì ASP.NET Core ChatController. |
| **`app.js:642 & 1471`**| `POST /ai/api/rename_conversation.php` | `ChatController.cs` (`[HttpPost("rename-conversation")]`) | **Lệch URL** | Gọi endpoint PHP cũ, trên Kestrel trả về **404 Not Found**. |
| **`app.js:926`** | `POST /ai/api/generate_image.php` | `MediaController.cs` (`[HttpPost("generate-image")]`) | **Lệch URL** | JS gọi endpoint PHP cũ. Controller C# đã có sẵn `MediaController` nhưng route là `/api/media/generate-image`. |
| **`app.js:998 & 1154`**| `GET /ai/api/get_last_image.php` | `MediaController.cs` / `ChatController.cs` | **Không tồn tại ở C#** | Backend C# chưa có endpoint trả về ảnh gần nhất theo conversation_id. |
| **`app.js:1022`** | `POST /ai/api/edit_image.php` | `MediaController.cs` | **Lệch URL** | Backend C# xử lý qua `ImageGenerationService` nhưng chưa bind route PHP này. |
| **`app.js:1194`** | `POST /ai/api/generate_video.php` | `MediaController.cs` (`[HttpPost("generate-video")]`) | **Lệch URL** | Route C# là `/api/media/generate-video`. |
| **`app.js:1279`** | `POST /ai/api/login.php` | `AccountController.cs` (`[HttpPost("login")]`) | **Lệch URL** | Form đăng nhập trong modal app.js gọi file PHP cũ. |
| **`app.js:1348`** | `POST /ai/api/register.php` | `AccountController.cs` (`[HttpPost("register")]`) | **Lệch URL** | Form đăng ký gọi file PHP cũ. |
| **`app.js:1404`** | `GET /ai/api/get_conversations.php` | `ChatController.cs` (`[HttpGet("conversations")]`) | **Lệch URL** | Client load lịch sử chat từ file PHP cũ. |
| **`app.js:1491`** | `POST /ai/api/delete_conversation.php`| `ChatController.cs` (`[HttpPost("delete-conversation")]`)| **Lệch URL** | Client xoá hội thoại qua file PHP cũ. |
| **`app.js:1545`** | `GET /ai/api/get_messages.php` | `ChatController.cs` (`[HttpGet("messages")]`) | **Lệch URL** | Lấy tin nhắn hội thoại qua PHP cũ. |
| **`app.js:1883`** | `POST /ai/api/upload_avatar.php` | `AccountController.cs` (`[HttpPost("avatar")]`) | **Lệch URL** | Upload avatar profile gọi file PHP cũ. |
| **`app.js:2098`** | `GET /ai/api/search.php` | `ChatController.cs` (`[HttpGet("search")]`) | **Lệch URL** | Tìm kiếm tin nhắn. |
| **`app.js:2139, 2189`**| `POST /ai/api/upload.php` | `FileController.cs` (`[HttpPost("upload")]`) | **Lệch URL** | Upload file đính kèm. C# Controller nhận tại `/api/file/upload`. |
| **`app.js:2329`** | `GET /ai/api/search_history.php` | `ChatController.cs` | **Lệch URL** | Lịch sử tìm kiếm. |
| **`app.js:2382, 2780`**| `GET /ai/api/check_profile.php` | `AccountController.cs` (`[HttpGet("profile")]`) | **Lệch URL** | Kiểm tra thông tin user hiện tại. |
| **`app.js:2513`** | `POST /ai/api/update_name.php` | `AccountController.cs` (`[HttpPost("update-name")]`) | **Lệch URL** | Đổi tên hiển thị. |
| **`app.js:2569`** | `POST ai/api/google-login.php` | `AccountController.cs` (`[HttpPost("google-login")]`) | **Lệch URL** | Xác thực Google OAuth token. |
| **`app.js:2855`** | `GET /ai/api/check_video_job.php` | `MediaController.cs` / `MediaAsyncController` | **Không tồn tại ở C#** | Polling job video render chưa được nối với MassTransit Consumer. |
| **`app.js:3010`** | `POST /ai/api/generate_music.php` | `MediaController.cs` (`[HttpPost("generate-music")]`) | **Lệch URL** | Sinh nhạc. Route C# là `/api/media/generate-music`. |
| **`agent_ide.js:351`**| `GET /ai/api/agent/projects` | `WorkspaceController.cs` (`[HttpGet("projects")]`) | **Lệch URL** | Lấy danh sách project cho IDE. |
| **`agent_ide.js:507`**| `POST /ai/api/agent/projects/create` | `WorkspaceController.cs` (`[HttpPost("create")]`) | **Lệch URL** | Tạo project mới. |
| **`agent_ide.js:534`**| `POST /ai/api/agent/projects/upload-zip` | `FileController.cs` (`[HttpPost("upload-zip")]`) | **Lệch URL** | Upload mã nguồn ZIP. |
| **`agent_ide.js:553`**| `POST /ai/api/agent/projects/import-git` | `WorkspaceController.cs` (`[HttpPost("import-git")]`) | **Lệch URL** | Clone repo Git vào workspace. |
| **`agent_ide.js:583`**| `GET /api/workspace/{id}/tree` | `WorkspaceController.cs:GetFileTree` | **KHỚP CHÍNH XÁC (200 OK)**| Endpoint chuẩn ASP.NET Core. |
| **`agent_ide.js:758`**| `POST /api/workspace/file/rename` | `WorkspaceController.cs:RenameFile` | **KHỚP CHÍNH XÁC (200 OK)**| Endpoint chuẩn ASP.NET Core. |
| **`agent_ide.js:782`**| `POST /api/workspace/file/delete` | `WorkspaceController.cs:DeleteFile` | **KHỚP CHÍNH XÁC (200 OK)**| Endpoint chuẩn ASP.NET Core. |
| **`agent_ide.js:825`**| `POST /api/workspace/file/create` | `WorkspaceController.cs:CreateFile` | **KHỚP CHÍNH XÁC (200 OK)**| Endpoint chuẩn ASP.NET Core. |
| **`agent_ide.js:841`**| `POST /api/workspace/folder/create`| `WorkspaceController.cs:CreateFolder`| **KHỚP CHÍNH XÁC (200 OK)**| Endpoint chuẩn ASP.NET Core. |
| **`agent_ide.js:903`**| `POST /api/runtime/run` | `RuntimeController.cs:ExecuteCommand` | **KHỚP CHÍNH XÁC (200 OK)**| Endpoint chuẩn ASP.NET Core. |
| **`agent_ide.js:929`**| `GET /api/runtime/output/{id}` | `RuntimeController.cs:GetSessionOutput`| **KHỚP CHÍNH XÁC (200 OK)**| Endpoint chuẩn ASP.NET Core. |
| **`agent_ide.js:1011`**| `GET /api/workspace/file` | `WorkspaceController.cs:GetFileContent`| **KHỚP CHÍNH XÁC (200 OK)**| Endpoint chuẩn ASP.NET Core. |
| **`agent_ide.js:1037`**| `POST /api/editor/file/save` | `EditorController.cs:SaveFile` | **KHỚP CHÍNH XÁC (200 OK)**| Endpoint chuẩn ASP.NET Core. |
| **`agent_ide.js:1087`**| `GET /ai/api/agent/file/versions` | `VersionController.cs:GetFileHistory` | **Lệch URL** | Route C# là `/api/version/history`. |
| **`agent_ide.js:1115`**| `POST /ai/api/agent/file/rollback` | `VersionController.cs:RollbackVersion`| **Lệch URL** | Route C# là `/api/version/rollback`. |
| **`agent_ide.js:1143`**| `POST /ai/api/agent/analyze` | `AgentController.cs` (`[HttpPost("analyze")]`)| **Lệch URL** | Phân tích project. C# Controller nhận tại `/api/agent/analyze`. |
| **`agent_ide.js:1276`**| `POST /ai/api/agent/apply` | `AgentController.cs` (`[HttpPost("apply")]`)| **Lệch URL** | Áp dụng diff. C# Controller nhận tại `/api/agent/apply`. |
| **`Home/Index.cshtml:579`**| `POST /ai/api/agent/analyze` | `AgentController.cs` (`[HttpPost("analyze")]`)| **Lệch URL** | Phân tích yêu cầu lập trình. |
| **`Home/Index.cshtml:643`**| `POST /ai/api/agent/apply` | `AgentController.cs` (`[HttpPost("apply")]`)| **Lệch URL** | Áp dụng code change. |

> [!CRITICAL] NHẬN XÉT CỐT LÕI TỪ KIẾN TRÚC SƯ
> Dự án đang trong quá trình chuyển đổi (migration) từ backend PHP monolithic sang ASP.NET Core MVC C#.
> - Các chức năng mới của Web IDE trong `agent_ide.js` (tree, file, editor, runtime) đã được chuyển đổi hoàn toàn sang REST API chuẩn C# (`/api/workspace/...`, `/api/runtime/...`, `/api/editor/...`) và hoạt động chính xác.
> - Tuy nhiên, toàn bộ logic Chat, Đăng nhập, Đăng ký, và Face Biometrics trong `app.js` và `SignUp_LogIn_Form.js` vẫn giữ nguyên hardcoded path dạng PHP (`/ai/api/*.php`). Nếu chạy độc lập trên Kestrel / Docker mà không có Nginx rewrite rule hoặc Cloudflare worker routing sang PHP backend cũ, **100% các tính năng này sẽ trả về lỗi HTTP 404**.

---

## PHẦN 2: TOÀN VĂN LUỒNG ĐĂNG NHẬP & FACE BIOMETRICS FRONTEND (ĐÃ CHE SECRET)

### 2.1. `wwwroot/login/SignUp_LogIn_Form.js`
```javascript
// ===== SAFE READY =====
document.addEventListener("DOMContentLoaded", () => {

  const container = document.querySelector('.container');
  const loginForm = document.getElementById('loginForm');
  const registerForm = document.getElementById('registerForm');
  const registerBtn = document.querySelector('.register-btn');
  const loginBtn = document.querySelector('.login-btn');

  // Auto-fill username if available
  const lastUser = localStorage.getItem("last_logged_in_username");
  const usernameInput = document.getElementById("login_username");
  if (lastUser && usernameInput) {
    usernameInput.value = lastUser;
  }

  loginForm.classList.add('active');
  container.classList.remove('active');
  function isMobile(){
    return window.innerWidth <= 768;
  }

window.showRegisterForm = function(){

  if(window.innerWidth <= 768){
    // MOBILE
    registerForm.classList.add('active');
    loginForm.classList.remove('active');
  }else{
    // DESKTOP
    container.classList.add('active');
  }

}

window.showLoginForm = function(){

  if(window.innerWidth <= 768){
    // MOBILE
    loginForm.classList.add('active');
    registerForm.classList.remove('active');
  }else{
    // DESKTOP
    container.classList.remove('active');
  }

}

  // ===== BUTTON PANEL DESKTOP =====
  if(registerBtn){
    registerBtn.onclick = window.showRegisterForm;
  }

  if(loginBtn){
    loginBtn.onclick = window.showLoginForm;
  }

  // ===== DEFAULT MOBILE =====
function setupMobile(){
  if(isMobile()){
    container.classList.remove('active'); // mặc định login
  }
}

  setupMobile();

  // ===== FIX XOAY MÀN HÌNH =====
  window.addEventListener("resize", setupMobile);




const faceEnrollBtnProfile = document.getElementById("face-enroll-btn-profile");

if (faceEnrollBtnProfile) {
  faceEnrollBtnProfile.addEventListener("click", openFaceEnrollModal);
}

// Face Login button
const faceLoginBtn = document.getElementById("face-login-btn");
if (faceLoginBtn) {
  faceLoginBtn.addEventListener("click", loginWithFace);
}

// Face Close button
const faceCloseBtn = document.getElementById("face-close-btn");
if (faceCloseBtn) {
  faceCloseBtn.addEventListener("click", closeFaceEnrollModal);
}

let isAiReady = false;

// Initialize Face-API
async function initFaceAI() {
  try {
    const MODEL_URL = "facelib/models";
    await Promise.all([
      faceapi.nets.tinyFaceDetector.loadFromUri(MODEL_URL),
      faceapi.nets.faceLandmark68Net.loadFromUri(MODEL_URL),
      faceapi.nets.faceRecognitionNet.loadFromUri(MODEL_URL)
    ]);
    isAiReady = true;
    console.log("[FaceAI] Models loaded successfully");
  } catch (err) {
    console.error("[FaceAI] Error loading models:", err);
  }
}

// Start loading models immediately
initFaceAI();

async function stopAllCameras() {
  try {
    const streams = await navigator.mediaDevices.enumerateDevices();
    const tracks = window.localStream ? window.localStream.getTracks() : [];
    // brute force stop all tracks we can find
    const devices = await navigator.mediaDevices.getUserMedia({video: true}).then(s => s.getTracks().forEach(t => t.stop())).catch(() => {});
  } catch(e) {}
}

let isFaceLoginActive = false;
let faceStream = null;
let capturedFaceImage = "";
let autoCaptureInterval = null;
let stableStartTime = null;
let isSavingFace = false;
let autoCaptureCooldown = false;

function setFaceLoginMessage(message, isError = false) {
  const el = document.getElementById("face-login-status");
  if (!el) return;
  el.textContent = message;
  el.style.color = isError ? "#ef4444" : "#10b981";
}

function setFaceEnrollMessage(message, isError = false) {
  const el = document.getElementById("face-enroll-status");
  if (!el) return;
  el.textContent = message;
  el.style.color = isError ? "#ef4444" : "#10b981";
}

function getActiveUsernameInput() {
  // Check which form is active
  const loginForm = document.getElementById('loginForm');
  const registerForm = document.getElementById('registerForm');
  
  if (registerForm && registerForm.classList.contains('active')) {
    return document.getElementById("reg_username");
  }
  
  return document.getElementById("login_username")
    || document.getElementById("username")
    || document.querySelector('input[name="username"]');
}

async function openFaceEnrollModal() {
  if (!isAiReady) {
      alert("Hệ thống AI đang khởi tạo, vui lòng đợi vài giây...");
      return;
  }

  // Use logged in username if profile modal is open
  const isLoggedIn = document.getElementById("chatApp").style.display !== "none";
  let username = "";
  
  if (isLoggedIn) {
      username = localStorage.getItem("username") || "";
  } else {
      const usernameInput = getActiveUsernameInput();
      username = usernameInput ? usernameInput.value.trim() : "";
  }

  if (!username) {
    const isReg = document.getElementById('registerForm')?.classList.contains('active');
    const msg = isReg ? "Vui lòng nhập tên đăng ký trước" : "Vui lòng nhập username trước";
    if (isLoggedIn) {
        alert(msg);
    } else {
        setFaceLoginMessage(msg, true);
    }
    return;
  }

  const modal = document.getElementById("face-enroll-modal");
  const video = document.getElementById("face-video");

  try {
    faceStream = await navigator.mediaDevices.getUserMedia({
      video: { facingMode: "user" },
      audio: false
    });

    video.srcObject = faceStream;
    modal.style.display = "flex";
    capturedFaceImage = "";
    stableStartTime = null;
    isSavingFace = false;
    autoCaptureCooldown = false;

    const scanLine = document.getElementById("scan-line");
    if (scanLine) scanLine.style.display = "block";

    setFaceEnrollMessage("AI đang sẵn sàng, vui lòng nhìn thẳng...");

    startAutoCaptureCheck();
  } catch (err) {
    console.error(err);
    setFaceEnrollMessage("Cần cấp quyền Camera để dùng FaceID", true);
  }
}

function startAutoCaptureCheck() {
  stopAutoCaptureCheck();

  autoCaptureInterval = setInterval(async () => {
    if (isSavingFace || autoCaptureCooldown) return;

    const video = document.getElementById("face-video");
    if (!video || video.readyState < 2) return;

    // REAL FACE DETECTION
    const detection = await faceapi.detectSingleFace(video, new faceapi.TinyFaceDetectorOptions({ inputSize: 224, scoreThreshold: 0.5 }))
                                   .withFaceLandmarks();

    if (!detection) {
      stableStartTime = null;
      setFaceEnrollMessage("Hãy đưa khuôn mặt vào giữa khung tròn");
      return;
    }

    if (!stableStartTime) {
      stableStartTime = Date.now();
      setFaceEnrollMessage("Đã nhận diện khuôn mặt, giữ yên...");
      return;
    }

    const stableMs = Date.now() - stableStartTime;

    if (stableMs >= 1000) {
      autoCaptureCooldown = true;
      setFaceEnrollMessage("✅ Hợp lệ! Đang lưu khuôn mặt...");
      await autoCaptureAndSave();
    }
  }, 300);
}

function stopAutoCaptureCheck() {
  if (autoCaptureInterval) {
    clearInterval(autoCaptureInterval);
    autoCaptureInterval = null;
  }
}

let capturedDescriptor = null;

async function autoCaptureAndSave() {
  if (isSavingFace) return;
  isSavingFace = true;

  const video = document.getElementById("face-video");
  const canvas = document.getElementById("face-canvas");

  if (!video || !canvas) return;

  // 🧠 COMPUTE DESCRIPTOR FOR STORAGE
  const detection = await faceapi.detectSingleFace(video, new faceapi.TinyFaceDetectorOptions({ inputSize: 224, scoreThreshold: 0.5 }))
                                 .withFaceLandmarks()
                                 .withFaceDescriptor();

  if (!detection) {
      isSavingFace = false;
      autoCaptureCooldown = false;
      setFaceEnrollMessage("❌ Không thể lấy mã khuôn mặt, thử lại...", true);
      return;
  }
  
  capturedDescriptor = JSON.stringify(Array.from(detection.descriptor));

  const width = video.videoWidth;
  const height = video.videoHeight;
  canvas.width = width;
  canvas.height = height;

  const ctx = canvas.getContext("2d");
  ctx.drawImage(video, 0, 0, width, height);

  capturedFaceImage = canvas.toDataURL("image/jpeg", 0.9);
  await saveFaceImage();
}

async function saveFaceImage() {
  let username = "";
  const isLoggedIn = document.getElementById("chatApp").style.display !== "none";

  if (isLoggedIn) {
      username = localStorage.getItem("username") || "";
  } else {
      const usernameInput = getActiveUsernameInput();
      username = usernameInput ? usernameInput.value.trim() : "";
  }

  if (!username || !capturedDescriptor) {
    isSavingFace = false;
    autoCaptureCooldown = false;
    setFaceEnrollMessage("Thiếu dữ liệu", true);
    return;
  }

  try {
    const formData = new FormData();
    formData.append("username", username);
    formData.append("image", capturedFaceImage);
    formData.append("descriptor", capturedDescriptor); // 🔒 Biometric reference

    const response = await fetch("ai/api/save_face.php", {
      method: "POST",
      body: formData
    });


    const result = await response.json();

    if (result.success) {
      setFaceEnrollMessage("✅ Đã lưu khuôn mặt thành công!");
      setTimeout(() => closeFaceEnrollModal(), 1500);
    } else {
      isSavingFace = false;
      autoCaptureCooldown = false;
      setFaceEnrollMessage(result.message || "Lỗi khi lưu ảnh", true);
    }
  } catch (err) {
    console.error(err);
    isSavingFace = false;
    autoCaptureCooldown = false;
    setFaceEnrollMessage("Lỗi kết nối máy chủ", true);
  }
}

function closeFaceEnrollModal() {
  stopAutoCaptureCheck();
  const modal = document.getElementById("face-enroll-modal");
  const video = document.getElementById("face-video");
  if (faceStream) {
    faceStream.getTracks().forEach(track => track.stop());
    faceStream = null;
  }
  if (video) video.srcObject = null;
  if (modal) modal.style.display = "none";
  isSavingFace = false;
  autoCaptureCooldown = false;
}

async function loginWithFace() {
  if (isFaceLoginActive) return;
  if (!isAiReady) {
      setFaceLoginMessage("AI đang khởi động, vui lòng đợi...", true);
      return;
  }

  const usernameInput = getActiveUsernameInput();
  const username = usernameInput ? usernameInput.value.trim() : "";

  if (!username) {
    setFaceLoginMessage("Vui lòng nhập username trước", true);
    return;
  }

  try {
    isFaceLoginActive = true;
    setFaceLoginMessage("Đang khởi động camera...");

    const modal = document.getElementById("face-enroll-modal");
    const video = document.getElementById("face-video");

    faceStream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: "user" } });
    video.srcObject = faceStream;
    modal.style.display = "flex";
    
    const scanLine = document.getElementById("scan-line");
    if (scanLine) scanLine.style.display = "block";

    setFaceEnrollMessage("🔍 Đang xác minh danh tính...");
    
    // 🔒 NEW ARCHITECTURE: No client-side image fetching. Final matching on Server.
    const loop = setInterval(async () => {
        if (!isFaceLoginActive) { clearInterval(loop); return; }

        const liveDetection = await faceapi.detectSingleFace(video, new faceapi.TinyFaceDetectorOptions({ inputSize: 224, scoreThreshold: 0.5 }))
                                       .withFaceLandmarks()
                                       .withFaceDescriptor();
        
        if (liveDetection) {
            setFaceEnrollMessage("🛰️ Đang đối soát với máy chủ...");
            
            // Send live data to backend
            const formData = new FormData();
            formData.append("username", username);
            formData.append("descriptor", JSON.stringify(Array.from(liveDetection.descriptor)));

            try {
                const loginRes = await fetch("ai/api/login_face.php", { method: "POST", body: formData });
                const loginData = await loginRes.json();

                if (loginData.success) {
                    clearInterval(loop);
                    setFaceEnrollMessage("✅ Xác thực thành công! Đang vào hệ thống...");
                    localStorage.setItem("user_id", loginData.user_id);
                    localStorage.setItem("username", loginData.username);
                    localStorage.setItem("last_logged_in_username", loginData.username);
                    setTimeout(() => window.location.reload(), 800);
                } else {
                    setFaceEnrollMessage("❌ " + (loginData.message || "Không khớp khuôn mặt"), true);
                }
            } catch (e) {
                console.error("Auth error:", e);
                setFaceEnrollMessage("Lỗi kết nối máy chủ", true);
            }
        } else {
            setFaceEnrollMessage("Hãy nhìn thẳng vào camera");
        }
    }, 1000); 



    // Timeout after 20s
    setTimeout(() => { if (isFaceLoginActive) { clearInterval(loop); setFaceEnrollMessage("Hết thời gian chờ", true); isFaceLoginActive = false; }}, 20000);

  } catch (err) {
    console.error(err);
    setFaceLoginMessage("Lỗi khởi động FaceID", true);
    isFaceLoginActive = false;
  }
}

document.addEventListener("click", (e) => {
    // Listener logic
});

}); // END DOMContentLoaded

```

### 2.2. `Views/Shared/_Layout.cshtml`
```html
﻿<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>@ViewData["Title"] - BtoanAiNet</title>
    <script type="importmap"></script>
    <link rel="stylesheet" href="~/lib/bootstrap/dist/css/bootstrap.min.css" />
    <link rel="stylesheet" href="~/css/site.css" asp-append-version="true" />
    <link rel="stylesheet" href="~/BtoanAiNet.styles.css" asp-append-version="true" />
</head>
<body>
    <header>
        <nav class="navbar navbar-expand-sm navbar-toggleable-sm navbar-light bg-white border-bottom box-shadow mb-3">
            <div class="container-fluid">
                <a class="navbar-brand" asp-area="" asp-controller="Home" asp-action="Index">BtoanAiNet</a>
                <button class="navbar-toggler" type="button" data-bs-toggle="collapse" data-bs-target=".navbar-collapse" aria-controls="navbarSupportedContent"
                        aria-expanded="false" aria-label="Toggle navigation">
                    <span class="navbar-toggler-icon"></span>
                </button>
                <div class="navbar-collapse collapse d-sm-inline-flex justify-content-between">
                    <ul class="navbar-nav flex-grow-1">
                        <li class="nav-item">
                            <a class="nav-link text-dark" asp-area="" asp-controller="Home" asp-action="Index">Home</a>
                        </li>
                        <li class="nav-item">
                            <a class="nav-link text-dark" asp-area="" asp-controller="Home" asp-action="Privacy">Privacy</a>
                        </li>
                    </ul>
                </div>
            </div>
        </nav>
    </header>
    <div class="container">
        <main role="main" class="pb-3">
            @RenderBody()
        </main>
    </div>

    <footer class="border-top footer text-muted">
        <div class="container">
            &copy; 2026 - BtoanAiNet - <a asp-area="" asp-controller="Home" asp-action="Privacy">Privacy</a>
        </div>
    </footer>
    <script src="~/lib/jquery/dist/jquery.min.js"></script>
    <script src="~/lib/bootstrap/dist/js/bootstrap.bundle.min.js"></script>
    <script src="~/js/site.js" asp-append-version="true"></script>
    @await RenderSectionAsync("Scripts", required: false)
</body>
</html>

```

---

## PHẦN 3: TÓM TẮT CẤU TRÚC & CÁC ĐOẠN NGHI LỖI TRONG FRONTEND CHÍNH

### 3.1. Phân tích `wwwroot/js/app.js`
- **Mục đích:** File điều khiển giao diện chính của Chat AI (Btoan AI), quản lý hội thoại, gửi prompt, streaming phản hồi từ Gemini, sinh ảnh/nhạc/video, quản lý lịch sử tìm kiếm, và upload file.
- **Biến toàn cục quan trọng:**
  - `WORKER_URL = "/ai/api/chat.php"` (Dòng 2): URL backend chat chính.
  - `currentConversationId`: ID phiên chat đang kích hoạt.
  - `user_id`: Lưu trữ trong `localStorage.getItem("user_id")`.
  - `attachedFiles`: Mảng lưu file tạm người dùng đính kèm.
- **Các đoạn nghi lỗi kỹ thuật (Suspicious / Buggy Code):**
  1. **Hardcoded PHP endpoints:** Hầu hết các lời gọi AJAX đều trỏ tới `/ai/api/*.php` (như `check_profile.php`, `get_conversations.php`, `rename_conversation.php`). Khi chạy trên Kestrel .NET 10, Kestrel không có PHP FastCGI handler, gây lỗi `404 Not Found`.
  2. **Lỗi `res = await fetch(base64)` tại dòng 1952:**
     ```javascript
     const res = await fetch(base64)
     ```
     Cố gắng `fetch()` trực tiếp một chuỗi Data URL base64 trên trình duyệt một số phiên bản có thể bị chặn bởi chính sách CORS/CSP hoặc giới hạn dung lượng URL (>2MB). Nên chuyển đổi base64 sang Blob bằng helper function thay vì dùng fetch.
  3. **Lưu trữ user_id không an toàn trong `localStorage`:**
     Hệ thống phụ thuộc vào `localStorage.getItem("user_id")` gửi qua Query String (`?user_id=...`). Bất kỳ client nào cũng có thể sửa `localStorage` thành user_id khác để đọc toàn bộ lịch sử chat của người dùng khác (Insecure Direct Object Reference - IDOR). Cần chuyển sang sử dụng ASP.NET Core Cookie Authentication hoặc JWT Bearer Token trong Header `Authorization`.

---

### 3.2. Phân tích `wwwroot/js/agent_ide.js`
- **Mục đích:** Quản lý giao diện Web IDE lập trình của AI Agent: Cây thư mục file explorer, editor Monaco/CodeMirror, terminal điều khiển, diff viewer, và quản lý project.
- **Biến toàn cục quan trọng:**
  - `activeProjectId`: ID của project đang mở trong workspace.
  - `activeFilePath`: Đường dẫn file đang được mở trong editor.
  - `sessionId`: Session terminal hiện tại.
- **Các đoạn nghi lỗi kỹ thuật (Suspicious / Buggy Code):**
  1. **Sự pha trộn giữa REST API C# và PHP:**
     - Các dòng 583, 758, 782, 825, 841, 903, 1011, 1037 gọi đúng REST API C# (`/api/workspace/...`, `/api/runtime/...`, `/api/editor/...`).
     - Nhưng các dòng 351, 507, 534, 553, 1087, 1115, 1143, 1276 lại gọi `/ai/api/agent/...`. Nếu Kestrel chưa cấu hình rewrite, tính năng Agent Analyze và Rollback version sẽ fail 404.
  2. **Thiếu cơ chế tự động reconnect SignalR TerminalHub:**
     Khi kết nối websocket tới `TerminalHub` bị gián đoạn do sleep máy hoặc mạng chập chờn, terminal không tự động bắt lại phiên process mà phải reload lại toàn bộ trang.

---

### 3.3. Phân tích `Views/Home/Index.cshtml` & `Views/Agent/Index.cshtml`
- **`Views/Home/Index.cshtml`:**
  - Chứa cấu trúc HTML cho Chat Drawer, Main Chat Canvas, Prompt Box, Voice Input Modal, và Diff Modal.
  - Tại dòng 579 và 643 có inline JavaScript gọi `fetch("/ai/api/agent/analyze")` và `fetch("/ai/api/agent/apply")`. Logic này bị trùng lặp với `agent_ide.js`, gây phân mảnh mã nguồn client-side.
- **`Views/Agent/Index.cshtml`:**
  - View đại diện cho không gian làm việc Multi-Agent Workspace. Nạp các thư viện client: FontAwesome, Monaco Editor loader, xterm.js (terminal), DiffPlex visualizer.
  - Cần đảm bảo các script được nạp theo đúng thứ tự phụ thuộc (Dependencies Order) để tránh lỗi `monaco is not defined` hoặc `Terminal is not defined`.

---
