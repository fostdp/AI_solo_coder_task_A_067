FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["AlCellControl.csproj", "./"]
RUN dotnet restore "AlCellControl.csproj"

COPY . .
RUN dotnet publish "AlCellControl.csproj" \
    -c Release -o /app/publish \
    --no-restore \
    /p:PublishTrimmed=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN adduser --disabled-password --gecos "" appuser
USER appuser

COPY --from=build /app/publish .

EXPOSE 5000

ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_EnableDiagnostics=0

ENTRYPOINT ["dotnet", "AlCellControl.dll"]
