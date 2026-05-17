from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

from excel_reader import load_workbook_from_path, read_sheet_as_grid
from exporters import FORMAT_EXPORTERS, export

INVALID_FILENAME_CHARS = re.compile(r'[\\/:*?"<>|]')


def safe_name(name: str) -> str:
    return INVALID_FILENAME_CHARS.sub("_", name).strip() or "sheet"


def build_output_path(
    output_dir: Path,
    stem: str,
    sheet_name: str,
    ext: str,
    used_paths: set[Path],
) -> Path:
    base = f"{safe_name(stem)}_{safe_name(sheet_name)}"
    candidate = output_dir / f"{base}.{ext}"
    if candidate not in used_paths:
        used_paths.add(candidate)
        return candidate

    n = 1
    while True:
        candidate = output_dir / f"{base}_{n}.{ext}"
        if candidate not in used_paths:
            used_paths.add(candidate)
            return candidate
        n += 1


def resolve_file_output_dir(input_dir: Path, output_dir: Path, xlsx: Path) -> Path:
    rel_parent = xlsx.parent.relative_to(input_dir)
    file_output_dir = output_dir / rel_parent
    file_output_dir.mkdir(parents=True, exist_ok=True)
    return file_output_dir


def convert_folder(input_dir: Path, output_dir: Path, fmt: int) -> int:
    output_dir.mkdir(parents=True, exist_ok=True)
    ext, _ = FORMAT_EXPORTERS[fmt]

    xlsx_files = sorted(input_dir.rglob("*.xlsx"))
    if not xlsx_files:
        print(f"未在 {input_dir}（含子目录）中找到 .xlsx 文件")
        return 0

    files_processed = 0
    sheets_written = 0
    errors: list[str] = []
    used_paths: set[Path] = set()

    for xlsx in xlsx_files:
        rel_display = xlsx.relative_to(input_dir).as_posix()
        file_output_dir = resolve_file_output_dir(input_dir, output_dir, xlsx)
        try:
            wb = load_workbook_from_path(xlsx)
            files_processed += 1
            for sheet_name in wb.sheetnames:
                grid = read_sheet_as_grid(wb[sheet_name])
                if not grid:
                    continue
                out_path = build_output_path(
                    file_output_dir, xlsx.stem, sheet_name, ext, used_paths
                )
                export(grid, out_path, fmt)
                sheets_written += 1
                print(f"已生成: {out_path}")
            wb.close()
        except Exception as e:
            errors.append(f"{rel_display}: {e}")
            print(f"失败: {rel_display} - {e}", file=sys.stderr)

    print(
        f"\n完成: 处理 {files_processed} 个 Excel 文件, "
        f"生成 {sheets_written} 个输出文件"
    )
    if errors:
        print(f"失败 {len(errors)} 个:", file=sys.stderr)
        for err in errors:
            print(f"  - {err}", file=sys.stderr)

    return 1 if errors else 0


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="递归将文件夹内 .xlsx 转为 MD / CSV / HTML（每工作表一个文件，保持目录结构）"
    )
    parser.add_argument(
        "-i", "--input",
        dest="input_dir",
        required=True,
        help="输入文件夹路径（递归扫描子目录）",
    )
    parser.add_argument(
        "-o", "--output",
        dest="output_dir",
        required=True,
        help="输出文件夹路径（按输入相对路径递归创建子目录）",
    )
    parser.add_argument(
        "-f", "--format",
        dest="format",
        type=int,
        default=1,
        choices=[1, 2, 3],
        help="输出格式: 1=MD(默认), 2=CSV, 3=HTML",
    )
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    input_dir = Path(args.input_dir)
    output_dir = Path(args.output_dir)

    if not input_dir.is_dir():
        print(f"错误: 输入目录不存在: {input_dir}", file=sys.stderr)
        return 1

    return convert_folder(input_dir, output_dir, args.format)


if __name__ == "__main__":
    sys.exit(main())
