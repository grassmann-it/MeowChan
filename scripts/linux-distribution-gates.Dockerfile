# Native Ubuntu 24.04 acceptance desktops. Package creation and apt downloads
# happen outside the network-disabled acceptance containers.
FROM ubuntu:24.04 AS no-vlc
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update \
    && apt-get install --yes --no-install-recommends \
       python3 xvfb xauth openbox x11-utils dbus-daemon \
       ca-certificates fonts-dejavu-core libdbus-1-3 libx11-6 libice6 libsm6 \
       libfontconfig1 libglib2.0-0t64 libgl1 libopengl0 libegl1 libxkbcommon0 \
       libxcb1 libxrandr2 libxcursor1 libxi6 libxrender1 libxext6 libasound2t64 \
       libicu74 libssl3t64 zlib1g libgcc-s1 libstdc++6 libkrb5-3 \
    && rm -rf /var/lib/apt/lists/*

# Kept separate: bundled/AppImage acceptance must never inherit these libraries.
FROM no-vlc AS system-vlc
RUN apt-get update \
    && apt-get install --yes --no-install-recommends vlc libvlc5 libvlccore9 \
    && rm -rf /var/lib/apt/lists/*
