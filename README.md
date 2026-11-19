# FMS Onboard Telemetry (.NET 8 + SignalR)

## Prasyarat Sistem
- .NET 8 SDK / Runtime (`dotnet --version`).
- ODBC Driver 17 for SQL Server terinstal di host OS (`unixODBC` + `msodbcsql17` di Linux).
- File `signalr.min.js` v8 diletakkan di `FmsOnboard.Web/wwwroot/js/signalr.min.js` (unduh via `npm i @microsoft/signalr` dari `node_modules/@microsoft/signalr/dist/browser/signalr.min.js`).
- Salin `.env.example` menjadi `.env`, serta tempatkan `query_machine_ips.sql` dan `query_gps.sql` ke working directory saat menjalankan binary.

## Build Release
```bash
dotnet publish FmsOnboard.Collector -c Release -o ./publish/collector
dotnet publish FmsOnboard.Web -c Release -o ./publish/web
dotnet publish FmsOnboard.Backup -c Release -o ./publish/backup
```

## Menjalankan Layanan
### Collector ICMP + GPS
- Linux:
  ```bash
  sudo setcap cap_net_raw+ep ./publish/collector/FmsOnboard.Collector
  ./publish/collector/FmsOnboard.Collector
  ```
- Windows (Administrator):
  - run dashboard: dotnet FmsOnboard.Web\bin\Release\net8.0\FmsOnboard.Web.dll --contentroot C:\NET8ONB\FmsOnboard.Web
  - run collector: dotnet FmsOnboard.Collector\bin\Release\net8.0\FmsOnboard.Collector.dll

### Web Dashboard & SignalR
```bash
./publish/web/FmsOnboard.Web
```

### Backup SQLite (Cron / Task Scheduler)
```bash
./publish/backup/FmsOnboard.Backup
```
