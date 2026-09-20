#!/bin/zsh

set -euo pipefail

project_dir="${0:A:h}"
model_dir="$project_dir/Packages/jp.keijiro.rvm-coreml/Runtime/Models"
model_name="rvm_mobilenetv3_1280x720_s0.375_int8.mlmodel"
model_url="https://github.com/PeterL1n/RobustVideoMatting/releases/download/v1.0.0/$model_name"
model_sha256="68efe6e7a23d5337fb4f935f77e83b0ec3cc823803083953eb18f4cc0549d794"
destination="$model_dir/$model_name"

mkdir -p "$model_dir"
work_dir=$(mktemp -d)
trap 'rm -rf "$work_dir"' EXIT

curl -fL --retry 3 --output "$work_dir/$model_name" "$model_url"
actual_sha256=$(shasum -a 256 "$work_dir/$model_name" | awk '{print $1}')
if [[ "$actual_sha256" != "$model_sha256" ]]; then
    echo "Model checksum mismatch: expected $model_sha256, got $actual_sha256" >&2
    exit 1
fi

mv "$work_dir/$model_name" "$destination"
echo "Installed verified model at $destination"
