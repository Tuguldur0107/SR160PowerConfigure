#!/bin/bash
echo "Building SR160PowerConfig (cross-platform)..."
echo

dotnet build -c Release

if [ $? -eq 0 ]; then
    echo
    echo "BUILD SUCCESSFUL"
    echo "Output: bin/Release/net8.0/SR160PowerConfig"
else
    echo
    echo "BUILD FAILED"
fi
