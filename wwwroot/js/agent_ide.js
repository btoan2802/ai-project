/**
 * Btoan Antigravity Web IDE - Main Client Script
 * Complete 1:1 Implementation of Antigravity IDE Dark Modern
 */

let monacoEditor = null;
let monacoDiffEditor = null;
let activeProjectId = "demo";
let activeFilePath = null;
let activeDiffProposal = null;

// Multi-Tab & Model State: Map<path, { path, name, language, model, isDirty, originalContent }>
const openTabsMap = new Map();
let workspaceFilesList = [];
let gitChangesCache = [];

function getApiHeaders() {
    const headers = { 'Content-Type': 'application/json' };
    const uid = localStorage.getItem("user_id");
    if (uid && uid !== "0") {
        headers['X-User-Id'] = uid;
    }
    return headers;
}

function showRestrictedLoginPrompt(msg) {
    const modalEl = document.getElementById("restrictedLoginModal");
    const msgEl = document.getElementById("restrictedModalMessage");
    if (msgEl && msg) msgEl.innerText = msg;
    if (modalEl && typeof showModalSafely === "function") {
        showModalSafely("restrictedLoginModal");
    } else {
        alert("🔒 " + (msg || "Vui lòng đăng nhập tài khoản để thực hiện thao tác này."));
    }
}

function showModalSafely(modalId) {
    const el = document.getElementById(modalId);
    if (!el) return;
    if (window.bootstrap && bootstrap.Modal) {
        const modal = bootstrap.Modal.getInstance(el) || new bootstrap.Modal(el);
        modal.show();
    }
}

function hideModalSafely(modalId) {
    const el = document.getElementById(modalId);
    if (!el) return;
    if (window.bootstrap && bootstrap.Modal) {
        const modal = bootstrap.Modal.getInstance(el);
        if (modal) modal.hide();
    }
}

// ==========================================
// 1. MONACO EDITOR & DIFF EDITOR INITIALIZATION
// ==========================================
function initMonacoEditors() {
    if (typeof require === "undefined") {
        console.warn("[Monaco] require is not loaded yet, retrying...");
        setTimeout(initMonacoEditors, 200);
        return;
    }

    require.config({
        paths: { vs: 'https://cdn.jsdelivr.net/npm/monaco-editor@0.52.0/min/vs' }
    });

    require(['vs/editor/editor.main'], function () {
        const monacoContainer = document.getElementById('monacoContainer');
        const diffContainer = document.getElementById('monacoDiffContainer');

        if (!monacoContainer) return;

        // Custom Antigravity Dark Theme
        monaco.editor.defineTheme('antigravity-dark', {
            base: 'vs-dark',
            inherit: true,
            rules: [
                { token: 'comment', foreground: '6a9955', fontStyle: 'italic' },
                { token: 'keyword', foreground: '569cd6' },
                { token: 'string', foreground: 'ce9178' },
                { token: 'number', foreground: 'b5cea8' },
                { token: 'type', foreground: '4ec9b0' }
            ],
            colors: {
                'editor.background': '#1f1f1f',
                'editor.foreground': '#d4d4d4',
                'editorLineNumber.foreground': '#858585',
                'editorLineNumber.activeForeground': '#c6c6c6',
                'editorCursor.foreground': '#aeafad',
                'editor.selectionBackground': '#264f78',
                'editor.lineHighlightBackground': '#282828',
                'editorIndentGuide.background': '#404040',
                'editorIndentGuide.activeBackground': '#707070'
            }
        });

        // Normal Code Editor
        monacoEditor = monaco.editor.create(monacoContainer, {
            value: '// Welcome to Btoan Antigravity Web IDE\n// Select a file from Workspace Explorer to start coding.\n',
            language: 'csharp',
            theme: 'antigravity-dark',
            automaticLayout: true,
            fontSize: 13,
            lineNumbers: 'on',
            minimap: { enabled: true },
            scrollBeyondLastLine: false,
            renderWhitespace: 'selection',
            fontFamily: "'Fira Code', Consolas, Monaco, monospace"
        });

        // Monaco Diff Editor
        monacoDiffEditor = monaco.editor.createDiffEditor(diffContainer, {
            theme: 'antigravity-dark',
            automaticLayout: true,
            readOnly: true,
            renderSideBySide: true,
            fontSize: 13,
            fontFamily: "'Fira Code', Consolas, Monaco, monospace"
        });

        // Cursor change listener -> updates status bar Ln, Col
        monacoEditor.onDidChangeCursorPosition(e => {
            const lnColEl = document.getElementById("statusLnCol");
            if (lnColEl) {
                lnColEl.innerText = `Ln ${e.position.lineNumber}, Col ${e.position.column}`;
            }
        });

        // Content change listener -> mark tab dirty
        monacoEditor.onDidChangeModelContent(() => {
            if (activeFilePath && openTabsMap.has(activeFilePath)) {
                const tab = openTabsMap.get(activeFilePath);
                if (!tab.isDirty) {
                    tab.isDirty = true;
                    renderTabBar();
                }
                updateGitBadgeCount();
                scheduleOutlineRefresh();

                if (isAutoSaveEnabled) {
                    if (autoSaveTimer) clearTimeout(autoSaveTimer);
                    autoSaveTimer = setTimeout(() => {
                        saveCurrentFile(true);
                    }, 1500);
                }
            }
        });

        // Keyboard Shortcut: Ctrl+S to save
        monacoEditor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => {
            saveCurrentFile();
        });

        console.log("[Monaco] Antigravity Dark theme initialized successfully.");
        loadUserProjects();
    });
}

// ==========================================
// 2. WORKSPACE PROJECTS & FILE EXPLORER
// ==========================================
async function loadUserProjects() {
    const select = document.getElementById("projectSelect");
    if (!select) return;

    try {
        const res = await fetch('/api/ide/projects', {
            headers: getApiHeaders()
        });
        const data = await res.json();

        if (data.ok && Array.isArray(data.projects) && data.projects.length > 0) {
            select.innerHTML = "";
            data.projects.forEach(p => {
                const opt = document.createElement("option");
                opt.value = p.id;
                opt.innerText = p.name;
                select.appendChild(opt);
            });
            activeProjectId = data.projects[0].id;
            hideEmptyWorkspaceState();
        } else {
            select.innerHTML = `<option value="">-- Chưa có dự án --</option>`;
            activeProjectId = "";
            showEmptyWorkspaceState();
        }

        // Update Top Bar User Badge
        updateTopUserBadge(data.isAdmin, data.username);

        // Update Workspace Root Header Title
        const rootNameEl = document.getElementById("workspaceRootName");
        if (rootNameEl) {
            rootNameEl.innerText = activeProjectId ? (select.options[select.selectedIndex]?.text || activeProjectId) : "Chưa có dự án";
        }

        // Update Terminal Prompt
        updateTerminalPrompt();

    } catch (err) {
        console.warn("[IDE] Error loading projects:", err);
        select.innerHTML = `<option value="">-- Chưa có dự án --</option>`;
        activeProjectId = "";
        showEmptyWorkspaceState();
    }

    if (activeProjectId) {
        refreshFileTree();
        loadGitStatus();
        if (terminalHubConn && terminalHubConn.state === signalR.HubConnectionState.Connected) {
            terminalHubConn.invoke("InitializeTerminal", activeProjectId).catch(console.error);
        }
    } else {
        const container = document.getElementById("workspaceTreeContainer");
        if (container) {
            container.innerHTML = `<div class="text-secondary extra-small p-3 text-center">Chưa có dự án nào.<br><a href="#" onclick="openCreateProjectModal()" class="text-info text-decoration-none">+ Tạo dự án mới</a></div>`;
        }
    }
}

function showEmptyWorkspaceState() {
    const emptyState = document.getElementById("ideEmptyState");
    const monacoContainer = document.getElementById("monacoContainer");
    const diffContainer = document.getElementById("monacoDiffContainer");
    if (emptyState) emptyState.style.display = "flex";
    if (monacoContainer) monacoContainer.style.display = "none";
    if (diffContainer) diffContainer.style.display = "none";
}

function hideEmptyWorkspaceState() {
    const emptyState = document.getElementById("ideEmptyState");
    const monacoContainer = document.getElementById("monacoContainer");
    if (emptyState) emptyState.style.display = "none";
    if (monacoContainer) monacoContainer.style.display = "block";
}

function updateTopUserBadge(isAdmin, username) {
    const badgeContainer = document.getElementById("topUserBadgeContainer");
    if (!badgeContainer) return;

    const uname = username || localStorage.getItem("username") || (isAdmin ? "admin" : null);
    if (uname) {
        badgeContainer.innerHTML = `
            <div class="top-user-pill">
                <i class="bi bi-person-check-fill text-success me-1.5"></i>
                <span class="fw-semibold">${uname}</span>
            </div>
        `;
    } else {
        badgeContainer.innerHTML = `
            <button class="btn-sign-in" onclick="location.href='/?login=1'">
                <i class="bi bi-person me-1"></i> Sign In
            </button>
        `;
    }
}

function updateTerminalPrompt() {
    terminalPrompt = activeProjectId ? `PS D:\\UserWorkspaces\\${activeProjectId}> ` : `PS D:\\UserWorkspaces> `;
}

function onProjectChanged() {
    const select = document.getElementById("projectSelect");
    if (!select) return;
    activeProjectId = select.value;

    if (activeProjectId) {
        hideEmptyWorkspaceState();
        const rootNameEl = document.getElementById("workspaceRootName");
        if (rootNameEl) {
            rootNameEl.innerText = select.options[select.selectedIndex]?.text || activeProjectId;
        }
        updateTerminalPrompt();
        refreshFileTree();
        loadGitStatus();
        if (terminalHubConn && terminalHubConn.state === signalR.HubConnectionState.Connected) {
            terminalHubConn.invoke("InitializeTerminal", activeProjectId).catch(console.error);
        }
    } else {
        showEmptyWorkspaceState();
        updateTerminalPrompt();
        const container = document.getElementById("workspaceTreeContainer");
        if (container) {
            container.innerHTML = `<div class="text-secondary extra-small p-3 text-center">Chưa có dự án nào.</div>`;
        }
    }
}

async function switchActiveProject(projName) {
    if (!projName) return;
    activeProjectId = projName;
    const select = document.getElementById("projectSelect");
    if (select) {
        select.value = projName;
        let found = false;
        for (let i = 0; i < select.options.length; i++) {
            if (select.options[i].value === projName) {
                select.selectedIndex = i;
                found = true;
                break;
            }
        }
        if (!found) {
            const opt = document.createElement("option");
            opt.value = projName;
            opt.textContent = projName;
            opt.selected = true;
            select.appendChild(opt);
        }
    }

    hideEmptyWorkspaceState();
    const rootNameEl = document.getElementById("workspaceRootName");
    if (rootNameEl) {
        rootNameEl.innerText = projName;
    }
    updateTerminalPrompt();
    await refreshFileTree();
    loadGitStatus();
    if (terminalHubConn && terminalHubConn.state === signalR.HubConnectionState.Connected) {
        await terminalHubConn.invoke("InitializeTerminal", activeProjectId).catch(console.error);
    }
}
window.switchActiveProject = switchActiveProject;

async function refreshFileTree() {
    const container = document.getElementById("workspaceTreeContainer");
    if (!container) return;

    container.innerHTML = `<div class="text-secondary extra-small p-2"><div class="spinner-border spinner-border-sm me-1 text-info"></div> Loading files...</div>`;

    try {
        const res = await fetch(`/api/ide/files?project=${encodeURIComponent(activeProjectId)}`, {
            headers: getApiHeaders()
        });
        const data = await res.json();

        if (data.ok && Array.isArray(data.files)) {
            workspaceFilesList = data.files;
            renderFileTree(data.files, container);

            // Auto-open primary file if nothing opened
            if (!activeFilePath) {
                const primaryFile = data.files.find(f => !f.isDir && (f.name === "index.html" || f.path.endsWith("index.html") || f.name === "Program.cs"));
                if (primaryFile) {
                    openFileInEditor(primaryFile.path);
                } else if (data.files.length > 0 && !data.files[0].isDir) {
                    openFileInEditor(data.files[0].path);
                }
            }
        } else {
            container.innerHTML = `<div class="text-warning extra-small p-2">${data.message || "No files found in workspace"}</div>`;
        }
    } catch (err) {
        container.innerHTML = `<div class="text-danger extra-small p-2">Failed to load file tree: ${err.message}</div>`;
    }
}

function renderFileTree(files, container) {
    container.innerHTML = "";

    // Build hierarchical tree
    const root = { dirs: {}, files: [] };
    files.forEach(f => {
        const parts = f.path.split(/[\\/]/);
        let curr = root;
        for (let i = 0; i < parts.length - 1; i++) {
            const dirName = parts[i];
            if (!curr.dirs[dirName]) curr.dirs[dirName] = { name: dirName, dirs: {}, files: [] };
            curr = curr.dirs[dirName];
        }
        if (f.isDir) {
            const last = parts[parts.length - 1];
            if (!curr.dirs[last]) curr.dirs[last] = { name: last, dirs: {}, files: [] };
        } else {
            curr.files.push(f);
        }
    });

    const frag = document.createDocumentFragment();
    renderTreeLevel(root, frag, 0);
    container.appendChild(frag);
    container.oncontextmenu = (e) => {
        if (e.target === container || e.target.id === "workspaceTreeContainer") {
            onExplorerContextMenu(e, "", true);
        }
    };
}

// ==========================================
// VSCODE-ICONS MAP & HELPERS
// ==========================================
const VSCODE_ICONS_BASE = "https://cdn.jsdelivr.net/gh/vscode-icons/vscode-icons/icons/";

const FOLDER_ICON_MAP = {
    "src": "folder_type_src",
    "controllers": "folder_type_controller",
    "controller": "folder_type_controller",
    "models": "folder_type_model",
    "model": "folder_type_model",
    "views": "folder_type_view",
    "view": "folder_type_view",
    "wwwroot": "folder_type_public",
    "public": "folder_type_public",
    "static": "folder_type_public",
    "css": "folder_type_css",
    "styles": "folder_type_css",
    "style": "folder_type_css",
    "js": "folder_type_js",
    "scripts": "folder_type_js",
    "script": "folder_type_js",
    "images": "folder_type_images",
    "img": "folder_type_images",
    "components": "folder_type_component",
    "component": "folder_type_component",
    "api": "folder_type_api",
    "test": "folder_type_test",
    "tests": "folder_type_test",
    "docs": "folder_type_docs",
    "config": "folder_type_config",
    "settings": "folder_type_config",
    "git": "folder_type_git",
    ".git": "folder_type_git",
    "node_modules": "folder_type_node",
    "hubs": "folder_type_network",
    "hub": "folder_type_network",
    "services": "folder_type_service",
    "service": "folder_type_service",
    "data": "folder_type_database"
};

const FILE_ICON_MAP = {
    "cs": "file_type_csharp.svg",
    "js": "file_type_js.svg",
    "ts": "file_type_typescript.svg",
    "json": "file_type_json.svg",
    "html": "file_type_html.svg",
    "cshtml": "file_type_razor.svg",
    "css": "file_type_css.svg",
    "scss": "file_type_sass.svg",
    "sass": "file_type_sass.svg",
    "py": "file_type_python.svg",
    "md": "file_type_markdown.svg",
    "csproj": "file_type_csproj.svg",
    "xml": "file_type_xml.svg",
    "sql": "file_type_sql.svg",
    "png": "file_type_image.svg",
    "jpg": "file_type_image.svg",
    "jpeg": "file_type_image.svg",
    "gif": "file_type_image.svg",
    "svg": "file_type_svg.svg",
    "ico": "file_type_favicon.svg",
    "zip": "file_type_zip.svg",
    "rar": "file_type_zip.svg",
    "7z": "file_type_zip.svg",
    "tar": "file_type_zip.svg",
    "gz": "file_type_zip.svg",
    "txt": "file_type_text.svg",
    "yml": "file_type_yaml.svg",
    "yaml": "file_type_yaml.svg",
    "sh": "file_type_shell.svg",
    "bat": "file_type_bat.svg",
    "ps1": "file_type_powershell.svg"
};

const SPECIAL_FILE_NAMES = {
    "package.json": "file_type_npm.svg",
    "package-lock.json": "file_type_npm.svg",
    "tsconfig.json": "file_type_tsconfig.svg",
    ".gitignore": "file_type_git.svg",
    ".gitattributes": "file_type_git.svg",
    "dockerfile": "file_type_docker.svg",
    "docker-compose.yml": "file_type_docker.svg",
    "readme.md": "file_type_markdown.svg",
    "license": "file_type_license.svg"
};

function getFolderIconHtml(folderName, isOpen) {
    const lower = (folderName || "").toLowerCase();
    const iconBase = FOLDER_ICON_MAP[lower] || "default_folder";
    const suffix = isOpen ? "_opened.svg" : ".svg";
    const src = `${VSCODE_ICONS_BASE}${iconBase}${suffix}`;
    return `<img class="vscode-icon folder-icon-img me-1.5" src="${src}" alt="folder" onerror="this.onerror=null; this.replaceWith(document.createElement('span'));">`;
}

function getFileIconHtml(fileName) {
    const lower = (fileName || "").toLowerCase();
    let iconFile = SPECIAL_FILE_NAMES[lower];
    if (!iconFile) {
        const ext = lower.split('.').pop();
        iconFile = FILE_ICON_MAP[ext] || "default_file.svg";
    }
    const src = `${VSCODE_ICONS_BASE}${iconFile}`;
    return `<img class="vscode-icon file-icon-img me-1.5" src="${src}" alt="file" onerror="this.onerror=null; this.replaceWith(document.createElement('span'));">`;
}

let activeTreeNode = null; // { path, isDirectory, el }

function renderTreeLevel(dirObj, parentElement, depth, currentPath = "") {
    // Render subdirectories
    Object.keys(dirObj.dirs).sort().forEach(dName => {
        const sub = dirObj.dirs[dName];
        const dirRelPath = currentPath ? `${currentPath}/${dName}` : dName;
        const dirEl = document.createElement("div");
        dirEl.className = "tree-node";
        dirEl.style.paddingLeft = `${depth * 14 + 6}px`;
        dirEl.draggable = true;
        dirEl.dataset.path = dirRelPath;
        dirEl.dataset.isDir = "true";

        dirEl.innerHTML = `
            <div class="tree-node-label">
                <i class="bi bi-chevron-right text-secondary extra-small chevron-icon"></i>
                <span class="folder-icon-wrapper">${getFolderIconHtml(dName, false)}</span>
                <span class="tree-item-name">${escapeHtml(dName)}</span>
            </div>
        `;

        const childrenContainer = document.createElement("div");
        childrenContainer.style.display = "none";
        childrenContainer.dataset.parentPath = dirRelPath;

        dirEl.onclick = (e) => {
            e.stopPropagation();
            selectTreeNode(dirEl, dirRelPath, true);
            const isOpen = childrenContainer.style.display !== "none";
            childrenContainer.style.display = isOpen ? "none" : "block";
            const ch = dirEl.querySelector(".chevron-icon");
            if (ch) ch.className = isOpen ? "bi bi-chevron-right text-secondary extra-small chevron-icon" : "bi bi-chevron-down text-secondary extra-small chevron-icon";
            const iconWrapper = dirEl.querySelector(".folder-icon-wrapper");
            if (iconWrapper) iconWrapper.innerHTML = getFolderIconHtml(dName, !isOpen);
        };

        dirEl.oncontextmenu = (e) => {
            selectTreeNode(dirEl, dirRelPath, true);
            onExplorerContextMenu(e, dirRelPath, true, dirEl);
        };

        // Drag & Drop
        dirEl.ondragstart = (e) => {
            e.stopPropagation();
            e.dataTransfer.setData("text/plain", JSON.stringify({ path: dirRelPath, isDirectory: true }));
        };

        dirEl.ondragover = (e) => {
            e.preventDefault();
            e.stopPropagation();
            dirEl.classList.add("drag-over");
        };

        dirEl.ondragleave = (e) => {
            e.stopPropagation();
            dirEl.classList.remove("drag-over");
        };

        dirEl.ondrop = async (e) => {
            e.preventDefault();
            e.stopPropagation();
            dirEl.classList.remove("drag-over");
            try {
                const data = JSON.parse(e.dataTransfer.getData("text/plain"));
                if (data && data.path && data.path !== dirRelPath) {
                    await handleMoveItem(data.path, dirRelPath);
                }
            } catch (err) { }
        };

        parentElement.appendChild(dirEl);
        parentElement.appendChild(childrenContainer);
        renderTreeLevel(sub, childrenContainer, depth + 1, dirRelPath);
    });

    // Render files
    dirObj.files.sort((a, b) => a.name.localeCompare(b.name)).forEach(file => {
        const fEl = document.createElement("div");
        fEl.className = "tree-node";
        fEl.style.paddingLeft = `${depth * 14 + 18}px`;
        fEl.draggable = true;
        fEl.dataset.path = file.path;
        fEl.dataset.isDir = "false";
        if (file.path === activeFilePath) fEl.classList.add("active");

        // Check Git status for badge
        let gitBadgeHtml = "";
        const gitMod = gitChangesCache.find(g => file.path.includes(g.file) || g.file.includes(file.path));
        if (gitMod) {
            const bChar = gitMod.status === "A" || gitMod.status === "?" ? "U" : "M";
            const bClass = bChar === "U" ? "git-badge-u" : "git-badge-m";
            gitBadgeHtml = `<span class="git-status-badge ${bClass}">${bChar}</span>`;
        }

        fEl.innerHTML = `
            <div class="tree-node-label">
                ${getFileIconHtml(file.name)}
                <span class="tree-item-name">${escapeHtml(file.name)}</span>
            </div>
            <div>${gitBadgeHtml}</div>
        `;

        fEl.onclick = (e) => {
            e.stopPropagation();
            selectTreeNode(fEl, file.path, false);
            openFileInEditor(file.path);
        };

        fEl.oncontextmenu = (e) => {
            selectTreeNode(fEl, file.path, false);
            onExplorerContextMenu(e, file.path, false, fEl);
        };

        fEl.ondragstart = (e) => {
            e.stopPropagation();
            e.dataTransfer.setData("text/plain", JSON.stringify({ path: file.path, isDirectory: false }));
        };

        parentElement.appendChild(fEl);
    });
}

function selectTreeNode(el, path, isDirectory) {
    document.querySelectorAll(".tree-node.selected").forEach(n => n.classList.remove("selected"));
    if (el) el.classList.add("selected");
    activeTreeNode = { path, isDirectory, el };
}

function getFileIcon(fileName) {
    const ext = fileName.split('.').pop().toLowerCase();
    switch (ext) {
        case 'cs': return { icon: 'bi bi-filetype-cs', color: '#68217a' };
        case 'js': return { icon: 'bi bi-filetype-js', color: '#f7df1e' };
        case 'json': return { icon: 'bi bi-filetype-json', color: '#f59e0b' };
        case 'html': case 'cshtml': return { icon: 'bi bi-filetype-html', color: '#e34f26' };
        case 'css': return { icon: 'bi bi-filetype-css', color: '#1572b6' };
        case 'py': return { icon: 'bi bi-filetype-py', color: '#3776ab' };
        case 'md': return { icon: 'bi bi-file-earmark-text', color: '#60a5fa' };
        case 'csproj': case 'xml': return { icon: 'bi bi-filetype-xml', color: '#10b981' };
        default: return { icon: 'bi bi-file-earmark-code', color: '#9ca3af' };
    }
}

function getMonacoLanguage(fileName) {
    const ext = fileName.split('.').pop().toLowerCase();
    switch (ext) {
        case 'cs': return 'csharp';
        case 'js': return 'javascript';
        case 'json': return 'json';
        case 'html': case 'cshtml': return 'html';
        case 'css': return 'css';
        case 'py': return 'python';
        case 'md': return 'markdown';
        case 'xml': case 'csproj': return 'xml';
        default: return 'plaintext';
    }
}

// ==========================================
// 3. MULTI-TABS & FILE OPEN/SAVE
// ==========================================
async function openFileInEditor(filePath) {
    if (!monacoEditor) return;

    // Check if model already exists in memory
    if (openTabsMap.has(filePath)) {
        const tab = openTabsMap.get(filePath);
        activeFilePath = filePath;
        monacoEditor.setModel(tab.model);
        updateEditorUI(tab);
        return;
    }

    try {
        const res = await fetch(`/api/ide/file-content?project=${encodeURIComponent(activeProjectId)}&file=${encodeURIComponent(filePath)}`, {
            headers: getApiHeaders()
        });
        const data = await res.json();

        if (data.ok) {
            const fileName = filePath.split(/[\\/]/).pop();
            const lang = getMonacoLanguage(fileName);
            const modelUri = monaco.Uri.parse(`inmemory://workspace/${filePath}`);
            let model = monaco.editor.getModel(modelUri);
            if (!model) {
                model = monaco.editor.createModel(data.content, lang, modelUri);
            } else {
                model.setValue(data.content);
            }

            const tab = {
                path: filePath,
                name: fileName,
                language: lang,
                model: model,
                isDirty: false,
                originalContent: data.content
            };

            openTabsMap.set(filePath, tab);
            activeFilePath = filePath;
            monacoEditor.setModel(model);
            updateEditorUI(tab);

        } else {
            alert("❌ " + (data.message || "Failed to load file content"));
        }
    } catch (err) {
        alert("❌ Error opening file: " + err.message);
    }
}

function updateEditorUI(tab) {
    renderTabBar();

    // Update Breadcrumb & Top Bar Title
    const projName = activeProjectId || "Workspace";
    const bEl = document.getElementById("breadcrumbPath");
    if (bEl) bEl.innerHTML = `${escapeHtml(projName)} <i class="bi bi-chevron-right extra-small mx-1 text-secondary"></i> ${getFileIconHtml(tab.name)} <span>${escapeHtml(tab.path.replace(/\\/g, ' / '))}</span>`;

    const topTitleEl = document.getElementById("topBarTitle");
    if (topTitleEl) topTitleEl.innerText = `${projName} — ${tab.name}`;

    // Update Context Chip in Agent Prompt
    const chipEl = document.getElementById("activeFileChip");
    if (chipEl) {
        chipEl.innerHTML = `${getFileIconHtml(tab.name)} <span id="activeChipText">${escapeHtml(tab.name)}</span> <i class="bi bi-x ms-1 cursor-pointer" onclick="removeContextChip()"></i>`;
        chipEl.style.display = "inline-flex";
    }

    // Update Status Bar Language
    const langEl = document.getElementById("statusLanguage");
    if (langEl) {
        const langDisplayMap = {
            csharp: 'C#',
            javascript: 'JavaScript',
            json: 'JSON',
            html: 'HTML',
            css: 'CSS',
            python: 'Python',
            markdown: 'Markdown'
        };
        langEl.innerText = langDisplayMap[tab.language] || tab.language.toUpperCase();
    }

    // Highlight file in tree
    document.querySelectorAll(".tree-node").forEach(node => {
        if (node.innerText.includes(tab.name)) {
            node.classList.add("active");
        } else {
            node.classList.remove("active");
        }
    });

    scheduleOutlineRefresh();
    triggerEditorLayout();
}

function renderTabBar() {
    const tabBar = document.getElementById("editorTabBar");
    if (!tabBar) return;

    tabBar.innerHTML = "";
    openTabsMap.forEach((tab, path) => {
        const tabEl = document.createElement("div");
        tabEl.className = "editor-tab-item" + (path === activeFilePath ? " active" : "");

        tabEl.innerHTML = `
            ${getFileIconHtml(tab.name)}
            <span>${escapeHtml(tab.name)}</span>
            ${tab.isDirty ? '<span class="text-warning extra-small">●</span>' : ''}
            <span class="tab-close-btn" title="Close Tab (Ctrl+W)">&times;</span>
        `;

        tabEl.onclick = () => openFileInEditor(path);

        const closeBtn = tabEl.querySelector(".tab-close-btn");
        closeBtn.onclick = (e) => {
            e.stopPropagation();
            closeTab(path);
        };

        tabBar.appendChild(tabEl);
    });
}

function closeTab(path) {
    if (!openTabsMap.has(path)) return;
    openTabsMap.delete(path);

    if (activeFilePath === path) {
        const remaining = Array.from(openTabsMap.keys());
        if (remaining.length > 0) {
            openFileInEditor(remaining[remaining.length - 1]);
        } else {
            activeFilePath = null;
            if (monacoEditor) {
                monacoEditor.setValue("// Select a file from Explorer to begin coding.\n");
            }
            const chipEl = document.getElementById("activeFileChip");
            if (chipEl) chipEl.style.display = "none";
            const bEl = document.getElementById("breadcrumbPath");
            if (bEl) bEl.innerText = activeProjectId ? `${activeProjectId}` : "Select a file from Explorer";
            const topTitleEl = document.getElementById("topBarTitle");
            if (topTitleEl) topTitleEl.innerText = activeProjectId ? `${activeProjectId} — Antigravity IDE` : "Antigravity IDE";
            renderTabBar();
        }
    } else {
        renderTabBar();
    }
    updateGitBadgeCount();
    scheduleOutlineRefresh();
}

async function saveCurrentFile(silent = false) {
    if (!activeFilePath || !openTabsMap.has(activeFilePath)) return;
    const tab = openTabsMap.get(activeFilePath);
    const content = tab.model.getValue();

    try {
        const res = await fetch('/api/ide/save', {
            method: 'POST',
            headers: getApiHeaders(),
            body: JSON.stringify({
                project: activeProjectId,
                file: tab.path,
                content: content
            })
        });
        const data = await res.json();

        if (data.ok) {
            tab.isDirty = false;
            tab.originalContent = content;
            renderTabBar();
            updateGitBadgeCount();
            if (!silent) {
                showToastNotification(`Saved ${tab.name}`);
            }
            console.log(`[IDE] Saved ${tab.path} successfully.`);
            loadGitStatus();
        } else {
            if (!silent) {
                showRestrictedLoginPrompt(data.message || "Không thể lưu file ở chế độ xem an toàn.");
            }
        }
    } catch (err) {
        if (!silent) {
            alert("❌ Lỗi khi lưu file: " + err.message);
        }
    }
}

// ==========================================
// 4. ACTIVITY BAR TAB SWITCHING & SIDEBAR PANELS
// ==========================================
function switchActivityTab(tabName) {
    // Buttons
    ['actBtnExplorer', 'actBtnSearch', 'actBtnGit', 'actBtnRun'].forEach(id => {
        const btn = document.getElementById(id);
        if (btn) btn.classList.remove("active");
    });

    // Panels
    ['leftExplorerPanel', 'leftSearchPanel', 'leftGitPanel', 'leftRunPanel'].forEach(id => {
        const panel = document.getElementById(id);
        if (panel) panel.style.display = "none";
    });

    const sidebar = document.getElementById("primarySidebar");
    if (sidebar && sidebar.style.display === "none") {
        sidebar.style.display = "flex";
    }

    if (tabName === 'explorer') {
        document.getElementById('actBtnExplorer')?.classList.add('active');
        document.getElementById('leftExplorerPanel').style.display = 'flex';
    } else if (tabName === 'search') {
        document.getElementById('actBtnSearch')?.classList.add('active');
        document.getElementById('leftSearchPanel').style.display = 'flex';
        document.getElementById('ideSearchQuery')?.focus();
    } else if (tabName === 'git') {
        document.getElementById('actBtnGit')?.classList.add('active');
        document.getElementById('leftGitPanel').style.display = 'flex';
        loadGitStatus();
    } else if (tabName === 'run') {
        document.getElementById('actBtnRun')?.classList.add('active');
        document.getElementById('leftRunPanel').style.display = 'flex';
    }
}

function triggerEditorLayout() {
    try {
        if (monacoEditor) monacoEditor.layout();
        if (monacoDiffEditor) monacoDiffEditor.layout();
    } catch (e) { }
}

function toggleLeftSidebar() {
    const sidebar = document.getElementById("primarySidebar");
    const resizer = document.getElementById("resizerExplorer");
    if (!sidebar) return;
    const isHidden = sidebar.style.display === "none";
    sidebar.style.display = isHidden ? "flex" : "none";
    if (resizer) resizer.style.display = isHidden ? "block" : "none";
    triggerEditorLayout();
}

function toggleRightAgentPanel() {
    const panel = document.getElementById("rightAgentPanel");
    const resizer = document.getElementById("resizerAgent");
    if (!panel) return;
    const isHidden = panel.style.display === "none";
    panel.style.display = isHidden ? "flex" : "none";
    if (resizer) resizer.style.display = isHidden ? "block" : "none";
    triggerEditorLayout();
}

function toggleBottomPanel() {
    const panel = document.getElementById("bottomPanel");
    const resizer = document.getElementById("resizerTerminal");
    if (!panel) return;
    const isHidden = panel.classList.contains("ide-panel-hidden") || panel.style.display === "none";
    if (isHidden) {
        panel.classList.remove("ide-panel-hidden");
        panel.style.display = "flex";
        if (resizer) resizer.style.display = "block";
        setTimeout(() => {
            try { fitAddon?.fit(); } catch (e) { }
            try { xtermInstance?.focus(); } catch (e) { }
        }, 50);
    } else {
        panel.classList.add("ide-panel-hidden");
        panel.style.display = "none";
        if (resizer) resizer.style.display = "none";
    }
    triggerEditorLayout();
}

function closeBottomPanel() {
    const panel = document.getElementById("bottomPanel");
    const resizer = document.getElementById("resizerTerminal");
    if (panel) {
        panel.classList.add("ide-panel-hidden");
        panel.style.display = "none";
    }
    if (resizer) resizer.style.display = "none";
    triggerEditorLayout();
}

let isBottomPanelMaximized = false;
function toggleBottomPanelHeight() {
    const panel = document.getElementById("bottomPanel");
    if (!panel) return;
    const btnIcon = panel.querySelector(".bi-chevron-up, .bi-chevron-down");
    if (isBottomPanelMaximized) {
        panel.style.height = localStorage.getItem('antigravity_terminal_h') || "240px";
        isBottomPanelMaximized = false;
        if (btnIcon) {
            btnIcon.classList.remove('bi-chevron-down');
            btnIcon.classList.add('bi-chevron-up');
        }
    } else {
        panel.style.height = "65vh";
        isBottomPanelMaximized = true;
        if (btnIcon) {
            btnIcon.classList.remove('bi-chevron-up');
            btnIcon.classList.add('bi-chevron-down');
        }
    }
    triggerEditorLayout();
    setTimeout(() => {
        try { fitAddon?.fit(); } catch (e) { }
    }, 60);
}

function toggleAccordion(name) {
    if (name === 'outline') {
        const el = document.getElementById("accordionOutline");
        if (el) el.style.display = el.style.display === "none" ? "block" : "none";
    } else if (name === 'timeline') {
        const el = document.getElementById("accordionTimeline");
        if (el) el.style.display = el.style.display === "none" ? "block" : "none";
    }
}

// ==========================================
// 5. SEARCH & GIT STATUS INTEGRATION
// ==========================================
async function executeProjectSearch() {
    const query = document.getElementById("ideSearchQuery")?.value?.trim();
    const container = document.getElementById("searchResultsContainer");
    const summary = document.getElementById("searchResultsSummary");
    if (!container || !query) return;

    container.innerHTML = `<div class="text-secondary extra-small p-2"><div class="spinner-border spinner-border-sm me-1 text-info"></div> Searching...</div>`;

    try {
        const res = await fetch(`/api/ide/search?project=${encodeURIComponent(activeProjectId)}&q=${encodeURIComponent(query)}`, {
            headers: getApiHeaders()
        });
        const data = await res.json();

        if (data.ok && Array.isArray(data.results)) {
            if (summary) summary.innerText = `${data.count} result(s) found`;
            container.innerHTML = "";

            data.results.forEach(item => {
                const itemEl = document.createElement("div");
                itemEl.className = "p-1.5 rounded hover-bg-dark cursor-pointer text-light border-bottom border-secondary mb-1";
                itemEl.innerHTML = `
                    <div class="d-flex align-items-center justify-content-between">
                        <span class="fw-semibold text-info extra-small">${item.file.split(/[\\/]/).pop()}</span>
                        <span class="text-secondary extra-small">:${item.line}</span>
                    </div>
                    <div class="font-mono extra-small text-secondary text-truncate">${escapeHtml(item.snippet)}</div>
                `;
                itemEl.onclick = () => {
                    openFileInEditor(item.file);
                    setTimeout(() => {
                        if (monacoEditor) {
                            monacoEditor.revealLineInCenter(item.line);
                            monacoEditor.setPosition({ lineNumber: item.line, column: item.col });
                        }
                    }, 200);
                };
                container.appendChild(itemEl);
            });
        } else {
            if (summary) summary.innerText = "No results found";
            container.innerHTML = `<div class="text-secondary extra-small p-2">No matching code found.</div>`;
        }
    } catch (err) {
        container.innerHTML = `<div class="text-danger extra-small p-2">Search failed: ${err.message}</div>`;
    }
}

function clearProjectSearch() {
    const q = document.getElementById("ideSearchQuery");
    if (q) q.value = "";
    const container = document.getElementById("searchResultsContainer");
    if (container) container.innerHTML = "";
    const summary = document.getElementById("searchResultsSummary");
    if (summary) summary.innerText = "Press Enter to search workspace";
}

async function loadGitStatus() {
    try {
        const res = await fetch(`/api/ide/git-status?project=${encodeURIComponent(activeProjectId)}`, {
            headers: getApiHeaders()
        });
        const data = await res.json();

        if (data.ok && Array.isArray(data.changes)) {
            gitChangesCache = data.changes;

            // Update badges
            const bBadge = document.getElementById("gitBadgeCount");
            if (bBadge) bBadge.innerText = data.changes.length;
            const cBadge = document.getElementById("gitChangesCountBadge");
            if (cBadge) cBadge.innerText = data.changes.length;

            const list = document.getElementById("gitChangesList");
            if (list) {
                if (data.changes.length === 0) {
                    list.innerHTML = `<div class="text-secondary extra-small p-2">Working tree clean.</div>`;
                } else {
                    list.innerHTML = "";
                    data.changes.forEach(c => {
                        const itemEl = document.createElement("div");
                        itemEl.className = "tree-node";
                        const bClass = c.status === "U" ? "git-badge-u" : "git-badge-m";
                        itemEl.innerHTML = `
                            <div class="tree-node-label">
                                <i class="bi bi-file-earmark-code text-secondary"></i>
                                <span>${c.file}</span>
                            </div>
                            <span class="git-status-badge ${bClass}">${c.status}</span>
                        `;
                        itemEl.onclick = () => openFileInEditor(c.file);
                        list.appendChild(itemEl);
                    });
                }
            }
        }
    } catch (err) {
        console.warn("[Git] Git status check failed:", err);
    }
}

// ==========================================
// 6. TERMINAL EXECUTION (XTERM.JS + SIGNALR REALTIME STREAMING)
// ==========================================
let xtermInstance = null;
let fitAddon = null;
let terminalHubConn = null;
let terminalInputBuffer = "";
let terminalCursorPos = 0;
let terminalHistory = [];
let terminalHistoryIndex = -1;
let terminalPrompt = "PS D:\\UserWorkspaces> ";
let isTerminalProcessRunning = false;

function initXtermTerminal() {
    const container = document.getElementById("xtermTerminalContainer");
    if (!container) return;

    if (typeof Terminal === "undefined") {
        setTimeout(initXtermTerminal, 200);
        return;
    }

    if (xtermInstance) {
        return;
    }

    xtermInstance = new Terminal({
        theme: {
            background: '#181818',
            foreground: '#cccccc',
            cursor: '#ffffff',
            cursorAccent: '#181818',
            selectionBackground: 'rgba(255, 255, 255, 0.25)',
            black: '#181818',
            red: '#f14c4c',
            green: '#4ec9b0',
            yellow: '#dcdcaa',
            blue: '#264f78',
            magenta: '#c586c0',
            cyan: '#4dc4ff',
            white: '#cccccc',
            brightBlack: '#666666',
            brightRed: '#f14c4c',
            brightGreen: '#4ec9b0',
            brightYellow: '#dcdcaa',
            brightBlue: '#3b8eea',
            brightMagenta: '#d670d6',
            brightCyan: '#4dc4ff',
            brightWhite: '#ffffff'
        },
        fontFamily: "'Cascadia Code', 'Fira Code', Consolas, monospace",
        fontSize: 13,
        lineHeight: 1.25,
        cursorBlink: true,
        cursorStyle: 'block',
        convertEol: true,
        scrollback: 5000,
        allowTransparency: false
    });

    if (typeof FitAddon !== "undefined" && FitAddon.FitAddon) {
        fitAddon = new FitAddon.FitAddon();
        xtermInstance.loadAddon(fitAddon);
    }

    xtermInstance.open(container);
    if (fitAddon) {
        setTimeout(() => fitAddon.fit(), 100);
    }

    // Auto-fit on window resize
    window.addEventListener("resize", () => {
        try { fitAddon?.fit(); } catch (e) { }
    });

    // Smart Copy / Paste / Select All via attachCustomKeyEventHandler
    xtermInstance.attachCustomKeyEventHandler((e) => {
        if (e.type !== "keydown") return true;

        // 1. Copy: Ctrl + C or Ctrl + Shift + C
        if ((e.ctrlKey && !e.shiftKey && (e.key === 'c' || e.key === 'C')) ||
            (e.ctrlKey && e.shiftKey && (e.key === 'c' || e.key === 'C'))) {
            if (xtermInstance.hasSelection()) {
                const selectedText = xtermInstance.getSelection();
                navigator.clipboard.writeText(selectedText);
                xtermInstance.clearSelection();
                showToastNotification("Copied to clipboard");
                return false; // Prevent sending SIGINT / KillRunningProcess when copying text
            }
            if (e.shiftKey) return false;
            // No selection + Ctrl+C -> Pass to onData to send ^C and kill running process
            return true;
        }

        // 2. Paste: Ctrl + V or Ctrl + Shift + V
        if (e.ctrlKey && (e.key === 'v' || e.key === 'V')) {
            e.preventDefault();
            pasteIntoTerminal();
            return false; // Prevent double paste
        }

        // 3. Select All: Ctrl + Shift + A
        if (e.ctrlKey && e.shiftKey && (e.key === 'a' || e.key === 'A')) {
            e.preventDefault();
            xtermInstance.selectAll();
            return false;
        }

        return true;
    });

    // Right-click Context Menu on Terminal Container
    container.addEventListener("contextmenu", (e) => {
        onTerminalContextMenu(e);
    });

    // Native paste fallback on container (handles middle click or browser paste)
    container.addEventListener("paste", (e) => {
        e.preventDefault();
        const text = (e.clipboardData || window.clipboardData)?.getData('text');
        if (text) {
            insertTextIntoTerminalBuffer(text);
        }
    });

    // Keystroke handling
    xtermInstance.onData((data) => {
        handleTerminalKeyInput(data);
    });

    // Connect SignalR
    initTerminalSignalR();
}

async function initTerminalSignalR() {
    if (typeof signalR === "undefined") {
        setTimeout(initTerminalSignalR, 300);
        return;
    }

    terminalHubConn = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/terminal")
        .withAutomaticReconnect([0, 2000, 5000, 10000])
        .build();

    terminalHubConn.on("TerminalOutput", (chunk) => {
        if (xtermInstance) {
            xtermInstance.write(chunk);
        }
    });

    terminalHubConn.on("TerminalClear", () => {
        if (xtermInstance) {
            xtermInstance.clear();
        }
    });

    terminalHubConn.on("SessionInitialized", (ok, currentDir) => {
        if (ok && currentDir) {
            terminalPrompt = `PS ${currentDir}> `;
        }
    });

    terminalHubConn.on("DevServerDetected", (url) => {
        showToastNotification(`Dev Server running at ${url}`);
    });

    // Auto-refresh file tree when a terminal command finishes successfully (e.g. dotnet new, ng new)
    terminalHubConn.on("TerminalCommandFinished", (info) => {
        console.log("[Terminal] Command finished:", info);
        if (!info || info.exitCode === 0) {
            refreshFileTree();
        }
    });

    terminalHubConn.onreconnected(() => {
        if (activeProjectId) {
            terminalHubConn.invoke("InitializeTerminal", activeProjectId).catch(console.error);
        }
    });

    try {
        await terminalHubConn.start();
        console.log("[Terminal] Connected to TerminalHub successfully.");
        if (activeProjectId) {
            await terminalHubConn.invoke("InitializeTerminal", activeProjectId);
        }
    } catch (err) {
        console.warn("[Terminal] Could not connect to TerminalHub:", err);
        if (xtermInstance) {
            xtermInstance.writeln("\x1b[33m[Notice] Running in standalone terminal mode.\x1b[0m");
            xtermInstance.write(`\r\n${terminalPrompt}`);
        }
    }
}

function handleTerminalKeyInput(data) {
    if (!xtermInstance) return;

    // Ctrl+C: ASCII 3 (\x03)
    if (data === '\x03') {
        if (terminalHubConn && terminalHubConn.state === signalR.HubConnectionState.Connected) {
            terminalHubConn.invoke("KillRunningProcess").catch(console.error);
        }
        xtermInstance.write("^C\r\n" + terminalPrompt);
        terminalInputBuffer = "";
        terminalCursorPos = 0;
        return;
    }

    // Enter: ASCII 13 (\r)
    if (data === '\r') {
        xtermInstance.write("\r\n");
        const cmd = terminalInputBuffer.trim();
        terminalInputBuffer = "";
        terminalCursorPos = 0;

        if (cmd) {
            terminalHistory.push(cmd);
            terminalHistoryIndex = terminalHistory.length;
            sendTerminalCommand(cmd);
        } else {
            xtermInstance.write(terminalPrompt);
        }
        return;
    }

    // Backspace: ASCII 127 (\x7f) or \b
    if (data === '\x7f' || data === '\b') {
        if (terminalCursorPos > 0) {
            terminalInputBuffer = terminalInputBuffer.slice(0, terminalCursorPos - 1) + terminalInputBuffer.slice(terminalCursorPos);
            terminalCursorPos--;
            refreshCurrentCommandLine();
        }
        return;
    }

    // Arrow keys: \x1b[A, \x1b[B, \x1b[C, \x1b[D
    if (data === '\x1b[A') { // Up arrow
        if (terminalHistory.length > 0 && terminalHistoryIndex > 0) {
            terminalHistoryIndex--;
            terminalInputBuffer = terminalHistory[terminalHistoryIndex];
            terminalCursorPos = terminalInputBuffer.length;
            refreshCurrentCommandLine();
        }
        return;
    }
    if (data === '\x1b[B') { // Down arrow
        if (terminalHistoryIndex < terminalHistory.length - 1) {
            terminalHistoryIndex++;
            terminalInputBuffer = terminalHistory[terminalHistoryIndex];
            terminalCursorPos = terminalInputBuffer.length;
            refreshCurrentCommandLine();
        } else {
            terminalHistoryIndex = terminalHistory.length;
            terminalInputBuffer = "";
            terminalCursorPos = 0;
            refreshCurrentCommandLine();
        }
        return;
    }
    if (data === '\x1b[C') { // Right arrow
        if (terminalCursorPos < terminalInputBuffer.length) {
            terminalCursorPos++;
            xtermInstance.write(data);
        }
        return;
    }
    if (data === '\x1b[D') { // Left arrow
        if (terminalCursorPos > 0) {
            terminalCursorPos--;
            xtermInstance.write(data);
        }
        return;
    }

    // Normal typing: printable chars (length 1, code >= 32)
    if (data.length === 1 && data.charCodeAt(0) >= 32) {
        terminalInputBuffer = terminalInputBuffer.slice(0, terminalCursorPos) + data + terminalInputBuffer.slice(terminalCursorPos);
        terminalCursorPos++;
        refreshCurrentCommandLine();
    } else if (data.length > 1 && !data.startsWith('\x1b')) {
        // Multi-char paste
        terminalInputBuffer = terminalInputBuffer.slice(0, terminalCursorPos) + data + terminalInputBuffer.slice(terminalCursorPos);
        terminalCursorPos += data.length;
        refreshCurrentCommandLine();
    }
}

function refreshCurrentCommandLine() {
    if (!xtermInstance) return;
    xtermInstance.write('\r\x1b[K' + terminalPrompt + terminalInputBuffer);
    const moveBack = terminalInputBuffer.length - terminalCursorPos;
    if (moveBack > 0) {
        xtermInstance.write(`\x1b[${moveBack}D`);
    }
}

// Smart Paste into Terminal (with clean multi-line and buffer synchronization)
async function pasteIntoTerminal() {
    if (!xtermInstance) return;
    try {
        const text = await navigator.clipboard.readText();
        if (text) {
            insertTextIntoTerminalBuffer(text);
        }
    } catch (err) {
        console.warn("[Terminal] Clipboard read error:", err);
    }
}

function insertTextIntoTerminalBuffer(text) {
    if (!xtermInstance || !text) return;
    const cleanText = text.replace(/\r\n/g, '\n').replace(/\r/g, '\n');
    if (cleanText.includes('\n')) {
        const lines = cleanText.split('\n');
        for (let i = 0; i < lines.length; i++) {
            const line = lines[i];
            terminalInputBuffer += line;
            terminalCursorPos = terminalInputBuffer.length;
            if (i < lines.length - 1) {
                const cmd = terminalInputBuffer.trim();
                xtermInstance.write(line + "\r\n");
                terminalInputBuffer = "";
                terminalCursorPos = 0;
                if (cmd) {
                    terminalHistory.push(cmd);
                    terminalHistoryIndex = terminalHistory.length;
                    sendTerminalCommand(cmd);
                }
            } else {
                refreshCurrentCommandLine();
            }
        }
    } else {
        terminalInputBuffer = terminalInputBuffer.slice(0, terminalCursorPos) + cleanText + terminalInputBuffer.slice(terminalCursorPos);
        terminalCursorPos += cleanText.length;
        refreshCurrentCommandLine();
    }
}

// Terminal Context Menu
function onTerminalContextMenu(e) {
    e.preventDefault();
    e.stopPropagation();
    hideExplorerContextMenu();

    const menu = document.getElementById("terminalContextMenu");
    if (!menu) return;

    const copyItem = document.getElementById("ctxTermCopy");
    if (copyItem) {
        if (xtermInstance && xtermInstance.hasSelection()) {
            copyItem.classList.remove("disabled");
        } else {
            copyItem.classList.add("disabled");
        }
    }

    menu.style.display = "block";
    const x = Math.min(e.clientX, window.innerWidth - 210);
    const y = Math.min(e.clientY, window.innerHeight - 190);
    menu.style.left = `${Math.max(10, x)}px`;
    menu.style.top = `${Math.max(10, y)}px`;
}

function hideTerminalContextMenu() {
    const menu = document.getElementById("terminalContextMenu");
    if (menu) menu.style.display = "none";
}

function terminalContextCopy() {
    hideTerminalContextMenu();
    if (xtermInstance && xtermInstance.hasSelection()) {
        const text = xtermInstance.getSelection();
        navigator.clipboard.writeText(text);
        xtermInstance.clearSelection();
        showToastNotification("Copied to clipboard");
    }
}

function terminalContextPaste() {
    hideTerminalContextMenu();
    pasteIntoTerminal();
}

function terminalContextSelectAll() {
    hideTerminalContextMenu();
    if (xtermInstance) {
        xtermInstance.selectAll();
    }
}

function terminalContextClear() {
    hideTerminalContextMenu();
    clearTerminalOutput();
}

async function sendTerminalCommand(cmd) {
    if (terminalHubConn && terminalHubConn.state === signalR.HubConnectionState.Connected) {
        try {
            await terminalHubConn.invoke("ExecuteCommand", activeProjectId || "my-project", cmd);
        } catch (err) {
            xtermInstance.writeln(`\r\n\x1b[31m[Error] ${err.message}\x1b[0m`);
            xtermInstance.write(`\r\n${terminalPrompt}`);
        }
    } else {
        await runCustomTerminalCmd(cmd);
    }
}

function switchBottomTab(tabName) {
    const bottomPanel = document.getElementById("bottomPanel");
    const resizer = document.getElementById("resizerTerminal");
    if (bottomPanel) {
        bottomPanel.classList.remove("ide-panel-hidden");
        bottomPanel.style.display = "flex";
        if (resizer) resizer.style.display = "block";
    }

    ['bottomTabBtnTerminal', 'bottomTabBtnProblems', 'bottomTabBtnOutput'].forEach(id => {
        document.getElementById(id)?.classList.remove("active");
    });
    ['bottomTabTerminalBody', 'bottomTabProblemsBody', 'bottomTabOutputBody'].forEach(id => {
        const el = document.getElementById(id);
        if (el) el.style.display = "none";
    });

    if (tabName === 'terminal') {
        document.getElementById('bottomTabBtnTerminal')?.classList.add("active");
        document.getElementById('bottomTabTerminalBody').style.display = "flex";
        setTimeout(() => {
            try { fitAddon?.fit(); } catch (e) { }
            try { xtermInstance?.focus(); } catch (e) { }
        }, 50);
    } else if (tabName === 'problems') {
        document.getElementById('bottomTabBtnProblems')?.classList.add("active");
        document.getElementById('bottomTabProblemsBody').style.display = "block";
    } else if (tabName === 'output') {
        document.getElementById('bottomTabBtnOutput')?.classList.add("active");
        document.getElementById('bottomTabOutputBody').style.display = "block";
    }
    triggerEditorLayout();
}

async function runCustomTerminalCmd(cmd) {
    switchBottomTab('terminal');
    if (xtermInstance) {
        xtermInstance.writeln(`\r\n\x1b[36m> ${cmd}\x1b[0m`);
    }

    if (terminalHubConn && terminalHubConn.state === signalR.HubConnectionState.Connected) {
        try {
            await terminalHubConn.invoke("ExecuteCommand", activeProjectId || "my-project", cmd);
            return;
        } catch (e) {
            console.warn("[Terminal] SignalR execute failed, fallback to REST:", e);
        }
    }

    try {
        const res = await fetch('/api/ide/terminal', {
            method: 'POST',
            headers: getApiHeaders(),
            body: JSON.stringify({
                project: activeProjectId,
                command: cmd
            })
        });
        const data = await res.json();
        if (data.ok) {
            if (xtermInstance) {
                xtermInstance.writeln(data.output || "(Command completed)");
                xtermInstance.write(`\r\n${terminalPrompt}`);
            }
        } else {
            showRestrictedLoginPrompt(data.message || "Thực thi terminal bị từ chối.");
        }
    } catch (err) {
        if (xtermInstance) {
            xtermInstance.writeln(`\x1b[31mError: ${err.message}\x1b[0m`);
            xtermInstance.write(`\r\n${terminalPrompt}`);
        }
    }
}

function clearTerminalOutput() {
    if (xtermInstance) {
        xtermInstance.clear();
        xtermInstance.write(terminalPrompt);
    }
}

async function resetTerminalSession() {
    if (terminalHubConn && terminalHubConn.state === signalR.HubConnectionState.Connected) {
        await terminalHubConn.invoke("ResetTerminalSession", activeProjectId || "my-project");
    } else {
        clearTerminalOutput();
    }
}

function runSmartAction() {
    switchBottomTab('terminal');
    if (!activeFilePath) {
        runCustomTerminalCmd("dotnet build");
        return;
    }

    const ext = activeFilePath.split('.').pop().toLowerCase();
    if (ext === 'js') {
        runCustomTerminalCmd(`node ${activeFilePath}`);
    } else if (ext === 'py') {
        runCustomTerminalCmd(`python ${activeFilePath}`);
    } else {
        runCustomTerminalCmd("dotnet build");
    }
}

function runBuildInTerminal() {
    switchBottomTab('terminal');
    runCustomTerminalCmd("dotnet build");
}

function runTestInTerminal() {
    switchBottomTab('terminal');
    runCustomTerminalCmd("dotnet test");
}

function closeCurrentTab() {
    if (activeFilePath) {
        closeTab(activeFilePath);
    }
}

// ==========================================
// TOP BAR DROPDOWN MENUS MANAGEMENT (8 MENUS + HOVER SWITCHING)
// ==========================================
let activeOpenMenu = null;

function toggleTopMenu(menuName, event) {
    if (event) event.stopPropagation();
    const menuIdMap = {
        'file': 'topMenuFile',
        'edit': 'topMenuEdit',
        'selection': 'topMenuSelection',
        'view': 'topMenuView',
        'go': 'topMenuGo',
        'run': 'topMenuRun',
        'terminal': 'topMenuTerminal',
        'help': 'topMenuHelp'
    };
    const targetId = menuIdMap[menuName];
    const targetEl = document.getElementById(targetId);
    const isVisible = targetEl && targetEl.style.display === 'block';

    closeAllTopMenus();

    if (targetEl && !isVisible) {
        targetEl.style.display = 'block';
        activeOpenMenu = menuName;
    }
}

function onMenuItemHover(menuName) {
    if (activeOpenMenu && activeOpenMenu !== menuName) {
        toggleTopMenu(menuName);
    }
}

function closeAllTopMenus() {
    ['topMenuFile', 'topMenuEdit', 'topMenuSelection', 'topMenuView', 'topMenuGo', 'topMenuRun', 'topMenuTerminal', 'topMenuHelp'].forEach(id => {
        const el = document.getElementById(id);
        if (el) el.style.display = 'none';
    });
    activeOpenMenu = null;
}

document.addEventListener('click', (e) => {
    if (!e.target.closest('.top-bar-menu-wrapper')) {
        closeAllTopMenus();
    }
});

// ==========================================
// MONACO ACTIONS (EDIT / SELECTION / VIEW / GO / HELP)
// ==========================================
// --- Edit Menu Actions ---
function editorUndo() { monacoEditor?.trigger('menu', 'undo'); }
function editorRedo() { monacoEditor?.trigger('menu', 'redo'); }
function editorCut() { monacoEditor?.trigger('menu', 'editor.action.clipboardCutAction'); }
function editorCopy() { monacoEditor?.trigger('menu', 'editor.action.clipboardCopyAction'); }
function editorPaste() { monacoEditor?.trigger('menu', 'editor.action.clipboardPasteAction'); }
function editorFind() { monacoEditor?.trigger('menu', 'actions.find'); }
function editorReplace() { monacoEditor?.trigger('menu', 'editor.action.startFindReplaceAction'); }

// --- Selection Menu Actions ---
function editorSelectAll() { monacoEditor?.trigger('menu', 'editor.action.selectAll'); }
function editorExpandSelection() { monacoEditor?.trigger('menu', 'editor.action.smartSelect.expand'); }
function editorShrinkSelection() { monacoEditor?.trigger('menu', 'editor.action.smartSelect.shrink'); }
function editorCopyLineUp() { monacoEditor?.trigger('menu', 'editor.action.copyLinesUpAction'); }
function editorCopyLineDown() { monacoEditor?.trigger('menu', 'editor.action.copyLinesDownAction'); }
function editorMoveLineUp() { monacoEditor?.trigger('menu', 'editor.action.moveLinesUpAction'); }
function editorMoveLineDown() { monacoEditor?.trigger('menu', 'editor.action.moveLinesDownAction'); }
function editorAddCursorAbove() { monacoEditor?.trigger('menu', 'editor.action.insertCursorAbove'); }
function editorAddCursorBelow() { monacoEditor?.trigger('menu', 'editor.action.insertCursorBelow'); }
function editorSelectNextOccurrence() { monacoEditor?.trigger('menu', 'editor.action.addSelectionToNextFindMatch'); }

// --- View Menu Actions ---
let isWordWrap = false;
function toggleWordWrap() {
    isWordWrap = !isWordWrap;
    monacoEditor?.updateOptions({ wordWrap: isWordWrap ? 'on' : 'off' });
}

let isMinimap = true;
function toggleMinimap() {
    isMinimap = !isMinimap;
    monacoEditor?.updateOptions({ minimap: { enabled: isMinimap } });
}

let currentFontSize = 13;
function zoomInEditor() {
    currentFontSize += 1;
    monacoEditor?.updateOptions({ fontSize: currentFontSize });
}
function zoomOutEditor() {
    if (currentFontSize > 9) {
        currentFontSize -= 1;
        monacoEditor?.updateOptions({ fontSize: currentFontSize });
    }
}
function resetZoomEditor() {
    currentFontSize = 13;
    monacoEditor?.updateOptions({ fontSize: 13 });
}

// --- Go Menu Actions ---
function editorGotoLine() { monacoEditor?.trigger('menu', 'editor.action.gotoLine'); }
function editorGotoSymbol() { monacoEditor?.trigger('menu', 'editor.action.quickOutline'); }

const tabHistory = [];
let tabHistoryIndex = -1;

function recordTabNavigation(path) {
    if (tabHistory[tabHistoryIndex] !== path) {
        tabHistory.splice(tabHistoryIndex + 1);
        tabHistory.push(path);
        tabHistoryIndex = tabHistory.length - 1;
    }
}

function navigateTabBack() {
    if (tabHistoryIndex > 0) {
        tabHistoryIndex--;
        openFileInEditor(tabHistory[tabHistoryIndex]);
    }
}

function navigateTabForward() {
    if (tabHistoryIndex < tabHistory.length - 1) {
        tabHistoryIndex++;
        openFileInEditor(tabHistory[tabHistoryIndex]);
    }
}

// --- Help Menu Modals ---
function showHelpQuickStart() { showModalSafely("helpQuickStartModal"); }
function showHelpShortcuts() { showModalSafely("helpShortcutsModal"); }
function showHelpStatus() {
    const actEl = document.getElementById("helpStatusActiveProj");
    if (actEl) actEl.innerText = activeProjectId || "None";
    showModalSafely("helpStatusModal");
}
function showHelpAbout() { showModalSafely("helpAboutModal"); }

// ==========================================
// UPLOAD FOLDER / FILES & DOWNLOAD ZIP & PREVIEW
// ==========================================
function triggerFolderUpload() {
    const input = document.getElementById("folderUploadInput");
    if (input) input.click();
}

function triggerFilesUpload() {
    const input = document.getElementById("filesUploadInput");
    if (input) input.click();
}

async function handleFolderSelected(event) {
    const files = event.target.files;
    if (!files || files.length === 0) return;

    let proj = activeProjectId;
    if (!proj) {
        // Lấy tên thư mục cấp cao nhất từ file đầu tiên
        const firstRel = files[0].webkitRelativePath || files[0].name;
        const defaultName = firstRel.split('/')[0] || "UploadedProject";
        const customName = prompt("Nhập tên dự án cho thư mục tải lên:", defaultName);
        if (!customName) return;
        proj = customName.trim();

        // Tạo project trước
        await fetch('/api/ide/projects/create', {
            method: 'POST',
            headers: getApiHeaders(),
            body: JSON.stringify({ name: proj, template: 'empty' })
        });
    }

    const formData = new FormData();
    formData.append("project", proj);
    for (let i = 0; i < files.length; i++) {
        formData.append("files", files[i]);
        formData.append("relativePaths", files[i].webkitRelativePath || files[i].name);
    }

    const rootNameEl = document.getElementById("workspaceRootName");
    if (rootNameEl) rootNameEl.innerHTML = `<span class="spinner-border spinner-border-sm me-1"></span> Đang tải lên ${files.length} tệp...`;

    try {
        const res = await fetch('/api/ide/upload-folder', {
            method: 'POST',
            body: formData
        });
        const data = await res.json();
        if (data.ok) {
            await loadUserProjects();
            activeProjectId = proj;
            const select = document.getElementById("projectSelect");
            if (select) select.value = proj;
            refreshFileTree();
            alert(`✅ Đã tải lên thành công ${data.count || files.length} tệp vào dự án "${proj}"!`);
        } else {
            alert("❌ Lỗi tải thư mục: " + (data.message || "Unknown error"));
        }
    } catch (err) {
        alert("❌ Lỗi kết nối khi tải thư mục: " + err.message);
    }
}

async function handleFilesSelected(event) {
    const files = event.target.files;
    if (!files || files.length === 0) return;

    if (!activeProjectId) {
        alert("Vui lòng tạo hoặc chọn một dự án trước khi tải tệp tin lên!");
        return;
    }

    const formData = new FormData();
    formData.append("project", activeProjectId);
    for (let i = 0; i < files.length; i++) {
        formData.append("files", files[i]);
    }

    try {
        const res = await fetch('/api/ide/upload-files', {
            method: 'POST',
            body: formData
        });
        const data = await res.json();
        if (data.ok) {
            refreshFileTree();
            alert(`✅ Đã tải lên ${files.length} tệp thành công!`);
        } else {
            alert("❌ Lỗi: " + (data.message || "Unknown error"));
        }
    } catch (err) {
        alert("❌ Lỗi kết nối: " + err.message);
    }
}

function downloadCurrentProjectZip() {
    if (!activeProjectId) {
        alert("Chưa có dự án nào được chọn để tải về.");
        return;
    }
    window.location.href = `/api/ide/projects/download?project=${encodeURIComponent(activeProjectId)}`;
}

function openLivePreview() {
    if (!activeProjectId) {
        alert("Chưa có dự án nào được chọn. Hãy tạo hoặc mở dự án web có index.html để xem trước.");
        return;
    }

    const previewUrl = `/api/ide/preview/${encodeURIComponent(activeProjectId)}/index.html`;
    const iframe = document.getElementById("previewIframe");
    const badge = document.getElementById("previewModalUrlBadge");
    const newTabBtn = document.getElementById("previewOpenTabBtn");

    if (iframe) iframe.src = previewUrl;
    if (badge) badge.innerText = previewUrl;
    if (newTabBtn) newTabBtn.href = previewUrl;

    showModalSafely("previewModal");
}

function refreshPreviewIframe() {
    const iframe = document.getElementById("previewIframe");
    if (iframe) iframe.src = iframe.src;
}

function focusAgentScaffolding() {
    const panel = document.getElementById("rightAgentPanel");
    if (panel && panel.style.display === "none") {
        toggleRightAgentPanel();
    }
    const input = document.getElementById("agentTaskInput");
    if (input) {
        input.value = "Tạo một trang web shop bán hoa tươi mini bằng HTML5, CSS3 và JavaScript với giao diện hiện đại, responsive, có giỏ hàng tính tiền và danh mục sản phẩm.";
        input.focus();
    }
}

// ==========================================
// 7. ANTIGRAVITY AGENT & TOOL LOOP
// ==========================================
function quickAgentPrompt(text) {
    const input = document.getElementById("agentTaskInput");
    if (input) {
        input.value = text;
        input.focus();
    }
}

function resetAgentChat() {
    const splash = document.getElementById("chatSplashArea");
    const container = document.getElementById("agentMessagesContainer");
    if (splash) splash.style.display = "flex";
    if (container) {
        container.style.display = "none";
        container.innerHTML = "";
    }
}

function onPromptInput(e) {
    const val = e.target.value;
    const mentionPopup = document.getElementById("mentionPopup");
    const cmdPopup = document.getElementById("commandPopup");

    if (val.endsWith("@")) {
        showMentionPopup();
        if (cmdPopup) cmdPopup.style.display = "none";
    } else if (val.endsWith("/")) {
        if (cmdPopup) cmdPopup.style.display = "block";
        if (mentionPopup) mentionPopup.style.display = "none";
    } else {
        if (mentionPopup) mentionPopup.style.display = "none";
        if (cmdPopup) cmdPopup.style.display = "none";
    }
}

function onPromptKeyDown(e) {
    if (e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault();
        sendAgentTask();
    }
}

function showMentionPopup() {
    const popup = document.getElementById("mentionPopup");
    const list = document.getElementById("mentionFileList");
    if (!popup || !list) return;

    list.innerHTML = "";
    workspaceFilesList.slice(0, 10).forEach(f => {
        const item = document.createElement("div");
        item.className = "autocomplete-item";
        item.innerHTML = `<i class="bi bi-file-earmark-code text-info"></i> <span>${f.name}</span>`;
        item.onclick = () => {
            insertFileMention(f.name);
            popup.style.display = "none";
        };
        list.appendChild(item);
    });
    popup.style.display = "block";
}

function insertFileMention(fileName) {
    const input = document.getElementById("agentTaskInput");
    if (!input) return;
    input.value = input.value.replace(/@\w*$/, '') + `@${fileName} `;
    input.focus();
}

function insertSlashCommand(cmd) {
    const input = document.getElementById("agentTaskInput");
    if (!input) return;
    input.value = cmd + " ";
    document.getElementById("commandPopup").style.display = "none";
    input.focus();
}

function removeContextChip() {
    const chip = document.getElementById("activeFileChip");
    if (chip) chip.style.display = "none";
}

async function sendAgentTask() {
    const input = document.getElementById("agentTaskInput");
    if (!input) return;
    const task = input.value.trim();
    if (!task) return;
    input.value = "";

    // Hide splash, show messages
    document.getElementById("chatSplashArea").style.display = "none";
    const container = document.getElementById("agentMessagesContainer");
    container.style.display = "flex";

    // Append User Message
    const userMsg = document.createElement("div");
    userMsg.className = "p-2 rounded bg-dark border border-secondary text-light extra-small align-self-end";
    userMsg.style.maxWidth = "85%";
    userMsg.innerText = task;
    container.appendChild(userMsg);

    // Append Agent Loading Indicator
    const agentMsg = document.createElement("div");
    agentMsg.className = "d-flex flex-column gap-2 extra-small text-light";
    agentMsg.innerHTML = `
        <div class="task-running-pill">
            <div class="spinner-border spinner-border-sm text-info" style="width: 12px; height: 12px;"></div>
            <span>Antigravity Agent is inspecting workspace and synthesizing solution...</span>
        </div>
    `;
    container.appendChild(agentMsg);
    container.scrollTop = container.scrollHeight;

    const model = document.getElementById("agentModelSelect")?.value || "gemini-2.5-flash";
    const mode = document.getElementById("agentModeSelect")?.value || "fast";

    try {
        const res = await fetch('/api/ide/agent-run', {
            method: 'POST',
            headers: getApiHeaders(),
            body: JSON.stringify({
                project: activeProjectId,
                activeFile: activeFilePath,
                prompt: task,
                mode: mode,
                model: model
            })
        });
        const data = await res.json();

        if (data.ok) {
            renderAgentResponse(data, agentMsg);
        } else {
            agentMsg.innerHTML = `<div class="text-danger extra-small p-2 bg-dark rounded border border-danger">❌ ${data.message || "Agent execution failed"}</div>`;
        }
    } catch (err) {
        agentMsg.innerHTML = `<div class="text-danger extra-small p-2 bg-dark rounded border border-danger">❌ Error: ${err.message}</div>`;
    }
}

function renderAgentResponse(data, container) {
    container.innerHTML = "";

    // 0. CLI Scaffolding Commands Card (if Agent requested setup commands)
    if (Array.isArray(data.setupCommands) && data.setupCommands.length > 0) {
        window.currentAgentSetupCommands = data.setupCommands;
        const cliCard = document.createElement("div");
        cliCard.className = "agent-cli-scaffold-card p-2 rounded bg-dark border border-info mb-2";
        cliCard.innerHTML = `
            <div class="d-flex align-items-center justify-content-between mb-1.5">
                <span class="fw-semibold text-info extra-small"><i class="bi bi-terminal-fill me-1"></i> CLI Scaffolding Commands</span>
                <span class="badge bg-primary extra-small">${data.setupCommands.length} lệnh</span>
            </div>
            <div class="d-flex flex-column gap-1 mb-2 font-mono" style="font-size: 11px;">
                ${data.setupCommands.map(cmd => `
                    <div class="p-1 px-2 rounded bg-black border border-secondary text-light d-flex align-items-center">
                        <span class="text-secondary me-1.5">&gt;</span>
                        <span>${escapeHtml(cmd)}</span>
                    </div>
                `).join('')}
            </div>
            <button class="btn btn-sm btn-info text-dark fw-bold w-100 extra-small py-1 d-flex align-items-center justify-content-center gap-1.5" onclick="runScaffoldingAndApplyAll()">
                <i class="bi bi-lightning-charge-fill"></i> ⚡ Chạy lệnh khởi tạo & Áp dụng (Run & Apply)
            </button>
        `;
        container.appendChild(cliCard);
    }

    // 1. Plan Card
    if (Array.isArray(data.plan) && data.plan.length > 0) {
        const planCard = document.createElement("div");
        planCard.className = "agent-plan-card";
        planCard.innerHTML = `
            <div class="agent-plan-title"><i class="bi bi-list-check"></i> Plan:</div>
            ${data.plan.map(p => `
                <div class="agent-plan-item">
                    <i class="bi bi-check2 text-success"></i>
                    <span>${escapeHtml(p)}</span>
                </div>
            `).join('')}
        `;
        container.appendChild(planCard);
    }

    // 2. Explanation / Message
    if (data.explanation) {
        const expEl = document.createElement("div");
        expEl.className = "p-2 rounded bg-dark border border-secondary text-light extra-small";
        expEl.innerText = data.explanation;
        container.appendChild(expEl);
    }

    // 3. Multi-file Operations & Generated Images (Full-Project Scaffolding)
    const hasOps = Array.isArray(data.operations) && data.operations.length > 0;
    const hasImgs = Array.isArray(data.images) && data.images.length > 0;

    if (hasOps || hasImgs) {
        const opsCard = document.createElement("div");
        opsCard.className = "agent-operations-card";

        const opsList = data.operations || [];
        const imgsList = data.images || [];

        // Lưu global để review và accept
        window.currentAgentOperations = opsList;
        window.currentAgentImages = imgsList;

        const opsHtml = opsList.map((op, idx) => {
            const action = (op.action || "edit").toLowerCase();
            const badgeClass = action === "create" ? "badge-op-create" : (action === "delete" ? "badge-op-delete" : "badge-op-edit");
            const badgeLabel = action === "create" ? "NEW" : (action === "delete" ? "DEL" : "MOD");
            const fileName = (op.path || "").split(/[\\/]/).pop();
            return `
                <div class="agent-op-item">
                    <div class="agent-op-info">
                        <span class="badge-op ${badgeClass}">${badgeLabel}</span>
                        <span class="agent-op-path" title="${escapeHtml(op.path)}">${escapeHtml(fileName)}</span>
                    </div>
                    <button class="btn-op-review" onclick="reviewSingleOperation(${idx})"><i class="bi bi-eye"></i> Review</button>
                </div>
            `;
        }).join('');

        const imgsHtml = imgsList.map((img, idx) => {
            const fileName = (img.path || "").split(/[\\/]/).pop();
            return `
                <div class="agent-op-item">
                    <div class="agent-op-info">
                        <span class="badge-op badge-op-image">IMAGE</span>
                        <span class="agent-op-path" title="${escapeHtml(img.prompt)}"><i class="bi bi-image text-info me-1"></i>${escapeHtml(fileName)}</span>
                    </div>
                    <button class="btn-op-review" onclick="alert('Prompt ảnh AI:\n\n' + ${JSON.stringify(img.prompt)})"><i class="bi bi-stars"></i> Prompt</button>
                </div>
            `;
        }).join('');

        const totalItems = opsList.length + imgsList.length;
        opsCard.innerHTML = `
            <div class="agent-operations-header">
                <span><i class="bi bi-files me-1.5 text-info"></i> ${totalItems} tệp & hình ảnh đề xuất</span>
                <span class="badge bg-secondary extra-small">AI Scaffolding</span>
            </div>
            <div class="d-flex flex-column gap-1.5 my-1">
                ${opsHtml}
                ${imgsHtml}
            </div>
            <button id="btnBatchAcceptAll" class="btn-batch-accept w-100" onclick="applyAllAgentOperations()">
                <i class="bi bi-check2-all fs-6"></i> Áp dụng tất cả (${opsList.length} mã nguồn, ${imgsList.length} ảnh AI)
            </button>
        `;
        container.appendChild(opsCard);
    }
    // 4. Single Diff Summary Card (4 files changed +51 -3 [Review])
    else if (data.proposedFile && data.proposedContent) {
        activeDiffProposal = {
            file: data.proposedFile,
            content: data.proposedContent,
            original: data.originalContent || ""
        };

        const diffCard = document.createElement("div");
        diffCard.className = "agent-diff-card";
        diffCard.innerHTML = `
            <div class="agent-diff-summary mb-2">
                <span class="fw-semibold text-light">${data.proposedFile.split(/[\\/]/).pop()}</span>
                <span>
                    <span class="diff-count-add">+${data.additions || 12}</span>
                    <span class="diff-count-del">-${data.deletions || 2}</span>
                </span>
            </div>
            <div class="d-flex align-items-center gap-2">
                <button onclick="reviewAgentDiff()" class="btn-review-diff"><i class="bi bi-eye me-1"></i> Review</button>
                <button onclick="acceptCurrentDiff()" class="btn btn-sm btn-success extra-small py-0.5 px-2.5 fw-semibold"><i class="bi bi-check-lg me-1"></i> Accept All</button>
                <button onclick="rejectCurrentDiff()" class="btn btn-sm btn-outline-danger extra-small py-0.5 px-2"><i class="bi bi-x-lg me-1"></i> Reject</button>
            </div>
        `;
        container.appendChild(diffCard);
    }
}

async function runScaffoldingAndApplyAll() {
    const cmds = window.currentAgentSetupCommands || [];
    switchBottomTab('terminal');

    if (cmds.length > 0) {
        showToastNotification(`Đang thực thi ${cmds.length} lệnh scaffolding khởi tạo...`);
        for (const cmd of cmds) {
            if (terminalHubConn && terminalHubConn.state === signalR.HubConnectionState.Connected) {
                await terminalHubConn.invoke("ExecuteCommand", activeProjectId || "my-project", cmd);
            }
        }
    }

    if (window.currentAgentOperations && window.currentAgentOperations.length > 0) {
        await applyAllAgentOperations();
    } else {
        await refreshFileTree();
    }
}

function reviewSingleOperation(index) {
    if (!window.currentAgentOperations || !window.currentAgentOperations[index]) return;
    const op = window.currentAgentOperations[index];
    activeDiffProposal = {
        file: op.path,
        content: op.content || "",
        original: "" // File mới thì original là rỗng
    };
    reviewAgentDiff();
}

async function applyAllAgentOperations() {
    const ops = window.currentAgentOperations || [];
    const imgs = window.currentAgentImages || [];
    if (ops.length === 0 && imgs.length === 0) return;

    let proj = activeProjectId;
    if (!proj) {
        const customName = prompt("Nhập tên dự án mới để lưu các tệp tin này:", "MyWebProject");
        if (!customName) return;
        proj = customName.trim();

        // Tạo project
        await fetch('/api/ide/projects/create', {
            method: 'POST',
            headers: getApiHeaders(),
            body: JSON.stringify({ name: proj, template: 'empty' })
        });
        await loadUserProjects();
        activeProjectId = proj;
        const select = document.getElementById("projectSelect");
        if (select) select.value = proj;
    }

    const btn = document.getElementById("btnBatchAcceptAll");
    if (btn) {
        btn.disabled = true;
        btn.innerHTML = `<div class="spinner-border spinner-border-sm me-1"></div> Đang sinh ảnh AI & tạo ${ops.length + imgs.length} tệp...`;
    }

    try {
        const res = await fetch('/api/ide/files/apply-batch', {
            method: 'POST',
            headers: getApiHeaders(),
            body: JSON.stringify({
                project: proj,
                operations: ops,
                images: imgs
            })
        });
        const data = await res.json();
        if (data.ok) {
            closeDiffView();
            hideEmptyWorkspaceState();
            await refreshFileTree();

            // Tự động mở file index.html hoặc file đầu tiên
            const indexFile = ops.find(o => o.path.endsWith("index.html") || o.path === "index.html");
            if (indexFile) {
                openFileInEditor(indexFile.path);
            } else if (ops[0]?.path) {
                openFileInEditor(ops[0].path);
            }

            // In thông báo vào Terminal Output
            if (xtermInstance) {
                xtermInstance.writeln(`\r\n\x1b[32m[Antigravity Agent] Đã áp dụng thành công ${data.appliedCount || (ops.length + imgs.length)} tệp và hình ảnh vào dự án "${proj}"!\x1b[0m`);
                xtermInstance.write(`\r\n${terminalPrompt}`);
            }

            if (btn) {
                btn.className = "btn btn-sm btn-success w-100 py-1.5 fw-semibold";
                btn.innerHTML = `<i class="bi bi-check2-circle me-1"></i> Đã áp dụng thành công!`;
            }

            alert(`🎉 Đã tạo thành công dự án "${proj}" gồm ${ops.length} tệp mã nguồn và ${imgs.length} hình ảnh sinh bằng AI!\n\nNhấn nút [🌐 Preview] trên thanh Tab để trải nghiệm trực tiếp!`);
        } else {
            if (btn) {
                btn.disabled = false;
                btn.innerHTML = `<i class="bi bi-check2-all fs-6"></i> Áp dụng tất cả`;
            }
            alert("❌ Lỗi áp dụng tệp tin: " + (data.message || "Unknown error"));
        }
    } catch (err) {
        if (btn) {
            btn.disabled = false;
            btn.innerHTML = `<i class="bi bi-check2-all fs-6"></i> Áp dụng tất cả`;
        }
        alert("❌ Lỗi kết nối: " + err.message);
    }
}

// ==========================================
// 8. MONACO DIFF VIEW (REVIEW & MERGE)
// ==========================================
function reviewAgentDiff() {
    if (!activeDiffProposal || !monacoDiffEditor) return;

    const banner = document.getElementById("diffViewBanner");
    const mContainer = document.getElementById("monacoContainer");
    const diffContainer = document.getElementById("monacoDiffContainer");
    const diffName = document.getElementById("diffFileName");

    if (diffName) diffName.innerText = activeDiffProposal.file;
    if (banner) banner.style.display = "flex";
    if (mContainer) mContainer.style.display = "none";
    if (diffContainer) diffContainer.style.display = "block";

    const lang = getMonacoLanguage(activeDiffProposal.file);
    const origModel = monaco.editor.createModel(activeDiffProposal.original, lang);
    const modModel = monaco.editor.createModel(activeDiffProposal.content, lang);

    monacoDiffEditor.setModel({
        original: origModel,
        modified: modModel
    });
}

async function acceptCurrentDiff() {
    if (!activeDiffProposal) return;
    const file = activeDiffProposal.file;
    const content = activeDiffProposal.content;

    try {
        const res = await fetch('/api/ide/save', {
            method: 'POST',
            headers: getApiHeaders(),
            body: JSON.stringify({
                project: activeProjectId,
                file: file,
                content: content
            })
        });
        const data = await res.json();

        if (data.ok) {
            closeDiffView();
            // Reload file in editor
            openTabsMap.delete(file);
            await openFileInEditor(file);
            loadGitStatus();
            alert("✅ Đã áp dụng và lưu thay đổi thành công!");
        } else {
            showRestrictedLoginPrompt(data.message || "Không thể áp dụng thay đổi ở chế độ xem an toàn.");
        }
    } catch (err) {
        alert("❌ Lỗi khi áp dụng thay đổi: " + err.message);
    }
}

function rejectCurrentDiff() {
    closeDiffView();
    activeDiffProposal = null;
}

function closeDiffView() {
    const banner = document.getElementById("diffViewBanner");
    const mContainer = document.getElementById("monacoContainer");
    const diffContainer = document.getElementById("monacoDiffContainer");

    if (banner) banner.style.display = "none";
    if (mContainer) mContainer.style.display = "block";
    if (diffContainer) diffContainer.style.display = "none";
}

function toggleDiffSideBySide() {
    if (!monacoDiffEditor) return;
    const current = monacoDiffEditor.getRenderSideBySide();
    monacoDiffEditor.updateOptions({ renderSideBySide: !current });
}

// ==========================================
// 9. MODALS: CREATE PROJECT / FILE / FOLDER
// ==========================================
function openCreateProjectModal() {
    const errBox = document.getElementById("createProjectErrorMsg");
    if (errBox) errBox.style.display = "none";
    const nameInput = document.getElementById("newProjectName");
    if (nameInput) nameInput.value = "";
    showModalSafely("createProjectModal");
    setTimeout(() => nameInput?.focus(), 150);
}
function openImportGitModal() { showModalSafely("importGitModal"); }
function openCreateFileModal() { showModalSafely("createFileModal"); }
function openCreateFolderModal() { showModalSafely("createFolderModal"); }
function openQuickOpenModal() {
    const q = prompt("Nhập tên file cần mở nhanh (Ctrl+P):");
    if (q) {
        const match = workspaceFilesList.find(f => f.name.toLowerCase().includes(q.toLowerCase()));
        if (match) openFileInEditor(match.path);
        else alert("Không tìm thấy file phù hợp.");
    }
}

async function submitCreateProject() {
    const errBox = document.getElementById("createProjectErrorMsg");
    if (errBox) errBox.style.display = "none";

    const name = document.getElementById("newProjectName")?.value?.trim();
    const template = document.getElementById("newProjectTemplate")?.value || "empty";
    if (!name) {
        if (errBox) {
            errBox.textContent = "Vui lòng nhập tên dự án.";
            errBox.style.display = "block";
        }
        showToastNotification("Vui lòng nhập tên dự án.");
        return;
    }

    try {
        const res = await fetch('/api/ide/create-project', {
            method: 'POST',
            headers: getApiHeaders(),
            body: JSON.stringify({ name, template: "empty" })
        });
        const data = await res.json();
        if (data.ok) {
            hideModalSafely("createProjectModal");
            await loadUserProjects();

            // Switch to the newly created project
            const cleanName = data.project || name;
            await switchActiveProject(cleanName);

            // Template scaffolding CLI command mapping
            let scaffoldCmd = "";
            if (template === "dotnet-mvc") {
                scaffoldCmd = "dotnet new mvc";
            } else if (template === "dotnet-webapi") {
                scaffoldCmd = "dotnet new webapi --use-controllers";
            } else if (template === "angular") {
                scaffoldCmd = "npx -y @angular/cli new frontend --routing --style=scss --ssr=false --skip-git";
            } else if (template === "fullstack") {
                scaffoldCmd = "dotnet new webapi --use-controllers -n backend && npx -y @angular/cli new frontend --routing --style=scss --ssr=false --skip-git";
            } else if (template === "html5") {
                scaffoldCmd = "echo ^<!DOCTYPE html^>^<html^>^<head^>^<title^>" + cleanName + "^</title^>^</head^>^<body^>^<h1^>Welcome to " + cleanName + "^</h1^>^</body^>^</html^> > index.html";
            }

            if (scaffoldCmd) {
                switchBottomTab('terminal');
                showToastNotification(`Khởi tạo template [${template}]...`);
                setTimeout(async () => {
                    if (terminalHubConn && terminalHubConn.state === signalR.HubConnectionState.Connected) {
                        await terminalHubConn.invoke("ExecuteCommand", cleanName, scaffoldCmd);
                    }
                }, 600);
            } else {
                showToastNotification(`Đã tạo dự án ${cleanName} thành công!`);
            }
        } else {
            if (data.needLogin) {
                showRestrictedLoginPrompt(data.error);
            } else {
                const errorText = data.error || data.message || "Tạo dự án thất bại";
                if (errBox) {
                    errBox.textContent = "⚠️ " + errorText;
                    errBox.style.display = "block";
                }
                showToastNotification("⚠️ " + errorText);
            }
        }
    } catch (err) {
        if (errBox) {
            errBox.textContent = "❌ Lỗi: " + err.message;
            errBox.style.display = "block";
        }
        showToastNotification("Lỗi: " + err.message);
    }
}

async function submitImportGit() {
    const name = document.getElementById("gitProjectName")?.value?.trim();
    const url = document.getElementById("gitRepoUrl")?.value?.trim();
    if (!name || !url) return alert("Vui lòng nhập tên dự án và URL git.");

    try {
        const res = await fetch('/api/ide/git-clone', {
            method: 'POST',
            headers: getApiHeaders(),
            body: JSON.stringify({ name, repoUrl: url })
        });
        const data = await res.json();
        if (data.ok) {
            hideModalSafely("importGitModal");
            await loadUserProjects();
            alert(`✅ Đã import Git repository thành công!`);
        } else {
            showRestrictedLoginPrompt(data.message);
        }
    } catch (err) {
        alert("❌ Lỗi: " + err.message);
    }
}

async function submitCreateFileModal() {
    const file = document.getElementById("createFileInput")?.value?.trim();
    if (!file) return;

    try {
        const res = await fetch('/api/ide/create-file', {
            method: 'POST',
            headers: getApiHeaders(),
            body: JSON.stringify({ project: activeProjectId, file })
        });
        const data = await res.json();
        if (data.ok) {
            hideModalSafely("createFileModal");
            await refreshFileTree();
            await openFileInEditor(file);
        } else {
            showRestrictedLoginPrompt(data.message);
        }
    } catch (err) {
        alert("❌ Lỗi: " + err.message);
    }
}

async function submitCreateFolderModal() {
    const folder = document.getElementById("createFolderInput")?.value?.trim();
    if (!folder) return;

    try {
        const res = await fetch('/api/ide/create-folder', {
            method: 'POST',
            headers: getApiHeaders(),
            body: JSON.stringify({ project: activeProjectId, folder })
        });
        const data = await res.json();
        if (data.ok) {
            hideModalSafely("createFolderModal");
            await refreshFileTree();
        } else {
            showRestrictedLoginPrompt(data.message);
        }
    } catch (err) {
        alert("❌ Lỗi: " + err.message);
    }
}

function escapeHtml(str) {
    if (!str) return "";
    return str.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}

// ==========================================
// 12. DRAGGABLE SPLITTERS & PANELS
// ==========================================
function initDraggableSplitters() {
    // 1. Explorer Vertical Resizer
    const resizerExp = document.getElementById("resizerExplorer");
    const primarySidebar = document.getElementById("primarySidebar");
    if (resizerExp && primarySidebar) {
        const savedW = localStorage.getItem("antigravity_explorer_w");
        if (savedW) primarySidebar.style.width = savedW;

        let isDragging = false;
        resizerExp.addEventListener("mousedown", (e) => {
            isDragging = true;
            document.body.style.cursor = "col-resize";
            document.body.style.userSelect = "none";
        });

        window.addEventListener("mousemove", (e) => {
            if (!isDragging) return;
            const newW = Math.max(160, Math.min(600, e.clientX - 48)); // 48px is activity bar
            primarySidebar.style.width = `${newW}px`;
            triggerEditorLayout();
        });

        window.addEventListener("mouseup", () => {
            if (isDragging) {
                isDragging = false;
                document.body.style.cursor = "";
                document.body.style.userSelect = "";
                localStorage.setItem("antigravity_explorer_w", primarySidebar.style.width);
                triggerEditorLayout();
            }
        });
    }

    // 2. Right Agent Panel Vertical Resizer
    const resizerAgent = document.getElementById("resizerAgent");
    const rightPanel = document.getElementById("rightAgentPanel");
    if (resizerAgent && rightPanel) {
        const savedW = localStorage.getItem("antigravity_agent_w");
        if (savedW) rightPanel.style.width = savedW;

        let isDragging = false;
        resizerAgent.addEventListener("mousedown", (e) => {
            isDragging = true;
            document.body.style.cursor = "col-resize";
            document.body.style.userSelect = "none";
        });

        window.addEventListener("mousemove", (e) => {
            if (!isDragging) return;
            const newW = Math.max(260, Math.min(800, window.innerWidth - e.clientX));
            rightPanel.style.width = `${newW}px`;
            triggerEditorLayout();
        });

        window.addEventListener("mouseup", () => {
            if (isDragging) {
                isDragging = false;
                document.body.style.cursor = "";
                document.body.style.userSelect = "";
                localStorage.setItem("antigravity_agent_w", rightPanel.style.width);
                triggerEditorLayout();
            }
        });
    }

    // 3. Bottom Terminal Horizontal Resizer
    const resizerTerminal = document.getElementById("resizerTerminal");
    const bottomPanel = document.getElementById("bottomPanel");
    if (resizerTerminal && bottomPanel) {
        const savedH = localStorage.getItem("antigravity_terminal_h");
        if (savedH) bottomPanel.style.height = savedH;

        let isDragging = false;
        resizerTerminal.addEventListener("mousedown", (e) => {
            isDragging = true;
            document.body.style.cursor = "row-resize";
            document.body.style.userSelect = "none";
        });

        window.addEventListener("mousemove", (e) => {
            if (!isDragging) return;
            const newH = Math.max(100, Math.min(window.innerHeight - 150, window.innerHeight - e.clientY - 24)); // 24px status bar
            bottomPanel.style.height = `${newH}px`;
            triggerEditorLayout();
        });

        window.addEventListener("mouseup", () => {
            if (isDragging) {
                isDragging = false;
                document.body.style.cursor = "";
                document.body.style.userSelect = "";
                localStorage.setItem("antigravity_terminal_h", bottomPanel.style.height);
                triggerEditorLayout();
            }
        });
    }
}

// ==========================================
// 13. GLOBAL KEYBOARD SHORTCUTS & ACTIONS
// ==========================================
function initGlobalShortcuts() {
    window.addEventListener("keydown", (e) => {
        // Command Palette: Ctrl+Shift+P or F1
        if ((e.ctrlKey && e.shiftKey && (e.key === "P" || e.key === "p")) || e.key === "F1") {
            e.preventDefault();
            openCommandPalette();
            return;
        }

        // Toggle Terminal: Ctrl + ` (tilde / backquote)
        if (e.ctrlKey && (e.key === "`" || e.key === "~")) {
            e.preventDefault();
            toggleBottomPanel();
            return;
        }

        // Toggle Explorer Sidebar: Ctrl + B
        if (e.ctrlKey && !e.shiftKey && (e.key === "b" || e.key === "B")) {
            e.preventDefault();
            toggleLeftSidebar();
            return;
        }

        // Focus Agent Input: Ctrl + L or Ctrl + I
        if (e.ctrlKey && !e.shiftKey && (e.key === "l" || e.key === "L" || e.key === "i" || e.key === "I")) {
            e.preventDefault();
            const panel = document.getElementById("rightAgentPanel");
            if (panel && panel.style.display === "none") {
                panel.style.display = "flex";
                triggerEditorLayout();
            }
            const agentInput = document.getElementById("agentTaskInput");
            if (agentInput) agentInput.focus();
            return;
        }

        // Quick Open: Ctrl + P
        if (e.ctrlKey && !e.shiftKey && (e.key === "p" || e.key === "P")) {
            e.preventDefault();
            openQuickOpenModal();
            return;
        }

        // Save All: Ctrl + Shift + S
        if (e.ctrlKey && e.shiftKey && (e.key === "s" || e.key === "S")) {
            e.preventDefault();
            saveAllDirtyFiles();
            return;
        }

        // Save current: Ctrl + S
        if (e.ctrlKey && !e.shiftKey && (e.key === "s" || e.key === "S")) {
            e.preventDefault();
            saveCurrentFile();
            return;
        }

        // Global Search: Ctrl + Shift + F
        if (e.ctrlKey && e.shiftKey && (e.key === "f" || e.key === "F")) {
            e.preventDefault();
            switchActivityTab('search');
            setTimeout(() => document.getElementById("ideSearchQuery")?.focus(), 100);
            return;
        }

        // Format Document: Shift + Alt + F
        if (e.shiftKey && e.altKey && (e.key === "f" || e.key === "F")) {
            e.preventDefault();
            formatActiveDocument();
            return;
        }

        // Toggle Word Wrap: Alt + Z
        if (e.altKey && (e.key === "z" || e.key === "Z")) {
            e.preventDefault();
            toggleWordWrap();
            return;
        }

        // Close Active Tab: Alt + W
        if (e.altKey && (e.key === "w" || e.key === "W")) {
            e.preventDefault();
            if (activeFilePath) closeTab(activeFilePath);
            return;
        }

        // Zoom Editor Font: Ctrl + = / Ctrl + -
        if (e.ctrlKey && (e.key === "=" || e.key === "+")) {
            e.preventDefault();
            zoomEditorFont(1);
            return;
        }
        if (e.ctrlKey && e.key === "-") {
            e.preventDefault();
            zoomEditorFont(-1);
            return;
        }

        // Escape: close context menu & dropdowns
        if (e.key === "Escape") {
            hideExplorerContextMenu();
            const dd = document.getElementById("agentModelDropdown");
            if (dd) dd.style.display = "none";
        }

        // Explorer item shortcuts (when not focused in inputs/editors)
        const isInputFocused = document.activeElement && (
            document.activeElement.tagName === "INPUT" ||
            document.activeElement.tagName === "TEXTAREA" ||
            document.activeElement.closest(".monaco-editor")
        );

        if (!isInputFocused && activeTreeNode) {
            // F2: Inline Rename
            if (e.key === "F2") {
                e.preventDefault();
                startInlineRename(activeTreeNode);
                return;
            }

            // Delete: Delete Permanently
            if (e.key === "Delete") {
                e.preventDefault();
                contextTargetItem = activeTreeNode;
                contextMenuDeletePermanently();
                return;
            }

            // Ctrl + C: Copy explorer item
            if (e.ctrlKey && (e.key === "c" || e.key === "C")) {
                e.preventDefault();
                contextTargetItem = activeTreeNode;
                contextMenuCopy();
                return;
            }

            // Ctrl + X: Cut explorer item
            if (e.ctrlKey && (e.key === "x" || e.key === "X")) {
                e.preventDefault();
                contextTargetItem = activeTreeNode;
                contextMenuCut();
                return;
            }

            // Ctrl + V: Paste into folder
            if (e.ctrlKey && (e.key === "v" || e.key === "V")) {
                e.preventDefault();
                contextTargetItem = activeTreeNode;
                contextMenuPaste();
                return;
            }
        }
    }, true);
}

// Editor helpers
function formatActiveDocument() {
    if (monacoEditor) {
        monacoEditor.getAction('editor.action.formatDocument')?.run();
    }
}

let isWordWrapOn = true;
function toggleWordWrap() {
    if (monacoEditor) {
        isWordWrapOn = !isWordWrapOn;
        monacoEditor.updateOptions({ wordWrap: isWordWrapOn ? "on" : "off" });
        showToastNotification(`Word Wrap: ${isWordWrapOn ? 'ON' : 'OFF'}`);
    }
}

let currentEditorFontSize = 13;
function zoomEditorFont(delta) {
    currentEditorFontSize = Math.max(9, Math.min(26, currentEditorFontSize + delta));
    if (monacoEditor) monacoEditor.updateOptions({ fontSize: currentEditorFontSize });
    if (monacoDiffEditor) monacoDiffEditor.updateOptions({ fontSize: currentEditorFontSize });
}

// ==========================================
// 14. COMMAND PALETTE (Ctrl+Shift+P / F1)
// ==========================================
const COMMAND_PALETTE_ITEMS = [
    { id: 'view_toggle_sidebar', title: 'View: Toggle Primary Side Bar', shortcut: 'Ctrl+B', icon: 'bi-layout-sidebar', action: () => toggleLeftSidebar() },
    { id: 'view_toggle_terminal', title: 'View: Toggle Terminal', shortcut: 'Ctrl+`', icon: 'bi-terminal', action: () => toggleBottomPanel() },
    { id: 'view_toggle_agent', title: 'View: Toggle Antigravity Agent', shortcut: 'Ctrl+L', icon: 'bi-robot', action: () => toggleRightAgentPanel() },
    { id: 'file_save', title: 'File: Save', shortcut: 'Ctrl+S', icon: 'bi-save', action: () => saveCurrentFile() },
    { id: 'file_save_all', title: 'File: Save All', shortcut: 'Ctrl+Shift+S', icon: 'bi-files', action: () => saveAllDirtyFiles() },
    { id: 'file_new', title: 'File: New File', shortcut: '', icon: 'bi-file-earmark-plus', action: () => openCreateFileModal() },
    { id: 'folder_new', title: 'File: New Folder', shortcut: '', icon: 'bi-folder-plus', action: () => openCreateFolderModal() },
    { id: 'file_quick_open', title: 'File: Quick Open...', shortcut: 'Ctrl+P', icon: 'bi-search', action: () => openQuickOpenModal() },
    { id: 'terminal_build', title: 'Terminal: Run Build (dotnet build)', shortcut: '', icon: 'bi-play', action: () => runTerminalBuild() },
    { id: 'terminal_test', title: 'Terminal: Run Test (dotnet test)', shortcut: '', icon: 'bi-check-circle', action: () => runTerminalTest() },
    { id: 'terminal_clear', title: 'Terminal: Clear Console', shortcut: '', icon: 'bi-trash', action: () => clearTerminalOutput() },
    { id: 'editor_format', title: 'Editor: Format Document', shortcut: 'Shift+Alt+F', icon: 'bi-code-square', action: () => formatActiveDocument() },
    { id: 'editor_wrap', title: 'View: Toggle Word Wrap', shortcut: 'Alt+Z', icon: 'bi-text-wrap', action: () => toggleWordWrap() },
    { id: 'help_shortcuts', title: 'Preferences: Open Keyboard Shortcuts', shortcut: '', icon: 'bi-keyboard', action: () => openShortcutsModal() }
];

let selectedPaletteIndex = 0;
let filteredPaletteItems = [];

function openCommandPalette() {
    const modalEl = document.getElementById("commandPaletteModal");
    if (!modalEl) return;
    const modal = bootstrap.Modal.getOrCreateInstance(modalEl);
    modal.show();

    const input = document.getElementById("commandPaletteInput");
    if (input) {
        input.value = "";
        setTimeout(() => input.focus(), 150);
    }
    filterCommandPalette("");
}

function filterCommandPalette(query) {
    query = (query || "").toLowerCase().trim();
    if (!query) {
        filteredPaletteItems = [...COMMAND_PALETTE_ITEMS];
    } else {
        filteredPaletteItems = COMMAND_PALETTE_ITEMS.filter(item =>
            item.title.toLowerCase().includes(query) ||
            (item.shortcut && item.shortcut.toLowerCase().includes(query))
        );
    }
    selectedPaletteIndex = 0;
    renderCommandPaletteList();
}

function renderCommandPaletteList() {
    const list = document.getElementById("commandPaletteList");
    if (!list) return;

    if (filteredPaletteItems.length === 0) {
        list.innerHTML = `<div class="p-3 text-secondary text-center extra-small">No commands found</div>`;
        return;
    }

    list.innerHTML = filteredPaletteItems.map((item, idx) => `
        <div class="palette-cmd-item ${idx === selectedPaletteIndex ? 'selected' : ''}" onclick="executePaletteItem(${idx})">
            <div class="d-flex align-items-center">
                <i class="bi ${item.icon} me-2 text-info"></i>
                <span class="palette-cmd-title">${escapeHtml(item.title)}</span>
            </div>
            ${item.shortcut ? `<span class="palette-cmd-shortcut">${escapeHtml(item.shortcut)}</span>` : ''}
        </div>
    `).join('');

    const activeEl = list.children[selectedPaletteIndex];
    if (activeEl) activeEl.scrollIntoView({ block: 'nearest' });
}

function onPaletteInput(e) {
    filterCommandPalette(e.target.value);
}

function onPaletteKeyDown(e) {
    if (e.key === "ArrowDown") {
        e.preventDefault();
        if (filteredPaletteItems.length > 0) {
            selectedPaletteIndex = (selectedPaletteIndex + 1) % filteredPaletteItems.length;
            renderCommandPaletteList();
        }
    } else if (e.key === "ArrowUp") {
        e.preventDefault();
        if (filteredPaletteItems.length > 0) {
            selectedPaletteIndex = (selectedPaletteIndex - 1 + filteredPaletteItems.length) % filteredPaletteItems.length;
            renderCommandPaletteList();
        }
    } else if (e.key === "Enter") {
        e.preventDefault();
        executePaletteItem(selectedPaletteIndex);
    } else if (e.key === "Escape") {
        hideModalSafely("commandPaletteModal");
    }
}

function executePaletteItem(index) {
    const item = filteredPaletteItems[index];
    if (item) {
        hideModalSafely("commandPaletteModal");
        try {
            item.action();
        } catch (err) {
            console.error(err);
        }
    }
}

// ==========================================
// 15. AUTO-SAVE & SAVE ALL
// ==========================================
let isAutoSaveEnabled = false;
let autoSaveTimer = null;

function toggleAutoSave() {
    isAutoSaveEnabled = !isAutoSaveEnabled;
    const checkEl = document.getElementById("autoSaveCheckIcon");
    if (checkEl) {
        checkEl.style.visibility = isAutoSaveEnabled ? "visible" : "hidden";
    }
    showToastNotification(isAutoSaveEnabled ? "Auto Save: Enabled (1.5s)" : "Auto Save: Disabled");
}

async function saveAllDirtyFiles() {
    let savedCount = 0;
    const dirtyTabs = [];
    openTabsMap.forEach(t => {
        if (t.isDirty) dirtyTabs.push(t);
    });

    if (dirtyTabs.length === 0) {
        showToastNotification("All open files are saved.");
        return;
    }

    for (const tab of dirtyTabs) {
        try {
            const res = await fetch('/api/ide/save', {
                method: 'POST',
                headers: getApiHeaders(),
                body: JSON.stringify({
                    project: activeProjectId,
                    file: tab.path,
                    content: tab.model.getValue()
                })
            });
            const data = await res.json();
            if (data.ok) {
                tab.isDirty = false;
                tab.originalContent = tab.model.getValue();
                savedCount++;
            }
        } catch (err) {
            console.error("Save error for " + tab.path, err);
        }
    }
    renderTabBar();
    updateGitBadgeCount();
    showToastNotification(`Saved ${savedCount} file(s).`);
    loadGitStatus();
}

function updateGitBadgeCount() {
    const badge = document.getElementById("gitBadgeCount");
    if (!badge) return;
    let dirtyCount = 0;
    openTabsMap.forEach(t => {
        if (t.isDirty) dirtyCount++;
    });
    if (dirtyCount > 0) {
        badge.innerText = dirtyCount.toString();
        badge.style.display = "inline-flex";
    } else {
        badge.innerText = "0";
        badge.style.display = "none";
    }
}

// ==========================================
// 16. OUTLINE SYMBOL SCANNER & JUMP
// ==========================================
let outlineRefreshTimer = null;
function scheduleOutlineRefresh() {
    if (outlineRefreshTimer) clearTimeout(outlineRefreshTimer);
    outlineRefreshTimer = setTimeout(refreshOutlineSymbols, 350);
}

function refreshOutlineSymbols() {
    const container = document.getElementById("accordionOutline");
    if (!container) return;

    if (!monacoEditor || !activeFilePath) {
        container.innerHTML = `<div class="p-2 text-secondary extra-small">No symbols found in current file</div>`;
        return;
    }

    const content = monacoEditor.getValue();
    const lines = content.split('\n');
    const symbols = [];
    const ext = activeFilePath.split('.').pop()?.toLowerCase();

    for (let i = 0; i < lines.length; i++) {
        const line = lines[i];
        const lineNum = i + 1;
        const trimmed = line.trim();

        if (ext === 'cs') {
            const classMatch = trimmed.match(/(?:public|private|protected|internal)?\s*(?:static\s+)?(?:class|interface|struct|enum|record)\s+([a-zA-Z0-9_]+)/);
            if (classMatch) {
                symbols.push({ name: classMatch[1], type: 'class', icon: 'bi-box text-warning', line: lineNum });
                continue;
            }
            const methodMatch = trimmed.match(/(?:public|private|protected|internal)\s+(?:static\s+)?(?:async\s+)?[\w<>\[\],\s]+\s+([a-zA-Z0-9_]+)\s*\(/);
            if (methodMatch && !['if', 'for', 'foreach', 'while', 'switch', 'catch'].includes(methodMatch[1])) {
                symbols.push({ name: methodMatch[1], type: 'method', icon: 'bi-gear text-info', line: lineNum });
                continue;
            }
        } else if (ext === 'js' || ext === 'ts') {
            const funcMatch = trimmed.match(/(?:function\s+([a-zA-Z0-9_]+)|(?:const|let|var)\s+([a-zA-Z0-9_]+)\s*=\s*(?:function|\([^)]*\)\s*=>|\w+\s*=>))/);
            if (funcMatch) {
                symbols.push({ name: funcMatch[1] || funcMatch[2], type: 'function', icon: 'bi-code-slash text-info', line: lineNum });
                continue;
            }
            const classMatch = trimmed.match(/class\s+([a-zA-Z0-9_]+)/);
            if (classMatch) {
                symbols.push({ name: classMatch[1], type: 'class', icon: 'bi-box text-warning', line: lineNum });
                continue;
            }
        } else if (ext === 'html' || ext === 'cshtml') {
            const tagMatch = trimmed.match(/<(h[1-6]|section|header|footer|nav|main|div\s+id="([^"]+)")/i);
            if (tagMatch) {
                symbols.push({ name: tagMatch[2] || tagMatch[1], type: 'tag', icon: 'bi-tag text-success', line: lineNum });
            }
        }
    }

    if (symbols.length === 0) {
        container.innerHTML = `<div class="p-2 text-secondary extra-small">No symbols found in ${escapeHtml(activeFilePath)}</div>`;
        return;
    }

    container.innerHTML = symbols.map(s => `
        <div class="outline-item" onclick="jumpToEditorLine(${s.line})" title="Line ${s.line}">
            <i class="bi ${s.icon} me-1.5"></i>
            <span class="outline-name">${escapeHtml(s.name)}</span>
            <span class="outline-line text-secondary ms-auto extra-small">${s.line}</span>
        </div>
    `).join('');
}

function jumpToEditorLine(line) {
    if (monacoEditor) {
        monacoEditor.revealLineInCenter(line);
        monacoEditor.setPosition({ lineNumber: line, column: 1 });
        monacoEditor.focus();
    }
}

// ==========================================
// 17. CONTEXT MENU & EXPLORER OPERATIONS
// ==========================================
let contextTargetItem = null; // { path, isDirectory, el }
let explorerClipboard = null; // { mode: 'cut'|'copy', path, isDirectory }

function onExplorerContextMenu(e, path, isDirectory, targetEl = null) {
    e.preventDefault();
    e.stopPropagation();
    contextTargetItem = { path, isDirectory, el: targetEl };

    const menu = document.getElementById("explorerContextMenu");
    if (!menu) return;

    // Toggle items based on Folder vs File
    const ctxFolder = document.getElementById("ctxFolderItems");
    const ctxFile = document.getElementById("ctxFileItems");
    if (ctxFolder) ctxFolder.style.display = isDirectory ? "block" : "none";
    if (ctxFile) ctxFile.style.display = !isDirectory ? "block" : "none";

    // Toggle Paste availability
    const pasteBtn = document.getElementById("ctxItemPaste");
    if (pasteBtn) {
        if (explorerClipboard && explorerClipboard.path) {
            pasteBtn.classList.remove("disabled");
        } else {
            pasteBtn.classList.add("disabled");
        }
    }

    menu.style.display = "block";
    const x = Math.min(e.clientX, window.innerWidth - 240);
    const y = Math.min(e.clientY, window.innerHeight - 340);
    menu.style.left = `${Math.max(10, x)}px`;
    menu.style.top = `${Math.max(10, y)}px`;
}

function hideExplorerContextMenu() {
    const menu = document.getElementById("explorerContextMenu");
    if (menu) menu.style.display = "none";
}

document.addEventListener("click", (e) => {
    hideExplorerContextMenu();
    const dd = document.getElementById("agentModelDropdown");
    if (dd && !e.target.closest("#agentModelPill") && !e.target.closest("#agentModelDropdown")) {
        dd.style.display = "none";
    }
});

// Model Dropdown Functions
function toggleModelDropdown(e) {
    if (e) e.stopPropagation();
    const dd = document.getElementById("agentModelDropdown");
    if (!dd) return;
    const isHidden = dd.style.display === "none" || !dd.style.display;
    dd.style.display = isHidden ? "flex" : "none";
}

function selectAgentModel(modelId, label) {
    const selectEl = document.getElementById("agentModelSelect");
    if (selectEl) selectEl.value = modelId;
    const labelEl = document.getElementById("currentModelLabel");
    if (labelEl) labelEl.textContent = label;
    document.querySelectorAll(".agent-model-dropdown-item").forEach(item => {
        if (item.dataset.model === modelId) item.classList.add("active");
        else item.classList.remove("active");
    });
    const dd = document.getElementById("agentModelDropdown");
    if (dd) dd.style.display = "none";
}

// Inline New File / Folder
function contextMenuInlineNewFile() {
    hideExplorerContextMenu();
    const parentFolder = contextTargetItem?.isDirectory ? contextTargetItem.path : "";
    const fileName = prompt(parentFolder ? `Tạo file mới trong '${parentFolder}':` : "Tạo file mới:");
    if (!fileName) return;
    const fullPath = parentFolder ? `${parentFolder}/${fileName.trim()}` : fileName.trim();
    createFileDirectly(fullPath);
}

function contextMenuInlineNewFolder() {
    hideExplorerContextMenu();
    const parentFolder = contextTargetItem?.isDirectory ? contextTargetItem.path : "";
    const folderName = prompt(parentFolder ? `Tạo thư mục mới trong '${parentFolder}':` : "Tạo thư mục mới:");
    if (!folderName) return;
    const fullPath = parentFolder ? `${parentFolder}/${folderName.trim()}` : folderName.trim();
    createFolderDirectly(fullPath);
}

async function createFileDirectly(filePath) {
    try {
        const res = await fetch('/api/ide/create-file', {
            method: 'POST',
            headers: getApiHeaders(),
            body: JSON.stringify({
                project: activeProjectId,
                file: filePath,
                content: ""
            })
        });
        const data = await res.json();
        if (data.ok) {
            await refreshFileTree();
            await openFileInEditor(filePath);
            showToastNotification("Created file: " + filePath);
        } else {
            showRestrictedLoginPrompt(data.message || data.error);
        }
    } catch (err) {
        alert("Lỗi tạo file: " + err.message);
    }
}

async function createFolderDirectly(folderPath) {
    try {
        const res = await fetch('/api/ide/folder/create', {
            method: 'POST',
            headers: getApiHeaders(),
            body: JSON.stringify({
                project: activeProjectId,
                folder: folderPath
            })
        });
        const data = await res.json();
        if (data.ok) {
            await refreshFileTree();
            showToastNotification("Created folder: " + folderPath);
        } else {
            showRestrictedLoginPrompt(data.message || data.error);
        }
    } catch (err) {
        alert("Lỗi tạo thư mục: " + err.message);
    }
}

function contextMenuOpenFile() {
    hideExplorerContextMenu();
    if (contextTargetItem && !contextTargetItem.isDirectory && contextTargetItem.path) {
        openFileInEditor(contextTargetItem.path);
    }
}

function contextMenuDownloadFile() {
    hideExplorerContextMenu();
    if (!contextTargetItem || contextTargetItem.isDirectory || !contextTargetItem.path) return;
    window.location.href = `/api/ide/file/download?project=${encodeURIComponent(activeProjectId)}&path=${encodeURIComponent(contextTargetItem.path)}`;
}

function contextMenuOpenTerminal() {
    hideExplorerContextMenu();
    switchBottomTab('terminal');
    const folder = contextTargetItem ? (contextTargetItem.isDirectory ? contextTargetItem.path : "") : "";
    const cdCmd = folder ? `cd "${folder.replace(/\//g, '\\')}"\r` : "clear\r";
    if (terminalHubConn && terminalHubConn.state === signalR.HubConnectionState.Connected) {
        terminalHubConn.invoke("SendTerminalInput", cdCmd);
    }
}

function contextMenuFindInFolder() {
    hideExplorerContextMenu();
    switchActivityTab('search');
    const folder = contextTargetItem ? (contextTargetItem.isDirectory ? contextTargetItem.path : "") : "";
    const searchInput = document.getElementById("ideSearchQuery");
    if (searchInput) {
        if (folder) searchInput.placeholder = `Search in ${folder}...`;
        searchInput.focus();
    }
}

// Clipboard Cut / Copy / Paste
function contextMenuCut() {
    hideExplorerContextMenu();
    if (!contextTargetItem || !contextTargetItem.path) return;
    document.querySelectorAll(".tree-node.cut-item").forEach(el => el.classList.remove("cut-item"));
    explorerClipboard = { mode: 'cut', path: contextTargetItem.path, isDirectory: contextTargetItem.isDirectory };
    if (contextTargetItem.el) contextTargetItem.el.classList.add("cut-item");
    showToastNotification(`Cut: ${contextTargetItem.path}`);
}

function contextMenuCopy() {
    hideExplorerContextMenu();
    if (!contextTargetItem || !contextTargetItem.path) return;
    document.querySelectorAll(".tree-node.cut-item").forEach(el => el.classList.remove("cut-item"));
    explorerClipboard = { mode: 'copy', path: contextTargetItem.path, isDirectory: contextTargetItem.isDirectory };
    showToastNotification(`Copied: ${contextTargetItem.path}`);
}

async function contextMenuPaste() {
    hideExplorerContextMenu();
    if (!explorerClipboard || !explorerClipboard.path) return;

    let destFolder = "";
    if (contextTargetItem) {
        if (contextTargetItem.isDirectory) {
            destFolder = contextTargetItem.path;
        } else {
            const parts = contextTargetItem.path.split(/[\\/]/);
            parts.pop();
            destFolder = parts.join('/');
        }
    }

    const srcPath = explorerClipboard.path;
    const itemName = srcPath.split(/[\\/]/).pop();
    const destPath = destFolder ? `${destFolder}/${itemName}` : itemName;

    if (srcPath === destPath && explorerClipboard.mode === 'cut') {
        return;
    }

    try {
        if (explorerClipboard.mode === 'copy') {
            const res = await fetch('/api/ide/item/copy', {
                method: 'POST',
                headers: getApiHeaders(),
                body: JSON.stringify({
                    project: activeProjectId,
                    sourcePath: srcPath,
                    targetFolder: destFolder
                })
            });
            const data = await res.json();
            if (data.ok) {
                await refreshFileTree();
                showToastNotification("Pasted copy successfully.");
            } else {
                showRestrictedLoginPrompt(data.message || data.error);
            }
        } else if (explorerClipboard.mode === 'cut') {
            await handleMoveItem(srcPath, destFolder);
            explorerClipboard = null;
            document.querySelectorAll(".tree-node.cut-item").forEach(el => el.classList.remove("cut-item"));
        }
    } catch (err) {
        alert("Lỗi dán: " + err.message);
    }
}

async function handleMoveItem(srcPath, destFolder) {
    const itemName = srcPath.split(/[\\/]/).pop();
    const destPath = destFolder ? `${destFolder}/${itemName}` : itemName;
    if (srcPath === destPath) return;

    try {
        const res = await fetch('/api/ide/item/rename', {
            method: 'POST',
            headers: getApiHeaders(),
            body: JSON.stringify({
                project: activeProjectId,
                oldPath: srcPath,
                newPath: destPath
            })
        });
        const data = await res.json();
        if (data.ok) {
            if (openTabsMap.has(srcPath)) {
                closeTab(srcPath);
                await openFileInEditor(destPath);
            }
            await refreshFileTree();
            showToastNotification(`Moved ${itemName} to ${destFolder || 'root'}`);
        } else {
            showRestrictedLoginPrompt(data.message || data.error);
        }
    } catch (err) {
        alert("Lỗi di chuyển: " + err.message);
    }
}

function contextMenuCopyFullPath() {
    hideExplorerContextMenu();
    if (!contextTargetItem || !contextTargetItem.path) return;
    const full = `${activeProjectId}/${contextTargetItem.path}`;
    navigator.clipboard.writeText(full);
    showToastNotification("Copied path: " + full);
}

function contextMenuCopyRelativePath() {
    hideExplorerContextMenu();
    if (!contextTargetItem || !contextTargetItem.path) return;
    navigator.clipboard.writeText(contextTargetItem.path);
    showToastNotification("Copied relative path: " + contextTargetItem.path);
}

function contextMenuAddToAgent() {
    hideExplorerContextMenu();
    if (!contextTargetItem || !contextTargetItem.path) return;
    addMentionContextFile(contextTargetItem.path);
}

// Inline Rename (F2)
function contextMenuInlineRename() {
    hideExplorerContextMenu();
    if (!contextTargetItem || !contextTargetItem.path) return;
    startInlineRename(contextTargetItem);
}

function startInlineRename(targetItem) {
    if (!targetItem || !targetItem.path) return;
    const nodeEl = targetItem.el || document.querySelector(`.tree-node[data-path="${targetItem.path}"]`);
    if (!nodeEl) {
        contextMenuRename();
        return;
    }

    const labelSpan = nodeEl.querySelector(".tree-item-name");
    if (!labelSpan) return;

    const oldName = labelSpan.textContent;
    const oldPath = targetItem.path;

    const input = document.createElement("input");
    input.type = "text";
    input.className = "inline-tree-input";
    input.value = oldName;

    labelSpan.replaceWith(input);
    input.focus();

    // Select name before extension
    const dotIdx = oldName.lastIndexOf('.');
    if (!targetItem.isDirectory && dotIdx > 0) {
        input.setSelectionRange(0, dotIdx);
    } else {
        input.select();
    }

    let finished = false;
    const commitRename = async () => {
        if (finished) return;
        finished = true;
        const newName = input.value.trim();
        if (!newName || newName === oldName) {
            input.replaceWith(labelSpan);
            return;
        }

        const parts = oldPath.split(/[\\/]/);
        parts.pop();
        const newPath = parts.length > 0 ? `${parts.join('/')}/${newName}` : newName;

        try {
            const res = await fetch('/api/ide/item/rename', {
                method: 'POST',
                headers: getApiHeaders(),
                body: JSON.stringify({
                    project: activeProjectId,
                    oldPath: oldPath,
                    newPath: newPath
                })
            });
            const data = await res.json();
            if (data.ok) {
                if (openTabsMap.has(oldPath)) {
                    closeTab(oldPath);
                    await openFileInEditor(newPath);
                }
                await refreshFileTree();
                showToastNotification("Renamed successfully.");
            } else {
                input.replaceWith(labelSpan);
                showRestrictedLoginPrompt(data.message || data.error);
            }
        } catch (err) {
            input.replaceWith(labelSpan);
            alert("Lỗi đổi tên: " + err.message);
        }
    };

    input.onkeydown = (e) => {
        if (e.key === "Enter") {
            e.preventDefault();
            commitRename();
        } else if (e.key === "Escape") {
            e.preventDefault();
            finished = true;
            input.replaceWith(labelSpan);
        }
    };

    input.onblur = () => {
        setTimeout(commitRename, 100);
    };
}

// Delete Permanently
function contextMenuDeletePermanently() {
    hideExplorerContextMenu();
    if (!contextTargetItem || !contextTargetItem.path) return;
    const modalEl = document.getElementById("deleteConfirmModal");
    if (!modalEl) {
        contextMenuDelete();
        return;
    }
    const nameEl = document.getElementById("deleteTargetName");
    if (nameEl) nameEl.textContent = contextTargetItem.path;
    const modal = bootstrap.Modal.getOrCreateInstance(modalEl);
    modal.show();
}

async function confirmDeleteTargetItem() {
    hideModalSafely("deleteConfirmModal");
    if (!contextTargetItem || !contextTargetItem.path) return;

    try {
        const res = await fetch('/api/ide/item/delete', {
            method: 'POST',
            headers: getApiHeaders(),
            body: JSON.stringify({
                project: activeProjectId,
                file: contextTargetItem.path
            })
        });
        const data = await res.json();
        if (data.ok) {
            if (openTabsMap.has(contextTargetItem.path)) {
                closeTab(contextTargetItem.path);
            }
            await refreshFileTree();
            showToastNotification("Deleted permanently: " + contextTargetItem.path);
        } else {
            showRestrictedLoginPrompt(data.message || data.error);
        }
    } catch (err) {
        alert("Lỗi xóa: " + err.message);
    }
}

// ==========================================
// 18. TOAST NOTIFICATION (VS CODE STYLE)
// ==========================================
function showToastNotification(msg) {
    let container = document.getElementById("ideToastContainer");
    if (!container) {
        container = document.createElement("div");
        container.id = "ideToastContainer";
        container.style.position = "fixed";
        container.style.bottom = "32px";
        container.style.right = "16px";
        container.style.zIndex = "99999";
        container.style.display = "flex";
        container.style.flexDirection = "column";
        container.style.gap = "8px";
        container.style.pointerEvents = "none";
        document.body.appendChild(container);
    }

    const toast = document.createElement("div");
    toast.style.background = "#252526";
    toast.style.color = "#cccccc";
    toast.style.border = "1px solid #3c3c3c";
    toast.style.borderRadius = "4px";
    toast.style.padding = "8px 14px";
    toast.style.fontSize = "12px";
    toast.style.boxShadow = "0 4px 12px rgba(0,0,0,0.5)";
    toast.style.display = "flex";
    toast.style.alignItems = "center";
    toast.style.gap = "8px";
    toast.style.transition = "all 0.25s ease";
    toast.style.opacity = "0";
    toast.style.transform = "translateY(10px)";
    toast.innerHTML = `<i class="bi bi-info-circle text-info"></i> <span>${escapeHtml(msg)}</span>`;

    container.appendChild(toast);
    setTimeout(() => {
        toast.style.opacity = "1";
        toast.style.transform = "translateY(0)";
    }, 20);

    setTimeout(() => {
        toast.style.opacity = "0";
        toast.style.transform = "translateY(10px)";
        setTimeout(() => toast.remove(), 250);
    }, 2500);
}

// Initial Boot
window.addEventListener('DOMContentLoaded', () => {
    initMonacoEditors();
    initDraggableSplitters();
    initGlobalShortcuts();
    initXtermTerminal();
});
