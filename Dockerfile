# 1. 編譯階段
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 使用萬用字元 *.csproj，這樣不管你的專案叫什麼名字都能抓到
COPY *.csproj ./
RUN dotnet restore

# 複製所有檔案並編譯
COPY . .
# 修正：明確發布到 /app/out 資料夾
RUN dotnet publish -c Release -o /app/out

# 2. 執行階段
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# 從編譯階段的 /app/out 複製成品
COPY --from=build /app/out .

# Render 建議環境設定 (Render 預設會找 80 或 10000 埠)
ENV ASPNETCORE_URLS=http://+:80
EXPOSE 80

# 關鍵修正：DLL 名稱通常等於 Namespace 或 .csproj 檔名
# 根據你的程式碼，這裡應該改為 isRock.Template.dll
ENTRYPOINT ["dotnet", "isRock.Template.dll"]
