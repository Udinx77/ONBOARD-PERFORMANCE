using System.Globalization;
using FmsOnboard.Core;
using Microsoft.Data.Sqlite;

var cfg = AppConfig.FromEnv();
if (!File.Exists(cfg.DbFile))
    throw new FileNotFoundException($"Database tidak ditemukan: {cfg.DbFile}");

var backupDirRaw = Environment.GetEnvironmentVariable("BACKUP_DIR")?.Trim();
var backupDir = string.IsNullOrEmpty(backupDirRaw)
    ? Path.Combine(cfg.BaseDir, "backups")
    : (Path.IsPathRooted(backupDirRaw) ? backupDirRaw : Path.Combine(cfg.BaseDir, backupDirRaw));

int keep = int.TryParse(Environment.GetEnvironmentVariable("BACKUP_KEEP")?.Trim(), out int k) ? k : 14;

Directory.CreateDirectory(backupDir);
var filename = $"monitoring_{DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.db";
var target = Path.Combine(backupDir, filename);

using (var src = new SqliteConnection($"Data Source={cfg.DbFile};Mode=ReadOnly;"))
using (var dst = new SqliteConnection($"Data Source={target};"))
{
    src.Open();
    dst.Open();
    src.BackupDatabase(dst);
}

if (keep > 0)
{
    var files = Directory.GetFiles(backupDir, "monitoring_*.db")
        .OrderBy(f => f, StringComparer.Ordinal)
        .ToList();

    if (files.Count > keep)
    {
        foreach (var old in files.Take(files.Count - keep))
        {
            try { File.Delete(old); } catch { }
        }
    }
}

Console.WriteLine($"Backup selesai: {target}");
