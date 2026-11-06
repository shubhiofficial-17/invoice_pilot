FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY InvoicePilot.Api/InvoicePilot.Api.csproj InvoicePilot.Api/
RUN dotnet restore InvoicePilot.Api/InvoicePilot.Api.csproj
COPY InvoicePilot.Api/ InvoicePilot.Api/
RUN dotnet publish InvoicePilot.Api/InvoicePilot.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "InvoicePilot.Api.dll"]
