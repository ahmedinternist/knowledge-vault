const { app, BrowserWindow, dialog, ipcMain, shell, powerSaveBlocker, protocol, net } = require("electron");
const fs = require("fs");
const path = require("path");
const { pathToFileURL } = require("url");
const { execFile, spawn } = require("child_process");

// Covers are served as local URLs rather than embedded Base64 strings.  This
// keeps the library IPC payload small and lets Chromium release decoded images.
protocol.registerSchemesAsPrivileged([{ scheme: "kv-asset", privileges: { standard: true, secure: true, supportFetchAPI: true } }]);

let mainWindow;
let sleepBlockerId;
function indexerPath() { return app.isPackaged ? path.join(process.resourcesPath, "pdf-indexer", "PdfIndexer.exe") : path.join(__dirname, "backend", "pdf-indexer", "PdfIndexer.exe"); }
function libraryPath() { return path.join(app.getPath("appData"), "PDFLibraryManager", "library.db"); }
function runIndexer(...args) {
  return new Promise((resolve, reject) => {
    const executable = indexerPath();
    if (!fs.existsSync(executable)) return reject(new Error(`PDF indexer is missing: ${executable}`));
    execFile(executable, args, { windowsHide: true, maxBuffer: 64 * 1024 * 1024 }, (error, stdout, stderr) => error ? reject(new Error(stderr.trim() || error.message)) : resolve(stdout.trim()));
  });
}
function runRescanWithProgress(sender) {
  return new Promise((resolve, reject) => {
    const executable = indexerPath();
    if (!fs.existsSync(executable)) return reject(new Error(`PDF indexer is missing: ${executable}`));
    const child = spawn(executable, ["rescan-progress"], { windowsHide: true });
    let stderr = "", buffer = "";
    const handleLine = line => { if (!line.trim()) return; try { const update = JSON.parse(line); sender.send("library:scan-progress", update); } catch { /* Ignore malformed progress output; the process result remains authoritative. */ } };
    child.stdout.on("data", data => { buffer += data.toString(); const lines = buffer.split(/\r?\n/); buffer = lines.pop(); lines.forEach(handleLine); });
    child.stderr.on("data", data => { stderr += data.toString(); });
    child.on("error", error => reject(error));
    child.on("close", code => { if (buffer) handleLine(buffer); if (code === 0) resolve(true); else reject(new Error(stderr.trim() || `Rescan stopped with exit code ${code}.`)); });
  });
}
function localAssetUrl(filePath) {
  return `kv-asset://local/${Buffer.from(filePath).toString("base64url")}`;
}
function registerAssetProtocol() {
  protocol.handle("kv-asset", request => {
    try {
      const token = new URL(request.url).pathname.slice(1);
      const filePath = Buffer.from(token, "base64url").toString("utf8");
      if (!filePath || !fs.existsSync(filePath)) return new Response("Not found", { status: 404 });
      // The cache budget keeps recently displayed covers first.
      try { const stat = fs.statSync(filePath); fs.utimesSync(filePath, new Date(), stat.mtime); } catch { /* Loading the cover is still safe if its timestamp cannot be updated. */ }
      return net.fetch(pathToFileURL(filePath).href);
    } catch {
      return new Response("Not found", { status: 404 });
    }
  });
}
function toEntry(record) {
  const imagePath = record.CoverImagePath && fs.existsSync(record.CoverImagePath) ? record.CoverImagePath : record.ThumbnailPath;
  const thumbnail = imagePath && fs.existsSync(imagePath) ? localAssetUrl(imagePath) : "";
  return { path: record.Path, title: record.Title, name: record.Title, type: "PDF", parent: path.basename(path.dirname(record.Path)), extension: ".pdf", size: record.Size, created: Date.parse(record.CreatedAt) || 0, modified: Date.parse(record.ModifiedAt) || 0, hash: record.Hash || "", author: record.Author || "", subject: record.Subject || "", keywords: record.Keywords || "", pages: record.Pages, thumbnail, thumbnailMissing: Boolean(record.ThumbnailPath) && !thumbnail, coverImagePath: record.CoverImagePath || "", duplicateOf: record.DuplicateOf || "" };
}
async function entries(command = "list", ...args) { return JSON.parse(await runIndexer(command, ...args) || "[]").map(toEntry); }
async function entriesPage(command, request) { const page = JSON.parse(await runIndexer(command, JSON.stringify(request || {})) || "{}"); return { items: (page.Items || []).map(toEntry), total: Number(page.Total || 0) }; }
function collectionEntry(record) { const image = record.ImagePath && fs.existsSync(record.ImagePath) ? localAssetUrl(record.ImagePath) : ""; return { ...record, image }; }
function thumbnailDirectory() { return path.join(path.dirname(libraryPath()), "thumbnails"); }
function chromeExecutable() {
  const candidates = [
    process.env.PROGRAMFILES && path.join(process.env.PROGRAMFILES, "Google", "Chrome", "Application", "chrome.exe"),
    process.env["PROGRAMFILES(X86)"] && path.join(process.env["PROGRAMFILES(X86)"], "Google", "Chrome", "Application", "chrome.exe"),
    // Electron does not expose a `localAppData` getPath key.  Read the normal
    // Windows environment variable instead so this lookup is safe from IPC.
    process.env.LOCALAPPDATA && path.join(process.env.LOCALAPPDATA, "Google", "Chrome", "Application", "chrome.exe")
  ].filter(Boolean);
  return candidates.find(candidate => fs.existsSync(candidate)) || "";
}
async function openImageSearch(query) {
  const url = `https://www.google.com/search?tbm=isch&hl=en&q=${encodeURIComponent(String(query || "PDF book cover"))}`;
  const chrome = chromeExecutable();
  if (!chrome) return shell.openExternal(url);
  try {
    const child = spawn(chrome, ["--new-tab", url], { detached: true, stdio: "ignore", windowsHide: true });
    child.once("error", () => shell.openExternal(url));
    child.unref();
    return true;
  } catch {
    return shell.openExternal(url);
  }
}
async function cacheInfo() { const directory = thumbnailDirectory(); const thumbnails = fs.existsSync(directory) ? fs.readdirSync(directory).filter(name => /\.(png|webp|jpe?g)$/i.test(name)) : []; const thumbnailBytes = thumbnails.reduce((total, name) => total + (fs.statSync(path.join(directory, name)).size || 0), 0); return { databaseBytes: fs.existsSync(libraryPath()) ? fs.statSync(libraryPath()).size : 0, thumbnailCount: thumbnails.length, thumbnailBytes }; }
async function cleanThumbnailCache() { const directory = thumbnailDirectory(); if (!fs.existsSync(directory)) return 0; const records = [...JSON.parse(await runIndexer("list") || "[]"), ...JSON.parse(await runIndexer("trash") || "[]")]; const activeHashes = new Set(records.map(record => String(record.Hash || "").toLowerCase()).filter(Boolean)); let removed = 0; for (const name of fs.readdirSync(directory)) { const match = /^([a-f0-9]{64})\.(?:png|webp|jpe?g)$/i.exec(name); if (match && !activeHashes.has(match[1].toLowerCase())) { fs.unlinkSync(path.join(directory, name)); removed++; } } return removed; }
function createWindow() {
  mainWindow = new BrowserWindow({ width: 1400, height: 900, minWidth: 980, minHeight: 680, resizable: true, autoHideMenuBar: true, frame: false, transparent: true, backgroundMaterial: "acrylic", backgroundColor: "#00000000", webPreferences: { preload: path.join(__dirname, "electron-preload.cjs"), contextIsolation: true, nodeIntegration: false } });
  const rendererLog = path.join(app.getPath("userData"), "renderer-errors.log");
  mainWindow.webContents.on("console-message", (_event, level, message, line, sourceId) => { if (level >= 2) fs.appendFileSync(rendererLog, `${new Date().toISOString()} ${sourceId}:${line} ${message}\n`); });
  mainWindow.webContents.on("did-fail-load", (_event, code, description, url) => fs.appendFileSync(rendererLog, `${new Date().toISOString()} LOAD ${code} ${description} ${url}\n`));
  mainWindow.loadFile(path.join(__dirname, "index.html")).catch(error => dialog.showErrorBox("Knowledge Vault could not start", error.message));
  mainWindow.on("maximize", () => mainWindow?.webContents.send("window:state", { maximized: true }));
  mainWindow.on("unmaximize", () => mainWindow?.webContents.send("window:state", { maximized: false }));
  mainWindow.on("closed", () => { mainWindow = null; });
}
app.whenReady().then(() => {
  registerAssetProtocol();
  ipcMain.handle("library:list", () => entries());
  ipcMain.handle("library:list-page", (_event, request) => entriesPage("list-page", request));
  ipcMain.handle("library:search-page", (_event, request) => entriesPage("search-page", request));
  ipcMain.handle("library:title-suggestions", async () => JSON.parse(await runIndexer("titles") || "[]"));
  ipcMain.handle("library:ensure-thumbnail", async (_event, filePath) => { const thumbnail = await runIndexer("thumbnail", filePath); return thumbnail && fs.existsSync(thumbnail) ? localAssetUrl(thumbnail) : ""; });
  ipcMain.handle("library:list-trash", () => entries("trash"));
  ipcMain.handle("library:list-sources", async () => JSON.parse(await runIndexer("sources") || "[]"));
  ipcMain.handle("library:list-exclusions", async () => JSON.parse(await runIndexer("exclusions") || "[]"));
  ipcMain.handle("library:source-stats", async () => JSON.parse(await runIndexer("source-stats") || "[]"));
  ipcMain.handle("library:scan-log", async () => JSON.parse(await runIndexer("scan-log") || "[]"));
  ipcMain.handle("library:search", (_event, query) => entries("search", query));
  ipcMain.handle("library:update-meta", async (_event, update) => { await runIndexer("update-meta", JSON.stringify({ Path: update.path, Title: update.title, Author: update.author, Subject: update.subject, Keywords: update.keywords, Pages: update.pages, CreatedAt: update.createdAt, ModifiedAt: update.modifiedAt })); return true; });
  ipcMain.handle("library:update-author", async (_event, update) => Number(await runIndexer("update-author", JSON.stringify({ CurrentAuthor: update.currentAuthor, NewAuthor: update.newAuthor }))) || 0);
  ipcMain.handle("library:update-cover", (_event, update) => runIndexer("update-cover", JSON.stringify({ Path: update.path, CoverImagePath: update.coverImagePath })));
  ipcMain.handle("library:collections", async () => JSON.parse(await runIndexer("collections") || "[]").map(collectionEntry));
  ipcMain.handle("library:create-collection", (_event, name) => runIndexer("create-collection", name));
  ipcMain.handle("library:add-to-collection", (_event, name, filePath) => runIndexer("add-to-collection", name, filePath));
  ipcMain.handle("library:remove-from-collection", (_event, name, filePath) => runIndexer("remove-from-collection", name, filePath));
  ipcMain.handle("library:document-collections", async (_event, filePath) => JSON.parse(await runIndexer("document-collections", filePath) || "[]"));
  ipcMain.handle("library:rename-collection", (_event, change) => runIndexer("rename-collection", JSON.stringify({ OldName: change.oldName, NewName: change.newName })));
  ipcMain.handle("library:delete-collection", (_event, name) => runIndexer("delete-collection", name));
  ipcMain.handle("library:reorder-collections", (_event, names) => runIndexer("reorder-collections", JSON.stringify(names)));
  ipcMain.handle("library:collection-documents", (_event, name) => entries("collection-documents", name));
  ipcMain.handle("library:collection-page", (_event, request) => entriesPage("collection-page", request));
  ipcMain.handle("library:pick-collection-image", async () => { const result = await dialog.showOpenDialog({ title: "Choose collection picture", properties: ["openFile"], filters: [{ name: "Images", extensions: ["png", "jpg", "jpeg", "webp"] }] }); return result.canceled ? "" : result.filePaths[0]; });
  ipcMain.handle("library:pick-cover-image", async () => { const result = await dialog.showOpenDialog({ title: "Choose PDF cover image", properties: ["openFile"], filters: [{ name: "Images", extensions: ["png", "jpg", "jpeg", "webp"] }] }); return result.canceled ? "" : result.filePaths[0]; });
  ipcMain.handle("library:search-cover", (_event, query) => openImageSearch(query));
  ipcMain.handle("library:update-collection", (_event, appearance) => runIndexer("update-collection", JSON.stringify({ Name: appearance.name, Icon: appearance.icon, ImagePath: appearance.imagePath })));
  ipcMain.handle("library:document-tags", async (_event, filePath) => JSON.parse(await runIndexer("document-tags", filePath) || "[]"));
  ipcMain.handle("library:set-document-tags", (_event, update) => runIndexer("set-document-tags", JSON.stringify({ Path: update.path, Tags: update.tags })));
  ipcMain.handle("library:add-files", async () => { const result = await dialog.showOpenDialog({ title: "Add PDFs to Knowledge Vault", properties: ["openFile", "multiSelections"], filters: [{ name: "PDF documents", extensions: ["pdf"] }] }); if (!result.canceled) for (const filePath of result.filePaths) await runIndexer("add-file", filePath); return true; });
  ipcMain.handle("library:add-dropped-paths", async (_event, droppedPaths) => { const paths = [...new Set(Array.isArray(droppedPaths) ? droppedPaths.filter(value => typeof value === "string" && value) : [])]; for (const droppedPath of paths) { try { const info = fs.statSync(droppedPath); if (info.isDirectory()) await runIndexer("add-folder", droppedPath); else if (info.isFile() && path.extname(droppedPath).toLowerCase() === ".pdf") await runIndexer("add-file", droppedPath); } catch { /* Ignore files removed while being dropped. */ } } return true; });
  ipcMain.handle("library:add-folder", async () => { const result = await dialog.showOpenDialog({ title: "Add a folder to Knowledge Vault", properties: ["openDirectory"] }); if (!result.canceled) await runIndexer("add-folder", result.filePaths[0]); return true; });
  ipcMain.handle("library:exclude-folder", async () => { const result = await dialog.showOpenDialog({ title: "Exclude a folder from scanning", properties: ["openDirectory"] }); if (!result.canceled) await runIndexer("exclude", result.filePaths[0]); return true; });
  ipcMain.handle("library:remove-exclusion", (_event, excludedPath) => runIndexer("remove-exclusion", excludedPath));
  ipcMain.handle("library:set-source-scan-mode", (_event, update) => runIndexer("set-source-scan-mode", JSON.stringify({ Path: update.path, ScanSubfolders: Boolean(update.scanSubfolders) })));
  ipcMain.handle("library:remove-source", (_event, folderPath) => runIndexer("remove-source", folderPath));
  ipcMain.handle("library:remove-record", (_event, filePath) => runIndexer("remove-record", filePath));
  ipcMain.handle("library:rescan", async event => { await runRescanWithProgress(event.sender); return true; });
  ipcMain.handle("library:remove-missing", async () => Number(await runIndexer("remove-missing")) || 0);
  ipcMain.handle("library:cache-info", cacheInfo);
  ipcMain.handle("library:diagnostics", async () => ({ mainProcess: await process.getProcessMemoryInfo(), cache: await cacheInfo(), pid: process.pid }));
  ipcMain.handle("library:clean-thumbnail-cache", cleanThumbnailCache);
  ipcMain.handle("library:trash", (_event, filePath) => runIndexer("trash-file", filePath));
  ipcMain.handle("library:restore", (_event, filePath) => runIndexer("restore-file", filePath));
  ipcMain.handle("library:empty-trash", async () => Number(await runIndexer("empty-trash")) || 0);
  ipcMain.handle("library:open-file", async (_event, filePath) => fs.existsSync(filePath) ? (await shell.openPath(filePath) || "") : "This file no longer exists. Use Remove missing in Settings to clean it up.");
  ipcMain.handle("library:open-folder", async (_event, folderPath) => fs.existsSync(folderPath) ? (await shell.openPath(folderPath) || "") : "This folder no longer exists.");
  ipcMain.handle("system:open-default-apps", () => shell.openExternal("ms-settings:defaultapps"));
  ipcMain.handle("window:fullscreen", (_event, enabled) => { mainWindow?.setFullScreen(Boolean(enabled)); return mainWindow?.isFullScreen() || false; });
  ipcMain.handle("window:minimize", () => { mainWindow?.minimize(); return true; });
  ipcMain.handle("window:toggle-maximize", () => { if (!mainWindow) return false; if (mainWindow.isMaximized()) mainWindow.unmaximize(); else mainWindow.maximize(); return mainWindow.isMaximized(); });
  ipcMain.handle("window:close", () => { mainWindow?.close(); return true; });
  ipcMain.handle("window:is-maximized", () => mainWindow?.isMaximized() || false);
  ipcMain.handle("window:screen-on", (_event, enabled) => { if (enabled && !sleepBlockerId) sleepBlockerId = powerSaveBlocker.start("prevent-display-sleep"); if (!enabled && sleepBlockerId) { powerSaveBlocker.stop(sleepBlockerId); sleepBlockerId = undefined; } return Boolean(sleepBlockerId); });
  ipcMain.handle("library:backup", async (_event, clientState = {}) => { const result = await dialog.showSaveDialog({ title: "Back up Knowledge Vault library", defaultPath: "KnowledgeVault-backup.kvbackup", filters: [{ name: "Knowledge Vault backup", extensions: ["kvbackup"] }] }); if (result.canceled || !result.filePath) return false; if (!fs.existsSync(libraryPath())) await runIndexer("list"); const thumbnailDir = path.join(path.dirname(libraryPath()), "thumbnails"); const thumbnails = fs.existsSync(thumbnailDir) ? fs.readdirSync(thumbnailDir).filter(name => /^[a-f0-9]{64}\.(?:png|webp|jpe?g)$/i.test(name)).map(name => ({ name, data: fs.readFileSync(path.join(thumbnailDir, name)).toString("base64") })) : []; const backup = { version: 1, createdAt: new Date().toISOString(), database: fs.readFileSync(libraryPath()).toString("base64"), thumbnails, clientState }; fs.writeFileSync(result.filePath, JSON.stringify(backup)); return true; });
  ipcMain.handle("library:restore-backup", async () => { const result = await dialog.showOpenDialog({ title: "Restore Knowledge Vault library", properties: ["openFile"], filters: [{ name: "Knowledge Vault backup", extensions: ["kvbackup"] }] }); if (result.canceled || !result.filePaths[0]) return false; const backup = JSON.parse(fs.readFileSync(result.filePaths[0], "utf8")); if (!backup.database) throw new Error("This is not a valid Knowledge Vault backup."); const dataDir = path.dirname(libraryPath()); fs.mkdirSync(dataDir, { recursive: true }); fs.writeFileSync(libraryPath(), Buffer.from(backup.database, "base64")); const thumbnailDir = path.join(dataDir, "thumbnails"); fs.mkdirSync(thumbnailDir, { recursive: true }); for (const thumbnail of backup.thumbnails || []) { if (/^[a-f0-9]{64}\.(?:png|webp|jpe?g)$/i.test(thumbnail.name) && typeof thumbnail.data === "string") fs.writeFileSync(path.join(thumbnailDir, thumbnail.name), Buffer.from(thumbnail.data, "base64")); } return backup.clientState || {}; });
  createWindow(); app.on("activate", () => { if (!BrowserWindow.getAllWindows().length) createWindow(); });
});
app.on("window-all-closed", () => { if (process.platform !== "darwin") app.quit(); });
