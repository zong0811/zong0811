# 1. 編譯階段 (使用 .NET 10.0 SDK)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 複製專案檔並還原套件
COPY ["linebot.csproj", "./"]
RUN dotnet restore

# 複製其餘程式碼並發布
COPY . .
RUN dotnet publish "linebot.csproj" -c Release -o /app/out

# 2. 執行階段 (使用 .NET 10.0 Runtime)
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# 從編譯階段複製成品
COPY --from=build /app/out .

# Render 環境設定
ENV ASPNETCORE_URLS=http://+:80
EXPOSE 80

# 【關鍵修正】：檔名必須與 .csproj 一致，所以是 linebot.dll
ENTRYPOINT ["dotnet", "linebot.dll"]
