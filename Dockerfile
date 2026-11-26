FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY StaffBot.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish StaffBot.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
# База SQLite хранится в /data — подключите к этому пути Volume в Railway
ENV DB_PATH=/data/staff.db
RUN mkdir -p /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "StaffBot.dll"]
