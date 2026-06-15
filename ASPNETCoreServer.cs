
using Core.DTOs;
using Core.IOOperation;
using Core.Models;
using Core.ViewModels;

using Microsoft.EntityFrameworkCore;

using PdfSharp.Pdf.Filters;

using SharpCompress.Archives;

using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Reflection.Emit;

namespace Server;

public class ASPNETCoreServer (ObservableCollectionVM collectionVM)
{
    Task? _serverTask;
    // ───────── 展示属性 ─────────
    public string Ip { get; private set; } = GetLocalIP();
    public int Port { get;  set; } = 12965; // 默认值，启动后可由实际值覆盖

    public string FullAddress => $"http://{Ip}:{Port}";
    public bool IsRunning => _serverTask?.Status == TaskStatus.Running;

    // ───────── 事件 ─────────
    public event Func <Manga,Task<bool>>? EventDeleteMang;
    public event Action<LogEntry>? AddLog;
    public event Func<IEnumerable<Manga>,Task>? MangasRequested;
    public ObservableCollection<LogEntry> Logs { get; } = [];
    private static readonly SemaphoreSlim _coverSemaphore = new(1 , 1);

    private static string GetLocalIP()
    {
        return Dns.GetHostEntry(Dns.GetHostName())
            .AddressList
            .First(ip => ip.AddressFamily == AddressFamily.InterNetwork)
            .ToString();
    }

    public void ValidatePort(int expectedPort = 12965)
    {
        if (Port != expectedPort)
            throw new InvalidOperationException($"端口不匹配！实际端口：{Port}，预设端口：{expectedPort}");
    }

    public async Task StartServer()
    {
        if (IsRunning) return;
        var builder = WebApplication.CreateBuilder();

        // 添加这行代码，允许同步 IO，downloads中使用的是同步方法
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AllowSynchronousIO = true;
        });
        //  注册 CORS 服务
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.AllowAnyOrigin() // 允许 WebAssembly 的地址
                      .AllowAnyMethod()
                      .AllowAnyHeader();
            });
        });

        // ... 构建 app



        var app = builder.Build();

        //  启用 CORS 中间件
        app.UseCors();


        app.Urls.Add("http://*:12965");
        MapHttpMethod(app);


        _serverTask = app.RunAsync();

    }

    private void MapHttpMethod(WebApplication app)
    {
        //中间件：记录日志
        app.Use(async (context, next) =>
        {
            var start = DateTime.Now;
            var method = context.Request.Method;
            var path = context.Request.Path;
            var clientIp = context.Connection.RemoteIpAddress;

            await next();

            var elapsed = DateTime.Now - start;
            var statusCode = context.Response.StatusCode;

            var entry = new LogEntry
            {
                Time = DateTime.Now,
                Level = "INFO",
                IPAddress = clientIp,
                 StatusCode = statusCode,
                  Method = method,
                   Path = path, ElapsedTime = elapsed,
            };
            AddLog?.Invoke(entry);
        });


        app.MapGet("/api/health", () => Results.Ok());
        app.MapGet("/tags", () => Results.Ok(collectionVM.AllTags));

        app.MapGet("/folders/basicinfo", () =>
        Results.Ok(collectionVM.MangasGroups.Select(x => new MangasGroupDTO(x))));

        app.MapGet("/folders/{guid}", (string guid) =>
        {
            var folder = collectionVM.MangasGroups.FirstOrDefault(x => x.Guid == guid);
            if (folder != null)
            {
                var folderDTO = new MangasGroupDTO(folder);
                return Results.Ok(folderDTO);
            }
            else
            {
                return Results.NotFound();
            }
        });
        app.MapGet("/folders/{guid}/count", (string guid) =>
        {
            var group = collectionVM.MangasGroups.FirstOrDefault(x => x.Guid == guid);
            if (group != null)
            {
                return Results.Ok(group.Count);
            }
            else
            {
                return Results.NotFound();
            }
        });
        _ = app.MapGet("/folders/{guid}/{index}/{amount}" , async (string guid , int index , int amount) =>
        {
            var group = collectionVM.MangasGroups.FirstOrDefault(x => x.Guid == guid);
            if (group != null)
            {
                var mangas = group.Mangas.Skip(index).Take(amount);
                await MangasRequested?.Invoke(mangas);
                var dtos = mangas.ToAsyncEnumerable();
                return Results.Ok(dtos);
            }
            else
            {
                return Results.NotFound();
            }
        });

        app.MapGet("/mangas", () => Results.Ok(collectionVM.MangaList));

        app.MapGet("/mangas/{guid}", (string guid) =>
        {
            var manga = collectionVM.MangaList.FirstOrDefault(x => x.Guid == guid);

            if (manga != null)
            {
                return Results.Ok(manga);
            }
            else
            {
                return Results.NotFound();
            }
        });


        _ = app.MapGet("downloads/{guid}" , async (string guid , HttpContext context) =>
        {
            var manga = collectionVM.MangaList.FirstOrDefault(x => x.Guid == guid);
            switch (manga.Type)
            {
                case "":
                    using (var archive = SharpCompress.Archives.Zip.ZipArchive.CreateArchive())
                    {
                        archive.AddAllFromDirectory(manga.FilePath);

                        //context.Response.ContentLength = manga.FileSize;
                        context.Response.Headers["X-Estimated-Size"] = manga.FileSize.ToString();
                        //context.Response.Headers.Append("X-Estimated-Size", manga.FileSize.ToString());
                        //context.Response.Headers.Append("Access-Control-Expose-Headers", "X-Estimated-Size");
                        archive.SaveTo(context.Response.Body , new SharpCompress.Writers.Zip.ZipWriterOptions(SharpCompress.Common.CompressionType.None));

                    }

                    //context.Response.Body.Position = 0;
                    // 结束响应
                    await context.Response.CompleteAsync();
                    return Results.Empty;
                default:
                    return Results.File(manga.FilePath);
            }
        });
        #region 另一种传输方式，有问题，会中断连接
        //有问题，会中断连接
        //       app.MapGet("/downloads2/{guid}", async (string guid, HttpContext context) =>
        //       {
        //           var manga = viewmodel.MangaList.FirstOrDefault(x => x.Guid == guid);
        //           if(manga != null && manga.Type != "")
        //           {
        //               return Results.File(manga.FilePath, contentType: "application/octet-stream", fileDownloadName: $"{manga.Guid}.zip");
        //           }

        //           //MemoryStream stream = new();
        //           //Stream outputStream = context.Response.Body;

        //                   // 1. 获取文件夹路径
        //                   var directoryPath = manga.FilePath;
        //                   if (!Directory.Exists(directoryPath)) return Results.NotFound();

        //           // 2. 获取所有图片文件 (支持常见格式，按需添加)
        //           var files = Directory.GetFiles(directoryPath, "*", new EnumerationOptions() { RecurseSubdirectories = true })
        //                                //.Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        //                                //            f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        //                                //            f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
        //                                .OrderBy(f => f) // 建议排序，保证压缩包内顺序一致
        //   .ToList();
        //                  // 3. 设置响应头
        //                   context.Response.ContentType = "application/zip";
        //           context.Response.ContentLength = manga.FileSize;

        //           context.Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{manga.Guid}.zip\"");
        //                   // 4. 创建 ZipArchive (直接写入 Response.Body)
        //                   // 注意：这里不需要 using，因为当方法结束时，ASP.NET Core 会自动关闭 Response.Body
        //                   using (var archive = new ZipArchive(context.Response.Body, ZipArchiveMode.Create, leaveOpen: true))
        //                   {
        //                       foreach (var filePath in files)
        //                       {
        //                           // 5. 为每个文件创建一个 Zip 条目
        //                           // 这里使用 Path.GetFileName 确保压缩包里没有绝对路径
        //                           var entry = archive.CreateEntry(Path.GetFileName(filePath), CompressionLevel.NoCompression);

        //                           // 6. 打开源文件的读取流 (File.OpenRead 是异步友好的)
        //                           using (var fileStream = File.OpenRead(filePath))
        //                           {
        //                               // 7. 打开 Zip 条目的写入流
        //                               using (var entryStream = entry.Open())
        //                               {
        //                                   // 8. 核心：将文件流异步复制到 Zip 流中
        //                                   // 这一步是“流式”的关键：读一点 -> 压一点 -> 发一点
        //                                   await fileStream.CopyToAsync(entryStream);
        //                               }
        //                           }
        //                       }
        //                   }
        //           context.Response.Body.Position = 0;
        //                   // 结束响应
        //                   //await context.Response.CompleteAsync();
        //           return Results.Empty;
        //       }
        //);
        #endregion
        //app.MapGet("/covers/all", () => Results.Ok(viewmodel.MangaList.Select(x => x.CoverUri)));
        app.MapGet("/covers/{guid}", async (string guid,HttpContext httpContext) =>
        {
                var file = collectionVM.MangaList.SingleOrDefault(x => x.Guid == guid);
                var cover = file?.CoverUri;

            return File.Exists(cover) ? Results.File(cover) : Results.NotFound();



        });

        app.MapDelete("/mangas/{guid}" , async (string guid) =>
        {
            var manga = collectionVM.MangaList.SingleOrDefault(x => x.Guid == guid);
            if (manga is null)
                return Results.NotFound();

            if (EventDeleteMang is null)
                return Results.StatusCode(500);

            try
            {
                var success = await EventDeleteMang.Invoke(manga);
                return success ? Results.Ok() : Results.StatusCode(500);
            }
            catch
            {
                return Results.StatusCode(500);
            }
        });
    }
}
