using FmsOnboard.Collector;
using FmsOnboard.Core;

var cfg = AppConfig.FromEnv();
DatabaseService.InitializeDb(cfg.DbFile);

var lockPath = Path.Combine(cfg.BaseDir, "collector.lock");
using var lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(cfg);
builder.Services.AddHostedService<CollectorWorker>();

var host = builder.Build();
host.Run();
