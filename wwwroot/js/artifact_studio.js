/**
 * Btoan AI - Artifact Studio & Split-Pane Preview Panel
 * Supports Word (.docx), PDF (.pdf), Excel (.xlsx), PowerPoint (.pptx), Bundle (.zip)
 */

window.currentActiveArtifact = null;
window.currentSpreadsheetData = null;
window.currentSpreadsheetSheetIdx = 0;
window.currentSlideDeckData = null;
window.currentSlideIndex = 0;
window.inlineCanvasArtifacts = {};

// Extract [ARTIFACT type="..." title="..."]content[/ARTIFACT] blocks
function extractArtifacts(text) {
    if (!text) return [];
    const artifacts = [];
    const regex = /\[ARTIFACT(?:\s+type=["']([^"']+)["'])?(?:\s+title=["']([^"']+)["'])?\]([\s\S]*?)\[\/ARTIFACT\]/gi;
    let match;
    while ((match = regex.exec(text)) !== null) {
        const rawType = (match[1] || "docx").toLowerCase().trim();
        const type = normalizeArtifactType(rawType);
        const title = (match[2] || "Tài liệu").trim();
        const content = (match[3] || "").trim();
        artifacts.push({ type, title, content });
    }
    return artifacts;
}

function normalizeArtifactType(rawType) {
    if (rawType === "word" || rawType === "doc") return "docx";
    if (rawType === "excel" || rawType === "xls" || rawType === "sheet") return "xlsx";
    if (rawType === "ppt" || rawType === "powerpoint" || rawType === "slide" || rawType === "presentation") return "pptx";
    return rawType;
}

function getArtifactBadgeInfo(type) {
    switch (type) {
        case "docx":
            return { icon: "bi-file-earmark-word-fill", label: "Document · DOCX", cssClass: "docx" };
        case "pdf":
            return { icon: "bi-file-earmark-pdf-fill", label: "Document · PDF", cssClass: "pdf" };
        case "xlsx":
            return { icon: "bi-file-earmark-excel-fill", label: "Spreadsheet · XLSX", cssClass: "xlsx" };
        case "pptx":
            return { icon: "bi-file-earmark-ppt-fill", label: "Presentation · PPTX", cssClass: "pptx" };
        default:
            return { icon: "bi-file-earmark-text-fill", label: "File · " + type.toUpperCase(), cssClass: "docx" };
    }
}

// Render Artifact Cards or Gemini Inline Canvas inside bot message bubble
function renderArtifactCards(bubble, artifacts) {
    if (!bubble || !artifacts || artifacts.length === 0) return;

    // RULE: Khi AI chỉ xuất 1 tài liệu văn bản (docx hoặc pdf) -> hiển thị trực tiếp Inline Document Canvas Card chuẩn Gemini!
    if (artifacts.length === 1 && (artifacts[0].type === "docx" || artifacts[0].type === "pdf")) {
        renderGeminiInlineCanvas(bubble, artifacts[0]);
        return;
    }

    // Khi AI xuất Excel (.xlsx), PowerPoint (.pptx) hoặc từ 2 file trở lên -> hiển thị Artifact Cards + Split-Pane Claude
    let container = bubble.querySelector(".artifact-cards-container");
    if (!container) {
        container = document.createElement("div");
        container.className = "artifact-cards-container";
        bubble.appendChild(container);
    } else {
        container.innerHTML = "";
    }

    artifacts.forEach((art, index) => {
        const info = getArtifactBadgeInfo(art.type);
        const card = document.createElement("div");
        card.className = "artifact-card";
        card.title = `Nhấn để xem trước ${art.title}`;
        card.innerHTML = `
            <div class="artifact-card-left">
                <div class="artifact-card-icon ${info.cssClass}">
                    <i class="bi ${info.icon}"></i>
                </div>
                <div class="artifact-card-info">
                    <div class="artifact-card-title">${escapeHtml(art.title)}</div>
                    <div class="artifact-card-subtitle">${info.label}</div>
                </div>
            </div>
            <button class="artifact-card-btn-download" title="Tải xuống ${art.type.toUpperCase()}">
                <i class="bi bi-download"></i>
            </button>
        `;

        // Click on card body opens preview
        card.onclick = () => openArtifactPreview(art);

        // Click download button triggers direct file export
        const dlBtn = card.querySelector(".artifact-card-btn-download");
        dlBtn.onclick = (e) => {
            e.stopPropagation();
            downloadArtifactDirect(art, dlBtn);
        };

        container.appendChild(card);
    });

    // If 2 or more artifacts, provide a "Download all (.zip)" button
    if (artifacts.length >= 2) {
        const bundleBtn = document.createElement("button");
        bundleBtn.className = "artifact-bundle-download-btn";
        bundleBtn.innerHTML = `<i class="bi bi-file-earmark-zip-fill fs-6"></i> <span>Download all (.zip)</span>`;
        bundleBtn.onclick = () => downloadArtifactBundle(artifacts, bundleBtn);
        container.appendChild(bundleBtn);
    }

    // Auto open first artifact on wide desktop screen (>= 1200px)
    if (window.innerWidth >= 1200 && artifacts.length > 0) {
        setTimeout(() => {
            openArtifactPreview(artifacts[0]);
        }, 150);
    }
}

// 🌟 GEMINI INLINE CANVAS CARD (HIỂN THỊ TRỰC TIẾP TRONG CHAT CHO 1 TÀI LIỆU VĂN BẢN)
function renderGeminiInlineCanvas(bubble, artifact) {
    if (!bubble || !artifact) return;

    let canvasEl = bubble.querySelector(".gemini-inline-canvas-card");
    if (canvasEl) canvasEl.remove();

    const canvasId = "canvas_" + Date.now() + "_" + Math.floor(Math.random() * 1000);
    const canvasCard = document.createElement("div");
    canvasCard.className = "gemini-inline-canvas-card";
    canvasCard.id = canvasId;

    let renderedHtml = "";
    try {
        if (typeof marked !== "undefined") {
            renderedHtml = marked.parse(artifact.content || "");
        } else {
            renderedHtml = `<pre>${escapeHtml(artifact.content)}</pre>`;
        }
    } catch {
        renderedHtml = `<pre>${escapeHtml(artifact.content)}</pre>`;
    }

    canvasCard.innerHTML = `
        <div class="gemini-canvas-header">
            <div class="d-flex align-items-center gap-2 overflow-hidden">
                <i class="bi bi-file-earmark-richtext-fill text-warning fs-6"></i>
                <span class="fw-semibold text-white small text-truncate" style="max-width: 260px;" title="${escapeHtml(artifact.title)}">${escapeHtml(artifact.title)}</span>
            </div>
            <div class="d-flex align-items-center gap-1.5">
                <button class="btn btn-dark p-1" onclick="document.execCommand('undo')" title="Hoàn tác (Undo)">
                    <i class="bi bi-arrow-counterclockwise"></i>
                </button>
                <button class="btn btn-dark p-1" onclick="document.execCommand('redo')" title="Làm lại (Redo)">
                    <i class="bi bi-arrow-clockwise"></i>
                </button>
                <div class="dropdown d-inline-block">
                    <button class="btn btn-sm btn-primary rounded-pill px-2.5 py-0.5 d-flex align-items-center gap-1 extra-small fw-semibold shadow-sm" data-bs-toggle="dropdown" aria-expanded="false">
                        <span>Xuất</span> <i class="bi bi-chevron-down" style="font-size: 0.65rem;"></i>
                    </button>
                    <ul class="dropdown-menu dropdown-menu-end shadow border-0 rounded-3 p-1 mt-1" style="min-width: 170px; z-index: 1060;">
                        <li>
                            <button class="dropdown-item py-1.5 px-3 small d-flex align-items-center gap-2" onclick="exportInlineCanvas('${canvasId}', 'pdf')">
                                <i class="bi bi-file-earmark-pdf text-danger"></i> PDF (.pdf)
                            </button>
                        </li>
                        <li>
                            <button class="dropdown-item py-1.5 px-3 small d-flex align-items-center gap-2" onclick="exportInlineCanvas('${canvasId}', 'docx')">
                                <i class="bi bi-file-earmark-word text-primary"></i> Word (.docx)
                            </button>
                        </li>
                        <li><hr class="dropdown-divider my-1"></li>
                        <li>
                            <button class="dropdown-item py-1.5 px-3 small d-flex align-items-center gap-2" onclick="copyInlineCanvasMarkdown('${canvasId}')">
                                <i class="bi bi-clipboard"></i> Sao chép Markdown
                            </button>
                        </li>
                    </ul>
                </div>
                <button class="btn btn-dark p-1" onclick="openArtifactPreview(window.inlineCanvasArtifacts['${canvasId}'])" title="Mở Split-Pane bên phải">
                    <i class="bi bi-layout-sidebar-reverse"></i>
                </button>
                <button class="btn btn-dark p-1" onclick="this.closest('.gemini-inline-canvas-card').remove()" title="Đóng thẻ">
                    <i class="bi bi-x-lg"></i>
                </button>
            </div>
        </div>
        <div class="gemini-canvas-body" id="${canvasId}_body" contenteditable="true" spellcheck="false" title="Nhấn vào đây để chỉnh sửa chữ trực tiếp trước khi xuất file">
            <div class="extra-small fw-bold text-uppercase mb-1" style="color: #d97706; letter-spacing: 1px;">BÁO CÁO TỔNG QUAN</div>
            <h2 class="fw-bold mb-2" style="color: #0F4C5C;">${escapeHtml(artifact.title)}</h2>
            <hr style="border: 0; height: 2px; background: #0F4C5C; opacity: 1; margin: 8px 0 16px 0;">
            <div class="gemini-canvas-inner-content">${renderedHtml}</div>
        </div>
    `;

    if (!window.inlineCanvasArtifacts) window.inlineCanvasArtifacts = {};
    window.inlineCanvasArtifacts[canvasId] = artifact;

    bubble.appendChild(canvasCard);

    if (window.MathJax) {
        try { MathJax.typesetPromise([canvasCard]).catch(() => {}); } catch {}
    }
}

// Xuất file từ Canvas với nội dung đã được người dùng chỉnh sửa trực tiếp
async function exportInlineCanvas(canvasId, exportType) {
    const bodyEl = document.getElementById(canvasId + "_body");
    const art = window.inlineCanvasArtifacts ? window.inlineCanvasArtifacts[canvasId] : null;
    if (!bodyEl || !art) return;

    const innerContentEl = bodyEl.querySelector(".gemini-canvas-inner-content") || bodyEl;
    const editedText = innerContentEl.innerText || innerContentEl.textContent || art.content;

    const payload = {
        type: exportType,
        title: art.title,
        content: editedText
    };

    try {
        const res = await fetch("/api/file/export-artifact", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(payload)
        });
        const data = await res.json();
        if (data.success && data.url) {
            triggerBrowserDownload(data.url, data.filename);
        } else {
            alert("Lỗi xuất file: " + (data.error || "Không xác định"));
        }
    } catch (e) {
        alert("Lỗi kết nối tải file: " + e.message);
    }
}

function copyInlineCanvasMarkdown(canvasId) {
    const bodyEl = document.getElementById(canvasId + "_body");
    const art = window.inlineCanvasArtifacts ? window.inlineCanvasArtifacts[canvasId] : null;
    if (!bodyEl) return;
    const innerContentEl = bodyEl.querySelector(".gemini-canvas-inner-content") || bodyEl;
    const textToCopy = innerContentEl.innerText || innerContentEl.textContent || (art ? art.content : "");
    navigator.clipboard.writeText(textToCopy).then(() => {
        alert("Đã sao chép nội dung vào Clipboard!");
    });
}

// Split-Pane Preview Panel Opening
function openArtifactPreview(artifact) {
    if (!artifact) return;
    window.currentActiveArtifact = artifact;

    const panel = document.getElementById("artifactSplitPanel");
    const wrapper = document.querySelector(".main-app-wrapper");
    if (!panel) return;

    // Header info
    const info = getArtifactBadgeInfo(artifact.type);
    const iconEl = document.getElementById("artHeaderIcon");
    const titleEl = document.getElementById("artHeaderTitle");
    const subEl = document.getElementById("artHeaderSubtitle");
    const dlBtn = document.getElementById("artDownloadBtn");
    const tabsGroup = document.getElementById("artSpreadsheetTabs");

    if (iconEl) iconEl.innerHTML = `<i class="bi ${info.icon} text-${info.cssClass === 'docx' ? 'primary' : info.cssClass === 'pdf' ? 'danger' : info.cssClass === 'xlsx' ? 'success' : 'warning'}"></i>`;
    if (titleEl) titleEl.innerText = artifact.title || "Tài liệu";
    if (subEl) subEl.innerText = info.label;

    if (dlBtn) {
        dlBtn.onclick = () => downloadArtifactDirect(artifact, dlBtn);
    }

    // Toggle spreadsheet tab switcher (Data / Chart)
    if (tabsGroup) {
        tabsGroup.style.display = (artifact.type === "xlsx") ? "inline-flex" : "none";
        switchSpreadsheetView("data");
    }

    // Display panel
    panel.style.display = "flex";
    if (wrapper && window.innerWidth >= 1200) {
        wrapper.classList.add("has-artifact");
    }

    // Render body content
    renderArtifactPreviewBody(artifact);
}

function closeArtifactPanel() {
    const panel = document.getElementById("artifactSplitPanel");
    const wrapper = document.querySelector(".main-app-wrapper");
    if (panel) {
        panel.style.display = "none";
        panel.classList.remove("fullscreen");
    }
    if (wrapper) {
        wrapper.classList.remove("has-artifact");
    }
    const icon = document.getElementById("artFullscreenIcon");
    if (icon) {
        icon.className = "bi bi-arrows-fullscreen text-secondary";
    }
}

function toggleArtifactFullscreen() {
    const panel = document.getElementById("artifactSplitPanel");
    const icon = document.getElementById("artFullscreenIcon");
    if (!panel) return;

    panel.classList.toggle("fullscreen");
    if (panel.classList.contains("fullscreen")) {
        if (icon) icon.className = "bi bi-fullscreen-exit text-secondary";
    } else {
        if (icon) icon.className = "bi bi-arrows-fullscreen text-secondary";
    }
}

// Render Preview Body
function renderArtifactPreviewBody(artifact) {
    const bodyEl = document.getElementById("artPanelBody");
    if (!bodyEl) return;
    bodyEl.innerHTML = "";

    switch (artifact.type) {
        case "docx":
        case "pdf":
            renderDocumentPreview(bodyEl, artifact);
            break;
        case "xlsx":
            renderSpreadsheetPreview(bodyEl, artifact);
            break;
        case "pptx":
            renderPresentationPreview(bodyEl, artifact);
            break;
        default:
            renderDocumentPreview(bodyEl, artifact);
            break;
    }
}

// 1. Document Preview (.docx / .pdf)
function renderDocumentPreview(container, artifact) {
    const wrapper = document.createElement("div");
    wrapper.className = "w-100";

    // Top action bar
    const bar = document.createElement("div");
    bar.className = "d-flex justify-content-end gap-2 mb-2";
    bar.innerHTML = `
        <button class="btn btn-sm btn-outline-primary rounded-pill px-3 py-1" onclick="exportArtifactByType('docx')">
            <i class="bi bi-file-earmark-word me-1"></i> Tải Word (.docx)
        </button>
        <button class="btn btn-sm btn-outline-danger rounded-pill px-3 py-1" onclick="exportArtifactByType('pdf')">
            <i class="bi bi-file-earmark-pdf me-1"></i> Tải PDF (.pdf)
        </button>
    `;
    wrapper.appendChild(bar);

    const docPage = document.createElement("div");
    docPage.className = "doc-page-view";

    let mdHtml = "";
    try {
        if (typeof marked !== "undefined") {
            mdHtml = marked.parse(artifact.content || "");
        } else {
            mdHtml = `<pre>${escapeHtml(artifact.content)}</pre>`;
        }
    } catch {
        mdHtml = `<pre>${escapeHtml(artifact.content)}</pre>`;
    }

    docPage.innerHTML = `
        <h1 class="mb-3">${escapeHtml(artifact.title)}</h1>
        <div class="text-muted extra-small mb-4 fst-italic">Btoan AI Studio • ${new Date().toLocaleDateString('vi-VN')}</div>
        <div class="doc-body-content">${mdHtml}</div>
    `;

    wrapper.appendChild(docPage);
    container.appendChild(wrapper);

    // Typeset MathJax if present
    if (window.MathJax) {
        try { MathJax.typesetPromise([docPage]).catch(() => {}); } catch {}
    }
}

// 2. Spreadsheet Preview (.xlsx)
function renderSpreadsheetPreview(container, artifact) {
    let sheetData = null;
    try {
        sheetData = JSON.parse(artifact.content);
    } catch {
        sheetData = parseMarkdownTableToSheet(artifact.title, artifact.content);
    }

    if (!sheetData || !sheetData.sheets || sheetData.sheets.length === 0) {
        sheetData = {
            title: artifact.title || "BẢNG DỮ LIỆU",
            subtitle: "Dữ liệu xuất",
            sheets: [{ name: "Sheet1", columns: ["STT", "Nội dung"], rows: [[1, artifact.content]] }]
        };
    }

    window.currentSpreadsheetData = sheetData;
    window.currentSpreadsheetSheetIdx = 0;

    const spreadsheetWrapper = document.createElement("div");
    spreadsheetWrapper.className = "spreadsheet-container";
    spreadsheetWrapper.id = "spreadsheetMainContainer";

    // Formula bar
    const formulaBar = document.createElement("div");
    formulaBar.className = "formula-bar-container";
    formulaBar.innerHTML = `
        <div class="fw-bold text-muted extra-small me-1">fx</div>
        <div id="artCellAddress" class="cell-address-badge">A1</div>
        <input type="text" id="artCellFormula" class="formula-input" readonly value="">
    `;
    spreadsheetWrapper.appendChild(formulaBar);

    // Data grid / Canvas chart container
    const viewContainer = document.createElement("div");
    viewContainer.id = "artSpreadsheetViewArea";
    viewContainer.className = "excel-grid-wrapper";
    spreadsheetWrapper.appendChild(viewContainer);

    // Sheet tabs bar
    const sheetTabs = document.createElement("div");
    sheetTabs.className = "sheet-tabs-bar";
    sheetData.sheets.forEach((s, idx) => {
        const btn = document.createElement("button");
        btn.className = `sheet-tab-btn ${idx === 0 ? "active" : ""}`;
        btn.innerText = s.name || `Sheet${idx + 1}`;
        btn.onclick = () => {
            window.currentSpreadsheetSheetIdx = idx;
            sheetTabs.querySelectorAll(".sheet-tab-btn").forEach((b, i) => b.classList.toggle("active", i === idx));
            renderActiveSheetGrid(viewContainer, sheetData, idx);
        };
        sheetTabs.appendChild(btn);
    });
    spreadsheetWrapper.appendChild(sheetTabs);

    container.appendChild(spreadsheetWrapper);

    // Render active sheet grid
    renderActiveSheetGrid(viewContainer, sheetData, 0);
}

function renderActiveSheetGrid(container, data, sheetIdx) {
    container.innerHTML = "";
    const sheet = data.sheets[sheetIdx] || data.sheets[0];
    const cols = sheet.columns || [];
    const rows = sheet.rows || [];

    const table = document.createElement("table");
    table.className = "excel-table";

    // 1. Column letters row (A, B, C...)
    const colHeaderTr = document.createElement("tr");
    colHeaderTr.innerHTML = `<th class="excel-row-num"></th>` + cols.map((_, i) => `<th class="excel-col-header">${getExcelColLetter(i)}</th>`).join("");
    table.appendChild(colHeaderTr);

    // 2. Title Row (merged)
    if (data.title) {
        const titleTr = document.createElement("tr");
        titleTr.className = "title-row";
        titleTr.innerHTML = `<td class="excel-row-num">1</td><td colspan="${cols.length}">${escapeHtml(data.title)}</td>`;
        table.appendChild(titleTr);
    }

    // 3. Subtitle Row (merged)
    if (data.subtitle) {
        const subTr = document.createElement("tr");
        subTr.innerHTML = `<td class="excel-row-num">2</td><td colspan="${cols.length}" class="text-center text-muted extra-small fst-italic py-1">${escapeHtml(data.subtitle)}</td>`;
        table.appendChild(subTr);
    }

    // 4. Spacer row
    const spaceTr = document.createElement("tr");
    spaceTr.innerHTML = `<td class="excel-row-num">3</td><td colspan="${cols.length}" style="height: 12px; background: #fff;"></td>`;
    table.appendChild(spaceTr);

    // 5. Column Headers (Navy blue #0F4C5C)
    const dataHeaderTr = document.createElement("tr");
    dataHeaderTr.className = "header-row";
    dataHeaderTr.innerHTML = `<th class="excel-row-num">4</th>` + cols.map(c => `<th>${escapeHtml(c)}</th>`).join("");
    table.appendChild(dataHeaderTr);

    // 6. Data rows
    rows.forEach((r, rIdx) => {
        const tr = document.createElement("tr");
        const rowNum = rIdx + 5;
        let tdHtml = `<td class="excel-row-num">${rowNum}</td>`;

        let rArr = [];
        if (Array.isArray(r)) {
            rArr = r;
        } else if (r && typeof r === "object") {
            // Map object keys reliably to columns
            rArr = cols.map(c => {
                if (r[c] !== undefined) return r[c];
                const matchKey = Object.keys(r).find(k => k.trim().toLowerCase() === c.trim().toLowerCase());
                return matchKey ? r[matchKey] : "";
            });
        }

        const isEven = rIdx % 2 === 1;
        const rowBg = isEven ? '#f8fafc' : '#ffffff';

        cols.forEach((_, cIdx) => {
            const val = rArr[cIdx] !== undefined && rArr[cIdx] !== null ? rArr[cIdx] : "";
            const colLetter = getExcelColLetter(cIdx);
            const addr = `${colLetter}${rowNum}`;
            const isNum = typeof val === "number" || (!isNaN(val) && val !== "");
            const alignClass = cIdx === 0 ? "text-center" : (isNum ? "text-end" : "text-start");
            tdHtml += `<td class="${alignClass}" style="color: #0f172a !important; background-color: ${rowBg}; opacity: 1 !important; font-weight: 500;" data-addr="${addr}" data-val="${escapeHtml(String(val))}">${escapeHtml(String(val))}</td>`;
        });
        tr.innerHTML = tdHtml;
        table.appendChild(tr);
    });

    container.appendChild(table);

    // Add cell click interaction
    table.querySelectorAll("td[data-addr]").forEach(cell => {
        cell.onclick = () => {
            table.querySelectorAll("td.selected").forEach(c => c.classList.remove("selected"));
            cell.classList.add("selected");
            const addrEl = document.getElementById("artCellAddress");
            const formEl = document.getElementById("artCellFormula");
            if (addrEl) addrEl.innerText = cell.dataset.addr;
            if (formEl) formEl.value = cell.dataset.val;
        };
    });

    // Default select first data cell
    const firstCell = table.querySelector("td[data-addr]");
    if (firstCell) firstCell.click();
}

function switchSpreadsheetView(viewType) {
    const btnData = document.getElementById("artTabBtnData");
    const btnChart = document.getElementById("artTabBtnChart");
    const viewArea = document.getElementById("artSpreadsheetViewArea");
    if (!viewArea || !window.currentSpreadsheetData) return;

    if (btnData) btnData.classList.toggle("active", viewType === "data");
    if (btnChart) btnChart.classList.toggle("active", viewType === "chart");

    if (viewType === "data") {
        renderActiveSheetGrid(viewArea, window.currentSpreadsheetData, window.currentSpreadsheetSheetIdx);
    } else {
        renderSpreadsheetChart(viewArea, window.currentSpreadsheetData, window.currentSpreadsheetSheetIdx);
    }
}

function renderSpreadsheetChart(container, data, sheetIdx) {
    container.innerHTML = "";
    const sheet = data.sheets[sheetIdx] || data.sheets[0];
    const cols = sheet.columns || [];
    const rows = sheet.rows || [];

    // Find numeric column
    let numColIdx = -1;
    let labelColIdx = 0;

    for (let c = 0; c < cols.length; c++) {
        let hasNum = rows.some(r => {
            const v = Array.isArray(r) ? r[c] : Object.values(r)[c];
            return typeof v === "number" || (!isNaN(parseFloat(v)) && isFinite(v));
        });
        if (hasNum && c > 0) {
            numColIdx = c;
            break;
        }
    }

    if (numColIdx === -1) {
        container.innerHTML = `<div class="p-4 text-center text-muted">Bảng dữ liệu này không chứa cột số phù hợp để vẽ biểu đồ tự động.</div>`;
        return;
    }

    const labels = [];
    const values = [];

    rows.forEach(r => {
        const arr = Array.isArray(r) ? r : Object.values(r);
        labels.push(String(arr[labelColIdx] || ""));
        const v = parseFloat(arr[numColIdx]);
        values.push(isNaN(v) ? 0 : v);
    });

    const chartBox = document.createElement("div");
    chartBox.className = "p-3 d-flex flex-column align-items-center w-100";
    chartBox.innerHTML = `
        <div class="fw-bold text-dark mb-2">${escapeHtml(data.title || "Biểu đồ trực quan")} (${cols[numColIdx]})</div>
        <canvas id="artifactChartCanvas" width="560" height="300" style="max-width: 100%; height: auto; background: #fff; border-radius: 8px; border: 1px solid #e2e8f0;"></canvas>
    `;
    container.appendChild(chartBox);

    setTimeout(() => {
        const canvas = document.getElementById("artifactChartCanvas");
        if (canvas) drawBarChart(canvas, labels, values, cols[numColIdx]);
    }, 50);
}

function drawBarChart(canvas, labels, values, title) {
    const ctx = canvas.getContext("2d");
    if (!ctx) return;

    ctx.clearRect(0, 0, canvas.width, canvas.height);
    const maxVal = Math.max(...values, 10);
    const pad = 40;
    const chartW = canvas.width - pad * 2;
    const chartH = canvas.height - pad * 2;
    const barW = Math.max(16, (chartW / values.length) - 16);

    // Draw axes
    ctx.strokeStyle = "#cbd5e1";
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(pad, pad);
    ctx.lineTo(pad, canvas.height - pad);
    ctx.lineTo(canvas.width - pad, canvas.height - pad);
    ctx.stroke();

    // Draw bars
    values.forEach((val, i) => {
        const barH = (val / maxVal) * chartH;
        const x = pad + 10 + i * (barW + 16);
        const y = canvas.height - pad - barH;

        // Gradient bar in navy
        const grad = ctx.createLinearGradient(0, y, 0, canvas.height - pad);
        grad.addColorStop(0, "#0F4C5C");
        grad.addColorStop(1, "#1E3A8A");
        ctx.fillStyle = grad;
        ctx.fillRect(x, y, barW, barH);

        // Value text
        ctx.fillStyle = "#0F4C5C";
        ctx.font = "bold 11px sans-serif";
        ctx.textAlign = "center";
        ctx.fillText(String(val), x + barW / 2, y - 5);

        // Label text
        ctx.fillStyle = "#64748b";
        ctx.font = "10px sans-serif";
        const shortLabel = labels[i].length > 10 ? labels[i].substring(0, 8) + "…" : labels[i];
        ctx.fillText(shortLabel, x + barW / 2, canvas.height - pad + 15);
    });
}

function parseMarkdownTableToSheet(title, content) {
    if (!content) return null;
    
    // 1. Try pipe table | ... | ... |
    const pipeLines = content.split("\n").filter(l => l.trim().includes("|"));
    if (pipeLines.length >= 2) {
        const parseLine = l => l.split("|").map(c => c.trim()).filter(c => c.length > 0);
        const headerIndex = pipeLines.findIndex(l => !l.includes("---"));
        if (headerIndex !== -1 && headerIndex + 1 < pipeLines.length) {
            const cols = parseLine(pipeLines[headerIndex]);
            const rows = [];
            for (let i = headerIndex + 1; i < pipeLines.length; i++) {
                if (pipeLines[i].includes("---")) continue;
                const cells = parseLine(pipeLines[i]);
                if (cells.length > 0) {
                    const rowVals = cells.map(c => {
                        const n = parseFloat(c);
                        return !isNaN(n) && c.trim() === String(n) ? n : c;
                    });
                    rows.push(rowVals);
                }
            }
            if (cols.length > 0 && rows.length > 0) {
                return {
                    title: title || "BẢNG DỮ LIỆU",
                    subtitle: `Xuất bản: ${new Date().toLocaleDateString('vi-VN')}`,
                    sheets: [{ name: "BaoCao", columns: cols, rows: rows }]
                };
            }
        }
    }

    // 2. Try Tab or multi-space separated table
    const rawLines = content.split("\n").map(l => l.trim()).filter(l => l.length > 0 && !l.startsWith("```") && !l.startsWith("#"));
    if (rawLines.length >= 2) {
        const splitRegex = /\t+|\s{2,}/;
        const potentialCols = rawLines[0].split(splitRegex).map(c => c.trim()).filter(Boolean);
        if (potentialCols.length >= 2) {
            const rows = [];
            for (let i = 1; i < rawLines.length; i++) {
                const cells = rawLines[i].split(splitRegex).map(c => c.trim()).filter(Boolean);
                if (cells.length > 0) {
                    rows.push(cells.map(c => {
                        const n = parseFloat(c);
                        return !isNaN(n) && c.trim() === String(n) ? n : c;
                    }));
                }
            }
            if (rows.length > 0) {
                return {
                    title: title || "BẢNG DỮ LIỆU",
                    subtitle: `Xuất bản: ${new Date().toLocaleDateString('vi-VN')}`,
                    sheets: [{ name: "BaoCao", columns: potentialCols, rows: rows }]
                };
            }
        }
    }

    return null;
}

// 3. Presentation Preview (.pptx)
function renderPresentationPreview(container, artifact) {
    let deckData = null;
    try {
        deckData = JSON.parse(artifact.content);
    } catch {
        deckData = parseMarkdownToSlideDeck(artifact.title, artifact.content);
    }

    if (!deckData || !deckData.slides || deckData.slides.length === 0) {
        deckData = {
            title: artifact.title || "Btoan AI Presentation",
            subtitle: "Bài thuyết trình",
            slides: [{ title: "Nội dung", bullets: [artifact.content] }]
        };
    }

    window.currentSlideDeckData = deckData;
    window.currentSlideIndex = 0;

    const presWrapper = document.createElement("div");
    presWrapper.className = "presentation-container";

    // 16:9 Slide Stage
    const stage = document.createElement("div");
    stage.className = "slide-stage-wrapper";
    stage.id = "artSlideStage";
    presWrapper.appendChild(stage);

    // Slide Controls (Prev, Index, Next)
    const controls = document.createElement("div");
    controls.className = "slide-deck-controls";
    controls.innerHTML = `
        <button class="btn btn-sm btn-light border-0" id="slidePrevBtn" title="Slide trước"><i class="bi bi-chevron-left"></i></button>
        <span id="slideCounterText" class="fw-bold small px-2">1 / ${deckData.slides.length + 1}</span>
        <button class="btn btn-sm btn-light border-0" id="slideNextBtn" title="Slide sau"><i class="bi bi-chevron-right"></i></button>
    `;
    presWrapper.appendChild(controls);

    // Thumbnails Row
    const thumbsRow = document.createElement("div");
    thumbsRow.className = "slide-thumbs-row";
    thumbsRow.id = "artSlideThumbs";

    const totalSlides = deckData.slides.length + 1;
    for (let i = 0; i < totalSlides; i++) {
        const thumb = document.createElement("div");
        thumb.className = `slide-thumb-card ${i === 0 ? "active" : ""}`;
        thumb.innerText = `${i + 1}`;
        thumb.title = i === 0 ? "Trang bìa" : `Slide ${i}: ${deckData.slides[i - 1]?.title || ""}`;
        thumb.onclick = () => jumpToSlide(i);
        thumbsRow.appendChild(thumb);
    }
    presWrapper.appendChild(thumbsRow);

    container.appendChild(presWrapper);

    // Wire navigation buttons
    const prevBtn = controls.querySelector("#slidePrevBtn");
    const nextBtn = controls.querySelector("#slideNextBtn");
    if (prevBtn) prevBtn.onclick = () => jumpToSlide(window.currentSlideIndex - 1);
    if (nextBtn) nextBtn.onclick = () => jumpToSlide(window.currentSlideIndex + 1);

    // Render initial slide (Slide 0 is Title Slide)
    jumpToSlide(0);
}

function jumpToSlide(index) {
    const deck = window.currentSlideDeckData;
    if (!deck) return;

    const total = deck.slides.length + 1;
    if (index < 0) index = 0;
    if (index >= total) index = total - 1;

    window.currentSlideIndex = index;

    const stage = document.getElementById("artSlideStage");
    const counter = document.getElementById("slideCounterText");
    const thumbs = document.getElementById("artSlideThumbs");

    if (counter) counter.innerText = `${index + 1} / ${total}`;

    if (thumbs) {
        thumbs.querySelectorAll(".slide-thumb-card").forEach((t, i) => {
            t.classList.toggle("active", i === index);
        });
    }

    if (!stage) return;

    if (index === 0) {
        // Slide 1: Modern Navy Title Slide
        stage.innerHTML = `
            <div class="h-100 d-flex flex-column justify-content-center align-items-center text-center p-4" style="background: #0F4C5C; color: #fff;">
                <div class="badge rounded-pill bg-white text-dark px-3 py-1 mb-3 extra-small fw-bold">PRESENTATION</div>
                <h1 class="fw-bold display-6 mb-2 text-white">${escapeHtml(deck.title || "Btoan AI Presentation")}</h1>
                <p class="text-white-50 fs-6 fst-italic">${escapeHtml(deck.subtitle || "Tạo bởi Btoan AI Studio")}</p>
            </div>
        `;
    } else {
        // Content Slide
        const s = deck.slides[index - 1];
        const bulletsHtml = (s.bullets || []).map(b => `<li class="mb-2 fs-6 text-dark">${escapeHtml(b)}</li>`).join("");

        stage.innerHTML = `
            <div class="h-100 d-flex flex-column justify-content-between p-4 bg-white">
                <div>
                    <h2 class="fw-bold mb-2" style="color: #0F4C5C;">${escapeHtml(s.title || "Nội dung")}</h2>
                    <hr class="my-2" style="border-color: #e2e8f0;">
                    <ul class="mt-3 ps-3" style="line-height: 1.8;">
                        ${bulletsHtml}
                    </ul>
                </div>
                <div class="d-flex justify-content-between align-items-center text-muted extra-small pt-2 border-top">
                    <span>${escapeHtml(deck.title || "")}</span>
                    <span>${index + 1} / ${total}</span>
                </div>
            </div>
        `;
    }
}

function parseMarkdownToSlideDeck(title, content) {
    const slides = [];
    const lines = content.split("\n");
    let current = null;

    lines.forEach(l => {
        const line = l.trim();
        if (line.startsWith("#")) {
            if (current) slides.push(current);
            current = { title: line.replace(/^#+\s*/, ""), bullets: [] };
        } else if ((line.startsWith("- ") || line.startsWith("* ") || /^\d+\.\s*/.test(line)) && current) {
            current.bullets.push(line.replace(/^[-*\d.]+\s*/, ""));
        } else if (line.length > 0 && current) {
            current.bullets.push(line);
        }
    });
    if (current) slides.push(current);

    return {
        title: title || "Btoan AI Presentation",
        subtitle: `Tạo lúc: ${new Date().toLocaleDateString('vi-VN')}`,
        slides: slides.length > 0 ? slides : [{ title: "Tổng quan", bullets: [content] }]
    };
}

// Direct Artifact Download
async function downloadArtifactDirect(artifact, btnEl) {
    if (!artifact) return;
    const origHtml = btnEl ? btnEl.innerHTML : "";
    if (btnEl) btnEl.innerHTML = `<i class="bi bi-arrow-repeat spin"></i>`;

    try {
        const res = await fetch("/api/file/export-artifact", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
                type: artifact.type,
                title: artifact.title,
                content: artifact.content
            })
        });

        const data = await res.json();
        if (data.success && data.url) {
            triggerBrowserDownload(data.url, data.filename);
        } else {
            alert("Lỗi tạo file: " + (data.error || "Không xác định"));
        }
    } catch (e) {
        alert("Lỗi kết nối tải file: " + e.message);
    } finally {
        if (btnEl) btnEl.innerHTML = origHtml;
    }
}

// Bundle Download (.zip)
async function downloadArtifactBundle(artifacts, btnEl) {
    if (!artifacts || artifacts.length === 0) return;
    const origHtml = btnEl ? btnEl.innerHTML : "";
    if (btnEl) btnEl.innerHTML = `<i class="bi bi-arrow-repeat spin"></i> <span>Đang đóng gói ZIP...</span>`;

    try {
        const res = await fetch("/api/file/export-bundle", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
                bundleName: "Btoan_Artifacts",
                artifacts: artifacts
            })
        });

        const data = await res.json();
        if (data.success && data.zipUrl) {
            triggerBrowserDownload(data.zipUrl, data.zipFilename);
        } else {
            alert("Lỗi đóng gói zip: " + (data.error || "Không xác định"));
        }
    } catch (e) {
        alert("Lỗi kết nối tải zip: " + e.message);
    } finally {
        if (btnEl) btnEl.innerHTML = origHtml;
    }
}

function exportArtifactByType(targetType) {
    if (!window.currentActiveArtifact) return;
    const art = {
        type: targetType,
        title: window.currentActiveArtifact.title,
        content: window.currentActiveArtifact.content
    };
    downloadArtifactDirect(art);
}

function triggerBrowserDownload(url, filename) {
    const a = document.createElement("a");
    a.href = url;
    if (filename) a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
}

function getExcelColLetter(index) {
    let letter = "";
    while (index >= 0) {
        letter = String.fromCharCode((index % 26) + 65) + letter;
        index = Math.floor(index / 26) - 1;
    }
    return letter;
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
