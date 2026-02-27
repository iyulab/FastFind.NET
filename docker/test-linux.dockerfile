FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ .
RUN dotnet restore FastFind.sln
RUN dotnet build FastFind.sln --configuration Release --no-restore

FROM build AS test
# Create test file corpus
RUN mkdir -p /test-data && \
    for i in $(seq 1 1000); do \
        dir="/test-data/dir_$((i % 100))"; \
        mkdir -p "$dir"; \
        dd if=/dev/zero of="$dir/file_$i.txt" bs=1 count=$((RANDOM % 1000 + 1)) 2>/dev/null; \
    done
RUN dotnet test FastFind.Unix.Tests/ --configuration Release --no-build --filter "Category!=Performance" --logger "trx;LogFileName=results.trx" --results-directory /results

FROM scratch AS results
COPY --from=test /results/ /
