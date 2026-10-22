#!/bin/bash
# Builds the FFmpeg that ODM's player uses into libs/ffmpeg: an LGPL build with just
# what it needs - RTSP in, H.265 / H.264 / MJPEG / MPEG-4 decoding, and scaling to the
# display format.
#
# Needs MSYS2 (bash, make) started from a Visual Studio x64 command prompt
# (vcvars64.bat, then: set MSYS2_PATH_TYPE=inherit & C:\msys64\usr\bin\bash.exe -l),
# and nasm on the PATH.
#
# Usage: tools/build-ffmpeg.sh path/to/ffmpeg-7.0.1     (the release tarball, unpacked)
set -e
src="$(cd "$1" && pwd)"
repo="$(cd "$(dirname "$0")/.." && pwd)"
cd "$src"
./configure --toolchain=msvc --arch=x86_64 --target-os=win64 \
  --prefix="$repo/libs/ffmpeg" \
  --enable-shared --disable-static --disable-programs --disable-doc \
  --disable-everything --disable-avdevice --disable-avfilter --disable-swresample \
  --enable-network --enable-protocol=tcp,udp,rtp \
  --enable-demuxer=rtsp,sdp,rtp \
  --enable-decoder=hevc,h264,mjpeg,mpeg4 \
  --enable-parser=hevc,h264,mjpeg,mpeg4video \
  --enable-d3d11va --enable-dxva2 \
  --enable-hwaccel=hevc_d3d11va,h264_d3d11va,hevc_dxva2,h264_dxva2
make -j"$(nproc)"
make install
echo "FFmpeg installed in $repo/libs/ffmpeg"
