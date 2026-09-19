# Third-party notices

## Robust Video Matting

This project includes `rvm_mobilenetv3_960x540_s0.25_int8.mlmodel`, a derived model generated from the official Robust Video Matting weights and exporter:

- Project: Robust Video Matting
- Authors: Shanchuan Lin, Linjie Yang, Imran Saleemi, and Soumyadip Sengupta
- Repository: https://github.com/PeterL1n/RobustVideoMatting
- Weights: official MobileNetV3 checkpoint from release v1.0.0 (SHA-256 `3c7c1d92033f7c38d6577c481d13a195d7d80a159b960f4f3119ac7b534cf4f8`)
- Exporter revision: `b4850905347f4fcc588b5f7ed7cbfd34ae206436`
- Upstream license: GNU General Public License v3.0
- Model configuration: MobileNetV3, deep guided filter, 960 × 540, downsample ratio 0.25, INT8
- Export environment: Torch 1.8.1, Torchvision 0.9.1, CoreMLTools 5.0b1
- Generated model SHA-256: `6b4ee7da140911480c9c8d334faa875b28ce622952e8b9ec7bb25df92959108c`

The upstream copyright and license remain with their respective owners.
