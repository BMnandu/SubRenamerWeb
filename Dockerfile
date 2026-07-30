# ---- 构建阶段 ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 先拷 csproj 利用缓存
COPY src/SubRenamer.Core/SubRenamer.Core.csproj SubRenamer.Core/
COPY src/SubRenamer.Web/SubRenamer.Web.csproj SubRenamer.Web/
RUN dotnet restore SubRenamer.Web/SubRenamer.Web.csproj

# 拷贝源码并发布
COPY src/ .
RUN dotnet publish SubRenamer.Web/SubRenamer.Web.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---- 运行阶段 ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# 先装系统依赖(不依赖 build 产物,缓存稳定;改代码不会重新装这一层)
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg python3 python3-pip \
    && pip3 install --no-cache-dir --break-system-packages ffsubsync \
    && rm -rf /var/lib/apt/lists/*

# 再 COPY build 产物(代码变只重新跑这层 + chmod,不触发 apt 重装)
COPY --from=build /app/publish .

# 确保所有运行用户(UID 由 compose 控制)都能读取应用文件(NAS 源文件权限可能较严)
RUN chmod -R a+rX /app && mkdir -p /media /uploads /config

ENV MEDIA_DIR=/media \
    UPLOAD_DIR=/uploads \
    ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080
ENTRYPOINT ["dotnet", "SubRenamer.Web.dll"]