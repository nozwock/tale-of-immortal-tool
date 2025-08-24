default: release

release:
    dotnet publish -c Release --self-contained false /p:PublishSingleFile=true
