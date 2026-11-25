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
    const uid = localStorage.getItem("user_id") || "1";
    if (uid && uid !== "0") {
        headers['X-User-Id'] = uid;
    }
    const uname = localStorage.getItem("username") || localStorage.getItem("user_name");
    if (uname) {
        headers['X-Username'] = encodeURIComponent(uname);
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
const collapsedRootProjects = new Set();

function toggleRootProjectCollapse(projId, forceState) {
    const pId = projId || activeProjectId;
    if (!pId) return;

    const isCurrentlyCollapsed = collapsedRootProjects.has(pId);
    const shouldCollapse = forceState !== undefined ? forceState : !isCurrentlyCollapsed;

    if (shouldCollapse) {
        collapsedRootProjects.add(pId);
    } else {
        collapsedRootProjects.delete(pId);
    }

    applyRootProjectCollapseState(pId);
}

function applyRootProjectCollapseState(projId) {
    const pId = projId || activeProjectId;
    const isCollapsed = collapsedRootProjects.has(pId);
    const container = document.getElementById("workspaceTreeContainer");
    const rootChevron = document.getElementById("workspaceRootChevron") || document.querySelector("#workspaceRootHeader .bi-chevron-down, #workspaceRootHeader .bi-chevron-right");
    const folderIcon = document.getElementById("workspaceRootFolderIcon") || document.querySelector("#workspaceRootHeader .bi-folder, #workspaceRootHeader .bi-folder2-open");

    if (container) {
        container.style.display = isCollapsed ? "none" : "block";
    }

    if (rootChevron) {
        rootChevron.className = isCollapsed 
            ? "bi bi-chevron-right me-1.5 text-secondary" 
            : "bi bi-chevron-down me-1.5 text-secondary";
    }

    if (folderIcon) {
        folderIcon.className = isCollapsed 
            ? "bi bi-folder text-warning me-1.5" 
            : "bi bi-folder2-open text-warning me-1.5";
    }
}
window.toggleRootProjectCollapse = toggleRootProjectCollapse;
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
            const savedLastProj = localStorage.getItem("antigravity_last_project");
            const matchedProj = savedLastProj && data.projects.some(p => p.id === savedLastProj);
            activeProjectId = matchedProj ? savedLastProj : data.projects[0].id;
            select.value = activeProjectId;
            localStorage.setItem("antigravity_last_project", activeProjectId);
            hideEmptyWorkspaceState();
            loadAgentChatHistory(activeProjectId);
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

        const rootHeader = document.getElementById("workspaceRootHeader");
        if (rootHeader) {
            rootHeader.onclick = (e) => {
                if (e && e.target && e.target.closest('.workspace-root-actions')) return;
                activeTreeNode = { path: "", isDirectory: true, isProjectRoot: true, el: rootHeader };
                document.querySelectorAll(".explorer-tree-item, .workspace-root-title").forEach(el => el.classList.remove("selected"));
                rootHeader.classList.add("selected");
                toggleRootProjectCollapse(activeProjectId);
            };
        }
        applyRootProjectCollapseState(activeProjectId);

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
            terminalHubConn.invoke("InitializeTerminal", activeProjectId, "term-1").catch(console.error);
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
    localStorage.setItem("antigravity_last_project", activeProjectId);

    if (activeProjectId) {
        hideEmptyWorkspaceState();
        const rootNameEl = document.getElementById("workspaceRootName");
        if (rootNameEl) {
            rootNameEl.innerText = select.options[select.selectedIndex]?.text || activeProjectId;
        }
        updateTerminalPrompt();
        refreshFileTree();
        loadGitStatus();
        loadAgentChatHistory(activeProjectId);
        if (terminalHubConn && terminalHubConn.state === signalR.HubConnectionState.Connected) {
            terminalHubConn.invoke("InitializeTerminal", activeProjectId, "term-1").catch(console.error);
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
    localStorage.setItem("antigravity_last_project", activeProjectId);
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
    applyRootProjectCollapseState(projName);
    updateTerminalPrompt();
    await refreshFileTree();
    loadGitStatus();
    loadAgentChatHistory(activeProjectId);
    if (terminalHubConn && terminalHubConn.state === signalR.HubConnectionState.Connected) {
        await terminalHubConn.invoke("InitializeTerminal", activeProjectId, "term-1").catch(console.error);
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

            // Auto-restore open tabs from localStorage or auto-open primary file
            const restored = await restoreOpenTabsState();
            if (!restored && !activeFilePath) {
                const primaryFile = data.files.find(f => !f.isDir && (f.name === "index.html" || f.path.endsWith("index.html") || f.name === "Program.cs"));
                if (primaryFile) {
                    await openFileInEditor(primaryFile.path, true);
                } else if (data.files.length > 0 && !data.files[0].isDir) {
                    await openFileInEditor(data.files[0].path, true);
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
    applyRootProjectCollapseState(activeProjectId);
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
    "db": "file_type_sqlite.svg",
    "sqlite": "file_type_sqlite.svg",
    "sqlite3": "file_type_sqlite.svg",
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

function saveOpenTabsState() {
    if (!activeProjectId) return;
    const tabPaths = Array.from(openTabsMap.keys());
    localStorage.setItem(`antigravity_open_tabs_${activeProjectId}`, JSON.stringify(tabPaths));
    if (activeFilePath) {
        localStorage.setItem(`antigravity_active_tab_${activeProjectId}`, activeFilePath);
    } else {
        localStorage.removeItem(`antigravity_active_tab_${activeProjectId}`);
    }
}

async function restoreOpenTabsState() {
    if (!activeProjectId || !monacoEditor) return false;
    try {
        const raw = localStorage.getItem(`antigravity_open_tabs_${activeProjectId}`);
        const savedActive = localStorage.getItem(`antigravity_active_tab_${activeProjectId}`);
        if (raw) {
            const tabPaths = JSON.parse(raw);
            if (Array.isArray(tabPaths) && tabPaths.length > 0) {
                for (const p of tabPaths) {
                    await openFileInEditor(p, true);
                }
                const targetActive = (savedActive && openTabsMap.has(savedActive)) ? savedActive : tabPaths[0];
                if (targetActive && openTabsMap.has(targetActive)) {
                    await openFileInEditor(targetActive, false);
                }
                return true;
            }
        }
    } catch (e) {
        console.warn("Failed to restore open tabs state:", e);
    }
    return false;
}

function showImagePreviewModal(filePath) {
    const uid = localStorage.getItem("user_id") || "1";
    const cleanPath = filePath.replace(/\\/g, '/').replace(/^\//, '');
    const src = `/api/ide/raw?project=${encodeURIComponent(activeProjectId)}&file=${encodeURIComponent(cleanPath)}&user_id=${encodeURIComponent(uid)}&t=${Date.now()}`;
    const modalId = "ideImagePreviewModal";
    let modal = document.getElementById(modalId);
    if (!modal) {
        modal = document.createElement("div");
        modal.id = modalId;
        modal.style.cssText = "position:fixed;inset:0;background:rgba(0,0,0,0.75);z-index:99999;display:flex;align-items:center;justify-content:center;backdrop-filter:blur(4px);";
        modal.onclick = (e) => { if (e.target === modal) modal.style.display = 'none'; };
        document.body.appendChild(modal);
    }
    const fileName = filePath.split(/[\\/]/).pop();
    modal.innerHTML = `
        <div style="background:#1e1e2e;border:1px solid #313244;border-radius:12px;padding:16px;max-width:90vw;max-height:90vh;display:flex;flex-direction:column;box-shadow:0 12px 32px rgba(0,0,0,0.5);">
            <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:12px;border-bottom:1px solid #313244;padding-bottom:8px;">
                <span style="color:#cdd6f4;font-weight:600;font-size:14px;"><i class="bi bi-file-earmark-image me-1 text-info"></i> ${escapeHtml(fileName)}</span>
                <button type="button" class="btn btn-sm btn-outline-secondary" onclick="document.getElementById('${modalId}').style.display='none'"><i class="bi bi-x-lg"></i></button>
            </div>
            <div style="flex:1;overflow:auto;display:flex;align-items:center;justify-content:center;background:#11111b;border-radius:8px;padding:16px;background-image:linear-gradient(45deg, #181825 25%, transparent 25%), linear-gradient(-45deg, #181825 25%, transparent 25%), linear-gradient(45deg, transparent 75%, #181825 75%), linear-gradient(-45deg, transparent 75%, #181825 75%);background-size:20px 20px;background-position:0 0, 0 10px, 10px -10px, -10px 0px;">
                <img src="${src}" alt="${escapeHtml(fileName)}" style="max-width:100%;max-height:70vh;object-fit:contain;border-radius:6px;box-shadow:0 4px 16px rgba(0,0,0,0.4);" />
            </div>
            <div style="margin-top:10px;display:flex;justify-content:flex-end;gap:8px;">
                <a href="${src}" target="_blank" class="btn btn-sm btn-outline-primary" style="font-size:12px;"><i class="bi bi-box-arrow-up-right me-1"></i> Mở tab mới</a>
                <a href="${src}" download="${escapeHtml(fileName)}" class="btn btn-sm btn-primary" style="font-size:12px;"><i class="bi bi-download me-1"></i> Tải về</a>
            </div>
        </div>
    `;
    modal.style.display = "flex";
}

// ==========================================
// 3. MULTI-TABS & FILE OPEN/SAVE
// ==========================================
async function openFileInEditor(filePath, forceReload = false) {
    if (!monacoEditor) return;

    const lower = (filePath || "").toLowerCase();
    if (lower.endsWith('.db') || lower.endsWith('.sqlite') || lower.endsWith('.sqlite3')) {
        await openSqliteStudio(filePath);
        return;
    }
    if (lower.endsWith('.jpg') || lower.endsWith('.jpeg') || lower.endsWith('.png') || lower.endsWith('.gif') || lower.endsWith('.webp') || lower.endsWith('.svg') || lower.endsWith('.ico')) {
        showImagePreviewModal(filePath);
        return;
    }

    // Check if model already exists in memory and no forceReload requested
    if (!forceReload && openTabsMap.has(filePath)) {
        const tab = openTabsMap.get(filePath);
        // If existing model has no text but file might have content on disk, force reload
        if (tab && tab.model && tab.model.getValue().length === 0 && !tab.isDirty) {
            // fall through to fetch content
        } else {
            activeFilePath = filePath;
            monacoEditor.setModel(tab.model);
            updateEditorUI(tab);
            renderTabBar();
            saveOpenTabsState();
            try { monacoEditor.layout(); } catch {}
            return;
        }
    }

    try {
        const uid = localStorage.getItem("user_id") || "1";
        const res = await fetch(`/api/ide/file-content?project=${encodeURIComponent(activeProjectId)}&file=${encodeURIComponent(filePath)}&user_id=${encodeURIComponent(uid)}&_t=${Date.now()}`, {
            headers: getApiHeaders()
        });
        const data = await res.json();

        if (data.ok) {
            const fileName = filePath.split(/[\\/]/).pop();
            const lang = getMonacoLanguage(fileName);
            const modelUri = monaco.Uri.parse(`inmemory://workspace/${activeProjectId}/${filePath}`);
            let model = monaco.editor.getModel(modelUri);
            const content = data.content !== undefined && data.content !== null ? data.content : "";
            if (!model) {
                model = monaco.editor.createModel(content, lang, modelUri);
            } else {
                model.setValue(content);
            }

            const tab = {
                path: filePath,
                name: fileName,
                language: lang,
                model: model,
                isDirty: false,
                originalContent: content
            };

            openTabsMap.set(filePath, tab);
            activeFilePath = filePath;
            monacoEditor.setModel(model);
            updateEditorUI(tab);
            renderTabBar();
            saveOpenTabsState();
            try { monacoEditor.layout(); } catch {}

        } else {
            alert("❌ " + (data.message || "Failed to load file content"));
        }
    } catch (err) {
        alert("❌ Error opening file: " + err.message);
    }
}

// Tự động tải lại nội dung mới nhất từ đĩa cho toàn bộ các tab đang mở
async function reloadOpenFileTabs(modifiedFiles = null) {
    if (!monacoEditor || openTabsMap.size === 0) return;

    const normModified = Array.isArray(modifiedFiles)
        ? modifiedFiles.map(f => f.replace(/\\/g, '/').toLowerCase())
        : null;

    for (const [filePath, tab] of openTabsMap.entries()) {
        const normPath = filePath.replace(/\\/g, '/').toLowerCase();
        const shouldReload = !normModified || normModified.some(mf => normPath.endsWith(mf) || mf.endsWith(normPath));

        if (shouldReload) {
            try {
                const res = await fetch(`/api/ide/file-content?project=${encodeURIComponent(activeProjectId)}&file=${encodeURIComponent(filePath)}`, {
                    headers: getApiHeaders()
                });
                const data = await res.json();
                if (data.ok && typeof data.content === 'string') {
                    if (tab.model) {
                        tab.model.setValue(data.content);
                    }
                    tab.originalContent = data.content;
                    tab.isDirty = false;
                }
            } catch (err) {
                console.warn(`[ReloadTab] Failed to reload ${filePath}:`, err);
            }
        }
    }

    if (activeFilePath && openTabsMap.has(activeFilePath)) {
        const currentTab = openTabsMap.get(activeFilePath);
        monacoEditor.setModel(currentTab.model);
        updateEditorUI(currentTab);
    }
    renderTabBar();
}

function updateEditorUI(tab) {
    renderTabBar();

    if (window.innerWidth <= 768 && typeof window.switchMobileIdeTab === "function") {
        window.switchMobileIdeTab("editor");
    }

    const mContainer = document.getElementById("monacoContainer");
    const diffContainer = document.getElementById("monacoDiffContainer");
    const sqliteContainer = document.getElementById("sqliteStudioContainer");
    const liveAppContainer = document.getElementById("liveAppPreviewContainer");

    if (tab && tab.isSqlite) {
        if (mContainer) mContainer.style.display = "none";
        if (diffContainer) diffContainer.style.display = "none";
        if (liveAppContainer) liveAppContainer.style.display = "none";
        if (sqliteContainer) sqliteContainer.style.display = "flex";
    } else {
        if (sqliteContainer) sqliteContainer.style.display = "none";
        if (liveAppContainer) liveAppContainer.style.display = "none";
        if (diffContainer && diffContainer.style.display !== "none") {
            // Keep diff if active
        } else if (mContainer) {
            mContainer.style.display = "block";
            monacoEditor?.layout();
        }
    }

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
            const sqliteContainer = document.getElementById("sqliteStudioContainer");
            if (sqliteContainer) sqliteContainer.style.display = "none";
            const liveAppContainer = document.getElementById("liveAppPreviewContainer");
            if (liveAppContainer) liveAppContainer.style.display = "none";
            const mContainer = document.getElementById("monacoContainer");
            if (mContainer) mContainer.style.display = "block";

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
    saveOpenTabsState();
    updateGitBadgeCount();
    scheduleOutlineRefresh();
}

async function saveCurrentFile(silent = false) {
    if (!activeFilePath || !openTabsMap.has(activeFilePath)) return;
    const tab = openTabsMap.get(activeFilePath);
    if (tab.isSqlite || !tab.model) return;
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
// ==========================================
// 6. MULTI-TERMINAL & SIGNALR INTERACTIVE SHELL (VS Code Standard)
// ==========================================
let terminalSessions = [];
let activeTerminalSessionId = "term-1";
let isTerminalSplit = false;
let terminalSessionCounter = 1;

let xtermInstance = null; // References current active session's xterm
let fitAddon = null;      // References current active session's fitAddon
let terminalHubConn = null;
let terminalInputBuffer = "";
let terminalCursorPos = 0;
let terminalHistory = [];
let terminalHistoryIndex = -1;
let terminalPrompt = "PS D:\\UserWorkspaces> ";
let isTerminalProcessRunning = false;

function initXtermTerminal() {
    const panesWrapper = document.getElementById("terminalPanesWrapper");
    if (!panesWrapper) return;

    if (typeof Terminal === "undefined") {
        setTimeout(initXtermTerminal, 200);
        return;
    }

    if (terminalSessions.length > 0) return;

    // Create initial term-1 session
    const term1Pane = document.getElementById("xtermTerminalContainer");
    createTerminalSessionInstance("term-1", "powershell 1", term1Pane);

    // Connect SignalR
    initTerminalSignalR();
}

function getTerminalTheme() {
    return {
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
    };
}

function createTerminalSessionInstance(sessionId, name, containerEl) {
    if (!containerEl) {
        const wrapper = document.getElementById("terminalPanesWrapper");
        if (!wrapper) return null;
        containerEl = document.createElement("div");
        containerEl.id = `terminalPane_${sessionId}`;
        containerEl.className = "terminal-pane";
        containerEl.style.width = "100%";
        containerEl.style.height = "100%";
        containerEl.style.minHeight = "0";
        containerEl.style.minWidth = "0";
        containerEl.style.overflow = "hidden";
        containerEl.style.display = "none";
        wrapper.appendChild(containerEl);
    }

    const term = new Terminal({
        theme: getTerminalTheme(),
        fontFamily: "'Cascadia Code', 'Fira Code', Consolas, monospace",
        fontSize: 13,
        lineHeight: 1.25,
        cursorBlink: true,
        cursorStyle: 'block',
        convertEol: true,
        scrollback: 5000,
        allowTransparency: false
    });

    let fit = null;
    if (typeof FitAddon !== "undefined" && FitAddon.FitAddon) {
        fit = new FitAddon.FitAddon();
        term.loadAddon(fit);
    }

    term.open(containerEl);
    if (fit) {
        setTimeout(() => { try { fit.fit(); } catch (e) { } }, 100);
    }

    const sessionObj = {
        id: sessionId,
        name: name,
        xterm: term,
        fitAddon: fit,
        containerEl: containerEl,
        inputBuffer: "",
        cursorPos: 0,
        history: [],
        historyIndex: -1,
        prompt: terminalPrompt
    };

    term.attachCustomKeyEventHandler((e) => {
        if (e.type !== "keydown") return true;

        if ((e.ctrlKey && !e.shiftKey && (e.key === 'c' || e.key === 'C')) ||
            (e.ctrlKey && e.shiftKey && (e.key === 'c' || e.key === 'C'))) {
            if (term.hasSelection()) {
                const text = term.getSelection();
                navigator.clipboard.writeText(text);
                term.clearSelection();
                showToastNotification("Copied to clipboard");
                return false;
            }
            if (e.shiftKey) return false;
            return true;
        }

        if (e.ctrlKey && (e.key === 'v' || e.key === 'V')) {
            e.preventDefault();
            pasteIntoActiveTerminal();
            return false;
        }

        if (e.ctrlKey && e.shiftKey && (e.key === 'a' || e.key === 'A')) {
            e.preventDefault();
            term.selectAll();
            return false;
        }

        return true;
    });

    containerEl.addEventListener("contextmenu", (e) => onTerminalContextMenu(e));
    containerEl.addEventListener("paste", (e) => {
        e.preventDefault();
        const text = (e.clipboardData || window.clipboardData)?.getData('text');
        if (text) insertTextIntoActiveTerminal(text);
    });

    term.onData((data) => handleSessionKeyInput(sessionObj, data));

    terminalSessions.push(sessionObj);
    renderTerminalSessionsSidebar();

    return sessionObj;
}

function renderTerminalSessionsSidebar() {
    const listEl = document.getElementById("terminalSessionsList");
    if (listEl) {
        listEl.innerHTML = terminalSessions.map(s => `
            <div class="terminal-session-item ${s.id === activeTerminalSessionId ? 'active' : ''}" onclick="switchTerminalSession('${s.id}')">
                <div class="d-flex align-items-center gap-1.5 text-truncate">
                    <i class="bi bi-terminal ${s.id === activeTerminalSessionId ? 'text-info' : 'text-secondary'}" style="font-size: 11px;"></i>
                    <span class="text-truncate">${s.name}</span>
                </div>
                ${terminalSessions.length > 1 ? `
                <button onclick="event.stopPropagation(); closeTerminalSession('${s.id}')" class="terminal-session-close-btn" title="Close Terminal">
                    <i class="bi bi-x"></i>
                </button>` : ''}
            </div>
        `).join('');
    }

    const headerTabsEl = document.getElementById("terminalHeaderTabsContainer");
    if (headerTabsEl) {
        headerTabsEl.innerHTML = terminalSessions.map(s => `
            <div class="terminal-header-tab ${s.id === activeTerminalSessionId ? 'active' : ''}" onclick="switchTerminalSession('${s.id}')" style="cursor: pointer; padding: 2px 8px; border-radius: 4px; font-size: 11px; display: inline-flex; align-items: center; gap: 4px; border: 1px solid ${s.id === activeTerminalSessionId ? '#388bfd' : 'transparent'}; background: ${s.id === activeTerminalSessionId ? '#1f2937' : 'transparent'}; color: ${s.id === activeTerminalSessionId ? '#58a6ff' : '#94a3b8'};">
                <i class="bi bi-terminal" style="font-size: 10px;"></i>
                <span>${s.name}</span>
                ${terminalSessions.length > 1 ? `<span onclick="event.stopPropagation(); closeTerminalSession('${s.id}')" style="margin-left: 2px; opacity: 0.7; font-size: 13px; cursor: pointer; line-height: 1;" title="Đóng">&times;</span>` : ''}
            </div>
        `).join('');
    }

    const labelEl = document.getElementById("currentActiveSessionLabel");
    if (labelEl) {
        const cur = terminalSessions.find(s => s.id === activeTerminalSessionId);
        labelEl.innerText = cur ? `>_ ${cur.name}` : `>_ powershell`;
    }
}

function toggleTerminalSidebarList() {
    const sb = document.getElementById("terminalSessionsSidebar");
    if (sb) {
        const isHidden = (sb.style.display === "none");
        sb.style.display = isHidden ? "flex" : "none";
        setTimeout(() => {
            terminalSessions.forEach(s => s.fitAddon?.fit());
        }, 50);
    }
}

async function createNewTerminalSession() {
    terminalSessionCounter++;
    const newId = `term-${terminalSessionCounter}`;
    const newName = `powershell ${terminalSessionCounter}`;

    const newSession = createTerminalSessionInstance(newId, newName, null);
    if (!newSession) return;

    switchTerminalSession(newId);

    if (terminalHubConn && terminalHubConn.state === signalR.HubConnectionState.Connected) {
        try {
            await terminalHubConn.invoke("InitializeTerminal", activeProjectId || "app", newId);
        } catch (e) {
            console.warn("[Terminal] Failed to initialize new terminal session:", e);
            newSession.xterm.writeln(`\r\n\x1b[33m[Notice] Running in standalone session mode (${newId}).\x1b[0m`);
            newSession.xterm.write(`\r\n${terminalPrompt}`);
        }
    } else {
        newSession.xterm.writeln(`\r\n\x1b[33m[Notice] Running in standalone session mode (${newId}).\x1b[0m`);
        newSession.xterm.write(`\r\n${terminalPrompt}`);
    }
}

function switchTerminalSession(sessionId) {
    activeTerminalSessionId = sessionId;
    const session = terminalSessions.find(s => s.id === sessionId) || terminalSessions[0];
    if (!session) return;

    xtermInstance = session.xterm;
    fitAddon = session.fitAddon;
    terminalInputBuffer = session.inputBuffer;
    terminalCursorPos = session.cursorPos;
    terminalHistory = session.history;
    terminalHistoryIndex = session.historyIndex;
    terminalPrompt = session.prompt;

    const wrapper = document.getElementById("terminalPanesWrapper");
    if (wrapper) {
        if (isTerminalSplit && terminalSessions.length >= 2) {
            terminalSessions.forEach((s, idx) => {
                if (idx < 2) {
                    s.containerEl.style.display = "block";
                    s.containerEl.style.flex = "1 1 50%";
                    s.containerEl.style.borderRight = idx === 0 ? "1px solid #333" : "none";
                } else {
                    s.containerEl.style.display = "none";
                }
            });
        } else {
            terminalSessions.forEach(s => {
                if (s.id === sessionId) {
                    s.containerEl.style.display = "block";
                    s.containerEl.style.flex = "1 1 100%";
                    s.containerEl.style.borderRight = "none";
                } else {
                    s.containerEl.style.display = "none";
                }
            });
        }
    }

    renderTerminalSessionsSidebar();

    setTimeout(() => {
        try { session.fitAddon?.fit(); } catch (e) { }
        try { session.xterm?.focus(); } catch (e) { }
    }, 50);
}

function splitTerminalView() {
    isTerminalSplit = !isTerminalSplit;

    if (isTerminalSplit && terminalSessions.length < 2) {
        createNewTerminalSession();
        return;
    }

    switchTerminalSession(activeTerminalSessionId);
    showToastNotification(isTerminalSplit ? "Split Terminal enabled" : "Split Terminal disabled");
}

async function closeTerminalSession(sessionId) {
    if (terminalSessions.length <= 1) {
        clearTerminalOutput();
        return;
    }

    const idx = terminalSessions.findIndex(s => s.id === sessionId);
    if (idx === -1) return;

    const s = terminalSessions[idx];
    if (terminalHubConn && terminalHubConn.state === signalR.HubConnectionState.Connected) {
        terminalHubConn.invoke("KillRunningProcessInSession", sessionId).catch(() => {});
    }

    try { s.xterm?.dispose(); } catch (e) { }
    try { s.containerEl?.remove(); } catch (e) { }

    terminalSessions.splice(idx, 1);

    const nextSession = terminalSessions[Math.max(0, idx - 1)] || terminalSessions[0];
    if (nextSession) {
        switchTerminalSession(nextSession.id);
    } else {
        renderTerminalSessionsSidebar();
    }
}

function closeCurrentTerminalSession() {
    closeTerminalSession(activeTerminalSessionId);
}

async function initTerminalSignalR() {
    if (typeof signalR === "undefined") {
        setTimeout(initTerminalSignalR, 300);
        return;
    }

    const uid = localStorage.getItem("user_id") || "1";
    terminalHubConn = new signalR.HubConnectionBuilder()
        .withUrl(`/hubs/terminal?user_id=${encodeURIComponent(uid)}`)
        .withAutomaticReconnect([0, 2000, 5000, 10000])
        .build();

    terminalHubConn.on("TerminalOutput", (chunk, sId) => {
        const target = terminalSessions.find(s => s.id === (sId || activeTerminalSessionId)) || terminalSessions[0];
        if (target && target.xterm) {
            target.xterm.write(chunk);
        } else if (xtermInstance) {
            xtermInstance.write(chunk);
        }
    });

    terminalHubConn.on("TerminalClear", (sId) => {
        const target = terminalSessions.find(s => s.id === (sId || activeTerminalSessionId)) || terminalSessions[0];
        if (target && target.xterm) target.xterm.clear();
    });

    terminalHubConn.on("SessionInitialized", (ok, currentDir, sId) => {
        const target = terminalSessions.find(s => s.id === (sId || activeTerminalSessionId)) || terminalSessions[0];
        if (target && ok && currentDir) {
            target.prompt = `PS ${currentDir}> `;
            if (target.id === activeTerminalSessionId) {
                terminalPrompt = target.prompt;
            }
        }
    });

    terminalHubConn.on("DevServerDetected", (url) => {
        showToastNotification(`Dev Server running at ${url}`);
    });

    terminalHubConn.on("AppPreviewReady", (data) => {
        if (!data || !data.port) return;
        currentLiveAppPort = data.port;
        const portEl = document.getElementById("tabLiveAppPort");
        if (portEl) portEl.textContent = `:${data.port}`;
        const badge = document.getElementById("tabLiveAppBadge");
        if (badge) badge.classList.remove("d-none");
        const btnOpen = document.getElementById("tabBtnOpenApp");
        if (btnOpen) btnOpen.classList.remove("d-none");
        const btnStop = document.getElementById("tabBtnStopApp");
        if (btnStop) btnStop.classList.remove("d-none");
        showToastNotification(`🟢 Ứng dụng đã sẵn sàng trên cổng :${data.port}!`);
        loadActivePorts();
    });

    terminalHubConn.on("AppPreviewStopped", (data) => {
        currentLiveAppPort = null;
        const badge = document.getElementById("tabLiveAppBadge");
        if (badge) badge.classList.add("d-none");
        const btnOpen = document.getElementById("tabBtnOpenApp");
        if (btnOpen) btnOpen.classList.add("d-none");
        const btnStop = document.getElementById("tabBtnStopApp");
        if (btnStop) btnStop.classList.add("d-none");
        closeLiveAppPreview();
        loadActivePorts();
    });

    terminalHubConn.on("TerminalCommandFinished", (info) => {
        if (!info || info.exitCode === 0) {
            refreshFileTree();
        }
    });

    terminalHubConn.onreconnected(() => {
        if (activeProjectId) {
            terminalSessions.forEach(s => {
                terminalHubConn.invoke("InitializeTerminal", activeProjectId, s.id).catch(console.error);
            });
        }
    });

    try {
        await terminalHubConn.start();
        console.log("[Terminal] Connected to TerminalHub successfully.");
        if (activeProjectId) {
            await terminalHubConn.invoke("InitializeTerminal", activeProjectId, "term-1");
        }
    } catch (err) {
        console.warn("[Terminal] Could not connect to TerminalHub:", err);
        const s1 = terminalSessions[0];
        if (s1 && s1.xterm) {
            s1.xterm.writeln("\x1b[33m[Notice] Running in standalone terminal mode.\x1b[0m");
            s1.xterm.write(`\r\n${terminalPrompt}`);
        }
    }
}

function handleSessionKeyInput(session, data) {
    if (!session || !session.xterm) return;

    if (data === '\x03') { // Ctrl+C
        if (terminalHubConn && terminalHubConn.state === signalR.HubConnectionState.Connected) {
            terminalHubConn.invoke("KillRunningProcessInSession", session.id).catch(console.error);
        }
        session.xterm.write("^C\r\n" + (session.prompt || terminalPrompt));
        session.inputBuffer = "";
        session.cursorPos = 0;
        return;
    }

    if (data === '\r') { // Enter
        session.xterm.write("\r\n");
        const cmd = session.inputBuffer.trim();
        session.inputBuffer = "";
        session.cursorPos = 0;

        if (cmd) {
            session.history.push(cmd);
            session.historyIndex = session.history.length;
            sendTerminalCommandInSession(session, cmd);
        } else {
            session.xterm.write(session.prompt || terminalPrompt);
        }
        return;
    }

    if (data === '\x7f' || data === '\b') { // Backspace
        if (session.cursorPos > 0) {
            session.inputBuffer = session.inputBuffer.slice(0, session.cursorPos - 1) + session.inputBuffer.slice(session.cursorPos);
            session.cursorPos--;
            refreshSessionCommandLine(session);
        }
        return;
    }

    if (data === '\x1b[A') { // Up
        if (session.history.length > 0 && session.historyIndex > 0) {
            session.historyIndex--;
            session.inputBuffer = session.history[session.historyIndex];
            session.cursorPos = session.inputBuffer.length;
            refreshSessionCommandLine(session);
        }
        return;
    }

    if (data === '\x1b[B') { // Down
        if (session.historyIndex < session.history.length - 1) {
            session.historyIndex++;
            session.inputBuffer = session.history[session.historyIndex];
            session.cursorPos = session.inputBuffer.length;
            refreshSessionCommandLine(session);
        } else {
            session.historyIndex = session.history.length;
            session.inputBuffer = "";
            session.cursorPos = 0;
            refreshSessionCommandLine(session);
        }
        return;
    }

    if (data === '\x1b[C') { // Right
        if (session.cursorPos < session.inputBuffer.length) {
            session.cursorPos++;
            session.xterm.write(data);
        }
        return;
    }

    if (data === '\x1b[D') { // Left
        if (session.cursorPos > 0) {
            session.cursorPos--;
            session.xterm.write(data);
        }
        return;
    }

    if (data >= ' ' || data === '\t') {
        session.inputBuffer = session.inputBuffer.slice(0, session.cursorPos) + data + session.inputBuffer.slice(session.cursorPos);
        session.cursorPos += data.length;
        refreshSessionCommandLine(session);
    }
}

function refreshSessionCommandLine(session) {
    if (!session || !session.xterm) return;
    const prompt = session.prompt || terminalPrompt;
    session.xterm.write(`\r\x1b[2K${prompt}${session.inputBuffer}`);
    const moveBack = session.inputBuffer.length - session.cursorPos;
    if (moveBack > 0) {
        session.xterm.write(`\x1b[${moveBack}D`);
    }
}

async function sendTerminalCommandInSession(session, cmd) {
    if (terminalHubConn && terminalHubConn.state === signalR.HubConnectionState.Connected) {
        try {
            await terminalHubConn.invoke("ExecuteCommandInSession", activeProjectId || "my-project", cmd, session.id);
            return;
        } catch (err) {
            session.xterm.writeln(`\r\n\x1b[31m[Error] ${err.message}\x1b[0m`);
            session.xterm.write(`\r\n${session.prompt || terminalPrompt}`);
            return;
        }
    }
    await runCustomTerminalCmd(cmd);
}

function pasteIntoActiveTerminal() {
    navigator.clipboard.readText().then(text => {
        if (text) insertTextIntoActiveTerminal(text);
    }).catch(err => {
        console.warn("Clipboard read failed:", err);
    });
}

function insertTextIntoActiveTerminal(text) {
    const session = terminalSessions.find(s => s.id === activeTerminalSessionId) || terminalSessions[0];
    if (!session || !session.xterm) return;
    const cleanText = text.replace(/\r\n/g, '\n').replace(/\r/g, '\n');
    session.inputBuffer = session.inputBuffer.slice(0, session.cursorPos) + cleanText + session.inputBuffer.slice(session.cursorPos);
    session.cursorPos += cleanText.length;
    refreshSessionCommandLine(session);
}

// Global active ports loader & renderer
async function loadActivePorts() {
    try {
        const res = await fetch(`/api/ide/ports?project=${encodeURIComponent(activeProjectId || "")}`, {
            headers: getApiHeaders()
        });
        const data = await res.json();
        const container = document.getElementById("portsListTableContainer");
        const countBadge = document.getElementById("portsActiveCount");
        const headerBadge = document.getElementById("portsBadge");

        if (data.ok && Array.isArray(data.ports) && data.ports.length > 0) {
            if (countBadge) countBadge.textContent = `${data.ports.length} Active`;
            if (headerBadge) {
                headerBadge.textContent = data.ports.length;
                headerBadge.classList.remove("d-none");
            }

            container.innerHTML = `
                <div class="ports-table-wrapper">
                    <table class="ports-table">
                        <thead>
                            <tr>
                                <th>Port</th>
                                <th>Forwarded URL</th>
                                <th>Project</th>
                                <th>PID</th>
                                <th>Status</th>
                                <th style="text-align: right;">Actions</th>
                            </tr>
                        </thead>
                        <tbody>
                            ${data.ports.map(p => `
                                <tr>
                                    <td><span class="font-mono text-info fw-bold">:${p.port}</span></td>
                                    <td>
                                        <a href="/ide-preview/${p.port}/?user_id=${encodeURIComponent(localStorage.getItem('user_id') || '1')}" target="_blank" class="text-light text-decoration-none font-mono">
                                            http://127.0.0.1:${p.port} <i class="bi bi-box-arrow-up-right extra-small text-secondary"></i>
                                        </a>
                                    </td>
                                    <td><span class="text-warning">${p.projectName || activeProjectId}</span></td>
                                    <td><span class="text-secondary font-mono">${p.processId || '-'}</span></td>
                                    <td><span class="port-badge-running">● Running</span></td>
                                    <td style="text-align: right;">
                                        <button onclick="openLiveAppPortPreview(${p.port})" class="btn btn-sm btn-outline-info py-0 px-2 extra-small me-1">
                                            <i class="bi bi-globe me-1"></i>Preview
                                        </button>
                                        <button onclick="stopRunningPort(${p.port})" class="btn btn-sm btn-outline-danger py-0 px-2 extra-small">
                                            <i class="bi bi-stop-fill me-1"></i>Stop
                                        </button>
                                    </td>
                                </tr>
                            `).join('')}
                        </tbody>
                    </table>
                </div>
            `;
        } else {
            if (countBadge) countBadge.textContent = "0 Active";
            if (headerBadge) headerBadge.classList.add("d-none");
            container.innerHTML = `<div class="text-secondary p-3 text-center">Chưa có ứng dụng nào đang chạy trên cổng động (5200–5299). Bấm Run App để khởi động.</div>`;
        }
    } catch (e) {
        console.warn("[IDE] Error loading active ports:", e);
    }
}

function openLiveAppPortPreview(port) {
    currentLiveAppPort = port;
    openLiveAppPreview(port);
}

async function stopRunningPort(port) {
    await stopRunningApp(port);
}


function switchBottomTab(tabName) {
    const bottomPanel = document.getElementById("bottomPanel");
    const resizer = document.getElementById("resizerTerminal");
    if (bottomPanel) {
        bottomPanel.classList.remove("ide-panel-hidden");
        bottomPanel.style.display = "flex";
        if (resizer) resizer.style.display = "block";
    }

    ['bottomTabBtnTerminal', 'bottomTabBtnProblems', 'bottomTabBtnOutput', 'bottomTabBtnPorts'].forEach(id => {
        document.getElementById(id)?.classList.remove("active");
    });
    ['bottomTabTerminalBody', 'bottomTabProblemsBody', 'bottomTabOutputBody', 'bottomTabPortsBody'].forEach(id => {
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
    } else if (tabName === 'ports') {
        document.getElementById('bottomTabBtnPorts')?.classList.add("active");
        const portsBody = document.getElementById('bottomTabPortsBody');
        if (portsBody) portsBody.style.display = "block";
        loadActivePorts();
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

function detectProjectType() {
    const files = Array.isArray(workspaceFilesList) ? workspaceFilesList : [];
    const hasRootAngular = files.some(f => (f.path === "angular.json" || f.path === "./angular.json" || f.path === "/angular.json"));
    const hasFrontendAngular = files.some(f => (f.path.startsWith("frontend/") || f.path.startsWith("frontend\\")) && f.path.includes("angular.json"));
    const inFrontend = hasFrontendAngular && !hasRootAngular;
    const hasAngular = hasRootAngular || hasFrontendAngular 
        || files.some(f => f.path.includes("angular.json") || (f.path.includes("package.json") && f.path.includes("frontend"))) 
        || (activeProjectId && activeProjectId.toLowerCase().includes("angular"));
    
    const hasIndexHtml = files.some(f => f.path === "index.html" || f.path.endsWith("/index.html") || f.path.endsWith("\\index.html"));
    if (hasAngular) {
        return { type: "angular", inFrontend: inFrontend };
    }

    if (hasIndexHtml && !files.some(f => f.path.endsWith(".csproj") || f.path.endsWith(".sln"))) {
        return { type: "web" };
    }

    const hasDotnet = files.some(f => f.path.endsWith(".csproj") || f.path.endsWith(".sln"));
    if (hasDotnet) return { type: "dotnet" };

    const hasPackageJson = files.some(f => f.path.endsWith("package.json"));
    if (hasPackageJson) return { type: "node" };

    const hasPython = files.some(f => f.path.endsWith(".py") || f.path.endsWith("requirements.txt"));
    if (hasPython) return { type: "python" };

    return { type: "unknown" };
}

async function fetchDynamicPortAndRun(type, prefix = "") {
    let port = 5201;
    try {
        const res = await fetch(`/api/ide/allocate-port?project=${encodeURIComponent(activeProjectId || "app")}`, {
            headers: getApiHeaders()
        });
        const data = await res.json();
        if (data.ok && data.port) {
            port = data.port;
        }
    } catch (e) {
        console.warn("[IDE] Port allocation fallback to 5201:", e);
    }

    if (type === "dotnet") {
        showToastNotification(`Đang khởi chạy .NET trên cổng động :${port}...`);
        runCustomTerminalCmd(`dotnet run --urls http://127.0.0.1:${port}`);
    } else if (type === "angular") {
        showToastNotification(`Đang khởi chạy Angular trên cổng động :${port}...`);
        const projInfo = detectProjectType();
        const navCmd = projInfo.inFrontend ? `cd frontend; npx ng serve --port ${port}` : `npx ng serve --port ${port}`;
        runCustomTerminalCmd(navCmd);
    }
}

function runSmartAction() {
    switchBottomTab('terminal');
    const projInfo = detectProjectType();
    const ext = activeFilePath ? activeFilePath.split('.').pop().toLowerCase() : '';

    // 1. Angular Project
    if (projInfo.type === 'angular') {
        if (activeFilePath && activeFilePath.endsWith('.spec.ts')) {
            showToastNotification("Running Angular Tests (ng test)...");
            const testCmd = projInfo.inFrontend ? `cd frontend; npx ng test` : `npx ng test`;
            runCustomTerminalCmd(testCmd);
        } else {
            showToastNotification("Starting Angular App...");
            fetchDynamicPortAndRun("angular");
        }
        return;
    }

    // 2. Node.js / Frontend Project
    if (projInfo.type === 'node') {
        if (ext === 'js' || ext === 'ts') {
            runCustomTerminalCmd(`node ${activeFilePath}`);
        } else {
            runCustomTerminalCmd("npm start");
        }
        return;
    }

    // 3. Python Project / File
    if (ext === 'py' || projInfo.type === 'python') {
        if (activeFilePath && ext === 'py') {
            runCustomTerminalCmd(`python ${activeFilePath}`);
        } else {
            runCustomTerminalCmd("python main.py");
        }
        return;
    }

    // 4. Pure HTML / Web Static Project or File
    if (ext === 'html' || ext === 'htm' || projInfo.type === 'web') {
        openLivePreview();
        return;
    }

    // 5. JavaScript single file
    if (ext === 'js') {
        runCustomTerminalCmd(`node ${activeFilePath}`);
        return;
    }

    // 6. .NET / C# Project (default)
    if (activeFilePath && activeFilePath.endsWith('Test.cs')) {
        runCustomTerminalCmd("dotnet test");
    } else {
        fetchDynamicPortAndRun("dotnet");
    }
}

function runBuildInTerminal() {
    switchBottomTab('terminal');
    const projInfo = detectProjectType();
    if (projInfo.type === 'angular') {
        const prefix = projInfo.inFrontend ? 'cd frontend; ' : '';
        runCustomTerminalCmd(`${prefix}npx ng build`);
    } else if (projInfo.type === 'node') {
        runCustomTerminalCmd("npm run build");
    } else {
        runCustomTerminalCmd("dotnet build");
    }
}

function runTestInTerminal() {
    switchBottomTab('terminal');
    const projInfo = detectProjectType();
    if (projInfo.type === 'angular') {
        const prefix = projInfo.inFrontend ? 'cd frontend; ' : '';
        runCustomTerminalCmd(`${prefix}npx ng test`);
    } else if (projInfo.type === 'node') {
        runCustomTerminalCmd("npm test");
    } else {
        runCustomTerminalCmd("dotnet test");
    }
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

function triggerLivePreview() {
    openLivePreview();
}

async function openLivePreview() {
    if (!activeProjectId) {
        alert("Chưa có dự án nào được chọn. Hãy tạo hoặc mở dự án web có index.html để xem trước.");
        return;
    }

    const uid = localStorage.getItem("user_id") || "1";
    const previewUrl = `/api/ide/preview/${encodeURIComponent(activeProjectId)}/index.html?user_id=${encodeURIComponent(uid)}`;
    const iframe = document.getElementById("previewIframe");
    const badge = document.getElementById("previewModalUrlBadge");
    const newTabBtn = document.getElementById("previewOpenTabBtn");

    if (badge) badge.innerText = `/api/ide/preview/${activeProjectId}/index.html`;
    if (newTabBtn) newTabBtn.href = previewUrl;

    showModalSafely("previewModal");

    if (iframe) {
        // Luôn xóa sạch srcdoc để tránh opaque null origin trong Chrome làm hỏng script / localStorage
        try {
            iframe.removeAttribute("srcdoc");
        } catch {}
        // Nạp trực tiếp URL cùng domain để có đầy đủ quyền same-origin, chạy JavaScript và tải CSS hoàn hảo
        iframe.src = previewUrl;
    }
}

function refreshPreviewIframe() {
    openLivePreview();
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

async function resetAgentChat() {
    if (activeProjectId) {
        try {
            await fetch(`/api/ide/agent/history?project=${encodeURIComponent(activeProjectId)}`, {
                method: 'DELETE',
                headers: getApiHeaders()
            });
        } catch (e) { }
    }
    const splash = document.getElementById("chatSplashArea");
    const container = document.getElementById("agentMessagesContainer");
    if (splash) splash.style.display = "flex";
    if (container) {
        container.style.display = "none";
        container.innerHTML = "";
    }
    showToastNotification("Đã bắt đầu phiên trò chuyện mới!");
}

async function loadAgentChatHistory(projectId) {
    if (!projectId) return;
    const container = document.getElementById("agentMessagesContainer");
    const splash = document.getElementById("chatSplashArea");
    if (!container) return;

    try {
        const res = await fetch(`/api/ide/agent/history?project=${encodeURIComponent(projectId)}`, {
            headers: getApiHeaders()
        });
        const data = await res.json();
        const list = (data.messages || data.history || []);
        if (data.ok && Array.isArray(list) && list.length > 0) {
            if (splash) splash.style.display = "none";
            container.style.display = "flex";
            container.innerHTML = "";

            list.forEach(item => {
                if (item.role === "user") {
                    const userMsg = document.createElement("div");
                    userMsg.className = "p-2 rounded bg-dark border border-secondary text-light extra-small align-self-end";
                    userMsg.style.maxWidth = "85%";
                    userMsg.innerText = item.content;
                    container.appendChild(userMsg);
                } else {
                    const agentMsg = document.createElement("div");
                    agentMsg.className = "d-flex flex-column gap-2 extra-small text-light";
                    let parsedPlan = [];
                    let parsedOps = [];
                    try { if (item.planJson) parsedPlan = JSON.parse(item.planJson); } catch(e){}
                    try { if (item.toolActionsJson) parsedOps = JSON.parse(item.toolActionsJson); } catch(e){}
                    if (!parsedPlan.length && Array.isArray(item.plan)) parsedPlan = item.plan;
                    if (!parsedOps.length && Array.isArray(item.operations)) parsedOps = item.operations;

                    renderAgentResponse({
                        explanation: item.content,
                        plan: parsedPlan,
                        operations: parsedOps,
                        modifiedFiles: item.modifiedFiles || [],
                        executedCommands: item.executedCommands || []
                    }, agentMsg);
                    container.appendChild(agentMsg);
                }
            });
            container.scrollTop = container.scrollHeight;
        } else {
            if (splash) splash.style.display = "flex";
            container.style.display = "none";
            container.innerHTML = "";
        }
    } catch (e) {
        console.warn("Failed to load agent chat history:", e);
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
            <span>Đang phân tích cấu trúc dự án & tìm kiếm tệp...</span>
        </div>
    `;
    container.appendChild(agentMsg);
    container.scrollTop = container.scrollHeight;

    const model = document.getElementById("agentModelSelect")?.value || "gemini-2.5-flash";
    const mode = document.getElementById("agentModeSelect")?.value || "fast";

    // Animated Live Progress Ticker
    let elapsedSec = 0;
    const progressStages = [
        "Đang phân tích cấu trúc dự án & tìm kiếm tệp...",
        "Đang tự động viết mã nguồn & tạo Views/Controllers...",
        "Đang biên dịch kiểm tra mã nguồn (dotnet build)...",
        "Đang rà soát và hoàn tất giải pháp..."
    ];
    let stageIdx = 0;
    const pillSpan = agentMsg.querySelector(".task-running-pill span");
    const ticker = setInterval(() => {
        elapsedSec += 2;
        stageIdx = (stageIdx + 1) % progressStages.length;
        if (pillSpan) {
            pillSpan.innerText = `${progressStages[stageIdx]} (${elapsedSec}s)`;
        }
    }, 2000);

    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), 240000);

    try {
        const res = await fetch('/api/ide/agent-run', {
            method: 'POST',
            headers: {
                ...getApiHeaders(),
                'Accept': 'text/event-stream'
            },
            signal: controller.signal,
            body: JSON.stringify({
                project: activeProjectId,
                activeFile: activeFilePath,
                prompt: task,
                mode: mode,
                model: model,
                stream: true
            })
        });
        clearTimeout(timeoutId);

        if (!res.ok) {
            let errMsg = `HTTP error ${res.status}`;
            try {
                const errData = await res.json();
                errMsg = errData.message || errData.error || errMsg;
            } catch {}
            agentMsg.innerHTML = `<div class="text-danger extra-small p-2 bg-dark rounded border border-danger">❌ ${errMsg}</div>`;
            return;
        }

        const contentType = res.headers.get("content-type") || "";
        if (contentType.includes("text/event-stream")) {
            const reader = res.body.getReader();
            const decoder = new TextDecoder("utf-8");
            let buffer = "";
            let finalData = null;

            while (true) {
                const { done, value } = await reader.read();
                if (done) break;
                buffer += decoder.decode(value, { stream: true });

                const lines = buffer.split("\n\n");
                buffer = lines.pop() || "";

                for (const chunk of lines) {
                    if (!chunk.trim()) continue;
                    let eventType = "message";
                    let eventDataStr = "";

                    const chunkLines = chunk.split("\n");
                    for (const cl of chunkLines) {
                        const trimmedLine = cl.trim();
                        if (trimmedLine.startsWith(":") || !trimmedLine) {
                            // SSE comment / keepalive ping -> ignore
                            continue;
                        }
                        if (cl.startsWith("event:")) {
                            eventType = cl.substring(6).trim();
                        } else if (cl.startsWith("data:")) {
                            eventDataStr = cl.substring(5).trim();
                        }
                    }

                    if (!eventDataStr) continue;
                    let eventData = {};
                    try { eventData = JSON.parse(eventDataStr); } catch { eventData = { text: eventDataStr }; }

                    // Process stream event
                    if (eventType === "status" || eventType === "turn_start") {
                        if (pillSpan) {
                            pillSpan.innerText = eventData.message || `Lượt ${eventData.turn}: Đang xử lý...`;
                        }
                    } else if (eventType === "tool_start") {
                        if (pillSpan) {
                            pillSpan.innerText = eventData.message || `Đang chạy ${eventData.tool}...`;
                        }
                    } else if (eventType === "tool_done") {
                        if (pillSpan) {
                            pillSpan.innerText = eventData.message || `Đã xong ${eventData.tool}`;
                        }
                        // Real-time update: refresh tree and open created/edited file immediately
                        if (eventData.action === 'create' || eventData.action === 'edit' || eventData.action === 'image') {
                            if (typeof refreshFileTree === 'function') {
                                refreshFileTree().catch(console.warn);
                            }
                            if (eventData.target && typeof openFileInEditor === 'function') {
                                openFileInEditor(eventData.target, true).catch(console.warn);
                            }
                        }
                    } else if (eventType === "done") {
                        finalData = eventData;
                        renderAgentResponse(finalData, agentMsg);
                        if (typeof refreshFileTree === 'function') {
                            await refreshFileTree().catch(console.warn);
                        }
                        if (typeof reloadOpenFileTabs === 'function') {
                            await reloadOpenFileTabs(finalData.modifiedFiles).catch(console.warn);
                        }
                        if (Array.isArray(finalData.modifiedFiles) && finalData.modifiedFiles.length > 0) {
                            const codeFiles = finalData.modifiedFiles.filter(f => !(/\.(jpg|jpeg|png|gif|webp|svg|ico)$/i.test(f)));
                            const targetOpen = (!activeFilePath || !openTabsMap.has(activeFilePath)) ? (codeFiles[0] || finalData.modifiedFiles[0]) : activeFilePath;
                            if (targetOpen && typeof openFileInEditor === 'function') {
                                await openFileInEditor(targetOpen, true).catch(console.warn);
                            }
                        }
                    } else if (eventType === "error") {
                        agentMsg.innerHTML = `<div class="text-danger extra-small p-2 bg-dark rounded border border-danger">❌ ${eventData.error || "Agent execution failed"}</div>`;
                    }
                }
            }

            if (!finalData && !agentMsg.querySelector(".text-danger")) {
                if (typeof refreshFileTree === 'function') {
                    await refreshFileTree().catch(console.warn);
                }
            }
        } else {
            const data = await res.json();
            if (data.ok) {
                renderAgentResponse(data, agentMsg);
                if (typeof refreshFileTree === 'function') {
                    await refreshFileTree().catch(console.warn);
                }
                if (typeof reloadOpenFileTabs === 'function') {
                    await reloadOpenFileTabs(data.modifiedFiles).catch(console.warn);
                }
                if (Array.isArray(data.modifiedFiles) && data.modifiedFiles.length > 0) {
                    const targetOpen = (!activeFilePath || !openTabsMap.has(activeFilePath)) ? data.modifiedFiles[0] : activeFilePath;
                    if (targetOpen && typeof openFileInEditor === 'function') {
                        await openFileInEditor(targetOpen, true).catch(console.warn);
                    }
                }
            } else {
                agentMsg.innerHTML = `<div class="text-danger extra-small p-2 bg-dark rounded border border-danger">❌ ${data.message || data.error || "Agent execution failed"}</div>`;
            }
        }
    } catch (err) {
        clearTimeout(timeoutId);
        const isAbort = err.name === 'AbortError';
        let errHtml = `<div class="text-danger extra-small p-2 bg-dark rounded border border-danger">❌ ${isAbort ? 'Quá thời gian xử lý (timeout 240s). Vui lòng thử lại với prompt cụ thể hơn.' : ('Error: ' + err.message)}</div>`;
        errHtml += `
            <div class="mt-2 d-flex align-items-center gap-2">
                <button type="button" class="btn btn-sm btn-outline-warning" style="font-size: 11px;" onclick="continueAgentRemaining(${JSON.stringify(task)})">
                    <i class="bi bi-play-circle me-1"></i> Tiếp tục tạo các file còn lại
                </button>
            </div>
        `;
        agentMsg.innerHTML = errHtml;
        if (typeof refreshFileTree === 'function') {
            try { await refreshFileTree(); } catch(e) { console.warn(e); }
        }
    } finally {
        clearInterval(ticker);
    }
}

window.continueAgentRemaining = function(origPrompt) {
    const input = document.getElementById("agentPromptInput");
    if (input) {
        input.value = `Tiếp tục hoàn thiện các tệp còn thiếu trong dự án theo yêu cầu: "${origPrompt}". BẮT BUỘC gọi write_file để tạo các tệp chưa có với code hoàn chỉnh 100%.`;
        input.focus();
        runAgentTask();
    }
};

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

    // 2. Explanation / Message (Lọc sạch JSON Plan thô nếu AI vô tình xuất)
    let explanationText = data.explanation || "";
    if (explanationText.trim().startsWith("{") && explanationText.includes('"plan"')) {
        try {
            const parsed = JSON.parse(explanationText.trim());
            explanationText = parsed.explanation || (Array.isArray(parsed.plan) ? "Đã phân tích kế hoạch và thực hiện cập nhật dự án thành công." : "");
        } catch {
            explanationText = "Đã hoàn tất xử lý yêu cầu cập nhật dự án.";
        }
    }
    if (explanationText) {
        const expEl = document.createElement("div");
        expEl.className = "p-2 rounded bg-dark border border-secondary text-light extra-small";
        expEl.innerText = explanationText;
        container.appendChild(expEl);
    }

    // 2.5. Modified Files Card (Hiển thị các tệp đã trực tiếp sửa/tạo trên ổ đĩa)
    if (Array.isArray(data.modifiedFiles) && data.modifiedFiles.length > 0) {
        const modCard = document.createElement("div");
        modCard.className = "agent-modified-files-card p-2 rounded bg-dark border border-secondary mb-2";
        modCard.innerHTML = `
            <div class="d-flex align-items-center justify-content-between mb-1.5">
                <span class="fw-semibold text-success extra-small"><i class="bi bi-check-circle-fill me-1"></i> Đã cập nhật ${data.modifiedFiles.length} tệp trên ổ đĩa:</span>
                ${data.buildPassed ? '<span class="badge bg-success extra-small"><i class="bi bi-shield-check me-1"></i>dotnet build: Pass</span>' : ''}
            </div>
            <div class="d-flex flex-column gap-1">
                ${data.modifiedFiles.map(f => `
                    <div class="p-1 px-2 rounded bg-black border border-secondary text-light extra-small d-flex align-items-center justify-content-between">
                        <span class="font-mono text-info"><i class="bi bi-file-earmark-code me-1"></i>${escapeHtml(f)}</span>
                        <button class="btn btn-xs btn-outline-info py-0 px-1 extra-small" onclick="openFileInEditor('${escapeHtml(f.replace(/\\/g, '/'))}', true)">Xem</button>
                    </div>
                `).join('')}
            </div>
        `;
        container.appendChild(modCard);
    }

    // 2.6. Executed Commands Card (Kiểm tra lệnh dotnet build / shell đã chạy)
    if (Array.isArray(data.executedCommands) && data.executedCommands.length > 0) {
        const cmdCard = document.createElement("div");
        cmdCard.className = "agent-executed-commands-card p-2 rounded bg-dark border border-secondary mb-2";
        cmdCard.innerHTML = `
            <div class="fw-semibold text-warning extra-small mb-1"><i class="bi bi-terminal me-1"></i> Lệnh đã thực thi kiểm tra:</div>
            <div class="d-flex flex-column gap-1 font-mono" style="font-size: 11px;">
                ${data.executedCommands.map(cmd => `
                    <div class="p-1 px-2 rounded bg-black border border-secondary text-secondary d-flex align-items-center">
                        <span class="text-secondary me-1.5">&gt;</span>
                        <span class="text-light">${escapeHtml(cmd)}</span>
                    </div>
                `).join('')}
            </div>
        `;
        container.appendChild(cmdCard);
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
        const isAlreadyApplied = Boolean(data.alreadyApplied || (Array.isArray(data.modifiedFiles) && data.modifiedFiles.length > 0) || (opsList.length > 0 && opsList.every(o => o.alreadyApplied)));

        const actionFooterHtml = isAlreadyApplied ? `
            <div class="alert alert-success extra-small py-1.5 px-2 mb-0 d-flex align-items-center justify-content-between">
                <span><i class="bi bi-check2-circle me-1.5 text-success"></i> Đã tự động áp dụng vào dự án (${opsList.length} mã nguồn, ${imgsList.length} ảnh AI)</span>
                <button class="btn btn-sm btn-outline-light extra-small py-0.5 px-2" onclick="refreshFileTree()"><i class="bi bi-arrow-clockwise"></i> Đồng bộ</button>
            </div>
        ` : `
            <button id="btnBatchAcceptAll" class="btn-batch-accept w-100" onclick="applyAllAgentOperations()">
                <i class="bi bi-check2-all fs-6"></i> Áp dụng tất cả (${opsList.length} mã nguồn, ${imgsList.length} ảnh AI)
            </button>
        `;

        opsCard.innerHTML = `
            <div class="agent-operations-header">
                <span><i class="bi bi-files me-1.5 text-info"></i> ${totalItems} tệp & hình ảnh ${isAlreadyApplied ? 'đã tạo' : 'đề xuất'}</span>
                <span class="badge ${isAlreadyApplied ? 'bg-success' : 'bg-secondary'} extra-small">${isAlreadyApplied ? 'Live Applied' : 'AI Scaffolding'}</span>
            </div>
            <div class="d-flex flex-column gap-1.5 my-1">
                ${opsHtml}
                ${imgsHtml}
            </div>
            ${actionFooterHtml}
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

async function reviewSingleOperation(index) {
    if (!window.currentAgentOperations || !window.currentAgentOperations[index]) return;
    const op = window.currentAgentOperations[index];
    let content = op.content;
    if ((!content || content.length === 0) && op.path) {
        try {
            const uid = localStorage.getItem("user_id") || "1";
            const res = await fetch(`/api/ide/file-content?project=${encodeURIComponent(activeProjectId)}&file=${encodeURIComponent(op.path)}&user_id=${encodeURIComponent(uid)}&_t=${Date.now()}`, {
                headers: getApiHeaders()
            });
            const data = await res.json();
            if (data.ok && data.content) {
                content = data.content;
                op.content = content;
            }
        } catch (e) {
            console.warn("Failed to fetch op content for review", e);
        }
    }
    activeDiffProposal = {
        file: op.path,
        content: content || "",
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

    // Safety: ensure any operation without content fetches its disk content before apply
    for (const op of ops) {
        if ((!op.content || op.content.length === 0) && op.path) {
            try {
                const uid = localStorage.getItem("user_id") || "1";
                const res = await fetch(`/api/ide/file-content?project=${encodeURIComponent(proj)}&file=${encodeURIComponent(op.path)}&user_id=${encodeURIComponent(uid)}&_t=${Date.now()}`, {
                    headers: getApiHeaders()
                });
                const data = await res.json();
                if (data.ok && data.content) {
                    op.content = data.content;
                }
            } catch (e) {}
        }
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

    const btn = document.getElementById("btnSubmitCreateProject");
    const origBtnHtml = btn ? btn.innerHTML : `<i class="bi bi-plus-circle me-1"></i> Tạo Dự Án`;
    if (btn) {
        btn.disabled = true;
        btn.innerHTML = `<span class="spinner-border spinner-border-sm me-1.5" role="status" aria-hidden="true"></span> Đang khởi tạo dự án qua CLI, vui lòng đợi...`;
    }

    const abortCtrl = new AbortController();
    const timeoutId = setTimeout(() => abortCtrl.abort(), 95000); // 95s timeout for CLI operations

    try {
        const uid = localStorage.getItem("user_id") || "1";
        const res = await fetch('/api/ide/create-project', {
            method: 'POST',
            headers: getApiHeaders(),
            body: JSON.stringify({ name, template, userId: parseInt(uid) || 1 }),
            signal: abortCtrl.signal
        });
        clearTimeout(timeoutId);
        const data = await res.json();
        if (data.ok) {
            hideModalSafely("createProjectModal");
            const nameInput = document.getElementById("newProjectName");
            if (nameInput) nameInput.value = "";

            await loadUserProjects();

            // Switch to the newly created project
            const cleanName = data.project || name;
            await switchActiveProject(cleanName);

            // Automatically open primary file if created
            if (data.primaryFile) {
                setTimeout(async () => {
                    try {
                        await openFileInEditor(data.primaryFile);
                    } catch (e) {
                        console.warn("Could not auto-open primary file:", e);
                    }
                }, 400);
            }

            showToastNotification(`Đã tạo dự án '${cleanName}' theo chuẩn CLI thành công!`);
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
        clearTimeout(timeoutId);
        console.error("Error creating project:", err);
        const msg = err.name === "AbortError" 
            ? "Lệnh khởi tạo CLI mất quá nhiều thời gian (>90s). Vui lòng kiểm tra lại kết nối mạng." 
            : (err.message || "Lỗi khi gọi API tạo dự án.");
        if (errBox) {
            errBox.textContent = "❌ " + msg;
            errBox.style.display = "block";
        }
        showToastNotification("Lỗi: " + msg);
    } finally {
        if (btn) {
            btn.disabled = false;
            btn.innerHTML = origBtnHtml;
        }
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
                if (activeTreeNode.isProjectRoot) {
                    promptRenameActiveProject();
                } else {
                    startInlineRename(activeTreeNode);
                }
                return;
            }

            // Delete: Delete Permanently
            if (e.key === "Delete") {
                e.preventDefault();
                if (activeTreeNode.isProjectRoot) {
                    confirmDeleteActiveProject();
                } else {
                    contextTargetItem = activeTreeNode;
                    contextMenuDeletePermanently();
                }
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

function onExplorerContextMenu(e, path, isDirectory, targetEl = null, isProjectRoot = false) {
    e.preventDefault();
    e.stopPropagation();
    contextTargetItem = { path, isDirectory, el: targetEl, isProjectRoot };

    const menu = document.getElementById("explorerContextMenu");
    if (!menu) return;

    // Toggle items based on Project vs Folder vs File
    const ctxProject = document.getElementById("ctxProjectItems");
    const ctxFolder = document.getElementById("ctxFolderItems");
    const ctxFile = document.getElementById("ctxFileItems");

    if (isProjectRoot) {
        if (ctxProject) ctxProject.style.display = "block";
        if (ctxFolder) ctxFolder.style.display = "none";
        if (ctxFile) ctxFile.style.display = "none";
    } else {
        if (ctxProject) ctxProject.style.display = "none";
        if (ctxFolder) ctxFolder.style.display = isDirectory ? "block" : "none";
        if (ctxFile) ctxFile.style.display = !isDirectory ? "block" : "none";
    }

    // Toggle Paste availability
    const pasteBtn = document.getElementById("ctxItemPaste");
    if (pasteBtn) {
        if (explorerClipboard && explorerClipboard.path && !isProjectRoot) {
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

function onProjectContextMenu(e) {
    if (!activeProjectId) return;
    const rootHeader = document.getElementById("workspaceRootHeader");
    onExplorerContextMenu(e, "", true, rootHeader, true);
}

// Project Actions: Rename & Delete
async function promptRenameActiveProject() {
    hideExplorerContextMenu();
    if (!activeProjectId) {
        alert("Chưa có dự án nào được chọn.");
        return;
    }
    const newName = prompt(`Nhập tên mới cho dự án "${activeProjectId}":`, activeProjectId);
    if (!newName || newName.trim() === "" || newName.trim() === activeProjectId) return;

    try {
        const uid = localStorage.getItem("user_id") || "1";
        const res = await fetch('/api/ide/projects/rename', {
            method: 'POST',
            headers: getApiHeaders(),
            body: JSON.stringify({ oldName: activeProjectId, newName: newName.trim(), userId: parseInt(uid) || 1 })
        });
        const data = await res.json();
        if (data.ok) {
            showToastNotification(data.message || `Đã đổi tên dự án thành "${newName.trim()}"`);
            await loadUserProjects();
            await switchActiveProject(newName.trim());
        } else {
            alert("❌ " + (data.error || "Không thể đổi tên dự án"));
        }
    } catch (err) {
        alert("❌ Lỗi đổi tên dự án: " + err.message);
    }
}

async function confirmDeleteActiveProject() {
    hideExplorerContextMenu();
    if (!activeProjectId) {
        alert("Chưa có dự án nào được chọn.");
        return;
    }
    if (!confirm(`⚠️ CẢNH BÁO: Bạn có chắc chắn muốn xóa vĩnh viễn toàn bộ dự án "${activeProjectId}" và tất cả file bên trong không? Thao tác này không thể hoàn tác!`)) {
        return;
    }

    try {
        const uid = localStorage.getItem("user_id") || "1";
        const res = await fetch('/api/ide/projects/delete', {
            method: 'POST',
            headers: getApiHeaders(),
            body: JSON.stringify({ name: activeProjectId, userId: parseInt(uid) || 1 })
        });
        const data = await res.json();
        if (data.ok) {
            showToastNotification(data.message || `Đã xóa dự án "${activeProjectId}"`);
            openTabsMap.clear();
            renderTabBar();
            await loadUserProjects();
        } else {
            alert("❌ " + (data.error || "Không thể xóa dự án"));
        }
    } catch (err) {
        alert("❌ Lỗi xóa dự án: " + err.message);
    }
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
// 18. TOAST NOTIFICATION (TOP-CENTER NON-BLOCKING)
// ==========================================
function showToastNotification(msg) {
    let container = document.getElementById("ideToastContainer");
    if (!container) {
        container = document.createElement("div");
        container.id = "ideToastContainer";
        container.style.position = "fixed";
        container.style.top = "42px";
        container.style.left = "50%";
        container.style.transform = "translateX(-50%)";
        container.style.zIndex = "99999";
        container.style.display = "flex";
        container.style.flexDirection = "column";
        container.style.alignItems = "center";
        container.style.gap = "6px";
        container.style.pointerEvents = "none";
        document.body.appendChild(container);
    }

    const toast = document.createElement("div");
    toast.style.background = "#1e2433";
    toast.style.color = "#f1f5f9";
    toast.style.border = "1px solid rgba(99,102,241,0.4)";
    toast.style.borderRadius = "6px";
    toast.style.padding = "6px 14px";
    toast.style.fontSize = "12px";
    toast.style.boxShadow = "0 8px 24px rgba(0,0,0,0.6)";
    toast.style.display = "flex";
    toast.style.alignItems = "center";
    toast.style.gap = "8px";
    toast.style.pointerEvents = "auto";
    toast.style.transition = "all 0.25s ease";
    toast.style.opacity = "0";
    toast.style.transform = "translateY(-10px)";
    toast.innerHTML = `<i class="bi bi-info-circle text-info"></i> <span>${escapeHtml(msg)}</span> <button onclick="this.parentElement.remove()" style="background:none;border:none;color:#94a3b8;font-size:14px;cursor:pointer;margin-left:6px;line-height:1;" title="Đóng">&times;</button>`;

    container.appendChild(toast);
    setTimeout(() => {
        toast.style.opacity = "1";
        toast.style.transform = "translateY(0)";
    }, 20);

    setTimeout(() => {
        toast.style.opacity = "0";
        toast.style.transform = "translateY(-10px)";
        setTimeout(() => toast.remove(), 250);
    }, 4000);
}

// ==========================================
// SQLITE STUDIO & LIVE APP PREVIEW MODULES
// ==========================================
let currentSqliteDbPath = "";
let currentLiveAppPort = null;

async function openSqliteStudio(filePath) {
    currentSqliteDbPath = filePath;
    const fileName = filePath.split(/[\\/]/).pop();

    const mContainer = document.getElementById("monacoContainer");
    const diffContainer = document.getElementById("monacoDiffContainer");
    const sqliteContainer = document.getElementById("sqliteStudioContainer");
    const liveAppContainer = document.getElementById("liveAppPreviewContainer");

    if (mContainer) mContainer.style.display = "none";
    if (diffContainer) diffContainer.style.display = "none";
    if (liveAppContainer) liveAppContainer.style.display = "none";
    if (sqliteContainer) sqliteContainer.style.display = "flex";

    const tab = {
        path: filePath,
        name: fileName,
        language: 'sqlite',
        isSqlite: true,
        model: null,
        isDirty: false
    };

    openTabsMap.set(filePath, tab);
    activeFilePath = filePath;
    updateEditorUI(tab);

    await loadSqliteTables(filePath);
}

async function loadSqliteTables(dbPath) {
    const listEl = document.getElementById("sqliteTableList");
    if (!listEl) return;
    listEl.innerHTML = `<div class="text-secondary extra-small p-2"><div class="spinner-border spinner-border-sm me-1 text-info"></div> Loading tables...</div>`;

    try {
        const res = await fetch(`/api/ide/db/tables?project=${encodeURIComponent(activeProjectId || "")}&path=${encodeURIComponent(dbPath)}`, {
            headers: getApiHeaders()
        });
        const data = await res.json();
        if (data.ok && Array.isArray(data.tables)) {
            if (data.tables.length === 0) {
                listEl.innerHTML = `<div class="text-secondary extra-small p-2">Database chưa có bảng nào.</div>`;
                return;
            }

            listEl.innerHTML = "";
            data.tables.forEach((t, idx) => {
                const item = document.createElement("div");
                item.className = "sqlite-table-item" + (idx === 0 ? " active" : "");
                item.innerHTML = `
                    <span><i class="bi bi-table me-1.5 text-info"></i>${escapeHtml(t.name)}</span>
                    <span class="badge bg-secondary extra-small">${t.rowCount}</span>
                `;
                item.onclick = () => {
                    document.querySelectorAll(".sqlite-table-item").forEach(el => el.classList.remove("active"));
                    item.classList.add("active");
                    selectSqliteTable(t.name);
                };
                listEl.appendChild(item);
            });

            // Auto-select first table
            selectSqliteTable(data.tables[0].name);
        } else {
            listEl.innerHTML = `<div class="text-danger extra-small p-2">${escapeHtml(data.error || "Không thể nạp bảng")}</div>`;
        }
    } catch (err) {
        listEl.innerHTML = `<div class="text-danger extra-small p-2">${escapeHtml(err.message)}</div>`;
    }
}

function selectSqliteTable(tableName) {
    const input = document.getElementById("sqliteSqlInput");
    if (input) {
        input.value = `SELECT * FROM "${tableName}" LIMIT 100;`;
        executeSqliteQuery();
    }
}

async function executeSqliteQuery() {
    const input = document.getElementById("sqliteSqlInput");
    const statusEl = document.getElementById("sqliteQueryStatus");
    const resultBox = document.getElementById("sqliteResultContainer");
    if (!input || !resultBox) return;

    const sql = input.value.trim();
    if (!sql) return;

    if (statusEl) statusEl.innerHTML = `<div class="spinner-border spinner-border-sm text-info" style="width: 12px; height: 12px;"></div> Đang chạy...`;

    try {
        const res = await fetch('/api/ide/db/query', {
            method: 'POST',
            headers: getApiHeaders(),
            body: JSON.stringify({
                project: activeProjectId || "",
                path: currentSqliteDbPath,
                sql: sql
            })
        });
        const data = await res.json();
        if (data.ok) {
            if (data.isQuery) {
                if (statusEl) statusEl.textContent = `${data.rowCount} dòng (${data.elapsedMs} ms)`;

                if (!data.columns || data.columns.length === 0) {
                    resultBox.innerHTML = `<div class="text-secondary extra-small p-3">Kết quả trống.</div>`;
                    return;
                }

                let html = `<table class="sqlite-table"><thead><tr>`;
                data.columns.forEach(col => {
                    html += `<th>${escapeHtml(col)}</th>`;
                });
                html += `</tr></thead><tbody>`;

                data.rows.forEach(row => {
                    html += `<tr>`;
                    data.columns.forEach(col => {
                        const val = row[col];
                        if (val === null || val === undefined) {
                            html += `<td class="sqlite-null">NULL</td>`;
                        } else {
                            html += `<td>${escapeHtml(String(val))}</td>`;
                        }
                    });
                    html += `</tr>`;
                });
                html += `</tbody></table>`;
                resultBox.innerHTML = html;
            } else {
                if (statusEl) statusEl.textContent = `${data.rowsAffected} dòng ảnh hưởng (${data.elapsedMs} ms)`;
                resultBox.innerHTML = `<div class="alert alert-success extra-small m-3 py-2 px-3"><i class="bi bi-check-circle me-1"></i> ${escapeHtml(data.message || "Thực thi thành công.")}</div>`;
                refreshSqliteTables();
            }
        } else {
            if (statusEl) statusEl.textContent = `Lỗi (${data.elapsedMs || 0} ms)`;
            resultBox.innerHTML = `<div class="alert alert-danger extra-small m-3 py-2 px-3"><i class="bi bi-exclamation-triangle me-1"></i> <strong>Lỗi SQL:</strong> ${escapeHtml(data.error || "Không thể thực thi câu lệnh")}</div>`;
        }
    } catch (err) {
        if (statusEl) statusEl.textContent = `Lỗi kết nối`;
        resultBox.innerHTML = `<div class="alert alert-danger extra-small m-3 py-2 px-3"><i class="bi bi-wifi-off me-1"></i> <strong>Lỗi mạng:</strong> ${escapeHtml(err.message)}</div>`;
    }
}

function refreshSqliteTables() {
    if (currentSqliteDbPath) {
        loadSqliteTables(currentSqliteDbPath);
    }
}

function openAppLivePreview() {
    if (!currentLiveAppPort) {
        showToastNotification("Chưa có ứng dụng nào đang chạy.");
        return;
    }
    const mContainer = document.getElementById("monacoContainer");
    const diffContainer = document.getElementById("monacoDiffContainer");
    const sqliteContainer = document.getElementById("sqliteStudioContainer");
    const liveAppContainer = document.getElementById("liveAppPreviewContainer");
    const iframe = document.getElementById("liveAppIframe");
    const urlInput = document.getElementById("liveAppUrlInput");
    const link = document.getElementById("liveAppExternalLink");

    if (mContainer) mContainer.style.display = "none";
    if (diffContainer) diffContainer.style.display = "none";
    if (sqliteContainer) sqliteContainer.style.display = "none";
    if (liveAppContainer) liveAppContainer.style.display = "flex";

    const uid = localStorage.getItem("user_id") || "1";
    const proxyUrl = `/ide-preview/${currentLiveAppPort}/?user_id=${encodeURIComponent(uid)}`;
    if (iframe && iframe.src !== window.location.origin + proxyUrl) {
        iframe.src = proxyUrl;
    }
    if (urlInput) urlInput.value = proxyUrl;
    if (link) link.href = proxyUrl;
}

function closeLiveAppPreview() {
    const liveAppContainer = document.getElementById("liveAppPreviewContainer");
    if (liveAppContainer) liveAppContainer.style.display = "none";
    const sqliteContainer = document.getElementById("sqliteStudioContainer");
    const mContainer = document.getElementById("monacoContainer");

    if (activeFilePath && (activeFilePath.endsWith('.db') || activeFilePath.endsWith('.sqlite'))) {
        if (sqliteContainer) sqliteContainer.style.display = "flex";
    } else if (mContainer) {
        mContainer.style.display = "block";
        monacoEditor?.layout();
    }
}

function reloadLiveAppIframe() {
    const iframe = document.getElementById("liveAppIframe");
    if (iframe && currentLiveAppPort) {
        const uid = localStorage.getItem("user_id") || "1";
        iframe.src = `/ide-preview/${currentLiveAppPort}/?user_id=${encodeURIComponent(uid)}&_t=${Date.now()}`;
    }
}

async function stopRunningApp(targetPort) {
    let portToStop = targetPort || currentLiveAppPort;
    if (!portToStop) {
        const portEl = document.getElementById("tabLiveAppPort");
        if (portEl && portEl.textContent) {
            const m = portEl.textContent.match(/:?(\d+)/);
            if (m) portToStop = parseInt(m[1]);
        }
    }

    try {
        if (portToStop) {
            await fetch('/api/ide/ports/stop', {
                method: 'POST',
                headers: getApiHeaders(),
                body: JSON.stringify({ port: portToStop })
            });
        }
    } catch (err) {
        console.warn("Error calling stop-port API:", err);
    }

    if (terminalHubConn && terminalHubConn.state === signalR.HubConnectionState.Connected) {
        try {
            await terminalHubConn.invoke("KillRunningProcess");
        } catch (e) {
            console.error("Stop terminal error:", e);
        }
    }

    currentLiveAppPort = null;
    const badge = document.getElementById("tabLiveAppBadge");
    if (badge) badge.classList.add("d-none");
    const btnOpen = document.getElementById("tabBtnOpenApp");
    if (btnOpen) btnOpen.classList.add("d-none");
    const btnStop = document.getElementById("tabBtnStopApp");
    if (btnStop) btnStop.classList.add("d-none");
    closeLiveAppPreview();
    showToastNotification(portToStop ? `🛑 Đã dừng ứng dụng trên cổng :${portToStop}.` : "Đã gửi tín hiệu dừng ứng dụng.");
    if (typeof loadActivePorts === "function") {
        setTimeout(loadActivePorts, 300);
    }
}

// Initial Boot
window.addEventListener('DOMContentLoaded', () => {
    initMonacoEditors();
    initDraggableSplitters();
    initGlobalShortcuts();
    initXtermTerminal();

    // Ctrl + Enter shortcut for SQLite Query input
    const sqlInput = document.getElementById("sqliteSqlInput");
    if (sqlInput) {
        sqlInput.addEventListener("keydown", (e) => {
            if (e.ctrlKey && e.key === "Enter") {
                e.preventDefault();
                executeSqliteQuery();
            }
        });
    }
});

// Mobile IDE Tab Switcher
window.switchMobileIdeTab = function(tabName) {
    const container = document.querySelector('.vscode-main-container');
    if (container) {
        container.setAttribute('data-mobile-tab', tabName);
    }
    document.querySelectorAll('.mobile-tab-btn').forEach(b => b.classList.remove('active'));
    const btn = document.getElementById(
        tabName === 'explorer' ? 'mTabBtnExplorer' :
        tabName === 'editor' ? 'mTabBtnEditor' : 'mTabBtnAgent'
    );
    if (btn) btn.classList.add('active');

    if (tabName === 'editor') {
        setTimeout(() => {
            if (monacoEditor && typeof monacoEditor.layout === 'function') monacoEditor.layout();
            if (window.fitAddon && typeof window.fitAddon.fit === 'function') window.fitAddon.fit();
        }, 150);
    }
};
