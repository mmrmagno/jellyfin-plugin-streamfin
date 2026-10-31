#!/bin/bash -eu
targets=(hls_media hls_master hls_text flix_player flix_pages anime_pages hianime_config text tmdb music lyrics subsonic gain radio ytdlp deezer)
dotnet publish fuzz/StreamFin.Fuzz.csproj -c Release -r linux-x64 --self-contained \
  -p:JellyfinReferencePath=/opt/jellyfin -o "$OUT/streamfin-fuzz"
DOTNET_ROLL_FORWARD=Major sharpfuzz "$OUT/streamfin-fuzz/Jellyfin.Plugin.StreamFin.dll"
for target in "${targets[@]}"; do
  $CXX $CXXFLAGS -I/opt/libfuzzer-dotnet -DSTREAMFIN_TARGET="\"$target\"" .clusterfuzzlite/driver.cc $LIB_FUZZING_ENGINE -o "$OUT/$target"
  zip -qj "$OUT/${target}_seed_corpus.zip" fuzz/corpus/"$target"/*
done
cp fuzz/hls.dict "$OUT/hls_media.dict"
cp fuzz/hls.dict "$OUT/hls_master.dict"
cp fuzz/html.dict "$OUT/flix_player.dict"
cp fuzz/html.dict "$OUT/flix_pages.dict"
cp fuzz/html.dict "$OUT/anime_pages.dict"
