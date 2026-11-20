"""Assemble redistributable Windows package from same-pipeline build artifacts."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import zipfile

# Never packaged: game executables and data, firmware, logs (and symbols, except the verified ones).
FORBIDDEN = {'.self', '.elf', '.sprx', '.pkg', '.vol', '.adc', '.log', '.pdb'}
# FEAT-GTS-263: what ci/build-windows.ps1 -Target Tools produces (adcpatch and AMD's FidelityFX runtime).
TOOLS_FILES = ('adcpatch.exe', 'grim.ad', 'openGrim.ad', 'adcpatch-README.txt', 'GTAdhocToolchain-LICENSE.txt',
               'amd_fidelityfx_vk.dll', 'amd_fidelityfx_vk-LICENSE.txt')
# FEAT-RT-006: GT7's own built files, also from the Tools target, and where they go: one RyGTS folder runs
# both games (FEAT-RT-005), so the package carries what only ryGT7's package had.
GT7_TOOLS_FILES = (('libSceTextToSpeech.prx', 'libs/libSceTextToSpeech.prx'),  # GT7's stubs (runtime/gt7)
                   ('RyGTServer.exe', 'server/RyGTServer.exe'))               # online play (server/)
# FEAT-RT-006: the server's zstd, from the Runtime target's pinned toolchain.
GT7_RUNTIME_FILES = (('libzstd.dll', 'server/libzstd.dll'), ('zstd-LICENSE.txt', 'server/libzstd-LICENSE.txt'))
# FEAT-RT-006: folders of GT7's files taken from this repository as they are tracked (shadNet's committed
# shadnet.cfg has no keys).
REPOSITORY_TREES = (('server/shadnet', 'server/shadnet'), ('server/data', 'server/server-data'))
# Files that come from this repository.
REPOSITORY_FILES = (('loader/launcher/background.png', 'launcher/background.png'),
                    ('loader/launcher/README.txt', 'launcher/README.txt'),
                    ('docs/user/LINUX.md', 'LINUX.txt'),
                    ('docs/user/CAMERA_MANAGER.md', 'CAMERA_MANAGER.txt'),
                    ('ci/packaging/CUSA03220.json', 'user/custom_configs/CUSA03220.json'),
                    ('tools/amd/Add-To-AMD-Software.ps1', 'tools/amd/Add-To-AMD-Software.ps1'),
                    ('LICENSE', 'LICENSE'),
                    # FEAT-GTS-287: the DLSS plugin's own licence (MIT; dlss\ holds only the DLL).
                    ('plugins/dlss/LICENSE', 'ryGT_dlss-LICENSE.txt'),
                    # FEAT-RT-006: GT7's offline patch and the server's notes.
                    ('patches/CUSA24769.xml', 'patches/CUSA24769.xml'),
                    ('patches/README.md', 'patches/README.md'),
                    ('patches/Proper-Offline-Patch-README.txt', 'patches/Proper-Offline-Patch-README.txt'),
                    ('server/README.md', 'server/README.md'))


# What a shadNet or RyGTServer run leaves in those folders (a developer's checkout): never packaged.
TREE_SKIP_DIRS = {'db', 'score_data', 'tss_data', 'logs', 'captures', 'responses', 'rooms'}
TREE_SKIP_SUFFIXES = ('.log', '.running', '.db', '.db-shm', '.db-wal', '.token')


def repository_tree_files(root):
    """(file in the repository, its place in the package) for every file of REPOSITORY_TREES."""
    files = []
    for folder, target in REPOSITORY_TREES:
        found = sorted(path for path in (root / folder).rglob('*') if path.is_file() and
                       not TREE_SKIP_DIRS & set(path.relative_to(root / folder).parts[:-1]) and
                       not path.name.endswith(TREE_SKIP_SUFFIXES))
        if not found:
            raise RuntimeError(f'No files in {folder}')
        files += [(path.relative_to(root).as_posix(), target + '/' + path.relative_to(root / folder).as_posix())
                  for path in found]
    return files


def launcher_version(source_root):
    """LauncherVersion.Value from loader/Version.cs, or None when the source has no such file."""
    path = Path(source_root) / 'loader/Version.cs'
    if not path.is_file():
        return None
    match = re.search(r'public const string Value = "([^"]+)";', path.read_text(encoding='utf-8-sig'))
    return match.group(1) if match else None


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def write_packaged_defaults(stage):
    config_path = stage / 'user/custom_configs/CUSA03220.json'
    config = json.loads(config_path.read_text(encoding='utf-8-sig'))
    config.setdefault('GPU', {})['precompile_shaders'] = True
    config.setdefault('Vulkan', {})['pipeline_cache_enabled'] = True
    config_path.write_text(json.dumps(config, indent=2) + '\n', encoding='utf-8')


def verify_artifact(folder, filename, commit, kind="pe"):
    actual_commit = (folder / 'RYGT-COMMIT.txt').read_text(encoding='utf-8-sig').strip()
    if actual_commit != commit:
        raise RuntimeError(f'Artifact revision mismatch: {folder}')
    entries = {}
    for line in (folder / 'SHA256SUMS.txt').read_text(encoding='utf-8-sig').splitlines():
        digest, name = line.split('  ', 1)
        if (Path(name).name != name or any(c in name for c in ('/', '\\', ':')) or name in entries
                or not re.fullmatch('[0-9a-fA-F]{64}', digest)):
            raise RuntimeError('Invalid artifact manifest entry')
        if sha(folder / name) != digest.lower():
            raise RuntimeError(f'Artifact checksum mismatch: {name}')
        entries[name] = digest
    if filename not in entries:
        raise RuntimeError(f'Missing artifact checksum: {filename}')
    if kind == 'pe' and (folder / filename).read_bytes()[:2] != b'MZ':
        raise RuntimeError(f'Expected Windows PE artifact: {filename}')
    if kind == 'pdb' and not (folder / filename).read_bytes().startswith(b'Microsoft C/C++ MSF 7.00'):
        raise RuntimeError('Expected a verified Windows PDB')
    return folder / filename


def verify_bundle(folder, filename, commit, flavor):
    binary = verify_artifact(folder, filename, commit)
    metadata = json.loads(verify_artifact(folder, 'BUILD-PROFILE.json', commit, 'data').read_text(encoding='utf-8-sig'))
    if (metadata.get('schema') != 1 or metadata.get('flavor') != flavor or
            metadata.get('commit') != commit or metadata.get('optimized') is not True):
        raise RuntimeError('Build profile mismatch')
    if flavor == 'Diagnostics':
        pdb = verify_artifact(folder, Path(filename).stem + '.pdb', commit, 'pdb')
        symbols = json.loads(verify_artifact(folder, 'SYMBOLS.json', commit, 'data').read_text(encoding='utf-8-sig'))
        spec = importlib.util.spec_from_file_location('symbol_check', Path(__file__).parent / 'diagnostics/verify-symbols.py')
        checker = importlib.util.module_from_spec(spec); spec.loader.exec_module(checker)
        guid, age = checker.codeview(binary.read_bytes())
        if (metadata.get('full_symbols') is not True or symbols.get('binary') != filename or
                symbols.get('pdb') != pdb.name or symbols.get('binary_sha256') != sha(binary) or
                symbols.get('pdb_sha256') != sha(pdb) or symbols.get('guid') != guid or symbols.get('age') != age):
            raise RuntimeError('Symbols do not match the diagnostics binary')
    return binary


VARIANTS = (('Release', 'ci', ''), ('Diagnostics', 'debug-ci', '-diagnostics'))
# FEAT-GTS-260: the MinGW runtime the libraries and the converter were linked against.
RUNTIME_DLLS = ('libgcc_s_seh-1.dll', 'libstdc++-6.dll', 'libwinpthread-1.dll')
# FIX-GTS-262: runtime libraries the renderer repository implements (ci/build-windows.ps1 -Target Renderer).
# They are packaged last, over this source's own (much smaller) versions.
RENDERER_LIBRARIES = ('libSceAjm.prx', 'libSceNgs2.prx', 'libSceAudio3d.prx', 'libSceZlib.prx',
                      'libSceDiscMap.prx', 'libSceScreenShot.prx', 'libSceNpAuth.prx')
# FEAT-GTS-280: programs the renderer repository builds for the launcher, packaged in tools\. The
# HD rumble & adaptive triggers window's Test buttons run ryty-feel-test.
RENDERER_TOOLS = ('ryty-feel-test.exe',)
# FEAT-GTS-287: RyGTS's DLSS plugin, built with the renderer (ci/build-windows.ps1 -Target Renderer)
# and packaged alone in dlss\. NVIDIA's runtime (nvngx_dlss.dll, Streamline) is not packaged: players
# get it with the launcher's download button.
DLSS_PLUGIN = 'ryGT_dlss.dll'
# The licence of NVIDIA's NGX loader inside the plugin (the DLSS SDK's LICENSE.txt), at the package's root.
DLSS_LICENSE = 'NVIDIA-RTX-SDKs-LICENSE.txt'


def verify_runtime(folder, commit):
    """The runtime libraries and MinGW DLLs from ci/build-windows.ps1 -Target Runtime, checked against
    the artifact's own manifest and revision."""
    verify_artifact(folder, 'libc.prx', commit)
    names = [line.split('  ', 1)[1] for line in (folder / 'SHA256SUMS.txt').read_text(encoding='utf-8-sig').splitlines()]
    libraries = sorted(name for name in names if name.lower().endswith('.prx'))
    if 'libSceVideoOut.prx' in libraries or not {'libc.prx', 'libkernel.prx'} <= set(libraries):
        raise RuntimeError('Unexpected runtime library set')
    for dll in RUNTIME_DLLS + ('RUNTIME-TOOLCHAIN.json', 'ryty.exe', 'ryty-wheel-probe.exe'):
        if dll not in names:
            raise RuntimeError(f'Runtime artifact is missing {dll}')
    return [folder / name for name in libraries], [folder / name for name in RUNTIME_DLLS]


def pe_tables(data):
    """(exported names, {imported DLL (lower case): [imported names]}) of a PE32 or PE32+ image."""
    def u16(offset): return int.from_bytes(data[offset:offset + 2], 'little')
    def u32(offset): return int.from_bytes(data[offset:offset + 4], 'little')
    def u64(offset): return int.from_bytes(data[offset:offset + 8], 'little')
    header = u32(0x3c)
    if data[:2] != b'MZ' or data[header:header + 4] != b'PE\0\0':
        raise RuntimeError('Not a PE image')
    sections, optional_size = u16(header + 6), u16(header + 20)
    optional = header + 24
    magic = u16(optional)
    if magic not in (0x10b, 0x20b):  # PE32 (the .NET launcher) or PE32+
        raise RuntimeError('Not a PE image')
    wide = magic == 0x20b
    directories = optional + (112 if wide else 96)
    table = optional + optional_size
    spans = [(u32(table + 40 * i + 12), max(u32(table + 40 * i + 8), u32(table + 40 * i + 16)), u32(table + 40 * i + 20))
             for i in range(sections)]

    def offset(rva):
        for start, size, raw in spans:
            if start <= rva < start + size:
                return rva - start + raw
        raise RuntimeError('RVA outside the image')

    def text(rva):
        at = offset(rva)
        return data[at:data.index(b'\0', at)].decode('ascii', 'replace')

    exports = set()
    export_rva = u32(directories)
    if export_rva:
        at = offset(export_rva)
        names, name_table = u32(at + 24), u32(at + 32)
        exports = {text(u32(offset(name_table) + 4 * i)) for i in range(names)}
    imports = {}
    import_rva = u32(directories + 8)
    if import_rva:
        at = offset(import_rva)
        while u32(at + 12):
            thunks = u32(at) or u32(at + 16)
            entry, names = offset(thunks), []
            size, read = (8, u64) if wide else (4, u32)
            while read(entry):
                value = read(entry)
                if not value >> (size * 8 - 1):  # high bit: imported by ordinal
                    names.append(text((value & 0x7fffffff) + 2))
                entry += size
            imports.setdefault(text(u32(at + 12)).lower(), []).extend(names)
            at += 20
    return exports, imports


def check_package_imports(stage):
    """Every function a packaged module imports from another packaged module must be exported by it:
    an older library next to a newer libc, say, stops the game at start (error 127)."""
    modules = {}
    for path in list(stage.glob('*.dll')) + list(stage.glob('*.exe')) + list((stage / 'libs').glob('*.prx')):
        modules[path.name.lower()] = path
    tables = {name: pe_tables(path.read_bytes()) for name, path in modules.items()}
    missing = []
    for name, (_, imports) in sorted(tables.items()):
        for dll, functions in imports.items():
            if dll not in tables:
                continue  # Windows, the game's own modules (app0) and the like
            exported = tables[dll][0]
            missing += [f'{modules[name].name} -> {dll}: {function}' for function in functions if function not in exported]
    if missing:
        raise RuntimeError('Unresolved imports between packaged modules:\n  ' + '\n  '.join(missing[:20]))


def assemble(commit, flavor, channel, suffix, renderer_policy=None):
    launcher_folder = Path('artifacts/launcher' + suffix)
    renderer_folder = Path('artifacts/renderer' + suffix)
    launcher = verify_bundle(launcher_folder, 'RyGTLauncher.exe', commit, flavor)
    renderer = verify_bundle(renderer_folder, 'libSceVideoOut.prx', commit, flavor)
    root = Path(__file__).resolve().parent.parent
    pin = json.loads((renderer_policy or root / 'renderer/source.json').read_text())
    source_file = verify_artifact(renderer_folder, 'CI-SOURCE.txt', commit, 'data')
    source = source_file.read_text(encoding='utf-8-sig')
    lines = source.splitlines()
    expected = os.environ.get('RENDERER_SHA')
    if len(lines) != 2 or lines[0] != pin['repository'] or not re.fullmatch('[0-9a-f]{40}', lines[1]) or (expected and lines[1] != expected) or (pin['commit'] != 'HEAD' and pin['commit'] != lines[1]):
        raise RuntimeError('Renderer source revision mismatch')
    expected = lines[1]
    if b'1440140585595047977' not in renderer.read_bytes() or b'DISCORD-RPC: compiled in;' not in renderer.read_bytes():
        raise RuntimeError('Discord RPC missing or application ID incorrect')
    name = f'RyGT-{channel}-{commit[:8]}'  # FIX-RT-015: RyGT, one package for both games
    destination = Path('dist'); stage = destination / name
    stage.mkdir(parents=True, exist_ok=False)

    def place(source, relative):
        target = stage / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)

    # FEAT-GTS-263: every file is built by this pipeline, downloaded pinned by the Tools target, or
    # taken from this repository; there is no runtime archive any more.
    tree_files = repository_tree_files(root)
    for repository_file, relative in REPOSITORY_FILES + tuple(tree_files):
        place(root / repository_file, relative)
    place(launcher, 'RyGTLauncher.exe')
    place(renderer, 'libs/libSceVideoOut.prx')
    runtime_folder = Path('artifacts/runtime')
    libraries, dlls = verify_runtime(runtime_folder, commit)
    for library in libraries:
        place(library, 'libs/' + library.name)
    for dll in dlls:
        place(dll, dll.name)
        place(dll, 'converter/' + dll.name)  # the converter is linked against the same runtime
    place(runtime_folder / 'ryty.exe', 'converter/ryty.exe')
    place(runtime_folder / 'ryty-wheel-probe.exe', 'tools/ryty-wheel-probe.exe')
    for library_name in RENDERER_LIBRARIES:
        place(verify_artifact(renderer_folder, library_name, commit), 'libs/' + library_name)
    for tool_name in RENDERER_TOOLS:
        place(verify_artifact(renderer_folder, tool_name, commit), 'tools/' + tool_name)
    place(verify_artifact(renderer_folder, DLSS_PLUGIN, commit), 'dlss/' + DLSS_PLUGIN)
    place(verify_artifact(renderer_folder, DLSS_LICENSE, commit, 'data'), DLSS_LICENSE)
    renderer_profile = json.loads(verify_artifact(renderer_folder, 'BUILD-PROFILE.json', commit, 'data').read_text(encoding='utf-8-sig'))
    xr_commit = renderer_profile.get('openxr_sdk')
    if xr_commit is not None:
        xr = json.loads((root / 'ci/runtime-toolchain.json').read_text())['openxr_sdk']
        if xr_commit != xr['commit'] or b'SHARED_OPENXR\x00' not in renderer.read_bytes():
            raise RuntimeError('OpenXR renderer or SDK mismatch')
        place(verify_artifact(renderer_folder, 'OpenXR-LICENSE.txt', commit, 'data'), 'OpenXR-LICENSE.txt')
        place(verify_artifact(renderer_folder, 'rygt_openxr_info.exe', commit), 'tools/rygt_openxr_info.exe')
    tools_folder = Path('artifacts/tools')
    for file in TOOLS_FILES:
        kind = 'pe' if file.endswith(('.exe', '.dll')) else 'data'
        tool = verify_artifact(tools_folder, file, commit, kind)
        if file.startswith('amd_fidelityfx_vk'):
            place(tool, file)
        else:
            place(tool, 'tools/adcpatch/' + ('README.txt' if file == 'adcpatch-README.txt' else file))
    for file, relative in GT7_TOOLS_FILES:
        place(verify_artifact(tools_folder, file, commit), relative)
    for file, relative in GT7_RUNTIME_FILES:
        place(verify_artifact(runtime_folder, file, commit, 'pe' if file.endswith('.dll') else 'data'), relative)
    check_package_imports(stage)
    for name_required in ('converter/ryty.exe', 'tools/adcpatch/adcpatch.exe', 'tools/ryty-wheel-probe.exe',
                          'tools/ryty-feel-test.exe', 'dlss/' + DLSS_PLUGIN, DLSS_LICENSE,
                          'libs/libc.prx', 'libs/libkernel.prx', 'libs/libSceBluetoothHid.prx',
                          'amd_fidelityfx_vk.dll', 'amd_fidelityfx_vk-LICENSE.txt', 'launcher/background.png',
                          'user/custom_configs/CUSA03220.json', 'patches/CUSA24769.xml',
                          'server/shadnet/shadnet.exe') + tuple(relative for _, relative in GT7_TOOLS_FILES + GT7_RUNTIME_FILES):
        if not (stage / name_required).is_file():
            raise RuntimeError(f'Incomplete runtime package: {name_required}')
    if sorted(file.name for file in (stage / 'dlss').iterdir()) != [DLSS_PLUGIN]:
        raise RuntimeError('dlss\\ holds only ' + DLSS_PLUGIN + ' (NVIDIA\'s runtime is downloaded by the launcher)')
    write_packaged_defaults(stage)
    provenance = dict(commit=commit, flavor=flavor, channel=channel,
        configuration='RelWithDebInfo' if suffix else 'Release', optimized=True,
        pipeline=os.environ.get('CI_PIPELINE_URL'),
        renderer_source=source, launcher_version=launcher_version(root), launcher_sha256=sha(launcher),
        renderer_sha256=sha(renderer),
        runtime_toolchain=json.loads((runtime_folder / 'RUNTIME-TOOLCHAIN.json').read_text(encoding='utf-8-sig')),
        sources=dict(
            built=['RyGTLauncher.exe', 'libs/libSceVideoOut.prx', 'converter/ryty.exe', 'tools/ryty-wheel-probe.exe',
                   'tools/adcpatch/adcpatch.exe'] + sorted('libs/' + library.name for library in libraries) +
                  ['libs/' + name for name in RENDERER_LIBRARIES if name not in {library.name for library in libraries}] +
                  ['tools/' + name for name in RENDERER_TOOLS] + ['dlss/' + DLSS_PLUGIN] +
                  [relative for _, relative in GT7_TOOLS_FILES],
            toolchain=list(RUNTIME_DLLS) + ['converter/' + dll for dll in RUNTIME_DLLS] +
                      [relative for _, relative in GT7_RUNTIME_FILES],
            downloaded=['amd_fidelityfx_vk.dll', 'amd_fidelityfx_vk-LICENSE.txt', DLSS_LICENSE],
            repository=[relative for _, relative in REPOSITORY_FILES + tuple(tree_files)]))
    if xr_commit is not None:
        provenance['openxr_sdk'] = xr
        provenance['sources']['built'].append('tools/rygt_openxr_info.exe')
        provenance['sources']['downloaded'].append('OpenXR-LICENSE.txt')
    if suffix:
        # PDBs are not packaged. Only these explicitly verified same-build
        # symbols are admitted to debug-ci, adjacent to their matching modules.
        for folder, pdb_name, target in ((launcher_folder, 'RyGTLauncher.pdb', 'RyGTLauncher.pdb'),
                                        (renderer_folder, 'libSceVideoOut.pdb', 'libs/libSceVideoOut.pdb')):
            shutil.copy2(verify_artifact(folder, pdb_name, commit, 'pdb'), stage / target)
        tools = stage / 'tools/diagnostics'; tools.mkdir(parents=True)
        for file in ('analyze-trace.py', 'frame-capture-hotkey.py', 'watch-capture.py', 'Start-Capture.cmd', 'README.txt'):
            shutil.copy2(root / 'ci/diagnostics' / file, tools / file)
        (stage / 'investigation-build').mkdir()
        layout = verify_artifact(renderer_folder, 'frame-capture-layouts.json', commit, 'data')
        if sha(renderer).upper() not in json.loads(layout.read_text()):
            raise RuntimeError('Capture layout does not match the diagnostics binary')
        shutil.copy2(layout, stage / 'investigation-build/frame-capture-layouts.json')
        provenance['symbols'] = [json.loads((folder / 'SYMBOLS.json').read_text(encoding='utf-8-sig')) for folder in (launcher_folder, renderer_folder)]
        shutil.copy2(root / 'ci/diagnostics/DEBUG-README.txt', stage / 'README.txt')
    else:
        (stage / 'README.txt').write_text(
            'RyGT Release CI package (ci)\n\nRun RyGTLauncher.exe to set up your own decrypted GT Sport CUSA03220 v1.69 and/or\n'
            'Gran Turismo 7 CUSA24769 dumps; both games run from this one folder.\n'
            'Debug/Diagnostics is distributed separately as debug-ci.\n'
            'Game executable/data, firmware, saves, logs, caches and NVIDIA SDK DLLs are excluded.\n'
            'Supply required firmware modules and optional NVIDIA upscaler files yourself.\n'
            'Everything here is built from this pipeline revision, downloaded pinned by checksum\n'
            '(AMD FidelityFX runtime) or taken from the source repository: see BUILD_INFO.txt.\n'
            'This is a CI artifact, not a tagged release. See BUILD_INFO.txt and SHA256SUMS.txt.\n', encoding='utf-8')
    (stage / 'BUILD_INFO.txt').write_text(json.dumps(provenance, indent=2) + '\n', encoding='utf-8')
    allowed_pdb = {'RyGTLauncher.pdb', 'libs/libSceVideoOut.pdb'} if suffix else set()
    for file in stage.rglob('*'):
        if file.is_file() and file.suffix.lower() == '.pdb' and file.relative_to(stage).as_posix() not in allowed_pdb:
            raise RuntimeError('Unexpected packaged symbols')
        if file.is_file() and file.suffix.lower() in FORBIDDEN - {'.pdb'}:
            raise RuntimeError(f'Forbidden package content: {file.relative_to(stage)}')
    manifest = {file.relative_to(stage).as_posix(): sha(file) for file in sorted(stage.rglob('*')) if file.is_file()}
    (stage / 'SHA256SUMS.txt').write_text(''.join(f'{digest}  {name}\n' for name, digest in manifest.items()), encoding='utf-8')
    result = destination / (name + '.zip')
    with zipfile.ZipFile(result, 'x', zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for file in sorted(stage.rglob('*')):
            if file.is_file(): archive.write(file, file.relative_to(destination).as_posix())
    with zipfile.ZipFile(result) as archive:
        if archive.testzip() is not None: raise RuntimeError('ZIP integrity failure')
        for file, digest in manifest.items():
            if hashlib.sha256(archive.read(name + '/' + file)).hexdigest() != digest: raise RuntimeError('Packaged checksum mismatch')
    digest = sha(result)
    (destination / (result.name + '.sha256')).write_text(digest + '  ' + result.name + '\n')
    return dict(channel=channel, flavor=flavor, archive=result.name, sha256=digest, files=len(manifest)+1, renderer_commit=expected)


def package(channels=None, renderer_policy=None):
    commit = os.environ['CI_COMMIT_SHA']
    if not re.fullmatch('[0-9a-f]{40}', commit): raise RuntimeError('Invalid source revision')
    if channels is None:
        selected = os.environ.get('CI_CHANNEL')
        channels = (selected,) if selected else tuple(channel for _, channel, _ in VARIANTS)
    else:
        channels = (channels,) if isinstance(channels, str) else tuple(channels)
    valid_channels = tuple(channel for _, channel, _ in VARIANTS)
    if not channels or len(set(channels)) != len(channels) or any(channel not in valid_channels for channel in channels):
        raise ValueError('Invalid CI package channel selection')
    requested = set(channels)
    channels = tuple(channel for channel in valid_channels if channel in requested)
    variants = [assemble(commit, *variant, renderer_policy=renderer_policy) for variant in VARIANTS if variant[1] in channels]
    if len({variant['renderer_commit'] for variant in variants}) != 1:
        raise RuntimeError('Renderer revisions differ between package variants')
    (Path('dist') / 'CI-PACKAGES.json').write_text(json.dumps(dict(schema=2, commit=commit, renderer_commit=variants[0]['renderer_commit'], variants=variants), indent=2) + '\n')
    print(json.dumps(variants, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--renderer-policy', type=Path)
    package(renderer_policy=parser.parse_args().renderer_policy)
