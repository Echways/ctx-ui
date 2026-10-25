FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG VERSION=0.0.0
WORKDIR /source
COPY Directory.Build.props Directory.Packages.props ./
COPY src/CtxUi.Core/CtxUi.Core.csproj src/CtxUi.Core/
COPY src/CtxUi.Connect/CtxUi.Connect.csproj src/CtxUi.Connect/
COPY src/CtxUi.Gateway/CtxUi.Gateway.csproj src/CtxUi.Gateway/
COPY src/CtxUi.Panel/CtxUi.Panel.csproj src/CtxUi.Panel/
COPY src/CtxUi.Server/CtxUi.Server.csproj src/CtxUi.Server/
RUN dotnet restore src/CtxUi.Server -a $TARGETARCH
COPY src/ src/
RUN dotnet publish src/CtxUi.Server -c Release -a $TARGETARCH --no-restore -p:Version=$VERSION -p:DebugType=none -o /app \
    && mkdir /data

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY --from=build --chown=$APP_UID:$APP_UID --chmod=700 /data /data
ENV PATH=/app:$PATH \
    Urls=http://0.0.0.0:8080 \
    CtxUi__DataDir=/data
VOLUME /data
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --start-interval=2s CMD ["ctx-ui", "health"]
ENTRYPOINT ["ctx-ui"]
