#!/bin/zsh

set -euo pipefail

project_dir="${0:A:h}"
native_dir="$project_dir/Plugin"
source_files=(
    "$native_dir/RVMPlugin.mm"
    "$native_dir/RVMContext.mm"
    "$native_dir/RVMModel.mm"
    "$native_dir/RVMAlphaTexturePool.mm"
)
plist_file="$native_dir/Info.plist"
bundle_dir="$project_dir/Assets/Plugins/macOS/RVMPlugin.bundle"
binary_dir="$bundle_dir/Contents/MacOS"
unity_version=$(sed -n 's/^m_EditorVersion: //p' "$project_dir/ProjectSettings/ProjectVersion.txt")
plugin_api_dir="/Applications/Unity/Hub/Editor/$unity_version/Unity.app/Contents/Resources/PluginAPI"

if [[ ! -f "$plugin_api_dir/IUnityGraphicsMetal.h" ]]; then
    echo "Unity Metal plugin headers were not found at $plugin_api_dir" >&2
    exit 1
fi

mkdir -p "$binary_dir"
cp "$plist_file" "$bundle_dir/Contents/Info.plist"

xcrun --sdk macosx clang++ \
    -std=c++17 \
    -fobjc-arc \
    -fvisibility=hidden \
    -bundle \
    -arch arm64 \
    -arch x86_64 \
    -mmacosx-version-min=13.0 \
    -I "$plugin_api_dir" \
    -framework Foundation \
    -framework CoreML \
    -framework CoreVideo \
    -framework IOSurface \
    -framework Metal \
    "${source_files[@]}" \
    -o "$binary_dir/RVMPlugin"

codesign --force --sign - "$bundle_dir"
echo "Built $bundle_dir"
