"""Package only the plugin DLL and metadata, never the server's API assemblies."""
import json
import pathlib
import zipfile
import xml.etree.ElementTree as ET

root = pathlib.Path(__file__).resolve().parents[1]
output = root / 'artifacts/plugin'
version = ET.parse(root / 'src/StreamFin.csproj').findtext('.//Version')
manifest = {
    'guid': 'a74fdb2e-938f-483b-b2ef-f527e14ff724',
    'name': 'StreamFin', 'description': 'Stream anime, movies and TV from online sources.',
    'overview': 'Browse the Anime and Movies & TV channels and stream with Jellyfin.',
    'owner': 'mmrmagno', 'version': version, 'targetAbi': '12.3.0.0',
    'status': 'Active', 'autoUpdate': False,
}
(output / 'meta.json').write_text(json.dumps(manifest, indent=2) + '\n')
with zipfile.ZipFile(root / f'artifacts/StreamFin-{version}.zip', 'w', zipfile.ZIP_DEFLATED) as archive:
    for name in ['Jellyfin.Plugin.StreamFin.dll', 'meta.json']:
        archive.write(output / name, name)
    for name in ['LICENSE', 'THIRD_PARTY_NOTICES.txt', 'APACHE-2.0.txt']:
        archive.write(root / name, name)
print(f'Packaged artifacts/StreamFin-{version}.zip')
