FROM --platform=$BUILDPLATFORM node:24-bookworm-slim AS styles
WORKDIR /source
COPY src/ISLA/package*.json ./
RUN npm ci --no-audit --no-fund
COPY src/ISLA/Components/ ./Components/
COPY src/ISLA/Styles/ ./Styles/
RUN npm run css:build

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json ./
COPY src/ISLA/ISLA.csproj src/ISLA/
RUN dotnet restore src/ISLA/ISLA.csproj
COPY src/ISLA/ src/ISLA/
COPY --from=styles /source/wwwroot/app.css src/ISLA/wwwroot/app.css
RUN dotnet publish src/ISLA/ISLA.csproj -c Release --no-restore -p:SkipTailwind=true -o /app/publish /p:UseAppHost=false
# Fail the image build if the interactive UI's startup script was omitted.
RUN test -s /app/publish/wwwroot/_framework/blazor.web.js

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
ENV DataDirectory=/app/data
RUN mkdir /app/data && chown app:app /app/data
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "ISLA.dll"]
