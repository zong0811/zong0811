# 使用 .NET SDK 進行編譯 
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# --- 修正點：使用 *.csproj 確保能抓到檔案 ---
COPY ["*.csproj", "./"]
RUN dotnet restore

# 複製其餘內容並編譯
COPY . .
RUN dotnet build "*.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "*.csproj" -c Release -o /app/publish

# 建立執行環境
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .

# --- 重要：請確認你的輸出 dll 名稱 ---
# 如果不確定名稱，可以先改用以下這行來啟動（假設編譯出來的 dll 跟資料夾同名）
ENTRYPOINT ["dotnet", "linebot.dll"] 
# 註：如果還是噴錯，請檢查你的專案檔名稱究竟是什麼
