# Third-party notices

## Shuriken icon

`src/Assets/shuriken.svg` is the Material Design Icons **shuriken** icon by
Michael Richins, from https://github.com/Templarian/MaterialDesign.
Copyright the Pictogrammers contributors. Licensed under Apache-2.0;
see `APACHE-2.0.txt`. `anime.svg` and `anime.png` add AniFin colors and a background.

## AnimeGG provider

`src/AnimeGgSource.cs` adapts the search/episode/embed resolution approach from
https://github.com/Seanime-contributions/Seanime-Providers/tree/main/src/anime/animegg
(provider version 1.0.1, inspected 2026-10-06). The C# implementation adds request
validation, cancellation, numeric sorting, strict subtitle selection, and explicit errors.

MIT License

Copyright (c) 2025 Pal

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Jellyfin

AniFin references Jellyfin's server API assemblies, which are GPL-3.0 licensed.
Those assemblies are obtained from the pinned upstream image for compilation
and are not included in the AniFin plugin ZIP.
