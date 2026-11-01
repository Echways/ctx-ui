FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG VERSION=0.0.0
WORKDIR /source
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY extensions/builder/Directory.Build.props extensions/builder/
COPY extensions/builder/CtxUi.Builder.Contracts/ extensions/builder/CtxUi.Builder.Contracts/
COPY extensions/builder/CtxUi.Builder.Runner/ extensions/builder/CtxUi.Builder.Runner/
RUN dotnet publish extensions/builder/CtxUi.Builder.Runner -c Release -a $TARGETARCH -p:Version=$VERSION -p:DebugType=none -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS= \
    DOTNET_gcServer=0
EXPOSE 8090 8091
ENTRYPOINT ["dotnet", "CtxUi.Builder.Runner.dll"]
