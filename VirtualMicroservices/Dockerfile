FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["VirtualMicroservices.csproj", "./"]
RUN dotnet restore "VirtualMicroservices.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "VirtualMicroservices.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "VirtualMicroservices.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "VirtualMicroservices.dll"]
