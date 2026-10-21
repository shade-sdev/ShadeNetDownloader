using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ShadeNet.Core.Models;

namespace ShadeNet.Core.Storage;

/// <summary>
/// Crash-safe job + segment persistence (SQLite, WAL). Progress is checkpointed every couple of seconds and on every
/// state change; segment progress is trusted only up to the last checkpoint, the tail is simply re-downloaded.
/// </summary>
public sealed class JobStore : IDisposable
{
    const int SchemaVersion = 2;

    readonly object _gate = new();
    readonly SqliteConnection _db;

    public JobStore(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _db.Open();

        // Right after a crash the dead process' locks on the WAL files can linger for a moment (Windows reports a
        // plain I/O error). Opening is cheap, so retry briefly instead of failing the whole app start.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Exec("PRAGMA journal_mode=WAL;");
                break;
            }
            catch (SqliteException) when (attempt < 10)
            {
                Thread.Sleep(200);
            }
        }

        Exec("PRAGMA synchronous=NORMAL;");
        Migrate();
    }

    void Migrate()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version < 1)
        {
            Exec("""
                CREATE TABLE jobs (
                    id TEXT PRIMARY KEY,
                    url TEXT NOT NULL,
                    final_url TEXT,
                    referer TEXT,
                    cookies TEXT,
                    user_agent TEXT,
                    headers TEXT,
                    file_name TEXT NOT NULL,
                    save_dir TEXT NOT NULL,
                    category TEXT NOT NULL,
                    probed INTEGER NOT NULL,
                    total_size INTEGER,
                    content_type TEXT,
                    etag TEXT,
                    last_modified TEXT,
                    accepts_ranges INTEGER NOT NULL,
                    state INTEGER NOT NULL,
                    speed_limit INTEGER,
                    max_connections INTEGER NOT NULL,
                    priority INTEGER NOT NULL,
                    error TEXT,
                    error_kind INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    completed_at TEXT,
                    next_segment_id INTEGER NOT NULL
                );
                CREATE TABLE segments (
                    job_id TEXT NOT NULL,
                    id INTEGER NOT NULL,
                    start_pos INTEGER NOT NULL,
                    end_pos INTEGER NOT NULL,
                    downloaded INTEGER NOT NULL,
                    state INTEGER NOT NULL,
                    PRIMARY KEY (job_id, id)
                );
                """);
        }

        if (version < 2)
        {
            // stream (HLS) downloads: what kind of job this is, and the quality chosen / progress so far
            Exec("ALTER TABLE jobs ADD COLUMN kind INTEGER NOT NULL DEFAULT 0;");
            Exec("ALTER TABLE jobs ADD COLUMN stream_data TEXT;");
        }

        Exec($"PRAGMA user_version={SchemaVersion};");
    }

    /// <param name="segments">
    /// Optional pre-captured segment copy. The runner captures progress first, flushes the file to disk, and only then
    /// persists the capture - so the database never claims bytes that are not on disk.
    /// </param>
    public void SaveJob(DownloadJob job, IReadOnlyList<Segment>? segments = null)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();

            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO jobs (id,url,final_url,referer,cookies,user_agent,headers,file_name,save_dir,category,probed,
                                      total_size,content_type,etag,last_modified,accepts_ranges,state,speed_limit,max_connections,
                                      priority,error,error_kind,created_at,completed_at,next_segment_id,kind,stream_data)
                    VALUES ($id,$url,$final,$ref,$cookies,$ua,$headers,$name,$dir,$cat,$probed,
                            $total,$ctype,$etag,$lm,$ranges,$state,$limit,$maxc,
                            $prio,$err,$errk,$created,$completed,$nextseg,$kind,$stream)
                    ON CONFLICT(id) DO UPDATE SET
                        url=$url, final_url=$final, referer=$ref, cookies=$cookies, user_agent=$ua, headers=$headers,
                        file_name=$name, save_dir=$dir, category=$cat, probed=$probed, total_size=$total, content_type=$ctype,
                        etag=$etag, last_modified=$lm, accepts_ranges=$ranges, state=$state, speed_limit=$limit,
                        max_connections=$maxc, priority=$prio, error=$err, error_kind=$errk, completed_at=$completed,
                        next_segment_id=$nextseg, kind=$kind, stream_data=$stream;
                    """;
                Bind(cmd, "$id", job.Id.ToString());
                Bind(cmd, "$url", job.Url);
                Bind(cmd, "$final", job.FinalUrl);
                Bind(cmd, "$ref", job.Referer);
                Bind(cmd, "$cookies", job.Cookies);
                Bind(cmd, "$ua", job.UserAgent);
                Bind(cmd, "$headers", job.Headers.Count == 0 ? null : JsonSerializer.Serialize(job.Headers));
                Bind(cmd, "$name", job.FileName);
                Bind(cmd, "$dir", job.SaveDirectory);
                Bind(cmd, "$cat", job.Category);
                Bind(cmd, "$probed", job.Probed ? 1 : 0);
                Bind(cmd, "$total", job.TotalSize);
                Bind(cmd, "$ctype", job.ContentType);
                Bind(cmd, "$etag", job.ETag);
                Bind(cmd, "$lm", job.LastModified?.ToString("O", CultureInfo.InvariantCulture));
                Bind(cmd, "$ranges", job.AcceptsRanges ? 1 : 0);
                Bind(cmd, "$state", (int)job.State);
                Bind(cmd, "$limit", job.SpeedLimit);
                Bind(cmd, "$maxc", job.MaxConnections);
                Bind(cmd, "$prio", job.Priority);
                Bind(cmd, "$err", job.Error);
                Bind(cmd, "$errk", (int)job.ErrorKind);
                Bind(cmd, "$created", job.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
                Bind(cmd, "$completed", job.CompletedAt?.ToString("O", CultureInfo.InvariantCulture));

                int nextSeg;
                lock (job.Sync) nextSeg = job.NextSegmentId;
                Bind(cmd, "$nextseg", nextSeg);
                Bind(cmd, "$kind", (int)job.Kind);
                Bind(cmd, "$stream", job.Hls?.ToJson());
                cmd.ExecuteNonQuery();
            }

            SaveSegments(job, tx, segments);
            tx.Commit();
        }
    }

    void SaveSegments(DownloadJob job, SqliteTransaction tx, IReadOnlyList<Segment>? captured)
    {
        var copy = captured ?? job.CaptureSegments();

        using (var del = _db.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM segments WHERE job_id=$id";
            del.Parameters.AddWithValue("$id", job.Id.ToString());
            del.ExecuteNonQuery();
        }

        // Completed jobs no longer need their segment table.
        if (job.State == DownloadState.Completed) return;

        using var ins = _db.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = "INSERT INTO segments (job_id,id,start_pos,end_pos,downloaded,state) VALUES ($j,$i,$s,$e,$d,$st)";
        var pj = ins.Parameters.Add("$j", SqliteType.Text);
        var pi = ins.Parameters.Add("$i", SqliteType.Integer);
        var ps = ins.Parameters.Add("$s", SqliteType.Integer);
        var pe = ins.Parameters.Add("$e", SqliteType.Integer);
        var pd = ins.Parameters.Add("$d", SqliteType.Integer);
        var pst = ins.Parameters.Add("$st", SqliteType.Integer);
        foreach (var s in copy)
        {
            pj.Value = job.Id.ToString();
            pi.Value = s.Id;
            ps.Value = s.Start;
            pe.Value = s.End;
            pd.Value = s.Downloaded;
            pst.Value = (int)s.State;
            ins.ExecuteNonQuery();
        }
    }

    public List<DownloadJob> LoadAll()
    {
        lock (_gate)
        {
            var jobs = new Dictionary<string, DownloadJob>();

            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT * FROM jobs ORDER BY created_at";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var id = r.GetString(r.GetOrdinal("id"));
                    var job = new DownloadJob
                    {
                        Id = Guid.Parse(id),
                        CreatedAt = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at")), CultureInfo.InvariantCulture),
                    };
                    job.Url = r.GetString(r.GetOrdinal("url"));
                    job.FinalUrl = Str(r, "final_url");
                    job.Referer = Str(r, "referer");
                    job.Cookies = Str(r, "cookies");
                    job.UserAgent = Str(r, "user_agent");
                    if (Str(r, "headers") is { } h)
                        job.Headers = JsonSerializer.Deserialize<Dictionary<string, string>>(h) ?? new();
                    job.FileName = r.GetString(r.GetOrdinal("file_name"));
                    job.SaveDirectory = r.GetString(r.GetOrdinal("save_dir"));
                    job.Category = r.GetString(r.GetOrdinal("category"));
                    job.Probed = r.GetInt32(r.GetOrdinal("probed")) != 0;
                    job.TotalSize = Long(r, "total_size");
                    job.ContentType = Str(r, "content_type");
                    job.ETag = Str(r, "etag");
                    if (Str(r, "last_modified") is { } lm)
                        job.LastModified = DateTimeOffset.Parse(lm, CultureInfo.InvariantCulture);
                    job.AcceptsRanges = r.GetInt32(r.GetOrdinal("accepts_ranges")) != 0;
                    job.State = (DownloadState)r.GetInt32(r.GetOrdinal("state"));
                    job.SpeedLimit = Long(r, "speed_limit");
                    job.MaxConnections = r.GetInt32(r.GetOrdinal("max_connections"));
                    job.Priority = r.GetInt32(r.GetOrdinal("priority"));
                    job.Error = Str(r, "error");
                    job.ErrorKind = (JobErrorKind)r.GetInt32(r.GetOrdinal("error_kind"));
                    if (Str(r, "completed_at") is { } ca)
                        job.CompletedAt = DateTimeOffset.Parse(ca, CultureInfo.InvariantCulture);
                    job.NextSegmentId = r.GetInt32(r.GetOrdinal("next_segment_id"));
                    job.Kind = (DownloadKind)r.GetInt32(r.GetOrdinal("kind"));
                    if (job.Kind == DownloadKind.Hls) job.Hls = ShadeNet.Core.Hls.HlsJobData.FromJson(Str(r, "stream_data"));
                    jobs[id] = job;
                }
            }

            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT job_id,id,start_pos,end_pos,downloaded,state FROM segments ORDER BY job_id,start_pos";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (!jobs.TryGetValue(r.GetString(0), out var job)) continue;
                    job.Segments.Add(new Segment
                    {
                        Id = r.GetInt32(1),
                        Start = r.GetInt64(2),
                        End = r.GetInt64(3),
                        Downloaded = r.GetInt64(4),
                        State = (SegmentState)r.GetInt32(5),
                    });
                }
            }

            return jobs.Values.ToList();
        }
    }

    public void Delete(Guid id)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            foreach (var table in new[] { "segments WHERE job_id", "jobs WHERE id" })
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"DELETE FROM {table}=$id";
                cmd.Parameters.AddWithValue("$id", id.ToString());
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    static string? Str(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    static long? Long(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetInt64(i);
    }

    static void Bind(SqliteCommand cmd, string name, object? value) =>
        cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

    void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    object? Scalar(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _db.Dispose();
        }
    }
}
