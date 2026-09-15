using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using UglyToad.PdfPig;
using PDFtoImage;

var appData = GetDataDirectory();
Directory.CreateDirectory(appData);
Directory.CreateDirectory(Path.Combine(appData, "thumbnails"));
var databasePath = Path.Combine(appData, "library.db");
var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();

using var database = new SqliteConnection(connectionString);
database.Open();
Initialize(database);

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "list";
var argument = args.Skip(1).FirstOrDefault();
switch (command)
{
    case "add-file" when !string.IsNullOrWhiteSpace(argument):
        AddSource(database, argument, "file");
        Console.WriteLine(JsonSerializer.Serialize(new { indexed = await Rescan(database) }));
        break;
    case "add-folder" when !string.IsNullOrWhiteSpace(argument):
        AddSource(database, argument, "folder");
        Console.WriteLine(JsonSerializer.Serialize(new { indexed = await Rescan(database) }));
        break;
    case "exclude" when !string.IsNullOrWhiteSpace(argument):
        AddExclusion(database, argument);
        Console.WriteLine("true");
        break;
    case "exclusions":
        Console.WriteLine(JsonSerializer.Serialize(GetExclusions(database)));
        break;
    case "remove-exclusion" when !string.IsNullOrWhiteSpace(argument):
        RemoveExclusion(database, argument);
        Console.WriteLine("true");
        break;
    case "rescan":
        Console.WriteLine(JsonSerializer.Serialize(new { indexed = await Rescan(database) }));
        break;
    case "rescan-progress":
        var scanned = await Rescan(database, progress => Console.WriteLine(JsonSerializer.Serialize(progress)));
        Console.WriteLine(JsonSerializer.Serialize(new ScanProgress("complete", "", "", scanned)));
        break;
    case "remove-missing":
        Console.WriteLine(RemoveMissingSources(database));
        break;
    case "sources":
        Console.WriteLine(JsonSerializer.Serialize(GetSources(database).Select(source => new { source.Path, source.Kind, source.ScanSubfolders })));
        break;
    case "set-source-scan-mode" when !string.IsNullOrWhiteSpace(argument):
        SetSourceScanMode(database, JsonSerializer.Deserialize<SourceScanUpdate>(argument) ?? throw new ArgumentException("Invalid source scan update."));
        Console.WriteLine("true");
        break;
    case "remove-source" when !string.IsNullOrWhiteSpace(argument):
        RemoveFolderSource(database, argument);
        Console.WriteLine("true");
        break;
    case "remove-record" when !string.IsNullOrWhiteSpace(argument):
        RemoveLibraryRecord(database, argument);
        Console.WriteLine("true");
        break;
    case "trash":
        Console.WriteLine(JsonSerializer.Serialize(ListDocuments(database, true)));
        break;
    case "list-page":
        Console.WriteLine(JsonSerializer.Serialize(ListDocumentPage(database, JsonSerializer.Deserialize<PageRequest>(argument ?? "{}") ?? new PageRequest())));
        break;
    case "trash-file" when !string.IsNullOrWhiteSpace(argument):
        SetTrashState(database, argument, true);
        Console.WriteLine("true");
        break;
    case "restore-file" when !string.IsNullOrWhiteSpace(argument):
        SetTrashState(database, argument, false);
        Console.WriteLine("true");
        break;
    case "empty-trash":
        Console.WriteLine(EmptyTrash(database));
        break;
    case "search" when !string.IsNullOrWhiteSpace(argument):
        Console.WriteLine(JsonSerializer.Serialize(SearchDocuments(database, argument)));
        break;
    case "search-page":
        Console.WriteLine(JsonSerializer.Serialize(SearchDocumentPage(database, JsonSerializer.Deserialize<PageRequest>(argument ?? "{}") ?? new PageRequest())));
        break;
    case "titles":
        Console.WriteLine(JsonSerializer.Serialize(GetTitleSuggestions(database)));
        break;
    case "thumbnail" when !string.IsNullOrWhiteSpace(argument):
        Console.WriteLine(await EnsureThumbnail(database, argument));
        break;
    case "update-meta" when !string.IsNullOrWhiteSpace(argument):
        UpdateMetadata(database, JsonSerializer.Deserialize<MetadataUpdate>(argument) ?? throw new ArgumentException("Invalid metadata update."));
        Console.WriteLine("true");
        break;
    case "update-author" when !string.IsNullOrWhiteSpace(argument):
        Console.WriteLine(UpdateAuthorGroup(database, JsonSerializer.Deserialize<AuthorUpdate>(argument) ?? throw new ArgumentException("Invalid author update.")));
        break;
    case "update-cover" when !string.IsNullOrWhiteSpace(argument):
        UpdateDocumentCover(database, JsonSerializer.Deserialize<DocumentCoverUpdate>(argument) ?? throw new ArgumentException("Invalid cover update."));
        Console.WriteLine("true");
        break;
    case "source-stats":
        Console.WriteLine(JsonSerializer.Serialize(GetSourceStats(database)));
        break;
    case "scan-log":
        Console.WriteLine(JsonSerializer.Serialize(GetScanLog(database)));
        break;
    case "collections":
        Console.WriteLine(JsonSerializer.Serialize(GetCollections(database)));
        break;
    case "create-collection" when !string.IsNullOrWhiteSpace(argument):
        CreateCollection(database, argument);
        Console.WriteLine("true");
        break;
    case "add-to-collection" when args.Length >= 3:
        AddToCollection(database, args[1], args[2]);
        Console.WriteLine("true");
        break;
    case "remove-from-collection" when args.Length >= 3:
        RemoveFromCollection(database, args[1], args[2]);
        Console.WriteLine("true");
        break;
    case "document-collections" when !string.IsNullOrWhiteSpace(argument):
        Console.WriteLine(JsonSerializer.Serialize(GetDocumentCollections(database, argument)));
        break;
    case "rename-collection" when !string.IsNullOrWhiteSpace(argument):
        RenameCollection(database, JsonSerializer.Deserialize<CollectionRename>(argument) ?? throw new ArgumentException("Invalid collection rename."));
        Console.WriteLine("true");
        break;
    case "delete-collection" when !string.IsNullOrWhiteSpace(argument):
        DeleteCollection(database, argument);
        Console.WriteLine("true");
        break;
    case "reorder-collections" when !string.IsNullOrWhiteSpace(argument):
        ReorderCollections(database, JsonSerializer.Deserialize<List<string>>(argument) ?? throw new ArgumentException("Invalid collection order."));
        Console.WriteLine("true");
        break;
    case "collection-documents" when !string.IsNullOrWhiteSpace(argument):
        Console.WriteLine(JsonSerializer.Serialize(GetCollectionDocuments(database, argument)));
        break;
    case "collection-page" when !string.IsNullOrWhiteSpace(argument):
        Console.WriteLine(JsonSerializer.Serialize(GetCollectionDocumentPage(database, JsonSerializer.Deserialize<NamedPageRequest>(argument) ?? throw new ArgumentException("Invalid collection page request."))));
        break;
    case "update-collection" when !string.IsNullOrWhiteSpace(argument):
        UpdateCollectionAppearance(database, JsonSerializer.Deserialize<CollectionAppearance>(argument) ?? throw new ArgumentException("Invalid collection appearance."));
        Console.WriteLine("true");
        break;
    case "document-tags" when !string.IsNullOrWhiteSpace(argument):
        Console.WriteLine(JsonSerializer.Serialize(GetDocumentTags(database, argument)));
        break;
    case "set-document-tags" when !string.IsNullOrWhiteSpace(argument):
        SetDocumentTags(database, JsonSerializer.Deserialize<DocumentTagsUpdate>(argument) ?? throw new ArgumentException("Invalid tag update."));
        Console.WriteLine("true");
        break;
    case "smart-collections":
        Console.WriteLine(JsonSerializer.Serialize(GetSmartCollections(database)));
        break;
    case "create-smart-collection" when !string.IsNullOrWhiteSpace(argument):
        CreateSmartCollection(database, JsonSerializer.Deserialize<SmartCollectionInfo>(argument) ?? throw new ArgumentException("Invalid smart collection."));
        Console.WriteLine("true");
        break;
    case "delete-smart-collection" when !string.IsNullOrWhiteSpace(argument):
        DeleteSmartCollection(database, argument);
        Console.WriteLine("true");
        break;
    case "smart-collection-documents" when !string.IsNullOrWhiteSpace(argument):
        Console.WriteLine(JsonSerializer.Serialize(GetSmartCollectionDocuments(database, argument)));
        break;
    default:
        Console.WriteLine(JsonSerializer.Serialize(ListDocuments(database)));
        break;
}

static void Initialize(SqliteConnection db)
{
    if (TableExists(db, "documents") && !ColumnExists(db, "documents", "path"))
    {
        // Keep incompatible preview data for recovery instead of destroying it.
        // The current cache will rebuild from the configured source folders.
        Execute(db, $"ALTER TABLE documents RENAME TO documents_legacy_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
    }

    Execute(db, """
        CREATE TABLE IF NOT EXISTS sources (path TEXT PRIMARY KEY, kind TEXT NOT NULL, added_at TEXT NOT NULL, scan_subfolders INTEGER NOT NULL DEFAULT 1);
        CREATE TABLE IF NOT EXISTS exclusions (path TEXT PRIMARY KEY);
        CREATE TABLE IF NOT EXISTS ignored_files (path TEXT PRIMARY KEY);
        CREATE TABLE IF NOT EXISTS documents (
          path TEXT PRIMARY KEY, hash TEXT NOT NULL, size INTEGER NOT NULL, created_at TEXT NOT NULL, modified_at TEXT NOT NULL,
          title TEXT NOT NULL, author TEXT, subject TEXT, keywords TEXT, pages INTEGER NOT NULL, thumbnail_path TEXT, cover_image_path TEXT,
          duplicate_of TEXT, indexed_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS collections (name TEXT PRIMARY KEY, created_at TEXT NOT NULL, position INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS collection_documents (collection_name TEXT NOT NULL, document_path TEXT NOT NULL, PRIMARY KEY(collection_name, document_path));
        CREATE TABLE IF NOT EXISTS document_tags (document_path TEXT NOT NULL, tag TEXT NOT NULL, PRIMARY KEY(document_path, tag));
        CREATE TABLE IF NOT EXISTS smart_collections (name TEXT PRIMARY KEY, rule_type TEXT NOT NULL, rule_value TEXT, created_at TEXT NOT NULL, position INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS scan_log (id INTEGER PRIMARY KEY AUTOINCREMENT, path TEXT, level TEXT, message TEXT, created_at TEXT NOT NULL);
      """);

    // Older preview builds created a smaller documents table. Upgrade it in place
    // so a pre-existing library database never prevents the executable from starting.
    EnsureColumn(db, "documents", "hash", "TEXT NOT NULL DEFAULT ''");
    EnsureColumn(db, "documents", "size", "INTEGER NOT NULL DEFAULT 0");
    EnsureColumn(db, "documents", "created_at", "TEXT NOT NULL DEFAULT ''");
    EnsureColumn(db, "documents", "modified_at", "TEXT NOT NULL DEFAULT ''");
    EnsureColumn(db, "documents", "title", "TEXT NOT NULL DEFAULT ''");
    EnsureColumn(db, "documents", "author", "TEXT");
    EnsureColumn(db, "documents", "subject", "TEXT");
    EnsureColumn(db, "documents", "keywords", "TEXT");
    EnsureColumn(db, "documents", "pages", "INTEGER NOT NULL DEFAULT 0");
    EnsureColumn(db, "documents", "thumbnail_path", "TEXT");
    EnsureColumn(db, "documents", "cover_image_path", "TEXT");
    EnsureColumn(db, "documents", "duplicate_of", "TEXT");
    EnsureColumn(db, "documents", "indexed_at", "TEXT NOT NULL DEFAULT ''");
    EnsureColumn(db, "documents", "is_deleted", "INTEGER NOT NULL DEFAULT 0");
    EnsureColumn(db, "documents", "trashed_at", "TEXT");
    EnsureColumn(db, "documents", "content_text", "TEXT");
    EnsureColumn(db, "documents", "manual_metadata", "INTEGER NOT NULL DEFAULT 0");
    EnsureColumn(db, "sources", "last_scanned_at", "TEXT");
    EnsureColumn(db, "sources", "scan_subfolders", "INTEGER NOT NULL DEFAULT 1");
    // Libraries created by earlier builds may already have this table without its timestamp.
    // Upgrade it in place so existing collections remain usable.
    EnsureColumn(db, "collections", "created_at", "TEXT NOT NULL DEFAULT ''");
    EnsureColumn(db, "collections", "position", "INTEGER NOT NULL DEFAULT 0");
    EnsureColumn(db, "collections", "icon", "TEXT");
    EnsureColumn(db, "collections", "image_path", "TEXT");
    Execute(db, "CREATE INDEX IF NOT EXISTS ix_documents_hash ON documents(hash)");
}

static void EnsureColumn(SqliteConnection db, string table, string column, string definition)
{
    if (ColumnExists(db, table, column)) return;
    Execute(db, $"ALTER TABLE {table} ADD COLUMN {column} {definition}");
}

static bool TableExists(SqliteConnection db, string table)
{
    using var command = db.CreateCommand();
    command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table COLLATE NOCASE LIMIT 1";
    command.Parameters.AddWithValue("$table", table);
    return command.ExecuteScalar() is not null;
}

static bool ColumnExists(SqliteConnection db, string table, string column)
{
    using var command = db.CreateCommand();
    command.CommandText = $"PRAGMA table_info({table})";
    using var reader = command.ExecuteReader();
    while (reader.Read()) if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
    return false;
}

static void AddSource(SqliteConnection db, string path, string kind)
{
    if ((kind == "file" && !File.Exists(path)) || (kind == "folder" && !Directory.Exists(path))) throw new FileNotFoundException("The selected path no longer exists.", path);
    using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT OR IGNORE INTO sources(path, kind, added_at, scan_subfolders) VALUES ($path, $kind, $now, 1)";
    cmd.Parameters.AddWithValue("$path", Path.GetFullPath(path)); cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
}

static void AddExclusion(SqliteConnection db, string path)
{
    using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT OR REPLACE INTO exclusions(path) VALUES ($path)"; cmd.Parameters.AddWithValue("$path", Path.GetFullPath(path)); cmd.ExecuteNonQuery();
}

static List<string> GetExclusions(SqliteConnection db) => QueryStrings(db, "SELECT path FROM exclusions ORDER BY path").OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
static void RemoveExclusion(SqliteConnection db, string path) => Execute(db, "DELETE FROM exclusions WHERE path=$path", ("$path", Path.GetFullPath(path)));
static void SetSourceScanMode(SqliteConnection db, SourceScanUpdate update)
{
    var path = Path.GetFullPath(update.Path);
    var kind = ScalarString(db, "SELECT kind FROM sources WHERE path=$path", ("$path", path));
    if (!string.Equals(kind, "folder", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Only folder sources can change subfolder scanning.");
    Execute(db, "UPDATE sources SET scan_subfolders=$scan WHERE path=$path", ("$scan", update.ScanSubfolders ? "1" : "0"), ("$path", path));
}
static void RemoveFolderSource(SqliteConnection db, string path)
{
    path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    if (path.Length == 2 && path[1] == Path.VolumeSeparatorChar) path += Path.DirectorySeparatorChar;
    var kind = ScalarString(db, "SELECT kind FROM sources WHERE path=$path", ("$path", path));
    if (!string.Equals(kind, "folder", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Only folder scan sources can be removed here.");
    var prefix = path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
    // Remove the scan source and every catalog record below it, including collection and tag links.
    Execute(db, "DELETE FROM collection_documents WHERE document_path=$path OR substr(document_path,1,length($prefix))=$prefix", ("$path", path), ("$prefix", prefix));
    Execute(db, "DELETE FROM document_tags WHERE document_path=$path OR substr(document_path,1,length($prefix))=$prefix", ("$path", path), ("$prefix", prefix));
    Execute(db, "DELETE FROM documents WHERE path=$path OR substr(path,1,length($prefix))=$prefix", ("$path", path), ("$prefix", prefix));
    Execute(db, "DELETE FROM sources WHERE path=$path OR substr(path,1,length($prefix))=$prefix", ("$path", path), ("$prefix", prefix));
}
static void RemoveLibraryRecord(SqliteConnection db, string path)
{
    path = Path.GetFullPath(path);
    using var tx = db.BeginTransaction();
    Execute(db, "INSERT OR REPLACE INTO ignored_files(path) VALUES($path)", ("$path", path));
    Execute(db, "DELETE FROM collection_documents WHERE document_path=$path", ("$path", path));
    Execute(db, "DELETE FROM document_tags WHERE document_path=$path", ("$path", path));
    Execute(db, "DELETE FROM documents WHERE path=$path", ("$path", path));
    Execute(db, "DELETE FROM sources WHERE path=$path AND kind='file'", ("$path", path));
    tx.Commit();
}
static async Task<int> Rescan(SqliteConnection db, Action<ScanProgress>? progress = null)
{
    var exclusions = QueryStrings(db, "SELECT path FROM exclusions");
    var processed = 0;
    foreach (var source in GetSources(db))
    {
        progress?.Invoke(CreateScanProgress("source", source.Path, source.Path, processed));
        if (source.Kind == "file")
        {
            progress?.Invoke(CreateScanProgress("file", source.Path, source.Path, processed));
            try { await IndexFile(db, source.Path, exclusions); }
            catch (Exception ex) { Log(db, source.Path, "warning", $"Scan continued after this PDF failed: {ex.Message}"); }
            processed++;
            await YieldScanMemory(processed);
        }
        else foreach (var file in EnumeratePdfFiles(source.Path, exclusions, source.ScanSubfolders))
        {
            progress?.Invoke(CreateScanProgress("file", source.Path, file, processed));
            try { await IndexFile(db, file, exclusions); }
            catch (Exception ex) { Log(db, file, "warning", $"Scan continued after this PDF failed: {ex.Message}"); }
            processed++;
            await YieldScanMemory(processed);
        }
        Execute(db, "UPDATE sources SET last_scanned_at = $now WHERE path = $path", ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$path", source.Path));
    }
    TrimThumbnailCache(350L * 1024 * 1024);
    return processed;
}

static async Task YieldScanMemory(int processed)
{
    // Indexing is deliberately single-file-at-a-time. Periodically release
    // disposed PDF/image buffers before continuing a long scan.
    if (processed == 0 || processed % 12 != 0) return;
    GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    await Task.Delay(12);
}

static ScanProgress CreateScanProgress(string type, string source, string path, int processed) => new(type, source, path, processed, Environment.WorkingSet, GC.GetTotalMemory(false));

static IEnumerable<string> EnumeratePdfFiles(string folder, HashSet<string> exclusions, bool scanSubfolders)
{
    if (!Directory.Exists(folder) || IsExcluded(folder, exclusions)) yield break;
    IEnumerator<string>? enumerator = null;
    try { enumerator = Directory.EnumerateFiles(folder, "*.pdf", scanSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).GetEnumerator(); }
    catch { yield break; }
    using (enumerator)
    while (enumerator.MoveNext()) if (!IsExcluded(enumerator.Current, exclusions)) yield return enumerator.Current;
}

static async Task IndexFile(SqliteConnection db, string path, HashSet<string> exclusions)
{
    if (!File.Exists(path) || IsExcluded(path, exclusions)) return;
    if (ScalarString(db, "SELECT path FROM ignored_files WHERE path=$path", ("$path", Path.GetFullPath(path))) is not null) return;
    try
    {
        var file = new FileInfo(path);
        var hash = Sha256(path);
        using var pdf = PdfDocument.Open(path);
        var title = CleanTitle(Path.GetFileNameWithoutExtension(path));
        string? author = null, subject = null, keywords = null;
        try
        {
            var info = pdf.Information;
            var metadataTitle = info.Title;
            if (!string.IsNullOrWhiteSpace(metadataTitle)) title = CleanTitle(metadataTitle);
            author = info.Author;
            subject = info.Subject;
            keywords = info.Keywords;
        }
        catch (Exception ex)
        {
            Log(db, path, "warning", $"Malformed PDF metadata was ignored: {ex.Message}");
        }
        // Full PDF text is expensive to keep in memory and is used only by the
        // legacy keyword smart-collection rule. It is opt-in for large libraries.
        var content = "";
        if (Environment.GetEnvironmentVariable("PDF_LIBRARY_MANAGER_FULL_TEXT") == "1")
        {
            var text = new StringBuilder(300000);
            foreach (var page in pdf.GetPages())
            {
                if (text.Length >= 300000) break;
                text.AppendLine(page.Text);
            }
            content = text.Length > 300000 ? text.ToString(0, 300000) : text.ToString();
        }
        var duplicate = ScalarString(db, "SELECT path FROM documents WHERE hash = $hash AND path <> $path LIMIT 1", ("$hash", hash), ("$path", path));
        string? thumbnail = null;
        try { thumbnail = await GetOrCreateThumbnail(path, hash); }
        catch (Exception ex) { Log(db, path, "warning", $"Metadata was indexed, but thumbnail creation failed: {ex.Message}"); }
        UpsertDocument(db, new DocumentInfo(path, hash, file.Length, file.CreationTimeUtc, file.LastWriteTimeUtc, title, author, subject, keywords, pdf.NumberOfPages, thumbnail, null, duplicate, content));
    }
    catch (Exception ex) when (ex is not IOException && ex is not UnauthorizedAccessException)
    {
        // Some real-world PDFs have broken metadata or page-tree entries but still open in Windows readers.
        // Keep the file in the catalog with filename metadata instead of losing it during a folder scan.
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0)
            {
                Log(db, path, "warning", $"Skipped unreadable PDF: {ex.Message}");
                return;
            }
            var hash = Sha256(path);
            var duplicate = ScalarString(db, "SELECT path FROM documents WHERE hash = $hash AND path <> $path LIMIT 1", ("$hash", hash), ("$path", path));
            string? thumbnail = null;
            try { thumbnail = await GetOrCreateThumbnail(path, hash); }
            catch (Exception thumbnailError) { Log(db, path, "warning", $"Indexed with limited metadata; thumbnail was unavailable: {thumbnailError.Message}"); }
            UpsertDocument(db, new DocumentInfo(path, hash, file.Length, file.CreationTimeUtc, file.LastWriteTimeUtc, CleanTitle(Path.GetFileNameWithoutExtension(path)), null, null, null, 0, thumbnail, null, duplicate, ""));
            Log(db, path, "warning", $"Indexed with limited metadata because the PDF structure could not be fully read: {ex.Message}");
        }
        catch (Exception fallbackError)
        {
            Log(db, path, "warning", $"Skipped unreadable PDF after metadata recovery failed: {fallbackError.Message}");
        }
    }
    catch (Exception ex)
    {
        Log(db, path, "warning", $"Skipped unreadable PDF: {ex.Message}");
    }
}

static Task<string> GetOrCreateThumbnail(string pdfPath, string hash)
{
    var thumbnailDirectory = Path.Combine(GetDataDirectory(), "thumbnails");
    Directory.CreateDirectory(thumbnailDirectory);
    var thumbnailPath = Path.Combine(thumbnailDirectory, $"{hash}.webp");
    if (File.Exists(thumbnailPath)) return Task.FromResult(thumbnailPath);

    // Replace legacy, oversized PNG thumbnails the next time a document is
    // indexed.  Thumbnails are disposable and will be recreated if needed.
    var legacyThumbnailPath = Path.Combine(thumbnailDirectory, $"{hash}.png");
    if (File.Exists(legacyThumbnailPath)) File.Delete(legacyThumbnailPath);

    using var pdf = File.OpenRead(pdfPath);
    Conversion.SaveWebp(imageFilename: thumbnailPath, pdfStream: pdf, page: 0, leaveOpen: true, password: null, options: new RenderOptions(Width: 240, Height: null, WithAspectRatio: true));
    return Task.FromResult(thumbnailPath);
}

static string GetDataDirectory()
{
    var configured = Environment.GetEnvironmentVariable("PDF_LIBRARY_MANAGER_DATA_DIR");
    var directory = string.IsNullOrWhiteSpace(configured)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PDFLibraryManager")
        : configured;
    return Path.GetFullPath(directory);
}

static void UpsertDocument(SqliteConnection db, DocumentInfo item)
{
    using var cmd = db.CreateCommand(); cmd.CommandText = """
      INSERT INTO documents(path,hash,size,created_at,modified_at,title,author,subject,keywords,pages,thumbnail_path,cover_image_path,duplicate_of,indexed_at,content_text)
      VALUES($path,$hash,$size,$created,$modified,$title,$author,$subject,$keywords,$pages,$thumbnail,$cover,$duplicate,$indexed,$content)
      ON CONFLICT(path) DO UPDATE SET hash=$hash,size=$size,created_at=CASE WHEN manual_metadata=1 THEN created_at ELSE $created END,modified_at=CASE WHEN manual_metadata=1 THEN modified_at ELSE $modified END,title=CASE WHEN manual_metadata=1 THEN title ELSE $title END,author=CASE WHEN manual_metadata=1 THEN author ELSE $author END,subject=CASE WHEN manual_metadata=1 THEN subject ELSE $subject END,keywords=CASE WHEN manual_metadata=1 THEN keywords ELSE $keywords END,pages=CASE WHEN manual_metadata=1 THEN pages ELSE $pages END,thumbnail_path=$thumbnail,duplicate_of=$duplicate,indexed_at=$indexed,content_text=$content;
      """;
    cmd.Parameters.AddWithValue("$path", item.Path); cmd.Parameters.AddWithValue("$hash", item.Hash); cmd.Parameters.AddWithValue("$size", item.Size); cmd.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O")); cmd.Parameters.AddWithValue("$modified", item.ModifiedAt.ToString("O")); cmd.Parameters.AddWithValue("$title", item.Title); cmd.Parameters.AddWithValue("$author", item.Author ?? ""); cmd.Parameters.AddWithValue("$subject", item.Subject ?? ""); cmd.Parameters.AddWithValue("$keywords", item.Keywords ?? ""); cmd.Parameters.AddWithValue("$pages", item.Pages); cmd.Parameters.AddWithValue("$thumbnail", item.ThumbnailPath ?? ""); cmd.Parameters.AddWithValue("$cover", item.CoverImagePath ?? ""); cmd.Parameters.AddWithValue("$duplicate", item.DuplicateOf ?? ""); cmd.Parameters.AddWithValue("$indexed", DateTimeOffset.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$content", item.ContentText ?? ""); cmd.ExecuteNonQuery();
}

static List<DocumentInfo> ListDocuments(SqliteConnection db, bool deletedOnly = false, bool includeContent = false)
{
    using var cmd = db.CreateCommand(); cmd.CommandText = $"SELECT path,hash,size,created_at,modified_at,title,author,subject,keywords,pages,thumbnail_path,cover_image_path,duplicate_of,{(includeContent ? "content_text" : "NULL")} FROM documents WHERE path IS NOT NULL AND is_deleted = {(deletedOnly ? 1 : 0)} ORDER BY modified_at DESC";
    using var reader = cmd.ExecuteReader(); var items = new List<DocumentInfo>();
    while (reader.Read()) items.Add(new DocumentInfo(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), DateTimeOffset.Parse(reader.GetString(3)), DateTimeOffset.Parse(reader.GetString(4)), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetInt32(9), NullIfEmpty(reader.GetString(10)), reader.IsDBNull(11) ? null : NullIfEmpty(reader.GetString(11)), NullIfEmpty(reader.GetString(12)), reader.IsDBNull(13) ? null : NullIfEmpty(reader.GetString(13))));
    return items;
}

static List<DocumentInfo> SearchDocuments(SqliteConnection db, string query)
{
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT path,hash,size,created_at,modified_at,title,author,subject,keywords,pages,thumbnail_path,cover_image_path,duplicate_of,NULL FROM documents WHERE is_deleted = 0 AND title LIKE $query ORDER BY modified_at DESC";
    cmd.Parameters.AddWithValue("$query", $"%{query.Replace("%", "[%]").Replace("_", "[_]")}%");
    using var reader = cmd.ExecuteReader(); var items = new List<DocumentInfo>();
    while (reader.Read()) items.Add(new DocumentInfo(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), DateTimeOffset.Parse(reader.GetString(3)), DateTimeOffset.Parse(reader.GetString(4)), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetInt32(9), NullIfEmpty(reader.GetString(10)), reader.IsDBNull(11) ? null : NullIfEmpty(reader.GetString(11)), NullIfEmpty(reader.GetString(12)), reader.IsDBNull(13) ? null : NullIfEmpty(reader.GetString(13))));
    return items;
}

static DocumentPage ListDocumentPage(SqliteConnection db, PageRequest request)
{
    var offset = Math.Max(0, request.Offset);
    var limit = Math.Clamp(request.Limit, 1, 120);
    using var count = db.CreateCommand();
    count.CommandText = "SELECT COUNT(*) FROM documents WHERE path IS NOT NULL AND is_deleted=0";
    var total = Convert.ToInt32(count.ExecuteScalar());
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT path,hash,size,created_at,modified_at,title,author,subject,keywords,pages,thumbnail_path,cover_image_path,duplicate_of,NULL FROM documents WHERE path IS NOT NULL AND is_deleted=0 ORDER BY modified_at DESC LIMIT $limit OFFSET $offset";
    cmd.Parameters.AddWithValue("$limit", limit); cmd.Parameters.AddWithValue("$offset", offset);
    using var reader = cmd.ExecuteReader(); var items = new List<DocumentInfo>();
    while (reader.Read()) items.Add(ReadDocument(reader));
    return new DocumentPage(items, total);
}

static DocumentPage SearchDocumentPage(SqliteConnection db, PageRequest request)
{
    var offset = Math.Max(0, request.Offset);
    var limit = Math.Clamp(request.Limit, 1, 120);
    var terms = System.Text.RegularExpressions.Regex.Split(request.Query?.Trim() ?? "", @"\s+").Where(term => term.Length > 0).Take(8).ToList();
    var where = new StringBuilder("path IS NOT NULL AND is_deleted=0");
    for (var index = 0; index < terms.Count; index++) where.Append($" AND LOWER(title) LIKE LOWER($term{index}) ESCAPE '\\'");
    void AddTerms(SqliteCommand command)
    {
        for (var index = 0; index < terms.Count; index++) command.Parameters.AddWithValue($"$term{index}", $"%{terms[index].Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%");
    }
    using var count = db.CreateCommand(); count.CommandText = $"SELECT COUNT(*) FROM documents WHERE {where}"; AddTerms(count);
    var total = Convert.ToInt32(count.ExecuteScalar());
    using var cmd = db.CreateCommand(); cmd.CommandText = $"SELECT path,hash,size,created_at,modified_at,title,author,subject,keywords,pages,thumbnail_path,cover_image_path,duplicate_of,NULL FROM documents WHERE {where} ORDER BY modified_at DESC LIMIT $limit OFFSET $offset"; AddTerms(cmd); cmd.Parameters.AddWithValue("$limit", limit); cmd.Parameters.AddWithValue("$offset", offset);
    using var reader = cmd.ExecuteReader(); var items = new List<DocumentInfo>(); while (reader.Read()) items.Add(ReadDocument(reader));
    return new DocumentPage(items, total);
}

static List<string> GetTitleSuggestions(SqliteConnection db)
{
    using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT DISTINCT title FROM documents WHERE is_deleted=0 AND TRIM(title)<>'' ORDER BY title COLLATE NOCASE LIMIT 500";
    using var reader = cmd.ExecuteReader(); var titles = new List<string>(); while (reader.Read()) titles.Add(reader.GetString(0)); return titles;
}

static async Task<string> EnsureThumbnail(SqliteConnection db, string pdfPath)
{
    var path = Path.GetFullPath(pdfPath);
    using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT hash FROM documents WHERE path=$path AND is_deleted=0"; cmd.Parameters.AddWithValue("$path", path);
    var hash = cmd.ExecuteScalar() as string ?? throw new ArgumentException("PDF is not in the library.");
    var thumbnail = await GetOrCreateThumbnail(path, hash);
    Execute(db, "UPDATE documents SET thumbnail_path=$thumbnail WHERE path=$path", ("$thumbnail", thumbnail), ("$path", path));
    return thumbnail;
}

static void TrimThumbnailCache(long maxBytes)
{
    var directory = Path.Combine(GetDataDirectory(), "thumbnails");
    if (!Directory.Exists(directory)) return;
    var files = new DirectoryInfo(directory).EnumerateFiles("*.*", SearchOption.TopDirectoryOnly)
        .Where(file => file.Extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(file => file.LastAccessTimeUtc == DateTime.MinValue ? file.LastWriteTimeUtc : file.LastAccessTimeUtc).ToList();
    long retained = 0;
    foreach (var file in files)
    {
        retained += file.Length;
        if (retained <= maxBytes) continue;
        try { file.Delete(); } catch { /* A visible cover can be recreated later. */ }
    }
}

static DocumentInfo ReadDocument(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), DateTimeOffset.Parse(reader.GetString(3)), DateTimeOffset.Parse(reader.GetString(4)), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetInt32(9), NullIfEmpty(reader.GetString(10)), reader.IsDBNull(11) ? null : NullIfEmpty(reader.GetString(11)), NullIfEmpty(reader.GetString(12)), reader.IsDBNull(13) ? null : NullIfEmpty(reader.GetString(13)));

static void UpdateMetadata(SqliteConnection db, MetadataUpdate update)
{
    if (string.IsNullOrWhiteSpace(update.Title)) throw new ArgumentException("A title is required.");
    if (update.Pages < 0) throw new ArgumentException("Page count cannot be negative.");
    if (!DateTimeOffset.TryParse(update.CreatedAt, out var created) || !DateTimeOffset.TryParse(update.ModifiedAt, out var modified)) throw new ArgumentException("Invalid catalog date.");
    using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE documents SET title=$title,author=$author,subject=$subject,keywords=$keywords,pages=$pages,created_at=$created,modified_at=$modified,manual_metadata=1 WHERE path=$path";
    cmd.Parameters.AddWithValue("$path", Path.GetFullPath(update.Path)); cmd.Parameters.AddWithValue("$title", update.Title.Trim()); cmd.Parameters.AddWithValue("$author", update.Author ?? ""); cmd.Parameters.AddWithValue("$subject", update.Subject ?? ""); cmd.Parameters.AddWithValue("$keywords", update.Keywords ?? ""); cmd.Parameters.AddWithValue("$pages", update.Pages); cmd.Parameters.AddWithValue("$created", created.ToString("O")); cmd.Parameters.AddWithValue("$modified", modified.ToString("O")); cmd.ExecuteNonQuery();
}
static int UpdateAuthorGroup(SqliteConnection db, AuthorUpdate update)
{
    var current = update.CurrentAuthor == "Unknown author" ? "" : update.CurrentAuthor.Trim();
    var replacement = update.NewAuthor.Trim();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE documents SET author=$replacement,manual_metadata=1 WHERE COALESCE(author,'')=$current";
    cmd.Parameters.AddWithValue("$current", current); cmd.Parameters.AddWithValue("$replacement", replacement);
    return cmd.ExecuteNonQuery();
}

static void UpdateDocumentCover(SqliteConnection db, DocumentCoverUpdate update)
{
    var path = Path.GetFullPath(update.Path);
    var cover = string.IsNullOrWhiteSpace(update.CoverImagePath) ? "" : Path.GetFullPath(update.CoverImagePath);
    if (!string.IsNullOrWhiteSpace(cover) && !File.Exists(cover)) throw new FileNotFoundException("The selected cover image no longer exists.", cover);
    Execute(db, "UPDATE documents SET cover_image_path=$cover WHERE path=$path", ("$cover", cover), ("$path", path));
}

static List<SourceStat> GetSourceStats(SqliteConnection db)
{
    var sources = new List<(string Path, string Kind, string? LastScannedAt)>();
    using (var cmd = db.CreateCommand())
    {
        cmd.CommandText = "SELECT path,kind,COALESCE(last_scanned_at,'') FROM sources ORDER BY path";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) sources.Add((reader.GetString(0), reader.GetString(1), NullIfEmpty(reader.GetString(2))));
    }
    var documents = ListDocuments(db);
    return sources.Select(source => new SourceStat(source.Path, source.Kind,
        documents.Count(document => source.Kind == "folder"
            ? document.Path.StartsWith(source.Path, StringComparison.OrdinalIgnoreCase)
            : document.Path.Equals(source.Path, StringComparison.OrdinalIgnoreCase)), source.LastScannedAt)).ToList();
}

static List<ScanLogInfo> GetScanLog(SqliteConnection db)
{
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT path,level,message,created_at FROM scan_log ORDER BY id DESC LIMIT 100";
    using var reader = cmd.ExecuteReader();
    var result = new List<ScanLogInfo>();
    while (reader.Read()) result.Add(new ScanLogInfo(reader.IsDBNull(0) ? "" : reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
    return result;
}

static List<CollectionInfo> GetCollections(SqliteConnection db)
{
    using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT c.name,COUNT(cd.document_path),COALESCE(c.icon,''),COALESCE(c.image_path,'') FROM collections c LEFT JOIN collection_documents cd ON cd.collection_name=c.name GROUP BY c.name ORDER BY c.position,c.name"; using var reader = cmd.ExecuteReader(); var result = new List<CollectionInfo>(); while (reader.Read()) result.Add(new CollectionInfo(reader.GetString(0), reader.GetInt32(1), NullIfEmpty(reader.GetString(2)), NullIfEmpty(reader.GetString(3)))); return result;
}

static void CreateCollection(SqliteConnection db, string name)
{
    var clean = name.Trim(); if (string.IsNullOrWhiteSpace(clean)) throw new ArgumentException("Collection name is required."); Execute(db, "INSERT OR IGNORE INTO collections(name,created_at) VALUES($name,$now)", ("$name", clean), ("$now", DateTimeOffset.UtcNow.ToString("O")));
}

static void AddToCollection(SqliteConnection db, string name, string path)
{
    CreateCollection(db, name); Execute(db, "INSERT OR IGNORE INTO collection_documents(collection_name,document_path) VALUES($name,$path)", ("$name", name.Trim()), ("$path", Path.GetFullPath(path)));
}

static void RemoveFromCollection(SqliteConnection db, string name, string path) => Execute(db, "DELETE FROM collection_documents WHERE collection_name=$name AND document_path=$path", ("$name", name.Trim()), ("$path", Path.GetFullPath(path)));

static List<string> GetDocumentCollections(SqliteConnection db, string path)
{
    using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT collection_name FROM collection_documents WHERE document_path=$path ORDER BY collection_name"; cmd.Parameters.AddWithValue("$path", Path.GetFullPath(path)); using var reader = cmd.ExecuteReader(); var result = new List<string>(); while (reader.Read()) result.Add(reader.GetString(0)); return result;
}

static void RenameCollection(SqliteConnection db, CollectionRename rename)
{
    var oldName = rename.OldName.Trim(); var newName = rename.NewName.Trim();
    if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("Collection name is required.");
    if (oldName.Equals(newName, StringComparison.Ordinal)) return;
    if (ScalarString(db, "SELECT name FROM collections WHERE name=$name", ("$name", newName)) is not null) throw new ArgumentException("A collection with that name already exists.");
    Execute(db, "UPDATE collections SET name=$new WHERE name=$old", ("$new", newName), ("$old", oldName));
    Execute(db, "UPDATE collection_documents SET collection_name=$new WHERE collection_name=$old", ("$new", newName), ("$old", oldName));
}

static void DeleteCollection(SqliteConnection db, string name)
{
    Execute(db, "DELETE FROM collection_documents WHERE collection_name=$name", ("$name", name.Trim()));
    Execute(db, "DELETE FROM collections WHERE name=$name", ("$name", name.Trim()));
}

static void ReorderCollections(SqliteConnection db, List<string> names)
{
    var position = 1;
    foreach (var name in names.Distinct(StringComparer.Ordinal)) { Execute(db, "UPDATE collections SET position=$position WHERE name=$name", ("$position", position.ToString()), ("$name", name)); position++; }
}

static void UpdateCollectionAppearance(SqliteConnection db, CollectionAppearance appearance)
{
    CreateCollection(db, appearance.Name);
    Execute(db, "UPDATE collections SET icon=$icon,image_path=$image WHERE name=$name", ("$name", appearance.Name.Trim()), ("$icon", appearance.Icon?.Trim() ?? ""), ("$image", appearance.ImagePath?.Trim() ?? ""));
}

static List<DocumentInfo> GetCollectionDocuments(SqliteConnection db, string name)
{
    using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT d.path,d.hash,d.size,d.created_at,d.modified_at,d.title,d.author,d.subject,d.keywords,d.pages,d.thumbnail_path,d.cover_image_path,d.duplicate_of,NULL FROM documents d INNER JOIN collection_documents cd ON cd.document_path=d.path WHERE cd.collection_name=$name AND d.is_deleted=0 ORDER BY d.modified_at DESC"; cmd.Parameters.AddWithValue("$name", name); using var reader = cmd.ExecuteReader(); var items = new List<DocumentInfo>(); while (reader.Read()) items.Add(new DocumentInfo(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), DateTimeOffset.Parse(reader.GetString(3)), DateTimeOffset.Parse(reader.GetString(4)), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetInt32(9), NullIfEmpty(reader.GetString(10)), reader.IsDBNull(11) ? null : NullIfEmpty(reader.GetString(11)), NullIfEmpty(reader.GetString(12)), reader.IsDBNull(13) ? null : NullIfEmpty(reader.GetString(13)))); return items;
}

static DocumentPage GetCollectionDocumentPage(SqliteConnection db, NamedPageRequest request)
{
    var offset = Math.Max(0, request.Offset);
    var limit = Math.Clamp(request.Limit, 1, 120);
    var terms = System.Text.RegularExpressions.Regex.Split(request.Query?.Trim() ?? "", @"\s+").Where(term => term.Length > 0).Take(8).ToList();
    var where = new StringBuilder("cd.collection_name=$name AND d.is_deleted=0");
    for (var index = 0; index < terms.Count; index++) where.Append($" AND LOWER(d.title) LIKE LOWER($term{index}) ESCAPE '\\'");
    void AddParameters(SqliteCommand command)
    {
        command.Parameters.AddWithValue("$name", request.Name);
        for (var index = 0; index < terms.Count; index++) command.Parameters.AddWithValue($"$term{index}", $"%{terms[index].Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%");
    }
    using var count = db.CreateCommand();
    count.CommandText = $"SELECT COUNT(*) FROM collection_documents cd INNER JOIN documents d ON d.path=cd.document_path WHERE {where}";
    AddParameters(count);
    var total = Convert.ToInt32(count.ExecuteScalar());
    using var cmd = db.CreateCommand();
    cmd.CommandText = $"SELECT d.path,d.hash,d.size,d.created_at,d.modified_at,d.title,d.author,d.subject,d.keywords,d.pages,d.thumbnail_path,d.cover_image_path,d.duplicate_of,NULL FROM documents d INNER JOIN collection_documents cd ON cd.document_path=d.path WHERE {where} ORDER BY d.modified_at DESC LIMIT $limit OFFSET $offset";
    AddParameters(cmd);
    cmd.Parameters.AddWithValue("$limit", limit);
    cmd.Parameters.AddWithValue("$offset", offset);
    using var reader = cmd.ExecuteReader();
    var items = new List<DocumentInfo>();
    while (reader.Read()) items.Add(ReadDocument(reader));
    return new DocumentPage(items, total);
}

static List<string> GetDocumentTags(SqliteConnection db, string path)
{
    using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT tag FROM document_tags WHERE document_path=$path ORDER BY tag"; cmd.Parameters.AddWithValue("$path", Path.GetFullPath(path)); using var reader = cmd.ExecuteReader(); var result = new List<string>(); while (reader.Read()) result.Add(reader.GetString(0)); return result;
}

static void SetDocumentTags(SqliteConnection db, DocumentTagsUpdate update)
{
    var path = Path.GetFullPath(update.Path); var tags = update.Tags.Select(tag => tag.Trim()).Where(tag => !string.IsNullOrWhiteSpace(tag)).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();
    Execute(db, "DELETE FROM document_tags WHERE document_path=$path", ("$path", path));
    foreach (var tag in tags) Execute(db, "INSERT INTO document_tags(document_path,tag) VALUES($path,$tag)", ("$path", path), ("$tag", tag));
}

static List<SmartCollectionInfo> GetSmartCollections(SqliteConnection db)
{
    using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT name,rule_type,COALESCE(rule_value,''),created_at,position FROM smart_collections ORDER BY position,name"; using var reader = cmd.ExecuteReader(); var result = new List<SmartCollectionInfo>(); while (reader.Read()) result.Add(new SmartCollectionInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4))); return result;
}

static void CreateSmartCollection(SqliteConnection db, SmartCollectionInfo shelf)
{
    var name = shelf.Name.Trim(); var ruleType = shelf.RuleType.Trim().ToLowerInvariant(); var value = shelf.RuleValue?.Trim() ?? "";
    if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Smart collection name is required.");
    if (ruleType is not ("author" or "keyword" or "folder" or "unread")) throw new ArgumentException("Unsupported smart collection rule.");
    if (ruleType != "unread" && string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Enter a value for this smart collection rule.");
    using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO smart_collections(name,rule_type,rule_value,created_at,position) VALUES($name,$type,$value,$created,(SELECT COALESCE(MAX(position),0)+1 FROM smart_collections)) ON CONFLICT(name) DO UPDATE SET rule_type=$type,rule_value=$value"; cmd.Parameters.AddWithValue("$name", name); cmd.Parameters.AddWithValue("$type", ruleType); cmd.Parameters.AddWithValue("$value", value); cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
}

static void DeleteSmartCollection(SqliteConnection db, string name) => Execute(db, "DELETE FROM smart_collections WHERE name=$name", ("$name", name.Trim()));

static List<DocumentInfo> GetSmartCollectionDocuments(SqliteConnection db, string name)
{
    var shelf = GetSmartCollections(db).FirstOrDefault(item => item.Name.Equals(name, StringComparison.Ordinal)) ?? throw new ArgumentException("Smart collection not found.");
    var documents = ListDocuments(db, includeContent: true);
    var ruleValue = shelf.RuleValue ?? "";
    var matches = shelf.RuleType switch
    {
        "author" => documents.Where(doc => (doc.Author ?? "").Contains(ruleValue, StringComparison.OrdinalIgnoreCase)).ToList(),
        "keyword" => documents.Where(doc => (doc.Keywords ?? "").Contains(ruleValue, StringComparison.OrdinalIgnoreCase) || (doc.Subject ?? "").Contains(ruleValue, StringComparison.OrdinalIgnoreCase) || (doc.ContentText ?? "").Contains(ruleValue, StringComparison.OrdinalIgnoreCase)).ToList(),
        "folder" => documents.Where(doc => doc.Path.StartsWith(Path.GetFullPath(ruleValue), StringComparison.OrdinalIgnoreCase)).ToList(),
        _ => documents
    };
    return matches.Select(doc => doc with { ContentText = null }).ToList();
}

static void SetTrashState(SqliteConnection db, string path, bool deleted)
{
    Execute(db, "UPDATE documents SET is_deleted = $deleted, trashed_at = $trashed WHERE path = $path", ("$deleted", deleted ? "1" : "0"), ("$trashed", deleted ? DateTimeOffset.UtcNow.ToString("O") : ""), ("$path", Path.GetFullPath(path)));
}

static int EmptyTrash(SqliteConnection db)
{
    using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM documents WHERE is_deleted = 1";
    return cmd.ExecuteNonQuery();
}

static int RemoveMissingSources(SqliteConnection db)
{
    var missing = GetSources(db).Where(source => source.Kind == "file" ? !File.Exists(source.Path) : !Directory.Exists(source.Path)).ToList();
    var staleDocuments = QueryStrings(db, "SELECT path FROM documents").Where(path => !File.Exists(path)).ToList();
    using var tx = db.BeginTransaction();
    foreach (var source in missing) Execute(db, "DELETE FROM sources WHERE path = $path", ("$path", source.Path));
    foreach (var path in staleDocuments) Execute(db, "DELETE FROM documents WHERE path = $path", ("$path", path));
    tx.Commit(); return missing.Count + staleDocuments.Count;
}

static string CleanTitle(string name) => System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(name).Replace('_', ' ').Replace('-', ' '), @"\s*(copy|final|v\d+|\d{4,}|\(\d+\))\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
static string Sha256(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
static bool IsExcluded(string path, HashSet<string> exclusions)
{
    var candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    return exclusions.Any(excluded =>
    {
        var root = Path.GetFullPath(excluded).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    });
}
static void Log(SqliteConnection db, string path, string level, string message) => Execute(db, "INSERT INTO scan_log(path,level,message,created_at) VALUES($path,$level,$message,$now)", ("$path", path), ("$level", level), ("$message", message), ("$now", DateTimeOffset.UtcNow.ToString("O")));
static List<SourceInfo> GetSources(SqliteConnection db) { using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT path, kind, COALESCE(scan_subfolders, 1) FROM sources"; using var reader = cmd.ExecuteReader(); var result = new List<SourceInfo>(); while (reader.Read()) result.Add(new SourceInfo(reader.GetString(0), reader.GetString(1), reader.GetInt64(2) != 0)); return result; }
static HashSet<string> QueryStrings(SqliteConnection db, string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; using var reader = cmd.ExecuteReader(); var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase); while (reader.Read()) result.Add(reader.GetString(0)); return result; }
static string? ScalarString(SqliteConnection db, string sql, params (string Name, string Value)[] parameters) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value); return cmd.ExecuteScalar() as string; }
static void Execute(SqliteConnection db, string sql, params (string Name, string Value)[] parameters) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value); cmd.ExecuteNonQuery(); }
static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
record DocumentInfo(string Path, string Hash, long Size, DateTimeOffset CreatedAt, DateTimeOffset ModifiedAt, string Title, string? Author, string? Subject, string? Keywords, int Pages, string? ThumbnailPath, string? CoverImagePath, string? DuplicateOf, string? ContentText = null);
record PageRequest(int Offset = 0, int Limit = 60, string? Query = null);
record NamedPageRequest(string Name, int Offset = 0, int Limit = 60, string? Query = null);
record DocumentPage(List<DocumentInfo> Items, int Total);
record MetadataUpdate(string Path, string Title, string? Author, string? Subject, string? Keywords, int Pages, string CreatedAt, string ModifiedAt);
record AuthorUpdate(string CurrentAuthor, string NewAuthor);
record DocumentCoverUpdate(string Path, string? CoverImagePath);
record SourceScanUpdate(string Path, bool ScanSubfolders);
record SourceInfo(string Path, string Kind, bool ScanSubfolders);
record SourceStat(string Path, string Kind, int DocumentCount, string? LastScannedAt);
record ScanProgress(string Type, string Source, string Path, int Processed, long WorkingSetBytes = 0, long ManagedBytes = 0);
record ScanLogInfo(string Path, string Level, string Message, string CreatedAt);
record CollectionInfo(string Name, int DocumentCount, string? Icon, string? ImagePath);
record CollectionAppearance(string Name, string? Icon, string? ImagePath);
record CollectionRename(string OldName, string NewName);
record DocumentTagsUpdate(string Path, List<string> Tags);
record SmartCollectionInfo(string Name, string RuleType, string? RuleValue, string? CreatedAt = null, int Position = 0);
