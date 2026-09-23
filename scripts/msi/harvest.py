#!/usr/bin/env python3
"""Harvest a dotnet publish directory into a WiX v4 fragment.

Emits one file with two fragments:
  - the directory tree under INSTALLFOLDER (subdirectories such as runtimes/), and
  - a ComponentGroup named AppFiles with one component per file, referenced by the
    main package (packaging/msi/OpcBridge.wxs) via <ComponentGroupRef>.

File sources are written as absolute /src/... paths because the MSI is built inside
the SDK container with the repository mounted at /src (see scripts/msi/build-msi.sh).

Usage: harvest.py <publish-dir> <output-wxs> [root-directory-id] [component-group-name] [extra-skip-names]

  root-directory-id     WiX directory the files install into (default INSTALLFOLDER)
  component-group-name  ComponentGroup id the main package references (default AppFiles)
  extra-skip-names      comma-separated file names to exclude (e.g. the app exe,
                        which the main .wxs declares itself to attach shortcuts)
"""
import hashlib
import sys
from pathlib import Path
from xml.sax.saxutils import escape

from publish_skip import is_runtime_state

SKIP_SUFFIXES = (".pdb", ".log", ".lock")


def identifier(kind: str, relative: str) -> str:
    """Stable cross-run element id derived from the relative path."""
    digest = hashlib.sha1(relative.encode("utf-8")).hexdigest()[:10].upper()
    return f"{kind}_{digest}"


def should_skip(relative: Path, extra_skip_files: set[str]) -> bool:
    if is_runtime_state(relative):
        return True
    if relative.name in extra_skip_files:
        return True
    return relative.name.endswith(SKIP_SUFFIXES)


def collect(publish_dir: Path, extra_skip_files: set[str]) -> list[Path]:
    files = sorted(
        path for path in publish_dir.rglob("*")
        if path.is_file() and not should_skip(path.relative_to(publish_dir), extra_skip_files)
    )
    if not files:
        raise SystemExit(f"no harvestable files under {publish_dir}")
    return files


def container_source(publish_arg: str, relative: Path) -> str:
    """Source path as the SDK container sees it: the repository is mounted at /src."""
    return "/src/" + "/".join(part for part in (publish_arg.strip("/"), str(relative)) if part)


def build_tree(files: list[Path], publish_dir: Path) -> dict:
    """Nested {name: Node} tree of the directories that contain harvested files."""

    class Node:
        def __init__(self, name: str, relative: Path):
            self.name = name
            self.relative = relative
            self.children: dict = {}

        @property
        def element_id(self) -> str:
            return identifier("dir", str(self.relative))

    root: dict = {}
    directories = {path.relative_to(publish_dir).parent for path in files}
    for directory in sorted(directories, key=lambda item: str(item)):
        children = root
        cumulative = Path(".")
        for part in directory.parts:
            cumulative = cumulative / part if str(cumulative) != "." else Path(part)
            children = children.setdefault(part, Node(part, cumulative)).children
    return root


def render_tree(nodes: dict, depth: int) -> list[str]:
    pad = "        " + "    " * depth
    lines: list[str] = []
    for name in sorted(nodes):
        node = nodes[name]
        lines.append(f'{pad}<Directory Id="{node.element_id}" Name="{escape(node.name)}">')
        lines.extend(render_tree(node.children, depth + 1))
        lines.append(pad + "</Directory>")
    return lines


def render_components(files: list[Path], publish_dir: Path, publish_arg: str, directory_ids: dict, root_directory_id: str) -> list[str]:
    lines: list[str] = []
    for file_path in files:
        relative = file_path.relative_to(publish_dir)
        component_id = identifier("cmp", str(relative))
        file_id = identifier("fil", str(relative))
        parent_id = root_directory_id if str(relative.parent) == "." else directory_ids[str(relative.parent)]
        source = escape(container_source(publish_arg, relative))
        lines.append(f'      <Component Id="{component_id}" Directory="{parent_id}">')
        lines.append(f'        <File Id="{file_id}" Source="{source}" KeyPath="yes" />')
        lines.append("      </Component>")
    return lines


def main() -> None:
    publish_arg = sys.argv[1]
    publish_dir = Path(publish_arg).resolve()
    output_path = Path(sys.argv[2])
    root_directory_id = sys.argv[3] if len(sys.argv) > 3 else "INSTALLFOLDER"
    component_group = sys.argv[4] if len(sys.argv) > 4 else "AppFiles"
    extra_skip_names = {
        name.strip()
        for name in (sys.argv[5].split(",") if len(sys.argv) > 5 else [])
        if name.strip()
    }

    files = collect(publish_dir, extra_skip_files=extra_skip_names)
    tree = build_tree(files, publish_dir)

    # The tree nodes know their ids; expose them by relative path for the components.
    directory_ids: dict[str, str] = {}

    def index(nodes: dict) -> None:
        for node in nodes.values():
            directory_ids[str(node.relative)] = node.element_id
            index(node.children)

    index(tree)

    tree_lines = render_tree(tree, 0) if tree else []
    output = [
        '<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">',
        "  <!-- Generated by scripts/msi/harvest.py — do not edit. -->",
        "  <Fragment>",
    ]
    if tree_lines:
        output += [
            f'    <DirectoryRef Id="{root_directory_id}">',
            *tree_lines,
            "    </DirectoryRef>",
        ]
    output += [
        "  </Fragment>",
        "  <Fragment>",
        f'    <ComponentGroup Id="{component_group}">',
        *render_components(files, publish_dir, publish_arg, directory_ids, root_directory_id),
        "    </ComponentGroup>",
        "  </Fragment>",
        "</Wix>",
        "",
    ]
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text("\n".join(output), encoding="utf-8")
    print(f"harvested {len(files)} files -> {output_path}")


if __name__ == "__main__":
    main()
