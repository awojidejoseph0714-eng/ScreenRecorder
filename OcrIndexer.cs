using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using Microsoft.Data.Sqlite;

namespace ScreenRecorder
{
    public class SearchResult
    {
        public string FilePath { get; set; } = "";
        public double OffsetSeconds { get; set; }
        public string Text { get; set; } = "";
        public DateTime Timestamp { get; set; }
    }

    public class SegmentInfo
    {
        public long Id { get; set; }
        public string FilePath { get; set; } = "";
        public long StartTime { get; set; } // Unix ms
        public long EndTime { get; set; }   // Unix ms
        public bool IsPinned { get; set; }
    }

    public class OcrIndexer : IDisposable
    {
        private string _dbPath = "";
        private OcrEngine? _ocrEngine;
        private readonly ConcurrentQueue<(Bitmap bitmap, long segmentId, double offsetSec)> _queue = new();
        private readonly AutoResetEvent _queueSignal = new(false);
        private Thread? _workerThread;
        private bool _isRunning = false;
        private const int MaxQueueDepth = 8;

        public OcrIndexer()
        {
            // Initialize WinRT OCR Engine
            _ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (_ocrEngine != null)
            {
                // Warmup the OCR engine on a background thread so the first real frame is recognized instantly
                Task.Run(async () =>
                {
                    try
                    {
                        using (var softwareBitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 1, 1))
                        {
                            await _ocrEngine.RecognizeAsync(softwareBitmap);
                            Debug.WriteLine("OCR engine warmed up successfully on launch.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"OCR warmup error: {ex.Message}");
                    }
                });
            }
            else
            {
                Debug.WriteLine("OCR Engine could not be created from user profile languages.");
            }
        }

        public void Initialize(string outputDir)
        {
            if (!Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            _dbPath = Path.Combine(outputDir, "ocr_index.db");
            InitializeDatabase();

            // Start worker thread
            _isRunning = true;
            _workerThread = new Thread(ProcessQueue)
            {
                IsBackground = true,
                Name = "OcrWorker"
            };
            _workerThread.Start();
        }

        private void InitializeDatabase()
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();

            using (var cmd = new SqliteCommand("PRAGMA journal_mode=WAL;", conn))
            {
                cmd.ExecuteNonQuery();
            }

            string createSegmentsTable = @"
                CREATE TABLE IF NOT EXISTS segments (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    filepath TEXT UNIQUE,
                    start_time INTEGER,
                    end_time INTEGER,
                    is_pinned INTEGER DEFAULT 0
                );";

            string createOcrTable = @"
                CREATE TABLE IF NOT EXISTS ocr_entries (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    segment_id INTEGER,
                    offset_seconds REAL,
                    recognized_text TEXT,
                    FOREIGN KEY(segment_id) REFERENCES segments(id) ON DELETE CASCADE
                );";

            string createFtsTable = @"
                CREATE VIRTUAL TABLE IF NOT EXISTS ocr_fts USING fts5(
                    recognized_text,
                    segment_id UNINDEXED,
                    offset_seconds UNINDEXED
                );";

            using (var cmd = new SqliteCommand(createSegmentsTable, conn)) cmd.ExecuteNonQuery();
            using (var cmd = new SqliteCommand(createOcrTable, conn)) cmd.ExecuteNonQuery();
            using (var cmd = new SqliteCommand(createFtsTable, conn)) cmd.ExecuteNonQuery();
        }

        public long RegisterSegment(string filepath, long startTime)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={_dbPath}");
                conn.Open();

                string insertSql = @"
                    INSERT INTO segments (filepath, start_time, end_time, is_pinned)
                    VALUES ($filepath, $start_time, $end_time, 0)
                    ON CONFLICT(filepath) DO UPDATE SET start_time=$start_time;
                    SELECT last_insert_rowid();";

                using var cmd = new SqliteCommand(insertSql, conn);
                cmd.Parameters.AddWithValue("$filepath", filepath);
                cmd.Parameters.AddWithValue("$start_time", startTime);
                cmd.Parameters.AddWithValue("$end_time", startTime); // initially same

                object? result = cmd.ExecuteScalar();
                return result != null ? Convert.ToInt64(result) : -1;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error registering segment: {ex.Message}");
                return -1;
            }
        }

        public void UpdateSegmentEndTime(long segmentId, long endTime)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={_dbPath}");
                conn.Open();

                string updateSql = "UPDATE segments SET end_time = $end_time WHERE id = $id;";
                using var cmd = new SqliteCommand(updateSql, conn);
                cmd.Parameters.AddWithValue("$end_time", endTime);
                cmd.Parameters.AddWithValue("$id", segmentId);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating segment end time: {ex.Message}");
            }
        }

        public void PinSegment(string filepath, bool pin)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={_dbPath}");
                conn.Open();

                string updateSql = "UPDATE segments SET is_pinned = $pin WHERE filepath = $filepath;";
                using var cmd = new SqliteCommand(updateSql, conn);
                cmd.Parameters.AddWithValue("$pin", pin ? 1 : 0);
                cmd.Parameters.AddWithValue("$filepath", filepath);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error pinning segment: {ex.Message}");
            }
        }

        public void DeleteSegmentFromDb(string filepath)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={_dbPath}");
                conn.Open();

                // FTS matches should be deleted as well if we manage it. 
                // Since ocr_entries has foreign key ON DELETE CASCADE, we can delete the row from segments:
                // First get segment id
                long segmentId = -1;
                using (var getCmd = new SqliteCommand("SELECT id FROM segments WHERE filepath = $filepath", conn))
                {
                    getCmd.Parameters.AddWithValue("$filepath", filepath);
                    object? res = getCmd.ExecuteScalar();
                    if (res != null) segmentId = Convert.ToInt64(res);
                }

                if (segmentId != -1)
                {
                    using (var delFts = new SqliteCommand("DELETE FROM ocr_fts WHERE segment_id = $segmentId", conn))
                    {
                        delFts.Parameters.AddWithValue("$segmentId", segmentId);
                        delFts.ExecuteNonQuery();
                    }

                    using (var delSeg = new SqliteCommand("DELETE FROM segments WHERE id = $id", conn))
                    {
                        delSeg.Parameters.AddWithValue("$id", segmentId);
                        delSeg.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error deleting segment: {ex.Message}");
            }
        }

        public void CheckpointDatabase()
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={_dbPath}");
                conn.Open();
                using (var cmd = new SqliteCommand("PRAGMA wal_checkpoint(TRUNCATE);", conn))
                {
                    cmd.ExecuteNonQuery();
                    Debug.WriteLine("Database WAL file successfully checkpointed and truncated.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checkpointing database: {ex.Message}");
            }
        }

        public List<SegmentInfo> GetSegments()
        {
            var list = new List<SegmentInfo>();
            try
            {
                using var conn = new SqliteConnection($"Data Source={_dbPath}");
                conn.Open();

                using var cmd = new SqliteCommand("SELECT id, filepath, start_time, end_time, is_pinned FROM segments ORDER BY start_time ASC", conn);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new SegmentInfo
                    {
                        Id = reader.GetInt64(0),
                        FilePath = reader.GetString(1),
                        StartTime = reader.GetInt64(2),
                        EndTime = reader.GetInt64(3),
                        IsPinned = reader.GetInt32(4) == 1
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error reading segments: {ex.Message}");
            }
            return list;
        }

        public void AddFrameToQueue(Bitmap bitmap, long segmentId, double offsetSec)
        {
            if (!_isRunning || _ocrEngine == null) return;

            // Cap the queue depth to prevent memory bloating
            if (_queue.Count >= MaxQueueDepth)
            {
                if (_queue.TryDequeue(out var stale))
                {
                    stale.bitmap.Dispose();
                }
            }

            // Clone bitmap for background thread
            Bitmap clone = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppRgb);
            using (Graphics g = Graphics.FromImage(clone))
            {
                g.DrawImage(bitmap, 0, 0);
            }

            _queue.Enqueue((clone, segmentId, offsetSec));
            _queueSignal.Set();
        }

        private void ProcessQueue()
        {
            while (_isRunning)
            {
                if (_queue.TryDequeue(out var item))
                {
                    try
                    {
                        ProcessOcrItem(item.bitmap, item.segmentId, item.offsetSec).Wait();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"OCR indexing error: {ex.Message}");
                    }
                    finally
                    {
                        item.bitmap.Dispose();
                    }
                }
                else
                {
                    _queueSignal.WaitOne(1000);
                }
            }
        }

        private async Task ProcessOcrItem(Bitmap originalBmp, long segmentId, double offsetSec)
        {
            if (_ocrEngine == null) return;

            // 1. Downscale to 720p (1280x720) to significantly speed up OCR and reduce CPU load
            int targetWidth = 1280;
            int targetHeight = (int)(originalBmp.Height * (1280.0 / originalBmp.Width));
            
            using Bitmap downscaledBmp = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppRgb);
            using (Graphics g = Graphics.FromImage(downscaledBmp))
            {
                g.CompositingQuality = CompositingQuality.HighSpeed;
                g.InterpolationMode = InterpolationMode.Low;
                g.SmoothingMode = SmoothingMode.HighSpeed;
                g.DrawImage(originalBmp, 0, 0, targetWidth, targetHeight);
            }

            // 2. Convert Bitmap to WinRT SoftwareBitmap via standard BMP memory stream
            using (var ms = new MemoryStream())
            {
                downscaledBmp.Save(ms, ImageFormat.Bmp);
                ms.Position = 0;
                using (var winrtStream = ms.AsRandomAccessStream())
                {
                    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(winrtStream);
                    using (SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync())
                    {
                        // 3. Recognize text
                        OcrResult result = await _ocrEngine.RecognizeAsync(softwareBitmap);
                        string text = result.Text;

                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            // 4. Save to database
                            SaveOcrEntry(segmentId, offsetSec, text);
                        }
                    }
                }
            }
        }

        private void SaveOcrEntry(long segmentId, double offsetSec, string text)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={_dbPath}");
                conn.Open();

                // Start Transaction for speedy single-row inserts and consistency
                using var transaction = conn.BeginTransaction();

                string insertOcrSql = @"
                    INSERT INTO ocr_entries (segment_id, offset_seconds, recognized_text)
                    VALUES ($segment_id, $offset_seconds, $recognized_text);
                    SELECT last_insert_rowid();";

                long entryId = -1;
                using (var cmd = new SqliteCommand(insertOcrSql, conn, transaction))
                {
                    cmd.Parameters.AddWithValue("$segment_id", segmentId);
                    cmd.Parameters.AddWithValue("$offset_seconds", offsetSec);
                    cmd.Parameters.AddWithValue("$recognized_text", text);
                    object? res = cmd.ExecuteScalar();
                    if (res != null) entryId = Convert.ToInt64(res);
                }

                if (entryId != -1)
                {
                    string insertFtsSql = @"
                        INSERT INTO ocr_fts (rowid, recognized_text, segment_id, offset_seconds)
                        VALUES ($rowid, $recognized_text, $segment_id, $offset_seconds);";

                    using var cmdFts = new SqliteCommand(insertFtsSql, conn, transaction);
                    cmdFts.Parameters.AddWithValue("$rowid", entryId);
                    cmdFts.Parameters.AddWithValue("$recognized_text", text);
                    cmdFts.Parameters.AddWithValue("$segment_id", segmentId);
                    cmdFts.Parameters.AddWithValue("$offset_seconds", offsetSec);
                    cmdFts.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Database write error: {ex.Message}");
            }
        }

        public List<SearchResult> Search(string queryText)
        {
            var results = new List<SearchResult>();
            if (string.IsNullOrWhiteSpace(queryText)) return results;

            try
            {
                using var conn = new SqliteConnection($"Data Source={_dbPath}");
                conn.Open();

                // Standard search query using MATCH on FTS5
                // Join segments table to retrieve filepath and start_time
                string searchSql = @"
                    SELECT s.filepath, f.offset_seconds, f.recognized_text, s.start_time
                    FROM ocr_fts f
                    JOIN segments s ON f.segment_id = s.id
                    WHERE ocr_fts MATCH $query
                    ORDER BY s.start_time DESC, f.offset_seconds ASC
                    LIMIT 200;";

                using var cmd = new SqliteCommand(searchSql, conn);
                // Sanitize and query (clean up special characters if needed, or wrap in quotes for phrase matching)
                cmd.Parameters.AddWithValue("$query", queryText);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string filepath = reader.GetString(0);
                    double offsetSec = reader.GetDouble(1);
                    string text = reader.GetString(2);
                    long startTimeMs = reader.GetInt64(3);

                    DateTime timestamp = DateTimeOffset.FromUnixTimeMilliseconds(startTimeMs).DateTime.ToLocalTime()
                                         .AddSeconds(offsetSec);

                    results.Add(new SearchResult
                    {
                        FilePath = filepath,
                        OffsetSeconds = offsetSec,
                        Text = text,
                        Timestamp = timestamp
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error searching SQLite DB: {ex.Message}");
            }
            return results;
        }

        public void Dispose()
        {
            _isRunning = false;
            _queueSignal.Set();

            if (_workerThread != null && _workerThread.IsAlive)
            {
                _workerThread.Join(1000);
            }

            // Dispose queue items
            while (_queue.TryDequeue(out var item))
            {
                item.bitmap.Dispose();
            }

            _queueSignal.Dispose();
        }
    }
}
