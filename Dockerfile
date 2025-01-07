FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /build

ARG BUILD_VERSION="1.0.0.0"

COPY . .

RUN dotnet publish -c Release src/reactabot.csproj -o /build/output /p:BuildVersion=${BUILD_VERSION}

FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine

RUN apk --no-cache add icu-libs
WORKDIR /dotnetapp

COPY --from=build /build/output/ .
ENV DOCKER_HEALTHCHECK_FILEPATH=/dotnetapp/health.txt

HEALTHCHECK CMD test $(find $DOCKER_HEALTHCHECK_FILEPATH -mmin -3 | wc -l) -gt 0
ENTRYPOINT ["dotnet", "reactabot.dll"]