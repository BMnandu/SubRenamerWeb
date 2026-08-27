using System.Text.Json;
using System.Text.Json.Serialization;
using SubRenamer.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(
        new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// 字幕文件一般很小,放宽上传限制以应对批量/打包场景
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 200_000_000; // 200MB
});

// 路径配置(通过环境变量注入,容器挂载点)
builder.Services.AddSingleton(new AppPaths(
    MediaDir: NormalizeDir(builder.Configuration["MEDIA_DIR"] ?? "/media"),
    UploadDir: NormalizeDir(builder.Configuration["UPLOAD_DIR"] ?? "/uploads"),
    WorkDir: NormalizeDir(builder.Configuration["WORK_DIR"] ?? "/work")
));
builder.Services.AddSingleton(SyncRuntimeOptions.FromConfiguration(builder.Configuration));

builder.Services.AddSingleton<FileScanService>();
builder.Services.AddSingleton<SafePathService>();
builder.Services.AddSingleton<SubtitleNamingService>();
builder.Services.AddSingleton<SyncPlanService>();
builder.Services.AddSingleton<ISyncProcessRunner, FfsubsyncProcessRunner>();
builder.Services.AddSingleton<RenameService>();
builder.Services.AddSingleton<UploadService>();
builder.Services.AddSingleton<SubSyncService>();

var app = builder.Build();

// 启动时确保上传目录存在
var paths = app.Services.GetRequiredService<AppPaths>();
Directory.CreateDirectory(paths.UploadDir);
Directory.CreateDirectory(paths.EffectiveWorkDir);

app.UseStaticFiles();
app.UseSwagger();
app.UseSwaggerUI(c => c.RoutePrefix = "api/docs");

app.MapControllers();
app.MapFallbackToFile("index.html");

app.Run();

static string NormalizeDir(string dir) =>
    string.IsNullOrEmpty(dir) ? dir : dir.TrimEnd('/');
