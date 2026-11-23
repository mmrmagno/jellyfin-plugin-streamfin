FROM jellyfin/jellyfin@sha256:fea863d0cd9ec445985d9c75fc6ed64e38a1d2766cf0c250a33d553f4fc1b771 AS jellyfin
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS dotnet
RUN dotnet tool install --tool-path /opt/sharpfuzz SharpFuzz.CommandLine --version 2.2.0

FROM gcr.io/oss-fuzz-base/base-builder@sha256:1f9cf74bc3d7029b528494542f9d9851b256dceed38d3f420989434dc5ae0760
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 PATH=/usr/share/dotnet:/opt/sharpfuzz:$PATH
COPY --from=dotnet /usr/share/dotnet /usr/share/dotnet
COPY --from=dotnet /opt/sharpfuzz /opt/sharpfuzz
COPY --from=jellyfin /jellyfin/MediaBrowser.Common.dll /jellyfin/MediaBrowser.Controller.dll /jellyfin/MediaBrowser.Model.dll /jellyfin/Jellyfin.Data.dll /jellyfin/Jellyfin.Database.Implementations.dll /jellyfin/Jellyfin.Extensions.dll /jellyfin/Emby.Naming.dll /opt/jellyfin/
ADD --checksum=sha256:90f019e2e9ad3a0b93c7ecc2c5afb2fbfc8b5aab6aac51c7e0d349ec79354f36 https://raw.githubusercontent.com/Metalnem/libfuzzer-dotnet/bd39d4e88d715ab460a929943645be2a186cde52/libfuzzer-dotnet.cc /opt/libfuzzer-dotnet/libfuzzer-dotnet.cc
COPY . $SRC/streamfin
WORKDIR $SRC/streamfin
COPY .clusterfuzzlite/build.sh $SRC/
